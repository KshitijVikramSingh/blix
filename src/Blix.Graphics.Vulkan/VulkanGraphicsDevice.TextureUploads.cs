using Blix.Graphics;
using Silk.NET.Vulkan;

namespace Blix.Graphics.Vulkan;

// Per-frame texture uploads that do NOT stop the world.
//
// The gap this fills, and it was expensive: UploadTextureMip submits through
// EndSingleTimeCommands, which ends in vkQueueWaitIdle. That is exactly right for a
// load-time or streamed upload, where the caller's next line assumes the level is
// resident — and catastrophic for anything uploaded every frame, because the whole
// graphics queue drains mid-frame. CPU and GPU stop overlapping, so the frame costs
// CPU + GPU instead of max(CPU, GPU), and the wait is charged to whoever touched the
// device first rather than to the work it is waiting for.
//
// Measured in RTSGame, whose fog-of-war mask is uploaded on every frame its texels
// change: 20 ms of a 28 ms frame at a wide standoff, and about 33 ms of a 45 ms frame
// on a heavier map. A frame that skipped the upload — the fog happened to be clean —
// ran at about 7 ms. Same build, same view: the frame was bimodal and the mode was set
// by a dirty flag. Two observers disagreed by six times over it, and both were right.
//
// So: stage into a ring of host-visible buffers, record the copy and its barriers into
// the frame's own command buffer before any render pass opens, and let the GPU find the
// data when it gets there. Nothing waits.
//
// The ring is MaxFramesInFlight + 1 deep, for the same reason the transient vertex arena
// and the indirect ring are: uploads are queued during encode, BEFORE Execute's fence
// wait, so a ring only as deep as the frames in flight would let frame N overwrite
// bytes the GPU is still reading for an in-flight frame.
//
// Usage:
//     device.QueueTextureUpload(mask, 0, texels);   // any time before the frame is executed
//
// Use UploadTextureMip instead when the caller must know the data has landed — a
// streamed mip chain, a one-off load, anything whose next statement samples it.
public sealed partial class VulkanGraphicsDevice
{
    private const int TextureUploadSlots = MaxFramesInFlight + 1;

    // Per-slot capacity. Today's whole-frame traffic is one fog mask plus one wear mask,
    // both single-channel and well under a hundred kilobytes; 4 MiB leaves room for a
    // full 1024x1024 RGBA mask per frame without a resize path.
    private const int TextureUploadSlotBytes = 4 * 1024 * 1024;

    // Copy offsets satisfy the device's optimalBufferCopyOffsetAlignment and texel block size.
    // 256 covers every alignment reported by the drivers this
    // engine runs on and is a multiple of four, which is the spec's own floor. Alignment is
    // per allocation rather than per buffer, so the cost is a few wasted bytes per upload.
    private const int TextureUploadAlignment = 256;

    private VkBufferEntry[] textureUploadSlots = System.Array.Empty<VkBufferEntry>();
    private BumpSlot[] textureUploadBump = System.Array.Empty<BumpSlot>();
    private int textureUploadSlot;
    private int textureUploadHighWaterBytes;

    private readonly List<PendingTextureCopy> pendingTextureCopies = new();

    private readonly record struct PendingTextureCopy(
        int TextureId, int MipLevel, int Slot, ulong ByteOffset);

    /// <summary>Uploads queued for this frame and not yet recorded. For the diagnostics overlay.</summary>
    public int QueuedTextureUploadCount => pendingTextureCopies.Count;

    /// <summary>
    /// Builds the staging ring on first use.
    /// </summary>
    /// <remarks>
    /// Lazy, because it is twelve megabytes of host-visible memory and most Blix apps upload every texture
    /// they will ever need at load time. An engine that charges every consumer for a facility one of them
    /// wants is how a substrate gets a reputation.
    /// </remarks>
    private void CreateTextureUploadRing()
    {
        if (textureUploadSlots.Length > 0) return;
        textureUploadSlots = new VkBufferEntry[TextureUploadSlots];
        textureUploadBump = new BumpSlot[TextureUploadSlots];
        for (var i = 0; i < TextureUploadSlots; i++)
        {
            textureUploadSlots[i] = CreateHostVisibleBuffer(
                new byte[TextureUploadSlotBytes],
                BufferUsageFlags.TransferSrcBit,
                $"texture-upload.slot{i}");
            textureUploadBump[i] = new BumpSlot(TextureUploadSlotBytes);
        }
    }

    /// <summary>
    /// Stages one mip level's bytes and records the copy with this frame's commands.
    /// </summary>
    /// <remarks>
    /// The texture must be a sampled image sitting in ShaderReadOnlyOptimal — which is where
    /// every texture this device creates ends up — because the recorded barrier moves it from
    /// there to TransferDst and back. Queue as many as you like; they are recorded in the order
    /// queued, so two uploads to one level resolve to the last one.
    /// </remarks>
    public void QueueTextureUpload(TextureHandle handle, int mipLevel, ReadOnlySpan<byte> bytes)
    {
        ThrowIfDisposed();
        if (mipLevel < 0) throw new ArgumentOutOfRangeException(nameof(mipLevel));
        CreateTextureUploadRing();

        var e = textureTable[handle.Id];
        if (mipLevel >= e.MipCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mipLevel), $"texture '{e.Name}' has {e.MipCount} mips.");
        }

        if (!textureUploadBump[textureUploadSlot].TryAlloc(
                bytes.Length, TextureUploadAlignment, out var offset))
        {
            throw new InvalidOperationException(
                $"Texture upload ring slot exhausted uploading '{e.Name}' mip {mipLevel}: requested " +
                $"{bytes.Length}B, slot capacity is {TextureUploadSlotBytes}B. Either the frame is " +
                "uploading more than the ring was sized for, or an upload is being queued in a loop.");
        }

        UploadToHostVisibleBuffer(textureUploadSlots[textureUploadSlot].Memory, bytes, (ulong)offset);
        if (textureUploadBump[textureUploadSlot].Used > textureUploadHighWaterBytes)
        {
            textureUploadHighWaterBytes = textureUploadBump[textureUploadSlot].Used;
        }

        pendingTextureCopies.Add(
            new PendingTextureCopy(handle.Id, mipLevel, textureUploadSlot, (ulong)offset));
    }

    /// <summary>
    /// Records every queued copy into <paramref name="cmd"/>. Called once per frame, outside any
    /// render pass, before the passes are translated.
    /// </summary>
    /// <remarks>
    /// The barrier out of ShaderReadOnlyOptimal orders the copy after earlier sampling on the same
    /// queue. A barrier applies to commands earlier in submission order,
    /// which includes earlier submissions — so declaring the source stage as the fragment shader
    /// makes the previous frame's sampling of this image complete before the copy overwrites it,
    /// without anybody waiting on the CPU.
    /// </remarks>
    private unsafe void RecordQueuedTextureUploads(CommandBuffer cmd)
    {
        if (pendingTextureCopies.Count == 0) return;
        foreach (var pending in pendingTextureCopies)
        {
            var e = textureTable[pending.TextureId];
            TransitionImageLayout(
                cmd, e.Image, e.MipCount,
                ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal);
            var region = new BufferImageCopy
            {
                BufferOffset = pending.ByteOffset,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = (uint)pending.MipLevel,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D(
                    (uint)Math.Max(1, e.Width >> pending.MipLevel),
                    (uint)Math.Max(1, e.Height >> pending.MipLevel),
                    1),
            };
            Vk.CmdCopyBufferToImage(
                cmd,
                textureUploadSlots[pending.Slot].Buffer,
                e.Image,
                ImageLayout.TransferDstOptimal,
                1,
                in region);
            TransitionImageLayout(
                cmd, e.Image, e.MipCount,
                ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
            e.UploadedMips |= 1UL << pending.MipLevel;
        }

        pendingTextureCopies.Clear();
    }

    /// <summary>
    /// Advances to the next staging slot and rewinds it. Once per presented frame, beside
    /// AdvanceArenaSlot.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT called when a frame is abandoned — an out-of-date swapchain, a lost
    /// acquire — because the copies queued for that frame are still pending and still pointing
    /// into the current slot. Leaving the slot where it is keeps their bytes valid so the next
    /// real frame records them, rather than dropping an upload whose source has already told its
    /// owner it was taken.
    /// </remarks>
    internal void AdvanceTextureUploadSlot()
    {
        if (textureUploadBump.Length == 0) return;
        textureUploadSlot = (textureUploadSlot + 1) % TextureUploadSlots;
        textureUploadBump[textureUploadSlot].Reset();
    }

    /// <summary>Bytes used in the current slot, the all-time high-water mark, and capacity.</summary>
    internal (int Used, int HighWater, int Capacity) TextureUploadStats()
        => (textureUploadBump.Length == 0 ? 0 : textureUploadBump[textureUploadSlot].Used,
            textureUploadHighWaterBytes,
            TextureUploadSlotBytes);
}
