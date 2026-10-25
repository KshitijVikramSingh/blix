// The body of texel_nodes_shade.comp and texel_nodes_shade_split.comp: what follows is theirs, word for word.
// Sun nodes (SponzaLoop.SunNodes.cs): one pass of a slice's gather, shaded for EVERY sun at once. Where a ray goes and
// what it meets do not depend on the sun; only the light at the hit does -- its own direct sun and the patches' light
// for that sun. So the rays are traced once (texel_nodes_rays.comp, the CLOSEST batch) and each hit is shaded here for
// all the suns, as texel_gather.comp shades it for one:
//   the hit's sun    albedo x the sun's irradiance x n.l x its visibility: the sun bits (a shadow ray from each texel
//                    toward each sun, SunNodeBitsFor) of the texels the hit lies among (the cook's hash), weighed as
//                    a pixel weighs its texels -- or, among none, of the foliage's sun points (SunNodesFoliage.cs)
//   its bounce       albedo x its nearest patch's light for that sun (the patches re-solved per sun, SunNodePatchLight)
//   a miss           the sky, the same for every sun
// A workgroup a texel: its rays' hits made ready once (threads 0..31: the texels, their bits, the patch), then a thread
// a sun sums them. A ray's weight on irradiance (NodeRayWeights: the rays pass's mixture of cosine and the band's guide;
// pi for a plain cosine ray): a hit contributes albedo x E x weight / pi, a miss the sky x weight. NodeAccum holds the
// passes' running sum of each pass's mean; column 0's w the running sum of the square of each pass's luminance over
// all the columns (the light composed: the split's sky and sun), the moment its refinement reads its noise from
// (SponzaLoop.SunNodes.cs, the refining rounds).

#ifndef TEXEL_NODES_SHADE_GLSL
#define TEXEL_NODES_SHADE_GLSL
#define RAYS 32
#define PI 3.14159265359
// The includer defines MAX_WORDS (sun-bit words a candidate: 8 for up to 256 suns) and SHADE_THREADS (the workgroup).
layout(local_size_x = SHADE_THREADS) in;

layout(set = 0, binding = 0) uniform SunNodesShade {
    vec4 uShade;          // x the slice's texels, y suns, z the cook hash's slots, w the pass (0: the sums start)
    vec4 uTexelGrid;      // xyz the cells' origin, w the finest spacing
    vec4 uCookedGrid;
    vec4 uCookedDims;
    vec4 uSunIrradiance;  // rgb; w the visibility a hit among no texel takes (--sun-nodes-unknown-sun; 0 by default)
    vec4 uDetail;         // x the slice's first entry in NodeIds; y 1: write NodeDetail (the cells' sky visibility and
                          // direction, SponzaLoop.SplitDetail.cs); z 1: write NodeCensus (--split-noise-census)
    vec4 uSky;            // x strength, y mip, z the sun's column + 1 when the sky's part is kept (a sun change: the moment the sun's alone)
    vec4 uLookup;         // x a sun bit candidate's plane distance at most (cells), y the distance weight's spread (cells); z the hit cells
                          // answer only past this distance (nearer, the search's blend: a contact's light); w 1: the hit cells answer (hit_cells.comp)
    vec4 uFoliage;        // x the foliage hash's slots (0: no foliage points); y discovering: the list's room (0: off);
                          // z the exact-sun experiment (--sun-nodes-exact-sun, one sun): 1 write each hit's shadow
                          // ray toward sun 0 (ExactOrigins) and stop; 2 shade sun 0 by that ray's answer (ExactOccluded);
                          // 3 every hit's sun by the Metal trace's own shadow ray from it (hit flag 8, --hit-sun-ray), the
                          // cells and the search answering the patch alone; w the column that sun is + 1 (0: column 0)
    vec4 uFar;            // the far field (texel_far.glsl): x 0 off, 1 the record pass, 2 the sweep reads the record; y the
                          // patch spacing; z a known bin's weight (fixed point); w the columns
    vec4 uTransport;      // the transport census (N2, SponzaLoop.TransportCensus.cs): x 1 on; y the patch spacing; z the
                          // patches; w 1: the near-field comparison (N3: PatchDirectSums read, census slot 9)
    vec4 uFarTransport;   // the far field's light-free record (SponzaLoop.FarTransport.cs): x 1 recording it; y 1 (--texel-h4):
                          // the light-agnostic record into TexelH4 (texel_record_h4.glsl), z 1 the records' smooth normals
    vec4 uHitBlend;       // x 1: a hit's bounce blended over its patch and the patch's neighbours (hitPatchLight); y the
                          // patches' spacing (the blend's reach); z 1: a near hit's bounce the texels' about it
                          // (--near-texel-light), w the sun's column (its part per unit of uSunIrradiance)
    vec4 uCapture;        // the ray capture (--ray-capture, SponzaLoop.RayCapture.cs): x 1 on; y the texels sampled, 1 in
                          // 2^y by a hash of the id (negative: the one texel -y - 1); z the round's tag; w the records' room
    vec4 uNear;           // --near-cut (SponzaLoop.SunNodes.cs): x 1 the fine round, each texel's far part kept (FarPart); 2
                          // a near round, its rays cut at y and the far part added back; y the reach (the near texels'
                          // light's, --cell-near); z 1: a near hit among no texel the pass's near hits' light
                          // (--near-unknown-local, sNearKnown)
    vec4 uSunW;           // the sun-linear record (SponzaLoop.SunLinear.cs): x 1 on -- each texel's lit hits' sun over its
                          // cosine times the hit's normal (luminance, per unit of irradiance), into TexelSunW by id: the sun
                          // part its lit hits bring at any sun s is W.s (tools/gi-model/sun_flip_push.py: linear to 0.00%)
} u;

layout(set = 0, binding = 1) uniform samplerCube uSkyRadiance;
layout(std430, set = 0, binding = 2) readonly buffer Texels { uvec4 texels[]; };
#include "texel_record.glsl"
layout(std430, set = 0, binding = 3) readonly buffer NodeRays { vec4 nodeRays[]; };
layout(std430, set = 0, binding = 4) readonly buffer NodeHits { uvec4 nodeHits[]; };    // 3 a ray: the CLOSEST answer
layout(std430, set = 0, binding = 5) readonly buffer SunNodes { vec4 sunNodes[]; };
layout(std430, set = 0, binding = 6) readonly buffer SunNodeBits { uint sunBits[]; };
layout(std430, set = 0, binding = 7) readonly buffer SunNodeHash { uint cookHash[]; };
layout(std430, set = 0, binding = 8) readonly buffer SunNodePatchLight { uvec2 patchLight[]; };   // [patch][sun]: half rgb
layout(std430, set = 0, binding = 9) buffer NodeAccum { vec4 nodeAccum[]; };                       // [slice texel][sun]
// Counts in pairs of words (low, high: a cook's 8 G rays overflow 32 bits): [0] hits, [1] hits among no texel nor
// foliage point (no sun there), [2] hits with no patch, [3] misses, [4] hits answered by the foliage's points.
layout(std430, set = 0, binding = 10) buffer NodeStats { uint nodeStats[]; };
// The texels' light as the last resolve left it (slice_resolve.comp: rgb9e5, word 1 bit 16 once gathered; the sun's
// part per unit of the sun's irradiance): a near hit's bounce (uHitBlend.z).
layout(std430, set = 0, binding = 35) readonly buffer SplitSky { uvec2 skyPart[]; };
layout(std430, set = 0, binding = 36) readonly buffer SplitSun { uvec2 sunPart[]; };
// Which stage last wrote each texel's parts (provenance.glsl): only a gather's (codes 1-4) is a texel's own light -- the
// instant layer and the fine copy carry the coarse estimate a near hit's light is to replace.
layout(std430, set = 0, binding = 37) readonly buffer TexelProvenance { uint provenance[]; };
// Per triangle row, the sheet it is (KHR_materials_diffuse_transmission as the spec has it, --sheet-light; 0 none): rgb8
// T c, the top byte T (SponzaLoop.GpuReference.cs, SheetRowsBuffer). A hit on one reflects 1 - T of the sun on its side
// with its albedo, and passes T c of the sun on its far side: the hit's sun ray from that side (hit flag 8, the trace's
// sheets, blix_hwx.mm). uFoliage.w's sign: negative with sheets (the exact column then -w - 1).
layout(std430, set = 0, binding = 38) readonly buffer SheetRows { uint sheetRows[]; };
// The ray capture (uCapture.x on; the split variant's two-column fast path alone): each ray of the sampled texels, two
// uvec4s -- x the texel, y the round's tag | the pass << 16, z its hit distance (a miss 1e30), w how it was answered (0
// the sky, 1 the far record, 2 a hit cell, 3 the search's texels, 4 among none; + 8 its bounce a near texel's own light,
// + 16 no patch; bits 8-12 its place in its pass, which says its strategy: texel_nodes_rays.comp; bits 13-31 its hit's
// patch + 1, 0 none -- a miss or a far ray wraps, read only for hits); then its whole
// luminance (weight in), the bounce's share of it, half2 (its cosine to the texel's normal, its weight), the hit's
// likeliest texel (else 0xFFFFFFFF). tools/gi-model/ray_capture.py reads them.
layout(std430, set = 0, binding = 39) buffer RayCapture { uint captureCount; uint capturePad[3]; uvec4 captured[]; };
// --near-cut: a near round re-gathers the fine texels for their near hits' new light (the texels the last round
// gathered, nearTexelLight) -- nothing past the reach changes between rounds (hit cells, patches, the sky: the ray
// capture found no hit past it lit by a texel, 2026-10-22). So the fine round keeps, per texel, the part of its
// estimate its rays past the reach and its misses brought (FarPart [place][column]: rgb the passes' means summed, w the
// passes); a near round traces short rays (texel_nodes_rays.comp) for the near part alone and adds it back. FinePlace:
// a texel's place in the fine round + 1 (0 none). The split variant's fast path alone.
layout(std430, set = 0, binding = 40) readonly buffer FinePlace { uint finePlace[]; };
layout(std430, set = 0, binding = 41) buffer FarPart { vec4 farPart[]; };
// --near-glow-split: per slice texel, the passes' means of the sun's glow its rays met on sheets within the frame's
// pass-by (BlixSheetMinDistance, 5 cm: blix_hwx.mm) -- a thin wall's other face, whose sun the frame draws per pixel
// through the wall (lit.frag, sunTransmission). The GI keeps it whole; the lit pass leaves it out of a sheet pixel's own
// far side (slice_resolve.comp writes it per texel, GlowPart), so the wall's sun is the frame's sharp one, once.
layout(std430, set = 0, binding = 42) buffer NodeGlow { vec4 nodeGlow[]; };
layout(std430, set = 0, binding = 14) readonly buffer FoliagePoints { vec4 foliagePoints[]; };
layout(std430, set = 0, binding = 15) readonly buffer FoliageHash { uint foliageHash[]; };
layout(std430, set = 0, binding = 16) readonly buffer FoliageBits { uint foliageBits[]; };
// Discovering (SponzaLoop.SunNodes.cs, the Discover stage): each hit among neither texel nor sun point, appended (two
// vec4s: position, w 4; the normal it was met from) -- the CPU makes sun points of them before the trace proper.
layout(std430, set = 0, binding = 17) buffer NodeDiscover { uint discoverCount; uint discoverPad[3]; vec4 discovered[]; };
layout(std430, set = 0, binding = 18) buffer ExactOrigins { vec4 exactOrigins[]; };
layout(std430, set = 0, binding = 19) readonly buffer ExactOccluded { uint exactOccluded[]; };
layout(std430, set = 0, binding = 20) readonly buffer NodeRayWeights { float nodeRayWeight[]; };
// Each live texel's nearest patch it can see (texel_nodes_patch.comp; -1 none): a hit among texels reads its bounce from
// its likeliest texel's -- the nearest by distance alone was the one across a wall at a wall's foot.
layout(std430, set = 0, binding = 21) readonly buffer TexelPatch { int texelPatch[]; };
// The cells' detail (plan.md, "Probes after the cells", P2/P3), [slice texel][sun] as NodeAccum: xy the column's light's
// change as the normal tilts toward the texel's tangents t1, t2 (texelDetailFrame), luminance-weighted -- each pass's mean
// summed; z (column 0) the share of rays that escape, cosine-weighted (the sky visibility), each pass's mean summed.
layout(std430, set = 0, binding = 22) buffer NodeDetail { vec4 nodeDetail[]; };
layout(std430, set = 0, binding = 23) readonly buffer NodeIds { uint nodeIds[]; };
// --split-noise-census (SponzaLoop.NoiseCensus.cs): per slice texel six vec4s, each a sum over passes then the sum of its
// squares -- each pass's composed luminance by its source (x the sky's misses, y the bounce from a patch, z the sun direct
// at the hits, w the bounce a hit with no patch took): which source the texel's noise comes from; by the ray's length, the
// light from beyond R (x 0.5 m, y 1 m, z 2 m; a miss is beyond all; w the sun at hits beyond 1 m) and from within it (xyz
// the same R; w the sun at hits within 1 m): how much a texel's noise would fall if what lies beyond R came from its patch.
// With the far field (its reach R and the texel's patch): the texel's view split by what fills it (the near field's
// storage: what the mesh alone could cook, and what only the scene knows) -- [6] the cosine-weighted share of its rays
// that hit within R on its own placement (its patch's: the primitive), on another placement, past R, and escaped;
// [7] the distinct patches a pass's near hits landed on, own placement and other, the passes with any other-placement
// near hit, and the reach; [8] the primitives merged into connected objects (CensusObjects: placements sharing a
// welded vertex) -- x the share of the view near on another primitive of the SAME object (an authoring fragment), y
// near on another object, z the passes with any other-object near hit.
layout(std430, set = 0, binding = 24) buffer NodeCensus { vec4 nodeCensus[]; };
layout(std430, set = 0, binding = 30) readonly buffer CensusObjects { uint censusObject[]; };   // [placement]: its object
// The transport census (plan.md N2): each ray of a slice texel's sweep as a light-free relationship -- [slice texel]
// [pass x 32 + ray]: x its kind (bits 30-31: 0 the sky, 1 near on its own object, 2 near on another object, 3 past the
// reach) and what (the sky's octahedral bin of 64; the near hit's texel, or its patch | 1 << 29 among no texel; the far
// hit's patch), y the near hit's patch (else 0xFFFFFFFF). texel_transport_census.comp counts them.
layout(std430, set = 0, binding = 31) buffer NodeTransport { uvec2 nodeTransport[]; };
// The near-field comparison (texel_patch_direct.comp): each patch's n.l x visibility summed over its texels per column
// (x 65536), then each patch's texel count -- a near hit's direct sun if near transport were stored at patch level.
layout(std430, set = 0, binding = 32) readonly buffer PatchDirectSums { uint patchDirect[]; };
// The far field's LIGHT-FREE record (plan.md N3; SponzaLoop.FarTransport.cs): per patch per bin, what its texels' far
// rays met, not the light they brought -- FAR_SLOTS (patch + 1, weight) pairs, then the weight of the hits no slot held,
// the weight that escaped (the sky), and the whole weight (fixed point). texel_far_relight.comp makes the radiance record
// of it for whatever lights there are.
layout(std430, set = 0, binding = 33) buffer FarTransport { uint farTransport[]; };
// The hit cells (hit_cells.comp, SponzaLoop.HitCells.cs; uLookup.w on): word 0 the rows, a word a row, two a cell.
layout(std430, set = 0, binding = 34) readonly buffer HitCells { uint hitCells[]; };
#define FAR_SLOTS 3
#define FAR_TRANSPORT_STRIDE (2 * FAR_SLOTS + 3)
// [9] with the comparison: x each pass's composed (patch-level near - per-texel near) direct, summed; y the per-texel
// near direct's luminance, summed; z the patch-level one's.
// The far field (texel_far.glsl): each texel's guide patch (texel_nodes_rays.comp: the patch + 2), the record (written by
// the record pass, read by the sweep), and per slice texel per bin its rays' weight and the share of it gone far (x all,
// y far), summed over the slice's passes -- texel_far_finish.comp turns them and the record into the far light.
layout(std430, set = 0, binding = 25) readonly buffer NodeGuidePatch { uint guidePatch[]; };
// [slice texel][blended patch][bin] (texel_far.glsl, FAR_SET): x weight, y far weight, z w^2, w far w^2 -- the texel's
// rays where every blended patch knows the direction, binned in each patch's own frame.
layout(std430, set = 0, binding = 27) buffer NodeFarBins { vec4 farBins[]; };
// [slice texel]: x the sum of 1 / its rays' hit distances (a ray cut at the reach: the reach; a miss: nothing), y its
// rays -- the harmonic mean distance, the pooling's footprint (texel_far_pool.comp).
layout(std430, set = 0, binding = 29) buffer NodeHarmonic { vec2 nodeHarmonic[]; };

#define COOKED_BINDING 11
#define COOKED_GRID u.uCookedGrid
#define COOKED_DIMS u.uCookedDims
#include "cooked_patches.glsl"
#define NODE_LOOKUP
#include "texel_nodes.glsl"
#define FAR_RECORD_BINDING 26
#define FAR_RECORD_ACCESS
#define FAR_NEIGHBOURS_BINDING 28
#include "texel_far.glsl"
// --texel-h4: the light-agnostic record (texel_record_h4.glsl), written by the split variant's fast path alone; declared in
// both variants, as the command binds by the 256-sun variant's names.
layout(std430, set = 0, binding = 43) readonly buffer LightmapFrames { uvec2 lightmapFrames[]; };
layout(std430, set = 0, binding = 44) buffer TexelH4 { vec4 texelH4[]; };
// Its sums a slice's (uFarTransport.y 1, by the texel's place in the slice) or every texel's (2, BLIX_TEXEL_H4_FULL), and
// the shape they make into TexelOwnShape (texel_shape.glsl), as the lean shade's.
layout(std430, set = 0, binding = 45) writeonly buffer TexelOwnShape { uint ownShape[]; };
layout(std430, set = 0, binding = 46) buffer TexelSunW { vec4 texelSunW[]; };   // by id: xyz the passes' W sums, w the passes
shared vec4 sRecord[4];
#include "h4_kernel.glsl"
#include "texel_record_h4.glsl"
#include "texel_shape.glsl"

void nodeCount(uint k) {
    if (atomicAdd(nodeStats[2u * k], 1u) == 0xFFFFFFFFu) atomicAdd(nodeStats[2u * k + 1u], 1u);
}

shared vec3 sDir[RAYS];      // each ray's direction (the cells' detail)
vec2 captureOct(vec3 d) {
    d /= abs(d.x) + abs(d.y) + abs(d.z);
    return d.z >= 0.0 ? d.xy : (1.0 - abs(d.yx)) * vec2(d.x >= 0.0 ? 1.0 : -1.0, d.y >= 0.0 ? 1.0 : -1.0);
}
shared float sDist[RAYS];    // each ray's hit distance (a miss: 1e30), for the census
shared float sMiss[RAYS];    // a miss: its weight / pi (the sky visibility's sample); a hit: 0
shared vec4 sHit[RAYS];      // albedo x weight / pi (a hit) or the sky x weight (a miss); w its patch (-1 none), or -2 a miss
shared vec3 sNormal[RAYS];
shared float sUnknown[RAYS];
shared float sExact[RAYS];   // the exact-sun experiment: sun 0's visibility at the hit (-1: by the bits)   // the hit lies among no texel: its sun is uSunIrradiance.w
shared float sWeight[RAYS * NODE_CANDIDATES];
shared uint sBits[RAYS * NODE_CANDIDATES * MAX_WORDS];
shared float sColumnLum[MAX_WORDS * 32];   // each column's pass mean's luminance
shared vec4 sColumnSource[MAX_WORDS * 32]; // the census: each column's pass mean's luminance by source
shared vec4 sColumnFar[MAX_WORDS * 32];    // ... from beyond R
shared vec4 sColumnNear[MAX_WORDS * 32];   // ... from within R
shared vec4 sColumnCompare[MAX_WORDS * 32]; // the near-field comparison: (A - B, B, A, 0) of the near hits' direct
// The far field: the texel's patch (-1: none, or the far field off), its reach, the patch's frame; each ray's bin (-1:
// below the patch's horizon), whether it is far (past the reach, in a bin the record knows -- or, recording, any bin),
// its weight.
shared int sFarPatch;
shared float sFarReach;
shared int sFarIds[FAR_SET];
shared float sFarWeights[FAR_SET];
shared vec3 sFarN[FAR_SET], sFarT[FAR_SET], sFarB[FAR_SET];
shared int sBin[RAYS * FAR_SET];   // each ray's bin in each blended patch's frame (recording: [0] its own patch's)
shared float sFar[RAYS];
shared float sJoint[RAYS];         // every blended patch knows the ray's direction
shared float sRayWeight[RAYS];
shared uint sPlacement[RAYS];   // the census: each hit's placement (0xFFFFFFFF a miss)
// The two-column fast path (live: the sky and the sun): each ray's contribution per column, on its own thread, then summed
// -- the column loop below ran on 2 of the workgroup's 64 threads, walking the 32 rays one after another (2026-10-19).
shared vec3 sRaySum[RAYS * 2];
shared vec2 sRayTilt[RAYS * 2];
shared vec4 sRayKnown[RAYS * 2];
shared vec3 sUnknownBounce[2];
shared uint sCell[RAYS];
shared int sCand[RAYS * NODE_CANDIDATES];   // --list-cook: the search's candidate texels (-1: none; the foliage's: none)        // --list-cook: the hit's cell and side (cell x 2 + 1 for the back; ~0u: not by a cell)
shared int sNearTexel[RAYS];    // the transport census: each hit's likeliest texel (-1: among none)
#if MAX_WORDS == 1
shared vec4 sSheet[RAYS];       // the hit's sheet: rgb T c, a T (0: not a sheet); the 256-sun variant meets sheets as opaque
shared uint sHow[RAYS];         // the ray capture: how the hit was answered (RayCapture's w, before its flags); the
                                // 256-sun variant (at Metal's 32 KB) captures nothing
shared float sRayFar[RAYS];     // --near-cut: the ray met nothing within the reach (FarPart)
shared vec3 sRayNearGlow[RAYS]; // --near-glow-split: the ray's sun glow from a sheet within the pass-by (NodeGlow)
// The near unknown (default; --no-near-unknown-local): a near hit among no texel (none within the search's reach: a crease's foot, its records moved
// off the contact or buried) took its nearest patch facing its way -- by distance alone, through the wall at the wall's
// foot: crevices-dark's corridor corner read the sunlit floor outside, 1.4% of a texel's rays and 14% of its light,
// the seam point +12-16% (2026-10-22, --ray-capture-texel). Such a hit lies a few cm from the texel's near hits that
// found texels; it takes their mean light this pass, per column (w: how many).
shared vec4 sNearKnown[2];
shared vec4 sH4[RAYS * 3];      // --texel-h4: per ray per channel, its world-space H4 terms
shared vec3 sSunW[RAYS];        // the sun-linear record: per ray, its lit hit's sun over its cosine times the hit's normal
#define HOW(r, v) sHow[r] = v
#else
#define HOW(r, v)
#endif
shared float sTransportReach;
shared uint sTransportObject;

// The record pass: a far ray's light for a column into its patch's bin (c its weight x radiance, w its weight).
void farRecordAdd(int patchIndex, int bin, int column, vec3 c, float w, uint slot) {
    uint base = farBase(patchIndex, bin, u.uFar);
    uint at = farRgbAt(base, column);
    float jitter = fract(float(slot) * 0.7548776662 + float(column) * 0.5698402910 + u.uShade.w * 0.6180339887);
    atomicAdd(farRecord[at], farFixed(c.r, jitter));
    atomicAdd(farRecord[at + 1u], farFixed(c.g, fract(jitter + 0.3333)));
    atomicAdd(farRecord[at + 2u], farFixed(c.b, fract(jitter + 0.6667)));
    float l = dot(c, vec3(0.2126, 0.7152, 0.0722));
    if (w > 0.0) atomicAdd(farRecord[farSquareAt(base, column, u.uFar)], farFixed(l * l / w, fract(jitter + 0.5)));
}

// A hit's bounce, column k: its patch's light -- or blended over its patch and the patch's neighbours by the far field's
// kernel (texel_far.glsl, farSet: continuous across the patches, zero at a spacing). Read flat from the one patch, the
// bounce at a hit was one value over each patch, and every receiver near a patch-lit surface took the patches' outlines:
// the Cornell box's walls blotched where the path trace is smooth (2026-10-21).
// The hit's position from its ray (its origin, direction and distance), not kept: the 256-sun variant's threadgroup memory
// is at Metal's 32 KB.
vec3 unpackPart(uint p) {
    float scale = exp2(float(int(p >> 27) - 15 - 9));
    return vec3(p & 511u, (p >> 9) & 511u, (p >> 18) & 511u) * scale;
}

// A near hit's bounce from the texels about it (uHitBlend.z): the patch's light is the irradiance at a patch's centre,
// 12 cm and more from a crease, where most of a receiver beside the crease's light lands (tools/gi-model/
// crease_bounce.py: 25 cm patches +2-6% at a texel 2 cm off a crease, the texels' own light within 25 cm +-0.3%, one
// lagged step of it within 2.3%). The texels' light is the last resolve's, so the gather iterates on it; a texel not
// gathered yet (word 1 bit 16) leaves the patch's. sNearTexel: the hit's likeliest texel, on the hit's side.
// A texel's gathered light for a column (its part per unit of the sun's irradiance, scaled), or false before its gather.
// anyStage: whatever wrote the part (a fine copy's coarse estimate too) -- a sheet's far side (nearTexelLight), which a
// near hit reads for what comes through, not to replace a coarse estimate with its own gather.
bool texelPartLight(uint id, int k, out vec3 l, bool anyStage) {
    bool sunColumn = k == int(u.uHitBlend.w);
    uvec2 w = sunColumn ? sunPart[id] : skyPart[id];
    if ((w.y & 0x10000u) == 0u) return false;
    uint code = (provenance[id / 4u] >> (8u * (id % 4u) + (sunColumn ? 0u : 4u))) & 0xFu;
    if (!anyStage && (code < 1u || code > 4u) && code != 10u) return false;   // a gather's, or the lists' (texel_lists.comp)
    l = unpackPart(w.x) * (sunColumn ? u.uSunIrradiance.rgb : vec3(1.0));
    return true;
}
bool texelPartLight(uint id, int k, out vec3 l) { return texelPartLight(id, k, l, false); }

bool nearTexelLight(int r, int k, out vec3 l) {
    if (u.uHitBlend.z < 0.5 || sNearTexel[r] < 0) return false;
    uint id = uint(sNearTexel[r]);
    if (!texelPartLight(id, k, l)) return false;
#if MAX_WORDS == 1
    // A hit on a sheet that lets light through (sSheet: T c, T): by the spec 1 - T of the light on the side the ray met
    // goes to the base lobe (the hit's albedo, in sHit) and T c of the light on its far side comes through -- the far side's
    // record, the pair's other (same position; SponzaLoop.Lightmap.cs writes the front then the back). Without it only the
    // sun's glow came through at texel level: a closed thin shell's walls (the teacup, T 1) passed none of the light inside
    // the cup, and its camera paths read 13x what the renderer did (tools/gi-model/view_reference.py, 2026-10-22).
    if (sSheet[r].a > 0.0 && TEXEL_POSITION(id).w < 0.0) {
        vec3 p = TEXEL_POSITION(id).xyz;
        uint twin = TEXEL_POSITION(id + 1u).w < 0.0 && distance(TEXEL_POSITION(id + 1u).xyz, p) < 1e-4 ? id + 1u
                  : id > 0u && TEXEL_POSITION(id - 1u).w < 0.0 && distance(TEXEL_POSITION(id - 1u).xyz, p) < 1e-4 ? id - 1u : id;
        // The base lobe keeps 1 - T of the near side's light whatever the far side holds; the far side's light through
        // it from whatever wrote the twin's part (a fine copy's too). Both were taken only with the twin's light a
        // gather's: a closed shell's outer faces are mostly fine copies, so a near round's hits inside it reflected
        // the whole of the near side and passed none of the far -- each wall an opaque mirror, the sheet shell's
        // inside 1.26-1.47x its closed form, rising round by round (tools/gi-model/shell_slab.py, 2026-10-24).
        vec3 far;
        vec3 through = vec3(0.0);
        if (twin != id && texelPartLight(twin, k, far, true)) {
            // sHit's rgb is the hit's albedo x weight / pi: the far side's T c over the albedo, so add = weight / pi x T c x E.
            vec3 albedo = max(sHit[r].rgb / max(sRayWeight[r] / PI, 1e-8), vec3(1e-3));
            through = far * sSheet[r].rgb / albedo;
        }
        l = l * (1.0 - sSheet[r].a) + through;
    }
#endif
    return true;
}

vec3 hitPatchLight(int r, int k, int suns) {
    vec3 near;
    if (nearTexelLight(r, k, near)) return near;
    int pw = int(sHit[r].w);
    if (u.uHitBlend.x > 0.5) {
        int ids[FAR_SET];
        float ws[FAR_SET];
        vec3 hitPos = nodeRays[2u * (gl_WorkGroupID.x * uint(RAYS) + uint(r))].xyz + sDir[r] * sDist[r];
        farSet(hitPos, sNormal[r], pw, vec4(0.0, u.uHitBlend.y, 0.0, 0.0), ids, ws);
        vec3 l = vec3(0.0);
        for (int i = 0; i < FAR_SET; ++i) {
            if (ids[i] < 0) continue;
            uvec2 p = patchLight[uint(ids[i]) * uint(suns) + uint(k)];
            l += ws[i] * vec3(unpackHalf2x16(p.x), unpackHalf2x16(p.y).x);
        }
        return l;
    }
    uvec2 p = patchLight[uint(pw) * uint(suns) + uint(k)];
    return vec3(unpackHalf2x16(p.x), unpackHalf2x16(p.y).x);
}

void main() {
    uint group = gl_WorkGroupID.x;
    if (group >= uint(u.uShade.x)) return;
    uint lid = gl_LocalInvocationID.x;
    int suns = int(u.uShade.y);
    uint words = uint(suns + 31) / 32u;
    // Never while discovering nor for the exact-sun experiment: those passes trace every ray to its end.
    bool farOn = u.uFar.x > 0.5 && u.uFoliage.y < 0.5 && (u.uFoliage.z < 0.5 || u.uFoliage.z > 2.5);
    // The column whose sun the hits' exact visibility is (uFoliage.z 2, 3).
    int exactColumn = abs(u.uFoliage.w) > 0.5 ? int(abs(u.uFoliage.w) + 0.5) - 1 : 0;
    bool sheetsOn = u.uFoliage.w < -0.5;
    bool farRecording = farOn && u.uFar.x < 1.5;
    if (lid == 0u) {
        int fp = -1;
        float reach = 1e30;
        if (farOn) {
            uint id = nodeIds[uint(u.uDetail.x) + group];
            fp = texelPatch[id];
            if (fp < 0) fp = int(guidePatch[id]) - 2;
            if (fp >= 0) {
                vec3 x = TEXEL_POSITION(id).xyz;
                int ids[FAR_SET];
                float ws[FAR_SET];
                farSet(x, normalize(TEXEL_NORMAL(id).xyz), fp, u.uFar, ids, ws);
                reach = farSetReach(x, ids, u.uFar);
                // Recording, the rays go into the texel's own patch's record alone (slot 0), past the set's reach.
                if (farRecording) { ids[0] = fp; ws[0] = 1.0; for (int i = 1; i < FAR_SET; ++i) { ids[i] = -1; ws[i] = 0.0; } }
                for (int i = 0; i < FAR_SET; ++i) {
                    sFarIds[i] = ids[i];
                    sFarWeights[i] = ws[i];
                    if (ids[i] < 0) continue;
                    vec3 fn, ft, fb;
                    farFrame(ids[i], fn, ft, fb);
                    sFarN[i] = fn; sFarT[i] = ft; sFarB[i] = fb;
                }
            }
        }
        sFarPatch = max(fp, -1);
        sFarReach = reach;
    }
    // The transport census: the texel's reach (as the far field's, from its own patch alone) and its object.
    if (lid == 0u && u.uTransport.x > 0.5) {
        uint id = nodeIds[uint(u.uDetail.x) + group];
        int tp = texelPatch[id];
        sTransportReach = tp >= 0 ? max(u.uTransport.y, length(TEXEL_POSITION(id).xyz - cookedPatch[3 * tp].xyz) / 0.2126) : 4.0 * u.uTransport.y;
        uint own = tp >= 0 ? floatBitsToUint(cookedPatch[3 * tp].w) : 0xFFFFFFFFu;
        uint placements = uint(u.uDetail.w);
        sTransportObject = own < placements ? censusObject[own] : own;
    }
    barrier();
    if (lid < uint(RAYS)) {
        uint slot = group * uint(RAYS) + lid;
        uvec4 h0 = nodeHits[slot * 3u];
        uvec4 h2 = nodeHits[slot * 3u + 2u];
        vec3 origin = nodeRays[slot * 2u].xyz;
        vec3 dir = nodeRays[slot * 2u + 1u].xyz;
        sDir[lid] = dir;
        sDist[lid] = (h0.w & 1u) != 0u ? uintBitsToFloat(h0.x) : 1e30;
        sMiss[lid] = (h0.w & 1u) != 0u ? 0.0 : nodeRayWeight[slot] / PI;
        sRayWeight[lid] = nodeRayWeight[slot];
        sPlacement[lid] = (h0.w & 1u) != 0u ? nodeHits[slot * 3u + 1u].x : 0xFFFFFFFFu;
        sNearTexel[lid] = -1;
        sCell[lid] = 0xFFFFFFFFu;
        HOW(lid, (h0.w & 1u) != 0u ? 3u : 0u);
#if MAX_WORDS == 1
        sSheet[lid] = vec4(0.0);
        if (sheetsOn && (h0.w & 1u) != 0u) {
            uint hr = nodeHits[slot * 3u + 1u].z;
            uint sw = hr != 0xFFFFFFFFu ? sheetRows[hr & 0x7FFFFFFFu] : 0u;
            if (sw != 0u) sSheet[lid] = vec4(unpackUnorm4x8(sw).rgb, float(sw >> 24) / 255.0);
        }
#endif
        // A ray past the reach (a miss, or one texel_nodes_rays.comp cut at the reach) where every blended patch's
        // record knows its direction: the patches' light, not its own. Recording, every ray past the reach in any bin
        // above its own patch's horizon.
        bool joint = sFarPatch >= 0;
        for (int i = 0; i < FAR_SET; ++i) {
            int b = sFarPatch >= 0 && sFarIds[i] >= 0 ? farBin(dir, sFarN[i], sFarT[i], sFarB[i]) : -1;
            sBin[lid * uint(FAR_SET) + uint(i)] = b;
            if (sFarIds[i] >= 0 && sFarPatch >= 0) joint = joint && b >= 0 && (farRecording || farKnown(sFarIds[i], b, u.uFar));
        }
        int bin = sBin[lid * uint(FAR_SET)];
        bool far = joint && sDist[lid] >= sFarReach;
        sJoint[lid] = joint ? 1.0 : 0.0;
        sFar[lid] = far ? 1.0 : 0.0;
        if (farRecording && far) {
            uint at = farBase(sFarPatch, bin, u.uFar);
            float jitter = fract(float(slot) * 0.6180339887 + u.uShade.w * 0.7548776662);
            atomicAdd(farRecord[at], farFixed(nodeRayWeight[slot], jitter));
            if ((h0.w & 1u) == 0u) atomicAdd(farRecord[at + 1u], farFixed(nodeRayWeight[slot], jitter));
            atomicAdd(farRecord[at + 2u], farFixed(nodeRayWeight[slot] * nodeRayWeight[slot], fract(jitter + 0.5)));
        }
        for (int c = 0; c < NODE_CANDIDATES; ++c) { sWeight[lid * NODE_CANDIDATES + c] = 0.0; sCand[lid * NODE_CANDIDATES + c] = -1; }
        if (far && !farRecording) {
            // Its light is the record's (the column loop); the sky visibility from the record's escaped share.
            sHit[lid] = vec4(0.0, 0.0, 0.0, -3.0);
            float escaped = 0.0;
            for (int i = 0; i < FAR_SET; ++i)
                if (sFarIds[i] >= 0) escaped += sFarWeights[i] * farEscape(sFarIds[i], sBin[lid * uint(FAR_SET) + uint(i)], u.uFar);
            sMiss[lid] = nodeRayWeight[slot] / PI * escaped;
            sNormal[lid] = vec3(0.0);
            sUnknown[lid] = 0.0;
            sExact[lid] = -1.0;
            HOW(lid, 1u);
            nodeCount(5u);
        } else if ((h0.w & 1u) != 0u) {
            vec3 normal = uintBitsToFloat(h2.xyz);
            vec3 hn = dot(normal, dir) > 0.0 ? -normal : normal;
            vec3 hitPos = origin + dir * uintBitsToFloat(h0.x);
            if (u.uFoliage.z > 0.5 && u.uFoliage.z < 1.5) {
                exactOrigins[slot] = dot(hn, sunNodes[0].xyz) > 0.0 ? vec4(hitPos + hn * 0.01, uintBitsToFloat(0x7F800000u)) : vec4(0.0);
                return;
            }
            sNormal[lid] = hn;
            uvec4 h1 = nodeHits[slot * 3u + 1u];
            // Near hits (a contact, a crease) by the search's blend of the texels about them, the rest by their cell: one patch
            // a cell side read the creases' light from across them (crevices' seams 0.83 of the reference against the
            // search's 0.86, thinwall's leak 0.0017 against 0.0001, 2026-10-19).
            // A cell with a contact across it (hit_cells.comp: bit 31 of its word 1) is no answer for a hit: the search's, at
            // the hit.
            uint cellAt = 0u;
            bool byCell = u.uLookup.w > 0.5 && h1.z != 0xFFFFFFFFu && (h1.z & 0x80000000u) == 0u && uintBitsToFloat(h0.x) >= u.uLookup.z;
#if MAX_WORDS == 1
            // A hit on a sheet that lets light through, however far: the search's texels, so its far side's record comes
            // through (nearTexelLight); a cell's patch has no far side for a closed shell -- the teacup's 8 patches, the
            // light across its inside read without what came through its walls.
            if (sSheet[lid].a > 0.0) byCell = false;
#endif
            if (byCell) {
                uint r = hitCells[2u + 2u * h1.z];
                vec2 bary = vec2(float(h1.w & 0xFFFFu), float(h1.w >> 16)) / 65535.0;
                uint ci = min(uint(bary.x * float(r)), r - 1u);
                uint cj = min(uint(bary.y * float(r)), r - 1u - ci);
                cellAt = 1u + 2u * hitCells[0] + 2u * (hitCells[1u + 2u * h1.z] + ci * r - (ci * (ci - 1u)) / 2u + cj);
                byCell = (hitCells[cellAt + 1u] & 0x80000000u) == 0u;
            }
            if (byCell) {
                // The hit's cell: its row's square of r x r cells over the barycentrics (the row's own vertex order), the
                // lower triangle laid out row by row -- one read for the patch on the side it was met from, and the sun of
                // the side facing it (hit_cells.comp mode 1: the blend of the texels' bits this pass made per hit).
                uint at = cellAt;
                uint frontWord = hitCells[at];
                int patchIndex = int((dot(hn, normal) > 0.0 ? frontWord : hitCells[at + 1u]) & 0xFFFFFFu) - 1;
                sCell[lid] = ((at - 1u - 2u * hitCells[0]) / 2u) * 2u + (dot(hn, normal) > 0.0 ? 0u : 1u);
                sHit[lid] = vec4(unpackUnorm4x8(h2.w).rgb * nodeRayWeight[slot] / PI, float(patchIndex));
                sWeight[lid * NODE_CANDIDATES] = float(frontWord >> 24) / 255.0;
                for (uint w = 0u; w < words; ++w) sBits[lid * NODE_CANDIDATES * MAX_WORDS + w] = 0xFFFFFFFFu;
                sExact[lid] = u.uFoliage.z > 2.5 ? ((h0.w & 8u) != 0u ? 1.0 : 0.0) : -1.0;
                sUnknown[lid] = 0.0;
                HOW(lid, 2u);
                nodeCount(0u);
                if (patchIndex < 0) nodeCount(2u);
            } else {
            int ids[NODE_CANDIDATES];
            float weights[NODE_CANDIDATES];
            float total = nodeSetAt(0, hitPos, hn, u.uTexelGrid, uint(u.uShade.z), u.uLookup.xy, ids, weights);
            bool foliage = false;
            if (total <= 0.0 && u.uFoliage.x > 0.5) {
                total = nodeSetAt(1, hitPos, hn, u.uTexelGrid, uint(u.uFoliage.x), u.uLookup.xy, ids, weights);
                foliage = total > 0.0;
                if (foliage) nodeCount(4u);
            }
            // The bounce: from the patch the likeliest texel sees, when the hit lies among texels; else the nearest.
            int patchIndex;
            if (total > 0.0 && !foliage) {
                int likeliest = -1;
                float most = 0.0;
                for (int c = 0; c < NODE_CANDIDATES; ++c) if (ids[c] >= 0 && weights[c] > most) { most = weights[c]; likeliest = ids[c]; }
                patchIndex = max(texelPatch[likeliest], -1);
                sNearTexel[lid] = likeliest;
                if (u.uHitBlend.z > 0.5 && likeliest >= 0) {
                    uint code = (provenance[uint(likeliest) / 4u] >> (8u * (uint(likeliest) % 4u) + 4u)) & 0xFu;
                    if ((code >= 1u && code <= 4u) || code == 10u) nodeCount(6u);
                }
                // The texel saw none of its candidate patches (texel_nodes_patch.comp: 22.5% of Sponza's texels, so a
                // quarter of all hits brought no bounce at all -- dark and blotchy where geometry is busy, 2026-10-14):
                // the nearest patch on the hit's own placement facing within 60 degrees (the user's rule: not the nearest
                // anywhere, which crosses walls at their feet).
                if (patchIndex < 0) patchIndex = cookedNearestOn(hitPos, hn, nodeHits[slot * 3u + 1u].x);
            } else patchIndex = cookedNearest(hitPos, hn);
            sHit[lid] = vec4(unpackUnorm4x8(h2.w).rgb * nodeRayWeight[slot] / PI, float(patchIndex));
            for (int c = 0; c < NODE_CANDIDATES; ++c) {
                if (total <= 0.0 || ids[c] < 0) continue;
                sWeight[lid * NODE_CANDIDATES + c] = weights[c] / total;
                sCand[lid * NODE_CANDIDATES + c] = foliage ? -1 : ids[c];
                for (uint w = 0u; w < words; ++w)
                    sBits[(lid * NODE_CANDIDATES + uint(c)) * MAX_WORDS + w] = foliage
                        ? foliageBits[uint(ids[c]) * words + w] : sunBits[uint(ids[c]) * words + w];
            }
            sExact[lid] = u.uFoliage.z > 2.5 ? ((h0.w & 8u) != 0u ? 1.0 : 0.0)
                        : u.uFoliage.z > 1.5 ? ((exactOccluded[slot] & 1u) != 0u ? 0.0 : 1.0) : -1.0;
            nodeCount(0u);
            if (total <= 0.0) nodeCount(1u);
            if (total <= 0.0 && u.uFoliage.y > 0.5) {
                uint at = atomicAdd(discoverCount, 1u);
                if (at < uint(u.uFoliage.y)) {
                    discovered[2u * at] = vec4(hitPos, 4.0);
                    discovered[2u * at + 1u] = vec4(hn, 0.0);
                }
            }
            sUnknown[lid] = total <= 0.0 ? 1.0 : 0.0;
            if (total <= 0.0) HOW(lid, 4u);
            if (patchIndex < 0) nodeCount(2u);
            }
        } else {
            if (u.uFoliage.z > 0.5 && u.uFoliage.z < 1.5) { exactOrigins[slot] = vec4(0.0); return; }
            sHit[lid] = vec4(nodeRayWeight[slot] * textureLod(uSkyRadiance, dir, u.uSky.y).rgb * u.uSky.x, -2.0);
            sNormal[lid] = vec3(0.0);
            sUnknown[lid] = 0.0;
            nodeCount(3u);
        }
    }
    // Writing the exact-sun experiment's origins: nothing to shade (the whole workgroup leaves, before any barrier).
    if (u.uFoliage.z > 0.5 && u.uFoliage.z < 1.5) return;
    barrier();
    // The transport census: each ray as a relationship, no light in it.
    if (u.uTransport.x > 0.5 && lid < uint(RAYS) && u.uShade.w < 7.5) {
        uint kind, what, nearPatch = 0xFFFFFFFFu;
        if (sPlacement[lid] == 0xFFFFFFFFu) {
            vec3 d = sDir[lid];
            d /= abs(d.x) + abs(d.y) + abs(d.z);
            vec2 o = d.z >= 0.0 ? d.xy : (1.0 - abs(d.yx)) * vec2(d.x >= 0.0 ? 1.0 : -1.0, d.y >= 0.0 ? 1.0 : -1.0);
            ivec2 b = clamp(ivec2((o * 0.5 + 0.5) * 8.0), ivec2(0), ivec2(7));
            kind = 0u; what = uint(b.y * 8 + b.x);
        } else if (sDist[lid] < sTransportReach) {
            uint placements = uint(u.uDetail.w);
            uint hitObject = sPlacement[lid] < placements ? censusObject[sPlacement[lid]] : sPlacement[lid];
            kind = hitObject == sTransportObject ? 1u : 2u;
            int pw = int(sHit[lid].w);
            nearPatch = pw >= 0 ? uint(pw) : 0xFFFFFFFEu;
            what = sNearTexel[lid] >= 0 ? uint(sNearTexel[lid]) : (pw >= 0 ? uint(pw) | (1u << 29) : 0x1FFFFFFFu);
        } else {
            int pw = int(sHit[lid].w);
            kind = 3u; what = pw >= 0 ? uint(pw) : 0x3FFFFFFEu;
        }
        nodeTransport[group * 256u + uint(u.uShade.w) * uint(RAYS) + lid] = uvec2((kind << 30) | (what & 0x3FFFFFFFu), nearPatch);
    }
    // The light-free record: each far ray's hit patch (or the sky) into its texel's patch's bin -- after the hits are
    // shaded, so the hit's patch is known. Recording only.
    if (farRecording && u.uFarTransport.x > 0.5 && lid < uint(RAYS) && sFar[lid] > 0.5 && sFarPatch >= 0) {
        int bin = sBin[lid * uint(FAR_SET)];
        uint base = (uint(sFarPatch) * uint(FAR_BINS) + uint(bin)) * uint(FAR_TRANSPORT_STRIDE);
        uint slotIndex = group * uint(RAYS) + lid;
        uint w = farFixed(sRayWeight[lid], fract(float(slotIndex) * 0.6180339887 + u.uShade.w * 0.7548776662));
        atomicAdd(farTransport[base + uint(2 * FAR_SLOTS) + 2u], w);
        float hw = sHit[lid].w;
        if (hw < -1.5) atomicAdd(farTransport[base + uint(2 * FAR_SLOTS) + 1u], w);       // escaped
        else if (hw < -0.5) atomicAdd(farTransport[base + uint(2 * FAR_SLOTS)], w);       // a hit with no patch
        else {
            uint id = uint(hw) + 1u;
            bool placed = false;
            for (int s = 0; s < FAR_SLOTS && !placed; ++s) {
                uint was = atomicCompSwap(farTransport[base + uint(2 * s)], 0u, id);
                if (was == 0u || was == id) { atomicAdd(farTransport[base + uint(2 * s) + 1u], w); placed = true; }
            }
            if (!placed) atomicAdd(farTransport[base + uint(2 * FAR_SLOTS)], w);
        }
    }
    // The sweep: each bin's rays' weight, and how much of it went far -- the texel's own view of which directions are open
    // past its reach (texel_far_finish.comp weighs the record's light by it).
    if (farOn && !farRecording && lid == 0u) {
        vec2 hs = vec2(0.0, float(RAYS));
        for (int r = 0; r < RAYS; ++r) {
            float d = sFar[r] > 0.5 ? sFarReach : sDist[r];
            if (d < 1e29) hs.x += 1.0 / max(d, 1e-3);
        }
        nodeHarmonic[group] = (u.uShade.w > 0.5 ? nodeHarmonic[group] : vec2(0.0)) + hs;
    }
    // Per blended patch, in its own bins, over the rays every blended patch knows the direction of.
    if (farOn && !farRecording && sFarPatch >= 0)
        for (int ib = int(lid); ib < FAR_SET * FAR_BINS; ib += SHADE_THREADS) {
            int i = ib / FAR_BINS, b = ib % FAR_BINS;
            if (sFarIds[i] < 0) continue;
            vec4 wf = vec4(0.0);
            for (int r = 0; r < RAYS; ++r)
                if (sJoint[r] > 0.5 && sBin[r * FAR_SET + i] == b) {
                    float w = sRayWeight[r];
                    wf += vec4(w, sFar[r] * w, w * w, sFar[r] * w * w);
                }
            uint at = (group * uint(FAR_SET) + uint(i)) * uint(FAR_BINS) + uint(b);
            farBins[at] = (u.uShade.w > 0.5 ? farBins[at] : vec4(0.0)) + wf;
        }
    bool fastColumns = suns <= 2 && !farRecording && u.uDetail.z < 0.5;
    if (fastColumns) {
        const vec3 lumW = vec3(0.2126, 0.7152, 0.0722);
        bool detail = u.uDetail.y > 0.5;
        vec3 dn = vec3(0.0, 1.0, 0.0), dt1 = vec3(1.0, 0.0, 0.0), dt2 = vec3(0.0, 0.0, 1.0);
        if (detail) texelDetailFrame(normalize(TEXEL_NORMAL(nodeIds[uint(u.uDetail.x) + group]).xyz), dn, dt1, dt2);
        // Each ray's known bounce (its patch's light), for the unknown hits' estimate.
        if (lid < uint(RAYS)) {
            int r = int(lid);
            for (int k = 0; k < suns; ++k) {
                float pw = sHit[r].w;
                vec4 known = vec4(0.0);
                if (pw >= 0.0) known = vec4(hitPatchLight(r, k, suns), 1.0);
                sRayKnown[r * 2 + k] = known;
            }
        }
        barrier();
        if (lid < uint(suns)) {
            // The known hits' light, each weighted by what its hit sends back (sHit's rgb: albedo x weight / pi): a sheet hit's
            // light is per unit of ITS albedo (nearTexelLight: T c of the far side over it), so a plain mean handed a dark
            // texel's 1 / albedo to hits of any albedo -- the teacup's saucer gap, 1,000x at the albedo's floor and 60x a
            // record (2026-10-22). Weighted, the mean is the light per unit of albedo the known hits brought.
            vec3 known = vec3(0.0), knownW = vec3(0.0);
            float knownN = 0.0;
            for (int r = 0; r < RAYS; ++r)
                if (sRayKnown[r * 2 + int(lid)].w > 0.0) { known += sHit[r].rgb * sRayKnown[r * 2 + int(lid)].rgb; knownW += sHit[r].rgb; knownN += 1.0; }
            sUnknownBounce[lid] = knownN > 0.0 ? known / max(knownW, vec3(1e-8)) : vec3(0.0);
#if MAX_WORDS == 1
            vec3 near = vec3(0.0), nearW = vec3(0.0);
            float nearN = 0.0;
            if (u.uNear.z > 0.5)
                for (int r = 0; r < RAYS; ++r)
                    if (sHow[r] == 3u && sUnknown[r] < 0.5 && sDist[r] < u.uLookup.z && sHit[r].w >= 0.0) {
                        near += sHit[r].rgb * sRayKnown[r * 2 + int(lid)].rgb; nearW += sHit[r].rgb; nearN += 1.0;
                    }
            sNearKnown[lid] = nearN > 0.0 ? vec4(near / max(nearW, vec3(1e-8)), nearN) : vec4(0.0);
#endif
        }
        barrier();
        // Each ray, each column: the column loop's body (no census, no recording).
        if (lid < uint(RAYS)) {
            int r = int(lid);
            vec4 h = sHit[r];
            vec3 w = sDir[r];
            float tiltDen = max(dot(dn, w), 0.1);
#if MAX_WORDS == 1
            sSunW[r] = vec3(0.0);
#endif
            float captureBounce = 0.0;
            float captureLight = -1.0;   // the ray capture: the light the hit took, its sun column's (a hit among no texel)
            // --ray-capture-sun (the tag's bit 15): the hit's sun at full visibility and n.l 1 (luminance, weight and albedo in),
            // and n.l times the visibility its texels' sun bits blend to -- what a light-free list would give for the hit's sun.
            float captureSunFull = 0.0, captureSunBits = 0.0;
#if MAX_WORDS == 1
            sRayNearGlow[r] = vec3(0.0);
#endif
            for (int k = 0; k < suns; ++k) {
                vec3 toSun = sunNodes[k].xyz;
                uint kind = uint(sunNodes[k].w + 0.5);
                bool hasSun = (kind & 1u) != 0u, hasSky = (kind & 2u) != 0u;
                uint word = uint(k) / 32u, bit = 1u << (uint(k) % 32u);
                vec3 add = vec3(0.0);
                float tiltLight = 0.0;
                bool tilts = detail;
                if (h.w < -2.5) {
                    vec3 fl = vec3(0.0);
                    for (int i = 0; i < FAR_SET; ++i)
                        if (sFarIds[i] >= 0) fl += sFarWeights[i] * farLight(sFarIds[i], sBin[r * FAR_SET + i], k, u.uFar);
                    tiltLight = dot(sRayWeight[r] * fl, lumW);
                } else if (h.w < -1.5) {
                    if (hasSky) add = h.rgb;
                    tiltLight = dot(add, lumW);
                } else {
                    vec3 light = sUnknownBounce[k];
                    if (h.w >= 0.0) light = sRayKnown[r * 2 + k].rgb;
#if MAX_WORDS == 1
                    if (sUnknown[r] > 0.5 && sDist[r] < u.uLookup.z && sNearKnown[k].w > 0.0) light = sNearKnown[k].rgb;
#endif
                    captureBounce += dot(h.rgb * light, lumW);
                    if (k == int(u.uHitBlend.w)) captureLight = dot(light, lumW);
                    float ndotl = hasSun ? max(dot(sNormal[r], toSun), 0.0) : 0.0;
                    vec3 glow = vec3(0.0);
#if MAX_WORDS == 1
                    // A sheet: the base lobe keeps 1 - T of the sun on its side; T c of the sun on its far side is its glow.
                    float baseLobe = 1.0 - sSheet[r].a;
                    // A texel on a surface that lets light through (|w| 5: SponzaLoop.Lightmap.cs) leaves out the glow: the
                    // frame draws the sun through the sheets beyond it per pixel (lit.frag, sheetGlow).
                    bool ownSheet = abs(u.uNear.w - 1.0) < 0.5 && abs(TEXEL_POSITION(nodeIds[uint(u.uDetail.x) + group]).w) > 4.5;
                    if (hasSun && k == exactColumn && sSheet[r].a > 0.0 && sExact[r] >= 0.0 && !ownSheet)
                        glow = sSheet[r].rgb * u.uSunIrradiance.rgb * max(-dot(sNormal[r], toSun), 0.0) * sExact[r] * sRayWeight[r] / PI;
                    if (k == exactColumn) sRayNearGlow[r] = sDist[r] < 0.05 ? glow : vec3(0.0);
#else
                    float baseLobe = 1.0;
#endif
                    if (ndotl > 0.0 && hasSun) {
                        captureSunFull = dot(h.rgb * u.uSunIrradiance.rgb * baseLobe, lumW);
                        float vb = sUnknown[r] * u.uSunIrradiance.w;
                        for (int c = 0; c < NODE_CANDIDATES; ++c)
                            if ((sBits[(uint(r) * NODE_CANDIDATES + uint(c)) * MAX_WORDS + word] & bit) != 0u) vb += sWeight[r * NODE_CANDIDATES + c];
                        captureSunBits = ndotl * vb;
                    }
                    if (ndotl > 0.0) {
                        float sunVis;
                        if (k == exactColumn && sExact[r] >= 0.0) { sunVis = sExact[r] * baseLobe; light += u.uSunIrradiance.rgb * ndotl * sunVis; tilts = false; }
                        else {
                            float visible = sUnknown[r] * u.uSunIrradiance.w;
                            for (int c = 0; c < NODE_CANDIDATES; ++c)
                                if ((sBits[(uint(r) * NODE_CANDIDATES + uint(c)) * MAX_WORDS + word] & bit) != 0u) visible += sWeight[r * NODE_CANDIDATES + c];
                            sunVis = visible;
                            light += u.uSunIrradiance.rgb * ndotl * visible;
                        }
#if MAX_WORDS == 1
                        if (u.uSunW.x > 0.5 && k == exactColumn) sSunW[r] = dot(h.rgb, lumW) * sunVis * sNormal[r];
#endif
                    }
                    add = h.rgb * light + glow;
                    // The capture's alternative takes the sheet's glow as it is (the twin's sun, which a list carries alike).
                    if (hasSun && captureSunFull > 0.0) captureSunBits += dot(glow, lumW) / captureSunFull;
                    else if (hasSun && dot(glow, lumW) > 0.0) { captureSunFull = dot(glow, lumW); captureSunBits = 1.0; }
                    tiltLight = dot(add, lumW);
                }
                sRaySum[r * 2 + k] = add;
                sRayTilt[r * 2 + k] = tilts ? tiltLight * vec2(dot(dt1, w), dot(dt2, w)) / tiltDen : vec2(0.0);
            }
#if MAX_WORDS == 1
            // A sheet's texel keeps no far part: what it sees past the reach includes other sheets' texels, still settling
            // round by round (its rays are not cut either: texel_nodes_rays.comp).
            sRayFar[r] = (sDist[r] >= u.uNear.y || h.w < -1.5) && abs(TEXEL_POSITION(nodeIds[uint(u.uDetail.x) + group]).w) < 4.5 ? 1.0 : 0.0;
            if (u.uFarTransport.y > 0.5 && u.uNear.x < 1.5) {
                uint hid = nodeIds[uint(u.uDetail.x) + group];
                vec3 ns = recordH4Normal(hid, normalize(TEXEL_NORMAL(hid).xyz), u.uFarTransport.z > 0.5);
                vec3 all = sRaySum[r * 2] + (suns > 1 ? sRaySum[r * 2 + 1] : vec3(0.0));
                recordH4Ray(ns, w, all, sH4[r * 3], sH4[r * 3 + 1], sH4[r * 3 + 2]);
            }
            if (u.uCapture.x > 0.5) {
                uint id = nodeIds[uint(u.uDetail.x) + group];
                // y < 0: the one texel -y - 1 (--ray-capture-texel), every ray of it.
                uint bits = uint(max(u.uCapture.y, 0.0));
                bool picked = u.uCapture.y < 0.0 ? id == uint(-u.uCapture.y - 1.0) : (bits == 0u || ((id * 2654435761u) >> (32u - bits)) == 0u);
                if (picked && u.uCapture.x > 1.5) {
                    // --list-cook (SponzaLoop.Lists.cs): the ray light-free -- its target and its weight. Kinds: 0 a miss (the
                    // sky's bin: 8 x 8 octahedral, the world's), 1 a texel (the near hit's likeliest: its light and sun bit
                    // at a light change), 2 a cell's side (hit_cells.comp's CellLight: albedo x its patch's and sun's light),
                    // 3 none of those (among no texel, the foliage's, the far record's: the list's remainder).
                    uint at = atomicAdd(captureCount, 1u);
                    if (at < uint(u.uCapture.w)) {
                        float whole = 0.0;
                        for (int k = 0; k < suns; ++k) whole += dot(sRaySum[r * 2 + k], lumW);
                        uint kind = 3u, target = 0u;
                        if (h.w < -1.5 && h.w > -2.5) {
                            vec3 d = sDir[r] / (abs(sDir[r].x) + abs(sDir[r].y) + abs(sDir[r].z));
                            vec2 o = d.y >= 0.0 ? d.xz : (1.0 - abs(d.zx)) * vec2(d.x >= 0.0 ? 1.0 : -1.0, d.z >= 0.0 ? 1.0 : -1.0);
                            ivec2 b = clamp(ivec2((o * 0.5 + 0.5) * 8.0), ivec2(0), ivec2(7));
                            kind = 0u; target = uint(b.y * 8 + b.x);
                        } else if (sCell[r] != 0xFFFFFFFFu) { kind = 2u; target = sCell[r]; }
                        else if (sNearTexel[r] >= 0 && sUnknown[r] < 0.5 && h.w > -0.5) { kind = 1u; target = uint(sNearTexel[r]); }
                        // rgb: what the hit took -- a miss its sky light (x weight), a hit its albedo x weight / pi.
                        vec3 rgb = h.rgb;
                        // A near hit: one record a candidate texel the search blended (its share of the ray), as the hit's
                        // sun is the blend of their bits -- the first carries the ray (word 7's bit 8 clear), the rest not.
                        uint sub = 0u;
                        if (kind == 1u) {
                            for (int c = 0; c < NODE_CANDIDATES; ++c) {
                                int cand = sCand[r * NODE_CANDIDATES + c];
                                float cw = sWeight[r * NODE_CANDIDATES + c];
                                if (cand < 0 || cw <= 0.0) continue;
                                uint here = sub == 0u ? at : atomicAdd(captureCount, 1u);
                                if (here >= uint(u.uCapture.w)) break;
                                captured[2u * here] = uvec4(id, kind, uint(cand), packHalf2x16(rgb.rg * cw));
                                captured[2u * here + 1u] = uvec4(packHalf2x16(vec2(rgb.b * cw, 0.0)), floatBitsToUint(sub == 0u ? whole : 0.0),
                                    uint(u.uCapture.z) | (uint(u.uShade.w) << 16), uint(r) | (sub == 0u ? 0u : 256u));
                                sub++;
                            }
                        }
                        if (sub == 0u) {
                            captured[2u * at] = uvec4(id, kind == 1u ? 3u : kind, target, packHalf2x16(rgb.rg));
                            captured[2u * at + 1u] = uvec4(packHalf2x16(vec2(rgb.b, sRayWeight[r] / PI)), floatBitsToUint(whole),
                                uint(u.uCapture.z) | (uint(u.uShade.w) << 16), uint(r));
                        }
                    }
                } else if (picked) {
                    uint at = atomicAdd(captureCount, 1u);
                    if (at < uint(u.uCapture.w)) {
                        float whole = 0.0;
                        for (int k = 0; k < suns; ++k) whole += dot(sRaySum[r * 2 + k], lumW);
                        uint how = sHow[r];
                        vec3 near;
                        if (how >= 2u && h.w >= 0.0 && nearTexelLight(r, int(u.uHitBlend.w), near)) how += 8u;
                        if (how >= 2u && h.w < 0.0) how += 16u;
                        vec3 tn = TEXEL_NORMAL(id).xyz;
                        float cosine = dot(tn, tn) > 0.0 ? dot(normalize(tn), w) : 0.0;
                        captured[2u * at] = uvec4(id, uint(u.uCapture.z) | (uint(u.uShade.w) << 16), floatBitsToUint(sDist[r]), how | (uint(r) << 8) | (uint(int(h.w) + 1) << 13));
                        // BLIX_CAPTURE_DIR: word 5 the sun column's luminance alone (weight in), not the bounce's share -- a texel's
                        // gathered sun by direction (tools/gi-model/directional_ratio.py)
                        captured[2u * at + 1u] = uvec4(floatBitsToUint(whole), floatBitsToUint((uint(u.uCapture.z) & 0x4000u) != 0u
                            ? dot(sRaySum[r * 2 + int(u.uHitBlend.w)], lumW) : captureBounce),
                            (uint(u.uCapture.z) & 0x8000u) != 0u ? packHalf2x16(vec2(captureSunFull, captureSunBits))
                            // BLIX_CAPTURE_DIR (tag 0x4000): the ray's direction, octahedral (snorm 2 x 16), world space
                            : (uint(u.uCapture.z) & 0x4000u) != 0u ? packSnorm2x16(captureOct(sDir[r]))
                            : packHalf2x16(vec2(cosine, sRayWeight[r])),
                            // BLIX_CAPTURE_DIR: word 7 the ray's weight (cos / its mixture's pdf), the near texel's place
                            (uint(u.uCapture.z) & 0x4000u) != 0u ? floatBitsToUint(sRayWeight[r])
                            : sNearTexel[r] >= 0 ? uint(sNearTexel[r]) : floatBitsToUint(-max(captureLight, 1e-30)));
                    }
                }
            }
#endif
        }
        barrier();
#if MAX_WORDS == 1
        if (u.uFarTransport.y > 0.5 && u.uNear.x < 1.5 && lid < 4u) {
            uint hid = nodeIds[uint(u.uDetail.x) + group];
            uint at4 = (u.uFarTransport.y > 1.5 ? hid : group) * 4u + lid;
            vec4 before = u.uShade.w > 0.5 ? texelH4[at4] : vec4(0.0);
            vec4 after;
            if (lid < 3u) {
                vec4 sum = vec4(0.0);
                for (int r = 0; r < RAYS; ++r) sum += sH4[r * 3 + int(lid)];
                after = before + sum / float(RAYS);
            } else {
                float plain = 0.0;
                for (int r = 0; r < RAYS; ++r) plain += dot(sRaySum[r * 2] + (suns > 1 ? sRaySum[r * 2 + 1] : vec3(0.0)), lumW);
                vec3 ns = recordH4Normal(hid, normalize(TEXEL_NORMAL(hid).xyz), u.uFarTransport.z > 0.5);
                after = vec4(before.x + plain / float(RAYS), uintBitsToFloat(packSnorm2x16(recordH4Oct(ns))), 0.0, before.w + 1.0);
            }
            texelH4[at4] = after;
            sRecord[lid] = after;
        }
        barrier();
        if (u.uFarTransport.y > 0.5 && u.uNear.x < 1.5 && lid == 0u)
            texelShapeStore(nodeIds[uint(u.uDetail.x) + group], sRecord[0], sRecord[1], sRecord[2], sRecord[3]);
        // The sun-linear record, by id: a slice's first pass starts it (as the sums), a near round leaves it (its rays are cut).
        if (u.uSunW.x > 0.5 && u.uNear.x < 1.5 && lid == 0u) {
            uint hid = nodeIds[uint(u.uDetail.x) + group];
            vec3 sum = vec3(0.0);
            for (int r = 0; r < RAYS; ++r) sum += sSunW[r];
            vec4 before = u.uShade.w > 0.5 ? texelSunW[hid] : vec4(0.0);
            texelSunW[hid] = before + vec4(sum / float(RAYS), 1.0);
        }
#endif
        if (lid < uint(suns)) {
            int k = int(lid);
            vec3 sum = vec3(0.0);
            vec2 tilt = vec2(0.0);
            for (int r = 0; r < RAYS; ++r) { sum += sRaySum[r * 2 + k]; tilt += sRayTilt[r * 2 + k]; }
#if MAX_WORDS == 1
            if (u.uNear.x > 0.5) {
                uint place = finePlace[nodeIds[uint(u.uDetail.x) + group]];
                vec3 farSum = vec3(0.0);
                for (int r = 0; r < RAYS; ++r) farSum += sRayFar[r] * sRaySum[r * 2 + k];
                if (place > 0u) {
                    uint fp = (place - 1u) * 2u + uint(k);
                    if (u.uNear.x < 1.5) farPart[fp] += vec4(farSum / float(RAYS), 1.0);
                    else {
                        // The cut rays met "nothing" (the sky's light): theirs and the misses' out, the fine round's far part in.
                        vec4 f = farPart[fp];
                        sum += (f.w > 0.0 ? f.rgb / f.w : vec3(0.0)) * float(RAYS) - farSum;
                    }
                }
            }
#endif
            uint at = group * uint(suns) + uint(k);
            if (detail) {
                float escaped = 0.0;
                if (k == 0) for (int r = 0; r < RAYS; ++r) escaped += sMiss[r];
                vec4 d = vec4(tilt / float(RAYS), escaped / float(RAYS), 0.0);
                nodeDetail[at] = u.uShade.w > 0.5 ? nodeDetail[at] + d : d;
            }
            vec3 mean = sum / float(RAYS);
            vec4 before = u.uShade.w > 0.5 ? nodeAccum[at] : vec4(0.0);
            nodeAccum[at] = vec4(before.rgb + mean, before.w);
#if MAX_WORDS == 1
            if (k == exactColumn && u.uNear.w > 1.5) {
                vec3 g = vec3(0.0);
                if (abs(TEXEL_POSITION(nodeIds[uint(u.uDetail.x) + group]).w) > 4.5)
                    for (int r = 0; r < RAYS; ++r) g += sRayNearGlow[r];
                vec4 gb = u.uShade.w > 0.5 ? nodeGlow[group] : vec4(0.0);
                nodeGlow[group] = vec4(gb.rgb + g / float(RAYS), 0.0);
            }
#endif
            sColumnLum[k] = dot(mean, lumW);
            sColumnSource[k] = vec4(0.0); sColumnFar[k] = vec4(0.0); sColumnNear[k] = vec4(0.0); sColumnCompare[k] = vec4(0.0);
        }
    }
    for (int k = int(lid); k < suns && !fastColumns; k += SHADE_THREADS) {
        vec3 toSun = sunNodes[k].xyz;
        // The column's kind (SunNodes w): 1 its sun alone, 2 the sky alone, 3 both. Each part is what that part lights:
        // the sky the misses and (through the patches' sky part) the hits; the sun the hits, directly and through the
        // patches' sun part. The patches' light for the column is already that part's (the solve's component).
        uint kind = uint(sunNodes[k].w + 0.5);
        bool hasSun = (kind & 1u) != 0u, hasSky = (kind & 2u) != 0u;
        uint word = uint(k) / 32u, bit = 1u << (uint(k) % 32u);
        vec3 sum = vec3(0.0);
        // The cells' detail: the texel's frame, and each ray's contribution's luminance times its direction's tilt.
        bool detail = u.uDetail.y > 0.5;
        vec3 dn = vec3(0.0, 1.0, 0.0), dt1 = vec3(1.0, 0.0, 0.0), dt2 = vec3(0.0, 0.0, 1.0);
        if (detail) texelDetailFrame(normalize(TEXEL_NORMAL(nodeIds[uint(u.uDetail.x) + group]).xyz), dn, dt1, dt2);
        vec2 tilt = vec2(0.0);
        const vec3 lumW = vec3(0.2126, 0.7152, 0.0722);
        vec4 source = vec4(0.0);
        vec4 far = vec4(0.0), near = vec4(0.0), compare = vec4(0.0);
        bool census = u.uDetail.z > 0.5;
        // A hit with no patch (none seen, none on its placement facing its way: 4.5% of Sponza's hits, 2026-10-14) has no
        // known bounce -- unknown, not dark. It took 0, and those hits gather where the light is darkest (crevices, under
        // the canopy), so the dark was painted in. Now it takes the mean bounce of this texel's hits this pass that have
        // one: the estimate over what is known. (None known: 0, as before.)
        vec3 knownBounce = vec3(0.0), knownW = vec3(0.0);
        float knownHits = 0.0;
        for (int r = 0; r < RAYS; ++r) {
            float pw = sHit[r].w;
            if (pw < 0.0) continue;
            // Weighted by what the hit sends back, as the fast path's (sUnknownBounce): a sheet hit's light is per unit of its albedo.
            knownBounce += sHit[r].rgb * hitPatchLight(r, int(k), suns);
            knownW += sHit[r].rgb;
            knownHits += 1.0;
        }
        vec3 unknownBounce = knownHits > 0.0 ? knownBounce / max(knownW, vec3(1e-8)) : vec3(0.0);
        for (int r = 0; r < RAYS; ++r) {
            vec4 h = sHit[r];
            vec3 before = sum;
            if (h.w < -2.5) {
                // Far: its light comes in with the record's (texel_far_finish.comp); the direction still counts it.
                vec3 fl = vec3(0.0);
                for (int i = 0; i < FAR_SET; ++i)
                    if (sFarIds[i] >= 0) fl += sFarWeights[i] * farLight(sFarIds[i], sBin[r * FAR_SET + i], k, u.uFar);
                if (detail) { float l = dot(sRayWeight[r] * fl, lumW); vec3 w = sDir[r]; tilt += l * vec2(dot(dt1, w), dot(dt2, w)) / max(dot(dn, w), 0.1); }
                continue;
            }
            if (h.w < -1.5) {
                if (hasSky) { sum += h.rgb; source.x += dot(h.rgb, lumW); if (census) far.xyz += dot(h.rgb, lumW); }
                if (farRecording && sFar[r] > 0.5) farRecordAdd(sFarPatch, sBin[r * FAR_SET], k, sum - before, sRayWeight[r], group * uint(RAYS) + uint(r));
                if (detail) { float l = dot(sum - before, vec3(0.2126, 0.7152, 0.0722)); vec3 w = sDir[r]; tilt += l * vec2(dot(dt1, w), dot(dt2, w)) / max(dot(dn, w), 0.1); }
                continue;
            }
            vec3 light = unknownBounce;
            if (h.w >= 0.0) light = hitPatchLight(r, int(k), suns);
            if (h.w >= 0.0) source.y += dot(h.rgb * light, lumW); else source.w += dot(h.rgb * light, lumW);
            float ndotl = hasSun ? max(dot(sNormal[r], toSun), 0.0) : 0.0;
            // The near-field comparison: this near hit's direct as the patch level would give it (its patch's average
            // n.l x visibility), against the per-texel one below.
            bool comparing = census && u.uTransport.w > 0.5 && hasSun && sFarPatch >= 0 && sDist[r] < sFarReach && int(h.w) >= 0;
            float patchLevel = 0.0, texelLevel = 0.0;
            if (comparing) {
                uint columnsAll = uint(suns), patches = uint(u.uTransport.z);
                uint hp = uint(h.w);
                float texelsThere = float(patchDirect[patches * columnsAll + hp]);
                patchLevel = texelsThere > 0.0 ? float(patchDirect[hp * columnsAll + uint(k)]) / 65536.0 / texelsThere : 0.0;
            }
#if MAX_WORDS == 1
            if (hasSun && k == exactColumn && sSheet[r].a > 0.0 && sExact[r] >= 0.0) {
                // A sheet (as above): 1 - T of the sun on its side, T c of the sun on its far side.
                bool ownSheet = abs(u.uNear.w - 1.0) < 0.5 && abs(TEXEL_POSITION(nodeIds[uint(u.uDetail.x) + group]).w) > 4.5;
                sum += h.rgb * (light + u.uSunIrradiance.rgb * ndotl * sExact[r] * (1.0 - sSheet[r].a))
                     + (ownSheet ? vec3(0.0) : sSheet[r].rgb * u.uSunIrradiance.rgb * max(-dot(sNormal[r], toSun), 0.0) * sExact[r] * sRayWeight[r] / PI);
                continue;
            }
#endif
            if (ndotl > 0.0) {
                float visible = sUnknown[r] * u.uSunIrradiance.w;
                if (k == exactColumn && sExact[r] >= 0.0) { light += u.uSunIrradiance.rgb * ndotl * sExact[r]; sum += h.rgb * light; continue; }
                for (int c = 0; c < NODE_CANDIDATES; ++c)
                    if ((sBits[(uint(r) * NODE_CANDIDATES + uint(c)) * MAX_WORDS + word] & bit) != 0u) visible += sWeight[r * NODE_CANDIDATES + c];
                light += u.uSunIrradiance.rgb * ndotl * visible;
                float sunHere = dot(h.rgb * u.uSunIrradiance.rgb * ndotl * visible, lumW);
                texelLevel = ndotl * visible;
                source.z += sunHere;
                if (census) { if (sDist[r] >= 1.0) far.w += sunHere; else near.w += sunHere; }
            }
            if (comparing) {
                float a = dot(h.rgb * u.uSunIrradiance.rgb * patchLevel, lumW), b = dot(h.rgb * u.uSunIrradiance.rgb * texelLevel, lumW);
                compare += vec4(a - b, b, a, 0.0);
            }
            sum += h.rgb * light;
            if (farRecording && sFar[r] > 0.5) farRecordAdd(sFarPatch, sBin[r * FAR_SET], k, sum - before, sRayWeight[r], group * uint(RAYS) + uint(r));
            if (census) {
                float l = dot(h.rgb * light, lumW);
                vec3 beyond = vec3(greaterThanEqual(vec3(sDist[r]), vec3(0.5, 1.0, 2.0)));
                far.xyz += l * beyond;
                near.xyz += l * (1.0 - beyond);
            }
            if (detail) { float l = dot(sum - before, vec3(0.2126, 0.7152, 0.0722)); vec3 w = sDir[r]; tilt += l * vec2(dot(dt1, w), dot(dt2, w)) / max(dot(dn, w), 0.1); }
        }
        uint at = group * uint(suns) + uint(k);
        if (detail) {
            float escaped = 0.0;
            if (k == 0) for (int r = 0; r < RAYS; ++r) escaped += sMiss[r];
            vec4 d = vec4(tilt / float(RAYS), escaped / float(RAYS), 0.0);
            nodeDetail[at] = u.uShade.w > 0.5 ? nodeDetail[at] + d : d;
        }
        vec3 mean = sum / float(RAYS);
        vec4 before = u.uShade.w > 0.5 ? nodeAccum[at] : vec4(0.0);
        nodeAccum[at] = vec4(before.rgb + mean, before.w);
        sColumnLum[k] = dot(mean, vec3(0.2126, 0.7152, 0.0722));
        sColumnSource[k] = source / float(RAYS);
        sColumnFar[k] = far / float(RAYS);
        sColumnNear[k] = near / float(RAYS);
        sColumnCompare[k] = compare / float(RAYS);
    }
    barrier();
    // Thread 0 wrote column 0 above: the moment rides on it.
    if (lid == 0u) {
        float composed = 0.0;
        for (int k = 0; k < suns; ++k) composed += sColumnLum[k];
        // The sky's part kept (a sun change, SponzaLoop.SliceResolve.cs): the noise is the sun's alone.
        if (u.uSky.z > 0.5) composed = sColumnLum[int(u.uSky.z) - 1];
        nodeAccum[group * uint(suns)].w += composed * composed;
        if (u.uDetail.z > 0.5) {
            vec4 bySource = vec4(0.0), byFar = vec4(0.0), byNear = vec4(0.0);
            for (int k = 0; k < suns; ++k) { bySource += sColumnSource[k]; byFar += sColumnFar[k]; byNear += sColumnNear[k]; }
            bool first = u.uShade.w < 0.5;
            uint at = 10u * group;
            nodeCensus[at] = (first ? vec4(0.0) : nodeCensus[at]) + bySource;
            nodeCensus[at + 1u] = (first ? vec4(0.0) : nodeCensus[at + 1u]) + bySource * bySource;
            nodeCensus[at + 2u] = (first ? vec4(0.0) : nodeCensus[at + 2u]) + byFar;
            nodeCensus[at + 3u] = (first ? vec4(0.0) : nodeCensus[at + 3u]) + byFar * byFar;
            nodeCensus[at + 4u] = (first ? vec4(0.0) : nodeCensus[at + 4u]) + byNear;
            nodeCensus[at + 5u] = (first ? vec4(0.0) : nodeCensus[at + 5u]) + byNear * byNear;
            vec4 view = vec4(0.0), kinds = vec4(0.0), objects = vec4(0.0);
            if (sFarPatch >= 0) {
                uint own = floatBitsToUint(cookedPatch[3 * sFarPatch].w);
                uint placements = uint(u.uDetail.w);
                uint ownObject = own < placements ? censusObject[own] : own;
                int seen[RAYS];
                int seenCount = 0;
                float weightSum = 0.0;
                for (int r = 0; r < RAYS; ++r) {
                    float w = sRayWeight[r];
                    weightSum += w;
                    bool hit = sPlacement[r] != 0xFFFFFFFFu;
                    if (!hit) { view.w += w; continue; }
                    if (sFar[r] > 0.5 || sDist[r] >= sFarReach) { view.z += w; continue; }
                    bool same = sPlacement[r] == own;
                    if (same) view.x += w; else view.y += w;
                    if (!same) {
                        uint hitObject = sPlacement[r] < placements ? censusObject[sPlacement[r]] : sPlacement[r];
                        if (hitObject == ownObject) objects.x += w; else objects.y += w;
                    }
                    int pw = int(sHit[r].w);
                    if (pw < 0) continue;
                    bool again = false;
                    for (int k = 0; k < seenCount; ++k) again = again || seen[k] == pw;
                    if (again) continue;
                    seen[seenCount++] = pw;
                    if (floatBitsToUint(cookedPatch[3 * pw].w) == own) kinds.x += 1.0; else kinds.y += 1.0;
                }
                // A shortened ray that met nothing went past R: it counted as escaped above; a far ray is past R.
                for (int r = 0; r < RAYS; ++r) if (sPlacement[r] == 0xFFFFFFFFu && sFar[r] > 0.5) { view.w -= sRayWeight[r]; view.z += sRayWeight[r]; }
                if (weightSum > 0.0) { view /= weightSum; objects.xy /= weightSum; }
                objects.z = objects.y > 0.0 ? 1.0 : 0.0;
                kinds.z = view.y > 0.0 ? 1.0 : 0.0;
                kinds.w = sFarReach;
            }
            nodeCensus[at + 6u] = (first ? vec4(0.0) : nodeCensus[at + 6u]) + view;
            nodeCensus[at + 7u] = (first ? vec4(0.0) : nodeCensus[at + 7u]) + kinds;
            nodeCensus[at + 8u] = (first ? vec4(0.0) : nodeCensus[at + 8u]) + objects;
            vec4 compare = vec4(0.0);
            for (int k = 0; k < suns; ++k) compare += sColumnCompare[k];
            nodeCensus[at + 9u] = (first ? vec4(0.0) : nodeCensus[at + 9u]) + compare;
        }
    }
}
#endif
