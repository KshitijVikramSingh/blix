using System.Numerics;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// A CPU path-traced reference for the probe field.
//
// It shares scene inputs with the runtime — occupancy, per-cell albedo, sun direction, and measured
// irradiance — but computes transport independently with a brute-force path trace. Runtime ray
// closure already matches the cook's sky-visibility bake to 0.002 across a 5.5x enclosure range;
// this reference tests absolute bounce magnitude rather than self-consistency alone.
//
// Sun only, deliberately. The question is why sunlight does not spread, the sky's contribution
// arrives through a separate path, and leaving it out removes the one input this cannot reproduce
// faithfully (the irradiance cube lives on the GPU). The runtime side of the comparison is the
// difference between a census with the sun on and one with --sun-strength 0.
internal sealed partial class SponzaLoop
{
    private byte[]? occupancyCpu;
    private byte[]? albedoCpu;
    private int occCpuX, occCpuY, occCpuZ, albCpuX, albCpuY, albCpuZ;

    private float OccupancyAt(int x, int y, int z)
    {
        if (occupancyCpu is null) return 0f;
        if ((uint)x >= (uint)occCpuX || (uint)y >= (uint)occCpuY || (uint)z >= (uint)occCpuZ) return 0f;
        return occupancyCpu[(z * occCpuY + y) * occCpuX + x] / 255f;
    }

    private Vector3 AlbedoAtCpu(Vector3 world)
    {
        if (albedoCpu is null) return new Vector3(0.27f);
        var t = (world - skyVolumeMin) / skyVolumeSpan;
        var x = Math.Clamp((int)(t.X * albCpuX), 0, albCpuX - 1);
        var y = Math.Clamp((int)(t.Y * albCpuY), 0, albCpuY - 1);
        var z = Math.Clamp((int)(t.Z * albCpuZ), 0, albCpuZ - 1);
        var i = ((z * albCpuY + y) * albCpuX + x) * 4;
        // Gamma-2.0 encoded on disk; the shader squares it back and so must this.
        var r = albedoCpu[i] / 255f;
        var g = albedoCpu[i + 1] / 255f;
        var b = albedoCpu[i + 2] / 255f;
        return new Vector3(r * r, g * g, b * b);
    }

    // Marches the occupancy grid to a scattering event.
    //
    // Treat occupancy stochastically and report the cell-entry position. Interaction probability
    // equals cell density, while the entry point avoids a directional half-cell position bias.
    private bool MarchCpu(
        Vector3 origin, Vector3 dir, Random rng,
        out Vector3 hitPos, out Vector3 hitNormal, out Vector3 hitCell)
    {
        hitPos = default;
        hitNormal = default;
        hitCell = default;
        var cellSize = skyVolumeSpan / new Vector3(occCpuX, occCpuY, occCpuZ);
        var t = (origin - skyVolumeMin) / skyVolumeSpan;
        var cell = new Vector3(t.X * occCpuX, t.Y * occCpuY, t.Z * occCpuZ);
        var c = new int[] { (int)MathF.Floor(cell.X), (int)MathF.Floor(cell.Y), (int)MathF.Floor(cell.Z) };
        var d = new[] { dir.X, dir.Y, dir.Z };
        var step = new int[3];
        var tMax = new float[3];
        var tDelta = new float[3];
        var cs = new[] { cellSize.X, cellSize.Y, cellSize.Z };
        var frac = new[] { cell.X - c[0], cell.Y - c[1], cell.Z - c[2] };
        for (var a = 0; a < 3; a++)
        {
            if (MathF.Abs(d[a]) < 1e-8f) { step[a] = 0; tMax[a] = float.MaxValue; tDelta[a] = float.MaxValue; continue; }
            step[a] = d[a] > 0 ? 1 : -1;
            tDelta[a] = cs[a] / MathF.Abs(d[a]);
            tMax[a] = (d[a] > 0 ? (1f - frac[a]) : frac[a]) * tDelta[a];
        }
        var dims = new[] { occCpuX, occCpuY, occCpuZ };
        var tEnter = 0f;         // distance at which the ray entered the current cell
        var enteredAxis = -1;    // and through which face
        for (var iter = 0; iter < 2048; iter++)
        {
            var density = OccupancyAt(c[0], c[1], c[2]);
            if (density > 0f && rng.NextDouble() < density)
            {
                hitPos = origin + dir * tEnter;
                // Sample albedo at the occupied cell centre. The entry point lies on a cell boundary
                // and may otherwise floor into the empty neighbour; ray continuation still uses it.
                hitCell = skyVolumeMin
                        + (new Vector3(c[0], c[1], c[2]) + new Vector3(0.5f)) * cellSize;
                var n = new float[3];
                if (enteredAxis >= 0) n[enteredAxis] = -step[enteredAxis];
                hitNormal = new Vector3(n[0], n[1], n[2]);
                if (hitNormal.LengthSquared() < 1e-6f) hitNormal = -dir;
                return true;
            }
            var axis = tMax[0] < tMax[1] ? (tMax[0] < tMax[2] ? 0 : 2) : (tMax[1] < tMax[2] ? 1 : 2);
            if (step[axis] == 0) return false;
            tEnter = tMax[axis];
            enteredAxis = axis;
            c[axis] += step[axis];
            if ((uint)c[axis] >= (uint)dims[axis]) return false;
            tMax[axis] += tDelta[axis];
        }
        return false;
    }

    // Shadow rays return expected transmittance: the product of (1 - density) along the path. This
    // represents fractional coverage without adding stochastic variance to direct-light visibility.
    private float SunVisibilityCpu(Vector3 from, Vector3 toSun)
    {
        var cellSize = skyVolumeSpan / new Vector3(occCpuX, occCpuY, occCpuZ);
        var t0 = (from - skyVolumeMin) / skyVolumeSpan;
        var cell = new Vector3(t0.X * occCpuX, t0.Y * occCpuY, t0.Z * occCpuZ);
        var c = new[] { (int)MathF.Floor(cell.X), (int)MathF.Floor(cell.Y), (int)MathF.Floor(cell.Z) };
        var d = new[] { toSun.X, toSun.Y, toSun.Z };
        var cs = new[] { cellSize.X, cellSize.Y, cellSize.Z };
        var frac = new[] { cell.X - c[0], cell.Y - c[1], cell.Z - c[2] };
        var step = new int[3];
        var tMax = new float[3];
        var tDelta = new float[3];
        for (var a = 0; a < 3; a++)
        {
            if (MathF.Abs(d[a]) < 1e-8f) { step[a] = 0; tMax[a] = float.MaxValue; tDelta[a] = float.MaxValue; continue; }
            step[a] = d[a] > 0 ? 1 : -1;
            tDelta[a] = cs[a] / MathF.Abs(d[a]);
            tMax[a] = (d[a] > 0 ? (1f - frac[a]) : frac[a]) * tDelta[a];
        }
        var dims = new[] { occCpuX, occCpuY, occCpuZ };
        var transmittance = 1f;
        for (var iter = 0; iter < 2048; iter++)
        {
            transmittance *= 1f - OccupancyAt(c[0], c[1], c[2]);
            if (transmittance < 0.01f) return 0f;
            var axis = tMax[0] < tMax[1] ? (tMax[0] < tMax[2] ? 0 : 2) : (tMax[1] < tMax[2] ? 1 : 2);
            if (step[axis] == 0) return transmittance;
            c[axis] += step[axis];
            if ((uint)c[axis] >= (uint)dims[axis]) return transmittance;
            tMax[axis] += tDelta[axis];
        }
        return transmittance;
    }

    private Vector3 TraceRadianceCpu(Vector3 origin, Vector3 dir, int bounces, Random rng, Vector3 toSun, Vector3 sunIrr)
    {
        if (!MarchCpu(origin, dir, rng, out var hit, out var n, out var cell)) return Vector3.Zero;
        var cellDiag = (skyVolumeSpan / new Vector3(occCpuX, occCpuY, occCpuZ)).Length();
        var off = hit + n * cellDiag * 1.5f;
        var albedo = AlbedoAtCpu(cell);
        var ndotl = MathF.Max(Vector3.Dot(n, toSun), 0f);
        var direct = ndotl > 0f ? sunIrr * (ndotl * SunVisibilityCpu(off, toSun)) : Vector3.Zero;
        var outgoing = albedo * direct / MathF.PI;
        if (bounces > 0)
        {
            var next = CosineHemisphereCpu(n, rng);
            // Cosine sampling: the pdf's cos/pi cancels the BRDF's, leaving albedo * incoming.
            outgoing += albedo * TraceRadianceCpu(off, next, bounces - 1, rng, toSun, sunIrr);
        }
        return outgoing;
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

    /// <summary>Path-traces a handful of probes and prints the reference beside what the field holds.</summary>
    internal void WriteProbeReference(int probeCount, int paths, int bounces)
    {
        if (occupancyCpu is null || !bounceReady)
        {
            Console.WriteLine("[VulkanSponza] probe reference: no occupancy grid or no bounce field.");
            return;
        }
        var irr = device.ReadTexture(bounceTextures[BounceRead], out var w, out var h, out _);
        var toSun = -Vector3.Normalize(sunDirection);
        var sunIrr = EffectiveSunIrradiance;

        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] probe reference — CPU path trace, sun only, {paths} paths x {bounces} bounces:"));
        // Report closure and visibility beside radiance. A closure mismatch points to different
        // geometry traversal; matching closure with divergent radiance narrows the fault to surface
        // emission/transport. The aggregate closure reference is within 0.002 of the cook's bake.
        var depth = device.ReadTexture(bounceDepthTextures[BounceRead], out var dw, out var dh, out _);
        Console.WriteLine("    probe (x,y,z)        world            reference    measured     ratio   closure  expected  vis");

        var rng = new Random(12345);
        double refSum = 0, gotSum = 0;
        var compared = 0;
        for (var s = 0; s < probeCount; s++)
        {
            // Spread deterministically through the volume rather than sampled at random, so two runs
            // compare the same places.
            var px = (int)((s * 7 + 3) % bounceX);
            var py = (int)((s * 5 + 2) % bounceY);
            var pz = (int)((s * 11 + 5) % bounceZ);
            var origin = skyVolumeMin + (new Vector3(px, py, pz) + new Vector3(0.5f))
                       * skyVolumeSpan / new Vector3(bounceX, bounceY, bounceZ);

            // Mean irradiance over all surface orientations, which is what the census averages:
            // sample the normal uniformly and the incoming direction cosine-about-it, then the
            // estimator for E is pi * mean(L).
            Vector3 acc = Vector3.Zero;
            for (var p = 0; p < paths; p++)
            {
                var nrm = UniformSphereCpu(rng);
                var dir = CosineHemisphereCpu(nrm, rng);
                acc += TraceRadianceCpu(origin, dir, bounces, rng, toSun, sunIrr);
            }
            var reference = acc * (MathF.PI / paths);
            var refLum = 0.2126 * reference.X + 0.7152 * reference.Y + 0.0722 * reference.Z;

            // What the field holds at the same probe: the mean over its tile.
            // Average the 6x6 interior. The 8x8 tile's duplicated filtering border would count edge
            // directions twice and bias the mean toward the octahedral seam.
            const int tile = 8;
            var x0 = px * tile;
            var y0 = (py + pz * bounceY) * tile;
            double got = 0;
            var n2 = 0;
            for (var ty = 1; ty < tile - 1; ty++)
            for (var tx = 1; tx < tile - 1; tx++)
            {
                var ix = x0 + tx;
                var iy = y0 + ty;
                if (ix >= w || iy >= h) continue;
                var o = (iy * w + ix) * 8;
                got += 0.2126 * (float)BitConverter.ToHalf(irr, o)
                     + 0.7152 * (float)BitConverter.ToHalf(irr, o + 2)
                     + 0.0722 * (float)BitConverter.ToHalf(irr, o + 4);
                n2++;
            }
            got = n2 > 0 ? got / n2 : 0;
            refSum += refLum;
            gotSum += got;
            compared++;
            var probeIndex = (pz * bounceY + py) * bounceX + px;
            var vis = probeIndex < cellSkyVisibility.Length ? cellSkyVisibility[probeIndex] : 0f;
            var cx = px * tile;
            var cy = (py + pz * bounceY) * tile;
            var closure = (cx < dw && cy < dh)
                ? (float)BitConverter.ToHalf(depth, (cy * dw + cx) * 8 + 6)
                : 0f;
            Console.WriteLine(string.Create(Inv,
                $"    ({px,3},{py,3},{pz,3})   ({origin.X,6:0.0},{origin.Y,5:0.0},{origin.Z,6:0.0})   "
                + $"{refLum,9:0.0000}   {got,9:0.0000}   {(refLum > 1e-9 ? got / refLum : 0),6:0.00}x   "
                + $"{closure,7:0.000}  {1.0 - vis,8:0.000}  {vis,5:0.00}"));
        }
        Console.WriteLine(string.Create(Inv,
            $"    TOTAL  reference {refSum / Math.Max(1, compared):0.0000}   measured {gotSum / Math.Max(1, compared):0.0000}   "
            + $"the field is {(refSum > 1e-9 ? gotSum / refSum : 0):0.00}x the reference"));
    }
    // The same reference on the scene's triangles (RayQueryScene) rather than the occupancy grid, with the GPU's
    // baked per-triangle albedo and coverage read back: the arbiter between the march and the traced injection,
    // which the grid reference cannot be, since it shares the march's geometry. Sun only, the same probes, the same
    // estimator; a leaf is met by its coverage, by chance, as the runtime meets it.
    internal void WriteProbeReferenceTriangles(int probeCount, int paths, int bounces)
    {
        if (rayQueries is null || rayGpuData is null || !bounceReady)
        {
            Console.WriteLine("[VulkanSponza] triangle probe reference: needs the ray scene on the GPU (--gi-trace or --ray-check) and a bounce field.");
            return;
        }
        var surfaces = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
            device.ReadGpuBuffer(raySurfaces, 0, rayGpuData.RowPlacements.Length * 4).AsSpan()).ToArray();
        rayGpuData.ApplyCoverage(surfaces);
        var irr = device.ReadTexture(bounceTextures[BounceRead], out var w, out var h, out _);
        var toSun = -Vector3.Normalize(sunDirection);
        var sunIrr = EffectiveSunIrradiance;
        var scene = rayQueries;
        var data = rayGpuData;

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
            var direct = ndotl > 0f && !scene.Any(new Ray(p, toSun), 0f, float.PositiveInfinity, (uint)rng.Next())
                ? sunIrr * ndotl : Vector3.Zero;
            var outgoing = albedo * direct / MathF.PI;
            if (depth > 0) outgoing += albedo * Trace(p, CosineHemisphereCpu(n, rng), depth - 1, rng);
            return outgoing;
        }

        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] triangle probe reference — CPU path trace on the ray scene, sun only, {paths} paths x {bounces} bounces:"));
        Console.WriteLine("    probe (x,y,z)        world            reference    measured     ratio");
        var results = new (int X, int Y, int Z, Vector3 Origin, double Reference, double Measured)[probeCount];
        Parallel.For(0, probeCount, s =>
        {
            var px = (s * 7 + 3) % bounceX;
            var py = (s * 5 + 2) % bounceY;
            var pz = (s * 11 + 5) % bounceZ;
            var origin = skyVolumeMin + (new Vector3(px, py, pz) + new Vector3(0.5f)) * skyVolumeSpan / new Vector3(bounceX, bounceY, bounceZ);
            var rng = new Random(12345 + s);
            var acc = Vector3.Zero;
            for (var k = 0; k < paths; k++)
            {
                var nrm = UniformSphereCpu(rng);
                acc += Trace(origin, CosineHemisphereCpu(nrm, rng), bounces, rng);
            }
            var reference = acc * (MathF.PI / paths);
            const int tile = 8;
            double got = 0;
            var n2 = 0;
            for (var ty = 1; ty < tile - 1; ty++)
            for (var tx = 1; tx < tile - 1; tx++)
            {
                var ix = px * tile + tx;
                var iy = (py + pz * bounceY) * tile + ty;
                if (ix >= w || iy >= h) continue;
                var o = (iy * w + ix) * 8;
                got += 0.2126 * (float)BitConverter.ToHalf(irr, o) + 0.7152 * (float)BitConverter.ToHalf(irr, o + 2)
                     + 0.0722 * (float)BitConverter.ToHalf(irr, o + 4);
                n2++;
            }
            results[s] = (px, py, pz, origin, 0.2126 * reference.X + 0.7152 * reference.Y + 0.0722 * reference.Z, n2 > 0 ? got / n2 : 0);
        });
        foreach (var r in results)
        {
            Console.WriteLine(string.Create(Inv,
                $"    ({r.X,3},{r.Y,3},{r.Z,3})   ({r.Origin.X,6:0.0},{r.Origin.Y,5:0.0},{r.Origin.Z,6:0.0})   {r.Reference,9:0.0000}   {r.Measured,9:0.0000}   {(r.Reference > 1e-9 ? r.Measured / r.Reference : 0),6:0.00}x"));
        }
        var refMean = results.Average(r => r.Reference);
        var gotMean = results.Average(r => r.Measured);
        Console.WriteLine(string.Create(Inv,
            $"    TOTAL  triangle reference {refMean:0.0000}   measured {gotMean:0.0000}   the field is {(refMean > 1e-9 ? gotMean / refMean : 0):0.00}x the triangle reference"));
    }
}
