using System.Diagnostics;
using Blix.Core;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Diagnostics;

// Owner of the diagnostics frame lifecycle. Per frame:
//
//   debugSystem.BeginFrame(renderFrameContext);     // mints a DebugContext
//   debugSystem.Run(IDebuggable[]);                 // pull producers
//   ... game render & push-side producers ...
//   debugSystem.EndFrame();                         // snapshot -> ring
//
// The current frame is also reachable via IDebugHost.CurrentDebug so
// producers outside the pull cycle (renderers, etc.) can append draws or
// values during render. After EndFrame the Current context is cleared;
// the immutable DebugFrame is what survives, both in the history ring
// and (if Freeze was called) as the FrozenFrame.
//
// Freeze holds a strong reference separately from the ring so a long
// inspection survives ring overwrite (default 120 frames = ~2s @ 60fps).
//
// Not thread-safe. All calls run on the GL thread.
public sealed class DebugSystem
{
    public const int DefaultHistoryCapacity = 120;

    // Name used for the system-owned frame-level CPU timer pushed during
    // EndFrame. Exposed as a constant so sinks can locate it without
    // string-matching against an internal convention.
    public const string FrameTimerName = "frame";

    // Scope prefix the runtime uses when auto-emitting selection-related
    // draws and routing IDebugInspectable output. Anything under this
    // prefix on the path tree belongs to the current selection.
    public const string SelectionScope = "selection";

    // Bright magenta — high contrast against Sponza's warm interior
    // lighting AND distinct from the cyan that IDebugGeometrySource
    // producers tend to use for their per-entity bounds.
    private static readonly GraphicsColor SelectionHighlightColor = new(1.0f, 0.15f, 0.85f, 1.0f);

    // Reusable buffer for CollectSelectables. Cleared and refilled each
    // call so callers can call repeatedly without allocation churn.
    private readonly List<DebugSelectable> selectableScratch = new();

    private readonly Dictionary<string, object> pendingControlValues = new(StringComparer.Ordinal);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<IDebugFrameSink> sinks = new();
    private readonly List<IDebugContributor> contributors = new();
    private int frameCounter;
    private long frameStartTicks;

    public DebugSystem()
        : this(DefaultHistoryCapacity)
    {
    }

    public DebugSystem(int historyCapacity)
    {
        History = new DebugFrameHistory(historyCapacity);
    }

    public DebugState State { get; } = new();

    public DebugFrameHistory History { get; }

    // The live, mutable builder for the in-flight frame. Null between
    // EndFrame and the next BeginFrame.
    public DebugContext? Current { get; private set; }

    // Most recent finished frame, or null until the first EndFrame.
    public DebugFrame? LatestFrame => History.Latest;

    // Most recent per-pass/per-draw GPU command packet (shader name, live
    // uniform values, push constants, texture bindings). Set by the runtime
    // after each Execute; read by the overlay's Pipeline tab to inspect what
    // was actually fed to the shaders. Lags the live frame by one (it's
    // produced during Execute, after the overlay for that frame is built).
    public FrameDebugPacket? LatestFramePacket { get; private set; }

    public void SetFramePacket(FrameDebugPacket packet) => LatestFramePacket = packet;

    // When non-null, sinks should render this frame read-only instead of
    // Current. Held independently of the ring so it survives overwrite.
    public DebugFrame? FrozenFrame { get; private set; }

    public bool IsFrozen => FrozenFrame is not null;

    public DebugContext BeginFrame(RenderFrameContext frame)
    {
        frameCounter++;
        frameStartTicks = Stopwatch.GetTimestamp();
        Current = new DebugContext(State, frame, pendingControlValues, frameCounter, clock, SelectedPath);
        return Current;
    }

    public void AddSink(IDebugFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sinks.Add(sink);
    }

    public bool RemoveSink(IDebugFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return sinks.Remove(sink);
    }

    public IReadOnlyList<IDebugFrameSink> Sinks => sinks;

    // Register a contributor for the life of the system. Each frame:
    //   - if it implements IDebuggable, Run() calls Debug() inside an
    //     auto-scope of its DebugName;
    //   - if it implements a UI-layer interface (IDebugUi in the OpenTK
    //     runtime), the runtime discovers it via Contributors.
    //
    // Re-registering the same instance is a no-op rather than a duplicate;
    // we'd otherwise produce duplicate scopes on the same frame.
    public void Register(IDebugContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        if (!contributors.Contains(contributor))
        {
            contributors.Add(contributor);
        }
    }

    public bool Unregister(IDebugContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        return contributors.Remove(contributor);
    }

    public IReadOnlyList<IDebugContributor> Contributors => contributors;

    // === Selection ==========================================================

    // Stable identity of the currently-selected entity, null if no
    // selection. Persists across frames; survives Freeze.
    public string? SelectedPath { get; private set; }

    // World-space AABB of the selected entity at the moment Select was
    // called. Cached so the runtime can auto-emit a highlight outline
    // without re-walking selectables every frame. Accepts staleness if
    // the entity moves between selections (re-pick refreshes). For
    // dynamic scenes this can be lifted later by re-collecting per
    // frame; for static scenes (Sponza submeshes) it's free fidelity.
    public Bounds3? SelectedBounds { get; private set; }

    public void Select(string entityPath, Bounds3 bounds)
    {
        ArgumentException.ThrowIfNullOrEmpty(entityPath);
        SelectedPath = entityPath;
        SelectedBounds = bounds;
    }

    public void ClearSelection()
    {
        SelectedPath = null;
        SelectedBounds = null;
    }

    // Walks every registered IDebugSelectable and collects their
    // pickable entries into the shared scratch list. The list is
    // returned by ref-friendly IReadOnlyList; calling again clobbers
    // the previous result, so callers must consume immediately.
    //
    // Demo flow: build a screen-ray, call CollectSelectables, raycast
    // against bounds, call Select on the closest hit.
    public IReadOnlyList<DebugSelectable> CollectSelectables()
    {
        selectableScratch.Clear();
        for (var i = 0; i < contributors.Count; i++)
        {
            if (contributors[i] is IDebugSelectable selectable)
            {
                selectable.CollectSelectables(selectableScratch);
            }
        }
        return selectableScratch;
    }

    public void Run(params IDebuggable[] debuggables)
    {
        if (Current is null)
        {
            throw new InvalidOperationException("BeginFrame must be called before running debug contributors.");
        }

        // Two-pass walk over the registry:
        //
        //   1. IDebuggable.Debug() runs unconditionally. This is the state
        //      producer (Values/Stats/Timers/Events/Controls). Subsystem
        //      state populates here.
        //   2. IDebugGeometrySource.EmitGeometry() runs only if the
        //      producer's path is visible per DebugState.IsPathVisible.
        //      Disabling a layer skips the call entirely — zero CPU.
        //
        // Doing geometry as a second pass means all state from this frame
        // is already populated when geometry emits (so a producer that
        // implements both can read its own Stats during EmitGeometry if
        // it wants, though we don't depend on that ordering today).
        foreach (var contributor in contributors)
        {
            if (contributor is IDebuggable debuggable)
            {
                using (Current.Scope(debuggable.DebugName))
                {
                    debuggable.Debug(Current);
                }
            }
        }

        foreach (var debuggable in debuggables)
        {
            using (Current.Scope(debuggable.DebugName))
            {
                debuggable.Debug(Current);
            }
        }

        foreach (var contributor in contributors)
        {
            if (contributor is IDebugGeometrySource source &&
                State.IsPathVisible(source.DebugName))
            {
                using (Current.Scope(source.DebugName))
                {
                    source.EmitGeometry(Current);
                }
            }
        }

        // Selection sweep — runs after geometry so any inspectable that
        // wants to read this frame's just-emitted Stats / state has them
        // available. Skipped entirely when no selection is active.
        //
        // Selection highlight intentionally BYPASSES the normal layer
        // filter — it's system feedback ("here's what you picked"), not
        // user-content that the user might have accidentally toggled off
        // by clicking the wrong layer. The visibility is gated solely
        // on whether a selection exists.
        if (SelectedPath is not null)
        {
            using (Current.Scope(SelectionScope))
            {
                if (SelectedBounds is { } bounds)
                {
                    // Three layered visual cues so the user sees the
                    // selection regardless of camera angle, distance, or
                    // overlap with structural geometry:
                    //
                    //  1. Bounds AABB — gives shape + extent.
                    //  2. Sphere at the bounds center — survives even when
                    //     the camera is inside the AABB or the AABB
                    //     extends mostly off-screen (the floor case in
                    //     Sponza). Sized to a quarter of the longest
                    //     dimension so it's clearly inside the box.
                    //  3. Cross — gives a "this is the focal point"
                    //     readout regardless of orientation.
                    var size = bounds.Max - bounds.Min;
                    var center = (bounds.Min + bounds.Max) * 0.5f;
                    var longest = MathF.Max(MathF.Max(size.X, size.Y), size.Z);
                    // Sphere radius scales with the bounds but caps so a
                    // huge selection doesn't produce a giant sphere that
                    // dwarfs the camera. Floor: 0.5m. Chair: scales to
                    // the chair.
                    var sphereR = MathF.Min(MathF.Max(longest * 0.15f, 0.05f), 0.75f);
                    var crossSize = longest * 0.5f;

                    Current.Draw.Aabb(SelectedPath, bounds.Min, bounds.Max, SelectionHighlightColor);
                    Current.Draw.Sphere(SelectedPath + "/center", center, sphereR, SelectionHighlightColor, segments: 16);
                    Current.Draw.Cross(SelectedPath + "/marker", center, crossSize, SelectionHighlightColor);
                }

                foreach (var contributor in contributors)
                {
                    if (contributor is IDebugInspectable inspectable)
                    {
                        inspectable.Inspect(SelectedPath, Current);
                    }
                }
            }
        }
    }

    // Seals Current into an immutable DebugFrame, pushes it onto History,
    // and clears Current. Idempotent if no frame is in flight — callers
    // (the runtime) can invoke unconditionally on shutdown / abort paths
    // without tripping the BeginFrame contract.
    public void EndFrame()
    {
        if (Current is null)
        {
            return;
        }

        // Record the frame-level CPU timer before snapshot so it lands
        // inside the frozen entry list. The measurement spans BeginFrame
        // -> here, which includes diagnostic-overlay rendering. That's
        // intentional: a "frame ms" stat should reflect total wall-time
        // per tick, including the debug HUD's own cost.
        var frameTicks = Stopwatch.GetTimestamp() - frameStartTicks;
        var frameMs = frameTicks * (1000.0 / Stopwatch.Frequency);
        Current.Timers.AppendCompleted(FrameTimerName, scope: string.Empty, frameMs);

        var snapshot = Current.Snapshot(clock.Elapsed.TotalMilliseconds, SelectedPath);
        History.Push(snapshot);

        // Sinks see the snapshot already in History; a sink can therefore
        // safely read History during Consume (e.g. compute a moving avg).
        // Sink exceptions are caught and recorded as events on the NEXT
        // frame's context so one misbehaving sink can't tear down the
        // render loop. We don't have a context to record to yet, so for
        // Phase 4 we fall back to stderr — a sink that crashes deserves
        // a stderr line at minimum.
        for (var i = 0; i < sinks.Count; i++)
        {
            try
            {
                sinks[i].Consume(snapshot);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[diagnostics] sink {sinks[i].GetType().Name} threw: {ex}");
            }
        }

        Current = null;
    }

    public void Freeze()
    {
        // Prefer the most recent finished frame; that's what's been
        // committed to history and is therefore complete.
        FrozenFrame = History.Latest
            ?? throw new InvalidOperationException("No frames have been recorded yet; call EndFrame at least once before Freeze.");
    }

    public void Freeze(int frameNumber)
    {
        FrozenFrame = History.GetByFrameNumber(frameNumber)
            ?? throw new ArgumentOutOfRangeException(
                nameof(frameNumber), frameNumber,
                $"Frame {frameNumber} is not in the history ring (capacity {History.Capacity}, count {History.Count}).");
    }

    public void Unfreeze()
    {
        FrozenFrame = null;
    }

    public void SetControlValue(string path, object value)
    {
        pendingControlValues[path] = value;
    }
}
