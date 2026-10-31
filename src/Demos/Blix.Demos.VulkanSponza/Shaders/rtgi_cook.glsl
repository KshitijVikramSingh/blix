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
//   - the surface pieces: within every cell's cube, at every level, its surface's points joined where they lie within
//     their spacing on one plane -- each piece a cache cell of its own (cookPieceAt; rtgi_cache.glsl's rtgiRegionFor:
//     the two sides of a thin wall, a floor running under it, a wall and a face a gap in front of it, kept apart);
//   - the near field of the level-0 junction cells (the first COOK_COUNTS.y cells): NEAR_RAYS fixed links each to what
//     lies within the contact radius, relit each frame (rtgi_nearcook_update.comp), read over the cache's far light.
// Cells are ordered level 0's live junction cells first (the near field's), then level 0's others (COOK_COUNTS.x level-0
// cells in all), then level 1 up.
//
// The includer declares COOK_PARAMS (vec4: x the finest cell (m); y the cell table's slots - 1; z the cells; w 1 ready),
// COOK_COUNTS (vec4: x the level-0 cells; y the near field's cells; z the size table's slots - 1; w the coarse cube (m)),
// COOK_TABLE(i) (uvec2: key, cell) and COOK_POINTS(i) (vec4, two a cell: the point (xyz) and its bits (w: side, flags,
// level); the normal (xyz) and the neighbour mask (w, bits)); and, where it reads them, COOK_SCALE(i) (uvec2: key, size's
// bits) and COOK_PIECES(i) (uvec4: the cells' surface pieces, cookPieceAt).
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
vec3 rtgiRegionP = vec3(0.0);
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

// A key's point: its level-0 cooked cell (rtgiRegion, the near field's) and the position its regions are found from
// (rtgiRegionP, rtgi_cache.glsl's rtgiRegionFor).
void rtgiKeyAt(vec3 p, uint side) {
    rtgiRegion = nearRegion(p, side);
    rtgiRegionP = p;
}

#ifdef COOK_PIECES
// The cooked cells' surface pieces (SponzaLoop.RtgiPieces.cs): COOK_PIECES(i) uvec4 -- per cell its head (first entry,
// pieces), then per piece two (its centroid's bits, its 4 x 4 footprint across the cube; its own area, its whole).
// p's piece of the cube (c, level, side), 1 + its index -- 0 the cube's only piece, or no cooked cell: of the pieces
// whose footprint holds p (clamped into the cube across the surface: a neighbour's, the edge p's surface runs on
// into), the nearest along the normal; none, the nearest by both.
// The index of p's piece among the pieces of cooked cell j (its cube c at level, head its head): of those whose
// footprint holds p clamped into the cube, the nearest along the normal; none, the nearest by both.
uint cookPieceIndex(uvec4 head, ivec3 c, uint level, uint side, vec3 p) {
    uint axis = side >> 1u, a0 = (axis + 1u) % 3u, a1 = (axis + 2u) % 3u;
    float cs = cookSize(level);
    vec3 g = (p - rtgiOrigin(side)) / cs - vec3(c);
    float u = clamp(g[a0], 0.0, 0.999), v = clamp(g[a1], 0.0, 0.999);
    uint bit = 1u << (uint(u * 4.0) + 4u * uint(v * 4.0));
    vec3 pc = p;
    pc[a0] = (float(c[a0]) + u) * cs + rtgiOrigin(side)[a0];
    pc[a1] = (float(c[a1]) + v) * cs + rtgiOrigin(side)[a1];
    uint best = 0u;
    float bestScore = 1e30;
    for (uint k = 0u; k < head.y; k++) {
        uvec4 e = COOK_PIECES(head.x + 2u * k);
        vec3 d = pc - uintBitsToFloat(e.xyz);
        float score = ((e.w & bit) != 0u ? 0.0 : 1e3) + abs(d[axis]) + 0.25 * length(vec2(d[a0], d[a1]));
        if (score < bestScore) { bestScore = score; best = k; }
    }
    return best;
}

// The pieces in a cube past which a lookup starts a level finer (COOK_PIECE_SCAN): a coarse cube holds hundreds (every
// column, parapet and step under it, each its own -- 650 in one of the arches testbed's, its filter 36 ms a frame
// scanning them, 2026-10-31).
#define COOK_PIECE_SCAN 8u

// p's piece of the cube (c, level, side), 1 + its index -- 0 the cube's only piece, or no cooked cell. In a cube of many,
// found a level or more finer (the finest cooked cube holding p, clamped into this one, of few pieces) and followed up
// its parents: each piece a level up holds the pieces below it whole.
uint cookPieceAt(ivec3 c, uint level, uint side, vec3 p) {
    uint j = cookFind(c, level, side);
    if (j == COOK_NONE) return 0u;
    uvec4 head = COOK_PIECES(j);
    if (head.y <= 1u) return 0u;
    if (head.y <= COOK_PIECE_SCAN) return cookPieceIndex(head, c, level, side, p) + 1u;
    // p clamped into the cube, every axis: the finer cubes searched are this one's.
    float cs = cookSize(level);
    vec3 lo = vec3(c) * cs + rtgiOrigin(side);
    vec3 q = clamp(p, lo + 1e-4 * cs, lo + (1.0 - 1e-4) * cs);
    uint l = level;
    uvec4 hl = head;
    ivec3 cl = c;
    while (l > 0u && hl.y > COOK_PIECE_SCAN) {
        ivec3 cf = cookCoords(q, side, l - 1u);
        uint jf = cookFind(cf, l - 1u, side);
        if (jf == COOK_NONE) break;
        l--;
        cl = cf;
        hl = COOK_PIECES(jf);
    }
    uint k = cookPieceIndex(hl, cl, l, side, q);
    // Up the parents to the level asked.
    for (; l < level; l++) {
        uint parent = COOK_PIECES(hl.x + 2u * k + 1u).z;
        if (parent == 0xFFFFu) return cookPieceIndex(head, c, level, side, p) + 1u;
        k = parent;
        if (l + 1u < level) {
            ivec3 cu = cookCoords(q, side, l + 1u);
            uint ju = cookFind(cu, l + 1u, side);
            if (ju == COOK_NONE) return cookPieceIndex(head, c, level, side, p) + 1u;
            hl = COOK_PIECES(ju);
        }
    }
    return k + 1u;
}
#endif

#ifdef COOK_SCALE
// The cache's cell size the cook asks for at p on a side (m): the fine tier's 5 cm cube for that side, else the coarse
// tier's (COOK_COUNTS.w) -- the mean log size of the cook's surface points of that side there, each point's size its own
// side's samples on its own plane, smoothed along the surface (SponzaLoop.RtgiCook.cs); -1 where the cook covered nothing
// (the camera's distance sizes it then). rtgi_cache.glsl's rtgiCellSize asks it first. By side: the smallest of every
// side's in a cube put the floor's cells at the wall's size along every foot of a wall (2026-10-31).
float cookScaleIn(ivec3 cube, uint tier, uint side) {
    uvec2 h = cookCellHash(cube, tier, side, uint(COOK_COUNTS.z));
    for (uint k = 0u; k < COOK_PROBE_STEPS; k++) {
        uvec2 e = COOK_SCALE((h.x + k) & uint(COOK_COUNTS.z));
        if (e.x == h.y) return uintBitsToFloat(e.y);
        if (e.x == 0u) break;
    }
    return -1.0;
}
float cookScaleAt(vec3 p, uint side) {
    if (COOK_PARAMS.w < 0.5) return -1.0;
    float g = cookScaleIn(ivec3(floor((p - NEAR_SCALE_ORIGIN) / COOK_PARAMS.x)), 15u, side);
    if (g > 0.0) return g;
    ivec3 coarse = ivec3(floor((p - NEAR_SCALE_ORIGIN) / COOK_COUNTS.w));
    g = cookScaleIn(coarse, 14u, side);
    // No point of that side here (a shading normal's dominant axis not the triangle's -- a surface near 45 degrees): the
    // coarse cube's other sides, before the camera's distance (1% of Cornell's pixels).
    for (uint k = 1u; k < 6u && !(g > 0.0); k++) g = cookScaleIn(coarse, 14u, (side + k) % 6u);
    return g;
}
#endif

#ifdef COOK_CELL_POINTS
// A cell's training points (SponzaLoop.RtgiCook.cs): several spread over its own surface piece -- one per occupied
// sub-square of its 4 x 4 grid across the surface, in a Bayer order, inside-tested at junctions -- so a cell's mean is its
// surface's, not one spot's. One buffer of uvec2s, COOK_CELL_POINTS(k): the first COOK_PARAMS.z its cells' (x: the cell's
// first point | its count << 27; y: its turn, the next point to train from, rtgi_rays.comp), then the points, each within
// its cell's cube, 16 bits an axis (x: x | y << 16; y: z), y's upper bits its training tally (rtgi_train.comp: samples
// 16-21, those whose ray met a back face 22-27, COOK_POINT_DEAD -- dropped from the cell's turn).
#define COOK_POINT_DEAD 0x80000000u
#define COOK_POINT_INDEX(i) COOK_CELL_POINTS(i).x
uint cookPointCount(uint i) { return COOK_POINT_INDEX(i) >> 27; }
uint cookPointSlot(uint i, uint k) { return (COOK_POINT_INDEX(i) & 0x07FFFFFFu) + k; }
bool cookPointDead(uint i, uint k) { return (COOK_CELL_POINTS(cookPointSlot(i, k)).y & COOK_POINT_DEAD) != 0u; }
vec3 cookPointAt(uint i, uint k) {
    uint bits = cookBits(i);
    uint side = bits & 7u, level = (bits >> 8) & 15u;
    ivec3 c = cookCoords(COOK_POINTS(2u * i).xyz, side, level);
    uvec2 q = COOK_CELL_POINTS((COOK_POINT_INDEX(i) & 0x07FFFFFFu) + k);
    vec3 f = (vec3(q.x & 0xFFFFu, q.x >> 16, q.y & 0xFFFFu) + 0.5) / 65536.0;
    return rtgiOrigin(side) + (vec3(c) + f) * cookSize(level);
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
