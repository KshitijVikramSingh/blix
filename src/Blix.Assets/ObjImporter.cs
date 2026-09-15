using System.Globalization;
using System.Numerics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Cooked;

namespace Blix.Assets;

public sealed class ObjImporter : IAssetImporter<MeshData>
{
    public string Name => "static-mesh.obj";

    // When true, the imported mesh has its AABB center shifted to (0, 0, 0) — every
    // position is translated by -bounds.Center after parsing. Removes the surprise
    // factor of OBJ files authored at arbitrary world positions (Suzanne at world
    // ~(-2.5, 1.25, 4.1), Stanford bunny with feet at y=0.033 instead of bottom-centered,
    // etc.) and lets game code use a plain `Transform.Position` to place the mesh
    // wherever it should appear.
    //
    // Shift happens AFTER normal/UV synthesis (which uses absolute positions for the
    // spherical UV projection) — synth values stay tied to the original positions, only
    // the final vertex stream's Position is translated.
    public bool RecenterToOrigin { get; init; } = true;

    public MeshData Import(AssetImportContext context)
    {
        if (!File.Exists(context.SourcePath))
        {
            throw new FileNotFoundException($"OBJ file not found: {context.SourcePath}", context.SourcePath);
        }

        try
        {
            return ImportCore(context);
        }
        catch (ObjFormatException ex)
        {
            throw new AssetImportException(context.SourcePath, ex.LineNumber, ex.Message, ex);
        }
    }

    private readonly record struct FaceVertex(int Position, int TexCoord, int Normal);

    private MeshData ImportCore(AssetImportContext context)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        // Per-face triplets (after fan-triangulation). Texcoord/normal indices may be -1
        // for face formats that omit those streams; the post-pass synthesizes them.
        var faceTriplets = new List<FaceVertex>();

        using var reader = new StreamReader(context.SourcePath);
        var lineNumber = 0;
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0 || tokens[0].StartsWith('#'))
            {
                continue;
            }

            switch (tokens[0])
            {
                case "v":
                    positions.Add(ParseVector3(tokens, lineNumber));
                    break;

                case "vn":
                    normals.Add(ParseVector3(tokens, lineNumber));
                    break;

                case "vt":
                    uvs.Add(ParseVector2(tokens, lineNumber));
                    break;

                case "f":
                    ParseFace(tokens, lineNumber, positions, normals, uvs, faceTriplets);
                    break;

                // Directives we silently ignore: o (object), g (group), s (smoothing),
                // mtllib, usemtl, l (line). Adding material/group support means a richer
                // MeshData with submeshes, deferred until needed.
            }
        }

        if (faceTriplets.Count == 0)
        {
            throw new ObjFormatException(null, "file produced no faces.");
        }

        // Synthesize missing streams. If the file provided no normals, derive smooth
        // per-vertex normals from face geometry. If it provided no texcoords, project a
        // spherical UV from the position. Both keep the renderer's input layout uniform
        // (v3/vn/vt) without requiring every asset to be fully authored.
        //
        // Synthesis happens BEFORE recentering: smooth normals are translation-invariant
        // either way, but the spherical UV projection uses each vertex's direction from
        // origin — recentering first would shift the UV pole/seam relative to the mesh.
        Vector3[]? synthNormals = normals.Count == 0 ? ComputeSmoothNormals(positions, faceTriplets) : null;
        Vector2[]? synthUvs = uvs.Count == 0 ? ComputeSphericalUvs(positions) : null;

        // Recenter positions so the mesh's AABB center lands at the origin. Normals and
        // (already-computed) UVs stay attached to vertices via index, so the shift only
        // affects positions in the final stream.
        if (RecenterToOrigin && positions.Count > 0)
        {
            var rawCenter = Bounds3.FromPoints(positions.ToArray()).Center;
            for (var i = 0; i < positions.Count; i++)
            {
                positions[i] -= rawCenter;
            }
        }

        var vertexLookup = new Dictionary<FaceVertex, ushort>();
        var finalVertices = new List<VertexPosition3NormalTexture>();
        var indices = new List<ushort>(faceTriplets.Count);

        foreach (var triplet in faceTriplets)
        {
            // Key on the resolved triplet so vertices with identical (pos, uv, normal)
            // are shared. When uv/normal are synthesized (== -1), they're a function of
            // the position so we substitute the position index as the dedup key.
            var key = new FaceVertex(
                triplet.Position,
                triplet.TexCoord >= 0 ? triplet.TexCoord : triplet.Position,
                triplet.Normal >= 0 ? triplet.Normal : triplet.Position);

            if (!vertexLookup.TryGetValue(key, out var existing))
            {
                if (finalVertices.Count >= ushort.MaxValue)
                {
                    throw new ObjFormatException(
                        null,
                        $"mesh exceeds {ushort.MaxValue} unique vertices. The engine currently uses uint16 index buffers; adding IndexFormat.UInt32 is the fix.");
                }

                var position = positions[triplet.Position];
                var normal = triplet.Normal >= 0 ? normals[triplet.Normal] : synthNormals![triplet.Position];
                var uv = triplet.TexCoord >= 0 ? uvs[triplet.TexCoord] : synthUvs![triplet.Position];

                existing = (ushort)finalVertices.Count;
                finalVertices.Add(new VertexPosition3NormalTexture(
                    new GraphicsVector3(position.X, position.Y, position.Z),
                    new GraphicsVector3(normal.X, normal.Y, normal.Z),
                    new GraphicsVector2(uv.X, uv.Y)));
                vertexLookup[key] = existing;
            }
            indices.Add(existing);
        }

        var positionArray = positions.ToArray();
        var bounds = Bounds3.FromPoints(positionArray);
        var vertexBytes = VertexPosition3NormalTexture.Pack(finalVertices);

        return new MeshData(
            Name: context.AssetId.Value,
            VertexBytes: vertexBytes,
            Indices: indices.ToArray(),
            Layout: VertexPosition3NormalTexture.Layout,
            Bounds: bounds);
    }

    private static Vector3 ParseVector3(string[] tokens, int lineNumber)
    {
        if (tokens.Length < 4)
        {
            throw new ObjFormatException(lineNumber, $"'{tokens[0]}' expects 3 components.");
        }

        return new Vector3(
            ParseFloat(tokens[1], lineNumber),
            ParseFloat(tokens[2], lineNumber),
            ParseFloat(tokens[3], lineNumber));
    }

    private static Vector2 ParseVector2(string[] tokens, int lineNumber)
    {
        if (tokens.Length < 3)
        {
            throw new ObjFormatException(lineNumber, $"'{tokens[0]}' expects 2 components.");
        }

        return new Vector2(
            ParseFloat(tokens[1], lineNumber),
            ParseFloat(tokens[2], lineNumber));
    }

    private static float ParseFloat(string token, int lineNumber)
    {
        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new ObjFormatException(lineNumber, $"cannot parse float '{token}'.");
        }

        return value;
    }

    private static void ParseFace(
        string[] tokens,
        int lineNumber,
        List<Vector3> positions,
        List<Vector3> normals,
        List<Vector2> uvs,
        List<FaceVertex> faceTriplets)
    {
        if (tokens.Length < 4)
        {
            throw new ObjFormatException(lineNumber, "face requires at least 3 vertices.");
        }

        Span<FaceVertex> faceVerts = stackalloc FaceVertex[tokens.Length - 1];
        for (var i = 1; i < tokens.Length; i++)
        {
            faceVerts[i - 1] = ResolveFaceVertex(tokens[i], lineNumber, positions, normals, uvs);
        }

        // Fan-triangulate: (v0, v1, v2), (v0, v2, v3), ...
        for (var i = 1; i < faceVerts.Length - 1; i++)
        {
            faceTriplets.Add(faceVerts[0]);
            faceTriplets.Add(faceVerts[i]);
            faceTriplets.Add(faceVerts[i + 1]);
        }
    }

    private static FaceVertex ResolveFaceVertex(
        string spec,
        int lineNumber,
        List<Vector3> positions,
        List<Vector3> normals,
        List<Vector2> uvs)
    {
        // OBJ face vertex grammar:
        //   v          -> position only
        //   v/t        -> position + texcoord
        //   v//n       -> position + normal
        //   v/t/n      -> all three
        // Missing components are represented as empty parts.
        var parts = spec.Split('/');
        var pIdx = ResolveIndex(parts[0], positions.Count, lineNumber, "position");
        var tIdx = -1;
        var nIdx = -1;
        if (parts.Length > 1 && parts[1].Length > 0)
        {
            tIdx = ResolveIndex(parts[1], uvs.Count, lineNumber, "texcoord");
        }
        if (parts.Length > 2 && parts[2].Length > 0)
        {
            nIdx = ResolveIndex(parts[2], normals.Count, lineNumber, "normal");
        }
        return new FaceVertex(pIdx, tIdx, nIdx);
    }

    private static Vector3[] ComputeSmoothNormals(List<Vector3> positions, List<FaceVertex> faceTriplets)
    {
        // Accumulate face normals (un-normalized so larger triangles weight more, which
        // matches the standard area-weighted smooth-normal definition). The list contains
        // triplets already fan-triangulated, so we process them three at a time.
        var accum = new Vector3[positions.Count];
        for (var i = 0; i + 2 < faceTriplets.Count; i += 3)
        {
            var ia = faceTriplets[i].Position;
            var ib = faceTriplets[i + 1].Position;
            var ic = faceTriplets[i + 2].Position;
            var faceNormal = Vector3.Cross(positions[ib] - positions[ia], positions[ic] - positions[ia]);
            accum[ia] += faceNormal;
            accum[ib] += faceNormal;
            accum[ic] += faceNormal;
        }
        for (var i = 0; i < accum.Length; i++)
        {
            var v = accum[i];
            var lengthSq = v.LengthSquared();
            // Degenerate cases (isolated vertex, all-coincident faces) get +Y as a
            // harmless fallback rather than NaN.
            accum[i] = lengthSq > 1e-12f ? v / MathF.Sqrt(lengthSq) : new Vector3(0, 1, 0);
        }
        return accum;
    }

    private static Vector2[] ComputeSphericalUvs(List<Vector3> positions)
    {
        // Centroid-relative spherical projection. Has a seam at the dateline (where
        // atan2 wraps) but that's an inherent property of spherical UVs, not a bug.
        var centroid = Vector3.Zero;
        foreach (var p in positions)
        {
            centroid += p;
        }
        if (positions.Count > 0)
        {
            centroid /= positions.Count;
        }

        var uvs = new Vector2[positions.Count];
        for (var i = 0; i < positions.Count; i++)
        {
            var d = positions[i] - centroid;
            var len = d.Length();
            if (len < 1e-6f)
            {
                uvs[i] = new Vector2(0.5f, 0.5f);
                continue;
            }
            var nx = d.X / len;
            var ny = d.Y / len;
            var nz = d.Z / len;
            var u = 0.5f + MathF.Atan2(nz, nx) / (2.0f * MathF.PI);
            var v = 0.5f - MathF.Asin(Math.Clamp(ny, -1.0f, 1.0f)) / MathF.PI;
            uvs[i] = new Vector2(u, v);
        }
        return uvs;
    }

    private static int ResolveIndex(string token, int currentCount, int lineNumber, string kind)
    {
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
        {
            throw new ObjFormatException(lineNumber, $"invalid {kind} index '{token}'.");
        }

        int resolved;

        if (raw > 0)
        {
            resolved = raw - 1;
        }
        else if (raw < 0)
        {
            resolved = currentCount + raw;
        }
        else
        {
            throw new ObjFormatException(lineNumber, $"{kind} index 0 is invalid (OBJ indices are 1-based).");
        }

        if (resolved < 0 || resolved >= currentCount)
        {
            throw new ObjFormatException(
                lineNumber,
                $"{kind} index {raw} out of range (have {currentCount} {kind}(s) so far).");
        }

        return resolved;
    }

    private sealed class ObjFormatException : Exception
    {
        public ObjFormatException(int? lineNumber, string message)
            : base(message)
        {
            LineNumber = lineNumber;
        }

        public int? LineNumber { get; }
    }
}
