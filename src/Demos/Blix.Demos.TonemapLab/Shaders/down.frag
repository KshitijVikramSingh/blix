#version 450

// Half-resolution step of a glare chain: four bilinear taps over the source texel corners (a 4x4 tent). The glare passes
// feed each Gaussian term from the resolution its width needs, so a term 300 pixels wide is a 5-pixel blur of a 1/128 image.
layout(set = 0, binding = 0) uniform sampler2D uSrc;
layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

void main()
{
    vec2 o = 0.5 / vec2(textureSize(uSrc, 0));
    outColor = 0.25 * (texture(uSrc, vUv + vec2(-o.x, -o.y)) + texture(uSrc, vUv + vec2(o.x, -o.y))
                     + texture(uSrc, vUv + vec2(-o.x, o.y)) + texture(uSrc, vUv + vec2(o.x, o.y)));
}
