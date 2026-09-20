using System.Numerics;
using Blix.Graphics.Vulkan;

namespace Blix.Demos.VulkanSponza;

// A CPU path-traced reference for the probe field.
//
// <b>Built because three hand-built expectations for the field's magnitude were wrong in a row.</b>
// The runtime solve has been shown self-consistent — its ray closure matches the cook's independent
// sky-visibility bake to 0.002 across a 5.5x range of enclosure — but self-consistency says the
// transport loses nothing, not that the answer is right. "Is the bounce the correct BRIGHTNESS"
// needs something to be correct against, and arithmetic on the back of an envelope has now failed
// at that three times: once comparing the bounce field against the sky floor it is not supposed to
// carry, once against a sunlit floor the probes turned out to be ten metres above, and once with a
// binary sunlit counter when the effect was in the magnitude.
//
// So this shares the DATA and computes the TRANSPORT independently: the same occupancy grid, the
// same per-cell albedo, the same sun direction and measured irradiance, and a plain brute-force
// path trace over them. A disagreement is then about the solve rather than about the scene, which
// is the only way the comparison means anything.
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
    // <b>Stochastic, and at the cell it ENTERS rather than the cell's centre.</b> Both were wrong in
    // the first version and both bias directionally, which matters because the disagreement this
    // reference found was itself directional. A partial cell is partially-occluding geometry, so the
    // unbiased treatment is to interact there with probability equal to its density and pass through
    // otherwise — the previous rule accumulated transmittance and declared a hit once it fell under
    // a half, which turns a 0.3-density leaf cell into a guaranteed miss and three of them into a
    // guaranteed hit. And reporting the voxel centre put every interaction up to half a cell further
    // along the ray than it happened, always in the ray's own direction.
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
                // <b>Albedo comes from the CELL, not from the point of entry.</b> The entry point
                // sits exactly on the boundary between this cell and the one before it, and now
                // that the albedo grid matches the occupancy grid one to one, flooring it lands in
                // the empty neighbour as often as not — which returns black and silently deletes
                // the bounce. The runtime reads the cell centre; so must this. The entry point is
                // still where the ray continues from, which is what it was introduced for.
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

    // <b>The expected transmittance, not a coin flip.</b> A shadow ray wants the FRACTION of light
    // that survives, and the product of (1 - density) along the path is exactly that with none of
    // the variance a stochastic answer would add. It also matches what a partial cell means: a leaf
    // canopy at 0.3 per cell passes 70% of the sun, and the binary version this replaced called that
    // either fully lit or fully shadowed depending on how many cells it happened to cross.
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
        var irr = vk.ReadTexture(bounceTextures[BounceRead], out var w, out var h, out _);
        var toSun = -Vector3.Normalize(sunDirection);
        var sunIrr = EffectiveSunIrradiance;

        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] probe reference — CPU path trace, sun only, {paths} paths x {bounces} bounces:"));
        // <b>Closure and visibility beside the radiance, because they separate two explanations.</b>
        // If the +Z probes disagree on closure too, the two marches are finding different geometry
        // and the fault is in transport. If closure matches while radiance does not, the geometry is
        // agreed and something about what the surfaces RADIATE differs — which is a much smaller
        // search. The closure instrument already matched the cook's bake to 0.002 in aggregate; the
        // question is whether it still does at these specific probes.
        var depth = vk.ReadTexture(bounceDepthTextures[BounceRead], out var dw, out var dh, out _);
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
            // <b>The 6x6 interior, not the 8x8 tile.</b> The border ring is a duplicate of the edge
            // texels, wrapped for filtering — averaging the whole tile counts those twice and tilts
            // the mean toward whichever directions happen to sit on the octahedral seam. That is a
            // directional bias in a comparison whose finding was directional.
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
}
