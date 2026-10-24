using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Blix.Core;

namespace Blix.Demos.VulkanSponza;

// A sun that keeps moving (--sun-move DEG_PER_S; M toggles it, 1 deg/s when no rate was given): the frame's sun -- its
// direct light, the RT shadows -- turns every frame, while the GI chases it a light change at a time. The GI's sun
// (GiToSun: the live patch light, the texels' sun bits, the re-aim) is held while a change is in flight and catches up
// to the frame's when it lands; a sun turning every frame restarted the change every frame (LiveRestart) and the sun
// bits for every texel with it, so nothing landed. Each change is the angle the sun moved while the last one ran: the
// smaller it is, the fewer texels its instant layer moves and the sun change select gathers (SponzaLoop.RatioRelight.cs).
internal sealed partial class SponzaLoop
{
    private float sunMoveRate;
    private bool sunMoving;
    private Vector3 giSunTarget;
    private readonly Stopwatch sunMoveClock = new();

    // The sun the GI works toward: the frame's, or while the sun moves, the one its change in flight was aimed at.
    private Vector3 GiToSun => sunMoving && giSunTarget != Vector3.Zero ? giSunTarget : -Vector3.Normalize(sunDirection);

    private bool LightChangeInFlight => splitCooking || liveRetargeting || sunNodeStage != SunNodeStage.Off;

    private void ParseSunMove(AppArgs args)
    {
        if (args.Float("sun-move") is { } rate && rate != 0f)
        {
            sunMoveRate = rate;
            sunMoving = true;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] sun move: {rate:0.##} deg/s (--sun-move)."));
        }
    }
    private readonly Stopwatch sunMoveLogClock = new();

    private void ToggleSunMove()
    {
        sunMoving = !sunMoving;
        if (sunMoveRate == 0f) sunMoveRate = 1f;
        giSunTarget = -Vector3.Normalize(sunDirection);
        sunMoveClock.Restart();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] sun move: {(sunMoving ? $"on, {sunMoveRate:0.##} deg/s" : "off")}."));
    }

    // FollowSun, each frame: the frame's sun turned by the frame's time; the GI's caught up when no change is in flight.
    private void StepSunMove()
    {
        if (!sunMoving) return;
        // Set once the scene's sun is (--sun, the authored one): parsing comes before it.
        if (giSunTarget == Vector3.Zero) giSunTarget = -Vector3.Normalize(sunDirection);
        var dt = sunMoveClock.IsRunning ? (float)sunMoveClock.Elapsed.TotalSeconds : 0f;
        sunMoveClock.Restart();
        // The rate is the sun's own angle a second: its yaw turns faster the higher it stands (a degree of yaw at pitch p is
        // cos p of a degree of arc).
        sunYaw += sunMoveRate * dt * MathF.PI / 180f / MathF.Max(MathF.Cos(sunPitch), 0.05f);
        // Wrapped into the overlay's slider range (-180..180): Crytek Sponza's sun stands at 180, and the slider clamped
        // every step past it back -- the sun never moved while the overlay was up.
        if (sunYaw > MathF.PI) sunYaw -= 2f * MathF.PI;
        else if (sunYaw < -MathF.PI) sunYaw += 2f * MathF.PI;
        UpdateSunDirection();
        if (!LightChangeInFlight) giSunTarget = -Vector3.Normalize(sunDirection);
        if (!sunMoveLogClock.IsRunning || sunMoveLogClock.Elapsed.TotalSeconds >= 5)
        {
            sunMoveLogClock.Restart();
            var lag = MathF.Acos(Math.Clamp(Vector3.Dot(giSunTarget, -Vector3.Normalize(sunDirection)), -1f, 1f)) * 180f / MathF.PI;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] sun move: yaw {sunYaw * 180f / MathF.PI:0.0}, pitch {sunPitch * 180f / MathF.PI:0.0}; the GI's sun {lag:0.00} deg behind (a change in flight {LightChangeInFlight}, stage {sunNodeStage})."));
        }
    }
}
