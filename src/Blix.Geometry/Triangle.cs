using System.Numerics;

namespace Blix.Geometry;

// Three-vertex face — the atomic primitive of triangle-mesh colliders. Vertex order
// determines the front face: counter-clockwise winding when viewed from +Normal,
// matching the convention `Normal = normalize(cross(V1 - V0, V2 - V0))`. Raycasts
// against a triangle hit either side; AABB/sphere-vs-triangle tests don't care about
// winding either, but consumers that need a stable outward normal (e.g. for response
// math) rely on it.
public readonly record struct Triangle(Vector3 V0, Vector3 V1, Vector3 V2)
{
    // Unnormalised geometric normal — the cross product of the two edges sharing V0.
    // Length is twice the triangle's area, which some intersection tests need
    // alongside the direction.
    public Vector3 NormalRaw => Vector3.Cross(V1 - V0, V2 - V0);

    public Vector3 Normal => Vector3.Normalize(NormalRaw);

    public Vector3 Centroid => (V0 + V1 + V2) * (1.0f / 3.0f);
}
