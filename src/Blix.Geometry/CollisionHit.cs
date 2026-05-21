using System.Numerics;

namespace Blix.Geometry;

// Shared result type for discrete and sweep intersection tests. Designed up front to
// hold both — when sweep tests land in Phase 2 they reuse this struct, not a parallel
// type, so call sites don't fork on test variant.
//
// Conventions:
// - Time in [0, 1] for sweep tests (fraction of the step at which contact occurs).
//   Always 0 for discrete tests (the shapes are already in contact "now").
// - Normal points from B into A: the direction to push A along to separate it from B.
//   For sphere-sphere this is the unit vector from B.Center to A.Center.
// - Depth is the positive overlap distance for discrete tests; 0 for sweep tests where
//   the shapes only just touch at Time. Lets discrete depenetration and sweep slide-
//   along-surface use the same field.
public readonly record struct CollisionHit
{
    public float Time { get; init; }

    public Vector3 Point { get; init; }

    public Vector3 Normal { get; init; }

    public float Depth { get; init; }
}
