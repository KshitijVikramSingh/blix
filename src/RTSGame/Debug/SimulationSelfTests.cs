using System.Numerics;
using RTSGame.Control;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Commands;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;
using RTSGame.Simulation.Navigation;
using RTSGame.Simulation.Persistence;
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
        Check("impassable slopes are walked up to and never crossed", ImpassableSlopeIsNotCrossed());
        Check("placement rejects unsuitable terrain", PlacementRejectsUnsuitableTerrain());
        Check("crowd crosses a traversable terrain ramp", CrowdCrossesTerrainRamp());
        Check("crowd rounds a ramp-cliff corner without sticking", CrowdRoundsTerrainCorner());
        Check("terrain edits invalidate active routes", TerrainEditInvalidatesRoute());
        Check("a cached climb outlives a building and not a hill", ACachedClimbOutlivesABuildingAndNotAHill());
        Check("terrain-aware simulations stay deterministic", TerrainSimulationsMatch());
        Check("repath recovers from a nearby invalid start cell", RepathRecoversFromInvalidStart());
        Check(
            "a faction sees, remembers, and keeps its knowledge to itself",
            FactionKnowledgeSeesRemembersAndStaysPrivate());
        Check(
            "an order from ground the body does not fit on joins the field",
            AnOrderFromMarginalGroundJoinsTheField());
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
        Check("large bodies avoid each other before touching", LargeBodiesAvoidBeforeContact());
        Check("a mixed-size crowd files through a gate without overlap", MixedSizeCrowdIsSafe());
        Check("every roster radius is inside one clearance rung", RadiusRungsShareADecomposition());
        Check("every unit type routes on one decomposition", UnitTypesRouteByClass());
        Check("a body with a turning circle cannot come about like a person", WagonComesAboutSlowly());
        Check("a scout outpaces a villager in proportion to its speed", ScoutOutpacesVillager());
        Check("warning does not shrink as bodies get faster", WarningHoldsAcrossSpeeds());
        Check("a queue is worth more to a body that would otherwise be quick", CongestionIsPricedBySpeed());
        Check("a latecomer arrives at ground a settled crowd is standing on", LatecomerArrivesAtOccupiedGround());
        Check("a queue is worth more to a body that is wide", CongestionIsPricedByWidth());
        Check("the fingerprint reads every value a body carries", FingerprintReadsEveryBodyValue());
        Check(
            "a local re-rasterisation leaves the same grid as a full one",
            LocalRasterMatchesFullRebuild());
        Check("every field of the world is fingerprinted or argued away", WorldStateIsFullyAccountedFor());
        Check("a divergence is reported on the tick it happens", DivergenceIsCaughtWhenItAppears());
        Check("the checkpoint fingerprint reads the ground, not only the bodies", FullScopeReadsTheMap());
        Check("a standing assignment works with nobody watching", StandingAssignmentWorksUnwatched());
        Check("a standing assignment is parked by an order and given back on request", AssignmentSurvivesAnInterrupt());
        Check("an order holds until overridden and is never a trap", AnOrderNeverBecomesAMode());
        Check("a cohort owns its roster, and every departure names a reason", ACohortOwnsItsRoster());
        Check("a cohort outlives its move and is adopted by the next order", ACohortOutlivesItsMove());
        Check("a cohort is never grown, merged or reinforced", ACohortIsNeverGrown());
        Check("a crew survives the orders given to it and forgets its dead", ACrewSurvivesWhatIsDoneToIt());
        Check("a job's reach is written in bodies", JobReachIsWrittenInBodies());
        Check("an unreachable job fails politely", AnUnreachableJobFailsPolitely());
        Check("a workplace holds more hands than fit on it", AWorkplaceHoldsMoreHandsThanFitOnIt());
        Check("a fast body closes on a slow one", AFastBodyClosesOnASlowOne());
        Check("a saved world has the same future", ASavedWorldHasTheSameFuture());
        Check("a save this build cannot read is refused", ABadSaveIsRefused());
        Check("every order kind survives a save", EveryOrderKindSurvivesASave());
        Check("the year adds up and its rates are normalised", TheYearAddsUp());
        Check("the calendar is a knob and nothing wrote its answers down", TheCalendarIsAKnob());
        Check("a settlement feeds itself without losing a grain", ASettlementFeedsItself());
        Check("a working settlement runs identically twice", TheEconomyRunsIdenticallyTwice());
        Check("dropped cargo stays in the world and is recovered", DroppedCargoStaysInTheWorld());
        Check("a crop is three windows of labour", ACropIsThreeWindowsOfLabour());
        Check("a field yields what its labour earned it", AFieldYieldsWhatItsLabourEarned());
        Check("every resource is counted by the sums that name their fields", EveryResourceIsCounted());
        Check("wood comes out of trees, and trees run out", WoodComesOutOfTreesAndTreesRunOut());
        Check(
            "a lumber camp is a store nobody eats from, and that is what makes haulers",
            TheWoodLineDecidesWhetherHaulersAreNeeded());
        Check("a cart is a job a villager takes, and pays for", ACartIsAJobAndNotAUnit());
        Check("posting at an outcrop establishes quarry work", PostingAtAnOutcropEstablishesQuarryWork());
        Check("one route supplies every material a site needs", OneRouteSuppliesEveryMaterial());
        Check("a new builder's first trip is useful", ANewBuildersFirstTripIsUseful());
        Check("builders fetch, share, consume and clear a project", BuildersFetchShareConsumeAndClear());
        Check("a building costs timber carried out and hands standing at it", ABuildingCostsLabour());
        Check("repair consumes delivered material as condition returns", RepairConsumesAsConditionReturns());
        Check("a palisade becomes stone on the same stable node", APalisadeBecomesStoneOnTheSameNode());
        Check("a barracks turns the same villager into militia", ABarracksTrainsTheSameVillager());
        Check("housing caps a population and food brakes it", PeopleArriveWhenThereIsRoomAndFood());
        Check("a neighbour's full larder is not yours", NeighbourLarderIsNotYours());
        Check("a village is not founded in a forest", AVillageIsNotFoundedInAForest());
        Check("the bot budgets what a wall really costs", TheBotKnowsWhatAWallCosts());
        Check("a household that goes hungry loses somebody", PrivationSpendsItselfAsEmigration());
        Check("a wood hides what walks through it", TreesBlockSight());
        Check(
            "only as many defend as the fight needs, and the nearest ones go",
            OnlyTheNeededDefend());
        Check("a defender puts its load down before it joins", HandsAreFreedBeforeAFight());
        Check("only as many can fight a body as fit around it", AFightHasAFront());
        Check("a world full of standing assignments runs identically twice", JobsRunsIdenticallyTwice());
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
            // Flat, which is what this fixture's hand-built projection assumes.
            _ => 0f,
            projection,
            100,
            100,
            additive: false,
            player: new FactionId(0));
        return selection.Contains(inside) && !selection.Contains(outside);
    }

    private static bool IdenticalSimulationsMatch()
    {
        var fault = DeterminismCheck.Diverges(
            BuildDeterministicWorld(), BuildDeterministicWorld(), 180);
        if (fault is not null) Console.WriteLine($"    {fault}");
        return fault is null;
    }

    /// <summary>
    /// Every value a body carries has to reach the fingerprint. Perturb each one in turn
    /// and the fingerprint has to move.
    /// </summary>
    /// <remarks>
    /// This is the test that makes the coverage claim worth anything. The schema is derived
    /// from the struct by reflection, so it cannot forget a field — but "derived by
    /// reflection" is a claim about a mechanism, and the mechanism has several ways to be
    /// silently wrong: a nested struct walked to the wrong depth, a bool read as the address
    /// of a bool, a field whose two values happen to fold to the same number. A digest
    /// nobody has tried to fool is not evidence. So each of the seventy-odd leaves is
    /// changed to a different value, one at a time, and the fingerprint has to notice all of
    /// them.
    /// <para>
    /// It also fails when a field is added whose type the schema cannot reduce — the schema
    /// throws while being built rather than skipping it — which is the alarm that replaces
    /// remembering to extend this file.
    /// </para>
    /// </remarks>
    private static bool FingerprintReadsEveryBodyValue()
    {
        var world = BuildDeterministicWorld();
        Tick(world, 40);

        var missed = new List<string>();
        var bodies = world.Agents.MutableSpan();
        var baseline = DeterminismCheck.Fingerprint(world, DeterminismCheck.Scope.Tick);
        foreach (var leaf in AgentStateSchema.Leaves)
        {
            var original = bodies[0];
            bodies[0] = AgentStateSchema.Perturb(original, leaf);
            // Deliberately the Tick scope: perturbing a path handle to a handle that was
            // never issued would make the Full walk ask the pool for a route it does not
            // hold, which is a fair thing for the pool to refuse and not what is under test.
            var perturbed = DeterminismCheck.Fingerprint(world, DeterminismCheck.Scope.Tick);
            bodies[0] = original;
            if (perturbed == baseline) missed.Add(leaf.Name);
        }

        if (missed.Count > 0)
        {
            Console.WriteLine(
                $"    fingerprint blind to {missed.Count} of {AgentStateSchema.Leaves.Count} " +
                $"body values: {string.Join(", ", missed.Take(12))}");
            return false;
        }

        // Printed rather than asserted against a number. What the right count is depends on
        // what a body carries, which is a moving target by design; what matters is that it
        // is all of them, and the loop above is what establishes that.
        Console.WriteLine($"    fingerprint reads {AgentStateSchema.Leaves.Count} values per body");
        return DeterminismCheck.Fingerprint(world, DeterminismCheck.Scope.Tick) == baseline;
    }

    /// <summary>
    /// Every field of the world is classified as carried, derived or wall clock. A field
    /// that is none of those is a hole in the determinism check, and holes in a passing test
    /// do not announce themselves.
    /// </summary>
    /// <summary>
    /// A placement change re-rasterised locally must leave exactly the grid a full rebuild would.
    /// </summary>
    /// <remarks>
    /// <b>The guard on the only interesting claim §101 makes.</b> Skipping 1.44M terrain samples and most of a
    /// clearance pass is only sound if the answer is unchanged, and "should be identical" is precisely the kind
    /// of statement that stops being true without anybody noticing — a widened window off by a cell, a stale
    /// blocked flag, an obstacle bound gathered on the full path and forgotten on the local one. So the test
    /// builds a world with relief, blocks some ground, refreshes it locally, and then compares every cell
    /// against the same world rebuilt from scratch: blocked, clearance, height, traversal cost and speed.
    /// <para>
    /// Sculpted on purpose. On flat ground the terrain half of the raster is uniform and a bug in it cannot
    /// show, which is how a test like this passes while the feature is broken on every map anybody plays.
    /// </para>
    /// </remarks>
    private static bool LocalRasterMatchesFullRebuild()
    {
        var world = new SimulationWorld(200f);
        world.Terrain.SetRegion(Simulation.Terrain.Region.Downland);
        var layout = Simulation.Terrain.MapLayout.Composed(
            Simulation.Terrain.Archetype.SplitValley, 200f, 0x5EED1234u, 24f);
        Simulation.Terrain.ReliefPlan.FromLayout(layout, 200f, 0x5EED1234u).Apply(world.Terrain);
        world.RebuildTerrainNavigation();

        // A short wall, away from the edges, so the widened clearance window is interior on every side.
        // <b>The placement grid's own transform, not navigation's.</b> They have different cell sizes — 1.5 m
        // against 0.5 — so a navigation cell index handed to the placement grid is a different place, and
        // usually off its edge, which is how the first version of this test blocked nothing and said so.
        if (!world.Placement.Transform.TryWorldToCell(new Vector2(10f, -6f), out var centre))
        {
            Console.WriteLine("    the test's wall position is off the grid");
            return false;
        }

        for (var dx = 0; dx < 6; dx++)
        {
            world.Placement.SetOccupied(new GridCell(centre.X + dx, centre.Z), true);
        }

        // The path under test: local refresh, driven by the dirty rectangle the placement grid accumulated.
        var localBefore = NavigationRasterizer.LocalRebuilds;
        var occupied = world.Placement.OccupiedCells.Count;
        world.RefreshNavigationForTest();
        if (NavigationRasterizer.LocalRebuilds == localBefore)
        {
            Console.WriteLine(
                $"    the refresh did not take the local path: {occupied} cells occupied, " +
                $"terrain revision {world.Terrain.Revision} against rasterised " +
                $"{world.RasterisedTerrainRevisionForTest}");
            return false;
        }

        world.Navigation.ReadRasterForTest(
            out var localBlocked, out var localClearance, out var localHeights,
            out var localCosts, out var localSpeeds);
        NavigationRasterizer.Rebuild(world.Placement, world.Navigation, world.Terrain);
        world.Navigation.ReadRasterForTest(
            out var fullBlocked, out var fullClearance, out var fullHeights,
            out var fullCosts, out var fullSpeeds);

        for (var index = 0; index < fullBlocked.Length; index++)
        {
            if (localBlocked[index] == fullBlocked[index] &&
                localClearance[index] == fullClearance[index] &&
                localHeights[index] == fullHeights[index] &&
                localCosts[index] == fullCosts[index] &&
                localSpeeds[index] == fullSpeeds[index])
            {
                continue;
            }

            var cell = new GridCell(index % world.Navigation.Width, index / world.Navigation.Width);
            Console.WriteLine(
                $"    cell {cell.X},{cell.Z} differs: blocked {localBlocked[index]}/{fullBlocked[index]}, " +
                $"clearance {localClearance[index]:F4}/{fullClearance[index]:F4}, " +
                $"height {localHeights[index]:F4}/{fullHeights[index]:F4}, " +
                $"cost {localCosts[index]:F4}/{fullCosts[index]:F4}, " +
                $"speed {localSpeeds[index]:F4}/{fullSpeeds[index]:F4}");
            return false;
        }

        return true;
    }

    private static bool WorldStateIsFullyAccountedFor()
    {
        var fault = DeterminismCheck.CensusFault();
        if (fault is not null) Console.WriteLine($"    {fault}");
        return fault is null;
    }

    /// <summary>
    /// A check that reports the wrong tick is nearly as bad as one that reports nothing, so
    /// introduce a difference at a known tick and require it to be named.
    /// </summary>
    /// <remarks>
    /// The field perturbed here is <c>StuckSeconds</c>, chosen because the check this
    /// replaces was blind to it: it compares positions, and a body's stall clock takes
    /// seconds to turn into a position. This asserts both halves of the instrument — that it
    /// sees the field at all, and that it says <em>tick 24</em> rather than reporting a
    /// position at tick 60.
    /// </remarks>
    private static bool DivergenceIsCaughtWhenItAppears()
    {
        const int injectAt = 24;
        var fault = DeterminismCheck.Diverges(
            BuildDeterministicWorld(),
            BuildDeterministicWorld(),
            ticks: 60,
            fullEvery: 30,
            afterTick: tick => { });
        if (fault is not null)
        {
            Console.WriteLine($"    control run diverged from itself: {fault}");
            return false;
        }

        var second = BuildDeterministicWorld();
        var injected = DeterminismCheck.Diverges(
            BuildDeterministicWorld(),
            second,
            ticks: 60,
            fullEvery: 30,
            afterTick: tick =>
            {
                if (tick != injectAt) return;
                second.Agents.MutableSpan()[3].StuckSeconds += 0.5f;
            });

        var passed = injected is not null &&
                     injected.StartsWith($"tick {injectAt}:", StringComparison.Ordinal) &&
                     injected.Contains("StuckSeconds", StringComparison.Ordinal);
        Console.WriteLine($"    injected at tick {injectAt}, reported: {injected ?? "nothing"}");
        return passed;
    }

    /// <summary>
    /// The checkpoint scope has to actually visit the map, the stored routes and the
    /// colliders — the parts too large to compare every tick and therefore the parts an
    /// early return would silently skip.
    /// </summary>
    /// <remarks>
    /// Structural rather than behavioural, deliberately. Everything the wider scope reads is
    /// either derived from something the per-tick scope already reads, or reached through a
    /// revision counter that it reads, so there is no state that only the wide walk can
    /// notice — which means the honest thing to assert is that the walk goes there at all.
    /// The value-level proof lives in the body probe, where it can be made properly.
    /// </remarks>
    private static bool FullScopeReadsTheMap()
    {
        var world = new SimulationWorld();
        var id = world.SpawnAgent(new Vector2(-8f, 0f));
        world.QueueToggleObstacle(new Vector2(2f, 2f));
        world.QueueMove(new[] { id }, new Vector2(8f, 3f));
        Tick(world, 20);

        var narrow = DeterminismCheck.Trace(world, DeterminismCheck.Scope.Tick);
        var wide = DeterminismCheck.Trace(world, DeterminismCheck.Scope.Full);
        var sections = new[] { "raster.", "terrain.", "blocks.", "collider[", "route[" };
        var missing = sections.Where(section =>
            !wide.Any(entry => entry.Label.StartsWith(section, StringComparison.Ordinal))).ToArray();
        var leaked = sections.Where(section =>
            narrow.Any(entry => entry.Label.StartsWith(section, StringComparison.Ordinal))).ToArray();

        if (missing.Length > 0 || leaked.Length > 0)
        {
            Console.WriteLine(
                $"    checkpoint scope missing [{string.Join(", ", missing)}], " +
                $"per-tick scope reading [{string.Join(", ", leaked)}]");
            return false;
        }

        Console.WriteLine($"    fingerprint reads {narrow.Count:N0} values a tick, {wide.Count:N0} at a checkpoint");
        return wide.Count > narrow.Count;
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

    /// <summary>
    /// A cached climb outlives a building and not a hill: same routes across a placement change, and the
    /// cache dropped the moment the ground itself moves.
    /// </summary>
    /// <remarks>
    /// <b>The claim §114 rests on, and the way it could be wrong.</b> The corner-climb cache is now keyed by
    /// the terrain revision rather than the navigation one, so it survives a building going up — worth nine
    /// times fewer height samples on the click after a placement. The whole saving depends on a single
    /// sentence being true: a placement change cannot move the ground. If it ever stops being true, or if
    /// the key stops distinguishing two points that differ, the cache serves an answer for terrain that is
    /// no longer there and routes bend around hills that are not in front of them. That is a quiet
    /// divergence, and this arc has shipped two.
    /// <para>
    /// So the test is a comparison rather than an assertion about the cache: one world warms the cache, then
    /// takes the placement change; another is born with the change already in it and a cold cache. If the
    /// surviving entries are honest the two must price the same ground identically, and if any of them is
    /// stale they cannot. Sculpted relief deliberately — on flat ground every climb is zero and a cache
    /// returning nonsense returns the right nonsense.
    /// </para></remarks>
    private static bool ACachedClimbOutlivesABuildingAndNotAHill()
    {
        static SimulationWorld Sculpted()
        {
            var world = new SimulationWorld();
            // A ridge, so climbs along the sampled legs are not all zero.
            for (var z = 12; z <= 44; z++)
            for (var x = 24; x <= 30; x++)
            {
                world.Terrain.SetVertexHeight(x, z, 3.5f);
            }

            return world;
        }

        static void Build(SimulationWorld world)
        {
            if (!world.Placement.Transform.TryWorldToCell(new Vector2(2f, 2f), out var cell)) return;
            for (var dx = 0; dx < 4; dx++)
            {
                world.Placement.SetOccupied(new GridCell(cell.X + dx, cell.Z), true);
            }

            world.RefreshNavigationForTest();
        }

        var goal = new Vector2(11f, 11f);
        var probes = new List<Vector2>();
        for (var i = 0; i < 24; i++) probes.Add(new Vector2(-12f + i * 1.0f, -11f + i * 0.9f));

        // Warmed, then built on: the cache is carrying answers from before the mesh was replaced.
        var warmed = Sculpted();
        // <b>Let the raster catch up with the sculpting before reading the revision it is supposed to hold
        // still.</b> Raising a ridge moves the terrain revision once per vertex, and the grid only records
        // which terrain it sampled when the rasteriser next runs — so a revision read here, before any
        // rebuild, is the number from before the ridge existed, and the placement change below then appears
        // to have moved it. The first run of this test failed exactly that way and the code was right.
        warmed.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var terrainBefore = warmed.Navigation.TerrainRevision;
        var navBefore = warmed.Navigation.Revision;
        foreach (var probe in probes) warmed.CostToGoal(probe, goal, AgentDefaults.Radius);
        Build(warmed);
        var terrainAfterBuild = warmed.Navigation.TerrainRevision;
        var navAfterBuild = warmed.Navigation.Revision;

        // Cold, and born with the building already there.
        var fresh = Sculpted();
        Build(fresh);

        var matched = 0;
        var mismatched = 0;
        foreach (var probe in probes)
        {
            var a = warmed.CostToGoal(probe, goal, AgentDefaults.Radius);
            var b = fresh.CostToGoal(probe, goal, AgentDefaults.Radius);
            if (a is null != (b is null)) { mismatched++; continue; }
            if (a is { } x && b is { } y && MathF.Abs(x - y) > 0.001f) { mismatched++; continue; }
            matched++;
        }

        // And the ground moving does evict it, which is the only thing it may not survive.
        for (var z = 12; z <= 20; z++)
        for (var x = 34; x <= 38; x++)
        {
            warmed.Terrain.SetVertexHeight(x, z, 6f);
        }

        warmed.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var terrainAfterHill = warmed.Navigation.TerrainRevision;

        var survivedTheBuilding = terrainAfterBuild == terrainBefore && navAfterBuild > navBefore;
        var droppedForTheHill = terrainAfterHill > terrainAfterBuild;
        var passed = survivedTheBuilding && droppedForTheHill && mismatched == 0 && matched == probes.Count;
        Console.WriteLine(
            $"    across a building: terrain revision {terrainBefore} -> {terrainAfterBuild} " +
            $"(navigation {navBefore} -> {navAfterBuild}), {matched}/{probes.Count} probes priced " +
            $"identically to a cold world, {mismatched} differed | across a hill: terrain revision " +
            $"-> {terrainAfterHill} ({(droppedForTheHill ? "evicted" : "NOT EVICTED")})");
        return passed;
    }

    /// <summary>
    /// A body ordered past an impassable step walks up to it and stops on its own side.
    /// </summary>
    /// <remarks>
    /// <b>This test used to assert the body did not move at all, and that expectation was retired
    /// deliberately.</b> §103 chose best effort over refusal: an order to somewhere unreachable resolves to the
    /// nearest ground the body can actually stand on, so a unit told to cross a cliff walks to the foot of it
    /// rather than standing still with an order it has silently declined. Standing still was measured as the
    /// worse behaviour — it is what a player reads as a bug, and it was what an eighteen-second freeze looked
    /// like from the chair.
    /// <para>
    /// So the assertion is now the pair that actually matters, and it is stronger than the one it replaces:
    /// the body must SET OFF, and it must never end up on the far side of the step. The old test could have
    /// passed with a pathfinder that refused every route in the game.
    /// </para>
    /// </remarks>
    private static bool ImpassableSlopeIsNotCrossed()
    {
        var world = new SimulationWorld();
        var terrain = world.Terrain;
        for (var z = 0; z <= terrain.Transform.Height; z++)
        for (var x = 31; x <= terrain.Transform.Width; x++)
            terrain.SetVertexHeight(x, z, 1.5f);
        world.RebuildTerrainNavigation();

        var id = world.SpawnAgent(new Vector2(-6f, 0f));
        var start = world.Agents.Get(id).Position;
        world.QueueMove(new[] { id }, new Vector2(6f, 0f));
        var bestEffort = world.LastOrderWasBestEffort;
        Tick(world, 240);
        ref readonly var agent = ref world.Agents.Get(id);
        var crossed = agent.Position.X > 0.5f;
        var setOff = agent.Position.X > start.X + 0.5f;
        if (crossed)
        {
            Console.WriteLine($"    the body crossed the step: it is at x {agent.Position.X:F2}");
            return false;
        }

        if (!setOff)
        {
            Console.WriteLine(
                $"    the body never set off: x {start.X:F2} to {agent.Position.X:F2}, " +
                $"order reported best effort: {bestEffort}");
            return false;
        }

        return true;
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
        TerrainStressScenarios.Populate(first);
        TerrainStressScenarios.Populate(second);
        var fault = DeterminismCheck.Diverges(first, second, 600);
        if (fault is not null) Console.WriteLine($"    {fault}");
        return fault is null;
    }

    /// <summary>
    /// A body ordered from ground its own cell will not admit joins the shared field rather than
    /// searching the map for its slot.
    /// </summary>
    /// <remarks>
    /// <b>§116, and the number it guards is 1,254 ms.</b> The click after a placement change spent 70% of
    /// itself on two cross-map searches, and the cause was two bodies resting a hand's width over a clearance
    /// line beside a wall: <c>SampleFlowGradient</c> reads the cost at the body's own cell, gets infinity and
    /// refuses, where <c>FindPath</c> would have resolved that start outward and routed. So the order path
    /// answered a fifteen-centimetre overhang with a quarter of a million cell expansions, and one of the two
    /// bodies got nothing for it.
    /// <para>
    /// <b>The precondition is asserted, not assumed.</b> This test is only meaningful while the body it places
    /// really is standing somewhere the grid refuses it and really is physically clear of the block — a setup
    /// that quietly stops reproducing the condition would go on passing and guard nothing. Both are checked
    /// and both fail loudly.
    /// </para>
    /// <para>
    /// What it asserts is the reason, not the cost: exactly one <see cref="RouteReason.OrderFieldEntry"/> and
    /// no <see cref="RouteReason.OrderSlot"/>. A cost threshold would pass on a small map for the wrong
    /// reason, and the whole finding was that a count of queries cannot tell two situations apart.
    /// </para></remarks>
    private static bool AnOrderFromMarginalGroundJoinsTheField()
    {
        var world = new SimulationWorld();
        world.QueueToggleObstacle(Vector2.Zero);
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        if (!world.TryGetPlacementCell(Vector2.Zero, out var blockCell)) return false;

        const float radius = AgentDefaults.Radius;
        var center = world.Placement.Transform.CellCenter(blockCell);
        var half = world.Placement.Transform.CellSize * 0.5f;
        // <b>Inside the cell beside the block, at the far edge of it.</b> That cell's centre is half a cell
        // from the block, so it offers a quarter-metre of clearance and refuses a 37 cm body — while a body
        // standing at its far edge is 45 cm from the block and physically fine. That gap between "where the
        // body is" and "what its cell says" is the whole of §116.
        //
        // allowEmbedded because the spawn nudge exists precisely to prevent this: it walks a body out to
        // ground its radius fits on, which is the right default and would erase the case under test.
        var navigationCell = world.Navigation.Transform.CellSize;
        var beside = center - new Vector2(half + navigationCell * 0.9f, 0f);
        var marginal = world.SpawnAgent(beside, radius: radius, allowEmbedded: true);
        var companion = world.SpawnAgent(beside - new Vector2(3f, 0f), radius: radius);

        // <b>The cell the body is actually in, not the one it was aimed at.</b> Spawning can adjust a
        // position, and a precondition asserted about somewhere the body is not would pass while guarding
        // nothing — which is exactly the failure this test is written to prevent one level up.
        var standing = world.Agents.Get(marginal).Position;
        if (!world.Navigation.TryWorldToCell(standing, out var cell)) return false;
        var cellAdmitsBody = world.Navigation.IsWalkable(cell, radius);
        var bodyIsClear = world.IsAgentGeometryValid(marginal);
        if (cellAdmitsBody || !bodyIsClear)
        {
            Console.WriteLine(
                $"    marginal-ground PRECONDITION lost: cell admits body={cellAdmitsBody} " +
                $"(clearance {world.Navigation.Clearance(cell):F3} m, needs " +
                $"{radius + BodyFootprint.NavigationMargin:F3}), body clear of block={bodyIsClear}");
            return false;
        }

        var target = new Vector2(11f, 11f);
        var routesBefore = world.Routes.Snapshot();
        world.QueueMove(new[] { marginal, companion }, target);
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var rows = world.Routes.Since(routesBefore);
        var entries = rows.Where(row => row.Reason == RouteReason.OrderFieldEntry).Sum(row => row.Queries);
        var slotPaths = rows.Where(row => row.Reason == RouteReason.OrderSlot).Sum(row => row.Queries);

        // And it has to actually get there: an entry hop that is a dead end would satisfy everything above.
        Tick(world, 400 * WalkingPace);
        ref var agent = ref world.Agents.Get(marginal);
        var arrived = Vector2.Distance(agent.Position, target) < 3f;
        var passed = entries == 1 && slotPaths == 0 && arrived;
        if (!passed) Console.WriteLine(
            $"    marginal-ground entries={entries} slotPaths={slotPaths} arrived={arrived} " +
            $"residual={Vector2.Distance(agent.Position, target):F2} m " +
            $"expansions={rows.Sum(row => row.Expansions)} " +
            $"reasons=[{string.Join(", ", rows.Select(row => $"{row.Reason}x{row.Queries}"))}] " +
            $"refusals={world.GradientRefusals} outcomes={world.OrderOutcomes} " +
            $"stoodAt=({standing.X:F3},{standing.Y:F3}) cell=({cell.X},{cell.Z}) " +
            $"clearance={world.Navigation.Clearance(cell):F3}");
        return passed;
    }

    /// <summary>
    /// A faction sees where it stands, remembers where it has been, and knows nothing of the rest.
    /// </summary>
    /// <remarks>
    /// <b>The three properties that make this knowledge rather than fog.</b> §119 reserved a per-faction
    /// aggregate as simulation state and §131 built it, and what has to be true of it is: a faction can see
    /// around its own people, it still knows ground it has left — that is the difference between seeing and
    /// having seen — and it knows nothing about ground only somebody else has walked. The third is the one a
    /// rule-bot depends on and the one a bug would quietly remove: an aggregate that answered for every
    /// faction at once would pass the first two.
    /// </remarks>
    private static bool FactionKnowledgeSeesRemembersAndStaysPrivate()
    {
        // <b>A world big enough for two factions to be strangers.</b> On the default thirty-metre square the
        // two bodies stood sixteen metres apart with twenty-two metres of sight each, so each could see the
        // other's ground and "keeps its knowledge to itself" failed on a faction that was simply looking at
        // it. The privacy claim needs distance to mean anything.
        var world = new SimulationWorld(240f);
        var mine = new FactionId(0);
        var theirs = new FactionId(1);
        var here = new Vector2(-70f, 0f);
        var away = new Vector2(70f, 0f);

        var scout = world.SpawnAgent(here, mine);
        world.SpawnAgent(away, theirs);
        // <b>Past one refresh interval, because the observation is staggered.</b> Watchers are re-marked one
        // slot per tick so the pass costs a flat fraction of itself, which means a body's ground is not known
        // on the tick it appears. The first version of this asserted after two ticks and failed on a faction
        // that had simply not been asked yet — a test racing an interval it did not know about.
        Tick(world, FactionKnowledge.RefreshInterval + 2);

        var seesItsOwnGround = world.Knowledge.Knows(mine, here);
        var theirsIsPrivate = !world.Knowledge.Knows(mine, away);
        var theySeeTheirs = world.Knowledge.Knows(theirs, away);

        // Walk the scout away and check the ground it left is remembered rather than forgotten.
        world.QueueMove(new[] { scout }, new Vector2(-70f, 30f));
        Tick(world, 240 * WalkingPace);
        var remembers = world.Knowledge.Knows(mine, here);
        var stale = world.Knowledge.LastSeen(mine, here) < world.TickNumber;

        var passed = seesItsOwnGround && theirsIsPrivate && theySeeTheirs && remembers && stale;
        if (!passed) Console.WriteLine(
            $"    knowledge: sees own={seesItsOwnGround}, theirs private={theirsIsPrivate}, " +
            $"they see theirs={theySeeTheirs}, remembers after leaving={remembers}, " +
            $"memory is stale={stale} (last seen {world.Knowledge.LastSeen(mine, here)} of tick " +
            $"{world.TickNumber}), known cells mine={world.Knowledge.KnownCells(mine)} " +
            $"theirs={world.Knowledge.KnownCells(theirs)}");
        return passed;
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

        var small = world.SpawnAgent(
            new Vector2(-10f, 0.75f), radius: AgentDefaults.Radius, allowEmbedded: true);
        var large = world.SpawnAgent(new Vector2(-10f, 0.75f), radius: 0.80f, allowEmbedded: true);
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
        // Deliberately inside the block: expelling an embedded body is the thing under test.
        var id = world.SpawnAgent(new Vector2(0.75f, 0.75f), allowEmbedded: true);
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
        var fault = DeterminismCheck.Diverges(first, second, 45);
        if (fault is not null)
        {
            Console.WriteLine($"    {fault}");
            return false;
        }

        // Kept alongside the fingerprint rather than folded into it. A NaN position is
        // perfectly deterministic — two runs will agree on it bit for bit — so the
        // fingerprint is exactly the wrong instrument for noticing one, and five hundred
        // bodies shoving each other is where one would come from.
        for (var i = 0; i < 500; i++)
        {
            ref var body = ref first.Agents.Get(new AgentId(i));
            if (!float.IsFinite(body.Position.X) || !float.IsFinite(body.Position.Y)) return false;
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

    /// <summary>
    /// A unit given a standing assignment works it with nobody watching: walks to one end,
    /// dwells, walks to the other, dwells, indefinitely.
    /// </summary>
    /// <remarks>
    /// The shuttle is hauling with the cargo left out, which is why it is the assignment worth
    /// having first: Session 6 replaces its two points with a granary and a farm and its dwell
    /// with a load, and everything about the loop is already proven.
    /// </remarks>
    private static bool StandingAssignmentWorksUnwatched()
    {
        var world = new SimulationWorld();
        var id = world.SpawnAgent(new Vector2(-6f, 0f));
        var near = new Vector2(-5f, 0f);
        var far = new Vector2(5f, 0f);
        world.QueueAssign(new[] { id }, Assignment.Shuttle(near, far, dwellSeconds: 1.0f));

        var visitedNear = false;
        var visitedFar = false;
        for (var tick = 0; tick < 30 * 60; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            ref var body = ref world.Agents.Get(id);
            visitedNear |= Vector2.Distance(body.Position, near) < 0.5f;
            visitedFar |= Vector2.Distance(body.Position, far) < 0.5f;
        }

        var worker = world.Agents.Get(id).Jobs;
        // Ten metres each way at 1.79 m/s plus a second of dwell is about 12 s a round trip,
        // so a minute is four or five legs. Asserting "more than two" rather than an exact
        // count: what is under test is that the loop continues on its own, not the pace.
        var passed = worker.LegsCompleted >= 2 && visitedNear && visitedFar &&
                     worker.Retries == 0;
        Console.WriteLine(
            $"    shuttle legs={worker.LegsCompleted} nearVisited={visitedNear} " +
            $"farVisited={visitedFar} retries={worker.Retries}");
        return passed;
    }

    /// <summary>
    /// Session 5's gate. A unit with a standing assignment survives an interrupt and resumes
    /// it.
    /// </summary>
    /// <remarks>
    /// The unit obeys the order, so taking control works. Its assignment and its count of legs finished are
    /// exactly what they were, so the order cost it nothing. It then STAYS where it was sent — this test
    /// asserted the opposite until §106, when the automatic return was removed: a body that reached the place
    /// it was ordered to and then wandered back to work on a two-second timer is a body that did not obey the
    /// order, and it was measured disintegrating cohorts within seconds of arrival. And the work resumes when
    /// it is handed back, which is what keeps the assignment a parked thing rather than a lost one.
    /// </remarks>
    /// <summary>
    /// The roster is the cohort's, the field on the body is a cache of it, and nobody leaves a cohort
    /// without a reason being booked.
    /// </summary>
    /// <remarks>
    /// <b>This is the check §105 did not have.</b> Membership lived in two places that meant different
    /// things — a roster fixed at the moment of the order, and a field on the body that was the live truth —
    /// and every read of the roster was filtered by the field to reconcile them. A group could therefore
    /// lose a member without anything happening: the filter matched one fewer body, and a departure became
    /// indistinguishable from a body that had never joined. That is how the jobs layer took half a cohort
    /// back two seconds after it arrived while every instrument in the arc reported success.
    /// <para>
    /// So two things are asserted. The two representations agree on every tick of a real move — which is
    /// what lets the cohort loops walk the roster directly instead of defending themselves with a skip. And
    /// the ledger accounts for every membership that ended, with the jobs layer's reclaim booked apart from
    /// anything the player asked for, because that is the only exit nobody asked for and the only one worth
    /// watching.
    /// </para></remarks>
    /// <summary>
    /// A crew is a set the player named: it survives the orders given to it, it forgets its dead, and it is
    /// not the cohort that carrying out an order produces.
    /// </summary>
    /// <remarks>
    /// <b>§82's third representation, asserted as the thing that distinguishes it.</b> A transient selection
    /// is rebuilt by every marquee and a cohort is dissolved by the end of its move; a crew is worth having
    /// only because neither of those happens to it. So the test does to a crew the three things that end the
    /// other two — an order, a second order, and losing members — and requires the set to still be there.
    /// <para>
    /// It also asserts the separation directly: while the crew is carrying out an order there is a cohort
    /// with the same members and a different identity, and when the cohort is gone the crew is not. Collapsing
    /// them is the failure §82 named, and it would pass every other test in this file.
    /// </para></remarks>
    /// <summary>
    /// A cohort outlives its move: it goes to rest with its people, the next order to the same set adopts it
    /// rather than building a new one, and only an empty roster ends it.
    /// </summary>
    /// <remarks>
    /// <b>The seam §107 named and left uncut.</b> Retiring on "everybody has settled" was a locomotion
    /// lifetime wearing the cohort's clothes: the set died the moment the walk did, so the next order could
    /// not possibly be given to the same group — there was no group left to give it to. Splitting the two
    /// makes adoption expressible, and adoption is the whole visible payoff, because the cohort keeps the
    /// identity and the travel state that station-keeping reads instead of starting every re-order from "we
    /// have not agreed on a direction yet".
    /// <para>
    /// The three claims are asserted apart because they fail apart. A cohort that survived but was not
    /// adopted would show as a second id; a cohort that was adopted but did not survive is a contradiction
    /// that would show as a departure count; and a cohort that survives everything would leak, so the empty
    /// roster still has to end one.
    /// </para>
    /// <para>
    /// Partial orders are the case that decides what "the player holds one cohort" means in code. Ordering
    /// four of the eight must not drag the other four along and must not hand the four a formation laid out
    /// for eight — so it forms its own cohort, the remainder keeps the old one, and the ordered set has
    /// resolved to exactly one cohort either way.
    /// </para></remarks>
    private static bool ACohortOutlivesItsMove()
    {
        var world = new SimulationWorld(60f);
        var ids = new List<AgentId>();
        for (var i = 0; i < 8; i++)
        {
            ids.Add(world.SpawnAgent(new Vector2(-22f + i % 4 * 1.2f, -14f + i / 4 * 1.2f)));
        }

        world.QueueMove(ids, new Vector2(-8f, -4f));
        Tick(world, 30 * 25);
        var first = world.MoveGroups.Values.SingleOrDefault();
        var restedWithItsPeople = first is { AtRest: true, Members.Count: 8 };
        var firstId = first?.Id ?? 0;

        // The same set again. Nobody joins and nobody leaves, so the ledger should not move at all.
        var beforeSecond = world.CohortDepartures;
        world.QueueMove(ids, new Vector2(10f, 6f));
        Tick(world, 1);
        var second = world.MoveGroups.Values.SingleOrDefault();
        var adopted = world.LastOrderAdoptedCohort && second is not null && second.Id == firstId &&
                      !second.AtRest && second.Members.Count == 8;
        var after = world.CohortDepartures;
        var quiet = after.Superseded == beforeSecond.Superseded &&
                    after.Overridden == beforeSecond.Overridden &&
                    after.Interrupted == beforeSecond.Interrupted &&
                    after.Died == beforeSecond.Died;

        // Half of them somewhere else: a different intention, so a different cohort — and the four left
        // behind keep the one they were in.
        var half = ids.Take(4).ToArray();
        world.QueueMove(half, new Vector2(-10f, 12f));
        Tick(world, 1);
        var split = world.MoveGroups.Count == 2 &&
                    !world.LastOrderAdoptedCohort &&
                    world.MoveGroups.Values.Any(g => g.Id == firstId && g.Members.Count == 4) &&
                    world.MoveGroups.Values.Any(g => g.Id != firstId && g.Members.Count == 4);
        // Every body is in exactly one cohort, which is the invariant the split has to preserve.
        var oneCohortEach = RosterDisagreements(world) == 0;

        // And the only thing that ends a set is having nobody left in it.
        world.QueueStop(ids);
        Tick(world, 2);
        var endedWhenEmpty = world.MoveGroups.Count == 0;

        var passed = restedWithItsPeople && adopted && quiet && split && oneCohortEach && endedWhenEmpty;
        Console.WriteLine(
            $"    rested with its people={restedWithItsPeople} | adopted by the next order={adopted} " +
            $"(id {firstId}, ledger unmoved={quiet}) | half ordered away split it in two={split} " +
            $"(one cohort each={oneCohortEach}) | ended only when empty={endedWhenEmpty}");
        return passed;
    }

    /// <summary>
    /// A cohort is never grown or merged: exactly its roster adopts it, and every other set — a subset, a
    /// superset, or a reach across two cohorts — is a new cohort.
    /// </summary>
    /// <remarks>
    /// <b>Decided from the chair rather than left open.</b> §107 shelved "a cohort cannot take a new member"
    /// as a question for a later pass; there is no later pass. Reinforcement is not a thing cohorts do. The
    /// rule is one line — <em>exactly the roster adopts, anything else is new</em> — and the reason to want
    /// it that plain is that every alternative needs an answer to "whose formation is this now": a slot laid
    /// out for six against particular ground is not a vacancy that a seventh body can be given, and a merge
    /// of two cohorts has two travel states and no principled way to pick one.
    /// <para>
    /// So this asserts the rule at all four of its corners, because three of them were emergent rather than
    /// written: the exact match was built deliberately in §109, and subset, superset and overlap all fell out
    /// of "no match means create one". Behaviour that is right by accident is behaviour that is one
    /// refactor from being wrong, and the corners are cheap to pin down.
    /// </para></remarks>
    private static bool ACohortIsNeverGrown()
    {
        var world = new SimulationWorld(60f);
        var ids = new List<AgentId>();
        for (var i = 0; i < 12; i++)
        {
            ids.Add(world.SpawnAgent(new Vector2(-22f + i % 4 * 1.3f, -14f + i / 4 * 1.3f)));
        }

        var six = ids.Take(6).ToArray();
        world.QueueMove(six, new Vector2(-6f, -2f));
        Tick(world, 1);
        var original = world.MoveGroups.Values.Single().Id;

        // A superset. The obvious "reinforcement" gesture — the same six plus two more — and it must not
        // extend the six; it is a different set, so it is a different cohort.
        world.QueueMove(ids.Take(8), new Vector2(6f, 2f));
        Tick(world, 2);
        var supersetIsNew = !world.LastOrderAdoptedCohort &&
                            world.MoveGroups.Count == 1 &&
                            world.MoveGroups.Values.Single().Id != original &&
                            world.MoveGroups.Values.Single().Members.Count == 8;
        var eight = world.MoveGroups.Values.Single().Id;

        // A subset keeps the parent alive with the rest in it, which §109 already required; asserted here
        // beside its neighbours because the four corners are one rule and read as one.
        world.QueueMove(ids.Take(3), new Vector2(-4f, 10f));
        Tick(world, 2);
        var subsetIsNew = !world.LastOrderAdoptedCohort &&
                          world.MoveGroups.Count == 2 &&
                          world.MoveGroups.Values.Any(g => g.Id == eight && g.Members.Count == 5) &&
                          world.MoveGroups.Values.Any(g => g.Id != eight && g.Members.Count == 3);

        // And a reach across both: three out of one cohort and three out of the other. Neither is grown,
        // neither is merged, and the six that were asked for are one cohort.
        var across = ids.Take(2).Concat(ids.Skip(3).Take(4)).OrderBy(id => id.Value).ToArray();
        world.QueueMove(across, new Vector2(12f, -10f));
        Tick(world, 2);
        var reachIsNew = !world.LastOrderAdoptedCohort &&
                         world.MoveGroups.Values.Any(g => g.Members.Count == 6 &&
                                                          g.Members.All(across.Contains));
        // Nobody is on two rosters and nobody claims a cohort that does not hold them, through all of it.
        var consistent = RosterDisagreements(world) == 0;
        // Every cohort alive holds at least one body: no husk is left behind by any of the three.
        var noHusks = world.MoveGroups.Values.All(g => g.Members.Count > 0);

        // The one case that does adopt, asserted last so the rule reads as a rule and not as a ban.
        world.QueueMove(across, new Vector2(-12f, -10f));
        Tick(world, 1);
        var exactAdopts = world.LastOrderAdoptedCohort;

        var passed = supersetIsNew && subsetIsNew && reachIsNew && consistent && noHusks && exactAdopts;
        Console.WriteLine(
            $"    superset is a new cohort={supersetIsNew} subset={subsetIsNew} " +
            $"across two={reachIsNew} | exactly the roster still adopts={exactAdopts} " +
            $"| one cohort each={consistent}, no empty husks={noHusks}, " +
            $"{world.MoveGroups.Count} cohort(s) alive");
        return passed;
    }

    private static bool ACrewSurvivesWhatIsDoneToIt()
    {
        var world = new SimulationWorld(60f);
        var crews = new ControlGroups();
        var everyone = new List<AgentId>();
        for (var i = 0; i < 12; i++)
        {
            everyone.Add(world.SpawnAgent(new Vector2(-20f + i % 4 * 1.2f, -12f + i / 4 * 1.2f)));
        }

        var chosen = everyone.Take(6).ToArray();
        crews.Assign(1, chosen);
        var named = crews.Members(1, world.Agents).Count;

        // An order, which is what ends a selection's usefulness and creates a cohort.
        world.QueueMove(crews.Members(1, world.Agents), new Vector2(14f, 9f));
        Tick(world, 30 * 4);
        var cohorts = world.MoveGroups.Values.Count(group => group.Members.Count > 0);
        var throughAnOrder = crews.Members(1, world.Agents).Count;
        // Same people, different identity: the cohort knows them by a roster it owns, the crew by a list the
        // simulation has never heard of.
        var separate = world.MoveGroups.Values.Any(group =>
            group.Members.Count == 6 && group.Members.All(chosen.Contains));

        // A second order, then an override, then long enough for the cohort to be gone entirely.
        world.QueueMove(crews.Members(1, world.Agents), new Vector2(-14f, 9f));
        Tick(world, 30 * 2);
        world.QueueStop(crews.Members(1, world.Agents));
        Tick(world, 30 * 2);
        var cohortsLeft = world.MoveGroups.Values.Count(group => group.Members.Count > 0);
        var throughTheLot = crews.Members(1, world.Agents).Count;

        // Losing members. Two of the six die, and the crew reports what it actually has without anybody
        // having told it — which is the property that makes a list of ids safe outside the simulation.
        world.DespawnAgents(chosen.Take(2));
        var afterLosses = crews.Members(1, world.Agents).Count;

        // And membership edits, which are the affordance rather than an accident.
        crews.Add(1, everyone.Skip(6).Take(3));
        var extended = crews.Members(1, world.Agents).Count;
        crews.Assign(1, everyone.Skip(9));
        var replaced = crews.Members(1, world.Agents).Count;

        var passed = named == 6 && cohorts == 1 && separate && throughAnOrder == 6 &&
                     cohortsLeft == 0 && throughTheLot == 6 && afterLosses == 4 &&
                     extended == 7 && replaced == 3;
        Console.WriteLine(
            $"    crew of {named}: {throughAnOrder} through an order (cohorts={cohorts}, " +
            $"same members in one={separate}), {throughTheLot} through two more and a stop " +
            $"(cohorts={cohortsLeft}), {afterLosses} after two died, {extended} extended, {replaced} replaced");
        return passed;
    }

    private static bool ACohortOwnsItsRoster()
    {
        // A wider world than the tuned one, for the single reason that the straggler has to still be
        // walking when the near eight have long since stopped: on thirty metres the whole diagonal is
        // sixteen seconds and the window closes before the cohort has settled into it.
        var world = new SimulationWorld(60f);
        var post = new Vector2(-25f, -16f);
        var target = new Vector2(12f, 8f);

        // <b>Eight bodies that will arrive at once and one that cannot.</b> Not decoration: after §106 the
        // jobs layer is locked out of a body for as long as it is under orders, so the only window in which
        // it can take a member out of a cohort is the one where that member has reached its slot and the
        // cohort has not retired because somebody else is still walking. That window is exactly the §105
        // mechanism — "the first bodies to reach their slots are reclaimed while the rest are still walking"
        // — so the test builds it rather than hoping to catch it.
        var ids = new List<AgentId>();
        for (var i = 0; i < 8; i++)
        {
            ids.Add(world.SpawnAgent(target + new Vector2(i % 4 * 1.1f - 1.6f, i / 4 * 1.1f - 0.5f)));
        }
        var straggler = world.SpawnAgent(post);
        ids.Add(straggler);

        world.QueueMove(ids, target);

        var disagreements = 0;
        var firstFaultTick = -1;
        for (var tick = 0; tick < 30 * 10; tick++)
        {
            Tick(world, 1);
            var faults = RosterDisagreements(world);
            if (faults > 0 && firstFaultTick < 0) firstFaultTick = tick;
            disagreements += faults;
        }

        // The near eight are standing on their slots; the straggler is still crossing the map, so the
        // cohort is alive and they are inside it with nothing to do. Work handed to them here is the jobs
        // layer taking a member out of a cohort, which is the one exit the player did not ask for.
        var beforeReclaim = world.CohortDepartures;
        world.QueueAssign(ids.Take(8), Assignment.Hold(post, dwellSeconds: 0.5f));
        Tick(world, 30);
        var reclaimed = world.CohortDepartures.Interrupted - beforeReclaim.Interrupted;
        disagreements += RosterDisagreements(world);

        // And the straggler by hand, which is the same departure with somebody's name on it.
        var beforeStop = world.CohortDepartures;
        world.QueueStop(new[] { straggler });
        Tick(world, 2);
        var stopped = world.CohortDepartures.Overridden - beforeStop.Overridden;
        disagreements += RosterDisagreements(world);

        var ledger = world.CohortDepartures;
        var accounted = ledger.Superseded + ledger.Overridden + ledger.Interrupted + ledger.Died;
        var stranded = 0;
        foreach (var group in world.MoveGroups.Values) stranded += group.Members.Count;

        var passed = disagreements == 0 && reclaimed == 8 && stopped == 1 && accounted == 9 && stranded == 0;
        Console.WriteLine(
            $"    roster/back-pointer disagreements={disagreements}" +
            (firstFaultTick >= 0 ? $" from tick {firstFaultTick}" : string.Empty) +
            $" | reclaimed by the jobs layer={reclaimed} overridden={stopped}" +
            $" | ledger superseded={ledger.Superseded} overridden={ledger.Overridden} " +
            $"interrupted={ledger.Interrupted} died={ledger.Died} " +
            $"= {accounted} of 9 | still on a roster={stranded}");
        return passed;
    }

    /// <summary>
    /// Counts every way the roster and the body's back-pointer can fail to say the same thing.
    /// </summary>
    /// <remarks>
    /// Three distinct faults, deliberately summed rather than short-circuited so a run reports how bad the
    /// disagreement is and not merely that there was one: a roster naming a body that does not agree it is
    /// a member, a body on two rosters at once, and a body claiming a cohort no roster puts it in.
    /// </remarks>
    private static int RosterDisagreements(SimulationWorld world)
    {
        var faults = 0;
        var onARoster = new Dictionary<int, int>();
        foreach (var group in world.MoveGroups.Values)
        foreach (var member in group.Members)
        {
            if (!world.Agents.Contains(member)) { faults++; continue; }
            if (world.Agents.Get(member).MoveGroupId != group.Id) faults++;
            if (!onARoster.TryAdd(member.Value, group.Id)) faults++;
        }

        foreach (ref readonly var agent in world.Agents.All)
        {
            if (!agent.IsAlive || agent.MoveGroupId == 0) continue;
            if (!onARoster.TryGetValue(agent.Id.Value, out var roster) || roster != agent.MoveGroupId)
            {
                faults++;
            }
        }
        return faults;
    }

    private static bool AssignmentSurvivesAnInterrupt()
    {
        var world = new SimulationWorld();
        var id = world.SpawnAgent(new Vector2(-6f, 0f));
        var near = new Vector2(-5f, 0f);
        var far = new Vector2(5f, 0f);
        var assignment = Assignment.Shuttle(near, far, dwellSeconds: 1.0f);
        world.QueueAssign(new[] { id }, assignment);
        Tick(world, 30 * 20);

        var legsBefore = world.Agents.Get(id).Jobs.LegsCompleted;
        if (legsBefore < 1)
        {
            Console.WriteLine($"    interrupt setup invalid: only {legsBefore} legs before the order");
            return false;
        }

        // Somewhere neither end of the shuttle would take it.
        var elsewhere = new Vector2(0f, 8f);
        world.QueueMove(new[] { id }, elsewhere);

        // Mid-walk, while the order is still being carried out. Snapshots by value rather than
        // by reference: a ref into the store keeps reading the live body, so a check written
        // against one reads whatever the unit is doing by the time it is printed, not what it
        // was doing when the check was made.
        Tick(world, 30 * 3);
        var dragged = world.Agents.Get(id).Jobs;
        var suspended = dragged.IsInterrupted &&
                        dragged.Assignment == assignment &&
                        dragged.LegsCompleted == legsBefore;

        Tick(world, 30 * 9);
        var arrived = world.Agents.Get(id);
        var obeyed = Vector2.Distance(arrived.Position, elsewhere) < 1.0f;

        // Long enough that the old two-second grace would have expired many times over. It must NOT resume:
        // an order holds until something overrides it, so the body stays where it was sent.
        Tick(world, 30 * 30);
        var waiting = world.Agents.Get(id).Jobs;
        var stayedPut = waiting.IsInterrupted &&
                        waiting.Assignment == assignment &&
                        waiting.LegsCompleted == legsBefore &&
                        Vector2.Distance(world.Agents.Get(id).Position, elsewhere) < 1.5f;

        // Given the work back explicitly, which is the override. The assignment was parked, not lost, so it
        // picks up where it left off.
        world.QueueAssign(new[] { id }, assignment);
        Tick(world, 30 * 30);
        var resumed = world.Agents.Get(id).Jobs;
        var backAtWork = !resumed.IsInterrupted &&
                         resumed.LegsCompleted > legsBefore &&
                         resumed.Assignment == assignment;

        var passed = obeyed && suspended && stayedPut && backAtWork;
        Console.WriteLine(
            $"    obeyed={obeyed} suspended={suspended} stayedPut={stayedPut} " +
            $"legs {legsBefore} -> {dragged.LegsCompleted} while walking -> " +
            $"{waiting.LegsCompleted} while posted -> {resumed.LegsCompleted} once given back");
        return passed;
    }

    /// <summary>
    /// Taking control is an interrupt, not a mode: a run of orders leaves one interrupt and
    /// the same assignment, and clearing the assignment is the only thing that stops the work.
    /// </summary>
    /// <remarks>
    /// <b>What "not a mode" means changed in §106, and the invariant worth keeping did not.</b> A unit does
    /// now stay where it was last sent — the automatic return on a two-second timer is gone, because it made a
    /// cohort disintegrate within seconds of arriving and it meant an order was not really obeyed. What must
    /// remain true is that an order is never a trap: the assignment is parked rather than lost, a run of
    /// orders leaves one interrupt rather than a pile of state, being handed work back always takes, and
    /// clearing the assignment stops the work for good. A mode you cannot get out of is the failure; a unit
    /// that waits where you put it is not.
    /// </remarks>
    private static bool AnOrderNeverBecomesAMode()
    {
        var world = new SimulationWorld();
        var id = world.SpawnAgent(new Vector2(-6f, 0f));
        var post = new Vector2(-5f, 3f);
        world.QueueAssign(new[] { id }, Assignment.Hold(post, dwellSeconds: 0.5f));
        Tick(world, 30 * 15);
        var held = world.Agents.Get(id).Jobs.LegsCompleted;

        // Three orders in a row, the way a player actually gives them.
        foreach (var target in new[] { new Vector2(2f, -2f), new Vector2(6f, 0f), new Vector2(4f, 4f) })
        {
            world.QueueMove(new[] { id }, target);
            Tick(world, 30 * 6);
        }

        var ordered = world.Agents.Get(id).Jobs;
        var stillOneInterrupt = ordered.Interrupt == InterruptKind.Order &&
                                ordered.Assignment.Kind == AssignmentKind.Hold;
        Tick(world, 30 * 25);

        // It stays at the last place it was sent rather than drifting back to its post.
        var posted = world.Agents.Get(id);
        var heldPosition = posted.Jobs.IsInterrupted &&
                           posted.Jobs.LegsCompleted == held &&
                           Vector2.Distance(posted.Position, new Vector2(4f, 4f)) < 2f;

        // Handed the work back: the override always takes, which is what stops an order being a trap.
        world.QueueAssign(new[] { id }, Assignment.Hold(post, dwellSeconds: 0.5f));
        Tick(world, 30 * 25);
        var returned = world.Agents.Get(id);
        var wentBack = !returned.Jobs.IsInterrupted &&
                       returned.Jobs.LegsCompleted > held &&
                       Vector2.Distance(returned.Position, post) <
                       JobDefaults.AtPlaceDistance(returned.Radius);

        // Now take it off work for real, which is the other command.
        world.QueueAssign(new[] { id }, Assignment.None);
        Tick(world, 2);
        var releasedAt = world.Agents.Get(id).Jobs.LegsCompleted;
        Tick(world, 30 * 20);
        var idle = world.Agents.Get(id).Jobs;
        var stoppedForGood = !idle.HasAssignment && !idle.IsInterrupted &&
                             idle.LegsCompleted == releasedAt &&
                             idle.Activity == ActivityKind.None;

        var passed = stillOneInterrupt && heldPosition && wentBack && stoppedForGood;
        Console.WriteLine(
            $"    orders left interrupt={ordered.Interrupt} heldPosition={heldPosition} " +
            $"returned={wentBack} legs {held}->{returned.Jobs.LegsCompleted} then cleared={stoppedForGood}");
        return passed;
    }

    /// <summary>
    /// How near a body has to be to count as at its place is written in bodies, so a wagon
    /// works a post rather than circling one it cannot quite reach.
    /// </summary>
    /// <remarks>
    /// Four bugs in Session 4 were a distance tuned against a 0.37 m body and then applied to
    /// a 0.90 m one. This is the same trap in a new layer: arrival tolerance is contested
    /// ground for a wide body, and a job that demanded the exact point would send it round
    /// again every time it correctly stopped short. Both bodies are given the same post and
    /// both have to settle into working it without accumulating attempts.
    /// </remarks>
    private static bool JobReachIsWrittenInBodies()
    {
        var report = new List<string>();
        var passed = true;
        foreach (var type in new[] { UnitType.Villager, UnitType.HaulerCart })
        {
            var world = new SimulationWorld();
            var id = world.SpawnAgent(new Vector2(-7f, -7f), type);
            var post = new Vector2(4f, 4f);
            world.QueueAssign(new[] { id }, Assignment.Hold(post, dwellSeconds: 2.0f));
            Tick(world, 30 * 45);

            var body = world.Agents.Get(id);
            var reach = JobDefaults.AtPlaceDistance(body.Radius);
            var working = body.Jobs.LegsCompleted >= 2 && body.Jobs.Retries == 0 &&
                          Vector2.Distance(body.Position, post) < reach;
            report.Add(
                $"{type.Name} reach={reach:F2}m legs={body.Jobs.LegsCompleted} " +
                $"retries={body.Jobs.Retries} residual={Vector2.Distance(body.Position, post):F2}m");
            passed &= working;
        }

        Console.WriteLine($"    {string.Join(" | ", report)}");
        return passed;
    }

    /// <summary>
    /// A job whose place cannot be reached has to fail politely: keep trying, at a bounded
    /// rate, without churning the router or losing the assignment.
    /// </summary>
    /// <remarks>
    /// The alternative, discovered the hard way in every system that has one, is a unit asking
    /// for a route thirty times a second forever. Session 6's gate is a full year with no unit
    /// permanently stalled, and this is the seed of it: the retry is priced in seconds like
    /// everything else, so a stranded unit costs one query every couple of seconds and stands
    /// there visibly waiting rather than visibly broken.
    /// </remarks>
    private static bool AnUnreachableJobFailsPolitely()
    {
        var world = new SimulationWorld();
        var id = world.SpawnAgent(new Vector2(-6f, 0f));
        // A block of built ground, with the job's place in the middle of it.
        var walled = new Vector2(4f, 4f);
        for (var x = -1; x <= 1; x++)
        for (var z = -1; z <= 1; z++)
        {
            world.QueueToggleObstacle(walled + new Vector2(x * 1.5f, z * 1.5f));
        }
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        world.QueueAssign(new[] { id }, Assignment.Hold(walled, dwellSeconds: 1.0f));

        var queriesBefore = world.PathQueries;
        Tick(world, 30 * 40);
        var queries = world.PathQueries - queriesBefore;

        var stranded = world.Agents.Get(id).Jobs;
        // Forty seconds at one attempt per RetrySeconds is about twenty tries, and each try is
        // a handful of queries rather than one. A per-tick spin would be well over a thousand.
        var bounded = queries < 400;
        var stillCommitted = stranded.Assignment.Kind == AssignmentKind.Hold &&
                             stranded.LegsCompleted == 0 &&
                             stranded.Retries > 0;
        var passed = bounded && stillCommitted;
        Console.WriteLine(
            $"    unreachable retries={stranded.Retries} queries={queries} in 40 s, " +
            $"committed={stillCommitted}");
        return passed;
    }

    /// <summary>
    /// Two runs of a world full of standing assignments, including an interrupt, agree tick
    /// for tick over everything the jobs layer carries.
    /// </summary>
    /// <remarks>
    /// The rule this satisfies is "the determinism test grows with each system", and the point
    /// worth recording is how little it took: the seventeen values the jobs layer added to a
    /// body were being compared before this test existed, because the fingerprint reads what
    /// the struct declares. What this adds is a scenario that actually exercises them — a
    /// fingerprint that covers a field nothing ever writes proves nothing about it.
    /// </remarks>
    private static bool JobsRunsIdenticallyTwice()
    {
        var fault = DeterminismCheck.Diverges(
            BuildJobsWorld(),
            BuildJobsWorld(),
            ticks: 30 * 25,
            fullEvery: 60,
            afterTick: null);
        if (fault is not null) Console.WriteLine($"    {fault}");
        return fault is null;
    }

    /// <summary>
    /// Twenty units on standing assignments over shared ground, half of them interrupted part
    /// way through — deliberately enough traffic that the shuttles queue against each other.
    /// </summary>
    private static SimulationWorld BuildJobsWorld()
    {
        var world = new SimulationWorld();
        var workers = new List<AgentId>();
        for (var i = 0; i < 20; i++)
        {
            var id = world.SpawnAgent(new Vector2(-8f + i % 5 * 0.9f, -6f + i / 5 * 0.9f));
            workers.Add(id);
            // Shared endpoints on purpose: a lane both ways is where the jobs layer and the
            // congestion field have to agree, and where two runs are most likely not to.
            world.QueueAssign(
                new[] { id },
                i % 2 == 0
                    ? Assignment.Shuttle(new Vector2(-4f, 2f), new Vector2(6f, 2f), 0.8f)
                    : Assignment.Hold(new Vector2(2f + i % 3, -3f), 1.4f));
        }

        // An order to half of them, queued at construction so both worlds issue it identically.
        world.QueueMove(workers.Where((_, index) => index % 2 == 1), new Vector2(0f, 7f));
        return world;
    }

    /// <summary>
    /// A workplace holds more hands than can stand on one square metre, so twelve units given
    /// the same post all end up working it rather than queueing for the exact spot.
    /// </summary>
    /// <remarks>
    /// Found by measurement rather than by design. The first run of the jobs trace had eight of
    /// forty-eight units permanently unable to get to work, and they were the ones who lost the
    /// race to a shared point: a place was a spot for one body, so everybody else spent the run
    /// walking at an occupied square. The fix is not a looser tolerance — that would make a job
    /// inside a wall look reachable — it is asking the world <em>why</em> the body stopped short.
    /// Taken ground is worked from wherever the crowd left room; ground nobody can stand on is
    /// waited out. Both halves are asserted here and in <c>AnUnreachableJobFailsPolitely</c>.
    /// </remarks>
    private static bool AWorkplaceHoldsMoreHandsThanFitOnIt()
    {
        var world = new SimulationWorld();
        var post = new Vector2(3f, 3f);
        var hands = new List<AgentId>();
        for (var i = 0; i < 12; i++)
        {
            hands.Add(world.SpawnAgent(new Vector2(-8f + i % 4 * 0.9f, -6f + i / 4 * 0.9f)));
        }

        world.QueueAssign(hands, Assignment.Hold(post, dwellSeconds: 2f));
        // Seconds, not economy-seconds: this is how long twelve bodies take to walk somewhere and stand
        // still, which is a fact about legs and does not move when the calendar does.
        Tick(world, 30 * 60);

        var idle = 0;
        var stranded = 0;
        var furthest = 0f;
        foreach (var id in hands)
        {
            var body = world.Agents.Get(id);
            if (body.Jobs.LegsCompleted == 0) idle++;
            if (body.Jobs.CannotReachWork) stranded++;
            furthest = MathF.Max(furthest, Vector2.Distance(body.Position, post));
        }

        // Everybody working, nobody reporting a job they cannot reach, and the whole shift
        // standing within the crowded reach of the post rather than strung out behind it.
        var reach = JobDefaults.CrowdedPlaceDistance(AgentDefaults.Radius);
        var passed = idle == 0 && stranded == 0 && furthest <= reach;
        Console.WriteLine(
            $"    shared post: {hands.Count - idle}/{hands.Count} working, {stranded} unable to " +
            $"reach, furthest {furthest:F2}m against a crowded reach of {reach:F2}m");
        return passed;
    }

    /// <summary>
    /// A fast body closes on a fleeing slow one at the difference of their speeds — and, measured
    /// here so it is on the record, cannot then touch it.
    /// </summary>
    /// <remarks>
    /// §7's raid is exactly this and nothing had tested it: chase and flee were only ever pointed at
    /// a body that could not move. The design rests on interception being <em>emergent</em> —
    /// "loaded raiders are slow, so interception is emergent rather than scripted" — and the
    /// raider's window is derived from a response time against an approach. Both are claims about
    /// closing at the difference of two speeds, which is arithmetic and can be settled before health
    /// or damage exist.
    /// <para>
    /// The negative case is what makes it worth asserting: a villager chasing light cavalry must
    /// <em>lose</em> ground, or interception is being granted by the chase behaviour rather than
    /// earned by the legs.
    /// </para>
    /// <para>
    /// What this also found, and deliberately does not assert, because it is Session 8's problem
    /// rather than a locomotion fault: <b>closing is not the same as catching, and the difference is
    /// enormous.</b> The rate is exact over open field, but the last metre is not delivered by it. An
    /// earlier form of this measurement waited for contact instead of measuring a rate, and light
    /// cavalry starting 14 m behind a villager — closing at 1.71 m/s, so eight seconds of
    /// arithmetic — first came within 1.34 m after <b>100 seconds</b>. It ends in a circling
    /// stalemate at about contact distance: pure pursuit aims at where the quarry is, the quarry
    /// turns, the pursuer overshoots, and both bodies are correctly avoiding each other the whole
    /// time. Closest approach over two minutes is 0.8 m against 0.74 m of combined radii, so the
    /// geometry is not the obstacle — the pursuit curve is.
    /// </para>
    /// <para>
    /// §7 says combat resolves by physical contact and never by abstract resolution, so Session 8
    /// cannot assume a chase delivers contact. It needs an attack activity that commits to it —
    /// lead the quarry rather than aim at it, and let a body and its declared target ignore each
    /// other in the velocity solve, exactly as a mover and a settled ally already do. That exclusion
    /// has to be symmetric: the one-directional version drove idle units metres down a corridor when
    /// it was tried for the crowd case.
    /// </para>
    /// </remarks>
    private static bool AFastBodyClosesOnASlowOne()
    {
        var report = new List<string>();
        var passed = true;
        foreach (var (chaser, quarry) in new[]
                 {
                     (UnitType.LightCavalry, UnitType.Villager),
                     (UnitType.LightCavalry, UnitType.HaulerCart),
                     (UnitType.Villager, UnitType.LightCavalry),
                 })
        {
            var closing = chaser.MaximumSpeed - quarry.MaximumSpeed;
            var pursuit = MeasurePursuit(chaser, quarry);
            var measured = (pursuit.Early - pursuit.Late) / PursuitWindowSeconds;

            // Half the arithmetic rate is the bar, not the rate itself. Pure pursuit against a body
            // that turns loses ground to cornering, and a fleeing body does not run in a straight
            // line. What must hold is the sign and the order of magnitude: a faster body gains, a
            // slower one loses, and it happens at something recognisably like the speed difference.
            var agrees = closing > 0f
                ? measured > closing * 0.5f
                : measured < closing * 0.5f;
            passed &= agrees;
            report.Add(
                $"{chaser.Name} after {quarry.Name}: {closing:+0.00;-0.00} m/s of legs, " +
                $"{measured:+0.00;-0.00} measured, closest {pursuit.Closest:F1} m");
        }

        Console.WriteLine($"    {string.Join(" | ", report)}");
        return passed;
    }

    /// <summary>Seconds between the two separation samples a closing rate is measured over.</summary>
    private const float PursuitWindowSeconds = 15f;

    private readonly record struct Pursuit(float Early, float Late, float Closest);

    /// <summary>
    /// Separations early and late in an open-field pursuit, and the closest the two ever came.
    /// </summary>
    /// <remarks>
    /// On a 200 m world rather than the tuned 30 m square, and started 40 m apart, both for the same
    /// reason: inside thirty metres the quarry is against a boundary within seconds and every number
    /// becomes about the corner rather than about the legs. Cornering is real and belongs in the
    /// game; it does not belong in a measurement of closing rate. Nothing here asserts a tuned
    /// threshold, so the larger world costs nothing.
    /// </remarks>
    private static Pursuit MeasurePursuit(UnitType chaser, UnitType quarry)
    {
        const float headStart = 40f;
        var world = new SimulationWorld(200f);
        var hunter = world.SpawnAgent(new Vector2(-headStart * 0.5f, 0f), chaser);
        var prey = world.SpawnAgent(new Vector2(headStart * 0.5f, 0f), quarry);
        world.QueueChase(new[] { hunter }, prey);
        world.QueueFlee(new[] { prey }, hunter);

        var early = 0f;
        var late = 0f;
        var closest = float.PositiveInfinity;
        const int settleTicks = 30 * 5;
        var windowTicks = settleTicks + (int)(30 * PursuitWindowSeconds);
        for (var tick = 1; tick <= 30 * 120; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var separation = Vector2.Distance(
                world.Agents.Get(hunter).Position, world.Agents.Get(prey).Position);
            closest = MathF.Min(closest, separation);
            if (tick == settleTicks) early = separation;
            if (tick == windowTicks) late = separation;
        }

        return new Pursuit(early, late, closest);
    }

    /// <summary>
    /// A world saved and loaded is the same world, and — the half that finds things — it has the
    /// same future.
    /// </summary>
    /// <remarks>
    /// §5 makes this core loop rather than a save feature: a career ends and the next one begins on
    /// the same map, which has not reset. Rule 3 has asked for the discipline every session since,
    /// and until now nothing serialized anything, so the discipline was never once exercised.
    /// <para>
    /// The acceptance test is the determinism fingerprint, in two parts, and the second part is what
    /// makes this worth having. Matching fingerprints at the instant of loading only says the bytes
    /// round-tripped. Ticking both worlds forward together says the loaded world <em>continues</em>
    /// as the same world, and that is a strictly stronger claim: a save can carry every value the
    /// fingerprint reads and still resume differently, because the fingerprint's boundary arguments
    /// are about detecting a difference, not about reproducing the future. Two things were left out
    /// of the first version of the save for exactly that reason and were caught here — the congestion
    /// field's running totals, which decide when the next revision publishes, and the path pool's
    /// free list, which decides which handle the next route takes.
    /// </para>
    /// <para>
    /// The world under test is deliberately mid-everything: bodies walking, a group order in transit,
    /// standing assignments, an interrupt, congestion on the ground, built obstacles, a despawned
    /// unit leaving a tombstone, and an order still sitting in the queue unapplied.
    /// </para>
    /// </remarks>
    private static bool ASavedWorldHasTheSameFuture()
    {
        var world = BuildSaveWorld();
        // Everything except the counters of work done: a loaded world resumes with a cold
        // flow-field cache and has to rebuild what the original had already built, which is a true
        // difference between the two processes and not between the two worlds.
        var before = DeterminismCheck.Fingerprint(world, DeterminismCheck.Scope.Full, includeWork: false);
        var loaded = WorldSave.RoundTrip(world);
        // And the saved world forgets its route caches, so the two are compared on equal terms. A cost
        // field is refined as things ask about it, so what it holds depends on the order the questions
        // came in — which is not state any save could write down. Skipping this shows up as one unit
        // in the last place of a body's facing, one tick later.
        world.DropRouteCaches();

        if (DeterminismCheck.Fingerprint(loaded, DeterminismCheck.Scope.Full, includeWork: false) != before)
        {
            Console.WriteLine(
                "    the load does not match the save: " +
                $"{DeterminismCheck.Explain(world, loaded, DeterminismCheck.Scope.Full, includeWork: false)}");
            return false;
        }

        // The part that matters. Two hundred ticks of two worlds that must agree every one of them.
        var fault = DeterminismCheck.Diverges(
            world, loaded, ticks: 200, fullEvery: 25, afterTick: null, includeWork: false);
        if (fault is not null)
        {
            Console.WriteLine($"    the loaded world does not continue as the same world: {fault}");
            return false;
        }

        Console.WriteLine(
            $"    {SaveSize(BuildSaveWorld()):N0} bytes for {loaded.Agents.Count} bodies on " +
            $"{loaded.ExtentMeters:F0} m, identical over 200 ticks");
        return true;
    }

    /// <summary>
    /// A save must refuse a file it cannot read rather than interpreting it.
    /// </summary>
    /// <remarks>
    /// Bodies are stored as raw bytes, so a save written when <c>AgentState</c> had a different shape
    /// would load as plausible garbage — units at coordinates read out of the middle of somebody's
    /// stall timer. That is the one failure worth spending header bytes to make impossible, and the
    /// signature it checks is derived from the determinism schema rather than hand-maintained, so it
    /// moves on its own when the struct does.
    /// </remarks>
    private static bool ABadSaveIsRefused()
    {
        var reasons = new List<string>();
        Refused("not a save at all", stream => stream.Write(new byte[64]));
        Refused("a truncated save", stream =>
        {
            using var full = new MemoryStream();
            WorldSave.Save(BuildSaveWorld(), full);
            stream.Write(full.ToArray().AsSpan(0, 48));
        });
        Refused("a save from another body layout", stream =>
        {
            using var full = new MemoryStream();
            WorldSave.Save(BuildSaveWorld(), full);
            var bytes = full.ToArray();
            // The layout signature sits after the magic and the version.
            bytes[12] ^= 0xFF;
            stream.Write(bytes);
        });

        var passed = reasons.Count == 0;
        if (!passed) Console.WriteLine($"    accepted {string.Join(", ", reasons)}");
        return passed;

        void Refused(string what, Action<MemoryStream> write)
        {
            using var stream = new MemoryStream();
            write(stream);
            stream.Position = 0;
            try
            {
                WorldSave.Load(stream);
                reasons.Add(what);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or EndOfStreamException)
            {
            }
        }
    }

    /// <summary>
    /// Every kind of order has a save format, checked against the command hierarchy itself.
    /// </summary>
    /// <remarks>
    /// An order sitting in the queue when a save is taken is state, and a command kind the save
    /// format has never heard of would be silently dropped — a unit that was told to do something and
    /// never does it, once, after a load, which is close to undebuggable. The same census trick the
    /// determinism ledger uses: reflect over the hierarchy and require every concrete kind to be
    /// accounted for, so the alarm fires in the session that adds one.
    /// </remarks>
    private static bool EveryOrderKindSurvivesASave()
    {
        var kinds = typeof(AgentCommand).Assembly
            .GetTypes()
            .Where(type => type.IsSubclassOf(typeof(AgentCommand)) && !type.IsAbstract)
            .Select(type => type.Name)
            .ToArray();
        var missing = kinds.Where(kind => !WorldSave.SavedCommandKinds.Contains(kind)).ToArray();
        if (missing.Length > 0)
        {
            Console.WriteLine(
                $"    {string.Join(", ", missing)} would be dropped by every save. Add a CommandTag " +
                "and a case in WorldSave.");
            return false;
        }

        // And the round trip, with one of every kind actually in the queue, because a tag that is
        // declared and written wrongly reads back as a different order.
        var world = new SimulationWorld();
        var first = world.SpawnAgent(new Vector2(-3f, 0f));
        var second = world.SpawnAgent(new Vector2(3f, 0f));
        var both = new[] { first, second };
        world.QueueMove(both, new Vector2(0f, 4f));
        world.QueueStop(both);
        world.QueueFollow(new[] { first }, second);
        world.QueuePatrol(new[] { first }, new Vector2(2f, 2f));
        world.QueueChase(new[] { first }, second);
        world.QueueFlee(new[] { second }, first);
        world.QueueToggleObstacle(new Vector2(5f, 5f));
        world.QueueAssign(both, Assignment.Shuttle(new Vector2(-2f, 1f), new Vector2(2f, 1f), 1.5f));

        var queued = world.PendingCommands.Count;
        var fault = DeterminismCheck.Diverges(
            world, WorldSave.RoundTrip(world), ticks: 60, fullEvery: 20, afterTick: null, includeWork: false);
        if (fault is not null) Console.WriteLine($"    with {queued} orders in flight: {fault}");
        Console.WriteLine($"    {kinds.Length} order kinds, {queued} of them round-tripped in flight");
        return fault is null;
    }

    /// <summary>
    /// A world in the middle of everything a save could be taken during.
    /// </summary>
    private static SimulationWorld BuildSaveWorld()
    {
        var world = new SimulationWorld();
        var walkers = new List<AgentId>();
        for (var i = 0; i < 12; i++)
        {
            walkers.Add(world.SpawnAgent(new Vector2(-9f + i % 4 * 0.9f, -7f + i / 4 * 0.9f)));
        }

        var workers = new List<AgentId>();
        for (var i = 0; i < 6; i++)
        {
            workers.Add(world.SpawnAgent(
                new Vector2(6f, -6f + i * 0.9f),
                i % 2 == 0 ? UnitType.Villager : UnitType.HaulerCart));
        }

        AddEconomy(world);

        // Built ground, so the placement grid, the block colliders and the raster all have something
        // in them, and a route has something to go around.
        for (var z = -2; z <= 2; z++) world.QueueToggleObstacle(new Vector2(0f, z * 1.5f));
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);

        world.QueueAssign(workers, Assignment.Shuttle(new Vector2(7f, 7f), new Vector2(7f, -7f), 2f));
        world.QueueMove(walkers, new Vector2(9f, 8f));
        Tick(world, 90);

        // A tombstone, an interrupt in flight, and a jam: three states a save could land in that a
        // freshly built world never sits in.
        world.DespawnAgents(new[] { walkers[3] });
        world.QueueMove(workers.Take(3), new Vector2(-8f, 8f));
        Tick(world, 40);

        // An order accepted and not yet applied, which is what makes the pending queue non-empty at
        // the instant of the save.
        world.QueueMove(walkers.Skip(6), new Vector2(-9f, -9f));
        return world;
    }

    /// <summary>Adds a working economy to the world a save is taken from.</summary>
    /// <remarks>
    /// So the round trip covers nodes, ledgers and a cart with grain on its back rather than only
    /// bodies. Cargo in transit is the interesting case: it is neither stored nor consumed, and a save
    /// that dropped it would balance the books by losing units.
    /// </remarks>
    private static void AddEconomy(SimulationWorld world)
    {
        var granary = world.AddNode(NodeKind.Granary, new Vector2(-4f, 4f), capacity: 200);
        world.SeedStock(granary, Resource.Grain, 25);
        world.AddNode(NodeKind.House, new Vector2(-1f, 3f), capacity: 0, occupancy: 24);
        var farmId = world.AddNode(NodeKind.Farm, new Vector2(8f, -6f), capacity: 60);
        var farm = world.Nodes.Get(farmId).Position;
        var farmExtent = world.Nodes.Get(farmId).FootprintRadius;
        // Mid-cycle, so the save has a field with a year of labour on it and a reaper carrying part of it.
        world.Nodes.Get(farmId).PrepareWork = CropCycle.PrepareLabour * 0.8f;
        world.Nodes.Get(farmId).MaintainWork = CropCycle.MaintainLabour * 0.5f;
        var hand = world.SpawnAgent(farm + new Vector2(farmExtent + 1.3f, 0f), UnitType.Villager);
        world.QueueAssign(
            new[] { hand },
            Assignment.Work(
                farmId, farm, farmExtent, Resource.Grain,
                EconomySystem.WorkShiftSeconds, EconomySystem.HandoverSeconds));
        world.SpawnAgent(new Vector2(2f, 0f), UnitType.HaulerCart);
    }

    private static long SaveSize(SimulationWorld world)
    {
        using var stream = new MemoryStream();
        WorldSave.Save(world, stream);
        return stream.Length;
    }

    /// <summary>
    /// The calendar is a knob: turn the year and the day count, and every identity downstream still holds.
    /// </summary>
    /// <remarks>
    /// <b>This is the guard the time work needed and did not have.</b> Every other test in this file runs at
    /// one setting of the calendar, so a constant that had quietly captured a consequence of that setting
    /// looked exactly like a constant that had not. §112 turned the year up by three and found out the hard
    /// way: the foraging reaches tripled because a walking budget was written as a share of the year, the
    /// ration collapsed because it was derived from the day count, the crop windows became errands because
    /// they were the seconds a share came to, and nine tests failed for a tenth reason — durations chosen
    /// against a year that had moved.
    /// <para>
    /// Not one of those was caught by a test. They were caught by reading, one at a time, which is the
    /// method that works right up until the session somebody is in a hurry. So this turns the dials and
    /// asserts what must not move, what must move exactly with them, and what must stay in proportion —
    /// which is the whole factorisation, stated as arithmetic.
    /// </para>
    /// <para>
    /// The three claims, and why each one is separate. <b>Physical things must not move at all</b>: a
    /// cutter's reach is metres over a map, walked by a body at its own speed, and a longer year buys it
    /// nothing. <b>Derived things must move exactly</b>: the day divides the year, the seasons are shares of
    /// it. <b>Balanced things must stay in proportion</b>: a year's grain and a year's wood are what the
    /// economy is tuned in, so they are the same numbers however long a year takes to happen.
    /// </para></remarks>
    private static bool TheCalendarIsAKnob()
    {
        var year = WorldCalendar.YearSeconds;
        var days = WorldCalendar.DaysPerYear;
        var report = new List<string>();
        var held = true;
        try
        {
            // Physical facts, measured before anything is turned. Nothing below may move them.
            var cutterReach = Woodland.ReachMetres;
            var quarrierReach = Quarrying.ReachMetres;
            var ration = EconomyRates.GrainPerVillagerPerYear;

            foreach (var (scale, count) in new[]
                     { (1f, 270), (0.5f, 270), (3f, 270), (10f, 270), (1f, 365), (3f, 60) })
            {
                WorldCalendar.YearSeconds = year * scale;
                WorldCalendar.DaysPerYear = count;
                var now = WorldCalendar.YearSeconds;
                var ok = true;

                // Derived: the day divides the year by the count, and the seasons are shares that sum to it.
                ok &= MathF.Abs(WorldCalendar.DaySeconds * count - now) < now * 1e-4f;
                var seasons = 0f;
                foreach (var season in Enum.GetValues<Season>()) seasons += WorldCalendar.LengthOf(season);
                ok &= MathF.Abs(seasons - now) < now * 1e-4f;

                // Derived: a season's boundaries land where its shares say, whatever the year is.
                var elapsed = 0f;
                foreach (var season in Enum.GetValues<Season>())
                {
                    ok &= WorldCalendar.At(elapsed + 1f).Season == season;
                    elapsed += WorldCalendar.LengthOf(season);
                    ok &= WorldCalendar.At(elapsed - 1f).Season == season;
                }

                // Balanced: a year still draws and still yields the annual figures it is tuned in. Sampled
                // in proportion to the year rather than per second, so a ten-times year is not a ten-times
                // integration.
                foreach (var resource in new[] { Resource.Grain, Resource.Wood })
                {
                    var drawn = 0f;
                    const int samples = 4000;
                    var step = now / samples;
                    for (var i = 0; i < samples; i++)
                    {
                        drawn += EconomyRates.DrawPerSecond(
                            resource, WorldCalendar.At((i + 0.5f) * step).Season, appetite: 1f) * step;
                    }
                    var nominal = resource == Resource.Grain
                        ? EconomyRates.GrainPerVillagerPerYear
                        : EconomyRates.WoodPerVillagerPerYear;
                    ok &= MathF.Abs(drawn - nominal) < nominal * 0.01f;
                }

                var cut = Woodland.CutPerSecond * now * (1f - Woodland.CutterWalkShare);
                ok &= MathF.Abs(cut - EconomyRates.WoodPerHandPerYear) <
                      EconomyRates.WoodPerHandPerYear * 0.002f;

                // In proportion: a window is the share of its season it is meant to be, and a house is the
                // count of springs it is meant to be. These are the two that became literals and drifted.
                ok &= MathF.Abs(CropCycle.PrepareLabour / WorldCalendar.LengthOf(Season.Spring) - 0.75f) < 1e-4f;
                ok &= MathF.Abs(CropCycle.ReapLabour / WorldCalendar.LengthOf(Season.Harvest) - 0.8f) < 1e-4f;
                ok &= MathF.Abs(
                    Construction.LabourFor(NodeKind.House) /
                    WorldCalendar.LengthOf(Season.Spring) - 1f) < 1e-4f;

                // Physical and balance anchors: untouched by anything the calendar does.
                ok &= MathF.Abs(Woodland.ReachMetres - cutterReach) < 0.01f;
                ok &= MathF.Abs(Quarrying.ReachMetres - quarrierReach) < 0.01f;
                ok &= EconomyRates.GrainPerVillagerPerYear == ration;

                held &= ok;
                // <b>Simulated hours, said so.</b> Wall time is sim time over the compression, which this
                // layer does not know and must not guess at — and a report about clocks that quietly hands
                // you one unit while naming another is the exact fault this test exists to catch.
                report.Add(
                    $"{now / 3600f:F2} sim-h x{count} {(ok ? "ok" : "BROKE")} " +
                    $"(day {WorldCalendar.DaySeconds:F1} s, reach {Woodland.ReachMetres:F0}/" +
                    $"{Quarrying.ReachMetres:F0} m)");
            }
        }
        finally
        {
            WorldCalendar.YearSeconds = year;
            WorldCalendar.DaysPerYear = days;
        }

        Console.WriteLine($"    {string.Join(" | ", report)}");
        return held;
    }

    /// <summary>
    /// The year adds up, and moving when a resource arrives cannot change how much of it arrives.
    /// </summary>
    /// <remarks>
    /// The seasons are canonical and every rate is a function of them, which is only safe if the
    /// function is normalised — so this integrates each seasonal shape over a whole year and requires
    /// the answer to be the annual amount it was given. Without that, moving the harvest spike or
    /// lengthening a season is a balance change disguised as a flavour change, and the prototype's
    /// three disagreeing crop windows are what that looks like after a while.
    /// </remarks>
    private static bool TheYearAddsUp()
    {
        var seasons = 0f;
        foreach (var season in Enum.GetValues<Season>()) seasons += WorldCalendar.LengthOf(season);
        // <b>Each boundary from the lengths, not from the seconds they happened to fall at.</b> These were
        // 1199 / 1201 / 3001 / 4001 — §3's year written out — and a test that hardcodes where spring ends is
        // a test that fails the moment the calendar is retimed, which is exactly what it did in §112. What
        // the check is for is that a second either side of a boundary lands in the right season, and that
        // claim can be made without knowing where the boundary is.
        var boundaries = WorldCalendar.At(0f).Season == Season.Spring &&
                         WorldCalendar.At(WorldCalendar.YearSeconds + 1f) is
                             { Year: 1, Season: Season.Spring };
        var elapsed = 0f;
        foreach (var season in Enum.GetValues<Season>())
        {
            boundaries &= WorldCalendar.At(elapsed + 1f).Season == season;
            elapsed += WorldCalendar.LengthOf(season);
            boundaries &= WorldCalendar.At(elapsed - 1f).Season == season;
        }

        // Integrate each shape over the year a second at a time and compare with the annual figure.
        var report = new List<string>();
        var normalised = true;
        foreach (var resource in Resources.All)
        {
            var drawn = 0f;
            for (var second = 0f; second < WorldCalendar.YearSeconds; second += 1f)
            {
                drawn += EconomyRates.DrawPerSecond(
                    resource, WorldCalendar.At(second).Season, appetite: 1f);
            }

            var nominalDrawn = resource switch
            {
                Resource.Grain => EconomyRates.GrainPerVillagerPerYear,
                Resource.Wood => EconomyRates.WoodPerVillagerPerYear,
                _ => 0f,
            };
            // <b>A relative tolerance cannot check a nominal of zero, and stone's nominal is legitimately
            // zero.</b> `|drawn - 0| < 0 * 0.002` is false however right the answer is, so the first run with a
            // third resource failed this test while printing "Stone drawn 0/0" in its own evidence line. The
            // rule the test wants is "nothing draws stone", which is an exact claim rather than an approximate
            // one — so a resource nobody has written a draw for is checked exactly, and the rest by proportion.
            normalised &= nominalDrawn <= 0f
                ? drawn == 0f
                : MathF.Abs(drawn - nominalDrawn) < nominalDrawn * 0.002f;
            report.Add($"{resource} drawn {drawn:F0}/{nominalDrawn:F0}");
        }

        // Neither resource is a rate any more, so what is checked on the production side is that the
        // derivations still land on their annual figures. A cutter cuts for the share of the year it is
        // not walking, and the reach is the distance that share of walking buys.
        var cut = Woodland.CutPerSecond * WorldCalendar.YearSeconds *
                  (1f - Woodland.CutterWalkShare);
        normalised &= MathF.Abs(cut - EconomyRates.WoodPerHandPerYear) <
                      EconomyRates.WoodPerHandPerYear * 0.002f;
        // And the walking it implies really is that share of the year: the reach has to be the distance a
        // year's round trips fit into, or the two numbers are describing different woodcutters.
        var trips = EconomyRates.WoodPerHandPerYear / UnitType.Villager.CarryCapacity;
        var walking = trips * 2f * Woodland.ReachMetres / UnitType.Villager.MaximumSpeed;
        normalised &= MathF.Abs(walking - Woodland.CutterWalkShare * WorldCalendar.YearSeconds) <
                      WorldCalendar.YearSeconds * 0.002f;
        report.Add(
            $"wood cut {cut:F0}/{EconomyRates.WoodPerHandPerYear:F0} in " +
            $"{100f - Woodland.CutterWalkShare * 100f:F0}% of a year, reach {Woodland.ReachMetres:F0} m " +
            $"= {walking / WorldCalendar.YearSeconds * 100f:F0}% walking");

        // Each window has to be answerable inside its own season, or the phase is a deadline nobody can
        // meet. Reaping deliberately does not fit for one pair of hands — that is the scramble — so it is
        // checked against the window rather than against one worker.
        var fits = CropCycle.PrepareLabour < WorldCalendar.LengthOf(Season.Spring) &&
                   CropCycle.MaintainLabour < WorldCalendar.LengthOf(Season.Summer) &&
                   CropCycle.ReapLabour < WorldCalendar.LengthOf(Season.Harvest) &&
                   CropCycle.PhaseOf(Season.Spring) == CropPhase.Prepare &&
                   CropCycle.PhaseOf(Season.Summer) == CropPhase.Maintain &&
                   CropCycle.PhaseOf(Season.Harvest) == CropPhase.Reap &&
                   CropCycle.PhaseOf(Season.Winter) == CropPhase.Rest;
        normalised &= fits;
        report.Add(
            $"windows {CropCycle.PrepareLabour:F0}/{WorldCalendar.LengthOf(Season.Spring):F0} " +
            $"{CropCycle.MaintainLabour:F0}/{WorldCalendar.LengthOf(Season.Summer):F0} " +
            $"{CropCycle.ReapLabour:F0}/{WorldCalendar.LengthOf(Season.Harvest):F0}");

        var passed = MathF.Abs(seasons - WorldCalendar.YearSeconds) < 0.001f && boundaries && normalised;
        Console.WriteLine(
            $"    year {seasons:F0} s over {WorldCalendar.DaysPerYear} days | {string.Join(" | ", report)}");
        return passed;
    }
    /// <summary>The year the economy tests' durations were originally chosen against. See EconomySeconds.</summary>
    /// <remarks>
    /// §3's year, 5,400 sim seconds. Kept as a named constant rather than folded away because it is what
    /// every literal duration below actually means: these tests were written as "long enough for a field to
    /// be reaped", "enough of a harvest to see grain move", "a season and a bit", and each was then written
    /// down as the seconds that came to at the time.
    /// </remarks>
    private const float TunedYearSeconds = 5400f;

    /// <summary>
    /// A duration chosen against §3's year, in this one.
    /// </summary>
    /// <remarks>
    /// <b>Converted rather than restated, because the intent was always a share of the calendar.</b> §112
    /// tripled the year, and a test that ticks a fixed number of seconds through an economy whose rates all
    /// divide by the year is not measuring what it was written to measure — it is measuring a third of it.
    /// Rewriting fourteen literals by hand would have been fourteen chances to pick a number that looked
    /// right; one conversion is one decision, and it is the same decision the crop windows and construction
    /// costs made when they became shares of their season.
    /// </remarks>
    private static float EconomySeconds(float atTunedYear) =>
        atTunedYear * (WorldCalendar.YearSeconds / TunedYearSeconds);

    /// <summary>Ticks for a duration chosen against §3's year.</summary>
    private static int EconomyTicks(float atTunedYear) => (int)(30f * EconomySeconds(atTunedYear));

    /// <summary>
    /// A settlement produces, hauls, stores and consumes, and not one unit goes missing.
    /// </summary>
    /// <remarks>
    /// The small, fast version of Session 6's gate — <c>--settlement</c> is the full year. What it
    /// checks that a longer run cannot check any better is the exact one: stock is whole units, so
    /// <c>seeded + produced − consumed</c> must equal what is stored plus what is being carried, with no
    /// tolerance anywhere. Every tick.
    /// </remarks>
    private static bool ASettlementFeedsItself()
    {
        var world = new SimulationWorld();
        // Start in the harvest, because spring brings in no grain at all by design and a test that
        // began there would be measuring how well the settlement waits.
        world.StartAtSeconds(EconomySeconds(3100f));
        var granary = world.AddNode(NodeKind.Granary, Vector2.Zero, capacity: 400);
        world.SeedStock(granary, Resource.Grain, 40);
        // A house, because houses are the only things that eat. Inside the granary's catchment, so it
        // is actually supplied — a household outside every catchment goes hungry however full the
        // stores are, which is a different test.
        world.AddNode(NodeKind.House, new Vector2(3f, -3f), capacity: 0, occupancy: 8);

        for (var i = 0; i < 3; i++)
        {
            var farm = world.AddNode(NodeKind.Farm, new Vector2(-8f + i * 8f, 8f), capacity: 60);
            // The node's position after it is placed, not the one it was asked for: a building snaps to
            // its placement cell and can move by half a cell diagonal, and a hand spawned relative to
            // the original point ends up standing inside its own farm's wall.
            var placed = world.Nodes.Get(farm).Position;
            var extent = world.Nodes.Get(farm).FootprintRadius;
            // Prepared and tended already, because this test is about a settlement feeding itself in the
            // window where food arrives, not about waiting two seasons for spring to finish.
            world.Nodes.Get(farm).PrepareWork = CropCycle.PrepareLabour;
            world.Nodes.Get(farm).MaintainWork = CropCycle.MaintainLabour;
            world.Nodes.Get(farm).CycleYear = world.Date.Year;
            var hand = world.SpawnAgent(placed + new Vector2(extent + 1.3f, 0f), UnitType.Villager);
            world.QueueAssign(
                new[] { hand },
                Assignment.Work(
                    farm, placed, extent, Resource.Grain,
                    EconomySystem.WorkShiftSeconds, EconomySystem.HandoverSeconds));
        }

        for (var i = 0; i < 2; i++)
        {
            world.SpawnAgent(new Vector2(-2f + i * 4f, -3f), UnitType.HaulerCart);
        }

        var drift = default(ResourceTotals);
        var stalled = 0;
        for (var tick = 0; tick < EconomyTicks(240f); tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero) break;
        }

        foreach (ref readonly var agent in world.Agents.All)
        {
            if (!agent.IsAlive || !agent.Jobs.CannotReachWork) continue;
            stalled++;
            // Printed because a stall is silent otherwise: the interesting numbers are the distance
            // against the tolerance, which is how the missing footprint on a post was found.
            Console.WriteLine(
                $"      stalled {agent.Id} r={agent.Radius:F2} at ({agent.Position.X:F1},{agent.Position.Y:F1}) " +
                $"place=({agent.Jobs.Place.X:F1},{agent.Jobs.Place.Y:F1}) extent={agent.Jobs.PlaceExtent:F2} " +
                $"kind={agent.Jobs.Assignment.Kind} retries={agent.Jobs.Retries} " +
                $"dist={Vector2.Distance(agent.Position, agent.Jobs.Place):F2} " +
                $"tolerance={JobDefaults.AtPlaceDistance(agent.Radius, agent.Jobs.PlaceExtent):F2}");
        }

        // Every field worked, rather than every field manned at the sampling instant: a reaper spends much
        // of its time walking its crop in, so counting hands at one moment counts whoever happens to be
        // home. What has to be true is that all three fields were reaped and the food arrived.
        var worked = 0;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (node.IsAlive && node.Kind == NodeKind.Farm && node.ReapWork > 0f) worked++;
        }

        var stored = world.Nodes.Get(granary).Stock.Grain;
        // And no hauling at all, which is the point rather than an omission: the producer carries its own
        // crop, the granary is next door, and a settlement this compact has nothing for a cart to do.
        var passed = drift.IsZero && stalled == 0 && worked == 3 &&
                     world.Economy.Produced.Grain > 0 && world.Economy.Consumed.Grain > 0 &&
                     world.Economy.HaulsAssigned == 0 && stored > 0;
        Console.WriteLine(
            $"    settlement: {worked}/3 fields reaped, produced {world.Economy.Produced.Grain}, ate " +
            $"{world.Economy.Consumed.Grain}, {stored} in the granary, {world.Economy.HaulsAssigned} " +
            $"hauls needed, drift {drift.Grain}/{drift.Wood}, stalled {stalled}");
        return passed;
    }

    /// <summary>
    /// Two runs of a working settlement agree tick for tick, over everything the economy carries.
    /// </summary>
    /// <remarks>
    /// The fingerprint reads nodes, ledgers and cargo without having been told to — a node is plain
    /// data, so it goes through the same walker a pending order does. What this adds is a scenario in
    /// which any of that actually changes, because a fingerprint over a field nothing writes proves
    /// nothing about it. Hauling is the interesting part: the board prices every idle cart against every
    /// task and picks a winner, so a tie broken differently would show up here and nowhere else.
    /// </remarks>
    private static bool TheEconomyRunsIdenticallyTwice()
    {
        var fault = DeterminismCheck.Diverges(BuildEconomyWorld(), BuildEconomyWorld(), ticks: 30 * 90);
        if (fault is not null) Console.WriteLine($"    {fault}");
        return fault is null;
    }

    private static SimulationWorld BuildEconomyWorld()
    {
        var world = new SimulationWorld();
        var granary = world.AddNode(NodeKind.Granary, new Vector2(0f, -4f), capacity: 300);
        world.SeedStock(granary, Resource.Grain, 30);
        world.SeedStock(granary, Resource.Wood, 30);
        world.AddNode(NodeKind.House, new Vector2(3f, -4f), capacity: 0, occupancy: 8);

        // Fields and trees sharing one granary, so the board has ties to break and two kinds of cargo to
        // price.
        for (var i = 0; i < 4; i++)
        {
            var at = new Vector2(-9f + i * 6f, 8f);
            var farm = i % 2 == 0;
            world.AddNode(
                farm ? NodeKind.Farm : NodeKind.Tree,
                at,
                capacity: farm ? 80 : (int)Woodland.WoodPerTree,
                farm ? Resource.Grain : Resource.Wood);
            var site = world.Nodes.All[^1].Id;
            var placed = world.Nodes.Get(site).Position;
            var extent = world.Nodes.Get(site).FootprintRadius;
            if (!farm) world.SeedStock(site, Resource.Wood, (int)Woodland.WoodPerTree);
            var hand = world.SpawnAgent(placed + new Vector2(extent + 1.3f, 0f), UnitType.Villager);
            world.QueueAssign(
                new[] { hand },
                farm
                    ? Assignment.Work(
                        site, placed, extent, Resource.Grain,
                        EconomySystem.WorkShiftSeconds, EconomySystem.HandoverSeconds)
                    : Assignment.Work(
                        site, placed, extent, Resource.Wood,
                        Woodland.LoadSeconds(UnitType.Villager.CarryCapacity),
                        EconomySystem.HandoverSeconds));
        }

        for (var i = 0; i < 3; i++)
        {
            world.SpawnAgent(new Vector2(-3f + i * 3f, -8f), UnitType.HaulerCart);
        }

        return world;
    }

    /// <summary>
    /// A carrier destroyed leaves its load on the ground, and somebody else comes for it.
    /// </summary>
    /// <remarks>
    /// A resource is a physical thing. It does not stop existing because whoever was carrying it did, and
    /// this was got wrong first time round: a body dying with cargo was booked to a "lost" ledger, which
    /// quietly made killing a loaded raider the most effective way of destroying grain. §7 wants the
    /// opposite — <em>"the return trip is the defender's window"</em> only means anything if intercepting
    /// a raider <b>returns</b> the loot rather than denying it, and that requires the loot to be lying
    /// there afterwards.
    /// <para>
    /// So this is §7's interception in miniature, with the combat left out because none exists yet: load
    /// a cart, destroy it mid-journey, and require the grain to be on the ground, to be collected by
    /// another cart, to reach the granary, and for not one unit to have gone missing at any point. The
    /// conservation identity got <em>shorter</em> when this was fixed, which is usually the sign that a
    /// design correction was the right one.
    /// </para>
    /// </remarks>
    private static bool DroppedCargoStaysInTheWorld()
    {
        var world = new SimulationWorld();
        world.StartAtSeconds(EconomySeconds(3100f));
        var granary = world.AddNode(NodeKind.Granary, new Vector2(-9f, 0f), capacity: 400);
        var farm = new Vector2(9f, 0f);
        world.AddNode(NodeKind.Farm, farm, capacity: 200);
        world.SeedStock(world.Nodes.All[1].Id, Resource.Grain, 60);

        // Two carters: one to be destroyed carrying, one to come back for what it dropped. Built rather
        // than spawned, because there is no hauler unit — hauling is a job a villager takes and the
        // settlement pays for in timber, so the granary has to have some.
        world.SeedStock(granary, Resource.Wood, SimulationWorld.CartTimber * 2);
        var doomed = world.SpawnAgent(new Vector2(7f, 0f), UnitType.Villager);
        var spare = world.SpawnAgent(new Vector2(-7f, 0f), UnitType.Villager);
        var built = world.TryBuildCart(doomed) && world.TryBuildCart(spare);
        if (!built)
        {
            Console.WriteLine("    no cart could be built, so nothing can be hauled");
            return false;
        }

        // Let the board load the first cart and get it out on the road.
        var carried = 0;
        for (var tick = 0; tick < EconomyTicks(60f) && carried == 0; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            if (world.Agents.Contains(doomed)) carried = world.Agents.Get(doomed).Jobs.CarriedUnits;
        }

        if (carried == 0)
        {
            Console.WriteLine("    nothing was ever loaded, so there is nothing to drop");
            return false;
        }

        var beforeDrop = world.Economy.Discrepancy(world.Nodes, world.Agents);
        var where = world.Agents.Get(doomed).Position;
        world.DespawnAgents(new[] { doomed });

        // On the ground, where it fell, belonging to nobody.
        var pile = NodeId.None;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (node.IsAlive && node.IsPile) pile = node.Id;
        }

        var dropped = pile.IsValid ? world.Nodes.Get(pile).Stock.Grain : 0;
        var nearby = pile.IsValid && Vector2.Distance(world.Nodes.Get(pile).Position, where) < 0.01f;
        var afterDrop = world.Economy.Discrepancy(world.Nodes, world.Agents);

        // And the survivor fetches it. Two minutes is generous for twenty metres.
        var recovered = false;
        var worstDrift = 0L;
        for (var tick = 0; tick < EconomyTicks(150f) && !recovered; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            worstDrift = Math.Max(worstDrift, Math.Abs(drift.Grain));
            recovered = !world.Nodes.Contains(pile) && world.Nodes.Get(granary).Stock.Grain >= dropped;
        }

        var passed = dropped == carried && nearby && recovered && worstDrift == 0 &&
                     beforeDrop.Grain == 0 && afterDrop.Grain == 0;
        Console.WriteLine(
            $"    {carried} grain carried, {dropped} on the ground where it fell (matched position: " +
            $"{nearby}), recovered: {recovered}, worst drift: {worstDrift}");
        return passed;
    }

    /// <summary>
    /// A field's year is three windows of labour, and missing one costs what a later one cannot give back.
    /// </summary>
    /// <remarks>
    /// The arithmetic first, because it is exact and because every claim the mechanic makes is in it: the
    /// ceiling is set by breaking ground and nothing raises it afterwards; tending only retains; and what
    /// is reaped is what is had. Then the same thing through a real settlement, which is where a field that
    /// was never broken has to actually yield nothing rather than merely score zero.
    /// </remarks>
    private static bool ACropIsThreeWindowsOfLabour()
    {
        var field = new EconomyNode { Kind = NodeKind.Farm };
        var report = new List<string>();
        var passed = true;

        // Never broken: no ceiling, and nothing later matters.
        field.MaintainWork = CropCycle.MaintainLabour;
        passed &= CropCycle.PotentialOf(in field) == 0f;
        passed &= CropCycle.ReapTargetOf(in field) == 0f;
        passed &= CropCycle.StateOf(in field, Season.Harvest) == "failed";
        report.Add($"unbroken: potential {CropCycle.PotentialOf(in field):F2}, says " +
                   $"'{CropCycle.StateOf(in field, Season.Harvest)}'");

        // Broken and tended: all of it.
        field.PrepareWork = CropCycle.PrepareLabour;
        var full = CropCycle.PotentialOf(in field);
        passed &= MathF.Abs(full - 1f) < 0.001f;

        // Broken and neglected: the ceiling stands, a quarter of it does not.
        field.MaintainWork = 0f;
        var neglected = CropCycle.PotentialOf(in field);
        passed &= MathF.Abs(neglected - CropCycle.NeglectedRetention) < 0.001f;
        passed &= CropCycle.StateOf(in field, Season.Summer) == "neglected";

        // Half broken and fully tended: half a crop. Tending cannot raise a ceiling.
        field.PrepareWork = CropCycle.PrepareLabour * 0.5f;
        field.MaintainWork = CropCycle.MaintainLabour;
        var half = CropCycle.PotentialOf(in field);
        passed &= MathF.Abs(half - 0.5f) < 0.001f;
        report.Add($"full {full:F2}, neglected {neglected:F2}, half-broken {half:F2}");

        // Each window belongs to its season, and only its season asks for work.
        passed &= CropCycle.WantsWork(in field, CropPhase.Prepare) &&
                  !CropCycle.WantsWork(in field, CropPhase.Rest);

        Console.WriteLine($"    {string.Join(" | ", report)}");
        return passed;
    }

    /// <summary>
    /// Session 6.5's gate for stage A: a prepared field is reaped and carried in, an unbroken one is not.
    /// </summary>
    /// <remarks>
    /// Both halves in one run, side by side, because the interesting claim is the <em>difference</em>: the
    /// two fields are identical, worked by identical farmers for the same window, and one of them yields a
    /// crop because somebody broke its ground in a season that has already passed. Nothing visible at
    /// harvest distinguishes them except what the field says about itself.
    /// </remarks>
    /// <summary>
    /// The hand-written sums agree with the generic ones, for every resource there is.
    /// </summary>
    /// <remarks>
    /// <b>This is the guard that lets <c>NodeStock.Total</c> name its fields.</b> Writing it as a loop over
    /// <see cref="Resources.All"/> is correct by construction and far too slow where it is actually called —
    /// once per node per tick in the spent-node sweep, which on a thirty-thousand-tree map stopped the
    /// settlement year finishing at all. So it names its fields, and this notices if a resource is ever added
    /// to the enum and not to the sum.
    /// <para>
    /// Deliberately checking the property that would break rather than the code that would be missing: a value
    /// in one resource must show up in the total and must make it non-zero, whichever resource it is. A test
    /// that listed the resources by hand would have the same hole as the code.
    /// </para>
    /// </remarks>
    private static bool EveryResourceIsCounted()
    {
        var report = new List<string>();
        var counted = true;
        foreach (var resource in Resources.All)
        {
            var stock = default(NodeStock);
            stock.Add(resource, 7);
            var direct = stock.Total;
            var swept = stock.TotalBySweep;
            var zero = stock.IsZero;
            if (direct != 7 || swept != 7 || zero)
            {
                counted = false;
                report.Add($"{resource}: total {direct}, swept {swept}, zero {zero}");
            }
        }

        var empty = default(NodeStock);
        if (empty.Total != 0 || !empty.IsZero)
        {
            counted = false;
            report.Add($"empty: total {empty.Total}, zero {empty.IsZero}");
        }

        Console.WriteLine(
            "    " + (report.Count == 0
                ? $"all {Resources.All.Length} resources counted by Total, TotalBySweep and IsZero"
                : string.Join("; ", report)));
        return counted;
    }

    private static bool AFieldYieldsWhatItsLabourEarned()
    {
        var world = new SimulationWorld();
        // Start in the harvest, and hand-set what spring and summer did — which is the point: the
        // difference between these two fields was settled before this test starts.
        world.StartAtSeconds(EconomySeconds(3100f));
        var granary = world.AddNode(NodeKind.Granary, Vector2.Zero, capacity: 4000);
        var prepared = world.AddNode(NodeKind.Farm, new Vector2(9f, 0f), capacity: 400);
        var unbroken = world.AddNode(NodeKind.Farm, new Vector2(-9f, 0f), capacity: 400);
        world.Nodes.Get(prepared).PrepareWork = CropCycle.PrepareLabour;
        world.Nodes.Get(prepared).MaintainWork = CropCycle.MaintainLabour;
        world.Nodes.Get(prepared).CycleYear = world.Date.Year;
        world.Nodes.Get(unbroken).CycleYear = world.Date.Year;

        foreach (var farm in new[] { prepared, unbroken })
        {
            var at = world.Nodes.Get(farm).Position;
            var extent = world.Nodes.Get(farm).FootprintRadius;
            var hand = world.SpawnAgent(at + new Vector2(extent + 1.3f, 0f), UnitType.Villager);
            world.QueueAssign(
                new[] { hand },
                Assignment.Work(
                    farm, at, extent, Resource.Grain,
                    EconomySystem.WorkShiftSeconds, EconomySystem.HandoverSeconds));
        }

        var drift = default(ResourceTotals);
        for (var tick = 0; tick < EconomyTicks(300f); tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero) break;
        }

        var stored = world.Nodes.Get(granary).Stock.Grain;
        var reaped = world.Nodes.Get(prepared).ReapWork;
        var barren = world.Nodes.Get(unbroken).ReapWork;
        var says = CropCycle.StateOf(in world.Nodes.Get(unbroken), world.Date.Season);

        // The prepared field is being reaped and its crop is arriving; the unbroken one is not touched at
        // all, because there is nothing there to reap.
        var passed = reaped > 0f && barren == 0f && stored > 0 &&
                     says == "failed" && drift.IsZero;
        Console.WriteLine(
            $"    prepared field reaped {reaped:F0} labour-s and delivered {stored} grain; " +
            $"unbroken field reaped {barren:F0} and says '{says}'; drift {drift.Grain}");
        return passed;
    }

    /// <summary>
    /// Wood is felled out of a finite tree, not produced, and the tree goes away when it is gone.
    /// </summary>
    /// <remarks>
    /// The claim worth asserting is the <em>ledger</em> one: over the whole run, nothing is added to the
    /// world's wood. <c>Produced.Wood</c> stays at zero while wood moves out of a trunk, onto a back and
    /// into a granary, and conservation holds every tick — which is only possible because a tree's
    /// standing timber is stock like any other's. If wood were ever produced, this would be the test that
    /// noticed, and it would notice on the tick it happened.
    /// </remarks>
    private static bool WoodComesOutOfTreesAndTreesRunOut()
    {
        var world = new SimulationWorld();
        var granary = world.AddNode(NodeKind.Granary, Vector2.Zero, capacity: 4000);
        world.AddNode(NodeKind.House, new Vector2(0f, 7f), capacity: 0, occupancy: 4);
        // Fed, so nobody emigrates mid-test and takes a load of wood over the hill with them.
        world.SeedStock(granary, Resource.Grain, 2000);
        var tree = world.AddNode(NodeKind.Tree, new Vector2(9f, 0f), capacity: (int)Woodland.WoodPerTree);
        world.SeedStock(tree, Resource.Wood, (int)Woodland.WoodPerTree);
        var seeded = world.Economy.Seeded.Wood;

        var at = world.Nodes.Get(tree).Position;
        var extent = world.Nodes.Get(tree).FootprintRadius;
        var cutter = world.SpawnAgent(at + new Vector2(1.4f, 0f), UnitType.Villager);
        world.QueueAssign(
            new[] { cutter },
            Assignment.Work(
                tree, at, extent, Resource.Wood,
                Woodland.LoadSeconds(UnitType.Villager.CarryCapacity), EconomySystem.HandoverSeconds));

        // Long enough to fell the whole tree three loads over, plus the walking.
        var drift = default(ResourceTotals);
        var felled = false;
        for (var tick = 0; tick < EconomyTicks(1400f); tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero) break;
            if (!world.Nodes.Contains(tree)) felled = true;
        }

        var stored = world.Nodes.Get(granary).Stock.Wood;
        var (standing, trees) = world.Nodes.StandingTimber();
        // Everything the tree held is now in the granary, in somebody's hands, or burnt by the household.
        var accounted = stored + EconomySystem.CarriedTotal(world.Agents).Wood +
                        (int)world.Economy.Consumed.Wood + standing;
        var passed = felled && trees == 0 && world.Economy.Produced.Wood == 0 &&
                     stored > 0 && accounted == seeded &&
                     drift.IsZero;
        Console.WriteLine(
            $"    one tree of {seeded} wood: felled={felled}, {trees} left standing, " +
            $"{stored} in the granary, produced {world.Economy.Produced.Wood} (must be 0), " +
            $"{accounted}/{seeded} accounted for, drift {drift.Wood}");
        return passed;
    }

    /// <summary>
    /// The second half of Stage B's gate, both directions, on one map.
    /// </summary>
    /// <remarks>
    /// <b>The trigger is stranded stock, and this is what that buys.</b> Two settlements, identical
    /// except for one building:
    /// <list type="bullet">
    /// <item>Trees near the granary: the cutter walks its own wood in and <em>no cart moves at all</em>.
    /// Hauling in a compact settlement is not a small number, it is zero, because the producer carrying
    /// its own output is the whole design and a cart between a tree and a granary forty metres away is
    /// pure overhead.</item>
    /// <item>Trees a long way out with a forward depot beside them: the cutter delivers to the depot
    /// because it is nearer, the depot is a store no household draws from, and the board collects
    /// stranded stock — so carts appear, and wood reaches the granary having been carried by two
    /// different pairs of legs.</item>
    /// </list>
    /// Nothing in either half knows what a lumber camp is. The difference is entirely geometric.
    /// </remarks>
    private static bool TheWoodLineDecidesWhetherHaulersAreNeeded()
    {
        var (nearHauls, nearStored, nearDrift) = RunWoodLine(farTrees: false, withDepot: false);
        var (farHauls, farStored, farDrift) = RunWoodLine(farTrees: true, withDepot: true);
        var passed = nearHauls == 0 && nearStored > 0 && nearDrift == 0 &&
                     farHauls > 0 && farStored > 0 && farDrift == 0;
        Console.WriteLine(
            $"    trees at hand: {nearHauls} hauls, {nearStored} wood in the granary | " +
            $"wood line pushed out with a depot on it: {farHauls} hauls, {farStored} in the granary | " +
            $"drift {nearDrift}/{farDrift}");
        return passed;
    }

    private static (long Hauls, int Stored, long Drift) RunWoodLine(bool farTrees, bool withDepot)
    {
        var world = new SimulationWorld(240f);
        var granary = world.AddNode(NodeKind.Granary, Vector2.Zero, capacity: 4000);
        // A house, so the granary is a store somebody eats out of. Without one nothing draws on it, the
        // granary itself counts as stranded, and the test would measure the opposite of what it means to.
        world.AddNode(NodeKind.House, new Vector2(0f, 9f), capacity: 0, occupancy: 4);
        // And bread in it, because a household that goes hungry long enough loses somebody — who drops
        // whatever they were carrying, which becomes a heap, which the board sends a cart for. That is
        // correct behaviour and it is a haul this test would have counted as evidence about the wood line.
        world.SeedStock(granary, Resource.Grain, 2000);

        var line = farTrees ? 70f : 12f;
        var trees = new List<NodeId>();
        for (var i = 0; i < 6; i++)
        {
            var at = new Vector2(line + (i % 3) * 4f, (i / 3) * 4f - 2f);
            var tree = world.AddNode(NodeKind.Tree, at, capacity: (int)Woodland.WoodPerTree);
            world.SeedStock(tree, Resource.Wood, (int)Woodland.WoodPerTree);
            trees.Add(tree);
        }

        if (withDepot)
        {
            // The lumber camp. A store, at the tree line, that nobody lives near.
            world.AddNode(NodeKind.ForwardDepot, new Vector2(line - 6f, 0f), capacity: 400);
        }

        for (var i = 0; i < 2; i++)
        {
            var tree = trees[i];
            var at = world.Nodes.Get(tree).Position;
            var extent = world.Nodes.Get(tree).FootprintRadius;
            var cutter = world.SpawnAgent(at + new Vector2(1.4f, 0f), UnitType.Villager);
            world.QueueAssign(
                new[] { cutter },
                Assignment.Work(
                    tree, at, extent, Resource.Wood,
                    Woodland.LoadSeconds(UnitType.Villager.CarryCapacity),
                    EconomySystem.HandoverSeconds));
        }

        // Two carters, standing at the granary with nothing to do until the geometry gives them
        // something. The settlement pays for their carts out of the granary's timber — which is the
        // dependency worth having: hauling wood in requires already having wood.
        world.SeedStock(granary, Resource.Wood, SimulationWorld.CartTimber * 2);
        for (var i = 0; i < 2; i++)
        {
            world.TryBuildCart(world.SpawnAgent(new Vector2(-6f, i * 2f - 1f), UnitType.Villager));
        }

        var drift = 0L;
        for (var tick = 0; tick < EconomyTicks(1600f); tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var discrepancy = world.Economy.Discrepancy(world.Nodes, world.Agents);
            drift = discrepancy.Fault;
            if (drift != 0) break;
        }

        return (world.Economy.HaulsAssigned, world.Nodes.Get(granary).Stock.Wood, drift);
    }

    /// <summary>
    /// Hauling is a role, and the whole of what that means.
    /// </summary>
    /// <remarks>
    /// Four claims, and each of them is something the old permanent hauler unit could not express:
    /// <list type="bullet">
    /// <item><b>It costs timber, and the timber is gone.</b> Not moved onto the cart — turned into one. So
    /// it is consumed, the conservation identity still closes, and building a hauling network is visible
    /// in the ledger as a thing the settlement spent wood on.</item>
    /// <item><b>Without timber it is refused.</b> You cannot cart wood in before you have wood, which is
    /// the dependency Stage B's receding wood line exists to create.</item>
    /// <item><b>The body changes.</b> A carter is the <c>HaulerCart</c> frame worn by a person — wider,
    /// slower, holding more — and its collider proxies grow with it, or it would be priced at one size and
    /// collide at another.</item>
    /// <item><b>Asked to do something else, the cart goes.</b> But an <em>order</em> is not being asked to
    /// do something else: an interrupt never touches the assignment, so a carter sent somewhere by hand
    /// comes back to its route still pulling its cart.</item>
    /// </list>
    /// </remarks>
    private static bool ACartIsAJobAndNotAUnit()
    {
        var world = new SimulationWorld();
        var granary = world.AddNode(NodeKind.Granary, new Vector2(-9f, 0f), capacity: 4000);
        var depot = world.AddNode(NodeKind.ForwardDepot, new Vector2(9f, 0f), capacity: 4000);
        world.SeedStock(granary, Resource.Wood, SimulationWorld.CartTimber);
        // Enough that the route is still running when the order arrives, with room to spare at both ends.
        // A route that has drained its source or filled its sink has ended — correctly — and an ended route
        // has no cart to test. The first version of this had 120 grain and measured exactly that: all of it
        // moved, and LegsCompleted read as zero because the assignment had been cleared before the
        // assertion looked at it.
        //
        // <b>Sized ten times over, because the second time it happened the fixture was not what changed.</b>
        // 400 units was comfortable at a four-second handover — twenty legs of forty is 800, but the dwell
        // ate half the window — and dropping the handover to a quarter of a second let the same route move
        // the lot inside the same 240 s. A fixture whose margin depends on how long a transfer takes is
        // measuring the transfer, not the cart.
        world.SeedStock(depot, Resource.Grain, 4000);

        var villager = world.SpawnAgent(new Vector2(0f, 4f), UnitType.Villager);
        var pauper = world.SpawnAgent(new Vector2(0f, -4f), UnitType.Villager);
        var wideBefore = world.Agents.Get(villager).Radius;
        var carryBefore = world.Agents.Get(villager).CarryCapacity;
        var woodBefore = world.Nodes.Get(granary).Stock.Wood;
        var consumedBefore = world.Economy.Consumed.Wood;

        // Exactly one cart's worth of timber in the world, so the first route is granted and the second
        // is refused for want of it.
        var granted = world.TryAssignRoute(villager, depot, granary);
        var refused = !world.TryAssignRoute(pauper, depot, granary);
        var paid = woodBefore - world.Nodes.Get(granary).Stock.Wood == SimulationWorld.CartTimber &&
                   world.Economy.Consumed.Wood - consumedBefore == SimulationWorld.CartTimber;

        var carter = world.Agents.Get(villager);
        var wore = carter.HasCart &&
                   MathF.Abs(carter.Radius - UnitType.HaulerCart.Radius) < 0.001f &&
                   carter.CarryCapacity == UnitType.HaulerCart.CarryCapacity &&
                   MathF.Abs(
                       world.Colliders.Get(carter.Colliders.Movement).Shape.Radius -
                       UnitType.HaulerCart.Radius) < 0.001f;

        // The route runs, and keeps running: a standing commitment, not one round trip. A Haul would have
        // ended after the first delivery and gone back on the board.
        var legs = 0;
        for (var tick = 0; tick < EconomyTicks(240f); tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            legs = Math.Max(legs, world.Agents.Get(villager).Jobs.LegsCompleted);
        }

        var moved = world.Nodes.Get(granary).Stock.Grain;
        var stillOnRoute = world.Agents.Get(villager).Jobs.Assignment.Kind == AssignmentKind.Carry;

        // An order is an interrupt, so the cart survives it.
        world.QueueMove(new[] { villager }, new Vector2(0f, 9f));
        Tick(world, 30 * 6);
        var keptThroughAnOrder = world.Agents.Get(villager).HasCart;

        // Being taken off work is not an interrupt, and the cart goes with the job.
        world.QueueAssign(new[] { villager }, Assignment.None);
        Tick(world, 2);
        var after = world.Agents.Get(villager);
        var scrapped = !after.HasCart &&
                       MathF.Abs(after.Radius - wideBefore) < 0.001f &&
                       after.CarryCapacity == carryBefore &&
                       MathF.Abs(
                           world.Colliders.Get(after.Colliders.Movement).Shape.Radius - wideBefore) <
                       0.001f;

        var drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
        var passed = granted && refused && paid && wore && moved > 0 && legs > 2 && stillOnRoute &&
                     keptThroughAnOrder && scrapped && drift.IsZero;
        Console.WriteLine(
            $"    cart cost {SimulationWorld.CartTimber} wood (paid={paid}), second one refused={refused}, " +
            $"body {wideBefore:F2}->{UnitType.HaulerCart.Radius:F2} m (worn={wore}); route ran {legs} legs, " +
            $"moved {moved} grain, still standing={stillOnRoute}; kept through an order=" +
            $"{keptThroughAnOrder}, scrapped when taken off work={scrapped}; drift {drift.Grain}/{drift.Wood}");
        return passed;
    }

    /// <summary>
    /// The chair's generic post command recognises stone exactly as it recognises a field or tree.
    /// </summary>
    private static bool PostingAtAnOutcropEstablishesQuarryWork()
    {
        var world = new SimulationWorld();
        var granary = world.AddNode(NodeKind.Granary, Vector2.Zero, capacity: 4000);
        var rock = world.AddNode(
            NodeKind.Outcrop,
            new Vector2(8f, 0f),
            capacity: (int)Quarrying.StonePerOutcrop);
        world.SeedStock(rock, Resource.Stone, (int)Quarrying.StonePerOutcrop);
        var quarrier = world.SpawnAgent(new Vector2(6f, 0f), UnitType.Villager);

        ref readonly var outcrop = ref world.Nodes.Get(rock);
        // This is the same Hold the U key queues; SimulationWorld turns it into the work belonging to
        // the node under the post.
        world.QueueAssign(
            new[] { quarrier },
            Assignment.Hold(outcrop.Position, EconomySystem.WorkShiftSeconds, outcrop.FootprintRadius));

        var mostHands = 0;
        var drift = default(ResourceTotals);
        for (var tick = 0; tick < EconomyTicks(800f); tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            if (world.Nodes.Contains(rock)) mostHands = Math.Max(mostHands, world.Nodes.Get(rock).Hands);
            drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero || world.Nodes.Get(granary).Stock.Stone > 0) break;
        }

        ref readonly var body = ref world.Agents.Get(quarrier);
        var assigned = body.Jobs.Assignment.Kind == AssignmentKind.Work &&
                       body.Jobs.Assignment.Cargo == Resource.Stone;
        var stored = world.Nodes.Get(granary).Stock.Stone;
        var passed = assigned && mostHands == 1 && stored > 0 &&
                     world.Economy.Produced.Stone == 0 && drift.IsZero;
        Console.WriteLine(
            $"    assignment={body.Jobs.Assignment.Kind}/{body.Jobs.Assignment.Cargo}, " +
            $"hands at face={mostHands}, delivered={stored}, produced={world.Economy.Produced.Stone}, " +
            $"drift={drift.Stone}");
        return passed;
    }

    /// <summary>
    /// A player route is between places, and changes cargo when the destination's useful demand changes.
    /// </summary>
    private static bool OneRouteSuppliesEveryMaterial()
    {
        var world = new SimulationWorld();
        var source = world.AddNode(NodeKind.ForwardDepot, new Vector2(-7f, 0f), capacity: 4000);
        var site = world.AddNode(NodeKind.Granary, new Vector2(7f, 0f), capacity: 4000, built: false);
        var woodCost = Construction.TimberFor(NodeKind.Granary);
        var stoneCost = Construction.StoneFor(NodeKind.Granary);
        world.SeedStock(source, Resource.Wood, woodCost + SimulationWorld.CartTimber + 80);
        world.SeedStock(source, Resource.Stone, stoneCost + 80);
        var carter = world.SpawnAgent(new Vector2(-3f, 3f), UnitType.Villager);
        var granted = world.TryAssignRoute(carter, source, site);

        var carriedWood = false;
        var carriedStone = false;
        var checkedSave = false;
        string? divergence = null;
        var drift = default(ResourceTotals);
        for (var tick = 0; tick < EconomyTicks(900f); tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            ref readonly var body = ref world.Agents.Get(carter);
            if (body.Jobs.CarriedUnits > 0)
            {
                carriedWood |= body.Jobs.Carrying == Resource.Wood;
                carriedStone |= body.Jobs.Carrying == Resource.Stone;
            }

            ref readonly var building = ref world.Nodes.Get(site);
            if (!checkedSave && building.Stock.Wood > 0)
            {
                var loaded = WorldSave.RoundTrip(world);
                divergence = DeterminismCheck.Diverges(world, loaded, ticks: 300);
                checkedSave = true;
                if (divergence is not null) break;
            }

            drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero || building.Stock.Wood == woodCost && building.Stock.Stone == stoneCost) break;
        }

        ref readonly var supplied = ref world.Nodes.Get(site);
        var standing = world.Agents.Get(carter).Jobs.Assignment.Kind == AssignmentKind.Carry;
        var passed = granted && carriedWood && carriedStone && checkedSave && divergence is null &&
                     supplied.Stock.Wood == woodCost && supplied.Stock.Stone == stoneCost && standing &&
                     drift.IsZero;
        Console.WriteLine(
            $"    site received {supplied.Stock.Wood}/{woodCost} timber and " +
            $"{supplied.Stock.Stone}/{stoneCost} stone; carried wood={carriedWood}, stone={carriedStone}; " +
            $"route still standing={standing}; " +
            $"save future={(divergence is null ? "identical" : divergence)}; " +
            $"drift={drift.Wood}/{drift.Stone}");
        return passed;
    }

    /// <summary>A build order begins with the useful journey rather than an empty visit to the site.</summary>
    private static bool ANewBuildersFirstTripIsUseful()
    {
        var world = new SimulationWorld();
        var store = world.AddNode(NodeKind.Granary, new Vector2(-12f, 0f), capacity: 5000);
        var site = world.AddNode(NodeKind.Granary, new Vector2(12f, 0f), capacity: 5000, built: false);
        world.SeedStock(store, Resource.Wood, Construction.TimberFor(NodeKind.Granary) + 120);
        world.SeedStock(store, Resource.Stone, Construction.StoneFor(NodeKind.Granary) + 120);
        world.SeedStock(store, Resource.Grain, 17);

        var empty = world.SpawnAgent(new Vector2(0f, -2f), UnitType.Villager);
        var useful = world.SpawnAgent(Vector2.Zero, UnitType.Villager);
        var unrelated = world.SpawnAgent(new Vector2(0f, 2f), UnitType.Villager);
        var usefulLoad = Math.Min(40, world.Agents.Get(useful).CarryCapacity);
        world.Nodes.Get(store).Stock.Add(Resource.Wood, -usefulLoad);
        world.Agents.Get(useful).Jobs.Carrying = Resource.Wood;
        world.Agents.Get(useful).Jobs.CarriedUnits = usefulLoad;
        world.Nodes.Get(store).Stock.Add(Resource.Grain, -17);
        world.Agents.Get(unrelated).Jobs.Carrying = Resource.Grain;
        world.Agents.Get(unrelated).Jobs.CarriedUnits = 17;

        ref readonly var project = ref world.Nodes.Get(site);
        world.QueueAssign(
            new[] { empty, useful, unrelated },
            Assignment.Hold(project.Position, EconomySystem.WorkShiftSeconds, project.FootprintRadius));
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);

        ref readonly var fetching = ref world.Agents.Get(empty);
        ref readonly var delivering = ref world.Agents.Get(useful);
        ref readonly var returning = ref world.Agents.Get(unrelated);
        var fetchesFirst = fetching.Jobs.Assignment.Kind == AssignmentKind.Build &&
                           fetching.Jobs.Project == site && fetching.Jobs.Leg % 2 == 0 &&
                           fetching.Jobs.Assignment.Source == store &&
                           fetching.Jobs.Assignment.Sink == site &&
                           fetching.Jobs.ReservedUnits > 0 &&
                           Construction.CostFor(NodeKind.Granary)[fetching.Jobs.Assignment.Cargo] > 0;
        var usefulGoesToSite = delivering.Jobs.Assignment.Kind == AssignmentKind.Build &&
                               delivering.Jobs.Project == site && delivering.Jobs.Leg % 2 != 0 &&
                               delivering.Jobs.Assignment.Sink == site &&
                               delivering.Jobs.Carrying == Resource.Wood &&
                               delivering.Jobs.CarriedUnits == usefulLoad;
        var unrelatedGoesHome = returning.Jobs.Assignment.Kind == AssignmentKind.Build &&
                                returning.Jobs.Project == site && returning.Jobs.Leg % 2 != 0 &&
                                returning.Jobs.Assignment.Sink == store &&
                                returning.Jobs.Carrying == Resource.Grain &&
                                returning.Jobs.CarriedUnits == 17;
        var drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
        Console.WriteLine(
            $"    empty hand source-first={fetchesFirst} ({fetching.Jobs.Assignment.Cargo}, " +
            $"{fetching.Jobs.ReservedUnits} reserved); useful load site-first={usefulGoesToSite}; " +
            $"unrelated load store-first={unrelatedGoesHome}; drift={drift.Grain}/{drift.Wood}/{drift.Stone}");
        return fetchesFirst && usefulGoesToSite && unrelatedGoesHome && drift.IsZero;
    }

    /// <summary>
    /// Builders themselves close the physical loop: claim distinct useful loads, deliver, consume as they
    /// work, return an unrelated carried load, and leave no material or assignment in limbo at completion.
    /// </summary>
    private static bool BuildersFetchShareConsumeAndClear()
    {
        var world = new SimulationWorld();
        var store = world.AddNode(NodeKind.Granary, new Vector2(-9f, 0f), capacity: 5000);
        var site = world.AddNode(NodeKind.Granary, new Vector2(9f, 0f), capacity: 5000, built: false);
        var woodCost = Construction.TimberFor(NodeKind.Granary);
        var stoneCost = Construction.StoneFor(NodeKind.Granary);
        world.SeedStock(store, Resource.Wood, woodCost + 90);
        world.SeedStock(store, Resource.Stone, stoneCost + 90);
        world.SeedStock(store, Resource.Grain, 17);

        var builders = new[]
        {
            world.SpawnAgent(new Vector2(5f, -3f), UnitType.Villager),
            world.SpawnAgent(new Vector2(5f, -1f), UnitType.Villager),
            world.SpawnAgent(new Vector2(5f, 1f), UnitType.Villager),
            world.SpawnAgent(new Vector2(5f, 3f), UnitType.Villager),
        };
        // An irrelevant physical load must be returned, not deleted and not mistaken for project material.
        world.Nodes.Get(store).Stock.Add(Resource.Grain, -17);
        world.Agents.Get(builders[0]).Jobs.Carrying = Resource.Grain;
        world.Agents.Get(builders[0]).Jobs.CarriedUnits = 17;

        ref readonly var project = ref world.Nodes.Get(site);
        world.QueueAssign(
            builders,
            Assignment.Hold(project.Position, EconomySystem.WorkShiftSeconds, project.FootprintRadius));

        var carriedWood = false;
        var carriedStone = false;
        var sharedClaims = false;
        var incremental = false;
        var returnedIrrelevant = false;
        var checkedSave = false;
        string? divergence = null;
        var drift = default(ResourceTotals);
        for (var tick = 0; tick < EconomyTicks(1800f); tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);

            var woodClaims = 0;
            var stoneClaims = 0;
            foreach (var id in builders)
            {
                ref readonly var body = ref world.Agents.Get(id);
                if (body.Jobs.CarriedUnits > 0)
                {
                    carriedWood |= body.Jobs.Carrying == Resource.Wood;
                    carriedStone |= body.Jobs.Carrying == Resource.Stone;
                }

                if (body.Jobs.Assignment.Kind != AssignmentKind.Build) continue;
                if (body.Jobs.Assignment.Cargo == Resource.Wood && body.Jobs.ReservedUnits > 0) woodClaims++;
                if (body.Jobs.Assignment.Cargo == Resource.Stone && body.Jobs.ReservedUnits > 0) stoneClaims++;
            }

            sharedClaims |= woodClaims > 0 && stoneClaims > 0;
            returnedIrrelevant |= world.Nodes.Get(store).Stock.Grain == 17;
            ref readonly var building = ref world.Nodes.Get(site);
            incremental |= building.BuildWork > 0f && building.IsUnderConstruction &&
                           building.BuildConsumed.Total > 0 && building.WantsMaterials;

            if (!checkedSave && building.BuildConsumed.Total > 0 && building.IsUnderConstruction)
            {
                var loaded = WorldSave.RoundTrip(world);
                divergence = DeterminismCheck.Diverges(world, loaded, ticks: 300);
                checkedSave = true;
                if (divergence is not null) break;
            }

            drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero) break;
            if (!building.IsBuilt) continue;
            var cleared = builders.All(id =>
                world.Agents.Get(id).Jobs.Assignment.Kind != AssignmentKind.Build &&
                world.Agents.Get(id).Jobs.CarriedUnits == 0);
            if (cleared) break;
        }

        ref readonly var finished = ref world.Nodes.Get(site);
        var allCleared = builders.All(id =>
            world.Agents.Get(id).Jobs.Assignment.Kind != AssignmentKind.Build &&
            world.Agents.Get(id).Jobs.CarriedUnits == 0);
        var exactRecipe = finished.BuildConsumed.Wood == woodCost &&
                          finished.BuildConsumed.Stone == stoneCost &&
                          world.Economy.Consumed.Wood == woodCost &&
                          world.Economy.Consumed.Stone == stoneCost;
        var surplusCleared = SurplusLeavesFinishedHouse();
        var passed = finished.IsBuilt && incremental && carriedWood && carriedStone && sharedClaims &&
                     returnedIrrelevant && allCleared && exactRecipe && finished.Stock.Total == 0 &&
                     checkedSave && divergence is null && drift.IsZero && surplusCleared;
        Console.WriteLine(
            $"    incremental={incremental}, shared wood/stone claims={sharedClaims}, " +
            $"carried={carriedWood}/{carriedStone}, returned unrelated grain={returnedIrrelevant}; " +
            $"consumed {finished.BuildConsumed.Wood}/{woodCost} timber and " +
            $"{finished.BuildConsumed.Stone}/{stoneCost} stone; cleared={allCleared}; " +
            $"surplus house cleared={surplusCleared}; " +
            $"save future={(divergence is null ? "identical" : divergence)}; " +
            $"drift={drift.Wood}/{drift.Stone}");
        return passed;
    }

    private static bool SurplusLeavesFinishedHouse()
    {
        var world = new SimulationWorld();
        var store = world.AddNode(NodeKind.Granary, new Vector2(-7f, 0f), capacity: 2000);
        var site = world.AddNode(NodeKind.House, new Vector2(7f, 0f), capacity: 0, occupancy: 4, built: false);
        var cost = Construction.TimberFor(NodeKind.House);
        const int surplus = 13;
        world.SeedStock(store, Resource.Wood, cost + surplus);
        // A legacy over-delivery or cancelled parallel trip: physical, accounted stock already at the site.
        world.Nodes.Get(store).Stock.Add(Resource.Wood, -(cost + surplus));
        world.Nodes.Get(site).Stock.Add(Resource.Wood, cost + surplus);
        var builder = world.SpawnAgent(new Vector2(4f, 0f), UnitType.Villager);
        ref readonly var project = ref world.Nodes.Get(site);
        world.QueueAssign(
            new[] { builder },
            Assignment.Hold(project.Position, EconomySystem.WorkShiftSeconds, project.FootprintRadius));

        var drift = default(ResourceTotals);
        for (var tick = 0; tick < EconomyTicks(1500f); tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero) break;
            if (world.Nodes.Get(site).IsBuilt && world.Nodes.Get(site).Stock.Wood == 0 &&
                world.Nodes.Get(store).Stock.Wood == surplus &&
                world.Agents.Get(builder).Jobs.Assignment.Kind == AssignmentKind.None)
            {
                break;
            }
        }

        return world.Nodes.Get(site).IsBuilt && world.Nodes.Get(site).Stock.Wood == 0 &&
               world.Nodes.Get(store).Stock.Wood == surplus &&
               world.Economy.Consumed.Wood == cost &&
               world.Agents.Get(builder).Jobs.CarriedUnits == 0 && drift.IsZero;
    }

    /// <summary>
    /// A building is paid for in material and somebody's time, and does nothing until it is paid.
    /// </summary>
    /// <remarks>
    /// Four claims:
    /// <list type="bullet">
    /// <item><b>An unfinished building does nothing.</b> Not "does less" — a half-built granary stores
    /// nothing, owns no catchment and cannot be delivered to as a store, because every predicate on the
    /// node asks whether it is built rather than every caller remembering to.</item>
    /// <item><b>Timber has to be carried there.</b> A site is a demand at a place, which is the one task
    /// the hauling board reads backwards, and it is the most urgent thing on the board because hands
    /// standing at a site with no materials are hands doing nothing at all.</item>
    /// <item><b>Then hands.</b> Labour accrues per site from whoever is standing at it, so four builders
    /// are four times the work — a building has no output, so there is nothing to attribute.</item>
    /// <item><b>And the timber stops existing.</b> Consumed into the wall on completion, so the
    /// conservation identity closes with no term for material turned into building.</item>
    /// </list>
    /// </remarks>
    private static bool ABuildingCostsLabour()
    {
        var world = new SimulationWorld(240f);
        var granary = world.AddNode(NodeKind.Granary, Vector2.Zero, capacity: 4000);
        world.SeedStock(granary, Resource.Wood, 900);
        world.SeedStock(granary, Resource.Grain, 900);

        // A depot far enough away that its timber has to be carted rather than being in the same yard.
        var site = world.AddNode(
            NodeKind.ForwardDepot, new Vector2(40f, 0f), capacity: 400, built: false);
        var timber = Construction.TimberFor(NodeKind.ForwardDepot);
        var startedUseless = !world.Nodes.Get(site).Stores &&
                             !world.Nodes.Get(site).OwnsCatchment &&
                             world.Nodes.Get(site).IsUnderConstruction;

        // A carter to bring the materials, and two builders posted on the site.
        var carter = world.SpawnAgent(new Vector2(-6f, 0f), UnitType.Villager);
        world.TryBuildCart(carter);
        for (var i = 0; i < 2; i++)
        {
            var hand = world.SpawnAgent(new Vector2(40f, 6f + i * 1.4f), UnitType.Villager);
            world.QueueAssign(
                new[] { hand },
                Assignment.Post(
                    site,
                    world.Nodes.Get(site).Position,
                    world.Nodes.Get(site).FootprintRadius,
                    EconomySystem.WorkShiftSeconds));
        }

        // Long enough for the timber to arrive and two pairs of hands to finish 600 labour-seconds.
        var deliveredAt = -1f;
        var raisedAt = -1f;
        var drift = 0L;
        for (var tick = 1; tick <= EconomyTicks(900f); tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var discrepancy = world.Economy.Discrepancy(world.Nodes, world.Agents);
            drift = discrepancy.Fault;
            if (drift != 0) break;
            if (!world.Nodes.Contains(site)) break;
            if (deliveredAt < 0f && !world.Nodes.Get(site).WantsMaterials)
            {
                deliveredAt = tick / 30f;
            }

            if (raisedAt < 0f && world.Nodes.Get(site).IsBuilt) raisedAt = tick / 30f;
        }

        ref readonly var finished = ref world.Nodes.Get(site);
        var works = finished.IsBuilt && finished.Stores && finished.OwnsCatchment;
        // The timber left the world rather than sitting in the finished building.
        var spent = finished.Stock.Wood == 0 && world.Economy.Consumed.Wood >= timber;
        var passed = startedUseless && deliveredAt > 0f && raisedAt > deliveredAt && works && spent &&
                     world.Economy.Raised == 1 && drift == 0;
        Console.WriteLine(
            $"    a depot at 40 m: useless while a site={startedUseless}, {timber} timber carted out by " +
            $"{deliveredAt:F0} s, two builders finished {Construction.LabourFor(NodeKind.ForwardDepot):F0} " +
            $"labour-seconds by {raisedAt:F0} s, then it stores and owns a catchment={works}; timber " +
            $"consumed into the wall={spent}; drift {drift}");
        return passed;
    }

    /// <summary>Damage is not negative construction: repair has its own saved work, cost and condition.</summary>
    private static bool RepairConsumesAsConditionReturns()
    {
        var world = new SimulationWorld();
        var store = world.AddNode(NodeKind.Granary, new Vector2(-9f, 0f), capacity: 3000);
        var wall = world.AddNode(NodeKind.StoneWall, new Vector2(9f, 0f), capacity: 0);
        world.SeedStock(store, Resource.Stone, 300);
        var originalId = wall;
        var originalCollider = world.Nodes.Get(wall).Collider;
        var maximum = world.Nodes.Get(wall).MaxCondition;
        var damaged = world.DamageStructure(wall, maximum * 0.5f);
        var start = world.Nodes.Get(wall).Condition;
        var began = world.BeginRepair(wall);
        var recipe = StructuralProjects.CostFor(in world.Nodes.Get(wall));
        var labour = StructuralProjects.LabourFor(in world.Nodes.Get(wall));
        var consumedBefore = world.Economy.Consumed.Stone;
        var builders = new[]
        {
            world.SpawnAgent(new Vector2(-5f, -1f), UnitType.Villager),
            world.SpawnAgent(new Vector2(-5f, 1f), UnitType.Villager),
        };
        ref readonly var project = ref world.Nodes.Get(wall);
        world.QueueAssign(
            builders,
            Assignment.Hold(project.Position, EconomySystem.WorkShiftSeconds, project.FootprintRadius));

        var carried = false;
        var incremental = false;
        var checkedSave = false;
        string? divergence = null;
        var drift = default(ResourceTotals);
        for (var tick = 0; tick < EconomyTicks(900f); tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            foreach (var id in builders)
            {
                ref readonly var body = ref world.Agents.Get(id);
                carried |= body.Jobs.CarriedUnits > 0 && body.Jobs.Carrying == Resource.Stone;
            }

            ref readonly var structure = ref world.Nodes.Get(wall);
            incremental |= structure.StructuralProject == StructuralProjectKind.Repair &&
                           structure.Condition > start && structure.Condition < maximum &&
                           structure.StructuralConsumed.Stone > 0;
            if (!checkedSave && incremental)
            {
                var loaded = WorldSave.RoundTrip(world);
                divergence = DeterminismCheck.Diverges(world, loaded, ticks: 300);
                checkedSave = true;
                if (divergence is not null) break;
            }

            drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero) break;
            if (structure.StructuralProject == StructuralProjectKind.None &&
                builders.All(id => world.Agents.Get(id).Jobs.Assignment.Kind != AssignmentKind.Build))
            {
                break;
            }
        }

        ref readonly var repaired = ref world.Nodes.Get(wall);
        var exact = world.Economy.Consumed.Stone - consumedBefore == recipe.Stone;
        var stable = repaired.Id == originalId && repaired.Collider == originalCollider &&
                     repaired.Kind == NodeKind.StoneWall;
        var cleared = builders.All(id => world.Agents.Get(id).Jobs.Assignment.Kind != AssignmentKind.Build &&
                                               world.Agents.Get(id).Jobs.CarriedUnits == 0);
        var passed = damaged && began && recipe.Stone > 0 && labour > 0f && carried && incremental &&
                     repaired.StructuralProject == StructuralProjectKind.None &&
                     MathF.Abs(repaired.Condition - maximum) < 0.001f && exact && stable && cleared &&
                     checkedSave && divergence is null && drift.IsZero;
        Console.WriteLine(
            $"    condition {start:F0}->{repaired.Condition:F0}/{maximum:F0}, " +
            $"stone {recipe.Stone} consumed exactly={exact}, incremental={incremental}, carried={carried}; " +
            $"same node/collider={stable}, cleared={cleared}; " +
            $"save future={(divergence is null ? "identical" : divergence)}; drift={drift.Stone}");
        return passed;
    }

    /// <summary>The wall remains a palisade throughout work and swaps material only at completion.</summary>
    private static bool APalisadeBecomesStoneOnTheSameNode()
    {
        var world = new SimulationWorld();
        var store = world.AddNode(NodeKind.Granary, new Vector2(-10f, 0f), capacity: 4000);
        var wall = world.AddNode(NodeKind.PalisadeWall, new Vector2(10f, 0f), capacity: 0);
        world.SeedStock(store, Resource.Stone, 400);
        var originalCollider = world.Nodes.Get(wall).Collider;
        var originalHalfExtent = world.Nodes.Get(wall).HalfExtent;
        var began = world.BeginUpgrade(wall, NodeKind.StoneWall);
        var recipe = StructuralProjects.CostFor(in world.Nodes.Get(wall));
        var consumedBefore = world.Economy.Consumed.Stone;
        var builders = new[]
        {
            world.SpawnAgent(new Vector2(-5f, -2f), UnitType.Villager),
            world.SpawnAgent(new Vector2(-5f, 0f), UnitType.Villager),
            world.SpawnAgent(new Vector2(-5f, 2f), UnitType.Villager),
        };
        ref readonly var project = ref world.Nodes.Get(wall);
        world.QueueAssign(
            builders,
            Assignment.Hold(project.Position, EconomySystem.WorkShiftSeconds, project.FootprintRadius));

        var carriedStone = false;
        var progressedAsPalisade = false;
        var checkedSave = false;
        string? divergence = null;
        var drift = default(ResourceTotals);
        for (var tick = 0; tick < EconomyTicks(900f); tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            foreach (var id in builders)
            {
                ref readonly var body = ref world.Agents.Get(id);
                carriedStone |= body.Jobs.CarriedUnits > 0 && body.Jobs.Carrying == Resource.Stone;
            }

            ref readonly var structure = ref world.Nodes.Get(wall);
            progressedAsPalisade |= structure.StructuralProject == StructuralProjectKind.Upgrade &&
                                    structure.StructuralWork > 0f &&
                                    structure.Kind == NodeKind.PalisadeWall;
            if (!checkedSave && progressedAsPalisade && structure.StructuralConsumed.Stone > 0)
            {
                var loaded = WorldSave.RoundTrip(world);
                divergence = DeterminismCheck.Diverges(world, loaded, ticks: 300);
                checkedSave = true;
                if (divergence is not null) break;
            }

            drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero) break;
            if (structure.Kind == NodeKind.StoneWall &&
                builders.All(id => world.Agents.Get(id).Jobs.Assignment.Kind != AssignmentKind.Build))
            {
                break;
            }
        }

        ref readonly var stone = ref world.Nodes.Get(wall);
        var exact = world.Economy.Consumed.Stone - consumedBefore == recipe.Stone;
        var stable = stone.Id == wall && stone.Collider == originalCollider &&
                     MathF.Abs(stone.HalfExtent - originalHalfExtent) < 0.001f;
        var cleared = builders.All(id => world.Agents.Get(id).Jobs.Assignment.Kind != AssignmentKind.Build &&
                                               world.Agents.Get(id).Jobs.CarriedUnits == 0);
        var surplusCleared = SurplusLeavesUpgradedWall();
        var passed = began && recipe.Stone > 0 && carriedStone && progressedAsPalisade &&
                     stone.Kind == NodeKind.StoneWall && stone.StructuralProject == StructuralProjectKind.None &&
                     stone.Condition == stone.MaxCondition && exact && stable && cleared && surplusCleared &&
                     checkedSave && divergence is null && drift.IsZero;
        Console.WriteLine(
            $"    {recipe.Stone} stone carried={carriedStone} and consumed exactly={exact}; " +
            $"remained palisade while progressing={progressedAsPalisade}, then {stone.Kind}; " +
            $"same node/collider/footprint={stable}, surplus returned={surplusCleared}, cleared={cleared}; " +
            $"save future={(divergence is null ? "identical" : divergence)}; drift={drift.Stone}");
        return passed;
    }

    private static bool SurplusLeavesUpgradedWall()
    {
        var world = new SimulationWorld();
        var store = world.AddNode(NodeKind.Granary, new Vector2(-7f, 0f), capacity: 2000);
        var wall = world.AddNode(NodeKind.PalisadeWall, new Vector2(7f, 0f), capacity: 0);
        world.SeedStock(store, Resource.Stone, 300);
        if (!world.BeginUpgrade(wall, NodeKind.StoneWall)) return false;
        var cost = StructuralProjects.CostFor(in world.Nodes.Get(wall)).Stone;
        const int surplus = 13;
        world.Nodes.Get(store).Stock.Add(Resource.Stone, -(cost + surplus));
        world.Nodes.Get(wall).Stock.Add(Resource.Stone, cost + surplus);
        var builder = world.SpawnAgent(new Vector2(4f, 0f), UnitType.Villager);
        ref readonly var project = ref world.Nodes.Get(wall);
        world.QueueAssign(
            new[] { builder },
            Assignment.Hold(project.Position, EconomySystem.WorkShiftSeconds, project.FootprintRadius));

        var drift = default(ResourceTotals);
        for (var tick = 0; tick < EconomyTicks(900f); tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero) break;
            if (world.Nodes.Get(wall).Kind == NodeKind.StoneWall &&
                world.Nodes.Get(wall).Stock.Stone == 0 &&
                world.Nodes.Get(store).Stock.Stone == 300 - cost &&
                world.Agents.Get(builder).Jobs.Assignment.Kind == AssignmentKind.None)
            {
                break;
            }
        }

        return world.Nodes.Get(wall).Kind == NodeKind.StoneWall &&
               world.Nodes.Get(wall).Stock.Stone == 0 &&
               world.Nodes.Get(store).Stock.Stone == 300 - cost &&
               world.Agents.Get(builder).Jobs.CarriedUnits == 0 && drift.IsZero;
    }

    /// <summary>The barracks is built through the ordinary project seam, then converts rather than spawns.</summary>
    private static bool ABarracksTrainsTheSameVillager()
    {
        var world = new SimulationWorld(100f);
        var store = world.AddNode(NodeKind.Granary, new Vector2(-20f, 0f), capacity: 5000);
        var barracks = world.AddNode(NodeKind.Barracks, new Vector2(20f, 0f), capacity: 0, built: false);
        var construction = Construction.CostFor(NodeKind.Barracks);
        var equipment = MilitiaTraining.Cost;
        const int traineeCount = 2;
        world.SeedStock(store, Resource.Wood, construction.Wood + traineeCount * equipment.Wood);
        world.SeedStock(store, Resource.Stone, construction.Stone + traineeCount * equipment.Stone);

        var builders = new AgentId[4];
        for (var i = 0; i < builders.Length; i++)
        {
            builders[i] = world.SpawnAgent(new Vector2(-12f, -3f + i * 2f), UnitType.Villager);
        }

        ref readonly var site = ref world.Nodes.Get(barracks);
        world.QueueAssign(
            builders,
            Assignment.Hold(site.Position, EconomySystem.WorkShiftSeconds, site.FootprintRadius));

        var carriedBoth = false;
        var drift = default(ResourceTotals);
        for (var tick = 0; tick < EconomyTicks(1200f); tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var wood = false;
            var stone = false;
            foreach (var id in builders)
            {
                ref readonly var body = ref world.Agents.Get(id);
                wood |= body.Jobs.CarriedUnits > 0 && body.Jobs.Carrying == Resource.Wood;
                stone |= body.Jobs.CarriedUnits > 0 && body.Jobs.Carrying == Resource.Stone;
            }
            carriedBoth |= wood && stone;
            drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero || world.Nodes.Get(barracks).IsBuilt &&
                builders.All(id => world.Agents.Get(id).Jobs.Assignment.Kind == AssignmentKind.None))
            {
                break;
            }
        }

        var trainees = builders.Take(traineeCount).ToArray();
        var trainee = trainees[0];
        var liveBefore = world.Agents.LiveCount;
        world.QueueTrainMilitia(trainees, barracks);
        var fetchedEquipment = false;
        var savedInProgress = false;
        string? divergence = null;
        for (var tick = 0; tick < EconomyTicks(300f); tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            foreach (var id in trainees)
            {
                ref readonly var traineeBody = ref world.Agents.Get(id);
                fetchedEquipment |= traineeBody.Jobs.CarriedUnits > 0 &&
                                    traineeBody.Jobs.Carrying is Resource.Wood or Resource.Stone;
            }

            ref readonly var body = ref world.Agents.Get(trainee);
            if (!savedInProgress && trainees.Any(id =>
                    world.Agents.Get(id).Jobs.TrainingWork > 0f &&
                    world.Agents.Get(id).Jobs.TrainingWork < MilitiaTraining.Seconds))
            {
                var loaded = WorldSave.RoundTrip(world);
                divergence = DeterminismCheck.Diverges(world, loaded, ticks: 300);
                savedInProgress = true;
                if (divergence is not null) break;
            }

            drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero || trainees.All(id => world.Agents.Get(id).Role == AgentRole.Militia)) break;
        }

        var sameBody = world.Agents.LiveCount == liveBefore && trainees.All(id =>
            world.Agents.Contains(id) && world.Agents.Get(id).Id == id);
        var militaryFrame = trainees.All(id =>
        {
            ref readonly var militia = ref world.Agents.Get(id);
            return militia.Role == AgentRole.Militia &&
                   militia.CarryCapacity == UnitType.Militia.CarryCapacity &&
                   militia.Appetite == UnitType.Militia.Appetite &&
                   militia.Strength == UnitType.Militia.Strength &&
                   militia.Health == UnitType.Militia.Health;
        });
        var exact = world.Economy.Consumed.Wood == construction.Wood + traineeCount * equipment.Wood &&
                    world.Economy.Consumed.Stone == construction.Stone + traineeCount * equipment.Stone;

        // A militia body may still receive movement orders, but it cannot silently return to the economy.
        var field = world.AddNode(NodeKind.Farm, Vector2.Zero, capacity: 150, Resource.Grain);
        ref readonly var work = ref world.Nodes.Get(field);
        world.QueueAssign(
            new[] { trainee },
            Assignment.Hold(work.Position, EconomySystem.WorkShiftSeconds, work.FootprintRadius));
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        // A soldier may be posted on that ground as a garrison, but does not acquire its civilian work loop
        // and is not counted as a hand merely because the post touches a field.
        var leftWorkforce = world.Agents.Get(trainee).Jobs.Assignment.Kind != AssignmentKind.Work &&
                            world.Nodes.Get(field).Hands == 0;
        var completedSave = WorldSave.RoundTrip(world);
        var rolePersists = trainees.All(id => completedSave.Agents.Get(id).Role == AgentRole.Militia);
        drift = world.Economy.Discrepancy(world.Nodes, world.Agents);

        var passed = world.Nodes.Get(barracks).IsBuilt && carriedBoth && fetchedEquipment &&
                     savedInProgress && divergence is null && sameBody && militaryFrame && exact &&
                     leftWorkforce && rolePersists && drift.IsZero;
        Console.WriteLine(
            $"    barracks built={world.Nodes.Get(barracks).IsBuilt}, construction carried wood/stone={carriedBoth}; " +
            $"{traineeCount} trainees fetched equipment={fetchedEquipment}, same ids/headcount={sameBody}, militia frame={militaryFrame}; " +
            $"left workforce={leftWorkforce}, exact material sink={exact}; " +
            $"in-progress save future={(divergence is null ? "identical" : divergence)}, " +
            $"completed role saved={rolePersists}; drift={drift.Wood}/{drift.Stone}");
        return passed;
    }

    /// <summary>
    /// Growth needs three things, and each of them is something the player built.
    /// </summary>
    /// <remarks>
    /// Room in a house, a store in reach of that house, and enough put by to see the extra mouth through a
    /// winter. So this checks all three by taking them away one at a time on one map: a full house grows
    /// nobody, a house outside every catchment grows nobody however rich the settlement is, and an empty
    /// larder grows nobody however much housing there is.
    /// <para>
    /// The last one is a rate rather than a gate, which is the part worth asserting: a settlement with half
    /// a winter put by grows at half speed, so there is no cliff to farm up to the edge of.
    /// </para>
    /// </remarks>
    private static bool PeopleArriveWhenThereIsRoomAndFood()
    {
        var world = new SimulationWorld(240f);
        var granary = world.AddNode(NodeKind.Granary, Vector2.Zero, capacity: 9000);
        world.SeedStock(granary, Resource.Grain, 6000);
        world.SeedStock(granary, Resource.Wood, 6000);

        // Three houses: one with room in the catchment, one already full, one far outside every catchment.
        var roomy = world.AddNode(NodeKind.House, new Vector2(10f, 0f), capacity: 0, occupancy: 4);
        var full = world.AddNode(NodeKind.House, new Vector2(-10f, 0f), capacity: 0, occupancy: 1);
        var stranded = world.AddNode(NodeKind.House, new Vector2(0f, 108f), capacity: 0, occupancy: 4);
        world.SpawnAgent(new Vector2(-10f, 4f), UnitType.Villager);
        world.SpawnAgent(new Vector2(0f, 104f), UnitType.Villager);

        var started = world.Agents.LiveCount;
        Tick(world, (int)(30 * WorldCalendar.YearSeconds));

        var readiness = world.Economy.Readiness;
        var grewRoomy = world.Nodes.Get(roomy).Occupants > 0;
        var fullStayedFull = world.Nodes.Get(full).Occupants <= 1;
        var strandedGrewNobody = world.Nodes.Get(stranded).Occupants <= 1 &&
                                 !world.Nodes.Get(stranded).Supply.IsValid;
        var born = world.Economy.Born;
        var drift = world.Economy.Discrepancy(world.Nodes, world.Agents);

        // The same map with an empty larder, to show the brake is food and not housing. A second world
        // rather than emptying the first one's granary: writing a node's stock to zero destroys units
        // outside the ledger, and the drift check catches it — correctly, because a test that breaks
        // conservation to make a point has stopped testing the thing it was about.
        var (poorBorn, poorReadiness) = GrowthWithoutFood();
        var stoppedWhenEmpty = poorBorn == 0 && poorReadiness <= 0.001f;

        var passed = born > 0 && grewRoomy && fullStayedFull && strandedGrewNobody &&
                     readiness > 0.9f && stoppedWhenEmpty && drift.IsZero;
        Console.WriteLine(
            $"    a year at {readiness * 100f:F0}% readiness: {started} -> {world.Agents.LiveCount} people, " +
            $"{born} born into the house with room; full house stayed full={fullStayedFull}, " +
            $"house outside every catchment grew nobody={strandedGrewNobody}; the same map with an empty " +
            $"larder bore {poorBorn} at {poorReadiness * 100f:F0}% readiness; drift {drift.Grain}/{drift.Wood}");
        return passed;
    }

    /// <summary>
    /// A full larder next door is not your larder: readiness is per faction.
    /// </summary>
    /// <remarks>
    /// §138. Two settlements on one map, one stocked and one empty. Before the fix this test could not have
    /// failed the right way round: readiness summed every store and every appetite on the map, so the empty
    /// faction bore children out of its neighbour's granary and the stocked one was slowed by mouths it did
    /// not feed. It asserts the two figures <b>differ</b>, which is the thing a single world figure cannot do.
    /// </remarks>
    private static bool NeighbourLarderIsNotYours()
    {
        var world = new SimulationWorld(600f);
        var rich = new FactionId(0);
        var poor = new FactionId(1);

        // Far enough apart that neither granary is in the other's catchment, so the only thing that could
        // couple them is the readiness figure itself.
        var richGranary = world.AddNode(NodeKind.Granary, new Vector2(-200f, 0f), capacity: 9000, faction: rich);
        world.AddNode(NodeKind.House, new Vector2(-190f, 0f), capacity: 0, occupancy: 4, faction: rich);
        world.SeedStock(richGranary, Resource.Grain, 6000);
        world.SeedStock(richGranary, Resource.Wood, 6000);

        world.AddNode(NodeKind.Granary, new Vector2(200f, 0f), capacity: 9000, faction: poor);
        world.AddNode(NodeKind.House, new Vector2(190f, 0f), capacity: 0, occupancy: 4, faction: poor);

        world.SpawnAgent(new Vector2(-190f, 5f), UnitType.Villager, rich);
        world.SpawnAgent(new Vector2(190f, 5f), UnitType.Villager, poor);

        var before = (Rich: PeopleOf(world, rich), Poor: PeopleOf(world, poor));
        Tick(world, (int)(30 * WorldCalendar.YearSeconds));
        var after = (Rich: PeopleOf(world, rich), Poor: PeopleOf(world, poor));

        var richReadiness = world.Economy.ReadinessOf(rich);
        var poorReadiness = world.Economy.ReadinessOf(poor);
        var passed = richReadiness > 0.9f && poorReadiness <= 0.001f && after.Rich > before.Rich &&
                     after.Poor <= before.Poor;
        Console.WriteLine(
            $"    stocked neighbour at {richReadiness * 100f:F0}% grew {before.Rich}->{after.Rich}; " +
            $"empty one at {poorReadiness * 100f:F0}% grew {before.Poor}->{after.Poor} " +
            $"(one world figure would have fed both)");
        return passed;
    }

    /// <summary>
    /// A site is not a site if nobody can stand on it: founding refuses ground navigation calls solid.
    /// </summary>
    /// <remarks>
    /// §139. The gate it exercises had no test when it was written, which is the whole complaint this project
    /// makes about thresholds. It asserts the choice <b>moves</b> when the ground it settled on becomes
    /// forest — a scorer that ignores passability returns the same point both times, so the test can only
    /// pass because the gate is there.
    /// </remarks>
    private static bool AVillageIsNotFoundedInAForest()
    {
        const float extent = 600f;
        var world = new SimulationWorld(extent);
        world.Terrain.SetRegion(Region.Downland);
        var layout = MapLayout.Composed(Archetype.YValley, extent, 7u, 40f);
        ReliefPlan.FromLayout(layout, extent, 7u).Apply(world.Terrain);
        SettlementScenarios.PaintCountry(world);
        world.RebuildTerrainNavigation();

        var chosen = SettlementScenarios.ChooseSite(world, extent);
        var openBefore = world.Terrain.IsPassable(chosen);

        // Forest over the ground it chose, wide enough that no part of the keep-out square escapes.
        var step = world.Terrain.Transform.CellSize;
        for (var z = -40f; z <= 40f; z += step)
        for (var x = -40f; x <= 40f; x += step)
        {
            if (world.Terrain.Transform.TryWorldToCell(chosen + new Vector2(x, z), out var cell))
            {
                world.Terrain.SetSurface(cell, TerrainSurface.Forest);
            }
        }

        world.RebuildTerrainNavigation();
        var again = SettlementScenarios.ChooseSite(world, extent);
        var moved = Vector2.Distance(again, chosen) > 16f;
        var landedOpen = world.Terrain.IsPassable(again);

        var passed = openBefore && moved && landedOpen;
        Console.WriteLine(
            $"    chose ({chosen.X:F0},{chosen.Y:F0}) on open ground={openBefore}; with that ground under " +
            $"forest it chose ({again.X:F0},{again.Y:F0}), {Vector2.Distance(again, chosen):F0} m away " +
            $"and walkable={landedOpen}");
        return passed;
    }

    /// <summary>
    /// The bot's idea of what a stone wall costs is what a stone wall costs.
    /// </summary>
    /// <remarks>
    /// §140. The bot has to know a project's price <em>before</em> it commits, and StructuralProjects only
    /// answers for a node already upgrading — so the figure is duplicated, and a duplicated constant that
    /// nothing checks is a constant that drifts. This is the check.
    /// </remarks>
    private static bool TheBotKnowsWhatAWallCosts()
    {
        var world = new SimulationWorld(120f);
        var wall = world.AddNode(NodeKind.PalisadeWall, new Vector2(10f, 0f), capacity: 0);
        var began = world.BeginUpgrade(wall, NodeKind.StoneWall);
        var real = StructuralProjects.CostFor(world.Nodes.Get(wall)).Stone;
        var bots = AI.SettlementBot.StoneForAStoneWall;
        var passed = began && real > 0 && real == bots;
        Console.WriteLine(
            $"    a palisade turns to stone for {real} stone; the bot budgets {bots}");
        return passed;
    }

    private static int PeopleOf(SimulationWorld world, FactionId faction)
    {
        var total = 0;
        foreach (ref readonly var body in world.Agents.All)
        {
            if (body.IsAlive && body.Faction == faction) total++;
        }

        return total;
    }

    /// <summary>The same arrangement with nothing in the granary: housing alone grows nobody.</summary>
    private static (long Born, float Readiness) GrowthWithoutFood()
    {
        var world = new SimulationWorld(240f);
        world.AddNode(NodeKind.Granary, Vector2.Zero, capacity: 9000);
        world.AddNode(NodeKind.House, new Vector2(10f, 0f), capacity: 0, occupancy: 4);
        world.SpawnAgent(new Vector2(10f, 5f), UnitType.Villager);
        Tick(world, (int)(30 * WorldCalendar.YearSeconds));
        return (world.Economy.Born, world.Economy.Readiness);
    }

    /// <summary>
    /// A shortage costs people, and the cost is reversible.
    /// </summary>
    /// <remarks>
    /// Privation is a <em>state</em> and not an event: the household is going without for as long as the
    /// store it draws from is empty and it wants something, which is every tick of a famine. Measuring it
    /// on the ticks a whole unit of demand happened to come due counted one tick in a hundred, so a
    /// settlement whose wood ran out for a year accrued a minute of privation and nobody ever left.
    /// <para>
    /// And it drains faster than it fills, so a settlement that fixes its supply stops losing people
    /// rather than going on losing them for as long as the shortage lasted.
    /// </para>
    /// </remarks>
    private static bool PrivationSpendsItselfAsEmigration()
    {
        var world = new SimulationWorld(240f);
        var granary = world.AddNode(NodeKind.Granary, Vector2.Zero, capacity: 9000);
        var house = world.AddNode(NodeKind.House, new Vector2(9f, 0f), capacity: 0, occupancy: 4);
        for (var i = 0; i < 4; i++) world.SpawnAgent(new Vector2(9f, 5f + i), UnitType.Villager);
        var started = world.Agents.LiveCount;

        // An empty granary: they are in a catchment and there is nothing in it.
        Tick(world, (int)(30 * Population.PrivationSeconds * 1.1f));
        var lost = world.Economy.Emigrated;
        var privationRose = lost > 0;

        // Fill it, and the remaining households stop leaving.
        world.SeedStock(granary, Resource.Grain, 9000);
        world.SeedStock(granary, Resource.Wood, 9000);
        Tick(world, EconomyTicks(60f));
        var recovered = world.Nodes.Get(house).Privation <= 0.001f;
        var afterFilling = world.Economy.Emigrated;
        Tick(world, (int)(30 * Population.PrivationSeconds * 1.1f));
        var stayedPut = world.Economy.Emigrated == afterFilling;

        var drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
        var passed = privationRose && recovered && stayedPut && world.Agents.LiveCount < started &&
                     drift.IsZero;
        Console.WriteLine(
            $"    a season of empty stores: {started} -> {world.Agents.LiveCount} people, {lost} left; " +
            $"privation cleared once the granary was filled={recovered}, and nobody else left after=" +
            $"{stayedPut}; drift {drift.Grain}/{drift.Wood}");
        return passed;
    }

    /// <summary>
    /// The second half of forest cover: a wood conceals, and the gaps in it are the only sightlines.
    /// </summary>
    /// <remarks>
    /// Blocking movement gave a settlement constrained approach routes; this is what makes those routes
    /// worth constraining. The same body at the same range is seen across open ground and not seen through
    /// trees, so the wood a settlement has been cutting into is both the way in and the only way it can
    /// watch — and the value of the clearing it has made is that it can see across it.
    /// </remarks>
    /// <summary>
    /// A raider at a full granary, and a queue of villagers at increasing distance from it.
    /// </summary>
    /// <remarks>
    /// <b>The thing Stage E shipped without.</b> Everything the defence got wrong was visible in a number
    /// and invisible on screen, and the sharpest of them was that question two asked "can we take them"
    /// rather than "am I needed" — so twenty-six people answered one alarm, arrived one at a time, and were
    /// killed one at a time while the rest of the raid emptied the stores. What this pins down:
    /// <list type="bullet">
    /// <item><b>Enough go.</b> The committed strength covers the assailants with the margin, so the
    /// settlement is not sending a defence it knows will lose.</item>
    /// <item><b>No more than enough go.</b> Given a strength ratio, a fixed number is wanted; everybody
    /// else is surplus and says so. That is the answer that did not exist before.</item>
    /// <item><b>The nearest go.</b> Not an arbitrary subset — the ones who would arrive first, which is
    /// both the ones who arrive in time and the ones whose fields are least disrupted by going.</item>
    /// </list>
    /// </remarks>
    private static bool OnlyTheNeededDefend()
    {
        var world = new SimulationWorld(240f);
        var granary = world.AddNode(NodeKind.Granary, Vector2.Zero, capacity: 2000);
        world.SeedStock(granary, Resource.Grain, 900);

        // Twenty villagers in a line running away from the granary, a metre and a half apart, all of them
        // inside sight of it and inside the rally window.
        var line = new List<AgentId>();
        for (var i = 0; i < 20; i++)
        {
            line.Add(world.SpawnAgent(new Vector2(4f + i * 1.5f, 0f), UnitType.Villager));
        }

        // One hostile at the granary door.
        var raider = world.SpawnAgent(new Vector2(2.2f, 0f), UnitType.Raider, new FactionId(1));
        world.Agents.Get(raider).Directed = true;
        var menace = world.Agents.Get(raider).Strength;
        Tick(world, 2);

        var standing = new List<int>();
        for (var i = 0; i < line.Count; i++)
        {
            if (world.Agents.Get(line[i]).Standing) standing.Add(i);
        }

        var committed = 0f;
        foreach (var i in standing) committed += world.Agents.Get(line[i]).Strength;

        // Enough, and the margin is respected.
        var wanted = menace * Simulation.Threat.ThreatSystem.StandMargin;
        var enough = committed >= wanted;

        // Not everybody. One villager's strength short of the requirement would not have been enough, so
        // this is the tightest the prefix can be without under-committing.
        var lean = standing.Count < line.Count &&
                   committed - world.Agents.Get(line[standing[^1]]).Strength < wanted;

        // And it is a prefix of the line, which is what "the nearest ones go" means when the line is
        // ordered by distance.
        var nearestWent = true;
        for (var i = 0; i < standing.Count; i++)
        {
            if (standing[i] != i) nearestWent = false;
        }

        var spare = world.Threat.Surplus;
        var passed = enough && lean && nearestWent && spare > 0;
        Console.WriteLine(
            $"    one raider of strength {menace:F0} wants {wanted:F1}: {standing.Count} of {line.Count} " +
            $"stood for {committed:F0}, a prefix of the line={nearestWent}, one fewer would not do={lean}, " +
            $"{spare} left it to somebody closer");
        return passed;
    }

    /// <summary>
    /// A loaded villager decides to defend, and does something with the load first.
    /// </summary>
    /// <remarks>
    /// <b>Rushing to a fight with your hands full loses the fight and the goods in one move</b> — the load
    /// is the prize, and carrying it into the raider's reach hands it over without the raider having to
    /// walk anywhere. Both branches of the answer are pinned here, because the interesting one is the
    /// second:
    /// <list type="bullet">
    /// <item><b>A store out of the trouble</b> gets the load, and the units are in it rather than
    /// anywhere else — the ledger is watched across the whole thing.</item>
    /// <item><b>With nowhere safe to put it, the ground.</b> A heap in the open can be looted and that is
    /// the honest cost; what must not happen is a body walking into a fight still holding it, or the
    /// units quietly ceasing to exist.</item>
    /// </list>
    /// And in neither case does the shift change: an interrupt that rewrote an assignment to borrow the
    /// haul machinery would break the jobs model's one prohibition.
    /// </remarks>
    private static bool HandsAreFreedBeforeAFight()
    {
        // A raided granary, and a depot far enough away to be out of the trouble.
        var world = new SimulationWorld(240f);
        var granary = world.AddNode(NodeKind.Granary, Vector2.Zero, capacity: 2000);
        world.SeedStock(granary, Resource.Grain, 600);
        var depot = world.AddNode(NodeKind.ForwardDepot, new Vector2(0f, 26f), capacity: 500);

        var carrier = world.SpawnAgent(new Vector2(6f, 8f), UnitType.Villager);
        var shift = world.Agents.Get(carrier).Jobs.Assignment;
        world.SeedStock(granary, Resource.Grain, 0);
        // Forty grain on its back, taken out of the granary so the books balance.
        const int load = 40;
        ref var body = ref world.Agents.Get(carrier);
        world.Nodes.Get(granary).Stock.Add(Resource.Grain, -load);
        body.Jobs.Carrying = Resource.Grain;
        body.Jobs.CarriedUnits = load;

        // Enough neighbours that the fight is worth joining, and one raider at the granary door.
        for (var i = 0; i < 8; i++) world.SpawnAgent(new Vector2(3f + i * 1.2f, 2f), UnitType.Villager);
        var raider = world.SpawnAgent(new Vector2(2.2f, 0f), UnitType.Raider, new FactionId(1));
        world.Agents.Get(raider).Directed = true;

        var carriedIntoTheFight = false;
        var stowed = false;
        for (var tick = 0; tick < 30 * 40 && !stowed; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            ref readonly var it = ref world.Agents.Get(carrier);
            if (!it.IsAlive) break;
            if (it.Jobs.CarriedUnits <= 0) stowed = true;
            // Never within a raider's reach with its hands still full.
            else if (Vector2.Distance(it.Position, world.Agents.Get(raider).Position) < 2f)
            {
                carriedIntoTheFight = true;
            }
        }

        var intoTheDepot = world.Nodes.Get(depot).Stock.Grain;
        var drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
        var shiftIntact = world.Agents.Get(carrier).Jobs.Assignment.Kind == shift.Kind;

        // Now the same thing with nowhere to put it: the only store is the one being raided.
        var bare = new SimulationWorld(240f);
        var only = bare.AddNode(NodeKind.Granary, Vector2.Zero, capacity: 2000);
        bare.SeedStock(only, Resource.Grain, 600);
        var stuck = bare.SpawnAgent(new Vector2(6f, 8f), UnitType.Villager);
        ref var held = ref bare.Agents.Get(stuck);
        bare.Nodes.Get(only).Stock.Add(Resource.Grain, -load);
        held.Jobs.Carrying = Resource.Grain;
        held.Jobs.CarriedUnits = load;
        for (var i = 0; i < 8; i++) bare.SpawnAgent(new Vector2(3f + i * 1.2f, 2f), UnitType.Villager);
        var thief = bare.SpawnAgent(new Vector2(2.2f, 0f), UnitType.Raider, new FactionId(1));
        bare.Agents.Get(thief).Directed = true;
        for (var tick = 0; tick < 30 * 10; tick++)
        {
            bare.Tick((float)SimulationWorld.FixedDeltaSeconds);
            if (bare.Agents.Get(stuck).Jobs.CarriedUnits <= 0) break;
        }

        var onTheGround = 0;
        foreach (ref readonly var node in bare.Nodes.All)
        {
            if (node.IsAlive && node.IsPile) onTheGround += node.Stock.Grain;
        }

        var bareDrift = bare.Economy.Discrepancy(bare.Nodes, bare.Agents);
        var passed = stowed && !carriedIntoTheFight && intoTheDepot == load && shiftIntact &&
                     drift.Grain == 0 && onTheGround == load && bareDrift.Grain == 0;
        Console.WriteLine(
            $"    {load} grain and a raid at the granary: stowed={stowed}, into the depot " +
            $"{intoTheDepot}, never carried into reach={!carriedIntoTheFight}, shift intact={shiftIntact}; " +
            $"with nowhere safe, {onTheGround} left on the ground; drift {drift.Grain}/{bareDrift.Grain}");
        return passed;
    }

    /// <summary>
    /// Twelve bodies pressed onto one, and only the six that fit do any harm.
    /// </summary>
    /// <remarks>
    /// <b>The front, and it is a derivation rather than a dial.</b> A body of radius <c>a</c> in contact
    /// with one of radius <c>t</c> stands on a ring of radius <c>t + a</c> and takes up
    /// <c>2·asin(a / (t + a))</c> of it, so for two 0.37 m bodies exactly six fit and the seventh onwards
    /// is standing behind somebody. Without it a fight was decided by count alone — twelve villagers on one
    /// raider did twelve villagers' worth of damage — and no unit could ever be worth more than a warm
    /// body, which is the same thing as saying there is no reason to build a soldier.
    /// <para>
    /// Measured against the arithmetic rather than against a remembered number: the harm taken in one
    /// second has to be the six nearest strengths and not the twelve present ones.
    /// </para>
    /// </remarks>
    private static bool AFightHasAFront()
    {
        var world = new SimulationWorld(120f);
        var victim = world.SpawnAgent(Vector2.Zero, UnitType.Raider, new FactionId(1));
        world.Agents.Get(victim).Directed = true;
        var health = world.Agents.Get(victim).Health;

        // Twelve villagers packed onto it, close enough that every one of them is in reach.
        var ring = UnitType.Raider.Radius + UnitType.Villager.Radius;
        var mob = new List<AgentId>();
        for (var i = 0; i < 12; i++)
        {
            var angle = i / 12f * MathF.Tau;
            mob.Add(world.SpawnAgent(
                new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * ring * 1.05f,
                UnitType.Villager,
                new FactionId(0)));
        }

        // How many actually fit, from the geometry and nothing else.
        var fit = (int)MathF.Floor(
            MathF.Tau / Simulation.Threat.ThreatSystem.ContactArc(
                UnitType.Raider.Radius, UnitType.Villager.Radius));

        // One second of it, applied a tick at a time.
        var reached = 0;
        for (var tick = 0; tick < 30; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            reached = Math.Max(reached, world.Threat.Crowded);
        }

        var taken = health - world.Agents.Get(victim).Health;
        var byTheSix = fit * UnitType.Villager.Strength;
        var byTheTwelve = mob.Count * UnitType.Villager.Strength;

        // <b>A ceiling, not an equality, and that distinction is the assertion.</b> The arithmetic says six
        // villagers do six a second; the twelve are also shoving each other, so several drift out of reach
        // and the real figure sits below the cap — measured, about half of it. Asserting the sum would be
        // asserting that depenetration does nothing, which is both false and not what this is about. What
        // has to hold is that the cap binds and that count alone no longer decides the fight.
        // <b>A ceiling and a floor of "something happened", and no more than that.</b> The absolute figure
        // is not a property of the cap: it depends on how many of the ring are momentarily inside reach,
        // which is depenetration jitter against a reach of 0.81 m — when the reach floor of §33 was retired
        // this fell from 3.2 to 1.1 without the cap changing at all, and an assertion that moved with it
        // was measuring the wrong thing. What has to hold is that the cap binds, that count alone does not
        // decide the fight, and that a fight happens.
        var front = taken <= byTheSix * 1.05f && taken < byTheTwelve * 0.9f && taken > 0f;
        var shutOut = reached > 0;
        var passed = front && shutOut && fit == 6;
        Console.WriteLine(
            $"    12 villagers on one raider: {fit} fit by geometry, {reached} shut out at once; " +
            $"one second took {taken:F1} health, at or under the {byTheSix:F0} the six that fit could do " +
            $"and well under the {byTheTwelve:F0} all twelve would have");
        return passed;
    }

    private static bool TreesBlockSight()
    {
        var world = new SimulationWorld(240f);
        var watcher = world.SpawnAgent(Vector2.Zero, UnitType.Villager);
        var sight = world.Agents.Get(watcher).SightMetres;

        // A target well inside sight, and open ground between.
        var target = new Vector2(sight * 0.7f, 0f);
        var seenAcrossOpenGround = world.CanSee(in world.Agents.Get(watcher), target);
        var beyondSight = !world.CanSee(in world.Agents.Get(watcher), new Vector2(sight * 1.4f, 0f));

        // Now put a stand of trees between them, dense enough to close the ground.
        for (var i = 0; i < 40; i++)
        {
            var at = new Vector2(sight * 0.35f + i % 4 * 1.2f, (i / 4) * 1.2f - 5.4f);
            var tree = world.AddNode(NodeKind.Tree, at, capacity: (int)Woodland.WoodPerTree);
            world.SeedStock(tree, Resource.Wood, (int)Woodland.WoodPerTree);
        }

        world.RefreshForestCover();
        world.RebuildTerrainNavigation();
        var hiddenByTrees = !world.CanSee(in world.Agents.Get(watcher), target);

        // And a target the same distance away, but reached across the clearing beside the wood.
        var pastTheWood = new Vector2(0f, sight * 0.7f);
        var seenThroughTheGap = world.CanSee(in world.Agents.Get(watcher), pastTheWood);

        var passed = seenAcrossOpenGround && beyondSight && hiddenByTrees && seenThroughTheGap;
        Console.WriteLine(
            $"    sight {sight:F0} m: open ground at {sight * 0.7f:F0} m seen={seenAcrossOpenGround}, " +
            $"beyond sight refused={beyondSight}, same range through a wood seen={!hiddenByTrees}, " +
            $"through the clearing beside it seen={seenThroughTheGap}");
        return passed;
    }

    private static void Tick(SimulationWorld world, int count)
    {
        for (var i = 0; i < count; i++) world.Tick((float)SimulationWorld.FixedDeltaSeconds);
    }

    /// <summary>
    /// A gap of this many navigation cells admits a standard body and refuses a heavy one.
    /// </summary>
    /// <remarks>
    /// Three cells is 1.5 m, whose best cell-centre clearance is 0.75 m, which is exactly the width
    /// one placement cell leaves between two built walls. See §3 — the same threshold does duty for
    /// accidental wall gaps, deliberate gates and terrain clearings, and it is the only one the
    /// half-metre raster has between "everybody" and "nobody".
    /// </remarks>
    private const int FootGapCells = 3;

    /// <summary>Six cells, 3 m: the gate width, which admits everything.</summary>
    private const int GateGapCells = 6;

    /// <summary>
    /// Two bodies of the heavy class must begin avoiding each other before they are in contact.
    /// </summary>
    /// <remarks>
    /// The neighbour horizon used to be a flat 1.52 m between centres, which is less than two heavy
    /// bodies measure across the pair — so they never entered each other's neighbour list and the
    /// first either knew of the other was the position solver pushing them apart. Nothing overlapped,
    /// because contact resolution caught it, which is why no existing test saw this: the failure is
    /// that avoidance had become collision response. Measured in combined radii so it says the same
    /// thing at any body size, and asserted above 1.0, which is the moment of touching.
    /// </remarks>
    private static bool LargeBodiesAvoidBeforeContact()
    {
        var world = new SimulationWorld();
        var combined = UnitType.HaulerCart.Radius * 2f;
        var offset = combined * 0.33f;
        var left = world.SpawnAgent(new Vector2(-6f, offset * 0.5f), radius: UnitType.HaulerCart.Radius);
        var right = world.SpawnAgent(new Vector2(6f, -offset * 0.5f), radius: UnitType.HaulerCart.Radius);
        world.QueueMove(new[] { left }, new Vector2(6f, offset * 0.5f));
        world.QueueMove(new[] { right }, new Vector2(-6f, -offset * 0.5f));

        var reaction = -1f;
        var closest = float.MaxValue;
        for (var tick = 0; tick < 300 * WalkingPace; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            ref readonly var first = ref world.Agents.Get(left);
            ref readonly var second = ref world.Agents.Get(right);
            var distance = Vector2.Distance(first.Position, second.Position);
            closest = MathF.Min(closest, distance);
            if (reaction < 0f &&
                (MathF.Abs(first.Velocity.Y) > 0.05f || MathF.Abs(second.Velocity.Y) > 0.05f))
            {
                reaction = distance;
            }
        }

        var reactionInBodies = reaction / combined;
        var passed = reaction > 0f && reactionInBodies > 1.10f && closest >= combined - 0.01f;
        if (!passed)
        {
            Console.WriteLine(
                $"    heavy pair: reaction={reactionInBodies:F2} combined radii (needs >1.10), " +
                $"closest={closest / combined:F3} (needs >=0.99)");
        }

        return passed;
    }

    /// <summary>
    /// A column of mixed sizes through a gate keeps every pair apart by its own combined radius.
    /// </summary>
    /// <remarks>
    /// Every other separation assertion in this file compares against
    /// <c>SeparationThreshold(AgentDefaults.Radius)</c>, which is one body doubled and therefore
    /// means nothing once two sizes share a crowd — it would pass a wagon standing inside a
    /// villager. This one is pairwise, which is the only form that survives a second body type.
    /// </remarks>
    private static bool MixedSizeCrowdIsSafe()
    {
        // Three arrangements, not one. A single mix is a sample: sweeping the heavy count and
        // whether the heavy bodies lead or trail moved the worst overlap around by a factor of ten
        // while the correction share was being measured, so a test that fixes one arrangement is
        // choosing the answer it gets. These three are the ones that produced the worst figures.
        var passed = true;
        foreach (var (heavies, heavyFirst) in new[] { (4, true), (6, false), (8, true) })
        {
            var world = new SimulationWorld();
            var placement = world.Placement.Transform;
            for (var z = 0; z < placement.Height; z++)
            {
                if (z is 10 or 11) continue;
                world.QueueToggleObstacle(placement.CellCenter(new GridCell(10, z)));
            }

            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var gate = placement.CellCenter(new GridCell(10, 10));

            var ids = new List<AgentId>();
            for (var i = 0; i < 12; i++)
            {
                var heavy = heavyFirst ? i < heavies : i >= 12 - heavies;
                ids.Add(world.SpawnAgent(
                    new Vector2(gate.X - 5f - i % 3 * 2.2f, gate.Y + (i / 3 - 1.5f) * 2.2f),
                    radius: heavy ? UnitType.HaulerCart.Radius : AgentDefaults.Radius));
            }

            world.QueueMove(ids, new Vector2(gate.X + 5f, gate.Y));

            var worstRatio = float.MaxValue;
            for (var tick = 0; tick < 800 * WalkingPace; tick++)
            {
                world.Tick((float)SimulationWorld.FixedDeltaSeconds);
                for (var i = 0; i < ids.Count; i++)
                for (var j = i + 1; j < ids.Count; j++)
                {
                    ref readonly var first = ref world.Agents.Get(ids[i]);
                    ref readonly var second = ref world.Agents.Get(ids[j]);
                    // Pairwise: a bar built from one radius doubled would pass a wagon standing
                    // inside a villager, which is the whole reason this test exists.
                    var bar = AgentDefaults.SeparationThreshold(first.Radius, second.Radius);
                    worstRatio = MathF.Min(
                        worstRatio,
                        Vector2.Distance(first.Position, second.Position) / bar);
                }
            }

            var arrived = ids.Count(id => world.Agents.Get(id).Position.X > gate.X + 2f);
            if (worstRatio >= 1f && arrived == ids.Count) continue;
            passed = false;
            Console.WriteLine(
                $"    mixed gate, {heavies} heavy {(heavyFirst ? "leading" : "trailing")}: " +
                $"worst separation {worstRatio:F3} of the pair's bar (needs >=1.000), " +
                $"arrived {arrived}/{ids.Count}");
        }

        return passed;
    }

    /// <summary>
    /// Two radii the raster cannot tell apart must produce the same decomposition, and two it can
    /// must not.
    /// </summary>
    /// <remarks>
    /// This is the guard on §3's radius classes. Clearance is one sample per cell taken at its
    /// centre and every obstacle face is on the half-metre lattice, so the clearances a map can
    /// hold are 0.250, 0.354, 0.750, 0.791, 1.061 — and every body between 0.319 m and 0.715 m sees
    /// exactly the same ground. That is what lets a villager, a soldier, a scout and a hauler cart
    /// share one decomposition instead of retaining four identical copies of it.
    /// <para>
    /// The second half of the assertion is what stops it being vacuous, and it is the one that will
    /// fail first: put an obstacle on the map whose faces are not on the lattice — a rotated
    /// building, a scattered tree with a real footprint — and the achievable clearances become
    /// dense, the rungs move, and the sharing argument stops holding. A failure here is not a bug
    /// in the decomposition, it is notice that §3 needs re-deriving.
    /// </para>
    /// </remarks>
    private static bool RadiusRungsShareADecomposition()
    {
        var world = BuildTwoGapWall(out _, out _, out _);

        var villager = world.DecomposeWalkable(AgentDefaults.Radius);
        var cart = world.DecomposeWalkable(UnitType.HaulerCart.Radius);
        var withinRung = world.DecomposeWalkable(0.65f);
        // Everything in the roster is inside one rung, so all of it has to share one decomposition —
        // which is the whole reason the router keeps one mesh for the whole world. The 0.65 probe is
        // there to pin the top of the rung rather than the top of the roster: a unit added at any width
        // up to it is still free, and one added above it is not.
        var shared = villager.CoveredCells == withinRung.CoveredCells &&
                     villager.Count == withinRung.Count &&
                     villager.Crossings.Count == withinRung.Crossings.Count &&
                     villager.CoveredCells == cart.CoveredCells &&
                     villager.Count == cart.Count;
        var beyondRung = world.DecomposeWalkable(0.80f).CoveredCells < villager.CoveredCells;

        if (!shared || !beyondRung)
        {
            Console.WriteLine(
                $"    rungs: villager {villager.CoveredCells} cells / {villager.Count} rects, " +
                $"cart {cart.CoveredCells} / {cart.Count}, 0.65 {withinRung.CoveredCells} / " +
                $"{withinRung.Count} (all three must match), 0.80 must be fewer");
        }

        return shared && beyondRung;
    }

    // "a heavy body takes the gate a villager can skip" stood here and is retired with the second body
    // class. It asserted §3's end-to-end claim — that a clearing a foot unit slips through is not a route
    // for a 0.90 m body, which has to use the gate — and there is no 0.90 m body any more. What retired
    // the class is worth keeping: once buildings occupied the ground they stand on, a body that wide could
    // not be routed to a point beside one, because the clearance beside a wall is 0.75. The two-gap wall
    // it was built on is still here and still used by the rung test above, so re-deriving a second class
    // has somewhere to start if a finer navigation cell ever makes one affordable.

    /// <summary>Walks one body across the two-gap wall and reports where it got through.</summary>
    private static float? WallCrossingOf(float radius)
    {
        var world = BuildTwoGapWall(out var wallX, out var start, out var goal);
        var id = world.SpawnAgent(start, radius: radius);
        world.QueueMove(new[] { id }, goal);

        for (var tick = 0; tick < 900 * WalkingPace; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            ref readonly var agent = ref world.Agents.Get(id);
            if (agent.Position.X > wallX + 0.5f) return agent.Position.Y;
        }

        return null;
    }

    /// <summary>
    /// One wall with two ways through it: a foot-only clearing, and a gate that admits anything.
    /// </summary>
    private static SimulationWorld BuildTwoGapWall(out float wallX, out Vector2 start, out Vector2 goal)
    {
        var world = new SimulationWorld();
        var grid = world.Terrain.Transform;
        var column = grid.Width / 2;
        var narrowFrom = grid.Height / 2 - FootGapCells / 2;
        var gateFrom = grid.Height / 2 + 10;

        for (var z = 0; z < grid.Height; z++)
        {
            var inNarrow = z >= narrowFrom && z < narrowFrom + FootGapCells;
            var inGate = z >= gateFrom && z < gateFrom + GateGapCells;
            if (inNarrow || inGate) continue;
            world.Terrain.SetSurface(new GridCell(column, z), TerrainSurface.Impassable);
        }

        world.RebuildTerrainNavigation();
        wallX = world.Navigation.CellCenter(new GridCell(column, 0)).X;
        var centreZ = world.Navigation.CellCenter(new GridCell(0, grid.Height / 2)).Y;
        start = new Vector2(wallX - 6f, centreZ);
        goal = new Vector2(wallX + 6f, centreZ);
        return world;
    }

    /// <summary>
    /// Every unit type's own radius and its class's must be the same body to the router.
    /// </summary>
    /// <remarks>
    /// The hauler cart is 0.55 m and routes on the 0.37 m field, which is exact rather than
    /// approximate only while the two sit inside one clearance rung. That is what lets six unit
    /// types share two decompositions instead of retaining a copy each. If a type is ever given a
    /// radius across a rung boundary — or the raster stops producing rungs at all — this is where
    /// it surfaces, and the answer is to move the type or to re-derive §3, never to widen the test.
    /// </remarks>
    private static bool UnitTypesRouteByClass()
    {
        var world = BuildTwoGapWall(out _, out _, out _);
        var passed = true;
        foreach (var type in UnitType.All)
        {
            var own = world.DecomposeWalkable(type.Radius);
            var byClass = world.DecomposeWalkable(type.NavigationRadius);
            if (own.CoveredCells == byClass.CoveredCells &&
                own.Count == byClass.Count &&
                own.Crossings.Count == byClass.Crossings.Count)
            {
                continue;
            }

            passed = false;
            Console.WriteLine(
                $"    {type.Name}: own radius {type.Radius:F2} gives {own.CoveredCells} cells / " +
                $"{own.Count} rects, class radius {type.NavigationRadius:F2} gives " +
                $"{byClass.CoveredCells} / {byClass.Count} — they must match");
        }

        return passed;
    }

    /// <summary>
    /// A body with a turning circle takes materially longer to reverse than one without.
    /// </summary>
    /// <remarks>
    /// Asserted as time to come about rather than as the radius of the arc, because the arc is not
    /// what the implementation delivers: a wagon told to reverse slows down first, and by the time
    /// it is turning hard it is doing 0.3 m/s and traces well inside its nominal circle. What is
    /// real, and what a player sees at range, is that it cannot flick round — 6.4 s against a
    /// villager's 1.3. The pivot floor is a slider precisely because the look of it is unsettled.
    /// </remarks>
    private static bool WagonComesAboutSlowly()
    {
        var villager = SecondsToComeAbout(UnitType.Villager);
        var wagon = SecondsToComeAbout(UnitType.HeavyCavalry);
        var passed = villager > 0f && wagon > 0f && wagon > villager * 3f;
        if (!passed)
        {
            Console.WriteLine(
                $"    coming about: villager {villager:F1}s, wagon {wagon:F1}s " +
                "(wagon must take over three times as long)");
        }

        return passed;
    }

    private static float SecondsToComeAbout(UnitType type)
    {
        var world = new SimulationWorld();
        var id = world.SpawnAgent(new Vector2(-13f, 0f), type);
        world.QueueMove(new[] { id }, new Vector2(13f, 0f));
        // Ordered while still travelling: from rest there is no turn to measure.
        for (var tick = 0; tick < 90; tick++) world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        world.QueueMove(new[] { id }, new Vector2(-13f, 0f));

        for (var tick = 0; tick < 600; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            if (world.Agents.Get(id).Velocity.X < -0.1f)
            {
                return (tick + 1) * (float)SimulationWorld.FixedDeltaSeconds;
            }
        }

        return -1f;
    }

    /// <summary>
    /// A scout covers open ground faster than a villager, by about the ratio of their speeds.
    /// </summary>
    /// <remarks>
    /// The speed differential is the whole of what a scout is, and it is the first thing a
    /// threshold written as an absolute speed would quietly eat — several were, and are now shares
    /// of a body's own top speed for exactly this reason.
    /// </remarks>
    private static bool ScoutOutpacesVillager()
    {
        var villager = SecondsToCross(UnitType.Villager);
        var scout = SecondsToCross(UnitType.LightCavalry);
        var expected = UnitType.Villager.MaximumSpeed / UnitType.LightCavalry.MaximumSpeed;
        var actual = scout / villager;
        // Generous, because acceleration and arrival braking are shared costs that do not scale.
        var passed = villager > 0f && scout > 0f && actual < expected * 1.25f && actual > expected * 0.75f;
        if (!passed)
        {
            Console.WriteLine(
                $"    crossing: villager {villager:F1}s, scout {scout:F1}s, ratio {actual:F2} " +
                $"(speeds imply {expected:F2})");
        }

        return passed;
    }

    private static float SecondsToCross(UnitType type)
    {
        var world = new SimulationWorld();
        var id = world.SpawnAgent(new Vector2(-12f, 0f), type);
        world.QueueMove(new[] { id }, new Vector2(12f, 0f));
        for (var tick = 0; tick < 900 * WalkingPace; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            if (!world.Agents.Get(id).HasDestination)
            {
                return (tick + 1) * (float)SimulationWorld.FixedDeltaSeconds;
            }
        }

        return -1f;
    }

    /// <summary>
    /// A faster pair must not get less warning than a slower one.
    /// </summary>
    /// <remarks>
    /// The neighbour lookahead is a distance and the argument for its size is a reaction time, so
    /// left unscaled it hands a fast unit a fraction of the warning it hands a slow one — two light
    /// cavalry closing at 7 m/s had <b>0.05 s</b>, a tick and a half. Exactly the failure the flat
    /// horizon produced for large bodies, arriving through speed instead of through size, which is
    /// why this is asserted rather than remembered.
    /// </remarks>
    private static bool WarningHoldsAcrossSpeeds()
    {
        var villager = WarningSeconds(UnitType.Villager.MaximumSpeed);
        var scout = WarningSeconds(UnitType.LightCavalry.MaximumSpeed);
        var passed = villager > 0f && scout > villager * 0.75f;
        if (!passed)
        {
            Console.WriteLine(
                $"    warning: villager pair {villager:F2}s, scout pair {scout:F2}s " +
                "(a faster pair must not get materially less)");
        }

        return passed;
    }

    /// <summary>Seconds of approach a pair at this speed has when the solve first turns them.</summary>
    private static float WarningSeconds(float speed)
    {
        var world = new SimulationWorld();
        var combined = AgentDefaults.Radius * 2f;
        var offset = combined * 0.33f;
        var left = world.SpawnAgent(
            new Vector2(-9f, offset * 0.5f), radius: AgentDefaults.Radius, maximumSpeed: speed);
        var right = world.SpawnAgent(
            new Vector2(9f, -offset * 0.5f), radius: AgentDefaults.Radius, maximumSpeed: speed);
        world.QueueMove(new[] { left }, new Vector2(9f, offset * 0.5f));
        world.QueueMove(new[] { right }, new Vector2(-9f, -offset * 0.5f));

        for (var tick = 0; tick < 1200; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            ref readonly var first = ref world.Agents.Get(left);
            ref readonly var second = ref world.Agents.Get(right);
            if (MathF.Abs(first.Velocity.Y) <= 0.05f && MathF.Abs(second.Velocity.Y) <= 0.05f) continue;
            var gap = Vector2.Distance(first.Position, second.Position) - combined;
            return gap / (speed * 2f);
        }

        return -1f;
    }

    /// <summary>
    /// A jam costs the same wall clock whoever is in it, so it is worth more of a fast body's
    /// journey than of a slow one's.
    /// </summary>
    /// <remarks>
    /// Asserted on the term rather than on a route. Which way a body actually goes at a jam is
    /// chaotic in the geometry — swept across one wall and four unit types, three runs in
    /// twenty-four changed at all — so a test pinned to one of those outcomes would be a coin toss
    /// with a comment on it. What is not chaotic, and what would silently stop being true if the
    /// scaling were removed or the reference pace drifted, is the ordering and the ratio.
    /// </remarks>
    private static bool CongestionIsPricedBySpeed()
    {
        var cart = PathService.CongestionSpeedScale(UnitType.HaulerCart.MaximumSpeed);
        var villager = PathService.CongestionSpeedScale(UnitType.Villager.MaximumSpeed);
        var soldier = PathService.CongestionSpeedScale(UnitType.Militia.MaximumSpeed);
        var scout = PathService.CongestionSpeedScale(UnitType.LightCavalry.MaximumSpeed);

        var ordered = cart < villager && villager < scout;
        // The reference body is the unit of the field, so it must price a queue at exactly one.
        var referenceIsOne = MathF.Abs(villager - 1f) < 0.001f;
        // A soldier is 5% off a villager and must share a field with one, or six unit types become
        // six cached fields for a difference nobody could see.
        var soldierShares = MathF.Abs(soldier - villager) < 0.001f;
        // Twice the pace, twice the share of the journey a fixed delay eats.
        var scoutIsProportionate = MathF.Abs(scout - UnitType.LightCavalry.MaximumSpeed / 1.79f) < 0.09f;

        var passed = ordered && referenceIsOne && soldierShares && scoutIsProportionate;
        if (!passed)
        {
            Console.WriteLine(
                $"    congestion by speed: cart {cart:F3}, villager {villager:F3}, " +
                $"soldier {soldier:F3}, scout {scout:F3} — must ascend, villager exactly 1, " +
                "soldier equal to villager, scout proportionate");
        }

        return passed;
    }

    /// <summary>
    /// A unit sent, under its own order, to ground a crowd has already settled on must arrive.
    /// </summary>
    /// <remarks>
    /// The crowded-arrival path identified an obstruction by whether it had been *ordered* to the
    /// same destination. A group move gives every member its own formation slot, so a crowd that
    /// arrived earlier shares no requested destination with a latecomer, none of them was ever its
    /// obstruction, and the mechanism stayed switched off: it pushed toward a point thirty bodies
    /// were standing on until the test ran out of ticks.
    /// <para>
    /// Both sizes, because the shape of the bug is not about size — a large body merely meets it
    /// first, since it cannot squeeze close enough to the point to arrive the ordinary way. The
    /// villager here passed before the fix and is kept as the control: if it ever starts failing,
    /// the problem is arrival in general and not this.
    /// </para>
    /// </remarks>
    private static bool LatecomerArrivesAtOccupiedGround()
    {
        var passed = true;
        foreach (var type in new[] { UnitType.Villager, UnitType.HeavyCavalry })
        {
            var world = new SimulationWorld();
            var target = new Vector2(4f, 0f);
            var crowd = new List<AgentId>();
            for (var row = 0; row < 5; row++)
            for (var column = 0; column < 4; column++)
            {
                crowd.Add(world.SpawnAgent(
                    new Vector2(-4f + column * 0.9f, -2f + row * 0.9f), UnitType.Villager));
            }

            world.QueueMove(crowd, target);

            // Run until the crowd is genuinely settled, so the latecomer meets standing bodies
            // rather than a moving column it can follow in behind.
            var settled = false;
            for (var tick = 0; tick < 600 * WalkingPace && !settled; tick++)
            {
                world.Tick((float)SimulationWorld.FixedDeltaSeconds);
                settled = crowd.All(id => !world.Agents.Get(id).HasDestination);
            }

            if (!settled)
            {
                Console.WriteLine($"    {type.Name}: the crowd never settled, so this proves nothing");
                passed = false;
                continue;
            }

            var latecomer = world.SpawnAgent(new Vector2(-9f, 0f), type);
            world.QueueMove(new[] { latecomer }, target);

            var arrived = -1f;
            for (var tick = 0; tick < 600 * WalkingPace; tick++)
            {
                world.Tick((float)SimulationWorld.FixedDeltaSeconds);
                if (world.Agents.Get(latecomer).HasDestination) continue;
                arrived = (tick + 1) * (float)SimulationWorld.FixedDeltaSeconds;
                break;
            }

            if (arrived > 0f) continue;
            ref readonly var stalled = ref world.Agents.Get(latecomer);
            passed = false;
            Console.WriteLine(
                $"    {type.Name}: never arrived, stopped " +
                $"{Vector2.Distance(stalled.Position, target):F2} m short doing " +
                $"{stalled.Velocity.Length():F2} m/s, stuck {stalled.StuckSeconds:F1}s");
        }

        return passed;
    }

    /// <summary>
    /// A jam costs a wide body more than a narrow one, because most of the holes in a queue of
    /// narrow bodies are not it.
    /// </summary>
    /// <remarks>
    /// The speed term next door rests on a queue costing whoever is in it the same wall clock.
    /// Measured through one 3 m gap behind thirty villagers, it does not: a 0.55 m cart lost 0.2 s
    /// to it, a 0.37 m villager 0.3 s, and a 0.90 m body <b>16.2 s</b>. Width is by far the larger
    /// term and the router had none at all.
    /// <para>
    /// Asserted on the term and not on a route, for the same reason as its sibling: which way a
    /// body goes at a jam is chaotic in the geometry. The reference body must price a queue at
    /// exactly one, or every constant the congestion layer was tuned against silently moves.
    /// </para>
    /// </remarks>
    private static bool CongestionIsPricedByWidth()
    {
        var villager = PathService.CongestionSizeScale(UnitType.Villager.Radius);
        var cart = PathService.CongestionSizeScale(UnitType.HaulerCart.Radius);
        // Two widths in the roster now that the second body class is retired, so this is asserted as the
        // relation rather than as an ordering over three: the reference body prices a queue at exactly
        // one, and anything wider prices it in proportion to how much of the aperture it takes.
        var wider = cart > villager;
        var referenceIsOne = MathF.Abs(villager - 1f) < 0.001f;
        var proportionate =
            MathF.Abs(cart - UnitType.HaulerCart.Radius / AgentDefaults.Radius) < 0.001f;

        var passed = wider && referenceIsOne && proportionate;
        if (!passed)
        {
            Console.WriteLine(
                $"    congestion by width: villager {villager:F3} (must be exactly 1), cart " +
                $"{cart:F3} (must be wider and proportionate at " +
                $"{UnitType.HaulerCart.Radius / AgentDefaults.Radius:F3})");
        }

        return passed;
    }
}
