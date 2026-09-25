using System.Numerics;
using Blix.Graphics;

namespace Blix.Core;

/// <summary>
/// A named view: somewhere a world is seen from, and where that picture lands.
/// </summary>
/// <remarks>
/// <b>The generalisation of <see cref="RenderFrameContext"/>.</b> Blix assumed one view for its whole
/// life — <c>DebugDrawChannel.ViewProjection</c> was a single mutable field, <c>DebugFrame</c> carried one
/// matrix, and the dump schema recorded one camera. The justification was written down as
/// "every primitive in a single frame shares one camera", which is a claim about the world rather than a
/// cost decision, and it is the claim being retired. Cost was never the objection either: DebugDrawFrustum
/// has always carried a full 4x4 per command.
/// <para>
/// Viewports, picking, editor cameras, off-screen capture and second-camera inspection are all the same
/// mechanism once a view is a thing with a name. They were five separate features while it was a field.
/// </para>
/// <para>
/// <b>It is TOLD its matrix; it never owns a camera.</b> <c>Camera3D</c> lives a layer above this one and
/// carries policy — projection convention, field of view, how orbiting and flying feel. A view that holds a
/// matrix stays inert, and then a game camera, an editor camera, a shadow cascade and a matrix somebody
/// composed by hand all feed the identical mechanism. The moment a view knows how to compute its own
/// matrix, every consumer has to agree with how it does that.
/// </para>
/// <para>
/// <b>It knows a handle, never a renderer.</b> <see cref="RenderSurfaceHandle"/> is an opaque int from the
/// graphics abstraction. Blix.Core does not reference Blix.Render and must not start: a view says where a
/// picture goes, not how it is drawn.
/// </para>
/// </remarks>
public readonly record struct ViewDeclaration(
    ViewId Id,
    string Name,
    Matrix4x4 ViewProjection,
    RenderSurfaceHandle Target,
    Rect LogicalViewport,
    Rect PhysicalViewport);

/// <summary>
/// A view's identity, stable for the lifetime of the process.
/// </summary>
/// <remarks>
/// <b>The one part of a view that is deliberately not per-frame.</b> Declarations are values rebuilt every
/// frame — that is what stops a frozen frame from re-rendering through a camera that has since moved. But
/// "the trail of this body in the top-down view over the last N frames" has to correlate the same view
/// across frames, so identity has to outlive the declaration that carries it. Interned from the name by
/// <see cref="ViewTable"/>, so it is also a dense index when something wants an array per view.
/// </remarks>
public readonly record struct ViewId(int Id)
{
    /// <summary>No view. Drawing against this is a bug, not a default — see ViewTable.</summary>
    public static readonly ViewId None = new(-1);

    public bool IsValid => Id >= 0;

    public override string ToString() => IsValid ? $"view#{Id}" : "view#none";
}

/// <summary>
/// Interns view names into stable ids. One table, so there is one id space.
/// </summary>
/// <remarks>
/// <b>Deliberately one instance, for the same reason there is one definition of arrival in the jobs
/// layer.</b> If diagnostics interned names in one table and picking interned them in another, "view 3"
/// would mean two different things and the two would drift — the near-synonym fault this codebase has paid
/// for more than once. Rendering and input take their ids from this same table when they need them.
/// <para>
/// <b>You do not need a table to have a view.</b> The one instance in this engine lives on
/// <c>DebugState</c>, which reads as "views are a diagnostics concept" and is not what it means. An id is
/// only ever used to GROUP things across frames — which debug commands belong to which picture, and which
/// trail remembers which points. Everything else about a view is in the <see cref="ViewDeclaration"/>
/// value: <c>ViewPicking.RayThrough</c> takes one and never looks at the id, and
/// <c>Blix.Test.Graphics</c> Section AR picks through declarations built by hand with no table in sight.
/// </para>
/// <para>
/// So: build a declaration yourself to point at a picture; intern a name when you want the engine to
/// route debug geometry into it. That split was settled by the toolchain lab's embedded viewport — two
/// stages of a real consumer, and the table's location caused it no friction at all. What DID bite was
/// timing (a view is declared before UI layout and consumed during it), which moving the table would not
/// have helped.
/// </para>
/// <para>
/// Not thread-safe, and not meant to be: views are declared from the frame that draws them.
/// </para>
/// </remarks>
public sealed class ViewTable
{
    private readonly Dictionary<string, ViewId> ids = new(StringComparer.Ordinal);
    private readonly List<string> names = new();

    /// <summary>How many distinct view names have been seen. Ids are dense in [0, Count).</summary>
    public int Count => names.Count;

    /// <summary>The id for this name, minting one the first time it is asked for.</summary>
    public ViewId Intern(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (ids.TryGetValue(name, out var existing)) return existing;

        var minted = new ViewId(names.Count);
        names.Add(name);
        ids[name] = minted;
        return minted;
    }

    /// <summary>The name behind an id, for messages and dumps.</summary>
    public string NameOf(ViewId id) =>
        id.IsValid && id.Id < names.Count ? names[id.Id] : "<unknown view>";

    /// <summary>
    /// Builds a declaration for this frame, interning the name.
    /// </summary>
    /// <remarks>
    /// The physical viewport is passed rather than derived because only the host knows the backing scale,
    /// and every application that has had to work it out for itself has had the chance to get it wrong.
    /// </remarks>
    public ViewDeclaration Declare(
        string name,
        Matrix4x4 viewProjection,
        RenderSurfaceHandle target,
        Rect logicalViewport,
        Rect physicalViewport) =>
        new(Intern(name), name, viewProjection, target, logicalViewport, physicalViewport);

    /// <summary>A declaration for a view that fills a whole surface, which is the common case.</summary>
    public ViewDeclaration Declare(
        string name, Matrix4x4 viewProjection, RenderSurfaceHandle target, int width, int height, float scale = 1f)
    {
        var logical = new Rect(0f, 0f, width, height);
        var physical = new Rect(0f, 0f, width * scale, height * scale);
        return Declare(name, viewProjection, target, logical, physical);
    }
}
