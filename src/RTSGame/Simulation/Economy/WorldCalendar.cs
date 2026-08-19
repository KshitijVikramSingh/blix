namespace RTSGame.Simulation.Economy;

/// <summary>The four seasons, which are canonical. Everything else about the year derives.</summary>
/// <remarks>
/// The prototype had three disagreeing crop windows — one following the season names, one following a
/// crop model under which nothing was harvested during the season called Harvest, and a comment
/// following a third — and they drifted because nothing forced them to agree. So the seasons are the
/// only thing written down, and every window, rate and swing is a function of them.
/// </remarks>
internal enum Season
{
    /// <summary>Sowing. The labour crunch, and no harvest.</summary>
    Spring,

    /// <summary>Free labour: build, or cut wood so you do not freeze.</summary>
    Summer,

    /// <summary>The spike, and the other labour crunch.</summary>
    Harvest,

    /// <summary>The drain. No food production, heating burns wood, and the raids come.</summary>
    Winter,
}

/// <summary>Where in the year a tick falls.</summary>
/// <param name="Year">Years completed since the world began.</param>
/// <param name="Season">Which season this is.</param>
/// <param name="Day">Day unit within the year, 0 to 269.</param>
/// <param name="SecondsIntoSeason">How far into this season, in sim seconds.</param>
internal readonly record struct CalendarDate(int Year, Season Season, int Day, float SecondsIntoSeason)
{
    public override string ToString() => $"year {Year + 1}, {Season}, day {Day}";
}

/// <summary>
/// The year, derived from the tick number rather than stored.
/// </summary>
/// <remarks>
/// Not state, deliberately. A calendar that counted its own seconds would be a second opinion about
/// what time it is, and the one thing this project has learned twice about time is that two counters
/// for one quantity drift. The tick number is already saved, already fingerprinted, and already the
/// simulation's definition of when — so the date is a function of it and there is nothing to keep in
/// step.
/// <para>
/// The numbers are §3's table and are not independent: a year is 5,400 sim seconds, which at the
/// default 1.5x compression is an hour in the chair, and the four season lengths sum to it exactly.
/// A day unit is 20 s and 270 of them make the year, so the day is derived from the year rather than
/// the year from the day.
/// </para>
/// <para>
/// <b>Succession will want an epoch.</b> §5 has a career ending and the next beginning on the same
/// map, and the natural reading of "year 1" is the ruler's first rather than the world's. When that
/// lands it is one saved offset here, not a second clock.
/// </para>
/// </remarks>
internal static class WorldCalendar
{
    /// <summary>Sim seconds in a year. §3: an hour of wall clock at the default compression.</summary>
    public const float YearSeconds = 5400f;

    /// <summary>Sim seconds in a day unit. 270 of them make a year.</summary>
    public const float DaySeconds = 20f;

    /// <summary>Day units in a year, derived so it cannot disagree with the two above.</summary>
    public const int DaysPerYear = (int)(YearSeconds / DaySeconds);

    /// <summary>Length of each season in sim seconds, in season order. They sum to a year.</summary>
    private static readonly float[] SeasonSeconds = { 1200f, 1800f, 1000f, 1400f };

    /// <summary>Sim seconds from the start of the year to the start of each season.</summary>
    private static readonly float[] SeasonStart = BuildSeasonStarts();

    /// <summary>How long this season lasts, in sim seconds.</summary>
    public static float LengthOf(Season season) => SeasonSeconds[(int)season];

    /// <summary>Where in the year a given tick falls.</summary>
    public static CalendarDate At(long tickNumber) => At(tickNumber / 30f);

    /// <summary>Where in the year a given sim second falls.</summary>
    public static CalendarDate At(float seconds)
    {
        var year = (int)MathF.Floor(seconds / YearSeconds);
        var intoYear = seconds - year * YearSeconds;
        var season = Season.Winter;
        for (var candidate = 0; candidate < SeasonSeconds.Length; candidate++)
        {
            if (intoYear >= SeasonStart[candidate] + SeasonSeconds[candidate]) continue;
            season = (Season)candidate;
            break;
        }

        return new CalendarDate(
            year,
            season,
            (int)MathF.Floor(intoYear / DaySeconds),
            intoYear - SeasonStart[(int)season]);
    }

    /// <summary>Share of a year this season occupies, for turning an annual rate into a seasonal one.</summary>
    public static float YearShareOf(Season season) => LengthOf(season) / YearSeconds;

    private static float[] BuildSeasonStarts()
    {
        var starts = new float[SeasonSeconds.Length];
        for (var season = 1; season < SeasonSeconds.Length; season++)
        {
            starts[season] = starts[season - 1] + SeasonSeconds[season - 1];
        }

        var total = starts[^1] + SeasonSeconds[^1];
        if (MathF.Abs(total - YearSeconds) > 0.001f)
        {
            throw new InvalidOperationException(
                $"The seasons sum to {total} s and the year is {YearSeconds} s. One of them is wrong, " +
                "and the seasons are the canonical ones.");
        }

        return starts;
    }
}
