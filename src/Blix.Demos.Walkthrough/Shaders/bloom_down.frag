#version 410 core

// Bloom downsample (13-tap-ish). Simpler than Karis 13-tap: 4-tap box plus
// a centre tap with extra weight, gives smoother result than pure box
// without the fireflies that come from sampling just four corners of a
// high-HDR scene.

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uSrc;
uniform vec2 uSrcTexel;   // 1.0 / textureSize(uSrc, 0)

void main()
{
    vec3 c = vec3(0.0);
    // 4 corner taps at +/- 1 src-texel = +/- 0.5 in dst-texel space (since
    // we're downsampling by 2x). Plus 1 centre tap.
    c += texture(uSrc, vUv + vec2(-1.0, -1.0) * uSrcTexel).rgb;
    c += texture(uSrc, vUv + vec2( 1.0, -1.0) * uSrcTexel).rgb;
    c += texture(uSrc, vUv + vec2(-1.0,  1.0) * uSrcTexel).rgb;
    c += texture(uSrc, vUv + vec2( 1.0,  1.0) * uSrcTexel).rgb;
    c *= 0.125;
    c += texture(uSrc, vUv).rgb * 0.5;
    fragColor = vec4(c, 1.0);
}
