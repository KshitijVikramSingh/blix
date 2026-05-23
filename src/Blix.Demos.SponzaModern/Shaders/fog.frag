#version 410 core

// Volumetric fog with sun god-rays. Full-screen pass that, per pixel:
//   1. Reconstructs the scene's world-space far point from sampled depth.
//   2. Marches the view ray from camera to that point in N steps.
//   3. At each step, samples the directional shadow map to check whether
//      sunlight reaches this point in the volume.
//   4. Accumulates in-scattering (sun_color * visibility * phase) weighted
//      by remaining transmittance through the fog so far.
//
// Output is additive over the HDR scene buffer: only the in-scattered
// "god-ray" colour is written. We skip the multiplicative absorption term
// (which would darken the scene through fog) because additive blending
// can't combine add+multiply in one pass; for thin atmospheric fog the
// absorption is small enough that this reads convincingly anyway.

in vec2 vUv;
layout(location = 0) out vec4 fragColor;
// MRT attachment 1: roughness. Fog is additive, so writing 0 here leaves
// the underlying surface's roughness unchanged after blending.
layout(location = 1) out vec4 fragMaterial;

uniform sampler2D    uSceneDepth;       // non-shadow depth read
uniform sampler2DShadow uShadowMap;
uniform mat4  uInvViewProj;
uniform mat4  uLightViewProjection;
uniform vec3  uCameraPosition;
uniform vec3  uSunDirection;
uniform vec3  uSunColor;
uniform float uFogDensity;              // per-meter extinction
uniform float uFogScatter;              // sun in-scatter brightness multiplier
uniform float uFogSteps;                // 16-48
uniform float uFogMaxDistance;          // ray-march cap so distant skybox pixels don't go infinite

// Point lights also contribute volumetric scattering -- without this, the
// braziers cast warm pools on the floor but the air around them stays
// neutral grey. Unshadowed (no cube shadow lookup per fog step) for cost
// reasons; the visual loss is minor at typical fog density.
#define FOG_MAX_POINT_LIGHTS 8
uniform vec3  uPointLightPositions[FOG_MAX_POINT_LIGHTS];
uniform vec3  uPointLightColors[FOG_MAX_POINT_LIGHTS];   // intensity baked in
uniform float uPointLightRanges[FOG_MAX_POINT_LIGHTS];
uniform float uPointLightCount;
uniform float uFogPointScatter;         // point-light in-scatter multiplier

float sampleSunShadow(vec3 worldPos)
{
    vec4 lightSpace = uLightViewProjection * vec4(worldPos, 1.0);
    vec3 coords = lightSpace.xyz / lightSpace.w;
    coords = coords * 0.5 + 0.5;
    if (coords.z > 1.0) return 1.0;
    if (coords.x < 0.0 || coords.x > 1.0) return 1.0;
    if (coords.y < 0.0 || coords.y > 1.0) return 1.0;
    return texture(uShadowMap, vec3(coords.xy, coords.z - 0.0015));
}

void main()
{
    // Scene depth in [0, 1]. The skybox writes 0 because of LessEqual depth +
    // skybox-at-NDC-z=1; everything else writes its own depth. We march to
    // either the surface depth or a max distance, whichever's closer.
    float sceneDepth = texture(uSceneDepth, vUv).r;

    // Reconstruct world position from NDC. ndc.z = 2*depth - 1 because GL's
    // depth is in [0, 1] but ndc.z is in [-1, 1].
    vec4 ndcFar = vec4(vUv * 2.0 - 1.0, sceneDepth * 2.0 - 1.0, 1.0);
    vec4 worldFar4 = uInvViewProj * ndcFar;
    vec3 worldFar = worldFar4.xyz / worldFar4.w;

    vec3 rayVec = worldFar - uCameraPosition;
    float rayLen = length(rayVec);
    if (rayLen < 1e-3) discard;

    vec3 rayDir = rayVec / rayLen;
    float marchLen = min(rayLen, uFogMaxDistance);
    int steps = int(uFogSteps);
    float stepLen = marchLen / float(steps);

    // Mie-like forward-scattering phase. Higher when ray points toward the
    // sun -- this is what gives the "god rays beaming out of the sun" feel.
    float cosTheta = dot(rayDir, -uSunDirection);
    // Henyey-Greenstein with g=0.6 (forward-peaked). Approximated cheaply.
    float g = 0.6;
    float g2 = g * g;
    float phaseDenom = pow(max(1.0 + g2 - 2.0 * g * cosTheta, 0.0001), 1.5);
    float phase = (1.0 - g2) / (4.0 * 3.14159265 * phaseDenom);
    // Boost a bit so the rays read without cranking other terms.
    phase = max(phase, 0.05);

    vec3 fog = vec3(0.0);
    float transmittance = 1.0;
    // Start half a step in so each sample sits in the centre of its slab.
    vec3 pos = uCameraPosition + rayDir * (stepLen * 0.5);

    // --- Sun in-scatter via ray march ---------------------------------
    // Sun contribution still uses per-step march because each step's
    // visibility is sampled from the directional shadow map (sun rays only
    // reach unshadowed steps -- that's what makes god-rays appear).
    for (int i = 0; i < 64; ++i)
    {
        if (i >= steps) break;
        float visibility = sampleSunShadow(pos);
        float opacity = 1.0 - exp(-uFogDensity * stepLen);
        vec3 inScatter = uSunColor * uFogScatter * phase * visibility;
        fog += inScatter * opacity * transmittance;
        transmittance *= 1.0 - opacity;
        if (transmittance < 0.01) break;
        pos += rayDir * stepLen;
    }

    // --- Point-light in-scatter: bounded, analytical, no marching ----
    // Earlier per-step march produced visible "lamp tracks camera" jitter;
    // the analytical 1/r^2 line integral that replaced it was smooth but
    // spiked unboundedly for grazing rays (1/h -> infinity), blowing out
    // the scene the moment scatter was above zero.
    //
    // This version uses a BOUNDED smooth falloff with the closest distance
    // h from the view ray to each lamp -- physically a less faithful model
    // of fog inscatter (no actual line integration), but it produces the
    // warm halo around each brazier we want without any singularity. The
    // contribution is in [0, 1] per lamp, sum of N lamps in [0, N].
    int plCount = int(uPointLightCount);
    vec3 pointInscatter = vec3(0.0);
    for (int li = 0; li < FOG_MAX_POINT_LIGHTS; ++li)
    {
        if (li >= plCount) break;
        vec3 lampPos = uPointLightPositions[li];
        float range = uPointLightRanges[li];

        vec3 toLamp = lampPos - uCameraPosition;
        float tcp = dot(toLamp, rayDir);
        vec3 closestOnRay = uCameraPosition + rayDir * tcp;
        float h = length(lampPos - closestOnRay);
        if (h >= range) continue;

        // Smooth quadratic falloff from h=0 to h=range. Bounded [0,1].
        float falloff = clamp(1.0 - h / range, 0.0, 1.0);
        float contribution = falloff * falloff;

        // Soft segment fade. Earlier hard `if (tcp < 0 || tcp > marchLen)
        // continue;` produced visible polygonal edges where the lamp's
        // tcp on neighbouring pixels' rays crossed the segment boundaries.
        // Smoothstep fades in 1m over each end so the transition is
        // gradual rather than binary.
        float segFade = smoothstep(-range, 0.0, tcp)
                      * (1.0 - smoothstep(marchLen, marchLen + range, tcp));
        contribution *= segFade;

        // Use only the lamp's HUE (normalised colour direction), not its
        // intensity. Otherwise the scatter scales with the lamp's bright
        // emission and any non-zero scatter knob blows out the scene.
        vec3 lampHue = uPointLightColors[li] /
            max(max(uPointLightColors[li].r, uPointLightColors[li].g),
                max(uPointLightColors[li].b, 0.001));
        pointInscatter += lampHue * contribution;
    }
    // Modulate by fog density so no-fog -> no scatter, thick-fog -> more
    // scatter. Matches the physical model where inscatter scales with
    // medium density.
    fog += pointInscatter * uFogPointScatter * uFogDensity;

    fragColor = vec4(fog, 1.0);
    fragMaterial = vec4(0.0);
}
