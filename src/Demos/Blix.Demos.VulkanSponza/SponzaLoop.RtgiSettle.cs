using System.Diagnostics;
using System.Globalization;
using ImGuiNET;

namespace Blix.Demos.VulkanSponza;

// RTGI: the cache's first settle behind the cook screen. At boot the cache holds nothing and every pixel's light is
// young: the frames until its error comes down are spent on the cache alone -- the pre-pass, placement, the trace, the
// training, resolve, filter and eviction, and the readout (its uncertainty map placement reads, its error the progress)
// -- with the point budget at its most, and no upsample, lit pass or TAA; the screen is the cook screen's, the camera
// held, a window showing the visible's mean error as it falls. Done when that error fell under 2% over the last 120
// frames (at least 120, at most 3,000), and the scene comes up already lit. Only the first: a sun or light change and
// the camera's motion after it stay in the frame, the budget spending on them (SponzaLoop.RtgiBudget.cs) -- the user's
// call (2026-10-28). --no-rtgi-settle: the light learns in the frame from the start, as before (the truth dumps' frame
// numbers count from there).
internal sealed partial class SponzaLoop
{
    private const int RtgiSettleMinFrames = 120, RtgiSettleMaxFrames = 3000, RtgiSettleWindow = 120;
    private const float RtgiSettleDrop = 0.02f;
    private bool rtgiSettleWanted = true;
    private bool rtgiSettleDone;
    private int rtgiSettleCount;
    private readonly Stopwatch rtgiSettleClock = new();
    // The visible's mean relative error each settle frame (the readout's sum, read where it lies), the last window's.
    private readonly float[] rtgiSettleErrors = new float[RtgiSettleWindow];
    private float rtgiSettleFirstError = -1f, rtgiSettleError = -1f;
    private uint rtgiSettleLastSeen, rtgiSettleLastErr;
    private readonly byte[] rtgiSettleSeen = new byte[36];
    private int rtgiSettleVolumeFrames;
    // The volume not yet what the cache's light should settle against: placing, cooking or refining its probes (they size
    // the cells). The near field's cook too: its regions key the cells (rtgi_cache.glsl's rtgiRegion), and a settle ended before them
    // trained cells the regions then left behind -- the scene came up unsettled (the crevices testbed, 2026-10-30).
    private bool RtgiVolumeSettling => rtgiVolume && (!rtgiVolumeBuilt || !rtgiVolReady || (rtgiNearCook && !rtgiNearCookReady));

    // Settling now: its first settle not done, the scene and its tracer up, no cook holding the screen.
    private bool RtgiSettleNow => rtgiSettleWanted && !rtgiSettleDone && fullyLoaded && frameTracer is not null
        && !CookScreenNow && halfLightingNow;

    // After the frame's readout is recorded: one settle frame counted, the error read, the end judged.
    private void TickRtgiSettle()
    {
        if (!RtgiSettleNow || rtgiDoneCount.Equals(default(Blix.Graphics.GpuBufferHandle))) return;
        if (!rtgiSettleClock.IsRunning) rtgiSettleClock.Start();
        // The world volume: the cache's light is judged only once the volume is cooked and refined, and the near field cooked
        // -- the cells are sized and keyed by them then (a cache settled before was sized by the camera, and started over).
        // At most RtgiSettleMaxFrames of waiting.
        if (RtgiVolumeSettling && ++rtgiSettleVolumeFrames < RtgiSettleMaxFrames)
        {
            device.ReadMappedGpuBuffer(rtgiDoneCount, 0, rtgiSettleSeen);
            var s0 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(rtgiSettleSeen);
            rtgiSettleLastSeen = s0[0];
            rtgiSettleLastErr = s0[8];
            return;
        }
        device.ReadMappedGpuBuffer(rtgiDoneCount, 0, rtgiSettleSeen);
        var seen = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(rtgiSettleSeen);
        var seenNew = seen[0] - rtgiSettleLastSeen;
        var errNew = seen[8] - rtgiSettleLastErr;
        rtgiSettleLastSeen = seen[0];
        rtgiSettleLastErr = seen[8];
        if (seenNew == 0 || seenNew >= 1_000_000u) return;
        var mean = errNew / 4096f / seenNew;
        rtgiSettleErrors[rtgiSettleCount % RtgiSettleWindow] = mean;
        rtgiSettleCount++;
        rtgiSettleError = mean;
        if (rtgiSettleFirstError < 0f) rtgiSettleFirstError = mean;
        if (rtgiSettleCount < Math.Max(RtgiSettleMinFrames, RtgiSettleWindow)) return;
        // The window's first and last quarters: the error's fall over it, against noise.
        float early = 0f, late = 0f;
        var quarter = RtgiSettleWindow / 4;
        for (var k = 0; k < quarter; k++)
        {
            early += rtgiSettleErrors[(rtgiSettleCount + k) % RtgiSettleWindow];
            late += rtgiSettleErrors[(rtgiSettleCount - 1 - k + RtgiSettleWindow) % RtgiSettleWindow];
        }
        var drop = (early - late) / MathF.Max(early, 1e-6f);
        if (drop >= RtgiSettleDrop && rtgiSettleCount < RtgiSettleMaxFrames) return;
        rtgiSettleDone = true;
        rtgiSettleClock.Stop();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] rtgi: the light settled behind the cook screen in {rtgiSettleCount:N0} frames, {rtgiSettleClock.Elapsed.TotalSeconds:0.0} s: the visible's mean error {rtgiSettleFirstError:0.000} -> {late / quarter:0.000} ({100f * drop:0.0}% over the last {RtgiSettleWindow} frames)."));
    }

    // The cook window's section while settling.
    private void DrawRtgiSettle()
    {
        ImGui.TextUnformatted("Settling the light -- the cache learns the scene's light with the GPU to itself; the scene comes up lit.");
        if (RtgiVolumeSettling)
            ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture,
                $"World volume: {(!rtgiVolumeBuilt ? "placing probes" : !rtgiVolReady ? $"cooking what each probe sees (round {rtgiVolRefineRound + 1})" : "cooking the near field")}"));
        var fallen = rtgiSettleFirstError > 0f && rtgiSettleError >= 0f ? 1f - rtgiSettleError / rtgiSettleFirstError : 0f;
        ImGui.ProgressBar(Math.Clamp(rtgiSettleCount / (float)RtgiSettleMaxFrames, 0f, 1f), new System.Numerics.Vector2(-1f, 0f),
            string.Create(CultureInfo.InvariantCulture,
                $"{rtgiSettleCount:N0} frames, {rtgiSettleClock.Elapsed.TotalSeconds:0.0} s; the visible's error {rtgiSettleError:0.000} ({100f * fallen:0}% down from {rtgiSettleFirstError:0.000})"));
    }
}
