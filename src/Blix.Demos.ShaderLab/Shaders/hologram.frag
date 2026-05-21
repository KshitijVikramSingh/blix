#version 410 core

in vec3 worldNormal;
in vec3 worldPositionOut;
in vec3 objectPositionOut;
in float glitchMask;

layout (location = 0) out vec4 fragColor;
layout (location = 1) out vec4 fragLuminance;
layout (location = 2) out vec4 fragNormal;

uniform vec3 uCameraPosition;
uniform vec3 uHologramColor;
uniform float uOpacity;
uniform float uRimStrength;
uniform float uScanlineDensity;
uniform float uGlitchAmount;
uniform float uTime;

void main()
{
    vec3 N = normalize(worldNormal);
    vec3 V = normalize(uCameraPosition - worldPositionOut);
    float fresnel = pow(1.0 - max(dot(N, V), 0.0), 2.25);

    float scanPhase = objectPositionOut.y * uScanlineDensity + uTime * 2.4;
    float scan = smoothstep(0.38, 0.98, sin(scanPhase) * 0.5 + 0.5);
    float fineScan = smoothstep(0.82, 1.0, sin(scanPhase * 4.0) * 0.5 + 0.5);

    float bandGate = step(0.965, fract((objectPositionOut.y + uTime * 0.33) * 7.0));
    float glitch = clamp(glitchMask * 4.0 + bandGate * uGlitchAmount, 0.0, 1.0);

    vec3 base = uHologramColor;
    vec3 glow = base * (0.55 + scan * 0.95 + fineScan * 0.25);
    glow += base * fresnel * uRimStrength * 2.4;
    glow += vec3(0.7, 1.0, 1.0) * glitch * 1.4;

    float alpha = uOpacity * (0.20 + fresnel * 0.65 + scan * 0.25 + glitch * 0.25);
    alpha = clamp(alpha, 0.0, 0.82);

    fragColor = vec4(glow, alpha);

    float luma = dot(glow, vec3(0.2126, 0.7152, 0.0722));
    fragLuminance = vec4(vec3(luma), alpha);
    fragNormal = vec4(N * 0.5 + 0.5, alpha);
}
