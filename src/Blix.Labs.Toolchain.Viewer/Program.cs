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
        // --model <path> loads a glTF. Nothing else in the tree can look at an asset; the numbers
        // blix-cook inspect prints have always had to be trusted rather than seen.
        var modelPath = ArgValue(args, "--model");
        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Blix — toolchain lab",
            Width = 1280,
            Height = 760,
        });

        var loop = new ViewerLoop(modelPath);
        using var window = new Window(loop, options);
        window.Run();
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name) return args[i + 1];
        }

        return null;
    }
}

internal sealed class ViewerLoop : IGameLoop, IDebuggable, IUiSource, IInputHandler, IDisposable
{
    private readonly LabRenderer renderer = new();
    private LabScene scene = LabScene.Default();
    private readonly string? modelPath;
    private LabModel? model;
    private Matrix4x4 modelTransform = Matrix4x4.Identity;
    private bool showPivots = true;
    private bool showAllPivots;
    private bool showBounds = true;
    private int selectedNode = -1;

    public ViewerLoop(string? modelPath = null)
    {
        this.modelPath = modelPath;
    }


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

        if (modelPath is null) return;
        if (!File.Exists(modelPath))
        {
            Console.Error.WriteLine($"No model at {modelPath}.");
            return;
        }

        model = LabModel.Load(vk, modelPath);

        // The model is the subject now; the box ring is a backdrop that hides it.
        scene = LabScene.GroundOnly();

        // Assets arrive at whatever scale their author used — the Quaternius tank is ~14 units
        // long — so the lab normalises to a couple of metres and seats the model on the ground.
        // A viewer that shows nothing because the asset is off-screen teaches nothing.
        var extent = model.LongestExtent;
        var scale = extent > 0.001f ? 3f / extent : 1f;
        modelTransform = Matrix4x4.CreateScale(scale)
                         * Matrix4x4.CreateTranslation(0f, -model.BoundsMin.Y * scale, 0f);

        Console.WriteLine(
            $"model: {Path.GetFileName(modelPath)} — {model.Nodes.Count} node(s), {model.Parts.Count} part(s), " +
            $"bounds {model.BoundsMin.X:0.00},{model.BoundsMin.Y:0.00},{model.BoundsMin.Z:0.00} .. " +
            $"{model.BoundsMax.X:0.00},{model.BoundsMax.Y:0.00},{model.BoundsMax.Z:0.00}, " +
            $"scaled x{scale:0.000}");
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

        renderer.Render(commandList, scene, viewProjection, cameraPosition, model, modelTransform);
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

        if (model is not null) DrawModelGizmos(debug);

        if (showPrimitives) DrawPrimitiveVocabulary(debug);

        if (showTrail)
        {
            // Where the sun has been while it was being dragged.
            debug.Draw.Trail("sun/path", sunFrom, new GraphicsColor(1f, 0.75f, 0.3f, 1f), trailSeconds);
        }
    }

    // The node tree, and what the selected one actually is.
    //
    // The same facts blix-cook inspect prints — name, parent, composed pivot, bounds, vertex
    // count — except the selected node is simultaneously outlined in the scene, so a number and
    // the thing it describes are in front of you at once. That pairing is the entire reason this
    // exists; either half alone is what the project already had.
    private void DrawNodeList()
    {
        if (model is null) return;

        ImGui.Separator();
        ImGui.TextDisabled($"{model.Nodes.Count} nodes · {model.Parts.Count} meshes");

        if (ImGui.BeginChild("nodes", new Vector2(0, 150), ImGuiChildFlags.Borders))
        {
            for (var i = 0; i < model.Nodes.Count; i++)
            {
                var node = model.Nodes[i];
                if (node.PrimitiveCount == 0 && !showAllPivots) continue;

                var label = node.PrimitiveCount > 0
                    ? $"{node.Name}  ({node.VertexCount} v)"
                    : $"{node.Name}";
                if (ImGui.Selectable(label, selectedNode == i)) selectedNode = i;
            }
        }

        ImGui.EndChild();

        if (selectedNode < 0 || selectedNode >= model.Nodes.Count) return;

        var s = model.Nodes[selectedNode];
        var world = s.WorldTransform * modelTransform;
        ImGui.TextDisabled($"parent {s.ParentIndex}   prims {s.PrimitiveCount}");

        // The composed translation IS the rig pivot — the number a game drives the part about.
        ImGui.Text($"pivot  {world.M41:0.000}, {world.M42:0.000}, {world.M43:0.000}");

        if (Matrix4x4.Decompose(s.LocalTransform, out var ls, out var lr, out var lt))
        {
            ImGui.Text($"local T {lt.X:0.000}, {lt.Y:0.000}, {lt.Z:0.000}");
            ImGui.Text($"local S {ls.X:0.000}, {ls.Y:0.000}, {ls.Z:0.000}");
            ImGui.TextDisabled($"local R {lr.X:0.00}, {lr.Y:0.00}, {lr.Z:0.00}, {lr.W:0.00}");
        }
        else
        {
            ImGui.TextDisabled("local transform does not decompose (sheared?)");
        }
    }

    // <b>What the importer actually produced, drawn where it produced it.</b>
    //
    // blix-cook inspect prints these same numbers — composed-world pivots, assembled bounds, the
    // node tree — and printing them is where asset work has always had to stop. The measured-pivot
    // fit has been done by hand at least three times in this project (TankArena's tank rig, the CC0
    // sourcing workflow, the RTS villager), each time by turning knobs in an overlay until the model
    // looked right, because nothing could show whether a pivot was where the numbers claimed.
    //
    // An axis triad at a node's composed translation IS that claim, drawn.
    private void DrawModelGizmos(DebugContext debug)
    {
        if (model is null) return;

        using var scope = debug.Scope("model");
        var size = MathF.Max(0.06f, model.LongestExtent * ScaleOf(modelTransform) * 0.05f);

        if (showPivots)
        {
            for (var i = 0; i < model.Nodes.Count; i++)
            {
                var node = model.Nodes[i];
                var world = node.WorldTransform * modelTransform;
                var origin = new Vector3(world.M41, world.M42, world.M43);

                // A transform-only node is usually an armature or a rig pivot — terser, because a
                // 96-node tree of full triads is unreadable.
                if (node.PrimitiveCount == 0 && !showAllPivots) continue;
                var axisLength = node.PrimitiveCount > 0 ? size : size * 0.5f;
                var selected = i == selectedNode;

                // Local axes, not world: this is where "forward" is for THIS node, which is the
                // thing that disagrees with the engine's -Z and costs an afternoon.
                Axis(debug, node.Name + "/x", origin, Row(world, 0), axisLength,
                    selected ? new GraphicsColor(1f, 1f, 1f, 1f) : new GraphicsColor(0.95f, 0.3f, 0.3f, 1f));
                Axis(debug, node.Name + "/y", origin, Row(world, 1), axisLength,
                    selected ? new GraphicsColor(1f, 1f, 1f, 1f) : new GraphicsColor(0.3f, 0.95f, 0.4f, 1f));
                Axis(debug, node.Name + "/z", origin, Row(world, 2), axisLength,
                    selected ? new GraphicsColor(1f, 1f, 1f, 1f) : new GraphicsColor(0.4f, 0.55f, 0.95f, 1f));
            }
        }

        if (!showBounds) return;

        var min = Vector3.Transform(model.BoundsMin, modelTransform);
        var max = Vector3.Transform(model.BoundsMax, modelTransform);
        debug.Draw.Aabb("bounds", Vector3.Min(min, max), Vector3.Max(min, max),
            new GraphicsColor(0.9f, 0.85f, 0.4f, 0.9f));

        if (selectedNode >= 0 && selectedNode < model.Nodes.Count)
        {
            var node = model.Nodes[selectedNode];
            if (node.PrimitiveCount > 0)
            {
                var lo = Vector3.Transform(node.BoundsMin, modelTransform);
                var hi = Vector3.Transform(node.BoundsMax, modelTransform);
                debug.Draw.Aabb("selected", Vector3.Min(lo, hi), Vector3.Max(lo, hi),
                    new GraphicsColor(1f, 1f, 1f, 1f));
            }
        }
    }

    private static void Axis(
        DebugContext debug, string name, Vector3 origin, Vector3 direction, float length, GraphicsColor colour)
    {
        var d = direction.LengthSquared() > 1e-8f ? Vector3.Normalize(direction) : Vector3.UnitX;
        debug.Draw.Line(name, origin, origin + (d * length), colour);
    }

    // Row i of the row-vector matrix: the node's local axis in world space.
    private static Vector3 Row(Matrix4x4 m, int i) => i switch
    {
        0 => new Vector3(m.M11, m.M12, m.M13),
        1 => new Vector3(m.M21, m.M22, m.M23),
        _ => new Vector3(m.M31, m.M32, m.M33),
    };

    private static float ScaleOf(Matrix4x4 m) =>
        new Vector3(m.M11, m.M12, m.M13).Length();

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
        if (model is not null)
        {
            ImGui.Checkbox("node pivots", ref showPivots);
            ImGui.SameLine();
            ImGui.Checkbox("bounds", ref showBounds);
            if (showPivots) ImGui.Checkbox("include transform-only nodes", ref showAllPivots);

            DrawNodeList();
        }

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
    public void Dispose()
    {
        model?.Dispose();
        renderer.Dispose();
    }
}
