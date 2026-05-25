#version 450

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    mat4 uModel;
} frame;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inColor;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec3 vWorldPos;

void main() {
    vec4 world = frame.uModel * vec4(inPosition, 1.0);
    gl_Position = frame.uViewProjection * world;
    vColor = inColor;
    vWorldPos = world.xyz;
}
