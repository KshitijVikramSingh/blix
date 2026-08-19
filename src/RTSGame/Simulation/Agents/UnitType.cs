namespace RTSGame.Simulation.Agents;

/// <summary>
/// Everything about a unit that is a property of what it is rather than of what it is doing.
/// </summary>
/// <remarks>
/// The body was four loose floats passed to <c>Spawn</c> and defaulted from
/// <see cref="AgentDefaults"/>, which is fine while there is one kind of unit and a bug source the
/// moment there are six — the file's own remarks record what that cost last time it happened.
/// A type names them together so a villager cannot accidentally be given a wagon's turning circle.
/// <para>
/// Speeds are §3 of <c>plan-rts-game.md</c>, derived rather than chosen: a person walks 1.3–1.5 m/s,
/// a marching column makes about 1.4, a loaded handcart 1.0–1.2. The two that are *not* derived are
/// marked below, because a number nobody derived should be visibly a slider question rather than
/// quietly indistinguishable from one that was.
/// </para>
/// </remarks>
internal sealed record UnitType(
    string Name,
    BodyClass Class,
    float Radius,
    float MaximumSpeed,
    float TurningRadius,
    int CarryCapacity,
    float Appetite = 1f)
{
    /// <summary>
    /// Radius the navigation layer routes this unit at, which is its class's rather than its own.
    /// </summary>
    /// <remarks>
    /// Not an approximation. Two radii inside one clearance rung have *provably identical* walkable
    /// sets — walkability is monotone in radius, so equal cell counts across the band are equal
    /// sets — which is why a 0.55 m hauler may route on the 0.37 m field and still be exactly
    /// right. What it buys is that the decomposition and the flow fields are cached per class
    /// instead of per type, so six unit types are two meshes rather than four identical copies of
    /// two. The self-test <c>body radii inside one rung share a decomposition</c> is what fails if
    /// that ever stops being true.
    /// </remarks>
    public float NavigationRadius => AgentDefaults.RadiusOf(Class);

    /// <summary>Whether this body must roll to change direction.</summary>
    public bool HasTurningCircle => TurningRadius > 0f;

    // Appetite is on the type rather than inferred from the body, because inferring it was tried and
    // it read a hauler's carry capacity as evidence about its stomach. §6 says soldiers eat more and
    // sink other resources slightly; this is the first half of that, written where the roster is.

    /// <summary>The settlement's people. Everything else is measured against this one.</summary>
    public static readonly UnitType Villager =
        new("villager", BodyClass.Foot, AgentDefaults.Radius, 1.79f, 0f, 8);

    /// <summary>
    /// Slower than a villager, which is the intended ordering: kit costs pace.
    /// </summary>
    public static readonly UnitType Soldier =
        new("soldier", BodyClass.Foot, AgentDefaults.Radius, 1.70f, 0f, 0, Appetite: 1.35f);

    /// <summary>
    /// The hauler. Wider than a person and deliberately in the same class, so it takes the same
    /// passages and merely takes up more room in the queue for one.
    /// </summary>
    public static readonly UnitType HaulerCart =
        new("hauler cart", BodyClass.Foot, 0.55f, 1.10f, 0f, 40);

    /// <summary>
    /// Scout cavalry: a horse's pace on a person's footprint, so it goes wherever infantry goes.
    /// </summary>
    /// <remarks>
    /// It keeps the flat turn rate with everything else on foot, which at 3.5 m/s is a 1.16 m
    /// turning radius — a horse cornering like a person. That is a look question rather than a
    /// correctness one and belongs on the slider: the speed-scaled model was measured badly wrong
    /// inside a crowd, and this unit is in the crowd.
    /// </remarks>
    public static readonly UnitType LightCavalry =
        new("light cavalry", BodyClass.Foot, AgentDefaults.Radius, 3.50f, 0f, 0, Appetite: 1.6f);

    /// <summary>
    /// Heavy cavalry: faster than people, slower than scouts, and too wide for a clearing.
    /// </summary>
    /// <remarks>
    /// <b>2.5 m/s is not derived.</b> It is "between a walk and a scout" and nothing in §3 fixes
    /// it, so it is a slider question — unlike every other speed here, which comes from a
    /// real-world reading.
    /// </remarks>
    public static readonly UnitType HeavyCavalry =
        new("heavy cavalry", BodyClass.Heavy, AgentDefaults.HeavyRadius, 2.50f, 2.6f, 0, Appetite: 1.9f);

    /// <summary>
    /// The heavy hauler. Same pace as the cart: it trades in capacity and access, not speed.
    /// </summary>
    /// <remarks>
    /// §3 derived one cart speed and there is no reading that says a drawn wagon is slower than a
    /// pushed handcart, so it is not given one. Making its trade "carries five times as much and
    /// needs a gate" keeps the roster free of a number nobody could defend.
    /// </remarks>
    public static readonly UnitType Wagon =
        new("wagon", BodyClass.Heavy, AgentDefaults.HeavyRadius, 1.10f, 3.4f, 200);

    /// <summary>Every type, for the tests that have to hold for all of them.</summary>
    public static readonly UnitType[] All =
    {
        Villager, Soldier, HaulerCart, LightCavalry, HeavyCavalry, Wagon,
    };
}
