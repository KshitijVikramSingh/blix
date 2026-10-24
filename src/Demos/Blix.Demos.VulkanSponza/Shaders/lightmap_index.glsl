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
#endif
