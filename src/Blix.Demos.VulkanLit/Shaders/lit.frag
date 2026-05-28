#version 450

// Lit fragment shader. Composes direct lighting (sun + 2 spots + point)
// with image-based ambient, optional per-pixel debug-channel override.
// Helpers split into the .glsl library under this directory:
//   shadows.glsl          — 2D PCF + omnidirectional cube PCF
//   brdf.glsl             — Cook-Torrance terms + per-light helpers
//   normal_mapping.glsl   — cotangent-frame TBN reconstruction
//   ibl.glsl              — roughness-aware Fresnel + prefilter LOD ceiling
//   debug_channels.glsl   — per-pixel "Shader channel" selector

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    vec3 uSunDirection;
    float uSunIntensity;
    vec3 uAmbientColor;
    float uAmbientIntensity;
    mat4 uSunShadowVP;
    mat4 uSpot0ViewProj;
    vec4 uSpot0PosRange;
    vec4 uSpot0DirCosInner;
    vec4 uSpot0ColorCosOuter;
    mat4 uSpot1ViewProj;
    vec4 uSpot1PosRange;
    vec4 uSpot1DirCosInner;
    vec4 uSpot1ColorCosOuter;
    vec4 uPointPosFar;        // xyz position, w far plane
    vec4 uPointColorRange;    // xyz color*intensity, w range
    vec4 uLightEnable;        // x sun, y spot, z point (0/1 debug toggles)
    vec4 uCameraPos;          // xyz world camera position
    vec4 uDebug;              // x = per-pixel debug channel (0 = normal shading)
} frame;

// Set 1 = per-pass shadow maps. uSpotShadowMaps is a Count=2 array.
layout(set = 1, binding = 0) uniform sampler2D uSunShadowMap;
layout(set = 1, binding = 1) uniform sampler2D uSpotShadowMaps[2];
layout(set = 1, binding = 2) uniform samplerCube uPointShadowCube;
// IBL: diffuse irradiance, prefiltered specular env (mipped), split-sum LUT.
layout(set = 1, binding = 3) uniform samplerCube uIrradiance;
layout(set = 1, binding = 4) uniform samplerCube uPrefilteredEnv;
layout(set = 1, binding = 5) uniform sampler2D uBrdfLut;

// Set 2 = per-material.
layout(set = 2, binding = 0) uniform LitMaterial {
    vec4 uTint;
} mat;
layout(set = 2, binding = 1) uniform sampler2D uAlbedo;
layout(set = 2, binding = 2) uniform sampler2D uNormalMap;

// Per-draw: model matrix + metallic/roughness (vec4: metallic, roughness, _, _).
layout(push_constant) uniform PushConstants {
    mat4 uModel;
    vec4 uMatParams;
} pc;

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec4 vSunShadowCoord;
layout(location = 3) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

// Order matters: brdf's evalSpotBrdf calls sampleShadow from shadows.glsl,
// and the IBL ambient block below uses fresnelSchlickRoughness from ibl.glsl.
#include "shadows.glsl"
#include "brdf.glsl"
#include "normal_mapping.glsl"
#include "ibl.glsl"
#include "debug_channels.glsl"

void main() {
    vec3 Ngeom = normalize(vNormal);
    // Tangent-space normal from the map, scaled by normalScale (uMatParams.z).
    // Flat maps + scale 0 both reduce to the geometric normal.
    vec3 tn = texture(uNormalMap, vUv).xyz * 2.0 - 1.0;
    tn.xy *= pc.uMatParams.z;
    float tn2 = dot(tn, tn);
    vec3 tnN = tn2 > 1e-12 ? tn * inversesqrt(tn2) : vec3(0.0, 0.0, 1.0);
    vec3 N = perturbNormal(Ngeom, vWorldPos, vUv, tnN);
    vec3 V = normalize(frame.uCameraPos.xyz - vWorldPos);
    vec3 albedo = texture(uAlbedo, vUv).rgb * mat.uTint.rgb;
    float metallic = clamp(pc.uMatParams.x, 0.0, 1.0);
    float roughness = clamp(pc.uMatParams.y, 0.04, 1.0);
    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    vec3 Lo = vec3(0.0);

    // Sun (directional). sunShadow hoisted out of a block scope so the
    // debug-channel selector can reuse it without a second PCF sample.
    vec3 sunL = -normalize(frame.uSunDirection);
    float sunShadow = sampleShadow(uSunShadowMap, vSunShadowCoord, max(dot(N, sunL), 0.0));
    {
        vec3 radiance = vec3(frame.uSunIntensity) * sunShadow * frame.uLightEnable.x;
        Lo += brdf(N, V, sunL, radiance, albedo, metallic, roughness, F0);
    }

    // Spot lights (array of 2).
    Lo += evalSpotBrdf(N, V, vWorldPos, frame.uSpot0ViewProj, frame.uSpot0PosRange,
                       frame.uSpot0DirCosInner, frame.uSpot0ColorCosOuter,
                       uSpotShadowMaps[0], albedo, metallic, roughness, F0) * frame.uLightEnable.y;
    Lo += evalSpotBrdf(N, V, vWorldPos, frame.uSpot1ViewProj, frame.uSpot1PosRange,
                       frame.uSpot1DirCosInner, frame.uSpot1ColorCosOuter,
                       uSpotShadowMaps[1], albedo, metallic, roughness, F0) * frame.uLightEnable.y;

    // Point light (omnidirectional cube shadow).
    {
        vec3 fromPoint = vWorldPos - frame.uPointPosFar.xyz;
        float dist = length(fromPoint);
        vec3 L = -fromPoint / max(dist, 1e-4);
        float range = frame.uPointColorRange.w;
        float atten = clamp(1.0 - (dist * dist) / (range * range), 0.0, 1.0);
        float current = dist / frame.uPointPosFar.w;
        float shadow = samplePointShadow(uPointShadowCube, fromPoint, current, dist);
        vec3 radiance = frame.uPointColorRange.xyz * (atten * shadow * frame.uLightEnable.z);
        Lo += brdf(N, V, L, radiance, albedo, metallic, roughness, F0);
    }

    // --- Image-based lighting (ambient) ---------------------------------
    float NdotV = max(dot(N, V), 0.0);
    vec3 R = reflect(-V, N);
    vec3 F = fresnelSchlickRoughness(NdotV, F0, roughness);
    vec3 kd = (vec3(1.0) - F) * (1.0 - metallic);
    vec3 diffuseIBL = texture(uIrradiance, N).rgb * albedo;
    vec3 prefiltered = textureLod(uPrefilteredEnv, R, roughness * MAX_REFLECTION_LOD).rgb;
    vec2 brdfLutSample = texture(uBrdfLut, vec2(NdotV, roughness)).rg;
    vec3 specularIBL = prefiltered * (F0 * brdfLutSample.x + brdfLutSample.y);
    vec3 ambient = (kd * diffuseIBL + specularIBL) * frame.uAmbientIntensity;

    outColor = vec4(ambient + Lo, mat.uTint.a);

    // Per-pixel debug channels (overlay "Shader channel"). Isolates one
    // term so the shading math is visible.
    int dbgMode = int(frame.uDebug.x + 0.5);
    if (dbgMode > 0) {
        vec3 dbg = selectDebugChannel(
            dbgMode,
            albedo, N, Ngeom,
            roughness, metallic, NdotV,
            ambient,
            specularIBL * frame.uAmbientIntensity,
            kd * diffuseIBL * frame.uAmbientIntensity,
            sunShadow, vUv);
        outColor = vec4(dbg, 1.0);
    }
}
