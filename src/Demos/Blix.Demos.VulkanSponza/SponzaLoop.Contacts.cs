using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Blix.Geometry;

namespace Blix.Demos.VulkanSponza;

// Coplanar contacts. Where two solids touch (a block on the floor, a wall against the next box), two faces lie in one
// plane facing opposite ways, and a ray reaching the plane meets both at the same distance: which one the closest hit
// reports is the traversal's tie-break, about half of each. A ray travelling inside one of the solids then reads the
// other's FRONT face about half the time, and every rule that asks "inside a solid?" by counting back faces (patches,
// hashed texels, the lightmap's records) loses those rays: patches survived inside the crevices' corner block at the
// margin of their half-of-16 rule, and their rays were the cook's misses there (2026-10-13).
//
// The contact table (built once per scene): every triangle's opposite partners -- triangles of the same plane facing
// the other way whose bounds overlap its own. A front-face hit is a contact when the hit point lies inside one of its
// triangle's partners (a per-point test: the crevices' floor is one large triangle, mostly exposed, partly under a
// block), and is reported as that partner's back face. Exact, and no extra rays. Re-asking every front-face hit along
// jittered directions (the first form) made Sponza's hashed texel cook several times slower (its every sample's ray
// along the normal ends on some front face); the jitter now runs only on triangles that have partners, as a backstop.
internal sealed partial class SponzaLoop
{
    // Planes match within ContactAngle in normal and ContactDistance along it; a hit point counts as inside a partner
    // within ContactDistance of its edges.
    private const float ContactDistance = 2e-3f;
    private const float ContactNormalBins = 256f;

    private sealed class ContactTable
    {
        // Partners by (instance, triangle): each a world-space triangle and whose it is.
        public readonly Dictionary<(int Instance, int Triangle), List<(int Instance, int Triangle, Vector3 A, Vector3 B, Vector3 C)>> Partners = new();
    }

    private ContactTable? contactTable;
    private RayQueryScene? contactScene;
    private readonly object contactGate = new();

    private ContactTable Contacts(RayQueryScene scene)
    {
        lock (contactGate)
        {
            if (contactTable is not null && ReferenceEquals(contactScene, scene)) return contactTable;
            contactTable = BuildContacts(scene);
            contactScene = scene;
            return contactTable;
        }
    }

    private static ContactTable BuildContacts(RayQueryScene scene)
    {
        var clock = Stopwatch.StartNew();
        // Each triangle's plane, keyed with the normal made canonical (its first non-zero axis positive) so a face and
        // its opposite share a key; the side kept apart. Distance bins of ContactDistance; matched against the bins
        // either side too, so a plane on a bin edge still finds its partner.
        var keys = new List<(ulong Key, long Bin, int Instance, int Triangle, bool Flipped)>();
        for (var i = 0; i < scene.Instances.Count; i++)
        {
            var inst = scene.Instances[i];
            var m = inst.Mesh;
            for (var t = 0; t < m.Indices.Length / 3; t++)
            {
                var a = Vector3.Transform(m.Positions[m.Indices[t * 3]], inst.World);
                var b = Vector3.Transform(m.Positions[m.Indices[t * 3 + 1]], inst.World);
                var c = Vector3.Transform(m.Positions[m.Indices[t * 3 + 2]], inst.World);
                var cross = Vector3.Cross(b - a, c - a);
                var len = cross.Length();
                if (len < 1e-10f) continue;
                var n = cross / len;
                var flipped = n.X < -1e-6f || (MathF.Abs(n.X) <= 1e-6f && (n.Y < -1e-6f || (MathF.Abs(n.Y) <= 1e-6f && n.Z < 0f)));
                if (flipped) n = -n;
                var d = Vector3.Dot(n, a);
                var qx = (ulong)(long)MathF.Round(n.X * ContactNormalBins) & 0xFFFFF;
                var qy = (ulong)(long)MathF.Round(n.Y * ContactNormalBins) & 0xFFFFF;
                var qz = (ulong)(long)MathF.Round(n.Z * ContactNormalBins) & 0xFFFFF;
                keys.Add((qx | (qy << 20) | (qz << 40), (long)MathF.Floor(d / ContactDistance), i, t, flipped));
            }
        }
        keys.Sort((x, y) => x.Key != y.Key ? x.Key.CompareTo(y.Key) : x.Bin.CompareTo(y.Bin));
        var table = new ContactTable();
        (Vector3 A, Vector3 B, Vector3 C) World(int i, int t)
        {
            var inst = scene.Instances[i];
            var m = inst.Mesh;
            return (Vector3.Transform(m.Positions[m.Indices[t * 3]], inst.World), Vector3.Transform(m.Positions[m.Indices[t * 3 + 1]], inst.World),
                Vector3.Transform(m.Positions[m.Indices[t * 3 + 2]], inst.World));
        }
        long pairs = 0, tested = 0;
        var start = 0;
        while (start < keys.Count)
        {
            var end = start;
            while (end < keys.Count && keys[end].Key == keys[start].Key) end++;
            // Within one normal: the faces of either side, by bin; each front-side face against the back-side faces in
            // its bin and the two beside it, where the bounds overlap.
            var front = new Dictionary<long, List<int>>();
            var back = new Dictionary<long, List<int>>();
            for (var k = start; k < end; k++)
            {
                var into = keys[k].Flipped ? back : front;
                if (!into.TryGetValue(keys[k].Bin, out var list)) into[keys[k].Bin] = list = new List<int>();
                list.Add(k);
            }
            if (back.Count > 0)
                foreach (var (bin, fronts) in front)
                    for (var db = -1L; db <= 1L; db++)
                    {
                        if (!back.TryGetValue(bin + db, out var backs)) continue;
                        foreach (var f in fronts)
                        {
                            var fa = World(keys[f].Instance, keys[f].Triangle);
                            var fMin = Vector3.Min(fa.A, Vector3.Min(fa.B, fa.C)) - new Vector3(ContactDistance);
                            var fMax = Vector3.Max(fa.A, Vector3.Max(fa.B, fa.C)) + new Vector3(ContactDistance);
                            foreach (var g in backs)
                            {
                                tested++;
                                var ga = World(keys[g].Instance, keys[g].Triangle);
                                var gMin = Vector3.Min(ga.A, Vector3.Min(ga.B, ga.C));
                                var gMax = Vector3.Max(ga.A, Vector3.Max(ga.B, ga.C));
                                if (gMin.X > fMax.X || gMin.Y > fMax.Y || gMin.Z > fMax.Z || gMax.X < fMin.X || gMax.Y < fMin.Y || gMax.Z < fMin.Z) continue;
                                Add(table, keys[f].Instance, keys[f].Triangle, keys[g].Instance, keys[g].Triangle, ga);
                                Add(table, keys[g].Instance, keys[g].Triangle, keys[f].Instance, keys[f].Triangle, fa);
                                pairs++;
                            }
                        }
                    }
            start = end;
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] contacts: {pairs:N0} pairs of coplanar opposite faces ({table.Partners.Count:N0} triangles with a partner; {tested:N0} pairs tested) among {keys.Count:N0} triangles, in {clock.Elapsed.TotalSeconds:0.0} s."));
        return table;
    }

    private static void Add(ContactTable table, int i, int t, int pi, int pt, (Vector3 A, Vector3 B, Vector3 C) partner)
    {
        if (!table.Partners.TryGetValue((i, t), out var list)) table.Partners[(i, t)] = list = new();
        list.Add((pi, pt, partner.A, partner.B, partner.C));
    }

    // Whether p (on the plane) lies inside the triangle, within ContactDistance of its edges.
    private static bool InsideTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Cross(b - a, c - a);
        var area2 = n.Length();
        if (area2 < 1e-12f) return false;
        n /= area2;
        foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
        {
            var edge = v - u;
            var outward = Vector3.Normalize(Vector3.Cross(edge, n));
            if (Vector3.Dot(p - u, outward) > ContactDistance) return false;
        }
        return true;
    }

    // The closest hit, with a contact read as the back face it also is: the partner whose triangle holds the point;
    // or, on a triangle that has partners but none holding it (a point just past a partner's edge, beyond the
    // tolerance), ContactRetries re-asks along directions jittered by ContactJitter radians, a back face at the same
    // distance winning. A triangle with no partner is never re-asked. The lightmap's Metal test does the same
    // (blix_hwx.mm, blix_buried_hit), from the same table (GpuContacts).
    private const int ContactRetries = 4;
    private const float ContactJitter = 1e-3f;

    private RayHit? ClosestThroughContacts(RayQueryScene scene, in Ray ray, float tMin, float tMax, uint seed)
    {
        var hit = scene.Closest(ray, tMin, tMax, seed);
        if (hit is not { FrontFace: true } front) return hit;
        var table = Contacts(scene);
        if (!table.Partners.TryGetValue((front.Instance, front.Triangle), out var partners)) return hit;
        var p = ray.PointAt(front.T);
        foreach (var (pi, pt, a, b, c) in partners)
            if (InsideTriangle(p, a, b, c))
                return front with { FrontFace = false, Instance = pi, Triangle = pt };
        var d = ray.Direction;
        var u = Vector3.Normalize(MathF.Abs(d.Y) < 0.9f ? Vector3.Cross(d, Vector3.UnitY) : Vector3.Cross(d, Vector3.UnitX));
        var v = Vector3.Cross(d, u);
        var near = 1e-3f + 1e-4f * front.T;
        for (var k = 0; k < ContactRetries; k++)
        {
            var angle = k * (2f * MathF.PI / ContactRetries);
            var jittered = Vector3.Normalize(d + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * ContactJitter);
            if (scene.Closest(new Ray(ray.Origin, jittered), tMin, front.T + 2f * near, seed) is { FrontFace: false } back
                && MathF.Abs(back.T - front.T) <= near)
                return back;
        }
        return hit;
    }

    // The table for the GPU, by the rows of the tracer's data: RowStart[row]..RowStart[row + 1] index Partners, three
    // float4s each (the partner's corners; the first's w its placement). A row shared by several placements (an
    // instanced mesh's) has none: which placement was hit is not known by row, and the jitter answers there.
    private (uint[] RowStart, Vector4[] Partners) GpuContacts(RayQueryScene scene, (int[][] Map, int Rows) rowMap)
    {
        var table = Contacts(scene);
        var rows = rowMap.Rows;
        var map = rowMap.Map;
        var owners = new int[rows];
        Array.Fill(owners, -1);
        for (var i = 0; i < scene.Instances.Count; i++)
            for (var t = 0; t < scene.Instances[i].Mesh.Indices.Length / 3; t++)
            {
                var row = map[i][t];
                owners[row] = owners[row] == -1 ? i : -2;
            }
        var count = new uint[rows + 1];
        var byRow = new List<(int Row, int Instance, Vector3 A, Vector3 B, Vector3 C)>();
        foreach (var ((i, t), list) in table.Partners)
        {
            var row = map[i][t];
            if (owners[row] != i) continue;
            foreach (var (pi, _, a, b, c) in list) byRow.Add((row, pi, a, b, c));
        }
        byRow.Sort((x, y) => x.Row.CompareTo(y.Row));
        foreach (var e in byRow) count[e.Row + 1]++;
        for (var r = 0; r < rows; r++) count[r + 1] += count[r];
        var partners = new Vector4[Math.Max(1, byRow.Count * 3)];
        for (var k = 0; k < byRow.Count; k++)
        {
            var (_, pi, a, b, c) = byRow[k];
            partners[k * 3] = new Vector4(a, pi);
            partners[k * 3 + 1] = new Vector4(b, 0f);
            partners[k * 3 + 2] = new Vector4(c, 0f);
        }
        return (count, partners);
    }
}
