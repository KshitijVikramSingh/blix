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

// --- Cascaded directional shadows -----------------------------------------
//
// Selection only: the sampling is blix_sun_shadow_soft above, called once, on whichever
// cascade contains the fragment. That is the whole reason this is three lines of dispatch
// rather than a fourth percentage-closer filter — there are already three PCF
// implementations in this tree (here, Sponza's, VulkanLit's) and the way to stop there
// being a fourth is for the cascade layer not to need one.
//
// <b>THREE, fixed, and that is a portability fact rather than a preference.</b> Sampling a
// sampler2D array at a dynamically computed index needs
// shaderSampledImageArrayDynamicIndexing, which is not guaranteed — so every implementation
// that works everywhere branches on a constant index instead. Three named samplers say that
// honestly, where an array parameter would look general and only work on some drivers. Making
// the count a #define variant would make it the first axis of a shader permutation matrix, for
// a number nobody has wanted to change.
//
//   texelSizes  1.0 / map side, per cascade. Separate because the three maps need not be the
//               same size, and the PCF radius is measured in texels.
//   chosen      which cascade answered, or -1 when no cascade contains the fragment. For a debug
//               tint, and for finding out that a scene is spending three passes on one cascade's
//               work.
//
// <b>Selection is by CONTAINMENT, not by view depth, and that is not the textbook choice.</b> The
// usual scheme slices the view frustum by depth and picks by `dot(world - eye, forward)`. It assumes
// a camera standing AMONG the things it looks at. A camera that orbits its subject from outside is
// the other case, and there the frustum at the subject's depth is far wider than the subject —
// measured on the studio stage, every split ratio put the subject in a cascade COARSER than the
// single origin-fitted box it replaced, by 1.8x to 4.1x. Containment lets the boxes be fitted to
// the content instead of to the frustum, which is the arrangement that stage actually wants, and it
// costs the strictly more robust rule: the first cascade that can answer, answers.
//
// Returns 1 = lit, 0 = shadowed. Outside every cascade it returns 1: unshadowed is the honest answer
// where there is no data, and it is the one that does not draw a hard edge across the world.
float blix_cascade_contains(mat4 vp, vec3 world, out vec4 coord) {
    coord = vp * vec4(world, 1.0);
    vec3 ndc = coord.xyz / coord.w;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    // A margin, so a fragment is not handed to a cascade whose PCF kernel would reach off the edge
    // of the map and read whatever the clamp returns.
    const float EDGE = 0.02;
    if (uv.x < EDGE || uv.x > 1.0 - EDGE || uv.y < EDGE || uv.y > 1.0 - EDGE) return 0.0;
    if (ndc.z < 0.0 || ndc.z > 1.0) return 0.0;
    return 1.0;
}

float blix_sun_shadow_cascaded(
        sampler2D map0, sampler2D map1, sampler2D map2,
        mat4 vp0, mat4 vp1, mat4 vp2,
        vec3 texelSizes,
        vec3 world, float ndotl, float radiusTexels, vec2 pixel,
        out int chosen) {
    vec4 coord;
    if (blix_cascade_contains(vp0, world, coord) > 0.5) {
        chosen = 0;
        return blix_sun_shadow_soft(map0, coord, ndotl, texelSizes.x, radiusTexels, pixel);
    }
    if (blix_cascade_contains(vp1, world, coord) > 0.5) {
        chosen = 1;
        return blix_sun_shadow_soft(map1, coord, ndotl, texelSizes.y, radiusTexels, pixel);
    }
    if (blix_cascade_contains(vp2, world, coord) > 0.5) {
        chosen = 2;
        return blix_sun_shadow_soft(map2, coord, ndotl, texelSizes.z, radiusTexels, pixel);
    }
    chosen = -1;
    return 1.0;
}

// A flat tint per cascade, for looking at where the splits landed. Magenta means "beyond the
// last cascade", which is the case a still picture otherwise cannot distinguish from "lit".
vec3 blix_cascade_tint(int chosen) {
    if (chosen == 0) return vec3(1.0, 0.45, 0.45);
    if (chosen == 1) return vec3(0.45, 1.0, 0.45);
    if (chosen == 2) return vec3(0.45, 0.6, 1.0);
    return vec3(1.0, 0.2, 1.0);
}
