using Silk.NET.Core.Contexts;

namespace Blix.Graphics.Vulkan;

// The graphics backend — the sole IGraphicsDevice implementation. On macOS
// this runs through MoltenVK (Vulkan-to-Metal translation); on Linux and
// Windows it talks to a native Vulkan driver. The IGraphicsDevice surface is
// kept backend-neutral so game code doesn't depend on Vulkan specifics.
//
// Implementation lives across partial classes:
// - VulkanGraphicsDevice.cs (this file) — public lifecycle (ctor / Dispose),
//   Info, DiagnosticsSnapshot, default surface size, Execute entry point.
// - VulkanGraphicsDevice.Init.cs — instance / physical device / logical
//   device / queue creation. (Next push.)
// - VulkanGraphicsDevice.Stubs.cs — IGraphicsDevice resource-creation
//   methods we haven't lit up yet. Throw NotImplementedException with a
//   clear "not yet" message rather than silently misbehaving.
public sealed partial class VulkanGraphicsDevice : IGraphicsDevice
{
    private bool disposed;
    private int defaultSurfaceWidth = 1;
    private int defaultSurfaceHeight = 1;
    private int currentGpuFrameNumber;
    private readonly List<VkGpuPassTiming> pendingGpuTimings = new();
    private VkCpuFrameTiming lastCpuFrameTiming = new(0, 0, 0);

    /// <summary>
    /// Whether the swapchain's depth survives its pass, so an overlay can test against it.
    /// </summary>
    /// <remarks>
    /// Off unless asked for, because it is not free: storing a full-resolution depth buffer out to memory
    /// every frame is bandwidth a tile-based GPU would otherwise never spend — the default pass discards
    /// depth precisely because a single pass to the swapchain has no later reader. An application with no
    /// diagnostics has no overlay to depth-test and should not pay for one.
    /// </remarks>
    private readonly bool preserveSwapchainDepth;

    public VulkanGraphicsDevice(
        IVkSurface windowSurface, int initialWidth, int initialHeight, bool preserveSwapchainDepth = false)
    {
        this.preserveSwapchainDepth = preserveSwapchainDepth;
        Info = new GraphicsDeviceInfo(
            Vendor: "(initializing)",
            Renderer: "Blix.Graphics.Vulkan",
            Version: "0.0.0",
            ShadingLanguageVersion: "SPIR-V 1.5");
        defaultSurfaceWidth = Math.Max(initialWidth, 1);
        defaultSurfaceHeight = Math.Max(initialHeight, 1);
        InitializeVulkan(windowSurface);
        InitializeSwapchain();
    }

    public GraphicsDeviceInfo Info { get; private set; }

    // Computed each read so the transient-arena gauges reflect the live ring
    // state. The error fields stay zero/empty here — Vulkan surfaces validation
    // failures through the [vk-ERR] log path, not this snapshot.
    public GraphicsDeviceDiagnostics DiagnosticsSnapshot
    {
        get
        {
            var (used, high, cap) = TransientArenaStats();
            return new GraphicsDeviceDiagnostics(
                FrameErrorCount: 0, LastErrorContext: string.Empty, LastErrorMessage: string.Empty,
                TransientArenaBytesUsed: used, TransientArenaHighWaterBytes: high, TransientArenaCapacityBytes: cap,
                PipelineCacheCount: pipelineCache.Count,
                PipelineCacheHits: pipelineCacheHits, PipelineCacheMisses: pipelineCacheMisses);
        }
    }

    public void SetDefaultRenderSurfaceSize(int width, int height)
    {
        var w = Math.Max(width, 1);
        var h = Math.Max(height, 1);
        if (w == defaultSurfaceWidth && h == defaultSurfaceHeight) return;
        defaultSurfaceWidth = w;
        defaultSurfaceHeight = h;
        MarkSwapchainOutOfDate();
    }

    // Project the backend's live resource tables into a backend-neutral
    // snapshot for the diagnostics overlay. A point-in-time copy — safe for the
    // debug surface to read without touching Vulkan handles. Allocates, so it's
    // a diagnostics call, not a per-frame path (hot lookups use TryGetTextureSize
    // and the internal Get* accessors).
    public ResourceRegistrySnapshot SnapshotResources()
    {
        // Render-surface colour attachments live in textureTable too (so present
        // passes can sample them); classify by membership so the snapshot
        // distinguishes user uploads from render targets.
        var surfaceColorIds = new HashSet<int>();
        foreach (var surf in renderSurfaceTable.Values)
        {
            foreach (var c in surf.ColorAttachments)
            {
                surfaceColorIds.Add(c.Id);
            }
        }

        var textures = new List<TextureEntry>(textureTable.Count);
        foreach (var (id, e) in textureTable)
        {
            var kind = surfaceColorIds.Contains(id) ? TextureKind.RenderSurfaceColor : TextureKind.UserUploaded;
            // Resident only when every distinct level has landed — a bitmask, so
            // repeated uploads of one level can't fake a full chain.
            var allMips = e.MipCount >= 64 ? ulong.MaxValue : (1UL << e.MipCount) - 1UL;
            var residency = !e.Streamable ? TextureResidency.Resident
                : e.UploadedMips == 0 ? TextureResidency.Pending
                : (e.UploadedMips & allMips) == allMips ? TextureResidency.Resident
                : TextureResidency.Streaming;
            textures.Add(new TextureEntry(
                new TextureHandle(id), e.Name, e.Width, e.Height, e.MipCount, e.EngineFormat, kind, e.ByteSize, residency));
        }

        var vertexBuffers = new List<VertexBufferEntry>(vertexBufferTable.Count);
        foreach (var (id, e) in vertexBufferTable)
        {
            vertexBuffers.Add(new VertexBufferEntry(new VertexBufferHandle(id), e.Name, (long)e.Size));
        }

        var indexBuffers = new List<IndexBufferEntry>(indexBufferTable.Count);
        foreach (var (id, e) in indexBufferTable)
        {
            indexBuffers.Add(new IndexBufferEntry(new IndexBufferHandle(id), e.Name, (long)e.Size));
        }

        var shaderPrograms = new List<ShaderProgramEntry>(shaderProgramTable.Count);
        foreach (var (id, e) in shaderProgramTable)
        {
            shaderPrograms.Add(new ShaderProgramEntry(new ShaderProgramHandle(id), e.Name));
        }

        var pipelines = new List<PipelineEntry>(pipelineTable.Count);
        foreach (var (id, e) in pipelineTable)
        {
            pipelines.Add(new PipelineEntry(new PipelineHandle(id), e.Name, e.ShaderProgram, e.IsCompute));
        }

        var renderSurfaces = new List<RenderSurfaceEntry>(renderSurfaceTable.Count);
        foreach (var (id, e) in renderSurfaceTable)
        {
            // Render-surface depth is a renderbuffer (not sampleable, not in
            // textureTable), so DepthTexture stays null until a sampleable
            // depth path exists.
            renderSurfaces.Add(new RenderSurfaceEntry(
                new RenderSurfaceHandle(id), e.Name, (int)e.Width, (int)e.Height, e.ColorAttachments, DepthTexture: null));
        }

        return new ResourceRegistrySnapshot(
            vertexBuffers, indexBuffers, textures, shaderPrograms, pipelines, renderSurfaces);
    }

    // Execute the per-frame command list: walks each pass, builds a
    // FrameDebugPacket for the runtime's diagnostics adapter, and (eventually)
    // records + submits Vk command buffers. Until the swapchain integration
    // lands the body is metadata-only — it produces a valid FrameDebugPacket so the
    // diagnostic surface stays observable, but no GPU work is issued.
    public FrameDebugPacket Execute(RenderCommandList commandList)
    {
        ThrowIfDisposed();
        currentGpuFrameNumber++;

        // Drive the Vulkan per-frame flow: wait/acquire/record/submit/present.
        // Returns false if swapchain needed recreating (this frame produced
        // nothing visible) — the metadata packet below still describes the
        // attempted work so the diagnostic surface stays continuous.
        AcquireRecordSubmitPresent(commandList);

        var passPackets = new List<FrameDebugPass>(commandList.Passes.Count);
        var totalDraws = 0;
        foreach (var pass in commandList.Passes)
        {
            var draws = new List<FrameDebugDraw>();
            foreach (var cmd in pass.Commands)
            {
                if (cmd is DrawIndexedCommand d)
                {
                    draws.Add(new FrameDebugDraw(
                        Pipeline: d.Pipeline,
                        VertexBuffer: d.VertexBuffer,
                        IndexBuffer: d.IndexBuffer,
                        IndexCount: d.IndexCount,
                        UniformNames: NamesOf(d.Uniforms),
                        Textures: DebugTextures(d.Textures),
                        Shader: PipelineName(d.Pipeline),
                        Uniforms: DebugUniforms(d.Uniforms),
                        PushConstants: DecodePushFloats(d.PushConstants)));
                }
            }

            var hasColorClear = false;
            for (var i = 0; i < pass.Description.ClearColors.Count; i++)
            {
                if (pass.Description.ClearColors[i].HasValue) { hasColorClear = true; break; }
            }

            passPackets.Add(new FrameDebugPass(
                Name: pass.Name,
                Target: pass.Description.Target,
                Width: defaultSurfaceWidth,
                Height: defaultSurfaceHeight,
                ClearedColor: hasColorClear,
                ClearedDepth: pass.Description.ClearDepth,
                Draws: draws,
                TargetName: pass.Description.Target.Id == 0 ? "swapchain" : $"surface#{pass.Description.Target.Id}"));
            totalDraws += draws.Count;
        }

        return new FrameDebugPacket(
            TotalPasses: passPackets.Count,
            TotalDraws: totalDraws,
            Passes: passPackets);
    }

    // --- Frame-packet enrichment (shader-pipeline inspector) ---------------
    // These resolve the per-draw inputs into human-readable form for the
    // overlay's Pipeline tab: shader name, live uniform values, decoded push
    // constants, and the texture binding map. All best-effort — a missing
    // handle yields a placeholder rather than throwing during diagnostics.

    private string PipelineName(PipelineHandle h)
        => pipelineTable.TryGetValue(h.Id, out var e) ? e.Name : $"pipeline#{h.Id}";

    private static IReadOnlyList<string> NamesOf(IReadOnlyList<ShaderUniform> uniforms)
    {
        if (uniforms.Count == 0) return Array.Empty<string>();
        var names = new string[uniforms.Count];
        for (var i = 0; i < uniforms.Count; i++) names[i] = uniforms[i].Name;
        return names;
    }

    private static IReadOnlyList<FrameDebugUniform> DebugUniforms(IReadOnlyList<ShaderUniform> uniforms)
    {
        if (uniforms.Count == 0) return Array.Empty<FrameDebugUniform>();
        var list = new FrameDebugUniform[uniforms.Count];
        for (var i = 0; i < uniforms.Count; i++)
        {
            list[i] = new FrameDebugUniform(uniforms[i].Name, FormatUniform(uniforms[i].Value));
        }
        return list;
    }

    private IReadOnlyList<FrameDebugTexture> DebugTextures(IReadOnlyList<ShaderTextureBinding> textures)
    {
        if (textures.Count == 0) return Array.Empty<FrameDebugTexture>();
        var list = new FrameDebugTexture[textures.Count];
        for (var i = 0; i < textures.Count; i++)
        {
            var t = textures[i];
            var resource = textureTable.TryGetValue(t.Texture.Id, out var e) ? e.Name : $"tex#{t.Texture.Id}";
            list[i] = new FrameDebugTexture(t.Name, t.Slot, t.Texture, resource);
        }
        return list;
    }

    // Push constants are almost always tightly-packed floats (matrices,
    // vec4 param blocks), so decode the byte payload as a float array; the UI
    // groups them (16 -> 4x4 matrix, 4 -> vec4, etc.).
    private static IReadOnlyList<float> DecodePushFloats(byte[]? push)
    {
        if (push is null || push.Length < 4) return Array.Empty<float>();
        var floats = new float[push.Length / 4];
        Buffer.BlockCopy(push, 0, floats, 0, floats.Length * 4);
        return floats;
    }

    private static string FormatUniform(ShaderUniformValue value) => value switch
    {
        FloatUniform f => f.Value.ToString("0.###"),
        Vector2Uniform v => $"({v.Value.X:0.###}, {v.Value.Y:0.###})",
        Vector3Uniform v => $"({v.Value.X:0.###}, {v.Value.Y:0.###}, {v.Value.Z:0.###})",
        Vector4Uniform v => $"({v.Value.X:0.###}, {v.Value.Y:0.###}, {v.Value.Z:0.###}, {v.Value.W:0.###})",
        Matrix4x4Uniform m => FormatMatrix(m.Value),
        Matrix4x4ArrayUniform a => $"mat4[{a.Value.Length}]",
        Vector3ArrayUniform a => $"vec3[{a.Value.Length}]",
        FloatArrayUniform a => $"float[{a.Value.Length}]",
        _ => value.GetType().Name,
    };

    private static string FormatMatrix(System.Numerics.Matrix4x4 m) =>
        $"{m.M11:0.##} {m.M12:0.##} {m.M13:0.##} {m.M14:0.##}\n" +
        $"{m.M21:0.##} {m.M22:0.##} {m.M23:0.##} {m.M24:0.##}\n" +
        $"{m.M31:0.##} {m.M32:0.##} {m.M33:0.##} {m.M34:0.##}\n" +
        $"{m.M41:0.##} {m.M42:0.##} {m.M43:0.##} {m.M44:0.##}";

    // <b>A non-draining accumulator beside the draining queue, because two consumers cannot share one
    // drain.</b> The Silk runtime already calls ConsumeAvailableGpuTimings every frame to feed the overlay's
    // gpu/passes scope, so anything else asking for them gets an empty list — which is how a game measuring
    // itself ends up reporting that the GPU costs nothing. Totals are cumulative and never cleared; a caller
    // wanting a window takes a snapshot and subtracts, which is exact and needs no ownership.
    private readonly Dictionary<string, (double TotalMs, long Samples)> gpuPassTotals = new();

    /// <summary>Cumulative resolved GPU time per pass, and how many resolutions it is over.</summary>
    public IReadOnlyDictionary<string, (double TotalMs, long Samples)> GpuPassTotals => gpuPassTotals;

    /// <summary>Whether the device resolves timestamp queries at all. False = every total stays empty.</summary>
    public bool GpuTimestampsSupported => timestampsSupported;

    private void AccumulateGpuPassTotal(string pass, double elapsedMs)
    {
        var current = gpuPassTotals.TryGetValue(pass, out var found) ? found : (0.0, 0L);
        gpuPassTotals[pass] = (current.Item1 + elapsedMs, current.Item2 + 1);
    }

    // Drains GPU timings that the device has finished resolving since the
    // last call. Empty until VkQueryPool wiring lands.
    public IReadOnlyList<VkGpuPassTiming> ConsumeAvailableGpuTimings()
    {
        if (pendingGpuTimings.Count == 0)
        {
            return Array.Empty<VkGpuPassTiming>();
        }
        var copy = pendingGpuTimings.ToArray();
        pendingGpuTimings.Clear();
        return copy;
    }

    // CPU-phase breakdown of the most recent frame's AcquireRecordSubmitPresent
    // (wait / encode / submit-present). Lets a diagnostics surface separate the
    // draw-encode cost — the part batching/indirect would move — from the GPU/
    // vsync wait that dominates `execute` when the renderer is GPU-bound.
    public VkCpuFrameTiming LastCpuFrameTiming => lastCpuFrameTiming;

    // Block until the GPU has finished all submitted work. Callers that destroy
    // resources outside the device's own teardown (e.g. the runtime disposing its
    // debug line-drawer / imgui renderer) must wait idle first, or validation flags
    // the still-in-flight resources as destroyed-in-use.
    public void WaitIdle()
    {
        if (!disposed && Vk is not null && Device.Handle != 0) Vk.DeviceWaitIdle(Device);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Vk is not null && Device.Handle != 0) Vk.DeviceWaitIdle(Device);
        DestroyAllResources();
        DestroyVulkan();
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(VulkanGraphicsDevice));
    }
}
