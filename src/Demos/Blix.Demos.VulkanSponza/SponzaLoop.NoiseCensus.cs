using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --split-noise-census: where the split cook's noise comes from. Refining settled ~3% of 7 M texels a round, and neither
// judging the dark against the scene nor aiming rays at the brightest patches (MIS) moved how many the sweep settled
// (9,086,540 / 9,086,446 of 16 M, 2026-10-14). So each sweep pass's composed luminance is split by source
// (texel_nodes_shade.glsl, NodeCensus): the sky's misses, the bounce from a patch, the sun direct at the hits, the bounce a
// hit with no patch took. Each source's variance across passes says which one keeps a texel unsettled; printed when the
// sweep ends, the unsettled against the settled.
// Sponza, 2026-10-14 (512 sweep rays): the 6.95 M unsettled -- the sun at the hits 55% of the variance (the largest in 57%),
// the sky's misses 23%, the patches' bounce 20%, no-patch 1.5%; one ray's spread p50 3.0x the mean (5% wants ~3,600 rays).
// The 9.08 M settled are bounce and sky. Shaded texels seeing a little of the sunlit floor. The light lists counted only a
// patch's bounce (its direct sun is in its sunlets); counting the sunlets' sun onto their patches (BuildLights) changed
// nothing (8.99 M settled, the sun still 55%): reverted.
internal sealed partial class SponzaLoop
{
    private bool noiseCensus;
    private GpuBufferHandle sunNodeCensus;
    // [settled 0 / unsettled 1]: texels; each source's mean share of the variance and of the light; how often each is
    // the largest; the sources' variance summed over the whole's (1 when they are independent); the per-ray spread.
    private readonly long[] censusTexels = new long[2];
    private readonly double[,] censusVarianceShare = new double[2, 4];
    private readonly double[,] censusLightShare = new double[2, 4];
    private readonly long[,] censusDominant = new long[2, 4];
    private readonly double[] censusIndependence = new double[2];
    private readonly List<float>[] censusRaySpread = { new(), new() };
    // By the ray's length (the short-ray design: past R a ray would read its patch): [settled/unsettled, R 0.5/1/2 m] the
    // light's share and the variance's share from beyond R; texels whose near-only noise passes the bar (as if beyond R
    // were noise-free) and texels with nothing within R at all; the sun at hits beyond 1 m's variance share.
    private static readonly float[] CensusRadii = { 0.5f, 1f, 2f };
    private readonly double[,] censusFarLight = new double[2, 3];
    private readonly double[,] censusFarVariance = new double[2, 3];
    private readonly double[,] censusNearVariance = new double[2, 3];
    private readonly long[,] censusNearSettles = new long[2, 3];
    private readonly long[,] censusNearEmpty = new long[2, 3];
    private readonly double[] censusFarSunVariance = new double[2];
    // The near field's split by what fills a texel's view (with the far field: its reach): the mesh's own (its patch's
    // placement) against another primitive's -- what an asset cook could carry, what only the scene knows.
    private readonly double[] censusView = new double[4];          // near own, near other, past R, escaped
    private readonly long[] censusContact = new long[3];            // texels whose view is > 1% / 5% / 20% another's near
    private double censusOwnPatches, censusOtherPatches, censusOtherPasses, censusReach, censusNearOtherOfNear;
    private long censusViewTexels, censusNearTexels;
    // The primitives merged into objects (placements sharing a welded vertex): another primitive of the same object (an
    // authoring fragment) against another object -- what placement really adds.
    private GpuBufferHandle censusObjects;
    private int censusPlacements;
    private readonly double[] censusObjectView = new double[2];
    private readonly long[] censusObjectContact = new long[3];
    private double censusOtherObjectPasses, censusNearOtherObjectOfNear;

    private void BeginNoiseCensus()
    {
        if (!noiseCensus || !splitCooking) return;
        sunNodeCensus = Own(device.CreateGpuBuffer(checked(sunNodeSlice * 144), name: "sponza.sun-nodes.census"));
        Array.Clear(censusTexels);
        Array.Clear(censusVarianceShare);
        Array.Clear(censusLightShare);
        Array.Clear(censusDominant);
        Array.Clear(censusIndependence);
        foreach (var l in censusRaySpread) l.Clear();
        Array.Clear(censusFarLight);
        Array.Clear(censusFarVariance);
        Array.Clear(censusNearVariance);
        Array.Clear(censusNearSettles);
        Array.Clear(censusNearEmpty);
        Array.Clear(censusFarSunVariance);
        Array.Clear(censusView);
        Array.Clear(censusContact);
        censusOwnPatches = censusOtherPatches = censusOtherPasses = censusReach = censusNearOtherOfNear = 0;
        censusViewTexels = censusNearTexels = 0;
        Array.Clear(censusObjectView);
        Array.Clear(censusObjectContact);
        censusOtherObjectPasses = censusNearOtherObjectOfNear = 0;
        if (censusObjects.Equals(default(GpuBufferHandle))) BuildCensusObjects();
    }

    private bool CensusOn(int discover) =>
        noiseCensus && splitCooking && discover == 0 && sunNodeRefineRound == 0 && !sunNodeCensus.Equals(default(GpuBufferHandle));

    // SunNodeReadBack, a sweep slice: each texel's sources against its whole.
    private void ReadNoiseCensus(Vector4[] sums, int count, int suns, int passes)
    {
        if (!noiseCensus || sunNodeCensus.Equals(default(GpuBufferHandle))) return;
        var census = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(sunNodeCensus, 0, count * 144).AsSpan()).ToArray();
        for (var t = 0; t < count; t++)
        {
            float total = 0f;
            for (var k = 0; k < suns; k++) total += Luminance(new Vector3(sums[(long)t * suns + k].X, sums[(long)t * suns + k].Y, sums[(long)t * suns + k].Z));
            var mean = total / passes;
            var whole = MathF.Max(sums[(long)t * suns].W / passes - mean * mean, 0f);
            var s = census[9 * t];
            var q = census[9 * t + 1];
            var m = s / passes;
            var v = Vector4.Max(q / passes - m * m, Vector4.Zero);
            var vSum = v.X + v.Y + v.Z + v.W;
            var bar = texelNoise * MathF.Max(mean, MathF.Max(TexelNoiseFloor, sunNodeDarkBar));
            var unsettled = MathF.Sqrt(whole / passes) > bar ? 1 : 0;
            censusTexels[unsettled]++;
            if (vSum > 0f)
            {
                censusVarianceShare[unsettled, 0] += v.X / vSum;
                censusVarianceShare[unsettled, 1] += v.Y / vSum;
                censusVarianceShare[unsettled, 2] += v.Z / vSum;
                censusVarianceShare[unsettled, 3] += v.W / vSum;
                var top = v.X >= v.Y && v.X >= v.Z && v.X >= v.W ? 0 : v.Y >= v.Z && v.Y >= v.W ? 1 : v.Z >= v.W ? 2 : 3;
                censusDominant[unsettled, top]++;
                if (whole > 0f) censusIndependence[unsettled] += Math.Min(vSum / whole, 10f);
            }
            var lightSum = m.X + m.Y + m.Z + m.W;
            if (lightSum > 0f)
            {
                censusLightShare[unsettled, 0] += m.X / lightSum;
                censusLightShare[unsettled, 1] += m.Y / lightSum;
                censusLightShare[unsettled, 2] += m.Z / lightSum;
                censusLightShare[unsettled, 3] += m.W / lightSum;
            }
            var fm = census[9 * t + 2] / passes;
            var fv = Vector4.Max(census[9 * t + 3] / passes - fm * fm, Vector4.Zero);
            var nm = census[9 * t + 4] / passes;
            var nv = Vector4.Max(census[9 * t + 5] / passes - nm * nm, Vector4.Zero);
            for (var i = 0; i < 3; i++)
            {
                float F(Vector4 v) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;
                if (mean > 0f) censusFarLight[unsettled, i] += Math.Clamp(F(fm) / mean, 0f, 1f);
                if (whole > 0f)
                {
                    censusFarVariance[unsettled, i] += Math.Min(F(fv) / whole, 10f);
                    censusNearVariance[unsettled, i] += Math.Min(F(nv) / whole, 10f);
                }
                if (MathF.Sqrt(F(nv) / passes) <= bar) censusNearSettles[unsettled, i]++;
                if (F(nm) <= 0f) censusNearEmpty[unsettled, i]++;
            }
            if (whole > 0f) censusFarSunVariance[unsettled] += Math.Min(fv.W / whole, 10f);
            var view = census[9 * t + 6] / passes;
            var kinds = census[9 * t + 7] / passes;
            if (kinds.W > 0f)
            {
                censusViewTexels++;
                censusView[0] += view.X; censusView[1] += view.Y; censusView[2] += view.Z; censusView[3] += view.W;
                if (view.Y > 0.01f) censusContact[0]++;
                if (view.Y > 0.05f) censusContact[1]++;
                if (view.Y > 0.20f) censusContact[2]++;
                censusOwnPatches += kinds.X; censusOtherPatches += kinds.Y; censusOtherPasses += kinds.Z; censusReach += kinds.W;
                if (view.X + view.Y > 0f) { censusNearTexels++; censusNearOtherOfNear += view.Y / (view.X + view.Y); }
                var objects = census[9 * t + 8] / passes;
                censusObjectView[0] += objects.X; censusObjectView[1] += objects.Y;
                if (objects.Y > 0.01f) censusObjectContact[0]++;
                if (objects.Y > 0.05f) censusObjectContact[1]++;
                if (objects.Y > 0.20f) censusObjectContact[2]++;
                censusOtherObjectPasses += objects.Z;
                if (view.X + view.Y > 0f) censusNearOtherObjectOfNear += objects.Y / (view.X + view.Y);
            }
            // A pass's spread over its mean, times sqrt(32): one ray's (as if the rays were independent).
            if (mean > 0f && (t & 15) == 0) censusRaySpread[unsettled].Add(MathF.Sqrt(whole * 32f) / mean);
        }
    }

    // The placements merged into connected objects: every placement's vertices in the world, welded at a millimetre,
    // sorted, and placements sharing one united (union-find). A carved column split by material into five primitives is
    // one object; a column standing on a floor shares no vertex with it and stays two.
    private void BuildCensusObjects()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var instances = rayQueries?.Instances;
        var count = instances?.Count ?? 0;
        var parent = new int[Math.Max(count, 1)];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        if (instances is not null)
        {
            long total = 0;
            foreach (var inst in instances) total += inst.Mesh.Positions.Length;
            var keys = new ulong[total];
            var owners = new int[total];
            long at = 0;
            for (var p = 0; p < count; p++)
            {
                var inst = instances[p];
                foreach (var v in inst.Mesh.Positions)
                {
                    var w = Vector3.Transform(v, inst.World);
                    static ulong Q(float x) => (ulong)((long)MathF.Round(x * 1000f) + (1L << 20)) & 0x1FFFFFUL;
                    keys[at] = Q(w.X) | (Q(w.Y) << 21) | (Q(w.Z) << 42);
                    owners[at++] = p;
                }
            }
            Array.Sort(keys, owners);
            for (long i = 1; i < total; i++)
                if (keys[i] == keys[i - 1])
                {
                    var a = Find(owners[i]);
                    var b = Find(owners[i - 1]);
                    if (a != b) parent[a] = b;
                }
        }
        var objectOf = new uint[Math.Max(count, 1)];
        var distinct = new HashSet<int>();
        for (var p = 0; p < count; p++) { var r = Find(p); objectOf[p] = (uint)r; distinct.Add(r); }
        censusPlacements = count;
        censusObjects = Own(device.CreateGpuBuffer(objectOf.Length * 4, MemoryMarshal.AsBytes(objectOf.AsSpan()), "sponza.census.objects"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] near-field census: {count:N0} primitives welded into {distinct.Count:N0} connected objects in {clock.Elapsed.TotalSeconds:0.0} s."));
        // The largest objects: a building welded into one would make "another object" small for the wrong reason.
        if (instances is not null)
        {
            var tris = new Dictionary<uint, (long Tris, int Prims, int First)>();
            long all = 0;
            for (var p = 0; p < count; p++)
            {
                var t = instances[p].Mesh.TriangleCount;
                all += t;
                tris[objectOf[p]] = tris.TryGetValue(objectOf[p], out var e) ? (e.Tris + t, e.Prims + 1, e.First) : (t, 1, p);
            }
            foreach (var (o, e) in tris.OrderByDescending(kv => kv.Value.Tris).Take(8))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza]   object {o}: {100.0 * e.Tris / Math.Max(all, 1):0.0}% of the triangles, {e.Prims:N0} primitives (e.g. {(e.First < rayPlacementNames.Count ? rayPlacementNames[e.First] : "?")})."));
        }
    }

    // BeginSunNodeRefineRound, after the sweep: the census printed, its buffer let go.
    private void ReportNoiseCensus()
    {
        if (!noiseCensus || sunNodeCensus.Equals(default(GpuBufferHandle))) return;
        if (censusViewTexels > 0)
        {
            double n = censusViewTexels;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] near-field census ({censusViewTexels:N0} texels; reach {censusReach / n:0.00} m on average): the view, cosine-weighted -- near on its own primitive {100 * censusView[0] / n:0.0}%, near on another {100 * censusView[1] / n:0.0}%, past the reach {100 * censusView[2] / n:0.0}%, escaped {100 * censusView[3] / n:0.0}%."));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza]   contact texels (another primitive's near over 1% / 5% / 20% of the view): {100.0 * censusContact[0] / n:0.0}% / {100.0 * censusContact[1] / n:0.0}% / {100.0 * censusContact[2] / n:0.0}%; of a texel's near light, another primitive's {100 * censusNearOtherOfNear / Math.Max(censusNearTexels, 1):0.0}% on average; a pass's near hits land on {censusOwnPatches / n:0.0} own patches and {censusOtherPatches / n:0.0} others (32 rays: a floor on the count), another primitive met in {100 * censusOtherPasses / n:0.0}% of passes."));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza]   merged into objects ({censusPlacements:N0} primitives): of the near on another primitive, {100 * censusObjectView[0] / n:0.0}% of the view is the same object's other primitives (authoring fragments) and {100 * censusObjectView[1] / n:0.0}% another object's; another object's near over 1% / 5% / 20% of the view: {100.0 * censusObjectContact[0] / n:0.0}% / {100.0 * censusObjectContact[1] / n:0.0}% / {100.0 * censusObjectContact[2] / n:0.0}% of the texels; of a texel's near light, another object's {100 * censusNearOtherObjectOfNear / Math.Max(censusNearTexels, 1):0.0}%; another object met in {100 * censusOtherObjectPasses / n:0.0}% of passes."));
            Console.WriteLine("[VulkanSponza]   (own primitive = the texel's patch's placement: at a contact the two can differ.)");
        }
        RetireGpuBuffer(sunNodeCensus);
        sunNodeCensus = default;
        string[] names = { "sky misses", "patch bounce", "sun at hits", "no-patch bounce" };
        for (var u = 1; u >= 0; u--)
        {
            var n = Math.Max(censusTexels[u], 1);
            var spread = censusRaySpread[u].OrderBy(x => x).ToArray();
            string P(int p) => spread.Length > 0 ? spread[Math.Min(spread.Length - 1, spread.Length * p / 100)].ToString("0.00", CultureInfo.InvariantCulture) : "-";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] noise census, {(u == 1 ? "UNSETTLED" : "settled")} after the sweep: {censusTexels[u]:N0} texels; one ray's spread over the mean p25/p50/p75/p90 {P(25)}/{P(50)}/{P(75)}/{P(90)}; sources' variances over the whole's {censusIndependence[u] / n:0.00}."));
            for (var i = 0; i < 4; i++)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza]   {names[i],-16} light {100 * censusLightShare[u, i] / n,5:0.0}%  variance {100 * censusVarianceShare[u, i] / n,5:0.0}%  the largest in {100.0 * censusDominant[u, i] / n,5:0.0}% of texels"));
            for (var i = 0; i < 3; i++)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza]   beyond {CensusRadii[i]:0.0} m    light {100 * censusFarLight[u, i] / n,5:0.0}%  variance {100 * censusFarVariance[u, i] / n,5:0.0}% (within: {100 * censusNearVariance[u, i] / n,5:0.0}%)  settled on the near rays alone {100.0 * censusNearSettles[u, i] / n,5:0.0}%  nothing within {100.0 * censusNearEmpty[u, i] / n,5:0.0}%"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza]   the sun at hits beyond 1 m: variance {100 * censusFarSunVariance[u] / n,5:0.0}%"));
        }
    }
}
