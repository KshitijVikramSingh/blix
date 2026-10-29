// --rtgi-near-cook (SponzaLoop.RtgiNearCook.cs): the near field cooked from the geometry. Surface cells of NEAR_PARAMS.x
// metres (by side, as the cache's), placed at load from the triangles where the volume found the geometry tight (its
// refined probes); each traces NEAR_RAYS fixed cosine rays once, to the contact radius (rtgi_nearcook_rays.comp,
// rtgi_nearcook_cook.comp): how many left (its escape share) and, for each that met a surface, how far, that surface's
// normal and albedo. Each frame its near light is those surfaces' light from the cache (rtgi_nearcook_update.comp), and
// the readout reads it (rtgi_readout.frag readNearCooked) beside the cache's far light. Nothing is traced per pixel or per
// frame: the near field is the surface's, not the screen's -- the per-pixel near rays' history cleared as the camera
// moved, and a crease took seconds to come back (the user, 2026-10-29), who asked for it worked ahead of the camera, by
// chunk, as the volume is.
//
// The includer declares NEAR_TABLE(i) (uvec2: key, cell) and NEAR_PARAMS (vec4: x the cell's size; y the table's slots -
// 1; z the cells; w 1 on). Per cell: its point and side (vec4: xyz, w the side as bits), its normal (vec4); its links,
// NEAR_RAYS uvec2 (x: bit 31 met, bits 16-30 the distance in 32768ths of the radius, bits 0-15 the normal octahedral 8:8;
// y: the albedo, unorm 4x8); its light (vec4: rgb the near light, w the escape share).
#ifndef RTGI_NEARCOOK_GLSL
#define RTGI_NEARCOOK_GLSL

#define NEAR_RAYS 16u
#define NEAR_PROBE_STEPS 8u

uint nearHash(uint x) {
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

// The cell (c, side)'s two hashes: x the slot it starts probing at, y its key (never 0). SponzaLoop.RtgiNearCook.cs's
// NearHash is the same.
uvec2 nearCellHash(ivec3 c, uint side) {
    uint a = nearHash(uint(c.x) * 0x9E3779B1u ^ nearHash(uint(c.y) * 0x85EBCA77u ^ nearHash(uint(c.z) * 0xC2B2AE3Du ^ (side + 0x3A1u))));
    uint k = nearHash(a ^ 0x2545F491u ^ uint(c.x) * 0x1B873593u ^ uint(c.y) * 0xCC9E2D51u ^ uint(c.z) * 0x27D4EB2Fu) | 1u;
    return uvec2(a & uint(NEAR_PARAMS.y), k);
}

uint nearFind(ivec3 c, uint side) {
    uvec2 h = nearCellHash(c, side);
    for (uint k = 0u; k < NEAR_PROBE_STEPS; k++) {
        uvec2 e = NEAR_TABLE((h.x + k) & uint(NEAR_PARAMS.y));
        if (e.x == h.y) return e.y;
        if (e.x == 0u) break;
    }
    return 0xFFFFFFFFu;
}

// Ray k of cell i: cosine about n, a Hammersley set turned by the cell's own angle (the cook and the update agree).
vec3 nearRayDir(uint i, uint k, vec3 n) {
    uint bits = k;
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    float r1 = (float(k) + 0.5) / float(NEAR_RAYS), r2 = float(bits) * 2.3283064365386963e-10;
    float phi = 6.28318530718 * fract(r2 + float(nearHash(i) & 0xFFFFu) / 65536.0);
    float sr = sqrt(r1);
    vec3 t = normalize(abs(n.y) < 0.99 ? cross(n, vec3(0.0, 1.0, 0.0)) : cross(n, vec3(1.0, 0.0, 0.0)));
    vec3 b = cross(n, t);
    return normalize(t * (cos(phi) * sr) + b * (sin(phi) * sr) + n * sqrt(max(0.0, 1.0 - r1)));
}

vec2 nearOctEncode(vec3 v) {
    v /= abs(v.x) + abs(v.y) + abs(v.z);
    vec2 e = v.xy;
    if (v.z < 0.0) e = (1.0 - abs(e.yx)) * vec2(e.x >= 0.0 ? 1.0 : -1.0, e.y >= 0.0 ? 1.0 : -1.0);
    return e * 0.5 + 0.5;
}
vec3 nearOctDecode(vec2 f) {
    f = f * 2.0 - 1.0;
    vec3 v = vec3(f, 1.0 - abs(f.x) - abs(f.y));
    float t = max(-v.z, 0.0);
    v.x += v.x >= 0.0 ? -t : t;
    v.y += v.y >= 0.0 ? -t : t;
    return normalize(v);
}
#endif
