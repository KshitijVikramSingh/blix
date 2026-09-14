using System.Numerics;
using Blix;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Labs.Character;
using Blix.Runtime.Silk;
using ImGuiNET;

namespace Blix.Labs.Character.RoomApp;

// The character lab's ROOM: contact, with nothing else in the picture.
//
// ── Proves ──────────────────────────────────────────────────────────────────
//   • One source. What is drawn and what will be swept are the same triangles —
//     Room.Vertices and Room.Collider come out of the same pass, and the flat
//     shading means every facet you can see is a facet the sweep will hit.
//   • A claim you can check from the chair: the slope tint shades every surface by
//     the normal the collider reads, so a mis-wound face reads as a floor standing
//     up rather than as a surface that merely lights oddly.
//   • The lab family generalising: a second library with its own shaders, its own
//     renderer and three roots planned over it, none of which is the toolchain lab.
//
// ── Intentionally owns ──────────────────────────────────────────────────────
//   • Camera feel, what the panel shows, which part is selected.
//   • Nothing about movement. There is no body here yet, deliberately: a room that
//     had a character in it could not tell you whether a fault was the room's.
public static class Program
{
    public static void Main(string[] args)
    {
        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Blix — character lab: room",
            Width = 1280,
            Height = 760,
        });

        var loop = new RoomLoop();
        using var window = new Window(loop, options);
        window.Run();
    }
}

internal sealed class RoomLoop : IGameLoop, IDebuggable, IUiSource, IInputHandler, IDisposable
{
    private readonly RoomRenderer renderer = new();
    private readonly RoomCamera camera = new();
    private readonly Room room = Room.Build();

    private IRenderHost? host;
    private Matrix4x4 viewProjection = Matrix4x4.Identity;
    private float aspect = 16f / 9f;
    private int frames;

    private bool orbiting;
    private bool panning;

    // ── The body ────────────────────────────────────────────────────────────
    // <b>This owns the input and nothing else.</b> Where the body ends up is BodyResolver's, and
    // what it does about ground, slopes and steps is CharacterMotor's — and every number behind
    // that is on the panel, because the numbers are what a lab is for and one that hard-codes them
    // can only confirm the guess it was built with.
    private readonly CharacterMotor motor = new();
    private float bodyFacing;
    private Vector3 lastTravel;

    /// <summary>
    /// Whether the body turns toward where it is GOING or toward where the camera is LOOKING.
    /// </summary>
    /// <remarks>
    /// <b>Two different games, and the lab should not have to pick one.</b> Facing the travel
    /// direction is the Zelda/Mario model: press right and the character turns right. Facing the
    /// camera is the over-the-shoulder shooter model: the character always points away from the
    /// camera and strafes sideways. They are identical while walking straight forward and disagree
    /// completely the moment you strafe — which is exactly when "the body's facing looks wrong
    /// relative to the camera" gets reported, and neither answer is a bug.
    /// </remarks>
    private bool faceCamera = true;
    // Fast enough that the drawn facing keeps up with a camera being swung about: at 12 rad/s a
    // steady drag left the arrow a constant 15-20° behind where the body was actually going, which
    // reads as the facing being wrong rather than as lag. The slider stays, because dialling it
    // DOWN to see a body turn like a vehicle is a thing the Motion lab will want.
    private float turnRate = 20f;
    private bool bodyEnabled = true;
    private readonly HashSet<Key> held = new();

    private int selected = -1;
    private bool showNormals;
    private bool showBounds = true;
    private bool showGrid = true;
    private bool depthTestGizmos = true;
    private float slopeTint;

    // Cached per selection rather than per frame: the normals of a part do not change, and
    // rebuilding two lists every frame to draw the same arrows is work a lab can see in its own
    // frame time and then misattribute to the renderer.
    private readonly List<Vector3> normalPoints = new();
    private readonly List<Vector3> normalDirections = new();

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        motor.Teleport(Blix.Labs.Character.Room.SpawnPoint);
        camera.Rig = CameraRig.ThirdPerson;
        var vk = (VulkanGraphicsDevice)graphicsDevice;
        renderer.Load(vk, Path.Combine(AppContext.BaseDirectory, "Shaders"), room);

        Console.WriteLine($"room — {room.TriangleCount} triangles, {room.Parts.Count} parts, {room.SolidStarts.Count} solids");
        foreach (var part in room.Parts) Console.WriteLine($"  {part.Name,-12} {part.TriangleCount,5} tri  {ClaimOf(part)}");
    }

    public void OnUpdate(Time time)
    {
        renderer.SlopeTint = slopeTint;
        if (bodyEnabled) MoveBody((float)time.Delta);

        // Every rig but Orbit places itself from the body, so "follow" is now a property of the rig
        // rather than a toggle that fights it — which is what the old checkbox was, and why panning
        // had to switch it off.
        camera.Place(motor.Feet, motor.Height, room.Collider);
    }

    /// <summary>Gather the frame's intent and hand it to the motor.</summary>
    private void MoveBody(float deltaSeconds)
    {
        var (forward, right) = camera.GroundBasis;

        var wish = Vector3.Zero;
        if (held.Contains(Key.W)) wish += forward;
        if (held.Contains(Key.S)) wish -= forward;
        if (held.Contains(Key.D)) wish += right;
        if (held.Contains(Key.A)) wish -= right;

        // WHICH WAY THE BODY IS POINTING. The motor has no opinion — a capsule is symmetric and
        // nothing it computes depends on a facing — but a camera behind the shoulder needs one, and
        // so will every clip the Motion lab plays. Turned toward the walk rather than snapped, at a
        // rate that is a lab dial like everything else here.
        // Facing the camera is a standing decision, not a moving one — a shooter's character points
        // away from the camera whether or not it is walking.
        if (faceCamera)
        {
            bodyFacing = camera.Yaw;
        }
        else if (wish.LengthSquared() > 1e-6f)
        {
            var wanted = MathF.Atan2(-wish.X, -wish.Z);
            var delta = MathF.IEEERemainder(wanted - bodyFacing, MathF.Tau);
            bodyFacing += Math.Clamp(delta, -turnRate * deltaSeconds, turnRate * deltaSeconds);
        }

        var before = motor.Feet;
        motor.Step(wish, deltaSeconds, room.Collider);
        lastTravel = motor.Feet - before;

        // A body that leaves the room has found a hole, and chasing it into the void is a worse way
        // to learn that than being put back where it can be watched.
        if (motor.Feet.Y < -4f) motor.Teleport(Blix.Labs.Character.Room.SpawnPoint);
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frames++;
        if (frame.Height > 0) aspect = frame.Width / (float)frame.Height;
        viewProjection = camera.ViewProjection(aspect);
        renderer.Render(commandList, room, viewProjection, camera.Position);
    }

    public void Debug(DebugContext debug)
    {
        debug.State.DepthTestDrawing = depthTestGizmos;
        debug.Values.Value("frames", frames);
        debug.Values.Value("camera", camera.Target);
        debug.Stats.Gauge("triangles", room.TriangleCount);
        debug.Stats.Gauge("solids", room.SolidStarts.Count);
        debug.Values.Value("body", motor.Feet);
        debug.Values.Value("ground", motor.Grounded ? motor.GroundSlopeDegrees : -1f);
        debug.Stats.Gauge("contacts", motor.Contacts.Count);

        var (logicalW, logicalH) = host?.LogicalSize ?? (debug.Frame.Width, debug.Frame.Height);
        var declaration = debug.Draw.Declare(
            "main",
            viewProjection,
            RenderSurfaceHandle.Default,
            new Rect(0f, 0f, logicalW, logicalH),
            new Rect(0f, 0f, debug.Frame.Width, debug.Frame.Height));

        using (debug.Draw.In(declaration))
        {
            // A metre grid, so every claim in the panel has something to be read against: a 0.2 m
            // riser and a 0.3 m one are hard to tell apart by eye and trivial against a ruler.
            if (showGrid)
            {
                debug.Draw.Grid("floor", new Vector3(0f, 0.01f, 0f), 28f, 28, new GraphicsColor(0.35f, 0.38f, 0.42f, 1f));
            }

            if (selected >= 0 && selected < room.Parts.Count)
            {
                var part = room.Parts[selected];
                var (min, max) = BoundsOf(part);
                if (showBounds) debug.Draw.Aabb("selected", min, max, new GraphicsColor(1f, 0.82f, 0.25f, 1f));

                // THE WINDING, DRAWN. The probe checks arithmetically that every face's winding
                // agrees with the normal it declares; this is the same fact at a glance. An arrow
                // pointing into a solid is a contact normal that will push a body the wrong way.
                if (showNormals)
                {
                    debug.Draw.Normals(
                        "face-normals", normalPoints, normalDirections, 0.45f,
                        new GraphicsColor(0.35f, 0.9f, 1f, 1f));
                }
            }

            // THE BODY, and what the resolver did to it. The capsule is the actual collider — the
            // same one handed to the sweep, not a stand-in — so a gap between it and a surface is a
            // gap the arithmetic believes in.
            if (bodyEnabled && camera.ShowsBody)
            {
                // Blue standing, amber when the ground is too steep to stand on and it is sliding.
                // A slope limit is a number in a panel until it changes the colour of the thing you
                // are driving.
                var body = motor.Body;
                debug.Draw.Capsule(
                    "body", body.PointA, body.PointB, body.Radius,
                    motor.Standing
                        ? new GraphicsColor(0.35f, 0.85f, 1f, 1f)
                        : new GraphicsColor(1f, 0.7f, 0.2f, 1f));

                // TWO ARROWS, because they are two different facts and only their disagreement is
                // interesting. White is the FACING — a smoothed value the motor knows nothing about,
                // which a mesh would be rotated by. Cyan is where the body actually went this frame.
                // A body walking sideways is a perfectly good state; a body whose facing lags its
                // travel by a constant angle is a turn rate too low, and until both were drawn those
                // two looked identical.
                var eye = motor.Feet + new Vector3(0f, 0.9f, 0f);
                var facing = new Vector3(-MathF.Sin(bodyFacing), 0f, -MathF.Cos(bodyFacing));
                debug.Draw.Arrow("facing", eye, eye + (facing * 0.9f), new GraphicsColor(1f, 1f, 1f, 1f));

                var travelled = new Vector3(lastTravel.X, 0f, lastTravel.Z);
                if (travelled.LengthSquared() > 1e-8f)
                {
                    debug.Draw.Arrow(
                        "travel", eye, eye + (Vector3.Normalize(travelled) * 0.7f),
                        new GraphicsColor(0.3f, 0.95f, 1f, 1f));
                }

                // The ground normal, which is what every slope decision actually reads.
                if (motor.Grounded)
                {
                    debug.Draw.Arrow(
                        "ground-normal", motor.Feet, motor.Feet + (motor.GroundNormal * 0.8f),
                        new GraphicsColor(0.5f, 1f, 0.6f, 1f));
                }

                // Each contact the move ran into, with the normal it deflected along. An arrow
                // pointing INTO a surface is the fault this whole stage can produce, and it is the
                // one thing a position alone never shows.
                foreach (var contact in motor.Contacts)
                {
                    debug.Draw.Arrow(
                        "contact", contact.Point, contact.Point + (contact.Normal * 0.6f),
                        new GraphicsColor(1f, 0.45f, 0.2f, 1f));
                }

                // Where it has been. A trail is the cheapest way to see a body juddering against a
                // surface it should be sliding along, which is invisible frame by frame.
                debug.Draw.Trail("body-path", motor.Feet, new GraphicsColor(0.4f, 1f, 0.7f, 1f), 4f);
            }

            // Where a body starts. Drawn now because the spawn point is
            // already a claim the probe checks, and a claim nothing can see is one that rots.
            var spawn = Room.SpawnPoint;
            debug.Draw.Cross("spawn", spawn + new Vector3(0f, 0.05f, 0f), 0.5f, new GraphicsColor(0.4f, 1f, 0.5f, 1f));

            debug.Draw.Arrow(
                "sun", new Vector3(0f, 6f, 0f), new Vector3(0f, 6f, 0f) + renderer.SunDirection * 4f,
                new GraphicsColor(1f, 0.92f, 0.6f, 1f));
        }
    }

    public string DebugName => "room";

    public string UiName => "room";

    public void DrawUi()
    {
        ImGui.Begin("room");

        ImGui.Text($"{room.TriangleCount} triangles · {room.Parts.Count} parts · {room.SolidStarts.Count} solids");
        ImGui.Separator();

        if (ImGui.CollapsingHeader("camera", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // FOUR RIGS, because how a body feels to move is mostly the camera and the same
            // resolver reads completely differently through each. 1-4 on the keyboard.
            foreach (var (label, value) in new[]
            {
                ("orbit (1)", CameraRig.Orbit),
                ("3rd person (2)", CameraRig.ThirdPerson),
                ("first person (3)", CameraRig.FirstPerson),
                ("isometric (4)", CameraRig.Isometric),
            })
            {
                if (ImGui.RadioButton(label, camera.Rig == value)) camera.Rig = value;
            }

            var distance = camera.Distance;
            if (ImGui.SliderFloat("distance", ref distance, 1.2f, 40f)) camera.Distance = distance;

            var shoulder = camera.ShoulderOffset;
            if (ImGui.SliderFloat("shoulder", ref shoulder, -1.5f, 1.5f)) camera.ShoulderOffset = shoulder;

            // The framing dial. Aiming at the chest centres the body with floor below it — a follow
            // camera; aiming over its head pushes it low and fills the frame with the ground it is
            // about to walk into, which is what you want to be watching in a lab about walking.
            var aim = camera.AimHeight;
            if (ImGui.SliderFloat("aim height m", ref aim, 0f, 3f)) camera.AimHeight = aim;

            var fov = camera.FieldOfView * 180f / MathF.PI;
            if (ImGui.SliderFloat("fov deg", ref fov, 35f, 110f)) camera.FieldOfView = fov * MathF.PI / 180f;

            ImGui.Text($"yaw {camera.Yaw:0.00}  pitch {camera.Pitch:0.00}");
        }

        if (ImGui.CollapsingHeader("view", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // The one control worth having before anything moves: it shades every surface by the
            // normal the collider reads, which is how "is this really a 30 degree ramp" becomes a
            // question you can answer by looking.
            ImGui.SliderFloat("slope tint", ref slopeTint, 0f, 1f);
            ImGui.Checkbox("grid", ref showGrid);
            ImGui.SameLine();
            ImGui.Checkbox("bounds", ref showBounds);
            ImGui.SameLine();
            ImGui.Checkbox("normals", ref showNormals);
            ImGui.Checkbox("depth-test gizmos", ref depthTestGizmos);

            var exposure = renderer.Exposure;
            if (ImGui.SliderFloat("exposure", ref exposure, 0.2f, 3f)) renderer.Exposure = exposure;
        }

        if (ImGui.CollapsingHeader("body", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Checkbox("body", ref bodyEnabled);

            // WHICH WAY THE BODY POINTS is a game's decision, not a physics one — and the two models
            // are indistinguishable until you strafe.
            ImGui.Checkbox("face the camera (off = face travel)", ref faceCamera);
            if (!faceCamera) ImGui.SliderFloat("turn rad/s", ref turnRate, 1f, 30f);

            // The number behind "the facing looks wrong": how far the body is turned from where the
            // camera looks. Facing the camera holds it at 0; facing travel swings it to 90 on a pure
            // strafe, which is the model working rather than failing.
            var offset = MathF.IEEERemainder(bodyFacing - camera.Yaw, MathF.Tau) * 180f / MathF.PI;
            ImGui.Text($"facing is {offset:+0.0;-0.0;0.0} deg from the camera");

            // EVERY ONE OF THESE IS A DECISION, which is why none of them is a constant. The ramp
            // fan exists so the slope limit can be dragged across 30, 45 and 60 and the consequence
            // watched rather than argued about.
            var walk = motor.WalkSpeed;
            if (ImGui.SliderFloat("walk m/s", ref walk, 0.5f, 12f)) motor.WalkSpeed = walk;

            var gravity = motor.Gravity;
            if (ImGui.SliderFloat("gravity m/s2", ref gravity, 0f, 40f)) motor.Gravity = gravity;

            var limit = motor.SlopeLimitDegrees;
            if (ImGui.SliderFloat("slope limit deg", ref limit, 0f, 89f)) motor.SlopeLimitDegrees = limit;

            var step = motor.StepHeight;
            if (ImGui.SliderFloat("step height m", ref step, 0f, 1.5f)) motor.StepHeight = step;

            var slide = motor.SlideSpeed;
            if (ImGui.SliderFloat("slide m/s", ref slide, 0f, 15f)) motor.SlideSpeed = slide;

            ImGui.Separator();
            ImGui.Text($"feet {motor.Feet.X:0.00}, {motor.Feet.Y:0.00}, {motor.Feet.Z:0.00}");
            ImGui.Text(motor.Grounded
                ? $"{(motor.Standing ? "standing" : "SLIDING")} on {motor.GroundSlopeDegrees:0.#} deg"
                : "airborne");
            ImGui.Text($"contacts {motor.Contacts.Count}{(motor.SteppedUp ? " - stepped up" : string.Empty)}");

            if (ImGui.Button("respawn")) motor.Teleport(Blix.Labs.Character.Room.SpawnPoint);
        }

        if (ImGui.CollapsingHeader("parts", ImGuiTreeNodeFlags.DefaultOpen))
        {
            for (var i = 0; i < room.Parts.Count; i++)
            {
                var part = room.Parts[i];
                if (ImGui.Selectable($"{part.Name}##part{i}", selected == i))
                {
                    selected = selected == i ? -1 : i;
                    RebuildNormals();
                    if (selected >= 0)
                    {
                        var (min, max) = BoundsOf(room.Parts[selected]);
                        camera.Frame((min + max) * 0.5f, (max - min).Length() * 0.5f);
                    }
                }

                if (!ImGui.IsItemHovered()) continue;
                ImGui.SetTooltip($"{part.TriangleCount} triangles\n{ClaimOf(part)}");
            }
        }

        if (selected >= 0 && selected < room.Parts.Count)
        {
            var part = room.Parts[selected];
            ImGui.Separator();
            ImGui.Text(part.Name);
            ImGui.TextWrapped(ClaimOf(part));

            // MEASURED, not restated. The panel prints what the geometry actually is rather than
            // echoing the claim back — if the two ever differ, this is where it shows without
            // anyone running the probe.
            var (slopeMin, slopeMax) = WalkableSlopeRange(part);
            ImGui.Text(float.IsFinite(slopeMin)
                ? $"measured walkable slope: {slopeMin:0.##}° to {slopeMax:0.##}°"
                : "no walkable faces");
        }

        ImGui.End();
    }

    public void OnMouseDown(MouseButton button)
    {
        if (button == MouseButton.Left) orbiting = true;
        if (button == MouseButton.Right || button == MouseButton.Middle) panning = true;
    }

    public void OnMouseUp(MouseButton button)
    {
        if (button == MouseButton.Left) orbiting = false;
        if (button == MouseButton.Right || button == MouseButton.Middle) panning = false;
    }

    public void OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        if (orbiting)
        {
            camera.Orbit(deltaX, deltaY);
            return;
        }

        // Pan only means anything to the Orbit rig; every other one places itself from the body, and
        // the camera says so rather than the caller having to know.
        if (panning) camera.Pan(deltaX, deltaY);
    }

    public void OnMouseWheel(float offsetX, float offsetY) => camera.Zoom(offsetY);

    public void OnKeyDown(Key key)
    {
        held.Add(key);
        if (key == Key.N) showNormals = !showNormals;
        if (key == Key.G) showGrid = !showGrid;
        if (key == Key.T) slopeTint = slopeTint > 0.5f ? 0f : 1f;
        if (key == Key.Number1) camera.Rig = CameraRig.Orbit;
        if (key == Key.Number2) camera.Rig = CameraRig.ThirdPerson;
        if (key == Key.Number3) camera.Rig = CameraRig.FirstPerson;
        if (key == Key.Number4) camera.Rig = CameraRig.Isometric;
        if (key == Key.R) motor.Teleport(Blix.Labs.Character.Room.SpawnPoint);
    }

    public void OnKeyUp(Key key) => held.Remove(key);

    public void Dispose() => renderer.Dispose();

    private void RebuildNormals()
    {
        normalPoints.Clear();
        normalDirections.Clear();
        if (selected < 0) return;

        var part = room.Parts[selected];
        for (var i = part.FirstTriangle; i < part.FirstTriangle + part.TriangleCount; i++)
        {
            var tri = room.TriangleAt(i);
            normalPoints.Add((tri.V0 + tri.V1 + tri.V2) / 3f);

            // The WOUND normal rather than the stored one, because the wound normal is what a
            // contact will be resolved along. Drawing the stored one would be drawing the claim.
            normalDirections.Add(room.WoundNormal(i));
        }
    }

    private (Vector3 Min, Vector3 Max) BoundsOf(RoomPart part)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = part.FirstTriangle * 3; i < (part.FirstTriangle + part.TriangleCount) * 3; i++)
        {
            min = Vector3.Min(min, room.Positions[i]);
            max = Vector3.Max(max, room.Positions[i]);
        }
        return (min, max);
    }

    private (float Min, float Max) WalkableSlopeRange(RoomPart part)
    {
        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        for (var i = part.FirstTriangle; i < part.FirstTriangle + part.TriangleCount; i++)
        {
            var n = room.WoundNormal(i);
            if (n.Y <= 1e-3f) continue;
            var slope = Room.SlopeDegrees(n);
            min = MathF.Min(min, slope);
            max = MathF.Max(max, slope);
        }
        return (min, max);
    }

    private static string ClaimOf(RoomPart part)
    {
        var claims = new List<string>();
        if (part.WalkSlopeDegrees is { } slope) claims.Add($"every walkable face is {slope:0.##}°");
        if (part.RiserHeight is { } riser) claims.Add($"6 treads, {riser:0.00} m apart");
        if (part.ClearWidth is { } clear) claims.Add($"{clear:0.00} m clear between two solids");
        if (part.TopHeight is { } top) claims.Add($"{(part.Name == "beam" ? "underside" : "top")} at {top:0.00} m");
        if (part.CurvatureRadius is { } radius) claims.Add($"sphere of radius {radius:0.#} — slope is asin(r/R)");
        return claims.Count == 0 ? "no claim" : string.Join("; ", claims);
    }
}
