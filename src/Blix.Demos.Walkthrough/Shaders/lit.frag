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
in vec4 shadowCoord;

out vec4 fragColor;

uniform sampler2D uAlbedo;
uniform sampler2D uNormalMap;
uniform sampler2D uMetallicRoughness;
uniform sampler2D uEmissive;
uniform sampler2DShadow uShadowMap;
// HDR linear cubemap (no sRGB decode). Auto-generated mips approximate a
// roughness prefilter: sample at lod = roughness * (mipCount - 1) for spec,
// at lod = mipCount - 1 for diffuse irradiance (most-blurred mip is close
// enough to the cosine-weighted hemisphere for a low-frequency sky).
uniform samplerCube uEnvMap;
uniform float uEnvMapMipCount;

uniform vec4  uBaseColorFactor;
uniform vec3  uEmissiveFactor;
uniform float uMetallicFactor;
uniform float uRoughnessFactor;
uniform float uNormalScale;
uniform float uHasMetallicMap;

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

const float PI = 3.14159265;

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

// --- Cook-Torrance --------------------------------------------------------
float distributionGGX(float NdotH, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * denom * denom);
}

float geometrySchlickGGX(float NdotV, float k)
{
    return NdotV / (NdotV * (1.0 - k) + k);
}

float geometrySmith(float NdotV, float NdotL, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return geometrySchlickGGX(NdotV, k) * geometrySchlickGGX(NdotL, k);
}

vec3 fresnelSchlick(float cosTheta, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
}

// --- IBL via cubemap mip approximation ------------------------------------
vec3 sampleEnvSpecular(vec3 R, float roughness)
{
    float lod = roughness * max(uEnvMapMipCount - 1.0, 0.0);
    return textureLod(uEnvMap, R, lod).rgb;
}

vec3 sampleEnvIrradiance(vec3 N)
{
    // Smallest mip approximates a uniformly-blurred sky. Multiply by PI so
    // the cosine-weighted hemispherical irradiance from that radiance field
    // comes out right (E = PI * L for uniform L).
    float lod = max(uEnvMapMipCount - 1.0, 0.0);
    return textureLod(uEnvMap, N, lod).rgb * PI;
}

// --- Shadow PCF -----------------------------------------------------------
float sampleShadow(vec4 shadowProj, float NdotL)
{
    vec3 coords = shadowProj.xyz / shadowProj.w;
    coords = coords * 0.5 + 0.5;
    if (coords.z > 1.0) return 1.0;
    if (coords.x < 0.0 || coords.x > 1.0) return 1.0;
    if (coords.y < 0.0 || coords.y > 1.0) return 1.0;

    float bias = max(0.0015 * (1.0 - NdotL), 0.0005);
    float biasedZ = coords.z - bias;

    float sum = 0.0;
    vec2 texel = 1.0 / vec2(textureSize(uShadowMap, 0));
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            vec2 off = vec2(x, y) * texel;
            sum += texture(uShadowMap, vec3(coords.xy + off, biasedZ));
        }
    }
    return sum / 9.0;
}

// --- ACES filmic tonemap (Narkowicz 2015) ---------------------------------
vec3 acesFilm(vec3 x)
{
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main()
{
    vec2 uv = vec2(texCoord.x, 1.0 - texCoord.y);

    vec4 albedoSample = texture(uAlbedo, uv) * uBaseColorFactor;
    if (albedoSample.a < 0.5) discard;
    vec3 albedo = pow(albedoSample.rgb, vec3(2.2));

    vec3 mrSample = texture(uMetallicRoughness, uv).rgb;
    float metallic = mix(uMetallicFactor, mrSample.b * uMetallicFactor, uHasMetallicMap);
    float roughness = mix(uRoughnessFactor, mrSample.g * uRoughnessFactor, uHasMetallicMap);
    roughness = clamp(roughness, 0.05, 1.0);

    vec3 N = perturbNormal(normalize(worldNormal), worldPosition, uv);
    vec3 V = normalize(uCameraPosition - worldPosition);
    vec3 L = normalize(-uSunDirection);
    vec3 H = normalize(V + L);

    float NdotL = max(dot(N, L), 0.0);
    float NdotV = max(dot(N, V), 0.0);
    float NdotH = max(dot(N, H), 0.0);
    float VdotH = max(dot(V, H), 0.0);

    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    // --- Direct sun contribution -----------------------------------------
    float D = distributionGGX(NdotH, roughness);
    float G = geometrySmith(NdotV, NdotL, roughness);
    vec3  F = fresnelSchlick(VdotH, F0);
    vec3 specular = (D * G * F) / max(4.0 * NdotV * NdotL, 0.001);
    vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);
    float shadow = sampleShadow(shadowCoord, NdotL);
    vec3 direct = (kD * albedo / PI + specular) * uSunColor * NdotL * shadow;

    // --- Indirect (IBL) --------------------------------------------------
    // Fresnel-with-roughness (Lazarov 2013): rough surfaces don't get their
    // F lifted to 1.0 at grazing, fixing the "everything looks wet at
    // glancing angles" artefact you get from plain Schlick on IBL. Computed
    // component-wise to dodge a macOS GLSL parser issue with max(vec3, vec3)
    // when one operand is a vec3-from-scalar.
    vec3 invR = vec3(1.0 - roughness);
    vec3 F90 = vec3(
        max(invR.x, F0.x),
        max(invR.y, F0.y),
        max(invR.z, F0.z));
    vec3 F_ibl = F0 + (F90 - F0) * pow(1.0 - NdotV, 5.0);
    vec3 kD_ibl = (vec3(1.0) - F_ibl) * (1.0 - metallic);

    vec3 irradiance = sampleEnvIrradiance(N);
    vec3 indirectDiffuse = kD_ibl * albedo * irradiance;

    vec3 R = reflect(-V, N);
    vec3 envSpec = sampleEnvSpecular(R, roughness);
    // Roughness-dependent specular attenuation. Even with Fresnel-with-
    // roughness, the raw envSpec stays bright for rough surfaces and reads
    // as "shiny cloth". This factor knocks rough specular way down without
    // affecting smooth surfaces.
    float specAttenuation = 1.0 - roughness * roughness * uIblSpecAttenuation;
    vec3 indirectSpecular = envSpec * F_ibl * specAttenuation;

    vec3 metalFloor = F0 * irradiance * uMetalFloor * metallic;
    indirectSpecular = max(indirectSpecular, metalFloor);

    indirectDiffuse *= uIblDiffuseBoost;

    float indirectMix = uIndirectShadowBase + uIndirectShadowRange * shadow;
    vec3 indirect = (indirectDiffuse + indirectSpecular) * indirectMix;

    vec3 emissive = pow(texture(uEmissive, uv).rgb, vec3(2.2))
        * uEmissiveFactor * uEmissiveBoost;

    vec3 hdr = (direct + indirect) * uExposure + emissive;

    vec3 mapped = acesFilm(hdr);
    vec3 gamma = pow(mapped, vec3(1.0 / 2.2));
    fragColor = vec4(gamma, 1.0);
}
