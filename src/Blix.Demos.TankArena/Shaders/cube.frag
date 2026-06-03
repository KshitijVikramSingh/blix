#version 450

// Daylight world shading: a warm directional sun + hemispheric ambient (sky tint
// from above, ground tint from below), then distance fog toward the sky horizon so
// the arena fades into the background instead of ending at a hard grey edge.

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec4 vTint;
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(push_constant) uniform Push {
    mat4 uViewProjection;
    vec4 uCamPos;
    vec4 uSunDir;
};

const vec3 kSkyAmbient    = vec3(0.45, 0.55, 0.66);
const vec3 kGroundAmbient = vec3(0.24, 0.21, 0.18);
const vec3 kSunColor      = vec3(1.00, 0.96, 0.84);
const vec3 kFogColor      = vec3(0.72, 0.84, 0.94);   // matches the sky horizon
const float kFogStart     = 38.0;
const float kFogEnd       = 130.0;

void main() {
    vec3 n = normalize(vNormal);
    float diffuse = max(dot(n, normalize(uSunDir.xyz)), 0.0);
    vec3 ambient = mix(kGroundAmbient, kSkyAmbient, n.y * 0.5 + 0.5);
    vec3 lit = vTint.rgb * (ambient + kSunColor * diffuse * 0.9);

    float dist = length(vWorldPos - uCamPos.xyz);
    float fog = smoothstep(kFogStart, kFogEnd, dist);
    outColor = vec4(mix(lit, kFogColor, fog), vTint.a);
}
