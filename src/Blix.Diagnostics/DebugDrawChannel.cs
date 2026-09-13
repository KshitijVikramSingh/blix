using System.Diagnostics;
using Blix.Core;
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
// Primitives belong to a VIEW, declared for the frame that draws them. The
// channel used to hold one ViewProjection for the whole frame, justified as
// "every primitive in a single frame shares one camera" — an assumption
// about the world rather than a cost decision, and the one this arc retires.
// Viewports, picking, editor cameras, off-screen capture and looking at the
// same geometry through a second camera were five features while that was a
// field; they are one mechanism now.
public sealed class DebugDrawChannel
{
    private readonly DebugContext context;
    private readonly Stopwatch clock;
    private readonly List<DebugDrawCommand> commands = new();
    private readonly List<ViewDeclaration> views = new();
    private readonly Stack<ViewId> scopes = new();

    internal DebugDrawChannel(DebugContext context, Stopwatch clock)
    {
        this.context = context;
        this.clock = clock;
    }

    public IReadOnlyList<DebugDrawCommand> Commands => commands;

    /// <summary>The views declared this frame, in declaration order.</summary>
    public IReadOnlyList<ViewDeclaration> Views => views;

    /// <summary>
    /// Draws everything in the block into <paramref name="view"/>.
    /// </summary>
    /// <remarks>
    /// <b>Declaring and using are the same act, deliberately.</b> A view referenced by a command is
    /// therefore always present in the same frame, so a dump can be read without replaying the frame that
    /// produced it — you cannot draw into a view you did not declare, by construction.
    /// <para>
    /// Ambient at the call site and resolved-then-stored on the command, which is exactly the shape
    /// <see cref="DebugContext.Scope"/> already uses for paths. Ambient-only would re-create the mutable
    /// global that needed an emit-once warning to explain itself; stored-only would be noise at every call
    /// site. Declarations are VALUES rebuilt each frame — that is what stops a frozen frame from
    /// re-rendering through a camera that has since moved, which would look exactly like the diagnostics
    /// lying.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Declares a view covering the whole default surface, which is the common case.
    /// </summary>
    /// <remarks>
    /// Target and rect come from the frame's own <see cref="RenderFrameContext"/> — the degenerate single
    /// view Blix always had. This is shorthand, not a default: the name is still given, the declaration
    /// still happens, and drawing outside a scope still throws.
    /// </remarks>
    /// <remarks>
    /// <b>Both rectangles are the framebuffer's, which is only right when they agree.</b>
    /// <see cref="RenderFrameContext"/> is PHYSICAL pixels — 2x logical on a Retina display — and a
    /// pointer arrives in logical ones. So a view declared this way and then picked through is off by
    /// the backing scale, which is the exact bug a view carrying both rectangles exists to prevent.
    /// <para>
    /// Fine for drawing, where only the matrix matters. An application that PICKS through a view should
    /// declare it with the overload below, passing <see cref="IRenderHost.LogicalSize"/> for the logical
    /// rectangle — the host is the only thing that knows the scale.
    /// </para>
    /// </remarks>
    public ViewDeclaration Declare(string name, Matrix4x4 viewProjection) =>
        context.State.Views.Declare(
            name, viewProjection, RenderSurfaceHandle.Default, context.Frame.Width, context.Frame.Height);

    /// <summary>Declares a view onto an explicit surface and rectangle.</summary>
    public ViewDeclaration Declare(
        string name,
        Matrix4x4 viewProjection,
        RenderSurfaceHandle target,
        Rect logicalViewport,
        Rect physicalViewport) =>
        context.State.Views.Declare(name, viewProjection, target, logicalViewport, physicalViewport);

    /// <summary>Declares a whole-surface view and draws into it, in one call.</summary>
    public IDisposable In(string name, Matrix4x4 viewProjection) => In(Declare(name, viewProjection));

    public IDisposable In(ViewDeclaration view)
    {
        if (!view.Id.IsValid)
        {
            throw new ArgumentException(
                $"View '{view.Name}' has no id; declare it through ViewTable rather than building one by hand.",
                nameof(view));
        }

        var already = false;
        for (var i = 0; i < views.Count && !already; i++) already = views[i].Id == view.Id;
        if (!already) views.Add(view);

        scopes.Push(view.Id);
        return new ViewScope(this);
    }

    /// <summary>
    /// The view being drawn into, which there must be one of.
    /// </summary>
    /// <remarks>
    /// <b>Throws rather than defaulting.</b> Before views, a primitive emitted without a camera got the
    /// identity matrix, rendered in clip space, was invisible, and needed a printed warning to explain
    /// itself to whoever had lost an afternoon. A primitive with nowhere to be seen from is a bug in the
    /// producer, and the same call was settled the same way one layer down when BeginActivity was made to
    /// throw on a nameless act.
    /// </remarks>
    public ViewId CurrentView => scopes.Count > 0
        ? scopes.Peek()
        : throw new InvalidOperationException(
            "Debug primitives were emitted outside any view. Wrap them in `using (debug.Draw.In(view))`, " +
            "where `view` came from ViewTable.Declare(...).");

    private void PopView() => scopes.Pop();

    private sealed class ViewScope : IDisposable
    {
        private readonly DebugDrawChannel channel;
        private bool disposed;

        internal ViewScope(DebugDrawChannel channel) => this.channel = channel;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            channel.PopView();
        }
    }

    public void Line(string name, Vector3 a, Vector3 b, GraphicsColor color)
    {
        commands.Add(new DebugDrawLine(context.BuildPath(name), color, CurrentView, a, b));
    }

    public void Aabb(string name, Vector3 min, Vector3 max, GraphicsColor color)
    {
        commands.Add(new DebugDrawAabb(context.BuildPath(name), color, CurrentView, min, max));
    }

    public void Grid(string name, Vector3 center, float size, int divisions, GraphicsColor color)
    {
        commands.Add(new DebugDrawGrid(context.BuildPath(name), color, CurrentView, center, size, divisions));
    }

    public void Frustum(string name, Matrix4x4 viewProjection, GraphicsColor color)
    {
        commands.Add(new DebugDrawFrustum(context.BuildPath(name), color, CurrentView, viewProjection));
    }

    public void Sphere(string name, Vector3 center, float radius, GraphicsColor color, int segments = 24)
    {
        commands.Add(new DebugDrawSphere(context.BuildPath(name), color, CurrentView, center, radius, segments));
    }

    public void Plane(string name, Vector3 center, Vector3 normal, float size, GraphicsColor color)
    {
        commands.Add(new DebugDrawPlane(context.BuildPath(name), color, CurrentView, center, normal, size));
    }

    public void Ray(string name, Vector3 origin, Vector3 direction, float length, GraphicsColor color)
    {
        commands.Add(new DebugDrawRay(context.BuildPath(name), color, CurrentView, origin, direction, length));
    }

    public void Capsule(string name, Vector3 a, Vector3 b, float radius, GraphicsColor color, int segments = 16)
    {
        commands.Add(new DebugDrawCapsule(context.BuildPath(name), color, CurrentView, a, b, radius, segments));
    }

    // Obb takes a transform that maps the unit cube [-1, 1]^3 into world
    // space. For a (center, rotation, halfExtents) layout, the caller
    // composes: Matrix4x4.CreateScale(halfExtents) * rotation * Matrix4x4.CreateTranslation(center)
    // — keeping the matrix-vs-tuple decision at the producer site.
    public void Obb(string name, Matrix4x4 transform, GraphicsColor color)
    {
        commands.Add(new DebugDrawObb(context.BuildPath(name), color, CurrentView, transform));
    }

    /// <summary>
    /// Records where this thing is now, and draws everywhere it has been for the last few seconds.
    /// </summary>
    /// <remarks>
    /// <b>The one primitive with a memory.</b> Every other call here describes this instant; a trail is a
    /// standing question — "where has this been going?" — which nothing in diagnostics could ask, because
    /// a command could not outlive its frame.
    /// <para>
    /// Call it every frame with the current position. It only draws on frames where it is called, so a
    /// producer that stops asking stops painting, and nothing emits behind the caller's back. The points
    /// are remembered on <see cref="DebugState.Trails"/> keyed by path, so the same trail drawn in two
    /// views is one history seen twice rather than two histories.
    /// </para>
    /// </remarks>
    public void Trail(string name, Vector3 point, GraphicsColor color, float seconds = 3f)
    {
        var path = context.BuildPath(name);
        var remembered = context.State.Trails.Append(
            path, point, clock.Elapsed.TotalMilliseconds, seconds, context.FrameNumber);

        // Copied, because the store rewrites that list next frame and a snapshot must not change under a
        // sink that is already holding it.
        var points = new Vector3[remembered.Count];
        for (var i = 0; i < points.Length; i++) points[i] = remembered[i];
        commands.Add(new DebugDrawPolyline(path, color, CurrentView, points));
    }

    /// <summary>Draws an explicit path through space, remembering nothing.</summary>
    public void Polyline(string name, IReadOnlyList<Vector3> points, GraphicsColor color)
    {
        ArgumentNullException.ThrowIfNull(points);
        commands.Add(new DebugDrawPolyline(context.BuildPath(name), color, CurrentView, points));
    }

    public void Cross(string name, Vector3 center, float size, GraphicsColor color)
    {
        commands.Add(new DebugDrawCross(context.BuildPath(name), color, CurrentView, center, size));
    }

    public void Cone(string name, Vector3 apex, Vector3 axis, float length, float halfAngleRad, GraphicsColor color, int segments = 16)
    {
        commands.Add(new DebugDrawCone(context.BuildPath(name), color, CurrentView, apex, axis, length, halfAngleRad, segments));
    }

    public void Arrow(string name, Vector3 from, Vector3 to, GraphicsColor color)
    {
        commands.Add(new DebugDrawArrow(context.BuildPath(name), color, CurrentView, from, to));
    }

    // The arrays are held by reference for the lifetime of any frame
    // snapshot — see DebugDrawMeshWireframe for the immutability contract.
    public void MeshWireframe(string name, IReadOnlyList<Vector3> vertices, IReadOnlyList<int> edges, GraphicsColor color)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(edges);
        commands.Add(new DebugDrawMeshWireframe(context.BuildPath(name), color, CurrentView, vertices, edges));
    }

    public void Normals(string name, IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> normals, float length, GraphicsColor color)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(normals);
        commands.Add(new DebugDrawNormals(context.BuildPath(name), color, CurrentView, positions, normals, length));
    }
}
