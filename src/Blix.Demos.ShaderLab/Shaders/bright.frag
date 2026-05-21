#version 410 core

in vec2 textureCoordinate;
out vec4 fragmentColor;
uniform sampler2D uSceneTexture;

// Soft-knee bright pass for bloom. Anything above 1.0 (HDR overbright) contributes
// proportionally; values at or below 1.0 are suppressed. Combined with the smaller
// destination surface, this also serves as the first downsample of the bloom chain.
void main()
{
    vec3 color = texture(uSceneTexture, textureCoordinate).rgb;
    // Per-channel "amount above 1.0", scaled by overall luma so saturated highlights
    // contribute more than barely-over-threshold pixels. Avoids harsh ringing around
    // the bright cutoff.
    float maxChannel = max(color.r, max(color.g, color.b));
    float keep = max(maxChannel - 1.0, 0.0) / max(maxChannel, 1e-5);
    fragmentColor = vec4(color * keep, 1.0);
}
