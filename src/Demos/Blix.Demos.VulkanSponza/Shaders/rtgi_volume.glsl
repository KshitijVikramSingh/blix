// --rtgi-volume (SponzaLoop.RtgiVolume.cs): a sparse volume of probes whose cooked geometry sizes the cache's cells
// (volCellScale) and whose places the cells' traced visibility reaches for (rtgi_volume_vis.comp). The probes once learnt
// light as well, for young cells to lean on; the readout leaked and blotched with it, and nothing learns or reads it now
// (the layout keeps its words).
//
// What sees what is geometry, cooked once at load (rtgi_volume_cook.comp), never learnt from the light or tied to the
// camera: each probe pushed out of what it sat inside (or dropped), then its depth map -- 8 x 8 octahedral texels of the
// mean distance and mean squared distance to what it sees, ~8 rays a texel, and its harmonic mean distance (below).
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
// size the cells). A probe, VOL_FLOATS floats: its radiance as L1 spherical harmonics (r, g, b each Y00, Y1-1 (y), Y10
// (z), Y11 (x)) and its sky visibility (both unused), its offset (xyz, m), the rays held, its flags, its back-face share, its harmonic mean
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
// Flags: trained (no longer set: the light's); inside something (left out); cooked (moved and its depth map traced).
#define VOL_TRAINED 1u
#define VOL_DEAD 2u
#define VOL_COOKED 4u
#define VOL_PROBE_STEPS 8u
#define VOL_SIDE 8u
#define VOL_TEXELS 64u
// The depth map's reach, in spacings: past it a probe's view is open as far as any point that reads it lies.
#define VOL_REACH 2.0
const vec3 VOL_JITTER = vec3(0.037, 0.051, 0.029);

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

#endif
