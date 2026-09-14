using System.Numerics;
using Blix.Graphics;

namespace Blix.Labs.Character;

/// <summary>
/// An orbit camera that can also be walked around the hall.
/// </summary>
/// <remarks>
/// <b>Its own, rather than the toolchain lab's.</b> <c>LabCamera</c> orbits a subject that stands at
/// the origin and normalises itself to a couple of metres; a room is 28 m across and the thing worth
/// looking at is a different corner of it every time, so this one pans. Reaching across for it would
/// also have pulled a second lab's shaders into this executable's output through content propagation.
/// If stage M finds the rig types genuinely shared, THAT is the evidence for a common lab library —
/// a camera that had to grow a pan is evidence against.
/// </remarks>
public sealed class RoomCamera
{
    public Vector3 Target { get; set; } = new(0f, 1.2f, 0f);

    public float Yaw { get; set; } = 0.9f;

    public float Pitch { get; set; } = 0.55f;

    public float Distance { get; set; } = 24f;

    public float FieldOfView { get; set; } = MathF.PI / 3.2f;

    public float NearPlane { get; set; } = 0.1f;

    public float FarPlane { get; set; } = 200f;

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

    public Matrix4x4 ViewProjection(float aspect)
    {
        var safeAspect = aspect > 0.0001f && float.IsFinite(aspect) ? aspect : 16f / 9f;
        var view = Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);
        return view * GraphicsMatrices.CreatePerspectiveVulkan(FieldOfView, safeAspect, NearPlane, FarPlane);
    }

    public void Orbit(float deltaX, float deltaY)
    {
        Yaw -= deltaX * 0.008f;
        Pitch = Math.Clamp(Pitch + (deltaY * 0.006f), 0.05f, 1.45f);
    }

    public void Zoom(float wheelDelta) => Distance = Math.Clamp(Distance - (wheelDelta * 1.4f), 2f, 70f);

    /// <summary>
    /// Slide the target across the floor, in the camera's own frame.
    /// </summary>
    /// <remarks>
    /// Along the ground rather than along the view: dragging up on a pitched camera should walk you
    /// further down the hall, not lift you off the floor. Clamped to the hall so a pan cannot lose
    /// the room behind you — a camera that can get lost is a camera you spend the session re-finding.
    /// </remarks>
    public void Pan(float deltaX, float deltaY)
    {
        var forward = new Vector3(-MathF.Sin(Yaw), 0f, -MathF.Cos(Yaw));
        var right = new Vector3(forward.Z, 0f, -forward.X);
        var scale = Distance * 0.0016f;

        var moved = Target + (right * (-deltaX * scale)) + (forward * (-deltaY * scale));
        Target = new Vector3(
            Math.Clamp(moved.X, -Room.HallHalfX, Room.HallHalfX),
            Math.Clamp(moved.Y, 0f, 6f),
            Math.Clamp(moved.Z, -Room.HallHalfZ, Room.HallHalfZ));
    }

    /// <summary>Look at one feature of the room from far enough back to see all of it.</summary>
    public void Frame(Vector3 centre, float size)
    {
        Target = centre;
        Distance = Math.Clamp(size * 2.6f, 4f, 70f);
    }
}
