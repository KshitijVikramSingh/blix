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
    float uGlareTail;   // 1 = the fitted far tails; <1 moves the widest terms' energy into the next-widest (diffraction: terms 4,5 -> 3; scatter: term 3 -> 2)
    float uScatter;     // fraction of the light moved into the veil (0.066 = the standard's, age 25)
    int uOperator;
    int uOutput;
    int uPattern;
    int uGt7Sdr;
    int uGamutMap;      // 1: colours outside the target gamut are desaturated toward their own luminance instead of clipped per channel
    int uGamutTarget;   // 0 Rec.709 (clip at zero), 1 Display P3 (negative channels kept where P3 can show them: extended-linear output only)
} pc;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

const vec3 HUES[7] = vec3[7](vec3(1.0, 0.08, 0.04), vec3(1.0, 0.35, 0.03), vec3(0.1, 1.0, 0.1), vec3(0.05, 0.6, 1.0),
                             vec3(0.04, 0.08, 1.0), vec3(1.0, 0.1, 0.8), vec3(1.0));

// Colours the tone mapper leaves outside the display's Rec.709 gamut come out of the Rec.2020 -> 709 step with a negative channel.
// Clipping that channel to zero changes the hue as well as the chroma; this keeps the luminance and gives up only chroma: move
// toward the grey of the same luma by the least amount that makes the smallest channel zero (tmlab/color.py gamut_map_709).
vec3 gamutMap709(vec3 c)
{
    float y = dot(c, vec3(0.2126, 0.7152, 0.0722));
    float mn = min(c.r, min(c.g, c.b));
    if (mn >= 0.0) return c;
    if (y <= 0.0) return vec3(0.0);
    float t = y / (y - mn);
    return max(vec3(y) + t * (c - vec3(y)), vec3(0.0));
}

// The same toward Display P3: the map runs in P3's own RGB and the result is returned as linear Rec.709, negative where P3 needs it
// (an extended-linear surface shows those on a P3 panel). tmlab/color.py gamut_map(..., "p3"); matrices are GLSL column-major.
const mat3 REC709_TO_P3 = mat3(0.8224621, 0.0331941, 0.0170827, 0.1775380, 0.9668058, 0.0723974, 0.0000000, 0.0000001, 0.9105199);
const mat3 P3_TO_REC709 = mat3(1.2249400, -0.0420568, -0.0196377, -0.2249401, 1.0420569, -0.0786360, 0.0000000, -0.0000001, 1.0982736);
vec3 gamutMapP3(vec3 c709)
{
    vec3 c = REC709_TO_P3 * c709;
    float y = dot(c, vec3(0.2289254, 0.6917793, 0.0792953));   // LUMA709 expressed in P3 components: the same absolute luminance
    float mn = min(c.r, min(c.g, c.b));
    if (mn >= 0.0) return c709;
    if (y <= 0.0) return vec3(0.0);
    float t = y / (y - mn);
    return P3_TO_REC709 * max(vec3(y) + t * (c - vec3(y)), vec3(0.0));
}

void main()
{
    // 3: gamut bars, raw. Left to right: Rec.709 red, green, blue, then Display P3 red, green, blue, each as extended-linear Rec.709
    // (a P3 primary has a negative channel there). Top half 0.5, bottom half 1.0 of SDR white. On a P3 surface that honours negative
    // channels the P3 bars are visibly more saturated than the Rec.709 ones; if they match, negatives are being clipped somewhere.
    if (pc.uPattern == 3)
    {
        int bar = int(clamp(vUv.x * 6.0, 0.0, 5.999));
        vec3 prim = vec3(bar % 3 == 0 ? 1.0 : 0.0, bar % 3 == 1 ? 1.0 : 0.0, bar % 3 == 2 ? 1.0 : 0.0);
        vec3 v = bar < 3 ? prim : P3_TO_REC709 * prim;
        outColor = vec4(v * (vUv.y < 0.5 ? 0.5 : 1.0), 1.0);
        return;
    }

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
            float moved = (1.0 - pc.uGlareTail) * (GLARE_W[4] + GLARE_W[5]);   // energy moved from the far tail into term 3
            vec3 g = GLARE_W[0] * texture(uGlare0, vUv).rgb + GLARE_W[1] * texture(uGlare1, vUv).rgb
                   + GLARE_W[2] * texture(uGlare2, vUv).rgb + (GLARE_W[3] + moved) * texture(uGlare3, vUv).rgb
                   + pc.uGlareTail * (GLARE_W[4] * texture(uGlare4, vUv).rgb + GLARE_W[5] * texture(uGlare5, vUv).rgb);
            scene = mix(scene, g, pc.uGlare);
        }
        if (pc.uScatter > 0.0)
        {
            float movedS = (1.0 - pc.uGlareTail) * SCATTER_W[3];
            vec3 v = SCATTER_W[0] * texture(uScatter0, vUv).rgb + SCATTER_W[1] * texture(uScatter1, vUv).rgb
                   + (SCATTER_W[2] + movedS) * texture(uScatter2, vUv).rgb + pc.uGlareTail * SCATTER_W[3] * texture(uScatter3, vUv).rgb;
            scene = mix(scene, v, pc.uScatter);
        }
        if (pc.uSpikes > 0.0) scene += texture(uSpike, vUv).rgb;
        scene *= 32.0;   // the scene target stores light / 32 (see scene.frag); every term above is linear, so it is undone once, here
    }

    if (pc.uOutput == 3) { outColor = vec4(scene, 1.0); return; }   // --probe: the scene-linear composite (scene + glare + scatter + spikes), no exposure, no tone map
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
        else if (pc.uGt7Sdr != 0)
        {
            vec3 d = gt7_rec2020ToRec709(mapped);
            result = clamp(pc.uGamutMap != 0 ? gamutMap709(d) : d, 0.0, 1.0);
        }
        else
        {
            vec3 d = gt7_rec2020ToRec709(mapped * 100.0 / pc.uSdrWhiteNits);
            result = pc.uGamutMap != 0 ? (pc.uGamutTarget == 1 ? gamutMapP3(d) : gamutMap709(d)) : d;
        }
    }
    // Negative channels are kept only for the P3 target on an extended-linear surface; everything else is clipped at zero.
    outColor = vec4(pc.uGamutMap != 0 && pc.uGamutTarget == 1 && pc.uGt7Sdr == 0 ? result : max(result, vec3(0.0)), 1.0);
}
