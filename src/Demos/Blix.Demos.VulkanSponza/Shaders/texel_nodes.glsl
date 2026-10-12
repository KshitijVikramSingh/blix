// Sun nodes (SponzaLoop.SunNodes.cs): the cook's hash over every live texel, and the texels around a point. The
// includer declares `texels` (Texels: two vec4s a texel, as texel.glsl's TEXEL_POSITION / TEXEL_NORMAL) and
// `cookHash` (SunNodeHash: a texel id a slot).
#ifndef TEXEL_NODES_GLSL
#define TEXEL_NODES_GLSL

#define TEXEL_POSITION(i) texels[2 * (i)]
#define TEXEL_NORMAL(i) texels[2 * (i) + 1]
#define TEXEL_HASH(i) uvec4(0xFFFFFFFFu)   // texel.glsl's own hash is not read here
#include "texel.glsl"

#define NODE_EMPTY 0xFFFFFFFFu
#define NODE_HASH_PROBES 64
#define NODE_CANDIDATES 8

// A texel's key in the cook's hash: its position's finest cell and its normal's bin (level 0).
bool nodeTexelKey(uint id, vec4 grid, out uvec2 key) {
    vec3 n = texels[2u * id + 1u].xyz;
    key = uvec2(0u);
    if (dot(n, n) < 1e-12) return false;
    ivec3 cell = ivec3(floor((texels[2u * id].xyz - grid.xyz) / grid.w));
    if (any(lessThan(cell, ivec3(0))) || any(greaterThan(cell, ivec3(65535)))) return false;
    key = texelKey(cell, texelBin(normalize(n)), 0u);
    return true;
}

#ifdef NODE_LOOKUP
// Two sets share the lookup: 0 the live texels (Texels, SunNodeHash), 1 the foliage's sun points (FoliagePoints,
// FoliageHash: SponzaLoop.SunNodesFoliage.cs) -- the includer declares both.
vec4 nodePosition(int set, uint id) { return set == 0 ? texels[2u * id] : foliagePoints[2u * id]; }
vec4 nodeNormal(int set, uint id) { return set == 0 ? texels[2u * id + 1u] : foliagePoints[2u * id + 1u]; }
uint nodeSlot(int set, uint slot) { return set == 0 ? cookHash[slot] : foliageHash[slot]; }

bool nodeKeyOf(int set, uint id, vec4 grid, out uvec2 key) {
    vec3 n = nodeNormal(set, id).xyz;
    key = uvec2(0u);
    if (dot(n, n) < 1e-12) return false;
    ivec3 cell = ivec3(floor((nodePosition(set, id).xyz - grid.xyz) / grid.w));
    if (any(lessThan(cell, ivec3(0))) || any(greaterThan(cell, ivec3(65535)))) return false;
    key = texelKey(cell, texelBin(normalize(n)), 0u);
    return true;
}

int nodeFind(int set, ivec3 cell, uint bin, vec4 grid, uint slots) {
    if (any(lessThan(cell, ivec3(0))) || any(greaterThan(cell, ivec3(65535)))) return -1;
    uvec2 key = texelKey(cell, bin, 0u);
    uint slot = texelMix(key) & (slots - 1u);
    for (int probe = 0; probe < NODE_HASH_PROBES; ++probe) {
        uint e = nodeSlot(set, slot);
        if (e == NODE_EMPTY) return -1;
        uvec2 k;
        if (nodeKeyOf(set, e, grid, k) && k == key) return int(e);
        slot = (slot + 1u) & (slots - 1u);
    }
    return -1;
}

// What a point on a surface lies among in a set: the eight cells around it in its normal's bin (and, without any, in
// the bin across the nearest boundary), weighed as texelsAround weighs them (distance to the centre; the area, |w|: an
// atlas texel's and a foliage point's 4, -4 double-sided; the facing^4; dropped past a cell from the plane). Returns the
// summed weight (0: none found).
// lookup: x how far (cells) a candidate's plane may lie from the point, y the distance weight's spread (cells).
float nodeSetAt(int set, vec3 p, vec3 n, vec4 grid, uint slots, vec2 lookup, out int ids[NODE_CANDIDATES], out float weights[NODE_CANDIDATES]) {
    vec3 f = (p - grid.xyz) / grid.w - 0.5;
    ivec3 base = ivec3(floor(f));
    float total = 0.0;
    uint bins[2] = uint[2](texelBin(n), texelBinAlternate(n));
    for (int i = 0; i < NODE_CANDIDATES; ++i) { ids[i] = -1; weights[i] = 0.0; }
    for (int b = 0; b < 2 && total <= 0.0; ++b) {
        if (b == 1 && bins[1] == bins[0]) break;
        for (int i = 0; i < NODE_CANDIDATES; ++i) {
            int id = nodeFind(set, base + ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1), bins[b], grid, slots);
            if (id < 0) continue;
            vec4 tp = nodePosition(set, uint(id));
            vec3 tn = normalize(nodeNormal(set, uint(id)).xyz);
            float facing = dot(tn, n);
            if (facing <= 0.0 || abs(dot(tn, tp.xyz - p)) > lookup.x * grid.w) continue;
            vec3 dp = (tp.xyz - p) / grid.w;
            float f2 = facing * facing;
            ids[i] = id;
            weights[i] = exp(-dot(dp, dp) / (2.0 * lookup.y * lookup.y)) * min(abs(tp.w) / 4.0, 1.0) * f2 * f2;
            total += weights[i];
        }
    }
    return total;
}
#endif

#endif
