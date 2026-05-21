namespace Blix;

// "What kinds of things do I care about" for collision queries. Distinct from
// CollisionLayer to keep the semantics clear at call sites: a collider has a layer,
// a query has a mask, and a query matches a collider when their bits overlap.
//
// Default for queries that don't specify a mask is `All` — matches every layer.
// Keeps existing query call sites behaving as before, no opt-in needed.
public readonly record struct CollisionMask(uint Bits)
{
    // Matches every layer. Default for queries that don't filter.
    public static CollisionMask All => new(uint.MaxValue);

    // Matches no layer. Useful as a sentinel; query against `None` returns nothing.
    public static CollisionMask None => new(0u);

    // Build a mask that matches exactly one layer.
    public static CollisionMask FromLayer(CollisionLayer layer) => new(layer.Bits);

    // Build a mask that matches any of several layers — bitwise OR of their bits.
    public static CollisionMask FromLayers(params CollisionLayer[] layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var bits = 0u;
        foreach (var layer in layers) bits |= layer.Bits;
        return new CollisionMask(bits);
    }

    // True when the layer has any bit set that the mask also has set.
    public bool Matches(CollisionLayer layer) => (Bits & layer.Bits) != 0u;
}
