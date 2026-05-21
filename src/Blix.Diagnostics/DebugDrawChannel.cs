using Blix.Graphics;
using System.Numerics;

namespace Blix.Diagnostics;

public enum DebugDrawCommandKind
{
    Line,
    Aabb,
    Grid,
    Frustum
}

public sealed record DebugDrawCommand(
    DebugDrawCommandKind Kind,
    string Path,
    Vector3 A,
    Vector3 B,
    GraphicsColor Color,
    float Size = 0.0f,
    int Divisions = 0,
    Matrix4x4 Matrix = default);

public sealed class DebugDrawChannel
{
    private readonly DebugContext context;
    private readonly List<DebugDrawCommand> commands = new();

    internal DebugDrawChannel(DebugContext context)
    {
        this.context = context;
    }

    public IReadOnlyList<DebugDrawCommand> Commands => commands;

    public Matrix4x4 ViewProjection { get; set; } = Matrix4x4.Identity;

    public void Line(string name, Vector3 a, Vector3 b, GraphicsColor color)
    {
        commands.Add(new DebugDrawCommand(DebugDrawCommandKind.Line, context.BuildPath(name), a, b, color));
    }

    public void Aabb(string name, Vector3 min, Vector3 max, GraphicsColor color)
    {
        commands.Add(new DebugDrawCommand(DebugDrawCommandKind.Aabb, context.BuildPath(name), min, max, color));
    }

    public void Grid(string name, Vector3 center, float size, int divisions, GraphicsColor color)
    {
        commands.Add(new DebugDrawCommand(DebugDrawCommandKind.Grid, context.BuildPath(name), center, Vector3.Zero, color, size, divisions));
    }

    public void Frustum(string name, Matrix4x4 viewProjection, GraphicsColor color)
    {
        commands.Add(new DebugDrawCommand(DebugDrawCommandKind.Frustum, context.BuildPath(name), Vector3.Zero, Vector3.Zero, color, Matrix: viewProjection));
    }
}
