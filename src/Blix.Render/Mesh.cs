using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Render;

/// <summary>Geometry resident on a device: its buffers, how many indices, where it is, and how to read it.</summary>
/// <param name="Layout">
/// The vertex layout its buffer was written in. Kept with the buffers because a second pass over the same
/// geometry (a shadow caster, the pick pass) has to read the buffer the same way, and only the upload knew.
/// </param>
public sealed record Mesh(
    string Name,
    VertexBufferHandle VertexBuffer,
    IndexBufferHandle IndexBuffer,
    int IndexCount,
    Bounds3 Bounds,
    VertexLayout Layout);
