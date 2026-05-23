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
    public const uint Version1 = 1;
    public const uint LayoutPosition3NormalTexture = 1;

    public const int NoMaterial = -1;
    public const byte IndexFormatU16 = 0;
    public const byte IndexFormatU32 = 1;
}

public sealed record BlixMeshPrimitive(
    string Name,
    int MaterialIndex,
    Bounds3 Bounds,
    int VertexCount,
    byte[] VertexBytes,
    IndexFormat IndexFormat,
    ushort[] Indices16,
    uint[]? Indices32);

public sealed record BlixMeshFile(
    VertexLayout Layout,
    IReadOnlyList<BlixMeshPrimitive> Primitives);

public static class BlixMeshWriter
{
    public static void Write(string path, BlixMeshFile file)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(file);
        // Only one layout is recognised today. Bake the assumption in until
        // a second consumer demands the table; the format already carries
        // a layoutId so adding new layouts is additive.
        if (file.Layout.Stride != VertexPosition3NormalTexture.Layout.Stride)
        {
            throw new ArgumentException(
                $"BlixMesh v1 only supports the VertexPosition3NormalTexture layout (stride {VertexPosition3NormalTexture.Layout.Stride}); got stride {file.Layout.Stride}.",
                nameof(file));
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        bw.Write(BlixMesh.Magic);
        bw.Write(BlixMesh.Version1);
        bw.Write(BlixMesh.LayoutPosition3NormalTexture);
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
            if (p.IndexFormat == IndexFormat.UInt32)
            {
                if (p.Indices32 is null)
                {
                    throw new ArgumentException(
                        $"Primitive '{p.Name}' has IndexFormat=UInt32 but Indices32 is null.",
                        nameof(file));
                }
                bw.Write(BlixMesh.IndexFormatU32);
                bw.Write(p.Indices32.Length);
                bw.Write(MemoryMarshal.AsBytes(p.Indices32.AsSpan()));
            }
            else
            {
                bw.Write(BlixMesh.IndexFormatU16);
                bw.Write(p.Indices16.Length);
                bw.Write(MemoryMarshal.AsBytes(p.Indices16.AsSpan()));
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
        if (version != BlixMesh.Version1)
        {
            throw new InvalidDataException(
                $"'{path}' has unsupported .blixmesh version {version}; expected {BlixMesh.Version1}.");
        }
        var layoutId = br.ReadUInt32();
        if (layoutId != BlixMesh.LayoutPosition3NormalTexture)
        {
            throw new InvalidDataException(
                $"'{path}' uses unrecognised layout id {layoutId}.");
        }
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
            var indexCount = br.ReadInt32();
            ushort[] indices16;
            uint[]? indices32;
            if (indexFormatByte == BlixMesh.IndexFormatU32)
            {
                indices32 = new uint[indexCount];
                var raw = br.ReadBytes(indexCount * 4);
                raw.AsSpan().CopyTo(MemoryMarshal.AsBytes(indices32.AsSpan()));
                indices16 = Array.Empty<ushort>();
            }
            else
            {
                indices32 = null;
                indices16 = new ushort[indexCount];
                var raw = br.ReadBytes(indexCount * 2);
                raw.AsSpan().CopyTo(MemoryMarshal.AsBytes(indices16.AsSpan()));
            }

            primitives[i] = new BlixMeshPrimitive(
                Name: name,
                MaterialIndex: materialIndex,
                Bounds: bounds,
                VertexCount: vertexCount,
                VertexBytes: vertexBytes,
                IndexFormat: indexFormatByte == BlixMesh.IndexFormatU32 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                Indices16: indices16,
                Indices32: indices32);
        }

        return new BlixMeshFile(VertexPosition3NormalTexture.Layout, primitives);
    }
}
