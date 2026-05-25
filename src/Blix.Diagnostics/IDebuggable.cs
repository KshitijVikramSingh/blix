namespace Blix.Diagnostics;

// Pull-style producer of per-frame diagnostics. DebugSystem.Run invokes
// Debug() once per registered (or explicitly passed) contributor inside
// an auto-pushed scope of DebugName, so everything the producer emits is
// path-attributed to it without ceremony.
//
// DebugName is inherited from IDebugContributor so a class that also
// implements IDebugUi (runtime-side) gets a single identity for both
// the data and the panel.
public interface IDebuggable : IDebugContributor
{
    void Debug(DebugContext debug);
}
