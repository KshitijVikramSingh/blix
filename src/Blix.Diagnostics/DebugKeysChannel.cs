using Blix.Core;

namespace Blix.Diagnostics;

/// <summary>Who stands behind a key in the key list.</summary>
public enum DebugKeySource
{
    /// <summary>The host's own: the overlay toggle, the dump, the perf HUD, picking. Nobody else may bind these.</summary>
    Host,

    /// <summary>Bound to a control. The engine drives it: the key does what clicking the control does.</summary>
    Control,

    /// <summary>Described by the application, which handles the key itself. A claim the engine cannot check.</summary>
    Declared,
}

/// <summary>One line of the key list.</summary>
/// <param name="Binding">What is pressed, as a person would write it: <c>Z</c>, <c>Space</c>, <c>Right-drag</c>.</param>
/// <param name="Description">What it does.</param>
/// <param name="Path">Who declared it, as a diagnostics path.</param>
/// <param name="Source">Whether the engine drives it or only reports it.</param>
public sealed record DebugKeyEntry(string Binding, string Description, string Path, DebugKeySource Source);

/// <summary>Every key an application answers to, gathered in one list the overlay shows.</summary>
/// <remarks>
/// <b>Five applications each kept a private list of <c>input[Key.X].Pressed</c> checks</b>, one wrote its
/// keys into its control labels by hand, and one printed them to the console at startup. A key bound to a
/// control (<c>Controls.Toggle("Sun", sun, key: Key.Z)</c>) is driven by the engine; a key the application
/// handles itself is described here, so the list is complete either way.
/// <para>
/// Declared every frame, like values, so a binding that only holds sometimes (a step key while paused) is
/// listed only then, and a dump carries the keys of the frame it froze. One check covers both kinds: two
/// declarers claiming one key raise an error event naming both, and a key the host reserves is refused
/// outright, because the host's keys still fire and both things would happen.
/// </para>
/// </remarks>
public sealed class DebugKeysChannel
{
    private readonly DebugContext context;
    private readonly IReadOnlyList<DebugKeyEntry> hostKeys;
    private readonly HashSet<string> reportedConflicts;
    private readonly List<DebugKeyEntry> entries = new();

    internal DebugKeysChannel(DebugContext context, IReadOnlyList<DebugKeyEntry> hostKeys, HashSet<string> reportedConflicts)
    {
        this.context = context;
        this.hostKeys = hostKeys;
        this.reportedConflicts = reportedConflicts;
    }

    /// <summary>This frame's list: the host's keys first, then everything declared, in declaration order.</summary>
    public IReadOnlyList<DebugKeyEntry> Entries => hostKeys.Count == 0 ? entries : hostKeys.Concat(entries).ToArray();

    /// <summary>Lists a key the application handles itself.</summary>
    public void Describe(Key key, string what) => Add(Name(key), what, context.BuildPath(Name(key)), DebugKeySource.Declared);

    /// <summary>Lists a gesture the application handles itself: <c>"Right-drag"</c>, <c>"Wheel"</c>, <c>"Shift+W"</c>.</summary>
    public void Describe(string gesture, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gesture);
        Add(gesture, what, context.BuildPath(gesture), DebugKeySource.Declared);
    }

    /// <summary>Binds a key to the control at <paramref name="controlPath"/>; true on the frame it is pressed.</summary>
    internal bool Bind(Key key, string what, string controlPath)
    {
        Add(Name(key), what, controlPath, DebugKeySource.Control);
        return context.Input?[key].Pressed == true;
    }

    /// <summary>How a key is written in the list and beside its control.</summary>
    public static string Name(Key key) => key switch
    {
        >= Key.Number0 and <= Key.Number9 => ((int)(key - Key.Number0)).ToString(),
        Key.GraveAccent => "`",
        _ => key.ToString(),
    };

    private void Add(string binding, string what, string path, DebugKeySource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        foreach (var host in hostKeys)
        {
            if (string.Equals(host.Binding, binding, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"'{binding}' is the host's ({host.Description}) and still fires when pressed, so {path} " +
                    "cannot have it too. Pick another key.");
            }
        }

        foreach (var other in entries)
        {
            if (other.Path == path) return; // the same declaration, made twice this frame
            if (!string.Equals(other.Binding, binding, StringComparison.OrdinalIgnoreCase)) continue;
            var pair = string.CompareOrdinal(other.Path, path) < 0 ? $"{binding}|{other.Path}|{path}" : $"{binding}|{path}|{other.Path}";
            if (reportedConflicts.Add(pair))
            {
                context.Events.Error($"'{binding}' is claimed twice: {other.Path} ({other.Description}) and {path} ({what}).");
            }
        }

        entries.Add(new DebugKeyEntry(binding, what, path, source));
    }
}
