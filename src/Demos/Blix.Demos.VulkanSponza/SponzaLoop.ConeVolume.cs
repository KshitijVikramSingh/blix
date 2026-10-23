using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --cone-volume (with --cone-lookups; the runtime gather's model): voxel cone tracing's opacity along the cone, in front
// of the ray's exact hit. The patches splat into an anisotropic volume -- per voxel, per direction a ray travels (+x -x
// +y -y +z -z), the coverage the surfaces there give it (their area x |n . axis| over the voxel's face, to 1) and the
// light they send back toward it, premultiplied (the faces facing the ray: albedo / pi x their incident light, the
// sun's column with their own direct sun; a back face is dark) -- and its mip levels composite front to back along each
// direction, then average across. texel_nodes_lean.comp marches a cone from where its footprint reaches a voxel to a
// footprint short of its hit, the level by its footprint, and lights what is left unoccluded by the hit's footprint (or
// the sky). Rebuilt on the CPU at each light change from the patches' light read back (a prototype).
internal sealed partial class SponzaLoop
{
    private bool coneVolumeOn;
    private GpuBufferHandle coneVolumeBuffer, coneVolumeMetaBuffer;
    private int coneVolumeLevels;
    private float coneVolumeVoxel, coneVolumeExtent;
    private (Vector3, int) coneVolumeFor = (new Vector3(float.NaN), -1);

    private bool ConeVolumeReady => coneVolumeOn && !coneVolumeBuffer.Equals(default(GpuBufferHandle));

    private void BuildConeVolumeNow()
    {
        if (!coneVolumeOn || cookedPacked is not { } packed || sunNodePatchLight.Equals(default(GpuBufferHandle)) || solveBuffers.Length < 8) return;
        var key = (liveSunToward, liveSunColumn);
        if (coneVolumeFor == key) return;
        coneVolumeFor = key;
        var clock = Stopwatch.StartNew();
        var patches = packed.Length / 3;
        var columns = sunNodeKinds.Length;
        var skyColumn = Math.Max(0, Array.IndexOf(sunNodeKinds, 2));
        var sunColumn = Math.Max(0, Array.IndexOf(sunNodeKinds, 1));
        device.WaitIdle();
        var light = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(sunNodePatchLight, 0, patches * columns * 8).AsSpan()).ToArray();
        var terms = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(solveBuffers[7], 0, patches * 48).AsSpan()).ToArray();
        var direct = liveDirect.Equals(default(GpuBufferHandle)) ? new uint[patches * 2]
            : MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(liveDirect, 0, patches * 8).AsSpan()).ToArray();
        static Vector3 Half3(uint a, uint b) => new((float)BitConverter.UInt16BitsToHalf((ushort)(a & 0xFFFF)), (float)BitConverter.UInt16BitsToHalf((ushort)(a >> 16)), (float)BitConverter.UInt16BitsToHalf((ushort)(b & 0xFFFF)));
        var irr = EffectiveSunIrradiance;
        // Bounds and the finest voxel: the patch spacing, doubled until the finest level is at most 2 M voxels.
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        for (var i = 0; i < patches; i++) { var p = new Vector3(packed[3 * i].X, packed[3 * i].Y, packed[3 * i].Z); lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        var v0 = transportSpacing;
        lo -= new Vector3(v0); hi += new Vector3(v0);
        (int X, int Y, int Z) Dims(float v) => ((int)MathF.Ceiling((hi.X - lo.X) / v), (int)MathF.Ceiling((hi.Y - lo.Y) / v), (int)MathF.Ceiling((hi.Z - lo.Z) / v));
        while ((long)Dims(v0).X * Dims(v0).Y * Dims(v0).Z > 2_000_000) v0 *= 2f;
        var d0 = Dims(v0);
        // Level 0: per voxel per direction, coverage and premultiplied light (sky rgb, sun rgb): 7 floats.
        const int F = 7;
        var n0 = d0.X * d0.Y * d0.Z;
        var cur = new float[(long)n0 * 6 * F];
        var area = transportSpacing * transportSpacing;
        var axes = new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        for (var i = 0; i < patches; i++)
        {
            var p = new Vector3(packed[3 * i].X, packed[3 * i].Y, packed[3 * i].Z);
            var n = new Vector3(packed[3 * i + 1].X, packed[3 * i + 1].Y, packed[3 * i + 1].Z);
            if (n.LengthSquared() < 1e-12f) continue;
            n = Vector3.Normalize(n);
            var c = Vector3.Clamp((p - lo) / v0, Vector3.Zero, new Vector3(d0.X - 1, d0.Y - 1, d0.Z - 1));
            var vi = ((int)c.Z * d0.Y + (int)c.Y) * d0.X + (int)c.X;
            var albedo = new Vector3(terms[3 * i].X, terms[3 * i].Y, terms[3 * i].Z) / MathF.PI;
            var sky = Half3(light[2 * (i * columns + skyColumn)], light[2 * (i * columns + skyColumn) + 1]);
            var sun = Half3(light[2 * (i * columns + sunColumn)], light[2 * (i * columns + sunColumn) + 1]);
            var dn = direct[2 * i + 1] > 0 ? direct[2 * i] / 4096f / direct[2 * i + 1] : 0f;
            var loSky = albedo * sky;
            var loSun = albedo * (sun + irr * dn);
            for (var d = 0; d < 6; d++)
            {
                var cosA = Vector3.Dot(n, axes[d]);
                var a = area * MathF.Abs(cosA) / (v0 * v0);
                if (a <= 0f) continue;
                var at = ((long)vi * 6 + d) * F;
                cur[at] += a;
                if (cosA < 0f)   // faces the ray travelling along the axis: its light comes back toward the ray
                {
                    cur[at + 1] += a * loSky.X; cur[at + 2] += a * loSky.Y; cur[at + 3] += a * loSky.Z;
                    cur[at + 4] += a * loSun.X; cur[at + 5] += a * loSun.Y; cur[at + 6] += a * loSun.Z;
                }
            }
        }
        // Coverage to 1, the light premultiplied by it (the sum's light per coverage x the coverage).
        for (long e = 0; e < (long)n0 * 6; e++)
        {
            var a = cur[e * F];
            if (a <= 0f) continue;
            var cl = MathF.Min(a, 1f);
            for (var f = 1; f < F; f++) cur[e * F + f] *= cl / a;
            cur[e * F] = cl;
        }
        // Mips: along each direction the two children composited front to back, then the four across averaged.
        var levels = new List<(float[] Data, (int X, int Y, int Z) Dims, float Voxel)> { (cur, d0, v0) };
        while (levels.Count < 6)
        {
            var (pd, pdims, pv) = levels[^1];
            if (pdims.X <= 2 && pdims.Y <= 2 && pdims.Z <= 2) break;
            var nd = ((pdims.X + 1) / 2, (pdims.Y + 1) / 2, (pdims.Z + 1) / 2);
            var next = new float[(long)nd.Item1 * nd.Item2 * nd.Item3 * 6 * F];
            Parallel.For(0, nd.Item3, z =>
            {
                for (var y = 0; y < nd.Item2; y++)
                    for (var x = 0; x < nd.Item1; x++)
                        for (var d = 0; d < 6; d++)
                        {
                            var axis = d / 2;
                            var forward = d % 2 == 0;
                            var acc = new float[F];
                            for (var u = 0; u < 2; u++)
                                for (var w = 0; w < 2; w++)
                                {
                                    float alpha = 0; var col = new float[F - 1];
                                    for (var step = 0; step < 2; step++)
                                    {
                                        var s = forward ? step : 1 - step;
                                        int cx = 2 * x, cy = 2 * y, cz = 2 * z;
                                        if (axis == 0) { cx += s; cy += u; cz += w; } else if (axis == 1) { cy += s; cx += u; cz += w; } else { cz += s; cx += u; cy += w; }
                                        if (cx >= pdims.X || cy >= pdims.Y || cz >= pdims.Z) continue;
                                        var at = (((long)cz * pdims.Y + cy) * pdims.X + cx) * 6 * F + d * F;
                                        var a = pd[at];
                                        for (var f = 1; f < F; f++) col[f - 1] += (1f - alpha) * pd[at + f];
                                        alpha += (1f - alpha) * a;
                                    }
                                    acc[0] += alpha / 4f;
                                    for (var f = 1; f < F; f++) acc[f] += col[f - 1] / 4f;
                                }
                            var o = (((long)z * nd.Item2 + y) * nd.Item1 + x) * 6 * F + d * F;
                            for (var f = 0; f < F; f++) next[o + f] = acc[f];
                        }
            });
            levels.Add((next, nd, pv * 2f));
        }
        // Pack: per voxel per direction a uvec4 of halves (coverage, sky rgb, sun rgb); the levels' meta.
        long total = levels.Sum(l => (long)l.Dims.X * l.Dims.Y * l.Dims.Z * 6);
        var words = new uint[total * 4];
        var meta = new Vector4[levels.Count * 2];
        long offset = 0;
        static uint H2(float a, float b) => BitConverter.HalfToUInt16Bits((Half)Math.Min(a, 65000f)) | ((uint)BitConverter.HalfToUInt16Bits((Half)Math.Min(b, 65000f)) << 16);
        for (var l = 0; l < levels.Count; l++)
        {
            var (data, dims, v) = levels[l];
            meta[2 * l] = new Vector4(lo, v);
            meta[2 * l + 1] = new Vector4(dims.X, dims.Y, dims.Z, offset);
            var count = (long)dims.X * dims.Y * dims.Z * 6;
            for (long e = 0; e < count; e++)
            {
                var at = e * F;
                var o = (offset + e) * 4;
                words[o] = H2(data[at], data[at + 1]);
                words[o + 1] = H2(data[at + 2], data[at + 3]);
                words[o + 2] = H2(data[at + 4], data[at + 5]);
                words[o + 3] = H2(data[at + 6], 0f);
            }
            offset += count;
        }
        if (!coneVolumeBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(coneVolumeBuffer);
        if (!coneVolumeMetaBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(coneVolumeMetaBuffer);
        coneVolumeBuffer = Own(device.CreateGpuBuffer(checked((int)(words.LongLength * 4)), MemoryMarshal.AsBytes(words.AsSpan()), "sponza.cone-volume"));
        coneVolumeMetaBuffer = Own(device.CreateGpuBuffer(meta.Length * 16, MemoryMarshal.AsBytes(meta.AsSpan()), "sponza.cone-volume.meta"));
        coneVolumeLevels = levels.Count;
        coneVolumeVoxel = v0;
        coneVolumeExtent = (hi - lo).Length();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] cone volume: {levels.Count} levels from {v0:0.###} m ({d0.X} x {d0.Y} x {d0.Z}), {words.LongLength * 4 / 1048576.0:0.0} MB, from {patches:N0} patches in {clock.Elapsed.TotalMilliseconds:0} ms."));
    }
}
