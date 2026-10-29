#version 450

// --rtgi (S1, SponzaLoop.Rtgi.cs): the cache read at each half-res pixel, into the incident targets the upsample and the
// lit pass read (in place of incident-light-half): rgb the indirect irradiance (sky and bounce, no direct sun), a the
// sky visibility. Half-res pixel h stands for full-res pixel 2h + uHalf.yz (SponzaLoop.HalfLighting.cs). The eight
// cells around the point at its level (the same side), trilinear, those holding nothing left out; where none do, the
// level two coarser; where neither, nothing (black, the sky seen).

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;
layout(location = 1) out vec4 outIncidentGradient;
// The half-res guide (half_guide.glsl): this pixel's world position and normal, for the blur and the upsample.
layout(location = 2) out uvec2 outGuide;
// Reuse (uReuse): last frame's blurred incident for a pixel taken from history, -1 for one read fresh (rtgi_blur.frag).
layout(location = 3) out vec4 outReuse;

layout(set = 0, binding = 0) uniform RtgiReadout {
    mat4 uInvViewProj;   // inverse of the view-projection the pre-pass drew with
    vec4 uParams;        // rtgiParams (rtgi_cache.glsl)
    vec4 uCamera;        // xyz position
    vec4 uHalf;          // yz the full-res pixel of its 2x2
    vec4 uNearField;     // x the near field's radius (rtgi_cache.glsl's RTGI_NEAR); y 1: the gradient written; z 1: the
                         // tangent plane's four cells (readLevel), 0: the eight around the point
    vec4 uContact;       // x 1: contact detail on; yz the ratio's clamp; w the RTGI panel's view (0: the light)
    vec4 uUnsure;        // x the done threshold (relative error); y the uncertainty map's row (half-res pixels); z its size;
                         // w a full-res pixel's footprint per metre of distance
    vec4 uBlurAt;        // x the blur's radius at most (full-res pixels; 0: none); y the share of the hit distance it may reach;
                         // z the cache's frame number (the keep-alive below)
    mat4 uPrevViewProj;  // last frame's view-projection (the history's)
    vec4 uReuse;         // x 1: history to reuse; y this frame's fresh blocks (0-3); z how far a history pixel may sit from
                         // the point, per metre of distance; w the history's full-res pixel of its 2x2 (x + 2 y)
    vec4 uPrevCamera;    // xyz last frame's camera (the history guide's positions are relative to it)
    vec4 uHybrid;        // --rtgi-hybrid: x 1 on; y the training's stamp this frame; z the samples' search radius (half-res
                         // pixels); w the history's frames at most
    vec4 uHybrid2;       // x the cell's weight against the pixel's own samples (a prior, in samples; 0 none); y 1: the near field
                         // read from the world's near cells (readNear), not the pixels' history on the screen
    vec4 uPool;          // --rtgi-pool: x the coarser levels the light may pool up to (0: off); y the agreement it needs (standard
                         // errors)
    vec4 uVolume;        // --rtgi-volume: rtgi_volume.glsl's VOL_PARAMS (w 0: off)
    vec4 uVolumeGeom;    // rtgi_volume.glsl's VOL_GEOM (the cells sized by the geometry)
    vec4 uNearCook;      // --rtgi-near-cook: rtgi_nearcook.glsl's NEAR_PARAMS (w 0: off)
    vec4 uVolume2;       // x the volume's light's relative error, as a cell's (its weight against the cells'); y the visibility
                         // requests' room; z 1: neighbours left out where their traced ray was blocked; w 1: the probes read
} u;

layout(set = 0, binding = 1) uniform sampler2D uSceneDepth;
layout(set = 0, binding = 2) uniform sampler2D uPrepassNormal;
layout(std430, set = 0, binding = 3) readonly buffer RtgiKeys { uint rtgiKeys[]; };
layout(std430, set = 0, binding = 4) readonly buffer RtgiResolved { vec4 rtgiResolved[]; };
// The light blended with the cell's neighbours (rtgi_filter.comp): indirect rgb, sky visibility.
layout(std430, set = 0, binding = 5) readonly buffer RtgiFiltered { vec4 rtgiFiltered[]; };
// The irradiance vector (rtgi_resolve.comp): its part along the surface is how the irradiance turns with the normal.
layout(std430, set = 0, binding = 6) readonly buffer RtgiDirection { vec4 rtgiDirection[]; };
// The far light's share of a cell's light (the third moment vec4's w, rtgi_resolve.comp).
layout(std430, set = 0, binding = 7) readonly buffer RtgiMoments { vec4 rtgiMoments[]; };
layout(std430, set = 0, binding = 8) readonly buffer RtgiConfidence { vec4 rtgiConfidence[]; };
// The visible share done: one pixel in 16 counted (x), and of those the ones whose cell is done (y); SponzaLoop.Rtgi.cs
// reads and zeroes them at the profile window.
// z, w: the sampled pixels' cell lookups, and those that found no cell (a miss walks all RTGI_PROBES slots); [4], [5]
// the sampled pixels with a blur radius, and those under a half-res pixel (the blur reads nothing for them).
layout(std430, set = 0, binding = 9) buffer RtgiDoneCount { uint doneCount[10]; };
// Per half-res pixel: how unsure its cell is, for the next frame's ray placement (rtgi_rays.comp) -- read there for
// a texel's price, where each candidate found its cell in the hash itself: 5.8 ms a frame at 4x the rays (Crytek
// Sponza, 2026-10-26). The blend's relative error, at least the done threshold; a done cell an eighth of that; a cell
// holding under 4 samples, or none, 1; the sky -1.
layout(std430, set = 0, binding = 10) writeonly buffer RtgiUnsure { float rtgiUnsure[]; };
layout(std430, set = 0, binding = 11) readonly buffer RtgiHitInv { float rtgiHitInv[]; };
// Per half-res pixel, the blur's radius (half-res pixels; rtgi_blur.frag): sized here, where the pixel's cell is found
// already -- the blur found it in the hash again, 4.6 ms a frame at Crytek Sponza's 4x the rays (2026-10-26).
layout(std430, set = 0, binding = 12) writeonly buffer RtgiBlurRadius { float rtgiBlurRadius[]; };
// Each cell's light, direction, hit distance, how sure and how much it holds, in one 32-byte line (rtgi_cache.glsl): read
// in place of the five buffers above, which the panel's views alone still read.
layout(std430, set = 0, binding = 13) readonly buffer RtgiRecord { uvec4 rtgiRecord[]; };
// The cells this view reads kept from eviction (rtgi_evict.comp empties those no training stamped for --rtgi-max-age
// frames): a settled cell the placement no longer aims rays at was read here every frame, emptied at 1800 frames, and the
// screen's cells came back unsure together -- placed on, retrained from nothing, a wave of young cells through the resolve
// and the blend (a still Crytek Sponza view, frames 16.7 -> 25 ms for ~10 s every 30 s, 2026-10-27). A pixel in 64 a frame,
// in turn, each of its cells stamped a frame back in its accumulator's spare word 14 (rtgi_train.comp's keepAlive) where
// no training stamped it for 64 frames.
layout(std430, set = 0, binding = 14) buffer RtgiAccum { uint rtgiAccum[]; };
// Last frame's readout, kept by the blur (rtgi_blur.frag): raw, gradient, blurred, guide.
layout(set = 0, binding = 15) uniform sampler2D uHistRaw;
layout(set = 0, binding = 16) uniform sampler2D uHistGradient;
layout(set = 0, binding = 17) uniform sampler2D uHistBlurred;
layout(set = 0, binding = 18) uniform usampler2D uHistGuide;
// --rtgi-hybrid: the training's samples by half-res pixel (rtgi_train.comp: three a pixel), and each pixel's near light and
// escape share kept frame to frame (two a pixel: (rgb the near light, w the share of rays leaving past the contact radius),
// (x the samples held)), last frame's read, this frame's written.
layout(std430, set = 0, binding = 19) readonly buffer RtgiNearSamples { vec4 nearSamples[]; };
layout(std430, set = 0, binding = 20) readonly buffer RtgiNearPrev { vec4 nearPrev[]; };
layout(std430, set = 0, binding = 21) writeonly buffer RtgiNearCur { vec4 nearCur[]; };
// --rtgi-volume (rtgi_volume.glsl): the world's probes, what young and unsure cells lean on.
layout(std430, set = 0, binding = 22) readonly buffer RtgiVolumeTable { uvec2 volTable[]; };
layout(std430, set = 0, binding = 23) readonly buffer RtgiVolumeProbes { float volProbes[]; };
#define VOL_PARAMS u.uVolume
#define VOL_GEOM u.uVolumeGeom
#define VOL_TABLE(i) volTable[i]
layout(std430, set = 0, binding = 24) readonly buffer RtgiVolumeDepth { uint volDepth[]; };
// Per cache slot, which probes and neighbours its point sees (rtgi_volume_vis.comp: the key it was traced for, and the mask),
// and the requests for cells not traced yet.
layout(std430, set = 0, binding = 25) buffer RtgiVolumeCellVis { uvec2 cellVis[]; };
layout(std430, set = 0, binding = 26) buffer RtgiVolumeVisRequests { uint visCount; uint visPad[3]; vec4 visRequests[]; };
// --rtgi-near-cook (rtgi_nearcook.glsl): the cooked near cells, their table and light.
layout(std430, set = 0, binding = 27) readonly buffer RtgiNearTable { uvec2 nearTable[]; };
layout(std430, set = 0, binding = 28) readonly buffer RtgiNearLight { vec4 nearLight[]; };
layout(std430, set = 0, binding = 29) readonly buffer RtgiNearPoints { vec4 nearPoints[]; };
#define NEAR_POINTS(i) nearPoints[i]
#define NEAR_PARAMS u.uNearCook
#define NEAR_TABLE(i) nearTable[i]
// The cooked cells' regions by level and their links (rtgi_nearcook_cook.comp; rtgi_cache.glsl's rtgiRegion).
layout(std430, set = 0, binding = 30) readonly buffer RtgiNearRegions { uint nearRegions[]; };
#define NEAR_REGIONS(i) nearRegions[i]
#define RTGI_REGION_OF 1
layout(std430, set = 0, binding = 31) readonly buffer RtgiNearAdj { uint nearAdj[]; };
#define NEAR_ADJ(i) nearAdj[i]
#include "rtgi_nearcook.glsl"
#define VOL_PROBES(i) volProbes[i]
#define VOL_DEPTH(i) volDepth[i]
#include "rtgi_volume.glsl"
#define RTGI_GEOM_SCALE(p) volCellScale(p)
#define RTGI_FAR_GROW u.uVolumeGeom.z


// Blue (0) to green to red (1).
vec3 heat(float t) {
    t = clamp(t, 0.0, 1.0);
    return clamp(vec3(1.5 - abs(4.0 * t - 3.0), 1.5 - abs(4.0 * t - 2.0), 1.5 - abs(4.0 * t - 1.0)), 0.0, 1.0);
}

#define RTGI_KEYS(i) rtgiKeys[i]
#define RTGI_NEAR u.uNearField.x
#include "rtgi_cache.glsl"
#include "half_guide.glsl"

bool keeper = false;

// The pixel's cell's traced visibility (rtgi_volume_vis.comp), fetched once a pixel (cellVisFetch): visKnown when it is
// traced, visWord its mask, visLevel0 the cache level it was traced at. Not traced yet: the cell asked for, once (its key
// claimed in its visibility word).
bool visKnown = false;
uint visWord = 0u, visLevel0 = 0u;
void cellVisFetch(vec3 p, vec3 n, uint side, uint level) {
    if (u.uVolume.w < 0.5) return;
    uint sc = rtgiFind(rtgiCellHash(ivec3(floor((p - RTGI_ORIGIN) / rtgiSize(level, u.uParams))), level, side, u.uParams), u.uParams);
    if (sc == 0xFFFFFFFFu) return;
    uint key = rtgiKeys[sc];
    uvec2 v = cellVis[sc];
    if (v.x != key) {
        if (atomicCompSwap(cellVis[sc].x, v.x, key) != v.x) return;
        cellVis[sc].y = 0u;
        uint r = atomicAdd(visCount, 1u);
        if (r >= uint(u.uVolume2.y)) { cellVis[sc].x = 0u; return; }
        uint lv = min(volVisLevel(p), 15u);
        visRequests[2u * r] = vec4(p, uintBitsToFloat(sc));
        visRequests[2u * r + 1u] = vec4(n, uintBitsToFloat(lv | (level << 8) | (side << 16)));
        return;
    }
    if ((v.y & 0x1000u) == 0u) return;
    visKnown = true;
    visWord = v.y;
    visLevel0 = level;
}

// Whether the readout may blend cell cc (at a level) into point p's light: its neighbour's ray reached it (cellVisFetch),
// or it was not traced.
bool neighbourSeen(vec3 p, ivec3 cc, uint level, uint a0, uint a1, float size) {
    if (u.uVolume2.z < 0.5 || !visKnown || level < visLevel0 || level - visLevel0 > 2u) return true;
    uint base = 16u + (level - visLevel0) * 4u;
    int d0 = cc[a0] - int(floor((p[a0] - RTGI_ORIGIN[a0]) / size)), d1 = cc[a1] - int(floor((p[a1] - RTGI_ORIGIN[a1]) / size));
    if (d0 > 0 && (visWord & (1u << base)) == 0u) return false;
    if (d0 < 0 && (visWord & (1u << (base + 1u))) == 0u) return false;
    if (d1 > 0 && (visWord & (1u << (base + 2u))) == 0u) return false;
    if (d1 < 0 && (visWord & (1u << (base + 3u))) == 0u) return false;
    return true;
}

// --rtgi-volume: the volume's light at p facing n through the probes the pixel's cell was traced to see; none -- the cache
// alone -- until it has been.
vec4 volumeCell(vec3 p, vec3 n, out float sky) {
    sky = 0.0;
    if (!visKnown || u.uVolume2.w < 0.5) return vec4(0.0);
    uint lv = (visWord >> 8) & 15u;
    if (lv >= uint(VOL_PARAMS.y)) return vec4(0.0);
    vec4 e = volumeLevelMasked(p, n, lv, visWord & 0xFFu, sky);
    return e.w >= 0.05 ? vec4(e.rgb, 1.0) : vec4(0.0);
}

uint rtgiFindKept(uvec2 h, vec4 params) {
    uint s = rtgiFind(h, params);
    if (keeper && s != 0xFFFFFFFFu) {
        uint frame = uint(u.uBlurAt.z), seen = rtgiAccum[16u * s + 14u];
        if (((frame - seen) & 0xFFFFFFu) > 64u) atomicCompSwap(rtgiAccum[16u * s + 14u], seen, (seen & 0xFF000000u) | ((frame - 1u) & 0xFFFFFFu));
    }
    return s;
}

// Trilinear over the level's cells around p (their centres), w the weight found.
uint lookups = 0u, misses = 0u;
// Whether every cell the light was read from held a full fade's samples (32): the light the reuse below may keep.
bool settledRead = true;

// The last readLevel's relative error: its cells' (the blend's, rtgi_filter.comp), weighed as their light.
float readRel = 1.0;
vec4 readLevel(vec3 p, uint side, uint level, out float sky, out vec3 bvec, out float near, out float far) {
    float relSum = 0.0;
    float size = rtgiSize(level, u.uParams);
    vec3 f = (p - RTGI_ORIGIN) / size - 0.5;
    ivec3 c0 = ivec3(floor(f));
    vec3 t = f - vec3(c0);
    vec3 e = vec3(0.0);
    float w = 0.0;
    sky = 0.0;
    bvec = vec3(0.0);
    near = 0.0;
    far = 0.0;
    // The tangent plane's (uNearField.z): along the axis the side faces, the cell holding the point alone; across the
    // other two, bilinear. The eight around it reached in front of the surface and behind it -- air and the inside of the
    // wall, cells no ray trains -- and 37% of the lookups missed at Crytek Sponza (14% Cornell, 2026-10-27), each one a
    // walk of the probes and its memory.
    bool plane = u.uNearField.z > 0.5;
    uint axis = side >> 1u;
    uint a0 = (axis + 1u) % 3u, a1 = (axis + 2u) % 3u;
    int cAxis = int(floor((p[axis] - RTGI_ORIGIN[axis]) / size));
    for (int k = 0; k < (plane ? 4 : 8); k++) {
        ivec3 o = ivec3(k & 1, (k >> 1) & 1, k >> 2);
        if (plane) {
            o = ivec3(0);
            o[a0] = k & 1;
            o[a1] = k >> 1;
        }
        ivec3 cc = c0 + o;
        if (plane) cc[axis] = cAxis;
        if (plane && !neighbourSeen(p, cc, level, a0, a1, size)) continue;
        uint s = rtgiFindKept(rtgiCellHash(cc, level, side, u.uParams), u.uParams);
        lookups++;
        if (s == 0xFFFFFFFFu) { misses++; continue; }
        uvec4 a = rtgiRecord[2u * s], c = rtgiRecord[2u * s + 1u];
        float held = uintBitsToFloat(c.w);
        if (held < 0.5) continue;
        vec3 b = mix(1.0 - t, t, vec3(o));
        if (plane) b[axis] = 1.0;
        // Weighted by what it holds too, full at 32 samples: a new cell (a landing just requested, a level just blended in,
        // one refilled) fades in under its neighbours rather than showing its first draw -- odd lights that came and went.
        float wk = b.x * b.y * b.z * clamp(held / 32.0, 1.0 / 32.0, 1.0);
        if (held < 32.0) settledRead = false;
        vec2 rg = unpackHalf2x16(a.x), bs = unpackHalf2x16(a.y);
        vec2 dxy = unpackHalf2x16(c.x), dzn = unpackHalf2x16(c.y);
        e += vec3(rg, bs.x) * wk;
        sky += bs.y * wk;
        bvec += vec3(dxy, dzn.x) * wk;
        near += dzn.y * wk;
        far += unpackHalf2x16(c.z).x * wk;
        relSum += min(unpackHalf2x16(a.z).x, 4.0) * wk;
        w += wk;
    }
    if (w > 0.0) { e /= w; sky /= w; bvec /= w; near /= w; far /= w; }
    readRel = w > 0.0 ? relSum / w : 1.0;
    return vec4(e, w);
}

// The contact cells around p (rtgi_contact_train.comp: a quarter of the level's size, its level 16 up), trilinear by what
// each holds (full at 16): their near visibility, w the weight found.
vec2 readContact(vec3 p, uint side, uint level) {
    float size = 0.25 * rtgiSize(level, u.uParams);
    vec3 f = (p - RTGI_ORIGIN) / size - 0.5;
    ivec3 c0 = ivec3(floor(f));
    vec3 t = f - vec3(c0);
    float v = 0.0, w = 0.0;
    for (int k = 0; k < 8; k++) {
        ivec3 o = ivec3(k & 1, (k >> 1) & 1, k >> 2);
        uint s = rtgiFindKept(rtgiCellHash(c0 + o, level + 16u, side, u.uParams), u.uParams);
        if (s == 0xFFFFFFFFu) continue;
        float held = rtgiResolved[2u * s + 1u].y;
        if (held < 0.5) continue;
        vec3 b = mix(1.0 - t, t, vec3(o));
        float wk = b.x * b.y * b.z * clamp(held / 16.0, 1.0 / 16.0, 1.0);
        v += rtgiDirection[s].w * wk;
        w += wk;
    }
    return vec2(w > 0.0 ? v / w : 0.0, w);
}

// --rtgi-hybrid: the pixel's own near field. This frame's samples within uHybrid.z half-res pixels of where the point sat
// last frame (the samples' frame and the history's), each on its plane and facing, blended into its history there
// (validated by last frame's guide as the reuse is), at most uHybrid.w frames deep: (near light, escape share), count.
// --rtgi-hybrid's near cells (rtgi_near_shade.comp: a quarter of the cell's size, its level + 24), trilinear around p by what
// each holds (full at 16): rgb the near light per ray, w the share of rays that left past the contact radius; held the
// samples held, weighed the same way.
vec4 readNear(vec3 p, uint side, uint level, out float held) {
    float size = 0.25 * rtgiSize(level, u.uParams);
    vec3 f = (p - RTGI_ORIGIN) / size - 0.5;
    ivec3 c0 = ivec3(floor(f));
    vec3 t = f - vec3(c0);
    vec4 acc = vec4(0.0);
    float w = 0.0, hs = 0.0;
    for (int k = 0; k < 8; k++) {
        ivec3 o = ivec3(k & 1, (k >> 1) & 1, k >> 2);
        uint s = rtgiFindKept(rtgiCellHash(c0 + o, level + 24u, side, u.uParams), u.uParams);
        if (s == 0xFFFFFFFFu) continue;
        float hk = rtgiResolved[2u * s + 1u].y;
        if (hk < 0.5) continue;
        vec3 b = mix(1.0 - t, t, vec3(o));
        float wk = b.x * b.y * b.z * clamp(hk / 16.0, 1.0 / 16.0, 1.0);
        acc += vec4(rtgiResolved[2u * s].rgb, rtgiDirection[s].w) * wk;
        hs += hk * wk;
        w += wk;
    }
    held = w > 0.0 ? hs / w : 0.0;
    return w > 0.0 ? acc / w : vec4(0.0);
}

// --rtgi-near-cook: the cooked near cells around p on its surface (the tangent plane's four, bilinear, those gathered and in
// p's region; rtgi_nearcook.glsl): rgb their whole indirect irradiance (rtgi_nearcook_update.comp); found the weight found.
vec4 readNearCooked(vec3 p, uint side, out float found) {
    found = 0.0;
    if (NEAR_PARAMS.w < 0.5) return vec4(0.0);
    float size = NEAR_PARAMS.x;
    uint axis = side >> 1u, a0 = (axis + 1u) % 3u, a1 = (axis + 2u) % 3u;
    vec3 f = (p - RTGI_ORIGIN) / size - 0.5;
    ivec3 c0 = ivec3(floor(f));
    vec3 t = f - vec3(c0);
    int cAxis = int(floor((p[axis] - RTGI_ORIGIN[axis]) / size));
    vec4 acc = vec4(0.0);
    for (int k = 0; k < 4; k++) {
        ivec3 cc = c0;
        cc[a0] += k & 1;
        cc[a1] += k >> 1;
        cc[axis] = cAxis;
        uint i = nearFind(cc, side);
        if (i == 0xFFFFFFFFu || !nearLinked(rtgiRegion - 1u, i, side)) continue;
        vec4 nl = nearLight[i];
        if (nl.w < 0.5) continue;
        float w = ((k & 1) == 1 ? t[a0] : 1.0 - t[a0]) * ((k >> 1) == 1 ? t[a1] : 1.0 - t[a1]);
        acc += nl * w;
        found += w;
    }
    return found > 0.0 ? acc / found : vec4(0.0);
}

float hybridPrevRatio = -1.0;   // the pixel's ratio last frame (hybrid light over the cache's), -1 none
vec4 hybridNear(vec3 p, vec3 n, float dist, bool store, uint at, out float count) {
    ivec2 halfSize = textureSize(uHistGuide, 0);
    uint row = uint(u.uUnsure.y), size = uint(u.uUnsure.z);
    ivec2 c = ivec2(-1);
    vec4 pc = u.uPrevViewProj * vec4(p, 1.0);
    if (pc.w > 0.0) c = ivec2(floor((pc.xy / pc.w * 0.5 + 0.5) * vec2(textureSize(uSceneDepth, 0)) * 0.5));
    float tol = 0.01 * dist + 0.01;
    vec3 nsum = vec3(0.0);
    float esum = 0.0, ns = 0.0;
    int R = int(u.uHybrid.z);
    for (int dy = -R; dy <= R; ++dy)
        for (int dx = -R; dx <= R; ++dx) {
            ivec2 q = c + ivec2(dx, dy);
            if (any(lessThan(q, ivec2(0))) || any(greaterThanEqual(q, halfSize))) continue;
            uint k = uint(q.y) * row + uint(q.x);
            if (k >= size) continue;
            vec4 s1 = nearSamples[3u * k + 1u];
            if (s1.w != u.uHybrid.y || abs(dot(n, s1.xyz - p)) > tol) continue;
            if (dot(nearSamples[3u * k + 2u].xyz, n) < 0.9) continue;
            vec4 s0 = nearSamples[3u * k];
            nsum += s0.rgb; esum += s0.w; ns += 1.0;
        }
    vec4 hm = vec4(0.0);
    float hc = 0.0;
    if (all(greaterThanEqual(c, ivec2(0))) && all(lessThan(c, halfSize))) {
        uvec2 g = texelFetch(uHistGuide, c, 0).xy;
        if (!guideSky(g)) {
            vec3 gp = guidePosition(g, u.uPrevCamera.xyz);
            uint k = uint(c.y) * row + uint(c.x);
            if (k < size && dot(guideNormalOf(g), n) > 0.9 && abs(dot(n, gp - p)) < tol && length(gp - p) < 2.0 * u.uReuse.z * dist) {
                hm = nearPrev[2u * k];
                vec4 h1 = nearPrev[2u * k + 1u];
                hc = h1.x;
                hybridPrevRatio = hc > 0.0 ? h1.y : -1.0;
            }
        }
    }
    vec4 m = hm;
    float cn = hc;
    if (ns > 0.0) {
        // The samples held counted in full, the blend at least one in uHybrid.w frames: a frame's samples move the pixel by
        // their share of all held until that is under a frame's -- capped in samples, more rays a frame were a shorter
        // history, and the contacts flickered more for them (2026-10-29).
        vec4 est = vec4(nsum / ns, esum / ns);
        cn = min(hc + ns, 65504.0);
        m = hc > 0.0 ? mix(hm, est, max(ns / (hc + ns), 1.0 / u.uHybrid.w)) : est;
    }
    if (store) nearCur[2u * at] = m;
    count = cn;
    return m;
}

void main() {
    ivec2 h = ivec2(gl_FragCoord.xy);
    // Each pixel of an 8x8 block in turn.
    keeper = ((uint(h.x) & 7u) | ((uint(h.y) & 7u) << 3)) == (uint(u.uBlurAt.z) & 63u);
    ivec2 fullSize = textureSize(uSceneDepth, 0);
    ivec2 q = min(h * 2 + ivec2(u.uHalf.yz), fullSize - 1);
    float raw = texelFetch(uSceneDepth, q, 0).r;
    vec4 ns = texelFetch(uPrepassNormal, q, 0);
    outIncidentGradient = vec4(0.0);
    outGuide = GUIDE_SKY;
    outReuse = vec4(-1.0);
    uint unsureAt = uint(h.y) * uint(u.uUnsure.y) + uint(h.x);
    bool unsureIn = h.x < int(u.uUnsure.y) && unsureAt < uint(u.uUnsure.z);
    if (raw >= 1.0 - 1e-6 || dot(ns.xyz, ns.xyz) < 1e-6) {
        if (unsureIn) { rtgiUnsure[unsureAt] = -1.0; rtgiBlurRadius[unsureAt] = 0.0; }
        outIncident = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }
    vec2 ndc = (vec2(q) + 0.5) / vec2(fullSize) * 2.0 - 1.0;
    vec4 world = u.uInvViewProj * vec4(ndc, raw, 1.0);
    vec3 p = world.xyz / world.w;
    vec3 n = normalize(ns.xyz);
    outGuide = guidePack(p - u.uCamera.xyz, n);
    // Reused (uReuse.x): a settled cache gives a pixel the same light frame after frame -- ~17 hash lookups a half-res
    // pixel, the readout and the blur 3.1 ms a frame at 1080p (Crytek Sponza, 2026-10-27). A quarter of the screen read
    // fresh a frame, in 16 x 16 blocks in turn (whole SIMD groups: one pixel in four would run every group's full path);
    // the rest take last frame's values where its pixel there is this surface -- the same plane and normal, within
    // uReuse.z of the distance -- else read fresh too (a disocclusion). A cell's change shows within four frames (the
    // cache's own turns are sixteen).
    if (u.uReuse.x > 0.5) {
        uvec2 blk = uvec2(h) / 16u;
        bool fresh = ((blk.x & 1u) | ((blk.y & 1u) << 1)) == uint(u.uReuse.y);
        vec4 pc = u.uPrevViewProj * vec4(p, 1.0);
        if (!fresh && pc.w > 0.0) {
            // Bilinear at the point's place last frame, each of the four history pixels this surface (the same plane and
            // normal) or left out, the rest renormalised; read fresh where under half the weight survives. A history
            // pixel's sample stood at full-res 2j + its 2x2's pixel then (uReuse.w), not this frame's. Taken from the
            // nearest history pixel, every reused pixel snapped a pixel as the camera moved, the fresh blocks turning
            // through them: the light jittered in motion (the user, 2026-10-27).
            vec2 full = (pc.xy / pc.w * 0.5 + 0.5) * vec2(fullSize);
            vec2 subPrev = vec2(mod(u.uReuse.w, 2.0), floor(u.uReuse.w * 0.5));
            vec2 fj = (full - 0.5 - subPrev) * 0.5;
            ivec2 j0 = ivec2(floor(fj));
            vec2 tj = fj - vec2(j0);
            ivec2 halfSize = textureSize(uHistGuide, 0);
            float dist = length(p - u.uCamera.xyz);
            vec4 raw = vec4(0.0), grad = vec4(0.0), blurred = vec4(0.0);
            float settled = 0.0;
            float wSum = 0.0;
            for (int k = 0; k < 4; k++) {
                ivec2 j = j0 + ivec2(k & 1, k >> 1);
                if (any(lessThan(j, ivec2(0))) || any(greaterThanEqual(j, halfSize))) continue;
                uvec2 g = texelFetch(uHistGuide, j, 0).xy;
                if (guideSky(g)) continue;
                vec3 gp = guidePosition(g, u.uPrevCamera.xyz);
                if (dot(guideNormalOf(g), n) <= 0.95 || abs(dot(n, gp - p)) >= 0.01 * dist + 0.01 || length(gp - p) >= u.uReuse.z * dist) continue;
                float w = ((k & 1) == 1 ? tj.x : 1.0 - tj.x) * ((k >> 1) == 1 ? tj.y : 1.0 - tj.y);
                raw += texelFetch(uHistRaw, j, 0) * w;
                vec4 gj = texelFetch(uHistGradient, j, 0);
                grad += gj * w;
                settled += (gj.w > 0.5 ? 1.0 : 0.0) * w;
                blurred += texelFetch(uHistBlurred, j, 0) * w;
                wSum += w;
            }
            // Only light read from settled cells (the gradient's w): a cell still learning changes frame to frame, and
            // its reused 16 x 16 blocks against the fresh ones beside them showed as a checkerboard of squares turning
            // through the four phases in motion (the user, Crytek Sponza's roof and dim corners, 2026-10-28).
            if (wSum >= 0.5 && settled >= 0.999 * wSum) {
                outIncident = raw / wSum;
                outIncidentGradient = grad / wSum;
                outReuse = blurred / wSum;
                return;
            }
        }
    }
    uint side = rtgiSide(n);
    rtgiRegion = nearRegion(p, side);
    // The two levels around the point's, blended by where it sits between them (rtgiLevelF): no jump at a level's edge.
    float lf = rtgiLevelF(p, u.uCamera.xyz, u.uParams);
    uint level = uint(lf);
    float blurR = -1.0;
    if (unsureIn) {
        uint sc = rtgiFindKept(rtgiCellHash(ivec3(floor((p - RTGI_ORIGIN) / rtgiSize(level, u.uParams))), level, side, u.uParams), u.uParams);
        float unsure = 1.0;
        float rel = 1.0, invD = 0.0;
        if (sc != 0xFFFFFFFFu) {
            uvec4 a = rtgiRecord[2u * sc];
            vec2 conf = unpackHalf2x16(a.z);   // relative error, done
            rel = conf.x;
            invD = uintBitsToFloat(a.w);
            if (uintBitsToFloat(rtgiRecord[2u * sc + 1u].w) >= 4.0)
                unsure = conf.y > 0.5 ? 0.125 * u.uUnsure.x : clamp(conf.x, u.uUnsure.x, 1.0);
        }
        rtgiUnsure[unsureAt] = unsure;
        // The blur's radius (rtgi_blur.frag): the cell's size times its relative error over the done threshold (0.5 to 4
        // cells), twice, at most uBlurAt.y of the rays' harmonic mean hit distance; over a full-res pixel's footprint at
        // its distance, halved, at most uBlurAt.x / 2.
        float worldR = rtgiSize(level, u.uParams) * clamp(rel / u.uUnsure.x, 0.5, 4.0) * 2.0;
        if (invD > 1e-6) worldR = min(worldR, u.uBlurAt.y / invD);
        float dist = length(p - u.uCamera.xyz);
        blurR = u.uBlurAt.x > 0.0 ? min(worldR / (dist * u.uUnsure.w), u.uBlurAt.x) * 0.5 : 0.0;
        rtgiBlurRadius[unsureAt] = blurR;
    }
    uint top = uint(u.uParams.w);
    float t = lf - float(level);
    float sky0, sky1;
    vec3 b0, b1;
    float near0, near1, far0, far1;
    cellVisFetch(p, n, side, level);
    vec4 e0 = readLevel(p, side, level, sky0, b0, near0, far0);
    float rel0 = readRel;
    vec4 e1 = level < top ? readLevel(p, side, level + 1u, sky1, b1, near1, far1) : vec4(0.0);
    float rel1 = readRel;
    vec4 e;
    float sky, nearCell, farShare;
    vec3 bv;
    if (u.uNearField.w > 0.5) {
        // Each level by how much its cells hold (readLevel's w: the bilinear weights times held / 32, full at 1), over the
        // level above it: a cell just placed or restarted (a sun epoch's cut) fades in over its parent's light, not in its
        // place. readLevel's normalising takes the fade back wherever every cell around the point is young, and the finer
        // level was taken whenever it held anything: its first draws showed as a bright spot that blended away as it filled,
        // then another (the user, San Miguel, 2026-10-28). Filled cells (w 1) read as before.
        float c0 = clamp(e0.w, 0.0, 1.0), c1 = clamp(e1.w, 0.0, 1.0);
        vec4 e2 = vec4(0.0);
        float sky2 = 0.0, near2 = 0.0, far2 = 0.0;
        vec3 b2 = vec3(0.0);
        if (c1 < 1.0 && level + 1u < top) e2 = readLevel(p, side, min(level + 2u, top), sky2, b2, near2, far2);
        float c2 = e2.w >= 0.05 ? 1.0 : 0.0;
        // The coarse estimate: the next level up over the one above it, by its own fill.
        float k1 = c2 > 0.0 ? c1 : (c1 >= 0.05 ? 1.0 : 0.0);
        float kc = k1 + (1.0 - k1) * c2;
        if (kc > 0.0) {
            vec3 ce = (e1.rgb * k1 + e2.rgb * (1.0 - k1) * c2) / kc;
            float cs = (sky1 * k1 + sky2 * (1.0 - k1) * c2) / kc;
            vec3 cb = (b1 * k1 + b2 * (1.0 - k1) * c2) / kc;
            float cn = (near1 * k1 + near2 * (1.0 - k1) * c2) / kc, cf = (far1 * k1 + far2 * (1.0 - k1) * c2) / kc;
            // The point's own level by its fill and its place between the two (rtgiLevelF): filled, mix(e0, e1, t).
            float k0 = c0 >= 0.05 ? (1.0 - t) * c0 : 0.0;
            e = vec4(mix(ce, e0.rgb, k0), 1.0);
            sky = mix(cs, sky0, k0); bv = mix(cb, b0, k0); nearCell = mix(cn, near0, k0); farShare = mix(cf, far0, k0);
        }
        else if (c0 >= 0.05) { e = e0; sky = sky0; bv = b0; nearCell = near0; farShare = far0; }
        else e = vec4(0.0);
    }
    else if (e0.w >= 0.05 && e1.w >= 0.05) { e = vec4(mix(e0.rgb, e1.rgb, t), 1.0); sky = mix(sky0, sky1, t); bv = mix(b0, b1, t); nearCell = mix(near0, near1, t); farShare = mix(far0, far1, t); }
    else if (e0.w >= 0.05) { e = e0; sky = sky0; bv = b0; nearCell = near0; farShare = far0; }
    else if (e1.w >= 0.05) { e = e1; sky = sky1; bv = b1; nearCell = near1; farShare = far1; }
    else e = readLevel(p, side, min(level + 2u, top), sky, bv, nearCell, farShare);
    // --rtgi-volume: the cells' light against the world's probes by how sure each is -- a cell's relative error (the blend's,
    // at the pixel's own level where it holds any) and its fill (held / 32), the volume's uVolume2.x: inverse variance. A
    // surface the camera just reached reads the probes' light, trained before it got there, and the cells' as they settle;
    // where no cell holds anything, the probes' alone.
    if (u.uVolume.w > 0.5) {
        float vsky;
        vec4 v = volumeCell(p, n, vsky);
        if (v.w > 0.5) {
            // Both by where the point sits between its two levels (rtgiLevelF), as the light is: taken at the point's own
            // level alone, the weight jumped where the level did -- a ring at a fixed distance that moved with the camera
            // (the user, Bistro, 2026-10-29).
            float f0 = clamp(e0.w, 0.0, 1.0), f1 = clamp(e1.w, 0.0, 1.0);
            bool h0 = e0.w >= 0.05, h1 = e1.w >= 0.05;
            float fill = h0 && h1 ? mix(f0, f1, t) : h0 ? f0 : h1 ? f1 : clamp(e.w, 0.0, 1.0);
            float rel = max(h0 && h1 ? mix(rel0, rel1, t) : h0 ? rel0 : h1 ? rel1 : readRel, 0.02);
            float wc = e.w > 0.0 ? fill * fill / (rel * rel) : 0.0;
            float wv = 1.0 / (u.uVolume2.x * u.uVolume2.x);
            if (e.w <= 0.0) { bv = vec3(0.0); nearCell = 1.0; farShare = 1.0; sky = 0.0; settledRead = false; }
            e.rgb = (e.rgb * wc + v.rgb * wv) / (wc + wv);
            sky = (sky * wc + vsky * wv) / (wc + wv);
            e.w = 1.0;
        }
    }
    // Contact detail: the cell's light split into what came from past the contact radius (its far share) and what from
    // nearer, each scaled by its own visibility at the pixel against the cell's -- the contact cells' near visibility over
    // the light cells' (both the share of rays past the radius, cosine-weighted, by rays: rtgi_train.comp,
    // rtgi_contact_train.comp). A pixel more enclosed than its cell loses far light and gains the near surfaces' bounce.
    // Scaled whole by the visibility ratio, the creases read 6% dark (a crease's occluder lights it too); GTAO in either
    // place measured something else (18% bright), 2026-10-26.
    // --rtgi-pool (uPool.x levels at most): the light pooled up the coarser levels while they agree with it. A cell's light is
    // its samples' mean, and outdoors -- a shaded wall lit by the rare ray onto a sunlit facade -- ~100 samples left each
    // 20 cm cell off by about half, neighbours disagreeing: blotches that never settled (the user, Bistro, 2026-10-29). A
    // coarser level holds 4x the samples a step (twice the side over the surface); folded in by inverse variance while it
    // agrees within two standard errors of the pool, it stops at what is real -- a light's edge, a contact, where the
    // coarser cell's average differs. The cell size a pixel reads is thus the light's own: big on a flat lit wall, small
    // where the light changes.
    if (u.uPool.x > 0.5 && e.w > 0.0) {
        float skyD; vec3 bvD; float nD, fD;
        // The pixel's own level's error (the reads above may have ended at another), at most 1: a young cell's larger
        // error made every coarser level agree.
        readLevel(p, side, level, skyD, bvD, nD, fD);
        float lp = dot(e.rgb, vec3(0.2126, 0.7152, 0.0722));
        float rp = min(readRel, 1.0);
        float vp = max(rp * rp * lp * lp, 1e-12);
        vec3 ep = e.rgb;
        for (uint l = level + 1u; l <= min(level + uint(u.uPool.x), top); ++l) {
            vec4 ec = readLevel(p, side, l, skyD, bvD, nD, fD);
            if (ec.w < 0.05) continue;
            float lc = dot(ec.rgb, vec3(0.2126, 0.7152, 0.0722));
            float rc = min(readRel, 1.0);
            float vc = max(rc * rc * lc * lc, 1e-12);
            if (abs(lc - lp) > u.uPool.y * sqrt(vp + vc) + 0.01 * lp) break;
            ep = (ep / vp + ec.rgb / vc) / (1.0 / vp + 1.0 / vc);
            vp = 1.0 / (1.0 / vp + 1.0 / vc);
            lp = dot(ep, vec3(0.2126, 0.7152, 0.0722));
        }
        e.rgb = ep;
    }
    // --rtgi-hybrid: the cache's far light, the pixel's own near field -- per ray, a surface met within the contact radius
    // brings its own light, a ray past it the cell's mean far light per such ray (E x farShare / nearCell: the cache's
    // light from past the radius over the share of its rays that left), faded in by the samples held
    // (tools/gi-model/near_far_hybrid.py: Crytek 0.148 against the cache's 0.158, its darkest quarter 0.29 against 0.34).
    if (u.uHybrid.x > 0.5 && e.w > 0.0) {
        float held;
        bool world = u.uHybrid2.y > 0.5;
        vec4 nm = world ? readNear(p, side, level, held) : hybridNear(p, n, length(p - u.uCamera.xyz), unsureIn, unsureAt, held);
        if (held > 0.0) {
            // Pulled toward the cell's own (its escape share and its near light: the cache's, from far more rays) as uHybrid2.x
            // samples more: a pixel's few rays at a contact, a mid escape share, wobbled frame to frame -- the contacts' shading
            // crawled (the user, Crytek Sponza, 2026-10-29); consistent evidence still shows.
            float k = u.uHybrid2.x;
            float v = (held * nm.w + k * nearCell) / max(held + k, 1e-6);
            vec3 nearLight = (held * nm.rgb + k * e.rgb * (1.0 - farShare)) / max(held + k, 1e-6);
            vec3 hyb = v * e.rgb * farShare / max(nearCell, 0.05) + nearLight;
            // Applied as a ratio to the cache's light, the ratio itself averaged over the history's frames: the far term's
            // farShare / nearCell is the cache's statistics this frame, and in a dark corridor's thinly trained cells their
            // wobble, amplified by the division, crawled though the near samples were settled (the user's corridor:
            // 128 frames' history did not help, 2026-10-29).
            float le = dot(e.rgb, vec3(0.2126, 0.7152, 0.0722));
            float rNow = le > 1e-7 ? dot(hyb, vec3(0.2126, 0.7152, 0.0722)) / le : 1.0;
            // The world's near cells hold their own history (the resolve's): no screen history over them.
            float r = !world && hybridPrevRatio >= 0.0 ? mix(hybridPrevRatio, rNow, 1.0 / u.uHybrid.w) : rNow;
            r = mix(1.0, r, min(held / (world ? 16.0 : 4.0), 1.0));
            if (unsureIn) nearCur[2u * unsureAt + 1u] = vec4(held, r, 0.0, 0.0);
            e.rgb *= r;
        } else if (unsureIn) nearCur[2u * unsureAt + 1u] = vec4(0.0);
    } else if (u.uHybrid.x > 0.5 && unsureIn) {
        nearCur[2u * unsureAt] = vec4(0.0);
        nearCur[2u * unsureAt + 1u] = vec4(0.0);
    }
    // --rtgi-near-cook: where a cooked cell lies, its own gathered irradiance in place of the cache's (faded in by how much
    // of the point the cooked cells cover): the geometry's own answer at every crease and edge, no ratio of the cache's
    // statistics in it.
    if (u.uHybrid.x < 0.5 && NEAR_PARAMS.w > 0.5) {
        float found;
        vec4 nc = readNearCooked(p, side, found);
        if (found > 0.05 && NEAR_PARAMS.w > 1.5) {
            e.rgb = e.w > 0.0 ? mix(e.rgb, nc.rgb, clamp(found, 0.0, 1.0)) : nc.rgb;
            if (e.w <= 0.0) { sky = 0.0; bv = vec3(0.0); }
            e.w = 1.0;
        } else if (found > 0.05 && e.w > 0.0) {
            // The near light over the cache's far light: per ray, a surface met brings the cooked light, one past the
            // contact radius the cell's far light per such ray (E x farShare / nearCell).
            float esc = nc.w - 1.0;
            vec3 cooked = esc * e.rgb * farShare / max(nearCell, 0.05) + nc.rgb;
            e.rgb = mix(e.rgb, cooked, clamp(found, 0.0, 1.0));
        }
    }
    if (e.w > 0.0 && u.uContact.x > 0.5) {
        vec2 vc = readContact(p, side, level);
        if (vc.y >= 0.05) {
            float r = farShare * (vc.x + 0.02) / (nearCell + 0.02) + (1.0 - farShare) * (1.02 - vc.x) / (1.02 - nearCell);
            e.rgb *= clamp(r, u.uContact.y, u.uContact.z);
        }
    }
    outIncident = e.w > 0.0 ? vec4(e.rgb, sky) : vec4(0.0, 0.0, 0.0, 1.0);
    // How the irradiance turns with the normal, for the lit pass to carry it onto the normal-mapped one (its gradient,
    // luminance per unit of normal along the surface). Radiance taken as linear in direction, L = a + b.w, the irradiance
    // vector's part along the surface is pi/4 b_t and the irradiance's change 2 pi / 3 b_t: 8/3 of it. Zero before
    // (2026-10-26): the bounce light lost every normal map. uNearField.y 0: none.
    // The visible share done: one half-res pixel in 16, its level's nearest cell.
    if (e.w > 0.0 && ((uint(h.x) & 3u) | ((uint(h.y) & 3u) << 2)) == 0u) {
        uint sd = rtgiFindKept(rtgiCellHash(ivec3(floor((p - RTGI_ORIGIN) / rtgiSize(level, u.uParams))), level, side, u.uParams), u.uParams);
        atomicAdd(doneCount[0], 1u);
        atomicAdd(doneCount[2], lookups);
        atomicAdd(doneCount[3], misses);
        if (blurR >= 0.0) {
            atomicAdd(doneCount[4], 1u);
            if (blurR < 1.0) atomicAdd(doneCount[5], 1u);
        }
        if (sd != 0xFFFFFFFFu && unpackHalf2x16(rtgiRecord[2u * sd].z).y > 0.5) atomicAdd(doneCount[1], 1u);
        // Its relative error under 1% (6) and 3% (7): how far from done the visible is.
        float relD = sd != 0xFFFFFFFFu ? unpackHalf2x16(rtgiRecord[2u * sd].z).x : 1.0;
        if (relD < 0.01) atomicAdd(doneCount[6], 1u);
        if (relD < 0.03) atomicAdd(doneCount[7], 1u);
        // Its relative error summed (4096ths, at most 1): the screen's mean, the point budget's need (SponzaLoop.RtgiBudget.cs).
        atomicAdd(doneCount[8], uint(clamp(relD, 0.0, 1.0) * 4096.0));
    }
    // The RTGI panel's views (uContact.w, SponzaLoop.RtgiDebug.cs), the pixel's own cell (its level's, nearest) painted
    // in place of its light: 1 done (green) or open (red), by the light; 2 how unsure, the blend's relative error, from
    // the done threshold (blue) to 16x it (red); 3 samples held, 1 to 16,384 on a log scale; 4 its level; 5 the mean hit
    // distance, 5 cm (red) to 20 m (blue); 6 sky visibility; 7 sun visibility. No cell: magenta.
    int view = int(u.uContact.w + 0.5);
    // The cooked and world-space state, painted (SponzaLoop.RtgiDebug.cs's views 11-21): everything the light is made of
    // but the screen's stages, each as the pixel reads it. Magenta: none.
    if ((view >= 11 && view <= 21) || view == 24) {
        vec3 c = vec3(1.0, 0.0, 1.0);
        uint sc = rtgiFind(rtgiCellHash(ivec3(floor((p - RTGI_ORIGIN) / rtgiSize(level, u.uParams))), level, side, u.uParams), u.uParams);
        if (view == 11) {
            // The pixel's cell: a colour by its key (its level's, its side's) -- its bounds, what it spans.
            uvec2 hk = rtgiCellHash(ivec3(floor((p - RTGI_ORIGIN) / rtgiSize(level, u.uParams))), level, side, u.uParams);
            uint hh = rtgiHash(hk.y);
            c = vec3(hh & 255u, (hh >> 8) & 255u, (hh >> 16) & 255u) / 255.0 * 0.8 + 0.2;
        } else if (view == 12) {
            // Its size, the geometry's (5 cm blue to 1 m red, log); the camera's distance's where no probes are cooked: grey.
            float g = volCellScale(p);
            float size = rtgiCellSize(p, u.uCamera.xyz, u.uParams);
            c = g > 0.0 ? heat(log2(size / 0.05) / log2(20.0)) : vec3(0.5);
        } else if (view == 13 || view == 14) {
            // Its own light, unblended (the resolve's), or blended (the record the readout reads): the cell alone, no neighbours.
            if (sc != 0xFFFFFFFFu && rtgiResolved[2u * sc + 1u].y >= 0.5) {
                if (view == 13) c = rtgiResolved[2u * sc].rgb;
                else { uvec4 a = rtgiRecord[2u * sc]; c = vec3(unpackHalf2x16(a.x), unpackHalf2x16(a.y).x); }
            }
        } else if (view == 15) {
            // Its traced reach at its own level: red +a0 blocked, green -a0, blue +a1, and yellow -a1 dimming it; grey all
            // reached; magenta untraced.
            if (visKnown) {
                uint b = (visWord >> 16) & 15u;
                c = b == 15u ? vec3(0.6) : vec3((b & 1u) == 0u ? 1.0 : 0.1, (b & 2u) == 0u ? 1.0 : 0.1, (b & 4u) == 0u ? 1.0 : 0.1) * ((b & 8u) == 0u ? 0.5 : 1.0);
            }
        } else if (view == 16 || view == 17 || view == 18) {
            // The cooked near cell under the pixel: its colour by index, its escape share (white open, black enclosed), its
            // near light.
            uint ax = side >> 1u;
            ivec3 cc = ivec3(floor((p - RTGI_ORIGIN) / max(NEAR_PARAMS.x, 1e-4)));
            uint ni = NEAR_PARAMS.w > 0.5 ? nearFind(cc, side) : 0xFFFFFFFFu;
            if (ni != 0xFFFFFFFFu) {
                vec4 nl = nearLight[ni];
                uint hh = rtgiHash(ni * 0x9E3779B9u);
                // 17: buried red, cooked but never gathered blue, gathered white (by its open share).
                c = view == 16 ? vec3(hh & 255u, (hh >> 8) & 255u, (hh >> 16) & 255u) / 255.0
                  : view == 17 ? (nl.w < -0.5 ? vec3(1.0, 0.1, 0.1) : nl.w < 0.5 ? vec3(0.1, 0.2, 1.0) : vec3(0.3 + 0.7 * clamp(nl.w - 1.0, 0.0, 1.0)))
                  : nl.rgb;
            }
        } else if (view == 19) {
            // The probes its cell was traced to: the level (blue 0.5 m ... red 8 m), dimmed by how many of the 8 its rays
            // reached; magenta untraced, black no level.
            if (visKnown) {
                uint lv = (visWord >> 8) & 15u;
                c = lv >= uint(VOL_PARAMS.y) ? vec3(0.0) : heat(float(lv) / max(VOL_PARAMS.y - 1.0, 1.0)) * (0.2 + 0.8 * float(bitCount(visWord & 0xFFu)) / 8.0);
            }
        } else if (view == 20) {
            // The probes' harmonic mean distance at p (what sizes the cells), 5 cm blue to 5 m red, log.
            float g = volCellScale(p);
            if (g > 0.0) c = heat(log2(max(g / max(VOL_GEOM.x, 1e-4), 0.05) / 0.05) / log2(100.0));
        } else if (view == 24) {
            // Its region (rtgi_cache.glsl's rtgiRegion): a colour by it; grey region 0 (no cooked cell here).
            uint rg = rtgiRegionFor(level);
            uint hh = rtgiHash(rg * 0x9E3779B9u);
            c = rg == 0u ? vec3(0.5) : vec3(hh & 255u, (hh >> 8) & 255u, (hh >> 16) & 255u) / 255.0;
        } else {
            // 21: how many samples its cell holds, 1 to 16,384 (log) -- where the light is thin.
            if (sc != 0xFFFFFFFFu) c = heat(log2(max(rtgiResolved[2u * sc + 1u].y, 1.0)) / 14.0);
        }
        outIncident = vec4(c, sky);
        outIncidentGradient = vec4(0.0);
        return;
    }
    // 10: the world volume's light alone (magenta where it has none).
    if (view == 10) {
        float vsky;
        vec4 v = volumeCell(p, n, vsky);
        outIncident = vec4(v.w > 0.5 ? v.rgb : vec3(1.0, 0.0, 1.0), vsky);
    }
    if (view >= 1 && view <= 7) {
        uint s = rtgiFindKept(rtgiCellHash(ivec3(floor((p - RTGI_ORIGIN) / rtgiSize(level, u.uParams))), level, side, u.uParams), u.uParams);
        vec3 c = vec3(1.0, 0.0, 1.0);
        if (s != 0xFFFFFFFFu && rtgiResolved[2u * s + 1u].y >= 0.5) {
            vec4 r1 = rtgiResolved[2u * s + 1u];
            vec4 conf = rtgiConfidence[s];
            if (view == 1) c = (conf.w > 0.5 ? vec3(0.1, 1.0, 0.1) : vec3(1.0, 0.1, 0.1)) * max(dot(e.rgb, vec3(0.2126, 0.7152, 0.0722)), 0.05);
            else if (view == 2) c = heat(log2(max(conf.x / u.uUnsure.x, 1.0)) / 4.0);
            else if (view == 3) c = heat(log2(max(r1.y, 1.0)) / 14.0);
            else if (view == 4) c = heat(float(level) / max(float(top), 1.0));
            else if (view == 5) c = heat(1.0 - clamp(log2(max(1.0 / max(rtgiHitInv[s], 1e-4), 0.05) / 0.05) / 8.6, 0.0, 1.0));
            else if (view == 6) c = vec3(rtgiFiltered[s].a);
            else c = vec3(clamp(r1.x, 0.0, 1.0));
        }
        outIncident = vec4(c, sky);
    }
    if (e.w > 0.0 && u.uNearField.y > 0.5) outIncidentGradient = vec4((8.0 / 3.0) * (bv - dot(bv, n) * n), 0.0);
    // w: the light came from settled cells alone (settledRead), for the next frame's reuse.
    outIncidentGradient.w = e.w > 0.0 && settledRead ? 1.0 : 0.0;
}
