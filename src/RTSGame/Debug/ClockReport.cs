using System.Globalization;
using RTSGame.Rendering;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Economy;

namespace RTSGame.Debug;

/// <summary>
/// What the clocks currently come to, in the units they are actually experienced in.
/// </summary>
/// <remarks>
/// <b>Written because §112 spent an afternoon arguing about numbers nobody could see.</b> The calendar's
/// constants are simulated seconds; what a player meets is wall seconds, and between the two sits a
/// compression the calendar does not know about. So every question that mattered — how long is a sunrise,
/// does an order span a state change, can a unit cross the map in a day — was being answered by hand, in a
/// scratch script, from a model reimplemented beside the one the game runs. One of those hand answers was
/// invented outright and steered the whole pass.
/// <para>
/// This samples <see cref="Atmosphere.For"/> rather than reimplementing the trigonometry, which is the
/// point: a report that computes its own answer is a second opinion, and the one thing this project has
/// learned repeatedly is that two opinions about one quantity drift. If the renderer's sun changes, this
/// changes with it or it is wrong in a way somebody will notice.
/// </para>
/// <para>
/// It reports and it does not decide. The dimensionless ratios are at the top because they are what a
/// setting is actually chosen on — a day count that reads as a year, a lighting state longer than an order
/// — and the curve at the bottom is there because the welds in §112 make dawn length and year length one
/// decision, and a trade that cannot be dodged should at least be legible.
/// </para></remarks>
internal static class ClockReport
{
    /// <summary>Distances an ordinary command covers, for asking what one costs in wall seconds.</summary>
    private static readonly float[] CommandMetres = { 30f, 80f, 120f };

    /// <summary>Alternative years to price the trade at, as multiples of whatever is set.</summary>
    private static readonly float[] CurveScales = { 0.5f, 1f, 2f, 3.4f, 4.5f, 6.75f };

    public static int Run(float compression, float extentMetres, float? yearOverride, int? daysOverride)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        var year = WorldCalendar.YearSeconds;
        var days = WorldCalendar.DaysPerYear;
        try
        {
            if (yearOverride is { } y) WorldCalendar.YearSeconds = y;
            if (daysOverride is { } d) WorldCalendar.DaysPerYear = d;
            Report(compression, extentMetres);
        }
        finally
        {
            WorldCalendar.YearSeconds = year;
            WorldCalendar.DaysPerYear = days;
        }

        return 0;
    }

    private static void Report(float compression, float extentMetres)
    {
        var speed = AgentDefaults.MaximumSpeed;
        var yearSim = WorldCalendar.YearSeconds;
        var daySim = WorldCalendar.DaySeconds;
        var yearWall = yearSim / compression;
        var dayWall = daySim / compression;

        Console.WriteLine("RTSGame clocks");
        Console.WriteLine();
        Console.WriteLine("  dials");
        Console.WriteLine($"    session  year {yearSim:N0} sim s | compression {compression:F2} sim s per wall s" +
                          $" | map {extentMetres:N0} m");
        Console.WriteLine($"    design   {WorldCalendar.DaysPerYear} days a year | seasons " +
                          string.Join(
                              " / ",
                              Enum.GetValues<Season>().Select(s =>
                                  $"{WorldCalendar.YearShareOf(s) * 100f:F0}%")) +
                          $" | body {speed:F2} m/s | walking {Woodland.WalkSecondsPerYear:N0} sim s a year");
        Console.WriteLine();

        // <b>The ratios first, because they are what a setting is chosen on.</b> Every one of them is
        // dimensionless, so none of them cares which unit anybody was thinking in — which is exactly the
        // confusion that made this file necessary.
        var dawnWall = StateSeconds(Season.Spring, dayWall, "twilight") +
                       StateSeconds(Season.Spring, dayWall, "the horizon");
        var commandWall = 80f / speed / compression;
        var crossingWall = extentMetres / speed / compression;
        Console.WriteLine("  ratios — what a setting is chosen on");
        Console.WriteLine(
            $"    {WorldCalendar.DaysPerYear,6} days a year        " +
            (WorldCalendar.DaysPerYear >= 200 ? "reads as a year" : "READS AS A NUMBER, not a year"));
        Console.WriteLine(
            $"    {dawnWall / commandWall,6:F2} dawn per command   " +
            (dawnWall < commandWall ? "AN ORDER OUTLASTS THE SUNRISE" : "a sunrise outlasts an order"));
        // Days per crossing, not crossings per day. It was written the second way and printed 16.76 beside
        // "crossings a day" — a number that is right and a name that is its own reciprocal, which is the
        // fault this whole file exists to stop making.
        Console.WriteLine(
            $"    {crossingWall / dayWall,6:F2} days to cross     " +
            (crossingWall > dayWall ? "THE MAP CANNOT BE CROSSED IN A DAY" : "the map crosses inside a day"));
        Console.WriteLine(
            $"    {Woodland.ReachMetres / extentMetres,6:F3} reach per map      " +
            $"a cutter's arm is {Woodland.ReachMetres / extentMetres * 100f:F1}% of the map");
        Console.WriteLine();

        Console.WriteLine("  experienced, in wall time");
        Console.WriteLine($"    a year      {Wall(yearWall)}");
        foreach (var season in Enum.GetValues<Season>())
        {
            Console.WriteLine(
                $"    {season,-11} {Wall(WorldCalendar.LengthOf(season) / compression)}" +
                $"   ({WorldCalendar.LengthOf(season) / daySim:F0} days)");
        }

        Console.WriteLine($"    a day       {Wall(dayWall)}   (and one turn of the sun)");
        foreach (var metres in CommandMetres)
        {
            Console.WriteLine($"    a {metres,3:F0} m order {Wall(metres / speed / compression)}");
        }

        Console.WriteLine($"    the map     {Wall(crossingWall)}   corner to corner at {speed:F2} m/s");
        Console.WriteLine();

        // <b>Sampled through the renderer, not modelled beside it.</b> The bands are the ones Atmosphere
        // names, so if the sky's own idea of twilight moves, so does this.
        Console.WriteLine("  the light, in wall seconds, by season");
        Console.WriteLine($"    {"",-11}{"night",9}{"twilight",10}{"horizon",9}{"sunlit",9}");
        foreach (var season in Enum.GetValues<Season>())
        {
            Console.WriteLine(
                $"    {season,-11}" +
                $"{StateSeconds(season, dayWall, "night"),8:F1}s" +
                $"{StateSeconds(season, dayWall, "twilight"),9:F1}s" +
                $"{StateSeconds(season, dayWall, "the horizon"),8:F1}s" +
                $"{Sunlit(season, dayWall),8:F1}s");
        }

        Console.WriteLine();
        // <b>Solved rather than eyeballed, because the answer is the interesting part.</b> A dawn is a fixed
        // share of a day and an order is a fixed number of wall seconds, so "how long a year makes a sunrise
        // outlast an order" has one answer and it is worth printing next to the criterion it comes from —
        // particularly when it comes out at a length nobody would choose, which is a fact about the
        // criterion rather than about the calendar.
        var dawnShare = dawnWall / dayWall;
        var yearForDawn = commandWall / MathF.Max(1e-6f, dawnShare) * WorldCalendar.DaysPerYear;
        Console.WriteLine("  solved");
        Console.WriteLine(
            $"    a sunrise outlasts an 80 m order at a year of {Wall(yearForDawn)} — " +
            $"{yearForDawn / yearWall:F1}x this one");
        Console.WriteLine(
            $"    a sunrise is a tenth of one at {Wall(yearForDawn * 0.1f)}, a third at " +
            $"{Wall(yearForDawn / 3f)}");

        Console.WriteLine();
        Console.WriteLine("  the trade the calendar cannot dodge");
        Console.WriteLine("    a day is one turn of the sun and the day count is structure, so how long a");
        Console.WriteLine("    sunrise lasts and how long a year lasts are one decision:");
        Console.WriteLine();
        Console.WriteLine($"      {"year (wall)",-14}{"day",8}{"spring dawn",13}{"winter night",14}");
        var saved = WorldCalendar.YearSeconds;
        try
        {
            foreach (var scale in CurveScales)
            {
                WorldCalendar.YearSeconds = saved * scale;
                var d = WorldCalendar.DaySeconds / compression;
                var dawn = StateSeconds(Season.Spring, d, "twilight") +
                           StateSeconds(Season.Spring, d, "the horizon");
                var here = MathF.Abs(scale - 1f) < 0.001f ? "  <- here" : string.Empty;
                Console.WriteLine(
                    $"      {Wall(WorldCalendar.YearSeconds / compression),-14}{d,7:F0}s{dawn,12:F0}s" +
                    $"{StateSeconds(Season.Winter, d, "night"),13:F0}s{here}");
            }
        }
        finally
        {
            WorldCalendar.YearSeconds = saved;
        }
    }

    /// <summary>
    /// Wall seconds a lighting state lasts on a day in the middle of a season.
    /// </summary>
    /// <remarks>
    /// Walked at a fixed number of samples rather than a fixed step, so the cost of asking does not depend
    /// on how long a day happens to be — the answer is a share of the cycle, and the share is what is being
    /// measured.
    /// </remarks>
    private static float StateSeconds(Season season, float dayWallSeconds, string state)
    {
        const int samples = 2000;
        var midSeason = WorldCalendar.StartOf(season) + WorldCalendar.LengthOf(season) * 0.5f;
        var dayStart = MathF.Floor(midSeason / WorldCalendar.DaySeconds) * WorldCalendar.DaySeconds;
        var hits = 0;
        for (var i = 0; i < samples; i++)
        {
            var seconds = dayStart + (i + 0.5f) / samples * WorldCalendar.DaySeconds;
            var sky = Atmosphere.For(WorldCalendar.At(seconds), seconds, 0f, 1f);
            if (sky.Description == state) hits++;
        }

        return hits / (float)samples * dayWallSeconds;
    }

    /// <summary>Everything the sun is up for, which Atmosphere splits into three names.</summary>
    private static float Sunlit(Season season, float dayWallSeconds) =>
        dayWallSeconds
        - StateSeconds(season, dayWallSeconds, "night")
        - StateSeconds(season, dayWallSeconds, "twilight")
        - StateSeconds(season, dayWallSeconds, "the horizon");

    /// <summary>Wall seconds as something readable, with the unit always said.</summary>
    private static string Wall(float seconds) => seconds switch
    {
        < 90f => $"{seconds,6:F1} s",
        < 5400f => $"{seconds / 60f,6:F1} min",
        _ => $"{seconds / 3600f,6:F2} h",
    };
}
