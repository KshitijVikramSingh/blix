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
    // Resolve a selectable path ("scene/<bucket>/<i>") to its LOD-margin array
    // slot. Returns false for unknown buckets / out-of-range indices.
    private bool TryResolveMargin(string path, out float[] arr, out int index)
    {
        arr = System.Array.Empty<float>();
        index = -1;
        var parts = path.Split('/');
        if (parts.Length != 3 || !int.TryParse(parts[2], out index)) return false;
        arr = parts[1] switch
        {
            "opaque" => opaqueLodMargins,
            "blend" => blendLodMargins,
            _ => System.Array.Empty<float>(),
        };
        return index >= 0 && index < arr.Length;
    }

    // --- IDebuggable ------------------------------------------------------
    // Controls are read-back: the returned value feeds this frame's render.
    public void Debug(DebugContext debug)
    {
        // Cmd+C toggles this; Debug() runs unconditionally so it re-applies.
        debug.State.Enabled = overlayEnabled;
        // Overlay off → emit nothing. The panels gate on State.Enabled, but the
        // debug-line pass just renders whatever's queued, so we must skip the
        // Draw emissions too or the cascade/sun gizmos linger when hidden.
        if (!overlayEnabled) return;

        // Live tuning, grouped by scope. Controls are read-back: the returned
        // value feeds this frame's render (Debug() runs before OnRender).
        using (debug.Scope("Sun"))
        {
            var deg = 180f / MathF.PI;
            sunYaw   = debug.Controls.Float("Yaw (deg)", sunYaw * deg, -180f, 180f) / deg;
            sunPitch = debug.Controls.Float("Pitch (deg)", sunPitch * deg, -89f, -1f) / deg;
            sunStrength = debug.Controls.Float("Strength (x measured)", sunStrength, 0f, 8f);
            debug.Values.Value("sun-irradiance", $"{EffectiveSunIrradiance.X:0.00} ({sunIrradiance.X:0.00} measured)");
            UpdateSunDirection();
        }
        // Shadows + Render scopes are now [Tune]-tagged settings objects
        // (ShadowsSettings / RenderSettings), auto-paneled by tuneObjects below.
        // Shader-uniform tunables (//@tune in lit.frag): sun/ambient intensity,
        // metallic/normal/slope/glass thresholds, indirect-shadow base/range,
        // cascade-viz. Auto-built + read back here — grouped by their UBO block.
        tunePanel.BuildControls(debug);
        tuneObjects.BuildControls(debug);   // [Tune]-tagged CPU settings (Fog, …)

        // --- Live selection (ephemeral) -------------------------------------
        // Left-click picks a primitive; Cmd-click adds. Drag LOD margin to
        // coarsen/sharpen the whole selection at once (set-all); nothing is
        // saved. The framework highlights the primary; tint the rest here.
        if (selection.Count > 0)
        {
            using (debug.Scope("Selection"))
            {
                debug.Values.Value("count", selection.Count);
                var repPath = primarySelection ?? selection.First();
                var cur = TryResolveMargin(repPath, out var rArr, out var rIdx) ? rArr[rIdx] : 1f;
                var next = debug.Controls.Float("LOD margin (×px)", cur, 0f, 8f);
                if (next != cur)
                {
                    foreach (var p in selection)
                        if (TryResolveMargin(p, out var a, out var ix)) a[ix] = next;
                }
            }
        }

        using (debug.Scope("Cascades"))
        {
            cullEnabled   = debug.Controls.Toggle("Frustum cull", cullEnabled);
            cullMargin    = debug.Controls.Float("Cull margin", cullMargin, 0f, 5f);
            // Far distance of each cascade; clamped into ascending-ish bands so
            // the splits stay ordered as you drag.
            cascadeSplits[1] = debug.Controls.Float("Far: cascade 0", cascadeSplits[1], 1f, 20f);
            cascadeSplits[2] = debug.Controls.Float("Far: cascade 1", cascadeSplits[2], cascadeSplits[1], 50f);
            cascadeSplits[3] = debug.Controls.Float("Far: cascade 2", cascadeSplits[3], cascadeSplits[2], 150f);
        }
        // Exposure / tonemap / fly-speed / LOD-error budget are [Tune] on
        // RenderSettings (auto-paneled above). Vsync stays manual — it's a
        // device property, not a field: FIFO (capped, no tearing) vs Mailbox
        // (uncapped; tears on MoltenVK). Toggling recreates the swapchain.
        using (debug.Scope("Render"))
        {
            vk.VsyncEnabled = debug.Controls.Toggle("Vsync", vk.VsyncEnabled);
            // <b>Named, because a number is not a control.</b> Ten channels behind a 0..9 slider
            // meant the only way to know what 7 was involved reading the shader, which makes the
            // person at the keyboard — the one who can actually see the image — the one least able
            // to use the instrument. Controls.Enum has existed the whole time.
            vizChannel = debug.Controls.Enum("Show", (int)MathF.Round(vizChannel), VizChannelNames);
        }

        // The indirect solve's cost is rays x probes x march, divided by period, and every one of
        // those was a compile-time constant measured by rebuilding between runs. That is how the
        // grid's density grade stayed thrown away on this side for as long as it did: the two
        // readings were never on screen at the same time. They are controls now.
        if (skyVisibilityEnabled)
        {
            // <b>What one probe holds, with nothing multiplied into it.</b> An indirect term reaches
            // the eye only after albedo, AO and visibility have each taken a share, so "the bounce
            // looks weak" and "the bounce IS weak" were indistinguishable from the image alone.
            using (debug.Scope("Probes"))
            {
                showProbes    = debug.Controls.Toggle("Show probes", showProbes);
                probeRadius   = debug.Controls.Float("Radius (m)", probeRadius, 0.02f, 0.4f);
                probeExposure = debug.Controls.Float("Exposure", probeExposure, 0.1f, 20f);
                probeField    = debug.Controls.Enum("Field", (int)probeField,
                    new[] { "Bounce radiance", "Sky visibility", "Usefulness", "Reachable (red = rejected)" });
            }

            using (debug.Scope("Indirect"))
            {
                injectDensity = debug.Controls.Toggle("Density march", injectDensity);
                injectRays    = debug.Controls.Float("Rays / probe", injectRays, 8f, 64f);
                injectPeriod  = debug.Controls.Float("Refresh period", injectPeriod, 4f, 64f);
                injectTranslucency = debug.Controls.Float("Translucency", injectTranslucency, 0f, 1f);
                // 0 = never sleep, which is the honest A/B against everything before this.
                probeSleepFrames = MathF.Round(debug.Controls.Float("Sleep after (frames)", probeSleepFrames, 0f, 600f));
            }

            // The two cloth numbers were guesses written into a patch file, and a patch is cooked —
            // so judging them meant a re-cook per attempt, which is not judging. These override the
            // cooked values live for every sheened material at once, to FIND the number; the found
            // number then goes back into the patch, where it belongs.
            using (debug.Scope("Cloth"))
            {
                clothOverride   = debug.Controls.Toggle("Override the patch", clothOverride);
                sheenRoughness  = debug.Controls.Float("Sheen roughness", sheenRoughness, 0.05f, 1f);
                diffuseTransmit = debug.Controls.Float("Diffuse transmission", diffuseTransmit, 0f, 1f);
                debug.Values.Value("probe-refresh", $"{MathF.Round(injectRays)} rays every {MathF.Round(injectPeriod)}f");
            }
        }

        debug.Values.Value("shadow-map", $"{ShadowMapSizes[0]}/{ShadowMapSizes[1]}/{ShadowMapSizes[2]}");
        debug.Values.Value("splits-m", $"{cascadeSplits[1]:0}/{cascadeSplits[2]:0}/{cascadeSplits[3]:0}");
        // One shadow texel in WORLD units, per cascade — the quantity the map size and the splits
        // jointly imply, and the one that sets both the acne offset and the filter width. It was
        // computed every frame and never shown, so a cascade whose texel had grown to a third of a
        // metre looked, in the overlay, exactly like one whose texel was two centimetres.
        debug.Values.Value("cascade-texel-m",
            $"{cascadeTexelWorld[0]:0.000}/{cascadeTexelWorld[1]:0.000}/{cascadeTexelWorld[2]:0.000}");
        // Per-cascade caster counts after frustum cull (one frame stale — set
        // during the previous OnRender's graph.Execute).
        debug.Values.Value("cascade-casters", $"{cascadeDrawCounts[0]}/{cascadeDrawCounts[1]}/{cascadeDrawCounts[2]} of {opaqueDrawables.Count}");
        // Shadow-map cache hits: R = re-rendered this frame, · = served cached.
        debug.Values.Value("cascade-cache", $"{(cascadeRendered[0] ? 'R' : '·')}{(cascadeRendered[1] ? 'R' : '·')}{(cascadeRendered[2] ? 'R' : '·')}");
        debug.Values.Value("blend-draws", blendDrawables.Count);
        debug.Values.Value("cam-pos", cameraPosition);
        // LOD diagnostic: max levels available + histogram of selected levels
        // across opaque drawables at the current camera + pixel-error budget.
        var maxLevels = 0;
        var hist = new int[8];
        for (var i = 0; i < opaqueDrawables.Count; i++)
        {
            var d = opaqueDrawables[i];
            maxLevels = Math.Max(maxLevels, d.LodIndexCounts.Length);
            var lv = d.PickLod(cameraPosition, LodErrorScale, render.LodErrorPixels * opaqueLodMargins[i]);
            if (lv < hist.Length) hist[lv]++;
        }
        debug.Values.Value("lod-maxlevels", maxLevels);
        debug.Values.Value("lod-hist", $"{hist[0]}/{hist[1]}/{hist[2]}/{hist[3]} (err={render.LodErrorPixels:0.0}px)");

        // --- Perf instrumentation: weigh where the frame actually goes -------
        // CPU-phase split of the bundled `execute` timer. encode is the only
        // phase draw-COUNT moves (recording vkCmds → Metal encoder calls), so
        // it's the number A (batching) / B (GPU-driven indirect) would change;
        // wait is the GPU/vsync throttle (high = GPU-bound, can't be cut by
        // batching); submit is queue submit + present enqueue.
        //
        // The other half — per-pass GPU ms — is surfaced by the runtime under
        // the `gpu/passes` timer scope (Window drains ConsumeAvailableGpuTimings
        // each frame); the periodic console sink prints it. We deliberately do
        // NOT drain it here too — that would race the runtime and steal frames.
        var cpu = vk.LastCpuFrameTiming;
        debug.Values.Value("cpu-wait", $"{cpu.WaitMs:0.00}ms");
        debug.Values.Value("cpu-encode", $"{cpu.EncodeMs:0.00}ms");
        debug.Values.Value("cpu-submit", $"{cpu.SubmitPresentMs:0.00}ms");

        // Spatial gizmos: sun direction + the three cascade ortho boxes.
        // Every primitive below belongs to this view. Scoped rather than assigned: the old
        // per-channel matrix meant a frame could only ever be one world seen one way.
        using var view = debug.Draw.In("main", viewProj);
        debug.Draw.Arrow("sun/dir", -sunDirection * 6f, Vector3.Zero,
            new GraphicsColor(1f, 0.92f, 0.3f, 1f));
        var cascadeTints = new[]
        {
            new GraphicsColor(1f, 0.35f, 0.35f, 0.8f),
            new GraphicsColor(0.35f, 1f, 0.35f, 0.8f),
            new GraphicsColor(0.4f, 0.5f, 1f, 0.8f),
        };
        for (var c = 0; c < CascadeCount; c++)
        {
            debug.Draw.Frustum($"cascade/{c}", cascadeViewProj[c], cascadeTints[c]);
        }

        // <b>The multi-select highlight, which used to draw from the selection block above and
        // therefore from OUTSIDE any view.</b> That threw "Debug primitives were emitted outside any
        // view" and took the process with it — but only ever on the SECOND selection, because the
        // loop skips the primary and a single selection leaves nothing to draw. One Cmd-click was
        // the difference between working and an unhandled exception.
        foreach (var p in selection)
        {
            if (p == primarySelection) continue;
            if (sceneSelection.TryGetBounds(p, out var b))
                debug.Draw.Aabb($"sel/{p}", b.Min, b.Max, MultiSelectColor);
        }
    }
}
