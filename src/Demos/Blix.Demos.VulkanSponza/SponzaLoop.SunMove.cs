using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Blix.Core;

namespace Blix.Demos.VulkanSponza;

// A sun that keeps moving (--sun-move DEG_PER_S; M toggles it, 0.5 deg/s when no rate was given): the frame's sun -- its
// direct light, the RT shadows -- turns every frame, while the GI chases it a light change at a time. The GI's sun
// (GiToSun: the live patch light, the texels' sun bits, the re-aim) is held while a change is in flight and catches up
// to the frame's when it lands; a sun turning every frame restarted the change every frame (LiveRestart) and the sun
// bits for every texel with it, so nothing landed. Each change is the angle the sun moved while the last one ran: the
// smaller it is, the fewer texels its instant layer moves and the sun change select gathers (SponzaLoop.RatioRelight.cs).
internal sealed partial class SponzaLoop
{
    private float sunMoveRate;
    private bool sunMoving;
    // Along the authored sun's day (midsummer, AuthoredLatitude: SponzaLoop.LightletCensus.cs's BandNorth), from where it
    // stands: the hour angle (degrees) and its way, back and forth between morning and evening at the band's least
    // elevation. A turn about the zenith at its pitch, as at first, swung the sun round Crytek Sponza's whole sky.
    private float sunMoveHour = float.NaN, sunMoveWay = 1f, sunMoveNorth;
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
        if (sunMoveRate == 0f) sunMoveRate = 0.5f;
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
        if (float.IsNaN(sunMoveHour)) sunMoveNorth = BandNorth(out sunMoveHour);
        // The rate is the sun's own arc a second: on a day circle of declination d an hour angle's degree is cos d of one.
        var span = DayHalfSpan(Obliquity);
        sunMoveHour += sunMoveWay * sunMoveRate * dt / MathF.Cos(Obliquity * MathF.PI / 180f);
        if (MathF.Abs(sunMoveHour) > span) { sunMoveHour = MathF.CopySign(span, sunMoveHour); sunMoveWay = -sunMoveWay; }
        // Through yaw and pitch, as the Sun controls hold it (SponzaLoop.SunSweep.cs); the yaw within the slider's range.
        var travel = -ToScene(SunLocal(AuthoredLatitude, Obliquity, sunMoveHour), sunMoveNorth);
        sunPitch = MathF.Asin(Math.Clamp(travel.Y, -1f, 1f));
        sunYaw = MathF.Atan2(travel.X, -travel.Z);
        UpdateSunDirection();
        // The push steers the GI's sun in small steps once a gather has landed (SponzaLoop.FlipPush.cs); else the GI
        // catches up when its regather lands.
        if (!FlipPushStep() && !LightChangeInFlight) giSunTarget = -Vector3.Normalize(sunDirection);
        if (!sunMoveLogClock.IsRunning || sunMoveLogClock.Elapsed.TotalSeconds >= 5)
        {
            sunMoveLogClock.Restart();
            var lag = MathF.Acos(Math.Clamp(Vector3.Dot(giSunTarget, -Vector3.Normalize(sunDirection)), -1f, 1f)) * 180f / MathF.PI;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] sun move: hour angle {sunMoveHour:0.0}, elevation {-sunPitch * 180f / MathF.PI:0.0}; the GI's sun {lag:0.00} deg behind (a change in flight {LightChangeInFlight}, stage {sunNodeStage})."));
        }
    }
}
