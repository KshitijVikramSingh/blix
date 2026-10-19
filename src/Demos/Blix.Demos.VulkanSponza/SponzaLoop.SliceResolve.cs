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
            resolveState = Own(device.CreateGpuBuffer(checked(n * suns * 16), name: "sponza.resolve.state"));
            resolvePasses = Own(device.CreateGpuBuffer(n * 4, name: "sponza.resolve.passes"));
            for (var i = 0; i < 2; i++)
            {
                resolveIds[i] = Own(device.CreateGpuBuffer(n * 4, name: "sponza.resolve.ids"));
                resolvePlaces[i] = Own(device.CreateGpuBuffer(n * 4, name: "sponza.resolve.places"));
            }
            resolveCapacity = n;
        }
        if (resolveCount.Equals(default(GpuBufferHandle))) resolveCount = Own(device.CreateGpuBuffer(16, name: "sponza.resolve.count"));
        if (resolveHistogram.Equals(default(GpuBufferHandle))) resolveHistogram = Own(device.CreateGpuBuffer(512, name: "sponza.resolve.histogram"));
        device.WriteGpuBuffer(resolveCount, 0, new byte[16]);
        device.WriteGpuBuffer(resolveHistogram, 0, new byte[512]);
        resolveNext = 0;
        gpuResolving = true;
    }

    private int SunColumn() => Array.IndexOf(sunNodeKinds, 1);

    private void ResolveDispatch(int mode, int first, int count, int passes, bool refining, GpuBufferHandle ids, GpuBufferHandle places, float bar)
    {
        if (count == 0) return;
        var suns = sunNodeSuns!.Length;
        var irradiance = EffectiveSunIrradiance;
        var inverse = new Vector3(1f / MathF.Max(irradiance.X, 1e-6f), 1f / MathF.Max(irradiance.Y, 1e-6f), 1f / MathF.Max(irradiance.Z, 1e-6f));
        var detail = mode == 0 && !refining && !sunNodeDetail.Equals(default(GpuBufferHandle));
        var skyDetail = EnsureDetailBuffer(sunPart: false);
        var sunDetail = EnsureDetailBuffer(sunPart: true);
        var declared = sliceResolveInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        device.DispatchNow(new[] { new DispatchCommand(sliceResolvePipeline, (count + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uSlice", new Vector4Uniform(new Vector4(first, count, passes, refining ? 1f : 0f))),
                new("uSettle", new Vector4Uniform(new Vector4(texelMaxRays, texelNoise, TexelNoiseFloor, bar))),
                new("uSun", new Vector4Uniform(new Vector4(inverse, sunNodeRays / 32))),
                new("uMode", new Vector4Uniform(new Vector4(mode, suns, SunColumn(), detail ? 1f : 0f))),
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
                new ShaderBufferBinding("Histogram", resolveHistogram),
                new ShaderBufferBinding("NextCount", resolveCount),
            }.Where(b => declared.Contains(b.Name)).ToArray()) }, "slice-resolve");
    }

    private GpuBufferHandle EnsureDetailBuffer(bool sunPart)
    {
        ref var buffer = ref (sunPart ? ref splitSunDetailBuffer : ref splitSkyDetailBuffer);
        if (buffer.Equals(default(GpuBufferHandle)))
            buffer = Own(device.CreateGpuBuffer(Math.Max(texelCount, 1) * 4, new byte[Math.Max(texelCount, 1) * 4], sunPart ? "sponza.split.sun-detail" : "sponza.split.sky-detail"));
        return buffer;
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
        var left = ReadResolveCount();
        var of = sunNodeRefineRound == 0 ? sunNodeTraceIds.Length : sunNodeRefineIds.Length;
        if (sunNodeRefineRound == 0 && left > 0)
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
            device.WriteGpuBuffer(resolveCount, 0, new byte[16]);
            ResolveDispatch(1, 0, left, 0, false, resolveIds[judged], resolvePlaces[judged], sunNodeDarkBar);
            var after = ReadResolveCount();
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] sun nodes: the dark bar {sunNodeDarkBar:G3} ({texelDarkFloor:G2} of the median texel light); judged again, {left - after:N0} more texels settled."));
            left = after;
        }
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sun nodes: {of - left:N0} of {of:N0} texels settled after {(sunNodeRefineRound == 0 ? "the sweep" : $"round {sunNodeRefineRound}")} ({sunNodeClock.Elapsed.TotalMinutes:0.0} min); {left:N0} refine (resolved on the GPU)."));
        if (left == 0) return false;
        if (sunNodeRefineRound > 0 && of - left < splitRefineMinShare * of)
        {
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] sun nodes: round {sunNodeRefineRound} settled {of - left:N0} of {of:N0} ({(of - left) * 100.0 / Math.Max(1, of):0.0}%, under {splitRefineMinShare * 100:0}%): refining stops; {left:N0} keep their light as it is."));
            return false;
        }
        // The list just appended is the round's; the next round appends into the other.
        sunNodeRefineBuffer = resolveIds[resolveNext];
        sunNodeRefineIds = new uint[left];
        resolveNext = 1 - resolveNext;
        device.WriteGpuBuffer(resolveCount, 0, new byte[16]);
        sunNodeRefineRound++;
        sunNodeRefineRays += (long)left * RefinePasses * 32;
        sunNodeFirst = 0;
        sunNodePass = 0;
        return true;
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

    private bool IsResolveBuffer(GpuBufferHandle b) => b.Equals(resolveIds[0]) || b.Equals(resolveIds[1]);
}
