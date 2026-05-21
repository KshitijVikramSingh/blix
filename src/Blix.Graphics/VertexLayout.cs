namespace Blix.Graphics;

public sealed record VertexLayout(int Stride, IReadOnlyList<VertexAttribute> Attributes);

public sealed record VertexAttribute(
    int Location,
    VertexAttributeFormat Format,
    int Offset);

public enum VertexAttributeFormat
{
    Float2 = 0,
    Float3,
    Float4
}
