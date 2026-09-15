using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Tools.Studio;

/// <summary>
/// The stage's own graph and targets, handed to a tool during construction — rung four.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all, and why it is a construction-time thing.</b> A render graph's passes
/// have to be declared before it compiles; after that the shape is fixed. So a tool that needs a
/// pass of its own — a selection outline, a depth pre-pass, an object-id buffer — has exactly one
/// moment to say so, and without this its only option is to stop using the stage and write a
/// renderer.
/// </para>
/// <para>
/// It hands out the targets rather than hiding them, because a pass that cannot read the scene
/// depth or write the scene colour is not much of a pass. What it does not hand out is
/// <see cref="RenderGraph.Compile"/> — the stage compiles, once, when it is ready.
/// </para>
/// </remarks>
public sealed class StudioGraph
{
    private readonly List<(PassHandle Pass, Action<RenderPassBuilder> Record)> extensions;

    internal StudioGraph(
        RenderGraph graph,
        GraphResourceHandle sceneColour,
        GraphResourceHandle sceneDepth,
        GraphResourceHandle shadowDepth,
        ShaderInterface lit,
        ShaderInterface shadow,
        List<(PassHandle, Action<RenderPassBuilder>)> extensions)
    {
        Graph = graph;
        SceneColour = sceneColour;
        SceneDepth = sceneDepth;
        ShadowDepth = shadowDepth;
        Lit = lit;
        Shadow = shadow;
        this.extensions = extensions;
    }

    /// <summary>Declare passes on this. Do not compile it — the stage does that.</summary>
    public RenderGraph Graph { get; }

    /// <summary>The HDR colour the lit pass writes and the present pass tonemaps.</summary>
    public GraphResourceHandle SceneColour { get; }

    /// <summary>The scene's depth, for a pass that wants to read what is already in front.</summary>
    public GraphResourceHandle SceneDepth { get; }

    /// <summary>The sun's depth map.</summary>
    public GraphResourceHandle ShadowDepth { get; }

    /// <summary>
    /// The stage's own shader interfaces, because a pass cannot be declared without one.
    /// </summary>
    /// <remarks>
    /// <b>Found by the first thing that used this.</b> Rung four as first built handed out the graph
    /// and the targets and kept these private — and a GraphicsPass with no <c>.Shader(...)</c> is
    /// refused outright, so the hook was unusable by anything that did not already own a program.
    /// A tool adding an overlay wants the ordinary lit interface far more often than one of its own,
    /// and now it does not have to reflect a shader to say so.
    /// </remarks>
    public ShaderInterface Lit { get; }

    /// <summary>The caster interface — a matrix and no materials.</summary>
    public ShaderInterface Shadow { get; }

    /// <summary>
    /// Record this pass every frame, after the stage's own.
    /// </summary>
    /// <remarks>
    /// Two calls rather than one — declare the pass on <see cref="Graph"/>, then say how to record
    /// it — because the graph's builder returns a handle and the recording is a per-frame delegate.
    /// Folding them together would mean capturing the builder, which is a construction-time thing,
    /// inside something called every frame.
    /// </remarks>
    public void Record(PassHandle pass, Action<RenderPassBuilder> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        extensions.Add((pass, record));
    }
}
