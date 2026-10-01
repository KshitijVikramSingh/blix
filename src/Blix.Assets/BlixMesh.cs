using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Cooked;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Assets;

// Engine-native version 13 mesh container. The shared cooked preamble is followed,
// in order, by counted tables for primitives, materials, images, skins, clips,
// attachments, static parts, and authored nodes. All values are little-endian.
//
// Each primitive carries its own vertex layout, material-table row, bounds, packed
// vertex bytes, skin and node indices, index width, and LOD index chains. Material
// image fields are rows in this file's image table, not source glTF image indices.
// An image row separates identity (name and source-byte hash) from location (a
// recipe-authored relative resource). Texture packing and grouping remain recipe
// policy rather than an inference made by the reader.
//
// Static vertices remain world-baked for the flat runtime path. NodeIndex makes
// that transform reversible for hierarchy-aware consumers: compose the node's
// world transform from the node table and apply its inverse. Parent indices in
// both bone and node tables must refer backward, allowing one-pass composition.
//
// Tables are sequential because runtime readers consume the complete artifact;
// the format has no per-primitive offset table or partial-read contract.
public static class BlixMesh
{
    public const uint Magic = 0x4D584C42; // "BLXM" little-endian

    /// <summary>
    /// The recipe id the shipped mesh cook stamps. A project cooking its own meshes to this format
    /// stamps its own, which is what makes "who made this file" answerable.
    /// </summary>
    public const string ShippedRecipe = "gmsh";
    // Version 13 is the only accepted layout. It includes per-primitive layouts and LOD errors,
    // common provenance, material and image tables (each core channel with its TEXCOORD set, and
    // the normal scale), rig data, attachments/static parts, authored nodes, and the current
    // KHR_materials_* parameter block. v12 mirrors glTF's structure: a node table in every file,
    // meshes stored once and placed by nodes, vertices in mesh space as the complete static (6) or
    // skinned (7) vertex, skins as joint nodes, clips targeting nodes, and the source attributes the
    // cook did not carry (v13). v14 gives each track channel its glTF interpolation (LINEAR, STEP or
    // CUBICSPLINE, with the spline's in/out tangents), and tracks every animated node rather than
    // only skin 0's joints. v15 makes an image row one image as used — role, normal convention and
    // glTF sampler — so an image used two ways is two rows. v16 adds each core channel's
    // KHR_texture_transform to the material. Older layouts must be re-cooked.
    public const uint Version16 = 16;
    public const uint LayoutPosition3NormalTexture = 1;        // 32-byte
    public const uint LayoutPosition3NormalTangentTexture = 2; // 48-byte
    public const uint LayoutPosition3NormalTextureSkin4Tangent = 3; // 80-byte, rigged
    // The two colour-carrying static layouts. They reach this format through a RIG's attachments
    // and static parts, which are built by the static mesh path with includeColour — so a format
    // that knew only the skinned layout could store a rig's skeleton and not its cape.
    public const uint LayoutPosition3NormalTextureColor = 4;      // 36-byte
    public const uint LayoutPosition3NormalTexture2Color = 5;     // 44-byte, two UV sets
    public const uint LayoutPosition3NormalTangentTexture2Color = 6; // 60-byte, the complete static vertex
    public const uint LayoutPosition3NormalTextureSkin4Tangent2Color = 7; // 92-byte, the complete skinned vertex

    public const int NoMaterial = -1;

    /// <summary>
    /// A material channel with no texture, as distinct from one whose image cannot be found.
    /// </summary>
    public const int NoImage = -1;

    /// <summary>The image table's own file name inside a cooked asset's texture folder.</summary>
    /// <remarks>
    /// Where an EXTRACTED image goes: a glTF with its pixels embedded has no path to reuse, so the
    /// cook invents one and writes it into the table. Beside the mesh in a folder of its own rather
    /// than loose next to it, because one asset can carry a hundred of them.
    /// </remarks>
    public const string ExtractedImageFolder = ".textures";

    /// <summary>Alpha modes, matching glTF's own order so the byte is the spec's value.</summary>
    public const byte AlphaOpaque = 0;
    public const byte AlphaMask = 1;
    public const byte AlphaBlend = 2;
    public const byte IndexFormatU16 = 0;
    public const byte IndexFormatU32 = 1;

    /// <summary>The layout a stored id names. The inverse of <see cref="LayoutIdForStride"/>.</summary>
    public static VertexLayout LayoutForId(uint id) => id switch
    {
        LayoutPosition3NormalTexture => VertexPosition3NormalTexture.Layout,
        LayoutPosition3NormalTangentTexture => VertexPosition3NormalTangentTexture.Layout,
        LayoutPosition3NormalTextureSkin4Tangent => VertexPosition3NormalTextureSkin4Tangent.Layout,
        LayoutPosition3NormalTextureColor => VertexPosition3NormalTextureColor.Layout,
        LayoutPosition3NormalTexture2Color => VertexPosition3NormalTexture2Color.Layout,
        LayoutPosition3NormalTangentTexture2Color => VertexPosition3NormalTangentTexture2Color.Layout,
        LayoutPosition3NormalTextureSkin4Tangent2Color => VertexPosition3NormalTextureSkin4Tangent2Color.Layout,
        _ => throw new InvalidDataException($"Unrecognised BlixMesh layout id {id}."),
    };

    // Layout id ↔ stride. The layouts the cook emits.
    public static uint LayoutIdForStride(int stride) => stride switch
    {
        32 => LayoutPosition3NormalTexture,
        48 => LayoutPosition3NormalTangentTexture,
        80 => LayoutPosition3NormalTextureSkin4Tangent,
        36 => LayoutPosition3NormalTextureColor,
        44 => LayoutPosition3NormalTexture2Color,
        60 => LayoutPosition3NormalTangentTexture2Color,
        92 => LayoutPosition3NormalTextureSkin4Tangent2Color,
        _ => throw new ArgumentException($"No BlixMesh layout id for vertex stride {stride}.", nameof(stride)),
    };
}

// One LOD level: an index buffer over the primitive's shared vertex buffer,
// plus the world-space geometric error decimating to this level introduced
// (0 for LOD0, the original surface). Lods[0] is full detail; higher indices
// are progressively decimated with monotonically increasing error.
public sealed record BlixMeshLod(ushort[]? Indices16, uint[]? Indices32, float Error = 0f)
{
    public int IndexCount => Indices32?.Length ?? Indices16!.Length;
}

/// <summary>One image a material references: what it is, and where its pixels were put.</summary>
/// <remarks>
/// <paramref name="Resource"/> is written by the recipe rather than derived by the loader. The
/// shipped recipe records one relative artifact per source image; alternate grouping remains
/// project recipe and resolver policy.
/// </remarks>
/// <param name="Name">Human-meaningful and stable; for diagnostics and for a resolver to key on.</param>
/// <param name="ContentHash">
/// Of the SOURCE image bytes. Two rows with the same hash are the same picture however they were
/// named or wherever they were put, which is what makes dedup and cache reuse decidable without
/// reading pixels.
/// </param>
/// <param name="Resource">
/// Relative to the cooked mesh, with forward slashes, so it means the same thing on every machine.
/// </param>
/// <param name="Sampler">How the materials using this row sample it: glTF's sampler, as glTF's codes.</param>
/// <remarks>
/// A row is one image AS USED: one role (colour or data), one normal convention, one sampler. The same
/// source image used two ways is two rows — two cooked files when the role or convention differs, one
/// file and two samplers when only the sampler does.
/// </remarks>
public sealed record BlixMeshImage(string Name, ulong ContentHash, string Resource, BlixMeshSampler Sampler = default);

/// <summary>A <c>KHR_texture_transform</c>: a channel's UVs scaled, rotated, then offset (uv' = T R S uv).</summary>
/// <remarks>Its <c>texCoord</c> override is folded into the channel's TEXCOORD index at the cook.</remarks>
public readonly record struct BlixMeshUvTransform(Vector2 Offset, float Rotation, Vector2 Scale)
{
    public static BlixMeshUvTransform Identity { get; } = new(Vector2.Zero, 0f, Vector2.One);
}

/// <summary>The five core channels' UV transforms.</summary>
public sealed record BlixMeshUvTransforms(
    BlixMeshUvTransform BaseColor, BlixMeshUvTransform Normal, BlixMeshUvTransform MetallicRoughness,
    BlixMeshUvTransform Occlusion, BlixMeshUvTransform Emissive)
{
    public static BlixMeshUvTransforms Identity { get; } = new(
        BlixMeshUvTransform.Identity, BlixMeshUvTransform.Identity, BlixMeshUvTransform.Identity,
        BlixMeshUvTransform.Identity, BlixMeshUvTransform.Identity);

    public IEnumerable<BlixMeshUvTransform> All => new[] { BaseColor, Normal, MetallicRoughness, Occlusion, Emissive };
}

/// <summary>A glTF sampler, kept as glTF's own codes: what the file says, for the reader to map.</summary>
/// <param name="WrapS">10497 REPEAT, 33071 CLAMP_TO_EDGE, 33648 MIRRORED_REPEAT. 0 reads as REPEAT, the spec's default.</param>
/// <param name="MinFilter">9728/9729 NEAREST/LINEAR, 9984-9987 the four mipmap modes; 0 when unspecified.</param>
/// <param name="MagFilter">9728 NEAREST, 9729 LINEAR; 0 when unspecified.</param>
public readonly record struct BlixMeshSampler(int WrapS, int WrapT, int MinFilter, int MagFilter);

/// <summary>
/// A cooked material whose image fields index this file's image table. -1 means the channel has no
/// texture, distinct from a row whose resource is missing.
/// </summary>
public sealed record BlixMeshMaterial(
    string Name,
    Vector4 BaseColorFactor,
    int BaseColorTexCoord,
    int NormalTexCoord,
    float NormalScale,
    int MetallicRoughnessTexCoord,
    int OcclusionTexCoord,
    int EmissiveTexCoord,
    float MetallicFactor,
    float RoughnessFactor,
    float OcclusionStrength,
    Vector3 EmissiveFactor,
    float EmissiveStrength,
    byte AlphaMode,
    float AlphaCutoff,
    bool DoubleSided,
    float TransmissionFactor,
    int BaseColorImage = BlixMesh.NoImage,
    int NormalImage = BlixMesh.NoImage,
    int MetallicRoughnessImage = BlixMesh.NoImage,
    int OcclusionImage = BlixMesh.NoImage,
    int EmissiveImage = BlixMesh.NoImage,

    /// <summary>Every <c>KHR_materials_*</c> property, as cooked. Null for in-memory callers that omit the block.</summary>
    BlixMaterialExtensions? Extensions = null,

    /// <summary>Each core channel's <c>KHR_texture_transform</c>; null reads as identity on every channel.</summary>
    BlixMeshUvTransforms? UvTransforms = null)
{
    /// <summary>The extensions, never null — an absent block reads as every spec default.</summary>
    public BlixMaterialExtensions Ext => Extensions ?? BlixMaterialExtensions.None;
}

/// <summary>
/// The cooked mirror of <c>PbrMaterialExtensions</c>: every <c>KHR_materials_*</c> property.
/// </summary>
/// <remarks>
/// <para>
/// A format type rather than the engine one, for the same reason <see cref="BlixMeshMaterial"/> is
/// not <c>PbrMaterial</c>: the runtime types live in <c>Blix</c>, which references this assembly.
/// The visible difference is textures — the engine carries a resolved <c>TextureData</c>, a file
/// carries an INDEX into the image table, because a file cannot hold a decoded image and a path is
/// the thing that survives being written down.
/// </para>
/// <para>
/// Defaults are the SPEC's, not zero: IOR 1.5, attenuation distance infinite, specular strength 1.
/// Absence of an extension means "the base BRDF", and a file that wrote zeros would be authoring a
/// different material rather than declining to say anything.
/// </para>
/// </remarks>
public sealed record BlixMaterialExtensions(
    float TransmissionFactor, int TransmissionImage,
    float DiffuseTransmissionFactor, Vector3 DiffuseTransmissionColorFactor,
    int DiffuseTransmissionImage, int DiffuseTransmissionColorImage,
    Vector3 SheenColorFactor, float SheenRoughnessFactor,
    int SheenColorImage, int SheenRoughnessImage,
    float ThicknessFactor, float AttenuationDistance, Vector3 AttenuationColor, int ThicknessImage,
    float SpecularFactor, Vector3 SpecularColorFactor, int SpecularImage, int SpecularColorImage,
    float IndexOfRefraction,
    float ClearcoatFactor, float ClearcoatRoughnessFactor, float ClearcoatNormalScale,
    int ClearcoatImage, int ClearcoatRoughnessImage, int ClearcoatNormalImage,
    float IridescenceFactor, float IridescenceIor,
    float IridescenceThicknessMinimum, float IridescenceThicknessMaximum,
    int IridescenceImage, int IridescenceThicknessImage,
    float AnisotropyStrength, float AnisotropyRotation, int AnisotropyImage,
    float Dispersion, bool Unlit)
{
    /// <summary>Every field at its glTF-specified default: the material that declares no extension.</summary>
    public static readonly BlixMaterialExtensions None = new(
        TransmissionFactor: 0f, TransmissionImage: BlixMesh.NoImage,
        DiffuseTransmissionFactor: 0f, DiffuseTransmissionColorFactor: Vector3.One,
        DiffuseTransmissionImage: BlixMesh.NoImage, DiffuseTransmissionColorImage: BlixMesh.NoImage,
        SheenColorFactor: Vector3.Zero, SheenRoughnessFactor: 0f,
        SheenColorImage: BlixMesh.NoImage, SheenRoughnessImage: BlixMesh.NoImage,
        ThicknessFactor: 0f, AttenuationDistance: float.PositiveInfinity, AttenuationColor: Vector3.One,
        ThicknessImage: BlixMesh.NoImage,
        SpecularFactor: 1f, SpecularColorFactor: Vector3.One,
        SpecularImage: BlixMesh.NoImage, SpecularColorImage: BlixMesh.NoImage,
        IndexOfRefraction: 1.5f,
        ClearcoatFactor: 0f, ClearcoatRoughnessFactor: 0f, ClearcoatNormalScale: 1f,
        ClearcoatImage: BlixMesh.NoImage, ClearcoatRoughnessImage: BlixMesh.NoImage,
        ClearcoatNormalImage: BlixMesh.NoImage,
        IridescenceFactor: 0f, IridescenceIor: 1.3f,
        IridescenceThicknessMinimum: 100f, IridescenceThicknessMaximum: 400f,
        IridescenceImage: BlixMesh.NoImage, IridescenceThicknessImage: BlixMesh.NoImage,
        AnisotropyStrength: 0f, AnisotropyRotation: 0f, AnisotropyImage: BlixMesh.NoImage,
        Dispersion: 0f, Unlit: false);
}

/// <summary>One bone of a cooked skeleton — the format's mirror of the runtime <c>Bone</c>.</summary>
/// <remarks>
/// A format type rather than the runtime one, for the same reason <see cref="BlixMeshMaterial"/> is
/// not <c>PbrMaterial</c>: the runtime types live in <c>Blix</c>, which references this assembly,
/// and a format that reached back for them would invert that. The conversion is one constructor
/// call at each end, and it is what keeps the file readable by a tool that does not load the engine.
/// </remarks>
/// <summary>One bone of a skin: its joint node, its parent among the skin's bones, and its inverse bind.</summary>
/// <param name="NodeIndex">The joint: a row of the file's node table.</param>
public sealed record BlixMeshBone(string Name, int ParentIndex, Matrix4x4 InverseBindPose, int NodeIndex);

/// <summary>How a channel reads between keys: glTF's sampler interpolation.</summary>
public enum BlixMeshInterpolation : byte
{
    Linear = 0,
    Step = 1,
    CubicSpline = 2,
}

/// <summary>A translation or scale key. The tangents are read, and stored, only in a CUBICSPLINE channel.</summary>
public readonly record struct BlixMeshVectorKey(float Time, Vector3 Value, Vector3 InTangent = default, Vector3 OutTangent = default);

/// <summary>A rotation key. The tangents are read, and stored, only in a CUBICSPLINE channel.</summary>
public readonly record struct BlixMeshQuaternionKey(
    float Time, Quaternion Value, Quaternion InTangent = default, Quaternion OutTangent = default);

/// <summary>One node's animation across a clip. An empty channel array means that channel is absent.</summary>
/// <remarks>
/// Absent and empty are represented the same way. glTF gives a channel keys or does
/// not give it at all; a channel with zero keys has no meaning either way, so one representation
/// covers both and the reader needs no presence flag per channel.
/// </remarks>
/// <param name="NodeIndex">The node animated, as glTF's channels target nodes: a joint or any other node.</param>
public sealed record BlixMeshTrack(
    int NodeIndex,
    BlixMeshVectorKey[] Translation,
    BlixMeshQuaternionKey[] Rotation,
    BlixMeshVectorKey[] Scale,
    BlixMeshInterpolation TranslationInterpolation = BlixMeshInterpolation.Linear,
    BlixMeshInterpolation RotationInterpolation = BlixMeshInterpolation.Linear,
    BlixMeshInterpolation ScaleInterpolation = BlixMeshInterpolation.Linear);

/// <summary>A named animation. Duration is derived from the keys, never stored.</summary>
/// <remarks>
/// Derived rather than stored because a stored duration is a second source of truth that can
/// disagree with the keys — and the runtime <c>AnimationClip</c> already computes it from them.
/// </remarks>
public sealed record BlixMeshClip(string Name, BlixMeshTrack[] Tracks);

/// <summary>One skin: its bones, in hierarchy order — every bone's parent is -1 or strictly below it.</summary>
/// <remarks>
/// Where the skinned geometry sits is not stored here: it is the world transform of the node that
/// places the skinned mesh, which the node table already says.
/// </remarks>
public sealed record BlixMeshSkin(BlixMeshBone[] Bones);

/// <summary>One node of the authored scene graph, as glTF has one in every file.</summary>
/// <remarks>
/// Transform-only nodes are kept, not pruned: a parent that carries no geometry is still what a
/// child's transform is relative to, and dropping it would break the composition it exists for.
/// </remarks>
/// <param name="Name">The authored name; empty when the source gave none.</param>
/// <param name="MeshIndex">The mesh this node places, or -1.</param>
/// <param name="SkinIndex">The skin that deforms that mesh, or -1; a skinned mesh is placed by its skin, not its node.</param>
public sealed record BlixMeshNode(string Name, int ParentIndex, Matrix4x4 LocalTransform, int MeshIndex = -1, int SkinIndex = -1);

/// <summary>A mesh: primitives in mesh space, stored once however many nodes place it.</summary>
/// <param name="Name">The authored name; empty when the source gave none.</param>
/// <param name="SkinIndex">
/// The skin whose bone order its joint indices address, or -1 for a static mesh. A glTF mesh placed
/// both skinned and unskinned is two meshes here, because the vertices differ.
/// </param>
/// <summary>A source vertex attribute the cook did not carry, and on how many primitives.</summary>
public sealed record BlixMeshIgnored(string Semantic, int Primitives);

public sealed record BlixMeshMesh(string Name, IReadOnlyList<BlixMeshPrimitive> Primitives, int SkinIndex = -1);

public sealed record BlixMeshPrimitive(
    string Name,
    /// <summary>
    /// This primitive's own vertex layout.
    /// </summary>
    /// <remarks>
    /// Layout is per primitive because a rigged file may contain skinned geometry, static joint
    /// attachments, and independent static parts with different vertex widths.
    /// </remarks>
    VertexLayout Layout,
    int MaterialIndex,
    Bounds3 Bounds,
    int VertexCount,
    byte[] VertexBytes,
    IndexFormat IndexFormat,
    IReadOnlyList<BlixMeshLod> Lods);

/// <param name="Cooked">
/// The preamble, when this came off disk. Null when it was built in memory on the way to being
/// written — a file knows its own provenance, a thing about to become one does not yet.
/// </param>
/// <param name="Skins">
/// The skins, each with bones in hierarchy order — every bone's parent is -1 or strictly below it.
/// Empty for a static mesh, which is what makes "is this rigged?" answerable without reading a
/// vertex. A primitive names its skin by index.
/// </param>
/// <param name="Clips">The animations, if any. A rig may legitimately ship with none.</param>
/// <param name="Images">
/// Every image the materials reference, and where the cook put each one. A material channel's image
/// field is an index into THIS list. Empty means the materials reference no textures at all.
/// </param>
/// <param name="Materials">
/// The source's materials, in its own order, so a primitive's MaterialIndex addresses this table.
/// Empty is legal and means exactly what it says — an <c>.obj</c> with no <c>.mtl</c>, or a recipe
/// that has nothing to record — never "look in the source instead".
/// </param>
/// <param name="Ignored">The source's vertex attributes the cook did not carry, with how many primitives had each.</param>
public sealed record BlixMeshFile(
    IReadOnlyList<BlixMeshNode> Nodes,
    IReadOnlyList<BlixMeshMesh> Meshes,
    IReadOnlyList<BlixMeshMaterial>? Materials = null,
    IReadOnlyList<BlixMeshImage>? Images = null,
    IReadOnlyList<BlixMeshSkin>? Skins = null,
    IReadOnlyList<BlixMeshClip>? Clips = null,
    CookedHeader? Cooked = null,
    IReadOnlyList<BlixMeshIgnored>? Ignored = null)
{
    /// <summary>Never null: a file whose cook read every attribute reads as none.</summary>
    public IReadOnlyList<BlixMeshIgnored> IgnoredTable => Ignored ?? Array.Empty<BlixMeshIgnored>();

    /// <summary>A file with no hierarchy of its own: one root node placing one mesh of every primitive.</summary>
    /// <remarks>For a source that has no scene graph, such as an OBJ; glTF always has one.</remarks>
    public static BlixMeshFile Flat(
        string name, IReadOnlyList<BlixMeshPrimitive> primitives,
        IReadOnlyList<BlixMeshMaterial>? materials = null, IReadOnlyList<BlixMeshImage>? images = null) =>
        new(new[] { new BlixMeshNode(name, -1, Matrix4x4.Identity, MeshIndex: 0) },
            new[] { new BlixMeshMesh(name, primitives) }, materials, images);

    /// <summary>Never null: a file with no material table reads as an empty one.</summary>
    public IReadOnlyList<BlixMeshMaterial> MaterialTable => Materials ?? Array.Empty<BlixMeshMaterial>();

    /// <summary>Never null: a file with no image table reads as an empty one.</summary>
    public IReadOnlyList<BlixMeshImage> ImageTable => Images ?? Array.Empty<BlixMeshImage>();

    /// <summary>Never null: a file with no skin reads as none.</summary>
    public IReadOnlyList<BlixMeshSkin> SkinTable => Skins ?? Array.Empty<BlixMeshSkin>();

    /// <summary>Never null: a file with no animation reads as no clips.</summary>
    public IReadOnlyList<BlixMeshClip> ClipTable => Clips ?? Array.Empty<BlixMeshClip>();

    /// <summary>True when some node's mesh is deformed by a skin.</summary>
    public bool IsRigged => SkinTable.Count > 0;

    /// <summary>Every primitive a node places, in node order, with the placing node; a mesh placed twice appears twice.</summary>
    public IEnumerable<(BlixMeshPrimitive Primitive, int NodeIndex)> PlacedPrimitives()
    {
        for (var i = 0; i < Nodes.Count; i++)
        {
            if (Nodes[i].MeshIndex < 0) continue;
            foreach (var p in Meshes[Nodes[i].MeshIndex].Primitives) yield return (p, i);
        }
    }

    /// <summary>Every node's world transform, composed in one forward pass (parents precede children).</summary>
    public Matrix4x4[] WorldTransforms()
    {
        var world = new Matrix4x4[Nodes.Count];
        for (var i = 0; i < Nodes.Count; i++)
        {
            world[i] = Nodes[i].ParentIndex < 0
                ? Nodes[i].LocalTransform
                : Nodes[i].LocalTransform * world[Nodes[i].ParentIndex];
        }

        return world;
    }
}

internal static class BlixMeshBinary
{
    internal static string ReadString(BinaryReader br) =>
        System.Text.Encoding.UTF8.GetString(br.ReadBytes(br.ReadInt32()));

    internal static BlixMeshPrimitive ReadPrimitive(BinaryReader br, string path)
    {
        var name = ReadString(br);
        var layout = BlixMesh.LayoutForId(br.ReadUInt32());
        var materialIndex = br.ReadInt32();
        var minX = br.ReadSingle(); var minY = br.ReadSingle(); var minZ = br.ReadSingle();
        var maxX = br.ReadSingle(); var maxY = br.ReadSingle(); var maxZ = br.ReadSingle();
        var bounds = new Bounds3(new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ));
        var vertexCount = br.ReadInt32();
        var vertexBytes = br.ReadBytes(br.ReadInt32());
        var isU32 = br.ReadByte() == BlixMesh.IndexFormatU32;

        var lodCount = br.ReadInt32();
        if (lodCount is < 1 or > 32)
        {
            throw new InvalidDataException($"'{path}' primitive '{name}' has invalid lodCount {lodCount}.");
        }

        var lods = new BlixMeshLod[lodCount];
        for (var l = 0; l < lodCount; l++)
        {
            var indexCount = br.ReadInt32();
            var error = br.ReadSingle();
            if (isU32)
            {
                var indices32 = new uint[indexCount];
                br.ReadBytes(indexCount * 4).AsSpan().CopyTo(MemoryMarshal.AsBytes(indices32.AsSpan()));
                lods[l] = new BlixMeshLod(null, indices32, error);
            }
            else
            {
                var indices16 = new ushort[indexCount];
                br.ReadBytes(indexCount * 2).AsSpan().CopyTo(MemoryMarshal.AsBytes(indices16.AsSpan()));
                lods[l] = new BlixMeshLod(indices16, null, error);
            }
        }

        return new BlixMeshPrimitive(
            name, layout, materialIndex, bounds, vertexCount, vertexBytes,
            isU32 ? IndexFormat.UInt32 : IndexFormat.UInt16, lods);
    }

    internal static Matrix4x4 ReadMatrix(BinaryReader br) => new(
        br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
        br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
        br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
        br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

    internal static BlixMeshUvTransforms ReadUvTransforms(BinaryReader br)
    {
        BlixMeshUvTransform One() => new(new Vector2(br.ReadSingle(), br.ReadSingle()), br.ReadSingle(), new Vector2(br.ReadSingle(), br.ReadSingle()));
        return new BlixMeshUvTransforms(One(), One(), One(), One(), One());
    }

    internal static (BlixMeshVectorKey[] Keys, BlixMeshInterpolation Interpolation) ReadVectorKeys(BinaryReader br)
    {
        var interpolation = ReadInterpolation(br);
        var n = br.ReadInt32();
        var keys = new BlixMeshVectorKey[n];
        for (var i = 0; i < n; i++)
        {
            var time = br.ReadSingle();
            var value = ReadVector3(br);
            keys[i] = interpolation == BlixMeshInterpolation.CubicSpline
                ? new BlixMeshVectorKey(time, value, ReadVector3(br), ReadVector3(br))
                : new BlixMeshVectorKey(time, value);
        }

        return (keys, interpolation);
    }

    internal static (BlixMeshQuaternionKey[] Keys, BlixMeshInterpolation Interpolation) ReadQuaternionKeys(BinaryReader br)
    {
        var interpolation = ReadInterpolation(br);
        var n = br.ReadInt32();
        var keys = new BlixMeshQuaternionKey[n];
        for (var i = 0; i < n; i++)
        {
            var time = br.ReadSingle();
            var value = ReadQuaternion(br);
            keys[i] = interpolation == BlixMeshInterpolation.CubicSpline
                ? new BlixMeshQuaternionKey(time, value, ReadQuaternion(br), ReadQuaternion(br))
                : new BlixMeshQuaternionKey(time, value);
        }

        return (keys, interpolation);
    }

    private static BlixMeshInterpolation ReadInterpolation(BinaryReader br)
    {
        var raw = br.ReadByte();
        return raw <= (byte)BlixMeshInterpolation.CubicSpline
            ? (BlixMeshInterpolation)raw
            : throw new InvalidDataException($"a track channel names interpolation {raw}; 0-2 are defined.");
    }

    private static Vector3 ReadVector3(BinaryReader br) => new(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

    private static Quaternion ReadQuaternion(BinaryReader br) => new(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
}

public static class BlixMeshWriter
{
    /// <param name="stamp">
    /// Who cooked this, from what, and with which settings. Required so this writer cannot produce
    /// an unstamped artifact.
    /// </param>
    public static void Write(string path, BlixMeshFile file, in CookStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(file);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        CookPreamble.Write(fs, BlixMesh.Magic, BlixMesh.Version16, stamp);
        using var bw = new BinaryWriter(fs);

        bw.Write(file.Nodes.Count);
        foreach (var node in file.Nodes)
        {
            WriteString(bw, node.Name);
            bw.Write(node.ParentIndex);
            WriteMatrix(bw, node.LocalTransform);
            bw.Write(node.MeshIndex);
            bw.Write(node.SkinIndex);
        }

        bw.Write(file.Meshes.Count);
        foreach (var mesh in file.Meshes)
        {
            WriteString(bw, mesh.Name);
            bw.Write(mesh.SkinIndex);
            bw.Write(mesh.Primitives.Count);
            foreach (var p in mesh.Primitives) WritePrimitive(bw, p);
        }

        var materials = file.MaterialTable;
        bw.Write(materials.Count);
        foreach (var m in materials)
        {
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(m.Name);
            bw.Write(nameBytes.Length);
            bw.Write(nameBytes);
            bw.Write(m.BaseColorFactor.X); bw.Write(m.BaseColorFactor.Y);
            bw.Write(m.BaseColorFactor.Z); bw.Write(m.BaseColorFactor.W);
            bw.Write(m.BaseColorTexCoord);
            bw.Write(m.NormalTexCoord);
            bw.Write(m.NormalScale);
            bw.Write(m.MetallicRoughnessTexCoord);
            bw.Write(m.OcclusionTexCoord);
            bw.Write(m.EmissiveTexCoord);
            bw.Write(m.MetallicFactor);
            bw.Write(m.RoughnessFactor);
            bw.Write(m.OcclusionStrength);
            bw.Write(m.EmissiveFactor.X); bw.Write(m.EmissiveFactor.Y); bw.Write(m.EmissiveFactor.Z);
            bw.Write(m.EmissiveStrength);
            bw.Write(m.AlphaMode);
            bw.Write(m.AlphaCutoff);
            bw.Write(m.DoubleSided);
            bw.Write(m.TransmissionFactor);
            bw.Write(m.BaseColorImage);
            bw.Write(m.NormalImage);
            bw.Write(m.MetallicRoughnessImage);
            bw.Write(m.OcclusionImage);
            bw.Write(m.EmissiveImage);

            var x = m.Ext;
            bw.Write(x.TransmissionFactor); bw.Write(x.TransmissionImage);
            bw.Write(x.DiffuseTransmissionFactor); bw.Write(x.DiffuseTransmissionColorFactor.X); bw.Write(x.DiffuseTransmissionColorFactor.Y); bw.Write(x.DiffuseTransmissionColorFactor.Z);
            bw.Write(x.DiffuseTransmissionImage); bw.Write(x.DiffuseTransmissionColorImage);
            bw.Write(x.SheenColorFactor.X); bw.Write(x.SheenColorFactor.Y); bw.Write(x.SheenColorFactor.Z); bw.Write(x.SheenRoughnessFactor);
            bw.Write(x.SheenColorImage); bw.Write(x.SheenRoughnessImage);
            bw.Write(x.ThicknessFactor); bw.Write(x.AttenuationDistance);
            bw.Write(x.AttenuationColor.X); bw.Write(x.AttenuationColor.Y); bw.Write(x.AttenuationColor.Z); bw.Write(x.ThicknessImage);
            bw.Write(x.SpecularFactor); bw.Write(x.SpecularColorFactor.X); bw.Write(x.SpecularColorFactor.Y); bw.Write(x.SpecularColorFactor.Z);
            bw.Write(x.SpecularImage); bw.Write(x.SpecularColorImage);
            bw.Write(x.IndexOfRefraction);
            bw.Write(x.ClearcoatFactor); bw.Write(x.ClearcoatRoughnessFactor); bw.Write(x.ClearcoatNormalScale);
            bw.Write(x.ClearcoatImage); bw.Write(x.ClearcoatRoughnessImage); bw.Write(x.ClearcoatNormalImage);
            bw.Write(x.IridescenceFactor); bw.Write(x.IridescenceIor);
            bw.Write(x.IridescenceThicknessMinimum); bw.Write(x.IridescenceThicknessMaximum);
            bw.Write(x.IridescenceImage); bw.Write(x.IridescenceThicknessImage);
            bw.Write(x.AnisotropyStrength); bw.Write(x.AnisotropyRotation); bw.Write(x.AnisotropyImage);
            bw.Write(x.Dispersion); bw.Write(x.Unlit);

            foreach (var t in (m.UvTransforms ?? BlixMeshUvTransforms.Identity).All)
            {
                bw.Write(t.Offset.X); bw.Write(t.Offset.Y); bw.Write(t.Rotation); bw.Write(t.Scale.X); bw.Write(t.Scale.Y);
            }
        }

        var images = file.ImageTable;
        bw.Write(images.Count);
        foreach (var img in images)
        {
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(img.Name);
            bw.Write(nameBytes.Length);
            bw.Write(nameBytes);
            bw.Write(img.ContentHash);
            var resourceBytes = System.Text.Encoding.UTF8.GetBytes(img.Resource);
            bw.Write(resourceBytes.Length);
            bw.Write(resourceBytes);
            bw.Write(img.Sampler.WrapS); bw.Write(img.Sampler.WrapT);
            bw.Write(img.Sampler.MinFilter); bw.Write(img.Sampler.MagFilter);
        }

        var skins = file.SkinTable;
        bw.Write(skins.Count);
        foreach (var skin in skins)
        {
            bw.Write(skin.Bones.Length);
            foreach (var bone in skin.Bones)
            {
                var nameBytes = System.Text.Encoding.UTF8.GetBytes(bone.Name);
                bw.Write(nameBytes.Length);
                bw.Write(nameBytes);
                bw.Write(bone.ParentIndex);
                WriteMatrix(bw, bone.InverseBindPose);
                bw.Write(bone.NodeIndex);
            }
        }

        var clips = file.ClipTable;
        bw.Write(clips.Count);
        foreach (var clip in clips) WriteClip(bw, clip);

        var ignored = file.IgnoredTable;
        bw.Write(ignored.Count);
        foreach (var i in ignored)
        {
            WriteString(bw, i.Semantic);
            bw.Write(i.Primitives);
        }
    }

    private static void WriteClip(BinaryWriter bw, BlixMeshClip clip)
    {
        {
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(clip.Name);
            bw.Write(nameBytes.Length);
            bw.Write(nameBytes);
            bw.Write(clip.Tracks.Length);
            foreach (var track in clip.Tracks)
            {
                bw.Write(track.NodeIndex);
                WriteVectorKeys(bw, track.Translation, track.TranslationInterpolation);
                WriteQuaternionKeys(bw, track.Rotation, track.RotationInterpolation);
                WriteVectorKeys(bw, track.Scale, track.ScaleInterpolation);
            }
        }

    }

    private static void WriteString(BinaryWriter bw, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }


    private static void WritePrimitive(BinaryWriter bw, BlixMeshPrimitive p)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(p.Name);
        bw.Write(nameBytes.Length);
        bw.Write(nameBytes);
        bw.Write(BlixMesh.LayoutIdForStride(p.Layout.Stride));
        bw.Write(p.MaterialIndex);
        bw.Write(p.Bounds.Min.X); bw.Write(p.Bounds.Min.Y); bw.Write(p.Bounds.Min.Z);
        bw.Write(p.Bounds.Max.X); bw.Write(p.Bounds.Max.Y); bw.Write(p.Bounds.Max.Z);
        bw.Write(p.VertexCount);
        bw.Write(p.VertexBytes.Length);
        bw.Write(p.VertexBytes);
        bw.Write((byte)(p.IndexFormat == IndexFormat.UInt32 ? BlixMesh.IndexFormatU32 : BlixMesh.IndexFormatU16));
        if (p.Lods is null || p.Lods.Count == 0)
        {
            throw new ArgumentException($"Primitive '{p.Name}' has no LOD levels.", nameof(p));
        }

        bw.Write(p.Lods.Count);
        foreach (var lod in p.Lods)
        {
            if (p.IndexFormat == IndexFormat.UInt32)
            {
                if (lod.Indices32 is null)
                    throw new ArgumentException($"Primitive '{p.Name}' LOD has IndexFormat=UInt32 but Indices32 is null.", nameof(p));
                bw.Write(lod.Indices32.Length);
                bw.Write(lod.Error);
                bw.Write(MemoryMarshal.AsBytes(lod.Indices32.AsSpan()));
            }
            else
            {
                if (lod.Indices16 is null)
                    throw new ArgumentException($"Primitive '{p.Name}' LOD has IndexFormat=UInt16 but Indices16 is null.", nameof(p));
                bw.Write(lod.Indices16.Length);
                bw.Write(lod.Error);
                bw.Write(MemoryMarshal.AsBytes(lod.Indices16.AsSpan()));
            }
        }
    }

    private static void WriteMatrix(BinaryWriter bw, in Matrix4x4 m)
    {
        bw.Write(m.M11); bw.Write(m.M12); bw.Write(m.M13); bw.Write(m.M14);
        bw.Write(m.M21); bw.Write(m.M22); bw.Write(m.M23); bw.Write(m.M24);
        bw.Write(m.M31); bw.Write(m.M32); bw.Write(m.M33); bw.Write(m.M34);
        bw.Write(m.M41); bw.Write(m.M42); bw.Write(m.M43); bw.Write(m.M44);
    }

    private static void WriteVectorKeys(BinaryWriter bw, BlixMeshVectorKey[] keys, BlixMeshInterpolation interpolation)
    {
        bw.Write((byte)interpolation);
        bw.Write(keys.Length);
        foreach (var k in keys)
        {
            bw.Write(k.Time);
            bw.Write(k.Value.X); bw.Write(k.Value.Y); bw.Write(k.Value.Z);
            if (interpolation != BlixMeshInterpolation.CubicSpline) continue;
            bw.Write(k.InTangent.X); bw.Write(k.InTangent.Y); bw.Write(k.InTangent.Z);
            bw.Write(k.OutTangent.X); bw.Write(k.OutTangent.Y); bw.Write(k.OutTangent.Z);
        }
    }

    private static void WriteQuaternionKeys(BinaryWriter bw, BlixMeshQuaternionKey[] keys, BlixMeshInterpolation interpolation)
    {
        bw.Write((byte)interpolation);
        bw.Write(keys.Length);
        foreach (var k in keys)
        {
            bw.Write(k.Time);
            bw.Write(k.Value.X); bw.Write(k.Value.Y); bw.Write(k.Value.Z); bw.Write(k.Value.W);
            if (interpolation != BlixMeshInterpolation.CubicSpline) continue;
            bw.Write(k.InTangent.X); bw.Write(k.InTangent.Y); bw.Write(k.InTangent.Z); bw.Write(k.InTangent.W);
            bw.Write(k.OutTangent.X); bw.Write(k.OutTangent.Y); bw.Write(k.OutTangent.Z); bw.Write(k.OutTangent.W);
        }
    }
}

public static class BlixMeshReader
{
    /// <summary>Reads the <c>KHR_materials_*</c> block, in the order the writer emits it.</summary>
    /// <remarks>
    /// Positional and exact. There is no length prefix and no field tags, because the format does not
    /// do optional data — it bumps its version and re-cooks, as it has from v2 to v13 — and a reader
    /// that guessed would turn a format change into silently wrong materials rather than a refusal.
    /// </remarks>
    private static BlixMaterialExtensions ReadExtensions(BinaryReader br)
    {
        Vector3 V3() => new(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        return new BlixMaterialExtensions(
            TransmissionFactor: br.ReadSingle(), TransmissionImage: br.ReadInt32(),
            DiffuseTransmissionFactor: br.ReadSingle(), DiffuseTransmissionColorFactor: V3(),
            DiffuseTransmissionImage: br.ReadInt32(), DiffuseTransmissionColorImage: br.ReadInt32(),
            SheenColorFactor: V3(), SheenRoughnessFactor: br.ReadSingle(),
            SheenColorImage: br.ReadInt32(), SheenRoughnessImage: br.ReadInt32(),
            ThicknessFactor: br.ReadSingle(), AttenuationDistance: br.ReadSingle(),
            AttenuationColor: V3(), ThicknessImage: br.ReadInt32(),
            SpecularFactor: br.ReadSingle(), SpecularColorFactor: V3(),
            SpecularImage: br.ReadInt32(), SpecularColorImage: br.ReadInt32(),
            IndexOfRefraction: br.ReadSingle(),
            ClearcoatFactor: br.ReadSingle(), ClearcoatRoughnessFactor: br.ReadSingle(),
            ClearcoatNormalScale: br.ReadSingle(),
            ClearcoatImage: br.ReadInt32(), ClearcoatRoughnessImage: br.ReadInt32(),
            ClearcoatNormalImage: br.ReadInt32(),
            IridescenceFactor: br.ReadSingle(), IridescenceIor: br.ReadSingle(),
            IridescenceThicknessMinimum: br.ReadSingle(), IridescenceThicknessMaximum: br.ReadSingle(),
            IridescenceImage: br.ReadInt32(), IridescenceThicknessImage: br.ReadInt32(),
            AnisotropyStrength: br.ReadSingle(), AnisotropyRotation: br.ReadSingle(),
            AnisotropyImage: br.ReadInt32(),
            Dispersion: br.ReadSingle(), Unlit: br.ReadBoolean());
    }

    /// <summary>Reads a cooked mesh, refusing anything that is not one by name.</summary>
    /// <remarks>
    /// Everything past the preamble is wrapped, so a truncated or corrupt body arrives as the
    /// engine declining a file rather than as whatever <see cref="BinaryReader"/> happened to throw
    /// — the same sentence a bad glTF gets, so a tool catches one type and survives both.
    /// </remarks>
    public static BlixMeshFile Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = CookPreamble.Read(fs, path).Require(BlixMesh.Magic, BlixMesh.Version16, path, ".blixmesh");
        return AssetImportException.Refusing(path, () => ReadBody(fs, path, header), ".blixmesh");
    }

    private static BlixMeshFile ReadBody(Stream fs, string path, CookedHeader header)
    {
        using var br = new BinaryReader(fs);

        var nodeCount = br.ReadInt32();
        if (nodeCount is < 0 or > 1_000_000) throw new InvalidDataException($"'{path}' has invalid nodeCount {nodeCount}.");
        var nodes = new BlixMeshNode[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            var name = BlixMeshBinary.ReadString(br);
            var parent = br.ReadInt32();

            // Checked where the file and the offending node can both be named: a parent must already
            // have been read, or a consumer composing world matrices in one forward pass reads a
            // transform that does not exist yet.
            if (parent < -1 || parent >= i)
            {
                throw new InvalidDataException(
                    $"{path}: node {i} ('{name}') has parent {parent}; expected -1 or an index below {i}.");
            }

            nodes[i] = new BlixMeshNode(name, parent, BlixMeshBinary.ReadMatrix(br), br.ReadInt32(), br.ReadInt32());
        }

        var meshCount = br.ReadInt32();
        if (meshCount is < 0 or > 1_000_000) throw new InvalidDataException($"'{path}' has invalid meshCount {meshCount}.");
        var meshes = new BlixMeshMesh[meshCount];
        for (var i = 0; i < meshCount; i++)
        {
            var name = BlixMeshBinary.ReadString(br);
            var skinIndex = br.ReadInt32();
            var primitiveCount = br.ReadInt32();
            if (primitiveCount is < 0 or > 1_000_000)
                throw new InvalidDataException($"'{path}' mesh {i} has invalid primitiveCount {primitiveCount}.");
            var primitives = new BlixMeshPrimitive[primitiveCount];
            for (var k = 0; k < primitiveCount; k++) primitives[k] = BlixMeshBinary.ReadPrimitive(br, path);
            meshes[i] = new BlixMeshMesh(name, primitives, skinIndex);
        }

        var materialCount = br.ReadInt32();
        var materials = new BlixMeshMaterial[materialCount];
        for (var i = 0; i < materialCount; i++)
        {
            var nameLen = br.ReadInt32();
            var name = System.Text.Encoding.UTF8.GetString(br.ReadBytes(nameLen));
            var baseColor = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            var baseColorTexCoord = br.ReadInt32();
            var normalTexCoord = br.ReadInt32();
            var normalScale = br.ReadSingle();
            var metallicRoughnessTexCoord = br.ReadInt32();
            var occlusionTexCoord = br.ReadInt32();
            var emissiveTexCoord = br.ReadInt32();
            var metallic = br.ReadSingle();
            var roughness = br.ReadSingle();
            var occlusionStrength = br.ReadSingle();
            var emissive = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            var emissiveStrength = br.ReadSingle();
            var alphaMode = br.ReadByte();
            var alphaCutoff = br.ReadSingle();
            var doubleSided = br.ReadBoolean();
            var transmission = br.ReadSingle();
            materials[i] = new BlixMeshMaterial(
                name, baseColor, baseColorTexCoord,
                normalTexCoord, normalScale, metallicRoughnessTexCoord, occlusionTexCoord, emissiveTexCoord,
                metallic, roughness, occlusionStrength,
                emissive, emissiveStrength, alphaMode, alphaCutoff, doubleSided, transmission,
                BaseColorImage: br.ReadInt32(),
                NormalImage: br.ReadInt32(),
                MetallicRoughnessImage: br.ReadInt32(),
                OcclusionImage: br.ReadInt32(),
                EmissiveImage: br.ReadInt32(),
                Extensions: ReadExtensions(br),
                UvTransforms: BlixMeshBinary.ReadUvTransforms(br));
        }

        var imageCount = br.ReadInt32();
        var images = new BlixMeshImage[imageCount];
        for (var i = 0; i < imageCount; i++)
        {
            var nameLen = br.ReadInt32();
            var name = System.Text.Encoding.UTF8.GetString(br.ReadBytes(nameLen));
            var hash = br.ReadUInt64();
            var resourceLen = br.ReadInt32();
            var resource = System.Text.Encoding.UTF8.GetString(br.ReadBytes(resourceLen));
            var sampler = new BlixMeshSampler(br.ReadInt32(), br.ReadInt32(), br.ReadInt32(), br.ReadInt32());
            images[i] = new BlixMeshImage(name, hash, resource, sampler);
        }

        var skinCount = br.ReadInt32();
        var skins = new BlixMeshSkin[skinCount];
        for (var sk = 0; sk < skinCount; sk++)
        {
        var boneCount = br.ReadInt32();
        var bones = new BlixMeshBone[boneCount];
        for (var i = 0; i < boneCount; i++)
        {
            var nameLen = br.ReadInt32();
            var name = System.Text.Encoding.UTF8.GetString(br.ReadBytes(nameLen));
            var parent = br.ReadInt32();

            // The hierarchy invariant is checked HERE rather than left to Skeleton's constructor,
            // because here the file and the offending bone can both be named. A cooked file that
            // violates it is corrupt or written by something that reordered joints, and either way
            // the useful message says which bone and which parent.
            if (parent < -1 || parent >= i)
            {
                throw new InvalidDataException(
                    $"{path}: bone {i} ('{name}') has parent {parent}; expected -1 or an index below {i}.");
            }

            var inverseBind = BlixMeshBinary.ReadMatrix(br);
            var joint = br.ReadInt32();
            if (joint < 0 || joint >= nodes.Length)
                throw new InvalidDataException($"{path}: bone {i} ('{name}') names joint node {joint}, and there are {nodes.Length}.");
            bones[i] = new BlixMeshBone(name, parent, inverseBind, joint);
        }

        skins[sk] = new BlixMeshSkin(bones);
        }

        var clipCount = br.ReadInt32();
        var clips = new BlixMeshClip[clipCount];
        for (var i = 0; i < clipCount; i++)
        {
            var nameLen = br.ReadInt32();
            var name = System.Text.Encoding.UTF8.GetString(br.ReadBytes(nameLen));
            var trackCount = br.ReadInt32();
            var tracks = new BlixMeshTrack[trackCount];
            for (var t = 0; t < trackCount; t++)
            {
                var node = br.ReadInt32();
                var (translation, translationMode) = BlixMeshBinary.ReadVectorKeys(br);
                var (rotation, rotationMode) = BlixMeshBinary.ReadQuaternionKeys(br);
                var (scale, scaleMode) = BlixMeshBinary.ReadVectorKeys(br);
                tracks[t] = new BlixMeshTrack(node, translation, rotation, scale, translationMode, rotationMode, scaleMode);
            }

            clips[i] = new BlixMeshClip(name, tracks);
        }

        // Every cross-reference, refused by name: a node's mesh and skin, a mesh's skin, a track's node.
        for (var i = 0; i < nodes.Length; i++)
        {
            var n = nodes[i];
            if (n.MeshIndex < -1 || n.MeshIndex >= meshes.Length)
                throw new InvalidDataException($"{path}: node {i} ('{n.Name}') places mesh {n.MeshIndex}, and there are {meshes.Length}.");
            if (n.SkinIndex < -1 || n.SkinIndex >= skins.Length)
                throw new InvalidDataException($"{path}: node {i} ('{n.Name}') names skin {n.SkinIndex}, and there are {skins.Length}.");
            if (n.MeshIndex >= 0 && meshes[n.MeshIndex].SkinIndex != n.SkinIndex)
                throw new InvalidDataException(
                    $"{path}: node {i} ('{n.Name}') places mesh {n.MeshIndex} with skin {n.SkinIndex}, and that mesh's vertices are for skin {meshes[n.MeshIndex].SkinIndex}.");
        }

        foreach (var clip in clips)
        foreach (var track in clip.Tracks)
        {
            if (track.NodeIndex < 0 || track.NodeIndex >= nodes.Length)
                throw new InvalidDataException($"{path}: clip '{clip.Name}' animates node {track.NodeIndex}, and there are {nodes.Length}.");
        }

        var ignoredCount = br.ReadInt32();
        var ignored = new BlixMeshIgnored[ignoredCount];
        for (var i = 0; i < ignoredCount; i++) ignored[i] = new BlixMeshIgnored(BlixMeshBinary.ReadString(br), br.ReadInt32());

        return new BlixMeshFile(nodes, meshes, materials, images, skins, clips, header, ignored);
    }
}
