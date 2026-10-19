using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --live-transfer: the texels' gather cooked as transport, lit in real time (step 2 of the real-time GI). What a texel's
// rays reach -- the hit cells (SponzaLoop.HitCells.cs) and the side met, or a sky bin -- is the geometry's alone: cooked
// once, with many cosine rays (--transfer-rays, 1,024), into its K heaviest entries (--transfer-k, 16; the rest assumed
// lit as those are). At each light change nothing is traced: the cells' light (hit_cells.comp mode 1: albedo x their
// patch's light and the sun) and one pass (transfer_apply.comp) give every texel at every level the light its gather
// would have summed -- with no runtime Monte Carlo noise (128 runtime rays a texel left the walls blotchy) and nothing
// set off by a camera move. Cooked at boot for now (2a: the testbeds); a GI bundle stage next.
internal sealed partial class SponzaLoop
{
    private bool liveTransfer;
    private int transferRays = 1024, transferK = 10;
    // --transfer-near M: hits nearer than this keep their cell (the contact detail); past it, their patch.
    private float transferNear = 0.25f;
    private ShaderInterface transferAccumInterface = null!, transferFinalizeInterface = null!, transferApplyInterface = null!;
    private PipelineHandle transferAccumPipeline, transferFinalizePipeline, transferApplyPipeline;
    private GpuBufferHandle transferEntries, transferBins, cellLightBuffer;
    private bool transferReady;
    // The hybrid (2a): texels whose entries dropped more than --transfer-gather-share (5%) of their weight -- the
    // cook's flag that their light may lie in rare paths a compact list cannot keep (a corridor lit through a doorway:
    // 0.75 of the reference even with 50 entries) -- are also gathered at each light change, after the transfer.
    private float transferGatherShare = 0.01f, transferGatherTop = 0.1f;
    private uint[] transferGatherIds = Array.Empty<uint>();
    private Vector3 transferAppliedFor = new(float.NaN);
    private const int TransferTable = 512, TransferTail = 8, TransferRoom = 8192, TransferBinWords = 19;   // the shaders' TABLE, TAIL + 2; texels a cook slice
    private int TransferEntriesPer => transferK + TransferTail;

    private bool TransferOn => liveTexels && liveTransfer && (hitCellsOn || livePatchTexels);

    // --live-patch-texels (path B): every texel from the live patch light, blended over its patch set, times its contact
    // ratio (texel_patch_light.comp) -- no lists, no cells, no gather; the light change a pass.
    private bool livePatchTexels;
    // --live-patch-refine: path B at once, then the coarse levels gathered (with the hit cells) behind the cook screen and
    // the fine rebuilt from them through their 20 cm contact (transfer_fine.comp) -- the light change only, never the
    // camera. Path B alone had the energy but not the look: corners lifted, the curtains' bounce dull (2026-10-18).
    private bool livePatchRefine;
    private ShaderInterface texelPatchLightInterface = null!;
    private PipelineHandle texelPatchLightPipeline;

    private void BuildPatchTexels()
    {
        if (!livePatchTexels || transferReady || frameTracer is null || texelLiveIds is not { } all) return;
        CookTexelAo(all);
        // Each patch's octant openness, the mean of its texels' (texel_patch_light.comp mode 0): the geometry's, once.
        var patches = transportSolve!.Patches;
        patchOpenBuffer = Own(device.CreateGpuBuffer(patches * 18 * 4, new byte[patches * 18 * 4], "sponza.transfer.patch-open"));
        patchDirSharesBuffer = Own(device.CreateGpuBuffer(patches * 8 * 4, name: "sponza.transfer.patch-dir-shares"));
        PatchTexelsPass(0);
        if (livePatchRefine && liveCoarseLevels is { } levels)
        {
            var coarse = all.Where(id => levels[id] >= transferMinLevel).ToArray();
            var fine = all.Where(id => levels[id] < transferMinLevel).ToArray();
            transferIds = coarse;
            transferFineIdsBuffer = Own(device.CreateGpuBuffer(Math.Max(16, fine.Length * 4), MemoryMarshal.AsBytes(fine.AsSpan()), "sponza.transfer.fine-ids"));
            transferFineCount = fine.Length;
            transferGatherIds = coarse;   // gathered at each light change (LiveCoarseOrder), after path B's pass
            liveCoarseOrdered = null;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] patch texels, refined: {coarse.Length:N0} coarse texels gathered at each light change, {fine.Length:N0} fine from them."));
        }
        transferReady = true;
    }

    private void ApplyPatchTexelsNow()
    {
        if (!transferReady || texelLiveIds is not { } live) return;
        var clock = Stopwatch.StartNew();
        var sky = EnsureLivePart(false);
        var sun = EnsureLivePart(true);
        GpuBufferHandle Detail(ref GpuBufferHandle b, string name)
        {
            if (b.Equals(default(GpuBufferHandle))) b = Own(device.CreateGpuBuffer(Math.Max(texelCount, 1) * 4, new byte[Math.Max(texelCount, 1) * 4], name));
            return b;
        }
        var skyDetail = Detail(ref splitSkyDetailBuffer, "sponza.split.sky-detail");
        var sunDetail = Detail(ref splitSunDetailBuffer, "sponza.split.sun-detail");
        var irr = EffectiveSunIrradiance;
        var inv = new Vector3(irr.X > 0 ? 1f / irr.X : 0f, irr.Y > 0 ? 1f / irr.Y : 0f, irr.Z > 0 ? 1f / irr.Z : 0f);
        // The patches' light by where it comes from (patch_dir.comp), for this light.
        var d = transportSolve!;
        var declaredDir = patchDirInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        device.DispatchNow(new[] { new DispatchCommand(patchDirPipeline, (d.Patches + 63) / 64, 1, 1,
            new ShaderUniform[] { new("uDir", new Vector4Uniform(new Vector4(d.Patches, LiveComponents, liveSkyEscapeBuffer.Equals(default(GpuBufferHandle)) ? 99f : LiveSky, 0f))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("CookedPatches", cookedPatchBuffer),
                new ShaderBufferBinding("CouplingStart", solveBuffers[5]),
                new ShaderBufferBinding("Couplings", solveBuffers[6]),
                new ShaderBufferBinding("NodeOutgoing", liveOut),
                new ShaderBufferBinding("PatchSkyEscape", liveSkyEscapeBuffer.Equals(default(GpuBufferHandle)) ? patchDirSharesBuffer : liveSkyEscapeBuffer),
                new ShaderBufferBinding("SkyGuideLum", liveSkyLumBuffer.Equals(default(GpuBufferHandle)) ? patchDirSharesBuffer : liveSkyLumBuffer),
                new ShaderBufferBinding("PatchDirShares", patchDirSharesBuffer),
            }.Where(b => declaredDir.Contains(b.Name)).ToArray()) }, "patch-dir");
        PatchTexelsPass(1, sky, sun, skyDetail, sunDetail);
        splitDetailLanded = true;
        splitComposeDirty = true;
        splitComposeAgainFrame = framesRendered + 1;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] patch texels: {live.Length:N0} texels lit from the patches in {clock.Elapsed.TotalMilliseconds:0} ms."));
    }

    private void PatchTexelsPass(int mode, GpuBufferHandle sky = default, GpuBufferHandle sun = default, GpuBufferHandle skyDetail = default, GpuBufferHandle sunDetail = default)
    {
        if (texelLiveIds is not { } live) return;
        if (sky.Equals(default(GpuBufferHandle))) sky = sun = skyDetail = sunDetail = patchOpenBuffer;   // mode 0: unwritten
        var irr = EffectiveSunIrradiance;
        var inv = new Vector3(irr.X > 0 ? 1f / irr.X : 0f, irr.Y > 0 ? 1f / irr.Y : 0f, irr.Z > 0 ? 1f / irr.Z : 0f);
        var declared = texelPatchLightInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        const int perDispatch = 65535 * 64;
        for (var first = 0; first < live.Length; first += perDispatch)
            device.DispatchNow(new[] { new DispatchCommand(texelPatchLightPipeline, (Math.Min(perDispatch, live.Length - first) + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uPass", new Vector4Uniform(new Vector4(live.Length, first, sunNodeKinds.Length, mode))),
                    new("uColumns", new Vector4Uniform(new Vector4(Math.Max(0, Array.IndexOf(sunNodeKinds, 2)), Math.Max(0, liveSunColumn), 0f, 0f))),
                    new("uInvIrr", new Vector4Uniform(new Vector4(inv, 0f))),
                    new("uFar", new Vector4Uniform(farOn ? FarUniform(0) : Vector4.Zero)),
                    new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                    new("uCookedDims", new Vector4Uniform(cookedDims)),
                },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new[]
                {
                    new ShaderBufferBinding("Texels", texelBuffer),
                    new ShaderBufferBinding("LiveIds", sunNodeLive),
                    new ShaderBufferBinding("TexelPatch", sunNodeTexelPatch),
                    new ShaderBufferBinding("SunNodePatchLight", sunNodePatchLight),
                    new ShaderBufferBinding("AoBytes", texelAoBuffer),
                    new ShaderBufferBinding("SplitSky", sky),
                    new ShaderBufferBinding("SplitSun", sun),
                    new ShaderBufferBinding("SkyDetail", skyDetail),
                    new ShaderBufferBinding("SunDetail", sunDetail),
                    new ShaderBufferBinding("FarRecord", FarRecordBuffer),
                    new ShaderBufferBinding("PatchNeighbours", FarNeighboursBuffer),
                    new ShaderBufferBinding("DirOpen", texelDirOpenBuffer),
                    new ShaderBufferBinding("PatchOpen", patchOpenBuffer),
                    new ShaderBufferBinding("PatchDirShares", patchDirSharesBuffer),
                    new ShaderBufferBinding("AoShort", texelAoShortBuffer),
                }.Concat(CookedBuffers()).Where(b => declared.Contains(b.Name)).ToArray()) }, "texel-patch-light");
    }

    // A': the transfer's texels (the coarse levels), the fine ones, their contact visibility (a byte a texel id).
    private int transferMinLevel = 1, transferFineCount, transferAoRays = 64;
    private float transferAoReach = 0.2f;
    private uint[]? transferIds;
    private GpuBufferHandle transferIdsBuffer, transferFineIdsBuffer, texelAoBuffer, texelDirOpenBuffer, patchOpenBuffer, patchDirSharesBuffer, texelAoShortBuffer;
    // Path B's second contact scale (texel_ao.comp's short reach): creases and contacts.
    private float transferAoShortReach = 0.2f;
    private ShaderInterface patchDirInterface = null!;
    private PipelineHandle patchDirPipeline;
    private ShaderInterface texelAoInterface = null!, transferFineInterface = null!;
    private PipelineHandle texelAoPipeline, transferFinePipeline;

    // Every live texel's contact visibility (texel_ao.comp): cosine rays, a slice at a time through the gather's buffers.
    private void CookTexelAo(uint[] all)
    {
        var clock = Stopwatch.StartNew();
        texelAoBuffer = Own(device.CreateGpuBuffer(Math.Max(16, (texelCount + 3) / 4 * 4), new byte[Math.Max(16, (texelCount + 3) / 4 * 4)], "sponza.transfer.ao"));
        var counts = Own(device.CreateGpuBuffer(sunNodeSlice * 19 * 4, new byte[sunNodeSlice * 19 * 4], "sponza.transfer.ao-counts"));
        if (texelAoShortBuffer.Equals(default(GpuBufferHandle)))
            texelAoShortBuffer = Own(device.CreateGpuBuffer(Math.Max(16, (texelCount + 3) / 4 * 4), new byte[Math.Max(16, (texelCount + 3) / 4 * 4)], "sponza.transfer.ao-short"));
        if (texelDirOpenBuffer.Equals(default(GpuBufferHandle)))
            texelDirOpenBuffer = Own(device.CreateGpuBuffer(Math.Max(16, texelCount * 4), name: "sponza.transfer.dir-open"));
        var passes = Math.Max(1, transferAoRays / 32);
        var declared = texelAoInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        DispatchCommand Ao(int threads, int slice, int mode, int first) => new(texelAoPipeline, (threads + 63) / 64, 1, 1,
            new ShaderUniform[] { new("uAo", new Vector4Uniform(new Vector4(slice, transferAoReach, mode, first))), new("uAo2", new Vector4Uniform(new Vector4(transferAoShortReach, 0f, 0f, 0f))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("NodeHits", sunNodeHits),
                new ShaderBufferBinding("NodeRayWeights", sunNodeRayWeights),
                new ShaderBufferBinding("AoCounts", counts),
                new ShaderBufferBinding("AoBytes", texelAoBuffer),
                new ShaderBufferBinding("AoIds", sunNodeLive),
                new ShaderBufferBinding("NodeRays", sunNodeRaysBuffer),
                new ShaderBufferBinding("DirOpen", texelDirOpenBuffer),
                new ShaderBufferBinding("AoShort", texelAoShortBuffer),
            }.Where(b => declared.Contains(b.Name)).ToArray());
        for (var first = 0; first < all.Length; first += sunNodeSlice)
        {
            var slice = Math.Min(sunNodeSlice, all.Length - first);
            for (var pass = 0; pass < passes; pass++)
            {
                device.DispatchNow(new[] { SunNodeRaysCommand(sunNodeLive, first, slice, pass, plain: true) }, "texel-ao-rays");
                frameTracer!.Trace(sunNodeRaysBuffer, sunNodeHits, slice * 32, SunNodeSeed(first, pass, 0) ^ 0xA0u, closestOnly: true);
                device.DispatchNow(new[] { Ao(slice * 32, slice, 0, first) }, "texel-ao");
            }
            device.DispatchNow(new[] { Ao(slice, slice, 1, first) }, "texel-ao-finish");
        }
        RetireGpuBuffer(counts);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] transfer: contact visibility (within {transferAoReach * 100:0} cm) of {all.Length:N0} texels, {passes * 32} rays each, in {clock.Elapsed.TotalSeconds:0.0} s."));
    }

    // The fine texels from their coarse ones (transfer_fine.comp), after the transfer's pass.
    // After the coarse texels are lit -- the transfer's, and the gathered (the hybrid) landed over theirs: run before the
    // gather, the fine took the transfer's light where the gather had corrected it (the crevices' corridor: 0.56 with
    // every coarse texel gathered, 2026-10-17).
    private GpuBufferHandle fineDebugDummy, fineFlagsBuffer;
    // The last fine pass's flags (transfer_fine.comp): how many, and by why.
    private uint[] fineFlagCounts = new uint[4];
    // The fine refine (--fine-gather N, --fine-rays R): after the coarse gather of a light change, the fine texels the copy
    // cannot answer -- their coarse cells disagree, or none faces their way -- gathered themselves, the worst first, up to
    // N at R rays, behind the cook screen. The cooked mode's full gather answers curtains' folds and the walls' stones
    // the copy cannot (2026-10-19); this is it where it matters. 0: off.
    private int fineGatherBudget, fineGatherRays = 64;
    private bool fineGathering;
    private int fineGatherSavedMaxRays, fineGatherSavedRays;

    // The flagged, worst first, up to the budget.
    private uint[] FineGatherIds()
    {
        var count = (int)Math.Min(fineFlagCounts[0], (uint)transferFineCount);
        if (count == 0 || fineGatherBudget <= 0) return Array.Empty<uint>();
        var clock = Stopwatch.StartNew();
        var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(fineFlagsBuffer, 64, count * 8).AsSpan());
        // The worst first by a histogram of the spread (log2, 1/8 steps), not a sort: the cut is the bin the budget ends
        // in, those above it all taken (a sort of 5 M took 6 s, 2026-10-19).
        const int Bins = 256;
        static int Bin(float score) => Math.Clamp((int)(MathF.Log2(MathF.Max(score, 1f)) * 8f), 0, Bins - 1);
        var histogram = new int[Bins];
        for (var i = 0; i < count; i++) histogram[Bin(BitConverter.UInt32BitsToSingle(words[2 * i + 1]))]++;
        var take = Math.Min(count, fineGatherBudget);
        int cut = Bins - 1, above = 0;
        while (cut > 0 && above + histogram[cut] < take) above += histogram[cut--];
        var ids = new uint[take];
        int n = 0, atCut = take - above;
        for (var i = 0; i < count && n < take; i++)
        {
            var b = Bin(BitConverter.UInt32BitsToSingle(words[2 * i + 1]));
            if (b > cut || (b == cut && atCut-- > 0)) ids[n++] = words[2 * i];
        }
        if (n < take) ids = ids[..n];
        // In place order (the far field's key): picked in the order the flags were appended, a slice's texels lay all over
        // the scene and every hit's lookups missed the caches.
        if (farPlaceKey is { } placeKey && placeKey.Length == texelCount)
        {
            var keys = new ulong[ids.Length];
            for (var i = 0; i < ids.Length; i++) keys[i] = placeKey[ids[i]];
            Array.Sort(keys, ids);
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] fine refine: {n:N0} of {count:N0} flagged gathered at {fineGatherRays} rays (the spread at the cut {MathF.Pow(2f, cut / 8f):0.0}x; picked in {clock.Elapsed.TotalMilliseconds:0} ms)."));
        return ids;
    }

    private void ApplyFineNow()
    {
        if (transferFineCount == 0 || !transferReady) return;
        if (fineFlagsBuffer.Equals(default(GpuBufferHandle)))
            fineFlagsBuffer = Own(device.CreateGpuBuffer((16 + 2 * transferFineCount) * 4, name: "sponza.transfer.fine-flags"));
        device.WriteGpuBuffer(fineFlagsBuffer, 0, new byte[64]);
        var clock = Stopwatch.StartNew();
        FineDispatch(transferFineIdsBuffer, transferFineCount, default);
        device.WaitIdle();
        fineFlagCounts = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(fineFlagsBuffer, 0, 64).AsSpan()).ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] fine pass: {transferFineCount:N0} texels from the coarse in {clock.Elapsed.TotalMilliseconds:0} ms; flagged for their own gather {fineFlagCounts[0]:N0} ({100.0 * fineFlagCounts[0] / transferFineCount:0.0}%): none turned their way {fineFlagCounts[1]:N0}, the ones turned their way disagree {fineFlagCounts[2]:N0}, those and the cut ones disagree {fineFlagCounts[3]:N0}; the ones turned their way over 1.5/2/3/4/8/16x {string.Join(" / ", fineFlagCounts[4..10].Select(c => c.ToString("N0", CultureInfo.InvariantCulture)))}, against the cut ones {string.Join(" / ", fineFlagCounts[10..16].Select(c => c.ToString("N0", CultureInfo.InvariantCulture)))}."));
    }

    // BLIX_SPOT_WHY: the fine pass again over these texels (their light unchanged: the coarse texels it reads are as
    // they were), each writing its candidates (transfer_fine.comp, FineDebug): 32 words a texel.
    private uint[] FineCandidates(uint[] ids)
    {
        var idsBuffer = device.CreateGpuBuffer(Math.Max(16, ids.Length * 4), MemoryMarshal.AsBytes(ids.AsSpan()), "sponza.spot.fine-ids");
        var debug = device.CreateGpuBuffer(Math.Max(16, ids.Length * 128), name: "sponza.spot.fine-debug");
        FineDispatch(idsBuffer, ids.Length, debug);
        device.WaitIdle();
        var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(debug, 0, ids.Length * 128).AsSpan()).ToArray();
        device.DestroyGpuBuffer(idsBuffer);
        device.DestroyGpuBuffer(debug);
        return words;
    }

    private void FineDispatch(GpuBufferHandle idsBuffer, int count, GpuBufferHandle debug)
    {
        if (count == 0 || !transferReady) return;
        var debugOn = !debug.Equals(default(GpuBufferHandle));
        if (!debugOn)
        {
            if (fineDebugDummy.Equals(default(GpuBufferHandle))) fineDebugDummy = Own(device.CreateGpuBuffer(128, name: "sponza.transfer.fine-debug"));
            debug = fineDebugDummy;
        }
        var sky = EnsureLivePart(false);
        var sun = EnsureLivePart(true);
        var skyDetail = splitSkyDetailBuffer;
        var sunDetail = splitSunDetailBuffer;
        if (skyDetail.Equals(default(GpuBufferHandle)) || sunDetail.Equals(default(GpuBufferHandle))) return;
        var declared = transferFineInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        const int perDispatch = 65535 * 64;
        for (var first = 0; first < count; first += perDispatch)
            device.DispatchNow(new[] { new DispatchCommand(transferFinePipeline, (Math.Min(perDispatch, count - first) + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uFine", new Vector4Uniform(new Vector4(count, first, transferMinLevel, texelLevels - 1))),
                    new("uTexelGrid", new Vector4Uniform(texelGrid)),
                    new("uTexelParams", new Vector4Uniform(new Vector4(texelCapacity, texelLevels, debugOn ? 1f : 0f, Luminance(EffectiveSunIrradiance)))),
                },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new[]
                {
                    new ShaderBufferBinding("TexelHash", texelHashBuffer),
                    new ShaderBufferBinding("Texels", texelBuffer),
                    new ShaderBufferBinding("FineIds", idsBuffer),
                    new ShaderBufferBinding("SplitSky", sky),
                    new ShaderBufferBinding("SplitSun", sun),
                    new ShaderBufferBinding("SkyDetail", skyDetail),
                    new ShaderBufferBinding("SunDetail", sunDetail),
                    new ShaderBufferBinding("AoBytes", livePatchTexels ? texelAoShortBuffer : texelAoBuffer),
                    new ShaderBufferBinding("FineDebug", debug),
                    new ShaderBufferBinding("FineFlags", fineFlagsBuffer.Equals(default(GpuBufferHandle)) ? debug : fineFlagsBuffer),
                }.Where(b => declared.Contains(b.Name)).ToArray()) }, "transfer-fine");
    }

    // BLIX_TRANSFER_CHECK=1 (a measurement): the transfer applied and kept, then the gather run over every texel as
    // before, and the two compared texel by texel at its end (TransferCheckCompare).
    private static readonly bool transferCheck = Environment.GetEnvironmentVariable("BLIX_TRANSFER_CHECK") == "1";
    private uint[]? transferCheckSky, transferCheckSun;
    private bool transferChecked;

    private void TransferCheckSnapshot()
    {
        device.WaitIdle();
        transferCheckSky = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(EnsureLivePart(false), 0, texelCount * 8).AsSpan()).ToArray();
        transferCheckSun = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(EnsureLivePart(true), 0, texelCount * 8).AsSpan()).ToArray();
    }

    private void TransferCheckCompare()
    {
        if (transferCheckSky is not { } tSky || transferCheckSun is not { } tSun || texelLiveIds is not { } live) return;
        transferCheckSky = transferCheckSun = null;
        device.WaitIdle();
        var gSky = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(EnsureLivePart(false), 0, texelCount * 8).AsSpan()).ToArray();
        var gSun = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(EnsureLivePart(true), 0, texelCount * 8).AsSpan()).ToArray();
        var texels = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelBuffer, 0, texelCount * 32).AsSpan()).ToArray();
        static float L(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        var irr = L(EffectiveSunIrradiance);
        double sumT = 0, sumG = 0, sumTsky = 0, sumGsky = 0, sumTsun = 0, sumGsun = 0;
        var worst = new List<(float Ratio, uint Id, float T, float G, float TSky, float GSky)>();
        foreach (var id in live)
        {
            var ts = L(UnpackRgb9e5(tSky[2 * id])); var tu = L(UnpackRgb9e5(tSun[2 * id])) * irr;
            var gs = L(UnpackRgb9e5(gSky[2 * id])); var gu = L(UnpackRgb9e5(gSun[2 * id])) * irr;
            sumTsky += ts; sumGsky += gs; sumTsun += tu; sumGsun += gu; sumT += ts + tu; sumG += gs + gu;
            if (gs + gu > 1e-4f) worst.Add(((ts + tu) / (gs + gu), id, ts + tu, gs + gu, ts, gs));
        }
        // By the gather's light (luminance decades): the dark texels -- the crevices' corridor -- apart from the lit.
        var bands = new (double T, double G, int N)[6];
        foreach (var w in worst)
        {
            var band = Math.Clamp((int)Math.Floor(-Math.Log10(Math.Max(w.G, 1e-6))), 0, 5);
            bands[band] = (bands[band].T + w.T, bands[band].G + w.G, bands[band].N + 1);
        }
        Console.WriteLine("[VulkanSponza] transfer check, by the gather's light: " + string.Join("; ", bands.Select((b, i) =>
            string.Create(CultureInfo.InvariantCulture, $"1e-{i}..1e-{i + 1}: {b.N:N0} texels, {(b.G > 0 ? b.T / b.G : 0):0.000}"))));
        {
            var coarseIds = new HashSet<uint>(transferIds ?? Array.Empty<uint>());
            foreach (var coarseSide in new[] { true, false })
            {
                var bb = new (double T, double G, int N)[4];
                foreach (var w in worst)
                {
                    if (coarseIds.Contains(w.Id) != coarseSide) continue;
                    var band = Math.Clamp((int)Math.Floor(-Math.Log10(Math.Max(w.G, 1e-6))), 0, 3);
                    bb[band] = (bb[band].T + w.T, bb[band].G + w.G, bb[band].N + 1);
                }
                Console.WriteLine($"[VulkanSponza] transfer check, {(coarseSide ? "coarse" : "fine")} by the gather's light: " + string.Join("; ", bb.Select((b, i) =>
                    string.Create(CultureInfo.InvariantCulture, $"1e-{i}: {b.N:N0}, {(b.G > 0 ? b.T / b.G : 0):0.000}"))));
            }
        }
        // The band 0.01-0.1's darkest against the gather: where the corridor's light goes missing.
        var under = worst.Where(w => w.G is > 0.01f and < 0.1f).OrderBy(w => w.Ratio).Take(4).ToList();
        worst.Sort((a, b) => b.Ratio.CompareTo(a.Ratio));
        worst.InsertRange(0, under);
        {
            var coarseSet = new HashSet<uint>(transferIds ?? Array.Empty<uint>());
            double cT = 0, cG = 0, fT = 0, fG = 0, cTs = 0, cGs = 0, fTs = 0, fGs = 0;
            foreach (var id in live)
            {
                var ts = L(UnpackRgb9e5(tSun[2 * id])) * irr; var gs = L(UnpackRgb9e5(gSun[2 * id])) * irr;
                var t = L(UnpackRgb9e5(tSky[2 * id])) + ts;
                var g = L(UnpackRgb9e5(gSky[2 * id])) + gs;
                if (coarseSet.Contains(id)) { cT += t; cG += g; cTs += ts; cGs += gs; } else { fT += t; fG += g; fTs += ts; fGs += gs; }
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transfer check, coarse (the transfer's) {coarseSet.Count:N0}: all {cT / Math.Max(1e-9, cG):0.000}, sun {cTs / Math.Max(1e-9, cGs):0.000}; fine (from them) {live.Length - coarseSet.Count:N0}: all {fT / Math.Max(1e-9, fG):0.000}, sun {fTs / Math.Max(1e-9, fGs):0.000}."));
        }
        {
            // By the texel's own sun (its bit for the cook's sun): in the sun's shadow against in its light.
            var bitWords = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(liveSunBits, 0, texelCount * liveSunWords * 4).AsSpan()).ToArray();
            double sT = 0, sG = 0, lT = 0, lG = 0; int sN = 0, lN = 0;
            foreach (var id in live)
            {
                var lit = (bitWords[id * liveSunWords + liveSunColumn / 32] & (1u << (liveSunColumn % 32))) != 0;
                var t = L(UnpackRgb9e5(tSky[2 * id])) + L(UnpackRgb9e5(tSun[2 * id])) * irr;
                var g = L(UnpackRgb9e5(gSky[2 * id])) + L(UnpackRgb9e5(gSun[2 * id])) * irr;
                if (lit) { lT += t; lG += g; lN++; } else { sT += t; sG += g; sN++; }
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transfer check, in the sun's shadow {sN:N0} texels: {sT / Math.Max(1e-9, sG):0.000} of the gather (mean {sT / Math.Max(1, sN):0.0000} against {sG / Math.Max(1, sN):0.0000}); in its light {lN:N0}: {lT / Math.Max(1e-9, lG):0.000} (mean {lT / Math.Max(1, lN):0.0000} against {lG / Math.Max(1, lN):0.0000})."));
        }
        var ratios = worst.Select(w => w.Ratio).OrderBy(x => x).ToArray();
        float Q(double q) => ratios.Length == 0 ? 0 : ratios[(int)Math.Min(ratios.Length - 1, q * ratios.Length)];
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] transfer check: {live.Length:N0} texels, transfer over gather: all {sumT / Math.Max(1e-9, sumG):0.000}, sky {sumTsky / Math.Max(1e-9, sumGsky):0.000}, sun {sumTsun / Math.Max(1e-9, sumGsun):0.000}; per texel p10 {Q(0.1):0.00} median {Q(0.5):0.00} p90 {Q(0.9):0.00} p99 {Q(0.99):0.00}."));
        var per = TransferEntriesPer;
        if (livePatchTexels) return;   // path B: no entries to dump (the comparison above is its)
        var coarse = transferIds ?? Array.Empty<uint>();
        var entries = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(transferEntries, 0, coarse.Length * per * 8).AsSpan()).ToArray();
        var cellLight = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(cellLightBuffer, 0, (int)(hitCellCount * 16)).AsSpan()).ToArray();
        // By kind: each entry's share, and the sun part its cells bring (the far's and the patches' are the rest).
        double shCell = 0, shPatch = 0, shFar = 0, shSky = 0, sunCells = 0, sunAll = 0;
        for (var i = 0; i < coarse.Length; i++)
        {
            for (var k = 0; k < per; k++)
            {
                var key = entries[2 * (i * per + k)]; var y = entries[2 * (i * per + k) + 1];
                if (key == uint.MaxValue) break;
                var share = (float)BitConverter.UInt16BitsToHalf((ushort)(y & 0xFFFF));
                if ((key & 0x80000000u) != 0) shPatch += share;
                else if ((key & 0x40000000u) != 0) shSky += share;
                else if ((key & 0x20000000u) != 0) shFar += share;
                else { shCell += share; sunCells += share * L(UnpackRgb9e5(cellLight[4 * (key >> 1) + 2 * (key & 1) + 1])); }
            }
            sunAll += L(UnpackRgb9e5(tSun[2 * coarse[i]])) * irr;
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] transfer check, by kind: shares cells {shCell / live.Length:0.000}, patches {shPatch / live.Length:0.000}, far {shFar / live.Length:0.000}, sky {shSky / live.Length:0.000}; the sun part's mean {sunAll / live.Length:0.0000}, its cells' {sunCells / live.Length:0.0000} (the gather's {sumGsun / live.Length:0.0000})."));
        var patchTerms = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(solveBuffers[7], 0, transportSolve!.Patches * 48).AsSpan()).ToArray();
        var patchLightWords = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(sunNodePatchLight, 0, transportSolve.Patches * sunNodeKinds.Length * 8).AsSpan()).ToArray();
        var patchDirect = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(liveDirect, 0, transportSolve.Patches * 8).AsSpan()).ToArray();
        Vector3 PatchColumn(uint pi, int column)
        {
            var at = 2 * ((int)pi * sunNodeKinds.Length + column);
            var a = (float)BitConverter.UInt16BitsToHalf((ushort)(patchLightWords[at] & 0xFFFF));
            var b = (float)BitConverter.UInt16BitsToHalf((ushort)(patchLightWords[at] >> 16));
            var c = (float)BitConverter.UInt16BitsToHalf((ushort)(patchLightWords[at + 1] & 0xFFFF));
            return new Vector3(a, b, c);
        }
        var sunColumn = Math.Max(0, liveSunColumn);
        string PatchValue(uint pi)
        {
            var albedo = new Vector3(patchTerms[3 * pi].X, patchTerms[3 * pi].Y, patchTerms[3 * pi].Z);
            var count = patchDirect[2 * pi + 1];
            var direct = count > 0 ? patchDirect[2 * pi] / 4096f / count : 0f;
            return string.Create(CultureInfo.InvariantCulture,
                $" albedo {L(albedo):0.00} bounce sun {L(PatchColumn(pi, sunColumn)):0.0000} direct n.l {direct:0.0000} over {count} texels -> sun {L(albedo * (PatchColumn(pi, sunColumn) + EffectiveSunIrradiance * direct)):0.0000}");
        }
        var index = new Dictionary<uint, int>();
        for (var i = 0; i < coarse.Length; i++) index[coarse[i]] = i;
        var shown = 0;
        // Ground truth for a few: 4,096 cosine rays traced afresh, each hit's cell's sun light averaged (no keys, no
        // truncation) -- against the transfer and the gather.
        var hitWords = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(hitCellsBuffer, 0, (int)((1 + 2L * hitCellRows + 2L * hitCellCount) * 4)).AsSpan()).ToArray();
        float TruthSun(uint id)
        {
            var p = texels[2 * id]; var nn = Vector3.Normalize(new Vector3(texels[2 * id + 1].X, texels[2 * id + 1].Y, texels[2 * id + 1].Z));
            var o0 = new Vector3(p.X, p.Y, p.Z) + nn * 0.01f;
            var t1 = Vector3.Normalize(MathF.Abs(nn.Y) < 0.999f ? Vector3.Cross(Vector3.UnitY, nn) : Vector3.Cross(Vector3.UnitX, nn));
            var t2 = Vector3.Cross(nn, t1);
            const int N = 4096;
            var rays = new Vector4[2 * N];
            var rng = new Random(7);
            for (var i = 0; i < N; i++)
            {
                var r = MathF.Sqrt((float)rng.NextDouble()); var phi = 2 * MathF.PI * (float)rng.NextDouble();
                var d = Vector3.Normalize(t1 * r * MathF.Cos(phi) + t2 * r * MathF.Sin(phi) + nn * MathF.Sqrt(MathF.Max(0, 1 - r * r)));
                rays[2 * i] = new Vector4(o0, 0f); rays[2 * i + 1] = new Vector4(d, float.PositiveInfinity);
            }
            var hits = frameTracer!.TraceFromHost(rays, 99u, true, out _);
            double sum = 0; var cellBase = 1 + 2L * hitCellRows; var noRow = 0;
            for (var i = 0; i < N; i++)
            {
                if ((hits[12 * i + 3] & 1) == 0) continue;
                var row = hits[12 * i + 6];
                if (row == uint.MaxValue || (row & 0x80000000u) != 0) { noRow++; continue; }
                var raw = hits[12 * i + 7];
                var bx = (raw & 0xFFFF) / 65535f; var by = (raw >> 16) / 65535f;
                var rr = hitWords[2 + 2 * row]; var first = hitWords[1 + 2 * row];
                var ci = Math.Min((uint)(bx * rr), rr - 1); var cj = Math.Min((uint)(by * rr), rr - 1 - ci);
                var cell = first + ci * rr - ci * (ci - 1) / 2 + cj;
                var normal = new Vector3(BitConverter.UInt32BitsToSingle(hits[12 * i + 8]), BitConverter.UInt32BitsToSingle(hits[12 * i + 9]), BitConverter.UInt32BitsToSingle(hits[12 * i + 10]));
                var side = Vector3.Dot(normal, new Vector3(rays[2 * i + 1].X, rays[2 * i + 1].Y, rays[2 * i + 1].Z)) < 0 ? 0u : 1u;
                sum += L(UnpackRgb9e5(cellLight[4 * cell + 2 * side + 1]));
            }
            if (noRow > 0) Console.WriteLine($"        (truth: {noRow} hits with no row)");
            return (float)(sum / N);
        }
        foreach (var w in worst.Where(w => index.ContainsKey(w.Id)).Take(3))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    truth for texel {w.Id}: sun part {TruthSun(w.Id):0.0000}, transfer {L(UnpackRgb9e5(tSun[2 * w.Id])) * irr:0.0000}, gather {L(UnpackRgb9e5(gSun[2 * w.Id])) * irr:0.0000}"));
        foreach (var w in worst.Take(8))
        {
            var p = texels[2 * w.Id]; var n = texels[2 * w.Id + 1];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    texel {w.Id} at ({p.X:0.00},{p.Y:0.00},{p.Z:0.00}) n ({n.X:0.00},{n.Y:0.00},{n.Z:0.00}) w {p.W:0}: transfer {w.T:0.0000} (sky {w.TSky:0.0000}) gather {w.G:0.0000} (sky {w.GSky:0.0000}) x{w.Ratio:0.0}"));
            if (shown++ >= 4 || !index.ContainsKey(w.Id)) continue;
            var binWords = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(transferBins, index[w.Id] * TransferBinWords * 4, TransferBinWords * 4).AsSpan()).ToArray();
            float Bin(int k, int b) => ((binWords[k * 9 + b / 4] >> (8 * (b % 4))) & 255) / 255f;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"        far bins: {string.Join(" ", Enumerable.Range(0, 36).Where(b => Bin(0, b) > 0).Select(b => $"{b}:{Bin(0, b):0.000}"))}; sky {Enumerable.Range(0, 36).Sum(b => Bin(1, b)):0.000}"));
            {
                var tp = -1; var gp = -1;
                var fp = (int)(binWords[18] & 0xFFFFFF) - 1;
                Console.WriteLine(FormattableString.Invariant($"        the cook's far patch {fp}; far mode {FarUniform(0).X}"));
                var stride = 3 + 4 * sunNodeKinds.Length;
                if (fp >= 0)
                {
                    var rec = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(FarRecordBuffer, fp * 36 * stride * 4, 36 * stride * 4).AsSpan()).ToArray();
                    Console.WriteLine(FormattableString.Invariant($"        far patch {fp} (texel-patch {tp}, guide {gp}): ") + string.Join(" ", Enumerable.Range(0, 36).Where(b => Bin(0, b) > 0).Select(b =>
                        {
                            var at = b * stride; var wgt = Math.Max(rec[at], 1u);
                            float Col(int c) => 0.2126f * rec[at + 3 + 3 * c] / wgt + 0.7152f * rec[at + 4 + 3 * c] / wgt + 0.0722f * rec[at + 5 + 3 * c] / wgt;
                            return FormattableString.Invariant($"{b}:w{rec[at]} sky {Col(0):0.0000} sun {Col(1):0.0000}");
                        })));
                }
            }
            var at = index[w.Id] * per;
            for (var k = 0; k < per; k++)
            {
                var key = entries[2 * (at + k)]; var y = entries[2 * (at + k) + 1];
                if (key == uint.MaxValue) break;
                var share = (float)BitConverter.UInt16BitsToHalf((ushort)(y & 0xFFFF));
                var kind = (key & 0x80000000u) != 0 ? $"patch {key & 0x7FFFFFFF}" : (key & 0x40000000u) != 0 ? $"sky bin {key & 63}" : $"cell {key >> 1} side {key & 1}";
                var val = (key & 0xC0000000u) == 0 ? $" light sky {L(UnpackRgb9e5(cellLight[4 * (key >> 1) + 2 * (key & 1)])):0.0000} sun {L(UnpackRgb9e5(cellLight[4 * (key >> 1) + 2 * (key & 1) + 1])):0.0000}"
                    : (key & 0x80000000u) != 0 ? PatchValue(key & 0x7FFFFFFF) : "";
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        {kind}: share {share:0.0000} oct ({(y >> 16) & 255},{y >> 24}){val}"));
            }
        }
    }

    private void DeclareTransfer(Func<string[], ShaderInterface> reflect)
    {
        transferAccumInterface = reflect(new[] { "transfer_accum.comp" });
        transferFinalizeInterface = reflect(new[] { "transfer_finalize.comp" });
        texelPatchLightInterface = reflect(new[] { "texel_patch_light.comp" });
        patchDirInterface = reflect(new[] { "patch_dir.comp" });
        transferApplyInterface = reflect(new[] { "transfer_apply.comp" });
        texelAoInterface = reflect(new[] { "texel_ao.comp" });
        transferFineInterface = reflect(new[] { "transfer_fine.comp" });
        sliceResolveInterface = reflect(new[] { "slice_resolve.comp" });
    }

    private void CreateTransferPipelines(string shaderDir)
    {
        PipelineHandle Make(string name, ShaderInterface si) => Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, name + ".comp.spv")), si, name)), name));
        transferAccumPipeline = Make("transfer_accum", transferAccumInterface);
        transferFinalizePipeline = Make("transfer_finalize", transferFinalizeInterface);
        texelPatchLightPipeline = Make("texel_patch_light", texelPatchLightInterface);
        patchDirPipeline = Make("patch_dir", patchDirInterface);
        transferApplyPipeline = Make("transfer_apply", transferApplyInterface);
        texelAoPipeline = Make("texel_ao", texelAoInterface);
        transferFinePipeline = Make("transfer_fine", transferFineInterface);
        sliceResolvePipeline = Make("slice_resolve", sliceResolveInterface);
    }

    // After the hit cells (MergeDiscovered): every live texel's entries, a slice at a time, its rays' passes through the
    // gather's own ray, trace and buffers outside the frame (DispatchNow), then the slice's tables kept.
    private void BuildTransfer()
    {
        if (!TransferOn || livePatchTexels || transferReady || !HitCellsReady || frameTracer is null || texelLiveIds is not { } all || liveCoarseLevels is not { } levels) return;
        // A': the coarse levels' texels (level transferMinLevel and coarser) carry the transfer; the finer read theirs
        // through their contact visibility (texel_ao.comp, transfer_fine.comp).
        var live = all.Where(id => levels[id] >= transferMinLevel).ToArray();
        var fine = all.Where(id => levels[id] < transferMinLevel).ToArray();
        transferIds = live;
        transferIdsBuffer = Own(device.CreateGpuBuffer(Math.Max(16, live.Length * 4), MemoryMarshal.AsBytes(live.AsSpan()), "sponza.transfer.ids"));
        transferFineIdsBuffer = Own(device.CreateGpuBuffer(Math.Max(16, fine.Length * 4), MemoryMarshal.AsBytes(fine.AsSpan()), "sponza.transfer.fine-ids"));
        transferFineCount = fine.Length;
        CookTexelAo(all);
        var clock = Stopwatch.StartNew();
        // The far record up first: a ray is cut at the reach only in a direction its far patches' record knows (its
        // weight), and with none up no ray was -- every far ray went to a cell (2026-10-16).
        FarRelightNow();
        var room = Math.Min(sunNodeSlice, TransferRoom);   // 8,192 x 512 slots x 5 words: 84 MB
        var tableWords = (long)room * TransferTable * 5 + room + 2L * room * 64;   // + the far and sky bins (transfer_accum.comp)
        // Never past the device's budget (a 16 GB machine's GPU was lost to paging once, 2026-10-16): too large, no transfer.
        var need = (long)live.Length * (TransferEntriesPer * 8 + TransferBinWords * 4) + tableWords * 4;
        var (allocatedMb, recommendedMb) = frameTracer.Memory;
        if (allocatedMb + need / 1048576.0 > 0.97 * recommendedMb)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transfer: {need / 1048576.0:0} MB for {live.Length:N0} texels would pass the GPU's budget ({allocatedMb:0} of {recommendedMb:0} MB allocated) -- no transfer; the gather as before."));
            liveTransfer = false;
            // The trace's ids were taken for the transfer (none): the coarse levels' gather's now.
            liveCoarseOrdered = null;
            RetireTraceBuffer();
            sunNodeTraceIds = LiveCoarseOrder();
            sunNodeTraceBuffer = Own(device.CreateGpuBuffer(Math.Max(16, sunNodeTraceIds.Length * 4), MemoryMarshal.AsBytes(sunNodeTraceIds.AsSpan()), "sponza.sun-nodes.traced"));
            return;
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] transfer: {need / 1048576.0:0} MB for {live.Length:N0} texels ({allocatedMb:0} of {recommendedMb:0} MB allocated before)."));
        var init = new uint[tableWords];
        for (long s = 0; s < (long)room * TransferTable; s++) init[s * 5] = uint.MaxValue;
        var table = Own(device.CreateGpuBuffer(checked((int)(tableWords * 4)), MemoryMarshal.AsBytes(init.AsSpan()), "sponza.transfer.table"));
        transferEntries = Own(device.CreateGpuBuffer(checked((int)Math.Max(16L, (long)live.Length * TransferEntriesPer * 8)), name: "sponza.transfer.entries"));
        transferBins = Own(device.CreateGpuBuffer(checked((int)Math.Max(16L, (long)live.Length * TransferBinWords * 4)), name: "sponza.transfer.bins"));
        var stats = Own(device.CreateGpuBuffer(32, new byte[32], "sponza.transfer.stats"));
        var passes = Math.Max(1, transferRays / 32);
        long rays = 0;
        for (var first = 0; first < live.Length; first += room)
        {
            var slice = Math.Min(room, live.Length - first);
            for (var pass = 0; pass < passes; pass++)
            {
                device.DispatchNow(new[] { SunNodeRaysCommand(transferIdsBuffer, first, slice, pass, plain: true, farCut: true) }, "transfer-rays");
                frameTracer.Trace(sunNodeRaysBuffer, sunNodeHits, slice * 32, SunNodeSeed(first, pass, 0) ^ 0x7A5Fu, closestOnly: true);
                device.DispatchNow(new[] { TransferCommand(transferAccumPipeline, transferAccumInterface, slice * 32,
                    new Vector4(slice, transferNear, first, room), table, stats) }, "transfer-accum");
                rays += slice * 32L;
            }
            device.DispatchNow(new[] { TransferCommand(transferFinalizePipeline, transferFinalizeInterface, slice,
                new Vector4(slice, first, transferK, room), table, stats) }, "transfer-finalize");
        }
        RetireGpuBuffer(table);
        device.WaitIdle();
        {
            var binWords = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(transferBins, 0, live.Length * TransferBinWords * 4).AsSpan()).ToArray();
            // The worst --transfer-gather-top of them by what their entries dropped (past --transfer-gather-share).
            var ranked = Enumerable.Range(0, live.Length).Select(i => (Id: live[i], Dropped: (binWords[i * TransferBinWords + 18] >> 24) / 255f))
                .Where(x => x.Dropped > transferGatherShare).OrderByDescending(x => x.Dropped).Take((int)(transferGatherTop * live.Length)).ToList();
            var flagged = ranked.Select(x => x.Id).ToList();
            transferGatherIds = flagged.ToArray();
            liveCoarseOrdered = null;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transfer: the {flagged.Count:N0} of {live.Length:N0} texels ({100.0 * flagged.Count / Math.Max(1, live.Length):0.0}%) that dropped the most (down to {(ranked.Count > 0 ? ranked[^1].Dropped : 0):P0} of their weight) -- gathered at each light change too."));
        }
        var s3 = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(stats, 0, 32).AsSpan()).ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] transfer: the cook's rays -- hits {s3[3]:N0}, misses {s3[4]:N0} (cut at the reach {s3[5]:N0}), hits with no row {s3[6]:N0}."));
        RetireGpuBuffer(stats);
        transferReady = true;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] transfer: {live.Length:N0} texels x {passes * 32} rays ({rays / 1e9:0.00} G) cooked in {clock.Elapsed.TotalSeconds:0.0} s; {transferK} + {TransferTail} near entries and 36 far + 36 sky bins a texel ({(long)live.Length * (TransferEntriesPer * 8 + TransferBinWords * 4) / 1048576.0:0} MB); "
            + $"{100.0 * s3[0] / Math.Max(1u, s3[2]):0.0}% of texels reached more than {transferK} keys, the near weight kept (the tail's patches with it) {s3[1] / 1024.0 / Math.Max(1u, s3[2]) * 100:0.0}% on average."));
    }

    private DispatchCommand TransferCommand(PipelineHandle pipeline, ShaderInterface si, int threads, Vector4 slice, GpuBufferHandle table, GpuBufferHandle stats)
    {
        var declared = si.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        return new DispatchCommand(pipeline, (threads + 63) / 64, 1, 1,
            new ShaderUniform[] { new("uSlice", new Vector4Uniform(slice)), new("uFar", new Vector4Uniform(FarUniform(0))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("NodeRays", sunNodeRaysBuffer),
                new ShaderBufferBinding("NodeHits", sunNodeHits),
                new ShaderBufferBinding("NodeRayWeights", sunNodeRayWeights),
                new ShaderBufferBinding("HitCells", hitCellsBuffer),
                new ShaderBufferBinding("TransferTable", table),
                new ShaderBufferBinding("TransferEntries", transferEntries.Equals(default(GpuBufferHandle)) ? table : transferEntries),
                new ShaderBufferBinding("TransferStats", stats),
                new ShaderBufferBinding("TransferBins", transferBins.Equals(default(GpuBufferHandle)) ? table : transferBins),
                new ShaderBufferBinding("SunNodesLive", transferIdsBuffer),
                new ShaderBufferBinding("TexelPatch", sunNodeTexelPatch),
                new ShaderBufferBinding("NodeGuidePatch", sunNodeGuidePatch),
                new ShaderBufferBinding("CookedPatches", cookedPatchBuffer),
                new ShaderBufferBinding("FarRecord", FarRecordBuffer),
                new ShaderBufferBinding("Texels", texelBuffer),
            }.Where(b => declared.Contains(b.Name)).ToArray());
    }

    // The light change's whole cook (RunSunNodePassesNow, the trace empty): the cells' light is up (HitCellsSunNow);
    // every live texel's from its entries, into the parts and their detail.
    private void ApplyTransferNow()
    {
        if (!transferReady || transferIds is not { } live || cellLightBuffer.Equals(default(GpuBufferHandle))) return;
        var clock = Stopwatch.StartNew();
        var sky = EnsureLivePart(false);
        var sun = EnsureLivePart(true);
        GpuBufferHandle Detail(ref GpuBufferHandle b, string name)
        {
            if (b.Equals(default(GpuBufferHandle))) b = Own(device.CreateGpuBuffer(Math.Max(texelCount, 1) * 4, new byte[Math.Max(texelCount, 1) * 4], name));
            return b;
        }
        var skyDetail = Detail(ref splitSkyDetailBuffer, "sponza.split.sky-detail");
        var sunDetail = Detail(ref splitSunDetailBuffer, "sponza.split.sun-detail");
        var irr = EffectiveSunIrradiance;
        var inv = new Vector3(irr.X > 0 ? 1f / irr.X : 0f, irr.Y > 0 ? 1f / irr.Y : 0f, irr.Z > 0 ? 1f / irr.Z : 0f);
        var declared = transferApplyInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        const int perDispatch = 65535 * 64;
        for (var first = 0; first < live.Length; first += perDispatch)
        {
            var n = Math.Min(perDispatch, live.Length - first);
            device.DispatchNow(new[] { new DispatchCommand(transferApplyPipeline, (n + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uApply", new Vector4Uniform(new Vector4(live.Length, TransferEntriesPer, first, 0f))),
                    new("uColumns", new Vector4Uniform(new Vector4(sunNodeKinds.Length, Math.Max(0, Array.IndexOf(sunNodeKinds, 2)), Math.Max(0, liveSunColumn), 0f))),
                    new("uIrr", new Vector4Uniform(new Vector4(irr, 0f))),
                    new("uFar", new Vector4Uniform(FarUniform(0))),
                    new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                    new("uCookedDims", new Vector4Uniform(cookedDims)),
                    new("uInvIrr", new Vector4Uniform(new Vector4(inv, 0f))),
                    new("uSky", new Vector4Uniform(new Vector4(1f, 1f, 0f, 0f))),
                },
                new[] { new ShaderTextureBinding("uSkyRadiance", envCubeTexture) },
                Buffers: new[]
                {
                    new ShaderBufferBinding("Texels", texelBuffer),
                    new ShaderBufferBinding("LiveIds", transferIdsBuffer),
                    new ShaderBufferBinding("TransferEntries", transferEntries),
                    new ShaderBufferBinding("TransferBins", transferBins),
                    new ShaderBufferBinding("CellLight", cellLightBuffer),
                    new ShaderBufferBinding("SplitSky", sky),
                    new ShaderBufferBinding("SplitSun", sun),
                    new ShaderBufferBinding("SkyDetail", skyDetail),
                    new ShaderBufferBinding("SunDetail", sunDetail),
                    new ShaderBufferBinding("PatchTerms", solveBuffers[7]),
                    new ShaderBufferBinding("SunNodePatchLight", sunNodePatchLight),
                    new ShaderBufferBinding("PatchDirect", liveDirect),
                    new ShaderBufferBinding("TexelPatch", sunNodeTexelPatch),
                    new ShaderBufferBinding("NodeGuidePatch", sunNodeGuidePatch),
                    new ShaderBufferBinding("FarRecord", FarRecordBuffer),
                    new ShaderBufferBinding("PatchNeighbours", FarNeighboursBuffer),
                }.Concat(CookedBuffers()).Where(b => declared.Contains(b.Name)).ToArray()) }, "transfer-apply");
        }
        if (transferGatherIds.Length == 0 || transferCheck) ApplyFineNow();   // else at the cook's end, after the gather (FinishSunNodes)
        splitDetailLanded = true;
        splitComposeDirty = true;
        splitComposeAgainFrame = framesRendered + 1;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] transfer: {live.Length:N0} coarse texels lit from their entries and {transferFineCount:N0} fine from theirs in {clock.Elapsed.TotalMilliseconds:0} ms."));
    }
}
