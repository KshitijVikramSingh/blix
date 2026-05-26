#version 450

// Sun shadow pass — depth-only render from the sun's POV.
//
// Set 0 binding 0 = per-frame UBO (shared with lit pass). We sample
// only uSunShadowVP here; the rest of the UBO is dead-stripped from
// this shader's SPIR-V but the descriptor write still happens.
layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    vec3 uSunDirection;
    float uSunIntensity;
    vec3 uAmbientColor;
    float uAmbientIntensity;
    mat4 uSunShadowVP;
} frame;

layout(push_constant) uniform PushConstants {
    mat4 uModel;
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

void main() {
    gl_Position = frame.uSunShadowVP * pc.uModel * vec4(inPosition, 1.0);
}
