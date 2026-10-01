namespace Blix.Graphics;

/// <summary>What the bytes of one of Blix's own vertex layouts mean: where each attribute is, by role.</summary>
/// <remarks>
/// <para>
/// A <see cref="VertexLayout"/> says only formats and offsets, which is all a pipeline needs and not enough
/// to edit a vertex: in the skinned layouts the first Float4 is the bone indices, not the tangent, and code
/// that guessed by format would transform joint indices as a direction. So the layouts whose bytes Blix
/// edits on the CPU are named here, matched exactly, and anything else is refused by whoever asks.
/// </para>
/// <para>Offsets are bytes; -1 where the layout has no such attribute.</para>
/// </remarks>
/// <param name="Skinned">The layout carries joints and weights: its vertices are placed by a skin, not moved in place.</param>
public sealed record VertexSemantics(int Position, int Normal, int Uv0, int Uv1, int Tangent, bool Skinned)
{
    private static readonly (VertexLayout Layout, VertexSemantics Semantics)[] Known =
    {
        (VertexPosition3NormalTexture.Layout, new(0, 12, 24, -1, -1, false)),
        (VertexPosition3NormalTextureColor.Layout, new(0, 12, 24, -1, -1, false)),
        (VertexPosition3NormalTexture2Color.Layout, new(0, 12, 24, 32, -1, false)),
        (VertexPosition3NormalTangentTexture.Layout, new(0, 12, 40, -1, 24, false)),
        (VertexPosition3NormalTangentTexture2Color.Layout, new(0, 12, 40, 48, 24, false)),
        (VertexPosition3NormalTextureSkin4Tangent.Layout, new(0, 12, 24, -1, 64, true)),
        (VertexPosition3NormalTextureSkin4Tangent2Color.Layout, new(0, 12, 24, 80, 64, true)),
    };

    /// <summary>The meaning of <paramref name="layout"/>'s bytes, or null when it is not one of Blix's own.</summary>
    public static VertexSemantics? Of(VertexLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        foreach (var (known, semantics) in Known)
        {
            // Structural: a layout holding a list compares the list by reference.
            if (known.Stride == layout.Stride && known.Attributes.SequenceEqual(layout.Attributes)) return semantics;
        }

        return null;
    }
}
