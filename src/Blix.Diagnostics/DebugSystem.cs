using Blix.Core;

namespace Blix.Diagnostics;

public sealed class DebugSystem
{
    private readonly Dictionary<string, object> pendingControlValues = new(StringComparer.Ordinal);

    public DebugState State { get; } = new();

    public DebugContext? Current { get; private set; }

    public DebugContext BeginFrame(RenderFrameContext frame)
    {
        Current = new DebugContext(State, frame, pendingControlValues);
        return Current;
    }

    public void Run(params IDebuggable[] debuggables)
    {
        if (Current is null)
        {
            throw new InvalidOperationException("BeginFrame must be called before running debug contributors.");
        }

        foreach (var debuggable in debuggables)
        {
            using (Current.Scope(debuggable.DebugName))
            {
                debuggable.Debug(Current);
            }
        }
    }

    public void SetControlValue(string path, object value)
    {
        pendingControlValues[path] = value;
    }
}
