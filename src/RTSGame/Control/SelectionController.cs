using System.Numerics;
using RTSGame.Simulation.Agents;

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

    public void End(
        AgentStore agents,
        Matrix4x4 viewProjection,
        int width,
        int height,
        bool additive)
    {
        if (!IsPointerDown) return;
        IsPointerDown = false;
        if (!additive) selected.Clear();

        if (Vector2.DistanceSquared(DragStart, DragCurrent) <= DragThreshold * DragThreshold)
        {
            SelectClick(agents, viewProjection, width, height, additive);
            return;
        }

        var minimum = Vector2.Min(DragStart, DragCurrent);
        var maximum = Vector2.Max(DragStart, DragCurrent);
        foreach (ref readonly var agent in agents.All)
        {
            if (!TryProject(agent.Position, viewProjection, width, height, out var screen)) continue;
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

    private void SelectClick(AgentStore agents, Matrix4x4 viewProjection, int width, int height, bool additive)
    {
        AgentId? nearest = null;
        var nearestDistanceSquared = ClickRadius * ClickRadius;
        foreach (ref readonly var agent in agents.All)
        {
            if (!TryProject(agent.Position, viewProjection, width, height, out var screen)) continue;
            var distanceSquared = Vector2.DistanceSquared(screen, DragCurrent);
            if (distanceSquared > nearestDistanceSquared) continue;
            nearestDistanceSquared = distanceSquared;
            nearest = agent.Id;
        }

        if (nearest is not { } id) return;
        if (additive && !selected.Add(id)) selected.Remove(id);
        else selected.Add(id);
    }

    private static bool TryProject(
        Vector2 position,
        Matrix4x4 viewProjection,
        int width,
        int height,
        out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(position.X, 0.8f, position.Y, 1f), viewProjection);
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
