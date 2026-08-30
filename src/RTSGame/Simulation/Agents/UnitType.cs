namespace RTSGame.Simulation.Agents;

/// <summary>The durable economic/military identity of a body, independent of its current job or equipment.</summary>
internal enum AgentRole
{
    Villager,
    Militia,
    Raider,
    Other,
}

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
    float Radius,
    float MaximumSpeed,
    float TurningRadius,
    int CarryCapacity,
    float Appetite = 1f,
    float SightMetres = 22f,
    float Strength = 1f,
    float Health = 20f)
{
    /// <summary>
    /// Radius the navigation layer routes this unit at, which is the same for every unit.
    /// </summary>
    /// <remarks>
    /// Not an approximation. Two radii inside one clearance rung have *provably identical* walkable
    /// sets — walkability is monotone in radius, so equal cell counts across the band are equal sets —
    /// which is why a 0.55 m hauler may route on the 0.37 m field and be exactly right. What it buys is
    /// that the decomposition and the flow fields are cached once for the whole roster rather than per
    /// type. The self-test <c>every body routes on one decomposition</c> is what fails if that stops
    /// being true.
    /// <para>
    /// It was per body class, and there were two. See <see cref="AgentDefaults.RoutingRadius"/> for why
    /// there is now one: a 0.90 m body cannot be routed to a point beside a building, because the
    /// clearance beside a wall is 0.75.
    /// </para>
    /// </remarks>
    public float NavigationRadius => AgentDefaults.RoutingRadius;

    /// <summary>Whether this body must roll to change direction.</summary>
    public bool HasTurningCircle => TurningRadius > 0f;

    // Sight is on the body and not on the building, and that is a design decision rather than a
    // simplification. §7 wants an outpost to push detection outward — "105 at the settlement against 190
    // at an outpost" — and the honest way to get that is not a radius attached to a tower: it is that you
    // <em>posted somebody there</em>. A settlement's vision is its people, so an outpost extends it exactly
    // as far as the garrison you were willing to take off a field, which is §2's "structures buy back
    // attention" arriving as arithmetic instead of as a bonus.
    //
    // Twenty-two metres for a villager. Derived from the map rather than picked: the fields reach 25 m and
    // the forest closes at about 37, so a settlement's people between them see out to roughly where the
    // trees begin — and a raider walking out of the wood is noticed as it emerges, with about eleven
    // seconds before it reaches the granary. Tight, which is what forest cover is for.

    // Appetite is on the type rather than inferred from the body, because inferring it was tried and
    // it read a hauler's carry capacity as evidence about its stomach. §6 says soldiers eat more and
    // sink other resources slightly; this is the first half of that, written where the roster is.

    // Strength is damage a second and health is how many seconds of it a body survives, which makes a
    // fight legible without a combat model: five villagers against two raiders is five damage a second
    // against forty health each, so a raider falls in eight seconds while the raiders' six damage a second
    // takes a villager down in three. Eight seconds costs the settlement two people and the raid one — a
    // trade a player can read while it is happening, which is the only requirement at this stage.
    //
    // A villager fights badly on purpose. It is not a soldier; the point of §2's identity in its second
    // domain is that attention and material are substitutes in combat too, and a settlement that can defend
    // itself with farmhands has nothing to spend material on.

    /// <summary>The settlement's people. Everything else is measured against this one.</summary>
    /// <remarks>
    /// A carry of thirty is a sack, and it is derived rather than chosen. A field earns 0.88 grain a second
    /// of reaping, so thirty units is thirty-four seconds of work; twenty-three trips bring in a whole
    /// field's crop, and at ten metres to the store that is a quarter of the harvest window spent walking.
    /// It was eight, which made it eighty-eight trips and more walking than the window contains — the
    /// settlement starved with its fields full.
    /// </remarks>
    public static readonly UnitType Villager =
        new("villager", AgentDefaults.Radius, 1.79f, 0f, 30);

    /// <summary>
    /// Slower than a villager, which is the intended ordering: kit costs pace.
    /// </summary>
    public static readonly UnitType Militia =
        new("militia", AgentDefaults.Radius, 1.70f, 0f, 0, Appetite: 1.35f, Strength: 3f, Health: 45f);

    /// <summary>
    /// The handcart — a <em>frame a villager wears</em>, not a unit anybody spawns.
    /// </summary>
    /// <remarks>
    /// Wider than a person and deliberately in the same class, so it takes the same passages and merely
    /// takes up more room in the queue for one.
    /// <para>
    /// <b>Nothing in the game spawns this any more.</b> Hauling is a job: a villager given a route pays a
    /// sack of timber for a cart and wears these dimensions until it is given something else to do — see
    /// <c>SimulationWorld.TryAssignRoute</c>. So this entry is no longer "a kind of unit you have", it is
    /// the answer to "how big and how fast is somebody pulling a cart", which is exactly what a roster
    /// entry should be. The movement fixtures still spawn bodies at these dimensions directly, and that is
    /// legitimate: they are testing a 0.55 m body in a queue, not a hauler.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The scaffolding adversary's body — a raider. Strong, and slower once it is carrying.
    /// </summary>
    /// <remarks>
    /// The <em>body</em> belongs in the roster, like the handcart's does; the <em>behaviour</em> lives in
    /// <c>Debug/</c> beside the pen-escape crowd, because in the real game the thing over the hill is
    /// another player and none of the simulation may come to depend on scripted thieves existing. See §28.
    /// <para>
    /// Faster than a villager empty and slower loaded, which is what makes the return trip the defender's
    /// window: §7's whole argument for interception is that killing a loaded raider <em>returns</em> the
    /// grain rather than denying it. The loaded pace is applied by the raider's own behaviour, since
    /// carrying is a state rather than a kind of unit.
    /// </remarks>
    /// <summary>
    /// A thief, and priced as one rather than as a soldier.
    /// </summary>
    /// <remarks>
    /// <b>Health 18, and it was 40, which made a raid unbeatable by the people it was raiding.</b> Measured
    /// over eight raids: twenty-four raiders, one killed, and <em>eight villagers dead per raider</em>. At
    /// strength 1 a villager needed forty seconds of contact to bring one down and died in under seven, so
    /// a defence was a queue of people taking turns to lose.
    /// <para>
    /// Eighteen is set by the fight it should lose. Three or four villagers on one raider kill it in four
    /// or five seconds and it takes rather less than one of them with it; two of them trade one for one;
    /// one of them dies. That is the shape a settlement defending itself should have — <em>numbers work,
    /// but only just, and only together</em> — and it leaves a villager still fighting badly on purpose,
    /// which is the roster's intended ordering and is measured against the soldier at health 45 rather than
    /// against a thief.
    /// </para>
    /// <para>
    /// Strength stays at 3. A raider losing to four farmhands and killing any one of them it can get alone
    /// are both wanted, and it is strength that carries the second.
    /// </para>
    /// </remarks>
    public static readonly UnitType Raider =
        new("raider", AgentDefaults.Radius, 2.05f, 0f, 40, Appetite: 0f, SightMetres: 26f,
            Strength: 3f, Health: 18f);

    public static readonly UnitType HaulerCart =
        new("hauler cart", 0.55f, 1.10f, 0f, 40);

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
        new("light cavalry", AgentDefaults.Radius, 3.50f, 0f, 0, Appetite: 1.6f);

    /// <summary>
    /// Heavy cavalry: faster than people, slower than scouts, and unable to turn like either.
    /// </summary>
    /// <remarks>
    /// <b>2.5 m/s is not derived.</b> It is "between a walk and a scout" and nothing in §3 fixes
    /// it, so it is a slider question — unlike every other speed here, which comes from a
    /// real-world reading.
    /// <para>
    /// It was 0.90 m wide and "too wide for a clearing", which was the point of the second body class.
    /// The width is gone and the <em>turning circle</em> is what makes it heavy now: 2.6 m of it, so it
    /// cannot come about on the spot the way a person can. That was always the more interesting half of
    /// the distinction, and it costs the router nothing.
    /// </para>
    /// </remarks>
    public static readonly UnitType HeavyCavalry =
        new("heavy cavalry", AgentDefaults.Radius, 2.50f, 2.6f, 0, Appetite: 1.9f);

    /// <summary>Every type, for the tests that have to hold for all of them.</summary>
    public static readonly UnitType[] All =
    {
        Villager, Militia, Raider, HaulerCart, LightCavalry, HeavyCavalry,
    };

    public static AgentRole RoleOf(UnitType type) =>
        ReferenceEquals(type, Villager) || ReferenceEquals(type, HaulerCart) ? AgentRole.Villager :
        ReferenceEquals(type, Militia) ? AgentRole.Militia :
        ReferenceEquals(type, Raider) ? AgentRole.Raider : AgentRole.Other;
}
