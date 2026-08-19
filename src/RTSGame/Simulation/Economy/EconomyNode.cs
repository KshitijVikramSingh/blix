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

    /// <summary>Cuts wood. Seasonal in labour rather than in yield.</summary>
    Woodcutter,

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
/// How much ground a node stands on, and how near a body has to get to have reached it.
/// </summary>
/// <remarks>
/// <b>A building occupies exactly one placement cell.</b> Not a circle of whatever radius suited the
/// renderer — the placement grid is what the game already describes built ground with, both the
/// navigation raster and the static side of the velocity solve are derived from it, and a footprint
/// that does not line up with it cannot be described exactly by either.
/// <para>
/// The first attempt gave each kind its own radius and blocked every cell the circle touched, and the
/// quantisation bit immediately: a 1.7 m granary blocked a plus-shape four and a half metres across,
/// so the ring a cart was told to stand on — its radius plus the building's — was <em>inside the
/// wall</em>. Every hand in the settlement stalled and nothing was ever delivered. One cell, exactly,
/// removes the whole class of problem.
/// </para>
/// <para>
/// A granary being the same size as a house is a greybox simplification and is fine: what matters at
/// this stage is that the wall, the drawing and the arrival tolerance are the same wall. Real sizes
/// arrive with real art and become a cell count.
/// </para>
/// </remarks>
internal static class NodeFootprint
{
    /// <summary>Half the width of a building, which is half a placement cell.</summary>
    public static float HalfExtent => SimulationWorld.PlacementCellSize * 0.5f;

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
        : HalfExtent * 1.41421356f;

    /// <summary>Whether this kind of node is built ground that bodies must go around.</summary>
    public static bool Blocks(NodeKind kind) => kind != NodeKind.Pile;
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

    public readonly bool Produces_ => Kind is NodeKind.Farm or NodeKind.Woodcutter;

    /// <summary>Somewhere goods can be delivered to. A pile is not: nobody delivers to a pile.</summary>
    public readonly bool Stores => Kind is NodeKind.Granary or NodeKind.ForwardDepot;

    public readonly bool OwnsCatchment => Kind is NodeKind.Granary or NodeKind.ForwardDepot;

    /// <summary>Goods lying on the ground, which anybody may come for.</summary>
    public readonly bool IsPile => Kind == NodeKind.Pile;

    /// <summary>A clocked sink: it draws, and it is the last place a resource is physical.</summary>
    public readonly bool IsSink => Kind == NodeKind.House;

    /// <summary>Room for another household member.</summary>
    public readonly int Housing => Math.Max(0, Occupancy - Occupants);

    /// <summary>How near a body has to get to have reached this node.</summary>
    public readonly float FootprintRadius => NodeFootprint.ApproachRadiusOf(Kind);

    /// <summary>Room left for more of this resource.</summary>
    public readonly int RoomFor(Resource resource) => Math.Max(0, Capacity - Stock[resource]);
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
        LiveCount--;
        if (fed) Revision++;
        return true;
    }

    /// <summary>Everything stored anywhere, which conservation is checked against.</summary>
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
