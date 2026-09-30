using System.Buffers.Binary;
using System.Numerics;
using Blix.Assets;
using Blix.Cooked;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix;

/// <summary>A cooked primitive's vertices in the layout a caller's pipeline declares.</summary>
/// <remarks>
/// A static primitive cooks as the complete vertex (<see cref="VertexPosition3NormalTangentTexture2Color"/>),
/// so every narrower layout is a selection from data that exists: the tangent is the authored or
/// MikkTSpace frame, the second set and the colour are the source's or glTF's defaults. Nothing
/// here invents an attribute.
/// </remarks>
internal static class CookedVertices
{
    /// <summary>The static layout a load asks for, from its context's two switches.</summary>
    public static VertexLayout Requested(bool tangents, bool colour) => (tangents, colour) switch
    {
        (true, true) => VertexPosition3NormalTangentTexture2Color.Layout,
        (true, false) => VertexPosition3NormalTangentTexture.Layout,
        (false, true) => VertexPosition3NormalTexture2Color.Layout,
        _ => VertexPosition3NormalTexture.Layout,
    };

    /// <summary>
    /// <paramref name="p"/>'s vertices in <paramref name="to"/>, moved by <paramref name="toLocal"/>
    /// when given (the hierarchy loader's inverse node world).
    /// </summary>
    public static (byte[] Bytes, Bounds3 Bounds) Repack(
        BlixMeshPrimitive p, VertexLayout to, Matrix4x4? toLocal, string path)
    {
        if (toLocal is null && Same(p.Layout, to)) return (p.VertexBytes, p.Bounds);
        if (!Same(p.Layout, VertexPosition3NormalTangentTexture2Color.Layout))
        {
            throw new AssetImportException(
                path, null,
                $"primitive '{p.Name}' is stored as a {p.Layout.Stride}-byte layout and this load wants "
                + $"{to.Stride}; only the complete static vertex can be repacked — re-cook it");
        }

        var normalMatrix = toLocal is { } m ? GltfStaticImporter.ComputeNormalMatrix(m) : Matrix4x4.Identity;
        var source = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(p.VertexBytes);
        const int floats = 15;   // 60 bytes: 14 floats and a packed colour
        var bytes = new byte[p.VertexCount * to.Stride];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (var v = 0; v < p.VertexCount; v++)
        {
            var at = v * floats;
            var position = new Vector3(source[at], source[at + 1], source[at + 2]);
            var normal = new Vector3(source[at + 3], source[at + 4], source[at + 5]);
            var tangent = new Vector3(source[at + 6], source[at + 7], source[at + 8]);
            var handedness = source[at + 9];
            var uv = new Vector2(source[at + 10], source[at + 11]);
            var uv1 = new Vector2(source[at + 12], source[at + 13]);
            var colour = BinaryPrimitives.ReadUInt32LittleEndian(p.VertexBytes.AsSpan((v * 60) + 56, 4));

            if (toLocal is { } local)
            {
                position = Vector3.Transform(position, local);
                normal = Vector3.TransformNormal(normal, normalMatrix);
                if (normal.LengthSquared() > 1e-12f) normal = Vector3.Normalize(normal);
                // A tangent follows the surface, so it moves by the transform itself, not the
                // inverse-transpose a normal needs.
                tangent = Vector3.TransformNormal(tangent, local);
                if (tangent.LengthSquared() > 1e-12f) tangent = Vector3.Normalize(tangent);
            }

            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
            // Written in place: a Sponza load repacks millions of vertices, so no per-vertex arrays.
            var target = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan(v * to.Stride, to.Stride - (to.Stride % 4)));
            target[0] = position.X; target[1] = position.Y; target[2] = position.Z;
            target[3] = normal.X; target[4] = normal.Y; target[5] = normal.Z;
            var colourAt = -1;
            switch (to.Stride)
            {
                case 60:   // complete: tangent, uv0, uv1, colour
                    target[6] = tangent.X; target[7] = tangent.Y; target[8] = tangent.Z; target[9] = handedness;
                    target[10] = uv.X; target[11] = uv.Y; target[12] = uv1.X; target[13] = uv1.Y;
                    colourAt = 56;
                    break;
                case 48:   // tangent, uv0
                    target[6] = tangent.X; target[7] = tangent.Y; target[8] = tangent.Z; target[9] = handedness;
                    target[10] = uv.X; target[11] = uv.Y;
                    break;
                case 44:   // uv0, uv1, colour
                    target[6] = uv.X; target[7] = uv.Y; target[8] = uv1.X; target[9] = uv1.Y;
                    colourAt = 40;
                    break;
                default:   // 32: uv0
                    target[6] = uv.X; target[7] = uv.Y;
                    break;
            }

            if (colourAt >= 0) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((v * to.Stride) + colourAt, 4), colour);
        }

        return (bytes, p.VertexCount > 0 ? new Bounds3(min, max) : p.Bounds);
    }

    // Structural, because a record holding a list compares the list by reference.
    private static bool Same(VertexLayout a, VertexLayout b) =>
        a.Stride == b.Stride && a.Attributes.SequenceEqual(b.Attributes);
}
