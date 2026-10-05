using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;

namespace Blix.Demos.VulkanSponza;

// The probe clipmap's sampling on the CPU (blix_clipmapSample in probe_clipmap.glsl, function for function) over the
// atlases and states read back at the shot, so the surface reference can ask of each pixel not just what the field
// said but which probes said it, with what weight, and whether each one can see the point at all.
internal sealed partial class SponzaLoop
{
    private sealed class ClipmapReadback
    {
        public required ProbeClipmap Map;
        public required byte[] Irradiance;
        public required byte[] Depth;
        public required int Width;
        public required uint[] States;

        public Vector4 Texel(byte[] atlas, int x, int y)
        {
            var o = (y * Width + x) * 8;
            return new Vector4((float)BitConverter.ToHalf(atlas, o), (float)BitConverter.ToHalf(atlas, o + 2),
                (float)BitConverter.ToHalf(atlas, o + 4), (float)BitConverter.ToHalf(atlas, o + 6));
        }
    }

    // One probe's part in a point's answer: where it is, its share of the final value (shares sum to 1 over a
    // found answer), and the irradiance it gave.
    private readonly record struct ProbeShare(Vector3 Position, int Level, float Share, Vector3 Irradiance);

    private ClipmapReadback? ReadClipmap()
    {
        if (clipmap is null || clipmapFrame == 0) return null;
        var irr = device.ReadTexture(clipmapIrradiance, out var w, out _, out _);
        var depth = device.ReadTexture(clipmapDepth, out _, out _, out _);
        var slots = ClipmapLevels * clipmap.ProbesPerLevel;
        var states = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(clipmapState, 0, slots * 16).AsSpan()).ToArray();
        return new ClipmapReadback { Map = clipmap, Irradiance = irr, Depth = depth, Width = w, States = states };
    }

    private static Vector2 OctEncode(Vector3 d)
    {
        d /= MathF.Abs(d.X) + MathF.Abs(d.Y) + MathF.Abs(d.Z);
        if (d.Z >= 0f) return new Vector2(d.X, d.Y);
        return new Vector2((1f - MathF.Abs(d.Y)) * (d.X >= 0f ? 1f : -1f), (1f - MathF.Abs(d.X)) * (d.Y >= 0f ? 1f : -1f));
    }

    private static Vector4 TileSample(ClipmapReadback r, (int X, int Y) tile, Vector3 dir, bool depth)
    {
        var t = (OctEncode(Vector3.Normalize(dir)) * 0.5f + new Vector2(0.5f)) * 6f + new Vector2(0.5f);
        var bx = (int)MathF.Floor(t.X); var by = (int)MathF.Floor(t.Y);
        var fx = t.X - bx; var fy = t.Y - by;
        var atlas = depth ? r.Depth : r.Irradiance;
        var a = r.Texel(atlas, tile.X + bx, tile.Y + by);
        var b = r.Texel(atlas, tile.X + bx + 1, tile.Y + by);
        var c = r.Texel(atlas, tile.X + bx, tile.Y + by + 1);
        var d = r.Texel(atlas, tile.X + bx + 1, tile.Y + by + 1);
        return Vector4.Lerp(Vector4.Lerp(a, b, fx), Vector4.Lerp(c, d, fx), fy);
    }

    // blix_clipmapLevelSample: the eight probes around the lifted point, each with its unnormalised weight.
    private static List<(Vector3 Position, float Weight, Vector3 Irradiance)> LevelShares(ClipmapReadback r, int level, Vector3 world, Vector3 n)
    {
        var map = r.Map;
        var list = new List<(Vector3, float, Vector3)>(8);
        var spacing = map.Spacing(level);
        var p = world + n * (0.25f * spacing);
        var g = p / spacing - new Vector3(0.5f);
        var bx = (int)MathF.Floor(g.X); var by = (int)MathF.Floor(g.Y); var bz = (int)MathF.Floor(g.Z);
        var frac = g - new Vector3(bx, by, bz);
        for (var i = 0; i < 8; i++)
        {
            int ox = i & 1, oy = (i >> 1) & 1, oz = (i >> 2) & 1;
            var cell = new Int3(bx + ox, by + oy, bz + oz);
            var slot = map.Slot(cell);
            var s = level * map.ProbesPerLevel + map.SlotIndex(slot);
            var flags = r.States[s * 4 + 3];
            if ((flags & 1u) == 0 || (flags & 2u) != 0) continue;
            if ((int)r.States[s * 4] != cell.X || (int)r.States[s * 4 + 1] != cell.Y || (int)r.States[s * 4 + 2] != cell.Z) continue;
            var w = (ox == 1 ? frac.X : 1f - frac.X) * (oy == 1 ? frac.Y : 1f - frac.Y) * (oz == 1 ? frac.Z : 1f - frac.Z);
            var probe = map.ProbePosition(level, cell);
            var toProbe = probe - p;
            var dist = toProbe.Length();
            var dir = dist > 1e-5f ? toProbe / dist : n;
            var facing = Vector3.Dot(dir, n) * 0.5f + 0.5f;
            w *= facing * facing;
            if (w <= 1e-6f) continue;
            var tile = map.TileOrigin(level, slot);
            var moments = TileSample(r, tile, -dir, true);
            if (dist > moments.X)
            {
                var variance = MathF.Max(moments.Y, 1e-5f);
                var d = dist - moments.X;
                var chebyshev = variance / (variance + d * d);
                w *= MathF.Max(chebyshev * chebyshev * chebyshev, 0f);
            }
            if (w <= 1e-6f) continue;
            var irr = TileSample(r, tile, n, false);
            list.Add((probe, w, new Vector3(irr.X, irr.Y, irr.Z)));
        }
        return list;
    }

    // blix_clipmapSample, returning the shares instead of only their sum.
    private static List<ProbeShare> ClipmapShares(ClipmapReadback r, Vector3 world, Vector3 n)
    {
        var map = r.Map;
        var result = new List<ProbeShare>();
        var (level, blend) = map.Locate(world);
        if (level < 0) return result;
        for (var l = level; l < map.LevelCount; l++)
        {
            var a = LevelShares(r, l, world, n);
            var wa = a.Sum(s => s.Weight);
            if (wa <= 0f) { blend = 0f; continue; }
            var useNext = blend > 0f && l + 1 < map.LevelCount && map.InsideDistance(l + 1, world) >= 0f;
            var b = useNext ? LevelShares(r, l + 1, world, n) : new();
            var wb = b.Sum(s => s.Weight);
            var kb = wb > 0f ? blend : 0f;
            foreach (var s in a) result.Add(new ProbeShare(s.Position, l, (1f - kb) * s.Weight / wa, s.Irradiance));
            if (kb > 0f) foreach (var s in b) result.Add(new ProbeShare(s.Position, l + 1, kb * s.Weight / wb, s.Irradiance));
            return result;
        }
        return result;
    }
}
