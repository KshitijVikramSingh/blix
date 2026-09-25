using Blix.Graphics;

namespace Blix.Diagnostics;

// Default IFrameRecorder implementation that funnels render-backend
// observations into the active DebugContext via Stats and Timers.
//
// Per draw it writes both a top-level total ("draws", "triangles") and a
// per-pass attribution ("passes/<name>/draws", "passes/<name>/triangles").
// "triangles" means triangles actually submitted -- index count over three,
// times the instance count. See OnDraw for what it meant before, and why
// that was worth a comment this long.
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

    // <b>Reported next to the triangles because the two together are checkable and either alone is not.</b>
    // The instance-count bug below hid behind exactly that: a triangle figure with nothing to divide it by
    // looks equally plausible whether or not the instances were counted.
    private const string InstancesStat = "instances";
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
        //
        // MULTIPLIED BY THE INSTANCE COUNT, which it was not, and the
        // omission made this counter quietly useless for exactly the
        // content it is most often pointed at. An instanced draw of four
        // thousand trees reported one tree's worth of triangles, so a
        // frame measured here read 246k while the geometry actually
        // submitted was 2.8M -- an eleven-fold undercount, and the number
        // looked entirely plausible.
        //
        // The cost of that is not the wrong figure, it is the decisions
        // taken against it: the external RTSGame consumer concluded from these rows that its
        // shadow casters were cheap at "59k triangles per cascade" and
        // sized work accordingly. A counter nobody can tell is wrong is
        // worse than no counter, because it is believed.
        //
        // long, because the product overflows int on frames this engine
        // already draws: 16,384 instances is the batch ceiling and a
        // canopy is several thousand triangles.
        var instances = Math.Max(1, command.InstanceCount);
        var triangles = (long)(command.IndexCount / 3) * instances;

        ctx.Stats.Increment(DrawsStat);
        ctx.Stats.Count(TrianglesStat, triangles);
        ctx.Stats.Count(InstancesStat, instances);

        if (currentPass is null)
        {
            return;
        }

        using (ctx.Scope(PassesRoot))
        using (ctx.Scope(currentPass))
        {
            ctx.Stats.Increment(DrawsStat);
            ctx.Stats.Count(TrianglesStat, triangles);
            ctx.Stats.Count(InstancesStat, instances);
        }
    }
}
