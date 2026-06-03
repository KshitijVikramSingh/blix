#version 450

// Depth pre-pass for the opaque backdrop. Writes scene depth into a sampleable
// target so the following combined colour pass (opaque + particles) can sample
// it for the soft-particle fade — a colour target can only be written by one
// pass, and a pass can't sample its own depth attachment, so the depth is
// produced here first. Position only; the rasterizer captures gl_FragDepth.
//
// Push: mat4 uModel (0) + mat4 uViewProj (64) = 128 bytes, vertex only.

layout(push_constant) uniform Push {
    mat4 uModel;
    mat4 uViewProj;
};

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

void main() {
    gl_Position = uViewProj * uModel * vec4(inPosition, 1.0);
}
