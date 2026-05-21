#version 410 core

// Pong3D lit shader. Per-fragment Blinn-Phong with one directional key light
// + one point light. The point light is owned by the ball in Program.cs and
// color-shifts with ball speed, so as the rally heats up the whole arena
// gets bathed warmer. Cheap and bright.

in vec3 worldNormal;
in vec3 worldPosition;

out vec4 fragColor;

uniform vec3 uCameraPosition;

uniform vec3 uMaterialAlbedo;
uniform float uMaterialSpecular;
uniform float uMaterialShininess;
// 0..1; 1 = fully emissive (point light still contributes additively but the
// surface ignores diffuse shading). Used for paddles/ball so they read as
// "self-lit" against the dimmer rails.
uniform float uMaterialEmissive;

uniform vec3 uAmbient;
uniform vec3 uDirLightDir;
uniform vec3 uDirLightColor;

uniform vec3 uPointPosition;
uniform vec3 uPointColor;
uniform float uPointIntensity;
uniform float uPointRange;

vec3 shadeDirectional(vec3 N, vec3 V, vec3 albedo, float specular, float shininess)
{
    vec3 L = normalize(-uDirLightDir);
    float NdotL = max(dot(N, L), 0.0);
    vec3 H = normalize(L + V);
    float NdotH = max(dot(N, H), 0.0);
    float spec = pow(NdotH, shininess) * specular;
    return uDirLightColor * (albedo * NdotL + vec3(spec));
}

vec3 shadePoint(vec3 N, vec3 V, vec3 albedo, float specular, float shininess)
{
    vec3 toLight = uPointPosition - worldPosition;
    float dist = length(toLight);
    if (dist >= uPointRange) return vec3(0.0);
    vec3 L = toLight / max(dist, 0.0001);

    // Smooth inverse-quadratic-ish falloff: 1 at the source, 0 at uPointRange.
    float t = dist / uPointRange;
    float falloff = (1.0 - t) * (1.0 - t);
    falloff *= uPointIntensity;

    float NdotL = max(dot(N, L), 0.0);
    vec3 H = normalize(L + V);
    float NdotH = max(dot(N, H), 0.0);
    float spec = pow(NdotH, shininess) * specular;
    return uPointColor * (albedo * NdotL + vec3(spec)) * falloff;
}

void main()
{
    vec3 N = normalize(worldNormal);
    vec3 V = normalize(uCameraPosition - worldPosition);

    vec3 directLit = shadeDirectional(N, V, uMaterialAlbedo, uMaterialSpecular, uMaterialShininess);
    vec3 pointLit  = shadePoint(N, V, uMaterialAlbedo, uMaterialSpecular, uMaterialShininess);
    vec3 ambient = uAmbient * uMaterialAlbedo;

    vec3 lit = ambient + directLit + pointLit;
    // emissive=0 -> fully Phong-shaded. emissive=1 -> surface shows its raw
    // albedo brightly, with the ball point-light still rim-lighting it.
    vec3 selfLit = uMaterialAlbedo + pointLit;
    vec3 col = mix(lit, selfLit, uMaterialEmissive);

    fragColor = vec4(col, 1.0);
}
