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

    /// <summary>
    /// Works out the light for a date and a moment in the sun's cycle.
    /// </summary>
    /// <param name="date">The calendar, which decides the season and therefore the palette.</param>
    /// <param name="totalSeconds">Wall time, which drives the sun's own cycle.</param>
    /// <param name="bearingDegrees">Which way the sun rises from, so a map has a fixed east.</param>
    /// <param name="seasonality">How much the year is allowed to change the light, from none to all.</param>
    internal static Atmosphere For(
        CalendarDate date,
        double totalSeconds,
        float bearingDegrees,
        float seasonality)
    {
        var look = Blend(YearProgress(date), Math.Clamp(seasonality, 0f, 1f));

        // The sun's arc. Phase 0 is dawn, 0.5 is dusk, and the back half of the cycle is night — so the
        // day is half the cycle, which is roughly true of a temperate year on average and much simpler than
        // pretending to model latitude.
        var phase = (float)(totalSeconds / MathF.Max(30f, DayLengthSeconds) % 1.0);
        var daylight = phase < 0.5f;
        var acrossSky = daylight ? phase * 2f : (phase - 0.5f) * 2f;

        // A sine arc, so the sun spends most of its time high and hurries through the horizon — which is
        // what makes dawn and dusk brief and worth watching rather than half the cycle.
        var height = MathF.Sin(acrossSky * MathF.PI);
        var elevation = look.NoonElevation * height;

        // Dusk is not just a lower sun: it is a redder, weaker one, and the last of it is the best light in
        // the game. Anchored to how low the sun is rather than to the clock, so a winter afternoon is
        // permanently golden and a summer one only briefly.
        var low = 1f - MathF.Min(1f, height * 1.8f);
        var duskWarmth = low * low;

        if (!daylight)
        {
            // Night: the same maths with the moon standing in for the sun, and the palette swapped. Blended
            // in over the first and last of it so nightfall is a transition rather than a switch.
            var into = MathF.Min(1f, MathF.Min(acrossSky, 1f - acrossSky) * 6f);
            look = Mix(look, Nightfall, into);
            elevation = look.NoonElevation * height + Nightfall.NoonElevation;
            elevation = MathF.Max(8f, 30f * height);
            duskWarmth = 0f;
        }

        var bearing = bearingDegrees * MathF.PI / 180f;
        // Rises on the bearing and sets opposite it, which is what makes the shadows sweep across the
        // settlement over a cycle rather than merely lengthen and shorten in place.
        var swing = bearing + (daylight ? acrossSky : acrossSky + 1f) * MathF.PI;
        var elevationRadians = MathF.Max(2f, elevation) * MathF.PI / 180f;
        var horizontal = MathF.Cos(elevationRadians);
        var direction = Vector3.Normalize(new Vector3(
            MathF.Sin(swing) * horizontal,
            MathF.Sin(elevationRadians),
            MathF.Cos(swing) * horizontal));

        var sunColor = Vector3.Lerp(look.SunColor, new Vector3(1.00f, 0.66f, 0.38f), duskWarmth * 0.75f);
        var strength = look.SunStrength * (1f - duskWarmth * 0.55f);
        var hazeToward = Vector3.Lerp(look.HazeToward, new Vector3(1.00f, 0.62f, 0.34f), duskWarmth * 0.6f);

        return new Atmosphere(
            direction,
            sunColor,
            strength,
            look.SkyAmbient,
            look.GroundAmbient,
            look.AmbientStrength,
            look.SkyZenith,
            look.SkyHorizon,
            look.HazeAway,
            hazeToward,
            elevation,
            Describe(daylight, height, duskWarmth));
    }

    /// <summary>What to call the light, for the panel.</summary>
    private static string Describe(bool daylight, float height, float dusk) => !daylight
        ? height > 0.55f ? "night" : "small hours"
        : dusk > 0.62f
            ? height > 0.02f ? "first light" : "dusk"
            : height > 0.9f ? "midday" : dusk > 0.3f ? "low sun" : "afternoon";

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
