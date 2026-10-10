using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;

namespace Blix.Demos.VulkanSponza;

// --transfer-dump <file> (with --lightmap --transport-texels, a fixed --cam and --sun): the data for the surface transfer
// atlas's first experiment (plan.md, stage 5): can a texel's light be written as a non-negative blend of its nearby
// probes', with weights that hold when the sun moves? This bakes a deterministic sample of the atlas texels inside
// clipmap level 0 around the camera (BeginLightBake; nothing saved), lets the probes settle, and writes per texel its
// traced irradiance and, for each of the 8 level-0 probes around its lookup point, that probe's irradiance at the
// texel's normal and today's weight factors (trilinear, facing, Chebyshev: probe_clipmap.glsl, mirrored here). Run it
// once per sun; tools/transfer/fit.py fits weights on all suns but one and judges the one left out.
internal sealed partial class SponzaLoop
{
    private string? transferDumpPath;
    private bool transferBakeDone;
    private uint transferBakeDoneFrame;
    // Frames after the region's bake before the dump: the probes keep solving meanwhile.
    private const uint TransferSettleFrames = 900;
    // One texel in this many (by a hash of its record id, so every run picks the same ones).
    private const uint TransferSampleStride = 4;

    // BeginLightBake's live list under --transfer-dump: the sample, inside level 0 with a probe on every side.
    private bool TransferSampleKeeps(Vector4[] texels, int i, int firstAtlas)
    {
        if (i < firstAtlas || MathF.Abs(texels[2 * i].W) < 2f || clipmap is null) return false;
        // Either side of a double-sided pair may be drawn: each is its own sample.
        var h = (uint)i * 2654435761u;
        if ((h >> 7) % TransferSampleStride != 0) return false;
        var p = new Vector3(texels[2 * i].X, texels[2 * i].Y, texels[2 * i].Z);
        return clipmap.InsideDistance(0, p) >= 1.5f;
    }

    private void TransferDumpWhenDone()
    {
        if (transferDumpPath is null || !transferBakeDone || texelFrame - transferBakeDoneFrame < TransferSettleFrames || clipmap is null) return;
        var path = transferDumpPath;
        transferDumpPath = null;
        var irradiance = device.ReadTexture(clipmapIrradiance, out var aw, out _, out _);
        var depth = device.ReadTexture(clipmapDepth, out _, out _, out _);
        var states = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(clipmapState, 0, clipmap.LevelCount * clipmap.ProbesPerLevel * 16).AsSpan()).ToArray();
        var texels = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelBuffer, 0, texelCount * 32).AsSpan()).ToArray();
        var light = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelLightBuffer, 0, texelCount * 16).AsSpan()).ToArray();
        var live = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(lightBakeLiveBuffer, 0, lightBakeLive * 4).AsSpan()).ToArray();

        Vector4 Atlas(byte[] img, int x, int y)
        {
            var o = (y * aw + x) * 8;
            return new Vector4((float)BitConverter.ToHalf(img, o), (float)BitConverter.ToHalf(img, o + 2), (float)BitConverter.ToHalf(img, o + 4), (float)BitConverter.ToHalf(img, o + 6));
        }
        // probe_clipmap.glsl's blix_clipmapTileSample: a direction's place in the 6 x 6 interior, bilinear by hand.
        Vector4 TileSample((int X, int Y) tile, Vector3 dir, byte[] img)
        {
            dir = Vector3.Normalize(dir);
            var d = dir / (MathF.Abs(dir.X) + MathF.Abs(dir.Y) + MathF.Abs(dir.Z));
            var uv = d.Z >= 0f ? new Vector2(d.X, d.Y)
                : new Vector2((1f - MathF.Abs(d.Y)) * (d.X >= 0f ? 1f : -1f), (1f - MathF.Abs(d.X)) * (d.Y >= 0f ? 1f : -1f));
            var t = (uv * 0.5f + new Vector2(0.5f)) * 6f + new Vector2(0.5f);
            int bx = (int)MathF.Floor(t.X), by = (int)MathF.Floor(t.Y);
            float fx = t.X - bx, fy = t.Y - by;
            Vector4 A(int ox, int oy) => Atlas(img, tile.X + bx + ox, tile.Y + by + oy);
            return Vector4.Lerp(Vector4.Lerp(A(0, 0), A(1, 0), fx), Vector4.Lerp(A(0, 1), A(1, 1), fx), fy);
        }

        var spacing = clipmap.Spacing(0);
        var rows = new List<float>(live.Length * 66);
        var written = 0;
        foreach (var id in live)
        {
            var pos = new Vector3(texels[2 * id].X, texels[2 * id].Y, texels[2 * id].Z);
            var n = Vector3.Normalize(new Vector3(texels[2 * id + 1].X, texels[2 * id + 1].Y, texels[2 * id + 1].Z));
            var l = light[id];
            if (l.W <= 0f) continue;
            rows.Add(id); rows.Add(pos.X); rows.Add(pos.Y); rows.Add(pos.Z); rows.Add(n.X); rows.Add(n.Y); rows.Add(n.Z);
            rows.Add(l.X); rows.Add(l.Y); rows.Add(l.Z);
            var p = pos + n * (0.25f * spacing);
            var g = p / spacing - new Vector3(0.5f);
            var b = new Vector3(MathF.Floor(g.X), MathF.Floor(g.Y), MathF.Floor(g.Z));
            var f = g - b;
            for (var k = 0; k < 8; k++)
            {
                int ox = k & 1, oy = (k >> 1) & 1, oz = (k >> 2) & 1;
                var cell = new Int3((int)b.X + ox, (int)b.Y + oy, (int)b.Z + oz);
                var slot = clipmap.Slot(cell);
                var s = clipmap.SlotIndex(slot) * 4;
                var valid = (int)states[s] == cell.X && (int)states[s + 1] == cell.Y && (int)states[s + 2] == cell.Z
                    && (states[s + 3] & 1u) != 0 && (states[s + 3] & 2u) == 0;
                var trilinear = (ox == 1 ? f.X : 1f - f.X) * (oy == 1 ? f.Y : 1f - f.Y) * (oz == 1 ? f.Z : 1f - f.Z);
                var toProbe = clipmap.ProbePosition(0, cell) - p;
                var dist = toProbe.Length();
                var dir = dist > 1e-5f ? toProbe / dist : n;
                var facing = Vector3.Dot(dir, n) * 0.5f + 0.5f;
                var tile = clipmap.TileOrigin(0, slot);
                var chebyshev = 1f;
                var rgb = Vector3.Zero;
                if (valid)
                {
                    var moments = TileSample(tile, -dir, depth);
                    if (dist > moments.X)
                    {
                        var variance = MathF.Max(moments.Y, 1e-5f);
                        var dd = dist - moments.X;
                        var c = variance / (variance + dd * dd);
                        chebyshev = c * c * c;
                    }
                    var e = TileSample(tile, n, irradiance);
                    rgb = new Vector3(e.X, e.Y, e.Z);
                }
                rows.Add(valid ? 1f : 0f); rows.Add(trilinear); rows.Add(facing * facing); rows.Add(chebyshev);
                rows.Add(rgb.X); rows.Add(rgb.Y); rows.Add(rgb.Z);
            }
            written++;
        }
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(66);
            w.Write(written);
            foreach (var v in rows) w.Write(v);
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] transfer dump: {written:N0} texels (of {live.Length:N0} baked) with their 8 level-0 probes at {spacing} m, sun {sunDirection}: {path}"));
    }
}
