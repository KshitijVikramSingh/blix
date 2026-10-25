using System.Numerics;

namespace Blix.Geometry;

/// <summary>
/// The per-hit alpha test's data, appended to the surface words (<c>BlixRaySurfaces</c>) as a trailer: the cut-out materials' alpha, a
/// byte a texel, and what a ray needs to look it up at the point it hits. ray_query.glsl's blix_covered, blix_hwx.mm's blix_cover and
/// <see cref="Alpha"/> read it the same way.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a trailer and not a texture.</b> The engine binds no texture arrays and no bindless sets, so a trace shader cannot sample a
/// material's own albedo at a hit. The alpha channel is copied (ray_alpha_bake.comp) into one buffer the traversal already binds, at
/// a resolution capped per material, and read with a bilinear fetch the three twins spell out the same way. No shader gains a binding.
/// </para>
/// <para>
/// The trailer starts at word <c>rows</c> (the triangle count; the shader reads it as the length of the triangle block). Words from there:
/// <c>[0]</c> <see cref="Magic"/>, <c>[1]</c> placements P, <c>[2]</c> materials M, <c>[3]</c> vertices V, <c>[4]</c> the placement table's
/// offset, <c>[5]</c> the material table's, <c>[6]</c> the coordinates', <c>[7]</c> the atlas's (all in words from the trailer's start).
/// The placement table is P words: the placement's alpha material, or 0xFFFFFFFF for none. A material is four words: its atlas offset,
/// width | height &lt;&lt; 16, the cutoff (float bits), flags (0). The coordinates are V float2s, one per ray position (RayQueryGpuData.Uvs).
/// The atlas holds each material's width x height bytes row by row, four to a word, low byte first: its alpha times the base colour's.
/// </para>
/// <para>
/// A hit asks only where the triangle is <i>partly</i> covered (the surface word's sixteenths 1..15): 0 is never met and 16 or more is
/// always, as before. The bake gives those two values only to rows with no texel in doubt (ray_surface_bake.comp).
/// </para>
/// </remarks>
public static class RayAlphaAtlas
{
    public const uint Magic = 0xA17A0001u;
    public const uint None = 0xFFFFFFFFu;
    public const int HeaderWords = 8;
    public const int MaterialWords = 4;

    /// <summary>The longest side an alpha atlas entry takes: a texture larger than this is read at the mip that fits.</summary>
    public const int MaxSide = 512;

    /// <summary>One cut-out material's entry.</summary>
    public readonly record struct Material(int Width, int Height, float Cutoff);

    /// <summary>The trailer, its atlas still zero, and where each material's alpha goes.</summary>
    public sealed record Plan(uint[] Words, int[] AtlasWord)
    {
        /// <summary>Where material m's alpha starts, in words from the trailer's start.</summary>
        public int AtlasStart(int material) => AtlasWord[material];
    }

    /// <summary>Lays the trailer out. <paramref name="placementMaterial"/> is per placement, an index into <paramref name="materials"/> or <see cref="None"/>.</summary>
    public static Plan Build(ReadOnlySpan<uint> placementMaterial, IReadOnlyList<Material> materials, ReadOnlySpan<Vector2> uvs)
    {
        var placementTable = HeaderWords;
        var materialTable = placementTable + placementMaterial.Length;
        var uvTable = materialTable + materials.Count * MaterialWords;
        var atlas = uvTable + uvs.Length * 2;
        var atlasWord = new int[materials.Count];
        var total = atlas;
        for (var m = 0; m < materials.Count; m++)
        {
            atlasWord[m] = total;
            total += (materials[m].Width * materials[m].Height + 3) / 4;
        }
        var words = new uint[total];
        words[0] = Magic;
        words[1] = (uint)placementMaterial.Length;
        words[2] = (uint)materials.Count;
        words[3] = (uint)uvs.Length;
        words[4] = (uint)placementTable;
        words[5] = (uint)materialTable;
        words[6] = (uint)uvTable;
        words[7] = (uint)atlas;
        for (var p = 0; p < placementMaterial.Length; p++) words[placementTable + p] = placementMaterial[p];
        for (var m = 0; m < materials.Count; m++)
        {
            var at = materialTable + m * MaterialWords;
            words[at] = (uint)atlasWord[m];
            words[at + 1] = (uint)materials[m].Width | ((uint)materials[m].Height << 16);
            words[at + 2] = BitConverter.SingleToUInt32Bits(materials[m].Cutoff);
            words[at + 3] = 0u;
        }
        for (var v = 0; v < uvs.Length; v++)
        {
            words[uvTable + v * 2] = BitConverter.SingleToUInt32Bits(uvs[v].X);
            words[uvTable + v * 2 + 1] = BitConverter.SingleToUInt32Bits(uvs[v].Y);
        }
        return new Plan(words, atlasWord);
    }

    /// <summary>Whether <paramref name="trailer"/> (the words from the trailer's start) is one this reads.</summary>
    public static bool IsTrailer(ReadOnlySpan<uint> trailer) => trailer.Length >= HeaderWords && trailer[0] == Magic;

    /// <summary>The material's alpha at <paramref name="uv"/>: bilinear over its atlas bytes, the texture wrapping (glTF's default), 0..1.</summary>
    /// <remarks>Spelled as ray_query.glsl's blix_alphaAt: the texel centres at (i + 0.5) / size, the four neighbours wrapped, lerped in x then y.</remarks>
    public static float Alpha(ReadOnlySpan<uint> trailer, uint material, Vector2 uv)
    {
        var at = (int)trailer[5] + (int)material * MaterialWords;
        var offset = (int)trailer[at];
        var size = trailer[at + 1];
        var w = (int)(size & 0xFFFFu);
        var h = (int)(size >> 16);
        var fx = uv.X - MathF.Floor(uv.X);
        var fy = uv.Y - MathF.Floor(uv.Y);
        var px = fx * w - 0.5f;
        var py = fy * h - 0.5f;
        var x0 = (int)MathF.Floor(px);
        var y0 = (int)MathF.Floor(py);
        var tx = px - x0;
        var ty = py - y0;
        var top = Texel(trailer, offset, w, h, x0, y0) + (Texel(trailer, offset, w, h, x0 + 1, y0) - Texel(trailer, offset, w, h, x0, y0)) * tx;
        var bottom = Texel(trailer, offset, w, h, x0, y0 + 1) + (Texel(trailer, offset, w, h, x0 + 1, y0 + 1) - Texel(trailer, offset, w, h, x0, y0 + 1)) * tx;
        return top + (bottom - top) * ty;
    }

    private static float Texel(ReadOnlySpan<uint> trailer, int offset, int w, int h, int x, int y)
    {
        x = ((x % w) + w) % w;
        y = ((y % h) + h) % h;
        var index = y * w + x;
        return ((trailer[offset + (index >> 2)] >> ((index & 3) * 8)) & 255u) / 255f;
    }

    /// <summary>Whether the material's alpha at <paramref name="uv"/> passes its cutoff: the raster's alpha test.</summary>
    public static bool Passes(ReadOnlySpan<uint> trailer, uint material, Vector2 uv)
    {
        var at = (int)trailer[5] + (int)material * MaterialWords;
        return Alpha(trailer, material, uv) >= BitConverter.UInt32BitsToSingle(trailer[at + 2]);
    }

    /// <summary>The coordinates of vertex <paramref name="index"/>.</summary>
    public static Vector2 Uv(ReadOnlySpan<uint> trailer, uint index)
    {
        var at = (int)trailer[6] + (int)index * 2;
        return new Vector2(BitConverter.UInt32BitsToSingle(trailer[at]), BitConverter.UInt32BitsToSingle(trailer[at + 1]));
    }

    /// <summary>The coordinates at barycentrics (v1, v2) of triangle (a, b, c): a (1 - u - v) + b u + c v, as the shaders interpolate.</summary>
    public static Vector2 UvAt(ReadOnlySpan<uint> trailer, uint a, uint b, uint c, Vector2 barycentrics) =>
        Uv(trailer, a) * (1f - barycentrics.X - barycentrics.Y) + Uv(trailer, b) * barycentrics.X + Uv(trailer, c) * barycentrics.Y;
}
