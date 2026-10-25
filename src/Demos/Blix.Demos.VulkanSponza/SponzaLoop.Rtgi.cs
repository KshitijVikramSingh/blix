using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;

namespace Blix.Demos.VulkanSponza;

// --rtgi (S1 of the GI redesign, 2026-10-25): indirect light from a world radiance cache, no cook. A spatial hash of world
// cells (rtgi_cache.glsl: a cube at a level the distance from the camera picks, and the side the surface faces) holds
// each cell's indirect irradiance, sky visibility and sun visibility. Each frame:
//
//   rtgi-train    last frame's training points, their rays answered, into their cells (rtgi_train.comp): what the
//                 cosine ray saw -- the sky, or the cell where it landed (its irradiance and sun times its albedo) --
//                 and whether the sun ray reached
//   rtgi-resolve  each cell's frame of samples into a running mean (rtgi_resolve.comp); cells untrained for a while emptied
//   rtgi-rays     this frame's points (rtgi_rays.comp): --rtgi-tiles visible ones, a pixel of each tile over the screen, and
//                 --rtgi-hits where last frame's rays landed, so the cells a bounce away train too -- a path grows a
//                 vertex a frame through the cache; a cosine ray and a sun ray each, to Metal in the traced shadows' group
//   rtgi-readout  the cache at each half-res pixel (rtgi_readout.frag, trilinear over its level's cells) into the incident
//                 targets, in place of incident-light-half: the upsample and the lit pass read it unchanged
//
// The answers arrive a frame later (the group runs beside the frame); the readout reads what the cache holds now.
internal sealed partial class SponzaLoop
{
    private bool rtgi;
    private int rtgiTiles = 73_000, rtgiHitPoints = 32_000, rtgiSlotsLog2 = 21;
    private float rtgiCell = 0.05f, rtgiCellGrow = 0.01f, rtgiHistory = 64f, rtgiMaxAge = 600f;
    private const int RtgiTopLevel = 10;
    private ShaderInterface rtgiRaysInterface = null!, rtgiTrainInterface = null!, rtgiResolveInterface = null!, rtgiReadoutInterface = null!;
    private PassHandle rtgiRaysPassHandle, rtgiTrainPassHandle, rtgiResolvePassHandle, rtgiReadoutPassHandle;
    private PipelineHandle rtgiRaysPipeline, rtgiTrainPipeline, rtgiResolvePipeline, rtgiReadoutPipeline;
    // By the frame's parity: this frame's points, rays and answers; the other parity's are last frame's, trained now.
    private GpuBufferHandle[] rtgiPointsBuf = Array.Empty<GpuBufferHandle>(), rtgiRaysBuf = Array.Empty<GpuBufferHandle>(),
        rtgiHitsBuf = Array.Empty<GpuBufferHandle>(), rtgiOriginsBuf = Array.Empty<GpuBufferHandle>(), rtgiOccludedBuf = Array.Empty<GpuBufferHandle>();
    private GpuBufferHandle rtgiKeys, rtgiAccum, rtgiResolved;
    private uint rtgiFrameCount;
    private long rtgiTraced;

    private int RtgiPoints => rtgiTiles + rtgiHitPoints;
    private int RtgiSlots => 1 << rtgiSlotsLog2;
    private Vector4 RtgiParams => new(rtgiCell, rtgiCellGrow, RtgiSlots - 1, RtgiTopLevel);

    private void ParseRtgi(Blix.Core.AppArgs args)
    {
        rtgi = args.Flag("rtgi");
        if (args.Int("rtgi-tiles") is { } t) rtgiTiles = Math.Max(1, t);
        if (args.Int("rtgi-hits") is { } h) rtgiHitPoints = Math.Max(0, h);
        if (args.Int("rtgi-slots-log2") is { } sl) rtgiSlotsLog2 = Math.Clamp(sl, 16, 24);
        if (args.Float("rtgi-cell") is { } c) rtgiCell = MathF.Max(0.005f, c);
        if (args.Float("rtgi-cell-grow") is { } g) rtgiCellGrow = MathF.Max(0f, g);
        if (args.Float("rtgi-history") is { } hi) rtgiHistory = MathF.Max(1f, hi);
        if (args.Float("rtgi-max-age") is { } a) rtgiMaxAge = MathF.Max(1f, a);
        // The cache traces its own occlusion, and the Metal tracer answers its rays: the traced shadows build it.
        if (rtgi) rtShadows = true;
    }

    // Setup: the compute passes before the traced shadows' group that takes their batches.
    private void DeclareRtgiComputePasses(Func<string, ShaderInterface> reflect)
    {
        rtgiTrainInterface = reflect("rtgi_train.comp");
        rtgiTrainPassHandle = graph.ComputePass("rtgi-train").Shader(rtgiTrainInterface).Handle;
        rtgiResolveInterface = reflect("rtgi_resolve.comp");
        rtgiResolvePassHandle = graph.ComputePass("rtgi-resolve").Shader(rtgiResolveInterface).Handle;
        rtgiRaysInterface = reflect("rtgi_rays.comp");
        rtgiRaysPassHandle = graph.ComputePass("rtgi-rays").Shader(rtgiRaysInterface)
            .Read(SampleableSceneDepth).Read(SampleablePrepassNormal).Handle;
    }

    // Setup: the readout into the half-res incident targets, beside incident-light-half (a frame records one of them).
    private void DeclareRtgiReadoutPass(Func<string, string, ShaderInterface> reflect)
    {
        rtgiReadoutInterface = reflect("present.vert", "rtgi_readout.frag");
        rtgiReadoutPassHandle = graph.GraphicsPass("rtgi-readout")
            .Target(incidentHalfHandle, LoadOp.Clear, StoreOp.Store)
            .Target(incidentGradientHalfHandle, LoadOp.Clear, StoreOp.Store)
            .Read(SampleableSceneDepth)
            .Read(SampleablePrepassNormal)
            .Shader(rtgiReadoutInterface)
            .Handle;
    }

    private void CreateRtgiPipelines(string shaderDir, byte[] presentVertSpv)
    {
        PipelineHandle Compute(string name, ShaderInterface iface) => Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, name + ".comp.spv")), iface, name)), name));
        rtgiTrainPipeline = Compute("rtgi_train", rtgiTrainInterface);
        rtgiResolvePipeline = Compute("rtgi_resolve", rtgiResolveInterface);
        rtgiRaysPipeline = Compute("rtgi_rays", rtgiRaysInterface);
        rtgiReadoutPipeline = Pipeline(Own(device.CreateShaderProgramFromSpv(
                presentVertSpv, File.ReadAllBytes(Path.Combine(shaderDir, "rtgi_readout.frag.spv")), rtgiReadoutInterface, "rtgi_readout")),
            VertexPosition3NormalTexture.Layout, DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled, BlendState.Disabled }, rtgiReadoutPassHandle, "rtgi_readout");
    }

    // After the traced shadows are recorded: train, resolve, this frame's rays and their batches.
    private void RecordRtgi(int width, int height)
    {
        if (!rtgi || frameTracer is not { } tracer) return;
        var n = RtgiPoints;
        if (rtgiKeys.Equals(default(GpuBufferHandle)))
        {
            GpuBufferHandle[] Two(int bytes, string name) =>
                Enumerable.Range(0, 2).Select(s => Own(device.CreateGpuBuffer(bytes, name: $"sponza.rtgi.{name}{s}"))).ToArray();
            rtgiPointsBuf = Two(n * 32, "points");
            rtgiRaysBuf = Two(n * 32, "rays");
            rtgiHitsBuf = Two(n * 48, "hits");
            rtgiOriginsBuf = Two(n * 16, "origins");
            rtgiOccludedBuf = Two(n * 4, "occluded");
            rtgiKeys = Own(device.CreateGpuBuffer(RtgiSlots * 4, new byte[RtgiSlots * 4], "sponza.rtgi.keys"));
            rtgiAccum = Own(device.CreateGpuBuffer(RtgiSlots * 32, new byte[RtgiSlots * 32], "sponza.rtgi.accum"));
            rtgiResolved = Own(device.CreateGpuBuffer(RtgiSlots * 32, new byte[RtgiSlots * 32], "sponza.rtgi.resolved"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] rtgi: {rtgiTiles:N0} visible points and {rtgiHitPoints:N0} from last frame's hits a frame; cache {RtgiSlots:N0} slots ({RtgiSlots * 68 / 1048576.0:0} MB), cells {rtgiCell:0.###} m + {rtgiCellGrow:0.###} per metre, {rtgiHistory:0} samples held, untrained cells emptied after {rtgiMaxAge:0} frames."));
        }
        var cur = (int)(rtgiFrameCount & 1u);
        var prev = 1 - cur;
        var towardSun = -Vector3.Normalize(sunDirection);
        var groups = (n + 63) / 64;
        if (rtgiTraced > 0)
            graph.Dispatch(rtgiTrainPassHandle, new DispatchCommand(rtgiTrainPipeline, groups, 1, 1,
                new ShaderUniform[]
                {
                    new("uParams", new Vector4Uniform(RtgiParams)),
                    new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, n))),
                    new("uSun", new Vector4Uniform(new Vector4(towardSun, 1f))),
                    new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
                },
                new[] { new ShaderTextureBinding("uSkyRadiance", envCubeTexture) },
                Buffers: new[]
                {
                    new ShaderBufferBinding("RtgiPoints", rtgiPointsBuf[prev]),
                    new ShaderBufferBinding("RtgiRayBuffer", rtgiRaysBuf[prev]),
                    new ShaderBufferBinding("RtgiHits", rtgiHitsBuf[prev]),
                    new ShaderBufferBinding("RtgiOccluded", rtgiOccludedBuf[prev]),
                    new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                    new ShaderBufferBinding("RtgiAccum", rtgiAccum),
                    new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                }));
        graph.Dispatch(rtgiResolvePassHandle, new DispatchCommand(rtgiResolvePipeline, (RtgiSlots + 63) / 64, 1, 1,
            new ShaderUniform[] { new("uResolve", new Vector4Uniform(new Vector4(rtgiHistory, rtgiMaxAge, RtgiSlots, 0f))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                new ShaderBufferBinding("RtgiAccum", rtgiAccum),
                new ShaderBufferBinding("RtgiResolved", rtgiResolved),
            }));

        var tilesX = Math.Max(1, (int)MathF.Round(MathF.Sqrt(rtgiTiles * (float)width / height)));
        var tilesY = Math.Max(1, (rtgiTiles + tilesX - 1) / tilesX);
        // The visible points are as many tiles as the grid has (a whole grid, so every row is covered).
        Matrix4x4.Invert(viewProjJittered, out var invViewProj);
        graph.Dispatch(rtgiRaysPassHandle, new DispatchCommand(rtgiRaysPipeline, groups, 1, 1,
            new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                new("uSize", new Vector4Uniform(new Vector4(width, height, tilesX, tilesY))),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                new("uCounts", new Vector4Uniform(new Vector4(Math.Min(rtgiTiles, tilesX * tilesY), rtgiHitPoints, rtgiFrameCount % (1u << 24), rayReach))),
                new("uSun", new Vector4Uniform(new Vector4(towardSun, rtgiTraced > 0 ? 1f : 0f))),
            },
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
            },
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiPrevRays", rtgiRaysBuf[prev]),
                new ShaderBufferBinding("RtgiPrevHits", rtgiHitsBuf[prev]),
                new ShaderBufferBinding("RtgiPoints", rtgiPointsBuf[cur]),
                new ShaderBufferBinding("RtgiRayBuffer", rtgiRaysBuf[cur]),
                new ShaderBufferBinding("RtgiShadowOrigins", rtgiOriginsBuf[cur]),
            }));

        var rays = rtgiRaysBuf[cur];
        var hits = rtgiHitsBuf[cur];
        var origins = rtgiOriginsBuf[cur];
        var occluded = rtgiOccludedBuf[cur];
        var seed = unchecked(rtgiFrameCount * 0x9E3779B9u + 0x2545F491u);
        void Encode(ExternalWorkSync sync, MetalRayTracer t)
        {
            t.EncodeTrace(sync, rays, 0, hits, 0, n, seed);
            t.EncodeShadow(sync, origins, occluded, 0, n, towardSun, seed, statSlot: 3);
        }
        if (!TracerGroupsOn) graph.External(rtgiProbeTracePassHandle, sync => tracer.EncodeGroup(sync, () => Encode(sync, tracer)));
        else DeferTracerBatch(Encode);
        rtgiFrameCount++;
        rtgiTraced++;
    }

    private void RecordRtgiReadout()
    {
        Matrix4x4.Invert(viewProjJittered, out var invViewProj);
        var sub = LightingSubpixel;
        graph.Pass(rtgiReadoutPassHandle, scope => fullscreen.Draw(
            scope, rtgiReadoutPipeline,
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
            },
            pushConstants: null,
            uniforms: new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                new("uParams", new Vector4Uniform(RtgiParams)),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                new("uHalf", new Vector4Uniform(new Vector4(0f, sub.X, sub.Y, 0f))),
            },
            buffers: new[] { new ShaderBufferBinding("RtgiKeys", rtgiKeys), new ShaderBufferBinding("RtgiResolved", rtgiResolved) }));
    }

    private bool RtgiReadoutReady => rtgi && !rtgiKeys.Equals(default(GpuBufferHandle));

    // At the profile window: how full the cache is and how much its cells hold.
    private string RtgiCounts()
    {
        if (!RtgiReadoutReady || rtgiTraced < 2) return "";
        device.WaitIdle();
        var keys = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtgiKeys, 0, RtgiSlots * 4).AsSpan());
        var resolved = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtgiResolved, 0, RtgiSlots * 32).AsSpan());
        int used = 0, trained = 0, bad = 0;
        double samples = 0, energy = 0;
        string firstBad = "";
        for (var s = 0; s < keys.Length; s++)
        {
            if (keys[s] == 0u) continue;
            used++;
            var r1 = resolved[2 * s + 1];
            var r0 = resolved[2 * s];
            if (!float.IsFinite(r0.X + r0.Y + r0.Z + r0.W + r1.X + r1.Y + r1.Z + r1.W))
            {
                if (bad++ == 0) firstBad = string.Create(CultureInfo.InvariantCulture, $" first not finite: slot {s} {r0} {r1}");
                continue;
            }
            if (r1.Y < 0.5f) continue;
            trained++;
            samples += r1.Y;
            var e = resolved[2 * s];
            energy += 0.2126 * e.X + 0.7152 * e.Y + 0.0722 * e.Z;
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"rtgi: {used:N0} of {RtgiSlots:N0} slots held ({100.0 * used / RtgiSlots:0.0}%), {trained:N0} trained, {samples / Math.Max(1, trained):0.0} samples a cell, mean indirect {energy / Math.Max(1, trained):0.000}; {bad:N0} not finite{firstBad}; {rtgiTraced:N0} frames traced");
    }
}
