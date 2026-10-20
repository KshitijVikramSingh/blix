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
    private TextureHandle lightmapIndexTexture, lightmapIndexTextureNone;
    private GpuBufferHandle texelMetaBuffer, texelMetaNone;
    private bool atlasGather = true;
    // The index map on the CPU, kept only for --texel-inspect (the GPU's is a texture).
    private uint[]? lightmapIndexMapCpu;
    private bool AtlasGatherNow => atlasGather && lightmapTexels && !lightmapIndexTexture.Equals(default(TextureHandle))
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
        lightmapIndexTexture = Own(device.CreateTexture2D(new TextureDescription(lightmapAtlasWidth, lightmapAtlasHeight, TextureFormat.R32Uint,
            SamplerDescription.NearestClamp), MemoryMarshal.AsBytes(map.AsSpan()), "sponza.lightmap.index-texture"));
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

    private ShaderTextureBinding AtlasGatherTexture()
    {
        if (!lightmapIndexTexture.Equals(default(TextureHandle))) return new ShaderTextureBinding("uLightmapIndexTex", lightmapIndexTexture);
        if (lightmapIndexTextureNone.Equals(default(TextureHandle)))
            lightmapIndexTextureNone = Own(device.CreateTexture2D(new TextureDescription(1, 1, TextureFormat.R32Uint, SamplerDescription.NearestClamp),
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, "sponza.lightmap.index-texture.none"));
        return new ShaderTextureBinding("uLightmapIndexTex", lightmapIndexTextureNone);
    }

    private ShaderBufferBinding AtlasGatherMeta()
    {
        if (!texelMetaBuffer.Equals(default(GpuBufferHandle))) return new ShaderBufferBinding("TexelMeta", texelMetaBuffer);
        if (texelMetaNone.Equals(default(GpuBufferHandle))) texelMetaNone = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.lightmap.texel-meta.none"));
        return new ShaderBufferBinding("TexelMeta", texelMetaNone);
    }

    private IReadOnlyList<ShaderTextureBinding> WithLightmapCells(IReadOnlyList<ShaderTextureBinding> textures)
    {
        var cells = AtlasGatherTexture();
        var all = new ShaderTextureBinding[textures.Count + 1];
        for (var i = 0; i < textures.Count; i++) all[i] = textures[i];
        all[^1] = new ShaderTextureBinding("uLightmapCells", cells.Texture);
        return all;
    }
}
