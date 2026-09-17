using Blix.Graphics;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Blix.Graphics.Vulkan;

/// <summary>
/// Reading a rendered image back to the CPU.
/// </summary>
/// <remarks>
/// <b>The first pull in an otherwise push-only device.</b> Everything else here moves data toward the GPU;
/// this is the one operation that asks what came out. Blix had none, and the absence shaped how the project
/// worked — TankArena's model fit is dialled in through the overlay by eye, with a comment saying
/// "Screenshots don't work", and three rendering bugs in the view/lab arcs were caught only because a human
/// looked at a picture while four green bounded runs and zero validation errors said nothing.
/// <para>
/// <b>Synchronous, and unapologetically so.</b> It submits, waits the queue idle, maps and copies. That is
/// a stall measured in milliseconds and completely wrong for a per-frame path — and completely right for
/// the thing this is for, which is a tool taking one picture. An asynchronous ring belongs to whoever first
/// needs a capture every frame; guessing that shape now would be policy.
/// </para>
/// </remarks>
public sealed partial class VulkanGraphicsDevice
{
    /// <summary>
    /// Copies a rendered colour texture back to CPU bytes, tightly packed, top row first.
    /// </summary>
    /// <remarks>
    /// The image is expected to be in <c>SHADER_READ_ONLY_OPTIMAL</c> — which is where a render graph
    /// leaves a colour target that something sampled, and therefore where a frame's output sits once it has
    /// been presented. Run under <c>BLIX_VK_VALIDATE=1</c> if that is in doubt: a layout mismatch is
    /// exactly what the validation layers exist to say out loud.
    /// </remarks>
    public unsafe byte[] ReadTexture(TextureHandle handle, out int width, out int height, out TextureFormat format)
    {
        var entry = GetTexture(handle);
        width = entry.Width;
        height = entry.Height;
        format = entry.EngineFormat;

        var bytesPerPixel = BytesPerPixel(entry.EngineFormat);
        if (bytesPerPixel <= 0)
        {
            throw new NotSupportedException(
                $"Cannot read back texture format {entry.EngineFormat}: no known byte size. " +
                "Add it to VulkanGraphicsDevice.BytesPerPixel.");
        }

        var byteCount = checked(width * height * bytesPerPixel);
        var scratch = new byte[byteCount];
        var staging = CreateHostVisibleBuffer(scratch, BufferUsageFlags.TransferDstBit, "readback.staging");

        var cmd = BeginSingleTimeCommands();
        TransitionImageLayout(
            cmd, entry.Image, 1, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal);

        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,      // 0 = tightly packed to the extent below
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            ImageOffset = default,
            ImageExtent = new Extent3D((uint)width, (uint)height, 1),
        };
        Vk.CmdCopyImageToBuffer(
            cmd, entry.Image, ImageLayout.TransferSrcOptimal, staging.Buffer, 1, in region);

        // Put it back where the graph expects to find it, or the next frame's sample is a
        // layout error rather than a picture.
        TransitionImageLayout(
            cmd, entry.Image, 1, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal);
        EndSingleTimeCommands(cmd);

        var pixels = new byte[byteCount];
        void* mapped;
        ThrowIfNotSuccess(
            Vk.MapMemory(Device, staging.Memory, 0, staging.Size, 0, &mapped), "vkMapMemory(readback)");
        new ReadOnlySpan<byte>(mapped, byteCount).CopyTo(pixels);
        Vk.UnmapMemory(Device, staging.Memory);

        Vk.DestroyBuffer(Device, staging.Buffer, null);
        Vk.FreeMemory(Device, staging.Memory, null);

        return pixels;
    }

    /// <summary>Bytes per pixel for the formats readback understands. Zero means "not handled".</summary>
    private static int BytesPerPixel(TextureFormat format) => format switch
    {
        TextureFormat.Rgba8 => 4,
        TextureFormat.Rgba16F => 8,
        // Packed HDR: one 32-bit word holding 11/11/10 bits of float. The caller unpacks; this only
        // has to say how wide a pixel is. Without it a renderer using this as its scene target —
        // which is the point of the format — cannot be captured at all.
        TextureFormat.R11G11B10F => 4,
        _ => 0,
    };
}
