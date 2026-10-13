using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --transport-texels (stage 5a receivers; implies --transport-gpu): what a pixel reads (Shaders/texel.glsl says why).
// The cook places the texels; texel_mark.comp queues those the image reads and that still need rays; texel_gather.comp
// traces them against the cooked patches' light; the incident pass reads them in place of the clipmap where they have
// rays. A texel stops being traced at its target, so a still sun settles once.
internal sealed partial class SponzaLoop
{
    private bool transportTexels;
    // --transport-texel S: the texels' cell, metres. CPU, Sponza's hall: 2.5 cm 10.8% (the gather's own floor at 1024
    // rays), 5 cm 13.5%, 10 cm 14.5%, 20 cm 22.4%.
    private float texelSpacing = 0.05f;
    // --texel-budget N: texels gathered a frame (one pass of 32 rays each); --texel-rays N: the rays a texel stops at;
    // --texel-trust N: the rays at which its answer replaces the clipmap's whole (fewer: blended by the fraction).
    // 1024: the gather's cost is the trace (--texel-ablate on Sponza's hall, 65k rays a frame: 10.5 ms all in, 9.8 ms
    // without its shadow rays, cooked lookups and guide, 0.4 ms without the trace) -- ~8 M rays/s on this GPU. Half the
    // budget, ~5 ms, and only while a view is still settling (texelMaxRays). With frame rays on Metal, 32,768 (~1 M rays)
    // unless --texel-budget says: at 1,024 a sun's turn took ~7,000 frames to re-gather the ~460 k texels a view reads
    // (UploadTexels sets it).
    private int texelBudget = 1024;
    private bool texelBudgetGiven;
    // --texel-levels N: texels at N sizes, each twice the last (5 / 10 / 20 cm); --texel-pixels P: a pixel reads the
    // level whose texel spans about P pixels at its depth, and the coarser ones fill in until it has rays (texel.glsl).
    // A view used to need every surface it showed traced at 5 cm before it settled: ~240k texels on Sponza's hall at
    // 2,048 a frame, the camera "a really slow eraser" (the user), and the frame paying for detail it could not show.
    private int texelLevels = 3;
    // 24: at 8, at this window's resolution a texel of 5 cm still spanned 8 pixels out to ~10 m -- most of Sponza's
    // hall asked for the finest level, and the coarse ones added work instead of replacing it (settling slower, 88 ms).
    private float texelPixels = 24f;
    // --texel-shade-pixels P: the same in shade (texelLevelFor) -- finer, since there the bounce light is all a surface
    // shows.
    private float texelShadePixels = 6f;
    // --texel-ablate N: drop parts of the gather to attribute its cost (texel_gather.comp, uSky.w).
    private int texelAblate;
    // The occlusion estimate's reach (texel_gather.comp): openness counts cosine rays that meet nothing within it. CPU,
    // Sponza's hall: the patches' light x the texel's openness over its patches' at 1 m read 18.6% (energy 0.99) from 64
    // rays, as from 1024 -- against interp 27.4% and a 256-ray gather 18.5%; 0.5 m 19.0%, 0.25 m 20.4%.
    private const float TexelOpenReach = 1.0f;
    // --texel-blend 0|1: blend the occlusion estimate with the gathered light (by inverse variance; 0: gathered alone);
    // --texel-occlusion-error E: the estimate's relative error the blend assumes (its measured mean on the hall).
    private bool texelBlend = true;
    private float texelOcclusionError = 0.18f;
    private int texelTarget = 512;
    // --texel-max-rays N / --texel-noise T: past --texel-rays, a texel keeps gathering until the standard error of its
    // mean is under T of its value, or it reaches N (texel.glsl, texelSettled). Fixed at 1024 rays, the bounce-lit
    // balconies read "pointillist" to the user.
    // 2048: at 16384 the noise target kept thousands of texels gathering for good -- the gather never went idle once a
    // view looked settled (>10 ms a frame on Sponza).
    private int texelMaxRays = 2048;
    // 5%: at 2%, after 5,000 frames on Sponza's hall 106k of the 240k texels the image read were still short of it.
    private float texelNoise = 0.05f;
    // --texel-filter 0|1: the spatial filter over neighbouring texels, refreshed each frame a texel is seen
    // (texel_filter.comp).
    private bool texelFilter = true;
    // The blend's occlusion estimate claims at least texelOcclusionError of the gathered mean as its error, not only of
    // itself (texel_gather.comp): relative to itself alone, a crevice's near-zero estimate claimed near-certainty and won
    // over the gather -- Sponza (every 16th texel, against the raw ray bake): dim texels -22..-29% blended, -5..-7%
    // floored; hashed texels' total -7.1% -> -1.2%, atlas -1.2% -> -0.4%. The testbeds (crevices, -dark, -hashed against a
    // 32-bounce path trace) read within 1-2% either way. --texel-blend-floor 0: as before.
    private bool texelBlendFloor = true;
    // --texel-dump PATH (SponzaLoop.Texels.cs, UploadTexels).
    private string? texelDumpPath;
    // The filter's edge measured against the brighter texel, not the centre (texel_filter.comp): equal to the centre's on
    // every testbed (2026-10-13); off, a switch while Sponza's hashed texels are looked into.
    private bool texelFilterSymmetric;
    // The filter weighs each neighbour by the surface it holds in its cell, as the pixels' lookup does (texel_filter.comp).
    private bool texelFilterArea;
    private const float TexelNoiseFloor = 1e-4f;
    // 32 -- one pass: the blend (texel_gather.comp) already weighs a young gather's noise against the occlusion
    // estimate, so a texel's answer is whole from its first pass. At 64 a pixel showed the clipmap for two passes, and
    // the clipmap reads ~30% bright on Sponza's hall: every view arrived +20% and faded down ("a slow eraser").
    private float texelTrust = 32f;
    // One pixel in each 4 x 4 block queues the texels it reads, a different one each frame (all sixteen every 16
    // frames): a texel is 5 cm, many pixels share one up close, and a converged view queues nothing anyway. At every
    // second pixel each frame the mark cost ~3 ms on Sponza's hall.
    private const int TexelMarkStride = 4;
    private bool texelsUploaded;
    private float lodPixelsOverride = -1f;
    private int texelCount, texelCapacity;
    private Vector4 texelGrid;
    private uint texelFrame;
    private GpuBufferHandle texelHashBuffer, texelBuffer, texelLightBuffer, texelStampBuffer, texelQueueBuffer, texelProbeBuffer, texelEpochBuffer, texelPriorBuffer, texelMomentBuffer, texelFilteredBuffer, texelOpenBuffer, texelBlendBuffer, texelFilterListBuffer;
    // Following the sun: the cook's solve for another sun (SponzaLoop.Transport.cs), the sun it last solved for, the
    // solve in flight, and the texels' epoch -- bumped when new patch light lands; a texel from an older epoch is
    // queued again and keeps its old estimate as one pass's worth.
    private Func<Vector3, Vector3, (Vector3[] Incident, float[] Guides)>? transportRelight;
    // The same for many suns at once, without guides (SponzaLoop.SunNodes.cs).
    private Func<Vector3[], Vector3, Vector3[][]>? transportRelightMany;
    private (Vector3 ToSun, Vector3 Irradiance) transportRelightSun;
    private Task<(Vector3[] Incident, float[] Guides)>? relightTask;
    private (Vector3 ToSun, Vector3 Irradiance) relightTaskSun;
    private Stopwatch? relightClock;
    private Vector4[]? cookedPacked;
    private uint texelEpoch;
    private readonly List<(GpuBufferHandle Buffer, uint Frame)> retiredBuffers = new();
    private (int Frame, Vector2 Degrees)? sunChange;
    private ShaderInterface texelMarkInterface = null!, texelGatherInterface = null!, texelFilterInterface = null!;
    private PassHandle texelMarkPassHandle, texelGatherPassHandle, texelFilterPassHandle;
    private PipelineHandle texelMarkPipeline, texelGatherPipeline, texelFilterPipeline;
    private ShaderInterface texelFilterFillInterface = null!;
    private PassHandle texelFilterFillPassHandle;
    private PipelineHandle texelFilterFillPipeline;
    // Every live texel (the hashed records, and the atlas's not buried in a solid): what the filter sweep walks.
    private GpuBufferHandle texelLiveBuffer;
    private int texelLiveCount;
    // Resting: the texels' light is settled (a settled bake, loaded or just made) for the epoch it was settled at, so
    // nothing the mark could queue would change it and the filtered light is final once swept. The mark, the gather
    // (and its Metal batches, each a split of the frame) and the filter then do not run at all; the sun moving (a new
    // epoch) wakes them, and the texels gather as the image reads them, as before.
    private bool texelsSettled;
    private uint texelSettledEpoch;
    // The filter sweep's next entry in the live list; -1 when none is under way.
    private long filterSweepIssued = -1;
    private bool texelsRestReported;
    // --texel-wavefront: the gather as a wavefront on the ray batches (SponzaLoop.RayBatch.cs; texel_gather phases 3-5),
    // on the software traversal; with frame rays on Metal always, so Metal answers them (the light bake's too). The
    // inline gather (phase 0) otherwise.
    private bool texelWavefront;
    private bool TexelsOnWavefront => texelWavefront || FrameRaysOnHardware;
    private RayBatchPasses texelClosestBatch, texelShadowBatch;
    private PassHandle texelRaysPassHandle, texelShadePassHandle;
    // The wavefront's buffers, grown to the most texels a frame has asked for (placeholders while off: the program
    // declares them either way).
    private GpuBufferHandle texelWaveRays, texelWaveHits, texelWaveState, texelWaveOrigins, texelWaveOccluded;
    // The wavefront's live rays this frame, written by its first phase (texel_gather.comp): the Metal batches stop there.
    private GpuBufferHandle texelWaveLimit;
    private int texelWaveGroups = -1;
    // Texels the filter refreshes a frame at most (texel_filter.comp): the mark lists each texel the image reads once a
    // frame, a pixel in each 4 x 4 block -- ~15k a frame on Sponza's hall.
    private const int TexelFilterCapacity = 32768;

    private void ReadTexelArgs(AppArgs args)
    {
        transportTexels = args.Flag("transport-texels");
        texelWavefront = args.Flag("texel-wavefront");
        ReadLightBakeArgs(args);
        if (args.Float("transport-texel") is { } s) texelSpacing = Math.Clamp(s, 0.01f, 1f);
        if (args.Int("texel-budget") is { } b) { texelBudget = Math.Clamp(b, 64, 1 << 20); texelBudgetGiven = true; }
        if (args.Int("texel-rays") is { } r) texelTarget = Math.Clamp(r, 32, 1 << 16);
        if (args.Int("texel-max-rays") is { } mr) texelMaxRays = Math.Clamp(mr, texelTarget, 1 << 20);
        if (args.Float("texel-noise") is { } tn) texelNoise = Math.Max(0f, tn);
        if (args.Int("texel-filter") is { } tf) texelFilter = tf != 0;
        if (args.Int("texel-blend") is { } tb) texelBlend = tb != 0;
        if (args.Int("texel-blend-floor") is { } bf) texelBlendFloor = bf != 0;
        texelDumpPath = args.String("texel-dump");
        if (args.Int("texel-filter-symmetric") is { } fs) texelFilterSymmetric = fs != 0;
        if (args.Int("texel-filter-area") is { } fa) texelFilterArea = fa != 0;
        if (args.Int("texel-levels") is { } tl) texelLevels = Math.Clamp(tl, 1, 5);
        if (args.Int("texel-ablate") is { } ta) texelAblate = ta;
        if (args.Float("texel-pixels") is { } tp) texelPixels = Math.Max(0.5f, tp);
        if (args.Float("texel-shade-pixels") is { } tsp) texelShadePixels = Math.Max(0.5f, tsp);
        if (args.Float("texel-occlusion-error") is { } toe) texelOcclusionError = Math.Max(1e-3f, toe);
        if (args.Float("texel-trust") is { } t) texelTrust = Math.Max(1f, t);
        // --lod-pixels E: the LOD error budget (0: full detail everywhere) -- the [Tune] field is not reachable from the
        // command line. The texels are cooked from the full-detail ray scene; LOD'd raster puts faces elsewhere.
        if (args.Float("lod-pixels") is { } lod) lodPixelsOverride = Math.Max(0f, lod);
    }

    // The texels' 64-bit key, as texel.glsl packs it: x | y << 16 in the low word, z | bin << 16 in the high.
    private static (uint Lo, uint Hi) TexelKey(int x, int y, int z, int bin, int level) => ((uint)x | ((uint)y << 16), (uint)z | ((uint)bin << 16) | ((uint)level << 21));

    // texel_bin.glsl's texelBin: each component x 1.5 rounded to -1/0/1 (26 directions, axis and 45-degree normals at
    // bins' centres).
    private static int TexelBin(Vector3 n)
    {
        static int Q(float c) => Math.Clamp((int)MathF.Floor(c * 1.5f + 0.5f), -1, 1) + 1;
        return Q(n.X) + 3 * Q(n.Y) + 9 * Q(n.Z);
    }

    // texel.glsl's texelHash.
    private static uint TexelHash(uint lo, uint hi)
    {
        unchecked
        {
            var h = lo * 0x9E3779B1u;
            h ^= hi * 0x85EBCA77u;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            return h;
        }
    }

    // One texel per (cell x normal bin) that architecture passes through: the surface is sampled at half a cell
    // (stratified per triangle, at least one sample on any triangle), both sides; a texel sits at the centroid of its
    // samples with their mean normal. A side is dropped as inside a solid only when 15 of 16 rays meet back faces: at
    // "most of 8" (the patches' rule), 34 of the 576 hall reference points met a culled texel the camera sees --
    // Sponza's one-sided geometry shows its back to many visible surfaces. A texel never seen is never traced (only
    // what the image reads is queued), so a kept buried one costs memory alone.
    // firstLevel: the finest level a triangle gets (0 when null): --lightmap's surfaces start at 1, the atlas being
    // their level 0 (texel.glsl).
    private TexelBake? CookTexels(RayQueryScene scene, Func<int, int, (Vector3 A, Vector3 B, Vector3 C)> tri, Func<int, int, bool> cutout,
        Func<int, int, int>? firstLevel = null)
    {
        if (!transportTexels) return null;
        var clock = Stopwatch.StartNew();
        var s = texelSpacing;
        var min = sceneBoundsMin - new Vector3(2f * s);
        var sample = 0.5f * s;
        // Every triangle's samples, in parallel (a dictionary of cells per worker, merged after, in key order so the
        // texels' order does not depend on the scheduling). A sample whose ray along its normal first meets a BACK face
        // is inside a solid -- the hidden part of a face that runs on into another (the Cornell box's back wall into
        // its side slabs at every corner; Sponza's overlapping pieces), or a slab's inner side -- and is dropped: those
        // pulled the centroids of the texels straddling a corner into the corner, and they gathered as if there (the
        // box's inside corners dotted at every cell).
        var triangleList = new List<(int Instance, int Triangle)>();
        for (var i = 0; i < scene.Instances.Count; i++)
        {
            var triangles = scene.Instances[i].Mesh.Indices.Length / 3;
            for (var t = 0; t < triangles; t++) if (!cutout(i, t)) triangleList.Add((i, t));
        }
        var locals = new ConcurrentBag<Dictionary<ulong, (Vector3 Pos, Vector3 Nrm, int Hits)>>();
        long samples = 0, hidden = 0;
        // --lightmap-buried-why: every 64th dropped sample side, and whether what its ray met was a double-sided sheet.
        var sampleDrops = lightmapBuriedWhy ? new ConcurrentBag<(Vector3 P, Vector3 N, bool DoubleSided)>() : null;
        ProgressBegin("hashed texels: surface samples", triangleList.Count);
        Parallel.For(0, triangleList.Count,
            () => new Dictionary<ulong, (Vector3 Pos, Vector3 Nrm, int Hits)>(),
            (ti, _, local) =>
            {
                ProgressAdd("hashed texels: surface samples");
                var (i, t) = triangleList[ti];
                var firstLevelHere = firstLevel?.Invoke(i, t) ?? 0;
                var (a, b, c) = tri(i, t);
                var cross = Vector3.Cross(b - a, c - a);
                var area = cross.Length() * 0.5f;
                if (area <= 0f) return local;
                var front = Vector3.Normalize(cross);
                var count = Math.Max(1, (int)MathF.Ceiling(area / (sample * sample)));
                var rng = new Random(11 + ti);
                var u0 = (float)rng.NextDouble();
                var v0 = (float)rng.NextDouble();
                long mine = 0, mineHidden = 0;
                for (var k = 0; k < count; k++)
                {
                    var u = (u0 + k * 0.7548777f) % 1f;
                    var v = (v0 + k * 0.5698403f) % 1f;
                    if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                    var at = a + (b - a) * u + (c - a) * v;
                    for (var side = 0; side < 2; side++)
                    {
                        var n = side == 0 ? front : -front;
                        if (ClosestThroughContacts(scene, new Ray(at + n * 1e-3f, n), 0f, float.PositiveInfinity, (uint)(ti * 2 + side)) is { FrontFace: false } inside)
                        {
                            mineHidden++;
                            if (sampleDrops is not null && ((ti * 31 + k * 2 + side) & 63) == 0)
                                sampleDrops.Add((at, n, rayPlacementDoubleSided[inside.Instance]));
                            continue;
                        }
                        // Every level from the same sample: a coarse texel is the union of the fine ones it nests.
                        for (var level = firstLevelHere; level < texelLevels; level++)
                        {
                            var cell = (at - min) / (s * (1 << level));
                            int cx = (int)MathF.Floor(cell.X), cy = (int)MathF.Floor(cell.Y), cz = (int)MathF.Floor(cell.Z);
                            var packed = (ulong)(uint)cx | ((ulong)(uint)cy << 16) | ((ulong)(uint)cz << 32) | ((ulong)(uint)TexelBin(n) << 48) | ((ulong)(uint)level << 56);
                            var e = local.GetValueOrDefault(packed);
                            local[packed] = (e.Pos + at, e.Nrm + n, e.Hits + 1);
                        }
                    }
                    mine++;
                }
                Interlocked.Add(ref samples, mine);
                Interlocked.Add(ref hidden, mineHidden);
                return local;
            },
            local => locals.Add(local));
        ProgressEnd("hashed texels: surface samples");
        var merged = new Dictionary<ulong, (Vector3 Pos, Vector3 Nrm, int Hits)>();
        foreach (var local in locals)
            foreach (var (key, e) in local)
            {
                var m = merged.GetValueOrDefault(key);
                merged[key] = (m.Pos + e.Pos, m.Nrm + e.Nrm, m.Hits + e.Hits);
            }
        var orderedKeys = merged.Keys.OrderBy(k => k).ToArray();
        var sumPos = new List<Vector3>(orderedKeys.Length);
        var sumNrm = new List<Vector3>(orderedKeys.Length);
        var hits = new List<int>(orderedKeys.Length);
        var keys = new List<(uint Lo, uint Hi)>(orderedKeys.Length);
        foreach (var key in orderedKeys)
        {
            var e = merged[key];
            sumPos.Add(e.Pos); sumNrm.Add(e.Nrm); hits.Add(e.Hits);
            keys.Add(TexelKey((int)(key & 0xFFFF), (int)((key >> 16) & 0xFFFF), (int)((key >> 32) & 0xFFFF), (int)((key >> 48) & 0xFF), (int)(key >> 56)));
        }
        var placed = sumPos.Count;
        var pos = new Vector3[placed];
        var nrm = new Vector3[placed];
        for (var i = 0; i < placed; i++) { pos[i] = sumPos[i] / hits[i]; nrm[i] = Vector3.Normalize(sumNrm[i]); }
        // Also dropped when enclosed, as the lightmap's records are (SponzaLoop.Lightmap.cs, LightmapEnclosedTests): three
        // quarters of the rays meet a single-sided back face. A texel pressed against a solid (the crevices' roof over
        // its corner block) loses about half the rays that reach the solid's contact with the next (coplanar faces, the
        // other's front one answering), and kept 13-14 of 16: 12k such texels lived in the crevices, traced and never
        // settled. How many only this drops, and how many of those the camera sees, is reported.
        var keep = new bool[placed];
        var enclosedOnly = new bool[placed];
        var sheetsOnly = new bool[placed];   // dropped by the 15-of-16 rule with double-sided sheets' back faces counted, not enclosed
        ProgressBegin("hashed texels: inside a solid?", placed);
        Parallel.For(0, placed, i =>
        {
            ProgressAdd("hashed texels: inside a solid?");
            var r = new Random(91 + i);
            int back = 0, solid = 0;
            const int tests = 16;
            for (var k = 0; k < tests; k++)
            {
                var d = CosineHemisphereCpu(nrm[i], r);
                if (ClosestThroughContacts(scene, new Ray(pos[i] + nrm[i] * 1e-3f, d), 0f, float.PositiveInfinity, (uint)r.Next()) is { } h && !h.FrontFace)
                {
                    back++;
                    if (!rayPlacementDoubleSided[h.Instance]) solid++;
                }
            }
            var enclosed = solid * 4 >= tests * 3;
            keep[i] = back < tests - 1 && !enclosed;
            enclosedOnly[i] = back < tests - 1 && enclosed;
            sheetsOnly[i] = back >= tests - 1 && !enclosed;
        });
        ProgressEnd("hashed texels: inside a solid?");
        var kept = new List<int>(placed);
        for (var i = 0; i < placed; i++) if (keep[i]) kept.Add(i);
        {
            var eye = cameraPosition;
            int only = 0, seen = 0;
            for (var i = 0; i < placed; i++)
            {
                if (!enclosedOnly[i]) continue;
                only++;
                var toward = pos[i] + nrm[i] * 0.01f - eye;
                var dist = toward.Length();
                if (dist < 0.1f || dist > 12f || Vector3.Dot(toward, nrm[i]) >= 0f) continue;
                if (scene.Closest(new Ray(eye, toward / dist), 0f, dist, 1u) is null) seen++;
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] texels: {only:N0} dropped as enclosed alone (12 of 16 rays meet a single-sided back face), {seen:N0} of them in the camera's sight within 12 m."));
            if (lightmapBuriedWhy)
            {
                var dump = Environment.GetEnvironmentVariable("BLIX_DIAG_DUMP");
                ReportExposed("hashed texels, dropped as enclosed alone", scene,
                    Enumerable.Range(0, placed).Where(i => enclosedOnly[i]).Select(i => (pos[i], nrm[i])).ToList(), dump is null ? null : dump + ".hashed-enclosed");
                var keptSample = kept.Where((_, k) => k % Math.Max(1, kept.Count / 50_000) == 0).Select(i => (pos[i], nrm[i])).ToList();
                ReportExposed("hashed texels, kept (control, a sample)", scene, keptSample);
                // Gap 2: dropped by 15 of 16 back faces only because sheets' back faces counted.
                ReportExposed("hashed texels, dropped with sheets counted, not enclosed", scene,
                    Enumerable.Range(0, placed).Where(i => sheetsOnly[i]).Select(i => (pos[i], nrm[i])).ToList(), dump is null ? null : dump + ".hashed-sheets");
                // Gap 1: the sample rule's drops (one ray along the normal, any distance, a back face first).
                var drops = sampleDrops!.ToArray();
                ReportExposed($"hashed samples, dropped by the normal ray (every 64th; {drops.Count(d => d.DoubleSided):N0} of {drops.Length:N0} met a sheet)", scene,
                    drops.Select(d => (d.P, d.N)).ToList(), dump is null ? null : dump + ".sample-drops");
                ReportExposed("hashed samples, dropped by the normal ray at a sheet", scene, drops.Where(d => d.DoubleSided).Select(d => (d.P, d.N)).ToList());
                // Gap 3: how far below its surface a texel's centroid sits (a probe from 5 cm up along the normal), by level;
                // a centroid inside the surface starts the texel's test rays inside the solid.
                var depthBins = new long[texelLevels, 4];   // at or above (or within 1 mm), below by 1 mm-1 cm, 1-3 cm, more
                var enclosedBelow = 0;
                Parallel.For(0, placed, i =>
                {
                    if (!keep[i] && !enclosedOnly[i]) return;
                    var n = nrm[i];
                    // Below the surface: the probe met it before reaching the centroid (T < 0.05). No surface within
                    // 10 cm counts as above.
                    var probe = scene.Closest(new Ray(pos[i] + n * 0.05f, -n), 0f, 0.1f, 1u);
                    var depth = probe is { } h ? 0.05f - h.T : -1f;
                    var bin = depth <= 1e-3f ? 0 : depth <= 0.01f ? 1 : depth <= 0.03f ? 2 : 3;
                    Interlocked.Increment(ref depthBins[(int)(keys[i].Hi >> 21), bin]);
                    if (enclosedOnly[i] && depth > 1e-3f) Interlocked.Increment(ref enclosedBelow);
                });
                for (var level = 0; level < texelLevels; level++)
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"[VulkanSponza] hashed texels, level {level}: centroid at or above its surface {depthBins[level, 0]:N0}, below by 1 mm-1 cm {depthBins[level, 1]:N0}, 1-3 cm {depthBins[level, 2]:N0}, more {depthBins[level, 3]:N0}."));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] hashed texels: of the {enclosedOnly.Count(e => e):N0} dropped as enclosed alone, {enclosedBelow:N0} have their centroid over 1 mm below their surface."));
            }
        }
        texelCount = kept.Count;
        texelCapacity = 1;
        while (texelCapacity < 2 * texelCount) texelCapacity <<= 1;
        var table = new uint[texelCapacity * 4];
        Array.Fill(table, 0xFFFFFFFFu);
        var texels = new Vector4[texelCount * 2];
        long longestProbe = 0;
        for (var j = 0; j < texelCount; j++)
        {
            var i = kept[j];
            texels[2 * j] = new Vector4(pos[i], hits[i]);   // w: its surface samples (texel.glsl weighs by area)
            texels[2 * j + 1] = new Vector4(nrm[i], keys[i].Hi >> 21);   // w: its level
            var (lo, hi) = keys[i];
            var slot = TexelHash(lo, hi) & (uint)(texelCapacity - 1);
            var probe = 0;
            while (table[slot * 4] != 0xFFFFFFFFu) { slot = (slot + 1) & (uint)(texelCapacity - 1); probe++; }
            longestProbe = Math.Max(longestProbe, probe);
            table[slot * 4] = lo; table[slot * 4 + 1] = hi; table[slot * 4 + 2] = (uint)j; table[slot * 4 + 3] = 0;
        }
        var bake = new TexelBake(table, texels, texelCount, texelCapacity, new Vector4(min, s));
        var mb = (table.Length * 4L + texels.Length * 16L + texelCount * 92L) / 1048576.0;
        var perLevel = new int[texelLevels];
        foreach (var i in kept) perLevel[keys[i].Hi >> 21]++;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] texels: {samples:N0} surface samples at {sample * 100:0.#} cm ({hidden:N0} sides inside a solid dropped) -> {placed:N0} texels at {s * 100:0.#} cm and {texelLevels - 1} coarser level(s), {texelCount:N0} kept (the rest inside a solid; by level {string.Join(" / ", perLevel.Select(c => c.ToString("N0", CultureInfo.InvariantCulture)))}); hash {texelCapacity:N0} slots (longest probe {longestProbe}); {mb:0.0} MB; cooked in {clock.Elapsed.TotalSeconds:0.0} s."));
        if (longestProbe >= 64) Console.WriteLine("[VulkanSponza] texels: WARNING a hash probe run exceeds the shaders' 64 (TEXEL_PROBES): some texels cannot be found.");
        return bake;
    }

    // What the texel cook makes (and the transport cache keeps): the hash table, the texels (position + samples,
    // normal + level), their count, the table's slots, and the grid (finest cells' origin xyz, spacing w).
    internal sealed record TexelBake(uint[] Table, Vector4[] Texels, int Count, int Capacity, Vector4 Grid);

    // light: the baked light (SponzaLoop.LightBake.cs), or null -- the texels start from nothing, and with bakePath the
    // bake begins (firstAtlas: where the lightmap's records start, the count without them).
    private void UploadTexels(TexelBake bake, TexelLightState? light = null, string? bakePath = null, int firstAtlas = -1, uint[]? resolved = null)
    {
        texelCount = bake.Count;
        texelCapacity = bake.Capacity;
        // Every size below is int bytes: past 2 GB a buffer's size wraps negative. Refuse loudly instead.
        _ = checked(bake.Texels.Length * 16 + texelCount * 16);
        var stamps = new uint[texelCount];
        Array.Fill(stamps, 0xFFFFFFFFu);
        if (!texelBudgetGiven && FrameRaysOnHardware) texelBudget = 32768;
        var queueWords = 8 + 4 * Math.Max(texelBudget, LightBakeBudget);
        texelHashBuffer = Own(device.CreateGpuBuffer(bake.Table.Length * 4, MemoryMarshal.AsBytes(bake.Table.AsSpan()), "sponza.texels.hash"));
        texelBuffer = Own(device.CreateGpuBuffer(bake.Texels.Length * 16, MemoryMarshal.AsBytes(bake.Texels.AsSpan()), "sponza.texels"));
        // The gather's buffers (zeroed by CreateGpuBuffer where nothing is given); with a resolved cache, placeholders
        // until a light change wakes them (SponzaLoop.TexelRest.cs).
        var atRest = resolved is not null;
        if (atRest && texelRestPlaceholder.Equals(default(GpuBufferHandle)))
            texelRestPlaceholder = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.texels.rest-placeholder"));
        GpuBufferHandle Gather(int bytes, ReadOnlySpan<byte> initial, string name) =>
            atRest ? texelRestPlaceholder : Own(device.CreateGpuBuffer(bytes, initial, name));
        texelLightBuffer = Gather(texelCount * 16, light is null ? default : MemoryMarshal.AsBytes(light.Light.AsSpan()), "sponza.texels.light");
        texelStampBuffer = Gather(texelCount * 4, MemoryMarshal.AsBytes(stamps.AsSpan()), "sponza.texels.stamp");
        texelQueueBuffer = Own(device.CreateGpuBuffer(queueWords * 4, name: "sponza.texels.queue"));
        texelPriorBuffer = Gather(texelCount * 16, default, "sponza.texels.prior");
        texelFilteredBuffer = Gather(texelCount * 16, default, "sponza.texels.filtered");
        texelFilterListBuffer = Own(device.CreateGpuBuffer((2 + TexelFilterCapacity) * 4, name: "sponza.texels.filter-list"));
        texelOpenBuffer = Gather(texelCount * 16, light is null ? default : MemoryMarshal.AsBytes(light.Open.AsSpan()), "sponza.texels.open");
        texelBlendBuffer = Gather(texelCount * 16, light is null ? default : MemoryMarshal.AsBytes(light.Blend.AsSpan()), "sponza.texels.blend");
        texelMomentBuffer = Gather(texelCount * 4, light is null ? default : MemoryMarshal.AsBytes(light.Moment.AsSpan()), "sponza.texels.moment");
        texelEpochBuffer = Gather(texelCount * 4, default, "sponza.texels.epoch");
        texelProbeBuffer = Own(device.CreateGpuBuffer(24 * 24 * 48, new byte[24 * 24 * 48], "sponza.texels.probe"));
        texelGrid = bake.Grid;
        texelsUploaded = true;
        if (texelDumpPath is { } dump)
        {
            // --texel-dump PATH: every texel record (two vec4s: position, w its samples or +-4 an atlas texel's; mean
            // face normal, w its level or atlas coordinate) and where the atlas's begin, then the run closes.
            using (var w = new BinaryWriter(File.Create(dump)))
            {
                w.Write(texelCount);
                w.Write(firstAtlas);
                w.Write(MemoryMarshal.AsBytes(bake.Texels.AsSpan(0, texelCount * 2)));
            }
            Console.WriteLine($"[VulkanSponza] texels: {texelCount:N0} records dumped to {dump}; closing.");
            host.RequestClose();
        }
        {
            var all = firstAtlas < 0 ? texelCount : firstAtlas;
            var live = new List<uint>(texelCount);
            for (var i = 0; i < texelCount; i++)
                if (i < all || MathF.Abs(bake.Texels[2 * i].W) >= 2f) live.Add((uint)i);
            texelLiveCount = live.Count;
            texelLiveBuffer = atRest ? texelRestPlaceholder
                : Own(device.CreateGpuBuffer(Math.Max(live.Count, 1) * 4, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(live)), "sponza.texels.live"));
            // --lightlets: the live texels' sun records (SponzaLoop.LightletBake), read or baked.
            texelLiveIds = live.ToArray();
            BeginLightlets(bake.Texels, texelLiveIds, texelCount, bakePath);
        }
        // A resolved cache: at rest from the first frame. A full-state cache (LightBakeVersion 4, settled by its refining):
        // filtered once, then at rest, resolved -- and saved in that form for the next run. Neither: the bake begins.
        if (atRest) StartTexelsAtRest(resolved!);
        else if (light is not null)
        {
            SettleTexels("the baked light loaded");
            if (TexelsMayRest && bakePath is not null) pendingResolvedSave = ResolvedPathFor(bakePath);
        }
        else if (bakePath is not null && !BeginSplitLight() && !FoldBootSunIntoCook(bakePath))
        {
            lightBakePath = bakePath;
            BeginLightBake(texelLiveIds!, texelCount);
        }
    }

    // Each frame (with the cooked patches on the GPU): when the sun has turned more than a quarter degree (or changed
    // strength) from the one last solved, solve for it in the background; when that lands, upload the patches' new
    // light and bump the texels' epoch. A sun that keeps moving is chased one solve at a time.
    private void FollowSun()
    {
        StepSunSweep();
        if (sunChange is { } change && postLoadFrames == change.Frame)
        {
            sunYaw = change.Degrees.X * MathF.PI / 180f;
            sunPitch = change.Degrees.Y * MathF.PI / 180f;
            UpdateSunDirection();
            Console.WriteLine($"[VulkanSponza] sun turned by --sun-change at frame {postLoadFrames}: {sunDirection}");
        }
        for (var i = retiredBuffers.Count - 1; i >= 0; i--)
        {
            if (texelFrame - retiredBuffers[i].Frame < 4u) continue;
            device.DestroyGpuBuffer(retiredBuffers[i].Buffer);
            ownedGpuBuffers.Remove(retiredBuffers[i].Buffer);
            retiredBuffers.RemoveAt(i);
        }
        if (transportRelight is null || cookedPacked is null) return;
        if (relightTask is { IsCompleted: true } done)
        {
            var (incident, guides) = done.Result;
            UploadCookedGuides(guides);
            for (var i = 0; i < incident.Length; i++) cookedPacked[3 * i + 2] = new Vector4(incident[i], 0f);
            RetireGpuBuffer(cookedPatchBuffer);
            cookedPatchBuffer = Own(device.CreateGpuBuffer(cookedPacked.Length * 16, MemoryMarshal.AsBytes(cookedPacked.AsSpan()), "sponza.cooked.patches"));
            transportRelightSun = relightTaskSun;
            // The split light: the new sun's part read or cooked, the sky's kept (SponzaLoop.SplitLight.cs).
            if (splitLight) SplitSunChanged(transportRelightSun.ToSun);
            // The potential suns answer a sun in the band (RecordPotentialSuns): nothing to bake.
            else if (!PotentialSunsAnswer(transportRelightSun.ToSun))
            {
                texelEpoch++;
                RebakeForSun(transportRelightSun);
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport: patch light re-solved for the sun in {relightClock!.Elapsed.TotalSeconds:0.0} s; texels re-gather (epoch {texelEpoch})."));
            relightTask = null;
        }
        if (relightTask is not null) return;
        var toSun = -Vector3.Normalize(sunDirection);
        var irradiance = EffectiveSunIrradiance;
        var turned = MathF.Acos(Math.Clamp(Vector3.Dot(toSun, transportRelightSun.ToSun), -1f, 1f)) * 180f / MathF.PI;
        var changed = Vector3.Distance(irradiance, transportRelightSun.Irradiance) > 1e-3f * MathF.Max(1f, transportRelightSun.Irradiance.Length());
        if (turned < 0.25f && !changed) return;
        relightTaskSun = (toSun, irradiance);
        relightClock = Stopwatch.StartNew();
        var relight = transportRelight;
        relightTask = Task.Run(() => relight(toSun, irradiance));
    }

    // The mark's pixel in its 4 x 4 block this frame: all sixteen in each run of 16 frames, in an order that changes
    // run to run -- in step with frame mod 16 it always met a pixel under the same TAA jitter.
    private static uint MarkOffset(uint frame)
    {
        var run = frame / 16u;
        var h = run * 2654435761u ^ 0x9E3779B9u;
        h ^= h >> 15;
        // An odd multiplier and an offset permute 0..15.
        return ((frame % 16u) * ((h & 7u) * 2u + 1u) + (h >> 8)) % 16u;
    }

    // texel.glsl's texelLevelFor: levels, a pixel's width at unit depth (|M22| may carry a Y flip), the pixels a texel
    // should span.
    private Vector4 TexelLevelsUniform(int frameHeight) =>
        new(texelLevels, 2f / (MathF.Abs(cameraProjection.M22) * Math.Max(1, frameHeight)), texelPixels, texelShadePixels);

    // What the level rule's sunlit test reads (texel_mark.comp, incident_clipmap.frag): the sun, the cascades.
    private IEnumerable<ShaderUniform> TexelSunUniforms() => new ShaderUniform[]
    {
        new("uTexelSun", new Vector4Uniform(new Vector4(sunDirection, 0f))),
        new("uTexelCascadeVP", new Matrix4x4ArrayUniform(cascadeViewProj)),
    };

    private IEnumerable<ShaderTextureBinding> TexelCascadeTextures() => new[]
    {
        new ShaderTextureBinding("uTexelCascadeMaps[0]", graph.GetDepthTexture(cascadeHandles[0])),
        new ShaderTextureBinding("uTexelCascadeMaps[1]", graph.GetDepthTexture(cascadeHandles[1])),
        new ShaderTextureBinding("uTexelCascadeMaps[2]", graph.GetDepthTexture(cascadeHandles[2])),
    };

    // A buffer a frame in flight may still read: destroyed four frames on (FollowSun).
    private void RetireGpuBuffer(GpuBufferHandle buffer) => retiredBuffers.Add((buffer, texelFrame));

    private void EnsureTexelPlaceholders()
    {
        if (!texelHashBuffer.Equals(default(GpuBufferHandle))) return;
        texelHashBuffer = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.texels.hash.none"));
        texelBuffer = Own(device.CreateGpuBuffer(32, new byte[32], "sponza.texels.none"));
        texelLightBuffer = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.texels.light.none"));
        texelPriorBuffer = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.texels.prior.none"));
        texelFilteredBuffer = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.texels.filtered.none"));
        texelBlendBuffer = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.texels.blend.none"));
    }

    // What the incident pass binds and reads: the texels, or placeholders with the switch off.
    private (Vector4 Grid, Vector4 Params, ShaderBufferBinding[] Buffers) TexelIncidentInputs()
    {
        EnsureTexelPlaceholders();
        return (texelGrid, new Vector4(texelsUploaded ? texelCapacity : 1, texelsUploaded ? 1f : 0f, texelTrust, texelFilter ? 1f : 0f), new[]
        {
            new ShaderBufferBinding("TexelHash", texelHashBuffer),
            new ShaderBufferBinding("Texels", texelBuffer),
            new ShaderBufferBinding("TexelLight", texelLightBuffer),
            new ShaderBufferBinding("TexelPrior", texelPriorBuffer),
            new ShaderBufferBinding("TexelFiltered", texelFilteredBuffer),
            new ShaderBufferBinding("TexelBlend", texelBlendBuffer),
            new ShaderBufferBinding("TexelStamp", texelStampBuffer.Equals(default(GpuBufferHandle)) ? texelBlendBuffer : texelStampBuffer),
            new ShaderBufferBinding("TexelResolved", texelResolvedBuffer.Equals(default(GpuBufferHandle)) ? texelBlendBuffer : texelResolvedBuffer),
        });
    }

    // The gather wavefront's buffers for `groups` texels of 32 rays, grown as asked (one-element placeholders while off).
    private IEnumerable<ShaderBufferBinding> TexelWaveBindings(int groups)
    {
        var want = TexelsOnWavefront ? Math.Max(groups, texelWaveGroups) : Math.Max(texelWaveGroups, 0);
        if (texelWaveGroups < 0 || want > texelWaveGroups)
        {
            if (texelWaveGroups >= 0)
            {
                device.WaitIdle();
                foreach (var b in new[] { texelWaveRays, texelWaveHits, texelWaveState, texelWaveOrigins, texelWaveOccluded }) Disown(b);
            }
            var rays = Math.Max(want * 32, 1);
            texelWaveRays = Own(device.CreateGpuBuffer(rays * 32, name: "sponza.texel.wave-rays"));
            texelWaveHits = Own(device.CreateGpuBuffer(rays * 48, name: "sponza.texel.wave-hits"));
            texelWaveState = Own(device.CreateGpuBuffer(rays * 32, name: "sponza.texel.wave-state"));
            texelWaveOrigins = Own(device.CreateGpuBuffer(rays * 16, name: "sponza.texel.wave-origins"));
            texelWaveOccluded = Own(device.CreateGpuBuffer(rays * 4, name: "sponza.texel.wave-occluded"));
            texelWaveGroups = want;
        }
        yield return new ShaderBufferBinding("GatherRays", texelWaveRays);
        yield return new ShaderBufferBinding("GatherHits", texelWaveHits);
        yield return new ShaderBufferBinding("GatherRayState", texelWaveState);
        yield return new ShaderBufferBinding("GatherShadowOrigins", texelWaveOrigins);
        yield return new ShaderBufferBinding("GatherOccluded", texelWaveOccluded);
        if (texelWaveLimit.Equals(default(GpuBufferHandle))) texelWaveLimit = Own(device.CreateGpuBuffer(16, name: "sponza.texel.wave-limit"));
        yield return new ShaderBufferBinding("TexelWaveLimit", texelWaveLimit);
    }

    private void RecordTexels(Matrix4x4 invProjection, Matrix4x4 invView, int width, int height)
    {
        if (!texelsUploaded || rayBlockBuffers.Length == 0) return;
        if (sunNodeStage != SunNodeStage.Off) RecordSunNodes();
        RecordPotentialSuns();
        RecordPatchSplitCheck();
        RecordSplitCompose();
        FollowSun();
        SaveLightBakeWhenDone();
        LightletBakeWhenReady();
        // The atlas's light is gathered against the bake scene (SponzaLoop.BakeScene.cs) once it is there, never before.
        if (BakeSceneWanted && !bakeSceneSkipped && BakeTracer is null && !bakeTracerFailed && !bakeSceneReleased) return;
        var texelParams = new Vector4(texelCapacity, texelBudget, texelTarget, texelFrame);
        // While the light bakes (SponzaLoop.LightBake.cs) its slice takes the queue the mark would fill, at its budget.
        var baking = lightBakeActive;
        if (!baking && texelsSettled)
        {
            if (texelEpoch != texelSettledEpoch)
            {
                WakeTexels(keepResting: false);
                texelsSettled = false;
                filterSweepIssued = -1;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] texels: the light changed (epoch {texelEpoch}); they gather and filter as the image reads them."));
            }
            else
            {
                if (filterSweepIssued >= 0) RecordFilterSweep();
                else
                {
                    if (!texelsRestReported)
                    {
                        texelsRestReported = true;
                        Console.WriteLine("[VulkanSponza] texels: settled and filtered; the mark, the gather and the filter rest until the light changes.");
                    }
                    RestTexels();
                }
                texelFrame++;
                return;
            }
        }
        var gatherBudget = baking ? RecordLightBake() : texelBudget;
        var groups = (((width + TexelMarkStride - 1) / TexelMarkStride + 7) / 8, ((height + TexelMarkStride - 1) / TexelMarkStride + 7) / 8);
        // The mark's pixel in each 4 x 4 block. Under RenderSettings.HalfResLighting the incident pass reads only the
        // pixels at this frame's LightingSubpixel of each 2x2, so the mark takes one of the block's four of those (all
        // four over four frames of a run): a texel it queues is one the image reads, under the same jitter.
        var markIndex = MarkOffset(texelFrame);
        var markOffset = halfLightingNow
            ? new Vector2(2 * (markIndex & 1u) + LightingSubpixel.X, 2 * ((markIndex >> 1) & 1u) + LightingSubpixel.Y)
            : new Vector2(markIndex % 4, markIndex / 4);
        if (!baking)
        graph.Dispatch(texelMarkPassHandle, new DispatchCommand(texelMarkPipeline, groups.Item1, groups.Item2, 1,
            new ShaderUniform[]
            {
                new("uInvProjection", new Matrix4x4Uniform(invProjection)),
                new("uInvView", new Matrix4x4Uniform(invView)),
                new("uTarget", new Vector4Uniform(new Vector4(width, height, TexelMarkStride, 0f))),
                new("uOffset", new Vector4Uniform(new Vector4(markOffset.X, markOffset.Y, texelEpoch, 0f))),
                new("uNoise", new Vector4Uniform(new Vector4(texelMaxRays, texelNoise, TexelNoiseFloor, TexelFilterCapacity))),
                new("uTexelLevels", new Vector4Uniform(TexelLevelsUniform(height))),
                new("uTexelGrid", new Vector4Uniform(texelGrid)),
                new("uTexelParams", new Vector4Uniform(texelParams)),
                new("uLightmap", new Vector4Uniform(LightmapUniform)),
            }.Concat(TexelSunUniforms()).ToArray(),
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
            }.Concat(TexelCascadeTextures()).Append(LightmapTexelTexture()).ToArray(),
            Buffers: new[]
            {
                new ShaderBufferBinding("TexelHash", texelHashBuffer),
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("TexelLight", texelLightBuffer),
                new ShaderBufferBinding("TexelStamp", texelStampBuffer),
                new ShaderBufferBinding("TexelQueue", texelQueueBuffer),
                new ShaderBufferBinding("TexelProbe", texelProbeBuffer),
                new ShaderBufferBinding("TexelEpoch", texelEpochBuffer),
                new ShaderBufferBinding("TexelMoment", texelMomentBuffer),
                new ShaderBufferBinding("TexelFilterList", texelFilterListBuffer),
                new ShaderBufferBinding("LightmapIndexMap", LightmapIndexMap()),
            }));
        // Probe mode every frame (576 threads): the census reads the latest.
        {
            graph.Dispatch(texelMarkPassHandle, new DispatchCommand(texelMarkPipeline, 3, 3, 1,
                new ShaderUniform[]
                {
                    new("uInvProjection", new Matrix4x4Uniform(invProjection)),
                    new("uInvView", new Matrix4x4Uniform(invView)),
                    new("uTarget", new Vector4Uniform(new Vector4(width, height, 1f, 1f))),
                    new("uOffset", new Vector4Uniform(new Vector4(0f, 0f, texelEpoch, 0f))),
                    new("uNoise", new Vector4Uniform(new Vector4(texelMaxRays, texelNoise, TexelNoiseFloor, TexelFilterCapacity))),
                new("uTexelLevels", new Vector4Uniform(TexelLevelsUniform(height))),
                    new("uTexelGrid", new Vector4Uniform(texelGrid)),
                    new("uTexelParams", new Vector4Uniform(texelParams)),
                }.Concat(TexelSunUniforms()).Append(new ShaderUniform("uLightmap", new Vector4Uniform(LightmapUniform))).ToArray(),
                new[]
                {
                    new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                    new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                }.Concat(TexelCascadeTextures()).Append(LightmapTexelTexture()).ToArray(),
                Buffers: new[]
                {
                    new ShaderBufferBinding("TexelHash", texelHashBuffer),
                    new ShaderBufferBinding("Texels", texelBuffer),
                    new ShaderBufferBinding("TexelLight", texelLightBuffer),
                    new ShaderBufferBinding("TexelStamp", texelStampBuffer),
                    new ShaderBufferBinding("TexelQueue", texelQueueBuffer),
                    new ShaderBufferBinding("TexelProbe", texelProbeBuffer),
                    new ShaderBufferBinding("TexelEpoch", texelEpochBuffer),
                    new ShaderBufferBinding("TexelMoment", texelMomentBuffer),
                new ShaderBufferBinding("TexelFilterList", texelFilterListBuffer),
                    new ShaderBufferBinding("LightmapIndexMap", LightmapIndexMap()),
                }));
        }
        var gatherBuffers = rayBlockBuffers
            .Append(new ShaderBufferBinding("Texels", texelBuffer))
            .Append(new ShaderBufferBinding("TexelLight", texelLightBuffer))
            .Append(new ShaderBufferBinding("TexelQueue", texelQueueBuffer))
            .Append(new ShaderBufferBinding("TexelEpoch", texelEpochBuffer))
            .Append(new ShaderBufferBinding("TexelPrior", texelPriorBuffer))
            .Append(new ShaderBufferBinding("TexelMoment", texelMomentBuffer))
            .Append(new ShaderBufferBinding("CookedGuides", cookedGuideBuffer))
            .Append(new ShaderBufferBinding("TexelHash", texelHashBuffer))
            .Append(new ShaderBufferBinding("TexelFiltered", texelFilteredBuffer))
            .Append(new ShaderBufferBinding("TexelOpen", texelOpenBuffer))
            .Append(new ShaderBufferBinding("TexelBlend", texelBlendBuffer))
            .Concat(CookedBuffers())
            .Concat(TexelWaveBindings(gatherBudget)).ToArray();
        var declared = texelGatherInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        var wave = TexelsOnWavefront;
        // The light bake's gathers trace the bake scene; the runtime's the frame's tracer. Either way on Metal the batches
        // stop at the live count the first phase writes, and the empty slots past it are left as they are.
        var gatherOn = baking ? BakeTracer : null;
        var limited = wave && FrameRaysOnHardware && (gatherOn ?? (EnsureFrameTracer() ? frameTracer : null)) is not null;
        DispatchCommand Gather(int mode) => new(texelGatherPipeline, gatherBudget, 1, 1,
            new ShaderUniform[]
            {
                new("uTexelGrid", new Vector4Uniform(texelGrid)),
                new("uTexelParams", new Vector4Uniform(texelParams with { Y = gatherBudget })),
                new("uSunDirection", new Vector4Uniform(new Vector4(sunDirection, 0f))),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 1f))),
                // A ray's sky as the screen probes take it: strength 1, a little blurred (one mip).
                new("uSky", new Vector4Uniform(new Vector4(1f, 1f, texelEpoch, texelAblate))),
                new("uCascadeVP", new Matrix4x4ArrayUniform(cascadeViewProj)),
                new("uCascadeReceivers", new Matrix4x4Uniform(CascadeReceivers)),
                new("uOpen", new Vector4Uniform(new Vector4(TexelOpenReach, texelOcclusionError, texelBlend ? 1f : 0f, texelBlendFloor ? 1f : 0f))),
                new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                new("uCookedDims", new Vector4Uniform(cookedDims)),
                new("uWave", new Vector4Uniform(new Vector4(mode, limited ? 1f : 0f, 0f, 0f))),
            },
            new[]
            {
                new ShaderTextureBinding("uSkyRadiance", envCubeTexture),
                new ShaderTextureBinding("uCascadeShadowMaps[0]", graph.GetDepthTexture(cascadeHandles[0])),
                new ShaderTextureBinding("uCascadeShadowMaps[1]", graph.GetDepthTexture(cascadeHandles[1])),
                new ShaderTextureBinding("uCascadeShadowMaps[2]", graph.GetDepthTexture(cascadeHandles[2])),
            },
            Buffers: gatherBuffers.Where(b => declared.Contains(b.Name)).ToArray());
        // The light bake's last frame (FinishLightBake) queues no slice and the mark has not run: nothing to gather.
        if (gatherBudget == 0) { }
        else if (wave)
        {
            // The gather as a wavefront: rays out, their hits shaded, the hits' sun out, the answers gathered. Each batch
            // on Metal when frame rays are (RecordClosestBatch), else on the software traversal.
            var rayCount = gatherBudget * 32;
            // The bake scene answers the light bake's gathers; the runtime gathers take
            // the frame's tracer, as they do once it is released. rayCount is the budget's; the batches trace what the
            // first phase found queued (texelWaveLimit), so a settled view costs nothing here.
            var seed = unchecked(texelFrame * 0x9E3779B9u + 0x7F4A7C15u);
            graph.Dispatch(texelRaysPassHandle, Gather(3));
            RecordClosestBatch(texelClosestBatch, texelWaveRays, texelWaveHits, rayCount, seed, gatherOn, texelWaveLimit);
            graph.Dispatch(texelShadePassHandle, Gather(4));
            RecordShadowBatch(texelShadowBatch, texelWaveOrigins, texelWaveOccluded, rayCount, -Vector3.Normalize(sunDirection), seed ^ 0x5bd1e995u, gatherOn, texelWaveLimit);
            graph.Dispatch(texelGatherPassHandle, Gather(5));
        }
        else
        {
            graph.Dispatch(texelGatherPassHandle, Gather(0));
        }
        if (texelFilter) RecordTexelFilter();
        texelFrame++;
    }

    // The filter over this frame's list (the mark's, or the sweep's slice).
    private void RecordTexelFilter()
    {
            graph.Dispatch(texelFilterPassHandle, new DispatchCommand(texelFilterPipeline, TexelFilterCapacity, 1, 1,
                new ShaderUniform[]
                {
                    new("uTexelGrid", new Vector4Uniform(texelGrid)),
                    new("uTexelParams", new Vector4Uniform(new Vector4(texelCapacity, TexelFilterCapacity, texelTrust, texelFrame))),
                    new("uLightmap", new Vector4Uniform(LightmapUniform)),
                    new("uEdge", new Vector4Uniform(new Vector4(texelFilterSymmetric ? 1f : 0f, texelFilterArea ? 1f : 0f, 0f, 0f))),
                },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new[]
                {
                    new ShaderBufferBinding("TexelHash", texelHashBuffer),
                    new ShaderBufferBinding("Texels", texelBuffer),
                    new ShaderBufferBinding("TexelBlend", texelBlendBuffer),
                    new ShaderBufferBinding("TexelFiltered", texelFilteredBuffer),
                    new ShaderBufferBinding("TexelFilterList", texelFilterListBuffer),
                    new ShaderBufferBinding("LightmapIndexMap", LightmapIndexMap()),
                }));
    }

    // The texels' light is settled for this epoch (a settled bake loaded, or one just made): sweep the filter over every
    // live texel once, then rest (RecordTexels).
    private void SettleTexels(string why)
    {
        texelsSettled = true;
        texelSettledEpoch = texelEpoch;
        texelsRestReported = false;
        filterSweepIssued = texelFilter && texelLiveCount > 0 ? 0 : -1;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] texels: settled ({why}); the filter sweeps {texelLiveCount:N0} texels, {TexelFilterCapacity:N0} a frame."));
    }

    // One slice of the filter sweep: the next texels of the live list into the filter's list, and the filter.
    private void RecordFilterSweep()
    {
        var slice = (int)Math.Min(TexelFilterCapacity, texelLiveCount - filterSweepIssued);
        graph.Dispatch(texelFilterFillPassHandle, new DispatchCommand(texelFilterFillPipeline, (slice + 63) / 64, 1, 1,
            new ShaderUniform[] { new("uFill", new Vector4Uniform(new Vector4(filterSweepIssued, slice, texelFrame & 1u, texelLiveCount))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("TexelLive", texelLiveBuffer),
                new ShaderBufferBinding("TexelFilterList", texelFilterListBuffer),
            }));
        RecordTexelFilter();
        filterSweepIssued += slice;
        if (filterSweepIssued >= texelLiveCount) filterSweepIssued = -1;
    }

    // The lookup as the incident pass makes it, at the reference's 24 x 24 pixels (texel_mark.comp's probe mode, run
    // every frame): how many points find a texel, where they do not and with what bins, and each point's irradiance
    // (joined with the path trace by pixel). A CPU replay of the lookup was tried first and misled: its camera ray and
    // the pre-pass's pixel land on different surfaces wherever the geometry is fine.
    private void WriteTexelProbe()
    {
        if (!texelsUploaded) return;
        var gpu = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelProbeBuffer, 0, 24 * 24 * 48).AsSpan()).ToArray();
        int gpuPoints = 0, gpuFound = 0;
        var gpuLost = new List<string>();
        for (var k = 0; k < 24 * 24; k++)
        {
            if (gpu[3 * k + 1].W < 0.5f) continue;
            gpuPoints++;
            if (gpu[3 * k + 1].X > 0f) { gpuFound++; continue; }
            if (gpuLost.Count < 40) gpuLost.Add(string.Create(CultureInfo.InvariantCulture, $"{(k % 24 + 0.5f) / 24:0.0000},{(k / 24 + 0.5f) / 24:0.0000}(w {gpu[3 * k].W:0} bins {((int)gpu[3 * k].W - 1) % 32}/{((int)gpu[3 * k].W - 1) / 32} shading {gpu[3 * k + 1].Z:0} at {gpu[3 * k].X:0.00},{gpu[3 * k].Y:0.00},{gpu[3 * k].Z:0.00})"));
        }
        for (var k = 0; k < 24 * 24; k++)
        {
            if (gpu[3 * k + 1].W < 0.5f) continue;
            var e = gpu[3 * k + 2];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    texel probe at {(k % 24 + 0.5f) / 24:0.0000},{(k / 24 + 0.5f) / 24:0.0000} irradiance {0.2126f * e.X + 0.7152f * e.Y + 0.0722f * e.Z:0.00000} lit {e.W:0.000} found {gpu[3 * k + 1].X:0} weight {gpu[3 * k + 1].Y:0.000}"));
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] texel lookup on the GPU (probe mode): {gpuFound} of {gpuPoints} points find a texel."));
        Console.WriteLine($"    gpu lost: {string.Join(' ', gpuLost)}");
    }

    // At the shot: of the texels this frame's image read, how many have rays and how many reached the target; and how
    // many the last frame queued (by list, before the budget clamps them).
    private void WriteTexelCensus()
    {
        if (!texelsUploaded) return;
        WriteTexelProbe();
        // At rest the gather's buffers are gone (SponzaLoop.TexelRest.cs): every live texel was settled by the bake.
        if (texelGatherFreed)
        {
            Console.WriteLine("[VulkanSponza] texels: at rest at the shot -- every live texel settled; no gather state to count.");
            return;
        }
        var light = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelLightBuffer, 0, texelCount * 16).AsSpan()).ToArray();
        var stamps = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(texelStampBuffer, 0, texelCount * 4).AsSpan()).ToArray();
        var queue = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(texelQueueBuffer, 0, 32).AsSpan()).ToArray();
        var last = texelFrame - 1;
        int seen = 0, none = 0, partial = 0, done = 0, everTraced = 0;
        // --lightmap: the same split by kind (the hashed records, then the atlas's from lightmapFirstRecord).
        int seenAtlas = 0, tracedAtlas = 0;
        var rayBins = new int[6];   // under 512, 512-1023, 1024-2047, 2048-4095, 4096-8191, 8192+
        for (var i = 0; i < texelCount; i++)
        {
            if (light[i].W > 0f) everTraced++;
            if (lightmapTexels && i >= lightmapFirstRecord && light[i].W > 0f) tracedAtlas++;
            // Stamped in the last 16 frames: the mark visits each pixel once in 16, and the readback may land early.
            if (stamps[i] == 0xFFFFFFFFu || last - stamps[i] > 15u) continue;
            seen++;
            if (lightmapTexels && i >= lightmapFirstRecord) seenAtlas++;
            if (light[i].W <= 0f) none++;
            else if (light[i].W < texelTarget) partial++;
            else done++;
            if (light[i].W > 0f) rayBins[Math.Min(5, (int)MathF.Log2(MathF.Max(1f, light[i].W / 512f)) + (light[i].W >= 512f ? 1 : 0))]++;
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] texels after {texelFrame} frames: the image read {seen:N0} (in its last 16 frames: the mark rotates over a 4 x 4 block) -- {done:N0} past the minimum ({texelTarget} rays; rays held: <512 {rayBins[0]:N0}, 512+ {rayBins[1]:N0}, 1k+ {rayBins[2]:N0}, 2k+ {rayBins[3]:N0}, 4k+ {rayBins[4]:N0}, 8k+ {rayBins[5]:N0}), {partial:N0} under it, {none:N0} with none; {everTraced:N0} of {texelCount:N0} ever traced. Queued (both parities) {queue[0]:N0}/{queue[1]:N0}/{queue[2]:N0}/{queue[3]:N0} and {queue[4]:N0}/{queue[5]:N0}/{queue[6]:N0}/{queue[7]:N0} (none / few / under the minimum / settling; budget {texelBudget:N0})."));
        if (lightmapTexels)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] texels by kind: hashed read {seen - seenAtlas:N0}, traced {everTraced - tracedAtlas:N0} of {lightmapFirstRecord:N0}; lightmap read {seenAtlas:N0}, traced {tracedAtlas:N0} of {texelCount - lightmapFirstRecord:N0}."));
    }
}
