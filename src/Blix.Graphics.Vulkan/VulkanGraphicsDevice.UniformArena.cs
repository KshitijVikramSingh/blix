using Blix.Graphics;
using Silk.NET.Vulkan;

namespace Blix.Graphics.Vulkan;

// Per-frame transient UNIFORM arena — the sibling of the vertex arena, for the same reason and
// with the same ring.
//
// ── The fault it removes ────────────────────────────────────────────────────
// A ShaderUniform's value used to land in a buffer the PROGRAM owned, indexed by frame slot. Its
// granularity was therefore (program, frame), not (program, draw): two draws in a frame sharing a
// program and writing different values for the same member collided, and because host writes happen
// while commands are recorded while the GPU reads at execution, the last writer won for both. Not a
// race — deterministic aliasing, producing a picture that is internally consistent and wrong.
//
// It cost two bugs in one session (a second camera whose pass shared the lit program; then every
// debug view sharing the line drawer's) and no instrument saw either.
//
// Here each draw's block is bump-allocated from a per-frame ring and the descriptor is bound with a
// DYNAMIC OFFSET, so a draw's uniforms belong to that draw. Exactly what push constants already did
// for payloads under 128 bytes, extended to blocks that cannot fit there.
//
// ── Sets 0 and 1 only ───────────────────────────────────────────────────────
// Materials bind sets 2 and 3 and own their own buffers through MaterialBindings, which is already
// per-consumer and cannot alias. Making those dynamic would mean the material's descriptor writes
// and its bind had to carry offsets too — 33 call sites across nine files including the external RTSGame consumer — for no
// behavioural gain. Sets 0 and 1 are exclusively program-owned, which is what makes this safe.
public sealed partial class VulkanGraphicsDevice
{
    // Sized for a frame's worth of distinct uniform blocks, not a frame's worth of DRAWS: the
    // allocator reuses the last slice when a draw writes the same bytes, which is the common case
    // (one per-pass block handed to every draw in the pass). Sponza is the heaviest consumer at a
    // few hundred draws over a handful of distinct blocks.
    private const int UniformArenaSlotBytes = 1 * 1024 * 1024;

    private VkBufferEntry[] uniformArenaBuffers = System.Array.Empty<VkBufferEntry>();
    private BumpSlot[] uniformArenaBump = System.Array.Empty<BumpSlot>();

    // vkCmdBindDescriptorSets requires every dynamic offset to be a multiple of this. Read from the
    // device rather than assumed: it is 256 on plenty of hardware and 16 on some, and a hard-coded
    // guess is either wasteful or invalid with no middle ground.
    private int uniformOffsetAlignment = 256;

    private int uniformArenaHighWaterBytes;

    private void CreateUniformArena()
    {
        uniformArenaBuffers = new VkBufferEntry[ArenaSlots];
        uniformArenaBump = new BumpSlot[ArenaSlots];
        for (var i = 0; i < ArenaSlots; i++)
        {
            uniformArenaBuffers[i] = CreateHostVisibleBuffer(
                new byte[UniformArenaSlotBytes],
                BufferUsageFlags.UniformBufferBit,
                $"uniform-arena.slot{i}");
            uniformArenaBump[i] = new BumpSlot(UniformArenaSlotBytes);
        }
    }

    /// <summary>Copies one uniform block into the current ring slot and returns where it landed.</summary>
    /// <remarks>
    /// The offset is aligned to the device's minimum dynamic-offset alignment, which is what
    /// vkCmdBindDescriptorSets demands of the value it is handed.
    /// </remarks>
    private (Silk.NET.Vulkan.Buffer Buffer, uint Offset) AllocUniformBlock(System.ReadOnlySpan<byte> data, string name)
    {
        if (uniformArenaBuffers.Length == 0)
        {
            throw new System.InvalidOperationException("Uniform arena not initialized.");
        }

        if (!uniformArenaBump[arenaSlot].TryAlloc(data.Length, uniformOffsetAlignment, out var offset))
        {
            throw new System.InvalidOperationException(
                $"Transient uniform arena slot exhausted ({name}): requested {data.Length}B at " +
                $"alignment {uniformOffsetAlignment}; slot capacity is {UniformArenaSlotBytes}B. " +
                $"A frame is writing far more distinct uniform blocks than expected — the allocator " +
                $"reuses a slice when the bytes are unchanged, so this means they genuinely differ.");
        }

        var entry = uniformArenaBuffers[arenaSlot];
        UploadToHostVisibleBuffer(entry.Memory, data, (ulong)offset);
        if (uniformArenaBump[arenaSlot].Used > uniformArenaHighWaterBytes)
        {
            uniformArenaHighWaterBytes = uniformArenaBump[arenaSlot].Used;
        }

        return (entry.Buffer, (uint)offset);
    }

    // Rewound in lockstep with the vertex arena — same ring index, same slot-depth reasoning
    // (ArenaSlots = MaxFramesInFlight + 1, so the slot being rewound was last read a full
    // frames-in-flight ago and is GPU-complete).
    private void ResetUniformArenaSlot()
    {
        if (uniformArenaBump.Length == 0) return;
        uniformArenaBump[arenaSlot].Reset();
    }

    private unsafe void DestroyUniformArena()
    {
        foreach (var entry in uniformArenaBuffers)
        {
            if (entry.Buffer.Handle != 0) Vk.DestroyBuffer(Device, entry.Buffer, null);
            if (entry.Memory.Handle != 0) Vk.FreeMemory(Device, entry.Memory, null);
        }

        uniformArenaBuffers = System.Array.Empty<VkBufferEntry>();
        uniformArenaBump = System.Array.Empty<BumpSlot>();
    }

    /// <summary>
    /// Whether a set's uniform buffers are arena-allocated per draw rather than owned by the program.
    /// </summary>
    /// <remarks>
    /// Sets 0 and 1 only. <see cref="MaterialOwnedSet"/> (2) and set 3 are bound by MaterialBindings,
    /// which owns its own buffers and writes its own descriptors — making those dynamic would mean
    /// every material call site had to carry an offset, for a fault that cannot happen there.
    /// </remarks>
    internal static bool SetUsesDynamicUniforms(int setIdx) => setIdx < MaterialOwnedSet;
}
