using System.Numerics;

namespace Blix.Geometry;

// 2D analogue of CollisionHit. Same field semantics:
//   - Time in [0, 1] for sweep tests (fraction of step at contact); 0 for discrete.
//   - Normal points from B into A: apply Normal * Depth to A to depenetrate.
//   - Depth is positive overlap for discrete; 0 for sweep where shapes only just touch.
public readonly record struct CollisionHit2D
{
    public float Time { get; init; }

    public Vector2 Point { get; init; }

    public Vector2 Normal { get; init; }

    public float Depth { get; init; }
}
