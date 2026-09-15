#version 450

// Lambert + ambient for the opaque backdrop. Outputs LINEAR colour into the
// HDR target (the present pass tonemaps). Kept deliberately dim/sub-1.0 so the
// backdrop doesn't bloom — only the bright particle cores cross the threshold.

layout(push_constant) uniform Push {
    mat4 uModel;
    mat4 uViewProj;
    vec4 uAlbedo;
};

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

// Anti-aliased grid lines on near-horizontal surfaces, for depth + scale cues.
float gridLine(vec2 p, float cell) {
    vec2 c = p / cell;
    vec2 g = abs(fract(c - 0.5) - 0.5) / fwidth(c);
    return 1.0 - min(min(g.x, g.y), 1.0);
}

void main() {
    vec3 n = normalize(vNormal);
    vec3 sunDir = normalize(vec3(0.35, 1.0, 0.45));
    float ndl = max(dot(n, sunDir), 0.0);
    vec3 sun = vec3(1.0, 0.96, 0.88) * ndl;
    vec3 ambient = vec3(0.10, 0.12, 0.18);
    vec3 color = uAlbedo.rgb * (ambient + sun);

    // Floor grid: a coarse + fine line set, only where the surface faces up.
    if (n.y > 0.85) {
        float g = max(gridLine(vWorldPos.xz, 4.0), gridLine(vWorldPos.xz, 1.0) * 0.4);
        color += vec3(0.10, 0.13, 0.20) * g;
    }
    outColor = vec4(color, 1.0);
}
