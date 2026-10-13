using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using Blix.Geometry;

namespace Blix.Demos.VulkanSponza;

// Diagnostics for the cook's rules, against evidence other than the rule itself.
//   --lightmap-buried-why: what a burial rule alone buries (the lightmap's enclosed rule, the hashed texels' enclosed
//     rule) checked for EXPOSURE -- a clear line to it from open air in front of it. The open air: each patch's point
//     lifted ViewLift along its normal, kept when the line from the patch up to it is clear and none of six axis rays
//     from it meets a back face first (so not inside a solid).
//     A record that one of the ViewCandidates nearest such points in front of it can reach is exposed: a burial rule
//     that buries it buried something visible from somewhere, not only from the camera.
//   --transport-miss-why: the cook's rays whose hit found no patch it could see, on a front face (light really lost),
//     sampled and sorted by why: no patch facing the hit's way within reach, or the nearest one(s) blocked, and by what.
internal sealed partial class SponzaLoop
{
    private const float ViewLift = 0.3f;
    private const float ViewCell = 1f;
    private const int ViewCandidates = 32;
    private Dictionary<(int, int, int), List<Vector3>>? diagViews;
    private static readonly Vector3[] Axes = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
    private bool transportMissWhy;

    // The open-air points, from the cook's patches (positions, normals). Built when a diagnostic asks.
    private void BuildDiagViews(RayQueryScene scene, IReadOnlyList<Vector3> pos, IReadOnlyList<Vector3> nrm)
    {
        var kept = new ConcurrentBag<Vector3>();
        Parallel.For(0, pos.Count, i =>
        {
            var from = pos[i] + nrm[i] * 1e-3f;
            if (scene.Any(new Ray(from, nrm[i]), 0f, ViewLift, (uint)i)) return;
            // In open air: none of six axis rays from it meets a back face first (a contact counted as one).
            var v = pos[i] + nrm[i] * ViewLift;
            foreach (var axis in Axes)
                if (ClosestThroughContacts(scene, new Ray(v, axis), 0f, float.PositiveInfinity, (uint)i) is { FrontFace: false }) return;
            kept.Add(v);
        });
        var grid = new Dictionary<(int, int, int), List<Vector3>>();
        foreach (var v in kept)
        {
            var key = ViewKey(v);
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<Vector3>();
            list.Add(v);
        }
        diagViews = grid;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] diagnostics: {kept.Count:N0} open-air points of {pos.Count:N0} patches, {ViewLift} m up."));
    }

    private static (int, int, int) ViewKey(Vector3 p) =>
        ((int)MathF.Floor(p.X / ViewCell), (int)MathF.Floor(p.Y / ViewCell), (int)MathF.Floor(p.Z / ViewCell));

    // Whether a clear line reaches the point (p, n) from one of the nearest open-air points in front of it.
    private bool Exposed(RayQueryScene scene, Vector3 p, Vector3 n, uint seed)
    {
        if (diagViews is null) return false;
        var (kx, ky, kz) = ViewKey(p);
        var found = new List<(float D, Vector3 V)>();
        for (var dz = -1; dz <= 1; dz++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            if (!diagViews.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
            foreach (var v in list)
            {
                var d = v - p;
                var len = d.Length();
                if (len < 1e-3f || Vector3.Dot(d, n) < 0.1f * len) continue;
                found.Add((len, v));
            }
        }
        found.Sort((a, b) => a.D.CompareTo(b.D));
        var origin = p + n * 0.01f;
        foreach (var (_, v) in found.Take(ViewCandidates))
        {
            var d = v - origin;
            var len = d.Length();
            if (!scene.Any(new Ray(origin, d / len), 0f, len, seed)) return true;
        }
        return false;
    }

    // For the records a rule alone buries: how many are exposed, by the placement under them, and a few positions.
    private void ReportExposed(string what, RayQueryScene scene, IReadOnlyList<(Vector3 P, Vector3 N)> points, string? dumpPath = null)
    {
        if (diagViews is null)
        {
            Console.WriteLine($"[VulkanSponza] {what}: no open-air points (the cook's patches came from the cache before they were built).");
            return;
        }
        var exposed = new bool[points.Count];
        var task = "exposure: " + what;
        ProgressBegin(task, points.Count);
        Parallel.For(0, points.Count, k =>
        {
            exposed[k] = Exposed(scene, points[k].P, Vector3.Normalize(points[k].N), (uint)k);
            ProgressAdd(task);
        });
        ProgressEnd(task);
        var byName = new Dictionary<string, int>();
        var lines = new List<string>();
        int count = 0, offSurface = 0;
        for (var k = 0; k < points.Count; k++)
        {
            if (!exposed[k]) continue;
            count++;
            var (p, n) = (points[k].P, Vector3.Normalize(points[k].N));
            var under = scene.Closest(new Ray(p + n * 0.01f, -n), 0f, 0.05f, 1u);
            if (under is null) offSurface++;
            var name = under is { } h ? rayPlacementNames[h.Instance] : "(nothing under it)";
            byName[name] = byName.GetValueOrDefault(name) + 1;
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{p.X} {p.Y} {p.Z} {n.X} {n.Y} {n.Z} {name}"));
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] {what}: {count - offSurface:N0} of {points.Count:N0} exposed on a surface, {offSurface:N0} more off any surface (no surface within 5 cm under the point: a coarse cell's average past a face's edge, which the test cannot judge) (a clear line from open air {ViewLift} m off a patch, the {ViewCandidates} nearest in front)."));
        foreach (var (name, c) in byName.OrderByDescending(x => x.Value).Take(12))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {c,7:N0}  {name}"));
        if (dumpPath is not null) File.WriteAllLines(dumpPath, lines);
    }
}
