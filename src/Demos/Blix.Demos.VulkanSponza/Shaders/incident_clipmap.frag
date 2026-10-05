#version 450
// The incident-light field from the camera-relative probe clipmap (--gi-clipmap): what incident.frag writes, the
// same two quantities for the lit pass (rgb bounce, a sky visibility), read from the clipmap's traced probes rather
// than from the baked bounds-sized volumes. Its own program because it declares the clipmap's state buffer, which
// only exists when the clipmap does.
//
// Past the clipmap's coarsest level nothing answers: no bounce, and the sky taken as open.

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;

layout(set = 0, binding = 0) uniform IncidentClipmap {
    mat4 uInvProjection;
    mat4 uInvView;
    vec4 uTarget;
    vec4 uClipDims;      // xyz probes per level, w base spacing
    vec4 uClipParams;    // x blend band (probes)
    vec4 uOrigin0;
    vec4 uOrigin1;
    vec4 uOrigin2;
    vec4 uOrigin3;
} g;

layout(set = 0, binding = 1) uniform sampler2D uSceneDepth;
layout(set = 0, binding = 2) uniform sampler2D uPrepassNormal;
layout(set = 0, binding = 3) uniform sampler2D uClipmapIrradiance;
layout(set = 0, binding = 4) uniform sampler2D uClipmapDepth;
layout(std430, set = 0, binding = 5) readonly buffer ClipmapState { uvec4 states[]; };

#define BLIX_CLIPMAP_IRRADIANCE(t) texelFetch(uClipmapIrradiance, t, 0)
#define BLIX_CLIPMAP_DEPTH(t) texelFetch(uClipmapDepth, t, 0)
#define BLIX_CLIPMAP_STATE(s) states[s]
#include "probe_clipmap.glsl"

void main() {
    float raw = texture(uSceneDepth, vUv).r;
    if (raw >= 1.0 - 1e-6) {
        outIncident = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }
    vec4 view = g.uInvProjection * vec4(vUv * 2.0 - 1.0, raw, 1.0);
    vec3 worldPos = (g.uInvView * vec4(view.xyz / view.w, 1.0)).xyz;
    vec4 nSample = texture(uPrepassNormal, vUv);
    vec3 N = dot(nSample.xyz, nSample.xyz) > 1e-6
        ? normalize(nSample.xyz)
        : normalize((g.uInvView * vec4(0.0, 0.0, 1.0, 0.0)).xyz);

    BlixClipmap c;
    c.dims = ivec3(g.uClipDims.xyz);
    c.baseSpacing = g.uClipDims.w;
    c.blendProbes = g.uClipParams.x;
    c.origin[0] = ivec3(g.uOrigin0.xyz);
    c.origin[1] = ivec3(g.uOrigin1.xyz);
    c.origin[2] = ivec3(g.uOrigin2.xyz);
    c.origin[3] = ivec3(g.uOrigin3.xyz);
    bool found;
    vec4 field = blix_clipmapSample(c, worldPos, N, found);
    outIncident = found ? field : vec4(0.0, 0.0, 0.0, 1.0);
}
