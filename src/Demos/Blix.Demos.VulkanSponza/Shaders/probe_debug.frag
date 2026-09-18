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
#define PI 3.14159265359

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProj;
    vec4 uCameraPos;
    vec4 uProbeMin;
    vec4 uProbeSpan;
    vec4 uProbeDims;
    vec4 uProbeMode;    // x: 0 = bounce, 1 = sky visibility; y = exposure
} f;

layout(set = 1, binding = 0) uniform sampler3D uSkyBounceR;
layout(set = 1, binding = 1) uniform sampler3D uSkyVisibility;
layout(set = 1, binding = 2) uniform sampler3D uSkyBounceG;
layout(set = 1, binding = 3) uniform sampler3D uSkyBounceB;

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
        // L1 spherical harmonics, evaluated along this point's own normal — so a probe SHADES, and
        // a flat disc now means something is wrong rather than something is missing.
        vec4 shR = texture(uSkyBounceR, vProbeUvw);
        vec4 shG = texture(uSkyBounceG, vProbeUvw);
        vec4 shB = texture(uSkyBounceB, vProbeUvw);
        const float SY0 = 0.282095, SY1 = 0.488603;
        vec3 l0 = vec3(shR.x, shG.x, shB.x);
        vec3 dir = vec3(shR.y, shG.y, shB.y) * N.x
                 + vec3(shR.z, shG.z, shB.z) * N.y
                 + vec3(shR.w, shG.w, shB.w) * N.z;
        c = max(PI * SY0 * l0 + (2.0 * PI / 3.0) * SY1 * dir, vec3(0.0));
    } else {
        // L1 spherical harmonics, cosine-convolved: the same evaluation lit.frag does.
        vec4 sh = texture(uSkyVisibility, vProbeUvw);
        const float Y0 = 0.282095, Y1 = 0.488603;
        c = vec3(clamp((PI * Y0 * sh.x + (2.0 * PI / 3.0) * Y1 * dot(sh.yzw, N)) / PI, 0.0, 1.0));
    }

    outColor = vec4(c * f.uProbeMode.y, 1.0);
}
