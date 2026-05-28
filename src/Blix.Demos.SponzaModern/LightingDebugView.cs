using Blix.Diagnostics;
using Blix.Diagnostics.Overlay;
using ImGuiNET;

// LightingDebugView lives in the default namespace to match Program.cs,
// which uses top-level statements with no namespace declaration. The
// alternative — giving it Blix.Demos.SponzaModern and adding a using —
// would force a one-line import the demo doesn't otherwise need.

// Sponza's "Debug" scope distilled into a single registered contributor.
//
// Owns:
//   - SelectedViewIndex   — which uDebugView the lit shader writes
//   - VisualizeCascades   — toggles cascade-color overlay in the shadow path
//
// Implements both:
//   - IDebuggable: emits the two as Values so they show up in the State
//     panel (and so a JSON dump captures what the user had selected).
//   - IDebugUi:    renders a custom ImGui panel with a Combo + Checkbox.
//
// The custom panel is functionally equivalent to the Controls.Enum +
// Controls.Toggle pair it replaces, but it's a self-contained class
// instead of a dozen lines living inside the game's monolithic Debug()
// body — which is what Phase 5 is about. It's also a worked example for
// future producers (PbrSceneRenderer, asset systems) of how to ship a
// dedicated panel.
public sealed class LightingDebugView : IDebuggable, IDebugUi
{
    private readonly string[] viewNames;
    private int selectedViewIndex;
    private bool visualizeCascades;

    public LightingDebugView(string[] viewNames)
    {
        ArgumentNullException.ThrowIfNull(viewNames);
        if (viewNames.Length == 0)
        {
            throw new ArgumentException("At least one view name is required.", nameof(viewNames));
        }
        this.viewNames = viewNames;
    }

    public string DebugName => "lighting-debug";

    public int SelectedViewIndex => selectedViewIndex;

    public bool VisualizeCascades => visualizeCascades;

    public void Debug(DebugContext debug)
    {
        // Surface the current selection as Values so the State panel and
        // any frame dump records what the user had picked. The custom UI
        // mutates these fields directly; this is just read-out.
        debug.Values.Value("view", viewNames[selectedViewIndex]);
        debug.Values.Value("cascade-colors", visualizeCascades);
    }

    public void OnImGui()
    {
        ImGui.Combo("View", ref selectedViewIndex, viewNames, viewNames.Length);
        ImGui.Checkbox("Visualize cascades", ref visualizeCascades);
    }
}
