#pragma once

// Tonemap operators -- HDR-linear input, [0,1] LDR-linear output.
//
// All four operators take and return linear values. Apply gamma encode
// AFTER the operator. Apply colour grading (saturation, contrast,
// temperature) BEFORE the operator while still in HDR linear space; the
// operator's curve assumes a meaningful dynamic range and grading after
// tonemap just shifts already-clipped LDR values around.
//
// --- THERE IS A SECOND COPY OF THESE CURVES. EDIT BOTH. -------------------
// Blix.Graphics.Images/Tonemap.cs is the CPU twin, and it is SHIPPED, not a
// test fixture: a capture is read back from the HDR scene target before the
// present pass runs, so something has to apply the curve in C#. That pair has
// already gone wrong once -- the C# copy was hardcoded to ACES while this file
// offered four curves, so --tonemap-mode moved the screen and left every
// capture alone, and both halves were internally consistent the whole time.
//
// Blix.Test.Graphics section BD is what makes the two answerable to each
// other: it reads this file and fails when a constant named in Tonemap.cs
// stops appearing here, or when the mode thresholds change shape. A deliberate
// change to a curve or a threshold is therefore a two-file change.
// Same arrangement as sheen.glsl / section BE's twin.

// Narkowicz 2015 (Knarkowicz/Heitz) fit of the ACES Filmic curve to a
// rational polynomial. The "industry standard" cinematic look: saturated
// shadows, smooth highlight rolloff, slight warm bias.
vec3 blix_acesFilm(vec3 x)
{
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

// Classic Reinhard. Flatter than ACES, gentler highlight rolloff. Useful
// when the scene already has heavy contrast and ACES feels punchy.
vec3 blix_reinhard(vec3 x)
{
    return x / (1.0 + x);
}

// No curve, just a hard clamp. Debug reference -- shows where values clip
// without any softening so you can see if highlights are blown.
vec3 blix_neutral(vec3 x)
{
    return clamp(x, 0.0, 1.0);
}

// Approximated AgX (Troy Sobotka). Stretches to log space, applies a
// smootherstep sigmoid, decodes. Reads as "filmic but more neutral than
// ACES" -- preserves hue better in saturated highlights.
vec3 blix_agx(vec3 x)
{
    x = max(x, vec3(0.0));
    const float minEv = -12.47393;
    const float maxEv =   4.026069;
    vec3 logX = log2(max(x, vec3(1e-6)));
    vec3 t = clamp((logX - vec3(minEv)) / (maxEv - minEv), 0.0, 1.0);
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
}

// Mode selector. Caller passes a float uniform in [0..3]; values <0.5 pick
// ACES, <1.5 AgX, <2.5 Reinhard, else Neutral. Matches the engine's
// existing tonemap-mode convention.
vec3 blix_tonemap(vec3 hdr, float mode)
{
    if (mode < 0.5)      return blix_acesFilm(hdr);
    else if (mode < 1.5) return blix_agx(hdr);
    else if (mode < 2.5) return blix_reinhard(hdr);
    else                 return blix_neutral(hdr);
}

// --- Colour grading -------------------------------------------------------
// All in HDR-linear space.

// Saturation: blend between luminance-only and full colour. Rec. 709
// weights (close enough to perceived brightness for sRGB content).
vec3 blix_saturate(vec3 c, float saturation)
{
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
    return mix(vec3(luma), c, saturation);
}

// Contrast: pivots around 0.5 (mid-grey in linear). >1 punches blacks and
// whites; <1 flattens.
vec3 blix_contrast(vec3 c, float contrast)
{
    return (c - vec3(0.5)) * contrast + vec3(0.5);
}
