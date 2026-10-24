// A texel's record, as every shader reads it (SponzaLoop.TexelRecords.cs packs it): its position (w: its weight -- 4 an
// atlas texel's, -5 a double-sided pair's side, |w| < 2 buried -- or a hashed texel's surface samples) and its normal
// (w: an atlas texel's coordinate, x | y << 16 as float bits, or a hashed texel's level). The including shader declares
// the buffer (Texels, uvec4 texels[]; TEXEL_ARRAY names another) before this.
//
// 16 B a record (32 as two vec4s before, 2026-10-24), after a header of two uvec4s: [0] the positions' origin (xyz) and
// step (w), floats; [1].x the first atlas record. Each record:
//   x  position x (20 bits) | z's low 12 << 20          the position the origin + step x (0 .. 2^20 - 1) an axis
//   y  position y (20 bits) | z's high 8 << 20 | normal w's bits 0-3 << 28
//   z  normal, octahedral, 12 bits a component (0 / 4095 in x / y: none) | normal w's bits 4-11 << 24
//   w  position w, half | normal w's bits 12-27 << 16    normal w: an atlas texel's x | y << 14, a hashed texel's level
#ifndef TEXEL_RECORD_GLSL
#define TEXEL_RECORD_GLSL
#ifndef TEXEL_ARRAY
#define TEXEL_ARRAY texels
#endif

vec4 texelRecordPosition(uint i) {
    uvec4 r = TEXEL_ARRAY[2u + i];
    vec4 h = uintBitsToFloat(TEXEL_ARRAY[0]);
    uvec3 q = uvec3(r.x & 0xFFFFFu, r.y & 0xFFFFFu, (r.x >> 20) | (((r.y >> 20) & 0xFFu) << 12));
    return vec4(h.xyz + vec3(q) * h.w, unpackHalf2x16(r.w & 0xFFFFu).x);
}

vec4 texelRecordNormal(uint i) {
    uvec4 r = TEXEL_ARRAY[2u + i];
    uint w = (r.y >> 28) | ((r.z >> 24) << 4) | ((r.w >> 16) << 12);
    float nw = i >= TEXEL_ARRAY[1].x ? uintBitsToFloat((w & 0x3FFFu) | ((w >> 14) << 16)) : float(w);
    uvec2 o = uvec2(r.z & 0xFFFu, (r.z >> 12) & 0xFFFu);
    if (o.x == 0u && o.y == 4095u) return vec4(0.0, 0.0, 0.0, nw);
    vec2 e = vec2(o) / 4095.0 * 2.0 - 1.0;
    vec3 n = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) n.xy = (1.0 - abs(n.yx)) * vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return vec4(normalize(n), nw);
}

#define TEXEL_POSITION(i) texelRecordPosition(uint(i))
#define TEXEL_NORMAL(i) texelRecordNormal(uint(i))
#endif
