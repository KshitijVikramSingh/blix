using System.Numerics;
using Blix;
using Blix.Core;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Graphics.Vulkan;
using Blix.Labs.Toolchain;
using Blix.Diagnostics;
using Blix.Runtime.Silk;

namespace Blix.Labs.Toolchain.Capture;

// Renders the lab and writes what it rendered to a PNG.
//
// ── Why this exists ─────────────────────────────────────────────────────────
//   Blix could not read a rendered image back. Not "had no screenshot key" —
//   could not: colour attachments were created without TransferSrcBit, so they
//   were not legal copy sources, and nothing in the backend ever copied an image
//   to a buffer. The absence had already shaped the project. TankArena fits its
//   tank model by eye through the overlay, with a comment saying "Screenshots
//   don't work". And in the view and lab arcs, three rendering bugs — a second
//   view drawn through the wrong camera, an off-screen pass whose output nothing
//   sampled, seven objects stacked at the seventh's transform — were each caught
//   because a person looked at a picture, while four green bounded runs, zero
//   validation errors and healthy draw counts said nothing at all.
//
// ── What it captures ────────────────────────────────────────────────────────
//   The HDR scene target, not the swapchain. That needs no change to the lab's
//   render path, and it means the tonemap is applied HERE — so a capture holds
//   the real radiance and the curve is an offline choice rather than baked in.
public static class Program
{
    public static int Main(string[] args)
    {
        var output = ArgValue(args, "--out") ?? "capture.png";
        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Blix — lab capture",
            Width = 1280,
            Height = 720,
        });

        // A capture is a bounded run by definition. The host honours --frames, so this only
        // supplies a default for when nobody said.
        if (options.ExitAfterFrames <= 0) options = options with { ExitAfterFrames = 8 };

        var loop = new CaptureLoop(output, options.ExitAfterFrames);
        using (var window = new Window(loop, options))
        {
            window.Run();
        }

        if (loop.Written is { } path)
        {
            Console.WriteLine($"wrote {path}");
            return 0;
        }

        Console.Error.WriteLine("nothing was captured.");
        return 1;
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

internal sealed class CaptureLoop : IGameLoop, IDebuggable, IDisposable
{
    private readonly LabRenderer renderer = new();
    private readonly LabScene scene = LabScene.Default();
    private readonly string outputPath;
    private readonly int captureOnFrame;

    private VulkanGraphicsDevice device = null!;
    private int frames;
    private Matrix4x4 viewProjection = Matrix4x4.Identity;

    public CaptureLoop(string outputPath, int captureOnFrame)
    {
        this.outputPath = outputPath;
        this.captureOnFrame = Math.Max(1, captureOnFrame - 1);
    }

    /// <summary>Where the image went, or null if nothing was captured.</summary>
    public string? Written { get; private set; }

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        device = (VulkanGraphicsDevice)graphicsDevice;
        renderer.Load(device, Path.Combine(AppContext.BaseDirectory, "Shaders"));
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        var aspect = frame.Height > 0 ? frame.Width / (float)frame.Height : 16f / 9f;
        var eye = new Vector3(6.4f, 4.8f, 7.6f);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(0f, 1f, 0f), Vector3.UnitY);
        viewProjection = view * GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3.2f, aspect, 0.1f, 120f);

        renderer.Render(commandList, scene, viewProjection, eye);

        // Captured after the frame this call records has been executed — so the read
        // happens on the NEXT OnRender, when the target holds a finished picture rather
        // than one being built.
        frames++;
        if (frames == captureOnFrame + 1 && Written is null) Capture();
    }

    public string DebugName => "capture";

    /// <summary>
    /// Draws debug geometry INTO THE SCENE TARGET, so a capture holds it too.
    /// </summary>
    /// <remarks>
    /// The view names <see cref="LabRenderer.SceneSurface"/> rather than the swapchain, which is what puts
    /// these lines inside the image that gets read back. That was impossible until the line drawer learned
    /// to bake a pipeline per render target — it had exactly one, against the default pass, so debug
    /// geometry was silently swapchain-only and a capture could only ever show the scene without the
    /// engine's opinion of it drawn on top.
    /// <para>
    /// This is the whole point of capturing at all: "what shape did collision actually test?" is answered
    /// by a capsule in the picture, not by a clean render of the thing it was supposed to be wrapping.
    /// </para>
    /// </remarks>
    public void Debug(DebugContext debug)
    {
        var declaration = debug.Draw.Declare(
            "scene", viewProjection, renderer.SceneSurface,
            new Rect(0f, 0f, debug.Frame.Width, debug.Frame.Height),
            new Rect(0f, 0f, debug.Frame.Width, debug.Frame.Height));

        using var view = debug.Draw.In(declaration);

        debug.Draw.Grid("floor", Vector3.Zero, 24f, 24, new GraphicsColor(0.35f, 0.4f, 0.5f, 1f));
        debug.Draw.Arrow("sun", scene.SunDirection * 7f, Vector3.Zero, new GraphicsColor(1f, 0.9f, 0.5f, 1f));

        // A character's collider, standing on the ground where one would. The primitive the
        // whole vocabulary exists for, and the one that drew nothing at all until recently.
        debug.Draw.Capsule(
            "collider",
            new Vector3(0f, 0.45f, 0f), new Vector3(0f, 1.55f, 0f), 0.45f,
            new GraphicsColor(0.4f, 1f, 0.6f, 1f));
    }

    private void Capture()
    {
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

            // The same curve the present shader runs — blix_acesFilm — applied here instead,
            // then an sRGB encode because the swapchain's sRGB format does that in hardware
            // and a PNG has to do it itself. If a capture ever disagrees with the screen,
            // this pair is where to look first.
            (r, g, b) = (Aces(r * renderer.Exposure), Aces(g * renderer.Exposure), Aces(b * renderer.Exposure));

            var dst = i * 4;
            rgba[dst] = ToSrgbByte(r);
            rgba[dst + 1] = ToSrgbByte(g);
            rgba[dst + 2] = ToSrgbByte(b);
            rgba[dst + 3] = 255;
        }

        PngWriter.WriteRgba8(outputPath, rgba, width, height);
        Written = Path.GetFullPath(outputPath);
    }

    private static float Aces(float x)
    {
        const float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
        return Math.Clamp(x * ((a * x) + b) / ((x * ((c * x) + d)) + e), 0f, 1f);
    }

    private static byte ToSrgbByte(float linear)
    {
        var encoded = linear <= 0.0031308f
            ? linear * 12.92f
            : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(encoded * 255f), 0, 255);
    }

    public void Dispose() => renderer.Dispose();
}
