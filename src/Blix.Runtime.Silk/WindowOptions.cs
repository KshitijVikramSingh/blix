using Blix.Core;

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
    /// Reads the arguments every Blix application shares, leaving the rest to the application.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT own the whole command line. An application knows its own flags and this does
    /// not try to; it claims the handful that are about being a Blix application at all, and ignores
    /// everything else rather than failing on it. Unknown arguments are the application's business.
    /// </remarks>
    public static WindowOptions FromArgs(string[] args, WindowOptions? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var result = defaults ?? Default;

        for (var i = 0; i < args.Length; i++)
        {
            var next = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--frames" when int.TryParse(next, out var frames) && frames > 0:
                    result = result with { ExitAfterFrames = frames };
                    break;
                case "--width" when int.TryParse(next, out var width) && width > 0:
                    result = result with { Width = width };
                    break;
                case "--height" when int.TryParse(next, out var height) && height > 0:
                    result = result with { Height = height };
                    break;
                case "--title" when next is not null:
                    result = result with { Title = next };
                    break;
                case "--debug":
                    result = result with { Diagnostics = true };
                    break;
            }
        }

        return result;
    }
}
