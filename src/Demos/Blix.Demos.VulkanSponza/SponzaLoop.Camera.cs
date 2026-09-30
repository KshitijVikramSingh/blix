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
                triangleFrames = 0;
                cameraTriangleSum = 0;
                System.Array.Clear(cascadeTriangleSum);
            }
        }

        var dt = (float)time.Delta;
        var input = host.Input;
        ReadInput(input);

        // Translation: WASD + Space/Ctrl. Sprint via Shift, or Cmd/Super — the latter was the only
        // one of the two the Key enum could name when this was written.
        var move = Vector3.Zero;
        if (input[Key.W].Down) move += cameraForward;
        if (input[Key.S].Down) move -= cameraForward;
        var right = Vector3.Normalize(Vector3.Cross(cameraForward, Vector3.UnitY));
        if (input[Key.D].Down) move += right;
        if (input[Key.A].Down) move -= right;
        if (input[Key.Space].Down) move += Vector3.UnitY;
        if (input[Key.LeftControl].Down) move -= Vector3.UnitY;
        var sprint = input[Key.LeftShift].Down || input[Key.RightShift].Down
                     || input[Key.LeftSuper].Down || input[Key.RightSuper].Down;
        var speed = sprint ? render.MoveSpeed * 3f : render.MoveSpeed;
        if (move != Vector3.Zero)
        {
            cameraPosition += Vector3.Normalize(move) * speed * dt;
        }

        // Rotation: arrow keys (keyboard look — WASD already handles movement).
        // Left/Right yaw, Up/Down pitch; matches the mouse-look sign convention.
        const float lookSpeed = 1.8f; // rad/s
        var look = lookSpeed * dt;
        if (input[Key.Left].Down)  camYaw -= look;
        if (input[Key.Right].Down) camYaw += look;
        if (input[Key.Up].Down)    camPitch += look;
        if (input[Key.Down].Down)  camPitch -= look;
        var pitchLimit = MathF.PI / 2f - 0.01f;
        camPitch = Math.Clamp(camPitch, -pitchLimit, pitchLimit);

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
        var centre = foliageValid ? foliageCentre : skyVolumeMin + skyVolumeSpan * 0.5f;
        // Inside the building rather than around it: the arcade is where the overdraw, the cutout
        // foliage and the cascade transitions all are, and an exterior orbit sees none of them.
        var radius = MathF.Min(skyVolumeSpan.X, skyVolumeSpan.Z) * 0.20f;
        var eyeY = centre.Y * 0.55f + skyVolumeMin.Y * 0.45f;
        cameraPosition = new Vector3(
            centre.X + MathF.Cos(angle) * radius,
            eyeY,
            centre.Z + MathF.Sin(angle) * radius);

        // Face inward to keep the measured subject in view. The moving camera position still sweeps
        // each fitted light frustum through the scene and exercises cascade updates.
        var toCentre = centre - cameraPosition;
        camYaw = MathF.Atan2(toCentre.X, -toCentre.Z);
        camPitch = MathF.Atan2(toCentre.Y, new Vector2(toCentre.X, toCentre.Z).Length());
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var cp = MathF.Cos(camPitch);
        cameraForward = Vector3.Normalize(new Vector3(
            cp * MathF.Sin(camYaw),
            MathF.Sin(camPitch),
            -cp * MathF.Cos(camYaw)));
        var view = Matrix4x4.CreateLookAt(cameraPosition, cameraPosition + cameraForward, Vector3.UnitY);
        // Sponza atrium spans tens of metres; far plane needs to be generous.
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(fovYRadians, aspect, CameraNearPlane, CameraFarPlane);
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
        if (input[Key.Escape].Pressed) host.RequestClose();

        // Cursor capture follows the button rather than a bool kept in step with it.
        var look = input[MouseButton.Right].Down;
        if (look != mouseLook)
        {
            mouseLook = look;
            host.SetCursorCaptured(look);
        }

        if (mouseLook && input.MouseDelta != Vector2.Zero)
        {
            const float sensitivity = 0.0035f;
            camYaw += input.MouseDelta.X * sensitivity;
            camPitch -= input.MouseDelta.Y * sensitivity;
            var limit = MathF.PI / 2f - 0.01f;
            camPitch = Math.Clamp(camPitch, -limit, limit);
            UpdateCamera();
        }

        if (input.MouseWheel.Y != 0f)
        {
            render.MoveSpeed = Math.Clamp(render.MoveSpeed * (input.MouseWheel.Y > 0 ? 1.25f : 0.8f), 0.3f, 60f);
        }
    }
}
