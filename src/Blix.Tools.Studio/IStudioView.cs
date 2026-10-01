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
    PipelineHandle SkinnedBlendPipeline = default,

    /// <summary>The standard pipeline WITH back-face culling, for a single-sided material (glTF's default).</summary>
    /// <remarks>Zero in the caster pass, which culls nothing; a view falls back to <see cref="Pipeline"/>.</remarks>
    PipelineHandle CulledPipeline = default,

    /// <summary>
    /// The culled pipeline with clockwise front faces, for a part whose world transform mirrors (a negative
    /// determinant): mirroring reverses winding, so the face to cull reverses with it.
    /// </summary>
    PipelineHandle MirroredCulledPipeline = default,

    /// <summary>
    /// <see cref="Pipeline"/> with clockwise front faces, for a doubleSided part that mirrors: nothing is
    /// culled, but which side is the front still decides how it is lit (gl_FrontFacing).
    /// </summary>
    PipelineHandle MirroredPipeline = default,

    /// <summary><see cref="BlendPipeline"/> with clockwise front faces, for a blended part that mirrors.</summary>
    PipelineHandle MirroredBlendPipeline = default)
{
    /// <summary>The stage's per-asset state, for a stage view (ModelView) to find its asset's.</summary>
    internal StudioAssets? Assets { get; init; }

    /// <summary>The pass's textures plus this draw's albedo, with white in every other material channel.</summary>
    /// <remarks>
    /// For a draw with base colour alone. White is inert in the other channels only because the
    /// matching <see cref="StudioPush.Material"/> terms default to zero; see there.
    /// </remarks>
    public ShaderTextureBinding[] WithAlbedo(TextureHandle albedo) =>
        WithMaterial(albedo, White, White, White, White);

    /// <summary>The pass's textures plus this draw's glTF material channels.</summary>
    /// <remarks>
    /// <b>The stage assembles the list because it knows what the pass binds.</b> Every draw on a
    /// pipeline must bind every texture its shader declares, so a view that built the list itself
    /// would be one short the next time the pass grew one. The caster pass binds albedo alone: its
    /// shader declares albedo so it can cut out, and nothing else.
    /// </remarks>
    /// <param name="metallicRoughness">glTF packing: green is roughness, blue is metallic, both multiplying the factors.</param>
    /// <param name="occlusion">Red is ambient occlusion.</param>
    public ShaderTextureBinding[] WithMaterial(
        TextureHandle albedo, TextureHandle normal, TextureHandle metallicRoughness,
        TextureHandle occlusion, TextureHandle emissive)
    {
        if (Pass == StudioPass.Shadow) return new[] { new ShaderTextureBinding("uAlbedo", albedo) };
        return Append(Textures, albedo, normal, metallicRoughness, occlusion, emissive);
    }

    /// <summary>The pass's uniforms plus this surface's UV transforms; the caster pass reads none of them.</summary>
    internal ShaderUniform[] WithUv(in StudioSurface surface)
    {
        if (Pass == StudioPass.Shadow) return Uniforms;
        var all = new ShaderUniform[Uniforms.Length + 1];
        Uniforms.CopyTo(all, 0);
        all[^1] = new ShaderUniform("uUvRows", new Matrix4x4ArrayUniform(surface.UvRows));
        return all;
    }

    internal ShaderTextureBinding[] WithSurface(in StudioSurface surface) => WithMaterial(
        surface.Textures.Albedo, surface.Textures.Normal, surface.Textures.MetallicRoughness,
        surface.Textures.Occlusion, surface.Textures.Emissive);

    // Texture lists are retained by reference, so each recorded draw gets its own array.
    internal static ShaderTextureBinding[] Append(
        ShaderTextureBinding[] pass, TextureHandle albedo, TextureHandle normal, TextureHandle metallicRoughness,
        TextureHandle occlusion, TextureHandle emissive)
    {
        var all = new ShaderTextureBinding[pass.Length + 5];
        pass.CopyTo(all, 0);
        all[^5] = new ShaderTextureBinding("uAlbedo", albedo);
        all[^4] = new ShaderTextureBinding("uNormalMap", normal);
        all[^3] = new ShaderTextureBinding("uMetallicRoughness", metallicRoughness);
        all[^2] = new ShaderTextureBinding("uOcclusion", occlusion);
        all[^1] = new ShaderTextureBinding("uEmissive", emissive);
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
