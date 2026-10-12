using System.Diagnostics;
using System.Globalization;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --settle-log (--settle-every N): how long a live run takes to a stable image, and what its frames cost on the way. Every N frames
// (60 by default) what is presented (TAA's resolve; the lit image with TAA off) is read back at every 4th pixel and held
// against the previous read: the share of pixels whose luminance moved more than 5% (of max(theirs, 0.02)), and the
// window's frame periods (median, p95; the read's own stall left out), stamped with the seconds since the process
// started. STABLE once five reads in a row, all after loading, move under 1% of pixels -- with a still camera a
// settled image moves only by TAA's jitter and foliage coins, which this threshold rides above.
// --settle-dir D: a read that moves 0.8 points more than the median of the last ten (a tear, a pop) writes
// D/settle-<frame>.png: the image (grey) with what moved more than 25% in red, what moved 5-25% in yellow.
internal sealed partial class SponzaLoop
{
    private int settleLogEvery;
    private float[]? settlePrevious;
    private readonly List<double> settlePeriods = new();
    private bool settleStalled;
    private int settleQuiet;
    private bool settleStable;
    private long settleFrames;
    private string? settleDir;
    private readonly List<double> settleMoved = new();
    private static readonly DateTime SettleStart = Process.GetCurrentProcess().StartTime;

    private void RecordSettlePeriod(double periodMs)
    {
        if (settleLogEvery <= 0) return;
        if (settleStalled) { settleStalled = false; return; }
        settlePeriods.Add(periodMs);
    }

    private void SampleSettle()
    {
        if (settleLogEvery <= 0 || !sceneLoaded) return;
        if (++settleFrames % settleLogEvery != 0) return;
        var seconds = (DateTime.Now - SettleStart).TotalSeconds;
        var target = render.Taa > 0f ? taaHandles[taaWrite] : hdrHandle;
        var pixels = device.ReadTexture(graph.GetColorTexture(target), out var w, out var h, out var format);
        settleStalled = true;
        const int stride = 4;
        var gw = w / stride;
        var gh = h / stride;
        var lum = new float[gw * gh];
        for (var y = 0; y < gh; y++)
        for (var x = 0; x < gw; x++)
        {
            var i = y * stride * w + x * stride;
            float r, g, b;
            if (format == TextureFormat.Rgba16F)
            {
                r = (float)BitConverter.ToHalf(pixels, i * 8);
                g = (float)BitConverter.ToHalf(pixels, i * 8 + 2);
                b = (float)BitConverter.ToHalf(pixels, i * 8 + 4);
            }
            else
            {
                var p = BitConverter.ToUInt32(pixels, i * 4);
                r = UnpackFloat(p & 0x7FF, 6);
                g = UnpackFloat((p >> 11) & 0x7FF, 6);
                b = UnpackFloat((p >> 22) & 0x3FF, 5);
            }
            lum[y * gw + x] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }

        var moved = double.NaN;
        var meanStep = double.NaN;
        if (settlePrevious is { } prev && prev.Length == lum.Length)
        {
            long count = 0;
            double sum = 0;
            for (var i = 0; i < lum.Length; i++)
            {
                var step = Math.Abs(lum[i] - prev[i]) / Math.Max(Math.Max(lum[i], prev[i]), 0.02f);
                sum += step;
                if (step > 0.05f) count++;
            }
            moved = 100.0 * count / lum.Length;
            meanStep = 100.0 * sum / lum.Length;
        }
        if (settleDir is not null && fullyLoaded && !double.IsNaN(moved) && settleMoved.Count >= 5)
        {
            var recent = settleMoved.TakeLast(10).OrderBy(v => v).ToArray();
            var median = recent[recent.Length / 2];
            if (moved > median + 0.8)
            {
                var rgba = new byte[lum.Length * 4];
                for (var i = 0; i < lum.Length; i++)
                {
                    var step = Math.Abs(lum[i] - settlePrevious![i]) / Math.Max(Math.Max(lum[i], settlePrevious[i]), 0.02f);
                    var grey = (byte)Math.Clamp(MathF.Sqrt(lum[i] / (1f + lum[i])) * 200f, 0f, 200f);
                    (byte r, byte g, byte b) c = step > 0.25f ? ((byte)255, (byte)0, (byte)0) : step > 0.05f ? ((byte)255, (byte)220, (byte)0) : (grey, grey, grey);
                    rgba[i * 4] = c.r; rgba[i * 4 + 1] = c.g; rgba[i * 4 + 2] = c.b; rgba[i * 4 + 3] = 255;
                }
                var file = Path.Combine(settleDir, $"settle-{settleFrames}.png");
                Blix.Graphics.Images.PngWriter.WriteRgba8(file, rgba, gw, gh);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] settle: SPIKE at frame {settleFrames}: {moved:0.00}% moved against a median {median:0.00}%; {file}"));
            }
        }
        if (!double.IsNaN(moved) && fullyLoaded) settleMoved.Add(moved);
        settlePrevious = lum;

        settlePeriods.Sort();
        double At(double q) => settlePeriods.Count == 0 ? double.NaN : settlePeriods[Math.Min(settlePeriods.Count - 1, (int)(q * settlePeriods.Count))];
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] settle t={seconds,7:0.0}s frame {settleFrames,6} {(fullyLoaded ? "loaded " : "loading")}: frame median {At(0.5),6:0.0} ms p95 {At(0.95),6:0.0} ms; " +
            $"image moved {moved,6:0.00}% of pixels (>5%), mean step {meanStep,5:0.00}%"));
        settlePeriods.Clear();

        if (!fullyLoaded || settleStable || double.IsNaN(moved)) return;
        settleQuiet = moved < 1.0 ? settleQuiet + 1 : 0;
        if (settleQuiet < 5) return;
        settleStable = true;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] settle: STABLE at t={seconds:0.0}s (frame {settleFrames}, {postLoadFrames} after loading): five reads in a row under 1% of pixels moved; it first went quiet {4 * settleLogEvery} frames earlier."));
    }
}
