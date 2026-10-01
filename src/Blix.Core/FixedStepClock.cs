namespace Blix;

/// <summary>
/// The simulation clock a host runs an <see cref="IFixedGameLoop"/> on: frame time goes in, whole fixed steps
/// come out, each on a <see cref="Time"/> of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>The host's, not the game's.</b> Both hosts own one and run it after <see cref="IGameLoop.OnUpdate"/>, so
/// the order in a frame is the host's to state and the same in a window and headless: input held still,
/// the update (where input becomes intents and the time scale is chosen), every whole step, then render.
/// </para>
/// <para>
/// <b>Its own clock.</b> A step's <see cref="Time.Total"/> is simulation time, advanced by one step per step:
/// it runs at the time scale, stands still while paused, and is set by <see cref="Reset"/>. The frame's
/// <c>Total</c> is application time, and the two are different numbers on purpose.
/// </para>
/// <para>
/// <b>A cap in seconds, not in steps.</b> A frame contributes at most <see cref="MaxFrameDelta"/> before the
/// scale, so a stall (a breakpoint, an OS pause) drops time rather than replaying it, and a fast-forward still
/// runs every step it asked for: at 30 Hz and 6×, a long frame is 45 steps. Capping steps instead would make
/// the scale lie whenever it was above what the cap allowed.
/// </para>
/// </remarks>
public sealed class FixedStepClock
{
    /// <summary>The most frame time, in seconds, one frame contributes before the time scale.</summary>
    public const double MaxFrameDelta = 0.25;

    private double residual;
    private int resets;

    /// <summary>Simulation time at the last step: where the next step starts.</summary>
    public double Total { get; private set; }

    /// <summary>Simulation time accumulated toward the next step, in seconds; under one step after <see cref="Run"/>.</summary>
    public double Residual => residual;

    /// <summary>How far the residual is toward the next step, in [0, 1): what a renderer interpolates by.</summary>
    public double Alpha { get; private set; }

    /// <summary>Steps run by the last <see cref="Run"/>.</summary>
    public int LastSteps { get; private set; }

    /// <summary>
    /// Adds a frame's time, at the loop's scale, and calls <see cref="IFixedGameLoop.OnFixedUpdate"/> once per
    /// whole step. The step and scale are read now, after the update that may have changed them.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The step is not a finite number above zero; the scale is negative or not finite; or the frame, at that
    /// scale, asks for more steps than can be counted (a finite scale times a finite delta can still overflow).
    /// </exception>
    public void Run(IFixedGameLoop loop, double frameDelta)
    {
        ArgumentNullException.ThrowIfNull(loop);
        var step = loop.FixedStep;
        if (!double.IsFinite(step) || step <= 0.0)
        {
            throw new InvalidOperationException($"FixedStep must be a finite number of seconds above zero, got {step}.");
        }

        var scale = loop.FixedTimeScale;
        if (!double.IsFinite(scale) || scale < 0.0)
        {
            throw new InvalidOperationException($"FixedTimeScale must be finite and zero or above (zero pauses), got {scale}.");
        }

        var delta = double.IsFinite(frameDelta) ? Math.Clamp(frameDelta, 0.0, MaxFrameDelta) : 0.0;
        var accumulated = residual + (delta * scale);

        // Counted up front, never by subtracting until the residual runs out: past 2^52 steps' worth,
        // subtracting one step leaves the residual unchanged, and infinity minus a step is infinity. Either
        // would loop forever, from inputs that each passed the checks above.
        var wanted = Math.Floor(accumulated / step);
        if (!double.IsFinite(wanted) || wanted > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"FixedTimeScale {scale} over a {delta}s frame asks for {wanted} steps of {step}s; that many cannot be run.");
        }

        var count = (int)wanted;
        residual = Math.Max(0.0, accumulated - (count * step));

        // A reset from inside a step ends the run: the time it set has nothing left to step.
        var resetsBefore = resets;
        var steps = 0;
        while (steps < count && resets == resetsBefore)
        {
            Total += step;
            steps++;
            loop.OnFixedUpdate(new Time(Total, step));
        }

        LastSteps = steps;
        Alpha = Math.Clamp(residual / step, 0.0, 1.0);
    }

    /// <summary>Clears the residual and sets simulation time: a restart, a load, a scrub.</summary>
    /// <remarks>Called from inside a step, it also ends that frame's run: there is nothing left to step.</remarks>
    public void Reset(double total = 0.0)
    {
        if (!double.IsFinite(total)) throw new ArgumentOutOfRangeException(nameof(total), total, "simulation time must be finite.");
        Total = total;
        residual = 0.0;
        resets++;
        Alpha = 0.0;
    }
}
