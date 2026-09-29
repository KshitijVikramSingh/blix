// shadow.glsl's sampling functions, written once for both sampling forms.
//
// Included twice by shadow.glsl, after sampling_form.glsl has spelled one form and then the other:
// never include it directly, and never give it #pragma once, which would drop the second form.
// BLIX_TEX2D/BLIX_TEX3D are the texture parameter types, BLIX_SAMPLER_PARAM and BLIX_SAMPLER_ARG
// add the one sampler the separate form takes, and BLIX_S2D/BLIX_S3D spell a tap.

float blix_sun_shadow(BLIX_TEX2D shadowMap BLIX_SAMPLER_PARAM, vec4 coord, float ndotl) {
    vec3 ndc = coord.xyz / coord.w;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float current = ndc.z;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || current < 0.0 || current > 1.0) {
        return 1.0;
    }
    float bias = mix(0.004, 0.0006, ndotl);
    float closest = texture(BLIX_S2D(shadowMap), uv).r;
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
        BLIX_TEX2D shadowMap BLIX_SAMPLER_PARAM, vec4 coord, float ndotl, float texelSize, float radiusTexels, vec2 pixel) {
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
    // <b>Sixteen by default, overridable by the includer.</b> A define rather than a parameter
    // because the loop bound has to stay a compile-time constant to unroll, and an un-unrolled
    // sixteen-iteration loop with a dependent texture fetch is worse than any tap count it saves.
    //
    // It is worth having a dial here at all because the sun shadow measured 1.3-1.4x of Sponza's
    // whole frame — the largest single shading term in that renderer, larger than IBL and ambient
    // visibility together. What a tap buys is penumbra smoothness, which is a look decision, so the
    // number belongs to the consumer rather than to this file.
    const int taps = BLIX_SHADOW_PCF_TAPS;
    float lit = 0.0;
    for (int i = 0; i < taps; ++i) {
        float radius = sqrt((float(i) + 0.5) / float(taps));
        float theta = float(i) * 2.39996323;
        vec2 unit = vec2(cos(theta), sin(theta)) * radius;
        // Rotate the whole disc by this pixel's angle.
        vec2 turned = vec2(unit.x * cosine - unit.y * sine, unit.x * sine + unit.y * cosine);
        vec2 at = uv + turned * radiusTexels * texelSize;
        lit += (current - bias > texture(BLIX_S2D(shadowMap), at).r) ? 0.0 : 1.0;
    }

    return lit * (1.0 / float(taps));
}

float blix_sun_shadow_cascaded(
        BLIX_TEX2D map0, BLIX_TEX2D map1, BLIX_TEX2D map2 BLIX_SAMPLER_PARAM,
        mat4 vp0, mat4 vp1, mat4 vp2,
        vec3 texelWorld,
        vec3 world, vec3 normal, float ndotl, float radiusTexels, vec2 pixel,
        out int chosen) {
    vec4 coord;
    vec3 at;

    at = blix_shadow_normal_offset(world, normal, ndotl, texelWorld.x, BLIX_SHADOW_OFFSET_TEXELS);
    if (blix_cascade_contains(vp0, at, coord) > 0.5) {
        chosen = 0;
        return blix_sun_shadow_soft(
            map0 BLIX_SAMPLER_ARG, coord, ndotl, 1.0 / float(textureSize(BLIX_S2D(map0), 0).x), radiusTexels, pixel);
    }
    at = blix_shadow_normal_offset(world, normal, ndotl, texelWorld.y, BLIX_SHADOW_OFFSET_TEXELS);
    if (blix_cascade_contains(vp1, at, coord) > 0.5) {
        chosen = 1;
        return blix_sun_shadow_soft(
            map1 BLIX_SAMPLER_ARG, coord, ndotl, 1.0 / float(textureSize(BLIX_S2D(map1), 0).x), radiusTexels, pixel);
    }
    at = blix_shadow_normal_offset(world, normal, ndotl, texelWorld.z, BLIX_SHADOW_OFFSET_TEXELS);
    if (blix_cascade_contains(vp2, at, coord) > 0.5) {
        chosen = 2;
        return blix_sun_shadow_soft(
            map2 BLIX_SAMPLER_ARG, coord, ndotl, 1.0 / float(textureSize(BLIX_S2D(map2), 0).x), radiusTexels, pixel);
    }
    chosen = -1;
    return 1.0;
}

// The same cascade choice, for a consumer that cannot use the filtered lookup above.
//
// <b>This exists because SELECTION is the thing that has to be shared, and sampling is not.</b> A
// volumetric froxel has no surface: no normal to offset along, no grazing angle to scale a bias by,
// and no budget for sixteen taps at grid-resolution^3. So it cannot call
// blix_sun_shadow_cascaded — but if it answers "which cascade is this point in" with its own rule,
// the fog and the surfaces disagree about where a cascade ends, and a shaft of light steps at a
// boundary the geometry does not step at. That is exactly the bug this replaced: froxel.comp picked
// by view depth under a comment claiming it matched the lit pass, which had moved to containment.
//
// It shares blix_cascade_contains verbatim, EDGE margin included. The margin is there for a filter
// kernel this variant does not have, so it is fractionally conservative here — and that is the
// point: agreeing exactly with the surfaces matters more than reclaiming two percent of a cascade.
//
// `ndotl` scales the depth bias for a surface that might shadow itself. A point in a volume has no
// self to shadow, so a volume consumer passes 1.0 and gets the floor.
float blix_sun_shadow_cascaded_hard(
        BLIX_TEX2D map0, BLIX_TEX2D map1, BLIX_TEX2D map2 BLIX_SAMPLER_PARAM,
        mat4 vp0, mat4 vp1, mat4 vp2,
        vec3 world, float ndotl,
        out int chosen) {
    vec4 coord;
    if (blix_cascade_contains(vp0, world, coord) > 0.5) {
        chosen = 0;
        return blix_sun_shadow(map0 BLIX_SAMPLER_ARG, coord, ndotl);
    }
    if (blix_cascade_contains(vp1, world, coord) > 0.5) {
        chosen = 1;
        return blix_sun_shadow(map1 BLIX_SAMPLER_ARG, coord, ndotl);
    }
    if (blix_cascade_contains(vp2, world, coord) > 0.5) {
        chosen = 2;
        return blix_sun_shadow(map2 BLIX_SAMPLER_ARG, coord, ndotl);
    }
    chosen = -1;
    return 1.0;
}
