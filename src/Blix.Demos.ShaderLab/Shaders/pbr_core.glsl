// pbr_core.glsl
// Shared PBR + lighting code consumed by both cube.frag (static lit) and
// skin.lit.frag (skinned lit). Each frag declares its own varyings, outputs,
// and perturbNormal (because the TBN sourcing differs), then calls EvaluatePbr
// for the full BRDF + direct/indirect lighting evaluation.
//
// Inlined by Blix.Graphics.GlslPreprocessor at shader compile time. Do not
// declare varyings / fragment outputs here -- those live per-frag.

// --- PBR material inputs (glTF metallic-roughness convention) ----------------
uniform sampler2D uTexture;
uniform sampler2D uNormalMap;
uniform sampler2D uMetallicRoughnessMap;
uniform sampler2DShadow uShadowMap;
uniform samplerCube uEnvMap;
uniform vec4 uBaseColorFactor;
uniform float uMetallicFactor;
uniform float uRoughnessFactor;
uniform float uNormalScale;
uniform float uEnvMapMipCount;

// --- Lighting state ----------------------------------------------------------
uniform vec3 uLightDirection;     // directional sun, "FROM surface TO light"
uniform vec3 uCameraPosition;
uniform float uLightIntensity;
uniform float uAmbientBoost;
uniform float uSkyboxIntensity;

const int kMaxPointLights = 4;
const int kMaxSpotLights = 4;

struct PointLight
{
    vec3 position;
    vec3 color;
    float intensity;
    float range;
};

struct SpotLight
{
    vec3 position;
    vec3 direction;
    vec3 color;
    float intensity;
    float range;
    float innerCos;
    float outerCos;
};

uniform PointLight uPointLights[kMaxPointLights];
uniform SpotLight  uSpotLights [kMaxSpotLights];
uniform float      uPointLightCount;
uniform float      uSpotLightCount;

// Per-spot shadow casting. Up to kMaxSpotShadowCasters spots can cast
// shadows; the demo places shadow-casting spots at the front of uSpotLights so
// uSpotShadowMaps[i] corresponds to uSpotLights[i] for i < uSpotShadowCount.
// Spot lights past uSpotShadowCount don't cast shadows (no map allocated).
const int kMaxSpotShadowCasters = 4;
uniform sampler2DShadow uSpotShadowMaps[kMaxSpotShadowCasters];
uniform mat4            uSpotShadowVPs [kMaxSpotShadowCasters];
uniform float           uSpotShadowCount;

// Per-point cubemap shadow casting. Up to kMaxPointShadowCasters
// point lights cast shadows via a depth cubemap. Same convention as spots:
// shadow-casting point lights live at the front of uPointLights, so
// uPointShadowCubes[i] corresponds to uPointLights[i] for i < uPointShadowCount.
// Far-plane stored per-light because the sampled depth is normalised against
// the cubemap projection's far plane (= light.range).
const int kMaxPointShadowCasters = 2;
uniform samplerCubeShadow uPointShadowCubes[kMaxPointShadowCasters];
uniform float             uPointShadowFarPlanes[kMaxPointShadowCasters];
uniform float             uPointShadowCount;

// --- Constants ---------------------------------------------------------------
const float PI = 3.14159265359;
const float kShadowBias = 0.005;
const float kShadowAmbientFactor = 0.55;
const float kBlockerSearchRadiusTexels = 4.0;
const float kPenumbraScale = 32.0;
const int   kBlockerSamples = 8;

const vec2 kPoissonDisk16[16] = vec2[](
    vec2(-0.94201624, -0.39906216),
    vec2( 0.94558609, -0.76890725),
    vec2(-0.09418410, -0.92938870),
    vec2( 0.34495938,  0.29387760),
    vec2(-0.91588581,  0.45771432),
    vec2(-0.81544232, -0.87912464),
    vec2(-0.38277543,  0.27676845),
    vec2( 0.97484398,  0.75648379),
    vec2( 0.44323325, -0.97511554),
    vec2( 0.53742981, -0.47373420),
    vec2(-0.26496911, -0.41893023),
    vec2( 0.79197514,  0.19090188),
    vec2(-0.24188840,  0.99706507),
    vec2(-0.81409955,  0.91437590),
    vec2( 0.19984126,  0.78641367),
    vec2( 0.14383161, -0.14100790));

// --- Shadow sampling (PCSS) --------------------------------------------------
float hashAngle(vec2 fragCoord)
{
    float h = fract(sin(dot(fragCoord, vec2(12.9898, 78.233))) * 43758.5453);
    return h * 6.2831853;
}

// PCSS sampler parameterised over the shadow map so the same code serves the
// sun (uShadowMap) and each spot (uSpotShadowMaps[i]).
float sampleShadowFactor(sampler2DShadow shadowMap, vec4 coord, float bias)
{
    vec3 projected = coord.xyz / coord.w;
    projected = projected * 0.5 + 0.5;
    if (projected.x < 0.0 || projected.x > 1.0 ||
        projected.y < 0.0 || projected.y > 1.0 ||
        projected.z > 1.0)
    {
        return 1.0;
    }

    float angle = hashAngle(gl_FragCoord.xy);
    float cosA = cos(angle);
    float sinA = sin(angle);
    mat2 rot = mat2(cosA, -sinA, sinA, cosA);

    vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0));
    float refDepth = projected.z - bias;

    int blockers = 0;
    for (int i = 0; i < kBlockerSamples; ++i)
    {
        vec2 offset = rot * kPoissonDisk16[i] * texelSize * kBlockerSearchRadiusTexels;
        float occlusion = 1.0 - texture(shadowMap, vec3(projected.xy + offset, refDepth));
        blockers += int(occlusion > 0.5);
    }
    if (blockers == 0) return 1.0;

    float blockerFraction = float(blockers) / float(kBlockerSamples);
    float penumbraTexels = max(blockerFraction * kPenumbraScale, 1.0);

    float accum = 0.0;
    for (int i = 0; i < 16; ++i)
    {
        vec2 offset = rot * kPoissonDisk16[i] * texelSize * penumbraTexels;
        accum += texture(shadowMap, vec3(projected.xy + offset, refDepth));
    }
    return accum / 16.0;
}

// --- Derivative-based tangent frame (cube.frag uses; skin.lit.frag's no-tangent fallback uses) --
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
    float invmax = inversesqrt(max(dot(T, T), dot(B, B)));
    return mat3(T * invmax, B * invmax, N);
}

// --- PBR BRDF (Cook-Torrance + Lambert) --------------------------------------
float DistributionGGX(float NdotH, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH2 = NdotH * NdotH;
    float denom = NdotH2 * (a2 - 1.0) + 1.0;
    return a2 / (PI * denom * denom);
}

float GeometrySchlickGGX(float NdotX, float k)
{
    return NdotX / (NdotX * (1.0 - k) + k);
}
float GeometrySmith(float NdotV, float NdotL, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return GeometrySchlickGGX(NdotV, k) * GeometrySchlickGGX(NdotL, k);
}

vec3 FresnelSchlick(float cosTheta, vec3 F0)
{
    float oneMinus = 1.0 - cosTheta;
    return F0 + (1.0 - F0) * (oneMinus * oneMinus * oneMinus * oneMinus * oneMinus);
}

vec3 EvaluateLight(vec3 N, vec3 V, vec3 L, vec3 radiance, vec3 albedo, vec3 F0, float metallic, float roughness)
{
    vec3 H = normalize(L + V);
    float NdotL = max(dot(N, L), 0.0);
    if (NdotL <= 0.0) return vec3(0.0);

    float NdotV = max(dot(N, V), 0.0);
    float NdotH = max(dot(N, H), 0.0);
    float HdotV = max(dot(H, V), 0.0);

    float D = DistributionGGX(NdotH, roughness);
    float G = GeometrySmith(NdotV, NdotL, roughness);
    vec3 F = FresnelSchlick(HdotV, F0);

    vec3 spec = (D * G * F) / max(4.0 * NdotV * NdotL, 0.001);
    vec3 kS = F;
    vec3 kD = (vec3(1.0) - kS) * (1.0 - metallic);

    vec3 diffuse = kD * albedo;
    return (diffuse + spec) * radiance * NdotL;
}

// --- IBL (simple cubemap-mip approximation) ----------------------------------
// Env cubemap is Rgba16F linear HDR (no sRGB decode required).
vec3 SampleEnvSpecular(vec3 R, float roughness)
{
    float lod = roughness * max(uEnvMapMipCount - 1.0, 0.0);
    return textureLod(uEnvMap, R, lod).rgb * uSkyboxIntensity;
}

vec3 SampleEnvIrradiance(vec3 N)
{
    float lod = max(uEnvMapMipCount - 1.0, 0.0);
    // * PI: the cos-weighted hemispherical irradiance from a uniform radiance
    // field equals PI * radiance. The single-mip sample approximates radiance.
    return textureLod(uEnvMap, N, lod).rgb * uSkyboxIntensity * PI;
}

// PCSS-style soft point shadow. Same three-stage structure as the
// 2D PCSS (blocker search, penumbra estimation, variable-kernel PCF), adapted
// to cubemap direction-space sampling. Offsets live on a disc perpendicular to
// the base sampling direction; the disc gets rotated per fragment via the
// existing hashAngle so adjacent fragments dither rather than band.
//
// `searchRadius` and `penumbraScale` are direction-space scalars (effectively
// "how far in 3D world space to offset the sample direction"); tuned by hand
// to match the 2D PCSS feel.
const float kPointBlockerSearchRadius = 0.05;
const float kPointPenumbraScale = 0.30;

float samplePointShadow(samplerCubeShadow cube, vec3 toFrag, float farPlane, float bias)
{
    float dist = length(toFrag);
    if (dist < 1e-4) return 1.0;
    vec3 baseDir = toFrag / dist;
    float refDepth = clamp(dist / farPlane, 0.0, 1.0) - bias;

    // Build a tangent basis perpendicular to baseDir. Choose the axis-aligned
    // up vector that's *least* aligned with baseDir so the cross-product stays
    // well-conditioned.
    vec3 up = abs(baseDir.y) > 0.95 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 tangent = normalize(cross(up, baseDir));
    vec3 bitangent = cross(baseDir, tangent);

    // Per-fragment rotation of the Poisson disk in the tangent plane. Same
    // pattern the 2D path uses to hide the regularity of any single snapshot.
    float angle = hashAngle(gl_FragCoord.xy);
    float cosA = cos(angle);
    float sinA = sin(angle);

    // Stage 1: blocker search. 8 taps inside a small disc at the base direction.
    int blockers = 0;
    for (int i = 0; i < kBlockerSamples; ++i)
    {
        vec2 p = kPoissonDisk16[i];
        vec2 rp = vec2(p.x * cosA - p.y * sinA, p.x * sinA + p.y * cosA);
        vec3 offset = (tangent * rp.x + bitangent * rp.y) * kPointBlockerSearchRadius;
        vec3 sampleDir = baseDir + offset;
        float occlusion = 1.0 - texture(cube, vec4(normalize(sampleDir), refDepth));
        blockers += int(occlusion > 0.5);
    }
    if (blockers == 0) return 1.0;

    // Stage 2: penumbra estimation. Blocker fraction proxies for occluder
    // proximity; the kernel widens as more of the search neighbourhood is
    // shadowed.
    float blockerFraction = float(blockers) / float(kBlockerSamples);
    float penumbra = max(blockerFraction * kPointPenumbraScale, kPointBlockerSearchRadius);

    // Stage 3: PCF with variable-width kernel. 16 taps to match the 2D path's
    // smoothness, projected onto the same tangent disc.
    float accum = 0.0;
    for (int i = 0; i < 16; ++i)
    {
        vec2 p = kPoissonDisk16[i];
        vec2 rp = vec2(p.x * cosA - p.y * sinA, p.x * sinA + p.y * cosA);
        vec3 offset = (tangent * rp.x + bitangent * rp.y) * penumbra;
        vec3 sampleDir = baseDir + offset;
        accum += texture(cube, vec4(normalize(sampleDir), refDepth));
    }
    return accum / 16.0;
}

// --- Per-light attenuation profiles ------------------------------------------
float DistanceAttenuation(float distance, float range)
{
    float normDist = distance / max(range, 0.0001);
    float window = clamp(1.0 - normDist * normDist * normDist * normDist, 0.0, 1.0);
    return window * window / max(distance * distance, 0.01);
}

float ConeAttenuation(vec3 toFrag, vec3 lightDir, float innerCos, float outerCos)
{
    float cosAngle = dot(normalize(lightDir), normalize(toFrag));
    return smoothstep(outerCos, innerCos, cosAngle);
}

// --- The whole-fragment PBR evaluation ---------------------------------------
// Returns the three MRT outputs the scene pass expects. Both cube.frag and
// skin.lit.frag's main() call this after they've computed their own N via
// per-frag perturbNormal.
struct PbrFragmentOutputs
{
    vec4 color;
    vec4 luminance;
    vec4 normal;
};

PbrFragmentOutputs EvaluatePbr(vec3 N, vec3 worldPos, vec2 uv, vec4 shadowCoord)
{
    vec4 baseColorSrgb = texture(uTexture, uv) * uBaseColorFactor;
    vec3 albedo = pow(baseColorSrgb.rgb, vec3(2.2));

    vec3 mrSample = texture(uMetallicRoughnessMap, uv).rgb;
    float metallic = clamp(mrSample.b * uMetallicFactor, 0.0, 1.0);
    float roughness = clamp(mrSample.g * uRoughnessFactor, 0.04, 1.0);

    vec3 V = normalize(uCameraPosition - worldPos);
    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    // Directional sun (shadowed).
    vec3 sunDirection = normalize(uLightDirection);
    float rawDot = dot(N, sunDirection);
    float slopeBias = kShadowBias * clamp(2.0 / max(rawDot, 0.1), 1.0, 20.0);
    float sunShadow = rawDot > 0.05 ? sampleShadowFactor(uShadowMap, shadowCoord, slopeBias) : 1.0;
    vec3 sunRadiance = vec3(uLightIntensity);
    vec3 sunContribution = EvaluateLight(N, V, sunDirection, sunRadiance, albedo, F0, metallic, roughness) * sunShadow;

    // Point lights. Shadow-casting points (first uPointShadowCount of the array)
    // sample a depth cubemap; the rest contribute unshadowed.
    vec3 pointContribution = vec3(0.0);
    int pointCount = int(uPointLightCount);
    int pointShadowCount = int(uPointShadowCount);
    for (int i = 0; i < pointCount; ++i)
    {
        PointLight light = uPointLights[i];
        vec3 toLight = light.position - worldPos;
        float dist = length(toLight);
        if (dist > light.range) continue;
        vec3 L = toLight / max(dist, 0.0001);
        float atten = DistanceAttenuation(dist, light.range);
        vec3 radiance = light.color * light.intensity * atten;

        // Per-point shadow factor. Unrolled because dynamic samplerCubeShadow
        // indexing isn't portable across drivers. Vector FROM light TO fragment
        // is the cubemap sampling direction.
        float pointShadow = 1.0;
        float pointBias = 0.005 * clamp(2.0 / max(dot(N, L), 0.1), 1.0, 20.0);
        vec3 pointSampleDir = worldPos - light.position;
        if (i == 0 && pointShadowCount > 0)
        {
            pointShadow = samplePointShadow(uPointShadowCubes[0], pointSampleDir, uPointShadowFarPlanes[0], pointBias);
        }
        else if (i == 1 && pointShadowCount > 1)
        {
            pointShadow = samplePointShadow(uPointShadowCubes[1], pointSampleDir, uPointShadowFarPlanes[1], pointBias);
        }

        pointContribution += EvaluateLight(N, V, L, radiance, albedo, F0, metallic, roughness) * pointShadow;
    }

    // Spot lights. Shadow casters live at the front of uSpotLights (indices
    // [0, uSpotShadowCount)) and have a corresponding entry in uSpotShadowMaps /
    // uSpotShadowVPs. Indices 0..3 are unrolled because dynamic sampler array
    // indexing isn't reliable across drivers.
    vec3 spotContribution = vec3(0.0);
    int spotCount = int(uSpotLightCount);
    int spotShadowCount = int(uSpotShadowCount);
    for (int i = 0; i < spotCount; ++i)
    {
        SpotLight light = uSpotLights[i];
        vec3 toLight = light.position - worldPos;
        float dist = length(toLight);
        if (dist > light.range) continue;
        vec3 L = toLight / max(dist, 0.0001);
        float distAtten = DistanceAttenuation(dist, light.range);
        float coneAtten = ConeAttenuation(-L, light.direction, light.innerCos, light.outerCos);
        if (coneAtten <= 0.0) continue;
        vec3 radiance = light.color * light.intensity * distAtten * coneAtten;

        // Per-spot shadow factor. Bias uses N.L like the sun's slope bias.
        float spotShadow = 1.0;
        float spotBias = kShadowBias * clamp(2.0 / max(dot(N, L), 0.1), 1.0, 20.0);
        if (i == 0 && spotShadowCount > 0)
        {
            vec4 sc = uSpotShadowVPs[0] * vec4(worldPos, 1.0);
            spotShadow = sampleShadowFactor(uSpotShadowMaps[0], sc, spotBias);
        }
        else if (i == 1 && spotShadowCount > 1)
        {
            vec4 sc = uSpotShadowVPs[1] * vec4(worldPos, 1.0);
            spotShadow = sampleShadowFactor(uSpotShadowMaps[1], sc, spotBias);
        }
        else if (i == 2 && spotShadowCount > 2)
        {
            vec4 sc = uSpotShadowVPs[2] * vec4(worldPos, 1.0);
            spotShadow = sampleShadowFactor(uSpotShadowMaps[2], sc, spotBias);
        }
        else if (i == 3 && spotShadowCount > 3)
        {
            vec4 sc = uSpotShadowVPs[3] * vec4(worldPos, 1.0);
            spotShadow = sampleShadowFactor(uSpotShadowMaps[3], sc, spotBias);
        }

        spotContribution += EvaluateLight(N, V, L, radiance, albedo, F0, metallic, roughness) * spotShadow;
    }

    vec3 direct = sunContribution + pointContribution + spotContribution;

    // Indirect (IBL).
    vec3 R = reflect(-V, N);
    float NdotV = max(dot(N, V), 0.0);
    vec3 F_ibl = FresnelSchlick(NdotV, F0);
    vec3 kS_ibl = F_ibl;
    vec3 kD_ibl = (vec3(1.0) - kS_ibl) * (1.0 - metallic);

    vec3 irradiance = SampleEnvIrradiance(N);
    vec3 indirectDiffuse = kD_ibl * albedo * irradiance;

    vec3 envSpec = SampleEnvSpecular(R, roughness);
    vec3 indirectSpecular = envSpec * kS_ibl;

    float ambientShadowMix = mix(kShadowAmbientFactor, 1.0, sunShadow);
    vec3 indirect = (indirectDiffuse + indirectSpecular) * uAmbientBoost * ambientShadowMix;

    vec3 lit = direct + indirect;
    float luma = dot(lit, vec3(0.2126, 0.7152, 0.0722));

    PbrFragmentOutputs result;
    result.color = vec4(lit, baseColorSrgb.a);
    result.luminance = vec4(vec3(luma), baseColorSrgb.a);
    result.normal = vec4(N * 0.5 + 0.5, 1.0);
    return result;
}
