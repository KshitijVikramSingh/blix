using System.Numerics;
using Blix.Diagnostics;

namespace Blix.Demos.VulkanLit.Debug;

// Sun + ambient knobs. The sun direction is edited as yaw / pitch (radians)
// rather than a raw Vector3 so the sliders stay well-behaved — recomputing
// the unit vector each frame and pushing it back to the LitLoop. Pitch is
// clamped to a "always somewhat downward" range, matching the SponzaModern
// convention; a level with an upward-pointing sun would lift the upper
// bound back toward 0.
internal sealed class SunControls : IDebuggable
{
    private readonly LitLoop loop;
    private float yaw;
    private float pitch;
    private bool initialised;

    public SunControls(LitLoop loop)
    {
        this.loop = loop;
    }

    public string DebugName => "sun";

    public void Debug(DebugContext debug)
    {
        if (!initialised)
        {
            (yaw, pitch) = ToYawPitch(loop.SunDirection);
            initialised = true;
        }

        yaw = debug.Controls.Float("Yaw (rad)", yaw, -MathF.PI, MathF.PI);
        pitch = debug.Controls.Float("Pitch (rad)", pitch, -MathF.PI / 2f + 0.05f, -0.05f);

        var cp = MathF.Cos(pitch);
        loop.SunDirection = Vector3.Normalize(new Vector3(
            MathF.Sin(yaw) * cp,
            MathF.Sin(pitch),
            MathF.Cos(yaw) * cp));

        loop.SunIntensity = debug.Controls.Float("Intensity", loop.SunIntensity, 0f, 8f);
        loop.AmbientIntensity = debug.Controls.Float("Ambient (IBL)", loop.AmbientIntensity, 0f, 4f);
        loop.SunEnabled = debug.Controls.Toggle("Sun [Z]", loop.SunEnabled);

        debug.Values.Value("direction", loop.SunDirection);
        debug.Values.Value("ambient-color", loop.AmbientColor);
    }

    private static (float yaw, float pitch) ToYawPitch(Vector3 d)
    {
        // Pitch from Y component (asin); negative means light points down.
        // Yaw is rotation around +Y, atan2(X, Z) — matches the reconstruction
        // above so a no-op round-trip is the identity transform.
        return (MathF.Atan2(d.X, d.Z), MathF.Asin(d.Y));
    }
}
