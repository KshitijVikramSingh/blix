using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --rtgi-near-cook (on with --rtgi-volume; --rtgi-no-near-cook off): the near field cooked from the geometry
// (rtgi_nearcook.glsl). Where the volume found the geometry tight (its refined 1 m and 0.5 m probes: creases, gaps,
// contacts -- elsewhere the near field is the far light), the triangles are sampled into --rtgi-near-cell (5 cm) surface
// cells by side; each traces 16 fixed cosine rays to the contact radius once, in chunks through the RTGI Metal batch, and
// keeps its escape share and what each ray met. Each frame (a share of the cells) its near light is those surfaces' cache
// light (rtgi_nearcook_update.comp); the readout puts it over the cache's far light. The near rays per pixel
// (--rtgi-hybrid) kept a history on the screen, cleared as the camera moved, a crease seconds coming back; the user asked
// for it worked ahead, with the chunks, as the volume is (2026-10-29). Streamed worlds: a chunk's cells and their links
// would come and go with it -- one load here, every cell resident.
internal sealed partial class SponzaLoop
{
    private bool rtgiNearCook = true, rtgiNearCookBuilt, rtgiNearCookReady;
    private float rtgiNearCell = 0.05f;
    private int rtgiNearCookMost = 4_000_000;
    // Cells relit a frame at most, and the blend's weight of a frame.
    private int rtgiNearUpdateMost = 262_144;
    private float rtgiNearBlend = 0.25f;
    private RayQueryScene.Instance[] rtgiVolInstances = Array.Empty<RayQueryScene.Instance>();
    private Task<NearCookLayout>? rtgiNearBuild;
    private int rtgiNearCells, rtgiNearTableSlots, rtgiNearCookCursor, rtgiNearUpdateCursor;
    private int rtgiNearCookFirstNow, rtgiNearCookCountNow;
    private uint rtgiNearCookFrom;
    private readonly System.Diagnostics.Stopwatch rtgiNearCookClock = new();
    private GpuBufferHandle rtgiNearTable, rtgiNearPoints, rtgiNearLinks, rtgiNearLightBuf;
    private GpuBufferHandle[] rtgiNearCookRayBufs = Array.Empty<GpuBufferHandle>(), rtgiNearCookHitBufs = Array.Empty<GpuBufferHandle>();
    private ShaderInterface rtgiNearCookInterface = null!, rtgiNearUpdateInterface = null!;
    private PassHandle rtgiNearCookRaysPassHandle, rtgiNearCookPassHandle, rtgiNearUpdatePassHandle;
    private PipelineHandle rtgiNearCookPipeline, rtgiNearUpdatePipeline;
    private const int RtgiNearCookChunk = 32768;
    private const int NearRays = 16;

    private sealed record NearCookLayout(Vector4[] Points, uint[] Table, int Slots, double Ms);

    private bool RtgiNearCookOn => rtgiNearCook && rtgiNearCookReady;
    // rtgi_nearcook.glsl's NEAR_PARAMS.
    private Vector4 RtgiNearParams => new(rtgiNearCell, Math.Max(rtgiNearTableSlots, 1) - 1, rtgiNearCells, RtgiNearCookOn ? 1f : 0f);
    private GpuBufferHandle RtgiNearTableOrDummy => rtgiNearCookBuilt ? rtgiNearTable : rtgiVolDummy;
    private GpuBufferHandle RtgiNearLightOrDummy => rtgiNearCookBuilt ? rtgiNearLightBuf : rtgiVolDummy;

    private void ParseRtgiNearCook(Blix.Core.AppArgs args)
    {
        if (args.Flag("rtgi-no-near-cook")) rtgiNearCook = false;
        if (args.Float("rtgi-near-cell") is { } nc) rtgiNearCell = MathF.Max(0.01f, nc);
        if (args.Int("rtgi-near-cook-most") is { } most) rtgiNearCookMost = Math.Max(1024, most);
        if (args.Float("rtgi-near-blend") is { } nb) rtgiNearBlend = Math.Clamp(nb, 0.01f, 1f);
    }

    private void DeclareRtgiNearCookPasses(Func<string, ShaderInterface> reflect)
    {
        rtgiNearCookInterface = reflect("rtgi_nearcook_cook.comp");
        rtgiNearCookRaysPassHandle = graph.ComputePass("rtgi-near-cook-rays").AfterAsync().Shader(rtgiNearCookInterface).Handle;
    }

    private void DeclareRtgiNearCookAsyncPasses(Func<string, ShaderInterface> reflect)
    {
        rtgiNearCookPassHandle = graph.ComputePass("rtgi-near-cook").Async(rtgiAsync).Shader(rtgiNearCookInterface).Handle;
        rtgiNearUpdateInterface = reflect("rtgi_nearcook_update.comp");
        rtgiNearUpdatePassHandle = graph.ComputePass("rtgi-near-update").Async(rtgiAsync).Shader(rtgiNearUpdateInterface).Handle;
    }

    private void CreateRtgiNearCookPipelines(Func<string, ShaderInterface, ComputePipelineOptions, PipelineHandle> compute)
    {
        rtgiNearCookPipeline = compute("rtgi_nearcook_cook", rtgiNearCookInterface, ComputePipelineOptions.None);
        rtgiNearUpdatePipeline = compute("rtgi_nearcook_update", rtgiNearUpdateInterface, ComputePipelineOptions.None);
    }

    private static uint NearHash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352du;
        x ^= x >> 15; x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    // rtgi_nearcook.glsl's nearCellHash.
    private static (uint Start, uint Key) NearCellHash(int x, int y, int z, uint side, uint mask)
    {
        unchecked
        {
            var a = NearHash((uint)x * 0x9E3779B1u ^ NearHash((uint)y * 0x85EBCA77u ^ NearHash((uint)z * 0xC2B2AE3Du ^ (side + 0x3A1u))));
            var k = NearHash(a ^ 0x2545F491u ^ (uint)x * 0x1B873593u ^ (uint)y * 0xCC9E2D51u ^ (uint)z * 0x27D4EB2Fu) | 1u;
            return (a & mask, k);
        }
    }

    private static uint NearSide(Vector3 n)
    {
        var a = Vector3.Abs(n);
        if (a.X >= a.Y && a.X >= a.Z) return n.X >= 0f ? 0u : 1u;
        if (a.Y >= a.Z) return n.Y >= 0f ? 2u : 3u;
        return n.Z >= 0f ? 4u : 5u;
    }

    // Once the volume is ready: the cells placed from the triangles inside its refined regions, on a worker.
    private void StartRtgiNearCookBuild()
    {
        if (!rtgiNearCook || rtgiNearBuild is not null || !rtgiVolReady || rtgiVolInstances.Length == 0) return;
        var instances = rtgiVolInstances;
        rtgiVolInstances = Array.Empty<RayQueryScene.Instance>();
        var fineSets = new[] { (Set: new HashSet<long>(rtgiVolSets[0]), S: VolSpacingOf(0)), (Set: new HashSet<long>(rtgiVolSets[1]), S: VolSpacingOf(1)) };
        var cell = rtgiNearCell;
        var most = rtgiNearCookMost;
        rtgiNearBuild = Task.Run(() => BuildNearCookLayout(instances, fineSets, cell, most));
    }

    private static NearCookLayout BuildNearCookLayout(RayQueryScene.Instance[] instances, (HashSet<long> Set, float S)[] fine, float cell, int most)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // Inside the refined regions: a point whose probe cell, at either fine level, has one of its 8 probes placed.
        bool Inside(Vector3 p)
        {
            foreach (var (set, s) in fine)
            {
                if (set.Count == 0) continue;
                var g = p / s - new Vector3(0.5f) - VolJitter;
                int x = (int)MathF.Floor(g.X), y = (int)MathF.Floor(g.Y), z = (int)MathF.Floor(g.Z);
                for (var k = 0; k < 8; k++)
                    if (set.Contains(VolKey(x + (k & 1), y + ((k >> 1) & 1), z + (k >> 2)))) return true;
            }
            return false;
        }
        var cells = new ConcurrentDictionary<(int, int, int, uint), (Vector3 P, Vector3 N, int Count)>();
        var h = cell * 0.5f;
        Parallel.ForEach(instances, inst =>
        {
            var local = new Dictionary<(int, int, int, uint), (Vector3 P, Vector3 N, int Count)>();
            var pos = inst.Mesh.Positions;
            var idx = inst.Mesh.Indices;
            for (var t = 0; t + 2 < idx.Length; t += 3)
            {
                var a = Vector3.Transform(pos[idx[t]], inst.World);
                var b = Vector3.Transform(pos[idx[t + 1]], inst.World);
                var c = Vector3.Transform(pos[idx[t + 2]], inst.World);
                var nrm = Vector3.Cross(b - a, c - a);
                var len = nrm.Length();
                if (!(len > 1e-12f)) continue;
                nrm /= len;
                // Quick reject: none of its corners nor its centre inside, and small enough that none of it is.
                var big = MathF.Max((b - a).Length(), MathF.Max((c - a).Length(), (c - b).Length()));
                if (big < 0.5f && !Inside(a) && !Inside(b) && !Inside(c) && !Inside((a + b + c) / 3f)) continue;
                var side = NearSide(nrm);
                var nu = Math.Max(1, (int)MathF.Ceiling((b - a).Length() / h));
                var nv = Math.Max(1, (int)MathF.Ceiling((c - a).Length() / h));
                for (var i = 0; i <= nu; i++)
                for (var j = 0; j <= nv; j++)
                {
                    float u = i / (float)nu, v = j / (float)nv;
                    if (u + v > 1f) continue;
                    var p = a + (b - a) * u + (c - a) * v;
                    if (!Inside(p)) continue;
                    var key = ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell), side);
                    local.TryGetValue(key, out var e);
                    local[key] = (e.P + p, e.N + nrm, e.Count + 1);
                }
            }
            foreach (var (k, v) in local)
                cells.AddOrUpdate(k, v, (_, e) => (e.P + v.P, e.N + v.N, e.Count + v.Count));
        });
        var list = cells.Take(most).ToArray();
        var points = new Vector4[list.Length * 2];
        for (var i = 0; i < list.Length; i++)
        {
            var ((_, _, _, side), (p, n, count)) = list[i];
            points[2 * i] = new Vector4(p / count, BitConverter.UInt32BitsToSingle(side));
            var nn = n.Length() > 1e-6f ? Vector3.Normalize(n) : Vector3.UnitY;
            points[2 * i + 1] = new Vector4(nn, 0f);
        }
        var slots = 1024;
        while (slots < list.Length * 2) slots <<= 1;
        uint[] table;
        while (true)
        {
            table = new uint[slots * 2];
            var fits = true;
            for (var i = 0; i < list.Length && fits; i++)
            {
                var ((x, y, z, side), _) = list[i];
                var (start, key) = NearCellHash(x, y, z, side, (uint)(slots - 1));
                var placed = false;
                for (uint k = 0; k < 8 && !placed; k++)
                {
                    var at = (int)((start + k) & (uint)(slots - 1));
                    if (table[2 * at] != 0) continue;
                    table[2 * at] = key; table[2 * at + 1] = (uint)i; placed = true;
                }
                fits = placed;
            }
            if (fits) break;
            slots <<= 1;
        }
        return new NearCookLayout(points, table, slots, clock.Elapsed.TotalMilliseconds);
    }

    private void TakeRtgiNearCookLayout()
    {
        if (rtgiNearCookBuilt || rtgiNearBuild is not { IsCompleted: true } task) return;
        var layout = task.Result;
        rtgiNearBuild = Task.FromResult(layout with { Points = Array.Empty<Vector4>(), Table = Array.Empty<uint>() });
        rtgiNearCells = layout.Points.Length / 2;
        rtgiNearTableSlots = layout.Slots;
        var cells = Math.Max(rtgiNearCells, 1);
        rtgiNearTable = Own(device.CreateGpuBuffer(layout.Table.Length * 4, MemoryMarshal.AsBytes(layout.Table.AsSpan()).ToArray(), "sponza.rtgi.near-table"));
        rtgiNearPoints = Own(device.CreateGpuBuffer(cells * 32, rtgiNearCells > 0 ? MemoryMarshal.AsBytes(layout.Points.AsSpan()).ToArray() : new byte[32], "sponza.rtgi.near-points"));
        rtgiNearLinks = Own(device.CreateGpuBuffer(cells * NearRays * 8, new byte[cells * NearRays * 8], "sponza.rtgi.near-links"));
        rtgiNearLightBuf = Own(device.CreateGpuBuffer(cells * 16, new byte[cells * 16], "sponza.rtgi.near-light"));
        rtgiNearCookRayBufs = Enumerable.Range(0, 2).Select(k => Own(device.CreateGpuBuffer(RtgiNearCookChunk * NearRays * 32, name: $"sponza.rtgi.near-cook-rays{k}"))).ToArray();
        rtgiNearCookHitBufs = Enumerable.Range(0, 2).Select(k => Own(device.CreateGpuBuffer(RtgiNearCookChunk * NearRays * 48, name: $"sponza.rtgi.near-cook-hits{k}"))).ToArray();
        rtgiNearCookBuilt = true;
        rtgiNearCookFrom = rtgiFrameCount;
        rtgiNearCookClock.Restart();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] rtgi near cook: {rtgiNearCells:N0} cells of {rtgiNearCell * 100:0.#} cm in the volume's refined regions ({(cells * (32L + NearRays * 8 + 16) + layout.Table.Length * 4L) / 1048576.0:0} MB), table {layout.Slots:N0} slots; placed from the triangles in {layout.Ms:0} ms."));
        if (rtgiNearCells == 0) rtgiNearCookReady = true;
    }

    private DispatchCommand NearCookDispatch(int cur, int kind, int threads) => new(rtgiNearCookPipeline, (threads + 63) / 64, 1, 1,
        new ShaderUniform[]
        {
            new("uNearCook", new Vector4Uniform(RtgiNearParams)),
            new("uCook", new Vector4Uniform(new Vector4(rtgiNearCookFirstNow, rtgiNearCookCountNow, rtgiContactRadius, kind))),
        },
        Array.Empty<ShaderTextureBinding>(),
        Buffers: new[]
        {
            new ShaderBufferBinding("RtgiNearPoints", rtgiNearPoints),
            new ShaderBufferBinding("RtgiNearCookRays", rtgiNearCookRayBufs[cur]),
            new ShaderBufferBinding("RtgiNearCookHits", rtgiNearCookHitBufs[cur]),
            new ShaderBufferBinding("RtgiNearLinks", rtgiNearLinks),
            new ShaderBufferBinding("RtgiNearLight", rtgiNearLightBuf),
        });

    // Placement time: the next chunk's rays (traced in the Metal batch: RtgiNearCookRaysNow); with the training their
    // links. Then, cooked, each frame's relight.
    private void RecordRtgiNearCook(int cur, Vector3 towardSun)
    {
        rtgiNearCookCountNow = 0;
        if (!rtgiNearCook) return;
        StartRtgiNearCookBuild();
        TakeRtgiNearCookLayout();
        if (!rtgiNearCookBuilt || rtgiNearCells == 0) return;
        if (!rtgiNearCookReady)
        {
            rtgiNearCookFirstNow = rtgiNearCookCursor;
            rtgiNearCookCountNow = Math.Min(RtgiNearCookChunk, rtgiNearCells - rtgiNearCookCursor);
            graph.Dispatch(rtgiNearCookRaysPassHandle, NearCookDispatch(cur, 0, rtgiNearCookCountNow * NearRays));
            graph.Dispatch(rtgiNearCookPassHandle, NearCookDispatch(cur, 1, rtgiNearCookCountNow));
            rtgiNearCookCursor += rtgiNearCookCountNow;
            if (rtgiNearCookCursor >= rtgiNearCells)
            {
                rtgiNearCookReady = true;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] rtgi near cook: {rtgiNearCells:N0} cells traced ({rtgiNearCells * (long)NearRays / 1e6:0.0} M rays) in {rtgiFrameCount - rtgiNearCookFrom + 1} frames, {rtgiNearCookClock.Elapsed.TotalSeconds:0.0} s."));
            }
            return;
        }
        var count = Math.Min(rtgiNearCells, rtgiNearUpdateMost);
        var first = rtgiNearUpdateCursor;
        rtgiNearUpdateCursor = (rtgiNearUpdateCursor + count) % rtgiNearCells;
        graph.Dispatch(rtgiNearUpdatePassHandle, new DispatchCommand(rtgiNearUpdatePipeline, (count + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uParams", new Vector4Uniform(RtgiParams)),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                new("uSun", new Vector4Uniform(new Vector4(towardSun, 0f))),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
                new("uNearCook", new Vector4Uniform(RtgiNearParams)),
                new("uUpdate", new Vector4Uniform(new Vector4(first, count, rtgiNearBlend, rtgiContactRadius))),
                new("uVolume", new Vector4Uniform(RtgiVolumeParams)),
                new("uVolumeGeom", new Vector4Uniform(RtgiVolumeGeom)),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiNearPoints", rtgiNearPoints),
                new ShaderBufferBinding("RtgiNearLinks", rtgiNearLinks),
                new ShaderBufferBinding("RtgiNearLight", rtgiNearLightBuf),
                new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                new ShaderBufferBinding("RtgiRecord", rtgiRecord),
                new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                new ShaderBufferBinding("RtgiVolumeTable", RtgiVolTableOrDummy),
                new ShaderBufferBinding("RtgiVolumeProbes", RtgiVolProbesOrDummy),
            }));
    }
}
