using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;

namespace RTSGame.Debug;

/// <summary>
/// A settlement that produces, stores, hauls and consumes, run headless for as long as you like.
/// </summary>
/// <remarks>
/// This is Session 6's gate: <em>a settlement runs a full year with no counter drifting and no unit
/// permanently stalled.</em> Both halves are asserted rather than eyeballed, and the first one is exact
/// — stock is counted in whole units, so <c>seeded + produced − consumed</c> must equal what is stored
/// plus what is on somebody's back, with no tolerance. A single unit appearing or vanishing anywhere in
/// 162,000 ticks fails it and says which resource.
/// <para>
/// It is also §15's per-PR soak gate: a year is about four minutes at this population, which covers
/// three of §8's five stress points — a quiet season, the harvest crunch, and the winter drain.
/// </para>
/// </remarks>
internal static class SettlementScenarios
{
    private const int TicksPerSecond = 30;

    /// <summary>Farms, at one pair of hands each, which is the efficient staffing.</summary>
    /// <remarks>
    /// Lean rather than piled, because output has diminishing returns in hands at one place: twelve farms
    /// of one hand out-produce six of two by forty per cent for the same labour. That is §2's identity
    /// as a layout decision — lean staffing is efficient and wants attention at every season boundary,
    /// overstaffing is autonomous and wasteful — and the trace is where a player would see it.
    /// </remarks>
    private const int Farms = 12;

    private const int Woodcutters = 7;

    /// <summary>Hauler carts, at 0.55 m and 40 units, all of them Foot class.</summary>
    private const int Carts = 7;

    /// <summary>People per household. Houses are the clocked sinks; nothing else eats.</summary>
    private const int Occupancy = 4;

    /// <summary>
    /// Wagons, at 0.90 m — <b>zero, and that is a finding rather than a preference.</b>
    /// </summary>
    /// <remarks>
    /// Debt 7 wants many haulers of differing sizes sharing routes, and two wagons were in here for
    /// exactly that. They cannot work, and the reason is not the economy: <b>a Heavy-class body cannot be
    /// routed to a point beside a 1.5 m building on this map at all.</b> Measured — the wagon asks, is
    /// refused, and sits in the movement layer's limbo state; over a year, two of them accumulated a
    /// thousand refusals between them while a cost field priced the same journey at 89 seconds, so the
    /// route exists and the hierarchical search will not find it. Take the wagons out and the stalls go
    /// to zero with nothing else changed.
    /// <para>
    /// That is a routing question and it belongs with debt 6, which already flags that placing a building
    /// re-rasterises and that what it should do to the decomposition is undecided. Twenty scattered
    /// 1.5 m buildings is a case <c>--routingtest</c>'s staggered walls do not cover, and its <c>lost</c>
    /// column — cells the hierarchy cannot price — is the number to look at. Until then the settlement
    /// runs on carts, and debt 7 stays open for the second reason in a row that is not the one it expected.
    /// </para>
    /// </remarks>
    private const int Wagons = 0;

    /// <summary>Units of one resource a producer's yard holds before production stops.</summary>
    /// <remarks>
    /// The harvest crunch, in one number. A farm at the spike brings in 0.41 units a second and a
    /// hauler clears 40 units a round trip, so a yard this size fills in six minutes if nobody comes
    /// for it — and production then stops rather than grain being counted and dropped on the floor.
    /// §2 wanted harvest failure graded rather than binary: you bring in what your standing arrangement
    /// can carry, and this is where that happens.
    /// </remarks>
    private const int YardCapacity = 150;

    public static int Run(float extentMeters, float years)
    {
        var world = Build(extentMeters, out var granary);
        var totalTicks = (int)(years * WorldCalendar.YearSeconds * TicksPerSecond);
        var faults = new List<string>();

        Console.WriteLine(
            $"RTSGame settlement — {world.ExtentMeters:F0} m, {world.Agents.LiveCount} people, " +
            $"{world.Nodes.LiveCount} nodes, {years:F2} year(s)");
        Console.WriteLine(
            $"  {Farms} farms and {Woodcutters} woodcutters at one hand each, {Carts} carts and " +
            $"{Wagons} wagons, one granary of {world.Nodes.Get(granary).Capacity:N0}, " +
            $"housing for {Occupancy} per household");
        Console.WriteLine(
            "        date        | grain | wood  | hands | fields             | forest         | " +
            "hauls | carrying | grain-left | wood-left | short | unhoused | stalled | ms/tick");

        var reported = Season.Winter;
        for (var tick = 1; tick <= totalTicks; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);

            // Conservation is checked every tick, not every season. It is a handful of additions, and
            // the value of an exact ledger is knowing the tick a unit went missing on.
            var drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (drift.Grain != 0 || drift.Wood != 0)
            {
                faults.Add(
                    $"conservation broke on tick {tick} ({world.Date}): " +
                    $"{drift.Grain:+#;-#;0} grain, {drift.Wood:+#;-#;0} wood unaccounted for");
                break;
            }

            if (world.Date.Season == reported) continue;
            reported = world.Date.Season;
            Report(world, faults);
        }

        if (world.Date.Season != reported) Report(world, faults);
        Summarise(world, years);

        foreach (var fault in faults) Console.WriteLine($"  FAULT: {fault}");
        return faults.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// A settlement laid out inside one catchment, because that is what a catchment is for.
    /// </summary>
    /// <remarks>
    /// Producers are placed in a ring around the granary at a little over half the catchment radius, so
    /// every one of them is comfortably inside it and the haul legs are the length §6 says they should
    /// be. Everybody who works stands at their node under a <c>Hold</c> assignment, which is what makes
    /// them count as hands — so the economy is driven by the jobs layer rather than by a parallel notion
    /// of employment.
    /// </remarks>
    private static SimulationWorld Build(float extentMeters, out NodeId granary)
    {
        var world = new SimulationWorld(extentMeters);
        granary = Populate(world, Farms, Woodcutters, Carts, Wagons, ringRadius: 36f);
        return world;
    }

    /// <summary>
    /// Lays a working settlement into an existing world, and returns its granary.
    /// </summary>
    /// <remarks>
    /// Shared by the headless gate and the live game, deliberately: two settlement definitions would
    /// drift, and the one thing worth being able to say about the thing on screen is that it is the
    /// same arrangement the year-long run asserts about.
    /// </remarks>
    public static NodeId Populate(
        SimulationWorld world,
        int farms,
        int woodcutters,
        int carts,
        int wagons,
        float ringRadius,
        Vector2 centre = default)
    {
        var granary = world.AddNode(NodeKind.Granary, centre, capacity: 9000);

        // There is one harvest a year, so a settlement founded in spring lives on its stores until the
        // fiftieth day of the harvest season — 3,000 of the year's 5,400 seconds, better than half of it.
        // At 270 grain a head that is about 3,900 for this population, and a founding cache has to cover
        // it or the settlement starves through a summer with twelve healthy fields standing in front of
        // it. Seeded rather than conjured: the ledger records it, so conservation still balances.
        world.SeedStock(granary, Resource.Grain, 4200);
        world.SeedStock(granary, Resource.Wood, 1000);

        // The village core: houses on an arc to one side of the granary, tucked as close to it as their
        // own walls allow. A household outside every catchment goes hungry however full the stores are,
        // so near is the safe direction, and the arc is sized to leave a cart's width between
        // neighbours — buildings are 4.5 m across and a ring that fitted them at 1.5 m puts them
        // shoulder to shoulder.
        var people = farms + woodcutters + carts + wagons;
        var households = (people + Occupancy - 1) / Occupancy;
        var houseWidth = NodeFootprint.HalfExtentOf(NodeKind.House) * 2f;
        var houseArc = MathF.Max(
            NodeFootprint.HalfExtentOf(NodeKind.Granary) + houseWidth * 0.5f + 1.5f,
            households * houseWidth * 1.3f / MathF.PI);
        for (var i = 0; i < households; i++)
        {
            // Half a turn, centred on west, so the village sits on one side and the fields on the other.
            var angle = MathF.PI * (0.5f + (i + 0.5f) / households);
            world.AddNode(
                NodeKind.House,
                centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * houseArc,
                capacity: 0,
                occupancy: Occupancy);
        }

        var producers = new List<(NodeId Node, Vector2 At)>();

        // <b>The fields are one contiguous block abutting the granary</b>, marching away from the village
        // rather than ringed all round it. Three things settled that shape and none of them was taste.
        // The reaper carries its own crop in, so every metre of the walk is a metre not spent reaping —
        // measured, fields on a 36 m ring lost <em>half the crop</em> to commuting inside a harvest window
        // that only just holds the reaping, and packed against the store the same fields brought in 97%.
        // A field is not a wall any more (<c>NodeFootprint.Blocks</c>), so they can be laid edge to edge
        // with no gap and no maze: a patchwork, which is what fields are, and what a dozen 4.5 m plots
        // half a metre apart conspicuously was not. And putting them all on one side keeps the village
        // out of the middle of them, so the traffic between store and field never crosses the housing.
        var slot = NodeFootprint.HalfExtentOf(NodeKind.Farm) * 2f;
        var across = (int)MathF.Ceiling(MathF.Sqrt(farms));
        var firstColumn = NodeFootprint.HalfExtentOf(NodeKind.Granary) + slot * 0.5f;
        for (var i = 0; i < farms; i++)
        {
            var column = i / across;
            var row = i % across;
            var at = centre + new Vector2(
                firstColumn + column * slot,
                (row - (across - 1) * 0.5f) * slot);
            producers.Add((world.AddNode(NodeKind.Farm, at, YardCapacity, Resource.Grain), at));
        }

        // A settlement that starts partway through a year has already worked the windows that have passed.
        // Without this, any mid-year start lands on a harvest whose fields were never broken — twelve
        // failed crops and nothing to reap, which is correct arithmetic and a nonsense founding. It is a
        // fact about setting a scenario up, not a rule of the game: the ground was broken last spring by
        // people the simulation was not running yet.
        var phase = CropCycle.PhaseOf(world.Date.Season);
        foreach (var (node, _) in producers)
        {
            ref var field = ref world.Nodes.Get(node);
            if (field.Kind != NodeKind.Farm) continue;
            field.CycleYear = world.Date.Year;
            if (phase is CropPhase.Maintain or CropPhase.Reap) field.PrepareWork = CropCycle.PrepareLabour;
            if (phase is CropPhase.Reap) field.MaintainWork = CropCycle.MaintainLabour;
        }

        // The forest. Where it is, and how thin it has been cut, is the whole of the wood economy: there
        // is no woodcutter building any more, only trees and the people sent to them.
        ScatterWoodland(world, centre, ringRadius);

        // One hand per producer, posted. Staggered dwell, so the settlement does not breathe in unison
        // — see the note on the stagger below.
        for (var i = 0; i < producers.Count; i++)
        {
            var (node, _) = producers[i];
            // Read the node's position back rather than using the one it was asked for: a building is
            // snapped to its placement cell, which can move it by up to half a cell diagonal, and a hand
            // spawned relative to the original point could end up standing inside its own farm's wall.
            // Two of nineteen did exactly that, and the settlement lost a fifth of its harvest to it.
            var placed = world.Nodes.Get(node).Position;
            var extent = world.Nodes.Get(node).FootprintRadius;
            // Outward from the centre, so hands stand on the far side of the yard from the traffic.
            var outward = placed - centre;
            outward = outward.LengthSquared() > 0.001f ? Vector2.Normalize(outward) : Vector2.UnitX;
            // Mustered a body's width off the wall rather than off the circle round the building, which
            // put everybody 2.2 m out on the first frame and read as a settlement standing back from its
            // own work before it had even started.
            var hand = world.SpawnAgent(
                placed + outward * (world.Nodes.Get(node).HalfExtent + UnitType.Villager.Radius + 0.9f),
                UnitType.Villager);
            ref readonly var site = ref world.Nodes.Get(node);
            world.QueueAssign(
                new[] { hand },
                site.Kind == NodeKind.Farm
                    // A field is worked, not stood at: prepared in spring, kept in summer, reaped in
                    // harvest, and the crop carried in by whoever reaped it.
                    ? Assignment.Work(
                        node, placed, extent, Resource.Grain,
                        EconomySystem.WorkShiftSeconds, EconomySystem.HandoverSeconds)
                    : Assignment.Hold(placed, Stagger(20f, i, producers.Count), extent));
        }

        // Cutters. Posted at a tree rather than at a building, because there is no longer a building to
        // post them at: the settlement's wood comes out of the nearest trees to a store, and when those
        // are gone the cutters go looking for a store that can still reach one. Each is dropped on a
        // different tree so they do not all fell the same trunk.
        var claimed = new HashSet<int>();
        for (var i = 0; i < woodcutters; i++)
        {
            var tree = NextUnclaimedTree(world, granary, claimed);
            if (!tree.IsValid) break;
            ref readonly var trunk = ref world.Nodes.Get(tree);
            var at = trunk.Position;
            var extent = trunk.FootprintRadius;
            var hand = world.SpawnAgent(
                at + new Vector2(0f, extent + UnitType.Villager.Radius + 0.6f), UnitType.Villager);
            world.QueueAssign(
                new[] { hand },
                Assignment.Work(
                    tree, at, extent, Resource.Wood,
                    Woodland.LoadSeconds(UnitType.Villager.CarryCapacity),
                    EconomySystem.HandoverSeconds));
        }

        for (var i = 0; i < carts + wagons; i++)
        {
            var angle = i / (float)(carts + wagons) * MathF.Tau;
            world.SpawnAgent(
                centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 5f, UnitType.HaulerCart);
        }

        return granary;
    }

    /// <summary>The nearest tree to the store that no cutter has been sent to yet.</summary>
    private static NodeId NextUnclaimedTree(SimulationWorld world, NodeId store, HashSet<int> claimed)
    {
        var from = world.Nodes.Get(store).Position;
        var best = NodeId.None;
        var bestDistance = Woodland.ReachMetres * Woodland.ReachMetres;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || !node.IsStanding || claimed.Contains(node.Id.Value)) continue;
            var distance = Vector2.DistanceSquared(node.Position, from);
            if (distance > bestDistance) continue;
            bestDistance = distance;
            best = node.Id;
        }

        if (best.IsValid) claimed.Add(best.Value);
        return best;
    }

    /// <summary>
    /// Trees, thinned near the settlement and continuous further out.
    /// </summary>
    /// <remarks>
    /// <b>The gradient is the point, and it is not decoration — it is the record of past logging.</b>
    /// Close in, where people have been cutting for years, there are stragglers: single trees with gaps
    /// between them. Further out there are canopies, clumps that have been nibbled at. Past that it is
    /// unbroken woodland nobody has reached yet. So the map already tells the player which direction the
    /// wood ran out in, before the simulation has run a tick, and "distance is the terrain" is a fact
    /// about the ground rather than a comment in a seed function.
    /// <para>
    /// It is also what makes the stage's two halves both true of one map. The stragglers inside
    /// <see cref="Woodland.ReachMetres"/> of the granary are a compact settlement's whole wood supply and
    /// they last about two years, so a year-long run needs no cart. Fell them and the nearest tree is
    /// forty metres out, no store can reach it, and the only answer is a depot at the tree line — at
    /// which point the wood is piling up somewhere nobody lives and the carts have work.
    /// </para>
    /// <para>
    /// Deterministic, from a counter rather than a clock: two runs of this world must be the same world,
    /// which the fingerprint checks and the save relies on. There is no <c>Random</c> anywhere in the
    /// simulation and this is not the place to introduce one.
    /// </para>
    /// </remarks>
    private static void ScatterWoodland(SimulationWorld world, Vector2 centre, float ringRadius)
    {
        var seed = 0x9E3779B9u;

        float Next()
        {
            // splitmix32: one multiply-xor-shift chain, deterministic, and enough for a scatter.
            seed += 0x9E3779B9u;
            var z = seed;
            z = (z ^ (z >> 16)) * 0x21F0AAADu;
            z = (z ^ (z >> 15)) * 0x735A2D97u;
            z ^= z >> 15;
            return (z & 0xFFFFFFu) / (float)0x1000000u;
        }

        // A coarse hash grid over the candidate positions, so the spacing test looks at a handful of
        // neighbours instead of every tree placed so far. Ten thousand trees against a linear scan is
        // fifty million distance tests and several seconds of startup; against this it is a few hundred
        // thousand. The cell is sized to the widest spacing any band asks for, so a tree's neighbours are
        // always in its own cell or one adjacent.
        const float cellSize = 4f;
        var buckets = new Dictionary<(int, int), List<Vector2>>();

        bool TooClose(Vector2 at, float spacing)
        {
            var cx = (int)MathF.Floor(at.X / cellSize);
            var cz = (int)MathF.Floor(at.Y / cellSize);
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!buckets.TryGetValue((cx + dx, cz + dz), out var bucket)) continue;
                foreach (var other in bucket)
                {
                    if (Vector2.DistanceSquared(other, at) < spacing * spacing) return true;
                }
            }

            return false;
        }

        bool TryPlant(Vector2 at, float spacing)
        {
            // Never in the fields or under a building. A tree standing in a wheat field is not a
            // collision — trees do not block — it is a lie about what that ground is being used for.
            if (MathF.Abs(at.X - centre.X) < FieldKeepOut && MathF.Abs(at.Y - centre.Y) < FieldKeepOut)
            {
                return false;
            }

            if (TooClose(at, spacing)) return false;
            var node = world.AddNode(NodeKind.Tree, at, capacity: (int)Woodland.WoodPerTree);
            world.SeedStock(node, Resource.Wood, (int)Woodland.WoodPerTree);
            var settled = world.Nodes.Get(node).Position;
            var key = ((int)MathF.Floor(settled.X / cellSize), (int)MathF.Floor(settled.Y / cellSize));
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<Vector2>();
            list.Add(settled);
            return true;
        }

        void Band(float inner, float outer, int trees, float spacing, int clump)
        {
            for (var i = 0; i < trees; i++)
            {
                // A clump is one draw for the centre and the rest scattered around it, which is what
                // makes a canopy read as a canopy rather than as evenly spread noise.
                var anchor = centre + Polar(Next(), inner, outer, Next());
                for (var k = 0; k < clump; k++)
                {
                    var at = clump == 1
                        ? anchor
                        : anchor + new Vector2(Next() * 2f - 1f, Next() * 2f - 1f) * spacing * 2.2f;
                    for (var attempt = 0; attempt < 6; attempt++)
                    {
                        if (TryPlant(world.Terrain.ClampPosition(at), spacing)) break;
                        at = anchor + new Vector2(Next() * 2f - 1f, Next() * 2f - 1f) * spacing * 2.6f;
                    }
                }
            }
        }

        // <b>The first band is load-bearing and the rest are scenery.</b> Everything the economy gate
        // measures depends on how much wood stands within a cutter's reach of the granary — about two
        // years of this settlement's burning, so a one-year run never runs out and a two-year one only
        // just does. Change 46 and the numbers in §22 change with it. Everything past reach is the map
        // the player expands into, and its density is free to be whatever reads best.
        Band(FieldKeepOut + 3f, Woodland.ReachMetres, trees: 46, spacing: 3.4f, clump: 1);
        // Canopies: clumps just beyond reach, which is where the tree line currently sits. Started clear
        // of the reach radius rather than at it, because a clump scatters its members several metres
        // around its anchor and the ones that landed inward pushed the in-reach count from 46 to 66 —
        // half a settlement's annual fuel, arriving as a side effect of a density change.
        Band(Woodland.ReachMetres + 8f, ringRadius * 1.5f, trees: 120, spacing: 2.6f, clump: 6);
        // Closing up: the transition from a thinned edge to woodland proper.
        Band(ringRadius * 1.5f, ringRadius * 3f, trees: 260, spacing: 2.4f, clump: 9);
        // Continuous forest, and the reason a settlement expands rather than starves. Out to a bit under
        // half the map, because a 600 m world whose outer half is bare plain does not read as a world with
        // a forest in it — it reads as a diorama with a hedge round it.
        Band(ringRadius * 3f, ringRadius * 7f, trees: 900, spacing: 2.2f, clump: 11);

        (SeededTimber, SeededTrees) = world.Nodes.StandingTimber();
    }

    /// <summary>Half-width of the ground the fields and the village occupy, which stays clear.</summary>
    private const float FieldKeepOut = 16f;

    /// <summary>What the woodland held when it was seeded, so felling can be reported against it.</summary>
    private static int SeededTimber;

    private static int SeededTrees;

    private static Vector2 Polar(float turn, float inner, float outer, float radial)
    {
        var angle = turn * MathF.Tau;
        // Square-rooted so trees are spread evenly over the annulus rather than crowded at its inside.
        var radius = MathF.Sqrt(inner * inner + radial * (outer * outer - inner * inner));
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
    }

    /// <summary>
    /// Spreads a dwell across a cohort so they do not all finish at once.
    /// </summary>
    /// <remarks>
    /// Debt 11 from the jobs trace: sixteen units given the same assignment in the same tick arrive,
    /// dwell and leave together forever, and a lane's throughput pulses between nothing and everything
    /// instead of settling. A tenth of a period of spread is enough to break the lockstep, and it is the
    /// caller's business rather than the jobs layer's — the layer is right to be deterministic, and
    /// whoever hands out the work is the one who knows how many are being handed it.
    /// </remarks>
    private static float Stagger(float period, int index, int count) =>
        period * (1f + 0.1f * (index / (float)Math.Max(1, count) - 0.5f));

    /// <summary>
    /// How far each working body actually ends up from the wall it is working at.
    /// </summary>
    /// <remarks>
    /// Reported every season because it is the number that goes wrong silently. Three separate figures
    /// had to agree before it came right — the walk target, the arrival tolerance and the crowd fallback —
    /// and while they disagreed, every body in the settlement failed to arrive, waited out a retry, and
    /// settled short of its own work. Nothing failed; it just looked like hesitation. <b>"Settled short"
    /// being anything other than zero is the warning.</b>
    /// </remarks>
    private static void ReportGaps(SimulationWorld world)
    {
        var hands = new List<float>();
        var settled = 0;
        foreach (ref readonly var agent in world.Agents.All)
        {
            if (!agent.IsAlive || agent.Jobs.PlaceExtent <= 0f) continue;
            if (agent.Jobs.Activity == ActivityKind.None) continue;
            var half = new Vector2(agent.Jobs.PlaceHalfWidth);
            var nearest = Vector2.Clamp(
                agent.Position, agent.Jobs.Place - half, agent.Jobs.Place + half);
            hands.Add(Vector2.Distance(agent.Position, nearest));
            if (agent.Jobs.SettledNearby) settled++;
        }

        if (hands.Count == 0) return;
        hands.Sort();
        Console.WriteLine(
            $"      at the wall: median {hands[hands.Count / 2]:F2} m over {hands.Count} bodies, " +
            $"{settled} settled short of it");
    }

    private static float ClearanceAt(SimulationWorld world, Vector2 position) =>
        world.Navigation.TryWorldToCell(position, out var cell) ? world.Navigation.Clearance(cell) : -1f;

    private static void Report(SimulationWorld world, List<string> faults)
    {
        var grain = world.Economy.Outlook(Resource.Grain, world.Nodes, world.Agents, world.Date.Season);
        var wood = world.Economy.Outlook(Resource.Wood, world.Nodes, world.Agents, world.Date.Season);
        var carried = EconomySystem.CarriedTotal(world.Agents);
        var hands = 0;
        foreach (ref readonly var node in world.Nodes.All) hands += node.Hands;

        var stalled = 0;
        foreach (ref readonly var agent in world.Agents.All)
        {
            if (!agent.IsAlive || !agent.Jobs.CannotReachWork) continue;
            stalled++;
            // One failed walk is the crowd at the granary door — nineteen producers deliver to one
            // building and occasionally somebody is shouldered out of the spot it was aiming at, counts a
            // retry, and walks back. Three is not the crowd. The column still shows every body that has
            // fallen short so the number is visible; the fault is reserved for one that is not getting
            // there.
            if (agent.Jobs.Retries < 3) continue;
            // The numbers that identify the cause: how far it is against how near it has to be, and
            // what it thinks it is standing next to. Guessing at this cost two rounds.
            faults.Add(
                $"{agent.Id} (r={agent.Radius:F2}) cannot reach its {agent.Jobs.Assignment.Kind} at " +
                $"({agent.Jobs.Place.X:F1},{agent.Jobs.Place.Y:F1}) extent={agent.Jobs.PlaceExtent:F2}: " +
                $"it is {Vector2.Distance(agent.Position, agent.Jobs.Place):F2} m away and needs " +
                $"{JobDefaults.AtPlaceDistance(agent.Radius, agent.Jobs.PlaceExtent):F2}, " +
                $"{agent.Jobs.Retries} tries, state={agent.LocomotionState} " +
                $"moving={agent.HasDestination} route={world.GetRemainingPath(agent.Id).Length} " +
                $"navigable-here={world.IsAgentGeometryValid(agent.Id)} " +
                $"navRadius={agent.NavigationRadius:F2} " +
                $"clearance-here={ClearanceAt(world, agent.Position):F2} " +
                $"clearance-at-place={ClearanceAt(world, agent.Jobs.Place):F2} " +
                $"priced={world.TryTravelSeconds(agent.Position, agent.Jobs.Place, agent.NavigationRadius, out var seconds)}/{seconds:F0}s, " +
                $"at {world.Date}");
        }

        ReportGaps(world);
        Console.WriteLine(
            $"  {world.Date,-18} | {grain.Stored,5:N0} | {wood.Stored,5:N0} | {hands,5} | " +
            $"{Fields(world),-18} | {Forest(world),-14} | " +
            $"{world.Economy.HaulsAssigned,5:N0} | {carried.Total,8:N0} | " +
            $"{Seasons(grain.Seasons),10} | {Seasons(wood.Seasons),9} | " +
            $"{world.Economy.Unmet.Grain + world.Economy.Unmet.Wood,5:N0} | " +
            $"{world.UnhousedCount,8} | {stalled,7} | " +
            $"{world.Timings.Format(world.Agents.Count, world.TickNumber).Split("total ")[1].Split(" ms")[0]}");
    }

    /// <summary>
    /// The wood line: how much timber is left, and whether any of it is still in reach of a store.
    /// </summary>
    /// <remarks>
    /// <b>Trees in reach is the number Stage B is about.</b> Standing timber falling is the settlement
    /// eating its forest, which is expected and is the whole clock of the game. Trees <em>in reach</em>
    /// falling to zero is the settlement having outgrown its arrangement, and it is the moment a forward
    /// depot at the tree line stops being optional — after which the wood piles up somewhere nobody lives
    /// and the carts have work for the first time.
    /// </remarks>
    private static string Forest(SimulationWorld world)
    {
        var (standing, trees) = world.Nodes.StandingTimber();
        var reachable = 0;
        foreach (ref readonly var store in world.Nodes.All)
        {
            if (!store.IsAlive || !store.Stores) continue;
            foreach (ref readonly var tree in world.Nodes.All)
            {
                if (!tree.IsAlive || !tree.IsStanding || tree.Stock.Wood <= 0) continue;
                if (Vector2.Distance(tree.Position, store.Position) <= Woodland.ReachMetres) reachable++;
            }
        }

        // Axes actually swinging, rather than people who call themselves woodcutters. A cutter whose
        // trees run out loses its assignment altogether — it becomes spare labour, which is correct —
        // so counting cutters would count nobody at exactly the moment the number mattered. This one
        // goes to zero the season the wood line passes out of reach, which is the signal.
        return $"{trees,3} tr {reachable,3} nr {EconomySystem.CuttersAtWork(world.Agents),2} cut";
    }

    /// <summary>
    /// What the fields say about themselves, which is the only place the crop cycle is visible.
    /// </summary>
    /// <remarks>
    /// A field's trouble is always in the past — a ceiling not set in spring cannot be diagnosed at
    /// harvest from anything a body is doing — so the field has to say so at the time. Reported as the
    /// worst thing any field is saying plus how many agree with it, because twelve identical strings tell
    /// you less than one string and a count.
    /// </remarks>
    private static string Fields(SimulationWorld world)
    {
        var says = new Dictionary<string, int>();
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || node.Kind != NodeKind.Farm) continue;
            var state = CropCycle.StateOf(in node, world.Date.Season);
            says[state] = says.GetValueOrDefault(state) + 1;
        }

        if (says.Count == 0) return "none";
        var worst = says.OrderBy(entry => Rank(entry.Key)).First();
        return says.Count == 1
            ? $"{worst.Value} {worst.Key}"
            : $"{worst.Value} {worst.Key} +{says.Count - 1} more";
    }

    /// <summary>Worst first: a failure outranks a warning outranks everything being fine.</summary>
    private static int Rank(string state) => state switch
    {
        "failed" => 0,
        "unbroken" => 1,
        "neglected" => 2,
        "standing" => 3,
        _ when state.StartsWith("preparing", StringComparison.Ordinal) => 4,
        _ when state.StartsWith("reaping", StringComparison.Ordinal) => 5,
        _ when state.StartsWith("tending", StringComparison.Ordinal) => 6,
        _ => 7,
    };

    /// <summary>§8's autonomy time, rendered as the one thing the HUD says: how long.</summary>
    private static string Seasons(float seasons) => float.IsPositiveInfinity(seasons)
        ? "growing"
        : $"{seasons:F1} seas";

    private static void Summarise(SimulationWorld world, float years)
    {
        var economy = world.Economy;
        Console.WriteLine(
            $"  over {years:F2} year(s): produced {economy.Produced.Grain:N0} grain and " +
            $"{economy.Produced.Wood:N0} wood, ate {economy.Consumed.Grain:N0} and " +
            $"{economy.Consumed.Wood:N0}, went short {economy.Unmet.Grain:N0} and {economy.Unmet.Wood:N0}");
        Console.WriteLine(
            $"  hauling: {economy.HaulsAssigned:N0} jobs given out, {economy.HaulsAbandoned:N0} dropped " +
            "when the source emptied or the sink filled");
        var (standing, trees) = world.Nodes.StandingTimber();
        // Against what the woodland started with, not against everything ever seeded: the granary's
        // founding stock is also seeded wood, and subtracting standing timber from the whole ledger
        // credited the settlement with felling a thousand units of granary.
        Console.WriteLine(
            $"  forest: {trees:N0} of {SeededTrees:N0} trees left, holding {standing:N0} of " +
            $"{SeededTimber:N0} wood — wood is never produced, only taken out of trees");

        // What the year cost per person, against what the rates say it should have. A settlement that
        // ate less than its appetite went short somewhere, and the shortfall column says where.
        var mouths = world.Agents.LiveCount;
        Console.WriteLine(
            $"  per person per year: {economy.Consumed.Grain / MathF.Max(1f, mouths * years):N0} grain " +
            $"against a nominal {EconomyRates.GrainPerVillagerPerYear:N0}, " +
            $"{economy.Consumed.Wood / MathF.Max(1f, mouths * years):N0} wood against " +
            $"{EconomyRates.WoodPerVillagerPerYear:N0}");
    }
}
