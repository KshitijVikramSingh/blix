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
            DebugProgress.Set("textures", textureLoader.UploadedCount, textureLoader.UploadedCount + textureLoader.PendingCount, "stream");
            if (textureLoader.PendingCount == 0)
            {
                DebugProgress.End("textures");
                fullyLoaded = true;
                // Steady state starts here, so every measurement window does too: the streaming
                // frames rendered a different (flat) path entirely, and counting them diluted the
                // amortised cost of everything that only runs once the real path is live.
                host.Timing.ResetIsolatedTotals();
                // The device's GPU totals are cumulative by design: the window starts with this snapshot.
                gpuTotalsAtLoad = new Dictionary<string, GpuPassTotal>(host.Timing.GpuPassTotals);
                triangleFrames = 0;
                cameraTriangleSum = 0;
            }
        }

        var dt = (float)time.Delta;
        var input = host.Input;
        ReadInput(input);

        // <b>A shot is a measurement, so nothing at the keyboard moves it.</b> The shot window takes focus
        // when it opens; a key or a drag meant for another window then flew the camera, and two "identical"
        // runs captured two places (Bistro's noise floor read 91% of pixels changed). The pose is the
        // profile's start or --cam, and --walk still drives its own path.
        if (shotPath is not null)
        {
            UpdateCamera();
            return;
        }
        // The camera is locked while a cook holds the screen (CookScreenNow): the screen keeps the last frame, and the view
        // it shows is where the scene comes back.
        if (CookScreenNow || RtgiSettleNow)
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

    private void UpdateCamera()
    {
        // The controller's camera, whose view equals CreateLookAt(position, position + forward) (Test.Graphics BO.6).
        // Sponza atrium spans tens of metres; the far plane is generous (SponzaCamera).
        var view = camera.Camera.GetView();
        var proj = camera.Camera.GetProjection(aspect);
        // Kept apart as well as combined: the occlusion pyramid linearises depth with the projection
        // alone, and its test measures a bound's view depth with the view alone.
        cameraView = view;
        cameraProjection = proj;
        viewProj = view * proj;
    }

    // Recompute the sun travel direction from the overlay-driven yaw/pitch.
    private void UpdateSunDirection()
    {
        // The yaw within the overlay slider's range (-180..180), whatever set it: the slider clamps what it is handed, and
        // a sun turned past 180 (--sun-change 1500 190 -72, the moving sun from Crytek Sponza's 180) went back to 180.
        if (sunYaw > MathF.PI) sunYaw -= 2f * MathF.PI;
        else if (sunYaw < -MathF.PI) sunYaw += 2f * MathF.PI;
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
        // C: the camera's pose into the log, as --cam takes it -- a spot to point at, reproduced exactly. The demo's own
        // input: ImGui is given no keys, so its IsKeyPressed never fired and the key did nothing (2026-10-24).
        if (input[Key.C].Pressed)
            Console.WriteLine($"[VulkanSponza] camera pose: --cam {camera.Pose} (post-load frame {postLoadFrames})");
        // M: the sun moving, or held (SponzaLoop.SunMove.cs).
        if (input[Key.M].Pressed) ToggleSunMove();
    }
}
