using System.Numerics;

namespace Blix.Graphics.Images;

/// <summary>
/// The CPU twin of <c>Blix.Shaders/tonemap.glsl</c> (and <c>gt7.glsl</c>): HDR-linear in, [0,1] LDR-linear out.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists because a capture is taken before the present pass runs.</b> The capture tool reads
/// back the HDR scene target rather than the swapchain, which is what lets a capture hold real
/// radiance and makes the curve an offline choice — but it also means the curve has to be applied
/// here, in C#, by something. One copy of that already existed, hardcoded to ACES, and the result was
/// that <c>--tonemap-mode</c> moved the screen and not the picture.
/// </para>
/// <para>
/// <b>This is a second implementation of a thing that lives in GLSL, and that is a real hazard.</b>
/// The honest response is not a comment saying so — there was already one of those, reading "if a
/// capture ever disagrees with the screen, this pair is where to look first". It is
/// <c>Blix.Test.Graphics</c> section BD, which reads <c>tonemap.glsl</c> and fails if the constants
/// below have stopped appearing in it. A text check is crude next to reflecting an interface out of
/// SPIR-V, but it is the same idea: make the second copy answerable to the first rather than trusting
/// two files to stay in step.
/// </para>
/// </remarks>
public static class Tonemap
{
    // Narkowicz 2015 fit of the ACES filmic curve. Named rather than inlined so the drift check has
    // something to look for in the GLSL.
    public const float AcesA = 2.51f;
    public const float AcesB = 0.03f;
    public const float AcesC = 2.43f;
    public const float AcesD = 0.59f;
    public const float AcesE = 0.14f;

    /// <summary>AgX's log-space window, in stops.</summary>
    public const float AgxMinEv = -12.47393f;

    /// <inheritdoc cref="AgxMinEv"/>
    public const float AgxMaxEv = 4.026069f;

    /// <summary>ACES filmic. Saturated shadows, smooth highlight rolloff, a warm bias.</summary>
    public static Vector3 Aces(Vector3 x) => new(
        Aces(x.X), Aces(x.Y), Aces(x.Z));

    private static float Aces(float x) =>
        Math.Clamp((x * (AcesA * x + AcesB)) / (x * (AcesC * x + AcesD) + AcesE), 0f, 1f);

    /// <summary>Classic Reinhard. Flatter than ACES, gentler rolloff.</summary>
    public static Vector3 Reinhard(Vector3 x) => new(
        x.X / (1f + x.X), x.Y / (1f + x.Y), x.Z / (1f + x.Z));

    /// <summary>No curve, just a clamp. The reference that shows where values are clipping.</summary>
    public static Vector3 Neutral(Vector3 x) => new(
        Math.Clamp(x.X, 0f, 1f), Math.Clamp(x.Y, 0f, 1f), Math.Clamp(x.Z, 0f, 1f));

    /// <summary>Approximated AgX. Filmic but more neutral than ACES; keeps hue in bright colour.</summary>
    public static Vector3 Agx(Vector3 x) => new(
        Agx(x.X), Agx(x.Y), Agx(x.Z));

    private static float Agx(float x)
    {
        var logX = MathF.Log2(MathF.Max(MathF.Max(x, 0f), 1e-6f));
        var t = Math.Clamp((logX - AgxMinEv) / (AgxMaxEv - AgxMinEv), 0f, 1f);
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    // ---- GT7 ---------------------------------------------------------------------------------------------------------------
    // Polyphony Digital's GT7 tone mapping (SIGGRAPH 2025), MIT, Copyright (c) 2025 Polyphony Digital Inc. (see THIRD_PARTY_NOTICES.md),
    // plus the lab's eighth parameter and the gamut map. A port of gt7.glsl, which is a port of lab/tonemap/tmlab/gt7.py; the
    // parameter sets are generated (Gt7Presets.Generated.cs). Scene-linear Rec.709 in, scene 1.0 = Gt7Presets.SceneWhiteNits.

    private static float Gt7Smoothstep(float x, float e0, float e1)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Gt7Curve(float x, in Gt7Parameters p, float target)
    {
        if (x < 0f) return 0f;
        var k = (p.LinearSection - 1f) / (p.Alpha - 1f);
        var x0 = p.LinearSection * target;
        var span = target * k;
        var wLin = Gt7Smoothstep(x, 0f, p.GrayPoint);
        var toe = p.GrayPoint * MathF.Pow(x / p.GrayPoint, p.ToeStrength);
        var lower = (1f - wLin) * toe + wLin * x;
        if (x < x0) return lower;
        var t = (x - x0) / span;
        if (p.ShoulderTail == 0f) return x0 + span * (1f - MathF.Exp(-t));
        return x0 + span * (1f - MathF.Pow(1f + p.ShoulderTail * t, -1f / p.ShoulderTail));
    }

    // ST 2084, frame-buffer units (1.0 = 100 nits).
    private static float Gt7PqInverse(float v)
    {
        var y = MathF.Max(v, 0f) * 100f / 10000f;
        var ym = MathF.Pow(y, 0.1593017578125f);
        return MathF.Pow(2f, 78.84375f * (MathF.Log2(0.8359375f + 18.8515625f * ym) - MathF.Log2(1f + 18.6875f * ym)));
    }

    private static float Gt7Pq(float n)
    {
        n = Math.Clamp(n, 0f, 1f);
        var np = MathF.Pow(n, 1f / 78.84375f);
        var l = MathF.Max(np - 0.8359375f, 0f) / (18.8515625f - 18.6875f * np);
        return MathF.Pow(l, 1f / 0.1593017578125f) * 10000f / 100f;
    }

    private static Vector3 Gt7ToIctcp(Vector3 rgb)
    {
        var l = (1688f * rgb.X + 2146f * rgb.Y + 262f * rgb.Z) / 4096f;
        var m = (683f * rgb.X + 2951f * rgb.Y + 462f * rgb.Z) / 4096f;
        var s = (99f * rgb.X + 309f * rgb.Y + 3688f * rgb.Z) / 4096f;
        float lp = Gt7PqInverse(l), mp = Gt7PqInverse(m), sp = Gt7PqInverse(s);
        return new Vector3((2048f * lp + 2048f * mp) / 4096f,
            (6610f * lp - 13613f * mp + 7003f * sp) / 4096f,
            (17933f * lp - 17390f * mp - 543f * sp) / 4096f);
    }

    private static Vector3 Gt7FromIctcp(Vector3 c)
    {
        var l = c.X + 0.00860904f * c.Y + 0.11103f * c.Z;
        var m = c.X - 0.00860904f * c.Y - 0.11103f * c.Z;
        var s = c.X + 0.560031f * c.Y - 0.320627f * c.Z;
        float ll = Gt7Pq(l), ml = Gt7Pq(m), sl = Gt7Pq(s);
        return Vector3.Max(new Vector3(
            3.43661f * ll - 2.50645f * ml + 0.0698454f * sl,
            -0.79133f * ll + 1.9836f * ml - 0.192271f * sl,
            -0.0259499f * ll - 0.0989137f * ml + 1.12486f * sl), Vector3.Zero);
    }

    // Linear Rec.2020 frame-buffer values in, tone-mapped values out (HDR: 0..target; SDR: 0..1 after outputScale).
    private static Vector3 Gt7Apply(Vector3 rgb, in Gt7Parameters p, float target, float outputScale)
    {
        rgb = Vector3.Max(rgb, Vector3.Zero);
        var ucs = Gt7ToIctcp(rgb);
        var skewed = new Vector3(Gt7Curve(rgb.X, p, target), Gt7Curve(rgb.Y, p, target), Gt7Curve(rgb.Z, p, target));
        var skewedUcs = Gt7ToIctcp(skewed);
        var targetUcsL = Gt7ToIctcp(new Vector3(target)).X;
        var chromaScale = 1f - Gt7Smoothstep(ucs.X / targetUcsL, p.FadeStart, p.FadeEnd);
        var scaled = Gt7FromIctcp(new Vector3(skewedUcs.X, ucs.Y * chromaScale, ucs.Z * chromaScale));
        var blended = Vector3.Lerp(skewed, scaled, p.BlendRatio);
        return outputScale * Vector3.Min(blended, new Vector3(target));
    }

    // Rec.709 <-> Rec.2020: the BT.2087 matrix the lab uses (color.py), and its exact inverse.
    private static Vector3 Rec709ToRec2020(Vector3 c) => new(
        0.6274f * c.X + 0.3293f * c.Y + 0.0433f * c.Z,
        0.0691f * c.X + 0.9195f * c.Y + 0.0114f * c.Z,
        0.0164f * c.X + 0.0880f * c.Y + 0.8956f * c.Z);

    private static Vector3 Rec2020ToRec709(Vector3 c) => new(
        1.6605112079f * c.X - 0.5877105887f * c.Y - 0.0728006192f * c.Z,
        -0.1245614060f * c.X + 1.1329605139f * c.Y - 0.0083991078f * c.Z,
        -0.0181676866f * c.X - 0.1005605980f * c.Y + 1.1187282846f * c.Z);

    /// <summary>
    /// A colour the tone map leaves outside the Rec.709 gamut comes out with a negative channel. Clipping it changes hue as well as
    /// chroma; this moves toward the grey of the same luma by the least amount that makes the smallest channel zero, so luminance is
    /// kept and only chroma is given up. Colours already inside are untouched.
    /// </summary>
    public static Vector3 GamutMap709(Vector3 c)
    {
        var y = 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        var mn = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        if (mn >= 0f) return c;
        if (y <= 0f) return Vector3.Zero;
        var t = y / (y - mn);
        return Vector3.Max(new Vector3(y) + t * (c - new Vector3(y)), Vector3.Zero);
    }

    /// <summary>GT7, SDR: HDR-linear Rec.709 in (scene 1.0 = 250 nits), [0,1] display-linear Rec.709 out, like the other curves.</summary>
    public static Vector3 Gt7Sdr(Vector3 hdr709, Gt7Parameters p)
    {
        var mapped = Gt7Apply(Rec709ToRec2020(hdr709 * (Gt7Presets.SceneWhiteNits / 100f)), p, 2.5f, 0.4f);
        return Vector3.Clamp(GamutMap709(Rec2020ToRec709(mapped)), Vector3.Zero, Vector3.One);
    }

    /// <summary>
    /// GT7 aimed at a display peak (250 to 10000 nits), returned as extended-linear Rec.709 relative to the display's SDR white
    /// (1.0 is UI white; the peak lands at <paramref name="peakNits"/> / <paramref name="sdrWhiteNits"/>). For an HDR swapchain.
    /// </summary>
    public static Vector3 Gt7Hdr(Vector3 hdr709, Gt7Parameters p, float peakNits, float sdrWhiteNits)
    {
        var mapped = Gt7Apply(Rec709ToRec2020(hdr709 * (Gt7Presets.SceneWhiteNits / 100f)), p, peakNits / 100f, 1f);
        return GamutMap709(Rec2020ToRec709(mapped * (100f / sdrWhiteNits)));
    }

    /// <summary>
    /// The mode selector, with the same thresholds the shader's <c>blix_tonemap</c> uses.
    /// </summary>
    /// <remarks>
    /// A float rather than an enum because that is what the uniform carries, and the thresholds are
    /// the convention the engine already had: &lt;0.5 ACES, &lt;1.5 AgX, &lt;2.5 Reinhard, &lt;3.5
    /// neutral, &lt;4.5 GT7 (published), else GT7 (tuned). Restating them here is exactly what the drift
    /// check watches.
    /// </remarks>
    public static Vector3 Apply(Vector3 hdr, float mode) => mode switch
    {
        < 0.5f => Aces(hdr),
        < 1.5f => Agx(hdr),
        < 2.5f => Reinhard(hdr),
        < 3.5f => Neutral(hdr),
        < 4.5f => Gt7Sdr(hdr, Gt7Presets.Published),
        _ => Gt7Sdr(hdr, Gt7Presets.Tuned),
    };

    /// <summary>The same selector, by name.</summary>
    public static Vector3 Apply(Vector3 hdr, TonemapCurve curve) => Apply(hdr, (float)curve);
}

/// <summary>The engine's tonemap curves, in the order <c>blix_tonemap</c> selects them.</summary>
/// <remarks>
/// A name for the number the uniform already carries: cast to float to send it. A setting declared
/// as this type is a dropdown on the overlay and takes names on the command line
/// (<c>--tonemap-mode Neutral</c>), where the float it replaces was a slider with three meaningless
/// positions between every pair of curves.
/// </remarks>
public enum TonemapCurve
{
    Aces,
    AgX,
    Reinhard,
    Neutral,

    /// <summary>GT7 with the published parameters (Polyphony Digital; see THIRD_PARTY_NOTICES.md).</summary>
    Gt7,

    /// <summary>GT7 with the lab-tuned parameters (<c>Gt7Presets.Tuned</c>, generated from lab/tonemap).</summary>
    Gt7Tuned,
}
