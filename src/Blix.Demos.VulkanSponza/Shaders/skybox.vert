#version 450

// Fullscreen-triangle skybox vertex shader. Outputs a world-space direction
// reconstructed from the clip-space corner positions; the fragment shader
// uses that direction to sample the env cube. Placed at z = w so the post-
// perspective-divide depth lands at 1.0 (max depth in Vulkan NDC), so the
// pipeline's LessEqual depth test draws sky exactly where the depth buffer
// still carries the clear value.

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    vec3 uSunDirection;
    float uSunIntensity;
    vec3 uAmbientColor;
    float uIblIntensity;
    vec3 uCameraPos;
    float uEnvMipCount;
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

void main() {
    vec2 positions[3] = vec2[3](
        vec2(-1.0, -1.0),
        vec2( 3.0, -1.0),
        vec2(-1.0,  3.0)
    );
    vec2 ndc = positions[gl_VertexIndex];
    // Reconstruct the world-space point on the far plane that maps to this
    // NDC position. World direction from the camera to that point is what
    // we sample the cube by.
    mat4 invVP = inverse(frame.uViewProjection);
    vec4 worldPos = invVP * vec4(ndc, 1.0, 1.0);
    vWorldDir = worldPos.xyz / worldPos.w - frame.uCameraPos;
    // z = w forces post-divide depth = 1.0 (max). LessEqualNoWrite depth
    // state lets the sky pass at depth == clear value but lose to any
    // opaque geometry that wrote a closer depth.
    gl_Position = vec4(ndc, 1.0, 1.0);
}
