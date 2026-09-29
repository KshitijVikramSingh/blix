#version 450

// Samples the env cube at mip 0 (sharpest sky), outputs linear
// HDR. The present pass tonemaps to the swapchain.
//
// Declares only the two textures it reads. It used to restate the lit pass's set-1 layout so both
// could share one descriptor set; descriptor sets are per draw now, and each program is handed the
// textures it declares by name (SponzaLoop.Setup's skyBindings), so there is nothing to be
// compatible with. Binding numbers match the lit pass's only because the textures are the same.

// Background presentation samples the raw environment at mip 0. The prefiltered cube is reserved
// for material specular and is both convolved and lower-resolution.
layout(set = 1, binding = 10) uniform samplerCube uEnvCube;

// The froxel scattering grid, the same texture the lit pass fogs with, so the sky and the surfaces
// in front of it are fogged by one field.
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
