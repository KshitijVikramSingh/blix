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

internal sealed class ViewerLoop : IGameLoop, IDebuggable, IUiSource, IInputHandler
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
    private int frames;

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
        var projection = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3.2f, 16f / 9f, 0.1f, 120f);
        viewProjection = view * projection;

        scene.SetSunDirection(new Vector3(
            MathF.Cos(sunPitch) * MathF.Sin(sunYaw),
            MathF.Sin(sunPitch),
            MathF.Cos(sunPitch) * MathF.Cos(sunYaw)));
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frames++;
        var aspect = frame.Height > 0 ? frame.Width / (float)frame.Height : 16f / 9f;
        var view = Matrix4x4.CreateLookAt(cameraPosition, new Vector3(0f, 1f, 0f), Vector3.UnitY);
        viewProjection = view * GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3.2f, aspect, 0.1f, 120f);

        renderer.Render(commandList, scene, viewProjection, cameraPosition);
    }

    public void Debug(DebugContext debug)
    {
        debug.Values.Value("frames", frames);
        debug.Values.Value("sun", scene.SunDirection);
        debug.Stats.Gauge("objects", scene.Objects.Count);

        using var view = debug.Draw.In("main", viewProjection);
        debug.Draw.Grid("floor", Vector3.Zero, 24f, 24, new GraphicsColor(0.2f, 0.24f, 0.3f, 1f));

        // The sun, drawn where it is actually pointing — an arrow that is wrong is the
        // fastest way to notice a lighting convention has drifted.
        var sunFrom = scene.SunDirection * 7f;
        debug.Draw.Arrow("sun", sunFrom, Vector3.Zero, new GraphicsColor(1f, 0.9f, 0.5f, 1f));

        if (showTrail)
        {
            // Where the sun has been while it was being dragged.
            debug.Draw.Trail("sun/path", sunFrom, new GraphicsColor(1f, 0.75f, 0.3f, 1f), seconds: 6f);
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

        ImGui.Checkbox("sun trail", ref showTrail);
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

    public void OnUnload() => renderer.Dispose();
}
