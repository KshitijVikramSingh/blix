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
int nodeFind(ivec3 cell, uint bin, vec4 grid, uint slots) {
    if (any(lessThan(cell, ivec3(0))) || any(greaterThan(cell, ivec3(65535)))) return -1;
    uvec2 key = texelKey(cell, bin, 0u);
    uint slot = texelMix(key) & (slots - 1u);
    for (int probe = 0; probe < NODE_HASH_PROBES; ++probe) {
        uint e = cookHash[slot];
        if (e == NODE_EMPTY) return -1;
        uvec2 k;
        if (nodeTexelKey(e, grid, k) && k == key) return int(e);
        slot = (slot + 1u) & (slots - 1u);
    }
    return -1;
}

// The texels a point on a surface lies among: the eight cells around it in its normal's bin (and, without any, in the
// bin across the nearest boundary), weighed as texelsAround weighs them (distance to the texel's centre; its area,
// |w|: an atlas texel's is 4, -4 double-sided; its facing^4; dropped past a cell from its plane). Returns the summed
// weight (0: none found).
float nodeTexelsAt(vec3 p, vec3 n, vec4 grid, uint slots, out int ids[NODE_CANDIDATES], out float weights[NODE_CANDIDATES]) {
    vec3 f = (p - grid.xyz) / grid.w - 0.5;
    ivec3 base = ivec3(floor(f));
    float total = 0.0;
    uint bins[2] = uint[2](texelBin(n), texelBinAlternate(n));
    for (int b = 0; b < 2 && total <= 0.0; ++b) {
        if (b == 1 && bins[1] == bins[0]) break;
        for (int i = 0; i < NODE_CANDIDATES; ++i) {
            ids[i] = -1;
            weights[i] = 0.0;
            int id = nodeFind(base + ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1), bins[b], grid, slots);
            if (id < 0) continue;
            vec4 tp = texels[2 * id];
            vec3 tn = normalize(texels[2 * id + 1].xyz);
            float facing = dot(tn, n);
            if (facing <= 0.0 || abs(dot(tn, tp.xyz - p)) > grid.w) continue;
            vec3 dp = (tp.xyz - p) / grid.w;
            float f2 = facing * facing;
            ids[i] = id;
            weights[i] = exp(-dot(dp, dp) / (2.0 * 0.36)) * min(abs(tp.w) / 4.0, 1.0) * f2 * f2;
            total += weights[i];
        }
    }
    return total;
}
#endif

#endif
