using Blix.Diagnostics;

namespace Blix.Demos.VulkanLit.Debug;

// Exposure + bloom. Exposure is also bound to Up/Down keys in LitLoop's
// input handler; both surfaces drive the same field, so a slider drag
// after a key press picks up wherever the keys left off.
internal sealed class ToneMapControls : IDebuggable
{
    private readonly LitLoop loop;

    public ToneMapControls(LitLoop loop)
    {
        this.loop = loop;
    }

    public string DebugName => "tonemap";

    public void Debug(DebugContext debug)
    {
        loop.Exposure = debug.Controls.Float("Exposure [Up/Dn]", loop.Exposure, 0.001f, 4.0f);
        loop.BloomEnabled = debug.Controls.Toggle("Bloom [B]", loop.BloomEnabled);
        loop.BloomIntensity = debug.Controls.Float("Bloom intensity", loop.BloomIntensity, 0.0f, 2.0f);
    }
}
