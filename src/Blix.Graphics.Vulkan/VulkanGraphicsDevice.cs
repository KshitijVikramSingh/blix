using Silk.NET.Core.Contexts;

namespace Blix.Graphics.Vulkan;

// Vulkan backend, sibling to Blix.Graphics.OpenGL.OpenGLGraphicsDevice. On
// macOS this runs through MoltenVK (Vulkan-to-Metal translation); on Linux
// and Windows it talks to a native Vulkan driver. The IGraphicsDevice
// surface is intentionally the same as the GL backend — game code shouldn't
// care which one is under it.
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

    public VulkanGraphicsDevice(IVkSurface windowSurface, int initialWidth, int initialHeight)
    {
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

    public GraphicsDeviceDiagnostics DiagnosticsSnapshot { get; private set; } =
        new GraphicsDeviceDiagnostics(FrameErrorCount: 0, LastErrorContext: string.Empty, LastErrorMessage: string.Empty);

    public void SetDefaultRenderSurfaceSize(int width, int height)
    {
        var w = Math.Max(width, 1);
        var h = Math.Max(height, 1);
        if (w == defaultSurfaceWidth && h == defaultSurfaceHeight) return;
        defaultSurfaceWidth = w;
        defaultSurfaceHeight = h;
        MarkSwapchainOutOfDate();
    }

    public ResourceRegistrySnapshot SnapshotResources()
    {
        // Empty registry until resource creation paths are filled in.
        return new ResourceRegistrySnapshot(
            vertexBufferEntries: Array.Empty<VertexBufferEntry>(),
            indexBufferEntries: Array.Empty<IndexBufferEntry>(),
            textureEntries: Array.Empty<TextureEntry>(),
            shaderProgramEntries: Array.Empty<ShaderProgramEntry>(),
            pipelineEntries: Array.Empty<PipelineEntry>(),
            renderSurfaceEntries: Array.Empty<RenderSurfaceEntry>());
    }

    // Execute the per-frame command list. Symmetric with
    // OpenGLGraphicsDevice.Execute: walks each pass, builds a FrameDebugPacket
    // for the runtime's diagnostics adapter, and (eventually) records +
    // submits Vk command buffers. Until the swapchain integration lands the
    // body is metadata-only — it produces a valid FrameDebugPacket so the
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
                        UniformNames: Array.Empty<string>(),
                        Textures: Array.Empty<FrameDebugTexture>()));
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
                Draws: draws));
            totalDraws += draws.Count;
        }

        return new FrameDebugPacket(
            TotalPasses: passPackets.Count,
            TotalDraws: totalDraws,
            Passes: passPackets);
    }

    // Drains GPU timings that the device has finished resolving since the
    // last call. Symmetric with OpenGLGraphicsDevice.ConsumeAvailableGpuTimings.
    // Empty until VkQueryPool wiring lands.
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
