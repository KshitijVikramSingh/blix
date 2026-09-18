using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Cooked;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Assets;

// Engine-native binary mesh container. Cooked from .gltf (or .glb) at
// offline cook time; loaded at runtime with no JSON parse + no buffer
// interpretation -- just a header read + a memcpy of each primitive's
// packed vertex + index bytes. Replaces the ~1-5 seconds SharpGLTF's
// ModelRoot.Load + per-accessor interpretation otherwise burns for big
// scenes like Khronos Sponza Modern.
//
// File layout (little-endian), v4:
//
//   The shared Blix cooked preamble first — see Blix.Cooked/CookPreamble.cs for
//   its fields. It carries the magic, the version, which recipe produced this
//   file, what it was cooked from, and the settings used, and it tells a reader
//   where this header starts. Then:
//
//   offset  size    field
//   ---------------------------------
//   0       4       layoutId     (VertexLayout id; 1 = Position3NormalTexture, 2 = +Tangent)
//   4       4       primitiveCount
//   --------- 8 bytes (format header) ---------
//   For each primitive, sequentially:
//     nameLen[4]
//     name[nameLen]    UTF-8 (no NUL terminator)
//     materialIndex[4] glTF logical material index; -1 for no material
//     bounds[24]       6 floats: minX, minY, minZ, maxX, maxY, maxZ
//     vertexCount[4]
//     vertexBytesLen[4]
//     vertexBytes[vertexBytesLen]
//     indexFormat[1]   0 = UInt16, 1 = UInt32
//     indexCount[4]
//     indexBytes[indexCount * 2 or 4]
//
//   Then, after the last primitive, the material table; then the image table;
//   then (v7) the bone table and the clip table:
//     boneCount[4]
//     For each bone: nameLen[4] name[..] parentIndex[4] inverseBindPose[64]
//     clipCount[4]
//     For each clip: nameLen[4] name[..] trackCount[4]
//       For each track: boneIndex[4], then three channels in order
//         (translation vec3, rotation quat, scale vec3), each:
//           keyCount[4]  -- 0 means the channel is absent
//           per key: time[4] then 12 bytes (vec3) or 16 bytes (quat)
//
//   Then (v8) the node table:
//     nodeCount[4]
//     For each node: nameLen[4] name[..] parentIndex[4] localTransform[64]
//
//   ── Why the vertices stay world-baked ───────────────────────────────────
//   The cook bakes each node's world transform into its vertices, which is a
//   large part of what the flat load path buys. The studio's model view needs
//   the authored HIERARCHY — names, parents, pivots — and a table gives it
//   that without unbaking anything: it draws parts that are already in world
//   space and reads the table for structure.
//
//   A primitive's nodeIndex is what makes the baking reversible. A consumer
//   that genuinely needs local-space geometry composes the node's world matrix
//   from the table and applies its inverse — exactly recovering what the cook
//   folded in. That cost is paid by the consumer that wants it, when it wants
//   it, rather than by every load: cooking local vertices instead would have
//   made the flat path transform eight million vertices on every Sponza load.
//
//   A bone's ParentIndex is -1 or strictly less than its own index, which
//   Skeleton's constructor enforces; the writer does not reorder, so a file
//   that violates it is refused on read where it can be named.
//
//     imageCount[4]
//     For each image, sequentially:
//       nameLen[4] name[nameLen]         UTF-8, for a person and for diagnostics
//       contentHash[8]                   u64 of the SOURCE image bytes
//       resourceLen[4] resource[...]     UTF-8 relative reference, forward slashes
//
//     materialCount[4]
//     For each material, sequentially:
//       nameLen[4] name[nameLen]      UTF-8
//       baseColorFactor[16]           4 floats, linear
//       baseColorTexCoord[4]          which TEXCOORD set base colour samples
//       metallicFactor[4] roughnessFactor[4]
//       occlusionStrength[4]
//       emissiveFactor[12]            3 floats, linear
//       emissiveStrength[4]           KHR_materials_emissive_strength
//       alphaMode[1]                  0 OPAQUE, 1 MASK, 2 BLEND
//       alphaCutoff[4]
//       doubleSided[1]
//       transmissionFactor[4]         KHR_materials_transmission
//       baseColorImage[4] normalImage[4] metallicRoughnessImage[4]
//       occlusionImage[4] emissiveImage[4]
//                                     source IMAGE index per channel, -1 = none
//
// No offset/length table for primitives -- the runtime always walks all
// primitives in submission order anyway, and a sequential read avoids the
// "seek per primitive" cache miss the table would introduce.
//
// Materials ARE cooked here as of v5 -- every property except the image
// BYTES. That split is the whole of stage K-F, and it is the split the arc
// had collapsed: factors, alpha mode, alpha cutoff, double-sidedness, names
// and texCoord sets are properties of a material and cook like any other
// value, while the pixels are a separate artifact whose ADDRESSING (one file
// per source? an atlas? an array? a streaming pool?) is a project's decision
// and not this format's. Parking the first behind the second meant a cooked
// mesh could not describe its own surface for want of a decision about
// texture packing.
//
// So each channel records the source IMAGE INDEX rather than pixels, and the
// loader resolves those against images it decodes from the source. The source
// therefore stays required -- but required for ONE named reason instead of
// for everything, which is what CookedFlags.SourceRequiredForImagesOnly says.
//
// materialIndex on a primitive indexes THIS table, and the table is written in
// the source's own material order, so the number means what it always meant.
//
// ── The image table (v6), and the three things it separates ────────────────
//
// A material channel's image field is a ROW in this file's own image table. It
// used to be a glTF logical image index, which is a number that means nothing
// without the glTF open -- so a "cooked" mesh still pinned its source for the
// sole purpose of asking where the pixels were.
//
// Three things were tangled in that one number, and the table exists to pull
// them apart:
//
//   IDENTITY  which image is this?      -> Name + ContentHash, stable across
//                                          renames, and equal for two copies of
//                                          the same bytes.
//   LOCATION  where are its pixels?     -> Resource, a relative reference the
//                                          RECIPE writes.
//   GROUPING  how are many images       -> not here at all. It is a consequence
//             packed into artifacts?       of what a recipe writes into Resource.
//
// That last line is the point. Before this, "one cooked artifact per source
// image" was not a decision anyone had written down -- it was implied by a
// loader rule that swapped a source URI's extension, which is a grouping policy
// wearing a path's clothing. Cooking materials was then parked behind "textures
// need a grouping decision" while the tree quietly had one.
//
// Now the shipped recipe writes 1:1 siblings, as before, but as DATA. A project
// that wants atlases, arrays, shared palettes or a streaming pool writes rows
// naming whatever its own resolver understands; the engine's default resolver
// knows only "a relative path to a .blixtex" and never has to learn the rest.
//
// An EMBEDDED image is not a special case here. The cook extracts it, writes a
// .blixtex under `<stem>.textures/`, and fills in a row that looks like every
// other row. Embedded and external stop differing above the recipe.
public static class BlixMesh
{
    public const uint Magic = 0x4D584C42; // "BLXM" little-endian

    /// <summary>
    /// The recipe id the shipped mesh cook stamps. A project cooking its own meshes to this format
    /// stamps its own, which is what makes "who made this file" answerable.
    /// </summary>
    public const string ShippedRecipe = "gmsh";
    // v2: tangent-layout support + per-primitive LOD index chains (one shared
    // vertex buffer, N index buffers, coarsest selected by distance at runtime).
    // v3: each LOD level also carries its world-space geometric error (a float
    // after indexCount) so the runtime can do screen-space-error selection
    // instead of a magic metres-per-level distance. No back-read path — re-cook
    // to migrate (the cook is fast, and nothing ships older files).
    // v4: the shared cooked preamble replaces the private magic+version pair, so
    // provenance and settings travel with the file and a tool can read them
    // without knowing this format at all.
    // v5: a material table follows the primitives -- every material property
    // except image bytes (see the header note). No back-read path, by the same
    // rule as v3: re-cook to migrate, which the build does by itself because the
    // recipe assembly is one of the cook target's Inputs.
    // v6: an IMAGE TABLE follows the materials, and a material channel's image
    // field becomes a row in it rather than a glTF logical image index. That is
    // what lets a cooked mesh be loaded with no source file present at all --
    // see the header note on identity, location and grouping.
    // v7: a BONE table and a CLIP table follow the images, and layoutId 3 is the skinned vertex.
    // A rigged glTF had no cooked form at all — `blix check --cooked` said so on every one of them
    // — and that was the last category of asset in this tree with no fast path.
    //
    // <b>Two more tables rather than a new format, which the plan had called for.</b> Its reasoning
    // was that a rig "needs the skinned vertices, the skeleton, and the clips, which is a new format
    // rather than a fifth column in this one". But this format already carries materials and images,
    // neither of which is mesh data either, and both arrived as tables appended after the
    // primitives. A skeleton and its clips are the same move. Keeping one format also keeps one
    // RECIPE per source extension, which matters: BlixRecipes.For refuses to guess when two recipes
    // accept the same extension, so a second .gltf recipe would have made `blix cook` unable to
    // choose between them.
    // v8: a NODE table, and every primitive names the node it came from. Vertices stay
    // world-baked — the cook's whole value for the flat path — and the table is what lets a
    // consumer that wants the authored hierarchy have it anyway. See the header note.
    // v9: every material carries the KHR_materials_* block. The importer was reading two of the
    // thirteen extensions SharpGLTF surfaces and dropping eleven at the boundary, so a cooked file
    // could not carry what the engine had begun to read. Same rule as every bump before it: no
    // back-read, re-cook to migrate, which the build does by itself.
    public const uint Version9 = 9;
    public const uint LayoutPosition3NormalTexture = 1;        // 32-byte
    public const uint LayoutPosition3NormalTangentTexture = 2; // 48-byte
    public const uint LayoutPosition3NormalTextureSkin4Tangent = 3; // 80-byte, rigged
    // The two colour-carrying static layouts. They reach this format through a RIG's attachments
    // and static parts, which are built by the static mesh path with includeColour — so a format
    // that knew only the skinned layout could store a rig's skeleton and not its cape.
    public const uint LayoutPosition3NormalTextureColor = 4;      // 36-byte
    public const uint LayoutPosition3NormalTexture2Color = 5;     // 44-byte, two UV sets

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

/// <summary>A cooked material: every glTF material property except the image bytes.</summary>
/// <remarks>
/// <b>The image channels are source IMAGE indices, not paths and not pixels.</b> An index is what
/// survives the two things a path does not — a container with its images embedded, where there is no
/// path to write down, and a future in which the pixels move into some other artifact, where a path
/// would have to be rewritten in every cooked file. It is also what the loader already keys its
/// decoded-texture cache by, so resolving one costs a dictionary lookup.
/// <para>
/// -1 means the channel has no texture, which is different from an index that resolves to nothing.
/// </para>
/// </remarks>
/// <summary>One image a material references: what it is, and where its pixels were put.</summary>
/// <remarks>
/// <b><paramref name="Resource"/> is written by the recipe, not derived by the loader.</b> That is
/// the whole of how grouping stays the consumer's: the shipped recipe writes one cooked artifact per
/// source image and records its relative path, and a recipe that packs differently records something
/// its own resolver understands. Nothing in the engine infers a location from a name.
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
public sealed record BlixMeshImage(string Name, ulong ContentHash, string Resource);

public sealed record BlixMeshMaterial(
    string Name,
    Vector4 BaseColorFactor,
    int BaseColorTexCoord,
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

    /// <summary>Every <c>KHR_materials_*</c> property, as cooked. Null on a file older than v9.</summary>
    BlixMaterialExtensions? Extensions = null)
{
    /// <summary>The extensions, never null — an absent block reads as every spec default.</summary>
    public BlixMaterialExtensions Ext => Extensions ?? BlixMaterialExtensions.None;
}

/// <summary>
/// The cooked mirror of <c>GltfMaterialExtensions</c>: every <c>KHR_materials_*</c> property.
/// </summary>
/// <remarks>
/// <para>
/// A format type rather than the engine one, for the same reason <see cref="BlixMeshMaterial"/> is
/// not <c>GltfMaterial</c>: the runtime types live in <c>Blix</c>, which references this assembly.
/// The visible difference is textures — the engine carries a resolved <c>GltfTexture</c>, a file
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
/// not <c>GltfMaterial</c>: the runtime types live in <c>Blix</c>, which references this assembly,
/// and a format that reached back for them would invert that. The conversion is one constructor
/// call at each end, and it is what keeps the file readable by a tool that does not load the engine.
/// </remarks>
public sealed record BlixMeshBone(string Name, int ParentIndex, Matrix4x4 InverseBindPose);

/// <summary>A translation or scale key.</summary>
public readonly record struct BlixMeshVectorKey(float Time, Vector3 Value);

/// <summary>A rotation key.</summary>
public readonly record struct BlixMeshQuaternionKey(float Time, Quaternion Value);

/// <summary>One bone's animation across a clip. An empty channel array means that channel is absent.</summary>
/// <remarks>
/// <b>Absent and empty are the same thing here, deliberately.</b> glTF gives a channel keys or does
/// not give it at all; a channel with zero keys has no meaning either way, so one representation
/// covers both and the reader needs no presence flag per channel.
/// </remarks>
public sealed record BlixMeshTrack(
    int BoneIndex,
    BlixMeshVectorKey[] Translation,
    BlixMeshQuaternionKey[] Rotation,
    BlixMeshVectorKey[] Scale);

/// <summary>A named animation. Duration is derived from the keys, never stored.</summary>
/// <remarks>
/// Derived rather than stored because a stored duration is a second source of truth that can
/// disagree with the keys — and the runtime <c>AnimationClip</c> already computes it from them.
/// </remarks>
public sealed record BlixMeshClip(string Name, BlixMeshTrack[] Tracks);

/// <summary>One skin: its bones and the transform its mesh node sat under.</summary>
/// <remarks>
/// <b>A TABLE of skins, because a file can hold several, and this tree has one that does.</b> The
/// first cut of this format carried a single skeleton — until <c>tank.glb</c> was measured and found
/// to declare three. The rigged importer already knew: every primitive carries a <c>SkinIndex</c>,
/// and its comment records that choosing one skin and discarding the rest was a real bug once. A
/// cooked form that could hold only one would have reintroduced it silently, in a file, where it is
/// far harder to see.
/// <para>
/// <paramref name="MeshNodeTransform"/> travels with the skin because it belongs to it: most
/// authored characters put their axis correction on an ancestor node rather than per-vertex, and a
/// rig imported without it comes out lying on its side.
/// </para>
/// </remarks>
public sealed record BlixMeshSkin(BlixMeshBone[] Bones, Matrix4x4 MeshNodeTransform);

/// <summary>One node of the authored hierarchy — what a primitive was called and where it sat.</summary>
/// <remarks>
/// Transform-only nodes are kept, not pruned: a parent that carries no geometry is still what a
/// child's transform is relative to, and dropping it would break the composition it exists for.
/// </remarks>
public sealed record BlixMeshNode(string Name, int ParentIndex, Matrix4x4 LocalTransform);

/// <summary>Geometry parented to a bone — a cape, a knife in a hand slot.</summary>
/// <remarks>
/// Its primitives carry a STATIC layout while the skinned ones beside them carry an 80-byte skinned
/// layout, which is the whole reason a primitive owns its layout rather than the file.
/// </remarks>
public sealed record BlixMeshAttachment(
    string Name,
    string JointName,
    int JointIndex,
    int SkinIndex,
    Matrix4x4 LocalTransform,
    BlixMeshPrimitive[] Primitives);

/// <summary>Unskinned geometry that ships inside a rigged file, at its own world transform.</summary>
public sealed record BlixMeshStaticPart(
    string Name,
    Matrix4x4 WorldTransform,
    BlixMeshPrimitive[] Primitives);

public sealed record BlixMeshPrimitive(
    string Name,
    /// <summary>
    /// This primitive's own vertex layout.
    /// </summary>
    /// <remarks>
    /// <b>Per primitive, not per file, and that migration is what let a rig cook whole.</b> One
    /// layout per file was true while a file held one kind of geometry. A rigged glTF does not: its
    /// skinned primitives are 80-byte, while the ATTACHMENTS hanging off its bones — Rogue's cape
    /// and its two knives — are built through the static path and carry a static layout. With one
    /// layout per file, cooking a rig meant dropping them, and dropping them meant a cooked Rogue
    /// that draws a character with no cape. Refusing to cook such files was the first answer here,
    /// and it left exactly the half-cooked category this arc exists to remove.
    /// </remarks>
    VertexLayout Layout,
    int MaterialIndex,
    Bounds3 Bounds,
    int VertexCount,
    byte[] VertexBytes,
    IndexFormat IndexFormat,
    IReadOnlyList<BlixMeshLod> Lods,
    /// <summary>Which skin drives this primitive; 0 for a static mesh and for a single-skin rig.</summary>
    int SkinIndex = 0,
    /// <summary>
    /// Which node of the file's table this came from, or -1 when the file has no hierarchy.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes the cook's world-baking reversible.</b> The vertices are already in
    /// world space; a consumer that needs them in the node's own space composes that node's world
    /// matrix from the table and applies its inverse.
    /// </remarks>
    int NodeIndex = -1);

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
public sealed record BlixMeshFile(
    IReadOnlyList<BlixMeshPrimitive> Primitives,
    IReadOnlyList<BlixMeshMaterial>? Materials = null,
    IReadOnlyList<BlixMeshImage>? Images = null,
    IReadOnlyList<BlixMeshSkin>? Skins = null,
    IReadOnlyList<BlixMeshClip>? Clips = null,
    IReadOnlyList<BlixMeshAttachment>? Attachments = null,
    IReadOnlyList<BlixMeshStaticPart>? StaticParts = null,
    IReadOnlyList<BlixMeshNode>? Nodes = null,
    CookedHeader? Cooked = null)
{
    /// <summary>Never null: a file with no material table reads as an empty one.</summary>
    public IReadOnlyList<BlixMeshMaterial> MaterialTable => Materials ?? Array.Empty<BlixMeshMaterial>();

    /// <summary>Never null: a file with no image table reads as an empty one.</summary>
    public IReadOnlyList<BlixMeshImage> ImageTable => Images ?? Array.Empty<BlixMeshImage>();

    /// <summary>Never null: a static mesh reads as no skins.</summary>
    public IReadOnlyList<BlixMeshSkin> SkinTable => Skins ?? Array.Empty<BlixMeshSkin>();

    /// <summary>Never null: a rig with no animations reads as no clips.</summary>
    public IReadOnlyList<BlixMeshClip> ClipTable => Clips ?? Array.Empty<BlixMeshClip>();

    /// <summary>Never null.</summary>
    public IReadOnlyList<BlixMeshAttachment> AttachmentTable => Attachments ?? Array.Empty<BlixMeshAttachment>();

    /// <summary>Never null.</summary>
    public IReadOnlyList<BlixMeshStaticPart> StaticPartTable => StaticParts ?? Array.Empty<BlixMeshStaticPart>();

    /// <summary>Never null: a file cooked before the node table, or with no hierarchy, reads as none.</summary>
    public IReadOnlyList<BlixMeshNode> NodeTable => Nodes ?? Array.Empty<BlixMeshNode>();

    /// <summary>True when this file carries a skeleton, and therefore skinned vertices.</summary>
    public bool IsRigged => SkinTable.Count > 0;
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
        var skinIndex = br.ReadInt32();
        var nodeIndex = br.ReadInt32();
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
            isU32 ? IndexFormat.UInt32 : IndexFormat.UInt16, lods, skinIndex, nodeIndex);
    }

    internal static Matrix4x4 ReadMatrix(BinaryReader br) => new(
        br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
        br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
        br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
        br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

    internal static BlixMeshVectorKey[] ReadVectorKeys(BinaryReader br)
    {
        var n = br.ReadInt32();
        var keys = new BlixMeshVectorKey[n];
        for (var i = 0; i < n; i++)
        {
            keys[i] = new BlixMeshVectorKey(
                br.ReadSingle(), new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()));
        }

        return keys;
    }

    internal static BlixMeshQuaternionKey[] ReadQuaternionKeys(BinaryReader br)
    {
        var n = br.ReadInt32();
        var keys = new BlixMeshQuaternionKey[n];
        for (var i = 0; i < n; i++)
        {
            keys[i] = new BlixMeshQuaternionKey(
                br.ReadSingle(),
                new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()));
        }

        return keys;
    }
}

public static class BlixMeshWriter
{
    /// <param name="stamp">
    /// Who cooked this, from what, with which settings. <b>Required, and that is the point</b> — a
    /// recipe never writes bytes itself, so making this a parameter is what makes an unstamped
    /// cooked file impossible to produce rather than merely discouraged.
    /// </param>
    public static void Write(string path, BlixMeshFile file, in CookStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(file);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        CookPreamble.Write(fs, BlixMesh.Magic, BlixMesh.Version9, stamp);
        using var bw = new BinaryWriter(fs);

        bw.Write(file.Primitives.Count);
        foreach (var p in file.Primitives) WritePrimitive(bw, p);

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
        }

        var skins = file.SkinTable;
        bw.Write(skins.Count);
        foreach (var skin in skins)
        {
            WriteMatrix(bw, skin.MeshNodeTransform);
            bw.Write(skin.Bones.Length);
            foreach (var bone in skin.Bones)
            {
                var nameBytes = System.Text.Encoding.UTF8.GetBytes(bone.Name);
                bw.Write(nameBytes.Length);
                bw.Write(nameBytes);
                bw.Write(bone.ParentIndex);
                WriteMatrix(bw, bone.InverseBindPose);
            }
        }

        var clips = file.ClipTable;
        bw.Write(clips.Count);
        foreach (var clip in clips)
        {
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(clip.Name);
            bw.Write(nameBytes.Length);
            bw.Write(nameBytes);
            bw.Write(clip.Tracks.Length);
            foreach (var track in clip.Tracks)
            {
                bw.Write(track.BoneIndex);
                WriteVectorKeys(bw, track.Translation);
                WriteQuaternionKeys(bw, track.Rotation);
                WriteVectorKeys(bw, track.Scale);
            }
        }

        var attachments = file.AttachmentTable;
        bw.Write(attachments.Count);
        foreach (var a in attachments)
        {
            WriteString(bw, a.Name);
            WriteString(bw, a.JointName);
            bw.Write(a.JointIndex);
            bw.Write(a.SkinIndex);
            WriteMatrix(bw, a.LocalTransform);
            bw.Write(a.Primitives.Length);
            foreach (var p in a.Primitives) WritePrimitive(bw, p);
        }

        var staticParts = file.StaticPartTable;
        bw.Write(staticParts.Count);
        foreach (var sp in staticParts)
        {
            WriteString(bw, sp.Name);
            WriteMatrix(bw, sp.WorldTransform);
            bw.Write(sp.Primitives.Length);
            foreach (var p in sp.Primitives) WritePrimitive(bw, p);
        }

        var nodes = file.NodeTable;
        bw.Write(nodes.Count);
        foreach (var node in nodes)
        {
            WriteString(bw, node.Name);
            bw.Write(node.ParentIndex);
            WriteMatrix(bw, node.LocalTransform);
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
        bw.Write(p.SkinIndex);
        bw.Write(p.NodeIndex);
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

    private static void WriteVectorKeys(BinaryWriter bw, BlixMeshVectorKey[] keys)
    {
        bw.Write(keys.Length);
        foreach (var k in keys)
        {
            bw.Write(k.Time);
            bw.Write(k.Value.X); bw.Write(k.Value.Y); bw.Write(k.Value.Z);
        }
    }

    private static void WriteQuaternionKeys(BinaryWriter bw, BlixMeshQuaternionKey[] keys)
    {
        bw.Write(keys.Length);
        foreach (var k in keys)
        {
            bw.Write(k.Time);
            bw.Write(k.Value.X); bw.Write(k.Value.Y); bw.Write(k.Value.Z); bw.Write(k.Value.W);
        }
    }
}

public static class BlixMeshReader
{
    /// <summary>Reads the v9 <c>KHR_materials_*</c> block, in the order the writer emits it.</summary>
    /// <remarks>
    /// Positional and exact. There is no length prefix and no field tags, because the format does not
    /// do optional data — it bumps its version and re-cooks, as it has from v2 to v9 — and a reader
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
        var header = CookPreamble.Read(fs, path).Require(BlixMesh.Magic, BlixMesh.Version9, path, ".blixmesh");
        return AssetImportException.Refusing(path, () => ReadBody(fs, path, header), ".blixmesh");
    }

    private static BlixMeshFile ReadBody(Stream fs, string path, CookedHeader header)
    {
        using var br = new BinaryReader(fs);

        var primitiveCount = br.ReadInt32();
        if (primitiveCount is < 0 or > 1_000_000)
        {
            throw new InvalidDataException(
                $"'{path}' has invalid primitiveCount {primitiveCount}.");
        }

        var primitives = new BlixMeshPrimitive[primitiveCount];
        for (var i = 0; i < primitiveCount; i++) primitives[i] = BlixMeshBinary.ReadPrimitive(br, path);

        var materialCount = br.ReadInt32();
        var materials = new BlixMeshMaterial[materialCount];
        for (var i = 0; i < materialCount; i++)
        {
            var nameLen = br.ReadInt32();
            var name = System.Text.Encoding.UTF8.GetString(br.ReadBytes(nameLen));
            var baseColor = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            var baseColorTexCoord = br.ReadInt32();
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
                name, baseColor, baseColorTexCoord, metallic, roughness, occlusionStrength,
                emissive, emissiveStrength, alphaMode, alphaCutoff, doubleSided, transmission,
                BaseColorImage: br.ReadInt32(),
                NormalImage: br.ReadInt32(),
                MetallicRoughnessImage: br.ReadInt32(),
                OcclusionImage: br.ReadInt32(),
                EmissiveImage: br.ReadInt32(),
                Extensions: ReadExtensions(br));
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
            images[i] = new BlixMeshImage(name, hash, resource);
        }

        var skinCount = br.ReadInt32();
        var skins = new BlixMeshSkin[skinCount];
        for (var sk = 0; sk < skinCount; sk++)
        {
        var meshNodeTransform = BlixMeshBinary.ReadMatrix(br);
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

            bones[i] = new BlixMeshBone(name, parent, BlixMeshBinary.ReadMatrix(br));
        }

        skins[sk] = new BlixMeshSkin(bones, meshNodeTransform);
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
                tracks[t] = new BlixMeshTrack(
                    br.ReadInt32(), BlixMeshBinary.ReadVectorKeys(br), BlixMeshBinary.ReadQuaternionKeys(br), BlixMeshBinary.ReadVectorKeys(br));
            }

            clips[i] = new BlixMeshClip(name, tracks);
        }

        var attachmentCount = br.ReadInt32();
        var attachments = new BlixMeshAttachment[attachmentCount];
        for (var i = 0; i < attachmentCount; i++)
        {
            var name = BlixMeshBinary.ReadString(br);
            var jointName = BlixMeshBinary.ReadString(br);
            var jointIndex = br.ReadInt32();
            var skinIndex = br.ReadInt32();
            var local = BlixMeshBinary.ReadMatrix(br);
            var count = br.ReadInt32();
            var parts = new BlixMeshPrimitive[count];
            for (var k = 0; k < count; k++) parts[k] = BlixMeshBinary.ReadPrimitive(br, path);
            attachments[i] = new BlixMeshAttachment(name, jointName, jointIndex, skinIndex, local, parts);
        }

        var staticPartCount = br.ReadInt32();
        var staticParts = new BlixMeshStaticPart[staticPartCount];
        for (var i = 0; i < staticPartCount; i++)
        {
            var name = BlixMeshBinary.ReadString(br);
            var world = BlixMeshBinary.ReadMatrix(br);
            var count = br.ReadInt32();
            var parts = new BlixMeshPrimitive[count];
            for (var k = 0; k < count; k++) parts[k] = BlixMeshBinary.ReadPrimitive(br, path);
            staticParts[i] = new BlixMeshStaticPart(name, world, parts);
        }

        var nodeCount = br.ReadInt32();
        var nodes = new BlixMeshNode[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            var name = BlixMeshBinary.ReadString(br);
            var parent = br.ReadInt32();

            // Same invariant as a bone's, checked where the file and the offending node can both be
            // named: a parent must already have been read, or a consumer composing world matrices in
            // one forward pass reads a transform that does not exist yet.
            if (parent < -1 || parent >= i)
            {
                throw new InvalidDataException(
                    $"{path}: node {i} ('{name}') has parent {parent}; expected -1 or an index below {i}.");
            }

            nodes[i] = new BlixMeshNode(name, parent, BlixMeshBinary.ReadMatrix(br));
        }

        return new BlixMeshFile(
            primitives, materials, images, skins, clips, attachments, staticParts, nodes, header);
    }
}
