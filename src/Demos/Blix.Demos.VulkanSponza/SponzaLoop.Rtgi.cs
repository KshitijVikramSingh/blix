using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;

namespace Blix.Demos.VulkanSponza;

// --rtgi (S1 of the GI redesign, 2026-10-25): indirect light from a world radiance cache, no cook. A spatial hash of world
// cells (rtgi_cache.glsl: a cube at a level the distance from the camera picks, and the side the surface faces) holds
// each cell's indirect irradiance, sky visibility and sun visibility. Each frame:
//
//   rtgi-train    last frame's training points, their rays answered, into their cells (rtgi_train.comp): what the
//                 cosine ray saw -- the sky, or the cell where it landed (its irradiance and sun times its albedo) --
//                 and whether the sun ray reached
//   rtgi-resolve  each cell's frame of samples into a running mean (rtgi_resolve.comp); cells untrained for a while emptied
//   rtgi-rays     this frame's points (rtgi_rays.comp): --rtgi-tiles visible ones, a pixel of each tile over the screen, and
//                 --rtgi-hits where last frame's rays landed, so the cells a bounce away train too -- a path grows a
//                 vertex a frame through the cache; a cosine ray and a sun ray each, to Metal in the traced shadows' group
//   rtgi-readout  the cache at each half-res pixel (rtgi_readout.frag, trilinear over its level's cells) into the incident
//                 targets, in place of incident-light-half: the upsample and the lit pass read it unchanged
//
// The answers arrive a frame later (the group runs beside the frame); the readout reads what the cache holds now.
internal sealed partial class SponzaLoop
{
    private bool rtgi;
    // 4 M slots (272 MB): San Miguel held 1.8-3.6 M cells (87% of 4 M at the old 1 cm per metre growth, its landings then
    // failing to find a slot).
    private int rtgiTiles = 73_000, rtgiHitPoints = 32_000, rtgiSlotsLog2 = 22;
    // Cells untrained for --rtgi-max-age frames are emptied: 1,800 (30 s at 60 fps; at 600 a walk back to a place left 10 s
    // before found it gone and re-settling, 2026-10-26).
    // Cells 5 cm, growing 0.5 cm a metre from the camera (doubling every ~10 m; at 1 cm, every ~5 m, mid-distance GI went
    // coarse too soon -- the user, 2026-10-26).
    private float rtgiCell = 0.05f, rtgiCellGrow = 0.005f, rtgiMaxAge = 1800f;
    // --rtgi-near M: cells the finest out to M metres from the camera, growing past it (0: from the camera). The good GI
    // around the camera was too small a circle (the user, San Miguel, 2026-10-26: 15 m overkill, 8 m chosen).
    private float rtgiNearField = 8f;
    // --rtgi-settle-frames N: frames the cache trains before --shot-when-ready may shoot (600).
    private int rtgiSettleFrames = 600;
    // S2: a cell's history follows its light (rtgi_resolve.comp) -- up to --rtgi-history samples while its samples agree
    // with it (a fixed 64 shimmered, never settling: the user, 2026-10-26), the slow mean replaced by a fast one (--rtgi-fast
    // samples) when the two disagree by more than --rtgi-change-sigma of the fast one's standard errors.
    // The fast window: 64 (at 16 the cache reset itself on noise -- a bright frame or a dark one -- and read 0.18 rel
    // |error| at Crytek Sponza's points settled, 3-4% dark; 64: 0.10 and 1.00, 2026-10-26).
    // --rtgi-change-sigma 5 and --rtgi-change-frames 3: a disagreement must pass 5 standard errors three frames running
    // (rtgi_resolve.comp): the net for changes nobody announced; announced ones (the sun turned) cut at once.
    // --rtgi-history 16384: a still light keeps converging; capped at 1,024 a cell's mean kept moving by a 1/1,024 of each
    // sample, the shadowed areas' rare bright ones most -- mud that never settled (the user). Crytek Sponza settled: flicker
    // 1.6% -> 0.8%, error 0.100 -> 0.097 (2026-10-26). Changes are the cuts' (the sun's at once, the rest's net).
    private float rtgiHistory = 16384f, rtgiChangeSigma = 5f, rtgiFast = 64f, rtgiChangeFloor = 0.05f, rtgiChangeFrames = 3f;
    // --rtgi-sun-cut N: on a frame the sun turns (0.02 degrees or more), every cell's light history capped to N samples
    // (and its sun visibility's to 4): a known change, not one for the fast mean to find. 0: none.
    private float rtgiSunCut = 32f;
    // --rtgi-age F: a cell's light history held to F of its age in samples (at least --rtgi-age-floor, 64), so the samples
    // taken before the cells around it had their light fade fast (rtgi_resolve.comp). 0: no limit.
    private float rtgiAge = 0.25f, rtgiAgeFloor = 64f;
    private Vector3 rtgiLastSun;
    // Diagnostics for the cache's bounce (the dead end 23% dark, 2026-10-26): --rtgi-count-unknown counts a landing cell
    // that holds nothing as dark; --rtgi-no-coarse gives the training no coarser cell to read in its place.
    private bool rtgiCountUnknown, rtgiNoCoarse, rtgiBackfaceLit;
    // Landings in untrained cells, trained the next frame (rtgi_train.comp's requests): by parity. --rtgi-no-requests: off.
    private bool rtgiRequests = true;
    // --rtgi-clamp-sigma K: a sample capped at its cell's mean + K standard deviations once it holds 32 (0: off).
    private float rtgiClampSigma;
    private GpuBufferHandle[] rtgiRequestBufs = Array.Empty<GpuBufferHandle>();
    private const int RtgiTopLevel = 10;
    // --rtgi-screen (S3): each half-res pixel's light from the screen tiles' reservoirs (ReSTIR GI: rtgi_reservoir.comp,
    // rtgi_screen.frag) in place of the cache read at its cell. --rtgi-screen-taps N reservoirs a pixel (8) within
    // --rtgi-screen-radius tiles (2); --rtgi-reuse-max the count a reservoir keeps (20).
    private bool rtgiScreen;
    // With the reuse tested by rays (rtgi_spatial.comp) the screen gather keeps to its tile and the nearest: 4 within 1.
    private int rtgiScreenTaps = 4, rtgiScreenRadius = 1;
    private ShaderInterface rtgiSpatialInterface = null!;
    private PassHandle rtgiSpatialPassHandle;
    private PipelineHandle rtgiSpatialPipeline;
    private GpuBufferHandle[] rtgiProposals = Array.Empty<GpuBufferHandle>(), rtgiSpatialOrigins = Array.Empty<GpuBufferHandle>(),
        rtgiSpatialDirs = Array.Empty<GpuBufferHandle>(), rtgiSpatialOccluded = Array.Empty<GpuBufferHandle>();
    private float rtgiReuseMax = 20f;
    private ShaderInterface rtgiReservoirInterface = null!, rtgiScreenInterface = null!;
    private PassHandle rtgiReservoirPassHandle, rtgiScreenPassHandle;
    private PipelineHandle rtgiReservoirPipeline, rtgiScreenPipeline;
    private GpuBufferHandle[] rtgiReservoirs = Array.Empty<GpuBufferHandle>();
    // The view-projections the last two frames' points were drawn in, and the grid this frame's reservoirs are laid out in.
    private Matrix4x4 rtgiViewProj1, rtgiViewProj2, rtgiGridViewProj;
    private (int X, int Y) rtgiGrid;
    private int rtgiReservoirCur = -1, rtgiSpatialTiles;
    private ShaderInterface rtgiRaysInterface = null!, rtgiTrainInterface = null!, rtgiResolveInterface = null!, rtgiReadoutInterface = null!;
    private PassHandle rtgiRaysPassHandle, rtgiTrainPassHandle, rtgiResolvePassHandle, rtgiReadoutPassHandle;
    private PipelineHandle rtgiRaysPipeline, rtgiTrainPipeline, rtgiResolvePipeline, rtgiReadoutPipeline;
    // By the frame's parity: this frame's points, rays and answers; the other parity's are last frame's, trained now.
    private GpuBufferHandle[] rtgiPointsBuf = Array.Empty<GpuBufferHandle>(), rtgiRaysBuf = Array.Empty<GpuBufferHandle>(),
        rtgiHitsBuf = Array.Empty<GpuBufferHandle>(), rtgiOriginsBuf = Array.Empty<GpuBufferHandle>(), rtgiOccludedBuf = Array.Empty<GpuBufferHandle>();
    private GpuBufferHandle rtgiKeys, rtgiAccum, rtgiResolved, rtgiMoments, rtgiStamps, rtgiCells, rtgiFiltered, rtgiDirection;
    // Each cell's irradiance vector (where its light comes from, rtgi_resolve.comp): --rtgi-guide S of the training rays
    // drawn toward it where the cell holds --rtgi-guide-min samples (0.5, 16; 0: cosine alone, rtgi_rays.comp), and its part
    // along the surface written as the gradient the lit pass carries onto normal maps (--rtgi-no-gradient: not).
    private float rtgiGuide = 0.5f, rtgiGuideMin = 16f, rtgiGuideKappa = 40f;
    // Path guiding's table (rtgi_guide.glsl): 2^--rtgi-guide-slots-log2 coarse cells (18: 262,144, ~72 MB), each 32 bins
    // of where its light comes from, held over --rtgi-guide-history samples (4,096).
    private int rtgiGuideSlotsLog2 = 18;
    private float rtgiGuideHistory = 4096f;
    private GpuBufferHandle rtgiGuideKeys, rtgiGuideAccum, rtgiGuideDist;
    private ShaderInterface rtgiGuideResolveInterface = null!;
    private PassHandle rtgiGuideResolvePassHandle;
    private PipelineHandle rtgiGuideResolvePipeline;
    private int RtgiGuideSlots => 1 << rtgiGuideSlotsLog2;
    // --rtgi-adaptive S: that share of the visible points placed by the cache's uncertainty, each the likeliest of
    // --rtgi-adaptive-k tiles (0.5, 4; 0: a tile each, rtgi_rays.comp).
    private float rtgiAdaptive = 1f, rtgiAdaptiveK = 4f;
    // When a cell is done (rtgi_filter.comp, the blend judging): its shown light's relative standard error under
    // --rtgi-done-error (0.02) against at least --rtgi-dark-floor (0.002), and no drift at the last check, every
    // --rtgi-done-window frames it is trained (60). A done cell keeps 1 in --rtgi-trickle of its points' rays (8).
    // --rtgi-view-done paints the bounce light green where done, red where open.
    // 0.5% (2026-10-26): at 2% "done" was the noise left -- Cornell's two-seed noise 1.5%, p90 3.8% with 87% of the visible
    // done -- and it showed on a smooth wall (the user).
    private float rtgiDoneError = 0.005f, rtgiDarkFloor = 0.002f, rtgiDoneWindow = 60f, rtgiTrickle = 8f;
    private bool rtgiViewDone;
    private GpuBufferHandle rtgiConfidence, rtgiDoneCount;
    private bool rtgiNoGradient;
    // The neighbour blend (rtgi_filter.comp): the neighbours' share falls from 1 at --rtgi-filter-from samples to 0 at
    // --rtgi-filter-to (32, 256), stopped beyond --rtgi-filter-sigma (2) of the cell's spread; --rtgi-no-filter: none.
    private float rtgiFilterFrom = 32f, rtgiFilterTo = 256f, rtgiFilterSigma = 2f;
    // --rtgi-parent N: the parent cell (a level up) weighed as N samples beside the blend's own (32; 0: none).
    private float rtgiParentWeight = 32f;
    // --rtgi-filter-floor F: the neighbours' share never below F, however many samples a cell holds (0: it fades to none).
    private float rtgiFilterFloor = 1f;
    // --rtgi-cascade: the parent lean takes the parent's blend (rtgi_filter.comp), cascading up every level trained;
    // --rtgi-train-levels N: the levels a sample trains, its own first (3).
    // On by default with 5 levels trained (2026-10-26): Cornell, two seeds, error at 30 / 120 frames 0.056 / 0.027 against
    // the plain blend's 0.085 / 0.038 -- the coarse levels fill fast and lend the fine a good start; later alike.
    // --rtgi-no-cascade: the parent's raw light.
    private bool rtgiCascade = true;
    private int rtgiTrainLevels = 5;
    private bool rtgiNoFilter;
    private ShaderInterface rtgiFilterInterface = null!;
    private PassHandle rtgiFilterPassHandle;
    private PipelineHandle rtgiFilterPipeline;
    // The cells training touched this frame, by parity (rtgi_train.comp), for the resolve; and the sun's turns so far, each
    // cell taking the cut for a turn when next trained (rtgi_resolve.comp). A point touches at most three cells.
    private GpuBufferHandle[] rtgiDirtyBufs = Array.Empty<GpuBufferHandle>();
    private uint rtgiSunEpoch;
    private int RtgiDirtyRoom => RtgiPoints * rtgiTrainLevels + (rtgiContact ? rtgiContactRays : 0);
    // S4 contact detail (rtgi_contact_rays.comp, rtgi_contact_train.comp, rtgi_readout.frag): one half-res pixel in
    // --rtgi-contact-every (8) a frame, where its cell's near visibility is under 0.97, casts a short ray of
    // --rtgi-contact-radius (0.25 m), at most --rtgi-contact-rays (65,536) a frame, into contact cells a quarter the size;
    // the readout splits the light into far and near and scales each by its visibility against the cell's, clamped to
    // [0.3, 1.25]. Off by default (--rtgi-contact): measured slightly worse than without -- the gate's seams 0.063 / 0.975
    // against 0.043 / 1.018, Cornell's spotting 0.99% against 0.90% (2026-10-26).
    private bool rtgiContact;
    private int rtgiContactRays = 65_536, rtgiContactEvery = 8;
    private float rtgiContactRadius = 0.25f;
    private ShaderInterface rtgiContactRaysInterface = null!, rtgiContactTrainInterface = null!;
    private PassHandle rtgiContactRaysPassHandle, rtgiContactTrainPassHandle;
    private PipelineHandle rtgiContactRaysPipeline, rtgiContactTrainPipeline;
    private GpuBufferHandle[] rtgiContactCount = Array.Empty<GpuBufferHandle>(), rtgiContactOrigins = Array.Empty<GpuBufferHandle>(),
        rtgiContactDirs = Array.Empty<GpuBufferHandle>(), rtgiContactPoints = Array.Empty<GpuBufferHandle>(), rtgiContactOccluded = Array.Empty<GpuBufferHandle>();
    private ShaderInterface rtgiEvictInterface = null!;
    private PassHandle rtgiEvictPassHandle;
    private PipelineHandle rtgiEvictPipeline;
    // The eviction's slice: a sixteenth of the slots a frame.
    private const int RtgiEvictSlices = 16;
    private uint rtgiFrameCount;
    private long rtgiTraced;

    private int RtgiPoints => rtgiTiles + rtgiHitPoints;
    // --rtgi-seed N: every random choice of the cache's (its rays' directions and pixels, the tiles placed, the guide, the
    // Metal batches' seeds) offset -- two runs differing only in it measure what noise is left (tools/gi-model/
    // rtgi_timeline.py --pair). The frame's own count (stamps, windows) stays as it is.
    private uint rtgiSeed;
    // --rtgi-hit-sun S: the direct sun at a training ray's hit scaled by S (1; 0: none) -- a diagnostic.
    private float rtgiHitSun = 1f;
    private float RtgiRandomFrame => (rtgiFrameCount + rtgiSeed * 1_000_003u) % (1u << 24);
    private int RtgiSlots => 1 << rtgiSlotsLog2;
    private Vector4 RtgiParams => new(rtgiCell, rtgiCellGrow, RtgiSlots - 1, RtgiTopLevel);

    private void ParseRtgi(Blix.Core.AppArgs args)
    {
        rtgi = args.Flag("rtgi");
        if (args.Int("rtgi-seed") is { } sd) rtgiSeed = (uint)Math.Max(0, sd);
        if (args.Float("rtgi-hit-sun") is { } hs) rtgiHitSun = MathF.Max(0f, hs);
        if (args.Int("rtgi-tiles") is { } t) rtgiTiles = Math.Max(1, t);
        if (args.Int("rtgi-hits") is { } h) rtgiHitPoints = Math.Max(0, h);
        if (args.Int("rtgi-slots-log2") is { } sl) rtgiSlotsLog2 = Math.Clamp(sl, 16, 24);
        if (args.Float("rtgi-cell") is { } c) rtgiCell = MathF.Max(0.005f, c);
        if (args.Float("rtgi-cell-grow") is { } g) rtgiCellGrow = MathF.Max(0f, g);
        if (args.Float("rtgi-near") is { } nf) rtgiNearField = MathF.Max(0f, nf);
        if (args.Float("rtgi-history") is { } hi) rtgiHistory = MathF.Max(1f, hi);
        if (args.Float("rtgi-max-age") is { } a) rtgiMaxAge = MathF.Max(1f, a);
        if (args.Int("rtgi-settle-frames") is { } sf) rtgiSettleFrames = Math.Max(0, sf);
        if (args.Float("rtgi-change-sigma") is { } cs) rtgiChangeSigma = MathF.Max(0.5f, cs);
        if (args.Float("rtgi-fast") is { } fa) rtgiFast = MathF.Max(1f, fa);
        if (args.Float("rtgi-age") is { } ag) rtgiAge = Math.Clamp(ag, 0f, 1f);
        if (args.Float("rtgi-age-floor") is { } af) rtgiAgeFloor = MathF.Max(1f, af);
        if (args.Float("rtgi-change-floor") is { } cf) rtgiChangeFloor = MathF.Max(0f, cf);
        if (args.Float("rtgi-change-frames") is { } cfr) rtgiChangeFrames = MathF.Max(1f, cfr);
        if (args.Float("rtgi-sun-cut") is { } sc) rtgiSunCut = MathF.Max(0f, sc);
        rtgiCountUnknown = args.Flag("rtgi-count-unknown");
        rtgiNoCoarse = args.Flag("rtgi-no-coarse");
        rtgiBackfaceLit = args.Flag("rtgi-backface-lit");
        if (args.Flag("rtgi-no-requests")) rtgiRequests = false;
        fixedSubpixel = args.Flag("fixed-subpixel");
        if (args.Float("rtgi-clamp-sigma") is { } cls) rtgiClampSigma = MathF.Max(0f, cls);
        if (args.Float("rtgi-filter-from") is { } ffr) rtgiFilterFrom = MathF.Max(0f, ffr);
        if (args.Float("rtgi-filter-to") is { } fto) rtgiFilterTo = MathF.Max(1f, fto);
        if (args.Float("rtgi-filter-sigma") is { } fsg) rtgiFilterSigma = MathF.Max(0.1f, fsg);
        // --rtgi-denoise: the smoothing -- the neighbour blend and parent lean (rtgi_filter.comp), the temporal accumulation
        // (SponzaLoop.RtgiTemporal.cs). Off by default (2026-10-26): what read as blotches was the cache's long tail, not
        // noise (the user) -- the convergence is judged without it, the smoothing on its own.
        // The blend is shown by default again (2026-10-26): measured as convergence, not cosmetics -- Cornell, two seeds:
        // noise left halved at every frame (1.3% against 2.4% at 960), error against the truth lower (0.021 against 0.029).
        // --rtgi-no-filter: the raw cells shown (the blend still judging when they are done).
        args.Flag("rtgi-denoise");
        rtgiNoFilter = args.Flag("rtgi-no-filter");
        if (args.Float("rtgi-guide") is { } gd) rtgiGuide = Math.Clamp(gd, 0f, 0.95f);
        if (args.Float("rtgi-guide-min") is { } gm) rtgiGuideMin = MathF.Max(1f, gm);
        if (args.Float("rtgi-guide-kappa") is { } gk) rtgiGuideKappa = Math.Clamp(gk, 1f, 2000f);
        if (args.Int("rtgi-guide-slots-log2") is { } gsl) rtgiGuideSlotsLog2 = Math.Clamp(gsl, 12, 22);
        if (args.Float("rtgi-guide-history") is { } gh) rtgiGuideHistory = MathF.Max(16f, gh);
        rtgiNoGradient = args.Flag("rtgi-no-gradient");
        if (args.Float("rtgi-adaptive") is { } ad) rtgiAdaptive = Math.Clamp(ad, 0f, 1f);
        if (args.Float("rtgi-adaptive-k") is { } ak) rtgiAdaptiveK = Math.Clamp(ak, 1f, 16f);
        if (args.Float("rtgi-done-error") is { } de) rtgiDoneError = MathF.Max(1e-4f, de);
        if (args.Float("rtgi-dark-floor") is { } df) rtgiDarkFloor = MathF.Max(1e-6f, df);
        if (args.Float("rtgi-done-window") is { } dw) rtgiDoneWindow = MathF.Max(1f, dw);
        if (args.Float("rtgi-trickle") is { } tr) rtgiTrickle = MathF.Max(1f, tr);
        rtgiViewDone = args.Flag("rtgi-view-done");
        if (args.Flag("rtgi-contact")) rtgiContact = true;
        if (args.Flag("rtgi-no-contact")) rtgiContact = false;
        if (args.Int("rtgi-contact-rays") is { } cr) rtgiContactRays = Math.Max(1, cr);
        if (args.Int("rtgi-contact-every") is { } ce) rtgiContactEvery = Math.Max(1, ce);
        if (args.Float("rtgi-contact-radius") is { } crad) rtgiContactRadius = MathF.Max(0.01f, crad);
        if (args.Float("rtgi-parent") is { } pw) rtgiParentWeight = MathF.Max(0f, pw);
        if (args.Float("rtgi-filter-floor") is { } ffl) rtgiFilterFloor = Math.Clamp(ffl, 0f, 1f);
        if (args.Flag("rtgi-no-cascade")) rtgiCascade = false;
        args.Flag("rtgi-cascade");
        if (args.Int("rtgi-train-levels") is { } tl) rtgiTrainLevels = Math.Clamp(tl, 1, 8);
        rtgiScreen = args.Flag("rtgi-screen");
        if (rtgiScreen) rtgi = true;
        if (args.Int("rtgi-screen-taps") is { } taps) rtgiScreenTaps = Math.Clamp(taps, 1, 64);
        if (args.Int("rtgi-screen-radius") is { } rad) rtgiScreenRadius = Math.Clamp(rad, 0, 16);
        if (args.Float("rtgi-reuse-max") is { } rm) rtgiReuseMax = MathF.Max(0f, rm);
        if (args.String("rtgi-dump") is { } dump) rtgiDumpDir = dump;
        if (args.String("rtgi-dump-frames") is { } frames)
            rtgiDumpFrames = new SortedSet<int>(frames.Split(',').Select(f => int.Parse(f, CultureInfo.InvariantCulture)));
        // The cache traces its own occlusion, and the Metal tracer answers its rays: the traced shadows build it.
        if (rtgi) rtShadows = true;
    }

    // Setup: the compute passes before the traced shadows' group that takes their batches.
    private void DeclareRtgiComputePasses(Func<string, ShaderInterface> reflect)
    {
        rtgiTrainInterface = reflect("rtgi_train.comp");
        rtgiTrainPassHandle = graph.ComputePass("rtgi-train").Shader(rtgiTrainInterface).Handle;
        rtgiContactTrainInterface = reflect("rtgi_contact_train.comp");
        rtgiContactTrainPassHandle = graph.ComputePass("rtgi-contact-train").Shader(rtgiContactTrainInterface).Handle;
        rtgiResolveInterface = reflect("rtgi_resolve.comp");
        rtgiResolvePassHandle = graph.ComputePass("rtgi-resolve").Shader(rtgiResolveInterface).Handle;
        rtgiGuideResolveInterface = reflect("rtgi_guide_resolve.comp");
        rtgiGuideResolvePassHandle = graph.ComputePass("rtgi-guide-resolve").Shader(rtgiGuideResolveInterface).Handle;
        rtgiFilterInterface = reflect("rtgi_filter.comp");
        rtgiFilterPassHandle = graph.ComputePass("rtgi-filter").Shader(rtgiFilterInterface).Handle;
        rtgiEvictInterface = reflect("rtgi_evict.comp");
        rtgiEvictPassHandle = graph.ComputePass("rtgi-evict").Shader(rtgiEvictInterface).Handle;
        rtgiReservoirInterface = reflect("rtgi_reservoir.comp");
        rtgiReservoirPassHandle = graph.ComputePass("rtgi-reservoir").Shader(rtgiReservoirInterface).Handle;
        rtgiSpatialInterface = reflect("rtgi_spatial.comp");
        rtgiSpatialPassHandle = graph.ComputePass("rtgi-spatial").Shader(rtgiSpatialInterface).Handle;
        rtgiRaysInterface = reflect("rtgi_rays.comp");
        rtgiRaysPassHandle = graph.ComputePass("rtgi-rays").Shader(rtgiRaysInterface)
            .Read(SampleableSceneDepth).Read(SampleablePrepassNormal).Handle;
        rtgiContactRaysInterface = reflect("rtgi_contact_rays.comp");
        rtgiContactRaysPassHandle = graph.ComputePass("rtgi-contact-rays").Shader(rtgiContactRaysInterface)
            .Read(SampleableSceneDepth).Read(SampleablePrepassNormal).Handle;
    }

    // Setup: the readout into the half-res incident targets, beside incident-light-half (a frame records one of them).
    private void DeclareRtgiReadoutPass(Func<string, string, ShaderInterface> reflect)
    {
        rtgiReadoutInterface = reflect("present.vert", "rtgi_readout.frag");
        rtgiReadoutPassHandle = graph.GraphicsPass("rtgi-readout")
            .Target(incidentHalfHandle, LoadOp.Clear, StoreOp.Store)
            .Target(incidentGradientHalfHandle, LoadOp.Clear, StoreOp.Store)
            .Read(SampleableSceneDepth)
            .Read(SampleablePrepassNormal)
            .WritesGpuBuffers()
            .Shader(rtgiReadoutInterface)
            .Handle;
        rtgiScreenInterface = reflect("present.vert", "rtgi_screen.frag");
        rtgiScreenPassHandle = graph.GraphicsPass("rtgi-screen")
            .Target(incidentHalfHandle, LoadOp.Clear, StoreOp.Store)
            .Target(incidentGradientHalfHandle, LoadOp.Clear, StoreOp.Store)
            .Read(SampleableSceneDepth)
            .Read(SampleablePrepassNormal)
            .Shader(rtgiScreenInterface)
            .Handle;
    }

    private void CreateRtgiPipelines(string shaderDir, byte[] presentVertSpv)
    {
        PipelineHandle Compute(string name, ShaderInterface iface) => Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, name + ".comp.spv")), iface, name)), name));
        rtgiTrainPipeline = Compute("rtgi_train", rtgiTrainInterface);
        rtgiResolvePipeline = Compute("rtgi_resolve", rtgiResolveInterface);
        rtgiEvictPipeline = Compute("rtgi_evict", rtgiEvictInterface);
        rtgiFilterPipeline = Compute("rtgi_filter", rtgiFilterInterface);
        rtgiGuideResolvePipeline = Compute("rtgi_guide_resolve", rtgiGuideResolveInterface);
        rtgiContactRaysPipeline = Compute("rtgi_contact_rays", rtgiContactRaysInterface);
        rtgiContactTrainPipeline = Compute("rtgi_contact_train", rtgiContactTrainInterface);
        rtgiRaysPipeline = Compute("rtgi_rays", rtgiRaysInterface);
        rtgiReadoutPipeline = Pipeline(Own(device.CreateShaderProgramFromSpv(
                presentVertSpv, File.ReadAllBytes(Path.Combine(shaderDir, "rtgi_readout.frag.spv")), rtgiReadoutInterface, "rtgi_readout")),
            VertexPosition3NormalTexture.Layout, DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled, BlendState.Disabled }, rtgiReadoutPassHandle, "rtgi_readout");
        rtgiReservoirPipeline = Compute("rtgi_reservoir", rtgiReservoirInterface);
        rtgiSpatialPipeline = Compute("rtgi_spatial", rtgiSpatialInterface);
        rtgiScreenPipeline = Pipeline(Own(device.CreateShaderProgramFromSpv(
                presentVertSpv, File.ReadAllBytes(Path.Combine(shaderDir, "rtgi_screen.frag.spv")), rtgiScreenInterface, "rtgi_screen")),
            VertexPosition3NormalTexture.Layout, DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled, BlendState.Disabled }, rtgiScreenPassHandle, "rtgi_screen");
    }

    // After the traced shadows are recorded: train, resolve, this frame's rays and their batches.
    private void RecordRtgi(int width, int height)
    {
        if (!rtgi || frameTracer is not { } tracer) return;
        var n = RtgiPoints;
        if (rtgiKeys.Equals(default(GpuBufferHandle)))
        {
            GpuBufferHandle[] Two(int bytes, string name) =>
                Enumerable.Range(0, 2).Select(s => Own(device.CreateGpuBuffer(bytes, name: $"sponza.rtgi.{name}{s}"))).ToArray();
            rtgiPointsBuf = Two(n * 32, "points");
            rtgiRaysBuf = Two(n * 32, "rays");
            rtgiHitsBuf = Two(n * 48, "hits");
            rtgiOriginsBuf = Two(n * 16, "origins");
            rtgiOccludedBuf = Two(n * 4, "occluded");
            rtgiRequestBufs = Enumerable.Range(0, 2).Select(s => Own(device.CreateGpuBuffer(16 + Math.Max(1, rtgiHitPoints) * 32, new byte[16 + Math.Max(1, rtgiHitPoints) * 32], $"sponza.rtgi.requests{s}"))).ToArray();
            rtgiKeys = Own(device.CreateGpuBuffer(RtgiSlots * 4, new byte[RtgiSlots * 4], "sponza.rtgi.keys"));
            rtgiAccum = Own(device.CreateGpuBuffer(RtgiSlots * 52, new byte[RtgiSlots * 52], "sponza.rtgi.accum"));
            rtgiDirection = Own(device.CreateGpuBuffer(RtgiSlots * 16, new byte[RtgiSlots * 16], "sponza.rtgi.direction"));
            rtgiConfidence = Own(device.CreateGpuBuffer(RtgiSlots * 16, new byte[RtgiSlots * 16], "sponza.rtgi.confidence"));
            rtgiGuideKeys = Own(device.CreateGpuBuffer(RtgiGuideSlots * 4, new byte[RtgiGuideSlots * 4], "sponza.rtgi.guide-keys"));
            rtgiGuideAccum = Own(device.CreateGpuBuffer(RtgiGuideSlots * 33 * 4, new byte[RtgiGuideSlots * 33 * 4], "sponza.rtgi.guide-accum"));
            rtgiGuideDist = Own(device.CreateGpuBuffer(RtgiGuideSlots * 9 * 16, new byte[RtgiGuideSlots * 9 * 16], "sponza.rtgi.guide-dist"));
            rtgiDoneCount = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.rtgi.done-count"));
            rtgiResolved = Own(device.CreateGpuBuffer(RtgiSlots * 32, new byte[RtgiSlots * 32], "sponza.rtgi.resolved"));
            rtgiMoments = Own(device.CreateGpuBuffer(RtgiSlots * 48, new byte[RtgiSlots * 48], "sponza.rtgi.moments"));
            rtgiStamps = Own(device.CreateGpuBuffer(RtgiSlots * 8, new byte[RtgiSlots * 8], "sponza.rtgi.stamps"));
            rtgiCells = Own(device.CreateGpuBuffer(RtgiSlots * 16, new byte[RtgiSlots * 16], "sponza.rtgi.cells"));
            GpuBufferHandle[] Pair(int bytes, string name) =>
                Enumerable.Range(0, 2).Select(s => Own(device.CreateGpuBuffer(bytes, new byte[bytes], $"sponza.rtgi.contact-{name}{s}"))).ToArray();
            rtgiContactCount = Pair(16, "count");
            rtgiContactOrigins = Pair(rtgiContactRays * 16, "origins");
            rtgiContactDirs = Pair(rtgiContactRays * 16, "dirs");
            rtgiContactPoints = Pair(rtgiContactRays * 32, "points");
            rtgiContactOccluded = Pair(rtgiContactRays * 4, "occluded");
            rtgiFiltered = Own(device.CreateGpuBuffer(RtgiSlots * 16, new byte[RtgiSlots * 16], "sponza.rtgi.filtered"));
            rtgiDirtyBufs = Enumerable.Range(0, 2).Select(s => Own(device.CreateGpuBuffer(16 + RtgiDirtyRoom * 4, new byte[16 + RtgiDirtyRoom * 4], $"sponza.rtgi.dirty{s}"))).ToArray();
            if (rtgiScreen)
            {
                GpuBufferHandle[] Twin(int bytes, string name) =>
                    Enumerable.Range(0, 2).Select(s => Own(device.CreateGpuBuffer(bytes, new byte[bytes], $"sponza.rtgi.{name}{s}"))).ToArray();
                rtgiReservoirs = Twin(rtgiTiles * 64, "reservoirs");
                rtgiProposals = Twin(rtgiTiles * 48, "proposals");
                rtgiSpatialOrigins = Twin(rtgiTiles * 16, "spatial-origins");
                rtgiSpatialDirs = Twin(rtgiTiles * 16, "spatial-dirs");
                rtgiSpatialOccluded = Twin(rtgiTiles * 4, "spatial-occluded");
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] rtgi: {rtgiTiles:N0} visible points and {rtgiHitPoints:N0} from last frame's hits a frame; cache {RtgiSlots:N0} slots ({RtgiSlots * 68 / 1048576.0:0} MB), cells {rtgiCell:0.###} m + {rtgiCellGrow:0.###} per metre, {rtgiHistory:0} samples held, untrained cells emptied after {rtgiMaxAge:0} frames."));
        }
        var cur = (int)(rtgiFrameCount & 1u);
        var prev = 1 - cur;
        var towardSun = -Vector3.Normalize(sunDirection);
        var groups = (n + 63) / 64;
        var sunTurned = rtgiTraced > 0 && Vector3.Dot(towardSun, rtgiLastSun) < MathF.Cos(0.02f * MathF.PI / 180f);
        rtgiLastSun = towardSun;
        if (sunTurned && rtgiSunCut > 0f) rtgiSunEpoch++;
        var frameNumber = rtgiFrameCount + 1u;
        if (rtgiTraced > 0)
            graph.Dispatch(rtgiTrainPassHandle, new DispatchCommand(rtgiTrainPipeline, groups, 1, 1,
                new ShaderUniform[]
                {
                    new("uParams", new Vector4Uniform(RtgiParams)),
                    new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, n))),
                    new("uSun", new Vector4Uniform(new Vector4(towardSun, 1f))),
                    new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
                    new("uNearField", new Vector4Uniform(new Vector4(rtgiNearField, rtgiContactRadius, rtgiHitSun, rtgiTrainLevels))),
                    new("uClamp", new Vector4Uniform(new Vector4(rtgiClampSigma, frameNumber, RtgiDirtyRoom, 0f))),
                    new("uGuideTable", new Vector4Uniform(new Vector4(rtgiGuide > 0f ? RtgiGuideSlots - 1 : 0f, 0f, 0f, 0f))),
                    new("uTrain", new Vector4Uniform(new Vector4(rtgiCountUnknown ? 1f : 0f, rtgiNoCoarse ? 1f : 0f, rtgiBackfaceLit ? 1f : 0f, rtgiRequests ? rtgiHitPoints : 0f))),
                },
                new[] { new ShaderTextureBinding("uSkyRadiance", envCubeTexture) },
                Buffers: new[]
                {
                    new ShaderBufferBinding("RtgiPoints", rtgiPointsBuf[prev]),
                    new ShaderBufferBinding("RtgiRayBuffer", rtgiRaysBuf[prev]),
                    new ShaderBufferBinding("RtgiHits", rtgiHitsBuf[prev]),
                    new ShaderBufferBinding("RtgiOccluded", rtgiOccludedBuf[prev]),
                    new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                    new ShaderBufferBinding("RtgiAccum", rtgiAccum),
                    new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                    new ShaderBufferBinding("RtgiRequests", rtgiRequestBufs[cur]),
                    new ShaderBufferBinding("RtgiMoments", rtgiMoments),
                    new ShaderBufferBinding("RtgiStamps", rtgiStamps),
                    new ShaderBufferBinding("RtgiDirty", rtgiDirtyBufs[cur]),
                    new ShaderBufferBinding("RtgiCells", rtgiCells),
                    new ShaderBufferBinding("RtgiFiltered", rtgiFiltered),
                    new ShaderBufferBinding("GuideKeys", rtgiGuideKeys),
                    new ShaderBufferBinding("GuideAccum", rtgiGuideAccum),
                }));
        // Path guiding: each guide cell's frame of samples into its bins.
        graph.Dispatch(rtgiGuideResolvePassHandle, new DispatchCommand(rtgiGuideResolvePipeline, (RtgiGuideSlots + 63) / 64, 1, 1,
            new ShaderUniform[] { new("uGuideResolve", new Vector4Uniform(new Vector4(rtgiGuideHistory, RtgiGuideSlots, 0f, 0f))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("GuideKeys", rtgiGuideKeys),
                new ShaderBufferBinding("GuideAccum", rtgiGuideAccum),
                new ShaderBufferBinding("GuideDist", rtgiGuideDist),
            }));
        // Last frame's contact rays into the contact cells (S4), listed for the resolve too.
        if (rtgiContact)
            graph.Dispatch(rtgiContactTrainPassHandle, new DispatchCommand(rtgiContactTrainPipeline, (rtgiContactRays + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uParams", new Vector4Uniform(RtgiParams)),
                    new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                    new("uNearField", new Vector4Uniform(new Vector4(rtgiNearField, 0f, 0f, 0f))),
                    new("uList", new Vector4Uniform(new Vector4(frameNumber, RtgiDirtyRoom, rtgiContactRays, 0f))),
                },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new[]
                {
                    new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                    new ShaderBufferBinding("RtgiAccum", rtgiAccum),
                    new ShaderBufferBinding("RtgiStamps", rtgiStamps),
                    new ShaderBufferBinding("RtgiDirty", rtgiDirtyBufs[cur]),
                    new ShaderBufferBinding("RtgiCells", rtgiCells),
                    new ShaderBufferBinding("RtgiContactCount", rtgiContactCount[prev]),
                    new ShaderBufferBinding("RtgiContactPoints", rtgiContactPoints[prev]),
                    new ShaderBufferBinding("RtgiContactOccluded", rtgiContactOccluded[prev]),
                    new ShaderBufferBinding("RtgiNextContactCount", rtgiContactCount[cur]),
                }));
        // The cells trained this frame (the list's room dispatched; past its count each thread returns).
        graph.Dispatch(rtgiResolvePassHandle, new DispatchCommand(rtgiResolvePipeline, (RtgiDirtyRoom + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uResolve", new Vector4Uniform(new Vector4(rtgiHistory, rtgiMaxAge, RtgiSlots, rtgiChangeSigma))),
                new("uChange", new Vector4Uniform(new Vector4(rtgiChangeFrames, rtgiFast, rtgiChangeFloor, rtgiSunCut))),
                new("uAge", new Vector4Uniform(new Vector4(rtgiAge, rtgiAgeFloor, 0f, 0f))),
                new("uList", new Vector4Uniform(new Vector4(rtgiSunEpoch, RtgiDirtyRoom, 0f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                new ShaderBufferBinding("RtgiAccum", rtgiAccum),
                new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                new ShaderBufferBinding("RtgiMoments", rtgiMoments),
                new ShaderBufferBinding("RtgiStamps", rtgiStamps),
                new ShaderBufferBinding("RtgiDirty", rtgiDirtyBufs[cur]),
                new ShaderBufferBinding("RtgiNextDirty", rtgiDirtyBufs[prev]),
                new ShaderBufferBinding("RtgiDirection", rtgiDirection),
            }));
        // The cells just resolved, blended with their neighbours into what the readout and lookups read (a copy alone with
        // --rtgi-no-filter).
        graph.Dispatch(rtgiFilterPassHandle, new DispatchCommand(rtgiFilterPipeline, (RtgiDirtyRoom + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uParams", new Vector4Uniform(RtgiParams)),
                // The blend always runs -- it judges when a cell is done -- and is shown only with --rtgi-denoise.
                new("uFilter", new Vector4Uniform(new Vector4(rtgiFilterFrom, rtgiFilterTo, rtgiFilterSigma, RtgiDirtyRoom))),
                new("uParent", new Vector4Uniform(new Vector4(rtgiParentWeight, RtgiTopLevel, rtgiFilterFloor, rtgiNoFilter ? 0f : 1f))),
                new("uFilter2", new Vector4Uniform(new Vector4(rtgiCascade ? 1f : 0f, 0f, 0f, 0f))),
                new("uDone", new Vector4Uniform(new Vector4(frameNumber, rtgiDoneWindow, rtgiDoneError, rtgiDarkFloor))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                new ShaderBufferBinding("RtgiMoments", rtgiMoments),
                new ShaderBufferBinding("RtgiCells", rtgiCells),
                new ShaderBufferBinding("RtgiDirty", rtgiDirtyBufs[cur]),
                new ShaderBufferBinding("RtgiFiltered", rtgiFiltered),
                new ShaderBufferBinding("RtgiConfidence", rtgiConfidence),
            }));
        // A sixteenth of the slots a frame: those no training reached for --rtgi-max-age frames emptied.
        var slice = RtgiSlots / RtgiEvictSlices;
        graph.Dispatch(rtgiEvictPassHandle, new DispatchCommand(rtgiEvictPipeline, (slice + 63) / 64, 1, 1,
            new ShaderUniform[] { new("uEvict", new Vector4Uniform(new Vector4(frameNumber, rtgiMaxAge, (int)(rtgiFrameCount % RtgiEvictSlices) * slice, slice))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                new ShaderBufferBinding("RtgiMoments", rtgiMoments),
                new ShaderBufferBinding("RtgiStamps", rtgiStamps),
                new ShaderBufferBinding("RtgiDirection", rtgiDirection),
                new ShaderBufferBinding("RtgiConfidence", rtgiConfidence),
            }));

        // S3: last frame's visible points (tile i is point i) into this frame's reservoirs, merged with the frame before's.
        if (rtgiScreen && rtgiTraced > 0)
        {
            var tiles = Math.Min(rtgiTiles, rtgiGrid.X * rtgiGrid.Y);
            graph.Dispatch(rtgiReservoirPassHandle, new DispatchCommand(rtgiReservoirPipeline, (tiles + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uPrevViewProj", new Matrix4x4Uniform(rtgiViewProj2)),
                    new("uParams", new Vector4Uniform(RtgiParams)),
                    new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, tiles))),
                    new("uSun", new Vector4Uniform(new Vector4(towardSun, 1f))),
                    new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
                    new("uGrid", new Vector4Uniform(new Vector4(rtgiGrid.X, rtgiGrid.Y, width, height))),
                    new("uReuse", new Vector4Uniform(new Vector4(rtgiReuseMax, sunTurned ? 1f : 0f, RtgiRandomFrame, rtgiTraced > 1 && rtgiReuseMax > 0f ? 1f : 0f))),
                    new("uSpatialMerge", new Vector4Uniform(new Vector4(rtgiTraced > 2 ? 1f : 0f, sunTurned ? 1f : 0f, 0f, 0f))),
                },
                new[] { new ShaderTextureBinding("uSkyRadiance", envCubeTexture) },
                Buffers: new[]
                {
                    new ShaderBufferBinding("RtgiPoints", rtgiPointsBuf[prev]),
                    new ShaderBufferBinding("RtgiRayBuffer", rtgiRaysBuf[prev]),
                    new ShaderBufferBinding("RtgiHits", rtgiHitsBuf[prev]),
                    new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                    new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                    new ShaderBufferBinding("RtgiPrevReservoirs", rtgiReservoirs[prev]),
                    new ShaderBufferBinding("RtgiReservoirs", rtgiReservoirs[cur]),
                    new ShaderBufferBinding("RtgiProposals", rtgiProposals[prev]),
                    new ShaderBufferBinding("RtgiSpatialOccluded", rtgiSpatialOccluded[prev]),
                }));
            // Each tile's proposal from a neighbour, its ray cast in this frame's group, merged next frame where it arrived.
            graph.Dispatch(rtgiSpatialPassHandle, new DispatchCommand(rtgiSpatialPipeline, (tiles + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uGrid", new Vector4Uniform(new Vector4(rtgiGrid.X, rtgiGrid.Y, tiles, RtgiRandomFrame))),
                    new("uSpatial", new Vector4Uniform(new Vector4(3f, rayReach, rtgiReuseMax, 0f))),
                    new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new[]
                {
                    new ShaderBufferBinding("RtgiReservoirs", rtgiReservoirs[cur]),
                    new ShaderBufferBinding("RtgiProposals", rtgiProposals[cur]),
                    new ShaderBufferBinding("RtgiSpatialOrigins", rtgiSpatialOrigins[cur]),
                    new ShaderBufferBinding("RtgiSpatialDirs", rtgiSpatialDirs[cur]),
                }));
            rtgiSpatialTiles = tiles;
            rtgiReservoirCur = cur;
            rtgiGridViewProj = rtgiViewProj1;
        }

        var tilesX = Math.Max(1, (int)MathF.Round(MathF.Sqrt(rtgiTiles * (float)width / height)));
        var tilesY = Math.Max(1, (rtgiTiles + tilesX - 1) / tilesX);
        // The visible points are as many tiles as the grid has (a whole grid, so every row is covered).
        Matrix4x4.Invert(viewProjJittered, out var invViewProj);
        graph.Dispatch(rtgiRaysPassHandle, new DispatchCommand(rtgiRaysPipeline, groups, 1, 1,
            new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                new("uSize", new Vector4Uniform(new Vector4(width, height, tilesX, tilesY))),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                new("uCounts", new Vector4Uniform(new Vector4(Math.Min(rtgiTiles, tilesX * tilesY), rtgiHitPoints, RtgiRandomFrame, rayReach))),
                new("uSun", new Vector4Uniform(new Vector4(towardSun, rtgiTraced > 0 ? 1f : 0f))),
                new("uParams", new Vector4Uniform(RtgiParams)),
                new("uNearField", new Vector4Uniform(new Vector4(rtgiNearField, 0f, 0f, 0f))),
                new("uGuide", new Vector4Uniform(new Vector4(rtgiGuide, rtgiGuideMin, rtgiGuideKappa, RtgiGuideSlots - 1))),
                new("uAdapt", new Vector4Uniform(new Vector4(rtgiAdaptive, rtgiAdaptiveK, rtgiDoneError, rtgiTrickle))),
            },
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
            },
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiPrevRays", rtgiRaysBuf[prev]),
                new ShaderBufferBinding("RtgiPrevHits", rtgiHitsBuf[prev]),
                new ShaderBufferBinding("RtgiPoints", rtgiPointsBuf[cur]),
                new ShaderBufferBinding("RtgiRayBuffer", rtgiRaysBuf[cur]),
                new ShaderBufferBinding("RtgiShadowOrigins", rtgiOriginsBuf[cur]),
                new ShaderBufferBinding("RtgiRequests", rtgiRequestBufs[cur]),
                new ShaderBufferBinding("RtgiNextRequests", rtgiRequestBufs[prev]),
                new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                new ShaderBufferBinding("RtgiDirection", rtgiDirection),
                new ShaderBufferBinding("RtgiMoments", rtgiMoments),
                new ShaderBufferBinding("RtgiConfidence", rtgiConfidence),
                new ShaderBufferBinding("GuideKeys", rtgiGuideKeys),
                new ShaderBufferBinding("GuideDist", rtgiGuideDist),
            }));

        // This frame's contact rays (S4), into the same Metal group.
        var (halfW, halfH) = HalfLightingSize(width, height);
        if (rtgiContact)
            graph.Dispatch(rtgiContactRaysPassHandle, new DispatchCommand(rtgiContactRaysPipeline, (halfW + 7) / 8, (halfH + 7) / 8, 1,
                new ShaderUniform[]
                {
                    new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                    new("uSize", new Vector4Uniform(new Vector4(width, height, halfW, halfH))),
                    new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                    new("uParams", new Vector4Uniform(RtgiParams)),
                    new("uNearField", new Vector4Uniform(new Vector4(rtgiNearField, rtgiContactRadius, 0f, 0f))),
                    new("uContact", new Vector4Uniform(new Vector4(rtgiContactEvery, rtgiContactRays, RtgiRandomFrame, 0.97f))),
                },
                new[]
                {
                    new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                    new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                },
                Buffers: new[]
                {
                    new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                    new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                    new ShaderBufferBinding("RtgiDirection", rtgiDirection),
                    new ShaderBufferBinding("RtgiContactCount", rtgiContactCount[cur]),
                    new ShaderBufferBinding("RtgiContactOrigins", rtgiContactOrigins[cur]),
                    new ShaderBufferBinding("RtgiContactDirs", rtgiContactDirs[cur]),
                    new ShaderBufferBinding("RtgiContactPoints", rtgiContactPoints[cur]),
                }));
        var contactCount = rtgiContactCount[cur];
        var contactOrigins = rtgiContactOrigins[cur];
        var contactDirs = rtgiContactDirs[cur];
        var contactOccluded = rtgiContactOccluded[cur];
        var contact = rtgiContact;
        var spatial = rtgiScreen && rtgiSpatialTiles > 0;
        var spatialTiles = rtgiSpatialTiles;
        var spatialOrigins = spatial ? rtgiSpatialOrigins[cur] : default;
        var spatialDirs = spatial ? rtgiSpatialDirs[cur] : default;
        var spatialOccluded = spatial ? rtgiSpatialOccluded[cur] : default;
        var contactRoom = rtgiContactRays;
        var rays = rtgiRaysBuf[cur];
        var hits = rtgiHitsBuf[cur];
        var origins = rtgiOriginsBuf[cur];
        var occluded = rtgiOccludedBuf[cur];
        var seed = unchecked((rtgiFrameCount + rtgiSeed * 1_000_003u) * 0x9E3779B9u + 0x2545F491u);
        void Encode(ExternalWorkSync sync, MetalRayTracer t)
        {
            t.EncodeTrace(sync, rays, 0, hits, 0, n, seed);
            t.EncodeShadow(sync, origins, occluded, 0, n, towardSun, seed, statSlot: 3);
            if (contact) t.EncodeShadow(sync, contactOrigins, contactOccluded, 0, contactRoom, towardSun, seed ^ 0x51ED270Bu, statSlot: 3,
                limit: contactCount, directions: contactDirs);
            if (spatial) t.EncodeShadow(sync, spatialOrigins, spatialOccluded, 0, spatialTiles, towardSun, seed ^ 0x2C1B3C6Du, statSlot: 3,
                directions: spatialDirs);
        }
        if (!TracerGroupsOn) graph.External(rtgiProbeTracePassHandle, sync => tracer.EncodeGroup(sync, () => Encode(sync, tracer)));
        else DeferTracerBatch(Encode);
        rtgiGrid = (tilesX, tilesY);
        rtgiViewProj2 = rtgiViewProj1;
        rtgiViewProj1 = viewProjJittered;
        rtgiFrameCount++;
        rtgiTraced++;
    }

    private void RecordRtgiReadout()
    {
        Matrix4x4.Invert(viewProjJittered, out var invViewProj);
        var sub = LightingSubpixel;
        if (rtgiScreen && rtgiReservoirCur >= 0)
        {
            graph.Pass(rtgiScreenPassHandle, scope => fullscreen.Draw(
                scope, rtgiScreenPipeline,
                new[]
                {
                    new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                    new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                },
                pushConstants: null,
                uniforms: new ShaderUniform[]
                {
                    new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                    new("uGridViewProj", new Matrix4x4Uniform(rtgiGridViewProj)),
                    new("uParams", new Vector4Uniform(RtgiParams)),
                    new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, RtgiRandomFrame))),
                    new("uHalf", new Vector4Uniform(new Vector4(0f, sub.X, sub.Y, 0f))),
                    new("uGrid", new Vector4Uniform(new Vector4(rtgiGrid.X, rtgiGrid.Y, rtgiScreenTaps, rtgiScreenRadius))),
                },
                buffers: new[]
                {
                    new ShaderBufferBinding("RtgiKeys", rtgiKeys), new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                    new ShaderBufferBinding("RtgiReservoirs", rtgiReservoirs[rtgiReservoirCur]),
                }));
            return;
        }
        graph.Pass(rtgiReadoutPassHandle, scope => fullscreen.Draw(
            scope, rtgiReadoutPipeline,
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
            },
            pushConstants: null,
            uniforms: new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                new("uParams", new Vector4Uniform(RtgiParams)),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                new("uHalf", new Vector4Uniform(new Vector4(0f, sub.X, sub.Y, 0f))),
                new("uNearField", new Vector4Uniform(new Vector4(rtgiNearField, rtgiNoGradient ? 0f : 1f, 0f, 0f))),
                new("uContact", new Vector4Uniform(new Vector4(rtgiContact ? 1f : 0f, 0.3f, 1.25f, rtgiViewDone ? 1f : 0f))),
            },
            buffers: new[]
            {
                new ShaderBufferBinding("RtgiKeys", rtgiKeys), new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                new ShaderBufferBinding("RtgiFiltered", rtgiFiltered), new ShaderBufferBinding("RtgiDirection", rtgiDirection),
                new ShaderBufferBinding("RtgiMoments", rtgiMoments),
                new ShaderBufferBinding("RtgiConfidence", rtgiConfidence),
                new ShaderBufferBinding("RtgiDoneCount", rtgiDoneCount),
            }));
    }

    // --rtgi-dump DIR --rtgi-dump-frames F1,F2,...: at those post-load frames the incident field the lit pass read
    // (full res), the pre-pass normal and the per-pixel path trace if it runs (--gi-reference), for
    // tools/gi-model/rtgi_timeline.py to hold against a truth boot's trace.
    private string? rtgiDumpDir;
    private SortedSet<int> rtgiDumpFrames = new();

    private void RtgiDumpIfDue()
    {
        if (rtgiDumpDir is null || rtgiDumpFrames.Count == 0 || postLoadFrames < rtgiDumpFrames.Min) return;
        var frame = rtgiDumpFrames.Min;
        rtgiDumpFrames.Remove(frame);
        var dir = Path.Combine(rtgiDumpDir, string.Create(CultureInfo.InvariantCulture, $"f{frame}"));
        Directory.CreateDirectory(dir);
        device.WaitIdle();
        void Texture(string name, TextureHandle handle) => File.WriteAllBytes(Path.Combine(dir, name), device.ReadTexture(handle, out _, out _, out _));
        Texture("incident.rgba16f", graph.GetColorTexture(incidentHandle));
        Texture("normal.rgba16f", graph.GetColorTexture(SampleablePrepassNormal));
        if (referenceSamples > 0)
        {
            File.WriteAllBytes(Path.Combine(dir, "reference.f32x4"), device.ReadGpuBuffer(referenceAccum, 0, referenceSize.X * referenceSize.Y * 16));
            File.WriteAllBytes(Path.Combine(dir, "reference_moments.f32"), device.ReadGpuBuffer(referenceMoments, 0, referenceSize.X * referenceSize.Y * 4));
        }
        var (w, h) = profFrameSize;
        File.WriteAllText(Path.Combine(dir, "meta.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            frame = postLoadFrames, width = w, height = h, referenceWidth = referenceSize.X, referenceHeight = referenceSize.Y,
            referenceSamples, sunToward = new[] { -sunDirection.X, -sunDirection.Y, -sunDirection.Z }, camera = new[] { cameraPosition.X, cameraPosition.Y, cameraPosition.Z },
        }));
        Console.WriteLine($"[VulkanSponza] rtgi dump at frame {postLoadFrames}: {dir}");
    }

    private bool RtgiReadoutReady => rtgi && !rtgiKeys.Equals(default(GpuBufferHandle));

    // At the profile window: how full the cache is and how much its cells hold.
    private string RtgiCounts()
    {
        if (!RtgiReadoutReady || rtgiTraced < 2) return "";
        // A thirty-second of the slots (hashed, so any run of them is a fair sample), scaled up: reading them all was ~670 MB
        // at 8 M slots, a 400 ms hitch every profile window (San Miguel, 2026-10-26).
        const int Share = 32;
        var count = RtgiSlots / Share;
        device.WaitIdle();
        var keys = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtgiKeys, 0, count * 4).AsSpan());
        var resolved = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtgiResolved, 0, count * 32).AsSpan());
        var moments = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtgiMoments, 0, count * 48).AsSpan());
        var confidence = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtgiConfidence, 0, count * 16).AsSpan());
        var seen = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtgiDoneCount, 0, 16).AsSpan()).ToArray();
        device.WriteGpuBuffer(rtgiDoneCount, 0, new byte[16]);
        double done = 0;
        double cutsLight = 0;
        int used = 0, trained = 0, bad = 0;
        double samples = 0, energy = 0;
        string firstBad = "";
        for (var s = 0; s < keys.Length; s++)
        {
            if (keys[s] == 0u) continue;
            used++;
            var r1 = resolved[2 * s + 1];
            var r0 = resolved[2 * s];
            if (!float.IsFinite(r0.X + r0.Y + r0.Z + r0.W + r1.X + r1.Y + r1.Z + r1.W))
            {
                if (bad++ == 0) firstBad = string.Create(CultureInfo.InvariantCulture, $" first not finite: slot {s} {r0} {r1}");
                continue;
            }
            if (r1.Y < 0.5f) continue;
            trained++;
            samples += r1.Y;
            cutsLight += moments[3 * s].W;
            if (confidence[s].W > 0.5f) done++;
            var e = resolved[2 * s];
            energy += 0.2126 * e.X + 0.7152 * e.Y + 0.0722 * e.Z;
        }
        used *= Share;
        trained *= Share;
        bad *= Share;
        samples *= Share;
        energy *= Share;
        cutsLight *= Share;
        done *= Share;
        return string.Create(CultureInfo.InvariantCulture,
            $"rtgi: ~{used:N0} of {RtgiSlots:N0} slots held ({100.0 * used / RtgiSlots:0.0}%), {trained:N0} trained, {samples / Math.Max(1, trained):0.0} samples a cell, {100.0 * done / Math.Max(1, trained):0.0}% of cells done, {100.0 * seen[1] / Math.Max(1u, seen[0]):0.0}% of the visible, history cut {cutsLight / Math.Max(1, trained):0.00} times a cell, mean indirect {energy / Math.Max(1, trained):0.000}; {bad:N0} not finite{firstBad}; {rtgiTraced:N0} frames traced");
    }
}
