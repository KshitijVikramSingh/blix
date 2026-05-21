using Blix.Geometry;

namespace Blix;

// Owner-tagged container for static colliders. Game code populates the world with
// (owner, shape, layer) entries and queries it for raycasts or overlap regions.
// Centralises the iteration so call sites stop hand-writing pairwise loops against
// a hardcoded floor + cube + whatever.
//
// Storage is per-shape typed lists — no shared Collider type, no broadphase. When n²
// becomes a problem, swap the internals (BVH / grid / sweep-and-prune) without
// changing the public API. The CollisionContact3D<T> return type is shape-free, so
// callers don't need to know what shape the owner registered.
//
// Layers / masks filter what queries see. Each collider carries a CollisionLayer
// (defaulting to Default = bit 0); each query takes an optional CollisionMask
// (defaulting to All). A query matches a collider when `mask.Matches(entry.Layer)`.
//
// `T` is whatever tag a game uses to identify the owner — typically a GameObject
// reference, but the world stays generic so it works for any tag.
public sealed class CollisionWorld3D<T> where T : notnull
{
    // One list per shape variant. Per-shape storage keeps the inner loops type-stable
    // (no per-entry virtual dispatch) and lets each query trivially skip variants
    // that can't possibly intersect.
    private readonly List<(T Owner, Bounds3 Bounds, CollisionLayer Layer)> aabbs = new();
    private readonly List<(T Owner, BoundingSphere Sphere, CollisionLayer Layer)> spheres = new();
    private readonly List<(T Owner, Plane Plane, CollisionLayer Layer)> planes = new();
    private readonly List<(T Owner, TriangleMesh3D Mesh, CollisionLayer Layer)> triangleMeshes = new();
    private readonly List<(T Owner, Capsule Capsule, CollisionLayer Layer)> capsules = new();
    private readonly List<(T Owner, OrientedBounds3 Obb, CollisionLayer Layer)> obbs = new();

    // -- Add ----------------------------------------------------------------------
    // Per-shape overloads rather than a polymorphic Collider type. When the engine
    // gains a Collider abstraction (if it ever does), these can call into it
    // internally without breaking callers.
    //
    // The no-layer overloads use CollisionLayer.Default (bit 0) so existing call
    // sites that predate layers keep behaving as before.

    public void Add(T owner, Bounds3 bounds) => Add(owner, bounds, CollisionLayer.Default);

    public void Add(T owner, Bounds3 bounds, CollisionLayer layer) =>
        aabbs.Add((owner, bounds, layer));

    public void Add(T owner, BoundingSphere sphere) => Add(owner, sphere, CollisionLayer.Default);

    public void Add(T owner, BoundingSphere sphere, CollisionLayer layer) =>
        spheres.Add((owner, sphere, layer));

    public void Add(T owner, Plane plane) => Add(owner, plane, CollisionLayer.Default);

    public void Add(T owner, Plane plane, CollisionLayer layer) =>
        planes.Add((owner, plane, layer));

    public void Add(T owner, TriangleMesh3D mesh) => Add(owner, mesh, CollisionLayer.Default);

    public void Add(T owner, TriangleMesh3D mesh, CollisionLayer layer)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        triangleMeshes.Add((owner, mesh, layer));
    }

    public void Add(T owner, Capsule capsule) => Add(owner, capsule, CollisionLayer.Default);

    public void Add(T owner, Capsule capsule, CollisionLayer layer) =>
        capsules.Add((owner, capsule, layer));

    public void Add(T owner, OrientedBounds3 obb) => Add(owner, obb, CollisionLayer.Default);

    public void Add(T owner, OrientedBounds3 obb, CollisionLayer layer) =>
        obbs.Add((owner, obb, layer));

    // Remove every collider registered for this owner across all shapes. Use when an
    // object is being destroyed or its collision shape needs replacing — call Remove
    // then Add again with the new shape.
    public void Remove(T owner)
    {
        aabbs.RemoveAll(e => EqualityComparer<T>.Default.Equals(e.Owner, owner));
        spheres.RemoveAll(e => EqualityComparer<T>.Default.Equals(e.Owner, owner));
        planes.RemoveAll(e => EqualityComparer<T>.Default.Equals(e.Owner, owner));
        triangleMeshes.RemoveAll(e => EqualityComparer<T>.Default.Equals(e.Owner, owner));
        capsules.RemoveAll(e => EqualityComparer<T>.Default.Equals(e.Owner, owner));
        obbs.RemoveAll(e => EqualityComparer<T>.Default.Equals(e.Owner, owner));
    }

    public void Clear()
    {
        aabbs.Clear();
        spheres.Clear();
        planes.Clear();
        triangleMeshes.Clear();
        capsules.Clear();
        obbs.Clear();
    }

    // -- Raycast ------------------------------------------------------------------
    // Returns the closest hit within [0, maxDistance], or null if the ray hits nothing.
    // Ties (two entries at the exact same Time) resolve to whichever was registered
    // first — not a meaningful guarantee for tied surfaces, but stable for testing.

    public CollisionContact3D<T>? Raycast(Ray ray, float maxDistance = float.PositiveInfinity) =>
        Raycast(ray, CollisionMask.All, maxDistance);

    public CollisionContact3D<T>? Raycast(Ray ray, CollisionMask mask, float maxDistance = float.PositiveInfinity)
    {
        CollisionContact3D<T>? closest = null;
        var closestT = maxDistance;

        foreach (var (owner, aabb, layer) in aabbs)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Raycast(ray, aabb, closestT) is { } hit && hit.Time <= closestT)
            {
                closestT = hit.Time;
                closest = new CollisionContact3D<T>(owner, layer, hit);
            }
        }
        foreach (var (owner, sphere, layer) in spheres)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Raycast(ray, sphere, closestT) is { } hit && hit.Time <= closestT)
            {
                closestT = hit.Time;
                closest = new CollisionContact3D<T>(owner, layer, hit);
            }
        }
        foreach (var (owner, plane, layer) in planes)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Raycast(ray, plane, closestT) is { } hit && hit.Time <= closestT)
            {
                closestT = hit.Time;
                closest = new CollisionContact3D<T>(owner, layer, hit);
            }
        }
        foreach (var (owner, mesh, layer) in triangleMeshes)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Raycast(ray, mesh, closestT) is { } hit && hit.Time <= closestT)
            {
                closestT = hit.Time;
                closest = new CollisionContact3D<T>(owner, layer, hit);
            }
        }
        foreach (var (owner, capsule, layer) in capsules)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Raycast(ray, capsule, closestT) is { } hit && hit.Time <= closestT)
            {
                closestT = hit.Time;
                closest = new CollisionContact3D<T>(owner, layer, hit);
            }
        }
        foreach (var (owner, obb, layer) in obbs)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Raycast(ray, obb, closestT) is { } hit && hit.Time <= closestT)
            {
                closestT = hit.Time;
                closest = new CollisionContact3D<T>(owner, layer, hit);
            }
        }
        return closest;
    }

    // -- Overlap ------------------------------------------------------------------
    // Tests a query shape against every collider in the world; appends every hit to
    // `results`. The caller owns the list so repeated queries can reuse a single
    // buffer with zero allocations per call. Clears the list at the top.

    public void Overlap(Bounds3 query, List<CollisionContact3D<T>> results) =>
        Overlap(query, CollisionMask.All, results);

    public void Overlap(Bounds3 query, CollisionMask mask, List<CollisionContact3D<T>> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        results.Clear();
        foreach (var (owner, aabb, layer) in aabbs)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Test(query, aabb) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, sphere, layer) in spheres)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Test(sphere, query) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, plane, layer) in planes)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Test(query, plane) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, mesh, layer) in triangleMeshes)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Test(query, mesh) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, capsule, layer) in capsules)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Test(capsule, query) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, obb, layer) in obbs)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Test(obb, query) is { } hit) results.Add(new(owner, layer, hit));
        }
    }

    public void Overlap(BoundingSphere query, List<CollisionContact3D<T>> results) =>
        Overlap(query, CollisionMask.All, results);

    public void Overlap(BoundingSphere query, CollisionMask mask, List<CollisionContact3D<T>> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        results.Clear();
        foreach (var (owner, aabb, layer) in aabbs)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Test(query, aabb) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, sphere, layer) in spheres)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Test(query, sphere) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, plane, layer) in planes)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Test(query, plane) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, mesh, layer) in triangleMeshes)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Test(query, mesh) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, capsule, layer) in capsules)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Test(query, capsule) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, obb, layer) in obbs)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection.Test(query, obb) is { } hit) results.Add(new(owner, layer, hit));
        }
    }
}
