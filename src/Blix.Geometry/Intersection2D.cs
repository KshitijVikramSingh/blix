using System.Numerics;

namespace Blix.Geometry;

// 2D analogue of Intersection. Discrete intersection tests return null when shapes
// are disjoint and a CollisionHit2D (Time = 0) when they overlap. Normal points
// from B into A. Sweep tests are not implemented for v0 — they'll land when the
// first 2D consumer needs CCD, and the signature shape will match Intersection's
// (motion vector + Time field on the hit).
//
// Pair coverage (Test):
//   Bounds2 x Bounds2 / Circle / Capsule2D / OrientedBounds2 / LineMesh2D
//   Circle  x Circle / Capsule2D / OrientedBounds2 / LineMesh2D
//   Capsule2D x Capsule2D / OrientedBounds2 / LineMesh2D
//   OrientedBounds2 x OrientedBounds2 / LineMesh2D
//
// Raycast coverage: Ray2D x each of the 5 primitives.
//
// Asymmetric pairs (e.g. Circle x Bounds2 vs Bounds2 x Circle) are exposed in both
// orders via the canonical-pair helpers. The convention is "query-first": pass
// the moving / smaller shape first, the static / larger shape second.
public static class Intersection2D
{
    // ---------------------------------------------------------------------------
    //  Bounds2-vs-Bounds2 — axis-aligned overlap. Trivial reject + overlap-axis pick.
    // ---------------------------------------------------------------------------

    public static CollisionHit2D? Test(Bounds2 a, Bounds2 b)
    {
        var minOverlap = Vector2.Min(a.Max, b.Max) - Vector2.Max(a.Min, b.Min);
        if (minOverlap.X <= 0.0f || minOverlap.Y <= 0.0f)
        {
            return null;
        }

        // Pick the minimum-translation-vector axis: the axis with the smallest
        // overlap is the cheapest to depenetrate along.
        Vector2 normal;
        float depth;
        if (minOverlap.X < minOverlap.Y)
        {
            normal = new Vector2(a.Center.X < b.Center.X ? -1.0f : 1.0f, 0.0f);
            depth = minOverlap.X;
        }
        else
        {
            normal = new Vector2(0.0f, a.Center.Y < b.Center.Y ? -1.0f : 1.0f);
            depth = minOverlap.Y;
        }

        var point = Vector2.Clamp(a.Center, b.Min, b.Max);
        return new CollisionHit2D { Time = 0.0f, Point = point, Normal = normal, Depth = depth };
    }

    // ---------------------------------------------------------------------------
    //  Circle-vs-Circle — distance check.
    // ---------------------------------------------------------------------------

    public static CollisionHit2D? Test(Circle a, Circle b)
    {
        var delta = a.Center - b.Center;
        var distanceSquared = delta.LengthSquared();
        var radiusSum = a.Radius + b.Radius;
        if (distanceSquared >= radiusSum * radiusSum)
        {
            return null;
        }
        var distance = MathF.Sqrt(distanceSquared);
        // Degenerate: concentric circles. Pick an arbitrary axis (+X) so the contact
        // is still well-defined; depth is the full sum so the caller can separate.
        var normal = distance > 0.0f ? delta / distance : Vector2.UnitX;
        var depth = radiusSum - distance;
        var point = b.Center + normal * b.Radius;
        return new CollisionHit2D { Time = 0.0f, Point = point, Normal = normal, Depth = depth };
    }

    // ---------------------------------------------------------------------------
    //  Circle-vs-Bounds2 — closest-point-on-AABB then circle test.
    // ---------------------------------------------------------------------------

    public static CollisionHit2D? Test(Circle circle, Bounds2 aabb)
    {
        var closest = Vector2.Clamp(circle.Center, aabb.Min, aabb.Max);
        var delta = circle.Center - closest;
        var distanceSquared = delta.LengthSquared();
        if (distanceSquared >= circle.Radius * circle.Radius)
        {
            return null;
        }

        // Inside the AABB: the closest-point IS the centre. Resolve by pushing along
        // the AABB axis with minimum penetration depth.
        if (distanceSquared == 0.0f)
        {
            var halfSize = aabb.Size * 0.5f;
            var local = circle.Center - aabb.Center;
            var px = halfSize.X - MathF.Abs(local.X);
            var py = halfSize.Y - MathF.Abs(local.Y);
            Vector2 normal;
            float depth;
            if (px < py)
            {
                normal = new Vector2(local.X >= 0.0f ? 1.0f : -1.0f, 0.0f);
                depth = px + circle.Radius;
            }
            else
            {
                normal = new Vector2(0.0f, local.Y >= 0.0f ? 1.0f : -1.0f);
                depth = py + circle.Radius;
            }
            return new CollisionHit2D { Time = 0.0f, Point = circle.Center, Normal = normal, Depth = depth };
        }

        var distance = MathF.Sqrt(distanceSquared);
        var n = delta / distance;
        return new CollisionHit2D
        {
            Time = 0.0f,
            Point = closest,
            Normal = n,
            Depth = circle.Radius - distance,
        };
    }

    public static CollisionHit2D? Test(Bounds2 aabb, Circle circle)
    {
        if (Test(circle, aabb) is not { } hit) return null;
        // Flip normal: caller wants normal-from-B-into-A in their argument order.
        return hit with { Normal = -hit.Normal };
    }

    // ---------------------------------------------------------------------------
    //  Capsule-vs-Capsule — closest points on the two segments, then sphere test.
    // ---------------------------------------------------------------------------

    public static CollisionHit2D? Test(Capsule2D a, Capsule2D b)
    {
        var (pA, pB) = ClosestPointsOnSegments(a.PointA, a.PointB, b.PointA, b.PointB);
        var delta = pA - pB;
        var distanceSquared = delta.LengthSquared();
        var radiusSum = a.Radius + b.Radius;
        if (distanceSquared >= radiusSum * radiusSum)
        {
            return null;
        }
        var distance = MathF.Sqrt(distanceSquared);
        var normal = distance > 0.0f ? delta / distance : Vector2.UnitX;
        return new CollisionHit2D
        {
            Time = 0.0f,
            Point = pB + normal * b.Radius,
            Normal = normal,
            Depth = radiusSum - distance,
        };
    }

    public static CollisionHit2D? Test(Circle sphere, Capsule2D capsule)
    {
        var closest = ClosestPointOnSegment(sphere.Center, capsule.PointA, capsule.PointB);
        var delta = sphere.Center - closest;
        var distanceSquared = delta.LengthSquared();
        var radiusSum = sphere.Radius + capsule.Radius;
        if (distanceSquared >= radiusSum * radiusSum)
        {
            return null;
        }
        var distance = MathF.Sqrt(distanceSquared);
        var normal = distance > 0.0f ? delta / distance : Vector2.UnitX;
        return new CollisionHit2D
        {
            Time = 0.0f,
            Point = closest + normal * capsule.Radius,
            Normal = normal,
            Depth = radiusSum - distance,
        };
    }

    public static CollisionHit2D? Test(Capsule2D capsule, Bounds2 aabb)
    {
        // Approximate (same trade as 3D Capsule-vs-AABB): closest point on segment to
        // AABB centre, then circle-vs-AABB at that point with the capsule radius.
        // Exact for the common "character outside the wall" case; conservative when
        // the segment passes through at a steep angle.
        var closest = ClosestPointOnSegment(aabb.Center, capsule.PointA, capsule.PointB);
        var asCircle = new Circle(closest, capsule.Radius);
        return Test(asCircle, aabb);
    }

    // ---------------------------------------------------------------------------
    //  OBB pairs — transform query into OBB-local frame then run the AABB version.
    //  OBB-vs-OBB uses the SAT-with-4-axes pattern (each box contributes 2 unique
    //  axes; the other pair is colinear).
    // ---------------------------------------------------------------------------

    public static CollisionHit2D? Test(Circle circle, OrientedBounds2 obb)
    {
        var local = ToLocal(circle.Center, obb);
        var localAabb = new Bounds2(-obb.HalfExtents, obb.HalfExtents);
        if (Test(new Circle(local, circle.Radius), localAabb) is not { } localHit)
        {
            return null;
        }
        // Rotate normal back to world space (no translation — normals are direction-only).
        var worldNormal = ToWorldDirection(localHit.Normal, obb);
        var worldPoint = ToWorldPoint(localHit.Point, obb);
        return localHit with { Normal = worldNormal, Point = worldPoint };
    }

    public static CollisionHit2D? Test(OrientedBounds2 obb, Bounds2 aabb)
    {
        // OBB-vs-AABB via SAT. Axes: OBB's 2 local axes + AABB's 2 world axes.
        // 4 axes total but 2 may coincide if rotation is 0/90/180/270.
        Span<Vector2> axes = stackalloc Vector2[4];
        axes[0] = obb.AxisX;
        axes[1] = obb.AxisY;
        axes[2] = new Vector2(1.0f, 0.0f);
        axes[3] = new Vector2(0.0f, 1.0f);

        Span<Vector2> obbCorners = stackalloc Vector2[4];
        FillCorners(obb, obbCorners);
        Span<Vector2> aabbCorners = stackalloc Vector2[4];
        aabbCorners[0] = new Vector2(aabb.Min.X, aabb.Min.Y);
        aabbCorners[1] = new Vector2(aabb.Max.X, aabb.Min.Y);
        aabbCorners[2] = new Vector2(aabb.Max.X, aabb.Max.Y);
        aabbCorners[3] = new Vector2(aabb.Min.X, aabb.Max.Y);

        return SatTest(obbCorners, aabbCorners, axes, obb.Center, aabb.Center);
    }

    public static CollisionHit2D? Test(OrientedBounds2 a, OrientedBounds2 b)
    {
        Span<Vector2> axes = stackalloc Vector2[4];
        axes[0] = a.AxisX;
        axes[1] = a.AxisY;
        axes[2] = b.AxisX;
        axes[3] = b.AxisY;

        Span<Vector2> ac = stackalloc Vector2[4];
        FillCorners(a, ac);
        Span<Vector2> bc = stackalloc Vector2[4];
        FillCorners(b, bc);

        return SatTest(ac, bc, axes, a.Center, b.Center);
    }

    public static CollisionHit2D? Test(Capsule2D capsule, OrientedBounds2 obb)
    {
        // Move the capsule into OBB-local, then capsule-vs-AABB at the origin.
        var localA = ToLocal(capsule.PointA, obb);
        var localB = ToLocal(capsule.PointB, obb);
        var localAabb = new Bounds2(-obb.HalfExtents, obb.HalfExtents);
        if (Test(new Capsule2D(localA, localB, capsule.Radius), localAabb) is not { } localHit)
        {
            return null;
        }
        return localHit with
        {
            Normal = ToWorldDirection(localHit.Normal, obb),
            Point = ToWorldPoint(localHit.Point, obb),
        };
    }

    // ---------------------------------------------------------------------------
    //  LineMesh2D pairs — iterate segments. Each segment is treated as a degenerate
    //  capsule of radius 0; the contact is then "circle/capsule/etc. vs zero-radius
    //  capsule" which reuses the closest-point math.
    // ---------------------------------------------------------------------------

    public static CollisionHit2D? Test(Circle circle, LineMesh2D mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        // Broadphase: AABB-vs-AABB cull. Bound the circle by its enclosing AABB so the
        // mesh's coarse Bounds quickly rejects non-overlapping queries.
        var circleAabb = new Bounds2(circle.Center - new Vector2(circle.Radius), circle.Center + new Vector2(circle.Radius));
        if (Test(circleAabb, mesh.Bounds) is null)
        {
            return null;
        }

        CollisionHit2D? best = null;
        for (var i = 0; i < mesh.Segments.Length; i++)
        {
            var seg = mesh.Segments[i];
            var closest = ClosestPointOnSegment(circle.Center, seg.PointA, seg.PointB);
            var delta = circle.Center - closest;
            var distanceSquared = delta.LengthSquared();
            if (distanceSquared >= circle.Radius * circle.Radius)
            {
                continue;
            }
            var distance = MathF.Sqrt(distanceSquared);
            var normal = distance > 0.0f ? delta / distance : Vector2.UnitX;
            var hit = new CollisionHit2D
            {
                Time = 0.0f,
                Point = closest,
                Normal = normal,
                Depth = circle.Radius - distance,
            };
            // Keep the deepest overlap — the most "load-bearing" contact for one-shot
            // depenetration. A multi-contact resolver would return them all.
            if (best is null || hit.Depth > best.Value.Depth)
            {
                best = hit;
            }
        }
        return best;
    }

    public static CollisionHit2D? Test(Bounds2 aabb, LineMesh2D mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (Test(aabb, mesh.Bounds) is null)
        {
            return null;
        }

        // AABB-vs-segment via closest-point: the segment's nearest point to the AABB
        // centre is treated as a 0-radius capsule contact. Approximate when the segment
        // skims the AABB at a steep angle (same trade as Capsule-vs-AABB).
        CollisionHit2D? best = null;
        for (var i = 0; i < mesh.Segments.Length; i++)
        {
            var seg = mesh.Segments[i];
            var closest = ClosestPointOnSegment(aabb.Center, seg.PointA, seg.PointB);
            if (closest.X < aabb.Min.X || closest.X > aabb.Max.X ||
                closest.Y < aabb.Min.Y || closest.Y > aabb.Max.Y)
            {
                continue;
            }
            var halfSize = aabb.Size * 0.5f;
            var local = closest - aabb.Center;
            var px = halfSize.X - MathF.Abs(local.X);
            var py = halfSize.Y - MathF.Abs(local.Y);
            Vector2 normal;
            float depth;
            if (px < py)
            {
                normal = new Vector2(local.X >= 0.0f ? -1.0f : 1.0f, 0.0f);
                depth = px;
            }
            else
            {
                normal = new Vector2(0.0f, local.Y >= 0.0f ? -1.0f : 1.0f);
                depth = py;
            }
            var hit = new CollisionHit2D { Time = 0.0f, Point = closest, Normal = normal, Depth = depth };
            if (best is null || hit.Depth > best.Value.Depth) best = hit;
        }
        return best;
    }

    public static CollisionHit2D? Test(Capsule2D capsule, LineMesh2D mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (Test(capsule.Bounds, mesh.Bounds) is null)
        {
            return null;
        }

        CollisionHit2D? best = null;
        for (var i = 0; i < mesh.Segments.Length; i++)
        {
            var seg = mesh.Segments[i];
            var (pA, pB) = ClosestPointsOnSegments(capsule.PointA, capsule.PointB, seg.PointA, seg.PointB);
            var delta = pA - pB;
            var distanceSquared = delta.LengthSquared();
            if (distanceSquared >= capsule.Radius * capsule.Radius)
            {
                continue;
            }
            var distance = MathF.Sqrt(distanceSquared);
            var normal = distance > 0.0f ? delta / distance : Vector2.UnitX;
            var hit = new CollisionHit2D
            {
                Time = 0.0f,
                Point = pB,
                Normal = normal,
                Depth = capsule.Radius - distance,
            };
            if (best is null || hit.Depth > best.Value.Depth) best = hit;
        }
        return best;
    }

    public static CollisionHit2D? Test(OrientedBounds2 obb, LineMesh2D mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (Test(obb.Bounds, mesh.Bounds) is null)
        {
            return null;
        }

        // Per-segment: transform endpoints into OBB-local, run a proper segment-vs-AABB
        // test (not the approximate zero-radius-capsule path, which misses when the
        // segment passes through the AABB without either endpoint inside).
        CollisionHit2D? best = null;
        var localAabb = new Bounds2(-obb.HalfExtents, obb.HalfExtents);
        for (var i = 0; i < mesh.Segments.Length; i++)
        {
            var seg = mesh.Segments[i];
            var localA = ToLocal(seg.PointA, obb);
            var localB = ToLocal(seg.PointB, obb);
            if (SegmentIntersectsAabb(localA, localB, localAabb, out var entryT, out var localNormal))
            {
                var localPoint = localA + (localB - localA) * entryT;
                var worldHit = new CollisionHit2D
                {
                    Time = 0.0f,
                    Point = ToWorldPoint(localPoint, obb),
                    Normal = ToWorldDirection(localNormal, obb),
                    // Conservative depth: half of the smaller OBB extent. Game code
                    // depenetrating a static line mesh treats this as a contact point;
                    // a deeper penetration would need clip-against-AABB to measure
                    // the actual intrusion. Out of scope for v0 (LineMesh2D is static
                    // level geometry — game side resolves by stopping motion).
                    Depth = MathF.Min(obb.HalfExtents.X, obb.HalfExtents.Y),
                };
                if (best is null || worldHit.Depth > best.Value.Depth) best = worldHit;
            }
        }
        return best;
    }

    // Segment-vs-AABB via the slab method: parameterise the segment as P(t) = A + t(B-A)
    // for t in [0, 1] and find the t window inside the AABB on each axis. Returns true
    // when the windows overlap; entryT is the parametric position of first contact,
    // entryNormal is the axis-aligned normal of the entry face (pointing back along the
    // segment direction).
    private static bool SegmentIntersectsAabb(Vector2 a, Vector2 b, Bounds2 aabb, out float entryT, out Vector2 entryNormal)
    {
        entryT = 0.0f;
        entryNormal = Vector2.Zero;
        var direction = b - a;
        var tMin = 0.0f;
        var tMax = 1.0f;
        for (var axis = 0; axis < 2; axis++)
        {
            var origin = axis == 0 ? a.X : a.Y;
            var dir = axis == 0 ? direction.X : direction.Y;
            var lo = axis == 0 ? aabb.Min.X : aabb.Min.Y;
            var hi = axis == 0 ? aabb.Max.X : aabb.Max.Y;
            if (MathF.Abs(dir) < 1e-8f)
            {
                if (origin < lo || origin > hi) return false;
                continue;
            }
            var invD = 1.0f / dir;
            var t0 = (lo - origin) * invD;
            var t1 = (hi - origin) * invD;
            var enterAxis = t0 > t1 ? 1.0f : -1.0f;
            if (t0 > t1) (t0, t1) = (t1, t0);
            if (t0 > tMin)
            {
                tMin = t0;
                entryNormal = axis == 0 ? new Vector2(enterAxis, 0.0f) : new Vector2(0.0f, enterAxis);
            }
            tMax = MathF.Min(tMax, t1);
            if (tMin > tMax) return false;
        }
        entryT = tMin;
        return true;
    }

    // ---------------------------------------------------------------------------
    //  Raycasts — one per primitive type.
    // ---------------------------------------------------------------------------

    public static CollisionHit2D? Raycast(Ray2D ray, Circle circle, float maxDistance = float.PositiveInfinity)
    {
        // Quadratic intersection: |O + tD - C|^2 = r^2.
        var oc = ray.Origin - circle.Center;
        var b = Vector2.Dot(oc, ray.Direction);
        var c = oc.LengthSquared() - circle.Radius * circle.Radius;
        var discriminant = b * b - c;
        if (discriminant < 0.0f) return null;
        var sqrt = MathF.Sqrt(discriminant);
        var t = -b - sqrt;
        // Ray origin inside the circle: take the forward exit instead of the backward entry.
        if (t < 0.0f) t = -b + sqrt;
        if (t < 0.0f || t > maxDistance) return null;
        var point = ray.PointAt(t);
        var normal = Vector2.Normalize(point - circle.Center);
        return new CollisionHit2D { Time = t, Point = point, Normal = normal, Depth = 0.0f };
    }

    public static CollisionHit2D? Raycast(Ray2D ray, Bounds2 aabb, float maxDistance = float.PositiveInfinity)
    {
        // Slab method. For each axis, find the [tNear, tFar] window the ray spends inside
        // that slab; intersection is the overlap across axes.
        var tMin = 0.0f;
        var tMax = maxDistance;
        Vector2 normal = Vector2.Zero;
        for (var axis = 0; axis < 2; axis++)
        {
            var origin = axis == 0 ? ray.Origin.X : ray.Origin.Y;
            var dir = axis == 0 ? ray.Direction.X : ray.Direction.Y;
            var min = axis == 0 ? aabb.Min.X : aabb.Min.Y;
            var max = axis == 0 ? aabb.Max.X : aabb.Max.Y;
            if (MathF.Abs(dir) < 1e-8f)
            {
                if (origin < min || origin > max) return null;
                continue;
            }
            var invD = 1.0f / dir;
            var t0 = (min - origin) * invD;
            var t1 = (max - origin) * invD;
            var enterAxis = t0 > t1 ? 1.0f : -1.0f;
            if (t0 > t1) (t0, t1) = (t1, t0);
            if (t0 > tMin)
            {
                tMin = t0;
                normal = axis == 0 ? new Vector2(enterAxis, 0.0f) : new Vector2(0.0f, enterAxis);
            }
            tMax = MathF.Min(tMax, t1);
            if (tMin > tMax) return null;
        }
        if (tMin > maxDistance) return null;
        return new CollisionHit2D { Time = tMin, Point = ray.PointAt(tMin), Normal = normal, Depth = 0.0f };
    }

    public static CollisionHit2D? Raycast(Ray2D ray, Capsule2D capsule, float maxDistance = float.PositiveInfinity)
    {
        // Cap spheres first (cheap), then the cylindrical body via cross-product math
        // on the 2D segment. Keep the closest valid hit.
        CollisionHit2D? best = null;

        if (Raycast(ray, new Circle(capsule.PointA, capsule.Radius), maxDistance) is { } a &&
            (best is null || a.Time < best.Value.Time)) best = a;
        if (Raycast(ray, new Circle(capsule.PointB, capsule.Radius), maxDistance) is { } b &&
            (best is null || b.Time < best.Value.Time)) best = b;

        // Cylinder body: parameterise ray as P(t) = O + tD; segment as Q(u) = A + u(B-A).
        // Find (t, u) minimising |P - Q|^2 = r^2 with u ∈ [0, 1]. Done by Cramer's rule
        // on the 2x2 system; gives a candidate t value to validate.
        var ab = capsule.PointB - capsule.PointA;
        var ao = ray.Origin - capsule.PointA;
        // Closest-points style: project D and ab onto each other.
        var abLenSq = ab.LengthSquared();
        if (abLenSq > 0.0f)
        {
            var dd = Vector2.Dot(ray.Direction, ray.Direction); // 1.0 if unit
            var dab = Vector2.Dot(ray.Direction, ab);
            var aodot = Vector2.Dot(ray.Direction, ao);
            var aoab = Vector2.Dot(ao, ab);
            // From d/dt(|P-Q|^2)=0 and constraint Q is perpendicular projection.
            // Solve a*t + b*u = c1, d*t + e*u = c2 where (after derivation):
            //   a = dd                                          (positive)
            //   b = -dab
            //   c1 = -aodot
            //   d = dab
            //   e = -abLenSq
            //   c2 = -aoab
            var det = dd * (-abLenSq) - (-dab) * dab;
            if (MathF.Abs(det) > 1e-8f)
            {
                var t = (-aodot * (-abLenSq) - (-dab) * (-aoab)) / det;
                var u = (dd * (-aoab) - dab * (-aodot)) / det;
                if (t >= 0.0f && t <= maxDistance && u >= 0.0f && u <= 1.0f)
                {
                    var p = ray.PointAt(t);
                    var q = capsule.PointA + ab * u;
                    var delta = p - q;
                    if (delta.LengthSquared() <= capsule.Radius * capsule.Radius)
                    {
                        // True ray-cylinder hit point is the FIRST contact along the ray.
                        // Approximate Time as t (closest-points t) — good enough for
                        // sweep-free discrete queries; refining to entry-time would
                        // need a quadratic solve along the ray-direction perpendicular.
                        var normal = delta.LengthSquared() > 0.0f
                            ? Vector2.Normalize(delta)
                            : Vector2.UnitX;
                        var hit = new CollisionHit2D
                        {
                            Time = t,
                            Point = p,
                            Normal = normal,
                            Depth = 0.0f,
                        };
                        if (best is null || hit.Time < best.Value.Time) best = hit;
                    }
                }
            }
        }

        return best;
    }

    public static CollisionHit2D? Raycast(Ray2D ray, OrientedBounds2 obb, float maxDistance = float.PositiveInfinity)
    {
        // Transform ray into OBB-local frame, raycast against the axis-aligned box at origin,
        // then map the hit back to world space.
        var localOrigin = ToLocal(ray.Origin, obb);
        var localDir = ToLocalDirection(ray.Direction, obb);
        var localAabb = new Bounds2(-obb.HalfExtents, obb.HalfExtents);
        if (Raycast(new Ray2D(localOrigin, localDir), localAabb, maxDistance) is not { } localHit)
        {
            return null;
        }
        return localHit with
        {
            Point = ToWorldPoint(localHit.Point, obb),
            Normal = ToWorldDirection(localHit.Normal, obb),
        };
    }

    public static CollisionHit2D? Raycast(Ray2D ray, LineMesh2D mesh, float maxDistance = float.PositiveInfinity)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (Raycast(ray, mesh.Bounds, maxDistance) is null)
        {
            return null;
        }

        CollisionHit2D? best = null;
        var bestT = maxDistance;
        for (var i = 0; i < mesh.Segments.Length; i++)
        {
            var seg = mesh.Segments[i];
            if (RaySegmentIntersect(ray, seg.PointA, seg.PointB, bestT) is { } t)
            {
                var point = ray.PointAt(t);
                // Segment normal: perpendicular to segment, pointing toward ray origin so
                // a "ray approaching a wall" reads normal facing the ray.
                var segDir = seg.PointB - seg.PointA;
                var perp = new Vector2(-segDir.Y, segDir.X);
                if (Vector2.Dot(perp, ray.Direction) > 0.0f) perp = -perp;
                var normal = perp.LengthSquared() > 0.0f ? Vector2.Normalize(perp) : Vector2.UnitY;
                best = new CollisionHit2D { Time = t, Point = point, Normal = normal, Depth = 0.0f };
                bestT = t;
            }
        }
        return best;
    }

    // ---------------------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------------------

    // Ericson, "Real-Time Collision Detection", §5.1.2: closest point on segment AB
    // to point P. Returns the projected point clamped to [A, B].
    public static Vector2 ClosestPointOnSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lenSq = ab.LengthSquared();
        if (lenSq == 0.0f) return a;
        var t = Vector2.Dot(point - a, ab) / lenSq;
        t = Math.Clamp(t, 0.0f, 1.0f);
        return a + ab * t;
    }

    // Ericson §5.1.9: closest points on two segments (P0P1, Q0Q1). Returns (pointOnP, pointOnQ).
    // Handles parallel segments via length-squared checks at the boundaries.
    public static (Vector2 PointOnP, Vector2 PointOnQ) ClosestPointsOnSegments(
        Vector2 p0, Vector2 p1, Vector2 q0, Vector2 q1)
    {
        var d1 = p1 - p0;
        var d2 = q1 - q0;
        var r = p0 - q0;
        var a = Vector2.Dot(d1, d1);
        var e = Vector2.Dot(d2, d2);
        var f = Vector2.Dot(d2, r);

        // Both segments degenerate to points.
        if (a <= 1e-8f && e <= 1e-8f) return (p0, q0);

        float s, t;
        if (a <= 1e-8f)
        {
            // First segment is a point.
            s = 0.0f;
            t = Math.Clamp(f / e, 0.0f, 1.0f);
        }
        else
        {
            var c = Vector2.Dot(d1, r);
            if (e <= 1e-8f)
            {
                // Second segment is a point.
                t = 0.0f;
                s = Math.Clamp(-c / a, 0.0f, 1.0f);
            }
            else
            {
                var b = Vector2.Dot(d1, d2);
                var denom = a * e - b * b;
                s = denom != 0.0f ? Math.Clamp((b * f - c * e) / denom, 0.0f, 1.0f) : 0.0f;
                t = (b * s + f) / e;
                if (t < 0.0f) { t = 0.0f; s = Math.Clamp(-c / a, 0.0f, 1.0f); }
                else if (t > 1.0f) { t = 1.0f; s = Math.Clamp((b - c) / a, 0.0f, 1.0f); }
            }
        }
        return (p0 + d1 * s, q0 + d2 * t);
    }

    // Ray-vs-segment intersection. Returns t such that ray.Origin + t * ray.Direction
    // is the intersection, or null. Used by LineMesh2D raycast.
    private static float? RaySegmentIntersect(Ray2D ray, Vector2 a, Vector2 b, float maxT)
    {
        // Parametric: ray = O + tD, segment = A + u(B-A). Solve 2x2 system.
        var ab = b - a;
        var ao = ray.Origin - a;
        var det = ray.Direction.X * (-ab.Y) - ray.Direction.Y * (-ab.X);
        if (MathF.Abs(det) < 1e-8f) return null;
        var invDet = 1.0f / det;
        var t = (-ab.Y * (-ao.X) - (-ab.X) * (-ao.Y)) * invDet;
        var u = (ray.Direction.X * (-ao.Y) - ray.Direction.Y * (-ao.X)) * invDet;
        if (t < 0.0f || t > maxT) return null;
        if (u < 0.0f || u > 1.0f) return null;
        return t;
    }

    // OBB-local transforms. Rotation is CCW around +Z (matching Transform2D).
    private static Vector2 ToLocal(Vector2 world, OrientedBounds2 obb)
    {
        var c = MathF.Cos(-obb.Rotation);
        var s = MathF.Sin(-obb.Rotation);
        var t = world - obb.Center;
        return new Vector2(c * t.X - s * t.Y, s * t.X + c * t.Y);
    }

    private static Vector2 ToLocalDirection(Vector2 worldDir, OrientedBounds2 obb)
    {
        var c = MathF.Cos(-obb.Rotation);
        var s = MathF.Sin(-obb.Rotation);
        return new Vector2(c * worldDir.X - s * worldDir.Y, s * worldDir.X + c * worldDir.Y);
    }

    private static Vector2 ToWorldDirection(Vector2 localDir, OrientedBounds2 obb)
    {
        var c = MathF.Cos(obb.Rotation);
        var s = MathF.Sin(obb.Rotation);
        return new Vector2(c * localDir.X - s * localDir.Y, s * localDir.X + c * localDir.Y);
    }

    private static Vector2 ToWorldPoint(Vector2 localPoint, OrientedBounds2 obb)
    {
        return obb.Center + ToWorldDirection(localPoint, obb);
    }

    private static void FillCorners(OrientedBounds2 obb, Span<Vector2> dst)
    {
        var x = obb.AxisX * obb.HalfExtents.X;
        var y = obb.AxisY * obb.HalfExtents.Y;
        dst[0] = obb.Center - x - y;
        dst[1] = obb.Center + x - y;
        dst[2] = obb.Center + x + y;
        dst[3] = obb.Center - x + y;
    }

    // SAT discrete test: project both 4-corner shapes onto each axis, look for any
    // axis where intervals are disjoint (separating axis -> no collision). Track the
    // minimum-overlap axis as the depenetration vector.
    private static CollisionHit2D? SatTest(
        ReadOnlySpan<Vector2> a, ReadOnlySpan<Vector2> b, ReadOnlySpan<Vector2> axes,
        Vector2 centerA, Vector2 centerB)
    {
        var minOverlap = float.PositiveInfinity;
        var minAxis = Vector2.UnitX;
        for (var i = 0; i < axes.Length; i++)
        {
            var axis = axes[i];
            if (axis.LengthSquared() < 1e-12f) continue;
            var (aMin, aMax) = ProjectShape(a, axis);
            var (bMin, bMax) = ProjectShape(b, axis);
            var overlap = MathF.Min(aMax, bMax) - MathF.Max(aMin, bMin);
            if (overlap <= 0.0f) return null;
            if (overlap < minOverlap)
            {
                minOverlap = overlap;
                minAxis = axis;
            }
        }
        // Normal from B into A.
        var direction = centerA - centerB;
        if (Vector2.Dot(minAxis, direction) < 0.0f) minAxis = -minAxis;
        var contact = (centerA + centerB) * 0.5f; // approximate — exact contact pt needs clipping
        return new CollisionHit2D { Time = 0.0f, Point = contact, Normal = minAxis, Depth = minOverlap };
    }

    private static (float Min, float Max) ProjectShape(ReadOnlySpan<Vector2> corners, Vector2 axis)
    {
        var min = Vector2.Dot(corners[0], axis);
        var max = min;
        for (var i = 1; i < corners.Length; i++)
        {
            var v = Vector2.Dot(corners[i], axis);
            if (v < min) min = v;
            else if (v > max) max = v;
        }
        return (min, max);
    }
}
