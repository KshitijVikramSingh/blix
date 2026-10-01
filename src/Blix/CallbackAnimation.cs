namespace Blix;

// Escape hatch: wraps an arbitrary Func<double, bool> (the delta, still running?) as an IAnimation. Use this when
// the desired animation doesn't fit one of the typed concrete classes (yet). When the
// callback returns false, the host removes the animation in the same tick.
//
// Typed animations are the goal — this exists so a one-off doesn't have to grow into
// a new class. If the same callback shape appears twice, promote to a typed class.
public sealed class CallbackAnimation : IAnimation
{
    private readonly Func<double, bool> callback;

    public CallbackAnimation(Func<double, bool> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        this.callback = callback;
    }

    public bool Advance(double delta) => callback(delta);
}
