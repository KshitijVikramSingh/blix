using System.Numerics;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// CPU path-traced references for the probe clipmap (--probe-reference), on the scene's triangles.
//
// They share scene inputs with the runtime -- the ray scene, sun direction, measured irradiance and the cooked sky
// probe -- but compute transport independently by brute force: the surface reference at pixels the camera sees (what
// is SHADED), sky visibility at points through the scene, and the CPU sky against the cooked irradiance cube. (The
// probe-value references against the retired bounds-sized atlas, and the occupancy-grid march they used, are gone
// with it.)
internal sealed partial class SponzaLoop
{
    // The cooked sky probe, CPU-side: its environment radiance is what a sky-lit reference path escapes into.
    private Blix.Graphics.Images.BlixProbeData? iblProbeCpu;

    // A cube's texel for a direction, in Vulkan's face convention (+X, -X, +Y, -Y, +Z, -Z; spec "cube map face
    // selection"), nearest. Returns linear RGB.
    private static Vector3 CubeTexel(Half[] cube, int faceSize, Vector3 d)
    {
        var a = Vector3.Abs(d);
        int face; float sc, tc, ma;
        if (a.X >= a.Y && a.X >= a.Z) { ma = a.X; face = d.X >= 0 ? 0 : 1; sc = d.X >= 0 ? -d.Z : d.Z; tc = -d.Y; }
        else if (a.Y >= a.Z) { ma = a.Y; face = d.Y >= 0 ? 2 : 3; sc = d.X; tc = d.Y >= 0 ? d.Z : -d.Z; }
        else { ma = a.Z; face = d.Z >= 0 ? 4 : 5; sc = d.Z >= 0 ? d.X : -d.X; tc = -d.Y; }
        var s = 0.5f * (sc / ma + 1f);
        var t = 0.5f * (tc / ma + 1f);
        var x = Math.Clamp((int)(s * faceSize), 0, faceSize - 1);
        var y = Math.Clamp((int)(t * faceSize), 0, faceSize - 1);
        var o = ((face * faceSize + y) * faceSize + x) * 4;
        return new Vector3((float)cube[o], (float)cube[o + 1], (float)cube[o + 2]);
    }

    // The sky as the lighting integrals see it: the environment with the sun's disc replaced by the mean of the
    // annulus around it, as the cook does (PbrIblBaker.WithoutSun: radius 0.03 rad, annulus out to twice that). The
    // visible environment keeps the disc, stored as +Inf where it overflows a half, which no integral can use.
    private Vector3? skyAnnulusMean;
    private const float SunDiscRadius = 0.03f;

    // Lit by the procedural sky (no cooked probe: the city, the correctness scenes): the reference reads the same
    // analytic radiance the sky was baked from. It answered zero here, so a procedural scene's reference had no sky
    // at all -- the Cornell box's field read 2.6x "too bright" against a reference missing all its skylight.
    private bool proceduralSky;

    private Vector3 SkyRadiance(Vector3 d)
    {
        if (iblProbeCpu is not { } probe) return proceduralSky ? ProceduralSky.SkyColor(d, SkyBakeSunDirection) : Vector3.Zero;
        var value = CubeTexel(probe.EnvCube, probe.EnvFaceSize, d);
        if (probe.SunDirection is not { } sun) return float.IsFinite(value.X + value.Y + value.Z) ? value : Vector3.Zero;
        var toSun = -Vector3.Normalize(sun);
        if (skyAnnulusMean is null)
        {
            var rng = new Random(99);
            var sum = Vector3.Zero;
            var n = 0;
            var cosOuter = MathF.Cos(SunDiscRadius * 2f);
            var cosInner = MathF.Cos(SunDiscRadius);
            while (n < 4000)
            {
                var c = cosOuter + (float)rng.NextDouble() * (cosInner - cosOuter);
                var phi = (float)rng.NextDouble() * 2f * MathF.PI;
                var up = MathF.Abs(toSun.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
                var t = Vector3.Normalize(Vector3.Cross(up, toSun));
                var b = Vector3.Cross(toSun, t);
                var r = MathF.Sqrt(1f - c * c);
                var v = CubeTexel(probe.EnvCube, probe.EnvFaceSize, toSun * c + t * (r * MathF.Cos(phi)) + b * (r * MathF.Sin(phi)));
                if (!float.IsFinite(v.X + v.Y + v.Z)) continue;
                sum += v;
                n++;
            }
            skyAnnulusMean = sum / n;
        }
        return Vector3.Dot(d, toSun) >= MathF.Cos(SunDiscRadius) || !float.IsFinite(value.X + value.Y + value.Z)
            ? skyAnnulusMean.Value : value;
    }

    // The CPU sky against itself: irradiance integrated from the environment's radiance, for a handful of normals,
    // against the cooked irradiance cube in the same directions. A left-in sun disc or a wrong face convention shows
    // as a ratio far from one; a reference built on this sky is only as good as this check.
    private void WriteSkyConsistency()
    {
        if (iblProbeCpu is not { } probe) return;
        var rng = new Random(4242);
        var ratios = new List<double>();
        foreach (var n in new[] { Vector3.UnitY, -Vector3.UnitY, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ,
                                  Vector3.Normalize(new Vector3(1f, 1f, 1f)), Vector3.Normalize(new Vector3(-1f, 0.3f, 0.5f)) })
        {
            var acc = Vector3.Zero;
            const int samples = 20000;
            for (var k = 0; k < samples; k++) acc += SkyRadiance(CosineHemisphereCpu(n, rng));
            var integrated = acc * (MathF.PI / samples);
            var stored = CubeTexel(probe.IrradianceCube, probe.IrradianceFaceSize, n);
            double Lum(Vector3 v) => 0.2126 * v.X + 0.7152 * v.Y + 0.0722 * v.Z;
            ratios.Add(Lum(integrated) / Math.Max(1e-9, Lum(stored)));
        }
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] CPU sky check: irradiance integrated from the environment over the cooked irradiance cube, 8 normals: {string.Join(" ", ratios.Select(r => r.ToString("0.00", Inv)))}"));
        // The sky the clipmap's escaped rays read: the prefiltered cube at the mip they use (between 1 and 2), integrated
        // like irradiance. It should match the cooked irradiance if that cube is the disc-free sky, blurred.
        {
            var toSun = -Vector3.Normalize(sunDirection);
            var normals = new[] { Vector3.UnitY, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ,
                Vector3.Normalize(new Vector3(toSun.X, 0f, toSun.Z)), toSun, Vector3.Normalize(new Vector3(-toSun.X, 0f, -toSun.Z)) };
            var names = new[] { "+Y", "+X", "-X", "+Z", "-Z", "toward sun, level", "at the sun", "away from sun, level" };
            var line = new List<string>();
            for (var i = 0; i < normals.Length; i++)
            {
                var n = normals[i];
                var r = new Random(99);
                double Lum(Vector3 v) => 0.2126 * v.X + 0.7152 * v.Y + 0.0722 * v.Z;
                var acc = new Vector3[probe.PrefilterMipCount];
                const int samples = 20000;
                for (var k = 0; k < samples; k++)
                {
                    var d = CosineHemisphereCpu(n, r);
                    for (var m = 0; m < Math.Min(3, probe.PrefilterMipCount); m++)
                        acc[m] += CubeTexel(probe.PrefilteredSpecular[m], Math.Max(1, probe.PrefilterBaseSize >> m), d);
                }
                var stored = Lum(CubeTexel(probe.IrradianceCube, probe.IrradianceFaceSize, n));
                line.Add(string.Create(Inv, $"{names[i]}: mip0 {Lum(acc[0] * (MathF.PI / samples)) / stored:0.00} mip1 {Lum(acc[1] * (MathF.PI / samples)) / stored:0.00} mip2 {Lum(acc[2] * (MathF.PI / samples)) / stored:0.00}"));
            }
            Console.WriteLine("[VulkanSponza] CPU sky check: the prefiltered cube integrated like irradiance, over the cooked irradiance cube:");
            foreach (var l in line) Console.WriteLine("    " + l);
        }
        foreach (var (name, dir) in new[] { ("+Y", Vector3.UnitY), ("-Y", -Vector3.UnitY), ("+X", Vector3.UnitX), ("+Z", Vector3.UnitZ) })
        {
            Console.WriteLine(string.Create(Inv,
                $"    {name}: environment {CubeTexel(probe.EnvCube, probe.EnvFaceSize, dir)}, irradiance {CubeTexel(probe.IrradianceCube, probe.IrradianceFaceSize, dir)}"));
        }
        // How wide the anomaly at straight up is: the stored irradiance across the +Y face's centre, texel by texel.
        {
            var f = probe.IrradianceFaceSize;
            var cells = new List<string>();
            for (var dx = -3; dx <= 3; dx++)
            {
                var x = f / 2 + dx;
                var o = ((2 * f + f / 2) * f + x) * 4;   // face +Y, middle row
                cells.Add(string.Create(Inv, $"{(float)probe.IrradianceCube[o + 1]:0.000}"));
            }
            Console.WriteLine(string.Create(Inv, $"    irradiance cube +Y face (size {f}), middle row around the centre, green: {string.Join(" ", cells)}"));
        }
        // Tilted from straight up toward +X and toward -Z: does the disagreement grow toward the pole?
        {
            var rngTilt = new Random(31);
            var line = new List<string>();
            foreach (var deg in new[] { 0, 10, 20, 40, 60, 80 })
            foreach (var azimuth in new[] { Vector3.UnitX, -Vector3.UnitZ })
            {
                var a = deg * MathF.PI / 180f;
                var n = Vector3.Normalize(Vector3.UnitY * MathF.Cos(a) + azimuth * MathF.Sin(a));
                var acc = Vector3.Zero;
                for (var k = 0; k < 20000; k++) acc += SkyRadiance(CosineHemisphereCpu(n, rngTilt));
                var integrated = acc * (MathF.PI / 20000);
                var stored = CubeTexel(probe.IrradianceCube, probe.IrradianceFaceSize, n);
                double Lum(Vector3 v) => 0.2126 * v.X + 0.7152 * v.Y + 0.0722 * v.Z;
                line.Add(string.Create(Inv, $"{deg}deg{(azimuth.X > 0 ? "+X" : "-Z")} {Lum(integrated) / Lum(stored):0.00}"));
            }
            Console.WriteLine("    tilt from +Y: " + string.Join(", ", line));
        }
        // Where the environment is not finite, and how much of the +Y integral lands there or in the disc.
        {
            var faceTexels = probe.EnvFaceSize * probe.EnvFaceSize * 6;
            var infinite = 0;
            var maxAngle = 0.0;
            var toSun = probe.SunDirection is { } sdir ? -Vector3.Normalize(sdir) : Vector3.UnitY;
            for (var face = 0; face < 6; face++)
            for (var y = 0; y < probe.EnvFaceSize; y++)
            for (var x = 0; x < probe.EnvFaceSize; x++)
            {
                var o = ((face * probe.EnvFaceSize + y) * probe.EnvFaceSize + x) * 4;
                if (float.IsFinite((float)probe.EnvCube[o] + (float)probe.EnvCube[o + 1] + (float)probe.EnvCube[o + 2])) continue;
                infinite++;
                var sc = (x + 0.5f) / probe.EnvFaceSize * 2f - 1f;
                var tc = (y + 0.5f) / probe.EnvFaceSize * 2f - 1f;
                var dir = face switch
                {
                    0 => new Vector3(1f, -tc, -sc), 1 => new Vector3(-1f, -tc, sc),
                    2 => new Vector3(sc, 1f, tc), 3 => new Vector3(sc, -1f, -tc),
                    4 => new Vector3(sc, -tc, 1f), _ => new Vector3(-sc, -tc, -1f),
                };
                maxAngle = Math.Max(maxAngle, Math.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(dir), toSun), -1f, 1f)));
            }
            var rng2 = new Random(7);
            int inDisc = 0, total = 20000;
            var discEnergy = Vector3.Zero;
            for (var k = 0; k < total; k++)
            {
                var d = CosineHemisphereCpu(Vector3.UnitY, rng2);
                if (Vector3.Dot(d, toSun) >= MathF.Cos(SunDiscRadius)) { inDisc++; }
            }
            Console.WriteLine(string.Create(Inv,
                $"    environment: {infinite} non-finite texels of {faceTexels} (face size {probe.EnvFaceSize}), the farthest {maxAngle * 180 / Math.PI:0.00} deg from the sun; +Y samples in the disc: {inDisc} of {total}; annulus mean {skyAnnulusMean}"));
        }
        if (probe.SunDirection is { } sd) Console.WriteLine(string.Create(Inv, $"    recorded sun direction {sd}, irradiance {probe.SunIrradiance}; environment toward it {CubeTexel(probe.EnvCube, probe.EnvFaceSize, -Vector3.Normalize(sd))} / {CubeTexel(probe.EnvCube, probe.EnvFaceSize, Vector3.Normalize(sd))}"));
    }

    private static Vector3 CosineHemisphereCpu(Vector3 n, Random rng)
    {
        var u1 = (float)rng.NextDouble();
        var u2 = (float)rng.NextDouble();
        var r = MathF.Sqrt(u1);
        var theta = 2f * MathF.PI * u2;
        var x = r * MathF.Cos(theta);
        var y = r * MathF.Sin(theta);
        var z = MathF.Sqrt(MathF.Max(0f, 1f - u1));
        var up = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var t = Vector3.Normalize(Vector3.Cross(up, n));
        var b = Vector3.Cross(n, t);
        return Vector3.Normalize(t * x + b * y + n * z);
    }

    private static Vector3 UniformSphereCpu(Random rng)
    {
        var z = 1f - 2f * (float)rng.NextDouble();
        var r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        var phi = 2f * MathF.PI * (float)rng.NextDouble();
        return new Vector3(r * MathF.Cos(phi), z, r * MathF.Sin(phi));
    }

    // Sky visibility against the triangles: the mean fraction of sky seen from a point, over all directions, is exact
    // to compute (uniform rays, escaped or not, leaves met by their coverage). Held against the clipmap at the nearest
    // solved level-0 probe (the mean of its tile's directional values), the reference computed at that probe's own
    // point, so it is not judged somewhere it was not asked. Candidate points are a grid over the scene bounds.
    internal void WriteSkyVisibilityReference(int samples, int rays)
    {
        if (rayQueries is null || rayGpuData is null || clipmap is null)
        {
            Console.WriteLine("[VulkanSponza] sky visibility reference: needs the ray scene and the clipmap.");
            return;
        }
        var surfaces = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
            device.ReadGpuBuffer(raySurfaces, 0, rayGpuData.RowPlacements.Length * 4).AsSpan()).ToArray();
        rayGpuData.ApplyCoverage(surfaces);
        var depth = device.ReadTexture(clipmapDepth, out var dw, out var dh, out _);
        var states = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
            device.ReadGpuBuffer(clipmapState, 0, ClipmapLevels * clipmap.ProbesPerLevel * 16).AsSpan()).ToArray();
        var scene = rayQueries;

        double Reference(Vector3 p, int seed)
        {
            var rng = new Random(seed);
            var escaped = 0;
            for (var k = 0; k < rays; k++)
            {
                if (!scene.Any(new Ray(p, UniformSphereCpu(rng)), 0f, float.PositiveInfinity, (uint)rng.Next())) escaped++;
            }
            return escaped / (double)rays;
        }

        // Candidates: cell centres of a 48x27x32 grid over the scene bounds (the retired baked volume's resolution) inside
        // clipmap level 0 (a probe of margin), taken evenly through the list.
        const int gx = 48, gy = 27, gz = 32;
        var candidates = new List<Vector3>();
        for (var c = 0; c < gx * gy * gz; c++)
        {
            var x = c % gx; var y = c / gx % gy; var z = c / (gx * gy);
            var p = sceneBoundsMin + (new Vector3(x, y, z) + new Vector3(0.5f)) * sceneBoundsSpan / new Vector3(gx, gy, gz);
            if (clipmap.InsideDistance(0, p) >= 1f) candidates.Add(p);
        }
        var chosen = Enumerable.Range(0, Math.Min(samples, candidates.Count)).Select(k => candidates[k * candidates.Count / Math.Max(1, Math.Min(samples, candidates.Count))]).ToArray();
        var results = new (double Clip, double ClipRef, bool HasClip)[chosen.Length];
        Parallel.For(0, chosen.Length, k =>
        {
            var p = chosen[k];
            // The nearest level-0 clipmap probe, if solved and not buried.
            var s = clipmap.Spacing(0);
            var nearest = new Int3((int)MathF.Round(p.X / s - 0.5f), (int)MathF.Round(p.Y / s - 0.5f), (int)MathF.Round(p.Z / s - 0.5f));
            var slot = clipmap.Slot(nearest);
            var g = clipmap.SlotIndex(slot);
            var hasClip = new Int3((int)states[g * 4], (int)states[g * 4 + 1], (int)states[g * 4 + 2]) == nearest
                && (states[g * 4 + 3] & 1u) != 0 && (states[g * 4 + 3] & 2u) == 0;
            double clip = 0, clipRef = 0;
            if (hasClip)
            {
                var (tx, ty) = clipmap.TileOrigin(0, slot);
                for (var iy = 1; iy < 7; iy++)
                for (var ix = 1; ix < 7; ix++)
                    clip += (float)BitConverter.ToHalf(depth, ((ty + iy) * dw + tx + ix) * 8 + 6);
                clip /= 36;
                clipRef = Reference(clipmap.ProbePosition(0, nearest), 5000 + k);
            }
            results[k] = (clip, clipRef, hasClip);
        });

        var both = results.Where(r => r.HasClip && r.ClipRef > 0.01).ToArray();
        string Stats(IEnumerable<(double Got, double Want)> pairs)
        {
            var list = pairs.ToArray();
            if (list.Length == 0) return "none";
            var ratios = list.Select(q => q.Got / q.Want).OrderBy(v => v).ToArray();
            return string.Create(Inv,
                $"mean {list.Average(q => q.Got):0.000} against {list.Average(q => q.Want):0.000}, ratio median {ratios[ratios.Length / 2]:0.00} (p10 {ratios[ratios.Length / 10]:0.00}, p90 {ratios[ratios.Length * 9 / 10]:0.00}), mean |error| {list.Average(q => Math.Abs(q.Got - q.Want)):0.000} over {list.Length}");
        }
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sky visibility reference ({rays} rays a point, {chosen.Length} grid points inside clipmap level 0):"));
        Console.WriteLine("    probe clipmap:  " + Stats(both.Select(r => (r.Clip, r.ClipRef))));
    }
    // The arbiter for what is SHADED, whichever field produced it: at surface points the camera sees, the true
    // indirect irradiance (sun only, path-traced on the triangles) and cosine-weighted sky visibility, against what
    // the incident target holds at those pixels. Probe-value references cannot see a leak, because a leak happens
    // where probes are blended at a surface; this can. Points come from CPU camera rays through a grid of pixel
    // centres; a pixel whose neighbours' rays land more than 2% further or nearer is a depth edge, where the
    // raster's jitter could have shaded another surface, and is left out.
    // The clipmap's answers taken apart (SponzaLoop.ClipmapTwin): does the CPU twin reproduce the GPU's field, how
    // much of each answer came from probes the surface cannot see, and how far from the reference the field would
    // be had only the visible probes answered.
    private static void WriteLeakAttribution((double Field, double Ref, double Twin, double LeakShare, double Visible, double Level, double Distance, double Gtao, double ProbeValue, double ProbeRef, double ProbeDistance, double Ao05, double Ao1, double Ao2, double G1, double G2, double G2k8, double G2k16)[] rows)
    {
        static string Q(IEnumerable<double> values)
        {
            var v = values.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
            return v.Length == 0 ? "none" : string.Create(Inv, $"median {v[v.Length / 2]:0.00} (p10 {v[v.Length / 10]:0.00}, p90 {v[v.Length * 9 / 10]:0.00}) over {v.Length}");
        }
        var answered = rows.Where(r => !double.IsNaN(r.Twin)).ToArray();
        if (answered.Length == 0) return;
        var floor = 0.02 * rows.Average(r => r.Ref);
        var read = answered.Where(r => r.Ref > floor && !double.IsNaN(r.Visible)).ToArray();
        Console.WriteLine(string.Create(Inv, $"    clipmap twin: CPU over GPU field {Q(answered.Where(r => r.Field > 1e-5).Select(r => r.Twin / r.Field))}"));
        Console.WriteLine(string.Create(Inv,
            $"    leak: share of each answer from probes the surface cannot see: mean {answered.Average(r => r.LeakShare):0.000}, pixels with any {answered.Count(r => r.LeakShare > 0.001)} of {answered.Length}, over a quarter {answered.Count(r => r.LeakShare > 0.25)}"));
        // A sealed room (the thin-wall scene) has no light to compare with: what is above is all there is to say.
        if (read.Length == 0) return;
        Console.WriteLine(string.Create(Inv,
            $"    field as answered (twin):  mean {read.Average(r => r.Twin):0.00000} against {read.Average(r => r.Ref):0.00000}, ratio {Q(read.Select(r => r.Twin / r.Ref))}, mean |error| {read.Average(r => Math.Abs(r.Twin - r.Ref)):0.00000}"));
        Console.WriteLine(string.Create(Inv,
            $"    visible probes only:       mean {read.Average(r => r.Visible):0.00000} against {read.Average(r => r.Ref):0.00000}, ratio {Q(read.Select(r => r.Visible / r.Ref))}, mean |error| {read.Average(r => Math.Abs(r.Visible - r.Ref)):0.00000}"));
        foreach (var (name, pick) in new (string, Func<double, bool>)[] { ("ratio < 1.3", x => x < 1.3), ("ratio 1.3-2", x => x >= 1.3 && x < 2), ("ratio >= 2", x => x >= 2) })
        {
            var group = read.Where(r => pick(r.Twin / r.Ref)).ToArray();
            if (group.Length == 0) continue;
            Console.WriteLine(string.Create(Inv,
                $"      {name,-12} {group.Length,4} pixels: level mean {group.Average(r => r.Level):0.00}, camera distance median {group.Select(r => r.Distance).OrderBy(v => v).ElementAt(group.Length / 2):0.0} m; leak share mean {group.Average(r => r.LeakShare):0.000}; excess {group.Sum(r => r.Twin - r.Ref):0.0000} of the total {read.Sum(r => r.Twin - r.Ref):0.0000}; visible-only ratio {Q(group.Select(r => r.Visible / r.Ref))}"));
        }
        Console.WriteLine(string.Create(Inv,
            $"    as shaded (field x GTAO):  mean {read.Average(r => r.Field * r.Gtao):0.00000} against {read.Average(r => r.Ref):0.00000}, ratio {Q(read.Select(r => r.Field * r.Gtao / r.Ref))}, mean |error| {read.Average(r => Math.Abs(r.Field * r.Gtao - r.Ref)):0.00000}; GTAO {Q(read.Select(r => r.Gtao))}"));
        foreach (var (name, pick) in new (string, Func<double, bool>)[] { ("ratio < 1.3", x => x < 1.3), ("ratio 1.3-2", x => x >= 1.3 && x < 2), ("ratio >= 2", x => x >= 2) })
        {
            var group = read.Where(r => pick(r.Twin / r.Ref)).ToArray();
            if (group.Length == 0) continue;
            Console.WriteLine(string.Create(Inv,
                $"      {name,-12} {group.Length,4} pixels: GTAO {Q(group.Select(r => r.Gtao))}; as shaded ratio {Q(group.Select(r => r.Field * r.Gtao / r.Ref))}; excess as shaded {group.Sum(r => r.Field * r.Gtao - r.Ref):0.0000}"));
        }
        // Where the error enters: the strongest visible probe's stored value against the truth at that probe (the
        // solve), and the truth at that probe against the truth at the surface (the step from probe to surface).
        foreach (var (name, pick) in new (string, Func<double, bool>)[] { ("all", x => true), ("ratio < 1.3", x => x < 1.3), ("ratio 1.3-2", x => x >= 1.3 && x < 2), ("ratio >= 2", x => x >= 2) })
        {
            var group = read.Where(r => pick(r.Twin / r.Ref) && !double.IsNaN(r.ProbeRef) && r.ProbeRef > 1e-6).ToArray();
            if (group.Length == 0) continue;
            Console.WriteLine(string.Create(Inv,
                $"      {name,-12} {group.Length,4} pixels: probe stored / truth at probe {Q(group.Select(r => r.ProbeValue / r.ProbeRef))}; truth at probe / truth at surface {Q(group.Select(r => r.ProbeRef / r.Ref))}; probe {Q(group.Select(r => r.ProbeDistance))} m away"));
        }
        // Would a traced near-field visibility, ranged to the probe spacing, stand in for GTAO? The field times each.
        foreach (var (name, ao) in new (string, Func<(double Field, double Ref, double Twin, double LeakShare, double Visible, double Level, double Distance, double Gtao, double ProbeValue, double ProbeRef, double ProbeDistance, double Ao05, double Ao1, double Ao2, double G1, double G2, double G2k8, double G2k16), double>)[]
            { ("GTAO", r => r.Gtao), ("traced 0.5 spacing", r => r.Ao05), ("traced 1 spacing", r => r.Ao1), ("traced 2 spacings", r => r.Ao2) })
        {
            Console.WriteLine(string.Create(Inv,
                $"    field x {name,-19} mean {read.Average(r => r.Field * ao(r)):0.00000} against {read.Average(r => r.Ref):0.00000}, ratio {Q(read.Select(r => r.Field * ao(r) / r.Ref))}, mean |error| {read.Average(r => Math.Abs(r.Field * ao(r) - r.Ref)):0.00000}; worst group (field >= 2x) {Q(read.Where(r => r.Twin / r.Ref >= 2).Select(r => r.Field * ao(r) / r.Ref))}"));
        }
        // The gather prototype in place of field x occlusion: what it alone delivers (no GTAO on top).
        foreach (var (name, pick) in new (string, Func<(double Field, double Ref, double Twin, double LeakShare, double Visible, double Level, double Distance, double Gtao, double ProbeValue, double ProbeRef, double ProbeDistance, double Ao05, double Ao1, double Ao2, double G1, double G2, double G2k8, double G2k16), double>)[]
            { ("gather R=1 sp, 256", r => r.G1), ("gather R=2 sp, 256", r => r.G2), ("gather R=2 sp, 16", r => r.G2k16), ("gather R=2 sp, 8", r => r.G2k8) })
        {
            Console.WriteLine(string.Create(Inv,
                $"    {name,-21} mean {read.Average(r => pick(r)):0.00000} against {read.Average(r => r.Ref):0.00000}, ratio {Q(read.Select(r => pick(r) / r.Ref))}, mean |error| {read.Average(r => Math.Abs(pick(r) - r.Ref)):0.00000}; worst group (field >= 2x) {Q(read.Where(r => r.Twin / r.Ref >= 2).Select(r => pick(r) / r.Ref))}"));
        }
        // By the level that answered (share-weighted, rounded), and by camera distance: does the field go flat, and
        // bright, where the coarse levels answer?
        Console.WriteLine("    by level that answered:");
        foreach (var g in read.GroupBy(r => (int)Math.Round(r.Level)).OrderBy(g => g.Key))
        {
            var group = g.ToArray();
            Console.WriteLine(string.Create(Inv,
                $"      level {g.Key} {group.Length,4} pixels, camera distance median {group.Select(r => r.Distance).OrderBy(v => v).ElementAt(group.Length / 2):0.0} m: mean {group.Average(r => r.Twin):0.00000} against {group.Average(r => r.Ref):0.00000}, ratio {Q(group.Select(r => r.Twin / r.Ref))}; leak share {group.Average(r => r.LeakShare):0.000}; visible-only {Q(group.Select(r => r.Visible / r.Ref))}; excess {group.Sum(r => r.Twin - r.Ref):0.0000}; as shaded {Q(group.Select(r => r.Field * r.Gtao / r.Ref))}"));
        }
        // Flatness: across pixels answered by the same level, how much the field varies against how much the
        // reference does (coefficient of variation). A field that lights flat varies less than the truth.
        foreach (var g in read.GroupBy(r => (int)Math.Round(r.Level)).OrderBy(g => g.Key).Where(g => g.Count() >= 10))
        {
            static double Cv(IEnumerable<double> v) { var a = v.ToArray(); var m = a.Average(); return Math.Sqrt(a.Average(x => (x - m) * (x - m))) / m; }
            var group = g.ToArray();
            Console.WriteLine(string.Create(Inv,
                $"      level {g.Key} variation across pixels: field {Cv(group.Select(r => r.Twin)):0.00}, reference {Cv(group.Select(r => r.Ref)):0.00}"));
        }
    }

    internal void WriteSurfaceReference(int grid, int paths, int bounces)
    {
        if (rayQueries is null || rayGpuData is null || !incidentField)
        {
            Console.WriteLine("[VulkanSponza] surface reference: needs the ray scene and the incident field.");
            return;
        }
        var surfaces = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
            device.ReadGpuBuffer(raySurfaces, 0, rayGpuData.RowPlacements.Length * 4).AsSpan()).ToArray();
        rayGpuData.ApplyCoverage(surfaces);
        var incident = device.ReadTexture(graph.GetColorTexture(incidentHandle), out var iw, out var ih, out _);
        var toSun = -Vector3.Normalize(sunDirection);
        var sunIrr = EffectiveSunIrradiance;
        var scene = rayQueries;
        var data = rayGpuData;
        Matrix4x4.Invert(viewProj, out var invViewProj);

        Vector3 Albedo(int placement, int triangle)
        {
            var word = surfaces[data.RowOf(placement, triangle)];
            static float Linear(uint b) { var c = b / 255f; return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f); }
            return new Vector3(Linear(word & 0xFF), Linear((word >> 8) & 0xFF), Linear((word >> 16) & 0xFF));
        }
        Vector3 Normal(RayHit hit)
        {
            var inst = scene.Instances[hit.Instance];
            var m = inst.Mesh;
            var a = Vector3.Transform(m.Positions[m.Indices[hit.Triangle * 3]], inst.World);
            var b = Vector3.Transform(m.Positions[m.Indices[hit.Triangle * 3 + 1]], inst.World);
            var c = Vector3.Transform(m.Positions[m.Indices[hit.Triangle * 3 + 2]], inst.World);
            var n = Vector3.Cross(b - a, c - a);
            return n.LengthSquared() > 0f ? Vector3.Normalize(n) : Vector3.UnitY;
        }
        Vector3 Trace(Vector3 origin, Vector3 dir, int depth, Random rng)
        {
            var hit = scene.Closest(new Ray(origin, dir), 0f, float.PositiveInfinity, (uint)rng.Next());
            if (hit is not { } h) return Vector3.Zero;
            var n = Normal(h);
            if (Vector3.Dot(n, dir) > 0f) n = -n;
            var p = origin + dir * h.T + n * 0.01f;
            var albedo = Albedo(h.Instance, h.Triangle);
            var ndotl = MathF.Max(Vector3.Dot(n, toSun), 0f);
            var direct = ndotl > 0f && !scene.Any(new Ray(p, toSun), 0f, float.PositiveInfinity, (uint)rng.Next()) ? sunIrr * ndotl : Vector3.Zero;
            var outgoing = albedo * direct / MathF.PI;
            if (depth > 0) outgoing += albedo * Trace(p, CosineHemisphereCpu(n, rng), depth - 1, rng);
            return outgoing;
        }
        Ray CameraRay(float px, float py)
        {
            var ndc = new Vector2(px / iw * 2f - 1f, py / ih * 2f - 1f);
            var far = Vector4.Transform(new Vector4(ndc, 1f, 1f), invViewProj);
            return new Ray(cameraPosition, Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - cameraPosition));
        }

        // Sky-carried bounce: the sky the only light, escaped rays bringing the environment's radiance, the shading
        // point's own escapes excluded (that is the direct sky the lit pass adds through sky visibility).
        var probe = iblProbeCpu;
        Vector3 SkyTrace(Vector3 origin, Vector3 dir, int depth, Random rng)
        {
            var hit = scene.Closest(new Ray(origin, dir), 0f, float.PositiveInfinity, (uint)rng.Next());
            if (hit is not { } h) return SkyRadiance(dir);
            if (depth < 0) return Vector3.Zero;
            var n = Normal(h);
            if (Vector3.Dot(n, dir) > 0f) n = -n;
            var p = origin + dir * h.T + n * 0.01f;
            return Albedo(h.Instance, h.Triangle) * SkyTrace(p, CosineHemisphereCpu(n, rng), depth - 1, rng);
        }
        // All indirect diffuse at any point for a normal (sun bounce + sky bounce + direct sky), as the per-pixel
        // reference sums it: for judging a probe's stored value at the probe itself.
        Vector3 TotalReference(Vector3 x, Vector3 n, int count, Random rng)
        {
            var sum = Vector3.Zero;
            for (var j = 0; j < count; j++)
            {
                var d = CosineHemisphereCpu(n, rng);
                sum += Trace(x, d, bounces, rng);
                var first = scene.Closest(new Ray(x, d), 0f, float.PositiveInfinity, (uint)rng.Next());
                if (first is not { } f) { sum += SkyRadiance(d); continue; }
                var fn = Normal(f);
                if (Vector3.Dot(fn, d) > 0f) fn = -fn;
                sum += Albedo(f.Instance, f.Triangle) * SkyTrace(x + d * f.T + fn * 0.01f, CosineHemisphereCpu(fn, rng), bounces - 1, rng);
            }
            return sum * (MathF.PI / count);
        }
        // The per-pixel gather prototype: K cosine rays out to range R. A hit brings what that surface sends back
        // (albedo/pi times its direct sun and the field's irradiance there); a ray that meets nothing within R
        // brings the field's irradiance at its far end facing back along it, over pi: the probes' word for the
        // radiance arriving from that direction. Probes then carry the far field, the rays the near.
        var clipmapForGather = ClipmapActive ? ReadClipmap() : null;
        Vector3 FieldAt(Vector3 at, Vector3 facing)
        {
            var shares = ClipmapShares(clipmapForGather!, at, facing);
            if (shares.Count == 0) return probe is null ? Vector3.Zero : CubeTexel(probe.IrradianceCube, probe.IrradianceFaceSize, facing);
            var sum = Vector3.Zero;
            foreach (var sh in shares) sum += sh.Share * sh.Irradiance;
            return sum;
        }
        Vector3 Gather(Vector3 x, Vector3 n, float range, int count, Random rng)
        {
            var sum = Vector3.Zero;
            for (var j = 0; j < count; j++)
            {
                var d = CosineHemisphereCpu(n, rng);
                var h = scene.Closest(new Ray(x, d), 0f, range, (uint)rng.Next());
                if (h is { } hit)
                {
                    var hn = Normal(hit);
                    if (Vector3.Dot(hn, d) > 0f) hn = -hn;
                    var hp = x + d * hit.T + hn * 0.01f;
                    var ndotl = MathF.Max(Vector3.Dot(hn, toSun), 0f);
                    var sun = ndotl > 0f && !scene.Any(new Ray(hp, toSun), 0f, float.PositiveInfinity, (uint)rng.Next()) ? sunIrr * ndotl : Vector3.Zero;
                    sum += Albedo(hit.Instance, hit.Triangle) * (sun + FieldAt(hp, hn)) / MathF.PI;
                }
                else
                {
                    sum += FieldAt(x + d * range, d) / MathF.PI;
                }
            }
            return sum * (MathF.PI / count);
        }
        // The gather split by ray: each ray's field-based radiance beside the traced truth for the same ray (a hit:
        // that surface's albedo/pi times its sun and its true indirect; a miss: the ray continued past R, its
        // escape bringing the sky, its hit the same as a near hit). Sums are cosine-weighted, pi/count each.
        (Vector3 HitField, Vector3 HitTrue, Vector3 MissField, Vector3 MissTrue, Vector3 MissProxyTrue, Vector3 MissFull, int Hits) GatherSplit(Vector3 x, Vector3 n, float range, int count, int truthPaths, Random rng)
        {
            Vector3 hf = Vector3.Zero, ht = Vector3.Zero, mf = Vector3.Zero, mt = Vector3.Zero, mp = Vector3.Zero, mfull = Vector3.Zero; var hits = 0;
            Vector3 Leaving(RayHit hit, Vector3 origin, Vector3 d, bool truth, out Vector3 field)
            {
                var hn = Normal(hit);
                if (Vector3.Dot(hn, d) > 0f) hn = -hn;
                var hp = origin + d * hit.T + hn * 0.01f;
                var ndotl = MathF.Max(Vector3.Dot(hn, toSun), 0f);
                var sun = ndotl > 0f && !scene.Any(new Ray(hp, toSun), 0f, float.PositiveInfinity, (uint)rng.Next()) ? sunIrr * ndotl : Vector3.Zero;
                var albedo = Albedo(hit.Instance, hit.Triangle);
                field = albedo * (sun + FieldAt(hp, hn)) / MathF.PI;
                return albedo * (sun + TotalReference(hp, hn, truthPaths, rng)) / MathF.PI;
            }
            for (var j = 0; j < count; j++)
            {
                var d = CosineHemisphereCpu(n, rng);
                var h = scene.Closest(new Ray(x, d), 0f, float.PositiveInfinity, (uint)rng.Next());
                if (h is { } hit && hit.T <= range)
                {
                    hits++;
                    ht += Leaving(hit, x, d, true, out var f);
                    hf += f;
                }
                else
                {
                    mf += FieldAt(x + d * range, d) / MathF.PI;
                    // The same proxy (irradiance at the ray's end, facing along it) with the TRUE irradiance there.
                    mp += TotalReference(x + d * range, d, truthPaths, rng) / MathF.PI;
                    if (h is { } far) { mt += Leaving(far, x, d, true, out var farField); mfull += farField; }
                    else { var skyL = SkyRadiance(d); mt += skyL; mfull += skyL; }
                }
            }
            var k = MathF.PI / count;
            return (hf * k, ht * k, mf * k, mt * k, mp * k, mfull * k, hits);
        }
        var results = new (bool Use, double Bounce, double BounceRef, double Sky, double SkyRef, double SkyBounceRef,
            double DirectRef, double DirectModel, double DirectModelTrueV, double Twin, double LeakShare, double Visible, double Level, double Distance, double Gtao, float Px, float Py, double ProbeValue, double ProbeRef, double ProbeDistance, double Ao05, double Ao1, double Ao2, double G1, double G2, double G2k8, double G2k16, double HitField, double HitTrue, double MissField, double MissTrue, double HitShare, double MissProxy, double MissFull)[grid * grid];
        // With the clipmap: which probes answered each pixel, and which of them the surface cannot see (a segment
        // from the surface to the probe meets geometry). Those shares are light from the far side of a wall.
        var clipmapRead = clipmapForGather;
        // GTAO's visibility, which the lit pass multiplies the field by (ambientVis.r): the shaded indirect is field
        // times this, so that product is what the reference judges.
        var gtao = device.ReadTexture(graph.GetColorTexture(ambientDenoisedHandle), out var gw, out var gh, out var gf);
        float GtaoAt(float px, float py)
        {
            var x = Math.Clamp((int)(px / iw * gw), 0, gw - 1);
            var y = Math.Clamp((int)(py / ih * gh), 0, gh - 1);
            return gf switch
            {
                TextureFormat.R16F => (float)BitConverter.ToHalf(gtao, (y * gw + x) * 2),
                TextureFormat.Rgba16F => (float)BitConverter.ToHalf(gtao, (y * gw + x) * 8 + 6),
                TextureFormat.Rgba8 => gtao[(y * gw + x) * 4 + 3] / 255f,
                _ => float.NaN,
            };
        }
        Parallel.For(0, grid * grid, k =>
        {
            var gx = k % grid; var gy = k / grid;
            var px = (gx + 0.5f) / grid * iw;
            var py = (gy + 0.5f) / grid * ih;
            var ray = CameraRay(px, py);
            if (scene.Closest(ray) is not { } hit) return;
            foreach (var (dx, dy) in new[] { (2f, 0f), (-2f, 0f), (0f, 2f), (0f, -2f) })
            {
                if (scene.Closest(CameraRay(px + dx, py + dy)) is not { } other || MathF.Abs(other.T - hit.T) > 0.02f * hit.T) return;
            }
            var n = Normal(hit);
            if (Vector3.Dot(n, ray.Direction) > 0f) n = -n;
            var x = ray.PointAt(hit.T) + n * 0.01f;
            var rng = new Random(777 + k + refSeed * 100003);
            var acc = Vector3.Zero;
            var skyAcc = Vector3.Zero;
            var directAcc = Vector3.Zero;
            var open = 0;
            for (var j = 0; j < paths; j++)
            {
                var d = CosineHemisphereCpu(n, rng);
                acc += Trace(x, d, bounces, rng);
                if (!scene.Any(new Ray(x, d), 0f, float.PositiveInfinity, (uint)rng.Next()))
                {
                    open++;
                    // Direct sky: what an escaping direction brings, cosine-sampled so the cosine cancels.
                    directAcc += SkyRadiance(d);
                }
                // From the shading point, only a ray that meets a surface carries bounced sky.
                var first = scene.Closest(new Ray(x, d), 0f, float.PositiveInfinity, (uint)rng.Next());
                if (first is { } f)
                {
                    var fn = Normal(f);
                    if (Vector3.Dot(fn, d) > 0f) fn = -fn;
                    var fp = x + d * f.T + fn * 0.01f;
                    skyAcc += Albedo(f.Instance, f.Triangle) * SkyTrace(fp, CosineHemisphereCpu(fn, rng), bounces - 1, rng);
                }
            }
            var reference = acc * (MathF.PI / paths);
            var ix = Math.Clamp((int)px, 0, iw - 1);
            var iy = Math.Clamp((int)py, 0, ih - 1);
            var o = (iy * iw + ix) * 8;
            var got = new Vector3((float)BitConverter.ToHalf(incident, o), (float)BitConverter.ToHalf(incident, o + 2), (float)BitConverter.ToHalf(incident, o + 4));
            var sky = (float)BitConverter.ToHalf(incident, o + 6);
            double Lum(Vector3 v) => 0.2126 * v.X + 0.7152 * v.Y + 0.0722 * v.Z;
            // The lit pass's direct sky: the cooked irradiance at the normal times the field's sky visibility; and the same
            // with the true visibility, so the model's own bias is told apart from the field's visibility error.
            var cooked = probe is null ? Vector3.Zero : CubeTexel(probe.IrradianceCube, probe.IrradianceFaceSize, n);
            double twin = double.NaN, leak = double.NaN, visible = double.NaN, level = double.NaN;
            double probeValue = double.NaN, probeRef = double.NaN, probeDistance = double.NaN;
            double ao05 = double.NaN, ao1 = double.NaN, ao2 = double.NaN;
            double g1 = double.NaN, g2 = double.NaN, g2k8 = double.NaN, g2k16 = double.NaN;
            double hitField = double.NaN, hitTrue = double.NaN, missField = double.NaN, missTrue = double.NaN, hitShare = double.NaN, missProxy = double.NaN, missFull = double.NaN;
            if (clipmapRead is not null)
            {
                var shares = ClipmapShares(clipmapRead, ray.PointAt(hit.T), n);
                if (shares.Count > 0)
                {
                    var all = Vector3.Zero; var seen = Vector3.Zero; var seenShare = 0f;
                    ProbeShare? strongest = null;
                    foreach (var sh in shares)
                    {
                        all += sh.Share * sh.Irradiance;
                        var to = sh.Position - x;
                        var dist = to.Length();
                        if (dist < 1e-4f || !scene.Any(new Ray(x, to / dist), 0f, dist, (uint)rng.Next()))
                        {
                            seen += sh.Share * sh.Irradiance; seenShare += sh.Share;
                            if (strongest is null || sh.Share > strongest.Value.Share) strongest = sh;
                        }
                    }
                    // Traced near-field visibility, ranged by the answering level's spacing: the cosine-weighted share of
                    // directions that meet nothing within half, one and two spacings.
                    var spacing = clipmapRead.Map.Spacing((int)Math.Round(shares.Sum(sh => sh.Share * sh.Level)));
                    int f05 = 0, f1 = 0, f2 = 0; const int aoRays = 256;
                    for (var j = 0; j < aoRays; j++)
                    {
                        var d = CosineHemisphereCpu(n, rng);
                        var h = scene.Closest(new Ray(x, d), 0f, 2f * spacing, (uint)rng.Next());
                        var t = h is { } hh ? hh.T : float.PositiveInfinity;
                        if (t > 0.5f * spacing) f05++;
                        if (t > spacing) f1++;
                        if (t > 2f * spacing) f2++;
                    }
                    ao05 = f05 / (double)aoRays; ao1 = f1 / (double)aoRays; ao2 = f2 / (double)aoRays;
                    g1 = Lum(Gather(x, n, spacing, 256, new Random(5 + k)));
                    g2 = Lum(Gather(x, n, 2f * spacing, 256, new Random(6 + k)));
                    g2k8 = Lum(Gather(x, n, 2f * spacing, 8, new Random(7 + k)));
                    g2k16 = Lum(Gather(x, n, 2f * spacing, 16, new Random(8 + k)));
                    var split = GatherSplit(x, n, 2f * spacing, 48, 32, new Random(9 + k));
                    hitField = Lum(split.HitField); hitTrue = Lum(split.HitTrue); missField = Lum(split.MissField); missTrue = Lum(split.MissTrue);
                    hitShare = split.Hits / 48.0; missProxy = Lum(split.MissProxyTrue); missFull = Lum(split.MissFull);
                    // The strongest visible probe judged at its own position, for this pixel's normal.
                    if (strongest is { } top)
                    {
                        probeValue = Lum(top.Irradiance);
                        probeRef = Lum(TotalReference(top.Position, n, paths, new Random(91 + k)));
                        probeDistance = (top.Position - x).Length();
                    }
                    twin = Lum(all);
                    level = shares.Sum(sh => sh.Share * sh.Level);
                    leak = 1.0 - seenShare;
                    visible = seenShare > 1e-4f ? Lum(seen / seenShare) : double.NaN;
                }
            }
            results[k] = (true, Lum(got), Lum(reference), sky, open / (double)paths, Lum(skyAcc * (MathF.PI / paths)),
                Lum(directAcc * (MathF.PI / paths)), Lum(cooked) * sky, Lum(cooked) * open / paths, twin, leak, visible, level, hit.T, GtaoAt(px, py), px / iw, py / ih, probeValue, probeRef, probeDistance, ao05, ao1, ao2, g1, g2, g2k8, g2k16, hitField, hitTrue, missField, missTrue, hitShare, missProxy, missFull);
        });
        var used = results.Where(r => r.Use).ToArray();
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] surface reference ({used.Length} of {grid * grid} pixels off depth edges, {paths} paths x {bounces} bounces, sun only for bounce):"));
        foreach (var r in used)
        {
            // All the indirect diffuse light: what the field delivers (the clipmap's incident field is all of it)
            // against the three references summed (sun bounce at this run's sun, sky bounce, direct sky).
            var fieldTotal = r.Bounce;
            var refTotal = r.BounceRef + r.SkyBounceRef + r.DirectRef;
            Console.WriteLine(string.Create(Inv, $"    surface  at {r.Px:0.0000},{r.Py:0.0000} gtao {r.Gtao:0.000} level {r.Level:0.00} dist {r.Distance:0.0}  bounce {r.Bounce:0.00000} ref {r.BounceRef:0.00000}  sky {r.Sky:0.0000} ref {r.SkyRef:0.0000}  skybounce ref {r.SkyBounceRef:0.00000}  direct {r.DirectModel:0.00000} truev {r.DirectModelTrueV:0.00000} ref {r.DirectRef:0.00000}  total {fieldTotal:0.00000} ref {refTotal:0.00000}"));
        }
        var skyRatios = used.Where(r => r.SkyRef > 0.02).Select(r => r.Sky / r.SkyRef).OrderBy(v => v).ToArray();
        if (skyRatios.Length > 0)
        {
            Console.WriteLine(string.Create(Inv,
                $"    sky visibility: mean {used.Average(r => r.Sky):0.000} against {used.Average(r => r.SkyRef):0.000}, ratio median {skyRatios[skyRatios.Length / 2]:0.00} (p10 {skyRatios[skyRatios.Length / 10]:0.00}, p90 {skyRatios[skyRatios.Length * 9 / 10]:0.00}), mean |error| {used.Average(r => Math.Abs(r.Sky - r.SkyRef)):0.000}"));
        }
        Console.WriteLine(string.Create(Inv,
            $"    bounce (this run, with the sun as set): mean {used.Average(r => r.Bounce):0.00000}; sun-only reference mean {used.Average(r => r.BounceRef):0.00000}"));
        // The whole indirect diffuse: what the incident field delivers (with the clipmap it carries the sky too), against
        // the three references summed. Ratios over the pixels with enough reference light to be read (2% of the mean up).
        var totals = used.Select(r => (Field: r.Bounce, Ref: r.BounceRef + r.SkyBounceRef + r.DirectRef)).ToArray();
        var floor = 0.02 * totals.Average(t => t.Ref);
        var totalRatios = totals.Where(t => t.Ref > floor).Select(t => t.Field / t.Ref).OrderBy(v => v).ToArray();
        if (totalRatios.Length > 0)
        {
            Console.WriteLine(string.Create(Inv,
                $"    total indirect (clipmap field alone): mean {totals.Average(t => t.Field):0.00000} against {totals.Average(t => t.Ref):0.00000}, ratio median {totalRatios[totalRatios.Length / 2]:0.00} (p10 {totalRatios[totalRatios.Length / 10]:0.00}, p90 {totalRatios[totalRatios.Length * 9 / 10]:0.00}) over {totalRatios.Length}, mean |error| {totals.Average(t => Math.Abs(t.Field - t.Ref)):0.00000}"));
        }
        if (clipmapRead is not null)
        {
            var floorRef = 0.02 * used.Average(r => r.BounceRef + r.SkyBounceRef + r.DirectRef);
            static string Q2(IEnumerable<double> values)
            {
                var v = values.OrderBy(x => x).ToArray();
                return string.Create(Inv, $"median {v[v.Length / 2]:0.00} (p10 {v[v.Length / 10]:0.00}, p90 {v[v.Length * 9 / 10]:0.00})");
            }
            foreach (var (name, pick) in new (string, Func<double, bool>)[] { ("all", q => true), ("ratio < 1.3", q => q < 1.3), ("ratio >= 2", q => q >= 2) })
            {
                var g = used.Where(r => !double.IsNaN(r.HitTrue) && r.BounceRef + r.SkyBounceRef + r.DirectRef > floorRef
                    && pick(r.Twin / (r.BounceRef + r.SkyBounceRef + r.DirectRef))).ToArray();
                if (g.Length == 0) continue;
                Console.WriteLine(string.Create(Inv,
                    $"    gather split, {name,-12} {g.Length,4} px: rays hitting within R {g.Average(r => r.HitShare):0.00}; near hits field {g.Average(r => r.HitField):0.00000} true {g.Average(r => r.HitTrue):0.00000}; misses field {g.Average(r => r.MissField):0.00000} true-irradiance proxy {g.Average(r => r.MissProxy):0.00000} full-length (field at the far hit) {g.Average(r => r.MissFull):0.00000} true {g.Average(r => r.MissTrue):0.00000}; full-length gather total {Q2(g.Select(r => (r.HitField + r.MissFull) / (r.BounceRef + r.SkyBounceRef + r.DirectRef)))}, mean |error| {g.Average(r => Math.Abs(r.HitField + r.MissFull - (r.BounceRef + r.SkyBounceRef + r.DirectRef))):0.00000}; sum true {g.Average(r => r.HitTrue + r.MissTrue):0.00000} against reference {g.Average(r => r.BounceRef + r.SkyBounceRef + r.DirectRef):0.00000}"));
            }
        }
        if (clipmapRead is not null) WriteLeakAttribution(used.Select(r => (r.Bounce, Ref: r.BounceRef + r.SkyBounceRef + r.DirectRef, r.Twin, r.LeakShare, r.Visible, r.Level, r.Distance, r.Gtao, r.ProbeValue, r.ProbeRef, r.ProbeDistance, r.Ao05, r.Ao1, r.Ao2, r.G1, r.G2, r.G2k8, r.G2k16)).ToArray());
    }
}
