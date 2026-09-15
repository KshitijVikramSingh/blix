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
// No offset/length table for primitives -- the runtime always walks all
// primitives in submission order anyway, and a sequential read avoids the
// "seek per primitive" cache miss the table would introduce.
//
// Materials are NOT cooked here. The runtime still parses the sibling
// .gltf for material descriptors (factors, texture refs, alpha mode) via
// SharpGLTF; .blixmesh just replaces the slow part (buffer interpretation +
// vertex packing). materialIndex links a cooked primitive back to a glTF
// LogicalMaterial.
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
    public const uint Version4 = 4;
    public const uint LayoutPosition3NormalTexture = 1;        // 32-byte
    public const uint LayoutPosition3NormalTangentTexture = 2; // 48-byte

    public const int NoMaterial = -1;
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
public sealed record BlixMeshFile(
    VertexLayout Layout,
    IReadOnlyList<BlixMeshPrimitive> Primitives,
    CookedHeader? Cooked = null);

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
        CookPreamble.Write(fs, BlixMesh.Magic, BlixMesh.Version4, stamp);
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
        var header = CookPreamble.Read(fs, path).Require(BlixMesh.Magic, BlixMesh.Version4, path, ".blixmesh");
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

        return new BlixMeshFile(layout, primitives, header);
    }
}
