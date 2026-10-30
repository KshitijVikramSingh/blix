// RTGI's cooked cell set (SponzaLoop.RtgiCook.cs, rtgi_cook.comp): the cache's cells cooked from the geometry alone, by
// the census's rules (SponzaLoop.RtgiCensus.cs) -- a size field sampled by area over both sides of every surface, each
// side kept or dropped by its own samples, every surface point's cell at the level its size asks for and every level
// above it. Per cooked cell (keyed as the cache keys it, by coordinates at a level and side, without the region): one
// point on its own surface piece, its normal, its flags, and its 8 neighbours across the surface at its level that a ray
// from its point reached -- traced once at cook, the same for every pixel (the readout's neighbour test, the blend's
// reach). Beside it:
//   - the size table: the cache's cell size by position (cookScaleAt; rtgi_cache.glsl's rtgiCellSize asks it first), 5 cm
//     cubes where the size is under the fine tier's bound, COOK_COUNTS.w cubes elsewhere -- the size the cells were
//     enumerated with, so the cache's cells are the cooked ones wherever the cook covered;
//   - the regions: within each cube of the first NEAR_LEVELS levels, the level-0 cells joined by clear neighbour rays
//     are one piece, every piece but the cube's lowest a region of its own (rtgi_cache.glsl's rtgiRegion: the two sides of
//     a thin wall, a floor running under it, kept apart);
//   - the near field of the level-0 junction cells (the first COOK_COUNTS.y cells): NEAR_RAYS fixed links each to what
//     lies within the contact radius, relit each frame (rtgi_nearcook_update.comp), read over the cache's far light.
// Cells are ordered level 0's live junction cells first (the near field's), then level 0's others (COOK_COUNTS.x level-0
// cells in all), then level 1 up.
//
// The includer declares COOK_PARAMS (vec4: x the finest cell (m); y the cell table's slots - 1; z the cells; w 1 ready),
// COOK_COUNTS (vec4: x the level-0 cells; y the near field's cells; z the size table's slots - 1; w the coarse cube (m)),
// COOK_TABLE(i) (uvec2: key, cell) and COOK_POINTS(i) (vec4, two a cell: the point (xyz) and its bits (w: side, flags,
// level); the normal (xyz) and the neighbour mask (w, bits)); and, where it reads them, COOK_SCALE(i) (uvec2: key, size's
// bits) and COOK_REGIONS(i) (uint: a level-0 cell's region at each of NEAR_LEVELS levels).
#ifndef RTGI_COOK_GLSL
#define RTGI_COOK_GLSL

#ifndef RTGI_ORIGIN_DEFINED
#define RTGI_ORIGIN_DEFINED
// Every cell grid's origin (the cache's at every level, the cooked cells') for a side: the round coordinates along the
// surface, and RTGI_KEY_INSET back along its normal (rtgi_cache.glsl says why).
#define RTGI_KEY_INSET 0.0213
vec3 rtgiOrigin(uint side) {
    vec3 o = vec3(0.0);
    o[side >> 1u] = (side & 1u) == 0u ? RTGI_KEY_INSET : -RTGI_KEY_INSET;
    return o;
}
// The size table is by position alone, any side: off the round coordinates on every axis, so no surface lies on its
// boundaries.
#define NEAR_SCALE_ORIGIN vec3(0.0137, 0.0271, 0.0419)
#endif

// Linear probing's longest run (SponzaLoop.RtgiCook.cs's CookProbeSteps): a lookup stops at the first empty slot.
#define COOK_PROBE_STEPS 64u
#define COOK_NONE 0xFFFFFFFFu
// A cell's point's w (bits): its side (0-2); NEAR_BURIED, inside something (every candidate of a junction cell met back
// faces: a floor's cell under the wall standing on it) -- left out of every lookup and every training point;
// COOK_JUNCTION, another surface within reach of its point (the inside test's and the near field's cells); COOK_OWN, its
// surface's own level (else an ancestor of finer cells); the level, bits 8-11.
#define NEAR_BURIED 16u
#define COOK_JUNCTION 32u
#define COOK_OWN 64u
// The neighbour mask's (the normal's w) bit 8: traced (its bits 0-7 the ring's neighbours reached).
#define COOK_ADJ_TRACED 256u
// The near field's rays a cell.
#define NEAR_RAYS 32u
// The cache levels whose cubes the regions are found in (5 cm to 1.6 m); coarser, region 0.
#define NEAR_LEVELS 6u

uint cookHashU(uint x) {
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

// The cell (c, level, side)'s two hashes: x the slot it starts probing at, y its key (never 0). SponzaLoop.RtgiCook.cs's
// CookCellHash is the same. The size table's cubes are keyed alike, level 15 and side 7 (fine) or 6 (coarse).
uvec2 cookCellHash(ivec3 c, uint level, uint side, uint mask) {
    uint a = cookHashU(uint(c.x) * 0x9E3779B1u ^ cookHashU(uint(c.y) * 0x85EBCA77u ^ cookHashU(uint(c.z) * 0xC2B2AE3Du ^ (level * 8u + side + 0x3A1u))));
    uint k = cookHashU(a ^ 0x2545F491u ^ uint(c.x) * 0x1B873593u ^ uint(c.y) * 0xCC9E2D51u ^ uint(c.z) * 0x27D4EB2Fu) | 1u;
    return uvec2(a & mask, k);
}

uint cookFind(ivec3 c, uint level, uint side) {
    if (COOK_PARAMS.w < 0.5) return COOK_NONE;
    uvec2 h = cookCellHash(c, level, side, uint(COOK_PARAMS.y));
    for (uint k = 0u; k < COOK_PROBE_STEPS; k++) {
        uvec2 e = COOK_TABLE((h.x + k) & uint(COOK_PARAMS.y));
        if (e.x == h.y) return e.y;
        if (e.x == 0u) break;
    }
    return COOK_NONE;
}

float cookSize(uint level) { return COOK_PARAMS.x * exp2(float(level)); }
ivec3 cookCoords(vec3 p, uint side, uint level) { return ivec3(floor((p - rtgiOrigin(side)) / cookSize(level))); }
uint cookBits(uint i) { return floatBitsToUint(COOK_POINTS(2u * i).w); }
bool cookLive(uint i) { return (cookBits(i) & NEAR_BURIED) == 0u; }
uint cookAdj(uint i) { return floatBitsToUint(COOK_POINTS(2u * i + 1u).w); }
// The cooked cell holding p at a level (its side's), live, or COOK_NONE.
uint cookCellAt(vec3 p, uint side, uint level) {
    uint i = cookFind(cookCoords(p, side, level), level, side);
    return i != COOK_NONE && cookLive(i) ? i : COOK_NONE;
}

// A cell's 8 neighbours across its surface (along the side's two other axes, a0 = axis + 1 and a1 = axis + 2, mod 3),
// ordered so a neighbour's way back is k ^ 1; the neighbour mask's bit k (rtgi_cook.comp).
const ivec2 NEAR_RING[8] = ivec2[8](ivec2(1, 0), ivec2(-1, 0), ivec2(0, 1), ivec2(0, -1), ivec2(1, 1), ivec2(-1, -1), ivec2(1, -1), ivec2(-1, 1));
uint cookRing(int d0, int d1) {
    for (uint k = 0u; k < 8u; k++) if (NEAR_RING[k] == ivec2(d0, d1)) return k;
    return 8u;
}
// Whether cooked cell own reaches its neighbour (d0, d1) cells away along a0, a1 (each -1 to 1): itself always; its
// neighbour where the ray between their points was clear. Not cooked (COOK_NONE) or its rays not traced yet: true -- as
// before the cook, every neighbour.
bool cookReaches(uint own, int d0, int d1) {
    if (own == COOK_NONE || (d0 == 0 && d1 == 0)) return true;
    uint adj = cookAdj(own);
    if ((adj & COOK_ADJ_TRACED) == 0u) return true;
    uint k = cookRing(d0, d1);
    return k < 8u && (adj & (1u << k)) != 0u;
}

#ifndef RTGI_REGION_DECLARED
#define RTGI_REGION_DECLARED
uint rtgiRegion = 0u;
#endif

// The point's level-0 cooked cell, 1 + its index (0 none): its own -- or, that one missing or buried (a cell straddling a
// junction, a sliver of it above the floor), the next toward the visible surface (from the cell's middle toward the
// point, along the surface, one cell).
uint nearRegion(vec3 p, uint side) {
    if (COOK_PARAMS.w < 0.5) return 0u;
    float size = COOK_PARAMS.x;
    ivec3 c = ivec3(floor((p - rtgiOrigin(side)) / size));
    uint j = cookFind(c, 0u, side);
    if (j != COOK_NONE && cookLive(j)) return j + 1u;
    uint axis = side >> 1u, a0 = (axis + 1u) % 3u, a1 = (axis + 2u) % 3u;
    vec3 off = (p - rtgiOrigin(side)) / size - (vec3(c) + 0.5);
    ivec3 cc = c;
    if (abs(off[a0]) >= abs(off[a1])) cc[a0] += off[a0] >= 0.0 ? 1 : -1;
    else cc[a1] += off[a1] >= 0.0 ? 1 : -1;
    uint k = cookFind(cc, 0u, side);
    if (k != COOK_NONE && cookLive(k)) return k + 1u;
    return j != COOK_NONE ? j + 1u : 0u;
}

// Whether level-0 cooked cell i (at coordinates ci) may be blended with own (at co, the point's): the same, or its
// neighbour across the surface with a clear ray between them -- the 5 cm near light never taken from across a wall.
bool cookLinked(uint own, ivec3 co, uint i, ivec3 ci, uint side) {
    if (own == i) return true;
    if (own == COOK_NONE) return false;
    uint axis = side >> 1u, a0 = (axis + 1u) % 3u, a1 = (axis + 2u) % 3u;
    int d0 = ci[a0] - co[a0], d1 = ci[a1] - co[a1];
    uint k = cookRing(d0, d1);
    return k < 8u && (cookAdj(own) & (1u << k)) != 0u;
}

#ifdef COOK_REGIONS
// rtgi_cache.glsl's hook: the point's level-0 cooked cell's region at a level.
uint nearRegionOfLevel(uint level) {
    if (rtgiRegion == 0u || level >= NEAR_LEVELS || COOK_PARAMS.w < 0.5) return 0u;
    return COOK_REGIONS((rtgiRegion - 1u) * NEAR_LEVELS + level);
}
#endif

#ifdef COOK_SCALE
// The cache's cell size the cook asks for at p (m): the fine tier's 5 cm cube, else the coarse tier's (COOK_COUNTS.w) --
// the smallest size the cook's surface points there had, sides alike; -1 where the cook covered nothing (the camera's
// distance sizes it then). rtgi_cache.glsl's rtgiCellSize asks it first.
float cookScaleIn(ivec3 cube, uint tier) {
    uvec2 h = cookCellHash(cube, 15u, tier, uint(COOK_COUNTS.z));
    for (uint k = 0u; k < COOK_PROBE_STEPS; k++) {
        uvec2 e = COOK_SCALE((h.x + k) & uint(COOK_COUNTS.z));
        if (e.x == h.y) return uintBitsToFloat(e.y);
        if (e.x == 0u) break;
    }
    return -1.0;
}
float cookScaleAt(vec3 p) {
    if (COOK_PARAMS.w < 0.5) return -1.0;
    float g = cookScaleIn(ivec3(floor((p - NEAR_SCALE_ORIGIN) / COOK_PARAMS.x)), 7u);
    if (g > 0.0) return g;
    return cookScaleIn(ivec3(floor((p - NEAR_SCALE_ORIGIN) / COOK_COUNTS.w)), 6u);
}
#endif

// Ray k of NEAR_RAYS from cell i: cosine about n, a Hammersley set turned by the cell's own angle (the cook and the update
// agree).
vec3 nearRayDir(uint i, uint k, vec3 n) {
    uint bits = k;
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    float r1 = (float(k) + 0.5) / float(NEAR_RAYS), r2 = float(bits) * 2.3283064365386963e-10;
    float phi = 6.28318530718 * fract(r2 + float(cookHashU(i) & 0xFFFFu) / 65536.0);
    float sr = sqrt(r1);
    vec3 t = normalize(abs(n.y) < 0.99 ? cross(n, vec3(0.0, 1.0, 0.0)) : cross(n, vec3(1.0, 0.0, 0.0)));
    vec3 b = cross(n, t);
    return normalize(t * (cos(phi) * sr) + b * (sin(phi) * sr) + n * sqrt(max(0.0, 1.0 - r1)));
}

vec2 nearOctEncode(vec3 v) {
    v /= abs(v.x) + abs(v.y) + abs(v.z);
    vec2 e = v.xy;
    if (v.z < 0.0) e = (1.0 - abs(e.yx)) * vec2(e.x >= 0.0 ? 1.0 : -1.0, e.y >= 0.0 ? 1.0 : -1.0);
    return e * 0.5 + 0.5;
}
vec3 nearOctDecode(vec2 f) {
    f = f * 2.0 - 1.0;
    vec3 v = vec3(f, 1.0 - abs(f.x) - abs(f.y));
    float t = max(-v.z, 0.0);
    v.x += v.x >= 0.0 ? -t : t;
    v.y += v.y >= 0.0 ? -t : t;
    return normalize(v);
}
#endif
