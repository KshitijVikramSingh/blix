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

        // Volumetric fog is off by default (it adds a per-frame compute pass);
        // launch with --fog to start with it on, or toggle it in the overlay.
        var cmdArgs = Environment.GetCommandLineArgs();
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
        if (cmdArgs.Contains("--no-mask")) forceOpaqueMask = true;
        if (cmdArgs.Contains("--msaa1")) MsaaSamples = 1;
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
        }
        // <b>Required for any timing run, and its absence invalidated a whole measurement batch.</b>
        // With FIFO present the frame timer measures when the swapchain let go, not what the work
        // cost: every result lands on a multiple of the refresh interval, so 34 ms of work and 49 ms
        // of work both report 50. A matrix taken under vsync produced "removing work made it
        // slower", which is the shape that gave it away.
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

        // --- Render graph ------------------------------------------------
        graph = new RenderGraph(vk);
        var fullSize = new MatchSwapchainGraphSize(1.0f);
        // R11G11B10F (not Rgba16F): half the bytes/pixel → half the MSAA tile
        // footprint + resolve bandwidth on TBDR, for an opaque HDR radiance
        // target only ever sampled .rgb by tonemap. No alpha (glass blends with
        // source alpha, which needs no dst-alpha channel).
        hdrHandle = graph.ColorTarget("hdr", TextureFormat.R11G11B10F, fullSize);
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
        var presentInterface = Reflect("present.vert", "present.frag");
        // Reuses present.vert: both are fullscreen triangles synthesised from gl_VertexIndex.
        var gtaoInterface = Reflect("present.vert", "gtao.frag");
        var gtaoDenoiseInterface = Reflect("present.vert", "gtao_denoise.frag");
        var hiZInterface = Reflect("present.vert", "hiz_build.frag");
        var shadowOpaqueInterface = Reflect("shadow.vert", "shadow.frag");
        var shadowMaskInterface = Reflect("shadow_mask.vert", "shadow_mask.frag");

        // Scan lit.frag's //@tune decorators (shipped alongside the .spv) and
        // build the overlay's shader-variable panel. The panel owns the live
        // values + the dials; the per-frame write and the froxel sun term pull
        // from it by name.
        tunePanel = new ShaderTunablePanel(ShaderTunables.Scan(
            File.ReadAllText(Path.Combine(shaderDir, "lit.frag"))));
        tuneObjects = new ObjectTunables(fog, shadows, render, ambient);

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
        depthResolveHandle = graph.DepthTarget("scene-depth-1x", fullSize);
        // <b>HALF resolution, and the measurement is what decided it.</b> At full res the horizon
        // search alone measured ~50 ms against a 49.8 ms baseline for the whole rest of the frame —
        // it doubled the picture's cost. Quartering the pixels quarters that. The argument against
        // half res was that it needs a bilateral upsample and reconstruction should not creep in,
        // and that argument was wrong: the denoise below is already a spatial filter, and the house
        // rule bans TEMPORAL reconstruction, which neither of these is.
        ambientHandle = graph.ColorTarget(
            "ambient-visibility", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(aoScale));
        ambientDenoisedHandle = graph.ColorTarget("ambient-visibility-denoised", TextureFormat.Rgba16F, fullSize);

        depthPrepassHandle = graph.GraphicsPass("depth-prepass")
            .Depth(depthHandle, LoadOp.Clear, StoreOp.Store)
            .ResolveDepth(depthResolveHandle)     // free-ish: rides the pass's depth store
            .Shader(litInterface)
            .Handle;

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
            builder = level == 0 ? builder.Read(depthResolveHandle) : builder.Read(hiZHandles[level - 1]);
            hiZPassHandles[level] = builder.Shader(hiZInterface).Handle;
        }

        // Ambient visibility, between the pre-pass that gives it depth and the lit pass that
        // consumes it. Declared here because graph order IS declaration order.
        var gtaoBuilder = graph.GraphicsPass("gtao")
            .Target(ambientHandle, LoadOp.Clear, StoreOp.Store);
        // Reads every pyramid level: which one a tap lands on depends on how far it steps, so all
        // of them are inputs and the graph orders the whole chain ahead of this pass.
        for (var level = 0; level < HiZLevels; level++) gtaoBuilder = gtaoBuilder.Read(hiZHandles[level]);
        gtaoPassHandle = gtaoBuilder.Shader(gtaoInterface).Handle;

        // Spatial denoise. A separate pass rather than a wider kernel inside GTAO: the estimate and
        // its reconstruction are different jobs, and only one of them has to run the horizon search.
        gtaoDenoisePassHandle = graph.GraphicsPass("gtao-denoise")
            .Target(ambientDenoisedHandle, LoadOp.Clear, StoreOp.Store)
            .Read(ambientHandle)
            .Read(depthResolveHandle)
            .Shader(gtaoDenoiseInterface)
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
        litPassHandle = litPass.Handle;
        graph.Compile();

        // --- Shader programs + pipelines --------------------------------
        // shaderDir was resolved above (reflection sidecars live alongside the .spv).
        var litVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "lit.vert.spv"));
        var litFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "lit.frag.spv"));
        litProgram = vk.CreateShaderProgramFromSpv(litVertSpv, litFragSpv, litInterface, "lit");
        // Opaque + Mask share the no-blend pipeline group. Depth is now
        // LessEqual + NO write: the depth pre-pass already wrote the complete
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

        // Depth pre-pass pipelines — lit.vert (shared → invariant depth) + a
        // trivial fragment, depth-only into the 4× MSAA pre-pass surface. No
        // culling: solid geometry's nearest face still wins the depth test
        // (matching lit's back-cull front face), and double-sided geometry
        // always writes depth from either view side so the sky never overdraws
        // a back-facing curtain. LessEqualWrite; the lit pass then reads it.
        // Flat preview pipeline (streamed-load phase): lit.vert + flat.frag, lit
        // surface, depth-test no-write (the pre-pass wrote depth). Reuses
        // litInterface; flat.frag samples nothing, so set1/set2 stay unbound (as
        // with the trivial pre-pass program).
        var flatFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "flat.frag.spv"));
        flatProgram = vk.CreateShaderProgramFromSpv(litVertSpv, flatFragSpv, litInterface, "flat");
        flatPipeline = Pipeline(flatProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, litPassHandle, "flat");

        var prepassOpaqueFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "depth_prepass.frag.spv"));
        prepassOpaqueProgram = vk.CreateShaderProgramFromSpv(litVertSpv, prepassOpaqueFragSpv, litInterface, "depth_prepass");
        prepassOpaquePipeline = Pipeline(prepassOpaqueProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualWrite, RasterizerState.NoCulling,
            Array.Empty<BlendState>(), depthPrepassHandle, "depth_prepass");

        var prepassMaskFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "depth_prepass_mask.frag.spv"));
        prepassMaskProgram = vk.CreateShaderProgramFromSpv(litVertSpv, prepassMaskFragSpv, litInterface, "depth_prepass_mask");
        prepassMaskPipeline = Pipeline(prepassMaskProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualWrite, RasterizerState.NoCulling,
            Array.Empty<BlendState>(), depthPrepassHandle, "depth_prepass_mask");

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
        // <b>Through Pipeline(), so it carries graph.GetPassSurface(gtaoPassHandle).</b> Built the
        // way the PRESENT pipeline is built — vk.CreatePipeline with no RenderTarget — it compiled,
        // bound, and drew its triangle, and the target came back every pixel zero: present is
        // recorded straight on the command list against the swapchain, so its pipeline needs no
        // pass surface, and copying that shape into a GRAPH pass silently produces a pipeline
        // compatible with the wrong render pass.
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

        var gtaoDenoiseSpv = File.ReadAllBytes(Path.Combine(shaderDir, "gtao_denoise.frag.spv"));
        gtaoDenoiseProgram = vk.CreateShaderProgramFromSpv(
            presentVertSpv, gtaoDenoiseSpv, gtaoDenoiseInterface, "gtao_denoise");
        gtaoDenoisePipeline = Pipeline(gtaoDenoiseProgram, VertexPosition3NormalTexture.Layout,
            DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, gtaoDenoisePassHandle, "gtao_denoise");

        // Fullscreen triangle for the sky + present passes (positions synthesised
        // from gl_VertexIndex in the vertex shader — the buffer is never sampled).
        fullscreen = new FullscreenPass(vk, "present.dummy");

        // --- Froxel fog compute program + grid ---------------------------
        var froxelSpv = File.ReadAllBytes(Path.Combine(shaderDir, "froxel.comp.spv"));
        froxelProgram = vk.CreateComputeShaderProgramFromSpv(froxelSpv, froxelInterface, "froxel");
        froxelPipeline = vk.CreateComputePipeline(froxelProgram, "froxel");
        // View-aligned 3D scattering grid, sampled trilinearly by the lit pass.
        froxelGridTexture = vk.CreateStorageTexture3D(
            FroxelGridX, FroxelGridY, FroxelGridZ,
            TextureFormat.Rgba16F, SamplerDescription.LinearClamp, "sponza.froxel_grid");

        // Build the per-frame-constant buffers once (graph compiled + all
        // textures created by now). Reused every frame in OnRender.
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
        };

        // Froxel compute set-0 image bindings (constant handles): the storage
        // grid it writes (binding 1) + the cascade shadow maps it samples
        // (binding 2, Count=3). The UBO (binding 0) is written per frame.
        froxelBindings = new[]
        {
            new ShaderTextureBinding("uGrid", froxelGridTexture, Slot: 1),
            new ShaderTextureBinding("uCascadeShadowMaps[0]", graph.GetDepthTexture(cascadeHandles[0]), Slot: 2, ArrayIndex: 0),
            new ShaderTextureBinding("uCascadeShadowMaps[1]", graph.GetDepthTexture(cascadeHandles[1]), Slot: 2, ArrayIndex: 1),
            new ShaderTextureBinding("uCascadeShadowMaps[2]", graph.GetDepthTexture(cascadeHandles[2]), Slot: 2, ArrayIndex: 2),
        };

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
        AddOptionalPackPath(packsToParse, assetsRoot, "curtains", "addons/curtains");
        AddOptionalPackPath(packsToParse, assetsRoot, "ivy",      "addons/ivy");
        AddOptionalPackPath(packsToParse, assetsRoot, "trees",    "addons/trees");
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

    // IBL: prefer a cooked .blixprobe baked from an HDR sky (real GGX importance-
    // sampled specular + cosine irradiance + split-sum BRDF LUT, RGBA16F), else
    // the procedural analytic-sky bake (vanilla checkout that hasn't cooked one).
    // Preference: autumn_field (has a sun → high light/dark contrast for punchy
    // shadows; we align our directional sun to its detected sun) → rogland
    // overcast (sunless ambient, our own sun) → old sky → procedural. Sun
    // alignment is automatic: HdrSunFinder returns null for skies with no clear
    // sun, so we only align when the probe actually has one.
    private void LoadIbl(string assetsRoot)
    {
        string[] probeCandidates = { "autumn_field_4k.blixprobe", "rogland_overcast_4k.blixprobe", "sky_hdr.blixprobe" };
        var probeDir = Path.Combine(assetsRoot, "textures");
        var probePath = probeCandidates
            .Select(p => Path.Combine(probeDir, p))
            .FirstOrDefault(File.Exists)
            // <b>Then ANY probe in the directory, because a named list silently ignores one you
            // cooked.</b> The three names above are a real preference — the first has a sun to align
            // the directional light to, the second deliberately has none — so they keep priority.
            // What they should not do is send a person back to the procedural sky while a perfectly
            // good .blixprobe sits beside them under a name nobody hardcoded.
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
            irradianceCubeTexture = baked.Probe.DiffuseIrradiance;
            brdfLutTexture = baked.BrdfLut;
            iblPrefilterMips = baked.Probe.PrefilteredSpecularMipCount;
            Console.WriteLine($"[VulkanSponza] IBL: cooked probe {Path.GetFileName(probePath)} ({iblPrefilterMips} GGX prefilter mips).");
            // Align the directional sun (key light + shadow caster) to the probe's
            // detected sun so cast shadows match the visible sky sun. FROM-sun-
            // into-scene convention, matching sunDirection.
            if (baked.Probe.SunDirectionFromEquirect is { } hdrSun)
            {
                sunDirection = Vector3.Normalize(hdrSun);
                sunPitch = MathF.Asin(Math.Clamp(sunDirection.Y, -1f, 1f));
                sunYaw = MathF.Atan2(sunDirection.X, -sunDirection.Z);
                Console.WriteLine($"[VulkanSponza]   sun aligned to probe: {sunDirection}");
            }

            // <b>And its brightness comes from the same measurement.</b> The probe reports the
            // irradiance the sun actually delivers in the HDR's units, and the bake removed that
            // disc from the diffuse and specular integrals — so the sun arrives once, in the same
            // units as the sky. The scale that used to sit between them is not tuned to a better
            // value here; it no longer exists.
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

    /// <summary>The pack's model file: a cooked <c>.blixmesh</c> when there is one, else the glTF.</summary>
    /// <remarks>
    /// <b>Cooked FIRST, and that ordering is the point rather than an optimisation.</b> A cooked
    /// mesh now carries its own materials and an image table saying where every texture lives, so a
    /// pack directory holding nothing but a <c>.blixmesh</c> and its <c>.blixtex</c> files is a
    /// complete, loadable pack. Globbing for <c>*.gltf</c> first would have found nothing in exactly
    /// the layout the cook exists to produce — 6.3 GB of main Sponza becomes 1.6 GB, and the glTF
    /// and its 133 MB buffer are not part of it.
    /// <para>
    /// The glTF fallback stays for an uncooked checkout, which is still a supported way to run this.
    /// The importer takes either path and reports which one it took.
    /// </para>
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
        irradianceCubeTexture = sky.Irradiance;
        brdfLutTexture = sky.BrdfLut;
        iblPrefilterMips = sky.PrefilterMips;
    }
}
