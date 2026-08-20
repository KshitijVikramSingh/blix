namespace RTSGame.Simulation.Agents;

/// <summary>
/// Body dimensions and movement limits for a standard unit.
/// </summary>
/// <remarks>
/// These were duplicated as literals across spawning, the stress scenarios, the
/// renderer's model scale, and the separation thresholds every crowd test asserts
/// on. Changing how big a unit is therefore meant editing a dozen unrelated files
/// and silently invalidating any threshold that was missed — a test asserting a
/// gap of twice the old radius still passes on smaller bodies, it just stops
/// measuring anything. One definition, and everything derives from it.
/// </remarks>
internal static class AgentDefaults
{
    /// <summary>Body radius of a standard unit, in metres.</summary>
    public const float Radius = 0.37f;

    /// <summary>
    /// The one radius the navigation layer routes at.
    /// </summary>
    /// <remarks>
    /// <b>There used to be two.</b> §3 derived a second class at 0.90 m from the raster's clearance
    /// ladder — a body that needs a 3 m gate where a villager passes a 1.5 m clearing — and the
    /// derivation was sound. What retired it was the game: once buildings occupied the ground they stand
    /// on, a 0.90 m body could not be routed to a point beside a 1.5 m building at all, because the
    /// raster's clearance beside a wall is 0.75 and that is below 0.90. A hauler that cannot approach a
    /// granary is not a hauler, and a class every building refuses is not a class.
    /// <para>
    /// So every unit routes at this radius and the router keeps one decomposition instead of two. What
    /// distinguishes bodies now is what they physically are — the cart is 0.55 m wide and is avoided and
    /// depenetrated at 0.55 — and what they can do: speed, turning circle, capacity, appetite. Those were
    /// always the interesting axes; the navigation radius was the one that cost a mesh.
    /// </para>
    /// <para>
    /// <see cref="RoutingRadius"/> is deliberately its own name rather than an alias for
    /// <see cref="Radius"/>. They are equal and they mean different things: one is how wide a body is,
    /// the other is what the router plans for. A second class, if the navigation cell ever gets finer,
    /// changes the second and not the first.
    /// </para>
    /// </remarks>
    public const float RoutingRadius = Radius;

    /// <summary>Radius used by the dense stress scenarios, kept in proportion.</summary>
    public const float CrowdRadius = 0.265f;

    /// <summary>Rendered height of a body at <see cref="Radius"/>.</summary>
    public const float BodyHeight = 1.45f;

    /// <summary>Sustained travel speed of a standard unit, in metres per second.</summary>
    /// <remarks>
    /// A walk. It was 4.5 — a hard run at near world-record marathon pace, sustained, by
    /// everybody — chosen so locomotion tests would resolve quickly across a 30 m world, and
    /// the file admitted as much. In 3D it cannot hide: a 1.45 m body beside a modelled
    /// granary settles the question, and four independent readings agree on where it lands.
    /// A person walks at 1.3–1.5 m/s; a marching column makes about 1.4; a loaded handcart
    /// 1.0–1.2; and an AoE2 villager, converted through the unit-scaled tile the plan argues
    /// for, is 1.48. This is not a preference, it is what the medium fixes.
    /// <para>
    /// It is also the number the map is measured in. Every gameplay distance is a travel
    /// time, so cutting speed to a third cuts every derived distance to a third — which is
    /// why the world came down from 1200 m to 600 m in the same change. See
    /// <c>plan-rts-game.md</c> §3.
    /// </para>
    /// </remarks>
    public const float MaximumSpeed = 1.79f;

    /// <summary>
    /// How close a chasing body settles behind the body it is chasing, in metres.
    /// </summary>
    /// <remarks>
    /// Named because something else has to agree with it. It is a following distance — near enough to be
    /// on somebody's heels without shouldering them along — and for a chase that means to <em>fight</em>
    /// it is also the closest a defender will ever get. See <c>ThreatSystem.Update</c>: harm reached 0.81 m
    /// while a chase settled at 0.95, so every defender in the game halted a hand's breadth outside
    /// striking distance and stayed there. Twenty-four raiders walked home with six hundred grain and not
    /// one of them was ever hurt enough to notice.
    /// </remarks>
    public const float ChaseStopMetres = 0.95f;

    /// <summary>How hard a body picks up speed, in metres per second squared.</summary>
    /// <remarks>
    /// Mutable so the tuning overlay can move it; the constant is the shipped default.
    /// Sixteen is over one and a half g. That is not a person starting to walk, it is a
    /// body teleporting to its target velocity: whatever the velocity solve asked for was
    /// granted inside a tick, so the solve's answer and the body's motion were the same
    /// thing and there was no momentum to read on screen. A walking person manages perhaps
    /// 1 to 3.
    /// <para>
    /// It stayed at sixteen for a long time because every threshold in the self-tests was
    /// tuned against a body that reaches its speed instantly. That question has now been
    /// answered on the slider and the tests have been re-based against the answer rather
    /// than the other way round: two metres per second squared takes a walker to speed in
    /// three quarters of a second, which is a person setting off rather than one appearing
    /// at speed.
    /// </para>
    /// </remarks>
    public static float Acceleration = 2f;

    /// <summary>How hard a body sheds speed, in metres per second squared.</summary>
    /// <remarks>
    /// Separate from <see cref="Acceleration"/>, and larger, because stopping and starting
    /// are not the same act — a person can plant a foot and halt far quicker than they can
    /// get going. Steering used one rate for both, so a body being told to slow down eased
    /// off exactly as gently as it had built up, which is what makes a crowd look like it
    /// is coasting into things rather than stopping short of them.
    /// <para>
    /// Half again above <see cref="Acceleration"/>, which is the asymmetry the separation
    /// existed for: a body plants a foot and stops in half the distance it needs to get
    /// going. At walking speed that is a stopping distance of 0.375 m — inside its own body
    /// diameter — where the old pair stopped in 0.63 m despite being eight times stiffer,
    /// because stopping distance goes as the square of speed.
    /// </para>
    /// </remarks>
    public static float Deceleration = 3f;

    /// <summary>
    /// How fast a body may swing its direction of travel, in radians per second.
    /// </summary>
    /// <remarks>
    /// This is a physical limit on the body, not a cosmetic one. It used to be 9
    /// rad/s — over 500 degrees a second, i.e. no limit at all — and it was only
    /// ever applied to the rendered facing, never to motion. The velocity solve
    /// was therefore obeyed literally however violently its answer swung, and
    /// bodies at a contested doorway averaged over 200 degrees a second of
    /// direction change. A unit that has to turn before it can go somewhere else
    /// cannot take part in that, and the limit doubles as a low-pass filter on
    /// the solver without pretending to be one.
    /// </remarks>
    public static float MaximumTurnSpeed = 3.03f;

    /// <summary>Speed below which a body may turn freely, as it would on the spot.</summary>
    /// <remarks>
    /// An eighth of top speed. It was written as 0.55 m/s against a top speed of 4.5, and
    /// left alone it would have become a third of a walk — a body swinging freely at a third
    /// of its travel speed, which is a skater, not a person.
    /// <para>
    /// Now a *share*, in <see cref="FreeTurnShare"/>, and read against each body's own top speed
    /// rather than the default one. An eighth of a walk and an eighth of a gallop are different
    /// speeds and the same statement, which is what this was always trying to say.
    /// </para>
    /// </remarks>
    public static float FreeTurnSpeed => MaximumSpeed * FreeTurnShare;

    /// <summary>Speed the second-denominated constants in this simulation were tuned at.</summary>
    /// <remarks>
    /// 4.5 m/s, the run the body used to do. Kept as a number rather than deleted because
    /// every constant that says how long some physical condition lasts — how long a jam takes
    /// to drain, how long a body commits to a route, how long it waits at a gap before
    /// believing the gap is closed — was measured against a body moving at this pace. Those
    /// are not preferences that survive a change of speed; they are descriptions of how long
    /// something takes, and a crowd that walks takes three times as long to do the same thing.
    /// </remarks>
    public const float TuningSpeed = 4.5f;

    /// <summary>
    /// Multiplier taking a duration measured at <see cref="TuningSpeed"/> to the same
    /// behaviour at the current speed.
    /// </summary>
    /// <remarks>
    /// Applied only to durations that describe movement. A distance, a ratio, a count, or a
    /// threshold that is not about how far something got does not scale — and getting that
    /// distinction wrong in either direction is how a change of pace turns into a change of
    /// behaviour nobody intended.
    /// </remarks>
    public static float PaceScale => TuningSpeed / WorldPace;

    /// <summary>The pace this world's second-denominated constants are calibrated at.</summary>
    /// <remarks>
    /// Equal to a villager's speed, and deliberately a separate constant from it. Every duration in
    /// this simulation describes how long some physical condition lasts — how long a jam drains,
    /// how long a body commits to a route — and those are properties of the world, not of whoever
    /// happens to be walking through it. Derived from <see cref="MaximumSpeed"/> it would have
    /// meant "however fast the default unit is", so adding a scout at twice the pace or a cart at
    /// two thirds of it would have silently re-timed every one of them.
    /// </remarks>
    public const float WorldPace = 1.79f;

    /// <summary>Share of its own top speed below which a body may turn freely.</summary>
    public const float FreeTurnShare = 0.1222f;

    /// <summary>
    /// Speed below which a body counts as not travelling, in metres per second.
    /// </summary>
    /// <remarks>
    /// These next two used to be absolute numbers — 0.5 and 0.25 m/s — and every one of them
    /// was chosen as a feel of the body they were tuned on, which ran at 4.5. Written down as
    /// speeds, they survive a change of pace by changing meaning: 0.5 m/s is a ninth of a run
    /// and a third of a walk, so a body deliberately picking its way at a third of walking
    /// pace would have been declared stationary. Written down as fractions, they mean the same
    /// thing at any speed, which is what they were always trying to say.
    /// </remarks>
    public static float TravellingSpeed => MaximumSpeed * 0.1111f;

    /// <summary>Speed below which a body's intent is too faint to count as wanting to move.</summary>
    public static float IntentSpeed => MaximumSpeed * 0.0556f;

    /// <summary>
    /// Share of the distance a body could cover in a tick that counts as making progress.
    /// </summary>
    /// <remarks>
    /// Was 2 mm per tick flat, which at a run is one and a third per cent of a tick's travel
    /// and at a walk is four — so the same body doing the same thing would have been judged
    /// stuck three times as readily purely because it slowed down.
    /// </remarks>
    public const float ProgressShareOfStep = 0.0133f;

    /// <summary>
    /// Smallest centre distance two bodies may sit at and still count as separated, allowing a
    /// small tolerance for a single tick's contact.
    /// </summary>
    /// <remarks>
    /// Pairwise, because with two body classes in the world a bar built from one radius doubled is
    /// not a statement about any actual pair — it would pass a wagon standing inside a villager.
    /// The single-radius form is kept for the many assertions about a crowd that is all one size,
    /// and is the same arithmetic with both arguments equal.
    /// </remarks>
    public static float SeparationThreshold(float radius, float otherRadius) =>
        radius + otherRadius - 0.01f;

    /// <summary>The separation bar for two bodies of the same radius.</summary>
    public static float SeparationThreshold(float radius) => SeparationThreshold(radius, radius);
}

