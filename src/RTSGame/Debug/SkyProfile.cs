using System.Numerics;
using RTSGame.Rendering;
using RTSGame.Simulation.Economy;

namespace RTSGame.Debug;

/// <summary>
/// What the light does over a year, printed rather than watched.
/// </summary>
/// <remarks>
/// <b>A palette is the one part of this scene nobody can judge from a screenshot.</b> Two frames of the
/// same settlement in two seasons look different and that is all a person can say; whether the difference
/// is <em>the season</em> or merely the time of day the shot was taken is a question about numbers. And it
/// mattered: the low-sun colour was one fixed copper for the whole year against a ramp driven by the sun's
/// height alone, so at fifty degrees north — where midwinter noon never clears seventeen degrees — winter
/// noon was being painted as an autumn sunset. Nobody spotted it by looking, for two sessions.
/// <para>
/// So this prints the sun's height, the light's warmth and the grade at every watch of the day in every
/// season, and then checks the property the whole seasonal palette exists to provide: <b>that the season
/// moves the light further than the hour does</b>. That is what "glance at it and know the month" means,
/// and it is a comparison of two spreads rather than an opinion about a frame.
/// </para>
/// </remarks>
internal static class SkyProfile
{
    /// <summary>Hours worth printing: first light, morning, noon, afternoon, dusk, and the small hours.</summary>
    private static readonly float[] Watches = { 5f, 8f, 12f, 16f, 19f, 22f };

    /// <summary>The hours that are unambiguously day, which is where the two spreads are compared.</summary>
    private static readonly float[] Daylight = { 9f, 11f, 12f, 13f, 15f };

    public static int Run(float bearingDegrees, float seasonality)
    {
        Console.WriteLine(
            $"RTSGame sky profile — {bearingDegrees:F0}° bearing, seasonality {seasonality:F2}, " +
            $"50°N, a {Atmosphere.DayLengthSeconds:F0} s day");
        Console.WriteLine(
            "  midday saturation drop " +
            $"{Atmosphere.MiddaySaturationDrop:F2}, midday green drop {Atmosphere.MiddayGreenDrop:F2}");
        Console.WriteLine();
        Console.WriteLine(
            "  season  |  hour |   sun |  what it is | warmth | strength | expose | satur | green |  air");

        var faults = new List<string>();
        foreach (var season in new[] { Season.Spring, Season.Summer, Season.Harvest, Season.Winter })
        {
            foreach (var hour in Watches)
            {
                var sky = At(season, hour, bearingDegrees, seasonality);
                Console.WriteLine(
                    $"  {season,-7} | {hour,4:F0}h | {sky.SunElevationDegrees,4:F0}° | " +
                    $"{sky.Description,-11} | {Warmth(sky.SunColor),6:F3} | " +
                    $"{sky.SunIntensity,8:F2} | {sky.Grade.Exposure,6:F2} | " +
                    $"{sky.Grade.Saturation,5:F2} | {sky.Grade.FoliageChroma,5:F2} | " +
                    $"{sky.Haze,4:F2}");
            }

            Console.WriteLine();
        }

        // <b>The bug this file was written for, as a check that cannot rot.</b> A winter noon must not be
        // warmer than a harvest dusk. It is the exact inversion that was shipping, and it is the kind of
        // thing that reads as merely "a bit orange" on a screen and as an obvious fault in one column.
        // Against the warmest light each season actually has, found by scanning rather than by naming an
        // hour — the first version compared noon with "seven in the evening", which the sun sets before in
        // three seasons out of four, so it was reading the night palette and calling it dusk.
        var winterNoon = At(Season.Winter, 12f, bearingDegrees, seasonality);
        var winterBest = Warmest(Season.Winter, bearingDegrees, seasonality);
        var harvestBest = Warmest(Season.Harvest, bearingDegrees, seasonality);
        Console.WriteLine(
            $"  the warmest light of the year: harvest {harvestBest:F3} against winter {winterBest:F3}, " +
            $"and winter's own noon {Warmth(winterNoon.SunColor):F3}");
        if (winterBest >= harvestBest)
        {
            faults.Add(
                $"winter's warmest light ({winterBest:F3}) is not cooler than harvest's " +
                $"({harvestBest:F3}) — the low-sun colour is not seasonal, so the one season that should " +
                "be unmistakable is wearing another season's light");
        }

        // <b>The geometry, which is not a look and must stay honest — and the first version of this check
        // was wrong about what it was measuring.</b> It compared the noon sun at the middle of winter
        // against the atlas figure for the winter solstice, which the middle of a season is not: the four
        // seasons here are 1200, 1800, 1000 and 1400 seconds long, so no season's midpoint is a solstice.
        // The claim worth checking is about the year — that somewhere in it the noon sun is as low as the
        // atlas says and somewhere as high — so it walks every day and takes the extremes.
        var lowest = 90f;
        var highest = -90f;
        for (var day = 0; day < WorldCalendar.DaysPerYear; day++)
        {
            var noon = NoonOn(day, bearingDegrees, seasonality);
            lowest = MathF.Min(lowest, noon.SunElevationDegrees);
            highest = MathF.Max(highest, noon.SunElevationDegrees);
        }

        var summerNoon = At(Season.Summer, 12f, bearingDegrees, seasonality);
        Console.WriteLine(
            $"  the noon sun runs from {lowest:F1}° to {highest:F1}° over the year, " +
            $"and the seasons see {At(Season.Spring, 12f, bearingDegrees, seasonality).SunElevationDegrees:F0}° / " +
            $"{summerNoon.SunElevationDegrees:F0}° / " +
            $"{At(Season.Harvest, 12f, bearingDegrees, seasonality).SunElevationDegrees:F0}° / " +
            $"{winterNoon.SunElevationDegrees:F0}° at their middles");
        if (lowest > 20f || highest < 60f)
        {
            faults.Add(
                $"the sun's own habits have moved: the noon sun runs {lowest:F1}° to {highest:F1}° over " +
                "the year, and fifty degrees north says about 17° to 63°");
        }

        // And the seasons have to sit the right way round on that arc, which is the check that would have
        // caught the six-week offset between the calendar's year and the sun's: a spring lit as February.
        if (summerNoon.SunElevationDegrees <= At(Season.Spring, 12f, bearingDegrees, seasonality).SunElevationDegrees ||
            At(Season.Spring, 12f, bearingDegrees, seasonality).SunElevationDegrees <=
            winterNoon.SunElevationDegrees)
        {
            faults.Add(
                "the seasons are in the wrong order on the sun's arc — spring's noon must sit between " +
                "winter's and summer's, and the usual cause is the calendar's year and the sun's year " +
                "not starting in the same place");
        }

        // <b>Midday is pulled back, and the pull is itself seasonal</b> — summer reaches a high sun and
        // winter never does. Measured on the green drop rather than on the frame's saturation, and the
        // first version got that wrong: it compared noon against the same season at seven in the evening,
        // which in winter is the middle of the night, so what it actually measured was the night palette.
        // The green drop is a pure function of the sun's height and nothing else, so it cannot be confused
        // with anything.
        var summerPull = 1f - summerNoon.Grade.FoliageChroma;
        var winterPull = 1f - winterNoon.Grade.FoliageChroma;
        Console.WriteLine(
            $"  a noon sun takes {summerPull:F3} of green's chroma in summer and {winterPull:F3} in " +
            $"winter, and the frame's saturation is {summerNoon.Grade.Saturation:F2} against " +
            $"{winterNoon.Grade.Saturation:F2}");
        if (summerPull <= winterPull + 0.05f)
        {
            faults.Add(
                $"the midday pull-back is not seasonal — summer takes {summerPull:F3} and winter " +
                $"{winterPull:F3}, so it is being applied by the clock rather than by the sun's height");
        }

        // <b>The foggiest hour of the year has to be a winter morning</b>, and it has to come out of the
        // arithmetic rather than being written down — the season holds the base, the night adds to it and
        // the morning adds most, all multiplied. If a summer afternoon ever wins this, the three terms have
        // stopped compounding.
        var thickest = 0f;
        var thickestWhen = string.Empty;
        var clearest = 99f;
        var clearestWhen = string.Empty;
        foreach (var season in new[] { Season.Spring, Season.Summer, Season.Harvest, Season.Winter })
        {
            for (var hour = 0f; hour < 24f; hour += 0.5f)
            {
                var air = At(season, hour, bearingDegrees, seasonality).Haze;
                if (air > thickest)
                {
                    thickest = air;
                    thickestWhen = $"{season} at {hour:F0}h";
                }

                if (air >= clearest) continue;
                clearest = air;
                clearestWhen = $"{season} at {hour:F0}h";
            }
        }

        Console.WriteLine(
            $"  the air runs from {clearest:F2} ({clearestWhen}) to {thickest:F2} ({thickestWhen})");
        if (!thickestWhen.StartsWith("Winter", StringComparison.Ordinal))
        {
            faults.Add(
                $"the foggiest hour of the year is {thickestWhen} — it should be a winter morning, and " +
                "if it is not then the season, the night and the morning have stopped compounding");
        }

        if (!clearestWhen.StartsWith("Summer", StringComparison.Ordinal))
        {
            faults.Add($"the clearest air of the year is {clearestWhen}, and it should be a summer day");
        }

        // <b>The property the whole seasonal palette is for.</b> Take the light at the same hour in four
        // seasons: that spread is what a season is worth. Take it at five daylight hours within one season:
        // that spread is what the hour is worth. The first has to be the larger, or "time of day is a small
        // oscillation inside a strong seasonal palette" is a wish rather than a description.
        var seasonal = Spread(Season.Spring, Season.Summer, Season.Harvest, Season.Winter, bearingDegrees, seasonality);
        var diurnal = 0f;
        foreach (var season in new[] { Season.Spring, Season.Summer, Season.Harvest, Season.Winter })
        {
            diurnal = MathF.Max(diurnal, DaySpread(season, bearingDegrees, seasonality));
        }

        Console.WriteLine(
            $"  the season moves the light {seasonal:F3}; the hour moves it at most {diurnal:F3} " +
            $"across the day — a ratio of {seasonal / MathF.Max(0.0001f, diurnal):F2}");
        if (seasonal <= diurnal)
        {
            faults.Add(
                $"the hour moves the light further than the season does ({diurnal:F3} against " +
                $"{seasonal:F3}) — a glance at the frame says what time it is rather than what month");
        }

        foreach (var fault in faults) Console.WriteLine($"  FAULT: {fault}");
        return faults.Count > 0 ? 1 : 0;
    }

    /// <summary>The light in a given season at a given hour, at the middle of that season.</summary>
    /// <remarks>
    /// The middle of the season rather than its start, because that is where a season is most itself — the
    /// same reason the palette anchors there. The hour is laid onto a whole day so the date and the sun's
    /// position come from one number and cannot disagree.
    /// </remarks>
    private static Atmosphere At(Season season, float hour, float bearingDegrees, float seasonality)
    {
        var start = 0f;
        for (var i = 0; i < (int)season; i++) start += WorldCalendar.LengthOf((Season)i);
        var middle = start + WorldCalendar.LengthOf(season) * 0.5f;
        // Onto a day boundary, then the hour as a fraction of the sun's own cycle.
        var day = MathF.Floor(middle / Atmosphere.DayLengthSeconds) * Atmosphere.DayLengthSeconds;
        var seconds = day + hour / 24f * Atmosphere.DayLengthSeconds;
        return Atmosphere.For(WorldCalendar.At(seconds), seconds, bearingDegrees, seasonality);
    }

    /// <summary>The warmest the sun gets in a season, over the whole of a day.</summary>
    private static float Warmest(Season season, float bearingDegrees, float seasonality)
    {
        var warmest = -10f;
        for (var hour = 0f; hour < 24f; hour += 0.25f)
        {
            var sky = At(season, hour, bearingDegrees, seasonality);
            // Daylight only. The moon is blue and would win a "coldest" contest in every season, so
            // including the night would make this a comparison of nights.
            if (sky.SunElevationDegrees < -2f) continue;
            warmest = MathF.Max(warmest, Warmth(sky.SunColor));
        }

        return warmest;
    }

    /// <summary>Noon on a given day of the year, for walking the whole arc.</summary>
    private static Atmosphere NoonOn(int day, float bearingDegrees, float seasonality)
    {
        var seconds = day * WorldCalendar.DaySeconds + Atmosphere.DayLengthSeconds * 0.5f;
        return Atmosphere.For(WorldCalendar.At(seconds), seconds, bearingDegrees, seasonality);
    }

    /// <summary>How far apart the light is at noon across the seasons given.</summary>
    private static float Spread(
        Season a, Season b, Season c, Season d, float bearingDegrees, float seasonality)
    {
        var looks = new[]
        {
            Features(At(a, 12f, bearingDegrees, seasonality)),
            Features(At(b, 12f, bearingDegrees, seasonality)),
            Features(At(c, 12f, bearingDegrees, seasonality)),
            Features(At(d, 12f, bearingDegrees, seasonality)),
        };
        return MeanDistance(looks);
    }

    /// <summary>How far apart the light is across the daylight hours of one season.</summary>
    private static float DaySpread(Season season, float bearingDegrees, float seasonality)
    {
        var looks = new float[Daylight.Length][];
        for (var i = 0; i < Daylight.Length; i++)
        {
            looks[i] = Features(At(season, Daylight[i], bearingDegrees, seasonality));
        }

        return MeanDistance(looks);
    }

    /// <summary>
    /// The light as a vector, so two of them can be subtracted.
    /// </summary>
    /// <remarks>
    /// Colours and grade, not intensities. Brightness is the one thing a viewer's eye adapts away — a dim
    /// scene and a bright one of the same hue read as the same weather — so including sun strength would
    /// let a season claim distinctness it has no visible right to.
    /// </remarks>
    private static float[] Features(Atmosphere sky) => new[]
    {
        sky.SunColor.X, sky.SunColor.Y, sky.SunColor.Z,
        sky.SkyZenith.X, sky.SkyZenith.Y, sky.SkyZenith.Z,
        sky.SkyHorizon.X, sky.SkyHorizon.Y, sky.SkyHorizon.Z,
        sky.SkyAmbient.X, sky.SkyAmbient.Y, sky.SkyAmbient.Z,
        sky.HazeAway.X, sky.HazeAway.Y, sky.HazeAway.Z,
        sky.HazeToward.X, sky.HazeToward.Y, sky.HazeToward.Z,
        sky.Grade.Saturation, sky.Grade.Contrast, sky.Grade.Exposure,
    };

    private static float MeanDistance(float[][] looks)
    {
        var total = 0f;
        var pairs = 0;
        for (var i = 0; i < looks.Length; i++)
        for (var j = i + 1; j < looks.Length; j++)
        {
            var sum = 0f;
            for (var k = 0; k < looks[i].Length; k++)
            {
                var difference = looks[i][k] - looks[j][k];
                sum += difference * difference;
            }

            total += MathF.Sqrt(sum);
            pairs++;
        }

        return pairs == 0 ? 0f : total / pairs;
    }

    private static float Warmth(Vector3 color) => color.X - color.Z;
}
