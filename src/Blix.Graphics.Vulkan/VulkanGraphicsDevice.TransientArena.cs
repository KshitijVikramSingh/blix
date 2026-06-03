using Silk.NET.Vulkan;

namespace Blix.Graphics.Vulkan;

// Per-frame transient vertex-data arena: a ring of host-visible vertex buffers
// (one per slot), bump-allocated within the current slot each frame. This replaces
// the old "single dynamic vertex buffer re-mapped every frame" pattern used by
// SpriteBatch / VkLineDrawer, which raced the GPU across frames-in-flight — frame N
// could overwrite vertices the GPU was still reading for frame N-1. The ring depth
// (MaxFramesInFlight + 1, mirroring the indirect ring) guarantees the slot filled
// at frame N was last read at N-(MFIF+1), i.e. GPU-complete, so writes never
// collide with in-flight reads and no extra fence wait is needed.
public sealed partial class VulkanGraphicsDevice
{
    // One MORE slot than frames-in-flight — same reasoning as IndirectSlots: the
    // arena is filled during OnRender (encode), BEFORE Execute's vkWaitForFences,
    // so a MaxFramesInFlight-deep ring would let frame N write the slot the GPU is
    // still reading for an in-flight frame. The extra slot removes that race.
    private const int ArenaSlots = MaxFramesInFlight + 1;

    // Per-slot capacity. Worst-case single-frame transient vertex traffic today is
    // SpriteBatch (~576 KiB) + VkLineDrawer (~224 KiB) ≈ 0.8 MiB; 4 MiB leaves
    // headroom for the particle consumer to come without a resize path.
    private const int ArenaSlotBytes = 4 * 1024 * 1024;

    // Ring index, advanced once per presented frame in lockstep with the indirect
    // ring (NOT currentFrame, which only cycles MaxFramesInFlight).
    private int arenaSlot;
    private VertexBufferHandle[] arenaSlotBuffers = System.Array.Empty<VertexBufferHandle>();
    private BumpSlot[] arenaBump = System.Array.Empty<BumpSlot>();
    private int arenaHighWaterBytes;

    private void CreateTransientArena()
    {
        arenaSlotBuffers = new VertexBufferHandle[ArenaSlots];
        arenaBump = new BumpSlot[ArenaSlots];
        for (var i = 0; i < ArenaSlots; i++)
        {
            // Registered in vertexBufferTable so the returned handle resolves in the
            // draw path (GetVertexBuffer) AND is freed exactly once by
            // DestroyAllResources at teardown. The arena keeps no separate buffer
            // ownership, so there is no double-free to guard against.
            var entry = CreateHostVisibleBuffer(
                new byte[ArenaSlotBytes], BufferUsageFlags.VertexBufferBit, $"transient-arena.slot{i}");
            var id = nextResourceId++;
            vertexBufferTable[id] = entry;
            arenaSlotBuffers[i] = new VertexBufferHandle(id);
            arenaBump[i] = new BumpSlot(ArenaSlotBytes);
        }
    }

    // Sub-allocate a vertex slice from the CURRENT ring slot and copy `data` into
    // it. The returned slice's ByteOffset is aligned to vertexStride; bind the
    // buffer at that offset and draw the matching index buffer with base-0 indices
    // (firstVertex = 0). Throws if the slot is exhausted — callers that can bound
    // their traffic (VkLineDrawer caps before calling) won't hit it; SpriteBatch
    // already caps its sprite count.
    public TransientVertexSlice AllocVertices(ReadOnlySpan<byte> data, int vertexStride, string? name = null)
    {
        ThrowIfDisposed();
        if (arenaSlotBuffers.Length == 0)
        {
            throw new InvalidOperationException("Transient arena not initialized.");
        }
        if (!arenaBump[arenaSlot].TryAlloc(data.Length, vertexStride, out var offset))
        {
            throw new InvalidOperationException(
                $"Transient vertex arena slot exhausted ({name ?? "alloc"}): requested {data.Length}B " +
                $"at stride {vertexStride}; slot capacity is {ArenaSlotBytes}B.");
        }
        UploadToHostVisibleBuffer(GetVertexBuffer(arenaSlotBuffers[arenaSlot]).Memory, data, (ulong)offset);
        if (arenaBump[arenaSlot].Used > arenaHighWaterBytes)
        {
            arenaHighWaterBytes = arenaBump[arenaSlot].Used;
        }
        return new TransientVertexSlice(arenaSlotBuffers[arenaSlot], (ulong)offset, data.Length);
    }

    // Advance to the next ring slot and rewind its bump pointer. The new slot was
    // last filled ArenaSlots frames ago (GPU-complete), so the rewind is safe with
    // no fence wait. Called once per presented frame, alongside AdvanceIndirectSlot.
    internal void AdvanceArenaSlot()
    {
        if (arenaBump.Length == 0) return;
        arenaSlot = (arenaSlot + 1) % ArenaSlots;
        arenaBump[arenaSlot].Reset();
    }

    // Live gauges for the debug overlay: bytes used in the current slot, the
    // all-time per-slot high-water mark, and per-slot capacity.
    internal (int Used, int HighWater, int Capacity) TransientArenaStats()
        => (arenaBump.Length == 0 ? 0 : arenaBump[arenaSlot].Used, arenaHighWaterBytes, ArenaSlotBytes);
}
