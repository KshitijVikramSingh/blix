using Blix.Audio;
using Blix.Audio.OpenAL;
using Blix.Core;
using Blix.Diagnostics;
using Blix;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SilkWindowOptions = Silk.NET.Windowing.WindowOptions;
using BlixWindowOptions = Blix.Runtime.Silk.WindowOptions;
using BlixKey = Blix.Core.Key;
using BlixMouseButton = Blix.Core.MouseButton;
using SilkKey = Silk.NET.Input.Key;
using SilkMouseButton = Silk.NET.Input.MouseButton;

namespace Blix.Runtime.Silk;

// The Vulkan window/runtime adapter. Owns a Silk.NET window in
// "Vulkan API" mode, pumps the IGameLoop, and wires the diagnostics surface.
//
// Scope today: window opens, loop ticks, IDebuggable producers run,
// console + JSON dump sinks fire. Vulkan instance/surface/swapchain
// integration lands in the next push — Execute() currently records
// FrameDebugPacket metadata only and the window has no pixels to
// present yet. The whole point of this turn is proving the shape
// of the surface lines up; visible rendering comes after.
public sealed class Window : IRenderHost, IAudioHost, IDebugHost, IDisposable
{
    private readonly IWindow window;
    private readonly IGameLoop gameLoop;
    private readonly IInputHandler? inputHandler;
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
    private int renderedFrames;

    // Who owns each in-flight press. See Blix.Core.GestureOwnership: routing a release by who wants input
    // NOW is wrong in both directions, and the press already answered the question.
    private readonly GestureOwnership keysHeld = new();
    private readonly GestureOwnership buttonsHeld = new();

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
        // Resolve MoltenVK + libvulkan + validation layers before Silk's
        // first probe. No-op off macOS or when a LunarG SDK is already set up.
        MoltenVkBootstrap.EnsureLoaded();

        this.gameLoop = gameLoop;
        this.inputHandler = gameLoop as IInputHandler;
        this.uiSource = gameLoop as IUiSource;
        this.diagnostics = diagnostics;

        if (gameLoop is IDebuggable)
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
            if ((options ?? BlixWindowOptions.Default).Diagnostics) debugSystem.State.Enabled = true;
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
        window.Closing += OnClosing;
    }

    public void Run()
    {
        window.Run();
    }

    private void OnLoad()
    {
        var vkSurface = window.VkSurface
            ?? throw new InvalidOperationException("Silk window did not provide a Vulkan surface. Was the window created with WindowOptions.DefaultVulkan?");
        var fb = window.FramebufferSize;
        var w = Math.Max(fb.X > 0 ? fb.X : window.Size.X, 1);
        var h = Math.Max(fb.Y > 0 ? fb.Y : window.Size.Y, 1);
        graphicsDevice = new VulkanGraphicsDevice(vkSurface, w, h);
        Console.WriteLine($"Graphics: {graphicsDevice.Info.Vendor} | {graphicsDevice.Info.Renderer} | {graphicsDevice.Info.Version}");
        if (debugSystem is not null)
        {
            // VkLineDrawer translates debug.Draw.* commands into a Vulkan
            // line-pipeline draw on the OverlayRenderPass. Only allocate when
            // diagnostics are live (no IDebuggable game loop → no overlay).
            lineDrawer = new VkLineDrawer(graphicsDevice);
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
    }

    private void OnUpdate(double deltaTime)
    {
        totalTime += deltaTime;
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

        if (debugSystem is not null && gameLoop is IDebuggable debuggable)
        {
            debugSystem.BeginFrame(frame);
            using (debugSystem.Current!.Timers.Measure("run-debuggables"))
            {
                debugSystem.Run(debuggable);
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

    private void OnClosing()
    {
        gameLoop.OnUnload();
    }

    private void OnKeyDown(IKeyboard kbd, SilkKey key, int scancode)
    {
        // The runtime's own bindings answer first, so a UI with focus cannot swallow the dump key or the
        // overlay toggle — the two things most needed exactly when something has gone wrong.
        if (key == SilkKey.F12 && TryDumpCurrentFrame()) return;
        if (key == SilkKey.F1)
        {
            perfHudVisible = !perfHudVisible;
            return;
        }
        if (key == SilkKey.GraveAccent && debugSystem is not null)
        {
            debugSystem.State.ShowOverlay = !debugSystem.State.ShowOverlay;
            return;
        }
        if (!keysHeld.Press((int)key, UiWantsKeyboard)) return;
        inputHandler?.OnKeyDown(MapKey(key));
    }

    private void OnKeyUp(IKeyboard kbd, SilkKey key, int scancode)
    {
        // Delivered when the application owned the press, and not otherwise — see GestureOwnership. A
        // release always reaching the application fixes the stranded-key case and creates its mirror: a
        // key the UI swallowed handing the application an up it never had a down for.
        if (!keysHeld.Release((int)key)) return;
        inputHandler?.OnKeyUp(MapKey(key));
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

    private void OnMouseDown(IMouse mouse, SilkMouseButton button)
    {
        if (!buttonsHeld.Press((int)button, UiWantsMouse)) return;
        inputHandler?.OnMouseDown(MapMouseButton(button));
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
        inputHandler?.OnMouseUp(MapMouseButton(button));
    }

    private global::System.Numerics.Vector2 lastMousePosition;

    private void OnMouseMove(IMouse mouse, global::System.Numerics.Vector2 position)
    {
        var delta = position - lastMousePosition;
        lastMousePosition = position;
        if (UiWantsMouse) return;
        inputHandler?.OnMouseMove(position.X, position.Y, delta.X, delta.Y);
    }

    private void OnMouseScroll(IMouse mouse, ScrollWheel wheel)
    {
        // Feed the wheel to ImGui every time (consumed next BeginFrame); only
        // forward to the game when the overlay isn't capturing the mouse.
        lastWheel += wheel.Y;
        if (UiWantsMouse) return;
        inputHandler?.OnMouseWheel(wheel.X, wheel.Y);
    }

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
        graphicsDevice?.WaitIdle();
        imguiRenderer?.Dispose();
        lineDrawer?.Dispose();
        // The game loop may own GPU resources outside the device's auto-freed
        // tables (e.g. a RenderGraph's render passes + offscreen images). Dispose
        // it here — after WaitIdle so the GPU is done with them, before the device
        // is torn down so the frees still have a live device. Closing/OnUnload is
        // too early: it can fire mid-frame before the final submit.
        (gameLoop as IDisposable)?.Dispose();
        audioDevice?.Dispose();
        input?.Dispose();
        graphicsDevice?.Dispose();
        window.Dispose();
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

        var depthTested = debugSystem?.State.DepthTestDrawing ?? true;

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
                if (c.View != view.Id) continue;
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

        var overlayUp = debugSystem is { State.Enabled: true, State.ShowOverlay: true };
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
                else if (hudUp) imguiRenderer.DrawPerfHudText(hudText);
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
        SilkKey.A => BlixKey.A, SilkKey.B => BlixKey.B, SilkKey.C => BlixKey.C, SilkKey.D => BlixKey.D,
        SilkKey.E => BlixKey.E, SilkKey.F => BlixKey.F, SilkKey.G => BlixKey.G, SilkKey.H => BlixKey.H,
        SilkKey.I => BlixKey.I, SilkKey.J => BlixKey.J, SilkKey.K => BlixKey.K, SilkKey.L => BlixKey.L,
        SilkKey.M => BlixKey.M, SilkKey.N => BlixKey.N, SilkKey.O => BlixKey.O, SilkKey.P => BlixKey.P,
        SilkKey.Q => BlixKey.Q, SilkKey.R => BlixKey.R, SilkKey.S => BlixKey.S, SilkKey.T => BlixKey.T,
        SilkKey.U => BlixKey.U, SilkKey.V => BlixKey.V, SilkKey.W => BlixKey.W, SilkKey.X => BlixKey.X,
        SilkKey.Y => BlixKey.Y, SilkKey.Z => BlixKey.Z,
        SilkKey.ControlLeft => BlixKey.LeftControl,
        SilkKey.ControlRight => BlixKey.RightControl,
        SilkKey.SuperLeft => BlixKey.LeftSuper,
        SilkKey.SuperRight => BlixKey.RightSuper,
        SilkKey.ShiftLeft => BlixKey.LeftShift,
        SilkKey.ShiftRight => BlixKey.RightShift,
        // Both rows to the same value. Which physical key produced a digit is a fact about the keyboard,
        // and no caller has ever wanted it: a control group is bound to "4", not to "the 4 above the R".
        SilkKey.Number0 or SilkKey.Keypad0 => BlixKey.Number0,
        SilkKey.Number1 or SilkKey.Keypad1 => BlixKey.Number1,
        SilkKey.Number2 or SilkKey.Keypad2 => BlixKey.Number2,
        SilkKey.Number3 or SilkKey.Keypad3 => BlixKey.Number3,
        SilkKey.Number4 or SilkKey.Keypad4 => BlixKey.Number4,
        SilkKey.Number5 or SilkKey.Keypad5 => BlixKey.Number5,
        SilkKey.Number6 or SilkKey.Keypad6 => BlixKey.Number6,
        SilkKey.Number7 or SilkKey.Keypad7 => BlixKey.Number7,
        SilkKey.Number8 or SilkKey.Keypad8 => BlixKey.Number8,
        SilkKey.Number9 or SilkKey.Keypad9 => BlixKey.Number9,
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
