using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Blix.Graphics.Vulkan;

// Buffer, shader-program, and pipeline creation. Memory strategy for this
// push is "everything host-visible + coherent" — fine for tiny static
// geometry (a triangle's worth of vertices). The follow-up here is a real
// staging-buffer path for device-local memory once we have anything large
// enough to care, but for visible-triangle-on-screen this is enough.
public sealed partial class VulkanGraphicsDevice
{
    // Set 2 is per-material by engine convention (see
    // docs/vulkan-reshape-shaderlab-target.md "set-by-lifetime"). The
    // program creates the layout for this set so the pipeline layout
    // stays contiguous, but skips per-frame descriptor / UBO allocation
    // — those live on MaterialBindings instances instead.
    internal const int MaterialOwnedSet = 2;

    private int nextResourceId = 1;
    private readonly Dictionary<int, VkBufferEntry> vertexBufferTable = new();
    private readonly Dictionary<int, VkBufferEntry> indexBufferTable = new();
    private readonly Dictionary<int, VkShaderProgramEntry> shaderProgramTable = new();
    private readonly Dictionary<int, VkPipelineEntry> pipelineTable = new();
    private readonly Dictionary<int, MaterialBindings> materialTable = new();

    internal sealed class VkBufferEntry
    {
        public VkBuffer Buffer;
        public DeviceMemory Memory;
        public ulong Size;
        public IndexType IndexType; // Only meaningful for index buffers.
        public string Name = string.Empty;
    }

    internal sealed class VkShaderProgramEntry
    {
        public ShaderModule Vertex;
        public ShaderModule Fragment;
        public string Name = string.Empty;
        // Declared binding contract. Always non-null post-2b.
        public ShaderInterface Interface = null!;
        // Per-set resources indexed by Vulkan set number. Length = MaxSet+1
        // (Sets is empty when the interface declares no slots). Sets that
        // aren't declared by the interface but lie in [0, MaxSet] carry an
        // "empty layout" entry so the pipeline layout stays contiguous —
        // Vulkan rejects gaps in pSetLayouts.
        public VkShaderSetResources?[] Sets = Array.Empty<VkShaderSetResources?>();
    }

    internal sealed class VkShaderSetResources
    {
        public int Set;
        public DescriptorSetLayout Layout;
        public DescriptorPool Pool; // default(DescriptorPool) for empty intermediate sets
        public DescriptorSet[] PerFrame = Array.Empty<DescriptorSet>();
        // Buffer slots in this set, keyed by binding number. Each entry is
        // one UBO/SSBO per frame slot. Image/sampler slots in this set have
        // no entry here (their descriptor write lands in 2c).
        public Dictionary<int, VkBufferEntry[]> BuffersPerBinding = new();
        // Slots declared at this set, for name lookup during uniform writes.
        public List<DescriptorSetSlot> Slots = new();
    }

    internal sealed class VkPipelineEntry
    {
        public Pipeline Pipeline;
        public PipelineLayout Layout;
        public string Name = string.Empty;
        public ShaderProgramHandle ShaderProgram;
    }

    // --- Buffer creation ---------------------------------------------------

    public VertexBufferHandle CreateVertexBuffer(VertexBufferData data, string? name = null)
    {
        var entry = CreateHostVisibleBuffer(
            data.Bytes,
            BufferUsageFlags.VertexBufferBit,
            name ?? "vertexBuffer");
        var id = nextResourceId++;
        vertexBufferTable[id] = entry;
        return new VertexBufferHandle(id);
    }

    public void UpdateVertexBuffer(VertexBufferHandle handle, ReadOnlySpan<byte> bytes, int byteOffset = 0)
    {
        if (!vertexBufferTable.TryGetValue(handle.Id, out var e))
        {
            throw new InvalidOperationException($"Unknown vertex buffer handle {handle.Id}.");
        }
        UploadToHostVisibleBuffer(e.Memory, bytes, (ulong)byteOffset);
    }

    public void DestroyVertexBuffer(VertexBufferHandle handle)
    {
        if (!vertexBufferTable.Remove(handle.Id, out var e)) return;
        DestroyVkBufferEntry(e);
    }

    // Re-upload bytes into an existing (host-visible) index buffer. Mirrors
    // UpdateVertexBuffer for dynamic index streaming — the ImGui overlay
    // rewrites its index buffer every frame. Vulkan-device-specific (not on
    // IGraphicsDevice) since only the Vulkan ImGui backend needs it today.
    // The buffer must have been created large enough; this does not resize.
    public void UpdateIndexBuffer(IndexBufferHandle handle, ReadOnlySpan<byte> bytes, int byteOffset = 0)
    {
        if (!indexBufferTable.TryGetValue(handle.Id, out var e))
        {
            throw new InvalidOperationException($"Unknown index buffer handle {handle.Id}.");
        }
        UploadToHostVisibleBuffer(e.Memory, bytes, (ulong)byteOffset);
    }

    public IndexBufferHandle CreateIndexBuffer(
        IReadOnlyList<ushort> indices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static,
        string? name = null)
    {
        var bytes = new byte[indices.Count * sizeof(ushort)];
        var shorts = MemoryMarshal.Cast<byte, ushort>(bytes);
        for (var i = 0; i < indices.Count; i++) shorts[i] = indices[i];
        var entry = CreateHostVisibleBuffer(bytes, BufferUsageFlags.IndexBufferBit, name ?? "indexBuffer.u16");
        entry.IndexType = IndexType.Uint16;
        var id = nextResourceId++;
        indexBufferTable[id] = entry;
        return new IndexBufferHandle(id);
    }

    public IndexBufferHandle CreateIndexBuffer(
        IReadOnlyList<uint> indices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static,
        string? name = null)
    {
        var bytes = new byte[indices.Count * sizeof(uint)];
        var uints = MemoryMarshal.Cast<byte, uint>(bytes);
        for (var i = 0; i < indices.Count; i++) uints[i] = indices[i];
        var entry = CreateHostVisibleBuffer(bytes, BufferUsageFlags.IndexBufferBit, name ?? "indexBuffer.u32");
        entry.IndexType = IndexType.Uint32;
        var id = nextResourceId++;
        indexBufferTable[id] = entry;
        return new IndexBufferHandle(id);
    }

    public void DestroyIndexBuffer(IndexBufferHandle handle)
    {
        if (!indexBufferTable.Remove(handle.Id, out var e)) return;
        DestroyVkBufferEntry(e);
    }

    internal unsafe VkBufferEntry CreateHostVisibleBuffer(ReadOnlySpan<byte> data, BufferUsageFlags usage, string name)
    {
        var ci = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)data.Length,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        VkBuffer buf;
        ThrowIfNotSuccess(Vk.CreateBuffer(Device, in ci, null, &buf), "vkCreateBuffer");

        Vk.GetBufferMemoryRequirements(Device, buf, out var req);
        var typeIndex = FindMemoryTypeIndex(
            req.MemoryTypeBits,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        var ai = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = typeIndex,
        };
        DeviceMemory mem;
        ThrowIfNotSuccess(Vk.AllocateMemory(Device, in ai, null, &mem), "vkAllocateMemory");
        ThrowIfNotSuccess(Vk.BindBufferMemory(Device, buf, mem, 0), "vkBindBufferMemory");

        var entry = new VkBufferEntry
        {
            Buffer = buf,
            Memory = mem,
            Size = (ulong)data.Length,
            Name = name,
        };
        UploadToHostVisibleBuffer(mem, data, 0);
        return entry;
    }

    private unsafe void UploadToHostVisibleBuffer(DeviceMemory mem, ReadOnlySpan<byte> data, ulong offset)
    {
        void* ptr;
        ThrowIfNotSuccess(Vk.MapMemory(Device, mem, offset, (ulong)data.Length, 0, &ptr), "vkMapMemory");
        data.CopyTo(new Span<byte>(ptr, data.Length));
        Vk.UnmapMemory(Device, mem);
    }

    internal unsafe void DestroyVkBufferEntry(VkBufferEntry e)
    {
        if (e.Buffer.Handle != 0) Vk.DestroyBuffer(Device, e.Buffer, null);
        if (e.Memory.Handle != 0) Vk.FreeMemory(Device, e.Memory, null);
    }

    internal uint FindMemoryTypeIndex(uint typeFilter, MemoryPropertyFlags required)
    {
        Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var props);
        for (uint i = 0; i < props.MemoryTypeCount; i++)
        {
            var supported = (typeFilter & (1u << (int)i)) != 0;
            var matches = (props.MemoryTypes[(int)i].PropertyFlags & required) == required;
            if (supported && matches) return i;
        }
        throw new InvalidOperationException($"No memory type satisfies typeFilter=0x{typeFilter:x} required={required}.");
    }

    // --- Shader programs ---------------------------------------------------

    // Caller passes raw SPIR-V words for each stage and the program's declared
    // ShaderInterface. We don't compile GLSL at runtime — pre-compiled .spv
    // from glslc is the dependency-light path for now. The runtime-compile
    // path (libshaderc binding) is a future concern when we want hot reload.
    //
    // The ShaderInterface drives descriptor-set generation: one
    // VkDescriptorSetLayout per declared set, one VkDescriptorPool per set,
    // per-frame descriptor sets, and per-frame UBOs for each UniformBuffer/
    // StorageBuffer slot. Image and sampler slots get declared in the layout
    // but their descriptor writes land later when the texture infrastructure
    // (Vector A 2c) lands.
    public ShaderProgramHandle CreateShaderProgramFromSpv(
        byte[] vertexSpv,
        byte[] fragmentSpv,
        ShaderInterface shaderInterface,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(shaderInterface);
        shaderInterface.Validate();

        var vert = CreateShaderModule(vertexSpv, $"{name ?? "shader"}.vert");
        var frag = CreateShaderModule(fragmentSpv, $"{name ?? "shader"}.frag");
        var entry = new VkShaderProgramEntry
        {
            Vertex = vert,
            Fragment = frag,
            Name = name ?? "shader",
            Interface = shaderInterface,
        };
        CreateSetResources(entry);

        var id = nextResourceId++;
        shaderProgramTable[id] = entry;
        return new ShaderProgramHandle(id);
    }

    internal const int MaxFramesInFlightConst = 2; // mirrors VulkanGraphicsDevice.Swapchain.cs constant

    // Materializes per-set descriptor-set layouts, pools, and per-frame
    // descriptor sets from entry.Interface.Slots. Buffer slots also get a
    // per-frame VkBufferEntry (host-visible UBO/SSBO) wired into the
    // descriptor sets via vkUpdateDescriptorSets. Image/sampler slots get
    // their binding declared in the layout but no descriptor write yet — the
    // texture-infrastructure step (Vector A 2c) fills those in.
    private unsafe void CreateSetResources(VkShaderProgramEntry entry)
    {
        var slots = entry.Interface.Slots;
        if (slots.Count == 0)
        {
            entry.Sets = Array.Empty<VkShaderSetResources?>();
            return;
        }

        // Vulkan requires pipeline-layout set indices to be contiguous from
        // set 0. Allocate Sets[0..maxSet] and create an "empty" layout entry
        // for any intermediate set the shader doesn't declare.
        var maxSet = 0;
        foreach (var s in slots) if (s.Set > maxSet) maxSet = s.Set;
        entry.Sets = new VkShaderSetResources?[maxSet + 1];

        var slotsBySet = new Dictionary<int, List<DescriptorSetSlot>>();
        foreach (var s in slots)
        {
            if (!slotsBySet.TryGetValue(s.Set, out var bucket))
            {
                bucket = new List<DescriptorSetSlot>();
                slotsBySet[s.Set] = bucket;
            }
            bucket.Add(s);
        }

        for (var setIdx = 0; setIdx <= maxSet; setIdx++)
        {
            var setSlots = slotsBySet.TryGetValue(setIdx, out var bucket) ? bucket : new List<DescriptorSetSlot>();
            entry.Sets[setIdx] = CreateOneSetResources(entry.Name, setIdx, setSlots);
        }
    }

    private unsafe VkShaderSetResources CreateOneSetResources(string programName, int setIdx, List<DescriptorSetSlot> setSlots)
    {
        var resources = new VkShaderSetResources { Set = setIdx, Slots = setSlots };

        // --- Descriptor set layout ------------------------------------------
        // Empty layout (no bindings) is valid — used for "gap" sets the
        // shader skips between declared sets, keeping pSetLayouts contiguous.
        var bindings = stackalloc DescriptorSetLayoutBinding[Math.Max(1, setSlots.Count)];
        for (var i = 0; i < setSlots.Count; i++)
        {
            var s = setSlots[i];
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)s.Binding,
                DescriptorType = MapDescriptorType(s.Type),
                DescriptorCount = (uint)s.Count,
                StageFlags = MapStageFlags(s.Stages),
            };
        }
        var setCi = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = (uint)setSlots.Count,
            PBindings = setSlots.Count > 0 ? bindings : null,
        };
        DescriptorSetLayout setLayout;
        ThrowIfNotSuccess(
            Vk.CreateDescriptorSetLayout(Device, in setCi, null, &setLayout),
            $"vkCreateDescriptorSetLayout({programName}.set{setIdx})");
        resources.Layout = setLayout;

        // Gap sets have nothing to allocate beyond the empty layout — they
        // exist only to keep pSetLayouts contiguous for the pipeline layout.
        if (setSlots.Count == 0) return resources;

        // Material-owned sets (set 2 by convention): the layout exists for
        // the pipeline layout but per-frame descriptor sets + UBOs are
        // allocated by MaterialBindings instances on demand. Bail before
        // creating pool/sets/buffers.
        if (setIdx == MaterialOwnedSet) return resources;

        // --- Pool sized to the union of this set's per-frame allocations ----
        // One pool size per distinct DescriptorType used in the set,
        // multiplied by MaxFramesInFlightConst (one descriptor per slot per
        // frame). Sampler arrays multiply by Count.
        var perTypeCount = new Dictionary<DescriptorType, uint>();
        foreach (var s in setSlots)
        {
            var t = MapDescriptorType(s.Type);
            perTypeCount.TryGetValue(t, out var current);
            perTypeCount[t] = current + (uint)(s.Count * MaxFramesInFlightConst);
        }
        var poolSizes = stackalloc DescriptorPoolSize[perTypeCount.Count];
        var poolIdx = 0;
        foreach (var (type, count) in perTypeCount)
        {
            poolSizes[poolIdx++] = new DescriptorPoolSize { Type = type, DescriptorCount = count };
        }
        var poolCi = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = (uint)perTypeCount.Count,
            PPoolSizes = poolSizes,
            MaxSets = (uint)MaxFramesInFlightConst,
        };
        DescriptorPool pool;
        ThrowIfNotSuccess(
            Vk.CreateDescriptorPool(Device, in poolCi, null, &pool),
            $"vkCreateDescriptorPool({programName}.set{setIdx})");
        resources.Pool = pool;

        // --- One descriptor set per frame, all sharing the same layout ------
        var layouts = stackalloc DescriptorSetLayout[MaxFramesInFlightConst];
        for (var i = 0; i < MaxFramesInFlightConst; i++) layouts[i] = setLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = (uint)MaxFramesInFlightConst,
            PSetLayouts = layouts,
        };
        var sets = new DescriptorSet[MaxFramesInFlightConst];
        fixed (DescriptorSet* p = sets)
        {
            ThrowIfNotSuccess(
                Vk.AllocateDescriptorSets(Device, in allocInfo, p),
                $"vkAllocateDescriptorSets({programName}.set{setIdx})");
        }
        resources.PerFrame = sets;

        // --- Per-frame UBO/SSBO allocation + descriptor write ---------------
        foreach (var s in setSlots)
        {
            if (s.BlockLayout is not { } block) continue; // image/sampler — descriptor write deferred to 2c
            var usage = s.Type == ShaderResourceType.StorageBuffer
                ? BufferUsageFlags.StorageBufferBit
                : BufferUsageFlags.UniformBufferBit;
            var buffers = new VkBufferEntry[MaxFramesInFlightConst];
            var bytes = new byte[block.TotalSize];
            for (var i = 0; i < MaxFramesInFlightConst; i++)
            {
                var buf = CreateHostVisibleBuffer(bytes, usage, $"{programName}.set{setIdx}.binding{s.Binding}.buf[{i}]");
                buffers[i] = buf;
                var bufInfo = new DescriptorBufferInfo
                {
                    Buffer = buf.Buffer,
                    Offset = 0,
                    Range = (ulong)block.TotalSize,
                };
                var write = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = sets[i],
                    DstBinding = (uint)s.Binding,
                    DstArrayElement = 0,
                    DescriptorType = MapDescriptorType(s.Type),
                    DescriptorCount = 1,
                    PBufferInfo = &bufInfo,
                };
                Vk.UpdateDescriptorSets(Device, 1, in write, 0, default(CopyDescriptorSet*));
            }
            resources.BuffersPerBinding[s.Binding] = buffers;
        }

        return resources;
    }

    internal static DescriptorType MapDescriptorType(ShaderResourceType t) => t switch
    {
        ShaderResourceType.UniformBuffer => DescriptorType.UniformBuffer,
        ShaderResourceType.StorageBuffer => DescriptorType.StorageBuffer,
        ShaderResourceType.SampledImage => DescriptorType.CombinedImageSampler,
        ShaderResourceType.StorageImage => DescriptorType.StorageImage,
        ShaderResourceType.Sampler => DescriptorType.Sampler,
        _ => throw new InvalidOperationException($"Unknown ShaderResourceType {t}"),
    };

    internal static ShaderStageFlags MapStageFlags(ShaderStages s)
    {
        var flags = ShaderStageFlags.None;
        if (s.HasFlag(ShaderStages.Vertex)) flags |= ShaderStageFlags.VertexBit;
        if (s.HasFlag(ShaderStages.Fragment)) flags |= ShaderStageFlags.FragmentBit;
        if (s.HasFlag(ShaderStages.Compute)) flags |= ShaderStageFlags.ComputeBit;
        return flags;
    }

    public void DestroyShaderProgram(ShaderProgramHandle handle)
    {
        if (!shaderProgramTable.Remove(handle.Id, out var e)) return;
        DestroyVkShaderProgramEntry(e);
    }

    private unsafe ShaderModule CreateShaderModule(byte[] spv, string name)
    {
        if (spv.Length % 4 != 0)
        {
            throw new InvalidOperationException($"SPIR-V module '{name}' has byte length {spv.Length}, not a multiple of 4.");
        }
        fixed (byte* p = spv)
        {
            var ci = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spv.Length,
                PCode = (uint*)p,
            };
            ShaderModule mod;
            ThrowIfNotSuccess(Vk.CreateShaderModule(Device, in ci, null, &mod), $"vkCreateShaderModule({name})");
            return mod;
        }
    }

    private unsafe void DestroyVkShaderProgramEntry(VkShaderProgramEntry e)
    {
        foreach (var sr in e.Sets)
        {
            if (sr is null) continue;
            foreach (var buffers in sr.BuffersPerBinding.Values)
                foreach (var b in buffers) DestroyVkBufferEntry(b);
            if (sr.Pool.Handle != 0) Vk.DestroyDescriptorPool(Device, sr.Pool, null);
            if (sr.Layout.Handle != 0) Vk.DestroyDescriptorSetLayout(Device, sr.Layout, null);
        }
        if (e.Vertex.Handle != 0) Vk.DestroyShaderModule(Device, e.Vertex, null);
        if (e.Fragment.Handle != 0) Vk.DestroyShaderModule(Device, e.Fragment, null);
    }

    // --- Pipelines ---------------------------------------------------------

    public unsafe PipelineHandle CreatePipeline(PipelineDescription description, string? name = null)
    {
        if (!shaderProgramTable.TryGetValue(description.ShaderProgram.Id, out var prog))
        {
            throw new InvalidOperationException($"Unknown shader program handle {description.ShaderProgram.Id}.");
        }

        // Pipeline layout pulls in every set layout the shader declared (sets
        // 0..maxSet) plus its push-constant ranges. Gap sets carry an empty
        // layout — Vulkan rejects gaps in pSetLayouts.
        var setLayouts = stackalloc DescriptorSetLayout[Math.Max(1, prog.Sets.Length)];
        var setCount = (uint)prog.Sets.Length;
        for (var i = 0; i < prog.Sets.Length; i++)
        {
            setLayouts[i] = prog.Sets[i] is { } sr ? sr.Layout : default;
        }

        var declaredRanges = prog.Interface.PushConstants;
        var pushRanges = stackalloc Silk.NET.Vulkan.PushConstantRange[Math.Max(1, declaredRanges.Count)];
        for (var i = 0; i < declaredRanges.Count; i++)
        {
            var r = declaredRanges[i];
            pushRanges[i] = new Silk.NET.Vulkan.PushConstantRange
            {
                StageFlags = MapStageFlags(r.Stages),
                Offset = (uint)r.Offset,
                Size = (uint)r.Size,
            };
        }

        var layoutCi = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = setCount,
            PSetLayouts = setCount > 0 ? setLayouts : null,
            PushConstantRangeCount = (uint)declaredRanges.Count,
            PPushConstantRanges = declaredRanges.Count > 0 ? pushRanges : null,
        };
        PipelineLayout layout;
        ThrowIfNotSuccess(Vk.CreatePipelineLayout(Device, in layoutCi, null, &layout), "vkCreatePipelineLayout");

        using var entryName = new Utf8Pin("main");
        var vertStage = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = prog.Vertex,
            PName = entryName.Ptr,
        };
        var fragStage = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = prog.Fragment,
            PName = entryName.Ptr,
        };
        var stages = stackalloc PipelineShaderStageCreateInfo[] { vertStage, fragStage };

        // Vertex input from VertexLayout.
        var binding = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = (uint)description.VertexLayout.Stride,
            InputRate = VertexInputRate.Vertex,
        };
        var attrs = stackalloc VertexInputAttributeDescription[description.VertexLayout.Attributes.Count];
        for (var i = 0; i < description.VertexLayout.Attributes.Count; i++)
        {
            var a = description.VertexLayout.Attributes[i];
            attrs[i] = new VertexInputAttributeDescription
            {
                Binding = 0,
                Location = (uint)a.Location,
                Offset = (uint)a.Offset,
                Format = a.Format switch
                {
                    VertexAttributeFormat.Float2 => Format.R32G32Sfloat,
                    VertexAttributeFormat.Float3 => Format.R32G32B32Sfloat,
                    VertexAttributeFormat.Float4 => Format.R32G32B32A32Sfloat,
                    VertexAttributeFormat.UByte4Norm => Format.R8G8B8A8Unorm,
                    _ => throw new InvalidOperationException($"Unknown vertex attribute format {a.Format}."),
                },
            };
        }
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &binding,
            VertexAttributeDescriptionCount = (uint)description.VertexLayout.Attributes.Count,
            PVertexAttributeDescriptions = attrs,
        };

        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = description.Topology switch
            {
                PrimitiveTopology.Triangles => Silk.NET.Vulkan.PrimitiveTopology.TriangleList,
                PrimitiveTopology.Lines => Silk.NET.Vulkan.PrimitiveTopology.LineList,
                _ => Silk.NET.Vulkan.PrimitiveTopology.TriangleList,
            },
            PrimitiveRestartEnable = false,
        };

        // Viewport + scissor are dynamic state — set per command-buffer so
        // a single pipeline survives window resize. Standard tutorial-grade
        // setup; cost is negligible vs the resize-recreate alternative.
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1,
        };

        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable = false,
            RasterizerDiscardEnable = false,
            PolygonMode = PolygonMode.Fill,
            CullMode = description.Rasterizer.CullMode switch
            {
                CullMode.Back => CullModeFlags.BackBit,
                CullMode.Front => CullModeFlags.FrontBit,
                _ => CullModeFlags.None,
            },
            FrontFace = description.Rasterizer.FrontFace == FrontFace.CounterClockwise
                ? Silk.NET.Vulkan.FrontFace.CounterClockwise
                : Silk.NET.Vulkan.FrontFace.Clockwise,
            LineWidth = 1.0f,
        };

        var multisample = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };

        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = description.Depth.Enabled,
            DepthWriteEnable = description.Depth.WriteEnabled,
            DepthCompareOp = description.Depth.Compare == DepthCompare.Less
                ? CompareOp.Less
                : CompareOp.LessOrEqual,
            DepthBoundsTestEnable = false,
            StencilTestEnable = false,
        };

        // Color blend: single attachment (default render pass). When MRT
        // surfaces land, this fans out per ColorBlends entry.
        var blendState = description.ColorBlends.Count > 0 ? description.ColorBlends[0] : BlendState.Disabled;
        var colorBlendAttachment = new PipelineColorBlendAttachmentState
        {
            BlendEnable = blendState.Enabled,
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit
                           | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            SrcColorBlendFactor = blendState.Mode == BlendMode.Additive ? BlendFactor.One : BlendFactor.SrcAlpha,
            DstColorBlendFactor = blendState.Mode == BlendMode.Additive ? BlendFactor.One : BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.Zero,
            AlphaBlendOp = BlendOp.Add,
        };
        var colorBlend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            LogicOpEnable = false,
            AttachmentCount = 1,
            PAttachments = &colorBlendAttachment,
        };

        var dynStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynStates,
        };

        var gpci = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisample,
            PDepthStencilState = &depthStencil,
            PColorBlendState = &colorBlend,
            PDynamicState = &dynamicState,
            Layout = layout,
            // Pipeline's render pass picks based on description.RenderTarget.
            // Null → swapchain DefaultRenderPass; non-null → the surface's
            // own render pass (different attachment formats need a different
            // render-pass compat group).
            RenderPass = description.RenderTarget is { } target
                ? renderSurfaceTable[target.Id].RenderPass
                : DefaultRenderPass,
            Subpass = 0,
        };

        Pipeline pipeline;
        ThrowIfNotSuccess(
            Vk.CreateGraphicsPipelines(Device, default, 1, in gpci, null, &pipeline),
            "vkCreateGraphicsPipelines");

        var entry = new VkPipelineEntry
        {
            Pipeline = pipeline,
            Layout = layout,
            Name = name ?? "pipeline",
            ShaderProgram = description.ShaderProgram,
        };
        var id = nextResourceId++;
        pipelineTable[id] = entry;
        return new PipelineHandle(id);
    }

    public void DestroyPipeline(PipelineHandle handle)
    {
        if (!pipelineTable.Remove(handle.Id, out var e)) return;
        DestroyVkPipelineEntry(e);
    }

    private unsafe void DestroyVkPipelineEntry(VkPipelineEntry e)
    {
        if (e.Pipeline.Handle != 0) Vk.DestroyPipeline(Device, e.Pipeline, null);
        if (e.Layout.Handle != 0) Vk.DestroyPipelineLayout(Device, e.Layout, null);
    }

    // --- Lookup helpers (used by command translation) ----------------------

    internal VkBufferEntry GetVertexBuffer(VertexBufferHandle h) => vertexBufferTable[h.Id];
    internal VkBufferEntry GetIndexBuffer(IndexBufferHandle h) => indexBufferTable[h.Id];
    internal VkPipelineEntry GetPipeline(PipelineHandle h) => pipelineTable[h.Id];
    internal MaterialBindings GetMaterial(MaterialHandle h) => materialTable[h.Id];

    // --- Materials ---------------------------------------------------------

    // Creates a MaterialBindings carrying the descriptor set + UBOs for one
    // set (typically set 2 = per-material). The program must have declared
    // at least one slot at the target setIndex. Returns an opaque handle
    // the draw call uses to bind the material at its set index.
    //
    // framesInFlight: 1 (default) = static set, write once at setup.
    // >1 = per-frame replicated: N pools + sets + buffers. Use
    // MaxFramesInFlight from this device for the canonical replication
    // count. The bind path picks the matching slot from CurrentFrameSlot;
    // the caller writes per-frame data via MaterialBindings.WriteBuffer.
    public MaterialBindings CreateMaterial(
        ShaderProgramHandle programHandle,
        int setIndex = MaterialOwnedSet,
        int framesInFlight = 1,
        string? name = null)
    {
        if (!shaderProgramTable.TryGetValue(programHandle.Id, out var prog))
        {
            throw new InvalidOperationException($"Unknown shader program handle: {programHandle.Id}");
        }
        if (setIndex < 0 || setIndex >= prog.Sets.Length || prog.Sets[setIndex] is not { } sr)
        {
            throw new InvalidOperationException(
                $"Shader program '{prog.Name}' declares no slots at set {setIndex} — cannot create material for it.");
        }

        var id = nextResourceId++;
        var handle = new MaterialHandle(id);
        var material = new MaterialBindings(
            this,
            prog.Interface,
            sr.Layout,
            setIndex,
            framesInFlight,
            handle,
            name ?? $"material{id}");
        materialTable[id] = material;
        return material;
    }

    public void DestroyMaterial(MaterialHandle handle)
    {
        if (!materialTable.Remove(handle.Id, out var mat)) return;
        mat.DestroyResources();
    }

    private void DestroyAllMaterials()
    {
        foreach (var m in materialTable.Values) m.DestroyResources();
        materialTable.Clear();
    }

    // --- Cleanup -----------------------------------------------------------

    private void DestroyAllResources()
    {
        foreach (var e in pipelineTable.Values) DestroyVkPipelineEntry(e);
        pipelineTable.Clear();
        foreach (var e in shaderProgramTable.Values) DestroyVkShaderProgramEntry(e);
        shaderProgramTable.Clear();
        foreach (var e in vertexBufferTable.Values) DestroyVkBufferEntry(e);
        vertexBufferTable.Clear();
        DestroyAllMaterials();
        DestroyAllRenderSurfaces();
        DestroyAllTextures();
        DestroyAllSamplers();
        foreach (var e in indexBufferTable.Values) DestroyVkBufferEntry(e);
        indexBufferTable.Clear();
    }
}
