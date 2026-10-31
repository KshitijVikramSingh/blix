using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace Blix.Demos.VulkanSponza;

// The cooked cells' surface pieces, at every level: within each cell's cube, its surface joined where it runs on, one
// plane, unbroken -- a wall and a face a gap in front of it, facing alike, two pieces; a floor on both sides of a wall's
// foot, two pieces (the buried points under the wall dropped, the walk's gap between them). A cache cell is one piece
// (its region: 0 a cube's only piece, else 1 + its index), its light the piece's alone. They were the level-0 cells'
// regions, the first six levels alone: an open wall held no level-0 cell, region 0 everywhere, and one coarse cell held
// the lit courtyard wall and the dark faces of the panels' gap in front of it (the readout's view 35 blue across it, the
// user, the crevices testbed, 2026-10-31). rtgi_cook.glsl's cookPieceAt finds a position's piece: the cube's piece
// whose 4 x 4 footprint across the cube holds it, the nearest along the normal of those, else the nearest.
//
// The walk's kept points pooled first, by side, into surfels: the quarter-cubes of their own level's grid (a cell's
// 4 x 4 across, 4 deep) -- their area, centroid and normal summed. Joined as points within their spacing, 8.6 M points
// cost 40 s on the arches testbed (2026-10-31); surfels join their grid neighbours by lookup.
internal sealed partial class SponzaLoop
{
    // A surfel: its points' summed position, normal and area, their count.
    private sealed class CookSurfel
    {
        public Vector3 SumP, SumN;
        public float Area;
        public int Count;
        public void Add(Vector3 p, Vector3 n, float area) { SumP += p; SumN += n; Area += area; Count++; }
        public void Add(CookSurfel o) { SumP += o.SumP; SumN += o.SumN; Area += o.Area; Count += o.Count; }
    }

    // A piece of a cube: the sum of its surfels' centroids, their count (its centroid), its footprint (bit u + 4 v of the
    // cube's 4 x 4 across its side), its own area at the cube's level and its whole (every surfel at that level or below).
    private sealed class CookPiece
    {
        public Vector3 Sum;
        public int Count;
        public uint Mask;
        public float Own, Whole;
        // Its lowest surfel (its representative), and its index among its cube's pieces; the piece a level up holding it
        // (its index among that cube's pieces; 0xFFFF none).
        public int Rep = int.MaxValue, Index;
        public int Parent = 0xFFFF;
        public Vector3 Centroid => Sum / Math.Max(Count, 1);
    }

    // A surfel's key: its level, side and quarter-cube coordinates on its level's grid.
    private static long CookSurfelKey(int level, uint side, int x, int y, int z) =>
        ((long)level << 60) | ((long)side << 57) | (((long)(x + (1 << 18)) & 0x7FFFF) << 38) | (((long)(y + (1 << 18)) & 0x7FFFF) << 19) | ((long)(z + (1 << 18)) & 0x7FFFF);
    private static (int Level, uint Side, int X, int Y, int Z) CookSurfelUnkey(long k) =>
        ((int)(k >> 60), (uint)((k >> 57) & 7), (int)((k >> 38) & 0x7FFFF) - (1 << 18), (int)((k >> 19) & 0x7FFFF) - (1 << 18), (int)(k & 0x7FFFF) - (1 << 18));

    // A kept point into its surfel (the cells' walk, CookWalk's sink).
    private static void AddCookSurfel(Dictionary<long, CookSurfel> surfels, Vector3 p, Vector3 n, float area, uint side, int level, float finest)
    {
        var q = (p - NearGridOrigin(side)) / (finest * MathF.Pow(2f, level)) * 4f;
        var key = CookSurfelKey(level, side, (int)MathF.Floor(q.X), (int)MathF.Floor(q.Y), (int)MathF.Floor(q.Z));
        if (!surfels.TryGetValue(key, out var s)) surfels[key] = s = new CookSurfel();
        s.Add(p, n, area);
    }

    // Surfels within this many of the larger spacing of the two, on one plane to a share of it, join.
    private const float CookPieceReach = 1.8f, CookPiecePlane = 0.35f;

    // Per level, per cube (CensusCellKey), its pieces: largest whole area first.
    private static Dictionary<long, CookPiece[]>[] BuildCookPieces(Dictionary<long, CookSurfel> surfelMap, float finest, int levels, out double ms)
    {
        var clock = Stopwatch.StartNew();
        // In key order, so the pieces come out the same every cook.
        var keys = surfelMap.Keys.ToArray();
        Array.Sort(keys);
        var n = keys.Length;
        var index = new Dictionary<long, int>(n);
        for (var i = 0; i < n; i++) index[keys[i]] = i;
        var lv = new int[n];
        var sd = new uint[n];
        var c = new Vector3[n];
        var nrm = new Vector3[n];
        var area = new float[n];
        var h = new float[n];
        for (var i = 0; i < n; i++)
        {
            var (l, side, _, _, _) = CookSurfelUnkey(keys[i]);
            var s = surfelMap[keys[i]];
            lv[i] = l; sd[i] = side;
            c[i] = s.SumP / s.Count;
            nrm[i] = s.SumN.LengthSquared() > 1e-12f ? Vector3.Normalize(s.SumN) : Vector3.UnitY;
            area[i] = s.Area;
            h[i] = 0.25f * finest * MathF.Pow(2f, l);
        }
        // The joins: each surfel's grid neighbours at its level (from the lower index), and at the next level up's.
        var edgeLists = new List<(int, int)>[Environment.ProcessorCount];
        for (var t = 0; t < edgeLists.Length; t++) edgeLists[t] = new();
        Parallel.For(0, edgeLists.Length, t =>
        {
            var mine = edgeLists[t];
            for (var i = t; i < n; i += edgeLists.Length)
            {
                var (l, side, x, y, z) = CookSurfelUnkey(keys[i]);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dz = -1; dz <= 1; dz++)
                        {
                            if (index.TryGetValue(CookSurfelKey(l, side, x + dx, y + dy, z + dz), out var j) && j > i && Joins(i, j)) mine.Add((i, j));
                            if (l + 1 < levels && index.TryGetValue(CookSurfelKey(l + 1, side, (x >> 1) + dx, (y >> 1) + dy, (z >> 1) + dz), out var k) && Joins(i, k)) mine.Add((i, k));
                        }
            }
        });
        var edges = edgeLists.SelectMany(e => e).ToArray();
        var edgeMs = clock.Elapsed.TotalMilliseconds;
        // Per level: the joins within a cube unioned, each cube's pieces from its surfels' roots; each surfel's piece's
        // index in its cube kept (the parents, below).
        var result = new Dictionary<long, CookPiece[]>[levels];
        var pieceOf = new ushort[levels][];
        Parallel.For(0, levels, l =>
        {
            var cs = finest * MathF.Pow(2f, l);
            var cube = new (int X, int Y, int Z)[n];
            for (var i = 0; i < n; i++)
            {
                var g = (c[i] - NearGridOrigin(sd[i])) / cs;
                cube[i] = ((int)MathF.Floor(g.X), (int)MathF.Floor(g.Y), (int)MathF.Floor(g.Z));
            }
            var parent = new int[n];
            for (var i = 0; i < n; i++) parent[i] = i;
            foreach (var (a, b) in edges)
            {
                if (cube[a] != cube[b]) continue;
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
            var cubes = new Dictionary<long, Dictionary<int, CookPiece>>();
            for (var i = 0; i < n; i++)
            {
                var key = CensusCellKey(cube[i].X, cube[i].Y, cube[i].Z, sd[i]);
                if (!cubes.TryGetValue(key, out var pieces)) cubes[key] = pieces = new Dictionary<int, CookPiece>(1);
                var root = Find(i);
                if (!pieces.TryGetValue(root, out var piece)) pieces[root] = piece = new CookPiece();
                piece.Sum += c[i];
                piece.Count++;
                piece.Rep = Math.Min(piece.Rep, i);
                if (lv[i] == l) piece.Own += area[i];
                if (lv[i] <= l) piece.Whole += area[i];
                // Its footprint, its spacing wide, across the cube's 4 x 4.
                int axis = (int)(sd[i] >> 1), a0 = (axis + 1) % 3, a1 = (axis + 2) % 3;
                var g = (c[i] - NearGridOrigin(sd[i])) / cs;
                float u = Axis(g, a0) - CubeAxis(cube[i], a0), v = Axis(g, a1) - CubeAxis(cube[i], a1);
                var hw = 0.5f * h[i] / cs;
                int u0 = Math.Clamp((int)MathF.Floor((u - hw) * 4f), 0, 3), u1 = Math.Clamp((int)MathF.Floor((u + hw) * 4f), 0, 3);
                int v0 = Math.Clamp((int)MathF.Floor((v - hw) * 4f), 0, 3), v1 = Math.Clamp((int)MathF.Floor((v + hw) * 4f), 0, 3);
                for (var su = u0; su <= u1; su++)
                    for (var sv = v0; sv <= v1; sv++)
                        piece.Mask |= 1u << (su + 4 * sv);
            }
            var mine = new Dictionary<long, CookPiece[]>(cubes.Count);
            var ofRoot = new Dictionary<int, int>();
            foreach (var (key, pieces) in cubes)
            {
                var sorted = pieces.Values.OrderByDescending(p => p.Whole).ThenBy(p => p.Rep).ToArray();
                for (var k = 0; k < sorted.Length; k++) sorted[k].Index = k;
                foreach (var (root, piece) in pieces) ofRoot[root] = piece.Index;
                mine[key] = sorted;
            }
            var of = new ushort[n];
            for (var i = 0; i < n; i++) of[i] = (ushort)Math.Min(ofRoot[Find(i)], 0xFFFE);
            pieceOf[l] = of;
            result[l] = mine;

            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
        });
        // Each piece's parent: the piece a level up its representative lies in.
        for (var l = 0; l + 1 < levels; l++)
            foreach (var pieces in result[l].Values)
                foreach (var piece in pieces)
                    piece.Parent = pieceOf[l + 1][piece.Rep];
        ms = clock.Elapsed.TotalMilliseconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] rtgi cook: surface pieces: {n:N0} surfels, {edges.Length:N0} joins in {edgeMs:0} ms, {levels} levels in {ms - edgeMs:0} ms; cubes of several pieces by level {string.Join("/", result.Select(r => r.Values.Count(v => v.Length > 1)))}, the most in one by level {string.Join("/", result.Select(r => r.Count == 0 ? 0 : r.Values.Max(v => v.Length)))}."));
        return result;

        bool Joins(int a, int b)
        {
            var hm = MathF.Max(h[a], h[b]);
            var d = c[b] - c[a];
            if (d.LengthSquared() > CookPieceReach * CookPieceReach * hm * hm) return false;
            return MathF.Abs(Vector3.Dot(nrm[a], d)) <= CookPiecePlane * hm + 0.003f && Vector3.Dot(nrm[a], nrm[b]) >= 0.7f;
        }
        static float Axis(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
        static float CubeAxis((int X, int Y, int Z) v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
    }
}
