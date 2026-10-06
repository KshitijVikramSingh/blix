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
        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        renderHeightPx = host.LogicalSize.Height;

        // Fog is part of the standard composition; --no-fog retains the unscattered reference path.
        fog.Enabled = !args.Flag("no-fog");
        if (args.Flag("fog")) fog.Enabled = true;
        // --fog-stress flips fog on/off every ~90 frames so a validation run
        // exercises the compute storage-image layout transitions across the
        // disabled↔enabled boundary (the highest-risk sync path).
        if (args.Flag("fog-stress")) { fogStress = true; fog.Enabled = true; }
        // --no-ao: keep both ambient passes in the graph but give the search a zero radius, so a
        // paired run attributes the HORIZON SEARCH specifically rather than the whole feature.
        if (args.Flag("no-ao")) ambient.Enabled = false;
        if (args.Flag("no-shadow")) shadows.Enabled = false;
        if (args.Flag("ao-fullres")) aoScale = 1f;
        // The incident-light field ships on; --no-incident selects the inline reference path.
        if (args.Flag("no-incident")) incidentField = false;
        if (args.Flag("incident")) incidentField = true;
        if (args.Flag("incident-full")) { incidentField = true; incidentScale = 1f; }
        // The resolution knob itself, because "half" is a guess and the error it costs is a
        // function of how far the coarse texel centre sits from the fine pixel it answers for.
        if (args.Float("incident-scale") is { } isc)
        {
            incidentField = true;
            incidentScale = Math.Clamp(isc, 0.25f, 1f);
        }
        if (args.Flag("no-prepass")) noPrepass = true;
        if (args.String("probe") is { } probe) probeName = probe;
        // The census has to be able to ask about the FIELD rather than about the sleep policy:
        // with sleeping on, a probe the camera never looked at is zero, and a census of a still
        // camera's frame is then mostly a count of what the camera did not face.
        if (args.Float("probe-sleep") is { } ps) probeSleepFrames = MathF.Max(0f, ps);
        // Zero isolates sky-fed transport from the direct-sun source for probe censuses.
        if (args.Float("sun-strength") is { } ss) sunStrength = MathF.Max(0f, ss);
        if (args.Int("ref-bounces") is { } rb) refBounces = Math.Clamp(rb, 1, 8);
        if (args.Int("ref-probes") is { } rp) referenceProbes = Math.Clamp(rp, 1, 1024);
        if (args.String("shadow-maps") is { } shadowMaps)
        {
            var sm = shadowMaps.Split(',');
            if (sm.Length != 3 || !sm.All(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            {
                throw new AppArgsException($"--shadow-maps expects three sizes like 2048,1024,1024, got '{shadowMaps}'.");
            }

            ShadowMapSizes = sm.Select(v => Math.Clamp(int.Parse(v, CultureInfo.InvariantCulture), 256, 4096)).ToArray();
        }
        if (args.Float("foliage-lod") is { } fl) FoliageLodMargin = MathF.Max(0.1f, fl);
        if (args.Float("shadow-lod") is { } sl) shadowLodTexels = MathF.Max(0.1f, sl);
        if (args.Float("bounce-div") is { } bd) bounceDiv = Math.Clamp(bd, 0.5f, 8f);
        // The same axis stated the way it is usually wanted: a multiplier on probe COUNT.
        // --bounce-x2 is --bounce-div 1.587 without anybody having to know that.
        if (args.Float("bounce-x") is { } bx && bx > 0f) bounceDiv = 2f / MathF.Cbrt(bx);
        if (args.Float("inject-feedback") is { } ifb) injectFeedback = Math.Clamp(ifb, 0f, 4f);
        if (args.Float("transport-occlusion") is { } to) transportOcclusion = Math.Clamp(to, 0f, 1f);
        if (args.Flag("sky-no-inject")) skipInject = true;
        if (args.Flag("sky-no-sample")) skipSkySample = true;
        // Standard rendering uses baked enclosure and dynamic bounce. --no-sky disables both;
        // --sky remains a compatibility no-op for existing invocations.
        args.Flag("sky");
        skyVisibilityEnabled = !args.Flag("no-sky");
        if (skyVisibilityEnabled)
        {
            // Exposure accounts for the display level of the physically attenuated composition;
            // transport strength remains one so material albedo is not silently reinterpreted.
            render.Exposure = 1.0f;
        }
        if (args.Flag("no-mask")) forceOpaqueMask = true;
        // The A/B for the single-sample canopy: hashed stochastic cutout against the plain binary
        // one. Re-cooks nothing and rebuilds nothing — it changes one number in the material.
        if (args.Flag("no-hashed-alpha")) hashedAlpha = false;
        if (args.Flag("msaa1")) MsaaSamples = 1;
        if (args.Flag("msaa2")) MsaaSamples = 2;
        // Present so the sample count can be swept from the command line in BOTH directions. Without
        // it only the non-default could be asked for, so a paired run could not be ordered 4-2-2-4 —
        // and on this machine a single ordering is not a measurement.
        if (args.Flag("msaa4")) MsaaSamples = 4;
        // Align shadows and direct light to a detected environment sun by default. --sun-authored
        // opts out; probes without a detectable sun naturally retain the authored direction.
        if (!args.Flag("sun-authored")) alignSunToProbe = true;
        // --sun-overhead maximizes directly lit courtyard area when isolating base lighting. The
        // cascade fit handles the vertical-light up-vector degeneracy.
        if (args.Flag("sun-overhead")) sunOverhead = true;
        // Expose the probe view to automated/headless runs as well as the overlay checkbox.
        if (args.Flag("show-probes")) showProbes = true;
        if (args.Flag("probe-carryless")) probeCarryless = true;
        // GPU isolation submits and waits per pass. It attributes real tile execution but removes
        // overlap, so isolated pass times are not additive components of the normal frame.
        if (args.Flag("gpu-isolate")) host.Timing.IsolatePasses = true;
        if (args.Flag("no-caster-cull")) shadowCasterCull = false;
        if (args.Flag("no-foliage")) noFoliage = true;
        // --flatten: bake every placement into its own primitive, placed once at identity: the pre-
        // instancing shape through the same draw path, so an A/B isolates instancing alone.
        flatten = args.Flag("flatten");
        // --cpu-cull: build the indirect records and visible lists on the CPU, the way 1a did, for an A/B
        // against the GPU cull (scene_cull.comp) that is otherwise the default.
        gpuCull = !args.Flag("cpu-cull");
        // --lit-flat: the lit pass shades opaque geometry with flat.frag, everything else unchanged (same lit.vert,
        // culling, pre-pass and LOD). What a cost does under it is the part that is not material shading.
        litFlat = args.Flag("lit-flat");
        // --ray-scene: build CPU ray-query hierarchies over the scene at load and time camera rays (SponzaLoop.RayScene).
        rayScene = args.Flag("ray-scene");
        // --ray-check: also trace a fixed batch of rays on the GPU every frame and hold them against the CPU at the shot.
        rayCheck = args.Flag("ray-check");
        if (args.String("ray-bench") is { } bench)
        {
            if (bench is not ("mixed" or "probe" or "camera")) throw new AppArgsException($"--ray-bench takes mixed, probe or camera, not '{bench}'.");
            rayBench = bench;
            rayCheck = true;
        }
        // --ray-view: trace the camera's view and hold it against the raster at the shot (it brings the ray check with it).
        rayView = args.Flag("ray-view");
        rayCheck |= rayView;
        // The probe injection traces the scene's triangles by default: against a path-traced reference on the
        // triangles its field's per-probe error is 11x smaller than the occupancy march's on Sponza (which it
        // under-lit by about a quarter) and 2x on Bistro, and it is cheaper. --gi-march keeps the march for the A/B.
        giTrace = !args.Flag("gi-march");
        // --gi-clipmap: solve the camera-relative probe clipmap as well (stage 4c, SponzaLoop.Clipmap).
        clipmapEnabled = args.Flag("gi-clipmap");
        if (args.Float("clipmap-spacing") is { } clipSpacing) clipmapSpacing = Math.Max(0.05f, clipSpacing);
        if (args.Int("clipmap-budget") is { } clipBudget) clipmapBudget = Math.Clamp(clipBudget, 1, 65535);
        if (args.Float("clipmap-unknown-sky") is { } unknownSky) clipmapUnknownSky = Math.Clamp(unknownSky, 0f, 1f);
        // --gi-screen-probes: the per-tile gather over the clipmap (SponzaLoop.ScreenProbes); it needs the clipmap.
        screenProbesEnabled = args.Flag("gi-screen-probes");
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
        taaLinearBlend = args.Flag("taa-linear");
        // --taa-history11: the resolved history back in R11G11B10F (the control for history that stops converging
        // once a frame's step falls below half the format's rounding step); Rgba16F is the default.
        if (args.Flag("taa-history11")) taaHistory16 = false;
        if (args.Float("taa-relax") is { } relax) taaRelax = Math.Max(1f, relax);
        if (args.Float("taa-accumulate") is { } accumulate) taaAccumulate = Math.Max(0f, accumulate);
        // --stability K: per-pixel temporal variation over the K still frames before the shot (SponzaLoop.Stability).
        if (args.Int("stability") is { } stability) stabilityFrames = Math.Max(2, stability);
        noIncidentGradient = args.Flag("no-incident-gradient");
        if (args.Float("incident-normal-bias") is { } nb) incidentNormalBias = Math.Clamp(nb, 0f, 12f);
        if (args.Float("incident-gradient-clamp") is { } gc) incidentGradientClamp = Math.Clamp(gc, 0f, 4f);
        if (args.Int("screen-probe-reset-at") is { } resetAt) screenProbeResetAt = resetAt;
        if (args.Int("screen-probe-seed-offset") is { } seedOffset) screenProbeSeedOffset = seedOffset;
        if (args.Float("screen-probe-seed") is { } seed) screenProbeSeedFrames = Math.Max(0f, seed);
        if (args.Flag("no-young-filter")) screenProbeYoungWide = false;
        if (args.Int("clipmap-freeze") is { } freeze) clipmapFreeze = Math.Max(1, freeze);
        if (args.Int("screen-probe-filter") is { } spFilter) screenProbeFilterRadius = Math.Clamp(spFilter, 0, 4);
        if (args.Int("screen-probe-ablate") is { } ablate) screenProbeAblate = ablate;
        if (args.Int("orbit-frames") is { } orbitFrames) OrbitFrames = Math.Max(2, orbitFrames);
        if (screenProbesEnabled && !clipmapEnabled) throw new AppArgsException("--gi-screen-probes needs --gi-clipmap: its rays read the clipmap where they hit.");
        if (args.Float("screen-probe-history") is { } spHistory) screenProbeHistory = Math.Max(1f, spHistory);
        // The triangle probe reference (--probe-reference) traces the ray scene too. GI's own need for it waits
        // for the scene to say whether it has a probe field at all (ConsolidateBuffers).
        rayScene |= rayCheck || abMode == "trace" || args.Flag("probe-reference") || clipmapEnabled;
        if (args.Int("ray-region-triangles") is { } regionTriangles) rayRegionTriangles = Math.Max(1, regionTriangles);
        if (args.Float("ray-lod-error") is { } lodError) rayLodError = Math.Max(0f, lodError);
        if (args.Values("ray-probe", 3) is [var probeRay, var probeInstance, var probeTriangle])
        {
            rayCheckProbe = (int.Parse(probeRay, CultureInfo.InvariantCulture), int.Parse(probeInstance, CultureInfo.InvariantCulture),
                int.Parse(probeTriangle, CultureInfo.InvariantCulture));
        }
        // --no-occlusion: the GPU cull without its two-phase occlusion test (scene_occlusion.comp), for the A/B.
        occlusionCull = gpuCull && !args.Flag("no-occlusion");
        // --occlusion-cut: every frame is a camera cut. The early list draws nothing, so the late list carries
        // the whole frame: the case history cannot help, and the proof that the late list is complete.
        occlusionCut = args.Flag("occlusion-cut");
        if (args.Flag("probe-reference")) probeReference = true;
        if (args.Flag("no-sky-bounce")) noSkyBounce = true;
        if (args.Int("fog-slices") is { } fs) froxelGridZ = Math.Clamp(fs, 8, 128);
        // --orbit: drive the camera on a fixed path so a measurement is of the renderer rather than
        // of one photograph of it. Ignores --cam, which is the still counterpart.
        if (args.Flag("orbit")) orbit = true;
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
        if (args.Float("ao-debug") is { } aoDebugValue) aoDebug = aoDebugValue;
        if (args.Float("viz") is { } vizValue) vizChannel = vizValue;
        if (args.Float("ao-radius") is { } aoRadius) ambient.RadiusMetres = aoRadius;
        // --lod-arms <onPx> <offPx>: the two budgets --ab lod alternates between.
        if (args.Values("lod-arms", 2) is [var lodOn, var lodOff])
        {
            lodArmOn = float.Parse(lodOn, CultureInfo.InvariantCulture);
            lodArmOff = float.Parse(lodOff, CultureInfo.InvariantCulture);
        }
        // Timing runs require --no-vsync; FIFO quantizes frame periods to refresh intervals and can
        // reverse small A/B differences.
        if (args.Flag("no-vsync")) device.VsyncEnabled = false;
        // --shot <path>: render --shot-frames frames, write the ambient-visibility buffer and the
        // tonemapped scene beside it, and close. Headless in the sense that matters — nobody has
        // to be watching.
        if (args.String("shot") is { } shot) shotPath = shot;
        if (args.Int("shot-frames") is { } sf) shotFrame = sf;
        framesAfterLoad = args.Int("frames-after-load");

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
        LoadSkyVisibility(assetsRoot);

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
            taaHandles[i] = graph.ColorTarget($"taa{i}", taaHistory16 ? TextureFormat.Rgba16F : TextureFormat.R11G11B10F, fullSize);
        // MSAA colour + depth the lit pass renders into; resolves to hdr.
        hdrMsaaHandle = graph.ColorTarget("hdr-msaa", TextureFormat.R11G11B10F, fullSize, samples: MsaaSamples);
        depthHandle = graph.DepthTarget("scene-depth", fullSize, samples: MsaaSamples);

        // One depth target per cascade, sized per ShadowMapSizes (no 2D-array
        // creation API yet; the lit pass binds the three as a Count=3 sampler
        // array — mixed sizes are fine, each has its own view).
        for (var c = 0; c < CascadeCount; c++)
        {
            cascadeHandles[c] = graph.DepthTarget($"sun-cascade{c}",
                new FixedGraphSize(ShadowMapSizes[c], ShadowMapSizes[c]));
        }

        // The per-frame Frame UBO (view-proj, sun, camera, cascades, fog) and
        // its //@tune-decorated shader tunables are declared in lit.frag; their
        // std140 offsets are reflected from the SPIR-V, not hand-authored here.
        // Shader binding interfaces — descriptor sets, std140 UBO layouts, and
        // push-constant ranges — are reflected from the compiled SPIR-V at
        // build time (spirv-cross sidecars next to each .spv), not hand-
        // authored. See docs/architecture.md → the Vulkan binding model. Each program
        // reflects exactly what its stages declare, and a draw binds by name only
        // what its program declares: naming a texture the program lacks throws.
        // So the skybox has its own list of the six textures it samples rather
        // than being handed the lit pass's.
        var shaderDir = AppFiles.Shaders;
        ShaderInterface Reflect(params string[] stages) =>
            ShaderReflection.ForProgram(shaderDir, stages);

        var litInterface = Reflect("lit.vert", "lit.frag");
        var skyInterface = Reflect("skybox.vert", "skybox.frag");
        var probeInterface = Reflect("probe_debug.vert", "probe_debug.frag");
        var presentInterface = Reflect("present.vert", "present.frag");
        // Reuses present.vert: both are fullscreen triangles synthesised from gl_VertexIndex.
        var gtaoInterface = Reflect("present.vert", "gtao.frag");
        var gtaoDenoiseInterface = Reflect("present.vert", "gtao_denoise.frag");
        var hiZInterface = Reflect("present.vert", "hiz_build.frag");
        var incidentInterface = Reflect("present.vert", "incident.frag");
        incidentClipmapInterface = Reflect("present.vert", "incident_clipmap.frag");
        var incidentResolveInterface = Reflect("present.vert", "incident_resolve.frag");
        var shadowOpaqueInterface = Reflect("shadow.vert", "shadow.frag");
        var shadowMaskInterface = Reflect("shadow_mask.vert", "shadow_mask.frag");

        // Programs sharing the frame buffer must agree on reflected std140 offsets across program
        // boundaries; stage merging validates only one program at a time. Probe debug is excluded
        // because its set-0 block is a separate buffer on a separate pipeline.
        AssertFrameBlockAgrees(litInterface, ("skybox", skyInterface));

        // Scan lit.frag's //@tune decorators (shipped alongside the .spv) and
        // build the overlay's shader-variable panel. The panel owns the live
        // values + the dials; the per-frame write and the froxel sun term pull
        // from it by name.
        // Read from the sidecar the shader compiler wrote beside lit.frag.spv, rather than
        // rescanning GLSL here. The build has the expanded source with every include resolved;
        // this process has neither, which is why the decorators in frame.glsl were invisible to
        // a load-time scan and why the application used to ship its shader sources at all.
        tunePanel = new ShaderTunablePanel(
            ShaderTunableSidecar.Load(Path.Combine(shaderDir, "lit.frag.spv")));
        tuneObjects = new ObjectTunables(fog, shadows, render, ambient, camera);

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
        // that draws from them (the cascades), so it is declared ahead of all of them. Its dispatches
        // are fenced on both sides because they bind GPU buffers; the graph tracks images only.
        cullInterface = Reflect("scene_cull.comp");
        cullPassHandle = graph.ComputePass("scene-cull").Shader(cullInterface).Handle;
        raySurfaceBakeInterface = Reflect("ray_surface_bake.comp");
        raySurfaceBakePassHandle = graph.ComputePass("ray-surface-bake").Shader(raySurfaceBakeInterface).Handle;
        clipmapInterface = Reflect("clipmap_inject.comp");
        clipmapPassHandle = graph.ComputePass("probe-clipmap").Shader(clipmapInterface).Handle;
        rayCheckInterface = Reflect("ray_check.comp");
        rayCheckPassHandle = graph.ComputePass("ray-check").Shader(rayCheckInterface).Handle;

        // One graphics pass per cascade, each writing its own depth target.
        // Both shadow programs are render-pass-compatible with these passes.
        for (var c = 0; c < CascadeCount; c++)
        {
            cascadePassHandles[c] = graph.GraphicsPass($"sun-cascade{c}")
                .Depth(cascadeHandles[c], LoadOp.Clear, StoreOp.Store)
                .Shader(shadowOpaqueInterface, shadowMaskInterface)
                .Handle;
        }

        // Froxel fog compute pass — fills the 3D scattering grid. Declared
        // between the cascade passes and the lit pass so it runs after the
        // shadow maps are rendered (it samples them) and before the lit pass
        // composites its result. Reflected interface: UBO (set 0 binding 0),
        // the storage grid (binding 1), the cascade shadow maps (binding 2).
        var injectInterface = Reflect("sky_inject.comp");
        injectPassHandle = graph.ComputePass("sky-inject").Shader(injectInterface).Handle;

        // Samples the previous frame's resolved depth: declaration order puts this before the
        // current frame's depth pre-pass. Its marks are consumed by the NEXT frame's injection.
        usageInterface = Reflect("probe_usage.comp");
        probeUsagePassHandle = graph.ComputePass("probe-usage").Shader(usageInterface).Handle;

        var froxelInterface = Reflect("froxel.comp");
        var froxelPass = graph.ComputePass("froxel-fog").Shader(froxelInterface);
        for (var c = 0; c < CascadeCount; c++)
        {
            froxelPass = froxelPass.Read(cascadeHandles[c]);
        }
        froxelPassHandle = froxelPass.Handle;

        // Depth pre-pass — declared before lit; clears + writes the (4× MSAA)
        // scene depth for all non-blend geometry. Both pre-pass programs reuse
        // litInterface (lit.vert needs set 0 + the model push; the trivial
        // fragments use a subset), so the lit material descriptor set binds to
        // the mask variant unchanged.
        // 1x depth for GTAO to sample, and the ambient-visibility target it writes.
        // Rgba16F because .xyz is a bent normal — a direction needs signed components, and an
        // 8-bit one quantises the IBL lookup into visible facets on a smooth curved surface.
        // Only under MSAA: at one sample the depth target is already what a reader wants.
        if (MsaaSamples > 1) depthResolveHandle = graph.DepthTarget("scene-depth-1x", fullSize);
        // GTAO defaults to half resolution: the full-resolution horizon search measured about 50 ms
        // against a 49.8 ms rest-of-frame baseline. Bilateral denoise restores full-size output
        // without introducing temporal reconstruction.
        ambientHandle = graph.ColorTarget(
            "ambient-visibility", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(aoScale));
        ambientDenoisedHandle = graph.ColorTarget("ambient-visibility-denoised", TextureFormat.Rgba16F, fullSize);

        // Incident light is evaluated at volume frequency: rgb is bounce and a is baked sky
        // visibility. Rgba16F preserves HDR gradients; incidentScale 1 is the full-resolution arm.
        incidentHandle = graph.ColorTarget(
            "incident-light", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(incidentScale));
        incidentFullHandle = graph.ColorTarget("incident-light-full", TextureFormat.Rgba16F, fullSize);
        incidentGradientHandle = graph.ColorTarget(
            "incident-gradient", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(incidentScale));
        incidentGradientFullHandle = graph.ColorTarget("incident-gradient-full", TextureFormat.Rgba16F, fullSize);

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

        var prepassBuilder = graph.GraphicsPass("depth-prepass")
            .Target(prepassNormalHandle, LoadOp.Clear, StoreOp.Store)
            .Depth(depthHandle, LoadOp.Clear, StoreOp.Store)
            .Shader(litInterface);
        // Same rule as the depth: at one sample the target IS what a reader wants, and asking for
        // a resolve anyway is invalid.
        if (MsaaSamples > 1) prepassBuilder = prepassBuilder.ResolveColor(prepassNormalResolveHandle);
        // Rides the pass's depth store when there is something to resolve.
        if (MsaaSamples > 1) prepassBuilder = prepassBuilder.ResolveDepth(depthResolveHandle);
        depthPrepassHandle = prepassBuilder.Handle;

        // Two-phase occlusion culling (scene_occlusion.comp). The pre-pass above is its early half: what was
        // visible last frame. Its depth is reduced into a pyramid of its own, every placement is tested
        // against it, and what is visible but was not drawn is drawn by the late pre-pass into the same
        // depth and normals, before the main pyramid and everything after read them. A separate pyramid
        // rather than the main one so that GTAO and the rest see the whole frame's depth.
        occlusionInterface = Reflect("scene_occlusion.comp");
        for (var level = 0; level < HiZLevels; level++)
        {
            occZHandles[level] = graph.ColorTarget(
                $"occ-z{level}", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(0.5f / (1 << level)));
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
        if (MsaaSamples > 1) lateBuilder = lateBuilder.ResolveColor(prepassNormalResolveHandle).ResolveDepth(depthResolveHandle);
        latePrepassHandle = lateBuilder.Handle;

        // --ray-view: the traced camera view beside the raster depth, after every pre-pass has drawn into it.
        rayViewInterface = Reflect("ray_view.comp");
        rayViewPassHandle = graph.ComputePass("ray-view").Shader(rayViewInterface).Read(SampleableSceneDepth).Handle;

        // The Hi-Z pyramid, immediately after the pre-pass that resolves the depth it reduces.
        // Level 0 is half the framebuffer — the same grid GTAO already works on, so its consumers
        // need no extra rescaling — and each level halves again.
        for (var level = 0; level < HiZLevels; level++)
        {
            hiZHandles[level] = graph.ColorTarget(
                $"hi-z{level}", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(0.5f / (1 << level)));
        }
        for (var level = 0; level < HiZLevels; level++)
        {
            var builder = graph.GraphicsPass($"hi-z{level}")
                .Target(hiZHandles[level], LoadOp.Clear, StoreOp.Store);
            // Level 0 reduces the resolved scene depth; every level after reduces its predecessor.
            builder = level == 0 ? builder.Read(SampleableSceneDepth) : builder.Read(hiZHandles[level - 1]);
            hiZPassHandles[level] = builder.Shader(hiZInterface).Handle;
        }

        // Ambient visibility, between the pre-pass that gives it depth and the lit pass that
        // consumes it. Declared here because graph order IS declaration order.
        var gtaoBuilder = graph.GraphicsPass("gtao")
            .Target(ambientHandle, LoadOp.Clear, StoreOp.Store);
        // Reads every pyramid level: which one a tap lands on depends on how far it steps, so all
        // of them are inputs and the graph orders the whole chain ahead of this pass.
        for (var level = 0; level < HiZLevels; level++) gtaoBuilder = gtaoBuilder.Read(hiZHandles[level]);
        // ReadHistory samples the prior denoised target before this frame's denoise overwrites it;
        // the graph owns the required ordering and layout transition.
        gtaoBuilder = gtaoBuilder.ReadHistory(ambientDenoisedHandle);
        gtaoPassHandle = gtaoBuilder.Shader(gtaoInterface).Handle;

        // Spatial denoise. A separate pass rather than a wider kernel inside GTAO: the estimate and
        // its reconstruction are different jobs, and only one of them has to run the horizon search.
        gtaoDenoisePassHandle = graph.GraphicsPass("gtao-denoise")
            .Target(ambientDenoisedHandle, LoadOp.Clear, StoreOp.Store)
            .Read(ambientHandle)
            .Read(SampleableSceneDepth)
            .Shader(gtaoDenoiseInterface)
            .Handle;

        // Between the depth it unprojects and the lit pass that reads it. It also reads the bounce
        // atlas the injection dispatch writes. That atlas is device-owned rather than a graph
        // resource, so declaration order carries this dependency without a graph edge.
        // Screen probes read this frame's depth and normals and write buffers the incident pass reads; declared
        // between them, and fenced like every pass that binds GPU buffers.
        screenProbeInterface = Reflect("screen_probe.comp");
        var screenProbeBuilder = graph.ComputePass("screen-probes")
            .Read(SampleableSceneDepth)
            .Read(SampleablePrepassNormal);
        for (var c = 0; c < CascadeCount; c++) screenProbeBuilder = screenProbeBuilder.Read(cascadeHandles[c]);
        screenProbePassHandle = screenProbeBuilder.Shader(screenProbeInterface).Handle;
        screenProbeFilterInterface = Reflect("screen_probe_filter.comp");
        screenProbeFilterPassHandle = graph.ComputePass("screen-probe-filter").Shader(screenProbeFilterInterface).Handle;

        incidentPassHandle = graph.GraphicsPass("incident-light")
            .Target(incidentHandle, LoadOp.Clear, StoreOp.Store)
            .Target(incidentGradientHandle, LoadOp.Clear, StoreOp.Store)
            .Read(SampleableSceneDepth)
            .Read(SampleablePrepassNormal)
            .Shader(incidentInterface, incidentClipmapInterface)
            .Handle;

        incidentResolvePassHandle = graph.GraphicsPass("incident-resolve")
            .Target(incidentFullHandle, LoadOp.Clear, StoreOp.Store)
            .Target(incidentGradientFullHandle, LoadOp.Clear, StoreOp.Store)
            .Read(incidentHandle)
            .Read(incidentGradientHandle)
            .Read(SampleableSceneDepth)
            .Read(SampleablePrepassNormal)
            .Shader(incidentResolveInterface)
            .Handle;

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
        // Declare the cascade depth targets as inputs so the graph orders the
        // shadow passes before the lit pass and transitions them to
        // shader-read layout.
        for (var c = 0; c < CascadeCount; c++)
        {
            litPass = litPass.Read(cascadeHandles[c]);
        }
        litPass = litPass.Read(ambientDenoisedHandle);
        litPass = litPass.Read(incidentFullHandle);
        litPass = litPass.Read(incidentGradientFullHandle);
        litPassHandle = litPass.Handle;

        // One pass per parity. Only one is recorded each frame; the other's target is that frame's
        // history, and ReadHistory is what lets a pass declare a read of it before it is rewritten.
        var taaInterface = Reflect("present.vert", "taa.frag");
        for (var i = 0; i < 2; i++)
        {
            taaPassHandles[i] = graph.GraphicsPass($"taa-resolve{i}")
                .Target(taaHandles[i], LoadOp.Clear, StoreOp.Store)
                .Read(hdrHandle)
                .Read(SampleableSceneDepth)
                .ReadHistory(taaHandles[i ^ 1])
                .Shader(taaInterface)
                .Handle;
        }
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

        // One quad per probe, positions synthesised: the vertex buffer exists to satisfy the draw
        // and its contents are never read, exactly as FullscreenPass does for its triangle.
        var probeVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "probe_debug.vert.spv"));
        var probeFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "probe_debug.frag.spv"));
        probeProgram = Own(device.CreateShaderProgramFromSpv(probeVertSpv, probeFragSpv, probeInterface, "probe_debug"));
        var probeDummy = new VertexPosition3NormalTexture[4];
        for (var i = 0; i < 4; i++)
            probeDummy[i] = new VertexPosition3NormalTexture(
                new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0));
        probeVb = Own(device.CreateVertexBuffer(
            VertexPosition3NormalTexture.CreateBufferData(probeDummy), "probe.vb"));
        probeIb = Own(device.CreateIndexBuffer(new ushort[] { 0, 1, 2, 2, 1, 3 }, name: "probe.ib"));
        skyPipeline = Pipeline(skyProgram,
            VertexPosition3NormalTexture.Layout, // ignored — sky vert synthesises positions
            DepthState.LessEqualNoWrite, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, litPassHandle, "skybox");

        // Shadow caster pipelines. NoCulling so Sponza's double-sided foliage
        // and thin geometry still write depth from both faces; depth-write
        // LessEqual into the cascade target. Both are render-pass-compatible
        // with every cascade pass (all depth-only, same format), so we build
        // them against cascade 0's surface and reuse across cascades.
        var shadowVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow.vert.spv"));
        var shadowFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow.frag.spv"));
        shadowOpaqueProgram = Own(device.CreateShaderProgramFromSpv(shadowVertSpv, shadowFragSpv, shadowOpaqueInterface, "shadow.opaque"));
        shadowOpaquePipeline = Pipeline(shadowOpaqueProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualWrite, RasterizerState.NoCulling,
            Array.Empty<BlendState>(), cascadePassHandles[0], "shadow.opaque");

        var shadowMaskVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow_mask.vert.spv"));
        var shadowMaskFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow_mask.frag.spv"));
        shadowMaskProgram = Own(device.CreateShaderProgramFromSpv(shadowMaskVertSpv, shadowMaskFragSpv, shadowMaskInterface, "shadow.mask"));
        shadowMaskPipeline = Pipeline(shadowMaskProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualWrite, RasterizerState.NoCulling,
            Array.Empty<BlendState>(), cascadePassHandles[0], "shadow.mask");

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
            new[] { BlendState.Disabled }, depthPrepassHandle, "depth_prepass");

        var prepassMaskFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "depth_prepass_mask.frag.spv"));
        prepassMaskProgram = Own(device.CreateShaderProgramFromSpv(litVertSpv, prepassMaskFragSpv, litInterface, "depth_prepass_mask"));
        prepassMaskPipeline = Pipeline(prepassMaskProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualWrite, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, depthPrepassHandle, "depth_prepass_mask");

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

        var gtaoFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "gtao.frag.spv"));
        gtaoProgram = Own(device.CreateShaderProgramFromSpv(presentVertSpv, gtaoFragSpv, gtaoInterface, "gtao"));
        // Graph-pass pipelines must be created against the pass surface. Swapchain-present
        // pipelines have no graph render target and are not compatible here.
        gtaoPipeline = Pipeline(gtaoProgram, VertexPosition3NormalTexture.Layout,
            DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, gtaoPassHandle, "gtao");

        var hiZSpv = File.ReadAllBytes(Path.Combine(shaderDir, "hiz_build.frag.spv"));
        hiZProgram = Own(device.CreateShaderProgramFromSpv(presentVertSpv, hiZSpv, hiZInterface, "hiz_build"));
        for (var level = 0; level < HiZLevels; level++)
        {
            hiZPipelines[level] = Pipeline(hiZProgram, VertexPosition3NormalTexture.Layout,
                DepthState.Disabled, RasterizerState.NoCulling,
                new[] { BlendState.Disabled }, hiZPassHandles[level], $"hiz{level}");
            occZPipelines[level] = Pipeline(hiZProgram, VertexPosition3NormalTexture.Layout,
                DepthState.Disabled, RasterizerState.NoCulling,
                new[] { BlendState.Disabled }, occZPassHandles[level], $"occz{level}");
        }

        // One program, two pipelines — each bound to its own pass surface, because a pipeline is
        // compatible with the render pass it was built against and the two resolve passes target
        // different images.
        var taaSpv = File.ReadAllBytes(Path.Combine(shaderDir, "taa.frag.spv"));
        var taaProgram = Own(device.CreateShaderProgramFromSpv(presentVertSpv, taaSpv, taaInterface, "taa"));
        for (var i = 0; i < 2; i++)
        {
            taaPipelines[i] = Pipeline(taaProgram, VertexPosition3NormalTexture.Layout,
                DepthState.Disabled, RasterizerState.NoCulling,
                new[] { BlendState.Disabled }, taaPassHandles[i], $"taa{i}");
        }

        var gtaoDenoiseSpv = File.ReadAllBytes(Path.Combine(shaderDir, "gtao_denoise.frag.spv"));
        gtaoDenoiseProgram = Own(device.CreateShaderProgramFromSpv(
            presentVertSpv, gtaoDenoiseSpv, gtaoDenoiseInterface, "gtao_denoise"));
        gtaoDenoisePipeline = Pipeline(gtaoDenoiseProgram, VertexPosition3NormalTexture.Layout,
            DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, gtaoDenoisePassHandle, "gtao_denoise");

        var incidentSpv = File.ReadAllBytes(Path.Combine(shaderDir, "incident.frag.spv"));
        var incidentProgram = Own(device.CreateShaderProgramFromSpv(
            presentVertSpv, incidentSpv, incidentInterface, "incident"));
        incidentPipeline = Pipeline(incidentProgram, VertexPosition3NormalTexture.Layout,
            DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled, BlendState.Disabled }, incidentPassHandle, "incident");
        var incidentClipmapSpv = File.ReadAllBytes(Path.Combine(shaderDir, "incident_clipmap.frag.spv"));
        incidentClipmapPipeline = Pipeline(Own(device.CreateShaderProgramFromSpv(
                presentVertSpv, incidentClipmapSpv, incidentClipmapInterface, "incident_clipmap")),
            VertexPosition3NormalTexture.Layout, DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled, BlendState.Disabled }, incidentPassHandle, "incident_clipmap");

        var incidentResolveSpv = File.ReadAllBytes(Path.Combine(shaderDir, "incident_resolve.frag.spv"));
        var incidentResolveProgram = Own(device.CreateShaderProgramFromSpv(
            presentVertSpv, incidentResolveSpv, incidentResolveInterface, "incident_resolve"));
        incidentResolvePipeline = Pipeline(incidentResolveProgram, VertexPosition3NormalTexture.Layout,
            DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled, BlendState.Disabled }, incidentResolvePassHandle, "incident_resolve");

        // Fullscreen triangle for the sky + present passes (positions synthesised
        // from gl_VertexIndex in the vertex shader — the buffer is never sampled).
        fullscreen = new FullscreenPass(device, "present.dummy");
        // Depth-tested so probes sit in the scene rather than over it, and no depth WRITE so they
        // never occlude the geometry whose lighting they are there to explain.
        probePipeline = Pipeline(probeProgram, VertexPosition3NormalTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, litPassHandle, "probe_debug");

        if (bounceReady)
        {
            var injectSpv = File.ReadAllBytes(Path.Combine(shaderDir, "sky_inject.comp.spv"));
            injectProgram = Own(device.CreateComputeShaderProgramFromSpv(injectSpv, injectInterface, "sky_inject"));
            injectPipeline = Own(device.CreateComputePipeline(injectProgram, "sky_inject"));
            if (rayScene || giTrace)
            {
                var tracedInterface = Reflect("sky_inject_traced.comp");
                var tracedSpv = File.ReadAllBytes(Path.Combine(shaderDir, "sky_inject_traced.comp.spv"));
                injectTracedPipeline = Own(device.CreateComputePipeline(
                    Own(device.CreateComputeShaderProgramFromSpv(tracedSpv, tracedInterface, "sky_inject_traced")), "sky_inject_traced"));
            }
            // Rebuilt each frame around the write/read pair; see BounceBindings.
            injectBindings = BounceBindings();

            var usageSpv = File.ReadAllBytes(Path.Combine(shaderDir, "probe_usage.comp.spv"));
            var usageProgram = Own(device.CreateComputeShaderProgramFromSpv(usageSpv, usageInterface, "probe_usage"));
            probeUsagePipeline = Own(device.CreateComputePipeline(usageProgram, "probe_usage"));
        }

        var cullSpv = File.ReadAllBytes(Path.Combine(shaderDir, "scene_cull.comp.spv"));
        var cullProgram = Own(device.CreateComputeShaderProgramFromSpv(cullSpv, cullInterface, "scene_cull"));
        cullPipeline = Own(device.CreateComputePipeline(cullProgram, "scene_cull"));
        var rayViewSpv = File.ReadAllBytes(Path.Combine(shaderDir, "ray_view.comp.spv"));
        rayViewPipeline = Own(device.CreateComputePipeline(
            Own(device.CreateComputeShaderProgramFromSpv(rayViewSpv, rayViewInterface, "ray_view")), "ray_view"));
        var clipmapSpv = File.ReadAllBytes(Path.Combine(shaderDir, "clipmap_inject.comp.spv"));
        clipmapPipeline = Own(device.CreateComputePipeline(
            Own(device.CreateComputeShaderProgramFromSpv(clipmapSpv, clipmapInterface, "clipmap_inject")), "clipmap_inject"));
        var screenProbeSpv = File.ReadAllBytes(Path.Combine(shaderDir, "screen_probe.comp.spv"));
        screenProbePipeline = Own(device.CreateComputePipeline(
            Own(device.CreateComputeShaderProgramFromSpv(screenProbeSpv, screenProbeInterface, "screen_probe")), "screen_probe"));
        var screenProbeFilterSpv = File.ReadAllBytes(Path.Combine(shaderDir, "screen_probe_filter.comp.spv"));
        screenProbeFilterPipeline = Own(device.CreateComputePipeline(
            Own(device.CreateComputeShaderProgramFromSpv(screenProbeFilterSpv, screenProbeFilterInterface, "screen_probe_filter")), "screen_probe_filter"));
        var bakeSpv = File.ReadAllBytes(Path.Combine(shaderDir, "ray_surface_bake.comp.spv"));
        raySurfaceBakePipeline = Own(device.CreateComputePipeline(
            Own(device.CreateComputeShaderProgramFromSpv(bakeSpv, raySurfaceBakeInterface, "ray_surface_bake")), "ray_surface_bake"));
        var rayCheckSpv = File.ReadAllBytes(Path.Combine(shaderDir, "ray_check.comp.spv"));
        rayCheckPipeline = Own(device.CreateComputePipeline(
            Own(device.CreateComputeShaderProgramFromSpv(rayCheckSpv, rayCheckInterface, "ray_check")), "ray_check"));
        var occlusionSpv = File.ReadAllBytes(Path.Combine(shaderDir, "scene_occlusion.comp.spv"));
        var occlusionProgram = Own(device.CreateComputeShaderProgramFromSpv(occlusionSpv, occlusionInterface, "scene_occlusion"));
        occlusionPipeline = Own(device.CreateComputePipeline(occlusionProgram, "scene_occlusion"));

        // --- Froxel fog compute program + grid ---------------------------
        var froxelSpv = File.ReadAllBytes(Path.Combine(shaderDir, "froxel.comp.spv"));
        froxelProgram = Own(device.CreateComputeShaderProgramFromSpv(froxelSpv, froxelInterface, "froxel"));
        froxelPipeline = Own(device.CreateComputePipeline(froxelProgram, "froxel"));
        // View-aligned 3D scattering grid, sampled trilinearly by the lit pass. Sized from a
        // swapchain-matched graph target rather than from host.LogicalSize, because that is the
        // logical size and the framebuffer behind it is 2x on a Retina display — the difference
        // between eight pixels per froxel and sixteen.
        if (!device.TryGetTextureSize(graph.GetColorTexture(ambientDenoisedHandle), out var fbW, out var fbH))
        {
            throw new InvalidOperationException(
                "VulkanSponza: the full-size ambient target has no dimensions, so the froxel grid " +
                "cannot be sized against the framebuffer.");
        }
        (froxelGridX, froxelGridY) = FroxelGridSize(fbW, fbH);
        froxelGridTexture = device.CreateStorageTexture3D(
            froxelGridX, froxelGridY, froxelGridZ,
            TextureFormat.Rgba16F, SamplerDescription.LinearClamp, "sponza.froxel_grid");
        for (var i = 0; i < 2; i++)
        {
            fogScatterTextures[i] = device.CreateStorageTexture3D(
                froxelGridX, froxelGridY, froxelGridZ,
                TextureFormat.Rgba16F, SamplerDescription.LinearClamp, $"sponza.fog_scatter{i}");
        }
        Console.WriteLine(
            $"[VulkanSponza] froxel grid {froxelGridX}x{froxelGridY}x{froxelGridZ} " +
            $"({FroxelPixels} px/froxel at {fbW}x{fbH})");

        // Build the per-frame-constant buffers after graph compilation and texture creation. Reuse
        // them every frame in OnRender.
        passBindings = new[]
        {
            new ShaderTextureBinding("uIrradiance",     irradianceCubeTexture),
            new ShaderTextureBinding("uPrefilteredEnv", envCubeTexture),
            new ShaderTextureBinding("uBrdfLut",        brdfLutTexture),
            new ShaderTextureBinding("uCascadeShadowMaps[0]", graph.GetDepthTexture(cascadeHandles[0])),
            new ShaderTextureBinding("uCascadeShadowMaps[1]", graph.GetDepthTexture(cascadeHandles[1])),
            new ShaderTextureBinding("uCascadeShadowMaps[2]", graph.GetDepthTexture(cascadeHandles[2])),
            new ShaderTextureBinding("uFroxelGrid",           froxelGridTexture),
            new ShaderTextureBinding("uAmbientVisibility", graph.GetColorTexture(ambientDenoisedHandle)),
            new ShaderTextureBinding("uSkyVisibility",  skyVisibilityTextures[0]),
            new ShaderTextureBinding("uSkyVisibility1", skyVisibilityTextures[1]),
            new ShaderTextureBinding("uSkyVisibility2", skyVisibilityTextures[2]),
            // Bound per frame in OnRender, which flips between the pair; this is the initial one.
            new ShaderTextureBinding("uSkyBounce", bounceReady ? bounceTextures[0] : brdfLutTexture),
            new ShaderTextureBinding("uSkyBounceDepth", bounceReady ? bounceDepthTextures[0] : brdfLutTexture),
            // Bound to SOMETHING valid always — a descriptor set with a hole is a device loss, not a
            // dark curtain. uSheenMipCount being zero is what tells the shader not to read them.
            new ShaderTextureBinding("uSheenEnv", sheenMipCount > 0 ? sheenEnvTexture : envCubeTexture),
            new ShaderTextureBinding("uSheenLut", sheenMipCount > 0 ? sheenLutTexture : brdfLutTexture),
            new ShaderTextureBinding("uEnvCube", skyCubeTexture),
            // Ground truth for the leak metric. Same no-holes rule as uSheenEnv above: bound to a
            // valid 3D texture whether or not the volume shipped one, with uOccupancyDims.w the
            // flag that decides whether the shader may read it.
            new ShaderTextureBinding("uOccupancy",
                occX > 0 ? occupancyTexture : skyVisibilityTextures[0]),
            new ShaderTextureBinding(
                "uIncidentField", graph.GetColorTexture(incidentFullHandle)),
            new ShaderTextureBinding(
                "uIncidentGradient", graph.GetColorTexture(incidentGradientFullHandle)),
            new ShaderTextureBinding(
                "uPrepassNormalViz", graph.GetColorTexture(SampleablePrepassNormal)),
        };

        // The one binding that is not constant: the lit pass reads whichever of the bounce pair the
        // injection is not writing, so its slot is rewritten each frame.
        skyBounceBinding = Array.FindIndex(passBindings, b => b.Name == "uSkyBounce");
        // Same reason, different cause: the grid is re-created when the framebuffer changes size.
        froxelGridBinding = Array.FindIndex(passBindings, b => b.Name == "uFroxelGrid");

        // skybox.frag's two, taken from the lit list so the two can never hold different textures.
        // uSkyBounce's per-frame swap does not reach it (the sky does not read the bounce), and
        // uFroxelGrid's re-creation does, through its own index.
        string[] skySamples = { "uFroxelGrid", "uEnvCube" };
        skyBindings = skySamples.Select(n => passBindings.Single(b => b.Name == n)).ToArray();
        skyFroxelGridBinding = Array.FindIndex(skyBindings, b => b.Name == "uFroxelGrid");

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
            foreach (var (name, primitives) in ParsePacksParallel(packsToParse, flatten))
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
            // overdraw, pre-pass work, and all shadow cascades together; the pack-level arm can.
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
            // Kept on the CPU for the sky references (SponzaLoop.ProbeReference), which need the sky's radiance.
            iblProbeCpu = probeData;
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

    /// <summary>Loads the baked sky-visibility volume, if the pack ships one.</summary>
    /// <remarks>
    /// This bake is optional. When absent, identity volumes keep descriptors valid and describe a
    /// fully visible sky, preserving a uniform binding layout across both paths.
    /// </remarks>
    private void LoadSkyVisibility(string assetsRoot)
    {
        var path = Directory.EnumerateFiles(assetsRoot, "*.blixsky", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (path is not null)
        {
            try
            {
                var volume = Blix.Graphics.Images.BlixSkyVolume.Read(path);
                for (var t = 0; t < 3; t++)
                    skyVisibilityTextures[t] = Own(device.CreateTexture3D(
                        volume.SizeX, volume.SizeY, volume.SizeZ, TextureFormat.Rgba16F,
                        SamplerDescription.LinearClamp, volume.ToRgba16F(t), $"sponza.skyvis{t}"));
                skyVisibilityTexture = skyVisibilityTextures[0];
                skyVolumeMin = volume.Min;
                var span = volume.Max - volume.Min;
                skyVolumeSpan = span;
                skyVolumeInvSpan = new Vector3(1f / span.X, 1f / span.Y, 1f / span.Z);
                skyVolumeLoaded = true;
                probeX = volume.SizeX; probeY = volume.SizeY; probeZ = volume.SizeZ;

                // Mean of the L0 band over the whole volume. Every higher band integrates to zero
                // over the sphere, so this single coefficient IS the average fraction of sky a point
                // in this scene can see — the number the probe census measures the bounce against.
                var band0 = volume.ToRgba16F(0);
                var cells = volume.SizeX * volume.SizeY * volume.SizeZ;
                cellSkyVisibility = new float[cells];
                double visSum = 0;
                for (var c = 0; c < cells; c++)
                {
                    var v = (float)((float)BitConverter.ToHalf(band0, c * 8) * 0.282095);
                    cellSkyVisibility[c] = v;
                    visSum += v;
                }
                meanSkyVisibility = cells > 0 ? visSum / cells : 0;

                if (volume.HasOccupancy)
                {
                    occX = volume.OccupancyX; occY = volume.OccupancyY; occZ = volume.OccupancyZ;
                    occupancyCpu = volume.Occupancy;
                    occCpuX = occX; occCpuY = occY; occCpuZ = occZ;
                    occupancyTexture = Own(device.CreateTexture3D(
                        occX, occY, occZ, TextureFormat.R8,
                        SamplerDescription.LinearClamp, volume.Occupancy!, "sponza.occupancy"));
                    // Falls back to the occupancy texture's slot being filled by SOMETHING valid
                    // rather than going unbound: a missing albedo grid means an older .blixsky, and
                    // the shader's uAlbedoDims.w tells it to use the flat scalar instead.
                    if (volume.HasAlbedo)
                    {
                        albX = volume.AlbedoX; albY = volume.AlbedoY; albZ = volume.AlbedoZ;
                        albedoCpu = volume.Albedo;
                        albCpuX = albX; albCpuY = albY; albCpuZ = albZ;
                        // Albedo is one-to-one with occupancy cells. Nearest sampling preserves the
                        // struck cell's material instead of mixing the one-cell surface shell with
                        // empty space or neighbouring materials.
                        albedoTexture = Own(device.CreateTexture3D(
                            albX, albY, albZ, TextureFormat.Rgba8,
                            SamplerDescription.NearestClamp, volume.Albedo!, "sponza.albedo"));
                        Console.WriteLine(
                            $"[VulkanSponza]   albedo {albX}x{albY}x{albZ} ({volume.Albedo!.Length / 1024.0 / 1024.0:0.00} MB), so the bounce carries surface colour.");
                    }
                    // --bounce-div controls the spatial side of the atlas trade-off independently
                    // from its 36 directional samples. A divisor of one matches the visibility
                    // grid's roughly 0.8 m spacing; total solve cost scales cubically per axis.
                    bounceX = Math.Max(2, (int)MathF.Round(probeX / bounceDiv));
                    bounceY = Math.Max(2, (int)MathF.Round(probeY / bounceDiv));
                    bounceZ = Math.Max(2, (int)MathF.Round(probeZ / bounceDiv));
                    var atlasW = bounceX * OctTile;
                    var atlasH = bounceY * bounceZ * OctTile;
                    for (var i = 0; i < bounceTextures.Length; i++)
                    {
                        bounceTextures[i] = Own(device.CreateStorageTexture2D(
                            atlasW, atlasH, TextureFormat.Rgba16F,
                            SamplerDescription.LinearClamp, $"sponza.bounce{i}"));
                        bounceDepthTextures[i] = Own(device.CreateStorageTexture2D(
                            atlasW, atlasH, TextureFormat.Rgba16F,
                            SamplerDescription.LinearClamp, $"sponza.bounceDepth{i}"));
                    }
                    probeUsageTexture = Own(device.CreateStorageTexture3D(
                        bounceX, bounceY, bounceZ, TextureFormat.Rgba16F,
                        SamplerDescription.LinearClamp, "sponza.probeUsage"));
                    Console.WriteLine(
                        $"[VulkanSponza]   bounce probes {bounceX}x{bounceY}x{bounceZ} = " +
                        $"{bounceX * bounceY * bounceZ:N0}, octahedral atlas {atlasW}x{atlasH} " +
                        $"({2.0 * atlasW * atlasH * 8 / 1024 / 1024:0.00} MB for both buffers)");
                    bounceReady = true;
                    Console.WriteLine(
                        $"[VulkanSponza]   occupancy {occX}x{occY}x{occZ} shipped; sun bounce injected at runtime.");
                }
                Console.WriteLine(
                    $"[VulkanSponza] sky visibility: {Path.GetFileName(path)} " +
                    $"{volume.SizeX}x{volume.SizeY}x{volume.SizeZ} probes, " +
                    $"min {volume.Min} span {span} invSpan {skyVolumeInvSpan}");
                // Report shader participation, not merely successful file I/O.
                Console.WriteLine(
                    skyVisibilityEnabled && !skipSkySample
                        ? "[VulkanSponza]   sampled: sky visibility LIVE, bounce LIVE."
                        : $"[VulkanSponza]   sampled: NOT SAMPLED — every surface sees a full sky and " +
                          $"nothing bounces (skyVisibility={skyVisibilityEnabled}, skipSample={skipSkySample}).");
                // What the CPU thinks an up-facing surface sees, at two known places, so the
                // shader's answer can be compared against something rather than eyeballed.
                foreach (var (label, at) in new[]
                {
                    ("atrium floor", new Vector3(0f, 0.5f, 0f)),
                    ("above roof",   new Vector3(0f, 18f, 0f)),
                })
                {
                    var t = (at - volume.Min) * skyVolumeInvSpan;
                    var cx = Math.Clamp((int)(t.X * volume.SizeX), 0, volume.SizeX - 1);
                    var cy = Math.Clamp((int)(t.Y * volume.SizeY), 0, volume.SizeY - 1);
                    var cz = Math.Clamp((int)(t.Z * volume.SizeZ), 0, volume.SizeZ - 1);
                    // Derive cell stride from the format so CPU diagnostics follow SH band changes.
                    var o = ((cz * volume.SizeY + cy) * volume.SizeX + cx) * BlixSkyVolume.FloatsPerCell;
                    var l0 = volume.Coefficients[o];
                    var l1 = new Vector3(volume.Coefficients[o + 1], volume.Coefficients[o + 2], volume.Coefficients[o + 3]);
                    // The same cosine-convolved L2 evaluation the shader runs (sky_visibility.glsl),
                    // against +Y. Stopping at L1 here would have made the CPU and GPU answers differ
                    // by the exact band the last commit added, which is the one worth checking.
                    // dir = +Y, so of the five L2 terms only the two that survive dir.x = dir.z = 0
                    // contribute: the zonal (3z^2 - 1) collapses to -1 and (x^2 - y^2) to -1. Writing
                    // the whole basis out and substituting would be the same number with four more
                    // ways to mistype it.
                    const float Y0 = 0.282095f, Y1 = 0.488603f, Y20C = 0.315392f, Y22C = 0.546274f;
                    var band2 = -Y20C * volume.Coefficients[o + 6] - Y22C * volume.Coefficients[o + 8];
                    var vis = (MathF.PI * Y0 * l0
                               + (2f * MathF.PI / 3f) * Y1 * Vector3.Dot(l1, Vector3.UnitY)
                               + (MathF.PI / 4f) * band2) / MathF.PI;
                    Console.WriteLine($"[VulkanSponza]   {label,-13} uv {t} -> L0 {l0:0.000} vis(up) {vis:0.000}");
                }
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VulkanSponza] sky visibility: {Path.GetFileName(path)} unreadable — {ex.Message}");
            }
        }

        var open = new byte[4 * 2];
        // L0 for a fully open sphere: integral of Y0 over the sphere = 4*pi*0.282095.
        BitConverter.TryWriteBytes(open.AsSpan(0, 2), (Half)(4f * MathF.PI * 0.282095f));
        skyVisibilityTexture = Own(device.CreateTexture3D(
            1, 1, 1, TextureFormat.Rgba16F, SamplerDescription.LinearClamp, open, "sponza.skyvis.open"));
        // All three bands are bound whether or not a volume shipped (passBindings, the froxel and incident
        // passes), and a handle of 0 is no texture at all. An open sky is L0 alone: the higher bands are zero.
        var zero = Own(device.CreateTexture3D(
            1, 1, 1, TextureFormat.Rgba16F, SamplerDescription.LinearClamp, new byte[4 * 2], "sponza.skyvis.zero"));
        skyVisibilityTextures[0] = skyVisibilityTexture;
        skyVisibilityTextures[1] = zero;
        skyVisibilityTextures[2] = zero;
        skyVolumeLoaded = false;
        Console.WriteLine("[VulkanSponza] sky visibility: none found — every surface sees a full sky.");
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
}
