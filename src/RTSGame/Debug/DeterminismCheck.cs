using System.Numerics;
using System.Reflection;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Movement;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Threat;

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
        "SimulationWorld.nextMoveGroupId", "SimulationWorld.cohortDepartures",
        "SimulationWorld.blockColliders",
        // §131. The aggregate §119 reserved: what a faction can see and remembers seeing is simulation state,
        // read by decisions, and therefore fingerprinted — as against the renderer's fog, which is view state
        // and must never be read by the simulation. Getting these two the wrong way round is the mistake that
        // note exists to prevent, in both directions.
        "SimulationWorld.Knowledge",
        "ThreatSystem.Killed", "ThreatSystem.Dealt",
        "ThreatSystem.Standing", "ThreatSystem.Fleeing", "ThreatSystem.Surplus", "ThreatSystem.Crowded",
        "ThreatSystem.Contacts", "ThreatSystem.UnderAttack", "ThreatSystem.Attacking",
        "SimulationWorld.congestionRecoveryCooldown",
        "SimulationWorld.rasterizedTerrainRevision", "SimulationWorld.routePlansThisTick",
        "SimulationWorld.ExtentMeters", "SimulationWorld.Nodes", "SimulationWorld.economy",
        "EconomySystem.Produced", "EconomySystem.Consumed", "EconomySystem.Seeded",
        "EconomySystem.Unmet", "EconomySystem.HaulsAssigned", "EconomySystem.HaulsAbandoned",
        "EconomySystem.RoutesFinished", "EconomySystem.Born", "EconomySystem.Emigrated",
        "EconomySystem.Raised",
        "EconomySystem.boardCooldown",
        "MoveGroup.Id", "MoveGroup.members", "MoveGroup.plan",
        "MoveGroup.SettlingTicks", "MoveGroup.AtRest", "MoveGroup.TransitCentroid",
        "MoveGroup.HasTransitCentroid", "MoveGroup.TransitFlow",
        // Read through the cohort that carries it — Target, FormationRadius and Slots are the group's
        // accessors onto exactly these three fields, so the walk covers them without a second visit.
        "SlotPlan.Target", "SlotPlan.slots", "SlotPlan.FormationRadius",
    };

    /// <summary>
    /// Fields rebuilt from carried state before anything reads them. A divergence in one of
    /// these can only reach the next tick through something in <see cref="Carried"/>, and
    /// the reason it can is the value here — which is the part worth reviewing.
    /// </summary>
    private static readonly Dictionary<string, string> Derived = new()
    {
        // §138. Four per-faction scratch arrays behind Readiness, which is itself Carried below. All four are
        // cleared and refilled from the node store at the top of every population pass, before anything reads
        // them, so a save that restores the nodes restores these — and fingerprinting them would only be
        // fingerprinting the node store a second time.
        ["EconomySystem.factionGrain"] = "per-tick scratch, refilled from the node store before any read.",
        ["EconomySystem.factionWood"] = "per-tick scratch, refilled from the node store before any read.",
        ["EconomySystem.factionMouths"] = "per-tick scratch, refilled from the node store before any read.",
        ["EconomySystem.factionReadiness"] =
            "per-tick scratch, recomputed from the three above every pass. The player's figure is exposed " +
            "as Readiness, which is fingerprinted.",
        ["SimulationWorld.pathService"] =
            "caches keyed by the revisions of terrain, navigation and congestion, all three of " +
            "which are fingerprinted; its work counters are read through the world and are.",
        ["SimulationWorld.steeringSystem"] =
            "per-tick scratch. Its solver's counters are fingerprinted through the world, which " +
            "is the part that carries: a run that solved a different number of times has " +
            "already diverged in a decision.",
        ["SimulationWorld.collisionSystem"] = "per-tick scratch; contact results land on bodies within the tick.",
        ["SimulationWorld.agentIndex"] = "rebuilt from body positions every tick, and those are fingerprinted.",
        ["SimulationWorld.LastOrderWasBestEffort"] =
            "what the last order's goal resolution decided, kept so the game can say 'as close as we could " +
            "get'. Written by every move order before any route is asked for and read by nothing the " +
            "simulation does, so it cannot carry a difference into a decision — and it is derived from the " +
            "target and the navigation mesh, both of which are fingerprinted.",
        ["SimulationWorld.LastOrderAdoptedCohort"] =
            "whether the last order was taken by a cohort that already existed. Written by every move order " +
            "and read by nothing the simulation does; which cohort took an order IS carried, in moveGroups.",
        ["SimulationWorld.LastOrderFoundNothing"] = "as LastOrderWasBestEffort: a report, not an input.",
        ["SimulationWorld.LastOrderAnchorUnplaced"] = "as LastOrderWasBestEffort: a report, not an input.",
        ["SimulationWorld.LastOrderShortfall"] =
            "metres between the target asked for and the one used, for the same report. Nothing reads it back.",
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
        ["EconomySystem.nodesWithHands"] =
            "cache of the nodes whose fingerprinted Hands value was nonzero last tick. It is cleared " +
            "and rebuilt by CountHands; after a load the first pass deliberately resets every node instead.",
        ["EconomySystem.handsInitialized"] =
            "selects the one-time full reset after construction or load. Every later consequence lands in " +
            "EconomyNode.Hands, which is fingerprinted, and Read always invalidates this cache.",
        ["SimulationWorld.fellings"] =
            "a ring of where trees lately came down, so a stump can be drawn there. Cosmetic: no " +
            "decision anywhere reads it, and it is deliberately not saved either.",
        ["SimulationWorld.fellingCount"] =
            "how far round the fellings ring we are, which is part of the same cosmetic record.",
        ["SimulationWorld.forestNeighbours"] =
            "scratch for one felling's cover decision, gathered from the trees and read within the call.",
        ["SimulationWorld.threat"] =
            "the harm system itself, whose fields are censused in their own right below.",
        ["EconomySystem.Readiness"] =
            "recomputed from the stores and the households at the head of every population pass, before " +
            "anything reads it; it is a reported figure rather than a carried one.",
        ["ThreatSystem.landed"] =
            "who struck this tick, rebuilt inside the harm pass for whatever is reporting on the fight.",
        ["ThreatSystem.declined"] =
            "scratch for one body's already-covered alarms, cleared at the head of its own decision.",
        ["ThreatSystem.fellWithFaction"] =
            "who died this tick, mirrored from fallen with the faction kept, and read within the tick.",
        ["ThreatSystem.engaged"] =
            "scratch for one body's assailants, cleared and refilled inside the harm pass.",
        ["ThreatSystem.hostiles"] =
            "who can do harm, gathered at the head of every defence pass from the bodies themselves.",
        ["ThreatSystem.guarded"] =
            "what is worth protecting, rebuilt per body from the nodes and the loads on backs.",
        ["ThreatSystem.menace"] =
            "scratch for one body's weighing of a threat, refilled before it is read.",
        ["ThreatSystem.fallen"] =
            "who died this tick, refilled by every pass and handed straight back to the world.",
        ["EconomySystem.drawnOn"] =
            "rebuilt at the head of every pass of the board from the supply bindings on the houses, " +
            "which are on the nodes and are fingerprinted.",
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
    /// State this check deliberately does not cover, and the argument for each exclusion.
    /// </summary>
    /// <remarks>
    /// <b>Not a category the census enforces, and that is exactly why it is written down.</b> Every other
    /// list here is checked: a field appears or the census fails by name. This one records state the census
    /// <em>cannot</em> see, because it does not hang off <see cref="SimulationWorld"/> at all — so nothing
    /// will ever complain about it, and the only thing standing between it and the fingerprint is a later
    /// session's reading of why it is outside.
    /// <para>
    /// <b>Fog of war</b> (<c>RTSGame.Rendering.FogOfWar</c>) is renderer-side: two masks over ten-metre
    /// cells recording what the player has scouted and what they are watching. It is derived from unit
    /// positions and is therefore perfectly deterministic, which is the whole trap — the determinism
    /// argument for pulling it inside is sound and the consequence is not. The moment a simulation decision
    /// reads it, "what the player can see" becomes "what the world does", and view state is inside the
    /// fingerprint for good: two runs that differ only in where the camera has been would then diverge.
    /// The rule is a direction rather than a location — <b>fog may read the simulation; the simulation may
    /// never read fog</b> — and it holds cheaply because what crosses the seam is a pure predicate,
    /// <see cref="SimulationWorld.CanSee"/>, rather than state.
    /// </para>
    /// <para>
    /// <b>What is not an exclusion.</b> A per-faction knowledge aggregate — which cells a faction can
    /// currently see, as read by an AI opponent or by a defence deciding whether it is needed — is
    /// simulation state and belongs in <see cref="Carried"/> with a census entry, not here. It is not built.
    /// Conflating it with the masks above is the mistake this note exists to prevent, in both directions:
    /// fingerprinting the view, or letting a decision read something unfingerprinted.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> OutsideTheSimulation = new()
    {
        ["RTSGame.Rendering.FogOfWar"] =
            "renderer-side view state, derived from unit positions through SimulationWorld.CanSee and " +
            "never read back by the simulation. Fingerprinting it would put where the camera has been " +
            "inside the state two runs are compared on.",
    };

    /// <summary>
    /// Types the census walks field by field. Anything a carried field points at is either
    /// one of these, plain data, or has a line in <see cref="Boundaries"/>.
    /// </summary>
    private static readonly Type[] Censused =
    {
        typeof(SimulationWorld), typeof(MoveGroup), typeof(SlotPlan), typeof(EconomySystem),
        typeof(ThreatSystem),
    };

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
        ["FactionKnowledge"] =
            "every cell's last-seen tick, for every faction, in a fixed order. That single number is both " +
            "what a faction knows and how stale it is, so there is nothing else to read: a visible-now mask " +
            "would be the same value compared against the tick.",
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
                       "differ. A fourth answer is that it does not belong on the world at all — " +
                       "state that exists to be looked at rather than simulated is held by the " +
                       "renderer and stays outside this check, as " +
                       $"{string.Join(", ", OutsideTheSimulation.Keys)} " +
                       "does; see OutsideTheSimulation for why that is a direction rather than a " +
                       "preference.";
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
        // Why bodies have left cohorts, which is a decision record and not a position. Two runs that
        // disagree here have disagreed about who was in a group, and that is upstream of everywhere the
        // disagreement would otherwise first appear.
        var departures = world.CohortDepartures;
        sink.Add("CohortSuperseded", departures.Superseded);
        sink.Add("CohortOverridden", departures.Overridden);
        sink.Add("CohortInterrupted", departures.Interrupted);
        sink.Add("CohortDied", departures.Died);
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

        // <b>What each faction knows, every tick.</b> Not at a checkpoint like the map: knowledge changes on
        // every tick that anybody moves, and a divergence in it is a divergence in what a decision will be
        // allowed to read. Cheap because it is one long per ten-metre cell per faction, in a fixed order.
        sink.Push("knowledge", -1);
        sink.Add("knowledgeCells", world.Knowledge.Cells);
        for (var faction = 0; faction < FactionKnowledge.Factions; faction++)
        {
            var seen = world.Knowledge.SeenBy(faction);
            for (var cell = 0; cell < seen.Length; cell++)
            {
                if (seen[cell] == 0) continue;
                // Only what has been seen, and the cell index with it. Folding sixty thousand zeroes a tick
                // would be the same hash for every run that has looked at nothing, and the index is what keeps
                // two different cells from being one contribution.
                sink.Add("seenCell", cell);
                sink.Add("seenTick", seen[cell]);
            }
        }

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
        sink.Add("RoutesFinished", world.Economy.RoutesFinished);
        sink.Add("Born", world.Economy.Born);
        sink.Add("Emigrated", world.Economy.Emigrated);
        sink.Add("Raised", world.Economy.Raised);
        sink.Add("Killed", world.Threat.Killed);
        sink.Add("Dealt", world.Threat.Dealt);
        sink.Add("Standing", world.Threat.Standing);
        sink.Add("Surplus", world.Threat.Surplus);
        sink.Add("Crowded", world.Threat.Crowded);
        sink.Add("Contacts", world.Threat.Contacts);
        sink.Add("UnderAttack", world.Threat.UnderAttack);
        sink.Add("Attacking", world.Threat.Attacking);
        sink.Add("Fleeing", world.Threat.Fleeing);
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
            sink.Add("Members", group.Members.Count);
            sink.Add("FormationRadius", group.FormationRadius);
            sink.Add("SettlingTicks", group.SettlingTicks);
            sink.Add("AtRest", group.AtRest);
            sink.Add("TransitCentroid", group.TransitCentroid);
            sink.Add("HasTransitCentroid", group.HasTransitCentroid);
            sink.Add("TransitFlow", group.TransitFlow);
            if (scope == Scope.Tick) continue;

            for (var member = 0; member < group.Members.Count; member++)
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
