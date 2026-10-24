using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The incident pass's atlas lookup by gather (texel.glsl, texelsAroundLightmapGather): the index map as an R32UI texture,
// one textureGather for a pixel's 2x2, and a 4-byte meta word a record carrying everything the choice of texels reads --
// face normal (octahedral, 12 + 12 bits), whether it has one, a double-sided pair's front, not buried. The same choice as
// the buffer walk (texelsAroundLightmap), ~35 dependent loads a pixel -> one gather and ~8 (2026-10-20).
// --no-atlas-gather: the buffer walk; --ab atlasgather alternates the two.
internal sealed partial class SponzaLoop
{
    // The atlas's index map, tile-sparse (lightmap_index.glsl; a dense R32 texture before, 64 MB on Crytek Sponza).
    private GpuBufferHandle lightmapIndexBuffer, lightmapIndexNone;
    private int lightmapIndexWords;
    private GpuBufferHandle texelMetaBuffer, texelMetaNone;
    private bool atlasGather = true;
    // The index map on the CPU, kept only for --texel-inspect (the GPU's is tile-sparse).
    private uint[]? lightmapIndexMapCpu;
    private bool AtlasGatherNow => atlasGather && lightmapTexels && !lightmapIndexBuffer.Equals(default(GpuBufferHandle))
        && !(abMode == "atlasgather" && AbOffPhase);

    // Called where the atlas's records and index map go up (UploadLightmapTexels): `texels` every record (two Vector4 a
    // record: position with w the sample count, sign a double-sided pair's front, |w| < 2 buried; the face normal).
    private void UploadAtlasGather(uint[] map, Vector4[] texels, int first, int count)
    {
        var meta = new uint[Math.Max(1, count)];
        for (var r = 0; r < count; r++)
        {
            var pos = texels[2 * (first + r)];
            var nrm = texels[2 * (first + r) + 1];
            var normal = new Vector3(nrm.X, nrm.Y, nrm.Z);
            var length = normal.Length();
            var word = OctPack12(length > 1e-6f ? normal / length : Vector3.UnitZ);
            if (length > 1e-6f) word |= 1u << 29;
            if (pos.W < 0f) word |= 1u << 30;
            if (MathF.Abs(pos.W) >= 2f) word |= 1u << 31;
            meta[r] = word;
        }
        if (texelInspect is not null) lightmapIndexMapCpu = map;
        texelMetaBuffer = Own(device.CreateGpuBuffer(meta.Length * 4, MemoryMarshal.AsBytes(meta.AsSpan()), "sponza.lightmap.texel-meta"));
        var index = PackLightmapIndex(map, lightmapAtlasWidth, lightmapAtlasHeight);
        lightmapIndexWords = index.Length;
        lightmapIndexBuffer = Own(device.CreateGpuBuffer(index.Length * 4, MemoryMarshal.AsBytes(index.AsSpan()), "sponza.lightmap.index"));
    }

    // Octahedral, 12 bits a component (texel.glsl's texelMetaNormal).
    private static uint OctPack12(Vector3 n)
    {
        var p = new Vector2(n.X, n.Y) / (MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z));
        if (n.Z < 0f)
            p = new Vector2((1f - MathF.Abs(p.Y)) * (p.X >= 0f ? 1f : -1f), (1f - MathF.Abs(p.X)) * (p.Y >= 0f ? 1f : -1f));
        uint Q(float v) => (uint)Math.Clamp(MathF.Round((v * 0.5f + 0.5f) * 4095f), 0f, 4095f);
        return Q(p.X) | Q(p.Y) << 12;
    }

    // lightmap_index.glsl's words: the atlas size and its 8 x 8 tiles, a word a tile (its ids' first word, 0xFFFFFFFF none),
    // then the covered tiles' 64 ids each.
    private static uint[] PackLightmapIndex(uint[] map, int width, int height)
    {
        int tw = (width + 7) / 8, th = (height + 7) / 8;
        var tiles = new uint[tw * th];
        var covered = 0;
        for (var t = 0; t < tiles.Length; t++)
        {
            int x0 = t % tw * 8, y0 = t / tw * 8;
            var any = false;
            for (var y = y0; y < Math.Min(y0 + 8, height) && !any; y++)
                for (var x = x0; x < Math.Min(x0 + 8, width); x++)
                    if (map[y * width + x] != 0xFFFFFFFFu) { any = true; break; }
            tiles[t] = any ? (uint)covered++ : 0xFFFFFFFFu;
        }
        var head = 4 + tiles.Length;
        var words = new uint[head + covered * 64];
        Array.Fill(words, 0xFFFFFFFFu, head, covered * 64);
        words[0] = (uint)width; words[1] = (uint)height; words[2] = (uint)tw; words[3] = (uint)tiles.Length;
        for (var t = 0; t < tiles.Length; t++)
        {
            if (tiles[t] == 0xFFFFFFFFu) { words[4 + t] = 0xFFFFFFFFu; continue; }
            var at = (uint)(head + tiles[t] * 64);
            words[4 + t] = at;
            int x0 = t % tw * 8, y0 = t / tw * 8;
            for (var y = y0; y < Math.Min(y0 + 8, height); y++)
                for (var x = x0; x < Math.Min(x0 + 8, width); x++)
                    words[at + (y - y0) * 8 + (x - x0)] = map[y * width + x];
        }
        return words;
    }

    // The dense map back (the dumps' lightmap_index.r32u, as the tools read it).
    private uint[] ReadLightmapIndexDense(out int width, out int height)
    {
        var all = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(lightmapIndexBuffer, 0, checked(lightmapIndexWords * 4)).AsSpan()).ToArray();
        width = (int)all[0]; height = (int)all[1];
        var tw = (int)all[2];
        var dense = new uint[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var tile = all[4 + y / 8 * tw + x / 8];
                dense[y * width + x] = tile == 0xFFFFFFFFu ? 0xFFFFFFFFu : all[tile + (y % 8) * 8 + x % 8];
            }
        return dense;
    }

    private ShaderBufferBinding AtlasIndexBuffer()
    {
        if (!lightmapIndexBuffer.Equals(default(GpuBufferHandle))) return new ShaderBufferBinding("LightmapIndex", lightmapIndexBuffer);
        if (lightmapIndexNone.Equals(default(GpuBufferHandle)))
            lightmapIndexNone = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.lightmap.index.none"));   // a 0 x 0 atlas
        return new ShaderBufferBinding("LightmapIndex", lightmapIndexNone);
    }

    private ShaderBufferBinding AtlasGatherMeta()
    {
        if (!texelMetaBuffer.Equals(default(GpuBufferHandle))) return new ShaderBufferBinding("TexelMeta", texelMetaBuffer);
        if (texelMetaNone.Equals(default(GpuBufferHandle))) texelMetaNone = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.lightmap.texel-meta.none"));
        return new ShaderBufferBinding("TexelMeta", texelMetaNone);
    }

}
