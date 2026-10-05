using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --gi-clipmap: the camera-relative probe clipmap (stage 4c), solved by clipmap_inject.comp through the ray scene
// with nothing baked. Blix.Geometry's ProbeClipmap places its probes; this keeps the GPU side in step with it every
// frame (where each level's block is), owns the atlases and the per-slot state, and checks at the shot that the
// GPU's addressing is the C# contract's.
internal sealed partial class SponzaLoop
{
    private bool clipmapEnabled;
    // --clipmap-spacing M: level 0's probe spacing; --clipmap-budget N: probes solved a frame (64 rays each).
    private float clipmapSpacing = 0.5f;
    private int clipmapBudget = 512;
    // --clipmap-unknown-sky V: the sky visibility a probe ray takes at a hit no clipmap probe answers for. Closed (0):
    // in steady state 1 hit in 31,359 is unanswered, but at start-up nearly all are, and taking them as open sky lit
    // Sponza's field with glare it took 4,000 frames to lose (sky-carried bounce 0.0244 at frame 600, 0.0033 settled).
    private float clipmapUnknownSky = 0f;
    private ProbeClipmap? clipmap;
    private TextureHandle clipmapIrradiance;
    private TextureHandle clipmapDepth;
    private GpuBufferHandle clipmapState;
    private GpuBufferHandle clipmapQueue;
    private ShaderInterface clipmapInterface = null!;
    private PassHandle clipmapPassHandle;
    private PipelineHandle clipmapPipeline;
    private int clipmapFrame;
    private ShaderInterface incidentClipmapInterface = null!;
    private PipelineHandle incidentClipmapPipeline;

    // The clipmap is what the incident field and the lit pass read once it has been solved at least once.
    private bool ClipmapActive => clipmap is not null && clipmapFrame > 0;

    // The incident field from the clipmap (incident_clipmap.frag): the same target, the same two quantities.
    private void RecordIncidentClipmap(Matrix4x4 invProjection, Matrix4x4 invView, int width, int height)
    {
        var origins = clipmap!.Origins;
        graph.Pass(incidentPassHandle, scope => fullscreen.Draw(
            scope, incidentClipmapPipeline,
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                new ShaderTextureBinding("uClipmapIrradiance", clipmapIrradiance),
                new ShaderTextureBinding("uClipmapDepth", clipmapDepth),
                new ShaderTextureBinding("uIrradiance", irradianceCubeTexture),
            },
            pushConstants: null,
            uniforms: new ShaderUniform[]
            {
                new("uInvProjection", new Matrix4x4Uniform(invProjection)),
                new("uInvView", new Matrix4x4Uniform(invView)),
                new("uTarget", new Vector4Uniform(new Vector4(width, height, 1f / width, 1f / height))),
                new("uClipDims", new Vector4Uniform(new Vector4(ClipmapDims.X, ClipmapDims.Y, ClipmapDims.Z, clipmapSpacing))),
                new("uClipParams", new Vector4Uniform(new Vector4(clipmap.BlendProbes, 0f, 0f, 0f))),
                new("uOrigin0", new Vector4Uniform(new Vector4(origins[0].X, origins[0].Y, origins[0].Z, 0f))),
                new("uOrigin1", new Vector4Uniform(new Vector4(origins[1].X, origins[1].Y, origins[1].Z, 0f))),
                new("uOrigin2", new Vector4Uniform(new Vector4(origins[2].X, origins[2].Y, origins[2].Z, 0f))),
                new("uOrigin3", new Vector4Uniform(new Vector4(origins[3].X, origins[3].Y, origins[3].Z, 0f))),
            },
            buffers: new[] { new ShaderBufferBinding("ClipmapState", clipmapState) }));
    }

    private const int ClipmapLevels = 4;
    private static readonly Int3 ClipmapDims = new(32, 16, 32);
    // The depth moments' lobe: sharper keeps walls as edges, but each texel then hears fewer of 64 rays.
    private const float ClipmapDepthLobe = 12f;
    private const float ClipmapConvergence = 0.25f;

    private void CreateClipmap()
    {
        if (!clipmapEnabled || clipmap is not null) return;
        clipmap = new ProbeClipmap(ClipmapLevels, ClipmapDims, clipmapSpacing);
        clipmapIrradiance = Own(device.CreateStorageTexture2D(clipmap.AtlasWidth, clipmap.AtlasHeight, TextureFormat.Rgba16F,
            SamplerDescription.LinearClamp, "sponza.clipmap.irradiance"));
        clipmapDepth = Own(device.CreateStorageTexture2D(clipmap.AtlasWidth, clipmap.AtlasHeight, TextureFormat.Rgba16F,
            SamplerDescription.LinearClamp, "sponza.clipmap.depth"));
        var slots = ClipmapLevels * clipmap.ProbesPerLevel;
        // Every slot starts holding a cell no block contains, unsolved: the first scan re-points and queues all of them.
        var initial = new uint[slots * 4];
        for (var i = 0; i < slots; i++) { initial[i * 4] = 0x80000000u; initial[i * 4 + 1] = 0x80000000u; initial[i * 4 + 2] = 0x80000000u; }
        clipmapState = Own(device.CreateGpuBuffer(slots * 16, MemoryMarshal.AsBytes(initial.AsSpan()), "sponza.clipmap.state"));
        clipmapQueue = Own(device.CreateGpuBuffer((8 + slots) * 4, name: "sponza.clipmap.queue"));
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] probe clipmap: {ClipmapLevels} levels of {ClipmapDims.X}x{ClipmapDims.Y}x{ClipmapDims.Z} probes, spacing {clipmapSpacing:0.##} m doubling (level {ClipmapLevels - 1} spans {clipmap.Spacing(ClipmapLevels - 1) * ClipmapDims.X:0} m), atlases {clipmap.AtlasWidth}x{clipmap.AtlasHeight}, {clipmapBudget} probes a frame."));
    }

    private void RecordClipmap()
    {
        if (clipmap is null || rayBlockBuffers.Length == 0 || !raySurfacesBaked) return;
        clipmap.Follow(cameraPosition);
        var origins = new Vector4[4];
        for (var l = 0; l < ClipmapLevels; l++) origins[l] = new Vector4(clipmap.Origins[l].X, clipmap.Origins[l].Y, clipmap.Origins[l].Z, 0f);
        var slots = ClipmapLevels * clipmap.ProbesPerLevel;
        var textures = new[]
        {
            new ShaderTextureBinding("uClipmapIrradiance", clipmapIrradiance),
            new ShaderTextureBinding("uClipmapDepth", clipmapDepth),
            new ShaderTextureBinding("uIrradiance", irradianceCubeTexture),
            new ShaderTextureBinding("uSkyRadiance", envCubeTexture),
        };
        var buffers = rayBlockBuffers
            .Append(new ShaderBufferBinding("ClipmapState", clipmapState))
            .Append(new ShaderBufferBinding("ClipmapQueue", clipmapQueue)).ToArray();
        DispatchCommand Phase(int mode, int groups) => new(clipmapPipeline, groups, 1, 1, new ShaderUniform[]
        {
            new("uDims", new Vector4Uniform(new Vector4(ClipmapDims.X, ClipmapDims.Y, ClipmapDims.Z, clipmapSpacing))),
            new("uParams", new Vector4Uniform(new Vector4(clipmap.BlendProbes, ClipmapConvergence, mode, clipmapBudget))),
            new("uOrigin0", new Vector4Uniform(origins[0])),
            new("uOrigin1", new Vector4Uniform(origins[1])),
            new("uOrigin2", new Vector4Uniform(origins[2])),
            new("uOrigin3", new Vector4Uniform(origins[3])),
            new("uSunDirection", new Vector4Uniform(new Vector4(sunDirection, 0f))),
            new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 1f))),
            new("uFrame", new Vector4Uniform(new Vector4(clipmapFrame, 1f, 1f, ClipmapDepthLobe))),
            // y: the prefiltered sky's mip a probe ray reads, about a ray's 1/64 of the sphere (a ~15 degree cone).
            new("uFallback", new Vector4Uniform(new Vector4(clipmapUnknownSky, MathF.Min(1.5f, Math.Max(0, iblPrefilterMips - 1)), 0f, 0f))),
        }, textures, Buffers: buffers);
        graph.Dispatch(clipmapPassHandle, Phase(0, 1));
        graph.Dispatch(clipmapPassHandle, Phase(1, (slots + 63) / 64));
        graph.Dispatch(clipmapPassHandle, Phase(2, clipmapBudget));
        clipmapFrame++;
    }

    // At the shot: every slot's state against the C# contract (the scan re-points each one to the cell its level's
    // block now puts there), and how much of each level is solved and buried.
    private void WriteClipmapCheck()
    {
        if (clipmap is null || clipmapFrame == 0) return;
        var slots = ClipmapLevels * clipmap.ProbesPerLevel;
        var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(clipmapState, 0, slots * 16).AsSpan()).ToArray();
        var mismatched = 0;
        var solved = new int[ClipmapLevels];
        var buried = new int[ClipmapLevels];
        for (var g = 0; g < slots; g++)
        {
            var level = g / clipmap.ProbesPerLevel;
            var index = g % clipmap.ProbesPerLevel;
            var slot = new Int3(index % ClipmapDims.X, index / ClipmapDims.X % ClipmapDims.Y, index / (ClipmapDims.X * ClipmapDims.Y));
            var expected = clipmap.CellInSlot(level, slot);
            var cell = new Int3((int)words[g * 4], (int)words[g * 4 + 1], (int)words[g * 4 + 2]);
            if (cell != expected) mismatched++;
            if ((words[g * 4 + 3] & 1u) != 0) solved[level]++;
            if ((words[g * 4 + 3] & 2u) != 0) buried[level]++;
        }
        var counters = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(clipmapQueue, 0, 32).AsSpan()).ToArray();
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] probe clipmap, last frame: {counters[4]:N0} ray hits, {counters[5]:N0} ({100.0 * counters[5] / Math.Max(1u, counters[4]):0.0}%) where no probe answered (sky taken as {clipmapUnknownSky:0.##})."));
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] probe clipmap after {clipmapFrame} frames: slots whose cell differs from ProbeClipmap's {mismatched} of {slots}; solved per level {string.Join(" / ", solved)} of {clipmap.ProbesPerLevel}, buried {string.Join(" / ", buried)}."));
    }
}
