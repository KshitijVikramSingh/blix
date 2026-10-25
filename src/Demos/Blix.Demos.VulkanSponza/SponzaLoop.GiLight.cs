using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Blix.Core;
using Blix.Diagnostics;

namespace Blix.Demos.VulkanSponza;

// The GI's sun and the sliders' (2026-10-25, the user's): the Sun sliders turn the direct sun -- its light, the RT
// shadows -- and the GI holds the sun it was last lit for (GiToSun: giSunTarget). Two ways to move it:
//   Relight  a button: the GI's sun to the sliders', its full change behind the cook screen, the sweep's GPU slot raised
//            (Relight GPU ms) -- the boot's quality, as fast as the GPU goes.
//   Live     a toggle: the GI's sun follows the sliders', at most Live max deg/s, a change at a time once the last has
//            landed and composed, each at least Live min step; the light-change tiers' fast settings (--screen-first,
//            the provisional or kept start, the screen's stop rule: SponzaLoop.SweepFrame.cs ChangeStopsAt).
// Scripted runs keep their ways: --sun-change relights at its frame (the in-frame change, as before), --sun-move chases
// (SponzaLoop.SunMove.cs), --gi-follow: the GI follows the sliders every frame, as before this.
internal sealed partial class SponzaLoop
{
    private bool giHold = true, giLive;
    private float giLiveRate = 3f, giLiveMinStep = 0.5f, giRelightGpuMs = 30f, giKeepOldBelow = 10f;
    // The stop rules a mode lands at (0 all, 1 no near round, 2 the coarse sweep and its copy, 3 the screen's gather).
    private int giLiveStop = 3, giRelightStop;
    private static readonly string[] GiStopNames = { "full", "no near round", "coarse + copy", "screen" };
    private bool giRelighting;
    private (float GpuMs, bool CookScreen, int Stop, bool ScreenFirst, float OldStart, float KeepOld)? giSaved;
    private readonly Stopwatch giLiveClock = new();

    private void ParseGiLight(AppArgs args)
    {
        giHold = !args.Flag("gi-follow");
        giLive = args.Flag("gi-live");
        if (args.Float("gi-live-rate") is { } r) giLiveRate = Math.Clamp(r, 0.05f, 30f);
        if (args.Float("gi-live-min-step") is { } m) giLiveMinStep = Math.Clamp(m, 0.05f, 10f);
        if (args.Float("gi-relight-gpu-ms") is { } g) giRelightGpuMs = Math.Clamp(g, 1f, 200f);
        if (args.Float("gi-keep-old-below") is { } k) giKeepOldBelow = Math.Clamp(k, 0f, 90f);
    }

    private Vector3 SliderToSun => -Vector3.Normalize(sunDirection);
    // Live still walking toward the sliders' sun (a shot waits for it: SponzaLoop.GpuReference.cs ShotReady).
    private bool GiLiveBehind => giLive && giHold && giSunTarget != Vector3.Zero
        && MathF.Acos(Math.Clamp(Vector3.Dot(giSunTarget, SliderToSun), -1f, 1f)) * 180f / MathF.PI >= giLiveMinStep;

    // FollowSun, each frame: the held sun set once the scene's is; Live's steps.
    private void StepGiLight()
    {
        if (!giHold || sunMoving || flipPushChange) return;
        if (giSunTarget == Vector3.Zero) { giSunTarget = SliderToSun; return; }
        if (!giLive || giRelighting || !liveSkyGathered || LightChangeInFlight || ComposeUnderWay || splitComposeDirty)
        {
            if (!giLive) giLiveClock.Reset();
            return;
        }
        var to = SliderToSun;
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(giSunTarget, to), -1f, 1f)) * 180f / MathF.PI;
        if (angle < giLiveMinStep) return;
        // The step the rate allows since the last one began, at most two seconds' worth (idle, a jump of the sliders is still
        // walked at the rate); at least the min step once that much time has passed.
        if (!giLiveClock.IsRunning) giLiveClock.Start();
        var allowed = giLiveRate * MathF.Min((float)giLiveClock.Elapsed.TotalSeconds, 2f);
        if (allowed < giLiveMinStep) return;
        var step = MathF.Min(angle, allowed);
        giLiveClock.Restart();
        BeginGiChange(Rotate(giSunTarget, to, step), relight: false);
    }

    private static Vector3 Rotate(Vector3 from, Vector3 to, float degrees)
    {
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(from, to), -1f, 1f));
        if (angle < 1e-6f) return to;
        var t = Math.Clamp(degrees * MathF.PI / 180f / angle, 0f, 1f);
        var s = MathF.Sin(angle);
        return Vector3.Normalize(MathF.Sin((1f - t) * angle) / s * from + MathF.Sin(t * angle) / s * to);
    }

    // The button: the GI's sun to the sliders', the change behind the cook screen at the raised slot.
    private void Relight()
    {
        if (!liveSkyGathered) return;
        BeginGiChange(SliderToSun, relight: true);
    }

    private void BeginGiChange(Vector3 toSun, bool relight)
    {
        giSaved ??= (sweepGpuMs, changeCookScreen, changeStop, screenFirst, oldStartBelow, keepOldBelow);
        giRelighting = relight;
        sweepGpuMs = relight ? giRelightGpuMs : giSaved.Value.GpuMs;
        changeCookScreen = relight;
        changeStop = relight ? giRelightStop : giLiveStop;
        if (!relight)
        {
            // Live: the screen first; a small step keeps the old light, the rest start from the re-aim's provisional.
            screenFirst = true;
            oldStartBelow = MathF.Max(giSaved.Value.OldStart, 30f);
            keepOldBelow = giKeepOldBelow;
        }
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(giSunTarget, toSun), -1f, 1f)) * 180f / MathF.PI;
        giSunTarget = toSun;   // FollowSun's turn test starts the change (LiveRestart)
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] GI light: {(relight ? "relight" : "live step")} of {angle:0.00} deg (stop at {GiStopNames[changeStop]}{(relight ? $", {giRelightGpuMs:0} ms a frame, the cook screen" : "")})."));
    }

    // FinishSplitCook: a mode's change landed -- the settings it ran under back.
    private void GiChangeLanded()
    {
        if (giSaved is not { } saved) return;
        sweepGpuMs = saved.GpuMs;
        changeCookScreen = saved.CookScreen;
        changeStop = saved.Stop;
        screenFirst = saved.ScreenFirst;
        oldStartBelow = saved.OldStart;
        keepOldBelow = saved.KeepOld;
        giSaved = null;
        giRelighting = false;
    }

    // The Sun scope's panel (SponzaLoop.Debug.cs).
    private void GiLightControls(DebugContext debug)
    {
        using (debug.Scope("GI light"))
        {
            var lag = giSunTarget == Vector3.Zero ? 0f : MathF.Acos(Math.Clamp(Vector3.Dot(giSunTarget, SliderToSun), -1f, 1f)) * 180f / MathF.PI;
            debug.Values.Value("gi-sun", string.Create(CultureInfo.InvariantCulture,
                $"{lag:0.0} deg from the sliders' sun; {(giRelighting ? "relighting" : LightChangeInFlight ? (giLive ? "live step in flight" : "changing") : giLive ? "live" : giHold ? "held" : "following the sliders")}"));
            if (debug.Controls.Button("Relight (cook)") && !LightChangeInFlight) Relight();
            giLive = debug.Controls.Toggle("Live", giLive);
            giLiveRate = debug.Controls.Float("Live max (deg/s)", giLiveRate, 0.1f, 10f);
            giLiveMinStep = debug.Controls.Float("Live min step (deg)", giLiveMinStep, 0.1f, 5f);
            giLiveStop = debug.Controls.Enum("Live stops at", giLiveStop, GiStopNames);
            giKeepOldBelow = debug.Controls.Float("Live keeps the old light below (deg)", giKeepOldBelow, 0f, 30f);
            giRelightStop = debug.Controls.Enum("Relight stops at", giRelightStop, GiStopNames);
            giRelightGpuMs = debug.Controls.Float("Relight GPU ms a frame", giRelightGpuMs, 3f, 100f);
        }
    }
}
