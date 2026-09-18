using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Cooked;
using Blix.Core;
using SharpGLTF.Schema2;

namespace Blix.Tools.Inspect;

/// <summary>
/// <c>blix inspect</c> — lists what is in an asset. Reports; never judges.
/// </summary>
/// <remarks>
/// <para>
/// <b>Moved out of the cooker, where it never belonged.</b> It was <c>blix-cook inspect</c>, sitting
/// beside three verbs that transform files while itself transforming nothing — the same finding
/// <c>check</c> had, which turned out to be two tools sharing a name. A cooker's verbs cook.
/// </para>
/// <para>
/// <b>The inspect/check distinction is deliberate and worth keeping.</b> This lists and always exits
/// 0; <c>blix check</c> judges and exits non-zero when something is wrong. They overlap in subject
/// and not in purpose, and collapsing them would mean either a lister that fails or a judge that
/// shrugs.
/// </para>
/// <para>
/// <b>It now inspects cooked artifacts too, which is new.</b> Before the shared preamble there was
/// no way to ask a <c>.blixmesh</c> what made it, so nothing did — and the one type that could have
/// reported it had four states and no emitters. One verb over both halves of the pipeline is what
/// the preamble bought.
/// </para>
/// </remarks>
public static class Program
{
    [BlixApp("inspect", Summary = "list what is in an asset — source or cooked. always exits 0")]
    public static int Main(string[] args)
    {
        if (args.Length < 1 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("Usage: blix inspect <file>");
            Console.WriteLine("  A .gltf/.glb  — the node hierarchy, each mesh node's composed-world");
            Console.WriteLine("                  scale/translation (= rig pivot) and assembled bounds.");
            Console.WriteLine("  A cooked file — what made it, from what, with which settings, and");
            Console.WriteLine("                  whether the source still matches.");
            Console.WriteLine();
            Console.WriteLine("Always exits 0: it reports, it does not judge. For a verdict, use `blix check`.");
            return args.Length < 1 ? 1 : 0;
        }

        var path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No file at {path}.");
            return 1;
        }

        // <b>Decided by what the file IS, not by a flag.</b> A cooked artifact announces itself in
        // its first four bytes, so asking is cheaper and more honest than making the caller say
        // which kind of thing they are holding — and it means a format added later is inspectable
        // on the day it exists, without this tool learning about it.
        if (CookedFile.TryReadHeader(path) is { } cooked) return InspectCooked(path, cooked);

        return InspectGltf(new[] { "inspect", path });
    }

    private static int InspectCooked(string path, CookedHeader header)
    {
        var stamp = header.Stamp;
        Console.WriteLine($"{Path.GetFileName(path)} — a Blix cooked artifact");
        Console.WriteLine($"  format    {CookPreamble.Describe(header.Magic)} v{header.FormatVersion}");
        Console.WriteLine($"  recipe    '{stamp.Recipe}' v{stamp.RecipeVersion}");
        Console.WriteLine($"  settings  {(stamp.Parameters.Length > 0 ? stamp.Parameters : "(none recorded)")}");
        Console.WriteLine($"  source    {(stamp.SourcePath.Length > 0 ? stamp.SourcePath : "(not recorded)")}");
        if (stamp.SourceSize > 0) Console.WriteLine($"            {stamp.SourceSize:N0} bytes at cook time");

        // The state that used to be invisible. The loader's whole check was File.Exists, so a
        // cooked file older than its source was preferred over the source for as long as it sat
        // there, and nothing anywhere could say so.
        // Resolved against the ARTIFACT's directory, because that is what the stamp is relative to.
        // Resolving against the working directory instead would report every artifact's source as
        // missing the moment you ran this from anywhere but the right folder.
        var resolved = stamp.SourcePath.Length > 0
            ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", stamp.SourcePath))
            : null;
        var freshness = resolved is not null
            ? CookedFile.Compare(header, resolved)
            : CookedFile.Freshness.Unknown;
        Console.WriteLine($"  source is {freshness switch
        {
            CookedFile.Freshness.Current => "unchanged since this was cooked",
            CookedFile.Freshness.Stale => "CHANGED since this was cooked — re-cook it",
            CookedFile.Freshness.SourceMissing => "gone",
            _ => "not comparable — this artifact recorded nothing to compare against",
        }}");

        if (stamp.SourceRequired)
        {
            Console.WriteLine();
            Console.WriteLine("  NOTE: this artifact declares the source is still REQUIRED at load.");
            Console.WriteLine("        It is an optimisation, not a replacement — the source must travel with it.");
        }

        // A cooked mesh's materials, for the same reason the raw path reports them: a cooked file
        // that silently defaults every extension while the raw file reads them is indistinguishable
        // from a working one until something renders wrong. Printing both makes the round trip
        // checkable by eye and by diff.
        if (header.Magic == Blix.Assets.BlixMesh.Magic)
        {
            try
            {
                var mesh = Blix.Assets.BlixMeshReader.Read(path);
                ReportCookedExtensions(mesh);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  (materials unreadable: {ex.Message})");
            }
        }

        return 0;
    }

    /// <summary>The cooked mirror of <see cref="ReportMaterialExtensions"/>, off a .blixmesh.</summary>
    private static void ReportCookedExtensions(Blix.Assets.BlixMeshFile mesh)
    {
        var rows = new List<string>();
        foreach (var m in mesh.MaterialTable)
        {
            var e = m.Ext;
            var parts = new List<string>();
            if (e.TransmissionFactor > 0f) parts.Add($"transmission {e.TransmissionFactor:0.##}");
            if (e.DiffuseTransmissionFactor > 0f)
                parts.Add($"diffuse-transmission {e.DiffuseTransmissionFactor:0.##} rgb({e.DiffuseTransmissionColorFactor.X:0.##},{e.DiffuseTransmissionColorFactor.Y:0.##},{e.DiffuseTransmissionColorFactor.Z:0.##})");
            if (e.SheenColorFactor != System.Numerics.Vector3.Zero)
                parts.Add($"sheen rgb({e.SheenColorFactor.X:0.##},{e.SheenColorFactor.Y:0.##},{e.SheenColorFactor.Z:0.##}) rough {e.SheenRoughnessFactor:0.##}");
            if (e.ThicknessFactor > 0f)
            {
                var atten = e.AttenuationDistance >= float.MaxValue || float.IsPositiveInfinity(e.AttenuationDistance)
                    ? "none" : $"{e.AttenuationDistance:0.##}";
                parts.Add($"volume thickness {e.ThicknessFactor:0.##} atten {atten}");
            }
            if (e.ClearcoatFactor > 0f) parts.Add($"clearcoat {e.ClearcoatFactor:0.##} rough {e.ClearcoatRoughnessFactor:0.##}");
            if (e.IridescenceFactor > 0f) parts.Add($"iridescence {e.IridescenceFactor:0.##} ior {e.IridescenceIor:0.##}");
            if (e.AnisotropyStrength > 0f) parts.Add($"anisotropy {e.AnisotropyStrength:0.##} rot {e.AnisotropyRotation:0.##}");
            if (Math.Abs(e.SpecularFactor - 1f) > 1e-4f) parts.Add($"specular {e.SpecularFactor:0.##}");
            if (Math.Abs(e.IndexOfRefraction - 1.5f) > 1e-4f) parts.Add($"ior {e.IndexOfRefraction:0.##}");
            if (e.Dispersion > 0f) parts.Add($"dispersion {e.Dispersion:0.##}");
            if (e.Unlit) parts.Add("unlit");
            if (parts.Count > 0) rows.Add($"    {m.Name,-28} {string.Join(" · ", parts)}");
        }
        if (rows.Count == 0) return;
        Console.WriteLine("  material extensions (KHR_materials_*):");
        foreach (var r in rows) Console.WriteLine(r);
    }

    private static int InspectGltf(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: blix-cook inspect <gltf-or-glb>");
            return 1;
        }
        var path = args[1];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 1;
        }

        var model = new Blix.GltfStaticImporter().ImportNodes(
            new Blix.Assets.AssetImportContext(Blix.Assets.AssetId.Parse("inspect"), path));
        var nodes = model.Nodes;

        // Compose a node's world transform by walking up its parent chain (row-vector:
        // child = local * parent). ImportNodes keeps every node in LOCAL space, so this
        // is where the assembled placement comes from.
        Matrix4x4 World(int i)
        {
            var m = nodes[i].LocalTransform;
            for (var p = nodes[i].ParentIndex; p >= 0; p = nodes[p].ParentIndex) m *= nodes[p].LocalTransform;
            return m;
        }

        var meshNodes = 0;
        foreach (var n in nodes) if (n.Primitives.Length > 0) meshNodes++;
        Console.WriteLine($"{Path.GetFileName(path)}: {nodes.Length} nodes, {meshNodes} mesh-bearing");
        Console.WriteLine("  (mesh nodes show composed-world scale/translation [= rig PIVOT] + assembled bounds)");

        // Hierarchy depth for indentation.
        int Depth(int i)
        {
            var d = 0;
            for (var p = nodes[i].ParentIndex; p >= 0; p = nodes[p].ParentIndex) d++;
            return d;
        }

        for (var i = 0; i < nodes.Length; i++)
        {
            var n = nodes[i];
            var indent = new string(' ', 2 + Depth(i) * 2);
            if (n.Primitives.Length == 0)
            {
                // Transform-only node — list it (it may be an armature pivot) but keep it terse.
                Console.WriteLine($"{indent}[{i,3}] {n.Name}  (no mesh, parent={n.ParentIndex})");
                continue;
            }

            var w = World(i);
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var verts = 0;
            foreach (var prim in n.Primitives)
            {
                var md = prim.Mesh;
                verts += md.VertexCount;
                var stride = md.Layout.Stride;
                for (var v = 0; v < md.VertexCount; v++)
                {
                    var o = v * stride;
                    var lp = new Vector3(
                        BitConverter.ToSingle(md.VertexBytes, o),
                        BitConverter.ToSingle(md.VertexBytes, o + 4),
                        BitConverter.ToSingle(md.VertexBytes, o + 8));
                    var wp = Vector3.Transform(lp, w);
                    min = Vector3.Min(min, wp); max = Vector3.Max(max, wp);
                }
            }
            Matrix4x4.Decompose(w, out var scale, out _, out var trans);
            Console.WriteLine(
                $"{indent}[{i,3}] {n.Name}  parent={n.ParentIndex} prims={n.Primitives.Length} verts={verts}");
            Console.WriteLine(
                $"{indent}      pivot/trans=({trans.X:0.###}, {trans.Y:0.###}, {trans.Z:0.###})  " +
                $"scale=({scale.X:0.###}, {scale.Y:0.###}, {scale.Z:0.###})");
            Console.WriteLine(
                $"{indent}      bounds X[{min.X:0.##}, {max.X:0.##}]  Y[{min.Y:0.##}, {max.Y:0.##}]  Z[{min.Z:0.##}, {max.Z:0.##}]");
        }

        ReportMaterialExtensions(nodes);
        return 0;
    }

    /// <summary>Prints the KHR_materials_* properties each material declares, and nothing it does not.</summary>
    /// <remarks>
    /// <b>Conventions §3: the runtime has to be able to EXPLAIN what it loaded.</b> Until the
    /// importer read these, "does Blix see the sheen on this chair" had no answer short of running
    /// a renderer and squinting — which is how eleven spec-defined properties sat unparsed without
    /// anyone noticing. A material that declares nothing prints nothing, so this stays quiet on the
    /// assets that have no extensions and is the whole report on the ones that do.
    /// </remarks>
    private static void ReportMaterialExtensions(IReadOnlyList<Blix.GltfNode> nodes)
    {
        var seen = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var n in nodes)
        foreach (var prim in n.Primitives)
        {
            var m = prim.Material;
            if (m is null || seen.ContainsKey(m.Name)) continue;
            var e = m.Ext;
            var parts = new List<string>();
            if (e.TransmissionFactor > 0f) parts.Add($"transmission {e.TransmissionFactor:0.##}");
            if (e.DiffuseTransmissionFactor > 0f)
                parts.Add($"diffuse-transmission {e.DiffuseTransmissionFactor:0.##} rgb({e.DiffuseTransmissionColorFactor.X:0.##},{e.DiffuseTransmissionColorFactor.Y:0.##},{e.DiffuseTransmissionColorFactor.Z:0.##})");
            if (e.SheenColorFactor != System.Numerics.Vector3.Zero)
                parts.Add($"sheen rgb({e.SheenColorFactor.X:0.##},{e.SheenColorFactor.Y:0.##},{e.SheenColorFactor.Z:0.##}) rough {e.SheenRoughnessFactor:0.##}");
            if (e.ThicknessFactor > 0f)
            {
                // float.MaxValue is how a glTF says "no attenuation" — the spec's default is
                // infinite distance, and printing it as 3.4e38 makes a correct read look like a bug.
                var atten = e.AttenuationDistance >= float.MaxValue || float.IsPositiveInfinity(e.AttenuationDistance)
                    ? "none" : $"{e.AttenuationDistance:0.##}";
                parts.Add($"volume thickness {e.ThicknessFactor:0.##} atten {atten}");
            }
            if (e.ClearcoatFactor > 0f) parts.Add($"clearcoat {e.ClearcoatFactor:0.##} rough {e.ClearcoatRoughnessFactor:0.##}");
            if (e.IridescenceFactor > 0f) parts.Add($"iridescence {e.IridescenceFactor:0.##} ior {e.IridescenceIor:0.##}");
            if (e.AnisotropyStrength > 0f) parts.Add($"anisotropy {e.AnisotropyStrength:0.##} rot {e.AnisotropyRotation:0.##}");
            if (Math.Abs(e.SpecularFactor - 1f) > 1e-4f) parts.Add($"specular {e.SpecularFactor:0.##}");
            if (Math.Abs(e.IndexOfRefraction - 1.5f) > 1e-4f) parts.Add($"ior {e.IndexOfRefraction:0.##}");
            if (e.Dispersion > 0f) parts.Add($"dispersion {e.Dispersion:0.##}");
            if (e.Unlit) parts.Add("unlit");
            if (parts.Count > 0) seen[m.Name] = parts;
        }
        if (seen.Count == 0) return;
        Console.WriteLine("  material extensions (KHR_materials_*):");
        foreach (var (name, parts) in seen)
            Console.WriteLine($"    {name,-28} {string.Join(" · ", parts)}");
    }
}
