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

// Sibling to Blix.Runtime.OpenTK.Window. Owns a Silk.NET window in
// "Vulkan API" mode, pumps the IGameLoop, and wires the diagnostics
// surface so it stays identical across backends.
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
        }

        var resolved = options ?? BlixWindowOptions.Default;
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
            // VkImGuiRenderer draws the on-screen diagnostics panels (same
            // DebugOverlayUi the GL backend uses). Toggle with the ` key.
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

        if (diagnostics is { } sink)
        {
            sink.OnFrameDebug(packet, graphicsDevice.SnapshotResources());
        }

        // Present + buffer-swap lands here once swapchain integration is in
        // — Silk's IWindow.SwapBuffers is GL-specific, so the Vk path
        // calls vkQueuePresentKHR through the device. Until then this is a
        // no-op and the window stays unpainted.

        debugSystem?.EndFrame();
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
        inputHandler?.OnKeyDown(MapKey(key));
    }

    private void OnKeyUp(IKeyboard kbd, SilkKey key, int scancode)
    {
        inputHandler?.OnKeyUp(MapKey(key));
    }

    // True when the diagnostics overlay is up and ImGui is hovering/dragging a
    // panel — mouse input then drives the UI, not the game (so opening a panel
    // doesn't also swing the camera).
    private bool OverlayWantsMouse =>
        imguiRenderer is { } r &&
        debugSystem is { State.Enabled: true, State.ShowOverlay: true } &&
        r.WantCaptureMouse;

    private void OnMouseDown(IMouse mouse, SilkMouseButton button)
    {
        if (OverlayWantsMouse) return;
        inputHandler?.OnMouseDown(MapMouseButton(button));
    }

    private void OnMouseUp(IMouse mouse, SilkMouseButton button)
    {
        if (OverlayWantsMouse) return;
        inputHandler?.OnMouseUp(MapMouseButton(button));
    }

    private global::System.Numerics.Vector2 lastMousePosition;

    private void OnMouseMove(IMouse mouse, global::System.Numerics.Vector2 position)
    {
        var delta = position - lastMousePosition;
        lastMousePosition = position;
        if (OverlayWantsMouse) return;
        inputHandler?.OnMouseMove(position.X, position.Y, delta.X, delta.Y);
    }

    private void OnMouseScroll(IMouse mouse, ScrollWheel wheel)
    {
        // Feed the wheel to ImGui every time (consumed next BeginFrame); only
        // forward to the game when the overlay isn't capturing the mouse.
        lastWheel += wheel.Y;
        if (OverlayWantsMouse) return;
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
        imguiRenderer?.Dispose();
        lineDrawer?.Dispose();
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

        for (var i = 0; i < commands.Count; i++)
        {
            var c = commands[i];
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
                // Plane / Capsule / Cone / MeshWireframe / Normals not
                // implemented yet — silent skip rather than crash.
            }
        }

        if (!lineDrawer.HasLines) return;

        // Footgun guard: emit-once warning when commands were issued but the
        // game forgot to set debug.Draw.ViewProjection. Without this, lines
        // render in clip space and are almost always invisible.
        if (!warnedDebugIdentityVp && ctx.Draw.ViewProjection.Equals(global::System.Numerics.Matrix4x4.Identity))
        {
            warnedDebugIdentityVp = true;
            Console.Error.WriteLine("[diagnostics] debug.Draw.ViewProjection is Identity; lines will render in clip space (likely invisible). Set debug.Draw.ViewProjection = viewProj.");
        }

        var viewProj = ctx.Draw.ViewProjection;
        commandList.Pass(
            "debug",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: Array.Empty<GraphicsColor?>(),
                ClearDepth: false),
            pass => lineDrawer.Submit(pass, viewProj));
    }

    // Build the ImGui diagnostics panels for this frame and append a swapchain
    // overlay pass that draws them on top of the scene. Gated on the overlay
    // toggle (` key) so the panels only render when asked for. Mirrors the GL
    // backend's overlay hook; the panel content comes from the shared
    // DebugOverlayUi inside VkImGuiRenderer.
    private void AppendImGuiPass(RenderCommandList commandList, RenderFrameContext frame, float deltaTime)
    {
        if (imguiRenderer is null) return;
        var overlayUp = debugSystem is { State.Enabled: true, State.ShowOverlay: true };

        // Perf HUD: only when the full overlay is NOT up (the panels already show
        // frame time, and the point of the HUD is a minimal-cost measurement).
        if (!overlayUp)
        {
            if (!perfHudVisible) return;
            var (w, h) = LogicalSize;
            imguiRenderer.BeginFramePerfHud(
                w, h, frame.Width, frame.Height, deltaTime,
                $"{fpsDisplay:0} FPS  ({frameMsDisplay:0.0} ms)");
            commandList.Pass(
                "perf-hud",
                new RenderPassDescription(
                    Target: RenderSurfaceHandle.Default,
                    ClearColors: Array.Empty<GraphicsColor?>(),
                    ClearDepth: false),
                pass => imguiRenderer.Submit(pass));
            return;
        }

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

        imguiRenderer.BeginFrame(
            logicalW, logicalH, frame.Width, frame.Height, deltaTime,
            mousePos, left, right, middle, wheel, debugSystem!); // overlayUp ⇒ non-null

        commandList.Pass(
            "imgui",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: Array.Empty<GraphicsColor?>(),
                ClearDepth: false),
            pass => imguiRenderer.Submit(pass));
    }

    private bool warnedDebugIdentityVp;

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
