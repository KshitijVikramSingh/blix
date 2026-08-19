using System.Numerics;
using System.Reflection;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Movement;
using RTSGame.Simulation.Spatial;

namespace RTSGame.Debug;

/// <summary>
/// Compares two runs of the same simulation, over everything either of them carries
/// forward rather than over the handful of fields somebody remembered to name.
/// </summary>
/// <remarks>
/// The determinism check this replaces compared three to five fields of an
/// <see cref="AgentState"/> that has around seventy, at the last tick of the run, and
/// nothing at all above the level of a body. That is not a small gap. A divergence in
/// <c>RouteAdoptionDelay</c>, in a congestion cell's flow, in which id the next group
/// order takes, or in how many route plans a tick had left would all pass it — until,
/// some hundreds of ticks later, it happened to move somebody. Worse, the failure it
/// eventually reported would name a position at the end of the run, which is the least
/// useful place to be told about it.
/// <para>
/// So this does two things differently. It walks the state from a schema derived from
/// the state itself (see <see cref="AgentStateSchema"/>), which is what makes the check
/// grow when a system does rather than when somebody remembers. And it steps the two
/// worlds together, comparing every tick, so a mismatch is reported at the tick it
/// happened and named down to the field.
/// </para>
/// <para>
/// The ledger below is the other half. Every instance field of
/// <see cref="SimulationWorld"/> is classified as carried, derived or wall clock, and a
/// field that is none of those fails <see cref="CensusFault"/> by name. Adding state to
/// the world without saying which it is is the mistake this is here to make impossible,
/// because that mistake does not announce itself: an unfingerprinted field is simply a
/// hole in a test that goes on passing.
/// </para>
/// </remarks>
internal static class DeterminismCheck
{
    /// <summary>How much of the state to read.</summary>
    internal enum Scope
    {
        /// <summary>
        /// Everything carried on a body, plus the world's own scalars, counters and live
        /// group orders. Cheap enough to compare on every tick, which is what localises a
        /// divergence in time.
        /// </summary>
        Tick,

        /// <summary>
        /// Adds the bulk: stored routes, congestion cells and region stamps, the navigation
        /// raster, terrain heights and surfaces, colliders and formation slots. Proportional
        /// to the map rather than to the population, so it is compared at checkpoints.
        /// </summary>
        Full,
    }

    /// <summary>
    /// Fields the fingerprint reads. Named here as well as walked, so the census can tell
    /// a field that is covered from one that has simply not been thought about.
    /// </summary>
    private static readonly HashSet<string> Carried = new()
    {
        "SimulationWorld.Agents", "SimulationWorld.Terrain", "SimulationWorld.Placement",
        "SimulationWorld.Navigation", "SimulationWorld.Congestion", "SimulationWorld.Colliders",
        "SimulationWorld.TickNumber", "SimulationWorld.EpochTicks",
        "SimulationWorld.LastContactCount",
        "SimulationWorld.CongestionRepathCount", "SimulationWorld.ImmediateRouteRepairCount",
        "SimulationWorld.CongestionRerouteCount", "SimulationWorld.CrowdedArrivalBlockCount",
        "SimulationWorld.LastCongestionRoot", "SimulationWorld.LastCongestionRepathAgent",
        "SimulationWorld.commands", "SimulationWorld.paths", "SimulationWorld.moveGroups",
        "SimulationWorld.nextMoveGroupId", "SimulationWorld.blockColliders",
        "SimulationWorld.congestionRecoveryCooldown",
        "SimulationWorld.rasterizedTerrainRevision", "SimulationWorld.routePlansThisTick",
        "SimulationWorld.ExtentMeters", "SimulationWorld.Nodes", "SimulationWorld.economy",
        "EconomySystem.Produced", "EconomySystem.Consumed", "EconomySystem.Seeded",
        "EconomySystem.Unmet", "EconomySystem.HaulsAssigned", "EconomySystem.HaulsAbandoned",
        "EconomySystem.boardCooldown",
        "MoveGroup.Id", "MoveGroup.Target", "MoveGroup.Members", "MoveGroup.Slots",
        "MoveGroup.FormationRadius", "MoveGroup.SettlingTicks", "MoveGroup.TransitCentroid",
        "MoveGroup.HasTransitCentroid", "MoveGroup.TransitFlow",
    };

    /// <summary>
    /// Fields rebuilt from carried state before anything reads them. A divergence in one of
    /// these can only reach the next tick through something in <see cref="Carried"/>, and
    /// the reason it can is the value here — which is the part worth reviewing.
    /// </summary>
    private static readonly Dictionary<string, string> Derived = new()
    {
        ["SimulationWorld.pathService"] =
            "caches keyed by the revisions of terrain, navigation and congestion, all three of " +
            "which are fingerprinted; its work counters are read through the world and are.",
        ["SimulationWorld.steeringSystem"] =
            "per-tick scratch. Its solver's counters are fingerprinted through the world, which " +
            "is the part that carries: a run that solved a different number of times has " +
            "already diverged in a decision.",
        ["SimulationWorld.collisionSystem"] = "per-tick scratch; contact results land on bodies within the tick.",
        ["SimulationWorld.agentIndex"] = "rebuilt from body positions every tick, and those are fingerprinted.",
        ["SimulationWorld.placementHits"] = "query result buffer, refilled before each read.",
        ["SimulationWorld.holdPositionHits"] = "query result buffer, refilled before each read.",
        ["EconomySystem.tasks"] =
            "rebuilt from scratch by every pass of the hauling board, before anything reads it.",
        ["EconomySystem.claimed"] =
            "rebuilt by every pass of the board from which carts are already hauling, and that is on " +
            "the carts.",
        ["EconomySystem.idleHaulers"] =
            "rebuilt from the bodies by every pass of the board; who is idle is a fact about the " +
            "bodies, and those are fingerprinted.",
    };

    /// <summary>
    /// Fields that must never be fingerprinted. Wall clock differs between two runs of
    /// identical code by definition, so reading it here would make the check fail at random
    /// and be switched off — which is the worst of the available outcomes.
    /// </summary>
    private static readonly Dictionary<string, string> WallClock = new()
    {
        ["SimulationWorld.Timings"] = "measured nanoseconds; nothing in the simulation reads them back.",
        ["SimulationWorld.pathfindingTicksThisTick"] =
            "measured nanoseconds, accumulated for the overlay. Not a budget — the route plan " +
            "budget is routePlansThisTick, which is a count and is fingerprinted.",
    };

    /// <summary>
    /// Types the census walks field by field. Anything a carried field points at is either
    /// one of these, plain data, or has a line in <see cref="Boundaries"/>.
    /// </summary>
    private static readonly Type[] Censused =
        { typeof(SimulationWorld), typeof(MoveGroup), typeof(EconomySystem) };

    /// <summary>
    /// Subsystems read through a named surface instead of field by field, and what that
    /// surface is.
    /// </summary>
    /// <remarks>
    /// This is where the census stops, and stopping somewhere is the point: walking every
    /// private array of the congestion field or the navigation raster would be a second
    /// implementation of those subsystems living in a test. What each entry has to earn is
    /// the claim that the surface named is enough — that no difference can hide behind it and
    /// still reach a decision. Those are one-line arguments and they are reviewable, which is
    /// the most that can honestly be said for them.
    /// <para>
    /// A subsystem added without an entry here fails the census by name, in the session that
    /// added it. That is the whole mechanism: the jobs layer cannot hang a job board off the
    /// world without being asked, once, how the determinism check is supposed to see it.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> Boundaries = new()
    {
        ["AgentStore"] =
            "every slot including tombstones, each walked in full by AgentStateSchema, plus " +
            "its slot and live counts.",
        ["PathPool"] =
            "the routes bodies are actually holding, read through the bodies. Its free list " +
            "is not read and does not need to be: its only effect is which handle the next " +
            "route gets, and that lands on AgentState.Path.",
        ["TerrainMap"] =
            "its revision every tick; its surfaces and vertex heights at a checkpoint.",
        ["PlacementGrid"] = "its revision and occupied cells.",
        ["NavigationGrid"] =
            "its revision every tick; every cell's blocked flag, clearance, height, cost and " +
            "speed at a checkpoint.",
        ["CongestionField"] =
            "revision, peak, and every cell holding pressure with its flow — which is the " +
            "whole of the field that is not exactly zero — plus the region stamps at a " +
            "checkpoint. Its running totals reach a route only through the revision.",
        ["ColliderWorld"] =
            "every proxy ever added, in id order, with its owner, shape, centre and enabled " +
            "flag. Its hashes and partitions are rebuilt from those.",
        ["AgentCommand"] = "walked in full by PlainDataWalker, whatever kind of order it is.",
        ["NodeStore"] =
            "every slot including tombstones, each walked in full by PlainDataWalker because a node " +
            "is plain data, plus its slot count, live count and revision.",
    };

    private const BindingFlags Instance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>One number standing for everything the world carries at this instant.</summary>
    /// <param name="includeWork">
    /// Whether to read the counters of work done — routes planned, fields built, solves run. True
    /// when comparing two live runs, where identical code must do identical work and a counter is the
    /// earliest sign of a decision diverging. <b>False when comparing a world against a saved copy of
    /// itself</b>, because a loaded world resumes with a cold flow-field cache and has to redo work
    /// the original had already done. That is a true difference between the two <em>processes</em> and
    /// not between the two worlds, and saving the counters to hide it — which was tried — only moves
    /// the disagreement to the first tick.
    /// </param>
    public static ulong Fingerprint(SimulationWorld world, Scope scope, bool includeWork = true)
    {
        var sink = new FoldingStateSink();
        Walk(world, scope, includeWork, ref sink);
        return sink.Value;
    }

    /// <summary>Every value the fingerprint is made of, labelled, for explaining a mismatch.</summary>
    public static List<(string Label, ulong Value)> Trace(
        SimulationWorld world,
        Scope scope,
        bool includeWork = true)
    {
        var sink = new TracingStateSink();
        Walk(world, scope, includeWork, ref sink);
        return sink.Entries;
    }

    /// <summary>
    /// Steps both worlds forward together and returns a description of the first tick they
    /// disagree on, or null if they never do.
    /// </summary>
    /// <remarks>
    /// The <see cref="Scope.Tick"/> comparison runs every tick and the
    /// <see cref="Scope.Full"/> one every <paramref name="fullEvery"/> ticks and at the end,
    /// which is the trade: a divergence on a body or a world scalar is caught on the tick it
    /// appears, and one confined to the map or a stored route is caught within a checkpoint
    /// of it. Both are transformative on being told about it at tick 600.
    /// </remarks>
    /// <param name="afterTick">
    /// Runs after both worlds have stepped and before they are compared, for the one caller
    /// that needs to introduce a difference at a chosen tick: the probe that checks this
    /// reports the tick a divergence appeared on rather than the tick somebody noticed.
    /// </param>
    public static string? Diverges(
        SimulationWorld first,
        SimulationWorld second,
        int ticks,
        int fullEvery = 30,
        Action<int>? afterTick = null,
        bool includeWork = true)
    {
        // Before anything moves. A scenario that populated two worlds differently would
        // otherwise be reported as a divergence at tick 1, which sends the reader looking
        // at the tick instead of at the setup.
        if (Fingerprint(first, Scope.Full, includeWork) != Fingerprint(second, Scope.Full, includeWork))
        {
            return $"before the first tick: {Explain(first, second, Scope.Full, includeWork)}";
        }

        for (var tick = 1; tick <= ticks; tick++)
        {
            first.Tick((float)SimulationWorld.FixedDeltaSeconds);
            second.Tick((float)SimulationWorld.FixedDeltaSeconds);
            afterTick?.Invoke(tick);

            if (Fingerprint(first, Scope.Tick, includeWork) != Fingerprint(second, Scope.Tick, includeWork))
            {
                return $"tick {tick}: {Explain(first, second, Scope.Tick, includeWork)}";
            }

            if (tick % fullEvery != 0 && tick != ticks) continue;
            if (Fingerprint(first, Scope.Full, includeWork) != Fingerprint(second, Scope.Full, includeWork))
            {
                return $"tick {tick} (full): {Explain(first, second, Scope.Full, includeWork)}";
            }
        }

        return null;
    }

    /// <summary>
    /// Returns a description of any field of <see cref="SimulationWorld"/> or
    /// <see cref="MoveGroup"/> the ledger above has not classified, or null if every one is
    /// accounted for.
    /// </summary>
    /// <remarks>
    /// This is the alarm. State added to the world without a line in the ledger is state the
    /// determinism check does not read, and nothing else would notice — so this fails in the
    /// same session that adds it, which is the only session in which the author knows
    /// whether the field is carried or derived.
    /// </remarks>
    public static string? CensusFault()
    {
        foreach (var type in Censused)
        foreach (var field in type.GetFields(Instance))
        {
            if (field.IsStatic) continue;
            var name = $"{type.Name}.{Normalise(field.Name)}";
            if (Derived.ContainsKey(name) || WallClock.ContainsKey(name)) continue;
            if (!Carried.Contains(name))
            {
                return $"{name} ({field.FieldType.Name}) is not in the determinism " +
                       "ledger. Add it to DeterminismCheck: to Carried and to the walk if the " +
                       "next tick can read it, to Derived with the reason it is rebuilt first, " +
                       "or to WallClock if fingerprinting it would make two identical runs " +
                       "differ.";
            }

            // A carried field is only as covered as the thing it points at. This is the layer
            // the game systems will land on, so it is the layer worth holding.
            if (Unaccounted(field.FieldType) is { } stranger)
            {
                return $"{name} carries a {stranger}, which the determinism check has no " +
                       "account of. Either add it to Censused and walk its fields, or add it " +
                       "to Boundaries naming the surface the fingerprint reads it through.";
            }
        }

        return null;
    }

    /// <summary>
    /// The name of the first type inside <paramref name="type"/> that is neither plain data,
    /// nor censused, nor given a boundary — or null if everything in there is accounted for.
    /// </summary>
    /// <remarks>
    /// Containers are looked through rather than at: a dictionary of group orders is not
    /// itself interesting, and what matters is that both the key and the group are.
    /// </remarks>
    private static string? Unaccounted(Type type)
    {
        if (type.IsArray) return Unaccounted(type.GetElementType()!);
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                if (Unaccounted(argument) is { } stranger) return stranger;
            }

            return null;
        }

        if (AgentStateSchema.IsPlainData(type)) return null;
        if (Boundaries.ContainsKey(type.Name)) return null;
        return Censused.Any(censused => censused == type) ? null : type.Name;
    }

    /// <summary>Fields of a body the fingerprint reads, for reporting what the coverage is.</summary>
    public static int BodyFieldCount => AgentStateSchema.Leaves.Count;

    public static string Explain(
        SimulationWorld first,
        SimulationWorld second,
        Scope scope,
        bool includeWork = true)
    {
        var left = Trace(first, scope, includeWork);
        var right = Trace(second, scope, includeWork);
        for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            if (left[i].Label != right[i].Label)
            {
                return $"the two runs hold different state at position {i}: " +
                       $"{left[i].Label} against {right[i].Label}";
            }

            if (left[i].Value == right[i].Value) continue;
            return $"{left[i].Label} {Describe(left[i].Value)} against {Describe(right[i].Value)}";
        }

        return left.Count == right.Count
            // Cannot happen while the fingerprints disagree, and worth saying so rather than
            // reporting nothing: it would mean the fold sees something the trace does not.
            ? "the fingerprints disagree but every traced value matches — the walk and the fold " +
              "have come apart"
            : $"the two runs hold a different amount of state: {left.Count} values against {right.Count}";
    }

    /// <summary>
    /// Renders a fingerprinted value both ways. Almost every one of them is a float's bits
    /// or a small integer, and which of those it is depends on the field, so print both and
    /// let the reader pick.
    /// </summary>
    private static string Describe(ulong value) => value <= uint.MaxValue
        ? $"0x{value:x8} ({BitConverter.UInt32BitsToSingle((uint)value):G9} as a float, " +
          $"{(int)(uint)value} as an integer)"
        : $"0x{value:x16} ({(long)value})";

    private static string Normalise(string name)
    {
        if (!name.StartsWith('<')) return name;
        var end = name.IndexOf('>');
        return end > 1 ? name[1..end] : name;
    }

    /// <summary>
    /// Counters of work done. State only in the sense that identical code must do identical work —
    /// nothing reads them back — which makes them the earliest place a diverged decision shows up and
    /// the one part of the walk a saved world legitimately disagrees about.
    /// </summary>
    private static void WriteWorkCounters<TSink>(SimulationWorld world, ref TSink sink)
        where TSink : struct, IStateSink
    {
        sink.Add("CongestionRepathCount", world.CongestionRepathCount);
        sink.Add("ImmediateRouteRepairCount", world.ImmediateRouteRepairCount);
        sink.Add("CongestionRerouteCount", world.CongestionRerouteCount);
        sink.Add("CrowdedArrivalBlockCount", world.CrowdedArrivalBlockCount);
        sink.Add("FlowFieldBuilds", world.FlowFieldBuilds);
        sink.Add("PathQueries", world.PathQueries);
        sink.Add("RegionSearches", world.RegionSearches);
        sink.Add("TileRefinements", world.TileRefinements);
        sink.Add("AvoidanceSolves", world.AvoidanceSolves);
        sink.Add("AvoidanceInfeasible", world.AvoidanceInfeasible);
        sink.Add("AvoidanceTerrainFallbacks", world.AvoidanceTerrainFallbacks);
        sink.Add("AvoidanceTerrainDeadStops", world.AvoidanceTerrainDeadStops);
        sink.Add("AgentIndexRebuildsPerTick", world.AgentIndexRebuildsPerTick);
    }

    private static void Walk<TSink>(
        SimulationWorld world,
        Scope scope,
        bool includeWork,
        ref TSink sink)
        where TSink : struct, IStateSink
    {
        sink.Push("world", -1);
        sink.Add("TickNumber", world.TickNumber);
        sink.Add("EpochTicks", world.EpochTicks);
        sink.Add("ExtentMeters", world.ExtentMeters);
        sink.Add("Slots", world.Agents.Count);
        sink.Add("LiveBodies", world.Agents.LiveCount);
        sink.Add("PendingCommands", world.PendingCommands.Count);
        sink.Add("NextMoveGroupId", world.NextMoveGroupId);
        sink.Add("RoutePlansThisTick", world.RoutePlansThisTick);
        sink.Add("CongestionRecoveryCooldown", world.CongestionRecoveryCooldown);
        sink.Add("RasterizedTerrainRevision", world.RasterizedTerrainRevision);
        sink.Add("TerrainRevision", world.Terrain.Revision);
        sink.Add("NavigationRevision", world.Navigation.Revision);
        sink.Add("PlacementRevision", world.Placement.Revision);
        sink.Add("OccupiedCells", world.Placement.OccupiedCells.Count);
        sink.Add("BlockedCells", world.BlockColliders.Count);
        sink.Add("ColliderCount", world.Colliders.All.Count);
        sink.Add("Nodes", world.Nodes.Count);
        sink.Add("LiveNodes", world.Nodes.LiveCount);
        sink.Add("NodeRevision", world.Nodes.Revision);
        sink.Add("LastCongestionRoot", world.LastCongestionRoot.Value);
        sink.Add("LastCongestionRepathAgent", world.LastCongestionRepathAgent.Value);

        // Work counters. Not state in the sense that anything reads them back, but two runs
        // of identical code have to do identical work, and a decision that diverged without
        // moving anybody yet shows up here first — which is a tick or several before it shows
        // up anywhere else.
        sink.Push("work", -1);
        sink.Add("LastContactCount", world.LastContactCount);
        if (includeWork) WriteWorkCounters(world, ref sink);

        // Orders accepted this tick and applied on the next one. Walked in full rather than
        // counted: two runs holding a different order in the same slot would otherwise be
        // identical here and diverge a tick later, which is a tick's worth of looking in the
        // wrong place — and the jobs layer is about to put a great deal more through here.
        sink.Push("command", -1);
        foreach (var command in world.PendingCommands)
        {
            PlainDataWalker.Write(ref sink, "order", command);
        }

        WriteEconomy(world, ref sink);

        var bodies = world.Agents.All;
        for (var slot = 0; slot < bodies.Length; slot++)
        {
            AgentStateSchema.Write(ref sink, bodies[slot], slot);
        }

        WriteMoveGroups(world, scope, ref sink);
        WriteCongestion(world, scope, ref sink);
        if (scope == Scope.Tick) return;

        WriteRoutes(world, ref sink);
        WriteMap(world, ref sink);
        WriteColliders(world, ref sink);
    }

    /// <summary>
    /// The nodes and the ledgers, which between them are the whole economy's state.
    /// </summary>
    /// <remarks>
    /// Nodes go through <see cref="PlainDataWalker"/> rather than a hand-written field list, for the
    /// same reason a body goes through a compiled schema: a node is plain data, so what it carries is
    /// read off the struct and a field added by the trade layer is compared without anybody being asked.
    /// The ledgers are read as well as the stock, because they are what conservation is checked against
    /// and a difference in them is a unit produced or eaten in one run and not the other.
    /// </remarks>
    private static void WriteEconomy<TSink>(SimulationWorld world, ref TSink sink)
        where TSink : struct, IStateSink
    {
        sink.Push("economy", -1);
        sink.Add("BoardCooldown", world.Economy.BoardCooldown);
        sink.Add("HaulsAssigned", world.Economy.HaulsAssigned);
        sink.Add("HaulsAbandoned", world.Economy.HaulsAbandoned);
        foreach (var resource in Resources.All)
        {
            sink.Add("Produced", world.Economy.Produced[resource]);
            sink.Add("Consumed", world.Economy.Consumed[resource]);
            sink.Add("Seeded", world.Economy.Seeded[resource]);
            sink.Add("Unmet", world.Economy.Unmet[resource]);
        }

        var nodes = world.Nodes.All;
        for (var slot = 0; slot < nodes.Length; slot++)
        {
            sink.Push("node", slot);
            PlainDataWalker.Write(ref sink, "node", nodes[slot]);
        }
    }

    /// <summary>
    /// Group orders, in id order rather than in whatever order the dictionary holds them.
    /// </summary>
    /// <remarks>
    /// Two runs that created and retired the same groups will enumerate them identically, so
    /// sorting is not what makes this deterministic — it is what stops a difference in
    /// <em>which</em> groups exist from being reported as a difference in every group after
    /// it.
    /// </remarks>
    private static void WriteMoveGroups<TSink>(SimulationWorld world, Scope scope, ref TSink sink)
        where TSink : struct, IStateSink
    {
        foreach (var id in world.MoveGroups.Keys.OrderBy(id => id))
        {
            var group = world.MoveGroups[id];
            sink.Push("group", id);
            sink.Add("Id", group.Id);
            sink.Add("Target", group.Target);
            sink.Add("Members", group.Members.Length);
            sink.Add("FormationRadius", group.FormationRadius);
            sink.Add("SettlingTicks", group.SettlingTicks);
            sink.Add("TransitCentroid", group.TransitCentroid);
            sink.Add("HasTransitCentroid", group.HasTransitCentroid);
            sink.Add("TransitFlow", group.TransitFlow);
            if (scope == Scope.Tick) continue;

            for (var member = 0; member < group.Members.Length; member++)
            {
                sink.Add("Member", group.Members[member].Value);
                sink.Add("Slot", group.Slots[member]);
            }
        }
    }

    /// <summary>
    /// The congestion field, through the live set — which is both the whole of it that is
    /// not exactly zero and the order the decay sweep visits it in.
    /// </summary>
    /// <remarks>
    /// Pressure and flow are read separately because they diverge separately: two worlds can
    /// agree on how jammed every cell is and disagree about which way the jam faces, and
    /// that reaches a route through the directional factor without touching a body first.
    /// The field's private accumulators are not read here and do not need to be — their only
    /// route to a decision is the published revision and the region stamps, and those are.
    /// </remarks>
    private static void WriteCongestion<TSink>(SimulationWorld world, Scope scope, ref TSink sink)
        where TSink : struct, IStateSink
    {
        var congestion = world.Congestion;
        sink.Push("congestion", -1);
        sink.Add("Revision", congestion.Revision);
        sink.Add("Peak", congestion.Peak);
        sink.Add("LiveCellCount", congestion.LiveCellCount);
        foreach (var index in congestion.LiveCells)
        {
            var cell = congestion.CellOf(index);
            sink.Add("Cell", index);
            sink.Add("Pressure", congestion.At(cell));
            sink.Add("Flow", congestion.Flow(cell));
        }

        if (scope == Scope.Tick) return;
        for (var region = 0; region < congestion.RegionCount; region++)
        {
            sink.Add("RegionStamp", congestion.RegionStamp(region));
            sink.Add("RegionPressured", congestion.RegionHasPressure(region));
        }
    }

    /// <summary>
    /// Stored routes, read through the bodies holding them rather than out of the pool.
    /// </summary>
    /// <remarks>
    /// What matters about a route is the part not yet walked, which is what steers; the
    /// waypoints behind the body are dead and the handle itself is fingerprinted as a field.
    /// This also covers the pool's free list without reading it: a run that freed handles in
    /// a different order hands out a different handle next, and that lands on
    /// <c>AgentState.Path</c>.
    /// </remarks>
    private static void WriteRoutes<TSink>(SimulationWorld world, ref TSink sink)
        where TSink : struct, IStateSink
    {
        var bodies = world.Agents.All;
        for (var slot = 0; slot < bodies.Length; slot++)
        {
            if (!bodies[slot].IsAlive || !bodies[slot].Path.IsValid) continue;
            sink.Push("route", slot);
            var remaining = world.GetRemainingPath(new AgentId(slot));
            sink.Add("Waypoints", remaining.Length);
            foreach (var waypoint in remaining) sink.Add("Waypoint", waypoint);
        }
    }

    /// <summary>The ground: terrain as authored, and the navigation raster built from it.</summary>
    /// <remarks>
    /// Both, deliberately. The raster is derived from the terrain and the placement grid, so
    /// hashing it as well is redundant right up until the moment the rebuild is wrong, which
    /// is the moment worth catching — and dynamic rebuild granularity is an open question
    /// (plan-rts-game.md, debt 6), so the redundancy is aimed at something real.
    /// </remarks>
    private static void WriteMap<TSink>(SimulationWorld world, ref TSink sink)
        where TSink : struct, IStateSink
    {
        var navigation = world.Navigation;
        sink.Push("raster", -1);
        for (var z = 0; z < navigation.Height; z++)
        for (var x = 0; x < navigation.Width; x++)
        {
            var cell = new GridCell(x, z);
            sink.Add("Blocked", navigation.IsBlocked(cell));
            sink.Add("Clearance", navigation.Clearance(cell));
            sink.Add("Height", navigation.HeightAt(cell));
            sink.Add("Cost", navigation.TraversalCost(cell));
            sink.Add("Speed", navigation.SpeedMultiplier(cell));
        }

        var terrain = world.Terrain;
        sink.Push("terrain", -1);
        for (var z = 0; z < terrain.Transform.Height; z++)
        for (var x = 0; x < terrain.Transform.Width; x++)
        {
            sink.Add("Surface", (int)terrain.Surface(new GridCell(x, z)));
        }

        // One more vertex than cell in each direction; the heights are the corners.
        for (var z = 0; z <= terrain.Transform.Height; z++)
        for (var x = 0; x <= terrain.Transform.Width; x++)
        {
            sink.Add("VertexHeight", terrain.VertexHeight(x, z));
        }

        // Built ground twice over: the placement grid's own record of it, and the collider
        // standing in for each block. They are written by the same call and a difference
        // between them would be a bug in that call, which is a reason to read both rather
        // than a reason to trust either.
        sink.Push("blocks", -1);
        foreach (var cell in world.Placement.OccupiedCells)
        {
            sink.Add("Occupied", world.Placement.Transform.Index(cell));
        }

        foreach (var cell in world.BlockColliders.Keys
                     .OrderBy(cell => world.Placement.Transform.Index(cell)))
        {
            sink.Add("Cell", world.Placement.Transform.Index(cell));
            sink.Add("Collider", world.BlockColliders[cell].Value);
        }
    }

    private static void WriteColliders<TSink>(SimulationWorld world, ref TSink sink)
        where TSink : struct, IStateSink
    {
        var colliders = world.Colliders.All;
        for (var i = 0; i < colliders.Count; i++)
        {
            var proxy = colliders[i];
            sink.Push("collider", i);
            sink.Add("Id", proxy.Id.Value);
            sink.Add("OwnerKind", (int)proxy.Owner.Kind);
            sink.Add("OwnerValue", proxy.Owner.Value);
            sink.Add("Faction", proxy.Faction.Value);
            sink.Add("Layer", (int)proxy.Layer);
            sink.Add("Roles", (int)proxy.Roles);
            sink.Add("ShapeKind", (int)proxy.Shape.Kind);
            sink.Add("ShapeRadius", proxy.Shape.Radius);
            sink.Add("ShapeHalfExtents", proxy.Shape.HalfExtents);
            sink.Add("Center", proxy.Center);
            sink.Add("Enabled", proxy.Enabled);
        }
    }
}
