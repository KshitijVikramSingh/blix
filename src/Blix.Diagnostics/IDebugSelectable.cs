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
// Pick switch in the status bar, or Alt held), a click asks what is under the
// cursor, and the host answers by drawing every selectable's geometry into the
// one pixel under it with an ID per selectable, then reading that pixel back.
// The answer is exact: no box and no ray is involved. Clicking again at the same
// spot leaves the hit out and reaches what is behind it.
//
// This used to be the application's job (Sponza wrote the ray, the raycast, the
// multi-select and the highlights), and then a ray against these bounds, which
// picked the air under a vault: a box says where a thing might be, not where its
// surface is. Bounds remain, for drawing the highlight only.
//
// What stays with the producer is WHAT IS THERE: which entities exist, their
// geometry, and what selecting one does. Tool and gameplay picking (a viewer's
// joints, a tower-defence grid cell) are not this: they are the application's
// own, and use ViewPicking directly.
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
