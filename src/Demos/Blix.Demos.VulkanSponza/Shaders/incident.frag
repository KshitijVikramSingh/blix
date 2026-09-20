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
// <b>The normal is reconstructed from depth, and that is a real approximation.</b> Both terms are
// directional — the probe atlas is evaluated in the surface normal's direction and the sky
// visibility is an SH evaluated the same way — so the shading normal would be better. It is not
// available: this pass runs before the lit pass and there is no G-buffer. The geometric normal is
// defensible for a term whose angular content comes from an 8x8 octahedral tile, and the lit pass
// keeps the normal-mapped detail for everything else. Where it will show first is the alpha-cutout
// canopy, whose depth buffer is thousands of disconnected silhouettes and whose reconstructed
// normals are therefore nonsense — the same case gtao_denoise.frag already documents.
//
// <b>Measured, and the approximation that bites is the normal, not the upsample.</b> Against the
// lit pass doing both terms itself, at the same viewpoint with the occupancy march on in both:
//
//   mean |difference|  6.81/255 sRGB,  p95 26,  p99 55
//   32% of pixels differ by more than 8/255, 7.5% by more than 24
//
// Amplified, that difference is not a halo hugging silhouettes — which is what a bad bilateral
// upsample looks like — it is the masonry's own NORMAL MAP, legible across every stone surface. The
// inline path evaluates both terms in the normal-mapped N and this evaluates them in a geometric
// one, so the ambient stops responding to surface relief and flat stone reads flatter.
//
// The fix is not a better upsample, then: it is to stop collapsing the direction here. Sky
// visibility is the cheap half — the volume fetch is the expense and the SH evaluation is a few
// multiplies, so carrying L0+L1 through the field (exactly one RGBA16F) lets the lit pass evaluate
// it with its own shading normal and recovers the relief for nothing. The bounce is the hard half,
// because re-evaluating it at full resolution needs the blended octahedral tile rather than one
// RGB. Not built; the saving below is what buys the right to spend time on it.
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

// <b>The nearer of the two neighbours on each axis, not a fixed pair.</b> A fixed forward
// difference straddles every silhouette and invents a normal halfway between the two surfaces it
// spans — which on a canopy edge points the probe lookup somewhere neither leaf nor background
// faces. Picking whichever neighbour is closer in depth keeps the difference on ONE surface. Same
// reasoning, and the same fix, as gtao.frag's reconstructNormal.
vec3 reconstructViewNormal(vec2 uv, vec3 P) {
    vec2 texel = g.uTarget.zw;
    vec3 xr = viewPos(uv + vec2(texel.x, 0.0)) - P;
    vec3 xl = P - viewPos(uv - vec2(texel.x, 0.0));
    vec3 yu = viewPos(uv + vec2(0.0, texel.y)) - P;
    vec3 yd = P - viewPos(uv - vec2(0.0, texel.y));
    vec3 dx = abs(xr.z) < abs(xl.z) ? xr : xl;
    vec3 dy = abs(yu.z) < abs(yd.z) ? yu : yd;
    vec3 n = cross(dx, dy);
    float len = length(n);
    // Degenerate where the two differences are parallel — a flat run of identical depth at a
    // silhouette, or the far plane. Facing the camera is the harmless answer there.
    return len > 1e-8 ? n / len : vec3(0.0, 0.0, 1.0);
}

void main() {
    vec3 P = viewPos(vUv);

    // Sky: nothing to gather, and reconstructing a normal from a flat far plane produces noise that
    // the upsample would then drag onto the silhouettes next to it. Full visibility, zero bounce.
    if (texture(uSceneDepth, vUv).r >= 1.0 - 1e-6) {
        outIncident = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    vec3 nView = reconstructViewNormal(vUv, P);
    vec3 worldPos = (g.uInvView * vec4(P, 1.0)).xyz;
    vec3 N = normalize((g.uInvView * vec4(nView, 0.0)).xyz);

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
