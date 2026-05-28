#version 450

// ImGui overlay vertex shader. Maps ImDrawVert (pixel-space pos + uv + packed
// RGBA) into clip space via a scale/translate push constant supplied per
// frame from the draw data's display rect.

layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aColor;

layout(push_constant) uniform PushConstants {
    vec2 uScale;
    vec2 uTranslate;
} pc;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor;

void main() {
    vUv = aUv;
    vColor = aColor;
    gl_Position = vec4(aPos * pc.uScale + pc.uTranslate, 0.0, 1.0);
}
