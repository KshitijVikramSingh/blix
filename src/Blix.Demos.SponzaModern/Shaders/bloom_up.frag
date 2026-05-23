#version 410 core

// Bloom upsample. 9-tap tent filter (Karis 2014 / "next-gen post processing
// in Call of Duty Advanced Warfare"). Sampled coordinates spaced at half a
// destination-texel; combined with bilinear filtering on the source, this
// gives a smooth 3x3-ish footprint.
//
// The pipeline that drives this pass is configured with BlendState.Additive,
// so each fragment's RGB ADDS to whatever's already in the destination
// surface. Calling this on a chain of progressively-finer bloom buffers
// accumulates all mip contributions into the eventual full-res bloom.

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uSrc;
uniform vec2 uSrcTexel;   // 1.0 / textureSize(uSrc, 0)

void main()
{
    // 3x3 tent: 1/16 corners, 2/16 edges, 4/16 centre. Spread by 1 src-texel.
    vec3 c = vec3(0.0);
    c += texture(uSrc, vUv + vec2(-1.0, -1.0) * uSrcTexel).rgb * 0.0625;
    c += texture(uSrc, vUv + vec2( 0.0, -1.0) * uSrcTexel).rgb * 0.125;
    c += texture(uSrc, vUv + vec2( 1.0, -1.0) * uSrcTexel).rgb * 0.0625;
    c += texture(uSrc, vUv + vec2(-1.0,  0.0) * uSrcTexel).rgb * 0.125;
    c += texture(uSrc, vUv + vec2( 0.0,  0.0) * uSrcTexel).rgb * 0.25;
    c += texture(uSrc, vUv + vec2( 1.0,  0.0) * uSrcTexel).rgb * 0.125;
    c += texture(uSrc, vUv + vec2(-1.0,  1.0) * uSrcTexel).rgb * 0.0625;
    c += texture(uSrc, vUv + vec2( 0.0,  1.0) * uSrcTexel).rgb * 0.125;
    c += texture(uSrc, vUv + vec2( 1.0,  1.0) * uSrcTexel).rgb * 0.0625;
    fragColor = vec4(c, 1.0);
}
