using Blix.Core;
using Silk.NET.Core;

namespace Blix.Runtime.Silk;

/// <summary>
/// Window configuration passed to the runtime at construction.
/// </summary>
/// <remarks>
/// <b>Including the arguments every application was parsing for itself.</b> Six applications each carried
/// the same four-line loop looking for <c>--frames</c>, then threaded the answer into their own game loop,
/// which then had to count its own frames and ask to close. That is a per-application reimplementation of
/// something the host is better placed to do — it is the host that knows what a frame is.
/// <para>
/// One consequence worth noting: <c>VulkanHello</c> never had <c>--frames</c> at all, so its launcher
/// could not do a bounded run, and nothing said so — the flag was simply ignored. An application should not
/// have to opt in to the conventions of its own runtime.
/// </para>
/// </remarks>
public sealed record WindowOptions(string Title, int Width, int Height)
{
    public static readonly WindowOptions Default = new("Blix", 1280, 720);

    /// <summary>
    /// Close the window after this many rendered frames. Zero or less runs until asked to stop.
    /// </summary>
    /// <remarks>
    /// For bounded runs: a validation sweep, a screenshot, a smoke test in CI. Counted by the host in
    /// <c>OnRender</c>, so every application gets it whether or not it thought about it.
    /// </remarks>
    public int ExitAfterFrames { get; init; }

    /// <summary>Start with the diagnostics overlay live, for an application that produces diagnostics.</summary>
    public bool Diagnostics { get; init; }

    /// <summary>
    /// Images for the window icon, smallest first. Null uses the Blix mark; an empty list leaves
    /// whatever the platform would have shown.
    /// </summary>
    /// <remarks>
    /// A game shipping its own art sets this. The default is the engine's mark rather than nothing,
    /// because the alternative on Windows and Linux is the generic executable icon, which is what
    /// every unbranded window looks like and tells a player nothing about what they just launched.
    /// It is rasterised only when a window is actually created &mdash; see <see cref="BlixMark"/>.
    /// </remarks>
    public IReadOnlyList<RawImage>? Icons { get; init; }

    /// <summary>
    /// Write a JSON dump of this frame and carry on. 0 never dumps.
    /// </summary>
    /// <remarks>
    /// <b>Because F12 needs a person.</b> The dump has existed since the chassis arc and has been
    /// reachable exactly one way: someone at the keyboard. So a headless run could not produce the
    /// artifact an interactive run produces, which makes "send me a dump" impossible to check
    /// against — there is nothing to diff it with. A bounded run that writes one at a named frame is
    /// reproducible by construction: same arguments, same frame, same file.
    /// </remarks>
    public int DumpOnFrame { get; init; }

    /// <summary>
    /// Reads the arguments every windowed Blix application shares, leaving the rest to the
    /// application.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT own the whole command line. It reads the handful of flags that are
    /// about being a windowed Blix application at all, from the same <see cref="AppArgs"/> the
    /// application reads its own from, and anything neither reads is reported by
    /// <see cref="BlixApps.Main"/> rather than failed on. A value that cannot mean what it says
    /// (<c>--frames abc</c>) is an error, not a flag quietly skipped.
    /// </remarks>
    /// <summary>
    /// Run with the Vulkan validation layers, and fail the run if they report any error.
    /// </summary>
    /// <remarks>
    /// <c>BLIX_VK_VALIDATE=1</c> turns the layers on and prints what they say; this also makes what
    /// they say a verdict, so a bounded run in a gate fails on it instead of scrolling past it.
    /// </remarks>
    public bool Validate { get; init; }

    /// <summary>
    /// Seconds every frame advances by (<c>--step</c>), or null for the display's measured frame time.
    /// </summary>
    /// <remarks>
    /// The same flag as a headless run's, meaning the same thing (<see cref="FrameClock"/>): a bounded
    /// windowed run with a step tells its loop the same times every run. It does not change how fast frames
    /// arrive, which is still the display's, and it does not govern work budgeted by a stopwatch.
    /// </remarks>
    public double? Step { get; init; }

    public static WindowOptions FromArgs(AppArgs args, WindowOptions? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var result = defaults ?? Default;

        if (args.Int("frames") is { } frames) result = result with { ExitAfterFrames = Positive("frames", frames) };
        if (args.Int("dump-frame") is { } dumpFrame) result = result with { DumpOnFrame = Positive("dump-frame", dumpFrame) };
        if (args.Int("width") is { } width) result = result with { Width = Positive("width", width) };
        if (args.Int("height") is { } height) result = result with { Height = Positive("height", height) };
        if (args.String("title") is { } title) result = result with { Title = title };
        if (args.Flag("debug")) result = result with { Diagnostics = true };
        if (args.Flag("validate")) result = result with { Validate = true };
        if (args.Double("step") is { } step)
        {
            result = step > 0.0 && double.IsFinite(step)
                ? result with { Step = step }
                : throw new AppArgsException($"--step expects seconds above zero, got {step}.");
        }

        return result;
    }

    private static int Positive(string name, int value) =>
        value > 0 ? value : throw new AppArgsException($"--{name} expects a number above zero, got {value}.");
}
