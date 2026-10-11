using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;

namespace Blix.Demos.VulkanSponza;

// --rt-shadows: the lit pass's sun shadows traced on Apple's ray tracing hardware, inside the frame.
//
//   rt-shadow-rays (Vulkan compute)  one ray per pixel from the pre-pass's depth and normal, toward the sun
//   rt-shadow-trace (Metal)          any-hit through the ray scene (MetalRayTracer), coverage as the software trace
//                                    has it, into the mask lit.frag reads; the frame splits around it on the device's
//                                    frame timeline (RenderGraph.External)
//   lit-scene                        reads the mask for the direct sun instead of the cascades
//
// The trace runs alongside the passes declared between it and rt-shadow-join (RenderGraph.ExternalJoin). A surface
// facing away from the sun is traced from its far side (rt_shadow_rays.comp): its answer feeds diffuse transmission.
//
// Nothing asks the cascades whether a point sees the sun: the clipmap's injection traces shadow rays at its hits
// (Setup: --clipmap-shadow-rays), the fog is off unless asked for, and until the trace is ready the lit pass leaves the
// sun off rather than falling back to the cascades. The cascades still render (the texels' level rule reads them).
//
// --rt-isolated: no incident field either, so nothing but the trace lights the frame. The ambient is the irradiance
// cube times the sky visibility the trace finds (one cosine-weighted sky ray per pixel a frame, TAA averaging them);
// a way to judge the trace alone, which without bounce light leaves an enclosed courtyard mostly dark.
//
// The cascades still render: fog, the clipmap's injection, the texels and the incident pass read them. Until the
// acceleration structures exist (built once, after the surface bake has given coverage) the cascades shade as before.
// Origins and masks are one per frame slot, written in the slot's frame and read in it, so the slot's fence orders reuse.
internal sealed partial class SponzaLoop
{
    private bool rtShadows;
    private bool rtNoOverlap;
    private bool rtIsolated;
    // --rt-coverage-coin: leaf cards' shadows by the baked coverage's coin (as the software traversal meets them), not by
    // their textures' alpha at the point hit.
    private bool rtCoverageCoin;
    // --rt-alpha-mip N: the mip the shadows' alpha test reads (0: full resolution).
    private float rtAlphaMip;
    // --rt-alpha-ab: the lit pass's trace takes the coin and the alpha test in turn, a frame each, its GPU time kept
    // apart for each (stats slots 1, 2): one process, one thermal state -- across runs the fanless M4 throttles enough to
    // drown the difference. Measured so (Sponza, 2880x1620): the alpha test costs what the coin does, within 1%.
    private bool rtAlphaAb;
    private static readonly string[] RtShadowKinds = { "batches", "coin", "alpha test" };
    private ShaderInterface rtShadowRaysInterface = null!;
    private PassHandle rtShadowRaysPassHandle, rtShadowTracePassHandle;
    private PipelineHandle rtShadowRaysPipeline;
    // The frame's Metal tracer, shared by every wavefront (SponzaLoop.RayBatch.cs): built once, after the surface bake.
    private MetalRayTracer? frameTracer;
    private bool frameTracerFailed;
    private GpuBufferHandle[] rtShadowOrigins = Array.Empty<GpuBufferHandle>();
    private GpuBufferHandle[] rtShadowMasks = Array.Empty<GpuBufferHandle>();
    // Isolated only: one sky ray per pixel, per frame slot.
    private GpuBufferHandle[] rtShadowSky = Array.Empty<GpuBufferHandle>();
    private bool RtSkyRays => rtShadows && rtIsolated;
    private (int Width, int Height) rtShadowSize;
    private GpuBufferHandle rtShadowPlaceholder;
    // Until the trace is ready: live with no size, so lit.frag leaves the sun off.
    private GpuBufferHandle rtShadowPending;
    // The mask the lit pass binds this frame: a slot's, or the placeholder of zeros (the cascades shade).
    private GpuBufferHandle rtShadowBound;
    // Frames since the surface bake was recorded: the build reads its coverage once it has run. Counted once a frame
    // (rtBakeCountedFrame): every batch asks for the tracer, and counting the asks read the coverage before the bake ran
    // (no foliage partly covered, its shadows solid).
    private int rtFramesSinceBake;
    private long rtBakeCountedFrame = -1;
    private uint rtShadowFrame;
    private int rtShadowFrames;
    private MetalRayTracerBuild? rtBuild;

    private GpuBufferHandle RtShadowMask
    {
        get
        {
            if (!rtShadowBound.Equals(default(GpuBufferHandle))) return rtShadowBound;
            if (rtShadows && frameTracer is null)
            {
                if (rtShadowPending.Equals(default(GpuBufferHandle)))
                    rtShadowPending = Own(device.CreateGpuBuffer(16, MemoryMarshal.AsBytes(new uint[] { 0u, 0u, 1u, 0u }.AsSpan()), "sponza.rt-shadow.pending"));
                return rtShadowPending;
            }
            return RtShadowPlaceholder;
        }
    }

    private GpuBufferHandle RtShadowPlaceholder
    {
        get
        {
            if (rtShadowPlaceholder.Equals(default(GpuBufferHandle)))
                rtShadowPlaceholder = Own(device.CreateGpuBuffer(16, name: "sponza.rt-shadow.placeholder"));
            return rtShadowPlaceholder;
        }
    }

    private void RecordRtShadows(int width, int height)
    {
        if (!rtShadows || rayQueries is null || rayGpuData is null) return;
        if (!EnsureFrameTracer())
        {
            if (frameTracerFailed) rtShadows = false;
            return;
        }
        if (abMode == "rtshadow" && AbOffPhase)
        {
            // The A/B's OFF phase: the cascades shade, and no external pass splits the frame.
            if (!rtShadowBound.Equals(default(GpuBufferHandle)))
            {
                rtShadowBound = default;
                litClipmapBuffer = null;
                litSceneBuffers = null;
            }
            return;
        }

        if (rtShadowSize != (width, height))
        {
            // Sized with the window; a resize waits for the device like every other resize here.
            device.WaitIdle();
            foreach (var b in rtShadowOrigins) device.DestroyGpuBuffer(b);
            foreach (var b in rtShadowMasks) device.DestroyGpuBuffer(b);
            foreach (var b in rtShadowSky) device.DestroyGpuBuffer(b);
            var slots = device.MaxFramesInFlightCount;
            var header = MemoryMarshal.AsBytes(new uint[] { (uint)width, (uint)height, 1u, RtSkyRays ? 1u : 0u }.AsSpan()).ToArray();
            rtShadowOrigins = Enumerable.Range(0, slots)
                .Select(s => Own(device.CreateGpuBuffer(width * height * 16, name: $"sponza.rt-shadow.origins{s}"))).ToArray();
            rtShadowMasks = Enumerable.Range(0, slots)
                .Select(s => Own(device.CreateGpuBuffer(16 + width * height * 4, header, $"sponza.rt-shadow.mask{s}"))).ToArray();
            rtShadowSky = Enumerable.Range(0, RtSkyRays ? slots : 0)
                .Select(s => Own(device.CreateGpuBuffer(width * height * 32, name: $"sponza.rt-shadow.sky{s}"))).ToArray();
            rtShadowSize = (width, height);
        }

        var slot = device.CurrentFrameSlot;
        var origins = rtShadowOrigins[slot];
        var mask = rtShadowMasks[slot];
        GpuBufferHandle? sky = RtSkyRays ? rtShadowSky[slot] : null;
        var towardSun = -Vector3.Normalize(sunDirection);
        var bounds = rayQueries.Nodes[0].Bounds;
        var reach = (bounds.Max - bounds.Min).Length();
        Matrix4x4.Invert(viewProjJittered, out var invViewProj);
        graph.Dispatch(rtShadowRaysPassHandle, new DispatchCommand(rtShadowRaysPipeline, (width + 7) / 8, (height + 7) / 8, 1,
            new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                new("uSize", new Vector4Uniform(new Vector4(width, height, 0f, 0f))),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                new("uSun", new Vector4Uniform(new Vector4(towardSun, reach))),
                new("uSky", new Vector4Uniform(new Vector4(sky is null ? 0f : 1f, rtShadowFrame % (1u << 24), 0f, 0f))),
            },
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
            },
            // Without sky rays the shader writes none, and the placeholder only fills the binding.
            Buffers: new[] { new ShaderBufferBinding("RtShadowOrigins", origins), new ShaderBufferBinding("RtShadowSky", sky ?? RtShadowPlaceholder) }));

        // A new coin per frame for the partly covered (foliage), so TAA averages their shadows instead of freezing one draw.
        var tracer = frameTracer!;
        var count = width * height;
        var kind = rtCoverageCoin ? 1 : rtAlphaAb ? 1 + (int)(rtShadowFrame % 2) : 2;
        var seed = unchecked(rtShadowFrame++ * 0x9E3779B9u);
        graph.External(rtShadowTracePassHandle, sync => tracer.EncodeShadow(sync, origins, mask, 16, count, towardSun, seed, sky,
            alphaTest: kind == 2, statSlot: kind));
        rtShadowFrames++;

        rtShadowBound = mask;
        litClipmapBuffer = null;
        litSceneBuffers = null;
    }

    // Once, a few frames after the surface bake was recorded (so it has run): coverage read back, structures built.
    // A stall of a second or two, once, at load.
    private bool EnsureFrameTracer()
    {
        if (frameTracer is not null) return true;
        if (frameTracerFailed || !raySurfacesBaked) return false;
        if (rtBakeCountedFrame != postLoadFrames)
        {
            rtBakeCountedFrame = postLoadFrames;
            rtFramesSinceBake++;
        }
        if (rtFramesSinceBake < device.MaxFramesInFlightCount + 1) return false;
        var tracer = MetalRayTracer.TryCreate(device, out var reason);
        if (tracer is null)
        {
            Console.WriteLine($"[VulkanSponza] metal tracer: unavailable, {reason}; rays stay on the software traversal.");
            frameTracerFailed = true;
            return false;
        }
        var surfaceWords = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(raySurfaces, 0, rayGpuData!.RowPlacements.Length * 4).AsSpan()).ToArray();
        device.WaitIdle();
        var blocks = RayQueryGpuData.BlockNames.Select(name => rayBlockBuffers.First(b => b.Name == name).Buffer).ToArray();
        var (alphaMaterials, rowMaterial) = rtCoverageCoin ? (new List<MetalRayTracer.AlphaMaterial>(), Array.Empty<uint>()) : RtAlphaMaterials();
        var alphaTested = rowMaterial.Select(m => m != uint.MaxValue).ToArray();
        rtBuild = tracer.Build(rayGpuData, blocks, surfaceWords, alphaTested);
        if (!rtCoverageCoin) tracer.SetAlpha(alphaMaterials, rowMaterial, raySurfaceBakeBuffers.First(b => b.Name == "BlixRayUvs").Buffer, rtAlphaMip);
        frameTracer = tracer;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] metal tracer: {rtBuild.Value.SolidTriangles + rtBuild.Value.PartialTriangles:N0} triangles ({rtBuild.Value.PartialTriangles:N0} partly covered, {(rtCoverageCoin ? "by the coverage coin" : string.Create(Inv, $"alpha-tested by {alphaMaterials.Count} materials' textures"))}) in {rtBuild.Value.Hierarchies} bottom-level structures, built in {rtBuild.Value.CpuPrepareMs + rtBuild.Value.BlasGpuMs + rtBuild.Value.CompactTlasGpuMs:0} ms; {rtBuild.Value.CompactedBytes / 1048576.0:0} MB."));
        return true;
    }

    // The shadow trace's alpha test: each cut-out material's albedo, cutoff and alpha factor (what the raster's mask
    // pipelines discard by), and each triangle row's material. Rows of materials with no cutoff are solid and never ask.
    private (List<MetalRayTracer.AlphaMaterial> Materials, uint[] RowMaterial) RtAlphaMaterials()
    {
        var data = rayGpuData!;
        var index = new Dictionary<RayMaterial, int>();
        var materials = new List<MetalRayTracer.AlphaMaterial>();
        var rowMaterial = new uint[data.RowPlacements.Length];
        for (var row = 0; row < rowMaterial.Length; row++)
        {
            var material = rayPlacementMaterials[(int)data.RowPlacements[row]];
            if (material.AlphaCutoff <= 0f) { rowMaterial[row] = uint.MaxValue; continue; }
            if (!index.TryGetValue(material, out var m))
            {
                m = materials.Count;
                index[material] = m;
                materials.Add(new MetalRayTracer.AlphaMaterial(material.Albedo, material.AlphaCutoff, material.BaseColor.W));
            }
            rowMaterial[row] = (uint)m;
        }
        return (materials, rowMaterial);
    }

    private void WriteRtShadows()
    {
        if (frameTracer is null || !rtShadows) return;
        var rays = (long)rtShadowSize.Width * rtShadowSize.Height;
        // And what the lit pass was told: how much of the frame the sun reaches.
        var mask = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtShadowMasks[device.CurrentFrameSlot], 16, (int)rays * 4).AsSpan());
        long blocked = 0;
        foreach (var word in mask) if ((word & 1u) != 0) blocked++;
        long skyBlocked = 0;
        foreach (var word in mask) if ((word & 2u) != 0) skyBlocked++;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] rt shadows, last frame's mask: sun blocked at {100.0 * blocked / rays:0.0}% of pixels, reaching {100.0 * (rays - blocked) / rays:0.0}%{(RtSkyRays ? string.Create(Inv, $"; sky rays blocked at {100.0 * skyBlocked / rays:0.0}%") : "")}."));
        for (var slot = 1; slot < RtShadowKinds.Length; slot++)
        {
            var stats = frameTracer.ShadowStatsOf(slot);
            if (stats.Dispatches == 0) continue;
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] rt shadows, {RtShadowKinds[slot]}: {stats.Dispatches:N0} frames traced ({rtShadowFrames:N0} recorded), {rtShadowSize.Width}x{rtShadowSize.Height} = {rays:N0} rays a frame; Metal GPU {stats.MeanGpuMs:0.000} ms mean, {stats.LastGpuMs:0.000} ms last, {rays / Math.Max(stats.MeanGpuMs, 1e-6) / 1000.0:0.0} M rays/s; {stats.Failures} failed."));
        }
    }
}
