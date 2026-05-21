#version 410 core

in vec2 textureCoordinate;
out vec4 fragmentColor;
// sampler2DShadow because the shadow surface now has GL_TEXTURE_COMPARE_MODE on for the
// lit pass's hardware PCF. Sampling a compare-mode texture via plain sampler2D would
// be undefined per the GL spec, so we reconstruct depth via repeated comparisons.
uniform sampler2DShadow uSceneTexture;

const int kSlices = 16;

void main()
{
    // Reconstruct stored depth by counting how many evenly-spaced reference depths are
    // less-or-equal to the texel's stored depth. For depth D, refs in [0, D] return 1
    // and refs in (D, 1] return 0, so sum/N approximates D to 1/N quantization.
    float coverage = 0.0;
    for (int i = 0; i < kSlices; ++i)
    {
        float ref = (float(i) + 0.5) / float(kSlices);
        coverage += texture(uSceneTexture, vec3(textureCoordinate, ref));
    }
    float d = coverage / float(kSlices);
    // Invert: geometry closer to the light reads brighter than the far plane.
    fragmentColor = vec4(vec3(1.0 - d), 1.0);
}
