using System.Numerics;
using Blix.Graphics;

namespace Blix;

public sealed class Transform3D
{
    public Vector3 Position { get; set; } = Vector3.Zero;

    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    public Vector3 Scale { get; set; } = Vector3.One;

    // Local-forward convention matches Camera3D: identity rotation looks down -Z. A
    // GameObject with default rotation faces the same direction as a default camera.
    public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Rotation);

    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Rotation);

    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);

    public Matrix4x4 ToMatrix() => GraphicsMatrices.CreateModel(Position, Rotation, Scale);

    private Transform3D? parent;

    // Optional parent in a transform hierarchy. Position/Rotation/Scale stay
    // LOCAL (relative to the parent); WorldMatrix composes the chain. Assigning
    // a parent that would form a cycle throws — checked once, on assignment.
    public Transform3D? Parent
    {
        get => parent;
        set
        {
            for (var t = value; t is not null; t = t.parent)
            {
                if (ReferenceEquals(t, this))
                {
                    throw new InvalidOperationException(
                        "Transform3D.Parent would create a cycle in the hierarchy.");
                }
            }
            parent = value;
        }
    }

    // The local TRS composed up the parent chain into a world-space model matrix.
    // Row-vector composition (matching Skeleton's hierarchy walk): a vertex flows
    // child-local outward, so world = local * parentWorld. No dirty-flag cache —
    // ToMatrix is a few multiplies and hierarchies here are shallow; add caching
    // when a deep rig actually needs it.
    public Matrix4x4 WorldMatrix =>
        parent is null ? ToMatrix() : ToMatrix() * parent.WorldMatrix;

    // World-space position/rotation, for consumers that live outside the hierarchy
    // (a chase camera following a hull, a projectile spawned at a barrel tip). Root
    // transforms short-circuit to the local value; deeper ones decompose WorldMatrix.
    public Vector3 WorldPosition
    {
        get
        {
            if (parent is null) return Position;
            Matrix4x4.Decompose(WorldMatrix, out _, out _, out var translation);
            return translation;
        }
    }

    public Quaternion WorldRotation
    {
        get
        {
            if (parent is null) return Rotation;
            Matrix4x4.Decompose(WorldMatrix, out _, out var rotation, out _);
            return rotation;
        }
    }

    // Re-parent this transform. With keepWorldPose, the local TRS is recomputed so
    // the WORLD pose is unchanged across the switch — e.g. a projectile detaching
    // from a moving barrel (SetParent(null, keepWorldPose: true)) keeps its current
    // world position + orientation and flies straight, instead of snapping to the
    // barrel's local frame reinterpreted as world. Mirrors Skeleton.CreateRestPose's
    // local = world * inverse(parentWorld).
    public void SetParent(Transform3D? newParent, bool keepWorldPose = false)
    {
        if (!keepWorldPose)
        {
            Parent = newParent;
            return;
        }

        var world = WorldMatrix;
        var localMatrix = world;
        if (newParent is not null && Matrix4x4.Invert(newParent.WorldMatrix, out var inverseParent))
        {
            localMatrix = world * inverseParent;
        }

        Parent = newParent;   // cycle-checked
        if (Matrix4x4.Decompose(localMatrix, out var scale, out var rotation, out var translation))
        {
            Position = translation;
            Rotation = rotation;
            Scale = scale;
        }
    }

    public void LookAt(Vector3 target, Vector3 up)
    {
        // Mirrors Camera3D.LookAt: solves the rotation so local -Z aligns with
        // (target - Position). Row-vector basis matrix matches Quaternion.CreateFromRotationMatrix.
        var forward = Vector3.Normalize(target - Position);
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        var actualUp = Vector3.Cross(right, forward);

        var basis = new Matrix4x4(
            right.X, right.Y, right.Z, 0.0f,
            actualUp.X, actualUp.Y, actualUp.Z, 0.0f,
            -forward.X, -forward.Y, -forward.Z, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f);

        Rotation = Quaternion.CreateFromRotationMatrix(basis);
    }
}
