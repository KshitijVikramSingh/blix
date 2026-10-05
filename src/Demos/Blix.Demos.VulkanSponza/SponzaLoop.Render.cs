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

    // Fill an indirect buffer for one pass: per drawable, lodSlots VkDrawIndexedIndirectCommands, one per
    // LOD level, each instanced over the placements of it this pass sees at that level. The culling and
    // the level choice are exactly the old per-drawable ones, now per placement: each placement is tested
    // with its own world bounds and keeps its own LOD state. Its transform row goes into visibleScratch
    // from visibleBase on, grouped by drawable and level; a command's firstInstance is where its run
    // starts, which is what gl_InstanceIndex (instances.glsl) indexes. Levels nothing uses get
    // instanceCount 0 (a GPU no-op, so group offsets stay fixed). Returns the visible placement count.
    private int FillIndirect(
        List<Drawable> drawables, List<Placement> placements, float[] lodMargins, int[] lodState,
        IndirectBufferHandle buffer, int visibleBase,
        Frustum? cull, float margin, float worldErrorBudget = 0f,
        Frustum? receivers = null, Vector3 shadowSweepDir = default)
    {
        // Failed or empty pack loads create no indirect buffer; preserve their useful diagnostics
        // instead of following them with an unrelated invalid-handle failure.
        if (drawables.Count == 0) return 0;

        var cmds = MemoryMarshal.Cast<byte, uint>(indirectScratch.AsSpan());
        var visible = 0;
        // Count submitted triangles separately from pass time so fill resolution and caster LOD can
        // be attributed independently.
        fillIndirectTriangles = 0;
        // --ab lod reaches every camera and cascade fill through this shared selector. A zero budget
        // is PickLod's normal full-detail path.
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
                // Cull shadow casters by their conservative sun-swept bounds, not their unswept object
                // bounds. An off-volume caster remains relevant when its shadow can reach the cascade or
                // a visible receiver.
                var testBounds = bounds;
                if (shadowSweepDir != Vector3.Zero)
                {
                    // Sweep until the caster's shadow exits scene bounds. Cascade depth is a camera
                    // quantity and can be shorter than the light path to a receiver inside that cascade.
                    var sweep = shadowSweepDir * SceneExitDistance(bounds, shadowSweepDir);
                    testBounds = new Bounds3(
                        Vector3.Min(bounds.Min, bounds.Min + sweep),
                        Vector3.Max(bounds.Max, bounds.Max + sweep));
                }
                var vis = cull is not { } f || f.Intersects(testBounds, margin);
                // A caster also needs a swept intersection with visible receivers; otherwise its shadow
                // cannot contribute to the frame even if the light volume contains it.
                if (vis && receivers is { } rf) vis = rf.Intersects(testBounds, margin);

                // Per-placement LOD margin (live-tunable) scales the global px budget.
                // A world budget means this list is being drawn into something orthographic, where
                // camera pixels are not the unit of error. Nothing else about the fill changes.
                var lod = worldErrorBudget > 0f
                    ? d.PickLodWorld(worldErrorBudget * lodMargins[i], lodState[i])
                    : d.PickLod(bounds, cameraPosition, LodErrorScale, errorPixels * lodMargins[i], lodState[i]);
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
                visibleScratch[fill[levelScratch[k]]++] = (uint)placements[d.PlacementStart + k].Transform;
            }

            for (var l = 0; l < lodSlots; l++)
            {
                var has = l < d.LodIndexCounts.Length;
                var o = (di * lodSlots + l) * 5;
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
        device.WriteIndirectCommands(buffer, indirectScratch.AsSpan(0, drawables.Count * lodSlots * IndirectDraw.RecordStride));
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

    // A group's commands: its drawables' lodSlots each.
    private int GroupByteOffset(OpaqueGroup g) => g.Start * lodSlots * IndirectDraw.RecordStride;
    private int GroupDrawCount(OpaqueGroup g) => g.Count * lodSlots;

    // --ab lod reaches every camera and cascade selection through this. A zero budget is full detail.
    private float LodErrorPixelsNow => abMode == "lod" ? LodArmPixels : render.LodErrorPixels;

    // The lists a frame culls, each its own indirect records and run of the visible list: the camera's
    // over the opaque placements (shared by the depth pre-pass and the lit pass), one per cascade over
    // the same placements, the camera's late list (what occlusion finds the early one missed) and its final one
    // (early and late together, which the lit pass draws; both GPU only),
    // and the camera's over the blend ones. Every list before the blend one spans the opaque bucket.
    private const int SceneListCamera = 0;
    private static int SceneListCascade(int cascade) => 1 + cascade;
    private const int SceneListCameraLate = 1 + CascadeCount;
    // Early and late together: what the lit pass draws when occlusion is on.
    private const int SceneListCameraFinal = 2 + CascadeCount;
    private const int SceneListBlend = 3 + CascadeCount;
    private int SceneListRecordBase(int list) => opaqueDrawables.Count * lodSlots * list;
    private int SceneListVisibleBase(int list) => opaquePlacements.Count * list;

    // The CPU path's buffers and state per list; it has no late list.
    private IndirectBufferHandle SceneListIndirect(int list) =>
        list == SceneListCamera ? opaqueIndirect : list == SceneListBlend ? blendIndirect
        : list < SceneListCameraLate ? cascadeIndirect[list - 1]
        : throw new InvalidOperationException("the CPU cull has no late list");

    private int[] SceneListLodState(int list) =>
        list == SceneListCamera ? opaqueLodState : list == SceneListBlend ? blendLodState
        : list < SceneListCameraLate ? cascadeLodState[list - 1]
        : throw new InvalidOperationException("the CPU cull has no late list");

    // Cull one list and choose its levels: on the CPU into its indirect buffer (FillIndirect, returning the
    // visible count), or on the GPU as scene_cull.comp's three dispatches into the scene-cull pass
    // (returning -1: the count stays on the GPU). Either way the draws then read it via DrawSceneGroup.
    // cullViewProj and receiversViewProj are row-vector view-projections (clip = world · M), as the
    // camera and cascade matrices are kept.
    // occlusionEarly: this is the camera's early list, so it draws only what the occlusion test found
    // visible last frame (the late list, RecordOcclusion, catches the rest).
    private int CullList(
        int list, Matrix4x4? cullViewProj, float margin, float worldErrorBudget = 0f,
        Matrix4x4? receiversViewProj = null, Vector3 shadowSweepDir = default, bool occlusionEarly = false)
    {
        var blend = list == SceneListBlend;
        var drawables = blend ? blendDrawables : opaqueDrawables;
        var placements = blend ? blendPlacements : opaquePlacements;
        if (drawables.Count == 0) return 0;
        if (!gpuCull)
        {
            return FillIndirect(drawables, placements, blend ? blendLodMargins : opaqueLodMargins,
                SceneListLodState(list), SceneListIndirect(list), SceneListVisibleBase(list),
                cullViewProj is { } c ? Frustum.FromViewProjection(Matrix4x4.Transpose(c)) : null, margin, worldErrorBudget,
                receiversViewProj is { } r ? Frustum.FromViewProjection(Matrix4x4.Transpose(r)) : null, shadowSweepDir);
        }

        var firstPlacement = blend ? opaquePlacements.Count : 0;
        var records = drawables.Count * lodSlots;
        // The edit belongs to this list's bucket only when its row is in this list's range.
        var edit = pendingMarginEdit.Placement - 1;
        var editsHere = edit >= firstPlacement && edit < firstPlacement + placements.Count;
        if (editsHere) marginEditSent = true;
        DispatchCommand Phase(int mode, int items) => CullPhase(cullPipeline, mode, items,
            cullViewProj, margin, worldErrorBudget, receiversViewProj, shadowSweepDir,
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
        Matrix4x4? cullViewProj, float margin, float worldErrorBudget, Matrix4x4? receiversViewProj, Vector3 shadowSweepDir,
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
                new("uReceivers", new Matrix4x4Uniform(receiversViewProj ?? Matrix4x4.Identity)),
                new("uSweep", new Vector4Uniform(new Vector4(shadowSweepDir, shadowSweepDir == Vector3.Zero ? 0f : 1f))),
                new("uVolumeMin", new Vector4Uniform(new Vector4(skyVolumeMin, 0f))),
                new("uVolumeMax", new Vector4Uniform(new Vector4(skyVolumeMin + skyVolumeSpan, 0f))),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, LodErrorScale))),
                new("uLod", new Vector4Uniform(new Vector4(LodErrorPixelsNow, worldErrorBudget, LodHysteresis, margin))),
                new("uRange", new Vector4Uniform(new Vector4(firstPlacement, placementCount, firstDrawable, visibleBase))),
                new("uArgs", new Vector4Uniform(new Vector4(recordBase, records, lodSlots, mode))),
                new("uFlags", new Vector4Uniform(new Vector4(
                    cullViewProj is null ? 0f : 1f, receiversViewProj is null ? 0f : 1f, edit.Placement, edit.Margin))),
                new("uView", new Matrix4x4Uniform(cameraView)),
                new("uOcclusion", new Vector4Uniform(occlusion)),
            };
            return new DispatchCommand(pipeline, Math.Max(1, groups), 1, 1, uniforms,
                textures ?? Array.Empty<ShaderTextureBinding>(), Buffers: cullBuffers);
    }

    // The late half of two-phase occlusion: reduce the early pre-pass's depth into its own pyramid, test
    // every opaque placement against it (scene_occlusion.comp), and draw what is visible but was not drawn
    // into the same depth and normals. The lit pass then draws the final list, early and late together.
    private void RecordOcclusion(Matrix4x4? cameraCull, IReadOnlyList<ShaderUniform> perFrame, int frameWidth, int frameHeight)
    {
        RecordPyramid(occZPassHandles, occZPipelines, occZHandles, frameWidth, frameHeight, skip: false);

        var records = opaqueDrawables.Count * lodSlots;
        var pyramid = new ShaderTextureBinding[HiZLevels];
        for (var level = 0; level < HiZLevels; level++)
        {
            pyramid[level] = new ShaderTextureBinding($"uOccZ{level}", graph.GetColorTexture(occZHandles[level]));
        }
        DispatchCommand Phase(int mode, int items, int list, int visibleList) => CullPhase(occlusionPipeline, mode, items,
            cameraCull, CameraCullMargin, 0f, null, Vector3.Zero,
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
        MaterialHandle? material, byte[]? pushConstants = null)
    {
        var ib = g.IsU32 ? sharedIbU32 : sharedIbU16;
        if (gpuCull)
        {
            scope.DrawIndexedIndirect(sharedVb, ib, pipeline, sceneArgs,
                (SceneListRecordBase(list) + g.Start * lodSlots) * IndirectDraw.RecordStride, GroupDrawCount(g),
                uniforms, textures, material, pushConstants, sceneBuffers);
            return;
        }
        scope.DrawIndexedIndirect(sharedVb, ib, pipeline, SceneListIndirect(list), GroupByteOffset(g), GroupDrawCount(g),
            uniforms, textures, material, pushConstants, perDrawMaterial: sceneInstances!.Handle);
    }

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
        // Match-swapchain graph targets are reallocated rather than resized in place. Their public
        // handles stay stable, but their contents do not. GTAO reads ambientDenoisedHandle as the
        // preceding frame and TAA reads the other parity target, so both must refuse history once
        // after ANY recreation — not only a logical-window resize. Vsync changes and framebuffer-
        // only resizes recreate the same targets without calling this application's OnResize.
        if (graphResourceGeneration != graph.MatchSwapchainResourceGeneration)
        {
            graphResourceGeneration = graph.MatchSwapchainResourceGeneration;
            ambientHistoryValid = false;
            taaHistoryValid = false;
            Console.WriteLine("[VulkanSponza] graph resources recreated; GTAO/TAA history reset");
        }

        // Advance the global frame before every early return so loading measurements and A/B phase
        // transitions cannot stall on the branch they activate.
        framesRendered++;
        // Post-load frames are the reproducible clock for orbit, capture, and temporal sequences;
        // global frames still account for loading and A/B scheduling.
        if (fullyLoaded) postLoadFrames++;
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
        if (orbit) ApplyOrbit();
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

        gpuPasses.Sample();

        // Built once, before the cascades, because they need it too: a caster is culled against
        // where its shadow could FALL, and that is the camera's frustum rather than the light's.
        cameraFrustumThisFrame = Frustum.FromViewProjection(Matrix4x4.Transpose(viewProj));

        // Apply Halton (2,3) jitter as a clip-space translation: post-multiplication adds offset*w
        // to clip.xy without changing the unjittered projection used by spatial decisions.
        if (render.Taa > 0f && frame.Width > 0 && frame.Height > 0)
        {
            // Use the post-load clock so streaming duration cannot change a capture's jitter phase.
            var jx = (Halton(postLoadFrames, 2) - 0.5f) * 2f / frame.Width;
            var jy = (Halton(postLoadFrames, 3) - 0.5f) * 2f / frame.Height;
            viewProjJittered = viewProj * Matrix4x4.CreateTranslation(jx, jy, 0f);
        }
        else
        {
            viewProjJittered = viewProj;
        }

        // Stress hook: flip fog every 90 frames to exercise the on/off barrier
        // transitions under validation (no effect without --fog-stress).
        if (fogStress && (++fogStressFrame % 90 == 0)) fog.Enabled = !fog.Enabled;

        var perFrameList = new List<ShaderUniform>
        {
            // Only raster shading receives jitter. Cascades, culling, and LOD use the stable matrix
            // so a sub-pixel sample shift cannot perturb spatial policy or caches.
            new("uViewProjection",   new Matrix4x4Uniform(viewProjJittered)),
            new("uSunDirection",     new Vector3Uniform(sunDirection)),
            new("uSunIrradiance",    new Vector3Uniform(EffectiveSunIrradiance)),
            new("uCameraPos",        new Vector3Uniform(cameraPosition)),
            new("uEnvMipCount",      new FloatUniform(iblPrefilterMips)),
            new("uSheenMipCount",    new FloatUniform(sheenMipCount)),
            new("uMsaaSamples",      new FloatUniform(MsaaSamples)),
            new("uSkyDims",          new Vector4Uniform(new Vector4(probeX, probeY, probeZ, 0f))),
            // w marks whether shading should write usage at all — off while the volume is not ready.
            new("uBounceDims", new Vector4Uniform(new Vector4(
                bounceX, bounceY, bounceZ, bounceReady && ProbeSleepNow > 0f ? 1f : 0f))),
            new("uShadowStrength",   new FloatUniform(
                shadows.Enabled && !(abMode == "shadow" && AbOffPhase) ? 1f : 0f)),
            new("uCascadeViewProj",  new Matrix4x4ArrayUniform(cascadeViewProj)),
            // uCameraForward and uCascadeSplits are gone from the lit pass with the view-depth
            // cascade pick they existed for. The froxel dispatch below still takes the splits — it
            // marches the grid in view depth and genuinely needs them.
            new("uCascadeTexels",    new Vector4Uniform(
                new Vector4(cascadeTexelWorld[0], cascadeTexelWorld[1], cascadeTexelWorld[2], 0f))),
            new("uFog",              new Vector4Uniform(
                new Vector4(frame.Width, frame.Height, fog.Far, fog.Enabled ? 1f : 0f))),
            new("uVizChannel",       new FloatUniform(vizChannel)),
            // w: sky visibility is available, baked or (through the incident field) from the clipmap.
            new("uSkyMin",           new Vector4Uniform(new Vector4(
                skyVolumeMin, (skyVolumeLoaded || (ClipmapActive && incidentField)) && skyVisibilityEnabled && !skipSkySample ? 1f : 0f))),
            // w: how far along the normal the probe lookup is pushed. About one cell, so a surface
            // asks the cell in FRONT of it rather than the one it is embedded in.
            new("uSkyScale",         new Vector4Uniform(new Vector4(skyVolumeInvSpan, 0.6f))),
            new("uBounceStrength",   new FloatUniform((bounceReady || ClipmapActive) && skyVisibilityEnabled && !skipSkySample ? 1f : 0f)),
            // w gates the leak metric's march: 0 means no occupancy grid shipped and channel 21
            // has nothing to be the truth about.
            new("uOccupancyDims",    new Vector4Uniform(new Vector4(occX, occY, occZ, occX > 0 ? 1f : 0f))),
            // z gates the read. Off in the --ab off phase alongside the passes that fill it, so the
            // arm prices the whole substitution rather than half of it.
            new("uIncident",         new Vector4Uniform(new Vector4(
                Math.Max(1, (int)(frame.Width * incidentScale)),
                Math.Max(1, (int)(frame.Height * incidentScale)),
                incidentField && !(abMode == "incident" && AbOffPhase) ? 1f : 0f,
                // w: the incident field carries the sky's diffuse light itself (the probe clipmap), so the lit pass
                // adds none of its own.
                ClipmapActive && incidentField && !(abMode == "incident" && AbOffPhase) ? 1f : 0f))),
            // One component per --ab shading mode, live only during that mode's off-phase.
            new("uAbFlags",          new Vector4Uniform(new Vector4(
                AbOffPhase && abMode == "textures" ? 1f : 0f,
                AbOffPhase && abMode == "pbr"      ? 1f : 0f,
                AbOffPhase && abMode == "ibl"      ? 1f : 0f,
                AbOffPhase && abMode == "normal"   ? 1f : 0f))),
            new("uAbFlags2",         new Vector4Uniform(new Vector4(
                AbOffPhase && abMode == "indirect" ? 1f : 0f,
                //   y  drop the occupancy line-of-sight test back to Chebyshev alone, so the
                //      march's cost can be priced against the leak it removes.
                AbOffPhase && abMode == "occlusion" ? 1f : 0f, 0f, 0f))),
        };
        // The remaining //@tune uniforms (shadow slope scale, uVisualizeCascades) are appended by
        // name from the overlay panel — reflection lands each at its offset. The intensity and
        // material knobs are gone: sun and sky are measured from one capture and exposure is the
        // artistic control, so there is nothing left to balance by eye.
        tunePanel.AppendUniforms(perFrameList);
        var perFrame = perFrameList.ToArray();

        // Per-pass set-1 bindings (passBindings) and the identity model push
        // are built once at load (constant handles / identity transform) and
        // reused here — see OnLoad.

        // --- Streamed-load preview: flat geometry while textures upload ------
        // Geometry is built but its textures are still streaming into their
        // (allocated, undefined) handles, so we render the opaque set flat
        // (lit.vert + flat.frag, no material/IBL/shadow sampling) over the
        // pre-pass depth. No shadow cascades or fog yet — those start once
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

        // --- Shadow passes: opaque/mask occluders into each cascade ---------
        // Each cascade frustum-culls the opaque set against its ortho box, so
        // the near cascade only redraws nearby geometry instead of the whole
        // scene ×3. Blend drawables (windows) are skipped — translucent
        // surfaces shouldn't cast solid shadows. Casters route by alpha mode:
        // OPAQUE → push-only pipeline (no descriptor set); MASK → alpha-cutout
        // pipeline binding the albedo, so foliage casts leaf-shaped shadows.
        var cull = cullEnabled;
        var margin = cullMargin;
        // Reset the mask-push pool cursor; the cascade scopes rent monotonically
        // during graph.Execute, so each draw this frame gets a distinct buffer.
        maskPushCursor = 0;
        for (var c = 0; c < CascadeCount; c++)
        {
            var ci = c;
            // Shadows off: skip the pass (the lit shader early-outs without
            // sampling) and invalidate the cache so re-enabling forces a redraw.
            if (!shadows.Enabled)
            {
                cachedCascadeViewProj[ci] = default;
                cascadeRendered[ci] = false;
                continue;
            }
            var vp = cascadeViewProj[ci];
            // Cache validity includes the rendered light matrix, caster population, cascade LOD
            // budget, and—when receiver culling is active—the camera matrix. These are all inputs to
            // the stored depth image, including during asynchronous scene loading.
            var lodKey = abMode == "lod" && LodArmPixels <= 0f ? 0f : cascadeTexelWorld[ci] * shadowLodTexels;
            // Not due this frame: the map from an earlier frame still covers, and the lit pass is
            // already being handed the matrix it was rendered with.
            if (!cascadeDue[ci])
            {
                cascadeRendered[ci] = false;
                continue;
            }
            if (vp == cachedCascadeViewProj[ci]
                && opaquePlacements.Count == cachedCascadeCasters[ci]
                && lodKey == cachedCascadeLod[ci]
                && (!shadowCasterCull || viewProj == cachedCascadeCamera[ci]))
            {
                cascadeRendered[ci] = false;
                continue;
            }
            cachedCascadeViewProj[ci] = vp;
            cachedCascadeCasters[ci] = opaquePlacements.Count;
            cachedCascadeLod[ci] = lodKey;
            cachedCascadeCamera[ci] = viewProj;
            cascadeRendered[ci] = true;
            // Frustum.FromViewProjection expects a column-vector clip matrix
            // (clip = M·world); our cascade VP is the System.Numerics
            // row-vector form (clip = Vector4.Transform(world, M)), so transpose
            // to hand it the clip-coordinate generators as rows.
            // Cull this cascade's list (per-cascade frustum cull → 0 instanceCount; same SSE LOD
            // as the lit/pre-pass so shadow depth matches the shaded silhouette). Then one
            // indirect draw per group.
            cascadeTriangles[ci] = 0;
            cascadeDrawCounts[ci] = CullList(
                SceneListCascade(ci), cull ? vp : null, margin,
                // Shadow LOD is bounded by this cascade's world texel, not camera pixels. The full-
                // detail A/B arm explicitly overrides it so “no LOD” means every pass.
                abMode == "lod" && LodArmPixels <= 0f ? 0f : cascadeTexelWorld[ci] * shadowLodTexels,
                // Receiver culling tests the scene-bounded sun sweep against the camera frustum.
                shadowCasterCull && !(abMode == "castercull" && AbOffPhase) ? viewProj : null,
                Vector3.Normalize(sunDirection));
            cascadeTriangles[ci] = gpuCull ? -1 : fillIndirectTriangles;
            // Opaque casters all push the same bytes (this cascade's VP); mask casters push
            // per-material alpha params, so one mask push per group (constant within a material).
            // Where each placement stands is set 3's.
            var cascadeOpaquePush = ShadowOpaquePushBytes(vp);
            var cascadeList = SceneListCascade(ci);
            graph.Pass(cascadePassHandles[ci], scope =>
            {
                foreach (var g in opaqueGroups)
                {
                    if (g.IsMask)
                    {
                        var rep = opaqueDrawables[g.Start];
                        DrawSceneGroup(scope, cascadeList, g, shadowMaskPipeline,
                            Array.Empty<ShaderUniform>(), rep.ShadowAlbedoBinding, material: null,
                            pushConstants: RentMaskPush(vp, rep.AlphaCutoff, rep.BaseColorAlpha));
                    }
                    else
                    {
                        DrawSceneGroup(scope, cascadeList, g, shadowOpaquePipeline,
                            Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(), material: null,
                            pushConstants: cascadeOpaquePush);
                    }
                }
            });
        }

        // Froxel fog: fill the 3D scattering grid. Recorded here — after the
        // shadow scopes, before the lit scope — so this code reads in frame
        // order. (Execution is by graph declaration order regardless: the
        // froxel ComputePass is declared between the cascades and the lit pass,
        // so it dispatches after the shadow maps render and before the lit pass
        // samples the grid.) Skipped when fog is off — and the lit shader gates
        // on uFog.w, so when disabled the grid is never sampled (its contents
        // are undefined/stale; "disabled" must mean "never sample").
        if (fog.Enabled)
        {
            EnsureFroxelGrid(frame.Width, frame.Height);
            Matrix4x4.Invert(viewProj, out var invViewProj);
            var froxelUniforms = new ShaderUniform[]
            {
                new("uInvViewProj",   new Matrix4x4Uniform(invViewProj)),
                new("uCamPos",        new Vector4Uniform(new Vector4(cameraPosition, fog.Far))),
                new("uCamForward",    new Vector4Uniform(new Vector4(cameraForward, fog.Density))),
                // The froxel pass scatters the same measured RGB irradiance as the surface pass so
                // warm direct light remains warm in the medium.
                new("uSunDir",        new Vector4Uniform(new Vector4(sunDirection, 1f))),
                new("uSunColor",      new Vector4Uniform(new Vector4(EffectiveSunIrradiance, fog.Scatter))),
                // z: whether the indirect fields are there to be scattered. Without them the
                // medium falls back to the flat ambient floor, which is all it ever had.
                new("uFogParams",     new Vector4Uniform(new Vector4(
                    fog.PhaseG, fog.Ambient, fogIndirect ? 1f : 0f, (float)time.Total))),
                new("uMedium",        new Vector4Uniform(new Vector4(
                    fog.HeightFalloff, fog.Noise, 0f, 0f))),
                // Refuse history until both targets have valid contents, and again after a resize
                // recreates the grid.
                new("uTemporal",      new Vector4Uniform(new Vector4(
                    fogHistoryValid ? fog.Temporal : 0f, FogJitter(), fog.ShowRejection ? 1f : 0f, 0f))),
                new("uPrevViewProj",  new Matrix4x4Uniform(fogHistoryValid ? prevFogViewProj : viewProj)),
                new("uPrevCamPos",    new Vector4Uniform(new Vector4(
                    fogHistoryValid ? prevFogCamPos : cameraPosition, 0f))),
                new("uBoundsMin",     new Vector4Uniform(new Vector4(skyVolumeMin, 0f))),
                new("uBoundsSpan",    new Vector4Uniform(new Vector4(skyVolumeSpan, 0f))),
                new("uProbeDims",     new Vector4Uniform(new Vector4(bounceX, bounceY, bounceZ, 0f))),
                // w = 0: the fog's probe blend keeps the Chebyshev-only visibility test while the
                // lit pass's occlusion dial is being measured. One pass at a time.
                new("uOccupancyDims", new Vector4Uniform(new Vector4(occX, occY, occZ, 0f))),
                // No split depths: fog and surfaces share fitted-volume containment so they select
                // the same cascade at the same world position.
                new("uCascadeVP",     new Matrix4x4ArrayUniform(cascadeViewProj)),
            };
            graph.Dispatch(froxelPassHandle, new DispatchCommand(
                froxelPipeline,
                (froxelGridX + 7) / 8, (froxelGridY + 7) / 8, 1,
                froxelUniforms, FroxelBindings()));
            prevFogViewProj = viewProj;
            prevFogCamPos = cameraPosition;
            fogScatterWrite ^= 1;
            fogHistoryValid = true;
        }

        // Fill camera commands once for both depth and lit passes. Frustum culling zeroes
        // instanceCount without compacting groups, saving vertex/binning work while preserving
        // indirect offsets. Recorded off-screen geometry was 55% at the default view and 96% on
        // the orbit after cook-time spatial splitting made bounds selective.
        Matrix4x4? cameraCull = cullEnabled && !(abMode == "cull" && AbOffPhase) ? viewProj : null;
        //
        // The margin is insurance, not tuning. A chunk whose bounds sit exactly on a frustum plane
        // can fall either way on floating-point noise, and at the screen edge that reads as geometry
        // blinking in and out as you turn. Half a metre of slack costs a fraction of a percent of
        // the rejections and removes the whole class.
        RecordRaySurfaceBake();
        RecordClipmap();
        RecordRayCheck();

        // Occlusion needs the pre-pass's depth; without one the early list is the whole frustum's.
        var skipPrepass = noPrepass || (abMode == "prepass" && AbOffPhase);
        var occlusionNow = occlusionCull && !skipPrepass;
        CullList(SceneListCamera, cameraCull, margin: CameraCullMargin, occlusionEarly: occlusionNow);
        cameraTriangles = gpuCull ? -1 : fillIndirectTriangles;
        // Accumulate exactly one contiguous orbit so triangle means cover the full closed path and
        // do not depend on streaming duration or which arc happened to be sampled.
        if (!orbit || triangleFrames < OrbitFrames)
        {
            for (var c = 0; c < CascadeCount; c++) cascadeTriangleSum[c] += cascadeTriangles[c];
            cameraTriangleSum += cameraTriangles;
            triangleFrames++;
        }
        if (blendDrawables.Count > 0)
        {
            CullList(SceneListBlend, cameraCull, margin: CameraCullMargin);
        }

        // Depth pre-pass: same non-blend set as the lit pass (no cull, so the
        // depth the lit pass loads covers exactly what it shades), depth only.
        // Per group: mask binds the material (set 2 albedo) for the alpha
        // discard + the mask pipeline; opaque needs only set 0 + model push.
        // --ab prepass off-phase: record the pass (it still clears depth) but draw nothing into it,
        // so the lit pass below establishes depth itself through the writing pipelines.
        graph.Pass(depthPrepassHandle, scope =>
        {
            if (skipPrepass) return;
            foreach (var g in opaqueGroups)
            {
                DrawSceneGroup(scope, SceneListCamera, g, g.IsMask ? prepassMaskPipeline : prepassOpaquePipeline,
                    perFrame, Array.Empty<ShaderTextureBinding>(), material: g.IsMask ? g.Material : null);
            }
        });
        if (occlusionNow) RecordOcclusion(cameraCull, perFrame, frame.Width, frame.Height);
        RecordRayView(frame.Width, frame.Height);

        // Flip before dispatching: the pass writes one texture while every reader — the lit pass,
        // and the pass's own multi-bounce feedback — takes the other, which is what keeps the
        // compute off the fragment stage's critical path.
        // --ab inject skips only the compute dispatch. The prior atlas remains valid, isolating
        // injection cost from the shading path that consumes the field.
        var skipInjectNow = skipInject || (abMode == "inject" && AbOffPhase);
        if (probePingPong && bounceReady && skyVisibilityEnabled && !skipInjectNow) bounceWrite ^= 1;
        if (bounceReady && skyBounceBinding >= 0)
        {
            passBindings[skyBounceBinding] = new ShaderTextureBinding(
                "uSkyBounce", bounceTextures[BounceRead]);
            passBindings[skyBounceBinding + 1] = new ShaderTextureBinding(
                "uSkyBounceDepth", bounceDepthTextures[BounceRead]);
        }

        // Sun bounce into the probe grid. Cheap enough to redo every frame at this probe count, and
        // redoing it is the point: the whole reason it is not baked is that it must follow the sun.
        if (bounceReady && skyVisibilityEnabled && !skipInjectNow)
        {
            var injectUniforms = new ShaderUniform[]
            {
                new("uBoundsMin",  new Vector4Uniform(new Vector4(skyVolumeMin, 0f))),
                new("uBoundsSpan", new Vector4Uniform(new Vector4(skyVolumeSpan, ambient.BounceStrength))),
                new("uProbeDims",  new Vector4Uniform(new Vector4(bounceX, bounceY, bounceZ, MathF.Round(injectRays)))),
                new("uOccupancyDims", new Vector4Uniform(new Vector4(occX, occY, occZ, injectDensity ? 1f : 0f))),
                // w carries translucency: how much of what a partial cell absorbs comes out the far
                // side wearing its colour. Zero on opaque cells in the shader, or walls would leak.
                new("uAlbedoDims", new Vector4Uniform(new Vector4(albX, albY, albZ, injectTransmissionScale))),
                new("uSunDirection",  new Vector4Uniform(new Vector4(sunDirection, 0f))),
                // w: whether the sky-visibility volume is loaded, so the injector knows whether its
                // sky SOURCE term can be evaluated at all.
                // w gates the injector's SKY source term. --no-sky-bounce zeroes it while leaving the
                // visibility volume loaded for the lit pass, so the field carries the sun's bounce
                // alone — which is the only thing the CPU reference can reproduce faithfully, since
                // the irradiance cube it would need lives on the GPU.
                new("uSunIrradiance", new Vector4Uniform(new Vector4(
                    EffectiveSunIrradiance, skyVolumeLoaded && !noSkyBounce ? 1f : 0f))),
                new("uSchedule", new Vector4Uniform(new Vector4(
                    postLoadFrames, MathF.Round(injectPeriod),
                    ProbeSleepNow > 0f ? 1f / ProbeSleepNow : 0f,
                    probePingPong ? 1f : 0f))),
                // Eight injection periods before anything is allowed to sleep — enough for every
                // probe to solve and for several rounds of multi-bounce to propagate through the
                // ones no camera ever looks at.
                new("uWarmup", new Vector4Uniform(new Vector4(injectPeriod * 8f, 0f, 0f, 0f))),
                new("uTransport", new Vector4Uniform(new Vector4(
                    injectFeedback, transportOcclusion, 0f, 0f))),
            };
            // Traced (--gi-trace, or the on-phase of --ab trace): the same probes and atlas, rays through the ray scene.
            var traced = InjectTracedNow;
            graph.Dispatch(injectPassHandle, new DispatchCommand(
                traced ? injectTracedPipeline : injectPipeline,
                // One workgroup per probe: its 64 threads are the rays shared across all 36
                // interior texels of that probe's tile.
                bounceX * bounceY * bounceZ, 1, 1,
                injectUniforms, BounceBindings(), Buffers: traced ? rayBlockBuffers : null));
        }

        // Which probes this camera needs, sampled from the previous frame's resolved depth because
        // the usage dispatch executes before this frame's pre-pass. Marks are read by the NEXT
        // frame's injection; see probe_usage.comp for why this is not done in lit.frag.
        if (bounceReady && skyVisibilityEnabled && ProbeSleepNow > 0f)
        {
            Matrix4x4.Invert(viewProj, out var invViewProj);
            var usageUniforms = new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                new("uBoundsMin",   new Vector4Uniform(new Vector4(skyVolumeMin, 0f))),
                new("uBoundsSpan",  new Vector4Uniform(new Vector4(skyVolumeSpan, 0f))),
                new("uProbeDims",   new Vector4Uniform(new Vector4(bounceX, bounceY, bounceZ, 0f))),
                new("uDepthSize",   new Vector4Uniform(new Vector4(
                    frame.Width, frame.Height, 1f / frame.Width, 1f / frame.Height))),
            };
            var usageBindings = new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uProbeUsage", probeUsageTexture),
            };
            // One invocation per 8x8 pixel block; the shader strides by 8 again inside.
            var groupsX = (frame.Width / 8 + 7) / 8;
            var groupsY = (frame.Height / 8 + 7) / 8;
            graph.Dispatch(probeUsagePassHandle, new DispatchCommand(
                probeUsagePipeline, groupsX, groupsY, 1, usageUniforms, usageBindings));
        }

        // Hi-Z pyramid: level 0 reduces the resolved depth, each level after reduces its parent.
        RecordPyramid(hiZPassHandles, hiZPipelines, hiZHandles, frame.Width, frame.Height, skip: abMode == "hiz" && AbOffPhase);

        // Ambient visibility. Reads the 1x depth the pre-pass just resolved, writes bent normal +
        // visibility for the lit pass. When disabled the pass still runs and clears to white-ish —
        // a stale buffer would be worse than a cleared one, and "disabled" must mean "the term is
        // one", not "the term is whatever was here last frame".
        {
            Matrix4x4.Invert(cameraProjection, out var invProjection);
            Matrix4x4.Invert(cameraView, out var invView);
            // The AO buffer is half the framebuffer, so every pixel quantity the search uses — the
            // texel size it steps by and the scale that turns metres into pixels — is in ITS pixels,
            // not the screen's. Getting this wrong does not fail loudly; it silently halves or
            // doubles the radius, which reads as "the AO looks a bit off" and nothing more.
            var aoWidth = Math.Max(1, (int)(frame.Width * aoScale));
            var aoHeight = Math.Max(1, (int)(frame.Height * aoScale));
            var projectionScale = aoHeight * 0.5f / MathF.Tan(fovYRadians * 0.5f);
            var gtaoUniforms = new ShaderUniform[]
            {
                new("uInvProjection", new Matrix4x4Uniform(invProjection)),
                new("uInvView",       new Matrix4x4Uniform(invView)),
                new("uTarget",        new Vector4Uniform(new Vector4(
                    aoWidth, aoHeight, 1f / aoWidth, 1f / aoHeight))),
                new("uParams",        new Vector4Uniform(new Vector4(
                    ambient.Enabled && !(abMode == "gtao" && AbOffPhase) ? ambient.RadiusMetres : 0f,
                    projectionScale, aoDebug, 0f))),
                // The view ray, in NDC units. The negated Y is Vulkan's flip, taken from the
                // projection rather than rediscovered in the shader — the same flip whose omission
                // in the slice basis made the whole floor read as fully occluded.
                new("uRay",           new Vector4Uniform(new Vector4(
                    MathF.Tan(fovYRadians * 0.5f) * aspect, -MathF.Tan(fovYRadians * 0.5f),
                    CameraFarPlane * 0.98f, 0f))),
                // History is refused on the first frame and whenever the target has just been
                // re-created, for the same reason the fog's is: blending into an uninitialised
                // buffer is blending into whatever the allocator left.
                new("uTemporal",      new Vector4Uniform(new Vector4(
                    ambientHistoryValid ? ambient.Temporal : 0f,
                    // A golden-ratio walk over the slice span, so consecutive frames measure
                    // azimuths that are as far apart as a low-discrepancy sequence can put them.
                    (float)((postLoadFrames * 0.6180339887) % 1.0) * MathF.PI,
                    ambient.ShowRejection ? 1f : 0f, 0f))),
                new("uPrevViewProj",  new Matrix4x4Uniform(ambientHistoryValid ? prevAmbientViewProj : viewProj)),
            };
            var hiZBindings = new ShaderTextureBinding[HiZLevels + 1];
            for (var level = 0; level < HiZLevels; level++)
            {
                hiZBindings[level] = new ShaderTextureBinding(
                    $"uHiZ[{level}]", graph.GetColorTexture(hiZHandles[level]));
            }
            hiZBindings[HiZLevels] = new ShaderTextureBinding(
                "uHistory", graph.GetColorTexture(ambientDenoisedHandle));
            graph.Pass(gtaoPassHandle, scope => fullscreen.Draw(
                scope, gtaoPipeline, hiZBindings, pushConstants: null, uniforms: gtaoUniforms));
            prevAmbientViewProj = viewProj;
            ambientHistoryValid = true;

            var denoiseUniforms = new ShaderUniform[]
            {
                new("uInvProjection", new Matrix4x4Uniform(invProjection)),
                // Half-res texel size: the taps must step across the AO buffer's grid, not the
                // screen's, or a 3x3 gathers four copies of the same texel.
                new("uTarget",        new Vector4Uniform(new Vector4(
                    aoWidth, aoHeight, 1f / aoWidth, 1f / aoHeight))),
            };
            graph.Pass(gtaoDenoisePassHandle, scope => fullscreen.Draw(
                scope, gtaoDenoisePipeline,
                new[]
                {
                    new ShaderTextureBinding("uAmbientRaw", graph.GetColorTexture(ambientHandle)),
                    new ShaderTextureBinding(
                        "uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                },
                pushConstants: null,
                uniforms: denoiseUniforms));
        }

        // The incident-light field. Recorded unconditionally so the graph's target is never stale,
        // but skipped in the --ab off phase so the arm prices the PASS as well as the lit pass's
        // saving — leaving it running in both arms would count the saving and not what pays for it.
        if (incidentField && !(abMode == "incident" && AbOffPhase))
        {
            Matrix4x4.Invert(cameraProjection, out var incidentInvProj);
            Matrix4x4.Invert(cameraView, out var incidentInvView);
            var incW = Math.Max(1, (int)(frame.Width * incidentScale));
            var incH = Math.Max(1, (int)(frame.Height * incidentScale));
            var incidentUniforms = new ShaderUniform[]
            {
                new("uInvProjection", new Matrix4x4Uniform(incidentInvProj)),
                new("uInvView",       new Matrix4x4Uniform(incidentInvView)),
                new("uTarget",        new Vector4Uniform(new Vector4(
                    incW, incH, 1f / incW, 1f / incH))),
                new("uSkyMin",        new Vector4Uniform(new Vector4(
                    skyVolumeMin, skyVolumeLoaded && skyVisibilityEnabled && !skipSkySample ? 1f : 0f))),
                new("uSkyScale",      new Vector4Uniform(new Vector4(skyVolumeInvSpan, 0.6f))),
                new("uBounceDims",    new Vector4Uniform(new Vector4(bounceX, bounceY, bounceZ, 0f))),
                new("uOccupancyDims", new Vector4Uniform(new Vector4(
                    // Share the lit pass's occupancy control so inline and incident reconstruction
                    // use the same visibility policy.
                    occX, occY, occZ, occX > 0 ? tunePanel.Value("uProbeOcclusion") : 0f))),
                new("uParams",        new Vector4Uniform(new Vector4(
                    bounceReady && skyVisibilityEnabled && !skipSkySample ? 1f : 0f,
                    tunePanel.Value("uProbeTetrahedral"), 0f, 0f))),
            };
            if (ClipmapActive)
            {
                RecordScreenProbes(frame.Width, frame.Height);
                RecordIncidentClipmap(incidentInvProj, incidentInvView, incW, incH, frame.Width, frame.Height);
            }
            else graph.Pass(incidentPassHandle, scope => fullscreen.Draw(
                scope, incidentPipeline,
                new[]
                {
                    new ShaderTextureBinding(
                        "uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                    new ShaderTextureBinding(
                        "uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                    new ShaderTextureBinding("uSkyBounce",
                        bounceReady ? bounceTextures[BounceRead] : brdfLutTexture),
                    new ShaderTextureBinding("uSkyBounceDepth",
                        bounceReady ? bounceDepthTextures[BounceRead] : brdfLutTexture),
                    new ShaderTextureBinding("uSkyVisibility",  skyVisibilityTextures[0]),
                    new ShaderTextureBinding("uSkyVisibility1", skyVisibilityTextures[1]),
                    new ShaderTextureBinding("uSkyVisibility2", skyVisibilityTextures[2]),
                    new ShaderTextureBinding("uOccupancy",
                        occX > 0 ? occupancyTexture : skyVisibilityTextures[0]),
                },
                pushConstants: null,
                uniforms: incidentUniforms));

            graph.Pass(incidentResolvePassHandle, scope => fullscreen.Draw(
                scope, incidentResolvePipeline,
                new[]
                {
                    new ShaderTextureBinding("uIncidentRaw", graph.GetColorTexture(incidentHandle)),
                    new ShaderTextureBinding(
                        "uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                    new ShaderTextureBinding(
                        "uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                },
                pushConstants: null,
                uniforms: new ShaderUniform[]
                {
                    new("uInvProjection", new Matrix4x4Uniform(incidentInvProj)),
                    new("uSource", new Vector4Uniform(new Vector4(
                        incW, incH, 1f / incW, 1f / incH))),
                }));
        }

        graph.Pass(litPassHandle, scope =>
        {
            // Opaque + Mask first (depth-test, no write — pre-pass wrote depth),
            // then Blend (depth-test only), so translucent surfaces composite
            // over the resolved opaque depth without writing into it.
            //
            // One indirect draw per (pipeline, material) group over the buffer
            // filled above — ~800 per-object draws collapse to ~one per material.
            // All draws in a group share set0 + set2 (material) + set 3 (the scene's placements),
            // the multidraw constraint.
            foreach (var g in opaqueGroups)
            {
                var pipeline = g.Pipeline;
                if (skipPrepass)
                {
                    if (pipeline == opaqueSolidPipeline) pipeline = opaqueSolidPipelineWrites;
                    else if (pipeline == opaqueDoubleSidedPipeline) pipeline = opaqueDoubleSidedPipelineWrites;
                }
                if (litFlat) pipeline = g.Pipeline == opaqueDoubleSidedPipeline ? flatPipeline : flatSolidPipeline;
                DrawSceneGroup(scope, occlusionNow ? SceneListCameraFinal : SceneListCamera, g, pipeline, perFrame, passBindings, g.Material);
            }
            // The probe view, before the sky so the sky can still fill where nothing was drawn, and
            // before blend so glass composites over it like any other geometry.
            if (showProbes && skyVolumeLoaded)
            {
                var probeUniforms = new ShaderUniform[]
                {
                    new("uViewProj",  new Matrix4x4Uniform(viewProj)),
                    new("uCameraPos", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                    new("uProbeMin",  new Vector4Uniform(new Vector4(skyVolumeMin, probeRadius))),
                    new("uProbeSpan", new Vector4Uniform(new Vector4(skyVolumeSpan, 0f))),
                    // Field 1 is sky visibility, which lives on the dense VISIBILITY grid; 0 and 2
                    // are the bounce and its usefulness, which live on the coarser bounce grid.
                    new("uProbeDims", new Vector4Uniform(probeField > 0.5f && probeField < 1.5f
                        ? new Vector4(probeX, probeY, probeZ, 0f)
                        : new Vector4(bounceX, bounceY, bounceZ, 0f))),

                    new("uProbeMode", new Vector4Uniform(new Vector4(probeField, probeExposure, 0f, 0f))),
                    new("uBounceDims", new Vector4Uniform(new Vector4(bounceX, bounceY, bounceZ, 0f))),
                };
                scope.DrawIndexedInstanced(
                    probeVb, probeIb, probePipeline,
                    indexCount: 6,
                    instanceCount: probeField > 0.5f && probeField < 1.5f
                        ? probeX * probeY * probeZ
                        : bounceX * bounceY * bounceZ,
                    // Its OWN bindings: the probe shader declares uSkyBounce/uSkyVisibility at set 1
                    // slots 0 and 1, where the lit pass's list puts them at 7 and 6. Handing over a
                    // list built for a different shader binds by slot, not by name.
                    uniforms: probeUniforms,
                    textures: new[]
                    {
                        new ShaderTextureBinding("uSkyBounce",
                            bounceReady ? bounceTextures[BounceRead] : brdfLutTexture),
                        new ShaderTextureBinding("uSkyVisibility", skyVisibilityTexture),
                        new ShaderTextureBinding("uOccupancy", occupancyTexture),
                        new ShaderTextureBinding("uSkyBounceDepth",
                            bounceReady ? bounceDepthTextures[BounceRead] : brdfLutTexture),
                    },
                    // null, not an empty array: an empty array still counts as "push constants supplied", and
                    // this shader declares no ranges.
                    perDrawMaterial: null, pushConstants: null!);
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
        RecordTaaResolve();
        UploadVisible();
        graph.Execute(commandList);

        RecordPresentPass(commandList);
        taaWrite = taaWriteNext;

        // Capture only a completed lit arm after at least one prior fully loaded frame has populated
        // readback targets. Deadlines use post-load frames and require a useful timing sample set.
        var measuredFrames = postLoadFrames;
        SampleStability();
        if (shotPath is { } path && !shotWritten && fullyLoaded && !AbOffPhase
            && framePeriodCount >= 60 && measuredFrames >= shotFrame)
        {
            shotWritten = true;
            if (shotResized)
            {
                BlixApps.ReportFailure("the window was resized during the shot, so its frame is not comparable; nothing was written");
                host.RequestClose();
                return;
            }
            VerifyHiZ();
            OcclusionCensus();
            ProbeReachCensus();
            WriteAmbientShot(path);
            // Preserve the raw GTAO result beside the denoised lighting input so horizon-search
            // output can be inspected without the 3x3 bilateral neighbourhood.
            WriteAmbientRaw(Path.ChangeExtension(path, null) + ".raw.png");
            WriteSceneShot(Path.ChangeExtension(path, null) + ".scene.png");
            WriteStability(Path.ChangeExtension(path, null));
            LeakCensus();
            WritePassBreakdown();
            WriteLodCensus();
            WriteClusterCensus();
            WriteRayBenchmark();
            WriteRayCheck();
            WriteClipmapCheck();
            WriteScreenProbeCheck();
            WriteRayView(Path.ChangeExtension(path, null));
            WriteProbeCensus();
            if (probeReference)
            {
                WriteProbeReference(probeCount: referenceProbes, paths: 4096, bounces: refBounces);
                WriteProbeReferenceTriangles(probeCount: referenceProbes, paths: 4096, bounces: refBounces);
                WriteSkyVisibilityReference(samples: 200, rays: 2048);
                WriteSkyConsistency();
                WriteSurfaceReference(grid: 24, paths: 1024, bounces: refBounces);
            }
            WriteFrameStats();
            host.RequestClose();
        }
    }

    /// <summary>
    /// How much of the probe blend, over everything the camera can see, arrives through a wall.
    /// </summary>
    /// <remarks>
    /// Channel 21 marches occupancy to each contributing probe and reports leaked blend weight.
    /// Mean and tail quantiles are both retained because localized high leakage is more visible than
    /// the same weight distributed across the frame. The expensive march runs only for this channel.
    /// </remarks>
    private void LeakCensus()
    {
        if (vizChannel < 20.5f || vizChannel > 21.5f) return;

        var pixels = device.ReadTexture(
            graph.GetColorTexture(hdrHandle), out var width, out var height, out var format);
        if (format != TextureFormat.R11G11B10F) return;

        var measured = new List<float>(width * height / 4);
        double sum = 0;
        double confidenceSum = 0;
        for (var i = 0; i < width * height; i++)
        {
            var packed = BitConverter.ToUInt32(pixels, i * 4);
            // Channel 21 marks measured surfaces with green >= 0.5 and exactly zero blue. Requiring
            // both excludes bright skybox pixels that never ran the leak instrument.
            var green = UnpackFloat((packed >> 11) & 0x7FF, 6);
            if (green < 0.49f) continue;
            if (UnpackFloat((packed >> 22) & 0x3FF, 5) > 1e-4f) continue;
            var leak = UnpackFloat(packed & 0x7FF, 6);
            measured.Add(leak);
            sum += leak;
            confidenceSum += Math.Clamp((green - 0.5f) * 2f, 0f, 1f);
        }

        if (measured.Count == 0)
        {
            Console.WriteLine("[VulkanSponza] leak census: no pixel read the probe volume.");
            return;
        }

        measured.Sort();
        float Quantile(double q) => measured[Math.Clamp((int)(q * measured.Count), 0, measured.Count - 1)];
        var over = (double)measured.Count(v => v > 0.25f) / measured.Count;
        Console.WriteLine(
            $"[VulkanSponza] leak census ({measured.Count:N0} probe-lit pixels of {width * height:N0}): " +
            $"mean {sum / measured.Count:P1}, median {Quantile(0.5):P1}, p95 {Quantile(0.95):P1}, " +
            $"p99 {Quantile(0.99):P1}, {over:P1} of pixels over 25%, " +
            $"mean surviving weight {confidenceSum / measured.Count:P1}");
    }

    /// <summary>The ambient buffer before the denoise: rgb as written, alpha as visibility.</summary>
    private void WriteAmbientRaw(string path)
    {
        var pixels = device.ReadTexture(
            graph.GetColorTexture(ambientHandle), out var width, out var height, out var format);
        if (format != TextureFormat.Rgba16F) return;

        var rgb = new byte[width * height * 4];
        var vis = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var src = i * 8;
            var dst = i * 4;
            for (var c = 0; c < 3; c++)
            {
                var v = (float)BitConverter.ToHalf(pixels, src + c * 2);
                rgb[dst + c] = (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
            }
            rgb[dst + 3] = 255;
            var a = (float)BitConverter.ToHalf(pixels, src + 6);
            vis[dst] = vis[dst + 1] = vis[dst + 2] = (byte)Math.Clamp((int)MathF.Round(a * 255f), 0, 255);
            vis[dst + 3] = 255;
        }
        PngWriter.WriteRgba8(path, rgb, width, height);
        var visPath = Path.ChangeExtension(path, null) + ".vis.png";
        PngWriter.WriteRgba8(visPath, vis, width, height);
        Console.WriteLine($"[VulkanSponza]   {path} + {Path.GetFileName(visPath)}  ({width}x{height}, pre-denoise)");
    }

    /// <summary>What the injector actually wrote into the reachability channel, per probe.</summary>
    /// <remarks>
    /// Reads GPU output directly and reports reachability by height, avoiding drift between a CPU
    /// model of the march and the shader implementation.
    /// </remarks>
    private void ProbeReachCensus()
    {
        if (!bounceReady) return;
        var pixels = device.ReadTexture(
            bounceDepthTextures[BounceRead], out var w, out var h, out var format);
        if (format != TextureFormat.Rgba16F) { Console.WriteLine("[VulkanSponza] probe reach: unexpected format"); return; }

        Console.WriteLine("[VulkanSponza] probe reachability, as written by the injector:");
        var liveByY = new int[bounceY];
        var totalByY = new int[bounceY];
        var live = 0;
        for (var z = 0; z < bounceZ; z++)
        for (var y = 0; y < bounceY; y++)
        for (var x = 0; x < bounceX; x++)
        {
            // The tile's first INTERIOR texel; the border ring is copied from the interior and the
            // flag is constant across the tile, so any interior texel answers for the probe.
            var tx = x * OctTile + 1;
            var ty = (y + z * bounceY) * OctTile + 1;
            var reach = (float)BitConverter.ToHalf(pixels, ((ty * w) + tx) * 8 + 4);   // .b
            totalByY[y]++;
            if (reach >= 0.5f) { liveByY[y]++; live++; }
        }
        var total = bounceX * bounceY * bounceZ;
        Console.WriteLine($"  live {live:N0} of {total:N0} ({100.0 * live / total:0.0}%), rejected {total - live:N0}");
        // Optional raw dump preserves mean and variance for visibility analysis against the exact
        // atlas the GPU produced, plus the paired irradiance atlas below.
        if (Environment.GetEnvironmentVariable("BLIX_DUMP_PROBE_DEPTH") is { Length: > 0 } dumpPath)
        {
            var floats = new float[w * h * 2];
            for (var i = 0; i < w * h; i++)
            {
                floats[i * 2]     = (float)BitConverter.ToHalf(pixels, i * 8);       // mean
                floats[i * 2 + 1] = (float)BitConverter.ToHalf(pixels, i * 8 + 2);   // variance
            }
            var bytes = new byte[16 + floats.Length * 4];
            BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), w);
            BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), h);
            BitConverter.TryWriteBytes(bytes.AsSpan(8, 4), bounceX);
            BitConverter.TryWriteBytes(bytes.AsSpan(12, 4), bounceY);
            Buffer.BlockCopy(floats, 0, bytes, 16, floats.Length * 4);
            File.WriteAllBytes(dumpPath, bytes);
            Console.WriteLine($"  dumped depth atlas {w}x{h} to {dumpPath}");

            // The irradiance atlas beside it, same layout: the two are only meaningful together,
            // because every question about the blend is "what weight, times what colour".
            var irr = device.ReadTexture(bounceTextures[BounceRead], out var iw, out var ih, out _);
            var ifl = new float[iw * ih * 3];
            for (var i = 0; i < iw * ih; i++)
            {
                ifl[i * 3]     = (float)BitConverter.ToHalf(irr, i * 8);
                ifl[i * 3 + 1] = (float)BitConverter.ToHalf(irr, i * 8 + 2);
                ifl[i * 3 + 2] = (float)BitConverter.ToHalf(irr, i * 8 + 4);
            }
            var ib = new byte[16 + ifl.Length * 4];
            BitConverter.TryWriteBytes(ib.AsSpan(0, 4), iw);
            BitConverter.TryWriteBytes(ib.AsSpan(4, 4), ih);
            BitConverter.TryWriteBytes(ib.AsSpan(8, 4), bounceX);
            BitConverter.TryWriteBytes(ib.AsSpan(12, 4), bounceY);
            Buffer.BlockCopy(ifl, 0, ib, 16, ifl.Length * 4);
            var irrPath = Path.ChangeExtension(dumpPath, null) + ".irr.bin";
            File.WriteAllBytes(irrPath, ib);
            Console.WriteLine($"  dumped irradiance atlas {iw}x{ih} to {irrPath}");
        }
        for (var y = 0; y < bounceY; y++)
        {
            var wy = skyVolumeMin.Y + (y + 0.5f) * skyVolumeSpan.Y / bounceY;
            Console.WriteLine($"    y={wy,7:0.00} m  live {liveByY[y],4}/{totalByY[y],-4} ({100.0 * liveByY[y] / totalByY[y]:0}%)");
        }
    }

    /// <summary>Measures how much on-screen submitted geometry is hidden behind other geometry.</summary>
    /// <remarks>
    /// This capture-time CPU query projects each drawable AABB, samples a suitable Hi-Z level, and
    /// compares nearest object depth with the farthest depth over its screen rectangle. It reports
    /// both drawable and triangle shares to estimate the value of future GPU-driven culling without
    /// first requiring storage-capable indirect buffers and graph buffer barriers.
    /// </remarks>
    private void OcclusionCensus()
    {
        if (ReadHiZ() is not { } hiZ) return;
        var occluded = 0;
        long occludedTris = 0, totalTris = 0;
        var offscreen = 0;

        foreach (var p in opaquePlacements)
        {
            var tris = opaqueDrawables[p.Drawable].LodIndexCounts[0] / 3;
            totalTris += tris;
            switch (hiZ.Test(p.Bounds))
            {
                case HiZVerdict.OffScreen: offscreen++; break;
                case HiZVerdict.Hidden: occluded++; occludedTris += tris; break;
            }
        }

        var shown = opaquePlacements.Count - offscreen;
        Console.WriteLine(
            $"[VulkanSponza] occlusion census (Hi-Z level {HiZCensusLevel}, {hiZ.Width}x{hiZ.Height}): " +
            $"{occluded}/{shown} on-screen placements fully hidden, " +
            $"{occludedTris / 1000.0:0.0}k of {totalTris / 1000.0:0.0}k triangles " +
            $"({(totalTris > 0 ? 100.0 * occludedTris / totalTris : 0):0.0}%), {offscreen} off-screen");
    }

    // A level whose texels are coarse enough that a handful covers a typical object.
    private const int HiZCensusLevel = 3;

    private enum HiZVerdict { OffScreen, Hidden, Visible }

    // This frame's Hi-Z level, read back: the farthest depth in each texel, and the camera it was drawn with.
    private sealed record HiZSnapshot(float[] Farthest, int Width, int Height, Matrix4x4 View, Matrix4x4 ViewProj, float Near)
    {
        /// <summary>Whether everything these bounds could draw is behind the depth already recorded where they land.</summary>
        /// <remarks>
        /// Project the eight corners; take the screen rectangle and the NEAREST view depth. If the farthest
        /// depth recorded anywhere in that rectangle is still nearer than the bounds' closest point, nothing
        /// inside them would survive the depth test. Bounds reaching the near plane are never hidden.
        /// </remarks>
        public HiZVerdict Test(in Bounds3 b)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            var nearest = float.MaxValue;
            var anyInFront = false;
            for (var c = 0; c < 8; c++)
            {
                var corner = new Vector3(
                    (c & 1) == 0 ? b.Min.X : b.Max.X,
                    (c & 2) == 0 ? b.Min.Y : b.Max.Y,
                    (c & 4) == 0 ? b.Min.Z : b.Max.Z);
                var depth = -Vector3.Transform(corner, View).Z;
                if (depth <= Near) { anyInFront = true; continue; }
                nearest = MathF.Min(nearest, depth);
                anyInFront = true;

                var clip = Vector4.Transform(new Vector4(corner, 1f), ViewProj);
                var ndcX = clip.X / clip.W;
                var ndcY = clip.Y / clip.W;
                minX = MathF.Min(minX, ndcX); maxX = MathF.Max(maxX, ndcX);
                minY = MathF.Min(minY, ndcY); maxY = MathF.Max(maxY, ndcY);
            }

            if (!anyInFront || nearest == float.MaxValue) return HiZVerdict.OffScreen;
            // Entirely outside the frustum sideways: the frustum cull's job, not this test's.
            if (maxX < -1f || minX > 1f || maxY < -1f || minY > 1f) return HiZVerdict.OffScreen;

            var x0 = Math.Clamp((int)MathF.Floor((minX * 0.5f + 0.5f) * Width), 0, Width - 1);
            var x1 = Math.Clamp((int)MathF.Ceiling((maxX * 0.5f + 0.5f) * Width), 0, Width - 1);
            var y0 = Math.Clamp((int)MathF.Floor((minY * 0.5f + 0.5f) * Height), 0, Height - 1);
            var y1 = Math.Clamp((int)MathF.Ceiling((maxY * 0.5f + 0.5f) * Height), 0, Height - 1);

            var deepest = 0f;
            for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                deepest = MathF.Max(deepest, Farthest[y * Width + x]);
            return nearest > deepest ? HiZVerdict.Hidden : HiZVerdict.Visible;
        }
    }

    private HiZSnapshot? ReadHiZ()
    {
        var pixels = device.ReadTexture(
            graph.GetColorTexture(hiZHandles[HiZCensusLevel]), out var w, out var h, out var format);
        if (format != TextureFormat.Rgba16F) return null;
        var farthest = new float[w * h];
        for (var i = 0; i < w * h; i++) farthest[i] = (float)BitConverter.ToHalf(pixels, i * 8 + 2);
        return new HiZSnapshot(farthest, w, h, cameraView, viewProj, CameraNearPlane);
    }

    /// <summary>Checks each pyramid level really is the min/max of the one above it.</summary>
    /// <remarks>
    /// Recomputes conservative parent bounds from the real GPU buffers. Occlusion safety requires
    /// minimums never to increase and maximums never to decrease; tolerance covers fp16 reduction.
    /// </remarks>
    private void VerifyHiZ()
    {
        Console.WriteLine("[VulkanSponza] Hi-Z pyramid:");
        float[]? parent = null;
        int parentW = 0, parentH = 0;
        for (var level = 0; level < HiZLevels; level++)
        {
            var pixels = device.ReadTexture(
                graph.GetColorTexture(hiZHandles[level]), out var w, out var h, out var format);
            if (format != TextureFormat.Rgba16F) { Console.WriteLine("  unexpected format"); return; }

            var lo = new float[w * h];
            var hi = new float[w * h];
            for (var i = 0; i < w * h; i++)
            {
                lo[i] = (float)BitConverter.ToHalf(pixels, i * 8);
                hi[i] = (float)BitConverter.ToHalf(pixels, i * 8 + 2);
            }

            var finite = lo.Where(v => v > 0 && !float.IsInfinity(v)).ToArray();
            var nearest = finite.Length > 0 ? finite.Min() : 0f;
            var farthest = hi.Where(v => !float.IsInfinity(v)).DefaultIfEmpty(0f).Max();

            var verdict = "";
            if (parent is not null)
            {
                // Odd dimensions may require overlapping source footprints. Test conservative
                // containment rather than exact 2x2 equality, which can skip the final row/column.
                var spanX = Math.Max(1, parentW / w);
                var spanY = Math.Max(1, parentH / h);
                double worstLo = 0, worstHi = 0;
                var tight = true;
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    float expectLo = float.MaxValue, expectHi = float.MinValue;
                    for (var sy = 0; sy < spanY; sy++)
                    for (var sx = 0; sx < spanX; sx++)
                    {
                        var px = Math.Min(parentW - 1, x * spanX + sx);
                        var py = Math.Min(parentH - 1, y * spanY + sy);
                        expectLo = MathF.Min(expectLo, parent[(py * parentW + px) * 2]);
                        expectHi = MathF.Max(expectHi, parent[(py * parentW + px) * 2 + 1]);
                    }
                    var scale = MathF.Max(1f, MathF.Abs(expectHi));
                    // Violation only when the level is LESS conservative than its parent's window.
                    worstLo = Math.Max(worstLo, (lo[y * w + x] - expectLo) / scale);
                    worstHi = Math.Max(worstHi, (expectHi - hi[y * w + x]) / scale);
                    if (lo[y * w + x] < expectLo - 1e-3f || hi[y * w + x] > expectHi + 1e-3f) tight = false;
                }
                var ok = worstLo < 0.01 && worstHi < 0.01;
                var fit = tight ? "tight" : "overlapping";
                verdict = ok
                    ? $"  conservative OK, {fit}"
                    : $"  NOT CONSERVATIVE (min over by {worstLo:0.000}, max under by {worstHi:0.000})";
            }

            Console.WriteLine(
                $"  level {level}: {w,5}x{h,-4} nearest {nearest,7:0.00} m  farthest {farthest,8:0.00} m{verdict}");

            parent = new float[w * h * 2];
            for (var i = 0; i < w * h; i++) { parent[i * 2] = lo[i]; parent[i * 2 + 1] = hi[i]; }
            parentW = w; parentH = h;
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
            "flat"   => "everything (materials+IBL+shadows+AO)",
            "shadow" => "sun shadows",
            "gtao"   => "ambient visibility (GTAO)",
            "prepass"=> "the depth pre-pass",
            "hiz"    => "the Hi-Z pyramid",
            "textures"=> "material texture bandwidth",
            "pbr"    => "the GGX specular lobe",
            "ibl"    => "image-based lighting",
            "normal" => "normal mapping",
            "indirect"=> "the probe-volume terms (bounce + baked sky visibility)",
            "inject"  => "the bounce injection dispatch",
            "trace"   => "probe rays traced through the ray scene rather than marched through the occupancy grid",
            "cull"    => "camera frustum culling",
            "castercull" => "shadow caster culling against the camera",
            "sleep"   => "probes sleeping when nothing samples them",
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
                // State camera motion explicitly because cascade scheduling and LOD behavior differ
                // materially between a still view and the measurement orbit.
                (orbit ? "camera on the measurement orbit" : "CAMERA STILL (cascades cached)"));
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

    /// <summary>The injection pass's textures for this frame: write one, read the other.</summary>
    // The grid follows the framebuffer, so a resize re-creates it. It is a descriptor in two live
    // sets and cannot be swapped under work in flight — but a resize already stalls the pipeline,
    // which makes this the cheapest correct place to pay for the idle. Texture ids are never
    // reused, so the destroyed handle cannot come back and match something cached.
    private void EnsureFroxelGrid(int width, int height)
    {
        var (x, y) = FroxelGridSize(width, height);
        if (x == froxelGridX && y == froxelGridY) return;
        device.WaitIdle();
        var previous = froxelGridTexture;
        froxelGridX = x;
        froxelGridY = y;
        froxelGridTexture = device.CreateStorageTexture3D(
            x, y, froxelGridZ, TextureFormat.Rgba16F, SamplerDescription.LinearClamp,
            "sponza.froxel_grid");
        device.DestroyTexture(previous);
        for (var i = 0; i < 2; i++)
        {
            var staleScatter = fogScatterTextures[i];
            fogScatterTextures[i] = device.CreateStorageTexture3D(
                x, y, froxelGridZ, TextureFormat.Rgba16F, SamplerDescription.LinearClamp,
                $"sponza.fog_scatter{i}");
            device.DestroyTexture(staleScatter);
        }
        fogHistoryValid = false;
        if (froxelGridBinding >= 0)
        {
            passBindings[froxelGridBinding] =
                new ShaderTextureBinding("uFroxelGrid", froxelGridTexture);
        }
        if (skyFroxelGridBinding >= 0)
        {
            skyBindings[skyFroxelGridBinding] =
                new ShaderTextureBinding("uFroxelGrid", froxelGridTexture);
        }
        Console.WriteLine(
            $"[VulkanSponza] froxel grid {x}x{y}x{froxelGridZ} ({FroxelPixels} px/froxel at {width}x{height})");
    }

    // A low-discrepancy offset in [0,1) for this frame's slice sample, so successive frames land at
    // different depths inside the same segment. R2 rather than Halton: one multiply, no bit
    // reversal, and a better-spread sequence than either for one dimension.
    private float FogJitter() => (float)((postLoadFrames * 0.7548776662) % 1.0);

    // How far a point in these bounds can travel along `dir` before it leaves the scene volume.
    // A slab test against the sky volume, which is the authored extent of everything that can cast
    // or receive — past it there is nothing left to darken.
    private float SceneExitDistance(Bounds3 bounds, Vector3 dir)
    {
        var min = skyVolumeMin;
        var max = skyVolumeMin + skyVolumeSpan;
        var t = float.MaxValue;
        for (var a = 0; a < 3; a++)
        {
            var d = a == 0 ? dir.X : a == 1 ? dir.Y : dir.Z;
            if (MathF.Abs(d) < 1e-5f) continue;
            // Measure from the trailing face so a caster overlapping a scene boundary retains a
            // non-zero conservative sweep until its entire box exits the receiver volume.
            var start = d > 0
                ? (a == 0 ? bounds.Min.X : a == 1 ? bounds.Min.Y : bounds.Min.Z)
                : (a == 0 ? bounds.Max.X : a == 1 ? bounds.Max.Y : bounds.Max.Z);
            var wall = d > 0
                ? (a == 0 ? max.X : a == 1 ? max.Y : max.Z)
                : (a == 0 ? min.X : a == 1 ? min.Y : min.Z);
            t = MathF.Min(t, MathF.Max((wall - start) / d, 0f));
        }
        return t == float.MaxValue ? skyVolumeSpan.Length() : t;
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

    // Whether the fog has real fields to scatter. Both halves must be there: the baked sky
    // visibility volume decides how much sky a froxel sees, and the bounce atlas supplies what the
    // scene sent back. Either one missing and the medium is back to a constant, so say so once
    // here rather than testing three flags at the dispatch.
    private bool fogIndirect =>
        skyVolumeLoaded && skyVisibilityEnabled && bounceReady && !skipSkySample;

    // Rebuilt per frame, because the bounce atlas alternates: recorded BEFORE the write index
    // flips, so bounceTextures[bounceWrite] here is the solution the previous frame finished —
    // the same texture the lit pass reads as bounceTextures[BounceRead] after the flip.
    private ShaderTextureBinding[] FroxelBindings() => new[]
    {
        new ShaderTextureBinding("uGrid", froxelGridTexture),
        new ShaderTextureBinding("uCascadeShadowMaps[0]", graph.GetDepthTexture(cascadeHandles[0])),
        new ShaderTextureBinding("uCascadeShadowMaps[1]", graph.GetDepthTexture(cascadeHandles[1])),
        new ShaderTextureBinding("uCascadeShadowMaps[2]", graph.GetDepthTexture(cascadeHandles[2])),
        new ShaderTextureBinding("uSkyVisibility", skyVisibilityTextures[0]),
        new ShaderTextureBinding("uIrradiance", irradianceCubeTexture),
        // Never a hole, even before the first solve: a descriptor set with a gap is a device loss,
        // and uFogParams.z is what tells the shader not to read these.
        new ShaderTextureBinding("uAtlas", bounceReady ? bounceTextures[bounceWrite] : brdfLutTexture),
        new ShaderTextureBinding("uDepthAtlas", bounceReady ? bounceDepthTextures[bounceWrite] : brdfLutTexture),
        new ShaderTextureBinding("uScatterPrev", fogScatterTextures[fogScatterWrite ^ 1]),
        new ShaderTextureBinding("uScatter", fogScatterTextures[fogScatterWrite]),
        new ShaderTextureBinding("uOccupancy",
            occX > 0 ? occupancyTexture : skyVisibilityTextures[0]),
    };

    private ShaderTextureBinding[] BounceBindings() => new[]
    {
        new ShaderTextureBinding("uAtlas", bounceTextures[bounceWrite]),
        new ShaderTextureBinding("uDepthAtlas", bounceDepthTextures[bounceWrite]),
        new ShaderTextureBinding("uDepthAtlasPrev", bounceDepthTextures[BounceRead]),
        new ShaderTextureBinding("uOccupancy", occupancyTexture),
        new ShaderTextureBinding("uAlbedo", albX > 0 ? albedoTexture : occupancyTexture),
        new ShaderTextureBinding("uProbeUsage", probeUsageTexture),
        // All SH bands plus irradiance form the injector's distant-sky source term.
        new ShaderTextureBinding("uSkyVisibility", skyVisibilityTextures[0]),
        new ShaderTextureBinding("uSkyVisibility1", skyVisibilityTextures[1]),
        new ShaderTextureBinding("uSkyVisibility2", skyVisibilityTextures[2]),
        new ShaderTextureBinding("uIrradiance", irradianceCubeTexture),
        // Last frame's solution, which is what turns a rotation of sweeps into successive bounces
        // AND what lets this dispatch run without the lit pass waiting on it.
        new ShaderTextureBinding("uAtlasPrev", bounceTextures[BounceRead]),
    };

    // --- live GPU pass cost ----------------------------------------------
    // The mean of the last 60 frames per pass, taken from the host's cumulative totals: the same window
    // the overlay's Perf tab shows. A window rather than a lifetime mean, which includes loading and
    // responds too slowly for live controls.
    private GpuPassWindow gpuPasses = new(FrameTimings.None);

    /// <summary>What the probe field actually holds, against what the inputs say it should.</summary>
    /// <remarks>
    /// Compares stored incident light with measured sun, sky irradiance, enclosure, and closure so
    /// transport loss can be distinguished from presentation choices.
    ///
    /// The floor printed here is the SKY alone: sky irradiance times the mean visibility of the
    /// volume. It is a floor and not a target, because every probe should additionally carry bounce.
    /// A field sitting at or below it is carrying no bounce at all.
    /// </remarks>
    private void WriteProbeCensus()
    {
        if (!bounceReady) { Console.WriteLine("[VulkanSponza] probe census: no bounce field."); return; }
        var irr = device.ReadTexture(bounceTextures[BounceRead], out var w, out var h, out var format);
        if (format != TextureFormat.Rgba16F) { Console.WriteLine("[VulkanSponza] probe census: unexpected atlas format."); return; }

        // The depth atlas alongside it, for the ray closure the march parked in its alpha.
        var depth = device.ReadTexture(bounceDepthTextures[BounceRead], out var dw, out var dh, out _);
        var closureFlat = new double[dw * dh];
        for (var i = 0; i < dw * dh; i++)
            closureFlat[i] = (float)BitConverter.ToHalf(depth, i * 8 + 6);

        double sunlitSum = 0;
        var sunlitCount = 0;
        var lum = new List<double>(w * h);
        var lumFlat = new double[w * h];
        double sum = 0;
        var black = 0;
        const int tile = 8;
        // Tile (px,py) for probe p, matching blix_probeTile: x = p.x, y = p.y + p.z * dims.y.
        double lumByProbe(int probe, int t, int perProbe)
        {
            var px = probe % bounceX;
            var py = (probe / bounceX) % bounceY;
            var pz = probe / (bounceX * bounceY);
            var x0 = px * tile;
            var y0 = (py + pz * bounceY) * tile;
            var tx = x0 + (t % tile);
            var ty = y0 + (t / tile);
            if (tx >= w || ty >= h) return 0;
            return lumFlat[ty * w + tx];
        }
        for (var i = 0; i < w * h; i++)
        {
            var a = (float)BitConverter.ToHalf(irr, i * 8 + 6);
            if (a > 0.25) { sunlitSum += (a - 0.5) * 2.0; sunlitCount++; }
            double r = (float)BitConverter.ToHalf(irr, i * 8);
            double g = (float)BitConverter.ToHalf(irr, i * 8 + 2);
            double b = (float)BitConverter.ToHalf(irr, i * 8 + 4);
            var y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            if (y <= 1e-6) black++;
            lum.Add(y);
            lumFlat[i] = y;
            sum += y;
        }
        lum.Sort();
        double Pct(double p) => lum.Count == 0 ? 0 : lum[Math.Clamp((int)(p * lum.Count), 0, lum.Count - 1)];

        var sunLum = 0.2126 * EffectiveSunIrradiance.X + 0.7152 * EffectiveSunIrradiance.Y + 0.0722 * EffectiveSunIrradiance.Z;
        var median = Pct(0.5);
        Console.WriteLine("[VulkanSponza] probe census — irradiance the field is carrying:");
        Console.WriteLine(string.Create(Inv,
            $"    texels {lum.Count:N0} over {bounceX}x{bounceY}x{bounceZ} probes, {black * 100.0 / Math.Max(1, lum.Count):0.0}% exactly zero"));
        Console.WriteLine(string.Create(Inv,
            $"    luminance  median {median:0.0000}   p25 {Pct(0.25):0.0000}   p75 {Pct(0.75):0.0000}   p95 {Pct(0.95):0.0000}   mean {sum / Math.Max(1, lum.Count):0.0000}"));
        Console.WriteLine(string.Create(Inv,
            $"    against    sun irradiance {sunLum:0.000}  ->  median is {median / Math.Max(sunLum, 1e-6) * 100.0:0.00}% of it"));
        Console.WriteLine(string.Create(Inv,
            $"    sunlit     {(sunlitCount > 0 ? sunlitSum / sunlitCount : 0) * 100.0:0.0}% of what the probes can see is in sun (mean over solved texels)"));
        Console.WriteLine(string.Create(Inv,
            $"    volume     mean sky visibility {meanSkyVisibility:0.000} (a surface seeing this much sky, under an albedo ~0.27 scene)"));
        // Bin by baked cell visibility so open and enclosed regions do not collapse into one median.
        if (cellSkyVisibility.Length == bounceX * bounceY * bounceZ)
        {
            // Include height because equal sky visibility can describe geometrically distinct
            // courtyard and arcade regions.
            var bins = new (double Sum, int Count, double Vis, double Y, double Closure)[5];
            var perProbe = lum.Count / Math.Max(1, bounceX * bounceY * bounceZ);
            for (var probe = 0; probe < bounceX * bounceY * bounceZ; probe++)
            {
                var vis = cellSkyVisibility[probe];
                var bin = Math.Clamp((int)(vis * 5.0), 0, 4);
                double probeSum = 0;
                for (var t = 0; t < perProbe; t++) probeSum += lumByProbe(probe, t, perProbe);
                bins[bin].Sum += probeSum / Math.Max(1, perProbe);
                bins[bin].Count++;
                bins[bin].Vis += vis;
                var pz = probe / (bounceX * bounceY);
                var py2 = (probe / bounceX) % bounceY;
                bins[bin].Y += skyVolumeMin.Y + (py2 + 0.5f) * skyVolumeSpan.Y / bounceY;
                // Closure is constant across a tile, so the tile's first texel is the whole answer.
                var cx = (probe % bounceX) * tile;
                var cy = ((probe / bounceX) % bounceY + (probe / (bounceX * bounceY)) * bounceY) * tile;
                if (cx < dw && cy < dh) bins[bin].Closure += closureFlat[cy * dw + cx];
            }
            Console.WriteLine("    by enclosure (the cell's own sky visibility):");
            for (var b = 0; b < 5; b++)
            {
                if (bins[b].Count == 0) continue;
                Console.WriteLine(string.Create(Inv,
                    $"      visibility {b * 20,3}-{(b + 1) * 20,3}%  n={bins[b].Count,6:N0}  " +
                    $"mean visibility {bins[b].Vis / bins[b].Count:0.000}  " +
                    $"mean y {bins[b].Y / bins[b].Count,6:0.0} m  " +
                    $"irradiance {bins[b].Sum / bins[b].Count:0.0000}  " +
                    // Independently baked visibility predicts closure as 1 - visibility.
                    $"closure {bins[b].Closure / bins[b].Count:0.000} vs expected {1.0 - bins[b].Vis / bins[b].Count:0.000}"));
            }
        }
        else
        {
            Console.WriteLine(
                "    (no per-cell binning: the bounce grid is not 1:1 with the visibility volume)");
        }
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
        var hiZ = ReadHiZ();
        long placementTris = 0, clusterFrustumTris = 0, coneTris = 0, ineligibleTris = 0;
        long clustersInFrustum = 0, clustersAfterCone = 0, unclustered = 0;
        // Occlusion against this frame's Hi-Z, on top of the frustum: whole placements, then each
        // remaining cluster (after the cone).
        long placementHiddenTris = 0, clusterHiddenTris = 0, clustersVisible = 0;
        for (var i = 0; i < opaquePlacements.Count; i++)
        {
            var p = opaquePlacements[i];
            if (!frustum.Intersects(p.Bounds, CameraCullMargin)) continue;
            var d = opaqueDrawables[p.Drawable];
            var level = d.PickLod(p.Bounds, cameraPosition, LodErrorScale, render.LodErrorPixels * opaqueLodMargins[i], 0);
            placementTris += d.LodIndexCounts[level] / 3;
            var placementHidden = hiZ?.Test(p.Bounds) == HiZVerdict.Hidden;
            if (placementHidden) placementHiddenTris += d.LodIndexCounts[level] / 3;
            var clusters = d.LodClusters is { } lc && level < lc.Length ? lc[level] : Array.Empty<MeshCluster>();
            if (clusters.Count == 0)
            {
                unclustered += d.LodIndexCounts[level] / 3;
                clusterFrustumTris += d.LodIndexCounts[level] / 3;
                coneTris += d.LodIndexCounts[level] / 3;
                if (placementHidden) clusterHiddenTris += d.LodIndexCounts[level] / 3;
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
                if (placementHidden || hiZ?.Test(clusterBounds) == HiZVerdict.Hidden) clusterHiddenTris += tris;
                else clustersVisible++;
            }
        }

        double Share(long n) => placementTris > 0 ? n * 100.0 / placementTris : 0;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] cluster census at this camera (opaque, {render.LodErrorPixels:0.##} px): "
            + $"placement culling submits {placementTris:N0} triangles; "
            + $"cluster frustum keeps {clusterFrustumTris:N0} ({Share(clusterFrustumTris):0.0}%, {clustersInFrustum:N0} clusters); "
            + $"+ normal cone keeps {coneTris:N0} ({Share(coneTris):0.0}%, {clustersAfterCone:N0} clusters). "
            + $"Cone-ineligible (double-sided or non-uniform): {Share(ineligibleTris):0.0}%; unclustered: {Share(unclustered):0.0}%."));
        if (hiZ is null) return;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] occlusion on top (this frame's Hi-Z level {HiZCensusLevel}, {hiZ.Width}x{hiZ.Height}, conservative): "
            + $"placement occlusion keeps {placementTris - placementHiddenTris:N0} ({Share(placementTris - placementHiddenTris):0.0}%); "
            + $"frustum + cone + cluster occlusion keeps {coneTris - clusterHiddenTris:N0} "
            + $"({Share(coneTris - clusterHiddenTris):0.0}%, {clustersVisible:N0} clusters)."));
    }

    /// <summary>Prints resolved GPU milliseconds per pass, heaviest first.</summary>
    /// <remarks>
    /// Uses cumulative resolved device timestamps and prints sample counts beside each lifetime
    /// mean. On tile GPUs these timings attribute encoders but do not partition total frame time.
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

        Console.WriteLine("[VulkanSponza] GPU ms per pass (mean over resolved frames):");
        double frameTotal = 0;
        foreach (var entry in totals.OrderByDescending(e => e.Value.MeanMs))
        {
            if (entry.Value.Samples == 0) continue;
            var mean = entry.Value.MeanMs;
            frameTotal += mean;
            Console.WriteLine($"  {mean,8:0.000} ms  {entry.Key}  (n={entry.Value.Samples})");
        }
        Console.WriteLine($"  {frameTotal,8:0.000} ms  TOTAL");
        Console.WriteLine(
            "  NOTE: on a tile-based GPU (Apple/MoltenVK) these bracket ENCODER submission, not the "
            + "deferred tiled execution, so they do not sum to the frame. Compare them to each other, "
            + "and use paired A/B runs (--no-ao and friends) for absolute cost.");
        // Report triangle means over the measurement window rather than the camera's final pose.
        var tf = Math.Max(1, triangleFrames);
        if (gpuCull)
        {
            Console.WriteLine("[VulkanSponza] triangles submitted: counted on the GPU, not read back (--cpu-cull counts them).");
        }
        else Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] triangles submitted (mean of {tf} frames): cascades "
            + $"{cascadeTriangleSum[0] / tf:N0}/{cascadeTriangleSum[1] / tf:N0}/{cascadeTriangleSum[2] / tf:N0}, "
            + $"camera {cameraTriangleSum / tf:N0}"));
        Console.WriteLine(string.Create(Inv,
            $"  shadow maps {ShadowMapSizes[0]}/{ShadowMapSizes[1]}/{ShadowMapSizes[2]}, texel {cascadeTexelWorld[0]:0.000}/{cascadeTexelWorld[1]:0.000}/{cascadeTexelWorld[2]:0.000} m, budget {shadowLodTexels:0.0} texels"));

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

    /// <summary>Writes the ambient-visibility buffer as two PNGs: visibility, and bent normal.</summary>
    /// <remarks>
    /// Encodes raw linear visibility and signed bent-normal direction without exposure or tonemap;
    /// mid-grey visibility therefore means 0.5.
    /// </remarks>
    private void WriteAmbientShot(string path)
    {
        var pixels = device.ReadTexture(
            graph.GetColorTexture(ambientDenoisedHandle), out var width, out var height, out var format);
        if (format != TextureFormat.Rgba16F)
        {
            Console.Error.WriteLine($"[VulkanSponza] ambient target is {format}, expected Rgba16F.");
            return;
        }

        var visibility = new byte[width * height * 4];
        var bent = new byte[width * height * 4];
        double sum = 0;
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var i = 0; i < width * height; i++)
        {
            var src = i * 8;
            var nx = (float)BitConverter.ToHalf(pixels, src);
            var ny = (float)BitConverter.ToHalf(pixels, src + 2);
            var nz = (float)BitConverter.ToHalf(pixels, src + 4);
            var v  = (float)BitConverter.ToHalf(pixels, src + 6);

            sum += v; min = MathF.Min(min, v); max = MathF.Max(max, v);
            var dst = i * 4;
            var g = (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
            visibility[dst] = visibility[dst + 1] = visibility[dst + 2] = g;
            visibility[dst + 3] = 255;
            // Directions are signed; the usual half-and-shift so -1 reads black and +1 white.
            bent[dst]     = (byte)Math.Clamp((int)MathF.Round((nx * 0.5f + 0.5f) * 255f), 0, 255);
            bent[dst + 1] = (byte)Math.Clamp((int)MathF.Round((ny * 0.5f + 0.5f) * 255f), 0, 255);
            bent[dst + 2] = (byte)Math.Clamp((int)MathF.Round((nz * 0.5f + 0.5f) * 255f), 0, 255);
            bent[dst + 3] = 255;
        }

        var bentPath = Path.ChangeExtension(path, null) + ".bent.png";
        PngWriter.WriteRgba8(path, visibility, width, height);
        PngWriter.WriteRgba8(bentPath, bent, width, height);
        Console.WriteLine(
            $"[VulkanSponza] ambient visibility {width}x{height}: " +
            $"min {min:0.000} max {max:0.000} mean {sum / (width * height):0.000}");
        Console.WriteLine($"[VulkanSponza]   {path}");
        Console.WriteLine($"[VulkanSponza]   {bentPath}");
    }

    // Record one parity pass. The unrecorded target keeps the previous frame and becomes history.
    private void RecordTaaResolve()
    {
        if (render.Taa <= 0f) { taaHistoryValid = false; return; }
        Matrix4x4.Invert(viewProjJittered, out var invJittered);
        var uniforms = new ShaderUniform[]
        {
            new("uInvViewProjJittered", new Matrix4x4Uniform(invJittered)),
            new("uPrevViewProj",        new Matrix4x4Uniform(taaHistoryValid ? prevTaaViewProj : viewProj)),
            new("uParams",              new Vector4Uniform(new Vector4(
                render.Taa, taaHistoryValid ? 1f : 0f, render.ShowTaaRejection ? 1f : 0f, 0f))),
        };
        var bindings = new[]
        {
            new ShaderTextureBinding("uCurrent", graph.GetColorTexture(hdrHandle)),
            new ShaderTextureBinding("uHistory", graph.GetColorTexture(taaHandles[taaWrite ^ 1])),
            new ShaderTextureBinding("uDepth", graph.GetDepthTexture(SampleableSceneDepth)),
        };
        graph.Pass(taaPassHandles[taaWrite], scope => fullscreen.Draw(
            scope, taaPipelines[taaWrite], bindings, pushConstants: null, uniforms: uniforms));
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
        var hdrTex = render.Taa > 0f && taaHistoryValid
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

    // Opaque shadow caster push: [cascadeViewProj] = 64 bytes, matching shadow.vert's
    // PushConstants block. Where each placement stands is set 3's.
    private static byte[] ShadowOpaquePushBytes(Matrix4x4 cascadeViewProj)
    {
        var bytes = new byte[64];
        MemoryMarshal.Write(bytes.AsSpan(0, 64), in cascadeViewProj);
        return bytes;
    }

    // Mask shadow caster push: [cascadeViewProj | alphaParams] = 80 bytes, matching
    // shadow_mask's PushConstants block. alphaParams.xy = (alphaCutoff, baseColorAlpha).
    // Rents a pooled buffer (reset per frame via maskPushCursor) rather than allocating,
    // since these differ per draw.
    private byte[] RentMaskPush(Matrix4x4 cascadeViewProj, float alphaCutoff, float baseColorAlpha)
    {
        if (maskPushCursor >= maskPushPool.Count) maskPushPool.Add(new byte[80]);
        var bytes = maskPushPool[maskPushCursor++];
        MemoryMarshal.Write(bytes.AsSpan(0, 64), in cascadeViewProj);
        var alphaParams = new Vector4(alphaCutoff, baseColorAlpha, 0f, 0f);
        MemoryMarshal.Write(bytes.AsSpan(64, 16), in alphaParams);
        return bytes;
    }

    // --- input -------------------------------------------------------------
}
