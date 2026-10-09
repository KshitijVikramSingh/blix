// A texel's normal bin (texel.glsl): each component of the normal x 1.5 rounded to -1, 0 or 1, as the cook keys it
// (SponzaLoop.Texels.cs, TexelBin) -- 26 directions (the cube's faces, edges and corners). Axis-aligned and 45-degree
// normals sit at bins' centres; boundaries lie ~19.5 degrees off an axis. An octahedral 4x4 was tried first: its
// boundaries pass through the axes themselves (+z at the corner of four bins, -z on the fold), and a floor, a ceiling
// or a wall is exactly what architecture is made of -- the pre-pass's face bin differed from the cook's at 143 of 576
// points on Sponza's hall from derivative noise alone.
// Its own include so the pre-pass can write the bin of each pixel's FACE.
#ifndef TEXEL_BIN_GLSL
#define TEXEL_BIN_GLSL
#define TEXEL_BIN_COUNT 27
uint texelBin(vec3 n) {
    ivec3 q = clamp(ivec3(floor(n * 1.5 + 0.5)), ivec3(-1), ivec3(1)) + 1;
    return uint(q.x + 3 * q.y + 9 * q.z);
}

// The bin across the nearest boundary when the normal lies within ~2 degrees of one (else the bin itself): a lookup
// searches both, so a face whose noise straddles the line still finds the cook's texel.
uint texelBinAlternate(vec3 n) {
    vec3 s = n * 1.5 + 0.5;
    vec3 d = abs(s - floor(s) - 0.0);              // distance above the lower boundary
    vec3 u = abs(ceil(s) - s);                     // distance below the upper one
    vec3 m = min(d, u);
    int axis = m.x < m.y ? (m.x < m.z ? 0 : 2) : (m.y < m.z ? 1 : 2);
    if (m[axis] > 0.05) return texelBin(n);
    ivec3 q = clamp(ivec3(floor(s)), ivec3(-1), ivec3(1));
    q[axis] = clamp(d[axis] < u[axis] ? int(floor(s[axis])) - 1 : int(floor(s[axis])) + 1, -1, 1);
    q += 1;
    return uint(q.x + 3 * q.y + 9 * q.z);
}

// What the pre-pass writes in its normal target's w for a face normal: 1 + bin + 32 x alternate (an Rgba16F holds
// integers to 2048 exactly). texel.glsl unpacks it; 0 means none written.
float texelFaceBins(vec3 face) {
    return 1.0 + float(texelBin(face) + 32u * texelBinAlternate(face));
}
#endif
