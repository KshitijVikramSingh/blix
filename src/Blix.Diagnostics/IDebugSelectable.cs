namespace Blix.Diagnostics;

// Producer-side hook for "I have pickable entities."
//
// Destination-pattern: the runtime hands the source a pre-allocated
// List<DebugSelectable> to append into, avoiding the per-frame
// IEnumerable allocation a yield-return collector would incur. A
// producer with one entity appends one item; a producer with N
// entities (GltfSceneInstance and its submeshes) appends N.
//
// Picking flow:
//   demo calls debugSystem.CollectSelectables() -> List<DebugSelectable>
//   demo raycasts against the bounds, picks the closest
//   demo calls debugSystem.Select(path, bounds)
//
// The runtime never picks for the demo — it doesn't know about cameras
// or screen space. Picking math lives where the camera lives.
public interface IDebugSelectable : IDebugContributor
{
    void CollectSelectables(List<DebugSelectable> destination);
}
