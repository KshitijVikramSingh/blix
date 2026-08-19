using RTSGame.Simulation.Agents;

namespace RTSGame.Simulation.Jobs;

/// <summary>Tunables of the jobs layer. All of them are durations or multiples of a body.</summary>
/// <remarks>
/// Four separate bugs in Session 4 were a distance written as a flat number that had been
/// tuned against a 0.37 m body walking at 1.79 m/s, so nothing here is a bare metre. A
/// distance is a multiple of the body it describes; a duration is scaled by
/// <see cref="AgentDefaults.PaceScale"/> so that it means the same thing at any world pace.
/// </remarks>
internal static class JobDefaults
{
    /// <summary>
    /// How near a body has to be to count as at its place, as a multiple of its own radius.
    /// </summary>
    /// <remarks>
    /// Three radii: 1.11 m for a villager, 2.70 m for a wagon. It has to be looser than arrival
    /// tolerance and it cannot be a fixed distance, for the same reason the neighbour horizon
    /// could not be. Arrival already gives up and settles nearby when the ground is contested,
    /// so a job that demanded the exact point would send a unit that had correctly stopped
    /// 0.4 m short back around for another attempt, forever. Looser still would let a wagon
    /// call itself present from outside the yard.
    /// </remarks>
    internal static float PlaceRadiusShare = 3.0f;

    /// <summary>Seconds of standing free before an interrupted unit takes its job back up.</summary>
    /// <remarks>
    /// Long enough that a run of manual orders reads as one act of attention rather than a
    /// fight with the unit's own intentions, short enough that letting go of a unit visibly
    /// returns it to work. Two seconds at the tuned pace. This is the only number in the
    /// three-layer model a player will feel directly.
    /// </remarks>
    internal static float OrderGraceSeconds = 2.0f * AgentDefaults.PaceScale;

    /// <summary>
    /// How near counts as at a place a crowd is already standing on, once this body has tried
    /// and failed to reach it properly. A multiple of its own radius, like the reach itself.
    /// </summary>
    /// <remarks>
    /// Eight radii: 2.96 m for a villager. This exists because a workplace is a place for
    /// several pairs of hands and not a spot for one body. Sixteen units posted at one point
    /// cannot all stand on it, and without this the ones that lost the race spend the rest of
    /// their lives walking at it — which is exactly what the first run of the jobs trace showed,
    /// eight of forty-eight units permanently unable to get to work.
    /// <para>
    /// It only applies where the router says the place <em>is</em> reachable, so a unit assigned
    /// inside a wall goes on politely retrying rather than deciding it has arrived from three
    /// metres away. That distinction is the whole reason this is not simply a looser reach: the
    /// two cases look identical from the body and are completely different from the ground.
    /// </para>
    /// </remarks>
    internal static float PlaceCrowdShare = 8.0f;

    /// <summary>
    /// How far from a building's wall a body may settle when the ground against it is taken, as a
    /// multiple of its own radius.
    /// </summary>
    /// <remarks>
    /// Much tighter than <see cref="PlaceCrowdShare"/>, which is measured from a <em>point</em> and has to
    /// leave room for a crowd to arrange itself around one. A building has a whole wall to line up along,
    /// so three radii — 1.1 m for a villager, 1.65 for a cart — is a second rank behind the first and no
    /// more. It was eight, measured from the circle round the building rather than from the wall, and that
    /// is what let a cart call itself at work while standing four metres clear of a granary.
    /// </remarks>
    internal static float CrowdedTouchShare = 3.0f;

    /// <summary>
    /// Failed attempts at a reachable place before the body works from where the crowd left it.
    /// </summary>
    internal static int CrowdedAttempts = 2;

    /// <summary>Seconds before another attempt at a place the unit could not reach.</summary>
    /// <remarks>
    /// The bound on churn when a job's place is unreachable. Costs one route query every two
    /// seconds per stranded unit instead of one per tick, and leaves the unit visibly waiting
    /// rather than visibly broken.
    /// </remarks>
    internal static float RetrySeconds = 2.0f * AgentDefaults.PaceScale;

    /// <summary>
    /// Slack beyond touching, so a body that has arrived is not one contact away from having left.
    /// </summary>
    internal static float TouchSlack = 0.25f;

    /// <summary>
    /// How much of the arrival tolerance exists because the router works in cells.
    /// </summary>
    /// <remarks>
    /// One navigation cell. A body is routed to a cell the raster says it may occupy, and the clearance
    /// beside a wall is quantised — the achievable rungs on this raster are 0.25, 0.75 and 1.25 — so the
    /// nearest cell a 0.37 m body may stand in is a whole rung out from the wall, not a body's width out.
    /// Demanding closer than that is demanding something the router cannot deliver: measured, bodies
    /// stopped a median of 0.97 m from the wall while the tolerance asked for 0.62, so <b>all nineteen of
    /// them failed to arrive, waited out a retry, and settled short instead</b> — which is the hesitation
    /// that looked like slowing down on approach. It was never avoidance; it was a five-second pause
    /// before accepting a position it was already standing in.
    /// </remarks>
    internal static float RasterReach = 0.5f;

    /// <summary>How near this body has to be to count as at its place.</summary>
    /// <remarks>
    /// Whichever is larger: a few of the body's own radii, which is what a bare point on the ground
    /// wants, or <b>touching the thing</b> — its radius plus the body's plus a little slack. A place
    /// with an extent is a building, and reaching a building means reaching its wall. Demanding its
    /// centre is what had carts shouldering into farms and jostling the hands working them, and what
    /// stopped a wagon ever quite arriving anywhere.
    /// </remarks>
    internal static float AtPlaceDistance(float radius, float placeExtent = 0f) => MathF.Max(
        radius * PlaceRadiusShare,
        placeExtent + radius + TouchSlack);

    /// <summary>How near counts once a crowd has taken the place itself.</summary>
    internal static float CrowdedPlaceDistance(float radius, float placeExtent = 0f) =>
        placeExtent + radius * PlaceCrowdShare;
}
