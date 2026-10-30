#version 450

// Samples the env cube at mip 0 (sharpest sky), outputs linear
// HDR. The present pass tonemaps to the swapchain.
//
// Declares only the texture it reads. It used to restate the lit pass's set-1 layout so both
// could share one descriptor set; descriptor sets are per draw now, and each program is handed the
// textures it declares by name (SponzaLoop.Setup's skyBindings), so there is nothing to be
// compatible with. The binding number matches the lit pass's only because the texture is the same.

// Background presentation samples the raw environment at mip 0. The prefiltered cube is reserved
// for material specular and is both convolved and lower-resolution.
layout(set = 1, binding = 10) uniform samplerCube uEnvCube;

layout(location = 0) in vec3 vWorldDir;
layout(location = 0) out vec4 outColor;

void main() {
    vec3 dir = normalize(vWorldDir);
    outColor = vec4(textureLod(uEnvCube, dir, 0.0).rgb, 1.0);
}
