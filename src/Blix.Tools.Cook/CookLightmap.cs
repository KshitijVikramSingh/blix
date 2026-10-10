using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Blix.Assets;
using Blix.Core;
using Blix.Recipes;

namespace Blix.Tools.Cook;

// blix cook lightmap <dir-of-blixmesh> [--texels-per-metre N] [--padding N] [--only <substring>]
//
// Stage 5 (lightmaps), step 1: unwrap every primitive a scene draws with xatlas and say how good the unwrap is -- it
// writes nothing yet. Per pack (the top directory under <dir>: Sponza's main_sponza, curtains, ivy, trees):
//   charts and their types; how many vertices the seams split; stretch, from each triangle's map from the surface to
//   the atlas -- conformal distortion (the ratio of its two scales) and density (texels per square metre against the
//   asked density), area-weighted; the atlas texels every instance would need at this density;
//   and LOD: a primitive's LOD levels index the SAME vertices, so per-vertex lightmap UVs reach them -- unless a
//   simplified triangle's corners sit in different charts after the split, when it has no one UV. Counted per level.
public static partial class Program
{
    static int CookLightmap(AppArgs args)
    {
        var texelsPerMetre = args.Float("texels-per-metre", 20f);
        var padding = args.Int("padding", 2);
        var only = args.String("only");
        // --lod-test: per primitive, today's LOD chain against one simplified with every chart border locked (the split
        // mesh, simplified with LockBorder: a simplified triangle then never crosses a chart). Positions only and no
        // pruning in BOTH arms (on the split mesh each chart is its own component, and pruning would delete charts):
        // what seam-locking costs, at the same ratios (MeshRecipe.LodRatios), in triangles reached and world error.
        var lodTest = args.Flag("lod-test");
        float[] lodRatios = { 0.5f, 0.25f, 0.125f };
        var lodBase = new long[lodRatios.Length]; var lodSeam = new long[lodRatios.Length]; long lodFull = 0;
        var lodErrBase = new List<double>[lodRatios.Length]; var lodErrSeam = new List<double>[lodRatios.Length];
        for (var l = 0; l < lodRatios.Length; l++) { lodErrBase[l] = new List<double>(); lodErrSeam[l] = new List<double>(); }
        if (args.Positionals is not [var root]) { Console.Error.WriteLine("Usage: blix cook lightmap <dir> [--texels-per-metre N]"); return 2; }
        var meshes = Directory.Exists(root) ? Directory.GetFiles(root, "*.blixmesh", SearchOption.AllDirectories) : new[] { root };
        if (meshes.Length == 0) { Console.Error.WriteLine($"No .blixmesh under {root}."); return 2; }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Unwrapping {meshes.Length} cooked mesh file(s) at {texelsPerMetre} texels/m ({100f / texelsPerMetre:0.#} cm), padding {padding}"));
        var clock = Stopwatch.StartNew();
        var groups = new SortedDictionary<string, Stats>(StringComparer.Ordinal);
        var worst = new List<(string Name, double Stretch90, int Charts, int Triangles, double LodMixed)>();
        foreach (var path in meshes.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (only is not null && !path.Contains(only, StringComparison.Ordinal)) continue;
            var rel = Directory.Exists(root) ? Path.GetRelativePath(root, path) : Path.GetFileName(path);
            var group = rel.Split(Path.DirectorySeparatorChar)[0];
            if (!groups.TryGetValue(group, out var g)) groups[group] = g = new Stats();
            var file = BlixMeshReader.Read(path);
            // Unique primitives (one unwrap each) and how many times each is drawn (atlas space for every one).
            var drawn = new Dictionary<BlixMeshPrimitive, (Matrix4x4 World, int Count)>(ReferenceEqualityComparer.Instance);
            var order = new List<BlixMeshPrimitive>();
            foreach (var (prim, _, world) in file.DrawnPrimitives())
            {
                if (drawn.TryGetValue(prim, out var e)) { drawn[prim] = (e.World, e.Count + 1); continue; }
                drawn[prim] = (world, 1);
                order.Add(prim);
            }
            var primIndex = 0;
            foreach (var prim in order)
            {
                var key = prim;
                var (world, instances) = drawn[key];
                int stride = prim.Layout.Stride;
                int vertexCount = prim.VertexCount;
                byte[] bytes = prim.VertexBytes;
                var positions = new Vector3[vertexCount];
                for (var v = 0; v < vertexCount; v++)
                {
                    var o = v * stride;
                    positions[v] = Vector3.Transform(new Vector3(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8)), world);
                }
                var lods = new List<uint[]>();
                foreach (var lod in prim.Lods)
                {
                    uint[]? i32 = lod.Indices32;
                    ushort[]? i16 = lod.Indices16;
                    lods.Add(i32 ?? i16!.Select(i => (uint)i).ToArray());
                }
                var indices = lods[0];
                if (indices.Length < 3) continue;
                LightmapUnwrap.Result r;
                try { r = LightmapUnwrap.Unwrap(positions, indices, texelsPerMetre, padding); }
                catch (InvalidOperationException ex) { Console.WriteLine($"  {rel} #{primIndex}: {ex.Message}"); g.Failed++; continue; }
                var tris = indices.Length / 3;
                g.Primitives++; g.Instances += instances; g.Triangles += tris; g.InVertices += vertexCount; g.OutVertices += r.Uv.Length;
                g.Charts += r.Charts;
                for (var t = 0; t < 5; t++) g.ChartTypes[t] += r.ChartTypes[t];
                // Per triangle: the map from the surface (an orthonormal frame in its plane) to atlas texels.
                var stretches = new List<(double Area, double Ratio, double Density)>(tris);
                var area = 0.0;
                for (var t = 0; t < tris; t++)
                {
                    uint o0 = r.Indices[3 * t], o1 = r.Indices[3 * t + 1], o2 = r.Indices[3 * t + 2];
                    Vector3 p0 = positions[r.Xref[o0]], p1 = positions[r.Xref[o1]], p2 = positions[r.Xref[o2]];
                    var e1 = p1 - p0; var e2 = p2 - p0;
                    var n = Vector3.Cross(e1, e2);
                    var a3 = n.Length() * 0.5;
                    if (a3 <= 1e-12) continue;
                    var tx = Vector3.Normalize(e1); var ty = Vector3.Normalize(Vector3.Cross(Vector3.Normalize(n), tx));
                    double x1 = e1.Length(), x2 = Vector3.Dot(e2, tx), y2 = Vector3.Dot(e2, ty);
                    var size = new Vector2(r.Width, r.Height);
                    Vector2 q0 = r.Uv[o0] * size, q1 = r.Uv[o1] * size, q2 = r.Uv[o2] * size;
                    Vector2 d1 = q1 - q0, d2 = q2 - q0;
                    // J maps (x, y) on the surface to (u, v) texels: columns J * (x1, 0) = d1, J * (x2, y2) = d2.
                    double j00 = d1.X / x1, j10 = d1.Y / x1;
                    double j01 = (d2.X - j00 * x2) / y2, j11 = (d2.Y - j10 * x2) / y2;
                    // Singular values of J.
                    double aa = j00 * j00 + j10 * j10, bb = j00 * j01 + j10 * j11, cc = j01 * j01 + j11 * j11;
                    double tr = aa + cc, det = Math.Sqrt(Math.Max(0, (aa - cc) * (aa - cc) + 4 * bb * bb));
                    double s1 = Math.Sqrt(Math.Max(0, (tr + det) / 2)), s2 = Math.Sqrt(Math.Max(0, (tr - det) / 2));
                    var ratio = s2 > 1e-9 ? s1 / s2 : 1e9;
                    var density = s1 * s2 / (texelsPerMetre * texelsPerMetre);
                    stretches.Add((a3, ratio, density));
                    area += a3;
                }
                g.Area += area * instances;
                foreach (var s in stretches) { g.StretchSamples.Add((s.Area * instances, s.Ratio)); g.DensitySamples.Add((s.Area * instances, s.Density)); }
                g.TexelsNeeded += area * instances * texelsPerMetre * texelsPerMetre / Math.Max(0.05f, r.Utilization);
                // LOD: an input vertex is in every chart one of its output copies is in; a simplified triangle has one
                // UV only if its three corners share a chart.
                var chartsOf = new Dictionary<int, HashSet<int>>();
                for (var v = 0; v < r.Xref.Length; v++)
                {
                    if (!chartsOf.TryGetValue(r.Xref[v], out var set)) chartsOf[r.Xref[v]] = set = new HashSet<int>();
                    set.Add(r.VertexChart[v]);
                }
                var mixedWorst = 0.0;
                for (var l = 1; l < lods.Count; l++)
                {
                    var li = lods[l];
                    long mixed = 0, total = li.Length / 3;
                    for (var t = 0; t + 2 < li.Length; t += 3)
                    {
                        if (!chartsOf.TryGetValue((int)li[t], out var c0) || !chartsOf.TryGetValue((int)li[t + 1], out var c1) || !chartsOf.TryGetValue((int)li[t + 2], out var c2)) { mixed++; continue; }
                        if (!c0.Any(c => c1.Contains(c) && c2.Contains(c))) mixed++;
                    }
                    while (g.LodMixed.Count < l) { g.LodMixed.Add(0); g.LodTotal.Add(0); }
                    g.LodMixed[l - 1] += mixed; g.LodTotal[l - 1] += total;
                    mixedWorst = Math.Max(mixedWorst, total > 0 ? mixed / (double)total : 0);
                }
                if (lodTest)
                {
                    var flat = new float[positions.Length * 3];
                    for (var v = 0; v < positions.Length; v++) { flat[3 * v] = positions[v].X; flat[3 * v + 1] = positions[v].Y; flat[3 * v + 2] = positions[v].Z; }
                    var split = new float[r.Xref.Length * 3];
                    for (var v = 0; v < r.Xref.Length; v++) { var q = positions[r.Xref[v]]; split[3 * v] = q.X; split[3 * v + 1] = q.Y; split[3 * v + 2] = q.Z; }
                    var scaleBase = MeshoptNative.SimplifyScale(flat, positions.Length, 3);
                    var scaleSeam = MeshoptNative.SimplifyScale(split, r.Xref.Length, 3);
                    lodFull += tris;
                    for (var l = 0; l < lodRatios.Length; l++)
                    {
                        var b = MeshoptNative.Simplify(indices, flat, positions.Length, 3, lodRatios[l], 1f, MeshoptNative.Options.LockBorder, out var eb);
                        var sIdx = MeshoptNative.Simplify(r.Indices, split, r.Xref.Length, 3, lodRatios[l], 1f, MeshoptNative.Options.LockBorder, out var es);
                        lodBase[l] += b.Length / 3; lodSeam[l] += sIdx.Length / 3;
                        lodErrBase[l].Add(eb * scaleBase); lodErrSeam[l].Add(es * scaleSeam);
                    }
                }
                var p90 = WeightedPercentile(stretches.Select(s => (s.Area, s.Ratio)).ToList(), 0.9);
                worst.Add(($"{rel} #{primIndex}", p90, r.Charts, tris, mixedWorst));
                primIndex++;
            }
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Unwrapped in {clock.Elapsed.TotalSeconds:0.0} s."));
        foreach (var (name, g) in groups)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {name}: {g.Primitives:N0} primitives ({g.Instances:N0} drawn, {g.Failed} failed), {g.Triangles:N0} triangles, {g.Area:N0} m2 drawn"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    charts {g.Charts:N0} (planar {g.ChartTypes[0]:N0}, ortho {g.ChartTypes[1]:N0}, LSCM {g.ChartTypes[2]:N0}, piecewise {g.ChartTypes[3]:N0}); {g.Triangles / (double)Math.Max(1, g.Charts):0.0} triangles a chart; vertices {g.InVertices:N0} -> {g.OutVertices:N0} (x{g.OutVertices / (double)Math.Max(1, g.InVertices):0.00} at seams)"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    conformal stretch (scale ratio, area-weighted): median {WeightedPercentile(g.StretchSamples, 0.5):0.00}, p90 {WeightedPercentile(g.StretchSamples, 0.9):0.00}, p99 {WeightedPercentile(g.StretchSamples, 0.99):0.00}; density vs asked: p10 {WeightedPercentile(g.DensitySamples, 0.1):0.00}, median {WeightedPercentile(g.DensitySamples, 0.5):0.00}, p90 {WeightedPercentile(g.DensitySamples, 0.9):0.00}"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    atlas texels for every instance at this density (by packing utilization): {g.TexelsNeeded / 1e6:0.0} M (~{Math.Sqrt(g.TexelsNeeded):0} squared)"));
            for (var l = 0; l < g.LodMixed.Count; l++)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"    LOD {l + 1}: {g.LodMixed[l]:N0} of {g.LodTotal[l]:N0} simplified triangles have corners in no common chart ({100.0 * g.LodMixed[l] / Math.Max(1, g.LodTotal[l]):0.0}%)"));
        }
        if (lodTest && lodFull > 0)
        {
            static double Pct(List<double> v, double q) { if (v.Count == 0) return double.NaN; var s = v.OrderBy(x => x).ToList(); return s[Math.Min(s.Count - 1, (int)(q * s.Count))]; }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  LOD with chart borders locked, against today's (positions only, no pruning, borders locked in both), {lodFull:N0} triangles at LOD 0:"));
            for (var l = 0; l < lodRatios.Length; l++)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"    ratio {lodRatios[l]}: today {lodBase[l]:N0} triangles ({100.0 * lodBase[l] / lodFull:0.0}%), seam-locked {lodSeam[l]:N0} ({100.0 * lodSeam[l] / lodFull:0.0}%); world error per primitive median {1000 * Pct(lodErrBase[l], 0.5):0.00} -> {1000 * Pct(lodErrSeam[l], 0.5):0.00} mm, p90 {1000 * Pct(lodErrBase[l], 0.9):0.00} -> {1000 * Pct(lodErrSeam[l], 0.9):0.00} mm"));
        }
        Console.WriteLine("  worst primitives by conformal stretch p90:");
        foreach (var w in worst.OrderByDescending(w => w.Stretch90).Take(10))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {w.Name}: p90 {w.Stretch90:0.00}, {w.Charts:N0} charts over {w.Triangles:N0} triangles, LOD mixed up to {100 * w.LodMixed:0.0}%"));
        return 0;
    }

    sealed class Stats
    {
        public int Primitives, Instances, Failed, Charts;
        public long Triangles, InVertices, OutVertices;
        public double Area, TexelsNeeded;
        public readonly int[] ChartTypes = new int[5];
        public readonly List<(double Weight, double Value)> StretchSamples = new(), DensitySamples = new();
        public readonly List<long> LodMixed = new(), LodTotal = new();
    }

    static double WeightedPercentile(List<(double Weight, double Value)> samples, double q)
    {
        if (samples.Count == 0) return double.NaN;
        var sorted = samples.OrderBy(s => s.Value).ToList();
        var total = sorted.Sum(s => s.Weight);
        var acc = 0.0;
        foreach (var s in sorted) { acc += s.Weight; if (acc >= q * total) return s.Value; }
        return sorted[^1].Value;
    }
}
