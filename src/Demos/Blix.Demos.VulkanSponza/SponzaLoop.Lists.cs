using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// Light-free lists (a prototype; tools/gi-model/light_lists.py and light_lists_change.py the model): a light change lights
// the texels from lists cooked once, with no ray traced.
//   --list-cook  the gather (sweep, refine, the fine round; not the near rounds, which re-shade the same rays) records every
//                ray light-free (texel_nodes_shade.glsl, the capture's x 2): its texel, what it reached -- a sky bin, a near
//                hit's texel, a far hit's cell side -- and its weight. Folded per texel here, read back pass by pass, and
//                kept as a stage of the GI bundle ("texel-lists") once the cook lands.
//   --lists      when the stage is there, the gather is not run: the coarse texels' lists summed (texel_lists.comp,
//                --list-iterations times, each from the last's light), the coarse filter and the fine copy as the gather's
//                end has them, then the fine texels' lists, and the cook lands.
// The hit's sun is its texel's sun bit (near) or its cell's (far, hit_cells.comp's CellLight); rays the lists cannot keep
// (among no texel, the foliage's, the far record's) are the remainder: each texel's light scaled by its whole over what
// its lists kept, at the cook's light.
internal sealed partial class SponzaLoop
{
    private bool listCook, listsOn, listDebug;
    private GpuBufferHandle listDebugBuffer;
    private int listIterations = 3;
    // BLIX_LIST_SKY_EXACT=1 (a test): a sky bin's entry the cook's own sky light along its rays, not their mean direction.
    private static readonly bool listSkyExact = Environment.GetEnvironmentVariable("BLIX_LIST_SKY_EXACT") == "1";
    private const string ListsHeader = "texel lists v5";

    private sealed class ListAcc
    {
        public readonly Dictionary<ulong, Vector4> Entries = new();
        public int Rays;
        public double Whole, Kept;
        public readonly double[] KindWhole = new double[5];   // the gather's light by what each ray reached (--list-debug)
        public bool Fine;
    }
    private Dictionary<uint, ListAcc>? listAcc;

    // The stored lists, up on the GPU: coarse first [0, listCoarse), then fine.
    private GpuBufferHandle listTexelsBuffer, listStartBuffer, listEntriesBuffer, listNormBuffer, listPrevSky, listPrevSun;
    private int listCoarse, listCount;
    private bool listsLoaded;
    private ShaderInterface listsInterface = null!;
    private PipelineHandle listsPipeline;

    private void ReadListArgs(AppArgs args)
    {
        listCook = args.Flag("list-cook");
        listsOn = args.Flag("lists");
        listDebug = args.Flag("list-debug");
        if (args.Int("list-iterations") is { } it) listIterations = Math.Max(1, it);
        if (listCook)
        {
            // Every ray of every texel, a pass's at a time: the capture's buffer is the recording's.
            if (rayCaptureBits < 0) rayCaptureBits = 0;
            rayCaptureRoom = Math.Max(rayCaptureRoom, sunNodeSlice * 32 * 6);   // a near hit's candidates, a cell hit's patches: a record each
            listAcc = new Dictionary<uint, ListAcc>();
            Console.WriteLine("[VulkanSponza] lists: --list-cook -- the gather's rays recorded light-free into each texel's list.");
        }
    }

    private void DeclareLists(Func<string[], ShaderInterface> reflect) => listsInterface = reflect(new[] { "texel_lists.comp" });

    private void CreateListsPipeline(string shaderDir)
    {
        listsPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_lists.comp.spv")), listsInterface, "texel_lists")), "texel_lists"));
    }

    // Recording now: the cook's gather (not a near round's re-shade, not a light change's tier).
    private bool ListRecording => listCook && listAcc is not null && splitCooking && !(fineGathering && NearRoundIndex > 0);

    // After a pass: its records folded into the texels' lists, the capture's count reset.
    private void ListCookRead()
    {
        if (!ListRecording || rayCaptureBuffer.Equals(default(GpuBufferHandle))) return;
        device.WaitIdle();
        var met = BitConverter.ToUInt32(device.ReadGpuBuffer(rayCaptureBuffer, 0, 4));
        if (met > (uint)rayCaptureRoom) Console.WriteLine($"[VulkanSponza] lists: WARNING a pass's {met:N0} records past the room's {rayCaptureRoom:N0}: some lost.");
        var count = (int)Math.Min(met, (uint)rayCaptureRoom);
        if (count == 0) return;
        var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rayCaptureBuffer, 16, count * 32).AsSpan());
        var fine = fineGathering;
        for (var i = 0; i < count; i++)
        {
            var w = words.Slice(i * 8, 8);
            var id = w[0];
            var kind = w[1] & 7u;
            var target = w[2];
            var rg = UnpackHalf2(w[3]);
            var bw = UnpackHalf2(w[4]);
            var whole = BitConverter.UInt32BitsToSingle(w[5]);
            if (!listAcc!.TryGetValue(id, out var acc)) listAcc[id] = acc = new ListAcc { Fine = fine };
            var first = (w[7] & 256u) == 0u;   // a near hit's further candidates (texel_nodes_shade.glsl): not another ray
            if (first) { acc.Rays++; acc.Whole += whole; acc.KindWhole[kind] += whole; }
            if (kind == 3u) continue;
            if (first) acc.Kept += whole;
            var key = ((ulong)kind << 32) | target;
            var add = kind == 1u ? new Vector4(rg.X, rg.Y, bw.X, 0f) : new Vector4(rg.X, rg.Y, bw.X, bw.Y);   // a cell's: its rays' albedos too
            acc.Entries[key] = acc.Entries.TryGetValue(key, out var v) ? v + add : add;
        }
        device.WriteGpuBuffer(rayCaptureBuffer, 0, new byte[16]);
    }

    private static Vector2 UnpackHalf2(uint packed) =>
        new((float)BitConverter.UInt16BitsToHalf((ushort)(packed & 0xFFFFu)), (float)BitConverter.UInt16BitsToHalf((ushort)(packed >> 16)));

    private static uint PackHalf2(float a, float b) =>
        BitConverter.HalfToUInt16Bits((Half)a) | ((uint)BitConverter.HalfToUInt16Bits((Half)b) << 16);

    // The cook landed: the lists built and stored.
    private void SaveLists()
    {
        if (listAcc is not { Count: > 0 } acc || lightBakeKey is not { } key) return;
        var clock = Stopwatch.StartNew();
        var order = acc.Keys.Where(id => !acc[id].Fine).OrderBy(id => id).Concat(acc.Keys.Where(id => acc[id].Fine).OrderBy(id => id)).ToArray();
        var coarse = acc.Values.Count(a => !a.Fine);
        var start = new uint[order.Length + 1];
        var entries = new List<uint>();
        var norm = new float[order.Length * 2];
        long kinds0 = 0, kinds1 = 0, kinds2 = 0, kinds4 = 0;
        for (var i = 0; i < order.Length; i++)
        {
            var a = acc[order[i]];
            start[i] = (uint)(entries.Count / 4);
            foreach (var (k, v) in a.Entries.OrderBy(e => e.Key))
            {
                var kind = (uint)(k >> 32);
                if (kind == 0u) kinds0++; else if (kind == 1u) kinds1++; else if (kind == 4u) kinds4++; else kinds2++;
                entries.Add(kind | (kind == 0u && listSkyExact ? 256u : 0u)); entries.Add((uint)(k & 0xFFFFFFFFu)); entries.Add(PackHalf2(v.X, v.Y)); entries.Add(PackHalf2(v.Z, v.W));
            }
            norm[2 * i] = 1f / Math.Max(1, a.Rays);
            norm[2 * i + 1] = a.Kept > 1e-12 ? (float)(a.Whole / a.Kept) : 1f;
        }
        start[order.Length] = (uint)(entries.Count / 4);
        var ent = entries.ToArray();
        var kw = new float[order.Length * 4];
        for (var i = 0; i < order.Length; i++)
            for (var k = 0; k < 4; k++) kw[4 * i + k] = (float)(acc[order[i]].KindWhole[k] / Math.Max(1, acc[order[i]].Rays));
        SaveStage(key, "texel-lists-debug", w => WriteArray(w, kw));
        SaveStage(key, "texel-lists", w =>
        {
            w.Write(ListsHeader); w.Write(texelCount); w.Write(coarse);
            WriteArray(w, order); WriteArray(w, start); WriteArray(w, ent); WriteArray(w, norm);
        });
        var n = Math.Max(1, order.Length);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] lists: {order.Length:N0} texels ({coarse:N0} coarse) cooked into {ent.Length / 4:N0} entries ({ent.Length / 4.0 / n:0.0} a texel: sky bins {kinds0 / (double)n:0.0}, texels {kinds1 / (double)n:0.0}, cells {kinds2 / (double)n:0.0}, patches {kinds4 / (double)n:0.0}); {ent.Length * 4L / 1048576.0:0.0} MB at 16 B an entry; remainder scale median {norm.Where((_, i) => i % 2 == 1).OrderBy(x => x).ElementAt(n / 2):0.000}; built in {clock.Elapsed.TotalSeconds:0.0} s."));
        listAcc = null;
    }

    // --lists: the stored lists up, once.
    private bool ListsReady()
    {
        if (!listsOn || listCook) return false;
        if (listsLoaded) return listCount > 0;
        listsLoaded = true;
        if (lightBakeKey is not { } key) return false;
        uint[]? order = null, start = null, ent = null; float[]? norm = null; var coarse = 0;
        var ok = LoadStage(key, "texel-lists", r =>
        {
            if (r.ReadString() != ListsHeader || r.ReadInt32() != texelCount) return (object?)null;
            coarse = r.ReadInt32();
            order = ReadArray<uint>(r); start = ReadArray<uint>(r); ent = ReadArray<uint>(r); norm = ReadArray<float>(r);
            return new object();
        }) is not null;
        if (!ok || order is null) { Console.WriteLine("[VulkanSponza] lists: --lists, but no stored lists for this scene and cook (--list-cook first): the gather runs."); return false; }
        listCoarse = coarse; listCount = order.Length;
        listTexelsBuffer = Own(device.CreateGpuBuffer(Math.Max(16, order.Length * 4), MemoryMarshal.AsBytes(order.AsSpan()), "sponza.lists.texels"));
        listStartBuffer = Own(device.CreateGpuBuffer(Math.Max(16, start!.Length * 4), MemoryMarshal.AsBytes(start.AsSpan()), "sponza.lists.start"));
        listEntriesBuffer = Own(device.CreateGpuBuffer(Math.Max(16, ent!.Length * 4), MemoryMarshal.AsBytes(ent.AsSpan()), "sponza.lists.entries"));
        listNormBuffer = Own(device.CreateGpuBuffer(Math.Max(16, norm!.Length * 4), MemoryMarshal.AsBytes(norm.AsSpan()), "sponza.lists.norm"));
        listPrevSky = Own(device.CreateGpuBuffer(texelCount * 8, name: "sponza.lists.prev-sky"));
        listPrevSun = Own(device.CreateGpuBuffer(texelCount * 8, name: "sponza.lists.prev-sun"));
        if (listDebug) listDebugBuffer = Own(device.CreateGpuBuffer(order.Length * 16, name: "sponza.lists.debug"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] lists: {listCount:N0} texels' lists up ({coarse:N0} coarse), {ent.Length / 4:N0} entries, {ent.Length * 4L / 1048576.0:0.0} MB: a light change sums them, no gather."));
        return true;
    }

    // In place of the gather: the coarse lists, the coarse filter and the fine copy, the fine lists; then the cook lands.
    private void RunListsNow()
    {
        var clock = Stopwatch.StartNew();
        var times = new List<string>();
        // The cells' light again, now the live patch light has rested: the setup's (hit_cells.comp mode 1, cells-sun) ran
        // before its last bounces, and left the cells' bounce 1% low (--list-debug, 2026-10-22).
        var cellsClock = Stopwatch.StartNew();
        for (var first = 0; first < hitCellItems; first += 1 << 16)
            device.DispatchNow(new[] { HitCellsCommand(Math.Min(1 << 16, hitCellItems - first), first, 1) }, "hit-cells-sun");
        device.WaitIdle();
        times.Add(string.Create(CultureInfo.InvariantCulture, $"cells {cellsClock.Elapsed.TotalMilliseconds:0} ms"));
        foreach (var (nm, h) in new[] { ("sun bits", sunNodeBits), ("cell light", cellLightBuffer), ("texels", texelBuffer) })
            if (h.Equals(default(GpuBufferHandle))) Console.WriteLine($"[VulkanSponza] lists: WARNING no {nm} buffer.");
        void Sum(int first, int count, string what)
        {
            if (count <= 0) return;
            for (var it = 0; it < listIterations; it++)
            {
                var t = Stopwatch.StartNew();
                // The last iteration's light, read as it is (the dispatch writes the parts).
                device.WaitIdle();
                device.WriteGpuBuffer(listPrevSky, 0, device.ReadGpuBuffer(EnsureLivePart(false), 0, texelCount * 8));
                device.WriteGpuBuffer(listPrevSun, 0, device.ReadGpuBuffer(EnsureLivePart(true), 0, texelCount * 8));
                var copy = t.Elapsed.TotalMilliseconds;
                var declared = listsInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
                var toSun = -Vector3.Normalize(sunDirection);
                var irr = EffectiveSunIrradiance;
                device.DispatchNow(new[] { new DispatchCommand(listsPipeline, (count + 63) / 64, 1, 1,
                    new ShaderUniform[]
                    {
                        new("uLists", new Vector4Uniform(new Vector4(count, first, 1f, listDebug ? -1f : 1f))),
                        new("uToSun", new Vector4Uniform(new Vector4(toSun, SunColumn()))),
                        new("uIrr", new Vector4Uniform(new Vector4(irr, (sunNodeSuns!.Length + 31) / 32))),
                        new("uColumns", new Vector4Uniform(new Vector4(sunNodeKinds.Length, Math.Max(0, Array.IndexOf(sunNodeKinds, 2)), SunColumn(), 0f))),
                    },
                    new[] { new ShaderTextureBinding("uSkyRadiance", envCubeTexture) },
                    Buffers: new[]
                    {
                        new ShaderBufferBinding("ListTexels", listTexelsBuffer),
                        new ShaderBufferBinding("ListStart", listStartBuffer),
                        new ShaderBufferBinding("ListEntries", listEntriesBuffer),
                        new ShaderBufferBinding("ListNorm", listNormBuffer),
                        new ShaderBufferBinding("Texels", texelBuffer),
                        new ShaderBufferBinding("SunNodeBits", sunNodeBits),
                        new ShaderBufferBinding("CellLight", cellLightBuffer),
                        new ShaderBufferBinding("SunNodePatchLight", sunNodePatchLight),
                        new ShaderBufferBinding("PrevSky", listPrevSky),
                        new ShaderBufferBinding("PrevSun", listPrevSun),
                        new ShaderBufferBinding("SplitSky", EnsureLivePart(false)),
                        new ShaderBufferBinding("SplitSun", EnsureLivePart(true)),
                        ProvenanceBinding(),
                        new ShaderBufferBinding("ListDebug", listDebugBuffer.Equals(default(GpuBufferHandle)) ? listNormBuffer : listDebugBuffer),
                    }.Where(b => declared.Contains(b.Name)).ToArray()) }, "texel-lists");
                device.WaitIdle();
                times.Add(string.Create(CultureInfo.InvariantCulture, $"{what} {it + 1}: {t.Elapsed.TotalMilliseconds - copy:0.0} ms (+{copy:0} copying)"));
            }
        }
        Sum(0, listCoarse, "coarse");
        FilterCoarseNow();
        ApplyFineNow();
        Sum(listCoarse, listCount - listCoarse, "fine");
        if (listDebug) ListDebugReport();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] lists: the light summed from {listCount:N0} texels' lists in {clock.Elapsed.TotalMilliseconds:0} ms, no ray traced ({string.Join("; ", times)})."));
        sunNodeStage = SunNodeStage.Done;
        ResetSunNodeRefine();
        if (splitCooking) FinishSplitCook();
    }

    // --list-debug: the lists' light by kind against the gather's at the cook (the same sun: what each kind's stand-in costs).
    private void ListDebugReport()
    {
        if (lightBakeKey is not { } key || LoadStage(key, "texel-lists-debug", r => ReadArray<float>(r)) is not { } cook) return;
        device.WaitIdle();
        var now = MemoryMarshal.Cast<byte, float>(device.ReadGpuBuffer(listDebugBuffer, 0, listCount * 16).AsSpan()).ToArray();
        // BLIX_LIST_DEBUG_OUT=path: per list its texel id, then the cook's four kinds and the lists' (9 words a list).
        if (Environment.GetEnvironmentVariable("BLIX_LIST_DEBUG_OUT") is { Length: > 0 } outPath)
        {
            var ids = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(listTexelsBuffer, 0, listCount * 4).AsSpan());
            var rows = new float[listCount * 9];
            for (var i = 0; i < listCount; i++)
            {
                rows[9 * i] = BitConverter.UInt32BitsToSingle(ids[i]);
                for (var k = 0; k < 4; k++) { rows[9 * i + 1 + k] = cook[4 * i + k]; rows[9 * i + 5 + k] = now[4 * i + k]; }
            }
            File.WriteAllBytes(outPath, MemoryMarshal.AsBytes(rows.AsSpan()).ToArray());
        }
        foreach (var (name, lo, hi) in new[] { ("coarse", 0, listCoarse), ("fine", listCoarse, listCount) })
        {
            var parts = new List<string>();
            foreach (var (k, label) in new[] { (0, "sky"), (1, "texels"), (2, "cells"), (3, "remainder") })
            {
                double c = 0, l = 0;
                for (var i = lo; i < hi; i++) { c += cook[4 * i + k]; l += now[4 * i + k]; }
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{label} {l / Math.Max(1e-12, c) - 1:+0.00%;-0.00%} ({c / Math.Max(1, hi - lo):0.000} a texel at the cook)"));
            }
            Console.WriteLine($"[VulkanSponza] lists, --list-debug, {name}: the lists' light against the gather's by kind: " + string.Join("; ", parts));
        }
    }
}
