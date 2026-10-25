using System.Globalization;
using System.Numerics;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --ray-capture B (a measurement): every gather ray of 1 in 2^B texels (a hash of the id), as it was answered
// (texel_nodes_shade.glsl, RayCapture): its texel, round and pass, hit distance, how its light was found, the light it
// brought and the bounce's share. Contacts -- creases between bounce-lit surfaces -- take the refine's rays and still
// lead the gate's error; the capture says which rays carry their noise (near or far, sun or bounce, which answer), so a
// ray budget can be aimed by data (tools/gi-model/ray_capture.py). The view dump writes the records (rays_captured.u32x8);
// --ray-capture-room N records at most (4 M by default, 32 bytes each).
internal sealed partial class SponzaLoop
{
    private int rayCaptureBits = -1;
    // --ray-capture-texel ID: every gather ray of the one texel instead (a crease point's, say).
    private int rayCaptureTexel = -1;
    private int rayCaptureRoom = 1 << 22;
    private GpuBufferHandle rayCaptureBuffer;

    private void ReadRayCaptureArgs(AppArgs args)
    {
        rayCaptureBits = args.Int("ray-capture") ?? -1;
        rayCaptureRoom = args.Int("ray-capture-room") ?? rayCaptureRoom;
        rayCaptureTexel = args.Int("ray-capture-texel") ?? -1;
        rayCaptureSun = args.Flag("ray-capture-sun");
        if (rayCaptureTexel >= 0 && rayCaptureBits < 0) rayCaptureBits = 0;
        if (rayCaptureBits >= 0)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] ray capture: every gather ray of 1 in {1L << rayCaptureBits:N0} texels, up to {rayCaptureRoom:N0} records."));
    }

    private bool RayCaptureOn => rayCaptureBits >= 0;

    // The round a ray was shot in: the stage's provenance code (1 sweep, 2 refine, 3 fine, 4 visible-changed), the refine
    // round << 4, the near round << 8.
    private float RayCaptureTag => GatherProvenanceCode + 16f * sunNodeRefineRound + 256f * (fineGathering ? NearRoundsNow - nearRoundsLeft : 0)
        + (rayCaptureSun ? 32768f : 0f) + (captureDir ? 16384f : 0f);
    // BLIX_CAPTURE_DIR=1: the records' word 6 the ray's direction (octahedral, snorm 2 x 16), not cosine and weight
    // (tools/gi-model/far_record.py: the far record per patch and bin, from the sweep's own rays).
    private static readonly bool captureDir = Environment.GetEnvironmentVariable("BLIX_CAPTURE_DIR") == "1";
    // --ray-capture-sun: the records' word 6 is the hit's whole sun and its texels' bit blend (half2), not cosine and weight.
    private bool rayCaptureSun;

    private static readonly bool captureFineOnly = Environment.GetEnvironmentVariable("BLIX_CAPTURE_FINE_ONLY") == "1";
    private static readonly bool captureDist = Environment.GetEnvironmentVariable("BLIX_CAPTURE_DIST") == "1";
    private Vector4 RayCaptureUniform => listCook ? (ListRecording ? new Vector4(2f, 0f, RayCaptureTag, rayCaptureRoom) : Vector4.Zero)
        // BLIX_CAPTURE_FINE_ONLY: the fine round's first pass alone, in either shade (the full shade took the sweep's too and
        // its room filled before the fine round, 2026-10-25).
        : RayCaptureOn && (!captureFineOnly || fineGathering && NearRoundsNow == nearRoundsLeft)
        ? new Vector4(1f, rayCaptureTexel >= 0 ? -(rayCaptureTexel + 1f) : rayCaptureBits, RayCaptureTag, rayCaptureRoom) : Vector4.Zero;

    private ShaderBufferBinding RayCaptureBinding(GpuBufferHandle stand)
    {
        if (!RayCaptureOn) return new ShaderBufferBinding("RayCapture", stand);
        if (rayCaptureBuffer.Equals(default(GpuBufferHandle)))
            rayCaptureBuffer = Own(device.CreateGpuBuffer(16 + rayCaptureRoom * 32, new byte[16 + rayCaptureRoom * 32], "sponza.ray-capture"));
        return new ShaderBufferBinding("RayCapture", rayCaptureBuffer);
    }

    // The view dump's: the count, then the records it holds.
    private (GpuBufferHandle Buffer, long Bytes) RayCaptureForDump()
    {
        if (!RayCaptureOn || rayCaptureBuffer.Equals(default(GpuBufferHandle))) return (default, 0);
        var count = BitConverter.ToUInt32(device.ReadGpuBuffer(rayCaptureBuffer, 0, 4));
        if (count > rayCaptureRoom)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] ray capture: {count:N0} rays met, {rayCaptureRoom:N0} kept (the room)."));
        return (rayCaptureBuffer, 16 + (long)Math.Min(count, (uint)rayCaptureRoom) * 32);
    }
}
