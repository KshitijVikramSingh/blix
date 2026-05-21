using Blix.Assets;
using Blix.Graphics;

namespace Blix.Render;

public static class GraphicsDeviceMeshExtensions
{
    public static Mesh CreateMesh(this IGraphicsDevice device, MeshData data, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(data);

        var meshName = name ?? data.Name;

        var vertexBufferData = new VertexBufferData(
            new VertexBufferDescription(data.Layout, data.VertexCount, GraphicsBufferUsage.Static),
            data.VertexBytes);

        var vertexBuffer = device.CreateVertexBuffer(vertexBufferData, name: $"{meshName}.vertices");
        var indexBuffer = device.CreateIndexBuffer(data.Indices, name: $"{meshName}.indices");

        return new Mesh(meshName, vertexBuffer, indexBuffer, data.Indices.Length, data.Bounds);
    }
}
