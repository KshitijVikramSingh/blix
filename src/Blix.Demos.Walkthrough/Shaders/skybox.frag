#version 410 core

// Skybox: samples the HDR procedural env cubemap (same cube the lit shader
// uses for IBL). Tonemapped + gamma-encoded on the way out. Sun + horizon
// tint live in the cube data; this shader is just a viewer.

in vec3 viewDir;
out vec4 fragColor;

uniform samplerCube uEnvMap;
uniform float uExposure;

vec3 acesFilm(vec3 x)
{
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main()
{
    vec3 dir = normalize(viewDir);
    vec3 hdr = texture(uEnvMap, dir).rgb * uExposure;
    vec3 mapped = acesFilm(hdr);
    vec3 gamma = pow(mapped, vec3(1.0 / 2.2));
    fragColor = vec4(gamma, 1.0);
}
