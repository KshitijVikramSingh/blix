using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanSponza;

internal sealed partial class SponzaLoop
{
    // Index buffer a drawable's LOD indices live in (chosen at consolidation).
    private IndexBufferHandle SharedIb(Drawable d) => d.IndicesAreU32 ? sharedIbU32 : sharedIbU16;

    // Fill an indirect buffer for one pass (the CPU cull, --cpu-cull): per drawable, lodSlots
    // VkDrawIndexedIndirectCommands, one per LOD level, each instanced over the placements of it this pass
    // sees at that level. Each placement is tested with its own world bounds and keeps its own LOD state. Its transform row goes into visibleScratch
    // from visibleBase on, grouped by drawable and level; a command's firstInstance is where its run
    // starts, which is what gl_InstanceIndex (instances.glsl) indexes. Levels nothing uses get
    // instanceCount 0 (a GPU no-op, so group offsets stay fixed). Returns the visible placement count.
    private int FillIndirect(
        List<Drawable> drawables, List<Placement> placements, float[] lodMargins, int[] lodState,
        IndirectBufferHandle buffer, int visibleBase, Frustum? cull, float margin)
    {
        // Failed or empty pack loads create no indirect buffer; preserve their useful diagnostics
        // instead of following them with an unrelated invalid-handle failure.
        if (drawables.Count == 0) return 0;

        var cmds = MemoryMarshal.Cast<byte, uint>(indirectScratch.AsSpan());
        var visible = 0;
        // Count submitted triangles separately from pass time so fill resolution and LOD can be
        // attributed independently.
        fillIndirectTriangles = 0;
        // --ab lod reaches every fill through this shared selector. A zero budget is PickLod's normal
        // full-detail path.
        var errorPixels = LodErrorPixelsNow;
        Span<int> counts = stackalloc int[lodSlots];
        Span<int> fill = stackalloc int[lodSlots];
        var cursor = visibleBase;
        for (var di = 0; di < drawables.Count; di++)
        {
            var d = drawables[di];
            if (levelScratch.Length < d.PlacementCount) levelScratch = new int[d.PlacementCount];
            counts.Clear();
            for (var k = 0; k < d.PlacementCount; k++)
            {
                var i = d.PlacementStart + k;
                var bounds = placements[i].Bounds;
                var vis = cull is not { } f || f.Intersects(bounds, margin);

                // Per-placement LOD margin (live-tunable) scales the global px budget.
                var lod = d.PickLod(bounds, cameraPosition, LodErrorScale, errorPixels * lodMargins[i], lodState[i]);
                lodState[i] = lod;
                levelScratch[k] = vis ? lod : -1;
                if (!vis) continue;
                counts[lod]++;
                visible++;
                fillIndirectTriangles += d.LodIndexCounts[lod] / 3;
            }

            // Lay this drawable's visible placements out level by level, then one command per level.
            var run = cursor;
            for (var l = 0; l < lodSlots; l++) { fill[l] = run; run += counts[l]; }
            for (var k = 0; k < d.PlacementCount; k++)
            {
                if (levelScratch[k] < 0) continue;
                visibleScratch[fill[levelScratch[k]]++] = (uint)placements[d.PlacementStart + k].Transform | ((uint)Math.Min(levelScratch[k], 255) << 24);   // the level in the top 8 bits, as scene_cull.glsl writes it
            }

            // One record a drawable (recordSlots 1: no drawable placed twice): its one placement's level.
            var single = recordSlots == 1 ? Math.Max(0, levelScratch[..d.PlacementCount].ToArray().Max()) : -1;
            for (var r = 0; r < recordSlots; r++)
            {
                var l = single >= 0 ? single : r;
                var has = l < d.LodIndexCounts.Length;
                var o = (di * recordSlots + r) * 5;
                cmds[o + 0] = has ? (uint)d.LodIndexCounts[l] : 0u;  // indexCount
                cmds[o + 1] = has ? (uint)counts[l] : 0u;             // instanceCount (0 = nothing at this level)
                cmds[o + 2] = has ? (uint)d.LodFirstIndex[l] : 0u;    // firstIndex
                cmds[o + 3] = (uint)d.BaseVertex;                     // vertexOffset
                cmds[o + 4] = (uint)(fill[l] - counts[l]);            // firstInstance: where this level's run starts
            }

            cursor = run;
        }
        // Write exactly this list's prefix; the buffer is sized to its count,
        // and indirectScratch is sized for the largest (opaque) list.
        device.WriteIndirectCommands(buffer, indirectScratch.AsSpan(0, drawables.Count * recordSlots * IndirectDraw.RecordStride));
        visibleWritten = Math.Max(visibleWritten, cursor);
        return visible;
    }

    // This frame's visible placement indices, every pass's run end to end, into set 3 binding 1 of the
    // current frame slot. Once per frame, after every fill and before the graph executes.
    private void UploadVisible()
    {
        // The frame's dispatches are recorded: an edit one of them carried has been sent.
        if (marginEditSent) { pendingMarginEdit = default; marginEditSent = false; }
        if (sceneInstances is not { } instances || visibleWritten == 0) return;
        instances.WriteBuffer(device.CurrentFrameSlot, 1,
            MemoryMarshal.AsBytes(visibleScratch.AsSpan(0, visibleWritten)));
        visibleWritten = 0;
    }

    // A group's commands: its drawables' recordSlots each.
    private int GroupByteOffset(OpaqueGroup g) => g.Start * recordSlots * IndirectDraw.RecordStride;
    private int GroupDrawCount(OpaqueGroup g) => g.Count * recordSlots;

    // --lod-pixels E (SponzaLoop.Setup.cs): the LOD error budget in place of RenderSettings.LodErrorPixels (-1: none given).
    private float lodPixelsOverride = -1f;
    // --ab lod reaches every selection through this. A zero budget is full detail.
    private float LodErrorPixelsNow => abMode == "lod" ? LodArmPixels : lodPixelsOverride >= 0f ? lodPixelsOverride : render.LodErrorPixels;

    // The lists a frame culls, each its own indirect records and run of the visible list: the camera's
    // over the opaque placements (shared by the depth pre-pass and the lit pass), the camera's late list
    // (what occlusion finds the early one missed) and its final one (early and late together, which the lit
    // pass draws; both GPU only), and the camera's over the blend ones. Every list before the blend one spans
    // the opaque bucket.
    private const int SceneListCamera = 0;
    private const int SceneListCameraLate = 1;
    // Early and late together: what the lit pass draws when occlusion is on.
    private const int SceneListCameraFinal = 2;
    private const int SceneListBlend = 3;
    private int SceneListRecordBase(int list) => opaqueDrawables.Count * recordSlots * list;
    private int SceneListVisibleBase(int list) => opaquePlacements.Count * list;

    // The CPU path's buffers and state per list; it has no late list.
    private IndirectBufferHandle SceneListIndirect(int list) =>
        list == SceneListCamera ? opaqueIndirect : list == SceneListBlend ? blendIndirect
        : throw new InvalidOperationException("the CPU cull has no late list");

    private int[] SceneListLodState(int list) =>
        list == SceneListCamera ? opaqueLodState : list == SceneListBlend ? blendLodState
        : throw new InvalidOperationException("the CPU cull has no late list");

    // Cull one list and choose its levels: on the CPU into its indirect buffer (FillIndirect, returning the
    // visible count), or on the GPU as scene_cull.comp's three dispatches into the scene-cull pass
    // (returning -1: the count stays on the GPU). Either way the draws then read it via DrawSceneGroup.
    // cullViewProj is a row-vector view-projection (clip = world · M), as the camera's matrices are kept.
    // occlusionEarly: this is the camera's early list, so it draws only what the occlusion test found
    // visible last frame (the late list, RecordOcclusion, catches the rest).
    private int CullList(int list, Matrix4x4? cullViewProj, float margin, bool occlusionEarly = false)
    {
        var blend = list == SceneListBlend;
        var drawables = blend ? blendDrawables : opaqueDrawables;
        var placements = blend ? blendPlacements : opaquePlacements;
        if (drawables.Count == 0) return 0;
        if (!gpuCull)
        {
            return FillIndirect(drawables, placements, blend ? blendLodMargins : opaqueLodMargins,
                SceneListLodState(list), SceneListIndirect(list), SceneListVisibleBase(list),
                cullViewProj is { } c ? Frustum.FromViewProjection(Matrix4x4.Transpose(c)) : null, margin);
        }

        var firstPlacement = blend ? opaquePlacements.Count : 0;
        var records = drawables.Count * recordSlots;
        // The edit belongs to this list's bucket only when its row is in this list's range.
        var edit = pendingMarginEdit.Placement - 1;
        var editsHere = edit >= firstPlacement && edit < firstPlacement + placements.Count;
        if (editsHere) marginEditSent = true;
        DispatchCommand Phase(int mode, int items) => CullPhase(cullPipeline, mode, items,
            cullViewProj, margin,
            firstPlacement, placements.Count, blend ? opaqueDrawables.Count : 0, SceneListVisibleBase(list),
            SceneListRecordBase(list), records,
            editsHere ? pendingMarginEdit : default,
            new Vector4(occlusionEarly ? (occlusionCut ? 2f : 1f) : 0f, CameraNearPlane, 0f, 0f), null);

        graph.Dispatch(cullPassHandle, Phase(0, records));
        graph.Dispatch(cullPassHandle, Phase(1, placements.Count));
        graph.Dispatch(cullPassHandle, Phase(2, placements.Count));
        return -1;
    }

    // One dispatch of either cull shader; every uniform the shared Cull block (scene_cull.glsl) declares.
    private DispatchCommand CullPhase(
        PipelineHandle pipeline, int mode, int items,
        Matrix4x4? cullViewProj, float margin,
        int firstPlacement, int placementCount, int firstDrawable, int visibleBase, int recordBase, int records,
        (int Placement, float Margin) edit, Vector4 occlusion, IReadOnlyList<ShaderTextureBinding>? textures)
    {
            var groups = (items + CullGroupSize - 1) / CullGroupSize;
            if (groups > 65535)
            {
                throw new InvalidOperationException(
                    $"scene cull: {items} items is {groups} workgroups, past the 65535 one dispatch dimension holds.");
            }
            var uniforms = new ShaderUniform[]
            {
                new("uCull", new Matrix4x4Uniform(cullViewProj ?? viewProj)),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, LodErrorScale))),
                new("uLod", new Vector3Uniform(new Vector3(LodErrorPixelsNow, LodHysteresis, margin))),
                new("uRange", new Vector4Uniform(new Vector4(firstPlacement, placementCount, firstDrawable, visibleBase))),
                new("uArgs", new Vector4Uniform(new Vector4(recordBase, records, lodSlots, mode))),
                new("uSlots", new Vector4Uniform(new Vector4(lodSlots, recordSlots, 0f, 0f))),
                new("uFlags", new Vector3Uniform(new Vector3(cullViewProj is null ? 0f : 1f, edit.Placement, edit.Margin))),
                new("uView", new Matrix4x4Uniform(cameraView)),
                new("uOcclusion", new Vector4Uniform(occlusion)),
            };
            return new DispatchCommand(pipeline, Math.Max(1, groups), 1, 1, uniforms,
                textures ?? Array.Empty<ShaderTextureBinding>(), Buffers: cullBuffers);
    }

    // The late half of two-phase occlusion: reduce the early pre-pass's depth into its own pyramid, test
    // every opaque placement against it (scene_occlusion.comp), and draw what is visible but was not drawn
    // into the same depth and normals. The lit pass then draws the final list, early and late together.
    private Matrix4x4 occlusionLastView, occlusionLastProjection;
    private int occlusionLastPlacements, occlusionStillFrames;
    // --occlusion-always: the occlusion test and late pre-pass every frame, still or not.
    private bool occlusionAlways;

    private void RecordOcclusion(Matrix4x4? cameraCull, IReadOnlyList<ShaderUniform> perFrame, int frameWidth, int frameHeight)
    {
        RecordPyramid(occZPassHandles, occZPipelines, occZHandles, frameWidth, frameHeight, skip: false);

        var records = opaqueDrawables.Count * recordSlots;
        var pyramid = new ShaderTextureBinding[HiZLevels];
        for (var level = 0; level < HiZLevels; level++)
        {
            pyramid[level] = new ShaderTextureBinding($"uOccZ{level}", graph.GetColorTexture(occZHandles[level]));
        }
        DispatchCommand Phase(int mode, int items, int list, int visibleList) => CullPhase(occlusionPipeline, mode, items,
            cameraCull, CameraCullMargin,
            0, opaquePlacements.Count, 0, SceneListVisibleBase(visibleList), SceneListRecordBase(list), records,
            default, new Vector4(0f, CameraNearPlane,
                SceneListVisibleBase(SceneListCameraLate), SceneListVisibleBase(SceneListCameraFinal)), pyramid);
        // occlude reads the early slots (uRange.w) and writes the late and final ones (uOcclusion.zw), counting
        // into the late records (uArgs.x) and the final ones after them; reset and scatter run per list.
        graph.Dispatch(occlusionPassHandle, Phase(0, records, SceneListCameraLate, SceneListCameraLate));
        graph.Dispatch(occlusionPassHandle, Phase(0, records, SceneListCameraFinal, SceneListCameraFinal));
        graph.Dispatch(occlusionPassHandle, Phase(3, opaquePlacements.Count, SceneListCameraLate, SceneListCamera));
        graph.Dispatch(occlusionPassHandle, Phase(2, opaquePlacements.Count, SceneListCameraLate, SceneListCameraLate));
        graph.Dispatch(occlusionPassHandle, Phase(2, opaquePlacements.Count, SceneListCameraFinal, SceneListCameraFinal));

        graph.Pass(latePrepassHandle, scope =>
        {
            foreach (var g in opaqueGroups)
            {
                if (g.IsMask && SkipMaskDraws) continue;
                DrawSceneGroup(scope, SceneListCameraLate, g, g.IsMask ? prepassMaskPipeline : prepassOpaquePipeline,
                    perFrame, Array.Empty<ShaderTextureBinding>(), material: g.IsMask ? g.Material : null);
            }
        });
    }

    // A Hi-Z pyramid (hiz_build.frag): level 0 reduces the resolved scene depth, each level after its parent.
    private void RecordPyramid(
        PassHandle[] passes, PipelineHandle[] pipelines, GraphResourceHandle[] targets, int frameWidth, int frameHeight, bool skip)
    {
        Matrix4x4.Invert(cameraProjection, out var hiZInvProjection);
        for (var level = 0; level < HiZLevels; level++)
        {
            var srcW = level == 0 ? frameWidth : Math.Max(1, frameWidth >> level);
            var srcH = level == 0 ? frameHeight : Math.Max(1, frameHeight >> level);
            var dstW = Math.Max(1, frameWidth >> (level + 1));
            var dstH = Math.Max(1, frameHeight >> (level + 1));
            var source = level == 0
                ? graph.GetDepthTexture(SampleableSceneDepth)
                : graph.GetColorTexture(targets[level - 1]);
            var uniforms = new ShaderUniform[]
            {
                new("uInvProjection", new Matrix4x4Uniform(hiZInvProjection)),
                new("uSizes", new Vector4Uniform(new Vector4(srcW, srcH, dstW, dstH))),
                new("uMode",  new Vector4Uniform(new Vector4(level == 0 ? 1f : 0f, 0f, 0f, 0f))),
            };
            var pipeline = pipelines[level];
            graph.Pass(passes[level], scope =>
            {
                if (skip) return;
                fullscreen.Draw(
                    scope, pipeline,
                    new[] { new ShaderTextureBinding("uSource", source) },
                    pushConstants: null,
                    uniforms: uniforms);
            });
        }
    }

    private const int CullGroupSize = 64;
    // Whether a classify dispatch this frame carried pendingMarginEdit; cleared with it at frame end.
    private bool marginEditSent;

    // One group's indirect draw from a culled list: the GPU's records and set-3 buffers, or the CPU's.
    private void DrawSceneGroup(
        RenderPassBuilder scope, int list, OpaqueGroup g, PipelineHandle pipeline,
        IReadOnlyList<ShaderUniform> uniforms, IReadOnlyList<ShaderTextureBinding> textures,
        MaterialHandle? material, byte[]? pushConstants = null, bool litLayout = true)
    {
        var ib = g.IsU32 ? sharedIbU32 : sharedIbU16;
        // Every lit-layout draw binds the lit interface's set-1 buffers (LitSceneBuffers: the traced shadow mask).
        if (gpuCull)
        {
            scope.DrawIndexedIndirect(sharedVb, ib, pipeline, sceneArgs,
                (SceneListRecordBase(list) + g.Start * recordSlots) * IndirectDraw.RecordStride, GroupDrawCount(g),
                uniforms, textures, material, pushConstants, litLayout ? LitSceneBuffers : sceneBuffers);
            return;
        }
        scope.DrawIndexedIndirect(sharedVb, ib, pipeline, SceneListIndirect(list), GroupByteOffset(g), GroupDrawCount(g),
            uniforms, textures, material, pushConstants, perDrawMaterial: sceneInstances!.Handle,
            buffers: litLayout ? LitMaskBuffer : null);
    }

    // lit.frag declares the ray-traced shadow mask, and every program built on its interface (the pre-pass, flat) shares
    // that layout, so all of them bind it; the shadow programs have their own and must not (a buffer bound by a name the
    // program lacks is an error). RecordRtShadows drops both lists each frame it binds a new slot's mask.
    private ShaderBufferBinding[]? litSceneBuffers;
    private ShaderBufferBinding[]? litMaskBuffer;
    private ShaderBufferBinding[] LitMaskBuffer =>
        litMaskBuffer ??= new[] { new ShaderBufferBinding("RtShadowMask", RtShadowMask) };
    private ShaderBufferBinding[] LitSceneBuffers => litSceneBuffers ??= sceneBuffers.Concat(LitMaskBuffer).ToArray();

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        // The CPU this loop spends building a frame: culling, recording, and the graph's translation of
        // it, which is the cost GPU culling exists to take off the CPU. Post-load frames only, outside
        // an --ab run, alongside the frame periods (WriteFrameStats).
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var counted = fullyLoaded && abMode.Length == 0;
        RenderFrame(time, frame, commandList);
        if (!counted) return;
        recordMs[recordCount % recordMs.Length] = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        recordCount++;
    }

    private readonly double[] recordMs = new double[600];
    private int recordCount;

    private void RenderFrame(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        ProfileFrameBegin();
        // RenderSettings.RenderScale: the scene renders at this fraction of the window, every swapchain-sized target with
        // it, and the present pass scales the resolved image up (the overlay and imgui stay at the window's). A change
        // reallocates the targets, which the generation check below sees. Every pass below takes the frame's size from
        // here, so it agrees with its targets.
        if (MathF.Abs(graph.RenderScale - render.RenderScale) > 1e-4f) graph.RenderScale = render.RenderScale;
        var (renderWidth, renderHeight) = RenderGraph.RenderSizeFor(frame.Width, frame.Height, graph.RenderScale);
        frame = frame with { Width = renderWidth, Height = renderHeight };
        // The pixels the scene renders: what a screen-space error means (LodErrorScale). The window's size is in logical
        // points (OnResize, host.LogicalSize) -- half the pixels on a Retina display at render scale 1 -- and it was
        // read as pixels until 2026-10-27, so the mesh LODs were picked twice as coarse as asked there.
        renderHeightPx = renderHeight;
        // Match-swapchain graph targets are reallocated rather than resized in place. Their public
        // handles stay stable, but their contents do not. TAA reads the other parity target as the
        // preceding frame, so it must refuse history once after ANY recreation — not only a
        // logical-window resize. Vsync changes and framebuffer-only resizes recreate the same targets
        // without calling this application's OnResize.
        if (graphResourceGeneration != graph.MatchSwapchainResourceGeneration)
        {
            graphResourceGeneration = graph.MatchSwapchainResourceGeneration;
            taaHistoryValid = false;
            metalFxHistoryValid = false;
            Console.WriteLine("[VulkanSponza] graph resources recreated; TAA history reset");
        }

        // Advance the global frame before every early return so loading measurements and A/B phase
        // transitions cannot stall on the branch they activate.
        framesRendered++;
        // Post-load frames are the reproducible clock for the walk, capture, and temporal sequences;
        // global frames still account for loading and A/B scheduling.
        if (fullyLoaded) postLoadFrames++;
        GpuCaptureTick();
        RtgiBenchTick();
        // A shot measured across a resize is not the frame its size says (a window maximised mid-run
        // wrote a 2940x1838 image beside 1920x1080 ones), so the size the measured frames start at is
        // kept and the shot refuses a different one. The size, not a recreation: --no-vsync recreates
        // the swapchain at the same size, and a window the OS fits to the screen as it opens has
        // settled before anything is measured.
        if (fullyLoaded && shotSizeAtLoad is null) shotSizeAtLoad = (frame.Width, frame.Height);
        else if (shotSizeAtLoad is { } atLoad && atLoad != (frame.Width, frame.Height)) shotResized = true;
        if (framesAfterLoad is { } afterLoad && postLoadFrames == afterLoad)
        {
            Console.WriteLine($"[VulkanSponza] Exiting {afterLoad} frame(s) after load, {framesRendered} in all, as asked.");
            host.RequestClose();
        }
        ApplyWalk();
        var stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (lastFrameStamp != 0)
        {
            var periodMs = System.Diagnostics.Stopwatch.GetElapsedTime(lastFrameStamp, stamp).TotalMilliseconds;
            // Smoothed for the overlay only — the A/B buckets below keep the raw period, because a
            // distribution is the whole point of that instrument and a filter would flatten it.
            lastFramePeriodMs = lastFramePeriodMs > 0.0
                ? lastFramePeriodMs + (periodMs - lastFramePeriodMs) * 0.08
                : periodMs;
            // In --ab-flat the buckets follow the phase, not the load state, so both arms are
            // post-load: no texture uploads in flight to charge to whichever arm they land in.
            var flatFrame = abMode.Length > 0 ? AbOffPhase : !fullyLoaded;
            if (abMode.Length > 0 && !fullyLoaded)
            {
                // Loading frames belong to neither arm — they carry streaming uploads.
            }
            else if (flatFrame)
            {
                flatPeriodsMs[flatPeriodCount % flatPeriodsMs.Length] = periodMs;
                flatPeriodCount++;
            }
            else if (stabilityStalled)
            {
                // The period after a --stability readback includes its wait for the GPU: not a frame's cost.
                stabilityStalled = false;
            }
            else
            {
                framePeriodsMs[framePeriodCount % framePeriodsMs.Length] = periodMs;
                framePeriodCount++;
            }
        }
        lastFrameStamp = stamp;

        if (!sceneLoaded)
        {
            // Nothing to draw yet. The graph still needs an Execute so the
            // swapchain image transitions and the present pass clears.
            graph.Pass(litPassHandle, _ => { },
                clearColor: new GraphicsColor(0.05f, 0.07f, 0.10f, 1f));
            graph.Execute(commandList);
            RecordPresentPass(commandList);
            return;
        }


        // Apply Halton (2,3) jitter as a clip-space translation: post-multiplication adds offset*w
        // to clip.xy without changing the unjittered projection used by spatial decisions.
        if ((render.Taa > 0f || MetalFxOn) && frame.Width > 0 && frame.Height > 0)
        {
            // Use the post-load clock so streaming duration cannot change a capture's jitter phase.
            var jx = (Halton(postLoadFrames, 2) - 0.5f) * 2f / frame.Width;
            var jy = (Halton(postLoadFrames, 3) - 0.5f) * 2f / frame.Height;
            // The same offset in render pixels, for MetalFX (SponzaLoop.MetalFx.cs).
            metalFxJitter = new Vector2(Halton(postLoadFrames, 2) - 0.5f, Halton(postLoadFrames, 3) - 0.5f);
            cameraJitter = Matrix4x4.CreateTranslation(jx, jy, 0f);
            viewProjJittered = viewProj * cameraJitter;
        }
        else
        {
            cameraJitter = Matrix4x4.Identity;
            viewProjJittered = viewProj;
        }

        // The half-res lighting, decided once for the frame: the traced shadows, RTGI's readout and the lit pass's
        // uIncident.y must agree on it. Only once the readout can run (the upsample rides on it): before, the incident
        // targets are cleared and the shadows traced at full resolution.
        halfLightingNow = RtgiReadoutReady;
        halfShadowsNow = halfLightingNow && render.HalfResShadows;
        // The 2x2's sampled pixel turns each frame for TAA to gather; with no TAA it stays (the first), so a still frame is
        // the same frame -- turning, the half-res lighting shimmered on a four-frame cycle with the camera still.
        if (render.Taa > 0f) lightingFrame++;

        var perFrameList = new List<ShaderUniform>
        {
            // Only raster shading receives jitter. Culling and LOD use the stable matrix so a
            // sub-pixel sample shift cannot perturb spatial policy or caches.
            new("uViewProjection",   new Matrix4x4Uniform(viewProjJittered)),
            new("uSunDirection",     new Vector3Uniform(sunDirection)),
            new("uSunIrradiance",    new Vector3Uniform(EffectiveSunIrradiance)),
            new("uCameraPos",        new Vector3Uniform(cameraPosition)),
            new("uEnvMipCount",      new FloatUniform(iblPrefilterMips)),
            new("uSheenMipCount",    new FloatUniform(sheenMipCount)),
            new("uMsaaSamples",      new FloatUniform(MsaaSamples)),
            new("uShadowStrength",   new FloatUniform(
                shadows.Enabled && !(abMode == "shadow" && AbOffPhase) ? 1f : 0f)),
            new("uScreenSize",       new Vector2Uniform(new Vector2(frame.Width, frame.Height))),
            new("uVizChannel",       new FloatUniform(vizChannel)),
            // The incident field (RTGI's light, zero until its readout is ready): x whether it holds its own occlusion
            // (2: --material-ao-on-gi), y whether the traced shadows ran at half resolution (frame.glsl).
            new("uIncident",         new Vector2Uniform(new Vector2(
                IncidentHoldsOcclusion ? (materialAoOnGi ? 2f : 1f) : 0f,
                halfShadowsNow ? 1f : 0f))),
            new("uIncidentGradient", new Vector2Uniform(new Vector2(incidentNormalBias, incidentGradientClamp))),
            new("uViewProjUnjittered", new Matrix4x4Uniform(viewProj)),
            // Last frame's un-jittered camera, or this frame's on the first (zero motion rather than garbage).
            new("uPrevViewProjUnjittered", new Matrix4x4Uniform(velocityPrevValid ? velocityPrevViewProj : viewProj)),
            // One component per --ab shading mode, live only during that mode's off-phase.
            new("uAbFlags",          new Vector4Uniform(new Vector4(
                AbOffPhase && abMode == "textures" || ShaderProbe("textures") ? 1f : 0f,
                AbOffPhase && abMode == "pbr"      || ShaderProbe("pbr") ? 1f : 0f,
                AbOffPhase && abMode == "ibl"      || ShaderProbe("ibl") ? 1f : 0f,
                AbOffPhase && abMode == "normal"   || ShaderProbe("normal") ? 1f : 0f))),
            new("uIblBrdfLut",       new FloatUniform(iblBrdfLut ? 1f : 0f)),
        };
        // The remaining //@tune uniforms (uForceOpaqueCutout, uNoBounceTerm) are appended by name
        // from the overlay panel — reflection lands each at its offset. The intensity and material
        // knobs are gone: sun and sky are measured from one capture and exposure is the artistic
        // control, so there is nothing left to balance by eye.
        tunePanel.AppendUniforms(perFrameList);
        var perFrame = perFrameList.ToArray();

        // Per-pass set-1 bindings (passBindings) and the identity model push
        // are built once at load (constant handles / identity transform) and
        // reused here — see OnLoad.

        // --- Streamed-load preview: flat geometry while textures upload ------
        // Geometry is built but its textures are still streaming into their
        // (allocated, undefined) handles, so we render the opaque set flat
        // (lit.vert + flat.frag, no material/IBL/shadow sampling) over the
        // pre-pass depth. No traced shadows or GI yet — those start once
        // fullyLoaded. The pre-pass treats everything as solid opaque (no mask
        // discard against the not-yet-uploaded albedo).
        if (!fullyLoaded || AbFlatPhase)
        {
            CullList(SceneListCamera, cullViewProj: null, margin: 0f);
            graph.Pass(depthPrepassHandle, scope =>
            {
                foreach (var g in opaqueGroups)
                    DrawSceneGroup(scope, SceneListCamera, g, prepassOpaquePipeline,
                        perFrame, Array.Empty<ShaderTextureBinding>(), material: null);
            });
            graph.Pass(litPassHandle, scope =>
            {
                foreach (var g in opaqueGroups)
                    DrawSceneGroup(scope, SceneListCamera, g, flatPipeline,
                        perFrame, Array.Empty<ShaderTextureBinding>(), material: null);
            }, clearColor: new GraphicsColor(0.05f, 0.07f, 0.10f, 1f));
            UploadVisible();
            graph.Execute(commandList);
            RecordPresentPass(commandList);
            return;
        }

        // The frame's tracer being built or the reference tracing: only what it waits on, and the cook screen
        // (SponzaLoop.CookScreen.cs).
        if (CookScreenNow)
        {
            using (Prof("cook-frame")) RecordCookFrame(commandList);
            return;
        }

        // Fill camera commands once for both depth and lit passes. Frustum culling zeroes
        // instanceCount without compacting groups, saving vertex/binning work while preserving
        // indirect offsets. Recorded off-screen geometry was 55% at the default view and 96% on
        // a measurement orbit after cook-time spatial splitting made bounds selective.
        Matrix4x4? cameraCull = cullEnabled && !(abMode == "cull" && AbOffPhase) ? viewProj : null;
        // The margin is insurance, not tuning. A chunk whose bounds sit exactly on a frustum plane
        // can fall either way on floating-point noise, and at the screen edge that reads as geometry
        // blinking in and out as you turn. Half a metre of slack costs a fraction of a percent of
        // the rejections and removes the whole class.
        RecordRaySurfaceBake();
        profFrameSize = (frame.Width, frame.Height);
        using (Prof("rt-shadows")) RecordRtShadows(frame.Width, frame.Height);
        // The sun turned by --sun-change or the moving sun (SponzaLoop.Sun.cs, SponzaLoop.SunMove.cs), before RTGI sees it.
        ApplySunChangeIfDue();
        StepSunMove();
        using (Prof("rtgi"))
        {
            UpdateRtgiBudget();
            RecordRtgi(frame.Width, frame.Height);
        }
        RtgiDumpIfDue();
        FlushTracerGroup();

        var occlusionNow = occlusionCull && render.OcclusionCull;
        // Nothing moved for two frames (the camera, the placements): last frame's occlusion test marked every
        // visible placement, so this frame's early list is the whole visible set and the late list would be empty. The
        // occlusion pyramid, its cull and the late pre-pass -- which loads and stores all five pre-pass targets, ~224 MB
        // a frame drawing nothing (2026-10-20) -- are skipped, and the lit pass draws the early list.
        var occlusionSame = cameraView == occlusionLastView && cameraProjection == occlusionLastProjection
            && opaquePlacements.Count == occlusionLastPlacements;
        // Counted in frames the test actually RAN under this camera: frames before the scene (loading, the cook screen)
        // never set the placements' visible flags, and skipping on them drew an empty list -- a black frame until the
        // camera moved (2026-10-20).
        if (!occlusionSame) occlusionStillFrames = 0;
        occlusionLastView = cameraView; occlusionLastProjection = cameraProjection; occlusionLastPlacements = opaquePlacements.Count;
        var occlusionSkipped = occlusionNow && occlusionStillFrames >= 2 && !occlusionAlways && !(abMode == "occlskip" && AbOffPhase);
        occlusionNow &= !occlusionSkipped;
        using (Prof("cull")) CullList(SceneListCamera, cameraCull, margin: CameraCullMargin, occlusionEarly: occlusionNow || occlusionSkipped);
        cameraTriangles = gpuCull ? -1 : fillIndirectTriangles;
        cameraTriangleSum += cameraTriangles;
        triangleFrames++;
        if (blendDrawables.Count > 0)
        {
            CullList(SceneListBlend, cameraCull, margin: CameraCullMargin);
        }

        // Depth pre-pass: same non-blend set as the lit pass (no cull, so the
        // depth the lit pass loads covers exactly what it shades), depth only.
        // Per group: mask binds the material (set 2 albedo) for the alpha
        // discard + the mask pipeline; opaque needs only set 0 + model push.
        graph.Pass(depthPrepassHandle, scope =>
        {
            foreach (var g in opaqueGroups)
            {
                if (g.IsMask && SkipMaskDraws) continue;
                DrawSceneGroup(scope, SceneListCamera, g, g.IsMask ? prepassMaskPipeline : prepassOpaquePipeline,
                    perFrame, Array.Empty<ShaderTextureBinding>(), material: g.IsMask ? g.Material : null);
            }
        });
        if (occlusionNow)
        {
            using (Prof("occlusion")) RecordOcclusion(cameraCull, perFrame, frame.Width, frame.Height);
            occlusionStillFrames++;
        }

        // The incident-light field: RTGI's readout at half resolution, brought up to the full-size targets the lit pass
        // reads. Before the readout is ready (the scene loading, the Metal tracer pending) the targets are cleared and
        // nothing drawn: the lit pass reads no indirect light rather than a guess.
        {
            // The JITTERED projection: what the depth these passes read was rasterized with. Reconstructing with the
            // unjittered one put each point up to a pixel's footprint off its surface -- far off on a face seen
            // edge-on.
            Matrix4x4.Invert(cameraProjection * cameraJitter, out var incidentInvProj);
            Matrix4x4.Invert(cameraView, out var incidentInvView);
            RecordGpuReference();
            UpdateScreenRest();
            // A settled, still view's screen light every other frame (SponzaLoop.RtgiBudget.cs): the targets hold it.
            if (screenRestNow) { }
            else if (halfLightingNow)
            {
                RecordRtgiReadout();
                // The first settle draws no light (SponzaLoop.RtgiSettle.cs): the readout for its map and error alone.
                if (!RtgiSettleNow) RecordLightingUpsample(incidentInvProj, incidentInvView, frame.Height);
                if (RtgiTemporalNow) RecordRtgiTemporal();
            }
            // Recorded empty: the graph runs a pass with nothing drawn, so its Clear load op leaves the targets black
            // rather than undefined.
            else graph.Pass(incidentPassHandle, _ => { });
        }

        var settling = RtgiSettleNow;
        var litProbe = Environment.GetEnvironmentVariable("BLIX_PROBE") is { } lp && AbArm == 1 ? lp : "";
        if (settling || litProbe == "nolit") graph.Pass(litPassHandle, _ => { }, clearColor: new GraphicsColor(0.03f, 0.04f, 0.06f, 1f));
        else graph.Pass(litPassHandle, scope =>
        {
            // Opaque + Mask first (depth-test, no write — pre-pass wrote depth),
            // then Blend (depth-test only), so translucent surfaces composite
            // over the resolved opaque depth without writing into it.
            //
            // One indirect draw per (pipeline, material) group over the buffer
            // filled above — ~800 per-object draws collapse to ~one per material.
            // All draws in a group share set0 + set2 (material) + set 3 (the scene's placements),
            // the multidraw constraint.
            var leanNow = LitLeanNow;
            foreach (var g in opaqueGroups)
            {
                if (g.IsMask && (SkipMaskDraws || litProbe == "nomask")) continue;
                var pipeline = g.Pipeline;
                if (!g.IsMask && litEarlyZ)
                {
                    var lean = leanNow && !richMaterials.Contains(g.Material);
                    if (pipeline == opaqueSolidPipeline) pipeline = lean ? opaqueSolidPipelineLean : opaqueSolidPipelineEarly;
                    else if (pipeline == opaqueDoubleSidedPipeline) pipeline = lean ? opaqueDoubleSidedPipelineLean : opaqueDoubleSidedPipelineEarly;
                }
                if (litProbe == "litflat") pipeline = g.Pipeline == opaqueDoubleSidedPipeline ? flatPipeline : flatSolidPipeline;
                DrawSceneGroup(scope, occlusionNow ? SceneListCameraFinal : SceneListCamera, g, pipeline, perFrame, passBindings, g.Material);
            }
            // Sky after opaque, before blend. Fullscreen triangle; positions are
            // synthesised in skybox.vert, so it binds set-0 perFrame + textures only.
            fullscreen.Draw(scope, skyPipeline, skyBindings, uniforms: perFrame);
            // Blend (glass), depth-test only, after opaque + sky. One indirect
            // draw per (pipeline, material) group, same as opaque.
            foreach (var g in blendGroups)
            {
                DrawSceneGroup(scope, SceneListBlend, g, g.Pipeline, perFrame, passBindings, g.Material);
            }
        }, clearColor: new GraphicsColor(0.05f, 0.07f, 0.10f, 1f));

        // Resolve before the graph executes, so the pass is recorded with the rest of the frame.
        if (!settling) using (Prof("taa")) RecordTaaResolve();
        // Last: this frame's surface keys become the next frame's history (after TAA reads it). Only for a reader, TAA:
        // with TAA off nothing read it and it was 0.85 ms a frame at 2880 x 1620 (2026-10-24).
        if (SurfaceTargets && render.Taa > 0f)
        {
            graph.Pass(surfaceKeyHistoryPassHandle, scope => fullscreen.Draw(scope, surfaceKeyHistoryPipeline,
                new[] { new ShaderTextureBinding("uSource", graph.GetColorTexture(surfaceKeyHandle)) }));
        }
        using (Prof("upload")) UploadVisible();
        using (Prof("execute")) graph.Execute(commandList);

        RecordPresentPass(commandList);
        TickRtgiSettle();
        sceneFrameDrawn = !settling || sceneFrameDrawn;
        taaWrite = taaWriteNext;
        // The camera this frame's velocity was measured against becomes the next frame's "previous".
        velocityPrevViewProj = viewProj;
        velocityPrevValid = true;

        // Capture only a completed lit arm after at least one prior fully loaded frame has populated
        // readback targets. Deadlines use post-load frames and require a useful timing sample set.
        var measuredFrames = postLoadFrames;
        SampleStability();
        AuditOcclusion();
        WriteWalkDumps();
        WriteRtgiCapture();
        if (shotPath is { } path && !shotWritten && fullyLoaded && !AbOffPhase
            && framePeriodCount >= 60 && (measuredFrames >= shotFrame || ShotReady()))
        {
            shotWritten = true;
            if (shotResized)
            {
                BlixApps.ReportFailure("the window was resized during the shot, so its frame is not comparable; nothing was written");
                host.RequestClose();
                return;
            }
            WriteSceneShot(path);
            WriteStability(Path.ChangeExtension(path, null));
            WritePassBreakdown();
            WriteLodCensus();
            WriteClusterCensus();
            WriteRtShadows(Path.ChangeExtension(path, null));
            WriteLeakCensus(Path.ChangeExtension(path, null));
            WriteMemoryReport();
            WriteFrameTracerStats();
            WriteGpuSurfaceReference();
            WriteFrameStats();
            host.RequestClose();
        }
    }

    /// <summary>Frame-period statistics over the captured window, as a distribution rather than a number.</summary>
    /// <remarks>
    /// Median and quartiles, not a mean: this laptop throws occasional frames two and three times
    /// the typical cost, and a mean lets one of those outvote fifty good frames. The spread is
    /// printed because it is the thing that decides whether an A/B difference is real — a 5 ms
    /// effect inside a 25 ms interquartile range is not a finding, it is a wish.
    /// </remarks>
    private void WriteFrameStats()
    {
        // Name each arm after the feature actually toggled so reports remain self-describing across
        // all A/B modes.
        var term = abMode switch
        {
            "flat"   => "everything (materials+IBL+shadows+GI)",
            "shadow" => "sun shadows",
            "textures"=> "material texture bandwidth",
            "pbr"    => "the GGX specular lobe",
            "ibl"    => "image-based lighting",
            "normal" => "normal mapping",
            "cull"    => "camera frustum culling",
            "occlskip" => "the occlusion test and late pre-pass skipped while nothing moves (OFF: every frame)",
            "lod"     => lodArmOff > 0f
                ? string.Create(Inv, $"mesh LOD at {lodArmOn:0.##} px rather than {lodArmOff:0.##} px")
                : "mesh level of detail",
            _        => "post-load shading",
        };
        Report($"ON  : with {term}", framePeriodsMs, framePeriodCount);
        Report($"OFF : without {term}", flatPeriodsMs, flatPeriodCount);
        Report($"CPU : building the frame ({(gpuCull ? "GPU" : "CPU")} cull)", recordMs, recordCount);

        var lit = Median(framePeriodsMs, framePeriodCount);
        var flat = Median(flatPeriodsMs, flatPeriodCount);
        if (lit > 0 && flat > 0)
        {
            // Lead with the in-process ratio; absolute milliseconds remain useful context but vary
            // more with thermal state.
            Console.WriteLine(
                $"[VulkanSponza] {term}: {lit / flat:0.000}x of frame " +
                $"({lit - flat:0.00} ms at this run's {lit:0.0} ms median), " +
                "same geometry, same process, same thermal state, " +
                // State camera motion explicitly because LOD behaviour and the GI's caches differ
                // materially between a still view and a moving one.
                (walkTo is not null ? "camera on the walk" : "CAMERA STILL"));
        }

        static double Median(double[] buf, int count)
        {
            var n = Math.Min(count, buf.Length);
            if (n < 8) return 0;
            var sorted = buf.Take(n).OrderBy(v => v).ToArray();
            return sorted[sorted.Length / 2];
        }

        static void Report(string label, double[] buf, int count)
        {
            var n = Math.Min(count, buf.Length);
            if (n < 8) { Console.WriteLine($"[VulkanSponza] {label}: too few frames ({n})"); return; }
            var sorted = buf.Take(n).OrderBy(v => v).ToArray();
            double At(double q) => sorted[Math.Clamp((int)(q * (sorted.Length - 1)), 0, sorted.Length - 1)];
            Console.WriteLine(
                $"[VulkanSponza] {label} n={n,4}: median {At(0.5):0.00}  " +
                $"p25 {At(0.25):0.00}  p75 {At(0.75):0.00}  p95 {At(0.95):0.00}  min {sorted[0]:0.00}");
        }
    }

    /// <summary>Writes the lit scene (the HDR target) as a PNG.</summary>
    /// <remarks>
    /// Writes exposed radiance with an sRGB encode, without a filmic tonemap. This preserves a
    /// diagnostic view of whether darkness is in lighting or presentation and avoids enum-order
    /// coupling between the demo and shared tonemap helpers.
    /// </remarks>
    private void WriteSceneShot(string path)
    {
        var pixels = device.ReadTexture(
            graph.GetColorTexture(hdrHandle), out var width, out var height, out var format);
        if (format != TextureFormat.R11G11B10F)
        {
            Console.Error.WriteLine($"[VulkanSponza] scene target is {format}, expected R11G11B10F.");
            return;
        }

        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var packed = BitConverter.ToUInt32(pixels, i * 4);
            var r = UnpackFloat(packed & 0x7FF, 6);
            var g = UnpackFloat((packed >> 11) & 0x7FF, 6);
            var b = UnpackFloat((packed >> 22) & 0x3FF, 5);
            var dst = i * 4;
            rgba[dst]     = ToSrgbByte(r * render.Exposure);
            rgba[dst + 1] = ToSrgbByte(g * render.Exposure);
            rgba[dst + 2] = ToSrgbByte(b * render.Exposure);
            rgba[dst + 3] = 255;
        }

        PngWriter.WriteRgba8(path, rgba, width, height);
        Console.WriteLine($"[VulkanSponza]   {path}  (linear x exposure {render.Exposure:0.00}, sRGB encoded)");
    }

    // R11G11B10F: 5-bit exponent, no sign, mantissa 6 bits (R,G) or 5 bits (B).
    private static float UnpackFloat(uint bits, int mantissaBits)
    {
        var mantissaMask = (1u << mantissaBits) - 1u;
        var exponent = (int)(bits >> mantissaBits) & 0x1F;
        var mantissa = bits & mantissaMask;
        var scale = 1f / (mantissaMask + 1u);
        if (exponent == 0) return mantissa * scale * MathF.Pow(2f, -14f);
        if (exponent == 31) return mantissa == 0 ? float.PositiveInfinity : float.NaN;
        return (1f + mantissa * scale) * MathF.Pow(2f, exponent - 15);
    }

    private static byte ToSrgbByte(float linear)
    {
        if (float.IsNaN(linear)) return 255;   // NaN is a bug, and it should be loud
        linear = Math.Clamp(linear, 0f, 1f);
        var encoded = linear <= 0.0031308f
            ? linear * 12.92f
            : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(encoded * 255f), 0, 255);
    }

    /// <summary>Halton low-discrepancy sequence, one dimension.</summary>
    private static float Halton(int index, int radix)
    {
        var result = 0f;
        var f = 1f / radix;
        var i = index + 1;
        while (i > 0)
        {
            result += f * (i % radix);
            i /= radix;
            f /= radix;
        }
        return result;
    }

    /// <summary>What each LOD budget actually submits, counted rather than timed.</summary>
    /// <remarks>
    /// Triangle submission is the exact effect of an LOD budget and complements noisy timing. The
    /// saturation columns show how much geometry is already at each primitive's coarsest cooked
    /// level and therefore cannot benefit from a looser runtime budget.
    /// </remarks>
    private void WriteLodCensus()
    {
        if (opaquePlacements.Count == 0) return;
        // chain-bound is the submitted triangle share already at per-primitive minimum; in-frustum
        // is the triangle share surviving camera culling. Both are exact counts.
        var frustum = Frustum.FromViewProjection(Matrix4x4.Transpose(viewProj));
        Console.WriteLine("[VulkanSponza] LOD census at this camera (opaque only):");
        Console.WriteLine(
            "    budget      tris      vs 0px   chain-bound   in frustum   at own coarsest   histogram");
        long baseline = 0;
        foreach (var budget in new[] { 0f, 0.25f, 0.5f, 1f, 2f, 4f, 8f })
        {
            long indices = 0;
            long chainBound = 0;
            long visible = 0;
            var saturated = 0;
            var hist = new int[8];
            for (var i = 0; i < opaquePlacements.Count; i++)
            {
                var p = opaquePlacements[i];
                var d = opaqueDrawables[p.Drawable];
                // No hysteresis here: the census asks what a budget SETTLES at, and the band is a
                // property of how it is approached, not of where it arrives.
                var level = d.PickLod(p.Bounds, cameraPosition, LodErrorScale, budget * opaqueLodMargins[i], 0);
                var count = d.LodIndexCounts[level];
                indices += count;
                if (level == d.LodIndexCounts.Length - 1) { saturated++; chainBound += count; }
                if (frustum.Intersects(p.Bounds, 0f)) visible += count;
                if (level < hist.Length) hist[level]++;
            }
            var tris = indices / 3;
            if (budget == 0f) baseline = tris;
            var ratio = baseline > 0 ? (double)tris / baseline : 1.0;
            Console.WriteLine(string.Create(Inv,
                $"    {budget,5:0.##}px  {tris,9:N0}   {ratio,6:0.0%}   " +
                $"{(indices > 0 ? chainBound * 100.0 / indices : 0),10:0.0}%   " +
                $"{(indices > 0 ? visible * 100.0 / indices : 0),9:0.0}%   " +
                $"{saturated * 100.0 / opaquePlacements.Count,14:0.0}%   " +
                $"{hist[0]}/{hist[1]}/{hist[2]}/{hist[3]}"));
        }
    }

    /// <summary>What culling clusters rather than placements would take off the camera's triangles, here.</summary>
    /// <remarks>
    /// Exact counts at this camera, every opaque placement at the level the global budget settles at
    /// (no hysteresis, as the LOD census). Three filters, each on top of the last: the placement's
    /// bounds against the frustum (what is submitted now), each cluster's world bounds against it,
    /// and each cluster's normal cone against the camera (meshoptimizer's test: the cluster is
    /// back-facing when dot(normalize(apex - eye), axis) >= cutoff). The cone is only asked of
    /// single-sided geometry placed by a rotation and positive uniform scale; anything else keeps the
    /// cluster, and is counted as ineligible so the share the test can never reach is visible.
    /// </remarks>
    private void WriteClusterCensus()
    {
        if (opaquePlacements.Count == 0) return;
        var frustum = Frustum.FromViewProjection(Matrix4x4.Transpose(viewProj));
        var budget = LodErrorPixelsNow;
        long placementTris = 0, clusterFrustumTris = 0, coneTris = 0, ineligibleTris = 0;
        long clustersInFrustum = 0, clustersAfterCone = 0, unclustered = 0;
        for (var i = 0; i < opaquePlacements.Count; i++)
        {
            var p = opaquePlacements[i];
            if (!frustum.Intersects(p.Bounds, CameraCullMargin)) continue;
            var d = opaqueDrawables[p.Drawable];
            var level = d.PickLod(p.Bounds, cameraPosition, LodErrorScale, budget * opaqueLodMargins[i], 0);
            placementTris += d.LodIndexCounts[level] / 3;
            var clusters = d.LodClusters is { } lc && level < lc.Length ? lc[level] : Array.Empty<MeshCluster>();
            if (clusters.Count == 0)
            {
                unclustered += d.LodIndexCounts[level] / 3;
                clusterFrustumTris += d.LodIndexCounts[level] / 3;
                coneTris += d.LodIndexCounts[level] / 3;
                continue;
            }

            var world = sceneTransforms[p.Transform];
            var coneEligible = !d.DoubleSided && UniformScale(world) && world.GetDeterminant() > 0f;
            foreach (var c in clusters)
            {
                var tris = c.IndexCount / 3;
                var clusterBounds = WorldBounds(new Bounds3(c.Min, c.Max), world);
                if (!frustum.Intersects(clusterBounds, 0f)) continue;
                clustersInFrustum++;
                clusterFrustumTris += tris;
                if (!coneEligible) { ineligibleTris += tris; }
                else if (c.ConeCutoff < 1f)
                {
                    var apex = Vector3.Transform(c.ConeApex, world);
                    var axis = Vector3.Normalize(Vector3.TransformNormal(c.ConeAxis, world));
                    if (Vector3.Dot(Vector3.Normalize(apex - cameraPosition), axis) >= c.ConeCutoff) continue;
                }
                clustersAfterCone++;
                coneTris += tris;
            }
        }

        double Share(long n) => placementTris > 0 ? n * 100.0 / placementTris : 0;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] cluster census at this camera (opaque, {budget:0.##} px): "
            + $"placement culling submits {placementTris:N0} triangles; "
            + $"cluster frustum keeps {clusterFrustumTris:N0} ({Share(clusterFrustumTris):0.0}%, {clustersInFrustum:N0} clusters); "
            + $"+ normal cone keeps {coneTris:N0} ({Share(coneTris):0.0}%, {clustersAfterCone:N0} clusters). "
            + $"Cone-ineligible (double-sided or non-uniform): {Share(ineligibleTris):0.0}%; unclustered: {Share(unclustered):0.0}%."));
    }

    // The device's cumulative GPU pass totals when the scene finished loading: the breakdown is the window since.
    // (It used to print lifetime means, ~1,000 flat-preview loading frames included.)
    private Dictionary<string, GpuPassTotal> gpuTotalsAtLoad = new();

    /// <summary>Prints resolved GPU milliseconds per pass since load, heaviest per frame first.</summary>
    /// <remarks>
    /// Per run is one execution's mean; per frame divides by every frame since load, so a pass that runs every
    /// other frame or only when due counts what it costs a frame. On tile GPUs these timings attribute
    /// encoders but do not partition total frame time.
    /// </remarks>
    private void WritePassBreakdown()
    {
        if (!host.Timing.GpuTimestampsSupported)
        {
            Console.WriteLine("[VulkanSponza] GPU timestamps unsupported on this device — no pass breakdown.");
            return;
        }

        var totals = host.Timing.GpuPassTotals;
        if (totals.Count == 0)
        {
            Console.WriteLine("[VulkanSponza] no GPU pass timings resolved.");
            return;
        }

        var windowFrames = Math.Max(1, postLoadFrames);
        Console.WriteLine(string.Create(Inv, $"[VulkanSponza] GPU ms per pass since load ({windowFrames} frames; resolved timestamps):"));
        Console.WriteLine("     per-run    per-frame   runs/frame  pass");
        double frameTotal = 0;
        var window = totals
            .Select(e => (Pass: e.Key, Total: e.Value - gpuTotalsAtLoad.GetValueOrDefault(e.Key)))
            .Where(e => e.Total.Samples > 0)
            .OrderByDescending(e => e.Total.TotalMs);
        foreach (var (pass, total) in window)
        {
            var perFrame = total.TotalMs / windowFrames;
            frameTotal += perFrame;
            Console.WriteLine(string.Create(Inv,
                $"  {total.TotalMs / total.Samples,9:0.000} ms {perFrame,9:0.000} ms   {(double)total.Samples / windowFrames,8:0.00}   {pass}"));
        }
        Console.WriteLine(string.Create(Inv, $"            {frameTotal,9:0.000} ms  TOTAL per frame"));
        Console.WriteLine(
            "  NOTE: on a tile-based GPU (Apple/MoltenVK) these bracket ENCODER submission, not the "
            + "deferred tiled execution, so they do not sum to the frame. Compare them to each other, "
            + "and use paired A/B runs (--ab) for absolute cost.");
        // Report triangle means over the measurement window rather than the camera's final pose.
        var tf = Math.Max(1, triangleFrames);
        if (gpuCull)
        {
            Console.WriteLine("[VulkanSponza] triangles submitted: counted on the GPU, not read back (--cpu-cull counts them).");
        }
        else Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] triangles submitted (mean of {tf} frames): camera {cameraTriangleSum / tf:N0}"));

        // What the backend says the last frame submitted, pass by pass, beside what Sponza thinks it
        // drew. Sponza's figures above are its own culling and LOD choices; these are what reached
        // the device. Indirect records are counted and their triangles are not, because the GPU
        // decides what an indirect record draws.
        if (host.Timing.LastFrame is { } last)
        {
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] submitted in frame {last.Frame}: {last.Work.Passes} passes, {last.Work.Draws} draws " +
                $"({last.Work.Triangles:N0} triangles), {last.Work.IndirectDraws} indirect calls reading " +
                $"{last.Work.IndirectCommands:N0} records, {last.Work.Dispatches} dispatches"));
            foreach (var (pass, work) in host.Timing.LastFramePasses.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (work.Draws == 0 && work.IndirectDraws == 0 && work.Dispatches == 0) continue;
                Console.WriteLine(string.Create(Inv,
                    $"  {pass,-22} {work.Draws,5} draws {work.Triangles,12:N0} tris  {work.IndirectDraws,3} indirect {work.IndirectCommands,7:N0} records  {work.Dispatches,2} dispatches"));
            }
        }

        if (host.Timing.IsolatePasses)
        {
            // Per-run cost describes one execution; per-frame cost includes scheduling frequency.
            var frames = Math.Max(1, host.Timing.IsolatedFrames);
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] isolated GPU ms per pass over {frames} frames (own command buffer, fence-waited):"));
            Console.WriteLine("     per-run    per-frame   runs/frame  pass");
            double isoTotal = 0;
            foreach (var e in host.Timing.IsolatedPassTotals
                         .Where(e => e.Value.Samples > 0)
                         .OrderByDescending(e => e.Value.TotalMs / frames))
            {
                var perRun = e.Value.TotalMs / e.Value.Samples;
                var perFrame = e.Value.TotalMs / frames;
                isoTotal += perFrame;
                Console.WriteLine(string.Create(Inv,
                    $"  {perRun,8:0.000} ms  {perFrame,8:0.000} ms  {e.Value.Samples / (double)frames,9:0.00}   {e.Key}"));
            }
            Console.WriteLine(string.Create(Inv,
                $"             {isoTotal,8:0.000} ms  TOTAL per frame (exceeds it: isolation removes overlap)"));
        }

        // Report the submit/fence floor so sub-floor isolated timings are not overinterpreted.
        var floor = host.Timing.MeasureIsolationFloorMs();
        Console.WriteLine(string.Create(Inv,
            $"  submit+fence floor: {floor:0.000} ms — the smallest pass a per-pass command buffer could resolve; anything under this is below that instrument's noise."));
    }

    // The lean build this frame (lit_lean.frag): none of its compiled-out branches can be taken -- no debug view.
    // --no-lit-lean: never.
    private bool litLean = true;
    // --ibl-brdf-lut: the specular IBL's split sum read from the LUT (lit_body.glsl), not Karis' analytic fit.
    private bool iblBrdfLut;
    // The in-boot A/B's arm for a lit-shader term (BLIX_PROBE=textures|pbr|ibl|normal, uAbFlags).
    private bool ShaderProbe(string term) => AbArm == 1 && Environment.GetEnvironmentVariable("BLIX_PROBE") == term;
    private bool LitLeanNow => litLean && vizChannel <= 0f;

    // BLIX_SKIP_MASK=1: the alpha-masked groups (foliage) left out of the pre-passes and the lit pass -- a cost probe, the
    // GI untouched (its scene is cooked); the picture is missing them.
    private static readonly bool SkipMaskDraws = Environment.GetEnvironmentVariable("BLIX_SKIP_MASK") == "1";

    // Record one parity pass. The unrecorded target keeps the previous frame and becomes history.
    private void RecordTaaResolve()
    {
        // --metalfx: the temporal scaler in its place (SponzaLoop.MetalFx.cs); the TAA history then goes stale.
        if (RecordMetalFx()) { taaHistoryValid = false; return; }
        if (render.Taa <= 0f) { taaHistoryValid = false; return; }
        Matrix4x4.Invert(viewProjJittered, out var invJittered);
        var uniforms = new ShaderUniform[]
        {
            new("uInvViewProjJittered", new Matrix4x4Uniform(invJittered)),
            new("uPrevViewProj",        new Matrix4x4Uniform(taaHistoryValid ? prevTaaViewProj : viewProj)),
            new("uParams",              new Vector4Uniform(new Vector4(
                render.Taa, taaHistoryValid ? 1f : 0f, render.ShowTaaRejection ? 1f : 0f, taaCountRejection ? 1f : 0f))),
            new("uViewProj",            new Matrix4x4Uniform(viewProj)),
            new("uAccumulate",          new Vector3Uniform(new Vector3(taaAccumulate, taaRelax, 0.25f))),
        };
        if (taaStats.Equals(default(GpuBufferHandle))) taaStats = Own(device.CreateGpuBuffer(8, name: "sponza.taa.stats"));
        var bindings = new List<ShaderTextureBinding>
        {
            new ShaderTextureBinding("uCurrent", graph.GetColorTexture(hdrHandle)),
            new ShaderTextureBinding("uHistory", graph.GetColorTexture(taaHandles[taaWrite ^ 1])),
            new ShaderTextureBinding("uDepth", graph.GetDepthTexture(SampleableSceneDepth)),
        };
        if (SurfaceTargets)
        {
            bindings.Add(new ShaderTextureBinding("uVelocity", graph.GetColorTexture(velocityHandle)));
            bindings.Add(new ShaderTextureBinding("uSurfaceKey", graph.GetColorTexture(surfaceKeyHandle)));
            bindings.Add(new ShaderTextureBinding("uSurfaceKeyPrev", graph.GetColorTexture(surfaceKeyHistoryHandle)));
        }
        graph.Pass(taaPassHandles[taaWrite], scope => fullscreen.Draw(
            scope, taaPipelines[taaWrite], bindings, pushConstants: null, uniforms: uniforms,
            buffers: new[] { new ShaderBufferBinding("TaaStats", taaStats) }));
        // The UNJITTERED matrix, because the resolved image this frame writes is what the next frame
        // reprojects into — and that image is aligned to the un-jittered grid by construction.
        prevTaaViewProj = viewProj;
        taaHistoryValid = true;
        // Flip AFTER recording, so present binds the target this frame wrote and the next frame
        // reads it as history.
        taaWriteNext = taaWrite ^ 1;
    }

    private void RecordPresentPass(RenderCommandList commandList)
    {
        // Present samples whatever was resolved this frame, or the raw HDR when TAA is off.
        var hdrTex = metalFxWritten ? graph.GetColorTexture(metalFxOutHandle)
            : render.Taa > 0f && taaHistoryValid
            ? graph.GetColorTexture(taaHandles[taaWrite])
            : graph.GetColorTexture(hdrHandle);
        var push = new byte[8];
        var exposure = render.Exposure;   // local: MemoryMarshal.Write needs an `in` ref
        MemoryMarshal.Write(push.AsSpan(0, 4), in exposure);
        var tonemap = (uint)render.Tonemap;
        MemoryMarshal.Write(push.AsSpan(4, 4), in tonemap);
        commandList.Pass(
            "present",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0, 0, 0, 1) },
                ClearDepth: true),
            pass => fullscreen.Draw(
                pass, presentPipeline,
                new[] { new ShaderTextureBinding("uHdr", hdrTex) },
                push));
    }

    // --- input -------------------------------------------------------------
}
