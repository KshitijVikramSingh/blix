// --rtgi-volume (SponzaLoop.RtgiVolume.cs): the world's light as a volume of probes, trained at a fixed share of the
// cache's rays whatever the camera does (rtgi_volume_rays.comp, rtgi_volume_update.comp), so a surface reached for the
// first time already has roughly its light: what young cells lean on (rtgi_readout.frag) -- never trained into the cache
// (rtgi_train.comp). The screen-driven cache alone could not keep up with a moving camera:
// Bistro's wall 20% blotchy and a quarter dark for 5 s after a walk (the user, 2026-10-29).
//
// What sees what is geometry, cooked once at load (rtgi_volume_cook.comp), never learnt from the light or tied to the
// camera: each probe pushed out of what it sat inside (or dropped), then its depth map -- 8 x 8 octahedral texels of the
// mean distance and mean squared distance to what it sees, ~8 rays a texel. A point reads a probe only in front of its
// surface and where the depth map says the probe sees it (Chebyshev). Learnt from 32 light rays a pass into 8 octants,
// the test let Crytek Sponza's arches read probes across their walls: light leaks (the user, 2026-10-29).
//
// Sparse, in bricks of 4 x 4 x 4 probes, at levels: level l's probes VOL_PARAMS.x x 2^l apart (0.5, 1, 2, 4, 8 m), a probe
// of level l at global coordinate c standing at (c + 0.5 + VOL_JITTER) x its spacing plus its cooked offset. Bricks exist
// where geometry is: the 2 m and 8 m levels everywhere it is (the CPU places them from the triangles, and will by streamed
// chunk), 1 m and 0.5 m only where the cook found the geometry tighter than the spacing (SponzaLoop.RtgiVolume.cs's
// refinement) -- the density the geometry asks for, not the camera. Found through a hash table of (brick, level): linear
// probing over VOL_PROBE_STEPS slots, each (key, brick), key 0 empty. A probe's id is brick x 64 + its place in it.
//
// The same geometry sizes the cache's cells (rtgi_cache.glsl's rtgiCellSize): each probe's harmonic mean distance to what
// it sees (irradiance caching's measure of how far its light holds), interpolated over the VOL_GEOM.w level's probes,
// times VOL_GEOM.x. Sized by the camera's distance, a surface changed cells as the camera moved -- the light crawled and
// settled again, and read differently from each place (the user, Crytek Sponza, 2026-10-29).
//
// The includer declares VOL_TABLE(i) (uvec2), VOL_PROBES(i) (float), VOL_DEPTH(i) (uint), VOL_PARAMS (vec4: x level 0's
// spacing (m); y the levels; z the table's slots - 1; w 1 on) and VOL_GEOM (vec4: x the cache cell's size per metre of
// harmonic mean distance, 0 off; y the largest cell (m); z the camera's floor per metre of distance; w the level whose probes
// size the cells). A probe, VOL_FLOATS floats: its radiance as L1 spherical harmonics (r, g, b each Y00, Y1-1 (y), Y10 (z),
// Y11 (x)), its sky visibility, its offset (xyz, m), the rays held, its flags, its back-face share, its harmonic mean
// distance (m). Its depth map VOL_TEXELS uints (packHalf2x16: mean, mean squared, in spacings).
#ifndef RTGI_VOLUME_GLSL
#define RTGI_VOLUME_GLSL

#define VOL_FLOATS 24u
#define VOL_SH 0u
#define VOL_SKY 12u
#define VOL_OFFSET 13u
#define VOL_HELD 16u
#define VOL_FLAGS 17u
#define VOL_BACK 18u
#define VOL_SCALE 19u
// Flags: trained; inside something (left out); cooked (moved and its depth map traced).
#define VOL_TRAINED 1u
#define VOL_DEAD 2u
#define VOL_COOKED 4u
#define VOL_PROBE_STEPS 8u
#define VOL_SIDE 8u
#define VOL_TEXELS 64u
// The depth map's reach, in spacings: past it a probe's view is open as far as any point that reads it lies.
#define VOL_REACH 2.0
const vec3 VOL_JITTER = vec3(0.037, 0.051, 0.029);

const float VOL_Y0 = 0.282095;
const float VOL_Y1 = 0.488603;

uint volHash(uint x) {
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

float volSpacing(uint level) { return VOL_PARAMS.x * exp2(float(level)); }

// The brick (b, level)'s two hashes: x the slot it starts probing at, y its key (never 0). SponzaLoop.RtgiVolume.cs's
// BrickHash is the same.
uvec2 volBrickHash(ivec3 b, uint level) {
    uint a = volHash(uint(b.x) * 0x9E3779B1u ^ volHash(uint(b.y) * 0x85EBCA77u ^ volHash(uint(b.z) * 0xC2B2AE3Du ^ (level + 0x51u))));
    uint k = volHash(a ^ 0x68E31DA4u ^ uint(b.x) * 0x1B873593u ^ uint(b.y) * 0xCC9E2D51u ^ uint(b.z) * 0x27D4EB2Fu) | 1u;
    return uvec2(a & uint(VOL_PARAMS.z), k);
}

// The brick holding probe coordinate c at a level, or 0xFFFFFFFF.
uint volBrick(ivec3 c, uint level) {
    uvec2 h = volBrickHash(c >> 2, level);
    for (uint k = 0u; k < VOL_PROBE_STEPS; k++) {
        uvec2 e = VOL_TABLE((h.x + k) & uint(VOL_PARAMS.z));
        if (e.x == h.y) return e.y;
        if (e.x == 0u) break;
    }
    return 0xFFFFFFFFu;
}

uint volProbeId(uint brick, ivec3 c) { ivec3 l = c & 3; return brick * 64u + uint(l.x + 4 * l.y + 16 * l.z); }

// Where the grid puts a probe (its cooked offset not added).
vec3 volGridPosition(ivec3 c, uint level) { return (vec3(c) + 0.5 + VOL_JITTER) * volSpacing(level); }

vec3 volOffset(uint i) { uint b = i * VOL_FLOATS + VOL_OFFSET; return vec3(VOL_PROBES(b), VOL_PROBES(b + 1u), VOL_PROBES(b + 2u)); }

// Octahedral map of the sphere (z up), [0, 1]^2.
vec2 volOctEncode(vec3 v) {
    v /= abs(v.x) + abs(v.y) + abs(v.z);
    vec2 e = v.xy;
    if (v.z < 0.0) e = (1.0 - abs(e.yx)) * vec2(e.x >= 0.0 ? 1.0 : -1.0, e.y >= 0.0 ? 1.0 : -1.0);
    return e * 0.5 + 0.5;
}
vec3 volOctDecode(vec2 f) {
    f = f * 2.0 - 1.0;
    vec3 v = vec3(f, 1.0 - abs(f.x) - abs(f.y));
    float t = max(-v.z, 0.0);
    v.x += v.x >= 0.0 ? -t : t;
    v.y += v.y >= 0.0 ? -t : t;
    return normalize(v);
}
uint volTexel(vec3 d) {
    uvec2 t = min(uvec2(volOctEncode(d) * float(VOL_SIDE)), uvec2(VOL_SIDE - 1u));
    return t.y * VOL_SIDE + t.x;
}

// A probe's irradiance at normal n from its L1 radiance (the cosine lobe's convolution: pi, 2 pi / 3).
vec3 volIrradiance(uint i, vec3 n) {
    uint b = i * VOL_FLOATS + VOL_SH;
    vec3 c0 = vec3(VOL_PROBES(b), VOL_PROBES(b + 4u), VOL_PROBES(b + 8u));
    vec3 cy = vec3(VOL_PROBES(b + 1u), VOL_PROBES(b + 5u), VOL_PROBES(b + 9u));
    vec3 cz = vec3(VOL_PROBES(b + 2u), VOL_PROBES(b + 6u), VOL_PROBES(b + 10u));
    vec3 cx = vec3(VOL_PROBES(b + 3u), VOL_PROBES(b + 7u), VOL_PROBES(b + 11u));
    vec3 e = 3.14159265 * VOL_Y0 * c0 + (2.0943951 * VOL_Y1) * (cy * n.y + cz * n.z + cx * n.x);
    return max(e, vec3(0.0));
}

// Whether probe i (at q) sees the point pb: its depth map's texel toward pb, Chebyshev on its distances (cubed, DDGI's
// sharpening); 1 where pb is nearer than what it saw that way.
float volSees(uint i, vec3 q, vec3 pb, float s) {
    vec3 v = pb - q;
    float d = length(v) / s;
    if (d < 1e-4) return 1.0;
    vec2 m = unpackHalf2x16(VOL_DEPTH(i * VOL_TEXELS + volTexel(v / (d * s))));
    if (d <= m.x) return 1.0;
    float var = max(m.y - m.x * m.x, 0.0025);
    float dd = d - m.x;
    float c = var / (var + dd * dd);
    return c * c * c;
}

// One level's light at p facing n: its 8 probes around p, trilinear, each only where it is cooked, trained and not inside
// something, in front of the surface (by how squarely: no floor -- a probe behind it lights nothing of it), and by whether
// it sees p. xyz the irradiance, w the weight found; sky its visibility. untrained: some probe here is live but has not
// held VOL_READ_HELD rays yet.
#define VOL_READ_HELD 128.0
bool untrained = false;
// mask: of the 8 probes around p (bit k for offset (k & 1, k >> 1 & 1, k >> 2)), those a ray from the point reached
// (rtgi_volume_vis.comp: the cell's own), 0xFF where none was traced.
vec4 volumeLevelMasked(vec3 p, vec3 n, uint level, uint mask, out float sky) {
    sky = 0.0;
    untrained = false;
    float s = volSpacing(level);
    // Lifted off the surface a little (DDGI's normal bias): the depth map's own surface is not in front of the point.
    vec3 pb = p + n * (0.1 * s);
    vec3 g = p / s - 0.5 - VOL_JITTER;
    ivec3 base = ivec3(floor(g));
    vec3 t = g - vec3(base);
    vec3 e = vec3(0.0);
    float w = 0.0, wTri = 0.0;
    uint lastBrick = 0xFFFFFFFFu;
    ivec3 lastB = ivec3(0x7FFFFFFF);
    for (int k = 0; k < 8; ++k) {
        if ((mask & (1u << uint(k))) == 0u) continue;
        ivec3 o = ivec3(k & 1, (k >> 1) & 1, k >> 2);
        ivec3 c = base + o;
        ivec3 bc = c >> 2;
        if (bc != lastB) { lastB = bc; lastBrick = volBrick(c, level); }
        if (lastBrick == 0xFFFFFFFFu) continue;
        uint i = volProbeId(lastBrick, c);
        uint flags = uint(VOL_PROBES(i * VOL_FLOATS + VOL_FLAGS));
        if ((flags & VOL_COOKED) == 0u || (flags & VOL_DEAD) != 0u) continue;
        if ((flags & VOL_TRAINED) == 0u || VOL_PROBES(i * VOL_FLOATS + VOL_HELD) < VOL_READ_HELD) { untrained = true; continue; }
        vec3 tri = mix(1.0 - t, t, vec3(o));
        float wt = tri.x * tri.y * tri.z;
        vec3 q = volGridPosition(c, level) + volOffset(i);
        vec3 toProbe = q - p;
        float facing = dot(n, toProbe);
        if (facing <= 0.0) continue;
        float wk = wt * (facing / max(length(toProbe), 1e-4) * 0.5 + 0.5) * volSees(i, q, pb, s);
        wTri += wt;
        e += volIrradiance(i, n) * wk;
        sky += VOL_PROBES(i * VOL_FLOATS + VOL_SKY) * wk;
        w += wk;
    }
    if (w > 0.0) { e /= w; sky /= w; }
    return vec4(e, w);
}

vec4 volumeLevel(vec3 p, vec3 n, uint level, out float sky) { return volumeLevelMasked(p, n, level, 0xFFu, sky); }

// The probe a point's offset k (0-7) names at a level, and whether it is cooked and live: 0xFFFFFFFF none.
uint volProbeAt(vec3 p, uint level, uint k, out vec3 q) {
    float s = volSpacing(level);
    ivec3 c = ivec3(floor(p / s - 0.5 - VOL_JITTER)) + ivec3(k & 1u, (k >> 1) & 1u, k >> 2);
    q = volGridPosition(c, level);
    uint b = volBrick(c, level);
    if (b == 0xFFFFFFFFu) return 0xFFFFFFFFu;
    uint i = volProbeId(b, c);
    uint flags = uint(VOL_PROBES(i * VOL_FLOATS + VOL_FLAGS));
    if ((flags & VOL_COOKED) == 0u || (flags & VOL_DEAD) != 0u) return 0xFFFFFFFFu;
    q += volOffset(i);
    return i;
}

// The finest level with a live probe among the 8 around p, or VOL_PARAMS.y none: the level a cell's visibility is traced
// to (rtgi_volume_vis.comp).
uint volVisLevel(vec3 p) {
    uint levels = uint(VOL_PARAMS.y);
    for (uint l = 0u; l < levels; l++) {
        for (uint k = 0u; k < 8u; k++) { vec3 q; if (volProbeAt(p, l, k, q) != 0xFFFFFFFFu) return l; }
    }
    return levels;
}

// The cache cell's size the geometry asks for at p (m): VOL_GEOM.x times the harmonic mean distance of the VOL_GEOM.w
// level's probes around p (trilinear, the cooked and live ones), at most VOL_GEOM.y; -1 where none is cooked (the camera's
// distance sizes it then).
float volCellScale(vec3 p) {
    if (VOL_GEOM.x <= 0.0 || VOL_PARAMS.w < 0.5) return -1.0;
    uint level = uint(VOL_GEOM.w);
    float s = volSpacing(level);
    vec3 g = p / s - 0.5 - VOL_JITTER;
    ivec3 base = ivec3(floor(g));
    vec3 t = g - vec3(base);
    float r = 0.0, w = 0.0;
    uint lastBrick = 0xFFFFFFFFu;
    ivec3 lastB = ivec3(0x7FFFFFFF);
    for (int k = 0; k < 8; ++k) {
        ivec3 o = ivec3(k & 1, (k >> 1) & 1, k >> 2);
        ivec3 c = base + o;
        ivec3 bc = c >> 2;
        if (bc != lastB) { lastB = bc; lastBrick = volBrick(c, level); }
        if (lastBrick == 0xFFFFFFFFu) continue;
        uint i = volProbeId(lastBrick, c);
        uint flags = uint(VOL_PROBES(i * VOL_FLOATS + VOL_FLAGS));
        if ((flags & VOL_COOKED) == 0u || (flags & VOL_DEAD) != 0u) continue;
        vec3 tri = mix(1.0 - t, t, vec3(o));
        float wt = tri.x * tri.y * tri.z;
        r += VOL_PROBES(i * VOL_FLOATS + VOL_SCALE) * wt;
        w += wt;
    }
    return w > 1e-3 ? min(VOL_GEOM.x * r / w, VOL_GEOM.y) : -1.0;
}

// The volume's light at p facing n: the finest level whose probes around p see it, else the next up -- but not past a level
// whose probes here are still learning: a coarser level's probes, 8 m apart, read through the walls the finer ones were
// placed for (the user: Crytek Sponza leaking for minutes after boot, 2026-10-29). w 1 found, 0 none: where no probe sees
// the point, the volume says nothing (the cache alone).
vec4 volumeAt(vec3 p, vec3 n, out float sky) {
    sky = 0.0;
    if (VOL_PARAMS.w < 0.5) return vec4(0.0);
    uint levels = uint(VOL_PARAMS.y);
    for (uint l = 0u; l < levels; l++) {
        float s;
        vec4 e = volumeLevel(p, n, l, s);
        if (e.w >= 0.05) { sky = s; return vec4(e.rgb, 1.0); }
        if (untrained) break;
    }
    return vec4(0.0);
}
#endif
