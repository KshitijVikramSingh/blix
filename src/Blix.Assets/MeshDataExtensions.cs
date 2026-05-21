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
}
