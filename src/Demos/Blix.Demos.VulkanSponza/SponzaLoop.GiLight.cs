using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Blix.Core;
using Blix.Diagnostics;

namespace Blix.Demos.VulkanSponza;

// The GI's sun and the sliders' (2026-10-25, the user's): the Sun sliders turn the direct sun -- its light, the RT
// shadows -- and the GI holds the sun it was last lit for (GiToSun: giSunTarget). Two ways to move it:
//   Relight  a button: the GI's sun to the sliders', every texel gathered again behind the frame (the converge: the scene
//            back to the boot's quality, 0.025 in ~45 s on Crytek Sponza, the screen 0.070, 2026-10-25), nothing blocked.
//   Cook to completion  a button: the same behind the cook screen, the sweep's GPU slot raised (Relight GPU ms) and its
//            full refine -- the screen 0.046, ~130 s blocked.
//   Live     play / pause: the sun moves along its day (SponzaLoop.SunMove.cs) at Speed deg/s (at most Live max), morning
//            to evening or back, from where the sliders put it; the GI follows a change at a time once the last has
//            landed and composed (Live settle s), each at least Live min step, with the light-change tiers' fast settings
//            (--screen-first, the kept or provisional start, the screen's stop rule: SponzaLoop.SweepFrame.cs
//            ChangeStopsAt). Chasing the sliders as they were dragged chased every overshoot (the user, 2026-10-25).
// Scripted runs keep their ways: --sun-change relights at its frame (the in-frame change, as before), --sun-move chases
// (SponzaLoop.SunMove.cs), --gi-follow: the GI follows the sliders every frame, as before this.
internal sealed partial class SponzaLoop
{
    private bool giHold = true, giLive;
    private float giPlaySpeed = 1f, giLiveSettle = 0.3f;
    // Live max lead (deg, 5; the steps at most this, small steps up to 5 deg the user's choice): the playing sun waits while it is this far ahead of the GI's -- at 1 deg/s on Crytek Sponza
    // the GI fell behind, each step grew (6 -> 19 deg) and took longer (5 -> 18 s): 15-19 deg behind (2026-10-25).
    private float giMaxLead = 5f;
    private bool LiveSunWaits => giLive && giSunTarget != Vector3.Zero
        && MathF.Acos(Math.Clamp(Vector3.Dot(giSunTarget, SliderToSun), -1f, 1f)) * 180f / MathF.PI >= giMaxLead;
    private float giLiveRate = 3f, giLiveMinStep = 0.5f, giRelightGpuMs = 30f, giKeepOldBelow = 10f;
    // The stop rules a mode lands at (0 all, 1 no near round, 2 the coarse sweep and its copy, 3 the screen's gather).
    private int giLiveStop = 3, giRelightStop;
    private static readonly string[] GiStopNames = { "full", "no near round", "coarse + copy", "screen" };
    private bool giRelighting;
    // Live's converge: its steps stop at the screen's gather, so the texels off it (the corridors lit by bounce) were never
    // gathered again and drifted (the user, 2026-10-25). Once Live is paused and settled after a step, every texel is
    // gathered again at the same sun behind the frame (the select off, the full change, the normal slot); Play re-aims over
    // it. --gi-no-converge: off.
    private bool giConverge = true, giNeedsConverge, giConverging;
    private int giRelightAt = -1;
    private float giStopHour = float.NaN;
    private bool giStopRelight;
    private (float GpuMs, bool CookScreen, int Stop, bool ScreenFirst, float OldStart, float KeepOld, float ScreenRefine, float Boost, float Select)? giSaved;
    // Live's boost (GPU ms a frame, 0 off): a live step's sweep slot and its re-aim's bounces a frame (SponzaLoop.ChangeBoost).
    private float giBoostMs = 10f;

    private void ParseGiLight(AppArgs args)
    {
        giHold = !args.Flag("gi-follow");
        giLive = args.Flag("gi-live");
        if (args.Float("gi-live-speed") is { } sp) giPlaySpeed = Math.Clamp(sp, 0.05f, 30f);
        if (args.Float("gi-live-max-lead") is { } ml) giMaxLead = Math.Clamp(ml, 0.5f, 90f);
        if (args.Float("gi-live-settle") is { } ls) giLiveSettle = Math.Clamp(ls, 0f, 30f);
        if (args.Float("gi-live-boost-ms") is { } bm) giBoostMs = Math.Clamp(bm, 0f, 100f);
        // --gi-relight-at F (a measurement): pause Live and press Relight at post-load frame F (when no change is in flight).
        if (args.Int("gi-relight-at") is { } ra) giRelightAt = ra;
        // --gi-cook-to-completion-at F: the debug cook at post-load frame F.
        if (args.Int("gi-cook-to-completion-at") is { } ca) { giRelightAt = ca; giStopRelight = true; }
        giConverge = !args.Flag("gi-no-converge");
        // --gi-stop-hour H [relight] (a measurement): Live stops at the day's hour angle H exactly, then waits for its
        // converge -- or, with 'relight', presses Relight -- so every variant ends at the same sun.
        if (args.Values("gi-stop-hour", 1) is [var h]) giStopHour = float.Parse(h, CultureInfo.InvariantCulture);
        giStopRelight = args.Flag("gi-stop-relight");
        if (args.Float("gi-live-rate") is { } r) giLiveRate = Math.Clamp(r, 0.05f, 30f);
        if (args.Float("gi-live-min-step") is { } m) giLiveMinStep = Math.Clamp(m, 0.05f, 10f);
        if (args.Float("gi-relight-gpu-ms") is { } g) giRelightGpuMs = Math.Clamp(g, 1f, 200f);
        if (args.Float("gi-keep-old-below") is { } k) giKeepOldBelow = Math.Clamp(k, 0f, 90f);
    }

    private Vector3 SliderToSun => -Vector3.Normalize(sunDirection);
    private bool GiBehindSliders => giSunTarget != Vector3.Zero && MathF.Acos(Math.Clamp(Vector3.Dot(giSunTarget, SliderToSun), -1f, 1f)) * 180f / MathF.PI >= 0.05f;

    // FollowSun, each frame: the held sun set once the scene's is; Live's steps.
    private void StepGiLight()
    {
        if (!giHold || flipPushChange) return;
        // --gi-live: play from the start (once the light has landed).
        if (giLive && !sunMoving && liveSkyGathered) PlayLive(true);
        if (!sunMoving && giSunTarget == Vector3.Zero) giSunTarget = SliderToSun;
        // Paused (or stopped) behind the sliders' sun: one last step to it, then the converge there -- pausing left the GI on
        // its last step's sun, up to a step behind the one shown (a walk to noon read 18-26% dark, 2026-10-25).
        var settled = !sunMoving && liveSkyGathered && !LightChangeInFlight && !ComposeUnderWay && !splitComposeDirty && giRelightAt < 0 && !giRelighting;
        if (settled && giNeedsConverge && giSunTarget != Vector3.Zero
            && MathF.Acos(Math.Clamp(Vector3.Dot(giSunTarget, SliderToSun), -1f, 1f)) * 180f / MathF.PI >= 0.05f)
            BeginGiChange(SliderToSun, relight: false);
        else if (giConverge && giNeedsConverge && !giConverging && settled)
            BeginGiConverge();
        if (giRelightAt >= 0 && postLoadFrames >= giRelightAt && !LightChangeInFlight && liveSkyGathered)
        {
            giRelightAt = -1;
            if (giLive) PlayLive(false);
            if (giStopRelight) CookToCompletion(); else Relight();
        }
    }

    // Live's play / pause: the moving sun from where the sliders put it -- the day's hour at their elevation, on the side
    // nearer their azimuth (a sun off the day's circle comes onto it there).
    private void PlayLive(bool play)
    {
        giLive = play;
        if (!play) { sunMoving = false; return; }
        sunMoveRate = MathF.Min(giPlaySpeed, giLiveRate);
        sunMoveSettle = giLiveSettle;
        sunMoveNorth = BandNorth(out _);
        var now = SliderToSun;
        var elevation = MathF.Max(MathF.Asin(Math.Clamp(now.Y, -1f, 1f)) * 180f / MathF.PI, sunMoveMinElevation);
        var hour = HourAtElevation(elevation);
        float Off(float h) => Vector3.Dot(-ToScene(SunLocal(AuthoredLatitude, Obliquity, h), sunMoveNorth), -now);
        sunMoveHour = Off(hour) >= Off(-hour) ? hour : -hour;
        giSunTarget = giSunTarget == Vector3.Zero ? now : giSunTarget;
        sunMoveClock.Restart();
        sunMoving = true;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] GI light: live, the sun at {sunMoveRate:0.##} deg/s {(sunMoveWay > 0 ? "toward evening" : "toward morning")} from hour angle {sunMoveHour:0.0}."));
    }

    // StepSunMove, the hour just advanced: --gi-stop-hour reached -- the sun put there exactly and Live paused.
    private bool GiStopsAt(ref float hour, float before)
    {
        if (float.IsNaN(giStopHour) || !giLive) return false;
        if ((before - giStopHour) * (hour - giStopHour) > 0f && hour != giStopHour) return false;
        hour = giStopHour;
        giStopHour = float.NaN;
        PlayLive(false);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] GI light: live stopped at hour angle {hour:0.###} (--gi-stop-hour){(giStopRelight ? "; relight" : "; its converge next")}."));
        if (giStopRelight) giRelightAt = 0;
        return true;
    }

    // StepSunMove, the moving sun's GI settled and a step's worth behind: Live's step, with its settings.
    private bool LiveStepNow()
    {
        if (!giLive) return false;
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(giSunTarget, SliderToSun), -1f, 1f)) * 180f / MathF.PI;
        if (angle >= giLiveMinStep) { giConverging = false; BeginGiChange(SliderToSun, relight: false); }
        return true;
    }

    private static Vector3 Rotate(Vector3 from, Vector3 to, float degrees)
    {
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(from, to), -1f, 1f));
        if (angle < 1e-6f) return to;
        var t = Math.Clamp(degrees * MathF.PI / 180f / angle, 0f, 1f);
        var s = MathF.Sin(angle);
        return Vector3.Normalize(MathF.Sin((1f - t) * angle) / s * from + MathF.Sin(t * angle) / s * to);
    }

    // Relight: the converge at the sliders' sun, behind the frame.
    private void Relight()
    {
        if (!liveSkyGathered) return;
        BeginGiConverge();
    }

    // Cook to completion: the full change behind the cook screen at the raised slot.
    private void CookToCompletion()
    {
        if (!liveSkyGathered) return;
        BeginGiChange(SliderToSun, relight: true);
    }

    private void BeginGiChange(Vector3 toSun, bool relight)
    {
        giSaved ??= (sweepGpuMs, changeCookScreen, changeStop, screenFirst, oldStartBelow, keepOldBelow, screenRefineBudget, changeBoostMs, sunChangeSelect);
        // Relight gathers every texel as the boot does: the sun change select off (it took again only the texels whose relit
        // light moved from what they showed -- after Live's steps, stopped at the screen, the stale ones it skipped stayed
        // wrong, the user's 'after live a relight still looks wrong', 2026-10-25).
        sunChangeSelect = relight ? 0f : giSaved.Value.Select;
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
            // Live's steps (at most Live max lead, 5 deg) without the screen gather's refine: under 0.003 of the screen's error
            // for ~2 s a step at 2-5 deg (Crytek Sponza, 2026-10-25); the boost for the step.
            screenRefineBudget = 0f;
            changeBoostMs = giBoostMs;
        }
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(giSunTarget, toSun), -1f, 1f)) * 180f / MathF.PI;
        giSunTarget = toSun;   // FollowSun's turn test starts the change (LiveRestart)
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] GI light: {(relight ? "relight" : "live step")} of {angle:0.00} deg (stop at {GiStopNames[changeStop]}{(relight ? $", {giRelightGpuMs:0} ms a frame, the cook screen" : "")})."));
    }

    // Every texel gathered again at the GI's own sun, behind the frame (the restart the sun's turn would start, forced).
    private void BeginGiConverge()
    {
        giSaved ??= (sweepGpuMs, changeCookScreen, changeStop, screenFirst, oldStartBelow, keepOldBelow, screenRefineBudget, changeBoostMs, sunChangeSelect);
        sunChangeSelect = 0f;
        changeStop = 0;
        changeCookScreen = false;
        screenFirst = false;
        changeBoostMs = 0f;
        sweepGpuMs = giSaved.Value.GpuMs;
        keepOldBelow = MathF.Max(giKeepOldBelow, 1f);   // the same sun: the shown light stays up until gathered
        oldStartBelow = MathF.Max(giSaved.Value.OldStart, 30f);
        giConverging = true;
        giNeedsConverge = false;
        var angle = giSunTarget == Vector3.Zero ? 0f : MathF.Acos(Math.Clamp(Vector3.Dot(giSunTarget, SliderToSun), -1f, 1f)) * 180f / MathF.PI;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] GI light: converge -- every texel gathered again behind the frame, at the sliders' sun ({angle:0.00} deg from the GI's)."));
        giSunTarget = SliderToSun;
        // A turn starts its own restart (FollowSun's test); at the same sun it is forced.
        if (angle < 0.25f) LiveRestart(giSunTarget);
    }

    // FinishSplitCook: a mode's change landed -- the settings it ran under back.
    private void GiChangeLanded()
    {
        // A live step leaves the texels off the screen as they were: a converge follows once paused; a relight or a
        // converge gathered them all.
        if (giSaved is not null) giNeedsConverge = !giRelighting && !giConverging;
        giConverging = false;
        if (giSaved is not { } saved) return;
        sweepGpuMs = saved.GpuMs;
        changeCookScreen = saved.CookScreen;
        changeStop = saved.Stop;
        screenFirst = saved.ScreenFirst;
        oldStartBelow = saved.OldStart;
        keepOldBelow = saved.KeepOld;
        screenRefineBudget = saved.ScreenRefine;
        changeBoostMs = saved.Boost;
        sunChangeSelect = saved.Select;
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
                $"{lag:0.0} deg from the sliders' sun; {(giRelighting ? "cooking to completion" : giConverging ? "converging behind the frame" : LightChangeInFlight ? (giLive ? "live step in flight" : "changing") : giLive ? "live" : giHold ? "held" : "following the sliders")}"));
            if (debug.Controls.Button("Relight (behind the frame)") && !LightChangeInFlight && !giLive) Relight();
            if (debug.Controls.Button(giLive ? "Live: pause" : "Live: play")) PlayLive(!giLive);
            giPlaySpeed = debug.Controls.Float("Live speed (deg/s)", giPlaySpeed, 0.05f, giLiveRate);
            if (giLive) sunMoveRate = MathF.Min(giPlaySpeed, giLiveRate);
            sunMoveWay = debug.Controls.Enum("Live direction", sunMoveWay > 0 ? 0 : 1, new[] { "toward evening", "toward morning" }) == 0 ? 1f : -1f;
            sunMoveMinElevation = debug.Controls.Float("Live lowest sun (deg)", sunMoveMinElevation, 5f, 80f);
            giLiveRate = debug.Controls.Float("Live max (deg/s)", giLiveRate, 0.1f, 10f);
            giLiveMinStep = debug.Controls.Float("Live min step (deg)", giLiveMinStep, 0.1f, 5f);
            giMaxLead = debug.Controls.Float("Live max lead (deg)", giMaxLead, 0.5f, 30f);
            giBoostMs = debug.Controls.Float("Live boost GPU ms a frame (0 off)", giBoostMs, 0f, 30f);
            giLiveSettle = debug.Controls.Float("Live settle (s)", giLiveSettle, 0f, 5f);
            if (giLive) sunMoveSettle = giLiveSettle;
            giLiveStop = debug.Controls.Enum("Live stops at", giLiveStop, GiStopNames);
            giKeepOldBelow = debug.Controls.Float("Live keeps the old light below (deg)", giKeepOldBelow, 0f, 30f);
        }
        // Debugging: the full change behind the cook screen (the boot's quality at the most rays; ~130 s blocked).
        using (debug.Scope("GI light: debug"))
        {
            if (debug.Controls.Button("Cook to completion (blocking)") && !LightChangeInFlight && !giLive) CookToCompletion();
            giRelightStop = debug.Controls.Enum("Cook stops at", giRelightStop, GiStopNames);
            giRelightGpuMs = debug.Controls.Float("Cook GPU ms a frame", giRelightGpuMs, 3f, 100f);
        }
    }
}
