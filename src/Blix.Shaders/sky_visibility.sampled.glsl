// sky_visibility.glsl's sampling functions, written once for both sampling forms.
//
// Included twice by sky_visibility.glsl, after sampling_form.glsl has spelled one form and then the other:
// never include it directly, and never give it #pragma once, which would drop the second form.
// BLIX_TEX2D/BLIX_TEX3D are the texture parameter types, BLIX_SAMPLER_PARAM and BLIX_SAMPLER_ARG
// add the one sampler the separate form takes, and BLIX_S2D/BLIX_S3D spell a tap.

BlixSkySample blix_skyFetch(
    BLIX_TEX3D shA, BLIX_TEX3D shB, BLIX_TEX3D shC BLIX_SAMPLER_PARAM,
    vec3 boundsMin, vec3 invSpan, vec3 worldPos)
{
    vec3 uv = clamp((worldPos - boundsMin) * invSpan, vec3(0.0), vec3(1.0));
    BlixSkySample s;
    s.sh0 = texture(BLIX_S3D(shA), uv);
    s.sh1 = texture(BLIX_S3D(shB), uv);
    s.l2p2 = texture(BLIX_S3D(shC), uv).x;
    return s;
}

/// Fraction of the sky visible from `worldPos` looking along `dir`, in [0,1].
///
/// `normalPush` moves the lookup a little along `dir` before sampling: a cell straddling a wall
/// holds both sides of it, and a query taken exactly on the surface reads the enclosure on the
/// wrong side. It is the same failure as shadow acne and the same remedy.
float blix_skyVisibility(
    BLIX_TEX3D shA, BLIX_TEX3D shB, BLIX_TEX3D shC BLIX_SAMPLER_PARAM,
    vec3 boundsMin, vec3 invSpan, float normalPush,
    vec3 worldPos, vec3 dir)
{
    vec3 uv = clamp((worldPos + dir * normalPush - boundsMin) * invSpan, vec3(0.0), vec3(1.0));
    vec4 sh0 = texture(BLIX_S3D(shA), uv);
    vec4 sh1 = texture(BLIX_S3D(shB), uv);
    float l2p2 = texture(BLIX_S3D(shC), uv).x;

    // Cosine-convolved L2 evaluation (Ramamoorthi & Hanrahan): band coefficients pi, 2pi/3, pi/4.
    // <b>The quadratic band is what makes a cone expressible.</b> L0 is direction-independent, so
    // under L1 alone a vault ceiling inherited the arcade opening's brightness and read as
    // sky-facing — one linear lobe cannot subtract a bright opening from a surface pointing away
    // from it. The same deficit at the other end lost a courtyard floor's narrow zenith cone.
    const float Y0 = 0.282095, Y1 = 0.488603, Y2 = 1.092548, Y20C = 0.315392, Y22C = 0.546274;
    float band2 = Y2 * sh1.x * dir.x * dir.y
                + Y2 * sh1.y * dir.y * dir.z
                + Y20C * sh1.z * (3.0 * dir.z * dir.z - 1.0)
                + Y2 * sh1.w * dir.x * dir.z
                + Y22C * l2p2 * (dir.x * dir.x - dir.y * dir.y);
    return clamp((BLIX_SKYVIS_PI * Y0 * sh0.x
                  + (2.0 * BLIX_SKYVIS_PI / 3.0) * Y1 * dot(sh0.yzw, dir)
                  + (BLIX_SKYVIS_PI / 4.0) * band2) / BLIX_SKYVIS_PI, 0.0, 1.0);
}

/// Mean sky visibility over the WHOLE sphere, for a point inside a participating medium.
///
/// <b>A froxel has no normal, so the cosine form above does not apply to it.</b> What a point in
/// air scatters toward the eye is sky arriving from every direction at once, not sky arriving over
/// a hemisphere weighted by a surface it does not have. The spherical mean of an SH expansion is
/// its L0 coefficient alone — every higher band integrates to zero over the sphere — so this costs
/// one texel and one multiply, and needs neither of the other two band textures.
float blix_skyVisibilityMean(BLIX_TEX3D shA BLIX_SAMPLER_PARAM, vec3 boundsMin, vec3 invSpan, vec3 worldPos)
{
    vec3 uv = clamp((worldPos - boundsMin) * invSpan, vec3(0.0), vec3(1.0));
    return clamp(texture(BLIX_S3D(shA), uv).x * 0.282095, 0.0, 1.0);
}
