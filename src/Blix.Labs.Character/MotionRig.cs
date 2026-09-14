using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Assets;
using Blix.Core;
using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Labs.Character;

/// <summary>
/// A rigged glTF as this lab needs it: a skeleton, its clips, its meshes, and a palette to skin by.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written against the ENGINE's types rather than the toolchain lab's</b>, which was a decision
/// taken before the stage started. <c>GltfImporter</c>, <c>Skeleton</c>, <c>ClipPlayer</c>,
/// <c>BonePalette</c> and <c>MaterialBindings</c> do all the work; what is left here is plumbing
/// between those and ONE renderer's shaders, and this lab has its own shaders. Reaching across to
/// <c>LabRig</c> would couple two labs and pull a second lab's <c>.spv</c> into every executable
/// here through content propagation.
/// </para>
/// <para>
/// It came to about a hundred lines, which is the answer to the question R-A left open: there is no
/// shared lab library to extract, because what looked shared was mostly each lab's own renderer.
/// </para>
/// </remarks>
public sealed class MotionRig : IDisposable
{
    /// <summary>Must match the array bound in room_skinned.vert — the probe checks it against the SPIR-V.</summary>
    public const int MaxBones = 128;

    private readonly List<VertexBufferHandle> vertexBuffers = new();
    private readonly List<IndexBufferHandle> indexBuffers = new();
    private readonly List<int> indexCounts = new();

    private VulkanGraphicsDevice device = null!;
    private MaterialBindings bones = null!;
    private byte[] palettePayload = Array.Empty<byte>();

    public Skeleton Skeleton { get; private set; } = null!;

    public IReadOnlyList<AnimationClip> Clips { get; private set; } = Array.Empty<AnimationClip>();

    public BonePalette Palette { get; private set; } = null!;

    /// <summary>The asset's own root transform — the dialled-in constant every consumer restates.</summary>
    public Matrix4x4 MeshNodeTransform { get; private set; } = Matrix4x4.Identity;

    public MaterialHandle BoneMaterial => bones.Handle;

    public int PrimitiveCount => vertexBuffers.Count;

    public string SourcePath { get; private set; } = string.Empty;

    public static MotionRig Load(VulkanGraphicsDevice vk, string path, ShaderProgramHandle skinnedProgram)
    {
        ArgumentNullException.ThrowIfNull(vk);

        var model = new GltfImporter().Import(
            new AssetImportContext(AssetId.Parse("character.rig"), path));

        if (model.Skeleton is null || model.Skeleton.BoneCount == 0)
        {
            throw new InvalidOperationException($"{path} has no skeleton — it is not a rig.");
        }

        if (model.Skeleton.BoneCount > MaxBones)
        {
            throw new InvalidOperationException(
                $"{path} has {model.Skeleton.BoneCount} bones and room_skinned.vert declares {MaxBones}. " +
                "The bound is a literal because the SPIR-V target passes no -D, so it lives in two files " +
                "and the probe is what keeps them agreeing.");
        }

        var rig = new MotionRig
        {
            device = vk,
            SourcePath = path,
            Skeleton = model.Skeleton,
            Clips = model.Animations,
            MeshNodeTransform = model.MeshNodeTransform,
        };

        rig.Palette = new BonePalette(model.Skeleton.BoneCount);
        rig.palettePayload = new byte[model.Skeleton.BoneCount * 64];

        for (var i = 0; i < model.Primitives.Length; i++)
        {
            var mesh = model.Primitives[i].Mesh;
            rig.vertexBuffers.Add(vk.CreateVertexBuffer(
                new VertexBufferData(
                    new VertexBufferDescription(mesh.Layout, mesh.VertexCount, GraphicsBufferUsage.Static),
                    mesh.VertexBytes),
                $"rig.vb{i}"));

            rig.indexBuffers.Add(mesh.Indices32 is { } wide
                ? vk.CreateIndexBuffer(wide, name: $"rig.ib{i}")
                : vk.CreateIndexBuffer(mesh.Indices, name: $"rig.ib{i}"));

            rig.indexCounts.Add(mesh.IndexCount);
        }

        // Set 3, replicated per frame slot. A palette is per-DRAW data living in a descriptor, and a
        // descriptor's buffer is not copied at record time the way a push payload is — so one
        // material per frame slot is what keeps this frame's pose from being overwritten by the next
        // frame's before the GPU has read it.
        rig.bones = vk.CreateMaterial(
            skinnedProgram, setIndex: 3, framesInFlight: vk.MaxFramesInFlightCount, name: "rig.bones");

        return rig;
    }

    /// <summary>Compute the palette for a pose and upload it for this frame.</summary>
    public void UploadPose(Pose pose)
    {
        Skeleton.ComputeBonePalette(pose, Palette);

        for (var i = 0; i < Palette.Matrices.Length; i++)
        {
            MemoryMarshal.Write(palettePayload.AsSpan(i * 64, 64), Palette.Matrices[i]);
        }

        bones.WriteBuffer(device.CurrentFrameSlot, 0, palettePayload);
    }

    public AnimationClip? Clip(string name)
    {
        foreach (var clip in Clips)
        {
            if (string.Equals(clip.Name, name, StringComparison.OrdinalIgnoreCase)) return clip;
        }
        return null;
    }

    /// <summary>Draw every primitive. They all share one palette and one material.</summary>
    public void Draw(
        RenderPassBuilder pass,
        PipelineHandle pipeline,
        IReadOnlyList<ShaderUniform> uniforms,
        IReadOnlyList<ShaderTextureBinding> textures,
        byte[]? push)
    {
        for (var i = 0; i < vertexBuffers.Count; i++)
        {
            // NULL, not an empty array. The caster's shaders declare no push block at all, and the
            // device draws a distinction between "no payload" and "a payload of length zero" —
            // supplying the second against an interface that declares no ranges is an error, and
            // correctly so: it is a caller claiming to fill something that does not exist.
            if (push is null)
            {
                pass.DrawIndexed(
                    vertexBuffers[i], indexBuffers[i], pipeline, indexCounts[i],
                    uniforms, textures, material: BoneMaterial);
            }
            else
            {
                pass.DrawIndexed(
                    vertexBuffers[i], indexBuffers[i], pipeline, indexCounts[i],
                    uniforms, textures, material: BoneMaterial, pushConstants: push);
            }
        }
    }

    public void Dispose()
    {
        if (device is null) return;
        foreach (var vb in vertexBuffers) device.DestroyVertexBuffer(vb);
        foreach (var ib in indexBuffers) device.DestroyIndexBuffer(ib);
        if (bones is not null) device.DestroyMaterial(bones.Handle);
    }
}
