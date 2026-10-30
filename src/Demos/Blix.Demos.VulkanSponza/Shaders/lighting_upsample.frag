#version 450

// The incident light (RTGI's readout, rtgi_readout.frag) and, with RenderSettings.HalfResShadows, the traced sun shadows
// (rt_shadow_rays.comp) ran at half resolution, each half-res pixel h standing for ONE full-res pixel of its 2x2,
// 2h + uHalf.yz, a different one each frame (all four every four frames, TAA gathering them). This brings both back
// to full resolution for the lit pass, into the targets it reads either way.
//
// A full-res pixel q takes the four half-res samples around it, each weighted by
//   - where its pixel is: bilinear over the 2-pixel grid the samples' pixels sit on (q's own sample, when q is the
//     pixel this frame's sample stands for, takes all the weight);
//   - whether it is on q's surface: its point's distance from q's tangent plane, against a tolerance that grows with
//     the distance from the camera, and its normal against q's -- so light never crosses a silhouette or a crease;
//   - sky samples none.
// Where no neighbour is on q's surface (a thin wire, a leaf's edge), the one nearest q's plane answers alone, rather
// than a blend of surfaces q is not on.
//
// The sun's visibility rides in the gradient's w (the lit pass reads it there when frame.uIncident.w says so, see
// lit.frag rtSunVisibility): each sample's traced word, 1 where the sun reaches, weighted the same way.
//
// Penumbrae: each ray went toward its own point of the sun's disc (rt_shadow_rays.comp, ShadowsSettings.SunDiscDegrees),
// so where a penumbra falls neighbouring rays disagree, and the visibility there is how many of them reached. How
// wide to average: a penumbra is as wide as the blocker's distance times the disc's angle (the Metal pass writes how far
// each blocked ray went, the word's upper half), so the filter's radius in pixels is that width over a pixel's
// footprint at q's distance -- hard at contact, soft far from the blocker, as a real sun's shadows are. Taps on other
// surfaces are left out as above; TAA averages what remains over frames.

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;
layout(location = 1) out vec4 outIncidentGradient;

layout(set = 0, binding = 0) uniform LightingUpsample {
    mat4 uInvProjection;   // the jittered projection the pre-pass drew with, inverted
    mat4 uInvView;
    vec4 uHalf;            // x 1: the shadows are half-res too (carry the mask); yz: the full-res pixel of its 2x2 this frame's half-res samples stand for; w the tangent of
                           // the sun's angular radius (0: a point sun, no penumbra filter)
    vec4 uPixel;           // x a full-res pixel's angular size (radians at the centre of the view); y 1: a pixel no sample is on
                           // the surface of is marked (rgb -1) for its own lookup (rtgi_temporal.frag's fix-up)
} u;

layout(set = 0, binding = 1) uniform sampler2D uIncidentHalf;
layout(set = 0, binding = 2) uniform sampler2D uIncidentGradientHalf;
layout(set = 0, binding = 3) uniform sampler2D uSceneDepth;
layout(set = 0, binding = 4) uniform sampler2D uPrepassNormal;
// The half-res traced mask (SponzaLoop.RtShadows): width, height, 1 while live (0 for the placeholder: no traced
// shadows, the lit pass reads no w), 0; then a word per half-res pixel.
layout(std430, set = 0, binding = 5) readonly buffer RtShadowMask { uvec4 rtShadowHeader; uint rtShadowWords[]; };

vec3 worldAt(ivec2 pixel, float raw, vec2 fullSize) {
    vec2 uv = (vec2(pixel) + 0.5) / fullSize;
    vec4 view = u.uInvProjection * vec4(uv * 2.0 - 1.0, raw, 1.0);
    return (u.uInvView * vec4(view.xyz / view.w, 1.0)).xyz;
}

// x 1 where this frame's ray from half-res pixel h reached the sun; y how far it went before something blocked it (0
// where it reached). Not live yet (a size of zero): the sun is off, as lit.frag has it.
vec2 sunAt(ivec2 h) {
    // uHalf.x 0: the shadows are traced at full resolution (RenderSettings.HalfResShadows off) and lit.frag reads the
    // mask itself; nothing to carry here.
    if (u.uHalf.x < 0.5 || rtShadowHeader.z == 0u) return vec2(1.0, 0.0);
    if (uint(h.x) >= rtShadowHeader.x || uint(h.y) >= rtShadowHeader.y) return vec2(0.0);
    uint word = rtShadowWords[uint(h.y) * rtShadowHeader.x + uint(h.x)];
    return (word & 1u) != 0u ? vec2(0.0, unpackHalf2x16(word >> 16).x) : vec2(1.0, 0.0);
}

// The surface weight of a half-res sample (as in main): its point's distance from q's plane and its normal's agreement.
float surfaceWeight(ivec2 rep, vec3 pQ, vec3 nQ, float tolerance, vec2 fullSize) {
    float raw = texelFetch(uSceneDepth, rep, 0).r;
    if (raw >= 1.0 - 1e-6) return 0.0;
    vec3 p = worldAt(rep, raw, fullSize);
    float plane = abs(dot(nQ, p - pQ));
    vec4 nSample = texelFetch(uPrepassNormal, rep, 0);
    float facing = dot(nSample.xyz, nSample.xyz) > 1e-6 ? max(dot(normalize(nSample.xyz), nQ), 0.0) : 0.0;
    return exp(-(plane * plane) / (tolerance * tolerance)) * pow(facing, 8.0);
}

void main() {
    ivec2 fullSize = textureSize(uSceneDepth, 0);
    ivec2 halfSize = textureSize(uIncidentHalf, 0);
    ivec2 q = clamp(ivec2(gl_FragCoord.xy), ivec2(0), fullSize - 1);
    float rawQ = texelFetch(uSceneDepth, q, 0).r;
    if (rawQ >= 1.0 - 1e-6) {
        // Sky: as the readout leaves it.
        outIncident = vec4(0.0, 0.0, 0.0, 1.0);
        outIncidentGradient = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }
    vec3 pQ = worldAt(q, rawQ, vec2(fullSize));
    vec4 nSampleQ = texelFetch(uPrepassNormal, q, 0);
    vec3 cameraPos = (u.uInvView * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    vec3 nQ = dot(nSampleQ.xyz, nSampleQ.xyz) > 1e-6 ? normalize(nSampleQ.xyz) : normalize(cameraPos - pQ);
    // How far off q's plane a point may sit and still be q's surface: depth precision and the pre-pass's own
    // facets, both growing with the distance.
    float tolerance = 0.005 + 0.004 * length(pQ - cameraPos);

    ivec2 offset = ivec2(u.uHalf.yz);
    ivec2 base = ivec2(floor(vec2(q - offset) * 0.5));
    vec4 incident = vec4(0.0), gradient = vec4(0.0);
    float total = 0.0;
    float blocker = 0.0, blocked = 0.0;
    int lit = 0, sampled = 0;
    float bestPlane = 1e30;
    vec4 bestIncident = vec4(0.0, 0.0, 0.0, 1.0), bestGradient = vec4(0.0, 0.0, 0.0, 1.0);
    for (int k = 0; k < 4; ++k) {
        ivec2 h = clamp(base + ivec2(k & 1, k >> 1), ivec2(0), halfSize - 1);
        ivec2 rep = min(h * 2 + offset, fullSize - 1);
        float raw = texelFetch(uSceneDepth, rep, 0).r;
        if (raw >= 1.0 - 1e-6) continue;
        vec2 d = abs(vec2(rep - q)) * 0.5;
        float wPixel = max(1.0 - d.x, 0.0) * max(1.0 - d.y, 0.0);
        vec3 p = worldAt(rep, raw, vec2(fullSize));
        float plane = abs(dot(nQ, p - pQ));
        vec4 nSample = texelFetch(uPrepassNormal, rep, 0);
        float facing = dot(nSample.xyz, nSample.xyz) > 1e-6 ? max(dot(normalize(nSample.xyz), nQ), 0.0) : 0.0;
        float wSurface = exp(-(plane * plane) / (tolerance * tolerance)) * pow(facing, 8.0);
        vec4 sampleIncident = texelFetch(uIncidentHalf, h, 0);
        vec2 sun = sunAt(h);
        vec4 sampleGradient = vec4(texelFetch(uIncidentGradientHalf, h, 0).xyz, sun.x);
        float w = wPixel * wSurface;
        if (wSurface > 0.05) {
            sampled++;
            if (sun.x > 0.5) lit++; else { blocker += sun.y; blocked += 1.0; }
        }
        incident += sampleIncident * w;
        gradient += sampleGradient * w;
        total += w;
        // The fallback: nearest q's plane, its normal agreeing breaking ties toward the same surface.
        float score = plane / tolerance + (1.0 - facing) * 4.0;
        if (score < bestPlane) {
            bestPlane = score;
            bestIncident = sampleIncident;
            bestGradient = sampleGradient;
        }
    }
    if (sampled == 0 && u.uPixel.y > 0.5) {
        // Marked: the nearest sample is another surface's light (a crevice's other face, its lip) -- stippled through the
        // crevices testbed's gaps (the user, 2026-10-29).
        outIncident = vec4(-1.0, -1.0, -1.0, 1.0);
        outIncidentGradient = vec4(0.0, 0.0, 0.0, bestGradient.w);
    } else if (total > 1e-4) {
        outIncident = incident / total;
        outIncidentGradient = gradient / total;
    } else {
        outIncident = bestIncident;
        outIncidentGradient = bestGradient;
    }

    // The penumbra: as wide as the blocker's distance times the disc (both edges of it), over a pixel's footprint here.
    if (u.uHalf.w <= 0.0 || blocked < 0.5 || rtShadowHeader.z == 0u) return;
    float viewDistance = length(pQ - cameraPos);
    float widthPixels = 2.0 * u.uHalf.w * (blocker / blocked) / max(viewDistance * u.uPixel.x, 1e-6);
    // Its half-width in half-res samples (two full-res pixels each), at most 4 each way.
    float radius = min(widthPixels * 0.25, 4.0);
    if (radius < 0.75) return;
    int r = int(ceil(radius));
    ivec2 centre = clamp(ivec2(round(vec2(q - offset) * 0.5)), ivec2(0), halfSize - 1);
    // Where the four nearest agree and four taps at the radius do too, q is inside an umbra (or out of the penumbra):
    // nothing to average, and most of a shadowed frame stops here.
    if (lit == 0 || lit == sampled) {
        float edge = 0.0;
        edge += sunAt(clamp(centre + ivec2(r, 0), ivec2(0), halfSize - 1)).x;
        edge += sunAt(clamp(centre - ivec2(r, 0), ivec2(0), halfSize - 1)).x;
        edge += sunAt(clamp(centre + ivec2(0, r), ivec2(0), halfSize - 1)).x;
        edge += sunAt(clamp(centre - ivec2(0, r), ivec2(0), halfSize - 1)).x;
        if (edge == (lit == 0 ? 0.0 : 4.0)) return;
    }
    float sigma = 0.5 * radius + 0.5;
    float sum = 0.0, weight = 0.0;
    for (int dy = -r; dy <= r; ++dy) {
        for (int dx = -r; dx <= r; ++dx) {
            ivec2 h = centre + ivec2(dx, dy);
            if (any(lessThan(h, ivec2(0))) || any(greaterThanEqual(h, halfSize))) continue;
            ivec2 rep = min(h * 2 + offset, fullSize - 1);
            vec2 d = vec2(rep - q) * 0.5;
            float w = exp(-dot(d, d) / (2.0 * sigma * sigma)) * surfaceWeight(rep, pQ, nQ, tolerance, vec2(fullSize));
            sum += sunAt(h).x * w;
            weight += w;
        }
    }
    if (weight > 1e-4) outIncidentGradient.w = sum / weight;
}
