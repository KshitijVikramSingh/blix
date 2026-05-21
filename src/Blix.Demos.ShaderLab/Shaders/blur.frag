#version 410 core

in vec2 textureCoordinate;
out vec4 fragmentColor;
uniform sampler2D uSceneTexture;
// uDirection picks the axis: (1, 0) for the horizontal pass, (0, 1) for the vertical
// pass. The same shader runs twice per bloom level, ping-ponging between two surfaces.
uniform vec2 uDirection;

// 9-tap Gaussian (sigma = 2), normalized so weights sum to 1. Pre-baked into a const
// array for tightness; the loop is small enough that an explicit unroll is fine.
const float kWeights[5] = float[5](0.20416, 0.18016, 0.12382, 0.06628, 0.02762);

void main()
{
    vec2 texelSize = 1.0 / vec2(textureSize(uSceneTexture, 0));
    vec2 step = uDirection * texelSize;
    vec3 sum = texture(uSceneTexture, textureCoordinate).rgb * kWeights[0];
    for (int i = 1; i < 5; ++i)
    {
        vec2 offset = step * float(i);
        sum += texture(uSceneTexture, textureCoordinate + offset).rgb * kWeights[i];
        sum += texture(uSceneTexture, textureCoordinate - offset).rgb * kWeights[i];
    }
    fragmentColor = vec4(sum, 1.0);
}
