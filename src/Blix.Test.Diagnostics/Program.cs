using System.Numerics;
using Blix.Geometry;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Graphics;

// CLI test harness for Blix.Diagnostics. Mirrors the Blix.Test.Physics2D
// pattern: each ExpectX writes GREEN OK / RED FAIL, and the process exits
// non-zero if any case fails so CI can wire this in.

var t = new TestRunner();

// -- DebugSystem frame lifecycle ----------------------------------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    t.ExpectTrue("Initially no Current", sys.Current is null);
    t.ExpectTrue("Initially no LatestFrame", sys.LatestFrame is null);
    t.ExpectTrue("Initially not frozen", !sys.IsFrozen);
    t.ExpectTrue("Initially history empty", sys.History.Count == 0);

    var ctx = sys.BeginFrame(new RenderFrameContext(Width: 800, Height: 600));
    t.ExpectTrue("BeginFrame mints Current", ReferenceEquals(sys.Current, ctx));
    t.ExpectTrue("Frame number starts at 1", ctx.FrameNumber == 1);
    t.ExpectTrue("Frame context carried through", ctx.Frame.Width == 800 && ctx.Frame.Height == 600);

    sys.EndFrame();
    t.ExpectTrue("EndFrame clears Current", sys.Current is null);
    t.ExpectTrue("EndFrame populates LatestFrame", sys.LatestFrame is not null);
    t.ExpectTrue("Latest frame number == 1", sys.LatestFrame!.Number == 1);
    t.ExpectTrue("History count == 1 after first EndFrame", sys.History.Count == 1);

    // Second frame: number increments, latest updates.
    sys.BeginFrame(new RenderFrameContext(Width: 1024, Height: 768));
    t.ExpectTrue("Second frame number == 2", sys.Current!.FrameNumber == 2);
    sys.EndFrame();
    t.ExpectTrue("Latest now points at frame 2", sys.LatestFrame!.Number == 2);
    t.ExpectTrue("Latest carries new RenderFrameContext", sys.LatestFrame!.Frame.Width == 1024);

    // EndFrame without an active Current is idempotent.
    sys.EndFrame();
    sys.EndFrame();
    t.ExpectTrue("EndFrame idempotent when no Current", sys.History.Count == 2);
}

// -- Run() throws without BeginFrame ------------------------------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    try
    {
        sys.Run(new TestDebuggable("x"));
        t.Fail("Run without BeginFrame throws", "no exception thrown");
    }
    catch (InvalidOperationException)
    {
        t.Pass("Run without BeginFrame throws");
    }
}

// -- Snapshot is a defensive copy --------------------------------------------
// The critical correctness property: once a DebugFrame is produced, any
// further channel mutation on a subsequent context MUST NOT bleed in. We
// test this by holding a frame, beginning a new one that reuses the
// channels, and verifying entry counts on the held frame are unchanged.
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    var producer = new TestDebuggable("Sponza", debug =>
    {
        debug.Values.Value("FPS", 60);
        debug.Values.Value("Pos", new Vector3(1, 2, 3));
    });
    sys.Run(producer);
    sys.EndFrame();

    var frame1 = sys.LatestFrame!;
    t.ExpectTrue("Snapshot captured both values", frame1.Values.Count == 2);

    // Next frame: same producer adds three values.
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run(new TestDebuggable("Sponza", debug =>
    {
        debug.Values.Value("FPS", 30);
        debug.Values.Value("Pos", new Vector3(4, 5, 6));
        debug.Values.Value("Extra", 99);
    }));
    sys.EndFrame();

    t.ExpectTrue("Previous frame entry count unchanged after later frame", frame1.Values.Count == 2);
    t.ExpectTrue("Previous frame value still original", (int)frame1.Values[0].Value! == 60);
    t.ExpectTrue("Latest now has 3 values", sys.LatestFrame!.Values.Count == 3);
}

// -- Snapshot captures controls + draw + view-projection ---------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 2, Height: 2));
    var vp = Matrix4x4.CreateTranslation(7, 8, 9);
    sys.Run(new TestDebuggable("Frame", debug =>
    {
        debug.Controls.Toggle("On", true);
        debug.Controls.Float("Strength", 0.5f, 0.0f, 1.0f);
        using var view = debug.Draw.In("main", vp);
        debug.Draw.Line("ray", new Vector3(0, 0, 0), new Vector3(1, 0, 0),
            new GraphicsColor(1, 0, 0, 1));
    }));
    sys.EndFrame();

    var frame = sys.LatestFrame!;
    t.ExpectTrue("Controls captured", frame.Controls.Count == 2);
    t.ExpectTrue("Draw commands captured", frame.DrawCommands.Count == 1);
    t.ExpectTrue("Declared view captured",
        frame.Views.Count == 1 &&
        frame.Views[0].Name == "main" &&
        frame.Views[0].ViewProjection.M41 == 7 &&
        frame.Views[0].ViewProjection.M42 == 8 &&
        frame.Views[0].ViewProjection.M43 == 9);
    t.ExpectTrue("Command names the view it was drawn into",
        frame.DrawCommands[0].View == frame.Views[0].Id);
}

// -- Two views over the same geometry, in one frame ---------------------------
// The acceptance criterion for the view arc, and the thing RTS §212 needed and could not ask for: watch
// one body from a fixed vantage while the game camera does its own thing. It was impossible while a frame
// carried a single matrix, and it is a second scope now.
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 8, Height: 4));
    var game = Matrix4x4.CreateTranslation(1, 0, 0);
    var watch = Matrix4x4.CreateTranslation(0, 50, 0);
    sys.Run(new TestDebuggable("TwoViews", debug =>
    {
        var subject = new Vector3(3, 0, 3);
        using (debug.Draw.In("game", game))
        {
            debug.Draw.Cross("subject", subject, 1f, new GraphicsColor(1, 1, 1, 1));
        }

        using (debug.Draw.In("overhead", watch))
        {
            debug.Draw.Cross("subject", subject, 1f, new GraphicsColor(1, 1, 0, 1));
        }
    }));
    sys.EndFrame();

    var frame = sys.LatestFrame!;
    t.ExpectTrue("Both views declared in one frame", frame.Views.Count == 2);
    t.ExpectTrue("Views keep distinct identities", frame.Views[0].Id != frame.Views[1].Id);
    t.ExpectTrue("Same geometry drawn twice", frame.DrawCommands.Count == 2);
    t.ExpectTrue("Each command names its own view",
        frame.DrawCommands[0].View == frame.Views[0].Id &&
        frame.DrawCommands[1].View == frame.Views[1].Id);

    // Ids are interned from names, so the same name in a LATER frame is the same view — which is what any
    // trail or history has to rely on to mean anything.
    sys.BeginFrame(new RenderFrameContext(Width: 8, Height: 4));
    sys.Run(new TestDebuggable("Again", debug =>
    {
        using var again = debug.Draw.In("overhead", watch);
        debug.Draw.Cross("subject", Vector3.Zero, 1f, new GraphicsColor(1, 1, 0, 1));
    }));
    sys.EndFrame();
    t.ExpectTrue("A view keeps its identity across frames",
        sys.LatestFrame!.Views[0].Id == frame.Views[1].Id);
}

// -- Drawing with no view in scope is a bug, not a default --------------------
{
    var sys = new DebugSystem(historyCapacity: 2);
    sys.BeginFrame(new RenderFrameContext(Width: 2, Height: 2));
    var threw = false;
    try
    {
        sys.Run(new TestDebuggable("NoView", debug =>
            debug.Draw.Line("orphan", Vector3.Zero, Vector3.UnitX, new GraphicsColor(1, 0, 0, 1))));
    }
    catch (InvalidOperationException)
    {
        threw = true;
    }

    sys.EndFrame();
    t.ExpectTrue("A primitive with no view throws", threw);
}

// -- Ring buffer wrap --------------------------------------------------------
{
    var sys = new DebugSystem(historyCapacity: 3);
    for (var i = 0; i < 5; i++)
    {
        sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
        sys.EndFrame();
    }
    t.ExpectTrue("Count clamped to capacity", sys.History.Count == 3);
    t.ExpectTrue("Latest is most recent frame number", sys.LatestFrame!.Number == 5);

    var latestFirst = sys.History.EnumerateLatestFirst().Select(f => f.Number).ToArray();
    t.ExpectTrue("EnumerateLatestFirst order is 5,4,3",
        latestFirst.Length == 3 && latestFirst[0] == 5 && latestFirst[1] == 4 && latestFirst[2] == 3);

    t.ExpectTrue("GetByFrameNumber 5 hits", sys.History.GetByFrameNumber(5)?.Number == 5);
    t.ExpectTrue("GetByFrameNumber 3 hits", sys.History.GetByFrameNumber(3)?.Number == 3);
    t.ExpectTrue("GetByFrameNumber 2 (aged out) misses", sys.History.GetByFrameNumber(2) is null);
    t.ExpectTrue("GetByFrameNumber 99 (future) misses", sys.History.GetByFrameNumber(99) is null);
    t.ExpectTrue("GetByFrameNumber 0 misses", sys.History.GetByFrameNumber(0) is null);
}

// -- Freeze: latest + by number ---------------------------------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.EndFrame();
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.EndFrame();

    sys.Freeze();
    t.ExpectTrue("Freeze() captures latest", sys.IsFrozen && sys.FrozenFrame!.Number == 2);

    sys.Freeze(1);
    t.ExpectTrue("Freeze(1) captures frame 1", sys.FrozenFrame!.Number == 1);

    sys.Unfreeze();
    t.ExpectTrue("Unfreeze clears", !sys.IsFrozen && sys.FrozenFrame is null);
}

// -- Freeze on unknown frame number throws -----------------------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.EndFrame();

    try
    {
        sys.Freeze(99);
        t.Fail("Freeze(unknown) throws", "no exception thrown");
    }
    catch (ArgumentOutOfRangeException)
    {
        t.Pass("Freeze(unknown) throws");
    }
}

// -- Freeze on empty history throws ------------------------------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    try
    {
        sys.Freeze();
        t.Fail("Freeze() on empty history throws", "no exception thrown");
    }
    catch (InvalidOperationException)
    {
        t.Pass("Freeze() on empty history throws");
    }
}

// -- Frozen frame survives ring overwrite ------------------------------------
// The core point of holding FrozenFrame separately from the ring: a long
// inspection must not lose data when the ring wraps past it.
{
    var sys = new DebugSystem(historyCapacity: 3);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run(new TestDebuggable("S", debug => debug.Values.Value("k", "v1")));
    sys.EndFrame();

    sys.Freeze(); // freezes frame 1
    t.ExpectTrue("Frozen at frame 1", sys.FrozenFrame!.Number == 1);

    // Push enough frames to overwrite the ring slot frame 1 lived in.
    for (var i = 0; i < 10; i++)
    {
        sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
        sys.EndFrame();
    }

    t.ExpectTrue("History no longer holds frame 1", sys.History.GetByFrameNumber(1) is null);
    t.ExpectTrue("Frozen frame still has Number 1", sys.FrozenFrame!.Number == 1);
    t.ExpectTrue("Frozen frame still has original value",
        sys.FrozenFrame!.Values.Count == 1 &&
        (string)sys.FrozenFrame!.Values[0].Value! == "v1");
}

// -- DebugFrameHistory invariants --------------------------------------------
{
    try
    {
        _ = new DebugFrameHistory(0);
        t.Fail("History capacity 0 throws", "no exception thrown");
    }
    catch (ArgumentOutOfRangeException)
    {
        t.Pass("History capacity 0 throws");
    }
    try
    {
        _ = new DebugFrameHistory(-1);
        t.Fail("History capacity -1 throws", "no exception thrown");
    }
    catch (ArgumentOutOfRangeException)
    {
        t.Pass("History capacity -1 throws");
    }
}

// -- Stats: Count accumulates, Gauge overwrites -----------------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run(new TestDebuggable("S", debug =>
    {
        using (debug.Scope("Render"))
        {
            debug.Stats.Count("draws", 5);
            debug.Stats.Count("draws", 3);
            debug.Stats.Increment("draws");
            debug.Stats.Gauge("fps", 60.0);
            debug.Stats.Gauge("fps", 59.0);
        }
    }));
    sys.EndFrame();

    var frame = sys.LatestFrame!;
    var draws = frame.Stats.First(e => e.Name == "draws");
    var fps = frame.Stats.First(e => e.Name == "fps");
    t.ExpectTrue("Count accumulates (5+3+1)", draws.Value == 9.0);
    t.ExpectTrue("Count entry kind is Count", draws.Kind == DebugStatKind.Count);
    t.ExpectTrue("Count entry path includes scope", draws.Path == "S/Render/draws");
    t.ExpectTrue("Gauge keeps last write (59.0)", fps.Value == 59.0);
    t.ExpectTrue("Gauge entry kind is Gauge", fps.Kind == DebugStatKind.Gauge);
    t.ExpectTrue("Stats kept insertion order", frame.Stats[0].Name == "draws" && frame.Stats[1].Name == "fps");
}

// -- Stats: mixing Count and Gauge on the same path throws ------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    try
    {
        sys.Current!.Stats.Count("mix", 1);
        sys.Current!.Stats.Gauge("mix", 5.0);
        t.Fail("Stat kind mix throws", "no exception thrown");
    }
    catch (InvalidOperationException)
    {
        t.Pass("Stat kind mix throws");
    }
    sys.EndFrame();
}

// -- Timers: nested + repeated paths sum ------------------------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run(new TestDebuggable("S", debug =>
    {
        using (debug.Scope("Render"))
        {
            using (debug.Timers.Measure("Opaque"))
            {
                Spin(1);
                // Nested measure under the same scope; siblings produce
                // independent entries.
                using (debug.Timers.Measure("Sub"))
                {
                    Spin(1);
                }
            }
            // Second pass through the same Measure path: should aggregate
            // into the existing entry (Total sums, CallCount = 2).
            using (debug.Timers.Measure("Opaque"))
            {
                Spin(1);
            }
        }
    }));
    sys.EndFrame();

    var frame = sys.LatestFrame!;
    var opaque = frame.Timers.First(e => e.Name == "Opaque");
    var sub = frame.Timers.First(e => e.Name == "Sub");
    t.ExpectTrue("Repeated Measure aggregates CallCount", opaque.CallCount == 2);
    t.ExpectTrue("Repeated Measure aggregates TotalMs > 0", opaque.TotalMs > 0.0);
    t.ExpectTrue("Nested timer recorded independently", sub.CallCount == 1 && sub.TotalMs > 0.0);
    t.ExpectTrue("Timer path includes scope", opaque.Path == "S/Render/Opaque");
}

// -- Frame timer is emitted by DebugSystem ----------------------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    Spin(1);
    sys.EndFrame();

    var frame = sys.LatestFrame!;
    var frameTimer = frame.Timers.FirstOrDefault(e => e.Name == DebugSystem.FrameTimerName);
    t.ExpectTrue("Frame timer entry present", frameTimer is not null);
    t.ExpectTrue("Frame timer at root scope", frameTimer!.Scope == string.Empty);
    t.ExpectTrue("Frame timer path is bare name", frameTimer!.Path == DebugSystem.FrameTimerName);
    t.ExpectTrue("Frame timer measured positive duration", frameTimer!.TotalMs > 0.0);
    t.ExpectTrue("Frame timer single call", frameTimer!.CallCount == 1);
}

// -- Snapshot defensively copies Stats and Timers ---------------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run(new TestDebuggable("S", debug =>
    {
        debug.Stats.Count("c", 1);
        using (debug.Timers.Measure("m")) { Spin(1); }
    }));
    sys.EndFrame();
    var frame1 = sys.LatestFrame!;
    var statCount1 = frame1.Stats.Count;
    var timerCount1 = frame1.Timers.Count;

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run(new TestDebuggable("S", debug =>
    {
        debug.Stats.Count("c", 1);
        debug.Stats.Count("c2", 1);
        using (debug.Timers.Measure("m")) { Spin(1); }
        using (debug.Timers.Measure("m2")) { Spin(1); }
    }));
    sys.EndFrame();

    t.ExpectTrue("Previous frame Stats count unchanged", frame1.Stats.Count == statCount1);
    t.ExpectTrue("Previous frame Timers count unchanged", frame1.Timers.Count == timerCount1);
}

// -- DiagnosticsFrameRecorder: draws + triangles + per-pass attribution ------
// Drives the recorder against a synthetic command list with two passes and
// verifies both the top-level totals and the per-pass scoped paths.
{
    var sys = new DebugSystem(historyCapacity: 4);
    var recorder = new DiagnosticsFrameRecorder(sys);

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    var commandList = new RenderCommandList(recorder);
    commandList.Pass("opaque", new RenderPassDescription(
        RenderSurfaceHandle.Default,
        ClearColors: Array.Empty<GraphicsColor?>(),
        ClearDepth: false),
        pass =>
        {
            pass.DrawIndexed(new VertexBufferHandle(0), new IndexBufferHandle(0),
                new PipelineHandle(0), indexCount: 9, // 3 triangles
                Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>());
            pass.DrawIndexed(new VertexBufferHandle(0), new IndexBufferHandle(0),
                new PipelineHandle(0), indexCount: 6,
                Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>());
        });
    commandList.Pass("shadow", new RenderPassDescription(
        RenderSurfaceHandle.Default,
        ClearColors: Array.Empty<GraphicsColor?>(),
        ClearDepth: false),
        pass =>
        {
            pass.DrawIndexed(new VertexBufferHandle(0), new IndexBufferHandle(0),
                new PipelineHandle(0), indexCount: 3,
                Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>());
        });
    sys.EndFrame();

    var frame = sys.LatestFrame!;
    var topDraws = frame.Stats.First(e => e.Path == "draws");
    var topTriangles = frame.Stats.First(e => e.Path == "triangles");
    t.ExpectTrue("Top-level draws == 3", topDraws.Value == 3.0);
    t.ExpectTrue("Top-level triangles == 6", topTriangles.Value == 6.0); // 3+2+1
    t.ExpectTrue("Top-level draws scope is empty", topDraws.Scope == string.Empty);

    var opaqueDraws = frame.Stats.First(e => e.Path == "passes/opaque/draws");
    var opaqueTris = frame.Stats.First(e => e.Path == "passes/opaque/triangles");
    var shadowDraws = frame.Stats.First(e => e.Path == "passes/shadow/draws");
    t.ExpectTrue("Per-pass opaque draws == 2", opaqueDraws.Value == 2.0);
    t.ExpectTrue("Per-pass opaque triangles == 5", opaqueTris.Value == 5.0);
    t.ExpectTrue("Per-pass shadow draws == 1", shadowDraws.Value == 1.0);
    t.ExpectTrue("Per-pass scope is passes/<name>", opaqueDraws.Scope == "passes/opaque");

    // Per-pass build timer was recorded for each pass.
    var opaqueBuild = frame.Timers.First(e => e.Path == "passes/opaque/build");
    var shadowBuild = frame.Timers.First(e => e.Path == "passes/shadow/build");
    t.ExpectTrue("Per-pass build timer present (opaque)", opaqueBuild.CallCount == 1 && opaqueBuild.TotalMs >= 0.0);
    t.ExpectTrue("Per-pass build timer present (shadow)", shadowBuild.CallCount == 1 && shadowBuild.TotalMs >= 0.0);
}

// -- DiagnosticsFrameRecorder: instanced draws count every instance ----------
// The regression this exists for: OnDraw took indexCount/3 and ignored
// InstanceCount, so an instanced draw of four thousand trees reported one
// tree's worth of triangles. A frame read 246k where the geometry submitted
// was 2.8M, and nothing about the figure looked wrong -- RTSGame sized shadow
// work against it. A counter nobody can tell is lying is worse than none.
{
    var sys = new DebugSystem(historyCapacity: 4);
    var recorder = new DiagnosticsFrameRecorder(sys);

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    var commandList = new RenderCommandList(recorder);
    commandList.Pass("instanced", new RenderPassDescription(
        RenderSurfaceHandle.Default,
        ClearColors: Array.Empty<GraphicsColor?>(),
        ClearDepth: false),
        pass =>
        {
            // 4 triangles a mesh, 250 instances = 1,000 triangles.
            pass.DrawIndexedInstanced(new VertexBufferHandle(0), new IndexBufferHandle(0),
                new PipelineHandle(0), indexCount: 12, instanceCount: 250,
                Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(),
                new MaterialHandle(0), Array.Empty<byte>());
            // And a plain draw alongside it, so the two paths are known to agree
            // about what one instance means: 3 triangles, not 3 x nothing.
            pass.DrawIndexed(new VertexBufferHandle(0), new IndexBufferHandle(0),
                new PipelineHandle(0), indexCount: 9,
                Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>());
        });
    sys.EndFrame();

    var frame = sys.LatestFrame!;
    var tris = frame.Stats.First(e => e.Path == "passes/instanced/triangles");
    var draws = frame.Stats.First(e => e.Path == "passes/instanced/draws");
    // 1,000 + 3. Before the fix this read 4 + 3 = 7.
    t.ExpectTrue("Instanced triangles count every instance", tris.Value == 1003.0);
    t.ExpectTrue("Instanced draw is still one draw", draws.Value == 2.0);
}

// -- Recorder: OnPassEnd fires even if the record delegate throws ------------
// Without finally-protection on Pass(), the recorder's pass scope would
// leak past the throwing pass and subsequent OnDraw calls would attribute
// to the wrong path. Worth pinning down.
{
    var sys = new DebugSystem(historyCapacity: 4);
    var recorder = new DiagnosticsFrameRecorder(sys);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));

    var commandList = new RenderCommandList(recorder);
    try
    {
        commandList.Pass("flaky", new RenderPassDescription(
            RenderSurfaceHandle.Default,
            ClearColors: Array.Empty<GraphicsColor?>(),
            ClearDepth: false),
            _ => throw new InvalidOperationException("simulated"));
    }
    catch (InvalidOperationException) { /* expected */ }

    // After the throw, a fresh pass on the same list must NOT see attribution
    // pointing at "flaky" — which it would if OnPassEnd hadn't fired.
    commandList.Pass("good", new RenderPassDescription(
        RenderSurfaceHandle.Default,
        ClearColors: Array.Empty<GraphicsColor?>(),
        ClearDepth: false),
        pass => pass.DrawIndexed(new VertexBufferHandle(0), new IndexBufferHandle(0),
            new PipelineHandle(0), indexCount: 3,
            Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>()));

    sys.EndFrame();
    var frame = sys.LatestFrame!;
    t.ExpectTrue("Subsequent draw attributed correctly after throw",
        frame.Stats.Any(e => e.Path == "passes/good/draws") &&
        !frame.Stats.Any(e => e.Path == "passes/flaky/draws"));
}

// -- Recorder: OnDraw with no Current is a no-op (no IDebuggable game) -------
// The recorder is built against a DebugSystem that's never had BeginFrame
// called. OnDraw / OnPassBegin must silently skip — exceptions here would
// break headless or non-debuggable runtimes.
{
    var sys = new DebugSystem(historyCapacity: 4);
    var recorder = new DiagnosticsFrameRecorder(sys);
    var dummyCommand = new DrawIndexedCommand(
        new VertexBufferHandle(0), new IndexBufferHandle(0), new PipelineHandle(0),
        IndexCount: 3,
        Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>());
    try
    {
        recorder.OnPassBegin("any");
        recorder.OnDraw(in dummyCommand);
        recorder.OnPassEnd();
        t.Pass("Recorder with no active frame is silent");
    }
    catch (Exception ex)
    {
        t.Fail("Recorder with no active frame is silent", ex.Message);
    }
}

// -- ResourceUploader: IDebuggable surface -----------------------------------
// We can't construct a real ResourceUploader without an IGraphicsDevice,
// but Phase 3 test scope is the recorder + plumbing; the uploader's
// Debug() body is straight-line gauge writes covered by Phase 2's Stats
// tests. This block is intentionally omitted; the uploader's role is
// verified by the build (IDebuggable contract) and by Sponza launching.

// -- Events: severities + scope + chronological order -----------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run(new TestDebuggable("S", debug =>
    {
        debug.Events.Info("started");
        using (debug.Scope("Load"))
        {
            debug.Events.Warn("missing source", payload: "asset.png");
        }
        debug.Events.Error("kaboom");
    }));
    sys.EndFrame();

    var frame = sys.LatestFrame!;
    t.ExpectTrue("All three events captured", frame.Events.Count == 3);
    t.ExpectTrue("Chronological order preserved",
        frame.Events[0].Message == "started" &&
        frame.Events[1].Message == "missing source" &&
        frame.Events[2].Message == "kaboom");
    t.ExpectTrue("Severities correct",
        frame.Events[0].Severity == DebugEventSeverity.Info &&
        frame.Events[1].Severity == DebugEventSeverity.Warn &&
        frame.Events[2].Severity == DebugEventSeverity.Error);
    t.ExpectTrue("Path captures scope at emit time",
        frame.Events[0].Path == "S" &&
        frame.Events[1].Path == "S/Load" &&
        frame.Events[2].Path == "S");
    t.ExpectTrue("Payload preserved", (string)frame.Events[1].Payload! == "asset.png");
    t.ExpectTrue("Timestamps monotonic",
        frame.Events[0].TimestampMs <= frame.Events[1].TimestampMs &&
        frame.Events[1].TimestampMs <= frame.Events[2].TimestampMs);
}

// -- Events: AssetLoadReport payload -----------------------------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    var report = new AssetLoadReport(
        SourcePath: "Assets/foo.gltf",
        CookedPath: "Assets/foo.blixmesh",
        Mode: AssetLoadMode.Cooked,
        Bytes: 12345,
        LoadMs: 4.2);
    sys.Current!.Events.Info("loaded", payload: report);
    sys.EndFrame();

    var emitted = sys.LatestFrame!.Events[0];
    t.ExpectTrue("AssetLoadReport carried as payload", emitted.Payload is AssetLoadReport);
    var recovered = (AssetLoadReport)emitted.Payload!;
    t.ExpectTrue("Report fields round-trip",
        recovered.SourcePath == "Assets/foo.gltf" &&
        recovered.CookedPath == "Assets/foo.blixmesh" &&
        recovered.Mode == AssetLoadMode.Cooked &&
        recovered.Bytes == 12345);
}

// -- Snapshot defensively copies Events list ---------------------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Current!.Events.Info("a");
    sys.EndFrame();
    var frame1 = sys.LatestFrame!;
    var count1 = frame1.Events.Count;

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Current!.Events.Info("b");
    sys.Current!.Events.Info("c");
    sys.EndFrame();

    t.ExpectTrue("Previous frame Events count unchanged", frame1.Events.Count == count1);
}

// -- DebugSystem sinks: invoked after EndFrame, see populated frame ----------
{
    var sys = new DebugSystem(historyCapacity: 4);
    var captured = new List<DebugFrame>();
    sys.AddSink(new CapturingSink(captured));

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Current!.Events.Warn("x");
    t.ExpectTrue("Sink not called mid-frame", captured.Count == 0);
    sys.EndFrame();

    t.ExpectTrue("Sink called once after EndFrame", captured.Count == 1);
    t.ExpectTrue("Sink saw the emitted event",
        captured[0].Events.Count == 1 && captured[0].Events[0].Message == "x");
    t.ExpectTrue("Sinks list exposes registration", sys.Sinks.Count == 1);
}

// -- DebugSystem sinks: a throwing sink does not break the loop --------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    var captured = new List<DebugFrame>();
    sys.AddSink(new ThrowingSink());
    sys.AddSink(new CapturingSink(captured));

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.EndFrame();

    t.ExpectTrue("Subsequent sinks still invoked after a throw", captured.Count == 1);
    t.ExpectTrue("Throwing sink did not leak Current", sys.Current is null);
}

// -- ConsoleEventSink: prints only at or above MinSeverity -------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    var buffer = new StringWriter();
    sys.AddSink(new ConsoleEventSink(DebugEventSeverity.Warn, buffer));

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run(new TestDebuggable("S", debug =>
    {
        debug.Events.Info("quiet");
        debug.Events.Warn("loud");
        debug.Events.Error("loudest");
    }));
    sys.EndFrame();

    var output = buffer.ToString();
    t.ExpectTrue("Info entry filtered out", !output.Contains("quiet"));
    t.ExpectTrue("Warn entry printed",
        output.Contains("WARN") && output.Contains("loud"));
    t.ExpectTrue("Error entry printed",
        output.Contains("ERROR") && output.Contains("loudest"));
    t.ExpectTrue("Path included in line", output.Contains("S:"));
}

// -- ConsoleEventSink: root-scope events get <root> stub ---------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    var buffer = new StringWriter();
    sys.AddSink(new ConsoleEventSink(DebugEventSeverity.Info, buffer));
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Current!.Events.Info("hello");
    sys.EndFrame();
    t.ExpectTrue("Root scope renders as <root>", buffer.ToString().Contains("<root>"));
}

// -- Registry: registered contributors are walked by Run --------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    var registered = new TestDebuggable("Registered", debug =>
    {
        debug.Values.Value("from-registry", 1);
    });
    var explicitOne = new TestDebuggable("Explicit", debug =>
    {
        debug.Values.Value("from-explicit", 2);
    });
    sys.Register(registered);

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run(explicitOne);
    sys.EndFrame();

    var frame = sys.LatestFrame!;
    var fromRegistry = frame.Values.FirstOrDefault(e => e.Path == "Registered/from-registry");
    var fromExplicit = frame.Values.FirstOrDefault(e => e.Path == "Explicit/from-explicit");
    t.ExpectTrue("Registered contributor ran", fromRegistry is not null);
    t.ExpectTrue("Explicit contributor ran", fromExplicit is not null);
    t.ExpectTrue("Auto-scope applied to registered contributor",
        fromRegistry!.Scope == "Registered");
}

// -- Registry: re-Register same instance is a no-op (no duplicate run) ------
{
    var sys = new DebugSystem(historyCapacity: 4);
    var calls = 0;
    var d = new TestDebuggable("X", _ => calls++);
    sys.Register(d);
    sys.Register(d); // idempotent
    sys.Register(d);
    t.ExpectTrue("Contributors list deduplicated", sys.Contributors.Count == 1);

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run();
    sys.EndFrame();
    t.ExpectTrue("Debug() called exactly once for deduplicated registration", calls == 1);
}

// -- Registry: Unregister removes from future walks -------------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    var calls = 0;
    var d = new TestDebuggable("Y", _ => calls++);
    sys.Register(d);

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run();
    sys.EndFrame();

    var removed = sys.Unregister(d);
    t.ExpectTrue("Unregister returned true", removed);

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run();
    sys.EndFrame();

    t.ExpectTrue("Debug() not called after Unregister", calls == 1);
    t.ExpectTrue("Unregister of non-member returns false", !sys.Unregister(d));
}

// -- Registry: non-IDebuggable contributor doesn't break Run ----------------
// A contributor that only implements IDebugContributor (or a UI-only
// interface) must NOT make Run throw. The registry holds it, but the
// IDebuggable cast in the walk silently skips it.
{
    var sys = new DebugSystem(historyCapacity: 4);
    var bareContributor = new BareContributor("bare");
    sys.Register(bareContributor);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    try
    {
        sys.Run();
        t.Pass("Run() tolerates non-IDebuggable contributor");
    }
    catch (Exception ex)
    {
        t.Fail("Run() tolerates non-IDebuggable contributor", ex.Message);
    }
    sys.EndFrame();
}

// -- Registry: registered before history populates first frame correctly ----
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.Register(new TestDebuggable("Before", debug => debug.Values.Value("k", "v")));

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run();
    sys.EndFrame();

    t.ExpectTrue("Registered-before-first-frame appears in frame 1",
        sys.LatestFrame!.Values.Any(e => e.Path == "Before/k"));
}

// -- JsonDumpSink: Dump writes a file with the expected schema --------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    var tempDir = Path.Combine(Path.GetTempPath(), $"blix-dump-test-{Guid.NewGuid():N}");
    var sink = new JsonDumpSink(tempDir);

    sys.BeginFrame(new RenderFrameContext(Width: 1920, Height: 1080));
    sys.Run(new TestDebuggable("S", debug =>
    {
        debug.Values.Value("FPS", 60);
        debug.Stats.Count("draws", 42);
        using (debug.Timers.Measure("Opaque")) { }
        debug.Events.Warn("careful", payload: new AssetLoadReport(
            SourcePath: "x.gltf", CookedPath: null,
            Mode: AssetLoadMode.Fallback, Bytes: 0, LoadMs: 0));
    }));
    sys.EndFrame();

    var path = sink.Dump(sys.LatestFrame!);
    t.ExpectTrue("Dump wrote to expected filename",
        path.EndsWith(Path.Combine(tempDir, "frame-000001.json")) ||
        path.EndsWith($"frame-000001.json"));
    t.ExpectTrue("Dump file exists", File.Exists(path));

    var json = File.ReadAllText(path);
    t.ExpectTrue("JSON includes frame number", json.Contains("\"Number\": 1"));
    t.ExpectTrue("JSON includes Width/Height", json.Contains("\"Width\": 1920") && json.Contains("\"Height\": 1080"));
    t.ExpectTrue("JSON includes a Value entry", json.Contains("\"FPS\""));
    t.ExpectTrue("JSON includes a Stat entry", json.Contains("\"draws\""));
    t.ExpectTrue("JSON includes a Timer entry", json.Contains("\"Opaque\""));
    t.ExpectTrue("JSON includes an Event entry", json.Contains("\"careful\""));
    t.ExpectTrue("JSON serialises AssetLoadReport payload",
        json.Contains("\"SourcePath\"") && json.Contains("\"Fallback\""));
    t.ExpectTrue("JSON includes frame timer", json.Contains("\"frame\""));

    Directory.Delete(tempDir, recursive: true);
}

// -- JsonDumpSink: RequestDump arms Consume() then disarms ------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    var tempDir = Path.Combine(Path.GetTempPath(), $"blix-dump-test-{Guid.NewGuid():N}");
    var sink = new JsonDumpSink(tempDir);
    sys.AddSink(sink);

    t.ExpectTrue("Sink not armed by default", !sink.IsArmed);
    sink.RequestDump();
    t.ExpectTrue("RequestDump arms the sink", sink.IsArmed);

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.EndFrame();

    t.ExpectTrue("Consume disarms the sink", !sink.IsArmed);
    t.ExpectTrue("Armed Consume wrote a file",
        File.Exists(Path.Combine(tempDir, "frame-000001.json")));

    // Next frame without re-arming should NOT write.
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.EndFrame();
    t.ExpectTrue("Unarmed Consume does not write",
        !File.Exists(Path.Combine(tempDir, "frame-000002.json")));

    Directory.Delete(tempDir, recursive: true);
}

// -- JsonDumpSink: write failure does not throw -----------------------------
{
    // Pick a path that's guaranteed unwritable: a file (not a directory)
    // inside an existing directory, used as the OutputDirectory.
    var blockingFile = Path.Combine(Path.GetTempPath(), $"blix-blocking-{Guid.NewGuid():N}");
    File.WriteAllText(blockingFile, "not a directory");
    var sink = new JsonDumpSink(blockingFile);

    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.EndFrame();

    var savedErr = Console.Error;
    var capture = new StringWriter();
    Console.SetError(capture);
    try
    {
        var returnedPath = sink.Dump(sys.LatestFrame!);
        t.ExpectTrue("Dump returned path even on failure", returnedPath.Contains("frame-000001.json"));
        t.ExpectTrue("Dump did not throw on bad output dir", true);
        t.ExpectTrue("Failure logged to stderr", capture.ToString().Contains("JsonDumpSink failed"));
    }
    catch (Exception ex)
    {
        t.Fail("Dump did not throw on bad output dir", ex.Message);
    }
    finally
    {
        Console.SetError(savedErr);
        File.Delete(blockingFile);
    }
}

// -- Draw primitives: each method creates the right record type ------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    var d = sys.Current!.Draw;
    using var primitivesView = d.In("main", Matrix4x4.Identity);
    var col = new GraphicsColor(1, 0, 0, 1);
    d.Line("L", Vector3.Zero, Vector3.UnitX, col);
    d.Aabb("A", -Vector3.One, Vector3.One, col);
    d.Grid("G", Vector3.Zero, 4.0f, 8, col);
    d.Frustum("F", Matrix4x4.Identity, col);
    d.Sphere("Sp", Vector3.Zero, 1.0f, col);
    d.Plane("P", Vector3.Zero, Vector3.UnitY, 2.0f, col);
    d.Ray("R", Vector3.Zero, Vector3.UnitX, 5.0f, col);
    d.Capsule("Ca", -Vector3.UnitX, Vector3.UnitX, 0.5f, col);
    d.Obb("O", Matrix4x4.Identity, col);
    d.Cross("Cr", Vector3.Zero, 0.5f, col);
    d.Cone("Co", Vector3.Zero, Vector3.UnitY, 2.0f, 0.5f, col);
    d.Arrow("Ar", Vector3.Zero, Vector3.UnitZ, col);
    sys.EndFrame();

    var frame = sys.LatestFrame!;
    t.ExpectTrue("All 12 primitives recorded", frame.DrawCommands.Count == 12);
    t.ExpectTrue("Line record",     frame.DrawCommands[0]  is DebugDrawLine);
    t.ExpectTrue("Aabb record",     frame.DrawCommands[1]  is DebugDrawAabb);
    t.ExpectTrue("Grid record",     frame.DrawCommands[2]  is DebugDrawGrid);
    t.ExpectTrue("Frustum record",  frame.DrawCommands[3]  is DebugDrawFrustum);
    t.ExpectTrue("Sphere record",   frame.DrawCommands[4]  is DebugDrawSphere);
    t.ExpectTrue("Plane record",    frame.DrawCommands[5]  is DebugDrawPlane);
    t.ExpectTrue("Ray record",      frame.DrawCommands[6]  is DebugDrawRay);
    t.ExpectTrue("Capsule record",  frame.DrawCommands[7]  is DebugDrawCapsule);
    t.ExpectTrue("Obb record",      frame.DrawCommands[8]  is DebugDrawObb);
    t.ExpectTrue("Cross record",    frame.DrawCommands[9]  is DebugDrawCross);
    t.ExpectTrue("Cone record",     frame.DrawCommands[10] is DebugDrawCone);
    t.ExpectTrue("Arrow record",    frame.DrawCommands[11] is DebugDrawArrow);
}

// -- Draw primitives: scope path is captured at emit time -------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run(new TestDebuggable("physics", debug =>
    {
        using (debug.Scope("aabb"))
        {
            using var boxView = debug.Draw.In("main", Matrix4x4.Identity);
            debug.Draw.Aabb("box-3", -Vector3.One, Vector3.One,
                new GraphicsColor(0, 1, 0, 1));
        }
    }));
    sys.EndFrame();

    var c = (DebugDrawAabb)sys.LatestFrame!.DrawCommands[0];
    t.ExpectTrue("Path uses scope/scope/name", c.Path == "physics/aabb/box-3");
}

// -- LayersEnabled: prefix walk leaf -> root --------------------------------
{
    var state = new DebugState();
    t.ExpectTrue("Empty layers = always visible", state.IsPathVisible("a/b/c"));

    state.LayersEnabled["physics"] = false;
    t.ExpectTrue("Disabled root hides all under it",
        !state.IsPathVisible("physics/aabb/box-3") &&
        !state.IsPathVisible("physics") &&
        state.IsPathVisible("scene/foo"));

    state.LayersEnabled["physics"] = true;
    state.LayersEnabled["physics/aabb"] = false;
    t.ExpectTrue("Disabling a sub-prefix only hides the sub-tree",
        !state.IsPathVisible("physics/aabb/box-3") &&
        state.IsPathVisible("physics/velocities"));

    state.LayersEnabled["physics/aabb/box-3"] = true;
    // Leaf-explicit-true does NOT override an ancestor's false; prefix
    // walk short-circuits on the FIRST explicit false from leaf -> root.
    // Reorder check: walking finds the leaf first; leaf is true; keep
    // walking up to "physics/aabb" which is false; returns false.
    t.ExpectTrue("Explicit leaf-true does not override ancestor false",
        !state.IsPathVisible("physics/aabb/box-3"));

    t.ExpectTrue("Empty path is visible", state.IsPathVisible(string.Empty));
}

// -- JsonDumpSink: polymorphic draw commands serialise with discriminator ---
{
    var sys = new DebugSystem(historyCapacity: 4);
    var tempDir = Path.Combine(Path.GetTempPath(), $"blix-dump-draws-{Guid.NewGuid():N}");
    var sink = new JsonDumpSink(tempDir);

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    var d = sys.Current!.Draw;
    using var dumpView = d.In("overhead", Matrix4x4.CreateTranslation(0, 9, 0));
    var col = new GraphicsColor(1, 1, 1, 1);
    d.Sphere("ball", new Vector3(1, 2, 3), 0.5f, col);
    d.Arrow("vec", Vector3.Zero, Vector3.UnitX, col);
    sys.EndFrame();

    var path = sink.Dump(sys.LatestFrame!);
    var json = File.ReadAllText(path);
    t.ExpectTrue("JSON includes Sphere kind discriminator", json.Contains("\"Kind\": \"Sphere\""));
    t.ExpectTrue("JSON includes Sphere center field",
        json.Contains("\"Center\"") && json.Contains("\"X\": 1"));
    t.ExpectTrue("JSON includes Arrow kind", json.Contains("\"Kind\": \"Arrow\""));
    t.ExpectTrue("JSON arrow endpoints renamed to dodge factory collision",
        json.Contains("\"FromPoint\"") && json.Contains("\"ToPoint\""));

    // Schema 2. The version field is asserted because schema 1 did not have one despite the file claiming
    // a stable contract, and an unversioned dump is only readable by guessing.
    t.ExpectTrue("JSON declares its schema version", json.Contains("\"SchemaVersion\": 2"));
    t.ExpectTrue("JSON carries the frame's views", json.Contains("\"Views\""));
    t.ExpectTrue("JSON names the view by name, not by process-local id",
        json.Contains("\"View\": \"overhead\""));
    t.ExpectTrue("JSON no longer carries a single frame-wide camera",
        !json.Contains("\"DrawViewProjection\""));

    Directory.Delete(tempDir, recursive: true);
}

// -- IDebugGeometrySource: runs after IDebuggable, gated by layer ----------
{
    var sys = new DebugSystem(historyCapacity: 4);
    var order = new List<string>();
    var producer = new TestGeometryProducer("scene/foo",
        debugBody: _ => order.Add("Debug"),
        emitBody:  ctx =>
        {
            order.Add("EmitGeometry");
            using var submeshView = ctx.Draw.In("main", Matrix4x4.Identity);
            ctx.Draw.Aabb("submesh-0", -Vector3.One, Vector3.One,
                new GraphicsColor(0, 1, 0, 1));
        });
    sys.Register(producer);

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run();
    sys.EndFrame();

    t.ExpectTrue("Debug ran once", order.Count(x => x == "Debug") == 1);
    t.ExpectTrue("EmitGeometry ran once", order.Count(x => x == "EmitGeometry") == 1);
    t.ExpectTrue("Order is Debug then EmitGeometry",
        order.IndexOf("Debug") < order.IndexOf("EmitGeometry"));

    var frame = sys.LatestFrame!;
    var emit = (DebugDrawAabb)frame.DrawCommands[0];
    t.ExpectTrue("Auto-scope uses DebugName",
        emit.Path == "scene/foo/submesh-0");
}

// -- IDebugGeometrySource: disabled layer skips emission entirely -----------
// The critical property — when LayersEnabled gates the producer's path
// off, EmitGeometry is NOT called. This is what makes hundreds of debug
// AABBs zero-cost when the layer is off.
{
    var sys = new DebugSystem(historyCapacity: 4);
    var emitCalls = 0;
    var producer = new TestGeometryProducer("scene/foo",
        debugBody: _ => { },
        emitBody:  _ => emitCalls++);
    sys.Register(producer);

    sys.State.LayersEnabled["scene"] = false;
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run();
    sys.EndFrame();

    t.ExpectTrue("EmitGeometry skipped when parent prefix is disabled", emitCalls == 0);
    t.ExpectTrue("No draw commands emitted",
        sys.LatestFrame!.DrawCommands.Count == 0);

    sys.State.LayersEnabled["scene"] = true;
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run();
    sys.EndFrame();
    t.ExpectTrue("EmitGeometry called when re-enabled", emitCalls == 1);
}

// -- IDebugGeometrySource: layer gate at the producer's own path -----------
{
    var sys = new DebugSystem(historyCapacity: 4);
    var emitCalls = 0;
    sys.Register(new TestGeometryProducer("scene/foo",
        debugBody: _ => { }, emitBody: _ => emitCalls++));

    sys.State.LayersEnabled["scene/foo"] = false;
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run();
    sys.EndFrame();

    t.ExpectTrue("Producer-specific path disable also skips emission",
        emitCalls == 0);
}

// -- MeshWireframe + Normals: primitive shape correctness -------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    var verts = new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
    var edges = new int[] { 0, 1, 1, 2, 2, 0 };
    var pos = new Vector3[] { Vector3.Zero };
    var norms = new Vector3[] { Vector3.UnitY };
    using (sys.Current!.Draw.In("main", Matrix4x4.Identity))
    {
        sys.Current!.Draw.MeshWireframe("tri", verts, edges, new GraphicsColor(1, 1, 0, 1));
        sys.Current!.Draw.Normals("vn", pos, norms, 0.5f, new GraphicsColor(0, 1, 1, 1));
    }
    sys.EndFrame();

    var frame = sys.LatestFrame!;
    var wire = (DebugDrawMeshWireframe)frame.DrawCommands[0];
    t.ExpectTrue("MeshWireframe vertices captured",
        wire.Vertices.Count == 3 && wire.Edges.Count == 6);

    var n = (DebugDrawNormals)frame.DrawCommands[1];
    t.ExpectTrue("Normals positions captured",
        n.Positions.Count == 1 && n.Normals.Count == 1 && n.Length == 0.5f);
}

// -- JsonDumpSink: mesh primitives serialise summary fields only ------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    var tempDir = Path.Combine(Path.GetTempPath(), $"blix-mesh-dump-{Guid.NewGuid():N}");
    var sink = new JsonDumpSink(tempDir);

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    using var meshView = sys.Current!.Draw.In("main", Matrix4x4.Identity);
    sys.Current!.Draw.MeshWireframe("m",
        new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
        new int[] { 0, 1, 1, 2, 2, 0 },
        new GraphicsColor(1, 1, 1, 1));
    sys.Current!.Draw.Normals("n",
        new Vector3[] { Vector3.Zero, Vector3.UnitX },
        new Vector3[] { Vector3.UnitY, Vector3.UnitY },
        0.25f,
        new GraphicsColor(1, 1, 1, 1));
    sys.EndFrame();

    var path = sink.Dump(sys.LatestFrame!);
    var json = File.ReadAllText(path);
    t.ExpectTrue("MeshWireframe summary present",
        json.Contains("\"Kind\": \"MeshWireframe\"") &&
        json.Contains("\"VertexCount\": 3") &&
        json.Contains("\"EdgeCount\": 3"));
    t.ExpectTrue("Normals summary present",
        json.Contains("\"Kind\": \"Normals\"") &&
        json.Contains("\"NormalCount\": 2") &&
        json.Contains("\"Length\": 0.25"));
    // Confirm we did NOT embed the raw arrays (the contract is summary
    // only — dumps stay grep-able even for 100k-vertex meshes).
    t.ExpectTrue("Vertex array NOT embedded",
        !json.Contains("\"Vertices\""));

    Directory.Delete(tempDir, recursive: true);
}

// -- Selection: Select/Clear + cross-frame persistence ---------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    t.ExpectTrue("Initially no selection",
        sys.SelectedPath is null && sys.SelectedBounds is null);

    sys.Select("scene/foo/sub-3", new Bounds3(new Vector3(-1), new Vector3(1)));
    t.ExpectTrue("Select sets path", sys.SelectedPath == "scene/foo/sub-3");
    t.ExpectTrue("Select caches bounds",
        sys.SelectedBounds is { } b && b.Min == new Vector3(-1) && b.Max == new Vector3(1));

    // Survives across frames.
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run();
    sys.EndFrame();
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run();
    sys.EndFrame();
    t.ExpectTrue("Selection survives frame turnover",
        sys.SelectedPath == "scene/foo/sub-3");

    sys.ClearSelection();
    t.ExpectTrue("Clear nulls path", sys.SelectedPath is null && sys.SelectedBounds is null);
}

// -- CollectSelectables: walks registered IDebugSelectable producers --------
{
    var sys = new DebugSystem(historyCapacity: 4);
    var producerA = new TestSelectable("A",
        new DebugSelectable("a/1", new Bounds3(new Vector3(0), new Vector3(1))),
        new DebugSelectable("a/2", new Bounds3(new Vector3(2), new Vector3(3))));
    var producerB = new TestSelectable("B",
        new DebugSelectable("b/1", new Bounds3(new Vector3(10), new Vector3(11))));
    sys.Register(producerA);
    sys.Register(producerB);

    var s = sys.CollectSelectables();
    t.ExpectTrue("Collected from all sources", s.Count == 3);
    t.ExpectTrue("EntityPaths preserved",
        s[0].EntityPath == "a/1" && s[1].EntityPath == "a/2" && s[2].EntityPath == "b/1");

    // Calling again clears + refills — caller relies on this for picking.
    var s2 = sys.CollectSelectables();
    t.ExpectTrue("Repeated call returns fresh set, not appended",
        s2.Count == 3);
}

// -- Selection sweep in Run: auto-highlight + inspect dispatch --------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    var inspectCalls = new List<string>();
    sys.Register(new TestInspectable("scene/foo", path =>
    {
        inspectCalls.Add(path);
    }));

    // No selection: inspect not called, no auto-highlight emitted.
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run();
    sys.EndFrame();
    t.ExpectTrue("No inspect call when no selection", inspectCalls.Count == 0);
    t.ExpectTrue("No selection draw emitted",
        !sys.LatestFrame!.DrawCommands.Any(c => c.Path.StartsWith("selection/")));

    // With selection: inspect fires + highlight aabb appears.
    sys.Select("scene/foo/sub-0", new Bounds3(new Vector3(-1), new Vector3(1)));
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    // The highlight is drawn into whatever views the frame declared, so the frame needs one. A frame with
    // no views drew no picture, and there is nothing for system feedback to annotate.
    using (sys.Current!.Draw.In("main", Matrix4x4.Identity))
    {
        sys.Run();
    }

    sys.EndFrame();
    t.ExpectTrue("Inspect called with selected path",
        inspectCalls.Count == 1 && inspectCalls[0] == "scene/foo/sub-0");
    var hl = sys.LatestFrame!.DrawCommands.FirstOrDefault(c => c.Path.StartsWith("selection/"));
    t.ExpectTrue("Selection highlight aabb auto-emitted",
        hl is DebugDrawAabb a && a.Path == "selection/scene/foo/sub-0");
}

// -- Selection: inspect emissions land under "selection" scope --------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.Register(new TestInspectable("scene/foo", (path, ctx) =>
    {
        if (path != "scene/foo/sub-0") return;
        ctx.Values.Value("material", "Marble");
        ctx.Values.Value("submesh-index", 0);
    }));
    sys.Select("scene/foo/sub-0", new Bounds3(new Vector3(-1), new Vector3(1)));
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.Run();
    sys.EndFrame();

    var frame = sys.LatestFrame!;
    var material = frame.Values.First(v => v.Name == "material");
    t.ExpectTrue("Inspect Value scoped under 'selection'",
        material.Scope == "selection" && material.Path == "selection/material");
    t.ExpectTrue("Inspect Value carries data", (string)material.Value! == "Marble");
}

// -- Selection: highlight is emitted regardless of layer-filter state ------
// Phase 11 semantics: selection draws are system feedback, not user
// content, and bypass the layer filter so a stray "selection" toggle
// can't hide the very outline confirming what was picked. The emission
// always happens; the Window's render dispatch is what skips the
// filter check (tested via integration, not here).
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.Select("scene/foo", new Bounds3(new Vector3(0), new Vector3(1)));
    sys.State.LayersEnabled["selection"] = false;
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    using (sys.Current!.Draw.In("main", Matrix4x4.Identity))
    {
        sys.Run();
    }

    sys.EndFrame();

    t.ExpectTrue("Highlight emitted even when 'selection' layer is off",
        sys.LatestFrame!.DrawCommands.Any(c => c.Path.StartsWith("selection/") && c is DebugDrawAabb));
    t.ExpectTrue("Highlight includes both AABB and Cross marker",
        sys.LatestFrame!.DrawCommands.Any(c => c is DebugDrawAabb a && a.Path == "selection/scene/foo") &&
        sys.LatestFrame!.DrawCommands.Any(c => c is DebugDrawCross x && x.Path == "selection/scene/foo/marker"));
}

// -- Snapshot captures SelectedPath -----------------------------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.Select("entity-1", new Bounds3(Vector3.Zero, Vector3.One));
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.EndFrame();
    var frame1 = sys.LatestFrame!;
    t.ExpectTrue("DebugFrame.SelectedPath captured", frame1.SelectedPath == "entity-1");

    // Changing selection after snapshot doesn't mutate the snapshot.
    sys.Select("entity-2", new Bounds3(Vector3.Zero, Vector3.One));
    t.ExpectTrue("Previous frame's SelectedPath unchanged",
        frame1.SelectedPath == "entity-1");
}

// -- JsonDumpSink: SelectedPath shows in dump -------------------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    var tempDir = Path.Combine(Path.GetTempPath(), $"blix-sel-dump-{Guid.NewGuid():N}");
    var sink = new JsonDumpSink(tempDir);

    sys.Select("scene/main/submesh-7", new Bounds3(Vector3.Zero, Vector3.One));
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.EndFrame();

    var path = sink.Dump(sys.LatestFrame!);
    var json = File.ReadAllText(path);
    t.ExpectTrue("JSON includes SelectedPath",
        json.Contains("\"SelectedPath\": \"scene/main/submesh-7\""));

    Directory.Delete(tempDir, recursive: true);
}

// -- DebugContext.SelectedPath: snapshotted at BeginFrame -------------------
// Producers (e.g. GltfSceneInstance.EmitGeometry) read this to react to
// the current selection — must stay stable for the whole frame even if
// Select() is called mid-frame.
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.Select("entity-1", new Bounds3(Vector3.Zero, Vector3.One));

    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    t.ExpectTrue("ctx.SelectedPath captured at BeginFrame",
        sys.Current!.SelectedPath == "entity-1");

    // Mid-frame Select should NOT affect ctx.SelectedPath this tick.
    sys.Select("entity-2", new Bounds3(Vector3.Zero, Vector3.One));
    t.ExpectTrue("ctx.SelectedPath stable across mid-frame Select",
        sys.Current!.SelectedPath == "entity-1");
    sys.EndFrame();

    // Next frame picks up the change.
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    t.ExpectTrue("Next frame's ctx.SelectedPath has new value",
        sys.Current!.SelectedPath == "entity-2");
    sys.EndFrame();
}

// -- WallClockMs monotonic ---------------------------------------------------
{
    var sys = new DebugSystem(historyCapacity: 4);
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.EndFrame();
    var t1 = sys.LatestFrame!.WallClockMs;
    // Spin briefly so the Stopwatch advances at least one tick.
    var spin = System.Diagnostics.Stopwatch.StartNew();
    while (spin.Elapsed.TotalMilliseconds < 1.0) { }
    sys.BeginFrame(new RenderFrameContext(Width: 1, Height: 1));
    sys.EndFrame();
    var t2 = sys.LatestFrame!.WallClockMs;
    t.ExpectTrue("WallClockMs strictly increases", t2 > t1);
}

t.PrintSummary();
Environment.Exit(t.FailedCount);

// Busy-wait so a Timer measurement spans at least `targetMs` of wall time.
// Stopwatch is precise to sub-microsecond on modern hardware, so 1 ms is
// long enough to be measurable without making the test suite drag.
// Declared as a local function so it stays inside the implicit Main body
// (top-level statements + local functions must precede any type decls).
static void Spin(double targetMs)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.Elapsed.TotalMilliseconds < targetMs) { }
}

// ---------------------------------------------------------------------------

sealed class TestSelectable : IDebugSelectable
{
    private readonly DebugSelectable[] items;
    public TestSelectable(string name, params DebugSelectable[] items)
    {
        DebugName = name;
        this.items = items;
    }
    public string DebugName { get; }
    public void CollectSelectables(List<DebugSelectable> destination)
    {
        for (var i = 0; i < items.Length; i++) destination.Add(items[i]);
    }
}

sealed class TestInspectable : IDebugInspectable
{
    private readonly Action<string, DebugContext>? body2;
    private readonly Action<string>? body1;
    public TestInspectable(string name, Action<string> body) { DebugName = name; body1 = body; }
    public TestInspectable(string name, Action<string, DebugContext> body) { DebugName = name; body2 = body; }
    public string DebugName { get; }
    public void Inspect(string entityPath, DebugContext debug)
    {
        body1?.Invoke(entityPath);
        body2?.Invoke(entityPath, debug);
    }
}

sealed class TestGeometryProducer : IDebuggable, IDebugGeometrySource
{
    private readonly Action<DebugContext> debugBody;
    private readonly Action<DebugContext> emitBody;
    public TestGeometryProducer(string name, Action<DebugContext> debugBody, Action<DebugContext> emitBody)
    {
        DebugName = name;
        this.debugBody = debugBody;
        this.emitBody = emitBody;
    }
    public string DebugName { get; }
    public void Debug(DebugContext debug) => debugBody(debug);
    public void EmitGeometry(DebugContext debug) => emitBody(debug);
}

sealed class BareContributor : IDebugContributor
{
    public BareContributor(string name) { DebugName = name; }
    public string DebugName { get; }
}

sealed class CapturingSink : IDebugFrameSink
{
    private readonly List<DebugFrame> store;
    public CapturingSink(List<DebugFrame> store) { this.store = store; }
    public void Consume(DebugFrame frame) => store.Add(frame);
}

sealed class ThrowingSink : IDebugFrameSink
{
    public void Consume(DebugFrame frame) => throw new InvalidOperationException("boom");
}

sealed class TestDebuggable : IDebuggable
{
    private readonly Action<DebugContext>? body;

    public TestDebuggable(string name, Action<DebugContext>? body = null)
    {
        DebugName = name;
        this.body = body;
    }

    public string DebugName { get; }

    public void Debug(DebugContext debug) => body?.Invoke(debug);
}

sealed class TestRunner
{
    int passed;
    int failed;
    public int FailedCount => failed;

    public void ExpectTrue(string label, bool condition)
    {
        if (!condition) { Fail(label, "predicate was false"); return; }
        Pass(label);
    }

    public void Pass(string label) { Console.WriteLine($"  OK   {label}"); passed++; }
    public void Fail(string label, string detail) { Console.WriteLine($"  FAIL {label} - {detail}"); failed++; }

    public void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine($"{passed}/{passed + failed} passed, {failed} failed");
    }
}
