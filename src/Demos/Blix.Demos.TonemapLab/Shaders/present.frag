#version 450

#include "tonemap.glsl"
#include "diffuse_eon.glsl"
#include "bsdf_gltf.glsl"
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

    // 4: the EON diffuse table, raw: pi * f_EON(wi, wo) per channel for rho = (0.2, 0.5, 0.9), so lab/bsdf can compare this GLSL with its python
    // per pixel (tests/test_eon_gpu.py). Columns sweep NdotL from 0.02 to 1; rows are 5 roughnesses x 3 view cosines x 3 azimuth differences.
    if (pc.uPattern == 4)
    {
        const float R[5] = float[5](0.0, 0.25, 0.5, 0.75, 1.0);
        const float MUO[3] = float[3](0.1, 0.5, 0.9);
        const float PHI[3] = float[3](0.0, 1.5707963, 3.1415927);
        int row = int(clamp(vUv.y * 45.0, 0.0, 44.999));
        int ri = row / 9, rest = row % 9, mi = rest / 3, pi = rest % 3;
        float muI = 0.02 + 0.98 * floor(clamp(vUv.x, 0.0, 0.99999) * 256.0) / 255.0;   // 256 columns: 0.02 + 0.98 k / 255, k = 0..255
        float muO = MUO[mi];
        float sinI = sqrt(max(1.0 - muI * muI, 0.0)), sinO = sqrt(1.0 - muO * muO);
        float ldotv = sinI * sinO * cos(PHI[pi]) + muI * muO;
        outColor = vec4(BLIX_PI * blix_eonBrdf(vec3(0.2, 0.5, 0.9), R[ri], muI, muO, ldotv), 1.0);
        return;
    }

    // 5: the EON hemispherical albedo, raw: blix_eonHemisphericalAlbedo(rho, r) with rho sweeping 0.01..0.99 across 256 columns and 5 roughnesses down
    // (every channel the same), for lab/bsdf's tests/test_eon_gpu.py.
    if (pc.uPattern == 5)
    {
        const float R5[5] = float[5](0.0, 0.25, 0.5, 0.75, 1.0);
        int ri = int(clamp(vUv.y * 5.0, 0.0, 4.999));
        float rho = 0.01 + 0.98 * floor(clamp(vUv.x, 0.0, 0.99999) * 256.0) / 255.0;
        outColor = vec4(blix_eonHemisphericalAlbedo(vec3(rho), R5[ri]), 1.0);
        return;
    }

    // 6 and 7: the glTF BSDF (bsdf_gltf.glsl) for lab/bsdf's tests/test_gltf_gpu.py. Rows: 16 materials x 3 view cosines x 2 sides (the second side
    // views from below the surface, which only the interface answers); columns sweep the light's z from -0.99 to 0.99 over 256 columns at azimuth 0.6.
    // 6: |cos| f(wo, wi) per channel (pdf-free), PI times it; 7: (the material's pdf, the interface's |cos| f green channel, its pdf).
    if (pc.uPattern == 6 || pc.uPattern == 7)
    {
        const float VIEW[3] = float[3](0.9, 0.5, 0.2);
        int row = int(clamp(vUv.y * 96.0, 0.0, 95.999));
        int mi = row / 6, rest = row % 6, vi = rest / 2, side = rest % 2;
        BlixGltfMaterial m = blix_gltfDefaultMaterial();
        m.baseColor = vec3(0.7, 0.4, 0.2);
        if (mi == 1) { m.baseColor = vec3(0.9, 0.6, 0.3); m.metallic = 1.0; m.roughness = 0.6; }
        else if (mi == 2) { m.baseColor = vec3(0.8); m.metallic = 0.5; }
        else if (mi == 3) { m.baseColor = vec3(0.9, 0.95, 1.0); m.transmission = 1.0; }
        else if (mi == 4) { m.baseColor = vec3(0.5, 0.8, 0.6); m.roughness = 0.4; m.transmission = 0.8; m.ior = 1.45; }
        else if (mi == 5) { m.baseColor = vec3(0.3, 0.5, 0.2); m.roughness = 0.6; m.diffuseTransmission = 0.6; m.dtColor = vec3(0.5, 0.7, 0.2); m.diffuseRoughness = 0.5; }
        else if (mi == 6) { m.baseColor = vec3(0.5); m.specular = 0.5; m.specularColor = vec3(1.0, 0.8, 0.6); m.ior = 1.8; }
        else if (mi == 7) { m.baseColor = vec3(0.9, 1.0, 0.95); m.roughness = 0.45; m.transmission = 1.0; m.solid = true; }
        else if (mi == 8) { m.anisotropy = 0.7; m.anisotropyRotation = 0.5; }
        else if (mi == 9) { m.baseColor = vec3(0.9, 0.7, 0.4); m.metallic = 1.0; m.roughness = 0.4; m.anisotropy = 0.9; m.anisotropyRotation = 1.2; }
        else if (mi == 10) { m.clearcoat = 1.0; m.clearcoatRoughness = 0.2; }
        else if (mi == 11) { m.clearcoat = 0.5; m.clearcoatRoughness = 0.4; }
        else if (mi == 12) { m.baseColor = vec3(0.5, 0.2, 0.2); m.roughness = 0.7; m.sheenColor = vec3(0.8, 0.8, 0.9); m.sheenRoughness = 0.5; }
        else if (mi == 13) { m.baseColor = vec3(0.6); m.roughness = 0.4; m.iridescence = 1.0; m.iridescenceIor = 1.3; m.iridescenceThickness = 400.0; }
        else if (mi == 14) { m.baseColor = vec3(0.9, 0.8, 0.7); m.metallic = 1.0; m.roughness = 0.4; m.iridescence = 0.8; m.iridescenceIor = 1.5; m.iridescenceThickness = 300.0; }
        else if (mi == 15) { m.baseColor = vec3(0.5, 0.3, 0.6); m.roughness = 0.45; m.clearcoat = 0.6; m.clearcoatRoughness = 0.15; m.sheenColor = vec3(0.4); m.sheenRoughness = 0.6;
                             m.iridescence = 0.5; m.anisotropy = 0.4; m.anisotropyRotation = 0.3; }
        float cosO = VIEW[vi];
        float wiZ = -0.99 + 1.98 * floor(clamp(vUv.x, 0.0, 0.99999) * 256.0) / 255.0;
        float sinI = sqrt(max(1.0 - wiZ * wiZ, 0.0));
        vec3 wi = vec3(sinI * cos(0.6), sinI * sin(0.6), wiZ);
        vec3 wo = vec3(sqrt(1.0 - cosO * cosO), 0.0, cosO);
        float a = blix_bsdfAlpha(m.roughness);
        if (side == 1) { wo.z = -wo.z; }
        if (pc.uPattern == 6)
            outColor = vec4(side == 0 ? BLIX_PI * abs(wi.z) * blix_gltfEval(m, wo, wi) : vec3(0.0), 1.0);
        else
        {
            vec3 tint = vec3(1.0, 0.8, 0.6);
            outColor = vec4(side == 0 ? blix_gltfPdf(m, wo, wi) : 0.0, abs(wi.z) * blix_ifaceEval(a, 1.0, m.ior, tint, wo, wi).g, blix_ifacePdf(a, 1.0, m.ior, wo, wi), 1.0);
        }
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
            result = clamp(pc.uGamutMap != 0 ? gt7_gamutMap709(d) : d, 0.0, 1.0);
        }
        else
        {
            vec3 d = gt7_rec2020ToRec709(mapped * 100.0 / pc.uSdrWhiteNits);
            result = pc.uGamutMap != 0 ? (pc.uGamutTarget == 1 ? gamutMapP3(d) : gt7_gamutMap709(d)) : d;
        }
    }
    // Negative channels are kept only for the P3 target on an extended-linear surface; everything else is clipped at zero.
    outColor = vec4(pc.uGamutMap != 0 && pc.uGamutTarget == 1 && pc.uGt7Sdr == 0 ? result : max(result, vec3(0.0)), 1.0);
}
