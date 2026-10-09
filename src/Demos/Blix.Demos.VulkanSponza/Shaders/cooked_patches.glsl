// Cooked light transport (stage 5a): patches (position, normal, the indirect irradiance their solve left them -- sky,
// bounce, and the first bounce of direct sun through sunlets), found through a grid of cells (CSR: each cell's first
// entry and the patch ids). What a ray's hit reads: its OWN direct sun from the cascades (the caller's), the rest from
// its nearest patch facing the same way.
//
// The includer defines COOKED_BINDING (three consecutive set-0 bindings) and COOKED_GRID / COOKED_DIMS (the grid's
// lowest corner xyz and cell size w; its dims xyz, w 1 when uploaded).
#ifndef COOKED_PATCHES_GLSL
#define COOKED_PATCHES_GLSL
layout(std430, set = 0, binding = COOKED_BINDING) readonly buffer CookedPatches { vec4 cookedPatch[]; };   // 3 a patch: pos, normal, indirect
layout(std430, set = 0, binding = COOKED_BINDING + 1) readonly buffer CookedCells { uint cookedCellStart[]; };
layout(std430, set = 0, binding = COOKED_BINDING + 2) readonly buffer CookedIds { uint cookedId[]; };

vec3 cookedIndirect(vec3 p, vec3 n, out bool found) {
    found = false;
    if (COOKED_DIMS.w < 0.5) return vec3(0.0);
    ivec3 dims = ivec3(COOKED_DIMS.xyz);
    ivec3 c = ivec3(floor((p - COOKED_GRID.xyz) / COOKED_GRID.w));
    float bestD = 1e30;
    int best = -1;
    for (int dz = -1; dz <= 1; ++dz)
    for (int dy = -1; dy <= 1; ++dy)
    for (int dx = -1; dx <= 1; ++dx) {
        ivec3 cc = c + ivec3(dx, dy, dz);
        if (any(lessThan(cc, ivec3(0))) || any(greaterThanEqual(cc, dims))) continue;
        int cell = (cc.z * dims.y + cc.y) * dims.x + cc.x;
        for (uint k = cookedCellStart[cell]; k < cookedCellStart[cell + 1]; ++k) {
            int j = int(cookedId[k]);
            if (dot(cookedPatch[3 * j + 1].xyz, n) < 0.5) continue;
            vec3 d = cookedPatch[3 * j].xyz - p;
            float d2 = dot(d, d);
            if (d2 < bestD) { bestD = d2; best = j; }
        }
    }
    if (best < 0) return vec3(0.0);
    found = true;
    return cookedPatch[3 * best + 2].rgb;
}
#endif
