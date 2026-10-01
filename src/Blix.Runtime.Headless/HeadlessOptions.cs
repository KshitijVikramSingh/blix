using Blix.Core;

namespace Blix.Runtime.Headless;

/// <summary>How a headless run is sized, clocked and bounded.</summary>
/// <remarks>
/// <b>The same flags as a window, meaning the same things.</b> <c>--frames</c>, <c>--dump-frame</c>,
/// <c>--width</c>, <c>--height</c> and <c>--debug</c> are read by <see cref="FromArgs"/> exactly as
/// <c>WindowOptions.FromArgs</c> reads them, so one command line runs one loop with or without a
/// window. What a window adds of its own (<c>--title</c>) is not read here, and is reported unread
/// if it is passed.
/// </remarks>
/// <param name="Width">The size the loop is told it renders at, in pixels.</param>
/// <param name="Height">The size the loop is told it renders at, in pixels.</param>
/// <param name="ExitAfterFrames">Stop after this many frames. 0 runs until the loop asks to close.</param>
/// <param name="DumpOnFrame">Write the diagnostics JSON dump for this frame. 0 never dumps.</param>
/// <param name="Diagnostics">Start with diagnostics enabled.</param>
/// <param name="Step">Seconds per frame. Fixed, never the wall clock.</param>
public sealed record HeadlessOptions(
    int Width = 1280,
    int Height = 720,
    int ExitAfterFrames = 0,
    int DumpOnFrame = 0,
    bool Diagnostics = false,
    double Step = 1.0 / 60.0)
{
    /// <summary>A 1280×720 run at 60 steps a second, until the loop closes it.</summary>
    public static HeadlessOptions Default { get; } = new();

    /// <summary>
    /// Reads the arguments every Blix loop shares, leaving the rest to the application.
    /// </summary>
    /// <remarks>
    /// <c>--step</c> means what it means in a window (<c>WindowOptions.Step</c>): every frame advances by
    /// exactly that much. Here it is always set, because a headless run has no display to measure time by.
    /// A value that cannot mean what it says is an error.
    /// </remarks>
    public static HeadlessOptions FromArgs(AppArgs args, HeadlessOptions? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var result = defaults ?? Default;

        if (args.Int("frames") is { } frames) result = result with { ExitAfterFrames = Positive("frames", frames) };
        if (args.Int("dump-frame") is { } dumpFrame) result = result with { DumpOnFrame = Positive("dump-frame", dumpFrame) };
        if (args.Int("width") is { } width) result = result with { Width = Positive("width", width) };
        if (args.Int("height") is { } height) result = result with { Height = Positive("height", height) };
        if (args.Flag("debug")) result = result with { Diagnostics = true };
        if (args.Double("step") is { } step)
        {
            result = step > 0
                ? result with { Step = step }
                : throw new AppArgsException($"--step expects seconds above zero, got {step}.");
        }

        return result;
    }

    private static int Positive(string name, int value) =>
        value > 0 ? value : throw new AppArgsException($"--{name} expects a number above zero, got {value}.");
}
