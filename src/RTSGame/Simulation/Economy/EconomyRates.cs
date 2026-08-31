using RTSGame.Simulation.Agents;

namespace RTSGame.Simulation.Economy;

/// <summary>
/// Every rate the economy runs on, and the seasonal shape that modulates them.
/// </summary>
/// <remarks>
/// Two rules from §2 govern this file. <b>Rates, not gates:</b> nothing here is a prerequisite,
/// everything is continuous in the hands assigned to it, so strategies nobody thought of survive.
/// <b>Everything in time:</b> the primitives are annual amounts, because a year is the unit the
/// calendar is built from and "how long will this last" is the only question the HUD asks.
/// <para>
/// The seasonal shapes are normalised: a shape integrated over the year comes to exactly one, so
/// changing <em>when</em> a resource arrives cannot silently change <em>how much</em> arrives. That is
/// the same discipline as deriving path cost from speed — one source of truth for the quantity, and
/// the distribution is a separate, checkable thing.
/// </para>
/// </remarks>
internal static class EconomyRates
{
    /// <summary>Grain one field yields in a year, if it is prepared, tended and fully reaped.</summary>
    /// <remarks>
    /// Per <em>field</em> rather than per pair of hands, because a field's output is no longer a rate
    /// somebody stands next to: it is <see cref="CropCycle"/>'s three windows of labour, and hands only
    /// decide whether the windows are met. At 270 grain eaten per person per year this feeds 2.6 people,
    /// which is the pre-industrial band the settlement-size argument rests on.
    /// <para>
    /// The margin matters as much as the ratio. At 600 the fixed point — where the farmers feed the
    /// woodcutters who warm the farmers, and both feed anyone who produces nothing — landed on a
    /// settlement whose annual surplus was one per cent, which is a knife edge rather than an economy.
    /// </para>
    /// </remarks>
    internal static float GrainPerFarmPerYear = 700f;

    /// <summary>Wood one pair of hands cuts over a year, walking included.</summary>
    /// <remarks>
    /// Not a rate anything has any more — <see cref="Woodland"/> derives the cut rate, the load and the
    /// reach from it, and the wood itself comes out of trees. It survives as the <em>annual figure</em>
    /// the derivation is anchored to, which is where §2's "everything in time" wants it: a year is the
    /// unit the calendar is built from, and this is the only place a number about wood is chosen rather
    /// than computed. At 120 burnt per villager a year, four cutters keep seventeen people warm.
    /// </remarks>
    internal static float WoodPerHandPerYear = 500f;

    /// <summary>Stone one pair of hands quarries over a year, walking included.</summary>
    /// <remarks>
    /// Lower than wood's five hundred, and the gap is the claim: stone is heavier to win and further to carry,
    /// so a pair of hands on the rock brings in less of it than a pair of hands in the wood. Like
    /// <see cref="WoodPerHandPerYear"/> it is not a rate anything has — <see cref="Quarrying"/> derives the cut
    /// rate, the load and the reach from it — but the anchor the derivation hangs on, and the only place a
    /// number about stone is chosen rather than computed.
    /// <para>
    /// At 240 a year, one quarrier is most of a house's stone in a season, which keeps a quarry worth manning
    /// without making it the whole economy.
    /// </para>
    /// </remarks>
    internal static float StonePerHandPerYear = 240f;

    /// <summary>
    /// Grain one villager eats a year. An economic anchor, and its own knob.
    /// </summary>
    /// <remarks>
    /// It read <c>WorldCalendar.DaysPerYear</c>, on the strength of "one unit a day unit, so the day is the
    /// ration" — which is true today and true by coincidence. Two hundred and seventy is two different
    /// decisions that happen to share a number: how many days read as a year, which is legibility, and how
    /// much a person eats against a farm's seven hundred, which is the balance the whole economy sits on.
    /// <para>
    /// Welding them means the calendar cannot be made more legible without silently rebalancing the
    /// settlement — turn the day count to 365 and a farm quietly stops feeding 2.6 people. §112 found that
    /// by turning the dial the other way and watching one field come to feed four and a half times its
    /// share. So the ration is written down as the anchor it is, and the day being a ration stays true
    /// while both are 270 rather than being enforced by a derivation that hides which one is load-bearing.
    /// </para></remarks>
    internal static float GrainPerVillagerPerYear = 270f;

    /// <summary>Wood one household burns a year, before the winter swing.</summary>
    internal static float WoodPerVillagerPerYear = 120f;

    // "Diminishing returns on hands at one place" stood here as a square root, and it is <b>gone</b>: the
    // deadline does that work and does it better. A field asks for 900 labour-seconds inside a 1,200-second
    // spring, so one pair of hands just manages it and four finish early and then have nothing to do. Lean
    // staffing is efficient because it fills each window exactly; overstaffing is wasteful because the
    // window closes, not because output is taxed. §2's identity, with no invented curve in it.
    //
    // It survived one stage longer for wood, and Stage B took the last caller with it: a tree holds a
    // finite amount of wood and two cutters at one trunk fell it in half the time, which is not
    // diminishing returns, it is arithmetic.

    /// <summary>
    /// How much of a year's grain arrives per second of this season, as a multiple of the flat rate.
    /// </summary>
    /// <remarks>
    /// §6's calendar in one function. Nothing in spring — the crop is in the ground — a little through
    /// summer as it is tended, the spike at harvest, nothing in winter. Normalised below, so the
    /// annual total is <see cref="GrainPerHandPerYear"/> whatever shape this is given.
    /// </remarks>
    // GrainShape is retired with the grain rate: when a field yields is now a fact about which window the
    // year is in and how much labour went into the last one, not a curve.

    // WoodShape is retired with the wood rate. "Wood arrives when labour is free, which is summer and
    // winter" was a curve describing a decision — and it is a decision the player now actually makes,
    // because spring and harvest belong to the fields and whoever is not in a field can be at a tree.
    // A seasonal shape on wood would have been the game playing that allocation on the player's behalf
    // and then charging them for it.

    /// <summary>Wood burns far faster in winter, because every house needs heating.</summary>
    private static float WoodBurnShape(Season season) => season switch
    {
        Season.Spring => 0.7f,
        Season.Summer => 0.35f,
        Season.Harvest => 0.6f,
        _ => 3f,
    };

    /// <summary>Grain is eaten at the same rate all year. Everyone eats.</summary>
    private static float GrainDrawShape(Season season) => 1f;

    // ProductionPerSecond is retired. <b>Nothing in the economy produces at a rate.</b> A field is
    // prepared, kept and reaped and CropCycle owns all three; wood is standing in trees and Woodland
    // owns what it costs to fell one. Both were rates once, and both were the same bug in two costumes —
    // an abstract producer you could put anywhere, in a design whose premise is that the map is the
    // economy. The seasonal-shape machinery below survives for <em>draw</em>, which really is a curve:
    // every house burns three times as much wood in winter and nobody decides that.

    /// <summary>What a whole year of this field is worth, at full potential, on the ground it stands on.</summary>
    /// <remarks>
    /// <b>The rate is per farm-year; the field is what makes it per <em>this</em> farm.</b> Both places that
    /// used to multiply <c>GrainPerFarmPerYear</c> by a potential now go through here, because they were the
    /// two halves of one quantity — what a second of reaping earns, and what is left standing — and a
    /// fertility applied to one of them and not the other would have made the store's own forecast disagree
    /// with what the reapers brought in.
    /// </remarks>
    public static float FullYearOf(in EconomyNode farm) => GrainPerFarmPerYear * farm.Fertility;

    /// <summary>Grain a second of reaping earns from this field, at its own potential.</summary>
    public static float ReapedPerSecond(in EconomyNode farm) =>
        CropCycle.ReapTargetOf(in farm) <= 0f
            ? 0f
            : FullYearOf(in farm) * CropCycle.PotentialOf(in farm) / CropCycle.ReapTargetOf(in farm);

    /// <summary>Units per second one person draws of this resource right now.</summary>
    /// <remarks>
    /// <b>The two-way ternary this used to be was the worst of the lot, because it did not alias a store — it
    /// invented a demand.</b> <c>resource == Grain ? grainRation : woodRation</c> put stone in the else, so the
    /// moment stone existed every household began burning it at a villager's firewood rate. Measured on the
    /// first run that quarried any: 62 units out of the rock, 20 in hand, <b>42 consumed</b>, and conservation
    /// perfectly happy because the units really had left the world through a real door.
    /// <para>
    /// Nothing burns stone, so its annual draw is zero, and a resource nobody has written a draw for should
    /// read zero rather than inherit its neighbour's.
    /// </para>
    /// </remarks>
    public static float DrawPerSecond(Resource resource, Season season, float appetite)
    {
        var annual = resource switch
        {
            Resource.Grain => GrainPerVillagerPerYear,
            Resource.Wood => WoodPerVillagerPerYear,
            _ => 0f,
        };
        return annual / WorldCalendar.YearSeconds * Normalised(resource, season, production: false) *
               appetite;
    }

    /// <summary>
    /// How much this body eats relative to a villager, which is a property of the roster and not
    /// something to infer from the body.
    /// </summary>
    /// <remarks>
    /// It was inferred at first — from speed and carry capacity — and that read a hauler's empty cart
    /// as evidence about its stomach and a bare test agent as a soldier. Appetite is on
    /// <c>UnitType</c>, comes down through spawning, and rides on the body like every other fact
    /// about it.
    /// </remarks>
    public static float AppetiteOf(in AgentState agent) => agent.Appetite;

    /// <summary>
    /// A season's share of the shape, scaled so the shape integrates to one over the year.
    /// </summary>
    /// <remarks>
    /// This is what stops the calendar quietly changing the annual totals. Move the harvest spike or
    /// lengthen a season and the distribution changes; the yearly amount cannot, because it is divided
    /// out here. A shape that summed to something other than a year's worth would otherwise be a
    /// balance change disguised as a flavour change.
    /// </remarks>
    private static float Normalised(Resource resource, Season season, bool production)
    {
        var shape = Shape(resource, season, production);
        var weighted = 0f;
        foreach (var candidate in Enum.GetValues<Season>())
        {
            weighted += Shape(resource, candidate, production) * WorldCalendar.YearShareOf(candidate);
        }

        return weighted <= 0f ? 0f : shape / weighted;
    }

    private static float Shape(Resource resource, Season season, bool production) =>
        (resource, production) switch
        {
            (_, true) => throw new InvalidOperationException(
                "Nothing has a production shape. A field's output is CropCycle's three windows of " +
                "labour and wood is felled out of trees; asking when a resource arrives is asking the " +
                "question those two replaced."),
            (Resource.Grain, false) => GrainDrawShape(season),
            _ => WoodBurnShape(season),
        };
}
