using System.Numerics;
using Blix.Core;

namespace Blix.Diagnostics;

// Immutable per-frame snapshot of everything diagnostics captured. Built once
// by DebugContext.Snapshot() inside DebugSystem.EndFrame(). After that point
// the frame is read-only and safe to hand to sinks (ImGui, JSON dump, etc.)
// or to hold past the live builder's lifetime — Freeze() relies on this.
//
// Entry lists are defensive copies of the channels' internal storage so a
// subsequent BeginFrame() that clears and refills those channels cannot
// retroactively mutate a snapshot. Each List<T> in the channel becomes a
// fresh T[] backing an IReadOnlyList<T> view here.
public sealed class DebugFrame
{
    internal DebugFrame(
        int number,
        double wallClockMs,
        RenderFrameContext frame,
        IReadOnlyList<DebugValueEntry> values,
        IReadOnlyList<DebugControlEntry> controls,
        IReadOnlyList<DebugDrawCommand> drawCommands,
        Matrix4x4 drawViewProjection,
        IReadOnlyList<DebugStatEntry> stats,
        IReadOnlyList<DebugTimerEntry> timers,
        IReadOnlyList<DebugEventEntry> events,
        string? selectedPath)
    {
        Number = number;
        WallClockMs = wallClockMs;
        Frame = frame;
        Values = values;
        Controls = controls;
        DrawCommands = drawCommands;
        DrawViewProjection = drawViewProjection;
        Stats = stats;
        Timers = timers;
        Events = events;
        SelectedPath = selectedPath;
    }

    // Monotonically increasing frame number. The first frame is 1; 0 is
    // reserved for "no frame produced yet."
    public int Number { get; }

    // Milliseconds since DebugSystem construction at the moment EndFrame()
    // sealed this snapshot. Suitable for x-axis on history graphs; not a
    // wall-clock date.
    public double WallClockMs { get; }

    public RenderFrameContext Frame { get; }

    public IReadOnlyList<DebugValueEntry> Values { get; }

    public IReadOnlyList<DebugControlEntry> Controls { get; }

    public IReadOnlyList<DebugDrawCommand> DrawCommands { get; }

    public Matrix4x4 DrawViewProjection { get; }

    public IReadOnlyList<DebugStatEntry> Stats { get; }

    public IReadOnlyList<DebugTimerEntry> Timers { get; }

    public IReadOnlyList<DebugEventEntry> Events { get; }

    // Path of the entity selected at the moment this frame was sealed,
    // or null if no selection. Captured so JSON dumps and frozen frames
    // preserve "what was selected then" independently of the live
    // DebugSystem.SelectedPath, which may have changed since.
    public string? SelectedPath { get; }
}
