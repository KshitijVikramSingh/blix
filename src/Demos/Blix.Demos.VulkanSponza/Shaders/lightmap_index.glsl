// The lightmap atlas's map from texel to record id, tile-sparse (SponzaLoop.LightmapGather.cs, PackLightmapIndex): the
// includer declares LightmapIndex (uint lightmapIndex[]). Words: [0] the atlas's width, [1] its height, [2] its 8 x 8
// tiles a row, [3] their count; then a word a tile -- the first word of its 64 ids, or 0xFFFFFFFF where no record covers
// any of it -- then the covered tiles' ids, row by row within each. 0xFFFFFFFF where no record covers a texel. The dense
// R32 map was 64 MB on Crytek Sponza's 4096^2 atlas, 41% covered; half its tiles hold a record (2026-10-24).
#ifndef LIGHTMAP_INDEX_GLSL
#define LIGHTMAP_INDEX_GLSL
uvec2 lightmapIndexSize() { return uvec2(lightmapIndex[0], lightmapIndex[1]); }
uint lightmapIndexAt(ivec2 at) {
    uvec2 size = uvec2(lightmapIndex[0], lightmapIndex[1]);
    if (any(lessThan(at, ivec2(0))) || uint(at.x) >= size.x || uint(at.y) >= size.y) return 0xFFFFFFFFu;
    uvec2 a = uvec2(at);
    uint tile = lightmapIndex[4u + (a.y >> 3) * lightmapIndex[2] + (a.x >> 3)];
    return tile == 0xFFFFFFFFu ? 0xFFFFFFFFu : lightmapIndex[tile + ((a.y & 7u) << 3) + (a.x & 7u)];
}

#ifdef LIGHTMAP_CELLS
// The atlas resolve's cells (lightmap_cells.comp): the includer declares LightmapCellLight (uvec2 cellLight[]) beside
// LightmapIndex. A cell's two words, or "no record" (flag 1) off the atlas or in an empty tile.
#define LIGHTMAP_CELL_NONE 1u
#define LIGHTMAP_CELL_SLOW 2u    // not whole: the full lookup
#define LIGHTMAP_CELL_SEAMS 8u   // seam neighbours: the pixel's seam blend (incident_clipmap.frag's, from the cell)
#define LIGHTMAP_CELL_PAIR 4u    // its words the front's: a pixel facing away reads the back (id + 1)
#define LIGHTMAP_CELL_BACK_SLOW 16u
// The cell's record id (the index's word beside it).
uint lightmapCellId(ivec2 at) {
    uvec2 a = uvec2(at);
    uint tile = lightmapIndex[4u + (a.y >> 3) * lightmapIndex[2] + (a.x >> 3)];
    return tile == 0xFFFFFFFFu ? 0xFFFFFFFFu : lightmapIndex[tile + ((a.y & 7u) << 3) + (a.x & 7u)];
}
uvec2 lightmapCellAt(ivec2 at) {
    uvec2 size = uvec2(lightmapIndex[0], lightmapIndex[1]);
    if (any(lessThan(at, ivec2(0))) || uint(at.x) >= size.x || uint(at.y) >= size.y) return uvec2(0u, LIGHTMAP_CELL_NONE << 8);
    uvec2 a = uvec2(at);
    uint tile = lightmapIndex[4u + (a.y >> 3) * lightmapIndex[2] + (a.x >> 3)];
    if (tile == 0xFFFFFFFFu) return uvec2(0u, LIGHTMAP_CELL_NONE << 8);
    return cellLight[tile - 4u - lightmapIndex[3] + ((a.y & 7u) << 3) + (a.x & 7u)];
}
uint lightmapCellFlags(uvec2 c) { return (c.y >> 8) & 0xFFu; }
float lightmapCellSky(uvec2 c) { float s = float(c.y & 0xFFu) / 255.0; return s * s; }   // texelDetailUnpack's
vec3 lightmapCellNormal(uvec2 c) {
    vec2 e = vec2(float((c.y >> 16) & 0xFFu), float(c.y >> 24)) / 255.0 * 2.0 - 1.0;
    vec3 v = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    if (v.z < 0.0) v.xy = (1.0 - abs(v.yx)) * vec2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
    return normalize(v);
}
#endif
#endif
