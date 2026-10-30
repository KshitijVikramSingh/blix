using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Blix.Core;

namespace Blix.Demos.VulkanSponza;

// A sun that keeps moving (--sun-move DEG_PER_S; M toggles it, 0.5 deg/s when no rate was given): the frame's sun -- its
// direct light, the RT shadows, and RTGI, which sees the turn as any other change of the light -- turns every frame
// along the authored sun's day (StepSunMove, from the frame).
internal sealed partial class SponzaLoop
{
    private float sunMoveRate;
    private bool sunMoving;
    // Along the authored sun's day (midsummer, AuthoredLatitude: BandNorth), from where it stands: the hour angle
    // (degrees) and its way, back and forth between morning and evening at the day's least elevation. A turn about the
    // zenith at its pitch, as at first, swung the sun round Crytek Sponza's whole sky.
    private float sunMoveHour = float.NaN, sunMoveWay = 1f, sunMoveNorth;
    private readonly Stopwatch sunMoveClock = new();
    private readonly Stopwatch sunMoveLogClock = new();
    // The day kept above --sun-move-min-elevation (40 deg): down to the horizon, Crytek Sponza went too dark to judge.
    private float sunMoveMinElevation = 40f;

    // The day the sun moves along: Sponza at 35-45 deg N. North is set so the authored sun is a midsummer morning sun at
    // 37 deg N (the authored 74.5 deg elevation is reachable only south of ~39 deg); the day's span (DayHalfSpan) is
    // bounded at that latitude too, down to BandMinElevation. It was taken at 40 deg N while the sun was placed at 37:
    // the span the move clamped to and the day the sun travelled were two different days.
    private const float AuthoredLatitude = 37f, Obliquity = 23.44f, BandMinElevation = 5f;

    private void ParseSunMove(AppArgs args)
    {
        if (args.Float("sun-move") is { } rate && rate != 0f)
        {
            sunMoveRate = rate;
            sunMoving = true;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] sun move: {rate:0.##} deg/s (--sun-move)."));
        }
        if (args.Float("sun-move-min-elevation") is { } me) sunMoveMinElevation = Math.Clamp(me, 0f, 80f);
    }

    private static Vector3 SunLocal(float latitudeDeg, float declinationDeg, float hourDeg)
    {
        float r(float d) => d * MathF.PI / 180f;
        var (phi, dec, h) = (r(latitudeDeg), r(declinationDeg), r(hourDeg));
        var up = MathF.Sin(phi) * MathF.Sin(dec) + MathF.Cos(phi) * MathF.Cos(dec) * MathF.Cos(h);
        var east = -MathF.Cos(dec) * MathF.Sin(h);
        var north = MathF.Cos(phi) * MathF.Sin(dec) - MathF.Sin(phi) * MathF.Cos(dec) * MathF.Cos(h);
        return new Vector3(east, north, up);   // (east, north, up)
    }

    // The hour angle (degrees) at which a sun of this declination sets below the band's least elevation, at the authored
    // latitude.
    private static float DayHalfSpan(float decDeg)
    {
        float r(float d) => d * MathF.PI / 180f;
        var c = (MathF.Sin(r(BandMinElevation)) - MathF.Sin(r(AuthoredLatitude)) * MathF.Sin(r(decDeg))) / (MathF.Cos(r(AuthoredLatitude)) * MathF.Cos(r(decDeg)));
        return MathF.Acos(Math.Clamp(c, -1f, 1f)) * 180f / MathF.PI;
    }

    // Scene direction (y up) of a local (east, north, up), north at angle theta in the x-z plane.
    private static Vector3 ToScene(Vector3 enu, float theta) =>
        Vector3.Normalize(new Vector3(
            enu.Y * MathF.Cos(theta) - enu.X * MathF.Sin(theta),
            enu.Z,
            enu.X * MathF.Cos(theta) + enu.Y * MathF.Sin(theta)));

    // North from the authored sun: the hour angle that gives its elevation at midsummer, 37 deg N (morning), and north
    // turned so that sun's azimuth lands on the authored one.
    private float BandNorth(out float authoredHour)
    {
        var authored = -Vector3.Normalize(authoredSunDirection ?? sunDirection);
        var elev = MathF.Asin(Math.Clamp(authored.Y, -1f, 1f));
        float rad(float d) => d * MathF.PI / 180f;
        var cosH = (MathF.Sin(elev) - MathF.Sin(rad(AuthoredLatitude)) * MathF.Sin(rad(Obliquity)))
                   / (MathF.Cos(rad(AuthoredLatitude)) * MathF.Cos(rad(Obliquity)));
        authoredHour = -MathF.Acos(Math.Clamp(cosH, -1f, 1f)) * 180f / MathF.PI;
        var local = SunLocal(AuthoredLatitude, Obliquity, authoredHour);
        return MathF.Atan2(authored.Z, authored.X) - MathF.Atan2(local.X, local.Y);
    }

    // The hour angle (degrees) at which the moving sun's day (AuthoredLatitude, Obliquity) sinks to this elevation.
    private static float HourAtElevation(float elevationDeg)
    {
        float r(float d) => d * MathF.PI / 180f;
        var c = (MathF.Sin(r(elevationDeg)) - MathF.Sin(r(AuthoredLatitude)) * MathF.Sin(r(Obliquity))) / (MathF.Cos(r(AuthoredLatitude)) * MathF.Cos(r(Obliquity)));
        return MathF.Acos(Math.Clamp(c, -1f, 1f)) * 180f / MathF.PI;
    }

    private void ToggleSunMove()
    {
        sunMoving = !sunMoving;
        if (sunMoveRate == 0f) sunMoveRate = 0.5f;
        sunMoveClock.Restart();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] sun move: {(sunMoving ? $"on, {sunMoveRate:0.##} deg/s" : "off")}."));
    }

    // Each frame: the frame's sun turned by the frame's time.
    private void StepSunMove()
    {
        if (!sunMoving) return;
        var dt = sunMoveClock.IsRunning ? (float)sunMoveClock.Elapsed.TotalSeconds : 0f;
        sunMoveClock.Restart();
        if (float.IsNaN(sunMoveHour)) sunMoveNorth = BandNorth(out sunMoveHour);
        // The rate is the sun's own arc a second: on a day circle of declination d an hour angle's degree is cos d of one.
        var span = MathF.Min(DayHalfSpan(Obliquity), HourAtElevation(sunMoveMinElevation));
        sunMoveHour += sunMoveWay * sunMoveRate * dt / MathF.Cos(Obliquity * MathF.PI / 180f);
        if (MathF.Abs(sunMoveHour) > span) { sunMoveHour = MathF.CopySign(span, sunMoveHour); sunMoveWay = -sunMoveWay; }
        // Through yaw and pitch, as the Sun controls hold it; the yaw within the slider's range.
        var travel = -ToScene(SunLocal(AuthoredLatitude, Obliquity, sunMoveHour), sunMoveNorth);
        sunPitch = MathF.Asin(Math.Clamp(travel.Y, -1f, 1f));
        sunYaw = MathF.Atan2(travel.X, -travel.Z);
        UpdateSunDirection();
        if (!sunMoveLogClock.IsRunning || sunMoveLogClock.Elapsed.TotalSeconds >= 5)
        {
            sunMoveLogClock.Restart();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] sun move: hour angle {sunMoveHour:0.0}, elevation {-sunPitch * 180f / MathF.PI:0.0}."));
        }
    }
}
