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
    private static readonly System.Globalization.CultureInfo Inv =
        System.Globalization.CultureInfo.InvariantCulture;

    // --- LOD visibility instrument ---------------------------------------
    private bool showLodBoxes;
    private int[] lodLevels = Array.Empty<int>();
    private float[] lodPopAge = Array.Empty<float>();
    private const float PopHoldSeconds = 1.5f;
    private static readonly GraphicsColor PopColor = new(1f, 1f, 1f, 1f);
    // Level 0 is never drawn, so the first entry only exists to keep the index honest.
    private static readonly GraphicsColor[] LodTints =
    {
        new(0.4f, 1f, 0.4f, 0.6f),    // 0 — full detail (unused)
        new(1f, 0.9f, 0.25f, 0.7f),   // 1
        new(1f, 0.55f, 0.15f, 0.8f),  // 2
        new(1f, 0.2f, 0.2f, 0.9f),    // 3+
    };

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
        // Read-only values run even with the overlay hidden so F12 captures remain self-describing.
        // Controls and gizmos stop here because they are interactive or feed the debug-line pass.
        ReportValues(debug);
        if (!overlayEnabled) return;

        // Live tuning, grouped by scope. Controls are read-back: the returned
        // value feeds this frame's render (Debug() runs before OnRender).
        using (debug.Scope("Camera"))
        {
            // Keep the camera value pasteable as --cam so an observed frame can be replayed by the
            // headless measurement and capture paths.
            debug.Values.Value("--cam", string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{cameraPosition.X:0.##},{cameraPosition.Y:0.##},{cameraPosition.Z:0.##}," +
                $"{camYaw * 180f / MathF.PI:0.##},{camPitch * 180f / MathF.PI:0.##}"));
        }

        using (debug.Scope("Sun"))
        {
            var deg = 180f / MathF.PI;
            sunYaw   = debug.Controls.Float("Yaw (deg)", sunYaw * deg, -180f, 180f) / deg;
            sunPitch = debug.Controls.Float("Pitch (deg)", sunPitch * deg, -89f, -1f) / deg;
            sunStrength = debug.Controls.Float("Strength (x measured)", sunStrength, 0f, 8f);
            UpdateSunDirection();
        }
        // ShadowsSettings and RenderSettings are [Tune]-tagged and auto-paneled below.
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
            // Visualization channels are named here and share the shader's stable integer IDs.
            vizChannel = debug.Controls.Enum("Show", (int)MathF.Round(vizChannel), VizChannelNames);
            // Outlines every opaque primitive NOT at full detail, tinted by how coarse it is, and
            // flashes white the moment one switches level. The question this answers is not "how
            // much does LOD save" — the A/B answers that — but "which piece of wall was it".
            showLodBoxes = debug.Controls.Toggle("Show LOD levels", showLodBoxes);
            // Toggle the incident field under a still camera for direct visual comparison with the
            // inline path. Its allocation scale remains a launch flag because graph resources are
            // fixed at compile time.
            incidentField = debug.Controls.Toggle("Half-res incident field", incidentField);
        }

        // Expose the indirect solve's rays, refresh period, and occupancy interpretation together
        // so their cost and image effect can be compared within one run.
        if (skyVisibilityEnabled)
        {
            // Display raw per-probe fields before material albedo, AO, and visibility attenuate the
            // indirect contribution seen by the camera.
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
                // 256 is the ceiling: the workgroup has 256 lanes and each marches one ray.
                injectRays    = debug.Controls.Float("Rays / probe", injectRays, 8f, 256f);
                injectPeriod  = debug.Controls.Float("Refresh period", injectPeriod, 4f, 64f);
                injectTranslucency = debug.Controls.Float("Translucency", injectTranslucency, 0f, 1f);
                // Zero disables sleeping and supplies the full-grid A/B baseline.
                probeSleepFrames = MathF.Round(debug.Controls.Float("Sleep after (frames)", probeSleepFrames, 0f, 600f));
            }

            // Override cooked cloth values across sheened materials for live judgment. Settled
            // values belong back in the patch rather than in this ephemeral control state.
            using (debug.Scope("Cloth"))
            {
                clothOverride   = debug.Controls.Toggle("Override the patch", clothOverride);
                sheenRoughness  = debug.Controls.Float("Sheen roughness", sheenRoughness, 0.05f, 1f);
                diffuseTransmit = debug.Controls.Float("Diffuse transmission", diffuseTransmit, 0f, 1f);
            }
        }

        // Spatial gizmos: sun direction + the three cascade ortho boxes.
        // Every primitive below belongs to the main view; the scope supplies its matrix.
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

        // Draw secondary selections inside the active view. The framework owns the primary
        // highlight; these boxes make the rest of a multi-selection visible.
        foreach (var p in selection)
        {
            if (p == primarySelection) continue;
            if (sceneSelection.TryGetBounds(p, out var b))
                debug.Draw.Aabb($"sel/{p}", b.Min, b.Max, MultiSelectColor);
        }

        // Level 0 is deliberately not drawn: at a sane budget most of the scene is at full detail,
        // and outlining all of it would bury the handful of primitives the question is about.
        if (showLodBoxes)
        {
            for (var i = 0; i < lodLevels.Length && i < opaqueDrawables.Count; i++)
            {
                var level = lodLevels[i];
                var justPopped = lodPopAge[i] < PopHoldSeconds;
                if (level == 0 && !justPopped) continue;
                var bounds = opaqueDrawables[i].Bounds;
                debug.Draw.Aabb($"lod/{i}", bounds.Min, bounds.Max,
                    justPopped ? PopColor : LodTints[Math.Min(level, LodTints.Length - 1)]);
            }
        }
    }

    // Read-only state: emitted every frame, panel open or not, so an F12 dump always carries the
    // configuration that produced the frame. Nothing here is an input — no Controls calls, no Draw
    // calls — which is what makes it safe to run with the overlay hidden.
    private void ReportValues(DebugContext debug)
    {
        using (debug.Scope("Sun"))
        {
            debug.Values.Value("sun-irradiance",
                $"{EffectiveSunIrradiance.X:0.00} ({sunIrradiance.X:0.00} measured)");
        }

        using (debug.Scope("Indirect"))
        {
            // Report a windowed injection cost beside the controls that affect it; see
            // SampleGpuPassTimes.
            var injectMs = GpuPassMs("sky-inject");
            debug.Values.Value("inject-gpu", lastFramePeriodMs > 0.01
                ? $"{injectMs:0.00} ms ({injectMs / lastFramePeriodMs * 100.0:0.0}% of a {lastFramePeriodMs:0.0} ms frame)"
                : $"{injectMs:0.00} ms");

            // Report commanded work rather than inferring it from refresh period. Every probe
            // launches a workgroup each frame; selected probes march rays, while skipped probes copy
            // their irradiance and depth tiles into the other ping-pong atlas. Carry cost therefore
            // scales with probe count rather than refresh rate.
            var probes = bounceX * bounceY * bounceZ;
            const int tileTexels = 8 * 8;
            var rays = Math.Clamp((int)MathF.Round(injectRays), 8, 256);
            var period = MathF.Max(1f, MathF.Round(injectPeriod));
            var solving = Math.Max(1, (int)MathF.Round(probes / period));
            // Use invariant formatting so captures and measurement notes compare across locales.
            debug.Values.Value("inject-dispatch", string.Create(Inv,
                $"{probes:N0} workgroups x {tileTexels} lanes"));
            debug.Values.Value("inject-solving", string.Create(Inv,
                $"{solving:N0} probes x {rays} rays = {solving * (long)rays:N0} rays"));
            debug.Values.Value("inject-carry", string.Create(Inv,
                $"{(probes - solving) * (long)tileTexels * 2:N0} texel copies (irradiance+depth), period-independent"));
            debug.Values.Value("probe-refresh", $"{rays} rays every {period:0}f");
        }

        // Surface the same windowed pass timings used by the exit report. On tile-based GPUs these
        // bracket encoder submission rather than deferred tiled execution, so they need not sum to
        // the frame period and very small values are not proof that a pass is free.
        using (debug.Scope("GPU passes"))
        {
            var passTotal = GpuPassTotalMs();
            debug.Values.Value("encoded-total", $"{passTotal:0.00} ms (not the frame time)");
            foreach (var (pass, ms) in GpuPassesByCost().Take(8))
            {
                debug.Values.Value(pass, $"{ms:0.000} ms");
            }
        }

        debug.Values.Value("shadow-map", $"{ShadowMapSizes[0]}/{ShadowMapSizes[1]}/{ShadowMapSizes[2]}");
        debug.Values.Value("splits-m", $"{cascadeSplits[1]:0}/{cascadeSplits[2]:0}/{cascadeSplits[3]:0}");
        // One shadow texel in world units per cascade. Map size and fitted extent jointly determine
        // this value, which drives both receiver bias and filter width.
        debug.Values.Value("cascade-texel-m",
            $"{cascadeTexelWorld[0]:0.000}/{cascadeTexelWorld[1]:0.000}/{cascadeTexelWorld[2]:0.000}");
        // Per-cascade caster counts after frustum cull (one frame stale — set
        // during the previous OnRender's graph.Execute).
        debug.Values.Value("cascade-casters", $"{cascadeDrawCounts[0]}/{cascadeDrawCounts[1]}/{cascadeDrawCounts[2]} of {opaqueDrawables.Count}");
        // Shadow-map cache hits: R = re-rendered this frame, · = served cached.
        debug.Values.Value("cascade-tris", string.Create(Inv,
            $"{cascadeTriangles[0]:N0}/{cascadeTriangles[1]:N0}/{cascadeTriangles[2]:N0} vs camera {cameraTriangles:N0}"));
        debug.Values.Value("cascade-cache", $"{(cascadeRendered[0] ? 'R' : '·')}{(cascadeRendered[1] ? 'R' : '·')}{(cascadeRendered[2] ? 'R' : '·')}");
        debug.Values.Value("blend-draws", blendDrawables.Count);
        debug.Values.Value("cam-pos", cameraPosition);
        // LOD diagnostic: max levels available + histogram of selected levels
        // across opaque drawables at the current camera + pixel-error budget.
        //
        // Retain each drawable's rendered level and transition age as well as the aggregate
        // histogram so the gizmo pass can identify where and when a visible switch occurred.
        if (lodLevels.Length != opaqueDrawables.Count)
        {
            lodLevels = new int[opaqueDrawables.Count];
            lodPopAge = new float[opaqueDrawables.Count];
            Array.Fill(lodPopAge, float.MaxValue);
        }
        var maxLevels = 0;
        var hist = new int[8];
        var popped = 0;
        long submitted = 0;
        long full = 0;
        var dt = (float)(lastFramePeriodMs * 0.001);
        for (var i = 0; i < opaqueDrawables.Count; i++)
        {
            var d = opaqueDrawables[i];
            maxLevels = Math.Max(maxLevels, d.LodIndexCounts.Length);
            // Read the renderer's persistent selection. Hysteresis makes a second evaluation here
            // history-dependent and potentially different from the level actually submitted.
            var lv = i < opaqueLodState.Length ? opaqueLodState[i] : 0;
            if (lv < hist.Length) hist[lv]++;
            if (lv != lodLevels[i]) { lodPopAge[i] = 0f; popped++; }
            else lodPopAge[i] += dt;
            lodLevels[i] = lv;
            submitted += d.LodIndexCounts[Math.Min(lv, d.LodIndexCounts.Length - 1)];
            full += d.LodIndexCounts[0];
        }
        // Show the exact submitted/full-detail triangle trade beside the LOD budget; unlike frame
        // time, this responds immediately and is not confounded by thermal or scheduling drift.
        debug.Values.Value("lod-tris", string.Create(Inv,
            $"{submitted / 3:N0} of {full / 3:N0} ({(full > 0 ? submitted * 100.0 / full : 100.0):0.0}% of full detail)"));
        // A switch lasts one frame and the eye catches it as a flicker with no location. Held for
        // PopHoldSeconds so the box is still on the geometry when you look for it.
        var holding = 0;
        for (var i = 0; i < lodPopAge.Length; i++) if (lodPopAge[i] < PopHoldSeconds) holding++;
        debug.Values.Value("lod-popped", $"{popped} this frame, {holding} within {PopHoldSeconds:0.0}s");
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
    }
}
