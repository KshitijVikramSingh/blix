using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanSponza;

internal sealed partial class SponzaLoop
{
    // Build a graphics pipeline for a render-graph pass surface. Every scene
    // pipeline shares Triangles topology + a pass-surface target; only the
    // program/layout/depth/raster/blend (+ AlphaToCoverage) vary, so this trims
    // the ten near-identical PipelineDescription literals to one line each.
    private PipelineHandle Pipeline(
        ShaderProgramHandle program, VertexLayout layout, DepthState depth,
        RasterizerState raster, BlendState[] blend, PassHandle target, string name,
        bool alphaToCoverage = false) =>
        Own(device.CreatePipeline(new PipelineDescription(
            program, layout, PrimitiveTopology.Triangles, depth, raster, blend,
            RenderTarget: graph.GetPassSurface(target),
            AlphaToCoverage: alphaToCoverage), name));

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        device = graphicsDevice;
        // The scene is drawn as instanced multi-draw indirect, each record's firstInstance where its run of
        // placements starts (FillIndirect). Both are optional device features, and validation cannot see an
        // indirect buffer's contents, so their absence is refused here by name rather than drawn wrong.
        const GraphicsFeatures needed = GraphicsFeatures.MultiDrawIndirect | GraphicsFeatures.DrawIndirectFirstInstance;
        if ((device.Features & needed) != needed)
        {
            BlixApps.ReportFailure(
                $"this renderer draws with {needed}, and the device enabled only {device.Features}");
            host.RequestClose();
            return;
        }
        gpuPasses = new GpuPassWindow(host.Timing);
        textureLoader = new MaterialTextureLoader(device);
        // --no-textures: every material on the defaults, no texture loaded (what the frame costs without them).
        if (args.Flag("no-textures")) textureLoader.SkipTextures = true;
        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        renderHeightPx = host.LogicalSize.Height;

        if (args.Float("sun-disc") is { } sunDisc) shadows.SunDiscDegrees = Math.Clamp(sunDisc, 0f, 5f);
        if (args.Flag("penumbra-filter")) shadows.PenumbraFilter = true;
        if (args.Flag("rt-shadow-interleave")) shadows.Interleave = true;
        if (args.Flag("no-shadow")) shadows.Enabled = false;
        if (args.Float("cloth-transmission") is { } cloth) clothTransmission = Math.Clamp(cloth, 0f, 1f);
        materialOcclusion = !args.Flag("no-material-ao");
        materialAoOnGi = args.Flag("material-ao-on-gi");
        // --rt-shadow-full: every pixel traced every frame (ShadowsSettings.Interleave off).
        if (args.Flag("rt-shadow-full")) shadows.Interleave = false;
        if (args.Flag("no-rt-shadow-move-interleave")) rtShadowMoveInterleave = false;
        if (args.Flag("no-prepass")) noPrepass = true;
        if (args.String("probe") is { } probe) probeName = probe;
        // Zero isolates sky-fed transport from the direct-sun source for probe censuses.
        if (args.Float("sun-strength") is { } ss) sunStrength = MathF.Max(0f, ss);
        // --ref-bounces N: the path trace's bounces (3; the transport solves all of them, so judging it needs enough
        // that the rest is negligible -- 0.73^16 is under 1%). --ref-paths N (1024) and --ref-seed S (0): the reference's
        // own noise, measured by two seeds.
        if (args.Int("ref-bounces") is { } rb) refBounces = Math.Clamp(rb, 1, 64);
        if (args.Int("ref-paths") is { } rp) refPaths = Math.Clamp(rp, 16, 1 << 20);
        if (args.Int("ref-seed") is { } rs) refSeed = rs;
        if (args.Float("ref-lift") is { } rl) refLift = Math.Max(1e-5f, rl);
        if (args.Float("ref-sky") is { } rsk) refSky = Math.Max(0f, rsk);
        refSunDisc = args.String("ref-sun") switch
        {
            null or "disc" => true,
            "point" => false,
            _ => throw new AppArgsException("--ref-sun takes disc or point."),
        };
        if (args.Float("foliage-lod") is { } fl) FoliageLodMargin = MathF.Max(0.1f, fl);
        if (scene.Exposure is { } se) render.Exposure = se;
        // --exposure X: the display exposure from the start (the testbeds' sun and sky read white at Sponza's).
        if (args.Float("exposure") is { } ex) render.Exposure = Math.Clamp(ex, 0.05f, 16f);
        // The A/B for the single-sample canopy: hashed stochastic cutout against the plain binary
        // one. Re-cooks nothing and rebuilds nothing — it changes one number in the material.
        if (args.Flag("msaa1")) MsaaSamples = 1;
        if (args.Flag("msaa2")) MsaaSamples = 2;
        // Present so the sample count can be swept from the command line in BOTH directions. Without
        // it only the non-default could be asked for, so a paired run could not be ordered 4-2-2-4 —
        // and on this machine a single ordering is not a measurement.
        if (args.Flag("msaa4")) MsaaSamples = 4;
        // Align shadows and direct light to a detected environment sun by default. --sun-authored
        // opts out; probes without a detectable sun naturally retain the authored direction.
        if (!args.Flag("sun-authored")) alignSunToProbe = true;
        // --sun-overhead maximizes directly lit courtyard area when isolating base lighting.
        if (args.Flag("sun-overhead")) sunOverhead = true;
        // --sun, --sun-change (SponzaLoop.Sun.cs).
        ReadSunArgs(args);
        // GPU isolation submits and waits per pass. It attributes real tile execution but removes
        // overlap, so isolated pass times are not additive components of the normal frame.
        if (args.Flag("gpu-isolate")) host.Timing.IsolatePasses = true;
        if (args.Flag("no-foliage")) noFoliage = true;
        // --cpu-cull: build the indirect records and visible lists on the CPU, the way 1a did, for an A/B
        // against the GPU cull (scene_cull.comp) that is otherwise the default.
        gpuCull = !args.Flag("cpu-cull");
        // --lit-flat: the lit pass shades opaque geometry with flat.frag, everything else unchanged (same lit.vert,
        // culling, pre-pass and LOD). What a cost does under it is the part that is not material shading.
        litFlat = args.Flag("lit-flat");
        // --no-lit-early-z: opaque groups shade with the cutout build too (late depth test), as before 2026-10-24.
        litEarlyZ = !args.Flag("no-lit-early-z");
        litLean = !args.Flag("no-lit-lean");
        iblBrdfLut = args.Flag("ibl-brdf-lut");
        shadowKeep = !args.Flag("no-shadow-keep");
        ParseRtgi(args);
        ParseGpuCapture(args);
        ParseRtgiBench(args);
        ParseMetalFx(args);
        ParseRtgiTemporal(args);
        rayLeakCensus = args.Flag("ray-leak-census");
        memoryReport = args.Flag("memory-report");
        if (args.Flag("occlusion-audit")) occlusionAuditEvery = Math.Max(1, args.Int("occlusion-audit-every") ?? 30);
        if (args.Flag("settle-log")) settleLogEvery = Math.Max(1, args.Int("settle-every") ?? 60);
        if (args.String("settle-dir") is { } settleAt) { settleDir = settleAt; Directory.CreateDirectory(settleAt); }
        // RenderSettings.HalfResShadows from the command line, for scripted runs.
        if (args.Flag("half-res-shadows")) render.HalfResShadows = true;
        if (args.Flag("full-res-shadows")) render.HalfResShadows = false;
        metalDeviceQueue = args.Flag("metal-device-queue");
        tracerBatchesApart = args.Flag("tracer-batches-apart");
        // --no-frame-chain: frames may overlap on the GPU again (the A/B for IGraphicsDevice.ChainFrames).
        if (args.Flag("no-frame-chain")) device.ChainFrames = false;
        if (args.Flag("no-pass-barriers")) device.ExplicitPassBarriers = false;
        // --rt-no-overlap: no join, so the frame waits for the trace right after it (the overlap's control).
        rtNoOverlap = args.Flag("rt-no-overlap");
        rtNoPack = args.Flag("no-rt-pack");
        // --rt-isolated: nothing but the trace lights the frame, sky included: no incident field (SponzaLoop.RtShadows).
        rtIsolated = args.Flag("rt-isolated");
        rtCoverageCoin = args.Flag("rt-coverage-coin");
        if (args.Float("rt-alpha-mip") is { } alphaMip) rtAlphaMip = Math.Clamp(alphaMip, 0f, 12f);
        rtAlphaAb = args.Flag("rt-alpha-ab");
        // --no-taa: no TAA and so no sub-pixel jitter: the control for --stability, whose variation otherwise
        // counts every edge the jitter moves.
        if (args.Flag("no-taa")) render.Taa = 0f;
        // --taa X: the history weight (the overlay's "Taa"), for measuring what it leaves of the jitter.
        if (args.Float("taa") is { } taa) render.Taa = Math.Clamp(taa, 0f, 0.97f);
        // --taa-show-rejection: the resolve outputs 1 where it clamped history, 0 where it kept it; with --stability
        // the presented image's mean is then the rejection rate.
        if (args.Flag("taa-show-rejection")) render.ShowTaaRejection = true;
        if (args.String("taa-reproject") is { } reproject) taaMotionReprojection = reproject switch
        {
            "motion" => true, "old" => false,
            _ => throw new AppArgsException($"--taa-reproject takes motion or old, not '{reproject}'."),
        };
        if (args.String("taa-clip") is { } clip) taaVarianceClip = clip switch
        {
            "variance" => true, "box" => false,
            _ => throw new AppArgsException($"--taa-clip takes variance or box, not '{clip}'."),
        };
        if (args.Float("taa-gamma") is { } gamma) taaGamma = Math.Max(0.1f, gamma);
        taaCountRejection = args.Flag("taa-count-rejection");
        if (args.Float("taa-relax") is { } relax) taaRelax = Math.Max(1f, relax);
        if (args.Float("taa-accumulate") is { } accumulate) taaAccumulate = Math.Max(0f, accumulate);
        // --stability K: per-pixel temporal variation over the K still frames before the shot (SponzaLoop.Stability).
        if (args.Int("stability") is { } stability) stabilityFrames = Math.Max(2, stability);
        if (args.Float("taa-dynamic-count") is { } tdc) taaDynamicCount = Math.Max(1f, tdc);
        ParseMoverArgs();
        // --surface-check: hold the pre-pass's SurfaceKey and velocity against CPU rays at the shot (stage 4e-iv).
        surfaceCheck = args.Flag("surface-check");
        if (args.Float("incident-normal-bias") is { } nb) incidentNormalBias = Math.Clamp(nb, 0f, 12f);
        if (args.Float("incident-gradient-clamp") is { } gc) incidentGradientClamp = Math.Clamp(gc, 0f, 4f);
        if (args.Int("ray-region-triangles") is { } regionTriangles) rayRegionTriangles = Math.Max(1, regionTriangles);
        if (args.Float("ray-lod-error") is { } lodError) rayLodError = Math.Max(0f, lodError);
        // --no-occlusion: the GPU cull without its two-phase occlusion test (scene_occlusion.comp), for the A/B.
        occlusionCull = gpuCull && !args.Flag("no-occlusion");
        // --occlusion-cut: every frame is a camera cut. The early list draws nothing, so the late list carries
        // the whole frame: the case history cannot help, and the proof that the late list is complete.
        occlusionCut = args.Flag("occlusion-cut");
        // --gpu-reference: the gate's surface reference path-traced on the GPU (SponzaLoop.GpuReference.cs).
        gpuReference = args.Flag("gpu-reference");
        // --ref-in-frame: the gate's reference wave beside the scene's frames, as before; otherwise behind the cook screen.
        refInFrame = args.Flag("ref-in-frame");
        if (args.Int("gpu-ref-paths") is { } grp) gpuReferencePathsWanted = Math.Clamp(grp, 256, 1 << 22);
        if (args.Int("gpu-ref-lanes") is { } grl) referenceLanesAsked = Math.Clamp(grl, 64, 8192);
        if (args.Int("gpu-ref-seams") is { } grs) gpuReferenceSeams = Math.Clamp(grs, 0, 1024);
        if (args.Int("shot-hold") is { } hold) ShotReadyMargin = Math.Max(1, hold);
        ReadWalkArgs(args);
        // --no-cook-screen: the scene drawn while the frame's tracer builds or the reference traces (SponzaLoop.CookScreen.cs).
        cookScreenOff = args.Flag("no-cook-screen");
        // --lod-pixels E: the LOD error budget (0: full detail everywhere) -- the [Tune] field is not reachable from the
        // command line.
        if (args.Float("lod-pixels") is { } lod) lodPixelsOverride = Math.Max(0f, lod);
        // --cam x,y,z,yaw,pitch — a reproducible viewpoint. Without it every capture and every
        // census speaks only for wherever the camera happens to start, which for a question like
        // "how much of this scene is occluded" is the difference between a measurement and an
        // anecdote.
        camera.ReadArgs(args);
        if (args.Flag("ab-flat")) { abFlat = true; abMode = "flat"; }
        if (args.String("ab") is { } ab)
        {
            abMode = ab;
            abFlat = abMode == "flat";
        }
        Console.WriteLine("[VulkanSponza] rt shadows: sun visibility from rays (Metal); the sun is off until the trace is ready.");
        if (args.Float("viz") is { } vizValue) vizChannel = vizValue;
        // --lod-arms <onPx> <offPx>: the two budgets --ab lod alternates between.
        if (args.Values("lod-arms", 2) is [var lodOn, var lodOff])
        {
            lodArmOn = float.Parse(lodOn, CultureInfo.InvariantCulture);
            lodArmOff = float.Parse(lodOff, CultureInfo.InvariantCulture);
        }
        // Timing runs require --no-vsync; FIFO quantizes frame periods to refresh intervals and can
        // reverse small A/B differences.
        if (args.Flag("no-vsync")) device.VsyncEnabled = false;
        // --shot <path>: render --shot-frames frames, write the scene there (linear x exposure, sRGB
        // encoded; WriteSceneShot) and the shot's other read-outs beside it, and close. Headless in the
        // sense that matters — nobody has to be watching.
        if (args.String("shot") is { } shot) shotPath = shot;
        if (args.Int("shot-frames") is { } sf) shotFrame = sf;
        shotWhenReady = args.Flag("shot-when-ready");
        framesAfterLoad = args.Int("frames-after-load");
        occlusionAlways = args.Flag("occlusion-always");
        ParseSunMove(args);
        if (args.Float("render-scale") is { } renderScaleArg) render.RenderScale = Math.Clamp(renderScaleArg, 0.5f, 1f);

        // Seed sun yaw/pitch from the default direction so the Sun controls
        // start matching the baked look.
        sunPitch = MathF.Asin(Math.Clamp(sunDirection.Y, -1f, 1f));
        sunYaw = MathF.Atan2(sunDirection.X, -sunDirection.Z);


        if (!TryLocateScene(out var assetsRoot, out var packsToParse))
        {
            // A run that asked for the loaded scene and never loaded one has not passed.
            if (framesAfterLoad is not null || shotPath is not null)
                BlixApps.ReportFailure($"the {scene.Name} pack set is missing, so the loaded scene never rendered");
            return;
        }
        LoadIbl(assetsRoot);
        ChooseRayStore(assetsRoot);
        // The moving sun's north is set from this sun (BandNorth), whatever --sun or a move turns it to.
        authoredSunDirection = sunDirection;
        // --sun yaw,pitch (degrees; pitch negative, below the horizon of travel): a fixed sun for a measurement.
        if (sunOverrideDegrees is { } sunDeg)
        {
            sunYaw = sunDeg.X * MathF.PI / 180f;
            sunPitch = sunDeg.Y * MathF.PI / 180f;
            UpdateSunDirection();
            Console.WriteLine($"[VulkanSponza]   sun set to {sunDeg.X:0.##}, {sunDeg.Y:0.##} (yaw, pitch; {(args.Values("sun", 2) is not null ? "--sun" : "the scene's default")}): {sunDirection}");
            // The scene's default sun is its authored one: the moving sun's day (BandNorth) is fitted around it, as it was
            // around the probe's. --sun is a measurement and leaves the day where it was.
            if (args.Values("sun", 2) is null) authoredSunDirection = sunDirection;
        }

        // --- Render graph ------------------------------------------------
        graph = new RenderGraph(device);
        var fullSize = new MatchSwapchainGraphSize(1.0f);
        // R11G11B10F (not Rgba16F): half the bytes/pixel → half the MSAA tile
        // footprint + resolve bandwidth on TBDR, for an opaque HDR radiance
        // target only ever sampled .rgb by tonemap. No alpha (glass blends with
        // source alpha, which needs no dst-alpha channel).
        hdrHandle = graph.ColorTarget("hdr", TextureFormat.R11G11B10F, fullSize);
        // TAA ping-pongs because each resolve samples prior output while writing the next image.
        for (var i = 0; i < 2; i++)
            taaHandles[i] = graph.ColorTarget($"taa{i}", TextureFormat.Rgba16F, fullSize);
        if (SurfaceTargets)
        {
            surfaceKeyHistoryHandle = graph.ColorTarget("surface-key-history", TextureFormat.R32Uint, fullSize);
        }
        // MSAA colour + depth the lit pass renders into; resolves to hdr.
        // Only with MSAA (the single-sample lit pass renders straight into hdr): 18 MB unused otherwise.
        if (MsaaSamples > 1) hdrMsaaHandle = graph.ColorTarget("hdr-msaa", TextureFormat.R11G11B10F, fullSize, samples: MsaaSamples);
        depthHandle = graph.DepthTarget("scene-depth", fullSize, samples: MsaaSamples);

        // The per-frame Frame UBO (view-proj, sun, camera, the incident field's switches) and
        // its //@tune-decorated shader tunables are declared in frame.glsl; their
        // std140 offsets are reflected from the SPIR-V, not hand-authored here.
        // Shader binding interfaces — descriptor sets, std140 UBO layouts, and
        // push-constant ranges — are reflected from the compiled SPIR-V at
        // build time (spirv-cross sidecars next to each .spv), not hand-
        // authored. See docs/architecture.md → the Vulkan binding model. Each program
        // reflects exactly what its stages declare, and a draw binds by name only
        // what its program declares: naming a texture the program lacks throws.
        // So the skybox has its own list of the one texture it samples rather
        // than being handed the lit pass's.
        var shaderDir = AppFiles.Shaders;
        ShaderInterface Reflect(params string[] stages) =>
            ShaderReflection.ForProgram(shaderDir, stages);

        var litInterface = Reflect("lit.vert", "lit.frag");
        var skyInterface = Reflect("skybox.vert", "skybox.frag");
        var presentInterface = Reflect("present.vert", "present.frag");
        // Reuses present.vert: both are fullscreen triangles synthesised from gl_VertexIndex.
        var hiZInterface = Reflect("present.vert", "hiz_build.frag");

        // Programs sharing the frame buffer must agree on reflected std140 offsets across program
        // boundaries; stage merging validates only one program at a time.
        AssertFrameBlockAgrees(litInterface, ("skybox", skyInterface));

        // Scan lit.frag's //@tune decorators (shipped alongside the .spv) and
        // build the overlay's shader-variable panel. The panel owns the live
        // values + the dials; the per-frame write pulls from it by name.
        // Read from the sidecar the shader compiler wrote beside lit.frag.spv, rather than
        // rescanning GLSL here. The build has the expanded source with every include resolved;
        // this process has neither, which is why the decorators in frame.glsl were invisible to
        // a load-time scan and why the application used to ship its shader sources at all.
        tunePanel = new ShaderTunablePanel(
            ShaderTunableSidecar.Load(Path.Combine(shaderDir, "lit.frag.spv")));
        tuneObjects = new ObjectTunables(shadows, render, camera);

        // --tune <uName>=<value>, repeatable. A shader dial that can only be reached from the
        // overlay cannot be measured, because an --ab run has no overlay.
        foreach (var tune in args.All("tune"))
        {
            var kv = tune.Split('=');
            if (kv.Length != 2 || !float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var tv))
            {
                throw new AppArgsException($"--tune expects uName=value, got '{tune}'.");
            }

            Console.WriteLine(tunePanel.TrySetValue(kv[0], tv)
                ? $"[VulkanSponza] tune {kv[0]} = {tv}"
                : $"[VulkanSponza] tune {kv[0]}: no such shader uniform — ignored.");
        }

        // The scene cull: every pass's indirect records and visible list, written before the first pass
        // that draws from them (the pre-pass), so it is declared ahead of all of them. Its dispatches
        // are fenced on both sides because they bind GPU buffers; the graph tracks images only.
        // The mover's rows are written first of all, before anything culls or draws from the transform table.
        moverInterface = Reflect("mover.comp");
        moverPassHandle = graph.ComputePass("mover").Shader(moverInterface).Handle;
        moverRaysInterface = Reflect("mover_rays.comp");
        moverRaysPassHandle = graph.ComputePass("mover-rays").Shader(moverRaysInterface).Handle;
        cullInterface = Reflect("scene_cull.comp");
        cullPassHandle = graph.ComputePass("scene-cull").Shader(cullInterface).Handle;
        raySurfaceBakeInterface = Reflect("ray_surface_bake.comp");
        raySurfaceBakePassHandle = graph.ComputePass("ray-surface-bake").Shader(raySurfaceBakeInterface).Handle;

        // The ray batches' interfaces (SponzaLoop.RayBatch.cs), before the first consumer declares a batch.
        batchClosestInterface = Reflect("ray_batch_closest.comp");
        batchShadowInterface = Reflect("ray_batch_shadow.comp");
        // After every deferred batch's producers (SponzaLoop.TracerGroup.cs): a frame without traced screen shadows.
        tracerGroupPassHandle = graph.ExternalPass("tracer-group");

        // Depth pre-pass — declared before lit; clears + writes the (4× MSAA)
        // scene depth for all non-blend geometry. Both pre-pass programs reuse
        // litInterface (lit.vert needs set 0 + the model push; the trivial
        // fragments use a subset), so the lit material descriptor set binds to
        // the mask variant unchanged.
        // The 1x depth the passes after it sample. Only under MSAA: at one sample the depth target is
        // already what a reader wants.
        if (MsaaSamples > 1) depthResolveHandle = graph.DepthTarget("scene-depth-1x", fullSize);

        // Incident light, full resolution: rgb RTGI's incoming light, a sky visibility. Rgba16F preserves HDR gradients.
        incidentHandle = graph.ColorTarget("incident-light", TextureFormat.Rgba16F, fullSize);
        incidentGradientHandle = graph.ColorTarget("incident-gradient", TextureFormat.Rgba16F, fullSize);
        // RTGI's readout into these, lighting-upsample back into the two above.
        incidentHalfHandle = graph.ColorTarget("incident-light-half", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(0.5f));
        incidentGradientHalfHandle = graph.ColorTarget("incident-gradient-half", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(0.5f));

        // The pre-pass writes geometric normals for incident reconstruction instead of inferring
        // them from depth. That inference measured 5.31 mean sRGB error. Rgba16F avoids directional
        // quantization on smooth curvature.
        prepassNormalHandle = graph.ColorTarget(
            "prepass-normal", TextureFormat.Rgba16F, fullSize, samples: MsaaSamples);
        if (MsaaSamples > 1)
        {
            prepassNormalResolveHandle = graph.ColorTarget(
                "prepass-normal-1x", TextureFormat.Rgba16F, fullSize);
        }

        if (SurfaceTargets)
        {
            surfaceKeyHandle = graph.ColorTarget("surface-key", TextureFormat.R32Uint, fullSize);
            velocityHandle = graph.ColorTarget("velocity", TextureFormat.Rg16F, fullSize);
            if (MotionTarget) motionHandle = graph.ColorTarget("world-motion", TextureFormat.Rgba16F, fullSize);
        }
        var prepassBuilder = graph.GraphicsPass("depth-prepass")
            .Target(prepassNormalHandle, LoadOp.Clear, StoreOp.Store)
            .Depth(depthHandle, LoadOp.Clear, StoreOp.Store)
            .Shader(litInterface);
        if (SurfaceTargets)
        {
            prepassBuilder = prepassBuilder.Target(surfaceKeyHandle, LoadOp.Clear, StoreOp.Store)
                .Target(velocityHandle, LoadOp.Clear, StoreOp.Store);
            if (MotionTarget) prepassBuilder = prepassBuilder.Target(motionHandle, LoadOp.Clear, StoreOp.Store);
        }
        // Same rule as the depth: at one sample the target IS what a reader wants, and asking for
        // a resolve anyway is invalid.
        if (MsaaSamples > 1) prepassBuilder = prepassBuilder.ResolveColor(prepassNormalResolveHandle);
        // Rides the pass's depth store when there is something to resolve.
        if (MsaaSamples > 1) prepassBuilder = prepassBuilder.ResolveDepth(depthResolveHandle);
        depthPrepassHandle = prepassBuilder.Handle;

        // Two-phase occlusion culling (scene_occlusion.comp). The pre-pass above is its early half: what was
        // visible last frame. Its depth is reduced into a pyramid of its own, every placement is tested
        // against it, and what is visible but was not drawn is drawn by the late pre-pass into the same
        // depth and normals, before everything after reads them.
        occlusionInterface = Reflect("scene_occlusion.comp");
        for (var level = 0; level < HiZLevels; level++)
        {
            occZHandles[level] = graph.ColorTarget(
                $"occ-z{level}", TextureFormat.Rg16F, new MatchSwapchainGraphSize(0.5f / (1 << level)));   // min, max (hiz_build.frag)
        }
        for (var level = 0; level < HiZLevels; level++)
        {
            var builder = graph.GraphicsPass($"occ-z{level}").Target(occZHandles[level], LoadOp.Clear, StoreOp.Store);
            builder = level == 0 ? builder.Read(SampleableSceneDepth) : builder.Read(occZHandles[level - 1]);
            occZPassHandles[level] = builder.Shader(hiZInterface).Handle;
        }
        var occlusionPass = graph.ComputePass("occlusion-cull").Shader(occlusionInterface);
        for (var level = 0; level < HiZLevels; level++) occlusionPass = occlusionPass.Read(occZHandles[level]);
        occlusionPassHandle = occlusionPass.Handle;
        var lateBuilder = graph.GraphicsPass("depth-prepass-late")
            .Target(prepassNormalHandle, LoadOp.Load, StoreOp.Store)
            .Depth(depthHandle, LoadOp.Load, StoreOp.Store)
            .Shader(litInterface);
        if (SurfaceTargets)
        {
            lateBuilder = lateBuilder.Target(surfaceKeyHandle, LoadOp.Load, StoreOp.Store)
                .Target(velocityHandle, LoadOp.Load, StoreOp.Store);
            if (MotionTarget) lateBuilder = lateBuilder.Target(motionHandle, LoadOp.Load, StoreOp.Store);
        }
        if (MsaaSamples > 1) lateBuilder = lateBuilder.ResolveColor(prepassNormalResolveHandle).ResolveDepth(depthResolveHandle);
        latePrepassHandle = lateBuilder.Handle;

        // The traced shadows: the shadow rays as soon as the pre-pass has the depth and normals they start from, and the Metal
        // pass that traces them; the lit pass joins it (rt-shadow-join, below). Everything between (RTGI's readout)
        // reads none of it, so it runs while the rays are traced.
        rtShadowRaysInterface = Reflect("rt_shadow_rays.comp");
        rtShadowRaysPassHandle = graph.ComputePass("rt-shadow-rays").Shader(rtShadowRaysInterface)
            .Read(SampleableSceneDepth).Read(SampleablePrepassNormal).Handle;
        // The cache's rays traced in a Metal pass of their own before the sun's, where neither async compute nor the tracer
        // groups take them (SponzaLoop.Rtgi.cs).
        rtgiInlineTracePassHandle = graph.ExternalPass("rtgi-inline-trace");
        DeclareRtgiComputePasses(stage => Reflect(stage));
        rtShadowTracePassHandle = graph.ExternalPass("rt-shadow-trace");
        // The cache's rays (SponzaLoop.Rtgi.cs): traced after the sun's, which the lit pass waits for, and read only by the
        // cache's async update -- an async external pass, nothing in the frame waiting for it.
        rtgiTracePassHandle = graph.ExternalPass("rtgi-trace", async: true);

        // The gate's reference as a wavefront (SponzaLoop.GpuReference.cs), its batches between its steps, in frame order.
        referenceWaveInterface = Reflect("reference_wave.comp");
        referenceWaveStepPass = graph.ComputePass("reference-wave-step").Shader(referenceWaveInterface).Handle;
        referenceClosestBatch = DeclareClosestBatch("reference-closest");
        referenceWaveShadePass = graph.ComputePass("reference-wave-shade").Shader(referenceWaveInterface).Handle;
        referenceSunBatch = DeclareClosestBatch("reference-sun");
        referenceWaveResolvePass = graph.ComputePass("reference-wave-resolve").Shader(referenceWaveInterface).Handle;

        // The incident targets cleared and nothing drawn, for a frame with no readout yet (the cache not built, the
        // Metal tracer pending): the lit pass then reads no indirect light. Its interface is the upsample's, which
        // writes the same two targets.
        lightingUpsampleInterface = Reflect("present.vert", "lighting_upsample.frag");
        incidentPassHandle = graph.GraphicsPass("incident-clear")
            .Target(incidentHandle, LoadOp.Clear, StoreOp.Store)
            .Target(incidentGradientHandle, LoadOp.Clear, StoreOp.Store)
            .Shader(lightingUpsampleInterface)
            .Handle;
        // RTGI: the cache read into the half-res targets (SponzaLoop.Rtgi.cs).
        DeclareRtgiReadoutPass((vert, frag) => Reflect(vert, frag));


        // The traced shadows are needed from here on.
        if (!rtNoOverlap) graph.ExternalJoin(rtShadowTracePassHandle, "rt-shadow-join");
        // RTGI: the cache's update, after its last reader (the readout's blur) and the trace it trains from (the join
        // above): async beside the rest of the frame (SponzaLoop.Rtgi.cs).
        DeclareRtgiCachePasses(stage => Reflect(stage));
        // Interleaved (ShadowsSettings.Interleave): the untraced pixels' words, before the filter reads the mask.
        rtShadowFillInterface = Reflect("rt_shadow_fill.comp");
        rtShadowFillPassHandle = graph.ComputePass("rt-shadow-fill").Shader(rtShadowFillInterface)
            .Read(SampleableSceneDepth).Read(SampleablePrepassNormal).Handle;
        // The penumbra filtered in space (ShadowsSettings.PenumbraFilter): after the trace, before anything reads the mask.
        rtShadowFilterInterface = Reflect("rt_shadow_filter.comp");
        rtShadowFilterPassHandle = graph.ComputePass("rt-shadow-filter").Shader(rtShadowFilterInterface)
            .Read(SampleableSceneDepth).Read(SampleablePrepassNormal).Handle;

        // Half-res lighting back to full resolution (lighting_upsample.frag): after the join, since it reads the traced
        // mask too, and into the targets the lit pass reads.
        lightingUpsamplePassHandle = graph.GraphicsPass("lighting-upsample")
            .Target(incidentHandle, LoadOp.Clear, StoreOp.Store)
            .Target(incidentGradientHandle, LoadOp.Clear, StoreOp.Store)
            .Read(incidentHalfHandle)
            .Read(incidentGradientHalfHandle)
            .Read(SampleableSceneDepth)
            .Read(SampleablePrepassNormal)
            .Shader(lightingUpsampleInterface)
            .Handle;
        // --rtgi-temporal: the raw upsample, the accumulation into the targets above, and its copy (SponzaLoop.RtgiTemporal.cs).
        DeclareRtgiTemporalPasses((vert, frag) => Reflect(vert, frag), fullSize);

        // At one sample there is nothing to resolve, and asking for a resolve anyway is invalid —
        // so the single-sample path renders straight into the target present reads.
        var litPass = MsaaSamples > 1
            ? graph.GraphicsPass("lit-scene")
                .Target(hdrMsaaHandle, LoadOp.Clear, StoreOp.Store)   // render 4× MSAA
                .ResolveColor(hdrHandle)                              // resolve to 1× for present
                .Depth(depthHandle, LoadOp.Load, StoreOp.Store)       // load the pre-pass depth
                .Shader(litInterface, skyInterface)
            : graph.GraphicsPass("lit-scene")
                .Target(hdrHandle, LoadOp.Clear, StoreOp.Store)
                .Depth(depthHandle, LoadOp.Load, StoreOp.Store)
                .Shader(litInterface, skyInterface);
        litPass = litPass.Read(incidentHandle);
        litPass = litPass.Read(incidentGradientHandle);
        litPassHandle = litPass.Handle;

        // One pass per parity. Only one is recorded each frame; the other's target is that frame's
        // history, and ReadHistory is what lets a pass declare a read of it before it is rewritten.
        var taaInterface = Reflect("present.vert", SurfaceTargets ? "taa_surface.frag" : "taa.frag");
        for (var i = 0; i < 2; i++)
        {
            var taaBuilder = graph.GraphicsPass($"taa-resolve{i}")
                .Target(taaHandles[i], LoadOp.Clear, StoreOp.Store)
                .Read(hdrHandle)
                .Read(SampleableSceneDepth)
                .ReadHistory(taaHandles[i ^ 1]);
            if (SurfaceTargets)
            {
                taaBuilder = taaBuilder
                    .Read(velocityHandle)
                    .Read(surfaceKeyHandle)
                    .ReadHistory(surfaceKeyHistoryHandle);
            }
            taaPassHandles[i] = taaBuilder.Shader(taaInterface).Handle;
        }
        if (SurfaceTargets)
        {
            // After every reader of last frame's keys (TAA): this frame's become the history.
            surfaceKeyHistoryPassHandle = graph.GraphicsPass("surface-key-history")
                .Target(surfaceKeyHistoryHandle, LoadOp.Clear, StoreOp.Store)
                .Read(surfaceKeyHandle)
                .Shader(Reflect("present.vert", "copy_key.frag"))
                .Handle;
        }
        DeclareMetalFx();
        graph.Compile();
        graphResourceGeneration = graph.MatchSwapchainResourceGeneration;

        // --- Shader programs + pipelines --------------------------------
        // shaderDir was resolved above (reflection sidecars live alongside the .spv).
        var litVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "lit.vert.spv"));
        var litFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "lit.frag.spv"));
        litProgram = Own(device.CreateShaderProgramFromSpv(litVertSpv, litFragSpv, litInterface, "lit"));
        // Opaque + Mask share the no-blend pipeline group. Depth is
        // LessEqual with writes disabled: the depth pre-pass already wrote the complete
        // scene depth, so the lit pass only shades the front-most fragment
        // (overdraw killed). Mask materials trigger the discard branch via the
        // per-material UBO's alphaCutoff > 0; opaque materials leave it at 0.
        // AlphaToCoverage on the opaque/mask pipelines: cutout foliage (the
        // reclassified blend) outputs a sharpened coverage alpha → antialiased
        // leaf edges under MSAA; solid opaque outputs coverage 1.0 → no effect.
        opaqueSolidPipeline = Pipeline(litProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.BackFaceCulling,
            new[] { BlendState.Disabled }, litPassHandle, "lit.opaque", alphaToCoverage: MsaaSamples > 1);
        opaqueSolidPipelineWrites = Pipeline(litProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualWrite, RasterizerState.BackFaceCulling,
            new[] { BlendState.Disabled }, litPassHandle, "lit.opaque.writes",
            alphaToCoverage: MsaaSamples > 1);
        opaqueDoubleSidedPipelineWrites = Pipeline(litProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualWrite, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, litPassHandle, "lit.opaque.doubleSided.writes",
            alphaToCoverage: MsaaSamples > 1);
        opaqueDoubleSidedPipeline = Pipeline(litProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, litPassHandle, "lit.opaque.doubleSided", alphaToCoverage: MsaaSamples > 1);
        // Opaque (no cutout) groups draw with these: the early depth test against the pre-pass's depth, so a covered
        // surface is not shaded. The pipelines above stay for cutout groups and the no-pre-pass and flat variants.
        litOpaqueProgram = Own(device.CreateShaderProgramFromSpv(litVertSpv,
            File.ReadAllBytes(Path.Combine(shaderDir, "lit_opaque.frag.spv")), litInterface, "lit.opaque.early"));
        opaqueSolidPipelineEarly = Pipeline(litOpaqueProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.BackFaceCulling,
            new[] { BlendState.Disabled }, litPassHandle, "lit.opaque.early");
        opaqueDoubleSidedPipelineEarly = Pipeline(litOpaqueProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, litPassHandle, "lit.opaque.doubleSided.early");
        litLeanProgram = Own(device.CreateShaderProgramFromSpv(litVertSpv,
            File.ReadAllBytes(Path.Combine(shaderDir, "lit_lean.frag.spv")), litInterface, "lit.lean"));
        opaqueSolidPipelineLean = Pipeline(litLeanProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.BackFaceCulling,
            new[] { BlendState.Disabled }, litPassHandle, "lit.lean");
        opaqueDoubleSidedPipelineLean = Pipeline(litLeanProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, litPassHandle, "lit.lean.doubleSided");

        // Blend: depth-test (so windows don't draw behind walls) but no
        // depth-write (so successive translucent fragments don't z-fight),
        // src-alpha blend. Back-to-front sort within the blend bucket is a
        // deferred polish.
        blendSolidPipeline = Pipeline(litProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.BackFaceCulling,
            new[] { BlendState.AlphaBlend }, litPassHandle, "lit.blend");
        blendDoubleSidedPipeline = Pipeline(litProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.NoCulling,
            new[] { BlendState.AlphaBlend }, litPassHandle, "lit.blend.doubleSided");

        // Sky pipeline. Depth-test LessEqual with NO write, so the sky only
        // draws where the depth buffer still holds the clear value (1.0)
        // and never overwrites opaque-geometry depth. Drawn between
        // opaque/mask and blend in OnRender so blend windows composite
        // over the sky.
        var skyVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "skybox.vert.spv"));
        var skyFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "skybox.frag.spv"));
        skyProgram = Own(device.CreateShaderProgramFromSpv(skyVertSpv, skyFragSpv, skyInterface, "skybox"));

        skyPipeline = Pipeline(skyProgram,
            VertexPosition3NormalTexture.Layout, // ignored — sky vert synthesises positions
            DepthState.LessEqualNoWrite, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, litPassHandle, "skybox");

        // Depth + normal pre-pass pipelines — lit.vert (shared → invariant depth) plus fragments
        // that write the interpolated world normal into the matching colour target. No
        // culling: solid geometry's nearest face still wins the depth test
        // (matching lit's back-cull front face), and double-sided geometry
        // always writes depth from either view side so the sky never overdraws
        // a back-facing curtain. LessEqualWrite; the lit pass then reads it.
        // Flat preview pipeline (streamed-load phase): lit.vert + flat.frag, lit
        // surface, depth-test no-write (the pre-pass wrote depth). Reuses
        // litInterface; flat.frag samples nothing, so set1/set2 stay unbound (as
        // with the pre-pass programs).
        var flatFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "flat.frag.spv"));
        flatProgram = Own(device.CreateShaderProgramFromSpv(litVertSpv, flatFragSpv, litInterface, "flat"));
        flatPipeline = Pipeline(flatProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, litPassHandle, "flat");
        // --lit-flat's single-sided twin: the flat fragment over exactly the raster work the lit pipeline does.
        flatSolidPipeline = Pipeline(flatProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.BackFaceCulling,
            new[] { BlendState.Disabled }, litPassHandle, "flat.solid");

        var prepassOpaqueFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "depth_prepass.frag.spv"));
        prepassOpaqueProgram = Own(device.CreateShaderProgramFromSpv(litVertSpv, prepassOpaqueFragSpv, litInterface, "depth_prepass"));
        prepassOpaquePipeline = Pipeline(prepassOpaqueProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualWrite, RasterizerState.NoCulling,
            PrepassBlends(), depthPrepassHandle, "depth_prepass");

        var prepassMaskFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "depth_prepass_mask.frag.spv"));
        prepassMaskProgram = Own(device.CreateShaderProgramFromSpv(litVertSpv, prepassMaskFragSpv, litInterface, "depth_prepass_mask"));
        prepassMaskPipeline = Pipeline(prepassMaskProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualWrite, RasterizerState.NoCulling,
            PrepassBlends(), depthPrepassHandle, "depth_prepass_mask");

        var presentVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.vert.spv"));
        var presentFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.frag.spv"));
        presentProgram = Own(device.CreateShaderProgramFromSpv(presentVertSpv, presentFragSpv, presentInterface, "present"));
        presentPipeline = Own(device.CreatePipeline(new PipelineDescription(
            presentProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.Disabled,
            RasterizerState.NoCulling,
            BlendState.Disabled), "present"));

        // Graph-pass pipelines must be created against the pass surface. Swapchain-present
        // pipelines have no graph render target and are not compatible here.
        var hiZSpv = File.ReadAllBytes(Path.Combine(shaderDir, "hiz_build.frag.spv"));
        hiZProgram = Own(device.CreateShaderProgramFromSpv(presentVertSpv, hiZSpv, hiZInterface, "hiz_build"));
        for (var level = 0; level < HiZLevels; level++)
        {
            occZPipelines[level] = Pipeline(hiZProgram, VertexPosition3NormalTexture.Layout,
                DepthState.Disabled, RasterizerState.NoCulling,
                new[] { BlendState.Disabled }, occZPassHandles[level], $"occz{level}");
        }

        // One program, two pipelines — each bound to its own pass surface, because a pipeline is
        // compatible with the render pass it was built against and the two resolve passes target
        // different images.
        var taaSpv = File.ReadAllBytes(Path.Combine(shaderDir, SurfaceTargets ? "taa_surface.frag.spv" : "taa.frag.spv"));
        var taaProgram = Own(device.CreateShaderProgramFromSpv(presentVertSpv, taaSpv, taaInterface, "taa"));
        for (var i = 0; i < 2; i++)
        {
            taaPipelines[i] = Pipeline(taaProgram, VertexPosition3NormalTexture.Layout,
                DepthState.Disabled, RasterizerState.NoCulling,
                new[] { BlendState.Disabled },   // one target: the count rides in its alpha
                taaPassHandles[i], $"taa{i}");
        }
        if (SurfaceTargets)
        {
            var copyKeyInterface = Reflect("present.vert", "copy_key.frag");
            surfaceKeyHistoryPipeline = Pipeline(Own(device.CreateShaderProgramFromSpv(presentVertSpv,
                    File.ReadAllBytes(Path.Combine(shaderDir, "copy_key.frag.spv")), copyKeyInterface, "copy_key")),
                VertexPosition3NormalTexture.Layout, DepthState.Disabled, RasterizerState.NoCulling,
                new[] { BlendState.Disabled }, surfaceKeyHistoryPassHandle, "copy_key");
        }

        CreateRtgiPipelines(shaderDir, presentVertSpv);
        CreateRtgiTemporalPipelines(shaderDir, presentVertSpv);
        lightingUpsamplePipeline = Pipeline(Own(device.CreateShaderProgramFromSpv(
                presentVertSpv, File.ReadAllBytes(Path.Combine(shaderDir, "lighting_upsample.frag.spv")), lightingUpsampleInterface, "lighting_upsample")),
            VertexPosition3NormalTexture.Layout, DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled, BlendState.Disabled }, lightingUpsamplePassHandle, "lighting_upsample");


        // Fullscreen triangle for the sky + present passes (positions synthesised
        // from gl_VertexIndex in the vertex shader — the buffer is never sampled).
        fullscreen = new FullscreenPass(device, "present.dummy");

        moverPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "mover.comp.spv")), moverInterface!, "mover")), "mover"));
        moverRaysPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "mover_rays.comp.spv")), moverRaysInterface!, "mover_rays")), "mover_rays"));
        var cullSpv = File.ReadAllBytes(Path.Combine(shaderDir, "scene_cull.comp.spv"));
        var cullProgram = Own(device.CreateComputeShaderProgramFromSpv(cullSpv, cullInterface, "scene_cull"));
        cullPipeline = Own(device.CreateComputePipeline(cullProgram, "scene_cull"));
        rtShadowRaysPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "rt_shadow_rays.comp.spv")), rtShadowRaysInterface, "rt_shadow_rays")), "rt_shadow_rays"));
        rtShadowFilterPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "rt_shadow_filter.comp.spv")), rtShadowFilterInterface, "rt_shadow_filter")), "rt_shadow_filter"));
        rtShadowFillPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "rt_shadow_fill.comp.spv")), rtShadowFillInterface, "rt_shadow_fill")), "rt_shadow_fill"));
        CreateRayBatchPipelines(shaderDir);
        referenceWavePipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "reference_wave.comp.spv")), referenceWaveInterface, "reference_wave")), "reference_wave"));
        var bakeSpv = File.ReadAllBytes(Path.Combine(shaderDir, "ray_surface_bake.comp.spv"));
        raySurfaceBakePipeline = Own(device.CreateComputePipeline(
            Own(device.CreateComputeShaderProgramFromSpv(bakeSpv, raySurfaceBakeInterface, "ray_surface_bake")), "ray_surface_bake"));
        var occlusionSpv = File.ReadAllBytes(Path.Combine(shaderDir, "scene_occlusion.comp.spv"));
        var occlusionProgram = Own(device.CreateComputeShaderProgramFromSpv(occlusionSpv, occlusionInterface, "scene_occlusion"));
        occlusionPipeline = Own(device.CreateComputePipeline(occlusionProgram, "scene_occlusion"));

        // Build the per-frame-constant buffers after graph compilation and texture creation. Reuse
        // them every frame in OnRender.
        passBindings = new[]
        {
            new ShaderTextureBinding("uIrradiance",     irradianceCubeTexture),
            new ShaderTextureBinding("uPrefilteredEnv", envCubeTexture),
            new ShaderTextureBinding("uBrdfLut",        brdfLutTexture),
            // Bound to SOMETHING valid always — a descriptor set with a hole is a device loss, not a
            // dark curtain. uSheenMipCount being zero is what tells the shader not to read them.
            new ShaderTextureBinding("uSheenEnv", sheenMipCount > 0 ? sheenEnvTexture : envCubeTexture),
            new ShaderTextureBinding("uSheenLut", sheenMipCount > 0 ? sheenLutTexture : brdfLutTexture),
            new ShaderTextureBinding("uEnvCube", skyCubeTexture),
            new ShaderTextureBinding(
                "uIncidentField", graph.GetColorTexture(incidentHandle)),
            new ShaderTextureBinding(
                "uIncidentGradient", graph.GetColorTexture(incidentGradientHandle)),
            new ShaderTextureBinding(
                "uPrepassNormalViz", graph.GetColorTexture(SampleablePrepassNormal)),
        };

        // skybox.frag's one, taken from the lit list so the two can never hold different textures.
        skyBindings = new[] { passBindings.Single(b => b.Name == "uEnvCube") };

        // --- Load the scene's geometry ------------------------------------
        // Static meshes (neither scene skins): each cooked primitive
        // becomes one mesh with a baked-in node transform; its material is
        // resolved + cached by GetMaterial. Parsing is pure CPU (~7s — the bulk
        // of the load), so the AsyncLoadQueue runs it off-thread; the GPU work
        // (StageDrawable + ConsolidateBuffers) drains on the main thread via
        // TryFinishLoad. Until then sceneLoaded is false and OnRender shows a
        // responsive clear (loading screen). The packs are the scene profile's (TryLocateScene);
        // Sponza's candles are not among them (no light source).
        meshLoad.Start(() =>
        {
            var prims = new List<PlacedPrimitive>();
            foreach (var (name, primitives) in ParsePacksParallel(packsToParse))
            {
                prims.AddRange(primitives);
                Console.WriteLine(
                    $"[VulkanSponza] {name} pack: {primitives.Length} unique primitives, "
                    + $"{primitives.Sum(p => p.Worlds.Length)} placements.");
            }
            return prims;
        });

        UpdateCamera();
    }

    // Resolve the scene's cooked tree from its profile's variable (BLIX_SPONZA_ASSETS, BLIX_BISTRO_ASSETS):
    // the tree a setup script prepares. There is no bin-local fallback — a default nothing can populate only
    // ever reports the wrong missing path. Closes the window cleanly when a required pack is missing and
    // returns false so OnLoad bails.
    private bool TryLocateScene(out string assetsRoot, out List<(string Name, string Path)> packs)
    {
        packs = new List<(string Name, string Path)>();
        assetsRoot = Environment.GetEnvironmentVariable(scene.AssetsVariable) ?? "";
        if (assetsRoot.Length == 0)
        {
            Console.WriteLine($"[VulkanSponza] {scene.AssetsVariable} is not set. It names {scene.Name}'s cooked tree;");
            Console.WriteLine($"[VulkanSponza] {scene.SetupHint}.");
            host.RequestClose();
            return false;
        }

        foreach (var pack in scene.Packs)
        {
            // --no-foliage excludes cutout geometry from every pass. A uniform A/B cannot price discard,
            // overdraw, pre-pass work and the traced shadows' alpha tests together; the pack-level arm can.
            if (pack.Foliage && noFoliage) continue;
            var packDir = Path.Combine(assetsRoot, pack.Directory);
            var found = Directory.Exists(packDir) ? FirstAsset(packDir) : null;
            if (found is not null)
            {
                packs.Add((pack.Directory, found));
                continue;
            }

            if (!pack.Required) continue;
            Console.WriteLine($"[VulkanSponza] No cooked .blixmesh in {packDir}.");
            Console.WriteLine($"[VulkanSponza] Point {scene.AssetsVariable} at a cooked tree; {scene.SetupHint}.");
            host.RequestClose();
            return false;
        }

        return true;
    }

    // IBL prefers a cooked .blixprobe, then any probe in the scene's probe directory, and finally the
    // procedural sky. The scene profile's ProbeCandidates define the named order. Detected probe suns align
    // direct light automatically unless --sun-authored opts out.
    private void LoadIbl(string assetsRoot)
    {
        // --probe places an explicit asset ahead of the scene's preference list without requiring a
        // rebuild.
        string[] probeCandidates = probeName is { Length: > 0 }
            ? new[] { probeName.EndsWith(".blixprobe", StringComparison.Ordinal) ? probeName : probeName + ".blixprobe" }
                .Concat(scene.ProbeCandidates).ToArray()
            : scene.ProbeCandidates;
        var probeDir = Path.Combine(assetsRoot, scene.ProbeDirectory);
        var probePath = probeCandidates
            .Select(p => Path.Combine(probeDir, p))
            .FirstOrDefault(File.Exists)
            // Named preferences win; otherwise use the first cooked probe rather than falling back
            // to procedural lighting while a valid asset is available.
            ?? (Directory.Exists(probeDir)
                ? Directory.EnumerateFiles(probeDir, "*.blixprobe").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault()
                : null);
        if (probePath is null)
        {
            Console.WriteLine("[VulkanSponza] IBL: no cooked probe (run tools/setup-sponza-modern.sh / blix-cook probe); using procedural sky.");
            BakeProceduralIbl();
            return;
        }
        try
        {
            var probeData = BlixProbeReader.Read(probePath);
            var baked = EnvironmentBaker.UploadCookedProbe(device, probeData, "sponza.ibl");
            envCubeTexture = baked.Probe.PrefilteredSpecular;
            skyCubeTexture = baked.Probe.EnvCubemap;
            // Null when the probe is older than v4. Falling back to the specular cube would render
            // a dimmer, rimless something that looks like weak sheen rather than like absent sheen.
            if (baked.SheenPrefiltered is { } sheenCube && baked.SheenLut is { } sheenLut)
            {
                sheenEnvTexture = sheenCube;
                sheenLutTexture = sheenLut;
                sheenMipCount = baked.SheenMipCount;
                Console.WriteLine($"[VulkanSponza]   sheen: Charlie-prefiltered env, {baked.SheenMipCount} mips + albedo LUT.");
            }
            irradianceCubeTexture = baked.Probe.DiffuseIrradiance;
            brdfLutTexture = baked.BrdfLut;
            iblPrefilterMips = baked.Probe.PrefilteredSpecularMipCount;
            Console.WriteLine($"[VulkanSponza] IBL: cooked probe {Path.GetFileName(probePath)} ({iblPrefilterMips} GGX prefilter mips).");
            // Align the directional sun (key light + shadow caster) to the probe's
            // detected sun so cast shadows match the visible sky sun. FROM-sun-
            // into-scene convention, matching sunDirection.
            if (sunOverhead)
            {
                sunDirection = new Vector3(0f, -1f, 0f);
                sunPitch = -MathF.PI / 2f;
                sunYaw = 0f;
                Console.WriteLine("[VulkanSponza]   sun forced overhead (--sun-overhead).");
            }
            else if (alignSunToProbe && baked.Probe.SunDirectionFromEquirect is { } hdrSun)
            {
                sunDirection = Vector3.Normalize(hdrSun);
                sunPitch = MathF.Asin(Math.Clamp(sunDirection.Y, -1f, 1f));
                sunYaw = MathF.Atan2(sunDirection.X, -sunDirection.Z);
                Console.WriteLine(
                    $"[VulkanSponza]   sun from the sky: {sunDirection} " +
                    $"(elevation {MathF.Asin(Math.Clamp(sunDirection.Y, -1f, 1f)) * 180f / MathF.PI:0.0} deg, " +
                    $"yaw {sunYaw * 180f / MathF.PI:0.0} deg)");
            }

            // Use the probe's measured sun irradiance. The bake removes that disc from IBL so the
            // directional sun and remaining sky contribute once in the same units.
            if (baked.Probe.SunIrradiance is { } measured)
            {
                sunIrradiance = measured;
                Console.WriteLine($"[VulkanSponza]   sun irradiance measured: {measured}");
            }
            else
            {
                Console.WriteLine(
                    "[VulkanSponza]   probe carries no measured sun — re-cook it; using the fallback irradiance.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VulkanSponza] probe load failed ({ex.Message}); using procedural IBL.");
            BakeProceduralIbl();
        }
    }

    /// <summary>Returns the pack's cooked mesh, or null when the pack has not been cooked.</summary>
    /// <remarks>
    /// A cooked pack is self-contained through <c>.blixmesh</c> material/image metadata and sibling
    /// <c>.blixtex</c> files. The engine reads cooked models only, so a pack of sources is not one.
    /// </remarks>
    private static string? FirstAsset(string packDir) =>
        Directory.EnumerateFiles(packDir, "*.blixmesh", SearchOption.TopDirectoryOnly).FirstOrDefault();

    // Procedural-sky IBL fallback (no cooked .blixprobe). The synthesis lives in
    // the demo-owned ProceduralSky helper; this just binds the result.
    private void BakeProceduralIbl()
    {
        var sky = ProceduralSky.Bake(device, SkyBakeSunDirection);
        envCubeTexture = sky.EnvCube;
        skyCubeTexture = sky.EnvCube;
        irradianceCubeTexture = sky.Irradiance;
        brdfLutTexture = sky.BrdfLut;
        iblPrefilterMips = sky.PrefilterMips;
    }

    // The Frame block, as every program that declares it sees it. `authority` is the full
    // declaration (lit's, which names everything); each other program names a subset, and any name
    // they share has to sit at the same offset and be the same size. A disagreement is a silent
    // mis-read of live data, so it stops the boot rather than shading one pass with another pass's
    // uniforms.
    private static void AssertFrameBlockAgrees(
        ShaderInterface authority, params (string Name, ShaderInterface Iface)[] others)
    {
        UniformBlockLayout? Frame(ShaderInterface i) =>
            i.Slots.FirstOrDefault(sl => sl is { Set: 0, Binding: 0 })?.BlockLayout;

        var full = Frame(authority)
            ?? throw new InvalidOperationException(
                "VulkanSponza: the lit program does not declare the Frame block at set 0 binding 0.");

        foreach (var (name, iface) in others)
        {
            var block = Frame(iface);
            if (block is null) continue;   // a program that never reads the Frame block is fine
            foreach (var member in block.Members)
            {
                var reference = full.Members.FirstOrDefault(m => m.Name == member.Name);
                if (reference is null)
                {
                    throw new InvalidOperationException(
                        $"VulkanSponza: {name} declares Frame member '{member.Name}', which lit.frag " +
                        "does not — one of the two names is wrong, and std140 will not say which.");
                }
                if (reference.Offset != member.Offset || reference.Size != member.Size)
                {
                    throw new InvalidOperationException(
                        $"VulkanSponza: {name}'s Frame.{member.Name} is at offset {member.Offset} " +
                        $"size {member.Size}; lit.frag has it at offset {reference.Offset} size " +
                        $"{reference.Size}. The two read the same bytes as different members.");
                }
            }
        }
    }

    // The pre-pass's targets: the normal, and under single sampling the surface key, the velocity and the world motion
    // (stage 4e).
    // One per pre-pass colour target: normal; surface key and velocity; world motion (a target the pipelines once did
    // not count had nothing written to it).
    private BlendState[] PrepassBlends() => Enumerable.Repeat(BlendState.Disabled, !SurfaceTargets ? 1 : 3 + (MotionTarget ? 1 : 0)).ToArray();
}
