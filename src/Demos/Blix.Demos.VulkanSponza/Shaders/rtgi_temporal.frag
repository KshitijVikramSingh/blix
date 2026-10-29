#version 450

// --rtgi-temporal (SponzaLoop.RtgiTemporal.cs): the indirect light accumulated over frames, the light alone -- never the
// image. Each full-res pixel finds where its surface point was in last frame's view (the previous view-projection),
// reads the history there, and keeps it only if last frame saw the same point there (the history's distance from that
// frame's camera against this point's, within 2% + 2 cm): a surface just come into view starts over. Then
// out = mix(history, this frame's, 1 / frames held), at most uParams.x frames. The half-res lighting's sample turns
// through its 2x2 each frame (SponzaLoop.HalfLighting.cs), so held frames gather all four: full-res detail from half-res
// work, and the cache's remaining noise averaged. uParams.y 0: no history (the first frame, the sun turned).
//
// The gradient passes through as this frame's: its w is the traced sun's visibility, the direct light, never averaged.
//
// Anti-lag (NRD's fast history): beside it a fast history over uParams.z frames, with its luminance's square. The slow
// history is clamped into the fast one's mean +- uParams.w standard deviations before the blend: when the light truly
// changes the fast history moves within a few frames and drags the slow one along; noise stays inside the box. A sun
// change lagged by the slow history's 16 frames on top of the cache's settling (2026-10-26).

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;
layout(location = 1) out vec4 outIncidentGradient;
layout(location = 2) out vec4 outState;   // x the point's distance from this frame's camera, y frames held, zw its normal
                                          // (octahedral)
layout(location = 3) out vec4 outFast;    // the fast history: rgb, its luminance's square

layout(set = 0, binding = 0) uniform RtgiTemporal {
    mat4 uInvViewProj;   // this frame's (jittered, as the pre-pass drew), inverted
    mat4 uPrevViewProj;  // last frame's
    vec4 uCamera;        // xyz this frame's position
    vec4 uPrevCamera;    // xyz last frame's position
    vec4 uParams;        // x frames held at most; y 1 when the history may be read; z the fast history's frames; w the
                         // clamp's standard deviations (0: no clamp)
    vec4 uCacheParams;   // rtgiParams (rtgi_cache.glsl), for the fix-up
    vec4 uNearField;     // x RTGI_NEAR
    vec4 uVolume;        // rtgi_volume.glsl's VOL_PARAMS
    vec4 uVolumeGeom;    // rtgi_volume.glsl's VOL_GEOM
    vec4 uNearCook;      // rtgi_nearcook.glsl's NEAR_PARAMS
    vec4 uDebug;         // x the RTGI panel's view: 22 the fix-up's pixels magenta; 23 the history's frames held (0 rejected:
                         // blue, 16 red); 11-21 (the readout's): a fix-up pixel black
} u;

layout(set = 0, binding = 1) uniform sampler2D uRaw;
layout(set = 0, binding = 2) uniform sampler2D uRawGradient;
layout(set = 0, binding = 3) uniform sampler2D uSceneDepth;
layout(set = 0, binding = 4) uniform sampler2D uHistory;
layout(set = 0, binding = 5) uniform sampler2D uHistoryState;
layout(set = 0, binding = 6) uniform sampler2D uFastHistory;
layout(set = 0, binding = 7) uniform sampler2D uPrepassNormal;
// The fix-up's cache (a pixel the upsample found no sample of its surface for: lighting_upsample.frag marks it rgb -1).
layout(std430, set = 0, binding = 8) readonly buffer RtgiKeys { uint rtgiKeys[]; };
layout(std430, set = 0, binding = 9) readonly buffer RtgiRecord { uvec4 rtgiRecord[]; };
layout(std430, set = 0, binding = 10) readonly buffer RtgiVolumeTable { uvec2 volTable[]; };
layout(std430, set = 0, binding = 11) readonly buffer RtgiVolumeProbes { float volProbes[]; };
layout(std430, set = 0, binding = 12) readonly buffer RtgiNearTable { uvec2 nearTable[]; };
layout(std430, set = 0, binding = 13) readonly buffer RtgiNearLight { vec4 nearLight[]; };
// The cells' traced reach (rtgi_volume_vis.comp), as the readout reads it: the fix-up leaves out the same neighbours.
layout(std430, set = 0, binding = 14) readonly buffer RtgiVolumeCellVis { uvec2 cellVis[]; };
layout(std430, set = 0, binding = 15) readonly buffer RtgiNearPoints { vec4 nearPoints[]; };

#define VOL_PARAMS u.uVolume
#define VOL_GEOM u.uVolumeGeom
#define VOL_TABLE(i) volTable[i]
#define VOL_PROBES(i) volProbes[i]
#define VOL_DEPTH(i) 0u
#include "rtgi_volume.glsl"
#define RTGI_GEOM_SCALE(p) volCellScale(p)
#define RTGI_FAR_GROW u.uVolumeGeom.z
#define RTGI_KEYS(i) rtgiKeys[i]
#define RTGI_NEAR u.uNearField.x
// The cooked cells' regions by level and their links (rtgi_nearcook_cook.comp; rtgi_cache.glsl's rtgiRegion).
layout(std430, set = 0, binding = 16) readonly buffer RtgiNearRegions { uint nearRegions[]; };
#define NEAR_REGIONS(i) nearRegions[i]
#define RTGI_REGION_OF 1
layout(std430, set = 0, binding = 17) readonly buffer RtgiNearAdj { uint nearAdj[]; };
#define NEAR_ADJ(i) nearAdj[i]
#include "rtgi_cache.glsl"
#define NEAR_PARAMS u.uNearCook
#define NEAR_TABLE(i) nearTable[i]
#define NEAR_POINTS(i) nearPoints[i]
#include "rtgi_nearcook.glsl"

// The pixel's own cell's traced reach (rtgi_readout.frag's cellVisFetch, read alone: the readout asks for it): its
// neighbours a ray from its point reached, at its level and the two coarser. Without it the fix-up blended the cell past
// a junction -- a comb of light along the crevices testbed's wall edges, there from one place and not a step away (the
// user, 2026-10-29).
bool visKnown = false;
uint visWord = 0u, visLevel0 = 0u;
void fixVis(vec3 p, uint side, uint level) {
    if (u.uVolume.w < 0.5) return;
    vec4 params = u.uCacheParams;
    uint sc = rtgiFind(rtgiCellHash(ivec3(floor((p - RTGI_ORIGIN) / rtgiSize(level, params))), level, side, params), params);
    if (sc == 0xFFFFFFFFu) return;
    uvec2 v = cellVis[sc];
    if (v.x != rtgiKeys[sc] || (v.y & 0x1000u) == 0u) return;
    visKnown = true;
    visWord = v.y;
    visLevel0 = level;
}
bool neighbourSeen(vec3 p, ivec3 cc, uint level, uint a0, uint a1, float size) {
    int d0 = cc[a0] - int(floor((p[a0] - RTGI_ORIGIN[a0]) / size)), d1 = cc[a1] - int(floor((p[a1] - RTGI_ORIGIN[a1]) / size));
    // Not traced (the readout asks only at its half-res points): its own cell alone, never a blind neighbour.
    if (!visKnown) return u.uVolume.w < 0.5 || (d0 == 0 && d1 == 0);
    if (level < visLevel0 || level - visLevel0 > 2u) return true;
    uint base = 16u + (level - visLevel0) * 4u;
    if (d0 > 0 && (visWord & (1u << base)) == 0u) return false;
    if (d0 < 0 && (visWord & (1u << (base + 1u))) == 0u) return false;
    if (d1 > 0 && (visWord & (1u << (base + 2u))) == 0u) return false;
    if (d1 < 0 && (visWord & (1u << (base + 3u))) == 0u) return false;
    return true;
}

// One level's cells on p's surface (the tangent plane's four, bilinear, weighed by what each holds), as
// rtgi_readout.frag's readLevel: x-z the light, w the weight; sky, the near visibility and the far share beside.
vec4 fixLevel(vec3 p, uint side, uint level, out float sky, out float near, out float far) {
    vec4 params = u.uCacheParams;
    float size = rtgiSize(level, params);
    uint axis = side >> 1u, a0 = (axis + 1u) % 3u, a1 = (axis + 2u) % 3u;
    vec3 f = (p - RTGI_ORIGIN) / size - 0.5;
    ivec3 c0 = ivec3(floor(f));
    vec3 t = f - vec3(c0);
    int cAxis = int(floor((p[axis] - RTGI_ORIGIN[axis]) / size));
    vec3 e = vec3(0.0);
    float w = 0.0;
    sky = 0.0; near = 0.0; far = 0.0;
    for (int k = 0; k < 4; k++) {
        ivec3 cc = c0;
        cc[a0] += k & 1;
        cc[a1] += k >> 1;
        cc[axis] = cAxis;
        if (!neighbourSeen(p, cc, level, a0, a1, size)) continue;
        uint s = rtgiFind(rtgiCellHash(cc, level, side, params), params);
        if (s == 0xFFFFFFFFu) continue;
        uvec4 a = rtgiRecord[2u * s], c = rtgiRecord[2u * s + 1u];
        float held = uintBitsToFloat(c.w);
        if (held < 0.5) continue;
        float wk = ((k & 1) == 1 ? t[a0] : 1.0 - t[a0]) * ((k >> 1) == 1 ? t[a1] : 1.0 - t[a1]) * clamp(held / 32.0, 1.0 / 32.0, 1.0);
        vec2 rg = unpackHalf2x16(a.x), bs = unpackHalf2x16(a.y);
        e += vec3(rg, bs.x) * wk;
        sky += bs.y * wk;
        near += unpackHalf2x16(c.y).y * wk;
        far += unpackHalf2x16(c.z).x * wk;
        w += wk;
    }
    if (w > 0.0) { e /= w; sky /= w; near /= w; far /= w; }
    return vec4(e, w);
}

// The fix-up: the pixel's own light from the cache at full resolution (its level and the next, blended as the readout
// blends them; the cooked near field over it), where the upsample had none of its surface.
vec4 fixUp(vec3 p, vec3 n) {
    vec4 params = u.uCacheParams;
    uint side = rtgiSide(n);
    rtgiRegion = nearRegion(p, side);
    float lf = rtgiLevelF(p, u.uCamera.xyz, params);
    uint level = uint(lf);
    float t = lf - float(level);
    fixVis(p, side, level);
    float s0, s1, n0, n1, f0, f1;
    vec4 e0 = fixLevel(p, side, level, s0, n0, f0);
    vec4 e1 = level < uint(params.w) ? fixLevel(p, side, level + 1u, s1, n1, f1) : vec4(0.0);
    vec3 e; float sky, nearCell, farShare;
    if (e0.w >= 0.05 && e1.w >= 0.05) { e = mix(e0.rgb, e1.rgb, t); sky = mix(s0, s1, t); nearCell = mix(n0, n1, t); farShare = mix(f0, f1, t); }
    else if (e0.w >= 0.05) { e = e0.rgb; sky = s0; nearCell = n0; farShare = f0; }
    else if (e1.w >= 0.05) { e = e1.rgb; sky = s1; nearCell = n1; farShare = f1; }
    else {
        vec4 e2 = fixLevel(p, side, min(level + 2u, uint(params.w)), sky, nearCell, farShare);
        e = e2.rgb;
        if (e2.w < 0.05) { e = vec3(-1.0); sky = 1.0; }
    }
    if (NEAR_PARAMS.w > 0.5) {
        float size = NEAR_PARAMS.x;
        uint axis = side >> 1u, a0 = (axis + 1u) % 3u, a1 = (axis + 2u) % 3u;
        vec3 f = (p - RTGI_ORIGIN) / size - 0.5;
        ivec3 c0 = ivec3(floor(f));
        vec3 tt = f - vec3(c0);
        int cAxis = int(floor((p[axis] - RTGI_ORIGIN[axis]) / size));
        vec4 acc = vec4(0.0);
        float found = 0.0;
        for (int k = 0; k < 4; k++) {
            ivec3 cc = c0;
            cc[a0] += k & 1;
            cc[a1] += k >> 1;
            cc[axis] = cAxis;
            uint i = nearFind(cc, side);
            if (i == 0xFFFFFFFFu || !nearLinked(rtgiRegion - 1u, i, side)) continue;
            vec4 nl = nearLight[i];
            if (nl.w < 0.5) continue;
            float w = ((k & 1) == 1 ? tt[a0] : 1.0 - tt[a0]) * ((k >> 1) == 1 ? tt[a1] : 1.0 - tt[a1]);
            acc += nl * w;
            found += w;
        }
        vec4 nc = found > 0.05 ? acc / found : vec4(0.0);
        if (found > 0.05 && NEAR_PARAMS.w > 1.5) e = e.r < -0.5 ? nc.rgb : mix(e, nc.rgb, clamp(found, 0.0, 1.0));
        else if (found > 0.05 && e.r >= -0.5)
            e = mix(e, (nc.w - 1.0) * e * farShare / max(nearCell, 0.05) + nc.rgb, clamp(found, 0.0, 1.0));
    }
    if (e.r < -0.5) return vec4(0.0, 0.0, 0.0, 1.0);
    return vec4(e, sky);
}

float lum(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }
vec3 heat(float t) {
    t = clamp(t, 0.0, 1.0);
    return clamp(vec3(1.5 - abs(4.0 * t - 3.0), 1.5 - abs(4.0 * t - 2.0), 1.5 - abs(4.0 * t - 1.0)), 0.0, 1.0);
}

void main() {
    ivec2 q = ivec2(gl_FragCoord.xy);
    vec4 raw = texelFetch(uRaw, q, 0);
    outIncidentGradient = texelFetch(uRawGradient, q, 0);
    float depth = texelFetch(uSceneDepth, q, 0).r;
    if (raw.r < -0.5 && depth < 1.0 - 1e-6) {
        vec2 ndcF = (vec2(q) + 0.5) / vec2(textureSize(uSceneDepth, 0)) * 2.0 - 1.0;
        vec4 wf = u.uInvViewProj * vec4(ndcF, depth, 1.0);
        vec4 ns = texelFetch(uPrepassNormal, q, 0);
        vec3 nf = dot(ns.xyz, ns.xyz) > 1e-6 ? normalize(ns.xyz) : vec3(0.0, 1.0, 0.0);
        raw = fixUp(wf.xyz / wf.w, nf);
        int dv = int(u.uDebug.x + 0.5);
        if (dv == 22) raw = vec4(1.0, 0.0, 1.0, 1.0);
        else if ((dv >= 11 && dv <= 21) || dv == 24) raw = vec4(0.0, 0.0, 0.0, 1.0);
    }
    ivec2 size = textureSize(uSceneDepth, 0);
    float rawLum = lum(raw.rgb);
    if (depth >= 1.0 - 1e-6) { outIncident = raw; outState = vec4(0.0); outFast = vec4(raw.rgb, rawLum * rawLum); return; }
    vec2 ndc = (vec2(q) + 0.5) / vec2(size) * 2.0 - 1.0;
    vec4 w = u.uInvViewProj * vec4(ndc, depth, 1.0);
    vec3 p = w.xyz / w.w;
    float dist = length(p - u.uCamera.xyz);
    vec4 nsHere = texelFetch(uPrepassNormal, q, 0);
    vec3 nHere = dot(nsHere.xyz, nsHere.xyz) > 1e-6 ? normalize(nsHere.xyz) : vec3(0.0, 1.0, 0.0);
    float held = 0.0;
    vec4 history = raw;
    vec4 fast = vec4(raw.rgb, rawLum * rawLum);
    if (u.uParams.y > 0.5) {
        vec4 clip = u.uPrevViewProj * vec4(p, 1.0);
        if (clip.w > 1e-4) {
            vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
            if (all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)))) {
                vec4 state = texture(uHistoryState, uv);
                float expected = length(p - u.uPrevCamera.xyz);
                // Finite only: the history targets are not written before the first frame that keeps one (the boot's
                // settle draws no lit frame), and their garbage passed the state test now and then -- NaN, mixed in at
                // 15/16, kept every lit pixel black on a still camera until motion dropped the history (the user,
                // Crytek Sponza, 2026-10-29).
                vec4 hist = texture(uHistory, uv), fh = texture(uFastHistory, uv);
                // The same surface's, by its normal too: by the distance alone, at a junction the other wall's history (at
                // the same distance) was taken -- its light carried over the edge, a comb along the crevices testbed's
                // corners, and the opposing faces of a gap trading theirs (2026-10-29).
                vec2 hn = state.zw * 2.0 - 1.0;
                vec3 nPrev = vec3(hn, 1.0 - abs(hn.x) - abs(hn.y));
                float tt = max(-nPrev.z, 0.0);
                nPrev.x += nPrev.x >= 0.0 ? -tt : tt;
                nPrev.y += nPrev.y >= 0.0 ? -tt : tt;
                bool sameFacing = dot(normalize(nPrev), nHere) > 0.9;
                bool finite = !any(isnan(hist)) && !any(isinf(hist)) && !any(isnan(fh)) && !any(isinf(fh)) && !isnan(state.x);
                if (finite && sameFacing && state.y > 0.5 && abs(state.x - expected) < 0.02 * expected + 0.02) {
                    history = hist;
                    fast = fh;
                    held = state.y;
                }
            }
        }
    }
    // The fast history first, then the slow one clamped into its box.
    float wFast = 1.0 / min(held + 1.0, u.uParams.z);
    fast = mix(fast, vec4(raw.rgb, rawLum * rawLum), wFast);
    if (u.uParams.w > 0.0 && held > 0.5) {
        float mu = lum(fast.rgb);
        float sigma = sqrt(max(fast.a - mu * mu, 0.0));
        float hl = lum(history.rgb);
        // Plus 5% of the light: on a steady light the fast history's spread is near nothing, and a box that tight would
        // hold the slow history to the fast one -- six frames of accumulation, its flicker.
        float half_ = u.uParams.w * sigma + 0.05 * mu + 1e-4;
        float lo = max(mu - half_, 0.0), hi = mu + half_;
        float clamped = clamp(hl, lo, hi);
        if (hl > 1e-6 && clamped != hl) history.rgb *= clamped / hl;
    }
    held = min(held + 1.0, u.uParams.x);
    outIncident = mix(history, raw, 1.0 / held);
    int dv2 = int(u.uDebug.x + 0.5);
    // Views: the readout's and the fix-up's shown as they are (no history); 23 the frames the history held.
    if ((dv2 >= 11 && dv2 <= 22) || dv2 == 24) outIncident = raw;
    if (dv2 == 23) outIncident = vec4(heat(min(held - 1.0, 16.0) / 16.0), raw.a);
    outFast = fast;
    vec3 on = nHere / (abs(nHere.x) + abs(nHere.y) + abs(nHere.z));
    vec2 oe = on.z >= 0.0 ? on.xy : (1.0 - abs(on.yx)) * vec2(on.x >= 0.0 ? 1.0 : -1.0, on.y >= 0.0 ? 1.0 : -1.0);
    outState = vec4(dist, held, oe * 0.5 + 0.5);
}
