using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// BLIX_RECALL=<sky part file>,<sun part file> (a measurement): the live refine's light against the cooked mode's full
// gather of the same sun -- each texel gathered itself, 16.8 min on Sponza (2026-10-19) -- read from the cache. Asks
// whether the fine refine's flags find the texels the copy gets wrong (recall), how many of the flagged are (precision),
// and what the misses are; after the fine gather, whether it fixed the ones it took. The parts compared as the eye sees
// them: the sky's plus the sun's at this sun's irradiance, a texel's error over the larger of its reference light and a
// tenth of the median (dark texels and the reference's own noise are not errors).
internal sealed partial class SponzaLoop
{
    private static readonly string? recallFiles = Environment.GetEnvironmentVariable("BLIX_RECALL");
    private float[]? recallReference, recallInstant;
    private float recallFloor;
    private const float RecallBad = 0.5f;

    private float[]? RecallLight(uint[] sky, uint[] sun)
    {
        var irr = Luminance(EffectiveSunIrradiance);
        var light = new float[texelCount];
        Parallel.For(0, texelCount, i => light[i] = Luminance(UnpackRgb9e5(sky[2 * i])) + Luminance(UnpackRgb9e5(sun[2 * i])) * irr);
        return light;
    }

    private float[] RecallLive()
    {
        device.WaitIdle();
        var sky = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(EnsureLivePart(false), 0, texelCount * 8).AsSpan()).ToArray();
        var sun = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(EnsureLivePart(true), 0, texelCount * 8).AsSpan()).ToArray();
        return RecallLight(sky, sun)!;
    }

    // At the light change, after the instant layer: the reference read (once), the instant layer kept.
    private void RecallInstant()
    {
        if (recallFiles is null) return;
        if (recallReference is null)
        {
            var paths = recallFiles.Split(',');
            if (paths.Length != 2 || ReadResolved(paths[0], texelCount) is not { } sky || ReadResolved(paths[1], texelCount) is not { } sun)
            {
                Console.WriteLine($"[VulkanSponza] recall: the reference ({recallFiles}) is not a texel-resolved pair for {texelCount:N0} texels; off.");
                return;
            }
            recallReference = RecallLight(sky, sun);
            var sorted = recallReference!.Where(v => v > 0f).OrderBy(v => v).ToArray();
            recallFloor = 0.1f * (sorted.Length > 0 ? sorted[sorted.Length / 2] : 0f);
        }
        recallInstant = RecallLive();
    }

    private float RecallError(float live, float reference) => MathF.Abs(live - reference) / MathF.Max(reference, recallFloor);

    // A set's error against the reference: how many, the mean relative error, the share over RecallBad.
    private string RecallSummary(float[] light, IEnumerable<uint> ids)
    {
        var reference = recallReference!;
        long n = 0, bad = 0; double sum = 0, liveSum = 0, refSum = 0;
        foreach (var id in ids)
        {
            var e = RecallError(light[id], reference[id]);
            n++; sum += e; if (e > RecallBad) bad++;
            liveSum += light[id]; refSum += reference[id];
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"{n:N0} texels, energy {liveSum / Math.Max(1e-9, refSum):0.000}, mean error {sum / Math.Max(1, n):0.000}, over {RecallBad * 100:0}% off {bad:N0} ({100.0 * bad / Math.Max(1, n):0.0}%)");
    }

    // After the copy, before the fine gather: the flags against the texels the copy got wrong.
    private void RecallCopy(uint[] picked)
    {
        if (recallReference is not { } reference || recallInstant is not { } instant || texelLiveIds is not { } live) return;
        var copy = RecallLive();
        var coarseSet = new HashSet<uint>(transferGatherIds);
        var fineIds = live.Where(id => !coarseSet.Contains(id)).ToArray();
        Console.WriteLine("[VulkanSponza] recall, the instant layer, coarse: " + RecallSummary(instant, transferGatherIds));
        Console.WriteLine("[VulkanSponza] recall, the instant layer, fine: " + RecallSummary(instant, fineIds));
        Console.WriteLine("[VulkanSponza] recall, the coarse gather: " + RecallSummary(copy, transferGatherIds));
        Console.WriteLine("[VulkanSponza] recall, the fine copy: " + RecallSummary(copy, fineIds));
        // The flags: every flagged id (the fine pass's list), and the budget's pick.
        var flaggedCount = (int)Math.Min(fineFlagCounts[0], (uint)transferFineCount);
        var words = flaggedCount == 0 ? Array.Empty<uint>() : MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(fineFlagsBuffer, 64, flaggedCount * 8).AsSpan()).ToArray();
        var flagged = new bool[texelCount];
        for (var i = 0; i < flaggedCount; i++) flagged[words[2 * i]] = true;
        var pickedSet = new bool[texelCount];
        foreach (var id in picked) pickedSet[id] = true;
        long bad = 0, badFlagged = 0, badPicked = 0, flaggedN = 0, flaggedBad = 0;
        double badError = 0, badErrorFlagged = 0, badErrorPicked = 0;
        var missesByLight = new long[4];   // the misses by their reference light: under the floor, -1x, -4x, more x the floor
        var missLevels = new long[8];
        var misses = new List<(float E, uint Id)>();
        var levels = liveCoarseLevels;
        foreach (var id in fineIds)
        {
            var e = RecallError(copy[id], reference[id]);
            if (flagged[id]) { flaggedN++; if (e > RecallBad) flaggedBad++; }
            if (e <= RecallBad) continue;
            var excess = MathF.Abs(copy[id] - reference[id]);
            bad++; badError += excess;
            if (flagged[id]) { badFlagged++; badErrorFlagged += excess; }
            if (pickedSet[id]) { badPicked++; badErrorPicked += excess; }
            if (!flagged[id])
            {
                var r = reference[id] / MathF.Max(recallFloor, 1e-9f);
                missesByLight[r < 1f ? 0 : r < 4f ? 1 : r < 16f ? 2 : 3]++;
                if (levels is not null) missLevels[Math.Min(7, (int)levels[id])]++;
                if ((id & 63) == 0) misses.Add((e, id));
            }
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] recall: of the {bad:N0} fine texels the copy gets over {RecallBad * 100:0}% wrong, the flags find {100.0 * badFlagged / Math.Max(1, bad):0.0}% ({100.0 * badErrorFlagged / Math.Max(1e-9, badError):0.0}% of their error), the budget's {picked.Length:N0} take {100.0 * badPicked / Math.Max(1, bad):0.0}% ({100.0 * badErrorPicked / Math.Max(1e-9, badError):0.0}% of their error); of the {flaggedN:N0} flagged, {100.0 * flaggedBad / Math.Max(1, flaggedN):0.0}% are that wrong."));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] recall, the misses by their reference light (x the floor {recallFloor:G3}): under 1 {missesByLight[0]:N0}, 1-4 {missesByLight[1]:N0}, 4-16 {missesByLight[2]:N0}, more {missesByLight[3]:N0}; by level {string.Join(" / ", missLevels.Take(3).Select(c => c.ToString("N0", CultureInfo.InvariantCulture)))}."));
        // A sample of the worst misses: where, and their light against the reference's.
        var texels = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelBuffer, 0, texelCount * 32).AsSpan()).ToArray();
        foreach (var (e, id) in misses.OrderByDescending(m => m.E).Take(12))
        {
            var p = texels[2 * id]; var nw = texels[2 * id + 1];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    miss: at {p.X:0.00},{p.Y:0.00},{p.Z:0.00} normal {nw.X:0.00},{nw.Y:0.00},{nw.Z:0.00} level {(int)nw.W}: copy {copy[id]:0.0000} instant {instant[id]:0.0000} reference {reference[id]:0.0000} ({e * 100:0}% off)"));
        }
        recallPicked = picked;
    }

    private uint[] recallPicked = Array.Empty<uint>();

    // After the fine gather: the texels it took, and every fine texel, against the reference.
    private void RecallFinal()
    {
        if (recallReference is null || texelLiveIds is not { } live) return;
        var light = RecallLive();
        var coarseSet = new HashSet<uint>(transferGatherIds);
        Console.WriteLine("[VulkanSponza] recall, after the fine gather, the gathered: " + RecallSummary(light, recallPicked));
        Console.WriteLine("[VulkanSponza] recall, after the fine gather, every fine texel: " + RecallSummary(light, live.Where(id => !coarseSet.Contains(id))));
    }
}
