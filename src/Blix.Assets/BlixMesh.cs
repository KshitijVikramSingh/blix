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
//   Then, after the last primitive, the material table; then the image table:
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
    public const uint Version6 = 6;
    public const uint LayoutPosition3NormalTexture = 1;        // 32-byte
    public const uint LayoutPosition3NormalTangentTexture = 2; // 48-byte

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

    // Layout id ↔ stride. The only two layouts the cook emits.
    public static uint LayoutIdForStride(int stride) => stride switch
    {
        32 => LayoutPosition3NormalTexture,
        48 => LayoutPosition3NormalTangentTexture,
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
    int EmissiveImage = BlixMesh.NoImage);

public sealed record BlixMeshPrimitive(
    string Name,
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
    VertexLayout Layout,
    IReadOnlyList<BlixMeshPrimitive> Primitives,
    IReadOnlyList<BlixMeshMaterial>? Materials = null,
    IReadOnlyList<BlixMeshImage>? Images = null,
    CookedHeader? Cooked = null)
{
    /// <summary>Never null: a file with no material table reads as an empty one.</summary>
    public IReadOnlyList<BlixMeshMaterial> MaterialTable => Materials ?? Array.Empty<BlixMeshMaterial>();

    /// <summary>Never null: a file with no image table reads as an empty one.</summary>
    public IReadOnlyList<BlixMeshImage> ImageTable => Images ?? Array.Empty<BlixMeshImage>();
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
        var layoutId = BlixMesh.LayoutIdForStride(file.Layout.Stride);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        CookPreamble.Write(fs, BlixMesh.Magic, BlixMesh.Version6, stamp);
        using var bw = new BinaryWriter(fs);

        bw.Write(layoutId);
        bw.Write(file.Primitives.Count);

        foreach (var p in file.Primitives)
        {
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(p.Name);
            bw.Write(nameBytes.Length);
            bw.Write(nameBytes);
            bw.Write(p.MaterialIndex);
            bw.Write(p.Bounds.Min.X); bw.Write(p.Bounds.Min.Y); bw.Write(p.Bounds.Min.Z);
            bw.Write(p.Bounds.Max.X); bw.Write(p.Bounds.Max.Y); bw.Write(p.Bounds.Max.Z);
            bw.Write(p.VertexCount);
            bw.Write(p.VertexBytes.Length);
            bw.Write(p.VertexBytes);
            bw.Write((byte)(p.IndexFormat == IndexFormat.UInt32 ? BlixMesh.IndexFormatU32 : BlixMesh.IndexFormatU16));
            if (p.Lods is null || p.Lods.Count == 0)
            {
                throw new ArgumentException($"Primitive '{p.Name}' has no LOD levels.", nameof(file));
            }
            bw.Write(p.Lods.Count);
            foreach (var lod in p.Lods)
            {
                if (p.IndexFormat == IndexFormat.UInt32)
                {
                    if (lod.Indices32 is null)
                        throw new ArgumentException($"Primitive '{p.Name}' LOD has IndexFormat=UInt32 but Indices32 is null.", nameof(file));
                    bw.Write(lod.Indices32.Length);
                    bw.Write(lod.Error);
                    bw.Write(MemoryMarshal.AsBytes(lod.Indices32.AsSpan()));
                }
                else
                {
                    if (lod.Indices16 is null)
                        throw new ArgumentException($"Primitive '{p.Name}' LOD has IndexFormat=UInt16 but Indices16 is null.", nameof(file));
                    bw.Write(lod.Indices16.Length);
                    bw.Write(lod.Error);
                    bw.Write(MemoryMarshal.AsBytes(lod.Indices16.AsSpan()));
                }
            }
        }

        // The material table, after every primitive. Appended rather than placed up front so that a
        // reader walking primitives sequentially — which is what the runtime does — keeps doing
        // exactly that, and so the one structure whose size depends on the source's material count
        // sits where growing it moves nothing else.
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
    }
}

public static class BlixMeshReader
{
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
        var header = CookPreamble.Read(fs, path).Require(BlixMesh.Magic, BlixMesh.Version6, path, ".blixmesh");
        return AssetImportException.Refusing(path, () => ReadBody(fs, path, header), ".blixmesh");
    }

    private static BlixMeshFile ReadBody(Stream fs, string path, CookedHeader header)
    {
        using var br = new BinaryReader(fs);

        var layoutId = br.ReadUInt32();
        var layout = layoutId switch
        {
            BlixMesh.LayoutPosition3NormalTexture => VertexPosition3NormalTexture.Layout,
            BlixMesh.LayoutPosition3NormalTangentTexture => VertexPosition3NormalTangentTexture.Layout,
            _ => throw new InvalidDataException($"'{path}' uses unrecognised layout id {layoutId}."),
        };
        var primitiveCount = br.ReadInt32();
        if (primitiveCount < 0)
        {
            throw new InvalidDataException(
                $"'{path}' has invalid primitiveCount {primitiveCount}.");
        }

        var primitives = new BlixMeshPrimitive[primitiveCount];
        for (var i = 0; i < primitiveCount; i++)
        {
            var nameLen = br.ReadInt32();
            var nameBytes = br.ReadBytes(nameLen);
            var name = System.Text.Encoding.UTF8.GetString(nameBytes);
            var materialIndex = br.ReadInt32();
            var minX = br.ReadSingle(); var minY = br.ReadSingle(); var minZ = br.ReadSingle();
            var maxX = br.ReadSingle(); var maxY = br.ReadSingle(); var maxZ = br.ReadSingle();
            var bounds = new Bounds3(new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ));
            var vertexCount = br.ReadInt32();
            var vertexBytesLen = br.ReadInt32();
            var vertexBytes = br.ReadBytes(vertexBytesLen);
            var indexFormatByte = br.ReadByte();
            var isU32 = indexFormatByte == BlixMesh.IndexFormatU32;
            var lodCount = br.ReadInt32();
            if (lodCount < 1)
            {
                throw new InvalidDataException($"'{path}' primitive '{name}' has invalid LOD count {lodCount}.");
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

            primitives[i] = new BlixMeshPrimitive(
                Name: name,
                MaterialIndex: materialIndex,
                Bounds: bounds,
                VertexCount: vertexCount,
                VertexBytes: vertexBytes,
                IndexFormat: isU32 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                Lods: lods);
        }

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
                EmissiveImage: br.ReadInt32());
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

        return new BlixMeshFile(layout, primitives, materials, images, header);
    }
}
