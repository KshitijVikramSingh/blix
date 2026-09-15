#version 450

// Point-light cube shadow caster (one of 6 faces per draw). Unlike the
// sun/spot depth-only caster, this records LINEAR DISTANCE from the light
// (in point_shadow.frag) so the lit shader can compare world-space distance
// regardless of which cube face the light→fragment ray lands on.
//
// Push: mat4 uModel @0, mat4 uFaceViewProj @64, vec4 uLightPosFar @128
// (xyz = light world position, w = far plane). Vertex+Fragment visible.

layout(push_constant) uniform PushConstants {
    mat4 uModel;
    mat4 uFaceViewProj;
    vec4 uLightPosFar;
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

layout(location = 0) out vec3 vWorld;

void main() {
    vec4 world = pc.uModel * vec4(inPosition, 1.0);
    vWorld = world.xyz;
    gl_Position = pc.uFaceViewProj * world;
}
