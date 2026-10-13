using Blix.Diagnostics;

namespace Blix.Diagnostics.Overlay;

// A producer's own section at the top of the overlay's Logs tab: what a reader of the run's log wants beside it --
// a long job's progress and time left, the process's memory -- drawn by the producer (ImGui widgets), above the
// log, under a header of its DebugName. As IDebugUi's panels, discovered through the contributor registry, called
// every frame the tab is open, and isolated: a section that throws shows its error in place of itself.
public interface IDebugLogsSection : IDebugContributor
{
    void OnLogsSection();
}
