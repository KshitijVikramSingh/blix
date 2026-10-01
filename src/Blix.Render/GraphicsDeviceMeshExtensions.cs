using Blix.Assets;
using Blix.Graphics;

namespace Blix.Render;

public static class GraphicsDeviceMeshExtensions
{
    /// <summary>Uploads one imported mesh to the device: a vertex buffer, an index buffer, bounds.</summary>
    /// <remarks>
    /// <para>
    /// The whole of "turn a <see cref="MeshData"/> into something drawable", and deliberately no
    /// more. It takes no shader, no material, no pipeline and no instance count, because every
    /// consumer differs in exactly those and agrees on exactly this.
    /// </para>
    /// <para>
    /// <b>It could not be used until now, and the reason is the second line below.</b> This helper
    /// existed with no 32-bit branch, so it read <c>data.Indices</c> unconditionally — which for a
    /// 32-bit mesh is EMPTY, giving a silently index-less mesh and an index count of zero. Any rig
    /// or authored scene big enough to pass 65535 vertices in one primitive lands on that path, so
    /// every consumer wrote the branch itself and none of them adopted this. <see cref="MeshData"/>
    /// states the obligation in as many words: consumers branch on IndexFormat to pick the array
    /// and the overload. The helper that exists to spare consumers that branch was the one place
    /// not doing it.
    /// </para>
    /// </remarks>
    public static Mesh CreateMesh(this IGraphicsDevice device, MeshData data, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(data);

        var meshName = name ?? data.Name;

        var vertexBufferData = new VertexBufferData(
            new VertexBufferDescription(data.Layout, data.VertexCount, GraphicsBufferUsage.Static),
            data.VertexBytes);

        var vertexBuffer = device.CreateVertexBuffer(vertexBufferData, name: $"{meshName}.vertices");
        var indexBuffer = data.Indices32 is { } wide
            ? device.CreateIndexBuffer(wide, name: $"{meshName}.indices")
            : device.CreateIndexBuffer(data.Indices, name: $"{meshName}.indices");

        // data.IndexCount, not data.Indices.Length: the same 32-bit case that broke the buffer
        // above would report zero indices here, and a zero-index draw renders nothing at all
        // rather than failing.
        return new Mesh(meshName, vertexBuffer, indexBuffer, data.IndexCount, data.Bounds, data.Layout);
    }
}
