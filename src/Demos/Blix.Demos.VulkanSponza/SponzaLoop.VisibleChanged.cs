using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// A light change's first gather (the light-change design's tier 1, 2026-10-20): the texels on screen whose own sun bit
// flipped, gathered at the fine round's rays before the coarse sweep. The ratio relight (tier 0) is right to within a
// few percent where a texel's sun stayed as it was; where it flipped -- a shadow's edge passing -- the ratio carries the
// old sun's, and those are what the eye sees go wrong. ~1-2% of the texels for a 5-degree turn, ~28% for 20.
// The sun bits before the turn are kept (a bit a texel, read back as the re-aim starts); the view's, from the visible
// pass the re-aim asks for (SplitVisiblePending). The fine copy after the sweep leaves these texels as gathered.
// --no-visible-changed: the sweep first, as before.
internal sealed partial class SponzaLoop
{
    private bool visibleChanged = true;
    // The last sun's bit a texel, and the texels the image read as the sun turned (a bit a record each).
    private uint[]? sunBitsBefore, visibleAtTurn;
    // The texels the first gather took (a bit a record): the fine copy leaves them.
    private uint[]? visibleChangedMask;
    private bool visibleChangedGathering;
    private uint[] visibleChangedSavedIds = Array.Empty<uint>();
    private GpuBufferHandle visibleChangedSavedBuffer;
    private int visibleChangedSavedRays, visibleChangedSavedMaxRays;
    private readonly Stopwatch visibleChangedClock = new();

    // Tier 2: once the first gather is in, the rest of a light change's gather runs behind the frame -- the cook screen
    // down, the camera free, its passes held to a budget a frame (at least one pass: a 1M-ray pass is ~10 ms on Sponza).
    // --background-gather (off by default): the host's passes wait out the frames in flight -- a frame's first pass took
    // ~75 ms of waiting for ~10 of work, Sponza ran at ~10 fps and the change landed in 285 s against 50 under the cook
    // screen (2026-10-21). Worth it once the passes are recorded in the frame. --background-gather-ms N: the budget.
    private bool backgroundGather, gatherBehind;
    private double backgroundGatherMs = 8;
    // --sweep-in-frame: the visible and changed too (its gather's passes recorded in the frame, SponzaLoop.SweepFrame.cs):
    // on the host it was ~5 frames of 85-135 ms after a sun change on Crytek Sponza (2026-10-25).
    private bool GatherInBackground => (gatherBehind && backgroundGather || sweepInFrame && visibleChangedGathering) && SplitCookRunning;

    private void StartBackgroundGather()
    {
        if (!backgroundGather || !liveSkyGathered || gatherBehind) return;
        gatherBehind = true;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] light change: the rest of the gather behind the frame, {backgroundGatherMs:0} ms a frame (at least a pass)."));
    }

    private bool VisibleChangedOn => visibleChanged && liveTexels && livePatchTexels && livePatchRefine && TransferOn;

    // The column's bit a texel, from the sun bits as the GPU holds them.
    // --sweep-in-frame: read where it lies, no WaitIdle and no copy -- the bits were shot frames before (at the re-aim, and
    // the gather waits for them once a sun: liveBitsSyncedFor). Drained and copied, it held a frame ~90 ms on Crytek Sponza.
    private uint[] SunColumnBits()
    {
        using var profScope = Prof("sun-bits-read");
        var words = new uint[texelCount * liveSunWords];
        if (sweepInFrame) device.ReadMappedGpuBuffer(liveSunBits, 0, MemoryMarshal.AsBytes(words.AsSpan()));
        else
        {
            device.WaitIdle();
            words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(liveSunBits, 0, texelCount * liveSunWords * 4).AsSpan()).ToArray();
        }
        var bits = new uint[(texelCount + 31) / 32];
        int stride = liveSunWords, word = liveSunColumn / 32, bit = liveSunColumn % 32;
        Parallel.For(0, bits.Length, w =>
        {
            uint packed = 0;
            var end = Math.Min(32, texelCount - w * 32);
            for (var i = 0; i < end; i++)
                packed |= ((words[(w * 32 + i) * stride + word] >> bit) & 1u) << i;
            bits[w] = packed;
        });
        return bits;
    }

    // BeginLiveSunBits, before the bits are shot for a new sun: the old sun's kept.
    private void KeepSunBitsBefore()
    {
        sunBitsBefore = null;
        if (!VisibleChangedOn || !liveSunDone || liveSunColumn < 0 || liveSunBits.Equals(default(GpuBufferHandle)) || !liveSkyGathered) return;
        sunBitsBefore = SunColumnBits();
    }

    // RunSunNodePassesNow, the setup done and the coarse list up: the first gather's texels in its place, when any.
    private void BeginVisibleChanged()
    {
        visibleChangedMask = null;
        if (!VisibleChangedOn || visibleChangedGathering || fineGathering || sunBitsBefore is not { } before || visibleAtTurn is not { } seen)
        {
            StartBackgroundGather();
            return;
        }
        sunBitsBefore = null;
        visibleAtTurn = null;
        var clock = Stopwatch.StartNew();
        var after = SunColumnBits();
        var mask = new uint[after.Length];
        long flipped = 0, visible = 0;
        var ids = new List<uint>();
        for (var w = 0; w < mask.Length; w++)
        {
            var changed = before[w] ^ after[w];
            flipped += BitOperations.PopCount(changed);
            visible += BitOperations.PopCount(seen[w]);
            var m = changed & seen[w];
            mask[w] = m;
            while (m != 0)
            {
                var i = BitOperations.TrailingZeroCount(m);
                ids.Add((uint)(w * 32 + i));
                m &= m - 1;
            }
        }
        if (Environment.GetEnvironmentVariable("BLIX_VC_LEVELS") == "1" && liveCoarseLevels is { } lv)
        {
            var byLevel = new long[16, 3];
            for (var id = 0; id < texelCount; id++)
            {
                var l = Math.Min(15, (int)lv[id]);
                if ((seen[id >> 5] >> (id & 31) & 1u) != 0) byLevel[l, 0]++;
                if (((before[id >> 5] ^ after[id >> 5]) >> (id & 31) & 1u) != 0) byLevel[l, 1]++;
                if ((before[id >> 5] >> (id & 31) & 1u) != 0) byLevel[l, 2]++;
            }
            for (var l = 0; l < 16; l++)
                if (byLevel[l, 0] + byLevel[l, 1] > 0)
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] visible and changed, level {l}: visible {byLevel[l, 0]:N0}, flipped {byLevel[l, 1]:N0}, lit before {byLevel[l, 2]:N0}"));
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] visible and changed: {ids.Count:N0} texels on screen turned in or out of the sun (of {visible:N0} on screen, {flipped:N0} turned in all; picked in {clock.Elapsed.TotalMilliseconds:0} ms)."));
        if (ids.Count == 0) { StartBackgroundGather(); return; }
        var picked = ids.ToArray();
        // In place order (the far field's key), as the fine round's: the slices' lookups stay in the caches.
        if (farPlaceKey is { } placeKey && placeKey.Length == texelCount)
        {
            var keys = new uint[picked.Length];
            for (var i = 0; i < picked.Length; i++) keys[i] = placeKey[picked[i]];
            Array.Sort(keys, picked);
        }
        visibleChangedMask = mask;
        visibleChangedSavedIds = sunNodeTraceIds;
        visibleChangedSavedBuffer = sunNodeTraceBuffer;
        sunNodeTraceIds = picked;
        sunNodeTraceBuffer = Own(device.CreateGpuBuffer(picked.Length * 4, MemoryMarshal.AsBytes(picked.AsSpan()), "sponza.sun-nodes.visible-changed"));
        visibleChangedSavedRays = sunNodeRays;
        visibleChangedSavedMaxRays = texelMaxRays;
        sunNodeRays = fineGatherRays;
        texelMaxRays = Math.Max(fineGatherRays, fineGatherMaxRays);
        RetireGpuBuffer(sunNodeStats);
        sunNodeStats = NewSunNodeStats();
        sunNodeFirst = 0;
        sunNodePass = 0;
        ResetSunNodeCounters();
        visibleChangedGathering = true;
        visibleChangedClock.Restart();
    }

    // FinishSunNodes, the first gather done: the coarse sweep's list back, and on to it.
    private bool EndVisibleChanged()
    {
        if (!visibleChangedGathering) return false;
        visibleChangedGathering = false;
        RetireTraceBuffer();
        sunNodeTraceIds = visibleChangedSavedIds;
        sunNodeTraceBuffer = visibleChangedSavedBuffer;
        visibleChangedSavedIds = Array.Empty<uint>();
        visibleChangedSavedBuffer = default;
        sunNodeRays = visibleChangedSavedRays;
        texelMaxRays = visibleChangedSavedMaxRays;
        RetireGpuBuffer(sunNodeStats);
        sunNodeStats = NewSunNodeStats();
        sunNodeFirst = 0;
        sunNodePass = 0;
        sunNodeReadback = false;
        ResetSunNodeCounters();
        sunNodeClock.Restart();
        sunNodeStage = SunNodeStage.Tracing;
        if (splitCooking) FlushSplitLanding();
        // BLIX_DUMP_PARTS: the parts as tier 1 left them, and its texels (a bit a record).
        if (dumpParts is not null && visibleChangedMask is { } mask)
        {
            DumpParts("tier1");
            File.WriteAllBytes($"{dumpParts}.tier1mask", MemoryMarshal.AsBytes(mask.AsSpan()).ToArray());
        }
        StartBackgroundGather();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] visible and changed: landed in {visibleChangedClock.Elapsed.TotalSeconds:0.0} s; the coarse sweep next."));
        return true;
    }

    // ApplyFineNow: the fine texels the copy writes -- all of them but those the first gather took.
    private (GpuBufferHandle Ids, int Count, bool Own) FineCopyIds()
    {
        if (visibleChangedMask is not { } mask || texelLiveIds is not { } all || liveCoarseLevels is not { } levels)
            return (transferFineIdsBuffer, transferFineCount, false);
        var fine = all.Where(id => levels[id] < transferMinLevel && (mask[id >> 5] >> (int)(id & 31) & 1u) == 0).ToArray();
        return (device.CreateGpuBuffer(Math.Max(16, fine.Length * 4), MemoryMarshal.AsBytes(fine.AsSpan()), "sponza.transfer.fine-ids.copy"), fine.Length, true);
    }
}
