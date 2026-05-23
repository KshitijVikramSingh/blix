#version 410 core

// Procedural flame. Single billboard, but the fragment shader stacks several
// effects that together push the look past "scrolling noise on a teardrop":
//
//   1. 4-octave FBM with a per-octave rotation so the field isn't axis-aligned.
//   2. Domain warp -- the sampling coords are themselves displaced by a second
//      low-freq FBM. This is the cheapest single trick that pushes procedural
//      noise from "static texture" to "fluid".
//   3. Body + wisp split. The body is dense smooth turbulence inside a
//      breathing teardrop silhouette. The wisp layer is a separate high-freq
//      FBM whose contribution is gated to the upper third so flame tongues
//      look like they detach and rise.
//   4. Soft warm halo bleeding outside the silhouette. Sells the "this is
//      emitting light into the world" reading.
//   5. HDR core (>1.0) for the white-hot tip so the existing ACES tonemap
//      gives it a natural bloom-like rolloff.

in vec2 vUv;
layout(location = 0) out vec4 fragColor;
layout(location = 1) out vec4 fragMaterial;

uniform float uTime;
uniform vec3  uFlameColor;
uniform float uFlameIntensity;
uniform float uExposure;

#include "lib/noise.glsl"
// Local aliases keep the rest of this shader readable -- it pre-dates the
// blix_-prefixed library and reads cleanest with the unprefixed names.
#define hash21 blix_hash21
#define vnoise blix_vnoise2
#define fbm blix_fbm2

// Heat-to-colour ramp. Layered smoothsteps so transitions stay smooth but
// distinct bands (red, orange, yellow) read separately. Multiplied by the
// caller's flame tint (uFlameColor) and intensity outside.
vec3 fireRamp(float t)
{
    t = clamp(t, 0.0, 1.0);
    vec3 c = vec3(0.0);
    c += vec3(0.55, 0.05, 0.00) * smoothstep(0.04, 0.30, t);
    c += vec3(0.80, 0.25, 0.00) * smoothstep(0.22, 0.55, t);
    c += vec3(0.55, 0.40, 0.05) * smoothstep(0.50, 0.80, t);
    c += vec3(0.60, 0.55, 0.25) * smoothstep(0.75, 1.00, t);
    return c;
}

// Discrete embers rising from the base. Each of the EMBER_COUNT seed values
// drives one independent spark cycle: every `life` seconds the spark respawns
// at a new x with a new x-drift, climbs from y~0.25 to y~0.95, and fades.
// Returned value is a scalar 0..1 brightness at this fragment.
#define EMBER_COUNT 7
float embers(vec2 uv)
{
    float total = 0.0;
    const float life = 1.40;
    for (int i = 0; i < EMBER_COUNT; ++i)
    {
        float seed = float(i) * 11.7;
        float t = (uTime + seed) / life;
        float cycle = floor(t);
        float phase = fract(t);                      // 0..1 over this spark's life
        float x0 = hash21(vec2(cycle, seed)) * 1.20 - 0.60;
        float xDrift = (hash21(vec2(cycle + 1.0, seed + 3.0)) - 0.5) * 0.30 * phase;
        // Spark climbs faster than the body advection so it visibly detaches.
        float y0 = 0.25 + phase * 0.70;
        vec2 sparkPos = vec2(x0 + xDrift, y0);
        // Slightly elongated vertical streak shape (taller than wide).
        vec2 d = (uv - sparkPos) / vec2(0.020, 0.045);
        float dist = dot(d, d);
        // Brightness fades quadratically over life and falls off with distance.
        float fade = (1.0 - phase) * (1.0 - phase);
        total += exp(-dist) * fade;
    }
    return total;
}

void main()
{
    // u in [-1, 1], v in [0, 1] -- v=0 is the base of the flame.
    vec2 uv = vec2(vUv.x * 2.0 - 1.0, vUv.y);

    // --- Domain warp ----------------------------------------------------
    // Two FBM samples scrolling upward at different rates form a 2D
    // displacement field that warps the body sampling coords below. The
    // -0.5 recentres the noise around zero so warp is signed.
    vec2 warp = vec2(
        fbm(vec2(uv.x * 1.5,        uv.y * 1.5 - uTime * 0.45)),
        fbm(vec2(uv.x * 1.5 + 7.3,  uv.y * 1.5 - uTime * 0.55 + 13.7))
    ) - 0.5;
    vec2 wUv = uv + warp * 0.40;

    // --- Jagged breathing silhouette -----------------------------------
    // Real flames don't have a smooth teardrop edge -- the boundary is
    // chewed up by turbulence at multiple scales. Three contributions:
    //   breathe: slow ~5% width pulse, the "this thing is alive" cue.
    //   edgeNoise: high-freq noise scrolling upward, the "boundary is
    //     being eaten by turbulence" cue. Centred around 1.0 so it
    //     scales the half-width.
    //   pow(1-y, 0.5): the underlying teardrop tapering.
    float breathe = 0.95 + 0.05 * sin(uTime * 2.30 + uv.x * 1.7);
    float edgeNoise = fbm(vec2(uv.y * 7.0 - uTime * 1.6, uv.x * 2.5 + 41.0));
    float edgeMod = 0.70 + 0.55 * edgeNoise;
    float halfWidth = 0.62 * pow(max(1.0 - uv.y, 0.0), 0.50) * breathe * edgeMod;
    float silhouette = 1.0 - clamp(abs(uv.x) / max(halfWidth, 0.01), 0.0, 1.0);

    // --- Body layer -----------------------------------------------------
    // Dense FBM scrolling upward inside the silhouette. vertFade pulls
    // intensity down toward the tip so the flame doesn't read as a flat
    // slab.
    float bodyN = fbm(vec2(wUv.x * 2.0, wUv.y * 3.0 - uTime * 1.80));
    float vertFade = smoothstep(1.05, 0.20, uv.y);
    float bodyHeat = silhouette * (0.42 + bodyN * 0.72) * vertFade;

    // --- Wisp layer -----------------------------------------------------
    // Higher freq, faster scroll, narrow band of contribution near the top.
    // pow(n, 2.5) makes the wisps sparse (only the brightest noise peaks
    // survive) so they read as detaching tongues, not a uniform haze.
    float wispN = fbm(vec2(wUv.x * 5.0 + 17.0, wUv.y * 6.0 - uTime * 2.80));
    float wispBand = smoothstep(0.35, 0.70, uv.y) * (1.0 - smoothstep(0.75, 0.98, uv.y));
    float wisp = pow(wispN, 2.5) * wispBand * smoothstep(-0.05, 0.55, silhouette);

    // Sharpen body contrast slightly -- real fire has stark dark/bright
    // gradients within the body, not a uniform mid-tone glow.
    bodyHeat = pow(bodyHeat, 1.25) * 1.10;
    float heat = bodyHeat + wisp * 0.70;

    // Discrete sparks (computed once per fragment; brightness contributes
    // to heat so they trigger the white-hot core ramp too).
    float ember = embers(uv);
    heat += ember * 0.60;

    // --- Halo -----------------------------------------------------------
    // Soft warm bleed outside the body silhouette. haloDist is positive
    // outside the body and zero inside; exp falloff with the dist^2 term
    // gives a gaussian-ish bell. Fades to zero at the tip so the halo
    // doesn't poke above the flame.
    float haloDist = max(abs(uv.x) - halfWidth, 0.0);
    float halo = exp(-haloDist * haloDist * 16.0)
               * smoothstep(1.0, 0.10, uv.y)
               * 0.35;

    // --- Compose colour -------------------------------------------------
    vec3 c = fireRamp(heat);
    // Subtle blue/violet hint at the very base. Real fire's hottest
    // combustion zone (where the oxygen meets fuel) is blue/cyan; this
    // peeks through for the bottom ~15% of the flame at moderate heat.
    float baseZone = smoothstep(0.20, 0.05, uv.y) * smoothstep(0.20, 0.55, bodyHeat);
    c += vec3(0.10, 0.20, 0.55) * baseZone * 0.6;

    // White-hot core: HDR (>1) so ACES halos the brightest fragment. The
    // smoothstep gate keeps this contribution off everything below ~0.7
    // heat so only the very brightest spots get the white boost.
    c += vec3(1.50, 1.30, 0.70) * smoothstep(0.70, 1.00, heat);
    // Embers ride the same white-yellow palette but go HDR by themselves
    // so individual sparks read as pinpoint highlights against the body.
    c += vec3(1.80, 1.20, 0.45) * ember * 1.4;
    // Halo contributes a warm amber bleed -- multiplied by flame tint
    // so it follows uFlameColor (cooler greens/blues if the user wants
    // a fantasy magic flame later).
    c += halo * vec3(1.00, 0.50, 0.15);

    c *= uFlameColor * uFlameIntensity * uExposure;

    // Alpha: body silhouette is mostly opaque toward the centre; halo
    // adds a low-alpha aura outside. Clamp to keep alpha well-formed.
    float alpha = smoothstep(0.04, 0.28, heat) + halo * 0.55;
    alpha = clamp(alpha, 0.0, 1.0);
    if (alpha < 0.01) discard;

    fragColor = vec4(c, alpha);
    fragMaterial = vec4(1.0, 0.0, 0.0, alpha);
}
