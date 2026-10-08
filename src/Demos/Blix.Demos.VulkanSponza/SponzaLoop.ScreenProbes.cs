using System.Numerics;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --gi-screen-probes (with --gi-clipmap): a probe per 16x16 tile on the visible surface, its rays traced full length
// with the clipmap answering only at what they hit (screen_probe.glsl says why), read by the incident pass in place
// of the clipmap's own answer wherever a probe fits the pixel.
internal sealed partial class SponzaLoop
{
    private bool probeSupportNow;
    // Stage 4f: --dynamic-history N, the history cap of the probes' dynamic part (paths through a moving reach);
    // --dependency-reset, the control arm (a dependent probe drops its whole past); --no-dependency, no split at all
    // (every path static, as before 4f).
    private float dynamicHistory = 4f;
    private bool dependencyReset;
    private bool noDependency;
    // --dependency-everything: a control -- the reach every path counts as crossing is the whole world, so every
    // probe runs on the dynamic history alone. Where the real reach leaves bias this removes, a path is being missed.
    private bool dependencyEverything;
    // Stage 4f-ii': --fresh-passes N, how many passes of 32 rays a fresh probe traces; --fresh-frames F, under how
    // many frames last frame's probe in the tile makes it fresh.
    private float freshPasses = 4f;
    private float freshFrames = 4f;
    // --probe-ray-length M: how far a probe ray traces before the clipmap takes over (0: the whole way).
    private float probeRayLength = 0f;
    // --probe-layers 1|2: whether a tile may place a second probe on another surface (stage 4f-iv).
    private int probeLayers = 2;
    // --probe-layer-keys: a different SurfaceKey alone opens a second layer (the rule before depth discontinuities).
    private bool probeLayerKeys;
    // --dependent-passes N: passes a probe traces whose last trace crossed a moving reach (it also traces every frame).
    private float dependentPasses = 1f;
    private Bounds3 DependencyBox => dependencyEverything ? new Bounds3(new Vector3(-1e5f), new Vector3(1e5f)) : moverReach;
    private bool screenProbesEnabled;
    // --screen-probe-history N: frames a probe's radiance averages over at most. 256: at 64 a probe's average kept
    // wandering by its last few rays, and --stability (TAA off, still camera) measured the incident light varying
    // 2.02% (median pixel) with the clipmap frozen, against 0.51% at 256. The price is lag: a change in the light
    // takes ~4 s to settle in (adaptive history, short when the estimate disagrees with its past, is the fix when
    // something in a scene changes its lighting).
    private float screenProbeHistory = 256f;
    // --screen-probe-ablate N: drop parts of the trace to attribute its cost (screen_probe.comp, uParams.w).
    private int screenProbeAblate;
    // place (one workgroup a tile), trace (one thread a ray), integrate (one thread a probe slot).
    private static readonly string[] ScreenProbeStages = { "place", "trace", "integrate" };
    private readonly ShaderInterface[] screenProbeInterfaces = new ShaderInterface[3];
    private readonly PassHandle[] screenProbePassHandles = new PassHandle[3];
    private readonly PipelineHandle[] screenProbePipelines = new PipelineHandle[3];
    // Between the stages: a placement record per slot, and every ray's answer (slots x passes stride x 32).
    private GpuBufferHandle screenProbePlacements;
    private GpuBufferHandle screenProbeRays;
    private int ScreenProbePassStride => (int)Math.Max(1f, Math.Max(freshPasses, dependentPasses));
    // Ping-ponged: this frame's tile headers and probe pool, and last frame's for each probe's past.
    private readonly GpuBufferHandle[] screenProbeBuffers = new GpuBufferHandle[2];
    private readonly GpuBufferHandle[] screenProbeTileBuffers = new GpuBufferHandle[2];
    private GpuBufferHandle screenProbeDummy;
    private GpuBufferHandle screenProbeTileDummy;
    private GpuBufferHandle screenProbeStats;
    // The spatially filtered probes the incident pass reads (screen_probe_filter.comp); the next frame accumulates
    // from the unfiltered ones. --screen-probe-filter R: radius in tiles, 0 a copy.
    private ShaderInterface screenProbeFilterInterface = null!;
    private PassHandle screenProbeFilterPassHandle;
    private PipelineHandle screenProbeFilterPipeline;
    private GpuBufferHandle screenProbeFiltered;
    private int screenProbeFilterRadius = 1;
    // --screen-probe-reset-at F: drop every probe's past on post-load frame F, as a camera move does, so a shot a few
    // frames later shows what the user sees for the first seconds after moving.
    private int screenProbeResetAt = -1;
    // --screen-probe-seed N: frames a fresh probe's clipmap prior counts for (0, the default: from its own rays);
    // --young-filter: widen young probes' filter. Both off since stage 4f-ii': a fresh probe traces --fresh-passes
    // (4) passes of its own instead, and borrowing was biased -- after a reset, 30 frames on, the rest of the frame
    // read +8.1% (median 9.0%, p90 83%) with them against -0.3% (5.0%, 22%) with 4 passes and neither; the mover's
    // ring +32% against +12%.
    private float screenProbeSeedFrames = 0f;
    private bool screenProbeYoungWide = false;

    // --no-incident-gradient: the incident pass writes no gradient, and the lit pass shades indirect light at the
    // geometric normal (the control for what the normal map adds). The gradient comes from the clipmap's own
    // directional irradiance (incident_clipmap.frag), not the screen probes' SH, which shimmered.
    private bool noIncidentGradient;
    // --incident-normal-bias B: mips down the normal map is read for the gradient; --incident-gradient-clamp C: the
    // correction's factor stays in [1 - C, 1 + C] (it was [0, 3]). Sponza, still camera: pixels the gradient moves
    // (TAA off) against the presented image's median variation (TAA on): bias 0 (clamp 0-3) 13.0% / 6.69%, 1 5.3% /
    // 4.78%, 2 1.7% / 3.67%, 3 0.56% / 3.44%, none 0 / 3.40%. The detail IS the shimmer: what a normal map adds below
    // a few pixels, TAA's jitter samples differently every frame. Under the reworked TAA (motion reprojection,
    // Rgba16F history, 0.9) the same sweep reads bias 0/1/2 at 1.83/1.24/0.97% (no gradient 0.94%, default look
    // 0.83%), and at 1 the user saw "tiny spots of light and dark across walls": the presented-variation heat map
    // shows weave- and grain-scale speckle on every textured surface with it and none without. 2.
    private float incidentNormalBias = 2f;
    private float incidentGradientClamp = 0.5f;
    private (int X, int Y) screenProbeTiles;
    private int screenProbeCurrent;
    private int screenProbeFrame;
    // --screen-probe-seed-offset K: start the probes' random sequence elsewhere. Two converged runs with different
    // offsets differ only by the noise the probes have not averaged away: the measure of "settled into spots".
    private int screenProbeSeedOffset;
    private Matrix4x4 screenProbePrevViewProj;
    private bool screenProbeHistoryValid;

    // 21 vec4: position, normal, identity, nine static radiance coefficients, nine dynamic (stage 4f). A tile header
    // is one uvec4.
    private const int ScreenProbeBytes = 21 * 16;
    private const int ScreenProbeTileBytes = 16;
    private const int ScreenProbeFloats = ScreenProbeBytes / 4;
    // Probe slots a tile (SCREEN_PROBE_MAX_PER_TILE): the tile's surface, and a second where it spans one.
    private const int ScreenProbeLayers = 2;
    private const int ScreenProbeTile = 32;
    private const int ScreenProbesPerGroup = 2;

    private bool ScreenProbesActive => screenProbesEnabled && ClipmapActive;

    private void RecordScreenProbes(int frameWidth, int frameHeight)
    {
        if (!ScreenProbesActive || rayBlockBuffers.Length == 0) return;
        var tiles = ((frameWidth + ScreenProbeTile - 1) / ScreenProbeTile, (frameHeight + ScreenProbeTile - 1) / ScreenProbeTile);
        if (tiles != screenProbeTiles)
        {
            // The pool holds two probe slots a tile (stage 4f-iv: a second surface where the tile spans one); each
            // tile's header names the slots placed.
            for (var i = 0; i < 2; i++)
            {
                screenProbeBuffers[i] = Own(device.CreateGpuBuffer(tiles.Item1 * tiles.Item2 * ScreenProbeLayers * ScreenProbeBytes, name: $"sponza.screen-probes.{i}"));
                screenProbeTileBuffers[i] = Own(device.CreateGpuBuffer(tiles.Item1 * tiles.Item2 * ScreenProbeTileBytes, name: $"sponza.screen-probe-tiles.{i}"));
            }
            screenProbeTiles = tiles;
            screenProbeFiltered = Own(device.CreateGpuBuffer(tiles.Item1 * tiles.Item2 * ScreenProbeLayers * ScreenProbeBytes, name: "sponza.screen-probes.filtered"));
            if (screenProbeStats.Equals(default(GpuBufferHandle))) screenProbeStats = Own(device.CreateGpuBuffer(64, name: "sponza.screen-probe-stats"));
            var slots = tiles.Item1 * tiles.Item2 * ScreenProbeLayers;
            screenProbePlacements = Own(device.CreateGpuBuffer(slots * 48, name: "sponza.screen-probes.placements"));
            screenProbeRays = Own(device.CreateGpuBuffer(slots * ScreenProbePassStride * 32 * 16, name: "sponza.screen-probes.rays"));
            screenProbeHistoryValid = false;
        }
        if (postLoadFrames == screenProbeResetAt) screenProbeHistoryValid = false;
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
        var count = tiles.Item1 * tiles.Item2 * ScreenProbeLayers;
        var uniforms = new ShaderUniform[]
            {
                new("uInvProjection", new Matrix4x4Uniform(invProj)),
                new("uInvView", new Matrix4x4Uniform(invView)),
                new("uViewProj", new Matrix4x4Uniform(viewProj)),
                new("uPrevViewProj", new Matrix4x4Uniform(screenProbePrevViewProj)),
                new("uTarget", new Vector4Uniform(new Vector4(frameWidth, frameHeight, tiles.Item1, tiles.Item2))),
                new("uDims", new Vector4Uniform(new Vector4(ClipmapDims.X, ClipmapDims.Y, ClipmapDims.Z, clipmapSpacing))),
                new("uParams", new Vector4Uniform(new Vector4(ClipmapBlend, screenProbeHistoryValid ? 1f : 0f, pixelAtUnitDepth, screenProbeAblate))),
                new("uOrigin0", new Vector4Uniform(new Vector4(origins[0].X, origins[0].Y, origins[0].Z, 0f))),
                new("uOrigin1", new Vector4Uniform(new Vector4(origins[1].X, origins[1].Y, origins[1].Z, 0f))),
                new("uOrigin2", new Vector4Uniform(new Vector4(origins[2].X, origins[2].Y, origins[2].Z, 0f))),
                new("uOrigin3", new Vector4Uniform(new Vector4(origins[3].X, origins[3].Y, origins[3].Z, 0f))),
                new("uSunDirection", new Vector4Uniform(new Vector4(sunDirection, 0f))),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 1f))),
                // w: a ray's sky, a little blurred (one mip) so single directions do not flicker on the sky's detail.
                new("uFrame", new Vector4Uniform(new Vector4(screenProbeFrame + screenProbeSeedOffset, screenProbeHistory, 1f, 1f))),
                new("uCascadeVP", new Matrix4x4ArrayUniform(cascadeViewProj)),
                new("uParams2", new Vector4Uniform(new Vector4(screenProbeSeedFrames, probeSupportNow ? 1f : 0f, dependencyReset ? 1f : 0f, probeRayLength))),
                // The reach of what moved this frame: the mover's while it moves, none while held or absent.
                new("uDynamicMin", new Vector4Uniform(MoverActive && moverMoved && !noDependency ? new Vector4(DependencyBox.Min, 1f) : Vector4.Zero)),
                new("uDynamicMax", new Vector4Uniform(new Vector4(MoverActive ? DependencyBox.Max : Vector3.Zero, dynamicHistory))),
                new("uFresh", new Vector4Uniform(new Vector4(freshPasses, freshFrames, probeLayers < 2 ? 1f : 0f, dependentPasses))),
                new("uLayers", new Vector4Uniform(new Vector4(probeLayerKeys ? 1f : 0f, ScreenProbePassStride, 0f, 0f))),
            };
        var textures = new[]
        {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                new ShaderTextureBinding("uClipmapIrradiance", clipmapIrradiance),
                new ShaderTextureBinding("uClipmapDepth", clipmapDepth),
                new ShaderTextureBinding("uSkyRadiance", envCubeTexture),
                new ShaderTextureBinding("uSurfaceKey", graph.GetColorTexture(surfaceKeyHandle)),
                new ShaderTextureBinding("uVelocity", graph.GetColorTexture(velocityHandle)),
                new ShaderTextureBinding("uMotion", graph.GetColorTexture(motionHandle)),
                new ShaderTextureBinding("uCascadeShadowMaps[0]", graph.GetDepthTexture(cascadeHandles[0])),
                new ShaderTextureBinding("uCascadeShadowMaps[1]", graph.GetDepthTexture(cascadeHandles[1])),
                new ShaderTextureBinding("uCascadeShadowMaps[2]", graph.GetDepthTexture(cascadeHandles[2])),
        };
        var buffers = rayBlockBuffers
                .Append(new ShaderBufferBinding("ClipmapState", clipmapState))
                .Append(new ShaderBufferBinding("ScreenProbeTilesPrevious", previousTiles))
                .Append(new ShaderBufferBinding("ScreenProbesPrevious", previous))
                .Append(new ShaderBufferBinding("ScreenProbeTilesCurrent", currentTiles))
                .Append(new ShaderBufferBinding("ScreenProbesCurrent", current))
                .Append(new ShaderBufferBinding("ScreenProbeStats", screenProbeStats))
            .Append(new ShaderBufferBinding("ScreenProbePlacements", screenProbePlacements))
            .Append(new ShaderBufferBinding("ScreenProbeRays", screenProbeRays)).ToArray();
        // Each stage binds what its interface declares (the compiler may drop what a stage never reads).
        var groups = new[] { tiles.Item1 * tiles.Item2, (count * 32 + 63) / 64, (count + 63) / 64 };
        for (var stage = 0; stage < ScreenProbeStages.Length; stage++)
        {
            var declared = screenProbeInterfaces[stage].Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
            bool Declares(string name) => declared.Contains(name) || declared.Contains(name.Split('[')[0]);
            graph.Dispatch(screenProbePassHandles[stage], new DispatchCommand(screenProbePipelines[stage], groups[stage], 1, 1,
                uniforms, textures.Where(t => Declares(t.Name)).ToArray(),
                Buffers: buffers.Where(b => Declares(b.Name)).ToArray()));
        }
        graph.Dispatch(screenProbeFilterPassHandle, new DispatchCommand(screenProbeFilterPipeline, (count + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uTarget", new Vector4Uniform(new Vector4(frameWidth, frameHeight, tiles.Item1, tiles.Item2))),
                new("uParams", new Vector4Uniform(new Vector4(screenProbeFilterRadius, screenProbeYoungWide ? 0f : 1f, 0f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("ScreenProbeTiles", currentTiles),
                new ShaderBufferBinding("ScreenProbes", current),
                new ShaderBufferBinding("ScreenProbesFiltered", screenProbeFiltered),
            }));
        screenProbePrevViewProj = viewProj;
        screenProbeHistoryValid = true;
        screenProbeFrame++;
    }

    // What the incident pass binds: this frame's probes, or a placeholder when they are off.
    private (Vector4 Screen, Vector4 FrameSize, GpuBufferHandle Tiles, GpuBufferHandle Probes) ScreenProbeIncidentInputs(int frameWidth, int frameHeight)
    {
        if (ScreenProbesActive && screenProbeTiles.X > 0)
        {
            return (new Vector4(1f, screenProbeTiles.X, screenProbeTiles.Y, noIncidentGradient ? 1f : 0f), new Vector4(frameWidth, frameHeight, 0f, 0f),
                screenProbeTileBuffers[screenProbeCurrent], screenProbeFiltered);
        }
        if (screenProbeDummy.Equals(default(GpuBufferHandle)))
        {
            screenProbeDummy = Own(device.CreateGpuBuffer(ScreenProbeBytes, name: "sponza.screen-probes.none"));
            screenProbeTileDummy = Own(device.CreateGpuBuffer(ScreenProbeTileBytes, name: "sponza.screen-probe-tiles.none"));
        }
        return (new Vector4(0f, 0f, 0f, noIncidentGradient ? 1f : 0f), new Vector4(frameWidth, frameHeight, 0f, 0f), screenProbeTileDummy, screenProbeDummy);
    }

    // At the shot: how many frames each placed probe has accumulated (a probe that keeps losing its past shows only
    // this frame's few rays, which flickers).
    private void WriteScreenProbeCheck()
    {
        if (!ScreenProbesActive || screenProbeTiles.X == 0) return;
        var count = screenProbeTiles.X * screenProbeTiles.Y * ScreenProbeLayers;
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
            device.ReadGpuBuffer(screenProbeBuffers[screenProbeCurrent], 0, count * ScreenProbeBytes).AsSpan()).ToArray();
        var stats2 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(screenProbeStats, 0, 64).AsSpan()).ToArray();
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
            if (n < 7.5f) young.Add(string.Create(Inv, $"{i / ScreenProbeLayers % screenProbeTiles.X},{i / ScreenProbeLayers / screenProbeTiles.X}"));
        }
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] screen probes after {screenProbeFrame} frames: {placed} probes placed in {count} slots ({stats2[8]} second-layer placements over the run); frames accumulated: 1 {bins[0]}, 2-7 {bins[1]}, 8-31 {bins[2]}, 32-63 {bins[3]}, 64+ {bins[4]}."));
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
        if (MoverActive)
        {
            // The mover's probes against the rest: how many, how settled, and how bright their radiance's mean term is.
            var on = new List<(float Frames, float Dc)>();
            var off = new List<(float Frames, float Dc)>();
            for (var i = 0; i < count; i++)
            {
                if (floats[i * ScreenProbeFloats + 3] <= 0f) continue;
                var key = BitConverter.SingleToUInt32Bits(floats[i * ScreenProbeFloats + 8]);
                var b = i * ScreenProbeFloats + 12;
                var d = i * ScreenProbeFloats + 48;
                var sample = (floats[i * ScreenProbeFloats + 7], 0.2126f * (floats[b] + floats[d]) + 0.7152f * (floats[b + 1] + floats[d + 1]) + 0.0722f * (floats[b + 2] + floats[d + 2]));
                (moverKeys.Contains(key) ? on : off).Add(sample);
            }
            string Describe(List<(float Frames, float Dc)> l) => l.Count == 0 ? "none" : string.Create(Inv,
                $"{l.Count} probes, frames median {l.Select(x => x.Frames).OrderBy(x => x).ElementAt(l.Count / 2):0}, mean radiance (SH mean term) {l.Average(x => x.Dc):0.0000}");
            Console.WriteLine($"[VulkanSponza] screen probes on the mover: {Describe(on)}; elsewhere: {Describe(off)}");
        }
        var stats = stats2;
        double total = (double)stats[0] + stats[1] + stats[4] + stats[6];
        string Pct(int i) => string.Create(Inv, $"{100.0 * stats[i] / Math.Max(1.0, total):0.0}%");
        Console.WriteLine($"[VulkanSponza] screen probe history over the run: kept {Pct(6)}; started afresh because the nearest candidate failed: no candidate {Pct(0)}, another surface (key) {Pct(1)}, outside support {Pct(4)}; a path through a moving reach {Pct(5)}");
        // Displacement, a diagnostic only: of the probes that kept a past, how many sit on a point that moved in the
        // world, how far on average, and how many moved further than their own footprint. Not staleness: surface motion
        // is not lighting invalidation (the mover: a still probe beside it went +59% stale), and it must not become the
        // invalidation rule (stage 4f).
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] screen probe displacement: kept on a moving point {Pct(2)}, mean world displacement {(stats[2] > 0 ? stats[7] / (double)stats[2] : 0):0.0} mm, moved beyond its footprint {Pct(3)}"));
    }
}
