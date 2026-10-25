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
    // 4 M slots (272 MB): San Miguel held 1.8-3.6 M cells (87% of 4 M at the old 1 cm per metre growth, its landings then
    // failing to find a slot).
    private int rtgiTiles = 73_000, rtgiHitPoints = 32_000, rtgiSlotsLog2 = 22;
    // Cells untrained for --rtgi-max-age frames are emptied: 1,800 (30 s at 60 fps; at 600 a walk back to a place left 10 s
    // before found it gone and re-settling, 2026-10-26).
    // Cells 5 cm, growing 0.5 cm a metre from the camera (doubling every ~10 m; at 1 cm, every ~5 m, mid-distance GI went
    // coarse too soon -- the user, 2026-10-26).
    private float rtgiCell = 0.05f, rtgiCellGrow = 0.005f, rtgiMaxAge = 1800f;
    // --rtgi-settle-frames N: frames the cache trains before --shot-when-ready may shoot (600).
    private int rtgiSettleFrames = 600;
    // S2: a cell's history follows its light (rtgi_resolve.comp) -- up to --rtgi-history samples while its samples agree
    // with it (a fixed 64 shimmered, never settling: the user, 2026-10-26), the slow mean replaced by a fast one (--rtgi-fast
    // samples) when the two disagree by more than --rtgi-change-sigma of the fast one's standard errors.
    // The fast window: 64 (at 16 the cache reset itself on noise -- a bright frame or a dark one -- and read 0.18 rel
    // |error| at Crytek Sponza's points settled, 3-4% dark; 64: 0.10 and 1.00, 2026-10-26).
    // --rtgi-change-sigma 5 and --rtgi-change-frames 3: a disagreement must pass 5 standard errors three frames running
    // (rtgi_resolve.comp): the net for changes nobody announced; announced ones (the sun turned) cut at once.
    // --rtgi-history 16384: a still light keeps converging; capped at 1,024 a cell's mean kept moving by a 1/1,024 of each
    // sample, the shadowed areas' rare bright ones most -- mud that never settled (the user). Crytek Sponza settled: flicker
    // 1.6% -> 0.8%, error 0.100 -> 0.097 (2026-10-26). Changes are the cuts' (the sun's at once, the rest's net).
    private float rtgiHistory = 16384f, rtgiChangeSigma = 5f, rtgiFast = 64f, rtgiChangeFloor = 0.05f, rtgiChangeFrames = 3f;
    // --rtgi-sun-cut N: on a frame the sun turns (0.02 degrees or more), every cell's light history capped to N samples
    // (and its sun visibility's to 4): a known change, not one for the fast mean to find. 0: none.
    private float rtgiSunCut = 32f;
    private Vector3 rtgiLastSun;
    // Diagnostics for the cache's bounce (the dead end 23% dark, 2026-10-26): --rtgi-count-unknown counts a landing cell
    // that holds nothing as dark; --rtgi-no-coarse gives the training no coarser cell to read in its place.
    private bool rtgiCountUnknown, rtgiNoCoarse, rtgiBackfaceLit;
    // Landings in untrained cells, trained the next frame (rtgi_train.comp's requests): by parity. --rtgi-no-requests: off.
    private bool rtgiRequests = true;
    // --rtgi-clamp-sigma K: a sample capped at its cell's mean + K standard deviations once it holds 32 (0: off).
    private float rtgiClampSigma;
    private GpuBufferHandle[] rtgiRequestBufs = Array.Empty<GpuBufferHandle>();
    private const int RtgiTopLevel = 10;
    // --rtgi-screen (S3): each half-res pixel's light from the screen tiles' reservoirs (ReSTIR GI: rtgi_reservoir.comp,
    // rtgi_screen.frag) in place of the cache read at its cell. --rtgi-screen-taps N reservoirs a pixel (8) within
    // --rtgi-screen-radius tiles (2); --rtgi-reuse-max the count a reservoir keeps (20).
    private bool rtgiScreen;
    private int rtgiScreenTaps = 8, rtgiScreenRadius = 2;
    private float rtgiReuseMax = 20f;
    private ShaderInterface rtgiReservoirInterface = null!, rtgiScreenInterface = null!;
    private PassHandle rtgiReservoirPassHandle, rtgiScreenPassHandle;
    private PipelineHandle rtgiReservoirPipeline, rtgiScreenPipeline;
    private GpuBufferHandle[] rtgiReservoirs = Array.Empty<GpuBufferHandle>();
    // The view-projections the last two frames' points were drawn in, and the grid this frame's reservoirs are laid out in.
    private Matrix4x4 rtgiViewProj1, rtgiViewProj2, rtgiGridViewProj;
    private (int X, int Y) rtgiGrid;
    private int rtgiReservoirCur = -1;
    private ShaderInterface rtgiRaysInterface = null!, rtgiTrainInterface = null!, rtgiResolveInterface = null!, rtgiReadoutInterface = null!;
    private PassHandle rtgiRaysPassHandle, rtgiTrainPassHandle, rtgiResolvePassHandle, rtgiReadoutPassHandle;
    private PipelineHandle rtgiRaysPipeline, rtgiTrainPipeline, rtgiResolvePipeline, rtgiReadoutPipeline;
    // By the frame's parity: this frame's points, rays and answers; the other parity's are last frame's, trained now.
    private GpuBufferHandle[] rtgiPointsBuf = Array.Empty<GpuBufferHandle>(), rtgiRaysBuf = Array.Empty<GpuBufferHandle>(),
        rtgiHitsBuf = Array.Empty<GpuBufferHandle>(), rtgiOriginsBuf = Array.Empty<GpuBufferHandle>(), rtgiOccludedBuf = Array.Empty<GpuBufferHandle>();
    private GpuBufferHandle rtgiKeys, rtgiAccum, rtgiResolved, rtgiMoments, rtgiStamps;
    // The cells training touched this frame, by parity (rtgi_train.comp), for the resolve; and the sun's turns so far, each
    // cell taking the cut for a turn when next trained (rtgi_resolve.comp). A point touches at most three cells.
    private GpuBufferHandle[] rtgiDirtyBufs = Array.Empty<GpuBufferHandle>();
    private uint rtgiSunEpoch;
    private int RtgiDirtyRoom => RtgiPoints * 3;
    private ShaderInterface rtgiEvictInterface = null!;
    private PassHandle rtgiEvictPassHandle;
    private PipelineHandle rtgiEvictPipeline;
    // The eviction's slice: a sixteenth of the slots a frame.
    private const int RtgiEvictSlices = 16;
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
        if (args.Int("rtgi-settle-frames") is { } sf) rtgiSettleFrames = Math.Max(0, sf);
        if (args.Float("rtgi-change-sigma") is { } cs) rtgiChangeSigma = MathF.Max(0.5f, cs);
        if (args.Float("rtgi-fast") is { } fa) rtgiFast = MathF.Max(1f, fa);
        if (args.Float("rtgi-change-floor") is { } cf) rtgiChangeFloor = MathF.Max(0f, cf);
        if (args.Float("rtgi-change-frames") is { } cfr) rtgiChangeFrames = MathF.Max(1f, cfr);
        if (args.Float("rtgi-sun-cut") is { } sc) rtgiSunCut = MathF.Max(0f, sc);
        rtgiCountUnknown = args.Flag("rtgi-count-unknown");
        rtgiNoCoarse = args.Flag("rtgi-no-coarse");
        rtgiBackfaceLit = args.Flag("rtgi-backface-lit");
        if (args.Flag("rtgi-no-requests")) rtgiRequests = false;
        fixedSubpixel = args.Flag("fixed-subpixel");
        if (args.Float("rtgi-clamp-sigma") is { } cls) rtgiClampSigma = MathF.Max(0f, cls);
        rtgiScreen = args.Flag("rtgi-screen");
        if (rtgiScreen) rtgi = true;
        if (args.Int("rtgi-screen-taps") is { } taps) rtgiScreenTaps = Math.Clamp(taps, 1, 64);
        if (args.Int("rtgi-screen-radius") is { } rad) rtgiScreenRadius = Math.Clamp(rad, 0, 16);
        if (args.Float("rtgi-reuse-max") is { } rm) rtgiReuseMax = MathF.Max(0f, rm);
        if (args.String("rtgi-dump") is { } dump) rtgiDumpDir = dump;
        if (args.String("rtgi-dump-frames") is { } frames)
            rtgiDumpFrames = new SortedSet<int>(frames.Split(',').Select(f => int.Parse(f, CultureInfo.InvariantCulture)));
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
        rtgiEvictInterface = reflect("rtgi_evict.comp");
        rtgiEvictPassHandle = graph.ComputePass("rtgi-evict").Shader(rtgiEvictInterface).Handle;
        rtgiReservoirInterface = reflect("rtgi_reservoir.comp");
        rtgiReservoirPassHandle = graph.ComputePass("rtgi-reservoir").Shader(rtgiReservoirInterface).Handle;
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
        rtgiScreenInterface = reflect("present.vert", "rtgi_screen.frag");
        rtgiScreenPassHandle = graph.GraphicsPass("rtgi-screen")
            .Target(incidentHalfHandle, LoadOp.Clear, StoreOp.Store)
            .Target(incidentGradientHalfHandle, LoadOp.Clear, StoreOp.Store)
            .Read(SampleableSceneDepth)
            .Read(SampleablePrepassNormal)
            .Shader(rtgiScreenInterface)
            .Handle;
    }

    private void CreateRtgiPipelines(string shaderDir, byte[] presentVertSpv)
    {
        PipelineHandle Compute(string name, ShaderInterface iface) => Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, name + ".comp.spv")), iface, name)), name));
        rtgiTrainPipeline = Compute("rtgi_train", rtgiTrainInterface);
        rtgiResolvePipeline = Compute("rtgi_resolve", rtgiResolveInterface);
        rtgiEvictPipeline = Compute("rtgi_evict", rtgiEvictInterface);
        rtgiRaysPipeline = Compute("rtgi_rays", rtgiRaysInterface);
        rtgiReadoutPipeline = Pipeline(Own(device.CreateShaderProgramFromSpv(
                presentVertSpv, File.ReadAllBytes(Path.Combine(shaderDir, "rtgi_readout.frag.spv")), rtgiReadoutInterface, "rtgi_readout")),
            VertexPosition3NormalTexture.Layout, DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled, BlendState.Disabled }, rtgiReadoutPassHandle, "rtgi_readout");
        rtgiReservoirPipeline = Compute("rtgi_reservoir", rtgiReservoirInterface);
        rtgiScreenPipeline = Pipeline(Own(device.CreateShaderProgramFromSpv(
                presentVertSpv, File.ReadAllBytes(Path.Combine(shaderDir, "rtgi_screen.frag.spv")), rtgiScreenInterface, "rtgi_screen")),
            VertexPosition3NormalTexture.Layout, DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled, BlendState.Disabled }, rtgiScreenPassHandle, "rtgi_screen");
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
            rtgiRequestBufs = Enumerable.Range(0, 2).Select(s => Own(device.CreateGpuBuffer(16 + Math.Max(1, rtgiHitPoints) * 32, new byte[16 + Math.Max(1, rtgiHitPoints) * 32], $"sponza.rtgi.requests{s}"))).ToArray();
            rtgiKeys = Own(device.CreateGpuBuffer(RtgiSlots * 4, new byte[RtgiSlots * 4], "sponza.rtgi.keys"));
            rtgiAccum = Own(device.CreateGpuBuffer(RtgiSlots * 32, new byte[RtgiSlots * 32], "sponza.rtgi.accum"));
            rtgiResolved = Own(device.CreateGpuBuffer(RtgiSlots * 32, new byte[RtgiSlots * 32], "sponza.rtgi.resolved"));
            rtgiMoments = Own(device.CreateGpuBuffer(RtgiSlots * 48, new byte[RtgiSlots * 48], "sponza.rtgi.moments"));
            rtgiStamps = Own(device.CreateGpuBuffer(RtgiSlots * 8, new byte[RtgiSlots * 8], "sponza.rtgi.stamps"));
            rtgiDirtyBufs = Enumerable.Range(0, 2).Select(s => Own(device.CreateGpuBuffer(16 + RtgiDirtyRoom * 4, new byte[16 + RtgiDirtyRoom * 4], $"sponza.rtgi.dirty{s}"))).ToArray();
            if (rtgiScreen)
                rtgiReservoirs = Enumerable.Range(0, 2).Select(s => Own(device.CreateGpuBuffer(rtgiTiles * 64, new byte[rtgiTiles * 64], $"sponza.rtgi.reservoirs{s}"))).ToArray();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] rtgi: {rtgiTiles:N0} visible points and {rtgiHitPoints:N0} from last frame's hits a frame; cache {RtgiSlots:N0} slots ({RtgiSlots * 68 / 1048576.0:0} MB), cells {rtgiCell:0.###} m + {rtgiCellGrow:0.###} per metre, {rtgiHistory:0} samples held, untrained cells emptied after {rtgiMaxAge:0} frames."));
        }
        var cur = (int)(rtgiFrameCount & 1u);
        var prev = 1 - cur;
        var towardSun = -Vector3.Normalize(sunDirection);
        var groups = (n + 63) / 64;
        var sunTurned = rtgiTraced > 0 && Vector3.Dot(towardSun, rtgiLastSun) < MathF.Cos(0.02f * MathF.PI / 180f);
        rtgiLastSun = towardSun;
        if (sunTurned && rtgiSunCut > 0f) rtgiSunEpoch++;
        var frameNumber = rtgiFrameCount + 1u;
        if (rtgiTraced > 0)
            graph.Dispatch(rtgiTrainPassHandle, new DispatchCommand(rtgiTrainPipeline, groups, 1, 1,
                new ShaderUniform[]
                {
                    new("uParams", new Vector4Uniform(RtgiParams)),
                    new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, n))),
                    new("uSun", new Vector4Uniform(new Vector4(towardSun, 1f))),
                    new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
                    new("uClamp", new Vector4Uniform(new Vector4(rtgiClampSigma, frameNumber, RtgiDirtyRoom, 0f))),
                    new("uTrain", new Vector4Uniform(new Vector4(rtgiCountUnknown ? 1f : 0f, rtgiNoCoarse ? 1f : 0f, rtgiBackfaceLit ? 1f : 0f, rtgiRequests ? rtgiHitPoints : 0f))),
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
                    new ShaderBufferBinding("RtgiRequests", rtgiRequestBufs[cur]),
                    new ShaderBufferBinding("RtgiMoments", rtgiMoments),
                    new ShaderBufferBinding("RtgiStamps", rtgiStamps),
                    new ShaderBufferBinding("RtgiDirty", rtgiDirtyBufs[cur]),
                }));
        // The cells trained this frame (the list's room dispatched; past its count each thread returns).
        graph.Dispatch(rtgiResolvePassHandle, new DispatchCommand(rtgiResolvePipeline, (RtgiDirtyRoom + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uResolve", new Vector4Uniform(new Vector4(rtgiHistory, rtgiMaxAge, RtgiSlots, rtgiChangeSigma))),
                new("uChange", new Vector4Uniform(new Vector4(rtgiChangeFrames, rtgiFast, rtgiChangeFloor, rtgiSunCut))),
                new("uList", new Vector4Uniform(new Vector4(rtgiSunEpoch, RtgiDirtyRoom, 0f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                new ShaderBufferBinding("RtgiAccum", rtgiAccum),
                new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                new ShaderBufferBinding("RtgiMoments", rtgiMoments),
                new ShaderBufferBinding("RtgiStamps", rtgiStamps),
                new ShaderBufferBinding("RtgiDirty", rtgiDirtyBufs[cur]),
                new ShaderBufferBinding("RtgiNextDirty", rtgiDirtyBufs[prev]),
            }));
        // A sixteenth of the slots a frame: those no training reached for --rtgi-max-age frames emptied.
        var slice = RtgiSlots / RtgiEvictSlices;
        graph.Dispatch(rtgiEvictPassHandle, new DispatchCommand(rtgiEvictPipeline, (slice + 63) / 64, 1, 1,
            new ShaderUniform[] { new("uEvict", new Vector4Uniform(new Vector4(frameNumber, rtgiMaxAge, (int)(rtgiFrameCount % RtgiEvictSlices) * slice, slice))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                new ShaderBufferBinding("RtgiMoments", rtgiMoments),
                new ShaderBufferBinding("RtgiStamps", rtgiStamps),
            }));

        // S3: last frame's visible points (tile i is point i) into this frame's reservoirs, merged with the frame before's.
        if (rtgiScreen && rtgiTraced > 0)
        {
            var tiles = Math.Min(rtgiTiles, rtgiGrid.X * rtgiGrid.Y);
            graph.Dispatch(rtgiReservoirPassHandle, new DispatchCommand(rtgiReservoirPipeline, (tiles + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uPrevViewProj", new Matrix4x4Uniform(rtgiViewProj2)),
                    new("uParams", new Vector4Uniform(RtgiParams)),
                    new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, tiles))),
                    new("uSun", new Vector4Uniform(new Vector4(towardSun, 1f))),
                    new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
                    new("uGrid", new Vector4Uniform(new Vector4(rtgiGrid.X, rtgiGrid.Y, width, height))),
                    new("uReuse", new Vector4Uniform(new Vector4(rtgiReuseMax, sunTurned ? 1f : 0f, rtgiFrameCount % (1u << 24), rtgiTraced > 1 && rtgiReuseMax > 0f ? 1f : 0f))),
                },
                new[] { new ShaderTextureBinding("uSkyRadiance", envCubeTexture) },
                Buffers: new[]
                {
                    new ShaderBufferBinding("RtgiPoints", rtgiPointsBuf[prev]),
                    new ShaderBufferBinding("RtgiRayBuffer", rtgiRaysBuf[prev]),
                    new ShaderBufferBinding("RtgiHits", rtgiHitsBuf[prev]),
                    new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                    new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                    new ShaderBufferBinding("RtgiPrevReservoirs", rtgiReservoirs[prev]),
                    new ShaderBufferBinding("RtgiReservoirs", rtgiReservoirs[cur]),
                }));
            rtgiReservoirCur = cur;
            rtgiGridViewProj = rtgiViewProj1;
        }

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
                new ShaderBufferBinding("RtgiRequests", rtgiRequestBufs[cur]),
                new ShaderBufferBinding("RtgiNextRequests", rtgiRequestBufs[prev]),
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
        rtgiGrid = (tilesX, tilesY);
        rtgiViewProj2 = rtgiViewProj1;
        rtgiViewProj1 = viewProjJittered;
        rtgiFrameCount++;
        rtgiTraced++;
    }

    private void RecordRtgiReadout()
    {
        Matrix4x4.Invert(viewProjJittered, out var invViewProj);
        var sub = LightingSubpixel;
        if (rtgiScreen && rtgiReservoirCur >= 0)
        {
            graph.Pass(rtgiScreenPassHandle, scope => fullscreen.Draw(
                scope, rtgiScreenPipeline,
                new[]
                {
                    new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                    new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                },
                pushConstants: null,
                uniforms: new ShaderUniform[]
                {
                    new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                    new("uGridViewProj", new Matrix4x4Uniform(rtgiGridViewProj)),
                    new("uParams", new Vector4Uniform(RtgiParams)),
                    new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, rtgiFrameCount % (1u << 24)))),
                    new("uHalf", new Vector4Uniform(new Vector4(0f, sub.X, sub.Y, 0f))),
                    new("uGrid", new Vector4Uniform(new Vector4(rtgiGrid.X, rtgiGrid.Y, rtgiScreenTaps, rtgiScreenRadius))),
                },
                buffers: new[]
                {
                    new ShaderBufferBinding("RtgiKeys", rtgiKeys), new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                    new ShaderBufferBinding("RtgiReservoirs", rtgiReservoirs[rtgiReservoirCur]),
                }));
            return;
        }
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

    // --rtgi-dump DIR --rtgi-dump-frames F1,F2,...: at those post-load frames the incident field the lit pass read
    // (full res), the pre-pass normal and the per-pixel path trace if it runs (--gi-reference), for
    // tools/gi-model/rtgi_timeline.py to hold against a truth boot's trace.
    private string? rtgiDumpDir;
    private SortedSet<int> rtgiDumpFrames = new();

    private void RtgiDumpIfDue()
    {
        if (rtgiDumpDir is null || rtgiDumpFrames.Count == 0 || postLoadFrames < rtgiDumpFrames.Min) return;
        var frame = rtgiDumpFrames.Min;
        rtgiDumpFrames.Remove(frame);
        var dir = Path.Combine(rtgiDumpDir, string.Create(CultureInfo.InvariantCulture, $"f{frame}"));
        Directory.CreateDirectory(dir);
        device.WaitIdle();
        void Texture(string name, TextureHandle handle) => File.WriteAllBytes(Path.Combine(dir, name), device.ReadTexture(handle, out _, out _, out _));
        Texture("incident.rgba16f", graph.GetColorTexture(incidentHandle));
        Texture("normal.rgba16f", graph.GetColorTexture(SampleablePrepassNormal));
        if (referenceSamples > 0)
        {
            File.WriteAllBytes(Path.Combine(dir, "reference.f32x4"), device.ReadGpuBuffer(referenceAccum, 0, referenceSize.X * referenceSize.Y * 16));
            File.WriteAllBytes(Path.Combine(dir, "reference_moments.f32"), device.ReadGpuBuffer(referenceMoments, 0, referenceSize.X * referenceSize.Y * 4));
        }
        var (w, h) = profFrameSize;
        File.WriteAllText(Path.Combine(dir, "meta.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            frame = postLoadFrames, width = w, height = h, referenceWidth = referenceSize.X, referenceHeight = referenceSize.Y,
            referenceSamples, sunToward = new[] { -sunDirection.X, -sunDirection.Y, -sunDirection.Z }, camera = new[] { cameraPosition.X, cameraPosition.Y, cameraPosition.Z },
        }));
        Console.WriteLine($"[VulkanSponza] rtgi dump at frame {postLoadFrames}: {dir}");
    }

    private bool RtgiReadoutReady => rtgi && !rtgiKeys.Equals(default(GpuBufferHandle));

    // At the profile window: how full the cache is and how much its cells hold.
    private string RtgiCounts()
    {
        if (!RtgiReadoutReady || rtgiTraced < 2) return "";
        // A thirty-second of the slots (hashed, so any run of them is a fair sample), scaled up: reading them all was ~670 MB
        // at 8 M slots, a 400 ms hitch every profile window (San Miguel, 2026-10-26).
        const int Share = 32;
        var count = RtgiSlots / Share;
        device.WaitIdle();
        var keys = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtgiKeys, 0, count * 4).AsSpan());
        var resolved = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtgiResolved, 0, count * 32).AsSpan());
        var moments = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtgiMoments, 0, count * 48).AsSpan());
        double cutsLight = 0;
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
            cutsLight += moments[3 * s].W;
            var e = resolved[2 * s];
            energy += 0.2126 * e.X + 0.7152 * e.Y + 0.0722 * e.Z;
        }
        used *= Share;
        trained *= Share;
        bad *= Share;
        samples *= Share;
        energy *= Share;
        cutsLight *= Share;
        return string.Create(CultureInfo.InvariantCulture,
            $"rtgi: ~{used:N0} of {RtgiSlots:N0} slots held ({100.0 * used / RtgiSlots:0.0}%), {trained:N0} trained, {samples / Math.Max(1, trained):0.0} samples a cell, history cut {cutsLight / Math.Max(1, trained):0.00} times a cell, mean indirect {energy / Math.Max(1, trained):0.000}; {bad:N0} not finite{firstBad}; {rtgiTraced:N0} frames traced");
    }
}
