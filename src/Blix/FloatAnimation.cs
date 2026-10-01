namespace Blix;

// Curve-driven float animation: evaluates a curve at its own elapsed time (less any Delay) and
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

    // Seconds before the curve starts; until then it holds its value at time 0.
    public double Delay { get; init; }

    // Seconds advanced so far. Setting it moves the clock only: the target is written at the next Advance
    // (Advance(0) writes it now).
    public double Elapsed { get; set; }

    public bool Advance(double delta)
    {
        Elapsed += delta;
        var local = Math.Max(0.0, Elapsed - Delay);
        // Always pin the curve's current value first — for finite curves, this means
        // the final clamped value is written on the last tick before removal.
        Setter(Curve.Evaluate(local));
        // Infinite curves run forever (no IFiniteCurve to consult). Finite curves stay
        // alive until local exceeds their declared duration.
        return Curve is not IFiniteCurve<float> finite || local < finite.Duration;
    }
}
