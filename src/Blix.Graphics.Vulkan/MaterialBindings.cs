using System.Numerics;
using Silk.NET.Vulkan;

namespace Blix.Graphics.Vulkan;

// Carrier for a per-material descriptor set + its UBO/SSBO buffers.
//
// FramesInFlight=1 (default): one pool/set/buffer. Mutating live values
// while the frame is in flight is a validation hazard — write at setup.
//
// FramesInFlight>1: replicated per slot. Each frame the caller writes the
// matching slot via WriteBuffer(frameSlot, ...) and the bind path picks
// Sets[frameSlot]. CPU/GPU sync is provided by the swapchain's per-frame
// fence — no explicit fencing needed here.
public sealed class MaterialBindings
{
    private readonly VulkanGraphicsDevice device;
    private readonly List<DescriptorSetSlot> slots;

    public MaterialHandle Handle { get; }
    public string Name { get; }
    public int SetIndex { get; }
    public int FramesInFlight { get; }

    // Length == FramesInFlight; static materials carry single-element arrays.
    internal DescriptorPool[] Pools;
    internal DescriptorSet[] Sets;
    internal Dictionary<int, VulkanGraphicsDevice.VkBufferEntry>[] BuffersPerFrame;

    internal MaterialBindings(
        VulkanGraphicsDevice device,
        ShaderInterface shaderInterface,
        DescriptorSetLayout sharedLayout,
        int setIndex,
        int framesInFlight,
        MaterialHandle handle,
        string name)
    {
        if (framesInFlight < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(framesInFlight), framesInFlight, "framesInFlight must be >= 1.");
        }

        this.device = device;
        Handle = handle;
        Name = name;
        SetIndex = setIndex;
        FramesInFlight = framesInFlight;

        slots = new List<DescriptorSetSlot>();
        foreach (var s in shaderInterface.Slots)
        {
            if (s.Set == setIndex) slots.Add(s);
        }
        if (slots.Count == 0)
        {
            throw new InvalidOperationException(
                $"MaterialBindings '{name}' targets set {setIndex} but the shader interface declares no slots at that set.");
        }

        Pools = new DescriptorPool[framesInFlight];
        Sets = new DescriptorSet[framesInFlight];
        BuffersPerFrame = new Dictionary<int, VulkanGraphicsDevice.VkBufferEntry>[framesInFlight];
        for (var i = 0; i < framesInFlight; i++)
        {
            BuffersPerFrame[i] = new Dictionary<int, VulkanGraphicsDevice.VkBufferEntry>();
        }

        for (var i = 0; i < framesInFlight; i++)
        {
            AllocateDescriptorSet(sharedLayout, frameSlot: i);
            AllocateBufferSlots(frameSlot: i);
        }
    }

    private unsafe void AllocateDescriptorSet(DescriptorSetLayout sharedLayout, int frameSlot)
    {
        // One pool per frame slot; MaxSets=1 (one set per pool).
        var perTypeCount = new Dictionary<DescriptorType, uint>();
        foreach (var s in slots)
        {
            var t = MapDescriptorType(s.Type);
            perTypeCount.TryGetValue(t, out var current);
            perTypeCount[t] = current + (uint)s.Count;
        }
        var poolSizes = stackalloc DescriptorPoolSize[perTypeCount.Count];
        var idx = 0;
        foreach (var (type, count) in perTypeCount)
        {
            poolSizes[idx++] = new DescriptorPoolSize { Type = type, DescriptorCount = count };
        }
        var poolCi = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = (uint)perTypeCount.Count,
            PPoolSizes = poolSizes,
            MaxSets = 1,
        };
        DescriptorPool pool;
        VulkanGraphicsDevice.ThrowIfNotSuccess(
            device.Vk.CreateDescriptorPool(device.Device, in poolCi, null, &pool),
            $"vkCreateDescriptorPool(material:{Name},frame:{frameSlot})");
        Pools[frameSlot] = pool;

        var layout = sharedLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        DescriptorSet set;
        VulkanGraphicsDevice.ThrowIfNotSuccess(
            device.Vk.AllocateDescriptorSets(device.Device, in allocInfo, &set),
            $"vkAllocateDescriptorSets(material:{Name},frame:{frameSlot})");
        Sets[frameSlot] = set;
    }

    private unsafe void AllocateBufferSlots(int frameSlot)
    {
        foreach (var s in slots)
        {
            if (s.BlockLayout is not { } block) continue;
            var usage = s.Type == ShaderResourceType.StorageBuffer
                ? BufferUsageFlags.StorageBufferBit
                : BufferUsageFlags.UniformBufferBit;
            var buf = device.CreateHostVisibleBuffer(
                new byte[block.TotalSize], usage, $"{Name}.binding{s.Binding}.frame{frameSlot}.buf");
            BuffersPerFrame[frameSlot][s.Binding] = buf;

            var bufInfo = new DescriptorBufferInfo
            {
                Buffer = buf.Buffer,
                Offset = 0,
                Range = (ulong)block.TotalSize,
            };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = Sets[frameSlot],
                DstBinding = (uint)s.Binding,
                DstArrayElement = 0,
                DescriptorType = MapDescriptorType(s.Type),
                DescriptorCount = 1,
                PBufferInfo = &bufInfo,
            };
            device.Vk.UpdateDescriptorSets(device.Device, 1, in write, 0, default(CopyDescriptorSet*));
        }
    }

    // --- Public API --------------------------------------------------------

    // SetUniform / SetTexture broadcast to every frame slot — use these for
    // setup-time configuration. For per-frame data (e.g. bone palette), use
    // WriteBuffer(frameSlot, ...) into the current frame's slot only.

    public MaterialBindings SetUniform(int binding, string memberName, float value) =>
        WriteUniformBytes(binding, memberName, sizeof(float), span =>
        {
            unsafe { fixed (byte* p = span) *(float*)p = value; }
        });

    public MaterialBindings SetUniform(int binding, string memberName, Vector2 value) =>
        WriteUniformBytes(binding, memberName, 2 * sizeof(float), span =>
        {
            unsafe { fixed (byte* p = span) *(Vector2*)p = value; }
        });

    public MaterialBindings SetUniform(int binding, string memberName, Vector3 value) =>
        WriteUniformBytes(binding, memberName, 3 * sizeof(float), span =>
        {
            unsafe { fixed (byte* p = span) *(Vector3*)p = value; }
        });

    public MaterialBindings SetUniform(int binding, string memberName, Vector4 value) =>
        WriteUniformBytes(binding, memberName, 4 * sizeof(float), span =>
        {
            unsafe { fixed (byte* p = span) *(Vector4*)p = value; }
        });

    public MaterialBindings SetUniform(int binding, string memberName, Matrix4x4 value) =>
        WriteUniformBytes(binding, memberName, 16 * sizeof(float), span =>
        {
            unsafe { fixed (byte* p = span) *(Matrix4x4*)p = value; }
        });

    public unsafe MaterialBindings SetTexture(int binding, TextureHandle texture)
    {
        var slot = FindSlot(binding);
        if (slot is null)
        {
            throw new InvalidOperationException(
                $"MaterialBindings '{Name}' has no slot at binding {binding}.");
        }
        if (slot.Type is not (ShaderResourceType.SampledImage or ShaderResourceType.StorageImage or ShaderResourceType.Sampler))
        {
            throw new InvalidOperationException(
                $"MaterialBindings '{Name}' binding {binding} is {slot.Type}, not an image/sampler slot — use SetUniform instead.");
        }

        var tex = device.GetTexture(texture);
        var imgInfo = new DescriptorImageInfo
        {
            Sampler = tex.Sampler,
            ImageView = tex.View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        for (var i = 0; i < FramesInFlight; i++)
        {
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = Sets[i],
                DstBinding = (uint)binding,
                DstArrayElement = 0,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                PImageInfo = &imgInfo,
            };
            device.Vk.UpdateDescriptorSets(device.Device, 1, in write, 0, default(CopyDescriptorSet*));
        }
        return this;
    }

    // Whole-buffer overwrite at one frame slot. payload.Length must equal
    // the binding's BlockLayout.TotalSize.
    public unsafe MaterialBindings WriteBuffer(int frameSlot, int binding, ReadOnlySpan<byte> payload)
    {
        if (frameSlot < 0 || frameSlot >= FramesInFlight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameSlot), frameSlot,
                $"MaterialBindings '{Name}' was created with FramesInFlight={FramesInFlight}.");
        }
        var slot = FindSlot(binding);
        if (slot is null)
        {
            throw new InvalidOperationException(
                $"MaterialBindings '{Name}' has no slot at binding {binding}.");
        }
        if (slot.BlockLayout is not { } block)
        {
            throw new InvalidOperationException(
                $"MaterialBindings '{Name}' binding {binding} is {slot.Type}, not a buffer slot.");
        }
        if (payload.Length != block.TotalSize)
        {
            throw new ArgumentException(
                $"MaterialBindings '{Name}' binding {binding} expects {block.TotalSize} bytes, got {payload.Length}.",
                nameof(payload));
        }

        var buf = BuffersPerFrame[frameSlot][binding];
        void* ptr;
        VulkanGraphicsDevice.ThrowIfNotSuccess(
            device.Vk.MapMemory(device.Device, buf.Memory, 0, buf.Size, 0, &ptr),
            $"vkMapMemory(material:{Name}.binding{binding}.frame{frameSlot})");
        fixed (byte* src = payload)
        {
            System.Buffer.MemoryCopy(src, ptr, (long)buf.Size, payload.Length);
        }
        device.Vk.UnmapMemory(device.Device, buf.Memory);
        return this;
    }

    // --- Internals ---------------------------------------------------------

    private DescriptorSetSlot? FindSlot(int binding)
    {
        foreach (var s in slots) if (s.Binding == binding) return s;
        return null;
    }

    private unsafe MaterialBindings WriteUniformBytes(int binding, string memberName, int expectedSize, SpanWriter writer)
    {
        var slot = FindSlot(binding);
        if (slot is null)
        {
            throw new InvalidOperationException(
                $"MaterialBindings '{Name}' has no slot at binding {binding}.");
        }
        if (slot.BlockLayout is not { } block)
        {
            throw new InvalidOperationException(
                $"MaterialBindings '{Name}' binding {binding} is {slot.Type}, not a buffer slot — use SetTexture instead.");
        }
        UniformBlockMember? member = null;
        for (var i = 0; i < block.Members.Count; i++)
        {
            if (block.Members[i].Name == memberName) { member = block.Members[i]; break; }
        }
        if (member is null)
        {
            throw new InvalidOperationException(
                $"MaterialBindings '{Name}' binding {binding} has no member named '{memberName}' in its BlockLayout.");
        }
        if (member.Size < expectedSize)
        {
            throw new InvalidOperationException(
                $"MaterialBindings '{Name}' binding {binding} member '{memberName}' declared size {member.Size} but writer expects {expectedSize}.");
        }

        for (var i = 0; i < FramesInFlight; i++)
        {
            var buf = BuffersPerFrame[i][binding];
            void* ptr;
            VulkanGraphicsDevice.ThrowIfNotSuccess(
                device.Vk.MapMemory(device.Device, buf.Memory, 0, buf.Size, 0, &ptr),
                $"vkMapMemory(material:{Name}.binding{binding}.frame{i})");
            writer(new Span<byte>((byte*)ptr + member.Offset, expectedSize));
            device.Vk.UnmapMemory(device.Device, buf.Memory);
        }
        return this;
    }

    private delegate void SpanWriter(Span<byte> dst);

    private static DescriptorType MapDescriptorType(ShaderResourceType t) => t switch
    {
        ShaderResourceType.UniformBuffer => DescriptorType.UniformBuffer,
        ShaderResourceType.StorageBuffer => DescriptorType.StorageBuffer,
        ShaderResourceType.SampledImage => DescriptorType.CombinedImageSampler,
        ShaderResourceType.StorageImage => DescriptorType.StorageImage,
        ShaderResourceType.Sampler => DescriptorType.Sampler,
        _ => throw new InvalidOperationException($"Unknown ShaderResourceType {t}"),
    };

    internal unsafe void DestroyResources()
    {
        for (var i = 0; i < FramesInFlight; i++)
        {
            foreach (var buf in BuffersPerFrame[i].Values) device.DestroyVkBufferEntry(buf);
            if (Pools[i].Handle != 0) device.Vk.DestroyDescriptorPool(device.Device, Pools[i], null);
        }
    }
}
