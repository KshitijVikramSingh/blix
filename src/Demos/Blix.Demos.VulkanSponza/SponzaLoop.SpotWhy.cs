using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;

namespace Blix.Demos.VulkanSponza;

internal sealed partial class SponzaLoop
{
    // BLIX_SPOT_WHY=1 (a measurement): the coarse texels the camera sees, their gathered light against the instant
    // layer's (path B, snapshot at the light change), and the brightest over it traced again on the CPU -- what their
    // rays meet (by primitive), how far, how many back faces, how many misses. Keeps the CPU's scene.
    private static readonly bool spotWhy = Environment.GetEnvironmentVariable("BLIX_SPOT_WHY") == "1";
    private uint[]? spotSky, spotSun, spotSkyDetail;

    // The detail's sky visibility (texel.glsl texelDetailPack, bits 16-23).
    private static float SpotDetailSky(uint w) => ((w >> 16) & 0xFFu) / 255f;

    // The sky visibility traced: the cosine-weighted share of rays from the texel that meet nothing.
    private static float SpotSkyTraced(RayQueryScene scene, Vector3 p, Vector3 n, int rays)
    {
        var origin = p + n * 0.01f;
        var t = MathF.Abs(n.X) > 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var a = Vector3.Normalize(Vector3.Cross(n, t));
        var b = Vector3.Cross(n, a);
        var miss = 0;
        for (var k = 0; k < rays; k++)
        {
            var u = (k + 0.5f) / rays;
            var phi = k * 2.399963f;
            var r = MathF.Sqrt(u);
            var d = a * (r * MathF.Cos(phi)) + b * (r * MathF.Sin(phi)) + n * MathF.Sqrt(1f - u);
            if (scene.Closest(new Ray(origin, d), 0f, float.PositiveInfinity, (uint)k) is null) miss++;
        }
        return miss / (float)rays;
    }

    // One bounce of the sun, traced: over cosine rays from the texel, each hit's n.l times its own shadow ray to the sun
    // (the hit's albedo left out -- a shape to set against the gather's sun part, scaled by the view's sample).
    private static float SpotSunBounce(RayQueryScene scene, Vector3 p, Vector3 n, Vector3 toSun, int rays)
    {
        var origin = p + n * 0.01f;
        var t = MathF.Abs(n.X) > 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var a = Vector3.Normalize(Vector3.Cross(n, t));
        var b = Vector3.Cross(n, a);
        double sum = 0;
        for (var k = 0; k < rays; k++)
        {
            var u = (k + 0.5f) / rays;
            var phi = k * 2.399963f;
            var r = MathF.Sqrt(u);
            var d = a * (r * MathF.Cos(phi)) + b * (r * MathF.Sin(phi)) + n * MathF.Sqrt(1f - u);
            if (scene.Closest(new Ray(origin, d), 0f, float.PositiveInfinity, (uint)k) is not { } h) continue;
            var inst = scene.Instances[h.Instance];
            var ix = inst.Mesh.Indices;
            var p0 = Vector3.Transform(inst.Mesh.Positions[ix[3 * h.Triangle]], inst.World);
            var p1 = Vector3.Transform(inst.Mesh.Positions[ix[3 * h.Triangle + 1]], inst.World);
            var p2 = Vector3.Transform(inst.Mesh.Positions[ix[3 * h.Triangle + 2]], inst.World);
            var hn = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
            if (Vector3.Dot(hn, d) > 0f) hn = -hn;
            var nl = Vector3.Dot(hn, toSun);
            if (nl <= 0f) continue;
            var hp = origin + d * h.T + hn * 0.002f;
            if (!scene.Any(new Ray(hp, toSun), 0f, float.PositiveInfinity, (uint)k)) sum += nl;
        }
        return (float)(sum / rays);
    }

    private void SpotWhySnapshot()
    {
        device.WaitIdle();
        spotSky = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(EnsureLivePart(false), 0, texelCount * 8).AsSpan()).ToArray();
        spotSun = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(EnsureLivePart(true), 0, texelCount * 8).AsSpan()).ToArray();
        spotSkyDetail = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(splitSkyDetailBuffer, 0, texelCount * 4).AsSpan()).ToArray();
    }

    private void SpotWhyCompare(bool fine)
    {
        if (spotSky is not { } bSky || spotSun is not { } bSun || rayQueries is not { } scene || texelLiveIds is not { } live) return;
        var bDetail = spotSkyDetail ?? Array.Empty<uint>();
        if (fine) { spotSky = spotSun = null; spotSkyDetail = null; }
        device.WaitIdle();
        var gSky = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(EnsureLivePart(false), 0, texelCount * 8).AsSpan()).ToArray();
        var gSun = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(EnsureLivePart(true), 0, texelCount * 8).AsSpan()).ToArray();
        var gDetail = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(splitSkyDetailBuffer, 0, texelCount * 4).AsSpan()).ToArray();
        var texels = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelBuffer, 0, texelCount * 32).AsSpan()).ToArray();
        static float L(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        var irr = L(EffectiveSunIrradiance);
        var eye = cameraPosition;
        var fwd = camera.Forward;
        var cosCone = MathF.Cos(45f * MathF.PI / 180f);
        var ids = fine ? live.Except(transferGatherIds).ToArray() : transferGatherIds;
        var kind = fine ? "fine" : "coarse";
        var seen = new List<(uint Id, float G, float B, float GSun, float BSun)>();
        var lockSeen = new object();
        Parallel.For(0, ids.Length, k =>
        {
            var id = ids[k];
            var p = new Vector3(texels[2 * id].X, texels[2 * id].Y, texels[2 * id].Z);
            var nw = texels[2 * id + 1];
            var n = Vector3.Normalize(new Vector3(nw.X, nw.Y, nw.Z));
            var to = p + n * 0.02f - eye;
            var len = to.Length();
            if (len > 25f || len < 0.1f) return;
            var d = to / len;
            if (Vector3.Dot(d, fwd) < cosCone || Vector3.Dot(n, d) > 0f) return;
            if (scene.Closest(new Ray(eye, d), 0f, len * 0.995f, 1u) is not null) return;
            var gs = L(UnpackRgb9e5(gSky[2 * id])); var gu = L(UnpackRgb9e5(gSun[2 * id])) * irr;
            var bs = L(UnpackRgb9e5(bSky[2 * id])); var bu = L(UnpackRgb9e5(bSun[2 * id])) * irr;
            lock (lockSeen) seen.Add((id, gs + gu, bs + bu, gu, bu));
        });
        double sg = 0, sb = 0;
        foreach (var s in seen) { sg += s.G; sb += s.B; }
        var ratios = seen.Where(s => s.B > 1e-5f).Select(s => s.G / s.B).OrderBy(r => r).ToArray();
        float Q(double q) => ratios.Length == 0 ? 0 : ratios[(int)Math.Min(ratios.Length - 1, q * ratios.Length)];
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] spot why: {seen.Count:N0} {kind} texels seen from {eye.X:0.00},{eye.Y:0.00},{eye.Z:0.00}; gathered over instant all {sg / Math.Max(1e-9, sb):0.000}; per texel p10 {Q(0.1):0.00} median {Q(0.5):0.00} p90 {Q(0.9):0.00} p99 {Q(0.99):0.00}; over 2x {ratios.Count(r => r > 2f):N0}, over 4x {ratios.Count(r => r > 4f):N0}."));
        var median = seen.Select(s => s.G).OrderBy(g => g).ElementAtOrDefault(seen.Count / 2);
        // The scale: the gather's sun part over the traced bounce, on a sample of the texels seen.
        var toSun = -Vector3.Normalize(sunDirection);
        var sample = seen.Where((_, k) => k % Math.Max(1, seen.Count / 300) == 0).ToArray();
        double sampleG = 0, sampleT = 0, sampleB = 0;
        foreach (var s in sample)
        {
            var id = s.Id;
            var nw = texels[2 * id + 1];
            sampleT += SpotSunBounce(scene, new Vector3(texels[2 * id].X, texels[2 * id].Y, texels[2 * id].Z), Vector3.Normalize(new Vector3(nw.X, nw.Y, nw.Z)), toSun, 128);
            sampleG += s.GSun; sampleB += s.BSun;
        }
        var scaleG = sampleG / Math.Max(1e-9, sampleT);
        var scaleB = sampleB / Math.Max(1e-9, sampleT);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] spot why, {kind}: on {sample.Length} texels seen, the gather's sun part is {scaleG:0.0000} x the traced bounce, the instant's {scaleB:0.0000} x."));
        // The coarse texels seen with sky (their gather's visibility over 0.05): how deep their centroid sits under the
        // first front face down their normal from 30 cm up, and the sky traced from that face instead -- a centroid sunk
        // behind a one-sided wall sees the sky through it (2026-10-19).
        if (!fine)
        {
            var bins = new int[5];   // held sky: <=0.05, -0.2, -0.4, -0.7, more
            var lifted = new List<(float Z, float Depth, float FromSurface, float FromCentroid, Vector3 P, Vector3 N, int Level)>();
            foreach (var s in seen)
            {
                var z = SpotDetailSky(gDetail[s.Id]);
                bins[z <= 0.05f ? 0 : z <= 0.2f ? 1 : z <= 0.4f ? 2 : z <= 0.7f ? 3 : 4]++;
                if (z > 0.05f) lifted.Add((z, 0f, 0f, 0f, new Vector3(texels[2 * s.Id].X, texels[2 * s.Id].Y, texels[2 * s.Id].Z),
                    Vector3.Normalize(new Vector3(texels[2 * s.Id + 1].X, texels[2 * s.Id + 1].Y, texels[2 * s.Id + 1].Z)), (int)texels[2 * s.Id + 1].W));
            }
            var rows = lifted.ToArray();
            Parallel.For(0, rows.Length, k =>
            {
                var r = rows[k];
                var probe = scene.Closest(new Ray(r.P + r.N * 0.3f, -r.N), 0f, 0.6f, 1u);
                var depth = probe is { FrontFace: true } h ? 0.3f - h.T : float.NaN;
                var surface = float.IsNaN(depth) ? r.P : r.P + r.N * depth;
                rows[k] = r with { Depth = depth, FromSurface = SpotSkyTraced(scene, surface, r.N, 256), FromCentroid = SpotSkyTraced(scene, r.P, r.N, 256) };
            });
            var sunk = rows.Count(r => r.Depth > 1e-3f);
            var noFace = rows.Count(r => float.IsNaN(r.Depth));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] spot why, coarse sky: held <=0.05 {bins[0]:N0}, -0.2 {bins[1]:N0}, -0.4 {bins[2]:N0}, -0.7 {bins[3]:N0}, more {bins[4]:N0}; of the {rows.Length:N0} over 0.05, sunk over 1 mm {sunk:N0}, no front face within 30 cm {noFace:N0}; held {rows.Select(r => r.Z).DefaultIfEmpty().Average():0.000}, traced from the centroid {rows.Select(r => r.FromCentroid).DefaultIfEmpty().Average():0.000}, from the surface {rows.Select(r => r.FromSurface).DefaultIfEmpty().Average():0.000}."));
            foreach (var r in rows.OrderByDescending(r => r.Z - r.FromSurface).Take(10))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"    L{r.Level} at {r.P.X:0.00},{r.P.Y:0.00},{r.P.Z:0.00} normal {r.N.X:0.00},{r.N.Y:0.00},{r.N.Z:0.00}: held {r.Z:0.000}, traced from the centroid {r.FromCentroid:0.000}, from the surface {r.FromSurface:0.000}, depth {(float.IsNaN(r.Depth) ? "no face" : $"{r.Depth * 100:0.0} cm")}"));
        }
        // The profile: through the brightest fine texel over its instant, along its wall (a band 15 cm across, 2 m either
        // way, in its plane within 10 cm), every texel of every level -- what it holds against one bounce traced.
        if (fine && seen.Where(s => s.G > median && s.G > 2.5f * s.B).OrderByDescending(s => s.G - s.B).FirstOrDefault() is { Id: var pid } && pid != 0)
        {
            var c0 = new Vector3(texels[2 * pid].X, texels[2 * pid].Y, texels[2 * pid].Z);
            var cw = texels[2 * pid + 1];
            var cn = Vector3.Normalize(new Vector3(cw.X, cw.Y, cw.Z));
            var along = Vector3.Cross(Vector3.UnitY, cn);
            along = along.LengthSquared() > 1e-4f ? Vector3.Normalize(along) : Vector3.UnitX;
            var up = Vector3.Cross(cn, along);
            var coarseSet = new HashSet<uint>(transferGatherIds);
            foreach (var (axis, other, label) in new[] { (along, up, "along"), (up, along, "up") })
            {
                var rows = new List<(float X, int Level, bool Coarse, float G, float B, Vector3 P, Vector3 N, float Z, float Z0)>();
                foreach (var id in live)
                {
                    var q = new Vector3(texels[2 * id].X, texels[2 * id].Y, texels[2 * id].Z);
                    var qw = texels[2 * id + 1];
                    var qn = Vector3.Normalize(new Vector3(qw.X, qw.Y, qw.Z));
                    var dq = q - c0;
                    if (Vector3.Dot(qn, cn) < 0.9f || MathF.Abs(Vector3.Dot(dq, cn)) > 0.1f || MathF.Abs(Vector3.Dot(dq, other)) > 0.075f) continue;
                    var x = Vector3.Dot(dq, axis);
                    if (MathF.Abs(x) > 2f) continue;
                    var gu = L(UnpackRgb9e5(gSun[2 * id])) * irr;
                    var bu = L(UnpackRgb9e5(bSun[2 * id])) * irr;
                    rows.Add((x, (int)qw.W, coarseSet.Contains(id), gu, bu, q, qn, SpotDetailSky(gDetail[id]), bDetail.Length > id ? SpotDetailSky(bDetail[id]) : -1f));
                }
                var traced = new float[rows.Count];
                var skyTraced = new float[rows.Count];
                Parallel.For(0, rows.Count, k =>
                {
                    skyTraced[k] = SpotSkyTraced(scene, rows[k].P, rows[k].N, 256);
                    traced[k] = (float)(SpotSunBounce(scene, rows[k].P, rows[k].N, toSun, 256) * scaleG);
                });
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] spot why, profile {label} the wall through {c0.X:0.00},{c0.Y:0.00},{c0.Z:0.00} (normal {cn.X:0.00},{cn.Y:0.00},{cn.Z:0.00}): {rows.Count} texels; offset m, level, coarse, sun part held, instant's, traced at the texel's own position:"));
                var order = Enumerable.Range(0, rows.Count).OrderBy(k => rows[k].X).ToArray();
                foreach (var k in order)
                {
                    var r = rows[k];
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"    {r.X,6:0.00} L{r.Level} {(r.Coarse ? "c" : "f")} held {r.G:0.0000} instant {r.B:0.0000} traced {traced[k]:0.0000} sky held {r.Z:0.000} instant {r.Z0:0.000} traced {skyTraced[k]:0.000}"));
                }
            }
        }
        foreach (var s in seen.Where(s => s.G > median && s.G > 2.5f * s.B).OrderByDescending(s => s.G - s.B).Take(12))
        {
            var id = s.Id;
            var p = new Vector3(texels[2 * id].X, texels[2 * id].Y, texels[2 * id].Z);
            var nw = texels[2 * id + 1];
            var n = Vector3.Normalize(new Vector3(nw.X, nw.Y, nw.Z));
            var origin = p + n * 0.01f;
            var t = MathF.Abs(n.X) > 0.9f ? Vector3.UnitY : Vector3.UnitX;
            var a = Vector3.Normalize(Vector3.Cross(n, t));
            var b = Vector3.Cross(n, a);
            const int Rays = 256;
            int back = 0, miss = 0;
            double dist = 0;
            var byName = new Dictionary<string, int>();
            for (var k = 0; k < Rays; k++)
            {
                var u = (k + 0.5f) / Rays;
                var phi = k * 2.399963f;
                var r = MathF.Sqrt(u);
                var d = a * (r * MathF.Cos(phi)) + b * (r * MathF.Sin(phi)) + n * MathF.Sqrt(1f - u);
                if (scene.Closest(new Ray(origin, d), 0f, float.PositiveInfinity, (uint)k) is not { } h) { miss++; continue; }
                if (!h.FrontFace) back++;
                dist += h.T;
                var name = (h.Instance < rayPlacementNames.Count ? rayPlacementNames[h.Instance] : "?")
                    + (h.FrontFace ? "" : h.Instance < rayPlacementDoubleSided.Count && rayPlacementDoubleSided[h.Instance] ? " (back, 2-sided)" : " (back)");
                byName[name] = (byName.TryGetValue(name, out var had) ? had : 0) + 1;
            }
            var hitsN = Math.Max(1, Rays - miss);
            var traced = SpotSunBounce(scene, p, n, toSun, 512);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] spot why, {kind}: level {(int)nw.W} at {p.X:0.00},{p.Y:0.00},{p.Z:0.00} normal {n.X:0.00},{n.Y:0.00},{n.Z:0.00}, {Vector3.Distance(p, eye):0.0} m: gathered {s.G:0.0000} (sun {s.GSun:0.0000}) against instant {s.B:0.0000} (sun {s.BSun:0.0000}); traced sun bounce at the view's scale {traced * scaleG:0.0000}; rays: miss {miss * 100 / Rays}%, back {back * 100 / Rays}%, mean hit {dist / hitsN:0.00} m; hit ") +
                string.Join(", ", byName.OrderByDescending(x => x.Value).Take(7).Select(x => $"{x.Key} {x.Value * 100 / Rays}%")));
        }
        // The fine texels brightest over their instant: what the fine pass read for each -- its candidates, their light
        // (the sun part), their normal against the texel's, their weight (cut: by the normal), and the bounce traced at
        // each candidate's own place.
        if (fine)
        {
            var top = seen.Where(s => s.G > median && s.G > 2.5f * s.B).OrderByDescending(s => s.G - s.B).Take(8).ToArray();
            var words = FineCandidates(top.Select(s => s.Id).ToArray());
            for (var k = 0; k < top.Length; k++)
            {
                var id = top[k].Id;
                var p = new Vector3(texels[2 * id].X, texels[2 * id].Y, texels[2 * id].Z);
                var nw = texels[2 * id + 1];
                var n = Vector3.Normalize(new Vector3(nw.X, nw.Y, nw.Z));
                var w = words.AsSpan(k * 32, 32);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] spot why, fine read: texel at {p.X:0.00},{p.Y:0.00},{p.Z:0.00} normal {n.X:0.00},{n.Y:0.00},{n.Z:0.00}: held sun {top[k].GSun:0.0000}, instant {top[k].BSun:0.0000}, traced {SpotSunBounce(scene, p, n, toSun, 512) * scaleG:0.0000}; level read {(int)w[30]}, total weight {BitConverter.UInt32BitsToSingle(w[31]):0.000}"));
                for (var c = 0; c < 15 && w[2 * c] != uint.MaxValue; c++)
                {
                    var cid = w[2 * c];
                    var weight = BitConverter.UInt32BitsToSingle(w[2 * c + 1]);
                    var cp = new Vector3(texels[2 * cid].X, texels[2 * cid].Y, texels[2 * cid].Z);
                    var cw = texels[2 * cid + 1];
                    var cn = Vector3.Normalize(new Vector3(cw.X, cw.Y, cw.Z));
                    var cSun = L(UnpackRgb9e5(gSun[2 * cid])) * irr;
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"        L{(int)cw.W} {(weight < 0 ? "cut " : "")}weight {MathF.Abs(weight):0.000} offset {(cp - p).X:0.00},{(cp - p).Y:0.00},{(cp - p).Z:0.00} normal dot {Vector3.Dot(n, cn):0.00}: sun {cSun:0.0000}, traced there {SpotSunBounce(scene, cp, cn, toSun, 256) * scaleG:0.0000}"));
                }
            }
        }
    }
}
