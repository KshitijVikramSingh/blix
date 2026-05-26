#version 450

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
} frame;

// Set 1 = per-pass shadow maps. uSpotShadowMaps is a Count=2 array.
layout(set = 1, binding = 0) uniform sampler2D uSunShadowMap;
layout(set = 1, binding = 1) uniform sampler2D uSpotShadowMaps[2];
layout(set = 1, binding = 2) uniform samplerCube uPointShadowCube;

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

const float PI = 3.14159265359;

// 3×3 PCF: average 9 depth comparisons one texel apart for a soft edge
// (and to kill the single-tap shimmer under camera motion).
float sampleShadow(sampler2D map, vec4 coord, float NdotL) {
    vec3 ndc = coord.xyz / coord.w;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float current = ndc.z;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 ||
        current < 0.0 || current > 1.0) {
        return 1.0;
    }
    float bias = mix(0.005, 0.0005, NdotL);
    vec2 texel = 1.0 / vec2(textureSize(map, 0));
    float sum = 0.0;
    for (int x = -1; x <= 1; x++)
    for (int y = -1; y <= 1; y++) {
        float s = texture(map, uv + vec2(x, y) * texel).r;
        sum += (current - bias > s) ? 0.0 : 1.0;
    }
    return sum / 9.0;
}

// Disk PCF for the omnidirectional cube shadow: 20 fixed offset directions
// scaled by a distance-growing radius. currentNorm = dist/far.
const vec3 kCubeOffsets[20] = vec3[](
    vec3( 1, 1, 1), vec3( 1,-1, 1), vec3(-1,-1, 1), vec3(-1, 1, 1),
    vec3( 1, 1,-1), vec3( 1,-1,-1), vec3(-1,-1,-1), vec3(-1, 1,-1),
    vec3( 1, 1, 0), vec3( 1,-1, 0), vec3(-1,-1, 0), vec3(-1, 1, 0),
    vec3( 1, 0, 1), vec3(-1, 0, 1), vec3( 1, 0,-1), vec3(-1, 0,-1),
    vec3( 0, 1, 1), vec3( 0,-1, 1), vec3( 0,-1,-1), vec3( 0, 1,-1));

float samplePointShadow(samplerCube cube, vec3 fromLight, float currentNorm, float dist) {
    float bias = 0.02;
    float radius = (1.0 + dist * 0.1) * 0.01;
    float sum = 0.0;
    for (int i = 0; i < 20; i++) {
        float s = texture(cube, fromLight + kCubeOffsets[i] * radius).r;
        sum += (currentNorm - bias > s) ? 0.0 : 1.0;
    }
    return sum / 20.0;
}

// --- Cook-Torrance BRDF terms (metallic-roughness workflow) -------------
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

// Cotangent-frame normal mapping (Schüler). Builds a TBN from screen-space
// derivatives of world position + UV — no per-vertex tangent needed, so it
// works uniformly on the cube, ground, spheres, and skinned mesh. A flat
// normal map (0,0,1) leaves N unchanged.
vec3 perturbNormal(vec3 N, vec3 worldPos, vec2 uv, vec3 tangentNormal) {
    vec3 dp1 = dFdx(worldPos);
    vec3 dp2 = dFdy(worldPos);
    vec2 duv1 = dFdx(uv);
    vec2 duv2 = dFdy(uv);
    vec3 dp2perp = cross(dp2, N);
    vec3 dp1perp = cross(N, dp1);
    vec3 T = dp2perp * duv1.x + dp1perp * duv2.x;
    vec3 B = dp2perp * duv1.y + dp1perp * duv2.y;
    float invmax = inversesqrt(max(dot(T, T), dot(B, B)));
    mat3 TBN = mat3(T * invmax, B * invmax, N);
    return normalize(TBN * tangentNormal);
}

// One light's outgoing radiance via Cook-Torrance.
vec3 brdf(vec3 N, vec3 V, vec3 L, vec3 radiance, vec3 albedo,
          float metallic, float roughness, vec3 F0) {
    vec3 H = normalize(V + L);
    float NdotL = max(dot(N, L), 0.0);
    if (NdotL <= 0.0) return vec3(0.0);

    float D = distributionGGX(N, H, roughness);
    float G = geometrySmith(N, V, L, roughness);
    vec3 F = fresnelSchlick(max(dot(H, V), 0.0), F0);

    vec3 numerator = D * G * F;
    float denom = 4.0 * max(dot(N, V), 0.0) * NdotL + 1e-4;
    vec3 specular = numerator / denom;

    vec3 kd = (vec3(1.0) - F) * (1.0 - metallic);
    return (kd * albedo / PI + specular) * radiance * NdotL;
}

// One spot light through the BRDF: cone falloff + range atten + shadow,
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

void main() {
    vec3 Ngeom = normalize(vNormal);
    // Tangent-space normal from the map, scaled by normalScale (uMatParams.z).
    // Flat maps + scale 0 both reduce to the geometric normal.
    vec3 tn = texture(uNormalMap, vUv).xyz * 2.0 - 1.0;
    tn.xy *= pc.uMatParams.z;
    vec3 N = perturbNormal(Ngeom, vWorldPos, vUv, normalize(tn));
    vec3 V = normalize(frame.uCameraPos.xyz - vWorldPos);
    vec3 albedo = texture(uAlbedo, vUv).rgb * mat.uTint.rgb;
    float metallic = clamp(pc.uMatParams.x, 0.0, 1.0);
    float roughness = clamp(pc.uMatParams.y, 0.04, 1.0);
    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    vec3 Lo = vec3(0.0);

    // Sun (directional).
    {
        vec3 L = -normalize(frame.uSunDirection);
        float shadow = sampleShadow(uSunShadowMap, vSunShadowCoord, max(dot(N, L), 0.0));
        vec3 radiance = vec3(frame.uSunIntensity) * shadow * frame.uLightEnable.x;
        Lo += brdf(N, V, L, radiance, albedo, metallic, roughness, F0);
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

    // Flat ambient (replaced by IBL in a later step). Metals have no diffuse.
    vec3 ambient = albedo * frame.uAmbientColor * frame.uAmbientIntensity * (1.0 - metallic);

    outColor = vec4(ambient + Lo, mat.uTint.a);
}
