using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Tools.Studio;

/// <summary>Which of the stage's passes a view is being asked to draw into.</summary>
public enum StudioPass
{
    /// <summary>The sun's depth. No colour attachment, no materials — geometry and a matrix.</summary>
    Shadow,

    /// <summary>The lit HDR pass, and the panel viewport, which is the same pass from another camera.</summary>
    Lit,
}

/// <summary>
/// One pass, handed to a view: where to record, which pass it is, and what the stage already knows.
/// </summary>
/// <remarks>
/// <b>Everything here is the stage's, not the view's.</b> The view-projection, the sun, the shadow
/// map and the standard pipelines are computed once and handed down, so a view that wants the
/// ordinary look writes a draw call and nothing else — no lighting maths, no descriptor plumbing,
/// no second copy of the sun to keep in step.
/// </remarks>
/// <param name="Scope">The pass being recorded. Draws go here.</param>
/// <param name="Pass">Which pass, so a view can skip one — a ground plane casts nothing worth the fill.</param>
/// <param name="Uniforms">The stage's per-pass uniforms: view-projection, sun, camera.</param>
/// <param name="Textures">The shadow map in <see cref="StudioPass.Lit"/>; empty in the caster pass.</param>
/// <param name="Pipeline">The stage's standard pipeline for this pass.</param>
/// <param name="SkinnedPipeline">Its skinned twin, for a view that draws a rig.</param>
/// <param name="White">A 1×1 white texture, for a draw with no albedo of its own.</param>
public readonly record struct StudioDraw(
    RenderPassBuilder Scope,
    StudioPass Pass,
    ShaderUniform[] Uniforms,
    ShaderTextureBinding[] Textures,
    PipelineHandle Pipeline,
    PipelineHandle SkinnedPipeline,
    TextureHandle White);

/// <summary>
/// Something a tool puts on the stage.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the rung that decides the shape of everything above it.</b> The renderer used to name
/// its subjects —
/// <c>Render(..., StudioModel? model, Matrix4x4 modelTransform, StudioRig? rig, int rigInstances, ...)</c>
/// — which works exactly as long as there are two of them. The case that broke it is terrain: the
/// RTS map generator is a tool that needs to show a heightfield, and a heightfield is neither a
/// model nor a rig. Its only options against that signature were to pretend to be one, or to leave
/// the stage entirely and write a renderer.
/// </para>
/// <para>
/// So bringing a draw is the ORDINARY case rather than an escape hatch, and bringing a
/// <em>pipeline</em> needs no mechanism at all — a view that wants its own material simply ignores
/// <see cref="StudioDraw.Pipeline"/> and passes its own. That is the third rung, and it falls out of
/// this one rather than being designed.
/// </para>
/// <para>
/// What stays with the stage is furniture: the ground and the boxes. A tool does not choose whether
/// the stage has a floor — that is part of what makes it a stage rather than a blank device.
/// </para>
/// </remarks>
public interface IStudioView
{
    void Draw(in StudioDraw draw);
}
