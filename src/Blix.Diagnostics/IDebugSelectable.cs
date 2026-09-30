namespace Blix.Diagnostics;

// Producer-side hook for "I have pickable entities."
//
// Destination-pattern: the runtime hands the source a pre-allocated
// List<DebugSelectable> to append into, avoiding the per-frame
// IEnumerable allocation a yield-return collector would incur. A
// producer with one entity appends one item; a producer with N
// entities (GltfSceneInstance and its submeshes) appends N.
//
// Picking flow: the engine picks. With the overlay up and pick mode armed (the
// Selection tab, or Alt held), a click becomes a ray through the view it
// landed in (Blix.ViewPicking.RayThrough), every source's bounds are tested,
// and the nearest box in front of the eye is selected (DebugSystem.PickAlong).
// This used to be the application's job: Sponza wrote the ray, the raycast,
// the multi-select and the secondary highlights itself, around a runtime that
// held one path and a copy of its bounds.
//
// What stays with the producer is WHAT IS THERE: which entities exist, how they
// are stored, what their bounds mean, and what selecting one does. The engine
// supplies the ray, the rule and the set; the world is the application's. Tool
// and gameplay picking (a viewer's joints, a tower-defence grid cell) are not
// this: they are the application's own, and use ViewPicking directly.
public interface IDebugSelectable : IDebugContributor
{
    void CollectSelectables(List<DebugSelectable> destination);

    /// <summary>The CURRENT bounds of one entity this source owns, for the selection highlight.</summary>
    /// <remarks>
    /// Asked every frame for each selected path, which is what keeps a highlight on a thing that moves:
    /// the runtime used to copy the bounds at the click and draw that copy for as long as the selection
    /// lasted. The default walks <see cref="CollectSelectables"/>; a source with many entities and an
    /// index should answer from it.
    /// </remarks>
    bool TryGetBounds(string entityPath, out Blix.Geometry.Bounds3 bounds)
    {
        var all = new List<DebugSelectable>();
        CollectSelectables(all);
        foreach (var item in all)
        {
            if (item.EntityPath != entityPath) continue;
            bounds = item.Bounds;
            return true;
        }

        bounds = default;
        return false;
    }
}
