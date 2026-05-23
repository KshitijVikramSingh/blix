#version 410 core

// View-aligned billboard. The mesh provides quad corners in aPosition.xy as
// [-0.5..0.5] with aPosition.z = 0. We rebuild the world-space corner by
// expanding along the camera's right and up axes (taken from the view matrix's
// transposed top-left 3x3, i.e. the inverse rotation), so the quad always
// faces the camera regardless of viewing angle.

layout (location = 0) in vec2 aPosition;
layout (location = 1) in vec2 aTexCoord;

uniform mat4  uView;
uniform mat4  uProjection;
uniform vec3  uFlamePosition;
uniform float uFlameSize;
// Width/height ratio of the billboard. 1.0 for the square procedural shader,
// 0.5 for the Unity Labs atlas whose frames are 128x256 (tall). uFlameSize
// drives the height; width = uFlameSize * uFlameAspect.
uniform float uFlameAspect;

out vec2 vUv;

void main()
{
    // Row 0 of the view matrix = camera-space X axis as seen from world. The
    // transpose of view's 3x3 maps camera-axis -> world-axis, so column 0 of
    // that transpose (= row 0 of view) is camera-right in world space.
    vec3 camRight = vec3(uView[0][0], uView[1][0], uView[2][0]);
    vec3 camUp    = vec3(uView[0][1], uView[1][1], uView[2][1]);

    vec3 worldPos = uFlamePosition
                  + camRight * aPosition.x * uFlameSize * uFlameAspect
                  + camUp    * aPosition.y * uFlameSize;

    gl_Position = uProjection * uView * vec4(worldPos, 1.0);
    vUv = aTexCoord;
}
