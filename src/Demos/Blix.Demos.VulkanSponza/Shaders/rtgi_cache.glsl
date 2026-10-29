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
// Every cell grid's origin (the cache's at every level, the cooked near cells'): off the round coordinates scenes are
// modelled on. At the world's origin a floor at y = 0, a wall on a 5 cm multiple, lay on a cell boundary, and a point
// rebuilt from the depth buffer fell a hair either side as the view changed -- its cell, the cooked cell it read, chosen
// at random: half the floor's samples in each of two cells, lookups missing in streaks that moved with the camera (the
// user, the crevices testbed, 2026-10-30). Shared by all levels (a cell's parent is still its coordinates halved).
#ifndef RTGI_ORIGIN
#define RTGI_ORIGIN vec3(0.0137, 0.0271, 0.0419)
#endif
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
// RTGI_GEOM_SCALE(p): the cell's size the geometry asks for at p (rtgi_volume.glsl's volCellScale, includers with the
// volume), <= 0 none -- then the camera's distance sizes it, as before. RTGI_FAR_GROW: with the geometry's size, the
// camera's floor per metre of distance (0: none).
#ifndef RTGI_FAR_GROW
#define RTGI_FAR_GROW 0.0
#endif
float rtgiCellSize(vec3 p, vec3 camera, vec4 params) {
    float dist = length(p - camera);
#ifdef RTGI_GEOM_SCALE
    float g = RTGI_GEOM_SCALE(p);
    if (g > 0.0) return max(g, params.x + RTGI_FAR_GROW * dist);
#endif
    return params.x + params.y * max(dist - RTGI_NEAR, 0.0);
}

float rtgiLevelF(vec3 p, vec3 camera, vec4 params) {
    return clamp(log2(rtgiCellSize(p, camera, params) / params.x), 0.0, params.w);
}

uint rtgiLevel(vec3 p, vec3 camera, vec4 params) {
    return uint(clamp(floor(log2(rtgiCellSize(p, camera, params) / params.x)), 0.0, params.w));
}

float rtgiSize(uint level, vec4 params) { return params.x * exp2(float(level)); }

// A cell's sun visibility known within 0.02 (binomial, from 64 rays at least): its points trace the sun 1 in 8
// (rtgi_rays.comp), told by its sequence counter's top bit (rtgi_resolve.comp).
bool rtgiSunSettled(float visibility, float rays) {
    float pv = clamp(visibility, 0.0, 1.0);
    return rays >= 64.0 && sqrt(max(pv * (1.0 - pv), 1e-4) / rays) < 0.02;
}

// The cell's two hashes: x the slot it starts probing at, y its key.
// The region of the point a key is made for (rtgi_nearcook.glsl's nearRegion: the 2 m probe its cooked surface cell
// reaches, 0 where none is cooked), part of every cell's key: the two sides of a thin wall reach different probes, so they
// are different cells -- one cell held both rooms' floor where it ran under a wall, both faces of a crevice's lip, and
// every blend and lookup that found it by its key carried the light across (the user, the crevices testbed's edges,
// 2026-10-30). Set by each shader for the point it is keying (its cell's stored w, bits 8 up, for a cell's own).
uint rtgiRegion = 0u;

uvec2 rtgiCellHash(ivec3 c, uint level, uint side, vec4 params) {
    uint a = rtgiHash(uint(c.x) * 0x9E3779B1u ^ rtgiHash(uint(c.y) * 0x85EBCA77u ^ rtgiHash(uint(c.z) * 0xC2B2AE3Du ^ (level * 6u + side) ^ rtgiRegion * 0x632BE5ABu)));
    uint b = rtgiHash(a ^ 0x68E31DA4u ^ uint(c.x) * 0x1B873593u ^ uint(c.y) * 0xCC9E2D51u ^ uint(c.z) * 0x27D4EB2Fu ^ rtgiRegion * 0x5BD1E995u) | 1u;
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
