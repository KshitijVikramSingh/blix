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
// Holds the single source of truth for the panel layout, independent of the
// runtime that submits the ImGui draw data.
public sealed class DebugOverlayUi
{
    // Per-row sparkline opt-in. UI state, not diagnostics-system state —
    // lives only for the renderer's lifetime (one process run). Adding a
    // toggle per stat path keeps the Stats tab compact by default: rows
    // are text-only, user clicks the trailing icon to expand a graph.
    private readonly HashSet<string> sparklinesEnabled = new(StringComparer.Ordinal);

    // In-progress text edits, keyed by control path. UI state with the same lifetime as the
    // renderer, for the same reason as the set above — and load-bearing rather than a nicety:
    // an ImGui text field is edited across frames, so its buffer cannot be rebuilt from the
    // committed value on each one. See DebugControlKind.Text below.
    private readonly Dictionary<string, string> textEdits = new(StringComparer.Ordinal);

    // The selection shown last frame, so a new one brings the tab forward.
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
            // Selection tab — what is selected, its details and its edits. Shown while there is one; a new
            // selection brings it forward, and its close box clears it.
            if (debugSystem.SelectedPath is { } selPath)
            {
                var tabFlags = selPath != lastRenderedSelection ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                var open = true;
                if (ImGui.BeginTabItem("Selection", ref open, tabFlags))
                {
                    DrawSelectionTab(debugSystem, selPath, values, controls, interactive);
                    ImGui.EndTabItem();
                }
                if (!open) debugSystem.ClearSelection();
            }
            lastRenderedSelection = debugSystem.SelectedPath;

            if (ImGui.BeginTabItem("Perf"))
            {
                DrawPerfReport(debugSystem, stats, timers);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Pipeline"))
            {
                DrawPipelineTab(debugSystem);
                ImGui.EndTabItem();
            }

            if (debugSystem.LatestResourceSnapshot is { } resources && ImGui.BeginTabItem("Resources"))
            {
                DrawResources(resources);
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

            // An edit that applies to the selection lives on the Selection tab and nowhere else: the
            // same slider in the general list reads as a setting for the whole scene.
            var generalControls = controls.Where(c => !IsSelectionScope(c.Scope)).ToArray();
            if (generalControls.Length > 0 && ImGui.BeginTabItem("Controls"))
            {
                DrawControls(debugSystem, generalControls, interactive);
                ImGui.EndTabItem();
            }

            // Every key the application answers to, in one place: the host's, the ones bound to
            // controls (which the engine drives), and the ones the application describes and handles
            // itself (a claim the engine cannot check, so it is marked as one).
            var keys = interactive ? debugSystem.Current?.Keys.Entries : debugSystem.FrozenFrame?.Keys;
            if (keys is { Count: > 0 } && ImGui.BeginTabItem("Keys"))
            {
                DrawKeys(keys);
                ImGui.EndTabItem();
            }

            if (HasNonSelectionValues(values, debugSystem.SelectedPath is not null) &&
                ImGui.BeginTabItem("State"))
            {
                DrawValues(NonSelectionValues(values, debugSystem.SelectedPath is not null));
                ImGui.EndTabItem();
            }

            // Layers tab — reads paths from the live context so a new
            // producer's checkboxes appear immediately, not one frame later,
            // and from the layers already known, so one hidden or declared
            // hidden is still there to turn on.
            var liveDraws = debugSystem.Current?.Draw.Commands;
            if ((liveDraws is { Count: > 0 } || debugSystem.State.LayersEnabled.Count > 0)
                && ImGui.BeginTabItem("Layers"))
            {
                DrawLayersTree(debugSystem.State, liveDraws ?? Array.Empty<DebugDrawCommand>());
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
        // Pick mode sits beside it, and only when something can be picked.
        var canPick = debugSystem.Contributors.Any(c => c is IDebugSelectable);
        const string PickLabel = "Pick";
        var pickWidth = canPick
            ? ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(PickLabel).X
              + ImGui.GetStyle().ItemSpacing.X
            : 0f;
        RightAlignNextWidget(btnWidth + pickWidth);
        if (canPick)
        {
            var pick = debugSystem.State.PickMode;
            if (ImGui.Checkbox(PickLabel, ref pick)) debugSystem.State.PickMode = pick;
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Click to select what is under the pointer; click again for what is behind it. Or hold Alt and click.");
            }
            ImGui.SameLine();
        }
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

    private void DrawSelectionTab(
        DebugSystem debugSystem, string selPath, IReadOnlyList<DebugValueEntry> values,
        IReadOnlyList<DebugControlEntry> controls, bool interactive)
    {
        ImGui.TextUnformatted(selPath);
        if (debugSystem.LastPick is { Hit: { } hit } pick && hit == selPath)
        {
            ImGui.TextDisabled(pick.Excluded > 0
                ? $"under the cursor behind {pick.Excluded} earlier hit(s); click again to go further"
                : "under the cursor; click the same spot again for what is behind it");
        }
        var clearWidth = ImGui.CalcTextSize("Clear").X + ImGui.GetStyle().FramePadding.X * 2.0f;
        RightAlignNextWidget(clearWidth);
        if (ImGui.SmallButton("Clear"))
        {
            debugSystem.ClearSelection();
        }
        ImGui.Separator();

        // What an inspector declared for the selection (IDebugInspectable.Inspect, under the selection
        // scope), grouped by where it declared it: an inspector that writes
        // `using (debug.Scope("LOD"))` gets a LOD group holding both its readouts and its edits, so an
        // edit sits beside the numbers it changes. Anything declared at the top goes in the default group,
        // drawn first and without a heading.
        var hits = SelectionValues(values);
        var edits = controls.Where(c => IsSelectionScope(c.Scope)).ToArray();
        if (hits.Count == 0 && edits.Length == 0)
        {
            ImGui.TextDisabled("(no inspector data for this entity yet)");
            return;
        }

        var groups = hits.Select(v => SelectionGroup(v.Scope))
            .Concat(edits.Select(c => SelectionGroup(c.Scope)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(g => g.Length == 0 ? 0 : 1)
            .ThenBy(g => g, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var named = group.Length > 0;
            if (named && !ImGui.TreeNodeEx(group, ImGuiTreeNodeFlags.DefaultOpen)) continue;
            foreach (var value in hits)
            {
                if (SelectionGroup(value.Scope) == group) ImGui.TextUnformatted($"{value.Name}: {value.Value}");
            }

            foreach (var edit in edits)
            {
                if (SelectionGroup(edit.Scope) == group) DrawControl(debugSystem, edit, interactive);
            }

            if (named) ImGui.TreePop();
        }
    }

    // "selection/LOD" -> "LOD"; "selection" -> "" (the default group).
    private static string SelectionGroup(string scope) =>
        scope.Length <= SelectionScopeRoot.Length ? string.Empty : scope[(SelectionScopeRoot.Length + 1)..];

    private static void DrawKeys(IReadOnlyList<DebugKeyEntry> keys)
    {
        if (!ImGui.BeginTable("keys", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
        {
            return;
        }

        ImGui.TableSetupColumn("Key");
        ImGui.TableSetupColumn("Does");
        ImGui.TableSetupColumn("From");
        ImGui.TableHeadersRow();
        foreach (var k in keys)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(k.Binding);
            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(k.Description);
            ImGui.TableSetColumnIndex(2);
            var from = k.Source switch
            {
                DebugKeySource.Host => "host",
                DebugKeySource.Control => k.Path,
                _ => $"{k.Path} (declared)",
            };
            if (k.Source == DebugKeySource.Declared) ImGui.TextDisabled(from);
            else ImGui.TextUnformatted(from);
        }

        ImGui.EndTable();
    }

    // True if any registered contributor wants a custom panel. Cheap
    // walk; used only to gate showing the "Custom" tab so demos without
    // any IDebugUi producers don't see an empty tab.
    // Resources tab — the live GPU resource inventory from DebugSystem
    // .LatestResourceSnapshot (fed by the runtime each frame). Read-only:
    // textures (with streaming residency + footprint), buffers, pipelines,
    // shader programs, render surfaces.
    private static void DrawResources(ResourceRegistrySnapshot r)
    {
        long textureBytes = 0;
        var pending = 0;
        var streaming = 0;
        for (var i = 0; i < r.Textures.Count; i++)
        {
            textureBytes += r.Textures[i].ByteSize;
            switch (r.Textures[i].Residency)
            {
                case TextureResidency.Pending: pending++; break;
                case TextureResidency.Streaming: streaming++; break;
            }
        }
        long bufferBytes = 0;
        for (var i = 0; i < r.VertexBuffers.Count; i++) bufferBytes += r.VertexBuffers[i].ByteSize;
        for (var i = 0; i < r.IndexBuffers.Count; i++) bufferBytes += r.IndexBuffers[i].ByteSize;

        ImGui.TextUnformatted(
            $"{r.Textures.Count} textures ({FormatBytes(textureBytes)})  ·  " +
            $"{r.VertexBuffers.Count + r.IndexBuffers.Count} buffers ({FormatBytes(bufferBytes)})  ·  " +
            $"{r.Pipelines.Count} pipelines  ·  {r.ShaderPrograms.Count} shaders  ·  {r.RenderSurfaces.Count} surfaces");

        // Streaming progress (B4): how much of the texture set is still filling
        // in, derived from per-texture residency. Live upload queue depth +
        // drain-ms surface separately as gauges in the Stats tab.
        if (pending > 0 || streaming > 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f),
                $"streaming: {pending} pending, {streaming} in flight, {r.Textures.Count - pending - streaming} resident");
        }
        else
        {
            ImGui.TextDisabled("streaming: all textures resident");
        }
        ImGui.Separator();

        DrawResourceTextures(r.Textures);
        DrawResourceBuffers(r.VertexBuffers, r.IndexBuffers);
        DrawResourcePipelines(r.Pipelines, r.ShaderPrograms);
        DrawResourceSurfaces(r.RenderSurfaces);
    }

    private static void DrawResourceTextures(IReadOnlyList<TextureEntry> textures)
    {
        if (!ImGui.CollapsingHeader($"Textures ({textures.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }
        if (textures.Count == 0)
        {
            ImGui.TextDisabled("none");
            return;
        }
        if (!ImGui.BeginTable("res-textures", 6,
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY,
            new Vector2(0f, 220f)))
        {
            return;
        }
        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Size");
        ImGui.TableSetupColumn("Mips");
        ImGui.TableSetupColumn("Format");
        ImGui.TableSetupColumn("Kind");
        ImGui.TableSetupColumn("Footprint");
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        foreach (var t in textures)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); DrawResidencyName(t);
            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted($"{t.Width}×{t.Height}");
            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(t.MipCount.ToString());
            ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(t.Format.ToString());
            ImGui.TableSetColumnIndex(4); ImGui.TextUnformatted(KindLabel(t.Kind));
            ImGui.TableSetColumnIndex(5); ImGui.TextUnformatted(t.ByteSize > 0 ? FormatBytes(t.ByteSize) : "—");
        }
        ImGui.EndTable();
    }

    // Texture name coloured + tagged by streaming residency: red = Pending (no
    // mips yet), amber = Streaming (finer mips still landing), default = Resident.
    private static void DrawResidencyName(TextureEntry t)
    {
        switch (t.Residency)
        {
            case TextureResidency.Pending:
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"{t.Name}  (pending)");
                break;
            case TextureResidency.Streaming:
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"{t.Name}  (streaming)");
                break;
            default:
                ImGui.TextUnformatted(t.Name);
                break;
        }
    }

    private static string KindLabel(TextureKind kind) => kind switch
    {
        TextureKind.RenderSurfaceColor => "rt-color",
        TextureKind.RenderSurfaceDepth => "rt-depth",
        _ => "user",
    };

    private static void DrawResourceBuffers(IReadOnlyList<VertexBufferEntry> vbs, IReadOnlyList<IndexBufferEntry> ibs)
    {
        var total = vbs.Count + ibs.Count;
        if (!ImGui.CollapsingHeader($"Buffers ({total})"))
        {
            return;
        }
        if (total == 0)
        {
            ImGui.TextDisabled("none");
            return;
        }
        if (!ImGui.BeginTable("res-buffers", 3,
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
        {
            return;
        }
        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Type");
        ImGui.TableSetupColumn("Size");
        ImGui.TableHeadersRow();
        foreach (var b in vbs)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(b.Name);
            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted("vertex");
            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(FormatBytes(b.ByteSize));
        }
        foreach (var b in ibs)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(b.Name);
            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted("index");
            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(FormatBytes(b.ByteSize));
        }
        ImGui.EndTable();
    }

    private static void DrawResourcePipelines(IReadOnlyList<PipelineEntry> pipelines, IReadOnlyList<ShaderProgramEntry> shaders)
    {
        if (!ImGui.CollapsingHeader($"Pipelines ({pipelines.Count})  ·  Shaders ({shaders.Count})"))
        {
            return;
        }
        if (pipelines.Count > 0 && ImGui.BeginTable("res-pipelines", 3,
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
        {
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Shader");
            ImGui.TableSetupColumn("Stage");
            ImGui.TableHeadersRow();
            foreach (var p in pipelines)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(p.Name);
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted($"#{p.ShaderProgram.Id}");
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(p.IsCompute ? "compute" : "graphics");
            }
            ImGui.EndTable();
        }
        foreach (var s in shaders)
        {
            ImGui.BulletText(s.Name);
        }
    }

    private static void DrawResourceSurfaces(IReadOnlyList<RenderSurfaceEntry> surfaces)
    {
        if (surfaces.Count == 0)
        {
            return;
        }
        if (!ImGui.CollapsingHeader($"Render surfaces ({surfaces.Count})"))
        {
            return;
        }
        if (!ImGui.BeginTable("res-surfaces", 4,
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
        {
            return;
        }
        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Size");
        ImGui.TableSetupColumn("Color");
        ImGui.TableSetupColumn("Depth");
        ImGui.TableHeadersRow();
        foreach (var s in surfaces)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(s.Name);
            ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted($"{s.Width}×{s.Height}");
            ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(s.ColorAttachments.Count.ToString());
            ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(s.DepthTexture is not null ? "yes" : "—");
        }
        ImGui.EndTable();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes / (1024.0 * 1024.0):0.0} MB";
    }

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
        // The two switches above every layer: whether debug geometry draws at all, and whether the
        // scene hides it. Both are DebugState's, and this is the one panel that sets them.
        var drawAll = state.ShowDebugDraw;
        if (ImGui.Checkbox("Draw debug geometry", ref drawAll)) state.ShowDebugDraw = drawAll;
        ImGui.SameLine();
        var depthTest = state.DepthTestDrawing;
        if (ImGui.Checkbox("Hidden by the scene", ref depthTest)) state.DepthTestDrawing = depthTest;
        ImGui.Separator();

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
        foreach (var known in state.LayersEnabled.Keys)
        {
            if (!string.IsNullOrEmpty(known)) root.Ensure(known);
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
            // Set rather than removed: a layer declared hidden re-adds its default every frame it
            // is declared, so removing the entry would undo "All" one frame later.
            if (state.LayersEnabled.TryGetValue(c.FullPath, out var v) && !v)
            {
                state.LayersEnabled[c.FullPath] = true;
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

        // A node for a known layer that drew nothing this frame: listed, but no primitive counted.
        public void Ensure(string path)
        {
            var current = this;
            foreach (var seg in path.Split('/'))
            {
                if (!current.Children.TryGetValue(seg, out var child))
                {
                    child = new LayerNode(seg, current.FullPath.Length == 0 ? seg : current.FullPath + "/" + seg);
                    current.Children[seg] = child;
                }
                current = child;
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

    private void DrawPerfReport(DebugSystem debugSystem, IReadOnlyList<DebugStatEntry> stats, IReadOnlyList<DebugTimerEntry> timers)
    {
        DrawPhasesTable(timers);
        ImGui.Spacing();
        DrawDeviceFrame(debugSystem.Timing);
        ImGui.Spacing();
        DrawPassesTable(stats, timers, debugSystem.Timing, debugSystem.GpuPasses);
        ImGui.Spacing();
        DrawPacksTable(stats);
    }

    // The Window-level phase timers — frame, run-debuggables, build-commands,
    // execute. Identified by root-scope timers
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
    // What the device says the last frame cost, which "execute" above is one lump of: the wait for the
    // frame slot (GPU throttle and present pacing), recording the commands, and submitting them. And what
    // reached the device, which can differ from what was recorded (a zero-instance draw submits nothing).
    // Live even while frozen: the device keeps no history of these.
    private static void DrawDeviceFrame(IFrameTiming timing)
    {
        if (timing.LastFrame is not { } last) return;
        if (!ImGui.CollapsingHeader("Device frame", ImGuiTreeNodeFlags.DefaultOpen)) return;
        ImGui.TextUnformatted(
            $"wait {last.WaitMs:0.00} ms   encode {last.EncodeMs:0.00} ms   submit {last.SubmitPresentMs:0.00} ms");
        var w = last.Work;
        ImGui.TextUnformatted(
            $"submitted: {w.Passes} passes, {w.Draws} draws, {w.Triangles:N0} tris, " +
            $"{w.IndirectDraws} indirect ({w.IndirectCommands:N0} records), {w.Dispatches} dispatches");
        if (timing.IsolatePasses)
        {
            ImGui.TextColored(InfoColor,
                $"passes isolated: each submitted and timed alone ({timing.IsolatedFrames} frames); the frame runs slower");
        }
    }

    // One row per pass: what the application recorded, what reached the device, the CPU time to build it,
    // and its GPU time as a mean over the last frames (GpuPassWindow). With isolation on, the isolated
    // per-run mean too.
    private static void DrawPassesTable(
        IReadOnlyList<DebugStatEntry> stats, IReadOnlyList<DebugTimerEntry> timers, IFrameTiming timing, GpuPassWindow gpu)
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
            if (TryExtractSegment(timers[i].Scope, "passes/", out var name))
            {
                passes.Add(name);
            }
        }
        foreach (var name in timing.LastFramePasses.Keys) passes.Add(name);
        foreach (var (name, _) in gpu.ByCost()) passes.Add(name);
        if (passes.Count == 0)
        {
            return;
        }
        if (!ImGui.CollapsingHeader($"Passes ({passes.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var isolated = timing.IsolatePasses;
        if (!ImGui.BeginTable("perf-passes", isolated ? 8 : 7,
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
        {
            return;
        }
        ImGui.TableSetupColumn("Pass");
        ImGui.TableSetupColumn("Draws");
        ImGui.TableSetupColumn("Tris");
        ImGui.TableSetupColumn("Sent draws");
        ImGui.TableSetupColumn("Sent tris");
        ImGui.TableSetupColumn("CPU ms");
        ImGui.TableSetupColumn($"GPU ms ({gpu.Spanned}f)");
        if (isolated) ImGui.TableSetupColumn("Isolated ms");
        ImGui.TableHeadersRow();
        foreach (var pass in passes)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(pass);
            ImGui.TableSetColumnIndex(1); ImGui.Text(FormatStat(stats, $"passes/{pass}/draws", asCount: true));
            ImGui.TableSetColumnIndex(2); ImGui.Text(FormatStat(stats, $"passes/{pass}/triangles", asCount: true));
            var sent = timing.LastFramePasses.TryGetValue(pass, out var work);
            ImGui.TableSetColumnIndex(3); ImGui.Text(sent ? $"{work.Draws + work.IndirectDraws}" : "-");
            ImGui.TableSetColumnIndex(4); ImGui.Text(sent ? $"{work.Triangles:N0}" : "-");
            ImGui.TableSetColumnIndex(5); ImGui.Text(FormatTimerMs(timers, $"passes/{pass}/build"));
            var ms = gpu.MeanMs(pass);
            ImGui.TableSetColumnIndex(6); ImGui.Text(ms > 0.0 ? $"{ms:0.000}" : "-");
            if (isolated)
            {
                var iso = timing.IsolatedPassTotals.TryGetValue(pass, out var total) ? total.MeanMs : 0.0;
                ImGui.TableSetColumnIndex(7); ImGui.Text(iso > 0.0 ? $"{iso:0.000}" : "-");
            }
        }
        ImGui.EndTable();

        if (!timing.GpuTimestampsSupported)
        {
            ImGui.TextDisabled("GPU ms: this device reports no GPU timestamps.");
        }
        else
        {
            // Sponza's report carried this and the overlay did not, so the overlay's numbers read as costs.
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(
                "GPU ms attribute, they do not decompose: on a tile-based GPU (Apple, through MoltenVK) a pass's " +
                "timestamps bracket its encoding, so passes need not sum to the frame. Paired A/B runs give cost.");
            ImGui.PopTextWrapPos();
        }
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

    private void DrawControls(DebugSystem debugSystem, IReadOnlyList<DebugControlEntry> entries, bool interactive)
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

    private void DrawControl(DebugSystem debugSystem, DebugControlEntry entry, bool interactive)
    {
        ImGui.PushID(entry.Path);
        // A bound key is written beside the control it drives; the widget's id stays its path.
        var label = entry.Key is { } key ? $"{entry.Name} [{DebugKeysChannel.Name(key)}]" : entry.Name;

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
                if (ImGui.Checkbox(label, ref value) && interactive)
                {
                    debugSystem.SetControlValue(entry.Path, value);
                }

                break;
            }

            case DebugControlKind.Float:
            {
                var value = (float)entry.Value;
                if (ImGui.SliderFloat(label, ref value, entry.Min, entry.Max, "%.2f") && interactive)
                {
                    debugSystem.SetControlValue(entry.Path, value);
                }

                break;
            }

            case DebugControlKind.Enum:
            {
                var value = (int)entry.Value;
                var options = entry.Options ?? Array.Empty<string>();
                if (ImGui.Combo(label, ref value, options.ToArray(), options.Count) && interactive)
                {
                    debugSystem.SetControlValue(entry.Path, value);
                }

                break;
            }

            case DebugControlKind.Button:
            {
                if (ImGui.Button(label) && interactive)
                {
                    debugSystem.SetControlValue(entry.Path, true);
                }

                break;
            }

            case DebugControlKind.Text:
            {
                // <b>The edit buffer has to outlive the frame.</b> Every other control here is
                // stateless: a slider is handed this frame's float and hands one back. A text
                // field is not — it is being edited across many frames, and re-seeding it from
                // the committed value each frame overwrites the character just typed before it
                // can be seen. The field looked completely dead.
                //
                // So while the item is active its buffer is kept here and the committed value is
                // ignored; the moment it is not, the buffer is dropped and the field follows the
                // value again, so a control written from anywhere else still shows up.
                var editing = textEdits.TryGetValue(entry.Path, out var buffer);
                if (!editing) buffer = (string)entry.Value;

                var length = (uint)Math.Max(1, entry.MaxLength);
                var entered = ImGui.InputText(
                    label, ref buffer, length, ImGuiInputTextFlags.EnterReturnsTrue);

                if (ImGui.IsItemActive()) textEdits[entry.Path] = buffer;
                else textEdits.Remove(entry.Path);

                // <b>Committed on Enter or on losing focus, not per keystroke.</b> A name is
                // something you finish typing. Per-keystroke would make "spin" a real state on
                // the way to "spine", and a tool reacting to it would reload four times and fail
                // three.
                if ((entered || ImGui.IsItemDeactivatedAfterEdit()) && interactive)
                {
                    debugSystem.SetControlValue(entry.Path, buffer);
                    textEdits.Remove(entry.Path);
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
