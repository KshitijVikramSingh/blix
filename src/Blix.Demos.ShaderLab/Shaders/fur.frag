#version 410 core

in vec2 textureCoordinate;
in vec3 worldNormal;
in vec3 worldPositionOut;
in float furLayer;

layout (location = 0) out vec4 fragColor;
layout (location = 1) out vec4 fragLuminance;
layout (location = 2) out vec4 fragNormal;

uniform sampler2D uFurNoise;
uniform vec3 uLightDirection;
uniform vec3 uCameraPosition;
uniform float uLightIntensity;
uniform float uAmbientBoost;
uniform float uFurDensity;

void main()
{
    vec2 furUv = textureCoordinate * 6.0 + vec2(furLayer * 0.37, furLayer * 0.19);
    float noise = texture(uFurNoise, furUv).r;

    // Inner shells stay mostly solid; outer shells get progressively sparser so the
    // silhouette breaks into strands instead of reading like a transparent balloon.
    float cutoff = mix(0.08, 0.82, furLayer) + (1.0 - uFurDensity) * 0.28;
    if (furLayer > 0.06 && noise < cutoff)
    {
        discard;
    }

    vec3 N = normalize(worldNormal);
    vec3 L = normalize(uLightDirection);
    vec3 V = normalize(uCameraPosition - worldPositionOut);
    vec3 H = normalize(L + V);
    float NdotL = max(dot(N, L), 0.0);
    float NdotH = max(dot(N, H), 0.0);

    vec3 rootColor = vec3(0.38, 0.20, 0.10);
    vec3 tipColor = vec3(1.00, 0.78, 0.42);
    vec3 color = mix(rootColor, tipColor, furLayer * 0.9);

    float ambient = 0.16 * uAmbientBoost;
    float diffuse = 0.84 * NdotL * uLightIntensity;
    float rim = pow(1.0 - max(dot(N, V), 0.0), 2.4) * 0.25;
    float specular = pow(NdotH, 18.0) * 0.10 * uLightIntensity;
    vec3 lit = color * (ambient + diffuse + rim) + vec3(specular);

    float alpha = mix(1.0, 0.16, furLayer);
    fragColor = vec4(lit, alpha);

    float luma = dot(lit, vec3(0.2126, 0.7152, 0.0722));
    fragLuminance = vec4(vec3(luma), alpha);
    fragNormal = vec4(N * 0.5 + 0.5, alpha);
}
