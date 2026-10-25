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
layout(location = 2) out vec4 outState;   // x the point's distance from this frame's camera, y frames held
layout(location = 3) out vec4 outFast;    // the fast history: rgb, its luminance's square

layout(set = 0, binding = 0) uniform RtgiTemporal {
    mat4 uInvViewProj;   // this frame's (jittered, as the pre-pass drew), inverted
    mat4 uPrevViewProj;  // last frame's
    vec4 uCamera;        // xyz this frame's position
    vec4 uPrevCamera;    // xyz last frame's position
    vec4 uParams;        // x frames held at most; y 1 when the history may be read; z the fast history's frames; w the
                         // clamp's standard deviations (0: no clamp)
} u;

layout(set = 0, binding = 1) uniform sampler2D uRaw;
layout(set = 0, binding = 2) uniform sampler2D uRawGradient;
layout(set = 0, binding = 3) uniform sampler2D uSceneDepth;
layout(set = 0, binding = 4) uniform sampler2D uHistory;
layout(set = 0, binding = 5) uniform sampler2D uHistoryState;
layout(set = 0, binding = 6) uniform sampler2D uFastHistory;

float lum(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }

void main() {
    ivec2 q = ivec2(gl_FragCoord.xy);
    vec4 raw = texelFetch(uRaw, q, 0);
    outIncidentGradient = texelFetch(uRawGradient, q, 0);
    float depth = texelFetch(uSceneDepth, q, 0).r;
    ivec2 size = textureSize(uSceneDepth, 0);
    float rawLum = lum(raw.rgb);
    if (depth >= 1.0 - 1e-6) { outIncident = raw; outState = vec4(0.0); outFast = vec4(raw.rgb, rawLum * rawLum); return; }
    vec2 ndc = (vec2(q) + 0.5) / vec2(size) * 2.0 - 1.0;
    vec4 w = u.uInvViewProj * vec4(ndc, depth, 1.0);
    vec3 p = w.xyz / w.w;
    float dist = length(p - u.uCamera.xyz);
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
                if (state.y > 0.5 && abs(state.x - expected) < 0.02 * expected + 0.02) {
                    history = texture(uHistory, uv);
                    fast = texture(uFastHistory, uv);
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
    outFast = fast;
    outState = vec4(dist, held, 0.0, 0.0);
}
