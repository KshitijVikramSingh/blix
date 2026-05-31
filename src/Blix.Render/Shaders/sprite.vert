#version 450

// SpriteBatch vertex shader. The view-projection arrives as a vertex-stage
// push constant (set-less, per-draw). Depth is disabled for sprites and draw
// order is decided CPU-side by the sort mode, so we force gl_Position.z = 0:
// that keeps every quad inside Vulkan's [0,1] clip range regardless of the
// caller's near/far, sidestepping depth-range surprises in 2D.

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aTexCoord;
layout(location = 2) in vec4 aColor;

layout(location = 0) out vec2 vTexCoord;
layout(location = 1) out vec4 vColor;

layout(push_constant) uniform Push {
    mat4 uViewProjection;
} pc;

void main()
{
    vTexCoord = aTexCoord;
    vColor = aColor;
    vec4 clip = pc.uViewProjection * vec4(aPosition, 1.0);
    gl_Position = vec4(clip.xy, 0.0, 1.0);
}
