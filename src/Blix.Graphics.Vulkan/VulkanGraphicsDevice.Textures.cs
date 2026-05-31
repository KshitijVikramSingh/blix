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
        // Engine-side format + total GPU footprint, carried for the diagnostics
        // snapshot (SnapshotResources). ByteSize is the full mip chain × layers
        // (cube = ×6, 3D = ×depth); 0 for externally-owned images we don't size.
        public TextureFormat EngineFormat;
        public long ByteSize;
        // Streaming bookkeeping for the diagnostics snapshot. Streamable is set
        // only by AllocateTexture2DMips (the storage-then-stream path); every
        // other create path uploads in full, so the default (false) reads as
        // Resident. MipsUploaded counts levels landed via UploadTextureMip.
        public bool Streamable;
        public int MipsUploaded;
        public string Name = string.Empty;
    }

    // Inverse of MapTextureFormat. Render-graph attachments register through
    // RegisterExternalTexture with only a raw Vulkan Format; this recovers the
    // engine format for the diagnostics snapshot. Depth + anything unrecognised
    // is labelled conservatively — this output only feeds the diagnostic view.
    internal static TextureFormat MapVkFormatToEngine(Format f) => f switch
    {
        Format.R8G8B8A8Unorm => TextureFormat.Rgba8,
        Format.R8G8B8A8Srgb => TextureFormat.Rgba8Srgb,
        Format.R8Unorm => TextureFormat.R8,
        Format.R16G16B16A16Sfloat => TextureFormat.Rgba16F,
        Format.B10G11R11UfloatPack32 => TextureFormat.R11G11B10F,
        Format.BC7SrgbBlock => TextureFormat.Bc7Srgb,
        Format.BC7UnormBlock => TextureFormat.Bc7Unorm,
        Format.BC5UnormBlock => TextureFormat.Bc5Unorm,
        Format.BC6HUfloatBlock => TextureFormat.Bc6hUf16,
        Format.D32Sfloat or Format.D24UnormS8Uint or Format.D16Unorm
            or Format.X8D24UnormPack32 or Format.D32SfloatS8Uint => TextureFormat.Depth24,
        _ => TextureFormat.Rgba8,
    };

    // --- Public API --------------------------------------------------------

    public TextureHandle CreateTexture2D(TextureDescription description, ReadOnlySpan<byte> pixels, string? name = null)
    {
        if (description.Format == TextureFormat.Depth24)
        {
            throw new ArgumentException(
                "Depth textures must be created via render-surface attachment, not CreateTexture2D.", nameof(description));
        }

        // Generate a full mip chain (via GPU blit) for color textures whose
        // format supports a linear blit. Without mips, minified textures
        // (e.g. the checker floor at a grazing angle) alias into crawling
        // moiré and thrash the texture cache. Block-compressed formats can't
        // be blit-downsampled and must ship their own mips via the cube path;
        // they stay single-mip here.
        var vkFormat = MapTextureFormat(description.Format);
        var mipCount = SupportsLinearBlit(vkFormat)
            ? ComputeMipCount(description.Width, description.Height)
            : 1;

        var entry = UploadTexture2D(
            description.Width,
            description.Height,
            description.Format,
            mipCount,
            pixels,
            description.Sampler,
            name ?? "texture2D");

        var id = nextResourceId++;
        textureTable[id] = entry;
        return new TextureHandle(id);
    }

    // Mipped 2D texture from a pre-built mip chain — the cooked .blixtex path.
    // mipBytes[i] is level i's tightly-packed bytes (BC blocks or Rgba8); the
    // view exposes all levels for trilinear sampling. BC formats can't be
    // blit-downsampled, so they MUST arrive with their mips already encoded
    // (the cook does this); this path just uploads them verbatim. ImageExtent
    // is in texels — Vulkan handles BC 4×4 block addressing.
    public unsafe TextureHandle CreateTexture2DMipped(
        TextureDescription description,
        IReadOnlyList<byte[]> mipBytes,
        string? name = null)
    {
        if (mipBytes is null || mipBytes.Count == 0)
            throw new ArgumentException("CreateTexture2DMipped requires at least one mip level.", nameof(mipBytes));
        var label = name ?? "texture2D.mipped";
        var vkFormat = MapTextureFormat(description.Format);
        var mipCount = mipBytes.Count;

        // Concatenate the mip chain (mip-major) into one staging buffer.
        long total = 0;
        foreach (var m in mipBytes) total += m.Length;
        var concat = new byte[total];
        long c = 0;
        foreach (var m in mipBytes) { Array.Copy(m, 0L, concat, c, m.Length); c += m.Length; }
        var staging = CreateHostVisibleBuffer(concat, BufferUsageFlags.TransferSrcBit, $"{label}.staging");

        var imageCi = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = vkFormat,
            Extent = new Extent3D((uint)description.Width, (uint)description.Height, 1),
            MipLevels = (uint)mipCount,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Image image;
        ThrowIfNotSuccess(Vk.CreateImage(Device, in imageCi, null, &image), $"vkCreateImage({label})");

        Vk.GetImageMemoryRequirements(Device, image, out var memReq);
        var allocCi = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryTypeIndex(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        DeviceMemory memory;
        ThrowIfNotSuccess(Vk.AllocateMemory(Device, in allocCi, null, &memory), $"vkAllocateMemory({label})");
        ThrowIfNotSuccess(Vk.BindImageMemory(Device, image, memory, 0), $"vkBindImageMemory({label})");

        var cmd = BeginSingleTimeCommands();
        TransitionImageLayout(cmd, image, mipCount, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
        ulong offset = 0;
        for (var mip = 0; mip < mipCount; mip++)
        {
            var w = (uint)Math.Max(1, description.Width >> mip);
            var h = (uint)Math.Max(1, description.Height >> mip);
            var region = new BufferImageCopy
            {
                BufferOffset = offset,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = (uint)mip,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D(w, h, 1),
            };
            Vk.CmdCopyBufferToImage(cmd, staging.Buffer, image, ImageLayout.TransferDstOptimal, 1, in region);
            offset += (ulong)mipBytes[mip].Length;
        }
        TransitionImageLayout(cmd, image, mipCount, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        EndSingleTimeCommands(cmd);
        DestroyVkBufferEntry(staging);

        var viewCi = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = vkFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, (uint)mipCount, 0, 1),
        };
        ImageView view;
        ThrowIfNotSuccess(Vk.CreateImageView(Device, in viewCi, null, &view), $"vkCreateImageView({label})");

        var entry = new VkTextureEntry
        {
            Image = image,
            Memory = memory,
            View = view,
            Sampler = GetOrCreateSampler(description.Sampler),
            Width = description.Width,
            Height = description.Height,
            MipCount = mipCount,
            Format = vkFormat,
            EngineFormat = description.Format,
            ByteSize = description.Format.TextureByteCount(description.Width, description.Height, mipCount),
            Name = label,
        };
        var id = nextResourceId++;
        textureTable[id] = entry;
        return new TextureHandle(id);
    }

    // Allocate a mipped 2D texture with NO data uploaded yet — storage + view +
    // sampler only. Mips are filled afterwards, one at a time, by
    // UploadTextureMip. Backs the streamed load (ResourceUploader): the smallest
    // mip lands first and finer mips stream in over frames. The image is left in
    // ShaderReadOnly so it's bindable immediately; callers must not SAMPLE it
    // until the level they read has been uploaded (the demo binds a texture only
    // once its chain is complete — contents are undefined until then).
    public unsafe TextureHandle AllocateTexture2DMips(
        TextureDescription description,
        int mipCount,
        string? name = null)
    {
        if (mipCount < 1) throw new ArgumentOutOfRangeException(nameof(mipCount));
        var label = name ?? "texture2D.mips";
        var vkFormat = MapTextureFormat(description.Format);

        var imageCi = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = vkFormat,
            Extent = new Extent3D((uint)description.Width, (uint)description.Height, 1),
            MipLevels = (uint)mipCount,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Image image;
        ThrowIfNotSuccess(Vk.CreateImage(Device, in imageCi, null, &image), $"vkCreateImage({label})");

        Vk.GetImageMemoryRequirements(Device, image, out var memReq);
        var allocCi = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryTypeIndex(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        DeviceMemory memory;
        ThrowIfNotSuccess(Vk.AllocateMemory(Device, in allocCi, null, &memory), $"vkAllocateMemory({label})");
        ThrowIfNotSuccess(Vk.BindImageMemory(Device, image, memory, 0), $"vkBindImageMemory({label})");

        // Straight to ShaderReadOnly so it's bindable; UploadTextureMip flips
        // back to TransferDst per upload.
        var cmd = BeginSingleTimeCommands();
        TransitionImageLayout(cmd, image, mipCount, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal);
        EndSingleTimeCommands(cmd);

        var viewCi = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = vkFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, (uint)mipCount, 0, 1),
        };
        ImageView view;
        ThrowIfNotSuccess(Vk.CreateImageView(Device, in viewCi, null, &view), $"vkCreateImageView({label})");

        var entry = new VkTextureEntry
        {
            Image = image,
            Memory = memory,
            View = view,
            Sampler = GetOrCreateSampler(description.Sampler),
            Width = description.Width,
            Height = description.Height,
            MipCount = mipCount,
            Format = vkFormat,
            EngineFormat = description.Format,
            ByteSize = description.Format.TextureByteCount(description.Width, description.Height, mipCount),
            Name = label,
        };
        entry.Streamable = true; // starts Pending; UploadTextureMip fills the chain over frames
        var id = nextResourceId++;
        textureTable[id] = entry;
        return new TextureHandle(id);
    }

    // Upload one mip level of a texture created by AllocateTexture2DMips. Flips
    // the whole image to TransferDst, copies this level, flips back to
    // ShaderReadOnly. Safe to do per-mip because the texture isn't sampled until
    // its chain is complete (the upload runs on a single-time command that the
    // device waits on, so it never overlaps a sampling frame on this image).
    public unsafe void UploadTextureMip(TextureHandle handle, int mipLevel, ReadOnlySpan<byte> bytes)
    {
        if (mipLevel < 0) throw new ArgumentOutOfRangeException(nameof(mipLevel));
        var e = textureTable[handle.Id];
        if (mipLevel >= e.MipCount)
            throw new ArgumentOutOfRangeException(nameof(mipLevel), $"texture '{e.Name}' has {e.MipCount} mips.");

        // Residency bookkeeping: each level lands once, smallest-first. Clamp so
        // a defensive re-upload of a level can't push past the chain length.
        e.MipsUploaded = Math.Min(e.MipsUploaded + 1, e.MipCount);

        var staging = CreateHostVisibleBuffer(bytes, BufferUsageFlags.TransferSrcBit, $"{e.Name}.mip{mipLevel}.staging");
        var cmd = BeginSingleTimeCommands();
        TransitionImageLayout(cmd, e.Image, e.MipCount, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal);
        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = (uint)mipLevel,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(
                (uint)Math.Max(1, e.Width >> mipLevel), (uint)Math.Max(1, e.Height >> mipLevel), 1),
        };
        Vk.CmdCopyBufferToImage(cmd, staging.Buffer, e.Image, ImageLayout.TransferDstOptimal, 1, in region);
        TransitionImageLayout(cmd, e.Image, e.MipCount, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        EndSingleTimeCommands(cmd);
        DestroyVkBufferEntry(staging);
    }

    // Uploads a sampleable cubemap (6 faces, optional mip chain) from CPU
    // bytes. Data layout is face-major then mip-major: for face 0..5, the
    // tightly-packed mips 0..mipCount-1 (each mip sized faceSize>>level).
    // Used for IBL (procedural sky env + irradiance) — sampled as samplerCube.
    public unsafe TextureHandle CreateTextureCube(
        int faceSize, TextureFormat format, int mipCount,
        ReadOnlySpan<byte> data, SamplerDescription samplerDesc, string name)
    {
        var vkFormat = MapTextureFormat(format);

        // Validate total size = Σ over 6 faces of Σ over mips of mipByteCount.
        long expected = 0;
        for (var f = 0; f < 6; f++)
            for (var m = 0; m < mipCount; m++)
                expected += format.MipByteCount(faceSize >> m, faceSize >> m);
        if (data.Length != expected)
        {
            throw new ArgumentException(
                $"Cube '{name}' expected {expected} bytes ({faceSize}px, {mipCount} mips, 6 faces), got {data.Length}.",
                nameof(data));
        }

        var staging = CreateHostVisibleBuffer(data, BufferUsageFlags.TransferSrcBit, $"{name}.staging");

        var imageCi = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            Flags = ImageCreateFlags.CreateCubeCompatibleBit,
            ImageType = ImageType.Type2D,
            Format = vkFormat,
            Extent = new Extent3D((uint)faceSize, (uint)faceSize, 1),
            MipLevels = (uint)mipCount,
            ArrayLayers = 6,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Image image;
        ThrowIfNotSuccess(Vk.CreateImage(Device, in imageCi, null, &image), $"vkCreateImage({name}.cube)");

        Vk.GetImageMemoryRequirements(Device, image, out var memReq);
        var memTypeIdx = FindMemoryTypeIndex(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        var allocCi = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memTypeIdx,
        };
        DeviceMemory memory;
        ThrowIfNotSuccess(Vk.AllocateMemory(Device, in allocCi, null, &memory), $"vkAllocateMemory({name}.cube)");
        ThrowIfNotSuccess(Vk.BindImageMemory(Device, image, memory, 0), $"vkBindImageMemory({name}.cube)");

        var cmd = BeginSingleTimeCommands();
        TransitionImageLayout(cmd, image, mipCount, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, layerCount: 6);

        // One copy region per (face, mip). bufferOffset walks the data in the
        // same face-major/mip-major order the caller packed it.
        ulong offset = 0;
        for (var face = 0u; face < 6u; face++)
        {
            for (var mip = 0; mip < mipCount; mip++)
            {
                var dim = (uint)(faceSize >> mip);
                var region = new BufferImageCopy
                {
                    BufferOffset = offset,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = (uint)mip,
                        BaseArrayLayer = face,
                        LayerCount = 1,
                    },
                    ImageOffset = new Offset3D(0, 0, 0),
                    ImageExtent = new Extent3D(dim, dim, 1),
                };
                Vk.CmdCopyBufferToImage(cmd, staging.Buffer, image, ImageLayout.TransferDstOptimal, 1, in region);
                offset += (ulong)format.MipByteCount((int)dim, (int)dim);
            }
        }

        TransitionImageLayout(cmd, image, mipCount, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, layerCount: 6);
        EndSingleTimeCommands(cmd);

        var viewCi = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.TypeCube,
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
                LayerCount = 6,
            },
        };
        ImageView view;
        ThrowIfNotSuccess(Vk.CreateImageView(Device, in viewCi, null, &view), $"vkCreateImageView({name}.cube)");

        DestroyVkBufferEntry(staging);

        var entry = new VkTextureEntry
        {
            Image = image,
            Memory = memory,
            View = view,
            Sampler = GetOrCreateSampler(samplerDesc),
            Width = faceSize,
            Height = faceSize,
            MipCount = mipCount,
            Format = vkFormat,
            EngineFormat = format,
            ByteSize = format.TextureByteCount(faceSize, faceSize, mipCount) * 6,
            Name = name,
        };
        var cid = nextResourceId++;
        textureTable[cid] = entry;
        return new TextureHandle(cid);
    }

    // Single-mip RGBA16F cube (cooked .blixprobe env / irradiance). The Half[]
    // is face-major, RGBA-interleaved — exactly the byte layout the mipped
    // CreateTextureCube expects for one mip, so reinterpret and forward.
    public TextureHandle CreateTextureCubeHdr(
        int faceSize, ReadOnlySpan<Half> faces, SamplerDescription sampler, string? name = null)
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(faces);
        return CreateTextureCube(faceSize, TextureFormat.Rgba16F, 1, bytes, sampler, name ?? "cube.hdr");
    }

    // Mipped RGBA16F cube (cooked GGX-prefiltered specular). mipFaces is
    // mip-major (mipFaces[k] = all 6 faces at mip k, each face-major); the
    // upload path wants face-major then mip-major, so re-pack once.
    public TextureHandle CreateTextureCubeHdrMipped(
        int baseFaceSize, IReadOnlyList<Half[]> mipFaces, SamplerDescription sampler, string? name = null)
    {
        var mipCount = mipFaces.Count;
        long total = 0;
        for (var f = 0; f < 6; f++)
            for (var m = 0; m < mipCount; m++)
            {
                var s = Math.Max(1, baseFaceSize >> m);
                total += (long)s * s * 4 * sizeof(ushort); // RGBA16F = 4 halves
            }
        var packed = new byte[total];
        var dst = 0;
        for (var f = 0; f < 6; f++)
        {
            for (var m = 0; m < mipCount; m++)
            {
                var s = Math.Max(1, baseFaceSize >> m);
                var halvesPerFace = s * s * 4;
                var src = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                    mipFaces[m].AsSpan(f * halvesPerFace, halvesPerFace));
                src.CopyTo(packed.AsSpan(dst));
                dst += src.Length;
            }
        }
        return CreateTextureCube(baseFaceSize, TextureFormat.Rgba16F, mipCount, packed, sampler, name ?? "cube.hdr.mipped");
    }

    // 2D storage image (compute-writable + sampleable). No initial data — a
    // compute dispatch fills it; the dispatch's pre-barrier transitions it from
    // Undefined to General each frame. Usage STORAGE|SAMPLED.
    public unsafe TextureHandle CreateStorageTexture2D(
        int width, int height, TextureFormat format, SamplerDescription samplerDesc, string? name = null)
    {
        var vkFormat = MapTextureFormat(format);
        var imageCi = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = vkFormat,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Image image;
        ThrowIfNotSuccess(Vk.CreateImage(Device, in imageCi, null, &image), $"vkCreateImage({name}.storage2d)");

        Vk.GetImageMemoryRequirements(Device, image, out var memReq);
        var allocCi = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryTypeIndex(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        DeviceMemory memory;
        ThrowIfNotSuccess(Vk.AllocateMemory(Device, in allocCi, null, &memory), $"vkAllocateMemory({name}.storage2d)");
        ThrowIfNotSuccess(Vk.BindImageMemory(Device, image, memory, 0), $"vkBindImageMemory({name}.storage2d)");

        var viewCi = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = vkFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        ImageView view;
        ThrowIfNotSuccess(Vk.CreateImageView(Device, in viewCi, null, &view), $"vkCreateImageView({name}.storage2d)");

        var entry = new VkTextureEntry
        {
            Image = image,
            Memory = memory,
            View = view,
            Sampler = GetOrCreateSampler(samplerDesc),
            Width = width,
            Height = height,
            MipCount = 1,
            Format = vkFormat,
            EngineFormat = format,
            ByteSize = format.TextureByteCount(width, height, 1),
            Name = name ?? "storage2d",
        };
        var id = nextResourceId++;
        textureTable[id] = entry;
        return new TextureHandle(id);
    }

    // 3D storage image (compute-writable + sampleable) — e.g. a froxel/volume
    // grid. No initial data; the compute dispatch's pre-barrier moves it from
    // Undefined to General each frame. Usage STORAGE|SAMPLED, single mip/layer
    // (depth lives in the extent, not array layers — barriers use layerCount 1).
    public unsafe TextureHandle CreateStorageTexture3D(
        int width, int height, int depth, TextureFormat format, SamplerDescription samplerDesc, string? name = null) =>
        CreateImage3D(width, height, depth, format, samplerDesc,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit,
            uploadData: default, name ?? "storage3d");

    // Sampled 3D texture with initial data (RGBA-per-texel in the format's
    // layout, z-major then row-major). Fulfils the IGraphicsDevice stub.
    public unsafe TextureHandle CreateTexture3D(
        int width, int height, int depth, TextureFormat format,
        SamplerDescription sampler, ReadOnlySpan<byte> pixels, string? name = null)
    {
        var expected = format.MipByteCount(width, height) * depth;
        if (pixels.Length != expected)
        {
            throw new ArgumentException(
                $"Texture3D '{name}' expected {expected} bytes ({width}x{height}x{depth}), got {pixels.Length}.", nameof(pixels));
        }
        return CreateImage3D(width, height, depth, format, sampler,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, pixels, name ?? "texture3d");
    }

    // Shared 3D image creation. When uploadData is non-empty, stages + copies
    // it and leaves the image in ShaderReadOnly. A storage target with no
    // upload is also left in ShaderReadOnly (via a bare layout transition) so
    // it's valid to bind as a sampled image before its first compute write —
    // e.g. the froxel fog grid bound by the lit pass while fog is toggled off.
    // The compute pre-barrier uses oldLayout=Undefined, so it discards this
    // layout on the first dispatch regardless.
    private unsafe TextureHandle CreateImage3D(
        int width, int height, int depth, TextureFormat format, SamplerDescription samplerDesc,
        ImageUsageFlags usage, ReadOnlySpan<byte> uploadData, string name)
    {
        var vkFormat = MapTextureFormat(format);
        var imageCi = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type3D,
            Format = vkFormat,
            Extent = new Extent3D((uint)width, (uint)height, (uint)depth),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Image image;
        ThrowIfNotSuccess(Vk.CreateImage(Device, in imageCi, null, &image), $"vkCreateImage({name}.3d)");

        Vk.GetImageMemoryRequirements(Device, image, out var memReq);
        var allocCi = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryTypeIndex(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        DeviceMemory memory;
        ThrowIfNotSuccess(Vk.AllocateMemory(Device, in allocCi, null, &memory), $"vkAllocateMemory({name}.3d)");
        ThrowIfNotSuccess(Vk.BindImageMemory(Device, image, memory, 0), $"vkBindImageMemory({name}.3d)");

        if (!uploadData.IsEmpty)
        {
            var staging = CreateHostVisibleBuffer(uploadData, BufferUsageFlags.TransferSrcBit, $"{name}.staging");
            var cmd = BeginSingleTimeCommands();
            TransitionImageLayout(cmd, image, 1, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
            var region = new BufferImageCopy
            {
                BufferOffset = 0,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D((uint)width, (uint)height, (uint)depth),
            };
            Vk.CmdCopyBufferToImage(cmd, staging.Buffer, image, ImageLayout.TransferDstOptimal, 1, in region);
            TransitionImageLayout(cmd, image, 1, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
            EndSingleTimeCommands(cmd);
            DestroyVkBufferEntry(staging);
        }
        else if (usage.HasFlag(ImageUsageFlags.StorageBit))
        {
            // No initial data: still move out of Undefined so the image can be
            // bound as a sampled descriptor before its first compute write.
            var cmd = BeginSingleTimeCommands();
            TransitionImageLayout(cmd, image, 1, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal);
            EndSingleTimeCommands(cmd);
        }

        var viewCi = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type3D,
            Format = vkFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        ImageView view;
        ThrowIfNotSuccess(Vk.CreateImageView(Device, in viewCi, null, &view), $"vkCreateImageView({name}.3d)");

        var entry = new VkTextureEntry
        {
            Image = image,
            Memory = memory,
            View = view,
            Sampler = GetOrCreateSampler(samplerDesc),
            Width = width,
            Height = height,
            MipCount = 1,
            Format = vkFormat,
            EngineFormat = format,
            ByteSize = format.TextureByteCount(width, height, 1) * depth,
            Name = name,
        };
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

    // Pixel dimensions of a registered texture. The cheap hot-path lookup
    // SpriteBatch uses to map a pixel-space source rect to UVs per draw —
    // a direct table hit, versus allocating a full SnapshotResources walk
    // (which now surfaces texture entries, but is for diagnostics, not the
    // per-frame path). Returns false for unknown or destroyed handles.
    public bool TryGetTextureSize(TextureHandle handle, out int width, out int height)
    {
        if (textureTable.TryGetValue(handle.Id, out var entry))
        {
            width = entry.Width;
            height = entry.Height;
            return true;
        }
        width = 0;
        height = 0;
        return false;
    }

    // Register an externally-owned VkImage as a sampleable entry. Caller
    // retains image/memory/view lifetime — DestroyTexture only removes the
    // entry from the table, it does NOT destroy the underlying objects.
    internal TextureHandle RegisterExternalTexture(
        Image image,
        ImageView view,
        Sampler sampler,
        int width,
        int height,
        int mipCount,
        Format format,
        string name)
    {
        var entry = new VkTextureEntry
        {
            Image = image,
            Memory = default, // owned externally; cleared so DestroyVkTextureEntry won't free it
            View = view,      // same — owned externally; will be ignored by destroyer
            Sampler = sampler,
            Width = width,
            Height = height,
            MipCount = mipCount,
            Format = format,
            EngineFormat = MapVkFormatToEngine(format),
            ByteSize = 0, // externally-owned image; footprint accounted by the owner
            Name = name,
        };
        // Note: the destroy path in DestroyVkTextureEntry frees Image/Memory/View
        // unconditionally. For graph-owned resources, we register a SHADOW entry
        // that points at the same VkImage but with zeroed Memory/View so the
        // destroyer skips them. The graph still cleans up the real handles via
        // its own teardown path.
        // TRICK: we keep the Image and View handles non-zero here so sampling
        // works, but graph teardown destroys them BEFORE DestroyTexture is
        // called, leaving us with stale-but-zero handles to skip.
        // Simpler: use a separate registration path that opts out of destruction.
        var id = nextResourceId++;
        textureTable[id] = entry;
        return new TextureHandle(id);
    }

    // Companion to RegisterExternalTexture — removes the entry from the
    // table WITHOUT destroying the underlying Vulkan resources (graph owns
    // them). Use this in graph teardown instead of the standard
    // DestroyTexture (which frees image+memory+view).
    internal void UnregisterExternalTexture(TextureHandle handle)
    {
        textureTable.Remove(handle.Id);
    }

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
            // TransferSrc so each mip can be the blit source for the next when
            // generating the chain.
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit,
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

        if (mipCount > 1)
        {
            // Blit mip 0 down the chain; leaves every level ShaderReadOnly.
            GenerateMipmaps(cmd, image, width, height, mipCount);
        }
        else
        {
            TransitionImageLayout(cmd, image, mipCount, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        }
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
            EngineFormat = format,
            ByteSize = format.TextureByteCount(width, height, mipCount),
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
        ImageLayout newLayout,
        int layerCount = 1)
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
        else if (oldLayout == ImageLayout.ShaderReadOnlyOptimal && newLayout == ImageLayout.TransferDstOptimal)
        {
            // Streamed per-mip upload (UploadTextureMip): make the prior sampled
            // reads complete before we overwrite a mip level.
            srcAccess = AccessFlags.ShaderReadBit;
            dstAccess = AccessFlags.TransferWriteBit;
            srcStage = PipelineStageFlags.FragmentShaderBit;
            dstStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            // Bare layout move for an uninitialised image (e.g. a storage
            // target made sampleable before its first write). No prior writes
            // to make available; just establish the layout.
            srcAccess = 0;
            dstAccess = AccessFlags.ShaderReadBit;
            srcStage = PipelineStageFlags.TopOfPipeBit;
            dstStage = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit;
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
                LayerCount = (uint)layerCount,
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

    // --- Mip generation ----------------------------------------------------

    // Full chain length: floor(log2(max(w,h))) + 1.
    private static int ComputeMipCount(int width, int height)
    {
        var max = Math.Max(width, height);
        var levels = 1;
        while (max > 1) { max >>= 1; levels++; }
        return levels;
    }

    // True when the format can be the SOURCE of a linear-filtered blit — what
    // GenerateMipmaps needs to halve each level. Block-compressed (and some
    // float) formats may lack it, in which case the texture stays single-mip.
    private unsafe bool SupportsLinearBlit(Format format)
    {
        Vk.GetPhysicalDeviceFormatProperties(PhysicalDevice, format, out var props);
        return (props.OptimalTilingFeatures & FormatFeatureFlags.SampledImageFilterLinearBit) != 0;
    }

    // Generate mips 1..mipCount-1 by successively blitting the previous level
    // at half size. Expects every level currently in TransferDstOptimal (level
    // 0 holding the uploaded base). Leaves every level ShaderReadOnlyOptimal.
    private unsafe void GenerateMipmaps(CommandBuffer cmd, Image image, int width, int height, int mipCount)
    {
        int mipW = width, mipH = height;
        for (uint i = 1; i < (uint)mipCount; i++)
        {
            // Previous level becomes the blit source.
            var toSrc = MipBarrier(image, i - 1,
                ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal,
                AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
            Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
                DependencyFlags.None, 0, default(MemoryBarrier*), 0, default(BufferMemoryBarrier*), 1, in toSrc);

            int dstW = Math.Max(mipW / 2, 1);
            int dstH = Math.Max(mipH / 2, 1);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit, MipLevel = i - 1, BaseArrayLayer = 0, LayerCount = 1,
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit, MipLevel = i, BaseArrayLayer = 0, LayerCount = 1,
                },
            };
            blit.SrcOffsets[0] = new Offset3D(0, 0, 0);
            blit.SrcOffsets[1] = new Offset3D(mipW, mipH, 1);
            blit.DstOffsets[0] = new Offset3D(0, 0, 0);
            blit.DstOffsets[1] = new Offset3D(dstW, dstH, 1);
            Vk.CmdBlitImage(cmd,
                image, ImageLayout.TransferSrcOptimal,
                image, ImageLayout.TransferDstOptimal,
                1, in blit, Filter.Linear);

            // Source level is finished — promote it to shader-read.
            var toRead = MipBarrier(image, i - 1,
                ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.TransferReadBit, AccessFlags.ShaderReadBit);
            Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
                DependencyFlags.None, 0, default(MemoryBarrier*), 0, default(BufferMemoryBarrier*), 1, in toRead);

            mipW = dstW; mipH = dstH;
        }

        // The smallest level was never a blit source, so it's still TransferDst.
        var last = MipBarrier(image, (uint)mipCount - 1,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            DependencyFlags.None, 0, default(MemoryBarrier*), 0, default(BufferMemoryBarrier*), 1, in last);
    }

    private static ImageMemoryBarrier MipBarrier(Image image, uint mip,
        ImageLayout oldLayout, ImageLayout newLayout, AccessFlags src, AccessFlags dst) =>
        new()
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
                BaseMipLevel = mip, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1,
            },
            SrcAccessMask = src,
            DstAccessMask = dst,
        };

    // --- Single-time command buffer helpers --------------------------------

    // Allocate, begin recording, return. Caller submits via EndSingleTimeCommands.
    // Synchronous — fine because uploads happen at load time, not in the
    // render loop.
    internal unsafe CommandBuffer BeginSingleTimeCommands()
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

    internal unsafe void EndSingleTimeCommands(CommandBuffer cmd)
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

    internal static Format MapTextureFormat(TextureFormat f) => f switch
    {
        TextureFormat.Rgba8 => Format.R8G8B8A8Unorm,
        TextureFormat.Rgba8Srgb => Format.R8G8B8A8Srgb,
        TextureFormat.R8 => Format.R8Unorm,
        TextureFormat.Rgba16F => Format.R16G16B16A16Sfloat,
        TextureFormat.R11G11B10F => Format.B10G11R11UfloatPack32,
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
            // Anisotropic filtering only kicks in with linear minification and
            // needs the device feature. Cap at 8× (plenty for the grazing-angle
            // floor) clamped to the device limit. Nearest/compare samplers skip
            // it — anisotropy on a shadow-compare sampler is meaningless.
            AnisotropyEnable = AnisotropySupported && desc.MinFilter == TextureFilter.Linear && !desc.Compare,
            MaxAnisotropy = AnisotropySupported ? MathF.Min(8.0f, MaxAnisotropy) : 1.0f,
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
