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
        var freshness = stamp.SourcePath.Length > 0
            ? CookedFile.Compare(header, stamp.SourcePath)
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

        return 0;
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
        return 0;
    }
}
