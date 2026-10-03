using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// Teardown. Everything Sponza makes on the device is either registered here as it is made (Own) or held
// in a field this frees directly: the froxel and fog volumes (re-created on resize, so their current
// handles are the live ones), the IBL textures (the cooked probe's or the procedural sky's), the shared
// mesh bundle, and the texture loader with its defaults. BLIX_TEARDOWN_TRACE=1 lists whatever is still
// live after Dispose.
internal sealed partial class SponzaLoop
{
    private readonly List<ShaderProgramHandle> ownedPrograms = new();
    private readonly List<PipelineHandle> ownedPipelines = new();
    private readonly List<TextureHandle> ownedTextures = new();
    private readonly List<VertexBufferHandle> ownedVertexBuffers = new();
    private readonly List<IndexBufferHandle> ownedIndexBuffers = new();
    private readonly List<IndirectBufferHandle> ownedIndirect = new();
    private readonly List<MaterialHandle> ownedMaterials = new();
    private readonly List<GpuBufferHandle> ownedGpuBuffers = new();

    private ShaderProgramHandle Own(ShaderProgramHandle h) { ownedPrograms.Add(h); return h; }
    private PipelineHandle Own(PipelineHandle h) { ownedPipelines.Add(h); return h; }
    private TextureHandle Own(TextureHandle h) { ownedTextures.Add(h); return h; }
    private VertexBufferHandle Own(VertexBufferHandle h) { ownedVertexBuffers.Add(h); return h; }
    private IndexBufferHandle Own(IndexBufferHandle h) { ownedIndexBuffers.Add(h); return h; }
    private IndirectBufferHandle Own(IndirectBufferHandle h) { ownedIndirect.Add(h); return h; }
    private MaterialHandle Own(MaterialHandle h) { ownedMaterials.Add(h); return h; }
    private GpuBufferHandle Own(GpuBufferHandle h) { ownedGpuBuffers.Add(h); return h; }

    private void ReleaseDeviceResources()
    {
        if (device is null) return;
        foreach (var m in ownedMaterials) device.DestroyMaterial(m);
        foreach (var p in ownedPipelines) device.DestroyPipeline(p);
        foreach (var p in ownedPrograms) device.DestroyShaderProgram(p);
        foreach (var b in ownedIndirect) device.DestroyIndirectBuffer(b);
        foreach (var b in ownedGpuBuffers) device.DestroyGpuBuffer(b);
        foreach (var b in ownedVertexBuffers) device.DestroyVertexBuffer(b);
        foreach (var b in ownedIndexBuffers) device.DestroyIndexBuffer(b);

        var textures = new HashSet<int>();
        foreach (var t in ownedTextures
            .Concat(new[] { froxelGridTexture, envCubeTexture, skyCubeTexture, irradianceCubeTexture, brdfLutTexture, sheenEnvTexture, sheenLutTexture })
            .Concat(fogScatterTextures))
        {
            if (t.Id != 0 && textures.Add(t.Id)) device.DestroyTexture(t);
        }

        if (sharedVb.Id != 0) device.DestroyVertexBuffer(sharedVb);
        if (sharedIbU16.Id != 0) device.DestroyIndexBuffer(sharedIbU16);
        if (sharedIbU32.Id != 0) device.DestroyIndexBuffer(sharedIbU32);
        textureLoader?.Dispose();
    }
}
