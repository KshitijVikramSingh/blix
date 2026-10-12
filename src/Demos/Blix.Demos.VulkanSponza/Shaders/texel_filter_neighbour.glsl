// The texel filter's neighbourhood (texel_filter.comp, as the texels gather; texel_compose.comp, the split light's
// parts at rest): neighbour k of 27 around texel id, and its weight. The includer includes texel.glsl first.
#ifndef TEXEL_FILTER_NEIGHBOUR_GLSL
#define TEXEL_FILTER_NEIGHBOUR_GLSL

// Neighbour k (0-26; 13 itself) of texel id at (position, n), or -1; tg the grid its distances are in. Lightmap texels
// (the records from lightmap.w on; the hashed ones before): the 3 x 3 atlas texels around this one (its atlas coordinate
// rides in normal.w), on this side; the plane test of texelFilterWeight keeps a chart's neighbours across a seam out.
// Distances in 4 cm (the base texel; the cloth's 2 cm texels weigh their neighbours a little wider). Hashed texels: the
// 27 cells around its own at its level, in its normal's bin.
int texelFilterNeighbour(int id, uint k, vec3 position, vec3 n, TexelGrid finestGrid, vec4 lightmap, out TexelGrid tg) {
    tg = finestGrid;
    if (lightmap.x > 0.5 && float(id) >= lightmap.w - 0.5) {
        tg.spacing = 0.04;
        uint packedAt = floatBitsToUint(TEXEL_NORMAL(id).w);
        ivec2 at = ivec2(int(packedAt & 0xFFFFu), int(packedAt >> 16)) + ivec2(int(k) % 3, int(k) / 3) - 1;
        if (k == 4u) return id;
        if (k >= 9u || any(lessThan(at, ivec2(0))) || any(greaterThanEqual(at, ivec2(lightmap.yz)))) return -1;
        uint o = TEXEL_INDEX_MAP(at.y * int(lightmap.y) + at.x);
        if (o == 0xFFFFFFFFu) return -1;
        if (TEXEL_POSITION(o).w < 0.0 && dot(TEXEL_NORMAL(o).xyz, n) < 0.0) o += 1u;
        return abs(TEXEL_POSITION(o).w) >= 2.0 ? int(o) : -1;
    }
    tg = texelLevelGrid(finestGrid, uint(TEXEL_NORMAL(id).w + 0.5));
    ivec3 cell = ivec3(floor((position - tg.minCorner) / tg.spacing)) + ivec3(int(k) % 3, (int(k) / 3) % 3, int(k) / 9) - 1;
    return k == 13u ? id : texelFind(tg, cell, texelBin(n));
}

// Neighbour other's weight in texel (position, n)'s filter, its light's luminance otherLum against the centre's centre
// (0: not on this surface): distance (sigma a cell), normal, and how near its light is -- one more than ~half again
// brighter or darker is an edge (a shadow's, a corner's), not noise, and drops out. edge.x 1: the edge measured
// against the brighter of the two (--texel-filter-symmetric); edge.y 1: each neighbour weighed by the surface it holds
// (--texel-filter-area).
float texelFilterWeight(int other, vec3 position, vec3 n, TexelGrid tg, float centre, float otherLum, vec4 edge) {
    vec3 tn = TEXEL_NORMAL(other).xyz;
    vec3 tp = TEXEL_POSITION(other).xyz;
    if (dot(tn, n) <= 0.0 || abs(dot(n, tp - position)) > tg.spacing) return 0.0;
    vec3 dp = (tp - position) / tg.spacing;
    // Against the centre alone, the test is lopsided under noise: a texel noisy-high keeps its darker neighbours and is
    // pulled down, one noisy-low drops its brighter ones as edges and stays -- shade filtered darker than it is. Against
    // the brighter of the two, a ratio is an edge either way.
    float e = (otherLum - centre) / (0.5 * (edge.x > 0.5 ? max(otherLum, centre) : centre) + 1e-5);
    float f = dot(tn, n);
    float w = exp(-0.5 * dot(dp, dp)) * exp(-e * e) * f * f * f * f;
    // A texel's surface in its cell, as texelsAround weighs it (texel.glsl): a sliver of a wall at an inside corner holds
    // a texel lit like the corner, and at full weight it pulled its neighbours dark -- most at the hashed texels' coarser
    // levels, where cells are 8-16 cm and Sponza's mouldings make slivers.
    if (edge.y > 0.5) w *= min(abs(TEXEL_POSITION(uint(other)).w) / (4.0 * float(1u << (2u * tg.level))), 1.0);
    return w;
}

#endif
