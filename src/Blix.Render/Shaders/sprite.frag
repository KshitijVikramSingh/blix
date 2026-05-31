#version 450

// SpriteBatch fragment shader. One combined image sampler at set 0, binding 0
// (the inline ShaderTextureBinding path — slot 0). The sampled texel is
// modulated by the interpolated vertex colour, so a 1x1 white texture tinted
// by vertex colour fills solid rects, and the font atlas (white RGB + coverage
// alpha) tints text the same way.

layout(location = 0) in vec2 vTexCoord;
layout(location = 1) in vec4 vColor;

layout(location = 0) out vec4 fragColor;

layout(set = 0, binding = 0) uniform sampler2D uTexture;

void main()
{
    fragColor = texture(uTexture, vTexCoord) * vColor;
}
