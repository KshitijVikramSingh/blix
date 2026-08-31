namespace RTSGame.Simulation.Navigation;

/// <summary>
/// Why a route was asked for. Every call into <see cref="PathService.FindPath"/> names one.
/// </summary>
/// <remarks>
/// <b>Because "two route queries" is not a diagnosis.</b> §115 counted the searches in the click after a
/// placement change and found two of them, together some 60% of the whole event — twenty bodies took the
/// shared field as intended and two did not. Which two, and why, the count cannot say: a body that fell off
/// the field on arrival, a villager whose stored route died with the navigation revision, and a stuck body
/// asking again all look identical from a counter, and they want opposite fixes. One wants the field to hold
/// more bodies, one wants the revision bump not to invalidate a route it did not touch, one wants a cheaper
/// search.
/// <para>
/// Required rather than defaulted, on the same reasoning as <see cref="Movement.CohortDeparture"/>: a new
/// call site has to say what it is for, because the failure mode being guarded against is a route query
/// nobody knew was being issued.
/// </para>
/// <para>
/// The values divide into three groups worth keeping apart. <b>An order</b> — <see cref="OrderSlot"/>,
/// <see cref="SoloMove"/>, <see cref="FormationSlot"/>, <see cref="Patrol"/> — is the player asking, and its
/// cost is a click's cost. <b>A behaviour</b> — <see cref="Behavior"/>, <see cref="ChaseTarget"/>,
/// <see cref="ReturnToHold"/> — is the simulation acting on a standing intention. Everything else is
/// <b>recovery</b>: a route that stopped working, for six distinct reasons.
/// </para></remarks>
internal enum RouteReason
{
    /// <summary>A move order whose body the shared field refused, walking to its formation slot instead.</summary>
    OrderSlot,
    /// <summary>A move order given to a body with no cohort to share a field with.</summary>
    SoloMove,
    /// <summary>A cohort member claiming its slot on approach, once the group is near enough.</summary>
    FormationSlot,
    /// <summary>A patrol leg.</summary>
    Patrol,
    /// <summary>A standing behaviour — following or pursuing — re-aiming at where its subject now is.</summary>
    Behavior,
    /// <summary>A body closing on the target it was told to attack.</summary>
    ChaseTarget,
    /// <summary>A body walking back to the position it was told to hold.</summary>
    ReturnToHold,
    /// <summary>Routing round a crowd the body is being held up by. Budgeted; see MaxRoutePlansPerTick.</summary>
    CongestionReroute,
    /// <summary>
    /// A stored route invalidated by the navigation revision — terrain sculpted, or a building placed.
    /// </summary>
    /// <remarks>
    /// The one to watch. A flow-transit body costs nothing here because its field is keyed by the revision
    /// and simply rebuilt; a body holding a polyline pays a full search, and it pays it in the same tick as
    /// every other body holding one. This is the term §115 left unattributed.
    /// </remarks>
    NavigationChanged,
    /// <summary>A body with a live destination that produced no preferred velocity, asking for a route.</summary>
    NoVelocity,
    /// <summary>A body the shared field kept steering into something it cannot walk through.</summary>
    TransitRejected,
    /// <summary>
    /// A dropped body hopping back onto the field at the nearest cell the field can price.
    /// </summary>
    /// <remarks>Short by construction — that is the whole of §96's fix, and it should read as short here.</remarks>
    FieldEntry,
    /// <summary>
    /// A dropped body solving its own route to the real goal, because it is in a crowd.
    /// </summary>
    /// <remarks>
    /// Kept deliberately (§96): in a pen, a body that solves its own route can pick a different exit, and
    /// that diversity is the behaviour both pen self-tests assert. It is also the expensive branch — a full
    /// cross-map search — so it is worth seeing separately from the entry hop it is the alternative to.
    /// </remarks>
    TransitStranded,
    /// <summary>Repairing a route whose first segment an avoidance sidestep disconnected.</summary>
    RouteRepair,
    /// <summary>A body that has had no intent for long enough to try once more before giving the order up.</summary>
    NoIntentRetry,
    /// <summary>A scheduled congestion recovery, routing against the dynamic costs of a jammed crowd.</summary>
    CongestionRecovery,
}
