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

#endif
