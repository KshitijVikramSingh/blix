// World-anchored surface probes (Sponza stage 4g-ix): level 0 of the clipmap, but fixed in the world and only where
// surfaces are seen. The clipmap follows the camera, so a surface is answered by one level and then a coarser one as
// the camera walks -- its light changes with where the camera stands (measured: 6.7% spread, 16% range, walking ~3 to
// ~15 m). A probe here sits at the same place a level-0 probe would ((cell + 0.5) x spacing), but its cell never
// re-points: the same surface keeps the same probes wherever the camera is.
//
// Storage is the clipmap's, extended: a surface probe is a slot after the clipmap's (its state word, visible stamp
// and scheduling as any slot's), its tile below the clipmap's in the same atlases. A world cell finds its slot through
// a dense index over the scene's bounds (BLIX_SURFACE_INDEX(i): slot + 1, 0 none, >= 0xFFFFFFFE being allocated or
// full) -- simple, and sized for a building, not a city: a hash replaces it when a world outgrows it.
//
// Include after probe_clipmap.glsl (it uses its fetch and state macros, octahedral maps and visibility weighting).
#ifndef BLIX_PROBE_SURFACE_GLSL
#define BLIX_PROBE_SURFACE_GLSL

#define BLIX_SURFACE_ALLOCATED 4u   // in the state word's flags, beside SOLVED 1 and BURIED 2

struct BlixSurfaceGrid {
    ivec3 minCell;    // the index's lowest world cell
    ivec3 dims;       // cells the index spans
    float spacing;    // = the clipmap's base spacing
    int firstSlot;    // the first surface slot in the state buffer (after every clipmap slot)
    int tileRow0;     // the atlas row, in texels, where surface tiles start
    int columns;      // tiles across the atlas
    bool enabled;
};

int blix_surfaceCellIndex(BlixSurfaceGrid s, ivec3 cell) {
    ivec3 i = cell - s.minCell;
    if (any(lessThan(i, ivec3(0))) || any(greaterThanEqual(i, s.dims))) return -1;
    return (i.z * s.dims.y + i.y) * s.dims.x + i.x;
}

ivec2 blix_surfaceTileOrigin(BlixSurfaceGrid s, int slot) {
    return ivec2((slot % s.columns) * BLIX_CLIPMAP_TILE, s.tileRow0 + (slot / s.columns) * BLIX_CLIPMAP_TILE);
}

#ifdef BLIX_SURFACE_INDEX
// The slot a world cell's probe lives in, or -1.
int blix_surfaceSlot(BlixSurfaceGrid s, ivec3 cell) {
    int i = blix_surfaceCellIndex(s, cell);
    if (i < 0) return -1;
    uint v = BLIX_SURFACE_INDEX(i);
    return (v == 0u || v >= 0xFFFFFFFEu) ? -1 : int(v - 1u);
}

#ifdef BLIX_CLIPMAP_IRRADIANCE
// The surface probes' answer at a point for a surface facing n: as blix_clipmapLevelSample at level 0 -- the eight
// probes around the lifted point, trilinear share x facing x Chebyshev visibility -- over those allocated, solved and
// not buried. weight is what survived (0: nothing here; the clipmap answers instead).
vec4 blix_surfaceSample(BlixSurfaceGrid s, vec3 world, vec3 n, out float weight) {
    weight = 0.0;
    if (!s.enabled) return vec4(0.0);
    float spacing = s.spacing;
    vec3 p = world + n * BLIX_CLIPMAP_LIFT(spacing);
    vec3 g = p / spacing - 0.5;
    ivec3 base = ivec3(floor(g));
    vec3 frac = g - vec3(base);
    vec4 sum = vec4(0.0);
    for (int i = 0; i < 8; ++i) {
        ivec3 offset = ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        ivec3 cell = base + offset;
        int slot = blix_surfaceSlot(s, cell);
        if (slot < 0) continue;
        uvec4 state = BLIX_CLIPMAP_STATE(s.firstSlot + slot);
        if ((state.w & BLIX_CLIPMAP_SOLVED) == 0u || (state.w & BLIX_CLIPMAP_BURIED) != 0u || ivec3(state.xyz) != cell) continue;
        vec3 t = mix(1.0 - frac, frac, vec3(offset));
        float w = t.x * t.y * t.z;
        vec3 toProbe = (vec3(cell) + 0.5) * spacing - p;
        float dist = length(toProbe);
        vec3 dir = dist > 1e-5 ? toProbe / dist : n;
        float facing = dot(dir, n) * 0.5 + 0.5;
        w *= facing * facing;
        if (w <= 1e-6) continue;
        ivec2 tile = blix_surfaceTileOrigin(s, slot);
        vec2 moments = blix_clipmapTileSample(tile, -dir, true).rg;
        if (dist > moments.x) {
            float variance = max(moments.y, 1e-5);
            float d = dist - moments.x;
            float chebyshev = variance / (variance + d * d);
            w *= max(BLIX_CLIPMAP_VISIBILITY(chebyshev), 0.0);
        }
        if (w <= 1e-6) continue;
        sum += w * vec4(blix_clipmapTileSample(tile, n, false).rgb, blix_clipmapTileSample(tile, n, true).a);
        weight += w;
    }
    return weight > 0.0 ? sum / weight : vec4(0.0);
}

// What shading reads: the surface probes where they answer, the clipmap where they do not.
vec4 blix_worldSample(BlixSurfaceGrid s, BlixClipmap c, vec3 world, vec3 n, out bool found) {
    float w;
    vec4 a = blix_surfaceSample(s, world, n, w);
    if (w > 0.0) { found = true; return a; }
    return blix_clipmapSample(c, world, n, found);
}
#endif
#endif

#endif
