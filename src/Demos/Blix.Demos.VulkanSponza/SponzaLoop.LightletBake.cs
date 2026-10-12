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
    // 3: the footprint sampled (5 points a texel; 2 took texel centres alone, and missed sub-texel dapples).
    private const int LightletVersion = 3;
    private const int CoarseHour = 16, CoarseDec = 8;
    private const float LightletFar = 4f;
    private static readonly float[] LightletRingRadii = { 0.25f, 0.5f, 1f, 2f, 3f, 4f };
    private static readonly float[] LightletLevels = { 0f, 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f };
    private const float LightletLift = 0.002f;
    private const uint LightletSeed = 0x7A3E11u;
    private const int LightletSlice = 65536;
    // The footprint: a texel's centre and corners, in atlas cells (4 cm; curtains' 2 cm cells are over-covered: safe).
    private const float LightletCell = 0.04f;
    private const int Footprint = 5;
    private static readonly (float X, float Y)[] FootprintOffsets = { (0f, 0f), (-0.5f, -0.5f), (0.5f, -0.5f), (-0.5f, 0.5f), (0.5f, 0.5f) };

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
        // --ab lightlets: the records in the ON phase, every pixel traced in the OFF one.
        var on = abMode == "lightlets" ? !AbOffPhase : render.LightletSun;
        if (!on || lightlets is null) return Vector4.Zero;
        if (towardSun.Y <= MathF.Sin(BandMinElevation * MathF.PI / 180f)) return Vector4.Zero;
        var (hour, dec) = Equatorial(towardSun, BandLatitude, lightletNorth);
        var maxDec = Obliquity + 5f;
        var fi = (hour + RecordHourSpan) / (2f * RecordHourSpan) * (RecordNodesHour - 1);
        var fj = (dec + maxDec) / (2f * maxDec) * (RecordNodesDec - 1);
        if (fi < 0f || fj < 0f || fi > RecordNodesHour - 1 || fj > RecordNodesDec - 1) return Vector4.Zero;
        return new Vector4(fi, fj, 1f, render.LightletTau);
    }

    // Keyed by the transport cache's key (the scene, the lightmap, the texel layout the records index) and the band --
    // not by a solved sun's light bake: the records are the band's, the same for every sun in it. (Hung off the light
    // bake's path, each solved sun baked and stored its own copy.)
    private string LightletPathFor(string transportKey, float north) =>
        Path.ChangeExtension(TransportCachePath(transportKey), ".lightlets") + string.Create(CultureInfo.InvariantCulture,
            $".sun-v{LightletVersion}-{BandLatitude:0}n{Obliquity + 5f:0.0}d{RecordHourSpan:0}h{RecordNodesHour}x{RecordNodesDec}-north{north * 180f / MathF.PI:0.00}");

    // UploadTexels, with --lightlets: the records from their file, or a bake once the tracer is there.
    private void BeginLightlets(Vector4[] texels, uint[] live, int count, string? bakePath)
    {
        if (!lightletsAsked || lightBakeKey is null) return;
        var north = BandNorth(out _);
        lightletNorth = north;
        var path = LightletPathFor(lightBakeKey, north);
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
            // Each texel's FOOTPRINT: its centre and its four corners (adjacent texels share corners, so the middle of
            // every 2 x 2 a pixel blends is sampled too), on its plane.
            var points = new Vector4[n * Footprint * 2];
            Parallel.For(0, n, p =>
            {
                var id = (int)live[first + p];
                var c = new Vector3(texels[2 * id].X, texels[2 * id].Y, texels[2 * id].Z);
                var nrm = new Vector3(texels[2 * id + 1].X, texels[2 * id + 1].Y, texels[2 * id + 1].Z);
                var nn = nrm.LengthSquared() > 1e-12f ? Vector3.Normalize(nrm) : Vector3.UnitY;
                var t = Vector3.Normalize(MathF.Abs(nn.Y) < 0.999f ? Vector3.Cross(nn, Vector3.UnitY) : Vector3.Cross(nn, Vector3.UnitX));
                var bt = Vector3.Cross(nn, t);
                for (var f = 0; f < Footprint; f++)
                {
                    var (ox, oy) = FootprintOffsets[f];
                    points[2 * (p * Footprint + f)] = new Vector4(c + (t * ox + bt * oy) * LightletCell, 0f);
                    points[2 * (p * Footprint + f) + 1] = new Vector4(nrm, 0f);
                }
            });
            var bits = tracer.LightletVisibility(points, dense, words, LightletSeed, LightletLift, reach, out var ms);
            gpuMs += ms; denseRays += (long)n * Footprint * dense.Length;

            // Per texel and node: the points' majority sign; MIXED where they disagree (the footprint spans the
            // boundary: distance 0, never certain); near where mixed, a ring disagreed, or a neighbouring node differs.
            var sign = new bool[n * nodes];
            var mixed = new bool[n * nodes];
            var near = new bool[n * nodes];
            var ringDisagrees = new bool[n * nodes];
            Parallel.For(0, n, p =>
            {
                for (var k = 0; k < nodes; k++)
                {
                    var visible = 0;
                    var rd = false;
                    for (var f = 0; f < Footprint; f++)
                    {
                        long w0 = (long)(p * Footprint + f) * words;
                        var sv = Bit(bits, w0, k * perNode);
                        if (sv) visible++;
                        for (var a = 1; a < perNode && !rd; a++) if (Bit(bits, w0, k * perNode + a) != sv) rd = true;
                    }
                    sign[p * nodes + k] = visible * 2 >= Footprint;
                    mixed[p * nodes + k] = visible != 0 && visible != Footprint;
                    ringDisagrees[p * nodes + k] = rd;
                }
                for (var j = 0; j < RecordNodesDec; j++)
                for (var i = 0; i < RecordNodesHour; i++)
                {
                    var k = j * RecordNodesHour + i;
                    var isNear = ringDisagrees[p * nodes + k] || mixed[p * nodes + k];
                    for (var dj = -1; dj <= 1 && !isNear; dj++)
                    for (var di = -1; di <= 1 && !isNear; di++)
                    {
                        var (ii, jj) = (i + di, j + dj);
                        if (ii < 0 || jj < 0 || ii >= RecordNodesHour || jj >= RecordNodesDec) continue;
                        var kk = jj * RecordNodesHour + ii;
                        if (sign[p * nodes + kk] != sign[p * nodes + k] || mixed[p * nodes + kk]) isNear = true;
                    }
                    near[p * nodes + k] = isNear;
                }
            });
            // Rings from every footprint point around the near, unmixed nodes; the node's distance is the least.
            var pairs = new List<uint>();
            for (var p = 0; p < n; p++)
                for (var k = 0; k < nodes; k++)
                    if (near[p * nodes + k] && !mixed[p * nodes + k])
                        for (var f = 0; f < Footprint; f++) { pairs.Add((uint)(p * Footprint + f)); pairs.Add((uint)k); }
            var ringBits = pairs.Count == 0 ? Array.Empty<uint>()
                : tracer.LightletRings(points, CollectionsMarshal.AsSpan(pairs), nodeDirs, tanRadii, azimuths, LightletSeed, LightletLift, reach, out ms);
            if (pairs.Count > 0) { gpuMs += ms; ringRays += (long)pairs.Count / 2 * LightletRingRadii.Length * azimuths; }

            var dist = new float[n * nodes];
            for (var x = 0; x < n * nodes; x++) dist[x] = mixed[x] ? 0f : sign[x] ? LightletFar : -LightletFar;
            Parallel.For(0, pairs.Count / 2 / Footprint, g =>
            {
                var p = (int)pairs[2 * g * Footprint] / Footprint;
                var k = (int)pairs[2 * g * Footprint + 1];
                var s = sign[p * nodes + k];
                var least = ringDisagrees[p * nodes + k] ? 0.5f * (LightletRingRadii[^2] + LightletFar) : LightletFar;
                for (var f = 0; f < Footprint; f++)
                {
                    var q = g * Footprint + f;
                    var prev = 0f;
                    for (var r = 0; r < LightletRingRadii.Length; r++)
                    {
                        var any = false;
                        for (var a = 0; a < azimuths && !any; a++) any = Bit(ringBits, 2L * q, r * azimuths + a) != s;
                        if (any) { least = MathF.Min(least, 0.5f * (prev + LightletRingRadii[r])); break; }
                        prev = LightletRingRadii[r];
                    }
                }
                dist[p * nodes + k] = s ? least : -least;
            });

            // The records, built in parallel, then laid down in order.
            var built = new uint[n][];
            var uniformAs = new int[n];   // -1 a record; 0 never lit; 1 always lit
            long sliceRefined = 0;
            Parallel.For(0, n, p =>
            {
                var s0 = sign[p * nodes];
                var uniform = true;
                for (var k = 0; k < nodes && uniform; k++) uniform = !near[p * nodes + k] && !mixed[p * nodes + k] && sign[p * nodes + k] == s0;
                if (uniform) { uniformAs[p] = s0 ? 1 : 0; return; }
                uniformAs[p] = -1;
                float D(int i, int j) => dist[p * nodes + j * RecordNodesHour + i];
                var rec = new List<uint>(40);
                for (var w = 0; w < 16; w++)
                {
                    uint word = 0;
                    for (var b2 = 0; b2 < 8; b2++)
                    {
                        var c = w * 8 + b2;
                        word |= LightletCode(D(2 * (c % CoarseHour), 2 * (c / CoarseHour))) << (4 * b2);
                    }
                    rec.Add(word);
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
                rec.AddRange(mask);
                rec.AddRange(extra);
                Interlocked.Add(ref sliceRefined, extra.Count);
                built[p] = rec.ToArray();
            });
            for (var p = 0; p < n; p++)
            {
                var id = live[first + p];
                if (uniformAs[p] >= 0) { index[id] = (uint)uniformAs[p]; uniformCount++; continue; }
                index[id] = 2u + (uint)pool.Count;
                pool.AddRange(built[p]);
            }
            refinedCells += sliceRefined;
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
        // Up on the GPU; the CPU's copy (~930 MB on Sponza) is let go -- nothing reads it after the upload. The field
        // stays as the "records are up" mark.
        lightlets = new LightletRecords(Array.Empty<uint>(), Array.Empty<uint>());
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
