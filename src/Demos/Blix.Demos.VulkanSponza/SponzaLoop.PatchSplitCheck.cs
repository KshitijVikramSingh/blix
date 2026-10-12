using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;

namespace Blix.Demos.VulkanSponza;

// --patch-split-check [--patch-split-bounces N,M,...]: the patches' light split into the sky's and the sun's (the GPU solve's components,
// SponzaLoop.SunNodesSolve.cs), checked before anything builds on it. At the boot sun, for each bounce count asked
// (default 48 and 256): the sky alone, the sun alone and the two together, each solved from nothing. Reported: how far
// sky + sun lies from the two together (light is linear: it should be rounding), each part's share, how much each part
// still moves between the bounce counts (convergence -- the cook's 48 started from the boot answer, not from nothing),
// and the two together against the boot answer the transport cook solved. Then the run closes.
internal sealed partial class SponzaLoop
{
    private int[]? patchSplitBounces;
    private int patchSplitIndex = -1;
    private bool patchSplitSolving, patchSplitRead;
    private readonly List<(int Bounces, Vector3[][] Parts)> patchSplitResults = new();

    private void ReadPatchSplitArgs(AppArgs args)
    {
        if (!args.Flag("patch-split-check")) return;
        patchSplitBounces = args.String("patch-split-bounces") is { } list
            ? list.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray()
            : new[] { 48, 256 };
    }

    // RecordTexels, each frame of a --patch-split-check run.
    private void RecordPatchSplitCheck()
    {
        if (patchSplitBounces is null || transportSolve is null || cookedPacked is null || !EnsureFrameTracer()) return;
        if (patchSplitSolving)
        {
            // The frame that records the solve's last dispatches runs them after this returns: read the table a frame on.
            if (!patchSplitRead) { patchSplitRead = StepGpuSolve(); return; }
            patchSplitRead = false;
            patchSplitSolving = false;
            device.WaitIdle();
            var patches = transportSolve.Patches;
            var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(sunNodePatchLight, 0, patches * 3 * 8).AsSpan()).ToArray();
            var parts = new Vector3[3][];
            for (var c = 0; c < 3; c++)
            {
                parts[c] = new Vector3[patches];
                for (var p = 0; p < patches; p++)
                {
                    var lo = words[(p * 3 + c) * 2];
                    var hi = words[(p * 3 + c) * 2 + 1];
                    parts[c][p] = new Vector3((float)BitConverter.UInt16BitsToHalf((ushort)lo), (float)BitConverter.UInt16BitsToHalf((ushort)(lo >> 16)),
                        (float)BitConverter.UInt16BitsToHalf((ushort)hi));
                }
            }
            patchSplitResults.Add((patchSplitBounces[patchSplitIndex], parts));
        }
        if (++patchSplitIndex < patchSplitBounces.Length)
        {
            var boot = transportRelightSun.ToSun;
            BeginGpuSolve(new[] { (boot, false, 1f), (boot, true, 0f), (boot, true, 1f) }, warm: false, bounces: patchSplitBounces[patchSplitIndex]);
            patchSplitSolving = true;
            return;
        }
        ReportPatchSplit();
        patchSplitBounces = null;
        host.RequestClose();
    }

    private void ReportPatchSplit()
    {
        static float Lum(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        static double Off(Vector3[] a, Vector3[] b)   // sum |lum a - lum b| over sum lum b
        {
            double d = 0, n = 0;
            for (var i = 0; i < a.Length; i++) { d += Math.Abs(Lum(a[i]) - Lum(b[i])); n += Math.Abs(Lum(b[i])); }
            return n > 0 ? d / n : 0;
        }
        static double Sum(Vector3[] a) => a.Sum(v => (double)Lum(v));
        var bootAnswer = transportSolve!.BootIncident();
        // Each patch's couplings: their weights' sum (the share of its hemisphere they see: at most 1) and, times its
        // albedo, what a bounce can keep of its light there (the solve converges as the largest of these allows).
        var d = transportSolve;
        var sums = d.Couplings.Select(c => c?.Sum(x => (double)x.W) ?? 0).ToArray();
        var keep = sums.Select((v, i) => v * Lum(d.Albedo[i])).ToArray();
        double Pct(double[] a, double q) { var s2 = a.Order().ToArray(); return s2[Math.Min(s2.Length - 1, (int)(q * s2.Length))]; }
        // The albedo the patches were cooked with, and the albedo the ray scene's surfaces hold now (what the hits read).
        var patchAlbedo = d.Albedo.Select(a => (double)Lum(a)).ToArray();
        var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(raySurfaces, 0, rayGpuData!.RowPlacements.Length * 4).AsSpan()).ToArray();
        static float Lin(uint b) { var c = b / 255f; return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f); }
        var surfaceAlbedo = words.Select(w => (double)Lum(new Vector3(Lin(w & 255u), Lin((w >> 8) & 255u), Lin((w >> 16) & 255u)))).ToArray();
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] albedo (luminance): the patches' p10 {Pct(patchAlbedo, 0.1):0.000} p50 {Pct(patchAlbedo, 0.5):0.000} p90 {Pct(patchAlbedo, 0.9):0.000}; the ray surfaces' now p10 {Pct(surfaceAlbedo, 0.1):0.000} p50 {Pct(surfaceAlbedo, 0.5):0.000} p90 {Pct(surfaceAlbedo, 0.9):0.000} ({words.Count(w => w == 0xFFFFFFFFu):N0} of {words.Length:N0} still the unbaked white)."));
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] patch couplings: weight sums p50 {Pct(sums, 0.5):0.000}, p99 {Pct(sums, 0.99):0.000}, max {sums.Max():0.000} ({sums.Count(v => v > 1.0001):N0} over 1); albedo x sum p50 {Pct(keep, 0.5):0.000}, p99 {Pct(keep, 0.99):0.000}, max {keep.Max():0.000}."));
        var deepest = patchSplitResults[^1].Parts;
        Console.WriteLine(string.Create(Inv, $"[VulkanSponza] patch split check, {bootAnswer.Length:N0} patches at the boot sun, each part from nothing:"));
        foreach (var (bounces, parts) in patchSplitResults)
        {
            var (sky, sun, both) = (parts[0], parts[1], parts[2]);
            var summed = sky.Select((v, i) => v + sun[i]).ToArray();
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza]   {bounces,4} bounces: sky + sun vs together {Off(summed, both) * 100:0.000}% off; sky {Sum(sky) / Sum(both) * 100:0.0}% of the light, sun {Sum(sun) / Sum(both) * 100:0.0}%; " +
                $"vs {patchSplitResults[^1].Bounces} bounces: sky {Off(sky, deepest[0]) * 100:0.00}%, sun {Off(sun, deepest[1]) * 100:0.00}%, together {Off(both, deepest[2]) * 100:0.00}% (energy {Sum(both) / Sum(deepest[2]) * 100:0.00}%); " +
                $"together vs the boot answer {Off(both, bootAnswer) * 100:0.00}% (energy {Sum(both) / Sum(bootAnswer) * 100:0.00}%)."));
        }
    }
}
