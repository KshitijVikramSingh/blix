#version 450

// <b>A PUSH CONSTANT, not a uniform block, and the difference is the whole bug.</b>
//
// A shader program owns one per-frame uniform buffer per frame slot. The line drawer has one
// program and draws every declared view through it, so when each view passed its own
// uViewProjection as an inline uniform they all wrote the same 64 bytes — and since host writes
// happen while commands are RECORDED while the GPU reads at EXECUTION, the last view's matrix won
// for every debug draw in the frame. A second view therefore drew the first view's geometry through
// the second view's camera: a grid at a wrong angle and skeletons floating off their bodies.
//
// Push payloads are copied at record time (RenderCommand.cs says so in as many words), so each draw
// carries its own matrix and any number of views is fine. It fits easily: 64 bytes against the
// 128-byte minimum every Vulkan implementation guarantees.
//
// The drawer already reasoned about exactly this lifetime for its VERTEX buffer — that is why it
// accumulates every view into one buffer and submits ranges rather than clearing between views. The
// uniform had the same problem and was left inline, which is what a near-miss looks like.
layout(push_constant) uniform Push {
    mat4 uViewProjection;
};

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inColor;

layout(location = 0) out vec4 vColor;

void main() {
    gl_Position = uViewProjection * vec4(inPosition, 1.0);
    vColor = inColor;
}
