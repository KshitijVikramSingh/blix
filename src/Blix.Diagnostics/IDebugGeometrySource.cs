namespace Blix.Diagnostics;

// Producer-side hook for "I emit spatial debug geometry."
//
// Separate from IDebuggable because emission is conditional on the
// producer's path being visible (DebugState.IsPathVisible(DebugName)).
// When the user disables "scene" in the Layers panel, every registered
// IDebugGeometrySource whose DebugName starts with "scene" is skipped
// entirely — so a producer that would emit thousands of debug primitives
// pays zero CPU cost when its category is off.
//
// Auto-scope: the registry pushes Current.Scope(DebugName) around the
// EmitGeometry call, so emissions like Draw.Aabb("submesh-3/bounds")
// land at path "<DebugName>/submesh-3/bounds" without the producer
// managing scope manually.
//
// A producer may implement IDebuggable, IDebugGeometrySource, and
// IDebugUi independently; the same registry holds it, and each
// surface is invoked at its appropriate point in the frame.
public interface IDebugGeometrySource : IDebugContributor
{
    void EmitGeometry(DebugContext debug);
}
