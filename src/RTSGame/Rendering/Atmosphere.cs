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
/// <summary>
/// What the camera does with this light, as opposed to what the light is.
/// </summary>
/// <remarks>
/// <b>Grading belongs to the season, not to a global slider.</b> Exposure, saturation and contrast were one
/// setting for the whole year, which meant the year's palette could shift the hues around and never change
/// how the frame was <em>shot</em> — and how a frame is shot is most of what makes a winter photograph
/// unmistakable. The sliders remain, as multipliers over whatever the season asked for.
/// <para>
/// <c>FoliageChroma</c> is the odd one out: it is a multiplier on how chromatic green surfaces are allowed
/// to be, applied in the world shader rather than at present time, because it is aimed at one thing — the
/// green intensity of ground and canopy under a high sun — and pulling the whole frame's saturation to fix
/// it would take the colour out of the roofs and the earth as well.
/// </para>
/// </remarks>
internal readonly record struct SkyGrade(
    float Exposure,
    float Saturation,
    float Contrast,
    float FoliageChroma);

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
    float HourOfDay,
    /// <summary>How much air there is, as a multiplier on whatever the fog dial asks for.</summary>
    /// <remarks>
    /// <b>Three things multiplied, because mist is three things at once.</b> The season holds the base —
    /// winter air is cold and wet and still, a summer afternoon is the clearest of the year. Night adds to
    /// it, since air cools and what is in it condenses. And the morning adds most, because that is the end
    /// of a whole night of cooling and the sun has not yet burned any of it off: mist is a thing you find
    /// at dawn and lose by ten.
    /// <para>
    /// Multiplied rather than added so the extremes compound the way they do in the world — the foggiest
    /// hour of the year is a winter dawn by a wide margin, and it falls out of the arithmetic rather than
    /// being written down anywhere.
    /// </para>
    /// </remarks>
    float Haze,
    /// <summary>How far into the night it is: 0 in full daylight, 1 once the sun is well down.</summary>
    /// <remarks>
    /// The same ramp everything else in this file hangs off, exposed because the settlement's own lights
    /// need it — a window is lit because it is dark, and "is it dark" is a question about the sun's height
    /// rather than about the clock. Twilight therefore brings the windows up gradually and over the same
    /// seconds the palette is turning blue, which is the only way the two can agree.
    /// </remarks>
    float Nightness,
    SkyGrade Grade,
    string Description)
{
    /// <summary>
    /// How much of the frame's saturation a high sun takes away.
    /// </summary>
    /// <remarks>
    /// <b>Bright light is not more colourful light.</b> Reported as midday reading over-saturated, and it
    /// is: a strong sun raises every albedo in the scene toward the top of the curve, film desaturates as it
    /// rolls off, and the eye's own adaptation to a bright field pulls chroma down too — so a rendered noon
    /// that keeps the same saturation as a rendered dusk looks painted. Applied against the sun's height
    /// rather than the clock, so it takes the most out of a summer noon and almost nothing out of a winter
    /// one, which reinforces the season instead of averaging it.
    /// </remarks>
    internal static float MiddaySaturationDrop = 0.14f;

    /// <summary>How much of green's chroma a high sun takes away, on top of the frame's.</summary>
    /// <remarks>
    /// The specific complaint, and it deserves its own dial because green is not just another hue here: the
    /// canopy and the ground are most of the screen, so their saturation <em>is</em> the frame's mood. Real
    /// grass at noon goes toward straw and olive, not toward emerald.
    /// </remarks>
    internal static float MiddayGreenDrop = 0.22f;

    /// <summary>
    /// How long a sun cycle takes, in simulated seconds. The calendar's day, and not separately settable.
    /// </summary>
    /// <remarks>
    /// <b>Welded, and the slider that used to be here was the problem rather than the escape from it.</b>
    /// A day is one sunrise to the next; a sun on its own period makes the sky disagree with the date, and
    /// that was tried — at seven simulated minutes there were thirteen sun cycles in a year of ninety, a
    /// daily rhythm and an annual one at comparable timescales, and it was reported from the chair as
    /// impossible to tell apart. Syncing them fixed that and left a slider offering "a longer, cosier day at
    /// the cost of the calendar agreeing with the sky", which is an invitation to fix the symptom by
    /// lying.
    /// <para>
    /// So there is no dial here. How long a sunrise lasts is set where it is actually decided — by the
    /// length of the year, divided by a day count that is structure. That trade is real and
    /// <c>--clocks</c> reports it rather than offering a way around it.
    /// </para></remarks>
    internal static float DayLengthSeconds => WorldCalendar.DaySeconds;

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
    /// <remarks>
    /// <b><c>NoonElevation</c> is gone, and it had been dead for six commits.</b> It was authored for all
    /// five palettes, blended every frame and read by nothing — the sun's height comes from declination and
    /// latitude now, which is why winter's noon is low whether or not a palette says so. A palette field
    /// that looks like a decision and changes nothing is worse than a missing one.
    /// </remarks>
    private readonly record struct SeasonLook(
        Vector3 SunColor,
        float SunStrength,
        // What the sun goes to as it nears the horizon, and what the air toward it glows. Per season,
        // because a single copper for the whole year is what made winter noon look like an autumn sunset.
        Vector3 LowSunColor,
        Vector3 LowGlow,
        Vector3 SkyZenith,
        Vector3 SkyHorizon,
        Vector3 SkyAmbient,
        Vector3 GroundAmbient,
        float AmbientStrength,
        Vector3 HazeAway,
        Vector3 HazeToward,
        // The season's night: a tint over the shared night palette and a multiplier on its strength. A
        // winter night is colder and — because there is snow and no leaves — brighter than a summer one.
        Vector3 NightTint,
        float NightScale,
        // How much air this season has in it, before the time of day has its say. Cold, wet, still seasons
        // hold mist; a hot summer afternoon has the clearest air of the year.
        float HazeAmount,
        // What the camera does with this light, as distinct from what the light is. Exposure, saturation
        // and contrast are the season's own, so the grade is part of the palette rather than a global.
        float Exposure,
        float Saturation,
        float Contrast);

    // Spring: a high clean light and a lot of air in it. Summer: the sun overhead, deep sky, little haze.
    // Harvest: lower, golden, dusty — the season this whole game is about. Winter: barely clears the trees,
    // cold and pale, and the light itself is the thing that tells you the year is running out.
    private static readonly SeasonLook Spring = new(
        SunColor: new Vector3(1.00f, 0.96f, 0.88f),
        SunStrength: 2.9f,
        // A spring low sun is clean and pale gold: the air is washed rather than dusty.
        LowSunColor: new Vector3(1.00f, 0.80f, 0.58f),
        LowGlow: new Vector3(1.00f, 0.80f, 0.56f),
        SkyZenith: new Vector3(0.20f, 0.44f, 0.84f),
        SkyHorizon: new Vector3(0.70f, 0.84f, 0.95f),
        SkyAmbient: new Vector3(0.38f, 0.48f, 0.60f),
        GroundAmbient: new Vector3(0.22f, 0.24f, 0.16f),
        AmbientStrength: 1.15f,
        HazeAway: new Vector3(0.64f, 0.76f, 0.88f),
        HazeToward: new Vector3(0.92f, 0.90f, 0.80f),
        NightTint: new Vector3(0.98f, 1.00f, 1.04f),
        NightScale: 1.00f,
        // Wet ground and cold nights: the mistiest season bar winter.
        HazeAmount: 1.10f,
        Exposure: 1.00f,
        Saturation: 1.00f,
        Contrast: 1.00f);

    private static readonly SeasonLook Summer = new(
        SunColor: new Vector3(1.00f, 0.97f, 0.86f),
        SunStrength: 3.4f,
        // Hot amber, and the shortest golden hour of the year because the sun drops steeply.
        LowSunColor: new Vector3(1.00f, 0.70f, 0.42f),
        LowGlow: new Vector3(1.00f, 0.68f, 0.38f),
        SkyZenith: new Vector3(0.12f, 0.34f, 0.82f),
        SkyHorizon: new Vector3(0.62f, 0.80f, 0.96f),
        SkyAmbient: new Vector3(0.34f, 0.46f, 0.62f),
        GroundAmbient: new Vector3(0.26f, 0.24f, 0.14f),
        AmbientStrength: 1.10f,
        HazeAway: new Vector3(0.60f, 0.74f, 0.90f),
        HazeToward: new Vector3(0.96f, 0.90f, 0.74f),
        // A summer night is warm, short and never quite dark at this latitude.
        NightTint: new Vector3(1.06f, 1.00f, 0.94f),
        NightScale: 1.10f,
        // The clearest air of the year, which is most of why a summer noon reads as far-seeing.
        HazeAmount: 0.80f,
        // Bright and hard, but not lurid: the midday chroma pull below takes the green down, and it takes
        // the most out of summer precisely because summer is the season that reaches a high sun.
        Exposure: 1.02f,
        Saturation: 1.02f,
        Contrast: 1.06f);

    private static readonly SeasonLook Harvest = new(
        SunColor: new Vector3(1.00f, 0.88f, 0.64f),
        SunStrength: 3.0f,
        // The coppery one, and the season this game is about. Deepest low sun of the year.
        LowSunColor: new Vector3(1.00f, 0.56f, 0.26f),
        LowGlow: new Vector3(1.00f, 0.54f, 0.24f),
        SkyZenith: new Vector3(0.22f, 0.40f, 0.70f),
        SkyHorizon: new Vector3(0.86f, 0.80f, 0.66f),
        SkyAmbient: new Vector3(0.40f, 0.42f, 0.46f),
        GroundAmbient: new Vector3(0.32f, 0.26f, 0.14f),
        AmbientStrength: 1.18f,
        HazeAway: new Vector3(0.80f, 0.76f, 0.66f),
        HazeToward: new Vector3(1.00f, 0.84f, 0.58f),
        NightTint: new Vector3(1.02f, 0.99f, 0.96f),
        NightScale: 0.96f,
        // Mist over cut fields in the morning, and dust in the afternoon.
        HazeAmount: 1.05f,
        Exposure: 0.98f,
        Saturation: 1.08f,
        Contrast: 1.02f);

    private static readonly SeasonLook Winter = new(
        SunColor: new Vector3(0.92f, 0.94f, 1.00f),
        SunStrength: 2.1f,
        // <b>Pale rose, not copper — and this is the change that makes winter read as winter.</b> The low
        // sun colour was one fixed orange for the whole year, and the ramp that reaches it is a function of
        // the sun's height alone. At fifty north the midwinter sun never clears seventeen degrees, so the
        // ramp was pinned all day and winter noon was being painted as an autumn sunset: the one season
        // that should be unmistakable was wearing another season's light. A winter sun near the horizon is
        // weak and pink-white, because the light is thin rather than dusty.
        LowSunColor: new Vector3(0.98f, 0.85f, 0.80f),
        LowGlow: new Vector3(0.95f, 0.87f, 0.88f),
        SkyZenith: new Vector3(0.30f, 0.44f, 0.66f),
        SkyHorizon: new Vector3(0.80f, 0.85f, 0.92f),
        SkyAmbient: new Vector3(0.46f, 0.52f, 0.62f),
        GroundAmbient: new Vector3(0.24f, 0.26f, 0.28f),
        AmbientStrength: 1.30f,
        HazeAway: new Vector3(0.82f, 0.87f, 0.94f),
        HazeToward: new Vector3(0.92f, 0.93f, 0.96f),
        // Colder and brighter: bare ground, no canopy, and a clear sky that carries starlight.
        NightTint: new Vector3(0.90f, 0.96f, 1.12f),
        NightScale: 1.18f,
        // The season that changes how far you can see: cold, wet and still.
        HazeAmount: 1.50f,
        // Drained and flat, which is the strongest single seasonal signal the grade has. A winter frame
        // should look like it was shot on a duller day, not like a summer frame with blue in it.
        Exposure: 0.94f,
        Saturation: 0.80f,
        Contrast: 0.93f);

    /// <summary>
    /// Night, which is a convention rather than a measurement.
    /// </summary>
    /// <remarks>
    /// <b>Reported as pitch black and entirely invisible, which is what physical night is and not what a
    /// playable one is.</b> A real moonlit field is about a hundred-thousandth of a sunlit one; a night
    /// somebody is expected to keep playing through is perhaps a tenth of one, blue, with the shapes
    /// legible and the colour drained out of them. That is the convention every game of this kind uses and
    /// it is not a compromise — moonlight <em>is</em> what your eyes have adapted to, so a night that looks
    /// like this is closer to the experience than a correct one would be.
    /// <para>
    /// Ambient carries most of it, because at night there is no direction worth speaking of — which is why
    /// a night scene reads flat unless the ambient is doing the work. But not all of it: the moon is a real
    /// directional light and without it every roof and every wall shades identically and the settlement
    /// goes to silhouette. Weak, blue, and enough to keep an edge on things.
    /// </para>
    /// </remarks>
    private static readonly SeasonLook Nightfall = new(
        // <b>Colder and a third darker than the first version, because the settlement's own light arrived
        // and changed what night is for.</b> When nothing in the scene was warm, a night had to carry the
        // whole frame on its own and the safe choice was to keep it bright enough to read comfortably. Now
        // there are hearths in it, and the contrast between a cool wash and a few warm doorways <em>is</em>
        // the picture — so the wash goes down and further into the blue, and what you look at is the
        // village. The floor on legibility is the moon, which is unchanged in kind: it still throws a real
        // direction, so roofs and walls keep their shape rather than going to silhouette.
        SunColor: new Vector3(0.58f, 0.70f, 1.00f),
        SunStrength: 0.52f,
        // The low ramp is zero once the sun is down, so these only matter through the twilight blend.
        LowSunColor: new Vector3(0.58f, 0.70f, 1.00f),
        LowGlow: new Vector3(0.13f, 0.15f, 0.24f),
        SkyZenith: new Vector3(0.030f, 0.045f, 0.115f),
        SkyHorizon: new Vector3(0.085f, 0.110f, 0.215f),
        SkyAmbient: new Vector3(0.19f, 0.25f, 0.46f),
        GroundAmbient: new Vector3(0.085f, 0.100f, 0.165f),
        AmbientStrength: 0.86f,
        HazeAway: new Vector3(0.065f, 0.085f, 0.170f),
        HazeToward: new Vector3(0.115f, 0.130f, 0.215f),
        // Night does not tint itself — the season does that to it, in Tinted below.
        NightTint: Vector3.One,
        NightScale: 1.00f,
        // Night's own contribution is applied separately, against the sun's height.
        HazeAmount: 1.00f,
        // More exposure, because a dim scene wants to sit where the curve still has slope — that is what
        // keeps a darker night legible rather than merely dark, and it is a different control from how much
        // light is in the scene. Contrast above one now, which it was not: the point of a night with warm
        // points in it is the separation between them and the wash, and contrast is the dial that is
        // actually about separation. Chroma still comes down only a little, since the Purkinje shift in the
        // present pass is already draining colour out of the dark parts and two drains make a grey night.
        Exposure: 1.26f,
        Saturation: 0.90f,
        Contrast: 1.04f);

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

        // <b>The day of the year from the calendar, not from a fraction of it.</b> The date already knows
        // which day it is, and the calendar's days map onto 365 real ones for the declination formula's sake
        // — which is a scaling, not an approximation, since the formula only cares where in the cycle it is,
        // and it is therefore indifferent to §112 having changed how many days a year holds.
        //
        // <b>Plus an offset, because the two years did not start in the same place.</b> Found by printing
        // the profile rather than by looking at it: the calendar's year begins at spring and the
        // declination formula's begins on the first of January, and nothing had ever reconciled them — so
        // the light ran about six weeks early all year and the season called spring was being lit as
        // February, at a noon sun of 25° against harvest's 50°. A spring dimmer than an autumn is not a
        // subtle error and no amount of watching had caught it.
        //
        // Thirty days is not a taste: it is the offset that lands all four season middles on their solar
        // counterparts at once, which is as close as an unequal four-season year can get to a symmetric
        // solar one. Summer's middle falls on the June solstice, harvest's on the September equinox,
        // winter's within a day of the December solstice, and spring's in the second week of March.
        const float solarDayOffset = 30f;
        var dayOfYear =
            (date.Day / (float)WorldCalendar.DaysPerYear * 365f + solarDayOffset) % 365f;
        // Phase 0 is midnight. Fed simulated seconds rather than wall time, so the sun keeps step with the
        // calendar at any time compression — at three times speed the days were passing three times faster
        // than the sun, which is its own reason the two rhythms would not read.
        var hour = (float)(totalSeconds / MathF.Max(1f, DayLengthSeconds) % 1.0) * 24f;

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
        // Wider than civil twilight on purpose. Six degrees is what the atlas says and it is about a
        // second of a twenty-second day, which is not a transition anybody can watch — twelve either side
        // gives dawn and dusk a couple of seconds each, which at this pace is the difference between a
        // change and a cut.
        var above = Smoothstep(-12f, 13f, elevation);
        // How near the horizon it is while up, which is what makes light golden — and it stays golden all
        // afternoon in winter because in winter the sun never gets far from the horizon.
        var low = above * (1f - Smoothstep(4f, 26f, elevation));

        // <b>How far up the sun is when it is properly up</b>, which is the other half of the low ramp and
        // the thing the midday pull-backs hang off. Season-independent by construction, because it is
        // geometry: a summer noon reaches 63 degrees and a winter one 17, so this is near one in July and
        // stays near zero all winter without a palette having to say so.
        var high = above * Smoothstep(26f, 56f, elevation);

        // <b>The season's own air, taken before the night blend can overwrite it.</b> Caught by the profile
        // printing a winter night as hazier than a summer one by less than a winter noon was: everything
        // else in this palette is a colour and night is entitled to replace those, but how much water is in
        // the air is a fact about the month rather than about the hour. Blending it toward a shared night
        // value threw away the strongest half of the signal.
        var seasonalAir = look.HazeAmount;

        // Night is the same palette with the moon in it, faded in by how far the sun is down — and wearing
        // this season's cold. See Tinted.
        look = Mix(look, Tinted(Nightfall, look.NightTint, look.NightScale), 1f - above);

        // <b>Two directions blended, not one direction switched — and the switch was the choppiness.</b>
        // The moon is roughly opposite the sun, so a branch at elevation zero swung the light through a
        // hundred and eighty degrees in a single frame: every shadow in the settlement pivoted end to end
        // at sunset and again at dawn. Smoothing the palette could never have hidden that, because it was
        // the geometry jumping and not the colour.
        //
        // Both are unit vectors, so a lerp and a normalise walks the short way round between them and the
        // light swings across the sky over the whole of twilight instead of in one frame.
        var sunward = Skyward(azimuth, MathF.Max(6f, elevation));
        var moonward = Skyward(azimuth + MathF.PI, MathF.Max(14f, 34f + elevation * 0.5f));
        var direction = Vector3.Normalize(Vector3.Lerp(moonward, sunward, above));

        // Toward the season's own low sun rather than toward one copper for the whole year.
        var sunColor = Vector3.Lerp(look.SunColor, look.LowSunColor, low * 0.7f);
        // <b>Two terms, because one multiplied by "how far up the sun is" goes to nothing at night.</b> That
        // was the bug behind the pitch-black report: the whole directional contribution was scaled by the
        // sun's height, so below the horizon the scene had ambient and nothing else, and a scene lit only by
        // ambient has no shape in it at all. The moon is its own light and it does not care where the sun is.
        var daySun = look.SunStrength * above * (1f - low * 0.45f);
        var moon = Nightfall.SunStrength * (1f - above);
        var strength = daySun + moon;

        // <b>Haze is the sky seen edge-on, so it is the sky's own colour rather than two more constants.</b>
        // Which is both physically the case — aerial perspective tends toward the sky's radiance — and the
        // fix for the reported milkiness: pale constants under a bright sun turned the distance into a
        // white wall no matter what the sky was doing.
        var hazeAway = look.SkyHorizon * 0.62f + look.SkyZenith * 0.18f;
        var hazeToward = Vector3.Lerp(look.SkyHorizon * 0.85f, look.LowGlow, low * 0.55f);

        // <b>The grade the season asks for, with midday pulled back inside it.</b> Both pull-backs are
        // scaled by the sun's height, which is what makes time of day a smaller oscillation than the season
        // rather than a competing one: the amount of the pull is itself a seasonal quantity, since only
        // summer ever gets high enough to receive much of it.
        // <b>Mist: the season, the night, and the morning.</b> The morning term is a window on the clock
        // rather than on the sun's height, because "before the sun has burned it off" is what it means and
        // that is a time of day; it is then gated by darkness so it cannot put fog into a bright winter
        // noon, which at this latitude is only a few degrees above the horizon anyway.
        var dark = 1f - above;
        var morning = Window(hour, 5.5f, 5.5f);
        var haze = seasonalAir * (1f + 0.40f * dark) * (1f + 0.45f * morning * MathF.Max(dark, 0.35f));

        var grade = new SkyGrade(
            look.Exposure,
            look.Saturation * (1f - MiddaySaturationDrop * high),
            look.Contrast,
            1f - MiddayGreenDrop * high);

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
            hour,
            haze,
            1f - above,
            grade,
            Describe(elevation, low));
    }

    /// <summary>A unit vector from a bearing and an elevation, both in the convention the shaders use.</summary>
    private static Vector3 Skyward(float azimuth, float elevationDegrees)
    {
        var radians = elevationDegrees * MathF.PI / 180f;
        var horizontal = MathF.Cos(radians);
        return Vector3.Normalize(new Vector3(
            MathF.Sin(azimuth) * horizontal,
            MathF.Sin(radians),
            MathF.Cos(azimuth) * horizontal));
    }

    /// <summary>A soft window round an hour of the day, zero outside it and one at its middle.</summary>
    private static float Window(float hour, float centre, float halfWidth)
    {
        var distance = MathF.Abs(hour - centre) / MathF.Max(0.001f, halfWidth);
        return distance >= 1f ? 0f : 1f - distance * distance;
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
        Vector3.Lerp(a.SunColor, b.SunColor, t),
        float.Lerp(a.SunStrength, b.SunStrength, t),
        Vector3.Lerp(a.LowSunColor, b.LowSunColor, t),
        Vector3.Lerp(a.LowGlow, b.LowGlow, t),
        Vector3.Lerp(a.SkyZenith, b.SkyZenith, t),
        Vector3.Lerp(a.SkyHorizon, b.SkyHorizon, t),
        Vector3.Lerp(a.SkyAmbient, b.SkyAmbient, t),
        Vector3.Lerp(a.GroundAmbient, b.GroundAmbient, t),
        float.Lerp(a.AmbientStrength, b.AmbientStrength, t),
        Vector3.Lerp(a.HazeAway, b.HazeAway, t),
        Vector3.Lerp(a.HazeToward, b.HazeToward, t),
        Vector3.Lerp(a.NightTint, b.NightTint, t),
        float.Lerp(a.NightScale, b.NightScale, t),
        float.Lerp(a.HazeAmount, b.HazeAmount, t),
        float.Lerp(a.Exposure, b.Exposure, t),
        float.Lerp(a.Saturation, b.Saturation, t),
        float.Lerp(a.Contrast, b.Contrast, t));

    /// <summary>The shared night palette wearing one season's colour.</summary>
    /// <remarks>
    /// A tint on the colours and a scale on the two strengths, rather than four more authored night
    /// palettes. Night is the same convention whatever the month — a legible tenth of daylight, blue, with
    /// the colour drained out of it — and what changes with the season is how cold it is and how much of it
    /// there is. Two numbers say that; twenty would say it less clearly and drift out of step.
    /// </remarks>
    private static SeasonLook Tinted(SeasonLook night, Vector3 tint, float scale) => night with
    {
        SunColor = night.SunColor * tint,
        SunStrength = night.SunStrength * scale,
        LowSunColor = night.LowSunColor * tint,
        LowGlow = night.LowGlow * tint,
        SkyZenith = night.SkyZenith * tint,
        SkyHorizon = night.SkyHorizon * tint,
        SkyAmbient = night.SkyAmbient * tint,
        GroundAmbient = night.GroundAmbient * tint,
        AmbientStrength = night.AmbientStrength * scale,
        HazeAway = night.HazeAway * tint,
        HazeToward = night.HazeToward * tint,
    };
}
