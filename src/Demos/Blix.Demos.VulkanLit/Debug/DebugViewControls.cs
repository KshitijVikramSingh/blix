using Blix.Diagnostics;

namespace Blix.Demos.VulkanLit.Debug;

// "What am I looking at" — the present-pass view selector (Final / shadow
// maps / depth) and the per-pixel shader-channel selector (albedo / world
// normal / roughness / etc.). Two independent enums that the lit + present
// shaders branch on.
internal sealed class DebugViewControls : IDebuggable
{
    private readonly LitLoop loop;

    public DebugViewControls(LitLoop loop)
    {
        this.loop = loop;
    }

    public string DebugName => "view";

    public void Debug(DebugContext debug)
    {
        loop.ViewMode = debug.Controls.Enum("View [V]", loop.ViewMode, LitLoop.ViewModeLabels);
        loop.ShaderDebugMode = debug.Controls.Enum("Shader channel", loop.ShaderDebugMode, LitLoop.ShaderChannelOptions);
    }
}
