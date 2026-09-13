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
// different sampler at the descriptor layer, so the drop-in version is this one.
//
// A rotated Vogel disc rather than a grid, which is what makes sixteen binary taps look
// like a penumbra instead of like seventeen bands. See the remarks in the body.
//
// `texelSize` is 1.0 / shadow map side. Pass it in rather than assuming it — a
// hardcoded constant here silently changes the penumbra width when the map is
// resized, which is exactly the kind of coupling this library exists to avoid.
// `pixel` is the fragment's screen coordinate — gl_FragCoord.xy at the call site. Taken as a parameter
// rather than read directly, because this header is included by vertex shaders too and gl_FragCoord does
// not exist there: a built-in referenced inside a function nobody calls still fails the compile.
float blix_sun_shadow_soft(
        sampler2D shadowMap, vec4 coord, float ndotl, float texelSize, float radiusTexels, vec2 pixel) {
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

    // <b>Rotated per pixel, which is what breaks the tradeoff the old comment was stuck in.</b> A grid of
    // binary comparisons has only as many outcomes as it has taps — seventeen, for sixteen taps — and every
    // fragment on a given surface samples the same offsets, so those seventeen values lie down in bands.
    // Widening the kernel spreads the bands out and makes them mottling; narrowing it hides them by giving
    // up the softness. Neither is a fix, because the artefact is the *shared* pattern, not its size.
    //
    // Turning the pattern by a different angle at every pixel converts the banding into high-frequency
    // noise, which the eye reads as softness rather than as structure — and once the structure is gone the
    // kernel is free to be as wide as the look wants. Interleaved gradient noise (Jimenez) is the rotation:
    // one dot product and a fract, stable per pixel, and spectrally far better behaved than a hash.
    float ign = fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
    float rotation = ign * 6.2831853;
    float cosine = cos(rotation);
    float sine = sin(rotation);

    // A Vogel disc rather than a square: sqrt spacing on the golden angle puts the samples at even density
    // over a circle, so a penumbra is round. A square kernel makes a round shadow's edge subtly square, and
    // on a low sun with long shadows that is exactly where the eye is looking.
    const int taps = 16;
    float lit = 0.0;
    for (int i = 0; i < taps; ++i) {
        float radius = sqrt((float(i) + 0.5) / float(taps));
        float theta = float(i) * 2.39996323;
        vec2 unit = vec2(cos(theta), sin(theta)) * radius;
        // Rotate the whole disc by this pixel's angle.
        vec2 turned = vec2(unit.x * cosine - unit.y * sine, unit.x * sine + unit.y * cosine);
        vec2 at = uv + turned * radiusTexels * texelSize;
        lit += (current - bias > texture(shadowMap, at).r) ? 0.0 : 1.0;
    }

    return lit * (1.0 / float(taps));
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
