using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Graphics.Vulkan;
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
        vk.CreatePipeline(new PipelineDescription(
            program, layout, PrimitiveTopology.Triangles, depth, raster, blend,
            RenderTarget: graph.GetPassSurface(target),
            AlphaToCoverage: alphaToCoverage), name);

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        vk = (VulkanGraphicsDevice)graphicsDevice;
        textureLoader = new GltfTextureLoader(vk);
        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        renderHeightPx = host.LogicalSize.Height;

        // Fog is part of the standard composition; --no-fog retains the unscattered reference path.
        var cmdArgs = Environment.GetCommandLineArgs();
        fog.Enabled = !cmdArgs.Contains("--no-fog");
        if (cmdArgs.Contains("--fog")) fog.Enabled = true;
        // --fog-stress flips fog on/off every ~90 frames so a validation run
        // exercises the compute storage-image layout transitions across the
        // disabled↔enabled boundary (the highest-risk sync path).
        if (cmdArgs.Contains("--fog-stress")) { fogStress = true; fog.Enabled = true; }
        // --no-ao: keep both ambient passes in the graph but give the search a zero radius, so a
        // paired run attributes the HORIZON SEARCH specifically rather than the whole feature.
        if (cmdArgs.Contains("--no-ao")) ambient.Enabled = false;
        if (cmdArgs.Contains("--no-shadow")) shadows.Enabled = false;
        if (cmdArgs.Contains("--ao-fullres")) aoScale = 1f;
        // The incident-light field ships on; --no-incident selects the inline reference path.
        if (cmdArgs.Contains("--no-incident")) incidentField = false;
        if (cmdArgs.Contains("--incident")) incidentField = true;
        if (cmdArgs.Contains("--incident-full")) { incidentField = true; incidentScale = 1f; }
        for (var i = 0; i + 1 < cmdArgs.Length; i++)
        {
            // The resolution knob itself, because "half" is a guess and the error it costs is a
            // function of how far the coarse texel centre sits from the fine pixel it answers for.
            if (cmdArgs[i] == "--incident-scale" && float.TryParse(cmdArgs[i + 1], out var isc))
            {
                incidentField = true;
                incidentScale = Math.Clamp(isc, 0.25f, 1f);
            }
        }
        if (cmdArgs.Contains("--no-prepass")) noPrepass = true;
        for (var i = 0; i < cmdArgs.Length - 1; i++)
        {
            if (cmdArgs[i] == "--probe") probeName = cmdArgs[i + 1];
            // The census has to be able to ask about the FIELD rather than about the sleep policy:
            // with sleeping on, a probe the camera never looked at is zero, and a census of a still
            // camera's frame is then mostly a count of what the camera did not face.
            if (cmdArgs[i] == "--probe-sleep" && float.TryParse(cmdArgs[i + 1], out var ps))
                probeSleepFrames = MathF.Max(0f, ps);
            // Zero isolates sky-fed transport from the direct-sun source for probe censuses.
            if (cmdArgs[i] == "--sun-strength" && float.TryParse(cmdArgs[i + 1], out var ss))
                sunStrength = MathF.Max(0f, ss);
            if (cmdArgs[i] == "--ref-bounces" && int.TryParse(cmdArgs[i + 1], out var rb))
                refBounces = Math.Clamp(rb, 1, 8);
            if (cmdArgs[i] == "--shadow-maps" && cmdArgs[i + 1].Split(',') is { Length: 3 } sm
                && int.TryParse(sm[0], out var m0) && int.TryParse(sm[1], out var m1)
                && int.TryParse(sm[2], out var m2))
            {
                ShadowMapSizes = new[]
                {
                    Math.Clamp(m0, 256, 4096), Math.Clamp(m1, 256, 4096), Math.Clamp(m2, 256, 4096),
                };
            }
            if (cmdArgs[i] == "--foliage-lod" && float.TryParse(cmdArgs[i + 1], out var fl))
                FoliageLodMargin = MathF.Max(0.1f, fl);
            if (cmdArgs[i] == "--shadow-lod" && float.TryParse(cmdArgs[i + 1], out var sl))
                shadowLodTexels = MathF.Max(0.1f, sl);
            if (cmdArgs[i] == "--bounce-div" && float.TryParse(cmdArgs[i + 1], out var bd))
                bounceDiv = Math.Clamp(bd, 0.5f, 8f);
            // The same axis stated the way it is usually wanted: a multiplier on probe COUNT.
            // --bounce-x2 is --bounce-div 1.587 without anybody having to know that.
            if (cmdArgs[i] == "--bounce-x" && float.TryParse(cmdArgs[i + 1], out var bx) && bx > 0f)
                bounceDiv = 2f / MathF.Cbrt(bx);
        }
        for (var i = 0; i + 1 < cmdArgs.Length; i++)
        {
            if (cmdArgs[i] == "--inject-feedback" && float.TryParse(cmdArgs[i + 1], out var ifb))
                injectFeedback = Math.Clamp(ifb, 0f, 4f);
            if (cmdArgs[i] == "--transport-occlusion" && float.TryParse(cmdArgs[i + 1], out var to))
                transportOcclusion = Math.Clamp(to, 0f, 1f);
        }
        if (cmdArgs.Contains("--sky-no-inject")) skipInject = true;
        if (cmdArgs.Contains("--sky-no-sample")) skipSkySample = true;
        // Standard rendering uses baked enclosure and dynamic bounce. --no-sky disables both;
        // --sky remains a compatibility no-op for existing invocations.
        skyVisibilityEnabled = !cmdArgs.Contains("--no-sky");
        if (skyVisibilityEnabled)
        {
            // Exposure accounts for the display level of the physically attenuated composition;
            // transport strength remains one so material albedo is not silently reinterpreted.
            render.Exposure = 1.0f;
        }
        if (cmdArgs.Contains("--no-mask")) forceOpaqueMask = true;
        // The A/B for the single-sample canopy: hashed stochastic cutout against the plain binary
        // one. Re-cooks nothing and rebuilds nothing — it changes one number in the material.
        if (cmdArgs.Contains("--no-hashed-alpha")) hashedAlpha = false;
        if (cmdArgs.Contains("--msaa1")) MsaaSamples = 1;
        if (cmdArgs.Contains("--msaa2")) MsaaSamples = 2;
        // Present so the sample count can be swept from the command line in BOTH directions. Without
        // it only the non-default could be asked for, so a paired run could not be ordered 4-2-2-4 —
        // and on this machine a single ordering is not a measurement.
        if (cmdArgs.Contains("--msaa4")) MsaaSamples = 4;
        // Align shadows and direct light to a detected environment sun by default. --sun-authored
        // opts out; probes without a detectable sun naturally retain the authored direction.
        if (!cmdArgs.Contains("--sun-authored")) alignSunToProbe = true;
        // --sun-overhead maximizes directly lit courtyard area when isolating base lighting. The
        // cascade fit handles the vertical-light up-vector degeneracy.
        if (cmdArgs.Contains("--sun-overhead")) sunOverhead = true;
        // Expose the probe view to automated/headless runs as well as the overlay checkbox.
        if (cmdArgs.Contains("--show-probes")) showProbes = true;
        if (cmdArgs.Contains("--probe-carryless")) probeCarryless = true;
        // GPU isolation submits and waits per pass. It attributes real tile execution but removes
        // overlap, so isolated pass times are not additive components of the normal frame.
        if (cmdArgs.Contains("--gpu-isolate")) vk.GpuPassIsolation = true;
        if (cmdArgs.Contains("--no-caster-cull")) shadowCasterCull = false;
        if (cmdArgs.Contains("--no-foliage")) noFoliage = true;
        if (cmdArgs.Contains("--probe-reference")) probeReference = true;
        if (cmdArgs.Contains("--no-sky-bounce")) noSkyBounce = true;
        for (var i = 0; i < cmdArgs.Length - 1; i++)
        {
            if (cmdArgs[i] == "--fog-slices" && int.TryParse(cmdArgs[i + 1], out var fs))
                froxelGridZ = Math.Clamp(fs, 8, 128);
        }
        // --orbit: drive the camera on a fixed path so a measurement is of the renderer rather than
        // of one photograph of it. Ignores --cam, which is the still counterpart.
        if (cmdArgs.Contains("--orbit")) orbit = true;
        // --cam x,y,z,yaw,pitch — a reproducible viewpoint. Without it every capture and every
        // census speaks only for wherever the camera happens to start, which for a question like
        // "how much of this scene is occluded" is the difference between a measurement and an
        // anecdote.
        for (var i = 0; i < cmdArgs.Length - 1; i++)
        {
            if (cmdArgs[i] != "--cam") continue;
            var parts = cmdArgs[i + 1].Split(',');
            if (parts.Length >= 5
                && float.TryParse(parts[0], out var cx) && float.TryParse(parts[1], out var cy)
                && float.TryParse(parts[2], out var cz) && float.TryParse(parts[3], out var cyaw)
                && float.TryParse(parts[4], out var cpitch))
            {
                cameraPosition = new Vector3(cx, cy, cz);
                camYaw = cyaw * MathF.PI / 180f;
                camPitch = cpitch * MathF.PI / 180f;
            }
        }
        if (cmdArgs.Contains("--ab-flat")) { abFlat = true; abMode = "flat"; }
        for (var i = 0; i < cmdArgs.Length - 1; i++)
        {
            if (cmdArgs[i] != "--ab") continue;
            abMode = cmdArgs[i + 1];
            abFlat = abMode == "flat";
        }
        for (var i = 0; i < cmdArgs.Length - 1; i++)
        {
            if (cmdArgs[i] == "--ao-debug" && float.TryParse(cmdArgs[i + 1], out var aoDebugValue)) aoDebug = aoDebugValue;
            if (cmdArgs[i] == "--viz" && float.TryParse(cmdArgs[i + 1], out var vizValue)) vizChannel = vizValue;
            if (cmdArgs[i] == "--ao-radius" && float.TryParse(cmdArgs[i + 1], out var aoRadius)) ambient.RadiusMetres = aoRadius;
            // --lod-arms <onPx> <offPx>: the two budgets --ab lod alternates between.
            if (cmdArgs[i] == "--lod-arms" && i + 2 < cmdArgs.Length
                && float.TryParse(cmdArgs[i + 1], out var lodOn)
                && float.TryParse(cmdArgs[i + 2], out var lodOff))
            {
                lodArmOn = lodOn;
                lodArmOff = lodOff;
            }
        }
        // Timing runs require --no-vsync; FIFO quantizes frame periods to refresh intervals and can
        // reverse small A/B differences.
        if (cmdArgs.Contains("--no-vsync")) { startUnsynced = true; vk.VsyncEnabled = false; }
        // --shot <path>: render --shot-frames frames, write the ambient-visibility buffer and the
        // tonemapped scene beside it, and close. Headless in the sense that matters — nobody has
        // to be watching.
        for (var i = 0; i < cmdArgs.Length - 1; i++)
        {
            if (cmdArgs[i] == "--shot") shotPath = cmdArgs[i + 1];
            if (cmdArgs[i] == "--shot-frames" && int.TryParse(cmdArgs[i + 1], out var sf)) shotFrame = sf;
        }

        // Seed sun yaw/pitch from the default direction so the Sun controls
        // start matching the baked look.
        sunPitch = MathF.Asin(Math.Clamp(sunDirection.Y, -1f, 1f));
        sunYaw = MathF.Atan2(sunDirection.X, -sunDirection.Z);

        // Diagnostics overlay: GPU info + this loop's shadow/camera controls,
        // live values, and cascade gizmos (see Debug()).
        if (host is IDebugHost debugHost && debugHost.System is { } dbg)
        {
            // Only register the GPU contributor. The runtime already runs this
            // loop's Debug() via Run(debuggable) since it implements IDebuggable
            // — also registering it would run (and render its controls) twice.
            graphicsDevice.RegisterDebug(dbg);
            // Kept so click-to-pick can CollectSelectables()/Select(); the
            // SceneSelection contributor is registered after consolidation.
            debugSystem = dbg;
        }

        if (!TryLocateSponza(out var assetsRoot, out var gltfPath)) return;
        LoadIbl(assetsRoot);
        LoadSkyVisibility(assetsRoot);

        // --- Render graph ------------------------------------------------
        graph = new RenderGraph(vk);
        var fullSize = new MatchSwapchainGraphSize(1.0f);
        // R11G11B10F (not Rgba16F): half the bytes/pixel → half the MSAA tile
        // footprint + resolve bandwidth on TBDR, for an opaque HDR radiance
        // target only ever sampled .rgb by tonemap. No alpha (glass blends with
        // source alpha, which needs no dst-alpha channel).
        hdrHandle = graph.ColorTarget("hdr", TextureFormat.R11G11B10F, fullSize);
        // TAA ping-pongs because each resolve samples prior output while writing the next image.
        for (var i = 0; i < 2; i++)
            taaHandles[i] = graph.ColorTarget($"taa{i}", TextureFormat.R11G11B10F, fullSize);
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
        // reflects exactly what its stages declare; per-draw descriptor binding
        // skips any per-pass texture a program doesn't sample, so the skybox
        // no longer has to restate the lit pass's set-1 bindings for "layout
        // compatibility" — there is no shared bound set to be compatible with.
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        ShaderInterface Reflect(params string[] stages) =>
            ShaderReflection.MergeStages(
                stages.Select(s => ShaderReflection.Load(
                    Path.Combine(shaderDir, s + ".spv.refl.json"))).ToArray());

        var litInterface = Reflect("lit.vert", "lit.frag");
        var skyInterface = Reflect("skybox.vert", "skybox.frag");
        var probeInterface = Reflect("probe_debug.vert", "probe_debug.frag");
        var presentInterface = Reflect("present.vert", "present.frag");
        // Reuses present.vert: both are fullscreen triangles synthesised from gl_VertexIndex.
        var gtaoInterface = Reflect("present.vert", "gtao.frag");
        var gtaoDenoiseInterface = Reflect("present.vert", "gtao_denoise.frag");
        var hiZInterface = Reflect("present.vert", "hiz_build.frag");
        var incidentInterface = Reflect("present.vert", "incident.frag");
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
        tunePanel = new ShaderTunablePanel(ShaderTunables.Scan(
            File.ReadAllText(Path.Combine(shaderDir, "lit.frag"))));
        tuneObjects = new ObjectTunables(fog, shadows, render, ambient);

        // --tune <uName>=<value>, repeatable. A shader dial that can only be reached from the
        // overlay cannot be measured, because an --ab run has no overlay.
        var cmdArgsTune = Environment.GetCommandLineArgs();
        for (var i = 0; i < cmdArgsTune.Length - 1; i++)
        {
            if (cmdArgsTune[i] != "--tune") continue;
            var kv = cmdArgsTune[i + 1].Split('=');
            if (kv.Length != 2 || !float.TryParse(kv[1], out var tv)) continue;
            Console.WriteLine(tunePanel.TrySetValue(kv[0], tv)
                ? $"[VulkanSponza] tune {kv[0]} = {tv}"
                : $"[VulkanSponza] tune {kv[0]}: no such shader uniform — ignored.");
        }

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
        incidentPassHandle = graph.GraphicsPass("incident-light")
            .Target(incidentHandle, LoadOp.Clear, StoreOp.Store)
            .Read(SampleableSceneDepth)
            .Read(SampleablePrepassNormal)
            .Shader(incidentInterface)
            .Handle;

        incidentResolvePassHandle = graph.GraphicsPass("incident-resolve")
            .Target(incidentFullHandle, LoadOp.Clear, StoreOp.Store)
            .Read(incidentHandle)
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
        litProgram = vk.CreateShaderProgramFromSpv(litVertSpv, litFragSpv, litInterface, "lit");
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
        skyProgram = vk.CreateShaderProgramFromSpv(skyVertSpv, skyFragSpv, skyInterface, "skybox");

        // One quad per probe, positions synthesised: the vertex buffer exists to satisfy the draw
        // and its contents are never read, exactly as FullscreenPass does for its triangle.
        var probeVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "probe_debug.vert.spv"));
        var probeFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "probe_debug.frag.spv"));
        probeProgram = vk.CreateShaderProgramFromSpv(probeVertSpv, probeFragSpv, probeInterface, "probe_debug");
        var probeDummy = new VertexPosition3NormalTexture[4];
        for (var i = 0; i < 4; i++)
            probeDummy[i] = new VertexPosition3NormalTexture(
                new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0));
        probeVb = vk.CreateVertexBuffer(
            VertexPosition3NormalTexture.CreateBufferData(probeDummy), "probe.vb");
        probeIb = vk.CreateIndexBuffer(new ushort[] { 0, 1, 2, 2, 1, 3 }, name: "probe.ib");
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
        shadowOpaqueProgram = vk.CreateShaderProgramFromSpv(shadowVertSpv, shadowFragSpv, shadowOpaqueInterface, "shadow.opaque");
        shadowOpaquePipeline = Pipeline(shadowOpaqueProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualWrite, RasterizerState.NoCulling,
            Array.Empty<BlendState>(), cascadePassHandles[0], "shadow.opaque");

        var shadowMaskVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow_mask.vert.spv"));
        var shadowMaskFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow_mask.frag.spv"));
        shadowMaskProgram = vk.CreateShaderProgramFromSpv(shadowMaskVertSpv, shadowMaskFragSpv, shadowMaskInterface, "shadow.mask");
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
        flatProgram = vk.CreateShaderProgramFromSpv(litVertSpv, flatFragSpv, litInterface, "flat");
        flatPipeline = Pipeline(flatProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, litPassHandle, "flat");

        var prepassOpaqueFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "depth_prepass.frag.spv"));
        prepassOpaqueProgram = vk.CreateShaderProgramFromSpv(litVertSpv, prepassOpaqueFragSpv, litInterface, "depth_prepass");
        prepassOpaquePipeline = Pipeline(prepassOpaqueProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualWrite, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, depthPrepassHandle, "depth_prepass");

        var prepassMaskFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "depth_prepass_mask.frag.spv"));
        prepassMaskProgram = vk.CreateShaderProgramFromSpv(litVertSpv, prepassMaskFragSpv, litInterface, "depth_prepass_mask");
        prepassMaskPipeline = Pipeline(prepassMaskProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualWrite, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, depthPrepassHandle, "depth_prepass_mask");

        var presentVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.vert.spv"));
        var presentFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.frag.spv"));
        presentProgram = vk.CreateShaderProgramFromSpv(presentVertSpv, presentFragSpv, presentInterface, "present");
        presentPipeline = vk.CreatePipeline(new PipelineDescription(
            presentProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.Disabled,
            RasterizerState.NoCulling,
            BlendState.Disabled), "present");

        var gtaoFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "gtao.frag.spv"));
        gtaoProgram = vk.CreateShaderProgramFromSpv(presentVertSpv, gtaoFragSpv, gtaoInterface, "gtao");
        // Graph-pass pipelines must be created against the pass surface. Swapchain-present
        // pipelines have no graph render target and are not compatible here.
        gtaoPipeline = Pipeline(gtaoProgram, VertexPosition3NormalTexture.Layout,
            DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, gtaoPassHandle, "gtao");

        var hiZSpv = File.ReadAllBytes(Path.Combine(shaderDir, "hiz_build.frag.spv"));
        hiZProgram = vk.CreateShaderProgramFromSpv(presentVertSpv, hiZSpv, hiZInterface, "hiz_build");
        for (var level = 0; level < HiZLevels; level++)
        {
            hiZPipelines[level] = Pipeline(hiZProgram, VertexPosition3NormalTexture.Layout,
                DepthState.Disabled, RasterizerState.NoCulling,
                new[] { BlendState.Disabled }, hiZPassHandles[level], $"hiz{level}");
        }

        // One program, two pipelines — each bound to its own pass surface, because a pipeline is
        // compatible with the render pass it was built against and the two resolve passes target
        // different images.
        var taaSpv = File.ReadAllBytes(Path.Combine(shaderDir, "taa.frag.spv"));
        var taaProgram = vk.CreateShaderProgramFromSpv(presentVertSpv, taaSpv, taaInterface, "taa");
        for (var i = 0; i < 2; i++)
        {
            taaPipelines[i] = Pipeline(taaProgram, VertexPosition3NormalTexture.Layout,
                DepthState.Disabled, RasterizerState.NoCulling,
                new[] { BlendState.Disabled }, taaPassHandles[i], $"taa{i}");
        }

        var gtaoDenoiseSpv = File.ReadAllBytes(Path.Combine(shaderDir, "gtao_denoise.frag.spv"));
        gtaoDenoiseProgram = vk.CreateShaderProgramFromSpv(
            presentVertSpv, gtaoDenoiseSpv, gtaoDenoiseInterface, "gtao_denoise");
        gtaoDenoisePipeline = Pipeline(gtaoDenoiseProgram, VertexPosition3NormalTexture.Layout,
            DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, gtaoDenoisePassHandle, "gtao_denoise");

        var incidentSpv = File.ReadAllBytes(Path.Combine(shaderDir, "incident.frag.spv"));
        var incidentProgram = vk.CreateShaderProgramFromSpv(
            presentVertSpv, incidentSpv, incidentInterface, "incident");
        incidentPipeline = Pipeline(incidentProgram, VertexPosition3NormalTexture.Layout,
            DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, incidentPassHandle, "incident");

        var incidentResolveSpv = File.ReadAllBytes(Path.Combine(shaderDir, "incident_resolve.frag.spv"));
        var incidentResolveProgram = vk.CreateShaderProgramFromSpv(
            presentVertSpv, incidentResolveSpv, incidentResolveInterface, "incident_resolve");
        incidentResolvePipeline = Pipeline(incidentResolveProgram, VertexPosition3NormalTexture.Layout,
            DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, incidentResolvePassHandle, "incident_resolve");

        // Fullscreen triangle for the sky + present passes (positions synthesised
        // from gl_VertexIndex in the vertex shader — the buffer is never sampled).
        fullscreen = new FullscreenPass(vk, "present.dummy");
        // Depth-tested so probes sit in the scene rather than over it, and no depth WRITE so they
        // never occlude the geometry whose lighting they are there to explain.
        probePipeline = Pipeline(probeProgram, VertexPosition3NormalTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, litPassHandle, "probe_debug");

        if (bounceReady)
        {
            var injectSpv = File.ReadAllBytes(Path.Combine(shaderDir, "sky_inject.comp.spv"));
            injectProgram = vk.CreateComputeShaderProgramFromSpv(injectSpv, injectInterface, "sky_inject");
            injectPipeline = vk.CreateComputePipeline(injectProgram, "sky_inject");
            // Rebuilt each frame around the write/read pair; see BounceBindings.
            injectBindings = BounceBindings();

            var usageSpv = File.ReadAllBytes(Path.Combine(shaderDir, "probe_usage.comp.spv"));
            var usageProgram = vk.CreateComputeShaderProgramFromSpv(usageSpv, usageInterface, "probe_usage");
            probeUsagePipeline = vk.CreateComputePipeline(usageProgram, "probe_usage");
        }

        // --- Froxel fog compute program + grid ---------------------------
        var froxelSpv = File.ReadAllBytes(Path.Combine(shaderDir, "froxel.comp.spv"));
        froxelProgram = vk.CreateComputeShaderProgramFromSpv(froxelSpv, froxelInterface, "froxel");
        froxelPipeline = vk.CreateComputePipeline(froxelProgram, "froxel");
        // View-aligned 3D scattering grid, sampled trilinearly by the lit pass. Sized from a
        // swapchain-matched graph target rather than from host.LogicalSize, because that is the
        // logical size and the framebuffer behind it is 2x on a Retina display — the difference
        // between eight pixels per froxel and sixteen.
        if (!vk.TryGetTextureSize(graph.GetColorTexture(ambientDenoisedHandle), out var fbW, out var fbH))
        {
            throw new InvalidOperationException(
                "VulkanSponza: the full-size ambient target has no dimensions, so the froxel grid " +
                "cannot be sized against the framebuffer.");
        }
        (froxelGridX, froxelGridY) = FroxelGridSize(fbW, fbH);
        froxelGridTexture = vk.CreateStorageTexture3D(
            froxelGridX, froxelGridY, froxelGridZ,
            TextureFormat.Rgba16F, SamplerDescription.LinearClamp, "sponza.froxel_grid");
        for (var i = 0; i < 2; i++)
        {
            fogScatterTextures[i] = vk.CreateStorageTexture3D(
                froxelGridX, froxelGridY, froxelGridZ,
                TextureFormat.Rgba16F, SamplerDescription.LinearClamp, $"sponza.fog_scatter{i}");
        }
        Console.WriteLine(
            $"[VulkanSponza] froxel grid {froxelGridX}x{froxelGridY}x{froxelGridZ} " +
            $"({FroxelPixels} px/froxel at {fbW}x{fbH})");

        // Build the per-frame-constant buffers after graph compilation and texture creation. Reuse
        // them every frame in OnRender.
        identityPush = ModelPushBytes(Matrix4x4.Identity);
        passBindings = new[]
        {
            new ShaderTextureBinding("uIrradiance",     irradianceCubeTexture, Slot: 0),
            new ShaderTextureBinding("uPrefilteredEnv", envCubeTexture,        Slot: 1),
            new ShaderTextureBinding("uBrdfLut",        brdfLutTexture,        Slot: 2),
            new ShaderTextureBinding("uCascadeShadowMaps[0]", graph.GetDepthTexture(cascadeHandles[0]), Slot: 3, ArrayIndex: 0),
            new ShaderTextureBinding("uCascadeShadowMaps[1]", graph.GetDepthTexture(cascadeHandles[1]), Slot: 3, ArrayIndex: 1),
            new ShaderTextureBinding("uCascadeShadowMaps[2]", graph.GetDepthTexture(cascadeHandles[2]), Slot: 3, ArrayIndex: 2),
            new ShaderTextureBinding("uFroxelGrid",           froxelGridTexture, Slot: 4),
            new ShaderTextureBinding("uAmbientVisibility", graph.GetColorTexture(ambientDenoisedHandle), Slot: 5),
            new ShaderTextureBinding("uSkyVisibility",  skyVisibilityTextures[0], Slot: 6),
            new ShaderTextureBinding("uSkyVisibility1", skyVisibilityTextures[1], Slot: 14),
            new ShaderTextureBinding("uSkyVisibility2", skyVisibilityTextures[2], Slot: 15),
            // Bound per frame in OnRender, which flips between the pair; this is the initial one.
            new ShaderTextureBinding("uSkyBounce", bounceReady ? bounceTextures[0] : brdfLutTexture, Slot: 7),
            new ShaderTextureBinding("uSkyBounceDepth", bounceReady ? bounceDepthTextures[0] : brdfLutTexture, Slot: 13),
            // Bound to SOMETHING valid always — a descriptor set with a hole is a device loss, not a
            // dark curtain. uSheenMipCount being zero is what tells the shader not to read them.
            new ShaderTextureBinding("uSheenEnv", sheenMipCount > 0 ? sheenEnvTexture : envCubeTexture, Slot: 8),
            new ShaderTextureBinding("uSheenLut", sheenMipCount > 0 ? sheenLutTexture : brdfLutTexture, Slot: 9),
            new ShaderTextureBinding("uEnvCube", skyCubeTexture, Slot: 10),
            // Ground truth for the leak metric. Same no-holes rule as uSheenEnv above: bound to a
            // valid 3D texture whether or not the volume shipped one, with uOccupancyDims.w the
            // flag that decides whether the shader may read it.
            new ShaderTextureBinding("uOccupancy",
                occX > 0 ? occupancyTexture : skyVisibilityTextures[0], Slot: 16),
            new ShaderTextureBinding(
                "uIncidentField", graph.GetColorTexture(incidentFullHandle), Slot: 17),
            new ShaderTextureBinding(
                "uPrepassNormalViz", graph.GetColorTexture(SampleablePrepassNormal), Slot: 18),
        };

        // The one binding that is not constant: the lit pass reads whichever of the bounce pair the
        // injection is not writing, so its slot is rewritten each frame.
        skyBounceBinding = Array.FindIndex(passBindings, b => b.Name == "uSkyBounce");
        // Same reason, different cause: the grid is re-created when the framebuffer changes size.
        froxelGridBinding = Array.FindIndex(passBindings, b => b.Name == "uFroxelGrid");

        // --- Load Sponza geometry ---------------------------------------
        // Static-mesh importer (Sponza has no skinning): each glTF primitive
        // becomes one mesh with a baked-in node transform; its material is
        // resolved + cached by GetMaterial. Parsing is pure CPU (~7s — the bulk
        // of the load), so the AsyncLoadQueue runs it off-thread; the GPU work
        // (StageDrawable + ConsolidateBuffers) drains on the main thread via
        // TryFinishLoad. Until then sceneLoaded is false and OnRender shows a
        // responsive clear (loading screen). Candles excluded (no light source).
        var packsToParse = new List<(string Name, string Path, string AssetId)>
        {
            ("main", gltfPath, "models/sponza_main"),
        };
        // --no-foliage excludes cutout geometry from every pass. A uniform A/B cannot price discard,
        // overdraw, pre-pass work, and all shadow cascades together; the pack-level arm can.
        AddOptionalPackPath(packsToParse, assetsRoot, "curtains", "addons/curtains");
        if (!noFoliage)
        {
            AddOptionalPackPath(packsToParse, assetsRoot, "ivy",   "addons/ivy");
            AddOptionalPackPath(packsToParse, assetsRoot, "trees", "addons/trees");
        }
        meshLoad.Start(() =>
        {
            var prims = new List<GltfPrimitive>();
            foreach (var (name, model) in ParsePacksParallel(packsToParse))
            {
                prims.AddRange(model.Primitives);
                Console.WriteLine($"[VulkanSponza] {name} pack: {model.Primitives.Length} primitives.");
            }
            return prims;
        });

        UpdateCamera();
    }

    // Resolve the Sponza glTF: BLIX_SPONZA_ASSETS (the ~19GB pack set on an
    // external SSD shared across machines) when set, else the bin-local Assets/
    // copy. Closes the window cleanly when the packs aren't present (a vanilla
    // checkout that hasn't run setup-sponza-modern.sh) and returns false so
    // OnLoad bails. The glTF filename varies across Khronos revisions, so glob.
    private bool TryLocateSponza(out string assetsRoot, out string gltfPath)
    {
        gltfPath = "";
        assetsRoot = Environment.GetEnvironmentVariable("BLIX_SPONZA_ASSETS") is { Length: > 0 } env
            ? env
            : Path.Combine(AppContext.BaseDirectory, "Assets");
        var mainPackDir = Path.Combine(assetsRoot, "main_sponza");
        if (!Directory.Exists(mainPackDir))
        {
            Console.WriteLine($"[VulkanSponza] Main Sponza assets not found at {mainPackDir}.");
            Console.WriteLine("[VulkanSponza] Run tools/setup-sponza-modern.sh once to populate from your local Khronos packs,");
            Console.WriteLine("[VulkanSponza] or set BLIX_SPONZA_ASSETS to an existing pack dir (e.g. on an external SSD).");
            host.RequestClose();
            return false;
        }
        var found = FirstAsset(mainPackDir);
        if (found is null)
        {
            Console.WriteLine($"[VulkanSponza] No .blixmesh or .gltf in {mainPackDir}. Re-run tools/setup-sponza-modern.sh.");
            host.RequestClose();
            return false;
        }
        gltfPath = found;
        return true;
    }

    // IBL prefers a cooked .blixprobe, then any probe in the texture directory, and finally the
    // procedural sky. DefaultProbeCandidates defines the named order. Detected probe suns align
    // direct light automatically unless --sun-authored opts out.
    private void LoadIbl(string assetsRoot)
    {
        // --probe places an explicit asset ahead of the default preference list without requiring a
        // rebuild. Pizzo Pernice is the standard first choice; its detected sun direction and
        // irradiance independently round-trip to the source HDR within the recorded tolerance.
        string[] probeCandidates = probeName is { Length: > 0 }
            ? new[] { probeName.EndsWith(".blixprobe", StringComparison.Ordinal) ? probeName : probeName + ".blixprobe" }
                .Concat(DefaultProbeCandidates).ToArray()
            : DefaultProbeCandidates;
        var probeDir = Path.Combine(assetsRoot, "textures");
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
            var baked = EnvironmentBaker.UploadCookedProbe(vk, BlixProbeReader.Read(probePath), "sponza.ibl");
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

    private static void AddOptionalPackPath(
        List<(string Name, string Path, string AssetId)> packs, string assetsRoot, string packDirName, string assetId)
    {
        var packDir = Path.Combine(assetsRoot, packDirName);
        if (!Directory.Exists(packDir)) return;
        var asset = FirstAsset(packDir);
        if (asset is not null) packs.Add((packDirName, asset, assetId));
    }

    /// <summary>Returns the pack's cooked mesh when present, otherwise its source glTF.</summary>
    /// <remarks>
    /// A cooked pack is self-contained through <c>.blixmesh</c> material/image metadata and sibling
    /// <c>.blixtex</c> files. Source glTF remains a supported fallback for uncooked checkouts.
    /// </remarks>
    private static string? FirstAsset(string packDir) =>
        Directory.EnumerateFiles(packDir, "*.blixmesh", SearchOption.TopDirectoryOnly).FirstOrDefault()
        ?? Directory.EnumerateFiles(packDir, "*.gltf", SearchOption.TopDirectoryOnly).FirstOrDefault();

    // Procedural-sky IBL fallback (no cooked .blixprobe). The synthesis lives in
    // the demo-owned ProceduralSky helper; this just binds the result.
    private void BakeProceduralIbl()
    {
        var sky = ProceduralSky.Bake(vk, SkyBakeSunDirection);
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
                    skyVisibilityTextures[t] = vk.CreateTexture3D(
                        volume.SizeX, volume.SizeY, volume.SizeZ, TextureFormat.Rgba16F,
                        SamplerDescription.LinearClamp, volume.ToRgba16F(t), $"sponza.skyvis{t}");
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
                    occupancyTexture = vk.CreateTexture3D(
                        occX, occY, occZ, TextureFormat.R8,
                        SamplerDescription.LinearClamp, volume.Occupancy!, "sponza.occupancy");
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
                        albedoTexture = vk.CreateTexture3D(
                            albX, albY, albZ, TextureFormat.Rgba8,
                            SamplerDescription.NearestClamp, volume.Albedo!, "sponza.albedo");
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
                        bounceTextures[i] = vk.CreateStorageTexture2D(
                            atlasW, atlasH, TextureFormat.Rgba16F,
                            SamplerDescription.LinearClamp, $"sponza.bounce{i}");
                        bounceDepthTextures[i] = vk.CreateStorageTexture2D(
                            atlasW, atlasH, TextureFormat.Rgba16F,
                            SamplerDescription.LinearClamp, $"sponza.bounceDepth{i}");
                    }
                    probeUsageTexture = vk.CreateStorageTexture3D(
                        bounceX, bounceY, bounceZ, TextureFormat.Rgba16F,
                        SamplerDescription.LinearClamp, "sponza.probeUsage");
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
        skyVisibilityTexture = vk.CreateTexture3D(
            1, 1, 1, TextureFormat.Rgba16F, SamplerDescription.LinearClamp, open, "sponza.skyvis.open");
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
