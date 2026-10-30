#version 450

// One separable Gaussian pass: uDir selects the axis, uSigma is the standard deviation in texels of THIS image. Weights are
// computed here and normalised over the taps actually used, so the pass preserves energy (the glare model's weights sum to 1).
layout(set = 0, binding = 0) uniform sampler2D uSrc;
layout(push_constant) uniform Push {
    float uDirX;
    float uDirY;
    float uSigma;
} pc;
layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

void main()
{
    vec2 step = vec2(pc.uDirX, pc.uDirY) / vec2(textureSize(uSrc, 0));
    int radius = int(min(ceil(3.0 * pc.uSigma), 16.0));
    vec3 sum = vec3(0.0);
    float norm = 0.0;
    for (int k = -radius; k <= radius; ++k)
    {
        float w = exp(-0.5 * float(k * k) / (pc.uSigma * pc.uSigma));
        vec2 uv = vUv + float(k) * step;
        // Zero padding: light from outside the frame is absent, so a source near an edge loses the part of its halo that falls off
        // the frame (clamping would replicate the edge and invent light). The weights still normalise over every tap.
        if (uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0) sum += w * texture(uSrc, uv).rgb;
        norm += w;
    }
    outColor = vec4(sum / norm, 1.0);
}
