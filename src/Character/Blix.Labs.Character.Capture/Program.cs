using System.Numerics;
using Blix;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Graphics.Vulkan;
using Blix.Labs.Character;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Labs.Character.CaptureApp;

// The character lab's capture tool: the room, read back and written to a PNG.
//
// Why a second executable rather than a flag on the viewer. A capture has to be reproducible —
// same arguments, same file, byte for byte — which means it cannot read a clock or take a drag.
// The viewer is the opposite of both. The toolchain lab drew the same line for the same reason,
// and it is the line that makes a picture usable as evidence rather than as an impression.
//
// It reads the HDR SCENE target rather than the swapchain, so the room's render path needs no
// capture-shaped change and the tonemap is applied here on the CPU. A capture therefore holds real
// radiance, and the curve below is the same one the present shader runs.
public static class Program
{
    [BlixApp("room-shot", Summary = "render the room to a PNG", Headed = true)]
    public static void Main(string[] args)
    {
        var output = ArgValue(args, "--out") ?? "room.png";

        // A camera you can put where the question is. A capture of a 28 m hall from one fixed chair
        // answers about a third of what the room is for — the beam's underside and the gap's throat
        // are not visible from anywhere the default camera stands.
        var yaw = Float(args, "--yaw", 0.9f);
        // NaN is the "not given" sentinel, so a rig's own defaults survive unless overridden. A
        // literal default here would silently overwrite every rig with the orbit camera's framing,
        // which is the thing these captures exist to compare.
        var pitch = Float(args, "--pitch", float.NaN);
        var distance = Float(args, "--distance", float.NaN);
        var targetX = Float(args, "--x", 0f);
        var targetZ = Float(args, "--z", 0f);

        // 0 draws each part's own colour, 1 shades every surface by the normal the collider reads.
        var slope = Float(args, "--slope-tint", 0f);

        // <b>A capture of a RIG, which is what makes the rigs comparable at all.</b> Framing is the
        // one thing about a camera that cannot be judged from its parameters — "pitch 0.22, distance
        // 4.2" says nothing about whether the body sits where a third-person camera puts it — and
        // the only honest way to compare four of them is four pictures taken the same way.
        var rig = (ArgValue(args, "--rig") ?? "orbit").ToLowerInvariant() switch
        {
            "third" or "thirdperson" or "3" => CameraRig.ThirdPerson,
            "fps" or "first" or "firstperson" => CameraRig.FirstPerson,
            "iso" or "isometric" => CameraRig.Isometric,
            _ => CameraRig.Orbit,
        };

        // Where the body stands. It is SETTLED by the real motor rather than placed, so the capture
        // shows a body resting where the physics puts it — a picture of a camera framing a body that
        // is floating would be a picture of nothing.
        var bodyX = Float(args, "--body-x", Room.SpawnPoint.X);
        var bodyZ = Float(args, "--body-z", Room.SpawnPoint.Z);

        // <b>--walk drives the body before the picture is taken</b>, at a fixed 1/60 step and with no
        // wall clock anywhere, so the same arguments put it in the same place every run. A still of a
        // settled body shows where the resolver leaves things; only a body that has WALKED shows what
        // the resolver did on the way — which iterations fired, what it deflected off, and whether it
        // ended up wedged.
        var walkSeconds = Float(args, "--walk", 0f);
        var walkDegrees = Float(args, "--walk-dir", 0f);

        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Blix — character lab capture",
            Width = 1280,
            Height = 720,
        });

        if (options.ExitAfterFrames <= 0) options = options with { ExitAfterFrames = 8 };

        var loop = new CaptureLoop(
            output, options.ExitAfterFrames, yaw, pitch, distance, targetX, targetZ, slope,
            rig, new Vector3(bodyX, 0f, bodyZ), walkSeconds, walkDegrees);
        using (var window = new Window(loop, options))
        {
            window.Run();
        }

        if (loop.Written is not null)
        {
            Console.WriteLine($"wrote {loop.Written}");
            return;
        }

        Console.Error.WriteLine("nothing was captured.");
        Environment.Exit(1);
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name) return args[i + 1];
        }
        return null;
    }

    private static float Float(string[] args, string name, float fallback) =>
        float.TryParse(ArgValue(args, name), out var v) ? v : fallback;
}

internal sealed class CaptureLoop : IGameLoop, IDebuggable, IDisposable
{
    private readonly RoomRenderer renderer = new();
    private readonly RoomCamera camera = new();
    private readonly Room room = Room.Build();
    private readonly string outputPath;
    private readonly int captureOnFrame;
    private readonly float slopeTint;
    private readonly CameraRig rig;
    private readonly CharacterMotor motor = new();
    private readonly List<Vector3> path = new();
    private Vector3 walked;

    private VulkanGraphicsDevice device = null!;
    private Matrix4x4 viewProjection = Matrix4x4.Identity;
    private float aspect = 16f / 9f;
    private int frames;

    public string? Written { get; private set; }

    public CaptureLoop(
        string outputPath, int exitAfterFrames,
        float yaw, float pitch, float distance, float targetX, float targetZ, float slopeTint,
        CameraRig rig, Vector3 bodyAt, float walkSeconds, float walkDegrees)
    {
        this.outputPath = outputPath;
        this.slopeTint = slopeTint;
        this.rig = rig;

        // Settle the body before anything is framed: 240 fixed steps, no wall clock, so the same
        // arguments put it in the same place every run.
        motor.Teleport(bodyAt + new Vector3(0f, 2f, 0f));
        for (var i = 0; i < 240; i++) motor.Step(Vector3.Zero, 1f / 60f, room.Collider);

        if (walkSeconds > 0f)
        {
            var radians = walkDegrees * MathF.PI / 180f;
            var wish = new Vector3(-MathF.Sin(radians), 0f, -MathF.Cos(radians));
            var steps = (int)MathF.Round(walkSeconds * 60f);

            path.Add(motor.Feet);
            for (var i = 0; i < steps; i++)
            {
                motor.Step(wish, 1f / 60f, room.Collider);
                path.Add(motor.Feet);
            }

            walked = wish;
            Console.WriteLine(
                $"walked {walkSeconds:0.##}s at {walkDegrees:0.#}° -> {motor.Feet}, " +
                $"{motor.Contacts.Count} contact(s) on the last step");
        }

        // One before the last frame: the read-back needs the pass to have executed, and the last
        // frame of the run is the moment after which nothing else will.
        captureOnFrame = Math.Max(1, exitAfterFrames - 1);

        // The rig FIRST, because setting it applies that rig's defaults — and an explicit --pitch or
        // --distance is meant to override those rather than be overwritten by them.
        camera.Rig = rig;
        camera.Yaw = yaw;
        if (Explicit(pitch)) camera.Pitch = pitch;
        if (Explicit(distance)) camera.Distance = distance;

        if (rig == CameraRig.Orbit) camera.Target = new Vector3(targetX, 1.2f, targetZ);
        camera.Place(motor.Feet, motor.Height, room.Collider);
    }

    public string DebugName => "room-capture";

    private static readonly Vector3 Up = new(0f, 0.04f, 0f);

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        device = (VulkanGraphicsDevice)graphicsDevice;
        renderer.Load(device, Path.Combine(AppContext.BaseDirectory, "Shaders"), room);
        renderer.SlopeTint = slopeTint;

    }

    public void OnUpdate(Time time) { }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frames++;
        if (frame.Height > 0) aspect = frame.Width / (float)frame.Height;
        viewProjection = camera.ViewProjection(aspect);
        renderer.Render(commandList, room, viewProjection, camera.Position);
    }

    /// <summary>
    /// Draws the body INTO THE SCENE TARGET, so the capture holds it too.
    /// </summary>
    /// <remarks>
    /// Naming <see cref="RoomRenderer.SceneSurface"/> rather than the swapchain is what puts these
    /// lines inside the image that gets read back. Without it a rig capture would be a clean render
    /// of a room with no body in it — which is a picture of everything except the thing being judged.
    /// </remarks>
    public void Debug(DebugContext debug)
    {
        debug.Values.Value("frames", frames);

        // The same report the viewer publishes, so a dump from a capture and a dump from a window
        // are the same fields in the same units and a difference between them is a real difference.
        LabReport.Publish(
            debug, room, camera, motor, camera.Yaw, LabReport.FacingRule.CameraHeading, walked);

        var declaration = debug.Draw.Declare(
            "scene", viewProjection, renderer.SceneSurface,
            new Rect(0f, 0f, debug.Frame.Width, debug.Frame.Height),
            new Rect(0f, 0f, debug.Frame.Width, debug.Frame.Height));

        using (debug.Draw.In(declaration))
        {
            if (rig != CameraRig.FirstPerson)
            {
                var body = motor.Body;
                debug.Draw.Capsule(
                    "body", body.PointA, body.PointB, body.Radius,
                    new GraphicsColor(0.35f, 0.85f, 1f, 1f));

                // FACING THE CAMERA, the same rule the viewer's default uses — so a capture answers
                // "which way does the marker point" with exactly the picture the viewer would draw.
                var facing = new Vector3(-MathF.Sin(camera.Yaw), 0f, -MathF.Cos(camera.Yaw));
                var side = new Vector3(-facing.Z, 0f, facing.X);

                var eye = motor.Feet + new Vector3(0f, 0.9f, 0f);
                debug.Draw.Arrow("facing", eye, eye + (facing * 0.9f), new GraphicsColor(1f, 1f, 1f, 1f));

                var nose = motor.Feet + (facing * 0.85f) + new Vector3(0f, 0.03f, 0f);
                var tailL = motor.Feet + (facing * -0.15f) + (side * 0.42f) + new Vector3(0f, 0.03f, 0f);
                var tailR = motor.Feet + (facing * -0.15f) - (side * 0.42f) + new Vector3(0f, 0.03f, 0f);
                debug.Draw.Line("chevron-l", tailL, nose, new GraphicsColor(1f, 1f, 1f, 1f));
                debug.Draw.Line("chevron-r", tailR, nose, new GraphicsColor(1f, 1f, 1f, 1f));

                // A second marker whose meaning is unambiguous: a short post at the NOSE only. If the
                // chevron reads backwards, this says which end the lab thinks is the front.
                debug.Draw.Line("nose-post", nose, nose + new Vector3(0f, 0.5f, 0f), new GraphicsColor(0.2f, 1f, 0.4f, 1f));
            }

            // THE PATH IT WALKED, as a line rather than a trail: a trail ages out on a wall clock and
            // a capture has none, so a reproducible run needs the whole path drawn at once.
            for (var i = 1; i < path.Count; i++)
            {
                debug.Draw.Line($"path{i}", path[i - 1] + Up, path[i] + Up, new GraphicsColor(0.4f, 1f, 0.7f, 1f));
            }

            // WHAT THE RESOLVER DID ON THE LAST STEP: the body where each contact stopped it, the
            // contact normal, and the motion it had left afterwards. Three capsules in a corner is a
            // picture of three deflections; the same corner with one capsule is a picture of nothing.
            foreach (var contact in motor.Contacts)
            {
                var at = motor.Body;
                debug.Draw.Capsule(
                    "swept", at.PointA + contact.At, at.PointB + contact.At, at.Radius,
                    new GraphicsColor(0.6f, 0.6f, 0.75f, 1f));

                debug.Draw.Arrow(
                    "normal", contact.Point, contact.Point + (contact.Normal * 0.6f),
                    new GraphicsColor(1f, 0.45f, 0.2f, 1f));

                if (contact.After.LengthSquared() > 1e-10f)
                {
                    debug.Draw.Arrow(
                        "deflected", contact.Point, contact.Point + (Vector3.Normalize(contact.After) * 0.5f),
                        new GraphicsColor(0.3f, 0.95f, 1f, 1f));
                }
            }

            debug.Draw.Grid("floor", new Vector3(0f, 0.02f, 0f), 28f, 28, new GraphicsColor(0.35f, 0.4f, 0.5f, 1f));
        }

        if (frames == captureOnFrame && Written is null) Capture();
    }

    /// <summary>Was a camera parameter actually given, or is it the sentinel meaning "use the rig's"?</summary>
    private static bool Explicit(float value) => !float.IsNaN(value);

    private void Capture()
    {
        // Waits for the GPU, maps, copies. Wrong for a per-frame path and right for a tool taking
        // one picture — the same trade ReadTexture was added under.
        device.WaitIdle();

        var pixels = device.ReadTexture(renderer.SceneColour, out var width, out var height, out var format);
        if (format != TextureFormat.Rgba16F)
        {
            Console.Error.WriteLine($"Expected an Rgba16F scene target, got {format}.");
            return;
        }

        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var src = i * 8;
            var r = (float)BitConverter.ToHalf(pixels, src);
            var g = (float)BitConverter.ToHalf(pixels, src + 2);
            var b = (float)BitConverter.ToHalf(pixels, src + 4);

            // The same curve room_present.frag runs, applied here instead, then an sRGB encode
            // because the swapchain's sRGB format does that in hardware and a PNG must do it
            // itself. If a capture ever disagrees with the screen, this pair is where to look.
            (r, g, b) = (Aces(r * renderer.Exposure), Aces(g * renderer.Exposure), Aces(b * renderer.Exposure));

            var dst = i * 4;
            rgba[dst] = ToSrgbByte(r);
            rgba[dst + 1] = ToSrgbByte(g);
            rgba[dst + 2] = ToSrgbByte(b);
            rgba[dst + 3] = 255;
        }

        PngWriter.WriteRgba8(outputPath, rgba, width, height);
        Written = outputPath;
    }

    private static float Aces(float x)
    {
        const float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
        return Math.Clamp(x * ((a * x) + b) / ((x * ((c * x) + d)) + e), 0f, 1f);
    }

    private static byte ToSrgbByte(float linear)
    {
        var clamped = Math.Clamp(linear, 0f, 1f);
        var encoded = clamped <= 0.0031308f
            ? clamped * 12.92f
            : (1.055f * MathF.Pow(clamped, 1f / 2.4f)) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(encoded * 255f), 0, 255);
    }

    public void Dispose() => renderer.Dispose();
}
