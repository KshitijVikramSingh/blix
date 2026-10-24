using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The sun-linear record (texel_nodes_shade.glsl, uSunW): per texel, its gather's lit hits' sun over their cosine times the
// hit's normal (luminance, per unit of irradiance, a pass's mean over its rays, summed over its passes: w). The sun its
// lit hits bring at a sun s is then W.s / passes -- linear in s to 0.00% on Crytek Sponza (tools/gi-model/sun_flip_push.py),
// so a small sun step's change over the hits that stay lit needs no rays: the moving sun's relight takes it from here, and
// the hits that crossed a shadow edge from the push (SponzaLoop.FlipPush.cs). On while the sun moves (--sun-move, M) or
// with --sun-linear; 16 bytes a texel.
internal sealed partial class SponzaLoop
{
    private bool sunLinearForced;
    private GpuBufferHandle texelSunWBuffer;
    private bool SunLinearOn => (sunLinearForced || sunMoving) && liveTexels;

    private void ParseSunLinear(AppArgs args) => sunLinearForced = args.Flag("sun-linear");

    private Vector4 SunLinearUniform => new(SunLinearOn ? 1f : 0f, 0f, 0f, 0f);

    // The record, made once (zeros: no texel has one until a gather writes it).
    private GpuBufferHandle SunLinearBuffer(GpuBufferHandle stand)
    {
        if (!SunLinearOn || texelCount == 0) return stand;
        if (texelSunWBuffer.Equals(default(GpuBufferHandle)))
            texelSunWBuffer = Own(device.CreateGpuBuffer(checked(texelCount * 16), name: "sponza.texels.sun-w"));
        return texelSunWBuffer;
    }

    // BLIX_SUN_LINEAR_CHECK=1 (a measurement), at a landing: the record against the sun part it was gathered with -- W.s
    // is the lit hits' share, the rest the bounce: its share over the texels that have one.
    private static readonly bool sunLinearCheck = Environment.GetEnvironmentVariable("BLIX_SUN_LINEAR_CHECK") == "1";

    private void SunLinearReport()
    {
        if (!sunLinearCheck || texelSunWBuffer.Equals(default(GpuBufferHandle)) || texelLiveIds is not { } live) return;
        device.WaitIdle();
        var w = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelSunWBuffer, 0, texelCount * 16).AsSpan()).ToArray();
        var sun = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(EnsureLivePart(true), 0, texelCount * 8).AsSpan()).ToArray();
        var toSun = GiToSun;
        long with = 0, over = 0;
        var shares = new List<float>();
        foreach (var id in live)
        {
            var rec = w[id];
            if (rec.W <= 0f) continue;
            with++;
            var lit = Vector3.Dot(new Vector3(rec.X, rec.Y, rec.Z), toSun) / rec.W;
            var part = Luminance(UnpackRgb9e5(sun[2 * id]));
            if (part <= 1e-9f) continue;
            var share = lit / part;
            if (share > 1.05f) over++;
            if (shares.Count < 200_000) shares.Add(share);
        }
        shares.Sort();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] sun-linear record: {with:N0} of {live.Length:N0} live texels have one; the lit hits' share of the sun part (W.s over it) median {(shares.Count > 0 ? shares[shares.Count / 2] : 0f):0.000}, p10 {(shares.Count > 0 ? shares[shares.Count / 10] : 0f):0.000}, p90 {(shares.Count > 0 ? shares[shares.Count * 9 / 10] : 0f):0.000}; over 1.05 {over:N0}."));
    }
}
