using System.Numerics;

namespace Blix.Tools.Studio;

/// <summary>How Studio frames a subject: a <see cref="CameraController"/> orbiting it, with Studio's limits.</summary>
/// <remarks>
/// <b>Policy, not a camera.</b> The engine's <see cref="CameraController"/> is the camera; this is only what
/// Studio decides about it: subjects stand at the origin about two metres tall, the eye stays above the
/// floor and within a sensible range, and a drag turns at the rate the viewer was tuned to. Placement is in
/// Studio's own terms (the eye's azimuth and elevation round the subject).
/// </remarks>
public static class StudioFraming
{
    /// <summary>A camera with Studio's lens, and a controller looking at <paramref name="target"/> from around it.</summary>
    /// <param name="target">What to look at. Studio subjects stand at the origin, so about chest height.</param>
    /// <param name="azimuthDegrees">Where the eye sits round the target: 0 is on +Z, positive towards +X.</param>
    /// <param name="elevationDegrees">How far above the target the eye sits.</param>
    /// <param name="distance">How far from the target, in metres.</param>
    public static CameraController Around(Vector3 target, float azimuthDegrees, float elevationDegrees, float distance)
    {
        // The lens the Studio renderer was built around.
        var camera = new Camera3D { VerticalFieldOfView = MathF.PI / 3.2f, NearPlane = 0.1f, FarPlane = 120f };
        var controller = new CameraController(camera)
        {
            // Above the floor, and never straight down: an orbit that reaches the pole spins about it.
            MinPitch = -83f,
            MaxPitch = -4.5f,
            MinDistance = 3.5f,
            MaxDistance = 40f,
            // The rate the viewer's drags were tuned to by hand.
            Sensitivity = 0.007f,
        };
        var a = azimuthDegrees * MathF.PI / 180f;
        var e = elevationDegrees * MathF.PI / 180f;
        var eye = target + new Vector3(MathF.Cos(e) * MathF.Sin(a), MathF.Sin(e), MathF.Cos(e) * MathF.Cos(a)) * distance;
        controller.LookAt(eye, target);
        return controller;
    }

    /// <summary>The view-projection through one of a model's own cameras (glTF: the node looks down -Z, +Y up).</summary>
    /// <param name="placement">Where the tool drew the model (its fit to the stage); the camera moves with it.</param>
    /// <param name="aspect">The viewport's, used when the camera states none.</param>
    /// <returns>Null when no node carries camera <paramref name="camera"/>.</returns>
    public static Matrix4x4? ThroughCamera(Blix.Model model, int camera, Matrix4x4 placement, float aspect)
    {
        if ((uint)camera >= (uint)model.Cameras.Count) return null;
        var carrier = model.Nodes.FirstOrDefault(n => n.CameraIndex == camera);
        if (carrier is null) return null;
        var lens = model.Cameras[camera];
        var world = carrier.World * placement;
        var eye = Vector3.Transform(Vector3.Zero, world);
        var forward = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, world));
        var up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, world));
        var view = Matrix4x4.CreateLookAt(eye, eye + forward, up);
        // The placement's uniform scale, so near, far and an orthographic extent stay where the author put them.
        var scale = Vector3.TransformNormal(Vector3.UnitX, placement).Length();
        var near = MathF.Max(lens.ZNear * scale, 1e-4f);
        var far = float.IsFinite(lens.ZFar) ? lens.ZFar * scale : near * 1e5f;
        return lens.Orthographic
            ? view * Blix.Graphics.GraphicsMatrices.CreateOrthographicVulkan(2f * lens.XMag * scale, 2f * lens.YMag * scale, near, far)
            : view * Blix.Graphics.GraphicsMatrices.CreatePerspectiveVulkan(lens.YFov, lens.AspectRatio > 0f ? lens.AspectRatio : aspect, near, far);
    }

    /// <summary>Studio's subjects stand at the origin; this is where the eye aims by default.</summary>
    public static Vector3 SubjectCentre { get; } = new(0f, 1f, 0f);
}
