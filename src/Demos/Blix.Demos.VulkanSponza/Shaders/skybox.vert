#version 450

#include "fullscreen.glsl"

// Fullscreen-triangle skybox vertex shader. Outputs a world-space direction
// reconstructed from the clip-space corner positions; the fragment shader
// uses that direction to sample the env cube. Placed at z = w so the post-
// perspective-divide depth lands at 1.0 (max depth in Vulkan NDC), so the
// pipeline's LessEqual depth test draws sky exactly where the depth buffer
// still carries the clear value.

// <b>Declared by offset, and only what is read.</b> This is the same buffer lit.frag describes in
// full; std140 offsets are positional, so a reader may name any subset of it. This one used to
// name a prefix instead — six members, three of which had been RENAMED in lit.frag when the sun
// became a measured irradiance. uSunIntensity and uIblIntensity here were padding floats over
// there, and nothing said so: the declaration compiled, validated and ran, and would have handed
// back a pad to the first line that used it. Naming only what is used removes the drift instead of
// re-synchronising it, and SponzaLoop's AssertFrameBlockAgrees stops the boot if these offsets and
// lit.frag's ever disagree.
layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    layout(offset = 96)  vec3 uCameraPos;
    layout(offset = 432) vec4 uFog;   // x=screenW, y=screenH, z=fogFar, w=enabled(0/1)
} frame;

// Vertex inputs are declared (matching the pipeline's VertexPosition3-
// NormalTexture layout) but ignored — sky positions come from
// gl_VertexIndex. Without these declarations Silk/Vulkan can validate
// the pipeline's vertex bindings against an empty shader interface and
// some drivers behave unpredictably.
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

layout(location = 0) out vec3 vWorldDir;
// Screen UV for the fog lookup. In Vulkan both NDC y and gl_FragCoord y run downward, so this is
// the same coordinate the lit pass forms as gl_FragCoord.xy / screenSize — and the sky can have it
// without needing the framebuffer size at all.
layout(location = 1) out vec2 vScreenUv;
layout(location = 2) out vec4 vFog;

void main() {
    vec2 ndc = blix_fullscreenTriangleNdc(gl_VertexIndex);
    // Reconstruct the world-space point on the far plane that maps to this
    // NDC position. World direction from the camera to that point is what
    // we sample the cube by.
    mat4 invVP = inverse(frame.uViewProjection);
    vec4 worldPos = invVP * vec4(ndc, 1.0, 1.0);
    vWorldDir = worldPos.xyz / worldPos.w - frame.uCameraPos;
    // z = w forces post-divide depth = 1.0 (max). LessEqualNoWrite depth
    // state lets the sky pass at depth == clear value but lose to any
    // opaque geometry that wrote a closer depth.
    vScreenUv = ndc * 0.5 + 0.5;
    vFog = frame.uFog;
    gl_Position = vec4(ndc, 1.0, 1.0);
}
