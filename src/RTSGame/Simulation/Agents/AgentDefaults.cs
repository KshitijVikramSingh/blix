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

    public const float MaximumSpeed = 4.5f;

    /// <summary>How hard a body picks up speed, in metres per second squared.</summary>
    /// <remarks>
    /// Mutable so the tuning overlay can move it; the constant is the shipped default.
    /// Sixteen is over one and a half g. That is not a person starting to walk, it is a
    /// body teleporting to its target velocity: whatever the velocity solve asked for was
    /// granted inside a tick, so the solve's answer and the body's motion were the same
    /// thing and there was no momentum to read on screen. A walking person manages perhaps
    /// 1 to 3.
    /// <para>
    /// It stays at sixteen regardless, because every threshold in the self-tests was tuned
    /// against a body that reaches its speed instantly, and moving it breaks two of them.
    /// What it should be is a question about how the game feels, which is what the slider
    /// is for — and once that question is answered the tests want re-basing against the
    /// answer, not the other way round.
    /// </para>
    /// </remarks>
    public static float Acceleration = 16f;

    /// <summary>How hard a body sheds speed, in metres per second squared.</summary>
    /// <remarks>
    /// Separate from <see cref="Acceleration"/>, and larger, because stopping and starting
    /// are not the same act — a person can plant a foot and halt far quicker than they can
    /// get going. Steering used one rate for both, so a body being told to slow down eased
    /// off exactly as gently as it had built up, which is what makes a crowd look like it
    /// is coasting into things rather than stopping short of them.
    /// <para>
    /// Defaulted equal to <see cref="Acceleration"/> so the shipped behaviour is unchanged
    /// and the distinction only exists once somebody moves the slider.
    /// </para>
    /// </remarks>
    public static float Deceleration = 16f;

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
    public static float FreeTurnSpeed = 0.55f;

    /// <summary>
    /// Smallest centre distance two bodies of the given radii may sit at and still
    /// count as separated, allowing a small tolerance for a single tick's contact.
    /// </summary>
    public static float SeparationThreshold(float radius) => radius * 2f - 0.01f;
}
