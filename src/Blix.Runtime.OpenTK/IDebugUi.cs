using Blix.Diagnostics;

namespace Blix.Runtime.OpenTK;

// Producer-side hook for custom ImGui panels. The diagnostics core
// (Blix.Diagnostics) is UI-library-agnostic, so this interface lives
// where ImGui already does: any class that opts in to a custom panel
// inherits a hard reference to ImGuiNET via this namespace.
//
// Discovery is via the contributor registry: anything registered on
// DebugSystem that also implements IDebugUi gets OnImGui invoked once
// per frame by ImGuiOverlayRenderer, after the default channel panels.
//
// The renderer wraps the call in an ImGui CollapsingHeader keyed on
// DebugName so the panel layout is consistent across producers; the
// producer is responsible only for the panel BODY (widgets, plots, etc).
//
// OnImGui is called every frame, including when the diagnostics view is
// frozen. The producer owns its own state and decides how to react to
// freeze (typically: keep interacting; the producer's settings affect
// the NEXT frame regardless of freeze, because freeze only affects what
// channel data is *displayed*, not what producers compute).
public interface IDebugUi : IDebugContributor
{
    void OnImGui();
}
