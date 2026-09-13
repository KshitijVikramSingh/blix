using System.Diagnostics;
using Blix.Core;

namespace Blix.Diagnostics;

public sealed class DebugContext
{
    private readonly Stack<string> scopes = new();
    private readonly Dictionary<string, object> pendingControlValues;

    internal DebugContext(DebugState state, RenderFrameContext frame, Dictionary<string, object> pendingControlValues, int frameNumber, Stopwatch clock, string? selectedPath)
    {
        State = state;
        Frame = frame;
        this.pendingControlValues = pendingControlValues;
        FrameNumber = frameNumber;
        SelectedPath = selectedPath;
        Values = new DebugValues(this);
        Controls = new DebugControls(this);
        Draw = new DebugDrawChannel(this);
        Stats = new DebugStatsChannel(this);
        Timers = new DebugTimersChannel(this);
        Events = new DebugEventsChannel(this, clock);
    }

    public DebugState State { get; }

    public RenderFrameContext Frame { get; }

    // Monotonically increasing frame number assigned by DebugSystem.BeginFrame.
    // Producers can read it (e.g. to log "saw this on frame N"); the same
    // number is stamped onto the DebugFrame produced by Snapshot().
    public int FrameNumber { get; }

    public DebugStatsChannel Stats { get; }

    public DebugTimersChannel Timers { get; }

    public DebugEventsChannel Events { get; }

    // Snapshot of DebugSystem.SelectedPath taken at BeginFrame. Producers
    // can read this from inside Debug/EmitGeometry/Inspect to specialise
    // their behaviour for the selected entity — e.g. GltfSceneInstance
    // suppresses the per-submesh AABB for the selected submesh to avoid
    // drawing two overlapping outlines. Stays stable for the whole
    // frame even if Select() is called mid-frame (the change applies
    // next frame).
    public string? SelectedPath { get; }

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

    // Seals the current builder state into an immutable DebugFrame.
    //
    // Defensive-copies every entry list because the channels keep mutable
    // List<T> storage that the next frame will append to. Returning the
    // lists by reference would let a later BeginFrame() retroactively
    // mutate a snapshot held by a sink or by Freeze().
    internal DebugFrame Snapshot(double wallClockMs, string? selectedPath)
    {
        return new DebugFrame(
            FrameNumber,
            wallClockMs,
            Frame,
            Values.Entries.ToArray(),
            Controls.Entries.ToArray(),
            Draw.Commands.ToArray(),
            Draw.Views.ToArray(),
            Stats.Entries.ToArray(),
            Timers.Entries.ToArray(),
            Events.Entries.ToArray(),
            selectedPath);
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
