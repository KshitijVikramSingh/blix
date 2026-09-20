#version 450

// The incident-light field: what arrives at a surface from the probe volumes, at half resolution.
//
// <b>Do expensive spatial reasoning at the frequency of the information, not the frequency of the
// display.</b> The two terms computed here — bounced radiance from the probe atlas, and baked sky
// visibility — are both reconstructions of a volume whose own resolution is 48x27x32 probes and
// 256x141x168 occupancy cells. A 2880x1620 framebuffer asks that volume the same question about
// four Retina pixels that all land inside one probe cell, four times, independently. Measured, the
// pair costs 8.69 ms of a 19 ms lit pass, and the occupancy line-of-sight test that finally closes
// the leak (see probe_volume.glsl) costs 6.46 ms more on top. None of that buys a single pixel of
// detail the volume is capable of carrying.
//
// What DOES belong at full resolution stays there: albedo, normal mapping, the specular lobe, the
// specular IBL, shadow sampling. This pass moves the low-frequency half of the ambient and nothing
// else, which is why it writes incident RADIANCE rather than a finished colour — the lit pass still
// multiplies by its own per-pixel albedo, metalness and occlusion.
//
// Written as rgb = bounced incident radiance, a = sky visibility. One RGBA16F at half resolution
// replaces two volume reconstructions per full-res pixel.
//
// <b>The normal comes from the depth pre-pass, which is the whole story of this pass's accuracy.</b>
// Both terms are directional, and the first version inferred the normal from the depth buffer
// because there was no G-buffer to ask. That measured 5.31 mean sRGB against the lit pass and did
// not improve at full resolution — it was never a sampling-rate error. Splitting it showed the
// normal MAP's entire contribution to the ambient is 0.90, so the missing 4.4 was the inference
// failing wherever depth is not a smooth height field: the canopy, two-sided cloth, silhouettes.
//
// The pre-pass already rasterises all of it and already holds the interpolated normal, so it now
// writes one. What is still approximated here is only the normal map, which is worth that 0.90.
//
// <b>What it costs, against the lit pass doing both terms itself.</b> Same viewpoint, occupancy
// march on in both, noise floor 0.06 mean sRGB:
//
//   both terms                         5.20 mean,  p95 21,  27.8% of pixels past 8/255
//   sky visibility alone               2.73 mean,  p95 11,  12.1%
//   (so the bounce carries the rest)
//
// <b>And the obvious fix was built, measured, and lost.</b> The story the difference image tells is
// that the ambient stops responding to the masonry's normal map — this pass has only a geometric
// normal from depth, where the lit pass has the mapped one. sh0 in sky_visibility.glsl IS
// (L0, L1x, L1y, L1z), so carrying those four numbers in a second target and evaluating them at
// full resolution in the shading normal costs one RGBA16F pair and drops only L2, which measures at
// 0.36 mean — nearly nothing.
//
// It made the sky term WORSE: 3.69 against 2.73. Interpolating coefficients across the coarse grid
// and then evaluating is worse than interpolating the evaluated scalar, which is clamped to [0,1]
// and low-dynamic-range where the coefficients are neither, and the L1 dot product amplifies what
// the interpolation got wrong before the clamp truncates it asymmetrically.
//
// So the error here is not a direction error and no richer basis fixes it. It is a POSITION error:
// the coarse pass asks the volume where the half-res texel centre landed, and on a surface running
// away from the camera that is metres from where the fine pixel sits. The levers are the sampling
// position and incidentScale, not the payload.
//
// What it saves, paired and interleaved on the measurement orbit:
//
//   occupancy march off:  -7.04 ms   (the shipped configuration today)
//   occupancy march on:  -12.72 ms   (so the leak fix stops costing anything at all)

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
// The interpolated world normal the depth pre-pass wrote. See depth_prepass.frag: reconstructing
// this from depth measured 5.31 mean sRGB against the lit pass, and the normal map's entire
// contribution to the ambient measured 0.90 — so the error was the inference, not the detail.
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
