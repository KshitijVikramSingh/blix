#version 450

// The incident-light field: what arrives at a surface from the probe volumes, at half resolution.
//
// Evaluate low-frequency probe reconstruction at the field's resolution rather than once per
// display pixel. The recorded 48x27x32 probe and 256x141x168 occupancy volumes spent 8.69 ms on
// bounce plus sky reconstruction in the full-resolution lit pass, with occupancy visibility adding
// 6.46 ms. Repeating those queries across neighbouring Retina pixels cannot recover more volume
// detail.
//
// What DOES belong at full resolution stays there: albedo, normal mapping, the specular lobe, the
// specular IBL, shadow sampling. This pass moves the low-frequency half of the ambient and nothing
// else, which is why it writes incident RADIANCE rather than a finished colour — the lit pass still
// multiplies by its own per-pixel albedo, metalness and occlusion.
//
// Written as rgb = bounced incident radiance, a = sky visibility. One RGBA16F at half resolution
// replaces two volume reconstructions per full-res pixel.
//
// Both terms use the geometric world normal written by the depth pre-pass. Inferring it from depth
// measured 5.31 mean sRGB from the inline path and did not improve at full resolution; omitting the
// material normal map accounts for 0.90 of that difference. The remaining approximation avoids
// duplicating normal-map sampling and TBN work.
//
// Recorded half-resolution error against inline evaluation, with occupancy visibility enabled and
// a 0.06 mean-sRGB noise floor:
//
//   both terms                         5.20 mean,  p95 21,  27.8% of pixels past 8/255
//   sky visibility alone               2.73 mean,  p95 11,  12.1%
//   (so the bounce carries the rest)
//
// Store evaluated sky visibility rather than interpolated SH coefficients. Carrying L0/L1 to full
// resolution increased the sky-term error from 2.73 to 3.69 mean sRGB, while omitted L2 measured
// only 0.36. The dominant residual is positional: a coarse texel centre can reconstruct a different
// world point from the fine pixel. Tune incidentScale or the sampling position, not the payload.
// --no-incident retains the inline reference and the host records the paired timing comparison.

#include "fullscreen.glsl"
#include "probe_volume.glsl"
#include "sky_visibility.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;

layout(set = 0, binding = 0) uniform Incident {
    mat4 uInvProjection;   // clip -> view, for unprojecting depth
    mat4 uInvView;         // view -> world
    vec4 uTarget;          // xy = this target's size in pixels, zw = 1/size
    vec4 uSkyMin;          // xyz sky/probe volume min, w = 1 when the volume is loaded
    vec4 uSkyScale;        // xyz = 1/(max-min), w = normal push in metres
    vec4 uBounceDims;      // xyz bounce probe counts
    vec4 uOccupancyDims;   // xyz occupancy dims, w = occlusion strength
    // x = bounce strength (0 disables the probe term), y = tetrahedral reconstruction,
    // z unused, w unused.
    vec4 uParams;
} g;

layout(set = 0, binding = 1) uniform sampler2D uSceneDepth;
// Interpolated geometric world normal from the depth pre-pass; see depth_prepass.frag.
layout(set = 0, binding = 8) uniform sampler2D uPrepassNormal;
layout(set = 0, binding = 2) uniform sampler2D uSkyBounce;
layout(set = 0, binding = 3) uniform sampler2D uSkyBounceDepth;
layout(set = 0, binding = 4) uniform sampler3D uSkyVisibility;
layout(set = 0, binding = 5) uniform sampler3D uSkyVisibility1;
layout(set = 0, binding = 6) uniform sampler3D uSkyVisibility2;
layout(set = 0, binding = 7) uniform sampler3D uOccupancy;

// View-space position from the depth buffer.
vec3 viewPos(vec2 uv) {
    float raw = texture(uSceneDepth, uv).r;
    vec4 view = g.uInvProjection * vec4(uv * 2.0 - 1.0, raw, 1.0);
    return view.xyz / view.w;
}

void main() {
    vec3 P = viewPos(vUv);

    // Sky: nothing to gather, and reconstructing a normal from a flat far plane produces noise that
    // the upsample would then drag onto the silhouettes next to it. Full visibility, zero bounce.
    if (texture(uSceneDepth, vUv).r >= 1.0 - 1e-6) {
        outIncident = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    vec3 worldPos = (g.uInvView * vec4(P, 1.0)).xyz;
    // Already world-space and already flipped for back faces, which is the half a depth buffer
    // cannot supply at all: a two-sided curtain's far side needs the hemisphere it faces.
    vec4 nSample = texture(uPrepassNormal, vUv);
    // Length, not alpha: the resolve under MSAA averages directions, and a texel no geometry
    // reached stays at the cleared zero. Either way the only safe answer is to face the camera.
    vec3 N = dot(nSample.xyz, nSample.xyz) > 1e-6
        ? normalize(nSample.xyz)
        : normalize((g.uInvView * vec4(0.0, 0.0, 1.0, 0.0)).xyz);

    float skyVisibility = 1.0;
    if (g.uSkyMin.w > 0.5) {
        BlixSkySample s = blix_skyFetch(uSkyVisibility, uSkyVisibility1, uSkyVisibility2,
                                        g.uSkyMin.xyz, g.uSkyScale.xyz, worldPos);
        skyVisibility = blix_skyEvaluate(s, N);
    }

    vec3 incident = vec3(0.0);
    if (g.uParams.x > 0.5) {
        float confidence;
        incident = blix_probeIrradianceEx(
            uSkyBounce, uSkyBounceDepth, uOccupancy, ivec3(g.uBounceDims.xyz),
            ivec3(g.uOccupancyDims.xyz), g.uSkyMin.xyz, 1.0 / g.uSkyScale.xyz,
            worldPos, N, g.uParams.y > 0.5, g.uOccupancyDims.w, confidence);
    }

    outIncident = vec4(incident, skyVisibility);
}
