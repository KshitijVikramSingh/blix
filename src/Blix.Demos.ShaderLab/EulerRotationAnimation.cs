using System.Numerics;
using Blix;

// Demo-specific animation: rotate a Transform3D at fixed angular velocities around the
// world Y, X, and Z axes. Rotation is computed from absolute time (not accumulated from
// per-frame deltas) so values are stable and identical to the previous inline
// `frame.TotalTime * speed` formulas the demo used to do before animations existed.
//
// Lives in the demo rather than Blix because this is a closed-form, very
// specific rotation pattern. If a second demo wants the same shape, promote.
public sealed class EulerRotationAnimation : IAnimation
{
    public Transform3D Target { get; init; } = null!;

    public Vector3 RadiansPerSecond { get; init; }

    public bool Sample(Time time)
    {
        var t = (float)time.Total;
        Target.Rotation =
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, t * RadiansPerSecond.Y) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, t * RadiansPerSecond.X) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, t * RadiansPerSecond.Z);
        return true;
    }
}
