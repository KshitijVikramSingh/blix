// Lightlet sun records (SponzaLoop.LightletBake.cs has the layout): a texel's signed angular distance (degrees) to its
// visibility boundary at a sun given in FINE NODE coordinates over the band (x hour, y declination; 0..30, 0..14).
// Positive: the texel sees the sun. The includer defines LIGHTLET_INDEX(i) (a texel's index word) and LIGHTLET_POOL(i)
// (the records' words). An index of 0 is never lit over the band, 1 always: +-LIGHTLET_UNIFORM.
#ifndef LIGHTLET_GLSL
#define LIGHTLET_GLSL

#define LIGHTLET_UNIFORM 99.0
const float LIGHTLET_LEVELS[8] = float[8](0.0, 0.25, 0.5, 0.75, 1.0, 1.5, 2.0, 3.0);

float lightletDecode(uint code) {
    float m = LIGHTLET_LEVELS[code & 7u];
    return (code & 8u) != 0u ? m : -m;
}

// Coarse node (i, j) of the record at `base`: 16 x 8, eight codes a word.
float lightletCoarse(uint base, int i, int j) {
    uint c = uint(j * 16 + i);
    return lightletDecode((LIGHTLET_POOL(base + c / 8u) >> (4u * (c % 8u))) & 15u);
}

float lightletDistance(uint texel, vec2 fine) {
    uint idx = LIGHTLET_INDEX(texel);
    if (idx == 0u) return -LIGHTLET_UNIFORM;
    if (idx == 1u) return LIGHTLET_UNIFORM;
    uint base = idx - 2u;
    vec2 coarse = clamp(fine * 0.5, vec2(0.0), vec2(14.999, 6.999));
    int ci = int(floor(coarse.x)), cj = int(floor(coarse.y));
    uint cell = uint(cj * 15 + ci);
    uint maskWord = LIGHTLET_POOL(base + 16u + cell / 32u);
    if (((maskWord >> (cell % 32u)) & 1u) == 0u) {
        vec2 t = coarse - vec2(ci, cj);
        return mix(mix(lightletCoarse(base, ci, cj), lightletCoarse(base, ci + 1, cj), t.x),
                   mix(lightletCoarse(base, ci, cj + 1), lightletCoarse(base, ci + 1, cj + 1), t.x), t.y);
    }
    // Refined: the cell's word is the rank of its bit among the mask's.
    uint rank = bitCount(maskWord & ((1u << (cell % 32u)) - 1u));
    for (uint w = 0u; w < cell / 32u; ++w) rank += uint(bitCount(LIGHTLET_POOL(base + 16u + w)));
    uint extra = LIGHTLET_POOL(base + 20u + rank);
    // The cell's 3 x 3 fine nodes: corners are coarse nodes, the rest the extra word's codes in the bake's order.
    float n[9];
    n[0] = lightletCoarse(base, ci, cj);
    n[2] = lightletCoarse(base, ci + 1, cj);
    n[6] = lightletCoarse(base, ci, cj + 1);
    n[8] = lightletCoarse(base, ci + 1, cj + 1);
    n[1] = lightletDecode(extra & 15u);           // (1, 0)
    n[3] = lightletDecode((extra >> 4u) & 15u);   // (0, 1)
    n[4] = lightletDecode((extra >> 8u) & 15u);   // (1, 1)
    n[5] = lightletDecode((extra >> 12u) & 15u);  // (2, 1)
    n[7] = lightletDecode((extra >> 16u) & 15u);  // (1, 2)
    vec2 local = clamp(fine - vec2(2 * ci, 2 * cj), vec2(0.0), vec2(1.999));
    int fi = int(floor(local.x)), fj = int(floor(local.y));
    vec2 t = local - vec2(fi, fj);
    return mix(mix(n[fj * 3 + fi], n[fj * 3 + fi + 1], t.x), mix(n[(fj + 1) * 3 + fi], n[(fj + 1) * 3 + fi + 1], t.x), t.y);
}

#endif
