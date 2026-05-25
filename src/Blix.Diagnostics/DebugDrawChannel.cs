using Blix.Graphics;
using System.Numerics;

namespace Blix.Diagnostics;

// Per-frame channel for spatial debug primitives. Producers push commands;
// the runtime translates them to GL line geometry. Each command is one
// of the sealed derived DebugDrawCommand records (see DebugDrawCommand.cs).
//
// Path resolution is consistent with the rest of the diagnostics surface:
// commands inherit the current scope at emit time. Phase 8 also makes the
// path the layer-toggle key — DebugState.LayersEnabled gates whether a
// renderer dispatches a given command based on prefix match.
//
// ViewProjection is per-channel state (not per-command) because every
// primitive in a single frame shares one camera. The runtime reads it
// once when submitting the debug-draw pass.
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
        commands.Add(new DebugDrawLine(context.BuildPath(name), color, a, b));
    }

    public void Aabb(string name, Vector3 min, Vector3 max, GraphicsColor color)
    {
        commands.Add(new DebugDrawAabb(context.BuildPath(name), color, min, max));
    }

    public void Grid(string name, Vector3 center, float size, int divisions, GraphicsColor color)
    {
        commands.Add(new DebugDrawGrid(context.BuildPath(name), color, center, size, divisions));
    }

    public void Frustum(string name, Matrix4x4 viewProjection, GraphicsColor color)
    {
        commands.Add(new DebugDrawFrustum(context.BuildPath(name), color, viewProjection));
    }

    public void Sphere(string name, Vector3 center, float radius, GraphicsColor color, int segments = 24)
    {
        commands.Add(new DebugDrawSphere(context.BuildPath(name), color, center, radius, segments));
    }

    public void Plane(string name, Vector3 center, Vector3 normal, float size, GraphicsColor color)
    {
        commands.Add(new DebugDrawPlane(context.BuildPath(name), color, center, normal, size));
    }

    public void Ray(string name, Vector3 origin, Vector3 direction, float length, GraphicsColor color)
    {
        commands.Add(new DebugDrawRay(context.BuildPath(name), color, origin, direction, length));
    }

    public void Capsule(string name, Vector3 a, Vector3 b, float radius, GraphicsColor color, int segments = 16)
    {
        commands.Add(new DebugDrawCapsule(context.BuildPath(name), color, a, b, radius, segments));
    }

    // Obb takes a transform that maps the unit cube [-1, 1]^3 into world
    // space. For a (center, rotation, halfExtents) layout, the caller
    // composes: Matrix4x4.CreateScale(halfExtents) * rotation * Matrix4x4.CreateTranslation(center)
    // — keeping the matrix-vs-tuple decision at the producer site.
    public void Obb(string name, Matrix4x4 transform, GraphicsColor color)
    {
        commands.Add(new DebugDrawObb(context.BuildPath(name), color, transform));
    }

    public void Cross(string name, Vector3 center, float size, GraphicsColor color)
    {
        commands.Add(new DebugDrawCross(context.BuildPath(name), color, center, size));
    }

    public void Cone(string name, Vector3 apex, Vector3 axis, float length, float halfAngleRad, GraphicsColor color, int segments = 16)
    {
        commands.Add(new DebugDrawCone(context.BuildPath(name), color, apex, axis, length, halfAngleRad, segments));
    }

    public void Arrow(string name, Vector3 from, Vector3 to, GraphicsColor color)
    {
        commands.Add(new DebugDrawArrow(context.BuildPath(name), color, from, to));
    }

    // The arrays are held by reference for the lifetime of any frame
    // snapshot — see DebugDrawMeshWireframe for the immutability contract.
    public void MeshWireframe(string name, IReadOnlyList<Vector3> vertices, IReadOnlyList<int> edges, GraphicsColor color)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(edges);
        commands.Add(new DebugDrawMeshWireframe(context.BuildPath(name), color, vertices, edges));
    }

    public void Normals(string name, IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> normals, float length, GraphicsColor color)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(normals);
        commands.Add(new DebugDrawNormals(context.BuildPath(name), color, positions, normals, length));
    }
}
