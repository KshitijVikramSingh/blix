using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Blix.Geometry;

// The scene's hierarchies kept on disk: the build is the geometry's alone, and on Sponza it took 29.5 s of a ~80 s boot
// (2026-10-16). Written once; read back for the same placements -- the same meshes' every position and index, the same
// matrices, the same region budget (Key) -- in the time the file takes to read.
public sealed partial class RayQueryScene
{
    private const string StoreTag = "blix-ray-scene v1";

    /// <summary>What the build depends on, exactly: every placement's matrix, flags and reach, its mesh's every position and
    /// index, which placements share a mesh, and the region budget.</summary>
    public static string Key(IReadOnlyList<Instance> placements, int regionTriangles = DefaultRegionTriangles)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var meshIds = new Dictionary<RayMesh, int>(ReferenceEqualityComparer.Instance);
        Span<float> m = stackalloc float[16];
        Span<int> head = stackalloc int[6];
        foreach (var p in placements)
        {
            var w = p.World;
            m[0] = w.M11; m[1] = w.M12; m[2] = w.M13; m[3] = w.M14; m[4] = w.M21; m[5] = w.M22; m[6] = w.M23; m[7] = w.M24;
            m[8] = w.M31; m[9] = w.M32; m[10] = w.M33; m[11] = w.M34; m[12] = w.M41; m[13] = w.M42; m[14] = w.M43; m[15] = w.M44;
            sha.AppendData(MemoryMarshal.AsBytes(m));
            var fresh = !meshIds.TryGetValue(p.Mesh, out var id);
            if (fresh) meshIds[p.Mesh] = id = meshIds.Count;
            head[0] = id; head[1] = p.Dynamic ? 1 : 0; head[2] = p.Reach is null ? 0 : 1;
            head[3] = p.Mesh.Positions.Length; head[4] = p.Mesh.Indices.Length; head[5] = p.Mesh.Uvs is null ? 0 : 1;
            sha.AppendData(MemoryMarshal.AsBytes(head));
            if (p.Reach is { } r) sha.AppendData(MemoryMarshal.AsBytes(new[] { r.Min, r.Max }.AsSpan()));
            if (!fresh) continue;
            sha.AppendData(MemoryMarshal.AsBytes(p.Mesh.Positions.AsSpan()));
            sha.AppendData(MemoryMarshal.AsBytes(p.Mesh.Indices.AsSpan()));
            if (p.Mesh.Uvs is { } uvs) sha.AppendData(MemoryMarshal.AsBytes(uvs.AsSpan()));
        }
        sha.AppendData(BitConverter.GetBytes(regionTriangles));
        sha.AppendData(System.Text.Encoding.UTF8.GetBytes(StoreTag));
        return Convert.ToHexString(sha.GetHashAndReset())[..24].ToLowerInvariant();
    }

    /// <summary>The built hierarchies, under <paramref name="key"/> (<see cref="Key"/>).</summary>
    public void Write(Stream stream, string key)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(StoreTag);
        w.Write(key);
        var bvhIds = new Dictionary<TriangleBvh, int>(ReferenceEqualityComparer.Instance);
        var bvhs = new List<TriangleBvh>();
        foreach (var e in entries)
            if (bvhIds.TryAdd(e.Bvh, bvhs.Count)) bvhs.Add(e.Bvh);
        w.Write(bvhs.Count);
        foreach (var b in bvhs)
        {
            WriteArray(w, b.Positions);
            WriteArray(w, b.Indices);
            WriteArray(w, b.Nodes);
            WriteArray(w, b.Order);
        }
        w.Write(entries.Length);
        foreach (var e in entries)
        {
            w.Write(bvhIds[e.Bvh]);
            WriteArray(w, new[] { e.WorldToLocal });
            w.Write(e.Placement);
            WriteOptional(w, e.OwnerPlacement);
            WriteOptional(w, e.OwnerTriangle);
            // An instance's uvs are its mesh's (Build): only a region's are its own.
            WriteOptional(w, e.IsRegion ? e.Uvs : null);
        }
        WriteArray(w, Nodes);
        WriteArray(w, Order);
    }

    /// <summary>The hierarchies <see cref="Write"/> kept, for these placements; null when the file is another key's.</summary>
    public static RayQueryScene? Read(Stream stream, IReadOnlyList<Instance> placements, string key)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadString() != StoreTag || r.ReadString() != key) return null;
        var all = placements.ToArray();
        var bvhs = new TriangleBvh[r.ReadInt32()];
        for (var b = 0; b < bvhs.Length; b++)
            bvhs[b] = TriangleBvh.FromParts(ReadArray<Vector3>(r), ReadArray<uint>(r), ReadArray<BvhNode>(r), ReadArray<int>(r));
        var entries = new Entry[r.ReadInt32()];
        for (var e = 0; e < entries.Length; e++)
        {
            var bvh = bvhs[r.ReadInt32()];
            var worldToLocal = ReadArray<Matrix4x4>(r)[0];
            var placement = r.ReadInt32();
            var ownerPlacement = ReadOptional<int>(r);
            var ownerTriangle = ReadOptional<int>(r);
            var uvs = ReadOptional<Vector2>(r) ?? (placement >= 0 ? all[placement].Mesh.Uvs : null);
            entries[e] = new Entry(bvh, worldToLocal, placement, ownerPlacement, ownerTriangle, uvs);
        }
        return new RayQueryScene(all, entries, ReadArray<BvhNode>(r), ReadArray<int>(r));
    }

    private static void WriteArray<T>(BinaryWriter w, T[] values) where T : unmanaged
    {
        w.Write(values.Length);
        w.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static void WriteOptional<T>(BinaryWriter w, T[]? values) where T : unmanaged
    {
        w.Write(values is not null);
        if (values is not null) WriteArray(w, values);
    }

    private static T[] ReadArray<T>(BinaryReader r) where T : unmanaged
    {
        var values = new T[r.ReadInt32()];
        var bytes = MemoryMarshal.AsBytes(values.AsSpan());
        while (bytes.Length > 0)
        {
            var n = r.Read(bytes);
            if (n == 0) throw new EndOfStreamException();
            bytes = bytes[n..];
        }
        return values;
    }

    private static T[]? ReadOptional<T>(BinaryReader r) where T : unmanaged => r.ReadBoolean() ? ReadArray<T>(r) : null;
}
