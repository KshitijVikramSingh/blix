using System.Numerics;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Assets;

public static class MeshDataExtensions
{
    // Build a TriangleMesh3D from this MeshData. Reads the position component from
    // each vertex (first 12 bytes = three floats — the convention every Vertex*
    // packing in Blix.Graphics follows) and indexes them by MeshData.Indices.
    //
    // Optional `worldTransform` applies a column-vector model matrix to each position
    // before packing — turns local-space mesh data into a world-space static collider
    // in one call. Pass null for local-space triangles (typical when the caller will
    // apply the transform later, or when "world" and "local" coincide for this mesh).
    public static TriangleMesh3D ToTriangleMesh(this MeshData data, Matrix4x4? worldTransform = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        var stride = data.Layout.Stride;
        var vertexCount = data.VertexCount;
        var positions = new Vector3[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var offset = i * stride;
            var local = new Vector3(
                BitConverter.ToSingle(data.VertexBytes, offset),
                BitConverter.ToSingle(data.VertexBytes, offset + 4),
                BitConverter.ToSingle(data.VertexBytes, offset + 8));
            positions[i] = worldTransform.HasValue
                ? GraphicsMatrices.TransformPoint(worldTransform.Value, local)
                : local;
        }
        return TriangleMesh3D.FromIndexed(positions, data.Indices);
    }

    // Transform every vertex of a mesh, positions and normals both, returning a new
    // MeshData in the new space. Normals go through the inverse-transpose so a
    // non-uniform scale does not tilt them; positions go through the matrix directly.
    //
    // This is the operation that turns an imported asset into one a game can place:
    // authors put a model wherever the model was convenient, and a game wants it
    // normalised once, at load, rather than compensated for in every instance matrix.
    // Both TankArena and Bulwark grew a private copy of this before it lived here.
    //
    // Bounds are recomputed from the transformed positions. The vertex layout is
    // preserved byte for byte apart from the position and normal components, which are
    // the first 24 bytes of every Vertex* packing in Blix.Graphics.
    public static MeshData Transformed(this MeshData data, Matrix4x4 transform, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var stride = data.Layout.Stride;
        if (stride < 24)
        {
            throw new ArgumentException(
                $"Mesh '{data.Name}' has a {stride}-byte vertex; transforming needs a position and a " +
                "normal in the first 24 bytes.", nameof(data));
        }

        Matrix4x4.Invert(transform, out var inverse);
        var normalMatrix = Matrix4x4.Transpose(inverse);
        var bytes = (byte[])data.VertexBytes.Clone();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var v = 0; v < data.VertexCount; v++)
        {
            var at = v * stride;
            var position = Vector3.Transform(ReadVector3(bytes, at), transform);
            var normal = Vector3.TransformNormal(ReadVector3(bytes, at + 12), normalMatrix);
            if (normal.LengthSquared() > 1e-12f) normal = Vector3.Normalize(normal);
            WriteVector3(bytes, at, position);
            WriteVector3(bytes, at + 12, normal);
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        if (data.VertexCount == 0)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
        }

        return data with
        {
            Name = name ?? data.Name,
            VertexBytes = bytes,
            Bounds = new Bounds3(min, max),
            // A transform changes where the geometry is, not how it is indexed, so the LOD
            // chain still applies — but it indexes the vertices we just moved, so carrying
            // it across is correct and dropping it would silently lose the coarse levels.
        };
    }

    // Several meshes, each moved by its own matrix, as one mesh: what an instanced prop draws, since an
    // instance can place one mesh and not a hierarchy. Every part must share one vertex layout; each is
    // moved with Transformed (position and normal, the first 24 bytes) and the indices re-based. The
    // result keeps 16-bit indices while they fit and widens to 32-bit when the vertex count needs it.
    // The LOD chain is not carried: a merge of meshes has no single chain to keep.
    public static MeshData Merge(this IEnumerable<(MeshData Mesh, Matrix4x4 Transform)> parts, string name)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var moved = parts.Select(p => p.Mesh.Transformed(p.Transform)).ToArray();
        if (moved.Length == 0) throw new ArgumentException("Nothing to merge.", nameof(parts));
        var layout = moved[0].Layout;
        foreach (var m in moved)
        {
            if (m.Layout != layout && !m.Layout.Attributes.SequenceEqual(layout.Attributes))
            {
                throw new ArgumentException(
                    $"'{m.Name}' has a different vertex layout from '{moved[0].Name}'; a merged mesh has one layout.",
                    nameof(parts));
            }
        }

        var vertexCount = moved.Sum(m => m.VertexCount);
        var bytes = new byte[moved.Sum(m => m.VertexBytes.Length)];
        var wide = vertexCount > ushort.MaxValue;
        var indices16 = wide ? Array.Empty<ushort>() : new ushort[moved.Sum(m => m.IndexCount)];
        var indices32 = wide ? new uint[moved.Sum(m => m.IndexCount)] : null;
        int byteAt = 0, indexAt = 0, vertexBase = 0;
        foreach (var m in moved)
        {
            Buffer.BlockCopy(m.VertexBytes, 0, bytes, byteAt, m.VertexBytes.Length);
            byteAt += m.VertexBytes.Length;
            for (var i = 0; i < m.IndexCount; i++)
            {
                var index = (m.Indices32 is { } source32 ? source32[i] : m.Indices[i]) + (uint)vertexBase;
                if (wide) indices32![indexAt++] = index;
                else indices16[indexAt++] = (ushort)index;
            }

            vertexBase += m.VertexCount;
        }

        return new MeshData(name, bytes, indices16, layout, moved.CombinedBounds(), indices32);
    }

    // Bounds over a set of meshes, which is what a multi-primitive asset's real extent
    // is. A per-primitive bound is the extent of one material's worth of a building.
    public static Bounds3 CombinedBounds(this IEnumerable<MeshData> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;
        foreach (var mesh in meshes)
        {
            min = Vector3.Min(min, mesh.Bounds.Min);
            max = Vector3.Max(max, mesh.Bounds.Max);
            any = true;
        }

        return any ? new Bounds3(min, max) : new Bounds3(Vector3.Zero, Vector3.Zero);
    }

    private static Vector3 ReadVector3(byte[] bytes, int at) => new(
        BitConverter.ToSingle(bytes, at),
        BitConverter.ToSingle(bytes, at + 4),
        BitConverter.ToSingle(bytes, at + 8));

    private static void WriteVector3(byte[] bytes, int at, Vector3 value)
    {
        BitConverter.TryWriteBytes(bytes.AsSpan(at, 4), value.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(at + 4, 4), value.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(at + 8, 4), value.Z);
    }
}
