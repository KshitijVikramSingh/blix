// The light-agnostic record's direction as the frame reads it (--texel-h4): per texel and channel c, its shape s_c, the
// world-space H4 slope over its irradiance at its own normal -- E_c(n) = E_c(n0) (1 + s_c . (n - n0)) (texel_record_h4.glsl).
// One uint a texel and channel ([texel * 3 + c]): the shape over its scale as three snorm8 (bytes 0-2), the scale in the
// top byte as 2^((e - 128) / 16), rounded up, e 1-255 (0: none) -- the shapes' largest components run 0.6 at the median to
// 8 in the corridor's creases, so one fixed range clipped or wasted; steps ~0.4% of the channel's largest component.
uint texelShapePack(vec3 s) {
    float m = max(abs(s.x), max(abs(s.y), abs(s.z)));
    float e = clamp(ceil(16.0 * log2(max(m, 1e-30))) + 128.0, 1.0, 255.0);
    vec3 c = clamp(s / exp2((e - 128.0) / 16.0), -1.0, 1.0);
    uvec3 b = uvec3(ivec3(round(c * 127.0)) & 255);
    return b.x | (b.y << 8) | (b.z << 16) | (uint(e) << 24);
}
vec4 texelShapeUnpack(uint w) {   // xyz the shape, w 1 present / 0 none
    uint e = w >> 24;
    if (e == 0u) return vec4(0.0);
    ivec3 b = (ivec3(uvec3(w, w >> 8, w >> 16) & 255u) << 24) >> 24;
    return vec4(vec3(b) / 127.0 * exp2((float(e) - 128.0) / 16.0), 1.0);
}

// A gathered texel's shape from its record (TexelH4's 4 vec4s): per channel b / (a + b . n0), n0 the record's own normal
// (packed in [3].y); false where it has none (no passes, or no light at its normal).
bool texelShapeFromRecord(vec4 r0, vec4 r1, vec4 r2, vec4 r3, out vec3 s0, out vec3 s1, out vec3 s2) {
    s0 = vec3(0.0); s1 = vec3(0.0); s2 = vec3(0.0);
    if (r3.w <= 0.0) return false;
    vec2 o = unpackSnorm2x16(floatBitsToUint(r3.y));
    vec3 n0 = vec3(o, 1.0 - abs(o.x) - abs(o.y));
    if (n0.z < 0.0) n0.xy = (1.0 - abs(n0.yx)) * vec2(n0.x >= 0.0 ? 1.0 : -1.0, n0.y >= 0.0 ? 1.0 : -1.0);
    n0 = normalize(n0);
    float e0 = r0.x + dot(r0.yzw, n0), e1 = r1.x + dot(r1.yzw, n0), e2 = r2.x + dot(r2.yzw, n0);
    if (e0 <= 0.0 && e1 <= 0.0 && e2 <= 0.0) return false;
    // A dark channel keeps no shape (its slope over almost nothing): the brightest channel's in its place.
    float emax = max(e0, max(e1, e2));
    vec3 bmax = e0 == emax ? r0.yzw : e1 == emax ? r1.yzw : r2.yzw;
    vec3 sm = bmax / emax;
    s0 = e0 > 1e-3 * emax ? r0.yzw / e0 : sm;
    s1 = e1 > 1e-3 * emax ? r1.yzw / e1 : sm;
    s2 = e2 > 1e-3 * emax ? r2.yzw / e2 : sm;
    return true;
}

#ifndef TEXEL_SHAPE_NO_STORE
// A gathered texel's record into its own shape (TexelOwnShape: uint ownShape[]); none present where it makes none.
void texelShapeStore(uint id, vec4 r0, vec4 r1, vec4 r2, vec4 r3) {
    vec3 s0, s1, s2;
    bool any = texelShapeFromRecord(r0, r1, r2, r3, s0, s1, s2);
    ownShape[id * 3u] = any ? texelShapePack(s0) : 0u;
    ownShape[id * 3u + 1u] = any ? texelShapePack(s1) : 0u;
    ownShape[id * 3u + 2u] = any ? texelShapePack(s2) : 0u;
}
#endif
