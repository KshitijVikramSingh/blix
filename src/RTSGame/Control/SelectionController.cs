using System.Numerics;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;

namespace RTSGame.Control;

internal sealed class SelectionController
{
    private const float DragThreshold = 6f;
    private const float ClickRadius = 18f;
    private readonly HashSet<AgentId> selected = new();

    public IReadOnlyCollection<AgentId> Selected => selected;

    public bool IsPointerDown { get; private set; }

    public Vector2 DragStart { get; private set; }

    public Vector2 DragCurrent { get; private set; }

    public bool IsMarqueeVisible => IsPointerDown &&
        Vector2.DistanceSquared(DragStart, DragCurrent) > DragThreshold * DragThreshold;

    public bool Contains(AgentId id) => selected.Contains(id);

    public AgentId[] Snapshot() => selected.OrderBy(id => id.Value).ToArray();

    public void ReplaceWith(IEnumerable<AgentId> agents)
    {
        selected.Clear();
        foreach (var id in agents) selected.Add(id);
    }

    public void Begin(float screenX, float screenY)
    {
        IsPointerDown = true;
        DragStart = DragCurrent = new Vector2(screenX, screenY);
    }

    public void Update(float screenX, float screenY)
    {
        if (IsPointerDown) DragCurrent = new Vector2(screenX, screenY);
    }

    /// <param name="player">
    /// Whose units may be selected.
    /// <b>Added before an opponent went on the map, because until then there was nobody else's to take.</b>
    /// §133: a marquee had no faction filter, so dragging across a neighbour's village would have handed the
    /// player its villagers and the right to order them about — which is the same class of cheat
    /// <see cref="AI.SettlementBot"/> is built to make impossible in the other direction, and it would have
    /// been the player's to commit.
    /// </param>
    public void End(
        AgentStore agents,
        Func<Vector2, float> groundAt,
        Matrix4x4 viewProjection,
        int width,
        int height,
        bool additive,
        FactionId player)
    {
        if (!IsPointerDown) return;
        IsPointerDown = false;
        if (!additive) selected.Clear();

        if (Vector2.DistanceSquared(DragStart, DragCurrent) <= DragThreshold * DragThreshold)
        {
            SelectClick(agents, groundAt, viewProjection, width, height, additive, player);
            return;
        }

        var minimum = Vector2.Min(DragStart, DragCurrent);
        var maximum = Vector2.Max(DragStart, DragCurrent);
        foreach (ref readonly var agent in agents.All)
        {
            // Alive and ours. The liveness test was missing too, which was harmless while a tombstone was the
            // only thing it could have caught.
            if (!agent.IsAlive || agent.Faction != player) continue;
            if (!TryProject(agent.Position, groundAt(agent.Position), viewProjection, width, height, out var screen))
            {
                continue;
            }

            if (screen.X >= minimum.X && screen.X <= maximum.X &&
                screen.Y >= minimum.Y && screen.Y <= maximum.Y)
            {
                selected.Add(agent.Id);
            }
        }
    }

    public void Clear()
    {
        selected.Clear();
        IsPointerDown = false;
    }

    private void SelectClick(
        AgentStore agents,
        Func<Vector2, float> groundAt,
        Matrix4x4 viewProjection,
        int width,
        int height,
        bool additive,
        FactionId player)
    {
        AgentId? nearest = null;
        var nearestDistanceSquared = ClickRadius * ClickRadius;
        foreach (ref readonly var agent in agents.All)
        {
            if (!agent.IsAlive || agent.Faction != player) continue;
            if (!TryProject(agent.Position, groundAt(agent.Position), viewProjection, width, height, out var screen))
            {
                continue;
            }
            var distanceSquared = Vector2.DistanceSquared(screen, DragCurrent);
            if (distanceSquared > nearestDistanceSquared) continue;
            nearestDistanceSquared = distanceSquared;
            nearest = agent.Id;
        }

        if (nearest is not { } id) return;
        if (additive && !selected.Add(id)) selected.Remove(id);
        else selected.Add(id);
    }

    /// <summary>
    /// Where a body appears on screen, at the height it is actually standing.
    /// </summary>
    /// <remarks>
    /// <b>The Y was the constant 0.8 — mid-chest on flat ground, and flat ground is the only place it was
    /// right.</b> Every selection this class makes went through here, so on generated terrain a villager on a
    /// hill was projected as though it were standing at the bottom of the map: the click radius and the marquee
    /// both tested a screen position tens of metres from the body being drawn. Reported from the chair as
    /// villager selection being "most weird" while buildings and trees read fine, which is precisely the split
    /// it would produce — those two are picked against boxes that sample the ground, and this was not.
    /// <para>
    /// The sixth flat-ground constant this project has found in a world that has hills in it, after the far
    /// plane, the detail radius, the ground draw radius, the shadow box and the site scorer's grade gate. The
    /// tell is always the same: a number that is correct at <c>y = 0</c> and never questioned, because for a
    /// long time <c>y</c> was always zero.
    /// </para>
    /// </remarks>
    private static bool TryProject(
        Vector2 position,
        float ground,
        Matrix4x4 viewProjection,
        int width,
        int height,
        out Vector2 screen)
    {
        var clip = Vector4.Transform(
            new Vector4(position.X, ground + 0.8f, position.Y, 1f), viewProjection);
        if (clip.W <= 0f)
        {
            screen = default;
            return false;
        }

        var inverseW = 1f / clip.W;
        var ndc = new Vector3(clip.X * inverseW, clip.Y * inverseW, clip.Z * inverseW);
        if (ndc.Z < 0f || ndc.Z > 1f)
        {
            screen = default;
            return false;
        }

        screen = new Vector2((ndc.X + 1f) * 0.5f * width, (ndc.Y + 1f) * 0.5f * height);
        return true;
    }
}
