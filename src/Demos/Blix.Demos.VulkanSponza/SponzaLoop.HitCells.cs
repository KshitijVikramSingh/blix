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
        if (!HitCellsReady || !liveSunDone || liveSunColumn < 0 || hitCellsSun == (liveSunToward, liveSunColumn)) return;
        var clock = Stopwatch.StartNew();
        const int perDispatch = 65535 * 64;
        for (var first = 0; first < hitCellItems; first += perDispatch)
            device.DispatchNow(new[] { HitCellsCommand(Math.Min(perDispatch, hitCellItems - first), first, 1) }, "hit-cells-sun");
        hitCellsSun = (liveSunToward, liveSunColumn);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] hit cells: the sun of {hitCellCount:N0} cells from the texels' bits in {clock.Elapsed.TotalSeconds:0.00} s."));
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
        const int perDispatch = 65535 * 64;
        for (var first = 0; first < hitCellItems; first += perDispatch)
            device.DispatchNow(new[] { HitCellsCommand(Math.Min(perDispatch, hitCellItems - first), first) }, "hit-cells-build");
        hitCellsSun = (new Vector3(float.NaN), -1);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] hit cells: {cells:N0} cells over {rows:N0} triangle rows at {cellSize * 100:0} cm ({bytes / 1048576.0:0} MB), laid out in {layoutMs:0} ms, their patches in {clock.Elapsed.TotalMilliseconds - layoutMs:0} ms."));
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
                new("uLookup", new Vector4Uniform(new Vector4(sunNodePlane, sunNodeSigma, 0f, 0f))),
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
