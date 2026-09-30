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
// Not thread-safe, and not meant to be: it is single-threaded: the host calls it from its frame loop, on the thread that
// runs the loop, and nothing else calls it.
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

    // <b>The selection scope is the system's.</b> Contributors run inside a scope of their own name, so the
    // only way into the root "selection" scope, whose values and controls the Selection tab shows, would
    // be a contributor named for it. Refused by name rather than discovered as a panel that shows the
    // wrong thing.
    private static void RefuseReservedName(string name)
    {
        if (name == SelectionScope)
        {
            throw new ArgumentException(
                $"'{SelectionScope}' is the debug system's own scope (the Selection tab); give the contributor another name.");
        }
    }

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

    /// <summary>How long a trail survives after its producer stops asking for it.</summary>
    private const float TrailStaleSeconds = 30f;

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

    // Most recent resource inventory (textures, buffers, pipelines, shader
    // programs, render surfaces). Set by the runtime each frame; read by the
    // overlay's Resources tab. Like the packet, a point-in-time copy safe to
    // read off the live tables.
    public ResourceRegistrySnapshot? LatestResourceSnapshot { get; private set; }

    public void SetResourceSnapshot(ResourceRegistrySnapshot snapshot) => LatestResourceSnapshot = snapshot;

    // When non-null, sinks should render this frame read-only instead of
    // Current. Held independently of the ring so it survives overwrite.
    public DebugFrame? FrozenFrame { get; private set; }

    public bool IsFrozen => FrozenFrame is not null;

    /// <summary>Starts a frame.</summary>
    /// <param name="frame">The framebuffer, in physical pixels.</param>
    /// <param name="logicalSize">
    /// The window in the pointer's coordinates, which only the host knows. Without it a view declared by
    /// the shorthand <see cref="DebugDrawChannel.Declare(string, System.Numerics.Matrix4x4)"/> takes the
    /// framebuffer for both rectangles, and a click through it on a Retina display lands at twice the
    /// distance from the corner that it should.
    /// </param>
    /// <param name="input">This tick's input, which drives controls bound to a key. Ignored while frozen.</param>
    public DebugContext BeginFrame(
        RenderFrameContext frame, (int Width, int Height)? logicalSize = null, IInputState? input = null)
    {
        frameCounter++;
        frameStartTicks = Stopwatch.GetTimestamp();
        Current = new DebugContext(
            State, frame, logicalSize ?? (frame.Width, frame.Height), pendingControlValues, frameCounter, clock,
            SelectedPath, IsFrozen ? null : input, hostKeys, reportedKeyConflicts);
        return Current;
    }

    // === Keys ===============================================================

    private readonly List<DebugKeyEntry> hostKeys = new();
    private readonly HashSet<string> reportedKeyConflicts = new(StringComparer.Ordinal);

    /// <summary>The host's own keys, which lead the key list and which nothing else may bind.</summary>
    public IReadOnlyList<DebugKeyEntry> HostKeys => hostKeys;

    /// <summary>Called by the host, once, for each key or gesture it answers to itself.</summary>
    public void DeclareHostKey(string binding, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binding);
        hostKeys.Add(new DebugKeyEntry(binding, what, $"host/{binding}", DebugKeySource.Host));
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
    //   - if it implements a UI-layer interface (IDebugUi in the overlay
    //     layer), the runtime discovers it via Contributors.
    //
    // Re-registering the same instance is a no-op rather than a duplicate;
    // we'd otherwise produce duplicate scopes on the same frame.
    public void Register(IDebugContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        RefuseReservedName(contributor.DebugName);
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

    // === Frame timing =======================================================

    /// <summary>How the host's frames went: the device's own record, which the Perf tab reads.</summary>
    /// <remarks>
    /// <b>The Perf tab reads the record, not a copy of it:</b> the wait, encode and submit split of each
    /// frame, what reached the device, and per-pass GPU totals that a window can be taken from
    /// (<see cref="GpuPasses"/>). Display only: nothing here feeds a dump, which is the measurement
    /// design's question.
    /// </remarks>
    public IFrameTiming Timing { get; private set; } = FrameTimings.None;

    /// <summary>Each pass's GPU time over the last second or so, sampled at every EndFrame.</summary>
    public GpuPassWindow GpuPasses { get; private set; } = new(FrameTimings.None);

    /// <summary>Called by the host once it has a device.</summary>
    public void UseFrameTiming(IFrameTiming timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        Timing = timing;
        GpuPasses = new GpuPassWindow(timing);
    }

    // === Selection ==========================================================

    /// <summary>What is selected, which the inspector shows. Null if nothing.</summary>
    /// <remarks>
    /// One thing at a time: the debugger's default (conventions §10), revisited when a consumer needs a
    /// set. Persists across frames and survives Freeze.
    /// </remarks>
    public string? SelectedPath { get; private set; }

    /// <summary>The selection's bounds as its source reports them now, or null.</summary>
    /// <remarks>
    /// Asked of the source each time, not remembered from the click, so the highlight stays on a thing
    /// that moves.
    /// </remarks>
    public Bounds3? SelectedBounds => SelectedPath is { } path && TryGetBounds(path, out var b) ? b : null;

    public void Select(string entityPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(entityPath);
        if (entityPath != SelectedPath) ForgetSelectionEdits();
        SelectedPath = entityPath;
    }

    public void ClearSelection()
    {
        if (SelectedPath is not null) ForgetSelectionEdits();
        SelectedPath = null;
    }

    // <b>An edit on the Selection tab belongs to the thing that was selected.</b> Its control has one path
    // whatever is selected ("selection/LOD/margin"), and a panel edit is held until something overwrites
    // it, so a change of selection forgets these edits rather than hand them to the next thing selected.
    private void ForgetSelectionEdits()
    {
        foreach (var path in pendingControlValues.Keys.ToArray())
        {
            if (path == SelectionScope || path.StartsWith(SelectionScope + "/", StringComparison.Ordinal))
            {
                pendingControlValues.Remove(path);
            }
        }
    }

    /// <summary>The current bounds of any selectable entity, asked of whichever source owns it.</summary>
    public bool TryGetBounds(string entityPath, out Bounds3 bounds)
    {
        for (var i = 0; i < contributors.Count; i++)
        {
            if (contributors[i] is IDebugSelectable selectable && selectable.TryGetBounds(entityPath, out bounds))
            {
                return true;
            }
        }

        bounds = default;
        return false;
    }

    // Walks every registered IDebugSelectable and collects their
    // pickable entries into the shared scratch list. The list is
    // returned by ref-friendly IReadOnlyList; calling again clobbers
    // the previous result, so callers must consume immediately.
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

    // === Picking ============================================================
    //
    // A click in pick mode asks what is under the cursor, and the host answers by drawing every
    // selectable's geometry into one pixel (DebugPickGeometry): the answer is exact, and nothing here
    // looks at a box. The host takes the request after the frame's views are declared, draws, reads
    // the pixel back, and completes it.

    /// <summary>A pick waiting for the host: where the click was, and what to leave out.</summary>
    /// <param name="Pointer">In the window's logical coordinates.</param>
    /// <param name="Excluded">Earlier hits at this spot. Leaving them out is how clicking again steps behind them.</param>
    public sealed record PickRequest(System.Numerics.Vector2 Pointer, IReadOnlyCollection<string> Excluded);

    // A click this close to the last one, in logical pixels, is "the same spot": it steps behind the hit.
    private const float SameSpotPixels = 3f;
    private PickRequest? pendingPick;
    private System.Numerics.Vector2? lastPickPointer;
    private readonly HashSet<string> excludedHere = new(StringComparer.Ordinal);

    /// <summary>The last completed pick, whole: view, pixel, how much was drawn, and the hit. In dumps.</summary>
    public DebugPick? LastPick { get; private set; }

    /// <summary>Asks what is under <paramref name="pointer"/>; the host answers during the next frame.</summary>
    /// <remarks>
    /// Clicking again at the same spot while its hit is still selected leaves that hit out, so a second
    /// click on the tree reaches what is behind it. A miss, or a click anywhere else, starts over.
    /// </remarks>
    public void RequestPick(System.Numerics.Vector2 pointer)
    {
        var again = SelectedPath is { } hit && lastPickPointer is { } last
                    && System.Numerics.Vector2.Distance(last, pointer) <= SameSpotPixels;
        if (again) excludedHere.Add(SelectedPath!);
        else excludedHere.Clear();
        lastPickPointer = pointer;
        pendingPick = new PickRequest(pointer, excludedHere.ToArray());
    }

    /// <summary>The waiting pick, if any, which the caller now owns.</summary>
    public bool TryTakePickRequest([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PickRequest? request)
    {
        request = pendingPick;
        pendingPick = null;
        return request is not null;
    }

    /// <summary>The host's answer to a pick: selects the hit, or clears on a miss.</summary>
    public void CompletePick(DebugPick pick)
    {
        ArgumentNullException.ThrowIfNull(pick);
        LastPick = pick;
        if (pick.Hit is { } hit)
        {
            Select(hit);
            return;
        }

        excludedHere.Clear();
        ClearSelection();
    }

    public void Run(params IDebuggable[] debuggables)
    {
        foreach (var passed in debuggables) RefuseReservedName(passed.DebugName);
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
                // Asked of the source every frame rather than copied at the click, so the box stays on a
                // thing that moves.
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

                    // <b>Into every view — and this is a local default, not an engine law.</b>
                    // The selection highlight answers "here is what you picked", and the answer is the
                    // same whichever window you look through — so a second viewport that can see the
                    // object should show it selected too. Views are already declared by here: the
                    // selection sweep runs after every contributor, which is where an application
                    // declares the views it drew into.
                    //
                    // No views declared means the application drew nothing this frame, so there is no
                    // picture to annotate. That is not an error.
                    //
                    // Worth being explicit about the limit, because this is the one place in the view work
                    // where policy could walk back in unnoticed: "is this view an audience for selection
                    // overlays?" is NOT intrinsic to being a view. A view can be an inspector, a game
                    // camera, a shadow cascade, a capture target — and a cascade has no business showing a
                    // selection outline. Painting all of them is the right default for the one selection
                    // mechanism that exists today and nothing more. Do not generalise this into "system
                    // feedback goes into every view"; when a second consumer disagrees, the answer is for
                    // the SELECTION to learn which views it addresses, not for a view to grow a kind.
                    var declared = Current.Draw.Views.ToArray();
                    foreach (var view in declared)
                    {
                        using (Current.Draw.In(view))
                        {
                            Current.Draw.EmittingFeedback = true;
                            Current.Draw.Aabb(SelectedPath, bounds.Min, bounds.Max, SelectionHighlightColor);
                            Current.Draw.Sphere(SelectedPath + "/center", center, sphereR, SelectionHighlightColor, segments: 16);
                            Current.Draw.Cross(SelectedPath + "/marker", center, crossSize, SelectionHighlightColor);
                            Current.Draw.EmittingFeedback = false;
                        }
                    }
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

        GpuPasses.Sample();

        // Record the frame-level CPU timer before snapshot so it lands
        // inside the frozen entry list. The measurement spans BeginFrame
        // -> here, which includes diagnostic-overlay rendering. That's
        // intentional: a "frame ms" stat should reflect total wall-time
        // per tick, including the debug HUD's own cost.
        var frameTicks = Stopwatch.GetTimestamp() - frameStartTicks;
        var frameMs = frameTicks * (1000.0 / Stopwatch.Frequency);
        Current.Timers.AppendCompleted(FrameTimerName, scope: string.Empty, frameMs);

        // Trails whose producer has gone quiet are dropped, or every entity that ever had one would keep
        // its dictionary entry forever — the points age to empty on their own, the key does not. A leak
        // guard, deliberately far longer than any sensible trail duration, not a visual parameter.
        State.Trails.Expire(clock.Elapsed.TotalMilliseconds, TrailStaleSeconds);

        var snapshot = Current.Snapshot(clock.Elapsed.TotalMilliseconds, SelectedPath, LastPick);
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
