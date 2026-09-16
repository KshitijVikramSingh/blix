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
            if (!casterOnly) StudioPush.Material(push, part.BaseColour, part.Metallic, part.Roughness);

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
    /// Joint world transforms for the pose being drawn, from <c>RigSession.BoneWorlds</c>.
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
                StudioPush.Material(push, part.BaseColour, part.Metallic, part.Roughness, stride);
            }

            draw.Scope.DrawIndexedInstanced(
                vertexBuffer: part.Vertices,
                indexBuffer: part.Indices,
                pipeline: draw.SkinnedPipeline,
                indexCount: part.IndexCount,
                instanceCount: Instances,
                uniforms: draw.Uniforms,
                textures: casterOnly
                    ? draw.Textures
                    : new[] { draw.Textures[0], new ShaderTextureBinding("uAlbedo", part.Albedo, Slot: 1) },
                perDrawMaterial: Rig.BoneMaterial,
                pushConstants: push);
        }

        DrawAttachments(draw, casterOnly);
    }

    // <b>An attachment is a rung-two draw and costs nothing structurally.</b> It uses the stage's
    // STANDARD lit pipeline — the same one a ground plane or a box uses — with an ordinary model
    // matrix, because it is static geometry that happens to be carried by something that moves. No
    // fourth pipeline, no change to StudioPush, no new pass. That was the thing worth finding out:
    // the ladder said bringing a draw should be the ordinary case, and this is a draw.
    private void DrawAttachments(in StudioDraw draw, bool casterOnly)
    {
        if (VisibleAttachments.Count == 0 || BoneWorlds is null) return;

        var attachPush = casterOnly ? attachCaster : attachLit;
        foreach (var attachment in Rig.Attachments)
        {
            if (!VisibleAttachments.Contains(attachment.Name)) continue;
            if ((uint)attachment.JointIndex >= (uint)BoneWorlds.Count) continue;

            // local -> joint -> world. Row-vector, left to right, the same direction the hierarchy
            // walk composes in — a transposed multiply here puts the knife in the right place on a
            // rig with no rotation and nowhere near it on one with any.
            var model = attachment.LocalTransform * BoneWorlds[attachment.JointIndex] * Placement;

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
