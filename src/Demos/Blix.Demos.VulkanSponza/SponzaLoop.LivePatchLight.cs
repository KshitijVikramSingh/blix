using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --live-patch-light: the patches' light solved at runtime for the lights as they are (stage 2 of the real-time GI:
// the geometry cooked once, the light the runtime's). The transport's couplings, sunlets and patch terms -- the same
// inputs the band's solve reads (SponzaLoop.SunNodesSolve.cs, texel_nodes_solve.comp) -- with two components laid out
// as the split light's columns are, the sky alone (0) and the sun alone (1), so the texels' gather reads it as it reads
// a band's (SunNodePatchLight, [patch][column]):
//   a light changes  the sun's rays from every sunlet (the shadow batch) and their first bounce onto the patches: one
//                    frame, and the bounces go on from the last answer (warm), so the coarse light follows at once
//   each frame       LiveBouncesPerFrame bounces over the couplings, and the patches' light written (LivePatchLight)
//   at rest          after LiveBouncesToRest bounces with no change: nothing until the next
// The texels' gather reads LivePatchLight in stage 3; meanwhile, once it first rests, its answer is held against the
// CPU's boot solve for the same sun (BootIncident).
internal sealed partial class SponzaLoop
{
    private bool livePatchLight, liveAlways, liveTexels;
    // The live answer settled for the lights as they are: what --live-texels' gather waits for.
    private bool LivePatchLightRested => liveInjected && liveBounces >= LiveBouncesToRest
        && Vector3.Distance(liveToSun, -Vector3.Normalize(sunDirection)) < 1e-6f;
    // A sun that turned while the cook was in its setup (tasks off the frame thread): restarted once the setup is done.
    private Vector3? liveRestartPending;
    private const int LiveBouncesPerFrame = 1;
    // --live-bounces N (48): the bounces the live solve takes to rest.
    private int LiveBouncesToRest = 48;
    private const int LiveCoarseBounces = 16;
    // The components, as the split light's columns: the sky alone, then the sun alone.
    private const int LiveComponents = 2, LiveSky = 0, LiveSun = 1;
    private PassHandle liveSolveAPass, liveSolveBPass;
    private RayBatchPasses liveSunletBatch;
    private GpuBufferHandle liveFirst, liveIn, liveOut, liveOrigins, liveOccluded, liveSkyWeights;
    // Per patch, half rgb: its incident light for the lights as they are (texel_nodes_solve.comp mode 5).
    private GpuBufferHandle livePatchLightBuffer;
    private Vector3 liveToSun, liveIrr;
    private bool liveInjected, liveChecked;
    private int liveBounces;
    private readonly System.Diagnostics.Stopwatch liveClock = new();
    private int liveFramesActive;

    private void DeclareLivePatchLight(Func<string[], ShaderInterface> reflect)
    {
        liveSunInterface = reflect(new[] { "texel_live_sun.comp" });
        DeclareHitCells(reflect);
        DeclareTransfer(reflect);
        liveDirectInterface = reflect(new[] { "texel_live_direct.comp" });
        liveGuidesInterface = reflect(new[] { "texel_live_guides.comp" });
        liveGuidesPass = graph.ComputePass("live-patch-guides").Shader(liveGuidesInterface).Handle;
        liveDirectPass = graph.ComputePass("live-patch-direct").Shader(liveDirectInterface).Handle;
        liveSunAPass = graph.ComputePass("live-sun-bits-a").Shader(liveSunInterface).Handle;
        liveSunBatch = DeclareShadowBatch("live-sun-bits");
        liveSunBPass = graph.ComputePass("live-sun-bits-b").Shader(liveSunInterface).Handle;
        liveSolveAPass = graph.ComputePass("live-patch-solve-a").Shader(sunNodeSolveInterface).Handle;
        liveSunletBatch = DeclareShadowBatch("live-patch-sunlets");
        liveSolveBPass = graph.ComputePass("live-patch-solve-b").Shader(sunNodeSolveInterface).Handle;
    }

    // --live-texels: the texels' sun bits for the cook's sun, shot in the frame a chunk at a time (texel_live_sun.comp),
    // instead of through the host. Every live texel's, not the visible ones' first: any texel may be a ray's hit.
    private const int LiveSunChunk = 4 << 20;
    private ShaderInterface liveSunInterface = null!;
    private PipelineHandle liveSunPipeline;
    private PassHandle liveSunAPass, liveSunBPass;
    private RayBatchPasses liveSunBatch;
    private GpuBufferHandle liveSunBits, liveSunOrigins, liveSunOccluded;
    private int liveSunNext;
    private bool liveSunDone;
    private Vector3 liveSunToward;
    private int liveSunColumn, liveSunWords;
    private readonly System.Diagnostics.Stopwatch liveSunClock = new();

    // The cook asks for its columns' bits: its sun's, unless the live solve shot them for that sun already.
    private void BeginLiveSunBits(Vector3[] toSuns)
    {
        var column = Array.IndexOf(sunNodeKinds, 1);
        var toward = column >= 0 ? toSuns[column] : Vector3.UnitY;
        if (liveSunDone && liveSunToward == toward && liveSunColumn == column && liveSunWords == (toSuns.Length + 31) / 32) return;
        BeginLiveSunBits(toward, column, (toSuns.Length + 31) / 32);
    }

    private void BeginLiveSunBits(Vector3 toward, int column, int words)
    {
        liveSunColumn = column;
        liveSunWords = words;
        liveSunToward = toward;
        var bytes = checked(texelCount * liveSunWords * 4);
        if (liveSunBits.Equals(default(GpuBufferHandle)))
        {
            liveSunBits = Own(device.CreateGpuBuffer(bytes, new byte[bytes], "sponza.live.sun-bits"));
            liveSunOrigins = Own(device.CreateGpuBuffer(LiveSunChunk * 16, name: "sponza.live.sun-origins"));
            liveSunOccluded = Own(device.CreateGpuBuffer(LiveSunChunk * 4, name: "sponza.live.sun-occluded"));
        }
        liveSunNext = 0;
        liveSunDone = liveSunColumn < 0;
        liveSunClock.Restart();
    }

    private long liveSunRecordedFrame = -1;

    // The sun points' bits (sunNodeFoliagePoints into sunNodeFoliageBits) for the cook's sun, the same way, after the
    // texels': live mode shoots none through the host (ShootSunBits). -1: none asked.
    private int livePointNext, livePointCount = -1;
    private readonly System.Diagnostics.Stopwatch livePointClock = new();
    private bool LivePointsDone => livePointCount < 0 || livePointNext >= livePointCount;

    private void BeginLivePointBits()
    {
        livePointCount = sunNodePoints.Length / 2;
        livePointNext = 0;
        livePointClock.Restart();
    }

    // One chunk a frame, sharing the texels' (RecordLiveSunBits) frame: their bits first.
    private void RecordLivePointBits()
    {
        if (LivePointsDone || !liveSunDone || liveSunColumn < 0 || liveSunRecordedFrame == framesRendered) return;
        liveSunRecordedFrame = framesRendered;
        var n = Math.Min(LiveSunChunk, livePointCount - livePointNext);
        var reach = rayReach;
        DispatchCommand Pass(int mode) => new(liveSunPipeline, (n + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uChunk", new Vector4Uniform(new Vector4(n, livePointNext, mode, liveSunWords))),
                new("uToSun", new Vector4Uniform(new Vector4(liveSunToward, liveSunColumn))),
                new("uRay", new Vector4Uniform(new Vector4(sunNodeLift, reach, 1f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("Texels", sunNodeFoliagePoints),
                new ShaderBufferBinding("LiveIds", splitLiveBuffer),
                new ShaderBufferBinding("LiveSunOrigins", liveSunOrigins),
                new ShaderBufferBinding("LiveSunOccluded", liveSunOccluded),
                new ShaderBufferBinding("SunNodeBits", sunNodeFoliageBits),
            });
        graph.Dispatch(liveSunAPass, Pass(0));
        RecordShadowBatch(liveSunBatch, liveSunOrigins, liveSunOccluded, n, liveSunToward, 7u);
        graph.Dispatch(liveSunBPass, Pass(1));
        livePointNext += n;
        if (livePointNext < livePointCount) return;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] live texels: sun bits for {livePointCount:N0} sun points shot in the frame in {livePointClock.Elapsed.TotalSeconds:0.0} s."));
    }

    // Called by the live solve and by the cook's setup alike: one chunk a frame (its Metal pass takes one piece).
    private void RecordLiveSunBits()
    {
        if (liveSunDone || texelLiveIds is not { } live || liveSunRecordedFrame == framesRendered) return;
        liveSunRecordedFrame = framesRendered;
        var n = Math.Min(LiveSunChunk, live.Length - liveSunNext);
        var reach = rayReach;
        DispatchCommand Pass(int mode) => new(liveSunPipeline, (n + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uChunk", new Vector4Uniform(new Vector4(n, liveSunNext, mode, liveSunWords))),
                new("uToSun", new Vector4Uniform(new Vector4(liveSunToward, liveSunColumn))),
                new("uRay", new Vector4Uniform(new Vector4(sunNodeLift, reach, 0f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("LiveIds", splitLiveBuffer),
                new ShaderBufferBinding("LiveSunOrigins", liveSunOrigins),
                new ShaderBufferBinding("LiveSunOccluded", liveSunOccluded),
                new ShaderBufferBinding("SunNodeBits", liveSunBits),
            });
        graph.Dispatch(liveSunAPass, Pass(0));
        RecordShadowBatch(liveSunBatch, liveSunOrigins, liveSunOccluded, n, liveSunToward, 7u);
        graph.Dispatch(liveSunBPass, Pass(1));
        liveSunNext += n;
        if (liveSunNext < live.Length) return;
        liveSunDone = true;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] live texels: sun bits for {live.Length:N0} texels shot in the frame, {(live.Length + LiveSunChunk - 1) / LiveSunChunk} chunks in {liveSunClock.Elapsed.TotalSeconds:0.0} s."));
    }

    // The sun turned: the texels' cook for the old one is dropped (nothing of it kept but what landed) and both columns
    // start again for the new -- the coarse part from the live patch light as soon as it rests, the detail after.
    private void LiveRestart(Vector3 toSun)
    {
        var settingUp = sunNodeStage is SunNodeStage.Relighting or SunNodeStage.Prep or SunNodeStage.PatchPick or SunNodeStage.Discover or SunNodeStage.Merge;
        // A re-aim not yet tracing, the sun moved again (a slider dragged): re-aimed at the newest now. Parked as pending,
        // it waited for the setup to finish, and the setup for the old sun's bits, which no one shot any more: the sun
        // could not be changed (2026-10-19).
        if (settingUp && liveRetargeting)
        {
            liveRestartPending = null;
            LiveRetarget(toSun);
            return;
        }
        if (settingUp)
        {
            liveRestartPending = toSun;
            return;
        }
        liveRestartPending = null;
        if (liveCookWarm && sunNodeStage is SunNodeStage.Tracing or SunNodeStage.Off)
        {
            LiveRetarget(toSun);
            return;
        }
        if (splitCooking || sunNodeStage != SunNodeStage.Off)
        {
            device.WaitIdle();
            ResetSunNodeRefine();
            sunNodeRefineRays = 0;
            ReleaseSunNodeCook();
            sunNodeResults = Array.Empty<uint[]>();
            sunNodeDirsAsked = null;
            sunNodeReadback = false;
            sunNodeFirst = 0;
            sunNodePass = 0;
            liveReadbacks.Clear();
            splitCooking = false;
            sunNodeStage = SunNodeStage.Off;
        }
        StartSplitCook(new List<(Vector3? ToSun, string Path)> { (null, ""), (toSun, "") });
        splitCookPaths = new string?[] { null, null };
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] live texels: the sun turned to ({toSun.X:0.000},{toSun.Y:0.000},{toSun.Z:0.000}) -- the cook restarts; the coarse light once the patches rest."));
    }

    // --live-texels: the moment the live patch light rests, every live texel's coarse light from it -- the sky's part
    // and the sun's (SponzaLoop.SunProvisional.cs) -- with no wait on the cook's setup or its sweep. Its texels' patches
    // are the GI bundle's (the cook's pick, "texel-patch"); without them, each texel's nearest patch.
    private GpuBufferHandle liveTexelPatch;
    // liveTexelPatch came from the GI bundle's texel-patch stage (not a fill of -1): the sun nodes alias it unread.
    private bool liveTexelPatchStored;
    // Rested before the split light's buffers were up (the first boot): written as soon as they are.
    private bool liveProvisionalPending;

    private int provisionalCount;

    private void LiveProvisionalNow()
    {
        if (!liveTexels) return;
        liveProvisionalPending = !texelsUploaded || splitLiveBuffer.Equals(default(GpuBufferHandle)) || texelLiveIds is null;
        if (liveProvisionalPending) return;
        EnsureLiveTexelPatch();
        // The sky's part once gathered is kept (a sun change, SponzaLoop.SliceResolve.cs): its provisional would put the
        // patches' coarse sky over it (crevices' sky 0.8586 -> 0.865 after a sun change, 2026-10-19).
        if (!liveSkyGathered || regatherSky) ProvisionalPart(LiveSky, sunPart: false, LiveComponents, splitLiveBuffer, liveTexelPatch, livePatchLightBuffer);
        ProvisionalPart(LiveSun, sunPart: true, LiveComponents, splitLiveBuffer, liveTexelPatch, livePatchLightBuffer);
        provisionalCount++;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] live texels: provisional parts written (#{provisionalCount}, the sky's {(!liveSkyGathered || regatherSky ? "too" : "kept")}) at frame {framesRendered}, stage {sunNodeStage}."));
    }

    // The patches' first bounce of the sun from the texels' sun bits (texel_live_direct.comp).
    private ShaderInterface liveDirectInterface = null!;
    private PipelineHandle liveDirectPipeline;
    private PassHandle liveDirectPass;
    private GpuBufferHandle liveDirect;

    private void RecordLiveDirect(TransportSolveData d, Vector3 toSun, Vector3 irr)
    {
        EnsureLiveTexelPatch();
        DispatchCommand Pass(int mode, int threads, int first) => new(liveDirectPipeline, (threads + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uDirect", new Vector4Uniform(new Vector4(mode, threads, first, LiveComponents))),
                new("uToSun", new Vector4Uniform(new Vector4(toSun, LiveSun))),
                new("uIrr", new Vector4Uniform(new Vector4(irr, liveSunWords))),
                new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                new("uCookedDims", new Vector4Uniform(cookedDims)),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("LiveIds", splitLiveBuffer),
                new ShaderBufferBinding("TexelPatch", liveTexelPatch),
                new ShaderBufferBinding("SunNodeBits", liveSunBits),
                new ShaderBufferBinding("PatchDirect", liveDirect),
                new ShaderBufferBinding("CouplingStart", solveBuffers[5]),
                new ShaderBufferBinding("Couplings", solveBuffers[6]),
                new ShaderBufferBinding("PatchTerms", solveBuffers[7]),
                new ShaderBufferBinding("NodeFirst", liveFirst),
            }.Concat(CookedBuffers()).ToArray());
        graph.Dispatch(liveDirectPass, Pass(0, d.Patches, 0));
        var live = texelLiveIds!.Length;
        const int chunk = 65535 * 64;
        for (var first = 0; first < live; first += chunk)
            graph.Dispatch(liveDirectPass, Pass(1, Math.Min(chunk, live - first), first));
        graph.Dispatch(liveDirectPass, Pass(2, d.Patches, 0));
    }

    private void EnsureLiveTexelPatch()
    {
        if (!liveTexelPatch.Equals(default(GpuBufferHandle))) return;
        var picked = lightBakeKey is { } key ? LoadStage(key, "texel-patch", r => r.ReadString() != TexelPatchHeader ? null : ReadArray<int>(r)) : null;
        liveTexelPatchStored = picked is not null;
        if (picked is null) { picked = new int[texelCount]; Array.Fill(picked, -1); }
        liveTexelPatch = Own(device.CreateGpuBuffer(texelCount * 4, MemoryMarshal.AsBytes(picked.AsSpan()), "sponza.live.texel-patch"));
    }

    // A live cook that reached its trace keeps its setup -- the texels' hash, their patches, the sun points, the buffers:
    // the geometry's -- and a sun that turns after re-aims it: the accumulators cleared, the sun's direction, the sun
    // points' bits (off the frame thread) and the texels' (in the frame, by the live solve), the trace order with what is
    // on screen first. The setup took ~20 s a turn (2026-10-16).
    private bool liveCookWarm, liveRetargeting;

    private void LiveRetarget(Vector3 toSun)
    {
        device.WaitIdle();
        RestoreGatherScratch();
        ResetSunNodeRefine();
        sunNodeRefineRays = 0;
        sunNodeFirst = 0;
        sunNodePass = 0;
        sunNodeReadback = false;
        liveReadbacks.Clear();   // the old sun's slices: dropped, not landed
        farRelit = false;        // the far record relit for the new sun before the sweep (FarRelightNow)
        patchDirectDone = false;
        sunNodeDirsAsked = new[] { Vector3.UnitY, toSun };
        sunNodeDirs = new[] { new Vector4(Vector3.UnitY, 2f), new Vector4(toSun, 1f) };
        // The sky's part is the sky's and the geometry's, not the sun's: kept, once gathered (--regather-sky: both again).
        skyKept = liveSkyGathered && !regatherSky;
        RetireGpuBuffer(sunNodeSunsBuffer);
        sunNodeSunsBuffer = Own(device.CreateGpuBuffer(sunNodeDirs.Length * 16, MemoryMarshal.AsBytes(sunNodeDirs.AsSpan()), "sponza.sun-nodes.suns"));
        var directions = new[] { new Vector4(Vector3.UnitY, 0f), new Vector4(toSun, 0f) };
        sunNodeBitDirections = directions;
        if (sunNodeResults.Length != 2) sunNodeResults = new[] { new uint[texelCount * 2], new uint[texelCount * 2] };
        else foreach (var r in sunNodeResults) Array.Clear(r);
        BeginLivePointBits();
        splitCooking = true;
        BeginSplitCookLanding();
        liveRetargeting = true;
        sunNodeClock.Restart();
        sunNodeStage = SunNodeStage.Relighting;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] live texels: the sun turned to ({toSun.X:0.000},{toSun.Y:0.000},{toSun.Z:0.000}) -- the cook re-aimed (its setup kept)."));
    }

    private readonly System.Diagnostics.Stopwatch retargetWaitClock = System.Diagnostics.Stopwatch.StartNew();

    private void FinishLiveRetarget()
    {
        var toSun = sunNodeDirsAsked![1];
        RecordLivePointBits();
        if (!liveSunDone || liveSunToward != toSun || !LivePatchLightRested || !LivePointsDone || SplitVisiblePending)
        {
            // What it waits on, every few seconds: a re-aim that never traces is a sun the user cannot change.
            if (retargetWaitClock.Elapsed.TotalSeconds > 5)
            {
                retargetWaitClock.Restart();
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] live texels: the re-aim waits -- sun bits done {liveSunDone}, toward the new sun {liveSunToward == toSun}, patch light rested {LivePatchLightRested}, points done {LivePointsDone}, view pending {SplitVisiblePending}."));
            }
            return;
        }
        var live = texelLiveIds!;
        var oldTrace = sunNodeTraceBuffer;
        sunNodeTraceIds = LiveCoarseOrder();
        sunNodeTraceBuffer = ReferenceEquals(sunNodeTraceIds, live) ? sunNodeLive
            : Own(device.CreateGpuBuffer(Math.Max(16, sunNodeTraceIds.Length * 4), MemoryMarshal.AsBytes(sunNodeTraceIds.AsSpan()), "sponza.sun-nodes.traced"));
        if (!oldTrace.Equals(sunNodeLive) && !oldTrace.Equals(default(GpuBufferHandle))) RetireGpuBuffer(oldTrace);
        RetireGpuBuffer(sunNodeStats);
        sunNodeStats = Own(device.CreateGpuBuffer(48, new byte[48], "sponza.sun-nodes.stats"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] live texels: re-aimed in {sunNodeClock.Elapsed.TotalSeconds:0.0} s; tracing."));
        LogGatherMemory("the gather begins");
        sunNodeClock.Restart();
        ResetSunNodeCounters();
        liveRetargeting = false;
        // The guides and light lists are recorded in the frame the live patch light rests (RecordLiveGuides): done before the
        // gather (outside the frame) reads them.
        device.WaitIdle();
        sunNodeStage = SunNodeStage.Tracing;
    }

    // The patches' guides from the live light at rest (texel_live_guides.comp): the gather's rays go where light is.
    private ShaderInterface liveGuidesInterface = null!;
    private PipelineHandle liveGuidesPipeline;
    private PassHandle liveGuidesPass;
    private GpuBufferHandle liveGuides, liveLights, liveSkyEscapeBuffer, liveSkyLumBuffer;
    // The sky's part of the guides (SponzaLoop.Transport.cs folds them): per patch its escaped share a guide bin, and the
    // sky's luminance a guide bin.
    private float[]? liveSkyEscape, liveSkyLum;

    private void RecordLiveGuides(TransportSolveData d)
    {
        if (liveGuides.Equals(default(GpuBufferHandle)))
        {
            liveGuides = Own(device.CreateGpuBuffer(checked(d.Patches * 64 * 4), name: "sponza.live.guides"));
            liveLights = Own(device.CreateGpuBuffer(checked(d.Patches * LightListSize * 8), name: "sponza.live.light-lists"));
            var escape = liveSkyEscape is { } e && e.Length == d.Patches * 64 ? e : new float[d.Patches * 64];
            liveSkyEscapeBuffer = Own(device.CreateGpuBuffer(escape.Length * 4, MemoryMarshal.AsBytes(escape.AsSpan()), "sponza.live.sky-escape"));
            liveSkyEscape = null;   // up: the CPU's copy goes
        }
        // The sky's luminance a bin: its table each time (the sky may change; 64 floats).
        var lum = liveSkyLum ?? new float[64];
        if (liveSkyLumBuffer.Equals(default(GpuBufferHandle)))
            liveSkyLumBuffer = Own(device.CreateGpuBuffer(64 * 4, MemoryMarshal.AsBytes(lum.AsSpan()), "sponza.live.sky-lum"));
        graph.Dispatch(liveGuidesPass, new DispatchCommand(liveGuidesPipeline, (d.Patches + 63) / 64, 1, 1,
            new ShaderUniform[] { new("uGuides", new Vector4Uniform(new Vector4(d.Patches, LiveComponents, 0f, 0f))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("CookedPatches", cookedPatchBuffer),
                new ShaderBufferBinding("CouplingStart", solveBuffers[5]),
                new ShaderBufferBinding("Couplings", solveBuffers[6]),
                new ShaderBufferBinding("NodeOutgoing", liveOut),
                new ShaderBufferBinding("PatchGuides", liveGuides),
                new ShaderBufferBinding("PatchLights", liveLights),
                new ShaderBufferBinding("PatchSkyEscape", liveSkyEscapeBuffer),
                new ShaderBufferBinding("SkyGuideLum", liveSkyLumBuffer),
            }));
    }

    // --live-texels gathers the coarse levels of the whole scene at every light change, never by view: a camera move
    // sets off no cook (the user, 2026-10-16: "doing computation that's potentially heavy or cook-ish between camera
    // moves is a super bad idea"). The GI's budget follows the level: the hashed cells of liveMinLevel and coarser
    // (Sponza: 0.77 M at 20 cm, 2.45 M at 10 cm), at --live-rays each, the coarsest first; pixels read no finer
    // (TexelLevelsUniform). The finer levels -- the atlas's 12.7 M texels and the 5 cm cells -- keep the patches'
    // light until their detail comes from the offline cook. Gathering only what was on screen made every camera
    // move a cook of its own, and texels the jitter only sometimes landed on a trickle of them with the camera still.
    private int liveMinLevel = 1, liveRays = 128;
    private uint[]? liveCoarseIds, liveCoarseOrdered;

    // The coarse texels in trace order: each level coarsest first, in place order within it (the far field pools
    // neighbours), ordered once -- the geometry's.
    private uint[] LiveCoarseOrder()
    {
        if (liveCoarseOrdered is not null) return liveCoarseOrdered;
        // --live-transfer: nothing traced -- every texel is lit from its cooked entries (SponzaLoop.Transfer.cs).
        // The hybrid: the texels the transfer flagged (SponzaLoop.Transfer.cs), in place order.
        if (TransferOn && !transferCheck)
        {
            if (!transferReady) return Array.Empty<uint>();   // not cooked yet: asked again at the trace (UploadSunNodes is before it)
            var flagged = transferGatherIds;
            return liveCoarseOrdered = farField && flagged.Length > 0 ? FarSpatialOrder(flagged, flagged.Length) : flagged;
        }
        if (TransferOn) return liveCoarseOrdered = texelLiveIds ?? Array.Empty<uint>();   // the check: the gather over all
        var ids = liveCoarseIds ?? Array.Empty<uint>();
        if (!farField || ids.Length == 0) return liveCoarseOrdered = ids;
        var texels = liveCoarseLevels!;
        liveCoarseOrdered = ids.GroupBy(id => texels[id]).OrderByDescending(g => g.Key)
            .SelectMany(g => { var group = g.ToArray(); return FarSpatialOrder(group, group.Length); }).ToArray();
        return liveCoarseOrdered;
    }

    // Each texel's level (its record's normal w), for the coarse lists' grouping.
    private byte[]? liveCoarseLevels;

    // Each frame, beside the texels' work.
    private long liveRecordedFrame = -1;

    private void RecordLivePatchLight()
    {
        if (liveRetargeting && retargetWaitClock.Elapsed.TotalSeconds > 4.5)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] live patch light while re-aiming: on {livePatchLight}, solve {transportSolve is not null}, hardware {FrameRaysOnHardware}, tracer {frameTracer is not null}, recorded this frame {liveRecordedFrame == framesRendered}, inputs {(transportSolve is { } dd && EnsureLiveInputs(dd))}, texels up {texelsUploaded}, injected {liveInjected}, toward {liveToSun}."));
        if (!livePatchLight || transportSolve is not { } d || !FrameRaysOnHardware || frameTracer is null) return;
        // Recorded from the texels' frame or the cook screen's, once a frame either way.
        if (liveRecordedFrame == framesRendered) return;
        liveRecordedFrame = framesRendered;
        if (liveProvisionalPending) LiveProvisionalNow();
        if (liveRestartPending is { } pending && sunNodeStage is not (SunNodeStage.Relighting or SunNodeStage.Prep or SunNodeStage.PatchPick or SunNodeStage.Discover or SunNodeStage.Merge))
            LiveRestart(pending);
        if (!EnsureLiveInputs(d)) return;
        var toSun = -Vector3.Normalize(sunDirection);
        var irr = EffectiveSunIrradiance;
        var noSun = toSun.Y <= 0f || irr == Vector3.Zero;
        var changed = !liveInjected || toSun != liveToSun || irr != liveIrr;
        if (changed && liveTexels)
        {
            if (!texelsUploaded || texelLiveIds is null || splitLiveBuffer.Equals(default(GpuBufferHandle))) return;
            // The sun's first bounce from the texels' own sun bits (texel_live_direct.comp): the bits for this sun first
            // (texel_live_sun.comp, a chunk a frame), then the patches' sums and their first bounce -- no sunlets.
            if (!noSun && !(liveSunDone && liveSunToward == toSun))
            {
                if (liveSunToward != toSun || liveSunBits.Equals(default(GpuBufferHandle))) BeginLiveSunBits(toSun, LiveSun, 1);
                RecordLiveSunBits();
                return;
            }
            if (!noSun) RecordLiveDirect(d, toSun, irr);
            else graph.Dispatch(liveSolveBPass, LiveDispatch(1, toSun, d.Patches, true, LiveSun));
            if (!liveInjected)
            {
                graph.Dispatch(liveSolveBPass, LiveDispatch(1, Vector3.Zero, d.Patches, true, LiveSky));
                graph.Dispatch(liveSolveBPass, LiveDispatch(2, toSun, d.Patches * LiveComponents, noSun));
            }
            liveToSun = toSun;
            liveIrr = irr;
            liveInjected = true;
            liveBounces = 0;
            liveFramesActive = 0;
            liveClock.Restart();
            return;
        }
        if (changed)
        {
            if (!noSun)
            {
                graph.Dispatch(liveSolveAPass, LiveDispatch(0, toSun, solveSunlets, false, LiveSun));
                RecordShadowBatch(liveSunletBatch, liveOrigins, liveOccluded, solveSunlets, toSun, 5u);
            }
            graph.Dispatch(liveSolveBPass, LiveDispatch(1, toSun, d.Patches, noSun, LiveSun));
            if (!liveInjected)
            {
                graph.Dispatch(liveSolveBPass, LiveDispatch(1, Vector3.Zero, d.Patches, true, LiveSky));
                graph.Dispatch(liveSolveBPass, LiveDispatch(2, toSun, d.Patches * LiveComponents, noSun));
            }
            liveToSun = toSun;
            liveIrr = irr;
            liveInjected = true;
            liveBounces = 0;
            liveFramesActive = 0;
            liveClock.Restart();
            // The injection alone this frame (its shadow batch is the frame's heaviest work); the bounces from the next.
            return;
        }
        // --live-patch-always: never rest (a measurement of what a frame pays while the light moves).
        if (liveAlways && liveBounces >= LiveBouncesToRest) liveBounces = LiveBouncesToRest - LiveBouncesPerFrame;
        else if (liveBounces >= LiveBouncesToRest) return;
        for (var b = 0; b < LiveBouncesPerFrame; b++)
        {
            graph.Dispatch(liveSolveBPass, LiveDispatch(3, toSun, d.Patches * LiveComponents, noSun));
            graph.Dispatch(liveSolveBPass, LiveDispatch(4, toSun, d.Patches * LiveComponents, noSun));
        }
        graph.Dispatch(liveSolveBPass, LiveDispatch(5, toSun, d.Patches * LiveComponents, noSun));
        liveBounces += LiveBouncesPerFrame;
        liveFramesActive++;
        // The coarse light at LiveCoarseBounces (the bounces past it add a few percent), and again at rest.
        if (liveBounces == LiveCoarseBounces || liveBounces >= LiveBouncesToRest) LiveProvisionalNow();
        if (liveBounces >= LiveBouncesToRest && liveTexels) RecordLiveGuides(d);
        if (liveBounces >= LiveBouncesToRest)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] live patch light: at rest after {liveBounces} bounces over {liveFramesActive} frames ({liveClock.Elapsed.TotalSeconds:0.0} s) for the sun toward ({toSun.X:0.000},{toSun.Y:0.000},{toSun.Z:0.000})."));
            if (!liveChecked) CheckLiveAgainstBoot(d);
        }
    }

    // --live-texels never relights on the CPU: once the solve's inputs are on the GPU, the transport cook's CPU side goes
    // -- its relight closures hold the whole cook's scope (the couplings, the sunlets and their couplings, the sky bins:
    // gigabytes), and on 16 GB of memory shared with the GPU the boot paged until a frame passed the GPU's watchdog
    // (2026-10-16: the device lost at the live solve's first frame, 11 GB of GPU allocations beside a 7.7 GB heap).
    private void ReleaseCpuTransport()
    {
        transportRelight = null;
        transportRelightMany = null;
        transportBuildGuides = null;
        transportBuildLights = null;
        // The far lists from the GI bundle alone (no CPU scope behind them): the far field relights from them per light.
        var couplingsKey = transportCouplingsKey;
        transportBuildFarLists = couplingsKey is not null && File.Exists(TransportStagePath(couplingsKey, "far-lists"))
            ? reach => LoadStage(couplingsKey, "far-lists", r => r.ReadSingle() != reach ? null
                    : new FarLists(ReadArray<uint>(r), ReadArray<uint>(r), ReadArray<float>(r))) is { } l
                ? (l.Start, l.Ids, l.Weights) : throw new InvalidOperationException("the GI bundle's far lists are for another reach")
            : null;
        // BootIncident's closure is the cook's whole scope (a lambda holds its scope's every captured local -- the
        // couplings, the sky bins: 1.9 GB, 2026-10-19); its answer alone stays.
        if (transportSolve is { } d)
        {
            var boot = d.BootIncident();
            transportSolve = d with
            {
                BootIncident = () => boot,
                Couplings = Array.Empty<(int, float)[]>(), SunletCouplings = Array.Empty<(int, float)[]>(),
                SunletPos = new(), SunletNrm = new(), SunletAlbedo = new(),
            };
        }
        var before = GC.GetTotalMemory(false);
        ReleaseHeap();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] live texels: the transport cook's CPU side released (the managed heap {before / 1048576.0:N0} -> {GC.GetTotalMemory(false) / 1048576.0:N0} MB)."));
        LogMemoryInventory("the transport cook's CPU side released");
    }

    // --live-texels: once the transport cook has landed (everything it traced on the CPU is cooked or in the GI bundle),
    // the CPU's ray scene goes too -- a gigabyte and more of BVH and triangles that only the CPU-side diagnostics read
    // (the texel inspector's probes, the ray checks, the surface checks). --keep-cpu-scene keeps it.
    private bool keepCpuScene;

    private void DropCpuScene()
    {
        if (!liveTexels || keepCpuScene || rayQueries is null) return;
        // Whatever reads the CPU's scene keeps it: the references the gate holds the light against (2026-10-16: dropped,
        // the GPU reference stalled and the gate read garbage), the inspector, the checks, the watch, the leak census.
        if (gpuReference || probeReference || texelInspect is not null || rayLeakCensus || watch || rayCheck || rayView || surfaceCheck || spotWhy) return;
        rayQueries = null;
        lock (contactGate)
        {
            contactTable = null;
            contactScene = null;
            cookContactTracer = null;
            cookContactScene = null;
        }
        var before = GC.GetTotalMemory(false);
        ReleaseHeap();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] live texels: the CPU's ray scene dropped (the managed heap {before / 1048576.0:N0} -> {GC.GetTotalMemory(false) / 1048576.0:N0} MB; --keep-cpu-scene keeps it)."));
    }

    // --live-texels, once the light has landed: the ray data's geometry goes (its BVH, triangles, positions, texture
    // coordinates and the CPU's hierarchies, 1.8 GB on Sponza, 2026-10-19) -- the GPU holds its copies, and the Metal
    // tracer, the hit cells and the foliage's sun points are built. The rows' tables stay (the surface words, the
    // contacts' row map). The same readers as DropCpuScene keep it; --keep-cpu-scene keeps it.
    private void ReleaseRayGeometry()
    {
        if (!liveTexels || keepCpuScene || rayGpuData is not { GeometryReleased: false } data) return;
        if (CpuSceneReaders) return;
        if (rtShadows && rtBuild is null || hitCellsOn && hitCellsBuffer.Equals(default(GpuBufferHandle)) || rayTraversalDropped && frameTracer is null) return;
        data.ReleaseGeometry();
        var before = GC.GetTotalMemory(false);
        ReleaseHeap(compact: true);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] live texels: the ray data's geometry released (the managed heap {before / 1048576.0:N0} -> {GC.GetTotalMemory(false) / 1048576.0:N0} MB; --keep-cpu-scene keeps it)."));
    }

    // Whatever reads the CPU's scene or a cook's scratch after the light lands keeps them (DropCpuScene's readers).
    private bool CpuSceneReaders => gpuReference || probeReference || texelInspect is not null || rayLeakCensus || watch || rayCheck || rayView || surfaceCheck || spotWhy;

    // --live-texels, once the light has landed: the GPU buffers only a gather or the setup writes go (2026-10-20: Metal
    // stood at 11 of 12.1 GB recommended on a 16 GB machine, swapping) -- the setup's (the discovery's list, the surface
    // bake's rows: never again), and the gather's scratch, made again when a light change re-aims (RestoreGatherScratch)
    // or on demand (the resolve's lists, BeginGpuResolve; the fine pass's flags, ApplyFineNow).
    private void ReleaseGatherScratch()
    {
        if (!liveTexels || keepCpuScene || CpuSceneReaders || gatherScratchReleased) return;
        long freed = 0;
        var sizes = device.SnapshotResources().GpuBuffers.ToDictionary(x => x.Handle, x => x.SizeBytes);
        void Retire(ref GpuBufferHandle b)
        {
            if (b.Equals(default(GpuBufferHandle))) return;
            freed += sizes.GetValueOrDefault(b);
            RetireGpuBuffer(b);
            b = default;
        }
        // The setup's, never read again: placeholders where a binding still names them.
        Retire(ref sunNodeDiscoverList);
        sunNodeDiscoverList = Own(device.CreateGpuBuffer(48, new byte[48], "sponza.sun-nodes.discover.none"));
        for (var i = 0; i < raySurfaceBakeBuffers.Length; i++)
            if (raySurfaceBakeBuffers[i].Name == "BakeRows" && raySurfacesChecked)
            {
                var rows = raySurfaceBakeBuffers[i].Buffer;
                Retire(ref rows);
                raySurfaceBakeBuffers[i] = new ShaderBufferBinding("BakeRows", Own(device.CreateGpuBuffer(16, new byte[16], "sponza.ray.bake-rows.none")));
            }
        // The gather's.
        Retire(ref resolveState);
        Retire(ref resolvePasses);
        for (var i = 0; i < 2; i++) { Retire(ref resolveIds[i]); Retire(ref resolvePlaces[i]); }
        resolveCapacity = 0;
        Retire(ref fineFlagsBuffer);
        Retire(ref sunNodeRaysBuffer);
        Retire(ref sunNodeHits);
        gatherScratchReleased = true;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] live texels: the setup's and the gather's GPU scratch released ({freed / 1048576.0:N0} MB)."));
    }

    private bool gatherScratchReleased;

    // A light change re-aims: the gather's scratch again (the setup's sizes, SponzaLoop.SunNodes.cs).
    private void RestoreGatherScratch()
    {
        if (!gatherScratchReleased) return;
        var rays = sunNodeSlice * 32;
        sunNodeRaysBuffer = Own(device.CreateGpuBuffer(checked((int)((long)rays * 32)), name: "sponza.sun-nodes.rays"));
        sunNodeHits = Own(device.CreateGpuBuffer(checked((int)((long)rays * 48 + 80)), name: "sponza.sun-nodes.hits"));
        gatherScratchReleased = false;
    }

    // The shared inputs (the band's solve packs them; this starts it when no band has) and the live state.
    private bool EnsureLiveInputs(TransportSolveData d)
    {
        if (solveBuffers.Length == 0)
        {
            if (solvePack is null) StartSolvePack(withSunlets: !liveTexels);
            if (solvePack is not { IsCompleted: true } || solveActive) return false;   // a running band takes its own pack up
            UploadGpuSolve(solvePack.Result);
            solvePack = null;
            // Not in the frame that uploads 2 GB: the injection's 9.5 M sunlet rays wait a frame (the GPU's watchdog).
            return false;
        }
        if (!liveIn.Equals(default(GpuBufferHandle))) return true;
        if (liveTexels) ReleaseCpuTransport();
        var bytes = checked(d.Patches * LiveComponents * 16);
        liveFirst = Own(device.CreateGpuBuffer(bytes, new byte[bytes], "sponza.live.first"));
        liveIn = Own(device.CreateGpuBuffer(bytes, new byte[bytes], "sponza.live.in"));
        liveOut = Own(device.CreateGpuBuffer(bytes, new byte[bytes], "sponza.live.out"));
        liveOrigins = Own(device.CreateGpuBuffer(Math.Max(16, solveSunlets * 16), name: "sponza.live.sunlet-origins"));
        liveOccluded = Own(device.CreateGpuBuffer(Math.Max(16, solveSunlets * 4), name: "sponza.live.sunlet-occluded"));
        if (liveTexels) liveDirect = Own(device.CreateGpuBuffer(checked(d.Patches * 8), new byte[d.Patches * 8], "sponza.live.patch-direct"));
        liveSkyWeights = Own(device.CreateGpuBuffer(16, MemoryMarshal.AsBytes(new[] { 1f, 0f, 0f, 0f }.AsSpan()), "sponza.live.sky-weights"));
        var lightBytes = checked(d.Patches * LiveComponents * 8);
        livePatchLightBuffer = Own(device.CreateGpuBuffer(lightBytes, new byte[lightBytes], "sponza.live.patch-light"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] live patch light: {d.Patches:N0} patches, {solveCouplings:N0} couplings, {solveSunlets:N0} sunlets; {LiveBouncesPerFrame} bounce a frame while the light settles, {LiveBouncesToRest} to rest."));
        return true;
    }

    private DispatchCommand LiveDispatch(int mode, Vector3 toSun, int threads, bool noSun, int component = 0)
    {
        var groups = (threads + 63) / 64;
        var x = Math.Min(groups, 65535);
        var y = (groups + x - 1) / x;
        return new DispatchCommand(sunNodeSolvePipeline, x, y, 1,
            new ShaderUniform[]
            {
                new("uSolve", new Vector4Uniform(new Vector4(mode, transportSolve!.Patches, LiveComponents, component))),
                new("uSun", new Vector4Uniform(new Vector4(toSun, noSun ? 1f : 0f))),
                new("uIrr", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
                new("uCount", new Vector4Uniform(new Vector4(solveSunlets, 0f, LiveComponents, 1f / LiveComponents))),
                new("uLive", new Vector4Uniform(new Vector4(1f, 0f, 0f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("SunletPos", solveBuffers[0]),
                new ShaderBufferBinding("SunletNrm", solveBuffers[1]),
                new ShaderBufferBinding("SunletAlbedo", solveBuffers[2]),
                new ShaderBufferBinding("SunletOrigins", liveOrigins),
                new ShaderBufferBinding("SunletOccluded", liveOccluded),
                new ShaderBufferBinding("SunletCouplingStart", solveBuffers[3]),
                new ShaderBufferBinding("SunletCouplings", solveBuffers[4]),
                new ShaderBufferBinding("CouplingStart", solveBuffers[5]),
                new ShaderBufferBinding("Couplings", solveBuffers[6]),
                new ShaderBufferBinding("PatchTerms", solveBuffers[7]),
                new ShaderBufferBinding("NodeFirst", liveFirst),
                new ShaderBufferBinding("NodeIncoming", liveIn),
                new ShaderBufferBinding("NodeOutgoing", liveOut),
                new ShaderBufferBinding("SunNodePatchLight", livePatchLightBuffer),
                new ShaderBufferBinding("PatchAverage", solveAvgPatch),
                new ShaderBufferBinding("SunletAverage", solveAvgSunlet),
                new ShaderBufferBinding("SolveSkyWeights", liveSkyWeights),
                new ShaderBufferBinding("PatchTransmit", solveTransmit),
            });
    }

    // Once, at the first rest: the live answer against the CPU's boot solve (the same couplings, sunlets and sky; its
    // sun the boot's), patch by patch -- what stage 2 must reproduce before the texels read it.
    private void CheckLiveAgainstBoot(TransportSolveData d)
    {
        liveChecked = true;
        device.WaitIdle();
        var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(livePatchLightBuffer, 0, d.Patches * LiveComponents * 8).AsSpan()).ToArray();
        var boot = d.BootIncident();
        static double Lum(Vector3 v) => 0.2126 * v.X + 0.7152 * v.Y + 0.0722 * v.Z;
        double sumLive = 0, sumBoot = 0;
        var rel = new List<double>(d.Patches);
        for (var p = 0; p < d.Patches; p++)
        {
            var live = Vector3.Zero;
            for (var k = 0; k < LiveComponents; k++)
            {
                uint lo = words[2 * (p * LiveComponents + k)], hi = words[2 * (p * LiveComponents + k) + 1];
                live += new Vector3((float)BitConverter.UInt16BitsToHalf((ushort)(lo & 0xFFFF)), (float)BitConverter.UInt16BitsToHalf((ushort)(lo >> 16)),
                    (float)BitConverter.UInt16BitsToHalf((ushort)(hi & 0xFFFF)));
            }
            double l = Lum(live), b = Lum(boot[p]);
            sumLive += l; sumBoot += b;
            if (b > 1e-4) rel.Add(Math.Abs(l - b) / b);
        }
        rel.Sort();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] live patch light against the CPU's boot solve ({d.Patches:N0} patches; the boot sun {(authoredSunDirection is { } boot0 && liveToSun == -Vector3.Normalize(boot0) ? "is" : "may not be")} this one): energy {sumLive / Math.Max(sumBoot, 1e-9):0.000}, |diff|/boot median {(rel.Count > 0 ? rel[rel.Count / 2] : double.NaN):0.000} p90 {(rel.Count > 0 ? rel[(int)(rel.Count * 0.9)] : double.NaN):0.000}."));
    }
}
