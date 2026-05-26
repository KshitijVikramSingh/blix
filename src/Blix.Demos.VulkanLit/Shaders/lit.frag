#version 450

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    vec3 uSunDirection;
    float uSunIntensity;
    vec3 uAmbientColor;
    float uAmbientIntensity;
    mat4 uSunShadowVP;
    mat4 uSpotViewProj;
    vec4 uSpotPosRange;       // xyz position, w range
    vec4 uSpotDirCosInner;    // xyz direction, w cos(inner)
    vec4 uSpotColorCosOuter;  // xyz color*intensity, w cos(outer)
    vec4 uPointPosFar;        // xyz position, w far plane
    vec4 uPointColorRange;    // xyz color*intensity, w range
    vec4 uLightEnable;        // x sun, y spot, z point (0/1 debug toggles)
} frame;

// Set 1 = per-pass shadow maps.
layout(set = 1, binding = 0) uniform sampler2D uSunShadowMap;
layout(set = 1, binding = 1) uniform sampler2D uSpotShadowMap;
layout(set = 1, binding = 2) uniform samplerCube uPointShadowCube;

// Set 2 = per-material.
layout(set = 2, binding = 0) uniform LitMaterial {
    vec4 uTint;
} mat;
layout(set = 2, binding = 1) uniform sampler2D uAlbedo;

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec4 vSunShadowCoord;
layout(location = 3) in vec3 vWorldPos;
layout(location = 4) in vec4 vSpotShadowCoord;

layout(location = 0) out vec4 outColor;

// Shared shadow-map lookup: project to NDC, map to [0,1] UV, compare depth
// with a slope-scaled bias, range-check to treat out-of-frustum as lit.
float sampleShadow(sampler2D map, vec4 coord, float NdotL) {
    vec3 ndc = coord.xyz / coord.w;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float current = ndc.z;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 ||
        current < 0.0 || current > 1.0) {
        return 1.0;
    }
    float bias = mix(0.005, 0.0005, NdotL);
    float sampled = texture(map, uv).r;
    return (current - bias > sampled) ? 0.0 : 1.0;
}

void main() {
    vec3 N = normalize(vNormal);
    vec3 albedo = texture(uAlbedo, vUv).rgb * mat.uTint.rgb;

    // --- Sun (directional) ----------------------------------------------
    vec3 sunL = -normalize(frame.uSunDirection);
    float sunNdotL = max(dot(N, sunL), 0.0);
    float sunShadow = sampleShadow(uSunShadowMap, vSunShadowCoord, sunNdotL);
    vec3 sun = sunNdotL * frame.uSunIntensity * sunShadow * vec3(1.0) * frame.uLightEnable.x;

    // --- Spot light -----------------------------------------------------
    vec3 toSpot = frame.uSpotPosRange.xyz - vWorldPos;
    float spotDist = length(toSpot);
    vec3 spotL = toSpot / max(spotDist, 1e-4);
    float spotNdotL = max(dot(N, spotL), 0.0);
    // Cone: angle between the spotlight's forward axis and the direction
    // FROM the light TO the fragment (-spotL). Smooth edge inner→outer.
    float spotCos = dot(-spotL, normalize(frame.uSpotDirCosInner.xyz));
    float cone = smoothstep(frame.uSpotColorCosOuter.w, frame.uSpotDirCosInner.w, spotCos);
    // Inverse-square-ish range falloff, clamped so it dies at uRange.
    float range = frame.uSpotPosRange.w;
    float atten = clamp(1.0 - (spotDist * spotDist) / (range * range), 0.0, 1.0);
    float spotShadow = sampleShadow(uSpotShadowMap, vSpotShadowCoord, spotNdotL);
    vec3 spot = frame.uSpotColorCosOuter.xyz * (spotNdotL * cone * atten * spotShadow * frame.uLightEnable.y);

    // --- Point light (omnidirectional cube shadow) ----------------------
    vec3 fromPoint = vWorldPos - frame.uPointPosFar.xyz;
    float pointDist = length(fromPoint);
    vec3 pointL = -fromPoint / max(pointDist, 1e-4);
    float pointNdotL = max(dot(N, pointL), 0.0);
    float pointRange = frame.uPointColorRange.w;
    float pointAtten = clamp(1.0 - (pointDist * pointDist) / (pointRange * pointRange), 0.0, 1.0);
    // Cube stores normalized linear distance; compare with a small bias.
    float currentPointDist = pointDist / frame.uPointPosFar.w;
    float sampledPointDist = texture(uPointShadowCube, fromPoint).r;
    float pointShadow = (currentPointDist - 0.02 > sampledPointDist) ? 0.0 : 1.0;
    vec3 point = frame.uPointColorRange.xyz * (pointNdotL * pointAtten * pointShadow * frame.uLightEnable.z);

    vec3 ambient = frame.uAmbientColor * frame.uAmbientIntensity;
    vec3 lit = albedo * (ambient + sun + spot + point);
    outColor = vec4(lit, mat.uTint.a);
}
