// What the scene cull's two shaders share: the dispatch's uniforms, the placement, drawable and level
// records, the buffers they write, and the reset, LOD choice and scatter both run. scene_cull.comp
// classifies by frustum; scene_occlusion.comp by this frame's early depth. See scene_cull.comp.
#ifndef SCENE_CULL_GLSL
#define SCENE_CULL_GLSL

layout(set = 0, binding = 0) uniform Cull {
    mat4 uCull;        // view-projection whose frustum culls (column form: clip = uCull * world)
    vec4 uCamera;      // xyz camera position, w = pixels-per-world at unit distance
    vec3 uLod;         // x error pixels, y hysteresis, z cull margin
    vec4 uRange;       // x first placement (global), y placement count, z first drawable (global), w visible base
    vec4 uArgs;        // x first record, y record count, z (unused: uSlots), w mode
    vec3 uFlags;       // x cull by uCull, y edited placement + 1 (0 = none), z its new margin
    mat4 uView;        // the camera's view (occlusion: a bound's nearest view depth)
    vec4 uOcclusion;   // x 1 = the camera's early list draws only what was visible last frame, 2 = nothing
                       // (--occlusion-cut: every frame a camera cut); y near plane;
                       // z first visible slot of the camera's late list
    vec4 uSlots;       // x LOD levels a drawable in lods[]; y indirect records a drawable: one per level, or
                       // 1 when no drawable has two placements (the one placement's level written into it)
} u;

struct Placement {
    vec4 boundsMin;
    vec4 boundsMax;
    uint drawable;     // within its bucket; uRange.z makes it global
    uint transform;
    float margin;      // per-placement multiple of the LOD budget (the Selection panel's)
    uint pad;
};

struct DrawableInfo {
    uint lodCount;
    int baseVertex;
    uint placementStart;  // within its bucket
    uint pad;
};

struct DrawableLod {
    float error;
    uint indexCount;
    uint firstIndex;
    uint pad;
};

layout(std430, set = 0, binding = 1) buffer CullPlacements { Placement placements[]; };
layout(std430, set = 0, binding = 2) readonly buffer CullDrawables { DrawableInfo drawables[]; };
layout(std430, set = 0, binding = 3) readonly buffer CullLods { DrawableLod lods[]; };
// Per (pass, placement), at the placement's visible slot: bits 0-7 the LOD level kept for hysteresis,
// bit 31 set when this pass culled it. Persistent: a level survives into the next frame's choice.
layout(std430, set = 0, binding = 4) buffer CullState { uint state[]; };
layout(std430, set = 0, binding = 5) buffer CullCursor { uint cursor[]; };
layout(std430, set = 0, binding = 6) buffer SceneArgs { uint args[]; };
layout(std430, set = 0, binding = 7) buffer SceneVisible { uint visible[]; };

const uint Culled = 0x80000000u;
// On the camera's early slots only: the placement passed the occlusion test last frame (scene_occlusion.comp).
const uint WasVisible = 0x40000000u;

// The record a drawable's placement at this level counts into, and the level table's row.
uint recordOf(uint drawable, uint lod) { return uint(u.uSlots.y) > 1u ? drawable * uint(u.uSlots.y) + lod : drawable; }
uint lodRow(uint drawableGlobal, uint lod) { return drawableGlobal * uint(u.uSlots.x) + lod; }
// One record a drawable: the counting placement writes its level's range (the only placement, so no race).
void writeRange(uint o, uint drawableGlobal, uint lod) {
    if (uint(u.uSlots.y) > 1u) return;
    DrawableLod l = lods[lodRow(drawableGlobal, lod)];
    args[o + 0u] = l.indexCount;
    args[o + 2u] = l.firstIndex;
}

// Frustum.FromViewProjection: Gribb-Hartmann planes from the clip matrix's rows, normalised so a
// margin is in world units.
vec4 row(mat4 m, int i) { return vec4(m[0][i], m[1][i], m[2][i], m[3][i]); }
vec4 normalized(vec4 p) { float len = length(p.xyz); return len > 1e-6 ? p / len : p; }

bool insidePlane(vec4 p, vec3 bmin, vec3 bmax, float margin) {
    vec3 v = vec3(p.x > 0.0 ? bmax.x : bmin.x, p.y > 0.0 ? bmax.y : bmin.y, p.z > 0.0 ? bmax.z : bmin.z);
    return dot(p.xyz, v) + p.w + margin >= 0.0;
}

bool intersects(mat4 vp, vec3 bmin, vec3 bmax, float margin) {
    vec4 r0 = row(vp, 0), r1 = row(vp, 1), r2 = row(vp, 2), r3 = row(vp, 3);
    return insidePlane(normalized(r3 + r0), bmin, bmax, margin)
        && insidePlane(normalized(r3 - r0), bmin, bmax, margin)
        && insidePlane(normalized(r3 + r1), bmin, bmax, margin)
        && insidePlane(normalized(r3 - r1), bmin, bmax, margin)
        && insidePlane(normalized(r2), bmin, bmax, margin)
        && insidePlane(normalized(r3 - r2), bmin, bmax, margin);
}

// PickLod: the coarsest level whose error is in budget, coarsening only past the hysteresis band.
uint pickLod(uint lodBase, uint lodCount, float budget, float pixelsPerWorld, uint current) {
    if (lodCount <= 1u || budget <= 0.0) return 0u;
    uint wanted = 0u;
    for (uint l = 1u; l < lodCount; ++l) {
        if (lods[lodBase + l].error * pixelsPerWorld <= budget) wanted = l;
        else break;
    }
    current = min(current, lodCount - 1u);
    if (wanted <= current) return wanted;
    float banded = budget / u.uLod.y;
    uint settled = current;
    for (uint l = current + 1u; l <= wanted; ++l) {
        if (lods[lodBase + l].error * pixelsPerWorld <= banded) settled = l;
        else break;
    }
    return settled;
}

void reset(uint r) {
    uint slots = uint(u.uSlots.y);
    uint d = uint(u.uRange.z) + r / slots;
    uint l = r % slots;
    DrawableInfo info = drawables[d];
    bool has = l < info.lodCount;
    DrawableLod lod = lods[lodRow(d, l)];
    uint o = (uint(u.uArgs.x) + r) * 5u;
    args[o + 0u] = has ? lod.indexCount : 0u;
    args[o + 1u] = 0u;
    args[o + 2u] = has ? lod.firstIndex : 0u;
    args[o + 3u] = uint(info.baseVertex);
    args[o + 4u] = 0u;
    cursor[uint(u.uArgs.x) + r] = 0u;
}

void classify(uint i) {
    uint g = uint(u.uRange.x) + i;
    Placement p = placements[g];
    // A margin edit travels with the dispatch and lands here, in the one thread that owns the row.
    if (uint(u.uFlags.y) == g + 1u) {
        p.margin = u.uFlags.z;
        placements[g].margin = p.margin;
    }
    vec3 bmin = p.boundsMin.xyz, bmax = p.boundsMax.xyz;
    bool vis = u.uFlags.x <= 0.0 || intersects(u.uCull, bmin, bmax, u.uLod.z);

    uint d = uint(u.uRange.z) + p.drawable;
    uint lodCount = drawables[d].lodCount;
    uint at = uint(u.uRange.w) + i;
    uint current = state[at] & 0xFFu;
    vec3 nearest = clamp(u.uCamera.xyz, bmin, bmax);
    float dist = max(length(u.uCamera.xyz - nearest), 0.01);
    uint lod = pickLod(lodRow(d, 0u), lodCount, u.uLod.x * p.margin, u.uCamera.w / dist, current);
    uint history = state[at] & WasVisible;
    // Two-phase occlusion: the early list draws what the late test found visible last frame, and the late
    // list (scene_occlusion.comp) whatever else this frame's early depth does not hide.
    if (u.uOcclusion.x > 0.0 && history == 0u) vis = false;
    if (u.uOcclusion.x > 1.5) vis = false;
    state[at] = lod | (vis ? 0u : Culled) | history;
    if (!vis) return;
    uint o = (uint(u.uArgs.x) + recordOf(p.drawable, lod)) * 5u;
    writeRange(o, d, lod);
    atomicAdd(args[o + 1u], 1u);
}

void scatter(uint i) {
    uint at = uint(u.uRange.w) + i;
    uint s = state[at];
    if ((s & Culled) != 0u) return;
    uint lod = s & 0xFFu;
    Placement p = placements[uint(u.uRange.x) + i];
    uint first = uint(u.uArgs.x) + recordOf(p.drawable, 0u);
    uint start = uint(u.uRange.w) + drawables[uint(u.uRange.z) + p.drawable].placementStart;
    if (uint(u.uSlots.y) > 1u) for (uint l = 0u; l < lod; ++l) start += args[(first + l) * 5u + 1u];
    uint record = uint(u.uArgs.x) + recordOf(p.drawable, lod);
    // Every placement at this level writes the same value.
    args[record * 5u + 4u] = start;
    // The level rides in the top 8 bits (instances.glsl's instanceRow masks it off).
    visible[start + atomicAdd(cursor[record], 1u)] = p.transform | (min(lod, 255u) << 24u);
}

#endif
