#version 450

#include "tonemap.glsl"
#include "gt7.glsl"

// Exposure, white balance, tone map, and the output encode, in one pass over the scene target.
//
// uOutput:  0  display-relative linear: 1.0 is the display's SDR white. What an sRGB surface (which encodes in hardware)
//              and an HDR surface (extended-linear, where 1.0 is the UI white) both want. SDR operators land here.
//           1  not used by the shader itself; the C# side picks 0 or 2.
//           2  raw GT7 frame-buffer values (1.0 = 100 nits, Rec.2020), for `--selftest`: the comparison with gt7.py.
// uOperator: 0 Blix ACES, 1 AgX, 2 Reinhard, 3 neutral (src/Blix.Shaders/tonemap.glsl), 4 GT7.
// uPattern:  0 the scene; 1 EDR patches, raw (does the panel show more than SDR white at all); 2 a scene-linear ramp with
//            six hues and grey, through the whole pipeline (the lab's hue-shift test, live).
// GT7 under HDR emits nits; divided by uSdrWhiteNits that is the EDR value (1.0 = SDR white, headroom above it).

layout(set = 0, binding = 0) uniform sampler2D uScene;
// The six glare terms, each the scene blurred by one Gaussian of the fit in lab/tonemap/results/glare_fit.json (see tmlab/glare.py).
layout(set = 0, binding = 1) uniform sampler2D uGlare0;
layout(set = 0, binding = 2) uniform sampler2D uGlare1;
layout(set = 0, binding = 3) uniform sampler2D uGlare2;
layout(set = 0, binding = 4) uniform sampler2D uGlare3;
layout(set = 0, binding = 5) uniform sampler2D uGlare4;
layout(set = 0, binding = 6) uniform sampler2D uGlare5;
// Veiling glare: four Gaussians fitted to the Stiles-Holladay 10/theta^2 profile (results/scatter_fit.json), widths in degrees.
layout(set = 0, binding = 7) uniform sampler2D uScatter0;
layout(set = 0, binding = 8) uniform sampler2D uScatter1;
layout(set = 0, binding = 9) uniform sampler2D uScatter2;
layout(set = 0, binding = 10) uniform sampler2D uScatter3;
// Aperture-blade spikes: already weighted (tmlab/spikes.py), added as they are.
layout(set = 0, binding = 11) uniform sampler2D uSpike;
const float SCATTER_W[4] = float[4](0.184599424, 0.330670765, 0.342027359, 0.142702453);
const float GLARE_W[6] = float[6](0.808603086, 0.134753827, 0.0397676642, 0.0120275726, 0.00364401528, 0.00120383468);

layout(push_constant) uniform Push {
    float uExposure;
    float uAnchorNits;
    float uSdrWhiteNits;
    float uPeakNits;
    float uWbR;
    float uWbG;
    float uWbB;
    float uAlpha;
    float uGrayPoint;
    float uLinearSection;
    float uToeStrength;
    float uBlendRatio;
    float uFadeStart;
    float uFadeEnd;
    float uShoulderTail;
    float uGlare;       // 0 off .. 1 the full diffraction PSF
    float uSpikes;      // 1 when the spike pass drew this frame, else 0
    float uScatter;     // fraction of the light moved into the veil (0.066 = the standard's, age 25)
    int uOperator;
    int uOutput;
    int uPattern;
    int uGt7Sdr;
} pc;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

const vec3 HUES[7] = vec3[7](vec3(1.0, 0.08, 0.04), vec3(1.0, 0.35, 0.03), vec3(0.1, 1.0, 0.1), vec3(0.05, 0.6, 1.0),
                             vec3(0.04, 0.08, 1.0), vec3(1.0, 0.1, 0.8), vec3(1.0));

void main()
{
    // 1: raw EDR patches. No exposure, no tone map: the value written is the value sent. In an SDR surface everything
    // above 1.0 clips to white; on a surface with headroom the brighter patches should be visibly brighter.
    if (pc.uPattern == 1)
    {
        float patches[8] = float[8](0.25, 0.5, 1.0, 1.5, 2.0, 3.0, 4.0, 6.0);
        vec3 v;
        if (vUv.y < 0.62) v = vec3(patches[int(clamp(vUv.x * 8.0, 0.0, 7.999))]);
        else if (vUv.y < 0.70) v = vec3(0.0);
        else v = vec3(vUv.x * 8.0);                       // a ramp, 0 to 8 times SDR white
        outColor = vec4(v, 1.0);
        return;
    }

    vec3 scene;
    if (pc.uPattern == 2)
    {
        int row = int(clamp(vUv.y * 7.0, 0.0, 6.999));
        scene = HUES[row] * pow(10.0, -3.0 + 6.0 * vUv.x);
    }
    else
    {
        scene = texture(uScene, vUv).rgb;
        if (pc.uGlare > 0.0)
        {
            vec3 g = GLARE_W[0] * texture(uGlare0, vUv).rgb + GLARE_W[1] * texture(uGlare1, vUv).rgb
                   + GLARE_W[2] * texture(uGlare2, vUv).rgb + GLARE_W[3] * texture(uGlare3, vUv).rgb
                   + GLARE_W[4] * texture(uGlare4, vUv).rgb + GLARE_W[5] * texture(uGlare5, vUv).rgb;
            scene = mix(scene, g, pc.uGlare);
        }
        if (pc.uScatter > 0.0)
        {
            vec3 v = SCATTER_W[0] * texture(uScatter0, vUv).rgb + SCATTER_W[1] * texture(uScatter1, vUv).rgb
                   + SCATTER_W[2] * texture(uScatter2, vUv).rgb + SCATTER_W[3] * texture(uScatter3, vUv).rgb;
            scene = mix(scene, v, pc.uScatter);
        }
        if (pc.uSpikes > 0.0) scene += texture(uSpike, vUv).rgb;
    }

    vec3 c = scene * pc.uExposure * vec3(pc.uWbR, pc.uWbG, pc.uWbB);
    vec3 result;
    if (pc.uOperator < 4)
    {
        result = blix_tonemap(c, float(pc.uOperator));        // 0..1, relative to SDR white
    }
    else
    {
        Gt7Params p;
        p.alpha = pc.uAlpha;
        p.grayPoint = pc.uGrayPoint;
        p.linearSection = pc.uLinearSection;
        p.toeStrength = pc.uToeStrength;
        p.blendRatio = pc.uBlendRatio;
        p.fadeStart = pc.uFadeStart;
        p.fadeEnd = pc.uFadeEnd;
        p.shoulderTail = pc.uShoulderTail;
        p.target = pc.uGt7Sdr != 0 ? 2.5 : pc.uPeakNits / 100.0;
        p.outputScale = pc.uGt7Sdr != 0 ? 0.4 : 1.0;
        vec3 mapped = gt7_apply(gt7_rec709ToRec2020(c * (pc.uAnchorNits / 100.0)), p);
        if (pc.uOutput == 2) result = mapped;
        else if (pc.uGt7Sdr != 0) result = clamp(gt7_rec2020ToRec709(mapped), 0.0, 1.0);
        else result = gt7_rec2020ToRec709(mapped * 100.0 / pc.uSdrWhiteNits);
    }
    outColor = vec4(max(result, vec3(0.0)), 1.0);
}
