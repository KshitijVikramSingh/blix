#version 410 core

// Depth output with alpha-cutout discard. The bound framebuffer has no color
// attachments, so the rasterizer writes gl_FragDepth implicitly -- but only
// if the fragment survives the alpha test, which makes leaf-shape gaps in
// the shadow map (and therefore in fog god-rays + cascade shadow sampling).

in vec2 vTexCoord;

uniform sampler2D uAlbedo;
uniform vec4 uBaseColorFactor;
uniform float uAlphaCutoff;

void main()
{
    // Match lit.frag's UV convention (V flipped because images load with
    // stbi_set_flip_vertically_on_load(1) to match GL's bottom-up origin).
    vec2 uv = vec2(vTexCoord.x, 1.0 - vTexCoord.y);
    // Only the alpha channel matters here; sample the base colour texture
    // multiplied by the factor's alpha (glTF: baseColorFactor.a scales the
    // sampled alpha). uAlphaCutoff is 0 for OPAQUE materials so the discard
    // never fires for solid geometry.
    if (uAlphaCutoff > 0.0)
    {
        float a = texture(uAlbedo, uv).a * uBaseColorFactor.a;
        if (a < uAlphaCutoff) discard;
    }
}
