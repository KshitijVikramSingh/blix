#version 450

// Samples the env cube at mip 0 (sharpest sky), outputs linear
// HDR. The present pass tonemaps to the swapchain. Mirrors the lit pass's
// set-1 layout so both share the per-pass IBL bindings via one descriptor
// set; only uPrefilteredEnv is actually read.

layout(set = 1, binding = 0) uniform samplerCube uIrradiance;
layout(set = 1, binding = 1) uniform samplerCube uPrefilteredEnv;
layout(set = 1, binding = 2) uniform sampler2D   uBrdfLut;
// Declared only to keep set 1 layout-compatible with the lit pipeline in the
// same pass (the sky never samples the shadow cascades).
layout(set = 1, binding = 3) uniform sampler2D   uCascadeShadowMaps[3];
// <b>The sky, not the sky convolved for specular.</b> This used to read uPrefilteredEnv, whose
// mip 0 is a 128px roughness-0 convolution rather than the 256px cube the cook bakes — so the
// background was a soft, out-of-focus version of itself, most visible through a window where the
// eye has a hard frame to compare against.
layout(set = 1, binding = 10) uniform samplerCube uEnvCube;

layout(location = 0) in vec3 vWorldDir;
layout(location = 0) out vec4 outColor;

void main() {
    vec3 dir = normalize(vWorldDir);
    vec3 sky = textureLod(uEnvCube, dir, 0.0).rgb;
    outColor = vec4(sky, 1.0);
}
