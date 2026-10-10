#version 450
// The incident-light field from the camera-relative probe clipmap: the two quantities the lit pass reads (rgb
// incoming indirect light, a sky visibility), from the clipmap's traced probes. The clipmap is the scene's only
// diffuse GI; the screen probes, when on, refine this pass's answer.
//
// rgb is ALL the indirect diffuse light arriving (the clipmap's probes carry the sky they see, not a visibility
// fraction of it), so the lit pass adds no sky irradiance of its own when this field is active (frame.uIncident.z).
// Past the clipmap's coarsest level nothing answers: the cooked sky irradiance, as if the sky were open.

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;
// xyz: how the incident light's luminance changes with the normal, at the geometric normal. The lit pass uses it to
// carry the light to its normal-mapped normal (the pre-pass has no normal map). Taken from the CLIPMAP: its
// irradiance at the geometric normal and at two normals tilted ~20 degrees along the surface, from the same
// probes with the same weights, so it is deterministic. (The screen probes' SH gradient was tried first: the
// presented image's median per-pixel variation went 3.40% -> 5.70% with it, its directional bands being the
// noisiest part.) Applied relative, to whatever light the pixel ends with. w unused.
layout(location = 1) out vec4 outIncidentGradient;

layout(set = 0, binding = 0) uniform IncidentClipmap {
    mat4 uInvProjection;
    mat4 uInvView;
    vec4 uTarget;
    vec4 uClipDims;      // xyz probes per level, w base spacing
    vec4 uClipParams;    // x blend band (probes), y 1 + a level to answer from alone (--level-probe; 0: the normal answer),
                         // z a fixed lift (m, --clipmap-lift; 0: a quarter spacing), w the visibility power (0: 3)
    vec4 uOrigin0;
    vec4 uOrigin1;
    vec4 uOrigin2;
    vec4 uOrigin3;
    vec4 uScreen;        // screen probes: x 1 when they answer, yz tiles across and down; w 1: write no gradient
                         // (--no-incident-gradient, the control for what the normal map adds; screen probes or not)
    vec4 uFrameSize;     // xy the frame's pixels (the probes' tiles are in them)
    vec4 uSurfaceGrid;   // the surface probes (probe_surface.glsl, stage 4g-ix): lowest cell xyz, spacing w
    vec4 uSurfaceDims;   // dims xyz, first slot w
    vec4 uSurfaceAtlas;  // tile row, tiles across, slots, w 1 when on
    vec4 uTexelGrid;     // the texels (texel.glsl, stage 5a): the cells' origin xyz, spacing w
    vec4 uTexelParams;   // x hash capacity, y 1 when on, z the rays at which a texel's answer is trusted whole, w 1:
                         // read the spatially filtered light (texel_gather.comp)
    vec4 uTexelDebug;    // x the texel frame (the debug view's stamp test)
    vec4 uTexelLevels;   // x levels, y a pixel's width at unit depth, z the pixels a texel should span (texelLevelFor)
    vec4 uReference;     // the reference view (reference_trace.comp): x 1 when shown, yz its pixels (half the frame's);
                         // w 1: the texel debug view (--texel-debug: r the texels' confidence, g 1 where any was found,
                         // b 1 where the lookup found none)
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
bool screenProbesAt(vec3 worldPos, vec3 n, float viewDepth, out vec3 irradiance, out float confidence) {
    ivec2 tiles = ivec2(g.uScreen.yz);
    vec2 t = vUv * g.uFrameSize.xy / float(SCREEN_PROBE_TILE) - 0.5;
    ivec2 base = ivec2(floor(t));
    vec2 f = t - vec2(base);
    vec3 sum = vec3(0.0);
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
            weight += w;
        }
    }
    irradiance = weight > 1e-6 ? sum / weight : vec3(0.0);
    confidence = fitting > 1e-4 ? clamp(weight / fitting, 0.0, 1.0) : 0.0;
    return weight > 1e-6;
}

#define BLIX_CLIPMAP_IRRADIANCE(t) texelFetch(uClipmapIrradiance, t, 0)
#define BLIX_CLIPMAP_DEPTH(t) texelFetch(uClipmapDepth, t, 0)
#define BLIX_CLIPMAP_IRRADIANCE_FILTERED(p) textureLod(uClipmapIrradiance, (p) / vec2(textureSize(uClipmapIrradiance, 0)), 0.0)
#define BLIX_CLIPMAP_DEPTH_FILTERED(p) textureLod(uClipmapDepth, (p) / vec2(textureSize(uClipmapDepth, 0)), 0.0)
#define BLIX_CLIPMAP_STATE(s) states[s]
// uClipParams.z: a fixed lift in metres for every level (0: a quarter of the spacing); w: the Chebyshev visibility's
// power (0: cubed). The level probe's knobs.
// Copied to globals at the top of main: the library's functions have a local named g.
float clipLiftKnob, clipVisibilityKnob;
#define BLIX_CLIPMAP_LIFT(spacing) (clipLiftKnob > 0.0 ? clipLiftKnob : 0.25 * (spacing))
#define BLIX_CLIPMAP_VISIBILITY(c) (clipVisibilityKnob > 0.0 ? pow(max(c, 0.0), clipVisibilityKnob) : (c) * (c) * (c))
#include "probe_clipmap.glsl"
layout(std430, set = 0, binding = 9) readonly buffer SurfaceIndex { uint surfaceIndex[]; };
#define BLIX_SURFACE_INDEX(i) surfaceIndex[i]
#include "probe_surface.glsl"
layout(std430, set = 0, binding = 10) readonly buffer TexelHash { uvec4 texelHash[]; };
layout(std430, set = 0, binding = 11) readonly buffer Texels { vec4 texels[]; };
layout(std430, set = 0, binding = 12) readonly buffer TexelLight { vec4 texelLight[]; };
// A texel's old light while its gather for a new sun is young (texel_gather.comp): blended out over 256 rays.
layout(std430, set = 0, binding = 13) readonly buffer TexelPrior { vec4 texelPrior[]; };
layout(std430, set = 0, binding = 14) readonly buffer TexelFiltered { vec4 texelFiltered[]; };
layout(std430, set = 0, binding = 15) readonly buffer ReferenceAccum { vec4 referenceAccum[]; };
layout(std430, set = 0, binding = 16) readonly buffer TexelBlend { vec4 texelBlend[]; };
layout(std430, set = 0, binding = 17) readonly buffer TexelStamp { uint texelStamp[]; };
float debugStamped, debugLit;
#define TEXEL_HASH(i) texelHash[i]
#define TEXEL_POSITION(i) texels[2 * (i)]
#define TEXEL_NORMAL(i) texels[2 * (i) + 1]
#include "texel.glsl"

// One level's texels: their irradiance, weighted by distance and area and by how many rays each has; confidence the
// mean of those rays against the trust threshold. False where none of the eight has rays (foliage has no texels; a texel not
// yet gathered has none), and the field above stands.
bool texelsAtLevel(vec3 worldPos, vec3 n, float faceBin, uint level, out vec3 irradiance, out float confidence) {
    TexelGrid finest;
    finest.minCorner = g.uTexelGrid.xyz;
    finest.spacing = g.uTexelGrid.w;
    finest.capacity = uint(g.uTexelParams.x);
    finest.level = 0u;
    TexelGrid tg = texelLevelGrid(finest, level);
    int ids[TEXEL_CANDIDATES];
    float weights[TEXEL_CANDIDATES];
    texelsAround(tg, worldPos, n, faceBin, ids, weights);
    vec3 sum = vec3(0.0);
    float weight = 0.0, raysWeighted = 0.0, located = 0.0;
    float count = 0.0;
    debugStamped = 0.0; debugLit = 0.0;
    for (int i = 0; i < TEXEL_CANDIDATES; ++i) {
        if (ids[i] < 0) continue;
        located += weights[i];
        count += 1.0;
        if (uint(g.uTexelDebug.x) - texelStamp[ids[i]] < 32u) debugStamped += 1.0;
        if (texelLight[ids[i]].w > 0.0) debugLit += 1.0;
        vec4 light = texelLight[ids[i]];
        vec4 prior = texelPrior[ids[i]];
        if (light.w <= 0.0 && prior.w <= 0.0) continue;
        // What texel_gather.comp left it (the occlusion estimate and the gathered light blended), spatially filtered
        // by texel_filter.comp (or not, --texel-filter 0).
        vec3 own = g.uTexelParams.w > 0.5 && texelFiltered[ids[i]].w > 0.0 ? texelFiltered[ids[i]].rgb
            : texelBlend[ids[i]].w > 0.0 ? texelBlend[ids[i]].rgb : light.rgb;
        vec3 value = prior.w > 0.0 ? mix(prior.rgb, own, min(light.w / 256.0, 1.0)) : own;
        sum += weights[i] * value;
        weight += weights[i];
        raysWeighted += weights[i] * (prior.w > 0.0 ? 1.0 : min(light.w / g.uTexelParams.z, 1.0));
    }
    irradiance = weight > 1e-6 ? sum / weight : vec3(0.0);
    confidence = located > 1e-6 ? raysWeighted / located : 0.0;
    debugStamped /= max(count, 1.0); debugLit /= max(count, 1.0);
    return weight > 1e-6;
}

// The texels' answer over levels (texel.glsl, texelLevelFor): from the coarser of the pixel's two down, each finer
// level taking over by its confidence -- the pixel's own level by its share of the blend with the next (the fraction
// of its level), so a level changes smoothly across depth. A new view shows the coarse texels' light within frames
// (there are few of them) and the fine ones fill in where they have rays.
bool texelsAt(vec3 worldPos, vec3 n, float faceBin, float viewDepth, out vec3 irradiance, out float confidence) {
    float levelWanted = texelLevelFor(viewDepth, g.uTexelLevels, g.uTexelGrid.w);
    int finest = int(floor(levelWanted));
    float fraction = levelWanted - float(finest);
    int coarsest = min(finest + 1, int(g.uTexelLevels.x) - 1);
    irradiance = vec3(0.0);
    confidence = 0.0;
    bool any = false;
    float stampedSum = 0.0, litSum = 0.0;
    for (int level = coarsest; level >= finest; --level) {
        vec3 value;
        float c;
        if (!texelsAtLevel(worldPos, n, faceBin, uint(level), value, c)) continue;
        if (level == finest) { stampedSum = debugStamped; litSum = debugLit; }
        float take = level == finest && finest < coarsest ? c * (1.0 - fraction) : c;
        if (!any) { irradiance = value; confidence = c; any = true; take = 1.0; }
        irradiance = mix(irradiance, value, take);
        confidence = confidence + (1.0 - confidence) * c;
    }
    debugStamped = stampedSum; debugLit = litSum;
    return any;
}

void main() {
    clipLiftKnob = g.uClipParams.z;
    clipVisibilityKnob = g.uClipParams.w;
    // Exact fetches, as texel_mark.comp reads them: the texels a pixel weighs must be the ones the mark queued for it.
    ivec2 pixel = clamp(ivec2(vUv * vec2(textureSize(uSceneDepth, 0))), ivec2(0), textureSize(uSceneDepth, 0) - 1);
    float raw = texelFetch(uSceneDepth, pixel, 0).r;
    if (raw >= 1.0 - 1e-6) {
        outIncident = vec4(0.0, 0.0, 0.0, 1.0);
        outIncidentGradient = vec4(0.0);
        return;
    }
    vec2 pixelUv = (vec2(pixel) + 0.5) / vec2(textureSize(uSceneDepth, 0));
    vec4 view = g.uInvProjection * vec4(pixelUv * 2.0 - 1.0, raw, 1.0);
    vec3 worldPos = (g.uInvView * vec4(view.xyz / view.w, 1.0)).xyz;
    float viewDepth = abs(view.z / view.w);
    vec4 nSample = texelFetch(uPrepassNormal, pixel, 0);
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
    // Tangents, and two normals tilted along them; s is how far each moves the normal along its tangent.
    vec3 t1 = normalize(cross(abs(N.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), N));
    vec3 t2 = cross(N, t1);
    const float tilt = 0.35;
    const float s = tilt / 1.0595;   // tilt / sqrt(1 + tilt^2)
    vec3 e1, e2;
    if (g.uClipParams.y > 0.5) {
        // One level alone, no blend (the level probe): where its block holds the point and a probe answers; -1 where not.
        int forced = int(g.uClipParams.y + 0.5) - 1;
        float w = 0.0;
        vec4 f = blix_clipmapInsideDistance(c, forced, worldPos) >= 0.0 ? blix_clipmapLevelSample(c, forced, worldPos, N, w) : vec4(0.0);
        outIncident = w > 0.0 ? f : vec4(-1.0);
        outIncidentGradient = vec4(0.0);
        return;
    }
    vec4 field = blix_clipmapSampleDirs(c, worldPos, N, normalize(N + tilt * t1), normalize(N + tilt * t2), found, e1, e2);
    outIncident = found ? field : vec4(texture(uIrradiance, N).rgb, 1.0);
    // The surface probes answer where they exist: the same surface keeps the same probes wherever the camera stands.
    // The gradient below stays the clipmap's (relative, applied to whatever light the pixel ends with).
    BlixSurfaceGrid sg;
    sg.minCell = ivec3(g.uSurfaceGrid.xyz);
    sg.spacing = g.uSurfaceGrid.w;
    sg.dims = ivec3(g.uSurfaceDims.xyz);
    sg.firstSlot = int(g.uSurfaceDims.w);
    sg.tileRow0 = int(g.uSurfaceAtlas.x);
    sg.columns = int(g.uSurfaceAtlas.y);
    sg.enabled = g.uSurfaceAtlas.w > 0.5 && g.uClipParams.y < 0.5;   // a forced level is the clipmap's alone
    float surfaceWeight, surfaceConfidence;
    vec4 surfaceField = blix_surfaceSample(sg, worldPos, N, surfaceWeight, surfaceConfidence);
    if (surfaceWeight > 0.0) outIncident = found ? mix(outIncident, surfaceField, surfaceConfidence) : surfaceField;
    vec3 gathered;
    float confidence;
    bool texelFound = false;
    if (g.uTexelParams.y > 0.5 && g.uClipParams.y < 0.5 && texelsAt(worldPos, N, nSample.w, viewDepth, gathered, confidence)) {
        outIncident.rgb = mix(outIncident.rgb, gathered, confidence);
        texelFound = true;
    }
    if (g.uReference.w > 0.5) {
        // r confidence, g the share of the texels read the mark stamped in the last 32 frames, b the share with rays.
        outIncident = vec4(texelFound ? confidence : 0.0, debugStamped, debugLit, 1.0);
        outIncidentGradient = vec4(0.0);
        return;
    }
    if (g.uScreen.x > 0.5 && screenProbesAt(worldPos, N, viewDepth, gathered, confidence)) {
        outIncident.rgb = mix(outIncident.rgb, gathered, confidence);
    }
    // The reference view: the path-traced indirect light in place of every answer above (the gradient below still
    // carries it to the normal map, relative).
    if (g.uReference.x > 0.5) {
        ivec2 rp = min(ivec2(vUv * g.uReference.yz), ivec2(g.uReference.yz) - 1);
        vec4 acc = referenceAccum[rp.y * int(g.uReference.y) + rp.x];
        if (acc.w > 0.0) outIncident.rgb = acc.rgb / acc.w;
    }
    outIncidentGradient = vec4(0.0);
    const vec3 luma = vec3(0.2126, 0.7152, 0.0722);
    float lum0 = dot(field.rgb, luma);
    if (found && g.uScreen.w < 0.5 && lum0 > 1e-6) {
        // Relative change per unit of normal displacement, from the clipmap; scaled to the light the pixel ends with.
        vec3 relative = (t1 * (dot(e1, luma) - lum0) + t2 * (dot(e2, luma) - lum0)) / (s * lum0);
        outIncidentGradient = vec4(relative * dot(outIncident.rgb, luma), 0.0);
    }
}
