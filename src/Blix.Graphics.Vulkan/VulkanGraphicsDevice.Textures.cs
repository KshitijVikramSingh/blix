using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Blix.Graphics.Vulkan;

// VkImage + VkImageView + VkSampler + memory + staging-buffer upload +
// layout transitions. Owns CreateTexture2D, DestroyTexture, the sampler
// cache, and the single-time command buffer used for transitions and
// buffer-to-image copies during texture upload.
public sealed partial class VulkanGraphicsDevice
{
    // Sampler cache. SamplerDescription is a record so value-equality
    // dedupes identical samplers — the typical case is N textures all using
    // LinearRepeat, which should produce ONE VkSampler not N.
    private readonly Dictionary<SamplerDescription, Sampler> samplerCache = new();

    private readonly Dictionary<int, VkTextureEntry> textureTable = new();

    internal sealed class VkTextureEntry
    {
        public Image Image;
        public DeviceMemory Memory;
        public ImageView View;
        public Sampler Sampler;
        public int Width;
        public int Height;
        public int MipCount;
        public Format Format;
        public string Name = string.Empty;
    }

    // --- Public API --------------------------------------------------------

    public TextureHandle CreateTexture2D(TextureDescription description, ReadOnlySpan<byte> pixels, string? name = null)
    {
        if (description.Format == TextureFormat.Depth24)
        {
            throw new ArgumentException(
                "Depth textures must be created via render-surface attachment, not CreateTexture2D.", nameof(description));
        }

        var entry = UploadTexture2D(
            description.Width,
            description.Height,
            description.Format,
            mipCount: 1,
            pixels,
            description.Sampler,
            name ?? "texture2D");

        var id = nextResourceId++;
        textureTable[id] = entry;
        return new TextureHandle(id);
    }

    public void DestroyTexture(TextureHandle handle)
    {
        if (!textureTable.Remove(handle.Id, out var e)) return;
        DestroyVkTextureEntry(e);
    }

    internal VkTextureEntry GetTexture(TextureHandle h) => textureTable[h.Id];

    // --- Texture creation core ---------------------------------------------

    // Uploads a single-mip 2D texture. Path:
    //   1. Staging buffer (host-visible) — memcpy pixels in.
    //   2. VkImage (device-local) + VkDeviceMemory (dedicated allocation).
    //   3. Single-time command buffer: transition UNDEFINED → TRANSFER_DST,
    //      copy buffer → image, transition TRANSFER_DST → SHADER_READ_ONLY.
    //   4. VkImageView + cached VkSampler.
    //   5. Destroy staging buffer.
    private unsafe VkTextureEntry UploadTexture2D(
        int width,
        int height,
        TextureFormat format,
        int mipCount,
        ReadOnlySpan<byte> pixels,
        SamplerDescription samplerDesc,
        string name)
    {
        var vkFormat = MapTextureFormat(format);
        var expectedBytes = format.MipByteCount(width, height);
        if (pixels.Length != expectedBytes)
        {
            throw new ArgumentException(
                $"Texture '{name}' expected {expectedBytes} bytes for {width}x{height} {format}, got {pixels.Length}.",
                nameof(pixels));
        }

        // 1. Staging buffer.
        var staging = CreateHostVisibleBuffer(pixels, BufferUsageFlags.TransferSrcBit, $"{name}.staging");

        // 2. Image + memory.
        var imageCi = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = vkFormat,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = (uint)mipCount,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Image image;
        ThrowIfNotSuccess(Vk.CreateImage(Device, in imageCi, null, &image), $"vkCreateImage({name})");

        Vk.GetImageMemoryRequirements(Device, image, out var memReq);
        var memTypeIdx = FindMemoryTypeIndex(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        var allocCi = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memTypeIdx,
        };
        DeviceMemory memory;
        ThrowIfNotSuccess(Vk.AllocateMemory(Device, in allocCi, null, &memory), $"vkAllocateMemory({name})");
        ThrowIfNotSuccess(Vk.BindImageMemory(Device, image, memory, 0), $"vkBindImageMemory({name})");

        // 3. Transition + copy + transition (single-time command buffer).
        var cmd = BeginSingleTimeCommands();
        TransitionImageLayout(cmd, image, mipCount, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);

        var copyRegion = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D((uint)width, (uint)height, 1),
        };
        Vk.CmdCopyBufferToImage(cmd, staging.Buffer, image, ImageLayout.TransferDstOptimal, 1, in copyRegion);

        TransitionImageLayout(cmd, image, mipCount, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        EndSingleTimeCommands(cmd);

        // 4. View + sampler.
        var viewCi = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = vkFormat,
            Components = new ComponentMapping(
                ComponentSwizzle.Identity, ComponentSwizzle.Identity,
                ComponentSwizzle.Identity, ComponentSwizzle.Identity),
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = (uint)mipCount,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        ImageView view;
        ThrowIfNotSuccess(Vk.CreateImageView(Device, in viewCi, null, &view), $"vkCreateImageView({name})");

        var sampler = GetOrCreateSampler(samplerDesc);

        // 5. Drop the staging buffer.
        DestroyVkBufferEntry(staging);

        return new VkTextureEntry
        {
            Image = image,
            Memory = memory,
            View = view,
            Sampler = sampler,
            Width = width,
            Height = height,
            MipCount = mipCount,
            Format = vkFormat,
            Name = name,
        };
    }

    // --- Layout transitions ------------------------------------------------

    // The two transitions used during upload are well-known; encoded as a
    // pair-table rather than a fully general state machine.
    private unsafe void TransitionImageLayout(
        CommandBuffer cmd,
        Image image,
        int mipCount,
        ImageLayout oldLayout,
        ImageLayout newLayout)
    {
        AccessFlags srcAccess;
        AccessFlags dstAccess;
        PipelineStageFlags srcStage;
        PipelineStageFlags dstStage;

        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            srcAccess = 0;
            dstAccess = AccessFlags.TransferWriteBit;
            srcStage = PipelineStageFlags.TopOfPipeBit;
            dstStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            srcAccess = AccessFlags.TransferWriteBit;
            dstAccess = AccessFlags.ShaderReadBit;
            srcStage = PipelineStageFlags.TransferBit;
            // Fragment shader is where SampledImage typically reads — vertex
            // sampling is rare and would need a wider stage mask. Revisit
            // when ShaderLab introduces vertex-stage sampling.
            dstStage = PipelineStageFlags.FragmentShaderBit;
        }
        else
        {
            throw new InvalidOperationException($"Unsupported layout transition {oldLayout} → {newLayout}.");
        }

        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = (uint)mipCount,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
        };
        Vk.CmdPipelineBarrier(
            cmd,
            srcStage, dstStage,
            DependencyFlags.None,
            0, default(MemoryBarrier*),
            0, default(BufferMemoryBarrier*),
            1, in barrier);
    }

    // --- Single-time command buffer helpers --------------------------------

    // Allocate, begin recording, return. Caller submits via EndSingleTimeCommands.
    // Synchronous — fine because uploads happen at load time, not in the
    // render loop.
    private unsafe CommandBuffer BeginSingleTimeCommands()
    {
        var ai = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer cmd;
        ThrowIfNotSuccess(Vk.AllocateCommandBuffers(Device, in ai, &cmd), "vkAllocateCommandBuffers(singleTime)");

        var bi = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        ThrowIfNotSuccess(Vk.BeginCommandBuffer(cmd, in bi), "vkBeginCommandBuffer(singleTime)");
        return cmd;
    }

    private unsafe void EndSingleTimeCommands(CommandBuffer cmd)
    {
        ThrowIfNotSuccess(Vk.EndCommandBuffer(cmd), "vkEndCommandBuffer(singleTime)");

        var si = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
        };
        ThrowIfNotSuccess(Vk.QueueSubmit(GraphicsQueue, 1, in si, default), "vkQueueSubmit(singleTime)");
        ThrowIfNotSuccess(Vk.QueueWaitIdle(GraphicsQueue), "vkQueueWaitIdle(singleTime)");

        Vk.FreeCommandBuffers(Device, commandPool, 1, in cmd);
    }

    // --- Format mapping ----------------------------------------------------

    private static Format MapTextureFormat(TextureFormat f) => f switch
    {
        TextureFormat.Rgba8 => Format.R8G8B8A8Unorm,
        TextureFormat.Rgba8Srgb => Format.R8G8B8A8Srgb,
        TextureFormat.R8 => Format.R8Unorm,
        TextureFormat.Rgba16F => Format.R16G16B16A16Sfloat,
        TextureFormat.Bc7Srgb => Format.BC7SrgbBlock,
        TextureFormat.Bc7Unorm => Format.BC7UnormBlock,
        TextureFormat.Bc5Unorm => Format.BC5UnormBlock,
        TextureFormat.Bc6hUf16 => Format.BC6HUfloatBlock,
        TextureFormat.Depth24 => throw new ArgumentException(
            "Depth formats are not valid as sampleable textures here.", nameof(f)),
        _ => throw new ArgumentOutOfRangeException(nameof(f), f, null),
    };

    // Returns the cached VkSampler for the description, creating one on
    // first request. Cache is process-lifetime; samplers are cheap to keep
    // around and Vulkan caps the count per device at thousands so the
    // unique-sampler count we'll ever see (≤20) stays well under the limit.
    internal unsafe Sampler GetOrCreateSampler(SamplerDescription desc)
    {
        if (samplerCache.TryGetValue(desc, out var existing)) return existing;

        var ci = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MinFilter = MapFilter(desc.MinFilter),
            MagFilter = MapFilter(desc.MagFilter),
            // Mip filter follows MinFilter — bilinear within a mip pairs
            // with linear between mips for trilinear; nearest pairs with
            // nearest. When mipmaps are disabled the field is ignored.
            MipmapMode = desc.MinFilter == TextureFilter.Linear
                ? SamplerMipmapMode.Linear
                : SamplerMipmapMode.Nearest,
            AddressModeU = MapWrap(desc.WrapU),
            AddressModeV = MapWrap(desc.WrapV),
            AddressModeW = MapWrap(desc.WrapW),
            AnisotropyEnable = false,
            MaxAnisotropy = 1.0f,
            CompareEnable = desc.Compare,
            CompareOp = desc.Compare ? CompareOp.LessOrEqual : CompareOp.Always,
            MinLod = 0,
            // Unclamped — let the texture's own mip count limit sampling.
            // For non-mipped textures the view only exposes mip 0 so this
            // is a no-op.
            MaxLod = float.MaxValue,
            BorderColor = BorderColor.IntOpaqueBlack,
            UnnormalizedCoordinates = false,
        };
        Sampler sampler;
        ThrowIfNotSuccess(Vk.CreateSampler(Device, in ci, null, &sampler), "vkCreateSampler");
        samplerCache[desc] = sampler;
        return sampler;
    }

    private static Filter MapFilter(TextureFilter f) => f switch
    {
        TextureFilter.Nearest => Filter.Nearest,
        TextureFilter.Linear => Filter.Linear,
        _ => throw new ArgumentOutOfRangeException(nameof(f), f, null),
    };

    private static SamplerAddressMode MapWrap(TextureWrap w) => w switch
    {
        TextureWrap.Repeat => SamplerAddressMode.Repeat,
        TextureWrap.ClampToEdge => SamplerAddressMode.ClampToEdge,
        _ => throw new ArgumentOutOfRangeException(nameof(w), w, null),
    };

    private unsafe void DestroyAllSamplers()
    {
        foreach (var s in samplerCache.Values)
        {
            if (s.Handle != 0) Vk.DestroySampler(Device, s, null);
        }
        samplerCache.Clear();
    }

    private void DestroyAllTextures()
    {
        foreach (var e in textureTable.Values) DestroyVkTextureEntry(e);
        textureTable.Clear();
    }

    private unsafe void DestroyVkTextureEntry(VkTextureEntry e)
    {
        if (e.View.Handle != 0) Vk.DestroyImageView(Device, e.View, null);
        if (e.Image.Handle != 0) Vk.DestroyImage(Device, e.Image, null);
        if (e.Memory.Handle != 0) Vk.FreeMemory(Device, e.Memory, null);
        // Sampler stays in the cache — shared across textures, destroyed via DestroyAllSamplers.
    }
}
