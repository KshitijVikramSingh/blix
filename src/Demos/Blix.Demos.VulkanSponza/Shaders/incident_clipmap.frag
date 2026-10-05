#version 450
// The incident-light field from the camera-relative probe clipmap (--gi-clipmap): what incident.frag writes, the
// same two quantities for the lit pass (rgb bounce, a sky visibility), read from the clipmap's traced probes rather
// than from the baked bounds-sized volumes. Its own program because it declares the clipmap's state buffer, which
// only exists when the clipmap does.
//
// rgb is ALL the indirect diffuse light arriving (the clipmap's probes carry the sky they see, not a visibility
// fraction of it), so the lit pass adds no sky irradiance of its own when this field is active (frame.uIncident.w).
// Past the clipmap's coarsest level nothing answers: the cooked sky irradiance, as if the sky were open.

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;
// xyz: how the incident light's luminance changes with the normal, at the geometric normal (screen probes'
// radiance gives it exactly; the clipmap answers for one normal only, and contributes none). The lit pass uses it
// to carry the light to its normal-mapped normal. w unused.
layout(location = 1) out vec4 outIncidentGradient;

layout(set = 0, binding = 0) uniform IncidentClipmap {
    mat4 uInvProjection;
    mat4 uInvView;
    vec4 uTarget;
    vec4 uClipDims;      // xyz probes per level, w base spacing
    vec4 uClipParams;    // x blend band (probes)
    vec4 uOrigin0;
    vec4 uOrigin1;
    vec4 uOrigin2;
    vec4 uOrigin3;
    vec4 uScreen;        // screen probes: x 1 when they answer, yz tiles across and down; w 1: write no gradient
                         // (--no-incident-gradient, the control for what the normal map adds)
    vec4 uFrameSize;     // xy the frame's pixels (the probes' tiles are in them)
} g;

layout(set = 0, binding = 1) uniform sampler2D uSceneDepth;
layout(set = 0, binding = 2) uniform sampler2D uPrepassNormal;
layout(set = 0, binding = 3) uniform sampler2D uClipmapIrradiance;
layout(set = 0, binding = 4) uniform sampler2D uClipmapDepth;
layout(std430, set = 0, binding = 5) readonly buffer ClipmapState { uvec4 states[]; };
layout(set = 0, binding = 6) uniform samplerCube uIrradiance;
#include "screen_probe.glsl"
layout(std430, set = 0, binding = 7) readonly buffer ScreenProbeTiles { ScreenProbeTile tileHeaders[]; };
layout(std430, set = 0, binding = 8) readonly buffer ScreenProbes { ScreenProbe probes[]; };

// With screen probes: the probes of the four tiles around the pixel (as each tile's header names them), each
// weighted by its tile's bilinear share, how near its normal is to the pixel's, and how near the pixel lies to its
// plane (a probe on another surface tells nothing). Their radiance becomes irradiance here, for this pixel's normal.
// False where none of the four fits, and the clipmap's answer stands. confidence is how much of the fitting probes'
// weight is accumulated history (a probe counts fully from 8 frames): foliage and depth edges, where TAA's jitter
// puts each frame's probe on another surface, never accumulate, and there the clipmap's steady answer stays.
bool screenProbesAt(vec3 worldPos, vec3 n, float viewDepth, out vec3 irradiance, out vec3 gradient, out float confidence) {
    ivec2 tiles = ivec2(g.uScreen.yz);
    vec2 t = vUv * g.uFrameSize.xy / float(SCREEN_PROBE_TILE) - 0.5;
    ivec2 base = ivec2(floor(t));
    vec2 f = t - vec2(base);
    vec3 sum = vec3(0.0), gradientSum = vec3(0.0);
    float weight = 0.0, fitting = 0.0;
    for (int i = 0; i < 4; ++i) {
        ivec2 o = ivec2(i & 1, i >> 1);
        ivec2 tile = base + o;
        if (any(lessThan(tile, ivec2(0))) || any(greaterThanEqual(tile, tiles))) continue;
        ScreenProbeTile header = tileHeaders[tile.y * tiles.x + tile.x];
        float share = (o.x == 1 ? f.x : 1.0 - f.x) * (o.y == 1 ? f.y : 1.0 - f.y);
        for (uint j = 0u; j < min(header.y, uint(SCREEN_PROBE_MAX_PER_TILE)); ++j) {
            int index = int(header.x + j);
            vec3 pn = probes[index].normal.xyz;
            float facing = max(dot(pn, n), 0.0);
            float planeDistance = abs(dot(pn, worldPos - probes[index].position.xyz));
            float w = share * pow(facing, 8.0) * exp(-planeDistance / (0.01 * viewDepth));
            fitting += w;
            // A probe counts by what it has accumulated: one that just started (8 rays) yields to settled ones.
            w *= min(probes[index].normal.w / 8.0, 1.0);
            if (w <= 1e-5) continue;
            vec4 radiance[9];
            for (int k = 0; k < 9; ++k) radiance[k] = probes[index].radiance[k];
            sum += w * screenProbeIrradiance(radiance, n);
            gradientSum += w * screenProbeIrradianceGradient(radiance, n);
            weight += w;
        }
    }
    irradiance = weight > 1e-6 ? sum / weight : vec3(0.0);
    gradient = weight > 1e-6 ? gradientSum / weight : vec3(0.0);
    confidence = fitting > 1e-4 ? clamp(weight / fitting, 0.0, 1.0) : 0.0;
    return weight > 1e-6;
}

#define BLIX_CLIPMAP_IRRADIANCE(t) texelFetch(uClipmapIrradiance, t, 0)
#define BLIX_CLIPMAP_DEPTH(t) texelFetch(uClipmapDepth, t, 0)
#define BLIX_CLIPMAP_STATE(s) states[s]
#include "probe_clipmap.glsl"

void main() {
    float raw = texture(uSceneDepth, vUv).r;
    if (raw >= 1.0 - 1e-6) {
        outIncident = vec4(0.0, 0.0, 0.0, 1.0);
        outIncidentGradient = vec4(0.0);
        return;
    }
    vec4 view = g.uInvProjection * vec4(vUv * 2.0 - 1.0, raw, 1.0);
    vec3 worldPos = (g.uInvView * vec4(view.xyz / view.w, 1.0)).xyz;
    float viewDepth = abs(view.z / view.w);
    vec4 nSample = texture(uPrepassNormal, vUv);
    vec3 N = dot(nSample.xyz, nSample.xyz) > 1e-6
        ? normalize(nSample.xyz)
        : normalize((g.uInvView * vec4(0.0, 0.0, 1.0, 0.0)).xyz);

    BlixClipmap c;
    c.dims = ivec3(g.uClipDims.xyz);
    c.baseSpacing = g.uClipDims.w;
    c.blendProbes = g.uClipParams.x;
    c.origin[0] = ivec3(g.uOrigin0.xyz);
    c.origin[1] = ivec3(g.uOrigin1.xyz);
    c.origin[2] = ivec3(g.uOrigin2.xyz);
    c.origin[3] = ivec3(g.uOrigin3.xyz);
    bool found;
    vec4 field = blix_clipmapSample(c, worldPos, N, found);
    outIncident = found ? field : vec4(texture(uIrradiance, N).rgb, 1.0);
    outIncidentGradient = vec4(0.0);
    vec3 gathered, gradient;
    float confidence;
    if (g.uScreen.x > 0.5 && screenProbesAt(worldPos, N, viewDepth, gathered, gradient, confidence)) {
        outIncident.rgb = mix(outIncident.rgb, gathered, confidence);
        // The blend is linear in the light, so its gradient is the probes' scaled by their share.
        outIncidentGradient = g.uScreen.w > 0.5 ? vec4(0.0) : vec4(gradient * confidence, 0.0);
    }
}
