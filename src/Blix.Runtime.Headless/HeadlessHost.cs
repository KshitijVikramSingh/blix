using Blix.Core;
using Blix.Diagnostics;
using Blix.Graphics;

namespace Blix.Runtime.Headless;

/// <summary>
/// Runs a headless-capable <see cref="IGameLoop"/> with no window, no device and no wall clock.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same lifecycle for a loop that can run without a device, not a window removed from any
/// game.</b> A loop that creates GPU resources, or casts its device to a backend's, cannot run here,
/// and <see cref="NoGraphicsDevice"/> is what guarantees it finds out at the first such call rather
/// than drawing nothing quietly. Being headless-capable is something a loop is written to be.
/// </para>
/// <para>
/// <b>The same order, and the same arguments.</b> Each frame is what a window's is:
/// input held still for the tick, <see cref="IGameLoop.OnUpdate"/>, the loop's diagnostics,
/// <see cref="IGameLoop.OnRender"/>, then the dump and the frame bound. What differs is only what a
/// headless run cannot have. There is no device, so <see cref="IGameLoop.OnLoad"/> is handed a
/// <see cref="NoGraphicsDevice"/>. Nothing executes the recorded commands, so the render pass
/// counts in the diagnostics are what the loop recorded rather than what a GPU drew. And time is
/// a fixed step, so the same arguments give the same run.
/// </para>
/// <para>
/// <b>Input is scripted.</b> A <paramref name="input"/> callback is handed the frame number and the
/// input state before each tick, and records into it what a device would have. A replay, a test
/// and a bot drive a loop the same way.
/// </para>
/// </remarks>
public sealed class HeadlessHost : IRenderHost, IDebugHost
{
    private readonly IGameLoop gameLoop;
    private readonly HeadlessOptions options;
    private readonly Action<int, InputState>? input;
    private readonly InputState inputState = new();
    private readonly DebugSystem? debugSystem;
    private readonly DiagnosticsFrameRecorder? frameRecorder;
    private readonly JsonDumpSink? jsonDumpSink;
    private bool closeRequested;

    /// <param name="gameLoop">The loop to run.</param>
    /// <param name="options">Size, bound and step. <see cref="HeadlessOptions.FromArgs"/> reads them.</param>
    /// <param name="input">Called before each tick with the frame number, to record input.</param>
    public HeadlessHost(IGameLoop gameLoop, HeadlessOptions? options = null, Action<int, InputState>? input = null)
    {
        ArgumentNullException.ThrowIfNull(gameLoop);
        this.gameLoop = gameLoop;
        this.options = options ?? HeadlessOptions.Default;
        this.input = input;

        // The same sinks a window attaches, for the same reason: a dump taken here must be one
        // that can be put beside a dump taken there.
        if (gameLoop is IDebugContributor)
        {
            debugSystem = new DebugSystem();
            frameRecorder = new DiagnosticsFrameRecorder(debugSystem);
            debugSystem.AddSink(new ConsoleEventSink());
            if (Environment.GetEnvironmentVariable("BLIX_DIAG") != "off")
            {
                var intervalEnv = Environment.GetEnvironmentVariable("BLIX_DIAG_INTERVAL");
                var interval = int.TryParse(intervalEnv, out var n) && n > 0 ? n : 60;
                debugSystem.AddSink(new PeriodicConsoleSummarySink(interval));
            }

            jsonDumpSink = new JsonDumpSink();
            debugSystem.AddSink(jsonDumpSink);
            if (this.options.Diagnostics) debugSystem.State.ShowOverlay = true;
        }
    }

    /// <summary>How many frames have run.</summary>
    public int Frames { get; private set; }

    /// <summary>Where a requested dump is written.</summary>
    public string? DumpDirectory => jsonDumpSink?.OutputDirectory;

    /// <inheritdoc />
    public IInputState Input => inputState;

    /// <inheritdoc />
    public (int Width, int Height) LogicalSize => (options.Width, options.Height);

    /// <inheritdoc />
    /// <remarks>Null: there is no display.</remarks>
    public int? DisplayRefreshHz => null;

    /// <inheritdoc />
    /// <remarks>
    /// Nothing is submitted, so nothing is reported: no last frame, no timestamps, no totals. Zeros
    /// would read as a frame that cost nothing, which is a different claim.
    /// </remarks>
    public IFrameTiming Timing => FrameTimings.None;

    /// <inheritdoc />
    public DebugContext? CurrentDebug => debugSystem?.Current;

    /// <inheritdoc />
    public DebugSystem? System => debugSystem;

    /// <summary>Run until the frame bound, or until the loop asks to close.</summary>
    public void Run()
    {
        // Registered as a window registers them, the device before OnLoad and the loop after it, so a
        // dump from here lists the same contributors in the same order as one from there.
        var device = new NoGraphicsDevice();
        debugSystem?.Register(new GraphicsDeviceContributor(device));
        gameLoop.OnLoad(this, device);
        debugSystem?.Register((IDebugContributor)gameLoop);

        var frame = new RenderFrameContext(options.Width, options.Height);
        var total = 0.0;
        while (!closeRequested)
        {
            total += options.Step;
            var time = new Time(total, options.Step);

            input?.Invoke(Frames, inputState);
            inputState.BeginTick();
            gameLoop.OnUpdate(time);

            if (debugSystem is not null)
            {
                debugSystem.BeginFrame(frame, LogicalSize, inputState);
                using (debugSystem.Current!.Timers.Measure("run-debuggables"))
                {
                    debugSystem.Run();
                }
            }

            // Recorded and never executed. The recorder still counts every pass and draw, so a
            // headless dump says what the loop asked for.
            var commandList = new RenderCommandList(frameRecorder);
            using (debugSystem?.Current?.Timers.Measure("build-commands"))
            {
                gameLoop.OnRender(time, frame, commandList);
            }

            // Armed before EndFrame and counted like --frames, as in a window.
            if (options.DumpOnFrame > 0 && Frames + 1 == options.DumpOnFrame) jsonDumpSink?.RequestDump();
            debugSystem?.EndFrame();

            Frames++;
            if (options.ExitAfterFrames > 0 && Frames >= options.ExitAfterFrames)
            {
                Console.WriteLine($"Exiting after {Frames} frame(s) as asked.");
                break;
            }
        }

        gameLoop.OnUnload();
    }

    /// <inheritdoc />
    public void RequestClose() => closeRequested = true;

    /// <inheritdoc />
    /// <remarks>Nothing to title.</remarks>
    public void SetTitle(string title) { }

    /// <inheritdoc />
    /// <remarks>No cursor to capture.</remarks>
    public void SetCursorCaptured(bool captured) { }

    /// <inheritdoc />
    /// <remarks>No display to sync to.</remarks>
    public void SetVSync(bool enabled) { }

    /// <inheritdoc />
    /// <remarks>A headless run draws no UI, so no texture ever reaches a panel. Returns 0.</remarks>
    public nint RegisterUiTexture(TextureHandle texture) => 0;

    /// <inheritdoc />
    public void ReleaseUiTexture(nint id) { }
}
