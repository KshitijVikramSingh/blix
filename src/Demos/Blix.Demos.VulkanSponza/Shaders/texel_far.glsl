// The far field (SponzaLoop.FarField.cs): a texel traces only what lies near it; what lies beyond its reach R it reads
// from its patch's record -- the light arriving at the patch from beyond, per direction, gathered by every texel of the
// patch (the record pass: ~80 texels x 128 rays a patch). The noise census (2026-10-14): 85% of the unsettled texels'
// variance came from beyond 1 m (the sun at hits beyond 1 m alone 44%) -- shaded texels finding a little of the sunlit
// floor across the hall, ray by ray; their patch, pooling a hundred texels' rays, knows that light a hundred times better.
//   THE BINS     FAR_BINS hemispherical octahedral bins about the patch's normal (6 x 6: ~0.17 sr, ~24 degrees each).
//   THE REACH    R = max(the patch spacing, d / tan(12 deg)), d the texel's distance from its patch's centre: past R the
//                texel and the patch see the same thing in a direction to within half a bin (the parallax), and nearer
//                than a spacing the patch knows nothing finer than itself. No number picked: the sizes give it.
//   THE RECORD   per patch, per bin: the rays' weight (fixed point), the escaped rays' weight (the sky visibility), the
//                rays' weight^2 (the effective rays, (sum w)^2 / sum w^2: a guided ray toward the bright weighs a
//                fraction of a cosine ray's, and counting weight / pi undercounted the bright bins 3-10x), per
//                column the weight x radiance summed -- the cosine-weighted mean radiance, unbiased under the guided and
//                aimed rays -- and per column the weight x luminance^2 summed (the bin's spread: its mean's error). The
//                record is refined until its error is small against the patch's far light (texel_far_judge.comp), then
//                a bin still too uncertain is made unknown (its weight 0): a ray there is the texel's own, as before --
//                the rare light (a doorway seen down a dark corridor) stays with the texel's rays, which refine.
//   THE BLEND    a texel reads not one patch's record but up to FAR_SET patches' (farSet): its own and its own's
//                neighbours (texel_far_neighbours.comp: seen along the surface, so never across a thin wall), each
//                weighted by a smooth kernel of its distance (zero at one spacing) and the normals' agreement. Each
//                patch's far light is its own estimate, in its own bins: the texel's rays are binned in each patch's
//                frame, so a patch's record is weighed by the texel's open share in THAT patch's directions -- blending
//                the records under one patch's open share would hide the step and keep the bias (the user's review,
//                2026-10-14). A ray is far only where every blended patch knows its direction; the reach is the
//                farthest blended patch's.
// The includer declares FarRecord at FAR_RECORD_BINDING and PatchNeighbours at FAR_NEIGHBOURS_BINDING (FAR_RECORD_ACCESS: readonly or not), includes cooked_patches.glsl
// first, and passes uFar: x 0 off, 1 the record pass, 2 the sweep reads the record; y the patch spacing; z the weight a
// bin needs to be known (fixed point); w the columns.

#ifndef TEXEL_FAR_GLSL
#define TEXEL_FAR_GLSL

#define FAR_BINS 36
#define FAR_RES 6
#define FAR_FIXED 4096.0
// tan(12 degrees): half a bin.
#define FAR_HALF_BIN_TAN 0.2126

layout(std430, set = 0, binding = FAR_RECORD_BINDING) FAR_RECORD_ACCESS buffer FarRecord { uint farRecord[]; };
#ifdef FAR_NEIGHBOURS_BINDING
layout(std430, set = 0, binding = FAR_NEIGHBOURS_BINDING) readonly buffer PatchNeighbours { int farNeighbours[]; };
#endif
#define FAR_SET 4
#define FAR_NEIGHBOURS 8

// A bin's words: weight, escaped weight, weight^2, rgb per column, weight x luminance^2 per column.
uint farStride(vec4 far) { return 3u + 4u * uint(far.w + 0.5); }
uint farRgbAt(uint base, int column) { return base + 3u + 3u * uint(column); }
uint farSquareAt(uint base, int column, vec4 far) { return base + 3u + 3u * uint(far.w + 0.5) + uint(column); }
// The bin's effective rays: (sum w)^2 / sum w^2.
float farRays(uint base) { float w = float(farRecord[base]); return w * w / max(float(farRecord[base + 2u]) * FAR_FIXED, 1.0); }
uint farBase(int patchIndex, int bin, vec4 far) { return (uint(patchIndex) * uint(FAR_BINS) + uint(bin)) * farStride(far); }

void farFrame(int patchIndex, out vec3 n, out vec3 t, out vec3 b) {
    n = normalize(cookedPatch[3 * patchIndex + 1].xyz);
    t = normalize(abs(n.y) < 0.999 ? cross(vec3(0.0, 1.0, 0.0), n) : cross(vec3(1.0, 0.0, 0.0), n));
    b = cross(n, t);
}

// A direction's bin in the patch's frame (-1: below the patch's horizon -- the texel's own rays answer there).
int farBin(vec3 d, vec3 n, vec3 t, vec3 b) {
    float z = dot(d, n);
    if (z <= 0.0) return -1;
    float x = dot(d, t), y = dot(d, b);
    float s = abs(x) + abs(y) + z;
    vec2 p = vec2(x, y) / s;
    vec2 uv = vec2(p.x + p.y, p.x - p.y) * 0.5 + 0.5;
    ivec2 c = clamp(ivec2(uv * float(FAR_RES)), ivec2(0), ivec2(FAR_RES - 1));
    return c.y * FAR_RES + c.x;
}

float farReach(vec3 texelPosition, int patchIndex, vec4 far) {
    return max(far.y, length(texelPosition - cookedPatch[3 * patchIndex].xyz) / FAR_HALF_BIN_TAN);
}

bool farKnown(int patchIndex, int bin, vec4 far) {
    return float(farRecord[farBase(patchIndex, bin, far)]) >= far.z;
}

vec3 farLight(int patchIndex, int bin, int column, vec4 far) {
    uint at = farBase(patchIndex, bin, far);
    float w = max(float(farRecord[at]), 1.0);
    uint c = farRgbAt(at, column);
    return vec3(float(farRecord[c]), float(farRecord[c + 1u]), float(farRecord[c + 2u])) / w;
}

// A bin's mean's standard error, composed over the columns: its spread (the luminance^2 words) over its effective rays.
float farBinError(int patchIndex, int bin, vec4 far) {
    uint base = farBase(patchIndex, bin, far);
    float w = float(farRecord[base]);
    if (w <= 0.0) return 0.0;
    float variance = 0.0;
    for (int k = 0; k < int(far.w + 0.5); ++k) {
        uint at = farRgbAt(base, k);
        float m = dot(vec3(float(farRecord[at]), float(farRecord[at + 1u]), float(farRecord[at + 2u])) / w, vec3(0.2126, 0.7152, 0.0722));
        variance += max(float(farRecord[farSquareAt(base, k, far)]) / w - m * m, 0.0);
    }
    return sqrt(variance / max(farRays(base), 1.0));
}

float farEscape(int patchIndex, int bin, vec4 far) {
    uint at = farBase(patchIndex, bin, far);
    return float(farRecord[at + 1u]) / max(float(farRecord[at]), 1.0);
}

#ifdef FAR_NEIGHBOURS_BINDING
// The patches a texel at x (normal nx), whose own patch is primary, blends: the FAR_SET heaviest of its own and its own's
// neighbours, weights normalized. A kernel zero at one spacing holds at most four patches of a regular layout, and any
// patch within one spacing of x is within 1.75 of its own patch -- among its neighbours -- so the set changes only
// where the weights leaving it are zero: continuous. None weighing anything (the texel far from every patch): its own
// alone, as before. Returns how many.
void farSetConsider(int c, vec3 x, vec3 nx, vec4 far, inout int ids[FAR_SET], inout float ws[FAR_SET]) {
    float d = length(x - cookedPatch[3 * c].xyz) / far.y;
    float k = max(1.0 - d * d, 0.0);
    float w = k * k * smoothstep(0.5, 0.9, dot(nx, normalize(cookedPatch[3 * c + 1].xyz)));
    if (w <= ws[FAR_SET - 1]) return;
    int at = FAR_SET - 1;
    while (at > 0 && ws[at - 1] < w) { ws[at] = ws[at - 1]; ids[at] = ids[at - 1]; --at; }
    ws[at] = w;
    ids[at] = c;
}

int farSet(vec3 x, vec3 nx, int primary, vec4 far, out int ids[FAR_SET], out float ws[FAR_SET]) {
    for (int i = 0; i < FAR_SET; ++i) { ids[i] = -1; ws[i] = 0.0; }
    farSetConsider(primary, x, nx, far, ids, ws);
    for (int i = 0; i < FAR_NEIGHBOURS; ++i) {
        int c = farNeighbours[primary * FAR_NEIGHBOURS + i];
        if (c >= 0) farSetConsider(c, x, nx, far, ids, ws);
    }
    float total = 0.0;
    int count = 0;
    for (int i = 0; i < FAR_SET; ++i) if (ids[i] >= 0) { total += ws[i]; count++; }
    if (total <= 0.0) {
        ids[0] = primary; ws[0] = 1.0;
        for (int i = 1; i < FAR_SET; ++i) { ids[i] = -1; ws[i] = 0.0; }
        return 1;
    }
    for (int i = 0; i < FAR_SET; ++i) ws[i] /= total;
    return count;
}

// The reach for the set: past it the texel and every blended patch see a direction alike to within half a bin.
float farSetReach(vec3 x, int ids[FAR_SET], vec4 far) {
    float r = far.y;
    for (int i = 0; i < FAR_SET; ++i)
        if (ids[i] >= 0) r = max(r, length(x - cookedPatch[3 * ids[i]].xyz) / FAR_HALF_BIN_TAN);
    return r;
}
#endif

// Fixed point, rounded stochastically (u in [0, 1)): unbiased for the darkest light.
uint farFixed(float v, float u) { return uint(max(v, 0.0) * FAR_FIXED + u); }

#endif
