using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --lightmap with --transport-texels (lightmaps, step 3): the surfaces in the lightmap atlas (blix cook lightmap
// --write) get its texels instead of hashed cells; the ones the cook leaves out (foliage: --density "trees=off") keep
// the hashed texels, in the same buffers (UploadLightmapTexels). Each atlas texel a surface covers becomes a texel record -- its world position,
// face normal and atlas coordinate -- and the gather, blend, filter and lookup that served the hashed texels serve
// these (texel.glsl, texelsAroundLightmap): a pixel reads its four bilinear atlas texels through the pre-pass's
// lightmap texel target and the atlas's index map. A surface whose material is double-sided gets a PAIR of records
// (front, then back): one UV, two sides, two lights.
internal sealed partial class SponzaLoop
{
    // The scene atlas's size, from the .blixlightmap files (every file of a scene shares it).
    private static int lightmapAtlasWidth, lightmapAtlasHeight;
    // What the bake rasterizes, captured as the shared buffers are built: each drawable with a lightmap.
    private readonly List<(string Name, byte[] VertexBytes, int Stride, int NormalOffset, uint[] Indices, Matrix4x4 World, Vector2[] Texels, bool DoubleSided)> lightmapSources = new();
    private GpuBufferHandle lightmapIndexMapBuffer;
    private bool lightmapTexels;
    private int lightmapFirstRecord;

    // What one texel record costs on the GPU: its two vec4s (position, normal) and the light state UploadTexels makes
    // for each (light, prior, filtered, open, blend: a vec4 each; stamp, moment, epoch: a uint each).
    private const long LightmapRecordBytes = 2 * 16 + 5 * 16 + 3 * 4;
    // The most texel state the atlas may ask for. UploadTexels holds a zeroed managed copy of most of it while it
    // uploads, and on unified memory the GPU's share is the same RAM: the first try asked for ~50M+ records (8192^2 at
    // 4 cm, foliage both sides) and took the machine down with it. Over this, the bake refuses and says why.
    private const long LightmapRecordBudgetBytes = 2L << 30;

    // The atlas rasterized (in the background cook): every texel whose centre lies in a triangle gets a record, and
    // then the gutter -- empty texels beside covered ones, twice over -- points at its neighbour's record, so a
    // bilinear lookup at a chart's edge reads that chart's light instead of nothing. Two passes over the same triangles
    // in the same order: the first only claims texels and counts records, so the guard runs before anything sized by
    // the count is allocated; the second fills the records the first claimed. Null when over the budget.
    private (Vector4[] Texels, uint[] IndexMap, int Count)? BakeLightmapTexels()
    {
        var clock = Stopwatch.StartNew();
        int w = lightmapAtlasWidth, h = lightmapAtlasHeight;
        var map = new uint[(long)w * h];
        Array.Fill(map, 0xFFFFFFFFu);
        // Records per source, to say where they went when the budget refuses.
        var perSource = new long[lightmapSources.Count];
        long records = 0, covered = 0, paired = 0;
        // Double-sided texels whose stored face normal (the triangle's winding) disagrees with the interpolated vertex
        // normal -- what a pixel's shading normal is picked against (texelsAroundLightmap) -- and those near edge-on.
        long pairedOpposed = 0, pairedGrazing = 0;
        Vector4[]? texels = null;
        for (var pass = 0; pass < 2; pass++)
        for (var si = 0; si < lightmapSources.Count; si++)
        {
            var source = lightmapSources[si];
            var stride = source.Stride;
            Vector3 Position(uint v) => Vector3.Transform(new Vector3(
                BitConverter.ToSingle(source.VertexBytes, (int)v * stride),
                BitConverter.ToSingle(source.VertexBytes, (int)v * stride + 4),
                BitConverter.ToSingle(source.VertexBytes, (int)v * stride + 8)), source.World);
            var idx = source.Indices;
            for (var t = 0; t + 2 < idx.Length; t += 3)
            {
                Vector2 a = source.Texels[idx[t]], b = source.Texels[idx[t + 1]], c = source.Texels[idx[t + 2]];
                var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (MathF.Abs(area) < 1e-8f) continue;
                Vector3 pa = Position(idx[t]), pb = Position(idx[t + 1]), pc = Position(idx[t + 2]);
                var n = Vector3.Cross(pb - pa, pc - pa);
                if (n.LengthSquared() < 1e-20f) continue;
                n = Vector3.Normalize(n);
                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
                for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var wa = ((b.X - p.X) * (c.Y - p.Y) - (b.Y - p.Y) * (c.X - p.X)) / area;
                    var wb = ((c.X - p.X) * (a.Y - p.Y) - (c.Y - p.Y) * (a.X - p.X)) / area;
                    var wc = 1f - wa - wb;
                    if (wa < -1e-4f || wb < -1e-4f || wc < -1e-4f) continue;
                    var cell = (long)y * w + x;
                    if (pass == 0)
                    {
                        // Claim: the first triangle to reach a texel owns it (the second pass meets them in the same
                        // order). Counted past uint's reach rather than wrapped: the guard reads the long.
                        if (map[cell] != 0xFFFFFFFFu) continue;
                        var size = source.DoubleSided ? 2 : 1;
                        map[cell] = records < uint.MaxValue - 2 ? (uint)records : 0xFFFFFFFEu;
                        records += size;
                        perSource[si] += size;
                        covered++;
                        if (source.DoubleSided) paired++;
                        continue;
                    }
                    // Fill: the record this texel's owner claimed, written once (w is +-4 once written, 0 before).
                    var id = map[cell];
                    if (texels![2 * id].W != 0f) continue;
                    var pos = pa * wa + pb * wb + pc * wc;
                    var atlas = BitConverter.Int32BitsToSingle(x | (y << 16));
                    // Position w: the area weight texel.glsl's hashed path reads (4: whole), negative for a pair.
                    texels[2 * id] = new Vector4(pos, source.DoubleSided ? -4f : 4f);
                    texels[2 * id + 1] = new Vector4(n, atlas);
                    if (source.DoubleSided)
                    {
                        if (source.NormalOffset >= 0)
                        {
                            Vector3 Normal(uint v) => Vector3.TransformNormal(new Vector3(
                                BitConverter.ToSingle(source.VertexBytes, (int)v * stride + source.NormalOffset),
                                BitConverter.ToSingle(source.VertexBytes, (int)v * stride + source.NormalOffset + 4),
                                BitConverter.ToSingle(source.VertexBytes, (int)v * stride + source.NormalOffset + 8)), source.World);
                            var shading = Normal(idx[t]) * wa + Normal(idx[t + 1]) * wb + Normal(idx[t + 2]) * wc;
                            var agree = shading.LengthSquared() > 1e-12f ? Vector3.Dot(Vector3.Normalize(shading), n) : 0f;
                            if (agree < 0f) pairedOpposed++;
                            else if (agree < 0.3f) pairedGrazing++;
                        }
                        texels[2 * id + 2] = new Vector4(pos, -4f);
                        texels[2 * id + 3] = new Vector4(-n, atlas);
                    }
                }
            }
            if (pass == 0 && si == lightmapSources.Count - 1)
            {
                var bytes = records * LightmapRecordBytes;
                if (bytes > LightmapRecordBudgetBytes || records * 2 * 16 > int.MaxValue)
                {
                    var worst = Enumerable.Range(0, perSource.Length).OrderByDescending(i => perSource[i]).Take(8)
                        .Select(i => $"{lightmapSources[i].Name} {perSource[i] / (double)records:P1}");
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"[VulkanSponza] lightmap texels: REFUSED -- {records:N0} records ({covered:N0} atlas texels, {paired:N0} double-sided) would need {bytes / 1048576.0:N0} MB of texel state, over the {LightmapRecordBudgetBytes / 1048576:N0} MB budget. Coarsen the cook (blix cook lightmap --density \"pattern=cm\"). Most records: {string.Join(", ", worst)}. Using the hashed texels instead."));
                    return null;
                }
                texels = new Vector4[records * 2];
            }
        }
        long gutter = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            var next = (uint[])map.Clone();
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var cell = (long)y * w + x;
                if (map[cell] != 0xFFFFFFFFu) continue;
                for (var k = 0; k < 8; k++)
                {
                    int dx = k switch { 0 => 1, 1 => -1, 4 => 1, 5 => -1, 6 => 1, 7 => -1, _ => 0 };
                    int dy = k switch { 2 => 1, 3 => -1, 4 => 1, 5 => 1, 6 => -1, 7 => -1, _ => 0 };
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    var neighbour = map[(long)ny * w + nx];
                    if (neighbour == 0xFFFFFFFFu) continue;
                    next[cell] = neighbour;
                    gutter++;
                    break;
                }
            }
            map = next;
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] lightmap texels: {covered:N0} atlas texels covered ({paired:N0} of them double-sided, two records each), {gutter:N0} gutter texels pointing at a neighbour; {w} x {h} atlas; {records * LightmapRecordBytes / 1048576.0:N0} MB of texel state; baked in {clock.Elapsed.TotalSeconds:0.0} s."));
        if (paired > 0)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] lightmap double-sided texels: {pairedOpposed / (double)paired:P1} have vertex normals opposite their winding, {pairedGrazing / (double)paired:P1} within ~17 deg of edge-on (a pixel picks its side by the vertex normal)."));
        return (texels!, map, (int)records);
    }

    // Both kinds in one set of texel buffers: the hashed records first (the hash table names them, as without a
    // lightmap), then the atlas's from lightmapFirstRecord (the index map names them, shifted past the hashed ones).
    // Every pass that reads a record by id reads either kind; the filter tells them apart by id, mark and lookup by
    // whether the pixel has an atlas texel (texel.glsl).
    private void UploadLightmapTexels(TexelBake hashed, (Vector4[] Texels, uint[] IndexMap, int Count) atlas)
    {
        var first = hashed.Count;
        var texels = new Vector4[hashed.Texels.Length + atlas.Texels.Length];
        hashed.Texels.CopyTo(texels, 0);
        atlas.Texels.CopyTo(texels, hashed.Texels.Length);
        var map = atlas.IndexMap;
        for (var i = 0; i < map.Length; i++)
            if (map[i] != 0xFFFFFFFFu) map[i] += (uint)first;
        UploadTexels(hashed with { Texels = texels, Count = first + atlas.Count });
        lightmapIndexMapBuffer = Own(device.CreateGpuBuffer(map.Length * 4, MemoryMarshal.AsBytes(map.AsSpan()), "sponza.lightmap.index-map"));
        lightmapFirstRecord = first;
        lightmapTexels = true;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] texels: {first:N0} hashed (the surfaces the atlas leaves out) and {atlas.Count:N0} lightmap records, one set."));
    }

    // What the texel passes read for lightmap addressing: x 1 when on, yz the atlas size, w the first lightmap record
    // (below it, hashed).
    private Vector4 LightmapUniform => new(lightmapTexels ? 1f : 0f, lightmapAtlasWidth, lightmapAtlasHeight, lightmapFirstRecord);

    // The pre-pass's lightmap texel target -- or, without one, the SurfaceKey target, the same format, standing in
    // (the passes read it only when uLightmap.x is 1).
    private ShaderTextureBinding LightmapTexelTexture() =>
        new("uLightmapTexel", graph.GetColorTexture(lightmapEnabled ? lightmapTexelHandle : surfaceKeyHandle));

    private GpuBufferHandle LightmapIndexMap()
    {
        if (lightmapIndexMapBuffer.Equals(default(GpuBufferHandle)))
            lightmapIndexMapBuffer = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.lightmap.index-map.none"));
        return lightmapIndexMapBuffer;
    }
}
