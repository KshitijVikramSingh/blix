using System.Numerics;
using Blix;
using Blix.Graphics;
using Blix.Render;

namespace Blix.Demos.VulkanSponza;

// The panes that need what is behind them (single-sample only): a ROUGH pane blurs it, a SOLID (KHR_materials_volume with a thickness) bends it. A forward pass cannot read the
// colour it is writing, so the lit pass is split: the opaque surfaces, the sky and the smooth thin panes (SponzaLoop.Render.cs's two draws) in "lit-scene"; then the
// scene-behind chain -- that colour copied (level 0) and halved four times (1/2 .. 1/16, scene_behind_down.frag) into separate targets, the blur a rough pane reads at; then
// "lit-glass", which loads the colour again and draws the frosted and thick panes over it, each pixel REPLACED by what is behind it (lit_glass_backdrop.frag): read at the
// screen position of the exit point of the view's refracted ray (a solid), at the level the pane's roughness names, times (1 - F) T baseColor and the volume's attenuation,
// plus the sky's reflection. What it is not: refraction of what is not in the frame (the view behind a pane's edge clamps to the screen), the panes' own order (a pane
// seen through another is not in the chain, and two backdrop panes overlapping show the one drawn last), the second interface of a solid (its far wall is one chord of
// attenuation and the bend of the first surface alone).
internal sealed partial class SponzaLoop
{
    private const int GlassBackdropLevels = 5;
    private readonly GraphResourceHandle[] glassBehindHandles = new GraphResourceHandle[GlassBackdropLevels];
    private readonly PassHandle[] glassBehindPasses = new PassHandle[GlassBackdropLevels];
    private readonly PipelineHandle[] glassBehindPipelines = new PipelineHandle[GlassBackdropLevels];
    private PassHandle glassBackdropPass;
    private PipelineHandle glassBackdropSolidPipeline, glassBackdropDoubleSidedPipeline;
    private bool glassBackdropDeclared;

    // Materials whose panes read the backdrop: a transmitting material that is rough, or thick.
    private readonly HashSet<MaterialHandle> glassBackdropMaterials = new();
    private bool noGlassBackdrop;
    private readonly List<OpaqueGroup> glassBackdropGroups = new();

    private static bool NeedsGlassBackdrop(float transmission, float roughness, float ior, float thickness) =>
        transmission > 0f && (thickness > 0f || roughness * Math.Clamp(ior * 2f - 2f, 0f, 1f) > 0.04f);

    // After the lit pass is declared: the chain's targets and passes, and the glass pass that loads the colour again with the same reads.
    private void DeclareGlassBackdrop(Func<string, string, ShaderInterface> reflect, ShaderInterface litInterface, ShaderInterface skyInterface)
    {
        if (MsaaSamples > 1 || noGlassBackdrop) return;
        for (var k = 0; k < GlassBackdropLevels; k++)
        {
            glassBehindHandles[k] = graph.ColorTarget($"scene-behind{k}", TextureFormat.R11G11B10F, new MatchSwapchainGraphSize(1f / (1 << k)));
            glassBehindPasses[k] = graph.GraphicsPass($"scene-behind{k}")
                .Target(glassBehindHandles[k], LoadOp.DontCare, StoreOp.Store)
                .Read(k == 0 ? hdrHandle : glassBehindHandles[k - 1])
                .Shader(reflect("present.vert", k == 0 ? "scene_behind_copy.frag" : "scene_behind_down.frag"))
                .Handle;
        }
        var glass = graph.GraphicsPass("lit-glass")
            .Target(hdrHandle, LoadOp.Load, StoreOp.Store)
            .Depth(depthHandle, LoadOp.Load, StoreOp.Store)
            .Shader(litInterface, skyInterface);
        glass = glass.Read(incidentHandle).Read(incidentGradientHandle);   // the lit pass's own reads (SponzaLoop.Setup.cs)
        for (var k = 0; k < GlassBackdropLevels; k++) glass = glass.Read(glassBehindHandles[k]);
        glassBackdropPass = glass.Handle;
        glassBackdropDeclared = true;
    }

    private void CreateGlassBackdropPipelines(Func<string, string, ShaderInterface> reflect, ShaderInterface litInterface, byte[] litVertSpv, byte[] presentVertSpv, string shaderDir)
    {
        if (!glassBackdropDeclared) return;
        var litGlassBackdropProgram = Own(device.CreateShaderProgramFromSpv(litVertSpv,
            File.ReadAllBytes(Path.Combine(shaderDir, "lit_glass_backdrop.frag.spv")), litInterface, "lit.glass.backdrop"));
        glassBackdropSolidPipeline = Pipeline(litGlassBackdropProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.BackFaceCulling, new[] { BlendState.Disabled }, glassBackdropPass, "lit.glass.backdrop");
        glassBackdropDoubleSidedPipeline = Pipeline(litGlassBackdropProgram, VertexPosition3NormalTangentTexture.Layout,
            DepthState.LessEqualNoWrite, RasterizerState.NoCulling, new[] { BlendState.Disabled }, glassBackdropPass, "lit.glass.backdrop.doubleSided");
        for (var k = 0; k < GlassBackdropLevels; k++)
        {
            var frag = k == 0 ? "scene_behind_copy.frag" : "scene_behind_down.frag";
            glassBehindPipelines[k] = Pipeline(Own(device.CreateShaderProgramFromSpv(presentVertSpv,
                    File.ReadAllBytes(Path.Combine(shaderDir, frag + ".spv")), reflect("present.vert", frag), $"scene_behind{k}")),
                VertexPosition3NormalTexture.Layout, DepthState.Disabled, RasterizerState.NoCulling,
                new[] { BlendState.Disabled }, glassBehindPasses[k], $"scene_behind{k}");
        }
    }

    // The chain, recorded between the lit pass and the glass pass.
    private void RecordGlassBehind()
    {
        for (var k = 0; k < GlassBackdropLevels; k++)
        {
            var source = graph.GetColorTexture(k == 0 ? hdrHandle : glassBehindHandles[k - 1]);
            var level = k;
            graph.Pass(glassBehindPasses[k], scope => fullscreen.Draw(scope, glassBehindPipelines[level], new[] { new ShaderTextureBinding("uSrc", source) }));
        }
    }

    // The pass's texture list with the chain bound for real (passBindings holds placeholders for the other passes).
    private ShaderTextureBinding[] GlassBackdropBindings()
    {
        var list = passBindings.ToArray();
        for (var k = 0; k < GlassBackdropLevels; k++)
        {
            var i = Array.FindIndex(list, b => b.Name == $"uBehind{k}");
            if (i >= 0) list[i] = new ShaderTextureBinding($"uBehind{k}", graph.GetColorTexture(glassBehindHandles[k]));
        }
        return list;
    }
}
