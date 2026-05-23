#version 410 core

// Volumetric flame, phase A: analytic procedural density field, ray-marched
// through a bounding-box per lamp. This validates the volume-rendering
// pipeline so the density function can later be replaced with a sampler3D
// lookup of EmberGen/Houdini-baked data (phase C) without touching any of
// the marcher or compositor.
//
// Algorithm:
//   1. Convert ray to LOCAL space ([-0.5, 0.5] cube centered at origin).
//   2. Ray-AABB intersect to find [tNear, tFar] inside the box.
//   3. March N evenly-spaced steps from tNear to tFar.
//   4. At each step, sample density + temperature from an analytic function
//      (teardrop SDF + advected 3D FBM noise).
//   5. Front-to-back composite: emission accumulates weighted by remaining
//      transmittance; opacity = 1 - exp(-density * step_length)
//      (Beer-Lambert).
//
// Output is HDR over the existing scene -- alpha-blended with the lit pass.

in vec3 vWorldPos;
layout(location = 0) out vec4 fragColor;
// MRT attachment 1: roughness. Smoke/fire is matte; alpha-blended with
// the same alpha as the colour so opaque volume pixels replace the
// underlying surface's roughness with 1.0.
layout(location = 1) out vec4 fragMaterial;

uniform vec3  uCameraPosition;
uniform vec3  uVolumeCenter;
uniform float uVolumeSize;
uniform float uTime;
uniform vec3  uFlameColor;
uniform float uFlameIntensity;
uniform float uExposure;
uniform float uVolumeSteps;       // 16-64
uniform float uVolumeDensity;     // overall density multiplier
uniform float uVolumeRise;        // upward advection speed (procedural mode only)

// Real-data path: an R8 sampler3D containing N frames stacked along the
// depth axis. uVolumeFrames is the frame count; uVolumeFps drives playback
// speed. uFramePhase is per-flame so the four braziers don't loop in sync.
// uUseVdb toggles between the analytic path (0.0) and the texture-sampled
// path (1.0); kept as a uniform so the existing debug controls can A/B
// without recompiling.
uniform sampler3D uVolumeData;
uniform float uVolumeFrames;
uniform float uVolumeFps;
uniform float uFramePhase;
// Stacked-volume mixing: both contributions accumulate per sample.
//   VDB layer occupies the BOTTOM of the cube (the campfire's turbulent
//   embers + the base of the flame -- the part that already has real
//   simulation detail).
//   Procedural layer occupies the TOP of the cube (the narrow rising
//   teardrop -- gives the brazier its tall-and-pointed silhouette that
//   the wide campfire VDB lacks).
// Both weights at 0 = nothing. One at 0 = single-layer mode. Both > 0 =
// stacked (the default).
uniform float uVolumeVdbWeight;
uniform float uVolumeProcWeight;
// Per-conversion the VDB was normalised against its global-max temperature
// (~1.013 in the source pack), so most voxels read back at 0.1-0.3 after R8
// quantisation. Boost lifts the sampled temperature before the colour ramp
// so the ramp's orange/yellow bands actually trigger.
uniform float uVolumeTempBoost;

#include "lib/noise.glsl"
#define vnoise3 blix_vnoise3
#define fbm3 blix_fbm3

// --- VDB-backed temperature lookup ---------------------------------------
// Samples the packed 3D texture for frame `frameIdx`. The packing puts
// N frames stacked along the depth axis (W coord). Within a frame, V is
// "up" (matches our cube's local +Y) and W is the third horizontal axis.
float sampleVdbFrame(float frameIdx, vec3 local)
{
    // local in [-0.5, 0.5]; bring to [0, 1] for texture sampling.
    vec3 tex = local + vec3(0.5);
    // Within the cube we treat the bottom 90% as "fire region" so the very
    // top of the cube is reserved as a safe zone where the texture's edge
    // clamping doesn't bleed flame across the top. Shrink V toward the
    // middle.
    tex.y = clamp(tex.y, 0.01, 0.99);
    tex.x = clamp(tex.x, 0.01, 0.99);
    tex.z = clamp(tex.z, 0.01, 0.99);

    float f = mod(frameIdx, uVolumeFrames);
    // The W coord runs through all stacked frames: a frame occupies
    // [f / N, (f+1) / N) along W. Within that range, local.z maps to the
    // frame's own W axis.
    float w = (f + tex.z) / uVolumeFrames;
    return texture(uVolumeData, vec3(tex.x, tex.y, w)).r;
}

// --- Density + emission at a local-space point ---------------------------
// local is in [-0.5, 0.5]^3. Returns vec4(emissive_color.rgb, density).
vec4 sampleFire(vec3 local)
{
    float density = 0.0;
    float temperature = 0.0;

    // Vertical weighting for the two layers. The procedural teardrop now
    // covers the FULL cube (its own topFade tapers the tip naturally), and
    // the VDB extends well into the middle so the two overlap heavily --
    // the goal is "one fire" where the VDB provides turbulent detail and
    // the procedural provides the tall brazier silhouette, not "two stacked
    // fires" with a visible transition band.
    float vdbVerticalGate  = smoothstep( 0.20, -0.40, local.y);  // strong low, gone above y=0.2
    float procVerticalGate = 1.0;                                // procedural everywhere; its own SDF shapes it

    // --- VDB layer: campfire base from baked sim data ---
    if (uVolumeVdbWeight > 0.0)
    {
        float t_frame = uTime * uVolumeFps + uFramePhase;
        float idxA = floor(t_frame);
        float blend = fract(t_frame);
        float tA = sampleVdbFrame(idxA,       local);
        float tB = sampleVdbFrame(idxA + 1.0, local);
        float vdbTemp = mix(tA, tB, blend) * uVolumeTempBoost;
        float w = uVolumeVdbWeight * vdbVerticalGate;
        temperature += vdbTemp * w;
        density     += vdbTemp * uVolumeDensity * w;
    }

    // --- Procedural layer: tall narrow teardrop "brazier tongue" ---
    if (uVolumeProcWeight > 0.0)
    {
        float t = clamp(local.y + 0.5, 0.0, 1.0);
        float halfWidth = 0.40 * (1.0 - t * 0.55);
        float r = length(local.xz);
        float radial = 1.0 - clamp(r / max(halfWidth, 0.01), 0.0, 1.0);
        vec3 q = local * 4.0 + vec3(0.0, -uTime * uVolumeRise, 0.0);
        float n = fbm3(q);
        float topFade = smoothstep(1.0, 0.25, t);
        float procIntensity = radial * (0.35 + n * 0.85) * topFade;
        // Procedural's per-position temperature shaping (hotter core, cooler tip)
        float procTemp = procIntensity * (1.45 - t * 0.65) * (0.5 + radial * 0.8);
        float w = uVolumeProcWeight * procVerticalGate;
        temperature += procTemp * w;
        density     += procIntensity * uVolumeDensity * w;
    }

    // Blackbody-inspired colour ramp. The top two bands push past 1.0 in
    // every channel so the brightest pixels are HDR -- bloom and ACES turn
    // them into a punchy halo around the flame. Without this the ramp tops
    // out at saturated orange (no white-hot), which reads as "flat" fire
    // even at correct brightness.
    vec3 c = vec3(0.0);
    c += vec3(0.65, 0.05, 0.00) * smoothstep(0.05, 0.30, temperature);
    c += vec3(0.85, 0.30, 0.00) * smoothstep(0.25, 0.55, temperature);
    c += vec3(0.95, 0.65, 0.10) * smoothstep(0.50, 0.85, temperature);
    c += vec3(1.20, 1.00, 0.45) * smoothstep(0.80, 1.05, temperature);
    // White-hot HDR core. Triggers only at the very brightest spots so the
    // halo concentrates at the flame's centre rather than washing out the
    // whole shape.
    c += vec3(2.00, 1.60, 1.00) * smoothstep(1.20, 2.20, temperature);

    return vec4(c, max(density, 0.0));
}

// --- Ray-AABB intersection for local [-0.5, 0.5] cube --------------------
vec2 intersectCube(vec3 ro, vec3 rd)
{
    vec3 inv = 1.0 / rd;
    vec3 t1 = (vec3(-0.5) - ro) * inv;
    vec3 t2 = (vec3( 0.5) - ro) * inv;
    vec3 tmin = min(t1, t2);
    vec3 tmax = max(t1, t2);
    float tNear = max(max(tmin.x, tmin.y), tmin.z);
    float tFar  = min(min(tmax.x, tmax.y), tmax.z);
    return vec2(tNear, tFar);
}

void main()
{
    // Convert ray to LOCAL cube space. Translation by uVolumeCenter doesn't
    // affect direction; the uniform scale by uVolumeSize doesn't either
    // (it cancels out in the normalisation).
    vec3 localCam  = (uCameraPosition - uVolumeCenter) / uVolumeSize;
    vec3 localFrag = (vWorldPos        - uVolumeCenter) / uVolumeSize;
    vec3 rd = normalize(localFrag - localCam);

    vec2 t = intersectCube(localCam, rd);
    float tNear = max(t.x, 0.0);   // clamp to 0 when camera is inside the box
    float tFar  = t.y;
    if (tNear >= tFar) discard;

    int steps = int(uVolumeSteps);
    float stepLen = (tFar - tNear) / float(steps);
    vec3 stepVec = rd * stepLen;
    // Start half a step in so each sample sits at the *centre* of its slab
    // (reduces under-sampling artefacts at the boundary).
    vec3 pos = localCam + rd * tNear + stepVec * 0.5;

    vec3 accumColor = vec3(0.0);
    float transmittance = 1.0;

    for (int i = 0; i < 64; ++i)
    {
        if (i >= steps) break;
        vec4 s = sampleFire(pos);
        float density = s.a;
        if (density > 0.005)
        {
            // Beer-Lambert opacity for this slab.
            float opacity = 1.0 - exp(-density * stepLen);
            accumColor += s.rgb * opacity * transmittance;
            transmittance *= 1.0 - opacity;
            if (transmittance < 0.01) break;
        }
        pos += stepVec;
    }

    float alpha = 1.0 - transmittance;
    if (alpha < 0.005) discard;

    vec3 finalColor = accumColor * uFlameColor * uFlameIntensity * uExposure;
    fragColor = vec4(finalColor, alpha);
    fragMaterial = vec4(1.0, 0.0, 0.0, alpha);
}
