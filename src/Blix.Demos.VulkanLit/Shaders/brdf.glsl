// Cook-Torrance BRDF (metallic-roughness workflow).
//
// Pieces:
//   distributionGGX   — Trowbridge-Reitz normal distribution
//   geometrySmith     — Schlick-GGX masking/shadowing (Smith form)
//   fresnelSchlick    — direct-light Fresnel
//   brdf()            — one light's outgoing radiance
//   evalSpotBrdf()    — spot-light helper: cone falloff + range atten +
//                       shadow folded into radiance, then brdf().
//
// evalSpotBrdf depends on sampleShadow from shadows.glsl, so include
// shadows.glsl BEFORE this file.

#ifndef PI
#define PI 3.14159265359
#endif

float distributionGGX(vec3 N, vec3 H, float roughness) {
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float d = (NdotH * NdotH) * (a2 - 1.0) + 1.0;
    return a2 / max(PI * d * d, 1e-7);
}

float geometrySchlickGGX(float NdotV, float roughness) {
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return NdotV / (NdotV * (1.0 - k) + k);
}

float geometrySmith(vec3 N, vec3 V, vec3 L, float roughness) {
    return geometrySchlickGGX(max(dot(N, V), 0.0), roughness)
         * geometrySchlickGGX(max(dot(N, L), 0.0), roughness);
}

vec3 fresnelSchlick(float cosTheta, vec3 F0) {
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

// One light's outgoing radiance via Cook-Torrance.
vec3 brdf(vec3 N, vec3 V, vec3 L, vec3 radiance, vec3 albedo,
          float metallic, float roughness, vec3 F0) {
    float NdotL = max(dot(N, L), 0.0);
    if (NdotL <= 0.0) return vec3(0.0);
    // Zero-safe half-vector: V+L == 0 (back-facing surface lit opposite the
    // view) makes normalize(0) = NaN, which the bloom chain then spreads
    // into a screen-wide green wash. Metal's fast-math defeats any isnan()
    // guard, so we must avoid producing the NaN rather than detect it.
    vec3 vl = V + L;
    float vl2 = dot(vl, vl);
    vec3 H = vl2 > 1e-12 ? vl * inversesqrt(vl2) : N;

    float D = distributionGGX(N, H, roughness);
    float G = geometrySmith(N, V, L, roughness);
    vec3 F = fresnelSchlick(max(dot(H, V), 0.0), F0);

    vec3 numerator = D * G * F;
    float denom = 4.0 * max(dot(N, V), 0.0) * NdotL + 1e-4;
    vec3 specular = numerator / denom;

    vec3 kd = (vec3(1.0) - F) * (1.0 - metallic);
    return (kd * albedo / PI + specular) * radiance * NdotL;
}

// One spot light through the BRDF: cone falloff + range atten + shadow
// folded into the light radiance, then Cook-Torrance.
vec3 evalSpotBrdf(vec3 N, vec3 V, vec3 worldPos, mat4 vp,
                  vec4 posRange, vec4 dirCosInner, vec4 colorCosOuter,
                  sampler2D shadowMap, vec3 albedo, float metallic,
                  float roughness, vec3 F0) {
    vec3 toSpot = posRange.xyz - worldPos;
    float dist = length(toSpot);
    vec3 L = toSpot / max(dist, 1e-4);
    float spotCos = dot(-L, normalize(dirCosInner.xyz));
    float cone = smoothstep(colorCosOuter.w, dirCosInner.w, spotCos);
    float range = posRange.w;
    float atten = clamp(1.0 - (dist * dist) / (range * range), 0.0, 1.0);
    float shadow = sampleShadow(shadowMap, vp * vec4(worldPos, 1.0), max(dot(N, L), 0.0));
    vec3 radiance = colorCosOuter.xyz * (cone * atten * shadow);
    return brdf(N, V, L, radiance, albedo, metallic, roughness, F0);
}
