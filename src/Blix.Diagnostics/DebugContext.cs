using Blix.Core;

namespace Blix.Diagnostics;

public sealed class DebugContext
{
    private readonly Stack<string> scopes = new();
    private readonly Dictionary<string, object> pendingControlValues;

    internal DebugContext(DebugState state, RenderFrameContext frame, Dictionary<string, object> pendingControlValues)
    {
        State = state;
        Frame = frame;
        this.pendingControlValues = pendingControlValues;
        Values = new DebugValues(this);
        Controls = new DebugControls(this);
        Draw = new DebugDrawChannel(this);
    }

    public DebugState State { get; }

    public RenderFrameContext Frame { get; }

    public DebugValues Values { get; }

    public DebugControls Controls { get; }

    public DebugDrawChannel Draw { get; }

    public IReadOnlyList<DebugValueEntry> ValueEntries => Values.Entries;

    public IReadOnlyList<DebugControlEntry> ControlEntries => Controls.Entries;

    public IDisposable Scope(string name)
    {
        scopes.Push(name);
        return new DebugScope(this);
    }

    internal string CurrentScope
    {
        get
        {
            if (scopes.Count == 0)
            {
                return string.Empty;
            }

            return string.Join("/", scopes.Reverse());
        }
    }

    internal string BuildPath(string name)
    {
        var scope = CurrentScope;
        return string.IsNullOrWhiteSpace(scope) ? name : $"{scope}/{name}";
    }

    internal bool TryGetPendingControlValue<T>(string path, out T value)
    {
        if (pendingControlValues.TryGetValue(path, out var boxed) && boxed is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    internal void SetPendingControlValue(string path, object value)
    {
        pendingControlValues[path] = value;
    }

    internal void ClearPendingControlValue(string path)
    {
        pendingControlValues.Remove(path);
    }

    private void PopScope()
    {
        scopes.Pop();
    }

    private sealed class DebugScope : IDisposable
    {
        private DebugContext? context;

        public DebugScope(DebugContext context)
        {
            this.context = context;
        }

        public void Dispose()
        {
            context?.PopScope();
            context = null;
        }
    }
}
