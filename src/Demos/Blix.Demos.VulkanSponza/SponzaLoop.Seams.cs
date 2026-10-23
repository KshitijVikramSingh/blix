using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The texel filter across the lightmap's chart seams (texel_compose.comp, mode 1). An atlas texel's filter takes the 3 x 3
// atlas texels around it, so it never reaches across a seam: each chart -- each stone of a pillar -- was smoothed on its
// own, and the noise at its border stayed (2026-10-14, Sponza: texels touching across a seam differed 9.9% at the median
// against 2.9% inside a chart, p90 61% against 28%; the borders unbiased, signed mean -0.07%, but twice as noisy -- they
// have the fewest neighbours of their own). The user saw it as blocks of stone lit each its own way.
// So each atlas texel also lists the texels of OTHER charts that touch it in the world: within a texel (4.5 cm), on its
// plane (1 cm), facing its way (cos 0.95), not its neighbours in the atlas -- and in sight of it, a millimetre off both
// (the CPU's ray scene, while it is there): a wall thinner than the plane's centimetre has two faces turned the same way
// on one plane, and the 4 mm sheet shell's outer faces took its insides' light across it, 1.17x (2026-10-24). Built once
// from the records, here, as a list (start a texel, from the first atlas record; then the ids).
internal sealed partial class SponzaLoop
{
    private const int SeamNeighbourCap = 6;
    // A pixel's light blended across its chart's seams by these lists (incident_clipmap.frag, seamBlend; default,
    // --no-seam-blend: its own chart's texels alone).
    private bool seamBlend;
    private bool SeamBlendNow => seamBlend && !texelSeamStart.Equals(default(GpuBufferHandle));
    private IEnumerable<ShaderBufferBinding> SeamBlendBindings()
    {
        var stand = SeamBlendNow ? texelSeamStart : clipmapState;
        yield return new ShaderBufferBinding("TexelSeamStart", stand);
        yield return new ShaderBufferBinding("TexelSeamIds", SeamBlendNow ? texelSeamIds : clipmapState);
    }
    private GpuBufferHandle texelSeamStart, texelSeamIds;

    private void BuildSeamNeighbours(Vector4[] texels, int count, int firstAtlas)
    {
        if (firstAtlas < 0 || firstAtlas >= count) return;
        // A stage of the GI bundle (--cook-gi), under the lightmap records' key: the same records, so the same seams.
        var seamKey = lightmapRecordsKey is { } recordsKey ? recordsKey + (rayQueries is not null ? "-seen" : "") : null;
        if (seamKey is not null && LoadStage(seamKey, "seams", r => r.ReadInt32() != firstAtlas || r.ReadInt32() != count
                ? null : new SeamLists(ReadArray<uint>(r), ReadArray<uint>(r))) is { } stored)
        {
            UploadSeams(stored.Start, stored.Ids);
            return;
        }
        var (start, ids) = FindSeamNeighbours(texels, count, firstAtlas);
        if (seamKey is not null)
            SaveStage(seamKey, "seams", w => { w.Write(firstAtlas); w.Write(count); WriteArray(w, start); WriteArray(w, ids); });
        UploadSeams(start, ids);
    }

    private sealed record SeamLists(uint[] Start, uint[] Ids);

    private void UploadSeams(uint[] start, uint[] ids)
    {
        texelSeamStart = Own(device.CreateGpuBuffer(start.Length * 4, MemoryMarshal.AsBytes(start.AsSpan()), "sponza.texels.seam-start"));
        texelSeamIds = Own(device.CreateGpuBuffer(ids.Length * 4, MemoryMarshal.AsBytes(ids.AsSpan()), "sponza.texels.seam-ids"));
    }

    private (uint[] Start, uint[] Ids) FindSeamNeighbours(Vector4[] texels, int count, int firstAtlas)
    {
        var clock = Stopwatch.StartNew();
        var scene = rayQueries;
        const float Cell = 0.04f;
        var n = count - firstAtlas;
        var keys = new long[n];
        var order = new int[n];
        static long Pack(int x, int y, int z) => ((long)(x + (1 << 20)) << 42) | ((long)(y + (1 << 20)) << 21) | (long)(z + (1 << 20));
        Parallel.For(0, n, k =>
        {
            var p = texels[2 * (firstAtlas + k)];
            keys[k] = Pack((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));
            order[k] = k;
        });
        var sorted = (long[])keys.Clone();
        Array.Sort(sorted, order);
        var found = new int[n * SeamNeighbourCap];
        var counts = new int[n];
        Parallel.For(0, n, k =>
        {
            var id = firstAtlas + k;
            var p4 = texels[2 * id];
            if (MathF.Abs(p4.W) < 2f) return;
            var nr = texels[2 * id + 1];
            var p = new Vector3(p4.X, p4.Y, p4.Z);
            var nn = Vector3.Normalize(new Vector3(nr.X, nr.Y, nr.Z));
            var at = BitConverter.SingleToUInt32Bits(nr.W);
            int ax = (int)(at & 0xFFFF), ay = (int)(at >> 16);
            int cx = (int)MathF.Floor(p.X / Cell), cy = (int)MathF.Floor(p.Y / Cell), cz = (int)MathF.Floor(p.Z / Cell);
            var c = 0;
            for (var dz = -1; dz <= 1 && c < SeamNeighbourCap; dz++)
            for (var dy = -1; dy <= 1 && c < SeamNeighbourCap; dy++)
            for (var dx = -1; dx <= 1 && c < SeamNeighbourCap; dx++)
            {
                var want = Pack(cx + dx, cy + dy, cz + dz);
                var lo = Array.BinarySearch(sorted, want);
                if (lo < 0) continue;
                while (lo > 0 && sorted[lo - 1] == want) lo--;
                for (var s = lo; s < n && sorted[s] == want && c < SeamNeighbourCap; s++)
                {
                    var other = firstAtlas + order[s];
                    if (other == id) continue;
                    var o4 = texels[2 * other];
                    if (MathF.Abs(o4.W) < 2f) continue;
                    var on = texels[2 * other + 1];
                    var d = new Vector3(o4.X, o4.Y, o4.Z) - p;
                    if (d.LengthSquared() > 0.045f * 0.045f) continue;
                    if (Vector3.Dot(Vector3.Normalize(new Vector3(on.X, on.Y, on.Z)), nn) <= 0.95f) continue;
                    if (MathF.Abs(Vector3.Dot(d, nn)) > 0.01f) continue;
                    var oat = BitConverter.SingleToUInt32Bits(on.W);
                    if (Math.Abs((int)(oat & 0xFFFF) - ax) <= 2 && Math.Abs((int)(oat >> 16) - ay) <= 2) continue;
                    if (scene is not null)
                    {
                        var from = p + nn * 1e-3f;
                        var to = new Vector3(o4.X, o4.Y, o4.Z) + Vector3.Normalize(new Vector3(on.X, on.Y, on.Z)) * 1e-3f - from;
                        var len = to.Length();
                        if (len > 1e-5f && scene.Any(new Ray(from, to / len), 0f, len, (uint)(id * 31 + other))) continue;
                    }
                    found[k * SeamNeighbourCap + c++] = other;
                }
            }
            counts[k] = c;
        });
        var start = new uint[n + 1];
        for (var k = 0; k < n; k++) start[k + 1] = start[k] + (uint)counts[k];
        var ids = new uint[Math.Max(1, (int)start[n])];
        Parallel.For(0, n, k =>
        {
            for (var i = 0; i < counts[k]; i++) ids[start[k] + i] = (uint)found[k * SeamNeighbourCap + i];
        });
        var with = counts.Count(x => x > 0);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] texels: {with:N0} of {n:N0} atlas texels touch another chart ({(long)start[n]:N0} neighbours across seams, {(start.Length + ids.Length) * 4 / 1048576.0:0} MB); found in {clock.Elapsed.TotalSeconds:0.0} s."));
        return (start, ids);
    }

    // The compose filter's seam lists, placeholders without a lightmap.
    private IEnumerable<ShaderBufferBinding> SeamBindings()
    {
        if (texelSeamStart.Equals(default(GpuBufferHandle)))
        {
            texelSeamStart = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.texels.seam-start.none"));
            texelSeamIds = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.texels.seam-ids.none"));
        }
        yield return new ShaderBufferBinding("TexelSeamStart", texelSeamStart);
        yield return new ShaderBufferBinding("TexelSeamIds", texelSeamIds);
    }
}
