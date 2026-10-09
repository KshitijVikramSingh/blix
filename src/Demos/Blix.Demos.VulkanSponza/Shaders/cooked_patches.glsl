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

// The nearest patch facing the way n does (dot >= 0.5), or -1.
int cookedNearest(vec3 p, vec3 n) {
    if (COOKED_DIMS.w < 0.5) return -1;
    ivec3 dims = ivec3(COOKED_DIMS.xyz);
    ivec3 c = ivec3(floor((p - COOKED_GRID.xyz) / COOKED_GRID.w));
    float bestD = 1e30;
    int best = -1;
    // Cells are the patch spacing: the 27 around the hit hold the nearest facing patch nearly always; the ring
    // beyond (radius 2) only when they do not. At twice the spacing every hit read hundreds of patches, and the texel
    // gather ran at ~4 M rays/s on Sponza.
    for (int radius = 1; radius <= 2 && best < 0; ++radius)
    for (int dz = -radius; dz <= radius; ++dz)
    for (int dy = -radius; dy <= radius; ++dy)
    for (int dx = -radius; dx <= radius; ++dx) {
        if (radius == 2 && max(abs(dx), max(abs(dy), abs(dz))) < 2) continue;
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
    return best;
}

// The patches' light interpolated at p (facing within 60 degrees, Gaussian in distance over one spacing): rgb the
// indirect irradiance, a their openness (normal.w, SponzaLoop.Transport.cs) with the same weights; weight their sum.
vec4 cookedInterp(vec3 p, vec3 n, out float weight) {
    weight = 0.0;
    if (COOKED_DIMS.w < 0.5) return vec4(0.0);
    ivec3 dims = ivec3(COOKED_DIMS.xyz);
    float spacing = COOKED_GRID.w;
    ivec3 c = ivec3(floor((p - COOKED_GRID.xyz) / spacing));
    vec4 sum = vec4(0.0);
    for (int dz = -1; dz <= 1; ++dz)
    for (int dy = -1; dy <= 1; ++dy)
    for (int dx = -1; dx <= 1; ++dx) {
        ivec3 cc = c + ivec3(dx, dy, dz);
        if (any(lessThan(cc, ivec3(0))) || any(greaterThanEqual(cc, dims))) continue;
        int cell = (cc.z * dims.y + cc.y) * dims.x + cc.x;
        for (uint k = cookedCellStart[cell]; k < cookedCellStart[cell + 1]; ++k) {
            int j = int(cookedId[k]);
            vec4 pn = cookedPatch[3 * j + 1];
            float facing = dot(pn.xyz, n);
            if (facing < 0.5) continue;
            vec3 d = cookedPatch[3 * j].xyz - p;
            float w = facing * facing * exp(-dot(d, d) / (spacing * spacing));
            sum += w * vec4(cookedPatch[3 * j + 2].rgb, pn.w);
            weight += w;
        }
    }
    return weight > 1e-6 ? sum / weight : vec4(0.0);
}

vec3 cookedIndirect(vec3 p, vec3 n, out bool found) {
    int best = cookedNearest(p, n);
    found = best >= 0;
    return found ? cookedPatch[3 * best + 2].rgb : vec3(0.0);
}
#endif
