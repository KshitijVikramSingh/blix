#pragma once

// Single-tap directional (sun) shadow lookup — the look-free technique shared by the
// engine's lit demos (TankArena, Bulwark). `coord` is the fragment in the light's clip
// space (uSunShadowVP * world); `ndotl` (surface-to-sun dot) drives an angle-scaled
// depth bias to suppress acne on grazing faces. Vulkan convention: clip Z is already
// [0,1] and the Y-flip is baked into the light's ortho, so NDC->UV needs no flip.
// Returns 1 = lit, 0 = shadowed; the caller composes it into its own lighting/mood.
// Pair with Blix.Graphics.GraphicsMatrices.SunShadowViewProjection on the CPU side.
//
// No PCF — clean enough for low-poly. A future soft variant can live alongside.
float blix_sun_shadow(sampler2D shadowMap, vec4 coord, float ndotl) {
    vec3 ndc = coord.xyz / coord.w;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float current = ndc.z;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || current < 0.0 || current > 1.0) {
        return 1.0;
    }
    float bias = mix(0.004, 0.0006, ndotl);
    float closest = texture(shadowMap, uv).r;
    return (current - bias > closest) ? 0.0 : 1.0;
}
