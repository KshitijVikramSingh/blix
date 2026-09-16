using Blix.Assets;
using System.Numerics;
using Blix;
using Blix.Core;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Graphics.Vulkan;
using Blix.Tools.Studio;
using Blix.Diagnostics;
using Blix.Runtime.Silk;
using Blix.Cooked;

namespace Blix.Tools.Shot;

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
    [BlixApp("shot", Summary = "render a model or rig to a PNG", Headed = true)]
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

        // The negative control. Varied instances SHOULD look different; lockstep ones should differ
        // only by where they stand. A capture that can only ever show "different" proves nothing.
        var lockstep = args.Contains("--lockstep");

        // <b>--viewport reads back the PANEL's target rather than the main scene's.</b> The panel
        // itself is an ImGui window and this tool draws no UI, so without this the second camera
        // could only ever be checked by a person looking at a running window — which is exactly the
        // kind of "verified by eye, once" the capture tool exists to replace.
        var viewport = args.Contains("--viewport");

        // <b>--frames-out N writes N PNGs, one per fixed step.</b> A still frame answers "is this
        // pose right"; it cannot answer "is this MOTION right", which is a question about how one
        // frame follows another. A sequence at a fixed timestep is the smallest thing that can —
        // and because the step is fixed and nothing reads a clock, two runs of the same arguments
        // produce the same files, which is what makes a regression diffable rather than arguable.
        var sequence = int.TryParse(ArgValue(args, "--frames-out"), out var sq) ? Math.Max(0, sq) : 0;

        // --mask-root paints a mask onto the skeleton overlay. It changes no pose: the question it
        // answers is "which bones does a layer rooted here reach, and how softly does it stop", and
        // that is a picture of the MASK rather than of anything the mask was used for.
        // --skeleton-only draws the rig's bones and not its mesh. A mask lives INSIDE a body, and a
        // picture of one through an opaque character is a picture of a character — --xray puts the
        // lines in front of the mesh but does not stop the mesh being the thing you look at.
        var skeletonOnly = args.Contains("--skeleton-only");

        // A mask is a per-bone colour on a skeleton, and at the default framing a wrist is four
        // pixels. Pulling the eye toward the target is the difference between "the legs are grey"
        // being readable and being asserted — so the one camera knob this tool has is the one the
        // mask needed.
        var zoom = float.TryParse(ArgValue(args, "--zoom"), out var zf) && zf > 0.05f ? zf : 1f;

        // --mask-root, matching the name RigAnimation's MaskRoot member derives in the viewer. This
        // tool parses it by hand because it has no session — so the two names agree by care rather
        // than by construction, and that difference is the standing argument for eventually giving
        // it one.
        // <b>The stage self-test: rung four, exercised.</b> The extension hook lets a tool add a
        // pass of its own, and a hook nothing calls is a hook that rots. This declares a trivial
        // pass over the scene colour and asserts its record delegate ran — so the mechanism is
        // checked by something rather than shipped on faith, and deleting the loop that records
        // extensions fails it immediately.
        var stageSelfTest = args.Contains("--stage-selftest");

        var maskRoot = ArgValue(args, "--mask-root");
        var maskFalloff = int.TryParse(ArgValue(args, "--mask-falloff"), out var mf) ? Math.Max(0, mf) : 0;

        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Blix — lab capture",
            Width = 1280,
            Height = 720,
        });

        // A capture is a bounded run by definition. The host honours --frames, so this only
        // supplies a default for when nobody said.
        if (options.ExitAfterFrames <= 0) options = options with { ExitAfterFrames = 8 };

        // A sequence needs a frame per file plus the two the read-back lag costs. Raising the bound
        // here rather than making the caller compute it: "--frames-out 30" should write thirty
        // files, not twenty-eight and a silent truncation.
        if (sequence > 0 && options.ExitAfterFrames < sequence + 4)
        {
            options = options with { ExitAfterFrames = sequence + 4 };
        }

        var loop = new CaptureLoop(
            output, options.ExitAfterFrames, modelPath, rigPath, clipName, clipTime, xray, advance,
            driveRoot, instances, lockstep, viewport, sequence, maskRoot, maskFalloff, skeletonOnly,
            zoom, stageSelfTest, args);
        using (var window = new Window(loop, options))
        {
            window.Run();
        }

        // The stage self-test judges, so it exits non-zero rather than only printing. A mechanism
        // check that reports failure and returns 0 is a mechanism check nobody runs twice.
        if (stageSelfTest)
        {
            if (loop.ExtensionRecords == 0)
            {
                Console.Error.WriteLine(
                    "stage self-test: the extension pass was declared and NEVER recorded — rung four is broken.");
                return 1;
            }

            Console.WriteLine($"stage self-test: the extension pass recorded {loop.ExtensionRecords} frame(s)");
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
    private readonly StudioRenderer renderer = new();
    private readonly string outputPath;
    private readonly int captureOnFrame;

    private VulkanGraphicsDevice device = null!;
    private int frames;
    private Matrix4x4 viewProjection = Matrix4x4.Identity;

    private readonly string? modelPath;
    private StudioModel? model;
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
    private StudioRig? rig;
    private ClipPlayer? player;
    private readonly bool skeletonOnly;
    private readonly bool stageSelfTest;

    // Kept whole so the stage's declared knobs can be applied to the scene once it exists. This
    // tool parses its own flags in Main; the stage's it does not parse at all.
    private readonly string[] args = Array.Empty<string>();
    private int extensionRecords;

    internal int ExtensionRecords => extensionRecords;

    /// <summary>
    /// Rung four, exercised: a pass this tool adds to the stage, and proof that it ran.
    /// </summary>
    /// <remarks>
    /// It draws nothing. What is being checked is the seam — that a tool gets a window to declare a
    /// pass before the graph compiles, and that the stage then records it every frame. A hook with
    /// no consumer is a hook that rots, and this is the cheapest consumer that is honest: it makes
    /// no claim about what an extension would be FOR, only that one is possible.
    /// </remarks>
    /// <summary>
    /// The stage's own knobs, from the same declaration that renders them as a panel elsewhere.
    /// </summary>
    /// <remarks>
    /// <b>Without this, --sun-elevation was accepted and ignored.</b> Declaring a knob on
    /// StudioScene makes a flag exist; it does not make any particular tool read it, and this one
    /// parses its arguments by hand in Main and never built an ObjectTunables at all. A flag that
    /// silently does nothing is worse than one that does not exist, which is how this was found.
    /// </remarks>
    private void ApplyStageKnobs()
    {
        try
        {
            new ObjectTunables(renderer).Apply(args);
        }
        catch (ArgumentException bad)
        {
            Console.Error.WriteLine(bad.Message);
            Environment.Exit(1);
        }
    }

    private void ExtendStage(StudioGraph stage)
    {
        // Declared here because this is the only window in which a pass CAN be declared. Recorded
        // elsewhere, every frame, because the stage offers no hook for that and needs none — see
        // OnRender below.
        selfTestPass = stage.Graph.GraphicsPass("shot.selftest")
            .Target(stage.SceneColour, LoadOp.Load, StoreOp.Store)
            .Shader(stage.Lit)
            .Handle;
    }

    private PassHandle selfTestPass;
    private readonly float zoom;
    private readonly string? maskRoot;
    private readonly int maskFalloff;
    private BoneMask? mask;
    private BonePaletteSet? palettes;
    private readonly List<string> visibleAttachments = new();
    private Matrix4x4[] boneWorlds = Array.Empty<Matrix4x4>();
    private Matrix4x4 rigTransform = Matrix4x4.Identity;
    private readonly int instanceCount = 1;
    private readonly List<Matrix4x4> instancePlacements = new();
    private readonly List<Pose> instancePoses = new();

    // A scratch of its own rather than reusing `boneWorlds`, which is instance 0's and is read by
    // the skeleton overlay after the rig view is built. One buffer is still enough because RigView
    // asks for one body at a time and is finished before asking for the next.
    private Matrix4x4[] instanceWorlds = Array.Empty<Matrix4x4>();
    private readonly bool lockstep;
    private readonly bool viewport;
    private readonly int sequence;
    private int sequenceWritten;
    private readonly List<ClipPlayer> sequencePlayers = new();

    // The same 17 ms --advance uses, and off-rate for the same reason: the Rogue's clips are
    // authored at 30 fps, so a sixtieth divides most of them and every loop seam would land exactly
    // on a frame boundary — the one case a wrap bug cannot show itself in.
    private const double SequenceStep = 0.017;
    private readonly List<string> instanceClips = new();
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
        int instances = 1,
        bool lockstep = false,
        bool viewport = false,
        int sequence = 0,
        string? maskRoot = null,
        int maskFalloff = 0,
        bool skeletonOnly = false,
        float zoom = 1f,
        bool stageSelfTest = false,
        string[]? args = null)
    {
        this.args = args ?? Array.Empty<string>();
        this.skeletonOnly = skeletonOnly;
        this.stageSelfTest = stageSelfTest;
        this.zoom = zoom;
        this.maskRoot = maskRoot;
        this.maskFalloff = maskFalloff;
        this.viewport = viewport;
        this.sequence = sequence;
        instanceCount = Math.Clamp(instances, 1, StudioRig.MaxInstances);
        this.lockstep = lockstep;
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
        renderer.Load(
            device,
            Path.Combine(AppContext.BaseDirectory, "Shaders"),
            stageSelfTest ? ExtendStage : null);

        LoadRig();

        if (modelPath is null || !File.Exists(modelPath)) return;
        // <b>The same catch the judge has, for the same reason.</b> AssetImportException is the
        // engine refusing a file by name; anything else escaping here is a fault in this tool.
        // Without it a bad asset took the whole process down with a stack trace AFTER the window
        // had opened — which reads as "the viewer is broken" rather than "that file is not a glTF".
        try
        {
            model = StudioModel.Load(device, modelPath);
        }
        catch (AssetImportException refused)
        {
            Console.Error.WriteLine($"blix cannot read this: {refused.Message}");
            Environment.Exit(1);
        }
        ApplyStageKnobs();
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

        try
        {
            rig = StudioRig.Load(device, rigPath, renderer.SkinnedProgram);
        }
        catch (AssetImportException refused)
        {
            Console.Error.WriteLine($"blix cannot read this: {refused.Message}");
            Environment.Exit(1);
        }
        ApplyStageKnobs();

        player = new ClipPlayer(rig.Skeleton);

        if (maskRoot is not null)
        {
            try
            {
                mask = BoneMask.Subtree(rig.Skeleton, maskRoot, 1f, maskFalloff);
                Console.WriteLine(
                    $"mask from '{maskRoot}' falloff {maskFalloff}: reaches {mask.Reach()} of " +
                    $"{rig.Skeleton.BoneCount} bones, {mask.Reach(0.999f)} fully");
            }
            catch (ArgumentException ex)
            {
                // Loud rather than an empty overlay: a capture of a mask that silently covered
                // nothing is a picture of a skeleton, and it looks like a working one.
                Console.Error.WriteLine(ex.Message);
                Environment.Exit(1);
            }
        }
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
        instanceWorlds = new Matrix4x4[rig.Skeleton.BoneCount];

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--attach") continue;
            if (rig.Attachments.Any(a => string.Equals(a.Name, args[i + 1], StringComparison.Ordinal)))
            {
                visibleAttachments.Add(args[i + 1]);
            }
            else
            {
                Console.Error.WriteLine(
                    $"No attachment named '{args[i + 1]}'. This rig has: " +
                    (rig.Attachments.Count == 0 ? "none" : string.Join(", ", rig.Attachments.Select(a => a.Name))));
            }
        }

        // <b>One palette per body, packed into one buffer at a known stride.</b> Each instance is the
        // same clip at a different phase, which is what makes the picture evidence: three bodies in
        // the same pose would prove only that three draws happened.
        palettes = new BonePaletteSet(rig.Skeleton.BoneCount, StudioRig.MaxInstances);
        var spacing = MathF.Max(1.2f, rig.LongestExtent * scale * 0.75f);
        var half = (instanceCount - 1) * 0.5f;
        for (var i = 0; i < instanceCount; i++)
        {
            var pose = rig.Skeleton.CreateRestPose();
            var label = player.Clip?.Name ?? "(rest)";
            if (i == 0)
            {
                pose.CopyFrom(player.Pose);
                sequencePlayers.Add(player);
            }
            else if (lockstep)
            {
                // Same clip, same instant. These must come out identical apart from where they stand.
                var echo = new ClipPlayer(rig.Skeleton, player.Clip);
                echo.ScrubTo(player.Time);
                if (driveRoot) RootMotion.Strip(rig.Skeleton, echo.Pose, echo.RestPose);
                pose.CopyFrom(echo.Pose);
                sequencePlayers.Add(echo);
            }
            else
            {
                // <b>A different CLIP, not merely a different phase.</b> Staggering one clip proves
                // the phases are independent; it cannot prove the clips are, because there is only
                // one. Taken in order from the rig's own list so the picture is reproducible.
                var clip = rig.Clips.Count > 0 ? rig.Clips[ClipIndexFor(rig, player.Clip, i)] : null;
                label = clip?.Name ?? "(rest)";
                var echo = new ClipPlayer(rig.Skeleton, clip);
                echo.ScrubTo(i / (double)instanceCount * echo.Duration);
                if (driveRoot) RootMotion.Strip(rig.Skeleton, echo.Pose, echo.RestPose);
                pose.CopyFrom(echo.Pose);
                sequencePlayers.Add(echo);
            }

            var placement = Matrix4x4.CreateTranslation((i - half) * spacing, 0f, 0f) * rigTransform;
            palettes.Add(rig.Skeleton, pose, rig.MeshNodeTransform * placement);
            instancePlacements.Add(placement);
            instancePoses.Add(pose);
            instanceClips.Add(label);
        }

        rowWidth = (instanceCount - 1) * spacing;

        // <b>The verdict, printed.</b> A PNG shows bodies; it cannot show whether they came from
        // different slices or from one slice read N times that happened to look plausible.
        //
        // Fingerprinted WITHOUT the placement. The placement is baked into each drawn palette, so
        // three bodies standing a metre apart have different matrices whatever their poses are — a
        // verdict taken from the drawn slices reads "all different" even under lockstep, which makes
        // the control useless. The question is whether the POSES differ, so the poses are what gets
        // hashed, each at identity.
        if (instanceCount > 1)
        {
            var bare = new BonePaletteSet(rig.Skeleton.BoneCount, instanceCount);
            foreach (var p in instancePoses) bare.Add(rig.Skeleton, p, Matrix4x4.Identity);

            var distinctPoses = 0;
            var seen = new HashSet<ulong>();
            for (var i = 0; i < bare.Count; i++)
            {
                if (seen.Add(bare.Fingerprint(i))) distinctPoses++;
            }

            var expectation = lockstep
                ? distinctPoses == 1
                    ? "one pose in every slot, as the control requires"
                    : $"CONTROL FAILED — one clip at one instant produced {distinctPoses} poses"
                : distinctPoses == bare.Count
                    ? "every body holds a different pose"
                    : $"ALIASED — {bare.Count} bodies hold only {distinctPoses} distinct pose(s)";

            Console.WriteLine($"  {bare.Count} instance(s), {(lockstep ? "LOCKSTEP" : "varied")}: {expectation}");
            for (var i = 0; i < bare.Count; i++)
            {
                Console.WriteLine(
                    $"    [{i}] {instanceClips[i],-30} pose {bare.Fingerprint(i):x16}  " +
                    $"drawn {palettes.Fingerprint(i):x16}");
            }
        }

        StudioRig.ComputeBoneWorlds(rig.Skeleton, player.Pose, boneWorlds);

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
        // The rig's OWN half-height, not a constant that happened to suit one asset. A hardcoded
        // 1.4 m aims over the head of anything shorter, and --zoom then magnifies empty air: the
        // first mask capture centred on the sky with the skeleton falling off the bottom edge.
        var target = rig is not null
            ? new Vector3(0f, rigDrawnHeight * 0.5f, 0f)
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
        // Applied LAST, so it composes with every framing rule above rather than replacing one.
        eye = target + ((eye - target) / zoom);

        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        viewProjection = view * GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3.2f, aspect, 0.1f, 120f);

        if (rig is not null && palettes is not null) rig.UploadPalettes(palettes);

        // The panel camera looks from the opposite side, so a --viewport capture and a plain one of
        // the same arguments are visibly two cameras rather than one picture twice.
        var panelEye = new Vector3(-eye.X, eye.Y, -eye.Z);
        var panelView = Matrix4x4.CreateLookAt(panelEye, target, Vector3.UnitY)
                        * GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3.2f, aspect, 0.1f, 120f);

        var views = new List<IStudioView>();
        if (model is not null) views.Add(new ModelView(model, modelTransform));
        if (!skeletonOnly && rig is not null)
        {
            // Same wiring as the viewer, because a capture that could not show an attachment would
            // make the one instrument that produces evidence blind to the thing being added.
            var rigView = new RigView(rig, palettes?.Count ?? 0)
            {
                BoneWorlds = boneWorlds,
                Placement = rigTransform,
                // Same wiring as the viewer: each body carries its gear at its own pose, so an
                // --instances capture shows N bodies armed rather than N bodies and one weapon.
                // This tool keeps its own players rather than a RigAnimation, so the delegate is built
                // here from its instance poses instead of handed over.
                InstanceBoneWorlds = instancePoses.Count == 0 ? null : InstanceWorldsFor,
                Placements = instancePlacements,
            };
            foreach (var name in visibleAttachments) rigView.VisibleAttachments.Add(name);
            views.Add(rigView);
        }

        // The self-test's own pass, recorded by this tool rather than by the stage. Any point
        // before Render will do: RenderGraph.Pass stores a scope against a handle and Execute walks
        // declaration order, so this runs after the stage's passes because that is where it was
        // declared — not because of where this line sits.
        if (stageSelfTest) renderer.Graph.Pass(selfTestPass, _ => extensionRecords++);

        renderer.Render(
            commandList, viewProjection, eye, views,
            viewport ? panelView : null, panelEye);

        // Debug() runs BEFORE this in the frame, so it annotates with whatever was stored last time
        // round. Storing the matrix the read-back target is actually drawn through — rather than
        // the main camera's — is what stops a --viewport capture's skeleton from being drawn in the
        // wrong place by exactly the difference between two cameras.
        viewProjection = viewport ? panelView : viewProjection;

        // Captured after the frame this call records has been executed — so the read
        // happens on the NEXT OnRender, when the target holds a finished picture rather
        // than one being built.
        frames++;

        // <b>A sequence advances the clip between frames; a single capture never does.</b> Same
        // fixed step --advance uses, for the same reason: nothing here reads a wall clock, so the
        // Nth file of a run is the Nth file of every run with those arguments.
        //
        // It starts at frame 2 rather than at captureOnFrame. That field means "the LAST frame of
        // the run", which is the right moment for one picture and the wrong one for many — deriving
        // the sequence's start from it wrote nothing at all, because the run ended on the frame the
        // first file was due.
        if (sequence > 0 && sequenceWritten < sequence)
        {
            // Read back the frame BEFORE this one — the target holds a finished picture only after
            // its pass has executed, which is the same reason a single capture fires one frame late.
            if (frames >= 2) CaptureSequenceFrame();
            AdvanceSequence();
            return;
        }

        if (frames == captureOnFrame + 1 && Written is null) Capture();
    }

    // One step of the sequence: move every clock on by the fixed step and re-pose every instance.
    // The placements are rebuilt too, so a driven root moves the body between files.
    private void AdvanceSequence()
    {
        if (rig is null || player is null || palettes is null) return;

        player.Advance(SequenceStep);
        rootTravel += Vector3.TransformNormal(player.RootDelta.Translation, rig.MeshNodeTransform);

        palettes.Reset();
        for (var i = 0; i < instancePoses.Count; i++)
        {
            var pose = instancePoses[i];
            if (i == 0)
            {
                pose.CopyFrom(player.Pose);
            }
            else
            {
                // Echoes advance on their own clocks, exactly as they do in the viewer — a sequence
                // where only the subject moved would show the instancing frozen and read as a bug.
                sequencePlayers[i].Advance(SequenceStep);
                pose.CopyFrom(sequencePlayers[i].Pose);
            }

            if (driveRoot) RootMotion.Strip(rig.Skeleton, pose, player.RestPose);
            var placement = driveRoot && i == 0
                ? Matrix4x4.CreateTranslation(rootTravel) * instancePlacements[i]
                : instancePlacements[i];
            palettes.Add(rig.Skeleton, pose, rig.MeshNodeTransform * placement);
        }

        StudioRig.ComputeBoneWorlds(rig.Skeleton, instancePoses[0], boneWorlds);
    }

    private void CaptureSequenceFrame()
    {
        var path = SequencePath(sequenceWritten);
        if (!WriteImage(path, viewport ? renderer.ViewportColour : renderer.SceneColour)) return;
        sequenceWritten++;
        Written ??= path;
        if (sequenceWritten >= sequence) Console.WriteLine($"wrote {sequenceWritten} frame(s)");
    }

    // "walk.png" + 3 -> "walk.003.png". Zero-padded so the files sort in play order in every tool
    // that lists them, which is the only reason a sequence is easier to read than a folder of names.
    private string SequencePath(int index)
    {
        var directory = Path.GetDirectoryName(outputPath);
        var stem = Path.GetFileNameWithoutExtension(outputPath);
        var extension = Path.GetExtension(outputPath);
        var name = $"{stem}.{index:000}{extension}";
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    public string DebugName => "capture";

    /// <summary>
    /// Draws debug geometry INTO THE SCENE TARGET, so a capture holds it too.
    /// </summary>
    /// <remarks>
    /// The view names <see cref="StudioRenderer.SceneSurface"/> rather than the swapchain, which is what puts
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

        // <b>Whichever target is being read back.</b> A --viewport capture that drew its gizmos into
        // the scene target would come back as a clean render with no overlay at all — the geometry
        // would exist, in the other picture. Naming the surface the capture reads is what keeps the
        // annotation and the image the same image.
        var surface = viewport ? renderer.ViewportSurface : renderer.SceneSurface;
        var width = viewport ? debug.Frame.Width * 0.5f : debug.Frame.Width;
        var height = viewport ? debug.Frame.Height * 0.5f : debug.Frame.Height;
        var declaration = debug.Draw.Declare(
            viewport ? "viewport" : "scene", viewProjection, surface,
            new Rect(0f, 0f, width, height),
            new Rect(0f, 0f, width, height));

        using var view = debug.Draw.In(declaration);

        // Lifted a hair off the ground quad: both at y=0 z-fight, and a patchy grid reads as a
        // rendering fault rather than as two coplanar surfaces.
        debug.Draw.Grid("floor", new Vector3(0f, 0.02f, 0f), 24f, 24, new GraphicsColor(0.35f, 0.4f, 0.5f, 1f));
        debug.Draw.Arrow("sun", renderer.SunDirection * 7f, Vector3.Zero, new GraphicsColor(1f, 0.9f, 0.5f, 1f));

        // The skeleton, through the SAME SkeletonGizmo the viewer uses. That shared call is the
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
                StudioRig.ComputeBoneWorlds(rig.Skeleton, instancePoses[i], worlds);
                SkeletonGizmo.Draw(
                    debug,
                    rig.Skeleton,
                    worlds,
                    rig.MeshNodeTransform * instancePlacements[i],
                    SkeletonGizmo.Options.Default,
                    selectedBone: -1,
                    restWorlds: null,
                    include: rig.DeformHierarchy,
                    mask: mask);
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

    // Instance i's clip: i steps along the rig's own list from whichever clip the subject is on.
    // In order rather than random, so two runs of the same arguments produce the same picture.
    private static int ClipIndexFor(StudioRig rig, AnimationClip? subject, int instance)
    {
        var start = 0;
        for (var i = 0; i < rig.Clips.Count; i++)
        {
            if (!ReferenceEquals(rig.Clips[i], subject)) continue;
            start = i;
            break;
        }

        return (start + instance) % rig.Clips.Count;
    }

    // Rebuilt per call rather than cached, because Debug() runs a handful of times in a bounded run
    // and a 41-matrix walk is not worth a field. Cache it the day a capture has hundreds of bones.
    /// <summary>One instance's bone worlds, into a shared scratch — valid until the next call.</summary>
    /// <remarks>
    /// Mirrors <c>RigAnimation.InstanceBoneWorlds</c>, and carries the same contract: RigView asks for
    /// one body at a time at draw time and is finished with the answer before asking for the next.
    /// Collecting these into an array would give every body the last one's pose.
    /// </remarks>
    private IReadOnlyList<Matrix4x4> InstanceWorldsFor(int instance)
    {
        var at = Math.Clamp(instance, 0, instancePoses.Count - 1);
        StudioRig.ComputeBoneWorlds(rig!.Skeleton, instancePoses[at], instanceWorlds);
        return instanceWorlds;
    }

    private static Matrix4x4[] RestWorlds(StudioRig rig, ClipPlayer player)
    {
        var worlds = new Matrix4x4[rig.Skeleton.BoneCount];
        StudioRig.ComputeBoneWorlds(rig.Skeleton, player.RestPose, worlds);
        return worlds;
    }

    private void Capture()
    {
        if (WriteImage(outputPath, viewport ? renderer.ViewportColour : renderer.SceneColour))
        {
            Written = Path.GetFullPath(outputPath);
        }

        // <b>With a second camera, write BOTH targets.</b> Two cameras rendering one picture is a
        // bug this session hit twice — once because two passes shared a shader program's uniform
        // buffer, once because the line drawer did the same for every view — and BOTH times every
        // instrument said the run was fine. Validation was clean, the draw counts were right, and a
        // capture of either target on its own looked exactly as it should, because each target held
        // the same camera and neither picture could contradict the other.
        //
        // Two files from one run is the instrument that was missing. If they show the same camera,
        // something upstream is sharing state between the two views.
        if (!viewport) return;
        var companion = SceneCompanionPath();
        if (WriteImage(companion, renderer.SceneColour))
        {
            Console.WriteLine($"wrote {Path.GetFullPath(companion)}  (main camera, for comparison)");
        }
    }

    // "panel.png" -> "panel.scene.png".
    private string SceneCompanionPath()
    {
        var directory = Path.GetDirectoryName(outputPath);
        var name = $"{Path.GetFileNameWithoutExtension(outputPath)}.scene{Path.GetExtension(outputPath)}";
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    /// <summary>Reads the target back, tonemaps it, and writes a PNG. False when the read was not usable.</summary>
    /// <remarks>
    /// Shared by the single capture and every frame of a sequence, because a sequence whose frames
    /// were encoded by a second copy of this could disagree with the still — same scene, different
    /// curve — and the whole value of a sequence is that its frames are comparable with each other
    /// and with everything else the tool has ever written.
    /// </remarks>
    private bool WriteImage(string path, TextureHandle source)
    {
        // The viewport target is half the swapchain's size and holds the SECOND camera's picture.
        // Same format as the scene target — deliberately, so both take this one read-back path and
        // the tonemap below applies to either.
        var pixels = device.ReadTexture(source, out var width, out var height, out var format);
        if (format != TextureFormat.Rgba16F)
        {
            Console.Error.WriteLine($"Expected an Rgba16F scene target, got {format}.");
            return false;
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

        PngWriter.WriteRgba8(path, rgba, width, height);
        return true;
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
