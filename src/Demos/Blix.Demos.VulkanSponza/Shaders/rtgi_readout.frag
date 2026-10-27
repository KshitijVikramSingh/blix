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
layout(std430, set = 0, binding = 9) buffer RtgiDoneCount { uint doneCount[8]; };
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

vec4 readLevel(vec3 p, uint side, uint level, out float sky, out vec3 bvec, out float near, out float far) {
    float size = rtgiSize(level, u.uParams);
    vec3 f = p / size - 0.5;
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
    int cAxis = int(floor(p[axis] / size));
    for (int k = 0; k < (plane ? 4 : 8); k++) {
        ivec3 o = ivec3(k & 1, (k >> 1) & 1, k >> 2);
        if (plane) {
            o = ivec3(0);
            o[a0] = k & 1;
            o[a1] = k >> 1;
        }
        ivec3 cc = c0 + o;
        if (plane) cc[axis] = cAxis;
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
        vec2 rg = unpackHalf2x16(a.x), bs = unpackHalf2x16(a.y);
        vec2 dxy = unpackHalf2x16(c.x), dzn = unpackHalf2x16(c.y);
        e += vec3(rg, bs.x) * wk;
        sky += bs.y * wk;
        bvec += vec3(dxy, dzn.x) * wk;
        near += dzn.y * wk;
        far += unpackHalf2x16(c.z).x * wk;
        w += wk;
    }
    if (w > 0.0) { e /= w; sky /= w; bvec /= w; near /= w; far /= w; }
    return vec4(e, w);
}

// The contact cells around p (rtgi_contact_train.comp: a quarter of the level's size, its level 16 up), trilinear by what
// each holds (full at 16): their near visibility, w the weight found.
vec2 readContact(vec3 p, uint side, uint level) {
    float size = 0.25 * rtgiSize(level, u.uParams);
    vec3 f = p / size - 0.5;
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
                grad += texelFetch(uHistGradient, j, 0) * w;
                blurred += texelFetch(uHistBlurred, j, 0) * w;
                wSum += w;
            }
            if (wSum >= 0.5) {
                outIncident = raw / wSum;
                outIncidentGradient = grad / wSum;
                outReuse = blurred / wSum;
                return;
            }
        }
    }
    uint side = rtgiSide(n);
    // The two levels around the point's, blended by where it sits between them (rtgiLevelF): no jump at a level's edge.
    float lf = rtgiLevelF(p, u.uCamera.xyz, u.uParams);
    uint level = uint(lf);
    float blurR = -1.0;
    if (unsureIn) {
        uint sc = rtgiFindKept(rtgiCellHash(ivec3(floor(p / rtgiSize(level, u.uParams))), level, side, u.uParams), u.uParams);
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
    vec4 e0 = readLevel(p, side, level, sky0, b0, near0, far0);
    vec4 e1 = level < top ? readLevel(p, side, level + 1u, sky1, b1, near1, far1) : vec4(0.0);
    vec4 e;
    float sky, nearCell, farShare;
    vec3 bv;
    if (e0.w >= 0.05 && e1.w >= 0.05) { e = vec4(mix(e0.rgb, e1.rgb, t), 1.0); sky = mix(sky0, sky1, t); bv = mix(b0, b1, t); nearCell = mix(near0, near1, t); farShare = mix(far0, far1, t); }
    else if (e0.w >= 0.05) { e = e0; sky = sky0; bv = b0; nearCell = near0; farShare = far0; }
    else if (e1.w >= 0.05) { e = e1; sky = sky1; bv = b1; nearCell = near1; farShare = far1; }
    else e = readLevel(p, side, min(level + 2u, top), sky, bv, nearCell, farShare);
    // Contact detail: the cell's light split into what came from past the contact radius (its far share) and what from
    // nearer, each scaled by its own visibility at the pixel against the cell's -- the contact cells' near visibility over
    // the light cells' (both the share of rays past the radius, cosine-weighted, by rays: rtgi_train.comp,
    // rtgi_contact_train.comp). A pixel more enclosed than its cell loses far light and gains the near surfaces' bounce.
    // Scaled whole by the visibility ratio, the creases read 6% dark (a crease's occluder lights it too); GTAO in either
    // place measured something else (18% bright), 2026-10-26.
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
        uint sd = rtgiFindKept(rtgiCellHash(ivec3(floor(p / rtgiSize(level, u.uParams))), level, side, u.uParams), u.uParams);
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
    }
    // The RTGI panel's views (uContact.w, SponzaLoop.RtgiDebug.cs), the pixel's own cell (its level's, nearest) painted
    // in place of its light: 1 done (green) or open (red), by the light; 2 how unsure, the blend's relative error, from
    // the done threshold (blue) to 16x it (red); 3 samples held, 1 to 16,384 on a log scale; 4 its level; 5 the mean hit
    // distance, 5 cm (red) to 20 m (blue); 6 sky visibility; 7 sun visibility. No cell: magenta.
    int view = int(u.uContact.w + 0.5);
    if (view >= 1 && view <= 7) {
        uint s = rtgiFindKept(rtgiCellHash(ivec3(floor(p / rtgiSize(level, u.uParams))), level, side, u.uParams), u.uParams);
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
}
