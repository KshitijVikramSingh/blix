using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --lightmap with --transport-texels (lightmaps, step 3): the surfaces in the lightmap atlas (blix cook lightmap
// --write) get its texels instead of hashed cells; the ones the cook leaves out (foliage: --density "trees=off") keep
// the hashed texels, in the same buffers (UploadLightmapTexels). Each atlas texel a surface covers becomes a texel record -- its world position,
// face normal and atlas coordinate -- and the gather, blend, filter and lookup that served the hashed texels serve
// these (texel.glsl, texelsAroundLightmap): a pixel reads its four bilinear atlas texels through the pre-pass's
// lightmap texel target and the atlas's index map. A surface whose material is double-sided gets a PAIR of records
// (front, then back): one UV, two sides, two lights.
//
// The atlas also carries LOD 0's shading frame (its interpolated vertex normal and tangent, per covered texel): the
// pre-pass and lit.frag read it (lightmap_frame.glsl) in place of the drawn LOD's, so a coarse LOD's long triangles
// light and normal-map as the full surface does, and a LOD switch moves the silhouette, not the light.
internal sealed partial class SponzaLoop
{
    // The scene atlas's size, from the .blixlightmap files (every file of a scene shares it).
    private static int lightmapAtlasWidth, lightmapAtlasHeight;
    // What the bake rasterizes, captured as the shared buffers are built: each drawable with a lightmap.
    private readonly List<(string Name, byte[] VertexBytes, int Stride, int NormalOffset, int TangentOffset, uint[] Indices, Matrix4x4 World, Vector2[] Texels, bool DoubleSided)> lightmapSources = new();
    private GpuBufferHandle lightmapIndexMapBuffer;
    // Set 1's LightmapFrames (lightmap_frame.glsl): [0] the atlas size, then per record id + 1 its packed LOD 0
    // shading frame; and the index map again, under the lit interface's name for it.
    private GpuBufferHandle lightmapFramesBuffer;
    private bool lightmapTexels;
    private bool lightmapKeepBuried;
    // --lightmap-backface N: of 16 hemisphere rays, how many meeting a back face first bury a texel (4: a quarter).
    private int lightmapBackface = 4;
    private bool lightmapNoFrames;
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
    private (Vector4[] Texels, uint[] IndexMap, uint[] Frames, int Count)? BakeLightmapTexels(RayQueryScene scene, string cacheKey)
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
        // Per record (pairs share the front's): LOD 0's interpolated vertex normal and tangent, packed (PackFrame).
        uint[]? frames = null;
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
                    if (source.NormalOffset >= 0)
                    {
                        Vector3 Attribute(uint v, int offset) => Vector3.TransformNormal(new Vector3(
                            BitConverter.ToSingle(source.VertexBytes, (int)v * stride + offset),
                            BitConverter.ToSingle(source.VertexBytes, (int)v * stride + offset + 4),
                            BitConverter.ToSingle(source.VertexBytes, (int)v * stride + offset + 8)), source.World);
                        var shading = Attribute(idx[t], source.NormalOffset) * wa + Attribute(idx[t + 1], source.NormalOffset) * wb + Attribute(idx[t + 2], source.NormalOffset) * wc;
                        var tangent = Vector3.UnitX;
                        var handed = 1f;
                        if (source.TangentOffset >= 0)
                        {
                            tangent = Attribute(idx[t], source.TangentOffset) * wa + Attribute(idx[t + 1], source.TangentOffset) * wb + Attribute(idx[t + 2], source.TangentOffset) * wc;
                            handed = BitConverter.ToSingle(source.VertexBytes, (int)idx[t] * stride + source.TangentOffset + 12) < 0f ? -1f : 1f;
                        }
                        if (shading.LengthSquared() > 1e-12f)
                        {
                            (frames![2 * id], frames[2 * id + 1]) = PackFrame(Vector3.Normalize(shading), tangent, handed);
                            if (source.DoubleSided)
                            {
                                var agree = Vector3.Dot(Vector3.Normalize(shading), n);
                                if (agree < 0f) pairedOpposed++;
                                else if (agree < 0.3f) pairedGrazing++;
                            }
                        }
                    }
                    if (source.DoubleSided)
                    {
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
                frames = new uint[records * 2];
            }
        }
        // Sides inside a solid -- where one piece runs on into another, or a slab's inner face -- are buried: gathered,
        // they read the dark inside and a bilinear lookup spreads it onto the visible texels beside them (a crisp black
        // square a texel wide, where one of a pixel's four is). A lightmap baker's backface tolerance: buried when
        // lightmapBackface of 16 hemisphere rays meet a back face first. The hashed cook's rule (a ray along the normal
        // meeting one, then 15 of 16) left the texels at a joint -- the end of a wall running into the next, whose normal
        // ray goes out free while its slanting ones go into the solid -- gathering half dark: Sponza's upper walls showed
        // them as black squares along every such edge. A buried record keeps its place (and its pair's sign) with |w| 1
        // instead of 4; texel.glsl and the filter skip it, and a pixel's other texels or the coarser level answer.
        // --lightmap-keep-buried keeps them all.
        // The test is 9.8M rays and more on Sponza (~170 s), so its answer is kept beside the transport cache, under the
        // same key (which names the scene and these .blixlightmap files).
        long buried = 0;
        var buriedCached = false;
        if (!lightmapKeepBuried)
        {
            var count = (int)records;
            var flags = ReadBuriedCache(cacheKey, count, lightmapBackface);
            buriedCached = flags is not null;
            flags ??= new bool[count];
            if (!buriedCached)
            Parallel.For(0, count, k =>
            {
                var p = new Vector3(texels![2 * k].X, texels[2 * k].Y, texels[2 * k].Z);
                var n = new Vector3(texels[2 * k + 1].X, texels[2 * k + 1].Y, texels[2 * k + 1].Z);
                var origin = p + n * 1e-3f;
                var r = new Random(173 + k);
                var back = 0;
                const int tests = 16;
                for (var t = 0; t < tests && back < lightmapBackface; t++)
                    if (scene.Closest(new Ray(origin, CosineHemisphereCpu(n, r)), 0f, float.PositiveInfinity, (uint)r.Next()) is { FrontFace: false }) back++;
                flags[k] = back >= lightmapBackface;
            });
            if (!buriedCached) WriteBuriedCache(cacheKey, flags, lightmapBackface);
            for (var k = 0; k < count; k++)
                if (flags[k]) { texels![2 * k].W = MathF.Sign(texels[2 * k].W); buried++; }
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
            $"[VulkanSponza] lightmap texels: {covered:N0} atlas texels covered ({paired:N0} of them double-sided, two records each), {gutter:N0} gutter texels pointing at a neighbour; {w} x {h} atlas; {records * LightmapRecordBytes / 1048576.0:N0} MB of texel state; {(lightmapKeepBuried ? "buried sides kept (--lightmap-keep-buried)" : string.Create(CultureInfo.InvariantCulture, $"{buried:N0} records buried in a solid ({buried / (double)Math.Max(1, records):P1}), skipped{(buriedCached ? ", from the cache" : "")}"))}; baked in {clock.Elapsed.TotalSeconds:0.0} s."));
        if (paired > 0)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] lightmap double-sided texels: {pairedOpposed / (double)paired:P1} have vertex normals opposite their winding, {pairedGrazing / (double)paired:P1} within ~17 deg of edge-on (a pixel picks its side by the vertex normal)."));
        return (texels!, map, frames!, (int)records);
    }

    // A shading frame in two words: the normal octahedral as two snorm16s, the tangent the same with its handedness in
    // the lowest bit of the first (lightmap_frame.glsl unpacks both).
    private static (uint Normal, uint Tangent) PackFrame(Vector3 normal, Vector3 tangent, float handed)
    {
        static uint Oct(Vector3 v)
        {
            v /= MathF.Abs(v.X) + MathF.Abs(v.Y) + MathF.Abs(v.Z);
            var o = v.Z >= 0f ? new Vector2(v.X, v.Y)
                : new Vector2((1f - MathF.Abs(v.Y)) * (v.X >= 0f ? 1f : -1f), (1f - MathF.Abs(v.X)) * (v.Y >= 0f ? 1f : -1f));
            static uint Snorm(float f) => (uint)(ushort)(short)MathF.Round(Math.Clamp(f, -1f, 1f) * 32767f);
            return Snorm(o.X) | (Snorm(o.Y) << 16);
        }
        // The tangent, made perpendicular to the normal (Gram-Schmidt, as lit.frag does); any perpendicular when it is not.
        var t = tangent - normal * Vector3.Dot(normal, tangent);
        if (t.LengthSquared() < 1e-12f) t = MathF.Abs(normal.X) < 0.9f ? Vector3.Cross(normal, Vector3.UnitX) : Vector3.Cross(normal, Vector3.UnitY);
        var packed = Oct(Vector3.Normalize(t)) & ~1u;
        return (Oct(normal), packed | (handed < 0f ? 1u : 0u));
    }

    private static string BuriedCachePath(string cacheKey) => Path.ChangeExtension(TransportCachePath(cacheKey), ".lightmap-buried");

    private const int BuriedCacheVersion = 2;

    private static bool[]? ReadBuriedCache(string cacheKey, int count, int backface)
    {
        var path = BuriedCachePath(cacheKey);
        if (!File.Exists(path)) return null;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadString() != "blix-lightmap-buried" || r.ReadInt32() != BuriedCacheVersion || r.ReadInt32() != count || r.ReadInt32() != backface) return null;
            var bits = r.ReadBytes((count + 7) / 8);
            if (bits.Length != (count + 7) / 8) return null;
            var flags = new bool[count];
            for (var k = 0; k < count; k++) flags[k] = (bits[k >> 3] & (1 << (k & 7))) != 0;
            return flags;
        }
        catch (IOException) { return null; }
    }

    private static void WriteBuriedCache(string cacheKey, bool[] flags, int backface)
    {
        var bits = new byte[(flags.Length + 7) / 8];
        for (var k = 0; k < flags.Length; k++) if (flags[k]) bits[k >> 3] |= (byte)(1 << (k & 7));
        var path = BuriedCachePath(cacheKey);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var w = new BinaryWriter(File.Create(path));
            w.Write("blix-lightmap-buried");
            w.Write(BuriedCacheVersion);
            w.Write(flags.Length);
            w.Write(backface);
            w.Write(bits);
        }
        catch (IOException e) { Console.WriteLine($"[VulkanSponza] lightmap: buried cache not written ({e.Message})."); }
    }

    // Both kinds in one set of texel buffers: the hashed records first (the hash table names them, as without a
    // lightmap), then the atlas's from lightmapFirstRecord (the index map names them, shifted past the hashed ones).
    // Every pass that reads a record by id reads either kind; the filter tells them apart by id, mark and lookup by
    // whether the pixel has an atlas texel (texel.glsl).
    private void UploadLightmapTexels(TexelBake hashed, (Vector4[] Texels, uint[] IndexMap, uint[] Frames, int Count) atlas,
        TexelLightState? light = null, string? bakePath = null)
    {
        var first = hashed.Count;
        var texels = new Vector4[hashed.Texels.Length + atlas.Texels.Length];
        hashed.Texels.CopyTo(texels, 0);
        atlas.Texels.CopyTo(texels, hashed.Texels.Length);
        var map = atlas.IndexMap;
        for (var i = 0; i < map.Length; i++)
            if (map[i] != 0xFFFFFFFFu) map[i] += (uint)first;
        UploadTexels(hashed with { Texels = texels, Count = first + atlas.Count }, light, bakePath, first);
        lightmapIndexMapBuffer = Own(device.CreateGpuBuffer(map.Length * 4, MemoryMarshal.AsBytes(map.AsSpan()), "sponza.lightmap.index-map"));
        // The frames by absolute record id, after a one-entry header with the atlas size (zero: none).
        var frames = new uint[2 * (1 + first + atlas.Count)];
        frames[0] = (uint)lightmapAtlasWidth;
        frames[1] = (uint)lightmapAtlasHeight;
        atlas.Frames.CopyTo(frames, 2 * (1 + first));
        if (!lightmapNoFrames)
            lightmapFramesBuffer = Own(device.CreateGpuBuffer(frames.Length * 4, MemoryMarshal.AsBytes(frames.AsSpan()), "sponza.lightmap.frames"));
        // The lit interface's bindings are cached by name (LitSceneBuffers): rebuilt with these.
        litSceneBuffers = null;
        litClipmapBuffer = null;
        lightmapFirstRecord = first;
        lightmapTexels = true;
        UploadTransferWeights(first, atlas.Count);
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

    // What the lit interface binds (set 1, lightmap_frame.glsl): the index map and the frames, or placeholders whose
    // zero header says there is no atlas.
    private IEnumerable<ShaderBufferBinding> LitLightmapBuffers()
    {
        if (lightmapFramesBuffer.Equals(default(GpuBufferHandle)))
            lightmapFramesBuffer = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.lightmap.frames.none"));
        return new[] { new ShaderBufferBinding("LightmapCells", LightmapIndexMap()), new ShaderBufferBinding("LightmapFrames", lightmapFramesBuffer) };
    }

    private GpuBufferHandle LightmapIndexMap()
    {
        if (lightmapIndexMapBuffer.Equals(default(GpuBufferHandle)))
            lightmapIndexMapBuffer = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.lightmap.index-map.none"));
        return lightmapIndexMapBuffer;
    }
}
