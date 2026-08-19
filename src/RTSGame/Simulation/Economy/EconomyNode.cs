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

    /// <summary>Collider standing in for this node, so bodies do not walk through it.</summary>
    public ColliderId Collider;

    public bool IsAlive;

    public readonly bool Produces_ => Kind is NodeKind.Farm or NodeKind.Woodcutter;
    public readonly bool Stores => Kind is NodeKind.Granary or NodeKind.ForwardDepot;
    public readonly bool OwnsCatchment => Kind is NodeKind.Granary or NodeKind.ForwardDepot;

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
        Revision++;
        return id;
    }

    public bool Remove(NodeId id)
    {
        if (!Contains(id)) return false;
        ref var node = ref nodes[id.Value];
        node.IsAlive = false;
        node.Stock = default;
        node.Pending = default;
        node.Hands = 0;
        LiveCount--;
        Revision++;
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
