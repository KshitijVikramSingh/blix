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
        RecallSky();
    }

    // The instant layer's sky visibility (the detail's, what the lit pass reads) against the cooked gather's.
    private void RecallSky()
    {
        if (recallFiles is null || texelLiveIds is not { } live) return;
        var skyPath = recallFiles.Split(',')[0];
        if (ReadDetailFile(DetailPathFor(skyPath), texelCount) is not { } reference) { Console.WriteLine("[VulkanSponza] recall, sky: no reference detail."); return; }
        if (PartDetailsDropped) { Console.WriteLine("[VulkanSponza] recall, sky: no detail words under the record (--no-texel-h4 for them)."); return; }
        device.WaitIdle();
        var mine = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(splitSkyDetailBuffer, 0, texelCount * 4).AsSpan()).ToArray();
        var coarseSet = new HashSet<uint>(transferGatherIds);
        foreach (var coarse in new[] { true, false })
        {
            double sumAbs = 0, sumMine = 0, sumRef = 0; long n = 0, off = 0;
            foreach (var id in live)
            {
                if (coarseSet.Contains(id) != coarse) continue;
                var a = ((mine[id] >> 16) & 255) / 255f; var b = ((reference[id] >> 16) & 255) / 255f;
                sumAbs += MathF.Abs(a - b); sumMine += a; sumRef += b; n++;
                if (MathF.Abs(a - b) > 0.2f) off++;
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] recall, the instant layer's sky visibility, {(coarse ? "coarse" : "fine")}: {n:N0} texels, mean {sumMine / Math.Max(1, n):0.000} against the reference's {sumRef / Math.Max(1, n):0.000}, mean |difference| {sumAbs / Math.Max(1, n):0.000}, over 0.2 off {100.0 * off / Math.Max(1, n):0.0}%."));
        }
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
        var texels = ReadTexelRecords(0, texelCount);
        foreach (var (e, id) in misses.OrderByDescending(m => m.E).Take(12))
        {
            var p = texels[2 * id]; var nw = texels[2 * id + 1];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    miss: at {p.X:0.00},{p.Y:0.00},{p.Z:0.00} normal {nw.X:0.00},{nw.Y:0.00},{nw.Z:0.00} level {(int)nw.W}: copy {copy[id]:0.0000} instant {instant[id]:0.0000} reference {reference[id]:0.0000} ({e * 100:0}% off)"));
        }
        recallPicked = picked;
        RecallRules(fineIds, copy, instant, words, flaggedCount);
    }

    // Selection rules scored offline at equal budgets: each ranks the fine texels; a budget takes the top. Recall: the
    // share of the copy's bad texels (over RecallBad) taken, and of their error; precision: the share of the taken that are.
    private void RecallRules(uint[] fineIds, float[] copy, float[] instant, uint[] flagWords, int flaggedCount)
    {
        var reference = recallReference!;
        var spread = new float[texelCount];
        for (var i = 0; i < flaggedCount; i++) spread[flagWords[2 * i]] = BitConverter.UInt32BitsToSingle(flagWords[2 * i + 1]);
        float Disagree(uint id) => MathF.Max(copy[id], recallFloor) / MathF.Max(instant[id], recallFloor) is var r && r < 1f ? 1f / r : r;
        // How far the copy is off, as each rule guesses it.
        var rules = new (string Name, Func<uint, float> Score)[]
        {
            ("the flags by spread (now)", id => spread[id]),
            ("copy against instant", id => Disagree(id)),
            ("the flags by spread x light", id => spread[id] > 0f ? spread[id] * MathF.Max(copy[id], recallFloor) : 0f),
            ("copy against instant x light", id => Disagree(id) * MathF.Max(copy[id], recallFloor)),
            ("either: max of spread and copy against instant", id => MathF.Max(spread[id], Disagree(id))),
            ("either, x light", id => MathF.Max(spread[id], Disagree(id)) * MathF.Max(copy[id], recallFloor)),
            ("the error itself (the best any rule can do)", id => MathF.Abs(copy[id] - reference[id])),
        };
        double totalBad = 0, totalError = 0;
        foreach (var id in fineIds) { var e = RecallError(copy[id], reference[id]); if (e > RecallBad) { totalBad++; totalError += MathF.Abs(copy[id] - reference[id]); } }
        // Corrections of the copy itself, free at a light change: capped at k x the instant layer (the hidden faces: the copy
        // 40x over, the instant right), against the reference -- and what each costs where the copy was right.
        foreach (var k in new[] { 2f, 4f, 8f, 16f, 32f })
        {
            double sumErr = 0, sumAbs = 0, refSum = 0, liveSum = 0; long badN = 0, worsened = 0;
            foreach (var id in fineIds)
            {
                var c = MathF.Min(copy[id], k * MathF.Max(instant[id], recallFloor));
                var e = RecallError(c, reference[id]);
                sumErr += e; sumAbs += MathF.Abs(c - reference[id]); if (e > RecallBad) badN++;
                if (RecallError(copy[id], reference[id]) <= RecallBad && e > RecallBad) worsened++;
                refSum += reference[id]; liveSum += c;
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] recall, the copy capped at {k}x the instant layer: energy {liveSum / Math.Max(1e-9, refSum):0.000}, mean error {sumErr / fineIds.Length:0.000}, over 50% off {100.0 * badN / fineIds.Length:0.0}% (made bad {worsened:N0}), the error's sum {100 * sumAbs / Math.Max(1e-9, totalError):0.0}% of the uncapped bad ones'."));
        }
        foreach (var (name, score) in rules)
        {
            var scored = new float[fineIds.Length];
            Parallel.For(0, fineIds.Length, i => scored[i] = score(fineIds[i]));
            var order = Enumerable.Range(0, fineIds.Length).ToArray();
            Array.Sort(scored.ToArray(), order);
            Array.Reverse(order);
            var line = new List<string>();
            foreach (var budget in new[] { 1_000_000, 2_000_000, 4_000_000 })
            {
                double bad = 0, error = 0; long taken = 0;
                for (var k = 0; k < Math.Min(budget, order.Length); k++)
                {
                    var id = fineIds[order[k]];
                    if (scored[order[k]] <= 0f) break;
                    taken++;
                    if (RecallError(copy[id], reference[id]) > RecallBad) { bad++; error += MathF.Abs(copy[id] - reference[id]); }
                }
                line.Add(string.Create(CultureInfo.InvariantCulture, $"{budget / 1_000_000} M: recall {100 * bad / Math.Max(1, totalBad):0.0}% ({100 * error / Math.Max(1e-9, totalError):0.0}% of the error), precision {100 * bad / Math.Max(1, taken):0.0}%"));
            }
            Console.WriteLine($"[VulkanSponza] recall, rule {name}: " + string.Join("; ", line));
        }
    }

    private uint[] recallPicked = Array.Empty<uint>();

    // BLIX_DUMP_PARTS=<prefix> (a diagnosis): the parts at the light change's stages (after the coarse gather, after the
    // copy, at the end), each texel's sky and sun words, to <prefix>.<stage>.
    private static readonly string? dumpParts = Environment.GetEnvironmentVariable("BLIX_DUMP_PARTS");
    // The frame after a landing at which the parts, the resolved light and the provenance are dumped as "rest".
    private long restDumpFrame = -1;
    private void DumpAtRest()
    {
        if (restDumpFrame < 0 || framesRendered < restDumpFrame) return;
        restDumpFrame = -1;
        DumpParts("rest");
        // The texels' records (two vec4 each: position, normal with the atlas coordinate in w), for where each one is.
        if (dumpParts is not null) File.WriteAllBytes($"{dumpParts}.texels", MemoryMarshal.AsBytes(ReadTexelRecords(0, texelCount).AsSpan()).ToArray());
    }

    // At the shot: what the pixels read -- the resolved light, the details -- to <prefix>.shot.
    private void DumpShotState()
    {
        if (dumpParts is null) return;
        device.WaitIdle();
        using var w = new BinaryWriter(File.Create($"{dumpParts}.shot"));
        w.Write(texelCount);
        foreach (var b in new[] { texelResolvedBuffer, splitSkyDetailBuffer, splitSunDetailBuffer })
        {
            var bytes = b.Equals(default(GpuBufferHandle)) || (PartDetailsDropped && !b.Equals(texelResolvedBuffer)) ? new byte[0]
                : device.ReadGpuBuffer(b, 0, b.Equals(texelResolvedBuffer) ? texelCount * 8 : texelCount * 4);
            w.Write(bytes.Length); w.Write(bytes);
        }
        Console.WriteLine($"[VulkanSponza] shot state dumped: {dumpParts}.shot");
    }

    // Each stage's dumps numbered in order (<prefix>.<stage>.<n>): a run with a sun change keeps both suns'. With
    // sunBits, the texels' sun visibility bits after the parts (words a texel, the sun's column, then the words).
    private readonly Dictionary<string, int> dumpPartsCount = new();

    private void DumpParts(string stage, bool sunBits = false)
    {
        if (dumpParts is null) return;
        device.WaitIdle();
        var sky = device.ReadGpuBuffer(EnsureLivePart(false), 0, texelCount * 8);
        var sun = device.ReadGpuBuffer(EnsureLivePart(true), 0, texelCount * 8);
        var n = dumpPartsCount.GetValueOrDefault(stage);
        dumpPartsCount[stage] = n + 1;
        var path = $"{dumpParts}.{stage}.{n}";
        using var w = new BinaryWriter(File.Create(path));
        w.Write(texelCount); w.Write(sky); w.Write(sun);
        if (sunBits && !liveSunBits.Equals(default(GpuBufferHandle)))
        {
            w.Write(liveSunWords); w.Write(liveSunColumn);
            w.Write(device.ReadGpuBuffer(liveSunBits, 0, texelCount * liveSunWords * 4));
        }
        // The resolved light the image reads (composed, filtered), and who wrote each texel's parts
        // (SponzaLoop.TexelPicker.cs), beside it.
        if (!texelResolvedBuffer.Equals(default(GpuBufferHandle)))
            File.WriteAllBytes(path + ".resolved", device.ReadGpuBuffer(texelResolvedBuffer, 0, texelCount * 8));
        if (!texelProvenanceBuffer.Equals(default(GpuBufferHandle)) && texelProvenanceBytes >= (texelCount + 3) / 4 * 4)
            File.WriteAllBytes(path + ".provenance", device.ReadGpuBuffer(texelProvenanceBuffer, 0, texelProvenanceBytes));
        Console.WriteLine($"[VulkanSponza] parts dumped: {path}");
    }

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
