using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The rays' per-hit alpha test (Blix.Geometry's RayAlphaAtlas): each cut-out material's alpha, copied into a trailer on the surface words, so a
// ray meets a partly covered triangle where the material's alpha passes its cutoff at the point hit -- the raster's alpha test -- not by a coin
// keyed on the triangle. --ray-alpha-coin: the coin (the A/B; nothing is appended). --ray-alpha-side N: the longest side an entry keeps (default 512).
internal sealed partial class SponzaLoop
{
    private ShaderInterface rayAlphaBakeInterface = null!;
    private PassHandle rayAlphaBakePassHandle;
    private PipelineHandle rayAlphaBakePipeline;
    private bool rayAlphaCoin;
    private int rayAlphaSide = RayAlphaAtlas.MaxSide;

    // The frame scene's plan (null: none), and the materials each entry reads, for the dispatches that fill it.
    private RayAlphaAtlas.Plan? rayAlphaPlan;
    private List<RayMaterial> rayAlphaMaterials = new();

    private void ReadRayAlphaArgs(AppArgs args)
    {
        rayAlphaCoin = args.Flag("ray-alpha-coin");
        if (args.Int("ray-alpha-side") is { } side) rayAlphaSide = Math.Clamp(side, 16, 4096);
    }

    // Whether a material's rays test its alpha: a cutout (a cutoff, authored MASK or BLEND folded to one) and not a clear pane.
    private static bool AlphaTested(RayMaterial m) => m.AlphaCutoff > 0f && !m.Clear;

    // The mip of the material's albedo that the atlas entry is read at (ray_alpha_bake.comp's, and the bake's fine coverage pass): the texture over the entry's side.
    private float RayAlphaMip(RayMaterial material)
    {
        if (rayAlphaCoin || !AlphaTested(material) || !device.TryGetTextureSize(material.Albedo, out var w, out var h)) return 0f;
        return MathF.Log2(Math.Max(1f, Math.Max(w, h) / (float)Math.Min(Math.Max(w, h), rayAlphaSide)));
    }

    // The trailer for a scene whose placements carry rayPlacementMaterials, appended to its surface block (block 6): the block grows by
    // the trailer's words, and every reader of the first `rows` words is unaffected.
    private (RayAlphaAtlas.Plan? Plan, List<RayMaterial> Materials) PlanRayAlpha(RayQueryGpuData data, ref byte[] surfaceBlock)
    {
        var materials = new List<RayMaterial>();
        if (rayAlphaCoin) return (null, materials);
        var index = new Dictionary<RayMaterial, uint>();
        var perPlacement = new uint[rayPlacementMaterials.Count];
        for (var p = 0; p < perPlacement.Length; p++)
        {
            var material = rayPlacementMaterials[p];
            if (!AlphaTested(material)) { perPlacement[p] = RayAlphaAtlas.None; continue; }
            if (!index.TryGetValue(material, out var m)) { index[material] = m = (uint)materials.Count; materials.Add(material); }
            perPlacement[p] = m;
        }
        if (materials.Count == 0) return (null, materials);
        var entries = new List<RayAlphaAtlas.Material>(materials.Count);
        foreach (var material in materials)
        {
            if (!device.TryGetTextureSize(material.Albedo, out var w, out var h)) (w, h) = (1, 1);
            var scale = Math.Min(1f, rayAlphaSide / (float)Math.Max(w, h));
            entries.Add(new RayAlphaAtlas.Material(Math.Max(1, (int)MathF.Round(w * scale)), Math.Max(1, (int)MathF.Round(h * scale)), material.AlphaCutoff));
        }
        var plan = RayAlphaAtlas.Build(perPlacement, entries, data.Uvs);
        var rows = data.RowPlacements.Length;
        var grown = new byte[rows * 4 + plan.Words.Length * 4];
        Buffer.BlockCopy(surfaceBlock, 0, grown, 0, Math.Min(surfaceBlock.Length, rows * 4));
        MemoryMarshal.AsBytes(plan.Words.AsSpan()).CopyTo(grown.AsSpan(rows * 4));
        surfaceBlock = grown;
        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[VulkanSponza] ray alpha: {materials.Count} cut-out materials read at the point hit ({string.Join(", ", entries.Take(6).Select(e => $"{e.Width}x{e.Height}"))}{(entries.Count > 6 ? ", ..." : "")}); trailer {plan.Words.Length * 4 / 1048576.0:0.0} MB on the surface words."));
        return (plan, materials);
    }

    // The dispatches that fill the atlas: each entry's material alpha, after the surface bake and the same textures' readiness.
    private void RecordRayAlphaBake(RayAlphaAtlas.Plan? plan, List<RayMaterial> materials, RayQueryGpuData data, ShaderBufferBinding[] buffers)
    {
        if (plan is null) return;
        var rows = data.RowPlacements.Length;
        for (var m = 0; m < materials.Count; m++)
        {
            var at = (int)plan.Words[5] + m * RayAlphaAtlas.MaterialWords;
            var size = plan.Words[at + 1];
            var w = (int)(size & 0xFFFFu);
            var h = (int)(size >> 16);
            var words = (w * h + 3) / 4;
            graph.Dispatch(rayAlphaBakePassHandle, new DispatchCommand(rayAlphaBakePipeline, (words + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uSize", new Vector4Uniform(new Vector4(w, h, BitConverter.UInt32BitsToSingle((uint)(rows + plan.AtlasStart(m))), materials[m].BaseColor.W))),
                },
                new[] { new ShaderTextureBinding("uAlbedo", materials[m].Albedo) },
                Buffers: buffers.Where(b => b.Name == "BlixRaySurfaces").ToArray()));
        }
    }
}
