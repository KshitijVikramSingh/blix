namespace RTSGame.Simulation.Movement;

/// <summary>
/// Why a body stopped being a member of a cohort.
/// </summary>
/// <remarks>
/// <b>Every way out of a cohort is named, because §105 was one of them going unnamed.</b> The jobs layer
/// reclaimed half a cohort two seconds after it arrived, the group learned about it by quietly counting fewer
/// members, and from the chair that was indistinguishable from an order being abandoned. A departure with a
/// reason attached is an event; a member count that went down is not.
/// <para>
/// The values divide into what the player asked for and what happened to the body. <see cref="Superseded"/>,
/// <see cref="Overridden"/> and <see cref="Arrived"/> are the cohort's own business — a newer order, a
/// different verb, or the job finished. <see cref="Died"/> is not a decision at all. That leaves
/// <see cref="Interrupted"/> as the only path by which a body leaves a cohort without the player having said
/// anything, which is exactly the one to keep counted and in view.
/// </para></remarks>
internal enum CohortDeparture
{
    /// <summary>A newer move order took the body into a different cohort.</summary>
    Superseded,
    /// <summary>A command that is not a move — stop, follow, chase, flee, patrol — took the body.</summary>
    Overridden,
    /// <summary>The jobs layer took the body back to a standing assignment. The only implicit exit.</summary>
    Interrupted,
    /// <summary>The cohort reached its target and retired, releasing every member.</summary>
    Arrived,
    /// <summary>The body left the world.</summary>
    Died,
}
