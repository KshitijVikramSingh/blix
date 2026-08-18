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
    /// Still longer than the deposit gain — pressure builds in about a second and fades
    /// over rather more than two — because symmetric fast response is what made the crowd
    /// oscillate: a route was abandoned, its pressure vanished immediately, and the field
    /// flipped straight back. Some reluctance to forgive a bad route is what lets the
    /// alternative actually get used.
    /// <para>
    /// It was 4.5 s, and that was too much reluctance to be useful. Nothing deposits
    /// pressure once a jam starts moving again, so everything after that is the fade
    /// tail: a cleared gap went on looking blocked for four and a half seconds, and with
    /// route adoption staggered on top, units were committing to a detour at about the
    /// moment the thing they were detouring around finished draining. Visible in play as
    /// re-deciding too late to matter, and it is also why the detour costs had to be
    /// enormous to work at all — they were bidding against pressure that no longer
    /// existed. Shortening this let those costs become honest delays, and the two changes
    /// together take the pen from four exits and 19.2 s to two exits and 13.4 s, with
    /// routes at 1.06x optimal against 1.19x.
    /// </para>
    /// </remarks>
    internal static float DecaySeconds = 2.2f;
    /// <summary>Multiplier on the deposit rate, so build stays fast despite slow decay.</summary>
    /// <remarks>
    /// Sized so a fully committed jam settles near sixteen, which at the current
    /// seconds-per-pressure is about six seconds of delay for one cell. The gain
    /// used to put the ceiling past forty — nineteen seconds to cross a single
    /// half-metre cell — which is not a claim about time, it is just a very large
    /// number, and it made every route through a crowd equally unthinkable.
    /// </remarks>
    internal static float DepositGain = 1.2f;
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
    internal static float OpenGroundShare = 0.22f;
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
    internal static float FollowingFactor = 0.90f;
    /// <summary>Cost scale for travelling against it.</summary>
    internal static float OpposingFactor = 1.8f;

    /// <summary>Pressure below which a cell is rounded to nothing and stops being visited.</summary>
    /// <remarks>
    /// Decay is exponential, so a cell that stops receiving deposits never reaches zero
    /// on its own — it would take a quarter of an hour of multiplications to underflow,
    /// and until it did, every tick would go on visiting it. Something has to declare a
    /// value spent.
    /// <para>
    /// The threshold is set by what the number is <em>for</em> rather than by what looks
    /// small. One unit of pressure is <c>PathService.CongestionSecondsPerPressure</c> —
    /// 0.4 s — of routing delay, charged at half rate per cell, so 1e-8 of pressure is
    /// 2e-9 s on an edge that costs about 0.11 s to walk. That is eight orders of
    /// magnitude below the edge and well under the last bit of a float carrying it, so
    /// adding it to a route cost cannot change the route cost. It is discarded because
    /// it provably cannot be read, not because it is nearly zero.
    /// </para>
    /// <para>
    /// The tail is long even so: a fully committed jam at sixteen takes some 47 s to
    /// fade this far. That is affordable only because pressure comes from stalled bodies
    /// and nothing else, so the live set is the ground where movement has recently
    /// failed, not the ground anyone has walked over.
    /// </para>
    /// </remarks>
    private const float SpentPressure = 1e-8f;

    private readonly GridTransform transform;
    private readonly float[] pressure;
    // Mean travel direction of whatever is depositing here, scaled by how much it
    // deposited. Kept as an unnormalised sum so that opposing contributions
    // cancel: a cell with equal traffic both ways ends up omnidirectional, which
    // is the correct reading of a genuinely contested space.
    private readonly float[] flowX;
    private readonly float[] flowZ;
    // Cells holding pressure, in ascending cell index. Everything outside this is
    // exactly zero and is skipped: decaying a zero yields a zero, adding it to the
    // total changes nothing, and it can never be the peak. Kept ordered so the decay
    // sweep visits cells in the same order a full sweep did, which is what makes the
    // accumulated total bit-for-bit the number the dense version produced.
    private int[] live = Array.Empty<int>();
    private int liveCount;
    private readonly bool[] isLive;
    // Cells that took a deposit this tick and were not already live. Sorted and merged
    // in after the deposit pass, because deposits arrive in agent order and the live
    // set has to stay in cell order.
    private int[] admitted = Array.Empty<int>();
    private int admittedCount;
    private int[] merged = Array.Empty<int>();
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
        isLive = new bool[pressure.Length];
    }

    /// <summary>Cells in the field.</summary>
    public int CellCount => pressure.Length;
    /// <summary>Cells actually holding pressure, which is what a tick costs.</summary>
    public int LiveCellCount => liveCount;

    public float At(GridCell cell) =>
        transform.Contains(cell) ? pressure[transform.Index(cell)] : 0f;

    public void Update(NavigationGrid navigation, ReadOnlySpan<AgentState> agents, float deltaSeconds)
    {
        var decay = MathF.Exp(-deltaSeconds / DecaySeconds);
        var total = 0f;
        var kept = 0;
        for (var slot = 0; slot < liveCount; slot++)
        {
            var i = live[slot];
            var value = pressure[i] * decay;
            if (value < SpentPressure)
            {
                pressure[i] = 0f;
                flowX[i] = 0f;
                flowZ[i] = 0f;
                isLive[i] = false;
                continue;
            }

            pressure[i] = value;
            flowX[i] *= decay;
            flowZ[i] *= decay;
            total += value;
            live[kept++] = i;
        }

        liveCount = kept;
        admittedCount = 0;

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
                var after = before + deposit;
                // A deposit too faint to be worth decaying is dropped outright rather
                // than left on the cell. Anything the sweep does not carry is never
                // decayed again, so a residue below the admission threshold would sit
                // there for the rest of the game — invisible in the cost, and a
                // permanent disagreement between the field and the set that sweeps it.
                if (after < SpentPressure) continue;
                pressure[index] = after;
                var applied = after - before;
                flowX[index] += intent.X * applied;
                flowZ[index] += intent.Y * applied;
                total += applied;
                if (!isLive[index])
                {
                    isLive[index] = true;
                    if (admittedCount == admitted.Length)
                    {
                        Array.Resize(ref admitted, Math.Max(64, admitted.Length * 2));
                    }

                    admitted[admittedCount++] = index;
                }
            }
        }

        MergeAdmitted();

        var peak = 0f;
        for (var slot = 0; slot < liveCount; slot++)
        {
            peak = MathF.Max(peak, pressure[live[slot]]);
        }

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
    /// Why the swept set and the field disagree, or null when they agree.
    /// </summary>
    /// <remarks>
    /// The sweep visits a tracked set rather than the whole field, so a cell that takes
    /// pressure without being admitted is never decayed again: a jam that has cleared
    /// goes on charging routes for the rest of the game, and nothing on screen says why.
    /// It is the one failure this rewrite can have that no movement metric would catch,
    /// because it needs a specific cell to be deposited on in a specific way — so it is
    /// asserted directly rather than inferred from behaviour.
    /// </remarks>
    internal string? DescribeSweepFault()
    {
        for (var slot = 1; slot < liveCount; slot++)
        {
            if (live[slot] > live[slot - 1]) continue;
            return $"swept set out of order at {slot}: cell {live[slot - 1]} then {live[slot]}";
        }

        var pressured = 0;
        for (var i = 0; i < pressure.Length; i++)
        {
            if (pressure[i] == 0f && flowX[i] == 0f && flowZ[i] == 0f)
            {
                if (isLive[i]) return $"cell {i} is swept but holds nothing";
                continue;
            }

            pressured++;
            if (!isLive[i]) return $"cell {i} holds {pressure[i]} and is never decayed";
        }

        return pressured == liveCount
            ? null
            : $"{pressured} cells hold pressure and {liveCount} are swept";
    }

    /// <summary>
    /// Folds this tick's newly pressured cells into the live set, keeping it ordered.
    /// </summary>
    /// <remarks>
    /// A sort of the whole set every tick would cost more than the sweep it is there to
    /// avoid. Only the admissions need sorting — a few cells around each stalled body —
    /// and merging two ordered runs is linear in the set, which is the same order as the
    /// sweep itself.
    /// </remarks>
    private void MergeAdmitted()
    {
        if (admittedCount == 0) return;
        Array.Sort(admitted, 0, admittedCount);

        var required = liveCount + admittedCount;
        if (merged.Length < required) merged = new int[Math.Max(64, required * 2)];

        var read = 0;
        var add = 0;
        var write = 0;
        while (read < liveCount && add < admittedCount)
        {
            merged[write++] = live[read] < admitted[add] ? live[read++] : admitted[add++];
        }

        while (read < liveCount) merged[write++] = live[read++];
        while (add < admittedCount) merged[write++] = admitted[add++];

        (live, merged) = (merged, live);
        liveCount = write;
        admittedCount = 0;
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
