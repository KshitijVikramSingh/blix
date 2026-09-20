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
    private int FillIndirect(
        List<Drawable> drawables, float[] lodMargins, int[] lodState, IndirectBufferHandle buffer,
        Frustum? cull, float margin, float worldErrorBudget = 0f,
        Frustum? receivers = null, Vector3 shadowSweepDir = default)
    {
        // <b>An empty scene has no indirect buffer, and that used to be a crash.</b> When every
        // pack failed to load — stale cooked files after a format bump — the demo reported exactly
        // that, by name, for each pack, and then died three lines later on "Unknown indirect buffer
        // handle 0". A tool that diagnoses its own problem and then throws something unrelated has
        // buried the useful half of its output under the useless half.
        if (drawables.Count == 0) return 0;

        var cmds = MemoryMarshal.Cast<byte, uint>(indirectScratch.AsSpan());
        var visible = 0;
        // <b>What this fill actually handed the GPU, so a cascade's cost can be split.</b> Isolation
        // says cascade 0 costs 6.3 ms and cascade 2 costs 2.6, and there are two candidate reasons —
        // cascade 0 has four times the shadow-map texels to fill, and a texel eight times smaller,
        // which through PickLodWorld buys it far finer geometry. Timing cannot separate those;
        // counting the triangles can, and then only one of them needs a fix.
        fillIndirectTriangles = 0;
        // <b>--ab lod prices the whole LOD system in one arm.</b> Every list selects through this
        // one function — the camera pass, the blend pass and all three shadow cascades — so the off
        // phase is the renderer with NO level of detail rather than with a different budget.
        // Zero is PickLod's own "full detail" case, so the off arm goes through the identical code
        // path as a user dragging the budget to nothing, not a second selection rule beside it.
        var errorPixels = abMode == "lod" ? LodArmPixels : render.LodErrorPixels;
        for (var i = 0; i < drawables.Count; i++)
        {
            var d = drawables[i];
            var vis = cull is not { } f || f.Intersects(d.Bounds, margin);
            // <b>A caster only matters if its shadow can land somewhere the camera can see.</b>
            // Culling against the cascade's own box asks "is this object lit", which in an
            // overhead-sun scene is nearly everything — cascade-casters read 5406/5420/5420 of
            // 5420, and the maps were taking 8.86M triangles against the camera's 166,557. The
            // question worth asking is different: sweep the caster's bounds along the light
            // direction, and if that volume misses the camera frustum then nothing it darkens is
            // on screen. Conservative by construction, because the swept box contains the true
            // shadow volume.
            if (vis && receivers is { } rf)
            {
                // <b>Swept until the shadow leaves the SCENE, not until it leaves the cascade.</b>
                // The first version swept by the cascade's far distance, which is a statement about
                // the camera and not about the light: a roofline caster at 17 m with the sun at 53
                // degrees throws its shadow about 21 m before reaching the floor, so cascade 0's
                // 14 m sweep cut it — while the shadow itself landed well inside cascade 0. The
                // result was patches of missing shadow that came and went with the view angle and
                // that nothing on screen could affect, because nothing on screen controlled it.
                //
                // The honest bound is where the swept box exits the scene bounds, which is tighter
                // for a caster near the floor than for one under the roof — exactly the right shape,
                // since a low caster genuinely cannot shadow much.
                var sweep = shadowSweepDir * SceneExitDistance(d.Bounds, shadowSweepDir);
                var swept = new Bounds3(
                    Vector3.Min(d.Bounds.Min, d.Bounds.Min + sweep),
                    Vector3.Max(d.Bounds.Max, d.Bounds.Max + sweep));
                vis = rf.Intersects(swept, margin);
            }
            // Per-primitive LOD margin (live-tunable) scales the global px budget.
            // A world budget means this list is being drawn into something orthographic, where
            // camera pixels are not the unit of error. Nothing else about the fill changes.
            var lod = worldErrorBudget > 0f
                ? d.PickLodWorld(worldErrorBudget * lodMargins[i], lodState[i])
                : d.PickLod(cameraPosition, LodErrorScale, errorPixels * lodMargins[i], lodState[i]);
            lodState[i] = lod;
            var o = i * 5;
            cmds[o + 0] = (uint)d.LodIndexCounts[lod]; // indexCount
            cmds[o + 1] = vis ? 1u : 0u;               // instanceCount (0 = culled)
            cmds[o + 2] = (uint)d.LodFirstIndex[lod];  // firstIndex
            cmds[o + 3] = (uint)d.BaseVertex;          // vertexOffset
            cmds[o + 4] = 0;                           // firstInstance
            if (vis) { visible++; fillIndirectTriangles += d.LodIndexCounts[lod] / 3; }
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
        // <b>One clock for everything a measurement has to reproduce.</b> framesRendered counts
        // loading frames and framePeriodCount+flatPeriodCount counts them too outside --ab (a
        // non-ab run files every loading frame under flatPeriodCount), so both moved with how long
        // texture streaming happened to take. The orbit rode one and the capture deadline rode the
        // other, which meant two runs of the SAME command captured from two different points on
        // the circle — and a paired comparison between them was reading a camera move as a result.
        if (fullyLoaded) postLoadFrames++;
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

        SampleGpuPassTimes();

        // Built once, before the cascades, because they need it too: a caster is culled against
        // where its shadow could FALL, and that is the camera's frustum rather than the light's.
        cameraFrustumThisFrame = Frustum.FromViewProjection(Matrix4x4.Transpose(viewProj));

        // <b>A clip-space translation, so the offset is exact without touching the projection.</b>
        // Post-multiplying by a translation adds jitter * w to clip.xy, which is precisely a
        // sub-pixel shift of the whole frustum — the alternative is editing the perspective matrix's
        // terms and getting a sign convention wrong in a way nothing reports.
        //
        // Halton (2,3) rather than a random pair: successive frames need to land at points a
        // low-discrepancy sequence spreads evenly over the pixel, which is the same argument the fog
        // slice jitter and the GTAO slice rotation both rest on. Accumulating identical samples
        // reduces nothing.
        if (render.Taa > 0f && frame.Width > 0 && frame.Height > 0)
        {
            var jx = (Halton(framesRendered, 2) - 0.5f) * 2f / frame.Width;
            var jy = (Halton(framesRendered, 3) - 0.5f) * 2f / frame.Height;
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
            // <b>The JITTERED matrix, and only here.</b> Cascade fitting, frustum culling and LOD
            // all keep the true one: a sub-pixel offset is meaningless to them and feeding it in
            // would make a cascade refit, and a cache miss, every frame for nothing.
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
            // w gates the leak metric's march: 0 means no occupancy grid shipped and channel 21
            // has nothing to be the truth about.
            new("uOccupancyDims",    new Vector4Uniform(new Vector4(occX, occY, occZ, occX > 0 ? 1f : 0f))),
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
            FillIndirect(opaqueDrawables, opaqueLodMargins, opaqueLodState, opaqueIndirect, cull: null, margin: 0f);
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
            // <b>The cache keyed on the matrix alone, and the scene can change without it.</b> A
            // static camera leaves every cascade VP constant, so each rendered ONCE -- and that once
            // happened while the packs were still streaming, with opaqueDrawables empty and
            // FillIndirect returning zero draws. The depth target then held an empty shadow map for
            // as long as nobody moved: no canopy self-shadowing, no contact shadows, a scene lit
            // flat by an unshadowed sun. It showed as "sun-cascade0 (n=1)" in the pass breakdown
            // next to gtao's n=697, and as "cascade-casters 0/0/0 of 0", both of which I read as
            // instrument noise before reading them as the answer.
            //
            // Keyed on the caster COUNT as well, so streaming a pack in invalidates it. Cheap, and
            // it fails in the safe direction: a spurious redraw costs one pass, a missed one costs
            // every shadow in the frame.
            // <b>And on the LOD budget, because that changes the GEOMETRY the casters are made of.</b>
            // Dragging "Lod error pixels" with the camera still left every shadow map holding the
            // meshes selected at the old budget, with nothing on screen to say so — and it would
            // have quietly made --ab lod measure its off arm against cached shadows built by its
            // on arm.
            // The cascade's own budget, since that is what decides ITS geometry — it moves when the
            // cascade refits, which is exactly when the cached map has to be rebuilt anyway.
            var lodKey = abMode == "lod" && LodArmPixels <= 0f ? 0f : cascadeTexelWorld[ci] * shadowLodTexels;
            // <b>And on the CAMERA, now that the caster set depends on it.</b> A cascade is fitted to
            // the camera's frustum slice and texel-snapped, so a small rotation can leave the light
            // matrix bit-identical while the set of casters whose shadows can reach the screen has
            // changed entirely. Keyed on the light matrix alone, the cache would then serve a map
            // built for a view that no longer exists — shadows simply missing, with nothing to say
            // why. Same family as the two cache bugs already fixed here; caching is a claim about
            // what the result depends on, and this changed what it depends on.
            // Not due this frame: the map from an earlier frame still covers, and the lit pass is
            // already being handed the matrix it was rendered with.
            if (!cascadeDue[ci])
            {
                cascadeRendered[ci] = false;
                continue;
            }
            if (vp == cachedCascadeViewProj[ci]
                && opaqueDrawables.Count == cachedCascadeCasters[ci]
                && lodKey == cachedCascadeLod[ci]
                && (!shadowCasterCull || viewProj == cachedCascadeCamera[ci]))
            {
                cascadeRendered[ci] = false;
                continue;
            }
            cachedCascadeViewProj[ci] = vp;
            cachedCascadeCasters[ci] = opaqueDrawables.Count;
            cachedCascadeLod[ci] = lodKey;
            cachedCascadeCamera[ci] = viewProj;
            cascadeRendered[ci] = true;
            // Frustum.FromViewProjection expects a column-vector clip matrix
            // (clip = M·world); our cascade VP is the System.Numerics
            // row-vector form (clip = Vector4.Transform(world, M)), so transpose
            // to hand it the clip-coordinate generators as rows.
            var cascadeFrustum = Frustum.FromViewProjection(Matrix4x4.Transpose(vp));
            // Fill this cascade's indirect buffer (per-cascade frustum cull → 0
            // instanceCount; same SSE LOD as the lit/pre-pass so shadow depth
            // matches the shaded silhouette). Then one indirect draw per group.
            cascadeTriangles[ci] = 0;
            cascadeDrawCounts[ci] = FillIndirect(
                opaqueDrawables, opaqueLodMargins, cascadeLodState[ci], cascadeIndirect[ci],
                cull ? cascadeFrustum : null, margin,
                // <b>The cascade's own texel, not the camera's pixel.</b> The comment that used to
                // sit here said "same SSE LOD as the lit/pre-pass so shadow depth matches the shaded
                // silhouette" — which is a requirement between the DEPTH PRE-PASS and the lit pass,
                // where two passes rasterise the same triangles into the same buffer. A shadow map
                // is its own render of its own geometry, compared against nothing.
                //
                // The --ab lod off arm is the exception: it means "no level of detail anywhere", and
                // a cascade quietly keeping its own would make the arm measure less than it claims.
                abMode == "lod" && LodArmPixels <= 0f ? 0f : cascadeTexelWorld[ci] * shadowLodTexels,
                // The sweep is this cascade's far distance: a shadow that has travelled further than
                // the cascade covers has left it, and the next cascade owns that ground.
                shadowCasterCull && !(abMode == "castercull" && AbOffPhase) ? cameraFrustumThisFrame : null,
                Vector3.Normalize(sunDirection));
            cascadeTriangles[ci] = fillIndirectTriangles;
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
            EnsureFroxelGrid(frame.Width, frame.Height);
            Matrix4x4.Invert(viewProj, out var invViewProj);
            var froxelUniforms = new ShaderUniform[]
            {
                new("uInvViewProj",   new Matrix4x4Uniform(invViewProj)),
                new("uCamPos",        new Vector4Uniform(new Vector4(cameraPosition, fog.Far))),
                new("uCamForward",    new Vector4Uniform(new Vector4(cameraForward, fog.Density))),
                // The froxel pass scatters the same sun, so it takes the same measured irradiance
                // — all three channels of it. It used to take the red channel as a scalar and a
                // white colour, so a warm sun scattered grey light through the air.
                new("uSunDir",        new Vector4Uniform(new Vector4(sunDirection, 1f))),
                new("uSunColor",      new Vector4Uniform(new Vector4(EffectiveSunIrradiance, fog.Scatter))),
                // z: whether the indirect fields are there to be scattered. Without them the
                // medium falls back to the flat ambient floor, which is all it ever had.
                new("uFogParams",     new Vector4Uniform(new Vector4(
                    fog.PhaseG, fog.Ambient, fogIndirect ? 1f : 0f, (float)time.Total))),
                new("uMedium",        new Vector4Uniform(new Vector4(
                    fog.HeightFalloff, fog.Noise, 0f, 0f))),
                // <b>History is refused on the first frame after it could be wrong.</b> The pair
                // starts uninitialised, and it also goes stale whenever the grid is re-created for
                // a resize — blending into either is blending into whatever the allocator left.
                new("uTemporal",      new Vector4Uniform(new Vector4(
                    fogHistoryValid ? fog.Temporal : 0f, FogJitter(), fog.ShowHistory ? 1f : 0f, 0f))),
                new("uPrevViewProj",  new Matrix4x4Uniform(fogHistoryValid ? prevFogViewProj : viewProj)),
                new("uPrevCamPos",    new Vector4Uniform(new Vector4(
                    fogHistoryValid ? prevFogCamPos : cameraPosition, 0f))),
                new("uBoundsMin",     new Vector4Uniform(new Vector4(skyVolumeMin, 0f))),
                new("uBoundsSpan",    new Vector4Uniform(new Vector4(skyVolumeSpan, 0f))),
                new("uProbeDims",     new Vector4Uniform(new Vector4(bounceX, bounceY, bounceZ, 0f))),
                // w = 0: the fog's probe blend keeps the Chebyshev-only visibility test while the
                // lit pass's occlusion dial is being measured. One pass at a time.
                new("uOccupancyDims", new Vector4Uniform(new Vector4(occX, occY, occZ, 0f))),
                // No splits: the fog picks its cascade by containment through the shared lookup,
                // exactly as the lit pass does. It used to take the view-depth bounds and select on
                // them, which silently stopped matching the surfaces when the lit pass moved.
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

        // Fill the camera opaque indirect commands once per frame (LOD by SSE, frustum-culled);
        // both the depth pre-pass and the lit pass consume this buffer — they draw the identical
        // opaque set at identical LODs, and now the identical VISIBLE set.
        //
        // <b>The camera pass submitted the whole scene, every frame, in every direction.</b> Only
        // the shadow cascades ever tested a frustum. That was a defensible omission at 424
        // primitives, where each one spanned enough of the scene that a frustum test rejected almost
        // nothing — and it silently stopped being true the moment the cook started splitting on
        // extent. The census measures what was being thrown at the GPU to be clipped: 55% of
        // submitted triangles off-screen at the default camera, and 96% on the measurement orbit,
        // which is a camera standing inside a building looking at one wall of it.
        //
        // Culling here zeroes instanceCount rather than removing the command, exactly as the
        // cascades do, so what is saved is vertex and binning work and not draw calls.
        var cameraFrustum = cullEnabled && !(abMode == "cull" && AbOffPhase)
            ? cameraFrustumThisFrame
            : (Frustum?)null;
        //
        // The margin is insurance, not tuning. A chunk whose bounds sit exactly on a frustum plane
        // can fall either way on floating-point noise, and at the screen edge that reads as geometry
        // blinking in and out as you turn. Half a metre of slack costs a fraction of a percent of
        // the rejections and removes the whole class.
        FillIndirect(opaqueDrawables, opaqueLodMargins, opaqueLodState, opaqueIndirect, cameraFrustum, margin: CameraCullMargin);
        cameraTriangles = fillIndirectTriangles;
        // <b>Exactly one revolution, started at the top of one.</b> Averaging "however many frames
        // happened" over a closed path averages whichever ARC the run covered — two runs of the
        // same length reported 450,076 camera triangles and 590,138 with an identical camera, which
        // made the counted instrument as run-dependent as the timing it replaced. Sampling is armed
        // at a period boundary and stops after a full period, so the window is the same stretch of
        // path every time and the mean belongs to the configuration.
        // Any CONTIGUOUS period covers the whole closed path, so the window does not need to start
        // at a period boundary — and requiring one was worse than not averaging at all: it armed at
        // the first multiple of the period after loading finished, load time varies, and two runs
        // collected 1 frame each because the boundary landed near the end of the run.
        if (!orbit || triangleFrames < OrbitFrames)
        {
            for (var c = 0; c < CascadeCount; c++) cascadeTriangleSum[c] += cascadeTriangles[c];
            cameraTriangleSum += fillIndirectTriangles;
            triangleFrames++;
        }
        if (blendDrawables.Count > 0) FillIndirect(blendDrawables, blendLodMargins, blendLodState, blendIndirect, cameraFrustum, margin: CameraCullMargin);

        // Depth pre-pass: same non-blend set as the lit pass (no cull, so the
        // depth the lit pass loads covers exactly what it shades), depth only.
        // Per group: mask binds the material (set 2 albedo) for the alpha
        // discard + the mask pipeline; opaque needs only set 0 + model push.
        // --ab prepass off-phase: record the pass (it still clears depth) but draw nothing into it,
        // so the lit pass below establishes depth itself through the writing pipelines.
        var skipPrepass = noPrepass || (abMode == "prepass" && AbOffPhase);
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
        // <b>--ab inject prices the injection compute pass.</b> Nothing did, and it is now the
        // heaviest thing the sky path does: 256 rays per probe rather than 64, marched through the
        // occupancy grid with a nested sun walk per hit. Skipping it leaves the atlases exactly as
        // they were, so the lit pass still reads a valid field and the arms differ by the DISPATCH
        // alone, which is the quantity in question.
        var skipInjectNow = skipInject || (abMode == "inject" && AbOffPhase);
        if (probePingPong && bounceReady && skyVisibilityEnabled && !skipInjectNow) bounceWrite ^= 1;
        if (bounceReady && skyBounceBinding >= 0)
        {
            passBindings[skyBounceBinding] = new ShaderTextureBinding(
                "uSkyBounce", bounceTextures[BounceRead], Slot: 7);
            passBindings[skyBounceBinding + 1] = new ShaderTextureBinding(
                "uSkyBounceDepth", bounceDepthTextures[BounceRead], Slot: 13);
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
                new("uAlbedoDims", new Vector4Uniform(new Vector4(albX, albY, albZ, injectTranslucency))),
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
                    framesRendered, MathF.Round(injectPeriod),
                    ProbeSleepNow > 0f ? 1f / ProbeSleepNow : 0f,
                    probePingPong ? 1f : 0f))),
            };
            graph.Dispatch(injectPassHandle, new DispatchCommand(
                injectPipeline,
                // One workgroup per probe now, not a 4x4x4 block of them: a workgroup's 64 threads
                // are the 64 rays, shared between all 36 texels of that probe's tile.
                bounceX * bounceY * bounceZ, 1, 1,
                injectUniforms, BounceBindings()));
        }

        // Which probes this frame needs, from the depth the pre-pass just wrote. Marks are read by
        // the NEXT frame's injection; see probe_usage.comp for why this is not done in lit.frag.
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
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth), Slot: 1),
                new ShaderTextureBinding("uProbeUsage", probeUsageTexture, Slot: 2),
            };
            // One invocation per 8x8 pixel block; the shader strides by 8 again inside.
            var groupsX = (frame.Width / 8 + 7) / 8;
            var groupsY = (frame.Height / 8 + 7) / 8;
            graph.Dispatch(probeUsagePassHandle, new DispatchCommand(
                probeUsagePipeline, groupsX, groupsY, 1, usageUniforms, usageBindings));
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
                    projectionScale, aoDebug, ambient.Slices))),
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
                    (float)((framesRendered * 0.6180339887) % 1.0) * MathF.PI,
                    ambient.ShowRejection ? 1f : 0f, ambient.Steps))),
                new("uPrevViewProj",  new Matrix4x4Uniform(ambientHistoryValid ? prevAmbientViewProj : viewProj)),
            };
            var hiZBindings = new ShaderTextureBinding[HiZLevels + 1];
            for (var level = 0; level < HiZLevels; level++)
            {
                hiZBindings[level] = new ShaderTextureBinding(
                    $"uHiZ[{level}]", graph.GetColorTexture(hiZHandles[level]), Slot: 1, ArrayIndex: level);
            }
            hiZBindings[HiZLevels] = new ShaderTextureBinding(
                "uHistory", graph.GetColorTexture(ambientDenoisedHandle), Slot: 2);
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
                            bounceReady ? bounceTextures[BounceRead] : brdfLutTexture, Slot: 0),
                        new ShaderTextureBinding("uSkyVisibility", skyVisibilityTexture, Slot: 1),
                        new ShaderTextureBinding("uOccupancy", occupancyTexture, Slot: 2),
                        new ShaderTextureBinding("uSkyBounceDepth",
                            bounceReady ? bounceDepthTextures[BounceRead] : brdfLutTexture, Slot: 3),
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

        // Resolve before the graph executes, so the pass is recorded with the rest of the frame.
        RecordTaaResolve();
        graph.Execute(commandList);

        RecordPresentPass(commandList);
        taaWrite = taaWriteNext;

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
        var measuredFrames = postLoadFrames;
        if (shotPath is { } path && !shotWritten && fullyLoaded && !AbOffPhase
            && framePeriodCount >= 60 && measuredFrames >= shotFrame)
        {
            shotWritten = true;
            VerifyHiZ();
            OcclusionCensus();
            ProbeReachCensus();
            WriteAmbientShot(path);
            // <b>The RAW buffer as well, because the denoised one cannot answer questions about the
            // search.</b> Every reading taken off the denoised target is a depth-weighted average of
            // a 3x3 neighbourhood, which is exactly right for lighting and exactly wrong for "what
            // did this pixel's horizon search conclude" — and it silently turned a normal probe into
            // a probe of nine blended normals.
            WriteAmbientRaw(Path.ChangeExtension(path, null) + ".raw.png");
            WriteSceneShot(Path.ChangeExtension(path, null) + ".scene.png");
            LeakCensus();
            WritePassBreakdown();
            WriteLodCensus();
            WriteProbeCensus();
            if (probeReference) WriteProbeReference(probeCount: 12, paths: 4096, bounces: refBounces);
            WriteFrameStats();
            host.RequestClose();
        }
    }

    /// <summary>
    /// How much of the probe blend, over everything the camera can see, arrives through a wall.
    /// </summary>
    /// <remarks>
    /// <b>The number that was being judged by eye.</b> Probe leaking has been diagnosed all evening
    /// from screenshots — "colour bleeds from the back of the interior walls" — and a screenshot
    /// cannot say whether a change made it better by a third or worse by a tenth. Channel 21 has
    /// each shaded pixel march the occupancy grid to every probe voting on it and report the share
    /// of blend weight that is voting from behind geometry; this averages that over the frame.
    ///
    /// The distribution matters more than the mean and that is why both are printed. A leak is
    /// visible where it is CONCENTRATED — one wall taking a third of its bounce from the far side
    /// reads as a coloured stain, while the same total weight spread thinly over the whole atrium
    /// reads as nothing at all. A mean that falls while the tail grows is a change that made the
    /// image worse, and only the tail says so.
    ///
    /// Requires --viz 21: outside that channel the march does not run, because eight marches of up
    /// to 24 steps per pixel is not something to pay for in a frame nobody is measuring.
    /// </remarks>
    private void LeakCensus()
    {
        if (vizChannel < 20.5f || vizChannel > 21.5f) return;

        var pixels = vk.ReadTexture(
            graph.GetColorTexture(hdrHandle), out var width, out var height, out var format);
        if (format != TextureFormat.R11G11B10F) return;

        var measured = new List<float>(width * height / 4);
        double sum = 0;
        double confidenceSum = 0;
        for (var i = 0; i < width * height; i++)
        {
            var packed = BitConverter.ToUInt32(pixels, i * 4);
            // <b>Green AND a blue of exactly zero, because the skybox is also in this buffer.</b>
            // The skybox pass never runs lit.frag, so it never writes the marker — but the sky it
            // writes is bright, and its green channel sails past any "is this 1.0" test. Every sky
            // pixel therefore counted as a measured surface reporting no leak, which is why the
            // census reported all 4,665,600 pixels as probe-lit and diluted the mean with a third
            // of a frame of guaranteed zeroes. Channel 21 writes blue 0 exactly and the sky cannot.
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
    /// <summary>What the injector actually wrote into the reachability channel, per probe.</summary>
    /// <remarks>
    /// <b>Because modelling the shader on the CPU disagreed with the picture, twice.</b> An offline
    /// replica of the march predicted 75-100% of the probes inside the cypress reachable at every
    /// height; the probe view showed the lower half rejected. One of the two is wrong and no amount
    /// of reasoning settles which — so this reads the channel back off the GPU and reports it by
    /// height, which is the axis the disagreement is on.
    /// </remarks>
    private void ProbeReachCensus()
    {
        if (!bounceReady) return;
        var pixels = vk.ReadTexture(
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
        // <b>The whole atlas, as floats, because the visibility test cannot be judged from a
        // summary.</b> Every claim made about Chebyshev so far came from a CPU replica of the depth
        // map; the map itself is right here and the replica is a guess about it. Dumping mean and
        // mean-square per texel lets the leak be computed against what the GPU actually holds.
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
            var irr = vk.ReadTexture(bounceTextures[BounceRead], out var iw, out var ih, out _);
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
            "indirect"=> "the probe-volume terms (bounce + baked sky visibility)",
            "inject"  => "the bounce injection dispatch",
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
                "same geometry, same process, same thermal state, " +
                // <b>Said out loud, because a still-camera number reads exactly like a moving one.</b>
                // Cascades serve from cache and LOD never switches with the camera parked, so a
                // shadow or LOD figure taken that way is missing most of what it claims to price —
                // and nothing in the printed line used to say which kind it was.
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
    // The grid follows the framebuffer, so a resize re-creates it. It is a descriptor in two live
    // sets and cannot be swapped under work in flight — but a resize already stalls the pipeline,
    // which makes this the cheapest correct place to pay for the idle. Texture ids are never
    // reused, so the destroyed handle cannot come back and match something cached.
    private void EnsureFroxelGrid(int width, int height)
    {
        var (x, y) = FroxelGridSize(width, height);
        if (x == froxelGridX && y == froxelGridY) return;
        vk.WaitIdle();
        var previous = froxelGridTexture;
        froxelGridX = x;
        froxelGridY = y;
        froxelGridTexture = vk.CreateStorageTexture3D(
            x, y, froxelGridZ, TextureFormat.Rgba16F, SamplerDescription.LinearClamp,
            "sponza.froxel_grid");
        vk.DestroyTexture(previous);
        for (var i = 0; i < 2; i++)
        {
            var staleScatter = fogScatterTextures[i];
            fogScatterTextures[i] = vk.CreateStorageTexture3D(
                x, y, froxelGridZ, TextureFormat.Rgba16F, SamplerDescription.LinearClamp,
                $"sponza.fog_scatter{i}");
            vk.DestroyTexture(staleScatter);
        }
        fogHistoryValid = false;
        if (froxelGridBinding >= 0)
        {
            passBindings[froxelGridBinding] =
                new ShaderTextureBinding("uFroxelGrid", froxelGridTexture, Slot: 4);
        }
        Console.WriteLine(
            $"[VulkanSponza] froxel grid {x}x{y}x{froxelGridZ} ({FroxelPixels} px/froxel at {width}x{height})");
    }

    // A low-discrepancy offset in [0,1) for this frame's slice sample, so successive frames land at
    // different depths inside the same segment. R2 rather than Halton: one multiply, no bit
    // reversal, and a better-spread sequence than either for one dimension.
    private float FogJitter() => (float)((framesRendered * 0.7548776662) % 1.0);

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
            // The far corner in this axis is whichever the ray is heading toward.
            var start = d > 0
                ? (a == 0 ? bounds.Max.X : a == 1 ? bounds.Max.Y : bounds.Max.Z)
                : (a == 0 ? bounds.Min.X : a == 1 ? bounds.Min.Y : bounds.Min.Z);
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
        new ShaderTextureBinding("uGrid", froxelGridTexture, Slot: 1),
        new ShaderTextureBinding("uCascadeShadowMaps[0]", graph.GetDepthTexture(cascadeHandles[0]), Slot: 2, ArrayIndex: 0),
        new ShaderTextureBinding("uCascadeShadowMaps[1]", graph.GetDepthTexture(cascadeHandles[1]), Slot: 2, ArrayIndex: 1),
        new ShaderTextureBinding("uCascadeShadowMaps[2]", graph.GetDepthTexture(cascadeHandles[2]), Slot: 2, ArrayIndex: 2),
        new ShaderTextureBinding("uSkyVisibility", skyVisibilityTextures[0], Slot: 3),
        new ShaderTextureBinding("uIrradiance", irradianceCubeTexture, Slot: 4),
        // Never a hole, even before the first solve: a descriptor set with a gap is a device loss,
        // and uFogParams.z is what tells the shader not to read these.
        new ShaderTextureBinding("uAtlas", bounceReady ? bounceTextures[bounceWrite] : brdfLutTexture, Slot: 5),
        new ShaderTextureBinding("uDepthAtlas", bounceReady ? bounceDepthTextures[bounceWrite] : brdfLutTexture, Slot: 6),
        new ShaderTextureBinding("uScatterPrev", fogScatterTextures[fogScatterWrite ^ 1], Slot: 7),
        new ShaderTextureBinding("uScatter", fogScatterTextures[fogScatterWrite], Slot: 8),
        new ShaderTextureBinding("uOccupancy",
            occX > 0 ? occupancyTexture : skyVisibilityTextures[0], Slot: 9),
    };

    private ShaderTextureBinding[] BounceBindings() => new[]
    {
        new ShaderTextureBinding("uAtlas", bounceTextures[bounceWrite], Slot: 1),
        new ShaderTextureBinding("uDepthAtlas", bounceDepthTextures[bounceWrite], Slot: 11),
        new ShaderTextureBinding("uDepthAtlasPrev", bounceDepthTextures[BounceRead], Slot: 12),
        new ShaderTextureBinding("uOccupancy", occupancyTexture, Slot: 2),
        new ShaderTextureBinding("uAlbedo", albX > 0 ? albedoTexture : occupancyTexture, Slot: 5),
        new ShaderTextureBinding("uProbeUsage", probeUsageTexture, Slot: 10),
        // <b>All three bands plus the irradiance cube, because the injector now uses the sky as a
        // SOURCE.</b> uSkyVisibility was bound here and never read by the shader; the bounce chain
        // could only be started by a sunlit surface, so arcades and corridors had no energy put
        // into them at all.
        new ShaderTextureBinding("uSkyVisibility", skyVisibilityTextures[0], Slot: 3),
        new ShaderTextureBinding("uSkyVisibility1", skyVisibilityTextures[1], Slot: 6),
        new ShaderTextureBinding("uSkyVisibility2", skyVisibilityTextures[2], Slot: 8),
        new ShaderTextureBinding("uIrradiance", irradianceCubeTexture, Slot: 9),
        // Last frame's solution, which is what turns a rotation of sweeps into successive bounces
        // AND what lets this dispatch run without the lit pass waiting on it.
        new ShaderTextureBinding("uAtlasPrev", bounceTextures[BounceRead], Slot: 4),
    };

    // --- live GPU pass cost ----------------------------------------------
    // <b>Cumulative totals cannot answer a question somebody is asking with a slider.</b>
    // VulkanGraphicsDevice.GpuPassTotals is summed over the whole process, so a mean over it says
    // what a pass has cost on average since launch — including the frames spent streaming textures.
    // Move "Rays / probe" from 64 to 8 and that mean barely twitches for a minute. Differencing the
    // totals against the previous frame's snapshot gives the cost of the frames since, and a light
    // EMA over that settles in about a second, which is the timescale a hand on a dial works at.
    private readonly Dictionary<string, (double Ms, long Samples)> gpuPassPrev = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> gpuPassMs = new(StringComparer.Ordinal);

    private void SampleGpuPassTimes()
    {
        if (!vk.GpuTimestampsSupported) return;
        foreach (var (name, current) in vk.GpuPassTotals)
        {
            var had = gpuPassPrev.TryGetValue(name, out var previous);
            gpuPassPrev[name] = (current.TotalMs, current.Samples);
            if (!had) continue;
            var frames = current.Samples - previous.Samples;
            if (frames <= 0) continue;
            var perFrame = (current.TotalMs - previous.Ms) / frames;
            gpuPassMs[name] = gpuPassMs.TryGetValue(name, out var ema)
                ? ema + (perFrame - ema) * 0.08
                : perFrame;
        }
    }

    /// <summary>Windowed GPU milliseconds for one pass, or 0 before it has been resolved twice.</summary>
    internal double GpuPassMs(string pass) => gpuPassMs.TryGetValue(pass, out var v) ? v : 0.0;

    /// <summary>The heaviest passes by windowed cost, for the overlay's live breakdown.</summary>
    internal IEnumerable<KeyValuePair<string, double>> GpuPassesByCost() =>
        gpuPassMs.OrderByDescending(e => e.Value);

    /// <summary>Sum of every pass's windowed cost. NOT the frame time — see WritePassBreakdown.</summary>
    internal double GpuPassTotalMs() => gpuPassMs.Values.Sum();

    /// <summary>What the probe field actually holds, against what the inputs say it should.</summary>
    /// <remarks>
    /// <b>"The interior is too dark" is an arithmetic claim and nobody has ever checked it.</b> The
    /// dark half of Sponza is lit by two things and both are measured quantities: the sky, times the
    /// fraction of it a point can see, plus bounce from what the sun does reach. So the field has an
    /// expected magnitude, and if it comes in far under that the answer is a transport bug rather
    /// than a tonemap preference — which is the difference between fixing it and turning a dial that
    /// says "x measured" to 3.4.
    ///
    /// The floor printed here is the SKY alone: sky irradiance times the mean visibility of the
    /// volume. It is a floor and not a target, because every probe should additionally carry bounce.
    /// A field sitting at or below it is carrying no bounce at all.
    /// </remarks>
    private void WriteProbeCensus()
    {
        if (!bounceReady) { Console.WriteLine("[VulkanSponza] probe census: no bounce field."); return; }
        var irr = vk.ReadTexture(bounceTextures[BounceRead], out var w, out var h, out var format);
        if (format != TextureFormat.Rgba16F) { Console.WriteLine("[VulkanSponza] probe census: unexpected atlas format."); return; }

        // The depth atlas alongside it, for the ray closure the march parked in its alpha.
        var depth = vk.ReadTexture(bounceDepthTextures[BounceRead], out var dw, out var dh, out _);
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
        // <b>Binned by how much sky the probe's own cell can see, which is the question.</b> A single
        // median over the whole volume mixes a courtyard probe with one inside a wall and reports
        // something true of neither. The bake measures enclosure per cell; this asks what the solve
        // delivered as a function of it. Light failing to reach the enclosed bins is a transport
        // problem; every bin being uniformly dim is a units problem; and they need opposite fixes.
        if (cellSkyVisibility.Length == bounceX * bounceY * bounceZ)
        {
            // <b>And the mean height, because visibility alone does not say WHERE.</b> A bin at 50%
            // sky could be a courtyard probe over a sunlit floor or an arcade-edge probe under the
            // roofline, and those two have expectations an order of magnitude apart. Without this
            // the deficit I read off the 40-60% bin rested on a guess about which it was.
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
                    // <b>The test.</b> A ray closes on geometry or escapes to sky, so the mean over
                    // a probe's rays should be 1 minus the sky it can see. The bake computed that
                    // visibility independently; a closure well under it is transport being dropped,
                    // and a closure that matches means the field's scale is the scene's, not a bug.
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
    /// <b>The question "is a looser budget worth anything" is answerable without a stopwatch.</b>
    /// A budget's whole effect is which index range each primitive draws from, so the triangles it
    /// submits is an exact, noiseless function of it — no thermal drift, no interquartile range, no
    /// paired runs. If two budgets submit the same geometry then no timing difference between them
    /// can be real, and on this laptop a timing run is the less trustworthy of the two instruments
    /// by a wide margin.
    ///
    /// The saturation column is the one that ends the argument: a primitive already at its OWN
    /// coarsest level cannot be coarsened further by any budget, and with 4 m chunks most of them
    /// have short chains — the simplifier stops at MinLodIndices, and a chunk of a few hundred
    /// triangles reaches that in one or two steps.
    /// </remarks>
    private void WriteLodCensus()
    {
        if (opaqueDrawables.Count == 0) return;
        // <b>The two columns that decide what to do next, and they are counted rather than timed.</b>
        // "chain-bound" is the share of submitted TRIANGLES sitting in primitives already at their
        // own coarsest level — the ceiling on what a longer decimation chain could ever buy, which
        // the per-PRIMITIVE saturation figure badly misreports because primitives are not the same
        // size. "in frustum" is what survives the camera's own frustum, which the camera pass does
        // not currently test at all: with 424 scene-spanning primitives a frustum test rejected
        // almost nothing, and at 4 m chunks that stopped being true without anyone re-checking.
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
            for (var i = 0; i < opaqueDrawables.Count; i++)
            {
                var d = opaqueDrawables[i];
                // No hysteresis here: the census asks what a budget SETTLES at, and the band is a
                // property of how it is approached, not of where it arrives.
                var level = d.PickLod(cameraPosition, LodErrorScale, budget * opaqueLodMargins[i], 0);
                var count = d.LodIndexCounts[level];
                indices += count;
                if (level == d.LodIndexCounts.Length - 1) { saturated++; chainBound += count; }
                if (frustum.Intersects(d.Bounds, 0f)) visible += count;
                if (level < hist.Length) hist[level]++;
            }
            var tris = indices / 3;
            if (budget == 0f) baseline = tris;
            var ratio = baseline > 0 ? (double)tris / baseline : 1.0;
            Console.WriteLine(string.Create(Inv,
                $"    {budget,5:0.##}px  {tris,9:N0}   {ratio,6:0.0%}   " +
                $"{(indices > 0 ? chainBound * 100.0 / indices : 0),10:0.0}%   " +
                $"{(indices > 0 ? visible * 100.0 / indices : 0),9:0.0}%   " +
                $"{saturated * 100.0 / opaqueDrawables.Count,14:0.0}%   " +
                $"{hist[0]}/{hist[1]}/{hist[2]}/{hist[3]}"));
        }
    }

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
        // <b>The MEAN over the run, not the last frame's snapshot.</b> A single frame's counts are
        // taken at wherever the camera happened to stop, and on the orbit two runs of the same
        // length landed far enough apart to report 165,007 camera triangles against 480,333 — which
        // made a counted instrument as unreliable as the timing it was meant to replace. Averaged
        // over every frame, the figure is a property of the configuration again.
        var tf = Math.Max(1, triangleFrames);
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] triangles submitted (mean of {tf} frames): cascades "
            + $"{cascadeTriangleSum[0] / tf:N0}/{cascadeTriangleSum[1] / tf:N0}/{cascadeTriangleSum[2] / tf:N0}, "
            + $"camera {cameraTriangleSum / tf:N0}"));
        Console.WriteLine(string.Create(Inv,
            $"  shadow maps {ShadowMapSizes[0]}/{ShadowMapSizes[1]}/{ShadowMapSizes[2]}, texel {cascadeTexelWorld[0]:0.000}/{cascadeTexelWorld[1]:0.000}/{cascadeTexelWorld[2]:0.000} m, budget {shadowLodTexels:0.0} texels"));

        if (vk.GpuPassIsolation)
        {
            // <b>Two columns, because a scheduled pass separates them.</b> per-run is what an
            // execution costs; per-frame is what the renderer pays, and they differ by exactly the
            // fraction of frames the pass runs in. A cascade updated every fourth frame keeps its
            // per-run cost and drops its per-frame cost fourfold, and only the second column can
            // see that — which is why this exists before the scheduling does.
            var frames = Math.Max(1, vk.GpuIsolationFrames);
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] isolated GPU ms per pass over {frames} frames (own command buffer, fence-waited):"));
            Console.WriteLine("     per-run    per-frame   runs/frame  pass");
            double isoTotal = 0;
            foreach (var e in vk.GpuPassIsolatedTotals
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

        // <b>And what the alternative instrument would cost before it measured anything.</b>
        var floor = vk.MeasureSubmitFloorMs();
        Console.WriteLine(string.Create(Inv,
            $"  submit+fence floor: {floor:0.000} ms — the smallest pass a per-pass command buffer could resolve; anything under this is below that instrument's noise."));
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

    // <b>One of the two resolve passes, alternating.</b> Not recording the other is what makes its
    // target this frame's history — the graph executes only what is recorded, so the unrecorded
    // pass's image survives untouched from the frame before.
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
            new ShaderTextureBinding("uCurrent", graph.GetColorTexture(hdrHandle), Slot: 1),
            new ShaderTextureBinding("uHistory", graph.GetColorTexture(taaHandles[taaWrite ^ 1]), Slot: 2),
            new ShaderTextureBinding("uDepth", graph.GetDepthTexture(SampleableSceneDepth), Slot: 3),
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
