// The half-res guide (rtgi_readout.frag writes it, beside its light): per half-res pixel, the world position of the
// full-res pixel it stands for (xyz) and that pixel's normal (w). The blur reads it in place of the full-res depth and
// normal at its taps -- fetches two pixels apart, and a position rebuilt through a matrix each.
// w: the normal octahedral, two 12-bit components as one exact float integer (bits written to a float target may come
// back changed: a NaN's are not kept); the sky -1.
float guideNormal(vec3 n) {
    n /= abs(n.x) + abs(n.y) + abs(n.z);
    vec2 e = n.z >= 0.0 ? n.xy : (1.0 - abs(n.yx)) * vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    uvec2 q = uvec2(round(clamp(e * 0.5 + 0.5, 0.0, 1.0) * 4095.0));
    return float(q.x * 4096u + q.y);
}

vec3 guideNormalOf(float w) {
    uint v = uint(w);
    vec2 e = vec2(v >> 12, v & 4095u) / 4095.0 * 2.0 - 1.0;
    vec3 n = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) n.xy = (1.0 - abs(n.yx)) * vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
    return normalize(n);
}

bool guideSky(vec4 g) { return g.w < 0.0; }
