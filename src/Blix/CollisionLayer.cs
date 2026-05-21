namespace Blix;

// "What kind of thing am I" for collision filtering. Each collider in a
// CollisionWorld3D carries a layer; queries filter by CollisionMask. Conventionally
// a single bit (one layer per collider), but the type doesn't enforce that — a
// collider can sit in multiple layers if a game wants overlapping categories.
//
// Bit 0 (CollisionLayer.Default) is what colliders get when no layer is specified
// — keeps existing Add calls behaving as before, no opt-in required.
public readonly record struct CollisionLayer(uint Bits)
{
    // Default for colliders added without an explicit layer: bit 0 set. Anything
    // matched by CollisionMask.All (the default mask) — i.e., the existing "every
    // query sees everything" behaviour pre-layers.
    public static CollisionLayer Default => new(1u);

    // Pick the bit at `index` (0..31). Typical use: `CollisionLayer.FromIndex(2)`
    // for "the third layer." Throws if index is out of range.
    public static CollisionLayer FromIndex(int index)
    {
        if (index < 0 || index >= 32)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Layer index must be in [0, 32).");
        }
        return new CollisionLayer(1u << index);
    }
}
