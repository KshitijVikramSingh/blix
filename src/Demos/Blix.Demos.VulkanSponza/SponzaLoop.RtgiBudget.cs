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
    // On by default since 2026-10-28 (--rtgi-no-budget: the most every frame).
    private bool rtgiBudget = true;
    // --rtgi-budget-ms T (8; 0 off): the frame's ceiling. Over it (the frame interval's running mean -- GPU-bound since the
    // decoupled present) the spend above the floor is cut at once, under it by 0.3 ms it comes back slowly: a still view
    // holds the display's 120 rather than swinging between 90 and 120 at ~9.8 ms frames (the user, Crytek Sponza 1080p,
    // 2026-10-28), and a change spends what fits.
    private float rtgiBudgetMs = 8f, rtgiBudgetCap = 1f, rtgiBudgetFloorScale = 1f, rtgiBudgetFrameMs;
    private readonly Stopwatch rtgiBudgetFrameClock = Stopwatch.StartNew();
    // The floor: 80,000 visible and 24,000 from hits -- a settled Crytek Sponza 1080p 6.98 ms against 7.53 at 110,000 and
    // 32,000 (the in-boot A/B), the truth the same for the still view, a 90 degree turn and an instant sun jump within
    // 0.005 (2026-10-28).
    private int rtgiBudgetMinTiles = 80_000, rtgiBudgetMinHits = 24_000;
    private float rtgiBudgetTau = 0.75f;
    // --rtgi-budget-jump R: the visible's error needs points from R times its settled floor, all of them at 1.5 R. 2: a
    // settled Crytek Sponza 7.52 ms against 8.35 at 1 and 9.21 at fixed points, the still view's and a turn's truth within
    // 0.003 of 1's (2026-10-28).
    private float rtgiBudgetJump = 2f;
    // A sun or light change's need falls over --rtgi-budget-sun-tau seconds (3), the camera's over --rtgi-budget-tau: a
    // relight takes the cells' histories longer than a turn takes the new cells -- at 0.75 an instant sun jump's truth
    // 0.192 / 0.127 / 0.096 at 30 / 120 / 600 frames after it, at 3 0.184 / 0.116 / 0.089, fixed points 0.187 / 0.113 /
    // 0.086 (2026-10-28).
    private float rtgiBudgetSunTau = 3f, rtgiNeedSun;
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
        if (args.Flag("rtgi-budget")) rtgiBudget = true;
        if (args.Flag("rtgi-no-budget")) rtgiBudget = false;
        if (args.Float("rtgi-budget-ms") is { } ms) rtgiBudgetMs = MathF.Max(0f, ms);
        if (args.Values("rtgi-budget-min", 2) is [var v, var h])
        {
            rtgiBudgetMinTiles = Math.Max(1, int.Parse(v, CultureInfo.InvariantCulture));
            rtgiBudgetMinHits = Math.Max(0, int.Parse(h, CultureInfo.InvariantCulture));
        }
        if (args.Float("rtgi-budget-tau") is { } tau) rtgiBudgetTau = MathF.Max(0.01f, tau);
        if (args.Float("rtgi-budget-jump") is { } jump) rtgiBudgetJump = MathF.Max(1f, jump);
        if (args.Float("rtgi-budget-sun-tau") is { } st) rtgiBudgetSunTau = MathF.Max(0.01f, st);
        if (args.Float("rtgi-list-worth-moving") is { } lwm) rtgiListWorthMoving = MathF.Max(1f, lwm);
        if (args.Int("rtgi-list-room-moving") is { } lrm) rtgiListRoomMoving = Math.Max(1024, lrm);
    }

    // Before RecordRtgi: this frame's visible and hit points.
    private void UpdateRtgiBudget()
    {
        var on = rtgiBudget || (Environment.GetEnvironmentVariable("BLIX_PROBE") == "budget" && AbArm == 1);
        // The first settle (SponzaLoop.RtgiSettle.cs) spends the most: the GPU is the cache's.
        if (!on || RtgiSettleNow || rtgiStats.Equals(default(Blix.Graphics.GpuBufferHandle)))
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
        rtgiMotionNow = MathF.Max(motion, rtgiMotionNow * MathF.Exp(-dt / rtgiBudgetTau));
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
            unsettled = Math.Clamp((mean - rtgiBudgetJump * rtgiBudgetErrFloor) / MathF.Max(0.5f * rtgiBudgetJump * rtgiBudgetErrFloor, 1e-4f), 0f, 1f);
        }
        rtgiBudgetUnsettled = unsettled;

        var fall = MathF.Exp(-dt / rtgiBudgetTau);
        rtgiNeedSun = MathF.Max(sun, rtgiNeedSun * MathF.Exp(-dt / rtgiBudgetSunTau));
        rtgiNeedVisible = MathF.Max(MathF.Max(motion, rtgiNeedSun), MathF.Max(MathF.Max(madeVisible, unsettled), rtgiNeedVisible * fall));
        rtgiNeedHits = MathF.Max(MathF.Max(rtgiNeedSun, 0.5f * motion), MathF.Max(MathF.Max(madeHits, unsettled), rtgiNeedHits * fall));
        // The ceiling: the frame interval's running mean over ~16 frames against it.
        var frameMs = (float)rtgiBudgetFrameClock.Elapsed.TotalMilliseconds;
        rtgiBudgetFrameClock.Restart();
        rtgiBudgetFrameMs = rtgiBudgetFrameMs <= 0f ? frameMs : rtgiBudgetFrameMs + (Math.Clamp(frameMs, 0f, 50f) - rtgiBudgetFrameMs) / 16f;
        // Over the ceiling with the spend above the floor already gone (a walk: new cells, shadows retraced, the screen's
        // light every frame), the floor itself comes down to half; under it, the floor comes back first, then the rest.
        if (rtgiBudgetMs > 0f)
        {
            if (rtgiBudgetFrameMs > rtgiBudgetMs)
            {
                if (rtgiBudgetCap > 0f) rtgiBudgetCap = MathF.Max(0f, rtgiBudgetCap - 0.05f);
                else rtgiBudgetFloorScale = MathF.Max(0.5f, rtgiBudgetFloorScale - 0.02f);
            }
            else if (rtgiBudgetFrameMs < rtgiBudgetMs - 0.3f)
            {
                if (rtgiBudgetFloorScale < 1f) rtgiBudgetFloorScale = MathF.Min(1f, rtgiBudgetFloorScale + 0.005f);
                else rtgiBudgetCap = MathF.Min(1f, rtgiBudgetCap + 0.005f);
            }
        }
        // The needs keep their own fall; the cap limits what they spend.
        var cap = rtgiBudgetMs > 0f ? rtgiBudgetCap : 1f;
        var minTiles = (int)(Math.Min(rtgiBudgetMinTiles, rtgiTiles) * rtgiBudgetFloorScale);
        var minHits = (int)(Math.Min(rtgiBudgetMinHits, rtgiHitPoints) * rtgiBudgetFloorScale);
        rtgiTilesNow = minTiles + (int)((rtgiTiles - minTiles) * MathF.Min(rtgiNeedVisible, cap));
        rtgiHitsNow = minHits + (int)((rtgiHitPoints - minHits) * MathF.Min(rtgiNeedHits, cap));
        rtgiBudgetSumTiles += rtgiTilesNow;
        rtgiBudgetSumHits += rtgiHitsNow;
        rtgiBudgetFrames++;
    }

    // The training's list worth this frame (rtgi_train.comp's listNow: a cell listed for the resolve and the blend once its
    // pending samples times it reach those it holds): --rtgi-list-worth (8) on a still view, down to --rtgi-list-worth-moving
    // (2) as the camera moves -- a walk makes new cells every frame, each listed on every sample while young, the resolve
    // and the blend 1.1 of a walking Crytek Sponza 1080p frame's extra ~5 ms (2026-10-28); at 2 a young cell is resolved
    // as its samples come in pairs and more.
    private float rtgiListWorthMoving = 2f, rtgiMotionNow;
    // The cells the training may list this frame: the dirty list's room on a still view, down to --rtgi-list-room-moving
    // (32,768) as the camera moves. A walk lists every young cell it trains, every frame; the rest wait, their samples kept
    // in their accumulators, and are listed when next trained -- a young cell's light a frame or two later, where the
    // resolve and the blend over all of them were ~1 ms of a walking Crytek Sponza 1080p frame (2026-10-28).
    private int rtgiListRoomMoving = 32_768;
    private int RtgiListRoomNow => (int)(RtgiDirtyRoom + (Math.Min(rtgiListRoomMoving, RtgiDirtyRoom) - RtgiDirtyRoom) * rtgiMotionNow);
    private float RtgiListWorthNow => rtgiListWorth + (MathF.Min(rtgiListWorthMoving, rtgiListWorth) - rtgiListWorth) * rtgiMotionNow;

    // The profile line's: the window's mean spend.
    private string RtgiBudgetWork()
    {
        if (!rtgiBudget || rtgiBudgetFrames == 0) return "";
        var s = string.Create(CultureInfo.InvariantCulture,
            $"; budget: visible {rtgiBudgetSumTiles / rtgiBudgetFrames:N0}, hits {rtgiBudgetSumHits / rtgiBudgetFrames:N0} a frame (need now {rtgiNeedVisible:0.00} / {rtgiNeedHits:0.00}, cap {rtgiBudgetCap:0.00} floor {rtgiBudgetFloorScale:0.00} at {rtgiBudgetFrameMs:0.0} ms, unsettled {rtgiBudgetUnsettled:0.00}, error floor {rtgiBudgetErrFloor:0.000})");
        rtgiBudgetSumTiles = rtgiBudgetSumHits = 0;
        rtgiBudgetFrames = 0;
        return s;
    }
}

// --rtgi-screen-every N (4; 1 off): on a settled, still view the screen's light -- the readout, its blur and the upsample,
// ~1.2 ms a frame (Crytek Sponza 1080p: every 2nd frame -0.62 ms, every 4th -1.25, 7.43 -> 6.18 ms, the in-boot A/B,
// 2026-10-28) -- every Nth frame; between, the lit pass reads
// the incident targets as they were (as the incident pass's reuse did). Still: the camera and the sun as they were for two
// frames; settled: the budget's needs under a quarter, none from the sun (the cache's new cells keep the visible's ~0.1).
// Anything that moves, a sun change, the cache's error jumping, a truth dump or the first settle: every frame again.
internal sealed partial class SponzaLoop
{
    private int rtgiScreenEvery = 4;
    private int rtgiStillFrames;
    private long rtgiScreenFrame;
    private System.Numerics.Vector3 restLastPosition, restLastSun;
    private float restLastYaw, restLastPitch;
    private bool screenRestNow;

    private void ParseScreenRest(AppArgs args)
    {
        if (args.Int("rtgi-screen-every") is { } every) rtgiScreenEvery = Math.Max(1, every);
    }

    // Before the half-res lighting is recorded: whether this frame skips it.
    private void UpdateScreenRest()
    {
        var still = cameraPosition == restLastPosition && camera.Yaw == restLastYaw && camera.Pitch == restLastPitch && sunDirection == restLastSun;
        rtgiStillFrames = still ? rtgiStillFrames + 1 : 0;
        restLastPosition = cameraPosition;
        restLastYaw = camera.Yaw;
        restLastPitch = camera.Pitch;
        restLastSun = sunDirection;
        var settled = rtgiBudget && rtgiNeedVisible < 0.25f && rtgiNeedHits < 0.25f && rtgiNeedSun < 0.01f;
        var eligible = rtgiScreenEvery > 1 && RtgiReadoutReady && !RtgiSettleNow && !RtgiDumpDue && !RtgiTemporalNow
            && !gpuReference && !MoverActive && rtgiStillFrames >= 2 && settled;
        if (Environment.GetEnvironmentVariable("BLIX_PROBE") == "screenrest") eligible &= AbArm == 1;
        rtgiScreenFrame++;
        screenRestNow = eligible && rtgiScreenFrame % rtgiScreenEvery != 0;
    }
}
