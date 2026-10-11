#version 450

// RenderSettings.HalfResLighting: the incident light (incident_clipmap.frag) and the traced sun shadows
// (rt_shadow_rays.comp) ran at half resolution, each half-res pixel h standing for ONE full-res pixel of its 2x2,
// 2h + uHalf.yz, a different one each frame (all four every four frames, TAA gathering them). This brings both back
// to full resolution for the lit pass, into the targets it reads either way.
//
// A full-res pixel q takes the four half-res samples around it, each weighted by
//   - where its pixel is: bilinear over the 2-pixel grid the samples' pixels sit on (q's own sample, when q is the
//     pixel this frame's sample stands for, takes all the weight);
//   - whether it is on q's surface: its point's distance from q's tangent plane, against a tolerance that grows with
//     the distance from the camera, and its normal against q's -- so light never crosses a silhouette or a crease;
//   - sky samples none.
// Where no neighbour is on q's surface (a thin wire, a leaf's edge), the one nearest q's plane answers alone, rather
// than a blend of surfaces q is not on.
//
// The sun's visibility rides in the gradient's w (the lit pass reads it there when frame.uIncident.w says so, see
// lit.frag rtSunVisibility): each sample's traced word, 1 where the sun reaches, weighted the same way, so a shadow's
// edge is as soft as the upsample makes it and no softer.

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;
layout(location = 1) out vec4 outIncidentGradient;

layout(set = 0, binding = 0) uniform LightingUpsample {
    mat4 uInvProjection;   // the jittered projection the pre-pass drew with, inverted
    mat4 uInvView;
    vec4 uHalf;            // yz: the full-res pixel of its 2x2 this frame's half-res samples stand for
} u;

layout(set = 0, binding = 1) uniform sampler2D uIncidentHalf;
layout(set = 0, binding = 2) uniform sampler2D uIncidentGradientHalf;
layout(set = 0, binding = 3) uniform sampler2D uSceneDepth;
layout(set = 0, binding = 4) uniform sampler2D uPrepassNormal;
// The half-res traced mask (SponzaLoop.RtShadows): width, height, 1 while live (0 for the placeholder: no traced
// shadows, the lit pass asks the cascades and reads no w), 1 with sky rays; then a word per half-res pixel.
layout(std430, set = 0, binding = 5) readonly buffer RtShadowMask { uvec4 rtShadowHeader; uint rtShadowWords[]; };

vec3 worldAt(ivec2 pixel, float raw, vec2 fullSize) {
    vec2 uv = (vec2(pixel) + 0.5) / fullSize;
    vec4 view = u.uInvProjection * vec4(uv * 2.0 - 1.0, raw, 1.0);
    return (u.uInvView * vec4(view.xyz / view.w, 1.0)).xyz;
}

// 1 where this frame's ray from half-res pixel h reached the sun. Not live yet (a size of zero): the sun is off, as
// lit.frag has it.
float sunAt(ivec2 h) {
    if (rtShadowHeader.z == 0u) return 1.0;
    if (uint(h.x) >= rtShadowHeader.x || uint(h.y) >= rtShadowHeader.y) return 0.0;
    return (rtShadowWords[uint(h.y) * rtShadowHeader.x + uint(h.x)] & 1u) != 0u ? 0.0 : 1.0;
}

void main() {
    ivec2 fullSize = textureSize(uSceneDepth, 0);
    ivec2 halfSize = textureSize(uIncidentHalf, 0);
    ivec2 q = clamp(ivec2(gl_FragCoord.xy), ivec2(0), fullSize - 1);
    float rawQ = texelFetch(uSceneDepth, q, 0).r;
    if (rawQ >= 1.0 - 1e-6) {
        // Sky: as the incident pass leaves it.
        outIncident = vec4(0.0, 0.0, 0.0, 1.0);
        outIncidentGradient = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }
    vec3 pQ = worldAt(q, rawQ, vec2(fullSize));
    vec4 nSampleQ = texelFetch(uPrepassNormal, q, 0);
    vec3 cameraPos = (u.uInvView * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    vec3 nQ = dot(nSampleQ.xyz, nSampleQ.xyz) > 1e-6 ? normalize(nSampleQ.xyz) : normalize(cameraPos - pQ);
    // How far off q's plane a point may sit and still be q's surface: depth precision and the pre-pass's own
    // facets, both growing with the distance.
    float tolerance = 0.005 + 0.004 * length(pQ - cameraPos);

    ivec2 offset = ivec2(u.uHalf.yz);
    ivec2 base = ivec2(floor(vec2(q - offset) * 0.5));
    vec4 incident = vec4(0.0), gradient = vec4(0.0);
    float total = 0.0;
    float bestPlane = 1e30;
    vec4 bestIncident = vec4(0.0, 0.0, 0.0, 1.0), bestGradient = vec4(0.0, 0.0, 0.0, 1.0);
    for (int k = 0; k < 4; ++k) {
        ivec2 h = clamp(base + ivec2(k & 1, k >> 1), ivec2(0), halfSize - 1);
        ivec2 rep = min(h * 2 + offset, fullSize - 1);
        float raw = texelFetch(uSceneDepth, rep, 0).r;
        if (raw >= 1.0 - 1e-6) continue;
        vec2 d = abs(vec2(rep - q)) * 0.5;
        float wPixel = max(1.0 - d.x, 0.0) * max(1.0 - d.y, 0.0);
        vec3 p = worldAt(rep, raw, vec2(fullSize));
        float plane = abs(dot(nQ, p - pQ));
        vec4 nSample = texelFetch(uPrepassNormal, rep, 0);
        float facing = dot(nSample.xyz, nSample.xyz) > 1e-6 ? max(dot(normalize(nSample.xyz), nQ), 0.0) : 0.0;
        float wSurface = exp(-(plane * plane) / (tolerance * tolerance)) * pow(facing, 8.0);
        vec4 sampleIncident = texelFetch(uIncidentHalf, h, 0);
        vec4 sampleGradient = vec4(texelFetch(uIncidentGradientHalf, h, 0).xyz, sunAt(h));
        float w = wPixel * wSurface;
        incident += sampleIncident * w;
        gradient += sampleGradient * w;
        total += w;
        // The fallback: nearest q's plane, its normal agreeing breaking ties toward the same surface.
        float score = plane / tolerance + (1.0 - facing) * 4.0;
        if (score < bestPlane) {
            bestPlane = score;
            bestIncident = sampleIncident;
            bestGradient = sampleGradient;
        }
    }
    if (total > 1e-4) {
        outIncident = incident / total;
        outIncidentGradient = gradient / total;
    } else {
        outIncident = bestIncident;
        outIncidentGradient = bestGradient;
    }
}
