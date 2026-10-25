using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --live-texels on the host: a traced slice resolved on the GPU (slice_resolve.comp) instead of read back -- its sums
// into each texel's running state, its light and detail into the parts, the refine's judgement, the unsettled appended
// for the next round. ProcessSlice in C# cost 110 ms a slice of 32 K texels: 57 s of Sponza's 3.6 min light change
// (2026-10-19). The CPU reads a count a round and, after the sweep, the light's histogram (the dark bar). --cpu-resolve:
// the C# path, as before.
internal sealed partial class SponzaLoop
{
    private ShaderInterface sliceResolveInterface = null!;
    private PipelineHandle sliceResolvePipeline;
    private bool cpuResolve;
    // The running state by place in the sweep's list (4 floats a column, the passes); two lists of the unsettled (ids and
    // places) the rounds trade; their counts; the sweep's light histogram.
    private GpuBufferHandle resolveState, resolvePasses, resolveCount, resolveHistogram;
    private readonly GpuBufferHandle[] resolveIds = new GpuBufferHandle[2], resolvePlaces = new GpuBufferHandle[2];
    private int resolveCapacity, resolveNext;
    private bool gpuResolving;
    // A sun change keeps the sky's part (gathered whole at boot): the gather writes and judges the sun's alone, and
    // neither the instant layer nor the fine copy touches the sky's (2026-10-19). --regather-sky: both parts again.
    private bool liveSkyGathered, skyKept, regatherSky;
    // The fine round's refine mask (a bit a texel): its contact-flagged texels alone refine.
    private GpuBufferHandle refineMaskBuffer;
    private bool refineMasked;
    // The pooled stop (default; --no-settle-pool: each texel's own noise alone, as before) (slice_resolve.comp; tools/gi-model/settle_rule.py) -- each sweep pools its texels'
    // relative noise by patch, and the whole sweep is judged again against it and the bar.
    private bool settlePool;
    private GpuBufferHandle resolvePool;
    // --near-glow-split: the sheets' near walls' glow kept apart per texel (slice_resolve.comp GlowPart).
    private bool nearGlowSplit;
    private GpuBufferHandle sunNodeGlow, resolveGlowState, texelGlowPart;
    private bool NearGlowSplitNow => nearGlowSplit && !sunNodeGlow.Equals(default(GpuBufferHandle)) && !texelGlowPart.Equals(default(GpuBufferHandle))
        && !resolveGlowState.Equals(default(GpuBufferHandle));
    private bool SettlePoolNow => settlePool && !liveTexelPatch.Equals(default(GpuBufferHandle)) && cookedPacked is not null;

    private bool GpuResolveOn => liveTexels && !cpuResolve && SunNodeRefines && sunNodeSuns is { Length: > 0 };

    // The sweep begins: buffers for its list (kept while big enough), the counts and the histogram cleared.
    private void BeginGpuResolve()
    {
        var n = sunNodeTraceIds.Length;
        var suns = sunNodeSuns!.Length;
        if (n > resolveCapacity)
        {
            foreach (var b in new[] { resolveState, resolvePasses, resolveIds[0], resolveIds[1], resolvePlaces[0], resolvePlaces[1] })
                if (!b.Equals(default(GpuBufferHandle))) RetireGpuBuffer(b);
            resolveState = Own(SweepBuffer(checked(n * suns * 16), default, "sponza.resolve.state"));
            if (!resolveGlowState.Equals(default(GpuBufferHandle))) RetireGpuBuffer(resolveGlowState);
            resolveGlowState = Own(SweepBuffer(checked(n * 16), default, "sponza.resolve.glow-state"));
            resolvePasses = Own(SweepBuffer(n * 4, default, "sponza.resolve.passes"));
            // Readable where they lie (ReadMappedGpuBuffer): the in-frame sweep's round end sorts them without a wait
            // (SponzaLoop.SweepFrame.cs).
            for (var i = 0; i < 2; i++)
            {
                resolveIds[i] = Own(device.CreateReadableGpuBuffer(n * 4, "sponza.resolve.ids"));
                resolvePlaces[i] = Own(device.CreateReadableGpuBuffer(n * 4, "sponza.resolve.places"));
            }
            resolveCapacity = n;
            EnsureResolveScores(n);
        }
        if (resolveCount.Equals(default(GpuBufferHandle))) resolveCount = Own(device.CreateReadableGpuBuffer(16, "sponza.resolve.count"));
        if (resolveHistogram.Equals(default(GpuBufferHandle))) resolveHistogram = Own(device.CreateReadableGpuBuffer(512, "sponza.resolve.histogram"));
        device.WriteMappedGpuBuffer(resolveCount, 0, new byte[16]);
        device.WriteMappedGpuBuffer(resolveHistogram, 0, new byte[512]);
        if (SettlePoolNow)
        {
            var bytes = Math.Max(32, cookedPacked!.Length / 3 * 32);   // 8 words a patch (slice_resolve.comp, poolAdd)
            if (resolvePool.Equals(default(GpuBufferHandle))) resolvePool = Own(SweepBuffer(bytes, default, "sponza.resolve.pool"));
            else if (sweepInFrame) device.WriteMappedGpuBuffer(resolvePool, 0, new byte[bytes]);
            else device.WriteGpuBuffer(resolvePool, 0, new byte[bytes]);
        }
        resolveNext = 0;
        gpuResolving = true;
    }

    private int SunColumn() => Array.IndexOf(sunNodeKinds, 1);

    // inFrame: recorded in that pass of the frame (SponzaLoop.SweepFrame.cs) rather than run at once.
    private void ResolveDispatch(int mode, int first, int count, int passes, bool refining, GpuBufferHandle ids, GpuBufferHandle places, float bar,
        PassHandle? inFrame = null)
    {
        if (count == 0) return;
        var suns = sunNodeSuns!.Length;
        var irradiance = EffectiveSunIrradiance;
        var inverse = new Vector3(1f / MathF.Max(irradiance.X, 1e-6f), 1f / MathF.Max(irradiance.Y, 1e-6f), 1f / MathF.Max(irradiance.Z, 1e-6f));
        var detail = mode == 0 && !refining && !sunNodeDetail.Equals(default(GpuBufferHandle));
        var skyDetail = EnsureDetailBuffer(sunPart: false);
        var sunDetail = EnsureDetailBuffer(sunPart: true);
        var declared = sliceResolveInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        var command = new DispatchCommand(sliceResolvePipeline, (count + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uSlice", new Vector4Uniform(new Vector4(first, count, passes, refining ? 1f : 0f))),
                new("uSettle", new Vector4Uniform(new Vector4(texelMaxRays, texelNoise, TexelNoiseFloor, bar))),
                new("uSun", new Vector4Uniform(new Vector4(inverse, sunNodeRays / 32))),
                new("uMode", new Vector4Uniform(new Vector4(mode, suns, SunColumn(), detail ? 1f : 0f))),
                new("uKeep", new Vector4Uniform(new Vector4(skyKept ? 1f : 0f, refineMasked && fineGathering ? 1f : 0f, GatherProvenanceCode, SettlePoolNow ? 1f : 0f))),
                new("uGlow", new Vector4Uniform(new Vector4(NearGlowSplitNow ? 1f : 0f, RefineWorstUniform, 0f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RoundIds", ids),
                new ShaderBufferBinding("RoundPlaces", places),
                new ShaderBufferBinding("Accum", sunNodeAccum),
                new ShaderBufferBinding("DetailAccum", detail ? sunNodeDetail : sunNodeAccum),
                new ShaderBufferBinding("State", resolveState),
                new ShaderBufferBinding("StatePasses", resolvePasses),
                new ShaderBufferBinding("SplitSky", EnsureLivePart(false)),
                new ShaderBufferBinding("SplitSun", EnsureLivePart(true)),
                new ShaderBufferBinding("SkyDetail", skyDetail),
                new ShaderBufferBinding("SunDetail", sunDetail),
                new ShaderBufferBinding("Next", resolveIds[resolveNext]),
                new ShaderBufferBinding("NextPlaces", resolvePlaces[resolveNext]),
                new ShaderBufferBinding("NextScores", ScoresBinding(resolveCount)),
                new ShaderBufferBinding("Histogram", resolveHistogram),
                new ShaderBufferBinding("NextCount", resolveCount),
                new ShaderBufferBinding("RefineMask", refineMaskBuffer.Equals(default(GpuBufferHandle)) ? resolveCount : refineMaskBuffer),
                ProvenanceBinding(),
                TexelRaysBinding(),
                new ShaderBufferBinding("TexelPatch", SettlePoolNow ? liveTexelPatch : resolveCount),
                new ShaderBufferBinding("Pool", SettlePoolNow ? resolvePool : resolveCount),
                new ShaderBufferBinding("NodeGlow", NearGlowSplitNow ? sunNodeGlow : resolveCount),
                new ShaderBufferBinding("GlowState", NearGlowSplitNow ? resolveGlowState : resolveCount),
                new ShaderBufferBinding("GlowPart", NearGlowSplitNow ? texelGlowPart : resolveCount),
            }.Where(b => declared.Contains(b.Name)).ToArray());
        if (inFrame is { } pass) graph.Dispatch(pass, command);
        else device.DispatchNow(new[] { command }, "slice-resolve");
    }

    private GpuBufferHandle EnsureDetailBuffer(bool sunPart)
    {
        return PartDetail(ref sunPart ? ref splitSunDetailBuffer : ref splitSkyDetailBuffer, sunPart);
    }

    // A slice's passes done: resolved where it lies, and on. False when the sweep or a round ended here.
    private bool ResolveSliceNow(int slice, int passes)
    {
        var refining = sunNodeRefineRound > 0;
        // The round's list: the sweep's ids (its place is its index), a round's the list the last one appended.
        var ids = refining ? sunNodeRefineBuffer : sunNodeTraceBuffer;
        var places = refining ? resolvePlaces[1 - resolveNext] : sunNodeTraceBuffer;
        ResolveDispatch(0, sunNodeFirst, slice, passes, refining, ids, places, sunNodeDarkBar);
        splitComposeDirty = true;
        splitComposeAgainFrame = framesRendered + 1;
        sunNodeFirst += slice;
        var length = refining ? sunNodeRefineIds.Length : sunNodeTraceIds.Length;
        if (sunNodeFirst < length) return true;
        if (BeginGpuRefineRound()) return false;
        gpuResolving = false;
        FinishSunNodes();
        return false;
    }

    private int ReadResolveCount()
    {
        device.WaitIdle();
        return (int)MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(resolveCount, 0, 4).AsSpan())[0];
    }

    // BeginSunNodeRefineRound on the GPU's lists: after the sweep the dark bar from its histogram and its unsettled judged
    // again; then a round over what is left, or none.
    private bool BeginGpuRefineRound()
    {
        using var profScope = Prof("gather.round-end");
        var left = ReadResolveCount();
        var of = sunNodeRefineRound == 0 ? sunNodeTraceIds.Length : sunNodeRefineIds.Length;
        // The pooled stop: the whole sweep judged again (the pool complete now), whatever its slices appended.
        var pooled = SettlePoolNow;
        if (sunNodeRefineRound == 0 && (left > 0 || pooled))
        {
            var histogram = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(resolveHistogram, 0, 512).AsSpan()).ToArray();
            long total = 0, seen = 0;
            foreach (var c in histogram) total += c;
            for (var b = 0; b < histogram.Length && texelDarkFloor > 0f; b++)
            {
                seen += histogram[b];
                if (seen * 2 < total) continue;
                sunNodeDarkBar = texelDarkFloor * MathF.Pow(2f, (b + 0.5f) / 4f - 24f);
                break;
            }
            // The sweep's unsettled (list resolveNext) judged against the bar, into the other.
            var judged = resolveNext;
            resolveNext = 1 - resolveNext;
            device.WriteMappedGpuBuffer(resolveCount, 0, new byte[16]);
            if (pooled) ResolveDispatch(1, 0, of, 0, false, sunNodeTraceBuffer, sunNodeTraceBuffer, sunNodeDarkBar);
            else ResolveDispatch(1, 0, left, 0, false, resolveIds[judged], resolvePlaces[judged], sunNodeDarkBar);
            var after = ReadResolveCount();
            Console.WriteLine(pooled
                ? string.Create(Inv, $"[VulkanSponza] sun nodes: the dark bar {sunNodeDarkBar:G3} ({texelDarkFloor:G2} of the median texel light); the whole sweep judged again with its patches' pooled noise: {after:N0} refine (its slices alone: {left:N0}).")
                : string.Create(Inv, $"[VulkanSponza] sun nodes: the dark bar {sunNodeDarkBar:G3} ({texelDarkFloor:G2} of the median texel light); judged again, {left - after:N0} more texels settled."));
            left = after;
        }
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sun nodes: {of - left:N0} of {of:N0} texels settled after {(sunNodeRefineRound == 0 ? "the sweep" : $"round {sunNodeRefineRound}")} ({sunNodeClock.Elapsed.TotalMinutes:0.0} min); {left:N0} refine (resolved on the GPU)."));
        DumpRefineAudit(of, left);
        // --refine-worst: a round that settled all it traced goes on while the pool holds candidates.
        if (left == 0 && !(refineWorst && sunNodeRefineRound > 0 && worstPoolIds.Length > 0)) return false;
        if (RefineStops(of, left))
        {
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] sun nodes: round {sunNodeRefineRound} settled {of - left:N0} of {of:N0} ({RefineStopsWhy(of, left)}): refining stops; {left:N0} keep their light as it is."));
            return false;
        }
        // The list just appended is the round's; the next round appends into the other. In id order: the appends land as
        // the threads meet (slice_resolve.comp, append), and a round of more than one slice reads the texels its earlier
        // slices resolved -- which slice a texel fell in moved its light run to run (464 texels apart at 1,024-texel slices,
        // 2026-10-22; Sponza's rounds span hundreds of slices).
        if (refineWorst) left = (sunNodeRefineIds = SelectWorstRound(resolveNext, left)).Length;
        else sunNodeRefineIds = SortResolveList(resolveNext, left);
        sunNodeRefineBuffer = resolveIds[resolveNext];
        resolveNext = 1 - resolveNext;
        device.WriteMappedGpuBuffer(resolveCount, 0, new byte[16]);
        sunNodeRefineRound++;
        sunNodeRefineRays += (long)left * RefinePasses * 32;
        sunNodeFirst = 0;
        sunNodePass = 0;
        return true;
    }

    // BLIX_REFINE_AUDIT=<prefix> (a measurement): at each round's end the round's texels and what the resolve judged them
    // by -- ids, places, the state's sums and moments, passes, the list it appended (the unsettled), the settle uniforms;
    // the patches' pools, the texels' patches and the sky part once a gather (tools: the refine audit).
    private static readonly string? refineAudit = Environment.GetEnvironmentVariable("BLIX_REFINE_AUDIT");
    private int refineAuditGather = -1;

    private void DumpRefineAudit(int of, int left)
    {
        if (refineAudit is null) return;
        device.WaitIdle();
        var suns = sunNodeSuns!.Length;
        var refining = sunNodeRefineRound > 0;
        if (!refining) refineAuditGather++;   // a gather's sweep: the coarse, the fine round, a near round, a sun change's
        var ids = refining ? sunNodeRefineIds : sunNodeTraceIds;
        var path = string.Create(Inv, $"{refineAudit}.g{refineAuditGather}.r{sunNodeRefineRound}");
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(sunNodeRefineRound); w.Write(of); w.Write(left); w.Write(suns); w.Write(SunColumn());
            w.Write((float)texelMaxRays); w.Write(texelNoise); w.Write(TexelNoiseFloor); w.Write(sunNodeDarkBar);
            w.Write(skyKept ? 1 : 0); w.Write(SettlePoolNow ? 1 : 0); w.Write(RefinePasses); w.Write(sunNodeRays / 32);
            w.Write(fineGathering ? 1 : 0); w.Write(texelCount);
            w.Write(MemoryMarshal.AsBytes(ids.AsSpan(0, of)).ToArray());
            var places = refining ? device.ReadGpuBuffer(resolvePlaces[1 - resolveNext], 0, of * 4) : MemoryMarshal.AsBytes(Enumerable.Range(0, of).Select(i => (uint)i).ToArray().AsSpan()).ToArray();
            w.Write(places);
            w.Write(device.ReadGpuBuffer(resolveState, 0, checked(resolveCapacity * suns * 16)));
            w.Write(device.ReadGpuBuffer(resolvePasses, 0, resolveCapacity * 4));
            w.Write(left > 0 ? device.ReadGpuBuffer(resolveIds[resolveNext], 0, left * 4) : Array.Empty<byte>());
        }
        if (sunNodeRefineRound == 0)
        {
            File.WriteAllBytes($"{refineAudit}.g{refineAuditGather}.sky", device.ReadGpuBuffer(EnsureLivePart(false), 0, texelCount * 8));
            if (!resolvePool.Equals(default(GpuBufferHandle)) && SettlePoolNow)
                File.WriteAllBytes($"{refineAudit}.g{refineAuditGather}.pool", device.ReadGpuBuffer(resolvePool, 0, Math.Max(32, cookedPacked!.Length / 3 * 32)));
            if (!liveTexelPatch.Equals(default(GpuBufferHandle)) && !File.Exists($"{refineAudit}.patch"))
                File.WriteAllBytes($"{refineAudit}.patch", device.ReadGpuBuffer(liveTexelPatch, 0, texelCount * 4));
        }
        Console.WriteLine($"[VulkanSponza] refine audit: {path} ({of:N0} texels, {left:N0} unsettled)");
    }

    // The GPU's memory at a gather's ends: Metal's allocation against its recommended working set, and ours by kind.
    private void LogGatherMemory(string when)
    {
        if (frameTracer is { } tracer)
        {
            var (allocatedMb, recommendedMb) = tracer.Memory;
            Console.WriteLine(string.Create(Inv, $"[VulkanSponza] memory, {when}: Metal {allocatedMb:N0} of {recommendedMb:N0} MB recommended."));
        }
        LogMemoryInventory(when);
    }

    // List k's first count entries (ids and their places) put in id order, read back and written again.
    private uint[] SortResolveList(int k, int count)
    {
        if (count < 2) return MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(resolveIds[k], 0, Math.Max(4, count * 4)).AsSpan())[..count].ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        device.WaitIdle();
        var ids = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(resolveIds[k], 0, count * 4).AsSpan()).ToArray();
        var places = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(resolvePlaces[k], 0, count * 4).AsSpan()).ToArray();
        Array.Sort(ids, places);
        device.WriteGpuBuffer(resolveIds[k], 0, MemoryMarshal.AsBytes(ids.AsSpan()));
        device.WriteGpuBuffer(resolvePlaces[k], 0, MemoryMarshal.AsBytes(places.AsSpan()));
        Console.WriteLine(string.Create(Inv, $"[VulkanSponza] sun nodes: the round's {count:N0} texels put in id order in {clock.Elapsed.TotalMilliseconds:0} ms (read back, sorted, written)."));
        return ids;
    }

    private bool IsResolveBuffer(GpuBufferHandle b) => b.Equals(resolveIds[0]) || b.Equals(resolveIds[1]);
}
