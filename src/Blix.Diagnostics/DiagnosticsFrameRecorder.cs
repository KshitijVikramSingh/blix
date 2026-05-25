using Blix.Graphics;

namespace Blix.Diagnostics;

// Default IFrameRecorder implementation that funnels render-backend
// observations into the active DebugContext via Stats and Timers.
//
// Per draw it writes both a top-level total ("draws", "triangles") and a
// per-pass attribution ("passes/<name>/draws", "passes/<name>/triangles").
// The double-counting is intentional: it removes the need for sinks to
// aggregate across pass rows to show a frame total, and the channels'
// path-keyed aggregation is fast enough that two Stats writes per draw
// is invisible compared to a real GL draw call.
//
// Pass timing: OnPassBegin opens scope "passes/<name>" only long enough
// to start a Timers.Measure("build"). The returned token captures the
// path at construction (see DebugTimersChannel.Measure), so disposing it
// in OnPassEnd records against the correct path even though by then the
// scope stack has unwound. This lets the recorder time pass-recording
// without leaving residual scope state around the rest of the frame.
//
// All counters silently skip when DebugSystem.Current is null (e.g. the
// game loop isn't IDebuggable, so no DebugContext exists). That matches
// the rest of the diagnostics surface: producing diagnostics is opt-in.
public sealed class DiagnosticsFrameRecorder : IFrameRecorder
{
    private const string PassesRoot = "passes";
    private const string DrawsStat = "draws";
    private const string TrianglesStat = "triangles";
    private const string PassBuildTimer = "build";

    private readonly DebugSystem system;
    private string? currentPass;
    private IDisposable? currentPassTimer;

    public DiagnosticsFrameRecorder(DebugSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        this.system = system;
    }

    public void OnPassBegin(string passName)
    {
        ArgumentNullException.ThrowIfNull(passName);
        currentPass = passName;

        var ctx = system.Current;
        if (ctx is null)
        {
            return;
        }

        // Push scope only long enough to start the measure — the token
        // captures its path at Measure() time, so it's safe to dispose
        // the scopes immediately.
        using (ctx.Scope(PassesRoot))
        using (ctx.Scope(passName))
        {
            currentPassTimer = ctx.Timers.Measure(PassBuildTimer);
        }
    }

    public void OnPassEnd()
    {
        // Dispose first so the elapsed measurement is recorded; null the
        // fields second so a late OnDraw (which would be a dispatcher bug)
        // cannot retroactively attribute to a closed pass.
        currentPassTimer?.Dispose();
        currentPassTimer = null;
        currentPass = null;
    }

    public void OnDraw(in DrawIndexedCommand command)
    {
        var ctx = system.Current;
        if (ctx is null)
        {
            return;
        }

        // Triangle-list assumption matches every pipeline in the engine
        // today. If line/point primitives appear later this becomes a
        // pipeline-aware lookup, but for now indexCount/3 is honest.
        var triangles = command.IndexCount / 3;

        ctx.Stats.Increment(DrawsStat);
        ctx.Stats.Count(TrianglesStat, triangles);

        if (currentPass is null)
        {
            return;
        }

        using (ctx.Scope(PassesRoot))
        using (ctx.Scope(currentPass))
        {
            ctx.Stats.Increment(DrawsStat);
            ctx.Stats.Count(TrianglesStat, triangles);
        }
    }
}
