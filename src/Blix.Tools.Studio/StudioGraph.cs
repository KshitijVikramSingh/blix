using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Tools.Studio;

/// <summary>
/// The stage's own graph and targets, handed to a tool during construction — rung four.
/// </summary>
/// <remarks>
/// <para>
/// Render-graph shape is fixed at compile time, so a tool declares selection, object-id, or other
/// additional passes through this object while Studio is being constructed.
/// </para>
/// <para>
/// Extensions append after Studio's lit pass and before presentation because execution follows
/// declaration order. This is deliberately not an insertion API; a pass that must precede Studio's
/// lit pass requires a different composition boundary.
/// </para>
/// <para>
/// Targets and shader interfaces are exposed so the extension can declare a valid pass. Studio
/// retains compilation and execution ownership; tools record their pass each frame through
/// <see cref="StudioRenderer.Graph"/>.
/// </para>
/// </remarks>
public sealed class StudioGraph
{
    internal StudioGraph(
        RenderGraph graph,
        GraphResourceHandle sceneColour,
        GraphResourceHandle sceneDepth,
        GraphResourceHandle shadowDepth,
        ShaderInterface lit,
        ShaderInterface shadow)
    {
        Graph = graph;
        SceneColour = sceneColour;
        SceneDepth = sceneDepth;
        ShadowDepth = shadowDepth;
        Lit = lit;
        Shadow = shadow;
    }

    /// <summary>Declare passes on this. Do not compile it — the stage does that.</summary>
    public RenderGraph Graph { get; }

    /// <summary>The HDR colour the lit pass writes and the present pass tonemaps.</summary>
    public GraphResourceHandle SceneColour { get; }

    /// <summary>The scene's depth, for a pass that wants to read what is already in front.</summary>
    public GraphResourceHandle SceneDepth { get; }

    /// <summary>The sun's depth map.</summary>
    public GraphResourceHandle ShadowDepth { get; }

    /// <summary>The stage's lit interface, available to extensions that reuse its program contract.</summary>
    public ShaderInterface Lit { get; }

    /// <summary>The caster interface — a matrix and no materials.</summary>
    public ShaderInterface Shadow { get; }

}
