using System.Numerics;
using Blix.Assets;
using Blix.Geometry;
using Blix.Graphics;
using SharpGLTF.Schema2;

namespace Blix.Recipes;

/// <summary>A glTF primitive's vertices and triangles, in mesh space, as the cook writes them.</summary>
/// <remarks>
/// Matrices need no conversion at this boundary: SharpGLTF delivers them in System.Numerics row-vector form,
/// which is the engine's convention (F-016).
/// </remarks>
internal static class GltfVertices
{
    /// <summary>A static primitive as the complete 60-byte vertex (<see cref="VertexPosition3NormalTangentTexture2Color"/>).</summary>
    /// <remarks>
    /// Spec §3.7.2.1 "Meshes / Overview", the mesh.primitive attribute table:
    /// https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#meshes-overview. This reads POSITION and
    /// optional NORMAL, TANGENT, TEXCOORD_0, TEXCOORD_1 and COLOR_0; what else a primitive carries is recorded
    /// by <see cref="GltfUnread"/>. The tangent is ZERO where the source authored none: the cook fills it with
    /// MikkTSpace's frame, and nothing else guesses.
    /// </remarks>
    public static MeshData Static(string name, MeshPrimitive primitive, bool flipTextureV = false)
    {
        var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array()
            ?? throw new InvalidOperationException("glTF mesh primitive missing required POSITION accessor.");
        var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
        var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        // Authored tangents (glTF vec4: xyz dir + w handedness). MikkTSpace-compatible per spec, so
        // forwarding beats recomputing.
        var tangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();
        // AsColorArray, not AsVector4Array: COLOR_0 is legally float, ushort-normalised or byte-normalised,
        // and vec3 as well as vec4. This accessor collapses all six spellings to 0..1 RGBA with alpha
        // defaulted to opaque, which is the only reading correct for every one.
        var colours = primitive.GetVertexAccessor("COLOR_0")?.AsColorArray();
        var uv1 = primitive.GetVertexAccessor("TEXCOORD_1")?.AsVector2Array();

        var vertexCount = positions.Count;
        var minB = new Vector3(float.PositiveInfinity);
        var maxB = new Vector3(float.NegativeInfinity);

        // V canonicalisation (V -> 1-V), opt-in per cook via flipV: bottom-up (OpenGL-authored) sources
        // sample vertically inverted on a top-down (Vulkan / D3D) sampler.
        Vector2 Uv(Vector2 uv) => new(uv.X, flipTextureV ? 1.0f - uv.Y : uv.Y);

        // Positions and directions go through the identity transform, as they always have: it writes an
        // authored -0 as +0, and dropping it would change cooked bytes (not values) at this recipe version.
        static Vector3 Point(Vector3 p) => GraphicsMatrices.TransformPoint(Matrix4x4.Identity, p);
        static Vector3 Direction(Vector3 d) => Vector3.Normalize(GraphicsMatrices.TransformDirection(Matrix4x4.Identity, d));

        var verts = new VertexPosition3NormalTangentTexture2Color[vertexCount];
        for (var v = 0; v < vertexCount; v++)
        {
            var p = Point(positions[v]);
            var n = Direction(normals is null ? Vector3.UnitY : normals[v]);
            var tangent = new GraphicsVector4(0f, 0f, 0f, 0f);
            if (tangents is not null)
            {
                var t = tangents[v];
                var tDir = Direction(new Vector3(t.X, t.Y, t.Z));
                tangent = new GraphicsVector4(tDir.X, tDir.Y, tDir.Z, t.W < 0f ? -1f : 1f);
            }

            var uv = Uv(uvs is null ? Vector2.Zero : uvs[v]);
            // A mesh with no second set gets its first one mirrored, not zeroed: a material that names set 1
            // anyway then samples the same place rather than collapsing the whole surface onto one texel.
            var second = uv1 is null ? uv : Uv(uv1[v]);
            // White when the primitive has no COLOR_0 of its own: glTF's default, and the common case.
            var c = colours is null
                ? VertexPosition3NormalTextureColor.White
                : VertexPosition3NormalTextureColor.Pack(colours[v].X, colours[v].Y, colours[v].Z, colours[v].W);
            verts[v] = new VertexPosition3NormalTangentTexture2Color(
                new GraphicsVector3(p.X, p.Y, p.Z),
                new GraphicsVector3(n.X, n.Y, n.Z),
                tangent,
                new GraphicsVector2(uv.X, uv.Y),
                new GraphicsVector2(second.X, second.Y),
                c);
            minB = Vector3.Min(minB, p);
            maxB = Vector3.Max(maxB, p);
        }

        var packed = VertexPosition3NormalTangentTexture2Color.Pack(verts);
        var layout = VertexPosition3NormalTangentTexture2Color.Layout;
        SanitizePackedUVs(packed, vertexCount, layout.Stride, uvOffset: 10 * sizeof(float), name);
        var (indices16, indices32) = Narrowest(TriangleIndices(name, primitive), vertexCount);
        return new MeshData(
            name, packed, indices16, layout,
            vertexCount == 0 ? Bounds3.Empty : new Bounds3(minB, maxB),
            Indices32: indices32);
    }

    /// <summary>A skinned primitive's 80-byte vertices, joints remapped to the skin's bone order.</summary>
    /// <remarks>
    /// POSITION, JOINTS_0 and WEIGHTS_0 are required; NORMAL / TEXCOORD_0 default to the up-normal and (0, 0).
    /// Vertices stay in mesh space, which glTF skinning requires: inverse binds map mesh space to joint space,
    /// so any transform applied first breaks the math. The tangent is zero where none is authored.
    /// </remarks>
    public static MeshData Skinned(string name, MeshPrimitive primitive, int[] oldToNew)
    {
        var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array()
            ?? throw new InvalidOperationException("glTF mesh primitive missing required POSITION accessor.");
        var jointsAcc = primitive.GetVertexAccessor("JOINTS_0")
            ?? throw new InvalidOperationException("glTF mesh primitive missing JOINTS_0 — not a skinned mesh.");
        var weightsAcc = primitive.GetVertexAccessor("WEIGHTS_0")
            ?? throw new InvalidOperationException("glTF mesh primitive missing WEIGHTS_0 — not a skinned mesh.");
        var jointsArray = jointsAcc.AsVector4Array();
        var weightsArray = weightsAcc.AsVector4Array();

        // Gather every complete JOINTS_n/WEIGHTS_n pair before selecting and renormalising the
        // strongest four influences for the engine vertex layout.
        var extraJoints = new List<IList<Vector4>>();
        var extraWeights = new List<IList<Vector4>>();
        for (var set = 1; ; set++)
        {
            var j = primitive.GetVertexAccessor($"JOINTS_{set}");
            var w = primitive.GetVertexAccessor($"WEIGHTS_{set}");
            if (j is null || w is null) break;
            extraJoints.Add(j.AsVector4Array());
            extraWeights.Add(w.AsVector4Array());
        }
        var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
        var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        var tangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();

        var vertexCount = positions.Count;
        var vertices = new VertexPosition3NormalTextureSkin4Tangent[vertexCount];
        var min = positions[0];
        var max = positions[0];
        for (var v = 0; v < vertexCount; v++)
        {
            var p = positions[v];
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);

            var n = normals?[v] ?? new Vector3(0.0f, 1.0f, 0.0f);
            // Preserve glTF UVs: glTF coordinates, decoded row order, and Vulkan sampling all use
            // the same top-left image convention on this path.
            var uv = uvs?[v] ?? Vector2.Zero;

            // Remap joint indices through the topo-sort. Each slot is a float that the vertex shader
            // int()s at lookup time. Unused slots (weight == 0) still get remapped so the stored index
            // stays within bounds.
            Vector4 newIdx, w;
            if (extraJoints.Count == 0)
            {
                // Preserve authored slot order when all influences already fit the layout.
                var oldIdx = jointsArray[v];
                newIdx = new Vector4(
                    oldToNew[(int)oldIdx.X],
                    oldToNew[(int)oldIdx.Y],
                    oldToNew[(int)oldIdx.Z],
                    oldToNew[(int)oldIdx.W]);
                w = weightsArray[v];
            }
            else
            {
                (newIdx, w) = SelectInfluences(v, jointsArray, weightsArray, extraJoints, extraWeights, oldToNew);
            }

            var t = tangents?[v] ?? Vector4.Zero;

            vertices[v] = new VertexPosition3NormalTextureSkin4Tangent(
                new GraphicsVector3(p.X, p.Y, p.Z),
                new GraphicsVector3(n.X, n.Y, n.Z),
                new GraphicsVector2(uv.X, uv.Y),
                new GraphicsVector4(newIdx.X, newIdx.Y, newIdx.Z, newIdx.W),
                new GraphicsVector4(w.X, w.Y, w.Z, w.W),
                new GraphicsVector4(t.X, t.Y, t.Z, t.W));
        }

        var (indices16, indices32) = Narrowest(TriangleIndices(name, primitive), vertexCount);
        return new MeshData(
            name, VertexPosition3NormalTextureSkin4Tangent.Pack(vertices), indices16,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            new Bounds3(min, max),
            Indices32: indices32);
    }

    /// <summary>A primitive's triangles as a flat index list, whatever its mode says.</summary>
    /// <remarks>
    /// TRIANGLES is read as authored; TRIANGLE_STRIP and TRIANGLE_FAN are unrolled into lists (glTF
    /// 2.0 §3.7.2.1), and a primitive with no index accessor draws its vertices in order. POINTS and the
    /// three LINE modes are refused by name: Blix draws triangles, and reading a line's indices as a
    /// triangle list is the garbage this replaces.
    /// </remarks>
    public static uint[] TriangleIndices(string name, MeshPrimitive primitive)
    {
        if (primitive.DrawPrimitiveType is PrimitiveType.POINTS or PrimitiveType.LINES
            or PrimitiveType.LINE_LOOP or PrimitiveType.LINE_STRIP)
        {
            throw new InvalidOperationException(
                $"glTF primitive '{name}' is {primitive.DrawPrimitiveType}; Blix draws triangles, and a point or line primitive is not read.");
        }

        // The vertex order the mode reads: the index accessor (sparse substitutions applied — an index
        // accessor may itself be sparse), or the vertices in order when there is none.
        var count = primitive.GetVertexAccessor("POSITION")?.Count ?? 0;
        uint[] raw = primitive.IndexAccessor is not { } accessor ? Enumerable.Range(0, count).Select(i => (uint)i).ToArray()
            : accessor.IsSparse ? accessor.AsScalarArray().Select(v => (uint)MathF.Round(v)).ToArray()
            : accessor.AsIndicesArray().ToArray();
        // An index past the vertices reads memory that is not this primitive's: refused, by name. This is
        // also how primitive restart (an all-ones index, which glTF forbids) is caught.
        if (raw.FirstOrDefault(i => i >= count) is var beyond && raw.Any(i => i >= count))
        {
            throw new InvalidOperationException(
                $"glTF primitive '{name}' indexes vertex {beyond} of {count}; glTF forbids an index past the vertices (and primitive restart).");
        }

        // glTF 2.0 §3.7.2.1's counts, refused by name rather than repaired: a list's vertices MUST be
        // divisible by three, a strip's or fan's MUST be at least three. Dropping the remainder would
        // quietly delete geometry the file says is there — and with SharpGLTF's validator out of the way
        // for a lenient file, nothing else would say so.
        var isList = primitive.DrawPrimitiveType is not (PrimitiveType.TRIANGLE_STRIP or PrimitiveType.TRIANGLE_FAN);
        if (isList ? raw.Length == 0 || raw.Length % 3 != 0 : raw.Length < 3)
        {
            throw new InvalidOperationException(
                $"glTF primitive '{name}' has {raw.Length} vertex index(es) for {primitive.DrawPrimitiveType}; glTF requires "
                + (isList ? "a non-zero multiple of three." : "at least three."));
        }

        // glTF 2.0 §3.7.2.1: strip triangle i is (v_i, v_i+1+i%2, v_i+2-i%2) and fan triangle i is
        // (v_i+1, v_i+2, v_0), both keeping the first triangle's winding.
        switch (primitive.DrawPrimitiveType)
        {
            case PrimitiveType.TRIANGLE_STRIP:
            {
                var list = new uint[Math.Max(0, raw.Length - 2) * 3];
                for (var i = 0; i + 2 < raw.Length; i++)
                {
                    list[i * 3] = raw[i];
                    list[i * 3 + 1] = raw[i + 1 + i % 2];
                    list[i * 3 + 2] = raw[i + 2 - i % 2];
                }

                return list;
            }
            case PrimitiveType.TRIANGLE_FAN:
            {
                var list = new uint[Math.Max(0, raw.Length - 2) * 3];
                for (var i = 0; i + 2 < raw.Length; i++)
                {
                    list[i * 3] = raw[i + 1];
                    list[i * 3 + 1] = raw[i + 2];
                    list[i * 3 + 2] = raw[0];
                }

                return list;
            }
            default:
                return raw;
        }
    }

    // The narrowest index width that fits. UInt16 covers virtually every authored asset; UInt32 kicks in
    // for large packs like Khronos Sponza Modern's curtains (66k vertices in a single primitive).
    private static (ushort[] Indices16, uint[]? Indices32) Narrowest(uint[] triangles, int vertexCount) =>
        vertexCount > ushort.MaxValue
            ? (Array.Empty<ushort>(), triangles)
            : (triangles.Select(i => (ushort)i).ToArray(), null);

    /// <summary>
    /// The four strongest influences on one vertex, renormalised, out of however many the file gives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four is the vertex-layout limit. The layout carries four bone indices and four weights; widening it
    /// to eight costs 32 bytes on every skinned vertex in every asset, for influences that are almost always
    /// negligible.
    /// </para>
    /// <para>
    /// The strongest four are retained. glTF does not require influence sets to be sorted, so
    /// "the first four" can discard the influence that actually shapes the vertex and keep three that
    /// barely move it.
    /// </para>
    /// <para>
    /// Retained weights are renormalised. Weights sum to 1 across ALL sets, so keeping a subset leaves them
    /// summing to less, and a skinning matrix scaled by 0.8 drags its vertex a fifth of the way to the
    /// origin. Approximate deformation is a limitation; a collapsing mesh is a bug.
    /// </para>
    /// </remarks>
    private static (Vector4 Joints, Vector4 Weights) SelectInfluences(
        int v,
        IList<Vector4> joints0, IList<Vector4> weights0,
        List<IList<Vector4>> extraJoints, List<IList<Vector4>> extraWeights,
        int[] oldToNew)
    {
        Span<(int Joint, float Weight)> all = stackalloc (int, float)[4 + (extraJoints.Count * 4)];
        var n = 0;
        void Take(Vector4 j, Vector4 w, Span<(int, float)> into, ref int at)
        {
            into[at++] = ((int)j.X, w.X);
            into[at++] = ((int)j.Y, w.Y);
            into[at++] = ((int)j.Z, w.Z);
            into[at++] = ((int)j.W, w.W);
        }

        Take(joints0[v], weights0[v], all, ref n);
        for (var s = 0; s < extraJoints.Count; s++) Take(extraJoints[s][v], extraWeights[s][v], all, ref n);

        // Selection sort for the top four: n is at most a handful, and this keeps ties in the order
        // the file listed them so a re-cook gives the same answer.
        for (var i = 0; i < 4 && i < n; i++)
        {
            var best = i;
            for (var k = i + 1; k < n; k++)
            {
                if (all[k].Weight > all[best].Weight) best = k;
            }

            (all[i], all[best]) = (all[best], all[i]);
        }

        var total = 0f;
        for (var i = 0; i < 4 && i < n; i++) total += all[i].Weight;
        var scale = total > 1e-6f ? 1f / total : 0f;

        var idx = Vector4.Zero;
        var wt = Vector4.Zero;
        for (var i = 0; i < 4; i++)
        {
            var (joint, weight) = i < n ? all[i] : (0, 0f);
            var remapped = (float)oldToNew[joint];
            var scaled = weight * scale;
            switch (i)
            {
                case 0: idx.X = remapped; wt.X = scaled; break;
                case 1: idx.Y = remapped; wt.Y = scaled; break;
                case 2: idx.Z = remapped; wt.Z = scaled; break;
                default: idx.W = remapped; wt.W = scaled; break;
            }
        }

        return (idx, wt);
    }

    // Some authored assets ship one or two vertices with extreme UV values (we've seen -42470 in the
    // Khronos Intel Sponza source). With wrap=Repeat the GPU still tiles, but adjacent triangles' UV
    // interpolation drags across thousands of units, blowing up dFdx/dFdy so the sampler picks the
    // coarsest mip everywhere -> washed-out garbage. Fold any out-of-range vertex back into [0,1) with
    // `frac` so the LOD calc and texture cache stay sane; tiled textures still tile correctly because
    // frac is the same value modulo 1.
    private const float MaxReasonableUV = 100.0f;

    private static void SanitizePackedUVs(byte[] vertexBytes, int vertexCount, int stride, int uvOffset, string ownerName)
    {
        var touched = 0;
        var span = vertexBytes.AsSpan();
        for (var v = 0; v < vertexCount; v++)
        {
            var slot = span.Slice(v * stride + uvOffset, 8);
            var u = System.Runtime.InteropServices.MemoryMarshal.Read<float>(slot);
            var vv = System.Runtime.InteropServices.MemoryMarshal.Read<float>(slot.Slice(4));
            var fix = false;
            if (MathF.Abs(u) > MaxReasonableUV) { u -= MathF.Floor(u); fix = true; }
            if (MathF.Abs(vv) > MaxReasonableUV) { vv -= MathF.Floor(vv); fix = true; }
            if (fix)
            {
                System.Runtime.InteropServices.MemoryMarshal.Write(slot, in u);
                System.Runtime.InteropServices.MemoryMarshal.Write(slot.Slice(4), in vv);
                touched++;
            }
        }
        if (touched > 0)
        {
            Console.WriteLine(
                $"[MeshRecipe] sanitized {touched} vertex UV(s) in '{ownerName}' " +
                $"(values exceeded |UV|>{MaxReasonableUV}; folded with frac to [0,1))");
        }
    }
}
