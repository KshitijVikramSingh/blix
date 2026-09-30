using System.Numerics;
using Blix.Graphics;

namespace Blix.Tools.Studio;

/// <summary>
/// Alpha-mode decisions shared by Studio's static and rigged views.
/// </summary>
internal static class StudioAlpha
{
    /// <summary>The cutout threshold for MASK; zero for modes where glTF alphaCutoff has no meaning.</summary>
    public static float CutoffFor(AlphaMode mode, float cutoff) =>
        mode == AlphaMode.Mask ? cutoff : 0f;

    /// <summary>
    /// Whether a part is drawn in the blended group: last, and not into the shadow map.
    /// </summary>
    /// <remarks>
    /// Blended parts follow opaque parts but retain asset order within their group. Studio does not
    /// claim depth-correct ordering for overlapping transparent parts.
    /// </remarks>
    public static bool IsBlended(AlphaMode mode) => mode == AlphaMode.Blend;
}

/// <summary>
/// A model on the stage: its skinned parts drawn once for N instances out of one sliced palette, and every
/// other part rigidly — at its node's world, or at the posed joint that carries it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One view, as there is one <see cref="Model"/>.</b> A file with no skin has no skinned parts and no
/// joints, so it draws its static parts at their nodes and nothing else is asked of the caller. A skinned
/// one is drawn at whatever pose the stage last received (<see cref="StudioRenderer.UploadPalettes"/>).
/// </para>
/// <para>
/// The hierarchy lives in the palette rather than in the transforms, and every primitive of an
/// instance reads the same slice: a skin that placed its parts individually would tear along their
/// seams.
/// </para>
/// <para>
/// The palette must be uploaded before the pass is recorded, not here. A descriptor set's
/// buffer is not copied at record time the way a push payload is, so the draw carries a binding
/// rather than a copy of the matrices — which is what makes one palette per frame slot correct and
/// one palette per renderer a frame of lag.
/// </para>
/// </remarks>
public sealed class ModelView : IStudioView
{
    private readonly byte[] lit = new byte[StudioPush.LitBytes];
    private readonly byte[] caster = new byte[StudioPush.SkinnedCasterBytes];

    public ModelView(Model model, int instances = 1)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Instances = instances;
    }

    /// <summary>Colour overrides by material name, or null to draw what the file said.</summary>
    /// <remarks>Null preserves authored colour. Overrides are keyed by material name.</remarks>
    public StudioTints? Tints { get; set; }

    private Vector3 Colour(string materialName, Vector3 assetColour) =>
        Tints?.Resolve(materialName, assetColour) ?? assetColour;

    /// <summary>The engine model drawn: loaded through the stage (<see cref="StudioRenderer.LoadModel"/>).</summary>
    public Model Model { get; }

    /// <summary>How many bodies the skinned parts cover. One takes exactly the same path as eight.</summary>
    public int Instances { get; set; }

    /// <summary>
    /// Joint world transforms for the pose being drawn, from <c>RigAnimation.BoneWorlds</c>.
    /// </summary>
    /// <remarks>
    /// These are joint positions, not palette matrices. Attachments compose against joint worlds;
    /// palette matrices include inverse bind and are not positions.
    /// </remarks>
    public IReadOnlyList<Matrix4x4>? BoneWorlds { get; set; }

    /// <summary>
    /// Where the model stands. Composed onto every rigid part; the skin gets it through the palette.
    /// </summary>
    /// <remarks>
    /// Skinned placement is baked into the palette; static parts and attachments need the same placement here.
    /// </remarks>
    public Matrix4x4 Placement { get; set; } = Matrix4x4.Identity;

    /// <summary>
    /// Which attachments to draw, by name; null draws every one the file carries.
    /// </summary>
    /// <remarks>
    /// The file's own answer is the default: every mesh a joint carries is part of the model. Choosing
    /// among mutually exclusive gear is caller policy, and a set here is how a caller states it.
    /// </remarks>
    public ISet<string>? VisibleAttachments { get; set; }

    /// <summary>
    /// Bone worlds for one instance, asked for at DRAW time. Null draws attachments for instance 0
    /// alone, from <see cref="BoneWorlds"/>.
    /// </summary>
    /// <remarks>
    /// The callback is evaluated immediately per instance because
    /// <c>RigAnimation.InstanceBoneWorlds</c> may return shared scratch valid only until the next
    /// call. <see cref="DrawAttachments"/> must therefore remain instance-outer.
    /// </remarks>
    public Func<int, IReadOnlyList<Matrix4x4>>? InstanceBoneWorlds { get; set; }

    /// <summary>
    /// Where each body stands, one per instance — <c>RigAnimation.Placements</c>. Falls back to
    /// <see cref="Placement"/> for any instance this does not cover.
    /// </summary>
    /// <remarks>
    /// The palette bakes placement into each skinned slice, so the bodies already stand apart
    /// without this. An attachment is not skinned, so it needs the same placement by another route —
    /// the same asymmetry <see cref="Placement"/> documents, now once per body.
    /// </remarks>
    public IReadOnlyList<Matrix4x4>? Placements { get; set; }

    /// <summary>
    /// Which attachments instance <c>i</c> shows. Null gives every instance
    /// <see cref="VisibleAttachments"/>.
    /// </summary>
    /// <remarks>Selection source is caller policy: inventory, state, or a tool control.</remarks>
    public Func<int, ISet<string>?>? InstanceAttachments { get; set; }

    private StudioModel Studio(in StudioDraw draw) =>
        (draw.Assets ?? throw new InvalidOperationException("A ModelView is drawn by the stage that loaded its model.")).For(Model);

    public void Draw(in StudioDraw draw)
    {
        var casterOnly = draw.Pass == StudioPass.Shadow;

        // Opaque then blended, for the same reason ModelView sweeps twice — and across all three kinds
        // of part, so a blended attachment or static part draws after every opaque surface of the body.
        for (var pass = 0; pass < 2; pass++)
        {
            var blendedGroup = pass == 1;
            if (Instances > 0) DrawSkinned(draw, casterOnly, blendedGroup);
            DrawAttachments(draw, casterOnly, blendedGroup);
            DrawStaticParts(draw, casterOnly, blendedGroup);
        }
    }

    private void DrawSkinned(in StudioDraw draw, bool casterOnly, bool blendedGroup)
    {
        var studio = Studio(draw);
        foreach (var part in studio.SkinnedParts)
        {
            // The part's OWN skin's bone count: each skin's palettes are packed at its own stride, and
            // skins of one file need not have the same number of bones.
            var stride = (float)studio.Skins[part.SkinIndex].Skeleton.BoneCount;
            var blended = StudioAlpha.IsBlended(part.AlphaMode);
            if (blended != blendedGroup) continue;
            if (casterOnly && blended) continue;

            byte[] push;
            if (casterOnly)
            {
                // No model matrix at all: the bones carry the placement, so one here would apply
                // it twice. The stride is the whole payload.
                // Sixteen bytes, and no StudioPush.Matrix call — that writes sixty-four and would
                // run straight off the end of this buffer.
                // y and z carry the cutout, in components this vec4 was already pushing and
                // ignoring — so a skinned caster that can discard is still sixteen bytes.
                push = caster;
                var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(push.AsSpan());
                floats[0] = stride;
                floats[1] = StudioAlpha.CutoffFor(part.AlphaMode, part.AlphaCutoff);
                floats[2] = part.BaseAlpha;
                floats[3] = 0f;
            }
            else
            {
                push = lit;
                StudioPush.Matrix(Matrix4x4.Identity, push);
                StudioPush.Material(
                    push, Colour(part.MaterialName, part.BaseColour), part.Metallic, part.Roughness, part.Surface,
                    stride, alphaCutoff: StudioAlpha.CutoffFor(part.AlphaMode, part.AlphaCutoff),
                    baseAlpha: part.BaseAlpha, albedoUvSet: part.AlbedoUvSet);
            }

            // BLEND is already unculled. Other double-sided materials use the unculled skinned
            // pipeline; closed single-sided parts retain back-face culling.
            var skinned = blended && draw.SkinnedBlendPipeline.Id != 0
                ? draw.SkinnedBlendPipeline
                : part.DoubleSided && draw.SkinnedDoubleSidedPipeline.Id != 0
                    ? draw.SkinnedDoubleSidedPipeline
                    : draw.SkinnedPipeline;

            draw.Scope.DrawIndexedInstanced(
                vertexBuffer: part.Vertices,
                indexBuffer: part.Indices,
                pipeline: skinned,
                indexCount: part.IndexCount,
                instanceCount: Instances,
                uniforms: draw.Uniforms,
                textures: draw.WithSurface(part.Surface),
                // Each part selects its authored skin; skins may share joints but not inverse binds.
                perDrawMaterial: studio.Skins[part.SkinIndex].BoneMaterial,
                pushConstants: push);
        }
    }

    // Static rig parts use the standard rigid pipeline and are always drawn. Attachments are
    // caller-selected because several authored options may occupy the same joint.
    private void DrawStaticParts(in StudioDraw draw, bool casterOnly, bool blendedGroup)
    {
        if (Studio(draw).StaticParts.Count == 0) return;

        var push = casterOnly ? attachCaster : attachLit;
        foreach (var part in Studio(draw).StaticParts)
        {
            var blended = StudioAlpha.IsBlended(part.AlphaMode);
            if (blended != blendedGroup) continue;
            if (casterOnly && blended) continue;

            // No joint, so no joint world: the node's own world matrix and the body's placement.
            var cutoff = StudioAlpha.CutoffFor(part.AlphaMode, part.AlphaCutoff);
            StudioPush.Matrix(part.WorldTransform * Placement, push);
            if (casterOnly) StudioPush.CasterCutout(push, cutoff, part.BaseAlpha, part.AlbedoUvSet);
            else StudioPush.Material(push, Colour(part.MaterialName, part.BaseColour), part.Metallic, part.Roughness,
                     part.Surface, alphaCutoff: cutoff, baseAlpha: part.BaseAlpha, albedoUvSet: part.AlbedoUvSet);

            draw.Scope.DrawIndexed(
                vertexBuffer: part.Vertices,
                indexBuffer: part.Indices,
                pipeline: blended && draw.BlendPipeline.Id != 0 ? draw.BlendPipeline : draw.Pipeline,
                indexCount: part.IndexCount,
                uniforms: draw.Uniforms,
                textures: draw.WithSurface(part.Surface),
                pushConstants: push);
        }
    }

    private void DrawAttachments(in StudioDraw draw, bool casterOnly, bool blendedGroup)
    {
        if (Studio(draw).Attachments.Count == 0) return;
        if (InstanceBoneWorlds is null && BoneWorlds is null) return;

        var attachPush = casterOnly ? attachCaster : attachLit;

        // InstanceBoneWorlds may return shared scratch, so finish one body's attachments before
        // asking for the next body's transforms. See the property contract above.
        var bodies = InstanceBoneWorlds is null ? 1 : Math.Max(1, Instances);
        for (var body = 0; body < bodies; body++)
        {
            var worlds = InstanceBoneWorlds is null ? BoneWorlds : InstanceBoneWorlds(body);
            if (worlds is null) continue;

            var visible = InstanceAttachments is null ? VisibleAttachments : InstanceAttachments(body);

            var placement = Placements is not null && (uint)body < (uint)Placements.Count
                ? Placements[body]
                : Placement;

            DrawAttachmentsFor(draw, casterOnly, blendedGroup, worlds, visible, placement, attachPush);
        }
    }

    private void DrawAttachmentsFor(
        in StudioDraw draw,
        bool casterOnly,
        bool blendedGroup,
        IReadOnlyList<Matrix4x4> worlds,
        ISet<string>? visible,
        Matrix4x4 placement,
        byte[] attachPush)
    {
        foreach (var attachment in Studio(draw).Attachments)
        {
            if (visible is not null && !visible.Contains(attachment.Name)) continue;
            if ((uint)attachment.JointIndex >= (uint)worlds.Count) continue;
            var blended = StudioAlpha.IsBlended(attachment.AlphaMode);
            if (blended != blendedGroup) continue;
            if (casterOnly && blended) continue;

            // local -> joint -> world. Row-vector, left to right, the same direction the hierarchy
            // walk composes in — a transposed multiply here puts the knife in the right place on a
            // rig with no rotation and nowhere near it on one with any.
            var model = attachment.LocalTransform * worlds[attachment.JointIndex] * placement;

            var cutoff = StudioAlpha.CutoffFor(attachment.AlphaMode, attachment.AlphaCutoff);
            StudioPush.Matrix(model, attachPush);
            if (casterOnly) StudioPush.CasterCutout(attachPush, cutoff, attachment.BaseAlpha, attachment.AlbedoUvSet);
            else StudioPush.Material(attachPush, Colour(attachment.MaterialName, attachment.BaseColour), attachment.Metallic,
                     attachment.Roughness, attachment.Surface, alphaCutoff: cutoff, baseAlpha: attachment.BaseAlpha,
                     albedoUvSet: attachment.AlbedoUvSet);

            draw.Scope.DrawIndexed(
                vertexBuffer: attachment.Vertices,
                indexBuffer: attachment.Indices,
                pipeline: blended && draw.BlendPipeline.Id != 0 ? draw.BlendPipeline : draw.Pipeline,
                indexCount: attachment.IndexCount,
                uniforms: draw.Uniforms,
                textures: draw.WithSurface(attachment.Surface),
                pushConstants: attachPush);
        }
    }

    // Separate from the skinned buffers above: a skinned caster pushes sixteen bytes and a static
    // one pushes sixty-four, so sharing would run one of them off the end of the other's buffer.
    private readonly byte[] attachLit = new byte[StudioPush.LitBytes];
    private readonly byte[] attachCaster = new byte[StudioPush.CasterBytes];
}
