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

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;
layout(location = 1) out vec4 outIncidentGradient;
layout(location = 2) out vec4 outState;   // x the point's distance from this frame's camera, y frames held

layout(set = 0, binding = 0) uniform RtgiTemporal {
    mat4 uInvViewProj;   // this frame's (jittered, as the pre-pass drew), inverted
    mat4 uPrevViewProj;  // last frame's
    vec4 uCamera;        // xyz this frame's position
    vec4 uPrevCamera;    // xyz last frame's position
    vec4 uParams;        // x frames held at most; y 1 when the history may be read
} u;

layout(set = 0, binding = 1) uniform sampler2D uRaw;
layout(set = 0, binding = 2) uniform sampler2D uRawGradient;
layout(set = 0, binding = 3) uniform sampler2D uSceneDepth;
layout(set = 0, binding = 4) uniform sampler2D uHistory;
layout(set = 0, binding = 5) uniform sampler2D uHistoryState;

void main() {
    ivec2 q = ivec2(gl_FragCoord.xy);
    vec4 raw = texelFetch(uRaw, q, 0);
    outIncidentGradient = texelFetch(uRawGradient, q, 0);
    float depth = texelFetch(uSceneDepth, q, 0).r;
    ivec2 size = textureSize(uSceneDepth, 0);
    if (depth >= 1.0 - 1e-6) { outIncident = raw; outState = vec4(0.0); return; }
    vec2 ndc = (vec2(q) + 0.5) / vec2(size) * 2.0 - 1.0;
    vec4 w = u.uInvViewProj * vec4(ndc, depth, 1.0);
    vec3 p = w.xyz / w.w;
    float dist = length(p - u.uCamera.xyz);
    float held = 0.0;
    vec4 history = raw;
    if (u.uParams.y > 0.5) {
        vec4 clip = u.uPrevViewProj * vec4(p, 1.0);
        if (clip.w > 1e-4) {
            vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
            if (all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)))) {
                vec4 state = texture(uHistoryState, uv);
                float expected = length(p - u.uPrevCamera.xyz);
                if (state.y > 0.5 && abs(state.x - expected) < 0.02 * expected + 0.02) {
                    history = texture(uHistory, uv);
                    held = state.y;
                }
            }
        }
    }
    held = min(held + 1.0, u.uParams.x);
    outIncident = mix(history, raw, 1.0 / held);
    outState = vec4(dist, held, 0.0, 0.0);
}
