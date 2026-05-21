namespace Blix;

// Curve-driven float animation: evaluates a curve at `time.Total - StartTime` and
// writes the result through a setter closure. Intended for things like material
// uniforms, FoV, light intensity — anywhere a single scalar varies over time.
//
// Setter is a closure rather than a typed reference because float-valued state lives
// in many different places (Material.SetUniform(name, ...), DirectionalLight.Intensity,
// Camera3D.VerticalFieldOfView, etc.) and forcing each to grow an "animatable field"
// abstraction would be busywork. The closure captures whatever needs to be written.
public sealed class FloatAnimation : IAnimation
{
    public ICurve<float> Curve { get; init; } = null!;

    public Action<float> Setter { get; init; } = null!;

    // The time at which Evaluate(0) should be the result. Defaults to 0 — set this in
    // OnLoad to the current Time.Total if the animation should start "now."
    public double StartTime { get; init; }

    public bool Sample(Time time)
    {
        var local = time.Total - StartTime;
        // Always pin the curve's current value first — for finite curves, this means
        // the final clamped value is written on the last tick before removal.
        Setter(Curve.Evaluate(local));
        // Infinite curves run forever (no IFiniteCurve to consult). Finite curves stay
        // alive until local exceeds their declared duration.
        return Curve is not IFiniteCurve<float> finite || local < finite.Duration;
    }
}
