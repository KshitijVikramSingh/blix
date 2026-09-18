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
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanSponza;

internal sealed partial class SponzaLoop
{
    // Index buffer a drawable's LOD indices live in (chosen at consolidation).
    private IndexBufferHandle SharedIb(Drawable d) => d.IndicesAreU32 ? sharedIbU32 : sharedIbU16;

    // Fill an indirect buffer: one VkDrawIndexedIndirectCommand per opaque
    // drawable (in grouped order), LOD picked by screen-space error. When a cull
    // frustum is given, culled objects get instanceCount 0 (drawn as a GPU no-op
    // — no compaction needed, so group offsets stay fixed). Written to the
    // current frame slot. Returns the visible count (for the diagnostic).
    private int FillIndirect(List<Drawable> drawables, float[] lodMargins, IndirectBufferHandle buffer, Frustum? cull, float margin)
    {
        // <b>An empty scene has no indirect buffer, and that used to be a crash.</b> When every
        // pack failed to load — stale cooked files after a format bump — the demo reported exactly
        // that, by name, for each pack, and then died three lines later on "Unknown indirect buffer
        // handle 0". A tool that diagnoses its own problem and then throws something unrelated has
        // buried the useful half of its output under the useless half.
        if (drawables.Count == 0) return 0;

        var cmds = MemoryMarshal.Cast<byte, uint>(indirectScratch.AsSpan());
        var visible = 0;
        for (var i = 0; i < drawables.Count; i++)
        {
            var d = drawables[i];
            var vis = cull is not { } f || f.Intersects(d.Bounds, margin);
            // Per-primitive LOD margin (live-tunable) scales the global px budget.
            var lod = d.PickLod(cameraPosition, LodErrorScale, render.LodErrorPixels * lodMargins[i]);
            var o = i * 5;
            cmds[o + 0] = (uint)d.LodIndexCounts[lod]; // indexCount
            cmds[o + 1] = vis ? 1u : 0u;               // instanceCount (0 = culled)
            cmds[o + 2] = (uint)d.LodFirstIndex[lod];  // firstIndex
            cmds[o + 3] = (uint)d.BaseVertex;          // vertexOffset
            cmds[o + 4] = 0;                           // firstInstance
            if (vis) visible++;
        }
        // Write exactly this list's prefix; the buffer is sized to its count,
        // and indirectScratch is sized for the largest (opaque) list.
        vk.WriteIndirectCommands(buffer, indirectScratch.AsSpan(0, drawables.Count * VulkanGraphicsDevice.IndirectCommandStride));
        return visible;
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        // <b>Before the early return, or the cheap half of the experiment is never recorded.</b> The
        // interesting frames are the ones drawn WHILE loading — flat-shaded, no materials — and the
        // first version of this counted only frames that got past `sceneLoaded`, so the flat bucket
        // came back empty and the comparison this exists for could not be made.
        // <b>Counted here, before every early return.</b> It used to be incremented at the bottom,
        // which the flat path never reaches — so the moment --ab-flat entered its flat phase the
        // counter froze, the phase it is derived from never advanced, and the run stayed flat
        // forever. A toggle driven by a counter that the toggled-to branch stops advancing can only
        // ever fire once.
        framesRendered++;
        var stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (lastFrameStamp != 0)
        {
            var periodMs = System.Diagnostics.Stopwatch.GetElapsedTime(lastFrameStamp, stamp).TotalMilliseconds;
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

        // Stress hook: flip fog every 90 frames to exercise the on/off barrier
        // transitions under validation (no effect without --fog-stress).
        if (fogStress && (++fogStressFrame % 90 == 0)) fog.Enabled = !fog.Enabled;

        var perFrameList = new List<ShaderUniform>
        {
            new("uViewProjection",   new Matrix4x4Uniform(viewProj)),
            new("uSunDirection",     new Vector3Uniform(sunDirection)),
            new("uSunIrradiance",    new Vector3Uniform(sunIrradiance)),
            new("uCameraPos",        new Vector3Uniform(cameraPosition)),
            new("uEnvMipCount",      new FloatUniform(iblPrefilterMips)),
            new("uSheenMipCount",    new FloatUniform(sheenMipCount)),
            // x < 0 means "use what the material carries"; the overlay sets it to find a value.
            new("uClothOverride",    new Vector4Uniform(clothOverride
                ? new Vector4(sheenRoughness, diffuseTransmit, 0f, 0f)
                : new Vector4(-1f, -1f, 0f, 0f))),
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
            new("uSkyMin",           new Vector4Uniform(new Vector4(
                skyVolumeMin, skyVolumeLoaded && skyVisibilityEnabled && !skipSkySample ? 1f : 0f))),
            // w: how far along the normal the probe lookup is pushed. About one cell, so a surface
            // asks the cell in FRONT of it rather than the one it is embedded in.
            new("uSkyScale",         new Vector4Uniform(new Vector4(skyVolumeInvSpan, 0.6f))),
            new("uBounceStrength",   new FloatUniform(bounceReady && skyVisibilityEnabled && !skipSkySample ? 1f : 0f)),
            // One component per --ab shading mode, live only during that mode's off-phase.
            new("uAbFlags",          new Vector4Uniform(new Vector4(
                AbOffPhase && abMode == "textures" ? 1f : 0f,
                AbOffPhase && abMode == "pbr"      ? 1f : 0f,
                AbOffPhase && abMode == "ibl"      ? 1f : 0f,
                AbOffPhase && abMode == "normal"   ? 1f : 0f))),
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
            FillIndirect(opaqueDrawables, opaqueLodMargins, opaqueIndirect, cull: null, margin: 0f);
            graph.Pass(depthPrepassHandle, scope =>
            {
                foreach (var g in opaqueGroups)
                    scope.DrawIndexedIndirect(
                        vertexBuffer: sharedVb, indexBuffer: g.IsU32 ? sharedIbU32 : sharedIbU16,
                        pipeline: prepassOpaquePipeline, indirectBuffer: opaqueIndirect,
                        indirectByteOffset: g.Start * VulkanGraphicsDevice.IndirectCommandStride, drawCount: g.Count,
                        uniforms: perFrame, textures: Array.Empty<ShaderTextureBinding>(),
                        material: null, pushConstants: identityPush);
            });
            graph.Pass(litPassHandle, scope =>
            {
                foreach (var g in opaqueGroups)
                    scope.DrawIndexedIndirect(
                        vertexBuffer: sharedVb, indexBuffer: g.IsU32 ? sharedIbU32 : sharedIbU16,
                        pipeline: flatPipeline, indirectBuffer: opaqueIndirect,
                        indirectByteOffset: g.Start * VulkanGraphicsDevice.IndirectCommandStride, drawCount: g.Count,
                        uniforms: perFrame, textures: Array.Empty<ShaderTextureBinding>(),
                        material: null, pushConstants: identityPush);
            }, clearColor: new GraphicsColor(0.05f, 0.07f, 0.10f, 1f));
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
            // Cached: this cascade's VP is unchanged since it was last rendered,
            // so its depth target still holds the right result — skip the pass
            // entirely (no begin-render-pass → contents persist in ShaderReadOnly,
            // which is exactly what the lit pass samples).
            if (vp == cachedCascadeViewProj[ci])
            {
                cascadeRendered[ci] = false;
                continue;
            }
            cachedCascadeViewProj[ci] = vp;
            cascadeRendered[ci] = true;
            // Frustum.FromViewProjection expects a column-vector clip matrix
            // (clip = M·world); our cascade VP is the System.Numerics
            // row-vector form (clip = Vector4.Transform(world, M)), so transpose
            // to hand it the clip-coordinate generators as rows.
            var cascadeFrustum = Frustum.FromViewProjection(Matrix4x4.Transpose(vp));
            // Fill this cascade's indirect buffer (per-cascade frustum cull → 0
            // instanceCount; same SSE LOD as the lit/pre-pass so shadow depth
            // matches the shaded silhouette). Then one indirect draw per group.
            cascadeDrawCounts[ci] = FillIndirect(opaqueDrawables, opaqueLodMargins, cascadeIndirect[ci], cull ? cascadeFrustum : null, margin);
            // Opaque casters all push the same bytes (identity model + this
            // cascade's VP); mask casters push per-material alpha params, so one
            // mask push per group (constant within a material).
            var cascadeOpaquePush = ShadowOpaquePushBytes(Matrix4x4.Identity, vp);
            var cascadeBuf = cascadeIndirect[ci];
            graph.Pass(cascadePassHandles[ci], scope =>
            {
                foreach (var g in opaqueGroups)
                {
                    var ib = g.IsU32 ? sharedIbU32 : sharedIbU16;
                    var byteOffset = g.Start * VulkanGraphicsDevice.IndirectCommandStride;
                    if (g.IsMask)
                    {
                        var rep = opaqueDrawables[g.Start];
                        scope.DrawIndexedIndirect(
                            vertexBuffer: sharedVb, indexBuffer: ib, pipeline: shadowMaskPipeline,
                            indirectBuffer: cascadeBuf, indirectByteOffset: byteOffset, drawCount: g.Count,
                            uniforms: Array.Empty<ShaderUniform>(), textures: rep.ShadowAlbedoBinding,
                            material: null,
                            pushConstants: RentMaskPush(Matrix4x4.Identity, vp, rep.AlphaCutoff, rep.BaseColorAlpha));
                    }
                    else
                    {
                        scope.DrawIndexedIndirect(
                            vertexBuffer: sharedVb, indexBuffer: ib, pipeline: shadowOpaquePipeline,
                            indirectBuffer: cascadeBuf, indirectByteOffset: byteOffset, drawCount: g.Count,
                            uniforms: Array.Empty<ShaderUniform>(), textures: Array.Empty<ShaderTextureBinding>(),
                            material: null, pushConstants: cascadeOpaquePush);
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
            Matrix4x4.Invert(viewProj, out var invViewProj);
            var froxelUniforms = new ShaderUniform[]
            {
                new("uInvViewProj",   new Matrix4x4Uniform(invViewProj)),
                new("uCamPos",        new Vector4Uniform(new Vector4(cameraPosition, fog.Far))),
                new("uCamForward",    new Vector4Uniform(new Vector4(cameraForward, fog.Density))),
                // The froxel pass scatters the same sun, so it takes the same measured irradiance.
                new("uSunDir",        new Vector4Uniform(new Vector4(sunDirection, sunIrradiance.X))),
                new("uSunColor",      new Vector4Uniform(new Vector4(1f, 1f, 1f, fog.Scatter))),
                new("uFogParams",     new Vector4Uniform(new Vector4(fog.PhaseG, fog.Ambient, 0f, 0f))),
                // No splits: the fog picks its cascade by containment through the shared lookup,
                // exactly as the lit pass does. It used to take the view-depth bounds and select on
                // them, which silently stopped matching the surfaces when the lit pass moved.
                new("uCascadeVP",     new Matrix4x4ArrayUniform(cascadeViewProj)),
            };
            graph.Dispatch(froxelPassHandle, new DispatchCommand(
                froxelPipeline,
                (FroxelGridX + 7) / 8, (FroxelGridY + 7) / 8, 1,
                froxelUniforms, froxelBindings));
        }

        // Fill the camera opaque indirect commands once per frame (LOD by SSE, no
        // cull); both the depth pre-pass and the lit pass consume this buffer —
        // they draw the identical opaque set at identical LODs.
        FillIndirect(opaqueDrawables, opaqueLodMargins, opaqueIndirect, cull: null, margin: 0f);
        if (blendDrawables.Count > 0) FillIndirect(blendDrawables, blendLodMargins, blendIndirect, cull: null, margin: 0f);

        // Depth pre-pass: same non-blend set as the lit pass (no cull, so the
        // depth the lit pass loads covers exactly what it shades), depth only.
        // Per group: mask binds the material (set 2 albedo) for the alpha
        // discard + the mask pipeline; opaque needs only set 0 + model push.
        // --ab prepass off-phase: record the pass (it still clears depth) but draw nothing into it,
        // so the lit pass below establishes depth itself through the writing pipelines.
        var skipPrepass = abMode == "prepass" && AbOffPhase;
        graph.Pass(depthPrepassHandle, scope =>
        {
            if (skipPrepass) return;
            foreach (var g in opaqueGroups)
            {
                scope.DrawIndexedIndirect(
                    vertexBuffer: sharedVb,
                    indexBuffer: g.IsU32 ? sharedIbU32 : sharedIbU16,
                    pipeline: g.IsMask ? prepassMaskPipeline : prepassOpaquePipeline,
                    indirectBuffer: opaqueIndirect,
                    indirectByteOffset: g.Start * VulkanGraphicsDevice.IndirectCommandStride,
                    drawCount: g.Count,
                    uniforms: perFrame,
                    textures: Array.Empty<ShaderTextureBinding>(),
                    material: g.IsMask ? g.Material : null,
                    pushConstants: identityPush);
            }
        });

        // Flip before dispatching: the pass writes one texture while every reader — the lit pass,
        // and the pass's own multi-bounce feedback — takes the other, which is what keeps the
        // compute off the fragment stage's critical path.
        if (bounceReady && skyVisibilityEnabled && !skipInject) bounceWrite ^= 1;
        if (bounceReady && skyBounceBinding >= 0)
        {
            passBindings[skyBounceBinding] = new ShaderTextureBinding(
                "uSkyBounce", bounceTextures[bounceWrite ^ 1], Slot: 7);
        }

        // Sun bounce into the probe grid. Cheap enough to redo every frame at this probe count, and
        // redoing it is the point: the whole reason it is not baked is that it must follow the sun.
        if (bounceReady && skyVisibilityEnabled && !skipInject)
        {
            var injectUniforms = new ShaderUniform[]
            {
                new("uBoundsMin",  new Vector4Uniform(new Vector4(skyVolumeMin, 0f))),
                new("uBoundsSpan", new Vector4Uniform(new Vector4(skyVolumeSpan, ambient.BounceStrength))),
                new("uProbeDims",  new Vector4Uniform(new Vector4(probeX, probeY, probeZ, MathF.Round(injectRays)))),
                new("uOccupancyDims", new Vector4Uniform(new Vector4(occX, occY, occZ, injectDensity ? 1f : 0f))),
                // w carries translucency: how much of what a partial cell absorbs comes out the far
                // side wearing its colour. Zero on opaque cells in the shader, or walls would leak.
                new("uAlbedoDims", new Vector4Uniform(new Vector4(albX, albY, albZ, injectTranslucency))),
                new("uSunDirection",  new Vector4Uniform(new Vector4(sunDirection, 0f))),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(sunIrradiance, 0f))),
                new("uSchedule",      new Vector4Uniform(new Vector4(framesRendered, MathF.Round(injectPeriod), 0f, 0f))),
            };
            graph.Dispatch(injectPassHandle, new DispatchCommand(
                injectPipeline,
                (probeX + 3) / 4, (probeY + 3) / 4, (probeZ + 3) / 4,
                injectUniforms, BounceBindings()));
        }

        // Hi-Z pyramid: level 0 reduces the resolved depth, each level after reduces its parent.
        {
            Matrix4x4.Invert(cameraProjection, out var hiZInvProjection);
            for (var level = 0; level < HiZLevels; level++)
            {
                var srcW = level == 0 ? frame.Width : Math.Max(1, frame.Width >> level);
                var srcH = level == 0 ? frame.Height : Math.Max(1, frame.Height >> level);
                var dstW = Math.Max(1, frame.Width >> (level + 1));
                var dstH = Math.Max(1, frame.Height >> (level + 1));
                var source = level == 0
                    ? graph.GetDepthTexture(SampleableSceneDepth)
                    : graph.GetColorTexture(hiZHandles[level - 1]);
                var uniforms = new ShaderUniform[]
                {
                    new("uInvProjection", new Matrix4x4Uniform(hiZInvProjection)),
                    new("uSizes", new Vector4Uniform(new Vector4(srcW, srcH, dstW, dstH))),
                    new("uMode",  new Vector4Uniform(new Vector4(level == 0 ? 1f : 0f, 0f, 0f, 0f))),
                };
                var levelIndex = level;
                var skipHiZ = abMode == "hiz" && AbOffPhase;
                graph.Pass(hiZPassHandles[level], scope =>
                {
                    if (skipHiZ) return;
                    fullscreen.Draw(
                        scope, hiZPipelines[levelIndex],
                        new[] { new ShaderTextureBinding("uSource", source, Slot: 1) },
                        pushConstants: null,
                        uniforms: uniforms);
                });
            }
        }

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
            };
            var hiZBindings = new ShaderTextureBinding[HiZLevels];
            for (var level = 0; level < HiZLevels; level++)
            {
                hiZBindings[level] = new ShaderTextureBinding(
                    $"uHiZ[{level}]", graph.GetColorTexture(hiZHandles[level]), Slot: 1, ArrayIndex: level);
            }
            graph.Pass(gtaoPassHandle, scope => fullscreen.Draw(
                scope, gtaoPipeline, hiZBindings, pushConstants: null, uniforms: gtaoUniforms));

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
                    new ShaderTextureBinding("uAmbientRaw", graph.GetColorTexture(ambientHandle), Slot: 1),
                    new ShaderTextureBinding(
                        "uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth), Slot: 2),
                },
                pushConstants: null,
                uniforms: denoiseUniforms));
        }

        graph.Pass(litPassHandle, scope =>
        {
            // Opaque + Mask first (depth-test, no write — pre-pass wrote depth),
            // then Blend (depth-test only), so translucent surfaces composite
            // over the resolved opaque depth without writing into it.
            //
            // One indirect draw per (pipeline, material) group over the buffer
            // filled above — ~800 per-object draws collapse to ~one per material.
            // All draws in a group share set0 + set2 (material) + identity push
            // (the static importer bakes transforms), the multidraw constraint.
            foreach (var g in opaqueGroups)
            {
                var pipeline = g.Pipeline;
                if (skipPrepass)
                {
                    if (pipeline == opaqueSolidPipeline) pipeline = opaqueSolidPipelineWrites;
                    else if (pipeline == opaqueDoubleSidedPipeline) pipeline = opaqueDoubleSidedPipelineWrites;
                }
                scope.DrawIndexedIndirect(
                    vertexBuffer: sharedVb,
                    indexBuffer: g.IsU32 ? sharedIbU32 : sharedIbU16,
                    pipeline: pipeline,
                    indirectBuffer: opaqueIndirect,
                    indirectByteOffset: g.Start * VulkanGraphicsDevice.IndirectCommandStride,
                    drawCount: g.Count,
                    uniforms: perFrame,
                    textures: passBindings,
                    material: g.Material,
                    pushConstants: identityPush);
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
                    new("uProbeDims", new Vector4Uniform(new Vector4(probeX, probeY, probeZ, 0f))),
                    new("uProbeMode", new Vector4Uniform(new Vector4(probeField, probeExposure, 0f, 0f))),
                };
                scope.DrawIndexedInstanced(
                    probeVb, probeIb, probePipeline,
                    indexCount: 6, instanceCount: probeX * probeY * probeZ,
                    // Its OWN bindings: the probe shader declares uSkyBounce/uSkyVisibility at set 1
                    // slots 0 and 1, where the lit pass's list puts them at 7 and 6. Handing over a
                    // list built for a different shader binds by slot, not by name.
                    uniforms: probeUniforms,
                    textures: new[]
                    {
                        new ShaderTextureBinding("uSkyBounce",
                            bounceReady ? bounceTextures[bounceWrite ^ 1] : skyVisibilityTexture, Slot: 0),
                        new ShaderTextureBinding("uSkyVisibility", skyVisibilityTexture, Slot: 1),
                    },
                    // null, not an empty array: an empty array still counts as "push constants supplied", and
                    // this shader declares no ranges.
                    perDrawMaterial: null, pushConstants: null!);
            }

            // Sky after opaque, before blend. Fullscreen triangle; positions are
            // synthesised in skybox.vert, so it binds set-0 perFrame + textures only.
            fullscreen.Draw(scope, skyPipeline, passBindings, uniforms: perFrame);
            // Blend (glass), depth-test only, after opaque + sky. One indirect
            // draw per (pipeline, material) group, same as opaque.
            foreach (var g in blendGroups)
            {
                scope.DrawIndexedIndirect(
                    vertexBuffer: sharedVb,
                    indexBuffer: g.IsU32 ? sharedIbU32 : sharedIbU16,
                    pipeline: g.Pipeline,
                    indirectBuffer: blendIndirect,
                    indirectByteOffset: g.Start * VulkanGraphicsDevice.IndirectCommandStride,
                    drawCount: g.Count,
                    uniforms: perFrame,
                    textures: passBindings,
                    material: g.Material,
                    pushConstants: identityPush);
            }
        }, clearColor: new GraphicsColor(0.05f, 0.07f, 0.10f, 1f));

        graph.Execute(commandList);

        RecordPresentPass(commandList);

        // Capture only from the LIT arm — a flat-phase capture would be a picture of the control.
        // <b>fullyLoaded, or the capture can read a buffer no pass has written.</b> Moving the frame
        // counter above the early returns (so --ab could flip phases) also made it count LOADING
        // frames, and the flat loading path returns before the GTAO pass is recorded. A capture that
        // fired during loading therefore read an untouched target and produced NaN — which then
        // looked exactly like a shader bug, and cost a bisect through two shader constants that
        // were never involved.
        // <b>And not on the FIRST such frame either.</b> ReadTexture runs inside OnRender, before this
        // frame's recorded commands have been submitted, so it necessarily reads what the previous
        // frame left behind. Capturing the instant fullyLoaded flips means reading targets the flat
        // loading path never wrote — which came back as uninitialised half-floats, i.e. NaN, and
        // looked for all the world like a shader that had started producing NaN.
        // framePeriodCount only advances on fully-lit frames, so it is the honest clock here.
        // <b>Counted in POST-LOAD frames, because that is what the measurement is made of.</b> It
        // counted every frame including the hundreds spent streaming textures, so an --ab run with
        // --shot-frames 1200 gave each arm only 60-90 samples out of a 600-frame buffer — and the
        // resulting numbers wobbled by 2x between runs while looking like measurements. With the
        // deadline in post-load frames the buffers fill, and the ratio between arms stabilises to
        // within a few per cent.
        var measuredFrames = framePeriodCount + flatPeriodCount;
        if (shotPath is { } path && !shotWritten && fullyLoaded && !AbOffPhase
            && framePeriodCount >= 60 && measuredFrames >= shotFrame)
        {
            shotWritten = true;
            VerifyHiZ();
            OcclusionCensus();
            WriteAmbientShot(path);
            // <b>The RAW buffer as well, because the denoised one cannot answer questions about the
            // search.</b> Every reading taken off the denoised target is a depth-weighted average of
            // a 3x3 neighbourhood, which is exactly right for lighting and exactly wrong for "what
            // did this pixel's horizon search conclude" — and it silently turned a normal probe into
            // a probe of nine blended normals.
            WriteAmbientRaw(Path.ChangeExtension(path, null) + ".raw.png");
            WriteSceneShot(Path.ChangeExtension(path, null) + ".scene.png");
            WritePassBreakdown();
            WriteFrameStats();
            host.RequestClose();
        }
    }

    /// <summary>The ambient buffer before the denoise: rgb as written, alpha as visibility.</summary>
    private void WriteAmbientRaw(string path)
    {
        var pixels = vk.ReadTexture(
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

    /// <summary>How much of the submitted scene is hidden behind other geometry, right now.</summary>
    /// <remarks>
    /// <b>The measurement that decides whether GPU-driven culling is worth building.</b> Doing the
    /// test for real needs a compute shader writing visibility into the indirect buffer, which this
    /// engine cannot express yet: DispatchCommand carries no material, so a dispatch cannot bind a
    /// storage buffer; indirect buffers are created without StorageBufferBit; and the graph infers
    /// image barriers but not buffer ones. That is a substantial arc, and the honest thing is to
    /// find out what it would buy BEFORE building it.
    ///
    /// So the same test runs here on the CPU, once, off a synchronous readback that would be far too
    /// expensive per frame but costs nothing in a capture. It is the real test: project each
    /// drawable's world AABB, take the pyramid level where its screen rect spans a few texels, and
    /// ask whether the NEAREST point of the box is behind the FARTHEST depth recorded across that
    /// rect. That is the conservative occlusion query, and it is what the max channel exists for.
    ///
    /// Reported as a share of TRIANGLES as well as of objects, because 401 drawables are not equal:
    /// culling 200 tiny ones is worth less than culling the two that carry a million triangles each.
    /// </remarks>
    private void OcclusionCensus()
    {
        // A level whose texels are coarse enough that a handful covers a typical object.
        const int Level = 3;
        var pixels = vk.ReadTexture(
            graph.GetColorTexture(hiZHandles[Level]), out var w, out var h, out var format);
        if (format != TextureFormat.Rgba16F) return;

        var farthest = new float[w * h];
        for (var i = 0; i < w * h; i++) farthest[i] = (float)BitConverter.ToHalf(pixels, i * 8 + 2);

        var occluded = 0;
        long occludedTris = 0, totalTris = 0;
        var offscreen = 0;

        foreach (var d in opaqueDrawables)
        {
            var tris = d.LodIndexCounts[0] / 3;
            totalTris += tris;

            // Project the eight corners; track the screen rect and the NEAREST view depth.
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            var nearest = float.MaxValue;
            var anyInFront = false;
            for (var c = 0; c < 8; c++)
            {
                var corner = new Vector3(
                    (c & 1) == 0 ? d.Bounds.Min.X : d.Bounds.Max.X,
                    (c & 2) == 0 ? d.Bounds.Min.Y : d.Bounds.Max.Y,
                    (c & 4) == 0 ? d.Bounds.Min.Z : d.Bounds.Max.Z);
                var view = Vector3.Transform(corner, cameraView);
                var depth = -view.Z;
                if (depth <= CameraNearPlane) { anyInFront = true; continue; }
                nearest = MathF.Min(nearest, depth);
                anyInFront = true;

                var clip = Vector4.Transform(new Vector4(corner, 1f), viewProj);
                var ndcX = clip.X / clip.W;
                var ndcY = clip.Y / clip.W;
                minX = MathF.Min(minX, ndcX); maxX = MathF.Max(maxX, ndcX);
                minY = MathF.Min(minY, ndcY); maxY = MathF.Max(maxY, ndcY);
            }

            if (!anyInFront || nearest == float.MaxValue) { offscreen++; continue; }
            // Entirely outside the frustum sideways — the frustum cull's job, not this census's.
            if (maxX < -1f || minX > 1f || maxY < -1f || minY > 1f) { offscreen++; continue; }

            var x0 = Math.Clamp((int)MathF.Floor((minX * 0.5f + 0.5f) * w), 0, w - 1);
            var x1 = Math.Clamp((int)MathF.Ceiling((maxX * 0.5f + 0.5f) * w), 0, w - 1);
            var y0 = Math.Clamp((int)MathF.Floor((minY * 0.5f + 0.5f) * h), 0, h - 1);
            var y1 = Math.Clamp((int)MathF.Ceiling((maxY * 0.5f + 0.5f) * h), 0, h - 1);

            // Farthest recorded depth anywhere the object covers. If even that is nearer than the
            // object's closest point, nothing the object could draw would survive the depth test.
            var deepest = 0f;
            for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                deepest = MathF.Max(deepest, farthest[y * w + x]);

            if (nearest > deepest)
            {
                occluded++;
                occludedTris += tris;
            }
        }

        var shown = opaqueDrawables.Count - offscreen;
        Console.WriteLine(
            $"[VulkanSponza] occlusion census (Hi-Z level {Level}, {w}x{h}): " +
            $"{occluded}/{shown} on-screen drawables fully hidden, " +
            $"{occludedTris / 1000.0:0.0}k of {totalTris / 1000.0:0.0}k triangles " +
            $"({(totalTris > 0 ? 100.0 * occludedTris / totalTris : 0):0.0}%), {offscreen} off-screen");
    }

    /// <summary>Checks each pyramid level really is the min/max of the one above it.</summary>
    /// <remarks>
    /// <b>A pyramid that produces plausible pixels and the wrong reduction is the worst case.</b>
    /// Occlusion culling built on a max channel that is not actually the maximum drops geometry that
    /// is visible, and the symptom is objects vanishing at certain camera angles — a bug that looks
    /// like culling logic and is not. So the level-to-level relationship is checked arithmetically
    /// here, on the real buffers, rather than trusted because the image looked like a depth buffer.
    ///
    /// Recomputed on the CPU from level i to predict level i+1, then compared. The tolerance is
    /// half-float slack, not a fudge: the GPU reduced in fp16 and so must this comparison.
    /// </remarks>
    private void VerifyHiZ()
    {
        Console.WriteLine("[VulkanSponza] Hi-Z pyramid:");
        float[]? parent = null;
        int parentW = 0, parentH = 0;
        for (var level = 0; level < HiZLevels; level++)
        {
            var pixels = vk.ReadTexture(
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
                // <b>Conservativeness, not equality — and the difference is the whole point.</b>
                // This first asserted that a level EQUALS the 2x2 reduction of its parent, and
                // levels 2 and 4 failed: 405 rows reduce to 202, so a footprint of exactly two
                // SKIPS a row. The shader spans ceil() instead and overlaps, which is why it passed
                // through the even levels and not the odd ones.
                //
                // Equality was the wrong invariant to demand. What a consumer needs is that the
                // pyramid never UNDER-states: min no larger than the true min, max no smaller than
                // the true max. An overlapping footprint satisfies that and a truncating one does
                // not, so the check that would have blessed the dangerous version is the one that
                // was failing the safe one.
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
        // Name the arms after what was actually toggled. They were hardcoded as "lit"/"flat", which
        // was a lie the moment --ab grew a second mode: a shadow run printed its shadows-off arm as
        // "geometry only", which is the kind of label that gets quoted back as a fact.
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
            _        => "post-load shading",
        };
        Report($"ON  : with {term}", framePeriodsMs, framePeriodCount);
        Report($"OFF : without {term}", flatPeriodsMs, flatPeriodCount);

        var lit = Median(framePeriodsMs, framePeriodCount);
        var flat = Median(flatPeriodsMs, flatPeriodCount);
        if (lit > 0 && flat > 0)
        {
            // <b>The ratio first, because it is the part that reproduces.</b> Across three runs of
            // one configuration the absolute cost ranged 3.29-4.94 ms while the ratio held within
            // 0.04 — the milliseconds are a product of whatever thermal state the process found, the
            // ratio is a property of the renderer.
            Console.WriteLine(
                $"[VulkanSponza] {term}: {lit / flat:0.000}x of frame " +
                $"({lit - flat:0.00} ms at this run's {lit:0.0} ms median), " +
                "same geometry, same process, same thermal state");
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
    /// <b>Exposure and an sRGB encode, deliberately NOT the tonemap.</b> Blix.Graphics.Images.Tonemap
    /// orders its modes differently from this demo's TonemapMode enum, so passing one to the other
    /// silently picks the wrong curve — and for the question this capture exists to answer ("is that
    /// surface actually black, or is the film curve crushing it?") a film curve is the last thing
    /// you want in the way. What lands in the file is radiance × exposure, gamma-encoded.
    /// </remarks>
    private void WriteSceneShot(string path)
    {
        var pixels = vk.ReadTexture(
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
    private ShaderTextureBinding[] BounceBindings() => new[]
    {
        new ShaderTextureBinding("uBounce", bounceTextures[bounceWrite], Slot: 1),
        new ShaderTextureBinding("uOccupancy", occupancyTexture, Slot: 2),
        new ShaderTextureBinding("uAlbedo", albX > 0 ? albedoTexture : occupancyTexture, Slot: 5),
        new ShaderTextureBinding("uSkyVisibility", skyVisibilityTexture, Slot: 3),
        // Last frame's solution, which is what turns a rotation of sweeps into successive bounces
        // AND what lets this dispatch run without the lit pass waiting on it.
        new ShaderTextureBinding("uBouncePrev", bounceTextures[bounceWrite ^ 1], Slot: 4),
    };

    /// <summary>Prints resolved GPU milliseconds per pass, heaviest first.</summary>
    /// <remarks>
    /// <b>The device has measured this all along and nobody outside the overlay ever read it.</b>
    /// VulkanGraphicsDevice.GpuPassTotals is cumulative resolved timestamp time per pass; the Silk
    /// runtime drains the per-frame stream into the overlay's gpu/passes scope, and the console dump
    /// prints only the top-level CPU timers. So every perf question so far has been answered with
    /// one number for the whole frame — which can say "it got slower" and never "the horizon search
    /// is fifty of those milliseconds".
    ///
    /// Totals are cumulative and never cleared, so a mean over samples is the honest per-frame
    /// figure: the early frames while textures stream are in there too, which is exactly why the
    /// sample count is printed beside it rather than hidden.
    /// </remarks>
    private void WritePassBreakdown()
    {
        if (!vk.GpuTimestampsSupported)
        {
            Console.WriteLine("[VulkanSponza] GPU timestamps unsupported on this device — no pass breakdown.");
            return;
        }

        var totals = vk.GpuPassTotals;
        if (totals.Count == 0)
        {
            Console.WriteLine("[VulkanSponza] no GPU pass timings resolved.");
            return;
        }

        Console.WriteLine("[VulkanSponza] GPU ms per pass (mean over resolved frames):");
        double frameTotal = 0;
        foreach (var entry in totals.OrderByDescending(e => e.Value.Samples > 0 ? e.Value.TotalMs / e.Value.Samples : 0))
        {
            if (entry.Value.Samples == 0) continue;
            var mean = entry.Value.TotalMs / entry.Value.Samples;
            frameTotal += mean;
            Console.WriteLine($"  {mean,8:0.000} ms  {entry.Key}  (n={entry.Value.Samples})");
        }
        Console.WriteLine($"  {frameTotal,8:0.000} ms  TOTAL");
        Console.WriteLine(
            "  NOTE: on a tile-based GPU (Apple/MoltenVK) these bracket ENCODER submission, not the "
            + "deferred tiled execution, so they do not sum to the frame. Compare them to each other, "
            + "and use paired A/B runs (--no-ao and friends) for absolute cost.");
    }

    /// <summary>Writes the ambient-visibility buffer as two PNGs: visibility, and bent normal.</summary>
    /// <remarks>
    /// <b>Raw, not tonemapped, and that is the point.</b> Visibility is a fraction and a bent normal
    /// is a direction; running either through exposure and a film curve would turn the one buffer
    /// that can answer "is this term correct" into another picture that merely looks plausible.
    /// The encode is a plain linear-to-byte so a mid-grey pixel means 0.5.
    /// </remarks>
    private void WriteAmbientShot(string path)
    {
        var pixels = vk.ReadTexture(
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

    private void RecordPresentPass(RenderCommandList commandList)
    {
        var hdrTex = graph.GetColorTexture(hdrHandle);
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
                new[] { new ShaderTextureBinding("uHdr", hdrTex, Slot: 0) },
                push));
    }

    private static byte[] ModelPushBytes(Matrix4x4 model)
    {
        var bytes = new byte[64];
        MemoryMarshal.Write(bytes.AsSpan(0, 64), in model);
        return bytes;
    }

    // Opaque shadow caster push: [model | cascadeViewProj] = 128 bytes,
    // matching shadow.vert's PushConstants block.
    private static byte[] ShadowOpaquePushBytes(Matrix4x4 model, Matrix4x4 cascadeViewProj)
    {
        var bytes = new byte[128];
        MemoryMarshal.Write(bytes.AsSpan(0, 64), in model);
        MemoryMarshal.Write(bytes.AsSpan(64, 64), in cascadeViewProj);
        return bytes;
    }

    // Mask shadow caster push: [model | cascadeViewProj | alphaParams] = 144
    // bytes, matching shadow_mask's PushConstants block. alphaParams.xy =
    // (alphaCutoff, baseColorAlpha). Rents a pooled buffer (reset per frame via
    // maskPushCursor) rather than allocating, since these differ per draw.
    private byte[] RentMaskPush(
        Matrix4x4 model, Matrix4x4 cascadeViewProj, float alphaCutoff, float baseColorAlpha)
    {
        if (maskPushCursor >= maskPushPool.Count) maskPushPool.Add(new byte[144]);
        var bytes = maskPushPool[maskPushCursor++];
        MemoryMarshal.Write(bytes.AsSpan(0, 64), in model);
        MemoryMarshal.Write(bytes.AsSpan(64, 64), in cascadeViewProj);
        var alphaParams = new Vector4(alphaCutoff, baseColorAlpha, 0f, 0f);
        MemoryMarshal.Write(bytes.AsSpan(128, 16), in alphaParams);
        return bytes;
    }

    // --- IInputHandler ----------------------------------------------------
}
