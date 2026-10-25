#pragma once

// GT7 tone mapping (Polyphony Digital, SIGGRAPH 2025), ported from lab/tonemap/tmlab/gt7.py and color.py, which are held
// to the reference sample's own numbers (lab/tonemap/tests/golden). This file is a THIRD copy of that arithmetic, after
// the python and the published C++, so it is checked the same way: `--selftest` renders a known pattern through it and
// lab/tonemap/tests/test_glsl_parity.py compares the result with gt7.py. Keep the argument orders: smoothstep takes its
// value FIRST, as the reference's does.
//
// Input: linear Rec.2020 in frame-buffer units (1.0 = 100 cd/m^2). The ICtCp constants are ITU-T T.302's, as in the sample.
// shoulderTail is the lab's own parameter: 0 is the published exponential shoulder exactly.

struct Gt7Params
{
    float alpha;
    float grayPoint;
    float linearSection;
    float toeStrength;
    float blendRatio;
    float fadeStart;
    float fadeEnd;
    float shoulderTail;
    float target;          // display peak in frame-buffer units: nits / 100 (2.5 for the SDR paper white)
    float outputScale;     // 1.0 for HDR; 1 / 2.5 for SDR, which undoes the 250-nit paper white
};

float gt7_smoothstep(float x, float e0, float e1)
{
    float t = clamp((x - e0) / (e1 - e0), 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}

float gt7_curve(float x, Gt7Params p)
{
    if (x < 0.0) return 0.0;
    float k = (p.linearSection - 1.0) / (p.alpha - 1.0);
    float x0 = p.linearSection * p.target;
    float span = p.target * k;
    float wLin = gt7_smoothstep(x, 0.0, p.grayPoint);
    float toe = p.grayPoint * pow(x / p.grayPoint, p.toeStrength);
    float lower = (1.0 - wLin) * toe + wLin * x;
    if (x < x0) return lower;
    float t = (x - x0) / span;
    if (p.shoulderTail == 0.0) return x0 + span * (1.0 - exp(-t));
    return x0 + span * (1.0 - pow(1.0 + p.shoulderTail * t, -1.0 / p.shoulderTail));
}

// ST 2084, frame-buffer units (1.0 = 100 nits).
float gt7_pqInverse(float v)
{
    float y = max(v, 0.0) * 100.0 / 10000.0;
    float ym = pow(y, 0.1593017578125);
    return exp2(78.84375 * (log2(0.8359375 + 18.8515625 * ym) - log2(1.0 + 18.6875 * ym)));
}

float gt7_pq(float n)
{
    n = clamp(n, 0.0, 1.0);
    float np = pow(n, 1.0 / 78.84375);
    float l = max(np - 0.8359375, 0.0) / (18.8515625 - 18.6875 * np);
    return pow(l, 1.0 / 0.1593017578125) * 10000.0 / 100.0;
}

vec3 gt7_rgb2020ToIctcp(vec3 rgb)
{
    float l = dot(vec3(1688.0, 2146.0, 262.0), rgb) / 4096.0;
    float m = dot(vec3(683.0, 2951.0, 462.0), rgb) / 4096.0;
    float s = dot(vec3(99.0, 309.0, 3688.0), rgb) / 4096.0;
    float lp = gt7_pqInverse(l), mp = gt7_pqInverse(m), sp = gt7_pqInverse(s);
    return vec3((2048.0 * lp + 2048.0 * mp) / 4096.0,
                (6610.0 * lp - 13613.0 * mp + 7003.0 * sp) / 4096.0,
                (17933.0 * lp - 17390.0 * mp - 543.0 * sp) / 4096.0);
}

vec3 gt7_ictcpToRgb2020(vec3 ictcp)
{
    float l = ictcp.x + 0.00860904 * ictcp.y + 0.11103 * ictcp.z;
    float m = ictcp.x - 0.00860904 * ictcp.y - 0.11103 * ictcp.z;
    float s = ictcp.x + 0.560031 * ictcp.y - 0.320627 * ictcp.z;
    float ll = gt7_pq(l), ml = gt7_pq(m), sl = gt7_pq(s);
    return max(vec3(dot(vec3(3.43661, -2.50645, 0.0698454), vec3(ll, ml, sl)),
                    dot(vec3(-0.79133, 1.9836, -0.192271), vec3(ll, ml, sl)),
                    dot(vec3(-0.0259499, -0.0989137, 1.12486), vec3(ll, ml, sl))), 0.0);
}

// Linear Rec.2020 frame-buffer values in, tone-mapped values out (HDR: 0..target; SDR: 0..1 after outputScale).
vec3 gt7_apply(vec3 rgb, Gt7Params p)
{
    rgb = max(rgb, 0.0);
    vec3 ucs = gt7_rgb2020ToIctcp(rgb);
    vec3 skewed = vec3(gt7_curve(rgb.r, p), gt7_curve(rgb.g, p), gt7_curve(rgb.b, p));
    vec3 skewedUcs = gt7_rgb2020ToIctcp(skewed);
    float targetUcsL = gt7_rgb2020ToIctcp(vec3(p.target)).x;
    float chromaScale = 1.0 - gt7_smoothstep(ucs.x / targetUcsL, p.fadeStart, p.fadeEnd);
    vec3 scaled = gt7_ictcpToRgb2020(vec3(skewedUcs.x, ucs.y * chromaScale, ucs.z * chromaScale));
    vec3 blended = mix(skewed, scaled, p.blendRatio);
    return p.outputScale * min(blended, vec3(p.target));
}

// Rec.709 <-> Rec.2020, the BT.2087 matrix the lab uses (color.py), and its exact inverse.
vec3 gt7_rec709ToRec2020(vec3 c)
{
    return vec3(dot(vec3(0.6274, 0.3293, 0.0433), c),
                dot(vec3(0.0691, 0.9195, 0.0114), c),
                dot(vec3(0.0164, 0.0880, 0.8956), c));
}

vec3 gt7_rec2020ToRec709(vec3 c)
{
    return vec3(dot(vec3(1.6605112079, -0.5877105887, -0.0728006192), c),
                dot(vec3(-0.1245614060, 1.1329605139, -0.0083991078), c),
                dot(vec3(-0.0181676866, -0.1005605980, 1.1187282846), c));
}
