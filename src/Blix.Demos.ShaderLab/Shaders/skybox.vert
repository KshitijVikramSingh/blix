#version 410 core

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aTexCoord;

out vec3 worldDirection;

uniform mat4 uView;
uniform mat4 uProjection;

void main()
{
    // The skybox is rendered using a unit cube centered at the origin. The model-space
    // position doubles as the world-space ray direction for cubemap sampling, since
    // the cube is centered on the camera (we strip translation from uView).
    worldDirection = aPosition;
    mat4 viewNoTranslation = mat4(mat3(uView));
    vec4 clipPos = uProjection * viewNoTranslation * vec4(aPosition, 1.0);
    // Force depth to the far plane (w/w = 1.0 in NDC after perspective divide). The
    // pipeline runs with LessEqual depth compare so scene geometry at depth < 1.0
    // occludes the skybox; only background pixels still at depth=1.0 (the clear) get
    // the sky color.
    gl_Position = clipPos.xyww;
}
