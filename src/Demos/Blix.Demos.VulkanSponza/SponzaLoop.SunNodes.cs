using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --sun-nodes FILE: the texels' light for many suns from ONE trace (plan.md, potential suns). A light bake traces every
// texel's gather afresh for each sun (Sponza: 20 minutes, 18 G rays), but where a gather ray goes and what it meets do
// not depend on the sun -- only the light at the hit does. So each slice of live texels is traced once, RAYS a pass,
// and every hit is shaded for every sun (texel_nodes_shade.comp): its direct sun from the sun bits of the texels it
// lies among (the cook's hash, texel_nodes_prep.comp; the bits a shadow ray from each live texel toward each sun, on
// Metal: SunNodeBitsFor), its bounce from its nearest patch's light re-solved
// for that sun (transportRelight, per sun, before the trace), a miss the sky.
//
// FILE: one sun a line, "hour dec" in degrees at the band's latitude (as --sun-sweep). Each sun's light is written
// resolved (SponzaLoop.TexelRest.cs's format) to --sun-nodes-out DIR (default FILE's directory) as sun-NNN.resolved,
// and a manifest line (FILE.nodes.manifest) as --sun-sweep writes it, so the two compare file for file. Only the first
// --sun-nodes-keep suns are kept (default all: Cornell; on Sponza every sun's light is 123 MB). The run closes after.
//   --sun-nodes-rays N   rays a texel (default 512, in passes of 32)
//   --sun-nodes-slice N  texels traced together (default 32,768: N x 32 rays a frame)
internal sealed partial class SponzaLoop
{
    private (float Hour, float Dec)[]? sunNodeSuns;
    private string sunNodeManifest = "";
    private string sunNodeDir = "";
    private int sunNodeRays = 512, sunNodeSlice = 32768, sunNodeKeep = int.MaxValue;
    private int sunNodeStride = 1;
    // How far off its surface a sun bit's ray starts: the gather's own offset for its hits' shadow rays (texel_gather.comp,
    // hitPos + hn * 0.01); the lightlets use 2 mm (Sponza's authored sun: the same light either way). --sun-nodes-lift M.
    private float sunNodeLift = 0.01f;
    // Which texels' sun bits a hit reads (texel_nodes.glsl, nodeSetAt): those whose plane lies within sunNodePlane cells
    // of it, weighed by distance with a spread of sunNodeSigma cells. A pixel's lookup takes a whole cell (texel.glsl's
    // texelsAround); here that let a hit borrow the sun of texels on the next surface over -- Sponza's authored sun, every
    // 16th texel, against a shadow ray at every hit: 1.47% off at a cell, 0.72% at a quarter (the mid-lit texels' bias
    // +2.2% -> +0.5%); the spread hardly mattered (0.6 -> 0.35: 0.72% -> 0.70%).
    private float sunNodePlane = 0.25f, sunNodeSigma = 0.6f;
    // --sun-nodes-cpu-solve: the patches' light by the CPU's batched relight, not the GPU's (the check between them).
    private bool sunNodeCpuSolve;
    private float sunNodeUnknownSun;
    // The texels traced (every live one but under --sun-nodes-stride), and their ids on the GPU.
    private uint[] sunNodeTraceIds = Array.Empty<uint>();
    private GpuBufferHandle sunNodeTraceBuffer;
    private ShaderInterface sunNodePrepInterface = null!, sunNodeRaysInterface = null!, sunNodeShadeInterface = null!;
    private PassHandle sunNodePrepPass, sunNodeRaysPass, sunNodeShadePass;
    private PipelineHandle sunNodePrepPipeline, sunNodeRaysPipeline, sunNodeShadePipeline;
    private RayBatchPasses sunNodeClosestBatch;
    // --sun-nodes-exact-sun (one sun): the hits' sun by a shadow ray each, as the bake's gather has it -- the check on
    // the sun bits' interpolation.
    private bool sunNodeExact;
    // The band's guides steer half of each texel's rays (texel_nodes_rays.comp); --sun-nodes-no-guide: cosine rays only.
    private bool sunNodeGuide = true;
    private bool sunNodeGuided;
    private GpuBufferHandle sunNodeGuideBuffer, sunNodeRayWeights;
    private PassHandle sunNodeExactOriginsPass;
    private RayBatchPasses sunNodeExactBatch;
    private GpuBufferHandle sunNodeExactOrigins, sunNodeExactOccluded;

    private enum SunNodeStage { Off, Waiting, Relighting, Prep, Discover, Merge, Tracing, Done }
    private SunNodeStage sunNodeStage;
    private Task<uint[]>? sunNodeRelight;
    private Task<(uint[] Texel, Vector4[] Points, uint[] PointBits)>? sunNodeBitsTask;
    // The sun points (SunNodesFoliage.cs: the foliage's, then the discovery's), kept to be added to; what their rays
    // and the texels' went out with.
    private Vector4[] sunNodePoints = Array.Empty<Vector4>();
    private uint[] sunNodePointBits = Array.Empty<uint>();
    private Vector4[] sunNodeBitDirections = Array.Empty<Vector4>();
    private float sunNodeReach;
    // Discovery: every --sun-nodes-discover-stride th live texel traced one pass first (0: none).
    private int sunNodeDiscoverStride = 8;
    private const int DiscoverRoom = 1 << 22;
    private uint[] sunNodeDiscoverIds = Array.Empty<uint>();
    private GpuBufferHandle sunNodeDiscoverBuffer, sunNodeDiscoverList;
    private int sunNodeDiscoverFirst;
    private Task<(Vector4[] Added, uint[] Bits)>? sunNodeDiscoverTask;
    private GpuBufferHandle sunNodeFoliagePoints, sunNodeFoliageHash, sunNodeFoliageBits;
    private int sunNodeFoliageSlots;
    private Vector4[] sunNodeDirs = Array.Empty<Vector4>();
    private GpuBufferHandle sunNodeLive, sunNodeSunsBuffer, sunNodeBits, sunNodeHash, sunNodePatchLight, sunNodeRaysBuffer,
        sunNodeHits, sunNodeAccum, sunNodeStats;
    private int sunNodeHashSlots, sunNodeFirst, sunNodePass;
    private bool sunNodeReadback;
    private uint[][] sunNodeResults = Array.Empty<uint[]>();
    private readonly Stopwatch sunNodeClock = new();
    // The boot sun, when the cook answers it in place of a light bake (FoldBootSunIntoCook): the last of the cook's suns,
    // its light kept whole and saved where the bake would have saved it.
    private string? sunNodeBootPath;
    private uint[]? sunNodeBootResult;
    private int SunNodeCompressed => sunNodeSuns!.Length - (sunNodeBootPath is null ? 0 : 1);
    private double sunNodeRelightSeconds, sunNodeReadSeconds;

    private void ReadSunNodeArgs(AppArgs args)
    {
        // The cook's settings, read for both the study (--sun-nodes) and the band's grid (--potential-suns).
        if (args.Int("sun-nodes-rays") is { } r) sunNodeRays = Math.Max(32, r / 32 * 32);
        if (args.Int("sun-nodes-slice") is { } s) sunNodeSlice = Math.Clamp(s, 1024, 65535);
        if (args.Int("sun-nodes-discover-stride") is { } ds) sunNodeDiscoverStride = Math.Max(0, ds);
        if (args.Float("sun-nodes-lift") is { } lift) sunNodeLift = Math.Max(0f, lift);
        if (args.Float("sun-nodes-unknown-sun") is { } us) sunNodeUnknownSun = Math.Clamp(us, 0f, 1f);
        sunNodeCpuSolve = args.Flag("sun-nodes-cpu-solve");
        sunNodeGuide = !args.Flag("sun-nodes-no-guide");
        if (args.Int("sun-nodes-bounces") is { } nb) sunNodeBouncesAsked = Math.Max(0, nb);
        if (args.Float("sun-nodes-plane") is { } pl) sunNodePlane = Math.Max(0.01f, pl);
        if (args.Float("sun-nodes-sigma") is { } sg) sunNodeSigma = Math.Max(0.05f, sg);
        // Study only: every S-th live texel traced (the sun bits and the hash still hold them all), and one sun's hits by
        // a shadow ray each. A grid kept for the run needs every texel, and every sun.
        var stride = args.Int("sun-nodes-stride");
        var exact = args.Flag("sun-nodes-exact-sun");
        if (args.Flag("potential-suns"))
        {
            if (stride is > 1 || exact)
                throw new AppArgsException("--potential-suns cooks every texel at every node: --sun-nodes-stride and --sun-nodes-exact-sun are for --sun-nodes studies.");
            // The band's grid (SponzaLoop.PotentialSuns.cs), from the cache or cooked here; the run goes on with it.
            potentialSunsAsked = true;
            sunNodeSuns = PotentialGrid();
            sunNodeKeep = 0;
            sunNodeSlice = args.Int("sun-nodes-slice") is { } gs ? Math.Clamp(gs, 1024, 65535) : 65535;
            sunNodeStage = SunNodeStage.Waiting;
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] potential suns: {GridHour} x {GridDec} nodes over the band, {sunNodeRays} rays a texel when cooked."));
            return;
        }
        if (args.String("sun-nodes") is not { } file) return;
        var suns = new List<(float, float)>();
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            suns.Add((float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture)));
        }
        sunNodeSuns = suns.ToArray();
        sunNodeDir = args.String("sun-nodes-out") ?? Path.GetDirectoryName(Path.GetFullPath(file))!;
        Directory.CreateDirectory(sunNodeDir);
        sunNodeManifest = file + ".nodes.manifest";
        if (args.Int("sun-nodes-keep") is { } k) sunNodeKeep = Math.Max(0, k);
        if (stride is { } st) sunNodeStride = Math.Max(1, st);
        sunNodeExact = exact;
        sunNodeStage = SunNodeStage.Waiting;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sun nodes: {sunNodeSuns.Length} suns from {file}, {sunNodeRays} rays a texel, slices of {sunNodeSlice:N0}; out to {sunNodeDir}."));
    }

    private void DeclareSunNodePasses(Func<string[], ShaderInterface> reflect)
    {
        DeclareSunNodeSolvePasses(reflect);
        sunNodePrepInterface = reflect(new[] { "texel_nodes_prep.comp" });
        sunNodePrepPass = graph.ComputePass("sun-nodes-prep").Shader(sunNodePrepInterface).Handle;
        sunNodeRaysInterface = reflect(new[] { "texel_nodes_rays.comp" });
        sunNodeRaysPass = graph.ComputePass("sun-nodes-rays").Shader(sunNodeRaysInterface).Handle;
        sunNodeClosestBatch = DeclareClosestBatch("sun-nodes-closest");
        sunNodeExactOriginsPass = graph.ComputePass("sun-nodes-exact-origins").Shader(reflect(new[] { "texel_nodes_shade.comp" })).Handle;
        sunNodeExactBatch = DeclareShadowBatch("sun-nodes-exact");
        sunNodeShadeInterface = reflect(new[] { "texel_nodes_shade.comp" });
        sunNodeShadePass = graph.ComputePass("sun-nodes-shade").Shader(sunNodeShadeInterface).Handle;
    }

    private void CreateSunNodePipelines(string shaderDir)
    {
        PipelineHandle Make(string name, ShaderInterface si) => Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, name + ".comp.spv")), si, name)), name));
        CreateSunNodeSolvePipeline(shaderDir);
        sunNodePrepPipeline = Make("texel_nodes_prep", sunNodePrepInterface);
        sunNodeRaysPipeline = Make("texel_nodes_rays", sunNodeRaysInterface);
        sunNodeShadePipeline = Make("texel_nodes_shade", sunNodeShadeInterface);
    }

    // RecordTexels, each frame of a --sun-nodes run.
    private void RecordSunNodes()
    {
        switch (sunNodeStage)
        {
            case SunNodeStage.Waiting: BeginSunNodes(); break;
            case SunNodeStage.Relighting:
                // The patches' light: solved on the GPU (SponzaLoop.SunNodesSolve.cs), or the CPU's task.
                var solved = sunNodeRelight is null ? StepGpuSolve() : sunNodeRelight.IsCompleted;
                if (solved && SunNodeTraceReady) UploadSunNodes();
                break;
            case SunNodeStage.Prep: RecordSunNodePrep(); break;
            case SunNodeStage.Discover: RecordSunNodeDiscover(); break;
            case SunNodeStage.Merge: MergeDiscovered(); break;
            case SunNodeStage.Tracing: RecordSunNodeSlice(); break;
        }
    }

    // The trace waits for the texels' sun bits (SunNodeBitsFor) as well as the patches' solve.
    private bool SunNodeTraceReady => texelLiveIds is not null && sunNodeBitsTask is { IsCompleted: true }
        && sunNodeGuideTask is null or { IsCompleted: true };

    // As soon as the texels, the patches and the tracer are up (the sunlets' sun rays go to Metal): each sun's direction,
    // the texels' sun bits toward them, and the patches' light, all the suns solved together -- both off the frame thread.
    private void BeginSunNodes()
    {
        // After the texels are up: a boot sun with no light cached joins the suns there (FoldBootSunIntoCook).
        if (!texelsUploaded || transportRelightMany is null || cookedPacked is null || !EnsureFrameTracer() || relightTask is not null || lightBakeActive) return;
        if (potentialSunsAsked && (lightBakeKey is null || TryLoadPotentialSuns())) return;
        var suns = sunNodeSuns!;
        var north = BandNorth(out _);
        sunNodeDirs = new Vector4[suns.Length];
        var toSuns = new Vector3[suns.Length];
        for (var k = 0; k < suns.Length; k++)
        {
            toSuns[k] = ToScene(SunLocal(BandLatitude, suns[k].Dec, suns[k].Hour), north);
            sunNodeDirs[k] = new Vector4(toSuns[k], 0f);
        }
        sunNodeBitsTask = SunNodeBitsFor(toSuns);
        if (SunNodeGpuSolve)
        {
            sunNodeClock.Restart();
            // The cook's suns, each with the sky, warm-started from the boot answer (as the CPU's relight is).
            BeginGpuSolve(sunNodeDirs.Select(d => (new Vector3(d.X, d.Y, d.Z), true, 1f)).ToArray(), warm: true);
            sunNodeRelight = null;
            sunNodeStage = SunNodeStage.Relighting;
            Console.WriteLine(string.Create(Inv, $"[VulkanSponza] sun nodes: solving {transportSolve!.Patches:N0} patches' light for {suns.Length} suns on the GPU."));
            return;
        }
        var relightMany = transportRelightMany;
        var patches = cookedPacked.Length / 3;
        var irradiance = EffectiveSunIrradiance;
        sunNodeClock.Restart();
        sunNodeRelight = Task.Run(() =>
        {
            var incident = relightMany(toSuns, irradiance);
            // [patch][sun], half rgb in two words.
            var light = new uint[(long)patches * suns.Length * 2];
            Parallel.For(0, patches, p =>
            {
                for (var k = 0; k < suns.Length; k++)
                {
                    var c = incident[k][p];
                    long at = ((long)p * suns.Length + k) * 2;
                    light[at] = HalfBits(c.X) | (HalfBits(c.Y) << 16);
                    light[at + 1] = HalfBits(c.Z);
                }
            });
            return light;
        });
        sunNodeStage = SunNodeStage.Relighting;
        Console.WriteLine(string.Create(Inv, $"[VulkanSponza] sun nodes: solving {patches:N0} patches' light for {suns.Length} suns."));
    }

    private static uint HalfBits(float v) => BitConverter.HalfToUInt16Bits((Half)v);

    private void UploadSunNodes()
    {
        var light = sunNodeRelight?.Result;
        sunNodeRelight = null;
        ReleaseGpuSolve();
        sunNodeRelightSeconds = sunNodeClock.Elapsed.TotalSeconds;   // the solve, and any wait on the texels after it
        var suns = sunNodeSuns!.Length;
        var live = texelLiveIds!;
        var words = (suns + 31) / 32;
        if (words > 8) throw new InvalidOperationException($"--sun-nodes: at most 256 suns ({suns} asked).");
        sunNodeHashSlots = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(live.Length * 2, 1024));
        var empty = new uint[sunNodeHashSlots];
        Array.Fill(empty, 0xFFFFFFFFu);
        sunNodeLive = Own(device.CreateGpuBuffer(live.Length * 4, MemoryMarshal.AsBytes(live.AsSpan()), "sponza.sun-nodes.live"));
        sunNodeTraceIds = sunNodeStride == 1 ? live : live.Where((_, i) => i % sunNodeStride == 0).ToArray();
        sunNodeTraceBuffer = sunNodeStride == 1 ? sunNodeLive
            : Own(device.CreateGpuBuffer(sunNodeTraceIds.Length * 4, MemoryMarshal.AsBytes(sunNodeTraceIds.AsSpan()), "sponza.sun-nodes.traced"));
        sunNodeSunsBuffer = Own(device.CreateGpuBuffer(sunNodeDirs.Length * 16, MemoryMarshal.AsBytes(sunNodeDirs.AsSpan()), "sponza.sun-nodes.suns"));
        var (bits, points, pointBits) = sunNodeBitsTask!.Result;
        sunNodeBitsTask = null;
        sunNodeBits = Own(device.CreateGpuBuffer(checked(bits.Length * 4), MemoryMarshal.AsBytes(bits.AsSpan()), "sponza.sun-nodes.bits"));
        UploadSunPoints(points, pointBits);
        sunNodeDiscoverList = Own(device.CreateGpuBuffer(16 + DiscoverRoom * 32, new byte[16 + DiscoverRoom * 32], "sponza.sun-nodes.discover"));
        sunNodeHash = Own(device.CreateGpuBuffer(checked((int)((long)sunNodeHashSlots * 4)), MemoryMarshal.AsBytes(empty.AsSpan()), "sponza.sun-nodes.hash"));
        if (light is not null)
            sunNodePatchLight = Own(device.CreateGpuBuffer(checked((int)((long)light.Length * 4)), MemoryMarshal.AsBytes(light.AsSpan()), "sponza.sun-nodes.patch-light"));
        var patchLightBytes = (long)cookedPacked!.Length / 3 * suns * 8;
        var rays = sunNodeSlice * 32;
        sunNodeRaysBuffer = Own(device.CreateGpuBuffer(checked((int)((long)rays * 32)), name: "sponza.sun-nodes.rays"));
        sunNodeHits = Own(device.CreateGpuBuffer(checked((int)((long)rays * 48 + 80)), name: "sponza.sun-nodes.hits"));
        sunNodeRayWeights = Own(device.CreateGpuBuffer(rays * 4, name: "sponza.sun-nodes.ray-weights"));
        var guides = sunNodeGuideTask?.Result;
        sunNodeGuideTask = null;
        sunNodeGuided = guides is not null;
        guides ??= new float[64];
        sunNodeGuideBuffer = Own(device.CreateGpuBuffer(guides.Length * 4, MemoryMarshal.AsBytes(guides.AsSpan()), "sponza.sun-nodes.guides"));
        if (sunNodeExact)
        {
            sunNodeExactOrigins = Own(device.CreateGpuBuffer(rays * 16, name: "sponza.sun-nodes.exact-origins"));
            sunNodeExactOccluded = Own(device.CreateGpuBuffer(rays * 4, name: "sponza.sun-nodes.exact-occluded"));
        }
        sunNodeAccum = Own(device.CreateGpuBuffer(checked((int)((long)sunNodeSlice * suns * 16)), name: "sponza.sun-nodes.accum"));
        sunNodeStats = Own(device.CreateGpuBuffer(48, new byte[48], "sponza.sun-nodes.stats"));
        var keep = Math.Min(sunNodeKeep, suns);
        sunNodeResults = new uint[keep][];
        for (var k = 0; k < keep; k++) sunNodeResults[k] = new uint[texelCount * 2];
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sun nodes: patches solved in {sunNodeRelightSeconds:0.0} s; up: bits {(long)texelCount * words * 4 / 1048576.0:0} MB, hash {sunNodeHashSlots * 4L / 1048576.0:0} MB, patch light {patchLightBytes / 1048576.0:0} MB, rays and hits {rays * 80L / 1048576.0:0} MB, sums {(long)sunNodeSlice * suns * 16 / 1048576.0:0} MB; keeping {keep} suns' light ({keep * texelCount * 8L / 1048576.0:0} MB)."));
        if (rayLodError > 0f)
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] sun nodes: WARNING the frame's ray scene is simplified ({rayLodError} m): a texel behind its mesh's coarse surface gathers from inside it (Sponza: 10% of the light off). Cook with --ray-lod-error 0."));
        sunNodeStage = SunNodeStage.Prep;
    }

    private void RecordSunNodePrep()
    {
        var live = texelLiveIds!.Length;
        var buffers = new[]
        {
            new ShaderBufferBinding("Texels", texelBuffer),
            new ShaderBufferBinding("SunNodesLive", sunNodeLive),
            new ShaderBufferBinding("SunNodeHash", sunNodeHash),
        };
        const int chunk = 65535 * 64;
        for (var first = 0; first < live; first += chunk)
            graph.Dispatch(sunNodePrepPass, new DispatchCommand(sunNodePrepPipeline, (Math.Min(chunk, live - first) + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uPrep", new Vector4Uniform(new Vector4(first, 0f, 0f, sunNodeHashSlots))),
                    new("uRange", new Vector4Uniform(new Vector4(live, 0f, 0f, 0f))),
                    new("uTexelGrid", new Vector4Uniform(texelGrid)),
                },
                Array.Empty<ShaderTextureBinding>(), Buffers: buffers));
        sunNodeFirst = 0;
        sunNodePass = 0;
        sunNodeReadback = false;
        sunNodeClock.Restart();
        sunNodeDiscoverIds = sunNodeDiscoverStride == 0 ? Array.Empty<uint>() : texelLiveIds!.Where((_, i) => i % sunNodeDiscoverStride == 0).ToArray();
        sunNodeDiscoverFirst = 0;
        if (sunNodeDiscoverIds.Length > 0)
            sunNodeDiscoverBuffer = Own(device.CreateGpuBuffer(sunNodeDiscoverIds.Length * 4, MemoryMarshal.AsBytes(sunNodeDiscoverIds.AsSpan()), "sponza.sun-nodes.discover-ids"));
        sunNodeStage = sunNodeDiscoverIds.Length > 0 ? SunNodeStage.Discover : SunNodeStage.Tracing;
    }

    // A pass of the slice a frame: its rays, the CLOSEST batch, every hit shaded for every sun. When the slice has all
    // its passes, the next frame reads its sums back (the GPU idle first) and moves on.
    private void RecordSunNodeSlice()
    {
        var live = sunNodeTraceIds;
        var suns = sunNodeSuns!.Length;
        var passes = sunNodeRays / 32;
        if (sunNodeReadback)
        {
            var clock = Stopwatch.StartNew();
            var count = Math.Min(sunNodeSlice, live.Length - sunNodeFirst);
            device.WaitIdle();
            var sums = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(sunNodeAccum, 0, count * suns * 16).AsSpan()).ToArray();
            if (potentialSunsAsked) StagePotentialRows(sums, count, suns, SunNodeCompressed, passes, sunNodeFirst + count >= live.Length);
            var results = sunNodeResults;
            var first = sunNodeFirst;
            if (sunNodeBootPath is not null)
            {
                var boot = sunNodeBootResult ??= new uint[texelCount * 2];
                Parallel.For(0, count, t =>
                {
                    var id = (int)live[first + t];
                    var v = sums[(long)t * suns + suns - 1];
                    boot[2 * id] = PackRgb9e5(new Vector3(v.X, v.Y, v.Z) / passes);
                    boot[2 * id + 1] = 0xFFFFu | 0x10000u;
                });
            }
            Parallel.For(0, count, t =>
            {
                var id = (int)live[first + t];
                for (var k = 0; k < results.Length; k++)
                {
                    var s = sums[(long)t * suns + k];
                    results[k][2 * id] = PackRgb9e5(new Vector3(s.X, s.Y, s.Z) / passes);
                    results[k][2 * id + 1] = 0xFFFFu | 0x10000u;
                }
            });
            sunNodeReadSeconds += clock.Elapsed.TotalSeconds;
            sunNodeReadback = false;
            sunNodeFirst += count;
            var done = (double)sunNodeFirst / live.Length;
            if ((int)(done * 20) != (int)((double)(sunNodeFirst - count) / live.Length * 20))
                Console.WriteLine(string.Create(Inv,
                    $"[VulkanSponza] sun nodes: {done * 100:0}% of {live.Length:N0} texels ({sunNodeClock.Elapsed.TotalMinutes:0.0} min)."));
            if (sunNodeFirst >= live.Length) { FinishSunNodes(); return; }
        }
        var slice = Math.Min(sunNodeSlice, live.Length - sunNodeFirst);
        DispatchSunNodePass(sunNodeTraceBuffer, sunNodeFirst, slice, sunNodePass, discover: 0);
        if (++sunNodePass == passes)
        {
            sunNodePass = 0;
            sunNodeReadback = true;
        }
    }

    private void FinishSunNodes()
    {
        sunNodeStage = SunNodeStage.Done;
        var suns = sunNodeSuns!;
        var trace = sunNodeClock.Elapsed.TotalMinutes;
        var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(sunNodeStats, 0, 40).AsSpan()).ToArray();
        var stats = Enumerable.Range(0, 5).Select(k => (ulong)words[2 * k] | ((ulong)words[2 * k + 1] << 32)).ToArray();
        var hits = Math.Max(stats[0], 1ul);
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sun nodes: traced in {trace:0.0} min ({(long)sunNodeTraceIds.Length * sunNodeRays / 1e9:0.00} G rays, {sunNodeReadSeconds:0} s of it reading back) for {suns.Length} suns ({(sunNodeGuided ? "half the rays by the band's guides" : "cosine rays")}), after {sunNodeRelightSeconds:0.0} s of patch solves; hits {stats[0]:N0} ({100.0 * stats[4] / hits:0.00}% answered by the foliage's points, {100.0 * stats[1] / hits:0.00}% among neither: no sun; {100.0 * stats[2] / hits:0.00}% no patch), misses {stats[3]:N0}."));
        // The grid's suns, counted before the boot sun's answer is handed over (RestOnBootSun forgets it was one of them).
        var compressed = SunNodeCompressed;
        RestOnBootSun();
        if (potentialSunsAsked)
        {
            FinishPotentialSuns(compressed);
            return;
        }
        var lines = new List<string> { "# hour dec elevation toSun.x toSun.y toSun.z resolved" };
        for (var k = 0; k < sunNodeResults.Length; k++)
        {
            var path = Path.Combine(sunNodeDir, string.Create(Inv, $"sun-{k:000}.resolved"));
            WriteResolved(path, sunNodeResults[k]);
            var d = sunNodeDirs[k];
            lines.Add(string.Create(Inv,
                $"{suns[k].Hour:0.####} {suns[k].Dec:0.####} {MathF.Asin(d.Y) * 180f / MathF.PI:0.###} {d.X:0.######} {d.Y:0.######} {d.Z:0.######} {path}"));
        }
        File.WriteAllLines(sunNodeManifest, lines);
        sunNodeResults = Array.Empty<uint[]>();
        Console.WriteLine($"[VulkanSponza] sun nodes: done; manifest {sunNodeManifest}; closing.");
        host.RequestClose();
    }

    // texel_resolve.comp's packRgb9e5.
    private static uint PackRgb9e5(Vector3 c)
    {
        const float maxValue = 65408f;
        c = Vector3.Clamp(c, Vector3.Zero, new Vector3(maxValue));
        var maxC = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        var e = Math.Max(-16, (int)MathF.Floor(MathF.Log2(MathF.Max(maxC, 1e-30f)))) + 1;
        var scale = MathF.Pow(2f, e - 9);
        if ((int)MathF.Floor(maxC / scale + 0.5f) == 512) { e += 1; scale *= 2f; }
        var r = (uint)MathF.Floor(c.X / scale + 0.5f);
        var g = (uint)MathF.Floor(c.Y / scale + 0.5f);
        var b = (uint)MathF.Floor(c.Z / scale + 0.5f);
        return r | (g << 9) | (b << 18) | ((uint)(e + 15) << 27);
    }

    // The cook's buffers, once the nodes are compressed and up (the live list stays: the resolve reads it).
    private void ReleaseSunNodeCook()
    {
        foreach (var b in new[] { sunNodeSunsBuffer, sunNodeBits, sunNodeHash, sunNodePatchLight, sunNodeRaysBuffer, sunNodeHits, sunNodeAccum, sunNodeStats, sunNodeFoliagePoints, sunNodeFoliageHash, sunNodeFoliageBits, sunNodeDiscoverBuffer, sunNodeDiscoverList, sunNodeGuideBuffer, sunNodeRayWeights })
            if (!b.Equals(default(GpuBufferHandle))) RetireGpuBuffer(b);
        sunNodeSunsBuffer = sunNodeBits = sunNodeHash = sunNodePatchLight = sunNodeRaysBuffer = sunNodeHits = sunNodeAccum = sunNodeStats = default;
        sunNodeFoliagePoints = sunNodeFoliageHash = sunNodeFoliageBits = sunNodeDiscoverBuffer = sunNodeDiscoverList = sunNodeGuideBuffer = sunNodeRayWeights = default;
        sunNodePoints = Array.Empty<Vector4>();
        sunNodePointBits = Array.Empty<uint>();
    }

    // UploadTexels with no light cached for the boot sun: a cook about to trace every texel's gather anyway answers it --
    // one more sun shaded from the same rays, where a light bake is a gather of its own (Sponza: 20 minutes). Not when
    // the boot sun is outside the band (the sun records answer only inside it) or no cook will run.
    private bool FoldBootSunIntoCook(string bakePath)
    {
        if (sunNodeStage != SunNodeStage.Waiting || sunNodeSuns is null || lightBakeKey is null) return false;
        if (potentialSunsAsked && File.Exists(PotentialSunsPathFor(lightBakeKey))) return false;
        var toSun = transportRelightSun.ToSun;
        var north = BandNorth(out _);
        var (hour, dec) = Equatorial(toSun, BandLatitude, north);
        if (MathF.Abs(dec) > Obliquity + 5f || MathF.Abs(hour) > DayHalfSpan(dec)) return false;
        sunNodeSuns = sunNodeSuns.Append((hour, dec)).ToArray();
        sunNodeBootPath = bakePath;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] light bake: not run -- the sun nodes' cook answers the boot sun (hour {hour:0.00}, dec {dec:0.00}) from the same rays; the texels gather as they are seen until it lands."));
        return true;
    }

    // The cook's answer for the boot sun: saved as the bake's resolved light, and the texels at rest on it.
    private void RestOnBootSun()
    {
        if (sunNodeBootPath is not { } path || sunNodeBootResult is not { } words) return;
        WriteResolved(ResolvedPathFor(path), words);
        if (!texelResolvedBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(texelResolvedBuffer);
        texelResolvedBuffer = Own(device.CreateGpuBuffer(words.Length * 4, MemoryMarshal.AsBytes(words.AsSpan()), "sponza.texels.resolved"));
        texelResting = true;
        texelResolveCurrent = true;
        texelResolvedFrame = texelFrame;   // RestTexels lets the gather's buffers go a few frames on
        texelsSettled = true;
        texelSettledEpoch = texelEpoch;
        pendingResolvedSave = null;
        sunNodeBootResult = null;
        sunNodeBootPath = null;
        Console.WriteLine("[VulkanSponza] texels: at rest on the cook's answer for the boot sun.");
    }

    // Each live texel's sun bits: bit k where a ray from it toward sun k meets nothing (Metal, the frame's tracer, its
    // alpha test as the traced shadows have it) -- what the gather's hits read their direct sun from. One ray a texel and
    // sun, from its centre lifted off its surface (Sponza: 15 M texels x 154 suns, 2.4 G rays); the lightlet records
    // answered this before, and cost a bake of their own when not cached (Sponza: ~35 minutes).
    private Task<(uint[] Texel, Vector4[] Points, uint[] PointBits)> SunNodeBitsFor(Vector3[] toSuns)
    {
        var tracer = frameTracer!;
        var live = texelLiveIds!;
        var count = texelCount;
        var words = (toSuns.Length + 31) / 32;
        device.WaitIdle();
        var texels = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelBuffer, 0, count * 32).AsSpan()).ToArray();
        var reach = rayQueries is { } rq ? (rq.Nodes[0].Bounds.Max - rq.Nodes[0].Bounds.Min).Length() : 1000f;
        var directions = toSuns.Select(d => new Vector4(d, 0f)).ToArray();
        sunNodeBitDirections = directions;
        sunNodeReach = reach;
        var lift = sunNodeLift;
        // The foliage's sun points (SunNodesFoliage.cs) from the frame's ray scene and its baked coverage.
        var rayData = rayGpuData;
        var surfaceWords = rayData is null ? Array.Empty<uint>()
            : MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(raySurfaces, 0, rayData.RowPlacements.Length * 4).AsSpan()).ToArray();
        var grid = texelGrid;
        return Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            var bits = new uint[(long)count * words];
            double gpuMs = 0;
            for (var first = 0; first < live.Length; first += LightletSlice)
            {
                var n = Math.Min(LightletSlice, live.Length - first);
                var points = new Vector4[n * 2];
                for (var p = 0; p < n; p++)
                {
                    var id = (int)live[first + p];
                    points[2 * p] = texels[2 * id] with { W = 0f };
                    points[2 * p + 1] = texels[2 * id + 1] with { W = 0f };
                }
                var slice = tracer.LightletVisibility(points, directions, words, LightletSeed, lift, reach, out var ms);
                gpuMs += ms;
                for (var p = 0; p < n; p++)
                    Array.Copy(slice, (long)p * words, bits, (long)live[first + p] * words, words);
            }
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] sun nodes: sun bits for {live.Length:N0} texels x {toSuns.Length} suns ({(long)live.Length * toSuns.Length / 1e9:0.00} G rays) in {clock.Elapsed.TotalSeconds:0.0} s ({gpuMs / 1000:0.0} s GPU)."));
            var foliage = Array.Empty<Vector4>();
            var foliageBits = Array.Empty<uint>();
            if (rayData is not null)
            {
                var fclock = Stopwatch.StartNew();
                foliage = BuildFoliagePoints(rayData, surfaceWords, grid);
                foliageBits = ShootSunBits(tracer, foliage, directions, words, reach, lift, out var fgpu);
                Console.WriteLine(string.Create(Inv,
                    $"[VulkanSponza] sun nodes: the foliage's sun points: {foliage.Length / 2:N0} on the partly covered triangles, their bits in {fclock.Elapsed.TotalSeconds:0.0} s ({fgpu / 1000:0.0} s GPU)."));
            }
            return (bits, foliage, foliageBits);
        });
    }

    // One pass of a slice: its rays (from `ids`, the texels traced), the CLOSEST batch, every hit shaded for every sun --
    // or, discovering (`discover`: the list's room), the hits among neither texel nor sun point listed (NodeDiscover).
    private void DispatchSunNodePass(GpuBufferHandle ids, int first, int slice, int pass, int discover)
    {
        var raysDeclared = sunNodeRaysInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        graph.Dispatch(sunNodeRaysPass, new DispatchCommand(sunNodeRaysPipeline, slice, 1, 1,
            new ShaderUniform[]
            {
                new("uSlice", new Vector4Uniform(new Vector4(first, slice, pass, sunNodeGuided ? 1f : 0f))),
                new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                new("uCookedDims", new Vector4Uniform(cookedDims)),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("SunNodesLive", ids),
                new ShaderBufferBinding("NodeRays", sunNodeRaysBuffer),
                new ShaderBufferBinding("NodeRayWeights", sunNodeRayWeights),
                new ShaderBufferBinding("NodeGuides", sunNodeGuideBuffer),
            }.Concat(CookedBuffers()).Where(b => raysDeclared.Contains(b.Name)).ToArray()));
        var seed = unchecked((uint)(first * 2654435761L) ^ (uint)pass * 0x9E3779B9u ^ (discover > 0 ? 0xD15C0u : 0u));
        // The frame's tracer: cook with --ray-lod-error 0, so it is the LOD 0 the texels sit on (UploadSunNodes warns). The
        // bake scene's tracer read the light 28% dark on Cornell, the same 120 triangles (its hits' albedo; open).
        RecordClosestBatch(sunNodeClosestBatch, sunNodeRaysBuffer, sunNodeHits, slice * 32, seed);
        var declared = sunNodeShadeInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        var exact = sunNodeExact && discover == 0;
        if (exact)
        {
            graph.Dispatch(sunNodeExactOriginsPass, ShadeCommand(1f));
            RecordShadowBatch(sunNodeExactBatch, sunNodeExactOrigins, sunNodeExactOccluded, slice * 32,
                new Vector3(sunNodeDirs[0].X, sunNodeDirs[0].Y, sunNodeDirs[0].Z), seed ^ 0x5bd1e995u);
        }
        graph.Dispatch(sunNodeShadePass, ShadeCommand(exact ? 2f : 0f));
        DispatchCommand ShadeCommand(float exactMode) => new DispatchCommand(sunNodeShadePipeline, slice, 1, 1,
            new ShaderUniform[]
            {
                new("uShade", new Vector4Uniform(new Vector4(slice, sunNodeSuns!.Length, sunNodeHashSlots, pass))),
                new("uTexelGrid", new Vector4Uniform(texelGrid)),
                new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                new("uCookedDims", new Vector4Uniform(cookedDims)),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, sunNodeUnknownSun))),
                // A ray's sky as the gather takes it: strength 1, one mip blurred.
                new("uSky", new Vector4Uniform(new Vector4(1f, 1f, 0f, 0f))),
                new("uLookup", new Vector4Uniform(new Vector4(sunNodePlane, sunNodeSigma, 0f, 0f))),
                new("uFoliage", new Vector4Uniform(new Vector4(sunNodeFoliageSlots, discover, exactMode, 0f))),
            },
            new[] { new ShaderTextureBinding("uSkyRadiance", envCubeTexture) },
            Buffers: new[]
            {
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("NodeRays", sunNodeRaysBuffer),
                new ShaderBufferBinding("NodeHits", sunNodeHits),
                new ShaderBufferBinding("SunNodes", sunNodeSunsBuffer),
                new ShaderBufferBinding("SunNodeBits", sunNodeBits),
                new ShaderBufferBinding("SunNodeHash", sunNodeHash),
                new ShaderBufferBinding("SunNodePatchLight", sunNodePatchLight),
                new ShaderBufferBinding("NodeAccum", sunNodeAccum),
                new ShaderBufferBinding("NodeStats", sunNodeStats),
                new ShaderBufferBinding("FoliagePoints", sunNodeFoliagePoints),
                new ShaderBufferBinding("FoliageHash", sunNodeFoliageHash),
                new ShaderBufferBinding("FoliageBits", sunNodeFoliageBits),
                new ShaderBufferBinding("NodeDiscover", sunNodeDiscoverList),
                new ShaderBufferBinding("ExactOrigins", sunNodeExact ? sunNodeExactOrigins : sunNodeRaysBuffer),
                new ShaderBufferBinding("ExactOccluded", sunNodeExact ? sunNodeExactOccluded : sunNodeHits),
                new ShaderBufferBinding("NodeRayWeights", sunNodeRayWeights),
            }.Concat(CookedBuffers()).Where(b => declared.Contains(b.Name)).ToArray());
    }

    // Each point's sun bits (two vec4s a point: position, normal), as the texels' are shot.
    private static uint[] ShootSunBits(Blix.Graphics.MetalHWX.MetalRayTracer tracer, Vector4[] points, Vector4[] directions, int words, float reach, float lift, out double gpuMs)
    {
        var count = points.Length / 2;
        var bits = new uint[(long)count * words];
        gpuMs = 0;
        for (var first = 0; first < count; first += LightletSlice)
        {
            var n = Math.Min(LightletSlice, count - first);
            var slice = points.AsSpan(first * 2, n * 2).ToArray();
            for (var i = 0; i < n; i++) slice[2 * i] = slice[2 * i] with { W = 0f };
            var answer = tracer.LightletVisibility(slice, directions, words, LightletSeed, lift, reach, out var ms);
            gpuMs += ms;
            Array.Copy(answer, 0, bits, (long)first * words, (long)n * words);
        }
        return bits;
    }

    // The sun points up (replacing any before): positions and normals, their hash, their bits.
    private void UploadSunPoints(Vector4[] points, uint[] bits)
    {
        foreach (var b in new[] { sunNodeFoliagePoints, sunNodeFoliageHash, sunNodeFoliageBits })
            if (!b.Equals(default(GpuBufferHandle))) RetireGpuBuffer(b);
        sunNodePoints = points;
        sunNodePointBits = bits;
        var hash = SunPointHash(points, texelGrid);
        sunNodeFoliageSlots = points.Length == 0 ? 0 : hash.Length;
        sunNodeFoliagePoints = Own(device.CreateGpuBuffer(Math.Max(16, points.Length * 16), MemoryMarshal.AsBytes(points.AsSpan()), "sponza.sun-nodes.sun-points"));
        sunNodeFoliageHash = Own(device.CreateGpuBuffer(Math.Max(16, hash.Length * 4), MemoryMarshal.AsBytes(hash.AsSpan()), "sponza.sun-nodes.sun-point-hash"));
        sunNodeFoliageBits = Own(device.CreateGpuBuffer(Math.Max(16, bits.Length * 4), MemoryMarshal.AsBytes(bits.AsSpan()), "sponza.sun-nodes.sun-point-bits"));
    }

    // Discovery: a pass of one slice of every --sun-nodes-discover-stride th live texel a frame, the hits among neither
    // texel nor sun point listed; after the last, the list read back and made sun points (off the frame thread).
    private void RecordSunNodeDiscover()
    {
        if (sunNodeDiscoverFirst < sunNodeDiscoverIds.Length)
        {
            var slice = Math.Min(sunNodeSlice, sunNodeDiscoverIds.Length - sunNodeDiscoverFirst);
            DispatchSunNodePass(sunNodeDiscoverBuffer, sunNodeDiscoverFirst, slice, 0, DiscoverRoom);
            sunNodeDiscoverFirst += slice;
            return;
        }
        device.WaitIdle();
        var count = (int)Math.Min(MemoryMarshal.Read<uint>(device.ReadGpuBuffer(sunNodeDiscoverList, 0, 4)), (uint)DiscoverRoom);
        var found = count == 0 ? Array.Empty<Vector4>()
            : MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(sunNodeDiscoverList, 16, count * 32).AsSpan()).ToArray();
        var existing = sunNodePoints;
        var grid = texelGrid;
        var tracer = frameTracer!;
        var directions = sunNodeBitDirections;
        var words = (directions.Length + 31) / 32;
        var reach = sunNodeReach;
        var lift = sunNodeLift;
        var clock = Stopwatch.StartNew();
        sunNodeDiscoverTask = Task.Run(() =>
        {
            var added = NewSunPoints(existing, found, grid);
            var bits = ShootSunBits(tracer, added, directions, words, reach, lift, out _);
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] sun nodes: discovery found {count:N0} hits among neither texel nor sun point ({(count >= DiscoverRoom ? "the list full; " : "")}every {sunNodeDiscoverStride}th texel, a pass), {added.Length / 2:N0} new sun points; bits in {clock.Elapsed.TotalSeconds:0.0} s."));
            return (added, bits);
        });
        sunNodeStage = SunNodeStage.Merge;
    }

    private void MergeDiscovered()
    {
        if (sunNodeDiscoverTask is not { IsCompleted: true } done) return;
        var (added, bits) = done.Result;
        sunNodeDiscoverTask = null;
        UploadSunPoints(sunNodePoints.Concat(added).ToArray(), sunNodePointBits.Concat(bits).ToArray());
        // The trace proper counts afresh.
        RetireGpuBuffer(sunNodeStats);
        sunNodeStats = Own(device.CreateGpuBuffer(48, new byte[48], "sponza.sun-nodes.stats"));
        sunNodeClock.Restart();
        sunNodeStage = SunNodeStage.Tracing;
    }
}
