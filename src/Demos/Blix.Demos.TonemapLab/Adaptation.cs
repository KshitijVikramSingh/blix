namespace Blix.Demos.TonemapLab;

// Auto-exposure as adaptation dynamics: a C# port of lab/tonemap/tmlab/adaptation.py (the model, its docstring and its specs
// are there) and of exposure.py's histogram. Held to the python by `--adaptation-selftest` and `--meter-selftest` and
// lab/tonemap/tests/test_csharp_parity.py, because a second copy of arithmetic is only as good as its check.
//
// Everything is in log2 luminance (EV) of scene-linear Rec.709 luma. S is the short-term level, A the long-term one; the
// exposure is `Key - S`. See the python for why: a short-term stage held inside a 3-5 EV window around a long-term stage that
// can itself only move over a 12 EV span (GT7's slides 176-221; the constants are tuned to the lab's own specs, not fitted).

/// <summary>The model's constants. Mutable fields, because every one is a slider.</summary>
/// <summary>The scene target stores scene-linear light divided by this (scene.frag), so a sun far above half-float range survives; readbacks multiply it back.</summary>
internal static class SceneStore
{
    public const float Scale = 32f;
}

internal sealed class AdaptationParams
{
    public float TauSLight = 0.4f;   // s: short-term, scene brighter than S
    public float TauSDark = 1.2f;    // s: short-term, scene darker than S
    public float RangeDark = 2.0f;   // EV below the long-term level S may sit (dark + light = 3..5 EV in total)
    public float RangeLight = 2.0f;  // EV above
    public float TauALight = 90.0f;  // s: long-term, brightening
    public float TauADark = 600.0f;  // s: long-term, darkening
    public float LightLimit = 2.0f;  // EV above the day level: the top of the long-term span
    public float LongRange = 12.0f;  // EV: the long-term span (GT7's figure)
    public float TrimHi = 0.09f;     // fraction of the brightest pixels the meter ignores (signs, speculars, the sun)
}

internal sealed class AdaptationState
{
    /// <summary>Where the exposure puts the metered level: log2 of mid grey, 0.18.</summary>
    public static readonly double Key = Math.Log2(0.18);

    public bool Started { get; private set; }
    public double Metered { get; private set; }   // m, the latest metered scene level
    public double Short { get; private set; }     // S
    public double Long { get; private set; }      // A
    public double DayLevel { get; private set; }  // the level the eye started adapted to; the long-term span is placed relative to it

    /// <summary>Adapt fully to <paramref name="m"/> and take it as the day level (the long-term span is placed from it).</summary>
    public void Reset(double m)
    {
        Metered = Short = Long = DayLevel = m;
        Started = true;
    }

    public void Observe(double m) => Metered = m;

    /// <summary>Advance by <paramref name="dt"/> seconds toward the latest metered level. Exactly adaptation.py's `simulate` step.</summary>
    public void Step(double dt, AdaptationParams p)
    {
        if (!Started) return;
        var lo = DayLevel + p.LightLimit - p.LongRange;
        var hi = DayLevel + p.LightLimit;
        var v = Metered;
        var ka = 1.0 - Math.Exp(-dt / (v > Long ? p.TauALight : p.TauADark));
        Long = Math.Clamp(Long + (v - Long) * ka, lo, hi);
        var ks = 1.0 - Math.Exp(-dt / (v > Short ? p.TauSLight : p.TauSDark));
        var s = Short + (v - Short) * ks;
        Short = Math.Clamp(s, Long - p.RangeDark, Long + p.RangeLight);
    }

    /// <summary>The exposure error m - S: negative is darker than proper, positive brighter.</summary>
    public double Error => Metered - Short;

    /// <summary>log2 of the exposure multiplier applied to scene-linear radiance.</summary>
    public double ExposureEv => Key - Short;
}

/// <summary>The trimmed log-average luminance of a frame, from its log2-luminance histogram (exposure.py / adaptation.meter_ev).</summary>
internal static class Meter
{
    private const int Bins = 96;
    private const double Lo = -14.0;
    private const double Hi = 10.0;
    private const double Width = (Hi - Lo) / Bins;   // 0.25 EV

    /// <summary>Normalised histogram of log2 luminance (Rec.709 luma, clipped to 2^-14 .. 2^10) over <paramref name="rgb"/>, three floats a pixel.</summary>
    public static double[] Histogram(ReadOnlySpan<float> rgb)
    {
        var counts = new double[Bins];
        var n = rgb.Length / 3;
        for (var i = 0; i < n; i++)
        {
            var y = 0.2126 * rgb[i * 3] + 0.7152 * rgb[i * 3 + 1] + 0.0722 * rgb[i * 3 + 2];
            y = Math.Clamp(y, Math.Pow(2.0, Lo), Math.Pow(2.0, Hi));
            var bin = (int)Math.Floor((Math.Log2(y) - Lo) / Width);
            counts[Math.Clamp(bin, 0, Bins - 1)] += 1.0;
        }
        for (var i = 0; i < Bins; i++) counts[i] /= Math.Max(n, 1);
        return counts;
    }

    /// <summary>
    /// The histogram of an equirectangular image (four floats a pixel, as ImageLoader gives them), weighted by solid angle
    /// (cos of each row's latitude), because an equirect over-samples the poles. hdri.py's `weighted_histogram`.
    /// </summary>
    public static double[] HistogramEquirect(float[] rgba, int width, int height)
    {
        var counts = new double[Bins];
        var total = 0.0;
        for (var y = 0; y < height; y++)
        {
            var weight = Math.Cos(Math.PI / 2.0 - Math.PI * (y + 0.5) / height);
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var luma = 0.2126 * rgba[i] + 0.7152 * rgba[i + 1] + 0.0722 * rgba[i + 2];
                luma = Math.Clamp(luma, Math.Pow(2.0, Lo), Math.Pow(2.0, Hi));
                var bin = (int)Math.Floor((Math.Log2(luma) - Lo) / Width);
                counts[Math.Clamp(bin, 0, Bins - 1)] += weight;
                total += weight;
            }
        }
        for (var i = 0; i < Bins; i++) counts[i] /= total;
        return counts;
    }

    /// <summary>Histogram of an RGBA16F readback (8 bytes a pixel, alpha ignored).</summary>
    public static double[] HistogramRgba16F(byte[] bytes, int pixels)
    {
        var rgb = new float[pixels * 3];
        for (var i = 0; i < pixels; i++)
        {
            for (var c = 0; c < 3; c++) rgb[i * 3 + c] = (float)BitConverter.ToHalf(bytes, i * 8 + c * 2) * SceneStore.Scale;
        }
        return Histogram(rgb);
    }

    /// <summary>The log-average level of everything below the brightest <paramref name="trimHi"/> fraction of the pixels.</summary>
    public static double TrimmedEv(double[] hist, double trimHi)
    {
        double cumulative = 0.0, weighted = 0.0, kept = 0.0;
        for (var i = 0; i < Bins; i++)
        {
            var h = hist[i];
            cumulative += h;
            var keep = Math.Clamp((1.0 - trimHi) - (cumulative - h), 0.0, h);   // a bin straddling the cut is split
            weighted += keep * (Lo + (i + 0.5) * Width);
            kept += keep;
        }
        return weighted / Math.Max(kept, 1e-12);
    }
}
