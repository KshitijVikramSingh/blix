using System.Numerics;
using System.Runtime.InteropServices;

namespace Blix.Demos.VulkanSponza;

// The texels' records on the GPU, packed (texel_record.glsl, which every shader reads them through): 16 B a record after
// a 32 B header, where the CPU's two Vector4s a texel (position, weight; normal, its atlas coordinate or level) took 32.
// Sponza's 17.4 M texels: 557 -> 278 MB (2026-10-24). The CPU keeps its Vector4s; what it reads back is decoded here.
//   header  [0] the positions' origin (xyz) and step (w), floats; [1].x the first atlas record (uint.MaxValue: none)
//   record  x  position x, 20 bits | z's low 12 << 20
//           y  position y, 20 bits | z's high 8 << 20 | normal w bits 0-3 << 28
//           z  normal, octahedral, 12 bits a component (x 0, y 4095: none) | normal w bits 4-11 << 24
//           w  position w, half | normal w bits 12-27 << 16
//   normal w: an atlas record's coordinate x | y << 14 (each under 16,384), a hashed texel's level
internal sealed partial class SponzaLoop
{
    private const int TexelRecordHeaderBytes = 32;
    private const uint TexelPositionSteps = (1u << 20) - 1u;

    private static uint[] PackTexelRecords(Vector4[] texels, int count, int firstAtlas)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        for (var i = 0; i < count; i++)
        {
            var p = new Vector3(texels[2 * i].X, texels[2 * i].Y, texels[2 * i].Z);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        if (count == 0) min = max = Vector3.Zero;
        var extent = max - min;
        var step = MathF.Max(MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) / TexelPositionSteps, 1e-9f);
        var words = new uint[8 + 4 * count];
        words[0] = BitConverter.SingleToUInt32Bits(min.X);
        words[1] = BitConverter.SingleToUInt32Bits(min.Y);
        words[2] = BitConverter.SingleToUInt32Bits(min.Z);
        words[3] = BitConverter.SingleToUInt32Bits(step);
        words[4] = firstAtlas >= 0 ? (uint)firstAtlas : uint.MaxValue;
        Parallel.For(0, count, i =>
        {
            var pw = texels[2 * i];
            var nw = texels[2 * i + 1];
            uint Q(float v, float o) => (uint)Math.Clamp(MathF.Round((v - o) / step), 0f, TexelPositionSteps);
            uint qx = Q(pw.X, min.X), qy = Q(pw.Y, min.Y), qz = Q(pw.Z, min.Z);
            uint w28;
            if (firstAtlas >= 0 && i >= firstAtlas)
            {
                var bits = BitConverter.SingleToUInt32Bits(nw.W);
                uint x = bits & 0xFFFFu, y = bits >> 16;
                if (x >= 1u << 14 || y >= 1u << 14) throw new InvalidOperationException($"texel record {i}: atlas coordinate {x},{y} past 16,384");
                w28 = x | (y << 14);
            }
            else w28 = (uint)Math.Clamp((int)MathF.Round(nw.W), 0, (1 << 28) - 1);
            var o = 4 * i + 8;
            words[o] = qx | ((qz & 0xFFFu) << 20);
            words[o + 1] = qy | ((qz >> 12) << 20) | ((w28 & 0xFu) << 28);
            words[o + 2] = OctNormal12(new Vector3(nw.X, nw.Y, nw.Z)) | (((w28 >> 4) & 0xFFu) << 24);
            words[o + 3] = BitConverter.HalfToUInt16Bits((Half)pw.W) | ((w28 >> 12) << 16);
        });
        return words;
    }

    private static uint OctNormal12(Vector3 n)
    {
        var len = n.Length();
        if (len < 1e-6f) return 0u | (4095u << 12);   // none
        n /= MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
        var e = new Vector2(n.X, n.Y);
        if (n.Z < 0f) e = new Vector2((1f - MathF.Abs(n.Y)) * (n.X >= 0f ? 1f : -1f), (1f - MathF.Abs(n.X)) * (n.Y >= 0f ? 1f : -1f));
        uint U(float v) => (uint)Math.Clamp(MathF.Round((v * 0.5f + 0.5f) * 4095f), 0f, 4095f);
        uint ux = U(e.X), uy = U(e.Y);
        if (ux == 0u && uy == 4095u) ux = 1u;   // a real normal off the "none" code
        return ux | (uy << 12);
    }

    // Records first .. first + count back as the CPU's Vector4 pairs (texel_record.glsl's decode).
    private Vector4[] ReadTexelRecords(int first, int count)
    {
        var header = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(texelBuffer, 0, TexelRecordHeaderBytes).AsSpan()).ToArray();
        var words = count == 0 ? Array.Empty<uint>()
            : MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(texelBuffer, checked(TexelRecordHeaderBytes + first * 16), checked(count * 16)).AsSpan()).ToArray();
        return DecodeTexelRecords(header, words, first, count);
    }

    private static Vector4[] DecodeTexelRecords(uint[] header, uint[] words, int first, int count)
    {
        var origin = new Vector3(BitConverter.UInt32BitsToSingle(header[0]), BitConverter.UInt32BitsToSingle(header[1]), BitConverter.UInt32BitsToSingle(header[2]));
        var step = BitConverter.UInt32BitsToSingle(header[3]);
        var firstAtlas = header[4];
        var texels = new Vector4[2 * count];
        Parallel.For(0, count, k =>
        {
            uint x = words[4 * k], y = words[4 * k + 1], z = words[4 * k + 2], w = words[4 * k + 3];
            var q = new Vector3(x & 0xFFFFFu, y & 0xFFFFFu, (x >> 20) | (((y >> 20) & 0xFFu) << 12));
            texels[2 * k] = new Vector4(origin + q * step, (float)BitConverter.UInt16BitsToHalf((ushort)(w & 0xFFFFu)));
            var w28 = (y >> 28) | ((z >> 24) << 4) | ((w >> 16) << 12);
            var nw = (uint)(first + k) >= firstAtlas ? BitConverter.UInt32BitsToSingle((w28 & 0x3FFFu) | ((w28 >> 14) << 16)) : w28;
            uint ox = z & 0xFFFu, oy = (z >> 12) & 0xFFFu;
            var n = Vector3.Zero;
            if (!(ox == 0u && oy == 4095u))
            {
                var e = new Vector2(ox / 4095f * 2f - 1f, oy / 4095f * 2f - 1f);
                n = new Vector3(e.X, e.Y, 1f - MathF.Abs(e.X) - MathF.Abs(e.Y));
                if (n.Z < 0f) n = new Vector3((1f - MathF.Abs(n.Y)) * (n.X >= 0f ? 1f : -1f), (1f - MathF.Abs(n.X)) * (n.Y >= 0f ? 1f : -1f), n.Z);
                n = Vector3.Normalize(n);
            }
            texels[2 * k + 1] = new Vector4(n, nw);
        });
        return texels;
    }
}
