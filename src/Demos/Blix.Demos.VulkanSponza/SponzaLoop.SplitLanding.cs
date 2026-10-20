using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The split light's gather as a run-time computation (plan.md, "Surface light: one design", W1): a source change is
// gathered again and lands as it goes, what the image reads first.
//   VISIBLE FIRST  when a cook starts while the scene is drawn (a relight), texel_visible.comp marks every texel the
//                  image reads (a bit a record); the next frame reads the bits and the cook traces those texels before
//                  the rest (UploadSunNodes takes the order).
//   LANDING        each slice read back goes straight into the parts the compose reads (the sun's per unit of
//                  irradiance, as FinishSplitCook stores it), flushed to the GPU at most once a SplitLandSeconds and
//                  whenever the visible texels are all in -- so the old light gives way texel by texel, where the cook
//                  ran, and the image is never blank.
//   LOCKED         once the visible order is read the cook holds the screen (CookScreenNow): the last frame stays up,
//                  the camera locked, the cook taking the cook screen's budget (the user's call, 2026-10-14: live, at
//                  50 ms of each ~150 ms frame, Sponza's sweep took ~23 min against 6).
internal sealed partial class SponzaLoop
{
    private const double SplitLandSeconds = 1.0;
    private ShaderInterface texelVisibleInterface = null!;
    private PassHandle texelVisiblePass;
    private PipelineHandle texelVisiblePipeline;
    private GpuBufferHandle texelVisibleBits;
    // Asked when a live cook starts; recorded the next frame (RecordTexels); read the frame after.
    private bool splitVisibleAsked, splitVisibleRecorded;
    private long splitVisibleRecordedFrame;
    private uint[]? splitVisibleOrder;
    private int splitVisibleCount;
    // The cook started with light already up: the scene stays drawn while it runs.
    private bool splitCookLive;
    // The parts as the compose reads them, on the CPU: landing writes slices in, a flush sends them whole.
    private uint[]? splitSkyWords, splitSunWords;
    private bool splitSkyLanded, splitSunLanded;
    private readonly Stopwatch splitLandClock = new();
    private bool splitVisibleLanded;

    private void DeclareTexelVisiblePass(Func<string[], ShaderInterface> reflect)
    {
        texelVisibleInterface = reflect(new[] { "texel_visible.comp" });
        texelVisiblePass = graph.ComputePass("texel-visible").Read(SampleableSceneDepth).Read(SampleablePrepassNormal)
            .Read(lightmapEnabled ? lightmapTexelHandle : surfaceKeyHandle).Shader(texelVisibleInterface).Handle;
    }

    private void CreateTexelVisiblePipeline(string shaderDir) =>
        texelVisiblePipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_visible.comp.spv")), texelVisibleInterface, "texel_visible")), "texel_visible"));

    // StartSplitCook: a cook with light already up runs live and traces what is seen first.
    private void BeginSplitCookLanding()
    {
        splitCookLive = true;   // at boot as after a sun move: the scene stays up (CookScreenNow)
        splitVisibleOrder = null;
        splitVisibleCount = 0;
        splitVisibleLanded = false;
        splitVisibleAsked = !liveTexels;   // live: the coarse levels scene-wide, no view first
        splitVisibleRecorded = false;
        splitSkyLanded = splitSunLanded = false;
        splitLandClock.Restart();
    }

    // The trace waits for the visible order when one was asked (SunNodeTraceReady).
    private bool SplitVisiblePending => splitCooking && splitVisibleAsked;

    // RecordTexels, each frame: the visible pass recorded once, its bits read the frame after.
    private void RecordTexelVisible(Matrix4x4 invProjection, Matrix4x4 invView, int width, int height)
    {
        if (!splitVisibleAsked) return;
        if (splitVisibleRecorded)
        {
            ReadTexelVisible();
            return;
        }
        var words = (texelCount + 31) / 32;
        if (!texelVisibleBits.Equals(default(GpuBufferHandle))) RetireGpuBuffer(texelVisibleBits);
        texelVisibleBits = Own(device.CreateGpuBuffer(words * 4, name: "sponza.texels.visible"));
        splitVisibleRecordedFrame = framesRendered;
        graph.Dispatch(texelVisiblePass, new DispatchCommand(texelVisiblePipeline, (width + 7) / 8, (height + 7) / 8, 1,
            new ShaderUniform[]
            {
                new("uInvProjection", new Matrix4x4Uniform(invProjection)),
                new("uInvView", new Matrix4x4Uniform(invView)),
                new("uTarget", new Vector4Uniform(new Vector4(width, height, 1f, 0f))),
                new("uTexelGrid", new Vector4Uniform(texelGrid)),
                new("uTexelParams", new Vector4Uniform(new Vector4(texelCapacity, texelLevels, 0f, 0f))),
                new("uLightmap", new Vector4Uniform(LightmapUniform)),
            },
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                AtlasGatherTexture(),
                LightmapTexelTexture(),
            },
            Buffers: new[]
            {
                new ShaderBufferBinding("TexelHash", texelHashBuffer),
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("TexelVisibleBits", texelVisibleBits),
            }));
        splitVisibleRecorded = true;
    }

    private void ReadTexelVisible()
    {
        var clock = Stopwatch.StartNew();
        var bitBytes = new byte[(texelCount + 31) / 32 * 4];
        bitBytes = device.ReadGpuBuffer(texelVisibleBits, 0, bitBytes.Length);
        var bits = MemoryMarshal.Cast<byte, uint>(bitBytes.AsSpan()).ToArray();
        RetireGpuBuffer(texelVisibleBits);
        texelVisibleBits = default;
        var live = texelLiveIds!;
        var order = new uint[live.Length];
        var front = 0;
        foreach (var id in live)
            if ((bits[id >> 5] >> (int)(id & 31) & 1u) != 0) order[front++] = id;
        var back = front;
        foreach (var id in live)
            if ((bits[id >> 5] >> (int)(id & 31) & 1u) == 0) order[back++] = id;
        splitVisibleOrder = order;
        splitVisibleCount = front;
        splitVisibleAsked = false;
        splitVisibleRecorded = false;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] split light: {front:N0} of {live.Length:N0} texels on screen go first ({clock.Elapsed.TotalMilliseconds:0} ms)."));
    }

    // UploadSunNodes: the order the cook traces in -- what is seen first, when known.
    private uint[] SplitTraceOrder(uint[] live) =>
        splitCooking && splitVisibleOrder is { Length: var n } order && n == live.Length ? order : live;

    // RecordSunNodeSlice, a slice read back (the sweep's or a refining round's): its texels into the parts.
    private void LandSplitSlice(uint[] ids, int first, int count)
    {
        if (!splitCooking) return;
        var results = sunNodeResults;
        var irradiance = EffectiveSunIrradiance;
        var inverse = new Vector3(1f / MathF.Max(irradiance.X, 1e-6f), 1f / MathF.Max(irradiance.Y, 1e-6f), 1f / MathF.Max(irradiance.Z, 1e-6f));
        if (liveTexels) LandSplitSliceLive(ids, first, count, inverse);
        else for (var k = 0; k < results.Length && k < sunNodeKinds.Length; k++)
        {
            var sunPart = sunNodeKinds[k] == 1;
            var words = results[k];
            var into = sunPart ? (splitSunWords ??= new uint[texelCount * 2]) : (splitSkyWords ??= new uint[texelCount * 2]);
            Parallel.For(0, count, t =>
            {
                var id = (int)ids[first + t];
                var w = words[2 * id];
                into[2 * id] = sunPart ? PackRgb9e5(UnpackRgb9e5(w) * inverse) : w;
                into[2 * id + 1] = words[2 * id + 1];
            });
            if (sunPart) splitSunLanded = true; else splitSkyLanded = true;
        }
        if (sunNodeRefineRound == 0 && !liveTexels) LandSplitDetail(ids, first, count);
        var visibleDone = !splitVisibleLanded && splitVisibleCount > 0 && sunNodeRefineRound == 0 && first + count >= splitVisibleCount;
        if (visibleDone)
        {
            splitVisibleLanded = true;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] split light: the {splitVisibleCount:N0} texels on screen landed ({sunNodeClock.Elapsed.TotalSeconds:0.0} s after the trace began)."));
        }
        if (visibleDone || splitLandClock.Elapsed.TotalSeconds >= SplitLandSeconds) FlushSplitLanding();
    }

    // --live-texels: the slice straight into the parts' buffers (split_scatter.comp) -- the buffers are the parts; no
    // mirror, no whole upload. Its ids once, then each column's words (the sun's per unit of its irradiance) and, on the
    // sweep, its cells' detail.
    private ShaderInterface splitScatterInterface = null!;
    private PipelineHandle splitScatterPipeline;
    private PassHandle splitScatterPass;

    private void LandSplitSliceLive(uint[] ids, int first, int count, Vector3 inverse)
    {
        var idsBuffer = Own(device.CreateGpuBuffer(Math.Max(16, count * 4), MemoryMarshal.AsBytes(ids.AsSpan(first, count)), "sponza.split.scatter-ids"));
        for (var k = 0; k < sunNodeResults.Length && k < sunNodeKinds.Length; k++)
        {
            var sunPart = sunNodeKinds[k] == 1;
            var words = sunNodeResults[k];
            var values = new uint[count * 2];
            Parallel.For(0, count, t =>
            {
                var id = (int)ids[first + t];
                var w = words[2 * id];
                values[2 * t] = sunPart ? PackRgb9e5(UnpackRgb9e5(w) * inverse) : w;
                values[2 * t + 1] = words[2 * id + 1];
            });
            Scatter(EnsureLivePart(sunPart), idsBuffer, values, count, 2);
            if (sunNodeRefineRound == 0 && k < sunNodeDetailWords.Length && sunNodeDetailWords[k].Length > 0)
            {
                var from = sunNodeDetailWords[k];
                var detail = new uint[count];
                Parallel.For(0, count, t => detail[t] = from[(int)ids[first + t]]);
                ref var detailBuffer = ref (sunPart ? ref splitSunDetailBuffer : ref splitSkyDetailBuffer);
                if (detailBuffer.Equals(default(GpuBufferHandle)))
                    detailBuffer = Own(device.CreateGpuBuffer(Math.Max(texelCount, 1) * 4, new byte[Math.Max(texelCount, 1) * 4], sunPart ? "sponza.split.sun-detail" : "sponza.split.sky-detail"));
                Scatter(detailBuffer, idsBuffer, detail, count, 1);
            }
        }
        RetireGpuBuffer(idsBuffer);
        splitComposeDirty = true;
        splitComposeAgainFrame = framesRendered + 1;
    }

    private void Scatter(GpuBufferHandle target, GpuBufferHandle ids, uint[] values, int count, int words)
    {
        var valuesBuffer = Own(device.CreateGpuBuffer(Math.Max(16, values.Length * 4), MemoryMarshal.AsBytes(values.AsSpan()), "sponza.split.scatter-values"));
        graph.Dispatch(splitScatterPass, new DispatchCommand(splitScatterPipeline, (count + 63) / 64, 1, 1,
            new ShaderUniform[] { new("uScatter", new Vector4Uniform(new Vector4(count, words, 0f, 0f))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("ScatterIds", ids),
                new ShaderBufferBinding("ScatterValues", valuesBuffer),
                new ShaderBufferBinding("ScatterTarget", target),
            }));
        RetireGpuBuffer(valuesBuffer);
    }

    // A part's buffer under --live-texels: made zeroed when it is not up (the provisional or the landing fills it).
    private GpuBufferHandle EnsureLivePart(bool sunPart)
    {
        ref var buffer = ref (sunPart ? ref splitSunBuffer : ref splitSkyBuffer);
        if (buffer.Equals(default(GpuBufferHandle)))
            buffer = Own(device.CreateGpuBuffer(texelCount * 8, new byte[texelCount * 8], sunPart ? "sponza.split.sun" : "sponza.split.sky"));
        if (sunPart) splitSunUp = true; else splitSkyUp = true;
        return buffer;
    }

    // The landed parts to the GPU: written into the parts' buffers in place, or made when a part is not up yet.
    private void FlushSplitLanding()
    {
        splitLandClock.Restart();
        if (splitSkyLanded && splitSkyWords is { } sky) WriteSplitPart(sky, sunPart: false);
        if (splitSunLanded && splitSunWords is { } sun) WriteSplitPart(sun, sunPart: true);
        splitSkyLanded = splitSunLanded = false;
        FlushSplitDetail();
    }

    private void WriteSplitPart(uint[] words, bool sunPart)
    {
        var buffer = sunPart ? splitSunBuffer : splitSkyBuffer;
        if (buffer.Equals(default(GpuBufferHandle)) || !(sunPart ? splitSunUp : splitSkyUp))
        {
            UploadSplitPart(words, sunPart);
            return;
        }
        device.WriteGpuBuffer(buffer, 0, MemoryMarshal.AsBytes(words.AsSpan()));
        splitComposeDirty = true;
    }
}
