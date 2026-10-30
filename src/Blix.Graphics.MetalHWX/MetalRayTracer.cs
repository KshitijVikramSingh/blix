using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;

namespace Blix.Graphics.MetalHWX;

/// <summary>What <see cref="MetalRayTracer.Build"/> made of a ray scene.</summary>
/// <param name="SolidTriangles">Fully covered: an opaque geometry, never asks the coverage function.</param>
/// <param name="PartialTriangles">Partly covered: a non-opaque geometry whose intersection function flips the coin.</param>
/// <param name="UncoveredTriangles">Coverage 0: left out, since no ray can meet them.</param>
/// <param name="DuplicateRows">Packed rows naming a triangle already taken in the same hierarchy (spatial-split references).</param>
public readonly record struct MetalRayTracerBuild(
    int Hierarchies, int Geometries, int Entries,
    long SolidTriangles, long PartialTriangles, long UncoveredTriangles, long DuplicateRows,
    double CpuPrepareMs, double CpuClassifyMs, double CpuPackMs, double BlasGpuMs, double CompactTlasGpuMs,
    long BlasBytes, long CompactedBytes, long TlasBytes, long ScratchBytes, long GeometryBytes);

/// <summary>What the in-frame shadow dispatches (<see cref="MetalRayTracer.EncodeShadow"/>) have cost, from their own GPU times.</summary>
/// <param name="Failures">Dispatches skipped for a bad argument or failed on the GPU (their signal still went out).</param>
public readonly record struct MetalShadowStats(long Dispatches, double TotalGpuMs, double LastGpuMs, long Failures)
{
    public double MeanGpuMs => Dispatches > 0 ? TotalGpuMs / Dispatches : 0;
}

/// <summary>
/// The packed ray scene (<see cref="RayQueryGpuData"/>) traced on Apple's ray tracing hardware, through a native Metal
/// pass working on the Vulkan device's own GPU buffers.
/// </summary>
/// <remarks>
/// <para>
/// One bottom-level acceleration structure per distinct hierarchy, built from the Positions block where it lies, and
/// one top-level instance per entry, so a hit names the same entry, triangle row and owner that ray_query.glsl's does
/// and is resolved through the same blocks. Rays and answers are the closest batch's (VulkanSponza's SponzaLoop.RayBatch.cs):
/// two float4 per ray (origin and tMin, direction and tMax), seed = the base plus its index; three uint4 per answer.
/// </para>
/// <para>
/// Every call waits for its GPU work, and expects Vulkan to be idle on the buffers it names: the caller orders it
/// (<see cref="IGraphicsDevice.WaitIdle"/>), as for a readback. Coverage is read once, at <see cref="Build"/>: a
/// change to the surfaces block afterwards needs a rebuild.
/// </para>
/// </remarks>
public sealed class MetalRayTracer : IDisposable
{
    private readonly IGraphicsDevice device;
    private nint ctx;

    private MetalRayTracer(IGraphicsDevice device, nint ctx)
    {
        this.device = device;
        this.ctx = ctx;
    }

    /// <summary>A tracer on this device, or null with the reason it cannot have one.</summary>
    /// <summary>
    /// Starts a GPU frame capture of the whole device -- the Vulkan frame (MoltenVK's queue) and this tracer's batches alike --
    /// into a .gputrace document at <paramref name="path"/>, until <see cref="EndCapture"/>. Needs MTL_CAPTURE_ENABLED=1 in
    /// the process environment before the Metal device is made. False with the reason when it cannot.
    /// </summary>
    public bool BeginCapture(string path, out string reason)
    {
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        reason = "";
        if (HwxNative.hwx_capture_begin(ctx, path) == 0) return true;
        reason = Marshal.PtrToStringUTF8(HwxNative.hwx_error(ctx)) ?? "unknown";
        return false;
    }

    /// <summary>Ends a capture <see cref="BeginCapture"/> started (the document is written then).</summary>
    public void EndCapture()
    {
        if (ctx != 0) HwxNative.hwx_capture_end(ctx);
    }

    public static unsafe MetalRayTracer? TryCreate(IGraphicsDevice device, out string reason)
    {
        if (!OperatingSystem.IsMacOS()) { reason = "Metal ray tracing needs macOS."; return null; }
        if ((device.Features & GraphicsFeatures.MetalObjects) == 0) { reason = "the device cannot export its Metal objects (GraphicsFeatures.MetalObjects)."; return null; }
        if (!File.Exists(HwxNative.LibraryPath)) { reason = $"{HwxNative.LibraryPath} was not built (BuildBlixHwx)."; return null; }
        var error = stackalloc byte[512];
        error[0] = 0;
        var ctx = HwxNative.hwx_create(device.ExportMetalDevice(), error, 512);
        if (ctx == 0) { reason = Marshal.PtrToStringUTF8((nint)error) ?? "hwx_create failed."; return null; }
        reason = "";
        return new MetalRayTracer(device, ctx);
    }

    /// <summary>Builds the acceleration structures over a packed scene.</summary>
    /// <param name="blocks">The scene's GPU buffers, in <see cref="RayQueryGpuData.BlockNames"/> order.</param>
    /// <param name="surfaces">The surfaces block's words as baked (coverage in the top byte), one per triangle row.</param>
    /// <param name="alphaTested">Optional, one per triangle row: rows the shadow pass alpha-tests (<see cref="SetAlpha"/>).
    /// They go in the non-opaque geometry even where the bake found them fully covered, since its samples can miss a
    /// cut-out's holes; the coverage coin still meets those every time.</param>
    public unsafe MetalRayTracerBuild Build(RayQueryGpuData data, IReadOnlyList<GpuBufferHandle> blocks, ReadOnlySpan<uint> surfaces,
        ReadOnlySpan<bool> alphaTested = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        if (blocks.Count != RayQueryGpuData.BlockNames.Length)
            throw new ArgumentException($"{blocks.Count} blocks; a packed scene has {RayQueryGpuData.BlockNames.Length}.", nameof(blocks));
        var rows = data.Triangles.Length / 4;
        if (surfaces.Length < rows) throw new ArgumentException($"{surfaces.Length} surface words for {rows} triangle rows.", nameof(surfaces));

        var clock = Stopwatch.StartNew();
        // A distinct hierarchy is a run of triangle rows from its base to the next one's.
        var bases = data.Instances.Select(i => i.TriangleBase).Distinct().Order().ToArray();
        var regionBases = data.Instances.Where(i => i.Placement == RayQueryGpuData.RegionPlacement).Select(i => i.TriangleBase).ToHashSet();
        var solid = new List<uint>[bases.Length];
        var partial = new List<uint>[bases.Length];
        var uncovered = new long[bases.Length];
        var duplicates = new long[bases.Length];
        var triangles = data.Triangles;
        var owners = data.Owners;
        var surfaceWords = surfaces.ToArray();
        var tested = alphaTested.ToArray();
        if (tested.Length != 0 && tested.Length < rows)
            throw new ArgumentException($"{tested.Length} alpha-tested flags for {rows} triangle rows.", nameof(alphaTested));
        Parallel.For(0, bases.Length, h =>
        {
            var first = (int)bases[h];
            var end = h + 1 < bases.Length ? (int)bases[h + 1] : rows;
            var region = regionBases.Contains(bases[h]);
            var seen = new HashSet<ulong>();
            var s = new List<uint>();
            var p = new List<uint>();
            for (var row = first; row < end; row++)
            {
                // A triangle's identity: its owner (placement, mesh triangle) in a region, its mesh index in an instance.
                var w = triangles[row * 4 + 3];
                var key = region ? ((ulong)owners[w * 2] << 32) | (owners[w * 2 + 1] & 0x7FFFFFFFu) : w;
                if (!seen.Add(key)) { duplicates[h]++; continue; }
                var coverage = RayQueryGpuData.CoverageByte(surfaceWords[row]);   // 0, 16..240 (sixteenths), 255 solid: the word's top byte also carries the roughness level
                if (coverage == 0) uncovered[h]++;
                else (coverage == 255 && (tested.Length == 0 || !tested[row]) ? s : p).Add((uint)row);
            }
            solid[h] = s;
            partial[h] = p;
        });

        var classifyMs = clock.Elapsed.TotalMilliseconds;
        // What the hardware traverses alone and what calls the coverage function (blix_coverage) at each candidate hit.
        // BLIX_HWX_ALL_OPAQUE=1: the partly covered built opaque too -- a timing probe for those calls, never an image to
        // keep (leaves and chains turn solid).
        var allOpaque = Environment.GetEnvironmentVariable("BLIX_HWX_ALL_OPAQUE") == "1";
        // Crytek Sponza: 228,576 and 32,383; built all opaque its RTGI rays took 3.75-3.91 ms a frame against 4.10-4.31 (2026-10-27).
        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[MetalHWX] {solid.Sum(l => (long)l.Count):N0} opaque triangles, {partial.Sum(l => (long)l.Count):N0} partly covered")
            + (allOpaque ? " (built opaque: BLIX_HWX_ALL_OPAQUE)" : " (their coverage function at every candidate hit)") + ".");
        if (allOpaque)
            for (var h = 0; h < bases.Length; h++) { solid[h].AddRange(partial[h]); partial[h].Clear(); }
        var primCount = solid.Sum(l => (long)l.Count) + partial.Sum(l => (long)l.Count);
        var indices = new uint[Math.Max(1, primCount * 3)];
        var primRows = new uint[Math.Max(1, primCount)];
        var blas = new HwxNative.Blas[bases.Length];
        var at = 0L;
        for (var h = 0; h < bases.Length; h++)
        {
            blas[h] = new HwxNative.Blas { PrimBase = (uint)at, SolidCount = (uint)solid[h].Count, PartialCount = (uint)partial[h].Count };
            foreach (var row in solid[h].Concat(partial[h]))
            {
                indices[at * 3] = triangles[row * 4];
                indices[at * 3 + 1] = triangles[row * 4 + 1];
                indices[at * 3 + 2] = triangles[row * 4 + 2];
                primRows[at] = row;
                at++;
            }
        }

        var packMs = clock.Elapsed.TotalMilliseconds - classifyMs;
        var hierarchyOf = new Dictionary<uint, uint>();
        for (var h = 0; h < bases.Length; h++) hierarchyOf[bases[h]] = (uint)h;
        var entries = new HwxNative.Entry[data.Instances.Length];
        for (var e = 0; e < entries.Length; e++)
        {
            // The stored matrix is world-to-mesh; Metal wants mesh-to-world, as columns: the row-vector matrix's rows.
            if (!Matrix4x4.Invert(data.Instances[e].WorldToLocal, out var world))
                throw new ArgumentException($"entry {e}'s matrix has no inverse.", nameof(data));
            ReadOnlySpan<float> m = stackalloc float[] { world.M11, world.M12, world.M13, world.M21, world.M22, world.M23,
                                                         world.M31, world.M32, world.M33, world.M41, world.M42, world.M43 };
            for (var k = 0; k < 12; k++) entries[e].Transform[k] = m[k];
            entries[e].Blas = hierarchyOf[data.Instances[e].TriangleBase];
        }
        var prepareMs = clock.Elapsed.TotalMilliseconds;

        HwxNative.BuildStats stats;
        int result;
        fixed (uint* ip = indices)
        fixed (uint* rp = primRows)
        fixed (HwxNative.Blas* bp = blas)
        fixed (HwxNative.Entry* ep = entries)
        {
            result = HwxNative.hwx_build(ctx,
                device.ExportMetalBuffer(blocks[4]), (uint)data.Positions.Length,
                device.ExportMetalBuffer(blocks[1]), device.ExportMetalBuffer(blocks[3]),
                device.ExportMetalBuffer(blocks[5]), device.ExportMetalBuffer(blocks[6]),
                ip, rp, (ulong)primCount, bp, (uint)blas.Length, ep, (uint)entries.Length, &stats);
        }
        if (result != 0) throw new InvalidOperationException($"Metal acceleration structure build: {Error}");

        return new MetalRayTracerBuild(bases.Length, (int)stats.GeometryCount, entries.Length,
            solid.Sum(l => (long)l.Count), partial.Sum(l => (long)l.Count), uncovered.Sum(), duplicates.Sum(),
            prepareMs, classifyMs, packMs, stats.BlasGpuMs, stats.CompactTlasGpuMs,
            (long)stats.BlasBytes, (long)stats.CompactedBytes, (long)stats.TlasBytes, (long)stats.ScratchBytes, (long)stats.GeometryBytes);
    }

    /// <summary>Traces <paramref name="count"/> rays, <paramref name="repeats"/> times; each dispatch's GPU milliseconds.</summary>
    /// <param name="closestOnly">Skip the any-hit trace (its flag then repeats the closest hit's).</param>
    /// <param name="sun">With it (toward the sun, xyz; w the lift along the hit's normal), each hit's sun too: a shadow ray
    /// from the hit to <paramref name="reach"/>, its hit flags gaining 8 when nothing meets it.</param>
    /// <param name="discTan">With <paramref name="sun"/>: the tangent of the sun's angular radius, each shadow ray toward its
    /// own point of the disc (0: the disc's centre, a point sun).</param>
    public unsafe double[] Trace(GpuBufferHandle rays, GpuBufferHandle hits, int count, uint seedBase, bool closestOnly, int repeats = 1,
        Vector4? sun = null, float reach = 0f, float discTan = 0f)
    {
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repeats);
        var ms = new double[repeats];
        fixed (double* mp = ms)
        {
            var s = sun ?? Vector4.Zero;
            if (HwxNative.hwx_trace(ctx, device.ExportMetalBuffer(rays), device.ExportMetalBuffer(hits), (uint)count, seedBase,
                    closestOnly ? 1u : 0u, (uint)repeats, mp, sun.HasValue ? (float*)&s : null, reach, discTan) != 0)
                throw new InvalidOperationException($"Metal trace: {Error}");
        }
        return ms;
    }

    /// <summary>
    /// From now on this tracer submits on the Vulkan device's own Metal command queue
    /// (<see cref="IGraphicsDevice.ExportMetalCommandQueue"/>), its in-frame passes in commit order with the frame's
    /// Vulkan work on one queue; <paramref name="deviceQueue"/> false: back to its own. Whether one queue is cheaper
    /// than two is measured, not assumed: on VulkanSponza's frame (2026-10) two were, the tracers' work running beside
    /// the Vulkan passes rather than between them.
    /// </summary>
    public void UseDeviceQueue(bool deviceQueue = true)
    {
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        HwxNative.hwx_use_queue(ctx, deviceQueue ? device.ExportMetalCommandQueue() : 0);
    }

    /// <summary>
    /// From now on this tracer submits on <paramref name="other"/>'s command queue: tracers whose in-frame passes share the
    /// frame's timeline must run in the order the frame encodes them, which one serial queue guarantees and two do not
    /// (VulkanSponza's BSDF reference tracer beside the frame's, SponzaLoop.ReferenceMaterials.cs).
    /// </summary>
    public void ShareQueueWith(MetalRayTracer other)
    {
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        ObjectDisposedException.ThrowIf(other.ctx == 0, other);
        HwxNative.hwx_share_queue(ctx, other.ctx);
    }

    /// <summary>The device's GPU memory in MB: allocated by every client (Vulkan's included), and the working set Metal
    /// recommends staying under before it pages.</summary>
    public unsafe (double AllocatedMb, double RecommendedMb) Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(ctx == 0, this);
            ulong allocated, recommended;
            HwxNative.hwx_memory(ctx, &allocated, &recommended);
            return (allocated / 1048576.0, recommended / 1048576.0);
        }
    }

    /// <summary>A material's alpha test, as the raster draws it: its albedo texture's alpha times a factor, against a cutoff.</summary>
    public readonly record struct AlphaMaterial(TextureHandle Albedo, float Cutoff, float AlphaFactor);

    /// <summary>
    /// From now on a shadow pass that asks for it (<see cref="EncodeShadow"/>'s alphaTest) meets a partly covered triangle
    /// where its material's alpha passes at the point hit, instead of by the coverage coin: shadows of cut-out leaves shaped
    /// like the leaves, and steady from frame to frame. Every other trace keeps the coin, the software traversal's.
    /// </summary>
    /// <param name="rowMaterial">Per packed triangle row, its index in <paramref name="materials"/>; uint.MaxValue: none
    /// (the coin decides).</param>
    /// <param name="uvs">The scene's texture coordinates on the GPU, one float2 per position (RayQueryGpuData.Uvs).</param>
    /// <param name="mip">The mip level the alpha is read at: coarser is cheaper (every leaf card a shadow ray crosses samples
    /// once) and rounder; 0 is the texture's own.</param>
    public unsafe void SetAlpha(IReadOnlyList<AlphaMaterial> materials, ReadOnlySpan<uint> rowMaterial, GpuBufferHandle uvs, float mip = 0f)
    {
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        var textures = new nint[Math.Max(1, materials.Count)];
        var cutoffs = new float[textures.Length];
        var factors = new float[textures.Length];
        for (var m = 0; m < materials.Count; m++)
        {
            textures[m] = device.ExportMetalTexture(materials[m].Albedo);
            cutoffs[m] = materials[m].Cutoff;
            factors[m] = materials[m].AlphaFactor;
        }
        fixed (nint* tp = textures)
        fixed (float* cp = cutoffs)
        fixed (float* fp = factors)
        fixed (uint* rp = rowMaterial)
        {
            if (HwxNative.hwx_set_alpha(ctx, tp, cp, fp, (uint)materials.Count, rp, (uint)rowMaterial.Length, device.ExportMetalBuffer(uvs), mip) != 0)
                throw new InvalidOperationException($"Metal alpha test: {Error}");
        }
    }

    /// <summary>
    /// From now on every trace but the shadow pass's own (its tables take the coin until <see cref="SetAlpha"/>) meets a partly covered triangle
    /// where its material's alpha passes at the point hit, read from the trailer of <see cref="Blix.Geometry.RayAlphaAtlas"/> on the surface
    /// words (<paramref name="rows"/> words in: the triangle count) -- the software traversal's per-hit test, test for test. With
    /// <paramref name="shadows"/> the tables <see cref="SetAlpha"/> gave textures to (the sun's shadows, the lightlet bake) read the atlas too, so
    /// authored BLEND is stochastic in shadow as in every other ray; they keep the coin where <see cref="SetAlpha"/> was not called.
    /// </summary>
    public void SetAlphaAtlas(int rows, bool shadows = false)
    {
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        if (HwxNative.hwx_set_alpha_atlas(ctx, (uint)rows, shadows ? 1 : 0) != 0)
            throw new InvalidOperationException($"Metal alpha atlas: {Error}");
    }

    /// <summary>
    /// Which triangle rows the sun's shadow rays go through (<see cref="EncodeShadow"/>'s <c>throughSheets</c>): per row,
    /// the sheet's transmittance as rgb8 (r in the low byte; the top byte 1 for a double-sided sheet, every crossing
    /// counted), 0 for none. The ray goes on through such a row (<see cref="EncodeShadow"/>'s throughAt). The rows must have
    /// been built non-opaque (<see cref="Build"/>'s alpha-tested flag), or the trace never asks.
    /// </summary>
    public unsafe void SetTransmit(ReadOnlySpan<uint> rowTransmit)
    {
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        fixed (uint* rp = rowTransmit)
        {
            if (HwxNative.hwx_set_transmit(ctx, rp, (uint)rowTransmit.Length) != 0)
                throw new InvalidOperationException($"Metal transmission: {Error}");
        }
    }

    /// <summary>
    /// Sun shadows as a frame's external pass (<see cref="RenderGraph.External"/>): waits for what the frame recorded
    /// before the pass, traces one ray per origin toward <paramref name="towardLight"/>, and signals the rest of the
    /// frame on. Returns without waiting.
    /// </summary>
    /// <param name="origins">float4 per ray: where it starts, and how far it goes (0 or less: no ray, reported unoccluded).</param>
    /// <param name="occluded">uint per ray from <paramref name="occludedOffset"/> bytes: 1 where something is in the way of the
    /// light, plus 2 where the sky ray (if any) meets something; written whole.</param>
    /// <param name="seedBase">Ray i's coverage seed is this plus i; vary it per frame for the partly covered to dither over time.</param>
    /// <param name="alphaTest">Meet cut-outs by their alpha (<see cref="SetAlpha"/>) rather than by the coverage coin the
    /// software batch uses; ignored until SetAlpha.</param>
    /// <param name="statSlot">Which of <see cref="ShadowStatsOf"/>'s slots (0-3) this dispatch's GPU time counts in.</param>
    /// <param name="skyRays">Optional: a second ray per ray, two float4s (origin and tMax, 0 for none; then direction); its
    /// word gains 2 where it meets anything (the point does not see the sky that way).</param>
    /// <remarks>
    /// Never throws for a bad argument: the frame waits for the signal, so the work is skipped, the signal still sent,
    /// and the failure counted in <see cref="ShadowStats"/>. Coverage and geometry are what <see cref="Build"/> saw.
    /// </remarks>
    /// <param name="limit">Optional: a uint the GPU wrote earlier in the frame (at <paramref name="limitOffset"/>), the live
    /// count; the rays past it are neither traced nor answered. A wavefront sized by its budget then costs what it holds.</param>
    /// <param name="directions">Optional: a float4 per ray, its own direction (xyz), in place of
    /// <paramref name="towardLight"/>.</param>
    /// <param name="pixelOf">Optional: packed rays -- ray i is two float4s in <paramref name="origins"/> (origin + tMax, direction)
    /// and its answer goes to the word pixelOf[i] (a uint per ray), not i.</param>
    /// <param name="throughAt">Through the rows <see cref="SetTransmit"/> names, when set: a ray that crossed such a sheet and
    /// reached the sun is answered occluded (the sheet's distance in the upper 16 bits, as any blocker's) with bit 2, and the
    /// word throughAt words past its own holds the light the sunlit sheet sends its point -- rgb8, each channel the square
    /// root of a fraction of the sun's. The occlusion buffer must hold those words too.</param>
    public unsafe void EncodeShadow(ExternalWorkSync sync, GpuBufferHandle origins, GpuBufferHandle occluded, long occludedOffset,
        int count, Vector3 towardLight, uint seedBase, GpuBufferHandle? skyRays = null, bool alphaTest = false, int statSlot = 0,
        GpuBufferHandle? limit = null, long limitOffset = 0, GpuBufferHandle? directions = null, GpuBufferHandle? pixelOf = null,
        uint? throughAt = null)
    {
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        var direction = stackalloc float[3] { towardLight.X, towardLight.Y, towardLight.Z };
        HwxNative.hwx_encode_shadow(ctx, sync.MetalSharedEvent, sync.WaitValue, sync.SignalValue,
            device.ExportMetalBuffer(origins), 0, device.ExportMetalBuffer(occluded), (ulong)occludedOffset,
            (uint)Math.Max(count, 0), direction, seedBase, skyRays is { } sky ? device.ExportMetalBuffer(sky) : 0,
            (alphaTest ? 1u : 0u) | (throughAt is not null ? 2u : 0u),
            (uint)Math.Clamp(statSlot, 0, 3), limit is { } l ? device.ExportMetalBuffer(l) : 0, (ulong)limitOffset,
            directions is { } d ? device.ExportMetalBuffer(d) : 0, pixelOf is { } px ? device.ExportMetalBuffer(px) : 0,
            throughAt ?? 0u);
    }

    /// <summary>
    /// Closest hits as a frame's external pass (a wavefront's batch): waits for what the frame recorded before the pass,
    /// traces <paramref name="count"/> rays in <see cref="Trace"/>'s formats (the any-hit trace skipped), and signals the
    /// rest of the frame on. Returns without waiting; never throws for a bad argument (counted in <see cref="TraceStats"/>).
    /// </summary>
    /// <param name="limit">Optional: the live count, as <see cref="EncodeShadow"/>'s.</param>
    /// <param name="sun">Optional: each hit's own sun ray too, as <see cref="Trace"/>'s (direction, lift), with
    /// <paramref name="reach"/> and <paramref name="discTan"/>.</param>
    public unsafe void EncodeTrace(ExternalWorkSync sync, GpuBufferHandle rays, long raysOffset, GpuBufferHandle hits, long hitsOffset,
        int count, uint seedBase, GpuBufferHandle? limit = null, long limitOffset = 0, Vector4? sun = null, float reach = 0f, float discTan = 0f, bool instances = false)
    {
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        var s = sun ?? Vector4.Zero;
        HwxNative.hwx_encode_trace(ctx, sync.MetalSharedEvent, sync.WaitValue, sync.SignalValue,
            device.ExportMetalBuffer(rays), (ulong)raysOffset, device.ExportMetalBuffer(hits), (ulong)hitsOffset,
            (uint)Math.Max(count, 0), seedBase, limit is { } l ? device.ExportMetalBuffer(l) : 0, (ulong)limitOffset,
            sun.HasValue ? (float*)&s : null, reach, discTan, instances ? 1 : 0);
    }

    /// <summary>
    /// MetalFX temporal upscaling as an external pass (native hwx_fx_encode): the frame's jittered colour, its depth and
    /// motion at the render size, into <paramref name="output"/> at the window's. <paramref name="jitter"/>: this frame's
    /// sample offset in render pixels; <paramref name="motionScale"/>: what turns the motion texture's values into pixels
    /// toward last frame's position; <paramref name="reset"/>: no history. The frame's event is signalled whatever
    /// happens; null when the scaler ran, else why not.
    /// </summary>
    public string? EncodeUpscale(ExternalWorkSync sync, TextureHandle color, TextureHandle depth, TextureHandle motion, TextureHandle output,
        Vector2 jitter, Vector2 motionScale, bool reset)
    {
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        var result = HwxNative.hwx_fx_encode(ctx, sync.MetalSharedEvent, sync.WaitValue, sync.SignalValue,
            device.ExportMetalTexture(color), device.ExportMetalTexture(depth), device.ExportMetalTexture(motion), device.ExportMetalTexture(output),
            jitter.X, jitter.Y, motionScale.X, motionScale.Y, reset ? 1 : 0);
        return result == 0 ? null : Error;
    }

    /// <summary>The MetalFX upscales so far: how many, their GPU time (ns) in all and the last's, and the failed.</summary>
    public static unsafe (ulong Dispatches, ulong TotalGpuNs, ulong LastGpuNs, ulong Failures) UpscaleStats()
    {
        ulong d, t, l, f;
        HwxNative.hwx_fx_stats(&d, &t, &l, &f);
        return (d, t, l, f);
    }

    /// <summary>
    /// Several in-frame batches as one external pass: one Metal command buffer that waits for the frame once, holds every
    /// <see cref="EncodeTrace"/> / <see cref="EncodeShadow"/> <paramref name="encode"/> makes (in order, their own sync
    /// values unused), and signals the frame on once. Each batch alone is a trip to Metal and back, the GPU idle at each
    /// turn. Inside a group the per-batch stats read the group's GPU time.
    /// </summary>
    public void EncodeGroup(ExternalWorkSync sync, Action encode)
    {
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        if (HwxNative.hwx_group_begin(ctx, sync.MetalSharedEvent, sync.WaitValue) != 0)
            throw new InvalidOperationException($"Metal batch group: {Error}");
        try
        {
            encode();
        }
        finally
        {
            HwxNative.hwx_group_end(ctx, sync.MetalSharedEvent, sync.SignalValue);
        }
    }

    /// <summary>What the in-frame closest-hit dispatches (<see cref="EncodeTrace"/>) have cost, from their own GPU times.</summary>
    public unsafe MetalShadowStats TraceStats
    {
        get
        {
            ObjectDisposedException.ThrowIf(ctx == 0, this);
            ulong dispatches, total, last, failures;
            HwxNative.hwx_trace_stats(ctx, &dispatches, &total, &last, &failures);
            return new MetalShadowStats((long)dispatches, total / 1e6, last / 1e6, (long)failures);
        }
    }

    /// <summary>What every in-frame shadow dispatch (<see cref="EncodeShadow"/>) has cost, from their own GPU times.</summary>
    public MetalShadowStats ShadowStats => ShadowStatsOf(-1);

    /// <summary>What the shadow dispatches of one stats slot (0-3; -1: all) have cost.</summary>
    public unsafe MetalShadowStats ShadowStatsOf(int slot)
    {
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        ulong dispatches, total, last, failures;
        HwxNative.hwx_shadow_stats(ctx, slot < 0 ? 0xFFFFFFFFu : (uint)slot, &dispatches, &total, &last, &failures);
        return new MetalShadowStats((long)dispatches, total / 1e6, last / 1e6, (long)failures);
    }

    private string Error => Marshal.PtrToStringUTF8(HwxNative.hwx_error(ctx)) ?? "unknown error";

    public void Dispose()
    {
        if (ctx == 0) return;
        HwxNative.hwx_destroy(ctx);
        ctx = 0;
    }
}
