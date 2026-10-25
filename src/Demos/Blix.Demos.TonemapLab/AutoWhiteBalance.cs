namespace Blix.Demos.TonemapLab;

// Auto white balance as adaptation dynamics: a C# port of lab/tonemap/tmlab/awb.py (the model, its limits and its specs are
// there; the constants are chosen to meet those specs, not fitted to data). Held to the python by `--awb-selftest` and
// lab/tonemap/tests/test_awb_parity.py.

/// <summary>The model's constants. Mutable fields, because every one is a slider.</summary>
internal sealed class AwbParams
{
    public float Tau = 4.0f;           // s
    public float Strength = 0.7f;      // partial adaptation: how much of the estimated cast is removed (0 = off)
    public float Virtual = 0.15f;      // weight of the always-present D65 virtual light
    public float ConfLo = -9.0f;       // meter EV at and below which a frame's colour is not believed
    public float ConfHi = -5.0f;       // and at and above which it is believed fully
    public float KelvinLo = 2200.0f;
    public float KelvinHi = 12000.0f;
    public float TintLimit = 0.06f;
    public float Trim = 0.09f;         // brightest fraction of pixels ignored
    public float Sat0 = 0.3f;          // saturation at which a pixel's weight has halved
    public float SkyWeight = 0.15f;    // what a pixel drawn as sky (alpha 1 in the meter pass) counts for
}

internal sealed class AutoWhiteBalance
{
    public static readonly double D65Mired = 1e6 / 6504.0;

    public double Mired { get; private set; } = D65Mired;
    public double Tint { get; private set; }
    public bool Started { get; private set; }

    /// <summary>Take the cast as already adapted to (a camera cut adapts at once, as the exposure's does).</summary>
    public void Reset(double mired, double tint)
    {
        Mired = mired;
        Tint = tint;
        Started = true;
    }

    private static (double R, double G, double B) Light(double mired)
    {
        var l = WhiteBalance.LightRgb(1e6 / mired);
        var d = WhiteBalance.LightRgb(6504.0);
        return (l.R / d.R, l.G / d.G, l.B / d.B);
    }

    /// <summary>Trimmed, saturation-discounted mean linear RGB of an RGBA16F readback (alpha is the sky mask). awb.measure_mean.</summary>
    public static (double R, double G, double B) Measure(byte[] bytes, int pixels, AwbParams p)
    {
        var rgba = new float[pixels * 4];
        for (var i = 0; i < pixels * 4; i++) rgba[i] = (float)BitConverter.ToHalf(bytes, i * 2);
        return Measure(rgba, pixels, p, useAlphaAsSky: true);
    }

    public static (double R, double G, double B) Measure(float[] rgba, int pixels, AwbParams p, bool useAlphaAsSky)
    {
        var luma = new double[pixels];
        for (var i = 0; i < pixels; i++) luma[i] = 0.2126 * rgba[i * 4] + 0.7152 * rgba[i * 4 + 1] + 0.0722 * rgba[i * 4 + 2];
        var sorted = (double[])luma.Clone();
        Array.Sort(sorted);
        var threshold = sorted[(int)Math.Ceiling((1.0 - p.Trim) * pixels) - 1];
        double sr = 0, sg = 0, sb = 0, sw = 0;
        for (var i = 0; i < pixels; i++)
        {
            if (luma[i] > threshold) continue;
            double r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
            var top = Math.Max(r, Math.Max(g, b));
            var sat = top > 1e-9 ? (top - Math.Min(r, Math.Min(g, b))) / Math.Max(top, 1e-9) : 0.0;
            var w = 1.0 / (1.0 + (sat / p.Sat0) * (sat / p.Sat0));
            if (useAlphaAsSky && rgba[i * 4 + 3] > 0.5f) w *= p.SkyWeight;
            sr += r * w; sg += g * w; sb += b * w; sw += w;
        }
        sw = Math.Max(sw, 1e-12);
        return (sr / sw, sg / sw, sb / sw);
    }

    /// <summary>The (mired, tint) whose gains turn <paramref name="mean"/> neutral, within the limits. awb.cast_of.</summary>
    public static (double Mired, double Tint) CastOf((double R, double G, double B) mean, AwbParams p)
    {
        double r = Math.Max(mean.R, 1e-9), g = Math.Max(mean.G, 1e-9), b = Math.Max(mean.B, 1e-9);
        double lo = 1e6 / p.KelvinHi, hi = 1e6 / p.KelvinLo;
        for (var i = 0; i < 60; i++)
        {
            var mid = 0.5 * (lo + hi);
            var l = Light(mid);
            if (l.R / l.B > r / b) hi = mid; else lo = mid;
        }
        var mired = 0.5 * (lo + hi);
        var light = Light(mired);
        var tint = Math.Clamp(g * light.R / (r * light.G) - 1.0, -p.TintLimit, p.TintLimit);
        return (mired, tint);
    }

    private static double Confidence(double meterEv, AwbParams p) => Math.Clamp((meterEv - p.ConfLo) / (p.ConfHi - p.ConfLo), 0.0, 1.0);

    /// <summary>Advance <paramref name="dt"/> seconds toward the target set by a measured cast and the metered EV. awb.State.step.</summary>
    public void Step(double dt, (double Mired, double Tint) measured, double meterEv, AwbParams p)
    {
        var w = (1.0 - p.Virtual) * Confidence(meterEv, p);
        var tm = Math.Clamp(D65Mired + w * (measured.Mired - D65Mired), 1e6 / p.KelvinHi, 1e6 / p.KelvinLo);
        var tt = Math.Clamp(w * measured.Tint, -p.TintLimit, p.TintLimit);
        var k = 1.0 - Math.Exp(-dt / p.Tau);
        Mired += (tm - Mired) * k;
        Tint += (tt - Tint) * k;
        Started = true;
    }

    /// <summary>Linear RGB gains for D65 + strength * (state - D65), luminance-normalised so exposure does not move. awb.State.gains.</summary>
    public (float R, float G, float B) Gains(AwbParams p) => GainsFor(D65Mired + p.Strength * (Mired - D65Mired), p.Strength * Tint);

    public static (float R, float G, float B) GainsFor(double mired, double tint)
    {
        var l = Light(mired);
        double gr = l.G / l.R, gg = 1.0 / (1.0 + tint), gb = l.G / l.B;
        var norm = 1.0 / (0.2126 * gr + 0.7152 * gg + 0.0722 * gb);
        return ((float)(gr * norm), (float)(gg * norm), (float)(gb * norm));
    }
}
