using Blix.Assets;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Recipes;

/// <summary>MikkTSpace tangents for a mesh whose source authored none.</summary>
/// <remarks>
/// <para>
/// glTF says a client SHOULD generate MikkTSpace tangents when TANGENT is absent, because that is
/// the frame normal-map bakers assume. The cook does it once, so a cooked mesh always carries a
/// frame its normal map agrees with.
/// </para>
/// <para>
/// MikkTSpace answers per triangle corner. Corners of one vertex that agree keep sharing it; a
/// vertex whose corners disagree (a UV seam, a mirror line) is split, so the index buffer grows only
/// where the frame genuinely breaks.
/// </para>
/// </remarks>
public static class TangentGeneration
{
    /// <summary>Where a tangent-bearing layout keeps the attributes MikkTSpace reads and writes.</summary>
    private readonly record struct Offsets(int Position, int Normal, int Uv, int Tangent);

    // Layouts carry no semantics, so the two that hold a tangent are named here and every other
    // layout is refused rather than guessed at by format.
    private static Offsets? OffsetsFor(VertexLayout layout)
    {
        if (Same(layout, VertexPosition3NormalTangentTexture2Color.Layout)) return new Offsets(0, 12, 40, 24);
        if (Same(layout, VertexPosition3NormalTangentTexture.Layout)) return new Offsets(0, 12, 40, 24);
        if (Same(layout, VertexPosition3NormalTextureSkin4Tangent2Color.Layout)) return new Offsets(0, 12, 24, 64);
        if (Same(layout, VertexPosition3NormalTextureSkin4Tangent.Layout)) return new Offsets(0, 12, 24, 64);
        return null;
    }

    // Structural, because a record holding a list compares the list by reference.
    private static bool Same(VertexLayout a, VertexLayout b) =>
        a.Stride == b.Stride && a.Attributes.SequenceEqual(b.Attributes);

    /// <summary>Whether every tangent in <paramref name="mesh"/> is zero: the rig importer's "none authored".</summary>
    public static bool HasNoTangents(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var at = OffsetsFor(mesh.Layout) ?? throw Unsupported(mesh);
        var stride = mesh.Layout.Stride;
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var o = (v * stride) + at.Tangent;
            if (BitConverter.ToSingle(mesh.VertexBytes, o) != 0f
                || BitConverter.ToSingle(mesh.VertexBytes, o + 4) != 0f
                || BitConverter.ToSingle(mesh.VertexBytes, o + 8) != 0f) return false;
        }

        return true;
    }

    /// <summary><paramref name="mesh"/> with MikkTSpace tangents, split where corners disagree.</summary>
    /// <remarks>Runs before LODs are built, which is where the cook calls it; a mesh with LODs is refused.</remarks>
    public static MeshData Generate(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Lods is { Count: > 1 })
            throw new InvalidOperationException($"'{mesh.Name}': tangents are generated before LODs are built, not after.");
        var at = OffsetsFor(mesh.Layout) ?? throw Unsupported(mesh);

        var stride = mesh.Layout.Stride;
        var vertices = mesh.VertexCount;
        var indices = mesh.Indices32 ?? mesh.Indices.Select(i => (uint)i).ToArray();
        if (indices.Length == 0) return mesh;

        var positions = new float[vertices * 3];
        var normals = new float[vertices * 3];
        var uvs = new float[vertices * 2];
        for (var v = 0; v < vertices; v++)
        {
            var o = v * stride;
            for (var k = 0; k < 3; k++)
            {
                positions[(v * 3) + k] = BitConverter.ToSingle(mesh.VertexBytes, o + at.Position + (k * 4));
                normals[(v * 3) + k] = BitConverter.ToSingle(mesh.VertexBytes, o + at.Normal + (k * 4));
            }

            uvs[v * 2] = BitConverter.ToSingle(mesh.VertexBytes, o + at.Uv);
            uvs[(v * 2) + 1] = BitConverter.ToSingle(mesh.VertexBytes, o + at.Uv + 4);
        }

        var corners = MikkTSpaceNative.CornerTangents(positions, normals, uvs, indices);

        // Weld: one output vertex per (source vertex, exact tangent). MikkTSpace gives corners it
        // treats as one vertex bit-identical tangents, so exact comparison is the right equality.
        var outIndex = new uint[indices.Length];
        var outOf = new Dictionary<(uint Vertex, int X, int Y, int Z, int W), uint>();
        var sources = new List<(uint Vertex, int Corner)>();
        for (var c = 0; c < indices.Length; c++)
        {
            var key = (indices[c],
                BitConverter.SingleToInt32Bits(corners[c * 4]), BitConverter.SingleToInt32Bits(corners[(c * 4) + 1]),
                BitConverter.SingleToInt32Bits(corners[(c * 4) + 2]), BitConverter.SingleToInt32Bits(corners[(c * 4) + 3]));
            if (!outOf.TryGetValue(key, out var index))
            {
                index = (uint)sources.Count;
                outOf[key] = index;
                sources.Add((indices[c], c));
            }

            outIndex[c] = index;
        }

        var bytes = new byte[sources.Count * stride];
        for (var v = 0; v < sources.Count; v++)
        {
            var (source, corner) = sources[v];
            Buffer.BlockCopy(mesh.VertexBytes, (int)source * stride, bytes, v * stride, stride);
            for (var k = 0; k < 4; k++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan((v * stride) + at.Tangent + (k * 4), 4), corners[(corner * 4) + k]);
            }
        }

        var wide = sources.Count > ushort.MaxValue || mesh.Indices32 is not null;
        return mesh with
        {
            VertexBytes = bytes,
            Indices = wide ? Array.Empty<ushort>() : outIndex.Select(i => (ushort)i).ToArray(),
            Indices32 = wide ? outIndex : null,
            Lods = null,
        };
    }

    private static NotSupportedException Unsupported(MeshData mesh) => new(
        $"'{mesh.Name}': a {mesh.Layout.Stride}-byte layout carries no tangent this generator knows where to write.");
}
