using System.Buffers.Binary;
using System.Numerics;
using Blix.Assets;
using Blix.Cooked;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix;

/// <summary>A cooked primitive's vertices in the layout a caller's pipeline declares.</summary>
/// <remarks>
/// A primitive cooks as the complete static or skinned vertex, so every narrower layout is a
/// selection from data that exists: the tangent is the authored or
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
    /// <paramref name="p"/>'s vertices in <paramref name="to"/>, moved by <paramref name="transform"/>
    /// when given (a node's world, for a load that wants geometry where the scene puts it).
    /// </summary>
    /// <remarks>
    /// The source is the complete static vertex or the complete skinned one. A static target takes
    /// either (a skinned mesh read as static geometry is its bind pose); the 80-byte skinned target is
    /// the complete skinned vertex's own prefix, and is refused a transform, because skinned vertices
    /// stay in mesh space for their inverse binds.
    /// </remarks>
    public static (byte[] Bytes, Bounds3 Bounds) Repack(
        BlixMeshPrimitive p, VertexLayout to, Matrix4x4? transform, string path)
    {
        if (transform is null && Same(p.Layout, to)) return (p.VertexBytes, p.Bounds);

        var skinnedSource = Same(p.Layout, VertexPosition3NormalTextureSkin4Tangent2Color.Layout);
        if (!skinnedSource && !Same(p.Layout, VertexPosition3NormalTangentTexture2Color.Layout))
        {
            throw new AssetImportException(
                path, null,
                $"primitive '{p.Name}' is stored as a {p.Layout.Stride}-byte layout and this load wants "
                + $"{to.Stride}; only a complete cooked vertex can be repacked — re-cook it");
        }

        if (Same(to, VertexPosition3NormalTextureSkin4Tangent.Layout))
        {
            if (!skinnedSource || transform is not null)
            {
                throw new AssetImportException(
                    path, null, $"primitive '{p.Name}' cannot be read as skinned vertices from a {p.Layout.Stride}-byte static layout");
            }

            var prefix = new byte[p.VertexCount * 80];
            for (var v = 0; v < p.VertexCount; v++) Buffer.BlockCopy(p.VertexBytes, v * 92, prefix, v * 80, 80);
            return (prefix, p.Bounds);
        }

        // Where each attribute sits in the source: the two complete vertices order them differently.
        var stride = p.Layout.Stride;
        var (at, normalAt, uvAt, tangentAt, uv1At, colourAt) = skinnedSource
            ? (0, 12, 24, 64, 80, 88)
            : (0, 12, 40, 24, 48, 56);

        var normalMatrix = transform is { } m ? GltfStaticImporter.ComputeNormalMatrix(m) : Matrix4x4.Identity;
        var bytes = new byte[p.VertexCount * to.Stride];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var src = p.VertexBytes;

        for (var v = 0; v < p.VertexCount; v++)
        {
            var o = v * stride;
            Vector3 V3(int a) => new(BitConverter.ToSingle(src, o + a), BitConverter.ToSingle(src, o + a + 4), BitConverter.ToSingle(src, o + a + 8));
            Vector2 V2(int a) => new(BitConverter.ToSingle(src, o + a), BitConverter.ToSingle(src, o + a + 4));
            var position = V3(at);
            var normal = V3(normalAt);
            var tangent = V3(tangentAt);
            var handedness = BitConverter.ToSingle(src, o + tangentAt + 12);
            var uv = V2(uvAt);
            var uv1 = V2(uv1At);
            var colour = BinaryPrimitives.ReadUInt32LittleEndian(src.AsSpan(o + colourAt, 4));

            if (transform is { } local)
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
            var colourOut = -1;
            switch (to.Stride)
            {
                case 60:   // complete: tangent, uv0, uv1, colour
                    target[6] = tangent.X; target[7] = tangent.Y; target[8] = tangent.Z; target[9] = handedness;
                    target[10] = uv.X; target[11] = uv.Y; target[12] = uv1.X; target[13] = uv1.Y;
                    colourOut = 56;
                    break;
                case 48:   // tangent, uv0
                    target[6] = tangent.X; target[7] = tangent.Y; target[8] = tangent.Z; target[9] = handedness;
                    target[10] = uv.X; target[11] = uv.Y;
                    break;
                case 44:   // uv0, uv1, colour
                    target[6] = uv.X; target[7] = uv.Y; target[8] = uv1.X; target[9] = uv1.Y;
                    colourOut = 40;
                    break;
                default:   // 32: uv0
                    target[6] = uv.X; target[7] = uv.Y;
                    break;
            }

            if (colourOut >= 0) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((v * to.Stride) + colourOut, 4), colour);
        }

        return (bytes, p.VertexCount > 0 ? new Bounds3(min, max) : p.Bounds);
    }

    // Structural, because a record holding a list compares the list by reference.
    private static bool Same(VertexLayout a, VertexLayout b) =>
        a.Stride == b.Stride && a.Attributes.SequenceEqual(b.Attributes);
}
