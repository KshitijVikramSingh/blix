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
    vec4 uLightmap;      // --lightmap: x 1 when the atlas's texels are on (texel.glsl), yz the atlas size, w its first record
    vec4 uTexelLevels;   // x levels, y a pixel's width at unit depth, z / w the pixels a texel should span in sun / in shade
    vec4 uTexelSun;      // xyz the direction the sun travels (texelLevelFor's sunlit)
    mat4 uTexelCascadeVP[3];
    vec4 uReference;     // the reference view (reference_trace.comp): x 1 when shown, yz its pixels (half the frame's);
                         // w 1: the texel debug view (--texel-debug: r the texels' confidence, g 1 where any was found,
                         // b 1 where the lookup found none); w 2: the records view (--texel-debug-records)
    vec4 uClipExact;     // x 1: whether a point sees each probe, TRACED (--clipmap-exact-visibility): the ceiling a
                         // cooked per-texel mask could reach, in place of the Chebyshev estimate; y / z DDGI's surface
                         // bias, along the normal / toward the viewer, in probe spacings (--clipmap-normal-bias /
                         // --clipmap-view-bias; both 0: the default lift); w 1: probes relocated (--clipmap-relocate)
    vec4 uTransfer;      // x 1: fitted transfer weights answer the pixels that have them (--transfer-weights,
                         // transferAt), texels unread; y the first atlas record
    vec4 uTransferStaleMin; // what moves (--mover), widened by a level-0 cell: a texel inside holds a transfer the mover
    vec4 uTransferStaleMax; // may have made untrue, and the probes answer it instead; w of the min 1 when there is one
    vec4 uHalf;          // x 1: this target is half the frame (RenderSettings.HalfResLighting), each pixel standing for
                         // the full-res pixel 2p + yz of its 2x2 (a different one each frame; lighting_upsample.frag)
} g;

layout(set = 0, binding = 1) uniform sampler2D uSceneDepth;
layout(set = 0, binding = 2) uniform sampler2D uPrepassNormal;
layout(set = 0, binding = 18) uniform sampler2D uTexelCascadeMaps[3];
#include "shadow.glsl"
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
// --clipmap-exact-visibility: a ray from the surface point to each probe decides whether it counts (the library's
// BLIX_CLIPMAP_PROBE_VISIBILITY). Eight rays a level a pixel: an experiment's price, not a renderer's.
#define BLIX_RAY_SET 0
#define BLIX_RAY_BINDING 24
#include "ray_query.glsl"
float clipExactKnob;
uint clipExactSeed;
float clipmapTracedVisibility(vec3 world, vec3 n, vec3 probe) {
    vec3 o = world + n * 0.01;
    vec3 d = probe - o;
    float len = length(d);
    if (len < 1e-4) return 1.0;
    clipExactSeed = blix_pcg(clipExactSeed);
    return blix_traceAny(o, d / len, 0.0, len, clipExactSeed) ? 0.0 : 1.0;
}
#define BLIX_CLIPMAP_PROBE_VISIBILITY(world, n, probe) (clipExactKnob > 0.5 ? clipmapTracedVisibility(world, n, probe) : -1.0)
// --clipmap-relocate: each probe where clipmap_inject.comp moved it (its offset from the cell's centre, by slot).
layout(std430, set = 0, binding = 32) readonly buffer ClipmapOffsets { vec4 clipmapOffsets[]; };
float clipRelocate;
#define BLIX_CLIPMAP_PROBE_OFFSET(s) (clipRelocate > 0.5 ? clipmapOffsets[s].xyz : vec3(0.0))
// DDGI's surface bias (--clipmap-normal-bias, --clipmap-view-bias): toward the viewer is the side of a wall the pixel is
// seen from. clipViewDir, the point to the camera, is set in main.
float clipNormalBias, clipViewBias;
vec3 clipViewDir;
#define BLIX_CLIPMAP_BIAS(world, n, spacing) (clipNormalBias > 0.0 || clipViewBias > 0.0 \
    ? ((n) * clipNormalBias + clipViewDir * clipViewBias) * (spacing) : (n) * BLIX_CLIPMAP_LIFT(spacing))
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
// --lightmap: the pre-pass's packed atlas texel and the atlas's map from texel to record (texel.glsl).
layout(set = 0, binding = 19) uniform usampler2D uLightmapTexel;
layout(std430, set = 0, binding = 20) readonly buffer LightmapIndexMap { uint lightmapIndexMap[]; };
#define TEXEL_INDEX_MAP(i) lightmapIndexMap[i]
// --transfer-weights: per atlas record (id - uTransfer.y), fitted weights over its 8 level-0 probes, 8 halves.
layout(std430, set = 0, binding = 33) readonly buffer TransferWeights { uvec4 transferWeights[]; };
// This pixel's packed atlas texel (main sets it): not 0, the atlas's texels are its level 0 and the hashed cells its
// coarser levels; 0 (no lightmap, or a surface the atlas leaves out), the hashed cells at every level.
uint pixelLightmapTexel;
#define TEXEL_HASH(i) texelHash[i]
#define TEXEL_POSITION(i) texels[2 * (i)]
#define TEXEL_NORMAL(i) texels[2 * (i) + 1]
#include "texel.glsl"

// The pixel's own direct sun, 0-1, for its texel level (texelLevelFor): facing away, or shadowed in the cascades, is
// shade; past the cascades, sun (far, where coarse texels are all a pixel can hold anyway).
float texelSunlit(vec3 world, vec3 n) {
    vec3 toSun = -normalize(g.uTexelSun.xyz);
    float ndotl = dot(n, toSun);
    if (ndotl <= 0.0) return 0.0;
    int cascade = -1;
    float lit = blix_sun_shadow_cascaded_hard(uTexelCascadeMaps[0], uTexelCascadeMaps[1], uTexelCascadeMaps[2],
        g.uTexelCascadeVP[0], g.uTexelCascadeVP[1], g.uTexelCascadeVP[2], world + n * 0.01, ndotl, cascade);
    return cascade < 0 ? 1.0 : lit;
}

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
    vec2 atlasTexel;
    if (level == 0u && texelLightmapUnpack(pixelLightmapTexel, atlasTexel)) {
        // Lightmap texels: the four bilinear atlas texels at the pixel's lightmap coordinate.
        for (int i = 0; i < TEXEL_CANDIDATES; ++i) { ids[i] = -1; weights[i] = 0.0; }
        int lm[4];
        float lw[4];
        texelsAroundLightmap(atlasTexel, n, ivec2(g.uLightmap.yz), lm, lw);
        for (int i = 0; i < 4; ++i) { ids[i] = lm[i]; weights[i] = lw[i]; }
    } else
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
    float levelWanted = texelLevelFor(viewDepth, g.uTexelLevels, g.uTexelGrid.w, texelSunlit(worldPos, n));
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
        // The first level found is the answer so far, its confidence its own (accumulating it again here, as this did,
        // read one level at 0.5 as 0.75 -- and let half-gathered texels push the clipmap out early).
        if (!any) { irradiance = value; confidence = c; any = true; continue; }
        irradiance = mix(irradiance, value, take);
        confidence = confidence + (1.0 - confidence) * c;
    }
    debugStamped = stampedSum; debugLit = litSum;
    return any;
}

// The surface transfer atlas (plan.md, stage 5): the pixel's nearest atlas texel's fitted weights over the 8 level-0
// probes around that texel's lookup point (its record's position lifted a quarter cell along its face normal, as the
// fit had it: SponzaLoop.TransferFit.cs), each probe read at the pixel's normal. False where the texel has no weights,
// or a probe the fit leaned on is not solved (or is buried) here now: today's blend answers instead.
// One atlas record's transfer: its weights over its 8 level-0 probes, read at N. False where it has none, where a
// probe it weighs is not there (unsolved, buried, another cell), or where a mover may have made it untrue.
bool transferRecord(BlixClipmap c, vec3 N, uint id, out vec3 irradiance) {
    irradiance = vec3(0.0);
    int rec = int(id) - int(g.uTransfer.y + 0.5);
    if (rec < 0) return false;
    uvec4 packed = transferWeights[rec];
    if (packed == uvec4(0u)) return false;
    float w[8];
    vec2 h;
    h = unpackHalf2x16(packed.x); w[0] = h.x; w[1] = h.y;
    h = unpackHalf2x16(packed.y); w[2] = h.x; w[3] = h.y;
    h = unpackHalf2x16(packed.z); w[4] = h.x; w[5] = h.y;
    h = unpackHalf2x16(packed.w); w[6] = h.x; w[7] = h.y;
    float spacing = c.baseSpacing;
    // A mover near the texel: what its transfer encodes -- the geometry within about a probe cell of it -- may have
    // changed, a curtain across the light over a wall that did not move. It is not corrected by a delta (the probes'
    // answer and the transfer's estimate the same bounce: adding would count it twice); the probes REPLACE it.
    vec3 where = TEXEL_POSITION(id).xyz;
    if (g.uTransferStaleMin.w > 0.5 && all(greaterThanEqual(where, g.uTransferStaleMin.xyz)) && all(lessThanEqual(where, g.uTransferStaleMax.xyz)))
        return false;
    vec3 p = where + normalize(TEXEL_NORMAL(id).xyz) * (0.25 * spacing);
    ivec3 base = ivec3(floor(p / spacing - 0.5));
    vec3 sum = vec3(0.0);
    for (int k = 0; k < 8; ++k) {
        if (w[k] <= 0.0) continue;
        ivec3 cell = base + ivec3(k & 1, (k >> 1) & 1, (k >> 2) & 1);
        ivec3 slot = blix_clipmapSlot(c, cell);
        uvec4 state = BLIX_CLIPMAP_STATE(blix_clipmapSlotIndex(c, slot));
        if ((state.w & BLIX_CLIPMAP_SOLVED) == 0u || (state.w & BLIX_CLIPMAP_BURIED) != 0u || ivec3(state.xyz) != cell) return false;
        sum += w[k] * blix_clipmapTileSample(blix_clipmapTileOrigin(c, 0, slot), N, false).rgb;
    }
    irradiance = sum;
    return true;
}

// The transfer at the pixel: bilinear over the four atlas texels around its atlas point, each read through its own
// probes, those that cannot answer (no weights, another face at a chart's edge, stale) left out and the rest
// renormalised. A texel's transfer is fitted alone, so the nearest one's alone printed the atlas grid as steps
// (Cornell's room view, the first run on clean probes); uTransfer.z 1 keeps the nearest (--transfer-nearest, the A/B).
bool transferAt(BlixClipmap c, vec3 N, out vec3 irradiance) {
    irradiance = vec3(0.0);
    vec2 atlasTexel;
    if (!texelLightmapUnpack(pixelLightmapTexel, atlasTexel)) return false;
    ivec2 size = ivec2(g.uLightmap.yz);
    if (g.uTransfer.z > 0.5) {
        ivec2 at = ivec2(floor(atlasTexel));
        if (any(lessThan(at, ivec2(0))) || any(greaterThanEqual(at, size))) return false;
        uint id = lightmapIndexMap[at.y * size.x + at.x];
        if (id == 0xFFFFFFFFu) return false;
        if (TEXEL_POSITION(id).w < 0.0 && dot(TEXEL_NORMAL(id).xyz, N) < 0.0) id += 1u;
        return transferRecord(c, N, id, irradiance);
    }
    int ids[4];
    float weights[4];
    texelsAroundLightmap(atlasTexel, N, size, ids, weights);
    vec3 sum = vec3(0.0);
    float total = 0.0;
    for (int i = 0; i < 4; ++i) {
        if (ids[i] < 0 || weights[i] <= 0.0) continue;
        if (dot(normalize(TEXEL_NORMAL(uint(ids[i])).xyz), N) < 0.7) continue;
        vec3 e;
        if (!transferRecord(c, N, uint(ids[i]), e)) continue;
        sum += weights[i] * e;
        total += weights[i];
    }
    if (total <= 1e-4) return false;
    irradiance = sum / total;
    return true;
}

void main() {
    clipLiftKnob = g.uClipParams.z;
    clipVisibilityKnob = g.uClipParams.w;
    clipExactKnob = g.uClipExact.x;
    clipRelocate = g.uClipExact.w;
    clipExactSeed = blix_pcg(uint(gl_FragCoord.x) * 7919u + uint(gl_FragCoord.y) * 104729u);
    // Exact fetches, as texel_mark.comp reads them: the texels a pixel weighs must be the ones the mark queued for it.
    ivec2 pixel = g.uHalf.x > 0.5
        ? clamp(ivec2(gl_FragCoord.xy) * 2 + ivec2(g.uHalf.yz), ivec2(0), textureSize(uSceneDepth, 0) - 1)
        : clamp(ivec2(vUv * vec2(textureSize(uSceneDepth, 0))), ivec2(0), textureSize(uSceneDepth, 0) - 1);
    float raw = texelFetch(uSceneDepth, pixel, 0).r;
    if (raw >= 1.0 - 1e-6) {
        outIncident = vec4(0.0, 0.0, 0.0, 1.0);
        outIncidentGradient = vec4(0.0);
        return;
    }
    vec2 pixelUv = (vec2(pixel) + 0.5) / vec2(textureSize(uSceneDepth, 0));
    vec4 view = g.uInvProjection * vec4(pixelUv * 2.0 - 1.0, raw, 1.0);
    vec3 worldPos = (g.uInvView * vec4(view.xyz / view.w, 1.0)).xyz;
    clipNormalBias = g.uClipExact.y;
    clipViewBias = g.uClipExact.z;
    clipViewDir = normalize((g.uInvView * vec4(0.0, 0.0, 0.0, 1.0)).xyz - worldPos);
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
    pixelLightmapTexel = g.uLightmap.x > 0.5 ? texelFetch(uLightmapTexel, pixel, 0).r : 0u;
    vec3 transferred;
    bool transferUsed = g.uTransfer.x > 0.5 && g.uClipParams.y < 0.5 && transferAt(c, N, transferred);
    if (transferUsed) outIncident.rgb = transferred;
    if (g.uTexelParams.y > 0.5 && g.uTransfer.x < 0.5 && g.uClipParams.y < 0.5 && texelsAt(worldPos, N, nSample.w, viewDepth, gathered, confidence)) {
        outIncident.rgb = mix(outIncident.rgb, gathered, confidence);
        texelFound = true;
    }
    if (g.uReference.w > 1.5) {
        // Which record each of the pixel's four lightmap texels gives it (texelsAroundLightmap's choice), by weight.
        vec2 at;
        vec3 shown = vec3(1.0, 0.0, 1.0);
        if (g.uLightmap.x > 0.5 && texelLightmapUnpack(pixelLightmapTexel, at)) {
            ivec2 atlasSize = ivec2(g.uLightmap.yz);
            vec2 f = at - 0.5;
            ivec2 base = ivec2(floor(f));
            vec2 t = f - vec2(base);
            shown = vec3(0.0);
            for (int i = 0; i < 4; ++i) {
                ivec2 o = ivec2(i & 1, i >> 1);
                ivec2 q = base + o;
                float w = (o.x == 1 ? t.x : 1.0 - t.x) * (o.y == 1 ? t.y : 1.0 - t.y);
                if (any(lessThan(q, ivec2(0))) || any(greaterThanEqual(q, atlasSize))) continue;
                uint id = TEXEL_INDEX_MAP(q.y * atlasSize.x + q.x);
                if (id == 0xFFFFFFFFu) continue;
                bool pair = TEXEL_POSITION(id).w < 0.0;
                bool back = pair && dot(TEXEL_NORMAL(id).xyz, N) < 0.0;
                if (back) id += 1u;
                vec3 kind = abs(TEXEL_POSITION(id).w) < 2.0 ? vec3(1.0, 0.0, 0.0)
                    : !pair ? vec3(1.0) : back ? vec3(0.0, 0.3, 1.0) : vec3(0.0, 1.0, 0.0);
                shown += w * kind;
            }
        }
        outIncident = vec4(shown, 1.0);
        outIncidentGradient = vec4(0.0);
        return;
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
    // w: 1 where the transfer answered (lit.frag reads only xyz) -- the shot counts them (WriteGpuSurfaceReference).
    outIncidentGradient.w = transferUsed ? 1.0 : 0.0;
}
