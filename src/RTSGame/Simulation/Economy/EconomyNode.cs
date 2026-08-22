using System.Numerics;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Persistence;

namespace RTSGame.Simulation.Economy;

internal readonly record struct NodeId(int Value)
{
    public static NodeId None => new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => $"node:{Value}";
}

/// <summary>What kind of place this is. §6: hauling is node to node and these are the nodes.</summary>
/// <remarks>
/// Households are deliberately not on this list. A person walking to the store daily is real, but a
/// hauler per household puts hauler count in proportion to population — hundreds — instead of
/// buildings, which is tens, and every interesting decision lives at the node level. That is the
/// difference between a hauling network you play and a traffic simulation you watch.
/// </remarks>
internal enum NodeKind
{
    /// <summary>Stores, and owns a catchment. The thing a settlement is arranged around.</summary>
    Granary,

    /// <summary>Produces, in the hands assigned to it, with a seasonal shape.</summary>
    Farm,

    /// <summary>
    /// A tree: a finite stock of wood standing where it grew.
    /// </summary>
    /// <remarks>
    /// A node because that is all a tree is — stock at a place, with a labour cost to release it — and
    /// the same argument that made a heap a node applies twice over here. It saves by memory copy, it is
    /// fingerprinted without being asked, an assignment can name it by a stable id, and felling it is
    /// <c>Remove</c>.
    /// <para>
    /// <b>The woodcutter node it replaces is retired.</b> That building accrued wood at a rate times a
    /// seasonal shape times the square root of the hands standing at it, whether or not there was a
    /// tree within a hundred metres — an abstract producer you could put anywhere, in a design whose
    /// premise is that distance is the terrain. It is exactly the mistake <see cref="CropCycle"/> was
    /// written to undo for grain, and it goes the same way: the labour is spent at the thing itself, and
    /// what a place is worth is a fact about the map. A lumber camp is now a
    /// <see cref="ForwardDepot"/> you build at the tree line, which is a store, which means the wood
    /// piling up in it is stock nobody eats — and that, not a rule about logging, is what puts carts on
    /// the road.
    /// </para>
    /// <para>
    /// A tree does not block. Hundreds of them do not re-rasterise the map, a body walks between the
    /// trunks, and a forest is a distance to be crossed rather than a wall to be routed around. That is
    /// deliberate and it is the cheap answer: the mechanic is <em>how far the wood is</em>, and making a
    /// woodland impassable would buy nothing but a hundred small holes in the navigation raster and the
    /// wide-body routing failure that is already a known debt.
    /// </para>
    /// </remarks>
    Tree,

    /// <summary>Stores at the frontier, so a distant holding runs without a hauler each way.</summary>
    ForwardDepot,

    /// <summary>
    /// Where people live, and the only place a resource stops being physical.
    /// </summary>
    /// <remarks>
    /// A house is a <em>clocked sink</em>: it draws for its occupants, and what it draws comes out of the
    /// store whose catchment it stands in without anybody carrying it. That is not a shortcut, it is
    /// §6's scoping decision made explicit — "households draw from their catchment directly", because a
    /// hauler per household would put hauler count in proportion to population, which is hundreds,
    /// instead of buildings, which is tens. The difference between a hauling network you play and a
    /// traffic simulation you watch.
    /// <para>
    /// So resources are physical everywhere except at the last step. They are grown at a yard, carried
    /// by a cart, stored in a granary, dropped on the road if the cart dies — and only when a household
    /// eats do they leave the world. Everything about distribution falls out of that: reach decides
    /// whether a house is supplied at all, so you cannot sprawl past your granaries, and a house outside
    /// every catchment goes hungry however full the stores are.
    /// </para>
    /// </remarks>
    House,

    /// <summary>
    /// Goods on the ground where somebody dropped them.
    /// </summary>
    /// <remarks>
    /// A resource is a physical thing, so it never stops existing because whoever was carrying it did.
    /// A cart destroyed on the road leaves its load lying there until something comes for it — one of
    /// yours, or one of theirs. That is what makes §7's interception worth doing: killing a loaded
    /// raider does not deny them the grain, it <em>returns</em> it, and the return trip is the
    /// defender's window precisely because the loot is recoverable at the end of it.
    /// <para>
    /// A pile is a node because that is all a pile is: stock at a place with nobody looking after it.
    /// The hauling board already looks for stock in the wrong place, so it collects piles without being
    /// taught what one is — and conservation needs no term for dropped goods, because a pile's contents
    /// are stored like anything else's.
    /// </para>
    /// </remarks>
    Pile,
}

/// <summary>
/// How much ground a node stands on, in placement cells, and how near a body has to get to reach it.
/// </summary>
/// <remarks>
/// <b>An odd number of cells, always.</b> A building is described in the grid the game already uses for
/// built ground — both the navigation raster and the static side of the velocity solve derive from it, so
/// a footprint that does not line up with it cannot be described exactly by either. Odd counts centre on
/// a cell, which means the building is symmetric about its own position and the wall, the drawing and the
/// arrival tolerance are the same square. An even count would have to centre on a cell corner and every
/// one of those three would round differently.
/// <para>
/// It was <em>one</em> cell for everything, which was 1.5 m — a granary the size of a garden shed
/// standing next to a 1.45 m villager. One cell was not a design decision, it was forced: while a second
/// body class existed at 0.90 m, anything larger left no cell beside it whose clearance a 0.90 body could
/// use, so the ring a cart was told to stand on fell inside the wall. Retiring that class removed the
/// constraint — the widest routing radius is now 0.37 m and the clearance one cell out from a wall is
/// 0.75 — so buildings can be the size they should have been.
/// </para>
/// </remarks>
internal static class NodeFootprint
{
    /// <summary>Placement cells across, odd so the building centres on one.</summary>
    /// <remarks>
    /// Five cells is 7.5 m for a granary — a communal barn beside a 1.45 m person — and three is 4.5 m
    /// for everything else, which is a cottage or a farmyard. The grid quantises to 1.5 m, so the
    /// available sizes are 1.5, 4.5 and 7.5, and there is no point pretending to finer control than the
    /// thing being described.
    /// </remarks>
    public static int CellsOf(NodeKind kind) => kind switch
    {
        NodeKind.Granary => 5,
        NodeKind.ForwardDepot => 3,
        NodeKind.Farm => 3,
        NodeKind.House => 3,
        _ => 0,
    };

    /// <summary>Half the width of a tree, which is not a multiple of a placement cell.</summary>
    /// <remarks>
    /// A trunk is a trunk. It is not built ground and it is not described in the grid built ground is
    /// described in, so it does not have to quantise to 1.5 m — and it must not, or a cutter would be
    /// told to stand two metres back from the thing it is felling.
    /// </remarks>
    internal const float TreeHalfExtent = 0.45f;

    /// <summary>Half the width of a node's own footprint, in metres.</summary>
    public static float HalfExtentOf(NodeKind kind) => kind switch
    {
        NodeKind.Tree => TreeHalfExtent,
        _ => CellsOf(kind) * SimulationWorld.PlacementCellSize * 0.5f,
    };

    /// <summary>
    /// Radius of the circle that just contains a building, which is what arrival is measured against.
    /// </summary>
    /// <remarks>
    /// The half-diagonal rather than the half-width, because a body approaching a corner is further from
    /// the centre than one approaching a face, and a tolerance drawn at the face would be one a diagonal
    /// approach could never satisfy — the body would be held off the corner by its own radius and go on
    /// walking at a place it had already reached.
    /// </remarks>
    public static float ApproachRadiusOf(NodeKind kind) => kind == NodeKind.Pile
        // A heap is not a wall. Walk right up to it.
        ? 0.4f
        : HalfExtentOf(kind) * 1.41421356f;

    /// <summary>
    /// Whether this kind of node is built ground that bodies must go around.
    /// </summary>
    /// <remarks>
    /// <b>Not the same question as how big it is,</b> which is why it is a separate list rather than
    /// <c>CellsOf(kind) > 0</c>. A field has a footprint — it is 4.5 m across, its hands are counted
    /// against its wall, and it is drawn at that size — and a field is <em>not a wall</em>. It is
    /// tilled ground. You walk onto it to work it, a cart cuts across the corner of it, and a dozen
    /// of them tile into one contiguous patchwork the way fields actually do.
    /// <para>
    /// Conflating the two made every farm a 4.5 m obstacle: twelve of them ringed round a granary
    /// turned the middle of the settlement into a maze, every farmhand had to be routed to the
    /// outside face of its own field, and the fields could not be laid edge to edge without sealing
    /// the settlement in. It is also what the earlier complaint about bodies getting stuck in farms
    /// was: they were not getting stuck, they were going round.
    /// </para>
    /// </remarks>
    public static bool Blocks(NodeKind kind) =>
        kind is NodeKind.Granary or NodeKind.ForwardDepot or NodeKind.House;
}

/// <summary>
/// A place that produces, stores, or both.
/// </summary>
/// <remarks>
/// Plain data, no references, like a body — so a node is saved by memory copy, read by the determinism
/// fingerprint without being asked, and referred to by a stable id rather than a pointer. §5's
/// serialization discipline is not a separate concern here; it is what the struct is.
/// </remarks>
internal struct EconomyNode
{
    public NodeId Id;
    public NodeKind Kind;
    public FactionId Faction;
    public Vector2 Position;

    /// <summary>Whole units of each resource physically here.</summary>
    public NodeStock Stock;

    /// <summary>Sub-unit production and draw not yet worth a whole unit.</summary>
    public NodePending Pending;

    /// <summary>Units of any one resource this place can hold.</summary>
    public int Capacity;

    /// <summary>
    /// Pairs of hands working here. §6: output is continuous in them, with diminishing returns.
    /// </summary>
    public int Hands;

    /// <summary>What this node produces, if it produces.</summary>
    public Resource Produces;

    /// <summary>Catchment budget in seconds at hauler pace, for a node that owns one.</summary>
    public float CatchmentSeconds;

    /// <summary>
    /// Labour-seconds spent putting this building up.
    /// </summary>
    /// <remarks>
    /// A node whose labour is not yet spent is that node, unfinished — see <see cref="Construction"/>. It
    /// occupies its final footprint from the moment it is placed, so bodies route around it while it is a
    /// site and completion re-rasterises nothing.
    /// </remarks>
    public float BuildWork;

    /// <summary>
    /// How well this ground grows grain, as a multiple of what level neutral ground grows.
    /// </summary>
    /// <remarks>
    /// <b>A property of the ground, read once when the field is placed.</b> Not sampled per tick, for three
    /// reasons that agree: the economy tick has no business reaching into the terrain, a save has to round-trip
    /// what a field is worth, and a field's fertility is settled when the ground is broken — which is the same
    /// thing <see cref="CropCycle"/> already says about the ceiling.
    /// <para>
    /// One on flat ground, and that is structural rather than a default: a map with no generated relief has no
    /// soil field to ask, so every calibrated scenario keeps the exact numbers it was measured with. Zero is a
    /// legitimate value — open water and bare crag grow nothing — so it is set explicitly at the one place
    /// nodes are made rather than left to the struct's default, where an unset field would silently be barren.
    /// </para>
    /// </remarks>
    public float Fertility;

    /// <summary>Labour-seconds of ground broken on this field this year. Sets its ceiling.</summary>
    public float PrepareWork;

    /// <summary>Labour-seconds of tending. Keeps the ceiling; cannot raise it.</summary>
    public float MaintainWork;

    /// <summary>Labour-seconds of reaping. Earns the crop as it goes.</summary>
    public float ReapWork;

    /// <summary>
    /// Year this field's three figures belong to, so a new spring starts from nothing.
    /// </summary>
    /// <remarks>
    /// Stored rather than cleared on a season boundary, because a season boundary is not an event anything
    /// subscribes to — the calendar is derived from the tick and nothing is notified. A field that finds
    /// itself in a year it has not worked yet resets itself, which needs no notification and survives a
    /// save landing mid-spring.
    /// </remarks>
    public int CycleYear;

    /// <summary>People this house has room for.</summary>
    public int Occupancy;

    /// <summary>People currently living here. Counted every tick, like hands.</summary>
    public int Occupants;

    /// <summary>
    /// Appetites of the people living here, added up.
    /// </summary>
    /// <remarks>
    /// Summed rather than averaged, and summed rather than taken as occupants times one appetite,
    /// because a household of two villagers and a soldier eats what those three eat. §6 says soldiers
    /// eat more, and this is where that arrives at a granary.
    /// </remarks>
    public float AppetiteSum;

    /// <summary>
    /// Progress toward the next villager born in this house, in seconds of eligibility.
    /// </summary>
    /// <remarks>
    /// On the house rather than on the settlement, because growth is proportional to housing built rather
    /// than to population — see <see cref="Population.PersonSeconds"/>. It also means the progress is
    /// <em>somewhere</em>: a house filling up is a thing on the map, and a house that stopped filling up
    /// because it went outside a catchment is a thing you can point at.
    /// </remarks>
    public float Growth;

    /// <summary>
    /// Seconds this household has gone without what it asked for.
    /// </summary>
    /// <remarks>
    /// The consequence of a shortage, and the reason it is not merely a counter: enough of this and
    /// somebody leaves. Accrued and drained here rather than settlement-wide, so a house outside every
    /// catchment empties itself while the ones inside do not — which puts the mistake on the map instead
    /// of in a shortfall total.
    /// </remarks>
    public float Privation;

    /// <summary>
    /// The store this sink draws from, or none if it stands outside every catchment.
    /// </summary>
    /// <remarks>
    /// Bound per node rather than per body, and only when the set of stores changes — a house does not
    /// move, so the question of which granary reaches it has a static answer. That is also why this
    /// moved off the bodies: asking every villager every few seconds which store fed it meant a routing
    /// query per person, and the answer never depended on the person.
    /// </remarks>
    public NodeId Supply;

    /// <summary>Route seconds from here to <see cref="Supply"/>, for the overlay.</summary>
    public float SupplySeconds;

    /// <summary>Node-network revision the supply binding was made against.</summary>
    public int SupplyRevision;

    /// <summary>Collider standing in for this node, so bodies do not walk through it.</summary>
    public ColliderId Collider;

    public bool IsAlive;

    /// <summary>
    /// Whether this building is finished.
    /// </summary>
    /// <remarks>
    /// <b>Every predicate below asks this, which is the point.</b> An unfinished granary stores nothing,
    /// feeds nobody and owns no catchment — and putting the test inside the predicates rather than at their
    /// twenty-six call sites means there are no twenty-six chances to forget one. Kinds that need no
    /// building answer true from the moment they exist, because their labour requirement is zero.
    /// </remarks>
    public readonly bool IsBuilt => BuildWork >= Construction.LabourFor(Kind);

    /// <summary>A site: placed, standing on its ground, and not yet a building.</summary>
    public readonly bool IsUnderConstruction => !IsBuilt;

    /// <summary>Timber still wanted on site before work can begin.</summary>
    public readonly int TimberWanted => IsBuilt
        ? 0
        : Math.Max(0, Construction.TimberFor(Kind) - Stock.Wood);

    /// <summary>A place worked for what it yields. Fields; nothing else, now that wood is trees.</summary>
    public readonly bool Produces_ => IsBuilt && Kind == NodeKind.Farm;

    /// <summary>
    /// A place labour is spent at: a field to be worked, a tree to be cut, or a building to be raised.
    /// </summary>
    public readonly bool IsWorkSite =>
        IsUnderConstruction || Kind is NodeKind.Farm or NodeKind.Tree;

    /// <summary>
    /// Stock that is not yet a resource — timber still standing in a tree.
    /// </summary>
    /// <remarks>
    /// Counted by conservation, because it is physically there and it will be somebody's wood, and
    /// excluded from every question about what the settlement <em>has</em>. Without the distinction a
    /// woodland in reach reads as twenty-five thousand wood in store and the autonomy figure — the one
    /// number the HUD is for — becomes a statement about the forest rather than about the winter.
    /// </remarks>
    public readonly bool IsStanding => Kind == NodeKind.Tree;

    /// <summary>Somewhere goods can be delivered to. A pile is not: nobody delivers to a pile.</summary>
    public readonly bool Stores => IsBuilt && Kind is NodeKind.Granary or NodeKind.ForwardDepot;

    /// <summary>Somewhere a cart can unload — a store, or a site waiting for its materials.</summary>
    public readonly bool AcceptsDeliveries => Stores || TimberWanted > 0;

    public readonly bool OwnsCatchment => IsBuilt && Kind is NodeKind.Granary or NodeKind.ForwardDepot;

    /// <summary>Goods lying on the ground, which anybody may come for.</summary>
    public readonly bool IsPile => Kind == NodeKind.Pile;

    /// <summary>A clocked sink: it draws, and it is the last place a resource is physical.</summary>
    public readonly bool IsSink => IsBuilt && Kind == NodeKind.House;

    /// <summary>Room for another household member.</summary>
    public readonly int Housing => Math.Max(0, Occupancy - Occupants);

    /// <summary>How near a body has to get to have reached this node.</summary>
    public readonly float FootprintRadius => NodeFootprint.ApproachRadiusOf(Kind);

    /// <summary>Half the width of this node's building, in metres.</summary>
    public readonly float HalfExtent => NodeFootprint.HalfExtentOf(Kind);

    /// <summary>
    /// Room left for more of this resource.
    /// </summary>
    /// <remarks>
    /// A site takes exactly its timber and nothing else, which is what makes it a valid destination for a
    /// haul without needing a second capacity field: <see cref="Capacity"/> goes on meaning what the
    /// finished building will hold, and the limit while it is a site is computed from what it is becoming.
    /// </remarks>
    public readonly int RoomFor(Resource resource) => IsUnderConstruction
        ? resource == Resource.Wood ? TimberWanted : 0
        : Math.Max(0, Capacity - Stock[resource]);
}

/// <summary>
/// Every node in the world, in slots, with tombstones.
/// </summary>
/// <remarks>
/// Deliberately the same shape as <c>AgentStore</c>, down to ids never being reused: a node id is an
/// array index, so a destroyed granary leaves a hole rather than renumbering every node after it, and
/// a hauling assignment that still names the dead node resolves to a dead node and is rejected rather
/// than resolving to somebody else's farm.
/// </remarks>
internal sealed class NodeStore
{
    private EconomyNode[] nodes = new EconomyNode[16];

    /// <summary>Slots ever allocated. Iteration bounds; not the number of nodes.</summary>
    public int Count { get; private set; }

    /// <summary>Nodes currently in the world.</summary>
    public int LiveCount { get; private set; }

    /// <summary>Bumped whenever the set of nodes changes, so catchments know to re-bind.</summary>
    public int Revision { get; private set; }

    public ReadOnlySpan<EconomyNode> All => nodes.AsSpan(0, Count);

    public Span<EconomyNode> MutableSpan() => nodes.AsSpan(0, Count);

    public bool Contains(NodeId id) =>
        id.Value >= 0 && id.Value < Count && nodes[id.Value].IsAlive;

    public ref EconomyNode Get(NodeId id)
    {
        if (!Contains(id)) throw new ArgumentOutOfRangeException(nameof(id), $"Unknown {id}.");
        return ref nodes[id.Value];
    }

    public NodeId Add(EconomyNode node)
    {
        if (Count == nodes.Length) Array.Resize(ref nodes, nodes.Length * 2);
        var id = new NodeId(Count);
        node.Id = id;
        node.IsAlive = true;
        nodes[Count++] = node;
        LiveCount++;
        // Only a node that feeds somebody changes who is fed by what. A sack of grain appearing on the
        // road does not, and bumping the revision for one would send every body in the world to
        // reconsider which granary supplies it every time a cart was destroyed.
        if (node.OwnsCatchment) Revision++;
        return id;
    }

    public bool Remove(NodeId id)
    {
        if (!Contains(id)) return false;
        ref var node = ref nodes[id.Value];
        var fed = node.OwnsCatchment;
        node.IsAlive = false;
        node.Stock = default;
        node.Pending = default;
        node.Hands = 0;
        node.Occupants = 0;
        node.AppetiteSum = 0f;
        node.PrepareWork = 0f;
        node.MaintainWork = 0f;
        node.ReapWork = 0f;
        node.Growth = 0f;
        node.Privation = 0f;
        node.BuildWork = 0f;
        LiveCount--;
        if (fed) Revision++;
        return true;
    }

    /// <summary>Everything physically anywhere, which conservation is checked against.</summary>
    /// <remarks>
    /// Timber delivered to a building site counts, because it is timber sitting on the ground at a place —
    /// it stops being timber only when the building is finished and it is consumed into it.
    /// </remarks>
    /// <remarks>
    /// Timber still standing in a tree is included, and has to be: it was seeded into the world, so it
    /// is on the left of the identity, and a cutter moving it from a trunk to its own hands must not
    /// look like a unit appearing out of nothing. What a settlement <em>has</em> is a different question
    /// — see <see cref="TotalHeld"/>.
    /// </remarks>
    public NodeStock TotalStored()
    {
        var total = default(NodeStock);
        foreach (ref readonly var node in All)
        {
            if (!node.IsAlive) continue;
            total.Grain += node.Stock.Grain;
            total.Wood += node.Stock.Wood;
        }

        return total;
    }

    /// <summary>Everything the settlement actually has: stores and heaps, not standing timber.</summary>
    public NodeStock TotalHeld()
    {
        var total = default(NodeStock);
        foreach (ref readonly var node in All)
        {
            if (!node.IsAlive || node.IsStanding) continue;
            total.Grain += node.Stock.Grain;
            total.Wood += node.Stock.Wood;
        }

        return total;
    }

    /// <summary>Timber left standing, and in how many trees.</summary>
    public (int Wood, int Trees) StandingTimber()
    {
        var wood = 0;
        var trees = 0;
        foreach (ref readonly var node in All)
        {
            if (!node.IsAlive || !node.IsStanding) continue;
            wood += node.Stock.Wood;
            trees++;
        }

        return (wood, trees);
    }

    internal void Write(WorldWriter writer)
    {
        writer.Int(LiveCount);
        writer.Int(Revision);
        writer.Blob<EconomyNode>(nodes.AsSpan(0, Count));
    }

    internal void Read(WorldReader reader)
    {
        LiveCount = reader.Int();
        Revision = reader.Int();
        var loaded = reader.Blob<EconomyNode>();
        nodes = loaded.Length == 0 ? new EconomyNode[16] : loaded;
        Count = loaded.Length;
    }
}
