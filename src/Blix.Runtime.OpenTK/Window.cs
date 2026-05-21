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
    private OpenGLGraphicsDevice? graphicsDevice;
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
            debugSystem.Run(debuggable);
        }

        var commandList = new RenderCommandList();
        gameLoop.OnRender(time, frame, commandList);
        AppendDebugDrawPass(commandList);

        if (graphicsDevice is { } device)
        {
            var packet = device.Execute(commandList);

            if (diagnostics is { } sink)
            {
                sink.OnFrameDebug(packet, device.SnapshotResources());
            }

            if (debugOverlayRenderer is { } overlayRenderer &&
                debugSystem is { State.Enabled: true, State.ShowOverlay: true })
            {
                overlayRenderer.RenderOverlay(
                    ClientSize,
                    GetCurrentFramebufferSize(),
                    (float)args.Time,
                    MouseState,
                    debugSystem);
            }
        }

        SwapBuffers();
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
        inputHandler?.OnKeyDown(MapKey(args.Key));
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

        foreach (var command in debugSystem.Current.Draw.Commands)
        {
            switch (command.Kind)
            {
                case DebugDrawCommandKind.Line:
                    debugDraw.Line(command.A, command.B, command.Color);
                    break;

                case DebugDrawCommandKind.Aabb:
                    debugDraw.Aabb(command.A, command.B, command.Color);
                    break;

                case DebugDrawCommandKind.Grid:
                    debugDraw.Grid(command.A, command.Size, command.Divisions, command.Color);
                    break;

                case DebugDrawCommandKind.Frustum:
                    debugDraw.Frustum(command.Matrix, command.Color);
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
