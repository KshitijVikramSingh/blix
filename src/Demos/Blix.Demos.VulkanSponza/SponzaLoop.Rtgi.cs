using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;

namespace Blix.Demos.VulkanSponza;

// RTGI (S1 of the GI redesign, 2026-10-25), the scene's only GI: indirect light from a world radiance cache, no cook. A spatial hash of world
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
//   rtgi-readout  the cache at each half-res pixel (rtgi_readout.frag, bilinear over its level's cells) into the incident
//                 targets: the upsample brings it to the full-size ones the lit pass reads
//
// The answers arrive a frame later (the group runs beside the frame); the readout reads what the cache holds now. The
// rays are the Metal tracer's (the traced shadows', SponzaLoop.RtShadows.cs): without it -- before it is built, or a
// run where it failed -- there is no cache, and the frame has no indirect light.
internal sealed partial class SponzaLoop
{
    // 4 M slots (272 MB): San Miguel held 1.8-3.6 M cells (87% of 4 M at the old 1 cm per metre growth, its landings then
    // failing to find a slot).
    // --rtgi-tiles / --rtgi-hits: 292,000 visible points and 128,000 from last frame's hits a frame (4x the 73,000 / 32,000
    // before): with placement in blocks, Crytek Sponza 33.4 ms a frame against 29.4 at 1x; Cornell's noise left at 60
    // frames 0.70% against 1.14%, at 16.5 ms against 8.7 -- better in the same second (2026-10-26). 220,000 visible since
    // 2026-10-28: each point ~18 ns of a frame through placement, the trace and the training, 70% of 292,000 -1.55 ms (the
    // in-boot A/B), and Crytek Sponza's truth 0.133 / 0.093 / 0.090 at frames 60 / 1200 / 3000 against 0.131 / 0.092 /
    // 0.090 (~0.003 run to run). 64,000 from hits since 2026-10-28: half -1.03 ms, the still view's truth the same, a 90
    // degree turn's the same, an instant sun jump's 0.186 / 0.114 / 0.086 at 30 / 120 / 600 frames after it against 0.178 /
    // 0.110 / 0.087 (a real sun moves a few degrees at a time; the budget spends more on it, SponzaLoop.RtgiBudget.cs).
    private int rtgiTiles = 220_000, rtgiHitPoints = 64_000, rtgiSlotsLog2 = 22;
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
    // The fast mean's change test (rtgi_resolve.comp) only within --rtgi-change-window frames (600) of a known change -- the
    // sun's turn (its epoch cuts what it lit directly; the test catches the bounces still moving after it) -- else off. On
    // throughout, as before, heavy-tailed light read as change on a still Crytek Sponza view: ~3,000 cells' histories cut a
    // frame, every cell ~6 times, its noise never settling (the user, 2026-10-29).
    private float rtgiChangeWindow = 600f;
    // The frame the sun last turned (RecordRtgi), which opens the change window.
    private uint rtgiSunTurnFrame = uint.MaxValue;
    // The blend's traced reach (rtgi_filter.comp reachable; the panel's "Blend: traced reach"), and the samples under which it
    // widens to 5 x 5 (--rtgi-filter-wide).
    private bool rtgiFilterReach = true;
    private float rtgiFilterWide = 256f;
    private float RtgiChangeFramesNow => rtgiSunTurnFrame != uint.MaxValue && rtgiFrameCount - rtgiSunTurnFrame < (uint)rtgiChangeWindow
        ? rtgiChangeFrames : 1e9f;
    // --rtgi-sun-cut N: on a frame the sun turns (0.02 degrees or more), every cell's light history capped to N samples
    // (and its sun visibility's to 4): a known change, not one for the fast mean to find. 0: none.
    private float rtgiSunCut = 32f;
    // A ray's hit in a cell not trained since the sun turned counts as not ready (rtgi_train.comp's irradianceAt): its
    // landing is requested, so the bounce sources off screen are retrained -- and cut -- within a frame or two of a turn
    // instead of whenever a hit point reaches them. Their old light had lingered: Crytek Sponza 300 frames after a sun
    // change (71.3, -50 -> 160, -40) read 2.6% bright, its darkest quarter 11.5% bright (|error| 0.381); with this 0.9%
    // and 4.8% (0.307), at 600 frames 0.4% and 0.9% (0.269 against 0.361), the darkest quarter's error 0.241 against
    // 0.273 at 2400 -- a boot at the new sun reads 0.1% at 300 (2026-10-27). --rtgi-no-stale-hits: off.
    private bool rtgiStaleHits = true;
    // The cache's update on the device's second queue (DeclareRtgiCachePasses); --rtgi-no-async: in place.
    private bool rtgiAsync = true;
    private PassHandle rtgiTracePassHandle;
    // The same trace in place, where neither async compute nor the tracer groups take it (declared in setup before the
    // traced shadows' trace).
    private PassHandle rtgiInlineTracePassHandle;
    // BLIX_RTGI_TRAIN_DOUBLE: bits of the training run twice to measure their cost (rtgi_train.comp's uDouble), for the
    // bench (SponzaLoop.RtgiBench.cs).
    private float rtgiTrainDouble = int.TryParse(Environment.GetEnvironmentVariable("BLIX_RTGI_TRAIN_DOUBLE"), out var v) ? v : 0;
    // A cell listed for the resolve and the blend once its waiting samples reach 1 / --rtgi-list-worth (8) of what it holds,
    // else one frame in --rtgi-filter-every (rtgi_train.comp's listNow); the resolve and the blend then take every cell
    // listed. --rtgi-list-all: every frame it is trained, the resolve's and the blend's own turns.
    private bool rtgiListOnTurn = true;
    private float rtgiListWorth = 8f;
    // --rtgi-age F: a cell's light history held to F of its age in samples (at least --rtgi-age-floor, 64), so the samples
    // taken before the cells around it had their light fade fast (rtgi_resolve.comp). 0: no limit.
    private float rtgiAge = 0.25f, rtgiAgeFloor = 64f;
    private Vector3 rtgiLastSun;
    // --rtgi-fallback-up N: a landing whose cell holds too little looks two levels up, then N more, for light (2). A landing
    // finding none gives no sample, and those are the unseen -- the back of an arch, inside a gap: mostly dark, so the
    // samples kept read bright where the geometry is complex (the user, Crytek Sponza). Crytek's error at 60 frames 0.207
    // -> 0.157, Cornell's 0.030 -> 0.027 (2026-10-26). Counted dark instead: Crytek 0.138, but Cornell 0.067 and the thin
    // wall's open room 0.963 -- the unseen are not always dark.
    private float rtgiFallbackUp = 2f;
    // A cell's frame words summed over the subgroup before they are written, and the lists' appends one atomic a subgroup
    // (rtgi_train.comp; --rtgi-no-subgroup: each lane its own). Alone no faster -- Crytek Sponza at 4x the rays 50.1 ms
    // p50 against 50.0, training 11.9 ms against 10.3 (2026-10-26): a subgroup's 32 points came from all over the screen,
    // seldom sharing a cell. Placement in blocks (below) keeps them together.
    private bool rtgiSubgroup = true;
    // The training's words summed over the workgroup, through a table in shared memory, before the cell's atomics
    // (rtgi_train.comp's tableAdd) -- in place of the subgroup's rounds, whose reductions Xcode's profile had the training
    // bound by (Crytek Sponza, 2026-10-26). With the hazard-tracked barriers, training 2.51 / 2.79 ms a frame with the
    // rounds, 2.54 / 2.46 with the table (Crytek Sponza, two runs each, 2026-10-27); the gate's numbers the same to the
    // third place. --rtgi-no-train-table: the subgroup's rounds (or none, --rtgi-no-subgroup).
    private bool rtgiTrainTable = true;
    // Placement's candidates from the subgroup's pool (rtgi_rays.comp): each point reads one besides its own tile and weighs
    // its neighbours' by shuffle, where it read its own four (and four more when it drew again) -- placement 1.80 -> 1.52
    // ms a dispatch (Crytek Sponza 1080p, the bench, 2026-10-28). --rtgi-no-pool: each its own.
    private bool rtgiPool = true;
    // Placement in blocks (rtgi_rays.comp): a subgroup's points own an 8 x 4 block of tiles, their candidates within
    // --rtgi-block-window tiles (8) but for --rtgi-block-global of them (0.25) drawn anywhere; hit points in strides.
    // --rtgi-no-block: row by row, candidates anywhere.
    private bool rtgiBlock = true;
    private float rtgiBlockGlobal = 0.25f, rtgiBlockWindow = 8f;
    // A point's cosine direction from its cell's own low-discrepancy sequence (rtgi_rays.comp): Cornell's noise left at 60
    // frames 1.45% against 1.66%, error 0.022 against 0.024; Crytek Sponza alike (2026-10-26). --rtgi-no-blue: hashed.
    private bool rtgiBlue = true;
    // A point whose cell's sun visibility is settled traces its sun 1 in 8 (rtgi_rays.comp). --rtgi-every-sun: always.
    private bool rtgiSettledSun = true;
    private GpuBufferHandle rtgiSeq;
    // --rtgi-hit-prior K: the shrinkage expects a cell's parent to differ by K x parent size / mean hit distance of the light
    // at least (rtgi_filter.comp; 0: none). 0.5: the gate's seams 0.030 / 1.024 -> 0.022 / 1.013, its corridor 0.015 ->
    // 0.010; Cornell's error at 960 / 6,000 frames 0.014 / 0.012 -> 0.012 / 0.010, its noise left at 60 1.45% -> 1.60%
    // (nooks keep their own light, open walls still borrow; 1 and 2 noisier, no better), 2026-10-26.
    private float rtgiHitPrior = 0.5f;
    private GpuBufferHandle rtgiHitInv;
    // What the readout reads of a cell, in one 32-byte line (rtgi_cache.glsl's rtgiRecord): written by the resolve and the
    // blend, zeroed by the eviction. 32 bytes a slot (128 MB at 4 M).
    private GpuBufferHandle rtgiRecord;
    // The cache's work counted by the training, the resolve and the blend (their tally), read and zeroed at the profile
    // window (RtgiWork): 32 words.
    private GpuBufferHandle rtgiStats;
    // The eviction's count of slots held, a counter a sweep of its slices in turn (rtgi_evict.comp), and the share held past
    // which it empties the old (--rtgi-evict-above; 0 always).
    private GpuBufferHandle rtgiOccupancy;
    private float rtgiEvictAbove = 0.8f;
    private long rtgiStatsTraced;
    // --rtgi-filter-every N: a cell holding --rtgi-filter-settled samples (256) or more is blended one frame in N, its turn by
    // its slot (rtgi_filter.comp): 4 -- at 4x the rays the blend 8.5 ms a frame every frame, 2.9 on the rota (Crytek
    // Sponza, 2026-10-26). 1: every cell trained, every frame. Listed only on that turn too (rtgi_train.comp's listNow),
    // also resolved one frame in N: 16 -- a still Crytek Sponza view's blend and resolve 2.0 + 1.7 ms listed every frame,
    // 2.7 + 1.2 at 4, 1.0 + 0.6 at 16 (2026-10-27); a sun turn lists every cell again (its epoch).
    private float rtgiFilterEvery = 16f, rtgiFilterSettled = 256f;
    // --rtgi-filter-young N: a young cell (under --rtgi-filter-settled samples) blended one frame in N (2; 1: every frame).
    // Coarse cells on the rota instead (parents, never shown) measured nothing: they are few, the young fine ones many.
    private float rtgiFilterYoung = 2f;
    // Landings in untrained cells, trained the next frame (rtgi_train.comp's requests): by parity. --rtgi-no-requests: off.
    private bool rtgiRequests = true;
    private GpuBufferHandle[] rtgiRequestBufs = Array.Empty<GpuBufferHandle>();
    // The request buffer of the frame `back` frames before this one (rtgiRequestBufs: three, in turn).
    private int RtgiRequestSlot(int back) => (int)((rtgiFrameCount + 3u + (uint)back) % 3u);
    private const int RtgiTopLevel = 10;
    private ShaderInterface rtgiRaysInterface = null!, rtgiTrainInterface = null!, rtgiResolveInterface = null!, rtgiReadoutInterface = null!;
    private PassHandle rtgiRaysPassHandle, rtgiTrainPassHandle, rtgiResolvePassHandle, rtgiReadoutPassHandle;
    private PipelineHandle rtgiRaysPipeline, rtgiTrainPipeline, rtgiResolvePipeline, rtgiReadoutPipeline, rtgiBlurPipeline;
    private ShaderInterface rtgiBlurInterface = null!;
    private PassHandle rtgiBlurPassHandle;
    // The readout's reuse (rtgi_readout.frag's uReuse): its targets kept for the next frame (by the blur, rtgi_blur.frag),
    // the view they were drawn with, the frame they hold. --rtgi-no-reuse: every pixel read fresh. Its history reprojected
    // bilinear, each tap the same surface: from the nearest pixel it jittered in motion; bilinear it holds (the user,
    // 2026-10-27).
    private GraphResourceHandle rtgiHalfReuseHandle, rtgiHistRawHandle, rtgiHistGradientHandle, rtgiHistBlurredHandle, rtgiHistGuideHandle;
    private bool rtgiReuse = true;
    private long rtgiReuseFrame = -2;
    private Matrix4x4 rtgiReusePrevViewProj;
    private Vector2 rtgiReusePrevSub;
    private Vector3 rtgiReusePrevCamera;
    private GraphResourceHandle rtgiHalfRawHandle;
    // The half-res guide the readout writes beside its light (half_guide.glsl): each half-res pixel's world position and
    // normal, which the blur reads at its taps in place of full-res depth and normals fetched two pixels apart (blur 3.03 ->
    // 2.25 / 2.45 ms at Crytek Sponza, 2026-10-27). RGBA32F: positions kept exactly. Not the upsample's: its four taps a
    // full-res pixel read it slower than the depth and normals (2.39-2.64 -> 2.95 / 3.12 ms).
    private GraphResourceHandle rtgiHalfGuideHandle;
    // Per half-res pixel, how unsure its cell is: the readout writes it, the next frame's ray placement reads it
    // (rtgi_readout.frag, rtgi_rays.comp). Sized for a 4096 x 2304 frame's half; its row the frame's (rtgiHalfRow).
    private const int RtgiUnsureSize = 2048 * 1152;
    private GpuBufferHandle rtgiUnsure;
    // Per half-res pixel, the blur's radius, sized by the readout (rtgi_readout.frag) and read by the blur (rtgi_blur.frag).
    private GpuBufferHandle rtgiBlurRadius;
    private int rtgiHalfRow;
    private bool rtgiUnsureWritten;
    // --rtgi-blur PX: the readout blurred in screen space by how sure the cache is (rtgi_blur.frag, ReBLUR's way), at most
    // PX full-res pixels (16; 0: a copy). The cache is already the history, in world space: a screen history of 16 frames
    // on top lagged the converging light (Cornell's error at 30 frames 0.111 against 0.036, Crytek Sponza's 0.54 against
    // 0.21), and the blur needs none. Cornell's noise left at 30 / 60 / 960 frames 2.6% / 1.62% / 0.73% -> 1.56% / 0.97%
    // / 0.43%, error at 30 / 60 0.036 / 0.022 -> 0.030 / 0.018 (2026-10-26). --rtgi-blur-hit: the share of the mean hit
    // distance the radius may reach (0.25).
    private float rtgiBlur = 16f, rtgiBlurHit = 0.25f;
    // By the frame's parity: this frame's points, rays and answers; the other parity's are last frame's, trained now.
    private GpuBufferHandle[] rtgiPointsBuf = Array.Empty<GpuBufferHandle>(), rtgiRaysBuf = Array.Empty<GpuBufferHandle>(),
        rtgiHitsBuf = Array.Empty<GpuBufferHandle>(), rtgiOriginsBuf = Array.Empty<GpuBufferHandle>(), rtgiOccludedBuf = Array.Empty<GpuBufferHandle>();
    private GpuBufferHandle rtgiKeys, rtgiAccum, rtgiResolved, rtgiMoments, rtgiStamps, rtgiCells, rtgiFiltered, rtgiDirection;
    // Each cell's irradiance vector (where its light comes from, rtgi_resolve.comp), its part along the surface written as
    // the gradient the lit pass carries onto normal maps (--rtgi-no-gradient: not).
    // --rtgi-adaptive S: that share of the visible points placed by the cache's uncertainty, each the likeliest of
    // --rtgi-adaptive-k tiles (0.5, 4; 0: a tile each, rtgi_rays.comp).
    private float rtgiAdaptive = 1f, rtgiAdaptiveK = 4f;
    // When a cell is done (rtgi_filter.comp, the blend judging): its shown light's relative standard error under
    // --rtgi-done-error (0.02) against at least --rtgi-dark-floor (0.002), and no drift at the last check, every
    // --rtgi-done-window frames it is trained (60). A done cell keeps 1 in --rtgi-trickle of its points' rays (8).
    // --rtgi-view-done paints the bounce light green where done, red where open.
    // 0.5% (2026-10-26): at 2% "done" was the noise left -- Cornell's two-seed noise 1.5%, p90 3.8% with 87% of the visible
    // done -- and it showed on a smooth wall (the user).
    // --rtgi-budget-error / --rtgi-budget-floor: a visible point kept with probability (its pixel's relative error over
    // the target)^2, at least the floor (rtgi_rays.comp); error 0: every point. Off: against Crytek Sponza's truth at
    // frames 60 / 1200 / 3000, off 0.124 / 0.090 / 0.090 at 419k points a frame, 6% 0.126 / 0.092 / 0.091 at 317k, 12%
    // 0.127 / 0.096 / 0.094 at 239k (2026-10-27) -- the cells' relative error (a pooled spread over heavy-tailed bounce
    // light) stays high everywhere, and fewer points only slowed convergence across the board.
    private float rtgiBudgetError = 0f, rtgiBudgetFloor = 0.125f;
    // --rtgi-settled-samples / --rtgi-settled-keep: a visible point whose cell holds that many samples kept at that share
    // (rtgi_rays.comp); samples 0: off.
    private float rtgiSettledSamples = 0f, rtgiSettledKeep = 0.25f;
    private float rtgiDoneError = 0.005f, rtgiDarkFloor = 0.002f, rtgiDoneWindow = 60f, rtgiTrickle = 8f;

    private GpuBufferHandle rtgiConfidence, rtgiDoneCount;
    private bool rtgiNoGradient;
    // The neighbour blend (rtgi_filter.comp): the neighbours' share falls from 1 at --rtgi-filter-from samples to 0 at
    // --rtgi-filter-to (32, 256), stopped beyond --rtgi-filter-sigma (2) of the cell's spread; --rtgi-no-filter: none.
    private float rtgiFilterFrom = 32f, rtgiFilterTo = 256f, rtgiFilterSigma = 2f;
    // --rtgi-parent N: the parent cell (a level up) weighed as N samples beside the blend's own (32; 0: none).
    private float rtgiParentWeight = 32f;
    // --rtgi-parent-sigma K: the parent weighs its samples besides (to --rtgi-parent-max, 65,536) where the cell's blend is
    // within K of their standard errors of it, plus --rtgi-parent-tol of the light (rtgi_filter.comp): cell size by how
    // smooth the light is. 0: the fixed weight alone.
    private float rtgiParentSigma = 2f, rtgiParentTol = 0.02f, rtgiParentMax = 65536f;
    // The harder cascade (the user: the noise is on the flat surfaces): the parent read interpolated at the cell's centre
    // (--rtgi-no-parent-interp: the cell holding it) and weighed by all its blend stands for, the chain up the levels
    // (--rtgi-no-parent-chain: its own samples alone).
    // The parent weighed as the better of two estimates (shrinkage, rtgi_filter.comp); --rtgi-no-parent-shrink: by the
    // agreement test (--rtgi-parent-sigma, --rtgi-parent-tol).
    // --rtgi-parent-pool: the shrinkage's cell error from the spread pooled over its neighbours alike in light, not its
    // own: noise left 10% lower, error alike, the gate's seams 0.032 / 1.025 against 0.027 / 1.021 -- off (the user).
    private bool rtgiParentInterp = true, rtgiParentChain = true, rtgiParentShrink = true, rtgiParentPool;
    private GpuBufferHandle rtgiStands;
    // --rtgi-filter-floor F: the neighbours' share never below F, however many samples a cell holds (0: it fades to none).
    private float rtgiFilterFloor = 1f;
    // The parent lean takes the parent's blend (rtgi_filter.comp), cascading up every level trained;
    // --rtgi-train-levels N: the levels a sample trains, its own first.
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
    // The cells training lists a frame (rtgi_train.comp): a point's levels and the finer level's cell where held, the hit
    // points' levels again to spare.
    private int RtgiDirtyRoom => (RtgiPoints + rtgiHitPoints) * rtgiTrainLevels;
    // The near field's reach (m): a training ray met past it counts as far light, the cooked near field's gathers reach
    // this far (SponzaLoop.RtgiNearCook.cs). --rtgi-contact-radius.
    private float rtgiContactRadius = 0.25f;
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
    // Each training ray's hit shoots its own sun ray (the trace's hit flag 8), not the hit cell's mean (rtgi_train.comp):
    // Crytek Sponza's downward faces 1.088 -> 1.059 against its truth settled, 1.107 -> 1.058 at 60 frames, energy 1.012 ->
    // 1.003; the frame alike (2026-10-26). --rtgi-no-trace-hit-sun: the cell's.
    private bool rtgiTraceHitSun = true;
    // A sample added three levels up 1 in 2, four up 1 in 4 (rtgi_train.comp). --rtgi-no-thin: every level every sample.
    private bool rtgiThin = true;
    // Keys and stamps read before their atomics in training (rtgi_train.comp). --rtgi-no-read-first: the atomics always.
    private bool rtgiReadFirst = true;
    // --rtgi-interp-from N: the parent read interpolated from N samples, below it the cell holding it (32; rtgi_filter.comp).
    private float rtgiInterpFrom = 32f;
    // --rtgi-resolve-every N: a settled cell (--rtgi-filter-settled samples) resolved one frame in N (2; rtgi_resolve.comp).
    private float rtgiResolveEvery = 2f;
    private float RtgiRandomFrame => (rtgiFrameCount + rtgiSeed * 1_000_003u) % (1u << 24);
    private int RtgiSlots => 1 << rtgiSlotsLog2;
    private Vector4 RtgiParams => new(rtgiCell, rtgiCellGrow, RtgiSlots - 1, RtgiTopLevel);

    private void ParseRtgi(Blix.Core.AppArgs args)
    {
        ParseRtgiBudget(args);
        ParseRtgiCapture(args);
        ParseScreenRest(args);
        if (args.Flag("no-rtgi-settle")) rtgiSettleWanted = false;
        if (args.Int("rtgi-seed") is { } sd) rtgiSeed = (uint)Math.Max(0, sd);
        if (args.Float("rtgi-hit-sun") is { } hs) rtgiHitSun = MathF.Max(0f, hs);
        if (args.Flag("rtgi-no-trace-hit-sun")) rtgiTraceHitSun = false;
        if (args.Flag("rtgi-no-thin")) rtgiThin = false;
        if (args.Flag("rtgi-no-read-first")) rtgiReadFirst = false;
        if (args.Float("rtgi-interp-from") is { } inf) rtgiInterpFrom = MathF.Max(0f, inf);
        if (args.Float("rtgi-resolve-every") is { } rev) rtgiResolveEvery = MathF.Max(1f, MathF.Round(rev));
        if (args.Int("rtgi-tiles") is { } t) rtgiTiles = Math.Max(1, t);
        if (args.Int("rtgi-hits") is { } h) rtgiHitPoints = Math.Max(0, h);
        if (args.Int("rtgi-slots-log2") is { } sl) rtgiSlotsLog2 = Math.Clamp(sl, 16, 24);
        if (args.Float("rtgi-cell") is { } c) rtgiCell = MathF.Max(0.005f, c);
        if (args.Float("rtgi-cell-grow") is { } g) rtgiCellGrow = MathF.Max(0f, g);
        if (args.Float("rtgi-near") is { } nf) rtgiNearField = MathF.Max(0f, nf);
        if (args.Float("rtgi-history") is { } hi) rtgiHistory = MathF.Max(1f, hi);
        if (args.Float("rtgi-max-age") is { } a) rtgiMaxAge = MathF.Max(1f, a);
        if (args.Float("rtgi-evict-above") is { } ea) rtgiEvictAbove = Math.Clamp(ea, 0f, 1f);
        if (args.Int("rtgi-settle-frames") is { } sf) rtgiSettleFrames = Math.Max(0, sf);
        if (args.Float("rtgi-change-sigma") is { } cs) rtgiChangeSigma = MathF.Max(0.5f, cs);
        if (args.Float("rtgi-fast") is { } fa) rtgiFast = MathF.Max(1f, fa);
        if (args.Float("rtgi-age") is { } ag) rtgiAge = Math.Clamp(ag, 0f, 1f);
        if (args.Float("rtgi-age-floor") is { } af) rtgiAgeFloor = MathF.Max(1f, af);
        if (args.Float("rtgi-change-floor") is { } cf) rtgiChangeFloor = MathF.Max(0f, cf);
        if (args.Float("rtgi-change-frames") is { } cfr) rtgiChangeFrames = MathF.Max(1f, cfr);
        if (args.Float("rtgi-change-window") is { } cw) rtgiChangeWindow = MathF.Max(0f, cw);
        if (args.Float("rtgi-filter-wide") is { } fw) rtgiFilterWide = MathF.Max(0f, fw);
        if (args.Float("rtgi-sun-cut") is { } sc) rtgiSunCut = MathF.Max(0f, sc);
        if (args.Flag("rtgi-no-stale-hits")) rtgiStaleHits = false;
        if (args.Flag("rtgi-list-all")) rtgiListOnTurn = false;
        if (args.Flag("rtgi-no-async")) rtgiAsync = false;
        if (args.Float("rtgi-list-worth") is { } lw) rtgiListWorth = MathF.Max(1f, lw);
        if (args.Float("rtgi-fallback-up") is { } fu) rtgiFallbackUp = Math.Clamp(fu, 0f, 6f);
        if (args.Flag("rtgi-no-subgroup")) rtgiSubgroup = false;
        if (args.Flag("rtgi-no-train-table")) rtgiTrainTable = false;
        if (args.Flag("rtgi-no-pool")) rtgiPool = false;
        if (args.Flag("rtgi-no-block")) rtgiBlock = false;
        if (args.Float("rtgi-block-global") is { } bg) rtgiBlockGlobal = Math.Clamp(bg, 0f, 1f);
        if (args.Float("rtgi-block-window") is { } bw) rtgiBlockWindow = MathF.Max(1f, MathF.Round(bw));
        if (args.Flag("rtgi-no-blue")) rtgiBlue = false;
        if (args.Float("rtgi-blur") is { } bl) rtgiBlur = MathF.Max(0f, bl);
        if (args.Float("rtgi-blur-hit") is { } bh) rtgiBlurHit = MathF.Max(0f, bh);
        if (args.Flag("rtgi-every-sun")) rtgiSettledSun = false;
        if (args.Float("rtgi-hit-prior") is { } hp) rtgiHitPrior = MathF.Max(0f, hp);
        if (args.Float("rtgi-filter-every") is { } fe) rtgiFilterEvery = MathF.Max(1f, MathF.Round(fe));
        if (args.Float("rtgi-filter-settled") is { } fs) rtgiFilterSettled = MathF.Max(1f, fs);
        if (args.Float("rtgi-filter-young") is { } fy) rtgiFilterYoung = MathF.Max(1f, MathF.Round(fy));
        if (args.Flag("rtgi-no-requests")) rtgiRequests = false;
        fixedSubpixel = args.Flag("fixed-subpixel");
        if (args.Float("rtgi-filter-from") is { } ffr) rtgiFilterFrom = MathF.Max(0f, ffr);
        if (args.Float("rtgi-filter-to") is { } fto) rtgiFilterTo = MathF.Max(1f, fto);
        if (args.Float("rtgi-filter-sigma") is { } fsg) rtgiFilterSigma = MathF.Max(0.1f, fsg);
        // The blend is shown (2026-10-26): measured as convergence, not cosmetics -- Cornell, two seeds: noise left halved
        // at every frame (1.3% against 2.4% at 960), error against the truth lower (0.021 against 0.029).
        // --rtgi-no-filter: the raw cells shown (the blend still judging when they are done).
        rtgiNoFilter = args.Flag("rtgi-no-filter");
        rtgiNoGradient = args.Flag("rtgi-no-gradient");
        if (args.Float("rtgi-adaptive") is { } ad) rtgiAdaptive = Math.Clamp(ad, 0f, 1f);
        if (args.Float("rtgi-adaptive-k") is { } ak) rtgiAdaptiveK = Math.Clamp(ak, 1f, 16f);
        if (args.Float("rtgi-done-error") is { } de) rtgiDoneError = MathF.Max(1e-4f, de);
        if (args.Float("rtgi-dark-floor") is { } df) rtgiDarkFloor = MathF.Max(1e-6f, df);
        if (args.Float("rtgi-done-window") is { } dw) rtgiDoneWindow = MathF.Max(1f, dw);
        if (args.Float("rtgi-trickle") is { } tr) rtgiTrickle = MathF.Max(1f, tr);
        if (args.Float("rtgi-budget-error") is { } be) rtgiBudgetError = MathF.Max(0f, be);
        if (args.Float("rtgi-budget-floor") is { } bf) rtgiBudgetFloor = Math.Clamp(bf, 0f, 1f);
        if (args.Float("rtgi-settled-samples") is { } ss) rtgiSettledSamples = MathF.Max(0f, ss);
        if (args.Float("rtgi-settled-keep") is { } sk) rtgiSettledKeep = Math.Clamp(sk, 0f, 1f);
        if (args.Flag("rtgi-view-done")) rtgiView = 1;
        if (args.Flag("rtgi-no-reuse")) rtgiReuse = false;
        if (args.Float("rtgi-contact-radius") is { } crad) rtgiContactRadius = MathF.Max(0.01f, crad);
        if (args.Float("rtgi-parent") is { } pw) rtgiParentWeight = MathF.Max(0f, pw);
        if (args.Float("rtgi-parent-sigma") is { } ps) rtgiParentSigma = MathF.Max(0f, ps);
        if (args.Float("rtgi-parent-tol") is { } pt) rtgiParentTol = MathF.Max(0f, pt);
        if (args.Float("rtgi-parent-max") is { } pm) rtgiParentMax = MathF.Max(1f, pm);
        if (args.Flag("rtgi-no-parent-interp")) rtgiParentInterp = false;
        if (args.Flag("rtgi-no-parent-chain")) rtgiParentChain = false;
        if (args.Flag("rtgi-no-parent-shrink")) rtgiParentShrink = false;
        if (args.Flag("rtgi-parent-pool")) rtgiParentPool = true;
        if (args.Float("rtgi-filter-floor") is { } ffl) rtgiFilterFloor = Math.Clamp(ffl, 0f, 1f);
        if (args.Flag("rtgi-no-cascade")) rtgiCascade = false;
        if (args.Int("rtgi-train-levels") is { } tl) rtgiTrainLevels = Math.Clamp(tl, 1, 8);
        ParseRtgiVolume(args);
        ParseRtgiCensus(args);
        if (args.String("rtgi-dump") is { } dump) rtgiDumpDir = dump;
        if (args.String("rtgi-dump-frames") is { } frames)
            rtgiDumpFrames = new SortedSet<int>(frames.Split(',').Select(f => int.Parse(f, CultureInfo.InvariantCulture)));
    }

    // Setup: the compute passes before the traced shadows' group that takes their batches.
    // Setup, after the blur, the cache's last reader this frame: its update -- training, the resolve, the blend, the
    // eviction -- on the device's second queue (async compute), beside the upsample, the lit pass and the rest of the frame,
    // which read none of it; the frame's end waits for it. The readout reads the cache as the last frame's update left
    // it, and the update reads this frame's points, rays and hits (placed and traced earlier in the frame). --rtgi-no-async:
    // in place on the graphics queue (2026-10-27).
    private void DeclareRtgiCachePasses(Func<string, ShaderInterface> reflect)
    {
        rtgiTrainInterface = reflect("rtgi_train.comp");
        rtgiTrainPassHandle = graph.ComputePass("rtgi-train").Async(rtgiAsync).Shader(rtgiTrainInterface).Handle;
        DeclareRtgiVolumeAsyncPasses(reflect);
        rtgiResolveInterface = reflect("rtgi_resolve.comp");
        rtgiResolvePassHandle = graph.ComputePass("rtgi-resolve").Async(rtgiAsync).Shader(rtgiResolveInterface).Handle;
        rtgiFilterInterface = reflect("rtgi_filter.comp");
        rtgiFilterPassHandle = graph.ComputePass("rtgi-filter").Async(rtgiAsync).Shader(rtgiFilterInterface).Handle;
        rtgiEvictInterface = reflect("rtgi_evict.comp");
        rtgiEvictPassHandle = graph.ComputePass("rtgi-evict").Async(rtgiAsync).Shader(rtgiEvictInterface).Handle;
        DeclareRtgiCensusReducePass();
    }

    // Setup, after the pre-pass: placement (this frame's rays, traced in the shadow trace's group after it).
    private void DeclareRtgiComputePasses(Func<string, ShaderInterface> reflect)
    {
        rtgiRaysInterface = reflect("rtgi_rays.comp");
        rtgiRaysPassHandle = graph.ComputePass("rtgi-rays").Shader(rtgiRaysInterface)
            .Read(SampleableSceneDepth).Read(SampleablePrepassNormal).Handle;
        DeclareRtgiVolumeRaysPass(reflect);
        DeclareRtgiCensusRaysPass(reflect);
    }

    // Setup: the readout into the half-res incident targets.
    private void DeclareRtgiReadoutPass(Func<string, string, ShaderInterface> reflect)
    {
        rtgiReadoutInterface = reflect("present.vert", "rtgi_readout.frag");
        // Into the raw half-res target, the blur after it into the incident (rtgi_blur.frag).
        rtgiHalfRawHandle = graph.ColorTarget("rtgi-half-raw", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(0.5f));
        rtgiHalfGuideHandle = graph.ColorTarget("rtgi-half-guide", TextureFormat.Rg32Uint, new MatchSwapchainGraphSize(0.5f));
        var half = new MatchSwapchainGraphSize(0.5f);
        rtgiHalfReuseHandle = graph.ColorTarget("rtgi-half-reuse", TextureFormat.Rgba16F, half);
        rtgiHistRawHandle = graph.ColorTarget("rtgi-hist-raw", TextureFormat.Rgba16F, half);
        rtgiHistGradientHandle = graph.ColorTarget("rtgi-hist-gradient", TextureFormat.Rgba16F, half);
        rtgiHistBlurredHandle = graph.ColorTarget("rtgi-hist-blurred", TextureFormat.Rgba16F, half);
        rtgiHistGuideHandle = graph.ColorTarget("rtgi-hist-guide", TextureFormat.Rg32Uint, half);
        // After the last frame's async update (DeclareRtgiCachePasses): the first pass of a frame reading what it writes.
        rtgiReadoutPassHandle = graph.GraphicsPass("rtgi-readout")
            .AfterAsync()
            .Target(rtgiHalfRawHandle, LoadOp.Clear, StoreOp.Store)
            .Target(incidentGradientHalfHandle, LoadOp.Clear, StoreOp.Store)
            .Target(rtgiHalfGuideHandle, LoadOp.Clear, StoreOp.Store)
            .Target(rtgiHalfReuseHandle, LoadOp.Clear, StoreOp.Store)
            .Read(SampleableSceneDepth)
            .Read(SampleablePrepassNormal)
            .ReadHistory(rtgiHistRawHandle).ReadHistory(rtgiHistGradientHandle).ReadHistory(rtgiHistBlurredHandle).ReadHistory(rtgiHistGuideHandle)
            .WritesGpuBuffers()
            .Shader(rtgiReadoutInterface)
            .Handle;
        rtgiBlurInterface = reflect("present.vert", "rtgi_blur.frag");
        var blur = graph.GraphicsPass("rtgi-blur")
            .Target(incidentHalfHandle, LoadOp.Clear, StoreOp.Store)
            .Read(rtgiHalfRawHandle)
            .Read(rtgiHalfGuideHandle)
            .Read(rtgiHalfReuseHandle)
            .Read(incidentGradientHalfHandle);
        // The history for the next frame's reuse, only with it on: the four writes cost the blur ~0.3 ms a frame at 1080p
        // (1.37 -> 1.66, 2026-10-27). Without, the shader's history outputs have no target and are dropped.
        if (rtgiReuse)
            blur = blur.Target(rtgiHistRawHandle, LoadOp.Clear, StoreOp.Store)
                .Target(rtgiHistGradientHandle, LoadOp.Clear, StoreOp.Store)
                .Target(rtgiHistBlurredHandle, LoadOp.Clear, StoreOp.Store)
                .Target(rtgiHistGuideHandle, LoadOp.Clear, StoreOp.Store);
        rtgiBlurPassHandle = blur.Shader(rtgiBlurInterface).Handle;
    }

    private void CreateRtgiPipelines(string shaderDir, byte[] presentVertSpv)
    {
        // Full 32-lane subgroups where a shader's subgroup work assumes them (the training's ballots and reductions, the
        // placement's 8 x 4 tiles), where the device can promise them (GraphicsFeatures.ComputeSubgroups32).
        var full32 = (device.Features & GraphicsFeatures.ComputeSubgroups32) != 0 ? ComputePipelineOptions.FullSubgroups32 : ComputePipelineOptions.None;
        PipelineHandle Compute(string name, ShaderInterface iface, ComputePipelineOptions options = ComputePipelineOptions.None) =>
            Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
                File.ReadAllBytes(Path.Combine(shaderDir, name + ".comp.spv")), iface, name)), name, options));
        rtgiTrainPipeline = Compute("rtgi_train", rtgiTrainInterface, full32);
        CreateRtgiVolumePipelines(Compute, full32);
        rtgiResolvePipeline = Compute("rtgi_resolve", rtgiResolveInterface);
        rtgiEvictPipeline = Compute("rtgi_evict", rtgiEvictInterface);
        rtgiFilterPipeline = Compute("rtgi_filter", rtgiFilterInterface);
        rtgiRaysPipeline = Compute("rtgi_rays", rtgiRaysInterface, full32);
        CreateRtgiCensusPipeline((name, iface) => Compute(name, iface));
        rtgiReadoutPipeline = Pipeline(Own(device.CreateShaderProgramFromSpv(
                presentVertSpv, File.ReadAllBytes(Path.Combine(shaderDir, "rtgi_readout.frag.spv")), rtgiReadoutInterface, "rtgi_readout")),
            VertexPosition3NormalTexture.Layout, DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled, BlendState.Disabled, BlendState.Disabled, BlendState.Disabled }, rtgiReadoutPassHandle, "rtgi_readout");

        rtgiBlurPipeline = Pipeline(Own(device.CreateShaderProgramFromSpv(
                presentVertSpv, File.ReadAllBytes(Path.Combine(shaderDir, "rtgi_blur.frag.spv")), rtgiBlurInterface, "rtgi_blur")),
            VertexPosition3NormalTexture.Layout, DepthState.Disabled, RasterizerState.NoCulling,
            rtgiReuse ? new[] { BlendState.Disabled, BlendState.Disabled, BlendState.Disabled, BlendState.Disabled, BlendState.Disabled } : new[] { BlendState.Disabled },
            rtgiBlurPassHandle, "rtgi_blur");
    }

    // After the traced shadows are recorded: train, resolve, this frame's rays and their batches.
    private void RecordRtgi(int width, int height)
    {
        rtgiHalfRow = HalfLightingSize(width, height).Width;
        if (frameTracer is not { } tracer) return;
        // This frame's spend (SponzaLoop.RtgiBudget.cs; the most without --rtgi-budget); the buffers are sized for the most.
        var lastTotal = rtgiLastPointTotal;
        var n = rtgiTilesNow + rtgiHitsNow;
        if (rtgiKeys.Equals(default(GpuBufferHandle)))
        {
            var most = RtgiPoints;
            GpuBufferHandle[] Two(int bytes, string name) =>
                Enumerable.Range(0, 2).Select(s => Own(device.CreateGpuBuffer(bytes, name: $"sponza.rtgi.{name}{s}"))).ToArray();
            rtgiPointsBuf = Two(most * 32, "points");
            rtgiRaysBuf = Two(most * 32, "rays");
            rtgiHitsBuf = Two(most * 48, "hits");
            rtgiOriginsBuf = Two(most * 16, "origins");
            rtgiOccludedBuf = Two(most * 4, "occluded");
            // Three, in turn by frame (RtgiRequestSlot): the training appends to this frame's while the next frame's
            // placement, beside the async update (DeclareRtgiCachePasses), reads the one two frames back and empties the
            // one after.
            rtgiRequestBufs = Enumerable.Range(0, 3).Select(s => Own(device.CreateGpuBuffer(16 + Math.Max(1, rtgiHitPoints) * 32, new byte[16 + Math.Max(1, rtgiHitPoints) * 32], $"sponza.rtgi.requests{s}"))).ToArray();
            rtgiKeys = Own(device.CreateGpuBuffer(RtgiSlots * 4, new byte[RtgiSlots * 4], "sponza.rtgi.keys"));
            rtgiAccum = Own(device.CreateGpuBuffer(RtgiSlots * 64, new byte[RtgiSlots * 64], "sponza.rtgi.accum"));
            rtgiDirection = Own(device.CreateGpuBuffer(RtgiSlots * 16, new byte[RtgiSlots * 16], "sponza.rtgi.direction"));
            rtgiConfidence = Own(device.CreateGpuBuffer(RtgiSlots * 16, new byte[RtgiSlots * 16], "sponza.rtgi.confidence"));
            rtgiUnsure = Own(device.CreateGpuBuffer(RtgiUnsureSize * 4, new byte[RtgiUnsureSize * 4], "sponza.rtgi.unsure"));
            rtgiBlurRadius = Own(device.CreateGpuBuffer(RtgiUnsureSize * 4, new byte[RtgiUnsureSize * 4], "sponza.rtgi.blur-radius"));
            rtgiHitInv = Own(device.CreateGpuBuffer(RtgiSlots * 4, new byte[RtgiSlots * 4], "sponza.rtgi.hit-inv"));
            rtgiRecord = Own(device.CreateGpuBuffer(RtgiSlots * 32, new byte[RtgiSlots * 32], "sponza.rtgi.record"));
            // Readable where it lies: the budget reads its counters each frame without a wait (SponzaLoop.RtgiBudget.cs).
            rtgiStats = Own(device.CreateReadableGpuBuffer(256, "sponza.rtgi.stats"));
            CreateRtgiVolumeDummy();
            rtgiOccupancy = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.rtgi.occupancy"));
            rtgiSeq = Own(device.CreateGpuBuffer(RtgiSlots * 4, new byte[RtgiSlots * 4], "sponza.rtgi.seq"));
            rtgiStands = Own(device.CreateGpuBuffer(RtgiSlots * 4, new byte[RtgiSlots * 4], "sponza.rtgi.stands"));
            // Readable where it lies: the budget reads the visible's error each frame without a wait (SponzaLoop.RtgiBudget.cs).
            rtgiDoneCount = Own(device.CreateReadableGpuBuffer(40, "sponza.rtgi.done-count"));
            rtgiResolved = Own(device.CreateGpuBuffer(RtgiSlots * 32, new byte[RtgiSlots * 32], "sponza.rtgi.resolved"));
            rtgiMoments = Own(device.CreateGpuBuffer(RtgiSlots * 48, new byte[RtgiSlots * 48], "sponza.rtgi.moments"));
            rtgiStamps = Own(device.CreateGpuBuffer(RtgiSlots * 8, new byte[RtgiSlots * 8], "sponza.rtgi.stamps"));
            rtgiCells = Own(device.CreateGpuBuffer(RtgiSlots * 16, new byte[RtgiSlots * 16], "sponza.rtgi.cells"));
            rtgiFiltered = Own(device.CreateGpuBuffer(RtgiSlots * 16, new byte[RtgiSlots * 16], "sponza.rtgi.filtered"));
            rtgiDirtyBufs = Enumerable.Range(0, 2).Select(s => Own(device.CreateGpuBuffer(16 + RtgiDirtyRoom * 4, new byte[16 + RtgiDirtyRoom * 4], $"sponza.rtgi.dirty{s}"))).ToArray();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] rtgi: {rtgiTiles:N0} visible points and {rtgiHitPoints:N0} from last frame's hits a frame; cache {RtgiSlots:N0} slots ({RtgiSlots * 80 / 1048576.0:0} MB), cells {rtgiCell:0.###} m + {rtgiCellGrow:0.###} per metre, {rtgiHistory:0} samples held, untrained cells emptied after {rtgiMaxAge:0} frames."));
        }
        // --rtgi-census: the census's frame in place of the cache's (SponzaLoop.RtgiCensus.cs).
        if (rtgiCensus) { RecordRtgiCensus(tracer); return; }
        // The panel's freeze: no rays, no training, the cache read as it stands.
        if (rtgiFrozen) return;
        var cur = (int)(rtgiFrameCount & 1u);
        var prev = 1 - cur;
        var towardSun = -Vector3.Normalize(sunDirection);
        var groups = (n + 63) / 64;
        var sunTurned = rtgiTraced > 0 && Vector3.Dot(towardSun, rtgiLastSun) < MathF.Cos(0.02f * MathF.PI / 180f);
        // Logged: the sun the cache trains under, its first frames and every turn -- a boot that showed the scene's default
        // sun's shadows and then snapped to --sun (the user, Crytek Sponza, 2026-10-26).
        if (sunTurned || rtgiTraced < 3)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] rtgi: sun toward {towardSun} at traced frame {rtgiTraced} (frame {framesRendered}){(sunTurned ? ", turned" : "")}."));
        rtgiLastSun = towardSun;
        if (sunTurned && rtgiSunCut > 0f) rtgiSunEpoch++;
        if (sunTurned) rtgiSunTurnFrame = rtgiFrameCount;
        var frameNumber = rtgiFrameCount + 1u;
        // On the bench (SponzaLoop.RtgiBench.cs) the training dispatched N times, the rest of the cache's frame skipped.
        var trainReps = RtgiTrainReps();
        // The world volume: its cook's rays (placement time) and their fold into the probes (with the training), this frame's
        // both; the cells' visibility and the near field's cook.
        if (!rtgiBenching)
        {
            RecordRtgiVolumeRays(cur);
            RecordRtgiVolumeVis(cur);
            RecordRtgiNearCook(cur, towardSun);
            RecordRtgiVolumeCook(cur);
        }
        if (rtgiTraced > 0)
        {
            var train = new DispatchCommand(rtgiTrainPipeline, groups, 1, 1,
                new ShaderUniform[]
                {
                    new("uParams", new Vector4Uniform(RtgiParams)),
                    new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, n))),
                    new("uSun", new Vector4Uniform(new Vector4(towardSun, 1f))),
                    new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
                    new("uNearField", new Vector4Uniform(new Vector4(rtgiNearField, rtgiContactRadius, rtgiHitSun, rtgiTrainLevels))),
                    new("uList", new Vector4Uniform(new Vector4(frameNumber, RtgiListRoomNow, RtgiListWorthNow, 0f))),
                    new("uEpoch", new Vector4Uniform(new Vector4(rtgiStaleHits ? 1f : 0f, rtgiSunEpoch, 0f, rtgiRequests ? rtgiHitPoints : 0f))),
                    new("uHitSun", new Vector4Uniform(new Vector4(rtgiTraceHitSun ? 1f : 0f, rtgiThin ? 1f : 0f, rtgiReadFirst ? 1f : 0f, rtgiListOnTurn ? rtgiFilterEvery : 0f))),
                    new("uFallback", new Vector4Uniform(new Vector4(rtgiFallbackUp, rtgiNearCook ? 1f : 0f, 0f, rtgiSubgroup ? (rtgiTrainTable ? 2f : 1f) : 0f))),
                    new("uDouble", new Vector4Uniform(new Vector4(rtgiTrainDouble, 0f, 0f, 0f))),
                    new("uCapture", new Vector4Uniform(RtgiCaptureUniform)),
                    new("uVolume", new Vector4Uniform(RtgiVolumeParams)),
                    new("uVolumeGeom", new Vector4Uniform(RtgiVolumeGeom)),
                    new("uNearCook", new Vector4Uniform(RtgiNearParams)),
                },
                new[] { new ShaderTextureBinding("uSkyRadiance", envCubeTexture) },
                Buffers: new[]
                {
                    new ShaderBufferBinding("RtgiPoints", rtgiPointsBuf[cur]),
                    new ShaderBufferBinding("RtgiRayBuffer", rtgiRaysBuf[cur]),
                    new ShaderBufferBinding("RtgiHits", rtgiHitsBuf[cur]),
                    new ShaderBufferBinding("RtgiOccluded", rtgiOccludedBuf[cur]),
                    new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                    new ShaderBufferBinding("RtgiAccum", rtgiAccum),
                    new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                    new ShaderBufferBinding("RtgiRequests", rtgiRequestBufs[RtgiRequestSlot(0)]),
                    new ShaderBufferBinding("RtgiMoments", rtgiMoments),
                    new ShaderBufferBinding("RtgiDirty", rtgiDirtyBufs[cur]),
                    new ShaderBufferBinding("RtgiCells", rtgiCells),
                    new ShaderBufferBinding("RtgiFiltered", rtgiFiltered),
                    new ShaderBufferBinding("RtgiCaptureOut", RtgiCaptureOutBuffer),
                    new ShaderBufferBinding("RtgiStats", rtgiStats),
                    new ShaderBufferBinding("RtgiRecord", rtgiRecord),
                    new ShaderBufferBinding("RtgiVolumeTable", RtgiVolTableOrDummy),
                    new ShaderBufferBinding("RtgiVolumeProbes", RtgiVolProbesOrDummy),
                    new ShaderBufferBinding("RtgiVolumeDepth", RtgiVolDepthOrDummy),
                    new ShaderBufferBinding("RtgiNearTable", RtgiNearTableOrDummy),
                    new ShaderBufferBinding("RtgiNearPoints", RtgiNearPointsOrDummy),
                    new ShaderBufferBinding("RtgiNearRegions", RtgiNearRegionsOrDummy),
                    new ShaderBufferBinding("RtgiNearScaleTable", RtgiNearScaleTableOrDummy),
                });
            for (var r = 0; r < trainReps; r++) graph.Dispatch(rtgiTrainPassHandle, train);
        }
        // The bench of the training ends the cache's frame here; of placement or the trace, goes on to them.
        if (rtgiBenching && rtgiBenchPass == "train") return;
        if (rtgiBenching) goto BenchPlacement;
        // The cells trained this frame (the list's room dispatched; past its count each thread returns).
        // Over the frame's list room (RtgiListRoomNow): the training listed no more.
        graph.Dispatch(rtgiResolvePassHandle, new DispatchCommand(rtgiResolvePipeline, (RtgiListRoomNow + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uResolve", new Vector4Uniform(new Vector4(rtgiHistory, rtgiMaxAge, RtgiSlots, rtgiChangeSigma))),
                new("uChange", new Vector4Uniform(new Vector4(RtgiChangeFramesNow, rtgiFast, rtgiChangeFloor, rtgiSunCut))),
                new("uAge", new Vector4Uniform(new Vector4(rtgiAge, rtgiAgeFloor, 0f, 0f))),
                new("uList", new Vector4Uniform(new Vector4(rtgiSunEpoch, RtgiDirtyRoom, 0f, 0f))),
                new("uRotaR", new Vector4Uniform(new Vector4(rtgiFilterSettled, rtgiListOnTurn ? 1f : rtgiResolveEvery, frameNumber, 0f))),
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
                new ShaderBufferBinding("RtgiSeq", rtgiSeq),
                new ShaderBufferBinding("RtgiDirection", rtgiDirection),
                new ShaderBufferBinding("RtgiHitInv", rtgiHitInv),
                new ShaderBufferBinding("RtgiRecord", rtgiRecord),
                new ShaderBufferBinding("RtgiStats", rtgiStats),
                new ShaderBufferBinding("RtgiCells", rtgiCells),
            }));
        // The cells just resolved, blended with their neighbours into what the readout and lookups read (a copy alone with
        // --rtgi-no-filter).
        graph.Dispatch(rtgiFilterPassHandle, new DispatchCommand(rtgiFilterPipeline, (RtgiListRoomNow + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uParams", new Vector4Uniform(RtgiParams)),
                // The blend always runs -- it judges when a cell is done -- and is shown unless --rtgi-no-filter.
                new("uFilter", new Vector4Uniform(new Vector4(rtgiFilterFrom, rtgiFilterTo, rtgiFilterSigma, RtgiDirtyRoom))),
                new("uParent", new Vector4Uniform(new Vector4(rtgiParentWeight, RtgiTopLevel, rtgiFilterFloor, rtgiNoFilter ? 0f : 1f))),
                new("uFilter2", new Vector4Uniform(new Vector4(rtgiCascade ? 1f : 0f, rtgiParentSigma, rtgiParentTol, rtgiParentMax))),
                new("uParent2", new Vector4Uniform(new Vector4(rtgiParentInterp ? 1f : 0f, rtgiParentChain ? 1f : 0f, rtgiParentShrink ? 1f : 0f, rtgiParentPool ? 1f : 0f))),
                new("uHit", new Vector4Uniform(new Vector4(rtgiHitPrior, 0f, 0f, 0f))),
                new("uRota", new Vector4Uniform(new Vector4(rtgiFilterSettled, rtgiListOnTurn ? 1f : rtgiFilterEvery, rtgiListOnTurn ? 1f : rtgiFilterYoung, 0f))),
                new("uInterp", new Vector4Uniform(new Vector4(rtgiInterpFrom, 0f, 0f, 0f))),
                new("uDone", new Vector4Uniform(new Vector4(frameNumber, rtgiDoneWindow, rtgiDoneError, rtgiDarkFloor))),
                new("uReach", new Vector4Uniform(new Vector4(RtgiVolOn && rtgiFilterReach ? 1f : 0f, rtgiFilterWide, 0.5f, 0f))),
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
                new ShaderBufferBinding("RtgiStands", rtgiStands),
                new ShaderBufferBinding("RtgiHitInv", rtgiHitInv),
                new ShaderBufferBinding("RtgiRecord", rtgiRecord),
                new ShaderBufferBinding("RtgiVolumeCellVis", RtgiVolCellVisOrDummy),
                new ShaderBufferBinding("RtgiStats", rtgiStats),
            }));
        // A sixteenth of the slots a frame: those no training reached for --rtgi-max-age frames emptied, while more than
        // --rtgi-evict-above of the slots are held.
        // The panel's reset: every slot at once, as untrained past its age (its frame pushed past every stamp).
        var slice = RtgiSlots / RtgiEvictSlices;
        var resetting = rtgiResetPending;
        rtgiResetPending = false;
        var evictCount = resetting ? RtgiSlots : slice;
        var sliceIndex = (int)(rtgiFrameCount % RtgiEvictSlices);
        var sweep = (rtgiFrameCount / RtgiEvictSlices) % 3u;
        graph.Dispatch(rtgiEvictPassHandle, new DispatchCommand(rtgiEvictPipeline, (evictCount + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uEvict", new Vector4Uniform(resetting
                    ? new Vector4(frameNumber + rtgiMaxAge + 2f, rtgiMaxAge, 0f, RtgiSlots)
                    : new Vector4(frameNumber, rtgiMaxAge, sliceIndex * slice, slice))),
                new("uPressure", new Vector4Uniform(new Vector4(sweep, resetting ? 0f : rtgiEvictAbove * RtgiSlots, !resetting && sliceIndex == 0 ? 1f : 0f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                new ShaderBufferBinding("RtgiMoments", rtgiMoments),
                new ShaderBufferBinding("RtgiStamps", rtgiStamps),
                new ShaderBufferBinding("RtgiAccum", rtgiAccum),
                new ShaderBufferBinding("RtgiDirection", rtgiDirection),
                new ShaderBufferBinding("RtgiConfidence", rtgiConfidence),
                new ShaderBufferBinding("RtgiRecord", rtgiRecord),
                new ShaderBufferBinding("RtgiCells", rtgiCells),
                new ShaderBufferBinding("RtgiStats", rtgiStats),
                new ShaderBufferBinding("RtgiOccupancy", rtgiOccupancy),
                new ShaderBufferBinding("RtgiSeq", rtgiSeq),
            }));

    BenchPlacement:
        var tilesX = Math.Max(1, (int)MathF.Round(MathF.Sqrt(rtgiTilesNow * (float)width / height)));
        var tilesY = Math.Max(1, (rtgiTilesNow + tilesX - 1) / tilesX);
        // The visible points are as many tiles as the grid has (a whole grid, so every row is covered).
        Matrix4x4.Invert(viewProjJittered, out var invViewProj);
        rtgiLastPointTotal = Math.Min(rtgiTilesNow, tilesX * tilesY) + rtgiHitsNow;
        rtgiCaptureSize = (width, height);
        var place = new DispatchCommand(rtgiRaysPipeline, groups, 1, 1,
            new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                new("uSize", new Vector4Uniform(new Vector4(width, height, tilesX, tilesY))),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                new("uCounts", new Vector4Uniform(new Vector4(Math.Min(rtgiTilesNow, tilesX * tilesY), rtgiHitsNow, RtgiRandomFrame, rayReach))),
                new("uSun", new Vector4Uniform(new Vector4(towardSun, rtgiTraced > 0 ? 1f : 0f))),
                new("uParams", new Vector4Uniform(RtgiParams)),
                new("uNearField", new Vector4Uniform(RtgiNearTrainUniform(rtgiHitsNow))),
                new("uAdapt", new Vector4Uniform(new Vector4(rtgiAdaptive, rtgiAdaptiveK, rtgiDoneError, rtgiTrickle))),
                new("uSkip", new Vector4Uniform(new Vector4((rtgiBenching && rtgiBenchPass == "rays" ? (int)rtgiTrainDouble : 0) | (rtgiPool ? 0 : 16), 0, 0, 0))),
                new("uBudget", new Vector4Uniform(new Vector4(rtgiBudgetError, rtgiBudgetFloor, rtgiSettledSamples, rtgiSettledKeep))),
                new("uBlock", new Vector4Uniform(new Vector4(rtgiBlock ? 1f : 0f, rtgiBlockGlobal, rtgiBlockWindow, lastTotal))),
                new("uUnsureMap", new Vector4Uniform(new Vector4(rtgiHalfRow, rtgiUnsureWritten ? 1f : 0f, RtgiUnsureSize, 0f))),
                new("uSeq", new Vector4Uniform(new Vector4(rtgiBlue ? 1f : 0f, rtgiSettledSun ? 1f : 0f, 0f, 0f))),
                new("uCapture", new Vector4Uniform(RtgiCaptureUniform)),
                new("uVolume", new Vector4Uniform(RtgiVolumeParams)),
                new("uVolumeGeom", new Vector4Uniform(RtgiVolumeGeom)),
                new("uNearCook", new Vector4Uniform(rtgiSurfaceTrain ? RtgiNearParams : Vector4.Zero)),
            },
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
            },
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiPrevRays", rtgiRaysBuf[prev]),
                new ShaderBufferBinding("RtgiVolumeTable", RtgiVolTableOrDummy),
                new ShaderBufferBinding("RtgiVolumeProbes", RtgiVolProbesOrDummy),
                new ShaderBufferBinding("RtgiNearTable", RtgiNearTableOrDummy),
                new ShaderBufferBinding("RtgiNearPoints", rtgiNearCookBuilt ? rtgiNearPoints : rtgiVolDummy),
                new ShaderBufferBinding("RtgiNearRegions", RtgiNearRegionsOrDummy),
                new ShaderBufferBinding("RtgiNearScaleTable", RtgiNearScaleTableOrDummy),
                new ShaderBufferBinding("RtgiCapturePixels", RtgiCapturePixelBuffer),
                new ShaderBufferBinding("RtgiCaptureOut", RtgiCaptureOutBuffer),
                new ShaderBufferBinding("RtgiNearList", RtgiNearListOrDummy),
                new ShaderBufferBinding("RtgiPrevHits", rtgiHitsBuf[prev]),
                new ShaderBufferBinding("RtgiPoints", rtgiPointsBuf[cur]),
                new ShaderBufferBinding("RtgiRayBuffer", rtgiRaysBuf[cur]),
                new ShaderBufferBinding("RtgiShadowOrigins", rtgiOriginsBuf[cur]),
                // The landing requests of the training two frames back (the last frame's may still be appending, beside
                // this placement: its update is async); this frame's training appends to its own, emptied here.
                new ShaderBufferBinding("RtgiRequests", rtgiRequestBufs[RtgiRequestSlot(-2)]),
                new ShaderBufferBinding("RtgiNextRequests", rtgiRequestBufs[RtgiRequestSlot(0)]),
                new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                new ShaderBufferBinding("RtgiDirection", rtgiDirection),
                new ShaderBufferBinding("RtgiMoments", rtgiMoments),
                new ShaderBufferBinding("RtgiConfidence", rtgiConfidence),
                new ShaderBufferBinding("RtgiSeq", rtgiSeq),
                new ShaderBufferBinding("RtgiUnsure", rtgiUnsure),
            });
        // On the bench of placement, N times.
        var placeReps = rtgiBenching && rtgiBenchPass == "rays" ? rtgiBenchReps : 1;
        for (var r = 0; r < placeReps; r++) graph.Dispatch(rtgiRaysPassHandle, place);
        var rays = rtgiRaysBuf[cur];
        var hits = rtgiHitsBuf[cur];
        var origins = rtgiOriginsBuf[cur];
        var occluded = rtgiOccludedBuf[cur];
        var seed = unchecked((rtgiFrameCount + rtgiSeed * 1_000_003u) * 0x9E3779B9u + 0x2545F491u);
        var traceHitSun = rtgiTraceHitSun;
        var hitSunReach = rayReach;
        var volRays = rtgiVolListedNow * rtgiVolRaysNow;
        var volRayBuf = volRays > 0 ? rtgiVolRayBufs[cur] : default;
        var volHitBuf = volRays > 0 ? rtgiVolHitBufs[cur] : default;
        var visRays = rtgiVolVisNow && !rtgiBenching ? RtgiVolVisRoom * RtgiVolVisRays : 0;
        var visRayBuf = visRays > 0 ? rtgiVolVisRays[cur] : default;
        var visHitBuf = visRays > 0 ? rtgiVolVisHits[cur] : default;
        var nearCookRays = rtgiNearCookCountNow > 0 ? rtgiNearCookRaysNow : 0;
        var nearCookRayBuf = nearCookRays > 0 ? rtgiNearCookRayBufs[cur] : default;
        var nearCookHitBuf = nearCookRays > 0 ? rtgiNearCookHitBufs[cur] : default;
        var hitSunDisc = SunDiscTangent;
        void Encode(ExternalWorkSync sync, MetalRayTracer t)
        {
            if (traceHitSun) t.EncodeTrace(sync, rays, 0, hits, 0, n, seed, sun: new Vector4(towardSun, 0.01f), reach: hitSunReach, discTan: hitSunDisc);
            else t.EncodeTrace(sync, rays, 0, hits, 0, n, seed);
            t.EncodeShadow(sync, origins, occluded, 0, n, towardSun, seed, statSlot: 3);
            if (volRays > 0) t.EncodeTrace(sync, volRayBuf, 0, volHitBuf, 0, volRays, seed ^ 0x1B873593u,
                sun: new Vector4(towardSun, 0.01f), reach: hitSunReach, discTan: hitSunDisc);
            if (visRays > 0) t.EncodeTrace(sync, visRayBuf, 0, visHitBuf, 0, visRays, seed ^ 0x68E31DA4u);
            if (nearCookRays > 0) t.EncodeTrace(sync, nearCookRayBuf, 0, nearCookHitBuf, 0, nearCookRays, seed ^ 0x7F4A7C15u);
        }
        // On the bench of the trace, N times.
        var traceReps = rtgiBenching && rtgiBenchPass == "trace" ? rtgiBenchReps : 1;
        void EncodeAll(ExternalWorkSync sync, MetalRayTracer t)
        {
            for (var r = 0; r < traceReps; r++) Encode(sync, t);
        }
        // With async compute (and its update async): a Metal pass of its own after the sun's (rtgi-trace), which the lit
        // pass's join no longer waits for -- in the sun's group, the frame waited at that join for the cache's ~2 ms of
        // rays too, ahead of the upsample and the lit pass (Crytek Sponza's intervals, 2026-10-28).
        if (rtgiAsync && (device.Features & GraphicsFeatures.AsyncCompute) != 0)
            graph.External(rtgiTracePassHandle, sync => tracer.EncodeGroup(sync, () => EncodeAll(sync, tracer)));
        else if (!TracerGroupsOn) graph.External(rtgiInlineTracePassHandle, sync => tracer.EncodeGroup(sync, () => EncodeAll(sync, tracer)));
        else DeferTracerBatch(EncodeAll);
        // The bench: the frame stands still (its number, its buffers' parity).
        if (rtgiBenching) return;
        rtgiFrameCount++;
        rtgiTraced++;
    }

    private void RecordRtgiReadout()
    {
        Matrix4x4.Invert(viewProjJittered, out var invViewProj);
        var sub = LightingSubpixel;
        // Reused from last frame's readout where it is there and the panel's views are off (they paint every pixel).
        // Not while the camera moves (rtgiMotionNow): the cells the view reaches are filling and drifting, and the reused 16 x 16
        // blocks against the fresh ones showed as a checkerboard turning in motion (the user, 2026-10-28) -- the settled-cells
        // rule (rtgi_readout.frag) left a band of it. 0.16 ms a moving frame (Crytek Sponza 1080p, the walk A/B).
        var reuse = rtgiReuse && rtgiView == 0 && rtgiReuseFrame == rtgiFrameCount - 1 && rtgiMotionNow <= 0f;
        graph.Pass(rtgiReadoutPassHandle, scope => fullscreen.Draw(
            scope, rtgiReadoutPipeline,
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                new ShaderTextureBinding("uHistRaw", graph.GetColorTexture(rtgiHistRawHandle)),
                new ShaderTextureBinding("uHistGradient", graph.GetColorTexture(rtgiHistGradientHandle)),
                new ShaderTextureBinding("uHistBlurred", graph.GetColorTexture(rtgiHistBlurredHandle)),
                new ShaderTextureBinding("uHistGuide", graph.GetColorTexture(rtgiHistGuideHandle)),
            },
            pushConstants: null,
            uniforms: new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                new("uParams", new Vector4Uniform(RtgiParams)),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                new("uHalf", new Vector4Uniform(new Vector4(0f, sub.X, sub.Y, 0f))),
                new("uPrevViewProj", new Matrix4x4Uniform(rtgiReusePrevViewProj)),
                // Within four full-res pixels' footprint of the point.
                new("uReuse", new Vector4Uniform(new Vector4(reuse ? 1f : 0f, rtgiFrameCount & 3u, 4f * RtgiPixelFootprint, rtgiReusePrevSub.X + 2f * rtgiReusePrevSub.Y))),
                new("uPrevCamera", new Vector4Uniform(new Vector4(rtgiReusePrevCamera, 0f))),
                new("uNearField", new Vector4Uniform(new Vector4(rtgiNearField, rtgiNoGradient ? 0f : 1f, 0f, 0f))),
                new("uView", new Vector4Uniform(new Vector4(rtgiView, 0f, 0f, 0f))),
                new("uUnsure", new Vector4Uniform(new Vector4(rtgiDoneError, rtgiHalfRow, RtgiUnsureSize, RtgiPixelFootprint))),
                new("uBlurAt", new Vector4Uniform(new Vector4(RtgiBlurNow, rtgiBlurHit, rtgiFrameCount, 0f))),
                new("uVolume", new Vector4Uniform(RtgiVolumeParams)),
                new("uVolumeGeom", new Vector4Uniform(RtgiVolumeGeom)),
                new("uNearCook", new Vector4Uniform(RtgiNearParams)),
                new("uVolume2", new Vector4Uniform(RtgiVolumeReadout)),
            },
            buffers: new[]
            {
                new ShaderBufferBinding("RtgiKeys", rtgiKeys), new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                new ShaderBufferBinding("RtgiAccum", rtgiAccum),
                new ShaderBufferBinding("RtgiFiltered", rtgiFiltered), new ShaderBufferBinding("RtgiDirection", rtgiDirection),
                new ShaderBufferBinding("RtgiMoments", rtgiMoments),
                new ShaderBufferBinding("RtgiConfidence", rtgiConfidence),
                new ShaderBufferBinding("RtgiDoneCount", rtgiDoneCount),
                new ShaderBufferBinding("RtgiUnsure", rtgiUnsure),
                new ShaderBufferBinding("RtgiHitInv", rtgiHitInv),
                new ShaderBufferBinding("RtgiBlurRadius", rtgiBlurRadius),
                new ShaderBufferBinding("RtgiRecord", rtgiRecord),
                new ShaderBufferBinding("RtgiVolumeTable", RtgiVolTableOrDummy),
                new ShaderBufferBinding("RtgiVolumeProbes", RtgiVolProbesOrDummy),
                new ShaderBufferBinding("RtgiVolumeDepth", RtgiVolDepthOrDummy),
                new ShaderBufferBinding("RtgiVolumeCellVis", RtgiVolCellVisOrDummy),
                new ShaderBufferBinding("RtgiVolumeVisRequests", RtgiVolVisRequestsNow),
                new ShaderBufferBinding("RtgiNearTable", RtgiNearTableOrDummy),
                new ShaderBufferBinding("RtgiNearLight", RtgiNearLightOrDummy),
                new ShaderBufferBinding("RtgiNearPoints", RtgiNearPointsOrDummy),
                    new ShaderBufferBinding("RtgiNearRegions", RtgiNearRegionsOrDummy),
                    new ShaderBufferBinding("RtgiNearScaleTable", RtgiNearScaleTableOrDummy),
                    new ShaderBufferBinding("RtgiNearAdj", RtgiNearAdjOrDummy),
            }));
        rtgiUnsureWritten = true;
        // Blurred by how sure the cache is, into the incident the upsample reads (rtgi_blur.frag; at 0 a copy).
        graph.Pass(rtgiBlurPassHandle, scope => fullscreen.Draw(
            scope, rtgiBlurPipeline,
            new[]
            {
                new ShaderTextureBinding("uIn", graph.GetColorTexture(rtgiHalfRawHandle)),
                new ShaderTextureBinding("uGuide", graph.GetColorTexture(rtgiHalfGuideHandle)),
                new ShaderTextureBinding("uReuse", graph.GetColorTexture(rtgiHalfReuseHandle)),
                new ShaderTextureBinding("uGradient", graph.GetColorTexture(incidentGradientHalfHandle)),
            },
            pushConstants: null,
            uniforms: new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                new("uParams", new Vector4Uniform(RtgiParams)),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, rtgiFrameCount))),
                new("uNearField", new Vector4Uniform(new Vector4(rtgiNearField, 0f, 0f, 0f))),
                // A full-res pixel's footprint per metre of distance: 1 / (pixels per unit at unit distance).
                new("uBlur", new Vector4Uniform(new Vector4(RtgiBlurNow, rtgiDoneError, rtgiBlurHit, RtgiPixelFootprint))),
                new("uHalf", new Vector4Uniform(new Vector4(rtgiView == 9 ? 1f : 0f, sub.X, sub.Y, rtgiHalfRow))),
            },
            buffers: new[] { new ShaderBufferBinding("RtgiBlurRadius", rtgiBlurRadius) }));
        rtgiReuseFrame = rtgiFrameCount;
        rtgiReusePrevViewProj = viewProjJittered;
        rtgiReusePrevSub = new Vector2(LightingSubpixel.X, LightingSubpixel.Y);
        rtgiReusePrevCamera = cameraPosition;
    }

    // --rtgi-dump DIR --rtgi-dump-frames F1,F2,...: at those post-load frames the incident field the lit pass read
    // (full res) and the pre-pass normal, for tools/gi-model/rtgi_timeline.py to hold against a --gpu-reference run's
    // points.
    private string? rtgiDumpDir;
    private SortedSet<int> rtgiDumpFrames = new();

    // A dump is due this frame: it reads the frame's own incident light (SponzaLoop.RtgiBudget.cs keeps the screen's light on).
    private bool RtgiDumpDue => rtgiDumpDir is not null && rtgiDumpFrames.Count > 0 && postLoadFrames >= rtgiDumpFrames.Min;

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
        var (w, h) = profFrameSize;
        File.WriteAllText(Path.Combine(dir, "meta.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            frame = postLoadFrames, width = w, height = h, sunToward = new[] { -sunDirection.X, -sunDirection.Y, -sunDirection.Z }, camera = new[] { cameraPosition.X, cameraPosition.Y, cameraPosition.Z },
        }));
        Console.WriteLine($"[VulkanSponza] rtgi dump at frame {postLoadFrames}: {dir}");
    }

    private bool RtgiReadoutReady => !rtgiKeys.Equals(default(GpuBufferHandle));

    // Whether the incident field holds its own occlusion: RTGI's readout does, its rays having found it, so the lit pass
    // takes neither the material's occlusion texture nor anything else for it on top (frame.uIncident.x, lit_body.glsl).
    private bool IncidentHoldsOcclusion => RtgiReadoutReady;

    // A full-res pixel's footprint per metre of distance, as the readout sizes the blur's radius and the blur its plane
    // tolerance (uUnsure.w, uBlur.w): doubled. Both were tuned by eye at render scale 1 on a Retina display when this came
    // from the window's logical height, half the frame's pixels (2026-10-26); with the render height in pixels
    // (renderHeightPx, 2026-10-27), doubled gives them the same numbers there -- the look kept -- and at any other scale
    // the same radius in the world.
    private float RtgiPixelFootprint => 2f / LodErrorScale;

    // At the profile window: how full the cache is and how much its cells hold.
    private string RtgiCounts()
    {
        if (!RtgiReadoutReady || rtgiTraced < 2) return "";
        // A 512th of the slots (hashed, so any run of them is a fair sample), scaled up: reading them all was ~670 MB at 8 M
        // slots, a 400 ms hitch every profile window (San Miguel, 2026-10-26); a 32nd with the device drained first was ~13
        // MB of large arrays every window, the full collections behind a walk's 30-40 ms frames (2026-10-28). Each read
        // waits for its own copy; the counts are a sample, a frame's update racing them is no matter.
        const int Share = 512;
        var count = RtgiSlots / Share;
        var keys = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtgiKeys, 0, count * 4).AsSpan());
        var resolved = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtgiResolved, 0, count * 32).AsSpan());
        var moments = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtgiMoments, 0, count * 48).AsSpan());
        var confidence = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtgiConfidence, 0, count * 16).AsSpan());
        var seen = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtgiDoneCount, 0, 32).AsSpan()).ToArray();
        var work = RtgiWork();
        device.WriteGpuBuffer(rtgiDoneCount, 0, new byte[32]);
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
            $"rtgi: ~{used:N0} of {RtgiSlots:N0} slots held ({100.0 * used / RtgiSlots:0.0}%), {trained:N0} trained, {samples / Math.Max(1, trained):0.0} samples a cell, {100.0 * done / Math.Max(1, trained):0.0}% of cells done, {100.0 * seen[1] / Math.Max(1u, seen[0]):0.0}% of the visible (under 1% error {100.0 * seen[6] / Math.Max(1u, seen[0]):0.0}%, under 3% {100.0 * seen[7] / Math.Max(1u, seen[0]):0.0}%), history cut {cutsLight / Math.Max(1, trained):0.00} times a cell, mean indirect {energy / Math.Max(1, trained):0.000}; {bad:N0} not finite{firstBad}; {rtgiTraced:N0} frames traced{work}; readout lookups {100.0 * seen[3] / Math.Max(1u, seen[2]):0.0}% missed ({seen[2] / (double)Math.Max(1u, seen[0]):0.0} a pixel), {100.0 * seen[5] / Math.Max(1u, seen[4]):0.0}% of pixels no blur");
    }

    // The cache's work a frame since the last window: points live, cell updates by level offset (0 the sample's own, then
    // 1-4 up), cells newly listed by offset, the resolve's listed / rota-skipped / young / settled / settled that drifted,
    // the blend's listed / skipped / blended. What a finest-level training (A) or change-driven work (B) would leave.
    private string RtgiWork()
    {
        var w = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtgiStats, 0, 256).AsSpan()).ToArray();
        device.WriteGpuBuffer(rtgiStats, 0, new byte[256]);
        var frames = Math.Max(1L, rtgiTraced - rtgiStatsTraced);
        rtgiStatsTraced = rtgiTraced;
        string F(int i) => (w[i] / (double)frames).ToString("N0", CultureInfo.InvariantCulture);
        return RtgiBudgetWork() + $"; work a frame: points {F(1)}, updates by offset {F(2)}/{F(3)}/{F(4)}/{F(5)}/{F(6)}, new listings {F(7)}/{F(8)}/{F(9)}/{F(10)}/{F(11)}; "
            + $"resolve listed {F(12)}, rota-skipped {F(13)}, young {F(14)}, settled {F(15)} (drifted {F(16)}); "
            + $"blend listed {F(18)}, skipped {F(19)}, blended {F(20)}; "
            + $"evicted by level {F(21)}/{F(22)}/{F(23)}/{F(24)}/{F(25)}/{F(26)}, holding 8+ {F(28)}; "
            + $"created at own level {F(29)}, above {F(30)}, landings {F(31)}; "
            + $"young: age-capped {F(32)}, cut {F(33)}, epoch-cut {F(34)}, held <8/<32/<128/<256 {F(35)}/{F(36)}/{F(37)}/{F(38)}, "
            + $"samples {F(39)}, by level {F(40)}/{F(41)}/{F(42)}/{F(43)}/{F(44)}/{F(45)}, disagreeing {F(46)}, age 1024+ {F(47)}";
    }
}
