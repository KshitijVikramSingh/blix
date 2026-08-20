using System.Numerics;
using RTSGame.Simulation.Economy;

namespace RTSGame.Rendering;

/// <summary>
/// What the light is doing: where the sun is, what colour everything is, and how much air there is.
/// </summary>
/// <remarks>
/// <b>The scene was lit by one fixed sun and painted with hardcoded colours, so a year looked like one
/// afternoon.</b> Which is a strange thing for this game to do: the season is the most important state in
/// its economy — a field's three windows, a winter's fuel, a harvest that either arrives or does not — and
/// the only thing that ever said what month it was, was a line of text. A settlement whose *light* tells
/// you it is late in the year needs no line of text.
/// <para>
/// Everything here is derived from two numbers: how far through the year the date is, and how far through
/// the day the sun is. The first comes from the calendar and is therefore the truth; the second does not,
/// and that is the one honest compromise in this file — see <see cref="DayLengthSeconds"/>.
/// </para>
/// </remarks>
internal readonly record struct Atmosphere(
    Vector3 SunDirection,
    Vector3 SunColor,
    float SunIntensity,
    Vector3 SkyAmbient,
    Vector3 GroundAmbient,
    float AmbientScale,
    Vector3 SkyZenith,
    Vector3 SkyHorizon,
    Vector3 HazeAway,
    Vector3 HazeToward,
    float SunElevationDegrees,
    string Description)
{
    /// <summary>
    /// How long a sun cycle takes, in simulated seconds.
    /// </summary>
    /// <remarks>
    /// <b>Not the calendar's day, and that is a decision rather than an oversight.</b>
    /// <c>WorldCalendar.DaySeconds</c> is twenty seconds, because a day is the unit a <em>ration</em> is
    /// measured in — one villager eats one unit a day, and that is what makes the year's arithmetic work.
    /// A sun going round every twenty seconds is a strobe, not a time of day.
    /// <para>
    /// So the light has its own period. The dissonance is real and worth naming: the panel will say day 245
    /// while the sun has been round a dozen times. The alternative is either a strobing sky or an economy
    /// whose ration is five minutes long, and of the three prices this is the one nobody looking at the
    /// screen will notice.
    /// </para>
    /// </remarks>
    internal static float DayLengthSeconds = 420f;

    /// <summary>How far through the year, 0 at the start of spring and 1 at the end of winter.</summary>
    /// <remarks>
    /// Seasons are not equal lengths — 1200, 1800, 1000, 1400 seconds — so this walks the actual lengths
    /// rather than treating each as a quarter. A year that reads as four equal quarters would put midsummer
    /// light in the middle of a season that is a third of the year long.
    /// </remarks>
    internal static float YearProgress(CalendarDate date)
    {
        var before = 0f;
        for (var i = 0; i < (int)date.Season; i++) before += WorldCalendar.LengthOf((Season)i);
        var within = WorldCalendar.LengthOf(date.Season) is var length && length > 0.01f
            ? date.SecondsIntoSeason / length
            : 0f;
        return (before + within * WorldCalendar.LengthOf(date.Season)) / WorldCalendar.YearSeconds;
    }

    /// <summary>
    /// The four corners of the year, and everything between them is a blend.
    /// </summary>
    /// <remarks>
    /// Anchored at the <em>middle</em> of each season rather than its start, because that is where a season
    /// is most itself: the blend then carries you through an autumn that is half harvest and half winter,
    /// which is what late autumn is.
    /// </remarks>
    private readonly record struct SeasonLook(
        float NoonElevation,
        Vector3 SunColor,
        float SunStrength,
        Vector3 SkyZenith,
        Vector3 SkyHorizon,
        Vector3 SkyAmbient,
        Vector3 GroundAmbient,
        float AmbientStrength,
        Vector3 HazeAway,
        Vector3 HazeToward);

    // Spring: a high clean light and a lot of air in it. Summer: the sun overhead, deep sky, little haze.
    // Harvest: lower, golden, dusty — the season this whole game is about. Winter: barely clears the trees,
    // cold and pale, and the light itself is the thing that tells you the year is running out.
    private static readonly SeasonLook Spring = new(
        NoonElevation: 44f,
        SunColor: new Vector3(1.00f, 0.96f, 0.88f),
        SunStrength: 2.9f,
        SkyZenith: new Vector3(0.20f, 0.44f, 0.84f),
        SkyHorizon: new Vector3(0.70f, 0.84f, 0.95f),
        SkyAmbient: new Vector3(0.38f, 0.48f, 0.60f),
        GroundAmbient: new Vector3(0.22f, 0.24f, 0.16f),
        AmbientStrength: 0.90f,
        HazeAway: new Vector3(0.64f, 0.76f, 0.88f),
        HazeToward: new Vector3(0.92f, 0.90f, 0.80f));

    private static readonly SeasonLook Summer = new(
        NoonElevation: 58f,
        SunColor: new Vector3(1.00f, 0.97f, 0.86f),
        SunStrength: 3.4f,
        SkyZenith: new Vector3(0.12f, 0.34f, 0.82f),
        SkyHorizon: new Vector3(0.62f, 0.80f, 0.96f),
        SkyAmbient: new Vector3(0.34f, 0.46f, 0.62f),
        GroundAmbient: new Vector3(0.26f, 0.24f, 0.14f),
        AmbientStrength: 0.85f,
        HazeAway: new Vector3(0.60f, 0.74f, 0.90f),
        HazeToward: new Vector3(0.96f, 0.90f, 0.74f));

    private static readonly SeasonLook Harvest = new(
        NoonElevation: 34f,
        SunColor: new Vector3(1.00f, 0.88f, 0.64f),
        SunStrength: 3.0f,
        SkyZenith: new Vector3(0.22f, 0.40f, 0.70f),
        SkyHorizon: new Vector3(0.86f, 0.80f, 0.66f),
        SkyAmbient: new Vector3(0.40f, 0.42f, 0.46f),
        GroundAmbient: new Vector3(0.32f, 0.26f, 0.14f),
        AmbientStrength: 0.92f,
        HazeAway: new Vector3(0.80f, 0.76f, 0.66f),
        HazeToward: new Vector3(1.00f, 0.84f, 0.58f));

    private static readonly SeasonLook Winter = new(
        NoonElevation: 19f,
        SunColor: new Vector3(0.92f, 0.94f, 1.00f),
        SunStrength: 2.1f,
        SkyZenith: new Vector3(0.30f, 0.44f, 0.66f),
        SkyHorizon: new Vector3(0.80f, 0.85f, 0.92f),
        SkyAmbient: new Vector3(0.46f, 0.52f, 0.62f),
        GroundAmbient: new Vector3(0.24f, 0.26f, 0.28f),
        AmbientStrength: 1.05f,
        HazeAway: new Vector3(0.82f, 0.87f, 0.94f),
        HazeToward: new Vector3(0.92f, 0.93f, 0.96f));

    /// <summary>Night, which is not a season but behaves like one for a couple of hours.</summary>
    /// <remarks>
    /// Moonlight is sunlight twice reflected, so it is the same colour arriving from the same place and
    /// merely far weaker and much bluer. Ambient carries almost all of it: at night there is no direction
    /// worth speaking of, which is exactly why a night scene reads as flat unless the ambient is doing the
    /// work.
    /// </remarks>
    private static readonly SeasonLook Nightfall = new(
        NoonElevation: 0f,
        SunColor: new Vector3(0.62f, 0.72f, 1.00f),
        SunStrength: 0.28f,
        SkyZenith: new Vector3(0.020f, 0.035f, 0.085f),
        SkyHorizon: new Vector3(0.075f, 0.095f, 0.16f),
        SkyAmbient: new Vector3(0.12f, 0.16f, 0.26f),
        GroundAmbient: new Vector3(0.05f, 0.06f, 0.09f),
        AmbientStrength: 0.55f,
        HazeAway: new Vector3(0.10f, 0.13f, 0.22f),
        HazeToward: new Vector3(0.18f, 0.19f, 0.26f));

    /// <summary>Where the settlement is, in degrees north, which is what decides the sun's habits.</summary>
    /// <remarks>
    /// Fifty is temperate northern Europe, which is the climate the whole economy assumes — one harvest a
    /// year, a winter you have to have stored for. It is not a dial because it is not a look: change it and
    /// the year's shape changes, which is a decision about what kind of place this is.
    /// </remarks>
    private const float LatitudeDegrees = 50f;

    /// <summary>
    /// Works out the light from where the sun actually is.
    /// </summary>
    /// <remarks>
    /// <b>The first version swept the sun's bearing through half a turn and called it a day, and it was
    /// reported as not rotating like that — correctly.</b> The real thing is three lines of spherical
    /// trigonometry and gives far more than it costs: declination from the day of the year, hour angle from
    /// the time, and elevation and azimuth from those and a latitude.
    /// <para>
    /// What falls out of it for free is the part worth having. At fifty degrees north the midwinter sun
    /// reaches <b>16.6°</b> and is up for <b>7.9 hours</b>; midsummer reaches <b>63.4°</b> and is up for
    /// <b>16.1</b>. So winter is short and dark and the sun crawls along the horizon, and none of that had
    /// to be authored — it is what the geometry says. A hand-tuned curve could imitate the elevation and
    /// would not have thought of the day length.
    /// </para>
    /// <para>
    /// And it fixes the choppiness, which was structural rather than a tuning problem. The old version had a
    /// day branch and a night branch with a blend stitched across the seam. Everything here is a continuous
    /// function of the sun's elevation, which crosses the horizon smoothly by itself — so dusk, nightfall
    /// and dawn are the same expression evaluated at different heights, and there is no seam to stitch.
    /// </para>
    /// </remarks>
    internal static Atmosphere For(
        CalendarDate date,
        double totalSeconds,
        float bearingDegrees,
        float seasonality)
    {
        var year = YearProgress(date);
        var look = Blend(year, Math.Clamp(seasonality, 0f, 1f));

        // Day of the year, and the hour of the day from the sun's own cycle. Phase 0 is midnight, so a
        // session that starts at zero starts at dawn a quarter of a cycle later rather than in the dark.
        var dayOfYear = year * 365f;
        var hour = (float)(totalSeconds / MathF.Max(30f, DayLengthSeconds) % 1.0) * 24f;

        var latitude = LatitudeDegrees * MathF.PI / 180f;
        // Declination: how far the sun's own circle is tilted this time of year. The 284 and the 365 are
        // the standard approximation and are accurate to a fraction of a degree, which is far finer than
        // anything downstream of it cares about.
        var declination = 23.44f * MathF.PI / 180f *
                          MathF.Sin(2f * MathF.PI * (284f + dayOfYear) / 365f);
        // Hour angle: fifteen degrees an hour, zero at noon.
        var hourAngle = (hour - 12f) * 15f * MathF.PI / 180f;

        var sinElevation =
            MathF.Sin(latitude) * MathF.Sin(declination) +
            MathF.Cos(latitude) * MathF.Cos(declination) * MathF.Cos(hourAngle);
        var elevationRadians = MathF.Asin(Math.Clamp(sinElevation, -1f, 1f));
        var elevation = elevationRadians * 180f / MathF.PI;

        // Azimuth from north, eastward, in the atan2 form so the quadrants look after themselves.
        var azimuth = MathF.Atan2(
            -MathF.Cos(declination) * MathF.Sin(hourAngle),
            MathF.Sin(declination) * MathF.Cos(latitude) -
            MathF.Cos(declination) * MathF.Sin(latitude) * MathF.Cos(hourAngle));
        // Plus whatever the map calls north, so a settlement can be turned without moving the sun's habits.
        azimuth += bearingDegrees * MathF.PI / 180f;

        // <b>One continuous ramp, and everything hangs off it.</b> Civil twilight is about six degrees, so
        // the sun's contribution fades in over the band either side of the horizon rather than switching on.
        var above = Smoothstep(-6f, 7f, elevation);
        // How near the horizon it is while up, which is what makes light golden — and it stays golden all
        // afternoon in winter because in winter the sun never gets far from the horizon.
        var low = above * (1f - Smoothstep(4f, 26f, elevation));

        // Night is the same palette with the moon in it, faded in by how far the sun is down.
        look = Mix(look, Nightfall, 1f - above);

        // Below the horizon the light comes from the moon, which is opposite the sun and much lower — and
        // it must never be at zero elevation or the shadow box reaches for infinity.
        var lightElevation = MathF.Max(6f, elevation);
        var lightAzimuth = azimuth;
        if (elevation < 0f)
        {
            lightAzimuth += MathF.PI;
            lightElevation = MathF.Max(12f, -elevation * 0.6f);
        }

        var lightRadians = lightElevation * MathF.PI / 180f;
        var horizontal = MathF.Cos(lightRadians);
        var direction = Vector3.Normalize(new Vector3(
            MathF.Sin(lightAzimuth) * horizontal,
            MathF.Sin(lightRadians),
            MathF.Cos(lightAzimuth) * horizontal));

        var sunColor = Vector3.Lerp(look.SunColor, new Vector3(1.00f, 0.62f, 0.34f), low * 0.7f);
        var strength = look.SunStrength * above * (1f - low * 0.45f);

        // <b>Haze is the sky seen edge-on, so it is the sky's own colour rather than two more constants.</b>
        // Which is both physically the case — aerial perspective tends toward the sky's radiance — and the
        // fix for the reported milkiness: pale constants under a bright sun turned the distance into a
        // white wall no matter what the sky was doing.
        var hazeAway = look.SkyHorizon * 0.62f + look.SkyZenith * 0.18f;
        var hazeToward = Vector3.Lerp(
            look.SkyHorizon * 0.85f, new Vector3(1.00f, 0.66f, 0.36f), low * 0.55f);

        return new Atmosphere(
            direction,
            sunColor,
            strength,
            look.SkyAmbient,
            look.GroundAmbient,
            look.AmbientStrength,
            look.SkyZenith,
            look.SkyHorizon,
            hazeAway,
            hazeToward,
            elevation,
            Describe(elevation, low));
    }

    private static float Smoothstep(float from, float to, float at)
    {
        var t = Math.Clamp((at - from) / MathF.Max(0.0001f, to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>What to call the light, from the sun's height alone.</summary>
    private static string Describe(float elevation, float low) => elevation switch
    {
        < -12f => "night",
        < -2f => "twilight",
        < 6f => "the horizon",
        _ => low > 0.55f ? "low sun" : elevation > 40f ? "high sun" : "daylight",
    };

    private static SeasonLook Blend(float yearProgress, float seasonality)
    {
        // Anchors at the middle of each season, wrapped, so the blend runs continuously through the year
        // and December leans back into spring rather than falling off the end of the table.
        var anchors = new[] { Spring, Summer, Harvest, Winter };
        var lengths = new float[4];
        var mids = new float[4];
        var running = 0f;
        for (var i = 0; i < 4; i++)
        {
            lengths[i] = WorldCalendar.LengthOf((Season)i) / WorldCalendar.YearSeconds;
            mids[i] = running + lengths[i] * 0.5f;
            running += lengths[i];
        }

        var from = 3;
        for (var i = 0; i < 4; i++)
        {
            if (yearProgress >= mids[i]) from = i;
        }

        var to = (from + 1) % 4;
        var span = mids[to] - mids[from];
        if (span <= 0f) span += 1f;
        var at = yearProgress - mids[from];
        if (at < 0f) at += 1f;
        var blended = Mix(anchors[from], anchors[to], Math.Clamp(at / span, 0f, 1f));

        // Seasonality dials the whole thing back toward the middle of the year rather than toward one
        // season, so turning it down flattens the year without picking a favourite month.
        var neutral = Mix(Mix(Spring, Summer, 0.5f), Mix(Harvest, Winter, 0.5f), 0.5f);
        return Mix(neutral, blended, seasonality);
    }

    private static SeasonLook Mix(SeasonLook a, SeasonLook b, float t) => new(
        float.Lerp(a.NoonElevation, b.NoonElevation, t),
        Vector3.Lerp(a.SunColor, b.SunColor, t),
        float.Lerp(a.SunStrength, b.SunStrength, t),
        Vector3.Lerp(a.SkyZenith, b.SkyZenith, t),
        Vector3.Lerp(a.SkyHorizon, b.SkyHorizon, t),
        Vector3.Lerp(a.SkyAmbient, b.SkyAmbient, t),
        Vector3.Lerp(a.GroundAmbient, b.GroundAmbient, t),
        float.Lerp(a.AmbientStrength, b.AmbientStrength, t),
        Vector3.Lerp(a.HazeAway, b.HazeAway, t),
        Vector3.Lerp(a.HazeToward, b.HazeToward, t));
}
