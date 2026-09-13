namespace RTSGame.Simulation.Economy;

/// <summary>
/// Where people come from, and where they go when a settlement cannot feed them.
/// </summary>
/// <remarks>
/// <b>Population is endogenous.</b> Nobody is built and nobody is trained: a villager appears at a house
/// that has room for them, inside the reach of a store that can feed them, when the settlement has enough
/// put by to see the extra mouth through a winter. That is the whole of it, and every term in it is
/// something the player built and can see.
/// <para>
/// Which makes housing the cap on population and food the brake on growth — §6's "grain → people, gated by
/// housing" — and it closes the loop the rest of the economy has been building toward. A surplus had no
/// purpose before this: stores climbed, autonomy climbed, and nothing happened. Now a surplus is people,
/// people are labour, and labour is the only thing that turns a field or a tree into anything.
/// </para>
/// <para>
/// <b>And a shortage has a consequence that is not a counter.</b> A household whose draw goes unmet
/// accumulates privation, and privation spends itself as <em>emigration</em> — somebody leaves. That is the
/// ordinary carrying-capacity correction, it is reversible, and it is legible: a house outside every
/// catchment empties itself while the ones inside do not, so the mistake is visible on the map rather than
/// in the shortfall column. Death is deliberately absent; it belongs with Stage E, where there is something
/// to die of.
/// </para>
/// </remarks>
internal static class Population
{
    /// <summary>
    /// Seconds of eligibility one new villager costs a household.
    /// </summary>
    /// <remarks>
    /// Half a year of good conditions per free place, and this one is <b>chosen rather than derived</b> —
    /// it is the pace of the game rather than a fact about anything. A real pre-industrial population grows
    /// under one per cent a year, which is invisible over a career; and a year per place, measured, came
    /// out at under two births a year on a settlement of twenty-six, which is an hour of play for one
    /// person at the default time compression.
    /// <para>
    /// Per <em>house with room</em> rather than per capita, which matters: growth is proportional to
    /// housing the player has built, not to population, so it does not compound on its own and there is no
    /// invented damping curve. Building houses is how you ask for people.
    /// </para>
    /// </remarks>
    internal static float PersonSeconds = WorldCalendar.YearSeconds * 0.5f;

    /// <summary>
    /// Seconds of unmet draw a household will bear before somebody leaves.
    /// </summary>
    /// <remarks>
    /// A season. Long enough that a bad week is weathered rather than punished — a store running dry for an
    /// afternoon is normal and should cost nothing — and short enough that a settlement which has genuinely
    /// outgrown its farms loses people inside the year it happened.
    /// </remarks>
    internal static float PrivationSeconds = WorldCalendar.LengthOf(Season.Winter);

    /// <summary>How fast privation drains once a household is being fed again.</summary>
    /// <remarks>
    /// Faster than it accrues, so recovery is quicker than decline. Otherwise a settlement that fixed its
    /// food supply would go on losing people for as long as the shortage lasted, which reads as the game
    /// ignoring the thing the player just did about it.
    /// </remarks>
    internal static float PrivationRecovery = 3f;

    /// <summary>
    /// How much of a resource the settlement wants put by before it grows, in seconds of draw.
    /// </summary>
    /// <remarks>
    /// <b>A winter.</b> Derived rather than picked: the year has one harvest and one season in which
    /// nothing grows and everything burns three times as much wood, so "can we feed one more" is exactly
    /// the question "would we still get through the winter". It also makes the threshold move with the
    /// calendar rather than being a day count somebody would have to remember to update.
    /// </remarks>
    internal static float BufferSeconds => WorldCalendar.LengthOf(Season.Winter);

    /// <summary>
    /// How ready the settlement is to feed one more mouth, from nothing to all of it.
    /// </summary>
    /// <remarks>
    /// Continuous, not a threshold, which is rule 1 of §2 — <em>rates, not gates</em>. A settlement with
    /// half a winter put by grows at half speed rather than not at all, so there is no cliff to fall off
    /// and no cliff to farm right up to the edge of. Both resources have to be there: bread and firewood
    /// are not substitutes, so this is a product and not a sum, and being rich in one covers nothing.
    /// <para>
    /// <b>Measured against the draw of everyone here plus one, which is what makes it well-defined at
    /// zero.</b> The first version measured against the current draw and treated no draw as an infinite
    /// buffer, on the reasoning that a settlement with no houses yet is not short of food — true of the
    /// <em>start</em> and catastrophically untrue of the end. A settlement whose last household starved out
    /// has no draw either, so it scored perfect readiness and its empty houses began producing people out
    /// of an empty granary: measured, a world with nothing in it grew a villager a year. Asking whether the
    /// stores would cover <em>one more than are here</em> has no zero case, because the answer always
    /// includes at least that one.
    /// </para>
    /// </remarks>
    /// <param name="mouths">
    /// Appetite of everyone the settlement is already feeding, summed. Appetites rather than heads because
    /// §6 says a soldier eats more, and one more villager is one more villager's worth on top of it.
    /// </param>
    public static float Readiness(int grainStored, int woodStored, float mouths, Season season)
    {
        var appetite = MathF.Max(0f, mouths) + 1f;
        var grain = Share(grainStored, EconomyRates.DrawPerSecond(Resource.Grain, season, appetite));
        var wood = Share(woodStored, EconomyRates.DrawPerSecond(Resource.Wood, season, appetite));
        return grain * wood;
    }

    private static float Share(float stored, float drawPerSecond) => drawPerSecond <= 1e-6f
        ? 0f
        : Math.Clamp(stored / (drawPerSecond * BufferSeconds), 0f, 1f);
}
