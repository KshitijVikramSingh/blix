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
#include "probe_volume.glsl"
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
layout(set = 1, binding = 2) uniform sampler3D uOccupancy;
// The depth atlas, whose .b the injector writes as reachability. The probe view never bound it,
// which is why it could only ever show what the injector STORED and never what a surface RECEIVES.
layout(set = 1, binding = 3) uniform sampler2D uSkyBounceDepth;

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
    if (f.uProbeMode.x > 2.5) {
        // <b>Reachability: what the BLEND does with this probe, not what the atlas holds.</b> The
        // three fields below all read stored values, so a probe rejected by every surface in the
        // scene still painted itself bright and looked like a culprit. This reads the flag the
        // injector writes into the depth atlas's spare channel — the same value blix_probeIrradiance
        // tests — so "is this probe lighting anything" finally has a picture.
        //
        // RED: marked unreachable, contributes nothing to any surface. GREY: live, and its radiance
        // is shown dimmed behind so a live probe still reads as itself.
        ivec3 bd = ivec3(f.uBounceDims.xyz);
        ivec3 bp = clamp(ivec3(vProbeUvw * vec3(bd)), ivec3(0), bd - 1);
        vec2 oct = blix_octEncode(N) * 0.5 + 0.5;
        vec2 tile = vec2(bp.x, bp.y + bp.z * bd.y) * 8.0;
        vec2 atlas = vec2(bd.x, bd.y * bd.z) * 8.0;
        vec2 uv = (tile + 1.0 + oct * 6.0) / atlas;
        float reach = texture(uSkyBounceDepth, uv).b;
        // <b>Flat, and NOT tinted by radiance.</b> The first version added the probe's own colour
        // to a grey base, so every live probe in a lit scene saturated to white and the field could
        // not be read at all -- a binary answer rendered as a continuous one. Bounce radiance is a
        // field of its own; this one has exactly two states and should look like it.
        c = reach < 0.5 ? vec3(0.85, 0.06, 0.06)    // rejected: lights nothing
                        : vec3(0.10, 0.55, 0.85);   // live
        c *= 1.0 / max(f.uProbeMode.y, 1e-3);       // undo the exposure multiply; this is a flag

    } else if (f.uProbeMode.x > 1.5) {
        // <b>What this probe is FOR, if anything.</b> A uniform grid over a bounding box obviously
        // wastes probes, and how many is a question people answer by looking at a cloud of spheres
        // and guessing. Measured on Sponza it is 2.9% inside solid geometry and 7.5% further than
        // one spacing from any surface — so nothing can sample them — with no sealed voids at all.
        // This paints that measurement rather than arguing it.
        float here = texture(uOccupancy, vProbeUvw).r;
        float near = 0.0;
        vec3 step = 1.0 / f.uBounceDims.xyz;
        for (int ax = 0; ax < 3; ++ax)
        for (int sgn = -1; sgn <= 1; sgn += 2) {
            vec3 off = vec3(0.0);
            off[ax] = float(sgn) * step[ax];
            near = max(near, texture(uOccupancy, clamp(vProbeUvw + off, vec3(0.0), vec3(1.0))).r);
        }
        c = here > 0.5      ? vec3(0.9, 0.1, 0.1)    // inside geometry: nothing can sample it
          : near < 0.01     ? vec3(0.9, 0.8, 0.1)    // no surface within a spacing: nothing does
                            : vec3(0.1, 0.8, 0.2);   // reachable by some surface
    } else if (f.uProbeMode.x < 0.5) {
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
