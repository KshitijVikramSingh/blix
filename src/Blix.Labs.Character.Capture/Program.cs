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
    public static void Main(string[] args)
    {
        var output = ArgValue(args, "--out") ?? "room.png";

        // A camera you can put where the question is. A capture of a 28 m hall from one fixed chair
        // answers about a third of what the room is for — the beam's underside and the gap's throat
        // are not visible from anywhere the default camera stands.
        var yaw = Float(args, "--yaw", 0.9f);
        var pitch = Float(args, "--pitch", 0.55f);
        var distance = Float(args, "--distance", 24f);
        var targetX = Float(args, "--x", 0f);
        var targetZ = Float(args, "--z", 0f);

        // 0 draws each part's own colour, 1 shades every surface by the normal the collider reads.
        var slope = Float(args, "--slope-tint", 0f);

        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Blix — character lab capture",
            Width = 1280,
            Height = 720,
        });

        if (options.ExitAfterFrames <= 0) options = options with { ExitAfterFrames = 8 };

        var loop = new CaptureLoop(output, options.ExitAfterFrames, yaw, pitch, distance, targetX, targetZ, slope);
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

    private VulkanGraphicsDevice device = null!;
    private Matrix4x4 viewProjection = Matrix4x4.Identity;
    private float aspect = 16f / 9f;
    private int frames;

    public string? Written { get; private set; }

    public CaptureLoop(
        string outputPath, int exitAfterFrames,
        float yaw, float pitch, float distance, float targetX, float targetZ, float slopeTint)
    {
        this.outputPath = outputPath;
        this.slopeTint = slopeTint;

        // One before the last frame: the read-back needs the pass to have executed, and the last
        // frame of the run is the moment after which nothing else will.
        captureOnFrame = Math.Max(1, exitAfterFrames - 1);

        camera.Yaw = yaw;
        camera.Pitch = pitch;
        camera.Distance = distance;
        camera.Target = new Vector3(targetX, 1.2f, targetZ);
    }

    public string DebugName => "room-capture";

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        device = (VulkanGraphicsDevice)graphicsDevice;
        renderer.Load(device, Path.Combine(AppContext.BaseDirectory, "Shaders"), room);
        renderer.SlopeTint = slopeTint;
        Console.WriteLine($"room — {room.TriangleCount} triangles, {room.Parts.Count} parts, {room.SolidStarts.Count} solids");
    }

    public void OnUpdate(Time time) { }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frames++;
        if (frame.Height > 0) aspect = frame.Width / (float)frame.Height;
        viewProjection = camera.ViewProjection(aspect);
        renderer.Render(commandList, room, viewProjection, camera.Position);
    }

    public void Debug(DebugContext debug)
    {
        debug.Values.Value("frames", frames);
        if (frames == captureOnFrame && Written is null) Capture();
    }

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
