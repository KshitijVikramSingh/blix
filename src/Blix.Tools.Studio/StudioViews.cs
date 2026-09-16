using System.Numerics;
using Blix.Graphics;

namespace Blix.Tools.Studio;

/// <summary>
/// A static glTF on the stage, placed by its own node hierarchy.
/// </summary>
/// <remarks>
/// Ready-made, so a tool that wants to look at a model writes no draw call. The contrast with
/// <see cref="RigView"/> is the whole difference between a static asset and a rigged one: here each
/// part is placed by its node's composed world matrix, because the hierarchy IS the articulation.
/// </remarks>
/// <summary>The cutout threshold a part's material asks for. Zero for anything that never cuts.</summary>
/// <remarks>
/// <b>Zero rather than the material's own cutoff for OPAQUE and BLEND.</b> glTF gives every material
/// an alphaCutoff whether or not its alphaMode uses one — the field defaults to 0.5 and means nothing
/// unless the mode is MASK. Pushing it regardless would punch holes in every opaque surface whose
/// texture happened to carry an alpha channel.
/// </remarks>
internal static class StudioAlpha
{
    public static float CutoffFor(GltfAlphaMode mode, float cutoff) =>
        mode == GltfAlphaMode.Mask ? cutoff : 0f;
}

public sealed class ModelView : IStudioView
{
    private readonly byte[] lit = new byte[StudioPush.LitBytes];
    private readonly byte[] caster = new byte[StudioPush.CasterBytes];

    public ModelView(StudioModel model, Matrix4x4 transform)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Transform = transform == default ? Matrix4x4.Identity : transform;
    }

    public StudioModel Model { get; }

    public Matrix4x4 Transform { get; set; }

    public void Draw(in StudioDraw draw)
    {
        var casterOnly = draw.Pass == StudioPass.Shadow;

        for (var index = 0; index < Model.Parts.Count; index++)
        {
            var part = Model.Parts[index];
            var node = Model.Nodes[part.NodeIndex];
            var push = casterOnly ? caster : lit;

            StudioPush.Matrix(node.WorldTransform * Transform, push);
            if (!casterOnly)
            {
                StudioPush.Material(
                    push, part.BaseColour, part.Metallic, part.Roughness,
                    alphaCutoff: StudioAlpha.CutoffFor(part.AlphaMode, part.AlphaCutoff),
                    baseAlpha: part.BaseAlpha);
            }

            // A FRESH texture array per part. Push payloads are copied at record time; texture
            // lists are still retained by reference, so a shared array would give every draw the
            // last part's albedo — the aliasing that once stacked seven boxes at the seventh's
            // transform, wearing a different hat.
            draw.Scope.DrawIndexed(
                vertexBuffer: part.Vertices,
                indexBuffer: part.Indices,
                pipeline: draw.Pipeline,
                indexCount: part.IndexCount,
                uniforms: draw.Uniforms,
                textures: casterOnly
                    ? draw.Textures
                    : new[] { draw.Textures[0], new ShaderTextureBinding("uAlbedo", part.Albedo, Slot: 1) },
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
/// <b>The palette must be uploaded before the pass is recorded</b>, not here. A descriptor set's
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

    public StudioRig Rig { get; }

    /// <summary>How many bodies this draw covers. One takes exactly the same path as eight.</summary>
    public int Instances { get; set; }

    /// <summary>
    /// Joint world transforms for the pose being drawn, from <c>RigAnimation.BoneWorlds</c>.
    /// </summary>
    /// <remarks>
    /// <b>Not the palette, and nothing here recomputes them.</b> A palette matrix is
    /// <c>InverseBindPose · world</c> — a displacement, not a position — so composing a knife onto
    /// one puts it near the origin. The worlds are already computed once per pose by the session;
    /// this only needs to be handed them.
    /// </remarks>
    public IReadOnlyList<Matrix4x4>? BoneWorlds { get; set; }

    /// <summary>
    /// Where the body stands. Composed onto every attachment; the skin gets it through the palette.
    /// </summary>
    /// <remarks>
    /// <b>The skinned path does not need this and the attachment path does</b>, which is the one
    /// genuine asymmetry between them. <c>PackInstances</c> bakes placement into each palette slice,
    /// so a skinned draw pushes the identity and the bones carry it — but an attachment is not
    /// skinned, so its placement has to arrive some other way, and the caller is the only thing that
    /// knows it.
    /// </remarks>
    public Matrix4x4 Placement { get; set; } = Matrix4x4.Identity;

    /// <summary>
    /// Attachments to draw, by name. Empty by default.
    /// </summary>
    /// <remarks>
    /// <b>Empty by default because five weapons in one hand is not a picture of anything.</b> Four
    /// of the Rogue's six attachments hang off <c>handslot.r</c>; a game shows one. Which one is the
    /// caller's decision and the engine has no opinion — the same rule pose composition follows,
    /// where the engine takes weights and knows nothing about states.
    /// </remarks>
    public HashSet<string> VisibleAttachments { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Bone worlds for one instance, asked for at DRAW time. Null draws attachments for instance 0
    /// alone, from <see cref="BoneWorlds"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A function rather than an array, and that is the whole design of this feature.</b>
    /// <c>RigAnimation.InstanceBoneWorlds</c> hands back a SHARED scratch for every instance past the
    /// first, valid only until the next call — so the obvious implementation,
    /// </para>
    /// <code>
    /// for (var i = 0; i &lt; n; i++) worlds[i] = session.InstanceBoneWorlds(i);   // WRONG
    /// </code>
    /// <para>
    /// fills the array with N references to one buffer and draws every body's gear in the LAST
    /// echo's pose. No crash and no warning — the same aliasing that once gave every part of a model
    /// the last part's albedo. Materialising N real arrays instead would be correct and would
    /// allocate boneCount matrices per instance per frame; handing this view a <c>RigAnimation</c>
    /// would be correct and would make a per-frame draw description reach back into durable state,
    /// which is the one property that keeps these two types separable.
    /// </para>
    /// <para>
    /// So the view asks for one instance's worlds at the moment it draws that instance and is
    /// finished with them before it asks for the next. <b><see cref="DrawAttachments"/> must stay
    /// instance-outer and attachment-inner for that to hold</b>, which is why the loops are written
    /// that way rather than the other.
    /// </para>
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
    /// <remarks>
    /// <b>The point of the stage: one body with a knife and its neighbour with a crossbow.</b> The
    /// engine takes a selection per body and has no opinion about what drives it — a state machine,
    /// an inventory, or a checkbox in a tool — which is the same rule pose composition follows,
    /// where the engine takes weights and knows nothing about states.
    /// </remarks>
    public Func<int, ISet<string>>? InstanceAttachments { get; set; }

    public void Draw(in StudioDraw draw)
    {
        if (Instances <= 0) return;

        var casterOnly = draw.Pass == StudioPass.Shadow;
        var stride = (float)Rig.Skeleton.BoneCount;

        foreach (var part in Rig.Parts)
        {
            byte[] push;
            if (casterOnly)
            {
                // No model matrix at all: the bones carry the placement, so one here would apply
                // it twice. The stride is the whole payload.
                // Sixteen bytes, and no StudioPush.Matrix call — that writes sixty-four and would
                // run straight off the end of this buffer.
                push = caster;
                var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(push.AsSpan());
                floats[0] = stride; floats[1] = 0f; floats[2] = 0f; floats[3] = 0f;
            }
            else
            {
                push = lit;
                StudioPush.Matrix(Matrix4x4.Identity, push);
                StudioPush.Material(
                    push, part.BaseColour, part.Metallic, part.Roughness, stride,
                    alphaCutoff: StudioAlpha.CutoffFor(part.AlphaMode, part.AlphaCutoff),
                    baseAlpha: part.BaseAlpha);
            }

            // <b>The material decides whether its back face exists.</b> Every material on all three
            // rigged assets in this tree is doubleSided and all of them were drawn by the culling
            // pipeline. Measured rather than described: honouring it moves 731 pixels of a peasant
            // capture (0.02%), mean delta +40 — POSITIVE, so this is surfaces appearing rather than
            // shading shifting — and 81% of those pixels fall in the head band where MI_Hair_1 sits.
            // A small effect from this camera, and a real one; a closed torso genuinely does not
            // care, which is why the culling comment on the pipeline above is not wrong either.
            var skinned = part.DoubleSided && draw.SkinnedDoubleSidedPipeline.Id != 0
                ? draw.SkinnedDoubleSidedPipeline
                : draw.SkinnedPipeline;

            draw.Scope.DrawIndexedInstanced(
                vertexBuffer: part.Vertices,
                indexBuffer: part.Indices,
                pipeline: skinned,
                indexCount: part.IndexCount,
                instanceCount: Instances,
                uniforms: draw.Uniforms,
                textures: casterOnly
                    ? draw.Textures
                    : new[] { draw.Textures[0], new ShaderTextureBinding("uAlbedo", part.Albedo, Slot: 1) },
                // <b>This part's skin, not the rig's first one.</b> Every part carries the index of
                // the skin that poses it, and each skin has its own palette buffer — same pose,
                // different inverse binds and a different authored frame.
                perDrawMaterial: (uint)part.SkinIndex < (uint)Rig.Skins.Count
                    ? Rig.Skins[part.SkinIndex].BoneMaterial
                    : Rig.BoneMaterial,
                pushConstants: push);
        }

        DrawAttachments(draw, casterOnly);
        DrawStaticParts(draw, casterOnly);
    }

    // <b>An attachment is a rung-two draw and costs nothing structurally.</b> It uses the stage's
    // STANDARD lit pipeline — the same one a ground plane or a box uses — with an ordinary model
    // matrix, because it is static geometry that happens to be carried by something that moves. No
    // fourth pipeline, no change to StudioPush, no new pass. That was the thing worth finding out:
    // the ladder said bringing a draw should be the ordinary case, and this is a draw.
    // <b>Always drawn, with no visibility set.</b> An attachment is a choice — four of the Rogue's
    // six share one hand and a game shows one — so the caller picks. A turret is not a choice; it is
    // part of the model, and hiding it by default would be the old bug wearing a checkbox.
    private void DrawStaticParts(in StudioDraw draw, bool casterOnly)
    {
        if (Rig.StaticParts.Count == 0) return;

        var push = casterOnly ? attachCaster : attachLit;
        foreach (var part in Rig.StaticParts)
        {
            // No joint, so no joint world: the node's own world matrix and the body's placement.
            StudioPush.Matrix(part.WorldTransform * Placement, push);
            if (!casterOnly)
            {
                StudioPush.Material(push, part.BaseColour, part.Metallic, part.Roughness);
            }

            draw.Scope.DrawIndexed(
                vertexBuffer: part.Vertices,
                indexBuffer: part.Indices,
                pipeline: draw.Pipeline,
                indexCount: part.IndexCount,
                uniforms: draw.Uniforms,
                textures: casterOnly
                    ? draw.Textures
                    : new[] { draw.Textures[0], new ShaderTextureBinding("uAlbedo", part.Albedo, Slot: 1) },
                pushConstants: push);
        }
    }

    private void DrawAttachments(in StudioDraw draw, bool casterOnly)
    {
        if (Rig.Attachments.Count == 0) return;
        if (InstanceBoneWorlds is null && BoneWorlds is null) return;

        var attachPush = casterOnly ? attachCaster : attachLit;

        // <b>Instance-outer, attachment-inner, and that order is load-bearing.</b> InstanceBoneWorlds
        // hands back a shared scratch for every body past the first; this loop reads one body's
        // worlds, draws everything that body carries, and only then asks for the next. Swapping the
        // loops would ask N times before the first draw and leave every body wearing the last one's
        // pose. See the remarks on InstanceBoneWorlds.
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
            if (!casterOnly)
            {
                StudioPush.Material(attachPush, attachment.BaseColour, attachment.Metallic, attachment.Roughness);
            }

            draw.Scope.DrawIndexed(
                vertexBuffer: attachment.Vertices,
                indexBuffer: attachment.Indices,
                pipeline: draw.Pipeline,
                indexCount: attachment.IndexCount,
                uniforms: draw.Uniforms,
                textures: casterOnly
                    ? draw.Textures
                    : new[] { draw.Textures[0], new ShaderTextureBinding("uAlbedo", attachment.Albedo, Slot: 1) },
                pushConstants: attachPush);
        }
    }

    // Separate from the skinned buffers above: a skinned caster pushes sixteen bytes and a static
    // one pushes sixty-four, so sharing would run one of them off the end of the other's buffer.
    private readonly byte[] attachLit = new byte[StudioPush.LitBytes];
    private readonly byte[] attachCaster = new byte[StudioPush.CasterBytes];
}
