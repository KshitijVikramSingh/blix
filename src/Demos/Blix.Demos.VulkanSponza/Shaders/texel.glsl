// Texels (stage 5a, --transport-texels): what a PIXEL reads. The cooked patches (0.25 m) carry transport -- what a ray
// reads where it lands -- but read at a shading point they stayed 21-28% off on Sponza's hall whatever the
// reconstruction: on detailed architecture a patch even a few centimetres away sits among other occluders than the
// point. Texels on the point's own surface, each gathering the cooked light, measured 13.5% at 5 cm (10.8% at 2.5 cm,
// the gather's own floor) on the CPU.
//
// The cook places one texel per (cell of S metres x normal bin) on every architecture surface (a cutout triangle
// gets none, as with patches), at the centroid of the surface inside the cell; a hash over (cell, bin) finds it.
// The bin is the FACE normal's (texel_bin.glsl: 26 directions, axis and 45-degree normals at bins' centres), so a
// corner's two walls, or a sheet's two sides, never share a texel; the pre-pass writes each pixel's face bins.
//
// The includer defines TEXEL_HASH(i) (the hash table's slot i: uvec4 key x, key y, texel index, unused) and
// TEXEL_POSITION(i) / TEXEL_NORMAL(i) (a texel's vec4s: xyz its position, w its surface samples; xyz its mean face
// normal).
#ifndef TEXEL_GLSL
#define TEXEL_GLSL

#define TEXEL_EMPTY 0xFFFFFFFFu
#define TEXEL_PROBES 64

struct TexelGrid {
    vec3 minCorner;   // the cells' origin (cell = floor((p - minCorner) / spacing))
    float spacing;
    uint capacity;    // hash slots, a power of two
};

#include "texel_bin.glsl"

uvec2 texelKey(ivec3 cell, uint bin) {
    return uvec2(uint(cell.x) | (uint(cell.y) << 16), uint(cell.z) | (bin << 16));
}

// The same mix as the cook's (TexelHash in SponzaLoop.Texels.cs).
uint texelMix(uvec2 k) {
    uint h = k.x * 0x9E3779B1u;
    h ^= k.y * 0x85EBCA77u;
    h ^= h >> 15;
    h *= 0x2C1B3C6Du;
    h ^= h >> 12;
    return h;
}

int texelFind(TexelGrid g, ivec3 cell, uint bin) {
    if (any(lessThan(cell, ivec3(0))) || any(greaterThan(cell, ivec3(65535)))) return -1;
    uvec2 key = texelKey(cell, bin);
    uint slot = texelMix(key) & (g.capacity - 1u);
    for (int i = 0; i < TEXEL_PROBES; ++i) {
        uvec4 e = TEXEL_HASH(slot);
        if (e.x == TEXEL_EMPTY) return -1;
        if (e.x == key.x && e.y == key.y) return int(e.z);
        slot = (slot + 1u) & (g.capacity - 1u);
    }
    return -1;
}

// The texels a point reads: the eight cells around it (trilinear), in its face's bin, or without one in each of up
// to five normal bins -- its normal's,
// and those of its normal tilted 25 degrees either way along two tangents. The pre-pass's normal is the interpolated
// VERTEX normal, while a texel's bin is its triangle's FACE normal: on Sponza's hall the two bins differed at 189 of
// 576 points, and the lookup found nothing at 52 of them. Each texel weighs by trilinear share x (its face normal .
// the point's normal)^4, and is dropped when its own plane passes more than a cell from the point.
#define TEXEL_BINS 5
#define TEXEL_CANDIDATES (8 * TEXEL_BINS)
// faceBin: the pixel's face bins as the pre-pass writes them in its normal target's w (texelFaceBins); when there
// only those are searched, else the five above. Searching five cost 5.3 ms in the mark pass alone (Sponza's hall).
void texelsAround(TexelGrid g, vec3 p, vec3 n, float faceBin, out int ids[TEXEL_CANDIDATES], out float weights[TEXEL_CANDIDATES]) {
    vec3 t1 = normalize(cross(abs(n.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), n));
    vec3 t2 = cross(n, t1);
    const float tilt = 0.466;   // tan(25 degrees)
    uint bins[TEXEL_BINS];
    // With the face's bins (texel_bin.glsl, texelFaceBins): its own, and the one across a boundary it lies within ~2
    // degrees of.
    bool exact = faceBin >= 1.0;
    uint packed = exact ? uint(faceBin + 0.5) - 1u : 0u;
    bins[0] = exact ? packed % 32u : texelBin(n);
    bins[1] = exact ? packed / 32u : texelBin(normalize(n + tilt * t1));
    bins[2] = exact ? bins[0] : texelBin(normalize(n - tilt * t1));
    bins[3] = exact ? bins[0] : texelBin(normalize(n + tilt * t2));
    bins[4] = exact ? bins[0] : texelBin(normalize(n - tilt * t2));
    vec3 f = (p - g.minCorner) / g.spacing - 0.5;
    ivec3 base = ivec3(floor(f));
    vec3 t = f - vec3(base);
    for (int b = 0; b < TEXEL_BINS; ++b) {
        bool repeat = false;
        for (int c = 0; c < b; ++c) repeat = repeat || bins[c] == bins[b];
        for (int i = 0; i < 8; ++i) {
            int slot = b * 8 + i;
            ids[slot] = -1;
            weights[slot] = 0.0;
            if (repeat) continue;
            ivec3 o = ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
            int id = texelFind(g, base + o, bins[b]);
            if (id < 0) continue;
            vec3 tn = TEXEL_NORMAL(id).xyz;
            float facing = dot(tn, n);
            if (facing <= 0.0 || abs(dot(tn, TEXEL_POSITION(id).xyz - p)) > g.spacing) continue;
            // By distance to the texel's own centre (the centroid of its surface in the cell), not the cell grid's
            // trilinear share, and by its area there (its samples, four for a cell a flat face crosses whole): a cell
            // that catches only a sliver of a wall at an inside corner holds a texel lit like the corner, and at full
            // trilinear weight it dotted the Cornell box's corners at every cell.
            vec4 tp = TEXEL_POSITION(id);
            vec3 dp = (tp.xyz - p) / g.spacing;
            float w = exp(-dot(dp, dp) / (2.0 * 0.36)) * min(tp.w / 4.0, 1.0);
            float f2 = facing * facing;
            ids[slot] = id;
            weights[slot] = w * f2 * f2;
        }
    }
}

// Whether a texel has rays enough (adaptive, texel_mark.comp): at least minRays; then until the standard error of
// its mean -- from how its 32-ray passes disagree (m2: the running mean of each pass's luminance squared) -- is under
// tol of its value (or of floorAbs, for the near-black), or it reaches maxRays. A fixed 1024 left bounce-lit corners
// pointillist: their light comes from small bright patches a ray either finds or misses, so they need many times the
// rays a sunlit wall does.
bool texelSettled(vec4 light, float m2, vec4 noise, float minRays) {
    if (light.w < minRays) return false;
    if (light.w >= noise.x) return true;
    float mean = dot(light.rgb, vec3(0.2126, 0.7152, 0.0722));
    float variance = max(m2 - mean * mean, 0.0);
    float standardError = sqrt(variance / (light.w / 32.0));
    return standardError <= noise.y * max(mean, noise.z);
}

#endif
