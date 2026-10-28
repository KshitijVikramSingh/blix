using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;

namespace Blix.Demos.VulkanSponza;

// --rtgi-budget: the cache's points spent by need, not a fixed count. Each point costs ~18 ns of a frame through
// placement, the trace and the training (Crytek Sponza 1080p, the in-boot A/B, 2026-10-28) -- the ~350 k a frame were
// ~6 ms of a ~10.8 ms frame, spent the same on a still view whose cells settled long ago as on a turn or a sun change.
// Each frame the visible points (rtgiTiles at most) and those from last frame's hits (rtgiHitPoints at most) are each
// somewhere between their floor (--rtgi-budget-min V,H) and that most, by a need in [0, 1]:
//   visible  the camera's motion (turning, moving: new pixels, disocclusions), a sun change, and the cells the training
//            made at the samples' own level (a surface the cache had not held);
//   hits     a sun change, half the camera's motion, and the cells made above the own level and for landings (the
//            light one bounce away, much of it off the screen).
// A need rises at once and falls over --rtgi-budget-tau seconds (0.75): a stop does not drop the budget while the cells
// it just made are young. Where in the screen the visible points go is placement's (the uncertainty map, rtgi_rays.comp);
// the budget says how many. The cache's counters are read where they lie (a mapped buffer, no wait), a frame or two
// behind.
internal sealed partial class SponzaLoop
{
    private bool rtgiBudget;
    private int rtgiBudgetMinTiles = 110_000, rtgiBudgetMinHits = 32_000;
    private float rtgiBudgetTau = 0.75f;
    // This frame's spend, and the needs behind it.
    private int rtgiTilesNow, rtgiHitsNow;
    // Last frame's points (visible and hits): this frame's hit points draw from its rays, as many as there were.
    private int rtgiLastPointTotal;
    private float rtgiNeedVisible = 1f, rtgiNeedHits = 1f, rtgiBudgetUnsettled;
    private readonly Stopwatch rtgiBudgetClock = Stopwatch.StartNew();
    private double rtgiBudgetLastSeconds;
    private Vector3 rtgiBudgetLastCamera, rtgiBudgetLastForward, rtgiBudgetLastSun;
    private readonly uint[] rtgiBudgetLastStats = new uint[3];
    private readonly byte[] rtgiBudgetStats = new byte[12];
    // The readout's sampled visible pixels and their relative error summed (rtgi_readout.frag's done counts 0 and 8), and
    // the mean's settled floor: it follows the mean down at once and up slowly.
    private readonly byte[] rtgiBudgetSeen = new byte[36];
    private uint rtgiBudgetLastSeen, rtgiBudgetLastErr;
    private float rtgiBudgetErrFloor = -1f;
    // The window's spend, for the profile line.
    private double rtgiBudgetSumTiles, rtgiBudgetSumHits;
    private int rtgiBudgetFrames;

    private void ParseRtgiBudget(AppArgs args)
    {
        rtgiBudget = args.Flag("rtgi-budget");
        if (args.Values("rtgi-budget-min", 2) is [var v, var h])
        {
            rtgiBudgetMinTiles = Math.Max(1, int.Parse(v, CultureInfo.InvariantCulture));
            rtgiBudgetMinHits = Math.Max(0, int.Parse(h, CultureInfo.InvariantCulture));
        }
        if (args.Float("rtgi-budget-tau") is { } tau) rtgiBudgetTau = MathF.Max(0.01f, tau);
    }

    // Before RecordRtgi: this frame's visible and hit points.
    private void UpdateRtgiBudget()
    {
        var on = rtgiBudget || (Environment.GetEnvironmentVariable("BLIX_PROBE") == "budget" && AbArm == 1);
        if (!on || rtgiStats.Equals(default(Blix.Graphics.GpuBufferHandle)))
        {
            rtgiTilesNow = rtgiTiles;
            rtgiHitsNow = rtgiHitPoints;
            return;
        }
        var now = rtgiBudgetClock.Elapsed.TotalSeconds;
        var dt = (float)Math.Clamp(now - rtgiBudgetLastSeconds, 1e-4, 0.25);
        rtgiBudgetLastSeconds = now;

        // The camera: degrees a second turned, metres a second moved -- 90 degrees or 2 m a second is all the need there is.
        var forward = Vector3.Normalize(new Vector3(MathF.Sin(camera.Yaw * MathF.PI / 180f), 0f, -MathF.Cos(camera.Yaw * MathF.PI / 180f)) * MathF.Cos(camera.Pitch * MathF.PI / 180f)
            + new Vector3(0f, MathF.Sin(camera.Pitch * MathF.PI / 180f), 0f));
        var turned = MathF.Acos(Math.Clamp(Vector3.Dot(forward, rtgiBudgetLastForward), -1f, 1f)) * 180f / MathF.PI;
        var moved = Vector3.Distance(cameraPosition, rtgiBudgetLastCamera);
        var motion = Math.Clamp(turned / dt / 90f + moved / dt / 2f, 0f, 1f);
        // The sun: any turn of it is a relight.
        var sun = Vector3.Dot(Vector3.Normalize(sunDirection), rtgiBudgetLastSun) < 0.99999f ? 1f : 0f;
        rtgiBudgetLastForward = forward;
        rtgiBudgetLastCamera = cameraPosition;
        rtgiBudgetLastSun = Vector3.Normalize(sunDirection);

        // The training's new cells since the last read (rtgi_train.comp's words 29-31: at the own level, above it, for
        // landings); a smaller count than last time is the profile's window reset (RtgiWork), the count itself new.
        device.ReadMappedGpuBuffer(rtgiStats, 29 * 4, rtgiBudgetStats);
        var counts = MemoryMarshal.Cast<byte, uint>(rtgiBudgetStats);
        Span<float> made = stackalloc float[3];
        for (var i = 0; i < 3; i++)
        {
            made[i] = counts[i] >= rtgiBudgetLastStats[i] ? counts[i] - rtgiBudgetLastStats[i] : counts[i];
            rtgiBudgetLastStats[i] = counts[i];
        }
        // 3% of the points making a cell each is all the need there is (a settled Crytek Sponza: ~0.3%).
        var madeVisible = Math.Clamp(made[0] / (0.03f * Math.Max(1, rtgiTilesNow)), 0f, 1f);
        var madeHits = Math.Clamp((made[1] + made[2]) / (0.03f * Math.Max(1, rtgiHitsNow)), 0f, 1f);

        // The visible's mean relative error against its settled floor: half again above it is all the need there is. After
        // a change the new and young cells read high and fall as they learn -- the spend stays while the light is still
        // settling, where a timer let it go: a 90 degree turn's truth 0.111 against 0.103 at fixed points 600 frames on
        // (2026-10-28). The counts wrap (uint); the profile's window reset reads as a frame with none.
        device.ReadMappedGpuBuffer(rtgiDoneCount, 0, rtgiBudgetSeen);
        var seen = MemoryMarshal.Cast<byte, uint>(rtgiBudgetSeen);
        var seenNew = seen[0] - rtgiBudgetLastSeen;
        var errNew = seen[8] - rtgiBudgetLastErr;
        rtgiBudgetLastSeen = seen[0];
        rtgiBudgetLastErr = seen[8];
        var unsettled = 0f;
        if (seenNew > 0 && seenNew < 1_000_000u)
        {
            var mean = errNew / 4096f / seenNew;
            rtgiBudgetErrFloor = rtgiBudgetErrFloor < 0f || mean < rtgiBudgetErrFloor ? mean : rtgiBudgetErrFloor + (mean - rtgiBudgetErrFloor) * 0.002f;
            unsettled = Math.Clamp((mean - rtgiBudgetErrFloor) / MathF.Max(0.5f * rtgiBudgetErrFloor, 1e-4f), 0f, 1f);
        }
        rtgiBudgetUnsettled = unsettled;

        var fall = MathF.Exp(-dt / rtgiBudgetTau);
        rtgiNeedVisible = MathF.Max(MathF.Max(motion, sun), MathF.Max(MathF.Max(madeVisible, unsettled), rtgiNeedVisible * fall));
        rtgiNeedHits = MathF.Max(MathF.Max(sun, 0.5f * motion), MathF.Max(MathF.Max(madeHits, unsettled), rtgiNeedHits * fall));
        var minTiles = Math.Min(rtgiBudgetMinTiles, rtgiTiles);
        var minHits = Math.Min(rtgiBudgetMinHits, rtgiHitPoints);
        rtgiTilesNow = minTiles + (int)((rtgiTiles - minTiles) * rtgiNeedVisible);
        rtgiHitsNow = minHits + (int)((rtgiHitPoints - minHits) * rtgiNeedHits);
        rtgiBudgetSumTiles += rtgiTilesNow;
        rtgiBudgetSumHits += rtgiHitsNow;
        rtgiBudgetFrames++;
    }

    // The profile line's: the window's mean spend.
    private string RtgiBudgetWork()
    {
        if (!rtgiBudget || rtgiBudgetFrames == 0) return "";
        var s = string.Create(CultureInfo.InvariantCulture,
            $"; budget: visible {rtgiBudgetSumTiles / rtgiBudgetFrames:N0}, hits {rtgiBudgetSumHits / rtgiBudgetFrames:N0} a frame (need now {rtgiNeedVisible:0.00} / {rtgiNeedHits:0.00}, unsettled {rtgiBudgetUnsettled:0.00}, error floor {rtgiBudgetErrFloor:0.000})");
        rtgiBudgetSumTiles = rtgiBudgetSumHits = 0;
        rtgiBudgetFrames = 0;
        return s;
    }
}
