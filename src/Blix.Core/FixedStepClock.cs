namespace Blix;

// Accumulator for fixed-step ticking. Each frame, `Accumulate(time)` adds the variable
// frame Δt to the running accumulator and returns how many fixed steps should fire
// this frame (zero or more). The Game base class owns one of these and dispatches
// `OnFixedUpdate` once per returned step.
//
// MaxStepsPerFrame caps the spiral-of-death: if the variable frame stalls (debugger,
// breakpoint, OS pause), the accumulator could otherwise build up seconds of
// unconsumed time and try to run hundreds of physics ticks the moment the app
// resumes — locking the main thread further and producing visible teleportation.
// Hitting the cap drops accumulated time (slow-mo recovery) rather than catching up.
// 4 steps is conventional; tune per game if needed.
public sealed class FixedStepClock
{
    private double accumulator;

    public FixedStepClock(double step, int maxStepsPerFrame = 4)
    {
        if (step <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(step), "Step must be positive.");
        }
        if (maxStepsPerFrame <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStepsPerFrame), "MaxStepsPerFrame must be positive.");
        }
        Step = step;
        MaxStepsPerFrame = maxStepsPerFrame;
    }

    public double Step { get; }

    public int MaxStepsPerFrame { get; }

    // Add the latest frame's Δt and return how many fixed steps fire this frame.
    // Each call to the consumer's OnFixedUpdate corresponds to one step of `Step`
    // seconds. Caller is responsible for actually ticking — this just produces a
    // count.
    public int Accumulate(Time frameTime)
    {
        accumulator += frameTime.Delta;
        var steps = 0;
        while (accumulator >= Step && steps < MaxStepsPerFrame)
        {
            accumulator -= Step;
            steps++;
        }
        // Cap hit: drop the unconsumed accumulator instead of catching up forever.
        if (accumulator >= Step)
        {
            accumulator = 0.0;
        }
        return steps;
    }
}
