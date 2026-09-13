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
        var modelPath = ArgValue(args, "--model");
        var rigPath = ArgValue(args, "--rig");

        // <b>A pose, named by a clip and a time, is a reproducible picture.</b> That pairing is what
        // makes a capture evidence rather than a screenshot: "Walking_A at 0.35 s looked like this"
        // can be re-rendered on any machine and diffed, where "the walk looked wrong" cannot. The
        // frame count is fixed and nothing here reads the clock, so two runs of the same arguments
        // produce the same bytes.
        var clipName = ArgValue(args, "--clip");
        var clipTime = double.TryParse(ArgValue(args, "--time"), out var parsed) ? parsed : 0.0;

        // <b>--xray, because a skeleton lives inside an opaque mesh.</b> Depth-tested gizmos are the
        // right default — they are what makes a line's position in the scene readable — but they
        // also mean a correct skeleton overlay shows almost nothing: the first capture of the Rogue
        // drew only the root's IK children, which radiate from the feet and read exactly like a
        // collapsed rig. The bones were right and the picture was lying.
        var xray = args.Contains("--xray");

        // <b>--advance turns "does this motion look right" into an artifact.</b> A capture normally
        // samples one pose and never runs the clock, which is what makes it reproducible. This runs
        // the clock a FIXED number of FIXED steps — no wall time anywhere — so it stays reproducible
        // while showing what a clip does over several loops.
        //
        // It exists for the one acceptance criterion a still frame cannot carry: a travelling clip's
        // delta must integrate to a straight line at even spacing across the loop seam. That is a
        // shape, not a number, and the only way to check a shape is to look at one.
        var advance = double.TryParse(ArgValue(args, "--advance"), out var secs) ? secs : 0.0;
        var driveRoot = args.Contains("--drive-root");

        // --instances N is the captured proof that N bodies hold N independent poses from one draw.
        // One palette binding served one pose per frame until recently; three bodies in one PNG at
        // three clip phases is the shape of that gap being closed, and a still frame carries it.
        var instances = int.TryParse(ArgValue(args, "--instances"), out var n) ? n : 1;

        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Blix — lab capture",
            Width = 1280,
            Height = 720,
        });

        // A capture is a bounded run by definition. The host honours --frames, so this only
        // supplies a default for when nobody said.
        if (options.ExitAfterFrames <= 0) options = options with { ExitAfterFrames = 8 };

        var loop = new CaptureLoop(
            output, options.ExitAfterFrames, modelPath, rigPath, clipName, clipTime, xray, advance,
            driveRoot, instances);
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
    private LabScene scene = LabScene.Default();
    private readonly string outputPath;
    private readonly int captureOnFrame;

    private VulkanGraphicsDevice device = null!;
    private int frames;
    private Matrix4x4 viewProjection = Matrix4x4.Identity;

    private readonly string? modelPath;
    private LabModel? model;
    private Matrix4x4 modelTransform = Matrix4x4.Identity;

    private readonly string? rigPath;
    private readonly string? clipName;
    private readonly double clipTime;
    private readonly bool xray;
    private readonly double advance;
    private readonly bool driveRoot;
    private readonly List<Vector3> rootPath = new();
    private Vector3 rootTravel;
    private float rigDrawnHeight = 3f;
    private LabRig? rig;
    private ClipPlayer? player;
    private BonePaletteSet? palettes;
    private Matrix4x4[] boneWorlds = Array.Empty<Matrix4x4>();
    private Matrix4x4 rigTransform = Matrix4x4.Identity;
    private readonly int instanceCount = 1;
    private readonly List<Matrix4x4> instancePlacements = new();
    private readonly List<Pose> instancePoses = new();
    private float rowWidth;

    public CaptureLoop(
        string outputPath,
        int captureOnFrame,
        string? modelPath = null,
        string? rigPath = null,
        string? clipName = null,
        double clipTime = 0.0,
        bool xray = false,
        double advance = 0.0,
        bool driveRoot = false,
        int instances = 1)
    {
        instanceCount = Math.Clamp(instances, 1, LabRig.MaxInstances);
        this.xray = xray;
        this.advance = advance;
        this.driveRoot = driveRoot;
        this.modelPath = modelPath;
        this.rigPath = rigPath;
        this.clipName = clipName;
        this.clipTime = clipTime;
        this.outputPath = outputPath;
        this.captureOnFrame = Math.Max(1, captureOnFrame - 1);
    }

    /// <summary>Where the image went, or null if nothing was captured.</summary>
    public string? Written { get; private set; }

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        device = (VulkanGraphicsDevice)graphicsDevice;
        renderer.Load(device, Path.Combine(AppContext.BaseDirectory, "Shaders"));

        LoadRig();

        if (modelPath is null || !File.Exists(modelPath)) return;
        model = LabModel.Load(device, modelPath);
        scene = LabScene.GroundOnly();
        var extent = model.LongestExtent;
        var scale = extent > 0.001f ? 3f / extent : 1f;
        modelTransform = Matrix4x4.CreateScale(scale)
                         * Matrix4x4.CreateTranslation(0f, -model.BoundsMin.Y * scale, 0f);
        Console.WriteLine(
            $"model: {Path.GetFileName(modelPath)} — {model.Nodes.Count} node(s), {model.Parts.Count} part(s), " +
            $"{model.TexturedPartCount} textured ({model.TextureCount} image(s))");
    }

    // Sampled ONCE, at load, and never advanced. A capture that ran the clock would produce a
    // different picture per run — which is precisely what a capture exists not to do.
    private void LoadRig()
    {
        if (rigPath is null || !File.Exists(rigPath)) return;

        rig = LabRig.Load(device, rigPath, renderer.SkinnedProgram);
        scene = LabScene.GroundOnly();

        player = new ClipPlayer(rig.Skeleton);
        if (clipName is not null)
        {
            player.Clip = rig.Clip(clipName);
            if (player.Clip is null)
            {
                Console.Error.WriteLine(
                    $"No clip named '{clipName}' in {Path.GetFileName(rigPath)}; capturing the rest pose.");
            }
        }

        player.ScrubTo(clipTime);

        var extent = rig.LongestExtent;
        var scale = extent > 0.001f ? 3f / extent : 1f;
        var rigBase = Matrix4x4.CreateScale(scale)
                      * Matrix4x4.CreateTranslation(0f, -rig.BoundsMin.Y * scale, 0f);
        rigTransform = rigBase;
        rigDrawnHeight = MathF.Max(0.5f, (rig.BoundsMax.Y - rig.BoundsMin.Y) * scale);

        // <b>17 ms, and deliberately not a sixtieth.</b> A step that divides the clip length puts
        // every wrap exactly on a seam, where the piecewise travel walk has nothing to do — the line
        // comes out clean whatever the code does. The Rogue's clips are authored at 30 fps, so 1/60
        // divides most of them exactly (a 0.40 s dodge is 24 frames of it) and this instrument would
        // have been blind to the bug it exists for. Off-rate, so every wrap lands mid-step.
        //
        // Same trick, same reason, as the probe's duration/7.37.
        const double Step = 0.017;
        var steps = advance > 0.0 ? (int)Math.Round(advance / Step) : 0;
        for (var i = 0; i < steps; i++)
        {
            player.Advance(Step);
            rootTravel += Vector3.TransformNormal(player.RootDelta.Translation, rig.MeshNodeTransform);
            rootPath.Add(Vector3.Transform(rootTravel, rigBase));
        }

        // Driving means the clip stops moving the body and the transform starts; leaving the root
        // animated as well moves a travelling clip twice. Same pairing the viewer's toggle makes.
        if (driveRoot)
        {
            RootMotion.Strip(rig.Skeleton, player.Pose, player.RestPose);
            rigTransform = Matrix4x4.CreateTranslation(rootTravel) * rigBase;
        }

        boneWorlds = new Matrix4x4[rig.Skeleton.BoneCount];

        // <b>One palette per body, packed into one buffer at a known stride.</b> Each instance is the
        // same clip at a different phase, which is what makes the picture evidence: three bodies in
        // the same pose would prove only that three draws happened.
        palettes = new BonePaletteSet(rig.Skeleton.BoneCount, LabRig.MaxInstances);
        var spacing = MathF.Max(1.2f, rig.LongestExtent * scale * 0.75f);
        var half = (instanceCount - 1) * 0.5f;
        for (var i = 0; i < instanceCount; i++)
        {
            var pose = rig.Skeleton.CreateRestPose();
            if (i == 0)
            {
                pose.CopyFrom(player.Pose);
            }
            else
            {
                // A fraction of the clip, not a fixed number of seconds: a fixed offset puts every
                // body on the same frame of a short clip, which reads as the instancing having failed.
                var echo = new ClipPlayer(rig.Skeleton, player.Clip);
                echo.ScrubTo(player.Time + (i / (double)instanceCount * player.Duration));
                if (driveRoot) RootMotion.Strip(rig.Skeleton, echo.Pose, echo.RestPose);
                pose.CopyFrom(echo.Pose);
            }

            var placement = Matrix4x4.CreateTranslation((i - half) * spacing, 0f, 0f) * rigTransform;
            palettes.Add(rig.Skeleton, pose, rig.MeshNodeTransform * placement);
            instancePlacements.Add(placement);
            instancePoses.Add(pose);
        }

        rowWidth = (instanceCount - 1) * spacing;

        LabRig.ComputeBoneWorlds(rig.Skeleton, player.Pose, boneWorlds);

        Console.WriteLine(
            $"rig: {Path.GetFileName(rigPath)} — {rig.Skeleton.BoneCount} bone(s), {rig.Clips.Count} clip(s), " +
            $"clip '{player.Clip?.Name ?? "(rest)"}' at {player.Time:0.000}s of {player.Duration:0.00}s");
        if (steps > 0)
        {
            Console.WriteLine(
                $"  advanced {steps} x {Step:0.0000}s = {steps * Step:0.000}s " +
                $"({(player.Duration > 0 ? steps * Step / player.Duration : 0):0.00} cycles); " +
                $"root travelled {rootTravel.Length():0.0000} m in rig space, " +
                $"{rootTravel.Length() * scale:0.0000} m as drawn");
        }
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        var aspect = frame.Height > 0 ? frame.Width / (float)frame.Height : 16f / 9f;
        // Pulled in when there is a subject, so it fills the frame rather than sitting in it. A rig
        // is framed higher and closer still: a character's interesting half is above its waist.
        var subject = model is not null || rig is not null;
        var eye = rig is not null
            ? new Vector3(2.6f, 2.0f, 3.4f)
            : subject ? new Vector3(3.4f, 2.4f, 4.2f) : new Vector3(6.4f, 4.8f, 7.6f);

        // A row of bodies needs a camera that can see the row. Framed on one, the third instance is
        // cropped at the edge — and a proof that three poses are independent is worth nothing if the
        // third one is off-screen.
        if (rig is not null && rowWidth > 0f) eye *= 1f + (rowWidth * 0.28f);
        var target = rig is not null
            ? new Vector3(0f, 1.4f, 0f)
            : subject ? new Vector3(0f, 0.7f, 0f) : new Vector3(0f, 1f, 0f);

        // <b>A travelling body needs a camera that knows where it went.</b> Framed on the character,
        // two seconds of root motion walks straight out of shot and the artifact becomes a picture of
        // an ear. Centre on the midpoint of the path and back off by its length, so the whole line and
        // the body at the end of it are both in frame — which is what makes "straight, evenly spaced"
        // something a reader can check rather than something the caption asserts.
        if (rootPath.Count >= 2)
        {
            var lo = rootPath[0];
            var hi = rootPath[^1];
            var centre = (lo + hi) * 0.5f;
            // The BODY's height, not only the path's length: a 1.7 m walk beside a 3 m character
            // framed on the walk alone puts the camera inside the character's knee. Whichever is
            // larger, with room around it.
            var reach = (MathF.Max(Vector3.Distance(lo, hi), rigDrawnHeight) * 1.6f) + 2f;
            target = centre + new Vector3(0f, rigDrawnHeight * 0.35f, 0f);

            // Offset ACROSS the travel, not along it: an eye placed down the line of motion sees the
            // path end-on as a single point, which is the one view that cannot show its spacing.
            var along = hi - lo;
            along.Y = 0f;
            var across = along.LengthSquared() > 1e-6f
                ? Vector3.Normalize(new Vector3(-along.Z, 0f, along.X))
                : new Vector3(0.6f, 0f, 0.8f);
            eye = target + (across * reach) + new Vector3(0f, reach * 0.45f, 0f);
        }
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        viewProjection = view * GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3.2f, aspect, 0.1f, 120f);

        if (rig is not null && palettes is not null) rig.UploadPalettes(palettes);

        renderer.Render(
            commandList, scene, viewProjection, eye, model, modelTransform, rig, palettes?.Count ?? 0);

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
        debug.State.DepthTestDrawing = !xray;

        var declaration = debug.Draw.Declare(
            "scene", viewProjection, renderer.SceneSurface,
            new Rect(0f, 0f, debug.Frame.Width, debug.Frame.Height),
            new Rect(0f, 0f, debug.Frame.Width, debug.Frame.Height));

        using var view = debug.Draw.In(declaration);

        // Lifted a hair off the ground quad: both at y=0 z-fight, and a patchy grid reads as a
        // rendering fault rather than as two coplanar surfaces.
        debug.Draw.Grid("floor", new Vector3(0f, 0.02f, 0f), 24f, 24, new GraphicsColor(0.35f, 0.4f, 0.5f, 1f));
        debug.Draw.Arrow("sun", scene.SunDirection * 7f, Vector3.Zero, new GraphicsColor(1f, 0.9f, 0.5f, 1f));

        // The skeleton, through the SAME LabSkeletonView the viewer uses. That shared call is the
        // whole reason the lab is a library: a capture drawn by its own copy of the overlay could
        // disagree with the window, and a picture that disagrees with the thing it documents is
        // worse than no picture.
        if (rig is not null && player is not null)
        {
            // One skeleton per instance, each scoped so `bone/17` under i0 and under i2 stay
            // distinct. Three skeletons in three shapes is the conclusive form of the proof: three
            // meshes could agree with each other and still be one pose drawn thrice.
            var worlds = new Matrix4x4[rig.Skeleton.BoneCount];
            for (var i = 0; i < instancePlacements.Count; i++)
            {
                using var instanceScope = debug.Scope($"i{i}");
                LabRig.ComputeBoneWorlds(rig.Skeleton, instancePoses[i], worlds);
                LabSkeletonView.Draw(
                    debug,
                    rig.Skeleton,
                    worlds,
                    rig.MeshNodeTransform * instancePlacements[i],
                    LabSkeletonView.Options.Default,
                    selectedBone: -1,
                    restWorlds: null,
                    include: rig.DeformHierarchy);
            }

            // The path, as a polyline. A root-motion bug has a SHAPE: straight with even spacing is
            // right, a stutter once per cycle is the loop wrap handled by subtraction, and a sideways
            // drift is a delta taken in the wrong frame. None of those is visible in a number.
            if (rootPath.Count >= 2)
            {
                debug.Draw.Polyline("root/path", rootPath, new GraphicsColor(0.3f, 0.9f, 0.6f, 1f));
                foreach (var (at, i) in rootPath.Select((v, i) => (v, i)))
                {
                    // A tick per sample, so EVEN SPACING is checkable and not merely asserted — a
                    // clean line drawn at uneven speed looks identical without them.
                    if (i % 6 != 0) continue;
                    debug.Draw.Cross($"root/tick{i}", at, 0.03f, new GraphicsColor(1f, 0.9f, 0.4f, 1f));
                }
            }

            return;
        }

        // A character's collider, standing on the ground where one would. The primitive the
        // whole vocabulary exists for, and the one that drew nothing at all until recently.
        if (model is null)
        {
            debug.Draw.Capsule(
                "collider",
                new Vector3(0f, 0.45f, 0f), new Vector3(0f, 1.55f, 0f), 0.45f,
                new GraphicsColor(0.4f, 1f, 0.6f, 1f));
            return;
        }

        // One axis triad per node, at its composed-world pivot. The claim blix-cook inspect
        // prints, drawn where it can be checked.
        // A tenth of the model's on-screen size: small enough that 96 of them stay readable, large
        // enough to see which way a node's axes point, which is the whole question.
        var size = MathF.Max(0.08f, model.LongestExtent * modelTransform.M11 * 0.1f);
        foreach (var node in model.Nodes)
        {
            var world = node.WorldTransform * modelTransform;
            var origin = new Vector3(world.M41, world.M42, world.M43);
            // Mesh-bearing nodes only. The tank has 96 nodes and 11 meshes — the other 85 are
            // track links and wheel pivots, and a triad on each is noise rather than information.
            if (node.PrimitiveCount == 0) continue;
            var length = size;
            debug.Draw.Line($"{node.Name}/x", origin,
                origin + (Vector3.Normalize(new Vector3(world.M11, world.M12, world.M13)) * length),
                new GraphicsColor(0.95f, 0.3f, 0.3f, 1f));
            debug.Draw.Line($"{node.Name}/y", origin,
                origin + (Vector3.Normalize(new Vector3(world.M21, world.M22, world.M23)) * length),
                new GraphicsColor(0.3f, 0.95f, 0.4f, 1f));
            debug.Draw.Line($"{node.Name}/z", origin,
                origin + (Vector3.Normalize(new Vector3(world.M31, world.M32, world.M33)) * length),
                new GraphicsColor(0.4f, 0.55f, 0.95f, 1f));
        }

        var lo = Vector3.Transform(model.BoundsMin, modelTransform);
        var hi = Vector3.Transform(model.BoundsMax, modelTransform);
        debug.Draw.Aabb("bounds", Vector3.Min(lo, hi), Vector3.Max(lo, hi),
            new GraphicsColor(0.9f, 0.85f, 0.4f, 1f));
    }

    // Rebuilt per call rather than cached, because Debug() runs a handful of times in a bounded run
    // and a 41-matrix walk is not worth a field. Cache it the day a capture has hundreds of bones.
    private static Matrix4x4[] RestWorlds(LabRig rig, ClipPlayer player)
    {
        var worlds = new Matrix4x4[rig.Skeleton.BoneCount];
        LabRig.ComputeBoneWorlds(rig.Skeleton, player.RestPose, worlds);
        return worlds;
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

    public void Dispose()
    {
        rig?.Dispose();
        model?.Dispose();
        renderer.Dispose();
    }
}
