#version 450
// What one probe holds, shown as a sphere lit by its own stored field.
//
// <b>The point is that a WRONG probe and a wrong image look nothing alike here.</b> An indirect
// term reaches the eye only after albedo, ambient occlusion and visibility have each taken a share,
// so "the bounce looks weak" and "the bounce is weak" were indistinguishable from the image. A
// sphere shaded by the probe alone removes every one of those factors.
//
// It also makes a structural fact self-evident: the bounce volume stores ONE RGB per probe, with no
// direction, where the visibility volume beside it stores L1 spherical harmonics. A directionless
// probe renders as a flat disc — no shading across the sphere at all — and a directional one does
// not. Whatever the field is doing, the sphere cannot hide it.
#include "octahedral.glsl"
#define PI 3.14159265359

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProj;
    vec4 uCameraPos;
    vec4 uProbeMin;
    vec4 uProbeSpan;
    vec4 uProbeDims;
    vec4 uProbeMode;    // x: 0 = bounce, 1 = sky visibility; y = exposure
    vec4 uBounceDims;   // xyz bounce probe counts
} f;

layout(set = 1, binding = 0) uniform sampler2D uSkyBounce;
layout(set = 1, binding = 1) uniform sampler3D uSkyVisibility;

layout(location = 0) in vec3 vProbeCentre;
layout(location = 1) in vec2 vQuad;
layout(location = 2) in vec3 vProbeUvw;

layout(location = 0) out vec4 outColor;

void main() {
    // Analytic sphere: the quad's disc is the silhouette, and z follows from it.
    float r2 = dot(vQuad, vQuad);
    if (r2 > 1.0) discard;

    vec3 toEye = normalize(f.uCameraPos.xyz - vProbeCentre);
    vec3 right = normalize(cross(abs(toEye.y) < 0.99 ? vec3(0, 1, 0) : vec3(1, 0, 0), toEye));
    vec3 up = cross(toEye, right);
    vec3 N = normalize(right * vQuad.x + up * vQuad.y + toEye * sqrt(max(1.0 - r2, 0.0)));

    vec3 c;
    if (f.uProbeMode.x < 0.5) {
        // The probe's own octahedral tile, sampled along this point's normal. A sphere is then a
        // direct picture of the tile: whatever structure the map holds, the sphere shows.
        ivec3 bd = ivec3(f.uBounceDims.xyz);
        ivec3 bp = clamp(ivec3(vProbeUvw * vec3(bd)), ivec3(0), bd - 1);
        vec2 oct = blix_octEncode(N) * 0.5 + 0.5;
        vec2 tile = vec2(bp.x, bp.y + bp.z * bd.y) * 8.0;
        vec2 atlas = vec2(bd.x, bd.y * bd.z) * 8.0;
        c = texture(uSkyBounce, (tile + 1.0 + oct * 6.0) / atlas).rgb;
    } else {
        // L1 spherical harmonics, cosine-convolved: the same evaluation lit.frag does.
        vec4 sh = texture(uSkyVisibility, vProbeUvw);
        const float Y0 = 0.282095, Y1 = 0.488603;
        c = vec3(clamp((PI * Y0 * sh.x + (2.0 * PI / 3.0) * Y1 * dot(sh.yzw, N)) / PI, 0.0, 1.0));
    }

    outColor = vec4(c * f.uProbeMode.y, 1.0);
}
