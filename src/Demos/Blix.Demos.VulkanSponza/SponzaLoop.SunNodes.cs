using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The texels' gather (the split light's cook, and --sun-nodes FILE: many suns from ONE trace). A light bake traces every
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
    // Each column's kind: 1 its sun alone, 2 the sky alone, 3 both (the default; texel_nodes_shade.comp). FILE's third
    // word: "sun" or "sky" (a sky column's hour and declination are not read).
    private int[] sunNodeKinds = Array.Empty<int>();
    private int SunNodeKind(int k) => k < sunNodeKinds.Length ? sunNodeKinds[k] : 3;
    // Columns' sun directions given outright (the split's: SponzaLoop.SplitLight.cs), not by hour and declination.
    private Vector3[]? sunNodeDirsAsked;
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
    private PassHandle sunNodePrepPass;
    // A pass of the trace, SunNodePassCopies times over (each copy its own passes): a frame graph pass takes one piece of
    // external (Metal) work a frame, so one set of passes held the cook to a pass a frame -- frames spinning for the
    // cook, ~3,800 of them for Sponza's sweep. Under the cook screen a frame records a copy a pass, up to the slice's
    // readback (SponzaLoop.CookScreen.cs). sunNodeCopy: the copy the next pass records into (0 outside it).
    // --split-passes-per-frame N (1, up to SunNodePassCopies): how many a cook-screen frame records. More is a heavier
    // command buffer: held at 1 while Sponza's split cook still meets the GPU's watchdog (2026-10-13).
    private const int SunNodePassCopies = 4;
    private int sunNodePassesPerFrame = 1;
    // --live-texels: the texels on screen take every copy a frame has (they must settle in a second or two); the rest of
    // the scene the asked passes, a background budget that never holds the image up.
    private int SunNodePassesNow => liveTexels && sunNodeRefineRound == 0 && sunNodeFirst < splitVisibleCount ? SunNodePassCopies : sunNodePassesPerFrame;
    private readonly PassHandle[] sunNodeRaysPasses = new PassHandle[SunNodePassCopies], sunNodeShadePasses = new PassHandle[SunNodePassCopies],
        sunNodeExactOriginsPasses = new PassHandle[SunNodePassCopies];
    private readonly RayBatchPasses[] sunNodeClosestBatches = new RayBatchPasses[SunNodePassCopies], sunNodeExactBatches = new RayBatchPasses[SunNodePassCopies];
    private int sunNodeCopy;
    private PipelineHandle sunNodePrepPipeline, sunNodeRaysPipeline, sunNodeShadePipeline;
    // Each texel's nearest visible patch (texel_nodes_patch.comp), before the trace: PatchCandidates rays a texel.
    private const int PatchCandidates = 4;
    private ShaderInterface sunNodePatchInterface = null!;
    private PassHandle sunNodePatchAPass, sunNodePatchBPass;
    private PipelineHandle sunNodePatchPipeline;
    private RayBatchPasses sunNodePatchBatch;
    private GpuBufferHandle sunNodeTexelPatch, sunNodePatchCandidates;
    private int sunNodePatchFirst;
    // --sun-nodes-exact-sun (one sun): the hits' sun by a shadow ray each, as the bake's gather has it -- the check on
    // the sun bits' interpolation.
    private bool sunNodeExact;
    // --hit-sun disc|point|cells (disc): every gather hit's sun by its own shadow ray, traced by the Metal trace in the
    // same thread as the hit (MetalRayTracer.Trace's sun, hit flag 8), toward its own point of the sun's disc
    // (ShadowsSettings.SunDiscDegrees, as the frame's direct sun and the GPU reference have it) or the disc's centre --
    // in place of the cells' and the search's blend of the texels' sun bits, which made O(1) errors within a grid spacing
    // of where a shadow edge meets a crease (tools/gi-model/sun_vis.py): Cornell's creases |error| 2.38% -> 2.08%, rms
    // 6.5 -> 5.1%, no noise added, 3.0 -> 3.1 ms a pass (2026-10-21). The cells and the search still answer the patch.
    // Not yet where sheets transmit (the bits' shadow rays pass the sheet they leave and go through cloth, blix_shadow;
    // the hit's does neither): those scenes keep the cells' sun until the materials' own pass.
    private bool hitSunRay = true, hitSunDisc = true;
    private bool hitSunSaid;
    // The gather's rays interleaved over each 3x3 tile of cells (texel_nodes_rays.comp, interleaveTile): from the open
    // courtyard of crevices (the user's view, two seeds) surfaces' noise 1.17x less at the same rays; down the dark
    // corridor, where the noise is near-field and neighbours do not share it (tools/gi-model/ray_mix.py), no gain.
    // On by default (2026-10-21). --no-interleave: off.
    private bool interleave = true;
    // Sheets that transmit as the spec has them in the GI (default; --no-sheet-light) (tools/gi-model/thin_sheet.py) -- a gather hit on
    // one reflects 1 - T of the sun on its side and passes T c of the sun on its far side (its own shadow ray from there:
    // the hit sun ray, on now where sheets are); the patch solve's sheets pass T c / albedo of their twin's light and keep
    // 1 - T of their own. Without, the sun through sheets is the frame's alone (its one-disc glow), as before.
    private bool sheetLight;
    private bool SheetsInGi => sheetLight && rtSheetRows > 0 && SheetRowsBuffer is not null;
    // --near-rounds N (2; 0 without the near texels' light): the fine round's texels gathered N times more, each on the light the last left (with
    // --near-texel-light, its near hits read the texels the last round gathered).
    private int nearRounds, nearRoundsLeft;
    private uint[] fineRoundIds = Array.Empty<uint>();
    // Per record, a bit: on a surface that lets light through (SponzaLoop.Lightmap.cs, UploadLightmapTexels).
    private uint[]? transmitRecords;
    // --patch-pick-old: the texel's patch pick without the behind rule and the scaled lift (texel_nodes_patch.comp), as before.
    private bool patchPickOld;
    // --near-cut (default; --no-near-cut the old full regather): a near round traces fineGatherRays short rays (to hitCellNear, the reach of the near texels' light) with
    // no refine, the fine round's far part added back (texel_nodes_shade.glsl FarPart): the ray capture had the near
    // rounds at two thirds of the fine round's rays, every one re-tracing what could not change (2026-10-22).
    private bool nearCut;
    // A near hit among no texel takes the pass's near hits' light (texel_nodes_shade.glsl; --no-near-unknown-local: its
    // nearest patch facing its way, as before).
    private bool nearUnknownLocal;
    // --sheet-glow-split (a measurement): a transmitting surface's texels leave out other sheets' glow and the frame draws
    // it per pixel (texel_nodes_shade.glsl ownSheet, lit.frag sheetGlow). Off: the GI keeps it whole, the frame's off.
    private bool sheetGlowSplit;
    // A lightmap record's rays about its smooth (shading) normal, not its face's (texel_nodes_rays.comp; default,
    // --no-gather-smooth-normal: its face's).
    private bool gatherSmooth;
    private bool GatherSmoothNow => gatherSmooth && !lightmapFramesBuffer.Equals(default(GpuBufferHandle));
    private GpuBufferHandle finePlaceBuffer, farPartBuffer;
    private int NearRoundIndex => nearRounds - nearRoundsLeft;
    // 0 off; 1 the fine round (its far part kept); 2 a near round (cut, the far part added back).
    private int NearCutMode => nearCut && fineGathering && nearRounds > 0 && !farPartBuffer.Equals(default(GpuBufferHandle)) ? (NearRoundIndex == 0 ? 1 : 2) : 0;

    // The fine round's places (a texel's place + 1) and its far parts cleared, once at its start.
    private void BeginNearCut(uint[] fineIds)
    {
        if (!nearCut || nearRounds == 0) return;
        if (!finePlaceBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(finePlaceBuffer);
        if (!farPartBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(farPartBuffer);
        var place = new uint[texelCount];
        for (var i = 0; i < fineIds.Length; i++) place[fineIds[i]] = (uint)i + 1;
        finePlaceBuffer = Own(device.CreateGpuBuffer(Math.Max(16, texelCount * 4), MemoryMarshal.AsBytes(place.AsSpan()), "sponza.sun-nodes.fine-place"));
        farPartBuffer = Own(device.CreateGpuBuffer(Math.Max(16, fineIds.Length * 2 * 16), new byte[Math.Max(16, fineIds.Length * 2 * 16)], "sponza.sun-nodes.far-part"));
    }

    // The fine refine's round over these texels: traced as the coarse were, a round of their own, the cook screen up.
    private void StartFineRound(uint[] fineIds)
    {
        RetireTraceBuffer();
        sunNodeTraceIds = fineIds;
        sunNodeTraceBuffer = Own(device.CreateGpuBuffer(fineIds.Length * 4, MemoryMarshal.AsBytes(fineIds.AsSpan()), "sponza.sun-nodes.fine"));
        fineGatherSavedRays = sunNodeRays;
        fineGatherSavedMaxRays = texelMaxRays;
        sunNodeRays = fineGatherRays;
        texelMaxRays = Math.Max(fineGatherRays, fineGatherMaxRays);
        RetireGpuBuffer(sunNodeStats);
        sunNodeStats = Own(device.CreateGpuBuffer(64, new byte[64], "sponza.sun-nodes.stats"));
        sunNodeFirst = 0;
        sunNodePass = 0;
        sunNodeClock.Restart();
        ResetSunNodeCounters();
        ResetSunNodeRefine();
        fineGathering = true;
        sunNodeStage = SunNodeStage.Tracing;
    }
    // A near hit's bounce (the search's, within --cell-near) the light the texel it lies among last gathered, not its
    // patch's (texel_nodes_shade.glsl, nearTexelLight; tools/gi-model/crease_bounce.py), with --near-rounds' regathers:
    // Cornell's creases |error| 1.92% -> 1.38%, the box's base +33% -> +13% (2026-10-21). --no-near-texel-light: off.
    private bool nearTexelLight = true;
    private bool NearTexelLightNow => nearTexelLight && !splitSkyBuffer.Equals(default(GpuBufferHandle)) && !splitSunBuffer.Equals(default(GpuBufferHandle));
    // The band's guides steer half of each texel's rays (texel_nodes_rays.comp); --sun-nodes-no-guide: cosine rays only.
    private bool sunNodeGuide = true;
    private bool sunNodeGuided;
    // The light strategy (texel_nodes_rays.comp): a quarter of each texel's rays aimed at the brightest patches its guide
    // patch sees (BuildLights), every ray weighted by multiple importance sampling over the three strategies. The sweep's
    // texels were noisy in the middle of the light, not the dark: a few rays in fifty hitting a sunlit patch carried most
    // of it, and refining settled ~3% of 7 M texels a round (2026-10-14). --sun-nodes-no-lights: cosine and guide only.
    private bool sunNodeLights = true;
    private bool sunNodeLit;
    private GpuBufferHandle sunNodeLightBuffer;
    private GpuBufferHandle sunNodeGuideBuffer, sunNodeRayWeights;
    private GpuBufferHandle sunNodeExactOrigins, sunNodeExactOccluded;

    private enum SunNodeStage { Off, Waiting, Relighting, Prep, PatchPick, Discover, Merge, Tracing, Done }
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
    private GpuBufferHandle sunNodeGuidePatch;
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
        // The cook's settings, read for both the study (--sun-nodes) and the split light's gather.
        if (args.Int("sun-nodes-rays") is { } r) sunNodeRays = Math.Max(32, r / 32 * 32);
        if (args.Int("sun-nodes-slice") is { } s) sunNodeSlice = Math.Clamp(s, 1024, 65535);
        if (args.Int("split-passes-per-frame") is { } ppf) sunNodePassesPerFrame = Math.Clamp(ppf, 1, SunNodePassCopies);
        sunNodeOnFrame = args.Flag("split-on-frame");
        if (args.Int("sun-nodes-discover-stride") is { } ds) sunNodeDiscoverStride = Math.Max(0, ds);
        if (args.Float("sun-nodes-lift") is { } lift) sunNodeLift = Math.Max(0f, lift);
        nearTexelLight = !args.Flag("no-near-texel-light");
        // On by default (2026-10-22: drapes against its physical reference, two seeds, surfaces |error| 3.36% -> 1.32%,
        // rms 8.9% -> 2.0%; the frame's own glow off with it). --no-sheet-light: the frame's glow alone, as before.
        sheetLight = !args.Flag("no-sheet-light");
        interleave = !args.Flag("no-interleave") || args.Flag("interleave");
        nearRounds = Math.Max(0, args.Int("near-rounds") ?? (nearTexelLight ? 2 : 0));
        texelH4 = args.Flag("texel-h4");
        // --texel-shape-luma: the lit pass takes the incident pass's luminance gradient, not the records' shapes per channel.
        texelShapeLuma = args.Flag("texel-shape-luma");
        // --texel-shape-pool R: the shapes pooled out to R metres (texel_compose.comp; 0.2 by default; 0: the filter's neighbours alone).
        if (args.Float("texel-shape-pool") is { } pool) texelShapePool = Math.Clamp(pool, 0f, 0.5f);
        switch (args.String("hit-sun"))
        {
            case null or "disc": break;
            case "point": hitSunDisc = false; break;
            case "cells": hitSunRay = hitSunDisc = false; break;
            default: throw new AppArgsException("--hit-sun takes disc, point or cells.");
        }
        if (args.Float("sun-nodes-unknown-sun") is { } us) sunNodeUnknownSun = Math.Clamp(us, 0f, 1f);
        sunNodeCpuSolve = args.Flag("sun-nodes-cpu-solve");
        sunNodeGuide = !args.Flag("sun-nodes-no-guide");
        sunNodeLights = !args.Flag("sun-nodes-no-lights");
        noiseCensus = args.Flag("split-noise-census");
        ReadRayCaptureArgs(args);
        ReadListArgs(args);
        // On by default (2026-10-22: the gate held; Cornell and crevices seams' |error| and noise down, two seeds each).
        guideSun = !args.Flag("no-guide-sun");
        instantOnly = args.Flag("instant-only");
        leanShade = args.Flag("lean-shade");
        coneLookups = args.Flag("cone-lookups");
        // --no-patch-levels: cones read a patch and its neighbours at most (the A/B for SponzaLoop.PatchLevels.cs).
        patchLevelsOn = !args.Flag("no-patch-levels");
        // (--cone-volume, VCT's opacity along the cone in front of the exact hit, was tried and taken out: one-sided, every
        // corridor 7-10% dark -- c24a5c9c.)
        // --guide-sky-only: the guides and light lists from the sky's light alone (no sun component, no direct sun) -- the
        // same whatever the sun: for lists cooked once and lit under every sun, so a list cook's default (--list-cook-sun-
        // guides: the cook's sun's). The corridor's lists, two cooks' rays, at three suns: corridor 0.018 / 0.035 / 0.018,
        // seams 0.034 / 0.029 / 0.039; the cook's sun's guides 0.014 / 0.034 / 0.017 and 0.033 / 0.029 / 0.043; cosine
        // alone 0.025 / 0.035 / 0.024 and 0.047 / 0.033 / 0.061 (2026-10-23).
        guideSkyOnly = args.Flag("guide-sky-only")
            || ((args.Flag("list-cook") || args.Flag("list-cook-append")) && !args.Flag("list-cook-sun-guides"));
        nearCut = !args.Flag("no-near-cut");
        // On by default (2026-10-22: two seeds, crevices seams' bias -1.2% -> -0.4%, every seed noise down; the gate held).
        settlePool = !args.Flag("no-settle-pool");
        nearUnknownLocal = !args.Flag("no-near-unknown-local");
        sheetGlowSplit = args.Flag("sheet-glow-split");
        nearGlowSplit = args.Flag("near-glow-split");
        // On by default (2026-10-22: drapes' curtain, close up, the GI's step at a strip's edge against the path trace
        // 1.10% -> 0.38% rms, under the 0.6% within a texel; the gate and two seeds a scene level).
        gatherSmooth = !args.Flag("no-gather-smooth-normal");
        seamBlend = !args.Flag("no-seam-blend");
        ReadFarArgs(args);
        ReadTransportCensusArgs(args);
        ReadFarTransportArgs(args);
        if (args.Int("sun-nodes-bounces") is { } nb) sunNodeBouncesAsked = Math.Max(0, nb);
        if (args.Float("sun-nodes-plane") is { } pl) sunNodePlane = Math.Max(0.01f, pl);
        nodePlanes = !args.Flag("no-node-planes");
        gatherClear = !args.Flag("no-gather-clear");
        if (args.Float("sun-nodes-sigma") is { } sg) sunNodeSigma = Math.Max(0.05f, sg);
        // Study only: every S-th live texel traced (the sun bits and the hash still hold them all), and one sun's hits by
        // a shadow ray each. A grid kept for the run needs every texel, and every sun.
        var stride = args.Int("sun-nodes-stride");
        var exact = args.Flag("sun-nodes-exact-sun");
        if (args.String("sun-nodes") is not { } file) return;
        var suns = new List<(float, float)>();
        var kinds = new List<int>();
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            suns.Add((float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture)));
            kinds.Add(parts.Length > 2 ? parts[2] switch { "sun" => 1, "sky" => 2, _ => 3 } : 3);
        }
        sunNodeSuns = suns.ToArray();
        sunNodeKinds = kinds.ToArray();
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
        DeclareLivePatchLight(reflect);
        DeclareFarPasses(reflect);
        DeclareTransportCensus(reflect);
        DeclarePatchDirect(reflect);
        DeclareFarRelight(reflect);
        DeclareSunProvisional(reflect);
        DeclareLists(reflect);
        leanShadeInterface = reflect(new[] { "texel_nodes_lean.comp" });
        DeclarePatchLevels(reflect);
        transportCensusPass = graph.ComputePass("sun-nodes-transport-census").Shader(transportCensusInterface).Handle;
        sunNodePrepInterface = reflect(new[] { "texel_nodes_prep.comp" });
        sunNodePrepPass = graph.ComputePass("sun-nodes-prep").Shader(sunNodePrepInterface).Handle;
        sunNodeRaysInterface = reflect(new[] { "texel_nodes_rays.comp" });
        texelClearInterface = reflect(new[] { "texel_clear.comp" });

        sunNodePatchInterface = reflect(new[] { "texel_nodes_patch.comp" });
        sunNodePatchAPass = graph.ComputePass("sun-nodes-patch-a").Shader(sunNodePatchInterface).Handle;
        sunNodePatchBatch = DeclareClosestBatch("sun-nodes-patch-closest");
        sunNodePatchBPass = graph.ComputePass("sun-nodes-patch-b").Shader(sunNodePatchInterface).Handle;
        sunNodeShadeInterface = reflect(new[] { "texel_nodes_shade.comp" });
        sunNodeShadeSplitInterface = reflect(new[] { "texel_nodes_shade_split.comp" });
        for (var c = 0; c < SunNodePassCopies; c++)
        {
            var tag = c == 0 ? "" : $"-{c}";
            sunNodeRaysPasses[c] = graph.ComputePass("sun-nodes-rays" + tag).Shader(sunNodeRaysInterface).Handle;
            sunNodeClosestBatches[c] = DeclareClosestBatch("sun-nodes-closest" + tag);
            sunNodeExactOriginsPasses[c] = graph.ComputePass("sun-nodes-exact-origins" + tag).Shader(sunNodeShadeInterface).Handle;
            sunNodeExactBatches[c] = DeclareShadowBatch("sun-nodes-exact" + tag);
            sunNodeShadePasses[c] = graph.ComputePass("sun-nodes-shade" + tag).Shader(sunNodeShadeInterface).Handle;
        }
    }

    private void CreateSunNodePipelines(string shaderDir)
    {
        PipelineHandle Make(string name, ShaderInterface si) => Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, name + ".comp.spv")), si, name)), name));
        CreateSunNodeSolvePipeline(shaderDir);
        liveSunPipeline = Make("texel_live_sun", liveSunInterface);
        CreateHitCellsPipeline(shaderDir);
        CreateTransferPipelines(shaderDir);
        CreateListsPipeline(shaderDir);
        leanShadePipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_nodes_lean.comp.spv")), leanShadeInterface, "texel_nodes_lean")), "texel_nodes_lean"));
        CreatePatchLevelsPipeline(shaderDir);
        liveDirectPipeline = Make("texel_live_direct", liveDirectInterface);
        liveGuidesPipeline = Make("texel_live_guides", liveGuidesInterface);
        CreateFarPipeline(shaderDir);
        CreateTransportCensusPipeline(shaderDir);
        CreatePatchDirectPipeline(shaderDir);
        CreateFarRelightPipeline(shaderDir);
        CreateSunProvisionalPipeline(shaderDir);
        sunNodePrepPipeline = Make("texel_nodes_prep", sunNodePrepInterface);
        sunNodePatchPipeline = Make("texel_nodes_patch", sunNodePatchInterface);
        sunNodeRaysPipeline = Make("texel_nodes_rays", sunNodeRaysInterface);
        texelClearPipeline = Make("texel_clear", texelClearInterface);
        sunNodeShadePipeline = Make("texel_nodes_shade", sunNodeShadeInterface);
        sunNodeShadeSplitPipeline = Make("texel_nodes_shade_split", sunNodeShadeSplitInterface);
    }

    // RecordTexels, each frame of a --sun-nodes run.
    private void RecordSunNodes()
    {
        switch (sunNodeStage)
        {
            case SunNodeStage.Waiting: BeginSunNodes(); break;
            case SunNodeStage.Relighting:
                if (liveTexels) RecordLiveSunBits();
                if (liveRetargeting) { FinishLiveRetarget(); break; }
                // The patches' light: solved on the GPU (SponzaLoop.SunNodesSolve.cs), or the CPU's task.
                var solved = sunNodeRelight is null ? StepGpuSolve() : sunNodeRelight.IsCompleted;
                if (solved && SunNodeTraceReady) UploadSunNodes();
                break;
            case SunNodeStage.Prep: RecordSunNodePrep(); break;
            case SunNodeStage.PatchPick: RecordSunNodePatchPick(); break;
            case SunNodeStage.Discover: RecordSunNodeDiscover(); break;
            case SunNodeStage.Merge: MergeDiscovered(); break;
            case SunNodeStage.Tracing: RecordSunNodeSlice(); break;
        }
    }

    // The trace waits for the texels' sun bits (SunNodeBitsFor) as well as the patches' solve.
    // The foliage's sun points, once a session (SunNodeBitsFor): the geometry's, so a live restart reuses them.
    private Vector4[]? sunNodeFoliageKept;
    private static Vector4[]? LoadFoliagePoints(string? key) => key is null ? null : LoadStage(key, "sun-points-foliage", r => ReadArray<Vector4>(r));

    private bool SunNodeTraceReady => texelLiveIds is not null && sunNodeBitsTask is { IsCompleted: true } && (!liveTexels || liveSunDone)
        && sunNodeGuideTask is null or { IsCompleted: true } && !SplitVisiblePending;

    // As soon as the texels, the patches and the tracer are up (the sunlets' sun rays go to Metal): each sun's direction,
    // the texels' sun bits toward them, and the patches' light, all the suns solved together -- both off the frame thread.
    private void BeginSunNodes()
    {
        // After the texels are up: a boot sun with no light cached joins the suns there (FoldBootSunIntoCook).
        if (!texelsUploaded || (!liveTexels && transportRelightMany is null) || cookedPacked is null || !EnsureFrameTracer() || relightTask is not null || lightBakeActive) return;
        // --live-texels: the patches' light is the live solve's, once it rests for these lights.
        if (liveTexels && !LivePatchLightRested) return;
        var suns = sunNodeSuns!;
        var north = BandNorth(out _);
        sunNodeDirs = new Vector4[suns.Length];
        var toSuns = new Vector3[suns.Length];
        for (var k = 0; k < suns.Length; k++)
        {
            // A sky column has no sun: straight up stands in (its bits are shot and never read).
            toSuns[k] = sunNodeDirsAsked is { } asked ? asked[k]
                : SunNodeKind(k) == 2 ? Vector3.UnitY : ToScene(SunLocal(BandLatitude, suns[k].Dec, suns[k].Hour), north);
            sunNodeDirs[k] = new Vector4(toSuns[k], SunNodeKind(k));
        }
        sunNodeBitsTask = SunNodeBitsFor(toSuns, texelsToo: !liveTexels);
        if (liveTexels)
        {
            BeginLiveSunBits(toSuns);
            // Its columns are the split light's (the sky, then the sun): the live buffer is read as a band's would be.
            sunNodePatchLight = livePatchLightBuffer;
            sunNodeRelight = null;
            sunNodeClock.Restart();
            sunNodeStage = SunNodeStage.Relighting;
            Console.WriteLine(string.Create(Inv, $"[VulkanSponza] sun nodes: the patches' light is the live solve's ({transportSolve!.Patches:N0} patches, at rest); no band solve."));
            return;
        }
        if (SunNodeGpuSolve)
        {
            sunNodeClock.Restart();
            // The cook's suns, each with the sky, warm-started from the boot answer (as the CPU's relight is).
            // Started from the boot answer only when every column is a sun with the sky (that answer is both together).
            var comps = sunNodeDirs.Select((d, k) => (new Vector3(d.X, d.Y, d.Z), (SunNodeKind(k) & 1) != 0, (SunNodeKind(k) & 2) != 0 ? 1f : 0f)).ToArray();
            BeginGpuSolve(comps, warm: comps.All(c => c.Item2 && c.Item3 > 0f));
            sunNodeRelight = null;
            sunNodeStage = SunNodeStage.Relighting;
            Console.WriteLine(string.Create(Inv, $"[VulkanSponza] sun nodes: solving {transportSolve!.Patches:N0} patches' light for {suns.Length} suns on the GPU."));
            return;
        }
        if (sunNodeKinds.Any(k => k != 3))
        {
            // The CPU's batched relight solves suns with the sky only: the parts apart need the GPU's components.
            Console.WriteLine("[VulkanSponza] sun nodes: sky or sun-alone columns need the GPU solve (frame rays on Metal, no --sun-nodes-cpu-solve); not cooked.");
            sunNodeStage = SunNodeStage.Done;
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
        var empty = new uint[sunNodeHashSlots * 2];   // id, fingerprint a slot (texel_nodes.glsl)
        Array.Fill(empty, 0xFFFFFFFFu);
        if (!sunNodeLive.Equals(default(GpuBufferHandle)) && !sunNodeLive.Equals(splitLiveBuffer)) RetireGpuBuffer(sunNodeLive);   // a cook before this one's
        // The split's upload of the same ids, when there is one (61 MB on Sponza, 2026-10-20).
        sunNodeLive = !splitLiveBuffer.Equals(default(GpuBufferHandle)) && ReferenceEquals(live, texelLiveIds) ? splitLiveBuffer
            : Own(device.CreateGpuBuffer(live.Length * 4, MemoryMarshal.AsBytes(live.AsSpan()), "sponza.sun-nodes.live"));
        // A live split cook traces what the image reads first (SponzaLoop.SplitLanding.cs); otherwise the live order.
        sunNodeTraceIds = sunNodeStride == 1 ? SplitTraceOrder(live) : live.Where((_, i) => i % sunNodeStride == 0).ToArray();
        // --live-texels: the coarse levels, scene-wide (LiveCoarseOrder).
        if (liveTexels) sunNodeTraceIds = LiveCoarseOrder();
        // The far field pools a slice's texels with their neighbours: neighbours trace together (SponzaLoop.FarField.cs).
        else if (farField && splitCooking && sunNodeStride == 1)
            sunNodeTraceIds = FarSpatialOrder(sunNodeTraceIds, ReferenceEquals(sunNodeTraceIds, live) ? 0 : splitVisibleCount);
        sunNodeTraceBuffer = ReferenceEquals(sunNodeTraceIds, live) ? sunNodeLive
            : Own(device.CreateGpuBuffer(Math.Max(16, sunNodeTraceIds.Length * 4), MemoryMarshal.AsBytes(sunNodeTraceIds.AsSpan()), "sponza.sun-nodes.traced"));
        sunNodeSunsBuffer = Own(device.CreateGpuBuffer(sunNodeDirs.Length * 16, MemoryMarshal.AsBytes(sunNodeDirs.AsSpan()), "sponza.sun-nodes.suns"));
        var (bits, points, pointBits) = sunNodeBitsTask!.Result;
        sunNodeBitsTask = null;
        sunNodeBits = liveTexels ? liveSunBits : Own(device.CreateGpuBuffer(checked(bits.Length * 4), MemoryMarshal.AsBytes(bits.AsSpan()), "sponza.sun-nodes.bits"));
        UploadSunPoints(points, pointBits);
        sunNodeDiscoverList = Own(device.CreateGpuBuffer(16 + DiscoverRoom * 32, new byte[16 + DiscoverRoom * 32], "sponza.sun-nodes.discover"));
        sunNodeHash = Own(device.CreateGpuBuffer(checked((int)((long)sunNodeHashSlots * 8)), MemoryMarshal.AsBytes(empty.AsSpan()), "sponza.sun-nodes.hash"));
        if (light is not null)
            sunNodePatchLight = Own(device.CreateGpuBuffer(checked((int)((long)light.Length * 4)), MemoryMarshal.AsBytes(light.AsSpan()), "sponza.sun-nodes.patch-light"));
        var patchLightBytes = (long)cookedPacked!.Length / 3 * suns * 8;
        var rays = sunNodeSlice * 32;
        sunNodeRaysBuffer = Own(device.CreateGpuBuffer(checked((int)((long)rays * 32)), name: "sponza.sun-nodes.rays"));
        sunNodeHits = Own(device.CreateGpuBuffer(checked((int)((long)rays * 48 + 80)), name: "sponza.sun-nodes.hits"));
        sunNodeRayWeights = Own(device.CreateGpuBuffer(rays * 4, name: "sponza.sun-nodes.ray-weights"));
        sunNodeGuidePatch = Own(device.CreateGpuBuffer(texelCount * 4, name: "sponza.sun-nodes.guide-patch"));   // zeros: not yet
        var noPatch = new int[texelCount];
        Array.Fill(noPatch, -1);
        sunNodeTexelPatch = Own(device.CreateGpuBuffer(texelCount * 4, MemoryMarshal.AsBytes(noPatch.AsSpan()), "sponza.sun-nodes.texel-patch"));
        sunNodePatchCandidates = Own(device.CreateGpuBuffer(rays * 4, name: "sponza.sun-nodes.patch-candidates"));
        var guides = sunNodeGuideTask?.Result;
        sunNodeGuideTask = null;
        sunNodeGuided = guides is not null;
        guides ??= new float[64];
        sunNodeGuideBuffer = Own(device.CreateGpuBuffer(guides.Length * 4, MemoryMarshal.AsBytes(guides.AsSpan()), "sponza.sun-nodes.guides"));
        // --live-texels: the live solve's guides (texel_live_guides.comp), rebuilt each time its light rests.
        if (liveTexels && sunNodeGuide && !liveGuides.Equals(default(GpuBufferHandle)))
        {
            RetireGpuBuffer(sunNodeGuideBuffer);
            sunNodeGuideBuffer = liveGuides;
            sunNodeGuided = true;
        }
        var lightLists = sunNodeGuided ? sunNodeLightLists : null;
        sunNodeLightLists = null;
        sunNodeLit = lightLists is not null;
        lightLists ??= new float[2];
        sunNodeLightBuffer = Own(device.CreateGpuBuffer(lightLists.Length * 4, MemoryMarshal.AsBytes(lightLists.AsSpan()), "sponza.sun-nodes.lights"));
        // --live-texels: the live solve's light lists (texel_live_guides.comp), with its guides.
        if (liveTexels && sunNodeLights && !liveLights.Equals(default(GpuBufferHandle)))
        {
            RetireGpuBuffer(sunNodeLightBuffer);
            sunNodeLightBuffer = liveLights;
            sunNodeLit = true;
            lightLists = new float[transportSolve!.Patches * LightListSize * 2];   // the log's count
        }
        Console.WriteLine(string.Create(Inv, $"[VulkanSponza] sun nodes: the light strategy {(sunNodeLit ? $"on ({lightLists.Length / 2 / LightListSize:N0} patches' lists)" : sunNodeLights ? "off: no light lists (no guides)" : liveTexels ? "off (live's default; --light-lists for them)" : "off (--sun-nodes-no-lights)")}."));
        if (sunNodeExact)
        {
            sunNodeExactOrigins = Own(device.CreateGpuBuffer(rays * 16, name: "sponza.sun-nodes.exact-origins"));
            sunNodeExactOccluded = Own(device.CreateGpuBuffer(rays * 4, name: "sponza.sun-nodes.exact-occluded"));
        }
        sunNodeAccum = Own(device.CreateGpuBuffer(checked((int)((long)sunNodeSlice * suns * 16)), name: "sponza.sun-nodes.accum"));
        if (nearGlowSplit)
        {
            sunNodeGlow = Own(device.CreateGpuBuffer(checked(sunNodeSlice * 16), name: "sponza.sun-nodes.glow"));
            if (texelGlowPart.Equals(default(GpuBufferHandle)))
                texelGlowPart = Own(device.CreateGpuBuffer(Math.Max(16, texelCount * 8), new byte[Math.Max(16, texelCount * 8)], "sponza.texels.glow-part"));
        }
        if (liveTexels)
        {
            // --live-texels: the sums in mapped memory, a few slices' worth, read back without a wait (LiveSliceReadbacks).
            sunNodeAccums = Enumerable.Range(0, LiveAccumSlots)
                .Select(k => Own(device.CreateReadableGpuBuffer(checked((int)((long)sunNodeSlice * suns * 16)), $"sponza.sun-nodes.accum{k}"))).ToArray();
            RetireGpuBuffer(sunNodeAccum);
            sunNodeAccumSlot = 0;
            sunNodeAccum = sunNodeAccums[0];
            liveReadbacks.Clear();
        }
        sunNodeStats = Own(device.CreateGpuBuffer(64, new byte[64], "sponza.sun-nodes.stats"));
        var keep = Math.Min(sunNodeKeep, suns);
        if (splitCooking) BeginSplitDetail(suns, keep);
        BeginFarField(suns);
        BeginTransportCensus();
        BeginNoiseCensus();
        sunNodeResults = new uint[keep][];
        for (var k = 0; k < keep; k++) sunNodeResults[k] = new uint[texelCount * 2];
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sun nodes: patches solved in {sunNodeRelightSeconds:0.0} s; up: bits {(long)texelCount * words * 4 / 1048576.0:0} MB, hash {sunNodeHashSlots * 8L / 1048576.0:0} MB, patch light {patchLightBytes / 1048576.0:0} MB, rays and hits {rays * 80L / 1048576.0:0} MB, sums {(long)sunNodeSlice * suns * 16 / 1048576.0:0} MB; keeping {keep} suns' light ({keep * texelCount * 8L / 1048576.0:0} MB)."));
        if (rayLodError > 0f)
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] sun nodes: WARNING the frame's ray scene is simplified ({rayLodError} m): a texel behind its mesh's coarse surface gathers from inside it (Sponza: 10% of the light off). Cook with --ray-lod-error 0."));
        sunNodeStage = SunNodeStage.Prep;
    }

    // The cook's hash holds one texel a plane within a key (texel_nodes_prep.comp), in steps of the search's plane reach:
    // one a key hid the teacup's floor under its saucer's bottom, 1.9 cm above it facing the same way (2026-10-23).
    // --no-node-planes: one a key, as before.
    private bool nodePlanes = true;
    // Each live texel's room along its normal (texel_clear.comp), traced once before the gathers: their rays' lift half of
    // it at most (texel_nodes_rays.comp). --no-gather-clear: 1 cm, as before.
    private bool gatherClear = true;
    private ShaderInterface texelClearInterface = null!;
    private PipelineHandle texelClearPipeline;
    private GpuBufferHandle texelClearBuffer;
    private bool GatherClearOn => gatherClear && !texelClearBuffer.Equals(default(GpuBufferHandle));
    private GpuBufferHandle patchClearBuffer;
    private bool PatchClearOn => gatherClear && !patchClearBuffer.Equals(default(GpuBufferHandle));

    // Each cooked patch's room along its normal (texel_clear.comp modes 2-3), once the patches are up: the pick's patch end.
    private void BuildPatchClear()
    {
        if (!gatherClear || !patchClearBuffer.Equals(default(GpuBufferHandle)) || cookedPacked is not { } packed || !EnsureFrameTracer() || frameTracer is null) return;
        var patches = packed.Length / 3;
        if (patches == 0) return;
        var fill = new float[patches];
        Array.Fill(fill, TexelClearReach);
        patchClearBuffer = Own(device.CreateGpuBuffer(patches * 4, MemoryMarshal.AsBytes(fill.AsSpan()), "sponza.patches.clear"));
        const int chunk = 1 << 20;
        var raysBuffer = device.CreateGpuBuffer(Math.Min(chunk, patches) * 32, name: "sponza.patches.clear-rays");
        var hitsBuffer = device.CreateGpuBuffer(Math.Min(chunk, patches) * 48 + 80, name: "sponza.patches.clear-hits");
        var cooked = CookedBuffers().First(b => b.Name == "CookedPatches");
        DispatchCommand Clear(int first, int count, int mode) => new(texelClearPipeline, (count + 63) / 64, 1, 1,
            new ShaderUniform[] { new("uClear", new Vector4Uniform(new Vector4(first, count, TexelClearReach, mode))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("Texels", patchClearBuffer),
                new ShaderBufferBinding("SunNodesLive", patchClearBuffer),
                new ShaderBufferBinding("NodeRays", raysBuffer),
                new ShaderBufferBinding("NodeHits", hitsBuffer),
                new ShaderBufferBinding("TexelClear", patchClearBuffer),
                cooked,
                new ShaderBufferBinding("PatchClear", patchClearBuffer),
            });
        for (var first = 0; first < patches; first += chunk)
        {
            var count = Math.Min(chunk, patches - first);
            device.DispatchNow(new[] { Clear(first, count, 2) }, "patch-clear-rays");
            frameTracer.Trace(raysBuffer, hitsBuffer, count, 0xC1EA7u + (uint)first, closestOnly: true);
            device.DispatchNow(new[] { Clear(first, count, 3) }, "patch-clear");
        }
        device.WaitIdle();
        device.DestroyGpuBuffer(raysBuffer);
        device.DestroyGpuBuffer(hitsBuffer);
    }
    private string GatherLiftKey => gatherClear ? ";lift clear" : "";
    private const float TexelClearReach = 0.02f;

    private void BuildTexelClear()
    {
        if (!gatherClear || !texelClearBuffer.Equals(default(GpuBufferHandle)) || !EnsureFrameTracer() || frameTracer is null || texelLiveIds is not { } live) return;
        var clock = Stopwatch.StartNew();
        var fill = new float[Math.Max(1, texelCount)];
        Array.Fill(fill, TexelClearReach);
        texelClearBuffer = Own(device.CreateGpuBuffer(fill.Length * 4, MemoryMarshal.AsBytes(fill.AsSpan()), "sponza.texels.clear"));
        // Its own rays, hits and ids: it runs before the sun bits (RecordLiveSunBits), ahead of the sun nodes' buffers.
        const int chunk = 1 << 20;
        var idsBuffer = device.CreateGpuBuffer(Math.Max(16, live.Length * 4), MemoryMarshal.AsBytes(live.AsSpan()), "sponza.texels.clear-ids");
        var raysBuffer = device.CreateGpuBuffer(chunk * 32, name: "sponza.texels.clear-rays");
        var hitsBuffer = device.CreateGpuBuffer(chunk * 48 + 80, name: "sponza.texels.clear-hits");
        DispatchCommand Clear(int first, int count, int mode) => new(texelClearPipeline, (count + 63) / 64, 1, 1,
            new ShaderUniform[] { new("uClear", new Vector4Uniform(new Vector4(first, count, TexelClearReach, mode))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("SunNodesLive", idsBuffer),
                new ShaderBufferBinding("NodeRays", raysBuffer),
                new ShaderBufferBinding("NodeHits", hitsBuffer),
                new ShaderBufferBinding("TexelClear", texelClearBuffer),
                new ShaderBufferBinding("CookedPatches", texelClearBuffer),
                new ShaderBufferBinding("PatchClear", texelClearBuffer),
            });
        for (var first = 0; first < live.Length; first += chunk)
        {
            var count = Math.Min(chunk, live.Length - first);
            device.DispatchNow(new[] { Clear(first, count, 0) }, "texel-clear-rays");
            frameTracer.Trace(raysBuffer, hitsBuffer, count, 0xC1EA5u + (uint)first, closestOnly: true);
            device.DispatchNow(new[] { Clear(first, count, 1) }, "texel-clear");
        }
        device.WaitIdle();
        device.DestroyGpuBuffer(idsBuffer);
        device.DestroyGpuBuffer(raysBuffer);
        device.DestroyGpuBuffer(hitsBuffer);
        Console.WriteLine(string.Create(Inv, $"[VulkanSponza] texels: each one's room along its normal, {live.Length:N0} rays to {TexelClearReach * 100:0} cm, in {clock.Elapsed.TotalMilliseconds:0} ms (the gather's lift half of it at most)."));
    }
    // A quarter of the plane reach (~3 mm): surfaces nearer than the reach apart keep an entry each -- the reach's own step
    // merged a 4 mm wall's two faces turned the same way (the sheet shell, 2026-10-24).
    private float NodePlaneStep => nodePlanes ? 0.25f * sunNodePlane * texelGrid.W : 0f;

    private void RecordSunNodePrep()
    {
        BuildTexelClear();
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
                    new("uPrep", new Vector4Uniform(new Vector4(first, NodePlaneStep, 0f, sunNodeHashSlots))),
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
        sunNodePatchFirst = 0;
        sunNodeStage = SunNodeStage.PatchPick;
    }

    // A pass of the slice a frame: its rays, the CLOSEST batch, every hit shaded for every sun. When the slice has all
    // its passes, the next frame reads its sums back (the GPU idle first) and moves on.
    private void RecordSunNodeSlice()
    {
        // Live: the sun points' bits first (shot in the frame after the merge, RecordLivePointBits).
        if (liveTexels && !LivePointsDone) { RecordLivePointBits(); return; }
        // The texels' and the sun points' bits are shot in the frame: recorded is not done -- with frames in flight, the
        // cells' sun pass and the gather's first passes (outside the frame, at once) read them before the last frames ran.
        // Crevices' seams, where the sun points answer, came out 0.78-0.83 of the reference instead of 0.87 as the timing
        // fell (2026-10-19). Once a sun, everything in flight finished first.
        if (liveTexels && liveBitsSyncedFor != liveSunToward)
        {
            device.WaitIdle();
            liveBitsSyncedFor = liveSunToward;
        }
        // Its own frame (~0.8 s on Sponza), ahead of the far relight's and the instant layer's.
        if (HitCellsSunNow()) return;
        var refining = sunNodeRefineRound > 0;
        var live = refining ? sunNodeRefineIds : sunNodeTraceIds;
        var suns = sunNodeSuns!.Length;
        var sweepPasses = sunNodeRays / 32;
        var passes = refining ? RefinePasses : sweepPasses;
        if (SunNodesOnHost)
        {
            RunSunNodePassesNow();
            return;
        }
        if (sunNodeReadback && !SunNodeReadBack()) return;
        if (liveTexels && !LiveSliceReadbacks(live, refining, passes)) return;
        if (farRecording)
        {
            FarRecordStep(now: false);
            return;
        }
        if (sunNodeRefineRound == 0) FarRelightNow();
        if (sunNodeRefineRound == 0) SunProvisionalNow();
        var slice = Math.Min(sunNodeSlice, live.Length - sunNodeFirst);
        // A round's rays carry on each texel's pass numbering (its seeds): every round after the sweep gives RefinePasses.
        var seedPass = refining ? sweepPasses + (sunNodeRefineRound - 1) * RefinePasses + sunNodePass : sunNodePass;
        DispatchSunNodePass(refining ? sunNodeRefineBuffer : sunNodeTraceBuffer, sunNodeFirst, slice, sunNodePass, discover: 0, seedPass);
        if (++sunNodePass == passes)
        {
            sunNodePass = 0;
            FarFinish(now: false, refining ? sunNodeRefineBuffer : sunNodeTraceBuffer, sunNodeFirst, slice, passes);
            TransportCensusFinish(now: false, slice, passes);
            if (liveTexels && sunNodeAccums.Length > 0)
            {
                // Read back once its frame is past the frames in flight (LiveSliceReadbacks): the next slice traces now.
                liveReadbacks.Enqueue((sunNodeAccumSlot, sunNodeFirst, slice, framesRendered));
                sunNodeFirst += slice;
                sunNodeAccumSlot = (sunNodeAccumSlot + 1) % sunNodeAccums.Length;
                sunNodeAccum = sunNodeAccums[sunNodeAccumSlot];
            }
            else sunNodeReadback = true;
        }
    }

    // A slice with all its passes: its sums read back (the GPU idle first), into the results (and landed), and on to the
    // next slice. False when the sweep or a round ended here (a new round starts next time, or the cook finished).
    // --live-texels: the slices' sums read where they lie once their frames are done -- no WaitIdle, no copy: each slice
    // drained the 3 frames in flight and waited on a copy before (a frame of 100-250 ms a slice, 2026-10-16).
    // BLIX_SYNC_ALL=1 (a diagnosis): the GPU idle around every gather pass.
    private static readonly bool syncAll = Environment.GetEnvironmentVariable("BLIX_SYNC_ALL") == "1";
    // The sun whose in-frame bits were waited on (RecordSunNodeSlice).
    private Vector3 liveBitsSyncedFor = new(float.NaN);
    private const int LiveAccumSlots = 4;
    // The read-backs' time by part (FinishSunNodes reports it): the mapped read, sums and refine, detail, census, landing.
    private readonly double[] readParts = new double[5];
    private int readSlices;
    private GpuBufferHandle[] sunNodeAccums = Array.Empty<GpuBufferHandle>();
    private int sunNodeAccumSlot;
    private readonly Queue<(int Slot, int First, int Count, long Frame)> liveReadbacks = new();

    // Each frame before a slice's pass: the queued slices whose frames are done, processed; false while the next pass
    // must wait -- its sums slot is still queued, or the sweep's (a round's) slices are all traced and some not read yet.
    private bool LiveSliceReadbacks(uint[] live, bool refining, int passes)
    {
        var inFlight = Math.Max(1, device.MaxFramesInFlightCount);
        var suns = sunNodeSuns!.Length;
        while (liveReadbacks.TryPeek(out var r) && r.Frame + inFlight <= framesRendered)
        {
            liveReadbacks.Dequeue();
            var clock = Stopwatch.StartNew();
            var bytes = new byte[r.Count * suns * 16];
            device.ReadMappedGpuBuffer(sunNodeAccums[r.Slot], 0, bytes);
            readParts[0] += clock.Elapsed.TotalSeconds;
            ProcessSlice(MemoryMarshal.Cast<byte, Vector4>(bytes.AsSpan()).ToArray(), live, r.First, r.Count, refining, passes);
            sunNodeReadSeconds += clock.Elapsed.TotalSeconds;
        }
        if (sunNodeFirst >= live.Length)
        {
            if (liveReadbacks.Count > 0) return false;
            // Every slice in: the progress line and the sweep's (the round's) end, as a read back slice's last.
            SliceAdvanced(live, 0, refining);
            return false;
        }
        foreach (var r in liveReadbacks) if (r.Slot == sunNodeAccumSlot) return false;
        return true;
    }

    private bool SunNodeReadBack()
    {
        var refining = sunNodeRefineRound > 0;
        var live = refining ? sunNodeRefineIds : sunNodeTraceIds;
        var suns = sunNodeSuns!.Length;
        var sweepPasses = sunNodeRays / 32;
        var passes = refining ? RefinePasses : sweepPasses;
        var clock = Stopwatch.StartNew();
        var count = Math.Min(sunNodeSlice, live.Length - sunNodeFirst);
        Vector4[] sums;
        if (liveTexels && SunNodesOnHost && Array.IndexOf(sunNodeAccums, sunNodeAccum) >= 0)
        {
            // On the host the slice's passes are done when DispatchNow returns: its sums read where they lie (mapped),
            // with no WaitIdle behind the frames in flight and no copy -- ~220 ms a slice before (2026-10-16).
            var bytes = new byte[count * suns * 16];
            device.ReadMappedGpuBuffer(sunNodeAccum, 0, bytes);
            sums = MemoryMarshal.Cast<byte, Vector4>(bytes.AsSpan()).ToArray();
            readParts[0] += clock.Elapsed.TotalSeconds;
        }
        else
        {
            device.WaitIdle();
            sums = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(sunNodeAccum, 0, count * suns * 16).AsSpan()).ToArray();
        }
        ProcessSlice(sums, live, sunNodeFirst, count, refining, passes);
        sunNodeReadSeconds += clock.Elapsed.TotalSeconds;
        sunNodeReadback = false;
        sunNodeFirst += count;
        return SliceAdvanced(live, count, refining);
    }

    // A slice's sums into the results, the parts and the censuses (SunNodeReadBack, or --live-texels' readback queue).
    private void ProcessSlice(Vector4[] sums, uint[] live, int first, int count, bool refining, int passes)
    {
        var suns = sunNodeSuns!.Length;
        var results = sunNodeResults;
        if (sunNodeBootPath is not null && !refining)
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
        var partClock = Stopwatch.StartNew();
        if (SunNodeRefines) RefineSlice(sums, first, count, suns, passes);
        else
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
        readParts[1] += partClock.Elapsed.TotalSeconds; partClock.Restart();
        if (!refining && splitCooking) ReadSplitDetail(sums, live, first, count, suns, passes);
        readParts[2] += partClock.Elapsed.TotalSeconds; partClock.Restart();
        if (!refining && splitCooking) ReadNoiseCensus(sums, count, suns, passes);
        readParts[3] += partClock.Elapsed.TotalSeconds; partClock.Restart();
        LandSplitSlice(live, first, count);
        readParts[4] += partClock.Elapsed.TotalSeconds;
        readSlices++;
    }

    // The progress line, and the sweep's or round's end once every slice is in: true while there are more to trace.
    private bool SliceAdvanced(uint[] live, int count, bool refining)
    {
        var done = (double)sunNodeFirst / live.Length;
        if ((int)(done * 20) != (int)((double)(sunNodeFirst - count) / live.Length * 20))
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] sun nodes{(refining ? $", refining round {sunNodeRefineRound}" : "")}: {done * 100:0}% of {live.Length:N0} texels ({sunNodeClock.Elapsed.TotalMinutes:0.0} min)."));
        if (sunNodeFirst >= live.Length)
        {
            if (SunNodeRefines && BeginSunNodeRefineRound()) return false;
            FinishSunNodes();
            return false;
        }
        return true;
    }

    private void FinishSunNodes()
    {
        sunNodeStage = SunNodeStage.Done;
        var suns = sunNodeSuns!;
        var trace = sunNodeClock.Elapsed.TotalMinutes;
        var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(sunNodeStats, 0, 64).AsSpan()).ToArray();
        ReportFarPool("over the refining rounds");
        var stats = Enumerable.Range(0, 7).Select(k => (ulong)words[2 * k] | ((ulong)words[2 * k + 1] << 32)).ToArray();
        var hits = Math.Max(stats[0], 1ul);
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sun nodes: traced in {trace:0.0} min ({(long)sunNodeTraceIds.Length * sunNodeRays / 1e9:0.00} G rays{(sunNodeRefineRound > 0 ? $" and {sunNodeRefineRays / 1e9:0.00} G refining in {sunNodeRefineRound} rounds" : "")}, {sunNodeReadSeconds:0} s of it reading back) for {suns.Length} suns ({(sunNodeLit ? "a quarter of the rays by the band's guides, a quarter at the light lists, MIS" : sunNodeGuided ? "half the rays by the band's guides" : "cosine rays")}), after {sunNodeRelightSeconds:0.0} s of patch solves; hits {stats[0]:N0} ({100.0 * stats[4] / hits:0.00}% answered by the foliage's points, {100.0 * stats[1] / hits:0.00}% among neither: no sun; {100.0 * stats[2] / hits:0.00}% no patch), misses {stats[3]:N0}{(farOn ? $", far by the record {stats[5]:N0}" : "")}{(nearTexelLight ? $", near hits lit by their texels {stats[6]:N0} ({100.0 * stats[6] / hits:0.0}%)" : "")}."));
        if (sunNodeNowPasses > 0)
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] sun nodes, outside the frame: {sunNodeNowPasses:N0} passes; rays {sunNodeNowSeconds[0]:0} s, trace {sunNodeNowSeconds[1]:0} s, shade {sunNodeNowSeconds[2]:0} s ({sunNodeNowSeconds[2] * 1000 / sunNodeNowPasses:0.0} ms a pass of {sunNodeSlice * 32:N0} rays; {sunNodeLaterPasses:N0} passes not a frame's first, their rays {sunNodeLaterRays * 1000 / Math.Max(1, sunNodeLaterPasses):0.0} ms each); of the trace's {sunNodeClock.Elapsed.TotalSeconds:0.0} s, passes {sunNodeNowSeconds.Sum():0.0} s, read-backs {sunNodeReadSeconds:0.0} s, the rest (frames, landing) {sunNodeClock.Elapsed.TotalSeconds - sunNodeNowSeconds.Sum() - sunNodeReadSeconds:0.0} s; shade passes by the hit cells {hitCellPassesOn:N0}, by the search {hitCellPassesOff:N0}."));
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sun nodes, the read-backs' parts over {readSlices:N0} slices: mapped read {readParts[0]:0.0} s, sums and refine {readParts[1]:0.0} s, detail {readParts[2]:0.0} s, noise census {readParts[3]:0.0} s, landing {readParts[4]:0.0} s."));
        Array.Clear(readParts);
        if (liveTexels) LogGatherMemory("the gather ends");
        readSlices = 0;
        Array.Clear(sunNodeNowSeconds);
        sunNodeNowPasses = 0;
        ResetSunNodeRefine();
        sunNodeRefineRays = 0;
        if (spotWhy) SpotWhyCompare(fine: false);
        if (EndVisibleChanged()) return;
        // BLIX_NO_FINE=1 (an A/B): the fine texels keep the instant layer's light after the gather.
        if (fineGathering && nearRoundsLeft > 0 && fineRoundIds.Length > 0)
        {
            // --near-rounds: the same texels gathered again, their near hits now lit by the texels the last round gathered
            // (--near-texel-light): the lagged step tools/gi-model/crease_bounce.py needs (one within 2.3%, two 0.6%).
            // The first near round every fine texel; the later ones the sheets that let light through alone. A closed thin shell's
            // walls pass light to each other across their gap, and each round takes one more step of it (the teacup's far
            // sides 0.069 excess after one round, 0.046 after two); elsewhere the second round changed no pixel (Sponza,
            // 2026-10-22) and cost 0.6 min.
            var later = nearRounds - nearRoundsLeft > 0;
            nearRoundsLeft--;
            var nearIds = later && transmitRecords is { } tr ? Array.FindAll(fineRoundIds, id => (tr[id >> 5] & (1u << (int)(id & 31))) != 0) : fineRoundIds;
            Console.WriteLine(string.Create(Inv, $"[VulkanSponza] near round: {nearIds.Length:N0} of the {fineRoundIds.Length:N0} fine texels gathered again on the last round's light{(later ? " (the sheets that let light through)" : "")} ({nearRoundsLeft} more)."));
            sunNodeRays = fineGatherSavedRays;
            texelMaxRays = fineGatherSavedMaxRays;
            if (nearIds.Length > 0)
            {
                StartFineRound(nearIds);
                if (NearCutMode == 2) texelMaxRays = sunNodeRays;
                return;
            }
            nearRoundsLeft = 0;   // no sheet to take another step: the round ends below
        }
        if (fineGathering)
        {
            // The fine refine's round done: its texels landed as they came; the rays as they were.
            fineGathering = false;
            sunNodeRays = fineGatherSavedRays;
            texelMaxRays = fineGatherSavedMaxRays;
            RecallFinal();
            DumpParts("final");
        }
        else if (TransferOn && transferReady && !transferCheck && transferGatherIds.Length > 0 && Environment.GetEnvironmentVariable("BLIX_NO_FINE") != "1")
        {
            DumpParts("coarse");
            FilterCoarseNow();
            ApplyFineNow();
            DumpParts("copy");
            if (spotWhy) SpotWhyCompare(fine: true);
            // The fine refine: the flagged texels traced as the coarse were, a round of their own, the cook screen up.
            var picked = FineGatherIds();
            RecallCopy(picked);
            if (picked is { Length: > 0 } fineIds)
            {
                fineRoundIds = fineIds;
                nearRoundsLeft = nearRounds;
                BeginNearCut(fineIds);
                StartFineRound(fineIds);
                return;
            }
        }
        if (transferChecked) TransferCheckCompare();
        if (splitCooking)
        {
            if (listCook) SaveLists();
            FinishSplitCook();
            return;
        }
        // The grid's suns, counted before the boot sun's answer is handed over (RestOnBootSun forgets it was one of them).
        var compressed = SunNodeCompressed;
        RestOnBootSun();
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
        // The live solve's patch light is its own (SponzaLoop.LivePatchLight.cs): the cook only borrowed it.
        if (sunNodePatchLight.Equals(livePatchLightBuffer)) sunNodePatchLight = default;
        if (sunNodeBits.Equals(liveSunBits)) sunNodeBits = default;
        if (sunNodeGuideBuffer.Equals(liveGuides)) sunNodeGuideBuffer = default;
        if (sunNodeLightBuffer.Equals(liveLights)) sunNodeLightBuffer = default;
        if (sunNodeTexelPatch.Equals(liveTexelPatch)) sunNodeTexelPatch = default;
        foreach (var b in new[] { sunNodeSunsBuffer, sunNodeBits, sunNodeHash, sunNodePatchLight, sunNodeRaysBuffer, sunNodeHits, sunNodeAccum, sunNodeStats, sunNodeFoliagePoints, sunNodeFoliageHash, sunNodeFoliageBits, sunNodeDiscoverBuffer, sunNodeDiscoverList, sunNodeGuideBuffer, sunNodeRayWeights, sunNodeTexelPatch, sunNodePatchCandidates, sunNodeGuidePatch, sunNodeDetail, sunNodeLightBuffer, sunNodeCensus })
            if (!b.Equals(default(GpuBufferHandle))) RetireGpuBuffer(b);
        sunNodeDetail = default;
        sunNodeDetailWords = Array.Empty<uint[]>();
        sunNodeLightBuffer = default;
        sunNodeCensus = default;
        sunNodeSunsBuffer = sunNodeBits = sunNodeHash = sunNodePatchLight = sunNodeRaysBuffer = sunNodeHits = sunNodeAccum = sunNodeStats = default;
        sunNodeFoliagePoints = sunNodeFoliageHash = sunNodeFoliageBits = sunNodeDiscoverBuffer = sunNodeDiscoverList = sunNodeGuideBuffer = sunNodeRayWeights = sunNodeTexelPatch = sunNodePatchCandidates = sunNodeGuidePatch = default;
        sunNodePoints = Array.Empty<Vector4>();
        sunNodePointBits = Array.Empty<uint>();
        ReleaseFarField();
    }

    // UploadTexels with no light cached for the boot sun: a cook about to trace every texel's gather anyway answers it --
    // one more sun shaded from the same rays, where a light bake is a gather of its own (Sponza: 20 minutes). Not when
    // the boot sun is outside the band (the sun records answer only inside it) or no cook will run.
    private bool FoldBootSunIntoCook(string bakePath)
    {
        if (sunNodeStage != SunNodeStage.Waiting || sunNodeSuns is null || lightBakeKey is null) return false;
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
    // texelsToo false (--live-texels): the foliage's points and bits alone; the texels' bits are shot in the frame
    // (SponzaLoop.LivePatchLight.cs, RecordLiveSunBits).
    private Task<(uint[] Texel, Vector4[] Points, uint[] PointBits)> SunNodeBitsFor(Vector3[] toSuns, bool texelsToo = true)
    {
        var tracer = frameTracer!;
        var live = texelsToo ? texelLiveIds! : Array.Empty<uint>();
        var count = texelsToo ? texelCount : 0;
        var words = (toSuns.Length + 31) / 32;
        device.WaitIdle();
        var texels = texelsToo ? ReadTexelRecords(0, count) : Array.Empty<Vector4>();
        var reach = rayReach;
        var directions = toSuns.Select(d => new Vector4(d, 0f)).ToArray();
        sunNodeBitDirections = directions;
        sunNodeReach = reach;
        var lift = sunNodeLift;
        // The foliage's sun points (SunNodesFoliage.cs) from the frame's ray scene and its baked coverage.
        var rayData = rayGpuData;
        var surfaceWords = rayData is null ? Array.Empty<uint>()
            : MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(raySurfaces, 0, rayData.RowPlacements.Length * 4).AsSpan()).ToArray();
        var grid = texelGrid;
        var foliageKey = lightBakeKey;
        var hostPointBits = !liveTexels;   // live: shot in the frame once the points are all up (RecordLivePointBits)
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
            if (texelsToo) Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] sun nodes: sun bits for {live.Length:N0} texels x {toSuns.Length} suns ({(long)live.Length * toSuns.Length / 1e9:0.00} G rays) in {clock.Elapsed.TotalSeconds:0.0} s ({gpuMs / 1000:0.0} s GPU)."));
            var foliage = Array.Empty<Vector4>();
            var foliageBits = Array.Empty<uint>();
            if (rayData is not null)
            {
                var fclock = Stopwatch.StartNew();
                // The points are the geometry's: built once a session, and a stage of the GI bundle.
                foliage = sunNodeFoliageKept ?? LoadFoliagePoints(foliageKey) ?? BuildFoliagePoints(rayData, surfaceWords, grid);
                if (sunNodeFoliageKept is null && foliageKey is not null && !File.Exists(TransportStagePath(foliageKey, "sun-points-foliage")))
                {
                    var save = foliage;
                    SaveStage(foliageKey, "sun-points-foliage", w => WriteArray(w, save));
                }
                sunNodeFoliageKept = foliage;
                double fgpu = 0;
                foliageBits = hostPointBits ? ShootSunBits(tracer, foliage, directions, words, reach, lift, out fgpu) : new uint[foliage.Length / 2 * words];
                Console.WriteLine(string.Create(Inv,
                    $"[VulkanSponza] sun nodes: the foliage's sun points: {foliage.Length / 2:N0} on the partly covered triangles, their bits in {fclock.Elapsed.TotalSeconds:0.0} s ({fgpu / 1000:0.0} s GPU)."));
            }
            return (bits, foliage, foliageBits);
        });
    }

    // One pass of a slice: its rays (from `ids`, the texels traced), the CLOSEST batch, every hit shaded for every sun --
    // or, discovering (`discover`: the list's room), the hits among neither texel nor sun point listed (NodeDiscover).
    // pass: the slice's pass (0: the sums start); seedPass, the texel's pass its rays are seeded by (-1: pass).
    private void DispatchSunNodePass(GpuBufferHandle ids, int first, int slice, int pass, int discover, int seedPass = -1)
    {
        if (seedPass < 0) seedPass = pass;
        var copy = sunNodeCopy;
        graph.Dispatch(sunNodeRaysPasses[copy], SunNodeRaysCommand(ids, first, slice, seedPass, discover));
        var seed = SunNodeSeed(first, seedPass, discover);
        // The frame's tracer: cook with --ray-lod-error 0, so it is the LOD 0 the texels sit on (UploadSunNodes warns). The
        // bake scene's tracer read the light 28% dark on Cornell, the same 120 triangles (its hits' albedo; open).
        RecordClosestBatch(sunNodeClosestBatches[copy], sunNodeRaysBuffer, sunNodeHits, slice * 32, seed);
        var exact = sunNodeExact && discover == 0;
        if (exact)
        {
            graph.Dispatch(sunNodeExactOriginsPasses[copy], SunNodeShadeCommand(ids, first, slice, pass, discover, 1f));
            RecordShadowBatch(sunNodeExactBatches[copy], sunNodeExactOrigins, sunNodeExactOccluded, slice * 32,
                new Vector3(sunNodeDirs[0].X, sunNodeDirs[0].Y, sunNodeDirs[0].Z), seed ^ 0x5bd1e995u);
        }
        graph.Dispatch(sunNodeShadePasses[copy], SunNodeShadeCommand(ids, first, slice, pass, discover, exact ? 2f : 0f));
    }

    // The column whose sun the gather's hits trace (--hit-sun-ray): the one sun of a live cook's columns; -1 off, or with
    // several suns (a cooked set of hours), where one shadow ray a hit answers only one of them.
    private int HitSunColumn => hitSunRay && (rtSheetRows == 0 || sheetLight) && sunNodeKinds.Count(k => (k & 1) != 0) == 1 && sunNodeBitDirections.Length == sunNodeKinds.Length
        ? Array.FindIndex(sunNodeKinds, k => (k & 1) != 0) : -1;

    private static uint SunNodeSeed(int first, int seedPass, int discover) =>
        unchecked((uint)(first * 2654435761L) ^ (uint)(seedPass + gatherSeedOffset) * 0x9E3779B9u ^ (discover > 0 ? 0xD15C0u : 0u));
    // BLIX_GATHER_SEED=N (a measurement): the gather's rays seeded N passes on -- two cooks' difference is their noise.
    // BLIX_RAY_CAP=metres (a measurement, the runtime gather's model): every gather ray ends there (past it a miss) -- what
    // short rays cost the trace against whole ones.
    private static readonly float rayCapMeasure = float.TryParse(Environment.GetEnvironmentVariable("BLIX_RAY_CAP"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rc) ? rc : 0f;
    private static readonly int gatherSeedOffset = int.TryParse(Environment.GetEnvironmentVariable("BLIX_GATHER_SEED"), out var gs) ? gs : 0;

    // On hardware rays the gather's passes run outside the frame graph (IGraphicsDevice.DispatchNow): its rays, the Metal
    // trace on the same buffers, the shade -- a pass a few tens of milliseconds, as many as a frame's budget holds, with
    // no hand-off between Vulkan and Metal inside a frame. In the frame one pass took a frame: Sponza's sweep was ~7,500
    // frames, ~24 M rays/s against Metal's ~150 M (2026-10-14). Not for --sun-nodes-exact-sun (its shadow batch).
    private bool SunNodesOnHost => !sunNodeOnFrame && FrameRaysOnHardware && !sunNodeExact && EnsureFrameTracer() && frameTracer is not null;
    // --split-on-frame: the gather's passes in the frame graph, as before (the A/B).
    private bool sunNodeOnFrame;
    // How long a frame gives the passes: under the cook screen most of it; with the scene up, a share of the frame.
    // The live budget counts the passes' own work, not the first pass's wait behind the frame's rendering on the
    // queue: counted, the wait (~100 ms) spent a 50 ms budget, one pass a frame -- Sponza's refine took 13.5 s for
    // ~1 s of rays (the rays kernel 1 ms, the frame's first ~100 ms, 2026-10-16). BLIX_LIVE_MS overrides it.
    private const double SunNodeCookScreenMs = 200;
    // --gather-steps-a-frame N: the gather's loop takes N steps a frame (a pass, a read-back, a record step), not as many as
    // its time budget holds -- the cook's schedule then the frame count's alone, run to run (a determinism probe).
    private int gatherStepsAFrame;
    private static readonly double SunNodeLiveMs = double.TryParse(Environment.GetEnvironmentVariable("BLIX_LIVE_MS"), out var ms) ? ms : 80;

    // texel_nodes_shade_split.comp: the same shade for up to 32 suns, at ~2.5x the workgroups resident (outside the frame).
    private ShaderInterface sunNodeShadeSplitInterface = null!;
    private PipelineHandle sunNodeShadeSplitPipeline;

    // The first dispatch's wait behind the frame's own work this frame (RunSunNodePassesNow leaves it out of its budget).
    private double sunNodeQueueWaitMs;

    private void SunNodePassNow(GpuBufferHandle ids, int first, int slice, int pass, int seedPass)
    {
        var t0 = Stopwatch.GetTimestamp();
        device.DispatchNow(new[] { SunNodeRaysCommand(ids, first, slice, seedPass) }, "sun-nodes-rays");
        var t1 = Stopwatch.GetTimestamp();
        var cells = HitCellsShade(0);
        var hitSun = HitSunColumn;
        if (!hitSunSaid)
        {
            hitSunSaid = true;
            Console.WriteLine(hitSun >= 0
                ? $"[VulkanSponza] gather: each hit's sun by its own shadow ray, toward {(hitSunDisc ? "a point of the sun's disc" : "the sun's centre")} (--hit-sun)."
                : $"[VulkanSponza] gather: each hit's sun from its cell's blend of the texels' sun bits ({(!hitSunRay ? "--hit-sun cells" : rtSheetRows > 0 ? $"{rtSheetRows:N0} rows of sheets that transmit: the hit's ray does not go through them yet" : "several suns")}).");
        }
        if (hitSun >= 0)
            frameTracer!.Trace(sunNodeRaysBuffer, sunNodeHits, slice * 32, SunNodeSeed(first, seedPass, 0), closestOnly: true,
                sun: sunNodeBitDirections[hitSun] with { W = gatherClear ? -sunNodeLift : sunNodeLift }, reach: rayReach, discTan: hitSunDisc ? SunDiscTangent : 0f);
        else frameTracer!.Trace(sunNodeRaysBuffer, sunNodeHits, slice * 32, SunNodeSeed(first, seedPass, 0), closestOnly: true);
        var t2 = Stopwatch.GetTimestamp();
        if (leanShade && cells) device.DispatchNow(new[] { LeanShadeCommand(ids, first, slice, pass, hitSun >= 0) }, "sun-nodes-lean");
        else device.DispatchNow(new[] { SunNodeShadeCommand(ids, first, slice, pass, 0, hitSun >= 0 ? 3f : 0f, few: sunNodeSuns!.Length <= 32,
            cells: cells, exactColumn: Math.Max(hitSun, 0)) }, "sun-nodes-shade");
        var t3 = Stopwatch.GetTimestamp();
        sunNodeNowSeconds[0] += Stopwatch.GetElapsedTime(t0, t1).TotalSeconds;
        if (sunNodeNowFrame == framesRendered) { sunNodeLaterRays += Stopwatch.GetElapsedTime(t0, t1).TotalSeconds; sunNodeLaterPasses++; }
        else sunNodeQueueWaitMs = Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
        sunNodeNowFrame = framesRendered;
        sunNodeNowSeconds[1] += Stopwatch.GetElapsedTime(t1, t2).TotalSeconds;
        sunNodeNowSeconds[2] += Stopwatch.GetElapsedTime(t2, t3).TotalSeconds;
        sunNodeNowPasses++;
        ListCookRead();
    }

    // The immediate passes' wall time by stage (rays, trace, shade), each including its wait: reported at the finish.
    private readonly double[] sunNodeNowSeconds = new double[3];
    // A trace's counters, from its start (MergeDiscovered, FinishLiveRetarget): they ran on across a session's cooks.
    private void ResetSunNodeCounters()
    {
        Array.Clear(sunNodeNowSeconds);
        sunNodeNowPasses = sunNodeLaterPasses = 0;
        sunNodeLaterRays = 0;
        sunNodeReadSeconds = 0;
    }

    private long sunNodeNowPasses, sunNodeLaterPasses, sunNodeNowFrame = -1;
    private double sunNodeLaterRays;

    // RecordSunNodeSlice on hardware: passes and read-backs until the frame's budget is spent or the stage moves on.
    private void RetireTraceBuffer()
    {
        if (!sunNodeTraceBuffer.Equals(sunNodeLive) && !sunNodeTraceBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(sunNodeTraceBuffer);
    }

    private void RunSunNodePassesNow()
    {
        using var profScope = Prof("gather.host");
        // The ray data's geometry goes as the gather starts, under the cook screen, rather than as the light lands:
        // everything that reads it (the tracer, the hit cells, the foliage points) is built by now, and its compacting
        // collection paused a lit frame ~4 s when it ran at the landing (2026-10-20). A no-op once released.
        ReleaseRayGeometry();
        // --live-transfer: the light change's cook is the transfer's pass, with nothing traced (SponzaLoop.Transfer.cs).
        // --live-transfer: the light change's cook is the transfer's pass, then the gather of the texels it flagged.
        if (TransferOn && transferReady && !transferCheck && sunNodeRefineRound == 0 && sunNodeFirst == 0 && sunNodePass == 0
            && transferAppliedFor != liveSunToward)
        {
            // The far record relit now, from the patches' direct sums now: relit at the cook (for its cut), before the
            // sums were up, its sun column was none -- and the apply's far light, most of a texel's, had no sun.
            // A step a frame (the sums and the lists up, the relight, the instant layer): together they held one frame
            // ~1.5 s after a sun change (2026-10-20).
            if (transferSetupFor != liveSunToward) { transferSetupFor = liveSunToward; transferSetupStep = 0; }
            if (transferSetupStep == 0)
            {
                patchDirectDone = false;
                farRelit = false;
                PatchDirectNow();
                if (farTransportMode && farOn && !farRecording) EnsureFarLists(farTransportPatches);
                transferSetupStep = 1;
                return;
            }
            if (transferSetupStep == 1)
            {
                FarRelightNow();
                transferSetupStep = 2;
                return;
            }
            transferSetupStep = 0;
            transferSetupFor = new Vector3(float.NaN);
            if (livePatchTexels) ApplyPatchTexelsNow(); else ApplyTransferNow();
            if (spotWhy) SpotWhySnapshot();
            RecallInstant();
            transferAppliedFor = liveSunToward;
            if (sunNodeTraceIds.Length == 0 && transferGatherIds.Length > 0)
            {
                // Cooked after this trace's ids were taken (at boot): the flagged now.
                RetireTraceBuffer();
                sunNodeTraceIds = LiveCoarseOrder();
                sunNodeTraceBuffer = Own(device.CreateGpuBuffer(Math.Max(16, sunNodeTraceIds.Length * 4), MemoryMarshal.AsBytes(sunNodeTraceIds.AsSpan()), "sponza.sun-nodes.traced"));
            }
            if (sunNodeTraceIds.Length == 0) { FinishSunNodes(); return; }
            BeginVisibleChanged();
        }
        if (TransferOn && transferCheck && transferReady && sunNodeRefineRound == 0 && sunNodeFirst == 0 && sunNodePass == 0 && transferCheckSky is null && !transferChecked)
        {
            patchDirectDone = false;
            farRelit = false;
            FarRelightNow();
            if (livePatchTexels) ApplyPatchTexelsNow(); else ApplyTransferNow();
            TransferCheckSnapshot();
            transferChecked = true;
        }
        var clock = Stopwatch.StartNew();
        var budget = CookScreenNow ? SunNodeCookScreenMs : GatherInBackground ? backgroundGatherMs : SunNodeLiveMs;
        sunNodeQueueWaitMs = 0;
        var steps = 0;
        while (sunNodeStage == SunNodeStage.Tracing
               && (gatherStepsAFrame > 0 ? steps++ < gatherStepsAFrame : clock.Elapsed.TotalMilliseconds - sunNodeQueueWaitMs < budget))
        {
            if (sunNodeReadback)
            {
                if (!SunNodeReadBack()) return;
                continue;
            }
            if (farRecording)
            {
                FarRecordStep(now: true);
                continue;
            }
            if (sunNodeRefineRound == 0) PatchDirectNow();
            if (sunNodeRefineRound == 0) FarRelightNow();
            if (sunNodeRefineRound == 0) SunProvisionalNow();
            // --lists: the stored lists in place of the gather (SponzaLoop.Lists.cs).
            if (!fineGathering && sunNodeRefineRound == 0 && sunNodeFirst == 0 && sunNodePass == 0 && ListsReady())
            {
                RunListsNow();
                return;
            }
            // --cone-lookups: the surface cache's coarser levels, for this light (SponzaLoop.PatchLevels.cs).
            if (leanShade && coneLookups && patchLevelsOn) { BuildPatchLevels(); LightPatchLevelsNow(); }
            // --instant-only (a measurement): no gather -- the cook lands on the instant layer's light (texel_patch_light.comp:
            // the patches' light by each texel's openness), what a light change shows before any ray.
            if (instantOnly && !fineGathering && sunNodeRefineRound == 0 && sunNodeFirst == 0 && sunNodePass == 0)
            {
                Console.WriteLine("[VulkanSponza] sun nodes: --instant-only -- no gather; the instant layer's light lands.");
                sunNodeStage = SunNodeStage.Done;
                ResetSunNodeRefine();
                if (splitCooking) FinishSplitCook();
                return;
            }
            var refining = sunNodeRefineRound > 0;
            var live = refining ? sunNodeRefineIds : sunNodeTraceIds;
            var sweepPasses = sunNodeRays / 32;
            var passes = refining ? RefinePasses : sweepPasses;
            var slice = Math.Min(sunNodeSlice, live.Length - sunNodeFirst);
            var seedPass = refining ? sweepPasses + (sunNodeRefineRound - 1) * RefinePasses + sunNodePass : sunNodePass;
            if (GpuResolveOn && !gpuResolving && !refining && sunNodeFirst == 0 && sunNodePass == 0) BeginGpuResolve();
            if (syncAll) device.WaitIdle();
            SunNodePassNow(refining ? sunNodeRefineBuffer : sunNodeTraceBuffer, sunNodeFirst, slice, sunNodePass, seedPass);
            if (syncAll) device.WaitIdle();
            if (++sunNodePass == passes)
            {
                sunNodePass = 0;
                FarFinish(now: true, refining ? sunNodeRefineBuffer : sunNodeTraceBuffer, sunNodeFirst, slice, passes);
                TransportCensusFinish(now: true, slice, passes);
                // On the GPU (SponzaLoop.SliceResolve.cs): resolved where it lies, nothing read back.
                if (gpuResolving)
                {
                    var clock2 = Stopwatch.StartNew();
                    var more = ResolveSliceNow(slice, passes);
                    sunNodeReadSeconds += clock2.Elapsed.TotalSeconds;
                    if (!more) return;
                    continue;
                }
                sunNodeReadback = true;
            }
        }
    }

    // plain: cosine rays alone, no far stop (the transfer's cook: the geometry's, so nothing of the light may steer them).
    // farCut: with plain, the far field's cut kept (the transfer's: its far rays are read from the record at the apply).
    private DispatchCommand SunNodeRaysCommand(GpuBufferHandle ids, int first, int slice, int seedPass, int discover = 0, bool plain = false, bool farCut = false)
    {
        var raysDeclared = sunNodeRaysInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        return new DispatchCommand(sunNodeRaysPipeline, slice, 1, 1,
            new ShaderUniform[]
            {
                new("uSlice", new Vector4Uniform(new Vector4(first, slice, seedPass + (plain ? 0 : gatherSeedOffset), sunNodeGuided && !plain ? 1f : 0f))),
                new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                new("uCookedDims", new Vector4Uniform(cookedDims)),
                // x 1: the light strategy on; y the patches' disk radius (a patch stands for spacing^2 of surface).
                new("uLights", new Vector4Uniform(new Vector4(sunNodeLit && !plain ? 1f : 0f, transportSpacing / MathF.Sqrt(MathF.PI), GatherSmoothNow ? 1f : 0f, GatherClearOn ? 1f : 0f))),
                new("uTile", new Vector4Uniform(new Vector4(interleave ? 1f : 0f, texelGrid.W, lightmapTexels ? lightmapFirstRecord : float.MaxValue, !plain && NearCutMode == 2 ? hitCellNear : !plain ? rayCapMeasure : 0f))),
                new("uFar", new Vector4Uniform(plain && !farCut ? Vector4.Zero : FarUniform(discover))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("NodeLights", sunNodeLightBuffer),
                new ShaderBufferBinding("SunNodesLive", ids),
                new ShaderBufferBinding("NodeRays", sunNodeRaysBuffer),
                new ShaderBufferBinding("NodeRayWeights", sunNodeRayWeights),
                new ShaderBufferBinding("NodeGuides", sunNodeGuideBuffer),
                new ShaderBufferBinding("NodeGuidePatch", sunNodeGuidePatch),
                new ShaderBufferBinding("TexelPatch", sunNodeTexelPatch),
                new ShaderBufferBinding("FarRecord", FarRecordBuffer),
                new ShaderBufferBinding("PatchNeighbours", FarNeighboursBuffer),
                new ShaderBufferBinding("LightmapFrames", GatherSmoothNow ? lightmapFramesBuffer : sunNodeAccum),
                new ShaderBufferBinding("TexelClear", GatherClearOn ? texelClearBuffer : sunNodeAccum),
            }.Concat(CookedBuffers()).Where(b => raysDeclared.Contains(b.Name)).ToArray());
    }

    // cells: the hit cells answer the hits (SponzaLoop.HitCells.cs), the trace having shot each hit's sun.
    // --lean-shade (a prototype, the runtime gather's model): texel_nodes_lean.comp in place of the shade on every pass the
    // hit cells answer -- each hit its cell's light; a near one (nearer than the cells' near distance, without a cell, a
    // contact's, a sheet's) the shade's search; the fine round's near rounds as the shade has them. The crevices corridor
    // and the materials testbeds as the shade (cells' sun) to the third decimal; Sponza's sweep shade 8 s against 64.
    // Searching only contacts and sheets (not the near distance) put the seams back to 0.064; the fine rounds' passes cost
    // the same with no search at all (~55 ms, the rays kernel slowed alike): the frame's own work beside them, not the
    // shade (2026-10-23).
    private bool leanShade, coneLookups, patchLevelsOn;
    private ShaderInterface leanShadeInterface = null!;
    private PipelineHandle leanShadePipeline;

    // --texel-h4 (the light-agnostic record): every gather pass's H4 per channel, beside the parts, summed over a slice's
    // passes in texelH4Buffer (64 B a texel of the slice, by its place, as the parts' sums; texel_record_h4.glsl), each pass
    // leaving the shape the sums make in texelOwnShapeBuffer. BLIX_TEXEL_H4_FULL=1: the sums kept for every texel (64 B a
    // texel, dumped as texel_h4.f32x16) for tools/gi-model/h4_record_check.py.
    private bool texelH4, texelShapeLuma;
    private float texelShapePool = 0.2f;
    private readonly bool texelH4Full = Environment.GetEnvironmentVariable("BLIX_TEXEL_H4_FULL") == "1";
    private float TexelH4Mode => texelH4Full ? 2f : 1f;
    private GpuBufferHandle texelH4Buffer;
    // The record's shape (texel_shape.glsl, 12 B a texel each: a snorm8 triple and a scale a channel): each texel's own --
    // its gather's, or its fine copy's (transfer_fine.comp), its provenance telling which -- and the pooled one the frame
    // reads (texel_compose.comp, the incident pass's TexelShape). 24 B a texel in all, the sums a slice's (136 B a texel
    // before, 2026-10-24).
    private GpuBufferHandle texelOwnShapeBuffer, texelShapeBuffer, texelShapeNone;
    private bool TexelShapeOn => texelH4 && !texelShapeBuffer.Equals(default(GpuBufferHandle));
    private GpuBufferHandle ShapeOr(GpuBufferHandle b)
    {
        if (!b.Equals(default(GpuBufferHandle))) return b;
        if (texelShapeNone.Equals(default(GpuBufferHandle))) texelShapeNone = Own(device.CreateGpuBuffer(64, new byte[64], "sponza.texel-shape.none"));
        return texelShapeNone;
    }

    // The record's buffer (made at its first gather) and whether the records' smooth normals are there to read.
    private (bool On, bool Frames) TexelH4Now()
    {
        if (texelH4 && texelH4Buffer.Equals(default(GpuBufferHandle)))
        {
            var sums = checked((texelH4Full ? texelCount : sunNodeSlice) * 64);
            texelH4Buffer = Own(device.CreateGpuBuffer(sums, new byte[sums], "sponza.texel-h4"));
            texelOwnShapeBuffer = Own(device.CreateGpuBuffer(checked(texelCount * 12), new byte[checked(texelCount * 12)], "sponza.texel-shape.own"));
            texelShapeBuffer = Own(device.CreateGpuBuffer(checked(texelCount * 12), new byte[checked(texelCount * 12)], "sponza.texel-shape"));
            litClipmapBuffer = null;   // the lit pass binds the shapes from here (LitLightmapBuffers)
            litSceneBuffers = null;
        }
        var on = texelH4 && !texelH4Buffer.Equals(default(GpuBufferHandle));
        return (on, on && GatherSmoothNow && !lightmapFramesBuffer.Equals(default(GpuBufferHandle)));
    }

    private DispatchCommand LeanShadeCommand(GpuBufferHandle ids, int first, int slice, int pass, bool exactSun)
    {
        var (h4On, framesOn) = TexelH4Now();
        var detail = splitCooking && sunNodeRefineRound == 0 && !sunNodeDetail.Equals(default(GpuBufferHandle));
        var declared = leanShadeInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        return new DispatchCommand(leanShadePipeline, slice, 1, 1,
            new ShaderUniform[]
            {
                new("uShade", new Vector4Uniform(new Vector4(slice, sunNodeSuns!.Length, 0f, pass))),
                new("uSky", new Vector4Uniform(new Vector4(1f, 1f, skyKept ? SunColumn() + 1 : 0f, 0f))),
                new("uDetail", new Vector4Uniform(new Vector4(first, detail ? 1f : 0f, 0f, 0f))),
                new("uNear", new Vector4Uniform(new Vector4(NearCutMode, hitCellNear, nearUnknownLocal ? 1f : 0f, 0f))),
                new("uLookup", new Vector4Uniform(new Vector4(sunNodePlane, sunNodeSigma, hitCellNear, NearTexelLightNow ? sunNodeHashSlots : 0f))),
                new("uTexelGrid", new Vector4Uniform(texelGrid)),
                new("uIrr", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, (sunNodeSuns!.Length + 31) / 32))),
                new("uSheets", new Vector4Uniform(new Vector4(SheetsInGi ? 1f : 0f, 0f, 0f, 0f))),
                // BLIX_CAPTURE_FINE_ONLY: the capture in the fine round's first pass alone (its crease texels, small); every ray
                // the same tag, so the hit's distance in the tag's place (z -1: texel_nodes_lean.comp).
                // BLIX_CAPTURE_DIST: every round's rays with their hits' distance (no tag), for where rays end.
                new("uCapture", new Vector4Uniform(RayCaptureOn && !listCook && (!captureFineOnly || fineGathering && nearRounds == nearRoundsLeft)
                    ? (captureFineOnly || captureDist ? RayCaptureUniform with { Z = -1f } : RayCaptureUniform) : Vector4.Zero)),
                new("uCone", new Vector4Uniform(new Vector4(coneLookups ? sunNodeRays : 0f, hitCellSize, transportSpacing, sunNodeKinds.Length))),
                new("uLevels", new Vector4Uniform(new Vector4(coneLookups && patchLevelsOn && PatchLevelsReady ? PatchLevelCount : 0, 0f, 0f, 0f))),
                new("uExact", new Vector4Uniform(new Vector4(exactSun ? 1f : 0f, Environment.GetEnvironmentVariable("BLIX_LEAN_SEARCH_ALL") == "1" ? 1f : 0f,
                    h4On ? TexelH4Mode : 0f, h4On && framesOn ? 1f : 0f))),
            },
            new[] { new ShaderTextureBinding("uSkyRadiance", envCubeTexture) },
            Buffers: new[]
            {
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("NodeRays", sunNodeRaysBuffer),
                new ShaderBufferBinding("NodeHits", sunNodeHits),
                new ShaderBufferBinding("SunNodes", sunNodeSunsBuffer),
                new ShaderBufferBinding("NodeRayWeights", sunNodeRayWeights),
                new ShaderBufferBinding("NodeIds", ids),
                new ShaderBufferBinding("HitCells", hitCellsBuffer),
                new ShaderBufferBinding("CellLight", cellLightBuffer),
                new ShaderBufferBinding("NodeAccum", sunNodeAccum),
                new ShaderBufferBinding("NodeDetail", detail ? sunNodeDetail : sunNodeAccum),
                new ShaderBufferBinding("FinePlace", finePlaceBuffer.Equals(default(GpuBufferHandle)) ? sunNodeAccum : finePlaceBuffer),
                new ShaderBufferBinding("FarPart", farPartBuffer.Equals(default(GpuBufferHandle)) ? sunNodeAccum : farPartBuffer),
                new ShaderBufferBinding("SunNodeHash", sunNodeHash),
                new ShaderBufferBinding("FoliagePoints", sunNodeFoliagePoints.Equals(default(GpuBufferHandle)) ? sunNodeAccum : sunNodeFoliagePoints),
                new ShaderBufferBinding("FoliageHash", sunNodeFoliageHash.Equals(default(GpuBufferHandle)) ? sunNodeAccum : sunNodeFoliageHash),
                new ShaderBufferBinding("SunNodeBits", sunNodeBits),
                new ShaderBufferBinding("SplitSky", NearTexelLightNow ? splitSkyBuffer : sunNodeAccum),
                new ShaderBufferBinding("SplitSun", NearTexelLightNow ? splitSunBuffer : sunNodeAccum),
                NearTexelLightNow ? ProvenanceBinding() : new ShaderBufferBinding("TexelProvenance", sunNodeAccum),
                new ShaderBufferBinding("SheetRows", SheetsInGi ? SheetRowsBuffer!.Value : sunNodeAccum),
                RayCaptureBinding(sunNodeAccum),
                new ShaderBufferBinding("SunNodePatchLight", sunNodePatchLight),
                new ShaderBufferBinding("PatchNeighbours", FarNeighboursBuffer),
                new ShaderBufferBinding("CookedPatches", cookedPatchBuffer),
                new ShaderBufferBinding("PatchDirect", liveDirect.Equals(default(GpuBufferHandle)) ? sunNodeAccum : liveDirect),
                new ShaderBufferBinding("LevelNodes", PatchLevelsReady ? levelNodesBuffer : sunNodeAccum),
                new ShaderBufferBinding("LevelParents", PatchLevelsReady ? levelParentsBuffer : sunNodeAccum),
                new ShaderBufferBinding("LevelNeighbours", PatchLevelsReady ? levelNeighboursBuffer : sunNodeAccum),
                new ShaderBufferBinding("LevelLight", PatchLevelsReady ? levelLightBuffer : sunNodeAccum),
                new ShaderBufferBinding("LightmapFrames", h4On && framesOn ? lightmapFramesBuffer : sunNodeAccum),
                new ShaderBufferBinding("TexelH4", h4On ? texelH4Buffer : sunNodeAccum),
                new ShaderBufferBinding("TexelOwnShape", h4On ? texelOwnShapeBuffer : sunNodeAccum),
            }.Where(b => declared.Contains(b.Name)).ToArray());
    }

    private DispatchCommand SunNodeShadeCommand(GpuBufferHandle ids, int first, int slice, int pass, int discover, float exactMode, bool few = false, bool cells = false,
        int exactColumn = 0)
    {
        // The cells' detail (SponzaLoop.SplitDetail.cs) from the sweep of a split cook; never discovering or refining.
        var detail = splitCooking && discover == 0 && sunNodeRefineRound == 0 && !sunNodeDetail.Equals(default(GpuBufferHandle));
        // --texel-h4: the split variant's (few) gather passes, not a discovery's.
        var shadeH4 = few && discover == 0 ? TexelH4Now() : (On: false, Frames: false);
        var declared = sunNodeShadeInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        return new DispatchCommand(few ? sunNodeShadeSplitPipeline : sunNodeShadePipeline, slice, 1, 1,
            new ShaderUniform[]
            {
                new("uShade", new Vector4Uniform(new Vector4(slice, sunNodeSuns!.Length, sunNodeHashSlots, pass))),
                new("uTexelGrid", new Vector4Uniform(texelGrid)),
                new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                new("uCookedDims", new Vector4Uniform(cookedDims)),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, sunNodeUnknownSun))),
                // A ray's sky as the gather takes it: strength 1, one mip blurred.
                new("uSky", new Vector4Uniform(new Vector4(1f, 1f, skyKept ? SunColumn() + 1 : 0f, 0f))),
                // w: the hit cells (SponzaLoop.HitCells.cs) answer the hit, their sun bit shot for this cook's sun.
                new("uLookup", new Vector4Uniform(new Vector4(sunNodePlane, sunNodeSigma, hitCellNear, cells ? 1f : 0f))),
                new("uFoliage", new Vector4Uniform(new Vector4(sunNodeFoliageSlots, discover, exactMode, (exactColumn + 1) * (SheetsInGi ? -1f : 1f)))),
                new("uDetail", new Vector4Uniform(new Vector4(first, detail ? 1f : 0f, CensusOn(discover) ? 1f : 0f, censusPlacements))),
                new("uFar", new Vector4Uniform(exactMode is > 0f and < 2.5f ? Vector4.Zero : FarUniform(discover))),
                new("uTransport", new Vector4Uniform(TransportUniform(discover))),
                new("uFarTransport", new Vector4Uniform(new Vector4(FarTransportUniform, shadeH4.On ? TexelH4Mode : 0f, shadeH4.Frames ? 1f : 0f, 0f))),
                new("uHitBlend", new Vector4Uniform(new Vector4(HitBlendNow ? 1f : 0f, transportSpacing, NearTexelLightNow ? 1f : 0f, SunColumn()))),
                new("uCapture", new Vector4Uniform(discover == 0 && exactMode is < 0.5f or > 2.5f ? RayCaptureUniform : Vector4.Zero)),
                new("uNear", new Vector4Uniform(discover == 0 && few ? new Vector4(NearCutMode, hitCellNear, nearUnknownLocal ? 1f : 0f, sheetGlowSplit ? 1f : NearGlowSplitNow ? 2f : 0f) : new Vector4(0f, 1e30f, 0f, 0f))),
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
                new ShaderBufferBinding("TexelPatch", sunNodeTexelPatch),
                new ShaderBufferBinding("NodeDetail", detail ? sunNodeDetail : sunNodeAccum),
                new ShaderBufferBinding("NodeIds", ids),
                new ShaderBufferBinding("NodeCensus", CensusOn(discover) ? sunNodeCensus : sunNodeAccum),
                new ShaderBufferBinding("NodeGuidePatch", sunNodeGuidePatch),
                new ShaderBufferBinding("FarRecord", FarRecordBuffer),
                new ShaderBufferBinding("NodeFarBins", FarBinsBuffer),
                new ShaderBufferBinding("PatchNeighbours", FarNeighboursBuffer),
                new ShaderBufferBinding("NodeHarmonic", FarHarmonicBuffer),
                new ShaderBufferBinding("CensusObjects", censusObjects.Equals(default(GpuBufferHandle)) ? sunNodeAccum : censusObjects),
                new ShaderBufferBinding("NodeTransport", TransportRecordsBuffer),
                new ShaderBufferBinding("PatchDirectSums", patchDirectSums.Equals(default(GpuBufferHandle)) ? sunNodeAccum : patchDirectSums),
                new ShaderBufferBinding("FarTransport", farTransportRecord.Equals(default(GpuBufferHandle)) ? sunNodeAccum : farTransportRecord),
                new ShaderBufferBinding("HitCells", hitCellsBuffer.Equals(default(GpuBufferHandle)) ? sunNodeAccum : hitCellsBuffer),
                new ShaderBufferBinding("SplitSky", NearTexelLightNow ? splitSkyBuffer : sunNodeAccum),
                new ShaderBufferBinding("SplitSun", NearTexelLightNow ? splitSunBuffer : sunNodeAccum),
                NearTexelLightNow ? ProvenanceBinding() : new ShaderBufferBinding("TexelProvenance", sunNodeAccum),
                new ShaderBufferBinding("SheetRows", SheetsInGi ? SheetRowsBuffer!.Value : sunNodeAccum),
                RayCaptureBinding(sunNodeAccum),
                new ShaderBufferBinding("NodeGlow", NearGlowSplitNow ? sunNodeGlow : sunNodeAccum),
                new ShaderBufferBinding("LightmapFrames", shadeH4.Frames ? lightmapFramesBuffer : sunNodeAccum),
                new ShaderBufferBinding("TexelH4", shadeH4.On ? texelH4Buffer : sunNodeAccum),
                new ShaderBufferBinding("TexelOwnShape", shadeH4.On ? texelOwnShapeBuffer : sunNodeAccum),
                new ShaderBufferBinding("FinePlace", finePlaceBuffer.Equals(default(GpuBufferHandle)) ? sunNodeAccum : finePlaceBuffer),
                new ShaderBufferBinding("FarPart", farPartBuffer.Equals(default(GpuBufferHandle)) ? sunNodeAccum : farPartBuffer),
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
    // The discovered sun points (hits among neither texel nor foliage point): a stage of the GI bundle, under the
    // combined key and the discovery's settings -- the geometry's; their sun bits are this cook's.
    // "rays 512" fixed: a discovery pass is one pass of 32 rays a texel whatever the sweep's count (--live-rays changed it,
    // and a live boot re-ran a 14 s discovery the bundle had, 2026-10-16).
    private string DiscoveredHeader => string.Create(Inv, $"discovered {(nodePlanes ? "v4" : "v1")}{GatherLiftKey};stride {sunNodeDiscoverStride};room {DiscoverRoom};rays 512;slice {sunNodeSlice};texels {texelCount}");

    private void RecordSunNodeDiscover()
    {
        if (sunNodeDiscoverFirst == 0 && lightBakeKey is { } key
            && LoadStage(key, "sun-points-discovered", r => r.ReadString() != DiscoveredHeader ? null : ReadArray<Vector4>(r)) is { } known)
        {
            var tracerK = frameTracer!;
            var dirsK = sunNodeBitDirections;
            var wordsK = (dirsK.Length + 31) / 32;
            var reachK = sunNodeReach;
            var liftK = sunNodeLift;
            var hostK = !liveTexels;
            sunNodeDiscoverTask = Task.Run(() => (known, hostK ? ShootSunBits(tracerK, known, dirsK, wordsK, reachK, liftK, out _) : new uint[known.Length / 2 * wordsK]));
            sunNodeDiscoverFirst = sunNodeDiscoverIds.Length;
            sunNodeStage = SunNodeStage.Merge;
            return;
        }
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
        var saveKey = lightBakeKey;
        var header = DiscoveredHeader;
        var hostAdded = !liveTexels;
        sunNodeDiscoverTask = Task.Run(() =>
        {
            var added = NewSunPoints(existing, found, grid);
            if (saveKey is not null) SaveStage(saveKey, "sun-points-discovered", w => { w.Write(header); WriteArray(w, added); });
            var bits = hostAdded ? ShootSunBits(tracer, added, directions, words, reach, lift, out _) : new uint[added.Length / 2 * words];
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] sun nodes: discovery found {count:N0} hits among neither texel nor sun point ({(count >= DiscoverRoom ? "the list full; " : "")}every {sunNodeDiscoverStride}th texel, a pass), {added.Length / 2:N0} new sun points; bits in {clock.Elapsed.TotalSeconds:0.0} s."));
            return (added, bits);
        });
        sunNodeStage = SunNodeStage.Merge;
    }

    // The merge, over frames: the sun points and bits in one, the hit cells' patches stepped (StepHitCells), the transfer
    // in one, then the patch texels' contact cook a ray pass at a time within 50 ms a frame (BuildPatchTexels) -- whole in one frame
    // it froze a fresh cook's window for 49.5 s.
    private int mergeStep;
    private double mergeUploadMs, mergeCellsMs, mergeTransferMs;
    private Stopwatch? mergeContactClock;

    private void MergeDiscovered()
    {
        if (mergeStep == 0)
        {
            if (sunNodeDiscoverTask is not { IsCompleted: true } done) return;
            var (added, bits) = done.Result;
            sunNodeDiscoverTask = null;
            var mergeClock = Stopwatch.StartNew();
            double Lap() { var s = mergeClock.Elapsed.TotalMilliseconds; mergeClock.Restart(); return s; }
            UploadSunPoints(sunNodePoints.Concat(added).ToArray(), sunNodePointBits.Concat(bits).ToArray());
            ResetSunNodeCounters();
            if (liveTexels) BeginLivePointBits();
            mergeUploadMs = Lap();
            BeginHitCells();
            mergeContactClock = Stopwatch.StartNew();
            mergeStep = 1;
            return;
        }
        if (mergeStep == 1)
        {
            if (!StepHitCells(50)) return;
            mergeCellsMs = mergeContactClock!.Elapsed.TotalMilliseconds;
            var transferClock = Stopwatch.StartNew();
            BuildTransfer();
            mergeTransferMs = transferClock.Elapsed.TotalMilliseconds;
            mergeContactClock.Restart();
            mergeStep = 2;
            return;
        }
        if (!BuildPatchTexels()) return;
        mergeStep = 0;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sun nodes: the merge -- sun points and bits {mergeUploadMs:0} ms (one frame), hit cells {mergeCellsMs:0} ms of frames, transfer {mergeTransferMs:0} ms (one frame); then the patch texels over {mergeContactClock!.Elapsed.TotalSeconds:0.0} s of frames."));
        // The trace proper counts afresh.
        RetireGpuBuffer(sunNodeStats);
        sunNodeStats = Own(device.CreateGpuBuffer(64, new byte[64], "sponza.sun-nodes.stats"));
        sunNodeClock.Restart();
        sunNodeStage = SunNodeStage.Tracing;
        if (liveTexels) liveCookWarm = true;
        if (liveTexels) Console.WriteLine(string.Create(Inv, $"[VulkanSponza] live texels: gathering the {sunNodeTraceIds.Length:N0} texels in view."));
        // The sun moved during the setup (the solve, the bits, the lists: ~1.5 min on Sponza): no tracing a sun that
        // is gone -- the newest's cook starts now (2026-10-14: a second move waited out the first's whole 3.3-min trace).
        if (SunOnlyCook && splitSunWanted is { } wanted && sunNodeDirsAsked is [var asked] && Vector3.Distance(wanted, asked) > 1e-5f)
            SplitSunChanged(wanted);
    }

    // Each live texel's nearest patch it can see, a slice a frame (as many texels as the rays buffer holds PatchCandidates
    // rays for): the candidates and their rays, the CLOSEST batch, the first unblocked kept.
    // Each texel's nearest visible patch: a stage of the GI bundle (under the transport's combined key -- the patches and
    // the texels both), picked on the GPU when it is not there.
    // v2/v3: candidates the texel lies behind left out, the rays' lift a quarter of the line at most (v3: re-picked once, as a first boot now adopts it into the live solve; 2026-10-22).
    private string TexelPatchHeader => string.Create(Inv, $"texel patch {(patchPickOld ? "v1" : "v3")}{GatherLiftKey};candidates {PatchCandidates};texels {texelCount}");

    private void RecordSunNodePatchPick()
    {
        BuildPatchClear();
        var live = texelLiveIds!;
        // The live solve's copy, read from the same stage: aliased without reading the 66 MB again.
        if (sunNodePatchFirst == 0 && liveTexels && liveTexelPatchStored && !liveTexelPatch.Equals(default(GpuBufferHandle)))
        {
            if (!sunNodeTexelPatch.Equals(liveTexelPatch)) RetireGpuBuffer(sunNodeTexelPatch);
            sunNodeTexelPatch = liveTexelPatch;
            sunNodePatchFirst = live.Length;
            sunNodeStage = sunNodeDiscoverIds.Length > 0 ? SunNodeStage.Discover : SunNodeStage.Tracing;
            return;
        }
        if (sunNodePatchFirst == 0 && lightBakeKey is { } key
            && LoadStage(key, "texel-patch", r => r.ReadString() != TexelPatchHeader ? null : ReadArray<int>(r)) is { } stored)
        {
            if (!sunNodeTexelPatch.Equals(liveTexelPatch)) RetireGpuBuffer(sunNodeTexelPatch);
            // The live solve's copy of the same stage, when loaded (66 MB on Sponza, 2026-10-20): read-only from here.
            sunNodeTexelPatch = liveTexels && !liveTexelPatch.Equals(default(GpuBufferHandle)) ? liveTexelPatch
                : Own(device.CreateGpuBuffer(texelCount * 4, MemoryMarshal.AsBytes(stored.AsSpan()), "sponza.sun-nodes.texel-patch"));
            sunNodePatchFirst = live.Length;
            sunNodeStage = sunNodeDiscoverIds.Length > 0 ? SunNodeStage.Discover : SunNodeStage.Tracing;
            return;
        }
        if (sunNodePatchFirst >= live.Length)
        {
            sunNodeStage = sunNodeDiscoverIds.Length > 0 ? SunNodeStage.Discover : SunNodeStage.Tracing;
            device.WaitIdle();
            var picked = MemoryMarshal.Cast<byte, int>(device.ReadGpuBuffer(sunNodeTexelPatch, 0, texelCount * 4).AsSpan()).ToArray();
            if (lightBakeKey is { } saveKey) SaveStage(saveKey, "texel-patch", w => { w.Write(TexelPatchHeader); WriteArray(w, picked); });
            // A first boot (no stored pick): the live solve and the settle pool read liveTexelPatch, which was -1 throughout --
            // every texel's direct sun into its nearest patch, no pool -- so a first boot's light was not a later boot's (the
            // crevices' corridor |error| 0.0177 picked, 0.0160 loaded, 2026-10-22). The pick into it, and the live solve and
            // the cook again from it once this setup is done: every boot then lights from the same pick.
            if (liveTexels && !liveTexelPatchStored && !liveTexelPatch.Equals(default(GpuBufferHandle)))
            {
                device.WriteGpuBuffer(liveTexelPatch, 0, MemoryMarshal.AsBytes(picked.AsSpan()));
                liveTexelPatchStored = true;
                liveInjected = false;
                liveRestartPending = -Vector3.Normalize(sunDirection);
                Console.WriteLine("[VulkanSponza] sun nodes: the pick was not stored -- the live solve and the cook restart from it (a first boot).");
            }
            int blocked = 0, none = 0;
            foreach (var id in live) { if (picked[id] == -1) blocked++; else if (picked[id] == -2) none++; }
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] sun nodes: each texel's nearest visible patch picked ({live.Length:N0} texels, {PatchCandidates} rays each); {blocked:N0} saw none of theirs, {none:N0} had none facing their way."));
            return;
        }
        var count = Math.Min(sunNodeSlice * 32 / PatchCandidates, live.Length - sunNodePatchFirst);
        var declared = sunNodePatchInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        DispatchCommand Pick(int mode) => new(sunNodePatchPipeline, (count + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uPatch", new Vector4Uniform(new Vector4(sunNodePatchFirst, count, mode, patchPickOld ? 0f : 1f))),
                new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                new("uCookedDims", new Vector4Uniform(cookedDims)),
                new("uClear", new Vector4Uniform(new Vector4(PatchClearOn ? 1f : 0f, lightmapTexels ? lightmapFirstRecord : float.MaxValue, 0f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("SunNodesLive", sunNodeLive),
                new ShaderBufferBinding("NodeRays", sunNodeRaysBuffer),
                new ShaderBufferBinding("NodeHits", sunNodeHits),
                new ShaderBufferBinding("PatchCandidates", sunNodePatchCandidates),
                new ShaderBufferBinding("TexelPatch", sunNodeTexelPatch),
                new ShaderBufferBinding("TexelClear", PatchClearOn ? texelClearBuffer : sunNodeTexelPatch),
                new ShaderBufferBinding("PatchClear", PatchClearOn ? patchClearBuffer : sunNodeTexelPatch),
            }.Concat(CookedBuffers()).Where(b => declared.Contains(b.Name)).ToArray());
        graph.Dispatch(sunNodePatchAPass, Pick(0));
        RecordClosestBatch(sunNodePatchBatch, sunNodeRaysBuffer, sunNodeHits, count * PatchCandidates, 0x9A7C3u);
        graph.Dispatch(sunNodePatchBPass, Pick(1));
        sunNodePatchFirst += count;
    }
}
