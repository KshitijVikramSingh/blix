namespace Blix.Diagnostics;

public sealed class DebugValues
{
    private readonly DebugContext context;
    private readonly List<DebugValueEntry> entries = new();

    internal DebugValues(DebugContext context)
    {
        this.context = context;
    }

    public IReadOnlyList<DebugValueEntry> Entries => entries;

    public void Value(string name, object? value)
    {
        entries.Add(new DebugValueEntry(context.BuildPath(name), context.CurrentScope, name, value));
    }
}

public sealed class DebugControls
{
    private readonly DebugContext context;
    private readonly List<DebugControlEntry> entries = new();

    internal DebugControls(DebugContext context)
    {
        this.context = context;
    }

    public IReadOnlyList<DebugControlEntry> Entries => entries;

    public bool Toggle(string name, bool value)
    {
        var path = context.BuildPath(name);
        if (context.TryGetPendingControlValue<bool>(path, out var pending))
        {
            value = pending;
        }

        entries.Add(new DebugControlEntry(path, context.CurrentScope, name, DebugControlKind.Boolean, value));
        return value;
    }

    public float Float(string name, float value, float min, float max)
    {
        var path = context.BuildPath(name);
        if (context.TryGetPendingControlValue<float>(path, out var pending))
        {
            value = pending;
        }

        value = Math.Clamp(value, min, max);
        entries.Add(new DebugControlEntry(path, context.CurrentScope, name, DebugControlKind.Float, value, min, max));
        return value;
    }

    public int Enum(string name, int value, IReadOnlyList<string> options)
    {
        var path = context.BuildPath(name);
        if (context.TryGetPendingControlValue<int>(path, out var pending))
        {
            value = pending;
        }

        value = Math.Clamp(value, 0, Math.Max(options.Count - 1, 0));
        entries.Add(new DebugControlEntry(path, context.CurrentScope, name, DebugControlKind.Enum, value, Options: options));
        return value;
    }

    /// <summary>A line of text the reader can edit, read back through the same call.</summary>
    /// <remarks>
    /// The kind that makes a declared value addressable by NAME rather than by number — a clip,
    /// a bone, an output path. Everything else here is a knob you nudge; this is the one you type
    /// the answer into, and it is what a tool needs before its state can describe a subject rather
    /// than only how that subject is displayed.
    /// </remarks>
    public string Text(string name, string value, int maxLength = 128)
    {
        var path = context.BuildPath(name);
        if (context.TryGetPendingControlValue<string>(path, out var pending))
        {
            value = pending ?? string.Empty;
        }

        value ??= string.Empty;
        if (maxLength > 0 && value.Length > maxLength) value = value[..maxLength];

        entries.Add(new DebugControlEntry(
            path, context.CurrentScope, name, DebugControlKind.Text, value, MaxLength: Math.Max(1, maxLength)));
        return value;
    }

    public bool Button(string name)
    {
        var path = context.BuildPath(name);
        var pressed = false;
        if (context.TryGetPendingControlValue<bool>(path, out var pending))
        {
            pressed = pending;
            context.ClearPendingControlValue(path);
        }

        entries.Add(new DebugControlEntry(path, context.CurrentScope, name, DebugControlKind.Button, pressed));
        return pressed;
    }
}
