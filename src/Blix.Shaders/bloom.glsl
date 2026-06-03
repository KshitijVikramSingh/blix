#pragma once

// Bloom building blocks for a separable-Gaussian bright/blur chain. The pipeline
// topology (quarter-res bright extract -> horizontal blur -> vertical blur) and
// the final composite/tonemap stay with the caller; these are just the per-pixel
// kernels both stages share.

// Bright-pass extract: keep only the over-threshold portion of an HDR colour,
// scaled so a pixel exactly at the knee contributes nothing and brighter pixels
// ramp in smoothly (Rec. 709 luma). Run into a smaller target to also downsample.
vec3 blix_bloomThreshold(vec3 hdr, float threshold)
{
    float luma = dot(hdr, vec3(0.2126, 0.7152, 0.0722));
    float contrib = max(luma - threshold, 0.0) / max(luma, 1e-4);
    return hdr * contrib;
}

// 9-tap separable Gaussian. texelStep is blurDir * (1 / targetResolution): pass
// (1/w, 0) for the horizontal pass and (0, 1/h) for the vertical pass.
vec3 blix_gaussianBlur9(sampler2D src, vec2 uv, vec2 texelStep)
{
    float w[5] = float[](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216);
    vec3 sum = texture(src, uv).rgb * w[0];
    for (int i = 1; i < 5; i++)
    {
        sum += texture(src, uv + texelStep * float(i)).rgb * w[i];
        sum += texture(src, uv - texelStep * float(i)).rgb * w[i];
    }
    return sum;
}
