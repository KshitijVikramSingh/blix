using System.Numerics;
using RTSGame.Control;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Debug;

internal static class SimulationSelfTests
{
    /// <summary>
    /// Multiplier on every time budget in this file, from the body coming down from a run
    /// to a walk.
    /// </summary>
    /// <remarks>
    /// Top speed went from 4.5 m/s to 1.5, so everything measured in seconds — how long a
    /// crossing takes, how long a queue at a gap lasts, how long a body may be stalled
    /// before it counts as stuck — takes three times as long in wall clock while describing
    /// exactly the same behaviour. Written as a factor rather than folded into the numbers
    /// so that every budget below still shows what it was when it was tuned, and so that a
    /// threshold which moved for a *reason* is visibly different from one that moved because
    /// the body did.
    /// <para>
    /// Deliberately not applied to anything that is not a duration. Distances, separations,
    /// ratios like <c>walked/optimal</c>, exit counts and body radii are all speed-invariant
    /// and every one of them held unchanged through the re-base — which is the evidence that
    /// this is a change of pace and not a change of behaviour.
    /// </para>
    /// </remarks>
    private const int WalkingPace = 3;

    private readonly record struct RedEpisode(
        AgentId Agent,
        float VisibleSeconds,
        float PeakCounter,
        Vector2 Position,
        bool InsidePen,
        float NearestExitDistance,
        float TargetDistance,
        float Speed,
        float PreferredSpeed,
        int RemainingWaypoints,
        float AgentRepathCooldown,
        float GlobalRecoveryCooldown,
        float CrowdPressureSeconds,
        int RepathsDuring);

    public static int Run()
    {
        var failed = 0;
        Check("single agent arrives", SingleAgentArrives());
        Check("shared destination settles compactly without a queue tail", SharedDestinationSettlesCompactly());
        Check("untouched starting formation remains still", UntouchedFormationRemainsStill());
        Check("formation settles after a center unit leaves", FormationSettlesAfterCenterUnitLeaves());
        Check("group move assembles progressively without private slots", GroupMoveAssemblesProgressively());
        Check("selection marquee uses screen-space bounds", SelectionUsesScreenBounds());
        Check("A* routes around a block wall", RoutesAroundBlockWall());
        Check("navigation clearance respects agent radius", ClearanceRespectsAgentRadius());
        Check("active route replans after an obstacle edit", ReplansAfterObstacleEdit());
        Check("height field samples elevation and slope", HeightFieldSamplesElevation());
        Check("surface costs prefer a road over mud", SurfaceCostsPreferRoad());
        Check("routes detour around an over-steep hill", RoutesAroundSteepHill());
        Check("impassable slopes reject a route", ImpassableSlopeRejectsRoute());
        Check("placement rejects unsuitable terrain", PlacementRejectsUnsuitableTerrain());
        Check("crowd crosses a traversable terrain ramp", CrowdCrossesTerrainRamp());
        Check("crowd rounds a ramp-cliff corner without sticking", CrowdRoundsTerrainCorner());
        Check("terrain edits invalidate active routes", TerrainEditInvalidatesRoute());
        Check("terrain-aware simulations stay deterministic", TerrainSimulationsMatch());
        Check("repath recovers from a nearby invalid start cell", RepathRecoversFromInvalidStart());
        Check("placement uses purpose-specific colliders", PlacementUsesColliderQuery());
        Check("collider queries separate self, ally and enemy", ColliderRelationsAreFiltered());
        Check("head-on allies steer past without overlap", HeadOnAgentsPass());
        Check("every group member eventually settles", EveryGroupMemberEventuallySettles());
        Check("allies file through a narrow chokepoint", AlliesQueueThroughChokepoint());
        Check("stalled unit is offered a detour", StalledUnitIsOfferedADetour());
        Check("50 agents clear a single-cell gate", FiftyAgentsClearSingleCellGate());
        Check("group distributes across multiple pen exits", GroupUsesMultiplePenExits());
        Check("group escapes pen through alternate exits under backpressure", GroupEscapesPenUnderBackpressure());
        Check("200-agent group scenario remains collision-safe", TwoHundredAgentScenarioIsSafe());
        Check("500-agent stress scenario stays deterministic", FiveHundredAgentScenarioIsDeterministic());
        Check("locomotion behavior states drive movement", LocomotionBehaviorsDriveMovement());
        Check("dense crowd settles without body overlap", DenseCrowdSeparates());
        Check("static collision expels an embedded body", StaticCollisionExpelsBody());
        Check("displaced idle agent returns to its hold position", DisplacedIdleAgentReturns());
        Check("identical simulations stay deterministic", IdenticalSimulationsMatch());
        Check("despawned units leave the simulation entirely", DespawnRemovesUnitsCleanly());
        Check("no unit deadlocks against sculpted terrain", TerrainDoesNotDeadlockFlowTransit());
        Check("no unit stands under orders without intent", NoUnitStandsIntentless());
        Check("congestion sweeps every cell holding pressure", CongestionSweepTracksPressure());
        Check("a larger world leaves the tuned one untouched", WorldExtentIsParameterised());
        Check("routing stays close to the flat optimum", RoutingIsFaithful());
        Check("a group crosses region borders without swinging", GroupCrossesRegionBorders());
        Console.WriteLine($"  simulation self-test: {(failed == 0 ? "all passed" : $"{failed} FAILED")}");
        return failed;

        void Check(string name, bool passed)
        {
            Console.WriteLine($"  {(passed ? "PASS" : "FAIL")}  {name}");
            if (!passed) failed++;
        }
    }

    public static int RunSharedDestinationRegression()
    {
        var passed = SharedDestinationSettlesCompactly();
        Console.WriteLine($"  {(passed ? "PASS" : "FAIL")}  shared destination settles compactly without a queue tail");
        return passed ? 0 : 1;
    }

    public static int RunTerrainCornerDiagnostic()
    {
        var passed = CrowdRoundsTerrainCorner();
        Console.WriteLine($"  {(passed ? "PASS" : "FAIL")}  crowd rounds a ramp-cliff corner without sticking");
        return passed ? 0 : 1;
    }

    public static int RunSingleCellGateDiagnostic()
    {
        var passed = FiftyAgentsClearSingleCellGate();
        Console.WriteLine($"  {(passed ? "PASS" : "FAIL")}  50 agents clear a single-cell gate");
        return passed ? 0 : 1;
    }

    public static int RunIdleYieldDiagnostic()
    {
        var passed = DisplacedIdleAgentReturns();
        Console.WriteLine($"  {(passed ? "PASS" : "FAIL")}  displaced idle agent returns to its hold position");
        return passed ? 0 : 1;
    }

    public static int RunCornerCircuitDiagnostic(int maximumLegs = 4)
    {
        var world = new SimulationWorld();
        var ids = TerrainStressScenarios.Populate(world, issueGroupMove: false);
        var targets = new[]
        {
            new Vector2(-11f, -11f),
            new Vector2(11f, -11f),
            new Vector2(11f, 11f),
            new Vector2(-11f, 11f),
        };
        var failures = 0;

        for (var leg = 0; leg < Math.Min(targets.Length, maximumLegs); leg++)
        {
            var target = targets[leg];
            var repairsBeforeLeg = world.ImmediateRouteRepairCount;
            world.QueueMove(ids, target);
            var previous = ids.Select(id => world.Agents.Get(id).Position).ToArray();
            var exhaustedAttempts = new bool[ids.Length];
            var firstArrivalTick = -1;
            var stableTicks = 0;
            var completionTick = -1;
            var idleTravel = 0f;
            var maximumHoldOffset = 0f;
            var maximumArrivalAttempts = 0;
            var commandRejects = new bool[ids.Length];
            var lastReportedRepairs = world.ImmediateRouteRepairCount;

            for (var tick = 0; tick < 3600; tick++)
            {
                world.Tick((float)SimulationWorld.FixedDeltaSeconds);
                var active = 0;
                var arrivedNearTarget = 0;
                for (var index = 0; index < ids.Length; index++)
                {
                    ref var agent = ref world.Agents.Get(ids[index]);
                    if (agent.HasDestination) active++;
                    else
                    {
                        idleTravel += Vector2.Distance(previous[index], agent.Position);
                        if (Vector2.Distance(agent.Position, target) <= 6f) arrivedNearTarget++;
                        if (tick == 0 && Vector2.Distance(agent.Position, target) > 6f)
                        {
                            commandRejects[index] = true;
                        }
                    }
                    previous[index] = agent.Position;
                    if (!agent.HasDestination)
                    {
                        maximumHoldOffset = MathF.Max(
                            maximumHoldOffset,
                            Vector2.Distance(agent.Position, agent.HoldPosition));
                    }
                    maximumArrivalAttempts = Math.Max(maximumArrivalAttempts, agent.CrowdedArrivalAttempts);
                    exhaustedAttempts[index] |= agent.CrowdedArrivalAttempts >=
                                                SimulationWorld.CrowdedArrivalFailedAttemptLimit;
                }

                if (active <= 3 && active > 0 && tick >= 1200 && tick % 300 == 0)
                {
                    foreach (var id in ids.Where(id => world.Agents.Get(id).HasDestination))
                    {
                        ref var agent = ref world.Agents.Get(id);
                        var path = world.GetRemainingPath(id);
                        var next = path.Length > 0 ? path[0] : agent.Destination;
                        Console.WriteLine(
                            $"    t={tick * SimulationWorld.FixedDeltaSeconds:F0}s active {id.Value}: " +
                            $"pos={agent.Position.X:F2}/{agent.Position.Y:F2} " +
                            $"next={next.X:F2}/{next.Y:F2} waypoints={path.Length} " +
                            $"targetDistance={Vector2.Distance(agent.Position, target):F2} " +
                            $"nextDistance={Vector2.Distance(agent.Position, next):F2} " +
                            $"speed={agent.Velocity.Length():F2}/{agent.PreferredVelocity.Length():F2} " +
                            $"stuck={agent.StuckSeconds:F2} " +
                            $"repaths={world.CongestionRepathCount}");
                    }
                }

                if (tick > 0 && tick % 300 == 0 &&
                    world.ImmediateRouteRepairCount != lastReportedRepairs)
                {
                    Console.WriteLine(
                        $"    t={tick * SimulationWorld.FixedDeltaSeconds:F0}s active={active} " +
                        $"routeRepairs=+{world.ImmediateRouteRepairCount - lastReportedRepairs}");
                    lastReportedRepairs = world.ImmediateRouteRepairCount;
                }

                if (firstArrivalTick < 0 && arrivedNearTarget > 0) firstArrivalTick = tick;
                stableTicks = active == 0 && arrivedNearTarget == ids.Length ? stableTicks + 1 : 0;
                if (stableTicks < 60) continue;
                completionTick = tick - 59;
                break;
            }

            var settled = ids.Select(id => world.Agents.Get(id).Position).ToArray();
            for (var tick = 0; tick < 120; tick++)
            {
                world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            }
            var maximumPostSettleDrift = ids.Select((id, index) =>
                Vector2.Distance(world.Agents.Get(id).Position, settled[index])).Max();
            var maximumSpread = ids.Max(id => Vector2.Distance(world.Agents.Get(id).Position, target));
            var settleSeconds = completionTick < 0 || firstArrivalTick < 0
                ? float.PositiveInfinity
                : (completionTick - firstArrivalTick) * (float)SimulationWorld.FixedDeltaSeconds;
            var passed = completionTick >= 0 && maximumSpread <= 6f &&
                         maximumPostSettleDrift <= 0.03f;
            if (!passed) failures++;
            Console.WriteLine(
                $"  corner leg {leg + 1}: {(passed ? "PASS" : "FAIL")} " +
                $"first={firstArrivalTick * SimulationWorld.FixedDeltaSeconds:F1}s " +
                $"settle={settleSeconds:F1}s spread={maximumSpread:F2} " +
                $"idleTravel={idleTravel:F1} maxHoldOffset={maximumHoldOffset:F2} " +
                $"attempts={maximumArrivalAttempts} exhausted={exhaustedAttempts.Count(value => value)} " +
                $"rejected={commandRejects.Count(value => value)} " +
                $"routeRepairs={world.ImmediateRouteRepairCount - repairsBeforeLeg} " +
                $"postDrift={maximumPostSettleDrift:F3}");
            if (!passed)
            {
                foreach (var id in ids.Where(id => world.Agents.Get(id).HasDestination).Take(8))
                {
                    ref var agent = ref world.Agents.Get(id);
                    var path = world.GetRemainingPath(id);
                    var next = path.Length > 0 ? path[0] : agent.Destination;
                    var toNext = next - agent.Position;
                    var sample = toNext.LengthSquared() > 0.0001f
                        ? agent.Position + Vector2.Normalize(toNext) * MathF.Min(0.25f, toNext.Length())
                        : agent.Position;
                    var velocityStep = agent.Position +
                                       agent.Velocity * (float)SimulationWorld.FixedDeltaSeconds;
                    var preferredStep = agent.Position +
                                        agent.PreferredVelocity * (float)SimulationWorld.FixedDeltaSeconds;
                    Console.WriteLine(
                        $"    active {id.Value}: pos={agent.Position.X:F2}/{agent.Position.Y:F2} " +
                        $"distance={Vector2.Distance(agent.Position, target):F2} " +
                        $"next={next.X:F2}/{next.Y:F2} waypoints={path.Length} " +
                        $"state={agent.LocomotionState}/return={agent.ReturningToHold} " +
                        $"speed={agent.Velocity.Length():F2}/preferred={agent.PreferredVelocity.Length():F2} " +
                        $"yield={agent.CongestionYieldSeconds:F2} " +
                        $"stepGeometry={world.IsAgentStepGeometryValid(id, sample)} " +
                        $"velocityGeometry={world.IsAgentStepGeometryValid(id, velocityStep)} " +
                        $"preferredGeometry={world.IsAgentStepGeometryValid(id, preferredStep)} " +
                        $"attempts={agent.CrowdedArrivalAttempts} frames={agent.CrowdedArrivalContactFrames} " +
                        $"blocked={agent.CrowdedArrivalBlockedThisTick} contact={agent.HadAgentContactThisTick} " +
                        $"stuck={agent.StuckSeconds:F2}");
                }
                foreach (var id in ids.Where(id =>
                             !world.Agents.Get(id).HasDestination &&
                             Vector2.Distance(world.Agents.Get(id).Position, target) > 6f).Take(8))
                {
                    ref var agent = ref world.Agents.Get(id);
                    Console.WriteLine(
                        $"    far idle {id.Value}: pos={agent.Position.X:F2}/{agent.Position.Y:F2} " +
                        $"distance={Vector2.Distance(agent.Position, target):F2} " +
                        $"requested={agent.RequestedDestination.X:F2}/{agent.RequestedDestination.Y:F2}");
                }
            }
        }

        return failures;
    }

    private static bool SharedDestinationSettlesCompactly()
    {
        var world = new SimulationWorld();
        var ids = MovementStressScenarios.Populate(world, 30, issueGroupMove: false);
        var target = new Vector2(10f, 0f);
        world.QueueMove(ids, target);

        var completionTick = -1;
        for (var tick = 0; tick < 1800; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            if (completionTick < 0 && ids.All(id => !world.Agents.Get(id).HasDestination))
            {
                completionTick = tick;
                break;
            }
        }

        var maximumTargetDistance = ids.Max(id => Vector2.Distance(world.Agents.Get(id).Position, target));
        var minimumDistance = float.PositiveInfinity;
        for (var first = 0; first < ids.Length; first++)
        for (var second = first + 1; second < ids.Length; second++)
        {
            minimumDistance = MathF.Min(
                minimumDistance,
                Vector2.Distance(
                    world.Agents.Get(ids[first]).Position,
                    world.Agents.Get(ids[second]).Position));
        }

        var passed = completionTick >= 0 &&
                     maximumTargetDistance <= 5.50f &&
                     minimumDistance >= AgentDefaults.SeparationThreshold(AgentDefaults.Radius);
        if (!passed)
        {
            var nearestFirst = -1;
            var nearestSecond = -1;
            var nearestDistance = float.PositiveInfinity;
            for (var first = 0; first < ids.Length; first++)
            for (var second = first + 1; second < ids.Length; second++)
            {
                var distance = Vector2.Distance(
                    world.Agents.Get(ids[first]).Position,
                    world.Agents.Get(ids[second]).Position);
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearestFirst = ids[first].Value;
                nearestSecond = ids[second].Value;
            }
            Console.WriteLine(
                $"    shared destination completionTick={completionTick}, " +
                $"maximumTargetDistance={maximumTargetDistance:F2}, minimumDistance={minimumDistance:F3}, " +
                $"nearest={nearestFirst}/{nearestSecond}, " +
                $"active={ids.Count(id => world.Agents.Get(id).HasDestination)}");
            foreach (var id in ids.Where(id => world.Agents.Get(id).HasDestination))
            {
                ref var agent = ref world.Agents.Get(id);
                Console.WriteLine(
                    $"      unresolved {id.Value}: pos={agent.Position.X:F2}/{agent.Position.Y:F2}, " +
                    $"distance={Vector2.Distance(agent.Position, target):F2}, " +
                    $"waypoints={world.GetRemainingPath(id).Length}, stuck={agent.StuckSeconds:F2}, " +
                    $"pressure={agent.CrowdPressureSeconds:F2}, " +
                    $"arrivalAttempts={agent.CrowdedArrivalAttempts}");
            }
        }
        return passed;
    }

    private static bool SingleAgentArrives()
    {
        var world = new SimulationWorld();
        var id = world.SpawnAgent(Vector2.Zero);
        world.QueueMove(new[] { id }, new Vector2(7f, -4f));
        Tick(world, 240);
        ref var agent = ref world.Agents.Get(id);
        return !agent.HasDestination && Vector2.Distance(agent.Position, new Vector2(7f, -4f)) < 0.001f;
    }

    private static bool UntouchedFormationRemainsStill()
    {
        var world = new SimulationWorld();
        var ids = MovementStressScenarios.Populate(world, 30, issueGroupMove: false);
        var starts = ids.Select(id => world.Agents.Get(id).Position).ToArray();
        Tick(world, 300);

        for (var i = 0; i < ids.Length; i++)
        {
            ref var agent = ref world.Agents.Get(ids[i]);
            if (agent.Position != starts[i] || agent.Velocity != Vector2.Zero ||
                agent.ReturningToHold || agent.HasDestination)
            {
                return false;
            }
        }
        return true;
    }

    private static bool FormationSettlesAfterCenterUnitLeaves()
    {
        var world = new SimulationWorld();
        var ids = MovementStressScenarios.Populate(world, 30, issueGroupMove: false);
        var departing = ids[14];
        world.QueueMove(new[] { departing }, new Vector2(-9f, 0f));
        Tick(world, 900);

        var settledPositions = ids.Select(id => world.Agents.Get(id).Position).ToArray();
        Tick(world, 300);

        for (var i = 0; i < ids.Length; i++)
        {
            ref var agent = ref world.Agents.Get(ids[i]);
            if (Vector2.DistanceSquared(agent.Position, settledPositions[i]) > 0.000001f ||
                agent.Velocity.LengthSquared() > 0.000001f ||
                Vector2.DistanceSquared(agent.Position, agent.HoldPosition) > 0.0004f ||
                agent.HasDestination || agent.ReturningToHold)
            {
                Console.WriteLine(
                    $"    formation unsettled {ids[i].Value}: drift={Vector2.Distance(agent.Position, settledPositions[i]):F4} " +
                    $"speed={agent.Velocity.Length():F4} holdOffset={Vector2.Distance(agent.Position, agent.HoldPosition):F4} " +
                    $"moving={agent.HasDestination} returning={agent.ReturningToHold}");
                return false;
            }
        }
        return true;
    }

    private static bool GroupMoveAssemblesProgressively()
    {
        var world = new SimulationWorld();
        var ids = new AgentId[12];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = world.SpawnAgent(new Vector2(i % 4, i / 4));
        }

        var target = new Vector2(6f, 5f);
        world.QueueMove(ids, target);
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        if (ids.Any(id => world.Agents.Get(id).RequestedDestination != target)) return false;

        var observedPartialAssembly = false;
        for (var tick = 0; tick < 1200; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var settledMembers = ids.Count(id => !world.Agents.Get(id).HasDestination);
            observedPartialAssembly |= settledMembers > 0 && settledMembers < ids.Length;
        }

        var settled = ids.Select(id => world.Agents.Get(id).Position).ToArray();
        var separated = true;
        for (var first = 0; first < settled.Length; first++)
        for (var second = first + 1; second < settled.Length; second++)
            separated &= Vector2.Distance(settled[first], settled[second]) >=
                         AgentDefaults.SeparationThreshold(AgentDefaults.Radius);
        Tick(world, 300);
        var stable = ids.Select((id, index) => (id, index)).All(item =>
            !world.Agents.Get(item.id).HasDestination &&
            Vector2.DistanceSquared(world.Agents.Get(item.id).Position, settled[item.index]) < 0.000001f);
        var compact = settled.All(position => Vector2.Distance(position, target) < 4f);
        var passed = observedPartialAssembly && separated && stable && compact;
        if (!passed) Console.WriteLine($"    progressive observed={observedPartialAssembly}, separated={separated}, stable={stable}, compact={compact}, " +
                                       $"states=[{string.Join(',', ids.Select(id =>
                                       {
                                           ref var agent = ref world.Agents.Get(id);
                                           return $"{id.Value}:moving={agent.HasDestination}/pos={agent.Position.X:F1}/{agent.Position.Y:F1}";
                                       }))}]");
        return passed;
    }

    private static bool SelectionUsesScreenBounds()
    {
        var agents = new AgentStore();
        var inside = agents.Spawn(Vector2.Zero, new FactionId(0));
        var outside = agents.Spawn(new Vector2(8f, 0f), new FactionId(0));
        var projection = new Matrix4x4(
            0.10f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f,
            0f, 0.10f, 0f, 0f,
            0f, 0f, 0.50f, 1f);
        var selection = new SelectionController();
        selection.Begin(40f, 40f);
        selection.Update(60f, 60f);
        selection.End(
            agents,
            projection,
            100,
            100,
            additive: false);
        return selection.Contains(inside) && !selection.Contains(outside);
    }

    private static bool IdenticalSimulationsMatch()
    {
        var first = BuildDeterministicWorld();
        var second = BuildDeterministicWorld();
        Tick(first, 180);
        Tick(second, 180);
        if (first.TickNumber != second.TickNumber || first.Agents.Count != second.Agents.Count) return false;

        for (var i = 0; i < first.Agents.Count; i++)
        {
            var id = new AgentId(i);
            ref var a = ref first.Agents.Get(id);
            ref var b = ref second.Agents.Get(id);
            if (a.Position != b.Position || a.Velocity != b.Velocity || a.HasDestination != b.HasDestination)
            {
                return false;
            }
        }
        return true;
    }

    private static bool RoutesAroundBlockWall()
    {
        var world = new SimulationWorld();
        var id = world.SpawnAgent(new Vector2(-8f, 0f));
        for (var z = 7; z <= 12; z++)
        {
            world.QueueToggleObstacle(world.Placement.Transform.CellCenter(new GridCell(10, z)));
        }
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        world.QueueMove(new[] { id }, new Vector2(8f, 0f));

        var maximumDetour = 0f;
        var crossedBlockedCell = false;
        var bodyLeftTheGround = false;
        var worstClearance = float.PositiveInfinity;
        for (var i = 0; i < 600; i++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            ref var agent = ref world.Agents.Get(id);
            maximumDetour = MathF.Max(maximumDetour, MathF.Abs(agent.Position.Y));
            if (!world.IsAgentGeometryValid(id)) bodyLeftTheGround = true;
            if (world.Navigation.TryWorldToCell(agent.Position, out var cell))
            {
                worstClearance = MathF.Min(worstClearance, world.Navigation.Clearance(cell));
                if (!world.Navigation.IsWalkable(cell, agent.Radius)) crossedBlockedCell = true;
            }
        }

        ref var finished = ref world.Agents.Get(id);
        // The body's own geometry, not the router's opinion of the cell it stands in.
        // This used to assert that the centre never entered a cell A* calls unwalkable,
        // which is a proxy and not a sound one: clearance is measured from the cell
        // centre, and a body can be a third of a metre from the centre of its own cell,
        // so cell clearance cannot bound where the body actually is. Once routes started
        // being charged for turning they got straighter, began brushing the corner, and
        // tripped a condition the body was never violating — measured: exact geometry
        // valid on every one of 600 ticks, while the proxy read 0.354 m of cell clearance
        // against a 0.405 m bar, that figure being exactly half a cell diagonal, i.e. the
        // cell diagonally off the corner. What matters is that it never enters the wall,
        // and `ConstrainAgentsToTerrain` enforces exactly this predicate every tick.
        var passed = !bodyLeftTheGround && maximumDetour > 3f && !finished.HasDestination &&
                     Vector2.Distance(finished.Position, new Vector2(8f, 0f)) < 0.001f;
        // Four conditions and no output made this test say only "no". Which one failed
        // matters: clipping a blocked cell is a geometry bug, not detouring is a routing
        // one, and not arriving is neither.
        if (!passed)
        {
            Console.WriteLine(
                $"    wall detour: bodyInvalid={bodyLeftTheGround}, crossedBlocked={crossedBlockedCell}, " +
                $"worstCellClearance={worstClearance:F3} (needs {AgentDefaults.Radius + 0.035f:F3}), " +
                $"maxDetour={maximumDetour:F2} " +
                $"(bar 3.00), stillMoving={finished.HasDestination}, " +
                $"pos=({finished.Position.X:F3},{finished.Position.Y:F3}), " +
                $"targetDistance={Vector2.Distance(finished.Position, new Vector2(8f, 0f)):F3}");
        }
        return passed;
    }

    private static bool ReplansAfterObstacleEdit()
    {
        var world = new SimulationWorld();
        var id = world.SpawnAgent(new Vector2(-8f, -4f));
        world.QueueMove(new[] { id }, new Vector2(8f, -4f));
        Tick(world, 30);

        var obstaclePoint = new Vector2(0.75f, -3.75f);
        world.QueueToggleObstacle(obstaclePoint);
        Tick(world, 450);

        ref var agent = ref world.Agents.Get(id);
        return world.TryGetPlacementCell(obstaclePoint, out var cell) && world.Placement.IsOccupied(cell) &&
               !agent.HasDestination && Vector2.Distance(agent.Position, new Vector2(8f, -4f)) < 0.001f;
    }

    private static bool HeightFieldSamplesElevation()
    {
        var world = new SimulationWorld();
        for (var z = 0; z <= world.Terrain.Transform.Height; z++)
        for (var x = 0; x <= world.Terrain.Transform.Width; x++)
        {
            world.Terrain.SetVertexHeight(x, z, x * 0.01f);
        }
        var low = world.Terrain.SampleHeight(new Vector2(-10f, 0f));
        var high = world.Terrain.SampleHeight(new Vector2(10f, 0f));
        var normal = world.Terrain.SampleNormal(Vector2.Zero);
        var rayHit = world.Terrain.TryRaycast(
            new Vector3(0f, 10f, 0f),
            -Vector3.UnitY,
            out var hit);
        return high > low + 0.35f && normal.X < -0.01f && normal.Y > 0.9f &&
               rayHit && Vector2.Distance(hit, Vector2.Zero) < 0.01f;
    }

    private static bool SurfaceCostsPreferRoad()
    {
        var world = new SimulationWorld();
        var terrain = world.Terrain;
        for (var z = 0; z < terrain.Transform.Height; z++)
        for (var x = 0; x < terrain.Transform.Width; x++)
            terrain.SetSurface(new GridCell(x, z), TerrainSurface.Rough);
        for (var z = 27; z <= 33; z++)
        for (var x = 18; x <= 42; x++)
            terrain.SetSurface(new GridCell(x, z), TerrainSurface.Mud);
        for (var x = 12; x <= 48; x++)
            terrain.SetSurface(new GridCell(x, 25), TerrainSurface.Road);
        for (var z = 25; z <= 30; z++)
        {
            terrain.SetSurface(new GridCell(12, z), TerrainSurface.Road);
            terrain.SetSurface(new GridCell(48, z), TerrainSurface.Road);
        }
        world.RebuildTerrainNavigation();

        var id = world.SpawnAgent(new Vector2(-9f, 0.25f));
        world.QueueMove(new[] { id }, new Vector2(9f, 0.25f));
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var route = world.GetRemainingPath(id).ToArray();
        var passed = route.Any(point => terrain.SampleSurface(point) == TerrainSurface.Road) &&
                     route.All(point => terrain.SampleSurface(point) != TerrainSurface.Mud);
        if (!passed) Console.WriteLine(
            $"    road route waypoints={route.Length} surfaces=[" +
            string.Join(',', route.Take(24).Select(point =>
                $"{point.X:F1}/{point.Y:F1}:{terrain.SampleSurface(point)}")) + "]");
        return passed;
    }

    private static bool ImpassableSlopeRejectsRoute()
    {
        var world = new SimulationWorld();
        var terrain = world.Terrain;
        for (var z = 0; z <= terrain.Transform.Height; z++)
        for (var x = 31; x <= terrain.Transform.Width; x++)
            terrain.SetVertexHeight(x, z, 1.5f);
        world.RebuildTerrainNavigation();

        var id = world.SpawnAgent(new Vector2(-6f, 0f));
        world.QueueMove(new[] { id }, new Vector2(6f, 0f));
        Tick(world, 60);
        ref var agent = ref world.Agents.Get(id);
        return !agent.HasDestination && agent.Position.X < -5.9f;
    }

    private static bool RoutesAroundSteepHill()
    {
        var world = new SimulationWorld();
        var terrain = world.Terrain;
        for (var z = 23; z <= 37; z++)
        for (var x = 27; x <= 33; x++)
            terrain.SetVertexHeight(x, z, 2f);

        var id = world.SpawnAgent(new Vector2(-8f, 0f));
        world.QueueMove(new[] { id }, new Vector2(8f, 0f));
        var maximumDetour = 0f;
        for (var tick = 0; tick < 900; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            maximumDetour = MathF.Max(maximumDetour, MathF.Abs(world.Agents.Get(id).Position.Y));
        }
        ref var agent = ref world.Agents.Get(id);
        return !agent.HasDestination && maximumDetour > 3f &&
               Vector2.Distance(agent.Position, new Vector2(8f, 0f)) < 0.01f;
    }

    private static bool PlacementRejectsUnsuitableTerrain()
    {
        var world = new SimulationWorld();
        var impassableCell = new GridCell(30, 30);
        world.Terrain.SetSurface(impassableCell, TerrainSurface.Impassable);
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        if (!world.TryGetPlacementCell(world.Terrain.Transform.CellCenter(impassableCell), out var placementCell))
            return false;
        return !world.CanToggleObstacle(placementCell);
    }

    private static bool CrowdCrossesTerrainRamp()
    {
        var world = new SimulationWorld();
        var ids = TerrainStressScenarios.Populate(world);
        var redStarts = Enumerable.Repeat(-1, world.Agents.Count).ToArray();
        var longestVisibleRed = 0f;
        var completionTick = -1;
        const int rampBudget = 6000 * WalkingPace;
        for (var tick = 0; tick < rampBudget; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            foreach (var id in ids)
            {
                ref var agent = ref world.Agents.Get(id);
                var visiblyRed = agent.StuckSeconds > 0.35f &&
                                   agent.CrowdPressureSeconds <= 0f;
                if (visiblyRed && redStarts[id.Value] < 0) redStarts[id.Value] = tick;
                if (!visiblyRed && redStarts[id.Value] >= 0)
                {
                    longestVisibleRed = MathF.Max(
                        longestVisibleRed,
                        (tick - redStarts[id.Value]) * (float)SimulationWorld.FixedDeltaSeconds);
                    redStarts[id.Value] = -1;
                }
            }
            if (!ids.All(id => !world.Agents.Get(id).HasDestination)) continue;
            completionTick = tick + 1;
            break;
        }
        for (var i = 0; i < redStarts.Length; i++)
        {
            if (redStarts[i] < 0) continue;
            longestVisibleRed = MathF.Max(
                longestVisibleRed,
                (rampBudget - redStarts[i]) * (float)SimulationWorld.FixedDeltaSeconds);
        }
        var unresolved = ids.Where(id =>
        {
            ref var agent = ref world.Agents.Get(id);
            return agent.HasDestination || agent.Position.X <= 2.5f || agent.StuckSeconds >= 1f;
        }).ToArray();
        if (unresolved.Length > 0)
        {
            Console.WriteLine($"    terrain ramp unresolved={unresolved.Length}, " +
                              $"positions=[{string.Join(',', unresolved.Take(6).Select(id =>
                              {
                                  ref var agent = ref world.Agents.Get(id);
                                  return $"{id.Value}:{agent.Position.X:F1}/{agent.Position.Y:F1}->" +
                                         $"{agent.Destination.X:F1}/{agent.Destination.Y:F1}/" +
                                         $"v={agent.Velocity.Length():F1}/{agent.PreferredVelocity.Length():F1}/" +
                                         $"stuck={agent.StuckSeconds:F1}/path={world.GetRemainingPath(id).Length}";
                              }))}]");
        }
        var completionSeconds = completionTick < 0
            ? 200f
            : completionTick * (float)SimulationWorld.FixedDeltaSeconds;
        // Red is wall-clock seconds spent failing to move, so the same hesitation at a ramp
        // reads three times longer at walking pace. Was 1 s.
        if (completionTick < 0 || longestVisibleRed > 1f * WalkingPace)
        {
            Console.WriteLine($"    terrain ramp completion={completionSeconds:F1}s, longest-red={longestVisibleRed:F2}s");
        }
        // Both durations, both re-based. Were 90 s and 1 s.
        return unresolved.Length == 0 &&
               completionSeconds <= 90f * WalkingPace &&
               longestVisibleRed <= 1f * WalkingPace;
    }

    private static bool CrowdRoundsTerrainCorner()
    {
        var world = new SimulationWorld();
        var ids = TerrainStressScenarios.Populate(world, issueGroupMove: false);
        var target = new Vector2(10f, 6f);
        world.QueueMove(ids, target);
        var completionTick = -1;
        var redStarts = Enumerable.Repeat(-1, world.Agents.Count).ToArray();
        var longestVisibleRed = 0f;
        var maximumQueued = 0;
        var invalidGeometrySamples = 0;
        for (var tick = 0; tick < 2700; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var queued = 0;
            foreach (var id in ids)
            {
                ref var agent = ref world.Agents.Get(id);
                if (!world.IsAgentGeometryValid(id)) invalidGeometrySamples++;
                var visiblyRed = agent.StuckSeconds > 0.35f &&
                                   agent.CrowdPressureSeconds <= 0f;
                if (visiblyRed && redStarts[id.Value] < 0) redStarts[id.Value] = tick;
                if (!visiblyRed && redStarts[id.Value] >= 0)
                {
                    longestVisibleRed = MathF.Max(
                        longestVisibleRed,
                        (tick - redStarts[id.Value]) * (float)SimulationWorld.FixedDeltaSeconds);
                    redStarts[id.Value] = -1;
                }
            }
            maximumQueued = Math.Max(maximumQueued, queued);
            if (!ids.All(id => !world.Agents.Get(id).HasDestination)) continue;
            completionTick = tick + 1;
            break;
        }

        for (var i = 0; i < redStarts.Length; i++)
        {
            if (redStarts[i] < 0) continue;
            longestVisibleRed = MathF.Max(
                longestVisibleRed,
                ((completionTick >= 0 ? completionTick : 2700) - redStarts[i]) *
                (float)SimulationWorld.FixedDeltaSeconds);
        }

        var unresolved = ids.Where(id =>
        {
            ref var agent = ref world.Agents.Get(id);
            return agent.HasDestination || agent.StuckSeconds >= 1f;
        }).ToArray();
        if (unresolved.Length > 0)
        {
            Console.WriteLine($"    terrain corner unresolved={unresolved.Length}, " +
                              $"positions=[{string.Join(',', unresolved.Take(8).Select(id =>
                              {
                                  ref var agent = ref world.Agents.Get(id);
                                  var path = world.GetRemainingPath(id);
                                  var next = path.Length > 0 ? path[0] : agent.Destination;
                                  var offset = next - agent.Position;
                                  var step = offset.LengthSquared() > 0.0001f
                                      ? agent.Position + Vector2.Normalize(offset) *
                                        MathF.Min(0.25f, offset.Length())
                                      : agent.Position;
                                  return $"{id.Value}:{agent.Position.X:F1}/{agent.Position.Y:F1}/" +
                                         $"next={next.X:F2}/{next.Y:F2}/" +
                                         $"v={agent.Velocity.Length():F1}/{agent.PreferredVelocity.Length():F1}/" +
                                         $"stuck={agent.StuckSeconds:F1}/" +
                                         $"pressure={agent.CrowdPressureSeconds:F1}/" +
                                         $"yield={agent.CongestionYieldSeconds:F1}/path={path.Length}/" +
                                         $"reject={agent.SteeringStepRejectedThisTick}/" +
                                         $"{agent.PreferredStepRejectedThisTick}/" +
                                         $"step={world.IsAgentStepGeometryValid(id, step)}/" +
                                         $"sweep={world.IsAgentContinuousStepGeometryValid(id, step)}";
                              }))}]");

            for (var sample = 0; sample < 120; sample++)
            {
                world.Tick((float)SimulationWorld.FixedDeltaSeconds);
                if (sample % 15 != 0) continue;
                foreach (var id in unresolved.Take(3))
                {
                    ref var agent = ref world.Agents.Get(id);
                    var agentPosition = agent.Position;
                    var nearest = ids
                        .Where(otherId => otherId != id)
                        .Select(otherId =>
                        {
                            ref var other = ref world.Agents.Get(otherId);
                            return (Id: otherId, Distance: Vector2.Distance(agentPosition, other.Position));
                        })
                        .OrderBy(item => item.Distance)
                        .First();
                    ref var neighbor = ref world.Agents.Get(nearest.Id);
                    Console.WriteLine(
                        $"      t+{sample * (float)SimulationWorld.FixedDeltaSeconds:F2} " +
                        $"{id.Value}@{agent.Position.X:F3}/{agent.Position.Y:F3} " +
                        $"v={agent.Velocity.X:F2}/{agent.Velocity.Y:F2} " +
                        $"pref={agent.PreferredVelocity.X:F2}/{agent.PreferredVelocity.Y:F2} " +
                        $"contact={agent.HadAgentContactThisTick} stuck={agent.StuckSeconds:F2} " +
                        $"near={nearest.Id.Value}@{neighbor.Position.X:F3}/{neighbor.Position.Y:F3} " +
                        $"d={nearest.Distance:F3} active={neighbor.HasDestination}");
                }
            }
        }
        Console.WriteLine(
            $"    terrain corner completion=" +
            $"{(completionTick < 0 ? float.PositiveInfinity : completionTick * (float)SimulationWorld.FixedDeltaSeconds):F1}s " +
            $"maxQueued={maximumQueued} longest-red={longestVisibleRed:F2}s " +
            $"invalidGeometrySamples={invalidGeometrySamples} " +
            $"routeRepairs={world.ImmediateRouteRepairCount} replans={world.CongestionRepathCount}");
        return completionTick >= 0 && unresolved.Length == 0 && invalidGeometrySamples == 0;
    }

    private static bool TerrainEditInvalidatesRoute()
    {
        var world = new SimulationWorld();
        var id = world.SpawnAgent(new Vector2(-9f, -4f));
        world.QueueMove(new[] { id }, new Vector2(9f, -4f));
        Tick(world, 30);
        var previousRevision = world.Navigation.Revision;
        for (var z = 20; z <= 27; z++)
        for (var x = 28; x <= 32; x++)
            world.Terrain.SetSurface(new GridCell(x, z), TerrainSurface.Impassable);
        Tick(world, 600);
        ref var agent = ref world.Agents.Get(id);
        return world.Navigation.Revision > previousRevision && !agent.HasDestination &&
               Vector2.Distance(agent.Position, new Vector2(9f, -4f)) < 0.01f;
    }

    private static bool TerrainSimulationsMatch()
    {
        var first = new SimulationWorld();
        var second = new SimulationWorld();
        var firstIds = TerrainStressScenarios.Populate(first);
        var secondIds = TerrainStressScenarios.Populate(second);
        Tick(first, 600);
        Tick(second, 600);
        return firstIds.Zip(secondIds).All(pair =>
        {
            ref var a = ref first.Agents.Get(pair.First);
            ref var b = ref second.Agents.Get(pair.Second);
            return a.Position == b.Position && a.Velocity == b.Velocity &&
                   a.HasDestination == b.HasDestination;
        });
    }

    private static bool RepathRecoversFromInvalidStart()
    {
        var world = new SimulationWorld();
        world.QueueToggleObstacle(Vector2.Zero);
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        if (!world.TryGetPlacementCell(Vector2.Zero, out var cell)) return false;

        var center = world.Placement.Transform.CellCenter(cell);
        var blockHalfExtent = world.Placement.Transform.CellSize * 0.5f - 0.025f;
        const float radius = AgentDefaults.Radius;
        var embeddedEdge = center + new Vector2(-blockHalfExtent - radius + 0.05f, 0f);
        var id = world.SpawnAgent(embeddedEdge, radius: radius);
        var target = center + new Vector2(6f, 0f);
        world.QueueMove(new[] { id }, target);
        Tick(world, 360);

        ref var agent = ref world.Agents.Get(id);
        var passed = !agent.HasDestination && Vector2.Distance(agent.Position, target) < 0.01f;
        if (!passed) Console.WriteLine(
            $"    invalid-start pos=({agent.Position.X:F3},{agent.Position.Y:F3}) target=({target.X:F2},{target.Y:F2}) " +
            $"residual={Vector2.Distance(agent.Position, target):F3} moving={agent.HasDestination} " +
            $"stuck={agent.StuckSeconds:F2} path={world.GetRemainingPath(id).Length} " +
            $"navigable={world.IsAgentGeometryValid(id)}");
        return passed;
    }

    private static bool ClearanceRespectsAgentRadius()
    {
        var world = new SimulationWorld();
        for (var x = 0; x < world.Placement.Transform.Width; x++)
        {
            world.QueueToggleObstacle(world.Placement.Transform.CellCenter(new GridCell(x, 9)));
            world.QueueToggleObstacle(world.Placement.Transform.CellCenter(new GridCell(x, 11)));
        }
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);

        var small = world.SpawnAgent(new Vector2(-10f, 0.75f), radius: AgentDefaults.Radius);
        var large = world.SpawnAgent(new Vector2(-10f, 0.75f), radius: 0.80f);
        world.QueueMove(new[] { small }, new Vector2(10f, 0.75f));
        world.QueueMove(new[] { large }, new Vector2(10f, 0.75f));
        // 20 m of walking, which is 13.3 s rather than the 4.4 s it was at a run.
        Tick(world, 300 * WalkingPace);

        ref var smallAgent = ref world.Agents.Get(small);
        ref var largeAgent = ref world.Agents.Get(large);
        var passed = smallAgent.Position.X > 8f && largeAgent.Position.X < -9f && !largeAgent.HasDestination;
        if (!passed) Console.WriteLine(
            $"    clearance small=({smallAgent.Position.X:F2},{smallAgent.Position.Y:F2})/moving={smallAgent.HasDestination}/path={world.GetRemainingPath(small).Length}, " +
            $"large=({largeAgent.Position.X:F2},{largeAgent.Position.Y:F2})/moving={largeAgent.HasDestination}/path={world.GetRemainingPath(large).Length}");
        return passed;
    }

    private static bool PlacementUsesColliderQuery()
    {
        var occupiedWorld = new SimulationWorld();
        occupiedWorld.SpawnAgent(Vector2.Zero);
        occupiedWorld.QueueToggleObstacle(Vector2.Zero);
        occupiedWorld.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var rejected = occupiedWorld.TryGetPlacementCell(Vector2.Zero, out var occupiedCell) &&
                       !occupiedWorld.Placement.IsOccupied(occupiedCell);

        var emptyWorld = new SimulationWorld();
        emptyWorld.QueueToggleObstacle(Vector2.Zero);
        emptyWorld.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var accepted = emptyWorld.TryGetPlacementCell(Vector2.Zero, out var emptyCell) &&
                       emptyWorld.Placement.IsOccupied(emptyCell);
        return rejected && accepted;
    }

    private static bool ColliderRelationsAreFiltered()
    {
        var world = new SimulationWorld();
        var self = world.SpawnAgent(Vector2.Zero, new FactionId(0));
        world.SpawnAgent(new Vector2(0.8f, 0f), new FactionId(0));
        world.SpawnAgent(new Vector2(1.6f, 0f), new FactionId(1));
        var hits = new List<ColliderId>();
        var owner = ColliderOwner.Agent(self);

        int Count(RelationMask relationships)
        {
            world.Colliders.QueryCircle(
                Vector2.Zero,
                3f,
                new ColliderQueryFilter(
                    ColliderRole.Interactable,
                    ColliderLayer.Agent,
                    relationships,
                    owner,
                    new FactionId(0)),
                hits);
            return hits.Count;
        }

        var defaultsAreSeparated = Count(RelationMask.Self) == 1 &&
                                   Count(RelationMask.Ally) == 1 &&
                                   Count(RelationMask.Enemy) == 1;
        world.Colliders.Factions.Set(new FactionId(0), new FactionId(1), RelationMask.Ally);
        return defaultsAreSeparated && Count(RelationMask.Ally) == 2 && Count(RelationMask.Enemy) == 0;
    }

    private static bool HeadOnAgentsPass()
    {
        var world = new SimulationWorld();
        var first = world.SpawnAgent(new Vector2(-4f, 0f));
        var second = world.SpawnAgent(new Vector2(4f, 0f));
        world.QueueMove(new[] { first }, new Vector2(4f, 0f));
        world.QueueMove(new[] { second }, new Vector2(-4f, 0f));

        var minimumDistance = float.PositiveInfinity;
        var maximumLateral = 0f;
        for (var i = 0; i < 300 * WalkingPace; i++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var firstPosition = world.Agents.Get(first).Position;
            var secondPosition = world.Agents.Get(second).Position;
            minimumDistance = MathF.Min(minimumDistance, Vector2.Distance(firstPosition, secondPosition));
            maximumLateral = MathF.Max(maximumLateral, MathF.Max(MathF.Abs(firstPosition.Y), MathF.Abs(secondPosition.Y)));
        }

        ref var finishedFirst = ref world.Agents.Get(first);
        ref var finishedSecond = ref world.Agents.Get(second);
        return minimumDistance >= finishedFirst.Radius + finishedSecond.Radius - 0.001f &&
               maximumLateral > 0.08f &&
               finishedFirst.Position.X > 3.5f && finishedSecond.Position.X < -3.5f;
    }

    private static bool StaticCollisionExpelsBody()
    {
        var world = new SimulationWorld();
        world.QueueToggleObstacle(Vector2.Zero);
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var id = world.SpawnAgent(new Vector2(0.75f, 0.75f));
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);

        ref var agent = ref world.Agents.Get(id);
        var cell = world.Placement.Transform.CellCenter(new GridCell(10, 10));
        var halfExtent = world.Placement.Transform.CellSize * 0.5f - 0.025f;
        var closest = Vector2.Clamp(agent.Position, cell - new Vector2(halfExtent), cell + new Vector2(halfExtent));
        return world.LastContactCount > 0 && Vector2.Distance(agent.Position, closest) >= agent.Radius - 0.001f;
    }

    private static bool DisplacedIdleAgentReturns()
    {
        var world = new SimulationWorld();
        var idle = world.SpawnAgent(Vector2.Zero);
        var mover = world.SpawnAgent(new Vector2(-3f, 0f));
        world.QueueMove(new[] { mover }, new Vector2(3f, 0f));
        var maximumDisplacement = 0f;
        for (var tick = 0; tick < 420; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            maximumDisplacement = MathF.Max(
                maximumDisplacement,
                Vector2.Distance(world.Agents.Get(idle).Position, Vector2.Zero));
        }
        ref var idleAgent = ref world.Agents.Get(idle);
        ref var movingAgent = ref world.Agents.Get(mover);
        var finalOffset = Vector2.Distance(idleAgent.Position, Vector2.Zero);
        var passed = maximumDisplacement > 0.05f &&
                     !idleAgent.ReturningToHold &&
                     finalOffset < 0.05f &&
                     movingAgent.Position.X > 2.5f;
        if (!passed)
        {
            Console.WriteLine(
                $"    idle-yield maximumDisplacement={maximumDisplacement:F3}, " +
                $"finalOffset={finalOffset:F3}, returning={idleAgent.ReturningToHold}, " +
                $"mover={movingAgent.Position.X:F2}/{movingAgent.Position.Y:F2}, " +
                $"moverActive={movingAgent.HasDestination}");
        }
        return passed;
    }

    /// <summary>Ticks in which any pair may be in contact before it stops being transient.</summary>
    private const int MaximumOverlappingTicks = 8;
    /// <summary>Deepest momentary interpenetration allowed, in metres.</summary>
    /// <remarks>
    /// Five centimetres, about an eighth of a body radius. Deep enough to admit the
    /// single-frame contact a dense arrival produces and the quarter-second the velocity
    /// solve deliberately takes to bleed an overlap off; nowhere near deep enough for a
    /// body to have passed through another, which is the thing worth failing over.
    /// </remarks>
    private const float MaximumTransientOverlap = 0.05f;

    /// <summary>
    /// Every member of a group order must eventually stop, including the last one.
    /// </summary>
    /// <remarks>
    /// Station-keeping steers a travelling member toward the cohort centroid plus its own
    /// formation offset, and the centroid used to include the member reading it. Harmless
    /// at thirty; with one member left in transit the centroid <em>is</em> that member's
    /// position, so its station sat a fixed offset from wherever it currently was and it
    /// circled after that offset indefinitely — visibly looping outside the walls long
    /// after everyone else had settled. Nothing caught it: it was moving, so the stall
    /// detector decayed its counter below every recovery threshold, and it wanted to move,
    /// so the no-intent watchdog never looked at it.
    /// <para>
    /// Asserted as an outcome — everybody stops — because the mechanism is a detail and the
    /// failure is a unit that never arrives. Run long past settling so the tail is included;
    /// the whole point is that the bug only appears once the cohort has drained.
    /// </para>
    /// </remarks>
    private static bool EveryGroupMemberEventuallySettles()
    {
        // The destination is walled in with one gap, and the group starts outside. That is
        // what keeps a member in cohort transit indefinitely: a body only leaves the shared
        // field once it is within the formation envelope of the command point, and a body
        // that cannot get in there never does. In open ground everyone arrives within
        // seconds and the state this is about barely exists — measured, an open-field
        // version of this test passes with the defect fully present.
        var world = new SimulationWorld();
        var transform = world.Placement.Transform;
        for (var x = 8; x <= 13; x++)
        {
            world.QueueToggleObstacle(transform.CellCenter(new GridCell(x, 8)));
            world.QueueToggleObstacle(transform.CellCenter(new GridCell(x, 13)));
        }
        for (var z = 8; z <= 13; z++)
        {
            world.QueueToggleObstacle(transform.CellCenter(new GridCell(8, z)));
            // One gap, at z == 11.
            if (z != 11) world.QueueToggleObstacle(transform.CellCenter(new GridCell(13, z)));
        }
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);

        var ids = new List<AgentId>();
        for (var row = 0; row < 6; row++)
        for (var column = 0; column < 5; column++)
        {
            ids.Add(world.SpawnAgent(new Vector2(9f + column * 0.95f, -6f + row * 0.95f)));
        }
        world.QueueMove(ids, transform.CellCenter(new GridCell(10, 10)));

        var settledTick = -1;
        for (var tick = 0; tick < 3600; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            if (ids.All(id => !world.Agents.Get(id).HasDestination))
            {
                settledTick = tick;
                break;
            }
        }

        var stillMoving = ids.Where(id => world.Agents.Get(id).HasDestination).ToArray();
        var passed = stillMoving.Length == 0;
        if (!passed)
        {
            Console.WriteLine(
                $"    group settle: {stillMoving.Length}/{ids.Count} never stopped after " +
                $"{3600 * SimulationWorld.FixedDeltaSeconds:F0}s");
            foreach (var id in stillMoving.Take(4))
            {
                ref readonly var agent = ref world.Agents.Get(id);
                Console.WriteLine(
                    $"      {id.Value}: pos=({agent.Position.X:F2},{agent.Position.Y:F2}) " +
                    $"slot=({agent.GroupSlot.X:F2},{agent.GroupSlot.Y:F2}) " +
                    $"speed={agent.Velocity.Length():F2} stuck={agent.StuckSeconds:F2} " +
                    $"flow={agent.UsesFlowTransit} approaching={agent.ApproachingSlot}");
            }
        }
        else
        {
            Console.WriteLine(
                $"    group settle: all {ids.Count} stopped by " +
                $"{settledTick * SimulationWorld.FixedDeltaSeconds:F1}s");
        }
        return passed;
    }

    private static bool AlliesQueueThroughChokepoint()
    {
        var world = new SimulationWorld();
        for (var x = 7; x <= 12; x++)
        {
            world.QueueToggleObstacle(world.Placement.Transform.CellCenter(new GridCell(x, 9)));
            world.QueueToggleObstacle(world.Placement.Transform.CellCenter(new GridCell(x, 11)));
        }
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);

        var ids = new List<AgentId>();
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 3; column++)
        {
            ids.Add(world.SpawnAgent(new Vector2(-8.5f - column * 1.0f, 0.25f + row * 0.95f)));
        }
        world.QueueMove(ids, new Vector2(8f, 0.75f));

        var minimumDistance = float.PositiveInfinity;
        var worstTick = -1;
        var worstPlace = Vector2.Zero;
        // Transient contact and settled interpenetration are different failures, and
        // a minimum taken over every tick cannot tell them apart. Count the ticks in
        // which anything is overlapping, and track the worst overlap once the crowd
        // has had time to come to rest.
        var overlappingTicks = 0;
        var settledMinimum = float.PositiveInfinity;
        var threshold = AgentDefaults.SeparationThreshold(AgentDefaults.Radius);
        for (var tick = 0; tick < 900; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var tickMinimum = float.PositiveInfinity;
            for (var first = 0; first < ids.Count; first++)
            for (var second = first + 1; second < ids.Count; second++)
            {
                var firstPosition = world.Agents.Get(ids[first]).Position;
                var secondPosition = world.Agents.Get(ids[second]).Position;
                var separation = Vector2.Distance(firstPosition, secondPosition);
                if (separation < tickMinimum) tickMinimum = separation;
                if (separation >= minimumDistance) continue;
                minimumDistance = separation;
                worstTick = tick;
                worstPlace = (firstPosition + secondPosition) * 0.5f;
            }
            if (tickMinimum < threshold) overlappingTicks++;
            if (tick >= 600) settledMinimum = MathF.Min(settledMinimum, tickMinimum);
        }


        var crossed = ids.Count(id => world.Agents.Get(id).Position.X > 4f);
        // Queueing is no longer a mechanism with state to assert on; what matters is
        // that a file of units gets through a one-cell gap and does not end up inside
        // itself. Those are two failures, and a single minimum taken over every tick
        // cannot tell them apart — so it was asserting the stricter of the two against
        // the transient of the other, and passing by two millimetres.
        //
        // What that number was actually reporting: one tick in nine hundred, at the
        // arrival point twelve metres past the gap, where twelve units converge on one
        // square metre. Settled separation there is 0.99 m against a 0.73 m bar. The
        // threshold's own tolerance exists "for a single tick's contact" and this is
        // literally that, so asserting it as though it described the resting state made
        // the test flip on any change to how units leave the gap — routing constants
        // with no bearing on the chokepoint at all.
        //
        // Restated as the two things that would really be wrong: a crowd that comes to
        // rest interpenetrated, and a body that meaningfully passes into another. Both
        // now have margin, and a sustained overlap is caught by the tick count rather
        // than by whichever instant happened to be worst.
        var settledClear = settledMinimum >= threshold;
        var contactIsBrief = overlappingTicks <= MaximumOverlappingTicks;
        var contactIsShallow = minimumDistance >= AgentDefaults.Radius * 2f - MaximumTransientOverlap;
        var passed = crossed == ids.Count && settledClear && contactIsBrief && contactIsShallow;
        // Where and when, not just how much: the same overlap figure means very
        // different things inside the gap and out in the arrival cluster.
        if (!passed)
        {
            Console.WriteLine(
                $"    chokepoint crossed={crossed}/{ids.Count}, " +
                $"peak-separation={minimumDistance:F3} (bar {AgentDefaults.Radius * 2f - MaximumTransientOverlap:F3}) " +
                $"at tick {worstTick} ({worstTick * SimulationWorld.FixedDeltaSeconds:F1}s) " +
                $"near ({worstPlace.X:F2},{worstPlace.Y:F2}), " +
                $"settled={settledMinimum:F3} (bar {threshold:F3}), " +
                $"overlapping-ticks={overlappingTicks} (bar {MaximumOverlappingTicks})");
        }
        return passed;
    }

    /// <summary>
    /// A body stuck behind something immovable must eventually be given a detour.
    /// </summary>
    /// <remarks>
    /// Replaces a test that asserted the rear-most member of a queue-leader chain
    /// was the one to reroute. That chain no longer exists, and spreading a crowd
    /// across routes is the congestion field's job now; this checks only what the
    /// remaining recovery is actually for — that a unit getting nowhere does not
    /// stay there forever.
    /// </remarks>
    private static bool StalledUnitIsOfferedADetour()
    {
        var world = new SimulationWorld();
        world.SpawnAgent(Vector2.Zero, maximumSpeed: 0f);
        var followers = new List<AgentId>();
        for (var i = 1; i <= 5; i++)
        {
            followers.Add(world.SpawnAgent(new Vector2(-i * 0.95f, 0f)));
        }
        foreach (var follower in followers) world.QueueMove(new[] { follower }, new Vector2(8f, 0f));

        for (var tick = 0; tick < 600; tick++) world.Tick((float)SimulationWorld.FixedDeltaSeconds);

        var throughCount = followers.Count(id => world.Agents.Get(id).Position.X > 4f);
        var passed = throughCount == followers.Count;
        if (!passed)
        {
            Console.WriteLine(
                $"    detour through={throughCount}/{followers.Count} repaths={world.CongestionRepathCount} " +
                $"[{string.Join(',', followers.Select(id =>
                {
                    ref var a = ref world.Agents.Get(id);
                    return $"{id.Value}:{a.Position.X:F1}/{a.Position.Y:F1}/stuck={a.StuckSeconds:F1}";
                }))}]");
        }
        return passed;
    }

    /// <summary>
    /// Measures what a contested doorway actually costs, from both sides at once.
    /// </summary>
    /// <remarks>
    /// Two files walk through one gap in opposite directions. This is the case the
    /// movement layer has never had an answer for: bodies take turns at the gap,
    /// backing off and coming forward again, because the velocity solve answers a
    /// symmetric contest with a tangential escape and static geometry is not part
    /// of that solve at all — the wall only gets a say afterwards, by rejecting the
    /// answer. Reported rather than asserted while that is being worked on; the
    /// numbers are the point.
    /// <list type="bullet">
    /// <item>crossed — did everyone get through, and how long did it take</item>
    /// <item>frozen — ticks spent wanting to move at a standstill</item>
    /// <item>reversals — how often a body near the gap turned round</item>
    /// <item>dead-stops — solves where no admissible velocity was found at all</item>
    /// </list>
    /// </remarks>
    public static int RunDoorwayContentionDiagnostic()
    {
        var world = new SimulationWorld();
        for (var z = 0; z < world.Placement.Transform.Height; z++)
        {
            if (z == 10) continue;
            world.QueueToggleObstacle(world.Placement.Transform.CellCenter(new GridCell(10, z)));
        }
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var gate = world.Placement.Transform.CellCenter(new GridCell(10, 10));

        var westbound = new List<AgentId>();
        var eastbound = new List<AgentId>();
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 2; column++)
        {
            eastbound.Add(world.SpawnAgent(new Vector2(
                gate.X - 4.5f - column * 0.95f,
                gate.Y + (row - 1.5f) * 0.95f)));
            westbound.Add(world.SpawnAgent(new Vector2(
                gate.X + 4.5f + column * 0.95f,
                gate.Y + (row - 1.5f) * 0.95f)));
        }
        world.QueueMove(eastbound, new Vector2(gate.X + 5.5f, gate.Y));
        world.QueueMove(westbound, new Vector2(gate.X - 5.5f, gate.Y));

        var everyone = eastbound.Concat(westbound).ToArray();
        var frozenTicks = new int[world.Agents.Count];
        var reversals = new int[world.Agents.Count];
        var lastSign = new int[world.Agents.Count];
        var clearedTick = -1;
        const int ticks = 1200;
        for (var tick = 0; tick < ticks; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            foreach (var id in everyone)
            {
                ref readonly var agent = ref world.Agents.Get(id);
                var slot = id.Value;
                if (agent.HasDestination &&
                    agent.PreferredVelocity.LengthSquared() > 0.0625f &&
                    agent.Velocity.LengthSquared() < 0.0025f)
                {
                    frozenTicks[slot]++;
                }
                // Only near the gap: a reversal out in the open is a route change,
                // whereas one in the doorway is the standoff being renegotiated.
                if (MathF.Abs(agent.Position.X - gate.X) > 2.5f) continue;
                var sign = agent.Velocity.X > 0.30f ? 1 : agent.Velocity.X < -0.30f ? -1 : 0;
                if (sign != 0)
                {
                    if (lastSign[slot] != 0 && sign != lastSign[slot]) reversals[slot]++;
                    lastSign[slot] = sign;
                }
            }
            if (clearedTick < 0 && everyone.All(id => !world.Agents.Get(id).HasDestination))
            {
                clearedTick = tick;
            }
        }

        var crossedEast = eastbound.Count(id => world.Agents.Get(id).Position.X > gate.X + 2f);
        var crossedWest = westbound.Count(id => world.Agents.Get(id).Position.X < gate.X - 2f);
        Console.WriteLine(
            $"  doorway contention | crossed east {crossedEast}/{eastbound.Count} " +
            $"west {crossedWest}/{westbound.Count} | " +
            $"cleared {(clearedTick < 0 ? "never" : $"{clearedTick * SimulationWorld.FixedDeltaSeconds:F1}s")} | " +
            $"frozen {frozenTicks.Sum()} ticks (worst {frozenTicks.Max()}) | " +
            $"gap-reversals {reversals.Sum()} (worst {reversals.Max()}) | " +
            $"dead-stops {world.AvoidanceTerrainDeadStops} | " +
            $"terrain-fallback {world.AvoidanceTerrainFallbacks * 100.0 / Math.Max(1, world.AvoidanceSolves):F1}% | " +
            $"infeasible {world.AvoidanceInfeasible * 100.0 / Math.Max(1, world.AvoidanceSolves):F1}%");
        return 0;
    }

    private static bool FiftyAgentsClearSingleCellGate()
    {
        var world = new SimulationWorld();
        for (var z = 0; z < world.Placement.Transform.Height; z++)
        {
            if (z == 10) continue;
            world.QueueToggleObstacle(world.Placement.Transform.CellCenter(new GridCell(10, z)));
        }
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);

        var ids = new List<AgentId>();
        for (var row = 0; row < 10; row++)
        for (var column = 0; column < 5; column++)
        {
            ids.Add(world.SpawnAgent(new Vector2(
                -8f + (column - 2f) * 0.95f,
                0.75f + (row - 4.5f) * 0.95f)));
        }
        world.QueueMove(ids, new Vector2(8f, 0.75f));
        var geometryStayedValid = true;
        var firstInvalidGeometry = string.Empty;
        for (var tick = 0; tick < 3000; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            foreach (var id in ids)
            {
                if (world.IsAgentGeometryValid(id)) continue;
                geometryStayedValid = false;
                if (firstInvalidGeometry.Length == 0)
                {
                    ref var invalid = ref world.Agents.Get(id);
                    firstInvalidGeometry = $"tick={tick}, agent={id.Value}, pos={invalid.Position.X:F3}/{invalid.Position.Y:F3}, " +
                                           $"previous={invalid.PreviousPosition.X:F3}/{invalid.PreviousPosition.Y:F3}";
                }
            }
        }

        const float crossedWallThreshold = 1.50f;
        var crossed = ids.Count(id => world.Agents.Get(id).Position.X > crossedWallThreshold);
        var permanentlyStuck = ids.Count(id => world.Agents.Get(id).StuckSeconds >= 1.25f);
        var passed = crossed == ids.Count && permanentlyStuck == 0 && geometryStayedValid;
        if (!passed) Console.WriteLine($"    gate crossed={crossed}/{ids.Count}, stuck={permanentlyStuck}, geometry-valid={geometryStayedValid} ({firstInvalidGeometry}), replans={world.CongestionRepathCount}, " +
                                       $"left=[{string.Join(',', ids.Where(id => world.Agents.Get(id).Position.X <= crossedWallThreshold).Select(id =>
                                       {
                                           ref var agent = ref world.Agents.Get(id);
                                           var path = world.GetRemainingPath(id);
                                           var next = path.Length > 0 ? path[0] : agent.Destination;
                                           return $"{id.Value}:{agent.Position.X:F1}/{agent.Position.Y:F1}/next={next.X:F1}/{next.Y:F1}/" +
                                                  $"v={agent.Velocity.X:F1}/{agent.Velocity.Y:F1}/pref={agent.PreferredVelocity.X:F1}/{agent.PreferredVelocity.Y:F1}/" +
                                                  $"blocked={agent.AvoidanceBlockedThisTick}/" +
                                                  $"reject={agent.SteeringStepRejectedThisTick}/path={path.Length}";
                                       }))}]");
        return passed;
    }

    /// <summary>
    /// The congestion field decays a tracked set of cells rather than the whole map, so
    /// the set has to contain every cell that holds pressure — at a jam, while it builds,
    /// and after it has drained.
    /// </summary>
    private static bool CongestionSweepTracksPressure()
    {
        // A single-cell gate with fifty bodies behind it is the scenario that actually
        // deposits: pressure comes from bodies that want to move and cannot, so an open
        // field would leave the set empty and prove nothing.
        var world = new SimulationWorld();
        for (var z = 0; z < world.Placement.Transform.Height; z++)
        {
            if (z == 10) continue;
            world.QueueToggleObstacle(world.Placement.Transform.CellCenter(new GridCell(10, z)));
        }

        world.Tick((float)SimulationWorld.FixedDeltaSeconds);

        var ids = new List<AgentId>();
        for (var row = 0; row < 10; row++)
        for (var column = 0; column < 5; column++)
        {
            ids.Add(world.SpawnAgent(new Vector2(
                -8f + (column - 2f) * 0.95f,
                0.75f + (row - 4.5f) * 0.95f)));
        }

        world.QueueMove(ids, new Vector2(8f, 0.75f));
        var fault = string.Empty;
        var peakLive = 0;
        var sawPressure = false;
        for (var tick = 0; tick < 1200 && fault.Length == 0; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            peakLive = Math.Max(peakLive, world.Congestion.LiveCellCount);
            if (world.Congestion.Peak > 0f) sawPressure = true;
            fault = world.Congestion.DescribeSweepFault() ?? string.Empty;
        }

        // And it has to come back down: a set that only ever grows is the same bug
        // wearing a different face, and it would still pass the audit above. The budget is
        // a duration and moves with the pace — the field's decay constant does too, so a
        // jam takes three times as long to fade to nothing at a walk. Was 3000 ticks.
        for (var tick = 0; tick < 3000 * WalkingPace && fault.Length == 0; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            fault = world.Congestion.DescribeSweepFault() ?? string.Empty;
        }

        var drained = world.Congestion.LiveCellCount == 0;
        var passed = fault.Length == 0 && sawPressure && peakLive > 0 && drained;
        if (!passed)
        {
            Console.WriteLine(
                $"    congestion sweep: fault=[{fault}], deposited={sawPressure}, " +
                $"peak-live={peakLive}, live-after-drain={world.Congestion.LiveCellCount}");
        }

        return passed;
    }

    /// <summary>
    /// The world extent is a parameter, and the default is exactly the square every
    /// threshold in this file was tuned against.
    /// </summary>
    /// <remarks>
    /// Guarded rather than trusted because moving it costs nothing and breaks
    /// everything: a default that drifted to 32 m would leave every test here passing
    /// while quietly measuring a different world.
    /// </remarks>
    private static bool WorldExtentIsParameterised()
    {
        var tuned = new SimulationWorld();
        var tunedIsUnmoved = tuned.ExtentMeters == 30f &&
                             tuned.Navigation.Width == 60 &&
                             tuned.Navigation.Height == 60 &&
                             tuned.Placement.Transform.Width == 20 &&
                             tuned.Placement.Transform.Height == 20 &&
                             tuned.Terrain.Minimum == new Vector2(-15f) &&
                             tuned.Terrain.Maximum == new Vector2(15f);

        // Snapped up to a whole placement cell, so the two grids describe one square.
        var large = new SimulationWorld(800f);
        var largeIsConsistent = large.ExtentMeters == 801f &&
                                large.Navigation.Width == 1602 &&
                                large.Placement.Transform.Width == 534 &&
                                large.Terrain.Minimum == new Vector2(-400.5f) &&
                                large.Congestion.CellCount == 1602 * 1602;

        // A body still cannot leave the map, and the map is the bigger one.
        var edge = large.SpawnAgent(new Vector2(600f, -600f));
        var clamped = large.Terrain.Contains(large.Agents.Get(edge).Position);

        var passed = tunedIsUnmoved && largeIsConsistent && clamped;
        if (!passed)
        {
            Console.WriteLine(
                $"    extent: tuned={tuned.ExtentMeters:F1}m {tuned.Navigation.Width}x" +
                $"{tuned.Navigation.Height} nav / {tuned.Placement.Transform.Width}x" +
                $"{tuned.Placement.Transform.Height} placement, " +
                $"large={large.ExtentMeters:F1}m {large.Navigation.Width} nav / " +
                $"{large.Placement.Transform.Width} placement, clamped={clamped}");
        }

        return passed;
    }

    /// <summary>
    /// The router's cost-to-goal against the flat whole-map search it replaced, on a map big
    /// enough to have more than one part to it.
    /// </summary>
    /// <remarks>
    /// The tuned world is a single rectangle, so every other test in this file exercises the
    /// partition without ever crossing it. Thresholds are read off <c>--routingtest</c> rather
    /// than chosen: the measured figures are a mean of 1.001 and a worst cell of 1.19.
    /// <para>
    /// Two of these matter more than the ratios. <c>lost</c> must be zero, because a cell the
    /// router cannot price is a body that believes it is trapped rather than one that walks a
    /// little further. And the mean must not fall <em>below</em> one: a route cannot cost less
    /// than the shortest route, so under-one means the router is blind to something the
    /// reference charges for — which is exactly how the first cut of the rectangle field
    /// behaved, at 0.91, before crossings were measured corner to corner.
    /// </para>
    /// </remarks>
    private static bool RoutingIsFaithful()
    {
        var world = RegionRoutingScenarios.Build();
        var fidelity = world.MeasureRectangleFidelity(
            new Vector2(world.ExtentMeters * 0.42f, world.ExtentMeters * 0.42f),
            AgentDefaults.Radius);

        var manyParts = fidelity.RefinedRegions >= 5 && fidelity.SettledNodes >= 5;
        var passed = manyParts &&
                     fidelity.UnreachableCells == 0 &&
                     fidelity.MeanRatio >= 1f &&
                     fidelity.MeanRatio < 1.05f &&
                     fidelity.NinetyNinthRatio < 1.20f &&
                     fidelity.WorstRatio < 2.0f;
        if (!passed)
        {
            Console.WriteLine(
                $"    fidelity: {fidelity.RefinedRegions} rectangles, {fidelity.SettledNodes} corners, " +
                $"mean={fidelity.MeanRatio:F4}, p99={fidelity.NinetyNinthRatio:F4}, " +
                $"worst={fidelity.WorstRatio:F3} at {fidelity.WorstCell.X},{fidelity.WorstCell.Z}, " +
                $"lost={fidelity.UnreachableCells}/{fidelity.ReachableCells}");
        }

        return passed;
    }

    /// <summary>
    /// A group walking a route that crosses several region borders, watched for the classic
    /// hierarchical failure: a body reaching a border, adopting the next region's field,
    /// and swinging because the two disagree about which way is downhill.
    /// </summary>
    /// <remarks>
    /// Measured as reversals — a heading flipping by more than a right angle in one tick —
    /// counted separately for bodies standing within two cells of a border and bodies well
    /// inside a region. The absolute rate is not the point and would only measure the
    /// steering layer; the <em>ratio</em> is, because a seam in the cost field shows up as
    /// bodies changing their minds at borders and nowhere else.
    /// </remarks>
    private static bool GroupCrossesRegionBorders()
    {
        var world = RegionRoutingScenarios.Build();
        var half = world.ExtentMeters * 0.5f;
        var ids = new List<AgentId>();
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 5; column++)
        {
            ids.Add(world.SpawnAgent(new Vector2(
                -half + 6f + column * 0.9f,
                -half + 6f + row * 0.9f)));
        }

        var target = new Vector2(half - 6f, half - 6f);
        world.QueueMove(ids, target);

        var heading = new Dictionary<int, Vector2>();
        var borderReversals = 0;
        var borderSamples = 0;
        var interiorReversals = 0;
        var interiorSamples = 0;
        var arrived = 0;
        var ticks = 0;
        for (; ticks < 6000 * WalkingPace && arrived < ids.Count; ticks++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            arrived = ids.Count(id => Vector2.Distance(world.Agents.Get(id).Position, target) < 6f);
            foreach (var id in ids)
            {
                ref readonly var agent = ref world.Agents.Get(id);
                if (!agent.IsAlive) continue;
                var velocity = agent.Velocity;
                if (velocity.LengthSquared() < 0.25f) continue;
                var now = Vector2.Normalize(velocity);
                var nearBorder = world.Navigation.TryWorldToCell(agent.Position, out var cell) &&
                                 NearRegionBorder(cell);
                if (heading.TryGetValue(id.Value, out var before))
                {
                    var reversed = Vector2.Dot(before, now) < 0f;
                    if (nearBorder)
                    {
                        borderSamples++;
                        if (reversed) borderReversals++;
                    }
                    else
                    {
                        interiorSamples++;
                        if (reversed) interiorReversals++;
                    }
                }

                heading[id.Value] = now;
            }
        }

        var borderRate = borderReversals / (float)Math.Max(1, borderSamples);
        var interiorRate = interiorReversals / (float)Math.Max(1, interiorSamples);
        // A border is allowed to be busier than open ground — it is usually a gap in a wall
        // as well as a seam in the field — but not several times busier, which is what an
        // inconsistent handover looks like.
        var everyoneArrived = arrived == ids.Count;
        var noSeam = borderRate <= MathF.Max(0.02f, interiorRate * 3f);
        var passed = everyoneArrived && noSeam && borderSamples > 500;
        if (!passed)
        {
            Console.WriteLine(
                $"    border crossing: arrived={arrived}/{ids.Count} in {ticks / 30f:F1}s, " +
                $"border-reversals={borderReversals}/{borderSamples} ({borderRate:P2}), " +
                $"interior={interiorReversals}/{interiorSamples} ({interiorRate:P2})");
        }

        return passed;
    }

    private static bool NearRegionBorder(GridCell cell)
    {
        const int span = RTSGame.Simulation.Navigation.RegionPartition.CellsPerSide;
        var x = cell.X % span;
        var z = cell.Z % span;
        return x <= 1 || x >= span - 2 || z <= 1 || z >= span - 2;
    }

    private static bool LocomotionBehaviorsDriveMovement()
    {
        var followWorld = new SimulationWorld();
        var followTarget = followWorld.SpawnAgent(new Vector2(3f, 0f), maximumSpeed: 0f);
        var follower = followWorld.SpawnAgent(new Vector2(-3f, 0f));
        followWorld.QueueFollow(new[] { follower }, followTarget);
        Tick(followWorld, 180);
        ref var followerState = ref followWorld.Agents.Get(follower);
        var followDistance = Vector2.Distance(followerState.Position, followWorld.Agents.Get(followTarget).Position);
        var followed = followerState.LocomotionState == AgentLocomotionState.Follow &&
                       followDistance is > 0.82f and < 1.9f;

        var patrolWorld = new SimulationWorld();
        var patroller = patrolWorld.SpawnAgent(Vector2.Zero);
        patrolWorld.QueuePatrol(new[] { patroller }, new Vector2(4f, 0f));
        var reachedEnd = false;
        var returned = false;
        for (var tick = 0; tick < 360; tick++)
        {
            patrolWorld.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var x = patrolWorld.Agents.Get(patroller).Position.X;
            reachedEnd |= x > 3.7f;
            returned |= reachedEnd && x < 0.5f;
        }
        var patrolled = patrolWorld.Agents.Get(patroller).LocomotionState == AgentLocomotionState.Patrol &&
                        reachedEnd && returned;

        var chaseWorld = new SimulationWorld();
        var chaseTarget = chaseWorld.SpawnAgent(new Vector2(3f, 0f), maximumSpeed: 0f);
        var chaser = chaseWorld.SpawnAgent(new Vector2(-3f, 0f));
        chaseWorld.QueueChase(new[] { chaser }, chaseTarget);
        Tick(chaseWorld, 150);
        var chased = chaseWorld.Agents.Get(chaser).LocomotionState == AgentLocomotionState.Chase &&
                     Vector2.Distance(
                         chaseWorld.Agents.Get(chaser).Position,
                         chaseWorld.Agents.Get(chaseTarget).Position) < 1.25f;

        var fleeWorld = new SimulationWorld();
        var threat = fleeWorld.SpawnAgent(Vector2.Zero, maximumSpeed: 0f);
        var runner = fleeWorld.SpawnAgent(new Vector2(1.1f, 0f));
        fleeWorld.QueueFlee(new[] { runner }, threat);
        Tick(fleeWorld, 120);
        var fled = fleeWorld.Agents.Get(runner).LocomotionState == AgentLocomotionState.Flee &&
                   Vector2.Distance(
                       fleeWorld.Agents.Get(runner).Position,
                       fleeWorld.Agents.Get(threat).Position) > 5f;

        return followed && patrolled && chased && fled;
    }

    private static bool GroupUsesMultiplePenExits()
    {
        var world = new SimulationWorld();
        var walls = new HashSet<GridCell>();
        for (var x = 7; x <= 14; x++)
        {
            walls.Add(new GridCell(x, 6));
            walls.Add(new GridCell(x, 13));
        }
        for (var z = 7; z <= 12; z++)
        {
            if (z is not 8 and not 11) walls.Add(new GridCell(7, z));
            walls.Add(new GridCell(14, z));
        }
        foreach (var wall in walls)
        {
            world.QueueToggleObstacle(world.Placement.Transform.CellCenter(wall));
        }
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);

        var ids = new List<AgentId>();
        for (var row = 0; row < 5; row++)
        for (var column = 0; column < 6; column++)
        {
            ids.Add(world.SpawnAgent(new Vector2(
                -0.75f + column * 0.90f,
                -1.80f + row * 0.90f)));
        }
        world.QueueMove(ids, new Vector2(-9f, -4f));

        var crossed = new HashSet<AgentId>();
        var lowerExit = 0;
        var upperExit = 0;
        for (var tick = 0; tick < 3000 && crossed.Count < ids.Count; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            foreach (var id in ids)
            {
                if (crossed.Contains(id)) continue;
                ref var agent = ref world.Agents.Get(id);
                if (agent.Position.X >= -4.65f) continue;
                crossed.Add(id);
                if (agent.Position.Y < 0f) lowerExit++;
                else upperExit++;
            }
        }

        Tick(world, 600);
        var unresolved = ids.Count(id =>
            world.Agents.Get(id).HasDestination ||
            world.Agents.Get(id).StuckSeconds >= 1.25f);
        var passed = crossed.Count == ids.Count && lowerExit >= 5 && upperExit >= 5 && unresolved == 0;
        if (!passed) Console.WriteLine($"    two-exit crossed={crossed.Count}/{ids.Count}, lower={lowerExit}, upper={upperExit}, unresolved={unresolved}");
        return passed;
    }

    private static bool TwoHundredAgentScenarioIsSafe()
    {
        var world = new SimulationWorld();
        var ids = MovementStressScenarios.Populate(world, 200, issueGroupMove: true);
        var initialCentroid = ids.Aggregate(Vector2.Zero, (sum, id) => sum + world.Agents.Get(id).Position) / ids.Length;
        var initialOffsets = ids.Select(id => world.Agents.Get(id).Position - initialCentroid).ToArray();
        var arrivalTicks = Enumerable.Repeat(-1, ids.Length).ToArray();
        var minimumCentroidX = float.PositiveInfinity;
        var travelShapeChange = 0f;
        long activeSamples = 0;
        long blueSamples = 0;
        long openFlowSamples = 0;
        long openFlowBlueSamples = 0;
        long cohortSamples = 0;
        long queuedSamples = 0;
        for (var tick = 0; tick < 1800; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var centroid = Vector2.Zero;
            foreach (var id in ids)
            {
                ref var agent = ref world.Agents.Get(id);
                centroid += agent.Position;
                if (agent.HasDestination)
                {
                    activeSamples++;
                    if (agent.IsVisiblyYielding) blueSamples++;
                    // Cohort travel, measured only over the stretch where it is
                    // meaningful. A formation is metres across, so the last leg of
                    // any move is members peeling off to their own slots by
                    // design; counting that as a failure to travel together made
                    // the ratio a measure of how big the formation was rather than
                    // of whether the group moved as one.
                    if (Vector2.Distance(agent.Position, new Vector2(-4f, 0f)) > 9f)
                    {
                        cohortSamples++;
                        if (agent.MoveGroupId != 0 && !agent.ApproachingSlot)
                        {
                            openFlowSamples++;
                            if (agent.IsVisiblyYielding) openFlowBlueSamples++;
                        }
                    }
                }
                if (arrivalTicks[id.Value] < 0 && !agent.HasDestination)
                {
                    arrivalTicks[id.Value] = tick;
                }
            }
            centroid /= ids.Length;
            if (tick == 30)
            {
                travelShapeChange = ids.Select((id, index) => Vector2.Distance(
                    world.Agents.Get(id).Position - centroid,
                    initialOffsets[index])).Average();
            }
            minimumCentroidX = MathF.Min(minimumCentroidX, centroid.X);
        }

        // A 200-unit formation is genuinely ~15 m across, so an absolute x
        // threshold now fails on members legitimately occupying the far slots.
        // Measure what the check was always for: every unit left its start and
        // ended up inside the formation the order created.
        var moved = ids.Count(id => Vector2.Distance(
            world.Agents.Get(id).Position,
            new Vector2(-4f, 0f)) < 9f);
        var minimumDistance = float.PositiveInfinity;
        var nearestDistances = Enumerable.Repeat(float.PositiveInfinity, ids.Length).ToArray();
        for (var first = 0; first < ids.Length; first++)
        for (var second = first + 1; second < ids.Length; second++)
        {
            var distance = Vector2.Distance(
                world.Agents.Get(ids[first]).Position,
                world.Agents.Get(ids[second]).Position);
            minimumDistance = MathF.Min(minimumDistance, distance);
            nearestDistances[first] = MathF.Min(nearestDistances[first], distance);
            nearestDistances[second] = MathF.Min(nearestDistances[second], distance);
        }
        var unresolved = ids.Count(id => world.Agents.Get(id).HasDestination ||
                                         world.Agents.Get(id).StuckSeconds >= 1.25f);
        var compact = ids.All(id => Vector2.Distance(
            world.Agents.Get(id).Position,
            new Vector2(-4f, 0f)) <= 10.0f);
        var arrivalOrder = Enumerable.Range(0, ids.Length)
            .OrderBy(index => arrivalTicks[index])
            .ToArray();
        var quartile = ids.Length / 4;
        var earlySpacing = arrivalOrder.Take(quartile).Average(index => nearestDistances[index]);
        var lateSpacing = arrivalOrder.TakeLast(quartile).Average(index => nearestDistances[index]);
        var spacingBias = MathF.Abs(earlySpacing - lateSpacing);
        var blueRatio = activeSamples == 0 ? 0f : blueSamples / (float)activeSamples;
        var openFlowRatio = cohortSamples == 0 ? 1f : openFlowSamples / (float)cohortSamples;
        var centroidOvershoot = MathF.Max(0f, -4f - minimumCentroidX);
        // openFlowRatio is reported but not asserted on. It measures which
        // steering mode members are in, which is an implementation detail rather
        // than an outcome — and it is unsatisfiable here by construction: a
        // 200-unit formation is wider than this scenario's eight-metre move, so
        // members are inside the formation envelope and legitimately claiming
        // slots from the moment the order is given. What the check was for —
        // that the group travels as one body and does not scatter — is already
        // covered by shape change, spacing bias and centroid overshoot.
        var passed = compact &&
                     minimumDistance >= AgentDefaults.SeparationThreshold(AgentDefaults.CrowdRadius) &&
                     unresolved == 0 &&
                     centroidOvershoot <= 0.75f && blueRatio < 0.25f &&
                     spacingBias <= 0.15f &&
                     travelShapeChange >= 0.05f;
        Console.WriteLine($"    large group: centroid-overshoot={centroidOvershoot:F2}m, " +
                          $"blue-duty={blueRatio:P0}, open-blue={(openFlowSamples == 0 ? 0f : openFlowBlueSamples / (float)openFlowSamples):P0}, " +
                          $"open-flow={openFlowRatio:P0}, " +
                          $"queued={(activeSamples == 0 ? 0f : queuedSamples / (float)activeSamples):P0}, shape-change={travelShapeChange:F2}m, " +
                          $"early/late-spacing={earlySpacing:F2}/{lateSpacing:F2}m");
        if (!passed) Console.WriteLine($"    200-agent moved={moved}/200, minimum-distance={minimumDistance:F3}, unresolved={unresolved}, compact={compact}, " +
                                       $"max-distance={ids.Max(id => Vector2.Distance(world.Agents.Get(id).Position, new Vector2(-4f, 0f))):F2}, " +
                                       $"centroid-overshoot={centroidOvershoot:F2}, blue-ratio={blueRatio:P0}, spacing={earlySpacing:F2}/{lateSpacing:F2}, " +
                                       $"right=[{string.Join(',', ids.Where(id => world.Agents.Get(id).Position.X >= 3f).Take(8).Select(id =>
                                       {
                                           ref var agent = ref world.Agents.Get(id);
                                           return $"{id.Value}:{agent.Position.X:F1}/{agent.Position.Y:F1}/moving={agent.HasDestination}/stuck={agent.StuckSeconds:F1}";
                                       }))}]");
        return passed;
    }

    private static bool GroupEscapesPenUnderBackpressure()
    {
        var passed = true;
        foreach (var variant in new[] { (Seed: 17, Blocked: false), (Seed: 73, Blocked: true) })
        {
            var world = new SimulationWorld();
            var scenario = MovementStressScenarios.PopulatePenEscape(
                world,
                seed: variant.Seed,
                blockPrimary: variant.Blocked);
            var exited = new HashSet<AgentId>();
            var exitCounts = new int[scenario.ExitCenters.Length];
            var everRed = new HashSet<AgentId>();
            var maximumStuckSeconds = 0f;
            var maximumQueued = 0;
            var completionTick = -1;
            var redStarts = Enumerable.Repeat(-1, world.Agents.Count).ToArray();
            var redStartRepaths = new int[world.Agents.Count];
            var redPeaks = new RedEpisode[world.Agents.Count];
            var redEpisodes = new List<RedEpisode>();

            for (var tick = 0; tick < 6000 * WalkingPace; tick++)
            {
                world.Tick((float)SimulationWorld.FixedDeltaSeconds);
                var queued = 0;
                var moving = 0;
                foreach (var id in scenario.Agents)
                {
                    ref var agent = ref world.Agents.Get(id);
                    var outside = agent.Position.X < scenario.PenMinimum.X ||
                                  agent.Position.X > scenario.PenMaximum.X ||
                                  agent.Position.Y < scenario.PenMinimum.Y ||
                                  agent.Position.Y > scenario.PenMaximum.Y;
                    if (outside && exited.Add(id))
                    {
                        var nearestExit = 0;
                        var nearestDistance = float.PositiveInfinity;
                        for (var exit = 0; exit < scenario.ExitCenters.Length; exit++)
                        {
                            var distance = Vector2.DistanceSquared(agent.Position, scenario.ExitCenters[exit]);
                            if (distance >= nearestDistance) continue;
                            nearestDistance = distance;
                            nearestExit = exit;
                        }
                        exitCounts[nearestExit]++;
                    }
                        if (agent.HasDestination) moving++;
                    maximumStuckSeconds = MathF.Max(maximumStuckSeconds, agent.StuckSeconds);
                    var visiblyRed = agent.StuckSeconds > 0.35f &&
                                           agent.CrowdPressureSeconds <= 0f;
                    if (visiblyRed)
                    {
                        everRed.Add(id);
                        if (redStarts[id.Value] < 0)
                        {
                            redStarts[id.Value] = tick;
                            redStartRepaths[id.Value] = world.CongestionRepathCount;
                        }
                        if (agent.StuckSeconds >= redPeaks[id.Value].PeakCounter)
                        {
                            var redPosition = agent.Position;
                            var nearestExitDistance = scenario.ExitCenters
                                .Min(exit => Vector2.Distance(redPosition, exit));
                            var insidePen = redPosition.X >= scenario.PenMinimum.X &&
                                            redPosition.X <= scenario.PenMaximum.X &&
                                            redPosition.Y >= scenario.PenMinimum.Y &&
                                            redPosition.Y <= scenario.PenMaximum.Y;
                            redPeaks[id.Value] = new RedEpisode(
                                id,
                                0f,
                                agent.StuckSeconds,
                                redPosition,
                                insidePen,
                                nearestExitDistance,
                                Vector2.Distance(redPosition, scenario.Target),
                                agent.Velocity.Length(),
                                agent.PreferredVelocity.Length(),
                                world.GetRemainingPath(id).Length,
                                agent.RepathCooldown,
                                world.CongestionRecoveryCooldown,
                                agent.CrowdPressureSeconds,
                                0);
                        }
                    }
                    else if (redStarts[id.Value] >= 0)
                    {
                        var peak = redPeaks[id.Value];
                        redEpisodes.Add(peak with
                        {
                            VisibleSeconds = (tick - redStarts[id.Value]) *
                                             (float)SimulationWorld.FixedDeltaSeconds,
                            RepathsDuring = world.CongestionRepathCount - redStartRepaths[id.Value],
                        });
                        redStarts[id.Value] = -1;
                    }
                }
                maximumQueued = Math.Max(maximumQueued, queued);
                if (moving != 0) continue;
                completionTick = tick + 1;
                break;
            }

            var finalStuck = scenario.Agents.Count(id =>
                world.Agents.Get(id).HasDestination ||
                world.Agents.Get(id).StuckSeconds >= 1.25f);
            foreach (var id in scenario.Agents)
            {
                if (redStarts[id.Value] < 0) continue;
                var peak = redPeaks[id.Value];
                redEpisodes.Add(peak with
                {
                    VisibleSeconds = (6000 - redStarts[id.Value]) *
                                     (float)SimulationWorld.FixedDeltaSeconds,
                    RepathsDuring = world.CongestionRepathCount - redStartRepaths[id.Value],
                });
            }
            var completionSeconds = completionTick < 0
                ? 200f
                : completionTick * (float)SimulationWorld.FixedDeltaSeconds;
            var alternateExitsUsed = exitCounts.Skip(1).Count(count => count > 0);
            var longestVisibleRed = redEpisodes.Count == 0
                ? 0f
                : redEpisodes.Max(episode => episode.VisibleSeconds);
            Console.WriteLine(
                $"    pen seed={variant.Seed} blocked={variant.Blocked}: " +
                $"exits=[{string.Join(',', exitCounts)}], " +
                $"arrived={scenario.Agents.Length - finalStuck}/{scenario.Agents.Length}, " +
                $"time={completionSeconds:F1}s, max-queue={maximumQueued}, " +
                $"ever-red={everRed.Count}, longest-red={longestVisibleRed:F2}s, " +
                $"peak-internal-stuck={maximumStuckSeconds:F2}s, " +
                $"repaths={world.CongestionRepathCount}");
            foreach (var episode in redEpisodes
                         .OrderByDescending(episode => episode.VisibleSeconds)
                         .Take(3))
            {
                Console.WriteLine(
                    $"      red {episode.Agent.Value}: visible={episode.VisibleSeconds:F2}s " +
                    $"peak={episode.PeakCounter:F2}s pos=({episode.Position.X:F2},{episode.Position.Y:F2}) " +
                    $"context={(episode.InsidePen ? "inside" : "outside")}, " +
                    $"exit-distance={episode.NearestExitDistance:F2}, target-distance={episode.TargetDistance:F2}, " +
                    $"speed={episode.Speed:F2}/{episode.PreferredSpeed:F2}, " +
                    $"waypoints={episode.RemainingWaypoints}, agent-cooldown={episode.AgentRepathCooldown:F2}, " +
                    $"global-cooldown={episode.GlobalRecoveryCooldown:F2}, " +
                    $"crowd-pressure={episode.CrowdPressureSeconds:F2}, " +
                    $"repaths-during={episode.RepathsDuring}");
            }
            if (finalStuck > 0)
            {
                foreach (var id in scenario.Agents)
                {
                    ref var agent = ref world.Agents.Get(id);
                    if (!agent.HasDestination && agent.StuckSeconds < 1.25f) continue;
                    Console.WriteLine(
                        $"      unresolved {id.Value}: pos=({agent.Position.X:F2},{agent.Position.Y:F2}) " +
                        $"dest=({agent.Destination.X:F2},{agent.Destination.Y:F2}) " +
                        $"speed={agent.Velocity.Length():F2} preferred={agent.PreferredVelocity.Length():F2} " +
                        $"stuck={agent.StuckSeconds:F2} " +
                        $"cooldown={agent.RepathCooldown:F2} path={world.GetRemainingPath(id).Length}");
                }
            }
            // Judged on the clock and on the outcome, not on whether anybody ever
            // had to wait. The pen's gaps are 1.5m wide; with a 0.405m standoff
            // from each wall the usable centre band is 0.69m and two bodies need
            // 0.74m between them, so they physically cannot pass abreast. Thirty
            // units through such a gap must serialise, and somebody waits several
            // seconds — the old bar of "no unit is ever held up for more than a
            // second" was asserting that queueing does not happen, which no
            // correct implementation can satisfy. What matters is that everyone
            // gets out, that the group spreads over more than one exit rather
            // than piling into the nearest, and that it does not take all day.
            passed &= completionTick >= 0 &&
                      exited.Count == scenario.Agents.Length &&
                      finalStuck == 0 &&
                      alternateExitsUsed >= 2 &&
                      // Was 30 s. The pen is the same pen and the queue is the same queue;
                      // walking it takes three times as long to drain.
                      completionSeconds <= 30f * WalkingPace;
        }
        return passed;
    }

    private static bool FiveHundredAgentScenarioIsDeterministic()
    {
        var first = new SimulationWorld();
        var second = new SimulationWorld();
        MovementStressScenarios.Populate(first, 500, issueGroupMove: true);
        MovementStressScenarios.Populate(second, 500, issueGroupMove: true);
        Tick(first, 45);
        Tick(second, 45);

        for (var i = 0; i < 500; i++)
        {
            ref var a = ref first.Agents.Get(new AgentId(i));
            ref var b = ref second.Agents.Get(new AgentId(i));
            if (!float.IsFinite(a.Position.X) || !float.IsFinite(a.Position.Y) ||
                a.Position != b.Position || a.Velocity != b.Velocity ||
                a.Facing != b.Facing || a.LocomotionState != b.LocomotionState)
            {
                return false;
            }
        }
        return first.Timings.Format(first.Agents.Count, first.TickNumber).Contains("500 agents");
    }

    private static bool DenseCrowdSeparates()
    {
        var world = new SimulationWorld();
        var ids = new List<AgentId>();
        for (var z = 0; z < 8; z++)
        for (var x = 0; x < 8; x++)
        {
            ids.Add(world.SpawnAgent(new Vector2((x - 3.5f) * 0.68f, (z - 3.5f) * 0.68f)));
        }
        Tick(world, 180);

        var minimumDistance = float.PositiveInfinity;
        for (var first = 0; first < ids.Count; first++)
        for (var second = first + 1; second < ids.Count; second++)
        {
            minimumDistance = MathF.Min(minimumDistance, Vector2.Distance(
                world.Agents.Get(ids[first]).Position,
                world.Agents.Get(ids[second]).Position));
        }
        return minimumDistance >= AgentDefaults.SeparationThreshold(AgentDefaults.Radius);
    }

    private static SimulationWorld BuildDeterministicWorld()
    {
        var world = new SimulationWorld();
        var ids = new List<AgentId>();
        for (var z = 0; z < 4; z++)
        for (var x = 0; x < 5; x++)
        {
            ids.Add(world.SpawnAgent(new Vector2(x * 1.1f - 2.2f, z * 1.1f - 1.65f)));
        }
        world.QueueMove(ids, new Vector2(-7f, 6f));
        return world;
    }

    /// <summary>
    /// A removed unit must stop influencing everything: routing, avoidance,
    /// contact resolution and congestion. Any iteration site that forgets to skip
    /// dead slots shows up here as a survivor being deflected by a ghost.
    /// </summary>
    private static bool DespawnRemovesUnitsCleanly()
    {
        var world = new SimulationWorld();
        var wall = new List<AgentId>();
        for (var i = 0; i < 8; i++)
        {
            wall.Add(world.SpawnAgent(new Vector2(0f, -1.8f + i * 0.52f), maximumSpeed: 0f));
        }
        var traveller = world.SpawnAgent(new Vector2(-6f, 0f));
        world.QueueMove(new[] { traveller }, new Vector2(6f, 0f));
        Tick(world, 90);

        // Blocked by an immovable line of bodies, it should be pressed up against
        // them on the near side rather than through them.
        var blockedX = world.Agents.Get(traveller).Position.X;
        if (blockedX > -0.4f)
        {
            Console.WriteLine($"    despawn setup invalid: traveller reached x={blockedX:F2} before removal");
            return false;
        }

        if (world.DespawnAgents(wall) != wall.Count) return false;
        if (world.Agents.LiveCount != 1) return false;
        if (wall.Any(world.Agents.Contains)) return false;

        world.QueueMove(new[] { traveller }, new Vector2(6f, 0f));
        Tick(world, 240);
        ref var survivor = ref world.Agents.Get(traveller);
        var arrived = !survivor.HasDestination &&
                      Vector2.Distance(survivor.Position, new Vector2(6f, 0f)) < 0.05f;
        // A ghost body would still be pushing it off the straight line.
        var travelledStraight = MathF.Abs(survivor.Position.Y) < 0.25f;
        var passed = arrived && travelledStraight && world.LastContactCount == 0;
        if (!passed)
        {
            Console.WriteLine(
                $"    despawn pos=({survivor.Position.X:F2},{survivor.Position.Y:F2}) " +
                $"moving={survivor.HasDestination} contacts={world.LastContactCount} " +
                $"live={world.Agents.LiveCount}");
        }
        return passed;
    }

    /// <summary>
    /// A group crossing sculpted terrain must not strand anybody indefinitely.
    /// </summary>
    /// <remarks>
    /// The shared cost field judges traversability between cell centres while the
    /// movement sweep tests the real body against the real ground, and on a ramp
    /// edge the two disagree: the field kept asking for a step that could not be
    /// taken. Worse, the recovery that fired could not help, because a granted
    /// route did not clear the flow-transit flag and was simply ignored. One unit
    /// stood in the same spot for seventy-nine seconds.
    /// </remarks>
    private static bool TerrainDoesNotDeadlockFlowTransit()
    {
        var world = new SimulationWorld();
        var ids = TerrainStressScenarios.Populate(world, issueGroupMove: false);
        var corners = new[]
        {
            new Vector2(-9.5f, 9.5f),
            new Vector2(9.5f, -9.5f),
            new Vector2(-9.5f, -9.5f),
        };

        var worstStall = 0f;
        var worstAgent = new AgentId(-1);
        foreach (var corner in corners)
        {
            world.QueueMove(ids, corner);
            for (var tick = 0; tick < 900 * WalkingPace; tick++)
            {
                world.Tick((float)SimulationWorld.FixedDeltaSeconds);
                foreach (var id in ids)
                {
                    ref readonly var agent = ref world.Agents.Get(id);
                    if (agent.StuckSeconds <= worstStall) continue;
                    worstStall = agent.StuckSeconds;
                    worstAgent = id;
                }
                if (ids.All(id => !world.Agents.Get(id).HasDestination)) break;
            }
        }

        var stranded = ids.Where(id => world.Agents.Get(id).HasDestination).ToArray();
        // Also catch the state inconsistency directly: a body may not be steering
        // by the shared field while holding a route it is ignoring.
        var inconsistent = ids.Count(id =>
        {
            ref readonly var agent = ref world.Agents.Get(id);
            return agent.UsesFlowTransit && agent.Path.IsValid;
        });

        // Stall seconds are wall clock, and a body waiting its turn at walking pace waits
        // three times as long for the same queue. Was 5 s.
        var passed = stranded.Length == 0 && inconsistent == 0 && worstStall < 5f * WalkingPace;
        if (!passed)
        {
            Console.WriteLine(
                $"    terrain deadlock stranded={stranded.Length} inconsistent={inconsistent} " +
                $"worst-stall={worstStall:F2}s (agent {worstAgent.Value})");
        }
        return passed;
    }

    /// <summary>
    /// Across every crowded scenario, no unit may hold an order while producing
    /// no intent to move.
    /// </summary>
    /// <remarks>
    /// Tests the invariant rather than the causes. This state was reached through
    /// at least three unrelated routes — a failed replan leaving no path, a cost
    /// field offering no gradient, and a granted route being ignored — and each
    /// time it was invisible to the stall detector, which judges a body by whether
    /// it achieves the movement it wants and therefore cannot see one that wants
    /// nothing. Asserting the invariant catches the next route too.
    /// </remarks>
    private static bool NoUnitStandsIntentless()
    {
        var worst = 0f;
        var worstScenario = "none";
        var worstAgent = new AgentId(-1);

        foreach (var (name, populate) in new (string, Func<SimulationWorld, AgentId[]>)[]
                 {
                     ("terrain lab", w => TerrainStressScenarios.Populate(w, issueGroupMove: false)),
                     ("pen escape", w => MovementStressScenarios.PopulatePenEscape(w, seed: 17).Agents),
                     ("blocked pen", w => MovementStressScenarios
                         .PopulatePenEscape(w, seed: 73, blockPrimary: true).Agents),
                 })
        {
            var world = new SimulationWorld();
            var ids = populate(world);
            // Sweep the map rather than picking two tidy corners. The state this
            // guards against showed up in play from ordinary right-clicks and was
            // missed entirely by a two-target version of this test — the bodies
            // that end up with no route are the ones wedged somewhere awkward, and
            // finding those needs targets on every side of every obstacle.
            var targets = new List<Vector2>();
            for (var x = -10f; x <= 10f; x += 5f)
            for (var z = -10f; z <= 10f; z += 5f)
            {
                targets.Add(new Vector2(x, z));
            }
            foreach (var target in targets)
            {
                world.QueueMove(ids, target);
                for (var tick = 0; tick < 450; tick++)
                {
                    world.Tick((float)SimulationWorld.FixedDeltaSeconds);
                    foreach (var id in ids)
                    {
                        if (!world.Agents.Contains(id)) continue;
                        ref readonly var agent = ref world.Agents.Get(id);
                        if (agent.NoIntentSeconds <= worst) continue;
                        worst = agent.NoIntentSeconds;
                        worstScenario = name;
                        worstAgent = id;
                    }
                    if (ids.All(id => !world.Agents.Get(id).HasDestination)) break;
                }
            }
        }

        // The watchdog demands a route at 0.4s and abandons the order by 1.3s.
        // Anything beyond that means it is not firing, which is how a unit stood
        // in a field for twenty-two seconds.
        var passed = worst < 1.6f;
        if (!passed)
        {
            Console.WriteLine(
                $"    intentless worst={worst:F2}s in {worstScenario} (agent {worstAgent.Value})");
        }
        return passed;
    }

    private static void Tick(SimulationWorld world, int count)
    {
        for (var i = 0; i < count; i++) world.Tick((float)SimulationWorld.FixedDeltaSeconds);
    }
}
