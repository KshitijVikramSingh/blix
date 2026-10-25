namespace Blix.Demos.TonemapLab;

// Manual white balance for the lab's slider: the gains that make a neutral surface under a Planckian light of a given
// colour temperature come out grey. A first, simple version (diagonal gains in linear sRGB, a green/magenta tint); the
// auto white balance GT7 describes (slide 222) adapts colour temperature and tint over time and is a later step.
internal static class WhiteBalance
{
    // The Planckian locus in CIE xy, the cubic-spline fit of Kim et al. (2002), valid 1667-25000 K. Checked against the
    // standard points in `SelfTest` (A = 2856 K at (0.4476, 0.4074); 6500 K at (0.3135, 0.3237)).
    public static (double X, double Y) PlanckianXy(double kelvin)
    {
        var t = Math.Clamp(kelvin, 1667.0, 25000.0);
        double x = t <= 4000.0
            ? -0.2661239e9 / (t * t * t) - 0.2343589e6 / (t * t) + 0.8776956e3 / t + 0.179910
            : -3.0258469e9 / (t * t * t) + 2.1070379e6 / (t * t) + 0.2226347e3 / t + 0.240390;
        double y;
        if (t <= 2222.0) y = -1.1063814 * x * x * x - 1.34811020 * x * x + 2.18555832 * x - 0.20219683;
        else if (t <= 4000.0) y = -0.9549476 * x * x * x - 1.37418593 * x * x + 2.09137015 * x - 0.16748867;
        else y = 3.0817580 * x * x * x - 5.87338670 * x * x + 3.75112997 * x - 0.37001483;
        return (x, y);
    }

    // The light's colour in linear sRGB (Y = 1), normalised later against 6504 K so that 6504 K is exactly neutral.
    internal static (double R, double G, double B) LightRgb(double kelvin)
    {
        var (x, y) = PlanckianXy(kelvin);
        var xx = x / y;
        var zz = (1.0 - x - y) / y;
        return (3.2404542 * xx - 1.5371385 - 0.4985314 * zz,
                -0.9692660 * xx + 1.8760108 + 0.0415560 * zz,
                0.0556434 * xx - 0.2040259 + 1.0572252 * zz);
    }

    /// <summary>Linear RGB gains (green 1) that make a neutral under a <paramref name="kelvin"/> light grey, 6504 K being identity.</summary>
    /// <param name="tint">Green(-) to magenta(+): the green gain is 1 / (1 + tint).</param>
    public static (float R, float G, float B) Gains(double kelvin, double tint)
    {
        var l = LightRgb(kelvin);
        var d = LightRgb(6504.0);
        var (r, g, b) = (l.R / d.R, l.G / d.G, l.B / d.B);
        return ((float)(g / r), (float)(1.0 / (1.0 + tint)), (float)(g / b));
    }

    public static string SelfTest()
    {
        var a = PlanckianXy(2856.0);
        var d = PlanckianXy(6500.0);
        var neutral = Gains(6504.0, 0.0);
        var warm = Gains(3200.0, 0.0);
        return $"A 2856 K xy ({a.X:F4}, {a.Y:F4}) expect (0.4476, 0.4074); 6500 K xy ({d.X:F4}, {d.Y:F4}) expect (0.3135, 0.3237); " +
               $"gains at 6504 K ({neutral.R:F3}, {neutral.G:F3}, {neutral.B:F3}) expect 1; at 3200 K ({warm.R:F2}, {warm.G:F2}, {warm.B:F2}) expect red < 1 < blue";
    }
}
