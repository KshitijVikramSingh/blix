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
    // --cell-near D (0: off): hits nearer than D take the search's blend, not their cell
    // (texel_nodes_shade.glsl), in both rounds -- one patch a cell side read the creases' light across them (crevices'
    // seams 0.83 -> 0.87, thinwall's leak 0.0017 -> 0.0002); the fine round's alone was worse (0.78). Sponza's near hits
    // are many and one in a workgroup holds the rest to the search: shade 10 -> 27 ms a 1 M rays (2026-10-19).
    private float hitCellNear = 0f;
    // The cells a sun edge crosses (their corners' sun over half apart, at each light change: 1.5% of Sponza's) take their
    // hits' sun from the search at the hit, their patch still the cell's (hit_cells.comp, texel_nodes_shade.glsl): what
    // the creases need, as BLIX_CELL_SPLIT read it (seams 0.87 against the cell's 0.83) -- read at one jittered frame.
    // Averaged over 64 (the gate since), 0.835 against 0.833: not worth the cells' sun pass's 2 s a light change and the
    // shade's 10 -> 19.6 ms a 1 M rays (2026-10-19). --sun-edges: on.
    private bool sunEdges;
    // --cell-marks: the cells a contact (the surface turning over 30 degrees between its centre and a corner) or a sun edge
    // crosses marked, their hits the search's (hit_cells.comp). Even so: Sponza's cells 24% marked and a workgroup's one
    // marked hit holding the rest to the search, shade 38 ms a 1 M rays, and the cell build 0.65 -> 6.4 s (2026-10-19).
    private bool cellMarks;
    // BLIX_CELL_SPLIT=1|2 (a measurement): the cells answer every hit, but 1 takes the sun, 2 the patch from the search at
    // the hit (texel_nodes_shade.glsl).
    private static readonly int cellSplit = int.TryParse(Environment.GetEnvironmentVariable("BLIX_CELL_SPLIT"), out var cs) ? cs : 0;
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

    // The cells' sun for the cook's (mode 1), before its trace: the texels' sun bits up for it (liveSunDone).
    private void HitCellsSunNow()
    {
        using var profScope = Prof("cells-sun");
        if (!HitCellsReady || !liveSunDone || liveSunColumn < 0 || hitCellsSun == (liveSunToward, liveSunColumn)) return;
        var clock = Stopwatch.StartNew();
        // A dispatch a quarter-million work items (of up to 256 cells each): Sponza's whole build in one ran past the GPU's
        // watchdog once a cell's corners were read too, and the device was lost (2026-10-19).
        const int perDispatch = 4096 * 64;
        for (var first = 0; first < hitCellItems; first += perDispatch)
            device.DispatchNow(new[] { HitCellsCommand(Math.Min(perDispatch, hitCellItems - first), first, 1) }, "hit-cells-sun");
        hitCellsSun = (liveSunToward, liveSunColumn);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] hit cells: the sun of {hitCellCount:N0} cells from the texels' bits in {clock.Elapsed.TotalSeconds:0.00} s{CellMarks()}."));
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

    private bool HitCellsReady => liveTexels && hitCellsOn && !hitCellsBuffer.Equals(default(GpuBufferHandle));

    private void DeclareHitCells(Func<string[], ShaderInterface> reflect)
    {
        hitCellsInterface = reflect(new[] { "hit_cells.comp" });
    }

    private void CreateHitCellsPipeline(string shaderDir) =>
        hitCellsPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "hit_cells.comp.spv")), hitCellsInterface, "hit_cells")), "hit_cells"));

    // Once the cook's setup is done (MergeDiscovered): the layout from the packed rows, then each cell's patches.
    private void BuildHitCells()
    {
        if (!liveTexels || !hitCellsOn || !hitCellsBuffer.Equals(default(GpuBufferHandle)) || rayGpuData is not { } data) return;
        var clock = Stopwatch.StartNew();
        var rows = data.Triangles.Length / 4;
        // The geometry's: read from the GI bundle when built before for the same stages and settings (~10 s of Sponza's
        // boot, 2026-10-19).
        var cachePath = TransferOn && !livePatchTexels || Environment.GetEnvironmentVariable("BLIX_NO_CELLS_CACHE") == "1" ? null : HitCellsPath(rows);   // A' adds its cells' light: built
        if (cachePath is not null && ReadHitCells(cachePath, rows))
        {
            hitCellsSun = (new Vector3(float.NaN), -1);
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
        if (TransferOn && !livePatchTexels) cellLightBuffer = Own(device.CreateGpuBuffer(checked((int)(cells * 16)), name: "sponza.hit-cells.light"));
        hitCellRows = rows;
        hitCellItems = work.Count / 2;
        hitCellWork = Own(device.CreateGpuBuffer(Math.Max(16, work.Count * 4), MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(work)), "sponza.hit-cells.work"));
        hitCellCount = cells;
        var layoutMs = clock.Elapsed.TotalMilliseconds;
        // A dispatch a quarter-million work items (of up to 256 cells each): Sponza's whole build in one ran past the GPU's
        // watchdog once a cell's corners were read too, and the device was lost (2026-10-19).
        const int perDispatch = 4096 * 64;
        for (var first = 0; first < hitCellItems; first += perDispatch)
            device.DispatchNow(new[] { HitCellsCommand(Math.Min(perDispatch, hitCellItems - first), first) }, "hit-cells-build");
        hitCellsSun = (new Vector3(float.NaN), -1);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] hit cells: {cells:N0} cells over {rows:N0} triangle rows at {cellSize * 100:0} cm ({bytes / 1048576.0:0} MB), laid out in {layoutMs:0} ms, their patches in {clock.Elapsed.TotalMilliseconds - layoutMs:0} ms."));
        if (cachePath is not null) WriteHitCells(cachePath, bytes, work.ToArray());
    }

    private const int HitCellsVersion = 1;

    // The cells' file in the GI bundle: keyed by the transport's stages (the geometry, the patches, the texels), the rows,
    // the cell size and the build's marks.
    private string? HitCellsPath(int rows)
    {
        if (transportCombinedKey is not { } combined) return null;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{combined};rows {rows};cell {hitCellSize};contacts {cellMarks};v{HitCellsVersion}"))))[..24].ToLowerInvariant();
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
                new("uRows", new Vector4Uniform(new Vector4(rows, firstRow, mode, cellLightBuffer.Equals(default(GpuBufferHandle)) ? 0f : 1f))),
                new("uColumns", new Vector4Uniform(new Vector4(sunNodeKinds.Length, Math.Max(0, Array.IndexOf(sunNodeKinds, 2)), Math.Max(0, liveSunColumn), 0f))),
                new("uIrr", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
                new("uToSun", new Vector4Uniform(new Vector4(liveSunToward, liveSunColumn))),
                new("uSunBits", new Vector4Uniform(new Vector4(liveSunWords, sunNodeUnknownSun, 0f, 0f))),
                new("uTexelGrid", new Vector4Uniform(texelGrid)),
                new("uShade", new Vector4Uniform(new Vector4(0f, 0f, sunNodeHashSlots, sunNodeFoliageSlots))),
                new("uLookup", new Vector4Uniform(new Vector4(sunNodePlane, sunNodeSigma, (cellMarks ? 1 : 0) | (sunEdges ? 2 : 0), 0f))),
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
            }.Concat(CookedBuffers()).Where(b => declared.Contains(b.Name)).ToArray());
    }
}
