// Sun nodes (SponzaLoop.SunNodes.cs): the cook's hash over every live texel, and the texels around a point. The
// includer declares `texels` (Texels: two vec4s a texel, as texel.glsl's TEXEL_POSITION / TEXEL_NORMAL) and
// `cookHash` (SunNodeHash: a texel id a slot).
#ifndef TEXEL_NODES_GLSL
#define TEXEL_NODES_GLSL

#include "texel_record.glsl"
#define TEXEL_HASH(i) uvec4(0xFFFFFFFFu)   // texel.glsl's own hash is not read here
#include "texel.glsl"

#define NODE_EMPTY 0xFFFFFFFFu
#define NODE_HASH_PROBES 64
#define NODE_CANDIDATES 8

// The cook's hash (SunNodeHash) holds two words a slot: the texel id, and its key's fingerprint, so a probe passes a
// slot by comparing one word -- it had worked the slot's texel's key out again from its position and normal (two
// scattered reads and a bin) on every probe, ~30 a hit: most of the shade's cost (2026-10-14). A matching fingerprint is
// confirmed against the texel, so the answer is the same.
uint nodeFingerprint(uvec2 key) { return key.x * 0x2C1B3C6Du ^ (key.y + 0x9E3779B9u) * 0x297A2D39u; }

// A texel's key in the cook's hash: its position's finest cell and its normal's bin (level 0).
// A texel's plane within its key: its place along its bin's direction, in steps of `step` (the search's plane reach) --
// so a key holds one texel a plane (texel_nodes_prep.comp), and two surfaces facing the same way within a cell both
// keep one. step 0: one a key. A sheet's side (w < 0: a pair, a double-sided point) keeps a plane of its own beside an
// opaque surface's: the teacup's saucer foot stands on the floor, its contact face's inside lies in the floor's plane,
// and with the smaller id it took the floor's entries -- the floor's hits under the saucer read the sheet, the crease
// beside the foot 2-7x its path trace (2026-10-24).
int nodePlane(uint id, float step) {
    if (step <= 0.0) return 0;
    vec3 d = normalize(clamp(floor(normalize(TEXEL_NORMAL(id).xyz) * 1.5 + 0.5), vec3(-1.0), vec3(1.0)));
    return 2 * int(floor(dot(d, TEXEL_POSITION(id).xyz) / step)) + (TEXEL_POSITION(id).w < 0.0 ? 1 : 0);
}

bool nodeTexelKey(uint id, vec4 grid, out uvec2 key) {
    vec3 n = TEXEL_NORMAL(id).xyz;
    key = uvec2(0u);
    if (dot(n, n) < 1e-12) return false;
    ivec3 cell = ivec3(floor((TEXEL_POSITION(id).xyz - grid.xyz) / grid.w));
    if (any(lessThan(cell, ivec3(0))) || any(greaterThan(cell, ivec3(65535)))) return false;
    key = texelKey(cell, texelBin(normalize(n)), 0u);
    return true;
}

#ifdef NODE_LOOKUP
// Two sets share the lookup: 0 the live texels (Texels, SunNodeHash), 1 the foliage's sun points (FoliagePoints,
// FoliageHash: SponzaLoop.SunNodesFoliage.cs) -- the includer declares both.
vec4 nodePosition(int set, uint id) { return set == 0 ? TEXEL_POSITION(id) : foliagePoints[2u * id]; }
vec4 nodeNormal(int set, uint id) { return set == 0 ? TEXEL_NORMAL(id) : foliagePoints[2u * id + 1u]; }
uint nodeSlot(int set, uint slot) { return set == 0 ? cookHash[2u * slot] : foliageHash[slot]; }

bool nodeKeyOf(int set, uint id, vec4 grid, out uvec2 key) {
    vec3 n = nodeNormal(set, id).xyz;
    key = uvec2(0u);
    if (dot(n, n) < 1e-12) return false;
    ivec3 cell = ivec3(floor((nodePosition(set, id).xyz - grid.xyz) / grid.w));
    if (any(lessThan(cell, ivec3(0))) || any(greaterThan(cell, ivec3(65535)))) return false;
    key = texelKey(cell, texelBin(normalize(n)), 0u);
    return true;
}

// The key's entries from `slot` on, one a call: the next one's id, -1 at the chain's end (a key holds one texel a
// plane, texel_nodes_prep.comp).
int nodeNext(int set, uvec2 key, uint fingerprint, vec4 grid, uint slots, inout uint slot, inout int probe) {
    while (probe < NODE_HASH_PROBES) {
        uint e = nodeSlot(set, slot);
        if (e == NODE_EMPTY) { probe = NODE_HASH_PROBES; return -1; }
        uint at = slot;
        slot = slot + 1u == slots ? 0u : slot + 1u;
        ++probe;
        uvec2 k;
        if ((set != 0 || cookHash[2u * at + 1u] == fingerprint) && nodeKeyOf(set, e, grid, k) && k == key) return int(e);
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
            ivec3 cell = base + ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
            if (any(lessThan(cell, ivec3(0))) || any(greaterThan(cell, ivec3(65535)))) continue;
            uvec2 key = texelKey(cell, bins[b], 0u);
            uint fingerprint = nodeFingerprint(key);
            uint slot = texelMix(key) % slots;
            int probe = 0;
            float nearestPlane = 1e30;
            // The cell's texels, one a plane: the best weighed. One a cell hid a surface beside another facing the same
            // way -- the teacup's floor under its saucer's bottom (1.9 cm up, past the plane's reach): the saucer's texel
            // kept the cell, and the floor's hits found none and took the nearest patch anywhere, the sunlit one at the
            // rim -- the gap 7x its path trace (2026-10-23).
            for (int id = nodeNext(set, key, fingerprint, grid, slots, slot, probe); id >= 0; id = nodeNext(set, key, fingerprint, grid, slots, slot, probe)) {
                vec4 tp = nodePosition(set, uint(id));
                // A buried texel (|w| 1: inside a solid, SponzaLoop.Lightmap.cs) answers nothing: its sun is none and the
                // patch it "sees" was found from inside the wall -- at a crease it lay nearest a hit and lent the hit the
                // sky-lit outside: the crevices' box joints glowed (2026-10-16).
                if (set == 0 && abs(tp.w) < 2.0) continue;
                vec3 tn = normalize(nodeNormal(set, uint(id)).xyz);
                float facing = dot(tn, n);
                float plane = abs(dot(tn, tp.xyz - p));
                if (facing <= 0.0 || plane > lookup.x * grid.w) continue;
                vec3 dp = (tp.xyz - p) / grid.w;
                float f2 = facing * facing;
                float weight = exp(-dot(dp, dp) / (2.0 * lookup.y * lookup.y)) * min(abs(tp.w) / 4.0, 1.0) * f2 * f2;
                // The entry on the hit's own surface first (its plane nearest, by a millimetre), then the weight: a 4 mm
                // wall's two faces turned the same way share the cell, and the sheet shell's floor read its top wall's
                // underside through the bottom wall, 0.77 of its path trace (2026-10-24).
                bool nearer = plane < nearestPlane - 1e-3;
                if (nearer || (plane < nearestPlane + 1e-3 && weight > weights[i])) { ids[i] = id; weights[i] = weight; nearestPlane = min(nearestPlane, plane); }
            }
            total += weights[i];
        }
    }
    return total;
}
#endif

#endif
