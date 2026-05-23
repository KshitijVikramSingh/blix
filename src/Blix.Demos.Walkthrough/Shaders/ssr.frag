#version 410 core

// Screen-space reflections. Marches a reflection ray in NDC/screen space
// (linear interpolation between the projected start and end of the ray),
// comparing each step's NDC.z against the depth buffer at the matching UV.
// Operating in screen space matches the depth buffer's actual precision and
// keeps step size sized to ~1 pixel rather than to a fixed world-units stride,
// which is what produced the visible vertical striping and bottom-edge smear
// in the earlier world-space implementation.
//
// Normal reconstruction from depth derivatives is too noisy at typical scene
// distances on 24-bit depth (adjacent pixels can resolve to the same depth
// bin, randomly flipping the dFdx/dFdy sign). We gate SSR to upward-facing
// surfaces with the noisy normal, then use a clean (0,1,0) for the actual
// reflection vector -- safe since the gate already ensured we're on an
// essentially-horizontal surface.

#include "lib/noise.glsl"

in vec2 vUv;
out vec4 fragColor;

uniform sampler2D uHdrScene;
uniform sampler2D uSceneDepth;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat4 uInvViewProj;
uniform vec3 uCameraPosition;
uniform float uMaxDistance;     // world-units to extend the ray before clipping
uniform float uSteps;           // 16-64
uniform float uThickness;       // hit tolerance in NDC.z units (small, e.g. 0.005)
uniform float uIntensity;       // overall multiplier on the contribution
uniform float uRoughnessCutoff; // skip fragments with roughness above this -- matte surfaces (cloth, plaster) don't reflect
uniform sampler2D uRoughnessMap; // R channel = roughness, written by lit pass as MRT attachment 1

vec3 reconstructWorld(vec2 uv, float depth)
{
    vec4 ndc = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 w = uInvViewProj * ndc;
    return w.xyz / w.w;
}

// World -> clip-space vec4 (no perspective divide; caller can test w > 0
// to determine if the point is in front of the camera).
vec4 worldToClip(vec3 world)
{
    return uProjection * (uView * vec4(world, 1.0));
}

void main()
{
    float centreDepth = texture(uSceneDepth, vUv).r;
    if (centreDepth >= 0.9999) { fragColor = vec4(0.0); return; }

    vec3 worldCentre = reconstructWorld(vUv, centreDepth);
    vec3 viewDir = normalize(worldCentre - uCameraPosition);

    // Gate-only reconstructed normal (see file header comment for why).
    vec3 dPdx = dFdx(worldCentre);
    vec3 dPdy = dFdy(worldCentre);
    vec3 Nrecon = normalize(cross(dPdx, dPdy));
    if (dot(Nrecon, -viewDir) < 0.0) Nrecon = -Nrecon;
    // Two-part gate. The Nrecon.y check passes for "reconstructed normal
    // points up" -- but the flip-to-face-camera above turns ARCH UNDERSIDES
    // (whose true geometric normal points down) into faux upward normals
    // when viewed from above. The spatial check rules those out: a real
    // floor is BELOW the camera; an arch underside isn't.
    float upMask = smoothstep(0.85, 0.97, Nrecon.y);
    float belowCam = step(worldCentre.y, uCameraPosition.y - 0.3);
    // Roughness gate: lit pass writes per-fragment roughness to the
    // R channel of uRoughnessMap as MRT attachment 1. Matte surfaces
    // (cloth, plaster, brick) sit above the cutoff and never reflect;
    // glossy surfaces (marble, polished metal) sit below it and do.
    // Smoothstep over a small window so the transition isn't a hard line.
    float roughness = texture(uRoughnessMap, vUv).r;
    float roughMask = 1.0 - smoothstep(
        uRoughnessCutoff - 0.05, uRoughnessCutoff, roughness);
    upMask *= belowCam * roughMask;
    if (upMask < 0.01) { fragColor = vec4(0.0); return; }

    vec3 N = vec3(0.0, 1.0, 0.0);
    vec3 reflectDir = normalize(reflect(viewDir, N));

    // Project ray start and end into clip space. Push the start slightly off
    // the surface so the first sample doesn't self-hit.
    vec3 rayStart = worldCentre + N * 0.02;
    vec3 rayEnd   = rayStart + reflectDir * uMaxDistance;

    vec4 clipStart = worldToClip(rayStart);
    vec4 clipEnd   = worldToClip(rayEnd);

    // If the ray's end lands behind the camera, clip it to a point on the
    // ray that's just in front of the near plane. Without this, the
    // perspective divide produces garbage NDC.
    if (clipEnd.w < 0.01)
    {
        // Solve for t such that clipStart.w + t * (clipEnd.w - clipStart.w) = 0.01.
        float t = (0.01 - clipStart.w) / (clipEnd.w - clipStart.w);
        clipEnd = mix(clipStart, clipEnd, t);
    }

    // Perspective divide -> NDC; then remap NDC.xy -> UV [0,1] and NDC.z ->
    // depth-buffer convention [0,1].
    vec3 ndcStart = clipStart.xyz / clipStart.w;
    vec3 ndcEnd   = clipEnd.xyz   / clipEnd.w;
    vec3 uvStart  = vec3(ndcStart.xy * 0.5 + 0.5, ndcStart.z * 0.5 + 0.5);
    vec3 uvEnd    = vec3(ndcEnd.xy   * 0.5 + 0.5, ndcEnd.z   * 0.5 + 0.5);

    int steps = int(uSteps);
    vec3 hitColour = vec3(0.0);
    float hitConfidence = 0.0;

    // Per-pixel jitter to break up step boundaries into noise rather than
    // visible bands. A hash on the screen UV gives a deterministic but
    // spatially-decorrelated offset within one step's worth of progress.
    float jitter = blix_screenHash(vUv);

    // Track the previous sample so a hit can be bisected against the
    // last-non-hit position for sub-step accuracy.
    vec3 prevSamp = uvStart;
    bool foundHit = false;
    vec3 hitSamp = vec3(0.0);

    for (int i = 1; i <= 64; ++i)
    {
        if (i > steps) break;
        float t = (float(i) + jitter * 0.5) / float(steps);
        vec3 samp = mix(uvStart, uvEnd, t);

        if (samp.x < 0.0 || samp.x > 1.0 || samp.y < 0.0 || samp.y > 1.0) break;
        if (samp.z < 0.0 || samp.z > 1.0) break;

        float sceneDepth = texture(uSceneDepth, samp.xy).r;
        float dd = samp.z - sceneDepth;
        if (dd > 0.0 && dd < uThickness)
        {
            foundHit = true;
            hitSamp = samp;
            break;
        }
        prevSamp = samp;
    }

    if (foundHit)
    {
        // Binary-search bisection: prevSamp was BEFORE the hit (in front of
        // visible surface), hitSamp is PAST it. Converge on the actual
        // crossing by halving 5 times -- gives ~1/32 of a step's worth of
        // precision, eliminating the band quantisation that produced the
        // blocky look.
        for (int j = 0; j < 5; ++j)
        {
            vec3 mid = (prevSamp + hitSamp) * 0.5;
            float midScene = texture(uSceneDepth, mid.xy).r;
            float midDD = mid.z - midScene;
            if (midDD > 0.0 && midDD < uThickness)
            {
                hitSamp = mid;
            }
            else if (midDD <= 0.0)
            {
                prevSamp = mid;
            }
            else
            {
                // dd > thickness; the ray went too far behind. Pull back.
                hitSamp = mid;
            }
        }

        hitColour = texture(uHdrScene, hitSamp.xy).rgb;
        // HDR clamp on the reflection sample. Without this, the volumetric
        // fire's white-hot core (~3.5 HDR) gets reflected at full intensity
        // and turns into over-saturated blobs in the marble's reflection.
        // 2.0 keeps moderate brightness (fire still visible in reflection)
        // but stops the HDR spike from dominating the floor.
        hitColour = min(hitColour, vec3(2.0));
        // Edge + distance fades. Use hitSamp's screen position for edge
        // fade and the unjittered t for distance fade.
        vec2 edge = min(hitSamp.xy, 1.0 - hitSamp.xy) * 8.0;
        float edgeFade = clamp(min(edge.x, edge.y), 0.0, 1.0);
        // Approximate t at hit by inverting the lerp on uvStart -> uvEnd.
        // Use the longer axis to avoid divide-by-zero.
        vec2 span = uvEnd.xy - uvStart.xy;
        float t = (abs(span.x) > abs(span.y))
            ? (hitSamp.x - uvStart.x) / max(abs(span.x), 1e-5) * sign(span.x)
            : (hitSamp.y - uvStart.y) / max(abs(span.y), 1e-5) * sign(span.y);
        float distFade = 1.0 - clamp(t, 0.0, 1.0);
        hitConfidence = edgeFade * distFade * upMask;
    }

    fragColor = vec4(hitColour * hitConfidence * uIntensity, 1.0);
}
