namespace Blix.Graphics.Vulkan;

// Public type vocabulary for the render graph (Vector B). Pure
// declarations; behavior lives in RenderGraph + builders + backend.
//
// Reduced-scope per docs/vector-b-plan.md:
// - LoadOp / StoreOp on .Target() at pass declaration time (F-012).
// - GraphResourceHandle for 2D color + depth targets.
// - DepthCubeHandle exposes .Face(int) → TextureView (Q-006, cube-face only).
// - Mip-level + array-layer subviews on TextureView are deferred to step 6
//   (bloom mip chain + IBL prefilter).

// Attachment load operation at the start of a render pass.
public enum LoadOp
{
    Clear,    // Clear the attachment to the pass's clear value
    Load,     // Preserve the attachment's existing contents
    DontCare, // Contents are undefined at pass start (cheapest; correct only
              // when the pass fully overwrites the attachment)
}

// Attachment store operation at the end of a render pass.
public enum StoreOp
{
    Store,    // Persist the attachment's contents for later passes
    DontCare, // Contents are discarded at pass end (cheapest; correct only
              // when no downstream pass reads the attachment)
}

// Discriminated union for graph resource sizing. Snapshot-on-resize for
// MatchSwapchainGraphSize per VB.vi.
public abstract record GraphSize;
public sealed record FixedGraphSize(int Width, int Height) : GraphSize;
public sealed record MatchSwapchainGraphSize(float Scale = 1.0f) : GraphSize;

// Opaque handle for graph-managed 2D color/depth resources.
// Returned by RenderGraph.ColorTarget / DepthTarget.
public readonly record struct GraphResourceHandle(int Id);

// Cube resource handle. Carries the same id space as GraphResourceHandle
// (the cube itself IS a graph resource) but exposes .Face(int) to produce
// a TextureView selecting one face for use as a render target.
//
// Implicit conversion to GraphResourceHandle lets Read() / Target() of the
// whole cube fall through to the standard path (e.g. fragment shader
// sampling the whole cube as samplerCube).
public readonly record struct DepthCubeHandle(int Id)
{
    public TextureView Face(int face)
    {
        if (face < 0 || face > 5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(face), face, "Cube face must be in [0, 5].");
        }
        return new TextureView(new GraphResourceHandle(Id), face);
    }

    public static implicit operator GraphResourceHandle(DepthCubeHandle h) =>
        new(h.Id);
}

// Sub-resource selector for a graph resource. Face is the only sub-resource
// selector in reduced scope (cubes). Mip + ArrayLayer fields land when
// step 6 needs them.
//
// Whole-image view: TextureView(handle) with Face null. Implicit conversion
// from GraphResourceHandle constructs the whole-image view automatically.
public readonly record struct TextureView(GraphResourceHandle Resource, int? Face = null)
{
    public bool IsWholeImage => Face is null;

    // Implicit conversion: plain handle becomes a whole-image view.
    public static implicit operator TextureView(GraphResourceHandle h) =>
        new(h, null);
}

// Opaque handle for a declared graph pass. Returned by graph.GraphicsPass /
// ComputePass; passed to graph.Pass(handle, scope) and SetPerPass(handle, struct).
public readonly record struct PassHandle(int Id);
