namespace Blix.Diagnostics;

// Fixed-capacity ring of finished DebugFrames. Phase 1 uses it only for
// "most recent N frames for lookup," which is enough to back Freeze(int)
// and a Phase 2 sparkline. Older frames silently drop off the tail.
//
// Lookup by frame Number scans the ring (O(capacity)); capacity stays
// small (default 120 frames = 2s @ 60fps), so a linear scan is fine and
// avoids a parallel dictionary that would have to stay in sync.
//
// Not thread-safe. All Push / read calls run on the GL thread.
public sealed class DebugFrameHistory
{
    private readonly DebugFrame?[] ring;
    private int writeIndex;
    private int count;

    public DebugFrameHistory(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        ring = new DebugFrame?[capacity];
    }

    public int Capacity => ring.Length;

    public int Count => count;

    // Most recent finished frame, or null if Push has never been called.
    public DebugFrame? Latest
    {
        get
        {
            if (count == 0)
            {
                return null;
            }

            var index = writeIndex - 1;
            if (index < 0)
            {
                index += ring.Length;
            }

            return ring[index];
        }
    }

    // Find a frame by its monotonic Number. Returns null if the frame has
    // already aged out of the ring (or was never recorded).
    public DebugFrame? GetByFrameNumber(int frameNumber)
    {
        if (frameNumber <= 0 || count == 0)
        {
            return null;
        }

        for (var i = 0; i < count; i++)
        {
            var index = writeIndex - 1 - i;
            if (index < 0)
            {
                index += ring.Length;
            }

            var frame = ring[index];
            if (frame is null)
            {
                continue;
            }

            if (frame.Number == frameNumber)
            {
                return frame;
            }

            // Frames are pushed in ascending Number order. Once we walk past
            // a frame older than the target, no earlier slot in the ring
            // could match.
            if (frame.Number < frameNumber)
            {
                return null;
            }
        }

        return null;
    }

    // Walks the ring newest -> oldest. Yields up to Count frames.
    public IEnumerable<DebugFrame> EnumerateLatestFirst()
    {
        for (var i = 0; i < count; i++)
        {
            var index = writeIndex - 1 - i;
            if (index < 0)
            {
                index += ring.Length;
            }

            var frame = ring[index];
            if (frame is not null)
            {
                yield return frame;
            }
        }
    }

    internal void Push(DebugFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ring[writeIndex] = frame;
        writeIndex = (writeIndex + 1) % ring.Length;
        if (count < ring.Length)
        {
            count++;
        }
    }
}
