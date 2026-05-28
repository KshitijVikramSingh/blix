// Cotangent-frame normal mapping (Schüler 2013). Builds a TBN from
// screen-space derivatives of world position + UV — no per-vertex tangent
// needed, so it works uniformly across cube, ground, spheres, and the
// skinned mesh. A flat normal map (0,0,1) leaves N unchanged.
//
// Degenerate frames (flat UVs / zero position derivatives near edges)
// produce inversesqrt(0) = Inf → NaN. We detect via the m < 1e-12 guard
// and fall back to the geometric normal so the bloom chain doesn't get
// fed a NaN that would spread into a screen-wide green wash.
vec3 perturbNormal(vec3 N, vec3 worldPos, vec2 uv, vec3 tangentNormal) {
    vec3 dp1 = dFdx(worldPos);
    vec3 dp2 = dFdy(worldPos);
    vec2 duv1 = dFdx(uv);
    vec2 duv2 = dFdy(uv);
    vec3 dp2perp = cross(dp2, N);
    vec3 dp1perp = cross(N, dp1);
    vec3 T = dp2perp * duv1.x + dp1perp * duv2.x;
    vec3 B = dp2perp * duv1.y + dp1perp * duv2.y;
    float m = max(dot(T, T), dot(B, B));
    if (m < 1e-12) return N;
    float invmax = inversesqrt(m);
    mat3 TBN = mat3(T * invmax, B * invmax, N);
    return normalize(TBN * tangentNormal);
}
