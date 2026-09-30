namespace Blix.Diagnostics;

public sealed class DebugState
{
    /// <summary>
    /// The engine's one view table, interning names into ids that stay stable for the process.
    /// </summary>
    /// <remarks>
    /// It lives on the state rather than the per-frame context because ids must outlive the declarations
    /// that carry them: a trail is "this body, in THAT view, over the last N frames", which cannot be said
    /// if the view's identity is rebuilt every frame. One table so there is one id space — two would drift
    /// and "view 3" would mean two things.
    /// </remarks>
    public Blix.Core.ViewTable Views { get; } = new();

    /// <summary>
    /// Where things have been. The one part of diagnostics that remembers anything across frames.
    /// </summary>
    /// <remarks>
    /// On the state for the same reason the view table is: channels clear every BeginFrame, and a memory
    /// that cleared with them would not be one. See <see cref="DebugTrails"/>.
    /// </remarks>
    public DebugTrails Trails { get; } = new();

    /// <summary>Whether the diagnostics panels are on screen. The host owns the key that flips it.</summary>
    /// <remarks>
    /// <b>One flag, where there were two.</b> An <c>Enabled</c> sat beside this one and the overlay drew
    /// only when both were true, while the backtick key flipped only this one. So a run without
    /// <c>--debug</c> had a key that did nothing, and four applications wrote <c>Enabled</c> from their
    /// own field every frame (one with its own Cmd+C) to get an overlay at all. Where it starts is the
    /// application's to say, once, through its host options (<c>Diagnostics</c>, which <c>--debug</c>
    /// also sets); after that it is the viewer's.
    /// <para>
    /// It does not gate the producers: <c>Debug()</c> runs either way, so a dump taken with the panels
    /// hidden still says what the frame was. A producer that would rather not build controls nobody can
    /// see reads it.
    /// </para>
    /// </remarks>
    public bool ShowOverlay { get; set; }

    /// <summary>Whether a click picks, latched from the Pick switch in the status bar. Holding Alt arms it too.</summary>
    /// <remarks>
    /// <b>The debugger takes a click only when asked.</b> A left click is gameplay input in most games,
    /// so picking on every click would put a tower down and select it at once. Armed, and with the
    /// overlay up, the host takes the click whole — the game sees neither the press nor its release —
    /// and asks what is under it (<see cref="DebugSystem.RequestPick"/>). Otherwise it is the game's.
    /// </remarks>
    public bool PickMode { get; set; }

    /// <summary>Whether debug geometry is drawn at all: the master switch above the layers.</summary>
    public bool ShowDebugDraw { get; set; } = true;

    /// <summary>
    /// Whether debug geometry is hidden by the scene in front of it.
    /// </summary>
    /// <remarks>
    /// On by default, because a gizmo drawn through the model it describes reads as floating in front of
    /// the world rather than sitting in it — reported from the chair as disorienting. Turn it off to see a
    /// primitive that is currently inside something, which is the case where drawing on top is the whole
    /// point.
    /// <para>
    /// It only means anything where the target has depth worth testing against. A pass that blits a
    /// finished picture onto the swapchain leaves that depth buffer empty, so debug geometry drawn there
    /// tests against nothing — see the lab's present pass, which carries scene depth across for exactly
    /// this reason.
    /// </para>
    /// </remarks>
    public bool DepthTestDrawing { get; set; } = true;

    // Path-prefix layer toggles for debug draws. Missing key = on
    // (default visible); explicit false = hidden. ShouldDraw walks
    // a command's path leaf -> root and short-circuits on the first
    // explicit `false`, so disabling "physics" hides every nested
    // sub-path under it without listing them individually. A key that is
    // present is also a layer the Layers tab lists, drawn this frame or not:
    // that is what lets a hidden layer be turned back on, and a layer
    // declared hidden (DebugDrawChannel.Layer) be found in the first place.
    //
    // Stored as a plain dictionary rather than a typed channel because
    // it's UI-driven state (toggled in ImGui) that needs to survive
    // multiple frames — same lifetime story as ShowOverlay above.
    public Dictionary<string, bool> LayersEnabled { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether a recorded primitive reaches the screen.</summary>
    /// <remarks>
    /// <b>The Layers tab was a panel of switches connected to nothing.</b> It listed every path drawn
    /// and wrote <see cref="LayersEnabled"/>, and the line pass never asked, so unticking a layer
    /// changed nothing on screen; the only thing it gated was <c>IDebugGeometrySource</c>, which no
    /// producer implements. Every application that wanted a gizmo it could hide grew its own toggle
    /// instead. This is the question the line pass asks now, per command.
    /// <para>
    /// The selection highlight (<see cref="DebugDrawCommand.Feedback"/>) answers to the master switch
    /// only. It is the system's reply to a click, and a layer unticked by accident should not make a
    /// pick look like it missed.
    /// </para>
    /// </remarks>
    public bool ShouldDraw(DebugDrawCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!ShowDebugDraw) return false;
        return command.Feedback || IsPathVisible(command.Path);
    }

    // Returns true if the command at `path` should be rendered. Walks
    // the path's prefix ladder (e.g. "a/b/c" -> "a/b" -> "a") and
    // returns false on the first explicit `false`. An empty / null
    // path is treated as always visible.
    public bool IsPathVisible(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return true;
        }
        if (LayersEnabled.Count == 0)
        {
            return true;
        }
        // Check the full path first, then progressively shorter prefixes.
        var current = path;
        while (true)
        {
            if (LayersEnabled.TryGetValue(current, out var enabled) && !enabled)
            {
                return false;
            }
            var slash = current.LastIndexOf('/');
            if (slash < 0)
            {
                return true;
            }
            current = current.Substring(0, slash);
        }
    }
}
