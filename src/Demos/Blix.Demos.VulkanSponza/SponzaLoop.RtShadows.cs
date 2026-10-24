using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
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
    private readonly (bool Half, int X, int Y)[] rtShadowSlotPixel = new (bool, int, int)[8];
    private bool rtNoOverlap;
    // --metal-device-queue: the Metal tracers on the Vulkan device's own command queue rather than one of their own. A
    // Sponza frame idles 7-19 ms around each hand-off between the two queues (Metal System Trace), and one queue was the
    // obvious fix -- measured, it is worse: --ab queue (LOD 0, camera still, one process) put one queue at a 96.4 ms
    // median against 73.6 ms on two (IQRs 88-100 and 69-80). On two queues the tracers' work (the traced shadows' 7.4 ms
    // among it) runs beside the Vulkan passes; on one it runs between them. Two it is; this keeps the other measurable.
    private bool metalDeviceQueue;
    private bool tracerOnDeviceQueue;
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
    private PassHandle rtShadowRaysPassHandle, rtShadowTracePassHandle, rtShadowFilterPassHandle;
    private PipelineHandle rtShadowRaysPipeline, rtShadowFilterPipeline;
    private ShaderInterface rtShadowFilterInterface = null!;
    // Interleaved (ShadowsSettings.Interleave): a pixel of each 2x2 traced a frame, rt-shadow-fill writing the others
    // from the last frame's mask (reprojected; each pixel's depth per slot, for its disocclusion test) or the neighbours.
    // A frame with no good history -- the first, after a resize, the sun moved -- traces every pixel.
    private ShaderInterface rtShadowFillInterface = null!;
    private PassHandle rtShadowFillPassHandle;
    private PipelineHandle rtShadowFillPipeline;
    private GpuBufferHandle[] rtShadowDepths = Array.Empty<GpuBufferHandle>();
    private int rtShadowPrevSlot = -1;
    private Matrix4x4 rtShadowPrevViewProj;
    private Vector3 rtShadowPrevSun;
    private uint rtShadowPhase;
    private static readonly uint[] RtShadowPhases = { 0u, 3u, 1u, 2u };
    // The frame's Metal tracer, shared by every wavefront (SponzaLoop.RayBatch.cs): built once, after the surface bake.
    private MetalRayTracer? frameTracer;
    private bool frameTracerFailed;
    private GpuBufferHandle[] rtShadowOrigins = Array.Empty<GpuBufferHandle>();
    private GpuBufferHandle[] rtShadowMasks = Array.Empty<GpuBufferHandle>();
    // The mask's planes after its header, a uint a pixel each: the words; the light the sheets a ray went through send the
    // pixel (rgb8, sqrt-encoded fraction of the sun's: the Metal pass, rt_shadow_fill.comp); that light filtered
    // (rt_shadow_filter.comp, which lit.frag reads with the header's z at 2).
    private const int RtMaskPlanes = 3;
    // Triangle rows of sheets that transmit (RtRowTransmit); none: the trace writes no sheet light.
    private int rtSheetRows;
    // Per ray, its own direction toward the sun's disc (ShadowsSettings.SunDiscDegrees), per frame slot.
    private GpuBufferHandle[] rtShadowDirections = Array.Empty<GpuBufferHandle>();
    // Per pixel the lightlets' answer (0 lit, 1 shadowed, 2 traced: rt_shadow_rays.comp), per frame slot.
    private GpuBufferHandle[] rtShadowBaked = Array.Empty<GpuBufferHandle>();
    private GpuBufferHandle RtShadowBakedBound => rtShadowBaked.Length > 0 && !rtShadowBound.Equals(default(GpuBufferHandle))
        ? rtShadowBaked[device.CurrentFrameSlot] : RtShadowPlaceholder;
    // Isolated only: one sky ray per pixel, per frame slot.
    private GpuBufferHandle[] rtShadowSky = Array.Empty<GpuBufferHandle>();
    private bool RtSkyRays => rtShadows && rtIsolated;
    private (int Width, int Height) rtShadowSize;
    // The traced rays packed (rt_shadow_rays.comp, uCompact): only they go to Metal, each SIMD group full. Off with sky
    // rays (traced per pixel) or --no-rt-pack (the A/B).
    private bool rtNoPack;
    private bool RtPack => !rtNoPack && !RtSkyRays;
    // The per-pixel origins and directions (285 MB at 2880x1620, two slots): the unpacked trace reads them, and the
    // census tools (leak census, lightlet census and record study); a packed frame needs neither.
    private bool RtPerPixel => !RtPack || rayLeakCensus || lightletCensus || lightletRecord;
    private GpuBufferHandle[] rtShadowPacked = Array.Empty<GpuBufferHandle>();
    private GpuBufferHandle[] rtShadowPixelOf = Array.Empty<GpuBufferHandle>();
    private GpuBufferHandle[] rtShadowCounters = Array.Empty<GpuBufferHandle>();
    private uint[] rtShadowSlotUses = Array.Empty<uint>();
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
        if (!rtShadows || rayGpuData is null) return;
        if (!EnsureFrameTracer())
        {
            if (frameTracerFailed) rtShadows = false;
            return;
        }
        // The A/B's OFF phase (the cascades shade), or shadows off (--no-shadow: the lit pass applies none, so the frame
        // traces none -- the tracer stays for the GI, its batches a group of their own): no external pass splits the frame.
        if (abMode == "rtshadow" && AbOffPhase || !shadows.Enabled)
        {
            rtShadowPrevSlot = -1;
            if (!rtShadowBound.Equals(default(GpuBufferHandle)))
            {
                rtShadowBound = default;
                litClipmapBuffer = null;
                litSceneBuffers = null;
            }
            return;
        }

        // RenderSettings.HalfResShadows: a ray per half-res pixel, from the full-res pixel it stands for this frame
        // (SponzaLoop.HalfLighting.cs); lighting_upsample.frag brings the mask back to full resolution.
        if (halfShadowsNow) (width, height) = HalfLightingSize(width, height);
        var sub = LightingSubpixel;
        // Which full-res pixel each slot's rays started from (the lightlet census reads them back, SponzaLoop.LightletCensus).
        rtShadowSlotPixel[device.CurrentFrameSlot] = (halfShadowsNow, sub.X, sub.Y);
        if (rtShadowSize != (width, height))
        {
            // Sized with the window; a resize waits for the device like every other resize here.
            device.WaitIdle();
            foreach (var b in rtShadowOrigins) device.DestroyGpuBuffer(b);
            foreach (var b in rtShadowMasks) device.DestroyGpuBuffer(b);
            foreach (var b in rtShadowSky) device.DestroyGpuBuffer(b);
            foreach (var b in rtShadowDirections) device.DestroyGpuBuffer(b);
            foreach (var b in rtShadowBaked) device.DestroyGpuBuffer(b);
            foreach (var b in rtShadowPacked) device.DestroyGpuBuffer(b);
            foreach (var b in rtShadowPixelOf) device.DestroyGpuBuffer(b);
            foreach (var b in rtShadowCounters) device.DestroyGpuBuffer(b);
            foreach (var b in rtShadowDepths) device.DestroyGpuBuffer(b);
            rtShadowPrevSlot = -1;
            var slots = device.MaxFramesInFlightCount;
            var header = MemoryMarshal.AsBytes(new uint[] { (uint)width, (uint)height, 1u, RtSkyRays ? 1u : 0u }.AsSpan()).ToArray();
            rtShadowOrigins = Enumerable.Range(0, RtPerPixel ? slots : 0)
                .Select(s => Own(device.CreateGpuBuffer(width * height * 16, name: $"sponza.rt-shadow.origins{s}"))).ToArray();
            rtShadowMasks = Enumerable.Range(0, slots)
                .Select(s => Own(device.CreateGpuBuffer(16 + width * height * 4 * RtMaskPlanes, header, $"sponza.rt-shadow.mask{s}"))).ToArray();
            rtShadowBaked = Enumerable.Range(0, slots)
                .Select(s => Own(device.CreateGpuBuffer(width * height * 4, name: $"sponza.rt-shadow.baked{s}"))).ToArray();
            rtShadowDirections = Enumerable.Range(0, RtPerPixel ? slots : 0)
                .Select(s => Own(device.CreateGpuBuffer(width * height * 16, name: $"sponza.rt-shadow.directions{s}"))).ToArray();
            rtShadowSky = Enumerable.Range(0, RtSkyRays ? slots : 0)
                .Select(s => Own(device.CreateGpuBuffer(width * height * 32, name: $"sponza.rt-shadow.sky{s}"))).ToArray();
            // The packed rays and their pixels live from this frame's rays pass to its trace (joined inside the frame), and
            // the frames are chained on the GPU (VulkanGraphicsDevice.ChainFrames): one copy, not one a frame in flight --
            // 36 B a pixel twice over, ~340 MB at 2880x1620 (2026-10-24). --no-frame-chain: one a slot again.
            var rayCopies = device is Blix.Graphics.Vulkan.VulkanGraphicsDevice { ChainFrames: false } ? slots : 1;
            rtShadowPacked = Enumerable.Range(0, RtPack ? rayCopies : 0)
                .Select(s => Own(device.CreateGpuBuffer(width * height * 32, name: $"sponza.rt-shadow.packed{s}"))).ToArray();
            rtShadowPixelOf = Enumerable.Range(0, RtPack ? rayCopies : 0)
                .Select(s => Own(device.CreateGpuBuffer(width * height * 4, name: $"sponza.rt-shadow.pixel-of{s}"))).ToArray();
            rtShadowCounters = Enumerable.Range(0, RtPack ? slots : 0)
                .Select(s => Own(device.CreateGpuBuffer(16, new byte[16], $"sponza.rt-shadow.counters{s}"))).ToArray();
            rtShadowDepths = Enumerable.Range(0, RtPack ? slots : 0)
                .Select(s => Own(device.CreateGpuBuffer(width * height * 4, name: $"sponza.rt-shadow.depth{s}"))).ToArray();
            rtShadowSlotUses = new uint[slots];
            rtShadowSize = (width, height);
        }

        var slot = device.CurrentFrameSlot;
        // Per pixel only when something reads them (RtPerPixel); the shader writes none otherwise.
        var origins = rtShadowOrigins.Length > 0 ? rtShadowOrigins[slot] : RtShadowPlaceholder;
        var mask = rtShadowMasks[slot];
        // Packed (RtPack): this use of the slot counts into one of its two counters and zeroes the other for its next.
        var pack = rtShadowPacked.Length > 0;
        var counter = pack ? (int)(rtShadowSlotUses[slot]++ & 1u) : 0;
        GpuBufferHandle? sky = RtSkyRays ? rtShadowSky[slot] : null;
        var towardSun = -Vector3.Normalize(sunDirection);
        // The sun's disc (ShadowsSettings.SunDiscDegrees): filtered in space after the trace (PenumbraFilter, the rays toward
        // its centre), or each ray toward its own point of it (rt_shadow_rays.comp) and TAA averaging them.
        var filterPenumbra = shadows.PenumbraFilter && !halfShadowsNow && SunDiscTangent > 0f;
        var discTangent = filterPenumbra ? 0f : SunDiscTangent;
        var directions = rtShadowDirections.Length > 0 ? rtShadowDirections[slot] : RtShadowPlaceholder;
        // Interleaved: packed rays only, one per full-res pixel (no half res, no sky rays, no census reading every pixel's).
        var interleave = shadows.Interleave && pack && !halfShadowsNow && sky is null && !RtPerPixel;
        var historyGood = interleave && rtShadowPrevSlot >= 0 && rtShadowPrevSlot != slot && rtShadowPrevSun == towardSun;
        var phase = historyGood ? RtShadowPhases[rtShadowPhase++ & 3u] : 0u;
        var reach = rayReach;
        Matrix4x4.Invert(viewProjJittered, out var invViewProj);
        graph.Dispatch(rtShadowRaysPassHandle, new DispatchCommand(rtShadowRaysPipeline, (width + 7) / 8, (height + 7) / 8, 1,
            new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                new("uSize", new Vector4Uniform(new Vector4(width, height, 0f, 0f))),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                new("uSun", new Vector4Uniform(new Vector4(towardSun, reach))),
                new("uSky", new Vector4Uniform(new Vector4(sky is null ? 0f : 1f, rtShadowFrame % (1u << 24), halfLightingNow || lightletCensus || lightletRecord || shotPath is not null ? 1f : 0f, 0f))),
                new("uHalf", new Vector4Uniform(new Vector4(halfShadowsNow ? 1f : 0f, sub.X, sub.Y, 0f))),
                // The frame's number for the disc's sequence, kept small: a float's fraction holds at 2^16.
                new("uDisc", new Vector4Uniform(new Vector4(discTangent, rtShadowFrame % 65536u, 0f, 0f))),
                new("uLightlet", new Vector4Uniform(LightletUniform(towardSun))),
                new("uLightmap", new Vector4Uniform(LightmapUniform)),
                new("uCompact", new Vector4Uniform(new Vector4(pack ? 1f : 0f, counter, rtShadowOrigins.Length > 0 ? 1f : 0f, historyGood ? 1f + phase : 0f))),
                new("uClear", new Vector4Uniform(new Vector4(GatherClearOn && texelsUploaded ? 1f : 0f, 0f, 0f, 0f))),
            },
            new[]
            {
                new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                LightmapTexelTexture(),
            },
            // Without sky rays the shader writes none, and the placeholder only fills the binding.
            Buffers: new[]
            {
                AtlasIndexBuffer(),
                new ShaderBufferBinding("RtShadowOrigins", origins), new ShaderBufferBinding("RtShadowSky", sky ?? RtShadowPlaceholder),
                new ShaderBufferBinding("RtShadowDirections", directions),
                new ShaderBufferBinding("RtShadowBaked", rtShadowBaked[slot]),
                new ShaderBufferBinding("Texels", texelsUploaded ? texelBuffer : RtShadowPlaceholder),
                new ShaderBufferBinding("LightletIndex", lightlets is null ? LightletPlaceholder : lightletIndexBuffer),
                new ShaderBufferBinding("LightletPool", lightlets is null ? LightletPlaceholder : lightletPoolBuffer),
                new ShaderBufferBinding("RtShadowPacked", pack ? rtShadowPacked[slot % rtShadowPacked.Length] : RtShadowPlaceholder),
                new ShaderBufferBinding("RtShadowPixelOf", pack ? rtShadowPixelOf[slot % rtShadowPixelOf.Length] : RtShadowPlaceholder),
                new ShaderBufferBinding("RtShadowCounters", pack ? rtShadowCounters[slot] : RtShadowPlaceholder),
                new ShaderBufferBinding("RtShadowMaskWords", mask),
                new ShaderBufferBinding("TexelClear", GatherClearOn && texelsUploaded ? texelClearBuffer : RtShadowPlaceholder),
            }));

        // A new coin per frame for the partly covered (foliage), so TAA averages their shadows instead of freezing one draw.
        var tracer = frameTracer!;
        var count = width * height;
        var kind = rtCoverageCoin ? 1 : rtAlphaAb ? 1 + (int)(rtShadowFrame % 2) : 2;
        var seed = unchecked(rtShadowFrame++ * 0x9E3779B9u);
        GpuBufferHandle? perRay = discTangent > 0f ? directions : null;
        // Through sheets that transmit (the curtains): their light on the points behind them, past the words (RtMaskPlanes).
        uint? throughAt = rtSheetRows > 0 ? (uint)count : null;
        if (pack)
        {
            var packed = rtShadowPacked[slot % rtShadowPacked.Length];
            var pixelOf = rtShadowPixelOf[slot % rtShadowPixelOf.Length];
            var counters = rtShadowCounters[slot];
            graph.External(rtShadowTracePassHandle, sync => tracer.EncodeGroup(sync, () =>
            {
                EncodeDeferredBatches(sync, tracer);
                tracer.EncodeShadow(sync, packed, mask, 16, count, towardSun, seed, null,
                    alphaTest: kind == 2, statSlot: kind, limit: counters, limitOffset: counter * 4, pixelOf: pixelOf, throughAt: throughAt);
            }));
        }
        else graph.External(rtShadowTracePassHandle, sync => tracer.EncodeGroup(sync, () =>
        {
            EncodeDeferredBatches(sync, tracer);
            tracer.EncodeShadow(sync, origins, mask, 16, count, towardSun, seed, sky,
                alphaTest: kind == 2, statSlot: kind, directions: perRay, throughAt: throughAt);
        }));
        // The frame's deferred batches go in this group (SponzaLoop.TracerGroup.cs).
        tracerGroupRecorded = ReferenceEquals(tracer, frameTracer);
        rtShadowFrames++;
        if (interleave)
        {
            var prev = historyGood ? rtShadowPrevSlot : slot;
            graph.Dispatch(rtShadowFillPassHandle, new DispatchCommand(rtShadowFillPipeline, (width + 7) / 8, (height + 7) / 8, 1,
                new ShaderUniform[]
                {
                    new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                    new("uPrevViewProj", new Matrix4x4Uniform(historyGood ? rtShadowPrevViewProj : viewProjJittered)),
                    new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                    new("uSize", new Vector4Uniform(new Vector4(width, height, 0f, 0f))),
                    new("uFill", new Vector4Uniform(new Vector4(phase, historyGood ? 1f : 0f, historyGood ? 0f : 1f, 0f))),
                },
                new[]
                {
                    new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                    new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                },
                Buffers: new[]
                {
                    new ShaderBufferBinding("RtShadowMask", mask),
                    new ShaderBufferBinding("RtShadowPrevMask", rtShadowMasks[prev]),
                    new ShaderBufferBinding("RtShadowDepth", rtShadowDepths[slot]),
                    new ShaderBufferBinding("RtShadowPrevDepth", rtShadowDepths[prev]),
                }));
            rtShadowPrevSlot = slot;
            rtShadowPrevViewProj = viewProjJittered;
            rtShadowPrevSun = towardSun;
        }
        else rtShadowPrevSlot = -1;
        if (filterPenumbra)
        {
            var pixelAngle = 2f / (MathF.Max(MathF.Abs(cameraProjection.M22), 1e-6f) * Math.Max(height, 1));
            graph.Dispatch(rtShadowFilterPassHandle, new DispatchCommand(rtShadowFilterPipeline, (width + 15) / 16, (height + 15) / 16, 1,
                new ShaderUniform[]
                {
                    new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                    new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                    new("uSize", new Vector4Uniform(new Vector4(width, height, 0f, 0f))),
                    new("uDisc", new Vector4Uniform(new Vector4(SunDiscTangent, pixelAngle, 0f, 0f))),
                },
                new[]
                {
                    new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
                    new ShaderTextureBinding("uPrepassNormal", graph.GetColorTexture(SampleablePrepassNormal)),
                },
                Buffers: new[] { new ShaderBufferBinding("RtShadowMask", mask) }));
        }

        rtShadowBound = mask;
        litClipmapBuffer = null;
        litSceneBuffers = null;
    }

    // Once, a few frames after the surface bake was recorded (so it has run): coverage read back, structures built.
    // A stall of a second or two, once, at load.
    private bool EnsureFrameTracer()
    {
        if (frameTracer is not null) return true;
        if (frameTracerFailed || !RaySurfacesReady) return false;
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
            RestoreRayTraversal("no Metal tracer");
            return false;
        }
        var surfaceWords = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(raySurfaces, 0, rayGpuData!.RowPlacements.Length * 4).AsSpan()).ToArray();
        device.WaitIdle();
        var blocks = RayQueryGpuData.BlockNames.Select(name => rayBlockBuffers.First(b => b.Name == name).Buffer).ToArray();
        var (alphaMaterials, rowMaterial) = rtCoverageCoin ? (new List<MetalRayTracer.AlphaMaterial>(), Array.Empty<uint>()) : RtAlphaMaterials();
        var alphaTested = rowMaterial.Select(m => m != uint.MaxValue).ToArray();
        // On its own queue unless --metal-device-queue (above); the bake scene's tracer shares this one either way
        // (SponzaLoop.BakeScene.cs).
        if (metalDeviceQueue) tracer.UseDeviceQueue();
        tracerOnDeviceQueue = metalDeviceQueue;
        // Sheets that transmit (KHR_materials_diffuse_transmission, --cloth-transmission: the curtains): the sun's shadow
        // rays go through them (MetalRayTracer.SetTransmit), so they are built non-opaque with the alpha-tested.
        var rowTransmit = RtRowTransmit(surfaceWords, out rtSheetRows);
        if (rtSheetRows > 0)
        {
            if (alphaTested.Length == 0) alphaTested = new bool[rowTransmit.Length];
            for (var row = 0; row < rowTransmit.Length; row++) if (rowTransmit[row] != 0u) alphaTested[row] = true;
        }
        rtBuild = tracer.Build(rayGpuData, blocks, surfaceWords, alphaTested);
        if (!rtCoverageCoin) tracer.SetAlpha(alphaMaterials, rowMaterial, raySurfaceBakeBuffers.First(b => b.Name == "BlixRayUvs").Buffer, rtAlphaMip);
        if (rtSheetRows > 0) tracer.SetTransmit(rowTransmit);
        frameTracer = tracer;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] metal tracer: {rtBuild.Value.SolidTriangles + rtBuild.Value.PartialTriangles:N0} triangles ({rtBuild.Value.PartialTriangles:N0} partly covered, {(rtCoverageCoin ? "by the coverage coin" : string.Create(Inv, $"alpha-tested by {alphaMaterials.Count} materials' textures"))}) in {rtBuild.Value.Hierarchies} bottom-level structures, built in {rtBuild.Value.CpuPrepareMs + rtBuild.Value.BlasGpuMs + rtBuild.Value.CompactTlasGpuMs:0} ms; {rtBuild.Value.CompactedBytes / 1048576.0:0} MB."));
        return true;
    }

    // Per triangle row, its placement's diffuse transmission as rgb8 (MetalRayTracer.SetTransmit; 0: none): the factor in
    // the cloth's own hue, one colour a sheet -- its rows' mean albedo (the surface bake's, sRGB in the words' low bytes)
    // over its brightest channel. Per triangle, the albedo printed the fabric's pattern into the light behind it, a
    // stained-glass copy of the cloth (2026-10-15); what lands behind a sheet is its glow summed over a curtain's width.
    private uint[] RtRowTransmit(uint[] surfaceWords, out int sheetRows)
    {
        var data = rayGpuData!;
        var rows = new uint[data.RowPlacements.Length];
        sheetRows = 0;
        var sums = new Dictionary<int, (Vector3 Sum, int Count)>();
        for (var row = 0; row < rows.Length; row++)
        {
            var placement = (int)data.RowPlacements[row];
            var t = rayPlacementTransmission[placement];
            if (t.X <= 0f && t.Y <= 0f && t.Z <= 0f) continue;
            static float Linear(uint b) { var c = b / 255f; return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f); }
            var w = surfaceWords[row];
            var a = new Vector3(Linear(w & 255u), Linear((w >> 8) & 255u), Linear((w >> 16) & 255u));
            sums[placement] = sums.TryGetValue(placement, out var v) ? (v.Sum + a, v.Count + 1) : (a, 1);
        }
        var doubleSidedRows = 0;
        for (var row = 0; row < rows.Length; row++)
        {
            var placement = (int)data.RowPlacements[row];
            if (!sums.TryGetValue(placement, out var v)) continue;
            var mean = v.Sum / v.Count;
            var top = MathF.Max(MathF.Max(mean.X, MathF.Max(mean.Y, mean.Z)), 1e-3f);
            var hue = mean / top;
            // A curtain (--cloth-transmission): its albedo's hue, capped at what its lit face reflects, as lit.frag caps it.
            // An authored material: T c as the spec has it, c its colour texture's mean over the row (lit.frag, dtColor) --
            // the cloth's rule on DiffuseTransmissionTest's panels left its T 1 panel passing nothing (cap 1 - T = 0).
            var factor = MathF.Max(rayPlacementTransmission[placement].X, MathF.Max(rayPlacementTransmission[placement].Y, rayPlacementTransmission[placement].Z));
            var t = rayPlacementCloth[placement]
                ? rayPlacementTransmission[placement] * hue * MathF.Min(1f, (1f - factor) * top / MathF.Max(factor, 1e-4f)) * RowTransmissionShare(row)
                : rayPlacementTransmission[placement] * RowTransmissionColour(row) * RowTransmissionShare(row);
            uint Byte(float x) => (uint)Math.Clamp(MathF.Round(x * 255f), 0f, 255f);
            // The top byte: a double-sided sheet counts every crossing, a closed one the faces a ray enters by.
            rows[row] = Math.Max(1u, Byte(t.X) | Byte(t.Y) << 8 | Byte(t.Z) << 16) | (rayPlacementDoubleSided[placement] ? 1u << 24 : 0u);
            sheetRows++;
            if (rayPlacementDoubleSided[placement]) doubleSidedRows++;
        }
        if (sheetRows > 0)
            Console.WriteLine(string.Create(Inv, $"[VulkanSponza] metal tracer: {sheetRows:N0} triangles of {sums.Count} sheets that transmit ({doubleSidedRows:N0} double-sided) -- the sun's shadow rays go through them, and the sunlit sheet's glow lights what is behind."));
        return rows;
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

    private void WriteRtShadows(string basePath)
    {
        if (frameTracer is null || !rtShadows || rtShadowMasks.Length == 0) return;   // --no-shadow: never traced
        var rays = (long)rtShadowSize.Width * rtShadowSize.Height;
        // And what the lit pass was told: how much of the frame the sun reaches.
        var mask = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtShadowMasks[device.CurrentFrameSlot], 16, (int)rays * 4).AsSpan());
        long blocked = 0;
        foreach (var word in mask) if ((word & 1u) != 0) blocked++;
        long skyBlocked = 0;
        foreach (var word in mask) if ((word & 2u) != 0) skyBlocked++;
        // The mask itself beside the shot (white: the sun reaches; black: blocked), pixel for pixel with the image.
        var rgba = new byte[rays * 4];
        for (var i = 0; i < rays; i++)
        {
            var lit = (mask[i] & 1u) == 0 ? (byte)255 : (byte)0;
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = lit;
            rgba[i * 4 + 3] = 255;
        }
        PngWriter.WriteRgba8(basePath + ".rtmask.png", rgba, rtShadowSize.Width, rtShadowSize.Height);
        if (rtShadowBaked.Length > 0)
        {
            // What the lightlets answered (rt_shadow_rays.comp): baked lit, baked shadowed, traced.
            var baked = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtShadowBaked[device.CurrentFrameSlot], 0, (int)rays * 4).AsSpan());
            var by = new long[5];
            foreach (var b in baked) by[Math.Min(b, 4u)]++;
            double Pc(long v) => 100.0 * v / rays;
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] lightlets, last frame: baked lit {Pc(by[0]):0.0}%, baked shadowed {Pc(by[1]):0.0}%; traced {Pc(by[2] + by[3] + by[4]):0.0}% -- uncertain {Pc(by[2]):0.0}%, no atlas texel {Pc(by[3]):0.0}%, texel missing or buried {Pc(by[4]):0.0}% -- of {rays:N0} pixels (sun records {(render.LightletSun && lightlets is not null ? "on" : "off")})."));
        }
        if (rtShadowCounters.Length > 0)
        {
            var c = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtShadowCounters[device.CurrentFrameSlot], 0, 16).AsSpan()).ToArray();
            var packed = Math.Max(c[0], c[1]);
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] rt shadows, last frame: {packed:N0} rays packed ({100.0 * packed / rays:0.0}% of {rays:N0} pixels); of them facing away from the sun (sheets, foliage) {c[3]:N0}."));
        }
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
