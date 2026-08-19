using System.Numerics;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Jobs;
using RTSGame.Simulation.Persistence;

namespace RTSGame.Simulation.Economy;

/// <summary>Cumulative totals, in whole units, so conservation is exact arithmetic.</summary>
internal struct ResourceTotals
{
    public long Grain;
    public long Wood;

    public long this[Resource resource]
    {
        readonly get => resource == Resource.Grain ? Grain : Wood;
        set
        {
            if (resource == Resource.Grain) Grain = value;
            else Wood = value;
        }
    }

    public void Add(Resource resource, long amount) => this[resource] += amount;
}

/// <summary>What a settlement is doing, for the one question the HUD asks.</summary>
/// <param name="Stored">Units in stores right now.</param>
/// <param name="DrawPerSecond">Units a second currently being consumed.</param>
/// <param name="ProducedPerSecond">Units a second currently arriving.</param>
/// <param name="Seasons">
/// How many seasons the stores last at the current net draw, or infinity if nothing is being lost.
/// §8's autonomy time, per resource.
/// </param>
internal readonly record struct ResourceOutlook(
    int Stored,
    float DrawPerSecond,
    float ProducedPerSecond,
    float Seasons);

/// <summary>
/// Production, consumption, catchments and the hauling board.
/// </summary>
/// <remarks>
/// Four things happen here every tick and the order matters. Hands are counted, because output is
/// continuous in them. Producers accrue. Consumers draw from the store whose catchment they stand in,
/// which is what makes consumption spatial and settlement layout a decision. And the board looks for
/// stock in the wrong place, prices each idle hauler's round trip <b>in seconds through the congestion
/// field</b>, and gives the job to the cheapest — which is what makes a jammed route pick a different
/// hauler without anybody writing a rule about jams.
/// <para>
/// The board runs on an interval rather than every tick, and the interval is in seconds like
/// everything else. Assignments are the graph, paths are the locomotion; this file only ever produces
/// the first, and the jobs layer turns it into the second.
/// </para>
/// </remarks>
internal sealed class EconomySystem
{
    /// <summary>Seconds between passes of the hauling board.</summary>
    /// <remarks>
    /// A hauling decision is worth making about as often as a hauler could act on one, and a leg is
    /// tens of seconds. Every tick would price thirty round trips a second to change its mind about
    /// journeys that take a minute.
    /// </remarks>
    internal static float BoardIntervalSeconds = 2f;

    /// <summary>Seconds a hauler spends loading or unloading at a node.</summary>
    /// <remarks>
    /// Not zero, because a hauling network with instant transfer has no reason to want more haulers
    /// than routes, and the queue at a busy granary is one of the things the congestion field exists to
    /// price. Four seconds against a leg of sixty is a tenth of the round trip.
    /// </remarks>
    internal static float HandoverSeconds = 4f;

    /// <summary>How near a body has to be to a node to be working it, in metres.</summary>
    /// <remarks>
    /// A node is a building rather than a point, and a place for several pairs of hands — the jobs
    /// layer already settles a crowd around a workplace rather than queueing it for one square metre,
    /// and this is the yard that crowd stands in.
    /// </remarks>
    internal static float WorkRadius = 4f;

    /// <summary>Share of a store's capacity above which it will give stock away.</summary>
    internal static float HighWater = 0.75f;

    /// <summary>Share below which a store will pull stock from a fuller one.</summary>
    internal static float LowWater = 0.25f;

    /// <summary>Seconds between a body looking for somewhere to live.</summary>
    internal static float RebindSeconds = 8f;

    /// <summary>
    /// Share of a cart's capacity worth sending it for, unless the yard is nearly full.
    /// </summary>
    /// <remarks>
    /// A cart sent for three units of grain spends a minute of round trip on a twentieth of a load, and
    /// while it is gone the yard it came from accumulates more than it collected. Waiting until there is
    /// a load worth fetching is not an optimisation, it is what makes the number of haulers a settlement
    /// needs proportional to what it produces rather than to how many yards it has.
    /// </remarks>
    internal static float WorthFetchingShare = 0.35f;

    private readonly List<HaulTask> tasks = new();
    private readonly List<AgentId> idleHaulers = new();
    private readonly HashSet<(NodeId Source, Resource Resource)> claimed = new();
    private float boardCooldown;

    /// <summary>Everything ever produced, consumed, or seeded into the world by hand.</summary>
    /// <remarks>
    /// The ledgers conservation is checked against, and the reason stock is counted in whole units:
    /// <c>Seeded + Produced - Consumed</c> must equal what is stored plus what is being carried,
    /// exactly, with no tolerance. A float ledger over a year of ticks could only ever be checked
    /// against a tolerance somebody picked, which is a much weaker claim than the gate asks for.
    /// <para>
    /// There is deliberately no term for goods lost. Nothing is lost: a body destroyed while carrying
    /// leaves a heap where it fell, and a heap is a node whose contents are stored like any other's. The
    /// identity got <em>shorter</em> when resources became physical, which is usually the sign that a
    /// design correction was the right one.
    /// </para>
    /// </remarks>
    public ResourceTotals Produced { get; private set; }

    public ResourceTotals Consumed { get; private set; }

    public ResourceTotals Seeded { get; private set; }

    /// <summary>Demand that arrived at an empty store. The signal, not an error.</summary>
    public ResourceTotals Unmet { get; private set; }

    /// <summary>Haul jobs handed out since the world began.</summary>
    public long HaulsAssigned { get; private set; }

    /// <summary>Haul jobs dropped because the source emptied or the sink filled.</summary>
    public long HaulsAbandoned { get; private set; }

    /// <summary>Seconds until the board next looks for work, which two runs have to agree on.</summary>
    internal float BoardCooldown => boardCooldown;

    /// <summary>Records stock put into the world by something other than production.</summary>
    public void RecordSeeded(Resource resource, int units)
    {
        var seeded = Seeded;
        seeded.Add(resource, units);
        Seeded = seeded;
    }

    /// <summary>The store of this faction with the most room for a resource, other than one.</summary>
    public static NodeId EmptiestStoreWithRoom(
        NodeStore nodes,
        Resource resource,
        FactionId faction,
        NodeId except)
    {
        var best = NodeId.None;
        var bestRoom = 0;
        foreach (ref readonly var node in nodes.All)
        {
            if (!node.IsAlive || !node.Stores || node.Id == except || node.Faction != faction) continue;
            var room = node.RoomFor(resource);
            if (room <= bestRoom) continue;
            bestRoom = room;
            best = node.Id;
        }

        return best;
    }

    /// <summary>
    /// What conservation says should be in stores and on backs, against what actually is.
    /// </summary>
    /// <remarks>
    /// Returns zero when the books balance. Anything else names how many units have appeared or
    /// vanished, which is the gate's "no counter drifting" as an exact statement rather than a
    /// tolerance.
    /// </remarks>
    public ResourceTotals Discrepancy(NodeStore nodes, AgentStore agents)
    {
        var stored = nodes.TotalStored();
        var carried = CarriedTotal(agents);
        var difference = default(ResourceTotals);
        foreach (var resource in Resources.All)
        {
            var expected = Seeded[resource] + Produced[resource] - Consumed[resource];
            difference[resource] = stored[resource] + carried[resource] - expected;
        }

        return difference;
    }

    /// <summary>Units currently on somebody's back, which are neither stored nor gone.</summary>
    public static NodeStock CarriedTotal(AgentStore agents)
    {
        var carried = default(NodeStock);
        foreach (ref readonly var agent in agents.All)
        {
            if (!agent.IsAlive || agent.Jobs.CarriedUnits <= 0) continue;
            carried.Add(agent.Jobs.Carrying, agent.Jobs.CarriedUnits);
        }

        return carried;
    }

    /// <summary>How long the stores last at the current net flow. §8's autonomy time.</summary>
    public ResourceOutlook Outlook(
        Resource resource,
        NodeStore nodes,
        AgentStore agents,
        Season season)
    {
        var stored = nodes.TotalStored()[resource];
        var draw = 0f;
        foreach (ref readonly var node in nodes.All)
        {
            if (!node.IsAlive || !node.IsSink) continue;
            draw += EconomyRates.DrawPerSecond(resource, season, node.AppetiteSum);
        }

        var produced = 0f;
        foreach (ref readonly var node in nodes.All)
        {
            if (!node.IsAlive || !node.Produces_ || node.Produces != resource) continue;
            produced += EconomyRates.ProductionPerSecond(resource, season) *
                        EconomyRates.HandsEffect(node.Hands);
        }

        var net = draw - produced;
        var seasons = net <= 0f
            ? float.PositiveInfinity
            : stored / net / (WorldCalendar.YearSeconds / 4f);
        return new ResourceOutlook(stored, draw, produced, seasons);
    }

    /// <summary>Runs the economy for one tick.</summary>
    /// <param name="price">
    /// Seconds a body of the given size takes to travel between two points, through terrain and
    /// whatever is jammed. Supplied by the world because only the router can answer it, and it is the
    /// currency the whole hauling decision is made in.
    /// </param>
    public void Update(
        NodeStore nodes,
        AgentStore agents,
        Season season,
        float deltaSeconds,
        TravelPrice price)
    {
        CountHands(nodes, agents);
        Produce(nodes, season, deltaSeconds);
        BindHomes(nodes, agents, deltaSeconds);
        BindCatchments(nodes, price);
        Consume(nodes, season, deltaSeconds);

        boardCooldown -= deltaSeconds;
        if (boardCooldown > 0f) return;
        boardCooldown = BoardIntervalSeconds;
        SweepEmptyPiles(nodes);
        RunBoard(nodes, agents, price);
    }

    /// <summary>Seconds between two points for a body of a given navigation radius.</summary>
    internal delegate bool TravelPrice(Vector2 from, Vector2 to, float navigationRadius, out float seconds);

    /// <summary>
    /// Counts the pairs of hands standing at each node.
    /// </summary>
    /// <remarks>
    /// Derived every tick rather than stored, and derived from the jobs layer rather than from a
    /// separate notion of employment: a body counts as a hand where it is <em>actually standing and
    /// working</em>, which means posting a villager at a farm is what makes the farm produce, and
    /// pulling it away with an order stops production for exactly as long as the order lasts. §2's
    /// identity — labour and attention are substitutes — falls out of that rather than being modelled.
    /// </remarks>
    private static void CountHands(NodeStore nodes, AgentStore agents)
    {
        var mutable = nodes.MutableSpan();
        for (var i = 0; i < mutable.Length; i++) mutable[i].Hands = 0;

        foreach (ref readonly var agent in agents.All)
        {
            if (!agent.IsAlive || agent.Jobs.Assignment.Kind != AssignmentKind.Hold) continue;
            if (agent.Jobs.Activity == ActivityKind.None || agent.Jobs.IsInterrupted) continue;
            // Posted here, not passing through. A hauler loading at a farm is standing in the yard and
            // is emphatically not a farmhand, which is what counting anybody nearby produced: seventeen
            // pairs of hands at sixteen farms. And it is where the body <em>is</em> rather than where it
            // was told to be, so a hand walking to the farm is not working it yet and one dragged away
            // by an order is not working it any more.
            var nearest = NodeAt(nodes, agent.Position, WorkRadius);
            if (!nearest.IsValid) continue;
            ref var node = ref nodes.Get(nearest);
            if (node.Produces_) node.Hands++;
        }
    }

    private void Produce(NodeStore nodes, Season season, float deltaSeconds)
    {
        var mutable = nodes.MutableSpan();
        var produced = Produced;
        for (var i = 0; i < mutable.Length; i++)
        {
            ref var node = ref mutable[i];
            if (!node.IsAlive || !node.Produces_ || node.Hands == 0) continue;
            var rate = EconomyRates.ProductionPerSecond(node.Produces, season) *
                       EconomyRates.HandsEffect(node.Hands);
            if (rate <= 0f) continue;

            var whole = node.Pending.Accrue(node.Produces, rate * deltaSeconds);
            if (whole <= 0) continue;
            // Capped at what the place can hold, and the excess is simply never produced rather than
            // produced and discarded. A full barn is a full barn; counting grain into a ledger and
            // then dropping it on the floor would be a drift the gate is specifically looking for.
            var room = node.RoomFor(node.Produces);
            var accepted = Math.Min(whole, room);
            node.Stock.Add(node.Produces, accepted);
            produced.Add(node.Produces, accepted);
        }

        Produced = produced;
    }

    /// <summary>
    /// Puts each body in a household, and counts who lives where.
    /// </summary>
    /// <remarks>
    /// Cheap and spatial only in that a body prefers a nearer house: this is straight-line distance
    /// rather than route seconds, because where somebody sleeps is not a logistics decision and paying
    /// for a routing query per person per rebind is exactly what moving the catchment onto the houses
    /// was for.
    /// </remarks>
    private static void BindHomes(NodeStore nodes, AgentStore agents, float deltaSeconds)
    {
        var houses = nodes.MutableSpan();
        for (var i = 0; i < houses.Length; i++)
        {
            if (!houses[i].IsSink) continue;
            houses[i].Occupants = 0;
            houses[i].AppetiteSum = 0f;
        }

        var bodies = agents.MutableSpan();
        for (var i = 0; i < bodies.Length; i++)
        {
            ref var agent = ref bodies[i];
            if (!agent.IsAlive) continue;
            agent.Home.RebindSeconds -= deltaSeconds;
            var housed = agent.Home.House.IsValid && nodes.Contains(agent.Home.House);
            var settled = housed &&
                          agent.Home.RebindSeconds > 0f &&
                          agent.Home.Revision == nodes.Revision;
            if (settled)
            {
                MoveIn(ref nodes.Get(agent.Home.House), in agent);
                continue;
            }

            agent.Home.RebindSeconds = RebindSeconds;
            agent.Home.Revision = nodes.Revision;
            agent.Home.House = NodeId.None;
            var nearest = float.PositiveInfinity;
            foreach (ref readonly var house in nodes.All)
            {
                if (!house.IsAlive || !house.IsSink || house.Faction != agent.Faction) continue;
                if (house.Housing <= 0) continue;
                var distance = Vector2.DistanceSquared(house.Position, agent.Position);
                if (distance >= nearest) continue;
                nearest = distance;
                agent.Home.House = house.Id;
            }

            if (agent.Home.House.IsValid) MoveIn(ref nodes.Get(agent.Home.House), in agent);
        }
    }

    private static void MoveIn(ref EconomyNode house, in AgentState occupant)
    {
        house.Occupants++;
        house.AppetiteSum += EconomyRates.AppetiteOf(occupant);
    }

    /// <summary>
    /// Binds each sink to the store whose catchment reaches it.
    /// </summary>
    /// <remarks>
    /// §6 inverts the obvious arrangement: a source owns a catchment and whatever is inside it is bound
    /// to it, rather than every consumer asking where the nearest source is. This is that, asked of the
    /// buildings — which do not move, so it is asked only when the set of stores changes — and asked in
    /// <b>seconds</b> rather than metres, so a catchment follows a road and stops at a ridge without
    /// anybody writing that down.
    /// <para>
    /// The budget is quoted at hauler pace and route seconds are at the router's reference pace, so it is
    /// converted before being compared. Skipping that conversion sizes every catchment by the ratio of
    /// the two speeds, which is how the mechanic was switched off the first time.
    /// </para>
    /// </remarks>
    private static void BindCatchments(NodeStore nodes, TravelPrice price)
    {
        var sinks = nodes.MutableSpan();
        for (var i = 0; i < sinks.Length; i++)
        {
            ref var sink = ref sinks[i];
            if (!sink.IsAlive || !sink.IsSink) continue;
            var bound = sink.Supply.IsValid && nodes.Contains(sink.Supply);
            if (bound && sink.SupplyRevision == nodes.Revision) continue;

            sink.SupplyRevision = nodes.Revision;
            sink.Supply = NodeId.None;
            sink.SupplySeconds = 0f;
            var best = float.PositiveInfinity;
            foreach (ref readonly var store in nodes.All)
            {
                if (!store.IsAlive || !store.OwnsCatchment || store.Faction != sink.Faction) continue;
                if (!price(sink.Position, store.Position, HaulerNavigationRadius, out var seconds))
                {
                    continue;
                }

                var budget = store.CatchmentSeconds * HaulerPace / RouteReferencePace;
                if (seconds > budget || seconds >= best) continue;
                best = seconds;
                sink.Supply = store.Id;
                sink.SupplySeconds = seconds;
            }
        }
    }

    /// <summary>The pace a catchment budget is quoted at — a loaded hauler's.</summary>
    internal static float HaulerPace = UnitType.HaulerCart.MaximumSpeed;

    /// <summary>The body a catchment is measured for, which is the cart that would serve it.</summary>
    internal static float HaulerNavigationRadius = UnitType.HaulerCart.NavigationRadius;

    /// <summary>The pace route seconds are denominated at.</summary>
    internal static float RouteReferencePace = AgentDefaults.WorldPace;

    /// <summary>
    /// Households draw for their occupants, out of the store whose catchment reaches them.
    /// </summary>
    /// <remarks>
    /// The only place in the simulation where a resource stops being a physical thing. It is taken from
    /// the store's stock and it does not travel to get here — that is the abstraction §6 chose
    /// deliberately, and it is the whole of the distribution logic: a house inside a catchment is fed, a
    /// house outside every catchment is not, and nothing else needs deciding.
    /// <para>
    /// Draw is per house rather than per body so the sub-unit remainder lives on the node with everything
    /// else that accrues, and so a settlement's demand is a property of its buildings.
    /// </para>
    /// </remarks>
    private void Consume(NodeStore nodes, Season season, float deltaSeconds)
    {
        var consumed = Consumed;
        var unmet = Unmet;
        var sinks = nodes.MutableSpan();
        for (var i = 0; i < sinks.Length; i++)
        {
            ref var sink = ref sinks[i];
            if (!sink.IsAlive || !sink.IsSink || sink.Occupants <= 0) continue;
            foreach (var resource in Resources.All)
            {
                var wanted = EconomyRates.DrawPerSecond(resource, season, sink.AppetiteSum) *
                             deltaSeconds;
                var whole = sink.Pending.Accrue(resource, wanted);
                if (whole <= 0) continue;

                // A household with no store in reach goes without, however full the world is. That is
                // what stops a settlement sprawling past its distribution.
                var available = sink.Supply.IsValid && nodes.Contains(sink.Supply)
                    ? nodes.Get(sink.Supply).Stock[resource]
                    : 0;
                var taken = Math.Min(whole, available);
                if (taken > 0) nodes.Get(sink.Supply).Stock.Add(resource, -taken);
                consumed.Add(resource, taken);
                if (whole > taken) unmet.Add(resource, whole - taken);
            }
        }

        Consumed = consumed;
        Unmet = unmet;
    }

    /// <summary>One journey worth making: move a resource from one node to another.</summary>
    private readonly record struct HaulTask(NodeId Source, NodeId Sink, Resource Resource, float Urgency);

    /// <summary>
    /// Finds stock in the wrong place and gives each journey to the hauler it costs least in seconds.
    /// </summary>
    /// <remarks>
    /// This is the decision the whole routing layer was built to make possible. A haul is priced as
    /// the seconds a given body would spend getting to the source and then to the sink, through
    /// terrain, through roads, and through whatever is currently jammed — so when a lane backs up, the
    /// hauler on the far side of the jam stops being the cheapest and somebody else takes the job.
    /// Nothing here knows what a jam is.
    /// </remarks>
    private void RunBoard(NodeStore nodes, AgentStore agents, TravelPrice price)
    {
        ReleaseStaleHauls(nodes, agents);
        CollectTasks(nodes, (int)(SmallestCapacity(agents) * WorthFetchingShare));
        if (tasks.Count == 0) return;

        idleHaulers.Clear();
        claimed.Clear();
        foreach (ref readonly var agent in agents.All)
        {
            if (!agent.IsAlive || agent.CarryCapacity <= 0) continue;
            // A yard already being collected from is not offered to a second cart. The alternative is
            // reserving units, which is more bookkeeping for the same effect: without either, two carts
            // are sent for the same grain and one of them arrives to an empty yard, which is what half
            // the abandoned jobs in the first run of this were.
            if (agent.Jobs.Assignment.Kind == AssignmentKind.Haul)
            {
                claimed.Add((agent.Jobs.Assignment.Source, agent.Jobs.Assignment.Cargo));
                continue;
            }

            if (agent.Jobs.HasAssignment || agent.Jobs.IsInterrupted) continue;
            idleHaulers.Add(agent.Id);
        }

        if (idleHaulers.Count == 0) return;

        // Most urgent first, and ties broken by node id so two runs of identical code hand out
        // identical jobs. Greedy rather than optimal: an assignment problem solved exactly would be a
        // better answer to a question that changes every two seconds.
        tasks.Sort((first, second) =>
        {
            var byUrgency = second.Urgency.CompareTo(first.Urgency);
            if (byUrgency != 0) return byUrgency;
            var bySource = first.Source.Value.CompareTo(second.Source.Value);
            return bySource != 0 ? bySource : ((int)first.Resource).CompareTo((int)second.Resource);
        });

        foreach (var task in tasks)
        {
            if (idleHaulers.Count == 0) break;
            if (claimed.Contains((task.Source, task.Resource))) continue;
            ref readonly var source = ref nodes.Get(task.Source);
            ref readonly var sink = ref nodes.Get(task.Sink);

            var bestSeconds = float.PositiveInfinity;
            var bestIndex = -1;
            for (var candidate = 0; candidate < idleHaulers.Count; candidate++)
            {
                ref readonly var hauler = ref agents.Get(idleHaulers[candidate]);
                if (!price(hauler.Position, source.Position, hauler.NavigationRadius, out var toSource))
                {
                    continue;
                }

                if (!price(source.Position, sink.Position, hauler.NavigationRadius, out var leg))
                {
                    continue;
                }

                var seconds = toSource + leg;
                if (seconds >= bestSeconds) continue;
                bestSeconds = seconds;
                bestIndex = candidate;
            }

            if (bestIndex < 0) continue;
            ref var chosen = ref agents.Get(idleHaulers[bestIndex]);
            JobSystem.Assign(ref chosen, Assignment.Haul(
                task.Source, source.Position, task.Sink, sink.Position, task.Resource, HandoverSeconds));
            claimed.Add((task.Source, task.Resource));
            idleHaulers.RemoveAt(bestIndex);
            HaulsAssigned++;
        }
    }

    /// <summary>Removes heaps that have been carried away, so an empty pile is not a permanent node.</summary>
    private static void SweepEmptyPiles(NodeStore nodes)
    {
        for (var slot = 0; slot < nodes.Count; slot++)
        {
            var id = new NodeId(slot);
            if (!nodes.Contains(id)) continue;
            ref readonly var node = ref nodes.Get(id);
            if (node.IsPile && node.Stock.Total <= 0) nodes.Remove(id);
        }
    }

    /// <summary>Drops a haul whose source has emptied or whose sink has filled.</summary>
    private void ReleaseStaleHauls(NodeStore nodes, AgentStore agents)
    {
        var bodies = agents.MutableSpan();
        for (var i = 0; i < bodies.Length; i++)
        {
            ref var agent = ref bodies[i];
            if (!agent.IsAlive || agent.Jobs.Assignment.Kind != AssignmentKind.Haul) continue;
            // A body holding cargo finishes its delivery whatever the board thinks, or the units it is
            // carrying would have nowhere to go and conservation would have something to say about it.
            if (agent.Jobs.CarriedUnits > 0) continue;

            var assignment = agent.Jobs.Assignment;
            var stale = !nodes.Contains(assignment.Source) || !nodes.Contains(assignment.Sink);
            if (!stale)
            {
                ref readonly var source = ref nodes.Get(assignment.Source);
                ref readonly var sink = ref nodes.Get(assignment.Sink);
                stale = source.Stock[assignment.Cargo] <= 0 || sink.RoomFor(assignment.Cargo) <= 0;
            }

            if (!stale) continue;
            JobSystem.Assign(ref agent, Assignment.None);
            HaulsAbandoned++;
        }
    }

    /// <summary>
    /// Every journey the settlement would benefit from, in node order.
    /// </summary>
    /// <remarks>
    /// Two shapes, which are §6's whole network: a producer's output belongs in a store, and a store
    /// that is running low should be topped up from one that is nearly full. The high and low water
    /// marks are what stop the second shape shuffling stock back and forth forever.
    /// </remarks>
    private void CollectTasks(NodeStore nodes, int worthLoad)
    {
        tasks.Clear();
        foreach (ref readonly var source in nodes.All)
        {
            if (!source.IsAlive) continue;
            foreach (var resource in Resources.All)
            {
                if (source.Stock[resource] <= 0) continue;
                var worthFetching = source.Stock[resource] >= worthLoad ||
                                    source.Stock[resource] >= source.Capacity * HighWater;
                // A heap on the ground is always worth collecting, however small: left alone it is not
                // stock in the wrong place, it is stock nobody has. What stops a cart crossing the map
                // for three units of grain is not a threshold, it is that the trip is priced and the
                // heap's urgency is proportional to its size.
                var giving = source.IsPile
                    ? true
                    : source.Produces_
                        ? worthFetching
                        : source.Stores && source.Stock[resource] >= source.Capacity * HighWater;
                if (!giving) continue;

                var bestSink = NodeId.None;
                var bestNeed = 0f;
                foreach (ref readonly var sink in nodes.All)
                {
                    if (!sink.IsAlive || !sink.Stores || sink.Id == source.Id) continue;
                    // A heap belongs to nobody, so anybody's store is a valid destination for it. That
                    // one relaxation is the whole of looting: an enemy's dropped grain is collected by
                    // the same board, priced the same way, with no rule about theft anywhere.
                    if (!source.IsPile && sink.Faction != source.Faction) continue;
                    var room = sink.RoomFor(resource);
                    if (room <= 0) continue;
                    if (source.Stores && sink.Stock[resource] > sink.Capacity * LowWater) continue;

                    // Emptiest store wins, so the settlement spreads its stock rather than topping up
                    // whichever node happens to be first.
                    var need = 1f - sink.Stock[resource] / (float)Math.Max(1, sink.Capacity);
                    if (need <= bestNeed) continue;
                    bestNeed = need;
                    bestSink = sink.Id;
                }

                if (!bestSink.IsValid) continue;
                // A producer's yard filling up is more urgent than a store being uneven, and a fuller
                // yard is more urgent than an emptier one.
                var fullness = source.Stock[resource] / (float)Math.Max(1, source.Capacity);
                var urgency = source.IsPile
                    // Bigger heaps first, and all of them below a yard about to overflow: a farm that
                    // stops producing costs more than grain sitting still costs.
                    ? 0.5f + 0.5f * MathF.Min(1f, source.Stock[resource] / MathF.Max(1f, worthLoad))
                    : source.Produces_
                        ? 1f + fullness
                        : bestNeed;
                tasks.Add(new HaulTask(source.Id, bestSink, resource, urgency));
            }
        }
    }

    /// <summary>
    /// The smallest cart in the world, which is what a load worth fetching is measured against.
    /// </summary>
    /// <remarks>
    /// The largest was tried and it is badly wrong: adding one 200-unit wagon to a settlement of
    /// 40-unit carts raised the bar to seventy units, no yard of a hundred and fifty ever reached it
    /// early enough, and the whole network went from 342 journeys a year to 163 while the settlement
    /// starved. A load worth the smallest cart's trip is worth dispatching; which cart goes is the
    /// board's decision and it is made in seconds.
    /// </remarks>
    private static int SmallestCapacity(AgentStore agents)
    {
        var smallest = int.MaxValue;
        foreach (ref readonly var agent in agents.All)
        {
            if (agent.IsAlive && agent.CarryCapacity > 0)
            {
                smallest = Math.Min(smallest, agent.CarryCapacity);
            }
        }

        return smallest == int.MaxValue ? 1 : smallest;
    }

    /// <summary>The node within <paramref name="radius"/> of a point, or none.</summary>
    public static NodeId NodeAt(NodeStore nodes, Vector2 position, float radius)
    {
        var best = NodeId.None;
        var bestDistance = radius * radius;
        foreach (ref readonly var node in nodes.All)
        {
            if (!node.IsAlive) continue;
            var distance = Vector2.DistanceSquared(node.Position, position);
            if (distance > bestDistance) continue;
            bestDistance = distance;
            best = node.Id;
        }

        return best;
    }

    internal void Write(WorldWriter writer)
    {
        writer.Float(boardCooldown);
        writer.Long(HaulsAssigned);
        writer.Long(HaulsAbandoned);
        foreach (var resource in Resources.All)
        {
            writer.Long(Produced[resource]);
            writer.Long(Consumed[resource]);
            writer.Long(Seeded[resource]);
            writer.Long(Unmet[resource]);
        }
    }

    internal void Read(WorldReader reader)
    {
        boardCooldown = reader.Float();
        HaulsAssigned = reader.Long();
        HaulsAbandoned = reader.Long();
        var produced = default(ResourceTotals);
        var consumed = default(ResourceTotals);
        var seeded = default(ResourceTotals);
        var unmet = default(ResourceTotals);
        foreach (var resource in Resources.All)
        {
            produced[resource] = reader.Long();
            consumed[resource] = reader.Long();
            seeded[resource] = reader.Long();
            unmet[resource] = reader.Long();
        }

        Produced = produced;
        Consumed = consumed;
        Seeded = seeded;
        Unmet = unmet;
    }
}
