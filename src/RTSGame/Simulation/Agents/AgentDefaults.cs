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
    public const float MaximumSpeed = 1.5f;

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
    public static float MaximumTurnSpeed = 4.0f;

    /// <summary>Speed below which a body may turn freely, as it would on the spot.</summary>
    /// <remarks>
    /// An eighth of top speed. It was written as 0.55 m/s against a top speed of 4.5, and
    /// left alone it would have become a third of a walk — a body swinging freely at a third
    /// of its travel speed, which is a skater, not a person.
    /// </remarks>
    public static float FreeTurnSpeed = MaximumSpeed * 0.1222f;

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
    public static float PaceScale => TuningSpeed / MaximumSpeed;

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
    /// Smallest centre distance two bodies of the given radii may sit at and still
    /// count as separated, allowing a small tolerance for a single tick's contact.
    /// </summary>
    public static float SeparationThreshold(float radius) => radius * 2f - 0.01f;
}
