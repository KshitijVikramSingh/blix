#version 450

layout(set = 0, binding = 0) uniform DebugFrame {
    mat4 uViewProjection;
} frame;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inColor;

layout(location = 0) out vec4 vColor;

void main() {
    gl_Position = frame.uViewProjection * vec4(inPosition, 1.0);
    vColor = inColor;
}
