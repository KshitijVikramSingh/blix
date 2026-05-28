using Blix.Diagnostics;
using Blix.Graphics;
using ImGuiNET;
using System.Numerics;

namespace Blix.Diagnostics.Overlay;

// Backend-agnostic ImGui panel builder for the diagnostics overlay. Reads the
// DebugSystem channels (Values/Controls/Stats/Timers/Events + IDebugUi custom
// panels) and emits ImGui draw calls. Knows nothing about GL or Vulkan — each
// runtime backend sets up ImGui IO + a render context, calls ImGui.NewFrame(),
// invokes Layout(), then ImGui.Render() and submits the draw data its own way.
//
// Extracted from the original OpenTK-only renderer so both the GL and Vulkan
// backends share one source of truth for the panel layout.
public sealed class DebugOverlayUi
{
    // Per-row sparkline opt-in. UI state, not diagnostics-system state —
    // lives only for the renderer's lifetime (one process run). Adding a
    // toggle per stat path keeps the Stats tab compact by default: rows
    // are text-only, user clicks the trailing icon to expand a graph.
    private readonly HashSet<string> sparklinesEnabled = new(StringComparer.Ordinal);

    // Track the path we showed the Selection tab for last frame so we can
    // auto-focus the tab when a new pick happens. Without this, picking
    // doesn't pull the user's attention to the new info.
    private string? lastRenderedSelection;

    // Builds the diagnostics window for the current frame. Call between
    // ImGui.NewFrame() and ImGui.Render().
    public void Layout(DebugSystem debugSystem)
    {
        // Source selection. Controls + Values come from live Current so
        // sliders feel responsive; Stats / Timers / Events come from the
        // most recent finished frame because the frame-level CPU timer is
        // appended inside EndFrame, after this render call. Frozen mode
        // wins everything.
        IReadOnlyList<DebugValueEntry> values;
        IReadOnlyList<DebugControlEntry> controls;
        IReadOnlyList<DebugStatEntry> stats;
        IReadOnlyList<DebugTimerEntry> timers;
        IReadOnlyList<DebugEventEntry> events;
        int? displayedFrameNumber;
        bool interactive;
        if (debugSystem.FrozenFrame is { } frozen)
        {
            values = frozen.Values;
            controls = frozen.Controls;
            stats = frozen.Stats;
            timers = frozen.Timers;
            events = frozen.Events;
            displayedFrameNumber = frozen.Number;
            interactive = false;
        }
        else if (debugSystem.Current is { } live)
        {
            values = live.ValueEntries;
            controls = live.ControlEntries;
            stats = debugSystem.LatestFrame?.Stats ?? Array.Empty<DebugStatEntry>();
            timers = debugSystem.LatestFrame?.Timers ?? Array.Empty<DebugTimerEntry>();
            events = debugSystem.LatestFrame?.Events ?? Array.Empty<DebugEventEntry>();
            displayedFrameNumber = live.FrameNumber;
            interactive = true;
        }
        else
        {
            return;
        }

        // Window setup: drop AlwaysAutoResize so the user can drag-resize;
        // allow ImGui's normal saved-settings so position + size + open
        // tab persist across runs via imgui.ini.
        ImGui.SetNextWindowPos(new Vector2(16.0f, 16.0f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(440.0f, 520.0f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.85f);
        ImGui.Begin("Diagnostics", ImGuiWindowFlags.None);

        DrawStatusBar(debugSystem, displayedFrameNumber, interactive, stats, timers);
        ImGui.Separator();

        if (ImGui.BeginTabBar("DiagnosticsTabs", ImGuiTabBarFlags.Reorderable))
        {
            // Selection tab — only visible when something is selected;
            // auto-focuses on a fresh pick so the user's attention goes
            // to the new info without a manual tab click. The flag-
            // taking BeginTabItem requires a ref-bool "open" param; the
            // close X it shows is harmless — we re-pass true every
            // frame so a click reopens the tab on the next render.
            if (debugSystem.SelectedPath is { } selPath)
            {
                var newPick = debugSystem.SelectedPath != lastRenderedSelection;
                var tabFlags = newPick ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                var openDummy = true;
                if (ImGui.BeginTabItem("Selection", ref openDummy, tabFlags))
                {
                    DrawSelectionTab(debugSystem, selPath, values);
                    ImGui.EndTabItem();
                }
            }
            lastRenderedSelection = debugSystem.SelectedPath;

            if (ImGui.BeginTabItem("Perf"))
            {
                DrawPerfReport(stats, timers);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Pipeline"))
            {
                DrawPipelineTab(debugSystem);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Stats"))
            {
                DrawStatsTab(debugSystem.History, stats);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Timers"))
            {
                DrawTimersTab(debugSystem.History, timers);
                ImGui.EndTabItem();
            }

            if (events.Count > 0 && ImGui.BeginTabItem("Events"))
            {
                DrawEvents(events);
                ImGui.EndTabItem();
            }

            if (controls.Count > 0 && ImGui.BeginTabItem("Controls"))
            {
                DrawControls(debugSystem, controls, interactive);
                ImGui.EndTabItem();
            }

            if (HasNonSelectionValues(values, debugSystem.SelectedPath is not null) &&
                ImGui.BeginTabItem("State"))
            {
                DrawValues(NonSelectionValues(values, debugSystem.SelectedPath is not null));
                ImGui.EndTabItem();
            }

            // Layers tab — reads paths from the live context so a new
            // producer's checkboxes appear immediately, not one frame later.
            var liveDraws = debugSystem.Current?.Draw.Commands;
            if (liveDraws is { Count: > 0 } && ImGui.BeginTabItem("Layers"))
            {
                DrawLayersTree(debugSystem.State, liveDraws);
                ImGui.EndTabItem();
            }

            if (HasCustomUi(debugSystem) && ImGui.BeginTabItem("Custom"))
            {
                DrawCustomUis(debugSystem);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    // Single-line status bar at the top of the window. Always visible.
    // The numbers that get scanned every frame (frame N, FPS, ms, draws,
    // tris) live here so the user doesn't have to expand anything to see
    // them. Freeze toggle on the right edge.
    private static void DrawStatusBar(
        DebugSystem debugSystem,
        int? frameNumber,
        bool interactive,
        IReadOnlyList<DebugStatEntry> stats,
        IReadOnlyList<DebugTimerEntry> timers)
    {
        var frameMs = LookupTimerMs(timers, DebugSystem.FrameTimerName);
        var draws = (long)LookupStatValue(stats, "draws");
        var tris  = (long)LookupStatValue(stats, "triangles");
        var fps   = frameMs > 0.0 ? 1000.0 / frameMs : 0.0;

        if (interactive)
        {
            ImGui.TextUnformatted($"frame {frameNumber}");
        }
        else
        {
            ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.2f, 1.0f),
                $"frame {frameNumber} (FROZEN)");
        }
        ImGui.SameLine(); ImGui.TextDisabled("|"); ImGui.SameLine();
        ImGui.Text($"{fps:0} fps");
        ImGui.SameLine(); ImGui.TextDisabled("|"); ImGui.SameLine();
        ImGui.Text($"{frameMs:0.00} ms");
        ImGui.SameLine(); ImGui.TextDisabled("|"); ImGui.SameLine();
        ImGui.Text($"{draws:N0} draws");
        if (tris > 0)
        {
            ImGui.SameLine(); ImGui.TextDisabled("|"); ImGui.SameLine();
            ImGui.Text($"{tris:N0} tris");
        }
        if (debugSystem.SelectedPath is { } selPath)
        {
            ImGui.SameLine(); ImGui.TextDisabled("|"); ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.0f, 1.0f),
                $"sel: {Shorten(selPath, 32)}");
        }

        // Freeze / unfreeze button on the right. Manual right-align so
        // the button always sits at the edge regardless of the dynamic
        // text length to its left.
        var btnText = interactive ? "Freeze" : "Unfreeze";
        var btnWidth = ImGui.CalcTextSize(btnText).X + ImGui.GetStyle().FramePadding.X * 2.0f;
        RightAlignNextWidget(btnWidth);
        if (ImGui.SmallButton(btnText))
        {
            if (interactive)
            {
                if (debugSystem.LatestFrame is not null)
                {
                    debugSystem.Freeze();
                }
            }
            else
            {
                debugSystem.Unfreeze();
            }
        }
    }

    // Right-align the next widget within the current line to the right
    // edge of the window's content region. Replaces the removed
    // ImGui.GetWindowContentRegionMax() with the current API (cursor
    // position + remaining available width).
    private static void RightAlignNextWidget(float widgetWidth)
    {
        ImGui.SameLine();
        var x = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - widgetWidth;
        if (x < ImGui.GetCursorPosX()) x = ImGui.GetCursorPosX(); // no overlap on narrow windows
        ImGui.SetCursorPosX(x);
    }

    private static double LookupStatValue(IReadOnlyList<DebugStatEntry> stats, string path)
    {
        for (var i = 0; i < stats.Count; i++)
        {
            if (stats[i].Path == path) return stats[i].Value;
        }
        return 0.0;
    }

    private static double LookupTimerMs(IReadOnlyList<DebugTimerEntry> timers, string path)
    {
        for (var i = 0; i < timers.Count; i++)
        {
            if (timers[i].Path == path) return timers[i].TotalMs;
        }
        return 0.0;
    }

    private static string Shorten(string s, int max)
    {
        if (s.Length <= max) return s;
        return "…" + s[^(max - 1)..];
    }

    private void DrawSelectionTab(DebugSystem debugSystem, string selPath, IReadOnlyList<DebugValueEntry> values)
    {
        ImGui.TextUnformatted(selPath);
        var clearWidth = ImGui.CalcTextSize("Clear").X + ImGui.GetStyle().FramePadding.X * 2.0f;
        RightAlignNextWidget(clearWidth);
        if (ImGui.SmallButton("Clear"))
        {
            debugSystem.ClearSelection();
        }
        ImGui.Separator();

        var hits = SelectionValues(values);
        if (hits.Count == 0)
        {
            ImGui.TextDisabled("(no inspector data for this entity yet)");
            return;
        }
        DrawValues(hits);
    }

    // True if any registered contributor wants a custom panel. Cheap
    // walk; used only to gate showing the "Custom" tab so demos without
    // any IDebugUi producers don't see an empty tab.
    private static bool HasCustomUi(DebugSystem debugSystem)
    {
        var contributors = debugSystem.Contributors;
        for (var i = 0; i < contributors.Count; i++)
        {
            if (contributors[i] is IDebugUi) return true;
        }
        return false;
    }

    private static bool HasNonSelectionValues(IReadOnlyList<DebugValueEntry> values, bool hasSelection)
    {
        if (!hasSelection) return values.Count > 0;
        for (var i = 0; i < values.Count; i++)
        {
            if (!IsSelectionScope(values[i].Scope)) return true;
        }
        return false;
    }

    private static void DrawCustomUis(DebugSystem debugSystem)
    {
        // Each registered contributor that opts in via IDebugUi gets its
        // own CollapsingHeader keyed on DebugName. Wrapping each call in
        // PushID + try/catch isolates a buggy panel from tearing down
        // the rest of the diagnostics overlay — a producer bug should
        // surface as a single missing panel, not a black-screened HUD.
        var contributors = debugSystem.Contributors;
        for (var i = 0; i < contributors.Count; i++)
        {
            if (contributors[i] is not IDebugUi ui)
            {
                continue;
            }

            var name = ui.DebugName;
            if (!ImGui.CollapsingHeader(name, ImGuiTreeNodeFlags.DefaultOpen))
            {
                continue;
            }

            ImGui.PushID(name);
            try
            {
                ui.OnImGui();
            }
            catch (Exception ex)
            {
                // The error stays visible in the panel so a producer
                // author sees it without trawling stderr.
                ImGui.TextColored(ErrorColor, $"panel threw: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                ImGui.PopID();
            }
        }
    }

    private static readonly Vector2 SparklineSize = new(120.0f, 24.0f);

    private static readonly Vector4 InfoColor  = new(0.65f, 0.85f, 1.00f, 1.0f);
    private static readonly Vector4 WarnColor  = new(1.00f, 0.78f, 0.30f, 1.0f);
    private static readonly Vector4 ErrorColor = new(1.00f, 0.45f, 0.45f, 1.0f);

    private static void DrawEvents(IReadOnlyList<DebugEventEntry> entries)
    {
        // Chronological top-to-bottom, severity-colored. Path shown when
        // non-empty so events fired from a scoped producer ("uploader",
        // "Sponza/Render/...") are visually attributable.
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var color = entry.Severity switch
            {
                DebugEventSeverity.Error => ErrorColor,
                DebugEventSeverity.Warn => WarnColor,
                _ => InfoColor
            };
            var label = string.IsNullOrEmpty(entry.Path)
                ? entry.Message
                : $"{entry.Path}: {entry.Message}";
            ImGui.TextColored(color, $"[{FormatSeverityShort(entry.Severity)}] {label}");
        }
    }

    // The path prefix the selection sweep emits under — kept in sync
    // with DebugSystem.SelectionScope. Duplicated as a literal here so
    // this UI code doesn't reach into the diagnostics core for one
    // string; if either side changes, both update together via search.
    private const string SelectionScopeRoot = "selection";

    private static IReadOnlyList<DebugValueEntry> SelectionValues(IReadOnlyList<DebugValueEntry> all)
    {
        var hits = new List<DebugValueEntry>();
        for (var i = 0; i < all.Count; i++)
        {
            if (IsSelectionScope(all[i].Scope))
            {
                hits.Add(all[i]);
            }
        }
        return hits;
    }

    private static IReadOnlyList<DebugValueEntry> NonSelectionValues(IReadOnlyList<DebugValueEntry> all, bool hasSelection)
    {
        if (!hasSelection)
        {
            return all;
        }
        var hits = new List<DebugValueEntry>();
        for (var i = 0; i < all.Count; i++)
        {
            if (!IsSelectionScope(all[i].Scope))
            {
                hits.Add(all[i]);
            }
        }
        return hits;
    }

    private static bool IsSelectionScope(string scope)
    {
        return scope == SelectionScopeRoot ||
               scope.StartsWith(SelectionScopeRoot + "/", StringComparison.Ordinal);
    }

    // Tree-style Layers panel built from the unique prefixes observed
    // in this frame's draw commands. Children collapse by default; each
    // node shows a (visible / total) leaf count and All / None buttons
    // that flip every descendant prefix at once. This is what makes the
    // panel tractable for Sponza, where the flat list grows to 400+ rows.
    private static void DrawLayersTree(DebugState state, IReadOnlyList<DebugDrawCommand> commands)
    {
        // Build a child-map from full paths. Each node tracks its
        // children (next segment) and its leaf count (the number of
        // distinct full paths under it). Leaf count drives the "(N/M)"
        // visibility readout next to each node.
        var root = new LayerNode("(root)", "");
        for (var i = 0; i < commands.Count; i++)
        {
            var path = commands[i].Path;
            if (string.IsNullOrEmpty(path)) continue;
            root.Add(path);
        }
        if (root.Children.Count == 0)
        {
            ImGui.TextDisabled("(no debug-draw paths emitted this frame)");
            return;
        }

        foreach (var child in root.ChildrenSorted())
        {
            DrawLayerNode(state, child);
        }
    }

    private static void DrawLayerNode(DebugState state, LayerNode node)
    {
        var visible = CountVisibleLeaves(state, node);
        var total = node.LeafCount;
        // The toggle that controls THIS node's prefix. Reads default-on
        // when not in the dict.
        if (!state.LayersEnabled.TryGetValue(node.FullPath, out var enabled))
        {
            enabled = true;
        }

        ImGui.PushID(node.FullPath);
        var changed = ImGui.Checkbox("##en", ref enabled);
        if (changed)
        {
            state.LayersEnabled[node.FullPath] = enabled;
        }
        ImGui.SameLine();

        // Leaves render as a single line; intermediate prefixes render
        // as a TreeNode you can expand. Visible / total count goes at
        // the line's right edge.
        var label = node.Children.Count == 0
            ? node.Label
            : $"{node.Label}";
        var counts = total > 0 ? $"({visible}/{total})" : string.Empty;

        if (node.Children.Count == 0)
        {
            ImGui.TextUnformatted(label);
            if (counts.Length > 0)
            {
                RightAlignNextWidget(ImGui.CalcTextSize(counts).X);
                ImGui.TextDisabled(counts);
            }
        }
        else
        {
            var open = ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.SpanAvailWidth);
            // Stamp "(visible/total) [All] [None]" against the right edge.
            // Width budget: "(NNN/NNN)" ~70px + two ~45px buttons.
            RightAlignNextWidget(160.0f);
            ImGui.TextDisabled(counts);
            ImGui.SameLine();
            if (ImGui.SmallButton("All"))
            {
                SetSubtreeEnabled(state, node, true);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("None"))
            {
                SetSubtreeEnabled(state, node, false);
            }
            if (open)
            {
                foreach (var child in node.ChildrenSorted())
                {
                    DrawLayerNode(state, child);
                }
                ImGui.TreePop();
            }
        }
        ImGui.PopID();
    }

    private static int CountVisibleLeaves(DebugState state, LayerNode node)
    {
        if (node.Children.Count == 0)
        {
            return state.IsPathVisible(node.FullPath) ? 1 : 0;
        }
        var sum = 0;
        foreach (var c in node.Children.Values)
        {
            sum += CountVisibleLeaves(state, c);
        }
        return sum;
    }

    private static void SetSubtreeEnabled(DebugState state, LayerNode node, bool enabled)
    {
        // Sets this node's prefix; for "All", clearing every descendant
        // override would be ideal but is more invasive. The walk leaf
        // -> root already short-circuits on the first explicit false,
        // so setting THIS node to true and pruning any false descendant
        // overrides is the right behavior — descendants that were
        // explicitly disabled would otherwise still hide. Same for
        // "None": just set this node false; descendants stay.
        state.LayersEnabled[node.FullPath] = enabled;
        if (enabled)
        {
            // Clear any descendant false-overrides so "All" really does
            // mean show everything under this prefix.
            ClearDescendantFalses(state, node);
        }
    }

    private static void ClearDescendantFalses(DebugState state, LayerNode node)
    {
        foreach (var c in node.Children.Values)
        {
            if (state.LayersEnabled.TryGetValue(c.FullPath, out var v) && !v)
            {
                state.LayersEnabled.Remove(c.FullPath);
            }
            ClearDescendantFalses(state, c);
        }
    }

    private sealed class LayerNode
    {
        public string Label { get; }
        public string FullPath { get; }
        public Dictionary<string, LayerNode> Children { get; } = new(StringComparer.Ordinal);
        public int LeafCount { get; private set; }

        public LayerNode(string label, string fullPath) { Label = label; FullPath = fullPath; }

        public void Add(string path)
        {
            var segments = path.Split('/');
            var current = this;
            for (var i = 0; i < segments.Length; i++)
            {
                var seg = segments[i];
                if (!current.Children.TryGetValue(seg, out var child))
                {
                    var full = i == 0 ? seg : current.FullPath + "/" + seg;
                    child = new LayerNode(seg, full);
                    current.Children[seg] = child;
                }
                current = child;
            }
            current.LeafCount++;
            // Propagate leaf counts up so intermediate node readouts
            // show the right totals.
            var walk = this;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                walk = walk.Children[segments[i]];
                walk.LeafCount++;
            }
        }

        public IEnumerable<LayerNode> ChildrenSorted()
            => Children.Values.OrderBy(c => c.Label, StringComparer.Ordinal);
    }

    private static string FormatSeverityShort(DebugEventSeverity severity) => severity switch
    {
        DebugEventSeverity.Info => "i",
        DebugEventSeverity.Warn => "!",
        DebugEventSeverity.Error => "x",
        _ => "?"
    };

    // Stats / Timers row layout. Each row is a single line of text by
    // default; clicking a small graph icon at the right edge toggles
    // a sparkline plot for that specific path. Keeps the panel tight
    // while letting the user "expand to see history" for whatever they
    // care about right now.
    // The Perf tab rolls existing Stats + Timers entries into three
    // compact tables — Phases, Passes, Packs — so "what's expensive?"
    // has a single canonical surface. No new instrumentation; everything
    // here is a lookup into entries the producers already emit.
    //
    // Auto-discovery: pass and pack names come from observed entry
    // scopes, not a hardcoded list. Generic across demos — a future
    // game with different pack/pass names gets a working Perf tab for
    // free.
    // Shader-pipeline inspector: the actual per-pass / per-draw wiring + data
    // fed to the GPU this frame. Pass -> target, then each draw's shader, the
    // live uniform values, decoded push constants, and the texture binding map.
    // Sourced from the FrameDebugPacket the backend produces each Execute
    // (Vulkan populates it; lags the live frame by one).
    private static void DrawPipelineTab(DebugSystem debugSystem)
    {
        var packet = debugSystem.LatestFramePacket;
        if (packet is null)
        {
            ImGui.TextDisabled("(no GPU frame packet yet — populated by the Vulkan backend)");
            return;
        }

        ImGui.Text($"{packet.TotalPasses} passes, {packet.TotalDraws} draws");
        ImGui.Separator();

        for (var p = 0; p < packet.Passes.Count; p++)
        {
            var pass = packet.Passes[p];
            ImGui.PushID(p);
            var header = $"{pass.Name}  →  {pass.TargetName}   ({pass.Draws.Count} draws)";
            if (ImGui.TreeNodeEx(header, ImGuiTreeNodeFlags.DefaultOpen))
            {
                var clears = $"{(pass.ClearedColor ? "color" : "load")}/{(pass.ClearedDepth ? "depth-clear" : "depth-keep")}";
                ImGui.TextDisabled($"{pass.Width}x{pass.Height}   {clears}");

                for (var d = 0; d < pass.Draws.Count; d++)
                {
                    var draw = pass.Draws[d];
                    ImGui.PushID(d);
                    var shader = string.IsNullOrEmpty(draw.Shader) ? "(shader?)" : draw.Shader;
                    if (ImGui.TreeNodeEx($"#{d}  {shader}   ({draw.IndexCount} idx)"))
                    {
                        DrawDrawInputs(draw);
                        ImGui.TreePop();
                    }
                    ImGui.PopID();
                }
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
    }

    private static void DrawDrawInputs(FrameDebugDraw draw)
    {
        var uniforms = draw.Uniforms ?? Array.Empty<FrameDebugUniform>();
        if (uniforms.Count > 0)
        {
            ImGui.TextColored(InfoColor, "uniforms");
            for (var i = 0; i < uniforms.Count; i++)
            {
                var u = uniforms[i];
                // Multi-line values (matrices) read better as name on one line,
                // value indented below; scalars/vectors stay inline.
                if (u.Value.Contains('\n'))
                {
                    ImGui.TextUnformatted($"  {u.Name}:");
                    ImGui.Indent();
                    ImGui.TextUnformatted(u.Value);
                    ImGui.Unindent();
                }
                else
                {
                    ImGui.TextUnformatted($"  {u.Name} = {u.Value}");
                }
            }
        }

        var push = draw.PushConstants ?? Array.Empty<float>();
        if (push.Count > 0)
        {
            ImGui.TextColored(InfoColor, $"push ({push.Count} floats)");
            // 16 floats almost always a mat4 — show as 4 rows; otherwise 4/row.
            for (var row = 0; row * 4 < push.Count; row++)
            {
                var a = row * 4;
                var sb = "  ";
                for (var c = 0; c < 4 && a + c < push.Count; c++)
                {
                    sb += push[a + c].ToString("0.###").PadRight(10);
                }
                ImGui.TextUnformatted(sb);
            }
        }

        if (draw.Textures.Count > 0)
        {
            ImGui.TextColored(InfoColor, "textures");
            for (var i = 0; i < draw.Textures.Count; i++)
            {
                var t = draw.Textures[i];
                var res = string.IsNullOrEmpty(t.Resource) ? $"tex#{t.Texture.Id}" : t.Resource;
                ImGui.TextUnformatted($"  slot {t.Slot}: {t.Name} → {res}");
            }
        }
    }

    private void DrawPerfReport(IReadOnlyList<DebugStatEntry> stats, IReadOnlyList<DebugTimerEntry> timers)
    {
        DrawPhasesTable(timers);
        ImGui.Spacing();
        DrawPassesTable(stats, timers);
        ImGui.Spacing();
        DrawPacksTable(stats);
    }

    // The Window-level phase timers — frame, build-commands, execute,
    // overlay, run-debuggables, swap. Identified by root-scope timers
    // (Scope == ""): these are emitted at the top level, not under
    // "passes/" or anywhere else.
    private static void DrawPhasesTable(IReadOnlyList<DebugTimerEntry> timers)
    {
        if (!ImGui.CollapsingHeader("Phases", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }
        if (!ImGui.BeginTable("perf-phases", 2,
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
        {
            return;
        }
        ImGui.TableSetupColumn("Phase");
        ImGui.TableSetupColumn("ms");
        ImGui.TableHeadersRow();
        for (var i = 0; i < timers.Count; i++)
        {
            var t = timers[i];
            if (!string.IsNullOrEmpty(t.Scope)) continue; // skip nested (pass/gpu) timers
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(t.Name);
            ImGui.TableSetColumnIndex(1); ImGui.Text($"{t.TotalMs:0.00}");
        }
        ImGui.EndTable();
    }

    // Per-pass table — auto-discovers pass names from entries whose
    // scope starts with "passes/" (DiagnosticsFrameRecorder convention)
    // or whose scope is exactly "gpu/passes" (Phase 7 GPU timing,
    // when enabled). Cols: pass, draws, tris, build-ms, gpu-ms.
    private static void DrawPassesTable(IReadOnlyList<DebugStatEntry> stats, IReadOnlyList<DebugTimerEntry> timers)
    {
        var passes = new SortedSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < stats.Count; i++)
        {
            if (TryExtractSegment(stats[i].Scope, "passes/", out var name))
            {
                passes.Add(name);
            }
        }
        for (var i = 0; i < timers.Count; i++)
        {
            var t = timers[i];
            if (TryExtractSegment(t.Scope, "passes/", out var name))
            {
                passes.Add(name);
            }
            // GPU timer convention: scope == "gpu/passes", name == passName.
            if (t.Scope == "gpu/passes")
            {
                passes.Add(t.Name);
            }
        }
        if (passes.Count == 0)
        {
            return;
        }
        if (!ImGui.CollapsingHeader($"Passes ({passes.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }
        if (!ImGui.BeginTable("perf-passes", 5,
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
        {
            return;
        }
        ImGui.TableSetupColumn("Pass");
        ImGui.TableSetupColumn("Draws");
        ImGui.TableSetupColumn("Tris");
        ImGui.TableSetupColumn("CPU ms");
        ImGui.TableSetupColumn("GPU ms");
        ImGui.TableHeadersRow();
        var sawGpuPath = false;
        var sawNonZeroGpu = false;
        foreach (var pass in passes)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(pass);
            ImGui.TableSetColumnIndex(1); ImGui.Text(FormatStat(stats, $"passes/{pass}/draws", asCount: true));
            ImGui.TableSetColumnIndex(2); ImGui.Text(FormatStat(stats, $"passes/{pass}/triangles", asCount: true));
            ImGui.TableSetColumnIndex(3); ImGui.Text(FormatTimerMs(timers, $"passes/{pass}/build"));

            // GPU timer path is gpu/passes/<name>. Three possible states:
            //   - timer absent ("-")     -> GPU timing disabled
            //   - timer present, ms = 0  -> driver returned zeros
            //                                (macOS GL through Metal
            //                                doesn't actually measure
            //                                per-pass time even when the
            //                                extension is exposed)
            //   - timer present, ms > 0  -> working
            (var gpu, var ms) = LookupTimerMsRaw(timers, $"gpu/passes/{pass}");
            ImGui.TableSetColumnIndex(4); ImGui.Text(gpu);
            if (gpu != "-")
            {
                sawGpuPath = true;
                if (ms > 0.0) sawNonZeroGpu = true;
            }
        }
        ImGui.EndTable();

        if (!sawGpuPath)
        {
            ImGui.TextDisabled("GPU ms: timing disabled. Enable via Controls > SponzaModern/Perf > GPU timing.");
        }
        else if (!sawNonZeroGpu)
        {
            ImGui.TextDisabled(
                "GPU ms: enabled but driver reports 0 for every pass. " +
                "macOS GL routes timestamps through Metal and reports submit-time, not GPU-execute-time " +
                "- timings are effectively unusable here. Linux/Windows drivers should populate normally.");
        }
    }

    private static (string formatted, double ms) LookupTimerMsRaw(IReadOnlyList<DebugTimerEntry> timers, string path)
    {
        for (var i = 0; i < timers.Count; i++)
        {
            if (timers[i].Path == path) return ($"{timers[i].TotalMs:0.00}", timers[i].TotalMs);
        }
        return ("-", 0.0);
    }

    // Per-pack table — auto-discovers pack names from stats whose scope
    // starts with "submeshes/" (skipping the top-level submeshes scope
    // itself, which carries aggregate counts not per-pack).
    private static void DrawPacksTable(IReadOnlyList<DebugStatEntry> stats)
    {
        var packs = new SortedSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < stats.Count; i++)
        {
            if (TryExtractSegment(stats[i].Scope, "submeshes/", out var name))
            {
                packs.Add(name);
            }
        }
        if (packs.Count == 0)
        {
            return;
        }
        if (!ImGui.CollapsingHeader($"Packs ({packs.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }
        if (!ImGui.BeginTable("perf-packs", 8,
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
        {
            return;
        }
        ImGui.TableSetupColumn("Pack");
        ImGui.TableSetupColumn("Batches");
        ImGui.TableSetupColumn("Primitives");
        ImGui.TableSetupColumn("Tris");
        ImGui.TableSetupColumn("Opaque");
        ImGui.TableSetupColumn("Mask");
        ImGui.TableSetupColumn("Blend");
        ImGui.TableSetupColumn("2-side");
        ImGui.TableHeadersRow();
        foreach (var pack in packs)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(pack);
            ImGui.TableSetColumnIndex(1); ImGui.Text(FormatStat(stats, $"submeshes/{pack}/batches",     asCount: true));
            ImGui.TableSetColumnIndex(2); ImGui.Text(FormatStat(stats, $"submeshes/{pack}/count",       asCount: true));
            ImGui.TableSetColumnIndex(3); ImGui.Text(FormatStat(stats, $"submeshes/{pack}/tris-total",  asCount: true));
            ImGui.TableSetColumnIndex(4); ImGui.Text(FormatStat(stats, $"submeshes/{pack}/opaque",      asCount: true));
            ImGui.TableSetColumnIndex(5); ImGui.Text(FormatStat(stats, $"submeshes/{pack}/mask",        asCount: true));
            ImGui.TableSetColumnIndex(6); ImGui.Text(FormatStat(stats, $"submeshes/{pack}/blend",       asCount: true));
            ImGui.TableSetColumnIndex(7); ImGui.Text(FormatStat(stats, $"submeshes/{pack}/double-sided",asCount: true));
        }
        ImGui.EndTable();
    }

    // Extracts the SINGLE segment immediately after `prefix` in `scope`.
    // "submeshes/main" + prefix "submeshes/" -> "main". Returns false
    // when scope doesn't start with prefix, OR equals it (top-level
    // scope with no per-pack/per-pass member).
    private static bool TryExtractSegment(string scope, string prefix, out string segment)
    {
        if (!scope.StartsWith(prefix, StringComparison.Ordinal))
        {
            segment = string.Empty;
            return false;
        }
        var rest = scope.AsSpan(prefix.Length);
        if (rest.Length == 0)
        {
            segment = string.Empty;
            return false;
        }
        var slash = rest.IndexOf('/');
        segment = slash < 0 ? rest.ToString() : rest[..slash].ToString();
        return segment.Length > 0;
    }

    private static string FormatStat(IReadOnlyList<DebugStatEntry> stats, string path, bool asCount)
    {
        for (var i = 0; i < stats.Count; i++)
        {
            if (stats[i].Path == path)
            {
                return asCount
                    ? ((long)stats[i].Value).ToString("N0")
                    : stats[i].Value.ToString("0.###");
            }
        }
        return "-";
    }

    private static string FormatTimerMs(IReadOnlyList<DebugTimerEntry> timers, string path)
    {
        for (var i = 0; i < timers.Count; i++)
        {
            if (timers[i].Path == path) return $"{timers[i].TotalMs:0.00}";
        }
        return "-";
    }

    private void DrawStatsTab(DebugFrameHistory history, IReadOnlyList<DebugStatEntry> entries)
    {
        foreach (var group in entries.GroupBy(entry => entry.Scope).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var label = string.IsNullOrWhiteSpace(group.Key) ? "Global" : group.Key;
            if (!ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
            {
                continue;
            }
            foreach (var entry in group)
            {
                DrawStatRow(history, entry);
            }
            ImGui.TreePop();
        }
    }

    private void DrawTimersTab(DebugFrameHistory history, IReadOnlyList<DebugTimerEntry> entries)
    {
        foreach (var group in entries.GroupBy(entry => entry.Scope).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var label = string.IsNullOrWhiteSpace(group.Key) ? "Global" : group.Key;
            if (!ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
            {
                continue;
            }
            foreach (var entry in group)
            {
                DrawTimerRow(history, entry);
            }
            ImGui.TreePop();
        }
    }

    private void DrawStatRow(DebugFrameHistory history, DebugStatEntry entry)
    {
        var summary = $"{entry.Name}: {FormatStatValue(entry)}";
        DrawSparklineToggleRow(history, entry.Path, summary, StatSelector);
    }

    private void DrawTimerRow(DebugFrameHistory history, DebugTimerEntry entry)
    {
        var summary = entry.CallCount > 1
            ? $"{entry.Name}: {entry.TotalMs:0.00} ms ({entry.CallCount}x)"
            : $"{entry.Name}: {entry.TotalMs:0.00} ms";
        DrawSparklineToggleRow(history, entry.Path, summary, TimerSelector);
    }

    private void DrawSparklineToggleRow(DebugFrameHistory history, string path, string summary, Func<DebugFrame, string, double> selector)
    {
        // The summary text + a small toggle button at the right that
        // expands / collapses an inline sparkline for this row. The
        // toggle state lives in sparklinesEnabled and persists across
        // frames within the renderer's lifetime.
        ImGui.TextUnformatted(summary);
        var enabled = sparklinesEnabled.Contains(path);
        // Right-align the toggle. "~" is the on indicator, "·" is off —
        // smaller surface than "graph" / "hide" but visually distinct.
        var icon = enabled ? "~" : "·";
        var iconWidth = ImGui.CalcTextSize(icon).X + ImGui.GetStyle().FramePadding.X * 2.0f;
        RightAlignNextWidget(iconWidth);
        ImGui.PushID(path);
        if (ImGui.SmallButton(icon))
        {
            if (enabled) sparklinesEnabled.Remove(path);
            else sparklinesEnabled.Add(path);
        }
        ImGui.PopID();

        if (enabled)
        {
            DrawSparkline(history, path, overlay: string.Empty, selector);
        }
    }

    private static string FormatStatValue(DebugStatEntry entry)
    {
        if (entry.Kind == DebugStatKind.Count)
        {
            // Counts are conceptually integers; round-trip through long to
            // drop the trailing ".00" the double formatter would otherwise
            // emit for whole numbers.
            return ((long)entry.Value).ToString("N0");
        }

        return entry.Value.ToString("0.###");
    }

    // Selector indirection lets the sparkline routine share a single
    // history walk between Stats and Timers without allocating closures
    // or branching inside the inner loop.
    private static readonly Func<DebugFrame, string, double> StatSelector = (frame, path) =>
    {
        for (var i = 0; i < frame.Stats.Count; i++)
        {
            if (frame.Stats[i].Path == path)
            {
                return frame.Stats[i].Value;
            }
        }

        return 0.0;
    };

    private static readonly Func<DebugFrame, string, double> TimerSelector = (frame, path) =>
    {
        for (var i = 0; i < frame.Timers.Count; i++)
        {
            if (frame.Timers[i].Path == path)
            {
                return frame.Timers[i].TotalMs;
            }
        }

        return 0.0;
    };

    private static void DrawSparkline(DebugFrameHistory history, string path, string overlay, Func<DebugFrame, string, double> selector)
    {
        var count = history.Count;
        if (count <= 1)
        {
            // PlotLines with <2 samples produces no curve; on the first
            // frame there's nothing useful to draw. The summary text is
            // already shown above the row by the caller, so we just
            // return silently.
            return;
        }

        var values = new float[count];
        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        var index = count - 1;
        foreach (var frame in history.EnumerateLatestFirst())
        {
            if (index < 0)
            {
                break;
            }

            var value = (float)selector(frame, path);
            values[index] = value;
            if (value < min) min = value;
            if (value > max) max = value;
            index--;
        }

        // Pad the range slightly so a flat line shows visibly inside the
        // plot rectangle rather than collapsing to the rim.
        if (max - min < 1e-6f)
        {
            var pad = Math.Max(1.0f, MathF.Abs(max) * 0.05f);
            min -= pad;
            max += pad;
        }

        ImGui.PlotLines(
            label: string.Empty,
            values: ref values[0],
            values_count: values.Length,
            values_offset: 0,
            overlay_text: overlay,
            scale_min: min,
            scale_max: max,
            graph_size: SparklineSize,
            stride: sizeof(float));
    }

    private static void DrawControls(DebugSystem debugSystem, IReadOnlyList<DebugControlEntry> entries, bool interactive)
    {
        foreach (var group in entries.GroupBy(entry => entry.Scope).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var label = string.IsNullOrWhiteSpace(group.Key) ? "Global" : group.Key;
            if (!ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
            {
                continue;
            }

            foreach (var entry in group)
            {
                DrawControl(debugSystem, entry, interactive);
            }

            ImGui.TreePop();
        }
    }

    private static void DrawControl(DebugSystem debugSystem, DebugControlEntry entry, bool interactive)
    {
        ImGui.PushID(entry.Path);

        if (!interactive)
        {
            // Frozen view: render the captured value as a static readout so
            // it's clear the slider isn't live, and so dragging it doesn't
            // silently mutate next frame's pending value via SetControlValue.
            ImGui.BeginDisabled();
        }

        switch (entry.Kind)
        {
            case DebugControlKind.Boolean:
            {
                var value = (bool)entry.Value;
                if (ImGui.Checkbox(entry.Name, ref value) && interactive)
                {
                    debugSystem.SetControlValue(entry.Path, value);
                }

                break;
            }

            case DebugControlKind.Float:
            {
                var value = (float)entry.Value;
                if (ImGui.SliderFloat(entry.Name, ref value, entry.Min, entry.Max, "%.2f") && interactive)
                {
                    debugSystem.SetControlValue(entry.Path, value);
                }

                break;
            }

            case DebugControlKind.Enum:
            {
                var value = (int)entry.Value;
                var options = entry.Options ?? Array.Empty<string>();
                if (ImGui.Combo(entry.Name, ref value, options.ToArray(), options.Count) && interactive)
                {
                    debugSystem.SetControlValue(entry.Path, value);
                }

                break;
            }

            case DebugControlKind.Button:
            {
                if (ImGui.Button(entry.Name) && interactive)
                {
                    debugSystem.SetControlValue(entry.Path, true);
                }

                break;
            }
        }

        if (!interactive)
        {
            ImGui.EndDisabled();
        }

        ImGui.PopID();
    }

    private static void DrawValues(IReadOnlyList<DebugValueEntry> entries)
    {
        foreach (var group in entries.GroupBy(entry => entry.Scope).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var label = string.IsNullOrWhiteSpace(group.Key) ? "Global" : group.Key;
            if (!ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
            {
                continue;
            }

            foreach (var entry in group)
            {
                ImGui.TextUnformatted($"{entry.Name}: {entry.Value}");
            }

            ImGui.TreePop();
        }
    }
}
