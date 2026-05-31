namespace Blix.Diagnostics;

// Base marker for "something the diagnostics system knows about." Carries
// only the identity needed to scope output and group UI rows.
//
// Two derived interfaces specialize the contract:
//
//   IDebuggable  — implements Debug(DebugContext); receives a pull call
//                  once per frame from DebugSystem.Run, runs inside an
//                  auto-pushed scope of its DebugName.
//
//   IDebugUi     — lives in the overlay layer (Blix.Diagnostics.Overlay) and
//                  receives OnImGui(); lets producers ship a custom panel
//                  that the ImGui sink renders alongside the default
//                  Stats/Events/Controls panels.
//
// A single producer class may implement both (e.g. LightingDebugView):
// the registry holds it once, the diagnostics core invokes Debug, the
// runtime invokes OnImGui.
public interface IDebugContributor
{
    string DebugName { get; }
}
