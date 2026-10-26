// --rtgi (S1 of the GI redesign, SponzaLoop.Rtgi.cs): the world radiance cache's cells. A cell is a cube of world space
// at a level -- base size times 2^level, the level chosen by the distance from the camera -- and the side a surface in it
// faces (the normal's dominant axis and sign, six). Two hashes of (cell, level, side): the first picks the slot, linear
// probing over RTGI_PROBES slots; the second is the key held in it (never 0, which marks an empty slot).
//
// Per slot (the includer declares the buffers):
//   rtgiKeys[s]               the key, 0 empty
//   rtgiAccum[8s .. 8s+7]     this frame's samples, fixed point (RTGI_SCALE): indirect irradiance rgb, their count,
//                             how many missed (sky), sun rays that reached, sun rays traced
//   rtgiResolved[2s]          indirect irradiance E (rgb: sky and bounce, no direct sun), sky visibility
//   rtgiResolved[2s + 1]      sun visibility, samples held (capped), frames since last trained, sun samples held
//   rtgiRecord[2s .. 2s + 1]  what the readout reads of the cell, in one 32-byte line (rtgi_readout.frag): it read four
//                             or five buffers a cell, 17 cells a half-res pixel, and was the frame's largest pass (5 ms at
//                             Crytek Sponza, 2026-10-27). Halves (packHalf2x16) but two floats:
//                             [2s].x  light r, g          (the blend's, rtgi_filter.comp)
//                             [2s].y  light b, sky        (the blend's)
//                             [2s].z  relative error, done (the blend's)
//                             [2s].w  inverse hit distance, float (the resolve's, rtgi_resolve.comp)
//                             [2s+1].x direction x, y     (the resolve's)
//                             [2s+1].y direction z, near visibility
//                             [2s+1].z far light's share, 0
//                             [2s+1].w samples held, float (0: nothing; the eviction zeroes it all, rtgi_evict.comp)
// The includer defines RTGI_KEYS(i), RTGI_RESOLVED(i) accessors, and the uniforms' values through rtgiParams:
//   x the finest cell (m), y its growth per metre of distance, z slots - 1 (a power of two), w the top level.

#define RTGI_PROBES 8u
// A slot's accumulator words: indirect rgb, count, sky misses, sun reached, sun traced, luminance squared, and the
// irradiance vector's three components (signed, two's complement in the uints: rtgi_train.comp), the near visibility's
// weighted count, the luminance the escaping rays brought (the far light: rtgi_readout.frag's contact split), and the
// rays' inverse hit distances (256ths; a miss at the ray's reach) -- how near the geometry around the cell is. Fourteen
// words used (RTGI_WORDS), the stride 16: a cell's frame in one 64-byte line, read and cleared as four uvec4s
// (rtgi_resolve.comp) and its atomics on one line.
#define RTGI_ACC 16u
#define RTGI_WORDS 14u
// Fixed point 4096, a sample capped at 512, rounded stochastically (rtgi_train.comp): at 256 and rounded to nearest, the
// gate's dark corridor (irradiance ~0.001, a sample ~0.26 of a unit) rounded most samples to 0 -- its dead end read 0.77,
// the dimmest quarter of its points 0.70 (2026-10-26). A frame's sum holds ~2,000 capped samples a cell.
#define RTGI_SCALE 4096.0
#define RTGI_MAX_SAMPLE 512.0
// The luminance's square: fixed point 4096, capped at 32^2.
#define RTGI_LUM2_SCALE 4096.0
#define RTGI_LUM2_MAX 32.0

uint rtgiHash(uint x) {
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

uint rtgiSide(vec3 n) {
    vec3 a = abs(n);
    if (a.x >= a.y && a.x >= a.z) return n.x >= 0.0 ? 0u : 1u;
    if (a.y >= a.z) return n.y >= 0.0 ? 2u : 3u;
    return n.z >= 0.0 ? 4u : 5u;
}

// The level as a continuous value: the readout blends the two around it (rtgi_readout.frag) and the training fills both
// (rtgi_train.comp). Picked whole, a surface near a level's edge jumped to the other level's cells as the camera moved --
// another set of cells, held differently -- and re-settled: patterns that moved with the camera (the user, San Miguel,
// 2026-10-26).
// RTGI_NEAR: the near field's radius (m) -- cells the finest out to it, growing past it (rtgi_cache.glsl's includers
// define it from their uniforms; 0 when they do not).
#ifndef RTGI_NEAR
#define RTGI_NEAR 0.0
#endif
float rtgiLevelF(vec3 p, vec3 camera, vec4 params) {
    float cell = params.x + params.y * max(length(p - camera) - RTGI_NEAR, 0.0);
    return clamp(log2(cell / params.x), 0.0, params.w);
}

uint rtgiLevel(vec3 p, vec3 camera, vec4 params) {
    float cell = params.x + params.y * max(length(p - camera) - RTGI_NEAR, 0.0);
    return uint(clamp(floor(log2(cell / params.x)), 0.0, params.w));
}

float rtgiSize(uint level, vec4 params) { return params.x * exp2(float(level)); }

// The cell's two hashes: x the slot it starts probing at, y its key.
uvec2 rtgiCellHash(ivec3 c, uint level, uint side, vec4 params) {
    uint a = rtgiHash(uint(c.x) * 0x9E3779B1u ^ rtgiHash(uint(c.y) * 0x85EBCA77u ^ rtgiHash(uint(c.z) * 0xC2B2AE3Du ^ (level * 6u + side))));
    uint b = rtgiHash(a ^ 0x68E31DA4u ^ uint(c.x) * 0x1B873593u ^ uint(c.y) * 0xCC9E2D51u ^ uint(c.z) * 0x27D4EB2Fu) | 1u;
    return uvec2(a & uint(params.z), b);
}

// The slot holding the cell, or 0xFFFFFFFF; every probe is looked at (an evicted slot may sit before the one held).
uint rtgiFind(uvec2 h, vec4 params) {
    for (uint k = 0u; k < RTGI_PROBES; k++) {
        uint s = (h.x + k) & uint(params.z);
        if (RTGI_KEYS(s) == h.y) return s;
    }
    return 0xFFFFFFFFu;
}
