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
/// <param name="Day">Day within the year, 0 to <see cref="WorldCalendar.DaysPerYear"/> minus one.</param>
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
/// The numbers are not independent: the four season lengths sum to the year exactly and the day divides
/// it exactly, so the day is derived from the year rather than the year from the day.
/// </para>
/// <para>
/// <b>§112 tried to retime this and stopped, and what it left behind is the shape rather than the
/// numbers.</b> The day was thirteen wall seconds, which is a whole sunrise inside one ordinary command;
/// the obvious answer is a longer day, and the arithmetic refuses it — a day a player can inhabit is tens
/// of seconds, so a year still holding a believable number of days is hours in the chair. Fewer days makes
/// the count arbitrary, and a calendar whose day count means nothing is worse than a brisk one.
/// </para>
/// <para>
/// So the numbers here are §3's, and the change is that they are now <em>knobs with nothing written down
/// downstream of them</em>. The year is the dial; the day divides it; the seasons are shares of it. Every
/// rate the economy has divides by the year, every crop window and construction cost is a share of its
/// season, and the foraging reaches are physical and do not move at all. Which of these is a design
/// decision and which is a session setting is a question about who turns it, not about how it is written.
/// </para>
/// <para>
/// Everything the economy is balanced in is expressed per year, so tripling the year's <em>duration</em>
/// leaves its <em>content</em> alone: every rate divides by this constant and slows uniformly. The two
/// places written in absolute seconds — the crop windows and the construction costs — were already
/// documented as shares of their season, and are now written that way so they cannot drift out of step
/// with a retiming again.
/// </para>
/// <para>
/// <b>Succession will want an epoch.</b> §5 has a career ending and the next beginning on the same
/// map, and the natural reading of "year 1" is the ruler's first rather than the world's. When that
/// lands it is one saved offset here, not a second clock.
/// </para>
/// </remarks>
internal static class WorldCalendar
{
    /// <summary>
    /// Sim seconds in a year. The one dial the whole calendar hangs off.
    /// </summary>
    /// <remarks>
    /// §3's hour of wall clock at the default compression. <b>The single number to argue about</b>, because
    /// the day count and the season shares below are structure rather than settings: move this and the day,
    /// the seasons, every economic rate and the length of a sunrise all move together, in the proportions
    /// they are supposed to keep.
    /// <para>
    /// Not <c>const</c>, and that is the point. A constant is a number the compiler folds into every caller
    /// and nobody can turn; this is a knob, and the self-tests turn it to prove that nothing downstream has
    /// written its consequences down.
    /// </para></remarks>
    public static float YearSeconds = 5400f;

    /// <summary>
    /// Days in a year. A design knob, and a legibility one.
    /// </summary>
    /// <remarks>
    /// <b>Structure, not a setting, and welded here so it cannot drift.</b> A day only earns the name if the
    /// count of them reads as a year — two hundred and seventy against a real three hundred and sixty-five
    /// does, and sixty is a number with no referent. It used to be <c>Year / Day</c>, which made the day
    /// primary and let the count fall out; §112 turned the derivation round so the count is fixed and the
    /// day divides the year, which is why it could previously drift to sixty without anything objecting.
    /// </remarks>
    public static int DaysPerYear = 270;

    /// <summary>Sim seconds in a day, and one turn of the sun. Derived, never set.</summary>
    /// <remarks>
    /// <b>A day is one sunrise to the next, so this is also the solar cycle.</b> Keeping them separate would
    /// let the sky disagree with the date, which <c>Atmosphere</c> records as having been tried: with the sun
    /// on its own period there were thirteen of its cycles in a year of ninety, and a daily rhythm and an
    /// annual one at comparable timescales were reported from the chair as impossible to tell apart.
    /// <para>
    /// Being derived is what makes the trade honest. With the day count fixed, <em>how long a sunrise lasts
    /// and how long a year lasts are one decision</em> — there is no dial here to relieve that, and there
    /// should not be. See <c>--clocks</c>, which reports the curve.
    /// </para></remarks>
    public static float DaySeconds => YearSeconds / MathF.Max(1, DaysPerYear);

    /// <summary>
    /// Share of the year each season takes, in season order. A design knob; they sum to one.
    /// </summary>
    /// <remarks>
    /// <b>Shares rather than seconds, because seconds are a consequence of the year and these are not.</b>
    /// §3's 1200/1800/1000/1400 out of 5,400, said the way that survives the year moving. Summer is the
    /// longest because it is the season with nothing forcing anybody's hand, and harvest the shortest
    /// because a deadline that is not short is not a deadline.
    /// </remarks>
    private static readonly float[] SeasonShares =
        { 1200f / 5400f, 1800f / 5400f, 1000f / 5400f, 1400f / 5400f };

    /// <summary>Share of the year elapsed before each season starts. Constant, like the shares it sums.</summary>
    private static readonly float[] SeasonStartShare = BuildSeasonStartShares();

    /// <summary>Sim seconds from the start of the year to the start of a season.</summary>
    public static float StartOf(Season season) => SeasonStartShare[(int)season] * YearSeconds;

    /// <summary>How long this season lasts, in sim seconds.</summary>
    public static float LengthOf(Season season) => SeasonShares[(int)season] * YearSeconds;

    /// <summary>Where in the year a given tick falls.</summary>
    public static CalendarDate At(long tickNumber) => At(tickNumber / 30f);

    /// <summary>Where in the year a given sim second falls.</summary>
    public static CalendarDate At(float seconds)
    {
        var year = (int)MathF.Floor(seconds / YearSeconds);
        var intoYear = seconds - year * YearSeconds;
        // Compared as shares of the year rather than as seconds, so the walk does not depend on the year's
        // length at all — the same three comparisons whatever the dial is set to.
        var intoYearShare = intoYear / YearSeconds;
        var season = Season.Winter;
        for (var candidate = 0; candidate < SeasonShares.Length; candidate++)
        {
            if (intoYearShare >= SeasonStartShare[candidate] + SeasonShares[candidate]) continue;
            season = (Season)candidate;
            break;
        }

        return new CalendarDate(
            year,
            season,
            (int)MathF.Floor(intoYear / DaySeconds),
            intoYear - StartOf(season));
    }

    /// <summary>Share of a year this season occupies, for turning an annual rate into a seasonal one.</summary>
    public static float YearShareOf(Season season) => LengthOf(season) / YearSeconds;

    /// <summary>
    /// Cumulative shares, and the check that the seasons are a whole year.
    /// </summary>
    /// <remarks>
    /// The check survived the change of units and is worth more in them: it used to compare a sum of seconds
    /// against the year, which is a statement about one setting of the dial. Against one it is a statement
    /// about the shares themselves, and therefore about every setting.
    /// </remarks>
    private static float[] BuildSeasonStartShares()
    {
        var starts = new float[SeasonShares.Length];
        for (var season = 1; season < SeasonShares.Length; season++)
        {
            starts[season] = starts[season - 1] + SeasonShares[season - 1];
        }

        var total = starts[^1] + SeasonShares[^1];
        if (MathF.Abs(total - 1f) > 0.0001f)
        {
            throw new InvalidOperationException(
                $"The season shares sum to {total} rather than to one whole year. The shares are the " +
                "canonical thing and one of them is wrong.");
        }

        return starts;
    }
}
