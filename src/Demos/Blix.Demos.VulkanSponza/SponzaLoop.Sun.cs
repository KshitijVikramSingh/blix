using System.Globalization;
using System.Numerics;
using Blix.Core;

namespace Blix.Demos.VulkanSponza;

// Where the sun stands: --sun (or the scene's default) at load, and --sun-change turning it once mid-run -- what RTGI's
// response to a light change is measured against.
internal sealed partial class SponzaLoop
{
    // --sun yaw,pitch (degrees), or the scene's own (SceneProfile.DefaultSunDegrees): set once the scene is located.
    private Vector2? sunOverrideDegrees;
    // The scene's own sun, before --sun or a move turned it (null until the scene is loaded): BandNorth fits the moving
    // sun's day around it.
    private Vector3? authoredSunDirection;
    // --sun-change F yaw pitch: the sun turned at post-load frame F (degrees, as --sun), once.
    private (int Frame, Vector2 Degrees)? sunChange;
    private bool sunChangeApplied;
    private int sunChangeAppliedAt;

    private void ReadSunArgs(AppArgs args)
    {
        if (args.Values("sun", 2) is [var sy, var sp]) sunOverrideDegrees = new Vector2(float.Parse(sy, CultureInfo.InvariantCulture), float.Parse(sp, CultureInfo.InvariantCulture));
        // The scene's own sun (SceneProfile.DefaultSunDegrees) where --sun, --sun-authored and --sun-overhead say none.
        else if (scene.DefaultSunDegrees is { } sceneSun && !args.Flag("sun-authored") && !args.Flag("sun-overhead")) sunOverrideDegrees = sceneSun;
        if (args.Values("sun-change", 3) is [var cf, var cy, var cp])
            sunChange = (int.Parse(cf, CultureInfo.InvariantCulture), new Vector2(float.Parse(cy, CultureInfo.InvariantCulture), float.Parse(cp, CultureInfo.InvariantCulture)));
    }

    // Each frame (SponzaLoop.Render.cs): --sun-change at or after its frame, once.
    private void ApplySunChangeIfDue()
    {
        if (sunChange is not { } change || sunChangeApplied || postLoadFrames < change.Frame) return;
        sunChangeApplied = true;
        sunChangeAppliedAt = postLoadFrames;
        sunYaw = change.Degrees.X * MathF.PI / 180f;
        sunPitch = change.Degrees.Y * MathF.PI / 180f;
        UpdateSunDirection();
        Console.WriteLine($"[VulkanSponza] sun turned by --sun-change at frame {postLoadFrames}: {sunDirection}");
    }
}
