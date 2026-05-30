using System.Numerics;
using System.Runtime.InteropServices;
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
// File layout (little-endian), v1:
//
//   offset  size    field
//   ---------------------------------
//   0       4       magic        = "BLXM"
//   4       4       version      = 1
//   8       4       layoutId     (VertexLayout id; currently only 1 = Position3NormalTexture)
//   12      4       primitiveCount
//   --------- 16 bytes (header) ---------
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
    // v2: tangent-layout support + per-primitive LOD index chains (one shared
    // vertex buffer, N index buffers, coarsest selected by distance at
    // runtime). No v1 read path — re-cook to migrate (the cook is fast, and
    // nothing ships v1 files).
    public const uint Version2 = 2;
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

// One LOD level: an index buffer over the primitive's shared vertex buffer.
// Lods[0] is full detail; higher indices are progressively decimated.
public sealed record BlixMeshLod(ushort[]? Indices16, uint[]? Indices32)
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

public sealed record BlixMeshFile(
    VertexLayout Layout,
    IReadOnlyList<BlixMeshPrimitive> Primitives);

public static class BlixMeshWriter
{
    public static void Write(string path, BlixMeshFile file)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(file);
        var layoutId = BlixMesh.LayoutIdForStride(file.Layout.Stride);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        bw.Write(BlixMesh.Magic);
        bw.Write(BlixMesh.Version2);
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
                    bw.Write(MemoryMarshal.AsBytes(lod.Indices32.AsSpan()));
                }
                else
                {
                    if (lod.Indices16 is null)
                        throw new ArgumentException($"Primitive '{p.Name}' LOD has IndexFormat=UInt16 but Indices16 is null.", nameof(file));
                    bw.Write(lod.Indices16.Length);
                    bw.Write(MemoryMarshal.AsBytes(lod.Indices16.AsSpan()));
                }
            }
        }
    }
}

public static class BlixMeshReader
{
    public static BlixMeshFile Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);

        var magic = br.ReadUInt32();
        if (magic != BlixMesh.Magic)
        {
            throw new InvalidDataException(
                $"'{path}' is not a .blixmesh file (magic mismatch: got 0x{magic:X8}).");
        }
        var version = br.ReadUInt32();
        if (version != BlixMesh.Version2)
        {
            throw new InvalidDataException(
                $"'{path}' has unsupported .blixmesh version {version}; expected {BlixMesh.Version2}. Re-run blix-cook mesh.");
        }
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
                if (isU32)
                {
                    var indices32 = new uint[indexCount];
                    br.ReadBytes(indexCount * 4).AsSpan().CopyTo(MemoryMarshal.AsBytes(indices32.AsSpan()));
                    lods[l] = new BlixMeshLod(null, indices32);
                }
                else
                {
                    var indices16 = new ushort[indexCount];
                    br.ReadBytes(indexCount * 2).AsSpan().CopyTo(MemoryMarshal.AsBytes(indices16.AsSpan()));
                    lods[l] = new BlixMeshLod(indices16, null);
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

        return new BlixMeshFile(layout, primitives);
    }
}
