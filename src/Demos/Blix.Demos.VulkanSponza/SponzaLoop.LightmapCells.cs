using System.Numerics;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The atlas resolve (lightmap_cells.comp): after each compose -- whenever the texels' resolved light changes -- each atlas
// cell's light, sky visibility, normal and flags, beside the tile-sparse index (8 B a cell of each covered tile), so the
// incident pass's lightmapped pixels read four cells rather than walking records every frame: the work per light change,
// not per pixel per frame (the incident pass was 35-42 ms of a moving frame at 2880 x 1620, 2026-10-24).
// --no-atlas-cells: the full lookup everywhere, as before.
internal sealed partial class SponzaLoop
{
    private bool atlasCells = true;
    private ShaderInterface lightmapCellsInterface = null!;
    private PassHandle lightmapCellsPass;
    private PipelineHandle lightmapCellsPipeline;
    private GpuBufferHandle lightmapCellLight, lightmapCellNone;
    private int lightmapIndexTiles;
    // The cells hold the light the incident pass would read: set by the resolve after a compose, cleared by anything
    // else that writes the resolved light (RestTexels' texel_resolve.comp).
    private bool lightmapCellsCurrent;
    private bool LightmapCellsNow => atlasCells && lightmapCellsCurrent && !lightmapCellLight.Equals(default(GpuBufferHandle));

    private void DeclareLightmapCellsPass(Func<string[], ShaderInterface> reflect)
    {
        lightmapCellsInterface = reflect(new[] { "lightmap_cells.comp" });
        lightmapCellsPass = graph.ComputePass("lightmap-cells").Shader(lightmapCellsInterface).Handle;
    }

    private void CreateLightmapCellsPipeline(string shaderDir)
    {
        lightmapCellsPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "lightmap_cells.comp.spv")), lightmapCellsInterface, "lightmap_cells")), "lightmap_cells"));
    }

    // With the index (UploadAtlasGather): a cell's two words for each of the covered tiles' 64.
    private void CreateLightmapCells(uint[] index)
    {
        if (!atlasCells) return;
        lightmapIndexTiles = (int)index[3];
        var covered = (index.Length - 4 - lightmapIndexTiles) / 64;
        var bytes = checked(Math.Max(1, covered) * 64 * 8);
        lightmapCellLight = Own(device.CreateGpuBuffer(bytes, name: "sponza.lightmap.cells"));
        lightmapCellsCurrent = false;
    }

    private ShaderBufferBinding LightmapCellsBinding()
    {
        if (!lightmapCellLight.Equals(default(GpuBufferHandle))) return new ShaderBufferBinding("LightmapCellLight", lightmapCellLight);
        if (lightmapCellNone.Equals(default(GpuBufferHandle))) lightmapCellNone = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.lightmap.cells.none"));
        return new ShaderBufferBinding("LightmapCellLight", lightmapCellNone);
    }

    // After the compose (RecordSplitCompose): the cells from the light it resolved.
    private void RecordLightmapCells()
    {
        if (!atlasCells || lightmapCellLight.Equals(default(GpuBufferHandle)) || texelDetailBuffer.Equals(default(GpuBufferHandle))) return;
        graph.Dispatch(lightmapCellsPass, new DispatchCommand(lightmapCellsPipeline, lightmapIndexTiles, 1, 1,
            new ShaderUniform[]
            {
                new("uCells", new Vector4Uniform(new Vector4(lightmapIndexTiles, lightmapFirstRecord, SeamBlendNow ? 1f : 0f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                AtlasIndexBuffer(),
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("TexelResolved", texelResolvedBuffer),
                new ShaderBufferBinding("TexelDetail", texelDetailBuffer),
                new ShaderBufferBinding("TexelSeamStart", SeamBlendNow ? texelSeamStart : lightmapCellLight),
                new ShaderBufferBinding("LightmapCellLight", lightmapCellLight),
            }));
        lightmapCellsCurrent = true;
    }
}
