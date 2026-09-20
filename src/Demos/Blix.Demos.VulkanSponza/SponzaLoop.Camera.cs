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
                vk.ResetGpuIsolation();
                triangleFrames = 0;
                cameraTriangleSum = 0;
                System.Array.Clear(cascadeTriangleSum);
            }
        }

        var dt = (float)time.Delta;

        // Translation: WASD + Space/Ctrl. Sprint via Cmd/Super (engine's Key
        // enum has no Shift).
        var move = Vector3.Zero;
        if (heldKeys.Contains(Key.W)) move += cameraForward;
        if (heldKeys.Contains(Key.S)) move -= cameraForward;
        var right = Vector3.Normalize(Vector3.Cross(cameraForward, Vector3.UnitY));
        if (heldKeys.Contains(Key.D)) move += right;
        if (heldKeys.Contains(Key.A)) move -= right;
        if (heldKeys.Contains(Key.Space)) move += Vector3.UnitY;
        if (heldKeys.Contains(Key.LeftControl)) move -= Vector3.UnitY;
        var sprint = heldKeys.Contains(Key.LeftSuper) || heldKeys.Contains(Key.RightSuper);
        var speed = sprint ? render.MoveSpeed * 3f : render.MoveSpeed;
        if (move != Vector3.Zero)
        {
            cameraPosition += Vector3.Normalize(move) * speed * dt;
        }

        // Rotation: arrow keys (keyboard look — WASD already handles movement).
        // Left/Right yaw, Up/Down pitch; matches the mouse-look sign convention.
        const float lookSpeed = 1.8f; // rad/s
        var look = lookSpeed * dt;
        if (heldKeys.Contains(Key.Left))  camYaw -= look;
        if (heldKeys.Contains(Key.Right)) camYaw += look;
        if (heldKeys.Contains(Key.Up))    camPitch += look;
        if (heldKeys.Contains(Key.Down))  camPitch -= look;
        var pitchLimit = MathF.PI / 2f - 0.01f;
        camPitch = Math.Clamp(camPitch, -pitchLimit, pitchLimit);

        UpdateCamera();
    }

    /// <summary>Places the camera on a closed, frame-indexed path for measurement runs.</summary>
    /// <remarks>
    /// <b>A still camera hides three of the frame's largest costs, and every A/B run so far has had
    /// one.</b> Shadow cascades serve from cache when the light matrix does not move, so the 3x
    /// caster redraw — 5,420 primitives each — is never paid in EITHER arm of a --ab shadow run.
    /// LOD never switches, so hysteresis and popping are untestable. The Hi-Z pyramid reduces a
    /// depth buffer that does not change. A measurement taken like that is not of the renderer, it
    /// is of one photograph of it.
    ///
    /// <b>The period is the A/B period, and that is not a coincidence.</b> --ab alternates arms
    /// every AbPeriodFrames; if the path period were anything else, the two arms would sample
    /// different stretches of it and the difference between them would be partly a difference of
    /// viewpoint. Matched, phase K and phase K+1 traverse the identical positions in the identical
    /// order, so the camera cancels exactly and what is left is the thing being toggled.
    ///
    /// Driven by the frame counter rather than elapsed time for the same reason: wall-clock would
    /// make the path depend on how fast each arm ran, which is the quantity under test.
    /// </remarks>
    private void ApplyOrbit()
    {
        var t = (framesRendered % OrbitFrames) / (float)OrbitFrames;
        var angle = t * MathF.Tau;
        var centre = skyVolumeMin + skyVolumeSpan * 0.5f;
        // Inside the building rather than around it: the arcade is where the overdraw, the cutout
        // foliage and the cascade transitions all are, and an exterior orbit sees none of them.
        var radius = MathF.Min(skyVolumeSpan.X, skyVolumeSpan.Z) * 0.28f;
        cameraPosition = new Vector3(
            centre.X + MathF.Cos(angle) * radius,
            skyVolumeMin.Y + 3.2f,
            centre.Z + MathF.Sin(angle) * radius);
        // Look along the path rather than at the centre: a camera pointed at one spot for a whole
        // revolution keeps the same geometry on screen and never re-fits a cascade the hard way.
        camYaw = -angle + MathF.PI * 0.5f;
        camPitch = -0.05f;
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

    public void OnKeyDown(Key key)
    {
        heldKeys.Add(key);
        switch (key)
        {
            // Cmd+C toggles the diagnostics overlay. Plain C does nothing.
            case Key.C:
                if (heldKeys.Contains(Key.LeftSuper) || heldKeys.Contains(Key.RightSuper))
                {
                    overlayEnabled = !overlayEnabled;
                }
                break;
            // Exposure / sun / shadow tuning live in the overlay Controls now;
            // arrow keys drive the camera (see OnUpdate).
            case Key.Escape:
                host.RequestClose();
                break;
        }
    }

    public void OnKeyUp(Key key) => heldKeys.Remove(key);

    public void OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        lastMouseX = x;
        lastMouseY = y;
        if (!mouseLook) return;
        const float sensitivity = 0.0035f;
        camYaw += deltaX * sensitivity;
        camPitch -= deltaY * sensitivity;
        var limit = MathF.PI / 2f - 0.01f;
        camPitch = Math.Clamp(camPitch, -limit, limit);
        UpdateCamera();
    }

    public void OnMouseDown(MouseButton button)
    {
        if (button == MouseButton.Right)
        {
            mouseLook = true;
            host.SetCursorCaptured(true);
        }
        else if (button == MouseButton.Left && !mouseLook)
        {
            PickAt(lastMouseX, lastMouseY);
        }
    }

    // Click-to-pick: build a geometric pick ray (backend-agnostic — no Vulkan-NDC
    // unproject needed), ray-test every registered selectable's AABB, and select
    // the smallest-volume hit ("the most specific thing under the cursor", as the
    // GL demo does). Misses clear the selection.
    private void PickAt(float mx, float my)
    {
        if (debugSystem is null) return;
        var w = host.LogicalSize.Width;
        var h = host.LogicalSize.Height;
        if (w <= 0 || h <= 0) return;

        var nx = 2f * mx / w - 1f;
        var ny = 1f - 2f * my / h;
        var right = Vector3.Normalize(Vector3.Cross(cameraForward, Vector3.UnitY));
        var up = Vector3.Cross(right, cameraForward);
        var tanV = MathF.Tan(fovYRadians * 0.5f);
        var dir = Vector3.Normalize(cameraForward + right * (nx * tanV * aspect) + up * (ny * tanV));
        var ray = new Blix.Geometry.Ray(cameraPosition, dir);

        var selectables = debugSystem.CollectSelectables();
        DebugSelectable? best = null;
        var bestVolume = float.PositiveInfinity;
        for (var i = 0; i < selectables.Count; i++)
        {
            var s = selectables[i];
            if (Blix.Geometry.Intersection.Raycast(ray, s.Bounds) is null) continue;
            var ext = s.Bounds.Max - s.Bounds.Min;
            var vol = ext.X * ext.Y * ext.Z;
            if (vol < bestVolume) { bestVolume = vol; best = s; }
        }

        // Cmd-click adds/toggles; plain click replaces. The framework tracks one
        // SelectedPath (the primary, last-picked) for its highlight + inspector;
        // the full multi-select set lives here and is drawn/edited in Debug().
        var add = heldKeys.Contains(Key.LeftSuper) || heldKeys.Contains(Key.RightSuper);
        if (best is { } pick)
        {
            if (add)
            {
                if (!selection.Remove(pick.EntityPath)) selection.Add(pick.EntityPath);
                primarySelection = selection.Contains(pick.EntityPath) ? pick.EntityPath
                    : (selection.Count > 0 ? selection.First() : null);
            }
            else
            {
                selection.Clear();
                selection.Add(pick.EntityPath);
                primarySelection = pick.EntityPath;
            }
        }
        else if (!add)
        {
            selection.Clear();
            primarySelection = null;
        }

        if (primarySelection is { } pp && sceneSelection.TryGetBounds(pp, out var pb))
            debugSystem.Select(pp, pb);
        else
            debugSystem.ClearSelection();
    }

    public void OnMouseUp(MouseButton button)
    {
        if (button == MouseButton.Right)
        {
            mouseLook = false;
            host.SetCursorCaptured(false);
        }
    }

    public void OnMouseWheel(float offsetX, float offsetY)
    {
        render.MoveSpeed = Math.Clamp(render.MoveSpeed * (offsetY > 0 ? 1.25f : 0.8f), 0.3f, 60f);
    }
}
