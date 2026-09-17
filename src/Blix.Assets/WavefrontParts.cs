using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Blix.Cooked;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Assets;

/// <summary>
/// An OBJ split into one mesh per material, with each material's colour.
/// </summary>
/// <remarks>
/// <b>What <see cref="ObjImporter"/>'s own comment said would be needed when it was needed.</b> That
/// importer flattens a file into one <c>MeshData</c> and drops <c>usemtl</c> on the floor, which is right
/// for a single-material body — the villager — and useless for anything with a trunk and a canopy.
/// <para>
/// Written as a separate loader rather than as a change to that one, deliberately: <c>MeshData</c> is a
/// mesh, and giving it submeshes to carry a material list would push a material system into the type every
/// other importer returns. What the renderer actually wants is what the glTF path already hands it — a
/// sequence of (mesh, colour) pairs, one per material — so this produces exactly that and the two paths
/// meet at the same shape.
/// </para>
/// <para>
/// Untextured by assumption, because the packs this exists for are: no <c>vt</c>, one flat <c>Kd</c> per
/// material, and a material name that says what the surface is. That last part is the reason this is worth
/// having — <c>Green</c>, <c>Wood</c>, <c>Stone</c> are the same names the glTF pack uses, so a model loaded
/// through here classifies itself exactly as one loaded through that does.
/// </para>
/// </remarks>
public static class WavefrontParts
{
    /// <summary>One material's geometry and the colour the artist gave it.</summary>
    public readonly record struct Part(string Material, MeshData Mesh, Vector4 Color);

    /// <summary>
    /// Reads an OBJ and its MTL into one part per material.
    /// </summary>
    /// <remarks>
    /// Two passes over the faces rather than one, because a material's faces are not contiguous in a
    /// Blender export: it writes an object at a time and may return to a material later, so the groups have
    /// to be gathered before any of them can be built.
    /// </remarks>
    public static IReadOnlyList<Part> Import(string objPath, bool recenter = true)
    {
        ArgumentNullException.ThrowIfNull(objPath);

        var watch = Stopwatch.StartNew();
        var cookedPath = Path.ChangeExtension(objPath, ".blixmesh");
        var cooked = TryReadCooked(cookedPath, recenter, out var why);

        var result = cooked ?? ImportSource(objPath, recenter);

        // <b>Reported, because a load nobody reports is a load no instrument can judge.</b> Before
        // this, `blix check --cooked` answered "nothing here that this judges" over a directory of
        // .obj files whether they were cooked or not — the sweep was not blind to the FILES, it was
        // blind to the LOAD, because no .obj reader had ever said what it did.
        if (AssetLoadLog.Enabled)
        {
            AssetLoadLog.Report(new AssetLoadReport(
                SourcePath: objPath,
                CookedPath: cooked is not null ? cookedPath : null,
                Mode: cooked is not null ? AssetLoadMode.Cooked : AssetLoadMode.Source,
                Bytes: SafeLength(cooked is not null ? cookedPath : objPath),
                LoadMs: watch.Elapsed.TotalMilliseconds,
                Recipe: cooked is not null ? CookedFile.TryReadHeader(cookedPath)?.Stamp.Recipe : null,
                Warning: why));
        }

        return result;
    }

    /// <summary>
    /// The cooked parts, or null with <paramref name="why"/> saying what stopped it.
    /// </summary>
    /// <remarks>
    /// <b>The settings are checked, not just the file's existence.</b> Every other cooked-sibling
    /// check in this tree is <c>File.Exists</c> and gets away with it because the recipe and the
    /// loader happen to agree; here they demonstrably need not. This reader centres a model by
    /// default and <c>SettlementArt</c> loads <c>Villager.obj</c> through <c>ObjImporter</c> with
    /// <c>RecenterToOrigin = false</c> — so a cook whose glob widened by one directory would hand
    /// that consumer geometry shifted by half a bounding box, on machines that had cooked and
    /// nowhere else. The recipe stamps what it used; this refuses what does not match.
    /// </remarks>
    private static IReadOnlyList<Part>? TryReadCooked(string cookedPath, bool recenter, out string? why)
    {
        why = null;
        if (!File.Exists(cookedPath))
        {
            why = "no .blixmesh sibling — the OBJ was parsed";
            return null;
        }

        BlixMeshFile file;
        try
        {
            file = BlixMeshReader.Read(cookedPath);
        }
        catch (AssetImportException stale)
        {
            // <b>Reported, not thrown.</b> A cooked artifact written by an older format is a reason
            // to parse the OBJ, not a reason to take a game down — and because the reason travels
            // out through the load report, `blix check --cooked` names the file rather than leaving
            // it quietly slow.
            why = $"{stale.Message} — the OBJ was parsed";
            return null;
        }

        var wanted = $"recenter={(recenter ? 1 : 0)}";
        if (file.Cooked?.Stamp.Parameters.Contains(wanted, StringComparison.Ordinal) != true)
        {
            why = $"a .blixmesh sibling exists but was not cooked with {wanted} — the OBJ was parsed";
            return null;
        }

        var parts = new Part[file.Primitives.Count];
        var materials = file.MaterialTable;
        for (var i = 0; i < parts.Length; i++)
        {
            var p = file.Primitives[i];
            var lod0 = p.Lods[0];

            // A primitive whose material index is out of range takes white rather than throwing:
            // the colour is the part's dressing, and a cooked file that has lost its table is still
            // geometry a person can look at. The material NAME falls back to the primitive's, which
            // the recipe writes as the material name anyway.
            var material = p.MaterialIndex >= 0 && p.MaterialIndex < materials.Count
                ? materials[p.MaterialIndex]
                : null;

            parts[i] = new Part(
                material?.Name ?? p.Name,
                new MeshData(
                    p.Name,
                    p.VertexBytes,
                    lod0.Indices16 ?? Array.Empty<ushort>(),
                    file.Layout,
                    p.Bounds,
                    Indices32: lod0.Indices32),
                material?.BaseColorFactor ?? Vector4.One);
        }

        return parts;
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
    }

    /// <summary>
    /// Parses the OBJ, ignoring any cooked artifact beside it.
    /// </summary>
    /// <remarks>
    /// <b>What a RECIPE must call, and the reason this is public.</b> <see cref="Import"/> prefers a
    /// cooked sibling, which is right for a game and catastrophic for a cook: a recipe reading
    /// through it would consume its own previous output and re-cook that — the one mistake a recipe
    /// can make that still looks like it worked. It was caught only because a format version
    /// happened to change in the same commit; at a matching version it would have been silent.
    /// </remarks>
    public static IReadOnlyList<Part> ImportSource(string objPath, bool recenter = true)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        // Faces per material, each triplet a (position, normal) pair of indices into the lists above.
        var groups = new Dictionary<string, List<(int Position, int Normal)>>();
        var order = new List<string>();
        var current = "default";

        foreach (var raw in File.ReadLines(objPath))
        {
            var tokens = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens[0].StartsWith('#')) continue;
            switch (tokens[0])
            {
                case "v":
                    positions.Add(Vector3Of(tokens));
                    break;
                case "vn":
                    normals.Add(Vector3Of(tokens));
                    break;
                case "usemtl":
                    current = tokens.Length > 1 ? tokens[1] : "default";
                    break;
                case "f":
                    if (!groups.TryGetValue(current, out var faces))
                    {
                        groups[current] = faces = new List<(int, int)>();
                        order.Add(current);
                    }

                    Triangulate(tokens, faces);
                    break;
            }
        }

        // Recentre against the whole model, not per part: a canopy centred on its own bounds and a trunk
        // centred on its would arrive at the origin as two objects on top of each other.
        var offset = Vector3.Zero;
        if (recenter && positions.Count > 0)
        {
            offset = Bounds3.FromPoints(positions.ToArray()).Center;
        }

        var colors = ReadMaterials(objPath);
        var parts = new List<Part>(order.Count);
        foreach (var material in order)
        {
            var faces = groups[material];
            if (faces.Count == 0) continue;

            var lookup = new Dictionary<(int, int), ushort>();
            var vertices = new List<VertexPosition3NormalTexture>();
            var indices = new List<ushort>(faces.Count);
            var used = new List<Vector3>();
            foreach (var (position, normal) in faces)
            {
                if (!lookup.TryGetValue((position, normal), out var index))
                {
                    if (vertices.Count >= ushort.MaxValue)
                    {
                        throw new InvalidOperationException(
                            $"'{objPath}' material '{material}' exceeds {ushort.MaxValue} vertices; the " +
                            "index buffers here are uint16.");
                    }

                    var at = positions[position] - offset;
                    // No normals in the file is legal OBJ and means "flat"; deriving smooth ones per part
                    // would seam every material boundary, so a missing normal points up rather than
                    // pretending to knowledge the file does not contain.
                    var n = normal >= 0 && normal < normals.Count ? normals[normal] : Vector3.UnitY;
                    index = (ushort)vertices.Count;
                    vertices.Add(new VertexPosition3NormalTexture(
                        new GraphicsVector3(at.X, at.Y, at.Z),
                        new GraphicsVector3(n.X, n.Y, n.Z),
                        // Untextured pack: a constant beats a synthesized projection, which would only
                        // invent a seam for a sampler nobody binds.
                        new GraphicsVector2(0f, 0f)));
                    lookup[(position, normal)] = index;
                    used.Add(at);
                }

                indices.Add(index);
            }

            var name = Path.GetFileNameWithoutExtension(objPath);
            parts.Add(new Part(
                material,
                new MeshData(
                    Name: $"{name}.{material}",
                    VertexBytes: VertexPosition3NormalTexture.Pack(vertices),
                    Indices: indices.ToArray(),
                    Layout: VertexPosition3NormalTexture.Layout,
                    Bounds: Bounds3.FromPoints(used.ToArray())),
                colors.TryGetValue(material, out var color) ? color : new Vector4(0.6f, 0.6f, 0.6f, 1f)));
        }

        return parts;
    }

    /// <summary>
    /// Reads the sibling MTL for each material's diffuse colour.
    /// </summary>
    /// <remarks>
    /// <c>Kd</c> only. These packs carry a specular and an emissive too and neither means anything to a
    /// shader that has no specular model and no emission, so reading them would be storing a number to
    /// ignore it. The file is found beside the OBJ by name rather than by following <c>mtllib</c>, because
    /// an exported <c>mtllib</c> is sometimes an absolute path from the machine that exported it.
    /// </remarks>
    private static Dictionary<string, Vector4> ReadMaterials(string objPath)
    {
        var colors = new Dictionary<string, Vector4>(StringComparer.Ordinal);
        var mtlPath = Path.ChangeExtension(objPath, ".mtl");
        if (!File.Exists(mtlPath)) return colors;

        var material = string.Empty;
        foreach (var raw in File.ReadLines(mtlPath))
        {
            var tokens = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens[0].StartsWith('#')) continue;
            if (tokens[0] == "newmtl" && tokens.Length > 1) material = tokens[1];
            else if (tokens[0] == "Kd" && tokens.Length > 3 && material.Length > 0)
            {
                colors[material] = new Vector4(
                    Float(tokens[1]), Float(tokens[2]), Float(tokens[3]), 1f);
            }
        }

        return colors;
    }

    /// <summary>Fans a polygon into triangles, taking only position and normal per corner.</summary>
    private static void Triangulate(string[] tokens, List<(int Position, int Normal)> into)
    {
        var corners = new List<(int, int)>(tokens.Length - 1);
        for (var i = 1; i < tokens.Length; i++)
        {
            var fields = tokens[i].Split('/');
            var position = Index(fields.Length > 0 ? fields[0] : string.Empty);
            var normal = Index(fields.Length > 2 ? fields[2] : string.Empty);
            corners.Add((position, normal));
        }

        for (var i = 1; i + 1 < corners.Count; i++)
        {
            into.Add(corners[0]);
            into.Add(corners[i]);
            into.Add(corners[i + 1]);
        }
    }

    /// <summary>OBJ indices are one-based, and negative means "counting back from here".</summary>
    private static int Index(string field) =>
        int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value > 0 ? value - 1 : value
            : -1;

    private static Vector3 Vector3Of(string[] tokens) => tokens.Length < 4
        ? Vector3.Zero
        : new Vector3(Float(tokens[1]), Float(tokens[2]), Float(tokens[3]));

    private static float Float(string token) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0f;
}
