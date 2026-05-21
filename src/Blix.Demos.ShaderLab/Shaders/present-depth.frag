#version 410 core

in vec2 textureCoordinate;
out vec4 fragmentColor;
uniform sampler2D uSceneTexture;

// Hardcoded to match the sandbox camera. Replace with uniforms once the engine
// has a float uniform type and the present pass can be told the camera planes.
const float kNear = 0.1;
const float kFar = 100.0;
const float kViewMax = 8.0;

void main()
{
    float d = texture(uSceneTexture, textureCoordinate).r;
    float ndc = d * 2.0 - 1.0;
    float linearViewZ = (2.0 * kNear * kFar) / (kFar + kNear - ndc * (kFar - kNear));
    float visualized = clamp((linearViewZ - kNear) / (kViewMax - kNear), 0.0, 1.0);
    fragmentColor = vec4(vec3(visualized), 1.0);
}
