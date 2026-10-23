using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// Hit cells (hit_cells.comp): which patch a gather ray's hit lies among, either side, cooked per triangle row and read by
// the shade from the hit's row and barycentrics (the Metal trace writes both) instead of searched per hit; the hit's sun
// is the cell's too: the shade's blend of the texels' sun bits at its centre, made at each sun change.
// The search (the cook's hash at the hit's cells, every candidate texel's record, then the nearest patch) was ~25 ms of a
// 39 ms shade pass of 1 M rays, and its random reads slowed the Metal trace beside it 4x (BLIX_SHADE_AB, 2026-10-16).
// The cells are the geometry's and the lookup's: built once when the cook's setup is done (the hash, the texels'
// patches, the sun points up). A sun bit a cell, shot from its centre, was wrong where a large triangle's cell centre
// lay inside a solid (a ray from inside escapes): the crevices' corridor came out 2.3x too bright; the hit's own shadow
// ray (exact, 0 or 1) mottled the walls (2026-10-16). The blend of the texels' bits skips buried texels and filters.
// --live-texels on the host passes only (their sun is one column); --no-hit-cells keeps the search.
internal sealed partial class SponzaLoop
{
    private bool hitCellsOn = true;
    // A cell's side on a row's barycentric square: 10 cm (the texels' level 1).
    private float hitCellSize = 0.1f;
    // --cell-near D (0: off): hits nearer than D take the search's blend, not their cell (texel_nodes_shade.glsl), in
    // both rounds -- one patch a cell side read the creases' light across them (crevices' seams 0.83 -> 0.87, thinwall's
    // leak 0.0017 -> 0.0002); the fine round's alone was worse (0.78). Their sun was the search's too: a cell across a
    // crease gave the hits beside it the wrong sun (Cornell's crease error 6.0% -> 2.1% at 0.1 m, 2026-10-21) -- now the
    // hit's own shadow ray's (--hit-sun), so the rule is for the patch. Sponza's near hits are many and one in a workgroup
    // holds the rest to the search: shade 10 -> 27 ms a 1 M rays (2026-10-19). 25 cm with the near hits lit by their
    // texels (--near-texel-light): the reach tools/gi-model/crease_bounce.py needs (10 cm leaves ~1% at a texel beside a
    // crease); Cornell's crease rms 3.10% -> 2.90%, the box's base +13% -> +8% (2026-10-21). Its cost on Sponza is the
    // next Sponza pass's to measure.
    private float hitCellNear = 0.25f;
    // --cell-marks: the cells a contact (the surface turning over 30 degrees between its centre and a corner) crosses
    // marked, their hits the search's (hit_cells.comp). Even so: Sponza's cells 24% marked and a workgroup's one marked
    // hit holding the rest to the search, shade 38 ms a 1 M rays, and the cell build 0.65 -> 6.4 s (2026-10-19).
    // (The cells a sun edge crossed were marked too, --sun-edges, their hits' sun the search's: the hit's own shadow ray
    // replaced it, 2026-10-21.)
    private bool cellMarks;
    private ShaderInterface hitCellsInterface = null!;
    private PipelineHandle hitCellsPipeline;
    private GpuBufferHandle hitCellsBuffer;
    private int hitCellRows, hitCellItems;
    private GpuBufferHandle hitCellWork;
    private long hitCellCount;

    // Whether a gather pass reads the cells (counted: the passes either way, logged at the trace's end): on the host only,
    // where the trace (SunNodePassNow) shoots each hit's sun.
    private long hitCellPassesOn, hitCellPassesOff;
    private (Vector3, int) hitCellsSun = (new Vector3(float.NaN), -1);
    // A pass under way: the sun it's for, the next work item, its clock.
    private (Vector3, int) hitCellsSunFor = (new Vector3(float.NaN), -1);
    private int hitCellsSunFirst;
    private readonly Stopwatch hitCellsSunClock = new();

    // The cells' sun for the cook's (mode 1), before its trace: the texels' sun bits up for it (liveSunDone).
    // True when it ran this call. Spread over frames, ~100 ms each: Sponza's whole pass is ~0.8 s (2026-10-20).
    private bool HitCellsSunNow()
    {
        using var profScope = Prof("cells-sun");
        if (!HitCellsReady || !liveSunDone || liveSunColumn < 0 || hitCellsSun == (liveSunToward, liveSunColumn)) return false;
        if (hitCellsSunFor != (liveSunToward, liveSunColumn))
        {
            hitCellsSunFor = (liveSunToward, liveSunColumn);
            hitCellsSunFirst = 0;
            hitCellsSunClock.Restart();
        }
        var clock = Stopwatch.StartNew();
        // A dispatch a quarter-million work items (of up to 256 cells each): Sponza's whole build in one ran past the GPU's
        // watchdog once a cell's corners were read too, and the device was lost (2026-10-19).
        const int perDispatch = 4096 * 64;
        for (; hitCellsSunFirst < hitCellItems && clock.Elapsed.TotalMilliseconds < 100; hitCellsSunFirst += perDispatch)
            device.DispatchNow(new[] { HitCellsCommand(Math.Min(perDispatch, hitCellItems - hitCellsSunFirst), hitCellsSunFirst, 1) }, "hit-cells-sun");
        if (hitCellsSunFirst < hitCellItems) return true;
        hitCellsSun = (liveSunToward, liveSunColumn);
        hitCellsSunFor = (new Vector3(float.NaN), -1);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] hit cells: the sun of {hitCellCount:N0} cells from the texels' bits in {hitCellsSunClock.Elapsed.TotalSeconds:0.00} s{CellMarks()}."));
        return true;
    }
    // BLIX_CELL_MARKS=1: the cells marked for the search (hit_cells.comp: bit 31 a contact, bit 30 a sun edge), counted.
    private static readonly bool cellMarksLog = Environment.GetEnvironmentVariable("BLIX_CELL_MARKS") == "1";

    private string CellMarks()
    {
        if (!cellMarksLog) return "";
        device.WaitIdle();
        var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(hitCellsBuffer, 0, (int)((1L + 2L * hitCellRows + 2L * hitCellCount) * 4)).AsSpan());
        var at = 1 + 2 * hitCellRows;
        long contact = 0, edge = 0, either = 0;
        for (long c = 0; c < hitCellCount; c++)
        {
            var w = words[(int)(at + 2 * c + 1)];
            if ((w & 0x80000000u) != 0) contact++;
            if ((w & 0x40000000u) != 0) edge++;
            if ((w & 0xC0000000u) != 0) either++;
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"; marked for the search: a contact {100.0 * contact / hitCellCount:0.0}%, a sun edge {100.0 * edge / hitCellCount:0.0}%, either {100.0 * either / hitCellCount:0.0}%");
    }

    private bool HitCellsShade(int discover)
    {
        var on = discover == 0 && HitCellsReady && liveSunColumn >= 0 && hitCellsSun == (liveSunToward, liveSunColumn);
        if (on) hitCellPassesOn++; else hitCellPassesOff++;
        return on;
    }

    private bool HitCellsReady => liveTexels && hitCellsOn && !hitCellsBuffer.Equals(default(GpuBufferHandle)) && hitCellsBuild is null;

    private void DeclareHitCells(Func<string[], ShaderInterface> reflect)
    {
        hitCellsInterface = reflect(new[] { "hit_cells.comp" });
    }

    private void CreateHitCellsPipeline(string shaderDir) =>
        hitCellsPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "hit_cells.comp.spv")), hitCellsInterface, "hit_cells")), "hit_cells"));

    // A fresh build's patches pass, stepped over frames (StepHitCells): whole in one frame it held Crytek Sponza's merge
    // frame 1.2 s (2026-10-23).
    private sealed class HitCellsBuild
    {
        public required string? Path;
        public required int Bytes;
        public required uint[] Work;
        public required Stopwatch Clock;
        public required double LayoutMs;
        public int First;
    }
    private HitCellsBuild? hitCellsBuild;
    private const string HitCellsProgress = "hit cells: their patches";

    // Once the cook's setup is done (MergeDiscovered): the layout from the packed rows, then each cell's patches.
    private void BuildHitCells()
    {
        BeginHitCells();
        while (!StepHitCells(double.PositiveInfinity)) { }
    }

    private void BeginHitCells()
    {
        if (!liveTexels || !hitCellsOn || !hitCellsBuffer.Equals(default(GpuBufferHandle)) || rayGpuData is not { } data) return;
        var clock = Stopwatch.StartNew();
        var rows = data.Triangles.Length / 4;
        // The geometry's: read from the GI bundle when built before for the same stages and settings (~10 s of Sponza's
        // boot, 2026-10-19).
        var cachePath = TransferOn && !livePatchTexels || Environment.GetEnvironmentVariable("BLIX_NO_CELLS_CACHE") == "1" ? null : HitCellsPath(rows);   // A' adds its cells' light: built
        if (cachePath is not null && ReadHitCells(cachePath, rows))
        {
            hitCellsSun = hitCellsSunFor = (new Vector3(float.NaN), -1);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] hit cells: {hitCellCount:N0} cells over {rows:N0} triangle rows read from {cachePath} in {clock.Elapsed.TotalMilliseconds:0} ms."));
            return;
        }
        var tris = data.Triangles;
        var pos = data.Positions;
        var cellSize = hitCellSize;
        var r = new ushort[rows];
        Parallel.For(0, rows, row =>
        {
            Vector3 P(uint v) { var p = pos[v]; return new Vector3(p.X, p.Y, p.Z); }
            var a = P(tris[4 * row]); var b = P(tris[4 * row + 1]); var c = P(tris[4 * row + 2]);
            var edge = MathF.Max(Vector3.Distance(a, b), MathF.Max(Vector3.Distance(b, c), Vector3.Distance(c, a)));
            r[row] = (ushort)Math.Clamp((int)MathF.Ceiling(edge / cellSize), 1, 1023);
        });
        var words = new uint[1 + 2L * rows];
        var work = new List<uint>(rows * 2);   // work items (hit_cells.comp): a row, its first cell -- 256 cells an item
        words[0] = (uint)rows;
        long cells = 0;
        for (var row = 0; row < rows; row++)
        {
            var n = (long)r[row] * (r[row] + 1) / 2;
            words[1 + 2 * row] = (uint)cells;
            words[2 + 2 * row] = r[row];
            for (long c = 0; c < n; c += 256) { work.Add((uint)row); work.Add((uint)c); }
            cells += n;
        }
        if ((1L + 2L * rows + 2L * cells) * 4 > int.MaxValue || cells >= 1L << 30)
        {
            Console.WriteLine($"[VulkanSponza] hit cells: {cells:N0} cells are past a buffer's 2 GB; the search kept.");
            hitCellsOn = false;
            return;
        }
        var bytes = (int)((1L + 2L * rows + 2L * cells) * 4);
        hitCellsBuffer = Own(device.CreateGpuBuffer(bytes, MemoryMarshal.AsBytes(words.AsSpan()), "sponza.hit-cells"));
        // --live-transfer: each cell's outgoing light, either side, per column (hit_cells.comp mode 1).
        if (TransferOn && (!livePatchTexels || ListCellLight)) cellLightBuffer = Own(device.CreateGpuBuffer(checked((int)(cells * 16)), name: "sponza.hit-cells.light"));
        hitCellRows = rows;
        hitCellItems = work.Count / 2;
        hitCellWork = Own(device.CreateGpuBuffer(Math.Max(16, work.Count * 4), MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(work)), "sponza.hit-cells.work"));
        hitCellCount = cells;
        hitCellsBuild = new HitCellsBuild { Path = cachePath, Bytes = bytes, Work = work.ToArray(), Clock = clock, LayoutMs = clock.Elapsed.TotalMilliseconds };
        ProgressBegin(HitCellsProgress, hitCellItems);
    }

    // Dispatches until budgetMs is spent; true once the build is done (and logged, and written).
    private bool StepHitCells(double budgetMs)
    {
        if (hitCellsBuild is not { } b) return true;
        // A dispatch at most a quarter-million work items (of up to 256 cells each): Sponza's whole build in one ran past
        // the GPU's watchdog once a cell's corners were read too, and the device was lost (2026-10-19). Stepped, a
        // sixteenth of that, so a frame's budget is met closely.
        var perDispatch = double.IsPositiveInfinity(budgetMs) ? 4096 * 64 : 4096 * 4;
        var step = Stopwatch.StartNew();
        while (b.First < hitCellItems && step.Elapsed.TotalMilliseconds < budgetMs)
        {
            var n = Math.Min(perDispatch, hitCellItems - b.First);
            device.DispatchNow(new[] { HitCellsCommand(n, b.First) }, "hit-cells-build");
            b.First += n;
            ProgressAdd(HitCellsProgress, n);
        }
        if (b.First < hitCellItems) return false;
        ProgressEnd(HitCellsProgress);
        hitCellsBuild = null;
        hitCellsSun = hitCellsSunFor = (new Vector3(float.NaN), -1);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] hit cells: {hitCellCount:N0} cells over {hitCellRows:N0} triangle rows at {hitCellSize * 100:0} cm ({b.Bytes / 1048576.0:0} MB), laid out in {b.LayoutMs:0} ms, their patches in {b.Clock.Elapsed.TotalMilliseconds - b.LayoutMs:0} ms over frames."));
        if (b.Path is not null) WriteHitCells(b.Path, b.Bytes, b.Work);
        return true;
    }

    private const int HitCellsVersion = 2;   // 2: a cell whose centre finds no texel takes a corner's patch (hit_cells.comp cellOwnPatch)

    // The cells' file in the GI bundle: keyed by the transport's stages (the geometry, the patches, the texels), the rows,
    // the cell size and the build's marks.
    private string? HitCellsPath(int rows)
    {
        if (transportCombinedKey is not { } combined) return null;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{combined};rows {rows};records {(lightmapTexels ? LightmapRecordsRule : "none")};cell {hitCellSize};contacts {cellMarks};v{HitCellsVersion}"))))[..24].ToLowerInvariant();
        return Path.Combine(transportCacheDir, key + ".hit-cells");
    }

    private bool ReadHitCells(string path, int rows)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var r = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 22));
            if (r.ReadString() != "blix-hit-cells" || r.ReadInt32() != HitCellsVersion || r.ReadInt32() != rows) return false;
            var cells = r.ReadInt64();
            var bytes = r.ReadInt32();
            var workCount = r.ReadInt32();
            var cellWords = r.ReadBytes(bytes);
            var work = r.ReadBytes(workCount * 4);
            if (cellWords.Length != bytes || work.Length != workCount * 4) return false;
            hitCellsBuffer = Own(device.CreateGpuBuffer(bytes, cellWords, "sponza.hit-cells"));
            hitCellWork = Own(device.CreateGpuBuffer(Math.Max(16, work.Length), work, "sponza.hit-cells.work"));
            // As a fresh build makes it (BuildHitCells): each cell's outgoing light, either side, per column.
            if (TransferOn && (!livePatchTexels || ListCellLight)) cellLightBuffer = Own(device.CreateGpuBuffer(checked((int)(cells * 16)), name: "sponza.hit-cells.light"));
            hitCellRows = rows;
            hitCellItems = workCount / 2;
            hitCellCount = cells;
            return true;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException)
        {
            Console.WriteLine($"[VulkanSponza] hit cells: {path} could not be read ({e.Message}); building them.");
            return false;
        }
    }

    // Read back once built (the patches' pass done) and written beside the boot in the background.
    private void WriteHitCells(string path, int bytes, uint[] work)
    {
        device.WaitIdle();
        var cellWords = device.ReadGpuBuffer(hitCellsBuffer, 0, bytes);
        var rows = hitCellRows;
        var cells = hitCellCount;
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".partial";
                using (var w = new BinaryWriter(new BufferedStream(File.Create(temp), 1 << 22)))
                {
                    w.Write("blix-hit-cells"); w.Write(HitCellsVersion); w.Write(rows); w.Write(cells); w.Write(bytes); w.Write(work.Length);
                    w.Write(cellWords);
                    w.Write(MemoryMarshal.AsBytes(work.AsSpan()));
                }
                File.Move(temp, path, overwrite: true);
                Console.WriteLine($"[VulkanSponza] hit cells: written to {path}.");
            }
            catch (IOException e) { Console.WriteLine($"[VulkanSponza] hit cells: {path} not written ({e.Message})."); }
        });
    }

    // rows, firstRow: the work items (hit_cells.comp: a row's cells, 256 an item).
    private DispatchCommand HitCellsCommand(int rows, int firstRow, int mode = 0)
    {
        var blocks = rayBlockBuffers;
        GpuBufferHandle Block(string name) => blocks.First(b => b.Name == name).Buffer;
        var declared = hitCellsInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        return new DispatchCommand(hitCellsPipeline, (rows + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uRows", new Vector4Uniform(new Vector4(rows, firstRow, mode, cellLightBuffer.Equals(default(GpuBufferHandle)) ? 0f : ListCellLight ? 2f : 1f))),
                new("uColumns", new Vector4Uniform(new Vector4(sunNodeKinds.Length, Math.Max(0, Array.IndexOf(sunNodeKinds, 2)), Math.Max(0, liveSunColumn), ListCellLight && HitBlendNow ? transportSpacing : 0f))),
                new("uIrr", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
                new("uToSun", new Vector4Uniform(new Vector4(liveSunToward, liveSunColumn))),
                new("uSunBits", new Vector4Uniform(new Vector4(liveSunWords, sunNodeUnknownSun, ListCellLight ? ListCellPoints : 0, 0f))),
                new("uTexelGrid", new Vector4Uniform(texelGrid)),
                new("uShade", new Vector4Uniform(new Vector4(0f, 0f, sunNodeHashSlots, sunNodeFoliageSlots))),
                new("uLookup", new Vector4Uniform(new Vector4(sunNodePlane, sunNodeSigma, cellMarks ? 1 : 0, 0f))),
                new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                new("uCookedDims", new Vector4Uniform(cookedDims)),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RayTriangles", Block("BlixRayTriangles")),
                new ShaderBufferBinding("RayPositions", Block("BlixRayPositions")),
                new ShaderBufferBinding("RayOwners", Block("BlixRayOwners")),
                new ShaderBufferBinding("HitCells", hitCellsBuffer),
                new ShaderBufferBinding("HitCellWork", hitCellWork),
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("SunNodeHash", sunNodeHash),
                new ShaderBufferBinding("TexelPatch", sunNodeTexelPatch),
                new ShaderBufferBinding("FoliagePoints", sunNodeFoliagePoints),
                new ShaderBufferBinding("FoliageHash", sunNodeFoliageHash),
                new ShaderBufferBinding("SunNodeBits", liveSunBits),
                new ShaderBufferBinding("FoliageBits", sunNodeFoliageBits),
                new ShaderBufferBinding("SunNodePatchLight", sunNodePatchLight),
                new ShaderBufferBinding("RaySurfaces", Block("BlixRaySurfaces")),
                new ShaderBufferBinding("CellLight", cellLightBuffer.Equals(default(GpuBufferHandle)) ? hitCellsBuffer : cellLightBuffer),
                new ShaderBufferBinding("PatchNeighbours", ListCellLight && HitBlendNow ? FarNeighboursBuffer : hitCellsBuffer),
            }.Concat(CookedBuffers()).Where(b => declared.Contains(b.Name)).ToArray());
    }
}
