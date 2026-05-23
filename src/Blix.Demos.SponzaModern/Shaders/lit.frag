#version 410 core

// Walkthrough PBR. Cook-Torrance direct lighting from a single sun + shadow
// map, plus real IBL via a baked HDR procedural cubemap (diffuse + specular).
// ACES filmic tonemap and 2.2 gamma encode at output.
//
// Tangent frame is synthesised per-fragment via screen-space derivatives so
// the static vertex layout (no TANGENT attr) still does normal mapping.

in vec3 worldPosition;
in vec3 worldNormal;
in vec2 texCoord;
in float viewDepth;

layout(location = 0) out vec4 fragColor;
// MRT attachment 1: per-fragment roughness, single channel. Sampled by the
// SSR post-pass to skip matte surfaces (cloth, plaster, brick). Sky/fog/
// volume passes write to this attachment too so the value stays meaningful
// after blending; see their shaders for what they output.
layout(location = 1) out vec4 fragMaterial;

uniform sampler2D uAlbedo;
uniform sampler2D uNormalMap;
uniform sampler2D uMetallicRoughness;
uniform sampler2D uEmissive;
// CSM: 3 cascade shadow maps + per-cascade light view-projections + split
// distances. Fragment picks cascade by view-space depth.
uniform sampler2DShadow uShadowMap;    // cascade 0 (closest)
uniform sampler2DShadow uShadowMap1;
uniform sampler2DShadow uShadowMap2;
#define CASCADE_COUNT 3
uniform mat4  uCascadeLightVPs[CASCADE_COUNT];
uniform float uCascadeSplits[CASCADE_COUNT + 1];
// Debug: when > 0.5, tint fragments by cascade colour (red/green/blue)
// instead of lighting them, so we can see which fragments fall in which
// cascade. Set to 0 in normal rendering.
uniform float uVisualizeCascades;
// HDR linear cubemap (no sRGB decode). Auto-generated mips approximate a
// roughness prefilter: sample at lod = roughness * (mipCount - 1) for spec,
// at lod = mipCount - 1 for diffuse irradiance (most-blurred mip is close
// enough to the cosine-weighted hemisphere for a low-frequency sky).
uniform samplerCube uEnvMap;
uniform float uEnvMapMipCount;
// Precomputed diffuse irradiance cubemap. Each texel is the integral of
// the env map over the cosine-weighted hemisphere centred on that
// direction. Replaces "sample the env map's smallest mip" (which averaged
// out all directional colour) with a real directional irradiance probe.
// If no HDR env was loaded the engine binds the regular env cube here, in
// which case the diffuse term reverts to a coarse approximation.
uniform samplerCube uDiffuseIrradiance;
// Karis split-sum: GGX-prefiltered specular cube (one mip per roughness)
// and the 2D BRDF integration LUT (R=scale on F0, G=bias). The lit shader
// composes them as `prefilteredColour * (F0 * brdf.r + brdf.g)` for the
// full split-sum specular IBL term.
uniform samplerCube uSpecularPrefilter;
uniform sampler2D   uBrdfLut;
uniform float       uSpecularPrefilterMipCount;

uniform vec4  uBaseColorFactor;
uniform vec3  uEmissiveFactor;
uniform float uMetallicFactor;
uniform float uRoughnessFactor;
// glTF alphaMode handling. > 0 = MASK (discard when albedo.a < cutoff);
// 0 = OPAQUE or BLEND (no discard, alpha is either irrelevant or
// consumed by the alpha-blend pipeline downstream). GltfSceneInstance
// sets this per-material based on the source alphaMode.
uniform float uAlphaCutoff;
uniform float uNormalScale;
uniform float uHasMetallicMap;
// glTF doubleSided gate: when > 0.5, fragments on the back face (i.e.
// !gl_FrontFacing) flip the geometric normal so two-sided geometry like
// foliage and fabric lights correctly from the inside. Set per-material
// from GltfMaterial.DoubleSided.
uniform float uDoubleSided;

// Baked ambient-occlusion. uOcclusion stores AO in the R channel
// (glTF spec). uOcclusionStrength scales between "AO ignored" (0) and
// "full AO" (1). uHasOcclusionMap gates the sample so materials without
// an occlusion texture fall back to ao = 1 without sampling whatever
// default placeholder is bound.
uniform sampler2D uOcclusion;
uniform float     uHasOcclusionMap;
uniform float     uOcclusionStrength;

uniform vec3  uCameraPosition;
uniform vec3  uSunDirection;
uniform vec3  uSunColor;
uniform float uExposure;

uniform float uEmissiveBoost;
uniform float uIblSpecAttenuation;
uniform float uIblDiffuseBoost;
uniform float uMetalFloor;
uniform float uIndirectShadowBase;
uniform float uIndirectShadowRange;
// Horizon-fade strength on indirect specular: kills the white-halo rim that
// Schlick/Lazarov Fresnel produces at silhouette pixels (NdotV -> 0). 0 = off,
// 1 = aggressive fade. The fade is a smoothstep on NdotV; uHorizonFadeStart
// controls where the fade begins (smaller = tighter to the silhouette).
uniform float uHorizonFadeStrength;
uniform float uHorizonFadeStart;
// Tint applied to env-cube samples (both diffuse irradiance and specular
// reflection). Matches the visible skybox tint so the IBL probe agrees
// with the backdrop. White (1,1,1) is identity; Night uses deep blue.
uniform vec3 uSkyTint;

// Multiplier on the SPECULAR portion of each point light's direct
// contribution. View-dependent specular highlights on small metallic
// geometry sitting near several point lights migrate fast enough across
// the surface to read as "the geometry is swinging" when the camera
// moves; dropping this lets the user attenuate that effect without
// sacrificing the diffuse glow from the same lights.
uniform float uPointLightSpecScale;

// Point lights. Cook-Torrance direct contribution per light. The first four
// lights cast cube shadows (sampled below); lights 4..7 are unshadowed.
// Falloff is the windowed inverse-square Frostbite uses: 1/r^2 multiplied by
// smooth (1 - (d/range)^4)^2 so the light reaches zero at its range without
// popping.
#define MAX_POINT_LIGHTS 8
#define MAX_POINT_SHADOWS 4
uniform vec3  uPointLightPositions[MAX_POINT_LIGHTS];
uniform vec3  uPointLightColors[MAX_POINT_LIGHTS];   // intensity baked in
uniform float uPointLightRanges[MAX_POINT_LIGHTS];
uniform float uPointLightCount;   // sent as float to avoid an IntUniform type

// Cube shadow maps for the first MAX_POINT_SHADOWS lights. Each stores linear
// distance-to-light normalised by uPointShadowFarPlane (see shadow_cube.frag).
// Per-light samplers (not an array of samplerCubeShadow) so the dynamic index
// in the loop unrolls cleanly on the macOS GL 4.10 driver.
uniform samplerCubeShadow uPointShadowMap0;
uniform samplerCubeShadow uPointShadowMap1;
uniform samplerCubeShadow uPointShadowMap2;
uniform samplerCubeShadow uPointShadowMap3;
uniform float uPointShadowFarPlane;
uniform float uPointShadowBias;
// Filter radius for 20-tap PCF on cube shadow lookups, in world units.
// 0 = single-tap hardware PCF (hard edges), ~0.1 = a few cm of penumbra.
uniform float uPointShadowFilterRadius;

#include "lib/pbr.glsl"

// Local short-name aliases. The library uses blix_-prefixed names so it
// can ship alongside other GLSL libraries without collisions; the rest of
// this file pre-dates the prefix.
#define PI BLIX_PI
#define distributionGGX     blix_distributionGGX
#define geometrySchlickGGX  blix_geometrySchlickGGX
#define geometrySmith       blix_geometrySmith
#define fresnelSchlick      blix_fresnelSchlick

// --- Tangent frame ---------------------------------------------------------
mat3 cotangentFrame(vec3 N, vec3 worldPos, vec2 uv)
{
    vec3 dp1 = dFdx(worldPos);
    vec3 dp2 = dFdy(worldPos);
    vec2 duv1 = dFdx(uv);
    vec2 duv2 = dFdy(uv);

    vec3 dp2perp = cross(dp2, N);
    vec3 dp1perp = cross(N, dp1);
    vec3 T = dp2perp * duv1.x + dp1perp * duv2.x;
    vec3 B = dp2perp * duv1.y + dp1perp * duv2.y;

    float invMax = inversesqrt(max(dot(T, T), dot(B, B)));
    return mat3(T * invMax, B * invMax, N);
}

vec3 perturbNormal(vec3 N, vec3 worldPos, vec2 uv)
{
    vec3 sampled = texture(uNormalMap, uv).rgb * 2.0 - 1.0;
    vec3 tangentNormal = mix(vec3(0.0, 0.0, 1.0), sampled, uNormalScale);
    return normalize(cotangentFrame(N, worldPos, uv) * tangentNormal);
}

// --- IBL via cubemap mip approximation ------------------------------------
vec3 sampleEnvSpecular(vec3 R, float roughness)
{
    float lod = roughness * max(uEnvMapMipCount - 1.0, 0.0);
    return textureLod(uEnvMap, R, lod).rgb;
}

vec3 sampleEnvIrradiance(vec3 N)
{
    // Pre-convolved diffuse irradiance cubemap. The PbrIblBaker already
    // baked in the cos-weighted hemispherical integral and the * PI factor,
    // so this is the proper irradiance E(N) -- no shader-side scaling.
    return texture(uDiffuseIrradiance, N).rgb;
}

// --- Shadow PCF on a single cascade --------------------------------------
// 3x3 hardware-PCF taps. cascadeIdx selects which sampler to read from --
// GLSL 4.10 doesn't allow dynamic indexing of sampler arrays, so we
// dispatch with if-else.
float samplePcfCascade(int cascadeIdx, vec2 uv, float biasedZ)
{
    vec2 texel;
    float sum = 0.0;
    if (cascadeIdx == 0)
    {
        texel = 1.0 / vec2(textureSize(uShadowMap, 0));
        for (int y = -1; y <= 1; ++y)
        for (int x = -1; x <= 1; ++x)
            sum += texture(uShadowMap, vec3(uv + vec2(x, y) * texel, biasedZ));
    }
    else if (cascadeIdx == 1)
    {
        texel = 1.0 / vec2(textureSize(uShadowMap1, 0));
        for (int y = -1; y <= 1; ++y)
        for (int x = -1; x <= 1; ++x)
            sum += texture(uShadowMap1, vec3(uv + vec2(x, y) * texel, biasedZ));
    }
    else
    {
        texel = 1.0 / vec2(textureSize(uShadowMap2, 0));
        for (int y = -1; y <= 1; ++y)
        for (int x = -1; x <= 1; ++x)
            sum += texture(uShadowMap2, vec3(uv + vec2(x, y) * texel, biasedZ));
    }
    return sum / 9.0;
}

// Picks the appropriate cascade for this fragment and samples it with PCF.
// Fragments beyond the last cascade's far plane get no shadow (returns 1.0)
// so the shadow doesn't cut hard at the cascade boundary.
float sampleShadowCsm(float NdotL)
{
    // Find cascade index: the first one whose far split is greater than
    // this fragment's view-space depth.
    int cascadeIdx = -1;
    for (int i = 0; i < CASCADE_COUNT; ++i)
    {
        if (viewDepth <= uCascadeSplits[i + 1])
        {
            cascadeIdx = i;
            break;
        }
    }
    if (cascadeIdx < 0) return 1.0;

    vec4 shadowProj = uCascadeLightVPs[cascadeIdx] * vec4(worldPosition, 1.0);
    vec3 coords = shadowProj.xyz / shadowProj.w;
    coords = coords * 0.5 + 0.5;
    if (coords.z > 1.0) return 1.0;
    if (coords.x < 0.0 || coords.x > 1.0) return 1.0;
    if (coords.y < 0.0 || coords.y > 1.0) return 1.0;

    // Bias scales with NdotL AND cascade depth range. Far cascades cover
    // ~10x more world space per shadow texel than near cascades, so a
    // constant bias either acnes the near cascade or peter-pans the far
    // one. Scale by the cascade's far split.
    float cascadeFar = uCascadeSplits[cascadeIdx + 1];
    float baseBias = max(0.001 * (1.0 - NdotL), 0.0003);
    float bias = baseBias * (1.0 + cascadeFar * 0.04);
    return samplePcfCascade(cascadeIdx, coords.xy, coords.z - bias);
}

// --- Point-light cube shadow PCF -----------------------------------------
// 20 sample directions around the light->frag vector. The points are the
// corners + edge-midpoints of a cube, which gives a decent rotation-invariant
// approximation of a sphere with only 20 taps. Filter radius is in world
// units (offsets are added directly to the unnormalised lookup direction --
// `texture(samplerCubeShadow, ...)` doesn't care about the direction's
// magnitude, only its bearing).
const vec3 cubeSampleOffsets[20] = vec3[](
    vec3( 1.0,  1.0,  1.0), vec3( 1.0, -1.0,  1.0),
    vec3(-1.0, -1.0,  1.0), vec3(-1.0,  1.0,  1.0),
    vec3( 1.0,  1.0, -1.0), vec3( 1.0, -1.0, -1.0),
    vec3(-1.0, -1.0, -1.0), vec3(-1.0,  1.0, -1.0),
    vec3( 1.0,  1.0,  0.0), vec3( 1.0, -1.0,  0.0),
    vec3(-1.0, -1.0,  0.0), vec3(-1.0,  1.0,  0.0),
    vec3( 1.0,  0.0,  1.0), vec3(-1.0,  0.0,  1.0),
    vec3( 1.0,  0.0, -1.0), vec3(-1.0,  0.0, -1.0),
    vec3( 0.0,  1.0,  1.0), vec3( 0.0, -1.0,  1.0),
    vec3( 0.0, -1.0, -1.0), vec3( 0.0,  1.0, -1.0)
);

// fragToLight points from the light TO the fragment. dist is its length;
// reused as the comparison reference after the same far-plane normalisation
// the shadow_cube.frag applied when writing.
float samplePointShadow(int idx, vec3 fragToLight, float dist)
{
    float refDepth = dist / uPointShadowFarPlane - uPointShadowBias;
    if (refDepth <= 0.0) return 1.0;
    if (refDepth >= 1.0) return 1.0;

    // Single-tap fast path when filtering is disabled. The 20-tap loop is
    // ~$$$ on macOS GL (no driver loop unrolling, indexed-sampler cost),
    // worth dodging when the user wants hard shadows.
    if (uPointShadowFilterRadius <= 0.0)
    {
        if (idx == 0) return texture(uPointShadowMap0, vec4(fragToLight, refDepth));
        if (idx == 1) return texture(uPointShadowMap1, vec4(fragToLight, refDepth));
        if (idx == 2) return texture(uPointShadowMap2, vec4(fragToLight, refDepth));
        if (idx == 3) return texture(uPointShadowMap3, vec4(fragToLight, refDepth));
        return 1.0;
    }

    // 20-tap PCF: branch on idx ONCE per fragment, then loop the 20 lookups
    // inside the chosen sampler so the driver can unroll cleanly.
    float r = uPointShadowFilterRadius;
    float shadow = 0.0;
    if (idx == 0)
    {
        for (int i = 0; i < 20; ++i)
            shadow += texture(uPointShadowMap0,
                vec4(fragToLight + cubeSampleOffsets[i] * r, refDepth));
    }
    else if (idx == 1)
    {
        for (int i = 0; i < 20; ++i)
            shadow += texture(uPointShadowMap1,
                vec4(fragToLight + cubeSampleOffsets[i] * r, refDepth));
    }
    else if (idx == 2)
    {
        for (int i = 0; i < 20; ++i)
            shadow += texture(uPointShadowMap2,
                vec4(fragToLight + cubeSampleOffsets[i] * r, refDepth));
    }
    else if (idx == 3)
    {
        for (int i = 0; i < 20; ++i)
            shadow += texture(uPointShadowMap3,
                vec4(fragToLight + cubeSampleOffsets[i] * r, refDepth));
    }
    else
    {
        return 1.0;
    }
    return shadow / 20.0;
}

#include "lib/tonemap.glsl"
#define acesFilm blix_acesFilm

void main()
{
    vec2 uv = vec2(texCoord.x, 1.0 - texCoord.y);

    vec4 albedoSample = texture(uAlbedo, uv) * uBaseColorFactor;
    if (uAlphaCutoff > 0.0 && albedoSample.a < uAlphaCutoff) discard;
    vec3 albedo = pow(albedoSample.rgb, vec3(2.2));



    vec3 mrSample = texture(uMetallicRoughness, uv).rgb;
    float metallic = mix(uMetallicFactor, mrSample.b * uMetallicFactor, uHasMetallicMap);
    float roughness = mix(uRoughnessFactor, mrSample.g * uRoughnessFactor, uHasMetallicMap);
    roughness = clamp(roughness, 0.05, 1.0);

    float aoSample = texture(uOcclusion, uv).r;
    float ao = mix(1.0, mix(1.0, aoSample, uOcclusionStrength), uHasOcclusionMap);

    // Flip the geometric normal on back faces of doubleSided geometry so
    // the lighting math sees the surface from the correct side. Done
    // BEFORE perturbNormal because the derivative-based cotangentFrame
    // builds T and B from cross products with N; flipping N first means
    // the whole tangent frame mirrors consistently, so normal-map data
    // lands the right way up on the back face.
    vec3 N0 = normalize(worldNormal);
    if (uDoubleSided > 0.5 && !gl_FrontFacing) N0 = -N0;
    vec3 N = perturbNormal(N0, worldPosition, uv);
    vec3 V = normalize(uCameraPosition - worldPosition);
    vec3 L = normalize(-uSunDirection);
    vec3 H = normalize(V + L);

    float NdotL = max(dot(N, L), 0.0);
    float NdotV = max(dot(N, V), 0.0);
    float NdotH = max(dot(N, H), 0.0);
    float VdotH = max(dot(V, H), 0.0);

    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    // Horizon fade. Same factor used on the indirect specular and reused
    // on the direct specular terms below to kill the white-halo rim that
    // Schlick Fresnel produces at NdotV -> 0. Computed once per fragment.
    float horizon = smoothstep(0.0, max(uHorizonFadeStart, 0.001), NdotV);
    horizon = mix(1.0 - uHorizonFadeStrength, 1.0, horizon);

    // --- Direct sun contribution -----------------------------------------
    float D = distributionGGX(NdotH, roughness);
    float G = geometrySmith(NdotV, NdotL, roughness);
    vec3  F = fresnelSchlick(VdotH, F0);
    vec3 specular = (D * G * F) / max(4.0 * NdotV * NdotL, 0.001);
    specular *= horizon;
    vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);
    float shadow = sampleShadowCsm(NdotL);
    // Direct-lighting micro-shadowing (Drobot-style). max(ao, 1 - NdotL)
    // means AO only modulates direct light on surfaces facing the light
    // (NdotL high -> 1-NdotL low -> ao wins, creases darken); surfaces
    // facing away (NdotL ~ 0 -> 1-NdotL ~ 1 -> 1 wins, no extra darken)
    // are already shadowed by the cosine term, so layering AO there would
    // just gray them out. Spec-correct AO is indirect-only; this is the
    // Frostbite/Marmoset-style extension that gives crevices realistic
    // contact darkening under direct light too.
    float aoMicroSun = max(ao, 1.0 - NdotL);
    vec3 direct = (kD * albedo / PI + specular) * uSunColor * NdotL * shadow * aoMicroSun;

    // --- Point lights ----------------------------------------------------
    int plCount = int(uPointLightCount);
    for (int i = 0; i < MAX_POINT_LIGHTS; ++i)
    {
        if (i >= plCount) break;
        vec3 toLight = uPointLightPositions[i] - worldPosition;
        float dist = length(toLight);
        float range = uPointLightRanges[i];
        if (dist >= range) continue;

        vec3 Lp = toLight / max(dist, 1e-4);
        vec3 Hp = normalize(V + Lp);
        float NdotLp = max(dot(N, Lp), 0.0);
        if (NdotLp <= 0.0) continue;
        float NdotHp = max(dot(N, Hp), 0.0);
        float VdotHp = max(dot(V, Hp), 0.0);

        float dr = dist / range;
        float window = clamp(1.0 - dr * dr * dr * dr, 0.0, 1.0);
        float att = window * window / max(dist * dist, 0.01);

        float Dp = distributionGGX(NdotHp, roughness);
        float Gp = geometrySmith(NdotV, NdotLp, roughness);
        vec3  Fp = fresnelSchlick(VdotHp, F0);
        vec3 specP = (Dp * Gp * Fp) / max(4.0 * NdotV * NdotLp, 0.001);
        specP *= horizon * uPointLightSpecScale;
        vec3 kDp = (vec3(1.0) - Fp) * (1.0 - metallic);

        // Sample cube shadow for the first MAX_POINT_SHADOWS lights. Direction
        // is from light to fragment so the cube lookup hits the same face the
        // shadow_cube pass rasterised.
        float shadowP = 1.0;
        if (i < MAX_POINT_SHADOWS)
        {
            shadowP = samplePointShadow(i, -toLight, dist);
        }

        float aoMicroP = max(ao, 1.0 - NdotLp);
        direct += (kDp * albedo / PI + specP) * uPointLightColors[i] * NdotLp * att * shadowP * aoMicroP;
    }

    // --- Indirect (IBL) --------------------------------------------------
    // Lazarov 2013 Fresnel: rough surfaces don't get their F lifted to 1.0
    // at grazing angles, fixing the "everything looks wet at glancing
    // angles" artefact plain Schlick produces on IBL.
    vec3 F_ibl = blix_fresnelLazarov(NdotV, F0, roughness);
    vec3 kD_ibl = (vec3(1.0) - F_ibl) * (1.0 - metallic);

    vec3 irradiance = sampleEnvIrradiance(N) * uSkyTint;
    vec3 indirectDiffuse = kD_ibl * albedo * irradiance;

    // --- Split-sum specular IBL (Karis 2014) ----------------------------
    // prefilteredColor = GGX-convolved env at this roughness;
    // brdf.rg     = LUT-stored scale/bias terms.
    // Their product is the full split-sum specular integrand for any F0,
    // which is significantly more accurate than the old
    // `F_ibl * envSpec` hack -- the GGX response shape is baked into both
    // the cube mip chain and the BRDF LUT, so Fresnel-at-grazing and
    // visibility falloff come out right at every roughness.
    vec3 R = reflect(-V, N);
    float lod = roughness * max(uSpecularPrefilterMipCount - 1.0, 0.0);
    vec3 prefilteredColor = textureLod(uSpecularPrefilter, R, lod).rgb * uSkyTint;
    vec2 brdf = texture(
        uBrdfLut,
        vec2(clamp(NdotV, 0.0, 1.0), clamp(roughness, 0.0, 1.0))).rg;
    float specAttenuation = 1.0 - roughness * roughness * uIblSpecAttenuation;
    // horizon was computed once at the top of main(); reused here so the
    // silhouette white-halo from grazing-Fresnel doesn't reappear.
    vec3 indirectSpecular = prefilteredColor * (F0 * brdf.x + brdf.y)
                          * specAttenuation * horizon;

    vec3 metalFloor = F0 * irradiance * uMetalFloor * metallic;
    indirectSpecular = max(indirectSpecular, metalFloor * horizon);

    indirectDiffuse *= uIblDiffuseBoost;

    float indirectMix = uIndirectShadowBase + uIndirectShadowRange * shadow;
    // Baked AO modulates the indirect (ambient) term in full per glTF spec.
    // Crevices that occlude the sky stay dark even when no direct light
    // reaches them; flat surfaces sample ao ~ 1 and are unaffected.
    vec3 indirect = (indirectDiffuse + indirectSpecular) * indirectMix * ao;

    vec3 emissive = pow(texture(uEmissive, uv).rgb, vec3(2.2))
        * uEmissiveFactor * uEmissiveBoost;

    // Output linear HDR. The composite pass downstream applies ACES + gamma
    // to the combined HDR scene + bloom buffer. uExposure stays here so
    // shadow-driven changes happen in scene space and look right under
    // bloom (the bloom sees the same brightness as the eventual tonemap).
    vec3 hdr = (direct + indirect) * uExposure + emissive;
    // Cascade visualization for debugging: tint by which cascade this
    // fragment falls into (cascade 0 = red, 1 = green, 2 = blue, beyond = grey).
    if (uVisualizeCascades > 0.5)
    {
        int cIdx = -1;
        for (int i = 0; i < CASCADE_COUNT; ++i)
        {
            if (viewDepth <= uCascadeSplits[i + 1]) { cIdx = i; break; }
        }
        vec3 cTint = vec3(0.3);
        if (cIdx == 0) cTint = vec3(0.6, 0.2, 0.2);
        else if (cIdx == 1) cTint = vec3(0.2, 0.6, 0.2);
        else if (cIdx == 2) cTint = vec3(0.2, 0.2, 0.6);
        hdr = mix(hdr, cTint * 5.0, 0.6);
    }

    fragColor = vec4(hdr, 1.0);
    fragMaterial = vec4(roughness, 0.0, 0.0, 1.0);
}
