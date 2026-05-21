#version 410 core

in vec2 textureCoordinate;
out vec4 fragmentColor;
uniform sampler2D uSceneTexture;

void main()
{
    // The scene is rendered in linear HDR. Encode to sRGB for the display, which is
    // sRGB-calibrated. Without this step the gamma-correct linear math we just did in
    // cube.frag would look washed out and dim on screen. No tone mapping yet - values
    // above 1.0 still clip; bloom + Reinhard will compress them in the next batch.
    vec3 linear = texture(uSceneTexture, textureCoordinate).rgb;
    // sRGB encode (approximate). The precise sRGB curve has a small linear toe near
    // black; for display the simpler pow form is visually indistinguishable.
    vec3 encoded = pow(max(linear, vec3(0.0)), vec3(1.0 / 2.2));
    fragmentColor = vec4(encoded, 1.0);
}
