#pragma once

// Single-tap directional (sun) shadow lookup — the look-free technique shared by the
// engine's lit demos (TankArena, Bulwark). `coord` is the fragment in the light's clip
// space (uSunShadowVP * world); `ndotl` (surface-to-sun dot) drives an angle-scaled
// depth bias to suppress acne on grazing faces. Vulkan convention: clip Z is already
// [0,1] and the Y-flip is baked into the light's ortho, so NDC->UV needs no flip.
// Returns 1 = lit, 0 = shadowed; the caller composes it into its own lighting/mood.
// Pair with Blix.Graphics.GraphicsMatrices.SunShadowViewProjection on the CPU side.
//
// Two variants: a single tap (hard, cheapest) and a percentage-closer filter
// (blix_sun_shadow_soft, below) for scenes where a hard shadow edge reads as a
// staircase rather than as a shadow.
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

// Percentage-closer filtered directional shadow. Same contract as the single-tap
// version above — 1 = lit, 0 = shadowed — but averages a 4x4 grid of comparisons
// spread over `radiusTexels` of the map, so the answer is a fraction rather than a
// yes/no and the edge of a shadow becomes a gradient a few centimetres wide.
//
// Manual taps against a plain sampler2D rather than hardware comparison against a
// sampler2DShadow: the compare-mode sampler is faster and smoother, and it needs a
// different sampler at the descriptor layer, so the drop-in version is this one. A
// 4x4 kernel is enough to hide a staircase without turning a wall's shadow to mush.
//
// `texelSize` is 1.0 / shadow map side. Pass it in rather than assuming it — a
// hardcoded constant here silently changes the penumbra width when the map is
// resized, which is exactly the kind of coupling this library exists to avoid.
float blix_sun_shadow_soft(
        sampler2D shadowMap, vec4 coord, float ndotl, float texelSize, float radiusTexels) {
    vec3 ndc = coord.xyz / coord.w;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float current = ndc.z;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || current < 0.0 || current > 1.0) {
        return 1.0;
    }
    // Small, because blix_shadow_normal_offset is expected to have moved the sample off its
    // own surface already. A large depth bias on top of a normal offset buys nothing but
    // peter-panning — shadows that float free of the thing casting them.
    float bias = mix(0.0012, 0.0002, ndotl);
    // Half-texel steps, so the sixteen taps span a little over two texels rather than six.
    // A wide kernel of binary comparisons is not soft, it is mottled: sixteen yes/no answers
    // spread over half a metre of ground quantise into visible blotches, which is most of what
    // reads as "dirty".
    float step = radiusTexels * texelSize * 0.34;
    float lit = 0.0;
    for (int y = -2; y <= 1; ++y) {
        for (int x = -2; x <= 1; ++x) {
            // Half-texel offsets so the four inner taps straddle the fragment rather
            // than one of them landing exactly on it and dominating the average.
            vec2 at = uv + vec2(float(x) + 0.5, float(y) + 0.5) * step;
            lit += (current - bias > texture(shadowMap, at).r) ? 0.0 : 1.0;
        }
    }
    return lit * (1.0 / 16.0);
}

// World-space offset to apply to a position BEFORE projecting it into the light's
// clip space, so that a surface does not shadow itself.
//
// This is the fix for shadow acne, and it is a different and better one from depth
// bias. Depth bias pushes the comparison along the light's Z, which is the wrong axis
// for a surface the light grazes: the error a shadow map makes on a near-parallel
// surface is that one texel covers a long slice of it, so the depth stored for the
// texel centre is wrong everywhere else in the texel by an amount that grows without
// bound as the angle closes. Enough depth bias to cover that detaches every shadow
// from its caster; not enough leaves the broad dirty smears that look like dirt on
// the ground rather than like shadow.
//
// Offsetting ALONG THE SURFACE NORMAL moves the sample sideways, off the surface, by
// a distance related to how wide a texel is in world units — which is the actual
// scale of the error. Scaled by how far from face-on the light is, so a surface
// pointing at the sun (where the error is small) is barely moved and a grazing one is
// moved by most of a texel.
//
// `texelWorld` is the world-space size of one shadow-map texel: the light's ortho
// extent divided by the map's side.
vec3 blix_shadow_normal_offset(vec3 world, vec3 normal, float ndotl, float texelWorld, float texels) {
    float slope = clamp(1.0 - ndotl, 0.0, 1.0);
    // The square root keeps a little offset even on face-on surfaces, where a pure
    // linear falloff leaves nothing at all and the last of the acne survives.
    return world + normal * (texelWorld * texels * (0.30 + 0.70 * sqrt(slope)));
}
