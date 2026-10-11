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
/// and is resolved through the same blocks. Rays and answers are ray_check.comp's: two float4 per ray (origin and tMin,
/// direction and tMax), seed = the base plus its index; three uint4 per answer.
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
                var coverage = surfaceWords[row] >> 24;
                if (coverage == 0) uncovered[h]++;
                else (coverage == 255 && (tested.Length == 0 || !tested[row]) ? s : p).Add((uint)row);
            }
            solid[h] = s;
            partial[h] = p;
        });

        var classifyMs = clock.Elapsed.TotalMilliseconds;
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
    /// <param name="closestOnly">Skip the any-hit trace (its flag then repeats the closest hit's), as --ray-bench does.</param>
    public unsafe double[] Trace(GpuBufferHandle rays, GpuBufferHandle hits, int count, uint seedBase, bool closestOnly, int repeats = 1)
    {
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repeats);
        var ms = new double[repeats];
        fixed (double* mp = ms)
        {
            if (HwxNative.hwx_trace(ctx, device.ExportMetalBuffer(rays), device.ExportMetalBuffer(hits), (uint)count, seedBase,
                    closestOnly ? 1u : 0u, (uint)repeats, mp) != 0)
                throw new InvalidOperationException($"Metal trace: {Error}");
        }
        return ms;
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
    public unsafe void SetAlpha(IReadOnlyList<AlphaMaterial> materials, ReadOnlySpan<uint> rowMaterial, GpuBufferHandle uvs)
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
            if (HwxNative.hwx_set_alpha(ctx, tp, cp, fp, (uint)materials.Count, rp, (uint)rowMaterial.Length, device.ExportMetalBuffer(uvs)) != 0)
                throw new InvalidOperationException($"Metal alpha test: {Error}");
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
    /// <param name="skyRays">Optional: a second ray per ray, two float4s (origin and tMax, 0 for none; then direction); its
    /// word gains 2 where it meets anything (the point does not see the sky that way).</param>
    /// <remarks>
    /// Never throws for a bad argument: the frame waits for the signal, so the work is skipped, the signal still sent,
    /// and the failure counted in <see cref="ShadowStats"/>. Coverage and geometry are what <see cref="Build"/> saw.
    /// </remarks>
    public unsafe void EncodeShadow(ExternalWorkSync sync, GpuBufferHandle origins, GpuBufferHandle occluded, long occludedOffset,
        int count, Vector3 towardLight, uint seedBase, GpuBufferHandle? skyRays = null, bool alphaTest = false)
    {
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        var direction = stackalloc float[3] { towardLight.X, towardLight.Y, towardLight.Z };
        HwxNative.hwx_encode_shadow(ctx, sync.MetalSharedEvent, sync.WaitValue, sync.SignalValue,
            device.ExportMetalBuffer(origins), 0, device.ExportMetalBuffer(occluded), (ulong)occludedOffset,
            (uint)Math.Max(count, 0), direction, seedBase, skyRays is { } sky ? device.ExportMetalBuffer(sky) : 0, alphaTest ? 1u : 0u);
    }

    /// <summary>
    /// Closest hits as a frame's external pass (a wavefront's batch): waits for what the frame recorded before the pass,
    /// traces <paramref name="count"/> rays in <see cref="Trace"/>'s formats (the any-hit trace skipped), and signals the
    /// rest of the frame on. Returns without waiting; never throws for a bad argument (counted in <see cref="TraceStats"/>).
    /// </summary>
    public unsafe void EncodeTrace(ExternalWorkSync sync, GpuBufferHandle rays, long raysOffset, GpuBufferHandle hits, long hitsOffset,
        int count, uint seedBase)
    {
        ObjectDisposedException.ThrowIf(ctx == 0, this);
        HwxNative.hwx_encode_trace(ctx, sync.MetalSharedEvent, sync.WaitValue, sync.SignalValue,
            device.ExportMetalBuffer(rays), (ulong)raysOffset, device.ExportMetalBuffer(hits), (ulong)hitsOffset,
            (uint)Math.Max(count, 0), seedBase);
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

    public unsafe MetalShadowStats ShadowStats
    {
        get
        {
            ObjectDisposedException.ThrowIf(ctx == 0, this);
            ulong dispatches, total, last, failures;
            HwxNative.hwx_shadow_stats(ctx, &dispatches, &total, &last, &failures);
            return new MetalShadowStats((long)dispatches, total / 1e6, last / 1e6, (long)failures);
        }
    }

    private string Error => Marshal.PtrToStringUTF8(HwxNative.hwx_error(ctx)) ?? "unknown error";

    public void Dispose()
    {
        if (ctx == 0) return;
        HwxNative.hwx_destroy(ctx);
        ctx = 0;
    }
}
