using System.Collections.Generic;
using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;

namespace RTSGame.AI;

/// <summary>
/// A rule-bot that runs one faction's settlement through the verbs a person has.
/// </summary>
/// <remarks>
/// <b>The acceptance test §4 set is "the AI can play the player's settlement", and it is only checkable if the
/// AI has no other way in.</b> So this holds a <see cref="SimulationWorld"/> and touches it through
/// <c>QueueAssign</c> and the other queued commands only — never a node's fields, never a body's state, never
/// the jobs layer directly. Everything it decides is a command a mouse could have issued, which is what makes
/// "no resource cheats, no vision cheats" a property of the code rather than a promise.
/// <para>
/// It reads two things: its own faction's nodes and bodies, which a player can see on their own screen, and
/// <see cref="FactionKnowledge"/>, which §131 built as the simulation-side answer to what a faction knows.
/// It does not read the fog — that is view state and §119's rule is a direction — and it does not read
/// another faction's stores.
/// </para>
/// <para>
/// <b>Outside the world, on the RaidDirector's precedent, and stepped inside the fixed tick.</b> A player is
/// not part of the world and its state is not world state; what makes that safe is that the bot is a pure
/// function of what it can see plus a tick counter, so two runs of the same fixture drive identically and the
/// determinism check still compares the worlds rather than the drivers.
/// </para>
/// <para>
/// <b>What this first cut is for.</b> Not to play well — to play at all, through the real interface, so the
/// seams show up while the settlement is the only thing at stake. It keeps its people employed and moves them
/// between grain and wood as the stores run down. Building, training and anything to do with a neighbour come
/// after there is a measurement saying this much works.
/// </para></remarks>
internal sealed class SettlementBot
{
    /// <summary>
    /// Ticks between decisions.
    /// </summary>
    /// <remarks>
    /// <b>A delegation-layer player thinks on a human cadence, not a simulation one.</b> §7 makes AI
    /// difficulty an attention budget rather than a cheat, and a bot re-deciding every tick would be spending
    /// attention no person has — thirty times a second on a settlement whose fastest meaningful change takes
    /// seconds. Half a second is faster than a player and slow enough that the cost is nothing.
    /// </remarks>
    private const int DecideEveryTicks = 30;

    /// <summary>Seasons of a resource below which everything spare goes to it.</summary>
    /// <remarks>
    /// Expressed in the same unit the settlement report uses — seasons of it left — because that is the figure
    /// a player would look at, and a bot reasoning in units nobody displays is a bot whose decisions cannot be
    /// argued with from the chair.
    /// </remarks>
    private const float ShortSeasons = 2.5f;

    /// <summary>
    /// Share of spare hands that go to the fields when nothing is short.
    /// </summary>
    /// <remarks>
    /// <b>A ratio, because a threshold on its own made the bot survive and never grow.</b> §135 measured a
    /// year: the founding's fixed eight farms and four cutters took thirteen people to twenty, and the bot
    /// stayed at thirteen with its grain drained from 4,200 to 680 and its woodpile the larger of the two. The
    /// reason was one number — it only moved hands onto grain below <see cref="ShortSeasons"/>, and a
    /// settlement founded with twenty-two seasons in store is nowhere near that, so everybody went to the wood
    /// and the larder paid for it until there was nothing spare to grow on.
    /// <para>
    /// Two thirds is the founding's own ratio, near enough — eight fields to four cutters — and it is used
    /// here rather than rediscovered because the founding's arrangement is the one the year legs assert an
    /// economy against. The thresholds still override it in either direction: a real shortage of either
    /// resource takes everything until it is not one.
    /// </para></remarks>
    private const float FieldShare = 2f / 3f;

    /// <summary>Hands put on one construction site at a time.</summary>
    /// <remarks>
    /// Two, because a site's labour is spent by whoever stands at it and one hand on a barracks is 1.5
    /// springs of one person's year. Not more, because every hand on a wall is a hand off the fields, and the
    /// §135 lesson is that this settlement's binding constraint is almost always the larder.
    /// </remarks>
    private const int Builders = 2;

    /// <summary>Palisades it puts up once it has somewhere to train.</summary>
    /// <remarks>
    /// Four, which is what the founding cache's stone pays to turn to stone — see the starting stock in
    /// <c>Populate</c>. A ring is not attempted: four segments on the approaches is a thing a player does
    /// early, and a closed wall is a design decision §7 has not made yet.
    /// </remarks>
    private const int WallsWanted = 4;

    /// <summary>Militia it keeps, once it has a barracks.</summary>
    /// <remarks>
    /// Four out of seventeen. Militia eat more and produce nothing, so this is a real cost paid out of the
    /// same larder the ratio above is protecting — which is the point: a standing force that cost nothing
    /// would make every later decision about defence uninteresting.
    /// </remarks>
    private const int MilitiaWanted = 4;

    /// <summary>How far out from the store a wall goes.</summary>
    /// <remarks>
    /// Outside the field keep-out, which is the ground the village and its fields occupy, so a wall does not
    /// stand in the wheat. Its own footprint is what stops it standing on a building.
    /// </remarks>
    private const float WallRingMetres = 27f;

    /// <summary>
    /// Ticks before it looks at the same site again.
    /// </summary>
    /// <remarks>
    /// <b>The one thing a bot has to remember, and not remembering it emptied every field on the map.</b>
    /// §140: the first version re-decided the whole project allocation twice a second against applied state.
    /// A hand posted at a site has not arrived yet, so the count says nobody is there, so it posts two more —
    /// measured over a year, 24,693 assignments where the working version issues seventeen, twenty-eight
    /// militia ordered against a target of four, and <b>every one of sixteen fields at zero hands for the
    /// whole year while both settlements starved</b>. §137's trap in a third costume: state read while the
    /// reader is in the middle of writing it.
    /// <para>
    /// A cooldown rather than a cleverer count, because what a player does here is post two villagers and
    /// then look at something else for a while. Ten seconds is long enough for a hand to walk to a site
    /// across a settlement and short enough that a dead project is picked up again.
    /// </para></remarks>
    private const int LookAgainTicks = 300;

    private readonly FactionId faction;
    private long nextDecision;

    /// <summary>When it last put hands on a given site, by node slot. Its own memory of its own orders.</summary>
    private readonly Dictionary<int, long> lastPosted = new();

    /// <summary>Militia it has ordered, which is not the same as militia that exist yet.</summary>
    private int militiaOrdered;

    /// <summary>Assignments issued, for a report that would rather count than assume.</summary>
    public int OrdersIssued { get; private set; }

    public int Decisions { get; private set; }

    /// <summary>Sites it has laid down, and militia it has raised, for the same reason as OrdersIssued.</summary>
    public int SitesPlaced { get; private set; }

    public int MilitiaRaised { get; private set; }

    /// <summary>Whose settlement this is, so a diagnostic can name it.</summary>
    public FactionId Faction => faction;

    public SettlementBot(FactionId faction) => this.faction = faction;

    public void Update(SimulationWorld world)
    {
        if (world.TickNumber < nextDecision) return;
        nextDecision = world.TickNumber + DecideEveryTicks;
        Decisions++;

        // What it has: its own stores, its own people, its own places of work. All of it visible on a screen.
        var store = NodeId.None;
        var fields = new List<(NodeId Id, Vector2 At, float Extent)>();
        var projects = new List<(NodeId Id, Vector2 At, float Extent)>();
        var barracks = NodeId.None;
        var hasBarracksSite = false;
        var walls = 0;
        var stone = 0;
        var timber = 0;
        foreach (var id in world.Nodes.SettlementNodes)
        {
            ref readonly var node = ref world.Nodes.Get(id);
            if (!node.IsAlive || node.Faction != faction) continue;
            if (node.Stores && !store.IsValid) store = node.Id;

            if (node.Kind == NodeKind.Farm) fields.Add((node.Id, node.Position, node.HalfExtent));
            if (node.Kind == NodeKind.Barracks)
            {
                if (node.IsBuilt) barracks = node.Id;
                else hasBarracksSite = true;
            }

            if (node.Kind is NodeKind.PalisadeWall or NodeKind.StoneWall) walls++;
            // Anything wanting labour, whether it is going up for the first time or turning to stone.
            if (node.IsUnderConstruction || node.HasStructuralProject)
            {
                projects.Add((node.Id, node.Position, node.HalfExtent));
            }

            if (node.Stores)
            {
                stone += node.Stock[Resource.Stone];
                timber += node.Stock[Resource.Wood];
            }
        }

        if (!store.IsValid) return;

        var mouths = 0;
        var idle = new List<AgentId>();
        foreach (ref readonly var body in world.Agents.All)
        {
            if (!body.IsAlive || body.Faction != faction) continue;
            mouths++;
            // <b>A cart is not a spare pair of hands, and treating it as one cost the bot its harvest.</b>
            // §135: the hauling board finds carts by their being IDLE — `HasCart` and no cargo assignment —
            // so employing one in a field takes it off the board for good. Measured over a year: the bot's
            // fields were worked and its granary still fell from 4,200 to 680 while the founded settlement
            // reaped to 5,295, because the crop was sitting in farm yards with nobody to carry it in. The
            // property is `HasCart` rather than carry capacity for the reason the board's own comment gives —
            // every villager carries now, a reaper walks its own crop in, so capacity marks nobody.
            if (body.HasCart) continue;

            // Idle means no standing commitment at all. A body under an order is left alone: §7 makes a manual
            // order an interrupt that expires, and a bot that re-assigned over one would be fighting the
            // player for its own units.
            if (!body.Jobs.HasAssignment && !body.Jobs.IsInterrupted) idle.Add(body.Id);
        }

        if (mouths == 0) return;

        // <b>The one judgement this cut makes, read from the figure a player is shown.</b> §8's autonomy time,
        // through the same Outlook call the HUD makes — filtered to this faction, because unfiltered it would
        // be answering about the opponent's larder as well as its own. Not recomputed from a rate of its own:
        // a bot reasoning in units nobody displays is a bot whose decisions cannot be argued with from the
        // chair, and a second opinion about what a mouth eats is the same risk as a second opinion about what
        // can be seen.
        var grainLeft = world.Economy.Outlook(
            Resource.Grain, world.Nodes, world.Agents, world.Date.Season, faction).Seasons;
        var woodLeft = world.Economy.Outlook(
            Resource.Wood, world.Nodes, world.Agents, world.Date.Season, faction).Seasons;

        // <b>Counting what is already posted, so the ratio is about the whole workforce.</b> A bot that split
        // only its spare hands would drift wherever the last few idlers happened to land.
        var onFields = 0;
        var onWood = 0;
        foreach (ref readonly var body in world.Agents.All)
        {
            if (!body.IsAlive || body.Faction != faction || body.HasCart) continue;
            if (body.Jobs.Assignment.Kind != AssignmentKind.Work) continue;
            if (body.Jobs.Assignment.Cargo == Resource.Grain) onFields++;
            else if (body.Jobs.Assignment.Cargo == Resource.Wood) onWood++;
        }

        // <b>What it builds and who it trains, before the employment loop, because that loop puts every idle
        // hand to work and would leave nothing for a site.</b> §140. Ordered the way a player orders it:
        // somewhere to train, then hands on whatever is going up, then a garrison, then walls — each step
        // only reached if the one before it is satisfied, so the bot never has four half-built things and
        // nobody standing at any of them.
        //
        // <b>One project per decision, and it remembers.</b> See LookAgainTicks: a decision cannot see the
        // orders it has just issued, so acting on every project every time is how a settlement ends up with
        // its whole workforce standing at building sites and nobody in the wheat.
        var spare = new List<AgentId>(idle);

        // Somewhere to train, first and once. A settlement with a barracks site already down does not want a
        // second one — the site is the decision, and it stays taken until it is built.
        if (!barracks.IsValid && !hasBarracksSite &&
            timber >= Construction.TimberFor(NodeKind.Barracks) &&
            stone >= Construction.StoneFor(NodeKind.Barracks))
        {
            if (Place(world, store, NodeKind.Barracks, WallRingMetres * 0.6f)) SitesPlaced++;
        }

        // Hands on one thing that is going up. Taken from the idle first and off the fields only if there are
        // none, which is what a player does: the site is the thing that is not happening on its own.
        foreach (var (site, at, extent) in projects)
        {
            if (lastPosted.TryGetValue(site.Value, out var when) && world.TickNumber - when < LookAgainTicks)
            {
                continue;
            }

            var standing = HandsOn(world, site);
            if (standing >= Builders) continue;
            for (var i = standing; i < Builders; i++)
            {
                var hand = TakeHand(world, spare);
                if (hand is not { } body) break;
                // Posted, not ordered to build: PostedOnAWorkSite turns a hold on a site into the build
                // assignment, which is the same conversion the player's post key relies on. One verb.
                world.QueueAssign(new[] { body }, Assignment.Hold(at, EconomySystem.WorkShiftSeconds, extent));
                OrdersIssued++;
            }

            lastPosted[site.Value] = world.TickNumber;
            break;
        }

        // A garrison, once there is somewhere to raise it. One at a time and counted by what it has ordered
        // rather than by what has appeared: a villager walking to the barracks is not militia yet, and
        // counting only the converted ordered twenty-eight of them out of thirteen people.
        if (barracks.IsValid && militiaOrdered < MilitiaWanted)
        {
            var hand = TakeHand(world, spare);
            if (hand is { } body)
            {
                world.QueueTrainMilitia(new[] { body }, barracks);
                OrdersIssued++;
                militiaOrdered++;
                MilitiaRaised++;
            }
        }

        // Walls, last, and only with a barracks standing and nothing else half-built: a settlement that walls
        // itself in before it can field anybody has spent its timber on the wrong half of a defence.
        if (barracks.IsValid && walls < WallsWanted && projects.Count == 0 &&
            timber >= Construction.TimberFor(NodeKind.PalisadeWall))
        {
            if (Place(world, store, NodeKind.PalisadeWall, WallRingMetres)) SitesPlaced++;
        }

        // A sound palisade becomes stone while there is stone to do it with. §71's one proved upgrade, and
        // the reason the founding cache carries stone at all.
        if (stone >= StoneWallUpgradeCost && projects.Count == 0)
        {
            foreach (var id in world.Nodes.SettlementNodes)
            {
                ref readonly var node = ref world.Nodes.Get(id);
                if (!node.IsAlive || node.Faction != faction) continue;
                if (node.Kind != NodeKind.PalisadeWall || !node.IsBuilt || node.HasStructuralProject) continue;
                if (node.Condition < node.MaxCondition - 0.0001f) continue;
                if (world.BeginUpgrade(id, NodeKind.StoneWall)) SitesPlaced++;
                break;
            }
        }

        idle = spare;

        // Everybody idle goes to work, on the fields if the larder is low and at the wood otherwise. Posted
        // one at a time so each gets its own site rather than all crowding the first.
        // A shortage of either resource takes everything until it is not one; otherwise the split holds.
        var fieldHands = HandsPerField(world, fields);
        var placed = 0;
        foreach (var hand in idle)
        {
            var employed = onFields + onWood;
            // <b>A shortage moves the ratio; it does not replace it.</b> §140. This was a two-way switch —
            // short of grain, everybody to the fields; short of wood, everybody to the trees — and it held up
            // for exactly as long as the bot did not spend anything. Buying a barracks costs 300 timber,
            // which drops the woodpile under the threshold in one act, and the switch then sent all thirteen
            // hands to the wood: measured, <b>zero hands on eight fields through the whole of summer</b>, a
            // settlement that had been keeping itself fed walking into a harvest with nothing planted.
            //
            // §71 named this shape when stone was added — a two-way ternary between two resources has no
            // room for the third case, and "both matter, one more than the other" is the ordinary case rather
            // than the exception. So the shortage leans the split hard and always leaves the other resource
            // somebody: at worst one hand in three, which is enough that a field is still being worked when
            // the shortage clears.
            var share =
                grainLeft < ShortSeasons ? 0.85f
                : woodLeft < ShortSeasons ? 0.35f
                : FieldShare;
            var wantsGrain = onFields < MathF.Ceiling((employed + 1) * share);
            var work = wantsGrain && fields.Count > 0
                ? LeastManned(fields, fieldHands)
                : NearestTreeTo(world, store);
            if (work is not { } site) continue;
            world.QueueAssign(new[] { hand }, site);
            OrdersIssued++;
            placed++;
            if (wantsGrain) onFields++;
            else onWood++;
        }
    }

    /// <summary>
    /// The field with the fewest hands on it.
    /// </summary>
    /// <remarks>
    /// <b>Least-manned, and a running counter cost the bot two thirds of its harvest.</b> §135: the first
    /// version indexed <c>fields[(handsPlaced) % fields.Count]</c>, which distributes the opening round
    /// perfectly and then wraps — a hand re-posted later comes back to field zero, which already has
    /// somebody, while the high-numbered fields lie fallow. Measured over a year against the founding, which
    /// posts one hand per farm: <b>nine of the bot's hands on fields produced 2,036 grain against eight of
    /// the founding's producing 5,235</b>, and the settlement never grew. Same labour, a third of the crop,
    /// because a third of the fields were being worked twice and the rest not at all.
    /// <para>
    /// Counted from the bodies rather than remembered, so it cannot drift out of step with what the hands are
    /// actually doing — which is the same reason the ratio above counts the workforce instead of tracking it.
    /// </para></remarks>
    /// <summary>Stone a sound palisade wants to become a stone wall. §71's one proved upgrade.</summary>
    /// <remarks>
    /// Named here rather than read from <c>StructuralProjects</c> because the cost of a project that has not
    /// begun is not a thing that layer answers — <c>CostFor</c> takes a node that is already upgrading. A
    /// figure the bot has to know before it commits is a figure it has to hold, and this one is asserted
    /// against the real cost by the self-test rather than trusted.
    /// </remarks>
    private const int StoneWallUpgradeCost = 120;

    /// <summary>The figure above, so a test can hold it to what the upgrade really costs.</summary>
    internal static int StoneForAStoneWall => StoneWallUpgradeCost;

    /// <summary>Hands standing at one site, counted from the bodies for the usual reason.</summary>
    private int HandsOn(SimulationWorld world, NodeId site)
    {
        var standing = 0;
        foreach (ref readonly var body in world.Agents.All)
        {
            if (!body.IsAlive || body.Faction != faction || body.HasCart) continue;
            var job = body.Jobs.Assignment;
            // Either already converted to a build assignment, or posted on it and about to be.
            if (job.Kind == AssignmentKind.Build && job.Source == site) standing++;
            else if (job.Kind == AssignmentKind.Hold &&
                     Vector2.DistanceSquared(job.Anchor, world.Nodes.Get(site).Position) < 4f)
            {
                standing++;
            }
        }

        return standing;
    }

    /// <summary>
    /// One pair of hands for a project: an idle body if there is one, otherwise somebody off the fields.
    /// </summary>
    /// <remarks>
    /// <b>Taking a worker is not a cheat and refusing to was the reason nothing ever got built.</b> The
    /// founding posts every hand it has, so the idle list is usually empty and a bot that only ever employed
    /// idlers would lay a site down and leave it standing for a year — §135's failure shape exactly, a policy
    /// that looks tried and is not. Pulling two villagers off the wheat to raise a barracks is the ordinary
    /// thing a person does, and it is one QueueAssign either way.
    /// </remarks>
    private AgentId? TakeHand(SimulationWorld world, List<AgentId> spare)
    {
        if (spare.Count > 0)
        {
            var first = spare[0];
            spare.RemoveAt(0);
            return first;
        }

        // Lowest id, so two runs of the same fixture take the same body. A bot that took whichever the
        // iteration order happened to reach first would be a determinism fault waiting for a reorder.
        AgentId? picked = null;
        foreach (ref readonly var body in world.Agents.All)
        {
            if (!body.IsAlive || body.Faction != faction || body.HasCart) continue;
            if (body.Role == AgentRole.Militia) continue;
            // A body under a manual order is left alone here for the same reason the employment loop leaves
            // it alone: §7 makes a player's order an interrupt that expires, and --handsoff says the person
            // can still reach into a bot-run settlement. A bot that pulled an interrupted villager onto a
            // building site would be countermanding the only orders on the map that came from a human.
            if (body.Jobs.IsInterrupted) continue;
            var job = body.Jobs.Assignment;
            if (job.Kind != AssignmentKind.Work || job.Cargo != Resource.Grain) continue;
            if (picked is not { } best || body.Id.Value < best.Value) picked = body.Id;
        }

        return picked;
    }

    /// <summary>
    /// Lays a site down on open ground near the store, and says whether it found any.
    /// </summary>
    /// <remarks>
    /// <b>The one thing here that is not a queued command, and it is not a privilege.</b> The player's own
    /// build key calls <c>AddNode</c> directly too — see <c>RtsGameLoop.Build</c> — because placing a site is
    /// not an order to a unit and there is nothing about it to interrupt or to survive a save: it happens
    /// between one tick and the next or not at all. What the bot does not get is a site on ground the player
    /// could not use, which is why this goes through the same <c>Terrain.CanPlace</c> the placement rules use
    /// and refuses to overlap anything standing.
    /// <para>
    /// A ring rather than a scan, walked at a fixed step from a fixed bearing, so the same fixture places the
    /// same building in the same spot twice.
    /// </para>
    /// </remarks>
    private bool Place(SimulationWorld world, NodeId store, NodeKind kind, float radius)
    {
        var centre = world.Nodes.Get(store).Position;
        var half = NodeFootprint.HalfExtentOf(kind);
        var extents = new Vector2(half);
        for (var ring = 0; ring < 3; ring++)
        for (var step = 0; step < 24; step++)
        {
            var angle = step / 24f * MathF.Tau;
            var at = centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (radius + ring * 6f);
            if (!world.Terrain.CanPlace(at, extents)) continue;
            if (Occupied(world, at, half)) continue;
            var built = !Construction.NeedsBuilding(kind);
            world.AddNode(kind, at, capacity: 0, Resource.Wood, faction: faction, built: built);
            return true;
        }

        return false;
    }

    /// <summary>Whether anything already stands where a footprint wants to go.</summary>
    private static bool Occupied(SimulationWorld world, Vector2 at, float half)
    {
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive) continue;
            // A tree is not an obstruction to a building — it does not block, and the ground under it is
            // ordinary ground. A standing outcrop is.
            if (node.Kind == NodeKind.Tree) continue;
            var apart = half + node.HalfExtent + 1.5f;
            if (Vector2.DistanceSquared(node.Position, at) < apart * apart) return true;
        }

        return false;
    }

    /// <summary>
    /// Hands already posted to each field, counted from the bodies.
    /// </summary>
    /// <remarks>
    /// Counted rather than remembered so it cannot drift from what the hands are actually doing, and taken
    /// once per decision because the caller then has to keep it up to date itself — see the note on
    /// <see cref="LeastManned"/> for why that is not optional.
    /// </remarks>
    private int[] HandsPerField(SimulationWorld world, List<(NodeId Id, Vector2 At, float Extent)> fields)
    {
        var hands = new int[fields.Count];
        foreach (ref readonly var body in world.Agents.All)
        {
            if (!body.IsAlive || body.Faction != faction || body.HasCart) continue;
            if (body.Jobs.Assignment.Kind != AssignmentKind.Work) continue;
            for (var i = 0; i < fields.Count; i++)
            {
                if (body.Jobs.Assignment.Source != fields[i].Id) continue;
                hands[i]++;
                break;
            }
        }

        return hands;
    }

    /// <summary>
    /// The field with the fewest hands, counting the ones this decision has already posted.
    /// </summary>
    /// <remarks>
    /// <b>The tally has to include what is still in the command queue, and leaving it out put every hand on
    /// one field.</b> §137: `QueueAssign` enqueues, so nothing a decision issues has taken effect while that
    /// decision is still running — every hand in the batch therefore looked at an empty field zero and went
    /// there. Measured: twelve hands on the first field and none on the other seven, against the founding's
    /// one apiece, which is the whole of the shortfall §136 could not explain and which a previous version of
    /// this method had avoided by accident with a running counter.
    /// <para>
    /// So the caller counts once and increments here. A read-only count of applied state is exactly the wrong
    /// shape for choosing between several things at once, which is the same mistake in miniature as reading a
    /// figure back from a structure you are in the middle of writing.
    /// </para></remarks>
    private static Assignment? LeastManned(
        List<(NodeId Id, Vector2 At, float Extent)> fields,
        int[] hands)
    {
        if (fields.Count == 0) return null;
        var best = 0;
        for (var i = 1; i < fields.Count; i++)
        {
            if (hands[i] < hands[best]) best = i;
        }

        hands[best]++;
        var (id, at, extent) = fields[best];
        return Assignment.Work(
            id, at, extent, Resource.Grain,
            EconomySystem.WorkShiftSeconds, EconomySystem.HandoverSeconds);
    }

    /// <summary>
    /// The nearest standing tree to the store, as a place to post a cutter.
    /// </summary>
    /// <remarks>
    /// The same rule the founding uses, for the same reason §22 gives: the settlement's wood comes out of the
    /// trees nearest a store, and when those are gone the line moves outward. Read from the node store, which
    /// is ground a faction standing there can see.
    /// </remarks>
    private Assignment? NearestTreeTo(SimulationWorld world, NodeId store)
    {
        var from = world.Nodes.Get(store).Position;
        var best = NodeId.None;
        var bestDistance = float.MaxValue;
        var at = Vector2.Zero;
        var extent = 0f;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || node.Kind != NodeKind.Tree || node.Stock[Resource.Wood] <= 0) continue;
            // Only ground this faction knows. A bot that could post a cutter at a trunk it has never seen
            // would be reading the world rather than its own knowledge, which is the one cheat this class
            // exists to make impossible.
            if (!world.Knowledge.Knows(faction, node.Position)) continue;
            var distance = Vector2.DistanceSquared(node.Position, from);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = node.Id;
            at = node.Position;
            extent = node.HalfExtent;
        }

        return best.IsValid
            ? Assignment.Work(
                best, at, extent, Resource.Wood,
                EconomySystem.WorkShiftSeconds, EconomySystem.HandoverSeconds)
            : null;
    }
}
