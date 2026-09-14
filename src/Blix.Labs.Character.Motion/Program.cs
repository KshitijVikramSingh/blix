using System.Numerics;
using Blix;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Labs.Character;
using Blix.Runtime.Silk;
using ImGuiNET;

namespace Blix.Labs.Character.MotionApp;

// The character lab's MOTION subject: clips and states, with nothing to walk into.
//
// ── Proves ──────────────────────────────────────────────────────────────────
//   • A state graph as DATA, evaluated in priority order, with a dwell and an
//     interrupt set — the machinery RTSGame, Runner and Bulwark each rebuilt.
//   • That the failures are reproducible on demand rather than anecdotal: the
//     jitter switch puts a body exactly on a gait threshold, and the dwell and
//     hysteresis switches each turn the flicker back on.
//   • A rig driven entirely through the ENGINE's types — GltfImporter, Skeleton,
//     ClipPlayer, BonePalette — with the lab owning only its own shaders.
//
// ── Intentionally owns ──────────────────────────────────────────────────────
//   • Which clips a state names, the thresholds, the dwell, and the input. All
//     policy, all on the panel, because they are what a lab exists to find.
//   • Nothing about contact. The ground is flat and the body does not collide:
//     a body that walks badly and animates well should be diagnosable without
//     the two being in the same picture.
public static class Program
{
    public static void Main(string[] args)
    {
        var rigPath = ArgValue(args, "--rig")
            ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "Blix.Demos.Runner", "Assets", "models", "Rogue.glb");

        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Blix — character lab: motion",
            Width = 1280,
            Height = 760,
        });

        var loop = new MotionLoop(Path.GetFullPath(rigPath), ArgValue(args, "--trace"));
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

internal sealed class MotionLoop : IGameLoop, IDebuggable, IUiSource, IInputHandler, IDisposable
{
    private readonly RoomRenderer renderer = new();
    private readonly RoomCamera camera = new();
    private readonly Room ground = Room.FlatGround();
    private readonly MotionGraph graph = MotionGraph.Humanoid();
    private readonly MotionMachine machine;
    private readonly string rigPath;
    private readonly string? tracePath;
    private readonly Random jitterSource = new(20260915);

    private MotionRig? rig;
    private ClipPlayer? player;
    private IRenderHost? host;
    private Matrix4x4 viewProjection = Matrix4x4.Identity;
    private float aspect = 16f / 9f;
    private int frames;
    private double seconds;

    private bool orbiting;
    private bool panning;

    // ── The synthetic body ──────────────────────────────────────────────────
    // There is no physics here at all: speed, groundedness and the rest are DIALS. That is the
    // isolation the stage is for — every value the graph reads can be put exactly where a fault
    // lives and held there, which a body being simulated will not do on request.
    private float speed;
    private float commandedSpeed;
    private bool grounded = true;
    private float verticalSpeed;
    private bool act;
    private bool hurt;
    private float hurtFor;

    // THE FAULT, ON A SWITCH. Jitter puts the speed exactly on a gait threshold with a per-frame
    // wobble — the situation every one of RTSGame's three reports describes, which until now could
    // only be reproduced by driving a whole simulation into the right corner.
    private bool jitter;
    private float jitterAround = 0.2f;
    private float jitterBand = 0.02f;

    private float rigScale = 0.9f;
    private float rigYaw;

    public MotionLoop(string rigPath, string? tracePath)
    {
        this.rigPath = rigPath;
        this.tracePath = tracePath;
        machine = new MotionMachine(graph);
    }

    public string DebugName => "motion";

    public string UiName => "motion";

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        var vk = (VulkanGraphicsDevice)graphicsDevice;
        renderer.Load(vk, Path.Combine(AppContext.BaseDirectory, "Shaders"), ground);

        camera.Rig = CameraRig.ThirdPerson;
        camera.Distance = 3.6f;

        if (!File.Exists(rigPath))
        {
            Console.Error.WriteLine($"no rig at {rigPath} — pass --rig <path.glb>");
            return;
        }

        rig = MotionRig.Load(vk, rigPath, renderer.SkinnedProgram);
        player = new ClipPlayer(rig.Skeleton);

        Console.WriteLine(
            $"rig — {Path.GetFileName(rigPath)}: {rig.Skeleton.BoneCount} bones, " +
            $"{rig.Clips.Count} clips, {rig.PrimitiveCount} primitive(s)");

        // WHICH CLIPS THE GRAPH ASKED FOR AND WHICH THE ASSET HAS. A state naming a clip the rig does
        // not carry is a state that shows the rest pose and says nothing, which reads as "the
        // animation is broken" and is really a spelling mistake. Said once, at load, in full.
        foreach (var state in graph.States)
        {
            if (rig.Clip(state.Clip) is null)
            {
                Console.WriteLine($"  state '{state.Name}' wants clip '{state.Clip}' — NOT IN THIS RIG");
            }
        }

        ApplyStateClip();
    }

    public void OnUpdate(Time time)
    {
        var dt = (float)Math.Min(time.Delta, 1.0 / 30.0);
        seconds += dt;

        if (hurtFor > 0f) hurtFor = MathF.Max(0f, hurtFor - dt);
        hurt = hurtFor > 0f;

        // The commanded speed is what you asked for; `speed` is what the graph sees. Jitter is
        // applied HERE rather than inside the machine, because a fault the instrument manufactures
        // is not the same as a fault the instrument has.
        speed = jitter
            ? jitterAround + ((float)jitterSource.NextDouble() * 2f - 1f) * jitterBand
            : commandedSpeed;

        if (!grounded) verticalSpeed -= 20f * dt;

        var before = machine.State;
        machine.Step(new MotionInput(speed, grounded, verticalSpeed, act, hurt), dt);
        if (machine.State != before) ApplyStateClip();

        player?.Advance(dt);
        camera.Place(Vector3.Zero, 1.8f, null);
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frames++;
        if (frame.Height > 0) aspect = frame.Width / (float)frame.Height;
        viewProjection = camera.ViewProjection(aspect);

        // Before recording, because the palette is read at Execute and written here — the draw
        // carries a descriptor set, not a copy of the matrices.
        if (rig is not null && player is not null) rig.UploadPose(player.Pose);

        var model = Matrix4x4.CreateScale(rigScale)
                  * Matrix4x4.CreateRotationY(rigYaw);
        var placement = rig is null ? Matrix4x4.Identity : rig.MeshNodeTransform * model;

        renderer.Render(commandList, ground, viewProjection, camera.Position, rig, placement);
    }

    public void Debug(DebugContext debug)
    {
        debug.Values.Value("frames", frames);

        using (debug.Scope("motion"))
        {
            debug.Values.Value("state", machine.State);
            debug.Values.Value("clip", machine.Current?.Clip ?? "<none>");
            debug.Values.Value("time-in-state", machine.TimeInState);
            debug.Values.Value("changes", machine.Changes);
            debug.Values.Value("changes-per-second", machine.ChangesPerSecond);
            debug.Values.Value("dwell", machine.DwellEnabled ? machine.Dwell : 0f);
            debug.Values.Value("last-why", machine.Last?.Why ?? "<none>");
            debug.Values.Value("speed", speed);
            debug.Values.Value("grounded", grounded);
            debug.Values.Value("acting", act);
        }

        if (machine.Last is { } change && MathF.Abs(machine.TimeInState) < 1e-4f)
        {
            debug.Events.Info($"{change.From} -> {change.To}", change.Why);
        }

        var (logicalW, logicalH) = host?.LogicalSize ?? (debug.Frame.Width, debug.Frame.Height);
        var declaration = debug.Draw.Declare(
            "main", viewProjection, RenderSurfaceHandle.Default,
            new Rect(0f, 0f, logicalW, logicalH),
            new Rect(0f, 0f, debug.Frame.Width, debug.Frame.Height));

        using (debug.Draw.In(declaration))
        {
            debug.Draw.Grid("floor", new Vector3(0f, 0.01f, 0f), 12f, 12, new GraphicsColor(0.35f, 0.38f, 0.42f, 1f));
        }
    }

    public void DrawUi()
    {
        ImGui.Begin("motion");

        if (ImGui.CollapsingHeader("state", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // THE ANSWER TO "THE ANIMATION GLITCHES". State, how long it has been held, and WHICH
            // condition put it there — the third is the one every report needs and no machine that
            // only reports its state can give.
            ImGui.Text($"{machine.State}  ({machine.Current?.Clip ?? "?"})");
            ImGui.Text($"held {machine.TimeInState:0.00}s");
            ImGui.Text(machine.Last is { } last
                ? $"via: {last.From} -> {last.To}  [{last.Why}]{(last.Interrupted ? "  INTERRUPT" : string.Empty)}"
                : "via: nothing yet");

            var rate = machine.ChangesPerSecond;
            ImGui.Text($"{machine.Changes} changes, {rate:0.0}/s{(rate > 4f ? "   FLICKERING" : string.Empty)}");
        }

        if (ImGui.CollapsingHeader("input", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.SliderFloat("speed m/s", ref commandedSpeed, 0f, 5f);
            ImGui.Checkbox("grounded", ref grounded);
            ImGui.SameLine();
            ImGui.Checkbox("acting (E)", ref act);
            if (ImGui.Button("hit it (H)")) hurtFor = 0.2f;

            ImGui.Separator();

            // THE FAULT, MANUFACTURED. Put the speed on a threshold and hold it there.
            ImGui.Checkbox("jitter the speed onto a threshold", ref jitter);
            if (jitter)
            {
                ImGui.SliderFloat("around", ref jitterAround, 0f, 3f);
                ImGui.SliderFloat("band", ref jitterBand, 0f, 0.2f);
            }
        }

        if (ImGui.CollapsingHeader("machine", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var dwell = machine.Dwell;
            if (ImGui.SliderFloat("dwell s", ref dwell, 0f, 1f)) machine.Dwell = dwell;

            // Both controls, on switches, because every claim either of them makes is worth nothing
            // without a run where it is off. A dwell stops a SPIKE; it does not stop a body sitting
            // on a threshold — that takes a band, and the two are separate switches for that reason.
            var dwellOn = machine.DwellEnabled;
            if (ImGui.Checkbox("dwell (off = react instantly)", ref dwellOn)) machine.DwellEnabled = dwellOn;

            if (ImGui.Button("reset")) machine.Reset();
        }

        if (ImGui.CollapsingHeader("transitions, in the order they are tried"))
        {
            foreach (var edge in graph.Transitions)
            {
                var mark = edge.Interrupts ? "!" : " ";
                ImGui.Text($"{mark} {edge.From,-7} -> {edge.To,-7}  {edge.Why}");
            }
        }

        if (ImGui.CollapsingHeader("history", ImGuiTreeNodeFlags.DefaultOpen))
        {
            for (var i = machine.History.Count - 1; i >= 0; i--)
            {
                var h = machine.History[i];
                ImGui.Text($"{h.AtSeconds,6:0.00}  {h.From} -> {h.To}  [{h.Why}]");
            }
        }

        if (ImGui.CollapsingHeader("body"))
        {
            ImGui.SliderFloat("scale", ref rigScale, 0.2f, 3f);
            ImGui.SliderAngle("yaw", ref rigYaw);
            ImGui.Text(rig is null ? "no rig loaded" : $"{rig.Skeleton.BoneCount} bones, {rig.Clips.Count} clips");
        }

        ImGui.End();
    }

    public void OnKeyDown(Key key)
    {
        if (key == Key.E) act = !act;
        if (key == Key.H) hurtFor = 0.2f;
        if (key == Key.Space) { grounded = false; verticalSpeed = 5f; }
        if (key == Key.G) grounded = !grounded;
        if (key == Key.J) jitter = !jitter;
    }

    public void OnMouseDown(MouseButton button)
    {
        if (button == MouseButton.Left) orbiting = true;
        if (button is MouseButton.Right or MouseButton.Middle) panning = true;
    }

    public void OnMouseUp(MouseButton button)
    {
        if (button == MouseButton.Left) orbiting = false;
        if (button is MouseButton.Right or MouseButton.Middle) panning = false;
    }

    public void OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        if (orbiting) camera.Orbit(deltaX, deltaY);
        else if (panning) camera.Pan(deltaX, deltaY);
    }

    public void OnMouseWheel(float offsetX, float offsetY) => camera.Zoom(offsetY);

    public void Dispose()
    {
        rig?.Dispose();
        renderer.Dispose();
    }

    /// <summary>Point the player at whatever clip the current state names.</summary>
    /// <remarks>
    /// <b>A hard cut, deliberately, and only for this stage.</b> Blending is M-B, and putting it in
    /// now would mean the first thing anyone sees is a cross-fade — which hides exactly the pops a
    /// state machine's ordering mistakes produce. See them first, then blend them away.
    /// </remarks>
    private void ApplyStateClip()
    {
        if (rig is null || player is null) return;

        var wanted = machine.Current?.Clip;
        if (wanted is null) return;

        var clip = rig.Clip(wanted);
        if (clip is null) return;

        player.Clip = clip;
        player.Loop = machine.Current?.Loops ?? true;
        player.ScrubTo(0.0);
    }
}
