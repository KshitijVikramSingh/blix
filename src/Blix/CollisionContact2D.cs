using Blix.Geometry;

namespace Blix;

// 2D analogue of CollisionContact3D<T>. Same shape — owner-tag + collision layer +
// geometric hit. Uses CollisionHit2D so the contact carries Vector2 positions and
// normals matching the 2D collision pipeline. CollisionLayer / CollisionMask are
// shared with the 3D path (single bit-set system covers both dimensions).
public readonly record struct CollisionContact2D<T>(T Owner, CollisionLayer Layer, CollisionHit2D Hit);
