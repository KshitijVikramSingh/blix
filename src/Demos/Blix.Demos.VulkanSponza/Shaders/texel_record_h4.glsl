// The light-agnostic texel record's gather side (--texel-h4; texel_nodes_lean.comp and texel_nodes_shade.glsl): every
// light's rays alike into a texel's H4 per channel, in world space -- E(n) = a + b . n for any normal n near the record's
// (a least-squares fit over normals within 45 deg of it: h4_kernel.glsl). Per texel, 4 vec4s:
//   [0..2]  each channel's (a, b.xyz)
//   [3]     the same rays' plain mean irradiance (luminance), the record's normal (snorm2x16 octahedral, as bits), 0, passes
// summed over the passes of the slice that last gathered the texel (reset at its first pass); a near round's cut rays out.
// The including shader declares LightmapFrames (uvec2 lightmapFrames[]) and TexelH4 (vec4 texelH4[]) and includes
// h4_kernel.glsl.

// The record's smooth normal, as its rays were drawn about it (texel_nodes_rays.comp's recordSmoothNormal).
vec3 recordH4Normal(uint id, vec3 face, bool smoothOn) {
    if (!smoothOn || lightmapFrames[0].x == 0u) return face;
    uint packed = lightmapFrames[1u + id].x;
    if (packed == 0u) return face;
    vec2 o = unpackSnorm2x16(packed);
    vec3 v = vec3(o, 1.0 - abs(o.x) - abs(o.y));
    if (v.z < 0.0) v.xy = (1.0 - abs(v.yx)) * vec2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
    v = normalize(v);
    if (dot(v, face) < 0.0) v = -v;
    return dot(v, face) > 0.866 ? v : face;
}

// A ray's light (its contribution: L / pdf x its cosine about ns) into its world-space H4 terms per channel.
void recordH4Ray(vec3 ns, vec3 dir, vec3 contribution, out vec4 r, out vec4 g, out vec4 b) {
    vec3 t1 = normalize(cross(abs(ns.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), ns));
    vec3 t2 = cross(ns, t1);
    float cs = dot(ns, dir);
    vec3 l = cs > 1e-4 ? contribution / cs : vec3(0.0);   // L / pdf
    vec4 k = h4Kernel(vec3(dot(t1, dir), dot(t2, dir), cs));
    vec4 kw = vec4(k.x, k.y * t1 + k.z * t2 + k.w * ns);
    r = l.r * kw; g = l.g * kw; b = l.b * kw;
}

vec2 recordH4Oct(vec3 n) {
    vec3 d = n / (abs(n.x) + abs(n.y) + abs(n.z));
    return d.z >= 0.0 ? d.xy : (1.0 - abs(d.yx)) * vec2(d.x >= 0.0 ? 1.0 : -1.0, d.y >= 0.0 ? 1.0 : -1.0);
}
