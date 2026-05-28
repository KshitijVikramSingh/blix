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
    Float4,
    // 4 unsigned bytes normalized to [0,1] (R8G8B8A8_UNORM). Packed vertex
    // color — ImGui stores per-vertex RGBA this way (4 bytes vs 16 for Float4).
    UByte4Norm
}
