namespace Blix;

/// <summary>A host's frame clock: one <see cref="Time"/> sample per frame, which every callback that frame sees.</summary>
/// <remarks>
/// <para>
/// <b>One sample, not one per callback.</b> The windowed host is driven by two callbacks with deltas of their
/// own, and it used to build OnRender's time from the update's total and the render's delta — two clocks
/// in one value. A frame now advances this once, in its update, and the render reads the same sample.
/// </para>
/// <para>
/// <b>Stepped or measured.</b> With a <see cref="Step"/>, every frame advances by exactly that many seconds,
/// whatever the wall clock did: that is what <c>--step</c> means in either host, and what makes a bounded
/// run's application time the same every run. Without one, a frame advances by the delta the host
/// measured. This governs the time callbacks are told, not how long anything takes: a budget measured
/// with a stopwatch (a texture drain, an async load) still runs on the wall clock.
/// </para>
/// </remarks>
public sealed class FrameClock
{
    /// <param name="step">Seconds per frame, or null to take each frame's measured delta.</param>
    public FrameClock(double? step = null)
    {
        if (step is { } s && !(s > 0.0 && double.IsFinite(s)))
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "a frame step must be a finite number of seconds above zero.");
        }

        Step = step;
    }

    /// <summary>Seconds every frame advances by, or null when frames take their measured delta.</summary>
    public double? Step { get; }

    /// <summary>This frame's sample; zero until the first <see cref="Advance"/>.</summary>
    public Time Current { get; private set; }

    /// <summary>Starts the next frame: advances by <see cref="Step"/>, or by <paramref name="measuredDelta"/> without one.</summary>
    /// <param name="measuredDelta">What the host measured since the last frame; ignored when stepped, and never negative.</param>
    public Time Advance(double measuredDelta)
    {
        var delta = Step ?? (double.IsFinite(measuredDelta) ? Math.Max(0.0, measuredDelta) : 0.0);
        Current = new Time(Current.Total + delta, delta);
        return Current;
    }
}
