#version 450

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    vec3 uSunDirection;
    float uSunIntensity;
    vec3 uAmbientColor;
    float uAmbientIntensity;
    mat4 uSunShadowVP;
    // Two spot lights. Each carries its own VP (for shadow projection) +
    // packed position/range, direction/cos-inner, color*intensity/cos-outer.
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
} frame;

// Set 1 = per-pass shadow maps. uSpotShadowMaps is a Count=2 array binding
// (one map per spot light) — exercises the array-descriptor path.
layout(set = 1, binding = 0) uniform sampler2D uSunShadowMap;
layout(set = 1, binding = 1) uniform sampler2D uSpotShadowMaps[2];
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

layout(location = 0) out vec4 outColor;

// Shared 2D shadow-map lookup: project to NDC, map to [0,1] UV, compare
// depth with a slope-scaled bias, range-check to treat out-of-frustum as lit.
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

// One spot light's contribution. shadowMap is one element of the array.
vec3 evalSpot(vec3 N, vec3 worldPos, mat4 vp,
              vec4 posRange, vec4 dirCosInner, vec4 colorCosOuter,
              sampler2D shadowMap) {
    vec3 toSpot = posRange.xyz - worldPos;
    float dist = length(toSpot);
    vec3 L = toSpot / max(dist, 1e-4);
    float ndotl = max(dot(N, L), 0.0);
    float spotCos = dot(-L, normalize(dirCosInner.xyz));
    float cone = smoothstep(colorCosOuter.w, dirCosInner.w, spotCos);
    float range = posRange.w;
    float atten = clamp(1.0 - (dist * dist) / (range * range), 0.0, 1.0);
    float shadow = sampleShadow(shadowMap, vp * vec4(worldPos, 1.0), ndotl);
    return colorCosOuter.xyz * (ndotl * cone * atten * shadow);
}

void main() {
    vec3 N = normalize(vNormal);
    vec3 albedo = texture(uAlbedo, vUv).rgb * mat.uTint.rgb;

    // --- Sun (directional) ----------------------------------------------
    vec3 sunL = -normalize(frame.uSunDirection);
    float sunNdotL = max(dot(N, sunL), 0.0);
    float sunShadow = sampleShadow(uSunShadowMap, vSunShadowCoord, sunNdotL);
    vec3 sun = sunNdotL * frame.uSunIntensity * sunShadow * vec3(1.0) * frame.uLightEnable.x;

    // --- Spot lights (array of 2) ---------------------------------------
    vec3 spot = evalSpot(N, vWorldPos, frame.uSpot0ViewProj,
                         frame.uSpot0PosRange, frame.uSpot0DirCosInner,
                         frame.uSpot0ColorCosOuter, uSpotShadowMaps[0])
              + evalSpot(N, vWorldPos, frame.uSpot1ViewProj,
                         frame.uSpot1PosRange, frame.uSpot1DirCosInner,
                         frame.uSpot1ColorCosOuter, uSpotShadowMaps[1]);
    spot *= frame.uLightEnable.y;

    // --- Point light (omnidirectional cube shadow) ----------------------
    vec3 fromPoint = vWorldPos - frame.uPointPosFar.xyz;
    float pointDist = length(fromPoint);
    vec3 pointL = -fromPoint / max(pointDist, 1e-4);
    float pointNdotL = max(dot(N, pointL), 0.0);
    float pointRange = frame.uPointColorRange.w;
    float pointAtten = clamp(1.0 - (pointDist * pointDist) / (pointRange * pointRange), 0.0, 1.0);
    float currentPointDist = pointDist / frame.uPointPosFar.w;
    float sampledPointDist = texture(uPointShadowCube, fromPoint).r;
    float pointShadow = (currentPointDist - 0.02 > sampledPointDist) ? 0.0 : 1.0;
    vec3 point = frame.uPointColorRange.xyz * (pointNdotL * pointAtten * pointShadow * frame.uLightEnable.z);

    vec3 ambient = frame.uAmbientColor * frame.uAmbientIntensity;
    vec3 lit = albedo * (ambient + sun + spot + point);
    outColor = vec4(lit, mat.uTint.a);
}
