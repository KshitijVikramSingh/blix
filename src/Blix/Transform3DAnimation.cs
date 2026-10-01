using System.Numerics;

namespace Blix;

// Drives any combination of Position, Rotation, and Scale on a Transform3D from
// independent curves. Channels are nullable — null means "leave this channel alone."
// Mix and match: pure-position animations, rotation + scale together, all three, etc.
//
// Lifecycle: the animation stays alive as long as ANY non-null channel is still
// running. A channel that holds a finite curve drops out once local time exceeds its
// duration; an infinite-curve channel keeps the animation alive forever. When all
// non-null channels have finished (or all three channels are null), Advance returns
// false and the host removes the animation.
//
// The final tick still writes the clamped end value of finite channels, so the
// Transform stays pinned at the curve's end state after removal.
public sealed class Transform3DAnimation : IAnimation
{
    public Transform3D Target { get; init; } = null!;

    public ICurve<Vector3>? Position { get; init; }

    public ICurve<Quaternion>? Rotation { get; init; }

    public ICurve<Vector3>? Scale { get; init; }

    // Seconds before the curves start; until then each channel holds its value at time 0.
    public double Delay { get; init; }

    // Seconds advanced so far. Setting it moves the clock only: the target is written at the next Advance
    // (Advance(0) writes it now).
    public double Elapsed { get; set; }

    public bool Advance(double delta)
    {
        Elapsed += delta;
        var local = Math.Max(0.0, Elapsed - Delay);

        // `alive` starts false and any non-null still-running channel flips it true.
        // If all three curves are null, the animation has nothing to do and removes
        // itself in the same tick — degenerate but handled gracefully.
        var alive = false;

        if (Position is not null)
        {
            Target.Position = Position.Evaluate(local);
            alive |= IsRunning(Position, local);
        }

        if (Rotation is not null)
        {
            Target.Rotation = Rotation.Evaluate(local);
            alive |= IsRunning(Rotation, local);
        }

        if (Scale is not null)
        {
            Target.Scale = Scale.Evaluate(local);
            alive |= IsRunning(Scale, local);
        }

        return alive;
    }

    private static bool IsRunning<T>(ICurve<T> curve, double local) =>
        curve is not IFiniteCurve<T> finite || local < finite.Duration;
}
