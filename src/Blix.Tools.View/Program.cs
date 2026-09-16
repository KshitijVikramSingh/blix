using Blix.Assets;
using System.Numerics;
using Blix;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Tools.Studio;
using Blix.Runtime.Silk;
using ImGuiNET;
using Blix.Cooked;

namespace Blix.Tools.View;

// The toolchain lab's viewer — chassis conventions over a real lit scene.
//
// ── Proves ──────────────────────────────────────────────────────────────────
//   • Several executables over ONE lab: the shaders, scene and renderer live in
//     Blix.Tools.Studio and arrive here as content. This project declares no
//     shaders and has no render code.
//   • Reflected binding: every descriptor set, UBO offset and push range comes from
//     spirv-cross sidecars, not from a hand-written ShaderInterface.
//   • The shared build targets doing real work — BlixShaderMode=Library plus
//     BlixShaderReflect, which was extracted the moment this became the second
//     consumer that wanted it.
//   • Chassis: IUiSource panel, IInputHandler with UI capture, host-owned --frames,
//     named views and trails.
//   • Skeletal animation, made visible: ClipPlayer drives a pose, SkeletonGizmo
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
    [BlixApp("view", Summary = "look at a model or rig", Headed = true)]
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

        // --mask names the CLIP a masked layer plays, and picks the composition with it for the
        // same reason --blend does: a mode reachable only through a combo box is a mode no bounded
        // run and no capture can get to, which makes it a mode nothing checks.
        var maskClip = ArgValue(args, "--mask");

        // Which BONE that layer starts at is not parsed here, and neither is the weight, the
        // falloff, the lockstep or the root drive. Those are declared state on RigAnimation, so
        // their flags are derived from the members — --mask-root, --weight, --mask-falloff,
        // --lockstep, --drive-root — and applied once the session exists. Nothing below writes a
        // parser for them, which is the only way a flag and a panel stay one thing.

        // --instances N draws N copies of the rig, each on its own clock. One is the ordinary case
        // and takes exactly the same path as eight — there is no single-body shader.
        var instances = int.TryParse(ArgValue(args, "--instances"), out var n) ? n : 1;
        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Blix — toolchain lab",
            Width = 1280,
            Height = 760,
        });

        var loop = new ViewerLoop(modelPath, rigPath, clipName, blendClip, additiveClip, instances, maskClip, args);
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
    private readonly StudioRenderer renderer = new();
    private readonly List<IStudioView> views = new();
    private readonly string? modelPath;
    private StudioModel? model;
    private Matrix4x4 modelTransform = Matrix4x4.Identity;

    // What is selected and what a click selects. Its own class because picking has two genuinely
    // different questions in it (a model's node against drawn bounds, a rig's joint against a drawn
    // cross) and both were inline in a method that also did camera work.
    private readonly StudioSelection selection = new();

    // <b>Constructed here and called from here.</b> The root builds its parts and drives them; there
    // is no registry, no discovery and no "which tool is active" branch. A different executable over
    // this same lab simply builds a different root.
    private ViewerPanels panels = null!;

    // ── The rig half ────────────────────────────────────────────────────────
    // <b>The session owns the clocks and the poses; this owns what is SHOWN.</b> Composing a pose
    // from one or two clips, stripping the root when driving, packing one palette per instance and
    // counting how many distinct poses came out are decisions the capture tool makes too — the same
    // decisions on a different clock, which is the second consumer §4 asks for. What stays here is
    // the viewer's own policy: which clip a list click assigns to, what the panel shows, and where
    // the camera is.
    private readonly string? rigPath;
    private readonly string? clipName;
    private readonly string? secondClip;
    private readonly int requestedInstances;
    private readonly PoseMode startMode;
    private StudioRig? rig;
    private RigInstances? session;

    private Matrix4x4 rigBase = Matrix4x4.Identity;
    private Matrix4x4 rigTransform = Matrix4x4.Identity;
    private float rigScale = 1f;

    private readonly List<Vector3> rootPath = new();

    public ViewerLoop(
        string? modelPath = null,
        string? rigPath = null,
        string? clipName = null,
        string? blendClip = null,
        string? additiveClip = null,
        int instances = 1,
        string? maskClip = null,
        string[]? args = null)
    {
        this.modelPath = modelPath;
        this.rigPath = rigPath;
        this.clipName = clipName;
        this.args = args ?? Array.Empty<string>();
        requestedInstances = instances;
        secondClip = blendClip ?? additiveClip ?? maskClip;
        startMode = blendClip is not null ? PoseMode.Blend
            : additiveClip is not null ? PoseMode.Additive
            : maskClip is not null ? PoseMode.Masked
            : PoseMode.Single;
    }

    // Kept whole rather than pre-parsed, because the declared flags cannot be applied until the
    // subject they write to exists — and the subject is a rig that has not been loaded yet.
    private readonly string[] args;


    // <b>Two cameras, one class.</b> The window's view and the panel's viewport each carried their
    // own yaw/pitch/distance and their own copy of the spherical-to-cartesian arithmetic — the same
    // decision written twice inside one file, which is the §4 bar met without either copy leaving
    // the building. StudioCamera owns the orbit; where a drag came from stays here, because the two
    // arrive by genuinely different routes (the host for one, an ImGui item for the other).
    private readonly StudioCamera camera = new(yaw: 0.7f, pitch: 0.45f, distance: 11f);
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

    // ── Stage A of the view arc ─────────────────────────────────────────────
    // Ids the HOST gave us for textures the UI can draw. Registered once at load rather than per
    // frame: an id is a dictionary entry, and minting one every frame would grow that dictionary
    // forever for a picture that never changed.
    private readonly List<(string Label, nint Id, int Width, int Height)> uiImages = new();
    private nint shadowMapId;

    // ── Stage B of the view arc: a second camera, shown in a panel ───────────
    // Its own yaw/pitch/distance, because a viewport that follows the main camera is a mirror and
    // proves nothing about views. Starts looking along a different axis so the two pictures are
    // obviously not the same picture.
    private nint viewportId;
    private readonly StudioCamera viewportCamera = new(yaw: -1.4f, pitch: 0.25f, distance: 7f)
    {
        // Looking a little higher than the main view, and from the other side, so the two pictures
        // are obviously not the same picture.
        Target = new Vector3(0f, 1.2f, 0f),
        MinDistance = 2.5f,
        MaxDistance = 30f,
        MinPitch = 0.05f,
    };

    private Matrix4x4 viewportViewProjection = Matrix4x4.Identity;
    private Vector3 viewportCameraPosition;

    // <b>The panel's rectangle, from LAST frame.</b> UI layout runs after the views are declared
    // and after the scene is recorded, so the rect a panel occupies this frame does not exist when
    // the picture for it is drawn. Every immediate-mode editor answers this the same way: draw at
    // the size the panel was, and wear one frame of stale aspect on a resize.
    //
    // Written down rather than hidden because stage D has to decide whether that lag is what the
    // substrate wants or whether layout and submission should be split so the rect is known first.
    private Vector2 viewportPanelSize = new(480f, 270f);

    // The aspect the viewport's projection was actually built with, kept so it can be published
    // beside the target's rather than assumed to match it.
    private float viewportAspect = 16f / 9f;
    private Vector2 viewportImageMin;
    private Vector2 viewportImageSize;

    // ── What the panels may read ────────────────────────────────────────────
    // Deliberately a list rather than "make everything internal": each line here is a thing the UI
    // is allowed to know about, and adding one is a decision rather than a side effect.
    internal StudioRenderer Renderer => renderer;


    internal StudioModel? Model => model;

    internal Matrix4x4 ModelTransform => modelTransform;

    internal StudioRig? Rig => rig;

    internal RigInstances? Session => session;

    // Declared state, rendered as flags at startup and watched for movement every frame. Every
    // member is Bespoke: this viewer already draws better controls than reflection could — a combo
    // of THIS rig's bone names beats a text field — so what it takes from here is the two things a
    // panel cannot do for itself, which are the command line and knowing that something moved.
    private ObjectTunables? tunables;

    // <b>Repeatable, and empty by default.</b> Four of the Rogue's six attachments hang off one
    // joint, so drawing all of them is four meshes in the same place — a picture of nothing. A flag
    // rather than only a panel, for the same reason --mask is one: a mode reachable only through a
    // checkbox is a mode no bounded run and no capture can get to.
    private readonly HashSet<string> visibleAttachments = new(StringComparer.Ordinal);

    // Per-body overrides, sparse on purpose: a body with no entry wears whatever `visibleAttachments`
    // says, so the common case (everyone carries the same thing) costs no per-body state at all and
    // the panel only writes an entry when someone asks for a difference.
    private readonly Dictionary<int, HashSet<string>> perInstanceAttachments = new();

    /// <summary>
    /// Which attachments are drawn. Mutated by the panel and read when the view is rebuilt.
    /// </summary>
    /// <remarks>
    /// <b>The viewer owns this, not the rig and not the session.</b> Which of four knives a
    /// character holds is a decision about what you want to look at, and the engine has no opinion
    /// — the same rule the mask follows, where the engine composes poses from weights and knows
    /// nothing about who set them.
    /// </remarks>
    internal ISet<string> VisibleAttachments => visibleAttachments;

    /// <summary>What body <paramref name="instance"/> carries — its override, or the shared set.</summary>
    internal ISet<string> AttachmentsForInstance(int instance) =>
        perInstanceAttachments.TryGetValue(instance, out var own) ? own : visibleAttachments;

    /// <summary>Gives a body a set of its own, seeded from the shared one. Idempotent.</summary>
    internal ISet<string> OverrideAttachmentsFor(int instance)
    {
        if (perInstanceAttachments.TryGetValue(instance, out var own)) return own;
        own = new HashSet<string>(visibleAttachments, StringComparer.Ordinal);
        perInstanceAttachments[instance] = own;
        return own;
    }

    /// <summary>Hands a body back to the shared set.</summary>
    internal void ClearAttachmentOverride(int instance) => perInstanceAttachments.Remove(instance);

    /// <summary>Whether this body has a set of its own.</summary>
    internal bool HasAttachmentOverride(int instance) => perInstanceAttachments.ContainsKey(instance);

    internal StudioSelection Selection => selection;

    internal StudioCamera ViewportCamera => viewportCamera;

    internal IReadOnlyList<(string Label, nint Id, int Width, int Height)> UiImages => uiImages;

    internal nint ShadowMapId => shadowMapId;

    internal nint ViewportId => viewportId;

    internal int Frames => frames;

    internal int RootPathCount => rootPath.Count;

    internal ViewDeclaration? ViewportView => viewportView;

    internal Vector2 ViewportImageMin { get => viewportImageMin; set => viewportImageMin = value; }

    internal Vector2 ViewportImageSize { get => viewportImageSize; set => viewportImageSize = value; }

    internal Vector2 ViewportPanelSize { get => viewportPanelSize; set => viewportPanelSize = value; }

    internal IRenderHost? Host => host;

    internal void ResetTravel() => ResetRootTravel();

    internal Matrix4x4 SubjectPlacementOf => SubjectPlacement;

    internal void PickThrough(in ViewDeclaration view, Vector2 pointer) => Pick(view, pointer);

    /// <summary>The whole UI, drawn by the panels the root constructed.</summary>
    public void DrawUi() => panels.Draw();

    public string DebugName => "lab";

    public string UiName => "lab";

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        panels = new ViewerPanels(this);
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

        // <b>The same catch the judge has, for the same reason.</b> AssetImportException is the
        // engine refusing a file by name; anything else escaping here is a fault in this tool.
        // Without it a bad asset took the whole process down with a stack trace AFTER the window
        // had opened — which reads as "the viewer is broken" rather than "that file is not a glTF".
        try
        {
            model = StudioModel.Load(vk, modelPath);
        }
        catch (AssetImportException refused)
        {
            Console.Error.WriteLine($"blix cannot read this: {refused.Message}");
            Environment.Exit(1);
        }

        // The model is the subject now; the box ring is a backdrop that hides it.

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

        try
        {
            rig = StudioRig.Load(vk, rigPath, renderer.SkinnedProgram);
        }
        catch (AssetImportException refused)
        {
            Console.Error.WriteLine($"blix cannot read this: {refused.Message}");
            Environment.Exit(1);
        }
        if (rig.Skeleton.BoneCount > StudioRig.MaxBones)
        {
            // Said here rather than discovered on the GPU: past the shader's array bound the draw
            // reads whatever follows the buffer, which renders as a character exploded across the
            // map with no validation error to explain it.
            Console.Error.WriteLine(
                $"{Path.GetFileName(rigPath)} has {rig.Skeleton.BoneCount} bones; the lab's skinned " +
                $"shader holds {StudioRig.MaxBones}. Raise the bound in studio_skinned.vert.");
        }

        session = new RigInstances(rig, requestedInstances)
        {
            // <b>The viewer's own look, set by the viewer.</b> Each body runs 17% faster than the one
            // before it, so bodies that start together visibly drift apart — the point being that a
            // glance shows they are not frame-locked. This lived inside the animation's own Advance,
            // where it was an aesthetic the shared type had no business holding and the capture tool
            // could not opt out of. Conventions §6.
            Step = (body, i, delta) =>
            {
                body.Subject.Clip = rig.Clips.Count > 0 ? rig.Clips[session!.ClipIndexFor(i)] : null;
                body.Subject.Paused = session!.Driven.Subject.Paused;
                body.Subject.Rate = session.Driven.Subject.Rate * (1f + (i * 0.17f));
                body.Advance(delta);
            },
        };
        session.Driven.Mode = startMode;
        // TWO targets. The session's knobs are Bespoke because this viewer draws better controls
        // for them; the SCENE's are not, so the stage's look is rendered by reflection — a Scene
        // panel this file does not write, and --sun-elevation it does not parse.
        // THREE targets now, not two. The set owns what is true of the row — how many bodies, are
        // they locked together — and each body owns its own composition. Binding only the set left
        // five knobs declared on a type nobody had bound: --mode, --weight, --mask-root,
        // --mask-falloff and --drive-root all parsed to nothing, silently, because nameof survives
        // a member moving to another class.
        tunables = new ObjectTunables(session, session.Driven, renderer);

        var bespoke = new[]
        {
            nameof(RigAnimation.Mode), nameof(RigAnimation.Weight), nameof(RigAnimation.MaskRoot),
            nameof(RigAnimation.MaskFalloff), nameof(RigInstances.Lockstep), nameof(RigInstances.DriveRoot),
        };

        // Checked, because the failure above has no symptom: a dropped flag and an absent flag look
        // the same from a window. This is the same rule the attachment names below already follow.
        tunables.RequireDeclared(bespoke);
        foreach (var member in bespoke) tunables.Bespoke.Add(member);

        // Every declared flag, parsed by nobody. A bad value throws with the member's own range or
        // option list in the message, which is more than the hand-written parsing it replaced ever
        // said — but it throws HERE, after the window is open, because a flag that writes a subject
        // cannot be applied before the subject exists and this subject is a rig that had to load
        // first. So it is caught and reported rather than allowed to surface as a stack trace
        // through Window.Run, which is what it did the first time.
        try
        {
            tunables.Apply(args);
        }
        catch (ArgumentException bad)
        {
            Console.Error.WriteLine(bad.Message);
            Environment.Exit(1);
        }

        // Named attachments, applied once the rig is loaded because a name only means something
        // against a rig. An unknown name is reported rather than ignored: a flag that landed and a
        // flag that was dropped look identical from a window.
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--attach") continue;
            var wanted = args[i + 1];
            if (rig.Attachments.Any(a => string.Equals(a.Name, wanted, StringComparison.Ordinal)))
            {
                visibleAttachments.Add(wanted);
            }
            else
            {
                Console.Error.WriteLine(
                    $"No attachment named '{wanted}'. This rig has: " +
                    (rig.Attachments.Count == 0
                        ? "none"
                        : string.Join(", ", rig.Attachments.Select(a => a.Name))));
            }
        }

        if (rig.Attachments.Count > 0)
        {
            Console.WriteLine(
                $"  {rig.Attachments.Count} attachment(s): " +
                string.Join(", ", rig.Attachments.Select(a => $"{a.Name}@{a.JointName}")) +
                $" — showing {visibleAttachments.Count}");
        }

        // A mask before anyone asks for one, because the first thing anybody does in this mode is
        // pick a spine. The guess is named as a guess in RigAnimation and the combo corrects it in
        // one click. --mask-root has already been applied above, so it wins by simply being there.
        var chosenRoot = session.Driven.MaskRoot.Length > 0
            ? session.Driven.MaskRoot
            : RigAnimation.GuessUpperBodyRoot(rig.Skeleton);

        var falloff = session.Driven.MaskFalloff > 0 ? session.Driven.MaskFalloff : 2;
        if (chosenRoot is null)
        {
            Console.WriteLine("  no bone matches the usual spine names — pick one in the mask panel");
        }
        else if (!session.Driven.SetMask(chosenRoot, falloff))
        {
            Console.WriteLine($"  no bone named '{chosenRoot}' — the mask is empty until one is picked");
        }
        else
        {
            // Said out loud, because a flag that landed and a flag that was ignored look identical
            // from a window. The capture has reported this since it was written; the viewer never
            // did, and a bounded run was the one place it mattered most.
            Console.WriteLine(
                $"  mask from '{chosenRoot}' falloff {falloff}: reaches {session.Driven.Mask!.Reach()} of " +
                $"{rig.Skeleton.BoneCount} bones, {session.Driven.Mask.Reach(0.999f)} fully");
        }

        // Prefer a walk on A and an idle on B when the asset has them: a blend between two named
        // gaits is the case the weight slider was built to show, and finding it by hand in a
        // seventy-six-clip list every launch is a tax on the thing being demonstrated.
        panels.SubjectClipIndex = clipName is not null
            ? NamedClip(rig, clipName)
            : PreferredClip(rig, new[] { "Walking_A", "Walking", "Walk", "Running_A", "Run" }, 0);
        panels.SecondaryClipIndex = secondClip is not null
            ? NamedClip(rig, secondClip)
            : PreferredClip(rig, new[] { "Running_A", "Running", "Run", "Idle", "Unarmed_Idle" }, 1);
        if (rig.Clips.Count > 0) session.Driven.Subject.Clip = rig.Clips[panels.SubjectClipIndex];
        if (rig.Clips.Count > 1) session.Driven.Secondary.Clip = rig.Clips[panels.SecondaryClipIndex];
        session.Driven.Refresh();


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

    private static int NamedClip(StudioRig rig, string name)
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

    private static int PreferredClip(StudioRig rig, string[] names, int fallback)
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
        cameraPosition = camera.Position;
        viewProjection = camera.ViewProjection(aspect);

        // The viewport's own camera. Aspect comes from the PANEL, not the window — that is the
        // whole difference between a second view and a second copy of this one, and it is why
        // StudioCamera takes the aspect rather than storing it.
        // <b>The TARGET's aspect, not the panel's.</b> The viewport target is half the swapchain on
        // both sides, so it carries the window's aspect; the panel then letterboxes the picture to
        // that same aspect. Projecting through the panel's aspect instead made three numbers out of
        // one — measured at 2.089 against a target of 1.684 in a default window, a 24% horizontal
        // stretch that turns a sphere into an ellipse and throws every pick off by the same factor.
        //
        // The fit's own comment had stated the invariant all along: stretching "would make the
        // picture disagree with the projection it was drawn through, and every ray cast into it
        // afterwards would be wrong by that same stretch". It was the caller breaking it.
        //
        // A panel-shaped picture would need a panel-shaped target, which means resizing a graph
        // resource as a window is dragged. Letterboxing is the cheaper answer and the one the panel
        // was already written for.
        viewportCameraPosition = viewportCamera.Position;
        viewportAspect = aspect;
        viewportViewProjection = viewportCamera.ViewProjection(viewportAspect);

        UpdateRig(time.Delta);
    }

    /// <summary>Advances the session and integrates where its root has been.</summary>
    /// <remarks>
    /// What is left here is the viewer's share of the work: deciding where the bodies stand, and
    /// remembering the path so it can be drawn. The clocks, the composition and the palette packing
    /// are <see cref="RigAnimation"/>'s, because the capture tool makes exactly those decisions too.
    /// </remarks>
    private void UpdateRig(double delta)
    {
        if (rig is null || session is null) return;

        session.Advance(delta);

        // <b>Scaled ONCE.</b> RootTravel is already in post-MeshNodeTransform space and rigBase
        // already carries the normalising scale, so a `* rigScale` here squared it — the Rogue
        // normalises by 1.372, so the body ran 1.88x too far and the trail agreed with it, which is
        // why two wrong things looked like one right one.
        // <b>No travel here any more.</b> This used to fold the driven body's travel into the
        // origin the whole row is laid on, which moved every body by one body's clip. Pack applies
        // each body's own travel to its own slot; this is just where the row stands.
        rigTransform = rigBase;

        // Bodies stand in a row across the camera's view, a stride apart, centred on the origin so
        // one body sits where one body always did. Where they stand is the lab's choice, which is
        // why the session takes it rather than inventing one.
        var spacing = MathF.Max(1.2f, rig.LongestExtent * rigScale * 0.75f);
        session.Pack(rigTransform, spacing);

        // Sampled in the space the trail is drawn in, so the line is where the body would be.
        var where = Vector3.Transform(session.Driven.RootTravel, rigBase);
        if (rootPath.Count == 0 || Vector3.DistanceSquared(rootPath[^1], where) > 1e-6f)
        {
            rootPath.Add(where);
            // Bounded, because an unbounded path is a memory leak wearing a gizmo. Long enough that
            // several loops of a walk are visible at once, which is the length the wrap bug needs to
            // be seen at — one cycle's worth would hide exactly the seam being looked for.
            if (rootPath.Count > 2048) rootPath.RemoveAt(0);
        }
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frames++;
        if (frame.Height > 0) aspect = frame.Width / (float)frame.Height;
        viewProjection = camera.ViewProjection(aspect);

        // Before recording, because the palette buffer is read at Execute and written here — the
        // draw carries a descriptor set, not a copy of the matrices.
        // Every skin's buffer, once per frame, before recording. Uploading only skin 0's left
        // tank.glb's tracks reading whatever the buffer happened to hold.
        if (rig is not null && session is not null)
        {
            for (var s = 0; s < session.SkinCount; s++) rig.UploadPalettes(session.PalettesFor(s), s);
        }

        // What this tool puts on the stage. Rebuilt per frame rather than cached, because the
        // instance count follows the session and a stale RigView would draw last frame's crowd.
        views.Clear();
        if (model is not null) views.Add(new ModelView(model, modelTransform));
        if (rig is not null)
        {
            // <b>The worlds come from the session, not from here.</b> They are already computed once
            // per pose for the skeleton gizmo; an attachment is the second reader of the same
            // number and recomputing them would be a second hierarchy walk for one knife.
            var rigView = new RigView(rig, session?.PalettesFor(0).Count ?? 0)
            {
                BoneWorlds = session?.Driven.BoneWorlds,
                Placement = rigTransform,
                // Every body wears its gear at ITS OWN pose. Before this the attachments were drawn
                // once, at instance 0's, so eight peasants shared one knife hanging off the first
                // one's hand. The delegate is evaluated at draw time on purpose — see RigView.
                InstanceBoneWorlds = session is null ? null : session.BoneWorldsFor,
                Placements = session?.Placements,
                InstanceAttachments = session is null ? null : AttachmentsForInstance,
            };
            foreach (var name in visibleAttachments) rigView.VisibleAttachments.Add(name);
            views.Add(rigView);
        }

        renderer.Render(
            commandList, viewProjection, cameraPosition, views,
            panels.ViewportOpen ? viewportViewProjection : null, viewportCameraPosition);
    }

    public void Debug(DebugContext debug)
    {
        // <b>Watching, not rendering.</b> Every member is Bespoke — this viewer's own controls are
        // better than reflection could generate — so nothing is drawn from here. What it takes is
        // the thing a panel cannot do for itself: notice that state moved, and let the session
        // recompose through ITunable.OnChanged.
        //
        // The five panel Refresh() calls this replaced turned out to have been REDUNDANT: Advance
        // composes every frame and the viewer advances every frame, so each of them composed a
        // second time in the same frame. Their cost was never the work, it was the belief that you
        // had to remember one after every write — and the tool that did NOT remember is the
        // capture, which composes once and for which this is load-bearing rather than ceremony.
        //
        // Three remain and all three are honest: one at startup before anything has advanced, and
        // two behind frame-step keys that move ClipPlayer.Time, which is not declared state and so
        // is not watched here.
        tunables?.BuildControls(debug);

        debug.State.DepthTestDrawing = panels.DepthTestGizmos;
        debug.Values.Value("frames", frames);
        debug.Values.Value("sun", renderer.SunDirection);

        // <b>Three aspects that must be one number, published so they can be checked.</b> The
        // viewport target is half the swapchain, the panel letterboxes to that same aspect, and the
        // projection is read back out of the matrix it was actually built with rather than from the
        // value that was meant to go in. A bounded run reports all three, so "they agree" is an
        // observation and not a comment — and it was False before this was fixed.
        // MatchSwapchainGraphSize(0.5) halves both sides, so the target carries the window's shape.
        var targetAspect = aspect;
        var panelAspect = viewportPanelSize.Y > 1f ? viewportPanelSize.X / viewportPanelSize.Y : 0f;
        debug.Values.Value("aspect/viewport-target", targetAspect);
        debug.Values.Value("aspect/viewport-projection", viewportAspect);
        debug.Values.Value("aspect/panel", panelAspect);
        debug.Values.Value("aspect/agree", MathF.Abs(viewportAspect - targetAspect) < 0.01f);

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
        if (panels.ViewportOpen && viewportImageSize.X > 1f && viewportImageSize.Y > 1f)
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
        var sunFrom = renderer.SunDirection * 7f;
        debug.Draw.Arrow("sun", sunFrom, Vector3.Zero, new GraphicsColor(1f, 0.9f, 0.5f, 1f));

        if (model is not null) DrawModelGizmos(debug);
        if (rig is not null) DrawRigGizmos(debug);

        if (trails && panels.ShowTrail)
        {
            // Where the sun has been while it was being dragged.
            debug.Draw.Trail("sun/path", sunFrom, new GraphicsColor(1f, 0.75f, 0.3f, 1f), panels.TrailSeconds);
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
        if (rig is null || session is null) return;

        using var scope = debug.Scope("rig");

        if (panels.ShowSkeleton)
        {
            var options = SkeletonGizmo.Options.Default with
            {
                Joints = panels.ShowJoints,
                RestGhost = panels.ShowRestGhost,
                AllAxes = panels.ShowAllBoneAxes,
                LeafStubs = panels.ShowLeafStubs,
                Scale = panels.GizmoScale,
            };

            // <b>One skeleton per instance, and that IS the proof.</b> Three bodies mid-stride at
            // three phases is suggestive; three SKELETONS in different shapes in one frame is
            // conclusive — a single palette read three times would draw the same pose three times
            // and the meshes would agree with each other.
            //
            // Scoped per instance so the debug names do not collide: `bone/17` means something
            // different under `i0` than under `i2`, and trails key off the name.
            DrawInstanceSkeleton(debug, 0, session!.Driven.BoneWorlds, options);
            for (var i = 0; i < (session!.Count - 1) && i + 1 < session.Placements.Count; i++)
            {
                DrawInstanceSkeleton(debug, i + 1, session.BoneWorldsFor(i + 1), options);
            }
        }

        if (!panels.ShowRootTrail) return;

        var head = rootPath.Count > 0 ? rootPath[^1] : Vector3.Transform(Vector3.Zero, rigBase);
        debug.Draw.Trail("root/recent", head, new GraphicsColor(0.4f, 1f, 0.7f, 1f), panels.TrailSeconds);
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
        DebugContext debug, int instance, IReadOnlyList<Matrix4x4> worlds, SkeletonGizmo.Options options)
    {
        if (rig is null || session is null || instance >= session.Placements.Count) return;

        using var scope = debug.Scope($"i{instance}");
        SkeletonGizmo.Draw(
            debug,
            rig.Skeleton,
            worlds,
            rig.MeshNodeTransform * session.Placements[instance],
            options,
            instance == 0 ? selection.Bone : -1,
            instance == 0 && panels.ShowRestGhost ? session.Driven.RestWorlds : null,
            panels.DeformBonesOnly ? rig.DeformHierarchy : null,
            // Painted only in the mode where a mask means anything. In the others the skeleton's
            // colours already say something — which bone is selected — and two meanings on one
            // channel is how an overlay stops being read at all.
            session.Driven.Mode == PoseMode.Masked ? session.Driven.Mask : null);
    }





    /// <summary>Where instance 0 stands. The subject is offset when there are echoes beside it.</summary>
    private Matrix4x4 SubjectPlacement =>
        session is { Placements.Count: > 0 } ? session.Placements[0] : rigTransform;

    private void ResetRootTravel()
    {
        session?.ResetTravel();
        rootPath.Clear();
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

        if (panels.ShowPivots)
        {
            for (var i = 0; i < model.Nodes.Count; i++)
            {
                var node = model.Nodes[i];
                var world = node.WorldTransform * modelTransform;
                var origin = new Vector3(world.M41, world.M42, world.M43);

                // A transform-only node is usually an armature or a rig pivot — terser, because a
                // 96-node tree of full triads is unreadable.
                if (node.PrimitiveCount == 0 && !panels.ShowAllPivots) continue;
                var axisLength = node.PrimitiveCount > 0 ? size : size * 0.5f;
                var selected = i == selection.Node;

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

        if (!panels.ShowBounds) return;

        var min = Vector3.Transform(model.BoundsMin, modelTransform);
        var max = Vector3.Transform(model.BoundsMax, modelTransform);
        debug.Draw.Aabb("bounds", Vector3.Min(min, max), Vector3.Max(min, max),
            new GraphicsColor(0.9f, 0.85f, 0.4f, 0.9f));

        if (selection.Node >= 0 && selection.Node < model.Nodes.Count)
        {
            var node = model.Nodes[selection.Node];
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




    public void OnKeyDown(Key key)
    {
        if (session is null) return;
        switch (key)
        {
            case Key.Space:
                session.Driven.Subject.Paused = !session.Driven.Subject.Paused;
                if (session.Driven.Mode != PoseMode.Single) session.Driven.Secondary.Paused = session.Driven.Subject.Paused;
                break;
            case Key.Left:
                session.Driven.Subject.Step(-1.0 / 30.0);
                session.Driven.Subject.Paused = true;
                session.Driven.Refresh();
                break;
            case Key.Right:
                session.Driven.Subject.Step(1.0 / 30.0);
                session.Driven.Subject.Paused = true;
                session.Driven.Refresh();
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
        camera.Orbit(deltaX, deltaY);
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
        if (mainView is { } view) Pick(view, position);
    }

    /// <summary>Turns a pointer into a selection through one view. See <see cref="StudioSelection"/>.</summary>
    private void Pick(in ViewDeclaration view, Vector2 pointer) =>
        selection.PickThrough(
            view,
            pointer,
            rig,
            SubjectPlacement,
            session?.Driven.BoneWorlds ?? Array.Empty<Matrix4x4>(),
            panels.DeformBonesOnly ? rig?.DeformHierarchy : null,
            panels.GizmoScale,
            model,
            modelTransform);

    public void OnMouseWheel(float offsetX, float offsetY)
    {
        camera.Zoom(offsetY);
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
