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
    public void OnUpdate(Time time)
    {
        // Promote the background-parsed packs to GPU resources on the main thread
        // (time-sliced; geometry + flat preview appears in ~1s).
        TryFinishLoad();
        // Stream texture mips into their pre-allocated handles, budgeted per
        // frame; the flat preview holds until this drains. Once empty, the full
        // lit+shadow loop takes over (fullyLoaded).
        if (sceneLoaded && !fullyLoaded)
        {
            textureLoader.Drain(budgetMillis: 6.0);
            if (textureLoader.PendingCount == 0)
            {
                fullyLoaded = true;
                // Steady state starts here, so every measurement window does too: the streaming
                // frames rendered a different (flat) path entirely, and counting them diluted the
                // amortised cost of everything that only runs once the real path is live.
                host.Timing.ResetIsolatedTotals();
                // The device's GPU totals are cumulative by design: the window starts with this snapshot.
                gpuTotalsAtLoad = new Dictionary<string, GpuPassTotal>(host.Timing.GpuPassTotals);
                triangleFrames = 0;
                cameraTriangleSum = 0;
                System.Array.Clear(cascadeTriangleSum);
            }
        }

        var dt = (float)time.Delta;
        var input = host.Input;
        ReadInput(input);

        // <b>A shot is a measurement, so nothing at the keyboard moves it.</b> The shot window takes focus
        // when it opens; a key or a drag meant for another window then flew the camera, and two "identical"
        // runs captured two places (Bistro's noise floor read 91% of pixels changed). The pose is the
        // profile's start or --cam, and --orbit still drives its own path.
        if (shotPath is not null)
        {
            UpdateCamera();
            return;
        }

        // The engine's layout: right-drag looks (the wheel sets the speed meanwhile), WASD with Space and
        // Ctrl flies, Shift or Cmd sprints, left-drag orbits, the wheel zooms. Arrow keys stay Sponza's:
        // keyboard look, for when a hand is on the keys.
        camera.DriveDefault(host, dt);
        const float lookSpeed = 1.8f; // rad/s
        var turn = new Vector2(
            (input[Key.Right].Down ? 1f : 0f) - (input[Key.Left].Down ? 1f : 0f),
            (input[Key.Up].Down ? 1f : 0f) - (input[Key.Down].Down ? 1f : 0f));
        if (turn != Vector2.Zero) camera.Turn(turn.X * lookSpeed * dt, turn.Y * lookSpeed * dt);

        UpdateCamera();
    }

    /// <summary>Places the camera on a closed, frame-indexed path for measurement runs.</summary>
    /// <remarks>
    /// The orbit exercises cascade refits, LOD transitions, and changing Hi-Z input that a still
    /// camera would leave cached or static.
    ///
    /// The path period matches <c>AbPeriodFrames</c>, so successive A/B phases traverse identical
    /// viewpoints in identical order and camera motion cancels from their comparison.
    ///
    /// A post-load frame clock, rather than elapsed time, makes the path independent of streaming
    /// duration and of the performance difference being measured.
    /// </remarks>
    private void ApplyOrbit()
    {
        // Use the post-load measurement clock so --shot-frames identifies the same viewpoint even
        // when texture streaming takes a different number of frames.
        var t = (postLoadFrames % OrbitFrames) / (float)OrbitFrames;
        var angle = t * MathF.Tau;
        // Orbit the foliage when there is any, because that is the content a measurement most often
        // wants in frame and the one a bounds-derived path missed entirely.
        var centre = foliageValid ? foliageCentre : sceneBoundsMin + sceneBoundsSpan * 0.5f;
        // Inside the building rather than around it: the arcade is where the overdraw, the cutout
        // foliage and the cascade transitions all are, and an exterior orbit sees none of them.
        var radius = MathF.Min(sceneBoundsSpan.X, sceneBoundsSpan.Z) * 0.20f;
        var eyeY = centre.Y * 0.55f + sceneBoundsMin.Y * 0.45f;
        var eye = new Vector3(
            centre.X + MathF.Cos(angle) * radius,
            eyeY,
            centre.Z + MathF.Sin(angle) * radius);

        // Face inward to keep the measured subject in view. The moving camera position still sweeps
        // each fitted light frustum through the scene and exercises cascade updates.
        camera.LookAt(eye, centre);
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        // The controller's camera, whose view equals CreateLookAt(position, position + forward) (Test.Graphics BO.6).
        // Sponza atrium spans tens of metres; the far plane is generous (SponzaCamera).
        var view = camera.Camera.GetView();
        var proj = camera.Camera.GetProjection(aspect);
        // Kept apart as well as combined: GTAO reconstructs VIEW space from depth, which needs the
        // projection alone, and returns a bent normal in world space, which needs the view alone.
        cameraView = view;
        cameraProjection = proj;
        viewProj = view * proj;
        UpdateCascades();
    }

    // Recompute the sun travel direction from the overlay-driven yaw/pitch.
    private void UpdateSunDirection()
    {
        var cp = MathF.Cos(sunPitch);
        sunDirection = Vector3.Normalize(new Vector3(
            cp * MathF.Sin(sunYaw),
            MathF.Sin(sunPitch),
            -cp * MathF.Cos(sunYaw)));
    }

    public void OnResize(int width, int height)
    {
        if (height > 0)
        {
            aspect = width / (float)height;
            renderHeightPx = height;
            UpdateCamera();
        }
    }

    /// <summary>Read the devices once per tick, at a point this loop chose.</summary>
    private void ReadInput(IInputState input)
    {
        // The mouse, the cursor capture and the wheel are the camera controller's (Drive, in OnUpdate).
        if (input[Key.Escape].Pressed) host.RequestClose();
        ToggleReference(input);
    }
}
