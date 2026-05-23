#version 410 core

// Composite + tonemap. Pulls the HDR scene buffer, the bloom upsample
// mip0, and the SSR contribution; applies a colour-grading stage
// (temperature / saturation / contrast) in linear HDR space; runs one of
// four tonemap operators; gamma encodes; writes to the swap chain.
//
// The grading stage runs BEFORE the tonemap because in HDR space the
// adjustments have access to the full dynamic range -- pulling temperature
// after tonemap would just shift LDR values, losing punch in the highlights.

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uHdrScene;
uniform sampler2D uBloom;
uniform sampler2D uSsr;
uniform float uBloomStrength;
uniform float uSsrStrength;
// Debug: 1.0 = show ONLY the SSR contribution (no scene/bloom). Useful for
// seeing exactly what the SSR pass is producing without other content masking it.
uniform float uSsrOnly;
// Debug: 1.0 = vertically flip the SSR texture when reading. Test for whether
// the screen-space convention is wrong somewhere; the math should produce a
// correct reflection without flipping but the toggle lets us A/B confirm.
uniform float uSsrFlipV;
// Grade stage.
uniform vec3  uColorTemp;        // multiplicative tint; default white (1,1,1)
uniform float uSaturation;       // 0 = grayscale, 1 = unchanged, >1 = punchier
uniform float uContrast;         // 1 = unchanged, <1 = flatter, >1 = punchier
// Tonemap mode: 0 = ACES, 1 = AgX, 2 = Reinhard, 3 = Neutral (clamp).
uniform float uTonemapMode;

#include "lib/tonemap.glsl"

void main()
{
    vec2 ssrUv = uSsrFlipV > 0.5 ? vec2(vUv.x, 1.0 - vUv.y) : vUv;
    vec3 scene = texture(uHdrScene, vUv).rgb;
    vec3 bloom = texture(uBloom,    vUv).rgb;
    vec3 ssr   = texture(uSsr,      ssrUv).rgb;
    vec3 hdr;
    if (uSsrOnly > 0.5)
    {
        hdr = ssr;
    }
    else
    {
        hdr = scene + bloom * uBloomStrength + ssr * uSsrStrength;
    }

    // --- Grade in linear HDR space --------------------------------------
    hdr *= uColorTemp;
    hdr = blix_saturate(hdr, uSaturation);
    hdr = blix_contrast(hdr, uContrast);

    // --- Tonemap + gamma -------------------------------------------------
    vec3 mapped = blix_tonemap(hdr, uTonemapMode);
    vec3 gamma = pow(mapped, vec3(1.0 / 2.2));
    fragColor = vec4(gamma, 1.0);
}
