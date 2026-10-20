using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The far field (texel_far.glsl; plan.md, "Short rays, then ask the patch"): a split cook's texel traces what lies near it
// and reads what lies beyond its reach from its patch's record. The noise census (2026-10-14, SponzaLoop.NoiseCensus.cs):
// the 7 M texels the sweep left unsettled had 85% of their variance from beyond 1 m, and 80% of them would settle on
// their near rays alone -- shaded texels finding a little of the sunlit floor across the hall, ray by ray. The patches
// already hold that light; the texels were fetching it again, 16 M times over.
//   THE RECORD PASS  before the sweep, --far-record-rays (128) a texel: every ray past its texel's reach adds its light
//                    into its patch's bin (texel_nodes_shade.glsl, recording). A patch pools ~80 texels' rays. Then
//                    judged (texel_far_judge.comp): a patch whose far light's error is over half the texels' bar gets
//                    another round for its texels, up to --far-record-max (1,024) rays a texel; then a bin still too
//                    uncertain is made unknown, its rays the texels' own. The first build had no such judge: the crevices'
//                    dark corridor, lit through a doorway one bin wide, read a few hundred rays' guess at it, shared by a
//                    patch -- its points 0.056 off the path trace against 0.029 (2026-10-14).
//   THE SWEEP        a ray whose bin the record knows stops at the reach (texel_nodes_rays.comp); one that gets there is
//                    far -- its light the record's. Each texel keeps, per bin, how much of its rays went far
//                    (NodeFarBins); a slice's last step (texel_far_finish.comp) adds the far light to its sums. The settling
//                    then judges the near rays' noise alone.
//   THE BLEND        a texel reads its patch's and its patch's neighbours' records (texel_far_neighbours.comp: seen along
//                    the surface), each patch's estimate in its own bins, weighted smoothly (texel_far.glsl, farSet): one
//                    patch's record a texel stepped where the texel's patch changed -- 0.25 m tiles on a flat wall.
//   THE POOLING      a slice's texels' estimates pooled with their neighbours' evidence (texel_far_pool.comp: Ward's
//                    footprint, compatible by geometry, reach and light; a plane fit; sampling + reuse error to the
//                    settling): a smooth wall settles on its first rays, a contact keeps its own.
// --far-debug far|assign|pool: the cook lands the far light alone, each texel's patches as colours (blended by their
// weights), or how many neighbours each texel pooled -- to see each on its own, against the reference. Never cached as
// the light.
// --no-far-field: every ray its own, as before (the A/B). Only for the split cook (its few columns: the record is a
// patch x 36 bins x (3 + 4 x columns) words -- Sponza's two columns 300 MB).
internal sealed partial class SponzaLoop
{
    private bool farField = true;
    private int farRecordRays = 128, farRecordMax = 1024;
    private const int FarBins = 36;
    private const float FarFixed = 4096f;
    // A bin is known once its rays' weight sums to this (a cosine ray's weight is pi): a few rays' worth.
    private const float FarKnownWeight = 8f * MathF.PI;
    private bool farOn, farRecording, farJudgePending;
    private int farDebug;   // 0 off, 1 the far light alone, 2 the patches as colours, 3 the pool sizes
    private GpuBufferHandle farEvidence, farSliceMap, farHarmonic, farPoolStats;
    private ShaderInterface farPoolInterface = null!;
    private PipelineHandle farPoolPipeline;
    private int farSerial;
    private GpuBufferHandle farNeighbours;
    private ShaderInterface farNeighboursInterface = null!;
    private PipelineHandle farNeighboursPipeline;
    private const int FarSet = 4, FarNeighbours = 8;
    // The pooling's settings in the cache's key (texel_far_pool.comp): 1 Ward's 0.15 over every cell; 2 0.5 H over a
    // 32-point spiral, neighbours counted once.
    // 3: judged against the first look's plane, no reach test, the trace in spatial order.
    // 4: the light test's sigma with the records' error and a 2% tolerance.
    // 5: transport mode's relight from complete lists, its bins' spread kept and judged.
    private const int FarPoolVersion = 5;
    private int farRecordPass, farRecordRound;
    private uint[] farRecordIds = Array.Empty<uint>();
    private GpuBufferHandle farRecordIdsBuffer, farVerdict;
    private int[]? farPatchOf;
    private long farRecordTotalRays;
    private ShaderInterface farJudgeInterface = null!;
    private PipelineHandle farJudgePipeline;
    private readonly System.Diagnostics.Stopwatch farClock = new();
    private GpuBufferHandle farRecord, farBinsBuffer;
    private ShaderInterface farFinishInterface = null!;
    private PassHandle farFinishPass, farPoolPass;
    private PipelineHandle farFinishPipeline;

    private void ReadFarArgs(AppArgs args)
    {
        farField = !args.Flag("no-far-field");
        if (args.Int("far-record-rays") is { } fr) farRecordRays = Math.Max(32, fr / 32 * 32);
        if (args.Int("far-record-max") is { } fm) farRecordMax = Math.Max(farRecordRays, fm);
        farDebug = args.String("far-debug") switch { "far" => 1, "assign" => 2, "pool" => 3, _ => 0 };
    }

    // The cache's key: what changes the far field's answer.
    private string FarKey => farField && sunNodeGuide ? string.Create(CultureInfo.InvariantCulture, $";far {farRecordRays} {farRecordMax} judged w2 blend pool v{FarPoolVersion}{(farTransportMode ? " transport" : "")}{(farDebug > 0 ? $" debug {farDebug}" : "")}") : "";

    private void DeclareFarPasses(Func<string[], ShaderInterface> reflect)
    {
        farFinishInterface = reflect(new[] { "texel_far_finish.comp" });
        farJudgeInterface = reflect(new[] { "texel_far_judge.comp" });
        farNeighboursInterface = reflect(new[] { "texel_far_neighbours.comp" });
        farPoolInterface = reflect(new[] { "texel_far_pool.comp" });
        farFinishPass = graph.ComputePass("sun-nodes-far-finish").Shader(farFinishInterface).Handle;
        farPoolPass = graph.ComputePass("sun-nodes-far-pool").Shader(farPoolInterface).Handle;
    }

    private void CreateFarPipeline(string shaderDir)
    {
        farFinishPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_far_finish.comp.spv")), farFinishInterface, "texel_far_finish")), "texel_far_finish"));
        farJudgePipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_far_judge.comp.spv")), farJudgeInterface, "texel_far_judge")), "texel_far_judge"));
        farNeighboursPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_far_neighbours.comp.spv")), farNeighboursInterface, "texel_far_neighbours")), "texel_far_neighbours"));
        farPoolPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_far_pool.comp.spv")), farPoolInterface, "texel_far_pool")), "texel_far_pool"));
    }

    // UploadSunNodes: on for a guided split cook of a few columns (the guide patch is the far field's when a texel saw none).
    private void BeginFarField(int suns)
    {
        // --live-texels: only with the complete far lists (the GI bundle's): relit for each light in milliseconds, no record
        // pass. Without them the record is a pass over every texel before the sweep (minutes), per light -- off.
        farOn = farField && splitCooking && sunNodeGuided && !sunNodeExact && sunNodeStride == 1 && suns <= 3 && cookedPacked is not null
            && frameTracer is not null && (!liveTexels || (farTransportMode && transportBuildFarLists is not null));
        farRecording = farOn;
        farRecordPass = 0;
        farRecordRound = 0;
        farJudgePending = false;
        farPatchOf = null;
        farRecordTotalRays = 0;
        if (!farOn)
        {
            Console.WriteLine($"[VulkanSponza] far field: off{(farField ? " (not a guided split cook of up to 3 columns)" : " (--no-far-field)")}.");
            // The patches' neighbours all the same, for the hits' bounce blend.
            if (hitBlend && cookedPacked is not null && frameTracer is not null && farNeighbours.Equals(default(GpuBufferHandle)))
                FindFarNeighbours(cookedPacked.Length / 3);
            return;
        }
        var patches = cookedPacked!.Length / 3;
        var bytes = checked((int)((long)patches * FarBins * (3 + 4 * suns) * 4));
        farRecord = Own(device.CreateGpuBuffer(bytes, new byte[bytes], "sponza.far.record"));
        farVerdict = Own(device.CreateGpuBuffer(patches * 4, name: "sponza.far.verdict"));
        farRecordIds = sunNodeTraceIds;
        farRecordIdsBuffer = sunNodeTraceBuffer;
        farBinsBuffer = Own(device.CreateGpuBuffer(checked(sunNodeSlice * FarSet * FarBins * 16), name: "sponza.far.bins"));
        FindFarNeighbours(patches);
        farEvidence = Own(device.CreateGpuBuffer(checked(sunNodeSlice * 7 * 16), name: "sponza.far.evidence"));
        farSliceMap = Own(device.CreateGpuBuffer(checked(texelCount * 4), new byte[texelCount * 4], "sponza.far.slice-map"));
        farHarmonic = Own(device.CreateGpuBuffer(checked(sunNodeSlice * 8), name: "sponza.far.harmonic"));
        farPoolStats = Own(device.CreateGpuBuffer(71 * 4, new byte[71 * 4], "sponza.far.pool-stats"));
        farSerial = 0;
        farClock.Restart();
        // Transport mode with a record kept from an earlier cook: relit, not recorded.
        if (BeginFarTransport(patches, suns))
        {
            farRecording = false;
            return;
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] far field: on; recording {farRecordRays} rays a texel into {patches:N0} patches' records ({bytes / 1048576.0:0} MB) before the sweep."));
    }

    // Each patch's neighbours it can see along the surface, once a cook (texel_far_neighbours.comp), in chunks the rays'
    // buffer holds (FarNeighbours rays a patch).
    // A stage of the GI bundle (--cook-gi's, under the patches' key): the geometry's alone.
    private void FindFarNeighbours(int patches)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var header = string.Create(CultureInfo.InvariantCulture, $"neighbours {FarNeighbours} spacing {transportSpacing:G6} patches {patches}");
        if (transportPatchesKey is { } key && LoadStage(key, "far-neighbours", r => r.ReadString() != header ? null : ReadArray<int>(r)) is { } stored)
        {
            farNeighbours = Own(device.CreateGpuBuffer(checked(patches * FarNeighbours * 4), MemoryMarshal.AsBytes(stored.AsSpan()), "sponza.far.neighbours"));
            return;
        }
        farNeighbours = Own(device.CreateGpuBuffer(checked(patches * FarNeighbours * 4), name: "sponza.far.neighbours"));
        var declared = farNeighboursInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        var chunk = sunNodeSlice * 32 / FarNeighbours;
        for (var first = 0; first < patches; first += chunk)
        {
            var count = Math.Min(chunk, patches - first);
            DispatchCommand Pass(int mode) => new(farNeighboursPipeline, (count + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uNeighbours", new Vector4Uniform(new Vector4(first, count, mode, transportSpacing))),
                    new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                    new("uCookedDims", new Vector4Uniform(cookedDims)),
                },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new[]
                {
                    new ShaderBufferBinding("NodeRays", sunNodeRaysBuffer),
                    new ShaderBufferBinding("NodeHits", sunNodeHits),
                    new ShaderBufferBinding("PatchNeighbours", farNeighbours),
                }.Concat(CookedBuffers()).Where(b => declared.Contains(b.Name)).ToArray());
            device.DispatchNow(new[] { Pass(0) }, "sun-nodes-far-neighbours");
            frameTracer!.Trace(sunNodeRaysBuffer, sunNodeHits, count * FarNeighbours, 0x7E1Bu, closestOnly: true);
            device.DispatchNow(new[] { Pass(1) }, "sun-nodes-far-neighbours-keep");
        }
        device.WaitIdle();
        var list = MemoryMarshal.Cast<byte, int>(device.ReadGpuBuffer(farNeighbours, 0, patches * FarNeighbours * 4).AsSpan()).ToArray();
        long kept = 0;
        foreach (var n in list) if (n >= 0) kept++;
        if (transportPatchesKey is { } saveKey) SaveStage(saveKey, "far-neighbours", w => { w.Write(header); WriteArray(w, list); });
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] far field: each patch's neighbours seen along the surface, {kept / (double)patches:0.0} a patch, in {clock.Elapsed.TotalSeconds:0.0} s."));
    }

    // The trace's order made spatial (Morton over half-metre cells, then the normal's major axis), within the texels on
    // screen and within the rest -- so a texel's neighbours trace in its slice and the pooling finds their evidence. In
    // the visible-first order 41% of the neighbours the crevices' texels found were in another slice (2026-10-14).
    // Each texel's place key, and every texel in its order, once a session: a texel does not move, so a cook after the
    // first (a live restart, SponzaLoop.LivePatchLight.cs) orders only the texels on screen -- the rest come in the
    // whole order with those left out. It read the texels back and sorted 16 M keys again each time (~8 s).
    private uint[]? farPlaceKey, farPlaceOrder;

    private uint[] FarSpatialOrder(uint[] order, int front)
    {
        using var profScope = Prof("far-order");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (farPlaceKey is { } placeKey && farPlaceOrder is { } placeOrder && placeKey.Length == texelCount)
        {
            var head = order.AsSpan(0, front).ToArray();
            var headKeys = head.Select(id => placeKey[id]).ToArray();
            Array.Sort(headKeys, head);
            var onScreen = new bool[texelCount];
            foreach (var id in head) onScreen[id] = true;
            var inOrder = new bool[texelCount];
            for (var i = front; i < order.Length; i++) inOrder[order[i]] = true;
            var ordered = new uint[order.Length];
            head.CopyTo(ordered, 0);
            var at = front;
            foreach (var id in placeOrder)
                if (inOrder[id] && !onScreen[id]) ordered[at++] = id;
            if (at == order.Length)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] far field: the trace ordered by place ({front:N0} on screen, then {order.Length - front:N0}; the place order kept) in {clock.Elapsed.TotalSeconds:0.0} s."));
                return ordered;
            }
        }
        // The keys are the geometry's: from the GI bundle when made before for the same texels, else made (the texels read
        // back, 22 s of Sponza's boot before the instant layer showed, 2026-10-19) and kept there.
        var keyPath = PlaceKeyPath();
        var key = keyPath is not null ? ReadPlaceKeys(keyPath) : null;
        var keyRead = key is not null;
        key ??= new uint[texelCount];
        var grid = texelGrid;
        const int chunk = 1 << 20;
        for (var first = 0; first < texelCount && !keyRead; first += chunk)
        {
            var count = Math.Min(chunk, texelCount - first);
            var records = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelBuffer, checked(first * 32), count * 32).AsSpan()).ToArray();
            Parallel.For(0, count, i =>
            {
                var p = records[2 * i];
                var n = records[2 * i + 1];
                static uint Spread(uint v) { v &= 0x3FF; v = (v | (v << 16)) & 0x030000FF; v = (v | (v << 8)) & 0x0300F00F; v = (v | (v << 4)) & 0x030C30C3; return (v | (v << 2)) & 0x09249249; }
                uint Cell(float v, float o) => (uint)Math.Clamp((int)MathF.Floor((v - o) / 0.5f), 0, 1023);
                var ax = MathF.Abs(n.X) >= MathF.Abs(n.Y) && MathF.Abs(n.X) >= MathF.Abs(n.Z) ? 0u : MathF.Abs(n.Y) >= MathF.Abs(n.Z) ? 1u : 2u;
                var sign = (ax == 0 ? n.X : ax == 1 ? n.Y : n.Z) < 0f ? 1u : 0u;
                var morton = Spread(Cell(p.X, grid.X)) | (Spread(Cell(p.Y, grid.Y)) << 1) | (Spread(Cell(p.Z, grid.Z)) << 2);
                key[first + i] = (morton << 2) ^ (ax * 2 + sign);   // 30 bits of cell, the face in the low bits' mix
            });
        }
        if (!keyRead && keyPath is not null) WritePlaceKeys(keyPath, key);
        var ids = (uint[])order.Clone();
        var keys = new ulong[ids.Length];
        for (var i = 0; i < ids.Length; i++) keys[i] = ((ulong)key[ids[i]] << 32) | (uint)i;   // stable: ties keep the old order
        Array.Sort(keys, ids, 0, front);
        Array.Sort(keys, ids, front, ids.Length - front);
        // Kept for the next cook: every texel of this order by its key alone.
        farPlaceKey = key;
        var all = (uint[])order.Clone();
        var allKeys = all.Select(id => key[id]).ToArray();
        Array.Sort(allKeys, all);
        farPlaceOrder = all;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] far field: the trace ordered by place ({front:N0} on screen, then {ids.Length - front:N0}; the keys {(keyRead ? "read from the GI bundle" : "made")}) in {clock.Elapsed.TotalSeconds:0.0} s."));
        return ids;
    }

    private const int PlaceKeyVersion = 1;

    // The place keys' file beside the GI bundle's stages: the texels' stages and count, the grid, the key's version.
    private string? PlaceKeyPath()
    {
        if (transportCombinedKey is not { } combined) return null;
        var g = texelGrid;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{combined};texels {texelCount};grid {g.X},{g.Y},{g.Z},{g.W};v{PlaceKeyVersion}"))))[..24].ToLowerInvariant();
        return Path.Combine(transportCacheDir, key + ".texel-place");
    }

    private uint[]? ReadPlaceKeys(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var r = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 22));
            if (r.ReadString() != "blix-texel-place" || r.ReadInt32() != PlaceKeyVersion || r.ReadInt32() != texelCount) return null;
            var keys = new uint[texelCount];
            var bytes = MemoryMarshal.AsBytes(keys.AsSpan());
            return r.BaseStream.Read(bytes) == bytes.Length ? keys : null;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException) { return null; }
    }

    private void WritePlaceKeys(string path, uint[] keys)
    {
        var count = texelCount;
        _ = Task.Run(() =>
        {
            try
            {
                var temp = path + ".partial";
                using (var w = new BinaryWriter(new BufferedStream(File.Create(temp), 1 << 22)))
                {
                    w.Write("blix-texel-place"); w.Write(PlaceKeyVersion); w.Write(count);
                    w.Write(MemoryMarshal.AsBytes(keys.AsSpan()));
                }
                File.Move(temp, path, overwrite: true);
                Console.WriteLine($"[VulkanSponza] far field: the texels' place keys written to {path}.");
            }
            catch (IOException e) { Console.WriteLine($"[VulkanSponza] far field: {path} not written ({e.Message})."); }
        });
    }

    private void ReleaseFarField()
    {
        foreach (var b in new[] { farRecord, farBinsBuffer, farVerdict, farNeighbours, farEvidence, farSliceMap, farHarmonic, farPoolStats })
            if (!b.Equals(default(GpuBufferHandle))) RetireGpuBuffer(b);
        if (!farRecordIdsBuffer.Equals(sunNodeTraceBuffer) && !farRecordIdsBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(farRecordIdsBuffer);
        farRecord = farBinsBuffer = farVerdict = farRecordIdsBuffer = farNeighbours = default;
        farEvidence = farSliceMap = farHarmonic = farPoolStats = default;
        farRecordIds = Array.Empty<uint>();
        farPatchOf = null;
        farOn = farRecording = false;
    }

    // The uniform the rays and the shade take: 0 off (and while discovering), 1 recording, 2 reading.
    private Vector4 FarUniform(int discover) => new(
        !farOn || discover > 0 ? 0f : farRecording ? 1f : 2f,
        transportSpacing,
        FarKnownWeight * FarFixed,
        sunNodeSuns?.Length ?? 0);

    // The record's buffer where a shader declares it (a placeholder when off: never indexed).
    private GpuBufferHandle FarRecordBuffer => farOn ? farRecord : sunNodeAccum;
    private GpuBufferHandle FarBinsBuffer => farOn ? farBinsBuffer : sunNodeAccum;
    // Bound wherever made: the far field's, and the hits' bounce blend (HitBlendNow) without it.
    private GpuBufferHandle FarNeighboursBuffer => !farNeighbours.Equals(default(GpuBufferHandle)) ? farNeighbours : sunNodeAccum;
    // A gather hit's bounce blended over its patch's neighbours (texel_nodes_shade.glsl, hitPatchLight): flat from the one
    // patch, the bounce near a patch-lit surface took the patches' outlines -- blotches on the Cornell box's walls where the
    // path trace is smooth (2026-10-21). --no-hit-blend: one patch, as before.
    private bool hitBlend = true;
    private bool HitBlendNow => hitBlend && !farNeighbours.Equals(default(GpuBufferHandle));
    private GpuBufferHandle FarHarmonicBuffer => farOn ? farHarmonic : sunNodeAccum;

    // The record pass, a pass at a time (immediate or in the frame): a round's texels in slices, farRecordRays / 32 passes
    // each; nothing read back. A round done, the patches judged (the next call: the frame's passes run by then); those
    // that need more give their texels another round, until none does or the texels have farRecordMax rays. Then the
    // record's last word, and the sweep.
    private void FarRecordStep(bool now)
    {
        if (farJudgePending)
        {
            FarJudgeRound();
            return;
        }
        var live = farRecordIds;
        var slice = Math.Min(sunNodeSlice, live.Length - sunNodeFirst);
        var passes = farRecordRays / 32;
        var seedPass = 4096 + farRecordRound * passes + farRecordPass;
        if (now) SunNodePassNow(farRecordIdsBuffer, sunNodeFirst, slice, farRecordPass, seedPass);
        else DispatchSunNodePass(farRecordIdsBuffer, sunNodeFirst, slice, farRecordPass, discover: 0, seedPass);
        if (++farRecordPass < passes) return;
        farRecordPass = 0;
        sunNodeFirst += slice;
        if (sunNodeFirst < live.Length) return;
        farRecordTotalRays += (long)live.Length * farRecordRays;
        farJudgePending = true;
    }

    private DispatchCommand FarJudgeCommand(int mode)
    {
        var patches = cookedPacked!.Length / 3;
        var declared = farJudgeInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        return new DispatchCommand(farJudgePipeline, (patches + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uJudge", new Vector4Uniform(new Vector4(mode, texelNoise * 0.5f, patches, FarKnownWeight * FarFixed))),
                new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                new("uCookedDims", new Vector4Uniform(cookedDims)),
                new("uFar", new Vector4Uniform(FarUniform(0))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("PatchVerdict", farVerdict),
                new ShaderBufferBinding("FarRecord", farRecord),
            }.Concat(CookedBuffers()).Where(b => declared.Contains(b.Name)).ToArray());
    }

    private void FarJudgeRound()
    {
        farJudgePending = false;
        device.WaitIdle();
        var patches = cookedPacked!.Length / 3;
        device.DispatchNow(new[] { FarJudgeCommand(0) }, "sun-nodes-far-judge");
        var verdict = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(farVerdict, 0, patches * 4).AsSpan()).ToArray();
        // Each texel's patch, as the shaders pick it (its nearest patch it can see, else its guide's), read once.
        if (farPatchOf is null)
        {
            var seen = MemoryMarshal.Cast<byte, int>(device.ReadGpuBuffer(sunNodeTexelPatch, 0, texelCount * 4).AsSpan()).ToArray();
            var guide = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(sunNodeGuidePatch, 0, texelCount * 4).AsSpan()).ToArray();
            farPatchOf = new int[texelCount];
            for (var i = 0; i < texelCount; i++) farPatchOf[i] = seen[i] >= 0 ? seen[i] : (int)guide[i] - 2;
        }
        var patchOf = farPatchOf;
        var needing = 0;
        foreach (var v in verdict) if (v != 0) needing++;
        var next = farRecordIds.Where(id => patchOf[id] >= 0 && verdict[patchOf[id]] != 0).ToArray();
        farRecordRound++;
        var raysSoFar = farRecordRound * farRecordRays;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] far field: record round {farRecordRound} ({raysSoFar} rays a texel): {patches - needing:N0} of {patches:N0} patches' far light within {texelNoise * 50:0.#}%; {next.Length:N0} texels {(next.Length > 0 && raysSoFar < farRecordMax ? "record more" : "left as they are")} ({farClock.Elapsed.TotalMinutes:0.0} min)."));
        sunNodeFirst = 0;
        sunNodePass = 0;
        if (next.Length > 0 && raysSoFar < farRecordMax)
        {
            if (!farRecordIdsBuffer.Equals(sunNodeTraceBuffer)) RetireGpuBuffer(farRecordIdsBuffer);
            farRecordIds = next;
            farRecordIdsBuffer = Own(device.CreateGpuBuffer(next.Length * 4, MemoryMarshal.AsBytes(next.AsSpan()), "sponza.far.record-ids"));
            return;
        }
        device.DispatchNow(new[] { FarJudgeCommand(1) }, "sun-nodes-far-last-word");
        device.WaitIdle();
        farRecording = false;
        farPatchOf = null;   // 64 MB a 16 M-texel scene: only the rounds need it
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] far field: recorded in {farClock.Elapsed.TotalMinutes:0.0} min ({farRecordTotalRays / 1e9:0.00} G rays, {farRecordRound} rounds); uncertain bins left to the texels' own rays; the sweep reads it."));
    }

    // A slice's passes done (the sweep's or a round's): its texels' far light into their sums, before they are read back.
    private DispatchCommand FarFinishCommand(GpuBufferHandle ids, int first, int slice, int passes)
    {
        var declared = farFinishInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        return new DispatchCommand(farFinishPipeline, (slice + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uFinish", new Vector4Uniform(new Vector4(first, slice, passes, sunNodeSuns!.Length))),
                new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                new("uCookedDims", new Vector4Uniform(cookedDims)),
                new("uFar", new Vector4Uniform(FarUniform(0))),
                new("uDebug", new Vector4Uniform(new Vector4(farDebug <= 2 ? farDebug : 0, farSerial, skyKept ? SunColumn() + 1 : 0f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("PatchNeighbours", farNeighbours),
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("NodeIds", ids),
                new ShaderBufferBinding("TexelPatch", sunNodeTexelPatch),
                new ShaderBufferBinding("NodeGuidePatch", sunNodeGuidePatch),
                new ShaderBufferBinding("NodeFarBins", farBinsBuffer),
                new ShaderBufferBinding("NodeAccum", sunNodeAccum),
                new ShaderBufferBinding("FarRecord", farRecord),
                new ShaderBufferBinding("FarEvidence", farEvidence),
                new ShaderBufferBinding("FarSliceMap", farSliceMap),
                new ShaderBufferBinding("NodeHarmonic", farHarmonic),
            }.Concat(CookedBuffers()).Where(b => declared.Contains(b.Name)).ToArray());
    }

    private DispatchCommand FarPoolCommand(GpuBufferHandle ids, int first, int slice, int passes)
    {
        var declared = farPoolInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        return new DispatchCommand(farPoolPipeline, (slice + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uPool", new Vector4Uniform(new Vector4(first, slice, passes, sunNodeSuns!.Length))),
                new("uTexelGrid", new Vector4Uniform(texelGrid)),
                new("uSerial", new Vector4Uniform(new Vector4(farSerial, sunNodeHashSlots, farDebug, skyKept ? SunColumn() + 1 : 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("NodeIds", ids),
                new ShaderBufferBinding("SunNodeHash", sunNodeHash),
                new ShaderBufferBinding("FarSliceMap", farSliceMap),
                new ShaderBufferBinding("FarEvidence", farEvidence),
                new ShaderBufferBinding("NodeAccum", sunNodeAccum),
                new ShaderBufferBinding("FarPoolStats", farPoolStats),
            }.Where(b => declared.Contains(b.Name)).ToArray());
    }

    // After the sweep and at the end: how many neighbours the texels pooled, and what that makes of the scene -- the
    // independent estimates it needed (a texel pooling n neighbours counts 1 / (n + 1)).
    private void ReportFarPool(string when)
    {
        if (!farOn || farPoolStats.Equals(default(GpuBufferHandle))) return;
        device.WaitIdle();
        var counts = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(farPoolStats, 0, 71 * 4).AsSpan()).ToArray();
        device.WriteGpuBuffer(farPoolStats, 0, new byte[71 * 4]);
        long texels = 0, alone = 0;
        double neighbours = 0, independent = 0;
        for (var n = 0; n <= 64; n++)
        {
            texels += counts[n];
            neighbours += (double)n * counts[n];
            independent += counts[n] / (n + 1.0);
            if (n == 0) alone = counts[n];
        }
        if (texels == 0) return;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] far field, pooled {when}: {texels:N0} texels, {neighbours / texels:0.0} neighbours each on average, {100.0 * alone / texels:0.0}% alone; ~{independent:N0} independent estimates ({independent / texels * 100:0.0}% of the texels); of {counts[65]:N0} neighbours found, {Share(66)} in another slice, {Share(67)} off the surface, {Share(68)} turned, {Share(69)} another reach, {Share(70)} another light (the second look)."));
        string Share(int k) => counts[65] > 0 ? string.Create(CultureInfo.InvariantCulture, $"{100.0 * counts[k] / counts[65]:0.0}%") : "-";
    }

    private void FarFinish(bool now, GpuBufferHandle ids, int first, int slice, int passes)
    {
        if (!farOn || farRecording) return;
        farSerial = farSerial % 65535 + 1;
        var command = FarFinishCommand(ids, first, slice, passes);
        // The diagnostics that land the far light or the blend skip the pooling (it would overwrite them).
        var pool = farDebug is 0 or 3 && Environment.GetEnvironmentVariable("BLIX_FAR_NOPOOL") != "1" ? FarPoolCommand(ids, first, slice, passes) : (DispatchCommand?)null;
        if (now)
        {
            device.DispatchNow(new[] { command }, "sun-nodes-far-finish");
            if (pool is { } p) device.DispatchNow(new[] { p }, "sun-nodes-far-pool");
        }
        else
        {
            graph.Dispatch(farFinishPass, command);
            if (pool is { } p) graph.Dispatch(farPoolPass, p);
        }
    }
}
