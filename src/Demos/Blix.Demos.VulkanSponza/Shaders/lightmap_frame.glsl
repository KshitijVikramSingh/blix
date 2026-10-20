// --lightmap: LOD 0's shading frame, read through the lightmap atlas (SponzaLoop.Lightmap.cs, PackFrame).
//
// A coarse LOD's triangle interpolates a few far-apart vertex normals over its whole face: its lighting and normal
// mapping smear, and turn when the LOD switches. The atlas holds, per covered texel, the full surface's interpolated
// normal and tangent; a pixel with an atlas texel reads those (bilinear over the four texels around it) instead, so
// the drawn LOD moves the silhouette, not the light. Pixels without one (foliage: left out of the atlas) keep their own.
//
// Set 1 on the lit interface, bound by name with ClipmapState (SponzaLoop.Render.cs, LitClipmapBuffer):
//   uLightmapCells  the atlas's map from texel to its record id (R32UI texture), 0xFFFFFFFF where none covers it
//   LightmapFrames  [0] the atlas size (zero: no atlas); [1 + id] record id's packed frame (zero: none)
#ifndef SPONZA_LIGHTMAP_FRAME_GLSL
#define SPONZA_LIGHTMAP_FRAME_GLSL

// The atlas's map from texel to record as an R32UI texture (SponzaLoop.LightmapGather.cs; a buffer of 256 MB until
// 2026-10-20, the same map the incident pass gathers from).
layout(set = 1, binding = 14) uniform utexture2D uLightmapCells;
//@sampler NearestClamp
layout(set = 1, binding = 16) uniform sampler uLightmapCellSampler;
#define LIGHTMAP_CELL_OF(at) texelFetch(usampler2D(uLightmapCells, uLightmapCellSampler), (at), 0).r
layout(std430, set = 1, binding = 15) readonly buffer LightmapFrames { uvec2 lightmapFrames[]; };

vec3 lightmapOctDecode(uint packed) {
    vec2 o = unpackSnorm2x16(packed);
    vec3 v = vec3(o, 1.0 - abs(o.x) - abs(o.y));
    if (v.z < 0.0) v.xy = (1.0 - abs(v.yx)) * vec2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
    return normalize(v);
}

// What a coarse level's pixel judges LOD 0's frames against, and takes when none agree: its triangle's FACE normal
// (the world position's screen derivatives), turned to the vertex normal's side -- not the vertex normal. The cooked
// chains collapse to fixed ratios, and a pier's joint vertices, their normals smoothed up and down the groove, spread
// those normals over the flat face (the geometric-normal view showed the smear, 2026-10-15). A column's coarse facet
// stays within ~20 degrees of its fine normals, so it still takes them.
vec3 lodFrameReference(vec3 worldPos, vec3 vertexN) {
    vec3 face = cross(dFdx(worldPos), dFdy(worldPos));
    if (dot(face, face) < 1e-20) return vertexN;
    face = normalize(face);
    return dot(face, vertexN) < 0.0 ? -face : face;
}

// The frame at `texel` (atlas units, centres at +0.5; the pre-pass's vLightmapTexel): world normal (the front side's:
// the caller flips it for a back face, as it does its own), tangent xyz and handedness w. False where the pixel has no
// atlas texel, or none of the four around it carries a frame.
bool lightmapShadingFrame(vec2 texel, vec3 vertexN, out vec3 normal, out vec4 tangent) {
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
    // A bilinear neighbour facing another way is another surface's. The gutter around a chart points each empty texel
    // at the first filled neighbour it finds (SponzaLoop.Lightmap.cs), which can be the next chart's -- a block's top,
    // the next stone: blended in, it tilted the normal at every joint and edge, and the sun's n.l drew a crevice along
    // flush joints on the lit faces (the user, 2026-10-14).
    // The reference: of the pixel's own texel and the four around it, the frame agreeing best with the pixel's vertex
    // normal. The own texel alone was wrong where it straddles an arris: its one record is one face's, and the other
    // face's pixels in it took that face's normal, dropped their own face's texels and drew the arris as a texel
    // staircase -- the arch mouldings' teeth on every coarse level (2026-10-14). The vertex normal turns smoothly, so
    // the switch between faces follows a smooth line; the best match is always one of the five, so none is dropped.
    vec3 ownN = vec3(0.0);
    {
        vec3 vn = dot(vertexN, vertexN) > 1e-12 ? normalize(vertexN) : vec3(0.0);
        float bestDot = -2.0;
        for (int i = 0; i < 5; ++i) {
            ivec2 at = i == 4 ? ivec2(floor(texel)) : base + ivec2(i & 1, i >> 1);
            if (any(lessThan(at, ivec2(0))) || any(greaterThanEqual(at, ivec2(size)))) continue;
            uint oid = LIGHTMAP_CELL_OF(at);
            if (oid == 0xFFFFFFFFu) continue;
            uvec2 op = lightmapFrames[1u + oid];
            if (op.x == 0u && op.y == 0u) continue;
            vec3 cn = lightmapOctDecode(op.x);
            float d = dot(cn, vn);
            if (d > bestDot) { bestDot = d; ownN = cn; }
        }
        // Even the best of the five turned more than 25 degrees from the drawn face (lodFrameReference): a coarse level
        // drawn flat over a joint its simplification collapsed (inside one chart, so the seam locks keep it), its texels
        // the groove's walls -- their normals drew bright and dark bands along the piers' joints at LOD error 2
        // (2026-10-15). The caller's own instead.
        if (bestDot < 0.9) return false;
    }
    for (int i = 0; i < 4; ++i) {
        ivec2 o = ivec2(i & 1, i >> 1);
        ivec2 at = base + o;
        if (any(lessThan(at, ivec2(0))) || any(greaterThanEqual(at, ivec2(size)))) continue;
        uint id = LIGHTMAP_CELL_OF(at);
        if (id == 0xFFFFFFFFu) continue;
        uvec2 packed = lightmapFrames[1u + id];
        if (packed.x == 0u && packed.y == 0u) continue;
        if (dot(ownN, ownN) > 0.5 && dot(lightmapOctDecode(packed.x), ownN) < 0.9) continue;
        // A floor under the bilinear weight, as texelsAroundLightmap has (texel.glsl): the reference can be a texel the
        // bilinear gives nothing.
        float w = max((o.x == 1 ? t.x : 1.0 - t.x) * (o.y == 1 ? t.y : 1.0 - t.y), 1.0 / 64.0);
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
