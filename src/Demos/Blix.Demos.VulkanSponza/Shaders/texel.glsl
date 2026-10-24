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
// The includer defines TEXEL_HASH(i) (the hash table's slot i, three words of TexelHash: uvec3 key x, key y, texel index; slots by modulo) and
// TEXEL_POSITION(i) / TEXEL_NORMAL(i) (a texel's vec4s: xyz its position, w its surface samples; xyz its mean face
// normal, w its level).
#ifndef TEXEL_GLSL
#define TEXEL_GLSL

#define TEXEL_EMPTY 0xFFFFFFFFu
#define TEXEL_PROBES 64

struct TexelGrid {
    vec3 minCorner;   // the cells' origin (cell = floor((p - minCorner) / spacing))
    float spacing;    // this level's: the finest's x 2^level
    uint capacity;    // hash slots, a power of two
    uint level;       // 0 the finest; each level's cells are twice the last's, nested (SponzaLoop.Texels.cs)
};

// The grid of another level: the same origin, cells 2^level times the finest's.
TexelGrid texelLevelGrid(TexelGrid finest, uint level) {
    TexelGrid g = finest;
    g.spacing = finest.spacing * float(1u << level);
    g.level = level;
    return g;
}

#include "texel_bin.glsl"

// A cell's frame for its detail (texel_nodes_shade.glsl writes, texel_compose.comp and incident_clipmap.frag read): its
// normal and two tangents, built as the incident pass tilts the normal, so the cook's direction and the pixel's agree.
void texelDetailFrame(vec3 n, out vec3 nn, out vec3 t1, out vec3 t2) {
    nn = n;
    t1 = normalize(cross(abs(n.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), n));
    t2 = cross(n, t1);
}

// A cell's detail word: x, y its light's relative change as the normal tilts toward t1, t2 (snorm8, over +-2); z its sky
// visibility (unorm8); w 255 when present (0: none -- a cook from before it, or not landed).
uint texelDetailPack(vec2 tilt, float skyVisibility) {
    vec2 t = clamp(tilt * 0.5, vec2(-1.0), vec2(1.0));
    uvec2 q = uvec2(ivec2(round(t * 127.0)) & 0xFF);
    return q.x | (q.y << 8) | (uint(round(sqrt(clamp(skyVisibility, 0.0, 1.0)) * 255.0)) << 16) | (255u << 24);
}

vec3 texelDetailUnpack(uint w) {
    ivec2 q = ivec2(int(w << 24) >> 24, int(w << 16) >> 24);
    // The sky visibility by its square root: linear, a byte's step was 20% of a dark crease's 0.02 and showed as texels.
    float sky = float((w >> 16) & 0xFFu) / 255.0;
    return vec3(vec2(q) / 127.0 * 2.0, sky * sky);
}

uvec2 texelKey(ivec3 cell, uint bin, uint level) {
    return uvec2(uint(cell.x) | (uint(cell.y) << 16), uint(cell.z) | (bin << 16) | (level << 21));
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
    uvec2 key = texelKey(cell, bin, g.level);
    uint slot = texelMix(key) % g.capacity;
    for (int i = 0; i < TEXEL_PROBES; ++i) {
        uvec3 e = TEXEL_HASH(slot);
        if (e.x == TEXEL_EMPTY) return -1;
        if (e.x == key.x && e.y == key.y) return int(e.z);
        slot = slot + 1u == g.capacity ? 0u : slot + 1u;
    }
    return -1;
}

// The level a pixel reads (fractional: it blends with the next coarser): the one whose texel spans about `pixels`
// pixels at viewDepth, from levels.y the pixel's width at unit depth; levels.x levels in all. `sunlit` (0-1, the
// pixel's own direct sun) picks between levels.w pixels in shade and levels.z in sun: in sun the direct light and its
// shadows carry the detail; in shade the bounce light is all there is, and coarse texels there flattened the folds of
// bounce-lit cloth into blocks (the user: "looses a lot of detail and becomes really choppy").
float texelLevelFor(float viewDepth, vec4 levels, float finestSpacing, float sunlit) {
    float want = mix(levels.w, levels.z, clamp(sunlit, 0.0, 1.0)) * viewDepth * levels.y;
    // levels.x's fraction x 8: the finest level read (--live-texels' liveMinLevel: the finer ones are not gathered).
    float levelCount = floor(levels.x);
    return clamp(log2(max(want / finestSpacing, 1e-6)), floor(fract(levels.x) * 8.0 + 0.5), levelCount - 1.0);
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
            float w = exp(-dot(dp, dp) / (2.0 * 0.36)) * min(tp.w / (4.0 * float(1u << (2u * g.level))), 1.0);
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

#ifdef TEXEL_INDEX_MAP
// ---- Lightmap addressing (--lightmap): a pixel with an atlas texel reads the atlas's texels, not hashed cells --------
// The pre-pass writes each pixel's atlas texel packed (depth_prepass.frag): u and v in eighths of a texel, 16 bits each,
// 0 where the surface has none. TEXEL_INDEX_MAP(i) (the includer's) is the atlas's map from texel (y * width + x) to its
// record, 0xFFFFFFFF where no surface covers it.
bool texelLightmapUnpack(uint packedTexel, out vec2 texel) {
    texel = vec2(float(packedTexel & 0xFFFFu), float(packedTexel >> 16)) / 8.0;
    return packedTexel != 0u;
}

// The four texels a bilinear lookup at `texel` reads (atlas units: texel centres at +0.5), with their weights, on the
// side `n` faces; -1 where the atlas has nothing (outside every chart, between charts).
void texelsAroundLightmap(vec2 texel, vec3 n, ivec2 atlasSize, out int ids[4], out float weights[4]) {
    vec2 f = texel - 0.5;
    ivec2 base = ivec2(floor(f));
    vec2 t = f - vec2(base);
    // A bilinear neighbour facing another way is another surface (records carry their face normal). The gutter around a
    // chart points each empty texel at the first filled neighbour it finds (SponzaLoop.Lightmap.cs), which can lie in the
    // next chart over -- around a column's corner, the face lit from another side: lines of the wrong light along the
    // rectangular columns' arrises (the user, 2026-10-14). Against the pixel's VERTEX normal instead, a smoothed edge
    // dropped its own texels and lit the edges from the probes (worse); face against face, a flat face agrees exactly.
    // The reference: of the own texel and the four around it, the record facing most as the pixel does (n). The own
    // texel alone was wrong where it straddles an arris -- its one record is one face's, and the other face's pixels in
    // it took that face's light, a texel staircase along columns' and arches' edges at any LOD (2026-10-14). n turns
    // smoothly, so the switch follows a smooth line; the best match is one of the five, so the pixel keeps its texels.
    vec3 ownNormal = vec3(0.0);
    float bestDot = -2.0;
    {
        for (int i = 0; i < 5; ++i) {
            ivec2 at = i == 4 ? ivec2(floor(texel)) : base + ivec2(i & 1, i >> 1);
            if (any(lessThan(at, ivec2(0))) || any(greaterThanEqual(at, atlasSize))) continue;
            uint oid = TEXEL_INDEX_MAP(at.y * atlasSize.x + at.x);
            if (oid == 0xFFFFFFFFu) continue;
            if (TEXEL_POSITION(oid).w < 0.0 && dot(TEXEL_NORMAL(oid).xyz, n) < 0.0) oid += 1u;
            if (abs(TEXEL_POSITION(oid).w) < 2.0) continue;
            vec3 cn = TEXEL_NORMAL(oid).xyz;
            float cl = length(cn);
            if (cl < 1e-6) continue;
            float d = dot(cn / cl, n);
            if (d > bestDot) { bestDot = d; ownNormal = cn; }
        }
    }
    // Even the best of the five turned more than 25 degrees from the pixel: a coarse level drawn flat over a joint its
    // simplification collapsed, the texels under it the groove's walls (lightmap_frame.glsl drops their frame too). The
    // ring of twelve around instead -- the faces either side of the groove, in the same chart -- the nearest four facing
    // as the pixel does; none there, the five's best as before.
    if (bestDot > -2.0 && bestDot < 0.9) {
        int found = 0;
        for (int k = 0; k < 16 && found < 4; ++k) {
            ivec2 o = ivec2(k & 3, k >> 2) - 1;
            if (o.x >= 0 && o.x <= 1 && o.y >= 0 && o.y <= 1) continue;
            ivec2 at = base + o;
            if (any(lessThan(at, ivec2(0))) || any(greaterThanEqual(at, atlasSize))) continue;
            uint id = TEXEL_INDEX_MAP(at.y * atlasSize.x + at.x);
            if (id == 0xFFFFFFFFu) continue;
            if (TEXEL_POSITION(id).w < 0.0 && dot(TEXEL_NORMAL(id).xyz, n) < 0.0) id += 1u;
            if (abs(TEXEL_POSITION(id).w) < 2.0) continue;
            vec3 tn = TEXEL_NORMAL(id).xyz;
            if (dot(tn, n) < 0.9 * length(tn)) continue;
            vec2 d = vec2(at) + 0.5 - texel;
            ids[found] = int(id);
            weights[found] = 1.0 / (1.0 + dot(d, d));
            found++;
        }
        if (found > 0) {
            for (int i = found; i < 4; ++i) { ids[i] = -1; weights[i] = 0.0; }
            return;
        }
    }
    float ownLength = length(ownNormal);
    for (int i = 0; i < 4; ++i) {
        ivec2 o = ivec2(i & 1, i >> 1);
        ivec2 at = base + o;
        ids[i] = -1;
        weights[i] = 0.0;
        if (any(lessThan(at, ivec2(0))) || any(greaterThanEqual(at, atlasSize))) continue;
        uint id = TEXEL_INDEX_MAP(at.y * atlasSize.x + at.x);
        if (id == 0xFFFFFFFFu) continue;
        // A double-sided surface's texel is a pair (position w < 0): the front's record, then the back's. The side
        // a pixel shows is the one its normal agrees with.
        if (TEXEL_POSITION(id).w < 0.0 && dot(TEXEL_NORMAL(id).xyz, n) < 0.0) id += 1u;
        // |w| 1: buried in a solid (the bake's test), never gathered; its neighbours answer.
        if (abs(TEXEL_POSITION(id).w) < 2.0) continue;
        vec3 tn = TEXEL_NORMAL(id).xyz;
        if (ownLength > 1e-6 && dot(tn, ownNormal) < 0.9 * length(tn) * ownLength) continue;
        ids[i] = int(id);
        // A floor under the bilinear weight: the reference can be a texel the bilinear gives nothing (the pixel on another
        // texel's centre), and the one texel it does weigh can face the other way -- every weight then 0, the lookup
        // "found nothing", and the pixel took the probes' light instead: bright dashes along the vaults' creases
        // (2026-10-15: texels at 0.03, the pixels at 0.6-0.7).
        weights[i] = max((o.x == 1 ? t.x : 1.0 - t.x) * (o.y == 1 ? t.y : 1.0 - t.y), 1.0 / 64.0);
    }
}

#ifdef TEXEL_ATLAS_GATHER
// The same four from the index map's words (lightmap_index.glsl; a texture gather before) and a 4-byte meta word a
// record (TEXEL_META(id), SponzaLoop.LightmapGather.cs) for everything the choice reads: bits 0-23 the face normal
// (octahedral, 12 + 12 bits), 29 the normal is not zero, 30 a double-sided pair's front (the back is id + 1), 31 not
// buried. ~35 dependent loads a pixel -> one gather and ~8 (2026-10-20). False where the full lookup must answer: the
// groove ring (the best of the four turned past 25 degrees) and the atlas's edge.
vec3 texelMetaNormal(uint m) {
    vec2 e = vec2(float(m & 0xFFFu), float((m >> 12) & 0xFFFu)) / 4095.0 * 2.0 - 1.0;
    vec3 v = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    if (v.z < 0.0) v.xy = (1.0 - abs(v.yx)) * vec2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
    return normalize(v);
}

bool texelsAroundLightmapGather(vec2 texel, vec3 n, ivec2 atlasSize, out int ids[4], out float weights[4]) {
    vec2 f = texel - 0.5;
    ivec2 base = ivec2(floor(f));
    vec2 t = f - vec2(base);
    for (int i = 0; i < 4; ++i) { ids[i] = -1; weights[i] = 0.0; }
    if (any(lessThan(base, ivec2(0))) || any(greaterThanEqual(base + 1, atlasSize))) return false;
    // The four as i = o.x + 2 o.y below (lightmap_index.glsl: one tile's words for most).
    uint raw[4] = uint[4](lightmapIndexAt(base), lightmapIndexAt(base + ivec2(1, 0)), lightmapIndexAt(base + ivec2(0, 1)), lightmapIndexAt(base + ivec2(1, 1)));
    uint id[4];
    uint meta[4];
    vec3 normals[4];
    float bestDot = -2.0;
    vec3 ownNormal = vec3(0.0);
    for (int i = 0; i < 4; ++i) {
        id[i] = raw[i];
        meta[i] = 0u;
        normals[i] = vec3(0.0);
        if (raw[i] == 0xFFFFFFFFu) continue;
        uint m = TEXEL_META(raw[i]);
        // A double-sided pair: the side the pixel's normal agrees with.
        if ((m & 0x40000000u) != 0u && dot(texelMetaNormal(m), n) < 0.0) { id[i] = raw[i] + 1u; m = TEXEL_META(id[i]); }
        meta[i] = m;
        if ((m & 0x80000000u) == 0u) continue;   // buried
        if ((m & 0x20000000u) == 0u) continue;   // no normal: not a reference
        normals[i] = texelMetaNormal(m);
        float d = dot(normals[i], n);
        if (d > bestDot) { bestDot = d; ownNormal = normals[i]; }
    }
    if (bestDot > -2.0 && bestDot < 0.9) return false;
    for (int i = 0; i < 4; ++i) {
        uint m = meta[i];
        if (raw[i] == 0xFFFFFFFFu || (m & 0x80000000u) == 0u) continue;
        if (bestDot > -2.0 && (m & 0x20000000u) != 0u && dot(normals[i], ownNormal) < 0.9) continue;
        ivec2 o = ivec2(i & 1, i >> 1);
        ids[i] = int(id[i]);
        weights[i] = max((o.x == 1 ? t.x : 1.0 - t.x) * (o.y == 1 ? t.y : 1.0 - t.y), 1.0 / 64.0);
    }
    return true;
}
#endif // TEXEL_ATLAS_GATHER

#endif // TEXEL_INDEX_MAP

#endif
