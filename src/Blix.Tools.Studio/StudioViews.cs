using System.Numerics;
using Blix.Graphics;

namespace Blix.Tools.Studio;

/// <summary>
/// Alpha-mode decisions shared by Studio's static and rigged views.
/// </summary>
internal static class StudioAlpha
{
    /// <summary>The cutout threshold for MASK; zero for modes where glTF alphaCutoff has no meaning.</summary>
    public static float CutoffFor(GltfAlphaMode mode, float cutoff) =>
        mode == GltfAlphaMode.Mask ? cutoff : 0f;

    /// <summary>
    /// Whether a part is drawn in the blended group: last, and not into the shadow map.
    /// </summary>
    /// <remarks>
    /// Blended parts follow opaque parts but retain asset order within their group. Studio does not
    /// claim depth-correct ordering for overlapping transparent parts.
    /// </remarks>
    public static bool IsBlended(GltfAlphaMode mode) => mode == GltfAlphaMode.Blend;
}

/// <summary>A static glTF on the stage, placed by its authored node hierarchy.</summary>
public sealed class ModelView : IStudioView
{
    private readonly byte[] lit = new byte[StudioPush.LitBytes];
    private readonly byte[] caster = new byte[StudioPush.CasterBytes];

    public ModelView(StudioModel model, Matrix4x4 transform)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Transform = transform == default ? Matrix4x4.Identity : transform;
    }

    /// <summary>Colour overrides by material name, or null to draw what the file said.</summary>
    /// <remarks>Null preserves authored colour. Overrides are keyed by material name.</remarks>
    public StudioTints? Tints { get; set; }

    private Vector3 Colour(string materialName, Vector3 assetColour) =>
        Tints?.Resolve(materialName, assetColour) ?? assetColour;

    public StudioModel Model { get; }

    public Matrix4x4 Transform { get; set; }

    public void Draw(in StudioDraw draw)
    {
        var casterOnly = draw.Pass == StudioPass.Shadow;

        // Draw opaque parts before the non-depth-writing blended group.
        for (var pass = 0; pass < 2; pass++)
        for (var index = 0; index < Model.Parts.Count; index++)
        {
            var part = Model.Parts[index];
            var blended = StudioAlpha.IsBlended(part.AlphaMode);
            if (blended != (pass == 1)) continue;

            // A blended caster would write a solid silhouette into the depth-only shadow map, which
            // is a transparent surface casting an opaque shadow. Skipped rather than approximated.
            if (casterOnly && blended) continue;
            var node = Model.Nodes[part.NodeIndex];
            var push = casterOnly ? caster : lit;

            var cutoff = StudioAlpha.CutoffFor(part.AlphaMode, part.AlphaCutoff);
            StudioPush.Matrix(node.WorldTransform * Transform, push);
            if (casterOnly) StudioPush.CasterCutout(push, cutoff, part.BaseAlpha, part.AlbedoUvSet);
            else StudioPush.Material(push, Colour(part.MaterialName, part.BaseColour), part.Metallic, part.Roughness,
                     alphaCutoff: cutoff, baseAlpha: part.BaseAlpha, albedoUvSet: part.AlbedoUvSet);

            // Texture lists are retained by reference, so each recorded draw needs its own array.
            draw.Scope.DrawIndexed(
                vertexBuffer: part.Vertices,
                indexBuffer: part.Indices,
                pipeline: blended && draw.BlendPipeline.Id != 0 ? draw.BlendPipeline : draw.Pipeline,
                indexCount: part.IndexCount,
                uniforms: draw.Uniforms,
                // Casters also bind albedo at slot 0 because MASK shadows sample alpha.
                textures: draw.WithAlbedo(part.Albedo),
                pushConstants: push);
        }
    }
}

/// <summary>
/// A rigged glTF on the stage — every primitive drawn once for N instances out of one sliced palette.
/// </summary>
/// <remarks>
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
public sealed class RigView : IStudioView
{
    private readonly byte[] lit = new byte[StudioPush.LitBytes];
    private readonly byte[] caster = new byte[StudioPush.SkinnedCasterBytes];

    public RigView(StudioRig rig, int instances = 1)
    {
        Rig = rig ?? throw new ArgumentNullException(nameof(rig));
        Instances = instances;
    }

    /// <summary>Colour overrides by material name, or null to draw what the file said.</summary>
    /// <remarks>Null preserves authored colour. Overrides are keyed by material name.</remarks>
    public StudioTints? Tints { get; set; }

    private Vector3 Colour(string materialName, Vector3 assetColour) =>
        Tints?.Resolve(materialName, assetColour) ?? assetColour;

    public StudioRig Rig { get; }

    /// <summary>How many bodies this draw covers. One takes exactly the same path as eight.</summary>
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
    /// Where the body stands. Composed onto every attachment; the skin gets it through the palette.
    /// </summary>
    /// <remarks>
    /// Skinned placement is baked into the palette; rigid attachments need the same placement here.
    /// </remarks>
    public Matrix4x4 Placement { get; set; } = Matrix4x4.Identity;

    /// <summary>
    /// Attachments to draw, by name. Empty by default.
    /// </summary>
    /// <remarks>Selection is caller policy; Studio does not choose among mutually exclusive gear.</remarks>
    public HashSet<string> VisibleAttachments { get; } = new(StringComparer.Ordinal);

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
    public Func<int, ISet<string>>? InstanceAttachments { get; set; }

    public void Draw(in StudioDraw draw)
    {
        if (Instances <= 0) return;

        var casterOnly = draw.Pass == StudioPass.Shadow;
        var stride = (float)Rig.Skeleton.BoneCount;

        // Opaque then blended, for the same reason ModelView sweeps twice.
        for (var pass = 0; pass < 2; pass++)
        foreach (var part in Rig.Parts)
        {
            var blended = StudioAlpha.IsBlended(part.AlphaMode);
            if (blended != (pass == 1)) continue;
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
                    push, Colour(part.MaterialName, part.BaseColour), part.Metallic, part.Roughness, stride,
                    alphaCutoff: StudioAlpha.CutoffFor(part.AlphaMode, part.AlphaCutoff),
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
                textures: draw.WithAlbedo(part.Albedo),
                // Each part selects its authored skin; skins may share joints but not inverse binds.
                perDrawMaterial: (uint)part.SkinIndex < (uint)Rig.Skins.Count
                    ? Rig.Skins[part.SkinIndex].BoneMaterial
                    : Rig.BoneMaterial,
                pushConstants: push);
        }

        DrawAttachments(draw, casterOnly);
        DrawStaticParts(draw, casterOnly);
    }

    // Static rig parts use the standard rigid pipeline and are always drawn. Attachments are
    // caller-selected because several authored options may occupy the same joint.
    private void DrawStaticParts(in StudioDraw draw, bool casterOnly)
    {
        if (Rig.StaticParts.Count == 0) return;

        var push = casterOnly ? attachCaster : attachLit;
        foreach (var part in Rig.StaticParts)
        {
            // No joint, so no joint world: the node's own world matrix and the body's placement.
            StudioPush.Matrix(part.WorldTransform * Placement, push);
            if (casterOnly) StudioPush.CasterCutout(push, 0f, 1f);
            else StudioPush.Material(push, Colour(part.MaterialName, part.BaseColour), part.Metallic, part.Roughness);

            draw.Scope.DrawIndexed(
                vertexBuffer: part.Vertices,
                indexBuffer: part.Indices,
                pipeline: draw.Pipeline,
                indexCount: part.IndexCount,
                uniforms: draw.Uniforms,
                textures: draw.WithAlbedo(part.Albedo),
                pushConstants: push);
        }
    }

    private void DrawAttachments(in StudioDraw draw, bool casterOnly)
    {
        if (Rig.Attachments.Count == 0) return;
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
            if (visible is null || visible.Count == 0) continue;

            var placement = Placements is not null && (uint)body < (uint)Placements.Count
                ? Placements[body]
                : Placement;

            DrawAttachmentsFor(draw, casterOnly, worlds, visible, placement, attachPush);
        }
    }

    private void DrawAttachmentsFor(
        in StudioDraw draw,
        bool casterOnly,
        IReadOnlyList<Matrix4x4> worlds,
        ISet<string> visible,
        Matrix4x4 placement,
        byte[] attachPush)
    {
        foreach (var attachment in Rig.Attachments)
        {
            if (!visible.Contains(attachment.Name)) continue;
            if ((uint)attachment.JointIndex >= (uint)worlds.Count) continue;

            // local -> joint -> world. Row-vector, left to right, the same direction the hierarchy
            // walk composes in — a transposed multiply here puts the knife in the right place on a
            // rig with no rotation and nowhere near it on one with any.
            var model = attachment.LocalTransform * worlds[attachment.JointIndex] * placement;

            StudioPush.Matrix(model, attachPush);
            if (casterOnly) StudioPush.CasterCutout(attachPush, 0f, 1f);
            else StudioPush.Material(attachPush, Colour(attachment.MaterialName, attachment.BaseColour), attachment.Metallic, attachment.Roughness);

            draw.Scope.DrawIndexed(
                vertexBuffer: attachment.Vertices,
                indexBuffer: attachment.Indices,
                pipeline: draw.Pipeline,
                indexCount: attachment.IndexCount,
                uniforms: draw.Uniforms,
                textures: draw.WithAlbedo(attachment.Albedo),
                pushConstants: attachPush);
        }
    }

    // Separate from the skinned buffers above: a skinned caster pushes sixteen bytes and a static
    // one pushes sixty-four, so sharing would run one of them off the end of the other's buffer.
    private readonly byte[] attachLit = new byte[StudioPush.LitBytes];
    private readonly byte[] attachCaster = new byte[StudioPush.CasterBytes];
}
