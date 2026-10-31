using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The cooked cells' surface pieces on the GPU (rtgi_cookpieces.comp): the CPU's BuildCookPieces (SponzaLoop.RtgiPieces.cs)
// over the GPU walk's surfels and their traced joins, a level at a time from the top -- each level's pieces' parents are
// the level above's. Per level, a step a frame: the components (rounds of hooking and jumping until nothing changes),
// the pieces numbered, their sums and cubes, the cubes' runs laid out (here), each cube's pieces in order; read back,
// the pieces by cube as FinalizeCook reads them. Crytek Sponza's 23 M surfels and 80 M joins took 27 s on the CPU.
internal sealed partial class SponzaLoop
{
    private ShaderInterface piecesInterface = null!;
    private PassHandle piecesPassHandle;
    private PipelineHandle piecesPipeline;

    private const int PkClear = 0, PkInit = 1, PkHook = 2, PkJump = 3, PkPieceId = 4, PkPieceAcc = 5, PkPieceCube = 6,
        PkCubeDense = 7, PkPieceFill = 8, PkPieceRank = 9, PkPieceOf = 10, PkEdgeLevels = 11;
    private const int PcChanged = 0, PcPieces = 1, PcCubes = 2, PcCubeFail = 3, PieceCounters = 16;
    private const int PbCount = 0, PbF = 1, PbU = 2, PbCubeKeys = 3, PbCubeVals = 4, PbEdgeLevels = 5;
    // Hook-and-jump rounds a frame (the frame's last round's changes read back: none, the components are done).
    private const int PieceRounds = 6;

    private void DeclareRtgiCookPiecesPass(Func<string, ShaderInterface> reflect)
    {
        piecesInterface = reflect("rtgi_cookpieces.comp");
        piecesPassHandle = graph.ComputePass("rtgi-cook-pieces").Shader(piecesInterface).Handle;
    }

    private void CreateRtgiCookPiecesPipeline(Func<string, ShaderInterface, ComputePipelineOptions, PipelineHandle> compute) =>
        piecesPipeline = compute("rtgi_cookpieces", piecesInterface, ComputePipelineOptions.None);

    private DispatchCommand PiecesDispatch(int kind, long items, int level = 0, int clearWhich = 0, uint fill = 0, long first = 0)
    {
        var w = cookWalk!;
        return new DispatchCommand(piecesPipeline, (int)Math.Clamp((items + 63) / 64, 1, int.MaxValue), 1, 1,
            new ShaderUniform[]
            {
                new("uKind", new Vector4Uniform(new Vector4(kind, level, Bits((uint)items), RtgiTopLevel + 1))),
                new("uField", new Vector4Uniform(new Vector4(rtgiCell, Bits((uint)Math.Max(w.CubeSlots - 1, 0)), clearWhich, Bits(fill)))),
                new("uClear", new Vector4Uniform(new Vector4(Bits((uint)first), 0f, 0f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("PieceSurfels", WalkOr(w.Surfels)),
                new ShaderBufferBinding("PieceEdges", WalkOr(w.Edges)),
                new ShaderBufferBinding("PieceJoinBits", WalkOr(cookJoinBits)),
                new ShaderBufferBinding("PieceCount", WalkOr(w.PieceCount)),
                new ShaderBufferBinding("PieceLabel", WalkOr(w.Label)),
                new ShaderBufferBinding("PieceId", WalkOr(w.PieceIds)),
                new ShaderBufferBinding("PieceRoot", WalkOr(w.PieceRoot)),
                new ShaderBufferBinding("PieceF", WalkOr(w.PieceF)),
                new ShaderBufferBinding("PieceU", WalkOr(w.PieceU)),
                new ShaderBufferBinding("PieceCubeKeys", WalkOr(w.CubeKeys)),
                new ShaderBufferBinding("PieceCubeVals", WalkOr(w.CubeVals)),
                new ShaderBufferBinding("PieceCubes", WalkOr(w.Cubes)),
                new ShaderBufferBinding("PieceList", WalkOr(w.PieceList)),
                new ShaderBufferBinding("PieceOut", WalkOr(w.PieceOut)),
                new ShaderBufferBinding("PieceOfCur", WalkOr(w.PieceOfCur)),
                new ShaderBufferBinding("PieceOfUp", WalkOr(w.PieceOfUp)),
                new ShaderBufferBinding("PieceEdgeLevels", WalkOr(w.EdgeLevels)),
            });
    }

    private void PiecesRun(int kind, long items, int level = 0) => graph.Dispatch(piecesPassHandle, PiecesDispatch(kind, items, level));

    private void PiecesClear(int which, long words, uint fill = 0, long first = 0)
    {
        if (words > 0) graph.Dispatch(piecesPassHandle, PiecesDispatch(PkClear, words, clearWhich: which, fill: fill, first: first));
    }

    private void RecordPieceRounds(CookWalkGpu w, int level)
    {
        for (var r = 0; r < PieceRounds; r++)
        {
            PiecesClear(PbCount, 1, 0u, PcChanged);
            PiecesRun(PkHook, w.EdgeCount, level);
            PiecesRun(PkJump, w.Surfels_);
            PiecesRun(PkJump, w.Surfels_);
        }
    }

    private uint[] ReadPieceCounters(CookWalkGpu w) =>
        MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(w.PieceCount, 0, PieceCounters * 4).AsSpan()).ToArray();

    // Called each frame in CookStage.GpuPieces; true once FinalizeCook is under way (cookFinalizeBuild) with the pieces.
    private bool StepGpuPieces(CookCells cells, (int Level, long Key)[] inside, uint[] chosen, int keepBacks)
    {
        var w = cookWalk!;
        var n = w.Surfels_;
        var levels = RtgiTopLevel + 1;
        switch (w.PStep)
        {
            case 0:
            {
                w.PClock.Restart();
                w.Label = WalkBuffer(w, Math.Max(n, 1) * 4L, "sponza.rtgi.piece-label");
                w.PieceIds = WalkBuffer(w, Math.Max(n, 1) * 4L, "sponza.rtgi.piece-id");
                w.PieceRoot = WalkBuffer(w, Math.Max(n, 1) * 4L, "sponza.rtgi.piece-root");
                w.PieceOfCur = WalkBuffer(w, Math.Max(n, 1) * 4L, "sponza.rtgi.piece-of-a");
                w.PieceOfUp = WalkBuffer(w, Math.Max(n, 1) * 4L, "sponza.rtgi.piece-of-b");
                w.PieceCount = WalkBuffer(w, PieceCounters * 4, "sponza.rtgi.piece-count");
                PiecesClear(PbCount, PieceCounters);
                // Each clear join's levels where its surfels share a cube, once.
                w.EdgeLevels = WalkBuffer(w, Math.Max((w.EdgeCount + 1) / 2, 1) * 4L, "sponza.rtgi.piece-edge-levels");
                PiecesRun(PkEdgeLevels, w.EdgeCount);
                w.PLevel = levels - 1;
                w.LevelPieces = new (WalkSlot[] Cubes, WalkSlot[] Out)[levels];
                PiecesRun(PkInit, n);
                RecordPieceRounds(w, w.PLevel);
                w.PStep = 2;
                w.Drain = 0;
                return false;
            }
            case 2:
            {
                if (++w.Drain < WalkDrainFrames) return false;
                device.WaitIdle();
                var c = ReadPieceCounters(w);
                w.Drain = 0;
                if (c[PcChanged] > 0)
                {
                    RecordPieceRounds(w, w.PLevel);
                    return false;
                }
                PiecesClear(PbCount, 1, 0u, PcPieces);
                PiecesRun(PkPieceId, n);
                w.PStep = 3;
                return false;
            }
            case 3:
            {
                if (++w.Drain < WalkDrainFrames) return false;
                device.WaitIdle();
                var pieces = (int)ReadPieceCounters(w)[PcPieces];
                w.Pieces_ = pieces;
                if (pieces > w.PieceRoom)
                {
                    w.PieceRoom = Math.Max(pieces, w.PieceRoom * 2);
                    foreach (var h in new[] { w.PieceF, w.PieceU, w.PieceList, w.PieceOut, w.CubeKeys, w.CubeVals, w.Cubes })
                        if (!h.Equals(default(GpuBufferHandle))) { w.Owned.Remove(h); Disown(h); }
                    w.CubeSlots = 1024;
                    while (w.CubeSlots < 2L * w.PieceRoom) w.CubeSlots <<= 1;
                    w.PieceF = WalkBuffer(w, (long)w.PieceRoom * 24, "sponza.rtgi.piece-f");
                    w.PieceU = WalkBuffer(w, (long)w.PieceRoom * 16, "sponza.rtgi.piece-u");
                    w.PieceList = WalkBuffer(w, (long)w.PieceRoom * 4, "sponza.rtgi.piece-list");
                    w.PieceOut = WalkBuffer(w, (long)w.PieceRoom * 32, "sponza.rtgi.piece-out");
                    w.CubeKeys = WalkBuffer(w, (long)w.CubeSlots * 8, "sponza.rtgi.piece-cube-keys");
                    w.CubeVals = WalkBuffer(w, (long)w.CubeSlots * 8, "sponza.rtgi.piece-cube-vals");
                    w.Cubes = WalkBuffer(w, (long)w.PieceRoom * 16, "sponza.rtgi.piece-cubes");
                }
                PiecesClear(PbF, 6L * pieces);
                PiecesClear(PbU, 4L * pieces);
                PiecesClear(PbCubeKeys, 2L * w.CubeSlots);
                PiecesClear(PbCubeVals, 2L * w.CubeSlots);
                PiecesClear(PbCount, 2, 0u, PcCubes);
                PiecesRun(PkPieceAcc, n, w.PLevel);
                PiecesRun(PkPieceCube, pieces, w.PLevel);
                PiecesRun(PkCubeDense, w.CubeSlots, w.PLevel);
                w.PStep = 4;
                w.Drain = 0;
                return false;
            }
            case 4:
            {
                if (++w.Drain < WalkDrainFrames) return false;
                device.WaitIdle();
                var c = ReadPieceCounters(w);
                if (c[PcCubeFail] > 0) Console.WriteLine($"[VulkanSponza] rtgi cook pieces: {c[PcCubeFail]} pieces found no cube slot at level {w.PLevel}.");
                var cubes = (int)c[PcCubes];
                w.Cubes_ = cubes;
                // Each cube's run of pieces, in the cubes' order.
                var runs = MemoryMarshal.Cast<byte, WalkSlot>(device.ReadGpuBuffer(w.Cubes, 0, Math.Max(cubes, 1) * 16).AsSpan()).ToArray();
                uint at = 0;
                for (var i = 0; i < cubes; i++) { runs[i].W = at; at += runs[i].Z; }
                device.WriteGpuBuffer(w.Cubes, 0, MemoryMarshal.AsBytes(runs.AsSpan()));
                PiecesRun(PkPieceFill, w.Pieces_, w.PLevel);
                PiecesRun(PkPieceRank, w.Pieces_, w.PLevel);
                PiecesRun(PkPieceOf, n, w.PLevel);
                w.PStep = 5;
                w.Drain = 0;
                return false;
            }
            case 5:
            {
                if (++w.Drain < WalkDrainFrames) return false;
                device.WaitIdle();
                var cubes = MemoryMarshal.Cast<byte, WalkSlot>(device.ReadGpuBuffer(w.Cubes, 0, Math.Max(w.Cubes_, 1) * 16).AsSpan()).ToArray();
                var outs = MemoryMarshal.Cast<byte, WalkSlot>(device.ReadGpuBuffer(w.PieceOut, 0, Math.Max(w.Pieces_, 1) * 32).AsSpan()).ToArray();
                w.LevelPieces[w.PLevel] = (cubes.AsSpan(0, w.Cubes_).ToArray(), outs);
                (w.PieceOfCur, w.PieceOfUp) = (w.PieceOfUp, w.PieceOfCur);
                if (--w.PLevel >= 0)
                {
                    PiecesRun(PkInit, n);
                    RecordPieceRounds(w, w.PLevel);
                    w.PStep = 2;
                    w.Drain = 0;
                    return false;
                }
                var levelPieces = w.LevelPieces;
                var ms = w.PClock.Elapsed.TotalMilliseconds;
                var (surfels, joins) = (n, w.EdgeCount);
                WalkFreeAll(w);
                cookWalk = null;
                Disown(cookJoinBits);
                cookJoinBits = default;
                var finest = rtgiCell;
                cookFinalizeBuild = Task.Run(() =>
                {
                    cells.Pieces = WalkPiecesByCube(levelPieces, surfels, joins, ms);
                    cells.PiecesMs = ms;
                    return FinalizeCook(cells, inside, chosen, finest, keepBacks);
                });
                return true;
            }
        }
        return false;
    }

    // Each level's pieces as BuildCookPieces gives them: by cube (CensusCellKey), in order.
    private static Dictionary<long, CookPiece[]>[] WalkPiecesByCube((WalkSlot[] Cubes, WalkSlot[] Out)[] levels, int surfels, int joins, double ms)
    {
        var result = new Dictionary<long, CookPiece[]>[levels.Length];
        Parallel.For(0, levels.Length, l =>
        {
            var (cubes, outs) = levels[l];
            var mine = result[l] = new Dictionary<long, CookPiece[]>(cubes.Length);
            foreach (var cube in cubes)
            {
                var (x, y, z, side, _) = UnpackWalkKey(cube.X, cube.Y);
                var pieces = new CookPiece[cube.Z];
                for (var k = 0; k < pieces.Length; k++)
                {
                    var a = outs[2 * (cube.W + k)];
                    var b = outs[2 * (cube.W + k) + 1];
                    pieces[k] = new CookPiece
                    {
                        Sum = new Vector3(BitConverter.UInt32BitsToSingle(a.X), BitConverter.UInt32BitsToSingle(a.Y), BitConverter.UInt32BitsToSingle(a.Z)),
                        Count = 1, Mask = a.W, Own = BitConverter.UInt32BitsToSingle(b.X), Whole = BitConverter.UInt32BitsToSingle(b.Y),
                        Parent = (int)Math.Min(b.Z, 0xFFFFu), Rep = (int)b.W, Index = k,
                    };
                }
                mine[CensusCellKey(x, y, z, side)] = pieces;
            }
        });
        if (CookDumpPath("multi.bin") is { } multi)
        {
            using var bw = new BinaryWriter(File.Create(multi));
            for (var l = 0; l < result.Length; l++)
                foreach (var (key, pieces) in result[l])
                    if (pieces.Length > 1) { bw.Write(l); bw.Write(key); bw.Write(pieces.Length); }
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] rtgi cook: surface pieces on the GPU: {surfels:N0} surfels, {joins:N0} joins, {result.Length} levels in {ms:0} ms; cubes of several pieces by level {string.Join("/", result.Select(r => r.Values.Count(v => v.Length > 1)))}, the most in one by level {string.Join("/", result.Select(r => r.Count == 0 ? 0 : r.Values.Max(v => v.Length)))}."));
        return result;
    }
}
