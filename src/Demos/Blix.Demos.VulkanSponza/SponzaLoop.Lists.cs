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
// The hit's sun is its texel's sun bit (near) or its cell's (far, hit_cells.comp's CellLight, with the cell's bounce: its
// patch blend averaged over its area); rays the lists cannot keep (among no texel, the foliage's, the far record's) are
// the remainder: each texel's light scaled by its whole over what its lists kept, at the cook's light. Packed a word an
// entry (PackLists; tools/gi-model/list_packing.py): 171 entries a texel on the crevices corridor, 0.7 KB.
internal sealed partial class SponzaLoop
{
    private bool listCook, listCookAppend, listsOn, listDebug;
    private GpuBufferHandle listDebugBuffer;
    private int listIterations = 3;
    // A cell side's patch blend, averaged over this many points a side of it (hit_cells.comp; 4: 0.0168 on the corridor,
    // the gather's).
    private const int ListCellPoints = 4;
    // The cells' light in the lists' form (albedo-free, the blend over the area, the sun): the lists' and the lean shade's.
    private bool ListCellLight => listsOn || leanShade;
    // v8: the search finds a texel a plane within a key (texel_nodes_prep.comp; --no-node-planes keeps v7).
    private string ListsHeader => (nodePlanes ? "texel lists v9" : "texel lists v7") + GatherLiftKey;

    private sealed class ListAcc
    {
        public readonly Dictionary<ulong, Vector3> Entries = new();
        public int Rays;
        public double Whole, Kept;
        public readonly double[] KindWhole = new double[4];   // the gather's light by what each ray reached (--list-debug)
        public bool Fine;
    }
    private Dictionary<uint, ListAcc>? listAcc;

    // The stored lists, up on the GPU: coarse first [0, listCoarse), then fine; the luminance's log2 range they pack over.
    private GpuBufferHandle listTexelsBuffer, listStartBuffer, listWordsBuffer, listNormBuffer, listPrevSky, listPrevSun;
    private Vector2 listPackRange;
    private int listCoarse, listCount;
    private bool listsLoaded;
    private ShaderInterface listsInterface = null!;
    private PipelineHandle listsPipeline;

    private void ReadListArgs(AppArgs args)
    {
        // --list-cook-append: the cook's rays added to the stored lists (another sun's guides: a ray carries its own pdf's
        // weight, so rays guided by several suns sum to one estimate -- a crease's lists cooked under one sun read +1% and
        // half again as noisy under another, the guides aimed elsewhere, 2026-10-23).
        // Twice the rays: a second cook appended with another seed (BLIX_GATHER_SEED): the corridor's seams 0.034 against
        // 0.052 at the cook's sun. One cook of 1,024 sweep rays instead did worse (0.039, its corridor 0.024 against 0.018;
        // not yet understood, 2026-10-23).
        listCookAppend = args.Flag("list-cook-append");
        listCook = args.Flag("list-cook") || listCookAppend;
        listsOn = args.Flag("lists");
        listDebug = args.Flag("list-debug");
        if (args.Int("list-iterations") is { } it) listIterations = Math.Max(1, it);
        if (listCook)
        {
            // Every ray of every texel, a pass's at a time: the capture's buffer is the recording's.
            if (rayCaptureBits < 0) rayCaptureBits = 0;
            rayCaptureRoom = Math.Max(rayCaptureRoom, sunNodeSlice * 32 * 4);   // a near hit's candidates take a record each
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
            var kind = w[1] & 3u;
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
            var add = new Vector3(rg.X, rg.Y, bw.X);
            acc.Entries[key] = acc.Entries.TryGetValue(key, out var v) ? v + add : add;
        }
        device.WriteGpuBuffer(rayCaptureBuffer, 0, new byte[16]);
    }

    private static Vector2 UnpackHalf2(uint packed) =>
        new((float)BitConverter.UInt16BitsToHalf((ushort)(packed & 0xFFFFu)), (float)BitConverter.UInt16BitsToHalf((ushort)(packed >> 16)));

    private static uint PackHalf2(float a, float b) =>
        BitConverter.HalfToUInt16Bits((Half)a) | ((uint)BitConverter.HalfToUInt16Bits((Half)b) << 16);

    // The cook landed: the lists built, packed and stored.
    private void SaveLists()
    {
        if (listAcc is not { Count: > 0 } acc || lightBakeKey is not { } key) return;
        var clock = Stopwatch.StartNew();
        var appended = listCookAppend ? AppendStoredLists(key, acc) : 0;
        var order = acc.Keys.Where(id => !acc[id].Fine).OrderBy(id => id).Concat(acc.Keys.Where(id => acc[id].Fine).OrderBy(id => id)).ToArray();
        var coarse = acc.Values.Count(a => !a.Fine);
        var norm = new float[order.Length * 2];
        for (var i = 0; i < order.Length; i++)
        {
            var a = acc[order[i]];
            norm[2 * i] = 1f / Math.Max(1, a.Rays);
            norm[2 * i + 1] = a.Kept > 1e-12 ? (float)(a.Whole / a.Kept) : 1f;
        }
        var (start, words, range, counts, escapes) = PackLists(order.Select(id => acc[id].Entries).ToArray());
        var sums = new float[order.Length * 3];   // its rays, its whole and kept light at the cook (an append sums them)
        for (var i = 0; i < order.Length; i++) { var a = acc[order[i]]; sums[3 * i] = a.Rays; sums[3 * i + 1] = (float)a.Whole; sums[3 * i + 2] = (float)a.Kept; }
        var kw = new float[order.Length * 4];
        for (var i = 0; i < order.Length; i++)
            for (var k = 0; k < 4; k++) kw[4 * i + k] = (float)(acc[order[i]].KindWhole[k] / Math.Max(1, acc[order[i]].Rays));
        SaveStage(key, "texel-lists-debug", w => WriteArray(w, kw));
        SaveStage(key, "texel-lists", w =>
        {
            w.Write(ListsHeader); w.Write(texelCount); w.Write(coarse); w.Write(range.X); w.Write(range.Y);
            WriteArray(w, order); WriteArray(w, start); WriteArray(w, words); WriteArray(w, norm); WriteArray(w, sums);
        });
        var n = Math.Max(1, order.Length);
        var entries = counts[0] + counts[1] + counts[2];
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] lists: {order.Length:N0} texels ({coarse:N0} coarse) cooked into {entries:N0} entries ({entries / (double)n:0.0} a texel: sky bins {counts[0] / (double)n:0.0}, texels {counts[1] / (double)n:0.0}, cells {counts[2] / (double)n:0.0}); packed {words.Length * 4L / 1048576.0:0.0} MB ({words.Length * 4.0 / n:0} B a texel; {escapes:N0} ids whole, {100.0 * escapes / Math.Max(1, entries):0.0}%); luminance over 2^{range.X:0.0}..2^{range.Y:0.0}; remainder scale median {norm.Where((_, i) => i % 2 == 1).OrderBy(x => x).ElementAt(n / 2):0.000}; built in {clock.Elapsed.TotalSeconds:0.0} s{(appended > 0 ? $"; {appended:N0} stored lists added to (--list-cook-append)" : "")}."));
        listAcc = null;
    }

    // --list-cook-append: the stored lists unpacked into this cook's (rays, light and entries summed). How many there were.
    private int AppendStoredLists(string key, Dictionary<uint, ListAcc> acc)
    {
        var stored = LoadStage(key, "texel-lists", r =>
        {
            if (r.ReadString() != ListsHeader || r.ReadInt32() != texelCount) return null;
            var coarse = r.ReadInt32();
            var range = new Vector2(r.ReadSingle(), r.ReadSingle());
            var order = ReadArray<uint>(r); var start = ReadArray<uint>(r); var words = ReadArray<uint>(r);
            ReadArray<float>(r);
            var sums = ReadArray<float>(r);
            return new StoredLists(coarse, range, order, start, words, sums);
        });
        if (stored is not { } s) { Console.WriteLine("[VulkanSponza] lists: --list-cook-append, but no stored lists of this format: these alone."); return 0; }
        for (var i = 0; i < s.Order.Length; i++)
        {
            if (!acc.TryGetValue(s.Order[i], out var a)) acc[s.Order[i]] = a = new ListAcc { Fine = i >= s.Coarse };
            a.Rays += (int)s.Sums[3 * i]; a.Whole += s.Sums[3 * i + 1]; a.Kept += s.Sums[3 * i + 2];
            var at = (int)s.Start[i];
            var h0 = s.Words[at]; var h1 = s.Words[at + 1];
            at += 2;
            var n = new[] { (int)(h0 & 0xFFFF), (int)(h0 >> 16), (int)h1 };
            for (var k = 0; k < 3; k++)
            {
                uint last = 0;
                for (var e = 0; e < n[k]; e++)
                {
                    var w = s.Words[at++];
                    var step = w >> 22;
                    var t = k == 0 ? step : step == 1023u ? s.Words[at++] : last + step;
                    last = t;
                    var key2 = ((ulong)k << 32) | t;
                    var v = UnpackRgb22(w, s.Range.X, s.Range.Y);
                    a.Entries[key2] = a.Entries.TryGetValue(key2, out var old) ? old + v : v;
                }
            }
        }
        return s.Order.Length;
    }

    private sealed record StoredLists(int Coarse, Vector2 Range, uint[] Order, uint[] Start, uint[] Words, float[] Sums);

    private static Vector3 UnpackRgb22(uint w, float lo, float hi)
    {
        var q = (w >> 10) & 4095u;
        if (q == 0u) return Vector3.Zero;
        var l = MathF.Pow(2f, lo + (q - 1f) * (hi - lo) / 4094f);
        var c = new Vector3(((w >> 5) & 31u) / 31f, (w & 31u) / 31f, 0f);
        c.Z = MathF.Max(1f - c.X - c.Y, 0f);
        return c * (l / MathF.Max(0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z, 1e-6f));
    }

    // The lists packed (texel_lists.comp reads them): per list two header words (the sky bins | the texels << 16; the
    // cells), then its entries kind by kind, each by target: a word an entry, its bin or its id's step from the last of
    // its kind in the top 10 bits (1023: the id whole in the next word), its rgb in the low 22 (PackRgb22).
    private static (uint[] Start, uint[] Words, Vector2 Range, long[] Counts, long Escapes) PackLists(IReadOnlyList<Dictionary<ulong, Vector3>> lists)
    {
        const float lumR = 0.2126f, lumG = 0.7152f, lumB = 0.0722f;
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var l in lists)
            foreach (var v in l.Values)
            {
                var y = lumR * v.X + lumG * v.Y + lumB * v.Z;
                if (y > 0f) { var g = MathF.Log2(y); lo = MathF.Min(lo, g); hi = MathF.Max(hi, g); }
            }
        if (lo > hi) { lo = 0f; hi = 1f; }
        lo = MathF.Max(MathF.Floor(lo), MathF.Ceiling(hi) - 48f);   // 48 stops: a step 1.2% (rounded half that)
        hi = MathF.Max(MathF.Ceiling(hi), lo + 1f);
        var start = new uint[lists.Count + 1];
        var words = new List<uint>();
        var counts = new long[3];
        long escapes = 0;
        for (var i = 0; i < lists.Count; i++)
        {
            start[i] = (uint)words.Count;
            var byKind = new[] { new List<(uint T, Vector3 V)>(), new List<(uint T, Vector3 V)>(), new List<(uint T, Vector3 V)>() };
            foreach (var (k, v) in lists[i]) byKind[(int)(k >> 32)].Add(((uint)(k & 0xFFFFFFFFu), v));
            foreach (var b in byKind) b.Sort((x, y) => x.T.CompareTo(y.T));
            if (byKind[0].Count > 0xFFFF || byKind[1].Count > 0xFFFF) throw new InvalidOperationException("lists: a list's kind past 65,535 entries");
            words.Add((uint)byKind[0].Count | ((uint)byKind[1].Count << 16));
            words.Add((uint)byKind[2].Count);
            for (var k = 0; k < 3; k++)
            {
                counts[k] += byKind[k].Count;
                uint last = 0;
                foreach (var (t, v) in byKind[k])
                {
                    var rgb = PackRgb22(v, lo, hi);
                    if (k == 0) { words.Add((t << 22) | rgb); continue; }   // a bin: 0..63, whole
                    var step = t - last;
                    last = t;
                    if (step < 1023u) words.Add((step << 22) | rgb);
                    else { words.Add((1023u << 22) | rgb); words.Add(t); escapes++; }
                }
            }
        }
        start[lists.Count] = (uint)words.Count;
        return (start, words.ToArray(), new Vector2(lo, hi), counts, escapes);
    }

    // rgb in 22 bits: luminance on a log2 scale over [lo, hi] in the top 12 (0 none, else 1 + its step of 4094), then r and
    // g over r + g + b in 5 bits each (texel_lists.comp, entryRgb). The model: 0.01% a list's light median, 0.04% p90.
    private static uint PackRgb22(Vector3 v, float lo, float hi)
    {
        v = Vector3.Max(v, Vector3.Zero);
        var y = 0.2126f * v.X + 0.7152f * v.Y + 0.0722f * v.Z;
        if (y <= 0f) return 0u;
        var q = (uint)Math.Clamp(MathF.Round((MathF.Log2(y) - lo) / (hi - lo) * 4094f), 0f, 4094f) + 1u;
        var sum = v.X + v.Y + v.Z;
        var r = (uint)Math.Clamp(MathF.Round(v.X / sum * 31f), 0f, 31f);
        var g = (uint)Math.Clamp(MathF.Round(v.Y / sum * 31f), 0f, 31f - r);
        return (q << 10) | (r << 5) | g;
    }

    // --lists: the stored lists up, once.
    private bool ListsReady()
    {
        if (!listsOn || listCook) return false;
        if (listsLoaded) return listCount > 0;
        listsLoaded = true;
        if (lightBakeKey is not { } key) return false;
        uint[]? order = null, start = null, packed = null; float[]? norm = null; var coarse = 0;
        var range = Vector2.Zero;
        var ok = LoadStage(key, "texel-lists", r =>
        {
            if (r.ReadString() != ListsHeader || r.ReadInt32() != texelCount) return (object?)null;
            coarse = r.ReadInt32();
            range = new Vector2(r.ReadSingle(), r.ReadSingle());
            order = ReadArray<uint>(r); start = ReadArray<uint>(r); packed = ReadArray<uint>(r); norm = ReadArray<float>(r);
            return new object();
        }) is not null;
        if (!ok || order is null) { Console.WriteLine("[VulkanSponza] lists: --lists, but no stored lists for this scene and cook (--list-cook first): the gather runs."); return false; }
        listCoarse = coarse; listCount = order.Length; listPackRange = range;
        listTexelsBuffer = Own(device.CreateGpuBuffer(Math.Max(16, order.Length * 4), MemoryMarshal.AsBytes(order.AsSpan()), "sponza.lists.texels"));
        listStartBuffer = Own(device.CreateGpuBuffer(Math.Max(16, start!.Length * 4), MemoryMarshal.AsBytes(start.AsSpan()), "sponza.lists.start"));
        listWordsBuffer = Own(device.CreateGpuBuffer(Math.Max(16, packed!.Length * 4), MemoryMarshal.AsBytes(packed.AsSpan()), "sponza.lists.words"));
        listNormBuffer = Own(device.CreateGpuBuffer(Math.Max(16, norm!.Length * 4), MemoryMarshal.AsBytes(norm.AsSpan()), "sponza.lists.norm"));
        listPrevSky = Own(device.CreateGpuBuffer(texelCount * 8, name: "sponza.lists.prev-sky"));
        listPrevSun = Own(device.CreateGpuBuffer(texelCount * 8, name: "sponza.lists.prev-sun"));
        if (listDebug) listDebugBuffer = Own(device.CreateGpuBuffer(order.Length * 16, name: "sponza.lists.debug"));
        var bytes = (packed.Length + start.Length + order.Length + norm.Length) * 4L;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] lists: {listCount:N0} texels' lists up ({coarse:N0} coarse), {bytes / 1048576.0:0.0} MB ({bytes / (double)Math.Max(1, listCount):0} B a texel): a light change sums them, no gather."));
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
                        new("uLists", new Vector4Uniform(new Vector4(count, first, 0f, listDebug ? -1f : 1f))),
                        new("uToSun", new Vector4Uniform(new Vector4(toSun, SunColumn()))),
                        new("uIrr", new Vector4Uniform(new Vector4(irr, (sunNodeSuns!.Length + 31) / 32))),
                        new("uPack", new Vector4Uniform(new Vector4(listPackRange, 0f, 0f))),
                    },
                    Array.Empty<ShaderTextureBinding>(),
                    Buffers: new[]
                    {
                        new ShaderBufferBinding("ListTexels", listTexelsBuffer),
                        new ShaderBufferBinding("ListStart", listStartBuffer),
                        new ShaderBufferBinding("ListWords", listWordsBuffer),
                        new ShaderBufferBinding("ListNorm", listNormBuffer),
                        new ShaderBufferBinding("Texels", texelBuffer),
                        new ShaderBufferBinding("SunNodeBits", sunNodeBits),
                        new ShaderBufferBinding("CellLight", cellLightBuffer),
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
        if (listDebug) ListFineCoverage();
        Sum(listCoarse, listCount - listCoarse, "fine");
        if (listDebug) ListDebugReport();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] lists: the light summed from {listCount:N0} texels' lists in {clock.Elapsed.TotalMilliseconds:0} ms, no ray traced ({string.Join("; ", times)})."));
        sunNodeStage = SunNodeStage.Done;
        ResetSunNodeRefine();
        if (splitCooking) FinishSplitCook();
    }

    // --list-debug: the fine texels the gather would take at this light (FineGatherIds, from the fine copy's flags) against
    // those with a list (the cook's light's pick). At (120, -40) against the cook's sun: 2,109 of 27,471 without one;
    // cooked with them too, the corridor's seams 0.0458 against 0.0470 -- not what holds them back (2026-10-23).
    private void ListFineCoverage()
    {
        var picked = FineGatherIds();
        var ids = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(listTexelsBuffer, listCoarse * 4, (listCount - listCoarse) * 4).AsSpan()).ToArray();
        var listed = new HashSet<uint>(ids);
        var have = picked.Count(listed.Contains);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] lists, --list-debug: the gather would take {picked.Length:N0} fine texels at this light; {have:N0} have a list, {picked.Length - have:N0} not; {listed.Count - have:N0} of the {listed.Count:N0} fine lists not among them."));
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
