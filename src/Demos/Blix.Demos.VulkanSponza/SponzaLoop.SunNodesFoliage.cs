using System.Numerics;
using Blix.Geometry;

namespace Blix.Demos.VulkanSponza;

// Sun nodes: the foliage's own sun points. A cut-out surface (a leaf card: partly covered in the ray scene) carries no
// texels, so a gather ray meeting one found no sun bits there and took no sun -- ~20% of Sponza's hits, and the yellow-
// green the sunlit tree throws onto the arcade was missing from every node (2026-10-12, viz channel 10 against the
// bake). Here every partly covered triangle is sampled about once a texel cell (its area over the cell's square, at
// most 64), each point keyed as a texel is (its cell, the bin of its normal) on BOTH sides -- a leaf is met from either
// -- the first point of a key keeps it. Their sun bits are shot as the texels' are (SunNodeBitsFor); a hit among no
// texel looks among these (texel_nodes.glsl, nodeSetAt, set 1). The discovery pass adds to the same set wherever a
// hit still finds neither (SponzaLoop.SunNodes.cs: MergeSunPoints) -- surfaces the texels miss for any reason.
internal sealed partial class SponzaLoop
{
    private const int FoliageMaxSamples = 64;

    // Points (two vec4s each: position with w 4 -- a whole cell's area to texel.glsl's weighing -- and normal) and their
    // hash (a point id a slot, NODE_EMPTY free).
    private static Vector4[] BuildFoliagePoints(RayQueryGpuData data, uint[] surfaces, Vector4 grid)
    {
        var rows = data.Triangles.Length / 4;
        var bases = data.Instances.Select(i => i.TriangleBase).Distinct().Order().ToArray();
        var cell = grid.W;
        var origin = new Vector3(grid.X, grid.Y, grid.Z);
        var perEntry = new List<(ulong Key, Vector4 P, Vector4 N)>[data.Instances.Length];
        Parallel.For(0, data.Instances.Length, e =>
        {
            var entry = data.Instances[e];
            var list = new List<(ulong, Vector4, Vector4)>();
            perEntry[e] = list;
            var first = (int)entry.TriangleBase;
            var next = Array.BinarySearch(bases, entry.TriangleBase) + 1;
            var end = next < bases.Length ? (int)bases[next] : rows;
            var toWorld = Matrix4x4.Identity;
            if (entry.Placement != RayQueryGpuData.RegionPlacement && !Matrix4x4.Invert(entry.WorldToLocal, out toWorld)) return;
            Vector3 P(uint v) => Vector3.Transform(new Vector3(data.Positions[v].X, data.Positions[v].Y, data.Positions[v].Z), toWorld);
            for (var row = first; row < end; row++)
            {
                var coverage = surfaces[row] >> 24;
                if (coverage == 0 || coverage == 255) continue;
                Vector3 a = P(data.Triangles[row * 4]), b = P(data.Triangles[row * 4 + 1]), c = P(data.Triangles[row * 4 + 2]);
                var cross = Vector3.Cross(b - a, c - a);
                var area = cross.Length() * 0.5f;
                if (area < 1e-10f) continue;
                var n = cross / (2f * area);
                var m = (int)Math.Clamp(MathF.Ceiling(MathF.Sqrt(area / (cell * cell))), 1, (int)MathF.Sqrt(FoliageMaxSamples));
                for (var i = 0; i < m; i++)
                for (var j = 0; j < m; j++)
                {
                    float r1 = (i + 0.5f) / m, r2 = (j + 0.5f) / m, s = MathF.Sqrt(r1);
                    var p = a * (1f - s) + b * (s * (1f - r2)) + c * (s * r2);
                    var f = (p - origin) / cell;
                    int cx = (int)MathF.Floor(f.X), cy = (int)MathF.Floor(f.Y), cz = (int)MathF.Floor(f.Z);
                    if (cx < 0 || cy < 0 || cz < 0 || cx > 65535 || cy > 65535 || cz > 65535) continue;
                    foreach (var side in new[] { n, -n })
                    {
                        var (lo, hi) = TexelKey(cx, cy, cz, TexelBin(side), 0);
                        list.Add((((ulong)hi << 32) | lo, new Vector4(p, 4f), new Vector4(side, 0f)));
                    }
                }
            }
        });
        var seen = new HashSet<ulong>();
        var points = new List<Vector4>();
        foreach (var list in perEntry)
        {
            if (list is null) continue;
            foreach (var (key, p, n) in list)
            {
                if (!seen.Add(key)) continue;
                points.Add(p);
                points.Add(n);
            }
        }
        return points.ToArray();
    }

    // A sun point's key, as a texel's (its cell, its normal's bin); null outside the grid.
    private static ulong? SunPointKey(Vector4 p, Vector4 n, Vector4 grid)
    {
        var f = (new Vector3(p.X, p.Y, p.Z) - new Vector3(grid.X, grid.Y, grid.Z)) / grid.W;
        int cx = (int)MathF.Floor(f.X), cy = (int)MathF.Floor(f.Y), cz = (int)MathF.Floor(f.Z);
        if (cx < 0 || cy < 0 || cz < 0 || cx > 65535 || cy > 65535 || cz > 65535) return null;
        var (lo, hi) = TexelKey(cx, cy, cz, TexelBin(Vector3.Normalize(new Vector3(n.X, n.Y, n.Z))), 0);
        return ((ulong)hi << 32) | lo;
    }

    // The points' hash (a point id a slot, 0xFFFFFFFF free), as texel_nodes.glsl's nodeFind probes it.
    private static uint[] SunPointHash(Vector4[] points, Vector4 grid)
    {
        var count = points.Length / 2;
        var slots = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(count * 2, 1024));
        var hash = new uint[slots];
        Array.Fill(hash, 0xFFFFFFFFu);
        for (var id = 0; id < count; id++)
        {
            if (SunPointKey(points[2 * id], points[2 * id + 1], grid) is not { } key) continue;
            var slot = TexelHash((uint)key, (uint)(key >> 32)) & (uint)(slots - 1);
            while (hash[slot] != 0xFFFFFFFFu) slot = (slot + 1) & (uint)(slots - 1);
            hash[slot] = (uint)id;
        }
        return hash;
    }

    // New points (two vec4s each) whose keys the set does not hold yet, the first of a key kept.
    private static Vector4[] NewSunPoints(Vector4[] existing, Vector4[] found, Vector4 grid)
    {
        var keys = new HashSet<ulong>();
        for (var i = 0; i < existing.Length / 2; i++)
            if (SunPointKey(existing[2 * i], existing[2 * i + 1], grid) is { } k) keys.Add(k);
        var added = new List<Vector4>();
        for (var i = 0; i < found.Length / 2; i++)
            if (SunPointKey(found[2 * i], found[2 * i + 1], grid) is { } k && keys.Add(k)) { added.Add(found[2 * i]); added.Add(found[2 * i + 1]); }
        return added.ToArray();
    }
}
