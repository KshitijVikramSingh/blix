// The incident pass's fragment shader, built by incident_clipmap.frag (everything) and incident_lean.frag (INCIDENT_LEAN).
// The incident-light field from the camera-relative probe clipmap: the two quantities the lit pass reads (rgb
// incoming indirect light, a sky visibility), from the clipmap's traced probes.
//
// rgb is ALL the indirect diffuse light arriving (the clipmap's probes carry the sky they see, not a visibility
// fraction of it), so the lit pass adds no sky irradiance of its own when this field is active (frame.uIncident.z).
// Past the clipmap's coarsest level nothing answers: the cooked sky irradiance, as if the sky were open.

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;
// xyz: how the incident light's luminance changes with the normal, at the geometric normal. The lit pass uses it to
// carry the light to its normal-mapped normal (the pre-pass has no normal map). Taken from the CLIPMAP: its
// irradiance at the geometric normal and at two normals tilted ~20 degrees along the surface, from the same
// probes with the same weights, so it is deterministic. (The screen probes' SH gradient was tried first: the
// presented image's median per-pixel variation went 3.40% -> 5.70% with it, its directional bands being the
// noisiest part.) Applied relative, to whatever light the pixel ends with. w unused.
layout(location = 1) out vec4 outIncidentGradient;

layout(set = 0, binding = 0) uniform IncidentClipmap {
    mat4 uInvProjection;
    mat4 uInvView;
    vec4 uTarget;
    vec4 uClipDims;      // xyz probes per level, w base spacing
    vec4 uClipParams;    // x blend band (probes), y 1 + a level to answer from alone (--level-probe; 0: the normal answer),
                         // z a fixed lift (m, --clipmap-lift; 0: a quarter spacing), w the visibility power (0: 3)
    vec4 uOrigin0;
    vec4 uOrigin1;
    vec4 uOrigin2;
    vec4 uOrigin3;
    vec4 uReference;     // the reference view (reference_trace.comp): x 1 when shown, yz its pixels (half the frame's)
    vec4 uClipExact;     // x 1: whether a point sees each probe, TRACED (--clipmap-exact-visibility): the ceiling a
                         // cooked per-texel mask could reach, in place of the Chebyshev estimate
    vec4 uHalf;          // x 1: this target is half the frame (RenderSettings.HalfResLighting), each pixel standing for
                         // the full-res pixel 2p + yz of its 2x2 (a different one each frame; lighting_upsample.frag)
    vec4 uReuse;         // x 1: reuse last frame's answer (SponzaLoop.Clipmap.cs, IncidentReuseNow); y the pixel of each
                         // 2x2 recomputed this frame (x + 2y), every other one reprojected where its surface matches
} g;
// INCIDENT_LEAN (incident_lean.frag): the reference view compiled out (a constant), as the lit pass's lean build: the
// pass pays for the registers of code it does not run. SponzaLoop.Clipmap.cs picks it where the view is off.
#ifdef INCIDENT_LEAN
#define G_REF_X (0.0)
#else
#define G_REF_X (g.uReference.x)
#endif

// Last frame's answer, for reuse: the pixel's motion, its surface key now and a frame ago, the incident light and
// gradient as the lit pass read them (copy_incident.frag).
layout(set = 0, binding = 39) uniform sampler2D uReuseVelocity;
layout(set = 0, binding = 40) uniform usampler2D uReuseKey;
layout(set = 0, binding = 41) uniform usampler2D uReuseKeyPrev;
layout(set = 0, binding = 42) uniform sampler2D uIncidentPrev;
layout(set = 0, binding = 43) uniform sampler2D uIncidentGradientPrev;

layout(set = 0, binding = 1) uniform sampler2D uSceneDepth;
layout(set = 0, binding = 2) uniform sampler2D uPrepassNormal;
layout(set = 0, binding = 3) uniform sampler2D uClipmapIrradiance;
layout(set = 0, binding = 4) uniform sampler2D uClipmapDepth;
layout(std430, set = 0, binding = 5) readonly buffer ClipmapState { uvec4 states[]; };
layout(set = 0, binding = 6) uniform samplerCube uIrradiance;
#define BLIX_CLIPMAP_IRRADIANCE(t) texelFetch(uClipmapIrradiance, t, 0)
#define BLIX_CLIPMAP_DEPTH(t) texelFetch(uClipmapDepth, t, 0)
#define BLIX_CLIPMAP_IRRADIANCE_FILTERED(p) textureLod(uClipmapIrradiance, (p) / vec2(textureSize(uClipmapIrradiance, 0)), 0.0)
#define BLIX_CLIPMAP_DEPTH_FILTERED(p) textureLod(uClipmapDepth, (p) / vec2(textureSize(uClipmapDepth, 0)), 0.0)
#define BLIX_CLIPMAP_STATE(s) states[s]
// uClipParams.z: a fixed lift in metres for every level (0: a quarter of the spacing); w: the Chebyshev visibility's
// power (0: cubed). The level probe's knobs.
// Copied to globals at the top of main: the library's functions have a local named g.
float clipLiftKnob, clipVisibilityKnob;
#define BLIX_CLIPMAP_LIFT(spacing) (clipLiftKnob > 0.0 ? clipLiftKnob : 0.25 * (spacing))
#define BLIX_CLIPMAP_VISIBILITY(c) (clipVisibilityKnob > 0.0 ? pow(max(c, 0.0), clipVisibilityKnob) : (c) * (c) * (c))
// --clipmap-exact-visibility: a ray from the surface point to each probe decides whether it counts (the library's
// BLIX_CLIPMAP_PROBE_VISIBILITY). Eight rays a level a pixel: an experiment's price, not a renderer's.
#define BLIX_RAY_SET 0
#define BLIX_RAY_BINDING 24
#include "ray_query.glsl"
float clipExactKnob;
uint clipExactSeed;
float clipmapTracedVisibility(vec3 world, vec3 n, vec3 probe) {
    vec3 o = world + n * 0.01;
    vec3 d = probe - o;
    float len = length(d);
    if (len < 1e-4) return 1.0;
    clipExactSeed = blix_pcg(clipExactSeed);
    return blix_traceAny(o, d / len, 0.0, len, clipExactSeed) ? 0.0 : 1.0;
}
#define BLIX_CLIPMAP_PROBE_VISIBILITY(world, n, probe) (clipExactKnob > 0.5 ? clipmapTracedVisibility(world, n, probe) : -1.0)
#include "probe_clipmap.glsl"
layout(std430, set = 0, binding = 15) readonly buffer ReferenceAccum { vec4 referenceAccum[]; };

void main() {
    clipLiftKnob = g.uClipParams.z;
    clipVisibilityKnob = g.uClipParams.w;
    clipExactKnob = g.uClipExact.x;
    clipExactSeed = blix_pcg(uint(gl_FragCoord.x) * 7919u + uint(gl_FragCoord.y) * 104729u);
    ivec2 pixel = g.uHalf.x > 0.5
        ? clamp(ivec2(gl_FragCoord.xy) * 2 + ivec2(g.uHalf.yz), ivec2(0), textureSize(uSceneDepth, 0) - 1)
        : clamp(ivec2(vUv * vec2(textureSize(uSceneDepth, 0))), ivec2(0), textureSize(uSceneDepth, 0) - 1);
    float raw = texelFetch(uSceneDepth, pixel, 0).r;
    if (raw >= 1.0 - 1e-6) {
        outIncident = vec4(0.0, 0.0, 0.0, 1.0);
        outIncidentGradient = vec4(0.0);
        return;
    }
    vec2 pixelUv = (vec2(pixel) + 0.5) / vec2(textureSize(uSceneDepth, 0));
    // Reuse (at rest: the probes and the light still): three of each 2x2 take last frame's answer where the same surface
    // showed there, reprojected by the pixel's motion; the fourth recomputes, a different one each frame, so a pixel's
    // answer is never more than four frames old.
    if (g.uReuse.x > 0.5 && uint(pixel.x & 1) + 2u * uint(pixel.y & 1) != uint(g.uReuse.y)) {
        ivec2 size = textureSize(uSceneDepth, 0);
        vec2 prevUv = pixelUv - texelFetch(uReuseVelocity, pixel, 0).xy;
        ivec2 pp = ivec2(floor(prevUv * vec2(size)));
        if (all(greaterThanEqual(pp, ivec2(0))) && all(lessThan(pp, size))) {
            uint key = texelFetch(uReuseKey, pixel, 0).r;
            if (key != 0u && key == texelFetch(uReuseKeyPrev, pp, 0).r) {
                outIncident = texelFetch(uIncidentPrev, pp, 0);
                outIncidentGradient = texelFetch(uIncidentGradientPrev, pp, 0);
                return;
            }
        }
    }
    vec4 view = g.uInvProjection * vec4(pixelUv * 2.0 - 1.0, raw, 1.0);
    vec3 worldPos = (g.uInvView * vec4(view.xyz / view.w, 1.0)).xyz;
    vec4 nSample = texelFetch(uPrepassNormal, pixel, 0);
    vec3 N = dot(nSample.xyz, nSample.xyz) > 1e-6
        ? normalize(nSample.xyz)
        : normalize((g.uInvView * vec4(0.0, 0.0, 1.0, 0.0)).xyz);

    vec3 t1 = normalize(cross(abs(N.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), N));
    vec3 t2 = cross(N, t1);

    BlixClipmap c;
    c.dims = ivec3(g.uClipDims.xyz);
    c.baseSpacing = g.uClipDims.w;
    c.blendProbes = g.uClipParams.x;
    c.origin[0] = ivec3(g.uOrigin0.xyz);
    c.origin[1] = ivec3(g.uOrigin1.xyz);
    c.origin[2] = ivec3(g.uOrigin2.xyz);
    c.origin[3] = ivec3(g.uOrigin3.xyz);
    bool found;
    // Tangents, and two normals tilted along them; s is how far each moves the normal along its tangent.
    const float tilt = 0.35;
    const float s = tilt / 1.0595;   // tilt / sqrt(1 + tilt^2)
    vec3 e1, e2;
    if (g.uClipParams.y > 0.5) {
        // One level alone, no blend (the level probe): where its block holds the point and a probe answers; -1 where not.
        int forced = int(g.uClipParams.y + 0.5) - 1;
        float w = 0.0;
        vec4 f = blix_clipmapInsideDistance(c, forced, worldPos) >= 0.0 ? blix_clipmapLevelSample(c, forced, worldPos, N, w) : vec4(0.0);
        outIncident = w > 0.0 ? f : vec4(-1.0);
        outIncidentGradient = vec4(0.0);
        return;
    }
    vec4 field = blix_clipmapSampleDirs(c, worldPos, N, normalize(N + tilt * t1), normalize(N + tilt * t2), found, e1, e2);
    outIncident = found ? field : vec4(texture(uIrradiance, N).rgb, 1.0);
    // The reference view: the path-traced indirect light in place of every answer above (the gradient below still
    // carries it to the normal map, relative).
    if (G_REF_X > 0.5) {
        ivec2 rp = min(ivec2(vUv * g.uReference.yz), ivec2(g.uReference.yz) - 1);
        vec4 acc = referenceAccum[rp.y * int(g.uReference.y) + rp.x];
        if (acc.w > 0.0) outIncident.rgb = acc.rgb / acc.w;
    }
    outIncidentGradient = vec4(0.0);
    const vec3 luma = vec3(0.2126, 0.7152, 0.0722);
    float lum0 = dot(field.rgb, luma);
    if (found && lum0 > 1e-6) {
        // Relative change per unit of normal displacement, from the clipmap; scaled to the light the pixel ends with.
        vec3 relative = (t1 * (dot(e1, luma) - lum0) + t2 * (dot(e2, luma) - lum0)) / (s * lum0);
        outIncidentGradient = vec4(relative * dot(outIncident.rgb, luma), 0.0);
    }
}
