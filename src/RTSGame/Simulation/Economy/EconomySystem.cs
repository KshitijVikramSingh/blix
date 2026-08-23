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
    public long Stone;

    public long this[Resource resource]
    {
        readonly get => resource switch
        {
            Resource.Grain => Grain,
            Resource.Wood => Wood,
            Resource.Stone => Stone,
            _ => throw new ArgumentOutOfRangeException(
                nameof(resource), resource, "this store has no field for that resource"),
        };
        set
        {
            switch (resource)
            {
                case Resource.Grain: Grain = value; break;
                case Resource.Wood: Wood = value; break;
                case Resource.Stone: Stone = value; break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(resource), resource, "this store has no field for that resource");
            }
        }
    }

    /// <summary>
    /// How far off the books are in total, counting each resource's error as an error.
    /// </summary>
    /// <remarks>
    /// Absolute values, because a drift is a fault whichever direction it goes and two faults must not cancel
    /// into a clean bill. A plain sum reported zero for one unit appearing here and one vanishing there.
    /// </remarks>
    public readonly long Fault
    {
        get
        {
            var total = 0L;
            foreach (var resource in Resources.All) total += Math.Abs(this[resource]);
            return total;
        }
    }

    /// <summary>Whether every resource is exactly zero. See <see cref="NodeStock.IsZero"/>.</summary>
    public readonly bool IsZero => Grain == 0 && Wood == 0 && Stone == 0;

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

    /// <summary>
    /// Seconds a producer works before carrying in whatever it has, if its hands do not fill first.
    /// </summary>
    /// <remarks>
    /// A fallback rather than the rule: a shift normally ends because the body is full. It exists so the
    /// last few units of a finished phase get carried in rather than waiting for a window that has closed,
    /// and so a body working a field that yields nothing still comes home occasionally.
    /// </remarks>
    internal static float WorkShiftSeconds = 45f;

    /// <summary>Seconds a hauler spends loading or unloading at a node.</summary>
    /// <remarks>
    /// <b>Very nearly nothing, and it was four seconds.</b> The argument for four is written down because
    /// it is a real one and it lost: a hauling network with instant transfer has no reason to want more
    /// haulers than routes, and the queue at a busy granary is one of the things the congestion field
    /// exists to price. Four seconds against a leg of sixty is only a tenth of the round trip.
    /// <para>
    /// What beat it is what a settlement <em>looks like</em>. Every transfer pays this, several times a
    /// round trip, at a farm, at a tree, at a granary, at a depot, and at a building site — so watching
    /// the settlement work meant watching people stand still at the exact moments they were supposed to be
    /// getting something done. A tenth of a round trip spent motionless is a tenth of the game.
    /// </para>
    /// <para>
    /// Not exactly zero: one tick is 33 ms and a transfer that completes on the tick of arrival makes the
    /// arrival itself unobservable, which matters for anything watching a body's state. A quarter of a
    /// second is under the eye's threshold and still a distinct step in the trace. On a slider, so the
    /// queues can be brought back — see <c>SettlementSettings</c>.
    /// </para>
    /// </remarks>
    internal static float HandoverSeconds = 0.25f;

    /// <summary>
    /// How near a body has to be to a building's wall to be working it, as a multiple of its radius.
    /// </summary>
    /// <remarks>
    /// The same figure the jobs layer settles a crowd at, and deliberately the same: a hand is a body the
    /// jobs layer considers to be <em>at</em> the node, and two definitions of that would drift. It was a
    /// flat four metres from the node's centre, which was fine while a building was one 1.5 m cell and
    /// stopped being fine the moment they were 4.5 and 7.5 — a hand standing at the corner of its own
    /// woodcutter was 4.8 m from the middle of it and did not count, so the settlement quietly lost two
    /// of nineteen pairs of hands and a seventh of its wood.
    /// </remarks>
    internal static float WorkReachShare => JobDefaults.CrowdedTouchShare;

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
    private readonly HashSet<NodeId> drawnOn = new();
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

    /// <summary>Standing routes that ran their source dry, which is a route ending as designed.</summary>
    public long RoutesFinished { get; private set; }

    /// <summary>Seconds until the board next looks for work, which two runs have to agree on.</summary>
    internal float BoardCooldown => boardCooldown;

    /// <summary>Records stock put into the world by something other than production.</summary>
    public void RecordSeeded(Resource resource, int units)
    {
        var seeded = Seeded;
        seeded.Add(resource, units);
        Seeded = seeded;
    }

    /// <summary>
    /// Records stock that has left the world by being turned into something.
    /// </summary>
    /// <remarks>
    /// Consumption is normally a household eating, and there is a second way now: a cart. Timber spent on
    /// a handcart has not moved somewhere, it has <em>stopped being timber</em>, so it belongs on the same
    /// side of the identity as a loaf. Recording it anywhere else — or not recording it — is a unit going
    /// missing, and the drift check would name the tick it happened on.
    /// </remarks>
    public void RecordConsumed(Resource resource, int units)
    {
        var consumed = Consumed;
        consumed.Add(resource, units);
        Consumed = consumed;
    }

    /// <summary>The nearest store of this faction holding at least <paramref name="units"/>.</summary>
    public static NodeId NearestStoreWith(
        NodeStore nodes,
        Resource resource,
        int units,
        FactionId faction,
        Vector2 from)
    {
        var best = NodeId.None;
        var bestDistance = float.PositiveInfinity;
        foreach (ref readonly var node in nodes.All)
        {
            if (!node.IsAlive || !node.Stores || node.Faction != faction) continue;
            if (node.Stock[resource] < units) continue;
            var distance = Vector2.DistanceSquared(node.Position, from);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = node.Id;
        }

        return best;
    }

    /// <summary>The nearest store of this faction with room for a resource, from a point.</summary>
    /// <remarks>
    /// Nearest in straight line rather than in route seconds, deliberately: a producer walking its own
    /// output in is making a short trip a dozen times an hour, and paying for a routing query each time to
    /// choose between two stores it can see would cost more than the walk. Hauling, which is the long leg
    /// and the one worth thinking about, is priced properly.
    /// </remarks>
    public static NodeId NearestStoreWithRoom(
        NodeStore nodes,
        Resource resource,
        FactionId faction,
        Vector2 from)
    {
        var best = NodeId.None;
        var bestDistance = float.PositiveInfinity;
        foreach (ref readonly var node in nodes.All)
        {
            if (!node.IsAlive || !node.Stores || node.Faction != faction) continue;
            if (node.RoomFor(resource) <= 0) continue;
            var distance = Vector2.DistanceSquared(node.Position, from);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = node.Id;
        }

        return best;
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
        // What the settlement has, not what is growing in front of it. A woodland in reach is
        // twenty-five thousand units of standing timber, and counting it here would answer "how long
        // will the stores last" with a statement about the forest.
        var stored = nodes.TotalHeld()[resource];
        var draw = 0f;
        foreach (ref readonly var node in nodes.All)
        {
            if (!node.IsAlive || !node.IsSink) continue;
            draw += EconomyRates.DrawPerSecond(resource, season, node.AppetiteSum);
        }

        var produced = 0f;
        if (resource == Resource.Grain)
        {
            foreach (ref readonly var node in nodes.All)
            {
                if (!node.IsAlive || node.Kind != NodeKind.Farm) continue;
                // A field's contribution is what it will still yield this year spread over what is left of
                // it, which is the honest answer to "how long will the stores last": a field standing
                // unreaped in harvest is income, and the same field in winter is not.
                var remaining = EconomyRates.FullYearOf(in node) * CropCycle.PotentialOf(in node) *
                                (1f - (CropCycle.ReapTargetOf(in node) <= 0f
                                    ? 1f
                                    : node.ReapWork / CropCycle.ReapTargetOf(in node)));
                produced += remaining / MathF.Max(1f, WorldCalendar.YearSeconds);
            }
        }
        else
        {
            // Wood income is however many axes are actually swinging. Not a rate a building has and not
            // a headcount of people who call themselves woodcutters: a cutter walking a load in, or one
            // whose trees have run out, is contributing nothing this second and the figure should say so.
            produced += DepositWorkersAtWork(agents, resource) * Deposits.TakePerSecond(resource);
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
        CalendarDate date,
        float deltaSeconds,
        TravelPrice price,
        Birth? born = null,
        Departure? left = null,
        Felled? felled = null)
    {
        var season = date.Season;
        CountHands(nodes, agents);
        RollCrops(nodes, date.Year);
        // Everything that is produced is produced by labour standing at the thing, now that wood is
        // trees. There is no longer a pass in which a node accrues output on its own.
        WorkSites(nodes, agents, season, deltaSeconds);
        Raise(nodes, deltaSeconds);
        BindHomes(nodes, agents, deltaSeconds);
        BindCatchments(nodes, price);
        Consume(nodes, season, deltaSeconds);
        // After consuming, because whether the settlement can afford another mouth is a question about
        // what is left once everybody has eaten.
        Populate(nodes, agents, season, deltaSeconds, born, left);

        boardCooldown -= deltaSeconds;
        if (boardCooldown > 0f) return;
        boardCooldown = BoardIntervalSeconds;
        SweepSpentNodes(nodes, felled);
        RunBoard(nodes, agents, price);
    }

    /// <summary>Seconds between two points for a body of a given navigation radius.</summary>
    internal delegate bool TravelPrice(Vector2 from, Vector2 to, float navigationRadius, out float seconds);

    /// <summary>Puts a new person into the world beside their house.</summary>
    /// <remarks>
    /// A delegate for the same reason <see cref="TravelPrice"/> is one: spawning a body needs the terrain,
    /// the placement grid and the collider world, and this file has none of them and should not. It decides
    /// <em>that</em> somebody is born and where; the world decides what a body is.
    /// </remarks>
    internal delegate void Birth(NodeId house, Vector2 position);

    /// <summary>Takes somebody out of the world, for good.</summary>
    internal delegate void Departure(AgentId body);

    /// <summary>A tree has come down, so the ground around it may have opened.</summary>
    internal delegate void Felled(Vector2 where);

    /// <summary>
    /// Grows the settlement where it can afford to, and loses people where it cannot.
    /// </summary>
    /// <remarks>
    /// <b>A villager appears at a house with room, inside the reach of a store that can feed them.</b>
    /// Every clause is load-bearing and every one of them is something the player built: the house is a
    /// building, the room in it is how big that building is, and the reach is where it was put relative to
    /// a granary. Nothing here is a timer.
    /// <para>
    /// Readiness is settlement-wide and continuous — a settlement with half a winter put by grows at half
    /// speed — so the brake is food and the cap is housing, which is §6's whole population model.
    /// </para>
    /// <para>
    /// The one thing that has to be careful is <em>which</em> body emigrates: the lowest id living in that
    /// house, so two runs of the same world lose the same person. Picking "the nearest" or "the first
    /// found" would be a divergence the fingerprint would catch a few thousand ticks later, somewhere
    /// unrelated.
    /// </para>
    /// </remarks>
    private void Populate(
        NodeStore nodes,
        AgentStore agents,
        Season season,
        float deltaSeconds,
        Birth? born,
        Departure? left)
    {
        var held = nodes.TotalHeld();
        var mouths = 0f;
        foreach (ref readonly var sink in nodes.All)
        {
            if (!sink.IsAlive || !sink.IsSink) continue;
            mouths += sink.AppetiteSum;
        }

        Readiness = Population.Readiness(held.Grain, held.Wood, mouths, season);

        var houses = nodes.MutableSpan();
        for (var i = 0; i < houses.Length; i++)
        {
            ref var house = ref houses[i];
            if (!house.IsAlive || !house.IsSink) continue;

            // Privation first, because a house losing people is not a house gaining them, and a household
            // that is starving should not be accruing growth from a settlement-wide readiness figure.
            if (house.Privation >= Population.PrivationSeconds && house.Occupants > 0)
            {
                var leaving = LowestOccupant(agents, house.Id);
                if (leaving.Value >= 0)
                {
                    house.Privation = 0f;
                    house.Growth = 0f;
                    Emigrated++;
                    left?.Invoke(leaving);
                    continue;
                }
            }

            // A house outside every catchment cannot feed anybody, so nobody is born into it however
            // rich the settlement is. That is the same rule that makes such a house go hungry, applied
            // to the other direction.
            if (house.Housing <= 0 || !house.Supply.IsValid || !nodes.Contains(house.Supply))
            {
                continue;
            }

            if (house.Privation > 0f) continue;
            house.Growth += deltaSeconds * Readiness;
            if (house.Growth < Population.PersonSeconds) continue;
            house.Growth -= Population.PersonSeconds;
            Born++;
            // Beside the house rather than in it. The world moves them off built ground if the spot is
            // occupied, which it usually is — a house is a 4.5 m building.
            born?.Invoke(house.Id, house.Position);
        }
    }

    /// <summary>The lowest-numbered body living in a house, or none.</summary>
    private static AgentId LowestOccupant(AgentStore agents, NodeId house)
    {
        foreach (ref readonly var agent in agents.All)
        {
            if (agent.IsAlive && agent.Home.House == house) return agent.Id;
        }

        return new AgentId(-1);
    }

    /// <summary>How ready the settlement is to feed one more mouth, from nothing to all of it.</summary>
    public float Readiness { get; private set; }

    /// <summary>People born since the world began.</summary>
    public long Born { get; private set; }

    /// <summary>People who left because their household went hungry too long.</summary>
    public long Emigrated { get; private set; }

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
            if (!agent.IsAlive) continue;
            if (agent.Jobs.Assignment.Kind is not (AssignmentKind.Hold or AssignmentKind.Work)) continue;
            if (agent.Jobs.Activity == ActivityKind.None || agent.Jobs.IsInterrupted) continue;
            // <b>The site this body was assigned to, and then whether it is actually standing there.</b>
            // Both halves matter. A hauler unloading in a farmyard is emphatically not a farmhand, which
            // is what counting anybody nearby produced — seventeen pairs of hands at sixteen farms — and a
            // body still walking to its field is not working it yet, which is what counting the assignment
            // alone would produce.
            //
            // It used to find the nearest node to the body by scanning every node in the world, which was
            // fine at twenty and is not at ten thousand: nineteen scans of a ten-thousand-element struct
            // array is thirty-odd megabytes of streaming per tick, and the settlement gate went from a
            // fifth of a millisecond a tick to one and three quarters the moment the forest got dense.
            // Asking the assignment is O(1) and is also the sharper question.
            var site = agent.Jobs.Assignment.Source;
            if (!nodes.Contains(site)) continue;
            ref var node = ref nodes.Get(site);
            // A field and a tree both count, because both are places labour is spent at. A store is not:
            // a cutter putting a load down in a granary is not employed by the granary.
            if (!node.IsWorkSite) continue;
            if (IsStandingAt(in node, in agent)) node.Hands++;
        }
    }

    /// <summary>Whether this body is close enough to a node's wall to be working it.</summary>
    /// <remarks>
    /// Measured to the building's wall rather than to its centre, so the answer does not depend on how big
    /// the building is — which a distance from the centre necessarily does, and got wrong the moment
    /// buildings stopped all being one cell. Squared throughout; there is no reason to take a root to
    /// compare against a threshold.
    /// </remarks>
    private static bool IsStandingAt(in EconomyNode node, in AgentState agent)
    {
        var reach = agent.Radius * WorkReachShare + JobDefaults.TouchSlack;
        var half = node.HalfExtent;
        var outX = MathF.Max(MathF.Abs(agent.Position.X - node.Position.X) - half, 0f);
        var outZ = MathF.Max(MathF.Abs(agent.Position.Y - node.Position.Y) - half, 0f);
        return outX * outX + outZ * outZ <= reach * reach;
    }

    /// <summary>
    /// Starts each field's year over when it finds itself in one it has not worked.
    /// </summary>
    /// <remarks>
    /// Pulled rather than pushed. The calendar is derived from the tick and nothing is notified when a
    /// season turns, so a field checks whether the year it last worked is the year it is in — which needs
    /// no event, survives a save landing mid-spring, and cannot be missed by a tick that happened to be
    /// spent elsewhere.
    /// </remarks>
    private static void RollCrops(NodeStore nodes, int year)
    {
        // The year is passed in rather than asked for. Asking WorldCalendar for it without a time is
        // asking what year second zero was in, which is always the first one — so every field decided it
        // had already worked the current year, never reset, and the second harvest reaped a crop the first
        // one had already taken. Two years of production came to exactly one year's worth, which is the
        // kind of number that gives itself away.
        var fields = nodes.MutableSpan();
        for (var i = 0; i < fields.Length; i++)
        {
            ref var field = ref fields[i];
            if (!field.IsAlive || field.Kind != NodeKind.Farm) continue;
            if (field.CycleYear == year) continue;
            field.CycleYear = year;
            field.PrepareWork = 0f;
            field.MaintainWork = 0f;
            field.ReapWork = 0f;
        }
    }

    /// <summary>
    /// Puts every pair of hands to work on whatever it is standing at.
    /// </summary>
    /// <remarks>
    /// <b>The only place output comes from.</b> A field is worked on the season's window and a tree is
    /// cut, and both are the same shape: labour is spent at a place, and what it yields goes straight
    /// into the hands of whoever spent the labour, to be walked in. Nothing in the economy accrues on
    /// its own any more.
    /// <para>
    /// A field's labour is accumulated <em>per field</em>, because that is what the three windows are
    /// counted in — 900 seconds of breaking, and the field does not care whether that is one pair of
    /// hands all spring or three for a third of it. A tree's is not accumulated at all: cutting takes
    /// wood out of a finite standing stock, so the tree gets smaller instead of the counter getting
    /// bigger, and when it is empty it is gone.
    /// </para>
    /// </remarks>
    private void WorkSites(NodeStore nodes, AgentStore agents, Season season, float deltaSeconds)
    {
        var phase = CropCycle.PhaseOf(season);
        var bodies = agents.MutableSpan();
        for (var i = 0; i < bodies.Length; i++)
        {
            ref var body = ref bodies[i];
            if (!body.IsAlive) continue;
            if (body.Jobs.Assignment.Kind != AssignmentKind.Work || body.Jobs.IsInterrupted) continue;
            if (body.Jobs.Leg % 2 != 0 || body.Jobs.Activity == ActivityKind.None) continue;

            var siteId = body.Jobs.Assignment.Source;
            if (!nodes.Contains(siteId)) continue;
            // Working means being there. A body still walking to the site is not breaking any ground
            // and not cutting any wood.
            if (!JobSystem.IsWorking(in body)) continue;
            // Both deposits take the same branch: a shift at a deposit is labour against a stock, and which
            // stock it is comes off the node. Left as a Tree-only test, a quarrier stood at its rock all year
            // and fell through to the crop code, which returned it as "not a farm" — no error, no work, and a
            // stone column of zeros that read exactly like an out-of-reach quarry.
            if (nodes.Get(siteId).IsNaturalDeposit)
            {
                Cut(ref nodes.Get(siteId), ref body, deltaSeconds);
                continue;
            }

            if (phase == CropPhase.Rest) continue;
            var produced = Produced;
            ref var field = ref nodes.Get(siteId);
            if (field.Kind != NodeKind.Farm) continue;
            if (!CropCycle.WantsWork(in field, phase))
            {
                // The window is answered. Carry in whatever is in hand; if there is nothing, wait here —
                // a farmer with no work to do belongs at its field, not commuting.
                if (body.Jobs.CarriedUnits > 0) JobSystem.EndShift(ref body);
                continue;
            }

            switch (phase)
            {
                case CropPhase.Prepare:
                    field.PrepareWork = MathF.Min(
                        CropCycle.PrepareLabour, field.PrepareWork + deltaSeconds);
                    break;
                case CropPhase.Maintain:
                    field.MaintainWork = MathF.Min(
                        CropCycle.MaintainLabour, field.MaintainWork + deltaSeconds);
                    break;
                case CropPhase.Reap:
                    var target = CropCycle.ReapTargetOf(in field);
                    var before = field.ReapWork;
                    field.ReapWork = MathF.Min(target, field.ReapWork + deltaSeconds);
                    var earned = field.Pending.Accrue(
                        Resource.Grain,
                        (field.ReapWork - before) * EconomyRates.ReapedPerSecond(in field));
                    if (earned > 0)
                    {
                        // Straight into the reaper's hands. It never sits in the field: grain a body is
                        // holding is grain the settlement has not got yet, which is the whole reason the
                        // walk is a cost.
                        body.Jobs.Carrying = Resource.Grain;
                        body.Jobs.CarriedUnits += earned;
                        produced.Add(Resource.Grain, earned);
                    }

                    if (body.Jobs.CarriedUnits >= body.CarryCapacity) JobSystem.EndShift(ref body);
                    break;
            }

            Produced = produced;
        }
    }

    /// <summary>
    /// Puts up the buildings that have their timber and somebody standing at them.
    /// </summary>
    /// <remarks>
    /// Per site from the hands at it, rather than per body like a crop: a building has no output, so there
    /// is nothing to attribute to whoever did the work and four builders are simply four times the work.
    /// <para>
    /// The timber is <b>consumed on completion</b> and not before, which is one decision worth stating.
    /// Spending it gradually would be more physical and would mean a half-built house had half its timber
    /// in it and half of it gone — and then abandoning a site would have destroyed material, so there would
    /// have to be a rule about salvage. Consuming it at the end means an unfinished site is simply timber
    /// standing on the ground where somebody left it, which is what conservation already knows how to
    /// describe and what a player would expect if they knocked the site down.
    /// </para>
    /// </remarks>
    private void Raise(NodeStore nodes, float deltaSeconds)
    {
        var consumed = Consumed;
        var sites = nodes.MutableSpan();
        for (var i = 0; i < sites.Length; i++)
        {
            ref var site = ref sites[i];
            if (!site.IsAlive || site.IsBuilt) continue;
            // Materials first: hands standing at a site with no timber are hands doing nothing, which is
            // the difference between a hauling problem and a labour problem and the report says which.
            if (site.WantsMaterials || site.Hands <= 0) continue;

            site.BuildWork += site.Hands * deltaSeconds;
            if (!site.IsBuilt) continue;

            // Finished. Every material stops being material, so it leaves the world through the same door a
            // loaf does and the identity still closes — all of them, because consuming only the timber would
            // have left the stone sitting in a finished building as stock nobody can reach and conservation
            // would have been right about it forever.
            var cost = Construction.CostFor(site.Kind);
            foreach (var resource in Resources.All)
            {
                if (cost[resource] <= 0) continue;
                site.Stock.Add(resource, -cost[resource]);
                consumed.Add(resource, cost[resource]);
            }
            site.BuildWork = Construction.LabourFor(site.Kind);
            Raised++;
        }

        Consumed = consumed;
    }

    /// <summary>Buildings finished since the world began.</summary>
    public long Raised { get; private set; }

    /// <summary>
    /// Takes wood out of a tree and puts it in the cutter's hands.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is produced here, and that is the whole of Stage B.</b> The wood was seeded into the
    /// world standing in this tree; cutting moves it from the tree's stock onto the body's back, so
    /// conservation sees one unit leave a store and arrive on a back and has no production term to
    /// reconcile. A settlement's total wood is bounded by its forest, permanently, which is the pressure
    /// the map is supposed to apply.
    /// <para>
    /// The labour accumulator lives on the tree, so two cutters on one trunk fell it in half the time,
    /// and whatever will not fit in the hands is put back rather than dropped: a body with room for two
    /// more units does not silently destroy the third.
    /// </para>
    /// </remarks>
    private static void Cut(ref EconomyNode deposit, ref AgentState body, float deltaSeconds)
    {
        // Which resource this is comes off the deposit rather than off the assignment, because the deposit is
        // the thing that has it. A body sent to an outcrop cannot come back with wood.
        var resource = Deposits.ResourceOf(deposit.Kind);
        var room = body.CarryCapacity - body.Jobs.CarriedUnits;
        if (room <= 0 || deposit.Stock[resource] <= 0)
        {
            JobSystem.EndShift(ref body);
            return;
        }

        var whole = deposit.Pending.Accrue(resource, Deposits.TakePerSecond(resource) * deltaSeconds);
        if (whole > 0)
        {
            var taken = Math.Min(whole, Math.Min(room, deposit.Stock[resource]));
            if (taken > 0)
            {
                deposit.Stock[resource] -= taken;
                body.Jobs.Carrying = resource;
                body.Jobs.CarriedUnits += taken;
            }

            // Worked but not carried: the swing still happened, so the labour stays on the deposit.
            if (whole > taken) deposit.Pending[resource] += whole - taken;
        }

        if (deposit.Stock[resource] <= 0 || body.Jobs.CarriedUnits >= body.CarryCapacity)
        {
            JobSystem.EndShift(ref body);
        }
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

        // An empty house has no privation, because privation is something a household is going through
        // and there is no household. Without this it keeps whatever it had when the last occupant left,
        // and the next person to move in inherits a full measure of somebody else's famine and walks
        // straight back out again.
        for (var i = 0; i < houses.Length; i++)
        {
            if (houses[i].IsSink && houses[i].Occupants == 0) houses[i].Privation = 0f;
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
            var wentWithout = false;
            foreach (var resource in Resources.All)
            {
                var wanted = EconomyRates.DrawPerSecond(resource, season, sink.AppetiteSum) *
                             deltaSeconds;
                // A household with no store in reach goes without, however full the world is. That is
                // what stops a settlement sprawling past its distribution.
                var available = sink.Supply.IsValid && nodes.Contains(sink.Supply)
                    ? nodes.Get(sink.Supply).Stock[resource]
                    : 0;
                // <b>Going without is a state, not an event.</b> Privation used to be set only on the
                // ticks a whole unit of demand actually came due and failed — which is about one tick in a
                // hundred, since a household draws a fraction of a unit per tick and the rest accumulates
                // in Pending. So a settlement whose wood ran out for a whole year accrued a minute of
                // privation instead of a year of it, and nobody ever left. What the household is actually
                // experiencing is that the store it draws from is empty and it wants something, which is
                // true every tick of the famine.
                if (wanted > 0f && available <= 0) wentWithout = true;

                var whole = sink.Pending.Accrue(resource, wanted);
                if (whole <= 0) continue;
                var taken = Math.Min(whole, available);
                if (taken > 0) nodes.Get(sink.Supply).Stock.Add(resource, -taken);
                consumed.Add(resource, taken);
                if (whole > taken) unmet.Add(resource, whole - taken);
            }

            // Privation is measured in seconds gone without, on the household that went without. It
            // drains faster than it fills, so a settlement that fixes its supply stops losing people
            // rather than going on losing them for as long as the shortage lasted.
            sink.Privation = MathF.Max(
                0f,
                sink.Privation + (wentWithout
                    ? deltaSeconds
                    : -deltaSeconds * Population.PrivationRecovery));
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
        MarkStoresSomebodyEatsFrom(nodes);
        CollectTasks(nodes, (int)(SmallestCapacity(agents) * WorthFetchingShare));
        if (tasks.Count == 0) return;

        idleHaulers.Clear();
        claimed.Clear();
        foreach (ref readonly var agent in agents.All)
        {
            // Only bodies that actually have a cart. Every villager has a carry capacity now — a reaper
            // walks its own crop in — so capacity is no longer what makes somebody a hauler, and reading
            // it as one would have put the board's stranded-stock journeys on farmhands.
            if (!agent.IsAlive || !agent.HasCart) continue;
            // A yard already being collected from is not offered to a second cart. The alternative is
            // reserving units, which is more bookkeeping for the same effect: without either, two carts
            // are sent for the same grain and one of them arrives to an empty yard, which is what half
            // the abandoned jobs in the first run of this were.
            if (agent.Jobs.Assignment.MovesCargo)
            {
                // A standing route counts as a claim on its source too: two carters sent for the same
                // stock is the same waste whoever sent them, and the player's route is the one the board
                // should defer to.
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
                task.Source, source.Position, task.Sink, sink.Position, task.Resource, HandoverSeconds,
                source.FootprintRadius, sink.FootprintRadius));
            claimed.Add((task.Source, task.Resource));
            idleHaulers.RemoveAt(bestIndex);
            HaulsAssigned++;
        }
    }

    /// <summary>Bodies currently standing at a deposit of this resource, working it.</summary>
    /// <remarks>
    /// Hands actually on the rock or the trunk, rather than people who hold the job — which is the
    /// distinction the wood income figure was built on and it matters more for stone, where the walk is
    /// longer and a larger share of a quarrier's day is spent on the road rather than at the face.
    /// </remarks>
    public static int DepositWorkersAtWork(AgentStore agents, Resource resource)
    {
        var working = 0;
        foreach (ref readonly var agent in agents.All)
        {
            if (!agent.IsAlive || agent.Jobs.IsInterrupted) continue;
            if (agent.Jobs.Assignment.Kind != AssignmentKind.Work) continue;
            if (agent.Jobs.Assignment.Cargo != resource) continue;
            if (agent.Jobs.Leg % 2 != 0 || !JobSystem.IsWorking(in agent)) continue;
            working++;
        }

        return working;
    }

    /// <summary>Bodies currently standing at a tree with an axe in them.</summary>
    public static int CuttersAtWork(AgentStore agents) =>
        DepositWorkersAtWork(agents, Resource.Wood);

    /// <summary>Bodies whose standing job is to work a deposit, wherever they are in the round trip.</summary>
    public static int DepositWorkers(AgentStore agents, Resource resource)
    {
        var workers = 0;
        foreach (ref readonly var agent in agents.All)
        {
            if (!agent.IsAlive) continue;
            if (agent.Jobs.Assignment.Kind != AssignmentKind.Work) continue;
            if (agent.Jobs.Assignment.Cargo == resource) workers++;
        }

        return workers;
    }

    /// <summary>Bodies whose standing job is to cut wood, wherever they are in the round trip.</summary>
    public static int Cutters(AgentStore agents) => DepositWorkers(agents, Resource.Wood);

    /// <summary>
    /// Removes what has been used up: heaps that have been carried away and trees that have been felled.
    /// </summary>
    /// <remarks>
    /// Felling is a sweep rather than an event at the moment the last unit is cut, for the same reason a
    /// field rolls its year over by noticing rather than by being told: no notification to miss, and it
    /// survives a save landing on the tick the axe went in. A tree at zero is standing dead for at most
    /// two seconds, which nobody can see and nothing depends on.
    /// </remarks>
    private static void SweepSpentNodes(NodeStore nodes, Felled? felled)
    {
        for (var slot = 0; slot < nodes.Count; slot++)
        {
            var id = new NodeId(slot);
            if (!nodes.Contains(id)) continue;
            ref readonly var node = ref nodes.Get(id);
            if (!(node.IsPile || node.IsNaturalDeposit) || node.Stock.Total > 0) continue;
            var standing = node.IsStanding;
            var where = node.Position;
            nodes.Remove(id);
            // A felled tree may have been the one holding a patch of ground closed. Told rather than
            // discovered, because the alternative is re-deciding the whole map's cover every time a heap is
            // carried away.
            if (standing) felled?.Invoke(where);
        }
    }

    /// <summary>
    /// The nearest tree with wood still in it, within <paramref name="reachMetres"/> of a point.
    /// </summary>
    /// <remarks>
    /// Straight-line, like every other question a body asks about its own next few steps, and for the
    /// same reason: this is asked once per load per cutter, and paying a routing query to choose between
    /// two trees a cutter can see would cost more than the walk between them.
    /// </remarks>
    /// <summary>Whether a body could get to a tree at all — see <c>SimulationWorld.CanReachTree</c>.</summary>
    internal delegate bool Reachable(Vector2 position);

    public static NodeId NearestDeposit(
        NodeStore nodes,
        AgentStore agents,
        Vector2 from,
        Resource resource,
        float reachMetres,
        AgentId self,
        Reachable? reachable)
    {
        var best = NodeId.None;
        var bestDistance = reachMetres * reachMetres;
        foreach (ref readonly var node in nodes.All)
        {
            if (!node.IsAlive || !node.IsNaturalDeposit || node.Stock[resource] <= 0) continue;
            var distance = Vector2.DistanceSquared(node.Position, from);
            if (distance > bestDistance) continue;
            if (IsClaimed(agents, node.Id, self)) continue;
            if (reachable is not null && !reachable(node.Position)) continue;
            bestDistance = distance;
            best = node.Id;
        }

        // Everything in reach already has somebody on it. Sharing a trunk is legitimate — two axes fell
        // a tree in half the time — so the claim is a preference and not a lock; without the fallback a
        // seventh cutter with six trees in reach would simply stop.
        return best.IsValid ? best : NearestDeposit(nodes, from, resource, reachMetres, reachable);
    }

    /// <summary>The nearest reachable tree with wood in it, whoever else is already on it.</summary>
    /// <remarks>
    /// <b>Reachability is not optional now that a forest interior is impassable.</b> The nearest tree to a
    /// store is very often one buried in the middle of a stand, and a cutter sent to one walks at it,
    /// fails to arrive, retries politely and never cuts anything — so the whole settlement would starve for
    /// wood while standing next to a forest. Only the fringe can be worked, which is the mechanic: fell the
    /// edge and the edge moves in.
    /// </remarks>
    public static NodeId NearestDeposit(
        NodeStore nodes,
        Vector2 from,
        Resource resource,
        float reachMetres,
        Reachable? reachable = null)
    {
        var best = NodeId.None;
        var bestDistance = reachMetres * reachMetres;
        foreach (ref readonly var node in nodes.All)
        {
            if (!node.IsAlive || !node.IsNaturalDeposit || node.Stock[resource] <= 0) continue;
            var distance = Vector2.DistanceSquared(node.Position, from);
            if (distance > bestDistance) continue;
            if (reachable is not null && !reachable(node.Position)) continue;
            bestDistance = distance;
            best = node.Id;
        }

        return best;
    }

    /// <summary>
    /// Whether some other body's standing job already names this tree.
    /// </summary>
    /// <remarks>
    /// Because "the nearest tree" is the same tree for every cutter based at the same store, and without
    /// this they all converge on one trunk: seven axes on one tree, felled in a seventh of the time, then
    /// all seven walk to the next one together. Measured, that also quietly cost the settlement its hand
    /// count — six of the seven were shoved off the trunk by the crowd and settled outside the reach that
    /// counts as working it, so a woodland with thirteen trees in reach was being worked by one person.
    /// <para>
    /// Read off the assignments rather than kept as a reservation table, for the same reason the hauling
    /// board reads which carts are already hauling: a second collection to keep in step with spawning,
    /// despawning and tombstoned slots is a collection that is eventually wrong, and this one would have
    /// to survive a save as well.
    /// </para>
    /// </remarks>
    private static bool IsClaimed(AgentStore agents, NodeId tree, AgentId self)
    {
        foreach (ref readonly var agent in agents.All)
        {
            if (!agent.IsAlive || agent.Id == self) continue;
            if (agent.Jobs.Assignment.Kind != AssignmentKind.Work) continue;
            if (agent.Jobs.Assignment.Source == tree) return true;
        }

        return false;
    }

    /// <summary>
    /// A store of this faction with a tree in reach of it, and that tree — the nearest such pair.
    /// </summary>
    /// <remarks>
    /// How a cutter follows a receding wood line without being told to. When the trees near its own base
    /// are gone it asks whether <em>any</em> of the settlement's stores can still reach one, and re-bases
    /// itself on that store — so building a forward depot at the tree line is enough to put the axes back
    /// to work, and no store anywhere having a tree in reach is the settlement being told, unambiguously,
    /// that it has run out of forest.
    /// </remarks>
    public static (NodeId Store, NodeId Deposit) NearestBaseWithDeposit(
        NodeStore nodes,
        AgentStore agents,
        FactionId faction,
        Vector2 from,
        Resource resource,
        float reachMetres,
        AgentId self,
        Reachable? reachable)
    {
        var bestStore = NodeId.None;
        var bestTree = NodeId.None;
        var bestDistance = float.PositiveInfinity;
        foreach (ref readonly var store in nodes.All)
        {
            if (!store.IsAlive || !store.Stores || store.Faction != faction) continue;
            if (store.RoomFor(resource) <= 0) continue;
            var tree = NearestDeposit(nodes, agents, store.Position, resource, reachMetres, self, reachable);
            if (!tree.IsValid) continue;
            var distance = Vector2.DistanceSquared(store.Position, from);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestStore = store.Id;
            bestTree = tree;
        }

        return (bestStore, bestTree);
    }

    /// <summary>Drops a haul whose source has emptied or whose sink has filled.</summary>
    private void ReleaseStaleHauls(NodeStore nodes, AgentStore agents)
    {
        var bodies = agents.MutableSpan();
        for (var i = 0; i < bodies.Length; i++)
        {
            ref var agent = ref bodies[i];
            if (!agent.IsAlive || !agent.Jobs.Assignment.MovesCargo) continue;
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
                // A finished building has no room for timber, which is correct and would otherwise strand
                // a cart mid-journey holding materials for a wall that no longer needs them. It is not
                // stale — it is a delivery that should be redirected — and Handover already does that when
                // the load arrives and will not fit.
                if (stale && sink.IsBuilt && assignment.Cargo == Resource.Wood) stale = false;
            }

            if (!stale) continue;
            // A route that has run dry is not an abandoned haul, it is a job finished — "they keep hauling
            // till the source exhausts" is the route ending on its own terms, and counting it as a
            // failure would make the abandoned column meaningless. The cart stays: a carter between
            // routes is a carter, and the board will find it stranded stock or a heap to fetch.
            if (agent.Jobs.Assignment.Kind == AssignmentKind.Carry) RoutesFinished++;
            else HaulsAbandoned++;
            JobSystem.Assign(ref agent, Assignment.None);
        }
    }

    /// <summary>
    /// Which stores something actually eats out of.
    /// </summary>
    /// <remarks>
    /// A house draws from the store whose catchment reaches it, so a store no house names is a store
    /// nothing draws on — and stock sitting in one is stock the settlement cannot use however full the
    /// building is. Derived from the bindings <see cref="BindCatchments"/> already made rather than
    /// stored, which costs one pass over the nodes every two seconds and cannot fall out of step with
    /// the thing it describes.
    /// </remarks>
    private void MarkStoresSomebodyEatsFrom(NodeStore nodes)
    {
        drawnOn.Clear();
        foreach (ref readonly var sink in nodes.All)
        {
            if (!sink.IsAlive || !sink.IsSink || sink.Occupants <= 0) continue;
            if (sink.Supply.IsValid) drawnOn.Add(sink.Supply);
        }
    }

    /// <summary>
    /// Every journey the settlement would benefit from, in node order.
    /// </summary>
    /// <remarks>
    /// Three shapes, and the middle one is Stage B's whole contribution to hauling:
    /// <list type="bullet">
    /// <item><b>A heap on the ground</b> is always worth collecting, however small — left alone it is
    /// not stock in the wrong place, it is stock nobody has.</item>
    /// <item><b>A store nothing draws on</b> is <em>stranded</em>. Its wood is real, it is in a
    /// building, and no household can eat out of it, so a cart's round trip is the only thing that turns
    /// it into supply. This is the trigger that makes a lumber camp work: you build a forward depot at
    /// the tree line so your cutters stop walking a hundred metres a load, and the wood then piles up
    /// somewhere nobody lives, and <em>that</em> is what puts carts on the road. Nothing knows what a
    /// lumber camp is.</item>
    /// <item><b>A store somebody does draw on, but which is nearly full</b>, can even things out toward
    /// one that is nearly empty. The high and low water marks stop that shuffling stock back and forth
    /// forever.</item>
    /// </list>
    /// <para>
    /// The trigger it replaces was "any store above its high-water mark", which could not express the
    /// distinction at all: a compact settlement's granary is the only store, sits below high water all
    /// year, and would never be collected from — while a forward depot at 70% would not be collected
    /// either, because it was not full enough, so a frontier holding simply filled up and stopped. The
    /// question is not how full a store is. It is whether anybody can reach what is in it.
    /// </para>
    /// </remarks>
    private void CollectTasks(NodeStore nodes, int worthLoad)
    {
        tasks.Clear();
        foreach (ref readonly var source in nodes.All)
        {
            // A tree is not a delivery. Standing timber is released by an axe, not collected by a cart,
            // and a woodland is two orders of magnitude more numerous than the buildings — so it is
            // skipped first, before the per-resource sweep, rather than falling through every test.
            if (!source.IsAlive || source.IsNaturalDeposit) continue;
            // Nor is a site a source. It is holding timber that is about to become a wall.
            if (source.IsUnderConstruction) continue;
            var stranded = source.Stores && !drawnOn.Contains(source.Id);
            foreach (var resource in Resources.All)
            {
                if (source.Stock[resource] <= 0) continue;
                var worthFetching = source.Stock[resource] >= worthLoad ||
                                    source.Stock[resource] >= source.Capacity * HighWater;
                var giving = source.IsPile
                    // What stops a cart crossing the map for three units of grain is not a threshold, it
                    // is that the trip is priced and the heap's urgency is proportional to its size.
                    ? true
                    : source.Produces_
                        ? worthFetching
                        : source.Stores &&
                          (stranded
                              ? worthFetching
                              : source.Stock[resource] >= source.Capacity * HighWater);
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
                    // Stranded stock only ever moves toward somewhere it can be eaten from; without
                    // that, two depots at the tree line would pass the same wood between themselves
                    // forever, both of them equally unreachable.
                    if (stranded && !drawnOn.Contains(sink.Id)) continue;
                    if (source.Stores && !stranded &&
                        sink.Stock[resource] > sink.Capacity * LowWater)
                    {
                        continue;
                    }

                    // Emptiest store wins, so the settlement spreads its stock rather than topping up
                    // whichever node happens to be first.
                    var need = 1f - sink.Stock[resource] / (float)Math.Max(1, sink.Capacity);
                    if (need <= bestNeed) continue;
                    bestNeed = need;
                    bestSink = sink.Id;
                }

                if (!bestSink.IsValid) continue;
                // Output accumulating where nobody can use it is the urgent case, because when the
                // building fills the people feeding it stop working. A store merely being uneven is not.
                var fullness = source.Stock[resource] / (float)Math.Max(1, source.Capacity);
                var urgency = source.IsPile
                    // Bigger heaps first, and all of them below a yard about to overflow.
                    ? 0.5f + 0.5f * MathF.Min(1f, source.Stock[resource] / MathF.Max(1f, worthLoad))
                    : source.Produces_ || stranded
                        ? 1f + fullness
                        : bestNeed;
                tasks.Add(new HaulTask(source.Id, bestSink, resource, urgency));
            }
        }

        CollectSiteDemand(nodes, worthLoad);
    }

    /// <summary>
    /// Timber wanted at building sites, which is the one task the board reads backwards.
    /// </summary>
    /// <remarks>
    /// Every other journey on the board starts from goods in the wrong place and looks for somewhere better
    /// — a heap, a stranded store, an uneven pair of granaries. A site is the opposite shape: it is a
    /// <em>demand</em> at a place, and the question is which store can answer it. So it gets its own pass
    /// rather than being bent into the source-driven sweep, and it is the most urgent thing on the board:
    /// hands standing at a site with no materials are hands doing nothing at all, which is worse than any
    /// amount of stock sitting still.
    /// <para>
    /// This is also the thing that makes a cart necessary rather than merely useful. A settlement with no
    /// carter cannot get timber to a site, and a cart costs timber — so the first cart comes out of the
    /// founding stores, and after that the hauling network is what lets the settlement build at all.
    /// </para>
    /// </remarks>
    private void CollectSiteDemand(NodeStore nodes, int worthLoad)
    {
        foreach (ref readonly var site in nodes.All)
        {
            if (!site.IsAlive || !site.WantsMaterials) continue;
            // <b>One task per material still owed, not one for timber.</b> A granary needs stone as well now,
            // and a board that only ever offered wood would leave a site standing at "wants 8 stone" forever
            // with hands beside it and no cart coming — the exact failure this whole layer exists to prevent,
            // reproduced for the new material.
            foreach (var resource in Resources.All)
            {
                var owed = site.Wanted(resource);
                if (owed <= 0) continue;
                var from = NearestStoreWith(
                    nodes,
                    resource,
                    Math.Min(owed, Math.Max(1, worthLoad)),
                    site.Faction,
                    site.Position);
                if (!nodes.Contains(from)) continue;
                // Above a producer's overflowing yard, which is 2.0 at its worst: idle labour costs more than
                // stalled production, because a farm that stops producing still has its hands doing something.
                tasks.Add(new HaulTask(from, site.Id, resource, 2.5f));
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
            if (agent.IsAlive && agent.HasCart)
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
