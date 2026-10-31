using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The profile window's RTGI line, read without waiting for the device: a 512th of the slots copied on the GPU into a
// buffer the CPU reads where it lies (rtgi_statsnap.comp), read a few frames on; the training's and the readout's
// counters (rtgiStats, rtgiDoneCount, already mapped) read where they lie and taken as differences since the last window,
// not reset. The reads before waited for the device seven times and wrote three times a window: a 100-200 ms frame and
// several 50-90 ms ones after it every five seconds, the hitches down to a few fps (Crytek Sponza, 2026-11-01). The
// line describes the window before last's sample.
internal sealed partial class SponzaLoop
{
    private ShaderInterface rtgiStatSnapInterface = null!;
    private PassHandle rtgiStatSnapPassHandle;
    private PipelineHandle rtgiStatSnapPipeline;
    private GpuBufferHandle rtgiStatSnap;
    private const int RtgiStatShare = 512;
    private const int RtgiStatSnapFrames = 4;
    private bool rtgiStatSnapWanted = true;
    private long rtgiStatSnapFrame = -1;
    private readonly uint[] rtgiStatLastSeen = new uint[10], rtgiStatLastWork = new uint[64];

    private void DeclareRtgiStatsPass(Func<string, ShaderInterface> reflect)
    {
        rtgiStatSnapInterface = reflect("rtgi_statsnap.comp");
        rtgiStatSnapPassHandle = graph.ComputePass("rtgi-stats").Async(rtgiAsync).Shader(rtgiStatSnapInterface).Handle;
    }

    private int RtgiStatSlots => RtgiSlots / RtgiStatShare;

    // RecordRtgi, after the eviction: this frame's sample, when one is wanted.
    private void RecordRtgiStatSnap()
    {
        if (!rtgiStatSnapWanted) return;
        if (rtgiStatSnap.Equals(default(GpuBufferHandle))) rtgiStatSnap = Own(device.CreateReadableGpuBuffer(RtgiStatSlots * 48, "sponza.rtgi.stat-snap"));
        rtgiStatSnapWanted = false;
        rtgiStatSnapFrame = rtgiFrameCount;
        graph.Dispatch(rtgiStatSnapPassHandle, new DispatchCommand(rtgiStatSnapPipeline, (RtgiStatSlots + 63) / 64, 1, 1,
            new ShaderUniform[] { new("uSnap", new Vector4Uniform(new Vector4(RtgiStatSlots, 0f, 0f, 0f))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                new ShaderBufferBinding("RtgiMoments", rtgiMoments),
                new ShaderBufferBinding("RtgiConfidence", rtgiConfidence),
                new ShaderBufferBinding("RtgiStatSnap", rtgiStatSnap),
            }));
    }

    // At the profile window: how full the cache is and how much its cells hold (the last sample, if it has landed), then
    // the next sample asked for.
    private string RtgiCounts()
    {
        if (!RtgiReadoutReady || rtgiTraced < 2) return "";
        if (rtgiStatSnapFrame < 0 || rtgiFrameCount - rtgiStatSnapFrame < RtgiStatSnapFrames)
        {
            rtgiStatSnapWanted |= rtgiStatSnapFrame < 0;
            return "";
        }
        var snap = new Vector4[3 * RtgiStatSlots];
        device.ReadMappedGpuBuffer(rtgiStatSnap, 0, MemoryMarshal.AsBytes(snap.AsSpan()));
        rtgiStatSnapWanted = true;
        var seenNow = new uint[10];
        device.ReadMappedGpuBuffer(rtgiDoneCount, 0, MemoryMarshal.AsBytes(seenNow.AsSpan()));
        var seen = new uint[10];
        for (var i = 0; i < 10; i++) { seen[i] = unchecked(seenNow[i] - rtgiStatLastSeen[i]); rtgiStatLastSeen[i] = seenNow[i]; }
        double done = 0, cutsLight = 0, samples = 0, energy = 0;
        int used = 0, trained = 0, bad = 0;
        var firstBad = "";
        for (var s = 0; s < RtgiStatSlots; s++)
        {
            var extra = snap[3 * s + 2];
            if (BitConverter.SingleToUInt32Bits(extra.X) == 0u) continue;
            used++;
            var r0 = snap[3 * s];
            var r1 = snap[3 * s + 1];
            if (!float.IsFinite(r0.X + r0.Y + r0.Z + r0.W + r1.X + r1.Y + r1.Z + r1.W))
            {
                if (bad++ == 0) firstBad = string.Create(CultureInfo.InvariantCulture, $" first not finite: slot {s} {r0} {r1}");
                continue;
            }
            if (r1.Y < 0.5f) continue;
            trained++;
            samples += r1.Y;
            cutsLight += extra.Y;
            if (extra.Z > 0.5f) done++;
            energy += 0.2126 * r0.X + 0.7152 * r0.Y + 0.0722 * r0.Z;
        }
        used *= RtgiStatShare; trained *= RtgiStatShare; bad *= RtgiStatShare;
        samples *= RtgiStatShare; energy *= RtgiStatShare; cutsLight *= RtgiStatShare; done *= RtgiStatShare;
        var work = RtgiWork();
        return string.Create(CultureInfo.InvariantCulture,
            $"rtgi: ~{used:N0} of {RtgiSlots:N0} slots held ({100.0 * used / RtgiSlots:0.0}%), {trained:N0} trained, {samples / Math.Max(1, trained):0.0} samples a cell, {100.0 * done / Math.Max(1, trained):0.0}% of cells done, {100.0 * seen[1] / Math.Max(1u, seen[0]):0.0}% of the visible (under 1% error {100.0 * seen[6] / Math.Max(1u, seen[0]):0.0}%, under 3% {100.0 * seen[7] / Math.Max(1u, seen[0]):0.0}%), history cut {cutsLight / Math.Max(1, trained):0.00} times a cell, mean indirect {energy / Math.Max(1, trained):0.000}; {bad:N0} not finite{firstBad}; {rtgiTraced:N0} frames traced{work}; readout lookups {100.0 * seen[3] / Math.Max(1u, seen[2]):0.0}% missed ({seen[2] / (double)Math.Max(1u, seen[0]):0.0} a pixel), {100.0 * seen[5] / Math.Max(1u, seen[4]):0.0}% of pixels no blur, {100.0 * seen[9] / Math.Max(1u, seen[0]):0.00}% of pixels sized by the camera (not cooked)");
    }

    // The cache's work a frame since the last window: points live, cell updates by level offset (0 the sample's own, then
    // 1-4 up), cells newly listed by offset, the resolve's listed / rota-skipped / young / settled / settled that drifted,
    // the blend's listed / skipped / blended. The counters run on (uint, wrapping); the window's are the differences.
    private string RtgiWork()
    {
        var now = new uint[64];
        device.ReadMappedGpuBuffer(rtgiStats, 0, MemoryMarshal.AsBytes(now.AsSpan()));
        var w = new uint[64];
        for (var i = 0; i < 64; i++) { w[i] = unchecked(now[i] - rtgiStatLastWork[i]); rtgiStatLastWork[i] = now[i]; }
        var frames = Math.Max(1L, rtgiTraced - rtgiStatsTraced);
        rtgiStatsTraced = rtgiTraced;
        string F(int i) => (w[i] / (double)frames).ToString("N0", CultureInfo.InvariantCulture);
        return RtgiBudgetWork() + $"; work a frame: points {F(1)}, updates by offset {F(2)}/{F(3)}/{F(4)}/{F(5)}/{F(6)}, new listings {F(7)}/{F(8)}/{F(9)}/{F(10)}/{F(11)}; "
            + $"resolve listed {F(12)}, rota-skipped {F(13)}, young {F(14)}, settled {F(15)} (drifted {F(16)}); "
            + $"blend listed {F(18)}, skipped {F(19)}, blended {F(20)}; "
            + $"evicted by level {F(21)}/{F(22)}/{F(23)}/{F(24)}/{F(25)}/{F(26)}, holding 8+ {F(28)}; "
            + $"created at own level {F(29)}, above {F(30)}, landings {F(31)}; "
            + $"young: age-capped {F(32)}, cut {F(33)}, epoch-cut {F(34)}, held <8/<32/<128/<256 {F(35)}/{F(36)}/{F(37)}/{F(38)}, "
            + $"samples {F(39)}, by level {F(40)}/{F(41)}/{F(42)}/{F(43)}/{F(44)}/{F(45)}, disagreeing {F(46)}, age 1024+ {F(47)}";
    }
}
