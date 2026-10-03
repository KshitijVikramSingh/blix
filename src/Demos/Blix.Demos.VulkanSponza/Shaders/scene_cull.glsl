// What the scene cull's two shaders share: the dispatch's uniforms, the placement, drawable and level
// records, the buffers they write, and the reset, LOD choice and scatter both run. scene_cull.comp
// classifies by frustum; scene_occlusion.comp by this frame's early depth. See scene_cull.comp.
#ifndef SCENE_CULL_GLSL
#define SCENE_CULL_GLSL

layout(set = 0, binding = 0) uniform Cull {
    mat4 uCull;        // view-projection whose frustum culls (column form: clip = uCull * world)
    mat4 uReceivers;   // camera view-projection a swept caster must also reach
    vec4 uSweep;       // xyz unit sun direction, w = 1 to sweep bounds along it
    vec4 uVolumeMin;   // scene volume a sweep runs to (SceneExitDistance)
    vec4 uVolumeMax;
    vec4 uCamera;      // xyz camera position, w = pixels-per-world at unit distance
    vec4 uLod;         // x error pixels, y world error budget (> 0 selects by world error), z hysteresis, w cull margin
    vec4 uRange;       // x first placement (global), y placement count, z first drawable (global), w visible base
    vec4 uArgs;        // x first record, y record count, z LOD slots, w mode
    vec4 uFlags;       // x cull by uCull, y cull by uReceivers, z edited placement + 1 (0 = none), w its new margin
    mat4 uView;        // the camera's view (occlusion: a bound's nearest view depth)
    vec4 uOcclusion;   // x 1 = the camera's early list draws only what was visible last frame, 2 = nothing
                       // (--occlusion-cut: every frame a camera cut); y near plane;
                       // z first visible slot of the camera's late list
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

// SceneExitDistance: how far these bounds travel along dir before leaving the scene volume, measured
// from the trailing face.
float exitDistance(vec3 bmin, vec3 bmax, vec3 dir) {
    float t = 3.402823e38;
    for (int a = 0; a < 3; ++a) {
        float d = dir[a];
        if (abs(d) < 1e-5) continue;
        float start = d > 0.0 ? bmin[a] : bmax[a];
        float wall = d > 0.0 ? u.uVolumeMax[a] : u.uVolumeMin[a];
        t = min(t, max((wall - start) / d, 0.0));
    }
    return t == 3.402823e38 ? length(u.uVolumeMax.xyz - u.uVolumeMin.xyz) : t;
}

// PickLod / PickLodWorld: the coarsest level whose error is in budget, coarsening only past the
// hysteresis band. pixelsPerWorld is 1 for a world budget.
uint pickLod(uint lodBase, uint lodCount, float budget, float pixelsPerWorld, uint current) {
    if (lodCount <= 1u || budget <= 0.0) return 0u;
    uint wanted = 0u;
    for (uint l = 1u; l < lodCount; ++l) {
        if (lods[lodBase + l].error * pixelsPerWorld <= budget) wanted = l;
        else break;
    }
    current = min(current, lodCount - 1u);
    if (wanted <= current) return wanted;
    float banded = budget / u.uLod.z;
    uint settled = current;
    for (uint l = current + 1u; l <= wanted; ++l) {
        if (lods[lodBase + l].error * pixelsPerWorld <= banded) settled = l;
        else break;
    }
    return settled;
}

void reset(uint r) {
    uint slots = uint(u.uArgs.z);
    uint d = uint(u.uRange.z) + r / slots;
    uint l = r % slots;
    DrawableInfo info = drawables[d];
    bool has = l < info.lodCount;
    DrawableLod lod = lods[d * slots + l];
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
    if (uint(u.uFlags.z) == g + 1u) {
        p.margin = u.uFlags.w;
        placements[g].margin = p.margin;
    }
    vec3 bmin = p.boundsMin.xyz, bmax = p.boundsMax.xyz;

    // Casters are tested by their bounds swept along the sun to where their shadow leaves the scene.
    vec3 tmin = bmin, tmax = bmax;
    if (u.uSweep.w > 0.0) {
        vec3 sweep = u.uSweep.xyz * exitDistance(bmin, bmax, u.uSweep.xyz);
        tmin = min(bmin, bmin + sweep);
        tmax = max(bmax, bmax + sweep);
    }
    float margin = u.uLod.w;
    bool vis = u.uFlags.x <= 0.0 || intersects(u.uCull, tmin, tmax, margin);
    if (vis && u.uFlags.y > 0.0) vis = intersects(u.uReceivers, tmin, tmax, margin);

    uint slots = uint(u.uArgs.z);
    uint d = uint(u.uRange.z) + p.drawable;
    uint lodCount = drawables[d].lodCount;
    uint at = uint(u.uRange.w) + i;
    uint current = state[at] & 0xFFu;
    uint lod;
    if (u.uLod.y > 0.0) {
        lod = pickLod(d * slots, lodCount, u.uLod.y * p.margin, 1.0, current);
    } else {
        vec3 nearest = clamp(u.uCamera.xyz, bmin, bmax);
        float dist = max(length(u.uCamera.xyz - nearest), 0.01);
        lod = pickLod(d * slots, lodCount, u.uLod.x * p.margin, u.uCamera.w / dist, current);
    }
    uint history = state[at] & WasVisible;
    // Two-phase occlusion: the early list draws what the late test found visible last frame, and the late
    // list (scene_occlusion.comp) whatever else this frame's early depth does not hide.
    if (u.uOcclusion.x > 0.0 && history == 0u) vis = false;
    if (u.uOcclusion.x > 1.5) vis = false;
    state[at] = lod | (vis ? 0u : Culled) | history;
    if (!vis) return;
    atomicAdd(args[(uint(u.uArgs.x) + p.drawable * slots + lod) * 5u + 1u], 1u);
}

void scatter(uint i) {
    uint at = uint(u.uRange.w) + i;
    uint s = state[at];
    if ((s & Culled) != 0u) return;
    uint lod = s & 0xFFu;
    Placement p = placements[uint(u.uRange.x) + i];
    uint slots = uint(u.uArgs.z);
    uint first = uint(u.uArgs.x) + p.drawable * slots;
    uint start = uint(u.uRange.w) + drawables[uint(u.uRange.z) + p.drawable].placementStart;
    for (uint l = 0u; l < lod; ++l) start += args[(first + l) * 5u + 1u];
    // Every placement at this level writes the same value.
    args[(first + lod) * 5u + 4u] = start;
    visible[start + atomicAdd(cursor[first + lod], 1u)] = p.transform;
}

#endif
