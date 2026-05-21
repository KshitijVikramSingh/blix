using Blix.Geometry;

namespace Blix;

// Owner-tagged collision hit. Returned by CollisionWorld3D queries so the caller can
// route the response back to whichever game object owns the collider that was hit.
// `Hit` is the bare geometric result; `Owner` is whatever T the world is parameterised
// over (typically GameObject in the demo, but the world is generic so any tag works).
// `Layer` is the collider's CollisionLayer — callers can branch on "what kind of thing
// did I hit" without going back to the owner to inspect.
public readonly record struct CollisionContact3D<T>(T Owner, CollisionLayer Layer, CollisionHit Hit);
