using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The cook's walk over every surface on the GPU (rtgi_cookwalk.comp): the burial check's walk (BuildCookVerifyPass) and
// the cells' (BuildCookCells: the size table, the cells, their surfels), which took Crytek Sponza's cook to three
// minutes on the CPU -- the GPU traced its 757 M rays in 5 s, the walks' 60 M surface points a point at a time took the
// rest (2026-10-31). The same rules, a frame a step behind the cook screen:
//   the burial check   the size field's passes uploaded, the walk dropping-only, then the dropped fine cubes near kept
//                      surface read back: the last size pass, traced as before;
//   the cells          the size field smoothed, the walk into the size table (written as rtgi_cook.glsl reads it), the
//                      cells' keys, numbered; read back, the cells' sums; read back, the surfels' first entries set, the
//                      winning points and the surfels' sums; read back into CookCells for the stages after it.
// Room runs out (a list, the lattice, a map): the step again with twice the room. --rtgi-cook-cpu: the CPU walk.
internal sealed partial class SponzaLoop
{
    private bool rtgiCookGpuWalk = true;
    private ShaderInterface walkInterface = null!;
    private PassHandle walkPassHandle;
    private PipelineHandle walkPipeline;

    // rtgi_cookwalk.comp's kinds, modes, counters, maps and their values' strides.
    private const int WkClear = 0, WkSeed = 1, WkArgs = 2, WkRound = 3, WkPoints = 4, WkMark = 5, WkSmooth = 6, WkBend = 7,
        WkCap = 8, WkFine = 9, WkTDrop = 10, WkVCheck = 11, WkDense = 12, WkRank = 13, WkSurfPrep = 14, WkJoins = 15, WkJoinFill = 16;
    private const int WmVerify = 0, WmTable = 1, WmKeys = 2, WmAcc = 3, WmFinal = 4, WmVerifyPick = 5;
    private const int WcList = 0, WcFinal = 2, WcLattice = 3, WcPoints = 4, WcBuried = 5, WcFilled = 6, WcNone = 7, WcOver = 8,
        WcMapFail = 9, WcVerify = 10, WcCells = 11, WcScaleFail = 12, WcFineN = 13, WcCoarseN = 14, WcBent = 15, WcArgs = 16, WcTDropN = 20,
        WcEdges = 21;
    private const int WalkCounters = 32;
    private const int WmapFine = 0, WmapCoarse = 1, WmapTDrop = 2, WmapCells = 3, WmapKept = 4, WmapVDrop = 5, WalkMaps = 6;
    // Values a slot, and where (0 the float values, 1 the uint).
    private static readonly (int Stride, int Kind)[] WalkMapValues = { (2, 0), (40, 0), (0, 0), (1, 1), (2, 1), (8, 1) };
    private const int WalkCellF = 56, WalkCellU = 17;
    // The clear kinds' buffers.
    private const int WbKeys = 0, WbMapF = 1, WbMapU = 2, WbCellF = 3, WbCellU = 4, WbSurfels = 5, WbCount = 6, WbScale = 7, WbCellM = 8;
    private const int WalkSmoothPasses = CookSmoothPasses;
    // Frames a step's dispatches are given before they are read back: the frame that recorded them is submitted before the
    // next one's cook step runs, and the read waits for the device.
    private const int WalkDrainFrames = 1;

    private sealed record WalkField(Vector4[] Samples, uint[] Passes, WalkSlot[] Grid, uint[] Order, int LastPass, int Count);
    [StructLayout(LayoutKind.Sequential)]
    private struct WalkSlot
    {
        public uint X, Y, Z, W;
        public WalkSlot(uint x, uint y, uint z, uint w) { X = x; Y = y; Z = z; W = w; }
    }

    private sealed class CookWalkGpu
    {
        public int Step, Drain, Rounds = 48;
        public Task<WalkField>? FieldBuild;
        public WalkField? Field;
        public Vector4[]? Triangles;
        public GpuBufferHandle Tris, Pass, Grid, Order, Samples, Count, Pieces, Final, Lattice, Keys, MapF, MapU,
            CellKeys, CellF, CellU, CellM, Surfels, Scale, Verify, Rank, SurfOwner, Edges;
        public int EdgeRoom, EdgeCount;
        // The pieces' stage (SponzaLoop.RtgiCookPiecesGpu.cs).
        public GpuBufferHandle Label, PieceIds, PieceRoot, PieceF, PieceU, CubeKeys, CubeVals, Cubes, PieceList, PieceOut,
            PieceOfCur, PieceOfUp, PieceCount;
        public int PStep, PLevel, PieceRoom, CubeSlots, Pieces_, Cubes_;
        public (WalkSlot[] Cubes, WalkSlot[] Out)[] LevelPieces = Array.Empty<(WalkSlot[], WalkSlot[])>();
        public readonly Stopwatch PClock = new();
        public void PieceBuffersFree()
        {
            Label = PieceIds = PieceRoot = PieceF = PieceU = CubeKeys = CubeVals = Cubes = PieceList = PieceOut = default;
            PieceOfCur = PieceOfUp = PieceCount = default;
        }
        public int ListRoom = 1 << 20, FinalRoom = 1 << 20, LatticeRoom = 1 << 23, VerifyRoom;
        public int[] MapLog2 = { 22, 19, 4, 23, 20, 22 };
        public int ScaleSlots = 1 << 24;
        public int Cells, Surfels_;
        public (uint[] Keys, float[] F, uint[] U, uint[] Meta, uint[] Scale, uint[] Counters)? CellsRead;
        public long KeySlots, MapFWords, MapUWords;
        public long[] MapValueBase = new long[WalkMaps];
        public uint[] Counters = Array.Empty<uint>();
        public readonly Stopwatch Clock = Stopwatch.StartNew(), StepClock = new();
        public double TableMs, CellsMs, VerifyMs;
        public CensusPass[] Passes = Array.Empty<CensusPass>();
        public readonly List<GpuBufferHandle> Owned = new();
    }
    private CookWalkGpu? cookWalk;

    private void DeclareRtgiCookWalkPass(Func<string, ShaderInterface> reflect)
    {
        walkInterface = reflect("rtgi_cookwalk.comp");
        walkPassHandle = graph.ComputePass("rtgi-cook-walk").Shader(walkInterface).Handle;
    }

    private void CreateRtgiCookWalkPipeline(Func<string, ShaderInterface, ComputePipelineOptions, PipelineHandle> compute) =>
        walkPipeline = compute("rtgi_cookwalk", walkInterface, ComputePipelineOptions.None);

    // Whether the walks run on the GPU: asked for, a device with float atomics, not the census's sampled share.
    private bool CookWalkOnGpu => rtgiCookGpuWalk && !rtgiCensus && (device.Features & GraphicsFeatures.BufferFloatAtomicAdd) != 0;

    // ---- Keys, as rtgi_cookwalk.comp packs them ----

    private static (uint K0, uint K1) PackWalkKey(int x, int y, int z, uint side, uint level)
    {
        uint bx = (uint)(x + (1 << 17)) & 0x3FFFFu, by = (uint)(y + (1 << 17)) & 0x3FFFFu, bz = (uint)(z + (1 << 17)) & 0x3FFFFu;
        return (0x80000000u | bx | ((by & 0x1FFFu) << 18), 0x80000000u | (by >> 13) | (bz << 5) | (side << 23) | (level << 26));
    }

    private static (int X, int Y, int Z, uint Side, uint Level) UnpackWalkKey(uint k0, uint k1)
    {
        uint bx = k0 & 0x3FFFFu, by = ((k0 >> 18) & 0x1FFFu) | ((k1 & 0x1Fu) << 13), bz = (k1 >> 5) & 0x3FFFFu;
        return ((int)bx - (1 << 17), (int)by - (1 << 17), (int)bz - (1 << 17), (k1 >> 23) & 7u, (k1 >> 26) & 15u);
    }

    private static uint WalkKeyHash(uint k0, uint k1) => NearHash(k0 * 0x9E3779B1u ^ NearHash(k1 + 0x7F4A7C15u));

    private static float Bits(uint v) => BitConverter.UInt32BitsToSingle(v);
    private static float Bits(int v) => BitConverter.UInt32BitsToSingle((uint)v);

    // ---- The field and the triangles, built on a worker ----

    // Every pass's samples as one list (rtgi_cookwalk.comp's WalkSamples), the passes' grids as one hash table, their
    // sample order.
    private static WalkField BuildWalkField(CensusPass[] passes)
    {
        var offsets = new int[passes.Length + 1];
        for (var p = 0; p < passes.Length; p++) offsets[p + 1] = offsets[p] + passes[p].Pos.Length;
        var n = offsets[^1];
        var samples = new Vector4[3 * Math.Max(n, 1)];
        var order = new uint[Math.Max(n, 1)];
        var desc = new uint[4 * (16 + WalkMaps)];
        var grids = new List<WalkSlot>();
        for (var p = 0; p < passes.Length; p++)
        {
            var pass = passes[p];
            var off = offsets[p];
            for (var i = 0; i < pass.Pos.Length; i++)
            {
                var g = off + i;
                var bits = pass.Side[i] | (pass.Dead[i] ? 8u : 0u) | ((uint)p << 4);
                var size = pass.Size[i];
                var nearest = float.IsFinite(pass.Nearest[i]) ? pass.Nearest[i] : float.MaxValue;
                var ls = MathF.Log2(size);
                samples[3 * g] = new Vector4(pass.Pos[i], Bits(bits));
                samples[3 * g + 1] = new Vector4(pass.Normal[i], size);
                samples[3 * g + 2] = new Vector4(nearest, ls, ls, 0f);
            }
            var grid = pass.Grid!;
            var po = grid.Order;
            for (var k = 0; k < po.Length; k++) order[off + k] = (uint)(off + po[k]);
            var runs = grid.Runs().ToArray();
            var slots = 64;
            while (slots < 2 * runs.Length) slots <<= 1;
            var table = new WalkSlot[slots];
            foreach (var (x, y, z, start, count) in runs)
            {
                var (k0, k1) = PackWalkKey(x, y, z, 0u, (uint)p);
                var h = WalkKeyHash(k0, k1);
                for (var i = 0u; ; i++)
                {
                    var s = (int)((h + i) & (uint)(slots - 1));
                    if (table[s].X != 0u) continue;
                    table[s] = new WalkSlot(k0, k1, (uint)(off + start), (uint)count);
                    break;
                }
            }
            desc[8 * p] = BitConverter.SingleToUInt32Bits(grid.Size);
            desc[8 * p + 1] = BitConverter.SingleToUInt32Bits(pass.Effective);
            desc[8 * p + 2] = (uint)(slots - 1);
            desc[8 * p + 3] = (uint)grids.Count;
            desc[8 * p + 4] = (uint)off;
            desc[8 * p + 5] = (uint)pass.Pos.Length;
            grids.AddRange(table);
        }
        return new WalkField(samples, desc, grids.ToArray(), order, passes.Length - 1, n);
    }

    private static Vector4[] BuildWalkTriangles(CensusGeometry geom)
    {
        var instances = geom.Instances;
        var firsts = new long[instances.Length + 1];
        for (var i = 0; i < instances.Length; i++) firsts[i + 1] = firsts[i] + instances[i].Mesh.TriangleCount;
        var tris = new Vector4[3 * Math.Max(firsts[^1], 1)];
        Parallel.For(0, instances.Length, i =>
        {
            for (var t = 0; t < instances[i].Mesh.TriangleCount; t++)
            {
                var (a, b, c) = CensusTriangle(instances[i], t);
                var at = 3 * (firsts[i] + t);
                tris[at] = new Vector4(a, 0f); tris[at + 1] = new Vector4(b, 0f); tris[at + 2] = new Vector4(c, 0f);
            }
        });
        return tris;
    }

    // ---- Buffers ----

    private GpuBufferHandle WalkBuffer(CookWalkGpu w, long bytes, string name, ReadOnlySpan<byte> data = default)
    {
        if (bytes > int.MaxValue) throw new InvalidOperationException($"rtgi cook walk: {name} needs {bytes / 1048576} MB, past a buffer's 2 GB");
        var h = Own(device.CreateGpuBuffer((int)Math.Max(bytes, 16), data, name));
        w.Owned.Add(h);
        return h;
    }

    private void WalkFree(CookWalkGpu w, ref GpuBufferHandle h)
    {
        if (h.Equals(default(GpuBufferHandle))) return;
        w.Owned.Remove(h);
        Disown(h);
        h = default;
    }

    private void WalkFreeAll(CookWalkGpu w)
    {
        device.WaitIdle();
        foreach (var h in w.Owned) Disown(h);
        w.Owned.Clear();
        w.Tris = w.Pass = w.Grid = w.Order = w.Samples = w.Count = w.Pieces = w.Final = w.Lattice = w.Keys = w.MapF = w.MapU = default;
        w.CellKeys = w.CellF = w.CellU = w.CellM = w.Surfels = w.Scale = w.Verify = w.Rank = w.SurfOwner = w.Edges = default;
        w.PieceBuffersFree();
    }

    // The field's buffers (the passes' samples, grids, order, descriptors), the triangles', the pieces', the maps'.
    private void WalkUpload(CookWalkGpu w, bool verify)
    {
        var f = w.Field!;
        WalkFree(w, ref w.Pass); WalkFree(w, ref w.Grid); WalkFree(w, ref w.Order); WalkFree(w, ref w.Samples);
        WalkFree(w, ref w.Pieces); WalkFree(w, ref w.Final); WalkFree(w, ref w.Lattice); WalkFree(w, ref w.Keys);
        WalkFree(w, ref w.MapF); WalkFree(w, ref w.MapU); WalkFree(w, ref w.Scale); WalkFree(w, ref w.Verify); WalkFree(w, ref w.CellKeys);
        if (w.Tris.Equals(default(GpuBufferHandle)))
            w.Tris = WalkBuffer(w, w.Triangles!.Length * 16L, "sponza.rtgi.walk-tris", MemoryMarshal.AsBytes(w.Triangles.AsSpan()));
        if (w.Count.Equals(default(GpuBufferHandle))) w.Count = WalkBuffer(w, WalkCounters * 4, "sponza.rtgi.walk-count");
        // The maps this walk uses: the burial check's two, or the cells' four.
        var used = verify ? new[] { WmapKept, WmapVDrop } : new[] { WmapFine, WmapCoarse, WmapTDrop, WmapCells };
        if (!verify && !CookDroppedTier) w.MapLog2[WmapTDrop] = 4;
        long keySlots = 0, fWords = 0, uWords = 0;
        var desc = (uint[])f.Passes.Clone();
        foreach (var m in Enumerable.Range(0, WalkMaps))
        {
            var slots = used.Contains(m) ? 1L << w.MapLog2[m] : 0L;
            var (stride, kind) = WalkMapValues[m];
            var at = 4 * (16 + m);
            desc[at] = (uint)keySlots;
            desc[at + 1] = (uint)Math.Max(slots - 1, 0);
            desc[at + 2] = (uint)(kind == 0 ? fWords : uWords);
            w.MapValueBase[m] = kind == 0 ? fWords : uWords;
            desc[at + 3] = (uint)stride;
            keySlots += slots;
            if (kind == 0) fWords += slots * stride; else uWords += slots * stride;
        }
        w.Pass = WalkBuffer(w, desc.Length * 4L, "sponza.rtgi.walk-pass", MemoryMarshal.AsBytes(desc.AsSpan()));
        w.Grid = WalkBuffer(w, f.Grid.Length * 16L, "sponza.rtgi.walk-grid", MemoryMarshal.AsBytes(f.Grid.AsSpan()));
        w.Order = WalkBuffer(w, f.Order.Length * 4L, "sponza.rtgi.walk-order", MemoryMarshal.AsBytes(f.Order.AsSpan()));
        w.Samples = WalkBuffer(w, f.Samples.Length * 16L, "sponza.rtgi.walk-samples", MemoryMarshal.AsBytes(f.Samples.AsSpan()));
        w.ListRoom = Math.Max(w.ListRoom, (int)Math.Min(int.MaxValue / 128, w.Triangles!.Length / 3 * 3L));
        w.Pieces = WalkBuffer(w, 2L * w.ListRoom * 64, "sponza.rtgi.walk-pieces");
        w.Final = WalkBuffer(w, (long)w.FinalRoom * 80, "sponza.rtgi.walk-final");
        w.Lattice = WalkBuffer(w, (long)w.LatticeRoom * 8, "sponza.rtgi.walk-lattice");
        w.Keys = WalkBuffer(w, keySlots * 8, "sponza.rtgi.walk-keys");
        w.MapF = WalkBuffer(w, fWords * 4, "sponza.rtgi.walk-mapf");
        w.MapU = WalkBuffer(w, uWords * 4, "sponza.rtgi.walk-mapu");
        w.KeySlots = keySlots; w.MapFWords = fWords; w.MapUWords = uWords;
        if (verify)
        {
            w.VerifyRoom = 1 << w.MapLog2[WmapVDrop];
            w.Verify = WalkBuffer(w, (long)w.VerifyRoom * 32, "sponza.rtgi.walk-verify");
        }
        else
        {
            w.Scale = WalkBuffer(w, (long)w.ScaleSlots * 8, "sponza.rtgi.walk-scale");
            w.CellKeys = WalkBuffer(w, (8L << w.MapLog2[WmapCells]), "sponza.rtgi.walk-cell-keys");
        }
    }

    private GpuBufferHandle WalkOr(GpuBufferHandle h) => h.Equals(default(GpuBufferHandle)) ? cookDummy : h;

    private DispatchCommand WalkDispatch(int kind, long groups, float y = 0f, long items = 0, float w = 0f, long clearAt = 0, bool indirect = false, uint clearY = 0, float clearZ = 0f)
    {
        var g = cookWalk!;
        var top = rtgiCell * MathF.Pow(2f, RtgiTopLevel);
        return new DispatchCommand(walkPipeline, indirect ? 1 : (int)Math.Clamp(groups, 1, int.MaxValue), 1, 1,
            new ShaderUniform[]
            {
                new("uKind", new Vector4Uniform(new Vector4(kind, y, Bits((uint)items), w))),
                new("uField", new Vector4Uniform(new Vector4(rtgiCell, top, g.Field?.LastPass ?? 0, CookDroppedTier ? 1f : 0f))),
                new("uRoom", new Vector4Uniform(new Vector4(Bits(g.ListRoom), Bits(g.FinalRoom), Bits(g.LatticeRoom), Bits(g.VerifyRoom)))),
                new("uMisc", new Vector4Uniform(new Vector4(CookCoarseCube, CookLiveReach, CookBendAngle, Bits(g.ScaleSlots - 1)))),
                new("uClear", new Vector4Uniform(new Vector4(Bits((uint)clearAt), Bits(clearY), clearZ, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("WalkTris", WalkOr(g.Tris)),
                new ShaderBufferBinding("WalkPass", WalkOr(g.Pass)),
                new ShaderBufferBinding("WalkGrid", WalkOr(g.Grid)),
                new ShaderBufferBinding("WalkOrder", WalkOr(g.Order)),
                new ShaderBufferBinding("WalkSamples", WalkOr(g.Samples)),
                new ShaderBufferBinding("WalkCount", WalkOr(g.Count)),
                new ShaderBufferBinding("WalkPieces", WalkOr(g.Pieces)),
                new ShaderBufferBinding("WalkFinal", WalkOr(g.Final)),
                new ShaderBufferBinding("WalkLattice", WalkOr(g.Lattice)),
                new ShaderBufferBinding("WalkKeys", WalkOr(g.Keys)),
                new ShaderBufferBinding("WalkMapF", WalkOr(g.MapF)),
                new ShaderBufferBinding("WalkMapU", WalkOr(g.MapU)),
                new ShaderBufferBinding("WalkCellKeys", WalkOr(g.CellKeys)),
                new ShaderBufferBinding("WalkCellF", WalkOr(g.CellF)),
                new ShaderBufferBinding("WalkCellU", WalkOr(g.CellU)),
                new ShaderBufferBinding("WalkCellM", WalkOr(g.CellM)),
                new ShaderBufferBinding("WalkSurfels", WalkOr(g.Surfels)),
                new ShaderBufferBinding("WalkScale", WalkOr(g.Scale)),
                new ShaderBufferBinding("WalkVerify", WalkOr(g.Verify)),
                new ShaderBufferBinding("WalkRank", WalkOr(g.Rank)),
                new ShaderBufferBinding("WalkSurfOwner", WalkOr(g.SurfOwner)),
                new ShaderBufferBinding("WalkEdges", WalkOr(g.Edges)),
                new ShaderBufferBinding("WalkJoinRays", WalkOr(cookJoins)),
            },
            IndirectArgs: indirect ? g.Count : null,
            IndirectOffset: indirect ? WcArgs * 4 : 0);
    }

    private void WalkRun(int kind, long items, float y = 0f, float w = 0f) =>
        graph.Dispatch(walkPassHandle, WalkDispatch(kind, (items + 63) / 64, y, items, w));

    private void WalkClear(int buffer, long words, uint fill = 0u, long first = 0)
    {
        if (words <= 0) return;
        graph.Dispatch(walkPassHandle, WalkDispatch(WkClear, (words + 63) / 64, buffer, words, Bits(fill), first));
    }

    // The pieces: every triangle's sides seeded, split over the rounds (each round's count read on the GPU), the final
    // pieces' points into the mode's sink.
    private void WalkPieces(CookWalkGpu w, bool dropsOnly)
    {
        var tris = w.Triangles!.Length / 3;
        WalkClear(WbCount, WalkCounters);
        WalkRun(WkSeed, tris);
        for (var r = 0; r < w.Rounds; r++)
        {
            var input = r & 1;
            graph.Dispatch(walkPassHandle, WalkDispatch(WkArgs, 1, input));
            graph.Dispatch(walkPassHandle, WalkDispatch(WkRound, 0, dropsOnly ? 1f : 0f, w: input, indirect: true));
        }
    }

    private void WalkPoints(int mode)
    {
        graph.Dispatch(walkPassHandle, WalkDispatch(WkArgs, 1, 2f));
        graph.Dispatch(walkPassHandle, WalkDispatch(WkPoints, 0, mode, indirect: true));
    }

    private long MapSlots(CookWalkGpu w, int m) => 1L << w.MapLog2[m];

    // The counters read back, and whether any room ran out (grown for the next try).
    private bool WalkOverflowed(CookWalkGpu w, string what)
    {
        w.Counters = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(w.Count, 0, WalkCounters * 4).AsSpan()).ToArray();
        var c = w.Counters;
        var over = c[WcOver];
        var left = c[WcList + (w.Rounds & 1)];
        var grown = new List<string>();
        if ((over & 1u) != 0) { w.ListRoom *= 2; grown.Add($"piece lists to {w.ListRoom:N0}"); }
        if ((over & 2u) != 0) { w.FinalRoom = Math.Max(w.FinalRoom * 2, (int)Math.Min(int.MaxValue / 80, c[WcFinal] * 5L / 4)); grown.Add($"final pieces to {w.FinalRoom:N0}"); }
        if ((over & 4u) != 0) { w.LatticeRoom = Math.Max(w.LatticeRoom * 2, (int)Math.Min(int.MaxValue / 8, c[WcLattice] * 5L / 4)); grown.Add($"lattice to {w.LatticeRoom:N0}"); }
        if ((over & 16u) != 0) { w.MapLog2[WmapVDrop]++; grown.Add("the verify list"); }
        if (c[WcMapFail] > 0)
        {
            for (var m = 0; m < WalkMaps; m++) w.MapLog2[m]++;
            grown.Add($"maps ({c[WcMapFail]:N0} inserts without a slot)");
        }
        if (c[WcScaleFail] > 0) { w.ScaleSlots *= 2; grown.Add($"the size table to {w.ScaleSlots:N0} slots"); }
        if (left > 0) { w.Rounds += 16; grown.Add($"rounds to {w.Rounds} ({left:N0} pieces left)"); }
        if ((over & 8u) != 0) Console.WriteLine("[VulkanSponza] rtgi cook walk: a piece's lattice past 128 points (clamped).");
        if (grown.Count == 0) return false;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] rtgi cook walk ({what}): room ran out -- again with {string.Join(", ", grown)}."));
        return true;
    }

    private uint[] ReadWalkU(GpuBufferHandle h, long words)
    {
        var result = new uint[words];
        const int piece = 1 << 26;
        for (long off = 0; off < 4 * words; off += piece)
        {
            var len = (int)Math.Min(piece, 4 * words - off);
            MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(h, (int)off, len).AsSpan()).CopyTo(result.AsSpan((int)(off / 4)));
        }
        return result;
    }
    private float[] ReadWalkF(GpuBufferHandle h, long words) => MemoryMarshal.Cast<uint, float>(ReadWalkU(h, words).AsSpan()).ToArray();

    // ---- The burial check (BuildCookVerifyPass) ----

    // Called each frame in CookStage.GpuVerify; true once the pass (or none) is in cookPassBuild.
    private bool StepGpuVerify(CensusGeometry geom)
    {
        var w = cookWalk ??= new CookWalkGpu();
        switch (w.Step)
        {
            case 0:
            {
                if (cookPassBuild is not { IsCompleted: true }) return false;
                var passes = w.Passes = cookPasses.ToArray();
                w.StepClock.Restart();
                w.FieldBuild = Task.Run(() =>
                {
                    w.Triangles ??= BuildWalkTriangles(geom);
                    return BuildWalkField(passes);
                });
                w.Step = 1;
                return false;
            }
            case 1:
            {
                if (w.FieldBuild is not { IsCompleted: true } build) return false;
                w.Field = build.Result;
                WalkUpload(w, verify: true);
                RecordWalkVerify(w);
                w.Step = 2;
                w.Drain = 0;
                return false;
            }
            case 2:
            {
                if (++w.Drain < WalkDrainFrames) return false;
                device.WaitIdle();
                if (WalkOverflowed(w, "burial check"))
                {
                    WalkUpload(w, verify: true);
                    RecordWalkVerify(w);
                    w.Drain = 0;
                    return false;
                }
                var c = w.Counters;
                var count = (int)Math.Min(c[WcVerify], (uint)w.VerifyRoom);
                var list = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(w.Verify, 0, Math.Max(count, 1) * 32).AsSpan()).ToArray();
                var finest = rtgiCell;
                var index = w.Passes.Length;
                w.VerifyMs = w.StepClock.Elapsed.TotalMilliseconds;
                var ms = w.VerifyMs;
                WalkFree(w, ref w.Keys); WalkFree(w, ref w.MapF); WalkFree(w, ref w.MapU); WalkFree(w, ref w.Verify);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] rtgi cook walk: burial check on the GPU: {c[WcFinal]:N0} pieces, {c[WcLattice]:N0} lattice points, {count:N0} dropped fine cubes by kept surface; {ms:0} ms."));
                cookPassBuild = Task.Run<CensusPass?>(() => WalkVerifyPass(list, count, index, finest, ms));
                w.Step = 3;
                return true;
            }
        }
        return true;
    }

    private void RecordWalkVerify(CookWalkGpu w)
    {
        var f = w.Field!;
        WalkClear(WbKeys, 2 * w.KeySlots);
        WalkClear(WbMapF, w.MapFWords);
        WalkClear(WbMapU, w.MapUWords);
        // The dropped cubes' least keys start past every key.
        WalkClear(WbMapU, MapSlots(w, WmapVDrop) * WalkMapValues[WmapVDrop].Stride, 0xFFFFFFFFu, w.MapValueBase[WmapVDrop]);
        WalkRun(WkMark, f.Count);
        WalkPieces(w, dropsOnly: true);
        WalkPoints(WmVerify);
        WalkPoints(WmVerifyPick);
        WalkRun(WkVCheck, MapSlots(w, WmapVDrop));
    }

    // The burial check's samples, as BuildCookVerifyPass lists them -- sorted, so the pass is the same every cook.
    private static CensusPass? WalkVerifyPass(Vector4[] list, int count, int index, float finest, double ms)
    {
        if (count == 0) return null;
        var items = new (Vector3 P, Vector3 N, byte Side)[count];
        for (var i = 0; i < count; i++)
            items[i] = (new Vector3(list[2 * i].X, list[2 * i].Y, list[2 * i].Z), new Vector3(list[2 * i + 1].X, list[2 * i + 1].Y, list[2 * i + 1].Z),
                (byte)BitConverter.SingleToUInt32Bits(list[2 * i].W));
        Array.Sort(items, (a, b) => a.Side != b.Side ? a.Side.CompareTo(b.Side) : a.P.X != b.P.X ? a.P.X.CompareTo(b.P.X) : a.P.Y != b.P.Y ? a.P.Y.CompareTo(b.P.Y) : a.P.Z.CompareTo(b.P.Z));
        var pass = new CensusPass
        {
            Index = index, Spacing = finest, Full = count, Keep = 1.0, Area = finest * finest,
            Gpu = new Vector4[count], Pos = new Vector3[count], Side = new byte[count], Alpha = new bool[count], Normal = new Vector3[count],
        };
        for (var i = 0; i < count; i++)
        {
            var (p, n, side) = items[i];
            pass.Gpu[i] = new Vector4(p, BitConverter.UInt32BitsToSingle(CensusOctPack(n)));
            pass.Pos[i] = p; pass.Side[i] = side; pass.Normal[i] = n;
        }
        pass.Ms = ms;
        return pass;
    }

    // ---- The cells (BuildCookCells) ----

    // Called each frame in CookStage.GpuCells; true once the cells are in cookCellsBuild.
    private bool StepGpuCells(CensusGeometry geom)
    {
        var w = cookWalk ??= new CookWalkGpu();
        switch (w.Step)
        {
            case 3:
            case 0:
            {
                // Room from the burial check's walk (fewer passes, so fewer pieces): a retry is a whole walk again.
                if (w.Counters.Length > WcLattice)
                {
                    w.FinalRoom = (int)Math.Min(int.MaxValue / 80, Math.Max(w.FinalRoom, 2L * w.Counters[WcFinal]));
                    w.LatticeRoom = (int)Math.Min(int.MaxValue / 8, Math.Max(w.LatticeRoom, 3L * w.Counters[WcLattice]));
                }
                var passes = w.Passes = cookPasses.ToArray();
                w.StepClock.Restart();
                w.FieldBuild = Task.Run(() =>
                {
                    w.Triangles ??= BuildWalkTriangles(geom);
                    return BuildWalkField(passes);
                });
                w.Step = 4;
                return false;
            }
            case 4:
            {
                if (w.FieldBuild is not { IsCompleted: true } build) return false;
                w.Field = build.Result;
                WalkUpload(w, verify: false);
                RecordWalkTable(w);
                w.Step = 5;
                w.Drain = 0;
                return false;
            }
            case 5:
            {
                if (++w.Drain < WalkDrainFrames) return false;
                device.WaitIdle();
                if (WalkOverflowed(w, "size table and cells"))
                {
                    WalkUpload(w, verify: false);
                    RecordWalkTable(w);
                    w.Drain = 0;
                    return false;
                }
                w.TableMs = w.StepClock.Elapsed.TotalMilliseconds;
                w.StepClock.Restart();
                // The size table rewritten at the CPU's room (CookTableRoom slots an entry), now its entries are counted:
                // laid out before they were, it had the most room any scene might need.
                var entries = (long)w.Counters[WcFineN] + w.Counters[WcCoarseN] + w.Counters[WcTDropN];
                var want = 1024;
                while (want < entries * CookTableRoom) want <<= 1;
                if (want < w.ScaleSlots)
                {
                    WalkFree(w, ref w.Scale);
                    w.ScaleSlots = want;
                    w.Scale = WalkBuffer(w, (long)want * 8, "sponza.rtgi.walk-scale");
                    WalkClear(WbScale, 2L * want);
                    WalkRun(WkCap, MapSlots(w, WmapCoarse));
                    WalkRun(WkFine, MapSlots(w, WmapFine));
                    if (CookDroppedTier) WalkRun(WkTDrop, MapSlots(w, WmapTDrop));
                }
                w.Cells = (int)w.Counters[WcCells];
                var cells = Math.Max(w.Cells, 1);
                WalkFree(w, ref w.CellF); WalkFree(w, ref w.CellU); WalkFree(w, ref w.CellM);
                // The cells numbered in key order, the same every cook (KIND_DENSE numbered them as threads met them): their
                // surfels' and pieces' order follows.
                var dense = ReadWalkU(w.CellKeys, 2L * w.Cells);
                var sortKeys = new ulong[w.Cells];
                var byKey = new int[w.Cells];
                for (var i = 0; i < w.Cells; i++) { sortKeys[i] = ((ulong)dense[2 * i + 1] << 32) | dense[2 * i]; byKey[i] = i; }
                Array.Sort(sortKeys, byKey);
                var rank = new uint[cells];
                var sorted = new uint[2 * cells];
                for (var r = 0; r < w.Cells; r++) { rank[byKey[r]] = (uint)r; sorted[2 * r] = (uint)sortKeys[r]; sorted[2 * r + 1] = (uint)(sortKeys[r] >> 32); }
                device.WriteGpuBuffer(w.CellKeys, 0, MemoryMarshal.AsBytes(sorted.AsSpan()));
                WalkFree(w, ref w.Rank);
                w.Rank = WalkBuffer(w, cells * 4L, "sponza.rtgi.walk-rank", MemoryMarshal.AsBytes(rank.AsSpan()));
                WalkRun(WkRank, MapSlots(w, WmapCells));
                w.CellF = WalkBuffer(w, (long)cells * WalkCellF * 4, "sponza.rtgi.walk-cell-f");
                w.CellU = WalkBuffer(w, (long)cells * WalkCellU * 4, "sponza.rtgi.walk-cell-u");
                w.CellM = WalkBuffer(w, (long)cells * 16, "sponza.rtgi.walk-cell-m");
                WalkClear(WbCellF, (long)cells * WalkCellF);
                WalkClear(WbCellU, (long)cells * WalkCellU, 0xFFFFFFFFu);
                WalkClear(WbCellM, cells);
                WalkPoints(WmAcc);
                w.Step = 6;
                w.Drain = 0;
                return false;
            }
            case 6:
            {
                if (++w.Drain < WalkDrainFrames) return false;
                device.WaitIdle();
                // Each cell's first surfel: its occupied quarter-cubes counted in order.
                var meta = ReadWalkU(w.CellM, 4L * w.Cells);
                long total = 0;
                for (var i = 0; i < w.Cells; i++)
                {
                    meta[4 * i + 3] = (uint)total;
                    total += BitOperations.PopCount(meta[4 * i + 1]) + BitOperations.PopCount(meta[4 * i + 2]);
                }
                device.WriteGpuBuffer(w.CellM, 0, MemoryMarshal.AsBytes(meta.AsSpan()));
                w.Surfels_ = (int)total;
                WalkFree(w, ref w.Surfels);
                w.Surfels = WalkBuffer(w, Math.Max(total, 1) * 32, "sponza.rtgi.walk-surfels");
                WalkClear(WbSurfels, Math.Max(total, 1) * 8);
                WalkPoints(WmFinal);
                w.Step = 7;
                w.Drain = 0;
                return false;
            }
            case 7:
            {
                if (++w.Drain < WalkDrainFrames) return false;
                device.WaitIdle();
                var c = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(w.Count, 0, WalkCounters * 4).AsSpan()).ToArray();
                if (c[WcScaleFail] > w.Counters[WcScaleFail])
                    Console.WriteLine($"[VulkanSponza] rtgi cook walk: the size table rewritten small lost {c[WcScaleFail] - w.Counters[WcScaleFail]} entries.");
                var n = w.Cells;
                w.CellsRead = (ReadWalkU(w.CellKeys, 2L * n), ReadWalkF(w.CellF, (long)n * WalkCellF), ReadWalkU(w.CellU, (long)n * WalkCellU),
                    ReadWalkU(w.CellM, 4L * n), ReadWalkU(w.Scale, 2L * w.ScaleSlots), c);
                // The surfels from their sums, then their candidate joins (the pieces' graph, PrepareCookSurfels').
                w.SurfOwner = WalkBuffer(w, Math.Max(w.Surfels_, 1) * 4L, "sponza.rtgi.walk-surf-owner");
                w.EdgeRoom = (int)Math.Min(int.MaxValue / 8, Math.Max(1L << 20, 4L * w.Surfels_));
                w.Edges = WalkBuffer(w, (long)w.EdgeRoom * 8, "sponza.rtgi.walk-edges");
                WalkRun(WkSurfPrep, n);
                RecordWalkJoins(w);
                w.Step = 8;
                w.Drain = 0;
                return false;
            }
            case 8:
            {
                if (++w.Drain < WalkDrainFrames) return false;
                device.WaitIdle();
                var c = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(w.Count, 0, WalkCounters * 4).AsSpan()).ToArray();
                if ((c[WcOver] & 32u) != 0)
                {
                    w.EdgeRoom = (int)Math.Min(int.MaxValue / 8, Math.Max(2L * w.EdgeRoom, c[WcEdges] * 5L / 4));
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] rtgi cook walk (joins): room ran out -- again with {w.EdgeRoom:N0} joins."));
                    WalkFree(w, ref w.Edges);
                    w.Edges = WalkBuffer(w, (long)w.EdgeRoom * 8, "sponza.rtgi.walk-edges");
                    RecordWalkJoins(w);
                    w.Drain = 0;
                    return false;
                }
                w.EdgeCount = (int)c[WcEdges];
                var (keys, f, uw, meta, scale, cc) = w.CellsRead!.Value;
                w.CellsRead = null;
                w.CellsMs = w.StepClock.Elapsed.TotalMilliseconds;
                var n = w.Cells;
                var counts = new CookWalkCounts { Points = cc[WcPoints], Buried = cc[WcBuried], Filled = w.Counters[WcFilled], None = w.Counters[WcNone], Pieces = w.Counters[WcFinal], Lattice = w.Counters[WcLattice] };
                var table = (Fine: (long)w.Counters[WcFineN], Coarse: (long)w.Counters[WcCoarseN], Bent: (int)w.Counters[WcBent], TDrop: (int)w.Counters[WcTDropN]);
                var scaleSlots = w.ScaleSlots;
                var finest = rtgiCell;
                var (tableMs, cellsMs, pieces, lattice, surfels, joins) = (w.TableMs, w.CellsMs, w.Counters[WcFinal], w.Counters[WcLattice], w.Surfels_, w.EdgeCount);
                // Everything but the surfels and their joins freed: the pieces' stage reads those (StepGpuPieces).
                foreach (var h in new[] { w.Tris, w.Pass, w.Grid, w.Order, w.Samples, w.Pieces, w.Final, w.Lattice, w.Keys, w.MapF, w.MapU,
                    w.CellKeys, w.CellF, w.CellU, w.CellM, w.Scale, w.Verify, w.Rank, w.SurfOwner })
                    if (!h.Equals(default(GpuBufferHandle))) { w.Owned.Remove(h); Disown(h); }
                w.Tris = w.Pass = w.Grid = w.Order = w.Samples = w.Pieces = w.Final = w.Lattice = w.Keys = w.MapF = w.MapU = default;
                w.CellKeys = w.CellF = w.CellU = w.CellM = w.Scale = w.Verify = w.Rank = w.SurfOwner = default;
                w.Field = null;
                w.Triangles = null;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] rtgi cook walk: cells on the GPU: {pieces:N0} pieces, {lattice:N0} lattice points, {counts.Points:N0} points kept, {counts.Buried:N0} dropped; {surfels:N0} surfels, {joins:N0} candidate joins; size table and cell keys {tableMs:0} ms, cells, surfels and joins {cellsMs:0} ms."));
                cookCellsBuild = Task.Run(() => AssembleWalkCells(n, keys, f, uw, meta, scale, scaleSlots, counts, table, finest, tableMs, cellsMs, surfels, joins));
                w.Step = 9;
                return true;
            }
        }
        return false;
    }

    private void RecordWalkJoins(CookWalkGpu w)
    {
        WalkClear(WbCount, 1, 0u, WcEdges);
        WalkClear(WbCount, 1, 0u, WcOver);
        graph.Dispatch(walkPassHandle, WalkDispatch(WkJoins, (w.Surfels_ + 63) / 64, items: w.Surfels_, clearY: (uint)w.EdgeRoom, clearZ: RtgiTopLevel + 1));
    }

    // A chunk of the joins' rays' ends into rtgi_cook.comp's RtgiCookJoins (the cook's JoinTrace stage, kinds 18 and 19 next).
    private void RecordWalkJoinFill(CookWalkGpu w, int first, int count) =>
        graph.Dispatch(walkPassHandle, WalkDispatch(WkJoinFill, (count + 63) / 64, items: count, clearAt: first));

    private void RecordWalkTable(CookWalkGpu w)
    {
        var f = w.Field!;
        WalkClear(WbKeys, 2 * w.KeySlots);
        WalkClear(WbMapF, w.MapFWords);
        WalkClear(WbMapU, w.MapUWords);
        WalkClear(WbScale, 2L * w.ScaleSlots);
        // The size field: dead samples marked, the log sizes smoothed (z -> y, y -> z, z -> y: the last in y).
        WalkRun(WkMark, f.Count);
        for (var k = 0; k < WalkSmoothPasses; k++) WalkRun(WkSmooth, f.Count, w: k % 2 == 0 ? 1f : 0f);
        WalkPieces(w, dropsOnly: false);
        WalkPoints(WmTable);
        WalkRun(WkBend, MapSlots(w, WmapCoarse));
        WalkRun(WkCap, MapSlots(w, WmapCoarse));
        WalkRun(WkFine, MapSlots(w, WmapFine));
        if (CookDroppedTier) WalkRun(WkTDrop, MapSlots(w, WmapTDrop));
        WalkPoints(WmKeys);
        WalkRun(WkDense, MapSlots(w, WmapCells));
    }

    // The GPU walk's results as BuildCookCells' (the cells' accumulations by level, the size table); the surfels and their
    // joins stay on the GPU for the pieces (StepGpuPieces).
    private static CookCells AssembleWalkCells(int n, uint[] keys, float[] f, uint[] uw, uint[] meta, uint[] scale,
        int scaleSlots, CookWalkCounts counts, (long Fine, long Coarse, int Bent, int TDrop) table, float finest, double tableMs, double cellsMs,
        int surfels, int joins)
    {
        var clock = Stopwatch.StartNew();
        var levels = RtgiTopLevel + 1;
        var cells = new CookCells { Share = 1f, FineCube = finest, Counts = counts };
        var acc = Enumerable.Range(0, levels).Select(_ => new Dictionary<long, CookAcc>()).ToArray();
        for (var i = 0; i < n; i++)
        {
            var (x, y, z, side, level) = UnpackWalkKey(keys[2 * i], keys[2 * i + 1]);
            var at = WalkCellF * i;
            var a = new CookAcc
            {
                N = new Vector3(f[at], f[at + 1], f[at + 2]), Area = f[at + 3], Junction = (meta[4 * i] & 1u) != 0,
                Best = new Vector3(f[at + 4], f[at + 5], f[at + 6]), BestD = 0f,
                Sub = new Vector3[CookCandidates], SubD = new float[CookCandidates],
            };
            for (var q = 0; q < CookCandidates; q++)
            {
                if (uw[WalkCellU * i + 1 + q] == 0xFFFFFFFFu) continue;
                a.Sub[q] = new Vector3(f[at + 8 + 3 * q], f[at + 9 + 3 * q], f[at + 10 + 3 * q]);
                a.SubMask |= 1 << q;
            }
            acc[level][CensusCellKey(x, y, z, side)] = a;
        }
        cells.Acc = acc;
        cells.Surfels = new CookSurfelSet { GpuJoins = joins };
        cells.SurfacePoints = surfels;
        cells.FineCubes = table.Fine;
        cells.CoarseCubes = table.Coarse;
        cells.BentCubes = table.Bent;
        cells.ScaleTable = scale;
        cells.ScaleSlots = scaleSlots;
        cells.ScaleEntries = (int)(table.Fine + table.Coarse + table.TDrop);
        ListCookCells(cells, levels);
        cells.TableMs = tableMs;
        cells.EnumerateMs = cellsMs + clock.Elapsed.TotalMilliseconds;
        return cells;
    }
}
