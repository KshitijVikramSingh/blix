using System.Numerics;
using Blix.Graphics;

namespace Blix.Tools.Studio;

/// <summary>
/// An orbit camera: a yaw, a pitch, a distance, and the matrices that follow from them.
/// </summary>
/// <remarks>
/// <b>Extracted because the viewer had two of them, not because the file was long.</b> The main view
/// and the embedded viewport each carried their own <c>yaw</c> / <c>pitch</c> / <c>distance</c> and
/// their own copy of the spherical-to-cartesian arithmetic — the same decision written twice, in one
/// file, which is the bar conventions §4 sets regardless of where the copies live.
/// <para>
/// <b>It owns no input.</b> A drag arrives through <see cref="IInputHandler"/> for one camera and
/// through an ImGui item's own state for the other — ImGui captures the pointer over its windows, so
/// a panel viewport has to ask the widget rather than the host. Those are two genuinely different
/// routes, so this takes deltas and has no opinion about where they came from.
/// </para>
/// <para>
/// <b>And no aspect.</b> The main view's aspect is the window's; the panel's is its own rectangle's,
/// known only after UI layout. Storing one would mean storing the wrong one half the time, so the
/// aspect is a parameter to <see cref="ViewProjection"/> and stays the caller's fact.
/// </para>
/// </remarks>
public sealed class StudioCamera
{
    /// <summary>Where the camera is looking. The lab's subjects stand at the origin.</summary>
    public Vector3 Target { get; set; } = new(0f, 1f, 0f);

    /// <summary>Rotation about the world Y axis, in radians.</summary>
    public float Yaw { get; set; }

    /// <summary>Elevation, in radians. Clamped by <see cref="Orbit"/> rather than here.</summary>
    public float Pitch { get; set; } = 0.45f;

    /// <summary>Distance from <see cref="Target"/>.</summary>
    public float Distance { get; set; } = 11f;

    /// <summary>Vertical field of view. Matches what the lab's renderer was built around.</summary>
    public float FieldOfView { get; set; } = MathF.PI / 3.2f;

    public float NearPlane { get; set; } = 0.1f;

    public float FarPlane { get; set; } = 120f;

    /// <summary>How far a pitch may travel. Below the floor and past the pole are both useless views.</summary>
    public float MinPitch { get; set; } = 0.08f;

    public float MaxPitch { get; set; } = 1.45f;

    public float MinDistance { get; set; } = 3.5f;

    public float MaxDistance { get; set; } = 40f;

    public StudioCamera(float yaw = 0.7f, float pitch = 0.45f, float distance = 11f)
    {
        Yaw = yaw;
        Pitch = pitch;
        Distance = distance;
    }

    /// <summary>Where the eye sits this frame, from the orbit parameters.</summary>
    public Vector3 Position
    {
        get
        {
            var offset = new Vector3(
                MathF.Cos(Pitch) * MathF.Sin(Yaw),
                MathF.Sin(Pitch),
                MathF.Cos(Pitch) * MathF.Cos(Yaw)) * Distance;
            return Target + offset;
        }
    }

    /// <summary>
    /// The view-projection through a viewport of this aspect.
    /// </summary>
    /// <remarks>
    /// Recomputed on demand rather than cached: the two consumers ask at different moments in the
    /// frame (the window's camera during update, the panel's after its rectangle is known), and a
    /// cache would have to be invalidated by whichever of them moved last. Two trig calls and a
    /// matrix multiply are cheaper than the bug that caching invites.
    /// </remarks>
    public Matrix4x4 ViewProjection(float aspect)
    {
        var safeAspect = aspect > 0.0001f && float.IsFinite(aspect) ? aspect : 16f / 9f;
        var view = Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);
        return view * GraphicsMatrices.CreatePerspectiveVulkan(FieldOfView, safeAspect, NearPlane, FarPlane);
    }

    /// <summary>Turn the camera by a pointer delta, in logical pixels.</summary>
    /// <remarks>
    /// The sensitivities are the ones the lab was tuned to by hand and are deliberately not a knob:
    /// a lab that let you tune how a drag feels would need you to tune it before you could compare
    /// two runs.
    /// </remarks>
    public void Orbit(float deltaX, float deltaY)
    {
        Yaw -= deltaX * 0.008f;
        Pitch = Math.Clamp(Pitch + (deltaY * 0.006f), MinPitch, MaxPitch);
    }

    /// <summary>Move in or out by a wheel notch.</summary>
    public void Zoom(float wheelDelta)
    {
        Distance = Math.Clamp(Distance - (wheelDelta * 0.8f), MinDistance, MaxDistance);
    }

    /// <summary>Frame a subject of this height, standing on the ground.</summary>
    /// <remarks>
    /// Used when an asset of unknown scale is loaded. Not a general "fit the bounds" — the lab
    /// normalises its subjects to a couple of metres before drawing them, so the only thing this
    /// needs to do is look at the middle of one and stand far enough back.
    /// </remarks>
    public void FrameSubject(float height)
    {
        var tall = MathF.Max(0.5f, height);
        Target = new Vector3(0f, tall * 0.45f, 0f);
        Distance = Math.Clamp(tall * 2.8f, MinDistance, MaxDistance);
    }
}
