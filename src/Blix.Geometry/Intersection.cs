using System.Numerics;

namespace Blix.Geometry;

// Discrete intersection tests. Each Test returns null when the two shapes are disjoint
// and a CollisionHit (with Time = 0) when they overlap. Hit.Normal points from B into
// A — apply Normal * Depth to A's position to depenetrate.
//
// Sweep tests (Intersection.Sweep) are deliberately defined in the same class but stay
// unimplemented until Phase 2. The signatures lock in the CCD-friendly shape so call
// sites that use sweep tomorrow look identical to discrete tests today.
public static class Intersection
{
    // -- Discrete -----------------------------------------------------------------

    public static CollisionHit? Test(BoundingSphere a, BoundingSphere b)
    {
        var delta = a.Center - b.Center;
        var distSq = delta.LengthSquared();
        var rSum = a.Radius + b.Radius;

        if (distSq >= rSum * rSum)
        {
            return null;
        }

        // Concentric spheres are a degenerate case — pick a stable normal so depen.
        // direction is deterministic rather than NaN-from-zero-length.
        var dist = MathF.Sqrt(distSq);
        var normal = dist > 0.0f ? delta / dist : Vector3.UnitY;

        return new CollisionHit
        {
            Time = 0.0f,
            Point = b.Center + normal * b.Radius,
            Normal = normal,
            Depth = rSum - dist,
        };
    }

    public static CollisionHit? Test(BoundingSphere sphere, Bounds3 aabb)
    {
        // Closest point on the AABB to the sphere center, then standard sphere-point
        // separation test.
        var closest = Vector3.Clamp(sphere.Center, aabb.Min, aabb.Max);
        var delta = sphere.Center - closest;
        var distSq = delta.LengthSquared();

        if (distSq >= sphere.Radius * sphere.Radius)
        {
            return null;
        }

        // If sphere center is inside the AABB, delta is zero — fall back to the axis
        // with the smallest exit distance for a stable normal.
        Vector3 normal;
        float depth;
        if (distSq > 0.0f)
        {
            var dist = MathF.Sqrt(distSq);
            normal = delta / dist;
            depth = sphere.Radius - dist;
        }
        else
        {
            var toMin = sphere.Center - aabb.Min;
            var toMax = aabb.Max - sphere.Center;
            var dx = MathF.Min(toMin.X, toMax.X);
            var dy = MathF.Min(toMin.Y, toMax.Y);
            var dz = MathF.Min(toMin.Z, toMax.Z);
            if (dx <= dy && dx <= dz)
            {
                normal = new Vector3(toMin.X < toMax.X ? -1.0f : 1.0f, 0.0f, 0.0f);
                depth = dx + sphere.Radius;
            }
            else if (dy <= dz)
            {
                normal = new Vector3(0.0f, toMin.Y < toMax.Y ? -1.0f : 1.0f, 0.0f);
                depth = dy + sphere.Radius;
            }
            else
            {
                normal = new Vector3(0.0f, 0.0f, toMin.Z < toMax.Z ? -1.0f : 1.0f);
                depth = dz + sphere.Radius;
            }
        }

        return new CollisionHit
        {
            Time = 0.0f,
            Point = closest,
            Normal = normal,
            Depth = depth,
        };
    }

    public static CollisionHit? Test(Bounds3 a, Bounds3 b)
    {
        // Standard AABB-AABB SAT: disjoint if any axis has no overlap. Otherwise the
        // contact normal lies along the axis with smallest push-out distance (the
        // minimum-translation vector to separate the boxes).
        var aMin = a.Min;
        var aMax = a.Max;
        var bMin = b.Min;
        var bMax = b.Max;

        if (aMax.X < bMin.X || aMin.X > bMax.X) return null;
        if (aMax.Y < bMin.Y || aMin.Y > bMax.Y) return null;
        if (aMax.Z < bMin.Z || aMin.Z > bMax.Z) return null;

        // Per-axis push-out distances:
        //   pushPos = distance to move A in the +axis direction to clear B
        //           = bMax - aMin
        //   pushNeg = distance to move A in the -axis direction to clear B
        //           = aMax - bMin
        // Smaller of the two is the minimum push-out on that axis. (For the typical
        // "A partially inside B from one side" case this equals the overlap-interval
        // length; for "A fully inside B on some axis" it's the smaller of the two
        // distances to either edge, which the overlap-interval formula would
        // under-report as the boxed-in dimension instead of the push-out distance.)
        var pushPosX = bMax.X - aMin.X;
        var pushNegX = aMax.X - bMin.X;
        var pushPosY = bMax.Y - aMin.Y;
        var pushNegY = aMax.Y - bMin.Y;
        var pushPosZ = bMax.Z - aMin.Z;
        var pushNegZ = aMax.Z - bMin.Z;

        var overlapX = MathF.Min(pushPosX, pushNegX);
        var overlapY = MathF.Min(pushPosY, pushNegY);
        var overlapZ = MathF.Min(pushPosZ, pushNegZ);

        Vector3 normal;
        float depth;
        if (overlapX <= overlapY && overlapX <= overlapZ)
        {
            normal = new Vector3(pushPosX < pushNegX ? 1.0f : -1.0f, 0.0f, 0.0f);
            depth = overlapX;
        }
        else if (overlapY <= overlapZ)
        {
            normal = new Vector3(0.0f, pushPosY < pushNegY ? 1.0f : -1.0f, 0.0f);
            depth = overlapY;
        }
        else
        {
            normal = new Vector3(0.0f, 0.0f, pushPosZ < pushNegZ ? 1.0f : -1.0f);
            depth = overlapZ;
        }

        // Contact point: midpoint of the overlap region — not on any particular
        // surface, but stable and visually sensible for debug draw.
        var contactMin = Vector3.Max(aMin, bMin);
        var contactMax = Vector3.Min(aMax, bMax);
        var point = (contactMin + contactMax) * 0.5f;

        return new CollisionHit
        {
            Time = 0.0f,
            Point = point,
            Normal = normal,
            Depth = depth,
        };
    }

    // -- Discrete vs Plane --------------------------------------------------------
    // A Plane represents the boundary of a solid halfspace on the negative-Normal
    // side. These tests report overlap when the shape penetrates that halfspace and
    // return Normal = plane.Normal (push out of the solid) with Depth = how far the
    // deepest point of the shape sits below the plane.

    public static CollisionHit? Test(BoundingSphere sphere, Plane plane)
    {
        var centreDist = plane.SignedDistance(sphere.Center);
        if (centreDist >= sphere.Radius)
        {
            return null;
        }
        return new CollisionHit
        {
            Time = 0.0f,
            Point = sphere.Center - plane.Normal * centreDist,
            Normal = plane.Normal,
            Depth = sphere.Radius - centreDist,
        };
    }

    public static CollisionHit? Test(Bounds3 aabb, Plane plane)
    {
        // The signed distance from the AABB center to the plane, plus/minus the
        // projection of the half-extents onto the normal, gives the range of signed
        // distances spanned by the AABB. If the minimum is < 0, the box penetrates
        // the halfspace.
        var center = aabb.Center;
        var halfExtents = aabb.Size * 0.5f;
        var radius =
            halfExtents.X * MathF.Abs(plane.Normal.X) +
            halfExtents.Y * MathF.Abs(plane.Normal.Y) +
            halfExtents.Z * MathF.Abs(plane.Normal.Z);
        var centreDist = plane.SignedDistance(center);
        var minDist = centreDist - radius;
        if (minDist >= 0.0f)
        {
            return null;
        }
        // Contact point: the box's deepest corner projected onto the plane. The
        // deepest corner is on the negative-normal side of the centre.
        var deepest = new Vector3(
            center.X - MathF.Sign(plane.Normal.X) * halfExtents.X,
            center.Y - MathF.Sign(plane.Normal.Y) * halfExtents.Y,
            center.Z - MathF.Sign(plane.Normal.Z) * halfExtents.Z);
        var deepestDist = plane.SignedDistance(deepest);
        return new CollisionHit
        {
            Time = 0.0f,
            Point = deepest - plane.Normal * deepestDist,
            Normal = plane.Normal,
            Depth = -minDist,
        };
    }

    // -- Discrete vs Capsule ------------------------------------------------------
    // Capsule = swept sphere; every Capsule-X test reduces to "closest point on
    // capsule's segment, then sphere-vs-X at that point" or, for the
    // Capsule-Triangle path, the broader "closest pair between segment and
    // triangle features." Normal points from B into A; depth is `radius - dist`
    // (the residual once we've measured how deeply A pierces B).

    public static CollisionHit? Test(Capsule a, Capsule b)
    {
        // Closest points between the two segments, then sphere-sphere at those
        // points using the per-capsule radii.
        var (pa, pb) = ClosestPointsOnSegments(a.PointA, a.PointB, b.PointA, b.PointB);
        return Test(new BoundingSphere(pa, a.Radius), new BoundingSphere(pb, b.Radius));
    }

    public static CollisionHit? Test(BoundingSphere sphere, Capsule capsule)
    {
        // Closest point on capsule segment to sphere centre — capsule degenerates
        // to a sphere at that point with capsule.Radius. Hit returned with normal
        // pointing from capsule (B) into sphere (A), matching the API convention.
        var closest = ClosestPointOnSegment(sphere.Center, capsule.PointA, capsule.PointB);
        return Test(sphere, new BoundingSphere(closest, capsule.Radius));
    }

    public static CollisionHit? Test(Capsule capsule, Bounds3 aabb)
    {
        // Approximate: find the segment point closest to the AABB's centre, then
        // sphere-vs-AABB at that point with capsule.Radius. Exact for the common
        // character-vs-wall case (segment outside the AABB); approximate when the
        // segment passes through the AABB at a steep angle (rare for character
        // controllers).
        var aabbCenter = (aabb.Min + aabb.Max) * 0.5f;
        var pointOnSegment = ClosestPointOnSegment(aabbCenter, capsule.PointA, capsule.PointB);
        return Test(new BoundingSphere(pointOnSegment, capsule.Radius), aabb);
    }

    public static CollisionHit? Test(Capsule capsule, Plane plane)
    {
        // Lowest endpoint by signed distance vs plane. Capsule overlaps iff that
        // endpoint is closer than capsule.Radius. Normal is plane.Normal (push
        // capsule out of the solid half-space).
        var d1 = plane.SignedDistance(capsule.PointA);
        var d2 = plane.SignedDistance(capsule.PointB);
        var lowest = d1 < d2 ? capsule.PointA : capsule.PointB;
        var lowestDist = MathF.Min(d1, d2);
        if (lowestDist >= capsule.Radius) return null;

        return new CollisionHit
        {
            Time = 0.0f,
            Point = lowest - plane.Normal * lowestDist,   // project onto plane
            Normal = plane.Normal,
            Depth = capsule.Radius - lowestDist,
        };
    }

    public static CollisionHit? Test(Capsule capsule, TriangleMesh3D mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (Test(capsule.Bounds, mesh.Bounds) is null) return null;

        CollisionHit? deepest = null;
        foreach (var tri in mesh.Triangles)
        {
            if (TestCapsuleTriangle(capsule, tri) is { } hit &&
                (deepest is null || hit.Depth > deepest.Value.Depth))
            {
                deepest = hit;
            }
        }
        return deepest;
    }

    // Capsule vs single triangle: find the closest pair between the capsule's
    // segment and the triangle's features (vertex regions, edge regions, face
    // interior). The closest pair determines the contact; if distance <
    // capsule.Radius, we have an overlap.
    //
    // NINE candidate pairs evaluated:
    //   2 segment endpoints vs triangle (closest-point-on-triangle)
    //   3 triangle vertices vs segment (closest-point-on-segment)
    //   3 triangle edges vs segment    (closest-points-on-two-segments)
    //   1 the segment's crossing of the triangle's PLANE
    // The minimum-distance pair is the contact.
    //
    // <b>The ninth was missing, and it is the impaled case.</b> A segment that passes clean through
    // the middle of a triangle is as intersected as anything can be, and the distance between them
    // is zero — but no endpoint, vertex or edge candidate is anywhere near it. For the unit triangle
    // in Blix.Test.Physics3D every one of the original eight sat a metre away, so the test returned
    // null for a capsule with a triangle through its waist.
    //
    // It shipped in the initial commit and was never exercised: nothing in this tree had ever
    // constructed a Capsule or a TriangleMesh3D outside Blix.Geometry until the character lab. The
    // failure mode it would have produced is the memorable kind — a body that clips into a wall
    // reports no contact at all once its axis is past the surface, so it neither stops nor pushes
    // out, and it drifts through.
    //
    // The crossing point is added as a candidate rather than special-cased: when it lands inside the
    // triangle the pair is (p, p) and the distance is zero, and when it lands outside it is simply
    // another point whose distance the minimum ignores.
    /// <summary>
    /// The closest pair between a capsule's SEGMENT and a triangle: squared distance, the point on
    /// the segment, and the point on the triangle.
    /// </summary>
    /// <remarks>
    /// Split out because the discrete test and the sweep below need exactly the same answer at
    /// different moments — one at rest, one at a proposed time — and two copies of a nine-candidate
    /// search is two places for the ninth to go missing again.
    /// <para>
    /// Note this is the segment's distance, not the capsule's: the capsule's surface is this minus
    /// the radius. Callers subtract, because a sweep wants the signed gap and a test wants the
    /// overlap, and those are the same number with opposite signs.
    /// </para>
    /// </remarks>
    private static (float DistanceSquared, Vector3 OnSegment, Vector3 OnTriangle) ClosestPair(
        Capsule capsule, Triangle tri)
    {
        var bestSegPoint = capsule.PointA;
        var bestTriPoint = tri.V0;
        var bestDistSq = float.PositiveInfinity;

        void Consider(Vector3 segPoint, Vector3 triPoint)
        {
            var dsq = (segPoint - triPoint).LengthSquared();
            if (dsq < bestDistSq)
            {
                bestDistSq = dsq;
                bestSegPoint = segPoint;
                bestTriPoint = triPoint;
            }
        }

        // 2 segment endpoints vs triangle
        Consider(capsule.PointA, ClosestPointOnTriangle(capsule.PointA, tri.V0, tri.V1, tri.V2));
        Consider(capsule.PointB, ClosestPointOnTriangle(capsule.PointB, tri.V0, tri.V1, tri.V2));

        // 3 triangle vertices vs segment
        Consider(ClosestPointOnSegment(tri.V0, capsule.PointA, capsule.PointB), tri.V0);
        Consider(ClosestPointOnSegment(tri.V1, capsule.PointA, capsule.PointB), tri.V1);
        Consider(ClosestPointOnSegment(tri.V2, capsule.PointA, capsule.PointB), tri.V2);

        // The segment's crossing of the triangle's plane — the case the other eight cannot see.
        var faceNormal = tri.NormalRaw;
        var faceLengthSq = faceNormal.LengthSquared();
        if (faceLengthSq > 1e-18f)
        {
            var unit = faceNormal / MathF.Sqrt(faceLengthSq);
            var da = Vector3.Dot(unit, capsule.PointA - tri.V0);
            var db = Vector3.Dot(unit, capsule.PointB - tri.V0);

            // Strictly opposite sides. A segment that merely touches the plane is already covered by
            // the endpoint candidates, and dividing by (da - db) when both are zero is not.
            if ((da < 0f && db > 0f) || (da > 0f && db < 0f))
            {
                var crossing = Vector3.Lerp(capsule.PointA, capsule.PointB, da / (da - db));
                Consider(crossing, ClosestPointOnTriangle(crossing, tri.V0, tri.V1, tri.V2));
            }
        }

        // 3 triangle edges vs segment
        var (s01, t01) = ClosestPointsOnSegments(capsule.PointA, capsule.PointB, tri.V0, tri.V1);
        Consider(s01, t01);
        var (s12, t12) = ClosestPointsOnSegments(capsule.PointA, capsule.PointB, tri.V1, tri.V2);
        Consider(s12, t12);
        var (s20, t20) = ClosestPointsOnSegments(capsule.PointA, capsule.PointB, tri.V2, tri.V0);
        Consider(s20, t20);

        return (bestDistSq, bestSegPoint, bestTriPoint);
    }

    private static CollisionHit? TestCapsuleTriangle(Capsule capsule, Triangle tri)
    {
        var (bestDistSq, bestSegPoint, bestTriPoint) = ClosestPair(capsule, tri);

        if (bestDistSq >= capsule.Radius * capsule.Radius) return null;

        var dist = MathF.Sqrt(bestDistSq);
        Vector3 normal;
        if (dist > 1e-6f)
        {
            normal = (bestSegPoint - bestTriPoint) / dist;
        }
        else
        {
            // The segment crosses the face: there is no direction between the closest pair, because
            // they are the same point. The triangle's OUTWARD normal is the answer, unsigned.
            //
            // <b>Signing it toward an endpoint is wrong, and the resolver found that out.</b> The
            // first version pointed it toward whichever side PointA was on — which for a body
            // sinking into a floor is the side UNDERNEATH, so depenetration pushed it further in.
            // A winding is not a tie-break; for a closed solid it already says which side is
            // outside, which is exactly what a body that is inside needs to be told.
            //
            // That relies on the mesh being closed and outward-wound. Blix.Labs.Character's probe
            // checks both for every solid in its room, which is where that check earns its keep:
            // it is not tidiness, it is the precondition for getting an impaled body out.
            //
            // The Depth here is the radius, which UNDERSTATES an impaled capsule — the true minimum
            // translation also has to carry the axis back out. Stated rather than papered over, and
            // it is why a resolver runs depenetration more than once: each pass gets the body
            // shallower until the ordinary closest-pair case takes over and finishes the job.
            normal = Vector3.Normalize(tri.NormalRaw);
        }
        return new CollisionHit
        {
            Time = 0.0f,
            Point = bestTriPoint,
            Normal = normal,
            Depth = capsule.Radius - dist,
        };
    }

    // -- Discrete vs OrientedBounds3 ---------------------------------------------
    // OBB tests use the "transform the query into OBB-local space, run the AABB
    // test there, transform the contact back to world" pattern wherever possible.
    // OBB-OBB is the exception: full 15-axis SAT, mirroring Bounds3-Bounds3 but
    // with rotated axes.

    public static CollisionHit? Test(BoundingSphere sphere, OrientedBounds3 obb)
    {
        // Sphere centre in OBB-local frame -> standard sphere-AABB at origin with
        // OBB's half-extents. Contact data transformed back to world.
        var invRot = Quaternion.Inverse(obb.Orientation);
        var localCenter = Vector3.Transform(sphere.Center - obb.Center, invRot);
        var aabbAtOrigin = new Bounds3(-obb.HalfExtents, obb.HalfExtents);
        var hit = Test(new BoundingSphere(localCenter, sphere.Radius), aabbAtOrigin);
        if (hit is null) return null;

        var localHit = hit.Value;
        var worldPoint = obb.Center + Vector3.Transform(localHit.Point, obb.Orientation);
        var worldNormal = Vector3.Transform(localHit.Normal, obb.Orientation);
        return new CollisionHit
        {
            Time = 0.0f,
            Point = worldPoint,
            Normal = worldNormal,
            Depth = localHit.Depth,
        };
    }

    public static CollisionHit? Test(OrientedBounds3 obb, Bounds3 aabb)
    {
        // Reuse the OBB-OBB path: an AABB is an OBB with identity rotation and
        // centre = aabb.Center, half-extents = aabb extents/2.
        var aabbCenter = (aabb.Min + aabb.Max) * 0.5f;
        var aabbHalf = (aabb.Max - aabb.Min) * 0.5f;
        return Test(obb, new OrientedBounds3(aabbCenter, Quaternion.Identity, aabbHalf));
    }

    public static CollisionHit? Test(OrientedBounds3 a, OrientedBounds3 b)
    {
        // 15-axis SAT: 3 face normals from each box (6 total) plus the 9
        // cross-products of edge directions. Reference: Ericson RTCD §4.4.1.
        //
        // Returned contact normal points from B into A (matching the API
        // convention) along whichever axis has the smallest overlap. Depth is
        // that overlap. Contact point is approximated as A's centre projected
        // toward B by Depth/2 along the normal -- good enough for kinematic
        // depenetration without a proper contact-manifold solver.
        var ax = Vector3.Transform(Vector3.UnitX, a.Orientation);
        var ay = Vector3.Transform(Vector3.UnitY, a.Orientation);
        var az = Vector3.Transform(Vector3.UnitZ, a.Orientation);
        var bx = Vector3.Transform(Vector3.UnitX, b.Orientation);
        var by = Vector3.Transform(Vector3.UnitY, b.Orientation);
        var bz = Vector3.Transform(Vector3.UnitZ, b.Orientation);
        var t = b.Center - a.Center;

        Span<Vector3> aAxes = stackalloc Vector3[3] { ax, ay, az };
        Span<Vector3> bAxes = stackalloc Vector3[3] { bx, by, bz };
        Span<float> aE = stackalloc float[3] { a.HalfExtents.X, a.HalfExtents.Y, a.HalfExtents.Z };
        Span<float> bE = stackalloc float[3] { b.HalfExtents.X, b.HalfExtents.Y, b.HalfExtents.Z };

        // Project both OBBs onto a candidate axis; return overlap (positive)
        // or -1 if disjoint.
        static float Overlap(
            Vector3 axis,
            ReadOnlySpan<Vector3> aAxes, ReadOnlySpan<float> aE,
            ReadOnlySpan<Vector3> bAxes, ReadOnlySpan<float> bE,
            Vector3 t)
        {
            float lenSq = axis.LengthSquared();
            if (lenSq < 1e-12f) return float.PositiveInfinity;   // skip near-degenerate cross-product axes
            float normFactor = 1.0f / MathF.Sqrt(lenSq);
            axis *= normFactor;

            float ra = MathF.Abs(Vector3.Dot(aAxes[0], axis)) * aE[0]
                     + MathF.Abs(Vector3.Dot(aAxes[1], axis)) * aE[1]
                     + MathF.Abs(Vector3.Dot(aAxes[2], axis)) * aE[2];
            float rb = MathF.Abs(Vector3.Dot(bAxes[0], axis)) * bE[0]
                     + MathF.Abs(Vector3.Dot(bAxes[1], axis)) * bE[1]
                     + MathF.Abs(Vector3.Dot(bAxes[2], axis)) * bE[2];
            float td = MathF.Abs(Vector3.Dot(t, axis));
            return ra + rb - td;
        }

        var bestOverlap = float.PositiveInfinity;
        var bestAxis = Vector3.UnitY;

        void Consider(Vector3 axis, ref float bestOverlap, ref Vector3 bestAxis,
                      ReadOnlySpan<Vector3> aAxes, ReadOnlySpan<float> aE,
                      ReadOnlySpan<Vector3> bAxes, ReadOnlySpan<float> bE,
                      Vector3 t)
        {
            var o = Overlap(axis, aAxes, aE, bAxes, bE, t);
            if (o < 0.0f) { bestOverlap = -1.0f; return; }
            if (o < bestOverlap)
            {
                bestOverlap = o;
                bestAxis = Vector3.Normalize(axis);
            }
        }

        // 6 face-normal axes
        Consider(ax, ref bestOverlap, ref bestAxis, aAxes, aE, bAxes, bE, t); if (bestOverlap < 0.0f) return null;
        Consider(ay, ref bestOverlap, ref bestAxis, aAxes, aE, bAxes, bE, t); if (bestOverlap < 0.0f) return null;
        Consider(az, ref bestOverlap, ref bestAxis, aAxes, aE, bAxes, bE, t); if (bestOverlap < 0.0f) return null;
        Consider(bx, ref bestOverlap, ref bestAxis, aAxes, aE, bAxes, bE, t); if (bestOverlap < 0.0f) return null;
        Consider(by, ref bestOverlap, ref bestAxis, aAxes, aE, bAxes, bE, t); if (bestOverlap < 0.0f) return null;
        Consider(bz, ref bestOverlap, ref bestAxis, aAxes, aE, bAxes, bE, t); if (bestOverlap < 0.0f) return null;

        // 9 cross-product axes
        Consider(Vector3.Cross(ax, bx), ref bestOverlap, ref bestAxis, aAxes, aE, bAxes, bE, t); if (bestOverlap < 0.0f) return null;
        Consider(Vector3.Cross(ax, by), ref bestOverlap, ref bestAxis, aAxes, aE, bAxes, bE, t); if (bestOverlap < 0.0f) return null;
        Consider(Vector3.Cross(ax, bz), ref bestOverlap, ref bestAxis, aAxes, aE, bAxes, bE, t); if (bestOverlap < 0.0f) return null;
        Consider(Vector3.Cross(ay, bx), ref bestOverlap, ref bestAxis, aAxes, aE, bAxes, bE, t); if (bestOverlap < 0.0f) return null;
        Consider(Vector3.Cross(ay, by), ref bestOverlap, ref bestAxis, aAxes, aE, bAxes, bE, t); if (bestOverlap < 0.0f) return null;
        Consider(Vector3.Cross(ay, bz), ref bestOverlap, ref bestAxis, aAxes, aE, bAxes, bE, t); if (bestOverlap < 0.0f) return null;
        Consider(Vector3.Cross(az, bx), ref bestOverlap, ref bestAxis, aAxes, aE, bAxes, bE, t); if (bestOverlap < 0.0f) return null;
        Consider(Vector3.Cross(az, by), ref bestOverlap, ref bestAxis, aAxes, aE, bAxes, bE, t); if (bestOverlap < 0.0f) return null;
        Consider(Vector3.Cross(az, bz), ref bestOverlap, ref bestAxis, aAxes, aE, bAxes, bE, t); if (bestOverlap < 0.0f) return null;

        // Flip the normal so it points from B into A (push A out of B).
        if (Vector3.Dot(bestAxis, t) > 0.0f) bestAxis = -bestAxis;

        return new CollisionHit
        {
            Time = 0.0f,
            Point = a.Center - bestAxis * (bestOverlap * 0.5f),
            Normal = bestAxis,
            Depth = bestOverlap,
        };
    }

    public static CollisionHit? Test(OrientedBounds3 obb, Plane plane)
    {
        // Project each OBB axis onto the plane normal and accumulate the
        // half-extent contributions to get the OBB's "radius" along that normal.
        // Then it's just signed_distance(center) vs that radius.
        var ax = Vector3.Transform(Vector3.UnitX, obb.Orientation);
        var ay = Vector3.Transform(Vector3.UnitY, obb.Orientation);
        var az = Vector3.Transform(Vector3.UnitZ, obb.Orientation);
        float r = MathF.Abs(Vector3.Dot(plane.Normal, ax)) * obb.HalfExtents.X
                + MathF.Abs(Vector3.Dot(plane.Normal, ay)) * obb.HalfExtents.Y
                + MathF.Abs(Vector3.Dot(plane.Normal, az)) * obb.HalfExtents.Z;
        var d = plane.SignedDistance(obb.Center);
        if (d - r >= 0.0f) return null;
        var depth = r - d;
        return new CollisionHit
        {
            Time = 0.0f,
            Point = obb.Center - plane.Normal * d,
            Normal = plane.Normal,
            Depth = depth,
        };
    }

    public static CollisionHit? Test(Capsule capsule, OrientedBounds3 obb)
    {
        // Same "transform query to OBB-local, run AABB version" trick as
        // sphere-OBB. The capsule's segment endpoints rotate into the OBB's
        // local frame; the capsule-AABB test runs at the origin.
        var invRot = Quaternion.Inverse(obb.Orientation);
        var localA = Vector3.Transform(capsule.PointA - obb.Center, invRot);
        var localB = Vector3.Transform(capsule.PointB - obb.Center, invRot);
        var aabbAtOrigin = new Bounds3(-obb.HalfExtents, obb.HalfExtents);
        var hit = Test(new Capsule(localA, localB, capsule.Radius), aabbAtOrigin);
        if (hit is null) return null;

        var localHit = hit.Value;
        return new CollisionHit
        {
            Time = 0.0f,
            Point = obb.Center + Vector3.Transform(localHit.Point, obb.Orientation),
            Normal = Vector3.Transform(localHit.Normal, obb.Orientation),
            Depth = localHit.Depth,
        };
    }

    public static CollisionHit? Test(OrientedBounds3 obb, TriangleMesh3D mesh)
    {
        // AABB early-out, then per-triangle test via "transform triangle into
        // OBB-local frame, run AABB-vs-triangle at origin." Avoids duplicating
        // the 13-axis SAT inside this file.
        ArgumentNullException.ThrowIfNull(mesh);
        if (Test(obb.Bounds, mesh.Bounds) is null) return null;

        var invRot = Quaternion.Inverse(obb.Orientation);
        var aabbAtOrigin = new Bounds3(-obb.HalfExtents, obb.HalfExtents);

        CollisionHit? deepest = null;
        foreach (var tri in mesh.Triangles)
        {
            var localTri = new Triangle(
                Vector3.Transform(tri.V0 - obb.Center, invRot),
                Vector3.Transform(tri.V1 - obb.Center, invRot),
                Vector3.Transform(tri.V2 - obb.Center, invRot));
            if (TestAabbTriangle(aabbAtOrigin, localTri) is { } hit &&
                (deepest is null || hit.Depth > deepest.Value.Depth))
            {
                deepest = new CollisionHit
                {
                    Time = 0.0f,
                    Point = obb.Center + Vector3.Transform(hit.Point, obb.Orientation),
                    Normal = Vector3.Transform(hit.Normal, obb.Orientation),
                    Depth = hit.Depth,
                };
            }
        }
        return deepest;
    }

    // -- Discrete vs TriangleMesh3D -----------------------------------------------
    // Both tests early-out against the mesh's aggregate AABB, then iterate triangles
    // and return the DEEPEST contact (largest overlap depth) across all hit
    // triangles. A body resting on a flat mesh-floor that spans multiple triangles
    // therefore reports one contact per query — the rest of the touching triangles
    // contribute no extra information for depenetration along a flat surface.

    public static CollisionHit? Test(BoundingSphere sphere, TriangleMesh3D mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (Test(sphere, mesh.Bounds) is null) return null;

        CollisionHit? deepest = null;
        foreach (var tri in mesh.Triangles)
        {
            if (TestSphereTriangle(sphere, tri) is { } hit &&
                (deepest is null || hit.Depth > deepest.Value.Depth))
            {
                deepest = hit;
            }
        }
        return deepest;
    }

    public static CollisionHit? Test(Bounds3 aabb, TriangleMesh3D mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (Test(aabb, mesh.Bounds) is null) return null;

        CollisionHit? deepest = null;
        foreach (var tri in mesh.Triangles)
        {
            if (TestAabbTriangle(aabb, tri) is { } hit &&
                (deepest is null || hit.Depth > deepest.Value.Depth))
            {
                deepest = hit;
            }
        }
        return deepest;
    }

    // Sphere vs single triangle: closest-point-on-triangle, then distance-vs-radius.
    // Exact — handles vertex, edge, and face contact regions correctly.
    private static CollisionHit? TestSphereTriangle(BoundingSphere sphere, Triangle tri)
    {
        var closest = ClosestPointOnTriangle(sphere.Center, tri.V0, tri.V1, tri.V2);
        var delta = sphere.Center - closest;
        var distSq = delta.LengthSquared();
        if (distSq >= sphere.Radius * sphere.Radius) return null;

        var dist = MathF.Sqrt(distSq);
        // Sphere centre exactly on the triangle → fall back to the triangle's own
        // face normal so the response direction is at least sensible.
        var normal = dist > 0.0f ? delta / dist : tri.Normal;

        return new CollisionHit
        {
            Time = 0.0f,
            Point = closest,
            Normal = normal,
            Depth = sphere.Radius - dist,
        };
    }

    // AABB vs single triangle: Akenine-Möller SAT with all 13 axes (3 box face
    // normals + 1 triangle face normal + 9 box-axis × triangle-edge cross products).
    // Yes/no overlap is exact; contact depth and normal are derived from the
    // triangle's face plane — best-effort for face contacts, slightly suboptimal
    // for edge/vertex contacts (which would need per-axis penetration tracking
    // through the SAT, ~3× the code). Documented limitation.
    private static CollisionHit? TestAabbTriangle(Bounds3 aabb, Triangle tri)
    {
        var center = aabb.Center;
        var e = aabb.Size * 0.5f;

        // Translate triangle so the box is centred at origin.
        var v0 = tri.V0 - center;
        var v1 = tri.V1 - center;
        var v2 = tri.V2 - center;

        // Triangle edges.
        var f0 = v1 - v0;
        var f1 = v2 - v1;
        var f2 = v0 - v2;

        // 9 cross-product axes: each box axis (X, Y, Z) crossed with each triangle
        // edge. AxisTest projects the triangle vertices onto the axis, computes the
        // box's projected radius, and returns false if the intervals are disjoint.
        if (!AxisTest( 0.0f, -f0.Z,  f0.Y, v0, v1, v2, e)) return null;
        if (!AxisTest( 0.0f, -f1.Z,  f1.Y, v0, v1, v2, e)) return null;
        if (!AxisTest( 0.0f, -f2.Z,  f2.Y, v0, v1, v2, e)) return null;
        if (!AxisTest( f0.Z,  0.0f, -f0.X, v0, v1, v2, e)) return null;
        if (!AxisTest( f1.Z,  0.0f, -f1.X, v0, v1, v2, e)) return null;
        if (!AxisTest( f2.Z,  0.0f, -f2.X, v0, v1, v2, e)) return null;
        if (!AxisTest(-f0.Y,  f0.X,  0.0f, v0, v1, v2, e)) return null;
        if (!AxisTest(-f1.Y,  f1.X,  0.0f, v0, v1, v2, e)) return null;
        if (!AxisTest(-f2.Y,  f2.X,  0.0f, v0, v1, v2, e)) return null;

        // 3 box face normal tests — disjoint if the triangle is entirely on one side.
        if (MathF.Min(MathF.Min(v0.X, v1.X), v2.X) >  e.X) return null;
        if (MathF.Max(MathF.Max(v0.X, v1.X), v2.X) < -e.X) return null;
        if (MathF.Min(MathF.Min(v0.Y, v1.Y), v2.Y) >  e.Y) return null;
        if (MathF.Max(MathF.Max(v0.Y, v1.Y), v2.Y) < -e.Y) return null;
        if (MathF.Min(MathF.Min(v0.Z, v1.Z), v2.Z) >  e.Z) return null;
        if (MathF.Max(MathF.Max(v0.Z, v1.Z), v2.Z) < -e.Z) return null;

        // 1 triangle face normal test: box-vs-triangle-plane.
        var triNormalRaw = Vector3.Cross(f0, f1);
        var dPlane = Vector3.Dot(triNormalRaw, v0);
        var rPlane =
            e.X * MathF.Abs(triNormalRaw.X) +
            e.Y * MathF.Abs(triNormalRaw.Y) +
            e.Z * MathF.Abs(triNormalRaw.Z);
        if (MathF.Abs(dPlane) > rPlane) return null;

        // Overlap confirmed by SAT. Compute contact info from the triangle plane:
        // normal is the triangle face normal pointing toward the box centre, depth
        // is how far the box's deepest corner sits beyond that plane.
        var triNormal = Vector3.Normalize(triNormalRaw);
        // Flip normal so it points AWAY from the triangle face into the box-occupied side.
        if (Vector3.Dot(triNormal, -v0) < 0.0f) triNormal = -triNormal;

        // Box corner most "into" the triangle (along -triNormal direction).
        var deepestCorner = new Vector3(
            center.X - MathF.Sign(triNormal.X) * e.X,
            center.Y - MathF.Sign(triNormal.Y) * e.Y,
            center.Z - MathF.Sign(triNormal.Z) * e.Z);

        // Signed distance from corner to triangle plane along triNormal: negative
        // means below the plane (penetrating); depth is |that|.
        var planeD = Vector3.Dot(triNormal, tri.V0);
        var depth = planeD - Vector3.Dot(triNormal, deepestCorner);
        if (depth < 0.0f) depth = 0.0f;

        return new CollisionHit
        {
            Time = 0.0f,
            Point = deepestCorner + triNormal * (depth * 0.5f),
            Normal = triNormal,
            Depth = depth,
        };
    }

    // Project the three triangle vertices onto axis (ax, ay, az), compute the box's
    // projected half-extent, return true if the intervals overlap. The axis doesn't
    // need to be unit length — both projections scale together.
    private static bool AxisTest(float ax, float ay, float az,
                                  Vector3 v0, Vector3 v1, Vector3 v2,
                                  Vector3 boxHalfExtents)
    {
        var p0 = ax * v0.X + ay * v0.Y + az * v0.Z;
        var p1 = ax * v1.X + ay * v1.Y + az * v1.Z;
        var p2 = ax * v2.X + ay * v2.Y + az * v2.Z;
        var min = MathF.Min(p0, MathF.Min(p1, p2));
        var max = MathF.Max(p0, MathF.Max(p1, p2));
        var r =
            boxHalfExtents.X * MathF.Abs(ax) +
            boxHalfExtents.Y * MathF.Abs(ay) +
            boxHalfExtents.Z * MathF.Abs(az);
        return !(min > r || max < -r);
    }

    // Closest point on triangle ABC to point P. Ericson Real-Time Collision
    // Detection §5.1.5 — barycentric region test. Correctly handles vertex, edge,
    // and face regions.
    private static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;

        var d1 = Vector3.Dot(ab, ap);
        var d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0.0f && d2 <= 0.0f) return a;   // vertex region A

        var bp = p - b;
        var d3 = Vector3.Dot(ab, bp);
        var d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0.0f && d4 <= d3) return b;     // vertex region B

        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0.0f && d1 >= 0.0f && d3 <= 0.0f)
        {
            var t = d1 / (d1 - d3);
            return a + ab * t;                    // edge AB
        }

        var cp = p - c;
        var d5 = Vector3.Dot(ab, cp);
        var d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0.0f && d5 <= d6) return c;     // vertex region C

        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0.0f && d2 >= 0.0f && d6 <= 0.0f)
        {
            var t = d2 / (d2 - d6);
            return a + ac * t;                    // edge AC
        }

        var va = d3 * d6 - d5 * d4;
        if (va <= 0.0f && (d4 - d3) >= 0.0f && (d5 - d6) >= 0.0f)
        {
            var t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return b + (c - b) * t;               // edge BC
        }

        // Inside face region: project via barycentric coordinates.
        var denom = 1.0f / (va + vb + vc);
        var v = vb * denom;
        var w = vc * denom;
        return a + ab * v + ac * w;
    }

    // Closest point on line segment AB to P. Standard parametric clamp.
    private static Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var sqLen = ab.LengthSquared();
        if (sqLen < 1e-12f) return a;   // degenerate segment
        var t = Vector3.Dot(p - a, ab) / sqLen;
        t = Math.Clamp(t, 0.0f, 1.0f);
        return a + ab * t;
    }

    // Closest points between two line segments AB and CD. Returns (pointOnAB,
    // pointOnCD). Standard formulation from Ericson, RTCD §5.1.9 -- parallel and
    // co-linear segments fall through the degenerate branches cleanly.
    private static (Vector3 OnAB, Vector3 OnCD) ClosestPointsOnSegments(
        Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        var d1 = b - a;
        var d2 = d - c;
        var r = a - c;
        var aDot = Vector3.Dot(d1, d1);
        var eDot = Vector3.Dot(d2, d2);
        var f = Vector3.Dot(d2, r);

        float s;
        float t;
        const float eps = 1e-12f;

        if (aDot <= eps && eDot <= eps)
        {
            return (a, c);   // both segments degenerate to points
        }
        if (aDot <= eps)
        {
            s = 0.0f;
            t = Math.Clamp(f / eDot, 0.0f, 1.0f);
        }
        else
        {
            var cDot = Vector3.Dot(d1, r);
            if (eDot <= eps)
            {
                t = 0.0f;
                s = Math.Clamp(-cDot / aDot, 0.0f, 1.0f);
            }
            else
            {
                var bDot = Vector3.Dot(d1, d2);
                var denom = aDot * eDot - bDot * bDot;
                s = denom > eps ? Math.Clamp((bDot * f - cDot * eDot) / denom, 0.0f, 1.0f) : 0.0f;
                t = (bDot * s + f) / eDot;
                if (t < 0.0f)
                {
                    t = 0.0f;
                    s = Math.Clamp(-cDot / aDot, 0.0f, 1.0f);
                }
                else if (t > 1.0f)
                {
                    t = 1.0f;
                    s = Math.Clamp((bDot - cDot) / aDot, 0.0f, 1.0f);
                }
            }
        }
        return (a + d1 * s, c + d2 * t);
    }

    // -- Sweep --------------------------------------------------------------------
    // Each Sweep takes the displacement of each shape over a single timestep and
    // returns CollisionHit? whose Time is the fraction of the step at which contact
    // first occurs ([0,1] inclusive). Already-overlapping shapes return Time = 0 and
    // a discrete-style Depth so callers can depenetrate before sweeping the next
    // frame; non-overlapping shapes that never touch during the step return null.

    public static CollisionHit? Sweep(
        BoundingSphere a, Vector3 motionA,
        BoundingSphere b, Vector3 motionB)
    {
        // Work in B's frame: A moves by `relMotion`, B is stationary. Find first t
        // where distance(centres) shrinks to (rA + rB).
        var relMotion = motionA - motionB;
        var startDelta = a.Center - b.Center;
        var rSum = a.Radius + b.Radius;

        // Already overlapping at t=0: hand back a discrete-style hit.
        var startDistSq = startDelta.LengthSquared();
        if (startDistSq <= rSum * rSum)
        {
            return Test(a, b);
        }

        // |startDelta + t·relMotion|² = rSum²  =>  at² + bt + c = 0
        // where a = relMotion·relMotion, b = 2·startDelta·relMotion,
        //       c = startDelta·startDelta − rSum²
        var aCoef = Vector3.Dot(relMotion, relMotion);
        if (aCoef <= 1e-12f)
        {
            // No relative motion and not already overlapping: they never meet.
            return null;
        }
        var bCoef = 2.0f * Vector3.Dot(startDelta, relMotion);
        var cCoef = startDistSq - rSum * rSum;

        var disc = bCoef * bCoef - 4.0f * aCoef * cCoef;
        if (disc < 0.0f) return null;

        var sqrtDisc = MathF.Sqrt(disc);
        var t = (-bCoef - sqrtDisc) / (2.0f * aCoef);   // smaller root = first contact
        if (t < 0.0f || t > 1.0f) return null;

        // Contact: place A and B at their TOI positions and form the normal from
        // B-into-A as for the discrete case.
        var posAAt = a.Center + motionA * t;
        var posBAt = b.Center + motionB * t;
        var normal = Vector3.Normalize(posAAt - posBAt);
        return new CollisionHit
        {
            Time = t,
            Point = posBAt + normal * b.Radius,
            Normal = normal,
            Depth = 0.0f,
        };
    }

    public static CollisionHit? Sweep(
        BoundingSphere sphere, Vector3 sphereMotion,
        Bounds3 aabb, Vector3 aabbMotion)
    {
        // Reduce to ray (sphere centre moving by relMotion) versus AABB inflated by
        // the sphere's radius on each axis. Conservative at edges/corners — a sphere
        // brushing the rounded corner of the Minkowski sum may be reported slightly
        // earlier than reality. Acceptable for the demo's slow-moving content; a
        // proper Minkowski-rounded-box implementation lands when CCD edge cases bite.
        var relMotion = sphereMotion - aabbMotion;

        var inflated = new Bounds3(
            aabb.Min - new Vector3(sphere.Radius),
            aabb.Max + new Vector3(sphere.Radius));

        // Already overlapping (sphere centre inside the inflated AABB): defer to the
        // discrete test for a stable hit.
        if (sphere.Center.X >= inflated.Min.X && sphere.Center.X <= inflated.Max.X &&
            sphere.Center.Y >= inflated.Min.Y && sphere.Center.Y <= inflated.Max.Y &&
            sphere.Center.Z >= inflated.Min.Z && sphere.Center.Z <= inflated.Max.Z)
        {
            return Test(sphere, aabb);
        }

        if (!SlabRay(sphere.Center, relMotion, inflated, out var tEnter, out var enterAxis, out var enterSign))
        {
            return null;
        }
        if (tEnter < 0.0f || tEnter > 1.0f) return null;

        var centreAtHit = sphere.Center + sphereMotion * tEnter;
        var normal = enterAxis switch
        {
            0 => new Vector3(enterSign, 0.0f, 0.0f),
            1 => new Vector3(0.0f, enterSign, 0.0f),
            _ => new Vector3(0.0f, 0.0f, enterSign),
        };
        var aabbAtHit = new Bounds3(aabb.Min + aabbMotion * tEnter, aabb.Max + aabbMotion * tEnter);
        var contactPoint = Vector3.Clamp(centreAtHit, aabbAtHit.Min, aabbAtHit.Max);
        return new CollisionHit
        {
            Time = tEnter,
            Point = contactPoint,
            Normal = normal,
            Depth = 0.0f,
        };
    }

    public static CollisionHit? Sweep(
        Bounds3 a, Vector3 motionA,
        Bounds3 b, Vector3 motionB)
    {
        // Already overlapping: discrete hit.
        if (Test(a, b) is { } overlap) return overlap;

        // Work in B's frame: A moves by relMotion. For each axis, compute the time
        // interval [enter, exit] during which the projections overlap. Intersect the
        // three intervals; if non-empty in [0, 1], the entry is TOI and the axis with
        // the latest entry gives the contact normal.
        var relMotion = motionA - motionB;

        var tEnter = 0.0f;
        var tExit = 1.0f;
        var enterAxis = 0;
        var enterSign = 1.0f;

        for (var axis = 0; axis < 3; axis++)
        {
            var aMin = axis == 0 ? a.Min.X : axis == 1 ? a.Min.Y : a.Min.Z;
            var aMax = axis == 0 ? a.Max.X : axis == 1 ? a.Max.Y : a.Max.Z;
            var bMin = axis == 0 ? b.Min.X : axis == 1 ? b.Min.Y : b.Min.Z;
            var bMax = axis == 0 ? b.Max.X : axis == 1 ? b.Max.Y : b.Max.Z;
            var v = axis == 0 ? relMotion.X : axis == 1 ? relMotion.Y : relMotion.Z;

            if (MathF.Abs(v) < 1e-12f)
            {
                // No relative motion on this axis; if they aren't already projection-
                // overlapping here, they never will be.
                if (aMax < bMin || aMin > bMax) return null;
                continue;
            }

            // Time at which A's projection enters B's: solve a + t·v == b boundary.
            float axisEnter, axisExit;
            float entrySign;
            if (v > 0.0f)
            {
                axisEnter = (bMin - aMax) / v;   // A's max reaches B's min
                axisExit  = (bMax - aMin) / v;   // A's min passes B's max
                entrySign = -1.0f;               // contact normal points back toward A
            }
            else
            {
                axisEnter = (bMax - aMin) / v;
                axisExit  = (bMin - aMax) / v;
                entrySign = 1.0f;
            }

            if (axisEnter > tEnter)
            {
                tEnter = axisEnter;
                enterAxis = axis;
                enterSign = entrySign;
            }
            if (axisExit < tExit) tExit = axisExit;
            if (tEnter > tExit) return null;
        }

        if (tEnter < 0.0f || tEnter > 1.0f) return null;

        var normal = enterAxis switch
        {
            0 => new Vector3(enterSign, 0.0f, 0.0f),
            1 => new Vector3(0.0f, enterSign, 0.0f),
            _ => new Vector3(0.0f, 0.0f, enterSign),
        };
        var aAtHit = new Bounds3(a.Min + motionA * tEnter, a.Max + motionA * tEnter);
        var bAtHit = new Bounds3(b.Min + motionB * tEnter, b.Max + motionB * tEnter);
        var contactMin = Vector3.Max(aAtHit.Min, bAtHit.Min);
        var contactMax = Vector3.Min(aAtHit.Max, bAtHit.Max);
        return new CollisionHit
        {
            Time = tEnter,
            Point = (contactMin + contactMax) * 0.5f,
            Normal = normal,
            Depth = 0.0f,
        };
    }

    // Plane sweeps. Planes are stationary by convention — they're infinite static
    // surfaces. If a moving plane is ever needed, add an overload then; for now the
    // shape argument list stays clean.

    public static CollisionHit? Sweep(BoundingSphere sphere, Vector3 sphereMotion, Plane plane)
    {
        // Distance from sphere centre to plane, minus radius, at start and end of the
        // step. If startDist < 0 the sphere is already overlapping → defer to discrete.
        // If endDist >= 0 they never touch during the step. Otherwise linearly
        // interpolate for the contact time.
        var startCentreDist = plane.SignedDistance(sphere.Center);
        if (startCentreDist < sphere.Radius)
        {
            return Test(sphere, plane);
        }
        var endCentreDist = plane.SignedDistance(sphere.Center + sphereMotion);
        if (endCentreDist >= sphere.Radius)
        {
            return null;
        }
        // SignedDistance is linear in position, so we can interpolate the "centre
        // distance shrinks to Radius" crossing directly.
        var t = (startCentreDist - sphere.Radius) / (startCentreDist - endCentreDist);
        if (t < 0.0f || t > 1.0f) return null;
        var centreAtHit = sphere.Center + sphereMotion * t;
        return new CollisionHit
        {
            Time = t,
            Point = centreAtHit - plane.Normal * sphere.Radius,
            Normal = plane.Normal,
            Depth = 0.0f,
        };
    }

    public static CollisionHit? Sweep(Bounds3 aabb, Vector3 aabbMotion, Plane plane)
    {
        // Same shape as sphere-vs-plane: signed distance of the deepest AABB point
        // is linear in the AABB's position (the box doesn't rotate during a sweep),
        // so a single linear interpolation gives the touch-time.
        var halfExtents = aabb.Size * 0.5f;
        var radius =
            halfExtents.X * MathF.Abs(plane.Normal.X) +
            halfExtents.Y * MathF.Abs(plane.Normal.Y) +
            halfExtents.Z * MathF.Abs(plane.Normal.Z);
        var startCentreDist = plane.SignedDistance(aabb.Center);
        if (startCentreDist - radius < 0.0f)
        {
            return Test(aabb, plane);
        }
        var endCentreDist = plane.SignedDistance(aabb.Center + aabbMotion);
        if (endCentreDist - radius >= 0.0f)
        {
            return null;
        }
        var t = (startCentreDist - radius) / (startCentreDist - endCentreDist);
        if (t < 0.0f || t > 1.0f) return null;
        var centreAtHit = aabb.Center + aabbMotion * t;
        var deepest = new Vector3(
            centreAtHit.X - MathF.Sign(plane.Normal.X) * halfExtents.X,
            centreAtHit.Y - MathF.Sign(plane.Normal.Y) * halfExtents.Y,
            centreAtHit.Z - MathF.Sign(plane.Normal.Z) * halfExtents.Z);
        return new CollisionHit
        {
            Time = t,
            Point = deepest,
            Normal = plane.Normal,
            Depth = 0.0f,
        };
    }

    // -- Raycast ------------------------------------------------------------------
    // Cast a ray and return the closest hit within [0, maxDistance]. CollisionHit.Time
    // here is the parametric distance along the ray (point = ray.Origin + ray.Direction
    // * Time), departing from the [0, 1] convention sweep tests use — rays have no
    // natural "step length" so an absolute distance is more useful for picking,
    // line-of-sight, and similar consumers. Pass maxDistance = float.PositiveInfinity
    // (the default) for unbounded rays. Ray.Direction is expected to be unit length.

    // -- Swept capsule ------------------------------------------------------------
    //
    // The character-controller primitive, and the one this whole family did not have: before the
    // character arc there was no capsule sweep of any kind and Sweep had ZERO callers outside this
    // file. Everything above sweeps spheres and boxes against planes and boxes, which is what a
    // projectile wants; a body that walks needs a capsule against a triangle mesh.

    /// <summary>
    /// A capsule moving by <paramref name="motion"/> against an infinite plane. Exact.
    /// </summary>
    /// <remarks>
    /// Both endpoints translate at the same rate, so the capsule's nearest approach to the plane is
    /// a linear function of time and there is nothing to iterate: the contact is one division. This
    /// exists as much to be the sweep against a TRIANGLE's reference as for its own sake — the
    /// triangle sweep converges on an answer, and the only honest way to say how close it gets is to
    /// point it at a case whose answer is known in closed form.
    /// </remarks>
    public static CollisionHit? Sweep(Capsule capsule, Vector3 motion, Plane plane)
    {
        // Already touching: a discrete hit at Time 0, which is the convention CollisionHit states.
        if (Test(capsule, plane) is { } overlap) return overlap;

        var rate = Vector3.Dot(motion, plane.Normal);

        // Receding or travelling parallel to the plane. Not "no contact ever" — no contact during
        // THIS step, which is what a sweep is asked about.
        if (rate >= -1e-9f) return null;

        var nearest = MathF.Min(plane.SignedDistance(capsule.PointA), plane.SignedDistance(capsule.PointB));
        var time = (capsule.Radius - nearest) / rate;
        if (time < 0f || time > 1f) return null;

        var lowest = plane.SignedDistance(capsule.PointA) <= plane.SignedDistance(capsule.PointB)
            ? capsule.PointA
            : capsule.PointB;
        var touch = lowest + (motion * time);

        return new CollisionHit
        {
            Time = time,
            Point = touch - (plane.Normal * capsule.Radius),
            Normal = plane.Normal,
            Depth = 0f,
        };
    }

    /// <summary>
    /// A capsule moving by <paramref name="motion"/> against one triangle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Conservative advancement, not a closed form.</b> The exact time of impact between a moving
    /// capsule and a triangle is a root of a piecewise system — the closest FEATURES change as the
    /// capsule slides along, so each pairing has its own polynomial and the answer is the earliest
    /// root of whichever is active at the time. Engines that do it analytically decompose into
    /// sphere-vs-face, three swept-cylinder edge tests and three swept-sphere vertex tests, and the
    /// edge cases between those regions are where they get it wrong.
    /// </para>
    /// <para>
    /// Advancement instead: measure the gap, step forward by the most the capsule could travel
    /// without closing it, measure again. Each step is exact and the sequence only ever
    /// UNDERSHOOTS, so the answer is always at or before the true contact. For a body that walks,
    /// erring early means stopping a hair short of a wall; erring late means standing inside it.
    /// </para>
    /// <para>
    /// <b>It cannot tunnel</b>, which is the property that matters and the one a discrete test
    /// cannot offer at any speed. The iteration cap is a budget, not a correctness condition: run
    /// out of iterations and the time returned is still a time before contact, so the body stops
    /// short rather than passing through. That is why exhaustion reports a hit rather than a miss.
    /// </para>
    /// </remarks>
    public static CollisionHit? Sweep(Capsule capsule, Vector3 motion, Triangle tri, float tolerance = 1e-4f)
    {
        var speed = motion.Length();

        // A capsule that is not moving cannot have a time of impact; the question collapses to the
        // discrete one, and answering it here rather than dividing by zero below.
        if (speed < 1e-9f) return TestCapsuleTriangle(capsule, tri);

        var time = 0f;
        for (var iteration = 0; iteration < MaxSweepIterations; iteration++)
        {
            var moved = new Capsule(
                capsule.PointA + (motion * time),
                capsule.PointB + (motion * time),
                capsule.Radius);

            var (distanceSquared, onSegment, onTriangle) = ClosestPair(moved, tri);
            var gap = MathF.Sqrt(distanceSquared) - capsule.Radius;

            if (gap <= tolerance)
            {
                var separation = onSegment - onTriangle;
                var length = separation.Length();

                // At a grazing contact the closest pair has all but collapsed, so the direction
                // between them is noise. The outward face normal is the stable answer — the same
                // choice the discrete test makes when a segment lies in the plane, and for the same
                // reason: a winding says which side is outside, and a body arriving from outside a
                // closed solid wants to be pushed back the way it came.
                var normal = length > 1e-6f
                    ? separation / length
                    : OutwardNormal(tri);

                // <b>A surface you are not moving INTO does not obstruct you.</b> A body resting on
                // the floor is in contact with it at time zero for ever, so without this a sweep
                // answers "the floor, now" to every horizontal step — and a resolver spends its
                // whole iteration budget deflecting a motion that was already tangential, arriving
                // nowhere. That is exactly how it failed: a walk of 200 steps that never moved, and
                // a graze along a wall that travelled 0.000 m.
                //
                // The rule is the one CollisionResponse already applies to velocity ("when it's
                // already moving away, no change"), applied one layer earlier to the query. At a
                // contact found part-way through a step the motion is approaching by construction,
                // so this only ever fires on a contact the body is already in.
                //
                // Overlap is a different question and is not filtered here: a body INSIDE something
                // has to be pushed out whichever way it happens to be moving, and that is what the
                // discrete test is for — a resolver depenetrates before it sweeps.
                if (Vector3.Dot(motion, normal) >= -1e-5f * speed) return null;

                return new CollisionHit
                {
                    Time = time,
                    Point = onTriangle,
                    Normal = normal,
                    Depth = 0f,
                };
            }

            // The most the capsule can advance without any part of it closing the gap. Exact,
            // because no point of a rigid body moves faster than the body does.
            time += gap / speed;
            if (time > 1f) return null;
        }

        // Out of budget while still separated. The time is a lower bound on contact, so reporting it
        // stops the body short of the surface rather than letting it continue into one.
        return new CollisionHit
        {
            Time = MathF.Min(time, 1f),
            Point = capsule.PointA + (motion * time),
            Normal = OutwardNormal(tri),
            Depth = 0f,
        };
    }

    /// <summary>
    /// A capsule moving by <paramref name="motion"/> against a whole mesh. The EARLIEST contact.
    /// </summary>
    /// <remarks>
    /// Earliest rather than deepest, which is the opposite of what the discrete
    /// <see cref="Test(Capsule, TriangleMesh3D)"/> reports — and correctly so. A discrete test is
    /// asked "how far in am I", where the worst overlap is the one to resolve; a sweep is asked
    /// "what do I hit first", where anything after the first contact is a question about a step the
    /// body will not get to take.
    /// <para>
    /// The broad phase is the swept AABB: the capsule's bounds unioned with the same bounds at the
    /// end of the motion. No BVH — this tree has never measured n² hurting, and the room the lab
    /// sweeps against is 748 triangles. When something measures it, the internals change and this
    /// signature does not.
    /// </para>
    /// </remarks>
    public static CollisionHit? Sweep(Capsule capsule, Vector3 motion, TriangleMesh3D mesh, float tolerance = 1e-4f)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var start = capsule.Bounds;
        var swept = new Bounds3(
            Vector3.Min(start.Min, start.Min + motion),
            Vector3.Max(start.Max, start.Max + motion));

        if (Test(swept, mesh.Bounds) is null) return null;

        CollisionHit? earliest = null;
        foreach (var tri in mesh.Triangles)
        {
            var triMin = Vector3.Min(tri.V0, Vector3.Min(tri.V1, tri.V2));
            var triMax = Vector3.Max(tri.V0, Vector3.Max(tri.V1, tri.V2));
            if (Test(swept, new Bounds3(triMin, triMax)) is null) continue;

            if (Sweep(capsule, motion, tri, tolerance) is not { } hit) continue;
            if (earliest is null || hit.Time < earliest.Value.Time) earliest = hit;

            // A contact at the very start of the step is as early as anything can be; nothing later
            // in the mesh can beat it, and an overlapping start wants resolving before a sweep means
            // anything anyway.
            if (earliest.Value.Time <= 0f) break;
        }

        return earliest;
    }

    /// <summary>
    /// How many times a sweep may measure and advance before it gives up and stops short.
    /// </summary>
    /// <remarks>
    /// Advancement converges geometrically away from tangency and slowly along it — a capsule
    /// skimming a surface at a shallow angle closes the gap by a little each step. 32 is where the
    /// room's own cases land well inside the budget; it is a cost ceiling rather than an accuracy
    /// claim, and the accuracy claim is made by the plane sweep it is checked against.
    /// </remarks>
    private const int MaxSweepIterations = 32;

    /// <summary>The triangle's outward normal, or up for a degenerate one.</summary>
    /// <remarks>
    /// Degenerate triangles have no normal at all, and a collider full of them reports contacts with
    /// NaN normals — which propagate into a position and end a run somewhere no number can describe.
    /// Up is arbitrary and finite, which is the property that matters.
    /// </remarks>
    private static Vector3 OutwardNormal(Triangle tri)
    {
        var raw = tri.NormalRaw;
        var lengthSquared = raw.LengthSquared();
        return lengthSquared < 1e-18f ? Vector3.UnitY : raw / MathF.Sqrt(lengthSquared);
    }

    public static CollisionHit? Raycast(Ray ray, BoundingSphere sphere, float maxDistance = float.PositiveInfinity)
    {
        // Sphere-ray quadratic: |origin + t*dir - centre|² = radius²
        var delta = ray.Origin - sphere.Center;
        var b = Vector3.Dot(delta, ray.Direction);
        var c = Vector3.Dot(delta, delta) - sphere.Radius * sphere.Radius;
        // Origin inside sphere (c < 0): tFront is negative; the ray exits through
        // tBack > 0. Treat the ray as hitting "at t=0" with the surface normal still
        // pointing outward — useful for picking when the camera is inside the volume.
        if (c < 0.0f)
        {
            return new CollisionHit
            {
                Time = 0.0f,
                Point = ray.Origin,
                Normal = Vector3.Normalize(-delta),
                Depth = 0.0f,
            };
        }
        var disc = b * b - c;
        if (disc < 0.0f) return null;
        var t = -b - MathF.Sqrt(disc);
        if (t < 0.0f || t > maxDistance) return null;
        var hitPoint = ray.Origin + ray.Direction * t;
        return new CollisionHit
        {
            Time = t,
            Point = hitPoint,
            Normal = Vector3.Normalize(hitPoint - sphere.Center),
            Depth = 0.0f,
        };
    }

    public static CollisionHit? Raycast(Ray ray, Bounds3 aabb, float maxDistance = float.PositiveInfinity)
    {
        if (!SlabRay(ray.Origin, ray.Direction, aabb, out var tEnter, out var enterAxis, out var enterSign))
        {
            return null;
        }
        // Ray origin inside the AABB: SlabRay reports tEnter < 0. Clamp to 0 and
        // report the entry point as the ray origin — same "you're already inside"
        // convention as the sphere raycast.
        if (tEnter < 0.0f) tEnter = 0.0f;
        if (tEnter > maxDistance) return null;
        var normal = enterAxis switch
        {
            0 => new Vector3(enterSign, 0.0f, 0.0f),
            1 => new Vector3(0.0f, enterSign, 0.0f),
            _ => new Vector3(0.0f, 0.0f, enterSign),
        };
        return new CollisionHit
        {
            Time = tEnter,
            Point = ray.Origin + ray.Direction * tEnter,
            Normal = normal,
            Depth = 0.0f,
        };
    }

    public static CollisionHit? Raycast(Ray ray, Plane plane, float maxDistance = float.PositiveInfinity)
    {
        // Solve plane.Normal · (origin + t·dir) = plane.Offset for t.
        var denom = Vector3.Dot(plane.Normal, ray.Direction);
        if (MathF.Abs(denom) < 1e-6f)
        {
            // Ray parallel to plane: never crosses unless already on it. The "ray
            // lies on the plane" case is degenerate enough to treat as no-hit.
            return null;
        }
        var t = (plane.Offset - Vector3.Dot(plane.Normal, ray.Origin)) / denom;
        if (t < 0.0f || t > maxDistance) return null;
        // The hit normal is plane.Normal when the ray hits from the +N side, else
        // flip it so the normal always faces the ray (picking convention).
        var hitNormal = denom < 0.0f ? plane.Normal : -plane.Normal;
        return new CollisionHit
        {
            Time = t,
            Point = ray.Origin + ray.Direction * t,
            Normal = hitNormal,
            Depth = 0.0f,
        };
    }

    public static CollisionHit? Raycast(Ray ray, TriangleMesh3D mesh, float maxDistance = float.PositiveInfinity)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        // Mesh-AABB early-out: if the ray doesn't enter the bounding box at all, no
        // triangle can be hit. Cheap rejection before the per-triangle inner loop.
        if (Raycast(ray, mesh.Bounds, maxDistance) is null) return null;

        CollisionHit? closest = null;
        var closestT = maxDistance;

        foreach (var tri in mesh.Triangles)
        {
            // Möller-Trumbore: solve for (t, u, v) such that
            //   ray.Origin + t·ray.Direction = (1-u-v)·V0 + u·V1 + v·V2
            // with u >= 0, v >= 0, u + v <= 1.
            var edge1 = tri.V1 - tri.V0;
            var edge2 = tri.V2 - tri.V0;
            var h = Vector3.Cross(ray.Direction, edge2);
            var a = Vector3.Dot(edge1, h);
            // a near zero means the ray is parallel to the triangle plane — skip
            // rather than divide and produce noisy hits.
            if (MathF.Abs(a) < 1e-8f) continue;
            var f = 1.0f / a;
            var s = ray.Origin - tri.V0;
            var u = f * Vector3.Dot(s, h);
            if (u < 0.0f || u > 1.0f) continue;
            var q = Vector3.Cross(s, edge1);
            var v = f * Vector3.Dot(ray.Direction, q);
            if (v < 0.0f || u + v > 1.0f) continue;
            var t = f * Vector3.Dot(edge2, q);
            if (t < 0.0f || t > closestT) continue;

            // Two-sided hit: the geometric normal `tri.Normal` faces one side; if the
            // ray approached from the other side, flip the reported normal so it
            // points back toward the ray (picking convention).
            var normal = tri.Normal;
            if (Vector3.Dot(normal, ray.Direction) > 0.0f) normal = -normal;

            closestT = t;
            closest = new CollisionHit
            {
                Time = t,
                Point = ray.Origin + ray.Direction * t,
                Normal = normal,
                Depth = 0.0f,
            };
        }

        return closest;
    }

    public static CollisionHit? Raycast(Ray ray, Capsule capsule, float maxDistance = float.PositiveInfinity)
    {
        // Capsule decomposes into infinite-cylinder (the swept midsection) + two
        // hemispherical end caps. Cast against each, take the closest valid hit.
        //
        // Cylinder math: Ericson RTCD §5.3.7. The cylinder is parameterised by
        // axis vector `d = PointB - PointA` and radius. Side hits are the smaller
        // root of a quadratic in t whose roots straddle the cylinder surface;
        // we then verify the hit point projects inside the segment (0 <= u <= 1).
        // End-cap hits handled by separate Ray-vs-Sphere calls at each endpoint.
        var axis = capsule.PointB - capsule.PointA;
        var axisLenSq = axis.LengthSquared();
        if (axisLenSq < 1e-12f)
        {
            // Degenerate capsule -- treat as a sphere centred at PointA.
            return Raycast(ray, new BoundingSphere(capsule.PointA, capsule.Radius), maxDistance);
        }

        // The two end-cap sphere tests cover the rounded ends. Run unconditionally
        // and merge with the cylinder test below.
        var capA = Raycast(ray, new BoundingSphere(capsule.PointA, capsule.Radius), maxDistance);
        var capB = Raycast(ray, new BoundingSphere(capsule.PointB, capsule.Radius), maxDistance);

        var bestT = maxDistance;
        CollisionHit? best = null;
        if (capA is { } a && a.Time <= bestT)
        {
            bestT = a.Time;
            best = a;
        }
        if (capB is { } b && b.Time <= bestT)
        {
            bestT = b.Time;
            best = b;
        }

        // Side (cylinder) test. Ericson formulation: solve quadratic aq*t^2 +
        // 2*bq*t + c = 0 where the coefficients depend on the ray-axis geometry.
        var m = ray.Origin - capsule.PointA;
        var md = Vector3.Dot(m, axis);
        var nd = Vector3.Dot(ray.Direction, axis);
        var aq = axisLenSq - nd * nd;
        if (MathF.Abs(aq) >= 1e-8f)
        {
            var nm = Vector3.Dot(ray.Direction, m);
            var bq = axisLenSq * nm - nd * md;
            var ck = Vector3.Dot(m, m) - capsule.Radius * capsule.Radius;
            var c = axisLenSq * ck - md * md;
            var disc = bq * bq - aq * c;
            if (disc >= 0.0f)
            {
                var t = (-bq - MathF.Sqrt(disc)) / aq;
                if (t >= 0.0f && t < bestT)
                {
                    var u = md + t * nd;
                    // u in [0, axisLenSq] means the cylinder hit projects inside
                    // the segment -- it's the side of the capsule, not the cap.
                    // (Note: u is scaled by axisLenSq since we never divided.)
                    if (u >= 0.0f && u <= axisLenSq)
                    {
                        var hitPoint = ray.Origin + ray.Direction * t;
                        // Normal is radially outward from the segment axis.
                        var axisPoint = capsule.PointA + axis * (u / axisLenSq);
                        var normal = Vector3.Normalize(hitPoint - axisPoint);
                        bestT = t;
                        best = new CollisionHit
                        {
                            Time = t,
                            Point = hitPoint,
                            Normal = normal,
                            Depth = 0.0f,
                        };
                    }
                }
            }
        }

        return best;
    }

    public static CollisionHit? Raycast(Ray ray, OrientedBounds3 obb, float maxDistance = float.PositiveInfinity)
    {
        // Transform ray into OBB-local frame, run the existing Ray-AABB at the
        // origin with the OBB's half-extents, then transform the hit point /
        // normal back to world space. Cheaper than ray-OBB SAT and reuses the
        // tuned slab math.
        var invRot = Quaternion.Inverse(obb.Orientation);
        var localOrigin = Vector3.Transform(ray.Origin - obb.Center, invRot);
        var localDir = Vector3.Transform(ray.Direction, invRot);
        var aabbAtOrigin = new Bounds3(-obb.HalfExtents, obb.HalfExtents);
        var hit = Raycast(new Ray(localOrigin, localDir), aabbAtOrigin, maxDistance);
        if (hit is null) return null;
        var localHit = hit.Value;
        return new CollisionHit
        {
            Time = localHit.Time,
            Point = obb.Center + Vector3.Transform(localHit.Point, obb.Orientation),
            Normal = Vector3.Transform(localHit.Normal, obb.Orientation),
            Depth = 0.0f,
        };
    }

    // Ray vs AABB slab test: returns the entry time along the ray (clamped to t >= 0
    // not enforced — callers test). Used by sphere-AABB sweep above.
    private static bool SlabRay(
        Vector3 origin, Vector3 direction, Bounds3 box,
        out float tEnter, out int enterAxis, out float enterSign)
    {
        tEnter = float.NegativeInfinity;
        var tExit = float.PositiveInfinity;
        enterAxis = 0;
        enterSign = 1.0f;

        for (var axis = 0; axis < 3; axis++)
        {
            var origAxis = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            var dirAxis = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            var minAxis = axis == 0 ? box.Min.X : axis == 1 ? box.Min.Y : box.Min.Z;
            var maxAxis = axis == 0 ? box.Max.X : axis == 1 ? box.Max.Y : box.Max.Z;

            if (MathF.Abs(dirAxis) < 1e-12f)
            {
                if (origAxis < minAxis || origAxis > maxAxis) return false;
                continue;
            }

            var inv = 1.0f / dirAxis;
            var t1 = (minAxis - origAxis) * inv;
            var t2 = (maxAxis - origAxis) * inv;
            float tNear, tFar, sign;
            if (t1 < t2)
            {
                tNear = t1; tFar = t2; sign = -1.0f;   // entered from the minus side
            }
            else
            {
                tNear = t2; tFar = t1; sign = 1.0f;
            }

            if (tNear > tEnter)
            {
                tEnter = tNear;
                enterAxis = axis;
                enterSign = sign;
            }
            if (tFar < tExit) tExit = tFar;
            if (tEnter > tExit) return false;
        }

        return true;
    }
}
