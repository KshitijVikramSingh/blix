#version 450

// Writes normalized linear distance from the point light into the depth
// buffer. The lit shader reconstructs the same value and compares, so the
// cube face the ray hits doesn't matter — all faces store comparable
// [0,1] distances.

layout(push_constant) uniform PushConstants {
    mat4 uModel;
    mat4 uFaceViewProj;
    vec4 uLightPosFar;  // xyz light position, w far plane
} pc;

layout(location = 0) in vec3 vWorld;

void main() {
    float dist = length(vWorld - pc.uLightPosFar.xyz) / pc.uLightPosFar.w;
    gl_FragDepth = clamp(dist, 0.0, 1.0);
}
