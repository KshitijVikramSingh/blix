using Blix.Diagnostics;

namespace Blix.Diagnostics.Overlay;

// Producer-side hook for custom ImGui panels. Lives in Blix.Diagnostics.Overlay
// (not the UI-agnostic Blix.Diagnostics core) because opting in to a custom
// panel means taking a hard reference to ImGuiNET, which this project carries.
// The runtime renders these panels through the shared DebugOverlayUi, so a
// producer writes OnImGui once and the overlay draws it.
//
// Discovery is via the contributor registry: anything registered on
// DebugSystem that also implements IDebugUi gets OnImGui invoked once per
// frame by DebugOverlayUi, after the default channel panels.
//
// The renderer wraps the call in an ImGui CollapsingHeader keyed on DebugName
// so the panel layout is consistent across producers; the producer is
// responsible only for the panel BODY (widgets, plots, etc).
//
// OnImGui is called every frame, including when the diagnostics view is
// frozen. The producer owns its own state and decides how to react to freeze
// (typically: keep interacting; the producer's settings affect the NEXT frame
// regardless of freeze, because freeze only affects what channel data is
// *displayed*, not what producers compute).
public interface IDebugUi : IDebugContributor
{
    void OnImGui();
}
