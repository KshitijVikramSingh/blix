using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The cells' detail (plan.md, "Probes after the cells", P2 and P3): what the incident pass took from the clipmap's probes
// for static surfaces, from the cells' own gather instead.
//   SKY VISIBILITY  the share of a cell's rays that escape, cosine-weighted -- specular occlusion, glass, the light behind
//                   a sheet read it.
//   DIRECTION       per part (sky, sun), the light's relative change as the normal tilts toward the cell's two tangents
//                   (texel.glsl, texelDetailFrame): the normal map's relief in the indirect light, at the cell's scale,
//                   where it came from a gradient between probes 0.5 m apart.
// Both from the sweep's rays (texel_nodes_shade.glsl, NodeDetail; refining improves the light, not these), one word a
// cell a part (texelDetailPack), landed as the light lands, cached beside each part (".detail"), and composed with the
// light (texel_compose.comp: the parts' directions weighted by their light, as the light is summed).
internal sealed partial class SponzaLoop
{
    private const int DetailVersion = 2;   // 2: the sky visibility by its square root (texel.glsl)
    private GpuBufferHandle sunNodeDetail;
    private uint[][] sunNodeDetailWords = Array.Empty<uint[]>();
    private uint[]? splitSkyDetail, splitSunDetail;
    private GpuBufferHandle splitSkyDetailBuffer, splitSunDetailBuffer, texelDetailBuffer;
    private bool splitDetailLanded;

    private static string DetailPathFor(string partPath) => partPath + ".detail";

    // UploadSunNodes, a split cook: the sums' buffer and each column's words.
    private void BeginSplitDetail(int suns, int columns)
    {
        // Live on the host: mapped, read where it lies once the slice's DispatchNow returns (ReadSplitDetail).
        sunNodeDetailMapped = liveTexels && SunNodesOnHost;
        sunNodeDetail = Own(sunNodeDetailMapped ? device.CreateReadableGpuBuffer(checked((int)((long)sunNodeSlice * suns * 16)), "sponza.sun-nodes.detail")
            : device.CreateGpuBuffer(checked((int)((long)sunNodeSlice * suns * 16)), name: "sponza.sun-nodes.detail"));
        sunNodeDetailWords = new uint[columns][];
        for (var k = 0; k < columns; k++) sunNodeDetailWords[k] = new uint[texelCount];
    }

    // SunNodeReadBack, the sweep's slice: each texel's detail per column, from its light's sums and the detail's.
    private bool sunNodeDetailMapped;

    private void ReadSplitDetail(Vector4[] sums, uint[] live, int first, int count, int suns, int passes)
    {
        if (sunNodeDetail.Equals(default(GpuBufferHandle)) || sunNodeDetailWords.Length == 0) return;
        Vector4[] detail;
        if (sunNodeDetailMapped)
        {
            var bytes = new byte[count * suns * 16];
            device.ReadMappedGpuBuffer(sunNodeDetail, 0, bytes);
            detail = MemoryMarshal.Cast<byte, Vector4>(bytes.AsSpan()).ToArray();
        }
        else detail = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(sunNodeDetail, 0, count * suns * 16).AsSpan()).ToArray();
        var words = sunNodeDetailWords;
        Parallel.For(0, count, t =>
        {
            var id = (int)live[first + t];
            var sky = detail[(long)t * suns].Z / passes;
            for (var k = 0; k < words.Length; k++)
            {
                var s = sums[(long)t * suns + k];
                var lum = 0.2126f * s.X + 0.7152f * s.Y + 0.0722f * s.Z;
                var d = detail[(long)t * suns + k];
                var tilt = lum > 1e-9f ? new Vector2(d.X, d.Y) / lum : Vector2.Zero;
                words[k][id] = PackDetail(tilt, sky);
            }
        });
    }

    private static uint PackDetail(Vector2 tilt, float sky)
    {
        static uint S8(float v) => (uint)((int)MathF.Round(Math.Clamp(v * 0.5f, -1f, 1f) * 127f) & 0xFF);
        return S8(tilt.X) | (S8(tilt.Y) << 8) | ((uint)MathF.Round(Math.Clamp(sky, 0f, 1f) * 255f) << 16) | (255u << 24);
    }

    // LandSplitSlice: the slice's detail into the parts' mirrors (the direction is relative: no sun scale to undo).
    private void LandSplitDetail(uint[] ids, int first, int count)
    {
        if (sunNodeDetailWords.Length == 0) return;
        for (var k = 0; k < sunNodeDetailWords.Length && k < sunNodeKinds.Length; k++)
        {
            var sunPart = sunNodeKinds[k] == 1;
            var from = sunNodeDetailWords[k];
            var into = sunPart ? (splitSunDetail ??= new uint[texelCount]) : (splitSkyDetail ??= new uint[texelCount]);
            Parallel.For(0, count, t => { var id = (int)ids[first + t]; into[id] = from[id]; });
        }
        splitDetailLanded = true;
    }

    // FlushSplitLanding / a part read: the mirrors to their buffers.
    private void FlushSplitDetail()
    {
        if (!splitDetailLanded) return;
        splitDetailLanded = false;
        if (splitSkyDetail is { } sky) splitSkyDetailBuffer = WriteDetail(splitSkyDetailBuffer, sky, "sponza.split.sky-detail");
        if (splitSunDetail is { } sun) splitSunDetailBuffer = WriteDetail(splitSunDetailBuffer, sun, "sponza.split.sun-detail");
        splitComposeDirty = true;
    }

    private GpuBufferHandle WriteDetail(GpuBufferHandle buffer, uint[] words, string name)
    {
        if (buffer.Equals(default(GpuBufferHandle)))
            return Own(device.CreateGpuBuffer(words.Length * 4, MemoryMarshal.AsBytes(words.AsSpan()), name));
        device.WriteGpuBuffer(buffer, 0, MemoryMarshal.AsBytes(words.AsSpan()));
        return buffer;
    }

    // BeginSplitLight: a part's detail read with it (none: the part's cook was from before -- the incident pass keeps the
    // clipmap there, and the detail comes with the next cook).
    private void ReadPartDetail(string partPath, bool sunPart)
    {
        var words = ReadDetailFile(DetailPathFor(partPath), texelCount);
        if (words is null) return;
        if (sunPart) splitSunDetail = words; else splitSkyDetail = words;
        splitDetailLanded = true;
        FlushSplitDetail();
    }

    // FinishSplitCook: each cooked column's detail saved beside its part.
    private void SaveSplitDetail()
    {
        for (var k = 0; k < sunNodeDetailWords.Length && k < splitCookPaths.Length; k++)
            if (splitCookPaths[k] is { } path) WriteDetailFile(DetailPathFor(path), sunNodeDetailWords[k]);
        sunNodeDetailWords = Array.Empty<uint[]>();
        if (!sunNodeDetail.Equals(default(GpuBufferHandle))) RetireGpuBuffer(sunNodeDetail);
        sunNodeDetail = default;
    }

    private static uint[]? ReadDetailFile(string path, int count)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadString() != "blix-texel-detail" || r.ReadInt32() != DetailVersion || r.ReadInt32() != count) return null;
            var words = new uint[count];
            var bytes = MemoryMarshal.AsBytes(words.AsSpan());
            return r.BaseStream.Read(bytes) == bytes.Length ? words : null;
        }
        catch (IOException) { return null; }
    }

    private static void WriteDetailFile(string path, uint[] words)
    {
        var partial = path + ".partial";
        try
        {
            using (var w = new BinaryWriter(File.Create(partial)))
            {
                w.Write("blix-texel-detail"); w.Write(DetailVersion); w.Write(words.Length);
                w.Write(MemoryMarshal.AsBytes(words.AsSpan()));
            }
            File.Move(partial, path, overwrite: true);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] texels: {words.Length:N0} cells' detail (sky visibility, direction) cached: {Path.GetFileName(path)}"));
        }
        catch (IOException e) { Console.WriteLine($"[VulkanSponza] texels: the cells' detail not cached ({e.Message})."); }
    }

    // The compose's detail buffers: the parts' (placeholders until any lands) and the composed one the incident pass reads.
    private IEnumerable<ShaderBufferBinding> DetailBindings()
    {
        var none = default(GpuBufferHandle);
        if (texelDetailBuffer.Equals(none)) texelDetailBuffer = Own(device.CreateGpuBuffer(Math.Max(texelCount, 1) * 4, name: "sponza.texels.detail"));
        if (splitSkyDetailBuffer.Equals(none)) splitSkyDetailBuffer = Own(device.CreateGpuBuffer(Math.Max(texelCount, 1) * 4, name: "sponza.split.sky-detail"));
        if (splitSunDetailBuffer.Equals(none)) splitSunDetailBuffer = Own(device.CreateGpuBuffer(Math.Max(texelCount, 1) * 4, name: "sponza.split.sun-detail"));
        yield return new ShaderBufferBinding("TexelSkyDetail", splitSkyDetailBuffer);
        yield return new ShaderBufferBinding("TexelSunDetail", splitSunDetailBuffer);
        yield return new ShaderBufferBinding("TexelDetail", texelDetailBuffer);
        yield return new ShaderBufferBinding("TexelSkyVis", CookedSkyVisNow ? texelSkyVisBuffer : splitSunDetailBuffer);
    }

    // The incident pass's: the composed detail once the compose made it, else a placeholder (every word 0: none).
    private GpuBufferHandle texelDetailPlaceholder;
    private GpuBufferHandle TexelDetailForIncident()
    {
        if (!texelDetailBuffer.Equals(default(GpuBufferHandle))) return texelDetailBuffer;
        if (texelDetailPlaceholder.Equals(default(GpuBufferHandle)))
            texelDetailPlaceholder = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.texels.detail.none"));
        return texelDetailPlaceholder;
    }

    // Whether the incident pass may read the detail (the compose made it): its uniform, so a placeholder is never indexed.
    private bool TexelDetailUp => !texelDetailBuffer.Equals(default(GpuBufferHandle));
    // The overlay's switch (on by default; --no-texel-detail): off, the probes answer everywhere as before (the A/B).
    private bool texelDetailOn = true;
}
