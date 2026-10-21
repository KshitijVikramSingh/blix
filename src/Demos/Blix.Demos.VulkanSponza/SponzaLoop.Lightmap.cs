using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;

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
    // Set 1's LightmapFrames (lightmap_frame.glsl): [0] the atlas size, then per record id + 1 its packed LOD 0
    // shading frame; and the index map again, under the lit interface's name for it.
    private GpuBufferHandle lightmapFramesBuffer;
    private bool lightmapTexels;
    // --lightmap-backface N: of 16 hemisphere rays, how many meeting a back face first bury a texel (4: a quarter).
    private int lightmapBackface = 4;
    // --lightmap-buried-reach M: a back face counts only this near (0.3 m). Open-bottomed stone resting on a capital
    // let a visible texel's slanting rays into the arch above, its inside faces ~0.5-1 m up, and buried it; a texel
    // gone past a joint into the next solid meets that solid's faces close by, and stays buried.
    private float lightmapBuriedReach = 0.3f;
    // --lightmap-buried-lift M: how far off its texel the test's rays start. Against the bake scene (full detail, the
    // surfaces the texels sit on) a millimetre. Against the frame's scene, simplified up to rayLodError, the LOD error
    // and a millimetre: on a concave curve its chords pass in front of LOD 0, and a texel lifted 1 mm started behind its
    // own mesh's coarse surface and was buried in plain view (the red tears along Sponza's arches).
    private float? lightmapBuriedLiftAsked;
    private float LightmapBuriedLift => lightmapBuriedLiftAsked ?? (BakeSceneWanted ? 0.001f : rayLodError + 0.001f);
    // --lightmap-buried-why: for buried records near the camera, what their test rays met (SponzaLoop.Lightmap.cs).
    private bool lightmapBuriedWhy;
    // The test's rays a record: 64 where Metal answers them (the edge between kept and buried was speckled at 16), 16
    // on the CPU; the threshold the same share of them as lightmapBackface is of 16.
    private int LightmapBuriedTests => FrameRaysOnHardware ? 64 : 16;
    private int LightmapBuriedThreshold => (LightmapBuriedTests * lightmapBackface + 15) / 16;
    // Enclosed: also buried when three quarters of this many more rays, at any distance, meet a back face first (not
    // all: where that solid touches another, a ray reaching the contact meets two coplanar faces and the other's front
    // face answers about half the time -- the corner block against the right wall, floor and roof). A face pressed
    // flat against a solid -- the crevices' floor under its blocks, a wall against the next box, a lintel against its
    // slab -- looks into it, and a solid 1.5-4 m across has its far faces well past lightmapBuriedReach: such texels
    // gathered the inside of the solid, never settled, and sent rays onto inner faces with no texels or patches. A visible
    // texel under open-bottomed stone (what the reach is short for) has some rays that get out.
    // --lightmap-enclosed N: those rays (16; 0: not tested).
    private int lightmapEnclosedTests = 16;
    private int LightmapEnclosedTests => lightmapEnclosedTests;
    // What the buried flags depend on, for the caches that hold them or what was gathered with them.
    private string LightmapBuriedRule => string.Create(CultureInfo.InvariantCulture, $"{LightmapBuriedThreshold}/{LightmapBuriedTests} within {lightmapBuriedReach} m from {LightmapBuriedLift} m off, single-sided; {(LightmapEnclosedTests > 0 ? $"enclosed {LightmapEnclosedTests * 3 / 4}/{LightmapEnclosedTests}; " : "")}contacts as back faces (the table, then 4 jittered retries where a triangle has partners); edges inset {LightmapEdgeInset} m");
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
        lightmapRecordsKey = cacheKey;
        var stored = LoadStage(cacheKey, "lightmap-records", r => r.ReadString() != LightmapRecordsRule ? null
            : new LightmapRecords(ReadArray<Vector4>(r), ReadArray<uint>(r), ReadArray<uint>(r), r.ReadInt32()));
        if (stored is { } done) return (done.Texels, done.IndexMap, done.Frames, done.Count);
        var baked = BakeLightmapTexelsNow(scene, cacheKey);
        if (baked is { } b)
            SaveStage(cacheKey, "lightmap-records", wr =>
            {
                wr.Write(LightmapRecordsRule);
                WriteArray(wr, b.Texels); WriteArray(wr, b.IndexMap); WriteArray(wr, b.Frames); wr.Write(b.Count);
            });
        return baked;
    }

    // The lightmap's records as the bake leaves them -- rasterized, gutter filled, buried and moved off contacts -- a
    // stage of the GI bundle (--cook-gi): what they depend on beyond the texels key (which names the scene, its
    // .blixlightmap files and the texel settings).
    private sealed record LightmapRecords(Vector4[] Texels, uint[] IndexMap, uint[] Frames, int Count);
    private const int LightmapRecordsVersion = 1;
    private string? lightmapRecordsKey;
    private string LightmapRecordsRule => string.Create(CultureInfo.InvariantCulture,
        $"records v{LightmapRecordsVersion};atlas {lightmapAtlasWidth}x{lightmapAtlasHeight};{LightmapBuriedRule};contacts {ContactRays} {ContactShare} {ContactStep};budget {LightmapRecordBudgetBytes}");

    private (Vector4[] Texels, uint[] IndexMap, uint[] Frames, int Count)? BakeLightmapTexelsNow(RayQueryScene scene, string cacheKey)
    {
        var clock = Stopwatch.StartNew();
        int w = lightmapAtlasWidth, h = lightmapAtlasHeight;
        var map = new uint[(long)w * h];
        Array.Fill(map, 0xFFFFFFFFu);
        // Records per source, to say where they went when the budget refuses.
        var perSource = new long[lightmapSources.Count];
        long records = 0, covered = 0, paired = 0, edgeCovered = 0;
        // Double-sided texels whose stored face normal (the triangle's winding) disagrees with the interpolated vertex
        // normal -- what a pixel's shading normal is picked against (texelsAroundLightmap) -- and those near edge-on.
        long pairedOpposed = 0, pairedGrazing = 0;
        Vector4[]? texels = null;
        // Per record (pairs share the front's): LOD 0's interpolated vertex normal and tangent, packed (PackFrame).
        uint[]? frames = null;
        // Four passes over the same triangles in the same order: claim the texels whose centre a triangle covers, then
        // (conservative) the still-empty texels a triangle overlaps; fill the first kind, then the second. Centres alone
        // left a chart thinner than a texel -- an arch rim's moulding strip -- without one record: nothing for the
        // gutter to spread, its pixels black, or a neighbouring chart's light (the tears along Sponza's arches). An
        // overlap texel's record is the triangle's point nearest its centre.
        for (var pass = 0; pass < 4; pass++)
        for (var si = 0; si < lightmapSources.Count; si++)
        {
            var source = lightmapSources[si];
            var edges = (pass & 1) == 1;
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
                    var inside = wa >= -1e-4f && wb >= -1e-4f && wc >= -1e-4f;
                    if (!edges && !inside) continue;
                    if (edges)
                    {
                        // Overlapping the texel's square (within its half diagonal of the centre), at the nearest point.
                        (wa, wb, wc) = NearestBarycentric(p, a, b, c);
                        if (Vector2.DistanceSquared(a * wa + b * wb + c * wc, p) > 0.5f) continue;
                    }
                    var cell = (long)y * w + x;
                    if (pass < 2)
                    {
                        // Claim: the first triangle to reach a texel owns it (the second pass meets them in the same
                        // order). Counted past uint's reach rather than wrapped: the guard reads the long.
                        if (map[cell] != 0xFFFFFFFFu) continue;
                        var size = source.DoubleSided ? 2 : 1;
                        map[cell] = records < uint.MaxValue - 2 ? (uint)records : 0xFFFFFFFEu;
                        records += size;
                        perSource[si] += size;
                        covered++;
                        if (edges) edgeCovered++;
                        if (source.DoubleSided) paired++;
                        continue;
                    }
                    // Fill: the record this texel's owner claimed, written once (w is +-4 once written, 0 before).
                    var id = map[cell];
                    if (texels![2 * id].W != 0f) continue;
                    var pos = Inset(pa, pb, pc, wa, wb, wc);
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
            if (pass == 1 && si == lightmapSources.Count - 1)
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
        // lightmapBackface in 16 of its hemisphere rays meet a back face first, within lightmapBuriedReach (LightmapBuriedRule). The hashed cook's rule (a ray along the normal
        // meeting one, then 15 of 16) left the texels at a joint -- the end of a wall running into the next, whose normal
        // ray goes out free while its slanting ones go into the solid -- gathering half dark: Sponza's upper walls showed
        // them as black squares along every such edge. A buried record keeps its place (and its pair's sign) with |w| 1
        // instead of 4; texel.glsl and the filter skip it, and a pixel's other texels or the coarser level answer.
        // Only a single-sided triangle has an inside: a double-sided one's back face
        // is the far side of a sheet (a curtain's next fold), and counting it buried 57% of the curtains' texels and
        // the pillar and floor texels beside them -- skipped, the survivors stretched over them in hard-edged blocks.
        // The test is 9.8M rays and more on Sponza (~170 s), so its answer is kept beside the transport cache, under the
        // same key (which names the scene and these .blixlightmap files).
        long buried = 0;
        var buriedCached = false;
        {
            var count = (int)records;
            var flags = ReadBuriedCache(cacheKey, count, LightmapBuriedRule);
            buriedCached = flags is not null;
            flags ??= new bool[count];
            // On Metal when frame rays are (the frame tracer, built a few frames in; this runs on the cook's thread):
            // ~210 M rays in about a second, against ~3 minutes on the CPU.
            var buriedClock = Stopwatch.StartNew();
            var metal = buriedCached ? null : WaitForBakeTracer();
            if (metal is not null)
            {
                ProgressBegin("lightmap: buried test (Metal)", 0);
                // The contact table by the rows of the data this tracer was built from (the bake scene's or the frame's).
                var onBake = ReferenceEquals(metal, BakeTracer);
                (int[][] Map, int Rows)? rowMap = onBake ? bakeSceneRowMap : rayGpuData is { } frameData ? (frameData.RowMap, frameData.RowPlacements.Length) : null;
                var (contactStart, contactTris) = rowMap is { } rm ? GpuContacts(scene, rm) : (Array.Empty<uint>(), Array.Empty<Vector4>());
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] lightmap buried test: contact table for Metal, by the {(onBake ? "bake scene's" : "frame's")} rows: {contactTris.Length / 3:N0} partners over {Math.Max(0, contactStart.Length - 1):N0} rows."));
                flags = metal.TestBuried(texels.AsSpan(0, count * 2), LightmapBuriedTests, LightmapBuriedThreshold, lightmapBuriedReach, LightmapBuriedLift, 173u,
                    CollectionsMarshal.AsSpan(rayPlacementDoubleSided), LightmapEnclosedTests, contactStart, contactTris, out var buriedGpuMs);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] lightmap buried test on Metal ({LightmapBuriedRule}): {count:N0} records in {buriedClock.Elapsed.TotalSeconds:0.0} s ({buriedGpuMs:0} ms GPU)."));
                ProgressEnd("lightmap: buried test (Metal)");
                if (lightmapBuriedWhy && LightmapEnclosedTests > 0)
                {
                    var without = metal.TestBuried(texels.AsSpan(0, count * 2), LightmapBuriedTests, LightmapBuriedThreshold, lightmapBuriedReach, LightmapBuriedLift, 173u,
                        CollectionsMarshal.AsSpan(rayPlacementDoubleSided), 0, contactStart, contactTris, out _);
                    ExplainEnclosed(scene, texels, flags, without);
                    // Metal against the CPU (same rules, same contact table and jitter; their own random rays): the
                    // records they disagree on, of a sample.
                    var step = Math.Max(1, count / 20_000);
                    int sampled = 0, metalOnly = 0, cpuOnly = 0;
                    var flagsNow = flags;
                    Parallel.For(0, (count + step - 1) / step, s =>
                    {
                        var k = s * step;
                        var cpu = BuriedOnCpu(scene, texels, k);
                        Interlocked.Increment(ref sampled);
                        if (flagsNow[k] && !cpu) Interlocked.Increment(ref metalOnly);
                        if (cpu && !flagsNow[k]) Interlocked.Increment(ref cpuOnly);
                    });
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"[VulkanSponza] lightmap buried, Metal against the CPU ({sampled:N0} records): buried by Metal alone {metalOnly:N0}, by the CPU alone {cpuOnly:N0}."));
                }
            }
            else if (!buriedCached)
                Parallel.For(0, count, k => flags[k] = BuriedOnCpu(scene, texels!, k));
            if (!buriedCached) WriteBuriedCache(cacheKey, flags, LightmapBuriedRule);
            if (lightmapBuriedWhy) ExplainBuried(scene, texels!, flags);
            for (var k = 0; k < count; k++)
                if (flags[k]) { texels![2 * k].W = MathF.Sign(texels[2 * k].W); buried++; }
        }
        var (moved, buriedAfter) = LiftOffContacts(scene, texels!, map, w, h);
        buried += buriedAfter;
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
            $"[VulkanSponza] lightmap texels: {covered:N0} atlas texels covered ({edgeCovered:N0} by a triangle's edge, not its centre; {paired:N0} double-sided, two records each), {gutter:N0} gutter texels pointing at a neighbour; {w} x {h} atlas; {records * LightmapRecordBytes / 1048576.0:N0} MB of texel state; {buried:N0} records buried in a solid ({buried / (double)Math.Max(1, records):P1}), skipped{(buriedCached ? ", from the cache" : "")}; baked in {clock.Elapsed.TotalSeconds:0.0} s."));
        if (paired > 0)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] lightmap double-sided texels: {pairedOpposed / (double)paired:P1} have vertex normals opposite their winding, {pairedGrazing / (double)paired:P1} within ~17 deg of edge-on (a pixel picks its side by the vertex normal)."));
        return (texels!, map, frames!, (int)records);
    }

    // Records where another solid touches their face: the edge inset (Inset) keeps a record off its own triangle's edges,
    // but a wall's face runs on behind the next box that rests against it, and a texel centred on that line -- the crevices'
    // corridor, where its side walls meet its end wall and roof -- started half its gather rays inside the box. The buried
    // test counts back faces within lightmapBuriedReach and passed it (most of those rays run further inside first); the
    // cook met them all, flipped them, and read the sky-lit outside: a bright texel at every joint (2026-10-16).
    // So each single-sided record beside a buried one sends ContactRays rays from where the cook starts them (1 cm off);
    // where ContactShare of them meet a single-sided back face at any distance, the record moves ContactStep across its
    // face, away from them, and is tested again; still inside, it is buried.
    private const int ContactRays = 32;
    private const float ContactShare = 1f / 8f, ContactStep = 0.02f;
    private (int Moved, int Buried) LiftOffContacts(RayQueryScene scene, Vector4[] texels, uint[] map, int w, int h)
    {
        bool Buried(uint id) => MathF.Abs(texels[2 * id].W) < 2f;
        var candidates = new List<uint>();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var id = map[(long)y * w + x];
            if (id == 0xFFFFFFFFu || texels[2 * id].W < 2f) continue;   // none, buried, or a double-sided pair
            var beside = false;
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                var other = map[(long)ny * w + nx];
                if (other != 0xFFFFFFFFu && Buried(other)) { beside = true; break; }
            }
            if (beside) candidates.Add(id);
        }
        // The share of the record's rays meeting a single-sided back face, and their mean direction.
        (float Share, Vector3 Toward) Inside(Vector3 pos, Vector3 n)
        {
            var t1 = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, n));
            var t2 = Vector3.Cross(n, t1);
            var origin = pos + n * 0.01f;
            int inside = 0;
            var toward = Vector3.Zero;
            for (var k = 0; k < ContactRays; k++)
            {
                float u1 = (k + 0.5f) / ContactRays, u2 = (k * 0.6180339887f) % 1f;
                float r = MathF.Sqrt(u1), phi = 2f * MathF.PI * u2;
                var d = Vector3.Normalize(t1 * (r * MathF.Cos(phi)) + t2 * (r * MathF.Sin(phi)) + n * MathF.Sqrt(MathF.Max(0f, 1f - u1)));
                if (scene.Closest(new Ray(origin, d)) is not { } hit || hit.FrontFace) continue;
                if (hit.Instance < rayPlacementDoubleSided.Count && rayPlacementDoubleSided[hit.Instance]) continue;
                inside++;
                toward += d;
            }
            return ((float)inside / ContactRays, toward);
        }
        int moved = 0, buried = 0;
        var clock = Stopwatch.StartNew();
        Parallel.ForEach(candidates, id =>
        {
            var pos = new Vector3(texels[2 * id].X, texels[2 * id].Y, texels[2 * id].Z);
            var n = Vector3.Normalize(new Vector3(texels[2 * id + 1].X, texels[2 * id + 1].Y, texels[2 * id + 1].Z));
            var (share, toward) = Inside(pos, n);
            if (share < ContactShare) return;
            var across = toward - n * Vector3.Dot(toward, n);
            if (across.LengthSquared() > 1e-8f)
            {
                var to = pos - Vector3.Normalize(across) * ContactStep;
                if (Inside(to, n).Share < ContactShare)
                {
                    texels[2 * id] = new Vector4(to, texels[2 * id].W);
                    Interlocked.Increment(ref moved);
                    return;
                }
            }
            texels[2 * id].W = MathF.Sign(texels[2 * id].W);
            Interlocked.Increment(ref buried);
        });
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] lightmap contacts: {candidates.Count:N0} records beside buried ones tested ({ContactRays} rays each, {clock.Elapsed.TotalSeconds:0.0} s); {moved:N0} moved {ContactStep * 100f:0} cm off a solid touching their face, {buried:N0} buried."));
        return (moved, buried);
    }

    // The barycentrics of the point of triangle abc nearest p (in the atlas's plane): Ericson's region tests.
    // A record's point, kept LightmapEdgeInset off each of its triangle's edges (or at its centroid, when narrower): a
    // texel at a chart's edge took the triangle's nearest point, on the edge itself, and where the edge meets another wall
    // at a corner its rays started in that wall's plane and went through it -- the thin wall's sealed room read the sky
    // outside at every corner (2026-10-13). Each weight floored at the inset over the triangle's height across from its
    // vertex, the rest spread as it was.
    // Half a texel (the atlas's 4 cm): an edge texel stands for the light as far in as its neighbours are apart. At 1 cm a
    // crease's edge texels gathered with half their hemisphere the other wall, ~3x darker than a pixel 2-3 cm out reads,
    // and the creases came out in jagged dark bands (crevices' seam energy 0.955 -> 0.978, its excess 0.0165 -> 0.0126,
    // the leak unchanged, 2026-10-21). --lightmap-edge-inset M.
    private static float LightmapEdgeInset = 0.02f;
    private static Vector3 Inset(Vector3 pa, Vector3 pb, Vector3 pc, float wa, float wb, float wc)
    {
        var twiceArea = Vector3.Cross(pb - pa, pc - pa).Length();
        float Floor(Vector3 e) => MathF.Min(LightmapEdgeInset * e.Length() / MathF.Max(twiceArea, 1e-12f), 1f / 3f);
        float fa = Floor(pc - pb), fb = Floor(pa - pc), fc = Floor(pb - pa);
        float ra = MathF.Max(wa - fa, 0f), rb = MathF.Max(wb - fb, 0f), rc = MathF.Max(wc - fc, 0f);
        var rest = 1f - fa - fb - fc;
        var sum = ra + rb + rc;
        if (sum > 1e-12f) { ra *= rest / sum; rb *= rest / sum; rc *= rest / sum; }
        else ra = rb = rc = rest / 3f;
        return pa * (fa + ra) + pb * (fb + rb) + pc * (fc + rc);
    }

    private static (float A, float B, float C) NearestBarycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector2.Dot(ab, ap), d2 = Vector2.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return (1f, 0f, 0f);
        var bp = p - b;
        float d3 = Vector2.Dot(ab, bp), d4 = Vector2.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return (0f, 1f, 0f);
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) { var v = d1 / (d1 - d3); return (1f - v, v, 0f); }
        var cp = p - c;
        float d5 = Vector2.Dot(ab, cp), d6 = Vector2.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return (0f, 0f, 1f);
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) { var u = d2 / (d2 - d6); return (1f - u, 0f, u); }
        var va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) { var u = (d4 - d3) / (d4 - d3 + (d5 - d6)); return (0f, 1f - u, u); }
        var denom = 1f / (va + vb + vc);
        float bv = vb * denom, cw = vc * denom;
        return (1f - bv - cw, bv, cw);
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

    // --lightmap-buried-why: up to 3,000 buried records within 12 m of the camera that it sees, their test rays again on the CPU: whose
    // back faces they met and how far, and a probe straight behind each (2 cm) -- a back face there means the record's
    // normal points into its own surface (the winding against the vertex normals), a front face that it points out.
    private void ExplainBuried(RayQueryScene scene, Vector4[] texels, bool[] flags)
    {
        var eye = cameraPosition;
        var picked = new List<int>();
        for (var k = 0; k < flags.Length && picked.Count < 3000; k++)
        {
            if (!flags[k]) continue;
            var p = new Vector3(texels[2 * k].X, texels[2 * k].Y, texels[2 * k].Z);
            var toward = p - eye;
            var d = toward.Length();
            if (d > 12f || d < 0.1f) continue;
            // Seen by the camera: the ray from the eye reaches the record's point (within 2 cm) on a front face.
            if (scene.Closest(new Ray(eye, toward / d), 0f, d + 0.02f, 1u) is { } seen && seen.T > d - 0.02f) picked.Add(k);
        }
        var byName = new Dictionary<string, int>();
        var distances = new int[5];   // < 1 cm, < 5 cm, < 10 cm, < 30 cm, beyond
        int behindBack = 0, behindFront = 0, behindNone = 0, hits = 0;
        foreach (var k in picked)
        {
            var p = new Vector3(texels[2 * k].X, texels[2 * k].Y, texels[2 * k].Z);
            var n = Vector3.Normalize(new Vector3(texels[2 * k + 1].X, texels[2 * k + 1].Y, texels[2 * k + 1].Z));
            var r = new Random(911 + k);
            for (var t = 0; t < 64; t++)
                if (scene.Closest(new Ray(p + n * LightmapBuriedLift, CosineHemisphereCpu(n, r)), 0f, lightmapBuriedReach, (uint)r.Next()) is { FrontFace: false } hit
                    && !rayPlacementDoubleSided[hit.Instance])
                {
                    hits++;
                    var name = rayPlacementNames[hit.Instance];
                    byName[name] = byName.GetValueOrDefault(name) + 1;
                    distances[hit.T < 0.01f ? 0 : hit.T < 0.05f ? 1 : hit.T < 0.1f ? 2 : hit.T < 0.3f ? 3 : 4]++;
                }
            var behind = scene.Closest(new Ray(p + n * 0.005f, -n), 0f, 0.02f, 1u);
            if (behind is null) behindNone++;
            else if (behind.Value.FrontFace) behindFront++;
            else behindBack++;
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] buried, why ({picked.Count:N0} records the camera sees within 12 m, {hits:N0} back-face hits): met at < 1 cm {distances[0]:N0}, < 5 cm {distances[1]:N0}, < 10 cm {distances[2]:N0}, < 30 cm {distances[3]:N0}; straight behind each: its own front face {behindFront:N0}, a back face {behindBack:N0}, nothing within 2 cm {behindNone:N0}."));
        foreach (var (name, c) in byName.OrderByDescending(x => x.Value).Take(12))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {c,7:N0}  {name}"));
    }

    // The buried test on the CPU, one record: as the Metal kernel (blix_buried), on the CPU scene with the same contact
    // table and jitter (ClosestThroughContacts).
    private bool BuriedOnCpu(RayQueryScene scene, Vector4[] texels, int k)
    {
        var p = new Vector3(texels[2 * k].X, texels[2 * k].Y, texels[2 * k].Z);
        var n = new Vector3(texels[2 * k + 1].X, texels[2 * k + 1].Y, texels[2 * k + 1].Z);
        if (n.LengthSquared() < 1e-12f) return false;
        n = Vector3.Normalize(n);
        var origin = p + n * LightmapBuriedLift;
        var r = new Random(173 + k);
        var back = 0;
        int tests = LightmapBuriedTests, threshold = LightmapBuriedThreshold;
        for (var t = 0; t < tests && back < threshold; t++)
            if (ClosestThroughContacts(scene, new Ray(origin, CosineHemisphereCpu(n, r)), 0f, lightmapBuriedReach, (uint)r.Next()) is { FrontFace: false } hit
                && !rayPlacementDoubleSided[hit.Instance]) back++;
        if (back >= threshold) return true;
        var escaped = 0;
        for (var t = 0; t < LightmapEnclosedTests; t++)
        {
            if (!(ClosestThroughContacts(scene, new Ray(origin, CosineHemisphereCpu(n, r)), 0f, float.PositiveInfinity, (uint)r.Next()) is { FrontFace: false } hit
                && !rayPlacementDoubleSided[hit.Instance])) escaped++;
            if (escaped * 4 > LightmapEnclosedTests) return false;
        }
        return LightmapEnclosedTests > 0;
    }

    // --lightmap-buried-why: the records only the enclosed rule buries, and of those the ones the camera sees (within
    // 12 m, facing it, nothing in the way), by the placement under them.
    private void ExplainEnclosed(RayQueryScene scene, Vector4[] texels, bool[] with, bool[] without)
    {
        var eye = cameraPosition;
        int only = 0, seen = 0;
        var byName = new Dictionary<string, int>();
        for (var k = 0; k < with.Length; k++)
        {
            if (!with[k] || without[k]) continue;
            only++;
            var p = new Vector3(texels[2 * k].X, texels[2 * k].Y, texels[2 * k].Z);
            var n = Vector3.Normalize(new Vector3(texels[2 * k + 1].X, texels[2 * k + 1].Y, texels[2 * k + 1].Z));
            var toward = p + n * 0.01f - eye;
            var d = toward.Length();
            if (d < 0.1f || d > 12f || Vector3.Dot(toward, n) >= 0f) continue;
            if (scene.Closest(new Ray(eye, toward / d), 0f, d, 1u) is not null) continue;
            seen++;
            var under = scene.Closest(new Ray(p + n * 0.01f, -n), 0f, 0.05f, 1u);
            var name = under is { } h ? rayPlacementNames[h.Instance] : "(nothing under it)";
            byName[name] = byName.GetValueOrDefault(name) + 1;
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] buried as enclosed alone: {only:N0} records; {seen:N0} of them in the camera's sight within 12 m."));
        foreach (var (name, c) in byName.OrderByDescending(x => x.Value).Take(12))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {c,7:N0}  {name}"));
        // Exposure (SponzaLoop.TransportDiag.cs): of what the enclosed rule alone buries; and, as the test's controls, of a
        // sample of the kept records (mostly exposed, if the test is sound) and of what the reach rule already buried.
        (Vector3, Vector3) At(int k) => (new Vector3(texels[2 * k].X, texels[2 * k].Y, texels[2 * k].Z),
            new Vector3(texels[2 * k + 1].X, texels[2 * k + 1].Y, texels[2 * k + 1].Z));
        List<(Vector3, Vector3)> Pick(Func<int, bool> which, int most)
        {
            var all = Enumerable.Range(0, with.Length).Where(which).ToArray();
            var step = Math.Max(1, all.Length / most);
            return all.Where((_, i) => i % step == 0).Select(At).ToList();
        }
        var dump = Environment.GetEnvironmentVariable("BLIX_DIAG_DUMP");
        ReportExposed("lightmap, buried as enclosed alone", scene, Pick(k => with[k] && !without[k], 1 << 30), dump is null ? null : dump + ".lightmap-enclosed");
        ReportExposed("lightmap, kept (control, a sample)", scene, Pick(k => !with[k] && MathF.Abs(texels[2 * k].W) >= 2f, 50_000));
        ReportExposed("lightmap, buried by the reach rule (control, a sample)", scene, Pick(k => without[k], 50_000));
    }

    // The frame's Metal tracer for work off the frame's thread: waited for while frame rays are on Metal and it is being
    // built (EnsureFrameTracer, a few frames after the surface bake); null otherwise, and the caller uses the CPU.
    private MetalRayTracer? WaitForFrameTracer()
    {
        if (!FrameRaysOnHardware) return null;
        // A cook on the frame's thread (--transport-wait) cannot wait for what the frame builds.
        if (!transportCookAsync) return Volatile.Read(ref frameTracer);
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref frameTracer) is null && !Volatile.Read(ref frameTracerFailed) && clock.Elapsed.TotalSeconds < 60) Thread.Sleep(20);
        return Volatile.Read(ref frameTracer);
    }

    private static string BuriedCachePath(string cacheKey) => Path.ChangeExtension(TransportCachePath(cacheKey), ".lightmap-buried");

    private const int BuriedCacheVersion = 4;

    private static bool[]? ReadBuriedCache(string cacheKey, int count, string rule)
    {
        var path = BuriedCachePath(cacheKey);
        if (!File.Exists(path)) return null;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadString() != "blix-lightmap-buried" || r.ReadInt32() != BuriedCacheVersion || r.ReadInt32() != count || r.ReadString() != rule) return null;
            var bits = r.ReadBytes((count + 7) / 8);
            if (bits.Length != (count + 7) / 8) return null;
            var flags = new bool[count];
            for (var k = 0; k < count; k++) flags[k] = (bits[k >> 3] & (1 << (k & 7))) != 0;
            return flags;
        }
        catch (IOException) { return null; }
    }

    private static void WriteBuriedCache(string cacheKey, bool[] flags, string rule)
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
            w.Write(rule);
            w.Write(bits);
        }
        catch (IOException e) { Console.WriteLine($"[VulkanSponza] lightmap: buried cache not written ({e.Message})."); }
    }

    // Both kinds in one set of texel buffers: the hashed records first (the hash table names them, as without a
    // lightmap), then the atlas's from lightmapFirstRecord (the index map names them, shifted past the hashed ones).
    // Every pass that reads a record by id reads either kind; the filter tells them apart by id, mark and lookup by
    // whether the pixel has an atlas texel (texel.glsl).
    private void UploadLightmapTexels(TexelBake hashed, (Vector4[] Texels, uint[] IndexMap, uint[] Frames, int Count) atlas,
        TexelLightState? light = null, string? bakePath = null, uint[]? resolved = null)
    {
        var first = hashed.Count;
        var texels = new Vector4[hashed.Texels.Length + atlas.Texels.Length];
        hashed.Texels.CopyTo(texels, 0);
        atlas.Texels.CopyTo(texels, hashed.Texels.Length);
        var map = atlas.IndexMap;
        for (var i = 0; i < map.Length; i++)
            if (map[i] != 0xFFFFFFFFu) map[i] += (uint)first;
        UploadTexels(hashed with { Texels = texels, Count = first + atlas.Count }, light, bakePath, first, resolved);
        if (splitLight || cookGi) BuildSeamNeighbours(texels, first + atlas.Count, first);
        UploadAtlasGather(map, texels, first, atlas.Count);
        // The frames by absolute record id, after a one-entry header with the atlas size (zero: none).
        var frames = new uint[2 * (1 + first + atlas.Count)];
        frames[0] = (uint)lightmapAtlasWidth;
        frames[1] = (uint)lightmapAtlasHeight;
        atlas.Frames.CopyTo(frames, 2 * (1 + first));
        lightmapFramesBuffer = Own(device.CreateGpuBuffer(frames.Length * 4, MemoryMarshal.AsBytes(frames.AsSpan()), "sponza.lightmap.frames"));
        // The lit interface's bindings are cached by name (LitSceneBuffers): rebuilt with these.
        litSceneBuffers = null;
        litClipmapBuffer = null;
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

    // What the lit interface binds (set 1, lightmap_frame.glsl): the index map and the frames, or placeholders whose
    // zero header says there is no atlas.
    private IEnumerable<ShaderBufferBinding> LitLightmapBuffers()
    {
        if (lightmapFramesBuffer.Equals(default(GpuBufferHandle)))
            lightmapFramesBuffer = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.lightmap.frames.none"));
        return new[] { new ShaderBufferBinding("LightmapFrames", lightmapFramesBuffer) };
    }

}
