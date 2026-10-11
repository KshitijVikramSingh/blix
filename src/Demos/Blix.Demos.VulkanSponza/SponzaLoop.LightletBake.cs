using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;

namespace Blix.Demos.VulkanSponza;

// --lightlets (plan.md, LIGHTLETS phase 1): every live texel's SUN RECORD -- its signed angular distance to its visibility
// boundary over the plausible band of suns (SponzaLoop.LightletCensus: the band; SponzaLoop.LightletRecord: why this
// shape) -- baked once per scene on Metal against the frame's tracer (what the traced shadows trace, alpha test
// included), saved beside the light bake.
//
// The record, adaptive and 4-bit (phase 0b: identical to the fine grid at ~90 B, 1.5% of pixels tracing at tau 2 deg,
// 0.001% misjudged): the fine grid is RecordNodesHour x RecordNodesDec nodes over hour angle x declination at the band's
// reference latitude; its every other node is the coarse grid, kept whole; a coarse cell the boundary crosses keeps its
// five other fine nodes too.
//   index[texel]  0 never lit over the band, 1 always lit, else 2 + the record's first word in the pool
//   record        16 words: the 128 coarse nodes' codes (8 a word, node j * 16 + i); 4 words: the refined mask over the
//                 15 x 7 coarse cells (cell cj * 15 + ci); then one word a refined cell, in mask order: its fine nodes
//                 (2ci+1, 2cj), (2ci, 2cj+1), (2ci+1, 2cj+1), (2ci+2, 2cj+1), (2ci+1, 2cj+2), a code each from bit 0
//   code          4 bits: bit 3 the sign (1: visible), bits 0-2 the magnitude's level in LightletLevels (toward zero)
//
// The bake, in two stages a slice of texels: (1) every fine node and a 4 deg ring of 4 around each (the dense pass); a
// node is NEAR the boundary where its ring disagrees or a neighbouring node's sign differs; (2) rings at
// LightletRingRadii x 8 around the near nodes only, the distance the middle of the first gap that disagrees. A node not
// near is LightletFar away: a lower bound (a margin is never overstated: understated, a pixel traces; overstated, it
// could be judged wrong). A texel with no near node and one sign everywhere is uniform: no record.
internal sealed partial class SponzaLoop
{
    private bool lightletsAsked, lightletsThenExit;
    // 2: baked on the frame tracer with its alpha test (1: the bake scene and the coverage coin, which disagreed with the
    // traced shadows under foliage).
    private const int LightletVersion = 2;
    private const int CoarseHour = 16, CoarseDec = 8;
    private const float LightletFar = 4f;
    private static readonly float[] LightletRingRadii = { 0.25f, 0.5f, 1f, 2f, 3f, 4f };
    private static readonly float[] LightletLevels = { 0f, 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f };
    private const float LightletLift = 0.002f;
    private const uint LightletSeed = 0x7A3E11u;
    private const int LightletSlice = 65536;

    internal sealed record LightletRecords(uint[] Index, uint[] Pool);

    // What the bake needs from UploadTexels, held until the tracer is there; the records once baked or read.
    private (Vector4[] Texels, uint[] Live, int Count, string Path, float North)? lightletInputs;
    private Task<LightletRecords>? lightletTask;
    private LightletRecords? lightlets;
    private GpuBufferHandle lightletIndexBuffer, lightletPoolBuffer;
    private bool LightletBakePending => lightletInputs is not null || lightletTask is { IsCompleted: false };
    private float lightletNorth;
    private GpuBufferHandle lightletPlaceholder;
    private GpuBufferHandle LightletPlaceholder => lightletPlaceholder.Equals(default(GpuBufferHandle))
        ? lightletPlaceholder = Own(device.CreateGpuBuffer(16, name: "sponza.lightlets.none")) : lightletPlaceholder;

    // rt_shadow_rays.comp's uLightlet: the sun in the band's fine node coordinates, whether the records answer this
    // frame (RenderSettings.LightletSun, records up, the sun inside the band), and tau.
    private Vector4 LightletUniform(Vector3 towardSun)
    {
        if (!render.LightletSun || lightlets is null || !halfLightingNow) return Vector4.Zero;
        if (towardSun.Y <= MathF.Sin(BandMinElevation * MathF.PI / 180f)) return Vector4.Zero;
        var (hour, dec) = Equatorial(towardSun, BandLatitude, lightletNorth);
        var maxDec = Obliquity + 5f;
        var fi = (hour + RecordHourSpan) / (2f * RecordHourSpan) * (RecordNodesHour - 1);
        var fj = (dec + maxDec) / (2f * maxDec) * (RecordNodesDec - 1);
        if (fi < 0f || fj < 0f || fi > RecordNodesHour - 1 || fj > RecordNodesDec - 1) return Vector4.Zero;
        return new Vector4(fi, fj, 1f, render.LightletTau);
    }

    private string LightletPathFor(string bakePath, float north) =>
        bakePath + string.Create(CultureInfo.InvariantCulture,
            $".sun-v{LightletVersion}-{BandLatitude:0}n{Obliquity + 5f:0.0}d{RecordHourSpan:0}h{RecordNodesHour}x{RecordNodesDec}-north{north * 180f / MathF.PI:0.00}");

    // UploadTexels, with --lightlets: the records from their file, or a bake once the tracer is there.
    private void BeginLightlets(Vector4[] texels, uint[] live, int count, string? bakePath)
    {
        if (!lightletsAsked || bakePath is null) return;
        var north = BandNorth(out _);
        lightletNorth = north;
        var path = LightletPathFor(bakePath, north);
        if (ReadLightlets(path, count) is { } read)
        {
            UploadLightlets(read, "read from the cache");
            return;
        }
        lightletInputs = (texels, live, count, path, north);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] lightlets: no sun records cached; baking {live.Length:N0} texels once the tracer is up ({RecordNodesHour * RecordNodesDec} nodes over the band)."));
    }

    // Each frame: start the bake when a tracer is up (the bake scene's, full detail; the frame's without one), land it.
    private void LightletBakeWhenReady()
    {
        if (lightletTask is { IsCompleted: true } done)
        {
            lightletTask = null;
            if (done.IsFaulted) Console.WriteLine($"[VulkanSponza] lightlets: bake failed ({done.Exception?.GetBaseException().Message}).");
            else UploadLightlets(done.Result, "baked");
            if (lightletsThenExit)
            {
                Console.WriteLine("[VulkanSponza] Exiting once the lightlets landed, as asked.");
                host.RequestClose();
            }
        }
        if (lightletInputs is not { } inputs || lightletTask is not null) return;
        // The FRAME tracer, with its alpha test: the records must answer what the traced shadows would (the bake scene's
        // full detail with the coverage coin disagreed under the tree's leaves).
        var tracer = EnsureFrameTracer() ? frameTracer : null;
        if (tracer is null || rayQueries is null) return;
        lightletInputs = null;
        var bounds = rayQueries.Nodes[0].Bounds;
        var reach = (bounds.Max - bounds.Min).Length();
        lightletTask = Task.Run(() =>
        {
            var records = BakeLightlets(tracer, inputs.Texels, inputs.Live, inputs.Count, inputs.North, reach);
            WriteLightlets(inputs.Path, records);
            return records;
        });
    }

    private static uint LightletCode(float v)
    {
        var m = MathF.Abs(v);
        var level = 0;
        for (var k = 0; k < LightletLevels.Length; k++) if (LightletLevels[k] <= m) level = k;
        return (v > 0f ? 8u : 0u) | (uint)level;
    }

    private static LightletRecords BakeLightlets(MetalRayTracer tracer, Vector4[] texels, uint[] live, int count, float north, float reach)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var nodes = RecordNodesHour * RecordNodesDec;
        var maxDec = Obliquity + 5f;
        var nodeDirs = new Vector4[nodes];
        for (var j = 0; j < RecordNodesDec; j++)
        for (var i = 0; i < RecordNodesHour; i++)
            nodeDirs[j * RecordNodesHour + i] = new Vector4(ToScene(SunLocal(BandLatitude,
                -maxDec + j * 2f * maxDec / (RecordNodesDec - 1), -RecordHourSpan + i * 2f * RecordHourSpan / (RecordNodesHour - 1)), north), 0f);
        // The dense table: each node, then its 4 deg ring of 4.
        const int perNode = 5;
        var dense = new Vector4[nodes * perNode];
        var ringTan = MathF.Tan(LightletFar * MathF.PI / 180f);
        for (var k = 0; k < nodes; k++)
        {
            var d = new Vector3(nodeDirs[k].X, nodeDirs[k].Y, nodeDirs[k].Z);
            var u = Vector3.Normalize(MathF.Abs(d.Y) < 0.999f ? Vector3.Cross(d, Vector3.UnitY) : Vector3.Cross(d, Vector3.UnitX));
            var v = Vector3.Cross(d, u);
            dense[k * perNode] = nodeDirs[k];
            for (var a = 0; a < 4; a++)
            {
                var ang = a * MathF.PI / 2f + 0.4f;
                dense[k * perNode + 1 + a] = new Vector4(Vector3.Normalize(d + (u * MathF.Cos(ang) + v * MathF.Sin(ang)) * ringTan), 0f);
            }
        }
        var words = (dense.Length + 31) / 32;
        var tanRadii = LightletRingRadii.Select(r => MathF.Tan(r * MathF.PI / 180f)).ToArray();
        const int azimuths = 8;
        bool Bit(uint[] b, long word0, int k) => (b[word0 + k / 32] >> (k % 32) & 1u) != 0;

        var index = new uint[count];
        var pool = new List<uint>(1 << 24);
        long denseRays = 0, ringRays = 0;
        double gpuMs = 0;
        long uniformCount = 0, refinedCells = 0;
        for (var first = 0; first < live.Length; first += LightletSlice)
        {
            var n = Math.Min(LightletSlice, live.Length - first);
            var points = new Vector4[n * 2];
            for (var p = 0; p < n; p++)
            {
                var id = (int)live[first + p];
                points[2 * p] = new Vector4(texels[2 * id].X, texels[2 * id].Y, texels[2 * id].Z, 0f);
                points[2 * p + 1] = new Vector4(texels[2 * id + 1].X, texels[2 * id + 1].Y, texels[2 * id + 1].Z, 0f);
            }
            var bits = tracer.LightletVisibility(points, dense, words, LightletSeed, LightletLift, reach, out var ms);
            gpuMs += ms; denseRays += (long)n * dense.Length;

            // Per point: each node's sign, and which nodes are near the boundary.
            var sign = new bool[n * nodes];
            var near = new bool[n * nodes];
            var ringDisagrees = new bool[n * nodes];
            Parallel.For(0, n, p =>
            {
                long w0 = (long)p * words;
                for (var k = 0; k < nodes; k++)
                {
                    var s = Bit(bits, w0, k * perNode);
                    sign[p * nodes + k] = s;
                    for (var a = 1; a < perNode; a++) if (Bit(bits, w0, k * perNode + a) != s) { ringDisagrees[p * nodes + k] = true; break; }
                }
                for (var j = 0; j < RecordNodesDec; j++)
                for (var i = 0; i < RecordNodesHour; i++)
                {
                    var k = j * RecordNodesHour + i;
                    var isNear = ringDisagrees[p * nodes + k];
                    for (var dj = -1; dj <= 1 && !isNear; dj++)
                    for (var di = -1; di <= 1 && !isNear; di++)
                    {
                        var (ii, jj) = (i + di, j + dj);
                        if (ii < 0 || jj < 0 || ii >= RecordNodesHour || jj >= RecordNodesDec) continue;
                        if (sign[p * nodes + jj * RecordNodesHour + ii] != sign[p * nodes + k]) isNear = true;
                    }
                    near[p * nodes + k] = isNear;
                }
            });
            var pairs = new List<uint>();
            for (var p = 0; p < n; p++)
                for (var k = 0; k < nodes; k++)
                    if (near[p * nodes + k]) { pairs.Add((uint)p); pairs.Add((uint)k); }
            var ringBits = pairs.Count == 0 ? Array.Empty<uint>()
                : tracer.LightletRings(points, CollectionsMarshal.AsSpan(pairs), nodeDirs, tanRadii, azimuths, LightletSeed, LightletLift, reach, out ms);
            if (pairs.Count > 0) { gpuMs += ms; ringRays += (long)pairs.Count / 2 * LightletRingRadii.Length * azimuths; }

            // Distances, then the records.
            var dist = new float[n * nodes];
            for (var p = 0; p < n * nodes; p++) dist[p] = sign[p] ? LightletFar : -LightletFar;
            for (var q = 0; q < pairs.Count / 2; q++)
            {
                var p = (int)pairs[2 * q];
                var k = (int)pairs[2 * q + 1];
                var s = sign[p * nodes + k];
                var d = ringDisagrees[p * nodes + k] ? 0.5f * (LightletRingRadii[^2] + LightletFar) : LightletFar;
                var prev = 0f;
                for (var r = 0; r < LightletRingRadii.Length; r++)
                {
                    var any = false;
                    for (var a = 0; a < azimuths && !any; a++) any = Bit(ringBits, 2L * q, r * azimuths + a) != s;
                    if (any) { d = 0.5f * (prev + LightletRingRadii[r]); break; }
                    prev = LightletRingRadii[r];
                }
                dist[p * nodes + k] = s ? d : -d;
            }
            for (var p = 0; p < n; p++)
            {
                var id = live[first + p];
                var s0 = sign[p * nodes];
                var uniform = true;
                for (var k = 0; k < nodes && uniform; k++) uniform = !near[p * nodes + k] && sign[p * nodes + k] == s0;
                if (uniform) { index[id] = s0 ? 1u : 0u; uniformCount++; continue; }
                index[id] = 2u + (uint)pool.Count;
                float D(int i, int j) => dist[p * nodes + j * RecordNodesHour + i];
                for (var w = 0; w < 16; w++)
                {
                    uint word = 0;
                    for (var b = 0; b < 8; b++)
                    {
                        var c = w * 8 + b;
                        word |= LightletCode(D(2 * (c % CoarseHour), 2 * (c / CoarseHour))) << (4 * b);
                    }
                    pool.Add(word);
                }
                var mask = new uint[4];
                var extra = new List<uint>();
                for (var cj = 0; cj < CoarseDec - 1; cj++)
                for (var ci = 0; ci < CoarseHour - 1; ci++)
                {
                    var refine = false;
                    for (var dj = 0; dj <= 2 && !refine; dj++)
                    for (var di = 0; di <= 2 && !refine; di++)
                        refine = MathF.Abs(D(2 * ci + di, 2 * cj + dj)) < LightletFar;
                    if (!refine) continue;
                    var cell = cj * (CoarseHour - 1) + ci;
                    mask[cell / 32] |= 1u << (cell % 32);
                    extra.Add(LightletCode(D(2 * ci + 1, 2 * cj)) | LightletCode(D(2 * ci, 2 * cj + 1)) << 4 | LightletCode(D(2 * ci + 1, 2 * cj + 1)) << 8
                              | LightletCode(D(2 * ci + 2, 2 * cj + 1)) << 12 | LightletCode(D(2 * ci + 1, 2 * cj + 2)) << 16);
                }
                pool.AddRange(mask);
                pool.AddRange(extra);
                refinedCells += extra.Count;
            }
            if (first / LightletSlice % 32 == 0)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] lightlets: {100.0 * (first + n) / live.Length:0}% ({clock.Elapsed.TotalMinutes:0.0} min)."));
        }
        var nonUniform = Math.Max(1, live.Length - uniformCount);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] lightlets: {live.Length:N0} texels baked in {clock.Elapsed.TotalMinutes:0.0} min ({(denseRays + ringRays) / 1e9:0.0} G rays, {gpuMs / 1000:0} s GPU): " +
            $"{100.0 * uniformCount / live.Length:0.0}% uniform over the band, {(double)refinedCells / nonUniform:0.0} refined cells a record; pool {pool.Count * 4 / 1048576.0:0} MB, index {count * 4 / 1048576.0:0} MB."));
        return new LightletRecords(index, pool.ToArray());
    }

    private void UploadLightlets(LightletRecords records, string how)
    {
        lightlets = records;
        lightletIndexBuffer = Own(device.CreateGpuBuffer(records.Index.Length * 4, MemoryMarshal.AsBytes(records.Index.AsSpan()), "sponza.lightlets.index"));
        lightletPoolBuffer = Own(device.CreateGpuBuffer(Math.Max(records.Pool.Length, 1) * 4, MemoryMarshal.AsBytes(records.Pool.AsSpan()), "sponza.lightlets.pool"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] lightlets: sun records {how} ({records.Index.Length:N0} texels, pool {records.Pool.Length * 4 / 1048576.0:0} MB)."));
    }

    private static void WriteLightlets(string path, LightletRecords records)
    {
        var partial = path + ".partial";
        try
        {
            using (var w = new BinaryWriter(new BufferedStream(File.Create(partial), 1 << 22)))
            {
                w.Write("blix-lightlets");
                w.Write(LightletVersion);
                w.Write(records.Index.Length);
                w.Write(records.Pool.Length);
                w.Write(MemoryMarshal.AsBytes(records.Index.AsSpan()));
                w.Write(MemoryMarshal.AsBytes(records.Pool.AsSpan()));
            }
            File.Move(partial, path, overwrite: true);
            Console.WriteLine($"[VulkanSponza] lightlets: saved ({new FileInfo(path).Length / 1048576.0:0} MB): {Path.GetFileName(path)}");
        }
        catch (IOException e)
        {
            try { File.Delete(partial); } catch (IOException) { }
            Console.WriteLine($"[VulkanSponza] lightlets: not saved ({e.Message}).");
        }
    }

    private static LightletRecords? ReadLightlets(string path, int count)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var r = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 22));
            if (r.ReadString() != "blix-lightlets" || r.ReadInt32() != LightletVersion || r.ReadInt32() != count) return null;
            var poolLength = r.ReadInt32();
            var index = MemoryMarshal.Cast<byte, uint>(r.ReadBytes(count * 4)).ToArray();
            var pool = MemoryMarshal.Cast<byte, uint>(r.ReadBytes(poolLength * 4)).ToArray();
            return index.Length == count && pool.Length == poolLength ? new LightletRecords(index, pool) : null;
        }
        catch (IOException) { return null; }
    }
}
