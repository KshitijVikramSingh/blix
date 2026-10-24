using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;

namespace Blix.Demos.VulkanSponza;

// --gpu-reference (with --shot): the gate's surface reference path-traced on the GPU as a WAVEFRONT (reference_wave.comp,
// one bounce a frame, every ray through the frame's batches -- SponzaLoop.RayBatch.cs: the software traversal, or Metal's
// hardware under --ray-backend hw|both). --reference-monolithic keeps the first form (reference_points.comp: whole paths
// in one kernel over ray_query.glsl), the wavefront's cross-check. Both take reference_path.glsl's sampling and bounce
// count, as the reference view does. The points are the CPU reference's (WriteSurfaceReference): a grid of camera pixels off
// depth edges, at the surface lifted 1 cm along its face normal. Paths accumulate over the frames before the shot,
// --ref-paths in all, --ref-bounces deep; at the shot the lines are the CPU reference's format (tools/testbeds/gate.py
// reads them), the CPU reference's own -- if --probe-reference also ran -- relabelled, and the two held against each
// other at the same points.
internal sealed partial class SponzaLoop
{
    private bool gpuReference;
    // --gpu-ref-paths N: the GPU reference's own path count (--ref-paths when absent), so it can run far past the CPU's.
    private int? gpuReferencePathsWanted;
    private int GpuReferenceTarget => gpuReferencePathsWanted ?? refPaths;
    private ShaderInterface gpuReferenceInterface = null!;
    private PassHandle gpuReferencePassHandle;
    private PipelineHandle gpuReferencePipeline;
    private List<(int K, float Px, float Py)>? gpuReferencePoints;
    private GpuBufferHandle gpuReferencePointBuffer, gpuReferenceAccum;
    private int gpuReferencePaths;
    private uint gpuReferenceDispatch;
    // The CPU reference's totals by point (WriteSurfaceReference), when it ran too: the cross-check.
    private readonly Dictionary<int, double> cpuSurfaceReference = new();
    private const int GpuReferenceGrid = 24;
    // --gpu-ref-seams N: N more points at concave creases (where two faces meet), printed as "seam" lines. The grid's
    // points almost never land within a few pixels of one, and a leak through a seam lights exactly there: the
    // crevices' box joints glowed with sky the gate never saw (2026-10-15). Their K: SeamKeyBase and up.
    private int gpuReferenceSeams;
    private const int SeamKeyBase = 1 << 20;
    private const int GpuReferencePathsPerThread = 4;
    private const int GpuReferencePathsPerDispatch = 64 * GpuReferencePathsPerThread;

    // The wavefront: lanes per point, each running whole paths one bounce a frame, up to its share of the paths.
    private bool referenceMonolithic;
    // --gpu-ref-lanes N: lanes a point. The wave runs a bounce a frame, so its frames go as paths over lanes: more lanes,
    // fewer frames for the same paths. By default enough that no lane runs more than ReferenceLaneMostPaths paths
    // (a power of two, 256 to ReferenceLanesAuto): 16,384 paths on 1,024 lanes are ~550 frames, where 256 lanes took
    // 2,180 and set the gate's pace (--shot-when-ready, 2026-10-13). The cap keeps the wave's buffers (~200 B a lane)
    // near 230 MB at the gate's 576 points; past it, ask. The dark corridor's 262,144-path reference (two 16,384-path
    // ones with different seeds sat 0.037 apart there, as far as the field from either) asks for 4,096.
    private int? referenceLanesAsked;
    private const int ReferenceLaneMostPaths = 16, ReferenceLanesAuto = 2048;
    private int ReferenceLanesPerPoint => referenceLanesAsked
        ?? Math.Clamp((int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, GpuReferenceTarget / ReferenceLaneMostPaths)), 256, ReferenceLanesAuto);
    private ShaderInterface referenceWaveInterface = null!;
    private PipelineHandle referenceWavePipeline;
    private PassHandle referenceWaveStepPass, referenceWaveShadePass, referenceWaveResolvePass;
    private RayBatchPasses referenceClosestBatch, referenceShadowBatch, referenceSunBatch;
    // The reference's sun rays traced as closest rays, anything met shade (reference_wave.comp, BatchSunRays): the paths'
    // own trace, independent of the frame's shadow kernel and its sheets. --ref-shadow-batch: the shadow batch, as before.
    private bool refSunClosest = true;
    // --ref-lift M: how far off a hit a reference path, and its sun ray, go on (reference_wave.comp; 1 mm). It was 1 cm:
    // the teacup's saucer stands 1.9 cm over the floor, and the floor under it read 0.33 there against 0.18 at 1 mm and
    // 0.16 at 0.2 mm -- converged by 1 mm, every other face of the saucer unchanged; the sheet slab's floor within 0.3%
    // of its closed form (2026-10-23).
    private float refLift = 0.001f;
    private int ReferenceLaneQuota => (GpuReferenceTarget + ReferenceLanesPerPoint - 1) / ReferenceLanesPerPoint;
    // The gate's points as PickGpuReferencePoints packed them (position, normal), and their wave.
    private List<Vector4>? gpuReferencePackedPoints;
    private ReferenceWave? gateWave;

    /// <summary>One reference wavefront: its points (two vec4s each: position, normal), lanesPerPoint lanes a point each
    /// running Quota whole paths one bounce a frame, and the buffers its steps and batches pass between them. The gate's
    /// points have one (gateWave).</summary>
    private sealed class ReferenceWave
    {
        public GpuBufferHandle Points, Lanes, Sums, Rays, Hits, Origins, Occluded, Directions, SunRays, SunHits;
        public int PointCount, LanesPerPoint, Quota;
        public uint Dispatch;
        public int Count => PointCount * LanesPerPoint;
    }

    // Per triangle row, its sheet for the reference (reference_wave.comp, ReferenceSheets): rgb8 T c, the top byte T; null
    // with no sheet in the scene (or --ref-opaque-sheets). The spec's terms, not RtRowTransmit's frame-side cap and hue.
    private GpuBufferHandle? referenceSheets;
    private bool referenceSheetsMade;
    private GpuBufferHandle? SheetRowsBuffer
    {
        get
        {
            if (referenceSheetsMade) return referenceSheets;
            referenceSheetsMade = true;
            if (!(refSheets || sheetLight) || rayGpuData is not { } data) return null;
            var rows = new uint[Math.Max(1, data.RowPlacements.Length)];
            var any = 0;
            double shareSum = 0;
            uint Byte(float x) => (uint)Math.Clamp(MathF.Round(x * 255f), 0f, 255f);
            for (var row = 0; row < data.RowPlacements.Length; row++)
            {
                var p = (int)data.RowPlacements[row];
                if (p >= rayPlacementTransmissionFactor.Count) continue;
                // T: the factor times the row's share of its factor texture (RowTransmissionShare: its alpha over the triangle);
                // c the colour factor times its colour texture's mean there (RowTransmissionColour).
                var share = RowTransmissionShare(row);
                var factor = rayPlacementTransmissionFactor[p] * share;
                var tc = rayPlacementTransmission[p] * share * RowTransmissionColour(row);
                if (rayPlacementTransmissionFactor[p] <= 0f) continue;
                rows[row] = Byte(tc.X) | Byte(tc.Y) << 8 | Byte(tc.Z) << 16 | Math.Max(1u, Byte(factor)) << 24;
                shareSum += share;
                any++;
            }
            if (any == 0) return null;
            Console.WriteLine(string.Create(Inv, $"[VulkanSponza] sheets: {any:N0} triangle rows are thin sheets (diffuse transmission), scattered as the spec has them{(refSheets ? " in the reference" : "")}{(sheetLight ? " and the GI" : "")}; their factor textures keep {shareSum / Math.Max(1, any):P0} of the factor on average{(raySurfacesChecked ? "" : " (the surface bake not read yet: whole)")}."));
            referenceSheets = Own(device.CreateGpuBuffer(rows.Length * 4, MemoryMarshal.AsBytes(rows.AsSpan()), "sponza.reference.sheets"));
            return referenceSheets;
        }
    }

    // Per triangle row, one bit: KHR_materials_unlit (reference_wave.comp, ReferenceUnlit) -- a camera path's first hit
    // there sees the base colour and ends. All zeros with none.
    private GpuBufferHandle? referenceUnlit;
    private GpuBufferHandle ReferenceUnlitBuffer
    {
        get
        {
            if (referenceUnlit is { } made) return made;
            var rows = rayGpuData?.RowPlacements ?? Array.Empty<uint>();
            var bits = new uint[rows.Length / 32 + 1];
            var any = 0;
            for (var row = 0; row < rows.Length; row++)
                if ((int)rows[row] < rayPlacementUnlit.Count && rayPlacementUnlit[(int)rows[row]]) { bits[row >> 5] |= 1u << (row & 31); any++; }
            if (any > 0) Console.WriteLine(string.Create(Inv, $"[VulkanSponza] view reference: {any:N0} triangle rows unlit (KHR_materials_unlit), their base colour as the camera sees it."));
            referenceUnlit = Own(device.CreateGpuBuffer(bits.Length * 4, MemoryMarshal.AsBytes(bits.AsSpan()), "sponza.reference.unlit"));
            return referenceUnlit.Value;
        }
    }

    private ReferenceWave CreateReferenceWave(List<Vector4> packed, int lanesPerPoint, int quota, string name)
    {
        var w = new ReferenceWave { PointCount = packed.Count / 2, LanesPerPoint = lanesPerPoint, Quota = quota };
        _ = SheetRowsBuffer;   // made with the wave, not at its first dispatch
        var lanes = w.Count;
        // Every lane starts with no path: its hit count -1.
        var start = new float[lanes * 16];
        for (var l = 0; l < lanes; l++) start[l * 16 + 3] = -1f;
        w.Points = Own(device.CreateGpuBuffer(packed.Count * 16, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(packed)), $"sponza.{name}.points"));
        w.Lanes = Own(device.CreateGpuBuffer(lanes * 64, MemoryMarshal.AsBytes(start.AsSpan()), $"sponza.{name}.lanes"));
        w.Sums = Own(device.CreateGpuBuffer(lanes * 32, new byte[lanes * 32], $"sponza.{name}.lane-sums"));
        w.Rays = Own(device.CreateGpuBuffer(lanes * 32, new byte[lanes * 32], $"sponza.{name}.rays"));
        w.Hits = Own(device.CreateGpuBuffer(lanes * 48, new byte[lanes * 48], $"sponza.{name}.hits"));
        w.Origins = Own(device.CreateGpuBuffer(lanes * 16, new byte[lanes * 16], $"sponza.{name}.shadow-origins"));
        w.Occluded = Own(device.CreateGpuBuffer(lanes * 4, new byte[lanes * 4], $"sponza.{name}.occluded"));
        w.Directions = Own(device.CreateGpuBuffer(lanes * 16, new byte[lanes * 16], $"sponza.{name}.shadow-directions"));
        w.SunRays = Own(device.CreateGpuBuffer(lanes * 32, new byte[lanes * 32], $"sponza.{name}.sun-rays"));
        w.SunHits = Own(device.CreateGpuBuffer(lanes * 48, new byte[lanes * 48], $"sponza.{name}.sun-hits"));
        return w;
    }

    // --shot-when-ready: the shot as soon as what it compares is there -- the texels at rest (settled and filtered; when
    // they are in use) and the gate's reference with all its paths (when asked for; its wave starts at load instead of
    // counting back from the shot frame) -- and ShotReadyMargin frames more for the image to catch up; --shot-frames
    // stays the latest it may be. The gate ran each scene a fixed 3,000-4,500 frames, long after both.
    private bool shotWhenReady;
    // --shot-hold N: frames at rest before the shot (default 120) -- a timing run's still/moving windows want seconds of it.
    private int ShotReadyMargin = 120;
    private int shotReadyFrame = -1;

    private bool ShotReady()
    {
        if (!shotWhenReady) return false;
        if (shotReadyFrame < 0)
        {
            // And no light on its way: a --sun-change still to come or relighting, the split light still cooking (a shot
            // of a sun change taken before its relight measured the old sun, 2026-10-14).
            var lightReady = (!transportTexels || texelsRestReported) && relightTask is null && !splitCooking
                && (sunChange is null || sunChangeApplied && postLoadFrames > sunChangeAppliedAt + 1);
            var referenceReady = !gpuReference || referenceMonolithic
                ? !gpuReference || gpuReferencePaths >= GpuReferenceTarget
                : cachedReferenceSums is not null || gateWave is { } w && w.Dispatch >= ReferenceWaveFrames(w);
            // The sun records when asked (--lightlets, --lightlet-sun): a shot of them before they land measured the
            // traced shadows instead (2026-10-14: a bake closed at 0%).
            var recordsReady = !lightletsAsked || lightlets is not null;
            // And --ref-view's camera paths, all of them (a shot before they finished compared a part of them, 2026-10-22).
            var viewReady = refViewStride <= 0 || viewWave is { } v && v.Dispatch >= ReferenceWaveFrames(v);
            if (!lightReady || !referenceReady || !recordsReady || !viewReady) return false;
            shotReadyFrame = postLoadFrames;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] shot: ready at post-load frame {postLoadFrames} (light {(transportTexels ? "at rest" : "not cooked")}, reference {(gpuReference ? "complete" : "none")}{(lightletsAsked ? ", sun records up" : "")}); writing in {ShotReadyMargin} frames."));
            DumpShotState();
        }
        // On a frame fixed in advance, not a fixed count after ready: TAA's jitter is a Halton sequence over the post-load
        // frame, and at a crease the shot's value hangs on which jitter it lands on -- the gate's crevices seams read 0.872
        // at frame 2263 and 0.783 at 2264 for identical light, as the cook landed a frame earlier or later (2026-10-19).
        // Rounded up to 256 frames, runs a few frames apart shoot the same one.
        var target = (shotReadyFrame + ShotReadyMargin + 255) / 256 * 256;
        // The frame the shot is taken on, for what counts back from it (--stability's K frames, SampleStability).
        shotFrame = target;
        // The 64 frames before it: each sample point's incident light summed over their jitter (WriteGpuSurfaceReference).
        if (postLoadFrames >= target - IncidentAverageFrames && postLoadFrames < target) AccumulateIncidentPoints();
        return postLoadFrames >= target;
    }

    // The frames a wave needs at most: every lane's quota of paths at their longest (bounces + 2 rays each).
    private int ReferenceWaveFrames(ReferenceWave w) => w.Quota * (refBounces + 2) + 4;

    // Each frame: from as many frames before the shot as the paths need, the points picked and their paths dispatched.
    private void RecordGpuReference()
    {
        if (!gpuReference || shotPath is null || shotWritten || rayQueries is not { } scene || rayBlockBuffers.Length == 0 || !RaySurfacesReady) return;
        if (!referenceMonolithic)
        {
            RecordGateReferenceWave(scene);
            return;
        }
        var dispatches = (GpuReferenceTarget + GpuReferencePathsPerDispatch - 1) / GpuReferencePathsPerDispatch;
        if (gpuReferencePoints is null)
        {
            if (postLoadFrames < shotFrame - dispatches - 2) return;
            PickGpuReferencePoints(scene);
        }
        if (gpuReferencePoints!.Count == 0 || gpuReferencePaths >= GpuReferenceTarget) return;
        var declared = gpuReferenceInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        graph.Dispatch(gpuReferencePassHandle, new DispatchCommand(gpuReferencePipeline, gpuReferencePoints.Count, 1, 1,
            new ShaderUniform[]
            {
                new("uSunDirection", new Vector4Uniform(new Vector4(sunDirection, 0f))),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 1f))),
                new("uParams", new Vector4Uniform(new Vector4(gpuReferenceDispatch + (uint)refSeed * 7919u, refBounces, GpuReferencePathsPerThread, gpuReferencePoints.Count))),
            },
            new[] { new ShaderTextureBinding("uSkyRadiance", envCubeTexture) },
            Buffers: rayBlockBuffers
                .Append(new ShaderBufferBinding("ReferencePointList", gpuReferencePointBuffer))
                .Append(new ShaderBufferBinding("ReferencePointAccum", gpuReferenceAccum))
                .Where(b => declared.Contains(b.Name)).ToArray()));
        gpuReferenceDispatch++;
        gpuReferencePaths += GpuReferencePathsPerDispatch;
    }

    // The gate's wave: started as many frames before the shot as its lanes' quota can take.
    private void RecordGateReferenceWave(RayQueryScene scene)
    {
        if (cachedReferenceSums is not null) return;
        if (gateWave is null)
        {
            if (!shotWhenReady && postLoadFrames < shotFrame - ReferenceLaneQuota * (refBounces + 2) - 4) return;
            if (shotWhenReady && postLoadFrames < 4) return;
            // Not before the Metal tracer is up (on hardware rays): the first frames' batches ran on the software traversal,
            // whose hits name no triangle row, so every lane's first path met the sheets as opaque -- 1 / quota of the light
            // through them lost (the slab's floor 15/16 of its closed form, 2026-10-21).
            if (FrameRaysOnHardware && frameTracer is null) return;
            PickGpuReferencePoints(scene);
            if (gpuReferencePackedPoints is not { Count: > 0 } packed) return;
            if (LoadCachedReference(packed)) return;
            gateWave = CreateReferenceWave(packed, ReferenceLanesPerPoint, ReferenceLaneQuota, "reference");
        }
        RecordReferenceWave(gateWave);
    }

    // One bounce of every lane's path: step (the next rays) -> CLOSEST -> shade (shadow rays, the next start) -> SHADOW ->
    // resolve. The batches are the frame's (SponzaLoop.RayBatch.cs): software, or Metal under --ray-backend hw|both.
    private void RecordReferenceWave(ReferenceWave w, MetalRayTracer? on = null)
    {
        var count = w.Count;
        if (count == 0) return;
        var frameSeed = w.Dispatch * 0x9E3779B9u + (uint)refSeed * 7919u;
        var declared = referenceWaveInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        DispatchCommand Wave(int mode) => new(referenceWavePipeline, (count + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uSunDirection", new Vector4Uniform(new Vector4(sunDirection, 0f))),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, refSky))),
                new("uParams", new Vector4Uniform(new Vector4(mode, refBounces, w.Quota, w.LanesPerPoint))),
                new("uCount", new Vector4Uniform(new Vector4(count, w.Dispatch + (uint)refSeed * 7919u, refSunDisc ? SunDiscTangent : 0f,
                    (refSheets && SheetRowsBuffer is not null ? 1f : 0f) + (refSunClosest ? 2f : 0f)))),
                new("uLift", new Vector4Uniform(new Vector4(refLift, 0f, 0f, 0f))),
            },
            new[] { new ShaderTextureBinding("uSkyRadiance", envCubeTexture) },
            Buffers: rayBlockBuffers
                .Append(new ShaderBufferBinding("ReferencePointList", w.Points))
                .Append(new ShaderBufferBinding("ReferenceLanes", w.Lanes))
                .Append(new ShaderBufferBinding("ReferenceLaneSums", w.Sums))
                .Append(new ShaderBufferBinding("BatchRays", w.Rays))
                .Append(new ShaderBufferBinding("BatchHits", w.Hits))
                .Append(new ShaderBufferBinding("BatchOrigins", w.Origins))
                .Append(new ShaderBufferBinding("BatchOccluded", w.Occluded))
                .Append(new ShaderBufferBinding("BatchDirections", w.Directions))
                .Append(new ShaderBufferBinding("ReferenceSheets", SheetRowsBuffer ?? w.Occluded))
                .Append(new ShaderBufferBinding("BatchSunRays", w.SunRays))
                .Append(new ShaderBufferBinding("BatchSunHits", w.SunHits))
                .Append(new ShaderBufferBinding("ReferenceUnlit", ReferenceUnlitBuffer))
                .Where(b => declared.Contains(b.Name)).ToArray());
        graph.Dispatch(referenceWaveStepPass, Wave(0));
        RecordClosestBatch(referenceClosestBatch, w.Rays, w.Hits, count, frameSeed, on);
        graph.Dispatch(referenceWaveShadePass, Wave(1));
        if (refSunClosest) RecordClosestBatch(referenceSunBatch, w.SunRays, w.SunHits, count, frameSeed ^ 0x5bd1e995u, on);
        else RecordShadowBatch(referenceShadowBatch, w.Origins, w.Occluded, count, -Vector3.Normalize(sunDirection), frameSeed ^ 0x5bd1e995u, on,
            directions: refSunDisc ? w.Directions : null);
        graph.Dispatch(referenceWaveResolvePass, Wave(2));
        w.Dispatch++;
    }

    // WriteSurfaceReference's points: the grid's camera pixels whose four neighbours, 2 px away, meet the same depth
    // (within 2%), at the hit lifted 1 cm along the face normal turned toward the camera.
    private void PickGpuReferencePoints(RayQueryScene scene)
    {
        device.TryGetTextureSize(graph.GetColorTexture(incidentHandle), out var iw, out var ih);
        Matrix4x4.Invert(viewProj, out var invViewProj);
        Ray CameraRay(float px, float py)
        {
            var ndc = new Vector2(px / iw * 2f - 1f, py / ih * 2f - 1f);
            var far = Vector4.Transform(new Vector4(ndc, 1f, 1f), invViewProj);
            return new Ray(cameraPosition, Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - cameraPosition));
        }
        Vector3 FaceNormal(RayHit hit)
        {
            var inst = scene.Instances[hit.Instance];
            var m = inst.Mesh;
            var a = Vector3.Transform(m.Positions[m.Indices[hit.Triangle * 3]], inst.World);
            var b = Vector3.Transform(m.Positions[m.Indices[hit.Triangle * 3 + 1]], inst.World);
            var c = Vector3.Transform(m.Positions[m.Indices[hit.Triangle * 3 + 2]], inst.World);
            var n = Vector3.Cross(b - a, c - a);
            return n.LengthSquared() > 0f ? Vector3.Normalize(n) : Vector3.UnitY;
        }
        var points = new List<(int K, float Px, float Py)>();
        var packed = new List<Vector4>();
        // The shading normal at each point (the pre-pass's: the one the incident pass reads the light at and the frame
        // shades with), not its triangle's face: on a faceted fold -- drapes' 2 cm strips with smooth normals -- they part by
        // up to half a facet's turn, and under light from one side the face's irradiance was up to 20% from the smooth one's
        // (tools/gi-model/facet_normal.py; the curtain's +7-10% against the path trace, 2026-10-21). --ref-face-normal: the
        // face's, as before. The point still lifted along its face.
        byte[]? shading = null;
        int sw = 0, sh = 0;
        if (!refFaceNormal) shading = device.ReadTexture(graph.GetColorTexture(SampleablePrepassNormal), out sw, out sh, out _);
        for (var k = 0; k < GpuReferenceGrid * GpuReferenceGrid; k++)
        {
            // At the centre of the pixel the field is read from (WriteGpuSurfaceReference: (int)px, (int)py), so the reference's
            // ray and the pixel see the same face.
            var px = MathF.Floor((k % GpuReferenceGrid + 0.5f) / GpuReferenceGrid * iw) + 0.5f;
            var py = MathF.Floor((k / GpuReferenceGrid + 0.5f) / GpuReferenceGrid * ih) + 0.5f;
            var ray = CameraRay(px, py);
            if (scene.Closest(ray) is not { } hit) continue;
            var edge = false;
            foreach (var (dx, dy) in new[] { (2f, 0f), (-2f, 0f), (0f, 2f), (0f, -2f) })
                if (scene.Closest(CameraRay(px + dx, py + dy)) is not { } other || MathF.Abs(other.T - hit.T) > 0.02f * hit.T) { edge = true; break; }
            if (edge) continue;
            var n = FaceNormal(hit);
            if (Vector3.Dot(n, ray.Direction) > 0f) n = -n;
            var at = n;
            if (shading is not null && sw > 0 && sh > 0)
            {
                var o = (Math.Clamp((int)(py * sh / ih), 0, sh - 1) * sw + Math.Clamp((int)(px * sw / iw), 0, sw - 1)) * 8;
                var s = new Vector3((float)BitConverter.ToHalf(shading, o), (float)BitConverter.ToHalf(shading, o + 2), (float)BitConverter.ToHalf(shading, o + 4));
                if (s.LengthSquared() > 1e-6f && Vector3.Dot(Vector3.Normalize(s), n) > 0.8f) at = Vector3.Normalize(s);
            }
            points.Add((k, px, py));
            packed.Add(new Vector4(ray.PointAt(hit.T) + n * 0.01f, 0f));
            packed.Add(new Vector4(at, 0f));
        }
        // The seams: a finer grid's pixels whose neighbours 3 px away all meet the same surface depth (no silhouette)
        // but at least one meets a face turned 45 degrees or more from this one -- a concave crease. Every candidate's
        // chance alike: a stride over them all.
        if (gpuReferenceSeams > 0)
        {
            const int Fine = 8 * GpuReferenceGrid;
            var seams = new List<(float Px, float Py, Vector3 P, Vector3 N)>();
            for (var k = 0; k < Fine * Fine; k++)
            {
                // A pixel's centre, as the grid's: at a crease, a point a fraction of a pixel off its pixel's centre had its
                // pixel on the other face -- the crevices' seams read 0.013 against 0.063 (2026-10-21).
                var px = MathF.Floor((k % Fine + 0.5f) / Fine * iw) + 0.5f;
                var py = MathF.Floor((k / Fine + 0.5f) / Fine * ih) + 0.5f;
                var ray = CameraRay(px, py);
                if (scene.Closest(ray) is not { } hit) continue;
                var n = FaceNormal(hit);
                if (Vector3.Dot(n, ray.Direction) > 0f) n = -n;
                bool edge = false, crease = false;
                foreach (var (dx, dy) in new[] { (3f, 0f), (-3f, 0f), (0f, 3f), (0f, -3f) })
                {
                    if (scene.Closest(CameraRay(px + dx, py + dy)) is not { } other || MathF.Abs(other.T - hit.T) > 0.05f * hit.T) { edge = true; break; }
                    var on = FaceNormal(other);
                    if (MathF.Abs(Vector3.Dot(on, n)) < 0.7f) crease = true;
                }
                // Its own pixel whole: the same face and depth a pixel each way, so the reference's ray and the raster (and its
                // jitter) meet the same surface. A point a pixel from a step between two parallel faces passed the normal
                // test frame by frame and read the nearer face's light against the farther's reference, 4x over (crevices,
                // 2026-10-21).
                if (!edge && crease)
                    foreach (var (dx, dy) in new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) })
                        if (scene.Closest(CameraRay(px + dx, py + dy)) is not { } near || MathF.Abs(near.T - hit.T) > 0.01f * hit.T
                            || Vector3.Dot(FaceNormal(near) * (Vector3.Dot(FaceNormal(near), ray.Direction) > 0f ? -1f : 1f), n) < 0.95f) { edge = true; break; }
                if (!edge && crease) seams.Add((px, py, ray.PointAt(hit.T) + n * 0.01f, n));
            }
            var take = Math.Min(gpuReferenceSeams, seams.Count);
            for (var j = 0; j < take; j++)
            {
                var s = seams[(int)((long)j * seams.Count / take)];
                points.Add((SeamKeyBase + j, s.Px, s.Py));
                packed.Add(new Vector4(s.P, 0f));
                packed.Add(new Vector4(s.N, 0f));
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] GPU surface reference: {take} seam points of {seams.Count} crease pixels on a {Fine}x{Fine} grid."));
        }
        gpuReferencePoints = points;
        gpuReferencePackedPoints = packed;
        if (points.Count == 0) return;
        gpuReferencePointBuffer = Own(device.CreateGpuBuffer(packed.Count * 16, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(packed)), "sponza.reference.points"));
        gpuReferenceAccum = Own(device.CreateGpuBuffer(points.Count * 16, new byte[points.Count * 16], "sponza.reference.points-accum"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] GPU surface reference: {points.Count} of {GpuReferenceGrid * GpuReferenceGrid} pixels off depth edges, {GpuReferenceTarget} paths x {refBounces} bounces, from post-load frame {postLoadFrames}."));
    }

    // BLIX_REF_CACHE=dir: the gate's reference kept on disk by what decides it -- its points (the scene and the camera
    // through them), the sun, the sky, the seed and the paths' rules -- and read back instead of traced again: a variant of
    // the GI against the same reference waited ~1 min of the reference's frames each run (2026-10-23).
    private static readonly string? referenceCacheDir = Environment.GetEnvironmentVariable("BLIX_REF_CACHE") is { Length: > 0 } rc ? rc : null;
    private Vector4[]? cachedReferenceSums;
    private string? referenceCacheFile;

    // The geometry by the transport's patches key (the scene's triangles): a variant of a scene under its name, its points
    // where the last one's were (a thicker roof over the same corridor), read the other's reference (2026-10-23).
    private string ReferenceCacheKey(List<Vector4> packed)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var head = string.Create(CultureInfo.InvariantCulture,
            $"ref v1;{sunDirection.X:G9},{sunDirection.Y:G9},{sunDirection.Z:G9};{EffectiveSunIrradiance.X:G9},{EffectiveSunIrradiance.Y:G9},{EffectiveSunIrradiance.Z:G9};sky {refSky:G9};seed {refSeed};bounces {refBounces};lanes {ReferenceLanesPerPoint};quota {ReferenceLaneQuota};disc {refSunDisc};sheets {refSheets};closest {refSunClosest};lift {refLift:G6};scene {scene.Name};geometry {transportPatchesKey ?? "-"};probe {probeName}");
        var bytes = System.Text.Encoding.UTF8.GetBytes(head).Concat(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(packed)).ToArray()).ToArray();
        return Convert.ToHexString(sha.ComputeHash(bytes))[..24].ToLowerInvariant();
    }

    private bool LoadCachedReference(List<Vector4> packed)
    {
        if (referenceCacheDir is null) return false;
        referenceCacheFile = Path.Combine(referenceCacheDir, ReferenceCacheKey(packed) + ".ref");
        if (!File.Exists(referenceCacheFile)) return false;
        var sums = MemoryMarshal.Cast<byte, Vector4>(File.ReadAllBytes(referenceCacheFile)).ToArray();
        if (sums.Length != gpuReferencePoints!.Count) return false;
        cachedReferenceSums = sums;
        Console.WriteLine($"[VulkanSponza] GPU surface reference: read from the cache ({referenceCacheFile}), not traced.");
        return true;
    }

    private void SaveCachedReference(Vector4[] sums)
    {
        if (referenceCacheFile is null) return;
        Directory.CreateDirectory(referenceCacheDir!);
        File.WriteAllBytes(referenceCacheFile, MemoryMarshal.AsBytes(sums.AsSpan()).ToArray());
        Console.WriteLine($"[VulkanSponza] GPU surface reference: kept in the cache ({referenceCacheFile}).");
    }

    // The wavefront's lanes summed per point: (sum of pi x radiance, paths).
    private Vector4[] ReferenceWaveSums(ReferenceWave w)
    {
        var sums = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(w.Sums, 0, w.Count * 32).AsSpan()).ToArray();
        var perPoint = new Vector4[w.PointCount];
        for (var p = 0; p < w.PointCount; p++)
            for (var k = 0; k < w.LanesPerPoint; k++) perPoint[p] += sums[(p * w.LanesPerPoint + k) * 2 + 1];
        return perPoint;
    }

    // The sample points' incident light over the frames before the shot: one jittered frame's value at a crease hangs on
    // its jitter (crevices' seams 0.78-0.87 by the frame, 2026-10-19); the mean over many does not.
    private const int IncidentAverageFrames = 64;
    private double[]? incidentPointSums;
    private int incidentPointFrames;
    // Each point's frames that counted: a frame whose pixel showed another face than the reference's point is left out.
    // A crease's pixel sits on the crease line, and under the jitter half its frames saw the other face -- the crevices'
    // seams read 0.013 against the reference's 0.063 where the pixel's own face read 0.065 (2026-10-21).
    private int[]? incidentPointCounts;
    private int incidentPointsOffFace;

    private void AccumulateIncidentPoints()
    {
        if (!gpuReference || gpuReferencePoints is not { Count: > 0 } points) return;
        var incident = device.ReadTexture(graph.GetColorTexture(incidentHandle), out var iw, out var ih, out _);
        var normals = device.ReadTexture(graph.GetColorTexture(SampleablePrepassNormal), out var nw, out var nh, out _);
        incidentPointSums ??= new double[points.Count];
        incidentPointCounts ??= new int[points.Count];
        var packed = gpuReferencePackedPoints;
        for (var i = 0; i < points.Count; i++)
        {
            var (_, px, py) = points[i];
            // This frame's face at the pixel against the point's (the reference's): another face, not counted.
            if (packed is not null && nw > 0 && nh > 0)
            {
                var no = (Math.Clamp((int)(py * nh / ih), 0, nh - 1) * nw + Math.Clamp((int)(px * nw / iw), 0, nw - 1)) * 8;
                var seen = new Vector3((float)BitConverter.ToHalf(normals, no), (float)BitConverter.ToHalf(normals, no + 2), (float)BitConverter.ToHalf(normals, no + 4));
                var want = new Vector3(packed[2 * i + 1].X, packed[2 * i + 1].Y, packed[2 * i + 1].Z);
                if (seen.LengthSquared() > 1e-6f && Vector3.Dot(Vector3.Normalize(seen), want) < 0.9f) { incidentPointsOffFace++; continue; }
            }
            var o = (Math.Clamp((int)py, 0, ih - 1) * iw + Math.Clamp((int)px, 0, iw - 1)) * 8;
            incidentPointSums[i] += 0.2126 * (float)BitConverter.ToHalf(incident, o) + 0.7152 * (float)BitConverter.ToHalf(incident, o + 2) + 0.0722 * (float)BitConverter.ToHalf(incident, o + 4);
            incidentPointCounts[i]++;
        }
        incidentPointFrames++;
    }

    // At the shot: the gate's lines from the GPU reference, and the CPU's held against it where both ran.
    private void WriteGpuSurfaceReference()
    {
        if (!gpuReference || gpuReferencePoints is not { Count: > 0 } points) return;
        var accum = cachedReferenceSums ?? (referenceMonolithic
            ? MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(gpuReferenceAccum, 0, points.Count * 16).AsSpan()).ToArray()
            : ReferenceWaveSums(gateWave!));
        if (cachedReferenceSums is null && !referenceMonolithic) SaveCachedReference(accum);
        var incident = device.ReadTexture(graph.GetColorTexture(incidentHandle), out var iw, out var ih, out _);
        static double Lum(Vector3 v) => 0.2126 * v.X + 0.7152 * v.Y + 0.0722 * v.Z;
        if (incidentPointFrames > 0)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] surface reference: each point's light the mean of {incidentPointFrames} jittered frames ({incidentPointsOffFace:N0} point-frames left out: their pixel showed another face)."));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] surface reference on the GPU ({(referenceMonolithic ? "monolithic" : FrameRaysOnHardware && frameTracer is not null ? "wavefront, Metal" : "wavefront, software")}; {points.Count} pixels off depth edges, {(referenceMonolithic ? gpuReferencePaths : (int)Math.Round(accum.Average(a => a.W)))} paths x {refBounces} bounces):"));
        if (!referenceMonolithic && FrameRaysOnHardware && frameTracer is not null)
        {
            var trace = frameTracer.TraceStats;
            var shadow = frameTracer.ShadowStatsOf(0);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] reference wavefront on Metal: closest {trace.Dispatches:N0} batches, {trace.MeanGpuMs:0.000} ms mean, {trace.Failures} failed; shadow {shadow.Dispatches:N0} batches, {shadow.MeanGpuMs:0.000} ms mean, {shadow.Failures} failed ({gateWave?.Count ?? 0:N0} rays a batch)."));
        }
        var pairs = new List<(double Cpu, double Gpu)>();
        for (var i = 0; i < points.Count; i++)
        {
            var (k, px, py) = points[i];
            var a = accum[i];
            var reference = a.W > 0f ? Lum(new Vector3(a.X, a.Y, a.Z) / a.W) : double.NaN;
            var o = (Math.Clamp((int)py, 0, ih - 1) * iw + Math.Clamp((int)px, 0, iw - 1)) * 8;
            // No frame showed the reference's face at the pixel (the reference's ray and the raster split a crease there): no
            // sample (gate.py skips a negative one) -- not the other face's light against this one's reference.
            var field = incidentPointFrames > 0 && incidentPointCounts![i] == 0 ? -1.0
                : incidentPointFrames > 0 ? incidentPointSums![i] / incidentPointCounts[i]
                : Lum(new Vector3((float)BitConverter.ToHalf(incident, o), (float)BitConverter.ToHalf(incident, o + 2), (float)BitConverter.ToHalf(incident, o + 4)));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    {(k >= SeamKeyBase ? "seam    " : "surface ")} at {px / iw:0.0000},{py / ih:0.0000}  gpu  total {field:0.00000} ref {reference:0.00000}"));
            if (cpuSurfaceReference.TryGetValue(k, out var cpu)) pairs.Add((cpu, reference));
        }
        if (pairs.Count == 0) return;
        var mean = pairs.Average(p => p.Cpu);
        var diffs = pairs.Select(p => Math.Abs(p.Gpu - p.Cpu) / Math.Max(mean, 1e-9)).OrderBy(v => v).ToArray();
        var ratios = pairs.Where(p => p.Cpu > 0.02 * mean).Select(p => p.Gpu / p.Cpu).OrderBy(v => v).ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] GPU reference against the CPU's at {pairs.Count} points: energy {pairs.Sum(p => p.Gpu) / pairs.Sum(p => p.Cpu):0.000}, median ratio {(ratios.Length > 0 ? ratios[ratios.Length / 2] : double.NaN):0.000}, |diff|/mean median {diffs[diffs.Length / 2]:0.000} p90 {diffs[(int)(diffs.Length * 0.9)]:0.000}."));
    }
}
