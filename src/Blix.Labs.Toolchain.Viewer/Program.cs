using System.Numerics;
using Blix;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
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
//   • Skeletal animation, made visible: ClipPlayer drives a pose, LabSkeletonView
//     draws it, and RootMotion says where a clip travels. The three composition
//     modes are three ENGINE primitives with no lab-local maths behind them —
//     ClipPlayer, PoseBlend.Lerp, PoseDelta.LayerOnto. The last two had unit tests
//     and no callers until this, which is its own kind of unverified.
//
// ── Intentionally owns ──────────────────────────────────────────────────────
//   • Camera feel, the panel's controls, what the lab scene contains.
//   • Which clip feeds which player, how the blend weight is driven, and whether
//     the root delta drives the model — all policy a game would decide for itself.
public static class Program
{
    public static void Main(string[] args)
    {
        // --model <path> loads a glTF as its authored NODE TREE; --rig <path> loads one as a
        // SKELETON and its clips. Two flags rather than one that guesses, because they are two
        // different questions about an asset and the answer to "which importer" is not something
        // a viewer should infer from whether a file happens to contain a skin.
        var modelPath = ArgValue(args, "--model");
        var rigPath = ArgValue(args, "--rig");

        // Saves hunting through seventy-six clips on every launch when you already know which
        // one you came to look at. Falls back to the preferred-name search when absent, and
        // says so rather than silently playing something else when the name does not match.
        var clipName = ArgValue(args, "--clip");

        // --blend / --additive name the SECOND clip and pick the composition with it, because the
        // mode and the clip are one decision: "blend into a run" is not two settings that happen to
        // agree. It also means a bounded run reaches the blend path at all — without a flag, the
        // only way in is a combo box, and a code path a headless run cannot reach is a code path
        // nothing checks.
        var blendClip = ArgValue(args, "--blend");
        var additiveClip = ArgValue(args, "--additive");

        // --instances N draws N copies of the rig, each on its own clock. One is the ordinary case
        // and takes exactly the same path as eight — there is no single-body shader.
        var instances = int.TryParse(ArgValue(args, "--instances"), out var n) ? n : 1;
        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Blix — toolchain lab",
            Width = 1280,
            Height = 760,
        });

        var loop = new ViewerLoop(modelPath, rigPath, clipName, blendClip, additiveClip, instances);
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

    // ── The rig half ────────────────────────────────────────────────────────
    // Two players rather than one, because a blend is two clips on two clocks. A single-clip
    // view is the degenerate case of that (weight 0), not a separate mode with its own code —
    // which is what stops "it works in single and not in blend" from being possible.
    private readonly string? rigPath;
    private readonly string? clipName;
    private readonly string? secondClip;
    private LabRig? rig;
    private ClipPlayer? playerA;
    private ClipPlayer? playerB;
    private Pose? posed;
    private BonePaletteSet? palettes;

    // <b>Every instance past the first runs its OWN clip on its OWN clock.</b>
    //
    // They were the subject's clip at staggered phases once, which proves phase independence and
    // nothing else: a mechanism that forced every body onto one clip would pass that test by
    // construction, because there was only one clip. A different clip, a different rate and a
    // different phase per body is the claim actually worth making, and it is the one a still frame
    // can carry — three bodies doing three different things.
    //
    // Still not a crowd: no AI, no director, no spawning. The clips are taken in order from the
    // rig's own list starting at the subject's, which is a lab picking something to show rather than
    // policy about what a game's bodies should be doing.
    private int instanceCount = 1;
    private ClipPlayer[] echoes = Array.Empty<ClipPlayer>();
    private readonly List<Matrix4x4> instancePlacements = new();

    // <b>The negative control.</b> Lockstep puts every body on the subject's clip at the subject's
    // time, and they must then be pixel-identical. Without it, "the bodies look different" is
    // evidence only that the poses differ — it cannot tell a working slice from a buffer that
    // happens to hold three different things for some other reason. Two checks that fail in
    // opposite directions are what make either of them mean something.
    private bool lockstep;

    // Set after packing, and shown in the panel so the claim sits next to the thing it describes
    // rather than in a log nobody opens.
    private int distinctPoses = 1;
    private BonePaletteSet? poseCheck;
    private Matrix4x4[] boneWorlds = Array.Empty<Matrix4x4>();
    private Matrix4x4[] restWorlds = Array.Empty<Matrix4x4>();

    // Scratch, rewritten per echo while drawing. One array rather than one per instance: the
    // overlay reads it and is done with it before the next echo overwrites it, which is the whole
    // difference between a draw-time scratch and a recorded payload.
    private Matrix4x4[] echoBoneWorlds = Array.Empty<Matrix4x4>();
    private Matrix4x4 rigBase = Matrix4x4.Identity;
    private Matrix4x4 rigTransform = Matrix4x4.Identity;
    private float rigScale = 1f;
    private int selectedBone = -1;
    private int clipIndexA;
    private int clipIndexB;
    private float blendWeight = 0.5f;
    private PoseMode poseMode = PoseMode.Single;
    private bool showSkeleton = true;
    private bool showRestGhost;
    private bool showAllBoneAxes;
    private bool showJoints = true;
    private bool showLeafStubs = true;
    private bool deformBonesOnly = true;
    private float gizmoScale = 1f;
    private string clipFilter = string.Empty;

    // Root motion, integrated. Translation only: turning a body by a clip's root ROTATION needs a
    // pivot convention (about the root's own origin? about the body's centre?) that no consumer in
    // this tree has asked for, and guessing one produces a rig that spins about the wrong point and
    // looks like a maths bug. The turn is measured and reported; it is just not applied.
    private bool driveRoot;
    private Vector3 rootTravel;

    // <b>Composed, not summed.</b> This was a running total of per-frame turn MAGNITUDES, which is
    // an angular path length and not a turn: a root that wobbles a degree each way reports two
    // degrees a frame and climbs forever. Left running on a clip with a 25-degree-per-cycle root it
    // read "2010.4 degrees turned", which sounds like five and a half revolutions and describes a
    // body that ended up facing where it started.
    //
    // Composing the deltas gives the NET turn, which is what the word means — and the path is kept
    // beside it, because "wobbled a lot and went nowhere" is also worth being able to see.
    private Quaternion rootTurn = Quaternion.Identity;
    private float rootTurnPathDegrees;
    private readonly List<Vector3> rootPath = new();
    private bool showRootTrail = true;

    private enum PoseMode
    {
        Single,
        Blend,
        Additive,
    }

    public ViewerLoop(
        string? modelPath = null,
        string? rigPath = null,
        string? clipName = null,
        string? blendClip = null,
        string? additiveClip = null,
        int instances = 1)
    {
        this.modelPath = modelPath;
        this.rigPath = rigPath;
        this.clipName = clipName;
        instanceCount = Math.Clamp(instances, 1, LabRig.MaxInstances);
        secondClip = blendClip ?? additiveClip;
        if (blendClip is not null) poseMode = PoseMode.Blend;
        else if (additiveClip is not null) poseMode = PoseMode.Additive;
    }


    private float yaw = 0.7f;
    private float pitch = 0.45f;
    private float distance = 11f;
    private bool dragging;

    // <b>A click is a press that did not become a drag.</b> Left-drag orbits, so selecting on the
    // press would fight the camera and selecting on every release would fire at the end of every
    // orbit. The pointer's travel since the press decides which gesture it was — and the press
    // already decides who owns it, which GestureOwnership settled one layer down.
    private Vector2 pressPosition;
    private float pressTravel;
    private Vector2 pointer;

    // The view the scene was drawn through, kept so a pointer can be turned into a ray through it.
    // A ViewDeclaration is a camera and a rectangle with a name, which is exactly what picking needs
    // and exactly what Camera3D.ScreenPointToRay cannot be handed.
    private ViewDeclaration? mainView;

    // The panel view, declared each frame with last frame's image rectangle. Kept so the panel can
    // turn a pointer into a ray during layout, which is the only moment ImGui will let it.
    private ViewDeclaration? viewportView;

    private Matrix4x4 viewProjection = Matrix4x4.Identity;
    private Vector3 cameraPosition;
    private float sunYaw = 0.5f;
    private float sunPitch = 0.9f;
    private bool showTrail = true;

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

    // Kept because the host is the only thing that knows the backing scale, and a view that will be
    // PICKED through needs its logical rectangle to match the coordinates a pointer arrives in.
    private IRenderHost? host;

    // Mirrors DebugState.DepthTestDrawing so the panel can flip it. Applied in Debug(), which is
    // the only place with a DebugContext to hand.
    private bool depthTestGizmos = true;

    // ── Stage A of the view arc ─────────────────────────────────────────────
    // Ids the HOST gave us for textures the UI can draw. Registered once at load rather than per
    // frame: an id is a dictionary entry, and minting one every frame would grow that dictionary
    // forever for a picture that never changed.
    private readonly List<(string Label, nint Id, int Width, int Height)> uiImages = new();
    private nint shadowMapId;
    private float thumbnailScale = 1f;

    // ── Stage B of the view arc: a second camera, shown in a panel ───────────
    // Its own yaw/pitch/distance, because a viewport that follows the main camera is a mirror and
    // proves nothing about views. Starts looking along a different axis so the two pictures are
    // obviously not the same picture.
    private nint viewportId;
    private float viewportYaw = -1.4f;
    private float viewportPitch = 0.25f;
    private float viewportDistance = 7f;
    private Matrix4x4 viewportViewProjection = Matrix4x4.Identity;
    private Vector3 viewportCameraPosition;
    private bool viewportOpen = true;

    // <b>The panel's rectangle, from LAST frame.</b> UI layout runs after the views are declared
    // and after the scene is recorded, so the rect a panel occupies this frame does not exist when
    // the picture for it is drawn. Every immediate-mode editor answers this the same way: draw at
    // the size the panel was, and wear one frame of stale aspect on a resize.
    //
    // Written down rather than hidden because stage D has to decide whether that lag is what the
    // substrate wants or whether layout and submission should be split so the rect is known first.
    private Vector2 viewportPanelSize = new(480f, 270f);
    private Vector2 viewportImageMin;
    private Vector2 viewportImageSize;

    public string DebugName => "lab";

    public string UiName => "lab";

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        var vk = (VulkanGraphicsDevice)graphicsDevice;

        // Shaders arrive from the lab library's content propagation — this executable
        // never compiled one.
        renderer.Load(vk, Path.Combine(AppContext.BaseDirectory, "Shaders"));

        // The sun's depth buffer, registered so it can be looked at. A depth image sampled by a
        // colour shader arrives as (d, 0, 0, 1) — a red-scale map, not a mistake — and the useful
        // question it answers is coarse: is the caster pass drawing anything at all, and does the
        // sun's frustum cover the subject? Both are visible in red.
        shadowMapId = host.RegisterUiTexture(renderer.ShadowDepth);

        // The panel viewport's colour. Registered once: the graph reallocates this texture on a
        // window resize but mutates the registered entry in place, so the handle stays valid.
        viewportId = host.RegisterUiTexture(renderer.ViewportColour);

        LoadRig(vk);

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

        foreach (var image in model.Images)
        {
            uiImages.Add((image.Name, host.RegisterUiTexture(image.Texture), image.Width, image.Height));
        }

        Console.WriteLine(
            $"model: {Path.GetFileName(modelPath)} — {model.Nodes.Count} node(s), {model.Parts.Count} part(s), " +
            $"{model.TexturedPartCount} textured ({model.TextureCount} image(s)), " +
            $"bounds {model.BoundsMin.X:0.00},{model.BoundsMin.Y:0.00},{model.BoundsMin.Z:0.00} .. " +
            $"{model.BoundsMax.X:0.00},{model.BoundsMax.Y:0.00},{model.BoundsMax.Z:0.00}, " +
            $"scaled x{scale:0.000}");
    }

    // <b>Loaded against the renderer's skinned PROGRAM, not just its device.</b> A bone palette is a
    // descriptor set, and a descriptor set's layout comes from the program that will read it — so a
    // rig cannot build its set-3 buffer until it knows which shader is on the other end. That
    // dependency is why this runs after renderer.Load rather than beside it.
    private void LoadRig(VulkanGraphicsDevice vk)
    {
        if (rigPath is null) return;
        if (!File.Exists(rigPath))
        {
            Console.Error.WriteLine($"No rig at {rigPath}.");
            return;
        }

        rig = LabRig.Load(vk, rigPath, renderer.SkinnedProgram);
        if (rig.Skeleton.BoneCount > LabRig.MaxBones)
        {
            // Said here rather than discovered on the GPU: past the shader's array bound the draw
            // reads whatever follows the buffer, which renders as a character exploded across the
            // map with no validation error to explain it.
            Console.Error.WriteLine(
                $"{Path.GetFileName(rigPath)} has {rig.Skeleton.BoneCount} bones; the lab's skinned " +
                $"shader holds {LabRig.MaxBones}. Raise the bound in lab_skinned.vert.");
        }

        scene = LabScene.GroundOnly();
        posed = rig.Skeleton.CreateRestPose();
        palettes = new BonePaletteSet(rig.Skeleton.BoneCount, LabRig.MaxInstances);
        boneWorlds = new Matrix4x4[rig.Skeleton.BoneCount];
        restWorlds = new Matrix4x4[rig.Skeleton.BoneCount];
        echoBoneWorlds = new Matrix4x4[rig.Skeleton.BoneCount];
        echoes = new ClipPlayer[Math.Max(0, instanceCount - 1)];
        for (var i = 0; i < echoes.Length; i++) echoes[i] = new ClipPlayer(rig.Skeleton);

        playerA = new ClipPlayer(rig.Skeleton, rig.Clips.Count > 0 ? rig.Clips[0] : null);
        playerB = new ClipPlayer(rig.Skeleton, rig.Clips.Count > 1 ? rig.Clips[1] : null);

        // Prefer a walk on A and an idle on B when the asset has them: a blend between two named
        // gaits is the case the weight slider was built to show, and finding it by hand in a
        // seventy-six-clip list every launch is a tax on the thing being demonstrated.
        clipIndexA = clipName is not null
            ? NamedClip(rig, clipName)
            : PreferredClip(rig, new[] { "Walking_A", "Walking", "Walk", "Running_A", "Run" }, 0);
        clipIndexB = secondClip is not null
            ? NamedClip(rig, secondClip)
            : PreferredClip(rig, new[] { "Running_A", "Running", "Run", "Idle", "Unarmed_Idle" }, 1);
        if (rig.Clips.Count > 0) playerA.Clip = rig.Clips[clipIndexA];
        if (rig.Clips.Count > 1) playerB.Clip = rig.Clips[clipIndexB];

        LabRig.ComputeBoneWorlds(rig.Skeleton, playerA.RestPose, restWorlds);

        var extent = rig.LongestExtent;
        rigScale = extent > 0.001f ? 3f / extent : 1f;
        rigBase = Matrix4x4.CreateScale(rigScale)
                  * Matrix4x4.CreateTranslation(0f, -rig.BoundsMin.Y * rigScale, 0f);
        rigTransform = rigBase;

        foreach (var image in rig.Images)
        {
            uiImages.Add((image.Name, host!.RegisterUiTexture(image.Texture), image.Width, image.Height));
        }

        Console.WriteLine(
            $"rig: {Path.GetFileName(rigPath)} — {rig.Skeleton.BoneCount} bone(s), {rig.Clips.Count} clip(s), " +
            $"{rig.Parts.Count} primitive(s), {rig.VertexCount} vertices, " +
            $"bounds {rig.BoundsMin.Y:0.00}..{rig.BoundsMax.Y:0.00} tall, scaled x{rigScale:0.000}");
    }

    private static int NamedClip(LabRig rig, string name)
    {
        for (var i = 0; i < rig.Clips.Count; i++)
        {
            if (ReferenceEquals(rig.Clips[i], rig.Clip(name))) return i;
        }

        // A rig with no clips at all is a legitimate asset — a skin posed only by a game — and the
        // fallback message read rig.Clips[0] to name what it was falling back TO, which on that rig
        // is an IndexOutOfRange thrown while reporting a miss. The error path was the crash.
        Console.Error.WriteLine(rig.Clips.Count > 0
            ? $"No clip named '{name}'; starting on {rig.Clips[0].Name}."
            : $"No clip named '{name}', and this rig has none; holding the rest pose.");
        return 0;
    }

    private static int PreferredClip(LabRig rig, string[] names, int fallback)
    {
        foreach (var wanted in names)
        {
            if (rig.Clip(wanted) is not { } found) continue;
            for (var i = 0; i < rig.Clips.Count; i++)
            {
                if (ReferenceEquals(rig.Clips[i], found)) return i;
            }
        }

        return Math.Min(fallback, Math.Max(0, rig.Clips.Count - 1));
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

        // The viewport's own camera. Aspect comes from the PANEL, not the window — that is the
        // whole difference between a second view and a second copy of this one.
        var viewportAspect = viewportPanelSize.Y > 1f ? viewportPanelSize.X / viewportPanelSize.Y : 16f / 9f;
        var viewportEye = new Vector3(
            MathF.Cos(viewportPitch) * MathF.Sin(viewportYaw),
            MathF.Sin(viewportPitch),
            MathF.Cos(viewportPitch) * MathF.Cos(viewportYaw)) * viewportDistance;
        viewportCameraPosition = viewportEye;
        viewportViewProjection = Matrix4x4.CreateLookAt(viewportEye, new Vector3(0f, 1.2f, 0f), Vector3.UnitY)
                                 * GraphicsMatrices.CreatePerspectiveVulkan(
                                     MathF.PI / 3.2f, viewportAspect, 0.1f, 120f);

        UpdateRig(time.Delta);
    }

    /// <summary>Advances the clips, composes the pose, and integrates the root's travel.</summary>
    /// <remarks>
    /// <b>The three composition modes are three engine primitives, not three implementations.</b>
    /// Single is <see cref="ClipPlayer"/> alone; Blend is <see cref="PoseBlend.Lerp"/> over two
    /// players; Additive is <see cref="PoseDelta.LayerOnto"/> per bone with B's clip read relative to
    /// rest. All three already existed and none had ever been looked at — <c>PoseBlend</c> and
    /// <c>PoseDelta</c> have unit tests and zero callers outside them, which is its own kind of
    /// unverified however many assertions cover the maths.
    /// </remarks>
    private void UpdateRig(double delta)
    {
        if (rig is null || playerA is null || playerB is null || posed is null || palettes is null) return;

        playerA.Advance(delta);

        // B's clock runs in blend and additive modes only. Advancing it in single mode would make
        // the blend slider jump to wherever B had drifted to, which reads as a glitch in the blend
        // rather than as a clock nobody stopped.
        if (poseMode != PoseMode.Single) playerB.Advance(delta);

        switch (poseMode)
        {
            case PoseMode.Blend:
                PoseBlend.Lerp(playerA.Pose, playerB.Pose, Math.Clamp(blendWeight, 0f, 1f), posed);
                break;

            case PoseMode.Additive:
                // B layered ON TOP of A: B's pose is read as an offset from rest, so a clip that
                // waves an arm waves it while A's legs keep walking. The order matters — A must be
                // the base, because the delta is applied to whatever is already in the target.
                for (var i = 0; i < posed.BoneCount; i++)
                {
                    posed.Locals[i] = PoseDelta.LayerOnto(
                        playerA.Pose.Locals[i],
                        playerA.RestPose.Locals[i],
                        playerB.Pose.Locals[i],
                        Math.Clamp(blendWeight, 0f, 1f));
                }

                break;

            default:
                posed.CopyFrom(playerA.Pose);
                break;
        }

        // Root travel comes from A only. A blend of two clips that travel at different speeds has no
        // single correct delta — the honest answer is a decision (take the dominant clip's, scale
        // both by weight, take the faster) and a lab should not invent one on a consumer's behalf.
        rootTravel += Vector3.TransformNormal(playerA.RootDelta.Translation, rig.MeshNodeTransform);
        rootTurn = Quaternion.Normalize(rootTurn * playerA.RootDelta.Rotation);
        rootTurnPathDegrees += Degrees(playerA.RootDelta.Rotation);

        // <b>Driving means the clip stops moving the body and the transform starts.</b> Leaving the
        // root animated AND applying the delta moves a travelling clip twice and snaps it back once
        // per cycle. Stripping is what a game does with root motion, and the toggle is the lab's
        // whole point: off shows the clip as authored (a dodge lurches back at every loop), on shows
        // the same clip driving a body in a straight line.
        if (driveRoot) RootMotion.Strip(rig.Skeleton, posed, playerA.RestPose);

        LabRig.ComputeBoneWorlds(rig.Skeleton, posed, boneWorlds);

        // <b>Scaled ONCE.</b> rootTravel is already in post-MeshNodeTransform space and rigBase
        // already carries the normalising scale, so a `* rigScale` here squared it — the Rogue
        // normalises by 1.372, so the body ran 1.88x too far and the trail agreed with it, which is
        // why two wrong things looked like one right one.
        rigTransform = driveRoot
            ? Matrix4x4.CreateTranslation(rootTravel) * rigBase
            : rigBase;

        PackInstances(delta);

        // Sampled in the space the trail is drawn in, so the line is where the body would be.
        var where = Vector3.Transform(rootTravel, rigBase);
        if (rootPath.Count == 0 || Vector3.DistanceSquared(rootPath[^1], where) > 1e-6f)
        {
            rootPath.Add(where);
            // Bounded, because an unbounded path is a memory leak wearing a gizmo. Long enough that
            // several loops of a walk are visible at once, which is the length the wrap bug needs to
            // be seen at — one cycle's worth would hide exactly the seam being looked for.
            if (rootPath.Count > 2048) rootPath.RemoveAt(0);
        }
    }

    /// <summary>Writes the subject's palette and every echo's into one buffer, sliced per instance.</summary>
    /// <remarks>
    /// <b>The gap this closes, stated plainly.</b> One palette binding held one pose, so two draws in
    /// a frame both read the second — fine for a lab with one subject and the first thing a game
    /// breaks. Here each body's matrices go into its own slice and the shader multiplies
    /// gl_InstanceIndex by the stride. Three bodies at three clip phases, one draw, one frame.
    /// <para>
    /// Each instance's PLACEMENT is baked into its palette rather than carried alongside, because a
    /// per-draw push constant cannot vary per instance. Bulwark does the same; RTSGame keeps a
    /// separate instance buffer instead. <see cref="BonePaletteSet"/> takes no view — it owns the
    /// stride, and where the model lives stays the caller's.
    /// </para>
    /// <para>
    /// The echoes are offset by a fraction of the clip rather than by a fixed number of seconds, so
    /// the spread reads the same on a 0.4 s dodge and a 2.4 s spin. A fixed offset would put every
    /// body on the same frame of a short clip, which looks like the instancing has failed.
    /// </para>
    /// </remarks>
    private void PackInstances(double delta)
    {
        if (rig is null || palettes is null || posed is null || playerA is null) return;

        palettes.Reset();
        instancePlacements.Clear();

        // Bodies stand in a row across the camera's view, a stride apart, centred on the origin so
        // one body sits where one body always did.
        var spacing = MathF.Max(1.2f, rig.LongestExtent * rigScale * 0.75f);
        var half = (instanceCount - 1) * 0.5f;

        var subject = Matrix4x4.CreateTranslation(-half * spacing, 0f, 0f) * rigTransform;
        palettes.Add(rig.Skeleton, posed, rig.MeshNodeTransform * subject);
        instancePlacements.Add(subject);

        for (var i = 0; i < echoes.Length; i++)
        {
            var echo = echoes[i];
            if (lockstep)
            {
                // The negative control: same clip, same instant. These must come out identical.
                echo.Clip = playerA.Clip;
                echo.ScrubTo(playerA.Time);
            }
            else
            {
                // Its own clip, its own rate, its own clock. A rate that differs per body is what
                // separates "independently posed" from "posed once and read N times" even when two
                // bodies happen to share a clip — they drift apart, and a frame-locked mechanism
                // cannot make them.
                echo.Clip = rig.Clips.Count > 0
                    ? rig.Clips[(clipIndexA + i + 1) % rig.Clips.Count]
                    : null;
                echo.Paused = playerA.Paused;
                echo.Rate = playerA.Rate * (1f + ((i + 1) * 0.17f));
                echo.Advance(delta);
            }

            var placement = Matrix4x4.CreateTranslation((i + 1 - half) * spacing, 0f, 0f) * rigTransform;
            palettes.Add(rig.Skeleton, echo.Pose, rig.MeshNodeTransform * placement);
            instancePlacements.Add(placement);
        }

        distinctPoses = CountDistinctPoses();
    }

    /// <summary>How many genuinely different POSES the live instances hold.</summary>
    /// <remarks>
    /// <b>Placement excluded, deliberately.</b> Each drawn palette has its body's placement baked in,
    /// so three bodies standing a metre apart hold different matrices whatever their poses are — a
    /// count taken from the drawn slices reads "all different" even under lockstep, and the control
    /// would then be incapable of failing. The bodies are re-posed at identity here purely to be
    /// counted, which costs one palette build per instance on a lab-sized crowd.
    /// <para>
    /// Varied should give one distinct pose per body; lockstep should give exactly one in total. Those
    /// are the two halves, and either alone proves nothing.
    /// </para>
    /// </remarks>
    private int CountDistinctPoses()
    {
        if (rig is null || playerA is null || posed is null) return 0;

        poseCheck ??= new BonePaletteSet(rig.Skeleton.BoneCount, LabRig.MaxInstances);
        poseCheck.Reset();
        poseCheck.Add(rig.Skeleton, posed, Matrix4x4.Identity);
        foreach (var echo in echoes) poseCheck.Add(rig.Skeleton, echo.Pose, Matrix4x4.Identity);

        var seen = new HashSet<ulong>();
        for (var i = 0; i < poseCheck.Count; i++) seen.Add(poseCheck.Fingerprint(i));
        return seen.Count;
    }

    // The turn's magnitude in degrees. Quaternion.W is cos(θ/2) and the sign of the axis is
    // irrelevant to "how far did it turn", so the absolute value keeps a half-turn from reading as
    // a full one.
    private static float Degrees(Quaternion q) =>
        2f * MathF.Acos(Math.Clamp(MathF.Abs(q.W), 0f, 1f)) * (180f / MathF.PI);

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frames++;
        if (frame.Height > 0) aspect = frame.Width / (float)frame.Height;
        var view = Matrix4x4.CreateLookAt(cameraPosition, new Vector3(0f, 1f, 0f), Vector3.UnitY);
        viewProjection = view * GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3.2f, aspect, 0.1f, 120f);

        // Before recording, because the palette buffer is read at Execute and written here — the
        // draw carries a descriptor set, not a copy of the matrices.
        if (rig is not null && palettes is not null) rig.UploadPalettes(palettes);

        renderer.Render(
            commandList, scene, viewProjection, cameraPosition, model, modelTransform,
            rig, palettes?.Count ?? 0,
            viewportOpen ? viewportViewProjection : null, viewportCameraPosition);
    }

    public void Debug(DebugContext debug)
    {
        debug.State.DepthTestDrawing = depthTestGizmos;
        debug.Values.Value("frames", frames);
        debug.Values.Value("sun", scene.SunDirection);
        debug.Stats.Gauge("objects", scene.Objects.Count);

        // Declared with BOTH rectangles rather than through the whole-surface shorthand: that one
        // fills logical and physical from RenderFrameContext, which is physical pixels, so on a 2x
        // display every pick would land at half the intended place.
        var (logicalW, logicalH) = host?.LogicalSize ?? (debug.Frame.Width, debug.Frame.Height);
        var declaration = debug.Draw.Declare(
            "main",
            viewProjection,
            RenderSurfaceHandle.Default,
            new Rect(0f, 0f, logicalW, logicalH),
            new Rect(0f, 0f, debug.Frame.Width, debug.Frame.Height));
        mainView = declaration;
        using (debug.Draw.In(declaration))
        {
            DrawSceneGizmos(debug, trails: true);
        }

        // ── Stage C: the same gizmos, through the panel's view ───────────────
        // <b>Declared with the IMAGE's rectangle, not the panel's.</b> The picture is letterboxed
        // inside the panel to keep its aspect, so the two differ by the letterbox — and a ray cast
        // through the panel rect would be off by exactly that, silently, and only on panels whose
        // shape happens not to match the target's.
        //
        // The runtime routes debug geometry to each declared view's own target, so naming
        // ViewportSurface here is all it takes to put the skeleton and the grid inside the panel.
        // That routing already existed and had never had a second view to prove it.
        //
        // The rectangle is LAST frame's: UI layout runs after this. See DrawViewportPanel.
        if (viewportOpen && viewportImageSize.X > 1f && viewportImageSize.Y > 1f)
        {
            var panelDeclaration = debug.Draw.Declare(
                "viewport",
                viewportViewProjection,
                renderer.ViewportSurface,
                new Rect(viewportImageMin.X, viewportImageMin.Y, viewportImageSize.X, viewportImageSize.Y),
                // The target is half the swapchain and the picture fills it, so the physical
                // rectangle is the whole of it rather than a sub-rect of the window.
                new Rect(0f, 0f, debug.Frame.Width * 0.5f, debug.Frame.Height * 0.5f));
            viewportView = panelDeclaration;

            using (debug.Draw.In(panelDeclaration))
            {
                // Trails off in the panel: a trail is keyed by name and remembers across frames, so
                // feeding one the same name from two views would interleave two cameras' worth of
                // points into a single history.
                DrawSceneGizmos(debug, trails: false);
            }
        }
        else
        {
            viewportView = null;
        }
    }

    // Everything the lab draws into a view. Called once per view rather than once per frame,
    // because a view is a camera AND a target — the same geometry seen twice is two sets of lines.
    private void DrawSceneGizmos(DebugContext debug, bool trails)
    {
        debug.Draw.Grid("floor", new Vector3(0f, 0.02f, 0f), 24f, 24, new GraphicsColor(0.2f, 0.24f, 0.3f, 1f));

        // The sun, drawn where it is actually pointing — an arrow that is wrong is the
        // fastest way to notice a lighting convention has drifted.
        var sunFrom = scene.SunDirection * 7f;
        debug.Draw.Arrow("sun", sunFrom, Vector3.Zero, new GraphicsColor(1f, 0.9f, 0.5f, 1f));

        if (model is not null) DrawModelGizmos(debug);
        if (rig is not null) DrawRigGizmos(debug);

        if (trails && showTrail)
        {
            // Where the sun has been while it was being dragged.
            debug.Draw.Trail("sun/path", sunFrom, new GraphicsColor(1f, 0.75f, 0.3f, 1f), trailSeconds);
        }
    }

    // <b>The pose, drawn.</b> This is the stage the whole arc rests on: until a skeleton is visible
    // over the mesh, "the character folded inside out" has two causes that look identical from the
    // outside — the clip was already wrong, or the thing that read it was. With the bones drawn,
    // they separate at a glance.
    //
    // The root's travel is drawn beside it as a path, because a root-motion bug has a shape: a
    // straight line at constant spacing is right, a line that stutters once per cycle is the loop
    // wrap handled by subtraction, and a line that drifts sideways is a delta taken in the wrong
    // frame. None of those are visible in a number.
    private void DrawRigGizmos(DebugContext debug)
    {
        if (rig is null || playerA is null) return;

        using var scope = debug.Scope("rig");

        if (showSkeleton)
        {
            var options = LabSkeletonView.Options.Default with
            {
                Joints = showJoints,
                RestGhost = showRestGhost,
                AllAxes = showAllBoneAxes,
                LeafStubs = showLeafStubs,
                Scale = gizmoScale,
            };

            // <b>One skeleton per instance, and that IS the proof.</b> Three bodies mid-stride at
            // three phases is suggestive; three SKELETONS in different shapes in one frame is
            // conclusive — a single palette read three times would draw the same pose three times
            // and the meshes would agree with each other.
            //
            // Scoped per instance so the debug names do not collide: `bone/17` means something
            // different under `i0` than under `i2`, and trails key off the name.
            DrawInstanceSkeleton(debug, 0, boneWorlds, options);
            for (var i = 0; i < echoes.Length && i + 1 < instancePlacements.Count; i++)
            {
                LabRig.ComputeBoneWorlds(rig.Skeleton, echoes[i].Pose, echoBoneWorlds);
                DrawInstanceSkeleton(debug, i + 1, echoBoneWorlds, options);
            }
        }

        if (!showRootTrail) return;

        var head = rootPath.Count > 0 ? rootPath[^1] : Vector3.Transform(Vector3.Zero, rigBase);
        debug.Draw.Trail("root/recent", head, new GraphicsColor(0.4f, 1f, 0.7f, 1f), trailSeconds);
        if (rootPath.Count >= 2)
        {
            // The whole path, not just the last few seconds. The seam at a loop boundary is a
            // once-per-cycle event, so a trail short enough to be tidy is a trail that expires
            // before the thing it exists to show comes round again.
            debug.Draw.Polyline("root/path", rootPath, new GraphicsColor(0.3f, 0.8f, 0.55f, 1f));
        }
    }

    // One instance's skeleton, at its own placement. Only the subject gets a selected bone —
    // selecting "the left wrist" on three bodies at once would highlight three joints and point at
    // none of them.
    private void DrawInstanceSkeleton(
        DebugContext debug, int instance, Matrix4x4[] worlds, LabSkeletonView.Options options)
    {
        if (rig is null || instance >= instancePlacements.Count) return;

        using var scope = debug.Scope($"i{instance}");
        LabSkeletonView.Draw(
            debug,
            rig.Skeleton,
            worlds,
            rig.MeshNodeTransform * instancePlacements[instance],
            options,
            instance == 0 ? selectedBone : -1,
            instance == 0 && showRestGhost ? restWorlds : null,
            deformBonesOnly ? rig.DeformHierarchy : null);
    }

    // The clip list, the transport, and the selected bone — the panel half of "see a pose".
    //
    // Seventy-six clips is past the point where a list is browsable, hence the filter box: the
    // Rogue's are named by weapon and action, so typing "walk" or "2H" is how anyone actually finds
    // one. A list this long without a filter is a list nobody reads.
    private void DrawRigPanel()
    {
        if (rig is null || playerA is null || playerB is null) return;

        // Both numbers, because they answer different questions: how much of the rig the mesh is
        // attached to, and how much of it has to be drawn to show those chains unbroken.
        ImGui.TextDisabled(
            $"{rig.Skeleton.BoneCount} bones ({rig.WeightedBoneCount} weighted, " +
            $"{rig.DeformHierarchyCount} drawn) · {rig.Clips.Count} clips · {rig.Parts.Count} prims");

        var mode = (int)poseMode;
        if (ImGui.Combo("compose", ref mode, "single\0blend A to B\0additive B on A\0"))
        {
            poseMode = (PoseMode)mode;
        }

        if (poseMode != PoseMode.Single)
        {
            ImGui.SliderFloat(poseMode == PoseMode.Blend ? "weight" : "overlay", ref blendWeight, 0f, 1f);
        }

        DrawTransport(playerA, "A");
        if (poseMode != PoseMode.Single) DrawTransport(playerB, "B");

        ImGui.Separator();
        // ##-prefixed id, so ImGui draws no label. The visible "filter" label sat between the
        // field and the button and squeezed "clear" down to "c" — a row that reads as a rendering
        // fault and is a layout one.
        ImGui.SetNextItemWidth(-56f);
        ImGui.InputText("##filter", ref clipFilter, 64);
        ImGui.SameLine();
        if (ImGui.SmallButton("clear")) clipFilter = string.Empty;

        // Which player the list assigns to. A single list that always targets A would make picking
        // B's clip impossible in blend mode; two lists would double the height of the panel for one
        // extra bit of state.
        var target = poseMode == PoseMode.Single ? 0 : ImGui.GetIO().KeyShift ? 1 : 0;
        ImGui.TextDisabled(poseMode == PoseMode.Single
            ? "click to play"
            : target == 0 ? "click -> A   (hold shift -> B)" : "click -> B");

        if (ImGui.BeginChild("clips", new Vector2(0, 160), ImGuiChildFlags.Borders))
        {
            for (var i = 0; i < rig.Clips.Count; i++)
            {
                var clip = rig.Clips[i];
                if (clipFilter.Length > 0 &&
                    clip.Name.IndexOf(clipFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var selected = i == clipIndexA || (poseMode != PoseMode.Single && i == clipIndexB);
                var tag = i == clipIndexA ? "A" : i == clipIndexB && poseMode != PoseMode.Single ? "B" : " ";

                // A zero-length clip is a POSE, not a fault — the Rogue ships seven of them. Marked
                // rather than hidden: selecting one and seeing the body hold that shape is how you
                // find out which pose it is.
                var length = clip.Duration > 0 ? $"{clip.Duration,5:0.00}s" : "  pose";
                if (!ImGui.Selectable($"{tag} {clip.Name}  {length}", selected)) continue;

                if (target == 1)
                {
                    clipIndexB = i;
                    playerB.Clip = clip;
                }
                else
                {
                    clipIndexA = i;
                    playerA.Clip = clip;
                    ResetRootTravel();
                }
            }
        }

        ImGui.EndChild();

        DrawInstancePanel();
        DrawRootMotionPanel();
        DrawBonePanel();
    }

    // What each body is actually doing, and whether the mechanism is holding.
    //
    // The two rows that matter are the checkbox and the verdict beside it. Varied should read
    // "distinct"; lockstep is the control — every body on one clip at one instant — and if THAT
    // still shows three different walks, the slice is not doing what it claims.
    private void DrawInstancePanel()
    {
        if (rig is null || playerA is null) return;
        if (!ImGui.CollapsingHeader("instances", ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.TextDisabled($"{instanceCount} of max {LabRig.MaxInstances} · one draw, one palette buffer");

        if (instanceCount <= 1)
        {
            ImGui.TextDisabled("pass --instances N to draw more");
            return;
        }

        ImGui.Checkbox("lockstep (negative control)", ref lockstep);

        // Varied wants one distinct pose per body; lockstep wants exactly one in total. Stating the
        // expectation beside the count is what makes a reader able to tell a pass from a number.
        var want = lockstep ? 1 : instanceCount;
        var ok = distinctPoses == want;
        ImGui.SameLine();
        if (ok) ImGui.TextDisabled($"{distinctPoses}/{want} distinct poses");
        else ImGui.TextColored(new Vector4(1f, 0.4f, 0.35f, 1f), $"{distinctPoses} poses, expected {want}");

        if (ImGui.BeginChild("instancelist", new Vector2(0, 96), ImGuiChildFlags.Borders))
        {
            ImGui.Text($"0  {playerA.Clip?.Name ?? "(rest)"}");
            ImGui.SameLine(220f);
            ImGui.TextDisabled($"t {playerA.Time,5:0.00}  x{playerA.Rate:0.00}");

            for (var i = 0; i < echoes.Length; i++)
            {
                var echo = echoes[i];
                ImGui.Text($"{i + 1}  {echo.Clip?.Name ?? "(rest)"}");
                ImGui.SameLine(220f);
                ImGui.TextDisabled($"t {echo.Time,5:0.00}  x{echo.Rate:0.00}");
            }
        }

        ImGui.EndChild();
    }

    private void DrawTransport(ClipPlayer player, string label)
    {
        ImGui.PushID(label);
        var paused = player.Paused;
        if (ImGui.Button(paused ? $"play {label}" : $"pause {label}")) player.Paused = !paused;
        ImGui.SameLine();

        // A thirtieth of a second rather than a keyframe, because a clip's keyframes are not
        // uniformly spaced and "one frame" in an animator's sense is a sampling rate, not a track
        // entry. Stepping by time is also what makes the root-motion readout comparable between
        // steps.
        if (ImGui.Button("<")) player.Step(-1.0 / 30.0);
        ImGui.SameLine();
        if (ImGui.Button(">")) player.Step(1.0 / 30.0);
        ImGui.SameLine();
        ImGui.TextDisabled($"{player.Time,5:0.000} / {player.Duration:0.00}s");

        var t = (float)player.Time;
        ImGui.SetNextItemWidth(-70f);
        if (ImGui.SliderFloat($"t {label}", ref t, 0f, MathF.Max(0.0001f, (float)player.Duration)))
        {
            // A scrub is a jump, not travel — ClipPlayer clears the root delta for exactly this, so
            // dragging the slider does not launch the body across the map.
            player.ScrubTo(t);
            player.Paused = true;
        }

        var rate = player.Rate;
        ImGui.SetNextItemWidth(-70f);
        if (ImGui.SliderFloat($"rate {label}", ref rate, -3f, 3f)) player.Rate = rate;
        ImGui.PopID();
    }

    private void DrawRootMotionPanel()
    {
        if (playerA is null) return;
        if (!ImGui.CollapsingHeader("root motion")) return;

        var perCycle = playerA.Clip is { } clip
            ? RootMotion.PerCycle(clip, playerA.RootBone, playerA.RestPose.Locals[playerA.RootBone])
            : RootMotion.None;

        // <b>The number that says whether a clip travels at all.</b> Most authored loops are made in
        // place — the root returns to where it started, per-cycle travel is ~0, and locomotion is the
        // game's job. A clip with real travel reports a metre or two here, and that is the clip whose
        // delta is worth driving anything with.
        ImGui.Text($"per cycle  {perCycle.Translation.X:0.000}, {perCycle.Translation.Y:0.000}, {perCycle.Translation.Z:0.000}");
        ImGui.TextDisabled($"           {perCycle.Distance:0.000} m, {Degrees(perCycle.Rotation):0.0}°");
        ImGui.Text($"travelled  {rootTravel.X:0.000}, {rootTravel.Y:0.000}, {rootTravel.Z:0.000}");
        ImGui.TextDisabled(
            $"           {rootTravel.Length():0.000} m net, {Degrees(rootTurn):0.0}° net turn");
        ImGui.TextDisabled($"           {rootTurnPathDegrees:0.0}° of turning done to get there");

        ImGui.Checkbox("drive the model", ref driveRoot);
        ImGui.SameLine();
        ImGui.Checkbox("trail", ref showRootTrail);
        if (ImGui.Button("reset travel")) ResetRootTravel();
        ImGui.SameLine();
        ImGui.TextDisabled($"{rootPath.Count} samples");
    }

    /// <summary>Where instance 0 stands. The subject is offset when there are echoes beside it.</summary>
    private Matrix4x4 SubjectPlacement =>
        instancePlacements.Count > 0 ? instancePlacements[0] : rigTransform;

    private void ResetRootTravel()
    {
        rootTravel = Vector3.Zero;
        rootTurn = Quaternion.Identity;
        rootTurnPathDegrees = 0f;
        rootPath.Clear();
    }

    // The selected bone, in both spaces. Local TRS is what a clip authored; world is where it ended
    // up. Seeing them together is what separates "this bone's track is wrong" from "this bone's
    // PARENT is wrong and it is being carried" — the single most common misreading of a bad pose.
    private void DrawBonePanel()
    {
        if (rig is null || posed is null) return;
        if (!ImGui.CollapsingHeader("bones")) return;

        if (ImGui.BeginChild("bonelist", new Vector2(0, 140), ImGuiChildFlags.Borders))
        {
            for (var i = 0; i < rig.Skeleton.BoneCount; i++)
            {
                var bone = rig.Skeleton.Bones[i];
                var indent = 0;
                for (var p = bone.ParentIndex; p >= 0; p = rig.Skeleton.Bones[p].ParentIndex) indent++;
                // A control bone is dimmed rather than hidden: it is still in the palette and still
                // animated, so a reader looking for "why is nothing moving" needs to be able to find
                // it — just not to have it shouting alongside the bones the mesh follows.
                var deform = rig.WeightedBones[i];
                var label = new string(' ', indent * 2) + bone.Name + (deform ? string.Empty : "  ·");
                if (!deform) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.6f, 1f));
                if (ImGui.Selectable($"{label}##{i}", selectedBone == i)) selectedBone = i;
                if (!deform) ImGui.PopStyleColor();
            }
        }

        ImGui.EndChild();

        if (selectedBone < 0 || selectedBone >= rig.Skeleton.BoneCount) return;

        var local = posed.Locals[selectedBone];
        var rest = playerA!.RestPose.Locals[selectedBone];
        var world = boneWorlds[selectedBone] * rig.MeshNodeTransform * SubjectPlacement;

        ImGui.TextDisabled($"bone {selectedBone} · parent {rig.Skeleton.Bones[selectedBone].ParentIndex}");
        ImGui.Text($"local T {local.Translation.X:0.000}, {local.Translation.Y:0.000}, {local.Translation.Z:0.000}");
        ImGui.Text($"local R {local.Rotation.X:0.000}, {local.Rotation.Y:0.000}, {local.Rotation.Z:0.000}, {local.Rotation.W:0.000}");
        ImGui.Text($"local S {local.Scale.X:0.000}, {local.Scale.Y:0.000}, {local.Scale.Z:0.000}");
        ImGui.Separator();
        ImGui.Text($"world   {world.M41:0.000}, {world.M42:0.000}, {world.M43:0.000}");

        // How far this bone has moved off its rest value, which is the one number that answers
        // "is this clip even touching this bone?" A bone a clip has no track for reads exactly 0.
        var offset = (local.Translation - rest.Translation).Length();
        var turned = Degrees(Quaternion.Inverse(rest.Rotation) * local.Rotation);
        ImGui.TextDisabled($"from rest  {offset:0.000} m, {turned:0.0}°");
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

    public void DrawUi()
    {
        ImGui.SetNextWindowSize(new Vector2(340, 440), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(20, 20), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("lab"))
        {
            ImGui.End();
            return;
        }

        // Grouped rather than one flat list of knobs. The panel grew a control at a time and had
        // become a wall — which is the same complaint about legibility that got the gizmos
        // depth-tested, one layer up.
        if (rig is not null) ImGui.TextDisabled(Path.GetFileName(rig.SourcePath));
        else if (model is not null) ImGui.TextDisabled(Path.GetFileName(model.SourcePath));
        else ImGui.TextDisabled("nothing loaded — pass --model <path.glb> or --rig <rigged.glb>");

        if (ImGui.CollapsingHeader("image", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var exposure = renderer.Exposure;
            if (ImGui.SliderFloat("exposure", ref exposure, 0.1f, 4f)) renderer.Exposure = exposure;

            var mode = (int)renderer.TonemapMode;
            if (ImGui.Combo("tonemap", ref mode, "ACES\0AgX\0Reinhard\0Neutral\0"))
            {
                renderer.TonemapMode = mode;
            }
        }

        if (ImGui.CollapsingHeader("sun", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.SliderFloat("yaw", ref sunYaw, -MathF.PI, MathF.PI);
            ImGui.SliderFloat("pitch", ref sunPitch, 0.15f, 1.5f);

            var ambient = scene.AmbientStrength;
            if (ImGui.SliderFloat("ambient", ref ambient, 0f, 0.4f)) scene.AmbientStrength = ambient;

            ImGui.Checkbox("trail", ref showTrail);
            if (showTrail) ImGui.SliderFloat("trail seconds", ref trailSeconds, 0.25f, 8f);
        }

        if (ImGui.CollapsingHeader("gizmos", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Checkbox("depth-tested", ref depthTestGizmos);
            if (model is not null)
            {
                ImGui.Checkbox("node pivots", ref showPivots);
                ImGui.SameLine();
                ImGui.Checkbox("bounds", ref showBounds);
                if (showPivots) ImGui.Checkbox("transform-only nodes too", ref showAllPivots);
            }

            if (rig is not null)
            {
                ImGui.Checkbox("skeleton", ref showSkeleton);
                ImGui.SameLine();
                ImGui.Checkbox("joints", ref showJoints);
                ImGui.Checkbox("rest ghost", ref showRestGhost);
                ImGui.SameLine();
                ImGui.Checkbox("all axes", ref showAllBoneAxes);
                ImGui.Checkbox("leaf stubs", ref showLeafStubs);
                ImGui.SameLine();
                // The default, because 20 of the Rogue's 41 bones are IK handles and roll controls
                // hanging off the root, and drawing them makes a starburst at the feet.
                ImGui.Checkbox("deform only", ref deformBonesOnly);
                ImGui.SliderFloat("gizmo size", ref gizmoScale, 0.25f, 4f);
            }
        }

        if (rig is not null && ImGui.CollapsingHeader("animation", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawRigPanel();
        }

        if (model is not null && ImGui.CollapsingHeader("nodes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawNodeList();
        }

        DrawImagePanel();
        DrawViewportPanel();

        ImGui.Separator();
        ImGui.TextDisabled(rig is not null
            ? $"drag to orbit · wheel to zoom · space plays · left/right step · {frames} frames"
            : $"drag to orbit · wheel to zoom · {frames} frames");
        ImGui.End();
    }

    // Transport on the keyboard, because scrubbing a pose means looking at the model rather than at
    // the slider you are dragging. Space and the arrows are what every animation tool uses; a lab
    // that invented its own would be asking to be relearned.
    // <b>A second camera, rendered off-screen and shown in a panel.</b> Stage B of the view arc.
    //
    // The picture here is the SAME scene through a different view-projection, drawn by the same
    // pipelines into a target of its own — not a copy of the main view, which would prove a texture
    // can be drawn (stage A did that) and nothing about views.
    //
    // Two things this settles, both written down because stage D has to weigh them:
    //
    //   • <b>The panel's rect is one frame old.</b> UI layout runs after views are declared and
    //     after the scene is recorded, so this frame's picture is drawn at last frame's size. A
    //     resize therefore shows one frame of stale aspect. That is what every immediate-mode
    //     editor does; the alternative is splitting layout from submission, which moves who owns
    //     the frame.
    //   • <b>The image rect is not the panel rect.</b> The target has its own aspect and the panel
    //     has another, so the picture is fitted inside with letterboxing. Stage C picks through
    //     THAT rect, not the panel's — which is exactly the offset-and-scale case ViewDeclaration's
    //     two rectangles exist for, and the case a full-window view could never exercise.
    private void DrawViewportPanel()
    {
        if (viewportId == 0) return;

        ImGui.SetNextWindowSize(new Vector2(520, 340), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(480, 470), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("viewport", ref viewportOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.SliderFloat("yaw", ref viewportYaw, -MathF.PI, MathF.PI);
        ImGui.SliderFloat("pitch", ref viewportPitch, 0.05f, 1.45f);
        ImGui.SliderFloat("dist", ref viewportDistance, 2.5f, 30f);

        var available = ImGui.GetContentRegionAvail();
        if (available.X < 32f || available.Y < 32f)
        {
            ImGui.End();
            return;
        }

        // Read for NEXT frame's projection. Reading it here rather than guessing is the whole
        // reason the lag is one frame and not permanent.
        viewportPanelSize = available;

        // Largest rect with the TARGET's aspect that fits the panel, centred. Stretching to fill
        // would make the picture disagree with the projection it was drawn through, and every ray
        // cast into it afterwards would be wrong by that same stretch — silently, and only on
        // panels whose shape happens not to match.
        var (targetW, targetH) = host?.LogicalSize ?? (16, 9);
        var targetAspect = targetH > 0 ? targetW / (float)targetH : 16f / 9f;
        var fitted = available.X / available.Y > targetAspect
            ? new Vector2(available.Y * targetAspect, available.Y)
            : new Vector2(available.X, available.X / targetAspect);

        var cursor = ImGui.GetCursorScreenPos();
        var offset = (available - fitted) * 0.5f;
        ImGui.SetCursorScreenPos(cursor + offset);

        viewportImageMin = cursor + offset;
        viewportImageSize = fitted;
        ImGui.Image(viewportId, fitted);

        // ── Stage C: driving a view from inside the widget ───────────────────
        // <b>Not through IInputHandler, and that is forced rather than chosen.</b> The viewport is
        // an ImGui window, so ImGui captures the pointer over it, GestureOwnership hands the press
        // to the UI, and the application's input handler is never called — correctly. The picture
        // is an ImGui ITEM, so the only place that can ask "is the pointer on it" is right here,
        // during layout, using the item state ImGui just computed.
        //
        // This is the shape every editor viewport has, and it is worth noticing that the engine
        // needed no change to allow it: the capture rule was already right, and the widget asking
        // about itself is what the rule leaves room for.
        var hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var drag = ImGui.GetIO().MouseDelta;
            viewportYaw -= drag.X * 0.008f;
            viewportPitch = Math.Clamp(viewportPitch + (drag.Y * 0.006f), 0.05f, 1.45f);
        }

        if (hovered)
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0f) viewportDistance = Math.Clamp(viewportDistance - (wheel * 0.8f), 2.5f, 30f);

            // <b>The ray, through the view's OWN rectangle.</b> ViewPicking compares the pointer
            // against the declaration's logical rect, so a picture at an offset inside a panel on a
            // 2x display picks correctly without a single scale factor written here. That is the
            // whole claim of carrying two rectangles, and until now nothing had ever declared a
            // view that was not the whole window.
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && viewportView is { } declared)
            {
                if (ViewPicking.RayThrough(declared, ImGui.GetMousePos()) is { } ray) PickBone(ray);
            }
        }

        var pointer = ImGui.GetMousePos();
        ImGui.TextDisabled(hovered
            ? $"pointer {pointer.X - viewportImageMin.X:0}, {pointer.Y - viewportImageMin.Y:0} in view"
            : "drag to orbit · wheel to zoom · click to pick");
        ImGui.End();
    }

    // <b>The asset's own textures, drawn.</b> Stage A of the view arc, and the first thing in this
    // engine to put a non-font image on screen.
    //
    // The lab has reported texture COUNTS since it learned to load a model — "1 image across 12
    // parts" — and a count is the least interesting fact about a texture. Which image, at what
    // size, and whether it is the one you meant are all answerable by looking, and until the UI
    // layer could read `cmd.TextureId` there was nowhere to look.
    private void DrawImagePanel()
    {
        if (uiImages.Count == 0 && shadowMapId == 0) return;

        // <b>Its own window, not a section of the lab panel.</b> It was a collapsing header at the
        // bottom of a panel that already holds image, sun, gizmo, animation, instance and node
        // sections — and it drew nothing, because ImGui clips items scrolled out of a window and
        // emits no draw for them. The draw count said 3 before and 3 after, which is what a
        // correct feature looks like when nothing can see it.
        //
        // A picture also wants room a sidebar cannot give it, and Stage B of the view arc needs a
        // window to put a viewport in. This is that window, arriving early.
        ImGui.SetNextWindowSize(new Vector2(320, 420), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(480, 20), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("images"))
        {
            ImGui.End();
            return;
        }

        ImGui.SliderFloat("thumb", ref thumbnailScale, 0.25f, 3f);

        foreach (var (label, id, width, height) in uiImages)
        {
            ImGui.TextDisabled($"{label}  {width}x{height}");

            // Fitted to the panel rather than drawn at native size: a 1024px atlas in a 340px
            // panel would push everything else off the edge, and the aspect has to be kept or the
            // thing being inspected is not the thing that shipped.
            var available = MathF.Max(64f, ImGui.GetContentRegionAvail().X);
            var side = MathF.Min(available, 160f * thumbnailScale);
            var scale = height > 0 ? side / width : side;
            ImGui.Image(id, new Vector2(side, MathF.Max(16f, height * scale)));
        }

        if (shadowMapId == 0)
        {
            ImGui.End();
            return;
        }

        ImGui.Separator();
        ImGui.TextDisabled($"sun depth  {LabRenderer.ShadowMapSize}x{LabRenderer.ShadowMapSize}");

        // Red-scale, and that is the format rather than a fault: a single-channel depth image
        // sampled by a colour shader is (d, 0, 0, 1). It answers a coarse question — is the caster
        // drawing, and does the sun cover the subject — and a shader that knew it was depth is a
        // second pipeline the arc has not earned yet.
        var shadowSide = MathF.Min(MathF.Max(64f, ImGui.GetContentRegionAvail().X), 160f * thumbnailScale);
        ImGui.Image(shadowMapId, new Vector2(shadowSide, shadowSide));
        ImGui.End();
    }

    public void OnKeyDown(Key key)
    {
        if (playerA is null) return;
        switch (key)
        {
            case Key.Space:
                playerA.Paused = !playerA.Paused;
                if (poseMode != PoseMode.Single && playerB is not null) playerB.Paused = playerA.Paused;
                break;
            case Key.Left:
                playerA.Step(-1.0 / 30.0);
                playerA.Paused = true;
                break;
            case Key.Right:
                playerA.Step(1.0 / 30.0);
                playerA.Paused = true;
                break;
        }
    }

    public void OnMouseDown(MouseButton button)
    {
        if (button != MouseButton.Left) return;
        dragging = true;
        pressPosition = pointer;
        pressTravel = 0f;
    }

    public void OnMouseUp(MouseButton button)
    {
        if (button != MouseButton.Left) return;
        dragging = false;

        // Four logical pixels of slop, because a click always moves a little.
        if (pressTravel <= 4f) PickAt(pressPosition);
    }

    public void OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        pointer = new Vector2(x, y);
        if (!dragging) return;

        pressTravel += MathF.Abs(deltaX) + MathF.Abs(deltaY);
        yaw -= deltaX * 0.008f;
        pitch = Math.Clamp(pitch + deltaY * 0.006f, 0.08f, 1.45f);
    }

    /// <summary>
    /// Selects the nearest node whose world bounds the pointer's ray enters.
    /// </summary>
    /// <remarks>
    /// <b>The first caller ViewPicking has ever had.</b> It was built and tested with the view arc and
    /// nothing used it — which is its own kind of unverified, however many assertions cover the maths.
    /// <para>
    /// Bounds rather than triangles, deliberately. A node's AABB is what the lab already computes and
    /// draws, so what gets picked is exactly what is outlined; picking against geometry the viewer does
    /// not show would select things for reasons the picture cannot explain. Triangle-accurate picking is
    /// a different question, and one no consumer has asked.
    /// </para>
    /// </remarks>
    private void PickAt(Vector2 position)
    {
        if (mainView is not { } view) return;
        if (ViewPicking.RayThrough(view, position) is not { } ray) return;

        if (rig is not null)
        {
            PickBone(ray);
            return;
        }

        if (model is null) return;

        var best = -1;
        var nearest = float.MaxValue;
        for (var i = 0; i < model.Nodes.Count; i++)
        {
            var node = model.Nodes[i];
            if (node.PrimitiveCount == 0) continue;

            var lo = Vector3.Transform(node.BoundsMin, modelTransform);
            var hi = Vector3.Transform(node.BoundsMax, modelTransform);
            var bounds = new Bounds3(Vector3.Min(lo, hi), Vector3.Max(lo, hi));

            if (Intersection.Raycast(ray, bounds) is not { } hit) continue;
            if (hit.Time >= nearest) continue;
            nearest = hit.Time;
            best = i;
        }

        // Clicking empty space clears, which is what every viewport does and what a reader expects.
        selectedNode = best;
    }

    /// <summary>Selects the joint whose drawn cross the pointer's ray enters.</summary>
    /// <remarks>
    /// <b>Against the cross, not against the mesh.</b> The same rule the node picking follows: what
    /// gets picked is exactly what is drawn, so a click lands on the thing the eye was aiming at.
    /// Picking against the skinned triangles instead would be more "accurate" and would select bones
    /// the picture cannot explain — an elbow through a sleeve, a hip through a tunic.
    /// <para>
    /// Hidden bones are unpickable, which is the other half of the same rule: with the IK controls
    /// filtered out, clicking near the feet must not silently select <c>control-heel-roll.r</c>.
    /// </para>
    /// </remarks>
    private void PickBone(Ray ray)
    {
        if (rig is null) return;

        var place = rig.MeshNodeTransform * SubjectPlacement;
        var span = LabSkeletonView.Span(
            rig.Skeleton, boneWorlds, place, deformBonesOnly ? rig.DeformHierarchy : null);

        // Twice the joint cross's own arm, so a click needs to be close but not surgical — the same
        // slack the four-pixel click/drag threshold grants the gesture one layer up.
        var reach = MathF.Max(0.005f, span * 0.012f) * gizmoScale * 2f;

        var best = -1;
        var nearest = float.MaxValue;
        for (var i = 0; i < rig.Skeleton.BoneCount; i++)
        {
            if (deformBonesOnly && !rig.DeformHierarchy[i]) continue;

            var world = boneWorlds[i] * place;
            var at = new Vector3(world.M41, world.M42, world.M43);
            var bounds = new Bounds3(at - new Vector3(reach), at + new Vector3(reach));
            if (Intersection.Raycast(ray, bounds) is not { } hit) continue;
            if (hit.Time >= nearest) continue;
            nearest = hit.Time;
            best = i;
        }

        selectedBone = best;
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
        rig?.Dispose();
        model?.Dispose();
        renderer.Dispose();
    }
}
