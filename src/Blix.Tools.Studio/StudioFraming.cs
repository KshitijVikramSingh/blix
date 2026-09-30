using System.Numerics;

namespace Blix.Tools.Studio;

/// <summary>How Studio frames a subject: a <see cref="CameraController"/> orbiting it, with Studio's limits.</summary>
/// <remarks>
/// <b>Policy, not a camera.</b> StudioCamera was an orbit camera of Studio's own, with its own pitch
/// convention (the eye's elevation, so positive looked down) and its own yaw sign. The engine's
/// <see cref="CameraController"/> does orbiting, flying and zooming as one camera, so what is left for
/// Studio is only what it decides: subjects stand at the origin about two metres tall, the eye stays above
/// the floor and within a sensible range, and a drag turns at the rate the viewer was tuned to.
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

    /// <summary>Studio's subjects stand at the origin; this is where the eye aims by default.</summary>
    public static Vector3 SubjectCentre { get; } = new(0f, 1f, 0f);
}
