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
        // <b>The read-only half runs whether or not anybody is looking at it.</b> This used to be
        // a bare early return, so with the panel hidden (Cmd+C) NOTHING was emitted — and an F12
        // frame dump taken in that state came back with two values and zero controls. A dump whose
        // whole job is to record the state that produced a frame was blank in exactly the case
        // where the state could not be read off the screen instead.
        //
        // Controls and gizmos still stop: a control is an input, and the debug-line pass renders
        // whatever is queued, so the cascade and sun gizmos would linger after hiding the overlay.
        ReportValues(debug);
        if (!overlayEnabled) return;

        // Live tuning, grouped by scope. Controls are read-back: the returned
        // value feeds this frame's render (Debug() runs before OnRender).
        using (debug.Scope("Camera"))
        {
            // <b>Pasteable straight back in as --cam, and that is the whole point.</b> Every number
            // measured tonight came from the measurement orbit, because the orbit was the only
            // viewpoint anything could be replayed at — so a defect seen from the chair and a
            // measurement taken headless were never about the same pixels. Three wrong conclusions
            // came out of that gap. An F12 dump now carries the viewpoint that produced the frame.
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
            // Outlines every opaque primitive NOT at full detail, tinted by how coarse it is, and
            // flashes white the moment one switches level. The question this answers is not "how
            // much does LOD save" — the A/B answers that — but "which piece of wall was it".
            showLodBoxes = debug.Controls.Toggle("Show LOD levels", showLodBoxes);
            // <b>Live, because this one has to be judged by eye and not by a number.</b> The field
            // and the inline path differ by 1.29 mean sRGB, which is small enough that a pair of
            // screenshots taken minutes apart cannot settle it and twice already has not. Flipping
            // it under a still camera puts both on the same retina a second apart.
            //
            // The resolution stays a launch flag (--incident-scale): the target's size is fixed
            // when the graph compiles. The error barely moves with it anyway — 5.32 at half and
            // 5.45 at full, back when the normal was the thing being measured.
            incidentField = debug.Controls.Toggle("Half-res incident field", incidentField);
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
                // 256 is the ceiling — the workgroup is 256 lanes and each marches one ray. The
                // range stopped at 64 because the value was inert; a dial that does nothing can
                // have any range at all.
                injectRays    = debug.Controls.Float("Rays / probe", injectRays, 8f, 256f);
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
            }
        }

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
            // <b>The cost, reported beside the dials that move it.</b> Windowed rather than a
            // lifetime mean, so a slider shows up here within about a second — see
            // SampleGpuPassTimes.
            var injectMs = GpuPassMs("sky-inject");
            debug.Values.Value("inject-gpu", lastFramePeriodMs > 0.01
                ? $"{injectMs:0.00} ms ({injectMs / lastFramePeriodMs * 100.0:0.0}% of a {lastFramePeriodMs:0.0} ms frame)"
                : $"{injectMs:0.00} ms");

            // What was actually commanded, which is not what the period suggests. EVERY probe's
            // workgroup launches every frame; the period only decides which of them go on to march
            // rays. The rest take the early-out — and the early-out is not free, because a probe
            // that skips its turn still has to COPY its whole tile into the other atlas (the pair
            // alternates every frame, so a probe that simply returned would be empty in one of
            // them). That copy is the part of this pass nobody ordered: it scales with probe count
            // and not with the refresh rate at all.
            var probes = bounceX * bounceY * bounceZ;
            const int tileTexels = 8 * 8;
            var rays = Math.Clamp((int)MathF.Round(injectRays), 8, 256);
            var period = MathF.Max(1f, MathF.Round(injectPeriod));
            var solving = Math.Max(1, (int)MathF.Round(probes / period));
            // <b>Invariant, not current, culture.</b> These came out as "6,19,008" on a machine set
            // to Indian digit grouping. A diagnostic is read against other diagnostics and pasted
            // into notes; it does not get to change shape with the locale.
            debug.Values.Value("inject-dispatch", string.Create(Inv,
                $"{probes:N0} workgroups x {tileTexels} lanes"));
            debug.Values.Value("inject-solving", string.Create(Inv,
                $"{solving:N0} probes x {rays} rays = {solving * (long)rays:N0} rays"));
            debug.Values.Value("inject-carry", string.Create(Inv,
                $"{(probes - solving) * (long)tileTexels * 2:N0} texel copies (irradiance+depth), period-independent"));
            debug.Values.Value("probe-refresh", $"{rays} rays every {period:0}f");
        }

        // <b>Live, because the console breakdown only prints when the process ends.</b> Every perf
        // question this session has been answered after the fact, from a log, about a run that had
        // already finished. These are the same timestamps the exit dump reads, windowed per frame.
        // The caveat travels with them: on a tile-based GPU they bracket ENCODER submission, not the
        // deferred tiled execution, so they do NOT sum to the frame period and a pass reading
        // 0.004 ms has not been shown to be free.
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
        debug.Values.Value("cascade-tris", string.Create(Inv,
            $"{cascadeTriangles[0]:N0}/{cascadeTriangles[1]:N0}/{cascadeTriangles[2]:N0} vs camera {cameraTriangles:N0}"));
        debug.Values.Value("cascade-cache", $"{(cascadeRendered[0] ? 'R' : '·')}{(cascadeRendered[1] ? 'R' : '·')}{(cascadeRendered[2] ? 'R' : '·')}");
        debug.Values.Value("blend-draws", blendDrawables.Count);
        debug.Values.Value("cam-pos", cameraPosition);
        // LOD diagnostic: max levels available + histogram of selected levels
        // across opaque drawables at the current camera + pixel-error budget.
        //
        // <b>And WHICH ones, and when they changed.</b> The histogram says four primitives coarsened
        // and says nothing about where they are, so "LOD is visible on that wall as I walk past it"
        // and "LOD is fine" produce the same three numbers. The level is recorded per drawable here
        // and the gizmo pass below draws it, so the thing that popped can be pointed at.
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
            // <b>Read, not re-picked.</b> Recomputing it here used to be harmless because the
            // selection was a pure function of the camera; with hysteresis it is a function of
            // history too, and a second evaluation would report a level the renderer never drew.
            var lv = i < opaqueLodState.Length ? opaqueLodState[i] : 0;
            if (lv < hist.Length) hist[lv]++;
            if (lv != lodLevels[i]) { lodPopAge[i] = 0f; popped++; }
            else lodPopAge[i] += dt;
            lodLevels[i] = lv;
            submitted += d.LodIndexCounts[Math.Min(lv, d.LodIndexCounts.Length - 1)];
            full += d.LodIndexCounts[0];
        }
        // <b>The trade, in one line, while the hand is on the slider.</b> Frame time on this machine
        // moves several milliseconds between two runs of the same thing, so dragging the budget and
        // watching the frame counter cannot separate a real saving from thermal drift. The triangle
        // count has no such problem: it is exactly what the budget decides, and it responds the
        // instant the dial does.
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
