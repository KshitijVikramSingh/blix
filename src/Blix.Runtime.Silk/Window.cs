using Blix.Audio;
using Blix.Audio.OpenAL;
using Blix.Core;
using Blix.Diagnostics;
using Blix;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;
using Silk.NET.Core;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SilkWindowOptions = Silk.NET.Windowing.WindowOptions;
using BlixWindowOptions = Blix.Runtime.Silk.WindowOptions;
using BlixKey = Blix.Core.Key;
using BlixMouseButton = Blix.Core.MouseButton;
using SilkKey = Silk.NET.Input.Key;
using BlixGamepadButton = Blix.Core.GamepadButton;
using BlixGamepadAxis = Blix.Core.GamepadAxis;
using SilkMouseButton = Silk.NET.Input.MouseButton;

namespace Blix.Runtime.Silk;

// The Vulkan window/runtime adapter. Owns a Silk.NET window in
// "Vulkan API" mode, pumps the IGameLoop, and wires the diagnostics surface.
//
// Owns the Silk window, Vulkan device/surface/swapchain, OpenAL session, input
// snapshot, optional ImGui and diagnostics surfaces, and the IGameLoop lifecycle.
// Applications still own their frame topology and recorded rendering work; this
// class is the desktop composition edge that executes and presents it.
public sealed class Window : IRenderHost, IAudioHost, IDebugHost, IDisposable
{
    private readonly IWindow window;
    private readonly IGameLoop gameLoop;
    private readonly IUiSource? uiSource;
    private readonly IRuntimeDiagnosticsSink? diagnostics;
    private readonly DebugSystem? debugSystem;
    private readonly DiagnosticsFrameRecorder? frameRecorder;
    private readonly JsonDumpSink? jsonDumpSink;
    private VulkanGraphicsDevice? graphicsDevice;
    private OpenALAudioDevice? audioDevice;
    private IInputContext? input;
    private VkLineDrawer? lineDrawer;
    private VkImGuiRenderer? imguiRenderer;
    private float lastWheel;
    private double totalTime;
    private readonly WindowOptions options;
    private readonly int validationErrorsBefore;
    private int renderedFrames;

    // Who owns each in-flight press. See Blix.Core.GestureOwnership: routing a release by who wants input
    // NOW is wrong in both directions, and the press already answered the question.
    private readonly GestureOwnership keysHeld = new();
    private readonly GestureOwnership buttonsHeld = new();

    // The application-visible half of input. GestureOwnership decides WHETHER a press reaches it;
    // this decides what the game sees when it looks. The two are deliberately separate: ownership
    // is about routing one gesture, and this is about holding still for the length of a tick.
    private readonly InputState inputState = new();

    // Whether this window currently has focus, which for a POLLED device is an eligibility
    // boundary rather than a nicety. See SampleGamepads.
    private bool windowFocused = true;

    /// <inheritdoc />
    public IInputState Input => inputState;

    // Lightweight perf HUD (F1): a debounced real-FPS readout drawn without the
    // DebugOverlayUi panels, so it measures actual frame rate at minimal cost.
    // FPS is averaged over a window (raw per-frame deltas are too jittery to read)
    // and refreshed a few times a second.
    private bool perfHudVisible;
    private double fpsAccumTime;
    private int fpsAccumFrames;
    private double fpsDisplay;
    private double frameMsDisplay;
    private const double FpsRefreshSeconds = 0.4;

    public Window(
        IGameLoop gameLoop,
        BlixWindowOptions? options = null,
        IRuntimeDiagnosticsSink? diagnostics = null)
    {
        // Validation errors are counted per process; this window answers for the ones after it began.
        validationErrorsBefore = VulkanGraphicsDevice.ValidationErrors;

        // --validate is BLIX_VK_VALIDATE=1 with a verdict attached. It has to be set before anything
        // reads the switch, which is read once for the process.
        if ((options ?? BlixWindowOptions.Default).Validate)
        {
            Environment.SetEnvironmentVariable("BLIX_VK_VALIDATE", "1");
            if (!RenderCommandDiagnostics.Enabled)
            {
                throw new InvalidOperationException(
                    "--validate was asked for after the validation switch had already been read as off. " +
                    "Construct the Window before recording any render commands.");
            }
        }

        // Resolve MoltenVK + libvulkan + validation layers before Silk's
        // first probe. No-op off macOS or when a LunarG SDK is already set up.
        MoltenVkBootstrap.EnsureLoaded();

        this.gameLoop = gameLoop;
        this.uiSource = gameLoop as IUiSource;
        this.diagnostics = diagnostics;

        if (gameLoop is IDebugContributor)
        {
            debugSystem = new DebugSystem();
            frameRecorder = new DiagnosticsFrameRecorder(debugSystem);
            debugSystem.AddSink(new ConsoleEventSink());
            // Periodic stdout digest of frame Values + Stats + Timers +
            // GPU pass timings. Default cadence: every 60 frames (~1s at
            // 60fps). Opt out with BLIX_DIAG=off; tune cadence with
            // BLIX_DIAG_INTERVAL=<frames>. Lives alongside ConsoleEventSink
            // (events → stderr) — these complement, not duplicate.
            if (Environment.GetEnvironmentVariable("BLIX_DIAG") != "off")
            {
                var intervalEnv = Environment.GetEnvironmentVariable("BLIX_DIAG_INTERVAL");
                var interval = int.TryParse(intervalEnv, out var n) && n > 0 ? n : 60;
                debugSystem.AddSink(new PeriodicConsoleSummarySink(interval));
            }
            jsonDumpSink = new JsonDumpSink();
            debugSystem.AddSink(jsonDumpSink);
            if ((options ?? BlixWindowOptions.Default).Diagnostics) debugSystem.State.ShowOverlay = true;

            // The keys this host answers to itself (OnKeyDown, OnMouseDown). They lead the key list, and
            // nothing may bind them: they would still fire, and both things would happen.
            debugSystem.DeclareHostKey("`", "show or hide the diagnostics overlay");
            debugSystem.DeclareHostKey("F1", "frame-rate readout, while the overlay is hidden");
            debugSystem.DeclareHostKey("F12", "dump this frame to dumps/frame-NNNNNN.json");
            debugSystem.DeclareHostKey("LeftAlt", "held, a click picks (with the overlay up)");
            debugSystem.DeclareHostKey("RightAlt", "held, a click picks (with the overlay up)");
        }

        var resolved = options ?? BlixWindowOptions.Default;
        this.options = resolved;
        var silkOptions = SilkWindowOptions.DefaultVulkan with
        {
            Title = resolved.Title,
            Size = new Vector2D<int>(resolved.Width, resolved.Height),
            VSync = true,
        };
        window = global::Silk.NET.Windowing.Window.Create(silkOptions);

        window.Load += OnLoad;
        window.Update += OnUpdate;
        window.Render += OnRender;
        window.Resize += OnResize;
        window.FramebufferResize += OnFramebufferResize;
        window.FocusChanged += OnFocusChanged;
    }

    // Called from OnLoad, not from the constructor: Silk refuses SetWindowIcon on a window it has
    // not initialised yet ("Window should be initialized"), and the constructor only creates it.
    private static void ApplyWindowIcon(IWindow window, BlixWindowOptions options)
    {
        var icons = options.Icons ?? BlixMark.WindowIcons();
        if (icons.Count == 0) return;

        try
        {
            // Windows and Linux: the title bar and the taskbar. Documented to do nothing on
            // macOS, where it returns without error rather than failing.
            window.SetWindowIcon(icons is RawImage[] array ? array : [.. icons]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An icon is decoration. A backend that refuses one must not stop an application.
            Console.Error.WriteLine($"[blix] window icon not applied: {ex.Message}");
        }

        // macOS has no window icon, so the line above is the whole story everywhere except the
        // one platform Blix is currently verified on. The dock tile is the icon there.
        if (OperatingSystem.IsMacOS())
        {
            var largest = options.Icons is null
                ? BlixMark.DockIcon()
                : icons.Aggregate((a, b) => b.Width > a.Width ? b : a);
            MacDockIcon.TrySet(largest);
        }
    }

    public void Run()
    {
        window.Run();
    }

    private void OnLoad()
    {
        ApplyWindowIcon(window, options);

        var vkSurface = window.VkSurface
            ?? throw new InvalidOperationException("Silk window did not provide a Vulkan surface. Was the window created with WindowOptions.DefaultVulkan?");
        var fb = window.FramebufferSize;
        var w = Math.Max(fb.X > 0 ? fb.X : window.Size.X, 1);
        var h = Math.Max(fb.Y > 0 ? fb.Y : window.Size.Y, 1);
        // An application that produces diagnostics gets a swapchain depth buffer that survives its
        // pass, so debug geometry drawn over the scene can be hidden by it. One without pays nothing.
        graphicsDevice = new VulkanGraphicsDevice(
            vkSurface, w, h, preserveSwapchainDepth: debugSystem is not null);
        Console.WriteLine($"Graphics: {graphicsDevice.Info.Vendor} | {graphicsDevice.Info.Renderer} | {graphicsDevice.Info.Version}");
        if (debugSystem is not null)
        {
            // VkLineDrawer translates debug.Draw.* commands into a Vulkan
            // line-pipeline draw on the OverlayRenderPass. Only allocate when
            // diagnostics are live (no IDebuggable game loop → no overlay).
            lineDrawer = new VkLineDrawer(graphicsDevice);

            // Every application gets the device's contributor; it used to be opt-in, and two did.
            debugSystem.Register(new GraphicsDeviceContributor(graphicsDevice));
            debugSystem.UseFrameTiming(graphicsDevice);
        }

        // <b>Built for anyone who wants a frame, not only for IDebuggable.</b> This used to live inside
        // the branch above, which is what made "does this application have an interface?" the same
        // question as "does it produce diagnostics?" — two unrelated things decided by one type test.
        if (debugSystem is not null || uiSource is not null)
        {
            imguiRenderer = new VkImGuiRenderer(graphicsDevice);
        }

        input = window.CreateInput();
        for (var i = 0; i < input.Keyboards.Count; i++)
        {
            input.Keyboards[i].KeyDown += OnKeyDown;
            input.Keyboards[i].KeyUp += OnKeyUp;
        }
        for (var i = 0; i < input.Mice.Count; i++)
        {
            input.Mice[i].MouseDown += OnMouseDown;
            input.Mice[i].MouseUp += OnMouseUp;
            input.Mice[i].MouseMove += OnMouseMove;
            input.Mice[i].Scroll += OnMouseScroll;
        }

        // Audio device construction can fail on machines without an OpenAL
        // backend installed. Catch + warn rather than aborting startup —
        // Game.AudioDevice stays null and audio-aware game code skips its
        // audio path via null-check.
        try
        {
            audioDevice = new OpenALAudioDevice();
            Console.WriteLine($"Audio: {audioDevice.Vendor} | {audioDevice.Renderer} | {audioDevice.Version}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Audio device unavailable: {ex.Message}");
        }

        ApplyDefaultSurfaceSize();
        gameLoop.OnLoad(this, graphicsDevice);

        // <b>The loop is a contributor like any other, registered rather than passed.</b> It used to be
        // handed to Run() each frame, which ran its Debug() and nothing else: a loop that was also
        // selectable, inspectable or a geometry source was silently ignored. Registered after OnLoad so
        // it runs where the passed loop did, after the device and anything the loop registered itself.
        debugSystem?.Register((IDebugContributor)gameLoop);
    }

    private void OnUpdate(double deltaTime)
    {
        totalTime += deltaTime;

        // Sampled before the flip, because a pad is state rather than a stream of events and the
        // flip is what turns state into transitions.
        SampleGamepads();

        // Exactly here, and exactly once. Everything the game reads is fixed from this line until
        // the next update, so rendering sees what the update before it saw, and a host running
        // several updates per rendered frame still reports a press on exactly one of them.
        inputState.BeginTick();

        gameLoop.OnUpdate(new Time(totalTime, deltaTime));
    }

    private void OnRender(double deltaTime)
    {
        if (graphicsDevice is null) return;

        // Debounced real FPS from the actual frame delta (wall clock), averaged
        // over a short window so the number is readable.
        fpsAccumTime += deltaTime;
        fpsAccumFrames++;
        if (fpsAccumTime >= FpsRefreshSeconds)
        {
            fpsDisplay = fpsAccumFrames / fpsAccumTime;
            frameMsDisplay = fpsAccumTime / fpsAccumFrames * 1000.0;
            fpsAccumTime = 0;
            fpsAccumFrames = 0;
        }

        var time = new Time(totalTime, deltaTime);
        var frame = CreateFrameContext();

        if (debugSystem is not null)
        {
            debugSystem.BeginFrame(frame, LogicalSize, Input);
            using (debugSystem.Current!.Timers.Measure("run-debuggables"))
            {
                debugSystem.Run();
            }
        }

        var commandList = new RenderCommandList(frameRecorder);
        using (debugSystem?.Current?.Timers.Measure("build-commands"))
        {
            gameLoop.OnRender(time, frame, commandList);
            AppendDebugLinesPass(commandList);
            AppendImGuiPass(commandList, frame, (float)deltaTime);
        }

        FrameDebugPacket packet;
        using (debugSystem?.Current?.Timers.Measure("execute"))
        {
            packet = graphicsDevice.Execute(commandList);
        }
        // Hand the per-pass/per-draw packet to the overlay's Pipeline tab.
        debugSystem?.SetFramePacket(packet);

        if (debugSystem?.Current is { } ctx)
        {
            var gpuTimings = graphicsDevice.ConsumeAvailableGpuTimings();
            for (var i = 0; i < gpuTimings.Count; i++)
            {
                var t = gpuTimings[i];
                ctx.Timers.AppendCompleted(t.PassName, scope: "gpu/passes", t.ElapsedMs);
            }
        }

        // Resource inventory: snapshot once and share with the overlay's
        // Resources tab and the runtime diagnostics sink. SnapshotResources
        // walks every table + allocates, so only do it when something will
        // read it — the overlay is visible, or a sink is attached. (The
        // overlay reads it via DebugSystem.LatestResourceSnapshot.)
        var overlayWantsResources = debugSystem?.State.ShowOverlay == true;
        if (overlayWantsResources || diagnostics is not null)
        {
            var resources = graphicsDevice.SnapshotResources();
            if (overlayWantsResources) debugSystem!.SetResourceSnapshot(resources);
            diagnostics?.OnFrameDebug(packet, resources);
        }

        // Present + buffer-swap lands here once swapchain integration is in
        // — Silk's IWindow.SwapBuffers is GL-specific, so the Vk path
        // calls vkQueuePresentKHR through the device. Until then this is a
        // no-op and the window stays unpainted.

        // ARMED BEFORE EndFrame, so the sink catches the frame being closed rather than the next one.
        // The counter is the one below, which has not been advanced yet — so --dump-frame 30 is the
        // thirtieth frame in the same counting --frames uses, and the two agree by construction
        // rather than by a comment asking the reader to add one.
        if (options.DumpOnFrame > 0 && renderedFrames + 1 == options.DumpOnFrame) jsonDumpSink?.RequestDump();

        debugSystem?.EndFrame();

        // <b>Bounded runs belong to the host.</b> Six applications counted their own frames and asked to
        // close; one (VulkanHello) never implemented it at all, so its launcher silently ignored --frames.
        // The host is the thing that knows what a frame is.
        renderedFrames++;
        if (options.ExitAfterFrames > 0 && renderedFrames >= options.ExitAfterFrames)
        {
            Console.WriteLine($"Exiting after {renderedFrames} frame(s) as asked.");
            window.Close();
        }
    }

    private void OnResize(Vector2D<int> size)
    {
        gameLoop.OnResize(size.X, size.Y);
    }

    private void OnFramebufferResize(Vector2D<int> size)
    {
        graphicsDevice?.SetDefaultRenderSurfaceSize(Math.Max(size.X, 1), Math.Max(size.Y, 1));
    }

    private void OnKeyDown(IKeyboard kbd, SilkKey key, int scancode)
    {
        // The runtime's own shortcuts OBSERVE, and do not consume. They still answer before any UI,
        // so a focused panel cannot swallow the dump key or the overlay toggle — the two things
        // most needed exactly when something has gone wrong. What changed is the `return` that
        // used to follow: while Blix could not even name F12, taking it was invisible. Now that a
        // game can bind Key.F12, quietly removing three keys from the keyboard would be a hole
        // nobody could see from inside their own code. Press it while a game binds it and both
        // things happen, which is surprising exactly once and findable immediately.
        if (key == SilkKey.F12) TryDumpCurrentFrame();
        if (key == SilkKey.F1) perfHudVisible = !perfHudVisible;
        if (key == SilkKey.GraveAccent && debugSystem is not null)
        {
            debugSystem.State.ShowOverlay = !debugSystem.State.ShowOverlay;
        }

        if (!keysHeld.Press((int)key, UiWantsKeyboard)) return;
        inputState.RecordKeyDown(MapKey(key));
    }

    private void OnKeyUp(IKeyboard kbd, SilkKey key, int scancode)
    {
        // Delivered when the application owned the press, and not otherwise — see GestureOwnership. A
        // release always reaching the application fixes the stranded-key case and creates its mirror: a
        // key the UI swallowed handing the application an up it never had a down for.
        if (!keysHeld.Release((int)key)) return;
        inputState.RecordKeyUp(MapKey(key));
    }

    // True once an ImGui frame has actually been built, which is when WantCapture* mean anything.
    private bool uiFrameBuilt;

    // <b>Whether the UI wants the pointer — any UI, not the diagnostics overlay specifically.</b> This
    // used to require debugSystem.State.ShowOverlay, so an application's own panels could be clicked
    // straight through into the game beneath them.
    private bool UiWantsMouse => imguiRenderer is { } r && uiFrameBuilt && r.WantCaptureMouse;

    // <b>Keyboard capture was defined and never once honoured.</b> VkImGuiRenderer has exposed
    // WantCaptureKeyboard since it was written and nothing read it, so typing into any ImGui text field
    // also drove the game — every keystroke arriving at both. Nothing had noticed because the only UI
    // that existed was the diagnostics overlay, which has almost no text fields.
    private bool UiWantsKeyboard => imguiRenderer is { } r && uiFrameBuilt && r.WantCaptureKeyboard;

    /// <summary>Forgets every in-flight press when the window loses focus.</summary>
    /// <remarks>
    /// <b>The releases are never coming.</b> Cmd-Tab away with a button or key down and the platform
    /// delivers the up event to whoever has focus now, not to us — so an application holding state on
    /// that press keeps holding it: a marquee that follows the cursor forever, a key the game believes
    /// is still down. Exactly the stranded-press bug <see cref="GestureOwnership"/> was built for, from
    /// the one direction it could not see.
    /// <para>
    /// <c>GestureOwnership.Clear</c> existed for this from the day it was written and nothing called it,
    /// because nothing hooked focus. A remedy with no caller is a remedy that has never run.
    /// </para>
    /// <para>
    /// The application IS handed synthetic releases, which is the opposite of what this said when
    /// only ownership was being cleared. Forgetting a press on the application's behalf leaves the
    /// application still holding it; the honest report is that everything was let go of, which is
    /// what physically happened as far as this window can ever know. The same rule covers a gamepad
    /// being unplugged, and for the same reason.
    /// </para>
    /// <para>
    /// Focus is also where a POLLED device differs from an event-driven one — see
    /// <c>SampleGamepads</c>, which stops feeding the pad in while the window is not focused.
    /// </para>
    /// </remarks>
    /// <summary>Makes a texture drawable inside a UI panel. See <see cref="IRenderHost"/>.</summary>
    /// <remarks>
    /// Returns 0 when there is no UI renderer — an application with no panels and no diagnostics
    /// never builds one. Zero is ImGui's own "no texture", so a panel that draws it gets the atlas
    /// fallback rather than a crash, which is the right shape for "there was nowhere to show this".
    /// </remarks>
    public nint RegisterUiTexture(TextureHandle texture) =>
        imguiRenderer?.RegisterTexture(texture) ?? 0;

    public void ReleaseUiTexture(nint id) => imguiRenderer?.ReleaseTexture(id);

    private void OnFocusChanged(bool focused)
    {
        windowFocused = focused;
        if (focused)
        {
            // Coming back, the pad is still holding whatever it was holding. That is not input.
            inputState.ResyncGamepads();
            return;
        }

        keysHeld.Clear();
        buttonsHeld.Clear();

        // And tell the application, rather than merely forgetting on its behalf. Clearing
        // ownership stops a stranded release being delivered; it does nothing about the game that
        // is still holding W. The tick after this reports the releases as though a person let go.
        inputState.ReleaseAll();
    }

    private void OnMouseDown(IMouse mouse, SilkMouseButton button)
    {
        // A pick is the debugger's gesture, so it is owned like a press the UI took: the game gets
        // neither the down nor, through GestureOwnership, the up. See DebugState.PickMode.
        if (button == SilkMouseButton.Left && !UiWantsMouse && PickArmed)
        {
            buttonsHeld.Press((int)button, uiWantsInput: true);
            debugSystem!.Pick(mouse.Position);
            return;
        }

        if (!buttonsHeld.Press((int)button, UiWantsMouse)) return;
        inputState.RecordMouseDown(MapMouseButton(button));
    }

    // The last frame's key list, one per line, for the F1 readout.
    private string? HudKeys() =>
        debugSystem?.LatestFrame?.Keys is { Count: > 0 } keys
            ? string.Join("\n", keys.Select(k => $"{k.Binding,-10} {k.Description}"))
            : null;

    // Armed from the Selection tab or by holding Alt, and only with the overlay up: the selection is
    // read there, and a hidden debugger taking clicks would be one nobody could see doing it.
    private bool PickArmed =>
        debugSystem is { State.ShowOverlay: true } debug
        && (debug.State.PickMode || AnyKeyDown(SilkKey.AltLeft, SilkKey.AltRight));

    private bool AnyKeyDown(params SilkKey[] keys)
    {
        if (input is null) return false;
        for (var k = 0; k < input.Keyboards.Count; k++)
        {
            foreach (var key in keys)
            {
                if (input.Keyboards[k].IsKeyPressed(key)) return true;
            }
        }

        return false;
    }

    private void OnMouseUp(IMouse mouse, SilkMouseButton button)
    {
        // <b>The release goes wherever the press went.</b> This was guarded on current UI capture once,
        // which dropped the release whenever the pointer happened to be over a panel when the button came
        // up — a drag begun in the world and finished over the panel left the game believing the button
        // was still down, so a marquee stayed live and followed a cursor that had long left it. Reported
        // from the chair as the mouse not lining up with the screen. Nothing about that looks like a
        // missing event.
        //
        // Then it was unconditional, which fixes that case and creates its reflection: a press the UI owned
        // still handing the application a release it never had a press for. Harmless if OnMouseUp only
        // clears a held set, and not harmless if it MEANS something — a shot loosed on release, a menu
        // opened. GestureOwnership settles it at the press, which is where it was always settled in the
        // reasoning.
        //
        // It costs the overlay nothing either way: ImGui never learned button state from these callbacks,
        // it polls IsButtonPressed in BeginFrame.
        if (!buttonsHeld.Release((int)button)) return;
        inputState.RecordMouseUp(MapMouseButton(button));
    }

    private void OnMouseMove(IMouse mouse, global::System.Numerics.Vector2 position)
    {
        // Both halves of eligibility in one place: a panel that captured the pointer, and a
        // window that is not focused. The difference between the places is the input layer's to
        // compute -- see InputState.RecordMousePosition for why it is not computed here.
        inputState.RecordMousePosition(position, eligible: !UiWantsMouse && windowFocused);
    }

    private void OnMouseScroll(IMouse mouse, ScrollWheel wheel)
    {
        // Feed the wheel to ImGui every time (consumed next BeginFrame); only
        // forward to the game when the overlay isn't capturing the mouse.
        lastWheel += wheel.Y;
        if (UiWantsMouse) return;
        inputState.RecordMouseWheel(new global::System.Numerics.Vector2(wheel.X, wheel.Y));
    }

    /// <summary>
    /// Read every pad's buttons and axes into the frame's input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sampled, not subscribed.</b> Silk offers ButtonDown/ButtonUp/ThumbstickMoved events as
    /// well, and using them would mean two models in one layer: buttons arriving as edges, axes
    /// arriving as... something, with a threshold nobody can justify deciding when a stick has
    /// moved enough to be worth an event. Reading the lot once per tick makes a pad exactly as
    /// analysable as a keyboard, and the transitions fall out of the flip like everything else.
    /// </para>
    /// <para>
    /// A pad Silk reports as disconnected is told to the state rather than dropped, because the
    /// difference matters: dropping it leaves a game holding a trigger that stopped existing.
    /// </para>
    /// </remarks>
    private void SampleGamepads()
    {
        if (input is null) return;

        // <b>Focus is an eligibility boundary, and only a polled device needs to be told.</b>
        // A keyboard and a mouse stop being eligible by themselves: the platform simply stops
        // sending their events to an unfocused window. A pad has no events to stop — it is read
        // every tick, whatever the window is doing — so releasing everything on focus loss and
        // then sampling again one tick later put the held button and the pulled trigger straight
        // back, and the game carried on driving while the player was in another application.
        // The invariant held inside InputState and the host defeated it on the way in.
        if (!windowFocused) return;   // NOT a guard clause to tidy away: H.3 asserts this read

        for (var i = 0; i < input.Gamepads.Count; i++)
        {
            var pad = input.Gamepads[i];
            if (!pad.IsConnected)
            {
                inputState.RecordGamepadDisconnected(pad.Index);
                continue;
            }

            inputState.RecordGamepadConnected(pad.Index, pad.Name ?? "");

            foreach (var button in pad.Buttons)
            {
                inputState.RecordGamepadButton(pad.Index, MapGamepadButton(button.Name), button.Pressed);
            }

            // Index, not name: Silk numbers thumbsticks and triggers rather than naming them, and
            // 0/1 is left/right on every pad it supports. A pad with more than two of either is
            // reported for the two this vocabulary has, and the rest is a question nobody has asked.
            foreach (var stick in pad.Thumbsticks)
            {
                if (stick.Index == 0)
                {
                    inputState.RecordGamepadAxis(pad.Index, BlixGamepadAxis.LeftX, stick.X);
                    inputState.RecordGamepadAxis(pad.Index, BlixGamepadAxis.LeftY, stick.Y);
                }
                else if (stick.Index == 1)
                {
                    inputState.RecordGamepadAxis(pad.Index, BlixGamepadAxis.RightX, stick.X);
                    inputState.RecordGamepadAxis(pad.Index, BlixGamepadAxis.RightY, stick.Y);
                }
            }

            foreach (var trigger in pad.Triggers)
            {
                if (trigger.Index == 0) inputState.RecordGamepadAxis(pad.Index, BlixGamepadAxis.LeftTrigger, Trigger01(trigger.Position));
                else if (trigger.Index == 1) inputState.RecordGamepadAxis(pad.Index, BlixGamepadAxis.RightTrigger, Trigger01(trigger.Position));
            }
        }
    }

    /// <summary>A trigger as GLFW reports it, to a trigger as Blix promises it.</summary>
    /// <remarks>
    /// <para>
    /// <b>GLFW gives all six gamepad axes in [-1, 1], triggers included, so an untouched trigger
    /// reads -1 and not 0.</b> Silk forwards the number untouched — checked by disassembling
    /// <c>GlfwGamepad.Update</c>, which contains no floating-point constants at all — so the
    /// conversion has to happen here.
    /// </para>
    /// <para>
    /// This is representation, not policy. Blix says a trigger is [0, 1]; a backend that measures
    /// the same physical thing on a different scale is exactly what a bridge is for. A deadzone
    /// would be the other kind of change and stays out.
    /// </para>
    /// <para>
    /// Left unconverted it is quietly wrong rather than obviously wrong: a resting controller
    /// reports full reverse on both triggers, and anything asking "is this pad being touched"
    /// answers yes forever.
    /// </para>
    /// </remarks>
    private static float Trigger01(float glfwAxis) => Math.Clamp((glfwAxis + 1f) * 0.5f, 0f, 1f);

    /// <summary>Silk's button names to Blix's, exhaustively. The suite checks that.</summary>
    private static BlixGamepadButton MapGamepadButton(ButtonName name) => name switch
    {
        ButtonName.A => BlixGamepadButton.A,
        ButtonName.B => BlixGamepadButton.B,
        ButtonName.X => BlixGamepadButton.X,
        ButtonName.Y => BlixGamepadButton.Y,
        ButtonName.LeftBumper => BlixGamepadButton.LeftBumper,
        ButtonName.RightBumper => BlixGamepadButton.RightBumper,
        ButtonName.Back => BlixGamepadButton.Back,
        ButtonName.Start => BlixGamepadButton.Start,
        ButtonName.Home => BlixGamepadButton.Home,
        ButtonName.LeftStick => BlixGamepadButton.LeftStick,
        ButtonName.RightStick => BlixGamepadButton.RightStick,
        ButtonName.DPadUp => BlixGamepadButton.DPadUp,
        ButtonName.DPadRight => BlixGamepadButton.DPadRight,
        ButtonName.DPadDown => BlixGamepadButton.DPadDown,
        ButtonName.DPadLeft => BlixGamepadButton.DPadLeft,
        _ => BlixGamepadButton.Unknown,
    };

    private bool TryDumpCurrentFrame()
    {
        if (debugSystem is null || jsonDumpSink is null) return false;
        if (debugSystem.FrozenFrame is { } frozen)
        {
            var path = jsonDumpSink.Dump(frozen);
            Console.WriteLine($"[diagnostics] dumped frozen frame {frozen.Number} -> {path}");
        }
        else
        {
            jsonDumpSink.RequestDump();
            Console.WriteLine("[diagnostics] dump armed; firing on next EndFrame");
        }
        return true;
    }

    private RenderFrameContext CreateFrameContext()
    {
        var size = window.FramebufferSize;
        var w = size.X > 0 ? size.X : window.Size.X;
        var h = size.Y > 0 ? size.Y : window.Size.Y;
        return new RenderFrameContext(Width: w, Height: h);
    }

    private void ApplyDefaultSurfaceSize()
    {
        var size = window.FramebufferSize;
        var w = size.X > 0 ? size.X : window.Size.X;
        var h = size.Y > 0 ? size.Y : window.Size.Y;
        graphicsDevice?.SetDefaultRenderSurfaceSize(Math.Max(w, 1), Math.Max(h, 1));
    }

    // IRenderHost
    public void SetTitle(string title) => window.Title = title;
    public void RequestClose() => window.Close();

    public void SetCursorCaptured(bool captured)
    {
        if (input is null) return;
        for (var i = 0; i < input.Mice.Count; i++)
        {
            input.Mice[i].Cursor.CursorMode = captured ? CursorMode.Raw : CursorMode.Normal;
        }
    }

    public (int Width, int Height) LogicalSize => (window.Size.X, window.Size.Y);

    public void SetVSync(bool enabled) => window.VSync = enabled;

    /// <summary>The refresh rate of the monitor this window is on, if the platform reports one.</summary>
    public int? DisplayRefreshHz => window.Monitor?.VideoMode.RefreshRate;

    /// <inheritdoc />
    /// <remarks>The device's own record, once there is a device; nothing before the first load.</remarks>
    public IFrameTiming Timing => (IFrameTiming?)graphicsDevice ?? FrameTimings.None;

    // IAudioHost facet. Returns the live OpenAL device. Throws if accessed
    // before OnLoad runs or when no audio backend is available (Game captures
    // it through `host as IAudioHost`, so a missing device surfaces there).
    public IAudioDevice AudioDevice =>
        audioDevice ?? throw new InvalidOperationException("Audio device is not initialised. OnLoad has not run yet, or no audio backend is available.");

    // IDebugHost
    public DebugContext? CurrentDebug => debugSystem?.Current;
    public DebugSystem? System => debugSystem;

    public void Dispose()
    {
        // Wait for in-flight frames to finish before tearing down the debug
        // renderers — they own pipelines/buffers the last frame may still reference,
        // and destroying those in-use trips validation. (graphicsDevice.Dispose
        // waits idle too, but only after these are already gone.)
        // <b>BLIX_TEARDOWN_TRACE names the step, because a SIGSEGV here names nothing.</b> An
        // intermittent crash in this method leaves a macOS report whose managed frames are
        // unsymbolised, and its only other evidence is which line of output came last — which is
        // "Exiting after N frames" for every step below alike. Off unless asked for; the cost of
        // asking is one environment variable, and the answer is the step's name.
        var trace = Environment.GetEnvironmentVariable("BLIX_TEARDOWN_TRACE") is { Length: > 0 };
        void Step(string name)
        {
            if (trace) Console.Error.WriteLine($"[teardown] {name}");
        }

        Step("wait-idle");
        graphicsDevice?.WaitIdle();

        // <b>OnUnload runs here, after the GPU is idle, and not when the window starts closing.</b>
        // Closing can fire before the final submit, so a loop that released its GPU resources in
        // OnUnload (the natural place) destroyed a pipeline, a buffer and a framebuffer the last
        // frame still used: Pong did, and validation said so at every exit. The headless host
        // also calls it last. Now the natural place is also the safe one.
        Step("unload");
        gameLoop.OnUnload();
        Step("imgui");
        imguiRenderer?.Dispose();
        Step("line-drawer");
        lineDrawer?.Dispose();
        // The game loop may own GPU resources outside the device's auto-freed
        // tables (e.g. a RenderGraph's render passes + offscreen images). Dispose
        // it here — after WaitIdle so the GPU is done with them, before the device
        // is torn down so the frees still have a live device.
        Step("game-loop");
        (gameLoop as IDisposable)?.Dispose();
        Step("audio");
        audioDevice?.Dispose();
        Step("input");
        input?.Dispose();
        Step("graphics-device");
        graphicsDevice?.Dispose();
        Step("window");
        window.Dispose();

        // Last, because the device's own teardown is where leaks are reported. Only the errors raised
        // since this window was made: two windows in one process, one after the other, each answer
        // for their own. Two at once would share the count, which the launcher never does.
        if (options.Validate && VulkanGraphicsDevice.ValidationErrors - validationErrorsBefore is > 0 and var errors)
        {
            BlixApps.ReportFailure($"{errors} Vulkan validation error(s); they are printed above as [vk-ERR ]");
        }

        Step("done");
    }

    // Walk the frame's accumulated debug.Draw.* commands, expand them into
    // line vertices on the VkLineDrawer, and append a swapchain pass that
    // submits them. The pass has empty ClearColors → the Vulkan backend
    // picks OverlayRenderPass (LoadOp.Load), so we draw OVER whatever the
    // game's pass(es) painted.
    private void AppendDebugLinesPass(RenderCommandList commandList)
    {
        if (debugSystem?.Current is not { } ctx) return;
        if (lineDrawer is null) return;
        var commands = ctx.Draw.Commands;
        if (commands.Count == 0) return;

        var views = ctx.Draw.Views;
        if (views.Count == 0) return;

        var state = debugSystem!.State;
        if (!state.ShowDebugDraw) return;
        var depthTested = state.DepthTestDrawing;

        // One buffer for the whole frame, one span per view. Cleared here because the ranged Submit below
        // deliberately does not reset — see VkLineDrawer.
        lineDrawer.Clear();

        for (var v = 0; v < views.Count; v++)
        {
            var view = views[v];
            var first = lineDrawer.VertexCount;

            for (var i = 0; i < commands.Count; i++)
            {
                var c = commands[i];
                if (c.View != view.Id || !state.ShouldDraw(c)) continue;
                switch (c)
                {
                    case DebugDrawLine d: lineDrawer.Line(d.A, d.B, d.Color); break;
                    case DebugDrawAabb d: lineDrawer.Aabb(d.Min, d.Max, d.Color); break;
                    case DebugDrawCross d: lineDrawer.Cross(d.Center, d.Size, d.Color); break;
                    case DebugDrawArrow d: lineDrawer.Arrow(d.From, d.To, d.Color); break;
                    case DebugDrawRay d:
                        var end = d.Origin + global::System.Numerics.Vector3.Normalize(d.Direction) * d.Length;
                        lineDrawer.Arrow(d.Origin, end, d.Color);
                        break;
                    case DebugDrawObb d: lineDrawer.Obb(d.Transform, d.Color); break;
                    case DebugDrawFrustum d: DrawFrustumLines(d.ViewProjection, d.Color); break;
                    case DebugDrawSphere d: DrawSphereLines(d.Center, d.Radius, d.Segments, d.Color); break;
                    case DebugDrawGrid d: DrawGridLines(d.Center, d.Size, d.Divisions, d.Color); break;
                    case DebugDrawPolyline d:
                        for (var p = 1; p < d.Points.Count; p++)
                        {
                            lineDrawer.Line(d.Points[p - 1], d.Points[p], d.Color);
                        }

                        break;
                    case DebugDrawPlane d: DrawPlaneLines(d.Center, d.Normal, d.Size, d.Color); break;
                    case DebugDrawCapsule d: DrawCapsuleLines(d.A, d.B, d.Radius, d.Segments, d.Color); break;
                    case DebugDrawCone d:
                        DrawConeLines(d.Apex, d.Axis, d.Length, d.HalfAngleRad, d.Segments, d.Color);
                        break;
                    case DebugDrawMeshWireframe d:
                        for (var e = 0; e + 1 < d.Edges.Count; e += 2)
                        {
                            int i0 = d.Edges[e], i1 = d.Edges[e + 1];
                            if ((uint)i0 >= d.Vertices.Count || (uint)i1 >= d.Vertices.Count) continue;
                            lineDrawer.Line(d.Vertices[i0], d.Vertices[i1], d.Color);
                        }

                        break;
                    case DebugDrawNormals d:
                        var pairs = Math.Min(d.Positions.Count, d.Normals.Count);
                        for (var n = 0; n < pairs; n++)
                        {
                            lineDrawer.Line(d.Positions[n], d.Positions[n] + d.Normals[n] * d.Length, d.Color);
                        }

                        break;

                    // <b>An unknown primitive is a bug, not a no-op.</b> Five commands — Plane,
                    // Capsule, Cone, MeshWireframe, Normals — sat in this switch for their whole
                    // lives as a comment saying "not implemented yet, silent skip rather than
                    // crash", so calling debug.Draw.Capsule() succeeded and drew nothing. A
                    // diagnostic that quietly does nothing is worse than one that does not exist:
                    // it answers a question wrongly. The same call was settled the same way when a
                    // primitive emitted outside a view was made to throw, which immediately found
                    // six producers drawing into nowhere.
                    default:
                        throw new NotSupportedException(
                            $"Debug primitive {c.GetType().Name} has no line expansion. Add one here — " +
                            "a debug command that draws nothing is a lie about what was asked.");
                }
            }

            var count = lineDrawer.VertexCount - first;
            if (count == 0) continue;

            // Each view lands on the surface it named. That one field is what makes an off-screen viewport
            // ordinary rather than special: the swapchain is just the view whose target is Default.
            var viewProj = view.ViewProjection;
            commandList.Pass(
                $"debug:{view.Name}",
                new RenderPassDescription(
                    Target: view.Target,
                    ClearColors: Array.Empty<GraphicsColor?>(),
                    ClearDepth: false,
                    // Debug geometry annotates a picture; it must never erase one. On the swapchain the
                    // empty clear list already selects the overlay pass; on an off-screen target this is
                    // what asks for the same thing.
                    LoadExisting: true),
                pass => lineDrawer.Submit(pass, viewProj, first, count, view.Target, depthTested));
        }
    }

    // <b>One ImGui frame, composed from whoever wants to be in it.</b> This was two mutually exclusive
    // paths — the diagnostics panels OR the perf HUD — each with its own copy of the IO setup, and an
    // application had no way into either. The frame is now built once and filled by everyone who has
    // something to draw, which is what lets an application's panels coexist with the diagnostics panels
    // instead of replacing them.
    private void AppendImGuiPass(RenderCommandList commandList, RenderFrameContext frame, float deltaTime)
    {
        uiFrameBuilt = false;
        if (imguiRenderer is null) return;

        var overlayUp = debugSystem is { State.ShowOverlay: true };
        var hudUp = perfHudVisible && !overlayUp;
        var appUi = uiSource;
        if (!overlayUp && !hudUp && appUi is null) return;

        var (logicalW, logicalH) = LogicalSize;
        var mousePos = global::System.Numerics.Vector2.Zero;
        bool left = false, right = false, middle = false;
        if (input is { Mice.Count: > 0 })
        {
            var m = input.Mice[0];
            mousePos = m.Position;
            left = m.IsButtonPressed(SilkMouseButton.Left);
            right = m.IsButtonPressed(SilkMouseButton.Right);
            middle = m.IsButtonPressed(SilkMouseButton.Middle);
        }

        var wheel = lastWheel;
        lastWheel = 0f;

        var hudText = $"{fpsDisplay:0} FPS  ({frameMsDisplay:0.0} ms)";
        imguiRenderer.BeginFrame(
            logicalW, logicalH, frame.Width, frame.Height, deltaTime,
            mousePos, left, right, middle, wheel,
            content: () =>
            {
                // The application first, then the engine's own panels. ImGui decides stacking itself, so
                // this is an ordering of construction rather than of depth — but it keeps a misbehaving
                // application from being able to prevent the diagnostics panels being built at all.
                appUi?.DrawUi();
                if (overlayUp) imguiRenderer.LayoutDiagnostics(debugSystem!);
                else if (hudUp) imguiRenderer.DrawPerfHudText(hudText, HudKeys());
            });

        // Only now do WantCaptureMouse / WantCaptureKeyboard describe anything real.
        uiFrameBuilt = true;

        commandList.Pass(
            "imgui",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: Array.Empty<GraphicsColor?>(),
                ClearDepth: false),
            pass => imguiRenderer.Submit(pass));
    }

    // Expand a view-projection into its 8 frustum corners (inverse-VP applied
    // to the NDC cube; Vulkan z ∈ [0,1]) and draw the 12 edges. The canonical
    // gizmo for inspecting a shadow camera's covered volume.
    private void DrawFrustumLines(global::System.Numerics.Matrix4x4 viewProj, GraphicsColor color)
    {
        if (!global::System.Numerics.Matrix4x4.Invert(viewProj, out var inv)) return;
        // NDC cube corners: x,y ∈ [-1,1], z ∈ [0,1]. Index bit 0=x,1=y,2=z(near/far).
        global::System.Numerics.Vector3 Corner(float x, float y, float z)
        {
            var p = global::System.Numerics.Vector4.Transform(new global::System.Numerics.Vector4(x, y, z, 1f), inv);
            return new global::System.Numerics.Vector3(p.X, p.Y, p.Z) / p.W;
        }
        var c = new global::System.Numerics.Vector3[8];
        var i = 0;
        for (var zi = 0; zi < 2; zi++)
        for (var yi = 0; yi < 2; yi++)
        for (var xi = 0; xi < 2; xi++)
            c[i++] = Corner(xi == 0 ? -1f : 1f, yi == 0 ? -1f : 1f, zi == 0 ? 0f : 1f);
        // Near quad (z=0): 0,1,3,2  Far quad (z=1): 4,5,7,6  Connectors.
        void E(int a, int b) => lineDrawer!.Line(c[a], c[b], color);
        E(0, 1); E(1, 3); E(3, 2); E(2, 0);   // near
        E(4, 5); E(5, 7); E(7, 6); E(6, 4);   // far
        E(0, 4); E(1, 5); E(2, 6); E(3, 7);   // connectors
    }

    // Three axis-aligned rings approximating a wireframe sphere.
    private void DrawSphereLines(global::System.Numerics.Vector3 center, float radius, int segments, GraphicsColor color)
    {
        if (segments < 3) segments = 3;
        var step = MathF.PI * 2f / segments;
        for (var s = 0; s < segments; s++)
        {
            var a = s * step;
            var b = (s + 1) * step;
            var (ca, sa) = (MathF.Cos(a) * radius, MathF.Sin(a) * radius);
            var (cb, sb) = (MathF.Cos(b) * radius, MathF.Sin(b) * radius);
            // XY, XZ, YZ rings.
            lineDrawer!.Line(center + new global::System.Numerics.Vector3(ca, sa, 0), center + new global::System.Numerics.Vector3(cb, sb, 0), color);
            lineDrawer!.Line(center + new global::System.Numerics.Vector3(ca, 0, sa), center + new global::System.Numerics.Vector3(cb, 0, sb), color);
            lineDrawer!.Line(center + new global::System.Numerics.Vector3(0, ca, sa), center + new global::System.Numerics.Vector3(0, cb, sb), color);
        }
    }

    // A square patch of the plane, plus its normal — enough to read orientation, which is
    // the thing a plane is usually being drawn to check.
    private void DrawPlaneLines(
        global::System.Numerics.Vector3 center, global::System.Numerics.Vector3 normal, float size, GraphicsColor color)
    {
        var n = normal.LengthSquared() > 1e-8f
            ? global::System.Numerics.Vector3.Normalize(normal)
            : global::System.Numerics.Vector3.UnitY;
        var (u, v) = Basis(n);
        var h = size * 0.5f;
        var a = center + (u * h) + (v * h);
        var b = center - (u * h) + (v * h);
        var cc = center - (u * h) - (v * h);
        var d = center + (u * h) - (v * h);
        lineDrawer!.Line(a, b, color);
        lineDrawer.Line(b, cc, color);
        lineDrawer.Line(cc, d, color);
        lineDrawer.Line(d, a, color);
        lineDrawer.Arrow(center, center + (n * (size * 0.35f)), color);
    }

    // Two end caps joined by side lines. The caps are rings in the plane perpendicular to
    // the axis plus two arcs over the ends, which reads as a capsule rather than as two
    // circles — the difference matters when what is being checked is a character collider.
    private void DrawCapsuleLines(
        global::System.Numerics.Vector3 a, global::System.Numerics.Vector3 b, float radius, int segments, GraphicsColor color)
    {
        if (segments < 4) segments = 4;
        var axis = b - a;
        var length = axis.Length();
        var n = length > 1e-6f ? axis / length : global::System.Numerics.Vector3.UnitY;
        var (u, v) = Basis(n);
        var step = MathF.PI * 2f / segments;

        for (var s = 0; s < segments; s++)
        {
            var t0 = s * step;
            var t1 = (s + 1) * step;
            var r0 = (u * (MathF.Cos(t0) * radius)) + (v * (MathF.Sin(t0) * radius));
            var r1 = (u * (MathF.Cos(t1) * radius)) + (v * (MathF.Sin(t1) * radius));
            lineDrawer!.Line(a + r0, a + r1, color);   // end rings
            lineDrawer.Line(b + r0, b + r1, color);
        }

        // Four side lines, and four arcs per cap through the poles.
        for (var q = 0; q < 4; q++)
        {
            var t = q * MathF.PI * 0.5f;
            var r = (u * (MathF.Cos(t) * radius)) + (v * (MathF.Sin(t) * radius));
            lineDrawer!.Line(a + r, b + r, color);

            var arc = Math.Max(3, segments / 4);
            for (var k = 0; k < arc; k++)
            {
                var p0 = k / (float)arc * MathF.PI * 0.5f;
                var p1 = (k + 1) / (float)arc * MathF.PI * 0.5f;
                var dir0 = (r * MathF.Cos(p0)) - (n * (radius * MathF.Sin(p0)));
                var dir1 = (r * MathF.Cos(p1)) - (n * (radius * MathF.Sin(p1)));
                lineDrawer.Line(a + dir0, a + dir1, color);
                lineDrawer.Line(b - dir0, b - dir1, color);
            }
        }
    }

    // Apex, base ring, and side lines. Half-angle rather than a base radius because that is
    // how a spotlight, a view cone and a field of view are all described.
    private void DrawConeLines(
        global::System.Numerics.Vector3 apex,
        global::System.Numerics.Vector3 axis,
        float length,
        float halfAngleRad,
        int segments,
        GraphicsColor color)
    {
        if (segments < 3) segments = 3;
        var n = axis.LengthSquared() > 1e-8f
            ? global::System.Numerics.Vector3.Normalize(axis)
            : global::System.Numerics.Vector3.UnitZ;
        var (u, v) = Basis(n);
        var baseCentre = apex + (n * length);
        var radius = MathF.Tan(Math.Clamp(halfAngleRad, 0.001f, 1.55f)) * length;
        var step = MathF.PI * 2f / segments;

        for (var s = 0; s < segments; s++)
        {
            var t0 = s * step;
            var t1 = (s + 1) * step;
            var p0 = baseCentre + (u * (MathF.Cos(t0) * radius)) + (v * (MathF.Sin(t0) * radius));
            var p1 = baseCentre + (u * (MathF.Cos(t1) * radius)) + (v * (MathF.Sin(t1) * radius));
            lineDrawer!.Line(p0, p1, color);
            if (s % Math.Max(1, segments / 4) == 0) lineDrawer.Line(apex, p0, color);
        }
    }

    // Any two axes perpendicular to n. Picking the smaller component to cross against keeps
    // the result well-conditioned when n is near an axis.
    private static (global::System.Numerics.Vector3 U, global::System.Numerics.Vector3 V) Basis(
        global::System.Numerics.Vector3 n)
    {
        var reference = MathF.Abs(n.Y) < 0.9f
            ? global::System.Numerics.Vector3.UnitY
            : global::System.Numerics.Vector3.UnitX;
        var u = global::System.Numerics.Vector3.Normalize(global::System.Numerics.Vector3.Cross(reference, n));
        var v = global::System.Numerics.Vector3.Cross(n, u);
        return (u, v);
    }

    // Flat grid of lines on the XZ plane at center.Y.
    private void DrawGridLines(global::System.Numerics.Vector3 center, float size, int divisions, GraphicsColor color)
    {
        if (divisions < 1) divisions = 1;
        var half = size * 0.5f;
        var step = size / divisions;
        for (var k = 0; k <= divisions; k++)
        {
            var off = -half + k * step;
            lineDrawer!.Line(center + new global::System.Numerics.Vector3(off, 0, -half), center + new global::System.Numerics.Vector3(off, 0, half), color);
            lineDrawer!.Line(center + new global::System.Numerics.Vector3(-half, 0, off), center + new global::System.Numerics.Vector3(half, 0, off), color);
        }
    }

    /// <summary>
    /// Silk's keyboard vocabulary to Blix's, exhaustively.
    /// </summary>
    /// <remarks>
    /// <b>Every key Silk can report has a value here, and the suite checks that.</b> A missing arm
    /// falls to <see cref="BlixKey.Unknown"/>, which is indistinguishable from a key the hardware
    /// does not have — so an incomplete map is not a gap a consumer can see, it is a key that does
    /// nothing for a reason nobody can find.
    ///
    /// Blix names its modifiers LeftX where Silk names them XLeft; that is a house style predating
    /// this and not worth churning consumers over.
    /// </remarks>
    private static BlixKey MapKey(SilkKey key) => key switch
    {
        SilkKey.Escape => BlixKey.Escape,
        SilkKey.Space => BlixKey.Space,
        SilkKey.Enter => BlixKey.Enter,
        SilkKey.Tab => BlixKey.Tab,
        SilkKey.Backspace => BlixKey.Backspace,
        SilkKey.Left => BlixKey.Left,
        SilkKey.Right => BlixKey.Right,
        SilkKey.Up => BlixKey.Up,
        SilkKey.Down => BlixKey.Down,
        SilkKey.A => BlixKey.A,
        SilkKey.B => BlixKey.B,
        SilkKey.C => BlixKey.C,
        SilkKey.D => BlixKey.D,
        SilkKey.E => BlixKey.E,
        SilkKey.F => BlixKey.F,
        SilkKey.G => BlixKey.G,
        SilkKey.H => BlixKey.H,
        SilkKey.I => BlixKey.I,
        SilkKey.J => BlixKey.J,
        SilkKey.K => BlixKey.K,
        SilkKey.L => BlixKey.L,
        SilkKey.M => BlixKey.M,
        SilkKey.N => BlixKey.N,
        SilkKey.O => BlixKey.O,
        SilkKey.P => BlixKey.P,
        SilkKey.Q => BlixKey.Q,
        SilkKey.R => BlixKey.R,
        SilkKey.S => BlixKey.S,
        SilkKey.T => BlixKey.T,
        SilkKey.U => BlixKey.U,
        SilkKey.V => BlixKey.V,
        SilkKey.W => BlixKey.W,
        SilkKey.X => BlixKey.X,
        SilkKey.Y => BlixKey.Y,
        SilkKey.Z => BlixKey.Z,
        SilkKey.Number0 => BlixKey.Number0,
        SilkKey.Number1 => BlixKey.Number1,
        SilkKey.Number2 => BlixKey.Number2,
        SilkKey.Number3 => BlixKey.Number3,
        SilkKey.Number4 => BlixKey.Number4,
        SilkKey.Number5 => BlixKey.Number5,
        SilkKey.Number6 => BlixKey.Number6,
        SilkKey.Number7 => BlixKey.Number7,
        SilkKey.Number8 => BlixKey.Number8,
        SilkKey.Number9 => BlixKey.Number9,
        SilkKey.ControlLeft => BlixKey.LeftControl,
        SilkKey.ControlRight => BlixKey.RightControl,
        SilkKey.SuperLeft => BlixKey.LeftSuper,
        SilkKey.SuperRight => BlixKey.RightSuper,
        SilkKey.ShiftLeft => BlixKey.LeftShift,
        SilkKey.ShiftRight => BlixKey.RightShift,
        SilkKey.AltLeft => BlixKey.LeftAlt,
        SilkKey.AltRight => BlixKey.RightAlt,
        SilkKey.Menu => BlixKey.Menu,

        SilkKey.Insert => BlixKey.Insert,
        SilkKey.Delete => BlixKey.Delete,
        SilkKey.Home => BlixKey.Home,
        SilkKey.End => BlixKey.End,
        SilkKey.PageUp => BlixKey.PageUp,
        SilkKey.PageDown => BlixKey.PageDown,

        SilkKey.CapsLock => BlixKey.CapsLock,
        SilkKey.ScrollLock => BlixKey.ScrollLock,
        SilkKey.NumLock => BlixKey.NumLock,
        SilkKey.PrintScreen => BlixKey.PrintScreen,
        SilkKey.Pause => BlixKey.Pause,

        SilkKey.Apostrophe => BlixKey.Apostrophe,
        SilkKey.Comma => BlixKey.Comma,
        SilkKey.Minus => BlixKey.Minus,
        SilkKey.Period => BlixKey.Period,
        SilkKey.Slash => BlixKey.Slash,
        SilkKey.Semicolon => BlixKey.Semicolon,
        SilkKey.Equal => BlixKey.Equal,
        SilkKey.LeftBracket => BlixKey.LeftBracket,
        SilkKey.BackSlash => BlixKey.BackSlash,
        SilkKey.RightBracket => BlixKey.RightBracket,
        SilkKey.GraveAccent => BlixKey.GraveAccent,
        SilkKey.World1 => BlixKey.World1,
        SilkKey.World2 => BlixKey.World2,

        SilkKey.F1 => BlixKey.F1,
        SilkKey.F2 => BlixKey.F2,
        SilkKey.F3 => BlixKey.F3,
        SilkKey.F4 => BlixKey.F4,
        SilkKey.F5 => BlixKey.F5,
        SilkKey.F6 => BlixKey.F6,
        SilkKey.F7 => BlixKey.F7,
        SilkKey.F8 => BlixKey.F8,
        SilkKey.F9 => BlixKey.F9,
        SilkKey.F10 => BlixKey.F10,
        SilkKey.F11 => BlixKey.F11,
        SilkKey.F12 => BlixKey.F12,
        SilkKey.F13 => BlixKey.F13,
        SilkKey.F14 => BlixKey.F14,
        SilkKey.F15 => BlixKey.F15,
        SilkKey.F16 => BlixKey.F16,
        SilkKey.F17 => BlixKey.F17,
        SilkKey.F18 => BlixKey.F18,
        SilkKey.F19 => BlixKey.F19,
        SilkKey.F20 => BlixKey.F20,
        SilkKey.F21 => BlixKey.F21,
        SilkKey.F22 => BlixKey.F22,
        SilkKey.F23 => BlixKey.F23,
        SilkKey.F24 => BlixKey.F24,
        SilkKey.F25 => BlixKey.F25,

        SilkKey.Keypad0 => BlixKey.Keypad0,
        SilkKey.Keypad1 => BlixKey.Keypad1,
        SilkKey.Keypad2 => BlixKey.Keypad2,
        SilkKey.Keypad3 => BlixKey.Keypad3,
        SilkKey.Keypad4 => BlixKey.Keypad4,
        SilkKey.Keypad5 => BlixKey.Keypad5,
        SilkKey.Keypad6 => BlixKey.Keypad6,
        SilkKey.Keypad7 => BlixKey.Keypad7,
        SilkKey.Keypad8 => BlixKey.Keypad8,
        SilkKey.Keypad9 => BlixKey.Keypad9,
        SilkKey.KeypadDecimal => BlixKey.KeypadDecimal,
        SilkKey.KeypadDivide => BlixKey.KeypadDivide,
        SilkKey.KeypadMultiply => BlixKey.KeypadMultiply,
        SilkKey.KeypadSubtract => BlixKey.KeypadSubtract,
        SilkKey.KeypadAdd => BlixKey.KeypadAdd,
        SilkKey.KeypadEnter => BlixKey.KeypadEnter,
        SilkKey.KeypadEqual => BlixKey.KeypadEqual,

        _ => BlixKey.Unknown,
    };

    private static BlixMouseButton MapMouseButton(SilkMouseButton button) => button switch
    {
        SilkMouseButton.Left => BlixMouseButton.Left,
        SilkMouseButton.Right => BlixMouseButton.Right,
        SilkMouseButton.Middle => BlixMouseButton.Middle,
        _ => BlixMouseButton.Unknown,
    };
}
