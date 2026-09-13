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

    public bool Enabled { get; set; }

    public bool ShowOverlay { get; set; } = true;

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
    // (default visible); explicit false = hidden. The renderer walks
    // a command's path leaf -> root and short-circuits on the first
    // explicit `false`, so disabling "physics" hides every nested
    // sub-path under it without listing them individually.
    //
    // Stored as a plain dictionary rather than a typed channel because
    // it's UI-driven state (toggled in ImGui) that needs to survive
    // multiple frames — same lifetime story as ShowOverlay above.
    public Dictionary<string, bool> LayersEnabled { get; } = new(StringComparer.Ordinal);

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
