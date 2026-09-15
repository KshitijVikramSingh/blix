#version 450

// Runner's instanced world fragment shader: per-instance tint with a soft sun
// Lambert term, then exponential distance fog (past a start distance) toward the
// sky-horizon colour so the far track fades into the haze while the near
// play-area stays crisp.

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec4 vTint;
layout(location = 2) in vec3 vWorldPos;

layout(push_constant) uniform Push {
    mat4 uViewProjection;
    vec4 uCamPos;
    vec4 uFogColor;
    vec4 uFogParams;   // x = density, y = start distance
};

layout(location = 0) out vec4 outColor;

const vec3 kSunDir = normalize(vec3(0.30, 0.85, -0.25));
const float kAmbient = 0.35;

void main() {
    vec3 n = normalize(vNormal);
    float diffuse = max(dot(n, kSunDir), 0.0);
    vec3 color = vTint.rgb * (kAmbient + (1.0 - kAmbient) * diffuse);

#ifdef ENABLE_FOG
    // Compiled in only for the FOG variant (glslc -DENABLE_FOG); the base variant
    // skips the distance haze. The push-constant block is declared unconditionally
    // above, so both variants share ONE interface — only the fragment math differs,
    // which is exactly what the variant system is for.
    float dist = length(vWorldPos - uCamPos.xyz);
    float fog = 1.0 - exp(-max(dist - uFogParams.y, 0.0) * uFogParams.x);
    color = mix(color, uFogColor.rgb, clamp(fog, 0.0, 1.0));
#endif

    outColor = vec4(color, vTint.a);
}
