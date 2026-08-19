using System.Numerics;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Navigation;
using RTSGame.Simulation.Persistence;

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

    /// <summary>Spawns a unit of the given type.</summary>
    public AgentId Spawn(Vector2 position, FactionId faction, UnitType type) => Spawn(
        position,
        faction,
        type.Radius,
        type.MaximumSpeed,
        type.NavigationRadius,
        type.TurningRadius,
        type.CarryCapacity,
        type.Appetite);

    public AgentId Spawn(
        Vector2 position,
        FactionId faction,
        float radius = AgentDefaults.Radius,
        float maximumSpeed = AgentDefaults.MaximumSpeed,
        float navigationRadius = 0f,
        float turningRadius = 0f,
        int carryCapacity = 0,
        float appetite = 1f)
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
            // A body spawned as a bare radius routes at that radius, which is what every
            // scenario predating unit types expects and is correct for any single-class world.
            NavigationRadius = navigationRadius > 0f ? navigationRadius : radius,
            TurningRadius = turningRadius,
            CarryCapacity = carryCapacity,
            Appetite = appetite,
            MaximumSpeed = maximumSpeed,
            Acceleration = AgentDefaults.Acceleration,
            Deceleration = AgentDefaults.Deceleration,
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

    /// <summary>Top speed of the fastest body currently alive, in metres per second.</summary>
    /// <remarks>
    /// The velocity solve's horizon widens with how fast a pair can close, so the broad phase has
    /// to reach out by the fastest body in the world for the same reason it reaches out by the
    /// biggest: it cannot know which neighbour it is about to find.
    /// </remarks>
    public float LargestSpeed()
    {
        var largest = 0f;
        foreach (ref readonly var agent in All)
        {
            if (agent.IsAlive) largest = MathF.Max(largest, agent.MaximumSpeed);
        }

        return largest;
    }

    /// <summary>Radius of the largest body currently alive, in metres.</summary>
    /// <remarks>
    /// Any broad phase that has to find every body a given one could be touching has to reach out
    /// by the biggest radius in the world, not by its own — so both the velocity solve and
    /// depenetration want this number and it is defined once. Zero when nothing is alive, which is
    /// the right answer: an empty query.
    /// </remarks>
    public float LargestRadius()
    {
        var largest = 0f;
        foreach (ref readonly var agent in All)
        {
            if (agent.IsAlive) largest = MathF.Max(largest, agent.Radius);
        }

        return largest;
    }

    /// <summary>Writes every slot, tombstones included, as one run of bytes.</summary>
    /// <remarks>
    /// No per-field code, and that is the design rather than a shortcut: <see cref="AgentState"/> is
    /// plain data all the way down, so the whole array is a memory copy and <b>a field added to a
    /// body is saved the day it is declared</b>. The same property makes the determinism fingerprint
    /// automatic, and the <c>unmanaged</c> constraint on <see cref="WorldWriter.Blob{T}"/> is what
    /// keeps it true: putting a reference on a body stops this compiling.
    /// <para>
    /// Dead slots go out with the living. An id is an index here and is never reused, so dropping
    /// tombstones would renumber every survivor after the gap and silently repoint the queue-leader
    /// chain and every per-agent diagnostic buffer at a different unit.
    /// </para>
    /// </remarks>
    internal void Write(WorldWriter writer)
    {
        writer.Int(LiveCount);
        writer.Blob<AgentState>(agents.AsSpan(0, Count));
    }

    internal void Read(WorldReader reader)
    {
        LiveCount = reader.Int();
        var loaded = reader.Blob<AgentState>();
        agents = loaded.Length == 0 ? new AgentState[64] : loaded;
        Count = loaded.Length;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= agents.Length) return;
        Array.Resize(ref agents, Math.Max(required, agents.Length * 2));
    }
}
