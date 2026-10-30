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
        RtgiControls(debug);
        // ShadowsSettings and RenderSettings are [Tune]-tagged and auto-paneled below.
        // Shader-uniform tunables (//@tune in frame.glsl: the cutout and bounce-term diagnostics)
        // are auto-built + read back here — grouped by their UBO block.
        tunePanel.BuildControls(debug);
        tuneObjects.BuildControls(debug);   // [Tune]-tagged CPU settings (Shadows, Render, the camera)

        // Exposure / tonemap / LOD-error budget / render scale are [Tune] on RenderSettings, the fly
        // speed on the camera (both auto-paneled above). Vsync stays manual — it's a
        // device property, not a field: FIFO (capped, no tearing) vs Mailbox
        // (uncapped; tears on MoltenVK). Toggling recreates the swapchain.
        using (debug.Scope("Render"))
        {
            device.VsyncEnabled = debug.Controls.Toggle("Vsync", device.VsyncEnabled);
            // The camera's frustum culling: off, every placement is drawn (the --ab cull arm, held).
            cullEnabled = debug.Controls.Toggle("Frustum cull", cullEnabled);
            // Visualization channels are named here and share the shader's stable integer IDs.
            vizChannel = debug.Controls.Enum("Show", (int)MathF.Round(vizChannel), VizChannelNames);
        }

        // Spatial gizmos: the sun's direction, an arrow into the origin.
        // Every primitive below belongs to the main view; the scope supplies its matrix.
        // A layer that starts hidden (the Layers tab): drawn always, the arrow sat at the origin, the tree's foot in
        // Sponza.
        using var view = debug.Draw.In("main", viewProj);
        if (debug.Draw.Layer("gizmos", visible: false))
        {
            debug.Draw.Arrow("sun/dir", -sunDirection * 6f, Vector3.Zero,
                new GraphicsColor(1f, 0.92f, 0.3f, 1f));
        }

        // Outlines every opaque primitive NOT at full detail, tinted by how coarse it is, and flashes
        // white the moment one switches level. The question this answers is not "how much does LOD
        // save" — the A/B answers that — but "which piece of wall was it". A layer that starts
        // hidden, switched in the Layers tab. CPU cull only (--cpu-cull), as the values in ReportValues.
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
        using (debug.Scope("Sun"))
        {
            debug.Values.Value("sun-irradiance",
                $"{EffectiveSunIrradiance.X:0.00} ({sunIrradiance.X:0.00} measured)");
        }

        // Per-pass GPU time, the CPU split and what was submitted are on the Perf tab, from the host's
        // timing record; the values here are Sponza's own.

        // The camera's triangles after its cull (one frame stale — set during the previous OnRender's
        // graph.Execute). Under GPU culling the count stays on the GPU: --cpu-cull reports it.
        debug.Values.Value("camera-tris", gpuCull
            ? "on the GPU (--cpu-cull counts)"
            : string.Create(Inv, $"{cameraTriangles:N0}"));
        debug.Values.Value("blend-draws", blendDrawables.Count);
        debug.Values.Value("cam-pos", cameraPosition);
        // LOD diagnostic: max levels available + histogram of selected levels
        // across opaque drawables at the current camera + pixel-error budget.
        //
        // Retain each drawable's rendered level and transition age as well as the aggregate
        // histogram so the gizmo pass can identify where and when a visible switch occurred.
        //
        // CPU cull only (--cpu-cull): the levels are opaqueLodState, which only the CPU fill writes. The GPU
        // cull keeps its levels in cullState on the GPU, and reading them back every frame for an overlay is a
        // stall; under it the instrument says so instead of reporting every placement at level 0.
        if (gpuCull)
        {
            const string onGpu = "on the GPU (--cpu-cull counts)";
            debug.Values.Value("lod-tris", onGpu);
            debug.Values.Value("lod-popped", onGpu);
            debug.Values.Value("lod-hist", string.Create(Inv, $"{onGpu} (err={LodErrorPixelsNow:0.0}px)"));
            return;
        }
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
        debug.Values.Value("lod-hist", string.Create(Inv, $"{hist[0]}/{hist[1]}/{hist[2]}/{hist[3]} (err={LodErrorPixelsNow:0.0}px)"));
    }
}
