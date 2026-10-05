using System.Numerics;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --gi-screen-probes (with --gi-clipmap): a probe per 16x16 tile on the visible surface, its rays traced full length
// with the clipmap answering only at what they hit (screen_probe.glsl says why), read by the incident pass in place
// of the clipmap's own answer wherever a probe fits the pixel.
internal sealed partial class SponzaLoop
{
    private bool screenProbesEnabled;
    // --screen-probe-history N: frames a probe's radiance averages over at most.
    private float screenProbeHistory = 64f;
    // --screen-probe-ablate N: drop parts of the trace to attribute its cost (screen_probe.comp, uParams.w).
    private int screenProbeAblate;
    private ShaderInterface screenProbeInterface = null!;
    private PassHandle screenProbePassHandle;
    private PipelineHandle screenProbePipeline;
    // Ping-ponged: this frame's tile headers and probe pool, and last frame's for each probe's past.
    private readonly GpuBufferHandle[] screenProbeBuffers = new GpuBufferHandle[2];
    private readonly GpuBufferHandle[] screenProbeTileBuffers = new GpuBufferHandle[2];
    private GpuBufferHandle screenProbeDummy;
    private GpuBufferHandle screenProbeTileDummy;
    private GpuBufferHandle screenProbeStats;
    private (int X, int Y) screenProbeTiles;
    private int screenProbeCurrent;
    private int screenProbeFrame;
    private Matrix4x4 screenProbePrevViewProj;
    private bool screenProbeHistoryValid;

    // 12 vec4: position, normal, identity, nine radiance coefficients. A tile header is one uvec4.
    private const int ScreenProbeBytes = 12 * 16;
    private const int ScreenProbeTileBytes = 16;
    private const int ScreenProbeFloats = ScreenProbeBytes / 4;
    private const int ScreenProbeTile = 16;
    private const int ScreenProbesPerGroup = 8;

    private bool ScreenProbesActive => screenProbesEnabled && ClipmapActive;

    private void RecordScreenProbes(int frameWidth, int frameHeight)
    {
        if (!ScreenProbesActive || rayBlockBuffers.Length == 0) return;
        var tiles = ((frameWidth + ScreenProbeTile - 1) / ScreenProbeTile, (frameHeight + ScreenProbeTile - 1) / ScreenProbeTile);
        if (tiles != screenProbeTiles)
        {
            // The pool holds one probe per tile today; the headers are what let that change.
            for (var i = 0; i < 2; i++)
            {
                screenProbeBuffers[i] = Own(device.CreateGpuBuffer(tiles.Item1 * tiles.Item2 * ScreenProbeBytes, name: $"sponza.screen-probes.{i}"));
                screenProbeTileBuffers[i] = Own(device.CreateGpuBuffer(tiles.Item1 * tiles.Item2 * ScreenProbeTileBytes, name: $"sponza.screen-probe-tiles.{i}"));
            }
            screenProbeTiles = tiles;
            if (screenProbeStats.Equals(default(GpuBufferHandle))) screenProbeStats = Own(device.CreateGpuBuffer(32, name: "sponza.screen-probe-stats"));
            screenProbeHistoryValid = false;
        }
        var previous = screenProbeBuffers[screenProbeCurrent];
        var previousTiles = screenProbeTileBuffers[screenProbeCurrent];
        screenProbeCurrent ^= 1;
        var current = screenProbeBuffers[screenProbeCurrent];
        var currentTiles = screenProbeTileBuffers[screenProbeCurrent];
        // One pixel's width at unit depth: what a tile's footprint on a surface scales from.
        // Magnitude only: a Y-flipped projection carries a negative M22.
        var pixelAtUnitDepth = 2f / (MathF.Abs(cameraProjection.M22) * frameHeight);
        Matrix4x4.Invert(cameraProjection, out var invProj);
        Matrix4x4.Invert(cameraView, out var invView);
        var origins = clipmap!.Origins;
        var count = tiles.Item1 * tiles.Item2;
        graph.Dispatch(screenProbePassHandle, new DispatchCommand(screenProbePipeline, (count + ScreenProbesPerGroup - 1) / ScreenProbesPerGroup, 1, 1,
            new ShaderUniform[]
            {
                new("uInvProjection", new Matrix4x4Uniform(invProj)),
                new("uInvView", new Matrix4x4Uniform(invView)),
                new("uViewProj", new Matrix4x4Uniform(viewProj)),
                new("uPrevViewProj", new Matrix4x4Uniform(screenProbePrevViewProj)),
                new("uTarget", new Vector4Uniform(new Vector4(frameWidth, frameHeight, tiles.Item1, tiles.Item2))),
                new("uDims", new Vector4Uniform(new Vector4(ClipmapDims.X, ClipmapDims.Y, ClipmapDims.Z, clipmapSpacing))),
                new("uParams", new Vector4Uniform(new Vector4(clipmap.BlendProbes, screenProbeHistoryValid ? 1f : 0f, pixelAtUnitDepth, screenProbeAblate))),
                new("uOrigin0", new Vector4Uniform(new Vector4(origins[0].X, origins[0].Y, origins[0].Z, 0f))),
                new("uOrigin1", new Vector4Uniform(new Vector4(origins[1].X, origins[1].Y, origins[1].Z, 0f))),
                new("uOrigin2", new Vector4Uniform(new Vector4(origins[2].X, origins[2].Y, origins[2].Z, 0f))),
                new("uOrigin3", new Vector4Uniform(new Vector4(origins[3].X, origins[3].Y, origins[3].Z, 0f))),
                new("uSunDirection", new Vector4Uniform(new Vector4(sunDirection, 0f))),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 1f))),
                // w: a ray's sky, a little blurred (one mip) so single directions do not flicker on the sky's detail.
                new("uFrame", new Vector4Uniform(new Vector4(screenProbeFrame, screenProbeHistory, 1f, 1f))),
                new("uCascadeVP", new Matrix4x4ArrayUniform(cascadeViewProj)),
            },
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                new ShaderTextureBinding("uClipmapIrradiance", clipmapIrradiance),
                new ShaderTextureBinding("uClipmapDepth", clipmapDepth),
                new ShaderTextureBinding("uSkyRadiance", envCubeTexture),
                new ShaderTextureBinding("uCascadeShadowMaps[0]", graph.GetDepthTexture(cascadeHandles[0])),
                new ShaderTextureBinding("uCascadeShadowMaps[1]", graph.GetDepthTexture(cascadeHandles[1])),
                new ShaderTextureBinding("uCascadeShadowMaps[2]", graph.GetDepthTexture(cascadeHandles[2])),
            },
            Buffers: rayBlockBuffers
                .Append(new ShaderBufferBinding("ClipmapState", clipmapState))
                .Append(new ShaderBufferBinding("ScreenProbeTilesPrevious", previousTiles))
                .Append(new ShaderBufferBinding("ScreenProbesPrevious", previous))
                .Append(new ShaderBufferBinding("ScreenProbeTilesCurrent", currentTiles))
                .Append(new ShaderBufferBinding("ScreenProbesCurrent", current))
                .Append(new ShaderBufferBinding("ScreenProbeStats", screenProbeStats)).ToArray()));
        screenProbePrevViewProj = viewProj;
        screenProbeHistoryValid = true;
        screenProbeFrame++;
    }

    // What the incident pass binds: this frame's probes, or a placeholder when they are off.
    private (Vector4 Screen, Vector4 FrameSize, GpuBufferHandle Tiles, GpuBufferHandle Probes) ScreenProbeIncidentInputs(int frameWidth, int frameHeight)
    {
        if (ScreenProbesActive && screenProbeTiles.X > 0)
        {
            return (new Vector4(1f, screenProbeTiles.X, screenProbeTiles.Y, 0f), new Vector4(frameWidth, frameHeight, 0f, 0f),
                screenProbeTileBuffers[screenProbeCurrent], screenProbeBuffers[screenProbeCurrent]);
        }
        if (screenProbeDummy.Equals(default(GpuBufferHandle)))
        {
            screenProbeDummy = Own(device.CreateGpuBuffer(ScreenProbeBytes, name: "sponza.screen-probes.none"));
            screenProbeTileDummy = Own(device.CreateGpuBuffer(ScreenProbeTileBytes, name: "sponza.screen-probe-tiles.none"));
        }
        return (Vector4.Zero, new Vector4(frameWidth, frameHeight, 0f, 0f), screenProbeTileDummy, screenProbeDummy);
    }

    // At the shot: how many frames each placed probe has accumulated (a probe that keeps losing its past shows only
    // this frame's few rays, which flickers).
    private void WriteScreenProbeCheck()
    {
        if (!ScreenProbesActive || screenProbeTiles.X == 0) return;
        var count = screenProbeTiles.X * screenProbeTiles.Y;
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
            device.ReadGpuBuffer(screenProbeBuffers[screenProbeCurrent], 0, count * ScreenProbeBytes).AsSpan()).ToArray();
        var placed = 0;
        var bins = new int[5];   // 1, 2-7, 8-31, 32-63, 64+
        var young = new List<string>();
        var fine = new int[17];   // frames accumulated in fours, 0-3 up to 64+
        for (var i = 0; i < count; i++)
        {
            if (floats[i * ScreenProbeFloats + 3] <= 0f) continue;
            placed++;
            var n = floats[i * ScreenProbeFloats + 7];
            bins[n < 1.5f ? 0 : n < 7.5f ? 1 : n < 31.5f ? 2 : n < 63.5f ? 3 : 4]++;
            fine[Math.Min(16, (int)(n / 4f))]++;
            if (n < 7.5f) young.Add(string.Create(Inv, $"{i % screenProbeTiles.X},{i / screenProbeTiles.X}"));
        }
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] screen probes after {screenProbeFrame} frames: {placed} of {count} tiles placed; frames accumulated: 1 {bins[0]}, 2-7 {bins[1]}, 8-31 {bins[2]}, 32-63 {bins[3]}, 64+ {bins[4]}."));
        Console.WriteLine($"    frames accumulated, in fours from 0-3: {string.Join(' ', fine)}");
        var exact = new SortedDictionary<float, int>();
        for (var i = 0; i < count; i++)
        {
            if (floats[i * ScreenProbeFloats + 3] <= 0f) continue;
            var n = floats[i * ScreenProbeFloats + 7];
            if (n >= 24f) exact[n] = exact.GetValueOrDefault(n) + 1;
        }
        Console.WriteLine($"    frames accumulated from 24 up, exact: {string.Join(' ', exact.Select(e => string.Create(Inv, $"{e.Key:0.###}x{e.Value}")))}; history cap {screenProbeHistory}");
        Console.WriteLine($"    screen probes under 8 frames, tiles: {string.Join(' ', young)}");
        var stats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(screenProbeStats, 0, 32).AsSpan()).ToArray();
        double total = stats.Take(7).Sum(v => (double)v);
        string Pct(int i) => string.Create(Inv, $"{100.0 * stats[i] / Math.Max(1.0, total):0.0}%");
        Console.WriteLine($"[VulkanSponza] screen probe history over the run: kept {Pct(6)}; started afresh because the nearest candidate failed: no candidate {Pct(0)}, identity {Pct(1)}, normal {Pct(2)}, plane {Pct(3)}, lateral {Pct(4)}, no longer visible {Pct(5)}");
    }
}
