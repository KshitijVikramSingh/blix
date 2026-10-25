#version 450

// Aperture-blade diffraction spikes (lab/tonemap/tmlab/spikes.py). A regular N-blade aperture sends each edge's light into a
// streak perpendicular to the edge: N spikes for even N, 2N for odd N, each a line through the source falling as A / r^2
// (r in Airy radii, fitted to the FFT of the polygon aperture). This pass adds, for every spike LINE, the scene sampled
// along that line on both sides, weighted by the line density. Taps are geometrically spaced from the kernel's knee to
// its reach (the density falls as 1/r^2, so the weight per tap is A * a * (q - 1) / r in texels).
// It reads the scene at 1/4 resolution: the streaks are long and soft, and 100+ taps a pixel are affordable there.
layout(set = 0, binding = 0) uniform sampler2D uSrc;
layout(push_constant) uniform Push {
    float uAngle0;     // first edge normal, radians
    float uAmp;        // line density amplitude PER SPIKE (already times the strength)
    float uAiryPx;     // Airy radius in texels of this image
    float uKnee;       // Airy radii where the kernel starts
    float uReachPx;    // furthest tap, in texels
    int uLines;
    int uTaps;
} pc;
layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

void main()
{
    vec2 texel = 1.0 / vec2(textureSize(uSrc, 0));
    float r0 = max(pc.uKnee * pc.uAiryPx, 0.5);
    float q = pow(max(pc.uReachPx / r0, 1.01), 1.0 / float(pc.uTaps));
    vec3 sum = vec3(0.0);
    for (int line = 0; line < pc.uLines; ++line)
    {
        float angle = pc.uAngle0 + float(line) * 3.14159265 / float(pc.uLines);
        vec2 dir = vec2(cos(angle), sin(angle)) * texel;
        float r = r0;
        for (int j = 0; j < pc.uTaps; ++j)
        {
            float rm = r * sqrt(q);                              // the tap sits at the geometric midpoint of its step
            float w = pc.uAmp * pc.uAiryPx * (q - 1.0) / rm;
            sum += w * (texture(uSrc, vUv + dir * rm).rgb + texture(uSrc, vUv - dir * rm).rgb);
            r *= q;
        }
    }
    outColor = vec4(sum, 1.0);
}
