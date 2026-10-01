#version 450

// A lean physically-based direct-lighting pass built ENTIRELY on the engine's shared
// GLSL library — no local copies of a BRDF or a shadow lookup. That is the point of
// the lab as much as the picture is: if blix_cookTorranceBrdf or blix_sun_shadow
// changes, this moves with it.
#include "pbr.glsl"
#include "shadow.glsl"
#include "ibl.glsl"

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    // One fitted light view-projection per cascade, near to far.
    mat4 uCascadeVP0;
    mat4 uCascadeVP1;
    mat4 uCascadeVP2;
    vec4 uCameraPosition;
    // xyz = each cascade's far bound along the view, in metres. w = paint the cascades instead of
    // shading, which is the only instrument that says whether three passes are doing three
    // cascades' work.
    vec4 uCascadeSplits;
    // xyz = one texel as a fraction of each cascade's own map. Per cascade because the PCF radius
    // is measured in texels and the three maps need not be the same size.
    vec4 uCascadeTexels;
    // The camera's unit forward. Cascade selection is by view DEPTH — dot(world - eye, forward) —
    // not by distance to the eye: at equal depth a fragment at the edge of a wide frustum is
    // further away than one in the centre, and picking by radius puts them in different cascades,
    // which shows as an arc across the picture.
    vec4 uCameraForward;
    vec4 uSunDirection;
    vec4 uSunColour;
    // x = prefilter mip ceiling (mip count - 1), FROM THE BAKE — see ibl.glsl for why this is a
    // value and not a constant. y = 1 when the environment probe is real, 0 when it is the 1x1
    // stand-in and the ambient should stay flat.
    vec4 uEnvironment;
};



// Inline per-draw textures belong to set 1; set 2 is reserved for MaterialHandle bindings. Every
// draw using this program, including Studio's ground, must bind every declared set-1 texture.
layout(set = 1, binding = 0) uniform sampler2D uCascade0;
layout(set = 1, binding = 1) uniform sampler2D uAlbedo;
layout(set = 1, binding = 5) uniform sampler2D uCascade1;
layout(set = 1, binding = 6) uniform sampler2D uCascade2;

// Environment baked once from StudioLook's authored sun. Identity stand-ins keep bindings complete
// when IBL is disabled.
layout(set = 1, binding = 2) uniform samplerCube uIrradiance;
layout(set = 1, binding = 3) uniform samplerCube uPrefilteredEnv;
layout(set = 1, binding = 4) uniform sampler2D uBrdfLut;

// The glTF material's other channels. Every draw binds all four; white is inert in each because the
// push terms that read them (normal scale, occlusion strength, emission) default to zero, and
// metallic-roughness multiplies its factors. Each samples the TEXCOORD set its channel names.
layout(set = 1, binding = 7) uniform sampler2D uNormalMap;          // linear, tangent space
layout(set = 1, binding = 8) uniform sampler2D uMetallicRoughness;  // linear, G = roughness, B = metallic
layout(set = 1, binding = 9) uniform sampler2D uOcclusion;          // linear, R = occlusion
layout(set = 1, binding = 10) uniform sampler2D uEmissive;          // sRGB

// Each core channel's KHR_texture_transform, as two rows (u' = a.xyz . (u, v, 1), v' = b.xyz . (u, v, 1)):
// channel c's rows are r = 2c and 2c + 1, held at uUvRows[r / 4][r % 4]. Base colour, normal,
// metallic-roughness, occlusion, emissive. Per draw (set 1 is per-draw); identity where untransformed.
layout(set = 1, binding = 11) uniform UvTransforms {
    mat4 uUvRows[3];
};

layout(push_constant) uniform Push {
    mat4 uModel;
    vec4 uBaseColour;
    vec4 uMaterial;
    // glTF lets each texture select its own TEXCOORD set.
    // x = albedo UV set, y = normal scale (0 = no normal map), z = the other channels' UV sets, one
    // bit each: 1 normal, 2 metallic-roughness, 4 occlusion, 8 emissive. A set bit reads TEXCOORD_1.
    vec4 uExtra;
    vec4 uEmission;       // rgb = emissive radiance, a = occlusion strength (0 = no occlusion)
};

layout(location = 0) in vec3 vWorld;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;
// glTF COLOR_0 multiplies base colour. White is the identity; asset-specific use as baked occlusion
// does not change the attribute's material meaning.
layout(location = 3) in vec4 vColour;
layout(location = 4) in vec2 vUv1;
// The cooked tangent frame: authored or MikkTSpace's, the frame a glTF normal map is baked against.
layout(location = 5) in vec4 vTangent;

layout(location = 0) out vec4 outColour;

// The surface frame glTF defines: the vertex tangent, re-orthogonalised against the interpolated
// normal, and the bitangent cross(N, T) * w. MikkTSpace's frame reproduced exactly is what a baked map
// expects, seams and mirrored UVs included. The cook wrote the tangent against TEXCOORD_0, so a normal
// map naming set 1 is read in set 0's frame.
mat3 surfaceFrame(vec3 N)
{
    vec3 T = normalize(vTangent.xyz - N * dot(N, vTangent.xyz));
    return mat3(T, cross(N, T) * vTangent.w, N);
}

vec2 channelUv(int bit)
{
    return (int(uExtra.z + 0.5) & bit) != 0 ? vUv1 : vUv;
}

vec2 uvOf(int channel, vec2 uv)
{
    int r = channel * 2;
    vec4 a = uUvRows[r / 4][r % 4];
    vec4 b = uUvRows[(r + 1) / 4][(r + 1) % 4];
    vec3 p = vec3(uv, 1.0);
    return vec2(dot(a.xyz, p), dot(b.xyz, p));
}

void main()
{
    vec3 N = normalize(vNormal);
    vec2 uvNormal = uvOf(1, channelUv(1));

    // The shadow's normal offset uses the geometric normal: it is about where the surface is, and a
    // normal map only says how it scatters light.
    vec3 geometric = N;
    float normalScale = uExtra.y;
    if (normalScale > 0.0)
    {
        // Z is rebuilt from XY: a cooked map is two-channel BC5. The scale bends XY, per glTF.
        vec2 xy = texture(uNormalMap, uvNormal).xy * 2.0 - 1.0;
        vec3 tangentNormal = vec3(xy * normalScale, sqrt(max(1.0 - dot(xy, xy), 0.0)));
        N = normalize(surfaceFrame(N) * tangentNormal);
    }
    vec3 V = normalize(uCameraPosition.xyz - vWorld);
    vec3 L = normalize(uSunDirection.xyz);

    float ndotl = max(dot(N, L), 0.0);

    // The shared shadow path receives world metres per texel; it derives sampling texels itself.
    int cascade;
    float shadow = blix_sun_shadow_cascaded(
        uCascade0, uCascade1, uCascade2,
        uCascadeVP0, uCascadeVP1, uCascadeVP2,
        uCascadeTexels.xyz,
        vWorld, geometric, max(dot(geometric, L), 0.0), 1.5, gl_FragCoord.xy,
        cascade);

    vec4 metallicRoughness = texture(uMetallicRoughness, uvOf(2, channelUv(2)));
    float metallic = clamp(uMaterial.x * metallicRoughness.b, 0.0, 1.0);
    float roughness = clamp(uMaterial.y * metallicRoughness.g, 0.04, 1.0);
    // A zero cutoff lets OPAQUE and MASK share this pipeline. Alpha is texture × baseColorFactor.a ×
    // vertex alpha, per glTF. `discard` prevents early-Z for the whole shader; Studio accepts that
    // cost for its small subjects rather than multiplying pipeline variants. Revisit for large views.
    vec2 uvAlbedo = uvOf(0, uExtra.x > 0.5 ? vUv1 : vUv);
    if (uMaterial.w > 0.0 && texture(uAlbedo, uvAlbedo).a * uBaseColour.a * vColour.a < uMaterial.w) discard;

    vec3 albedo = uBaseColour.rgb * texture(uAlbedo, uvAlbedo).rgb * vColour.rgb;

    vec3 F0 = mix(vec3(0.04), albedo, metallic);
    vec3 direct = blix_cookTorranceBrdf(N, V, L, albedo, F0, metallic, roughness)
                * uSunColour.rgb * ndotl * shadow;

    // The same ambient-strength control scales directional IBL or the explicit flat fallback.
    vec3 ambient;
    if (uEnvironment.y > 0.5)
    {
        ambient = blix_iblAmbient(
            N, V, albedo, metallic, roughness, F0,
            uIrradiance, uPrefilteredEnv, uBrdfLut, uEnvironment.x) * uSunColour.a;
    }
    else
    {
        ambient = albedo * uSunColour.a;
    }

    // glTF occlusion darkens indirect light only; the sun is already shadowed.
    ambient *= 1.0 + uEmission.a * (texture(uOcclusion, uvOf(3, channelUv(4))).r - 1.0);

    // Output the same composed alpha used by MASK; opaque pipelines ignore this channel.
    vec3 lit = direct + ambient + uEmission.rgb * texture(uEmissive, uvOf(4, channelUv(8))).rgb;

    // Cascade diagnostics replace shading with a flat band; shadow remains as brightness.
    if (uCascadeTexels.w > 0.5) lit = blix_cascade_tint(cascade) * mix(0.35, 1.0, shadow);

    outColour = vec4(lit, texture(uAlbedo, uvAlbedo).a * uBaseColour.a * vColour.a);
}
