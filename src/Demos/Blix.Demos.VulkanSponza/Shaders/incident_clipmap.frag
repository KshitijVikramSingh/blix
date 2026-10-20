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
    vec4 uTexelDebug;    // x the texel frame (the debug view's stamp test); y 1: the texels at rest (TexelResolved); z 1:
                         // the cells' detail answers (TexelDetail: P1-P3, the clipmap skipped where the cells are whole)
    vec4 uLightmap;      // --lightmap: x 1 when the atlas's texels are on (texel.glsl), yz the atlas size, w its first record
    vec4 uTexelLevels;   // x levels, y a pixel's width at unit depth, z / w the pixels a texel should span in sun / in shade
    vec4 uTexelSun;      // xyz the direction the sun travels (texelLevelFor's sunlit)
    mat4 uTexelCascadeVP[3];
    vec4 uReference;     // the reference view (reference_trace.comp): x 1 when shown, yz its pixels (half the frame's);
                         // w 1: the texel debug view (--texel-debug: r the texels' confidence, g 1 where any was found,
                         // b 1 where the lookup found none); w 2: the records view (--texel-debug-records)
    vec4 uClipExact;     // x 1: whether a point sees each probe, TRACED (--clipmap-exact-visibility): the ceiling a
                         // cooked per-texel mask could reach, in place of the Chebyshev estimate; y 1: count this
                         // frame's pixels by the path they take (IncidentCensus, SponzaLoop.IncidentCensus.cs); zw unused
    vec4 uHalf;          // x 1: this target is half the frame (RenderSettings.HalfResLighting), each pixel standing for
                         // the full-res pixel 2p + yz of its 2x2 (a different one each frame; lighting_upsample.frag)
    vec4 uReuse;         // x 1: reuse last frame's answer (SponzaLoop.Clipmap.cs, IncidentReuseNow); y the pixel of each
                         // 2x2 recomputed this frame (x + 2y), every other one reprojected where its surface matches
} g;

// Last frame's answer, for reuse: the pixel's motion, its surface key now and a frame ago, the incident light and
// gradient as the lit pass read them (copy_incident.frag).
layout(set = 0, binding = 39) uniform sampler2D uReuseVelocity;
layout(set = 0, binding = 40) uniform usampler2D uReuseKey;
layout(set = 0, binding = 41) uniform usampler2D uReuseKeyPrev;
layout(set = 0, binding = 42) uniform sampler2D uIncidentPrev;
layout(set = 0, binding = 43) uniform sampler2D uIncidentGradientPrev;

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
// At rest (uTexelDebug.y): each texel's answer resolved once (texel_resolve.comp) -- RGB9E5, then a 16-bit confidence and a
// valid bit -- and the buffers above are placeholders.
layout(std430, set = 0, binding = 34) readonly buffer TexelResolved { uvec2 texelResolved[]; };
// At rest with uTexelDebug.z: each texel's detail (texel.glsl, texelDetailPack; texel_compose.comp): its sky visibility and
// its light's change as the normal tilts. Summed over the texels read (detailSum: xy the tilt, z the sky visibility, w the
// weight), and detailMissing where a texel with light had none.
layout(std430, set = 0, binding = 35) readonly buffer TexelDetail { uint texelDetail[]; };
vec4 detailSum;
bool detailMissing;
// The census (uClipExact.y): pixels by path -- 0 sky, 1 the cells alone, then why the probes were read: 2 no texel found,
// 3 texels not at rest, 4 no detail up, 5 a texel read had no detail, 6 not whole (confidence), 7 a debug view or screen
// probes; 8 the two levels read (the coarser's weight above zero), 9 lightmapped past the atlas, 11 lightmapped -- among
// the cells-alone pixels.
layout(std430, set = 0, binding = 36) buffer IncidentCensus { uint census[]; };
void countPath(uint path) { if (g.uClipExact.y > 0.5) atomicAdd(census[path], 1u); }
vec3 unpackRgb9e5(uint v) {
    float scale = exp2(float(int(v >> 27) - 15 - 9));
    return vec3(float(v & 511u), float((v >> 9) & 511u), float((v >> 18) & 511u)) * scale;
}
float debugStamped, debugLit;
// --lightmap: the pre-pass's packed atlas texel and the atlas's map from texel to record (texel.glsl).
layout(set = 0, binding = 19) uniform usampler2D uLightmapTexel;
layout(std430, set = 0, binding = 20) readonly buffer LightmapIndexMap { uint lightmapIndexMap[]; };
#define TEXEL_INDEX_MAP(i) lightmapIndexMap[i]
// The index map as a texture and the records' meta words (texelsAroundLightmapGather, uClipExact.w).
layout(set = 0, binding = 44) uniform usampler2D uLightmapIndexTex;
layout(std430, set = 0, binding = 45) readonly buffer TexelMeta { uint texelMeta[]; };
#define TEXEL_ATLAS_GATHER
#define TEXEL_INDEX_TEX uLightmapIndexTex
#define TEXEL_META(id) texelMeta[(id) - uint(g.uLightmap.w)]
// This pixel's packed atlas texel (main sets it): not 0, the atlas's texels are its level 0 and the hashed cells its
// coarser levels; 0 (no lightmap, or a surface the atlas leaves out), the hashed cells at every level.
uint pixelLightmapTexel;
ivec2 pixelAt;   // the pixel whose lightmap texel pixelLightmapTexel is (uLightmapTexel's)
#define TEXEL_HASH(i) texelHash[i]
#define TEXEL_POSITION(i) texels[2 * (i)]
#define TEXEL_NORMAL(i) texels[2 * (i) + 1]
#include "texel.glsl"

// The pixel's own direct sun, 0-1, for its texel level (texelLevelFor): facing away, or shadowed in the cascades, is
// shade; past the cascades, sun (far, where coarse texels are all a pixel can hold anyway). Not the traced mask: its
// per-pixel answer flips at edges and disocclusions, and each flip switched the texel level -- tears along the walls as
// the cloth swung past, white flashes at edges (2026-10-20, even from last frame's complete mask).
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
        if (!(g.uClipExact.w > 0.5 && texelsAroundLightmapGather(atlasTexel, n, ivec2(g.uLightmap.yz), lm, lw)))
            texelsAroundLightmap(atlasTexel, n, ivec2(g.uLightmap.yz), lm, lw);
        // None of the four answers: a layer under another, nearly coplanar (Sponza's door ornaments are stacked so), wins
        // the depth test along thin lines, and its texels are rightly buried -- the pixel read nothing and took the
        // probes' light, bright dashes along the ornaments (2026-10-15). The neighbours' lightmap texels instead, out to
        // two pixels: mostly the layer on top, and texelsAroundLightmap keeps only the ones facing as this pixel does.
        if (lm[0] < 0 && lm[1] < 0 && lm[2] < 0 && lm[3] < 0) {
            ivec2 size = textureSize(uLightmapTexel, 0);
            for (int k = 0; k < 16 && lm[0] < 0 && lm[1] < 0 && lm[2] < 0 && lm[3] < 0; ++k) {
                int r = k < 8 ? 1 : 2;
                ivec2 o = ivec2(k % 8 < 3 ? -1 : k % 8 == 5 || k % 8 == 7 ? 0 : 1, k % 8 == 0 || k % 8 == 3 || k % 8 == 5 ? -1 : k % 8 == 2 || k % 8 == 4 || k % 8 == 7 ? 1 : 0) * r;
                ivec2 q = clamp(pixelAt + o, ivec2(0), size - 1);
                vec2 other;
                if (texelLightmapUnpack(texelFetch(uLightmapTexel, q, 0).r, other))
                    texelsAroundLightmap(other, n, ivec2(g.uLightmap.yz), lm, lw);
            }
        }
        for (int i = 0; i < 4; ++i) { ids[i] = lm[i]; weights[i] = lw[i]; }
    } else {
        texelsAround(tg, worldPos, n, faceBin, ids, weights);
        // The face's own bins found nothing: a thin stem turning through bins its cell holds no record in (few records a
        // cell on a stem) -- the pixel took the probes' light, white specks along the ivy's and the tree's branches
        // (2026-10-15). The five bins about the normal instead, as without the face's bins.
        if (faceBin >= 1.0) {
            bool none = true;
            for (int i = 0; i < TEXEL_CANDIDATES; ++i) none = none && ids[i] < 0;
            if (none) texelsAround(tg, worldPos, n, 0.0, ids, weights);
        }
    }
    vec3 sum = vec3(0.0);
    float weight = 0.0, raysWeighted = 0.0, located = 0.0;
    float count = 0.0;
    debugStamped = 0.0; debugLit = 0.0;
    bool resting = g.uTexelDebug.y > 0.5;
    for (int i = 0; i < TEXEL_CANDIDATES; ++i) {
        if (ids[i] < 0) continue;
        located += weights[i];
        count += 1.0;
        if (resting) {
            uvec2 r = texelResolved[ids[i]];
            if ((r.y >> 16) == 0u) continue;
            debugLit += 1.0;
            sum += weights[i] * unpackRgb9e5(r.x);
            weight += weights[i];
            if (g.uTexelDebug.z > 0.5) {
                uint d = texelDetail[ids[i]];
                if ((d >> 24) == 0u) detailMissing = true;
                else detailSum += weights[i] * vec4(texelDetailUnpack(d), 1.0);
            }
            raysWeighted += weights[i] * float(r.y & 0xFFFFu) / 65535.0;
            continue;
        }
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
// The coarser level blends in over the last TEXEL_LEVEL_BAND of each level's band only, not across all of it: across
// all of it 99.9% of pixels read two levels, and the second -- hashed cells, up to 8 corners x bins -- was ~26 ms of
// Sponza's frame (a Metal System Trace with the finest level alone, 2026-10-15). The finest is read first, and the coarse
// only where its weight is above zero or the finest is not whole.
#define TEXEL_LEVEL_BAND 0.25
bool texelsAt(vec3 worldPos, vec3 n, float faceBin, float viewDepth, out vec3 irradiance, out float confidence) {
    float levelWanted = texelLevelFor(viewDepth, g.uTexelLevels, g.uTexelGrid.w, texelSunlit(worldPos, n));
    int finest = int(floor(levelWanted));
    float fraction = clamp((levelWanted - float(finest) - (1.0 - TEXEL_LEVEL_BAND)) / TEXEL_LEVEL_BAND, 0.0, 1.0);
    // uClipExact.z: a pixel the atlas leaves out (foliage, small props: the hashed cells, up to 8 corners x bins a
    // level) reads one level, the next coarser, alone -- its indirect light a touch broader, at ~a quarter of the pass
    // (the leaves were ~4.7 of the incident pass's ~17 ms at the tree, 2026-10-20). The sun and shadows are not this.
    if (g.uClipExact.z > 0.5 && pixelLightmapTexel == 0u) {
        finest = min(finest + 1, int(g.uTexelLevels.x) - 1);
        fraction = 0.0;
    }
    int coarsest = min(finest + 1, int(g.uTexelLevels.x) - 1);
    irradiance = vec3(0.0);
    confidence = 0.0;
    vec3 fineValue;
    float fineC;
    bool fine = texelsAtLevel(worldPos, n, faceBin, uint(finest), fineValue, fineC);
    float stampedSum = debugStamped, litSum = debugLit;
    bool needCoarse = finest < coarsest && (!fine || fineC <= 0.999 || fraction > 0.0)
        && !(g.uClipExact.z > 0.5 && pixelLightmapTexel == 0u && fine);
    vec3 coarseValue;
    float coarseC;
    bool coarse = needCoarse && texelsAtLevel(worldPos, n, faceBin, uint(coarsest), coarseValue, coarseC);
    debugStamped = stampedSum; debugLit = litSum;
    // As before: the coarse level's answer first, the finer taking over by its confidence (its share of the blend with
    // the next, at the pixel's own level).
    if (!fine && !coarse) {
        // Neither level answers: the coarser levels up to the last, the first that does taking the pixel whole. Left to
        // the probes these pixels found none (the clipmap had no answer there either) and took the open-sky cube, full
        // sky in the shade: ~700 pixels a frame along the ivy's and the tree's stems, white specks and orange branches
        // (the census, 2026-10-15).
        for (int level = max(finest, coarsest) + 1; level < int(g.uTexelLevels.x); ++level) {
            vec3 value;
            float c;
            if (!texelsAtLevel(worldPos, n, faceBin, uint(level), value, c)) continue;
            irradiance = value;
            confidence = 1.0;
            debugStamped = stampedSum; debugLit = litSum;
            return true;
        }
        return false;
    }
    if (!coarse) { irradiance = fineValue; confidence = fineC; return true; }
    if (!fine) { irradiance = coarseValue; confidence = coarseC; return true; }
    float take = fineC * (1.0 - fraction);
    irradiance = mix(coarseValue, fineValue, take);
    confidence = coarseC + (1.0 - coarseC) * fineC;
    return true;
}

void main() {
    clipLiftKnob = g.uClipParams.z;
    clipVisibilityKnob = g.uClipParams.w;
    clipExactKnob = g.uClipExact.x;
    clipExactSeed = blix_pcg(uint(gl_FragCoord.x) * 7919u + uint(gl_FragCoord.y) * 104729u);
    // Exact fetches, as texel_mark.comp reads them: the texels a pixel weighs must be the ones the mark queued for it.
    ivec2 pixel = g.uHalf.x > 0.5
        ? clamp(ivec2(gl_FragCoord.xy) * 2 + ivec2(g.uHalf.yz), ivec2(0), textureSize(uSceneDepth, 0) - 1)
        : clamp(ivec2(vUv * vec2(textureSize(uSceneDepth, 0))), ivec2(0), textureSize(uSceneDepth, 0) - 1);
    float raw = texelFetch(uSceneDepth, pixel, 0).r;
    if (raw >= 1.0 - 1e-6) {
        countPath(0u);
        outIncident = vec4(0.0, 0.0, 0.0, 1.0);
        outIncidentGradient = vec4(0.0);
        return;
    }
    vec2 pixelUv = (vec2(pixel) + 0.5) / vec2(textureSize(uSceneDepth, 0));
    // Reuse (at rest: the texels, the probes and the light still): three of each 2x2 take last frame's answer where the
    // same surface showed there, reprojected by the pixel's motion; the fourth recomputes, a different one each frame,
    // so a pixel's answer is never more than four frames old. The texel lookups are the frame's largest pass (~10-20 ms
    // at 2880x1620, 2026-10-20); the light they read is low-frequency by construction (a texel spans 6-24 px).
    if (g.uReuse.x > 0.5 && uint(pixel.x & 1) + 2u * uint(pixel.y & 1) != uint(g.uReuse.y)) {
        ivec2 size = textureSize(uSceneDepth, 0);
        vec2 prevUv = pixelUv - texelFetch(uReuseVelocity, pixel, 0).xy;
        ivec2 pp = ivec2(floor(prevUv * vec2(size)));
        if (all(greaterThanEqual(pp, ivec2(0))) && all(lessThan(pp, size))) {
            uint key = texelFetch(uReuseKey, pixel, 0).r;
            if (key != 0u && key == texelFetch(uReuseKeyPrev, pp, 0).r) {
                outIncident = texelFetch(uIncidentPrev, pp, 0);
                outIncidentGradient = texelFetch(uIncidentGradientPrev, pp, 0);
                return;
            }
        }
    }
    vec4 view = g.uInvProjection * vec4(pixelUv * 2.0 - 1.0, raw, 1.0);
    vec3 worldPos = (g.uInvView * vec4(view.xyz / view.w, 1.0)).xyz;
    float viewDepth = abs(view.z / view.w);
    vec4 nSample = texelFetch(uPrepassNormal, pixel, 0);
    vec3 N = dot(nSample.xyz, nSample.xyz) > 1e-6
        ? normalize(nSample.xyz)
        : normalize((g.uInvView * vec4(0.0, 0.0, 1.0, 0.0)).xyz);

    // The cells first (P1): at rest, where their light is whole and every texel read carries its detail, they answer
    // alone -- the light, the sky visibility (P2), the normal map's direction (P3) -- and the clipmap and the surface
    // probes are not read. Elsewhere (moving things have no texels, a cell not landed, a cook from before the detail) the
    // probes as before, the texels blended over them.
    vec3 gathered;
    float confidence;
    detailSum = vec4(0.0);
    detailMissing = false;
    pixelLightmapTexel = g.uLightmap.x > 0.5 ? texelFetch(uLightmapTexel, pixel, 0).r : 0u;
    pixelAt = pixel;
    bool texelFound = g.uTexelParams.y > 0.5 && g.uClipParams.y < 0.5 && texelsAt(worldPos, N, nSample.w, viewDepth, gathered, confidence);
    vec3 t1 = normalize(cross(abs(N.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), N));
    vec3 t2 = cross(N, t1);
    if (texelFound && g.uTexelDebug.y > 0.5 && g.uTexelDebug.z > 0.5 && !detailMissing && detailSum.w > 1e-6
        && confidence > 0.999 && g.uReference.w < 0.5 && g.uScreen.x < 0.5 && g.uReference.x < 0.5) {
        countPath(1u);
        if (g.uClipExact.y > 0.5) {
            float lw = texelLevelFor(viewDepth, g.uTexelLevels, g.uTexelGrid.w, texelSunlit(worldPos, N));
            if (int(floor(lw)) < min(int(floor(lw)) + 1, int(g.uTexelLevels.x) - 1) && fract(lw) > 1.0 - TEXEL_LEVEL_BAND) countPath(8u);
            if (pixelLightmapTexel != 0u && lw >= 1.0) countPath(9u);         // a lightmapped pixel past the atlas
            if (pixelLightmapTexel != 0u) countPath(11u);
        }
        vec3 d = detailSum.xyz / detailSum.w;
        outIncident = vec4(gathered, d.z);
        float lum = dot(gathered, vec3(0.2126, 0.7152, 0.0722));
        outIncidentGradient = g.uScreen.w < 0.5 ? vec4((t1 * d.x + t2 * d.y) * lum, 0.0) : vec4(0.0);
        return;
    }

    countPath(!texelFound ? 2u : g.uTexelDebug.y < 0.5 ? 3u : g.uTexelDebug.z < 0.5 ? 4u : detailMissing || detailSum.w <= 1e-6 ? 5u
        : confidence <= 0.999 ? 6u : 7u);
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
    if (texelFound) countPath(14u);
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
    // The census: what answered a pixel the texels did not answer alone -- 12 the open-sky cube (no probe at all), 13 the
    // surface probes (14, counted above: texels found, but mixed with these).
    if (!found && surfaceWeight <= 0.0) countPath(12u);
    if (surfaceWeight > 0.0) countPath(13u);
    if (texelFound) outIncident.rgb = mix(outIncident.rgb, gathered, confidence);
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
}
