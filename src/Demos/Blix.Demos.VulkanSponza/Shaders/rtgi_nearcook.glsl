// --rtgi-near-cook (SponzaLoop.RtgiNearCook.cs): the near field cooked from the geometry. Surface cells of NEAR_PARAMS.x
// metres (by side, as the cache's), placed at load from the triangles where the volume found the geometry tight (its
// refined probes); each traces NEAR_RAYS fixed cosine rays once, to the contact radius (rtgi_nearcook_rays.comp,
// rtgi_nearcook_cook.comp): how many left (its escape share) and, for each that met a surface, how far, that surface's
// normal and albedo. Each frame its near light is those surfaces' light from the cache (rtgi_nearcook_update.comp), and
// the readout reads it (rtgi_readout.frag readNearCooked) beside the cache's far light. Nothing is traced per pixel or per
// frame: the near field is the surface's, not the screen's -- the per-pixel near rays' history cleared as the camera
// moved, and a crease took seconds to come back (the user, 2026-10-29), who asked for it worked ahead of the camera, by
// chunk, as the volume is.
//
// The includer declares NEAR_TABLE(i) (uvec2: key, cell) and NEAR_PARAMS (vec4: x the cell's size; y the table's slots -
// 1; z the cells; w 1 on, the near light over the cache's far light; 2 on, each cell's own full gather shown). Per cell: its point and side (vec4: xyz, w the side as bits), its normal (vec4); its links,
// NEAR_RAYS uvec2 (x: bit 31 met, bits 16-30 the distance in 32768ths of the radius, bits 0-15 the normal octahedral 8:8;
// y: the albedo, unorm 4x8); its light (vec4: rgb the near light, w the escape share).
#ifndef RTGI_NEARCOOK_GLSL
#define RTGI_NEARCOOK_GLSL

#define NEAR_RAYS 32u
#ifndef RTGI_ORIGIN
#define RTGI_ORIGIN vec3(0.0137, 0.0271, 0.0419)
#endif
#define NEAR_PROBE_STEPS 8u
// A cell's point's w (bits): its side (0-2), and NEAR_BURIED where its rays mostly met back faces -- sampled from a floor
// running under the wall standing on it, inside the wall (rtgi_nearcook_cook.comp): no surface anything reaches. Left out
// of every lookup and every training point.
#define NEAR_BURIED 16u

uint nearHash(uint x) {
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

// The cell (c, side)'s two hashes: x the slot it starts probing at, y its key (never 0). SponzaLoop.RtgiNearCook.cs's
// NearHash is the same.
uvec2 nearCellHash(ivec3 c, uint side) {
    uint a = nearHash(uint(c.x) * 0x9E3779B1u ^ nearHash(uint(c.y) * 0x85EBCA77u ^ nearHash(uint(c.z) * 0xC2B2AE3Du ^ (side + 0x3A1u))));
    uint k = nearHash(a ^ 0x2545F491u ^ uint(c.x) * 0x1B873593u ^ uint(c.y) * 0xCC9E2D51u ^ uint(c.z) * 0x27D4EB2Fu) | 1u;
    return uvec2(a & uint(NEAR_PARAMS.y), k);
}

uint nearFind(ivec3 c, uint side) {
    uvec2 h = nearCellHash(c, side);
    for (uint k = 0u; k < NEAR_PROBE_STEPS; k++) {
        uvec2 e = NEAR_TABLE((h.x + k) & uint(NEAR_PARAMS.y));
        if (e.x == h.y) return e.y;
        if (e.x == 0u) break;
    }
    return 0xFFFFFFFFu;
}

// Ray k of cell i: cosine about n, a Hammersley set turned by the cell's own angle (the cook and the update agree).
vec3 nearRayDir(uint i, uint k, vec3 n) {
    uint bits = k;
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    float r1 = (float(k) + 0.5) / float(NEAR_RAYS), r2 = float(bits) * 2.3283064365386963e-10;
    float phi = 6.28318530718 * fract(r2 + float(nearHash(i) & 0xFFFFu) / 65536.0);
    float sr = sqrt(r1);
    vec3 t = normalize(abs(n.y) < 0.99 ? cross(n, vec3(0.0, 1.0, 0.0)) : cross(n, vec3(1.0, 0.0, 0.0)));
    vec3 b = cross(n, t);
    return normalize(t * (cos(phi) * sr) + b * (sin(phi) * sr) + n * sqrt(max(0.0, 1.0 - r1)));
}

// The region of a point (rtgi_cache.glsl's rtgiRegion): its cooked cell's (the normal's w, rtgi_nearcook_cook.comp's
// third pass: 1 + the 2 m probe it reaches), 0 where none is cooked. The includer declares NEAR_POINTS(i) for it.
// The 8 neighbours of a cooked cell across its surface (along the side's two other axes), ordered so a neighbour's way
// back is k ^ 1; a cell's links (NEAR_ADJ: bit k, a clear ray to that neighbour's point, rtgi_nearcook_cook.comp).
const ivec2 NEAR_RING[8] = ivec2[8](ivec2(1, 0), ivec2(-1, 0), ivec2(0, 1), ivec2(0, -1), ivec2(1, 1), ivec2(-1, -1), ivec2(1, -1), ivec2(-1, 1));
// The cache levels whose cubes the regions are found in (5 cm to 1.6 m); coarser, region 0.
#define NEAR_LEVELS 6u

#ifdef NEAR_POINTS
// The point's cooked cell, 1 + its index (0 none): its own -- or, that one missing or buried (a cell straddling a
// junction, a sliver of it above the floor), the next toward the visible surface (from the cell's middle toward the point,
// along the surface, one cell).
uint nearRegion(vec3 p, uint side) {
    if (NEAR_PARAMS.w < 0.5) return 0u;
    float size = NEAR_PARAMS.x;
    ivec3 c = ivec3(floor((p - RTGI_ORIGIN) / size));
    uint j = nearFind(c, side);
    if (j != 0xFFFFFFFFu && (floatBitsToUint(NEAR_POINTS(2u * j).w) & NEAR_BURIED) == 0u) return j + 1u;
    uint axis = side >> 1u, a0 = (axis + 1u) % 3u, a1 = (axis + 2u) % 3u;
    vec3 off = (p - RTGI_ORIGIN) / size - (vec3(c) + 0.5);
    ivec3 cc = c;
    if (abs(off[a0]) >= abs(off[a1])) cc[a0] += off[a0] >= 0.0 ? 1 : -1;
    else cc[a1] += off[a1] >= 0.0 ? 1 : -1;
    uint k = nearFind(cc, side);
    if (k != 0xFFFFFFFFu && (floatBitsToUint(NEAR_POINTS(2u * k).w) & NEAR_BURIED) == 0u) return k + 1u;
    return j != 0xFFFFFFFFu ? j + 1u : 0u;
}
#endif
#ifndef RTGI_REGION_DECLARED
#define RTGI_REGION_DECLARED
uint rtgiRegion = 0u;
#endif
#ifdef NEAR_SCALE
// The cache's cell size the cooked cells ask for at p (m; rtgi_nearcook_cook.comp kinds 11-13), by p's 5 cm cube, any side;
// -1 where none is cooked (the probes size it then). rtgi_cache.glsl's rtgiCellSize asks it first. The includer declares
// NEAR_SCALE(i) (uvec2: key, size's bits; the near table's slots).
float nearScaleAt(vec3 p) {
    if (NEAR_PARAMS.w < 0.5) return -1.0;
    uvec2 h = nearCellHash(ivec3(floor((p - RTGI_ORIGIN) / NEAR_PARAMS.x)), 7u);
    for (uint k = 0u; k < NEAR_PROBE_STEPS; k++) {
        uvec2 e = NEAR_SCALE((h.x + k) & uint(NEAR_PARAMS.y));
        if (e.x == h.y) return uintBitsToFloat(e.y);
        if (e.x == 0u) break;
    }
    return -1.0;
}
#endif
#ifdef NEAR_REGIONS
// rtgi_cache.glsl's hook: the point's cooked cell's region at a level.
uint nearRegionOfLevel(uint level) {
    if (rtgiRegion == 0u || level >= NEAR_LEVELS || NEAR_PARAMS.w < 0.5) return 0u;
    return NEAR_REGIONS((rtgiRegion - 1u) * NEAR_LEVELS + level);
}
#endif
#if defined(NEAR_ADJ) && defined(NEAR_POINTS)
// Whether cooked cell i may be blended with cell own (the point's): the same, or its neighbour across the surface with a
// clear ray between them -- the 5 cm near light never taken from across a wall.
bool nearLinked(uint own, uint i, uint side) {
    if (own == i) return true;
    if (own == 0xFFFFFFFFu) return false;
    float size = NEAR_PARAMS.x;
    ivec3 co = ivec3(floor((NEAR_POINTS(2u * own).xyz - RTGI_ORIGIN) / size));
    ivec3 ci = ivec3(floor((NEAR_POINTS(2u * i).xyz - RTGI_ORIGIN) / size));
    uint axis = side >> 1u, a0 = (axis + 1u) % 3u, a1 = (axis + 2u) % 3u;
    ivec2 d = ivec2(ci[a0] - co[a0], ci[a1] - co[a1]);
    for (uint k = 0u; k < 8u; k++) if (NEAR_RING[k] == d) return (NEAR_ADJ(own) & (1u << k)) != 0u;
    return false;
}
#endif

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
