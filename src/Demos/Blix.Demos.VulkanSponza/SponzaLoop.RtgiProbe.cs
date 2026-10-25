using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;

namespace Blix.Demos.VulkanSponza;

// --rtgi-probe N (S0 of the GI redesign, 2026-10-25): what the screen gather would cost inside the frame, before any of
// it is built. Each frame N rays, one per tile of a grid over the screen, cosine-weighted from the pre-pass's depth and
// normal (rtgi_probe_rays.comp); --rtgi-probe-shadows M sun shadow rays from the first M of them, standing in for the
// cache-update rays' direct sun. Both go to Metal in the traced shadows' group (SponzaLoop.TracerGroup.cs) -- no trip of
// their own -- or, with --rtgi-probe-apart, in an external pass of their own, which prices a second hand-off. The last
// frame's hits are shaded against a spatial hash shaped like SHaRC's (rtgi_probe_shade.comp: lookup and atomic update),
// --rtgi-probe-no-shade skipping it. --ab rtgi alternates the probe on and off in one process (the fanless M4 throttles
// across runs), the profile window's frame times by phase.
internal sealed partial class SponzaLoop
{
    private int rtgiProbeRays, rtgiProbeShadows;
    private bool rtgiProbeApart, rtgiProbeNoShade;
    private const int RtgiCacheSlots = 1 << 22;
    private ShaderInterface rtgiProbeRaysInterface = null!, rtgiProbeShadeInterface = null!;
    private PassHandle rtgiProbeRaysPassHandle, rtgiProbeShadePassHandle, rtgiProbeTracePassHandle;
    private PipelineHandle rtgiProbeRaysPipeline, rtgiProbeShadePipeline;
    // Two of each, by the frame's parity: this frame writes one while its shade reads the other's answers.
    private GpuBufferHandle[] rtgiRays = Array.Empty<GpuBufferHandle>(), rtgiHits = Array.Empty<GpuBufferHandle>();
    private GpuBufferHandle rtgiOrigins, rtgiOccluded, rtgiCacheKeys, rtgiCacheData, rtgiLight;
    private uint rtgiFrame;
    private long rtgiFramesTraced;

    private bool RtgiProbeOn => rtgiProbeRays > 0;

    private void ParseRtgiProbe(Blix.Core.AppArgs args)
    {
        rtgiProbeRays = Math.Max(0, args.Int("rtgi-probe") ?? 0);
        rtgiProbeShadows = Math.Clamp(args.Int("rtgi-probe-shadows") ?? 0, 0, rtgiProbeRays);
        rtgiProbeApart = args.Flag("rtgi-probe-apart");
        rtgiProbeNoShade = args.Flag("rtgi-probe-no-shade");
    }

    // Setup: the rays pass after the pre-pass, before the traced shadows' group that takes their batches.
    private void DeclareRtgiProbePasses(Func<string, ShaderInterface> reflect)
    {
        rtgiProbeRaysInterface = reflect("rtgi_probe_rays.comp");
        rtgiProbeRaysPassHandle = graph.ComputePass("rtgi-probe-rays").Shader(rtgiProbeRaysInterface)
            .Read(SampleableSceneDepth).Read(SampleablePrepassNormal).Handle;
        rtgiProbeShadeInterface = reflect("rtgi_probe_shade.comp");
        rtgiProbeShadePassHandle = graph.ComputePass("rtgi-probe-shade").Shader(rtgiProbeShadeInterface).Handle;
        rtgiProbeTracePassHandle = graph.ExternalPass("rtgi-probe-trace");
    }

    private void CreateRtgiProbePipelines(string shaderDir)
    {
        rtgiProbeRaysPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "rtgi_probe_rays.comp.spv")), rtgiProbeRaysInterface, "rtgi_probe_rays")), "rtgi_probe_rays"));
        rtgiProbeShadePipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "rtgi_probe_shade.comp.spv")), rtgiProbeShadeInterface, "rtgi_probe_shade")), "rtgi_probe_shade"));
    }

    private void RecordRtgiProbe(int width, int height)
    {
        if (!RtgiProbeOn || abMode == "rtgi" && AbOffPhase || frameTracer is not { } tracer) return;
        var n = rtgiProbeRays;
        if (rtgiRays.Length == 0)
        {
            rtgiRays = Enumerable.Range(0, 2).Select(s => Own(device.CreateGpuBuffer(n * 32, name: $"sponza.rtgi-probe.rays{s}"))).ToArray();
            rtgiHits = Enumerable.Range(0, 2).Select(s => Own(device.CreateGpuBuffer(n * 48, name: $"sponza.rtgi-probe.hits{s}"))).ToArray();
            rtgiOrigins = Own(device.CreateGpuBuffer(Math.Max(1, rtgiProbeShadows) * 16, name: "sponza.rtgi-probe.origins"));
            rtgiOccluded = Own(device.CreateGpuBuffer(Math.Max(1, rtgiProbeShadows) * 4, name: "sponza.rtgi-probe.occluded"));
            rtgiCacheKeys = Own(device.CreateGpuBuffer(RtgiCacheSlots * 4, new byte[RtgiCacheSlots * 4], "sponza.rtgi-probe.cache-keys"));
            rtgiCacheData = Own(device.CreateGpuBuffer(RtgiCacheSlots * 16, new byte[RtgiCacheSlots * 16], "sponza.rtgi-probe.cache-data"));
            rtgiLight = Own(device.CreateGpuBuffer(n * 16, name: "sponza.rtgi-probe.light"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] rtgi probe: {n:N0} gather rays a frame, {rtgiProbeShadows:N0} shadow rays, {(rtgiProbeApart ? "a Metal trip of their own" : "in the traced shadows' group")}{(rtgiProbeNoShade ? ", no shade" : $", shaded against a {RtgiCacheSlots:N0}-slot hash")}{(abMode == "rtgi" ? " (--ab rtgi: on and off by phase)" : "")}."));
        }
        var cur = (int)(rtgiFrame & 1u);
        var prev = 1 - cur;
        // The last frame's answers first, before this frame's rays overwrite nothing of theirs (the other parity).
        if (!rtgiProbeNoShade && rtgiFramesTraced > 0)
            graph.Dispatch(rtgiProbeShadePassHandle, new DispatchCommand(rtgiProbeShadePipeline, (n + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uShade", new Vector4Uniform(new Vector4(n, RtgiCacheSlots - 1, 0.05f, 0.01f))),
                    new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new[]
                {
                    new ShaderBufferBinding("RtgiProbeRayBuffer", rtgiRays[prev]),
                    new ShaderBufferBinding("RtgiProbeHits", rtgiHits[prev]),
                    new ShaderBufferBinding("RtgiCacheKeys", rtgiCacheKeys),
                    new ShaderBufferBinding("RtgiCacheData", rtgiCacheData),
                    new ShaderBufferBinding("RtgiProbeLight", rtgiLight),
                }));

        // Tiles across and down for n of them over the screen's aspect.
        var tilesX = Math.Max(1, (int)MathF.Round(MathF.Sqrt(n * (float)width / height)));
        var tilesY = Math.Max(1, (n + tilesX - 1) / tilesX);
        Matrix4x4.Invert(viewProjJittered, out var invViewProj);
        graph.Dispatch(rtgiProbeRaysPassHandle, new DispatchCommand(rtgiProbeRaysPipeline, (n + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                new("uSize", new Vector4Uniform(new Vector4(width, height, tilesX, tilesY))),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                new("uProbe", new Vector4Uniform(new Vector4(n, rtgiProbeShadows, rtgiFrame % (1u << 24), rayReach))),
            },
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
            },
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiProbeRayBuffer", rtgiRays[cur]),
                new ShaderBufferBinding("RtgiProbeOrigins", rtgiOrigins),
            }));

        var rays = rtgiRays[cur];
        var hits = rtgiHits[cur];
        var origins = rtgiOrigins;
        var occluded = rtgiOccluded;
        var shadows = rtgiProbeShadows;
        var towardSun = -Vector3.Normalize(sunDirection);
        var seed = unchecked(rtgiFrame * 0x9E3779B9u + 0x5bd1e995u);
        void Encode(ExternalWorkSync sync, MetalRayTracer t)
        {
            t.EncodeTrace(sync, rays, 0, hits, 0, n, seed);
            if (shadows > 0) t.EncodeShadow(sync, origins, occluded, 0, shadows, towardSun, seed, statSlot: 3);
        }
        if (rtgiProbeApart || !TracerGroupsOn)
            graph.External(rtgiProbeTracePassHandle, sync => tracer.EncodeGroup(sync, () => Encode(sync, tracer)));
        else DeferTracerBatch(Encode);
        rtgiFrame++;
        rtgiFramesTraced++;
    }

    // At the profile window: how many of the last gather's rays met something, and how full the hash is.
    private string RtgiProbeCounts()
    {
        if (!RtgiProbeOn || rtgiHits.Length == 0 || rtgiFramesTraced < 2) return "";
        device.WaitIdle();
        var prev = (int)((rtgiFrame + 1u) & 1u);
        var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtgiHits[prev], 0, rtgiProbeRays * 48).AsSpan());
        var met = 0;
        for (var i = 0; i < rtgiProbeRays; i++) if ((words[12 * i + 3] & 1u) != 0u) met++;
        var keys = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtgiCacheKeys, 0, RtgiCacheSlots * 4).AsSpan());
        var used = 0;
        foreach (var k in keys) if (k != 0u) used++;
        return string.Create(CultureInfo.InvariantCulture,
            $"rtgi probe: {100.0 * met / rtgiProbeRays:0.0}% of {rtgiProbeRays:N0} gather rays met something; hash {used:N0} of {RtgiCacheSlots:N0} slots ({100.0 * used / RtgiCacheSlots:0.0}%); {rtgiFramesTraced:N0} frames traced");
    }
}
