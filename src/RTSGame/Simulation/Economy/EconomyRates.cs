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

    /// <summary>Wood one pair of hands cuts over a year.</summary>
    internal static float WoodPerHandPerYear = 500f;

    /// <summary>Grain one villager eats a year. One unit a day unit, so the day is the ration.</summary>
    internal static float GrainPerVillagerPerYear = WorldCalendar.DaysPerYear;

    /// <summary>Wood one household burns a year, before the winter swing.</summary>
    internal static float WoodPerVillagerPerYear = 120f;

    // "Diminishing returns on hands at one place" stood here as a square root, and it is retired: the
    // deadline does that work and does it better. A field asks for 900 labour-seconds inside a 1,200-second
    // spring, so one pair of hands just manages it and four finish early and then have nothing to do. Lean
    // staffing is efficient because it fills each window exactly; overstaffing is wasteful because the
    // window closes, not because output is taxed. §2's identity, with no invented curve in it.
    //
    // It is still here for the resources that have not been converted yet — wood, until Stage B gives
    // trees a labour cost of their own — and should go with the last of them.
    internal static float HandsEffect(int hands) => hands <= 0 ? 0f : MathF.Sqrt(hands);

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

    /// <summary>
    /// Wood arrives when labour is free, which is summer and winter. §6's other cycle.
    /// </summary>
    private static float WoodShape(Season season) => season switch
    {
        Season.Spring => 0.35f,
        Season.Summer => 1f,
        Season.Harvest => 0.2f,
        _ => 0.8f,
    };

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

    /// <summary>
    /// Units per second this node produces right now, per unit of hands effect.
    /// </summary>
    /// <remarks>
    /// Wood only. Grain is not a rate any more — a field is prepared, kept and reaped, and
    /// <see cref="CropCycle"/> owns all three. Asking this for grain is a bug, and it says so rather than
    /// returning a plausible number.
    /// </remarks>
    public static float ProductionPerSecond(Resource resource, Season season)
    {
        if (resource == Resource.Grain)
        {
            throw new InvalidOperationException(
                "Grain has no production rate. A field yields what CropCycle's three windows of labour " +
                "earn it, and asking for a rate is asking the question the crop cycle replaced.");
        }

        return WoodPerHandPerYear / WorldCalendar.YearSeconds *
               Normalised(resource, season, production: true);
    }

    /// <summary>Grain a second of reaping earns from this field, at its own potential.</summary>
    public static float ReapedPerSecond(in EconomyNode farm) =>
        CropCycle.ReapTargetOf(in farm) <= 0f
            ? 0f
            : GrainPerFarmPerYear * CropCycle.PotentialOf(in farm) / CropCycle.ReapTargetOf(in farm);

    /// <summary>Units per second one person draws of this resource right now.</summary>
    public static float DrawPerSecond(Resource resource, Season season, float appetite)
    {
        var annual = resource == Resource.Grain
            ? GrainPerVillagerPerYear
            : WoodPerVillagerPerYear;
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
            (Resource.Grain, true) => throw new InvalidOperationException("Grain has no production shape."),
            (Resource.Wood, true) => WoodShape(season),
            (Resource.Grain, false) => GrainDrawShape(season),
            _ => WoodBurnShape(season),
        };
}
