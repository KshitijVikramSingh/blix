using Blix.Geometry;

namespace Blix;

// 2D analogue of CollisionWorld3D. Same structural shape: owner-tagged per-shape
// lists, layer-masked Raycast + Overlap queries, no shared collider abstraction.
// The two worlds intentionally stay parallel rather than unified — keeping them as
// independent classes lets each evolve at its own pace (the 2D side may end up
// with a grid broadphase, the 3D side a BVH) and keeps every method call type-
// stable at the inner loop.
//
// Plane is omitted from the 2D shape list — half-spaces rarely earn their keep in
// 2D vs. just listing the wall segments. LineMesh2D + OBB2 cover the same content.
public sealed class CollisionWorld2D<T> where T : notnull
{
    private readonly List<(T Owner, Bounds2 Bounds, CollisionLayer Layer)> aabbs = new();
    private readonly List<(T Owner, Circle Circle, CollisionLayer Layer)> circles = new();
    private readonly List<(T Owner, Capsule2D Capsule, CollisionLayer Layer)> capsules = new();
    private readonly List<(T Owner, OrientedBounds2 Obb, CollisionLayer Layer)> obbs = new();
    private readonly List<(T Owner, LineMesh2D Mesh, CollisionLayer Layer)> meshes = new();

    // -- Add ----------------------------------------------------------------------

    public void Add(T owner, Bounds2 bounds) => Add(owner, bounds, CollisionLayer.Default);
    public void Add(T owner, Bounds2 bounds, CollisionLayer layer) => aabbs.Add((owner, bounds, layer));

    public void Add(T owner, Circle circle) => Add(owner, circle, CollisionLayer.Default);
    public void Add(T owner, Circle circle, CollisionLayer layer) => circles.Add((owner, circle, layer));

    public void Add(T owner, Capsule2D capsule) => Add(owner, capsule, CollisionLayer.Default);
    public void Add(T owner, Capsule2D capsule, CollisionLayer layer) => capsules.Add((owner, capsule, layer));

    public void Add(T owner, OrientedBounds2 obb) => Add(owner, obb, CollisionLayer.Default);
    public void Add(T owner, OrientedBounds2 obb, CollisionLayer layer) => obbs.Add((owner, obb, layer));

    public void Add(T owner, LineMesh2D mesh) => Add(owner, mesh, CollisionLayer.Default);
    public void Add(T owner, LineMesh2D mesh, CollisionLayer layer)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        meshes.Add((owner, mesh, layer));
    }

    public void Remove(T owner)
    {
        aabbs.RemoveAll(e => EqualityComparer<T>.Default.Equals(e.Owner, owner));
        circles.RemoveAll(e => EqualityComparer<T>.Default.Equals(e.Owner, owner));
        capsules.RemoveAll(e => EqualityComparer<T>.Default.Equals(e.Owner, owner));
        obbs.RemoveAll(e => EqualityComparer<T>.Default.Equals(e.Owner, owner));
        meshes.RemoveAll(e => EqualityComparer<T>.Default.Equals(e.Owner, owner));
    }

    public void Clear()
    {
        aabbs.Clear();
        circles.Clear();
        capsules.Clear();
        obbs.Clear();
        meshes.Clear();
    }

    // -- Raycast ------------------------------------------------------------------

    public CollisionContact2D<T>? Raycast(Ray2D ray, float maxDistance = float.PositiveInfinity) =>
        Raycast(ray, CollisionMask.All, maxDistance);

    public CollisionContact2D<T>? Raycast(Ray2D ray, CollisionMask mask, float maxDistance = float.PositiveInfinity)
    {
        CollisionContact2D<T>? closest = null;
        var closestT = maxDistance;

        foreach (var (owner, aabb, layer) in aabbs)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection2D.Raycast(ray, aabb, closestT) is { } hit && hit.Time <= closestT)
            {
                closestT = hit.Time;
                closest = new CollisionContact2D<T>(owner, layer, hit);
            }
        }
        foreach (var (owner, circle, layer) in circles)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection2D.Raycast(ray, circle, closestT) is { } hit && hit.Time <= closestT)
            {
                closestT = hit.Time;
                closest = new CollisionContact2D<T>(owner, layer, hit);
            }
        }
        foreach (var (owner, capsule, layer) in capsules)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection2D.Raycast(ray, capsule, closestT) is { } hit && hit.Time <= closestT)
            {
                closestT = hit.Time;
                closest = new CollisionContact2D<T>(owner, layer, hit);
            }
        }
        foreach (var (owner, obb, layer) in obbs)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection2D.Raycast(ray, obb, closestT) is { } hit && hit.Time <= closestT)
            {
                closestT = hit.Time;
                closest = new CollisionContact2D<T>(owner, layer, hit);
            }
        }
        foreach (var (owner, mesh, layer) in meshes)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection2D.Raycast(ray, mesh, closestT) is { } hit && hit.Time <= closestT)
            {
                closestT = hit.Time;
                closest = new CollisionContact2D<T>(owner, layer, hit);
            }
        }
        return closest;
    }

    // -- Overlap ------------------------------------------------------------------

    public void Overlap(Bounds2 query, List<CollisionContact2D<T>> results) =>
        Overlap(query, CollisionMask.All, results);

    public void Overlap(Bounds2 query, CollisionMask mask, List<CollisionContact2D<T>> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        results.Clear();
        foreach (var (owner, aabb, layer) in aabbs)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection2D.Test(query, aabb) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, circle, layer) in circles)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection2D.Test(circle, query) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, capsule, layer) in capsules)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection2D.Test(capsule, query) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, obb, layer) in obbs)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection2D.Test(obb, query) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, mesh, layer) in meshes)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection2D.Test(query, mesh) is { } hit) results.Add(new(owner, layer, hit));
        }
    }

    public void Overlap(Circle query, List<CollisionContact2D<T>> results) =>
        Overlap(query, CollisionMask.All, results);

    public void Overlap(Circle query, CollisionMask mask, List<CollisionContact2D<T>> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        results.Clear();
        foreach (var (owner, aabb, layer) in aabbs)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection2D.Test(query, aabb) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, circle, layer) in circles)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection2D.Test(query, circle) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, capsule, layer) in capsules)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection2D.Test(query, capsule) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, obb, layer) in obbs)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection2D.Test(query, obb) is { } hit) results.Add(new(owner, layer, hit));
        }
        foreach (var (owner, mesh, layer) in meshes)
        {
            if (!mask.Matches(layer)) continue;
            if (Intersection2D.Test(query, mesh) is { } hit) results.Add(new(owner, layer, hit));
        }
    }
}
