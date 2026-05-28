#version 450

// ImGui overlay fragment shader. The font atlas is white RGB with coverage in
// alpha, so the output color is just the vertex color modulated by coverage.
//
// The overlay draws onto the sRGB swapchain image, where the hardware encodes
// linear->sRGB on write. ImGui's vertex colors (and StyleColorsDark) are
// authored in sRGB space, so we convert them to linear here; the hardware
// re-encode then round-trips them back to the intended sRGB on screen. Without
// this the panels read washed-out / too bright (a double sRGB encode).

layout(set = 0, binding = 0) uniform sampler2D uFont;

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;

layout(location = 0) out vec4 outColor;

vec3 srgbToLinear(vec3 c) {
    bvec3 cutoff = lessThanEqual(c, vec3(0.04045));
    vec3 lower = c / 12.92;
    vec3 higher = pow((c + 0.055) / 1.055, vec3(2.4));
    return mix(higher, lower, cutoff);
}

void main() {
    vec4 texel = texture(uFont, vUv);
    outColor = vec4(srgbToLinear(vColor.rgb), vColor.a) * texel;
}
