using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The camera-relative probe clipmap (stage 4c), the scene's diffuse GI, solved by clipmap_inject.comp through the ray
// scene with nothing baked. Blix.Geometry's ProbeClipmap places its probes; this keeps the GPU side in step with it every
// frame (where each level's block is), owns the atlases and the per-slot state, and checks at the shot that the
// GPU's addressing is the C# contract's.
internal sealed partial class SponzaLoop
{
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
    // Stage 4f: the irradiance's two parts, private to the injection (their sum is clipmapIrradiance).
    private TextureHandle clipmapStatic;
    private TextureHandle clipmapDynamic;
    // --clipmap-dynamic-converge B: the dynamic part's blend per update (1: no history).
    private float clipmapDynamicConverge = 0.5f;
    // --clipmap-dependent-rays K: of a probe's 64 rays, how many must cross the moving reach for it to be re-solved first.
    private int clipmapDependentRays = 32;
    // --clipmap-dependent-share F: at most this share of the budget re-solves dependent probes a frame.
    private float clipmapDependentShare = 0.5f;
    // --clipmap-shadow-rays: every hit's sun by a shadow ray, as before the cascades were consulted (the A/B).
    private bool clipmapShadowRays;
    // --clipmap-young-rays P: a probe's first solve traces P passes of 64 rays, its second to fourth P/2 (clipmap_inject:
    // its first solve is the whole answer until the next, and its noise is the colour a wall flashes after a scroll).
    // 1: one pass, as every solve.
    private int clipmapYoungRays = 1;
    // --clipmap-wavefront: the probes' solves as a wavefront on the ray batches (SponzaLoop.RayBatch.cs; clipmap_inject
    // modes 3-5), on the software traversal; with frame rays on Metal (--ray-backend hw|both, --rt-shadows) always, so
    // Metal answers them. The inline solve (mode 2) otherwise.
    // --clipmap-inline: the inline solve whatever the frame's rays (the A/B).
    private bool clipmapWavefront, clipmapInline;
    private bool ClipmapOnWavefront => !clipmapInline && (clipmapWavefront || FrameRaysOnHardware);
    private RayBatchPasses clipmapClosestBatch, clipmapShadowBatch, clipmapVisibilityBatch;
    private PassHandle clipmapShadePassHandle, clipmapResolvePassHandle, clipmapVisibilityPassHandle;
    // With --clipmap-exact-visibility on the wavefront: each hit's rays to the probes its read weighs (16 slots a ray,
    // clipmap_inject's VIS_SLOTS) through a CLOSEST batch, and the read replayed with their answers (mode 6).
    private const int ClipmapVisSlots = 16;
    private bool ClipmapVisOnWavefront => ClipmapOnWavefront && clipmapExactVisibility;
    // The wavefront's buffers, sized for the budget's probes at clipmapYoungRays slots a lane (placeholders while off:
    // the program declares them either way).
    private GpuBufferHandle clipmapWaveRays, clipmapWaveHits, clipmapWaveState, clipmapWaveOrigins, clipmapWaveOccluded, clipmapWaveCounters;
    private GpuBufferHandle clipmapVisRays, clipmapVisHits;
    private int clipmapWaveRayCount = -1, clipmapVisRayCount = -1;

    private void EnsureClipmapWaveBuffers()
    {
        var rays = ClipmapOnWavefront ? clipmapBudget * 64 * clipmapYoungRays : 1;
        var visRays = ClipmapVisOnWavefront ? rays * ClipmapVisSlots : 1;
        if (visRays != clipmapVisRayCount)
        {
            if (clipmapVisRayCount >= 0)
            {
                device.WaitIdle();
                Disown(clipmapVisRays);
                Disown(clipmapVisHits);
            }
            clipmapVisRays = Own(device.CreateGpuBuffer(visRays * 32, name: "sponza.clipmap.vis-rays"));
            clipmapVisHits = Own(device.CreateGpuBuffer(visRays * 48, name: "sponza.clipmap.vis-hits"));
            clipmapVisRayCount = visRays;
        }
        if (rays == clipmapWaveRayCount) return;
        if (clipmapWaveRayCount >= 0)
        {
            device.WaitIdle();
            foreach (var b in new[] { clipmapWaveRays, clipmapWaveHits, clipmapWaveState, clipmapWaveOrigins, clipmapWaveOccluded, clipmapWaveCounters })
                Disown(b);
        }
        clipmapWaveRays = Own(device.CreateGpuBuffer(rays * 32, name: "sponza.clipmap.wave-rays"));
        clipmapWaveHits = Own(device.CreateGpuBuffer(rays * 48, name: "sponza.clipmap.wave-hits"));
        clipmapWaveState = Own(device.CreateGpuBuffer(rays * 64, name: "sponza.clipmap.wave-state"));
        clipmapWaveOrigins = Own(device.CreateGpuBuffer(rays * 16, name: "sponza.clipmap.wave-origins"));
        clipmapWaveOccluded = Own(device.CreateGpuBuffer(rays * 4, name: "sponza.clipmap.wave-occluded"));
        clipmapWaveCounters = Own(device.CreateGpuBuffer(Math.Max(1, clipmapBudget) * 16, name: "sponza.clipmap.wave-counters"));
        clipmapWaveRayCount = rays;
    }

    private IEnumerable<ShaderBufferBinding> ClipmapWaveBindings()
    {
        EnsureClipmapWaveBuffers();
        yield return new ShaderBufferBinding("InjectRays", clipmapWaveRays);
        yield return new ShaderBufferBinding("InjectHits", clipmapWaveHits);
        yield return new ShaderBufferBinding("InjectRayState", clipmapWaveState);
        yield return new ShaderBufferBinding("InjectShadowOrigins", clipmapWaveOrigins);
        yield return new ShaderBufferBinding("InjectOccluded", clipmapWaveOccluded);
        yield return new ShaderBufferBinding("InjectProbeCounters", clipmapWaveCounters);
        yield return new ShaderBufferBinding("InjectVisRays", clipmapVisRays);
        yield return new ShaderBufferBinding("InjectVisHits", clipmapVisHits);
    }
    private GpuBufferHandle clipmapState;
    private GpuBufferHandle clipmapQueue;
    private ShaderInterface clipmapInterface = null!;
    private PassHandle clipmapPassHandle;
    private PipelineHandle clipmapPipeline;
    private int clipmapFrame;
    // --clipmap-freeze N: stop solving after N frames, the clipmap then held as it is: a control for what its own
    // re-solving moves (every 512 probes a frame re-blended at 25%).
    private int clipmapFreeze = int.MaxValue;
    private ShaderInterface incidentClipmapInterface = null!;
    private bool incidentLean = true;
    private PipelineHandle incidentClipmapPipeline;

    private Vector4 ClipmapOrigin(int level) => clipmap is { } c
        ? new Vector4(c.Origins[level].X, c.Origins[level].Y, c.Origins[level].Z, 0f) : Vector4.Zero;
    // The fog binds the state buffer whether or not there is a clipmap (a hole is a device loss); uFogParams.z gates it.
    private GpuBufferHandle clipmapStatePlaceholder;
    private GpuBufferHandle ClipmapStateOrPlaceholder => clipmap is not null ? clipmapState
        : clipmapStatePlaceholder.Equals(default(GpuBufferHandle))
            ? clipmapStatePlaceholder = Own(device.CreateGpuBuffer(16, name: "sponza.clipmap.state.none")) : clipmapStatePlaceholder;

    // The clipmap is what the incident field and the lit pass read once it has been solved at least once.
    private bool probeUpdatesOff;
    private bool ClipmapActive => clipmap is not null && clipmapFrame > 0;

    // The incident field from the clipmap (incident_clipmap.frag): rgb the light arriving, a sky visibility.
    // half: into the half-size targets (RenderSettings.HalfResLighting), each pixel standing for LightingSubpixel of its 2x2;
    // RecordLightingUpsample brings them back.
    private void RecordIncidentClipmap(Matrix4x4 invProjection, Matrix4x4 invView, int width, int height, int frameWidth, int frameHeight,
        bool half = false)
    {
        var sub = LightingSubpixel;
        var origins = clipmap!.Origins;
        var screen = ScreenProbeIncidentInputs(frameWidth, frameHeight);
        var surface = SurfaceUniforms(ClipmapLevels * clipmap.ProbesPerLevel);
        var texel = TexelIncidentInputs();
        var reference = ReferenceIncidentInputs();
        var pick = TexelPickInputs(frameWidth, frameHeight);
        // The lean build where nothing it compiles out is on (incident_lean.frag); --no-incident-lean: never.
        var lean = incidentLean && pick.Code == 0f && reference.Params.X <= 0.5f && reference.Params.W <= 0.5f
            && screen.Screen.X <= 0.5f && IncidentStop == 0f;
        graph.Pass(half ? incidentHalfPassHandle : incidentPassHandle, scope => fullscreen.Draw(
            scope, half ? (lean ? incidentHalfLeanPipeline : incidentHalfPipeline) : (lean ? incidentLeanPipeline : incidentClipmapPipeline),
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                new ShaderTextureBinding("uClipmapIrradiance", clipmapIrradiance),
                new ShaderTextureBinding("uClipmapDepth", clipmapDepth),
                new ShaderTextureBinding("uIrradiance", irradianceCubeTexture),
            }.Concat(TexelCascadeTextures()).Append(LightmapTexelTexture()).Concat(IncidentReuseTextures()).ToArray(),
            pushConstants: null,
            uniforms: new ShaderUniform[]
            {
                new("uInvProjection", new Matrix4x4Uniform(invProjection)),
                new("uInvView", new Matrix4x4Uniform(invView)),
                new("uTarget", new Vector4Uniform(new Vector4(width, height, 1f / width, 1f / height))),
                new("uClipDims", new Vector4Uniform(new Vector4(ClipmapDims.X, ClipmapDims.Y, ClipmapDims.Z, clipmapSpacing))),
                new("uClipParams", new Vector4Uniform(new Vector4(ClipmapBlend, levelProbeForced + 1, clipmapLift, clipmapVisibilityPower))),
                new("uOrigin0", new Vector4Uniform(new Vector4(origins[0].X, origins[0].Y, origins[0].Z, 0f))),
                new("uOrigin1", new Vector4Uniform(new Vector4(origins[1].X, origins[1].Y, origins[1].Z, 0f))),
                new("uOrigin2", new Vector4Uniform(new Vector4(origins[2].X, origins[2].Y, origins[2].Z, 0f))),
                new("uOrigin3", new Vector4Uniform(new Vector4(origins[3].X, origins[3].Y, origins[3].Z, 0f))),
                new("uScreen", new Vector4Uniform(screen.Screen)),
                new("uFrameSize", new Vector4Uniform(screen.FrameSize)),
                new("uSurfaceGrid", new Vector4Uniform(surface.Grid)),
                new("uSurfaceDims", new Vector4Uniform(surface.Dims)),
                new("uSurfaceAtlas", new Vector4Uniform(surface.Atlas)),
                new("uTexelGrid", new Vector4Uniform(texel.Grid)),
                new("uTexelParams", new Vector4Uniform(texel.Params)),
                new("uReference", new Vector4Uniform(reference.Params)),
                new("uClipExact", new Vector4Uniform(new Vector4(incidentExactVisibility ? 1f : 0f, IncidentCensusNow(half) ? 1f : 0f, UnmappedCoarse ? 1f : 0f, AtlasGatherNow ? 1f : 0f))),
                new("uTexelDebug", new Vector4Uniform(new Vector4(texelFrame, texelResting ? 1f : 0f, texelDetailOn && TexelDetailUp ? 1f : 0f, pick.Code))),
                new("uLightmap", new Vector4Uniform(LightmapUniform)),
                new("uTexelLevels", new Vector4Uniform(TexelLevelsUniform(frameHeight))),
                new("uHalf", new Vector4Uniform(new Vector4(half ? 1f : 0f, sub.X, sub.Y, PatchViewColumns))),
                new("uReuse", new Vector4Uniform(IncidentReuseUniform(half))),
                new("uSeam", new Vector4Uniform(new Vector4(SeamBlendNow ? 1f : 0f, TexelShapeOn ? 1f : 0f, LightmapCellsNow ? 1f : 0f, IncidentStop))),
            }.Concat(TexelSunUniforms()).ToArray(),
            buffers: new[] { new ShaderBufferBinding("ClipmapState", clipmapState), new ShaderBufferBinding("ScreenProbeTiles", screen.Tiles), new ShaderBufferBinding("ScreenProbes", screen.Probes), new ShaderBufferBinding("SurfaceIndex", surfaceIndex) }.Concat(texel.Buffers).Append(reference.Buffer).Append(new ShaderBufferBinding("IncidentCensus", IncidentCensusBuffer())).Append(AtlasGatherMeta()).Append(AtlasIndexBuffer()).Append(LightmapCellsBinding())
                .Append(ProvenanceBinding()).Append(new ShaderBufferBinding("TexelShape", ShapeOr(texelShapeBuffer))).Append(TexelRaysBinding()).Append(pick.Buffer).Concat(PatchViewBindings()).Append(ReferenceMomentsBinding())
                .Concat(SeamBlendBindings())
                // The ray scene, for --clipmap-exact-visibility (declared always; read only then).
                .Concat(rayBlockBuffers).ToArray()));
    }

    // BLIX_INCIDENT_STOP=1 / 2: the incident pass out before / right after its texels are read (incident_clipmap.frag) --
    // a probe of where its time goes, not a mode to run.
    private static readonly float IncidentStop = float.TryParse(Environment.GetEnvironmentVariable("BLIX_INCIDENT_STOP"), out var stop) ? stop : 0f;

    private const int ClipmapLevels = 4;
    // Probes per level, x y z. --clipmap-height N sets y (16: level 0 reaches +-4 m vertically, 32: +-8 m); stage 4g-viii.
    private Int3 ClipmapDims = new(32, 16, 32);
    // The depth moments' lobe: sharper keeps walls as edges, but each texel then hears fewer of 64 rays.
    private const float ClipmapDepthLobe = 12f;
    // What a probe's light that crosses no moving reach can still be stale by: nothing, short of the slot being re-pointed
    // when the clipmap scrolls (which restarts its count). So it averages every solve evenly (1/n, to n = 255): with the
    // bounce read back from neighbours that are themselves converging, that is a stochastic approximation of the
    // fixed point, where a fixed 25% blend kept wandering (dark, bounce-lit areas worst; --stability, d175e53).
    // --clipmap-converge-floor sets a floor for the A/B.
    private float clipmapConvergeFloor;
    // After the sun turns (more than a quarter degree from the last direction the probes were solved under), the static
    // light blends at no less than this per solve for ClipmapSunTurnFrames: under 1/n a probe at n = 255 kept the old
    // sun's bounce light for hundreds of re-solves.
    private const float ClipmapSunTurnFloor = 0.2f;
    private const int ClipmapSunTurnFrames = 240;
    private Vector3 clipmapSolvedSun;
    private int clipmapSunTurnFrame = -1;
    private float ClipmapStaticFloor()
    {
        var toSun = -Vector3.Normalize(sunDirection);
        if (clipmapSolvedSun == Vector3.Zero) clipmapSolvedSun = toSun;
        if (Vector3.Dot(toSun, clipmapSolvedSun) < MathF.Cos(0.25f * MathF.PI / 180f))
        {
            clipmapSolvedSun = toSun;
            clipmapSunTurnFrame = clipmapFrame;
        }
        var turning = clipmapSunTurnFrame >= 0 && clipmapFrame - clipmapSunTurnFrame < ClipmapSunTurnFrames;
        return turning ? MathF.Max(clipmapConvergeFloor, ClipmapSunTurnFloor) : clipmapConvergeFloor;
    }
    // --clipmap-lift M / --clipmap-visibility-power P: the incident pass's lookup lift (0: a quarter of the spacing)
    // and Chebyshev power (0: 3). Knobs for the level probe (stage 4g-v), not tuned defaults.
    private float clipmapLift;
    private float clipmapVisibilityPower;
    private bool clipmapExactVisibility, incidentExactVisibility;
    // The blend band between levels, in probes, as every reader takes it (the overlay's slider; -1: the clipmap's own).
    // Under 7.5: the band must fit inside a level 16 probes tall.
    private float clipmapBlendOverride = -1f;
    private float ClipmapBlend => clipmap is null ? 0f : clipmapBlendOverride >= 0f ? clipmapBlendOverride : clipmap.BlendProbes;
    // --clipmap-young N: probes with fewer than N solves are queued before the round-robin, fewest first (0: off, the
    // A/B). Up to 16 (four buckets: 1, 2-3, 4-7, 8-15).
    private float clipmapYoung = 8f;
    private const int ClipmapYoungBuckets = 4;
    // Stage 4g-iii: the probes the image reads get up to this share of the budget, fewest solves first (8 buckets).
    private float clipmapVisibleShare = 0.75f;
    private const int ClipmapVisibleBuckets = 8;
    private const int ClipmapBuckets = ClipmapVisibleBuckets + ClipmapYoungBuckets;
    private GpuBufferHandle clipmapSeen;
    // Stage 4g-iv: --clipmap-guide F: half of each solve's rays go where the probe's radiance bins say the light is
    // (0: every ray uniform, the A/B); F is the bins' floor as a share of their mean (default 0.25).
    private bool clipmapGuide = true;
    private float clipmapGuideFloor = 0.25f;
    private TextureHandle clipmapRadiance;
    private ShaderInterface clipmapMarkInterface = null!;
    private PassHandle clipmapMarkPassHandle;
    private PipelineHandle clipmapMarkPipeline;
    private const int ClipmapMarkStride = 4;

    private void CreateClipmap()
    {
        if (clipmap is not null) return;
        clipmap = clipmapBlendOverride >= 0f
            ? new ProbeClipmap(ClipmapLevels, ClipmapDims, clipmapSpacing, clipmapBlendOverride)
            : new ProbeClipmap(ClipmapLevels, ClipmapDims, clipmapSpacing);
        // The surface probes' tiles sit below the clipmap's in the same atlases (stage 4g-ix).
        var atlasHeight = CreateSurfaceProbes(clipmap.AtlasHeight, clipmap.AtlasWidth);
        clipmapIrradiance = Own(device.CreateStorageTexture2D(clipmap.AtlasWidth, atlasHeight, TextureFormat.Rgba16F,
            SamplerDescription.LinearClamp, "sponza.clipmap.irradiance"));
        clipmapDepth = Own(device.CreateStorageTexture2D(clipmap.AtlasWidth, atlasHeight, TextureFormat.Rgba16F,
            SamplerDescription.LinearClamp, "sponza.clipmap.depth"));
        clipmapRadiance = Own(device.CreateStorageTexture2D(clipmap.AtlasWidth, atlasHeight, TextureFormat.Rgba16F,
            SamplerDescription.LinearClamp, "sponza.clipmap.radiance"));
        clipmapStatic = Own(device.CreateStorageTexture2D(clipmap.AtlasWidth, atlasHeight, TextureFormat.Rgba16F,
            SamplerDescription.LinearClamp, "sponza.clipmap.static"));
        clipmapDynamic = Own(device.CreateStorageTexture2D(clipmap.AtlasWidth, atlasHeight, TextureFormat.Rgba16F,
            SamplerDescription.LinearClamp, "sponza.clipmap.dynamic"));
        var slots = ClipmapLevels * clipmap.ProbesPerLevel;
        var total = slots + surfaceCapacity;
        // Every clipmap slot starts holding a cell no block contains, unsolved: the first scan re-points and queues all of
        // them. Surface slots start empty (not ALLOCATED) until clipmap_mark.comp allocates them.
        var initial = new uint[total * 4];
        for (var i = 0; i < slots; i++) { initial[i * 4] = 0x80000000u; initial[i * 4 + 1] = 0x80000000u; initial[i * 4 + 2] = 0x80000000u; }
        clipmapState = Own(device.CreateGpuBuffer(total * 16, MemoryMarshal.AsBytes(initial.AsSpan()), "sponza.clipmap.state"));
        // Each slot's probe offset from its cell's centre (--clipmap-relocate, clipmap_inject.comp), zero to begin.
        // Counters, the per-level unsolved queues, the dependent queue (stage 4f), the cursor, and the buckets' counts and
        // lists (every slot's worth, surface slots included): visible probes by solves (4g-iii), then young ones off
        // screen (4g-ii).
        clipmapQueue = Own(device.CreateGpuBuffer((8 + 2 * slots + 1 + ClipmapBuckets * (1 + total)) * 4, name: "sponza.clipmap.queue"));
        clipmapSeen = Own(device.CreateGpuBuffer(total * 4, new byte[total * 4], "sponza.clipmap.seen"));
        // The lit pass's glass reads the clipmap: swap its placeholders for the real thing.
        if (passBindings is not null)
        {
            for (var i = 0; i < passBindings.Length; i++)
            {
                if (passBindings[i].Name == "uClipmapIrradiance") passBindings[i] = new ShaderTextureBinding("uClipmapIrradiance", clipmapIrradiance);
                else if (passBindings[i].Name == "uClipmapDepth") passBindings[i] = new ShaderTextureBinding("uClipmapDepth", clipmapDepth);
            }
        }
        litSceneBuffers = null;
        litClipmapBuffer = null;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] probe clipmap: {ClipmapLevels} levels of {ClipmapDims.X}x{ClipmapDims.Y}x{ClipmapDims.Z} probes, spacing {clipmapSpacing:0.##} m doubling (level {ClipmapLevels - 1} spans {clipmap.Spacing(ClipmapLevels - 1) * ClipmapDims.X:0} m), atlases {clipmap.AtlasWidth}x{clipmap.AtlasHeight}, {clipmapBudget} probes a frame."));
    }

    // The pixels the last incident census found reading the probes (-1: no census yet).
    private long clipmapProbeReaders = -1;
    private bool clipmapAlways;
    // --freeze-cascades (measurement): the sun cascades fitted once, never refit or redrawn.
    private bool freezeCascades;
    // --unmapped-coarse: pixels the atlas leaves out read one coarser texel level alone (incident_clipmap.frag,
    // uClipExact.z). Off by default: ~2 ms at the tree, but the user saw pops and tears on the leaves while moving
    // (2026-10-20). --ab leaflookup alternates the two.
    private bool unmappedCoarse;
    private bool UnmappedCoarse => abMode == "leaflookup" ? !AbOffPhase : unmappedCoarse;
    private long clipmapRested;

    private void RecordClipmap()
    {
        if (clipmap is null || rayBlockBuffers.Length == 0 || !RaySurfacesReady) return;
        if (clipmapFrame >= clipmapFreeze) return;
        // --live-texels: the probes solved only while something reads them. With the texels at rest every pixel took the
        // cells alone (the census: 100%), and the probes' solve -- Metal's closest rays 4-7.5 ms a frame and its shadow
        // batch -- lit nothing (2026-10-19). Solved again once a census sees a pixel read them, the texels leave rest, or
        // the mover moves. --clipmap-always: every frame, as before.
        if (liveTexels && !clipmapAlways && clipmapProbeReaders == 0 && texelResting && !MoverActive)
        {
            clipmapRested++;
            return;
        }
        MarkIncidentDirty();   // the probes solve: what the incident pass reads where texels do not answer changes
        // Staged (ClipmapStaged): a wave in flight holds its blocks where they were when it started -- the slots it
        // picked must still be the cells it traced when it resolves -- so the clipmap follows the camera only as a wave
        // starts (frame A), and the wave's later phases run with its own origins, frame and seed.
        var staged = ClipmapOnWavefront && ClipmapStaged;
        var stageB = staged && clipmapWaveStage == 1;
        if (!stageB) clipmap.Follow(cameraPosition);
        var origins = new Vector4[4];
        for (var l = 0; l < ClipmapLevels; l++) origins[l] = new Vector4(clipmap.Origins[l].X, clipmap.Origins[l].Y, clipmap.Origins[l].Z, 0f);
        var slots = ClipmapLevels * clipmap.ProbesPerLevel;
        var textures = new[]
        {
            new ShaderTextureBinding("uClipmapIrradiance", clipmapIrradiance),
            new ShaderTextureBinding("uClipmapDepth", clipmapDepth),
            new ShaderTextureBinding("uClipmapStatic", clipmapStatic),
            new ShaderTextureBinding("uClipmapDynamic", clipmapDynamic),
            new ShaderTextureBinding("uClipmapRadiance", clipmapRadiance),
            new ShaderTextureBinding("uIrradiance", irradianceCubeTexture),
            new ShaderTextureBinding("uSkyRadiance", envCubeTexture),
            new ShaderTextureBinding("uCascadeShadowMaps[0]", graph.GetDepthTexture(cascadeHandles[0])),
            new ShaderTextureBinding("uCascadeShadowMaps[1]", graph.GetDepthTexture(cascadeHandles[1])),
            new ShaderTextureBinding("uCascadeShadowMaps[2]", graph.GetDepthTexture(cascadeHandles[2])),
        };
        // The reach of what moved this frame: the mover's while it moves (stage 4f).
        // The reach classifies paths whenever there is a mover, held or not: the static part averages every solve
        // (1/n), so a path through the reach counted static while the mover held would stay stale once it moved.
        // Priority re-solving is for when it moved this frame (uDynamicQueue.w).
        var dynamicReach = MoverActive;
        var dynamicActive = dynamicReach && moverMoved;
        var buffers = rayBlockBuffers
            .Append(new ShaderBufferBinding("ClipmapState", clipmapState))
            .Append(new ShaderBufferBinding("ClipmapQueue", clipmapQueue))
            .Append(new ShaderBufferBinding("ClipmapSeen", clipmapSeen))
            .Append(new ShaderBufferBinding("SurfaceIndex", surfaceIndex))
            .Concat(ClipmapWaveBindings()).ToArray();
        var surface = SurfaceUniforms(slots);
        var wave = ClipmapOnWavefront;
        var staticFloor = ClipmapStaticFloor();
        DispatchCommand Phase(int mode, int groups) => PhaseOf(mode, groups, origins, clipmapFrame);
        DispatchCommand PhaseOf(int mode, int groups, Vector4[] o, int solveFrame) => new(clipmapPipeline, groups, 1, 1, new ShaderUniform[]
        {
            new("uDims", new Vector4Uniform(new Vector4(ClipmapDims.X, ClipmapDims.Y, ClipmapDims.Z, clipmapSpacing))),
            new("uParams", new Vector4Uniform(new Vector4(ClipmapBlend, staticFloor, mode, clipmapBudget))),
            new("uOrigin0", new Vector4Uniform(o[0])),
            new("uOrigin1", new Vector4Uniform(o[1])),
            new("uOrigin2", new Vector4Uniform(o[2])),
            new("uOrigin3", new Vector4Uniform(o[3])),
            new("uSunDirection", new Vector4Uniform(new Vector4(sunDirection, 0f))),
            new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 1f))),
            new("uFrame", new Vector4Uniform(new Vector4(solveFrame, 1f, 1f, ClipmapDepthLobe))),
            new("uExact", new Vector4Uniform(new Vector4(clipmapExactVisibility ? 1f : 0f, 0f, 0f, 0f))),
            // y: the prefiltered sky's mip a probe ray reads, about a ray's 1/64 of the sphere (a ~15 degree cone).
            new("uSurfaceGrid", new Vector4Uniform(surface.Grid)),
            new("uSurfaceDims", new Vector4Uniform(surface.Dims)),
            new("uSurfaceAtlas", new Vector4Uniform(surface.Atlas)),
            new("uGuide", new Vector4Uniform(new Vector4(clipmapGuide ? 1f : 0f, clipmapGuideFloor, clipmapYoungRays, 0f))),
            new("uFallback", new Vector4Uniform(new Vector4(clipmapUnknownSky, MathF.Min(1.5f, Math.Max(0, iblPrefilterMips - 1)), clipmapYoung, MathF.Floor(clipmapBudget * clipmapVisibleShare)))),
            new("uDynamicMin", new Vector4Uniform(dynamicReach ? new Vector4(moverReach.Min, 1f) : Vector4.Zero)),
            new("uDynamicMax", new Vector4Uniform(new Vector4(MoverActive ? moverReach.Max : Vector3.Zero, clipmapDynamicConverge))),
            new("uDynamicQueue", new Vector4Uniform(new Vector4(clipmapDependentRays, MathF.Floor(clipmapBudget * clipmapDependentShare), clipmapShadowRays ? 1f : 0f, dynamicActive ? 1f : 0f))),
            new("uCascadeVP", new Matrix4x4ArrayUniform(cascadeViewProj)),
            new("uCascadeReceivers", new Matrix4x4Uniform(CascadeReceivers)),
            new("uWave", new Vector4Uniform(new Vector4(wave ? 1f : 0f, clipmapYoungRays, 0f, 0f))),
        }, textures, Buffers: buffers);
        if (staged)
        {
            // Frame B: the wave's hits shaded, its sun out (and its visibility rays) -- read next frame.
            var w = clipmapWave;
            if (stageB)
            {
                graph.Dispatch(clipmapShadePassHandle, PhaseOf(4, clipmapBudget, w.Origins, w.Frame));
                RecordShadowBatch(clipmapShadowBatch, clipmapWaveOrigins, clipmapWaveOccluded, w.Rays, -Vector3.Normalize(sunDirection), w.Seed ^ 0x5bd1e995u, deferred: true);
                if (ClipmapVisOnWavefront)
                    RecordClosestBatch(clipmapVisibilityBatch, clipmapVisRays, clipmapVisHits, w.Rays * ClipmapVisSlots, w.Seed ^ 0x27d4eb2fu, deferred: true);
                clipmapWaveStage = 2;
                return;
            }
            // Frame A: the last wave resolved, then the next one started -- its probes picked, its rays out, read next frame.
            if (clipmapWaveStage == 2)
            {
                if (ClipmapVisOnWavefront) graph.Dispatch(clipmapVisibilityPassHandle, PhaseOf(6, clipmapBudget, w.Origins, w.Frame));
                graph.Dispatch(clipmapResolvePassHandle, PhaseOf(5, clipmapBudget, w.Origins, w.Frame));
            }
            var rays = clipmapBudget * 64 * clipmapYoungRays;
            var waveSeed = unchecked((uint)clipmapFrame * 0x9E3779B9u);
            graph.Dispatch(clipmapPassHandle, Phase(0, 1));
            graph.Dispatch(clipmapPassHandle, Phase(1, (slots + surfaceCapacity + 63) / 64));
            graph.Dispatch(clipmapPassHandle, Phase(3, clipmapBudget));
            RecordClosestBatch(clipmapClosestBatch, clipmapWaveRays, clipmapWaveHits, rays, waveSeed, deferred: true);
            clipmapWave = (origins, clipmapFrame, waveSeed, rays);
            clipmapWaveStage = 1;
            clipmapFrame++;
            return;
        }
        graph.Dispatch(clipmapPassHandle, Phase(0, 1));
        graph.Dispatch(clipmapPassHandle, Phase(1, (slots + surfaceCapacity + 63) / 64));
        if (wave)
        {
            // The solve as a wavefront: rays out, their hits shaded, the hits' sun out, the answers solved. Each batch
            // on Metal when frame rays are (RecordClosestBatch), else on the software traversal.
            var rayCount = clipmapBudget * 64 * clipmapYoungRays;
            var seed = unchecked((uint)clipmapFrame * 0x9E3779B9u);
            graph.Dispatch(clipmapPassHandle, Phase(3, clipmapBudget));
            RecordClosestBatch(clipmapClosestBatch, clipmapWaveRays, clipmapWaveHits, rayCount, seed);
            graph.Dispatch(clipmapShadePassHandle, Phase(4, clipmapBudget));
            RecordShadowBatch(clipmapShadowBatch, clipmapWaveOrigins, clipmapWaveOccluded, rayCount, -Vector3.Normalize(sunDirection), seed ^ 0x5bd1e995u);
            if (ClipmapVisOnWavefront)
            {
                RecordClosestBatch(clipmapVisibilityBatch, clipmapVisRays, clipmapVisHits, rayCount * ClipmapVisSlots, seed ^ 0x27d4eb2fu);
                graph.Dispatch(clipmapVisibilityPassHandle, Phase(6, clipmapBudget));
            }
            graph.Dispatch(clipmapResolvePassHandle, Phase(5, clipmapBudget));
        }
        else
        {
            graph.Dispatch(clipmapPassHandle, Phase(2, clipmapBudget));
        }
        clipmapFrame++;
    }

    // The wavefront staged over frames (with the tracer's groups, SponzaLoop.TracerGroup.cs): frame A resolves the last
    // wave and starts the next (its probes picked, its rays out), frame B shades its hits and sends its sun rays; every
    // batch is read on the frame after, so none needs its own trip to Metal in the frame. In one frame, the chain went
    // to Metal and back three times, the GPU idle between (~19 ms of Sponza's 103 ms frame). A probe update takes two
    // frames instead of one; the cells answer 99.9% of Sponza's pixels at rest (the incident census), so the probes'
    // pace shows little. --clipmap-wave-inframe: the whole chain in one frame, as before.
    private bool clipmapWaveInFrame;
    private bool ClipmapStaged => !clipmapWaveInFrame && TracerGroupsOn && frameTracer is not null;
    private int clipmapWaveStage;
    private (Vector4[] Origins, int Frame, uint Seed, int Rays) clipmapWave;

    // Stamps the probes this frame's image reads (clipmap_mark.comp), for the next solve's visible queue.
    private void RecordClipmapMark(Matrix4x4 invProjection, Matrix4x4 invView, int width, int height)
    {
        if (clipmap is null || (clipmapVisibleShare <= 0f && !SurfaceProbesOn)) return;
        var origins = clipmap.Origins;
        var surface = SurfaceUniforms(ClipmapLevels * clipmap.ProbesPerLevel);
        var groups = (((width + ClipmapMarkStride - 1) / ClipmapMarkStride + 7) / 8, ((height + ClipmapMarkStride - 1) / ClipmapMarkStride + 7) / 8);
        graph.Dispatch(clipmapMarkPassHandle, new DispatchCommand(clipmapMarkPipeline, groups.Item1, groups.Item2, 1, new ShaderUniform[]
        {
            new("uInvProjection", new Matrix4x4Uniform(invProjection)),
            new("uInvView", new Matrix4x4Uniform(invView)),
            new("uTarget", new Vector4Uniform(new Vector4(width, height, ClipmapMarkStride, ClipmapMarkStride))),
            new("uDims", new Vector4Uniform(new Vector4(ClipmapDims.X, ClipmapDims.Y, ClipmapDims.Z, clipmapSpacing))),
            new("uParams", new Vector4Uniform(new Vector4(ClipmapBlend, clipmapFrame, levelProbe || forceLevel >= 0 ? 1f : 0f, 0f))),
            new("uOrigin0", new Vector4Uniform(new Vector4(origins[0].X, origins[0].Y, origins[0].Z, 0f))),
            new("uOrigin1", new Vector4Uniform(new Vector4(origins[1].X, origins[1].Y, origins[1].Z, 0f))),
            new("uOrigin2", new Vector4Uniform(new Vector4(origins[2].X, origins[2].Y, origins[2].Z, 0f))),
            new("uOrigin3", new Vector4Uniform(new Vector4(origins[3].X, origins[3].Y, origins[3].Z, 0f))),
            new("uSurfaceGrid", new Vector4Uniform(surface.Grid)),
            new("uSurfaceDims", new Vector4Uniform(surface.Dims)),
            new("uSurfaceAtlas", new Vector4Uniform(surface.Atlas)),
            new("uSurfaceCamera", new Vector4Uniform(new Vector4(cameraPosition, surfaceRadius))),
        }, new[]
        {
            new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
            new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
        }, Buffers: new[]
        {
            new ShaderBufferBinding("ClipmapSeen", clipmapSeen),
            new ShaderBufferBinding("ClipmapState", clipmapState),
            new ShaderBufferBinding("SurfaceIndex", surfaceIndex),
            new ShaderBufferBinding("SurfaceAlloc", surfaceAlloc),
        }));
    }

    // At the shot: every slot's state against the C# contract (the scan re-points each one to the cell its level's
    // block now puts there), and how much of each level is solved and buried.
    private void WriteClipmapCheck()
    {
        if (clipmap is null || clipmapFrame == 0) return;
        var slots = ClipmapLevels * clipmap.ProbesPerLevel;
        var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(clipmapState, 0, (slots + surfaceCapacity) * 16).AsSpan()).ToArray();
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
        // How many solves the probes hold (state bits 8-15), and how many were queued as young last frame.
        var solveBuckets = new int[5];
        for (var g = 0; g < slots; g++)
        {
            if ((words[g * 4 + 3] & 1u) == 0) continue;
            var n = (words[g * 4 + 3] >> 8) & 0xFFu;
            solveBuckets[n < 2 ? 0 : n < 4 ? 1 : n < 8 ? 2 : n < 32 ? 3 : 4]++;
        }
        var bucketCounts = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(clipmapQueue, (8 + 2 * slots + 1) * 4, ClipmapBuckets * 4).AsSpan()).ToArray();
        // The visible probes' own solves (what the image is made of), from the stamps.
        var seenStamps = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(clipmapSeen, 0, (slots + surfaceCapacity) * 4).AsSpan()).ToArray();
        WriteSurfaceCensus(words, seenStamps, slots);
        var visibleSolves = new List<uint>();
        for (var g = 0; g < slots; g++)
            if (seenStamps[g] + 2 > clipmapFrame && (words[g * 4 + 3] & 1u) != 0) visibleSolves.Add((words[g * 4 + 3] >> 8) & 0xFFu);
        visibleSolves.Sort();
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] probe clipmap solves: 1 {solveBuckets[0]:N0}, 2-3 {solveBuckets[1]:N0}, 4-7 {solveBuckets[2]:N0}, 8-31 {solveBuckets[3]:N0}, 32+ {solveBuckets[4]:N0}; "
            + $"queued last frame: visible {bucketCounts.Take(ClipmapVisibleBuckets).Sum(c => (long)c):N0} (share {clipmapVisibleShare:0.##}), young {bucketCounts.Skip(ClipmapVisibleBuckets).Sum(c => (long)c):N0} (--clipmap-young {clipmapYoung:0}); "
            + $"the image reads {visibleSolves.Count:N0} probes, their solves median {(visibleSolves.Count > 0 ? visibleSolves[visibleSolves.Count / 2] : 0)}, p10 {(visibleSolves.Count > 0 ? visibleSolves[visibleSolves.Count / 10] : 0)}."));
        var counters = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(clipmapQueue, 0, 32).AsSpan()).ToArray();
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] probe clipmap, last frame: {counters[4]:N0} ray hits, {counters[5]:N0} ({100.0 * counters[5] / Math.Max(1u, counters[4]):0.0}%) where no probe answered (sky taken as {clipmapUnknownSky:0.##}); dependent probes queued {counters[7]:N0}, solved with a path through a moving reach {counters[6]:N0} (budget {clipmapBudget})."));
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] probe clipmap after {clipmapFrame} frames: slots whose cell differs from ProbeClipmap's {mismatched} of {slots}; solved per level {string.Join(" / ", solved)} of {clipmap.ProbesPerLevel}, buried {string.Join(" / ", buried)}."));
    }
}
