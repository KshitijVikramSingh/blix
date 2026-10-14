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
// The includer declares FarRecord at FAR_RECORD_BINDING (FAR_RECORD_ACCESS: readonly or not), includes cooked_patches.glsl
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

float farEscape(int patchIndex, int bin, vec4 far) {
    uint at = farBase(patchIndex, bin, far);
    return float(farRecord[at + 1u]) / max(float(farRecord[at]), 1.0);
}

// Fixed point, rounded stochastically (u in [0, 1)): unbiased for the darkest light.
uint farFixed(float v, float u) { return uint(max(v, 0.0) * FAR_FIXED + u); }

#endif
