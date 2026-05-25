using System.Numerics;
using Blix.Audio;
using Blix.Audio.OpenAL;
using Blix.Core;
using Blix.Diagnostics;
using Blix;
using Blix.Graphics;
using Blix.Graphics.OpenGL;
using Blix.Render;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using GlfwMouseButton = OpenTK.Windowing.GraphicsLibraryFramework.MouseButton;
using MouseButton = Blix.Core.MouseButton;

namespace Blix.Runtime.OpenTK;

public sealed class Window : GameWindow, IRenderHost, IAudioHost, IDebugHost
{
    private readonly IGameLoop gameLoop;
    private readonly IInputHandler? inputHandler;
    private readonly IRuntimeDiagnosticsSink? diagnostics;
    private readonly DebugSystem? debugSystem;
    private readonly DiagnosticsFrameRecorder? frameRecorder;
    private readonly JsonDumpSink? jsonDumpSink;
    private OpenGLGraphicsDevice? graphicsDevice;
    // One-shot footgun guard for the most common debug-draw setup miss
    // (demo emits Draw primitives but never sets Draw.ViewProjection).
    // Set on the first frame the warning fires; never resets.
    private bool warnedAboutIdentityViewProjection;
    private OpenALAudioDevice? audioDevice;
    private ImGuiOverlayRenderer? debugOverlayRenderer;
    private DebugDraw? debugDraw;
    private double totalTime;

    public Window(
        IGameLoop gameLoop,
        WindowOptions? options = null,
        IRuntimeDiagnosticsSink? diagnostics = null)
        : base(CreateGameWindowSettings(), CreateNativeWindowSettings(options))
    {
        this.gameLoop = gameLoop;
        // Input is opt-in: the game loop only receives key/mouse events if it also
        // implements IInputHandler. Decoupling input from the loop contract keeps
        // headless/test usage clean and lets future games handle input through a
        // different (polling-style) channel without breaking the interface.
        this.inputHandler = gameLoop as IInputHandler;
        this.diagnostics = diagnostics;
        if (gameLoop is IDebuggable)
        {
            debugSystem = new DebugSystem();
            frameRecorder = new DiagnosticsFrameRecorder(debugSystem);
            // Default to warn+error on stderr; producers that emit info-level
            // chatter (e.g. ResourceUploader per-mip completions) stay quiet
            // unless the consumer opts in to a noisier sink.
            debugSystem.AddSink(new ConsoleEventSink());
            // F12 dumps the current display frame to disk; sink stays armed
            // between presses and discharges on the next EndFrame.
            jsonDumpSink = new JsonDumpSink();
            debugSystem.AddSink(jsonDumpSink);
        }
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        ApplyFramebufferViewport();
        graphicsDevice = new OpenGLGraphicsDevice();
        // Audio device construction can fail on machines without an OpenAL
        // backend (sandboxed CI, headless containers). Catch + warn rather
        // than aborting startup — Game.AudioDevice stays null and audio-aware
        // game code skips its audio path via null-check.
        try
        {
            audioDevice = new OpenALAudioDevice();
            Console.WriteLine($"Audio: {audioDevice.Vendor} | {audioDevice.Renderer} | {audioDevice.Version}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Audio device unavailable: {ex.Message}");
        }
        if (debugSystem is not null)
        {
            debugOverlayRenderer = new ImGuiOverlayRenderer();
            debugDraw = new DebugDraw(graphicsDevice);
        }

        SetDefaultRenderSurfaceSize();
        gameLoop.OnLoad(this, graphicsDevice);
    }

    // IAudioHost facet. Returns the live OpenAL device. Throws if accessed
    // before OnLoad or after Dispose — same lifecycle contract as the graphics
    // device exposed to game code.
    public IAudioDevice AudioDevice =>
        audioDevice ?? throw new InvalidOperationException("Audio device is not initialised. OnLoad has not run yet, or no audio backend is available.");

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        totalTime += args.Time;
        gameLoop.OnUpdate(new Time(totalTime, args.Time));
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        var time = new Time(totalTime, args.Time);
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
        // The three phase timers below split the per-tick CPU work into
        // build / submit / overlay so the HUD answers "is the cost in
        // recording commands, in the GL backend translating them, or in
        // the debug UI itself?" without anyone having to wire it per demo.
        using (debugSystem?.Current?.Timers.Measure("build-commands"))
        {
            gameLoop.OnRender(time, frame, commandList);
            AppendDebugDrawPass(commandList);
        }

        if (graphicsDevice is { } device)
        {
            FrameDebugPacket packet;
            using (debugSystem?.Current?.Timers.Measure("execute"))
            {
                packet = device.Execute(commandList);
            }

            // GPU pass timings landing this tick (results from this frame
            // or one of the previous few, driver-dependent). Pushed under
            // a dedicated "gpu/passes" scope so they group apart from the
            // CPU "passes/<name>/build" timers Phase 3 already emits.
            if (debugSystem?.Current is { } ctx)
            {
                var gpuTimings = device.ConsumeAvailableGpuTimings();
                for (var i = 0; i < gpuTimings.Count; i++)
                {
                    var t = gpuTimings[i];
                    ctx.Timers.AppendCompleted(t.PassName, scope: "gpu/passes", t.ElapsedMs);
                }
            }

            if (diagnostics is { } sink)
            {
                sink.OnFrameDebug(packet, device.SnapshotResources());
            }

            if (debugOverlayRenderer is { } overlayRenderer &&
                debugSystem is { State.Enabled: true, State.ShowOverlay: true })
            {
                using (debugSystem.Current?.Timers.Measure("overlay"))
                {
                    overlayRenderer.RenderOverlay(
                        ClientSize,
                        GetCurrentFramebufferSize(),
                        (float)args.Time,
                        MouseState,
                        debugSystem);
                }
            }
        }

        // SwapBuffers blocks on the display-sync source — VSync if it's
        // on, but on macOS the Cocoa/Metal layer can impose its own
        // half-rate / refresh sync that's *independent* of GL's VSync
        // mode. If `swap` shows 20+ ms while every other timer is small,
        // the cap isn't GPU work or driver overhead — it's the present.
        // Timed before EndFrame so the value lands in this frame's
        // snapshot (Current is still alive until EndFrame clears it).
        using (debugSystem?.Current?.Timers.Measure("swap"))
        {
            SwapBuffers();
        }

        // Seal the frame *after* swap so the frame timer covers the
        // entire wall-clock cost (including the present wait). ImGui
        // already read its data above the swap call.
        debugSystem?.EndFrame();
    }

    protected override void OnResize(ResizeEventArgs args)
    {
        base.OnResize(args);
        gameLoop.OnResize(args.Width, args.Height);
    }

    protected override void OnFramebufferResize(FramebufferResizeEventArgs args)
    {
        base.OnFramebufferResize(args);
        ApplyViewport(args.Width, args.Height);
        graphicsDevice?.SetDefaultRenderSurfaceSize(Math.Max(args.Width, 1), Math.Max(args.Height, 1));
    }

    protected override void OnKeyDown(KeyboardKeyEventArgs args)
    {
        base.OnKeyDown(args);
        // Runtime-owned shortcut: F12 captures the current display frame
        // (frozen if frozen, else latest finished snapshot) to disk via
        // JsonDumpSink. Swallowed before the input handler sees it so a
        // game that uses F12 for something else doesn't conflict — we
        // own the binding.
        if (args.Key == Keys.F12 && TryDumpCurrentFrame())
        {
            return;
        }
        // Tilde / backtick toggles the diagnostics overlay window. The
        // diagnostics system stays running (draws still emit, frames
        // still snapshot, sinks still fire) — only the ImGui overlay
        // hides. Same convention as console-toggle in many engines.
        if (args.Key == Keys.GraveAccent && debugSystem is not null)
        {
            debugSystem.State.ShowOverlay = !debugSystem.State.ShowOverlay;
            return;
        }
        inputHandler?.OnKeyDown(MapKey(args.Key));
    }

    // Matches paths emitted by DebugSystem.Run's selection sweep —
    // duplicated here as a literal so this runtime-side filter stays
    // independent of an import for one constant. Kept in sync with
    // DebugSystem.SelectionScope.
    private static bool IsSelectionPath(string path)
    {
        return path.StartsWith("selection/", StringComparison.Ordinal);
    }

    private bool TryDumpCurrentFrame()
    {
        if (debugSystem is null || jsonDumpSink is null)
        {
            return false;
        }

        // Frozen frame is dumped immediately because the user has it in
        // front of them and shouldn't have to wait for "the next live
        // frame"; live mode arms the sink so the dump captures the
        // frame number that will fire on the next EndFrame.
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

    protected override void OnKeyUp(KeyboardKeyEventArgs args)
    {
        base.OnKeyUp(args);
        inputHandler?.OnKeyUp(MapKey(args.Key));
    }

    protected override void OnMouseMove(MouseMoveEventArgs args)
    {
        base.OnMouseMove(args);
        if (IsDebugUiCapturingMouse())
        {
            return;
        }

        inputHandler?.OnMouseMove(args.X, args.Y, args.DeltaX, args.DeltaY);
    }

    protected override void OnMouseDown(MouseButtonEventArgs args)
    {
        base.OnMouseDown(args);
        if (IsDebugUiCapturingMouse())
        {
            return;
        }

        inputHandler?.OnMouseDown(MapMouseButton(args.Button));
    }

    protected override void OnMouseUp(MouseButtonEventArgs args)
    {
        base.OnMouseUp(args);
        if (IsDebugUiCapturingMouse())
        {
            return;
        }

        inputHandler?.OnMouseUp(MapMouseButton(args.Button));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs args)
    {
        base.OnMouseWheel(args);
        if (IsDebugUiCapturingMouse())
        {
            return;
        }

        inputHandler?.OnMouseWheel(args.OffsetX, args.OffsetY);
    }

    protected override void OnUnload()
    {
        gameLoop.OnUnload();
        debugOverlayRenderer?.Dispose();
        graphicsDevice?.Dispose();
        audioDevice?.Dispose();
        base.OnUnload();
    }

    public DebugContext? CurrentDebug => debugSystem?.Current;

    public DebugSystem? System => debugSystem;

    public void SetTitle(string title)
    {
        Title = title;
    }

    public void RequestClose()
    {
        Close();
    }

    public void SetCursorCaptured(bool captured)
    {
        // OpenTK's "Grabbed" hides the cursor AND locks it to the window center,
        // reporting all motion as deltas. That's the FPS-style capture the demo wants.
        CursorState = captured ? CursorState.Grabbed : CursorState.Normal;
    }

    public void SetVSync(bool enabled)
    {
        // OpenTK's VSync property: On clamps to refresh; Off lets the
        // GPU run uncapped (shows true frame cost in profiling); Adaptive
        // is per-driver-discretion. We expose binary on/off — Adaptive
        // is the user's display-control-panel concern, not engine API.
        VSync = enabled ? VSyncMode.On : VSyncMode.Off;
    }

    // Explicit interface impl — the base GameWindow already has a ClientSize property
    // (Vector2i), so we expose the IRenderHost.LogicalSize tuple form without shadowing.
    (int Width, int Height) IRenderHost.LogicalSize => (ClientSize.X, ClientSize.Y);

    private RenderFrameContext CreateFrameContext()
    {
        var size = GetCurrentFramebufferSize();
        return new RenderFrameContext(Width: size.X, Height: size.Y);
    }

    private void ApplyFramebufferViewport()
    {
        var size = GetCurrentFramebufferSize();
        ApplyViewport(size.X, size.Y);
    }

    private void SetDefaultRenderSurfaceSize()
    {
        var size = GetCurrentFramebufferSize();
        graphicsDevice?.SetDefaultRenderSurfaceSize(size.X, size.Y);
    }

    private void ApplyViewport(int width, int height)
    {
        GL.Viewport(0, 0, Math.Max(width, 1), Math.Max(height, 1));
    }

    private Vector2i GetCurrentFramebufferSize()
    {
        if (FramebufferSize.X > 0 && FramebufferSize.Y > 0)
        {
            return FramebufferSize;
        }

        return ClientSize;
    }

    private bool IsDebugUiCapturingMouse()
    {
        return debugSystem is { State.Enabled: true, State.ShowOverlay: true } &&
               debugOverlayRenderer is { WantsMouseCapture: true };
    }

    private void AppendDebugDrawPass(RenderCommandList commandList)
    {
        if (debugSystem is not { State.Enabled: true, State.ShowDebugDraw: true, Current.Draw.Commands.Count: > 0 } ||
            debugDraw is null)
        {
            return;
        }

        // Footgun guard: a demo that emits debug-draw primitives but
        // never assigns Draw.ViewProjection will see *nothing* render
        // because identity * world_position lands outside clip space.
        // The symptom is invisible — Phase 11 made selection visibility
        // depend on this, and the user hit it. Warn loudly once.
        if (!warnedAboutIdentityViewProjection &&
            debugSystem.Current.Draw.ViewProjection.Equals(Matrix4x4.Identity))
        {
            warnedAboutIdentityViewProjection = true;
            Console.Error.WriteLine(
                "[diagnostics] debug.Draw.ViewProjection is Matrix4x4.Identity but draw commands " +
                "were emitted this frame. Lines will render in clip space (likely invisible). " +
                "Set debug.Draw.ViewProjection = projection * view in your render code.");
        }

        var state = debugSystem.State;
        foreach (var command in debugSystem.Current.Draw.Commands)
        {
            // Path-prefix layer gate: disabled prefixes skip the entire
            // primitive instead of just hiding it in the UI, so the
            // CPU cost of expanding into line vertices is also saved.
            //
            // Selection draws are system feedback ("you picked this"),
            // not user-content. They bypass the filter so a stray click
            // in the Layers panel can't accidentally hide the very
            // outline the user needs to confirm what they picked.
            if (!IsSelectionPath(command.Path) && !state.IsPathVisible(command.Path))
            {
                continue;
            }
            switch (command)
            {
                case DebugDrawLine c:
                    debugDraw.Line(c.A, c.B, c.Color);
                    break;
                case DebugDrawAabb c:
                    debugDraw.Aabb(c.Min, c.Max, c.Color);
                    break;
                case DebugDrawGrid c:
                    debugDraw.Grid(c.Center, c.Size, c.Divisions, c.Color);
                    break;
                case DebugDrawFrustum c:
                    debugDraw.Frustum(c.ViewProjection, c.Color);
                    break;
                case DebugDrawSphere c:
                    debugDraw.Sphere(c.Center, c.Radius, c.Color, c.Segments);
                    break;
                case DebugDrawPlane c:
                    debugDraw.Plane(c.Center, c.Normal, c.Size, c.Color);
                    break;
                case DebugDrawRay c:
                    debugDraw.Ray(c.Origin, c.Direction, c.Length, c.Color);
                    break;
                case DebugDrawCapsule c:
                    debugDraw.Capsule(c.A, c.B, c.Radius, c.Color, c.Segments);
                    break;
                case DebugDrawObb c:
                    debugDraw.Obb(c.Transform, c.Color);
                    break;
                case DebugDrawCross c:
                    debugDraw.Cross(c.Center, c.Size, c.Color);
                    break;
                case DebugDrawCone c:
                    debugDraw.Cone(c.Apex, c.Axis, c.Length, c.HalfAngleRad, c.Color, c.Segments);
                    break;
                case DebugDrawArrow c:
                    debugDraw.Arrow(c.From, c.To, c.Color);
                    break;
                case DebugDrawMeshWireframe c:
                    debugDraw.MeshWireframe(c.Vertices, c.Edges, c.Color);
                    break;
                case DebugDrawNormals c:
                    debugDraw.Normals(c.Positions, c.Normals, c.Length, c.Color);
                    break;
            }
        }

        commandList.Pass(
            "debug",
            new RenderPassDescription(
                RenderSurfaceHandle.Default,
                ClearColors: Array.Empty<GraphicsColor?>(),
                ClearDepth: false),
            pass => debugDraw.Submit(pass, debugSystem.Current.Draw.ViewProjection));
    }

    private static GameWindowSettings CreateGameWindowSettings()
    {
        return new GameWindowSettings
        {
            UpdateFrequency = 60.0
        };
    }

    private static NativeWindowSettings CreateNativeWindowSettings(WindowOptions? options)
    {
        options ??= WindowOptions.Default;

        return new NativeWindowSettings
        {
            Title = options.Title,
            ClientSize = new Vector2i(options.Width, options.Height),
            APIVersion = new Version(4, 1)
        };
    }

    private static Key MapKey(Keys key)
    {
        return key switch
        {
            Keys.Escape => Key.Escape,
            Keys.Space => Key.Space,
            Keys.Enter => Key.Enter,
            Keys.Tab => Key.Tab,
            Keys.Backspace => Key.Backspace,
            Keys.Left => Key.Left,
            Keys.Right => Key.Right,
            Keys.Up => Key.Up,
            Keys.Down => Key.Down,
            Keys.A => Key.A,
            Keys.B => Key.B,
            Keys.C => Key.C,
            Keys.D => Key.D,
            Keys.E => Key.E,
            Keys.F => Key.F,
            Keys.G => Key.G,
            Keys.H => Key.H,
            Keys.I => Key.I,
            Keys.J => Key.J,
            Keys.K => Key.K,
            Keys.L => Key.L,
            Keys.M => Key.M,
            Keys.N => Key.N,
            Keys.O => Key.O,
            Keys.P => Key.P,
            Keys.Q => Key.Q,
            Keys.R => Key.R,
            Keys.S => Key.S,
            Keys.T => Key.T,
            Keys.U => Key.U,
            Keys.V => Key.V,
            Keys.W => Key.W,
            Keys.X => Key.X,
            Keys.Y => Key.Y,
            Keys.Z => Key.Z,
            Keys.LeftControl => Key.LeftControl,
            Keys.RightControl => Key.RightControl,
            Keys.LeftSuper => Key.LeftSuper,
            Keys.RightSuper => Key.RightSuper,
            _ => Key.Unknown
        };
    }

    private static MouseButton MapMouseButton(GlfwMouseButton button)
    {
        return button switch
        {
            GlfwMouseButton.Left => MouseButton.Left,
            GlfwMouseButton.Right => MouseButton.Right,
            GlfwMouseButton.Middle => MouseButton.Middle,
            _ => MouseButton.Unknown
        };
    }
}
