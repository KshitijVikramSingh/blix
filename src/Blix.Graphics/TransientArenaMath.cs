namespace Blix.Graphics;

// Pure, allocation-free math for the per-frame transient vertex arena. Lives in
// Blix.Graphics (not the Vulkan backend) so the production bump/ring logic is the
// SAME code the CPU test harness exercises — no device required to prove it.
public static class TransientArenaMath
{
    // Round a byte offset up to the next multiple of stride so a sub-slice's first
    // vertex sits on a vertex boundary. The arena binds the buffer at this offset
    // and draws base-0 indices (firstVertex = 0), so a misaligned offset would make
    // the index fetch read straddled bytes — alignment is a correctness invariant,
    // not an optimization.
    public static int AlignUp(int offset, int stride)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (stride <= 0) throw new ArgumentOutOfRangeException(nameof(stride));
        var rem = offset % stride;
        return rem == 0 ? offset : offset + (stride - rem);
    }

    // Ring slot for a monotonically-increasing frame index. With slots >=
    // MaxFramesInFlight + 1, the slot chosen at frame N was last touched at
    // N - slots, which is guaranteed GPU-complete — so no in-flight frame can be
    // reading it when frame N overwrites it. The hazard fix reduces to: consecutive
    // frames must map to DIFFERENT slots (slots >= 2), which this guarantees.
    public static int RingSlot(long frameIndex, int slots)
    {
        if (slots <= 0) throw new ArgumentOutOfRangeException(nameof(slots));
        return (int)(((frameIndex % slots) + slots) % slots);
    }
}

// A single ring slot's bump pointer over a fixed-capacity byte region. Each
// TryAlloc returns a stride-aligned offset and advances; on overflow it fails
// WITHOUT mutating, so a caller can throw cleanly. Reset rewinds for slot reuse.
// A mutable struct used in-place inside an array (arrays expose elements by ref,
// so a[i].TryAlloc(...) mutates the stored element, not a copy).
public struct BumpSlot
{
    public int Used;
    public readonly int Capacity;

    public BumpSlot(int capacity)
    {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        Used = 0;
    }

    public bool TryAlloc(int length, int stride, out int offset)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        offset = TransientArenaMath.AlignUp(Used, stride);
        if ((long)offset + length > Capacity)
        {
            offset = 0;
            return false;
        }
        Used = offset + length;
        return true;
    }

    public void Reset() => Used = 0;
}
