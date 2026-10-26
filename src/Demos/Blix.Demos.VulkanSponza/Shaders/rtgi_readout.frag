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

layout(set = 0, binding = 0) uniform RtgiReadout {
    mat4 uInvViewProj;   // inverse of the view-projection the pre-pass drew with
    vec4 uParams;        // rtgiParams (rtgi_cache.glsl)
    vec4 uCamera;        // xyz position
    vec4 uHalf;          // yz the full-res pixel of its 2x2
    vec4 uNearField;     // x the near field's radius (rtgi_cache.glsl's RTGI_NEAR); y 1: the gradient written
    vec4 uContact;       // x 1: contact detail on; yz the ratio's clamp; w the RTGI panel's view (0: the light)
    vec4 uUnsure;        // x the done threshold (relative error); y the uncertainty map's row (half-res pixels); z its size
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
layout(std430, set = 0, binding = 9) buffer RtgiDoneCount { uint doneCount[4]; };
// Per half-res pixel: how unsure its cell is, for the next frame's ray placement (rtgi_rays.comp) -- read there for
// a texel's price, where each candidate found its cell in the hash itself: 5.8 ms a frame at 4x the rays (Crytek
// Sponza, 2026-10-26). The blend's relative error, at least the done threshold; a done cell an eighth of that; a cell
// holding under 4 samples, or none, 1; the sky -1.
layout(std430, set = 0, binding = 10) writeonly buffer RtgiUnsure { float rtgiUnsure[]; };
layout(std430, set = 0, binding = 11) readonly buffer RtgiHitInv { float rtgiHitInv[]; };

// Blue (0) to green to red (1).
vec3 heat(float t) {
    t = clamp(t, 0.0, 1.0);
    return clamp(vec3(1.5 - abs(4.0 * t - 3.0), 1.5 - abs(4.0 * t - 2.0), 1.5 - abs(4.0 * t - 1.0)), 0.0, 1.0);
}

#define RTGI_KEYS(i) rtgiKeys[i]
#define RTGI_NEAR u.uNearField.x
#include "rtgi_cache.glsl"

// Trilinear over the level's cells around p (their centres), w the weight found.
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
    for (int k = 0; k < 8; k++) {
        ivec3 o = ivec3(k & 1, (k >> 1) & 1, k >> 2);
        uint s = rtgiFind(rtgiCellHash(c0 + o, level, side, u.uParams), u.uParams);
        if (s == 0xFFFFFFFFu) continue;
        vec4 r1 = rtgiResolved[2u * s + 1u];
        if (r1.y < 0.5) continue;
        vec3 b = mix(1.0 - t, t, vec3(o));
        // Weighted by what it holds too, full at 32 samples: a new cell (a landing just requested, a level just blended in,
        // one refilled) fades in under its neighbours rather than showing its first draw -- odd lights that came and went.
        float wk = b.x * b.y * b.z * clamp(r1.y / 32.0, 1.0 / 32.0, 1.0);
        vec4 r0 = rtgiFiltered[s];
        e += r0.rgb * wk;
        sky += r0.a * wk;
        bvec += rtgiDirection[s].xyz * wk;
        near += rtgiDirection[s].w * wk;
        far += rtgiMoments[3u * s + 2u].w * wk;
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
        uint s = rtgiFind(rtgiCellHash(c0 + o, level + 16u, side, u.uParams), u.uParams);
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
    ivec2 fullSize = textureSize(uSceneDepth, 0);
    ivec2 q = min(h * 2 + ivec2(u.uHalf.yz), fullSize - 1);
    float raw = texelFetch(uSceneDepth, q, 0).r;
    vec4 ns = texelFetch(uPrepassNormal, q, 0);
    outIncidentGradient = vec4(0.0);
    uint unsureAt = uint(h.y) * uint(u.uUnsure.y) + uint(h.x);
    bool unsureIn = h.x < int(u.uUnsure.y) && unsureAt < uint(u.uUnsure.z);
    if (raw >= 1.0 - 1e-6 || dot(ns.xyz, ns.xyz) < 1e-6) {
        if (unsureIn) rtgiUnsure[unsureAt] = -1.0;
        outIncident = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }
    vec2 ndc = (vec2(q) + 0.5) / vec2(fullSize) * 2.0 - 1.0;
    vec4 world = u.uInvViewProj * vec4(ndc, raw, 1.0);
    vec3 p = world.xyz / world.w;
    vec3 n = normalize(ns.xyz);
    uint side = rtgiSide(n);
    // The two levels around the point's, blended by where it sits between them (rtgiLevelF): no jump at a level's edge.
    float lf = rtgiLevelF(p, u.uCamera.xyz, u.uParams);
    uint level = uint(lf);
    if (unsureIn) {
        uint sc = rtgiFind(rtgiCellHash(ivec3(floor(p / rtgiSize(level, u.uParams))), level, side, u.uParams), u.uParams);
        float unsure = 1.0;
        if (sc != 0xFFFFFFFFu && rtgiResolved[2u * sc + 1u].y >= 4.0) {
            vec4 conf = rtgiConfidence[sc];
            unsure = conf.w > 0.5 ? 0.125 * u.uUnsure.x : clamp(conf.x, u.uUnsure.x, 1.0);
        }
        rtgiUnsure[unsureAt] = unsure;
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
        uint sd = rtgiFind(rtgiCellHash(ivec3(floor(p / rtgiSize(level, u.uParams))), level, side, u.uParams), u.uParams);
        atomicAdd(doneCount[0], 1u);
        if (sd != 0xFFFFFFFFu && rtgiConfidence[sd].w > 0.5) atomicAdd(doneCount[1], 1u);
    }
    // The RTGI panel's views (uContact.w, SponzaLoop.RtgiDebug.cs), the pixel's own cell (its level's, nearest) painted
    // in place of its light: 1 done (green) or open (red), by the light; 2 how unsure, the blend's relative error, from
    // the done threshold (blue) to 16x it (red); 3 samples held, 1 to 16,384 on a log scale; 4 its level; 5 the mean hit
    // distance, 5 cm (red) to 20 m (blue); 6 sky visibility; 7 sun visibility. No cell: magenta.
    int view = int(u.uContact.w + 0.5);
    if (view >= 1 && view <= 7) {
        uint s = rtgiFind(rtgiCellHash(ivec3(floor(p / rtgiSize(level, u.uParams))), level, side, u.uParams), u.uParams);
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
