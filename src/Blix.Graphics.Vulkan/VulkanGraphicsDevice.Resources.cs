using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Blix.Graphics.Vulkan;

// Buffer, shader-program, and pipeline creation. All buffers are
// host-visible + coherent today — staging path for device-local memory
// is a future step once geometry sizes warrant it.
public sealed partial class VulkanGraphicsDevice
{
    // Set 2's layout is owned by the shader program (for pipeline-layout
    // contiguity), but its descriptors + UBO live on MaterialBindings
    // instances. Other set indices flow through the per-draw transient pool.
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
        public ShaderModule Compute;   // set for compute-only programs
        public string Name = string.Empty;
        public ShaderInterface Interface = null!;
        // Indexed by Vulkan set number. Sets the interface skips between
        // declared ones carry an empty-layout placeholder so pSetLayouts
        // stays contiguous (Vulkan rejects gaps).
        public VkShaderSetResources?[] Sets = Array.Empty<VkShaderSetResources?>();
        // Lazily-built VkPipelineLayout, shared by every pipeline derived from
        // this program (the layout depends only on the interface, not on
        // depth/blend/raster/target). Built once via GetOrBuildLayout; owned by
        // the program and destroyed at program teardown — a pipeline can't
        // outlive its program, so this lifetime is strictly ⊇ every pipeline's.
        // Handle==0 means "not built yet".
        public PipelineLayout CachedLayout;
    }

    internal sealed class VkShaderSetResources
    {
        public int Set;
        public DescriptorSetLayout Layout;
        // UBO/SSBO storage per binding × frames-in-flight. Image/sampler
        // slots have no entry here — their descriptors are written from the
        // draw's textures into a transient set per draw.
        public Dictionary<int, VkBufferEntry[]> BuffersPerBinding = new();
        public List<DescriptorSetSlot> Slots = new();
    }

    internal sealed class VkPipelineEntry
    {
        public Pipeline Pipeline;
        public PipelineLayout Layout;
        public string Name = string.Empty;
        public ShaderProgramHandle ShaderProgram;
        // Bind point this pipeline was created for. Guards against binding a
        // compute pipeline for a draw or a graphics pipeline for a dispatch.
        public bool IsCompute;
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

    // Pre-compiled SPIR-V only — no runtime GLSL→SPIR-V (libshaderc) path
    // until we want hot reload.
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

    // Compute-only shader program. Single compute stage; same descriptor-set
    // layout machinery as graphics (the interface declares storage images /
    // SSBOs / UBOs the compute shader binds). Pair with CreateComputePipeline.
    public ShaderProgramHandle CreateComputeShaderProgramFromSpv(
        byte[] computeSpv, ShaderInterface shaderInterface, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(shaderInterface);
        shaderInterface.Validate();

        var comp = CreateShaderModule(computeSpv, $"{name ?? "compute"}.comp");
        var entry = new VkShaderProgramEntry
        {
            Compute = comp,
            Name = name ?? "compute",
            Interface = shaderInterface,
        };
        CreateSetResources(entry);

        var id = nextResourceId++;
        shaderProgramTable[id] = entry;
        return new ShaderProgramHandle(id);
    }

    // Must equal Swapchain.cs MaxFramesInFlight — UBO storage is sized off
    // this, and the transient pool count comes from here.
    internal const int MaxFramesInFlightConst = 2;

    // Builds the per-set DescriptorSetLayout + per-frame UBO storage for
    // every set the interface declares (plus empty-layout placeholders for
    // gaps, so pSetLayouts stays contiguous).
    private unsafe void CreateSetResources(VkShaderProgramEntry entry)
    {
        var slots = entry.Interface.Slots;
        if (slots.Count == 0)
        {
            entry.Sets = Array.Empty<VkShaderSetResources?>();
            return;
        }

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

        // Gap sets carry only the empty layout (pSetLayouts contiguity).
        if (setSlots.Count == 0) return resources;
        // Material sets own their own UBOs + descriptors via MaterialBindings.
        if (setIdx == MaterialOwnedSet) return resources;

        foreach (var s in setSlots)
        {
            if (s.BlockLayout is not { } block) continue;
            var usage = s.Type == ShaderResourceType.StorageBuffer
                ? BufferUsageFlags.StorageBufferBit
                : BufferUsageFlags.UniformBufferBit;
            var buffers = new VkBufferEntry[MaxFramesInFlightConst];
            var bytes = new byte[block.TotalSize];
            for (var i = 0; i < MaxFramesInFlightConst; i++)
            {
                buffers[i] = CreateHostVisibleBuffer(bytes, usage, $"{programName}.set{setIdx}.binding{s.Binding}.buf[{i}]");
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
            if (sr.Layout.Handle != 0) Vk.DestroyDescriptorSetLayout(Device, sr.Layout, null);
        }
        if (e.Vertex.Handle != 0) Vk.DestroyShaderModule(Device, e.Vertex, null);
        if (e.Fragment.Handle != 0) Vk.DestroyShaderModule(Device, e.Fragment, null);
        if (e.Compute.Handle != 0) Vk.DestroyShaderModule(Device, e.Compute, null);
        // Pipeline layout cached on the program (shared by all its pipelines).
        if (e.CachedLayout.Handle != 0) Vk.DestroyPipelineLayout(Device, e.CachedLayout, null);
    }

    // --- Pipelines ---------------------------------------------------------

    // Pipeline layout (descriptor set layouts + push-constant ranges) from a
    // program's interface. Shared by graphics + compute pipeline creation.
    private unsafe PipelineLayout BuildPipelineLayout(VkShaderProgramEntry prog)
    {
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
        return layout;
    }

    // Layout is a pure function of the program's interface, yet CreatePipeline ran
    // it per call. Build once, cache on the program, reuse for every pipeline
    // (graphics or compute) derived from it. The program owns the lifetime.
    private PipelineLayout GetOrBuildLayout(VkShaderProgramEntry prog)
    {
        if (prog.CachedLayout.Handle == 0)
        {
            prog.CachedLayout = BuildPipelineLayout(prog);
        }
        return prog.CachedLayout;
    }

    // Compute pipeline from a compute shader program (entry point "main").
    public unsafe PipelineHandle CreateComputePipeline(ShaderProgramHandle program, string? name = null)
    {
        if (!shaderProgramTable.TryGetValue(program.Id, out var prog))
        {
            throw new InvalidOperationException($"Unknown shader program handle {program.Id}.");
        }
        if (prog.Compute.Handle == 0)
        {
            throw new InvalidOperationException($"Shader program '{prog.Name}' is not a compute program.");
        }

        var layout = GetOrBuildLayout(prog);
        using var entryName = new Utf8Pin("main");
        var stage = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ComputeBit,
            Module = prog.Compute,
            PName = entryName.Ptr,
        };
        var ci = new ComputePipelineCreateInfo
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = stage,
            Layout = layout,
        };
        Pipeline pipeline;
        ThrowIfNotSuccess(
            Vk.CreateComputePipelines(Device, default, 1, in ci, null, &pipeline),
            $"vkCreateComputePipelines({name ?? prog.Name})");

        var entry = new VkPipelineEntry
        {
            Pipeline = pipeline,
            Layout = layout,
            Name = name ?? prog.Name,
            ShaderProgram = program,
            IsCompute = true,
        };
        var id = nextResourceId++;
        pipelineTable[id] = entry;
        return new PipelineHandle(id);
    }

    // Value-equatable cache key for a graphics PipelineDescription. PipelineDescription
    // is a record, but its ColorBlends is an IReadOnlyList → record equality compares
    // it by REFERENCE, so two structurally-identical descriptions built with separate
    // blend arrays would miss the cache. This struct flattens the value-relevant fields
    // and compares the blend list element-by-element.
    private readonly struct PipelineKey : IEquatable<PipelineKey>
    {
        private readonly int shaderProgramId;
        private readonly VertexLayout vertexLayout;
        private readonly PrimitiveTopology topology;
        private readonly DepthState depth;
        private readonly RasterizerState rasterizer;
        private readonly int renderTargetId;   // -1 == swapchain (null target)
        private readonly bool alphaToCoverage;
        private readonly BlendState[] blends;

        public PipelineKey(PipelineDescription d)
        {
            shaderProgramId = d.ShaderProgram.Id;
            vertexLayout = d.VertexLayout;
            topology = d.Topology;
            depth = d.Depth;
            rasterizer = d.Rasterizer;
            renderTargetId = d.RenderTarget?.Id ?? -1;
            alphaToCoverage = d.AlphaToCoverage;
            blends = d.ColorBlends.ToArray();
        }

        public bool Equals(PipelineKey other)
            => shaderProgramId == other.shaderProgramId
            && vertexLayout == other.vertexLayout
            && topology == other.topology
            && depth == other.depth
            && rasterizer == other.rasterizer
            && renderTargetId == other.renderTargetId
            && alphaToCoverage == other.alphaToCoverage
            && blends.AsSpan().SequenceEqual(other.blends);

        public override bool Equals(object? obj) => obj is PipelineKey k && Equals(k);

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(shaderProgramId);
            h.Add(vertexLayout);
            h.Add(topology);
            h.Add(depth);
            h.Add(rasterizer);
            h.Add(renderTargetId);
            h.Add(alphaToCoverage);
            foreach (var b in blends) h.Add(b);
            return h.ToHashCode();
        }
    }

    private readonly Dictionary<PipelineKey, PipelineHandle> pipelineCache = new();
    private int pipelineCacheHits;
    private int pipelineCacheMisses;

    // Opt-in cached pipeline creation: returns the SAME handle for a structurally
    // equal description, creating one only on a miss. Unlike CreatePipeline (which
    // always creates a fresh pipeline the caller owns + destroys), cached pipelines
    // are owned by the device and live until teardown — so callers using this MUST
    // NOT DestroyPipeline the result while another caller may still hold it.
    // Compute pipelines have no PipelineDescription and stay on CreateComputePipeline.
    public PipelineHandle GetOrCreatePipeline(PipelineDescription description, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(description);
        var key = new PipelineKey(description);
        if (pipelineCache.TryGetValue(key, out var existing))
        {
            pipelineCacheHits++;
            return existing;
        }
        pipelineCacheMisses++;
        var handle = CreatePipeline(description, name);
        pipelineCache[key] = handle;
        return handle;
    }

    public unsafe PipelineHandle CreatePipeline(PipelineDescription description, string? name = null)
    {
        if (!shaderProgramTable.TryGetValue(description.ShaderProgram.Id, out var prog))
        {
            throw new InvalidOperationException($"Unknown shader program handle {description.ShaderProgram.Id}.");
        }

        var layout = GetOrBuildLayout(prog);

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

        // Viewport + scissor are dynamic so resize doesn't recreate pipelines.
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

        // rasterizationSamples must match the target render pass. Offscreen
        // surfaces carry their sample count (MSAA passes); the swapchain
        // default pass is single-sample.
        var rasterSamples = description.RenderTarget is { } rt && renderSurfaceTable.TryGetValue(rt.Id, out var rtEntry)
            ? rtEntry.Samples
            : SampleCountFlags.Count1Bit;
        var multisample = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = rasterSamples,
            // Alpha-to-coverage: the lit mask pipelines enable this so foliage
            // cutout edges antialias against the MSAA samples. No-op at 1×.
            AlphaToCoverageEnable = description.AlphaToCoverage,
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

        // TODO MRT: when surfaces grow multiple attachments, fan out per ColorBlends entry.
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
            // Null target → swapchain default pass; offscreen surfaces carry
            // their own pass (different attachment formats = different compat).
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
        // Evict any cache entry pointing at this handle so a later GetOrCreatePipeline
        // never serves a destroyed pipeline. The cache is tiny (one entry per distinct
        // description), so the reverse scan is cheap.
        if (pipelineCache.Count > 0)
        {
            foreach (var kv in pipelineCache)
            {
                if (kv.Value.Id == handle.Id) { pipelineCache.Remove(kv.Key); break; }
            }
        }
        DestroyVkPipelineEntry(e);
    }

    private unsafe void DestroyVkPipelineEntry(VkPipelineEntry e)
    {
        if (e.Pipeline.Handle != 0) Vk.DestroyPipeline(Device, e.Pipeline, null);
        // The layout is owned by the shader program (CachedLayout), shared across
        // pipelines, and freed in DestroyVkShaderProgramEntry — not here.
    }

    // --- Lookup helpers (used by command translation) ----------------------

    internal VkBufferEntry GetVertexBuffer(VertexBufferHandle h) => vertexBufferTable[h.Id];
    internal VkBufferEntry GetIndexBuffer(IndexBufferHandle h) => indexBufferTable[h.Id];
    internal VkPipelineEntry GetPipeline(PipelineHandle h) => pipelineTable[h.Id];
    internal MaterialBindings GetMaterial(MaterialHandle h) => materialTable[h.Id];

    // --- Materials ---------------------------------------------------------

    // framesInFlight=1: static set, written once at setup. >1: per-frame
    // replicated (use MaxFramesInFlightCount) — caller writes the matching
    // slot each frame via MaterialBindings.WriteBuffer.
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
        DestroyIndirectBuffers();
        DestroyTransientDescriptorPools();
    }
}
