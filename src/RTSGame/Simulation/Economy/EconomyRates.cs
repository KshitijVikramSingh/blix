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
    /// <summary>Grain one pair of hands brings in over a year, at full effect.</summary>
    /// <remarks>
    /// Set against consumption rather than chosen: at 270 grain eaten per person per year this makes one
    /// farmer feed about 2.6 people, which is in the pre-industrial band the settlement-size argument
    /// rests on. Raise it and a settlement needs fewer hands on food and has more for everything else,
    /// which is exactly the dial §2's economic identity turns on.
    /// <para>
    /// The margin matters as much as the ratio. At 600 the fixed point — where the farmers feed the
    /// woodcutters who warm the farmers, and both feed the haulers who produce nothing — landed on a
    /// settlement whose annual surplus was one per cent, which is a knife edge rather than an economy:
    /// any hauling inefficiency at all took it negative. Logistics is a real cost and six carts are six
    /// mouths that grow nothing, so the rates have to clear that before there is anything to store.
    /// </para>
    /// </remarks>
    internal static float GrainPerHandPerYear = 700f;

    /// <summary>Wood one pair of hands cuts over a year.</summary>
    internal static float WoodPerHandPerYear = 500f;

    /// <summary>Grain one villager eats a year. One unit a day unit, so the day is the ration.</summary>
    internal static float GrainPerVillagerPerYear = WorldCalendar.DaysPerYear;

    /// <summary>Wood one household burns a year, before the winter swing.</summary>
    internal static float WoodPerVillagerPerYear = 120f;

    /// <summary>
    /// Diminishing returns on hands at one place. §6: a second farmer brings in more and eats.
    /// </summary>
    /// <remarks>
    /// Square root, which is the cheapest honest shape: the first hand is worth a full hand, the
    /// fourth is worth half of one. What matters is not the exponent but that it is below linear, which
    /// is what makes lean staffing efficient and overstaffing autonomous — the two ends of §2's
    /// identity. The exponent is a dial and this is where it lives.
    /// </remarks>
    internal static float HandsEffect(int hands) => hands <= 0 ? 0f : MathF.Sqrt(hands);

    /// <summary>
    /// How much of a year's grain arrives per second of this season, as a multiple of the flat rate.
    /// </summary>
    /// <remarks>
    /// §6's calendar in one function. Nothing in spring — the crop is in the ground — a little through
    /// summer as it is tended, the spike at harvest, nothing in winter. Normalised below, so the
    /// annual total is <see cref="GrainPerHandPerYear"/> whatever shape this is given.
    /// </remarks>
    private static float GrainShape(Season season) => season switch
    {
        Season.Spring => 0f,
        Season.Summer => 0.25f,
        Season.Harvest => 1f,
        _ => 0f,
    };

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

    /// <summary>Units per second this node produces right now, per unit of hands effect.</summary>
    public static float ProductionPerSecond(Resource resource, Season season)
    {
        var annual = resource == Resource.Grain ? GrainPerHandPerYear : WoodPerHandPerYear;
        return annual / WorldCalendar.YearSeconds * Normalised(resource, season, production: true);
    }

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
            (Resource.Grain, true) => GrainShape(season),
            (Resource.Wood, true) => WoodShape(season),
            (Resource.Grain, false) => GrainDrawShape(season),
            _ => WoodBurnShape(season),
        };
}
