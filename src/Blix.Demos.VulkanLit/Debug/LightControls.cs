using Blix.Diagnostics;

namespace Blix.Demos.VulkanLit.Debug;

// Per-light on/off isolation toggles (X / C keys also drive them). Light
// positions / colours are baked at scene construction time and are not
// tunable from the overlay yet; they appear as read-only Values so a
// frame dump captures them.
internal sealed class LightControls : IDebuggable
{
    private readonly LitLoop loop;

    public LightControls(LitLoop loop)
    {
        this.loop = loop;
    }

    public string DebugName => "lights";

    public void Debug(DebugContext debug)
    {
        loop.SpotEnabled = debug.Controls.Toggle("Spot [X]", loop.SpotEnabled);
        loop.PointEnabled = debug.Controls.Toggle("Point [C]", loop.PointEnabled);

        debug.Values.Value("spot0-pos", LitLoop.Spot0Position);
        debug.Values.Value("spot1-pos", LitLoop.Spot1Position);
        debug.Values.Value("point-pos", LitLoop.PointPosition);
    }
}
