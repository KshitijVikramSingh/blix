using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Runtime.Silk;
using Blix.Tools.Studio;

namespace HelloBlix3D;

public static class Program
{
    public static int Main(string[] args) => BlixApps.Main(args);

    [BlixApp("hello-3d", Summary = "a character on a lit stage, its look and its clip on the overlay", Headed = true, Default = true)]
    public static int Hello3D(AppArgs args)
    {
        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Hello Blix 3D",
            Width = 1280,
            Height = 720,
        });

        using var window = new Window(new Stage(args), options);
        window.Run();
        return 0;
    }

    // The same character, read on the CPU with no window: the check is that the stage has
    // something to show, and that the clip it opens on is one the character has.
    [BlixApp("hello-3d-check", Summary = "read the character headless and check the stage can play it")]
    public static int Check()
    {
        var character = new GltfImporter().Import(new AssetImportContext(AssetId.Parse("rogue"), Stage.Character));
        var clips = character.Animations.Select(clip => clip.Name).ToHashSet();
        var correct = character.Skeleton.BoneCount > 0 && clips.Contains(Playback.Opening);
        Console.WriteLine(correct
            ? $"hello-3d-check: {character.Skeleton.BoneCount} bones, {clips.Count} clips, opening on {Playback.Opening}"
            : $"hello-3d-check: the character has no bones, or no clip called {Playback.Opening}");
        return correct ? 0 : 1;
    }
}

// Which clip the character plays, and how fast. Both fields are sliders and text boxes on the overlay (press `),
// and flags on the command line: --clip Idle --speed 0.5.
internal sealed class Playback
{
    public const string Opening = "Cheer";

    [Tune] public string Clip { get; set; } = Opening;
    [Tune(0, 2)] public float Speed { get; set; } = 1f;
}

internal sealed class Stage(AppArgs args) : Game, IDebuggable
{
    public static string Character => AppFiles.Asset("models", "Rogue.glb");

    private readonly StudioRenderer stage = new();
    // The engine's camera: look, orbit, fly and zoom (see Drive). Framed round the character the way
    // Studio frames its subjects; --cam, or F12's dump of it, puts it back anywhere.
    private readonly CameraController camera = StudioFraming.Around(StudioFraming.SubjectCentre, -16.6f, 12f, 5.18f);
    private readonly Playback playback = new();
    private StudioRig rig = null!;
    private RigInstances body = null!;
    private ObjectTunables tunables = null!;
    private Matrix4x4 placement;
    private float aspect = 16f / 9f;

    protected override void OnLoad()
    {
        // The look this stage was tuned to on the overlay, and kept. Set before Load, because the
        // sky is baked from it there; the flags applied next can still override any of it.
        var look = stage.Look;
        look.SunElevation = 27.5f;
        look.AmbientStrength = 0.5f;
        look.ShadowSubjectRadius = 12f;
        look.ShadowDistance = 120f;
        look.HaloStrength = 1f;
        look.HaloTightness = 43.6f;
        look.HorizonBrightness = 0.2f;
        look.ZenithBrightness = 0.2f;
        look.Exposure = 0.5f;
        look.TonemapMode = TonemapCurve.Aces;

        // The sun, the shadows and the sky are the stage's own [Tune] settings, the camera's lens and feel are its own,
        // and the playback is ours.
        camera.FieldOfView = 0.8f;
        tunables = new ObjectTunables(playback, stage.Look, camera);
        tunables.Apply(args);
        camera.ReadArgs(args);

        stage.Load(GraphicsDevice);
        rig = StudioRig.Load(GraphicsDevice, Character, stage.SkinnedProgram);
        body = new RigInstances(rig, 1);

        // Two metres tall, standing on the ground, whatever size it was authored at.
        var scale = 2f / rig.LongestExtent;
        placement = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(0f, -rig.BoundsMin.Y * scale, 0f);
    }

    public override void OnUpdate(Time time)
    {
        camera.Drive(Host, (float)time.Delta);
        if (Host.Input[Key.Escape].Pressed) Host.RequestClose();

        body.Driven.Subject.Clip = rig.Clip(playback.Clip) ?? body.Driven.Subject.Clip;
        body.Driven.Subject.Rate = playback.Speed;
        body.Advance(time.Delta);
    }

    public override void OnRender(Time time, RenderFrameContext frame, RenderCommandList commands)
    {
        if (frame.Height > 0) aspect = frame.Width / (float)frame.Height;

        body.Pack(placement, spacing: 0f);
        rig.UploadPalettes(body.PalettesFor(0), 0);
        var character = new RigView(rig) { BoneWorlds = body.Driven.BoneWorlds, Placements = body.Placements };
        stage.Render(commands, camera.ViewProjection(aspect), camera.Position, new IStudioView[] { character });
    }

    public string DebugName => "stage";

    public void Debug(DebugContext debug)
    {
        camera.DescribeKeys(debug);
        debug.Keys.Describe(Key.Escape, "quit");
        tunables.BuildControls(debug);
    }

    public override void OnUnload()
    {
        rig.Dispose();
        stage.Dispose();
    }
}
