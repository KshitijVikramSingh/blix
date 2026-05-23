#version 410 core

// Skybox: samples the HDR procedural env cubemap (same cube the lit shader
// uses for IBL). Tonemapped + gamma-encoded on the way out. Sun + horizon
// tint live in the cube data; this shader is just a viewer.

in vec3 viewDir;
layout(location = 0) out vec4 fragColor;
// MRT attachment 1: roughness. Sky is matte from SSR's perspective so
// reflection rays that hit the sky pixel get skipped.
layout(location = 1) out vec4 fragMaterial;

uniform samplerCube uEnvMap;
uniform float uExposure;
// Tint multiplier on the sampled HDR sky. Default vec3(1.0) is identity;
// Night preset uses a deep-blue tint, Storm a cool gray.
uniform vec3 uSkyTint;

void main()
{
    // Linear HDR output -- composite pass handles ACES + gamma. No tonemap
    // here so the bloom downsampler can see the sky's full dynamic range.
    vec3 dir = normalize(viewDir);
    vec3 hdr = texture(uEnvMap, dir).rgb * uSkyTint * uExposure;
    fragColor = vec4(hdr, 1.0);
    fragMaterial = vec4(1.0, 0.0, 0.0, 1.0);
}
