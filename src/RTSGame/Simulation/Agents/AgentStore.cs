using System.Numerics;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Navigation;

namespace RTSGame.Simulation.Agents;

internal sealed class AgentStore
{
    private AgentState[] agents = new AgentState[64];

    /// <summary>Slots ever allocated. Iteration bounds; not the number of units.</summary>
    public int Count { get; private set; }

    /// <summary>Units currently in the world.</summary>
    public int LiveCount { get; private set; }

    /// <summary>
    /// Every slot, including removed ones. Callers must skip
    /// <see cref="AgentState.IsAlive"/> being false.
    /// </summary>
    /// <remarks>
    /// Removal tombstones rather than compacting, because an agent's id is used
    /// directly as an array index in several places — the queue-leader chain, and
    /// every per-agent diagnostic buffer. Swapping the tail into a freed slot
    /// would silently repoint all of those at a different unit. Ids are never
    /// reused for the same reason, so a stale reference resolves to a dead agent
    /// and is rejected rather than resolving to somebody else.
    /// </remarks>
    public ReadOnlySpan<AgentState> All => agents.AsSpan(0, Count);

    public AgentId Spawn(
        Vector2 position,
        FactionId faction,
        float radius = AgentDefaults.Radius,
        float maximumSpeed = AgentDefaults.MaximumSpeed)
    {
        EnsureCapacity(Count + 1);
        var id = new AgentId(Count);
        agents[Count++] = new AgentState
        {
            Id = id,
            LocomotionState = AgentLocomotionState.Idle,
            Faction = faction,
            PreviousPosition = position,
            Position = position,
            HoldPosition = position,
            Facing = Vector2.UnitX,
            Destination = position,
            Radius = radius,
            MaximumSpeed = maximumSpeed,
            Acceleration = 16f,
            MaximumTurnSpeed = AgentDefaults.MaximumTurnSpeed,
            Path = PathHandle.None,
            BehaviorTarget = new AgentId(-1),
            IsAlive = true,
        };
        LiveCount++;
        return id;
    }

    public bool Contains(AgentId id) =>
        id.Value >= 0 && id.Value < Count && agents[id.Value].IsAlive;

    /// <summary>
    /// Removes a unit. The slot is neutralised as well as flagged, so any loop
    /// that forgets to check <see cref="AgentState.IsAlive"/> does nothing
    /// harmful rather than steering, colliding with, or being avoided by a body
    /// that is no longer in the world.
    /// </summary>
    public bool Despawn(AgentId id)
    {
        if (!Contains(id)) return false;
        ref var agent = ref agents[id.Value];
        agent.IsAlive = false;
        agent.HasDestination = false;
        agent.UsesFlowTransit = false;
        agent.Velocity = Vector2.Zero;
        agent.PreferredVelocity = Vector2.Zero;
        agent.MaximumSpeed = 0f;
        agent.Radius = 0f;
        agent.MoveGroupId = 0;
        agent.BehaviorTarget = new AgentId(-1);
        LiveCount--;
        return true;
    }

    public ref AgentState Get(AgentId id)
    {
        if (!Contains(id)) throw new ArgumentOutOfRangeException(nameof(id), $"Unknown {id}.");
        return ref agents[id.Value];
    }

    public Span<AgentState> MutableSpan() => agents.AsSpan(0, Count);

    private void EnsureCapacity(int required)
    {
        if (required <= agents.Length) return;
        Array.Resize(ref agents, Math.Max(required, agents.Length * 2));
    }
}
