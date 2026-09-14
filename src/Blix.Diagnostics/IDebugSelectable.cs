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
// The runtime does not pick FOR the producer, but the reason has changed and the
// old one should not be left standing: this used to read "it doesn't know about
// cameras or screen space", which was true until views became first-class. A
// ViewDeclaration is exactly a camera and a screen rectangle with a name on it, and
// Blix.ViewPicking.RayThrough will turn a pointer into a ray through any of them —
// including one drawn into a panel or to an off-screen texture, which is precisely
// what Camera3D.ScreenPointToRay cannot express.
//
// What stays with the producer is WHAT IS THERE: which entities exist, how they are
// stored, what their bounds mean, and what selecting one does. The engine supplies
// the ray; the world is the application's.
public interface IDebugSelectable : IDebugContributor
{
    void CollectSelectables(List<DebugSelectable> destination);
}
