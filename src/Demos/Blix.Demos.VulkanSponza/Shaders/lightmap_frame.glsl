// --lightmap: LOD 0's shading frame, read through the lightmap atlas (SponzaLoop.Lightmap.cs, PackFrame).
//
// A coarse LOD's triangle interpolates a few far-apart vertex normals over its whole face: its lighting and normal
// mapping smear, and turn when the LOD switches. The atlas holds, per covered texel, the full surface's interpolated
// normal and tangent; a pixel with an atlas texel reads those (bilinear over the four texels around it) instead, so
// the drawn LOD moves the silhouette, not the light. Pixels without one (foliage: left out of the atlas) keep their own.
//
// Set 1 on the lit interface, bound by name with ClipmapState (SponzaLoop.Render.cs, LitClipmapBuffer):
//   LightmapCells   the atlas's map from texel (y * width + x) to its record id, 0xFFFFFFFF where none covers it
//   LightmapFrames  [0] the atlas size (zero: no atlas); [1 + id] record id's packed frame (zero: none)
#ifndef SPONZA_LIGHTMAP_FRAME_GLSL
#define SPONZA_LIGHTMAP_FRAME_GLSL

layout(std430, set = 1, binding = 14) readonly buffer LightmapCells { uint lightmapCellOf[]; };
layout(std430, set = 1, binding = 15) readonly buffer LightmapFrames { uvec2 lightmapFrames[]; };

vec3 lightmapOctDecode(uint packed) {
    vec2 o = unpackSnorm2x16(packed);
    vec3 v = vec3(o, 1.0 - abs(o.x) - abs(o.y));
    if (v.z < 0.0) v.xy = (1.0 - abs(v.yx)) * vec2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
    return normalize(v);
}

// The frame at `texel` (atlas units, centres at +0.5; the pre-pass's vLightmapTexel): world normal (the front side's:
// the caller flips it for a back face, as it does its own), tangent xyz and handedness w. False where the pixel has no
// atlas texel, or none of the four around it carries a frame.
bool lightmapShadingFrame(vec2 texel, out vec3 normal, out vec4 tangent) {
    normal = vec3(0.0, 0.0, 1.0);
    tangent = vec4(1.0, 0.0, 0.0, 1.0);
    uvec2 size = lightmapFrames[0];
    if (size.x == 0u || dot(texel, texel) < 1e-6) return false;
    vec2 f = texel - 0.5;
    ivec2 base = ivec2(floor(f));
    vec2 t = f - vec2(base);
    vec3 n = vec3(0.0);
    vec3 tg = vec3(0.0);
    float handed = 0.0, weight = 0.0;
    for (int i = 0; i < 4; ++i) {
        ivec2 o = ivec2(i & 1, i >> 1);
        ivec2 at = base + o;
        if (any(lessThan(at, ivec2(0))) || any(greaterThanEqual(at, ivec2(size)))) continue;
        uint id = lightmapCellOf[at.y * int(size.x) + at.x];
        if (id == 0xFFFFFFFFu) continue;
        uvec2 packed = lightmapFrames[1u + id];
        if (packed.x == 0u && packed.y == 0u) continue;
        float w = (o.x == 1 ? t.x : 1.0 - t.x) * (o.y == 1 ? t.y : 1.0 - t.y);
        n += w * lightmapOctDecode(packed.x);
        tg += w * lightmapOctDecode(packed.y & ~1u);
        handed += w * ((packed.y & 1u) != 0u ? -1.0 : 1.0);
        weight += w;
    }
    if (weight < 1e-6 || dot(n, n) < 1e-12) return false;
    normal = normalize(n);
    tangent = vec4(dot(tg, tg) > 1e-12 ? normalize(tg) : tangent.xyz, handed >= 0.0 ? 1.0 : -1.0);
    return true;
}

#endif
