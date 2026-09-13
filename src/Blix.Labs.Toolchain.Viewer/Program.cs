using System.Numerics;
using Blix;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Labs.Toolchain;
using Blix.Runtime.Silk;
using ImGuiNET;

namespace Blix.Labs.Toolchain.Viewer;

// The toolchain lab's viewer — chassis conventions over a real lit scene.
//
// ── Proves ──────────────────────────────────────────────────────────────────
//   • Several executables over ONE lab: the shaders, scene and renderer live in
//     Blix.Labs.Toolchain and arrive here as content. This project declares no
//     shaders and has no render code.
//   • Reflected binding: every descriptor set, UBO offset and push range comes from
//     spirv-cross sidecars, not from a hand-written ShaderInterface.
//   • The shared build targets doing real work — BlixShaderMode=Library plus
//     BlixShaderReflect, which was extracted the moment this became the second
//     consumer that wanted it.
//   • Chassis: IUiSource panel, IInputHandler with UI capture, host-owned --frames,
//     named views and trails.
//
// ── Intentionally owns ──────────────────────────────────────────────────────
//   • Camera feel, the panel's controls, what the lab scene contains.
public static class Program
{
    public static void Main(string[] args)
    {
        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Blix — toolchain lab",
            Width = 1280,
            Height = 760,
        });

        var loop = new ViewerLoop();
        using var window = new Window(loop, options);
        window.Run();
    }
}

internal sealed class ViewerLoop : IGameLoop, IDebuggable, IUiSource, IInputHandler, IDisposable
{
    private readonly LabRenderer renderer = new();
    private readonly LabScene scene = LabScene.Default();

    private float yaw = 0.7f;
    private float pitch = 0.45f;
    private float distance = 11f;
    private bool dragging;

    private Matrix4x4 viewProjection = Matrix4x4.Identity;
    private Vector3 cameraPosition;
    private float sunYaw = 0.5f;
    private float sunPitch = 0.9f;
    private bool showTrail = true;
    private bool showPrimitives = true;

    // Short, because this is a gizmo and not a motion study. Six seconds was the first
    // guess and reads as the arc refusing to leave; a trail that outlives the gesture that
    // drew it stops being an annotation and becomes clutter. Exposed as a dial rather than
    // re-guessed, since a lab is the place to find out what the right number feels like.
    private float trailSeconds = 1.5f;
    private int frames;

    // <b>One aspect, shared.</b> Debug() runs BEFORE OnRender in the frame, so a debug view
    // built from a hardcoded 16:9 drew its grid through a different projection than the
    // scene used — the grid and the ground quad are both 24 m across and did not line up.
    // The window's real aspect, captured where the runtime reports it.
    private float aspect = 16f / 9f;

    // Mirrors DebugState.DepthTestDrawing so the panel can flip it. Applied in Debug(), which is
    // the only place with a DebugContext to hand.
    private bool depthTestGizmos = true;

    public string DebugName => "lab";

    public string UiName => "lab";

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        var vk = (VulkanGraphicsDevice)graphicsDevice;

        // Shaders arrive from the lab library's content propagation — this executable
        // never compiled one.
        renderer.Load(vk, Path.Combine(AppContext.BaseDirectory, "Shaders"));
    }

    public void OnUpdate(Time time)
    {
        var eye = new Vector3(
            MathF.Cos(pitch) * MathF.Sin(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Cos(yaw)) * distance;

        cameraPosition = eye;
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(0f, 1f, 0f), Vector3.UnitY);
        viewProjection = view * GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3.2f, aspect, 0.1f, 120f);

        scene.SetSunDirection(new Vector3(
            MathF.Cos(sunPitch) * MathF.Sin(sunYaw),
            MathF.Sin(sunPitch),
            MathF.Cos(sunPitch) * MathF.Cos(sunYaw)));
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frames++;
        if (frame.Height > 0) aspect = frame.Width / (float)frame.Height;
        var view = Matrix4x4.CreateLookAt(cameraPosition, new Vector3(0f, 1f, 0f), Vector3.UnitY);
        viewProjection = view * GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3.2f, aspect, 0.1f, 120f);

        renderer.Render(commandList, scene, viewProjection, cameraPosition);
    }

    public void Debug(DebugContext debug)
    {
        debug.State.DepthTestDrawing = depthTestGizmos;
        debug.Values.Value("frames", frames);
        debug.Values.Value("sun", scene.SunDirection);
        debug.Stats.Gauge("objects", scene.Objects.Count);

        using var view = debug.Draw.In("main", viewProjection);
        debug.Draw.Grid("floor", new Vector3(0f, 0.02f, 0f), 24f, 24, new GraphicsColor(0.2f, 0.24f, 0.3f, 1f));

        // The sun, drawn where it is actually pointing — an arrow that is wrong is the
        // fastest way to notice a lighting convention has drifted.
        var sunFrom = scene.SunDirection * 7f;
        debug.Draw.Arrow("sun", sunFrom, Vector3.Zero, new GraphicsColor(1f, 0.9f, 0.5f, 1f));

        if (showPrimitives) DrawPrimitiveVocabulary(debug);

        if (showTrail)
        {
            // Where the sun has been while it was being dragged.
            debug.Draw.Trail("sun/path", sunFrom, new GraphicsColor(1f, 0.75f, 0.3f, 1f), trailSeconds);
        }
    }

    // Every debug primitive the channel offers, drawn at once.
    //
    // Five of these — Plane, Capsule, Cone, MeshWireframe, Normals — were exposed by the
    // API and silently skipped by the renderer for their whole lives, so calling them
    // succeeded and drew nothing. This is the standing check that they still arrive: if a
    // primitive stops rendering, it is visible here rather than discovered by someone
    // trying to debug a collider with it.
    //
    // The capsule is the one that matters most for what comes next. It is drawn where a
    // character's collider would stand, because "what shape did collision actually test?"
    // is the question a motion bug always turns into.
    private void DrawPrimitiveVocabulary(DebugContext debug)
    {
        var here = new Vector3(-5.5f, 0f, 0f);
        var white = new GraphicsColor(0.9f, 0.9f, 0.95f, 1f);
        var warm = new GraphicsColor(1f, 0.6f, 0.35f, 1f);
        var cool = new GraphicsColor(0.4f, 0.8f, 1f, 1f);

        using (debug.Scope("vocabulary"))
        {
            debug.Draw.Capsule("capsule", here + new Vector3(0f, 0.4f, 0f), here + new Vector3(0f, 1.4f, 0f), 0.4f, cool);
            debug.Draw.Plane("plane", here + new Vector3(-2f, 0.9f, 0f), new Vector3(0.3f, 1f, 0.2f), 1.6f, white);
            debug.Draw.Cone("cone", here + new Vector3(2f, 1.8f, 0f), -Vector3.UnitY, 1.5f, 0.45f, warm);
            debug.Draw.Sphere("sphere", here + new Vector3(2f, 0.5f, -2f), 0.45f, white);
            debug.Draw.Aabb("aabb", here + new Vector3(-2f, 0f, -2f), here + new Vector3(-1.2f, 0.8f, -1.2f), warm);
            debug.Draw.Cross("cross", here + new Vector3(0f, 0.05f, -2f), 0.5f, cool);
            debug.Draw.Ray("ray", here + new Vector3(0f, 2.2f, 0f), Vector3.UnitX, 1.5f, warm);

            // A triangle's edges and its vertex normals — the two that answer "what geometry
            // was actually tested?" when a sweep disagrees with what is on screen.
            var tri = new[]
            {
                here + new Vector3(3.6f, 0.05f, 1.2f),
                here + new Vector3(4.8f, 0.05f, 1.2f),
                here + new Vector3(4.2f, 0.05f, 2.4f),
            };
            debug.Draw.MeshWireframe("triangle", tri, new[] { 0, 1, 1, 2, 2, 0 }, white);
            debug.Draw.Normals("triangle/normals", tri, new[] { Vector3.UnitY, Vector3.UnitY, Vector3.UnitY }, 0.6f, cool);
        }
    }

    public void DrawUi()
    {
        ImGui.SetNextWindowSize(new Vector2(320, 260), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(20, 20), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("lab"))
        {
            ImGui.End();
            return;
        }

        ImGui.TextDisabled("shaders + scene live in Blix.Labs.Toolchain");
        ImGui.Separator();

        var exposure = renderer.Exposure;
        if (ImGui.SliderFloat("exposure", ref exposure, 0.1f, 4f)) renderer.Exposure = exposure;

        var mode = (int)renderer.TonemapMode;
        if (ImGui.Combo("tonemap", ref mode, "ACES\0AgX\0Reinhard\0Neutral\0")) renderer.TonemapMode = mode;

        ImGui.SliderFloat("sun yaw", ref sunYaw, -MathF.PI, MathF.PI);
        ImGui.SliderFloat("sun pitch", ref sunPitch, 0.15f, 1.5f);

        var ambient = scene.AmbientStrength;
        if (ImGui.SliderFloat("ambient", ref ambient, 0f, 0.4f)) scene.AmbientStrength = ambient;

        // Worth flipping rather than believing: with it off the grid and the capsule draw straight
        // through the boxes, which is what every gizmo in Blix did until now.
        ImGui.Checkbox("depth-test gizmos", ref depthTestGizmos);
        ImGui.Checkbox("primitive vocabulary", ref showPrimitives);
        ImGui.Checkbox("sun trail", ref showTrail);
        if (showTrail) ImGui.SliderFloat("trail seconds", ref trailSeconds, 0.25f, 8f);
        ImGui.TextDisabled($"drag to orbit · wheel to zoom · {frames} frames");
        ImGui.End();
    }

    public void OnMouseDown(MouseButton button)
    {
        if (button == MouseButton.Left) dragging = true;
    }

    public void OnMouseUp(MouseButton button)
    {
        if (button == MouseButton.Left) dragging = false;
    }

    public void OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        if (!dragging) return;
        yaw -= deltaX * 0.008f;
        pitch = Math.Clamp(pitch + deltaY * 0.006f, 0.08f, 1.45f);
    }

    public void OnMouseWheel(float offsetX, float offsetY)
    {
        distance = Math.Clamp(distance - offsetY * 0.8f, 3.5f, 40f);
    }

    // <b>Dispose, not OnUnload.</b> OnUnload fires from the window's Closing event, which can
    // land mid-frame before the final submit — tearing down GPU resources there is a crash,
    // and Window.Dispose says so in as many words. The runtime disposes the loop after
    // WaitIdle and before the device goes, which is the only safe window. TankArena and
    // Bulwark both take this route; this one had to crash first to join them.
    public void Dispose() => renderer.Dispose();
}
