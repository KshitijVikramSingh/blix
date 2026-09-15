using Blix.Diagnostics;

namespace Blix.Demos.VulkanLit.Debug;

// Camera tunables that the player would change live: fly speed, FOV,
// animation pause. The read-only camera state (position, forward, yaw,
// pitch) stays on LitLoop's own Debug() body as Values — it's
// observability, not tuning.
internal sealed class CameraTuningControls : IDebuggable
{
    private readonly LitLoop loop;

    public CameraTuningControls(LitLoop loop)
    {
        this.loop = loop;
    }

    public string DebugName => "camera-tuning";

    public void Debug(DebugContext debug)
    {
        loop.MoveSpeed = debug.Controls.Float("Fly speed", loop.MoveSpeed, 0.3f, 40f);
        loop.AnimPaused = debug.Controls.Toggle("Pause anim [P]", loop.AnimPaused);

        // FOV exposed in degrees (legible); stored internally in radians.
        // Guarding the writeback prevents float-to-deg-and-back drift from
        // mutating the field when the slider hasn't moved.
        var degIn = loop.FovYRadians * (180.0f / MathF.PI);
        var degOut = debug.Controls.Float("FOV (deg)", degIn, 30f, 110f);
        if (MathF.Abs(degOut - degIn) > 1e-4f)
        {
            loop.FovYRadians = degOut * (MathF.PI / 180.0f);
        }
    }
}
