#version 450

// Samples the env cube at mip 0 (sharpest sky), outputs linear
// HDR. The present pass tonemaps to the swapchain. Mirrors the lit pass's
// set-1 layout so both share the per-pass IBL bindings via one descriptor
// set; this shader reads the raw environment and froxel grid while the other declarations preserve
// descriptor-layout compatibility.

layout(set = 1, binding = 0) uniform samplerCube uIrradiance;
layout(set = 1, binding = 1) uniform samplerCube uPrefilteredEnv;
layout(set = 1, binding = 2) uniform sampler2D   uBrdfLut;
// Declared only to keep set 1 layout-compatible with the lit pipeline in the
// same pass (the sky never samples the shadow cascades).
layout(set = 1, binding = 3) uniform sampler2D   uCascadeShadowMaps[3];
// Background presentation samples the raw environment at mip 0. The prefiltered cube is reserved
// for material specular and is both convolved and lower-resolution.
layout(set = 1, binding = 10) uniform samplerCube uEnvCube;

// The froxel scattering grid, at the same set-1 slot the lit pass reads it from — the two
// pipelines share this descriptor set, which is why the sky can sample it without any new binding.
layout(set = 1, binding = 4) uniform sampler3D uFroxelGrid;

layout(location = 0) in vec3 vWorldDir;
layout(location = 1) in vec2 vScreenUv;
layout(location = 2) in vec4 vFog;
layout(location = 0) out vec4 outColor;

void main() {
    vec3 dir = normalize(vWorldDir);
    vec3 sky = textureLod(uEnvCube, dir, 0.0).rgb;

    // The sky lies beyond fog far, so sample the fully integrated final slice. This keeps the
    // background and geometry under the same medium instead of leaving a clear horizon.
    if (vFog.w > 0.5) {
        vec4 fog = texture(uFroxelGrid, vec3(vScreenUv, 1.0));
        sky = sky * fog.a + fog.rgb;
    }

    outColor = vec4(sky, 1.0);
}
