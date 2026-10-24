// The light-agnostic record's direction as the frame reads it (--texel-h4): per texel and channel c, its shape s_c, the
// world-space H4 slope over its irradiance at its own normal -- E_c(n) = E_c(n0) (1 + s_c . (n - n0)) (texel_record_h4.glsl).
// Three uvec2 a texel ([texel * 3 + c]): x the half pair (s.x, s.y), y (s.z, 1 present / 0 none).
uvec2 texelShapePack(vec3 s) { return uvec2(packHalf2x16(s.xy), packHalf2x16(vec2(s.z, 1.0))); }
vec4 texelShapeUnpack(uvec2 w) { return vec4(unpackHalf2x16(w.x), unpackHalf2x16(w.y)); }   // xyz the shape, w 1 present

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
// A gathered texel's record into its own shape (TexelOwnShape: uvec2 ownShape[]); none present where it makes none.
void texelShapeStore(uint id, vec4 r0, vec4 r1, vec4 r2, vec4 r3) {
    vec3 s0, s1, s2;
    bool any = texelShapeFromRecord(r0, r1, r2, r3, s0, s1, s2);
    ownShape[id * 3u] = any ? texelShapePack(s0) : uvec2(0u);
    ownShape[id * 3u + 1u] = any ? texelShapePack(s1) : uvec2(0u);
    ownShape[id * 3u + 2u] = any ? texelShapePack(s2) : uvec2(0u);
}
#endif
