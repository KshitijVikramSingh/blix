using System.Numerics;
using Silk.NET.Vulkan;

namespace Blix.Graphics.Vulkan;

// Per-material descriptor-set carrier (set 2 by convention from
// docs/vulkan-reshape-shaderlab-target.md). One instance owns ONE static
// VkDescriptorSet (not per-frame replicated) plus host-visible UBO/SSBO
// buffers for any buffer slots declared at its set index. Constructed
// via VulkanGraphicsDevice.CreateMaterial; values written via SetUniform
// / SetTexture; the handle is passed to RenderPassBuilder.DrawIndexed.
//
// Static-set design — no per-frame replication. The descriptor set is
// written once during setup (SetUniform / SetTexture calls happen at
// OnLoad, not mid-frame) and bound unchanged thereafter. Mutating a
// MaterialBindings while a frame using it is in flight is a Vulkan
// validation hazard — to change values safely, create a new instance.
//
// Per-slot addressing: binding number identifies the slot inside the
// set (per F-002: names live in BlockLayout for UBO members, not for
// descriptor lookup). SetUniform takes (binding, memberName, value)
// where memberName is the field inside the slot's BlockLayout.
public sealed class MaterialBindings
{
    private readonly VulkanGraphicsDevice device;
    private readonly List<DescriptorSetSlot> slots;

    public MaterialHandle Handle { get; }
    public string Name { get; }
    public int SetIndex { get; }

    internal DescriptorPool Pool;
    internal DescriptorSet Set;
    internal Dictionary<int, VulkanGraphicsDevice.VkBufferEntry> Buffers = new();

    internal MaterialBindings(
        VulkanGraphicsDevice device,
        ShaderInterface shaderInterface,
        DescriptorSetLayout sharedLayout,
        int setIndex,
        MaterialHandle handle,
        string name)
    {
        this.device = device;
        Handle = handle;
        Name = name;
        SetIndex = setIndex;

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

        AllocateDescriptorSet(sharedLayout);
        AllocateBufferSlots();
    }

    private unsafe void AllocateDescriptorSet(DescriptorSetLayout sharedLayout)
    {
        // Pool sized for exactly this material's slots (one descriptor per
        // slot, possibly with Count>1 for sampler arrays). MaxSets=1 because
        // we allocate the single material set up front.
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
            $"vkCreateDescriptorPool(material:{Name})");
        Pool = pool;

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
            $"vkAllocateDescriptorSets(material:{Name})");
        Set = set;
    }

    private unsafe void AllocateBufferSlots()
    {
        foreach (var s in slots)
        {
            if (s.BlockLayout is not { } block) continue;
            var usage = s.Type == ShaderResourceType.StorageBuffer
                ? BufferUsageFlags.StorageBufferBit
                : BufferUsageFlags.UniformBufferBit;
            var buf = device.CreateHostVisibleBuffer(
                new byte[block.TotalSize], usage, $"{Name}.binding{s.Binding}.buf");
            Buffers[s.Binding] = buf;

            var bufInfo = new DescriptorBufferInfo
            {
                Buffer = buf.Buffer,
                Offset = 0,
                Range = (ulong)block.TotalSize,
            };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = Set,
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
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = Set,
            DstBinding = (uint)binding,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imgInfo,
        };
        device.Vk.UpdateDescriptorSets(device.Device, 1, in write, 0, default(CopyDescriptorSet*));
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

        var buf = Buffers[binding];
        void* ptr;
        VulkanGraphicsDevice.ThrowIfNotSuccess(
            device.Vk.MapMemory(device.Device, buf.Memory, 0, buf.Size, 0, &ptr),
            $"vkMapMemory(material:{Name}.binding{binding})");
        writer(new Span<byte>((byte*)ptr + member.Offset, expectedSize));
        device.Vk.UnmapMemory(device.Device, buf.Memory);
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
        foreach (var buf in Buffers.Values) device.DestroyVkBufferEntry(buf);
        if (Pool.Handle != 0) device.Vk.DestroyDescriptorPool(device.Device, Pool, null);
    }
}
