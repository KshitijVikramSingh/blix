using System.Numerics;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Navigation;

/// <summary>
/// Decaying map of where movement is actually failing, used as extra traversal
/// cost by the routing layer.
/// </summary>
/// <remarks>
/// This deliberately measures <em>backpressure</em>, not density. A packed column
/// that is still advancing is healthy flow and must stay cheap, or every crowd
/// would talk itself into a detour. Only bodies that want to move and are not
/// moving deposit pressure.
/// <para>
/// It replaces the per-request cost grid the old recovery path allocated: that
/// one was a snapshot built for a single agent's replan, so nothing was shared,
/// and the global one-repath-per-2.5s cooldown meant thirty units needing an
/// alternate route would take over a minute to discover one. Pressure is
/// accumulated once per tick and read by every route built afterwards.
/// </para>
/// </remarks>
internal sealed class CongestionField
{
    /// <summary>Time for a deposit to decay to ~37% of its value.</summary>
    /// <remarks>
    /// Long relative to the deposit gain, on purpose: pressure builds in about a
    /// second and fades over several. Symmetric fast response is what made the
    /// crowd oscillate — a route was abandoned, its pressure vanished almost
    /// immediately, and the field flipped straight back. Being slow to forgive a
    /// bad route is what lets the alternative actually get used.
    /// </remarks>
    private const float DecaySeconds = 4.5f;
    /// <summary>Multiplier on the deposit rate, so build stays fast despite slow decay.</summary>
    /// <remarks>
    /// Sized so a fully committed jam settles near sixteen, which at the current
    /// seconds-per-pressure is about six seconds of delay for one cell. The gain
    /// used to put the ceiling past forty — nineteen seconds to cross a single
    /// half-metre cell — which is not a claim about time, it is just a very large
    /// number, and it made every route through a crowd equally unthinkable.
    /// </remarks>
    private const float DepositGain = 1.2f;
    // No ceiling on accumulated pressure. It is bounded naturally by the decay
    // time constant (weight * gain * decay) so it settles rather than diverging,
    // and an artificial cap became actively wrong once cost was denominated in
    // time: it made a thirty-deep queue price identically to a five-deep one, so
    // a detour that really was quicker never registered. What a jam costs is now
    // allowed to be as large as the jam actually is.
    /// <summary>Smallest absolute change that can justify rebuilding routes.</summary>
    private const float RebuildThreshold = 6f;
    // A threshold proportional to the published total was tried here, to make
    // "enough has changed" scale with crowd size. It barely reduced rebuild churn
    // (15 to 14 per 120 ticks) and it suppressed exactly the field updates that
    // let a crowd split across two gaps, so the symmetric two-gap case regressed
    // to a single file. Route churn is not what makes movement look erratic —
    // every member adopting a new field on the same tick is, and that is damped
    // in the steering layer instead.
    /// <summary>Minimum ticks between route rebuilds, so a churning jam cannot thrash them.</summary>
    private const int RebuildIntervalTicks = 8;
    /// <summary>Deposit for a body that no amount of pushing will move.</summary>
    private const float ImmovableBodyWeight = 3f;
    /// <summary>Share of a deposit that survives when the ground is wide open.</summary>
    // Raising this to make routes go around a standing crowd rather than through
    // it was tried at 0.45 and 0.70: both were slower than leaving it low (pen
    // totals 42.1s and 44.2s against 36.7s), because penalising open ground
    // enough to deflect a route also re-creates the saturated blob that leaves a
    // jam with no internal gradient. Route commitment is what actually stops a
    // unit setting off through a crowd and changing its mind halfway.
    private const float OpenGroundShare = 0.22f;
    /// <summary>Cost scale for travelling with the local flow.</summary>
    /// <remarks>
    /// Barely a discount, and deliberately so. A generous one (0.35 was tried)
    /// silently cancels the whole queue signal: the field only ever accumulates
    /// from bodies that are stalled, so pressure already *is* backpressure, and
    /// the queue at a chokepoint is by definition pointing the same way as the
    /// unit about to join it. Discounting that meant a two-second wait was priced
    /// at under one, which is less than the detour it should have triggered.
    /// Joining a stopped queue means inheriting its wait, whichever way it faces.
    /// The directional term exists to penalise opposing traffic, not to reward
    /// following.
    /// </remarks>
    private const float FollowingFactor = 0.90f;
    /// <summary>Cost scale for travelling against it.</summary>
    private const float OpposingFactor = 1.8f;

    private readonly GridTransform transform;
    private readonly float[] pressure;
    // Mean travel direction of whatever is depositing here, scaled by how much it
    // deposited. Kept as an unnormalised sum so that opposing contributions
    // cancel: a cell with equal traffic both ways ends up omnidirectional, which
    // is the correct reading of a genuinely contested space.
    private readonly float[] flowX;
    private readonly float[] flowZ;
    private float publishedTotal;
    private float currentTotal;
    private int ticksSinceRebuild;

    /// <summary>Bumped when the field has changed enough to be worth re-routing on.</summary>
    public int Revision { get; private set; }
    /// <summary>Highest pressure currently on any cell, for diagnostics.</summary>
    public float Peak { get; private set; }

    public CongestionField(GridTransform transform)
    {
        this.transform = transform;
        pressure = new float[transform.Width * transform.Height];
        flowX = new float[pressure.Length];
        flowZ = new float[pressure.Length];
    }

    public float At(GridCell cell) =>
        transform.Contains(cell) ? pressure[transform.Index(cell)] : 0f;

    public void Update(NavigationGrid navigation, ReadOnlySpan<AgentState> agents, float deltaSeconds)
    {
        var decay = MathF.Exp(-deltaSeconds / DecaySeconds);
        var total = 0f;
        for (var i = 0; i < pressure.Length; i++)
        {
            pressure[i] *= decay;
            flowX[i] *= decay;
            flowZ[i] *= decay;
            total += pressure[i];
        }

        foreach (ref readonly var agent in agents)
        {
            if (!agent.IsAlive) continue;
            var weight = DepositWeight(agent, navigation);
            if (weight <= 0f) continue;
            if (!transform.TryWorldToCell(agent.Position, out var center)) continue;
            // Intent, not velocity: a body that is stuck has almost no velocity,
            // but the direction it is trying to go is exactly what makes this
            // patch expensive for others going the same way and cheap for
            // those going with it.
            var intent = agent.PreferredVelocity.LengthSquared() > 0.0001f
                ? Vector2.Normalize(agent.PreferredVelocity)
                : Vector2.Zero;

            var influence = agent.Radius + 0.60f;
            var cellRadius = Math.Max(1, (int)MathF.Ceiling(influence / transform.CellSize));
            for (var z = center.Z - cellRadius; z <= center.Z + cellRadius; z++)
            for (var x = center.X - cellRadius; x <= center.X + cellRadius; x++)
            {
                var cell = new GridCell(x, z);
                if (!transform.Contains(cell)) continue;
                var distance = Vector2.Distance(transform.CellCenter(cell), agent.Position);
                if (distance >= influence) continue;
                var falloff = 1f - distance / influence;
                var index = transform.Index(cell);
                var before = pressure[index];
                var deposit = weight * DepositGain * falloff * falloff * deltaSeconds;
                pressure[index] = before + deposit;
                var applied = pressure[index] - before;
                flowX[index] += intent.X * applied;
                flowZ[index] += intent.Y * applied;
                total += applied;
            }
        }

        var peak = 0f;
        foreach (var value in pressure) peak = MathF.Max(peak, value);
        Peak = peak;
        currentTotal = total;
        ticksSinceRebuild++;
        // Routes are only allowed to notice the field on a slow cadence. Letting
        // every tick invalidate them would rebuild a Dijkstra per group per tick
        // and let a crowd oscillate between two exits at 30 Hz.
        if (ticksSinceRebuild < RebuildIntervalTicks) return;
        if (MathF.Abs(currentTotal - publishedTotal) < RebuildThreshold) return;
        publishedTotal = currentTotal;
        ticksSinceRebuild = 0;
        Revision++;
    }

    /// <summary>
    /// Multiplier on this cell's pressure for something travelling
    /// <paramref name="direction"/> through it.
    /// </summary>
    /// <remarks>
    /// Congestion is not a property of a place, it is a property of a place and a
    /// heading. Falling in behind a queue going your way costs you the queue's
    /// speed and nothing more, while pushing into a stream coming the other way
    /// is genuinely expensive — and treating both identically makes two groups
    /// passing each other both detour around the other for no reason. A cell with
    /// balanced two-way traffic cancels to omnidirectional and is charged in full.
    /// </remarks>
    public float DirectionalFactor(GridCell cell, Vector2 direction)
    {
        if (!transform.Contains(cell)) return 1f;
        var index = transform.Index(cell);
        var magnitude = MathF.Sqrt(flowX[index] * flowX[index] + flowZ[index] * flowZ[index]);
        var strength = pressure[index];
        if (magnitude <= 0.0001f || strength <= 0.0001f) return 1f;

        // How one-directional this cell's traffic actually is.
        var coherence = MathF.Min(1f, magnitude / strength);
        var alignment = (flowX[index] * direction.X + flowZ[index] * direction.Y) / magnitude;
        var directional = float.Lerp(OpposingFactor, FollowingFactor, (alignment + 1f) * 0.5f);
        return float.Lerp(1f, directional, coherence);
    }

    /// <summary>
    /// How strongly a body is reporting that this patch of ground does not work.
    /// </summary>
    private static float DepositWeight(in AgentState agent, NavigationGrid navigation)
    {
        // A body that cannot be shoved aside is a hole in the map, not traffic.
        // Routing only knows about terrain and placement blocks, so without this
        // an immovable unit parked in a doorway is invisible to every route ever
        // built through it: the crowd walks up to it, queues politely, and waits
        // there forever because nothing in the system can see why it is stuck.
        // Shovable allies are deliberately excluded — a unit can simply walk
        // through those, so they are not an obstruction worth detouring around.
        if (agent.MaximumSpeed <= 0f) return ImmovableBodyWeight * Constriction(agent, navigation);

        if (!agent.HasDestination) return 0f;
        // A body with no intent to move is not congestion.
        if (agent.PreferredVelocity.LengthSquared() <= 0.0625f) return 0f;
        var weight = 0f;
        if (agent.StuckSeconds > 0f) weight += MathF.Min(2f, agent.StuckSeconds * 2f);
        if (agent.AvoidanceBlockedThisTick) weight += 1.25f;
        return MathF.Min(3f, weight) * Constriction(agent, navigation);
    }

    /// <summary>
    /// How much a body stalled here actually obstructs, from 
    /// <see cref="OpenGroundShare"/> in the open to one in a gap.
    /// </summary>
    /// <remarks>
    /// Without this a crowd poisons its own escape route. Every member of a jam
    /// deposits at full strength on the ground it is standing on, so the whole
    /// blob saturates uniformly and there is no gradient left inside it — backing
    /// out to a gap two metres behind you prices identically to shoving forward
    /// through the one everybody else is using. Only the body at the very edge of
    /// the blob has a route that escapes the saturation, which is why exactly one
    /// unit would re-route and the rest kept waiting their turn.
    /// <para>
    /// A body stopped in open ground is a transient nuisance that can be walked
    /// around or shoved aside; a body stopped in a doorway is the reason nobody
    /// is getting through. Only the second should be steering routes.
    /// </para>
    /// </remarks>
    private static float Constriction(in AgentState agent, NavigationGrid navigation)
    {
        if (!navigation.TryWorldToCell(agent.Position, out var cell)) return OpenGroundShare;
        var clearance = navigation.Clearance(cell);
        var tight = agent.Radius * 2f;
        var open = agent.Radius * 5f;
        if (clearance <= tight) return 1f;
        if (clearance >= open) return OpenGroundShare;
        var openness = (clearance - tight) / (open - tight);
        return float.Lerp(1f, OpenGroundShare, openness);
    }
}
