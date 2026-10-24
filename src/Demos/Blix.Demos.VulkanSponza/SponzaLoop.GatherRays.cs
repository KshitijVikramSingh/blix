using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Diagnostics;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The texel picker's gather rays (--lean-shade): the picked pixel's heaviest texel gathered again now, its rays captured
// (texel_nodes_lean.comp's one-texel capture: direction, light, the hit's distance, what the hit read) and drawn in the
// "gather rays" layer -- each ray from the texel to its end, by what answered it: blue the sky, green its cell, yellow a
// near texel (a thin orange line to that texel), red borrowed from the texel's other hits; brighter the more it brought.
// Under --cone-lookups each hit's cone footprint as a small sphere. BLIX_GATHER_RAYS_TEXEL=id does the same once the
// light rests, printing what it caught (a check without the picker).
internal sealed partial class SponzaLoop
{
    private const int GatherRaysPasses = 8;   // 256 rays
    private bool gatherRaysPending;
    private uint gatherRaysTexel;
    private string? gatherRaysNote;
    private readonly List<(Vector3 From, Vector3 To, int Kind, float Light, Vector3? Near, float Footprint)> gatherRays = new();
    private static readonly uint? gatherRaysEnvTexel = uint.TryParse(Environment.GetEnvironmentVariable("BLIX_GATHER_RAYS_TEXEL"), out var gt) ? gt : null;
    private bool gatherRaysEnvDone;

    private void RunGatherRaysIfPending()
    {
        if (gatherRaysEnvTexel is { } envId && !gatherRaysEnvDone && texelsRestReported && !splitCooking)
        {
            gatherRaysEnvDone = true;
            gatherRaysTexel = envId;
            gatherRaysPending = true;
        }
        if (!gatherRaysPending) return;
        gatherRaysPending = false;
        if (!leanShade) { gatherRaysNote = "gather rays: boot with --lean-shade (its capture draws them)."; return; }
        if (splitCooking) { gatherRaysNote = "gather rays: the cook is still running; once the light rests."; return; }
        if (sunNodeRaysBuffer.Equals(default(GpuBufferHandle)) || sunNodeHits.Equals(default(GpuBufferHandle)) || frameTracer is null)
        { gatherRaysNote = "gather rays: the gather's buffers are gone (released after the cook)."; return; }
        var savedBits = rayCaptureBits; var savedTexel = rayCaptureTexel;
        rayCaptureBits = 0; rayCaptureTexel = (int)gatherRaysTexel;
        GpuBufferHandle ids = default;
        try
        {
            RayCaptureBinding(sunNodeAccum);   // made if it was not
            device.WriteGpuBuffer(rayCaptureBuffer, 0, new byte[16]);
            ids = device.CreateGpuBuffer(16, MemoryMarshal.AsBytes(new[] { gatherRaysTexel, 0u, 0u, 0u }.AsSpan()), "sponza.gather-rays.ids");
            for (var p = 0; p < GatherRaysPasses; p++) SunNodePassNow(ids, 0, 1, p, 7001 + p);
            device.WaitIdle();
            var count = (int)Math.Min(BitConverter.ToUInt32(device.ReadGpuBuffer(rayCaptureBuffer, 0, 4)), (uint)rayCaptureRoom);
            var words = count > 0 ? MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rayCaptureBuffer, 16, count * 32).AsSpan()).ToArray() : Array.Empty<uint>();
            var tex = ReadTexelRecords((int)gatherRaysTexel, 1);
            var n0 = Vector3.Normalize(new Vector3(tex[1].X, tex[1].Y, tex[1].Z));
            var origin = new Vector3(tex[0].X, tex[0].Y, tex[0].Z) + n0 * 0.01f;
            gatherRays.Clear();
            var kinds = new int[4];
            double total = 0;
            for (var i = 0; i < count; i++)
            {
                var w = words.AsSpan(i * 8, 8);
                var o = new Vector2((short)(w[1] & 0xFFFF) / 32767f, (short)(w[1] >> 16) / 32767f);
                var z = 1f - MathF.Abs(o.X) - MathF.Abs(o.Y);
                var d = z >= 0f ? new Vector3(o.X, o.Y, z) : new Vector3((1f - MathF.Abs(o.Y)) * MathF.Sign(o.X), (1f - MathF.Abs(o.X)) * MathF.Sign(o.Y), z);
                d = Vector3.Normalize(d);
                var l = new Vector3(BitConverter.UInt32BitsToSingle(w[2]), BitConverter.UInt32BitsToSingle(w[3]), BitConverter.UInt32BitsToSingle(w[4]));
                var c0 = BitConverter.UInt32BitsToSingle(w[5]);
                var light = (0.2126f * l.X + 0.7152f * l.Y + 0.0722f * l.Z) * MathF.Max(c0, 0f);
                var kind = (int)((w[6] >> 8) & 0xFF);
                var dist = (float)BitConverter.UInt16BitsToHalf((ushort)(w[6] >> 16));
                var end = origin + d * MathF.Min(dist, kind == 0 ? 1.5f : 30f);
                Vector3? near = null;
                if (kind == 2 && w[7] != 0xFFFFFFFFu && w[7] < (uint)texelCount)
                {
                    var t = ReadTexelRecords((int)w[7], 1)[0];
                    near = new Vector3(t.X, t.Y, t.Z);
                }
                // The cone a ray stands for (--cone-lookups): its share of the hemisphere's solid angle, its radius at the hit.
                var omega = MathF.PI / (count * MathF.Max(c0, 1e-3f));
                var footprint = kind == 0 ? 0f : MathF.Min(dist, 60f) * MathF.Sqrt(omega / MathF.PI);
                gatherRays.Add((origin, end, Math.Clamp(kind, 0, 3), light, near, footprint));
                kinds[Math.Clamp(kind, 0, 3)]++;
                total += light;
            }
            gatherRaysNote = string.Create(CultureInfo.InvariantCulture,
                $"gather rays of texel {gatherRaysTexel}: {count} rays -- sky {kinds[0]}, cell {kinds[1]}, near texel {kinds[2]}, borrowed {kinds[3]}; their light {total / Math.Max(1, count):0.0000}.");
            Console.WriteLine("[VulkanSponza] " + gatherRaysNote);
        }
        finally
        {
            rayCaptureBits = savedBits; rayCaptureTexel = savedTexel;
            if (!ids.Equals(default(GpuBufferHandle))) device.DestroyGpuBuffer(ids);
        }
    }

    private void DrawGatherRays(DebugContext debug)
    {
        if (gatherRays.Count == 0 || !debug.Draw.Layer("gather rays", visible: true)) return;
        var max = MathF.Max(gatherRays.Max(r => r.Light), 1e-9f);
        var tints = new[] { new Vector3(0.35f, 0.6f, 1f), new Vector3(0.3f, 1f, 0.4f), new Vector3(1f, 0.9f, 0.25f), new Vector3(1f, 0.3f, 0.3f) };
        for (var i = 0; i < gatherRays.Count; i++)
        {
            var r = gatherRays[i];
            var k = 0.25f + 0.75f * MathF.Sqrt(r.Light / max);
            var c = tints[r.Kind] * k;
            debug.Draw.Line($"gather/{i}", r.From, r.To, new GraphicsColor(c.X, c.Y, c.Z, 0.9f));
            if (r.Near is { } near) debug.Draw.Line($"gather/near/{i}", r.To, near, new GraphicsColor(1f, 0.55f, 0.15f, 0.6f));
            if (coneLookups && r.Footprint > 0.02f) debug.Draw.Sphere($"gather/cone/{i}", r.To, r.Footprint, new GraphicsColor(c.X, c.Y, c.Z, 0.35f), 8);
        }
    }
}
