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
    }
}
