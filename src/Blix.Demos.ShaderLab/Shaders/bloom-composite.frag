#version 410 core

in vec2 textureCoordinate;
out vec4 fragmentColor;
uniform sampler2D uSceneTexture;
// Three bloom levels at progressively halving resolution. Each captures bloom at a
// different radius - level 0 a tight halo, level 2 a wide diffuse glow. They're added
// equally; weighting them more toward higher levels widens the bloom but feels too
// soft for the era we're targeting.
uniform sampler2D uBloom0;
uniform sampler2D uBloom1;
uniform sampler2D uBloom2;
// Bloom intensity multiplier - dialing this lets you go from "subtle highlights" to
// "JJ Abrams lens flare" without recompiling.
uniform float uBloomStrength;
// Pre-tonemap exposure multiplier. Increasing brings more of the HDR range into the
// visible band; decreasing protects highlights from clipping.
uniform float uExposure;

void main()
{
    vec3 scene = texture(uSceneTexture, textureCoordinate).rgb;
    vec3 bloom =
        texture(uBloom0, textureCoordinate).rgb +
        texture(uBloom1, textureCoordinate).rgb +
        texture(uBloom2, textureCoordinate).rgb;

    // Composite in linear HDR space, then apply exposure.
    vec3 color = (scene + bloom * uBloomStrength) * uExposure;

    // Extended Reinhard tone map with a white-point control. Values approaching the
    // white point saturate to 1.0; values well below remain near-linear. White=4.0
    // gives a roomy highlight headroom typical of mid-2000s HDR rendering.
    const float kWhite = 4.0;
    vec3 tonemapped = color * (1.0 + color / (kWhite * kWhite)) / (1.0 + color);

    // sRGB encode for display.
    vec3 encoded = pow(max(tonemapped, vec3(0.0)), vec3(1.0 / 2.2));
    fragmentColor = vec4(encoded, 1.0);
}
