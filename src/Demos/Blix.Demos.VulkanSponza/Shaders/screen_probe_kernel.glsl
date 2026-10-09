// Screen probes (screen_probe.glsl): place one per tile on the visible surface, trace a few full-length rays from
// it, and fold them into the radiance its past carried.
//
// A workgroup holds PROBES_PER_GROUP probes of RAYS rays. Each probe:
//   place    the tile's centre pixel, or the first of four inner points that has a surface; position from the
//            depth, geometric normal and SurfaceKey from the pre-pass
//   history  two questions, kept apart (stage 4e):
//              ownership  the same SurfaceKey (source primitive x instance, one across the cook's chunks), found where
//                         the pre-pass's velocity says this pixel's surface was last frame. Mismatch: never reused.
//              support    within one tile's footprint at this depth of where this point WAS (its position now minus
//                         the pre-pass's world motion): whether a sample of the same surface is local enough to stand
//                         in here. Velocity finds the tile, world motion the place; neither asks how the surface moved
//                         (rigid, skinned, wind), and the key stays ownership alone, not a stand-in for a transform. A key is authoring granularity, not a connected place (35 of
//                         Sponza's 454 keys span disconnected regions, up to 4 over 15.5 m; Bistro 43 of 1,591, 30.9
//                         m), and radiance varies along one surface.
//            Measured under walking motion against the path-traced surface reference: key + support matched the old
//            rule's accuracy (Bistro mean error 0.173 against 0.172; Sponza 0.00037 against 0.00045) keeping 92-94% of
//            probes' history against 81-87%; key alone kept 97-98% and was 47% worse in Bistro. The old tests (same
//            material, normal within 25 degrees, the same plane, still visible) were proxies for the identity the
//            key now states, and went.
//            Staleness -- has the lighting cached here changed? -- is a fourth question, and open (plan stage 4f):
//            under the mover, correctly owned history read -36% on the curtain and +59% on the still wall beside it.
//            Surface motion is not lighting invalidation, so a kept probe's world displacement is counted as a
//            diagnostic only, never used to drop history.
//            Searched over the 3x3 tiles around where the probe falls last frame (TAA's jitter moves which surface a
//            tile's centre lands on at edges); the nearest candidate passing both wins, else the probe starts afresh.
//   trace    8 directions uniform over its hemisphere; a hit is lit by the sun and by the probe clipmap's
//            irradiance at the hit, re-radiated in its albedo; an escape brings the disc-free sky. The sun at a hit
//            is the shadow cascades' answer where they cover it (hard, as the fog takes it: the lit pass's own
//            maps, so a hit agrees with how that surface is shaded), a shadow ray beyond them. Shadow rays were
//            30% of this pass (6.5 of 21.5 ms on Sponza).
//   blend    the frame's estimate (2 pi / rays per sample, the uniform hemisphere's density) averaged in at 1/n,
//            n capped at uFrame.y
//
// Half the tile ROWS trace each frame, alternating (uParams.w bit 16 turns it off): a workgroup is 8 probes of one
// row, so whole workgroups skip the trace. Skipping alternate probes inside a group would save nothing, since an
// idle lane in a busy SIMD group costs what a working one does. A probe that does not trace still re-places itself,
// finds its past and carries it on unchanged; one with no past and no rays holds nothing (accumulated 0, so
// readers weigh it 0 and its neighbours or the clipmap answer).
//
// Writes the tile's header (first probe, count) as well as the probe: one per tile today, at pool index = tile.
//
// Young probes converge fast: after a camera move, a probe started from nothing showed its first few rays as
// tile-sized blotches for seconds (the raw indirect light 30 / 120 / 600 frames after a reset differed from the
// converged by 10.0 / 5.7 / 3.6%). A fresh probe (last frame's in its tile missing or under uFresh.y frames) now
// traces uFresh.x passes of its own rays, counted as that many frames, and a probe under 32 frames traces every
// frame whatever its row. Borrowing was tried first and is off by default: a clipmap prior (uParams2.x frames,
// --screen-probe-seed) and a filter widened for young probes (--young-filter) are both biased where lighting has an
// edge and after any reset (30 frames on: +8.1% against -0.3% with 4 passes of its own, p90 83% against 22%).

// Three stages, one body (frame audit F5): screen_probe_place.comp, screen_probe_trace.comp and
// screen_probe_integrate.comp include this with SCREEN_PROBE_PLACE / _TRACE / _INTEGRATE defined. They were one
// kernel, in which a ray cost 6-10x what the same traversal costs in a lean kernel (the GPU ray bench: 13.6 M probe-
// shaped rays/s; the probes ~1.7 M): traversal waits on memory and needs many threads in flight, and the fused
// kernel's per-lane state (two SH estimates, the history search, the placement's shared candidates) left room for
// few. Now: place writes one record per probe slot; trace is one thread per ray and holds nothing else; integrate,
// per slot, folds the rays into the probe and finds its past.

#define RAYS 32
#define PROBES_PER_GROUP 2
#define PI 3.14159265359

layout(set = 0, binding = 0) uniform ScreenProbes {
    mat4 uInvProjection;
    mat4 uInvView;
    mat4 uViewProj;        // this frame's, for the past probe's visibility now
    mat4 uPrevViewProj;    // last frame's, for finding each probe's past
    vec4 uTarget;          // xy frame pixels, zw tiles across and down
    vec4 uDims;            // the clipmap: xyz probes per level, w base spacing
    vec4 uParams;          // x the clipmap's blend band; y 1 when last frame's probes are valid; z one pixel's width
                           // at unit depth (2 tan(fov/2) / height); w a cost-attribution switch (--screen-probe-
                           // ablate): 1 no sun at hits, 2 no clipmap at hits, 4 no tracing at all, 8 shadow rays
                           // everywhere (the cascades not consulted), 16 every row traces every frame
    vec4 uOrigin0;
    vec4 uOrigin1;
    vec4 uOrigin2;
    vec4 uOrigin3;
    vec4 uSunDirection;    // xyz the direction the sun travels
    vec4 uSunIrradiance;   // rgb
    vec4 uFrame;           // x frame counter, y history cap (frames), z sky strength, w sky mip for a ray
    mat4 uCascadeVP[3];
    vec4 uParams2;         // x frames of history a fresh probe's clipmap prior counts for (0: start from nothing), y 1:
                           // support measured where the point is now, ignoring its world motion (--probe-support-now),
                           // z 1: the control arm (--dependency-reset): a dependent probe drops its whole past instead,
                           // w how far a probe ray traces, metres (--probe-ray-length; 0: the whole way)
    // Stage 4f: the reach of geometry that moved this frame (the mover's), w of the min 1 when there is one; w of
    // the max the dynamic part's history cap in frames (--dynamic-history).
    vec4 uDynamicMin;
    vec4 uDynamicMax;
    // Fresh probes (stage 4f-ii'): x passes of RAYS rays a fresh probe traces (1: as any other), y the frames under
    // which last frame's probe in this tile makes this one fresh; z 1: no second layer (--probe-layers 1); w passes
    // a probe traces whose last trace had a path through a moving reach (--dependent-passes; such a probe also
    // traces every frame, not every other row).
    vec4 uFresh;
    vec4 uLayers;          // x 1: a different SurfaceKey alone opens a second layer, as before (--probe-layer-keys);
                           // y the most passes any probe traces this frame: the stride of a slot's rays
    // Stage 5a, the GPU prototype (--transport-gpu): the cooked patches -- their grid (lowest corner xyz, cell size w)
    // and its dims (xyz; w 1 when they are uploaded). A hit's indirect light is its nearest facing patch's.
    vec4 uCookedGrid;
    vec4 uCookedDims;
} u;

layout(set = 0, binding = 1) uniform sampler2D uSceneDepth;
layout(set = 0, binding = 2) uniform sampler2D uPrepassNormal;
layout(set = 0, binding = 3) uniform sampler2D uClipmapIrradiance;
layout(set = 0, binding = 4) uniform sampler2D uClipmapDepth;
layout(std430, set = 0, binding = 5) readonly buffer ClipmapState { uvec4 states[]; };
layout(set = 0, binding = 6) uniform samplerCube uSkyRadiance;
layout(set = 0, binding = 12) uniform sampler2D uCascadeShadowMaps[3];
layout(set = 0, binding = 20) uniform usampler2D uSurfaceKey;
layout(set = 0, binding = 21) uniform sampler2D uVelocity;
layout(set = 0, binding = 22) uniform sampler2D uMotion;
#include "shadow.glsl"

#include "screen_probe.glsl"
layout(std430, set = 0, binding = 7) readonly buffer ScreenProbeTilesPrevious { ScreenProbeTile previousTiles[]; };
layout(std430, set = 0, binding = 8) readonly buffer ScreenProbesPrevious { ScreenProbe previous[]; };
layout(std430, set = 0, binding = 9) writeonly buffer ScreenProbeTilesCurrent { ScreenProbeTile currentTiles[]; };
layout(std430, set = 0, binding = 10) writeonly buffer ScreenProbesCurrent { ScreenProbe current[]; };
// Why probes start afresh, summed over the run: for each probe that found no past, the first test its nearest
// candidate failed. [0] no candidate tile at all, [1] ownership (key), [4] support, [6] probes that kept a past.
// Displacement of those kept (diagnostic): [2] on a point that moved in the world, [3] moved further than its footprint, [7] their
// world displacement summed in millimetres. [5] traced probes with a path through a moving reach (stage 4f).
// [8] second-layer probes placed (stage 4f-iv).
layout(std430, set = 0, binding = 11) buffer ScreenProbeStats { uint stats[16]; };
// Between the stages: a slot's placement (place -> trace, integrate), and every ray's answer (trace -> integrate):
// rgb radiance, w 1 when its path crossed a moving reach. A slot's rays at (slot * passes-stride + pass) * RAYS + ray.
struct ScreenProbePlacement {
    vec4 position;   // xyz world, w view depth (0: not placed)
    vec4 normal;     // xyz geometric normal, w passes it traces this frame
    uvec4 info;      // x SurfaceKey, y placed pixel (x | y << 16), z 1 when it traces this frame, w unused
};
layout(std430, set = 0, binding = 23) buffer ScreenProbePlacements { ScreenProbePlacement placements[]; };
layout(std430, set = 0, binding = 24) buffer ScreenProbeRays { vec4 rays[]; };

#define BLIX_RAY_SET 0
#define BLIX_RAY_BINDING 13
#include "ray_query.glsl"

#define BLIX_CLIPMAP_IRRADIANCE(t) texelFetch(uClipmapIrradiance, t, 0)
#define BLIX_CLIPMAP_DEPTH(t) texelFetch(uClipmapDepth, t, 0)
#define BLIX_CLIPMAP_IRRADIANCE_FILTERED(p) textureLod(uClipmapIrradiance, (p) / vec2(textureSize(uClipmapIrradiance, 0)), 0.0)
#define BLIX_CLIPMAP_DEPTH_FILTERED(p) textureLod(uClipmapDepth, (p) / vec2(textureSize(uClipmapDepth, 0)), 0.0)
#define BLIX_CLIPMAP_STATE(s) states[s]
#include "probe_clipmap.glsl"

#ifdef SCREEN_PROBE_PLACE
// The second layer's candidates (one tile a workgroup): xyz world, w view depth when off layer 0's surface, else -1.
shared vec4 sCandidate[16];
shared vec3 sCandidateNormal[16];
shared uint sCandidateKey[16];
shared int sCandidateShare[16];
#endif

// A probe ray's direction: a Fibonacci spiral over the hemisphere (uniform in solid angle), the whole set rotated
// about n and its heights jittered within their bands, both afresh per probe, pass and frame -- stratified in
// elevation AND azimuth (random azimuth alone let rays bunch on one side). Trace and integrate both ask, so the
// direction is not stored. seed is the ray's own.
vec3 probeRayDirection(int probeIndex, int pass, uint ray, vec3 n, out uint seed) {
    seed = blix_pcg(uint(u.uFrame.x) * 2654435761u + uint(probeIndex) * 9781u + ray + uint(pass) * 0x632BE5ABu);
    uint setSeed = blix_pcg(uint(u.uFrame.x) * 2654435761u + uint(probeIndex) * 9781u + uint(pass) * 0x632BE5ABu);
    float rotation = float(blix_pcg(setSeed) & 0xFFFFu) / 65536.0;
    float jitter = float(blix_pcg(seed) & 0xFFFFu) / 65536.0;
    float z = (float(ray) + jitter) / float(RAYS);
    float r = sqrt(max(0.0, 1.0 - z * z));
    float phi = 2.0 * PI * fract(float(ray) * 0.618034 + rotation);
    vec3 t = normalize(abs(n.y) < 0.999 ? cross(vec3(0.0, 1.0, 0.0), n) : cross(vec3(1.0, 0.0, 0.0), n));
    vec3 b = cross(n, t);
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * z);
}

int raySlot(int probeIndex, int pass, uint ray) { return (probeIndex * int(u.uLayers.y) + pass) * RAYS + int(ray); }

// Stage 4f, dependency: whether the segment origin + dir * [0, tEnd] passes through the reach of geometry that moved
// this frame. A path that does may carry light the motion changed; one that does not cannot have (lighting is
// otherwise static). Necessary, not sufficient: crossing the box need not mean the answer changed.
bool crossesMovingReach(vec3 origin, vec3 dir, float tEnd) {
    if (u.uDynamicMin.w < 0.5) return false;
    vec3 safe = mix(dir, vec3(1e-8), lessThan(abs(dir), vec3(1e-8)));
    vec3 t0 = (u.uDynamicMin.xyz - origin) / safe;
    vec3 t1 = (u.uDynamicMax.xyz - origin) / safe;
    vec3 lo = min(t0, t1);
    vec3 hi = max(t0, t1);
    return max(max(lo.x, lo.y), max(lo.z, 0.0)) <= min(min(hi.x, hi.y), min(hi.z, tEnd));
}

BlixClipmap clipmap() {
    BlixClipmap c;
    c.dims = ivec3(u.uDims.xyz);
    c.baseSpacing = u.uDims.w;
    c.blendProbes = u.uParams.x;
    c.origin[0] = ivec3(u.uOrigin0.xyz);
    c.origin[1] = ivec3(u.uOrigin1.xyz);
    c.origin[2] = ivec3(u.uOrigin2.xyz);
    c.origin[3] = ivec3(u.uOrigin3.xyz);
    return c;
}

// A pixel's surface: world position, view depth, geometric normal and identity. False where nothing was drawn.
bool surfaceAt(ivec2 pixel, out vec3 world, out float viewDepth, out vec3 n, out uint identity) {
    ivec2 size = ivec2(u.uTarget.xy);
    pixel = clamp(pixel, ivec2(0), size - 1);
    float raw = texelFetch(uSceneDepth, pixel, 0).r;
    vec4 ns = texelFetch(uPrepassNormal, pixel, 0);
    world = vec3(0.0); viewDepth = 0.0; n = vec3(0.0, 1.0, 0.0); identity = 0u;
    if (raw >= 1.0 - 1e-6 || dot(ns.xyz, ns.xyz) < 1e-6) return false;
    vec2 uv = (vec2(pixel) + 0.5) / vec2(size);
    vec4 view = u.uInvProjection * vec4(uv * 2.0 - 1.0, raw, 1.0);
    view.xyz /= view.w;
    world = (u.uInvView * vec4(view.xyz, 1.0)).xyz;
    viewDepth = abs(view.z);
    n = normalize(ns.xyz);
    identity = texelFetch(uSurfaceKey, pixel, 0).r;
    return true;
}

#ifdef SCREEN_PROBE_PLACE
// ---- place: one workgroup a tile, lanes 0-31 its first slot, 32-63 its second ------------------------------------
layout(local_size_x = 64) in;
void main() {
    uint lane = gl_LocalInvocationIndex;
    uint ray = lane % RAYS;
    // Two probe slots a tile (stage 4f-iv): slot = tile x 2 + layer. Layer 0 is the tile's surface; layer 1 a second
    // surface in the same tile, where one is there. A tile's header counts the layers placed, from slot tile x 2.
    int probeIndex = int(gl_WorkGroupID.x) * PROBES_PER_GROUP + int(lane / RAYS);
    int tileIndex = probeIndex / SCREEN_PROBE_MAX_PER_TILE;
    int layer = probeIndex % SCREEN_PROBE_MAX_PER_TILE;
    ivec2 tiles = ivec2(u.uTarget.zw);
    bool inRange = tileIndex < tiles.x * tiles.y;
    ivec2 tile = ivec2(tileIndex % max(tiles.x, 1), tileIndex / max(tiles.x, 1));

    vec3 world = vec3(0.0), n = vec3(0.0, 1.0, 0.0);
    float viewDepth = 0.0;
    uint identity = 0u;
    ivec2 placedPixel = ivec2(0);
    bool placed = false;
    ivec2 corner = tile * SCREEN_PROBE_TILE;
    if (inRange) {
        placedPixel = corner + SCREEN_PROBE_TILE / 2;
        placed = surfaceAt(placedPixel, world, viewDepth, n, identity);
        const ivec2 inner[4] = ivec2[4](ivec2(SCREEN_PROBE_TILE / 4), ivec2(3 * SCREEN_PROBE_TILE / 4, SCREEN_PROBE_TILE / 4), ivec2(SCREEN_PROBE_TILE / 4, 3 * SCREEN_PROBE_TILE / 4), ivec2(3 * SCREEN_PROBE_TILE / 4));
        for (int i = 0; i < 4 && !placed; ++i)
        {
            placedPixel = corner + inner[i];
            placed = surfaceAt(placedPixel, world, viewDepth, n, identity);
        }
    }

    // The second layer: of a 4 x 4 grid of points in the tile, those off layer 0's plane by more than the filter's
    // 2% of the depth -- a depth discontinuity, such as the recess behind a curtain whose tile probe sits on the
    // curtain -- and the probe goes on the one most of them share. A different SurfaceKey on the same plane (a floor
    // seam) does not count unless --probe-layer-keys: keys change across ~64% of tiles, and the second layer cost
    // ~10 of the fused pass's ~36 ms. Candidates in shared memory, one per lane of the second probe.
    bool secondLayer = inRange && layer == 1 && placed && u.uFresh.z < 0.5;
    if (layer == 1 && ray < 16u) {
        int i = int(ray);
        vec3 cw = vec3(0.0), cn = vec3(0.0, 1.0, 0.0);
        float cd = 0.0;
        uint ck = 0u;
        bool on = secondLayer && surfaceAt(corner + ivec2(4 + 8 * (i % 4), 4 + 8 * (i / 4)), cw, cd, cn, ck);
        bool off = abs(dot(n, cw - world)) > 0.02 * viewDepth;
        bool other = on && (off || (u.uLayers.x > 0.5 && ck != identity));
        sCandidate[i] = vec4(cw, other ? cd : -1.0);
        sCandidateNormal[i] = cn;
        sCandidateKey[i] = ck;
    }
    barrier();
    if (layer == 1 && ray < 16u) {
        int i = int(ray);
        int share = 0;
        if (sCandidate[i].w > 0.0)
            for (int j = 0; j < 16; ++j)
                if (sCandidate[j].w > 0.0 && sCandidateKey[j] == sCandidateKey[i]
                    && abs(dot(sCandidateNormal[i], sCandidate[j].xyz - sCandidate[i].xyz)) <= 0.02 * sCandidate[i].w) share++;
        sCandidateShare[i] = share;
    }
    barrier();
    if (layer == 1) {
        int bestCandidate = -1;
        int bestShare = 0;
        for (int i = 0; i < 16; ++i)
            if (sCandidateShare[i] > bestShare) { bestShare = sCandidateShare[i]; bestCandidate = i; }
        placed = secondLayer && bestCandidate >= 0;
        if (placed) {
            world = sCandidate[bestCandidate].xyz;
            n = sCandidateNormal[bestCandidate];
            viewDepth = sCandidate[bestCandidate].w;
            identity = sCandidateKey[bestCandidate];
            placedPixel = corner + ivec2(4 + 8 * (bestCandidate % 4), 4 + 8 * (bestCandidate / 4));
        }
    }
    if (ray != 0u || !inRange) return;

    // How many passes it traces, and whether its row traces at all this frame. Half the tile ROWS trace each frame,
    // alternating (uParams.w bit 16 turns it off). Young probes trace every frame: whether this tile's probe is
    // young is known only after its history search, so last frame's probe in this same tile is the guide.
    int passes = u.uParams.y > 0.5 ? 1 : int(u.uFresh.x);
    int ablate = int(u.uParams.w + 0.5);
    bool traceRow = (ablate & 16) != 0 || ((tile.y + int(u.uFrame.x)) & 1) == 0;
    if (u.uParams.y > 0.5) {
        ScreenProbeTile here = previousTiles[tileIndex];
        bool guided = uint(layer) < here.y;
        if (!guided || previous[here.x + uint(layer)].normal.w < 32.0) traceRow = true;
        // A fresh probe -- where the camera or a moving edge just uncovered a surface -- gets more rays of its own
        // rather than borrowing from the clipmap and its neighbours (both biased exactly at a lighting edge).
        if (!guided || previous[here.x + uint(layer)].normal.w < u.uFresh.y) passes = int(u.uFresh.x);
        // Where light is changing, spend rays there: a probe whose last trace crossed a moving reach traces every
        // frame (half-rate rows doubled its dynamic part's lag) and with --dependent-passes passes.
        if (guided && previous[here.x + uint(layer)].identity.z > 0u && u.uDynamicMin.w > 0.5) {
            traceRow = true;
            passes = max(passes, int(u.uFresh.w));
        }
    }
    ScreenProbePlacement p;
    p.position = vec4(world, placed ? viewDepth : 0.0);
    p.normal = vec4(n, float(passes));
    p.info = uvec4(identity, uint(placedPixel.x) | (uint(placedPixel.y) << 16), traceRow && placed ? 1u : 0u, 0u);
    placements[probeIndex] = p;
}
#endif

#ifdef SCREEN_PROBE_TRACE
// Cooked light transport (stage 5a): patches (position, normal, the indirect irradiance their solve left them -- sky,
// bounce, and the first bounce of direct sun through sunlets), found through a grid of cells (CSR: each cell's first
// entry and the patch ids). A hit takes its OWN direct sun from the cascades and the rest from its nearest patch
// facing the same way -- what the CPU spike measured at 5.6% on Sponza's hall (the final gather, exact sun at hits).
layout(std430, set = 0, binding = 25) readonly buffer CookedPatches { vec4 cookedPatch[]; };   // 3 a patch: pos, normal, indirect
layout(std430, set = 0, binding = 26) readonly buffer CookedCells { uint cookedCellStart[]; };
layout(std430, set = 0, binding = 27) readonly buffer CookedIds { uint cookedId[]; };

vec3 cookedIndirect(vec3 p, vec3 n, out bool found) {
    found = false;
    if (u.uCookedDims.w < 0.5) return vec3(0.0);
    ivec3 dims = ivec3(u.uCookedDims.xyz);
    ivec3 c = ivec3(floor((p - u.uCookedGrid.xyz) / u.uCookedGrid.w));
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

// ---- trace: one thread a ray, for every pass its probe traces ----------------------------------------------------
// A hit is lit by the sun (the shadow cascades where they cover it, a shadow ray beyond) and by the clipmap's
// irradiance at the hit, re-radiated in its albedo; an escape brings the disc-free sky. Short rays (--probe-ray-
// length) take the clipmap's light at their end instead. Each ray also says whether its path crossed a moving reach.
layout(local_size_x = 64) in;
void main() {
    int probeIndex = int(gl_GlobalInvocationID.x) / RAYS;
    uint ray = gl_GlobalInvocationID.x % uint(RAYS);
    ivec2 tiles = ivec2(u.uTarget.zw);
    if (probeIndex >= tiles.x * tiles.y * SCREEN_PROBE_MAX_PER_TILE) return;
    ScreenProbePlacement p = placements[probeIndex];
    int ablate = int(u.uParams.w + 0.5);
    if (p.position.w <= 0.0 || p.info.z == 0u || (ablate & 4) != 0) return;
    vec3 world = p.position.xyz;
    vec3 n = p.normal.xyz;
    float viewDepth = p.position.w;
    int passes = int(p.normal.w);
    for (int pass = 0; pass < passes; ++pass) {
        uint seed;
        vec3 dir = probeRayDirection(probeIndex, pass, ray, n, seed);
        vec3 origin = world + n * (0.01 + 0.001 * viewDepth);
        vec3 radiance = vec3(0.0);
        bool dependent = false;
        BlixRayHit hit;
        float rayLength = u.uParams2.w > 0.0 ? u.uParams2.w : uintBitsToFloat(0x7F800000u);
        if (blix_traceClosest(origin, dir, 0.0, rayLength, seed, hit)) {
            vec3 hn = dot(hit.normal, dir) > 0.0 ? -hit.normal : hit.normal;
            vec3 hitPos = origin + dir * hit.t;
            vec3 toSun = -normalize(u.uSunDirection.xyz);
            float ndotl = max(dot(hn, toSun), 0.0);
            // The gather segment, and the hit's way to the sun (the mover changes whether a hit sees it).
            dependent = crossesMovingReach(origin, dir, hit.t)
                || (ndotl > 0.0 && crossesMovingReach(hitPos + hn * 0.01, toSun, 1e30));
            float sun = 0.0;
            if (ndotl > 0.0 && (ablate & 1) == 0) {
                int cascade = -1;
                float lit = 1.0;
                if ((ablate & 8) == 0) {
                    lit = blix_sun_shadow_cascaded_hard(
                        uCascadeShadowMaps[0], uCascadeShadowMaps[1], uCascadeShadowMaps[2],
                        u.uCascadeVP[0], u.uCascadeVP[1], u.uCascadeVP[2], hitPos + hn * 0.01, ndotl, cascade);
                }
                if (cascade < 0) {
                    lit = blix_traceAny(hitPos + hn * 0.01, toSun, 0.0, uintBitsToFloat(0x7F800000u), blix_pcg(seed ^ 0x5bd1e995u)) ? 0.0 : 1.0;
                }
                sun = ndotl * lit;
            }
            bool known = false;
            vec4 field = vec4(0.0);
            bool cooked = false;
            vec3 cookedLight = cookedIndirect(hitPos, hn, cooked);
            if (cooked) { field = vec4(cookedLight, 1.0); known = true; }
            else if ((ablate & 2) == 0) field = blix_clipmapSample(clipmap(), hitPos, hn, known);
            radiance = hit.albedo * (u.uSunIrradiance.rgb * sun + (known ? field.rgb : vec3(0.0))) / PI;
        } else if (u.uParams2.w > 0.0) {
            bool known;
            vec4 far = blix_clipmapSample(clipmap(), origin + dir * rayLength, dir, known);
            radiance = known ? far.rgb / PI : textureLod(uSkyRadiance, dir, u.uFrame.w).rgb * u.uFrame.z;
            dependent = crossesMovingReach(origin, dir, rayLength);
        } else {
            radiance = textureLod(uSkyRadiance, dir, u.uFrame.w).rgb * u.uFrame.z;
            dependent = crossesMovingReach(origin, dir, 1e30);
        }
        rays[raySlot(probeIndex, pass, ray)] = vec4(radiance, dependent ? 1.0 : 0.0);
    }
}
#endif

#ifdef SCREEN_PROBE_INTEGRATE
// ---- integrate: one thread a probe slot ---------------------------------------------------------------------------
layout(local_size_x = 64) in;
void main() {
    int probeIndex = int(gl_GlobalInvocationID.x);
    ivec2 tiles = ivec2(u.uTarget.zw);
    if (probeIndex >= tiles.x * tiles.y * SCREEN_PROBE_MAX_PER_TILE) return;
    int tileIndex = probeIndex / SCREEN_PROBE_MAX_PER_TILE;
    int layer = probeIndex % SCREEN_PROBE_MAX_PER_TILE;
    ScreenProbePlacement p = placements[probeIndex];
    bool placed = p.position.w > 0.0;
    vec3 world = p.position.xyz;
    float viewDepth = p.position.w;
    vec3 n = p.normal.xyz;
    int passes = int(p.normal.w);
    uint identity = p.info.x;
    ivec2 placedPixel = ivec2(int(p.info.y & 0xFFFFu), int(p.info.y >> 16));
    bool traceRow = p.info.z != 0u;
    int ablate = int(u.uParams.w + 0.5);

    // Layer 1 is placed only where layer 0 is, so the tile's placed probes are its first slots.
    if (layer == 0) currentTiles[tileIndex] = ScreenProbeTile(uint(tileIndex * SCREEN_PROBE_MAX_PER_TILE),
        placed ? 1u + (placements[probeIndex + 1].position.w > 0.0 ? 1u : 0u) : 0u, 0u, 0u);
    if (layer == 1 && placed) atomicAdd(stats[8], 1u);
    ScreenProbe probe;
    probe.position = vec4(world, viewDepth);
    probe.normal = vec4(n, 0.0);
    probe.identity = uvec4(identity, 0u, 0u, 0u);
    for (int i = 0; i < 9; ++i) { probe.radiance[i] = vec4(0.0); probe.dynamicRadiance[i] = vec4(0.0); }
    if (!placed) { current[probeIndex] = probe; return; }

    // This frame's estimate: each ray's radiance times the basis, over the uniform hemisphere's density. Split by
    // dependency (stage 4f): each part estimates the integral over its own directions (a still probe's directions
    // through a fixed reach are a fixed set), so the parts add up to the whole and each keeps its own history. The
    // control arm folds both into the static part.
    bool split = u.uParams2.z < 0.5;
    vec3 estimate[9];
    vec3 estimateDynamic[9];
    for (int i = 0; i < 9; ++i) { estimate[i] = vec3(0.0); estimateDynamic[i] = vec3(0.0); }
    uint dependentRays = 0u;
    if (traceRow && (ablate & 4) == 0) {
        for (int pass = 0; pass < passes; ++pass)
        for (uint k = 0u; k < uint(RAYS); ++k) {
            uint seed;
            vec3 dir = probeRayDirection(probeIndex, pass, k, n, seed);
            vec4 r = rays[raySlot(probeIndex, pass, k)];
            float y[9];
            screenProbeBasis(dir, y);
            bool d = r.w > 0.5;
            if (d) dependentRays++;
            for (int i = 0; i < 9; ++i) {
                if (d && split) estimateDynamic[i] += r.rgb * y[i];
                else estimate[i] += r.rgb * y[i];
            }
        }
    }
    if (dependentRays > 0u) atomicAdd(stats[5], 1u);
    float scale = 2.0 * PI / float(RAYS * passes);
    // Its past: the same surface, by every test in the header.
    float history = 0.0;
    int best = -1;
    int nearestReasonOut = 0;
    float displacement = 0.0, footprint = 0.0;
    if (u.uParams.y > 0.5) {
        // Where this surface point was last frame: the pre-pass's velocity at the probe's own pixel (camera and object
        // motion both; a camera-only reprojection cannot follow a moving object).
        ivec2 clampedPixel = clamp(placedPixel, ivec2(0), ivec2(u.uTarget.xy) - 1);
        {
            vec2 uv = (vec2(clampedPixel) + 0.5) / u.uTarget.xy - texelFetch(uVelocity, clampedPixel, 0).xy;
            displacement = length(texelFetch(uMotion, clampedPixel, 0).xyz);
            vec3 then = u.uParams2.y > 0.5 ? world : world - texelFetch(uMotion, clampedPixel, 0).xyz;
            ivec2 pastTile = ivec2(floor(uv * u.uTarget.xy / float(SCREEN_PROBE_TILE)));
            footprint = float(SCREEN_PROBE_TILE) * u.uParams.z * viewDepth;
            float bestScore = 1e30;
            float nearest = 1e30;
            int nearestReason = 0;
            for (int k = 0; k < 9; ++k) {
                ivec2 t = pastTile + ivec2(k % 3 - 1, k / 3 - 1);
                if (any(lessThan(t, ivec2(0))) || any(greaterThanEqual(t, tiles))) continue;
                ScreenProbeTile header = previousTiles[t.y * tiles.x + t.x];
                for (uint j = 0u; j < min(header.y, uint(SCREEN_PROBE_MAX_PER_TILE)); ++j) {
                    int index = int(header.x + j);
                    vec4 pp = previous[index].position;
                    vec3 offset = then - pp.xyz;
                    float plane = abs(dot(n, offset));
                    float lateral = length(offset - n * dot(n, offset));
                    int reason = previous[index].identity.x != identity ? 1   // ownership
                        : lateral > footprint ? 4 : 0;                          // support
                    if (length(offset) < nearest) { nearest = length(offset); nearestReason = reason; nearestReasonOut = reason; }
                    if (reason != 0) continue;
                    float score = plane * 4.0 + lateral;
                    if (score >= bestScore) continue;
                    bestScore = score;
                    best = index;
                }
            }
        }
    }
    if (best >= 0) history = previous[best].normal.w;
    atomicAdd(stats[best >= 0 ? 6 : nearestReasonOut], 1u);
    if (best >= 0 && displacement > 0.0) {
        atomicAdd(stats[2], 1u);
        if (displacement > footprint) atomicAdd(stats[3], 1u);
        atomicAdd(stats[7], uint(displacement * 1000.0 + 0.5));
    }
    vec4 prior[9];
    for (int i = 0; i < 9; ++i) prior[i] = vec4(0.0);
    float priorFrames = 0.0;
    if (best < 0 && u.uParams2.x > 0.0) {
        bool known;
        vec4 field = blix_clipmapSample(clipmap(), world, n, known);
        if (known) {
            vec3 L = field.rgb / PI;
            prior[0] = vec4(L * (2.0 * PI * 0.282095), 0.0);
            vec3 k = L * (0.488603 * PI);
            prior[1] = vec4(k * n.y, 0.0);
            prior[2] = vec4(k * n.z, 0.0);
            prior[3] = vec4(k * n.x, 0.0);
            priorFrames = u.uParams2.x;
        }
    }
    float dynamicPast = best >= 0 ? float(previous[best].identity.y) : 0.0;
    if (!traceRow) {
        // No rays this frame: carry the past as it was, or hold nothing.
        for (int i = 0; i < 9; ++i) {
            probe.radiance[i] = best >= 0 ? previous[best].radiance[i] : prior[i];
            probe.dynamicRadiance[i] = best >= 0 ? previous[best].dynamicRadiance[i] : vec4(0.0);
        }
        probe.normal.w = best >= 0 ? history : priorFrames;
        probe.identity.y = uint(dynamicPast);
    probe.identity.z = best >= 0 ? previous[best].identity.z : 0u;
        current[probeIndex] = probe;
        return;
    }
    float past = best >= 0 ? history : priorFrames;
    // The control arm: a probe any of whose paths crossed the moving reach starts afresh, whole.
    if (!split && dependentRays > 0u) past = 0.0;
    // Counts are in frames' worth of rays: a fresh probe's passes count as that many frames.
    float frames = float(passes);
    float dynamicCount = min(dynamicPast + frames, max(u.uDynamicMax.w, frames));
    float count = min(past + frames, max(u.uFrame.y, frames));
    float alpha = min(frames / count, 1.0);
    for (int i = 0; i < 9; ++i) {
        vec3 fresh = estimate[i] * scale;
        vec3 from = best >= 0 ? previous[best].radiance[i].rgb : prior[i].rgb;
        probe.radiance[i] = vec4(past > 0.0 ? mix(from, fresh, alpha) : fresh, 0.0);
        vec3 freshDynamic = estimateDynamic[i] * scale;
        vec3 fromDynamic = best >= 0 ? previous[best].dynamicRadiance[i].rgb : vec3(0.0);
        probe.dynamicRadiance[i] = vec4(dynamicPast > 0.0 ? mix(fromDynamic, freshDynamic, min(frames / dynamicCount, 1.0)) : freshDynamic, 0.0);
    }
    probe.normal.w = count;
    probe.identity.y = uint(dynamicCount);
    probe.identity.z = dependentRays;
    current[probeIndex] = probe;
}
#endif
