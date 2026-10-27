// The half-res guide (rtgi_readout.frag writes it, beside its light): per half-res pixel, the position of the full-res
// pixel it stands for and that pixel's normal. The blur reads it in place of the full-res depth and normal at its taps --
// fetches two pixels apart, and a position rebuilt through a matrix each. 8 bytes (Rg32Uint): x the position relative to
// the camera of the frame that wrote it, its x and y as halves; y its z as a half (low 16 bits) and the normal
// octahedral, 8 bits a component (high 16); the sky both all ones. As Rgba32F (world position, the normal a float), 16
// bytes a tap: the blur's 13 fetches a pixel were bandwidth (~115 GB/s at 1.4 ms, Crytek Sponza 1080p, 2026-10-27).
// Halves relative to the camera hold a position to ~8 mm at 10 m, the normal to ~1 degree: the plane and normal tests
// take centimetres and 18 degrees.
const uvec2 GUIDE_SKY = uvec2(0xFFFFFFFFu);

uvec2 guidePack(vec3 rel, vec3 n) {
    n /= abs(n.x) + abs(n.y) + abs(n.z);
    vec2 e = n.z >= 0.0 ? n.xy : (1.0 - abs(n.yx)) * vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    uvec2 q = uvec2(round(clamp(e * 0.5 + 0.5, 0.0, 1.0) * 255.0));
    return uvec2(packHalf2x16(rel.xy), (packHalf2x16(vec2(rel.z, 0.0)) & 0xFFFFu) | (q.x << 16) | (q.y << 24));
}

bool guideSky(uvec2 g) { return g == GUIDE_SKY; }

vec3 guidePosition(uvec2 g, vec3 camera) { return camera + vec3(unpackHalf2x16(g.x), unpackHalf2x16(g.y).x); }

vec3 guideNormalOf(uvec2 g) {
    vec2 e = vec2((g.y >> 16) & 255u, g.y >> 24) / 255.0 * 2.0 - 1.0;
    vec3 n = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) n.xy = (1.0 - abs(n.yx)) * vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return normalize(n);
}
