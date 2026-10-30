#version 450

// Aperture-blade diffraction spikes (lab/tonemap/tmlab/spikes.py). A regular N-blade aperture sends each edge's light into a
// streak perpendicular to the edge: N spikes for even N, 2N for odd N, each a line through the source falling as A / r^2
// (r in Airy radii, fitted to the FFT of the polygon aperture). This pass adds, for every spike LINE, the scene sampled
// along that line on both sides, weighted by the line density A / rho^2 (rho in Airy radii). Taps are ONE TEXEL apart from the
// knee to the reach (geometric spacing left a point-like source as a string of beads far out). It reads the scene at 1/4
// resolution, where a few hundred taps a pixel per line are affordable.
layout(set = 0, binding = 0) uniform sampler2D uSrc;
layout(push_constant) uniform Push {
    float uAngle0;     // first edge normal, radians
    float uAmp;        // line density amplitude PER SPIKE (already times the strength)
    float uAiryPx;     // Airy radius in texels of this image
    float uKnee;       // Airy radii where the kernel starts
    float uReachPx;    // unused (the tap count sets the reach)
    int uLines;
    int uTaps;          // taps a side, one texel apart: the reach in texels
} pc;
layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

void main()
{
    vec2 texel = 1.0 / vec2(textureSize(uSrc, 0));
    float r0 = max(pc.uKnee * pc.uAiryPx, 1.0);
    vec3 sum = vec3(0.0);
    for (int line = 0; line < pc.uLines; ++line)
    {
        float angle = pc.uAngle0 + float(line) * 3.14159265 / float(pc.uLines);
        vec2 dir = vec2(cos(angle), sin(angle)) * texel;
        for (int j = 0; j < pc.uTaps; ++j)
        {
            float r = r0 + float(j);                             // one texel a step: continuous streaks even for a point-like source
            float w = pc.uAmp * pc.uAiryPx / (r * r);            // density A / rho^2 per Airy radius, times the step in Airy radii
            w *= 1.0 - smoothstep(0.45, 1.0, float(j) / float(pc.uTaps));   // fades out over the last half of the reach: no hard ends
            sum += w * (texture(uSrc, vUv + dir * r).rgb + texture(uSrc, vUv - dir * r).rgb);
        }
    }
    outColor = vec4(sum, 1.0);
}
