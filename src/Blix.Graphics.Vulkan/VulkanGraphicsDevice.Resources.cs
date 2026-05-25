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
    private int nextResourceId = 1;
    private readonly Dictionary<int, VkBufferEntry> vertexBufferTable = new();
    private readonly Dictionary<int, VkBufferEntry> indexBufferTable = new();
    private readonly Dictionary<int, VkShaderProgramEntry> shaderProgramTable = new();
    private readonly Dictionary<int, VkPipelineEntry> pipelineTable = new();

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
        // Descriptor infrastructure — non-default only when the program was
        // created with a UniformBlockLayout (i.e. it has a UBO at set=0,binding=0).
        public UniformBlockLayout? UniformLayout;
        public DescriptorSetLayout DescriptorSetLayout;
        public DescriptorPool DescriptorPool;
        public DescriptorSet[] DescriptorSetsPerFrame = Array.Empty<DescriptorSet>();
        public VkBufferEntry[] UniformBuffersPerFrame = Array.Empty<VkBufferEntry>();
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

    private unsafe VkBufferEntry CreateHostVisibleBuffer(ReadOnlySpan<byte> data, BufferUsageFlags usage, string name)
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

    private unsafe void DestroyVkBufferEntry(VkBufferEntry e)
    {
        if (e.Buffer.Handle != 0) Vk.DestroyBuffer(Device, e.Buffer, null);
        if (e.Memory.Handle != 0) Vk.FreeMemory(Device, e.Memory, null);
    }

    private uint FindMemoryTypeIndex(uint typeFilter, MemoryPropertyFlags required)
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

    // Caller passes raw SPIR-V words for each stage. We don't compile GLSL
    // at runtime — pre-compiled .spv from glslc is the dependency-light
    // path for now. The runtime-compile path (libshaderc binding) is a
    // future concern when we want hot reload.
    //
    // When uniformLayout is non-null, the shader is assumed to declare a
    // single uniform block at (set=0, binding=0) sized to layout.TotalSize.
    // We create the descriptor set layout, pool, per-frame descriptor sets,
    // and per-frame UBOs here so the draw path can route ShaderUniform
    // writes into the right UBO offset and bind the right descriptor set.
    public ShaderProgramHandle CreateShaderProgramFromSpv(
        byte[] vertexSpv,
        byte[] fragmentSpv,
        UniformBlockLayout? uniformLayout = null,
        string? name = null)
    {
        var vert = CreateShaderModule(vertexSpv, $"{name ?? "shader"}.vert");
        var frag = CreateShaderModule(fragmentSpv, $"{name ?? "shader"}.frag");
        var entry = new VkShaderProgramEntry
        {
            Vertex = vert,
            Fragment = frag,
            Name = name ?? "shader",
            UniformLayout = uniformLayout,
        };
        if (uniformLayout is not null)
        {
            CreateDescriptorInfrastructure(entry, uniformLayout);
        }
        var id = nextResourceId++;
        shaderProgramTable[id] = entry;
        return new ShaderProgramHandle(id);
    }

    private const int MaxFramesInFlightConst = 2; // mirrors VulkanGraphicsDevice.Swapchain.cs constant

    private unsafe void CreateDescriptorInfrastructure(VkShaderProgramEntry entry, UniformBlockLayout layout)
    {
        // Set layout: one UBO at binding 0, visible to vertex + fragment.
        // Real material systems will want this driven by the shader's
        // declared bindings (SPIR-V reflection or out-of-band material
        // descriptor). For the validation push this hard-coded single-UBO
        // shape is enough.
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
        };
        var setCi = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };
        DescriptorSetLayout setLayout;
        ThrowIfNotSuccess(Vk.CreateDescriptorSetLayout(Device, in setCi, null, &setLayout), "vkCreateDescriptorSetLayout");
        entry.DescriptorSetLayout = setLayout;

        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.UniformBuffer,
            DescriptorCount = (uint)MaxFramesInFlightConst,
        };
        var poolCi = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = (uint)MaxFramesInFlightConst,
        };
        DescriptorPool pool;
        ThrowIfNotSuccess(Vk.CreateDescriptorPool(Device, in poolCi, null, &pool), "vkCreateDescriptorPool");
        entry.DescriptorPool = pool;

        // Allocate descriptor sets — one per frame slot, all sharing the same layout.
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
            ThrowIfNotSuccess(Vk.AllocateDescriptorSets(Device, in allocInfo, p), "vkAllocateDescriptorSets");
        }
        entry.DescriptorSetsPerFrame = sets;

        // Create one UBO per frame slot and write its binding into the
        // corresponding descriptor set. UBOs stay host-visible+coherent;
        // per-draw uniform writes just memcpy into them.
        entry.UniformBuffersPerFrame = new VkBufferEntry[MaxFramesInFlightConst];
        var ubBytes = new byte[layout.TotalSize];
        for (var i = 0; i < MaxFramesInFlightConst; i++)
        {
            var ubo = CreateHostVisibleBuffer(ubBytes, BufferUsageFlags.UniformBufferBit, $"{entry.Name}.ubo[{i}]");
            entry.UniformBuffersPerFrame[i] = ubo;

            var bufInfo = new DescriptorBufferInfo
            {
                Buffer = ubo.Buffer,
                Offset = 0,
                Range = (ulong)layout.TotalSize,
            };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = sets[i],
                DstBinding = 0,
                DstArrayElement = 0,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo = &bufInfo,
            };
            Vk.UpdateDescriptorSets(Device, 1, in write, 0, default(CopyDescriptorSet*));
        }
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
        foreach (var ubo in e.UniformBuffersPerFrame) DestroyVkBufferEntry(ubo);
        if (e.DescriptorPool.Handle != 0) Vk.DestroyDescriptorPool(Device, e.DescriptorPool, null);
        if (e.DescriptorSetLayout.Handle != 0) Vk.DestroyDescriptorSetLayout(Device, e.DescriptorSetLayout, null);
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

        // Pipeline layout pulls in the shader program's descriptor set
        // layout when present. Programs created without a UniformBlockLayout
        // produce a zero-set-layout pipeline (e.g. the original hello-
        // triangle path, no uniforms).
        var setLayout = prog.DescriptorSetLayout;
        var hasSet = setLayout.Handle != 0;
        var layoutCi = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = hasSet ? 1u : 0u,
            PSetLayouts = hasSet ? &setLayout : null,
            PushConstantRangeCount = 0,
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
            RenderPass = DefaultRenderPass,
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

    // --- Cleanup -----------------------------------------------------------

    private void DestroyAllResources()
    {
        foreach (var e in pipelineTable.Values) DestroyVkPipelineEntry(e);
        pipelineTable.Clear();
        foreach (var e in shaderProgramTable.Values) DestroyVkShaderProgramEntry(e);
        shaderProgramTable.Clear();
        foreach (var e in vertexBufferTable.Values) DestroyVkBufferEntry(e);
        vertexBufferTable.Clear();
        foreach (var e in indexBufferTable.Values) DestroyVkBufferEntry(e);
        indexBufferTable.Clear();
    }
}
