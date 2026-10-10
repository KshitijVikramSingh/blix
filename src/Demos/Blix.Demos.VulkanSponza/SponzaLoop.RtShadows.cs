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
// The trace runs alongside the passes declared between it and rt-shadow-join (RenderGraph.ExternalJoin), and the
// ray generation skips surfaces facing away from the sun (in their own shadow). --rt-shadow-scale 2 traces one pixel
// of each 2x2 block (a different one each frame) and lit.frag upsamples by depth.
//
// The cascades still render: fog, the clipmap's injection, the texels and the incident pass read them. Until the
// acceleration structures exist (built once, after the surface bake has given coverage) the cascades shade as before.
// Origins and masks are one per frame slot, written in the slot's frame and read in it, so the slot's fence orders reuse.
internal sealed partial class SponzaLoop
{
    private bool rtShadows;
    private bool rtNoOverlap;
    // Full pixels per traced pixel, each way: 1 traces every pixel, 2 a quarter of them.
    private int rtShadowScale = 1;
    private ShaderInterface rtShadowRaysInterface = null!;
    private PassHandle rtShadowRaysPassHandle, rtShadowTracePassHandle;
    private PipelineHandle rtShadowRaysPipeline;
    // The frame's Metal tracer, shared by every wavefront (SponzaLoop.RayBatch.cs): built once, after the surface bake.
    private MetalRayTracer? frameTracer;
    private bool frameTracerFailed;
    private GpuBufferHandle[] rtShadowOrigins = Array.Empty<GpuBufferHandle>();
    private GpuBufferHandle[] rtShadowMasks = Array.Empty<GpuBufferHandle>();
    // The frame's size the buffers were made for, and the traced grid in it.
    private (int Width, int Height) rtShadowSize;
    private (int Width, int Height) rtShadowTraced;
    private GpuBufferHandle rtShadowPlaceholder;
    // The mask the lit pass binds this frame: a slot's, or the placeholder of zeros (the cascades shade).
    private GpuBufferHandle rtShadowBound;
    // Frames since the surface bake was recorded: the build reads its coverage once it has run.
    private int rtFramesSinceBake;
    private uint rtShadowFrame;
    private int rtShadowFrames;
    private MetalRayTracerBuild? rtBuild;

    private GpuBufferHandle RtShadowMask
    {
        get
        {
            if (rtShadowPlaceholder.Equals(default(GpuBufferHandle)))
                rtShadowPlaceholder = Own(device.CreateGpuBuffer(16, name: "sponza.rt-shadow.placeholder"));
            return rtShadowBound.Equals(default(GpuBufferHandle)) ? rtShadowPlaceholder : rtShadowBound;
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
            var slots = device.MaxFramesInFlightCount;
            var traced = ((width + rtShadowScale - 1) / rtShadowScale, (height + rtShadowScale - 1) / rtShadowScale);
            var header = MemoryMarshal.AsBytes(new uint[] { (uint)traced.Item1, (uint)traced.Item2, 1u, (uint)rtShadowScale }.AsSpan()).ToArray();
            var tracedCount = traced.Item1 * traced.Item2;
            rtShadowOrigins = Enumerable.Range(0, slots)
                .Select(s => Own(device.CreateGpuBuffer(tracedCount * 16, name: $"sponza.rt-shadow.origins{s}"))).ToArray();
            rtShadowMasks = Enumerable.Range(0, slots)
                .Select(s => Own(device.CreateGpuBuffer(16 + tracedCount * 4, header, $"sponza.rt-shadow.mask{s}"))).ToArray();
            rtShadowSize = (width, height);
            rtShadowTraced = traced;
        }

        var slot = device.CurrentFrameSlot;
        var origins = rtShadowOrigins[slot];
        var mask = rtShadowMasks[slot];
        var towardSun = -Vector3.Normalize(sunDirection);
        var bounds = rayQueries.Nodes[0].Bounds;
        var reach = (bounds.Max - bounds.Min).Length();
        Matrix4x4.Invert(viewProjJittered, out var invViewProj);
        var (tracedWidth, tracedHeight) = rtShadowTraced;
        // Which pixel of its block each traced pixel reads: all four in turn at half resolution, so TAA sees them all.
        var block = rtShadowScale > 1 ? (int)(rtShadowFrame % 4) : 0;
        var sample = new Vector2(block & 1, block >> 1);
        graph.Dispatch(rtShadowRaysPassHandle, new DispatchCommand(rtShadowRaysPipeline, (tracedWidth + 7) / 8, (tracedHeight + 7) / 8, 1,
            new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                new("uSize", new Vector4Uniform(new Vector4(tracedWidth, tracedHeight, rtShadowScale, 0f))),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                new("uSun", new Vector4Uniform(new Vector4(towardSun, reach))),
                new("uSample", new Vector4Uniform(new Vector4(sample, width, height))),
            },
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
            },
            Buffers: new[] { new ShaderBufferBinding("RtShadowOrigins", origins), new ShaderBufferBinding("RtShadowMask", mask) }));

        // A new coin per frame for the partly covered (foliage), so TAA averages their shadows instead of freezing one draw.
        var tracer = frameTracer!;
        var count = tracedWidth * tracedHeight;
        var seed = unchecked(rtShadowFrame++ * 0x9E3779B9u);
        graph.External(rtShadowTracePassHandle, sync => tracer.EncodeShadow(sync, origins, mask, 16, count, towardSun, seed));
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
        if (++rtFramesSinceBake < device.MaxFramesInFlightCount + 1) return false;
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
        rtBuild = tracer.Build(rayGpuData, blocks, surfaceWords);
        frameTracer = tracer;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] metal tracer: {rtBuild.Value.SolidTriangles + rtBuild.Value.PartialTriangles:N0} triangles ({rtBuild.Value.PartialTriangles:N0} partly covered) in {rtBuild.Value.Hierarchies} bottom-level structures, built in {rtBuild.Value.CpuPrepareMs + rtBuild.Value.BlasGpuMs + rtBuild.Value.CompactTlasGpuMs:0} ms; {rtBuild.Value.CompactedBytes / 1048576.0:0} MB."));
        return true;
    }

    private void WriteRtShadows()
    {
        if (frameTracer is null || !rtShadows) return;
        var stats = frameTracer.ShadowStats;
        var rays = (long)rtShadowTraced.Width * rtShadowTraced.Height;
        // What the last frame's rays were: traced, skipped as facing away, or no surface.
        var words = MemoryMarshal.Cast<byte, float>(device.ReadGpuBuffer(rtShadowOrigins[device.CurrentFrameSlot], 0, (int)rays * 16).AsSpan());
        long traced = 0, facingAway = 0;
        for (var i = 3; i < words.Length; i += 4)
        {
            if (words[i] > 0f) traced++;
            else if (words[i] < 0f) facingAway++;
        }
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] rt shadows, last frame's pixels: {100.0 * traced / rays:0.0}% traced, {100.0 * facingAway / rays:0.0}% facing away from the sun (not traced), {100.0 * (rays - traced - facingAway) / rays:0.0}% no surface."));
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] rt shadows: {stats.Dispatches:N0} frames traced ({rtShadowFrames:N0} recorded), {rtShadowTraced.Width}x{rtShadowTraced.Height} = {rays:N0} rays a frame for {rtShadowSize.Width}x{rtShadowSize.Height}{(rtShadowScale > 1 ? " (upsampled by depth)" : "")}; Metal GPU {stats.MeanGpuMs:0.000} ms mean, {stats.LastGpuMs:0.000} ms last, {rays / Math.Max(stats.MeanGpuMs, 1e-6) / 1000.0:0.0} M rays/s; {stats.Failures} failed."));
    }
}
