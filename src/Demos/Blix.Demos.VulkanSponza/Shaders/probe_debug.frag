#version 450
// Probe-field diagnostics drawn as analytic spheres. Bounce and sky-visibility modes evaluate their
// stored directional representations over the sphere without material albedo, GTAO, or receiver
// visibility, separating field contents from their eventual contribution to a shaded surface.
// Usefulness and reachability modes instead show categorical grid state.
#include "probe_volume.glsl"
#define PI 3.14159265359

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProj;
    vec4 uCameraPos;
    vec4 uProbeMin;
    vec4 uProbeSpan;
    vec4 uProbeDims;
    // x: 0 = bounce, 1 = sky visibility, 2 = usefulness, 3 = reachability; y = exposure
    vec4 uProbeMode;
    vec4 uBounceDims;   // xyz bounce probe counts
} f;

layout(set = 1, binding = 0) uniform sampler2D uSkyBounce;
layout(set = 1, binding = 1) uniform sampler3D uSkyVisibility;
layout(set = 1, binding = 2) uniform sampler3D uOccupancy;
// The depth atlas's .b channel stores the reachability flag used by receiver blending.
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
        // Display the reachability flag consumed by blix_probeIrradiance rather than stored
        // radiance: red probes contribute to no receiver; blue probes remain eligible.
        ivec3 bd = ivec3(f.uBounceDims.xyz);
        ivec3 bp = clamp(ivec3(vProbeUvw * vec3(bd)), ivec3(0), bd - 1);
        vec2 oct = blix_octEncode(N) * 0.5 + 0.5;
        vec2 tile = vec2(bp.x, bp.y + bp.z * bd.y) * 8.0;
        vec2 atlas = vec2(bd.x, bd.y * bd.z) * 8.0;
        vec2 uv = (tile + 1.0 + oct * 6.0) / atlas;
        float reach = texture(uSkyBounceDepth, uv).b;
        // Keep this categorical and independent of radiance so its two states remain legible.
        c = reach < 0.5 ? vec3(0.85, 0.06, 0.06)    // rejected: lights nothing
                        : vec3(0.10, 0.55, 0.85);   // live
        c *= 1.0 / max(f.uProbeMode.y, 1e-3);       // undo the exposure multiply; this is a flag

    } else if (f.uProbeMode.x > 1.5) {
        // Classify grid usefulness from occupancy. Sponza measured 2.9% of probes inside solid
        // geometry and another 7.5% farther than one probe spacing from any surface.
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
