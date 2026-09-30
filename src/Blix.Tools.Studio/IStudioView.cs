using Blix.Graphics;

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
/// <param name="SkinnedDoubleSidedPipeline">
/// The skinned pipeline WITHOUT back-face culling, for a rig part whose material says
/// <c>doubleSided</c>. Face culling is pipeline state in Vulkan, so this cannot be a push constant
/// and a view that honours the material has to be handed both. Equal to
/// <paramref name="SkinnedPipeline"/> in the caster pass, which does not cull either way.
/// </param>
/// <param name="White">A 1×1 white texture, for a draw with no albedo of its own.</param>
public readonly record struct StudioDraw(
    RenderPassBuilder Scope,
    StudioPass Pass,
    ShaderUniform[] Uniforms,
    ShaderTextureBinding[] Textures,
    PipelineHandle Pipeline,
    PipelineHandle SkinnedPipeline,
    TextureHandle White,
    PipelineHandle SkinnedDoubleSidedPipeline = default,

    /// <summary>The stage's pipeline for a BLEND material — blending on, depth write off.</summary>
    /// <remarks>
    /// <b>A pipeline rather than a push constant, because blending is pipeline state.</b> That is
    /// the whole reason MASK and BLEND land differently: a cutout is a comparison a fragment can
    /// make, and blending is not.
    /// </remarks>
    PipelineHandle BlendPipeline = default,

    /// <summary>Its skinned twin.</summary>
    PipelineHandle SkinnedBlendPipeline = default)
{
    /// <summary>The pass's textures plus this draw's albedo, at the slot this pass puts it in.</summary>
    /// <remarks>
    /// <b>Here because five views were assembling it by hand, and the stage grew a texture.</b> Each
    /// of them wrote <c>{ draw.Textures[0], uAlbedo }</c> — correct while the lit pass bound exactly
    /// one texture, and silently one-short the moment it bound four. Every draw on a pipeline must
    /// bind every texture its shader declares, so the assembly belongs to whoever knows what the
    /// pass binds, which is the stage and not the view.
    /// <para>
    /// The caster takes slot 0 and no shadow map: its shader declares an albedo so it can cut out,
    /// and nothing else.
    /// </para>
    /// </remarks>
    /// <summary>The stage's per-asset state, for a stage view (RigView, ModelView) to find its asset's.</summary>
    internal StudioAssets? Assets { get; init; }

    public ShaderTextureBinding[] WithAlbedo(TextureHandle albedo)
    {
        if (Pass == StudioPass.Shadow) return new[] { new ShaderTextureBinding("uAlbedo", albedo) };

        var all = new ShaderTextureBinding[Textures.Length + 1];
        Textures.CopyTo(all, 0);
        all[^1] = new ShaderTextureBinding("uAlbedo", albedo);
        return all;
    }
}

/// <summary>
/// Something a tool puts on the stage.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the rung that decides the shape of everything above it.</b> The renderer does not name
/// its subjects: a signature that took a model and a rig would work exactly as long as there are two
/// kinds. Terrain is the case that shows it: the RTS map generator needs to show a heightfield, which is
/// neither a model nor a rig, and against such a signature it could only pretend to be one or leave
/// the stage and write a renderer.
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
