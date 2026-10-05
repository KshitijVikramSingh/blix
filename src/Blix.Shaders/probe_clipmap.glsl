// A camera-relative clipmap of probes: where they are, and where each lives in the atlas. Blix.Geometry's
// ProbeClipmap is the contract, written once in C# and mirrored here function for function.
//
// BLIX_CLIPMAP_LEVELS levels of the same dims, level l spaced base * 2^l apart, each a block of cells centred on
// the camera and snapped to whole cells; a probe at the centre of its cell. A cell's slot is its coordinate modulo
// dims, so a block scrolling keeps every remaining probe in place. The atlas holds 8x8 tiles, levels side by side in
// x, then x across and y, z down within a level.
//
// The caller supplies a BlixClipmap (dims, base spacing, blend band, each level's lowest cell), typically from its
// own uniform block.
#ifndef BLIX_PROBE_CLIPMAP_GLSL
#define BLIX_PROBE_CLIPMAP_GLSL

#ifndef BLIX_CLIPMAP_LEVELS
#define BLIX_CLIPMAP_LEVELS 4
#endif
#define BLIX_CLIPMAP_TILE 8

struct BlixClipmap {
    ivec3 dims;
    float baseSpacing;
    float blendProbes;
    ivec3 origin[BLIX_CLIPMAP_LEVELS];   // each level's lowest cell
};

float blix_clipmapSpacing(BlixClipmap c, int level) { return c.baseSpacing * float(1 << level); }

ivec3 blix_clipmapCellOf(BlixClipmap c, int level, vec3 world) {
    return ivec3(floor(world / blix_clipmapSpacing(c, level)));
}

vec3 blix_clipmapProbePosition(BlixClipmap c, int level, ivec3 cell) {
    return (vec3(cell) + 0.5) * blix_clipmapSpacing(c, level);
}

// GLSL leaves % undefined for a negative operand, and cells below the world origin are negative. Nor can a float
// division decide it, since fast math does not round one correctly (-32 / 32 may land at -1.0000001). So shift the
// operand non-negative by a multiple of n first, integers only: exact for |v| < 2^22 (ProbeClipmap.MaxCell).
ivec3 blix_clipmapWrap(ivec3 v, ivec3 n) { return (v + n * ((1 << 22) / n)) % n; }

ivec3 blix_clipmapSlot(BlixClipmap c, ivec3 cell) { return blix_clipmapWrap(cell, c.dims); }

int blix_clipmapSlotIndex(BlixClipmap c, ivec3 slot) { return (slot.z * c.dims.y + slot.y) * c.dims.x + slot.x; }

ivec2 blix_clipmapTileOrigin(BlixClipmap c, int level, ivec3 slot) {
    return ivec2(level * c.dims.x + slot.x, slot.y + slot.z * c.dims.y) * BLIX_CLIPMAP_TILE;
}

ivec3 blix_clipmapCellInSlot(BlixClipmap c, int level, ivec3 slot) {
    ivec3 o = c.origin[level];
    return o + blix_clipmapWrap(slot - o, c.dims);
}

// How many probe spacings a point is inside the region a level can interpolate (negative outside).
float blix_clipmapInsideDistance(BlixClipmap c, int level, vec3 world) {
    vec3 g = world / blix_clipmapSpacing(c, level) - vec3(c.origin[level]) - 0.5;
    float low = min(g.x, min(g.y, g.z));
    vec3 high3 = vec3(c.dims - 1) - g;
    return min(low, min(high3.x, min(high3.y, high3.z)));
}

// The finest level that can answer for a point (-1: none), and how much of the next level to blend in.
int blix_clipmapLocate(BlixClipmap c, vec3 world, out float blend) {
    blend = 0.0;
    for (int l = 0; l < BLIX_CLIPMAP_LEVELS; ++l) {
        float inside = blix_clipmapInsideDistance(c, l, world);
        if (inside < 0.0) continue;
        blend = (c.blendProbes <= 0.0 || l == BLIX_CLIPMAP_LEVELS - 1) ? 0.0 : clamp(1.0 - inside / c.blendProbes, 0.0, 1.0);
        return l;
    }
    return -1;
}

// ---------------------------------------------------------------------------------------------------------------
// Sampling. What a probe holds, per slot, in two atlases of 8x8 tiles (a 6x6 octahedral interior and a border
// ring that makes bilinear filtering safe at the fold): the irradiance atlas (rgb, a surface facing that direction)
// and the depth atlas (r mean and g variance of hit distance, b unused, a directional sky visibility: the cosine-
// weighted share of rays that escaped). And a state word per slot: the cell it was solved for, and flags.
//
// The caller says how a texel and a state are read, so the injection can read the atlas it is updating in place
// (imageLoad) and shading can read it through a sampler (texelFetch), identically:
//   BLIX_CLIPMAP_IRRADIANCE(ivec2 texel)  -> vec4
//   BLIX_CLIPMAP_DEPTH(ivec2 texel)       -> vec4
//   BLIX_CLIPMAP_STATE(int slot)          -> uvec4  (slot = level * probes-per-level + slot index)
#define BLIX_CLIPMAP_SOLVED 1u
#define BLIX_CLIPMAP_BURIED 2u

#ifdef BLIX_CLIPMAP_IRRADIANCE
#include "octahedral.glsl"

// A direction's place in a tile's interior, bilinear by hand over the four texels around it.
vec4 blix_clipmapTileSample(ivec2 tile, vec3 dir, bool depth) {
    vec2 t = (blix_octEncode(normalize(dir)) * 0.5 + 0.5) * 6.0 + 0.5;   // texel space, past the border ring
    ivec2 base = ivec2(floor(t));
    vec2 f = t - vec2(base);
    vec4 a, b, c, d;
    if (depth) {
        a = BLIX_CLIPMAP_DEPTH(tile + base); b = BLIX_CLIPMAP_DEPTH(tile + base + ivec2(1, 0));
        c = BLIX_CLIPMAP_DEPTH(tile + base + ivec2(0, 1)); d = BLIX_CLIPMAP_DEPTH(tile + base + ivec2(1, 1));
    } else {
        a = BLIX_CLIPMAP_IRRADIANCE(tile + base); b = BLIX_CLIPMAP_IRRADIANCE(tile + base + ivec2(1, 0));
        c = BLIX_CLIPMAP_IRRADIANCE(tile + base + ivec2(0, 1)); d = BLIX_CLIPMAP_IRRADIANCE(tile + base + ivec2(1, 1));
    }
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// One level's answer at a point for a surface facing n: irradiance (rgb) and sky visibility (a), over the eight
// probes around it, each weighted by trilinear share, facing (squared, so a probe behind the surface counts for
// nothing) and Chebyshev visibility from its depth moments (cubed), and only if its slot holds the cell expected
// and is solved and not buried. weight is what survived (0: nothing could answer).
vec4 blix_clipmapLevelSample(BlixClipmap c, int level, vec3 world, vec3 n, out float weight) {
    float spacing = blix_clipmapSpacing(c, level);
    vec3 p = world + n * (0.25 * spacing);                 // lifted off the surface, a quarter cell
    vec3 g = p / spacing - 0.5;
    ivec3 base = ivec3(floor(g));
    vec3 frac = g - vec3(base);
    vec4 sum = vec4(0.0);
    weight = 0.0;
    for (int i = 0; i < 8; ++i) {
        ivec3 offset = ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        ivec3 cell = base + offset;
        ivec3 slot = blix_clipmapSlot(c, cell);
        uvec4 state = BLIX_CLIPMAP_STATE(level * c.dims.x * c.dims.y * c.dims.z + blix_clipmapSlotIndex(c, slot));
        if ((state.w & BLIX_CLIPMAP_SOLVED) == 0u || (state.w & BLIX_CLIPMAP_BURIED) != 0u
            || ivec3(state.xyz) != cell) continue;
        vec3 t = mix(1.0 - frac, frac, vec3(offset));
        float w = t.x * t.y * t.z;
        vec3 toProbe = blix_clipmapProbePosition(c, level, cell) - p;
        float dist = length(toProbe);
        vec3 dir = dist > 1e-5 ? toProbe / dist : n;
        float facing = dot(dir, n) * 0.5 + 0.5;
        w *= facing * facing;
        if (w <= 1e-6) continue;
        ivec2 tile = blix_clipmapTileOrigin(c, level, slot);
        vec2 moments = blix_clipmapTileSample(tile, -dir, true).rg;
        if (dist > moments.x) {
            float variance = max(moments.y, 1e-5);
            float d = dist - moments.x;
            float chebyshev = variance / (variance + d * d);
            w *= max(chebyshev * chebyshev * chebyshev, 0.0);
        }
        if (w <= 1e-6) continue;
        sum += w * vec4(blix_clipmapTileSample(tile, n, false).rgb, blix_clipmapTileSample(tile, n, true).a);
        weight += w;
    }
    return weight > 0.0 ? sum / weight : vec4(0.0);
}

// The clipmap's answer at a point: the finest level that holds it, blended toward the next near its edge. If a level
// has nothing (every probe rejected or unsolved), the next one answers alone. found is false past the coarsest level
// or where no level could answer.
vec4 blix_clipmapSample(BlixClipmap c, vec3 world, vec3 n, out bool found) {
    float blend;
    int level = blix_clipmapLocate(c, world, blend);
    found = false;
    if (level < 0) return vec4(0.0);
    for (int l = level; l < BLIX_CLIPMAP_LEVELS; ++l) {
        float w0;
        vec4 a = blix_clipmapLevelSample(c, l, world, n, w0);
        if (w0 <= 0.0) { blend = 0.0; continue; }   // nothing here: try the next level outright
        found = true;
        if (blend <= 0.0 || l + 1 >= BLIX_CLIPMAP_LEVELS || blix_clipmapInsideDistance(c, l + 1, world) < 0.0) return a;
        float w1;
        vec4 b = blix_clipmapLevelSample(c, l + 1, world, n, w1);
        return w1 > 0.0 ? mix(a, b, blend) : a;
    }
    return vec4(0.0);
}
#endif

#endif
