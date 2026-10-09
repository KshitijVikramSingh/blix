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
    private static readonly System.Globalization.CultureInfo Inv =
        System.Globalization.CultureInfo.InvariantCulture;

    // --- LOD visibility instrument ---------------------------------------
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
        // The camera's layout and its pasteable pose (--cam, so an observed frame can be replayed by the
        // headless measurement and capture paths), then the keys this loop handles itself. Listed with
        // the overlay hidden too, so F12 dumps carry them.
        camera.DescribeDefaultKeys(debug);
        debug.Keys.Describe("Arrows", "look around");
        debug.Keys.Describe(Key.Escape, "quit");
        debug.Keys.Describe(Key.G, "reference view (path-traced indirect light) on/off");

        // Read-only values run even with the overlay hidden so F12 captures remain self-describing.
        // Controls and gizmos stop here because they are interactive or feed the debug-line pass.
        ReportValues(debug);
        if (!debug.State.ShowOverlay) return;

        // Live tuning, grouped by scope. Controls are read-back: the returned
        // value feeds this frame's render (Debug() runs before OnRender).

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
            device.VsyncEnabled = debug.Controls.Toggle("Vsync", device.VsyncEnabled);
            // Visualization channels are named here and share the shader's stable integer IDs.
            vizChannel = debug.Controls.Enum("Show", (int)MathF.Round(vizChannel), VizChannelNames);
            // Toggle the incident field under a still camera: off, the lit pass uses the open-sky cube
            // with no clipmap light, the diagnostic --no-incident is.
            incidentField = debug.Controls.Toggle("Incident field (off: open sky cube)", incidentField);
        }

        // The clipmap's levels, live (stage 4g-v): answer from one level alone or the blend, and the knobs the level
        // probe measured. Each level alone shows what a surface's light does when it crosses into that level.
        if (clipmap is not null)
        {
            using (debug.Scope("GI levels"))
            {
                forceLevel = debug.Controls.Enum("Answer from", forceLevel + 1, ClipmapAnswerNames) - 1;
                clipmapBlendOverride = debug.Controls.Float("Blend band (probes)", ClipmapBlend, 0f, 7f);
                clipmapLift = debug.Controls.Float("Lookup lift (m, 0: quarter spacing)", clipmapLift, 0f, 2f);
                clipmapVisibilityPower = debug.Controls.Float("Visibility power (0: 3)", clipmapVisibilityPower, 0f, 16f);
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

        // Outlines every opaque primitive NOT at full detail, tinted by how coarse it is, and flashes
        // white the moment one switches level. The question this answers is not "how much does LOD
        // save" — the A/B answers that — but "which piece of wall was it". A layer that starts
        // hidden, switched in the Layers tab.
        //
        // Level 0 is deliberately not drawn: at a sane budget most of the scene is at full detail,
        // and outlining all of it would bury the handful of primitives the question is about.
        if (debug.Draw.Layer("lod", visible: false))
        {
            for (var i = 0; i < lodLevels.Length && i < opaquePlacements.Count; i++)
            {
                var level = lodLevels[i];
                var justPopped = lodPopAge[i] < PopHoldSeconds;
                if (level == 0 && !justPopped) continue;
                var bounds = opaquePlacements[i].Bounds;
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
        ReportReference(debug);
        using (debug.Scope("Sun"))
        {
            debug.Values.Value("sun-irradiance",
                $"{EffectiveSunIrradiance.X:0.00} ({sunIrradiance.X:0.00} measured)");
        }

        // Per-pass GPU time, the CPU split and what was submitted are on the Perf tab, from the host's
        // timing record; the values here are Sponza's own.

        debug.Values.Value("shadow-map", $"{ShadowMapSizes[0]}/{ShadowMapSizes[1]}/{ShadowMapSizes[2]}");
        debug.Values.Value("splits-m", $"{cascadeSplits[1]:0}/{cascadeSplits[2]:0}/{cascadeSplits[3]:0}");
        // One shadow texel in world units per cascade. Map size and fitted extent jointly determine
        // this value, which drives both receiver bias and filter width.
        debug.Values.Value("cascade-texel-m",
            $"{cascadeTexelWorld[0]:0.000}/{cascadeTexelWorld[1]:0.000}/{cascadeTexelWorld[2]:0.000}");
        // Per-cascade caster counts after frustum cull (one frame stale — set
        // during the previous OnRender's graph.Execute).
        // Under GPU culling both counts stay on the GPU: --cpu-cull reports them.
        debug.Values.Value("cascade-casters", gpuCull
            ? "on the GPU (--cpu-cull counts)"
            : $"{cascadeDrawCounts[0]}/{cascadeDrawCounts[1]}/{cascadeDrawCounts[2]} of {opaquePlacements.Count}");
        // Shadow-map cache hits: R = re-rendered this frame, · = served cached.
        debug.Values.Value("cascade-tris", gpuCull
            ? "on the GPU (--cpu-cull counts)"
            : string.Create(Inv,
                $"{cascadeTriangles[0]:N0}/{cascadeTriangles[1]:N0}/{cascadeTriangles[2]:N0} vs camera {cameraTriangles:N0}"));
        debug.Values.Value("cascade-cache", $"{(cascadeRendered[0] ? 'R' : '·')}{(cascadeRendered[1] ? 'R' : '·')}{(cascadeRendered[2] ? 'R' : '·')}");
        debug.Values.Value("blend-draws", blendDrawables.Count);
        debug.Values.Value("cam-pos", cameraPosition);
        // LOD diagnostic: max levels available + histogram of selected levels
        // across opaque drawables at the current camera + pixel-error budget.
        //
        // Retain each drawable's rendered level and transition age as well as the aggregate
        // histogram so the gizmo pass can identify where and when a visible switch occurred.
        if (lodLevels.Length != opaquePlacements.Count)
        {
            lodLevels = new int[opaquePlacements.Count];
            lodPopAge = new float[opaquePlacements.Count];
            Array.Fill(lodPopAge, float.MaxValue);
        }
        var maxLevels = 0;
        var hist = new int[8];
        var popped = 0;
        long submitted = 0;
        long full = 0;
        var dt = (float)(lastFramePeriodMs * 0.001);
        for (var i = 0; i < opaquePlacements.Count; i++)
        {
            var d = opaqueDrawables[opaquePlacements[i].Drawable];
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
    }
}
