using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --potential-suns (plan.md, potential suns): the texels' light at every sun in the band, cooked once and answered at
// once. The band's suns are a GridHour x GridDec grid of nodes -- declination evenly over the band, and along each
// day's arc the hour angle scaled by that day's span above BandMinElevation (u = hour / H0(dec), -1 sunrise .. 1
// sunset), so every node is a sun that can be up. The cook (SponzaLoop.SunNodes.cs) traces each texel's gather once and
// shades it at every node; each block of SunNodeBlocks.Block live texels is kept as a basis and coefficients
// (SunNodeBlocks: rank 8, 16 B a texel), saved beside the transport cache (.sunnodes-*). At run time a sun inside the
// band is answered by texel_nodes_resolve.comp -- the 4 x 4 nodes around it, Catmull-Rom -- into the texels' resting
// light: a sun change re-solves the patches as before (other readers want them) but bakes nothing. Outside the band (or
// with RenderSettings.PotentialSuns off) the bake answers, as before.
//
// Cornell (17 x 9, cooked against per-sun bakes at 16 suns between the nodes): 1.4% of the light's energy off, rank 8.
internal sealed partial class SponzaLoop
{
    private const int GridHour = 17, GridDec = 9;
    // 2: sun bits shot from the texels (a lift), the foliage's and the discovery's sun points, rays by the band's guides.
    private const int PotentialSunsVersion = 2;
    private bool potentialSunsAsked;
    private string? potentialSunsPath;
    // Loaded: on the GPU, answering when the settings and the sun allow.
    private bool potentialLoaded;
    private int potentialBlocks, potentialCols;
    private GpuBufferHandle potentialBasis, potentialCoefs, potentialLive, potentialBaked;
    private float potentialNorth;
    private ShaderInterface potentialResolveInterface = null!;
    private PassHandle potentialResolvePass;
    private PipelineHandle potentialResolvePipeline;
    // What the resting light last held from the nodes (the sun, the view), to resolve again only on a change.
    private Vector4 potentialResolvedKey = new(float.NaN);
    private bool potentialAnswering;
    private string potentialStatus = "off";
    private (Vector3 ToSun, Vector3 Irradiance) potentialBakedSun;

    // The cook's staging: the slices' rows (a texel, its light at every node) into blocks, compressed off the frame.
    private float[]? potentialPending;
    private int potentialPendingRows, potentialNextBlock;
    private ushort[]? potentialCookBasis, potentialCookCoefs;
    private readonly List<Task> potentialCompressions = new();
    private double potentialErrorSum, potentialLightSum;
    private readonly object potentialLock = new();

    private static float DayHalfSpan(float decDeg)
    {
        float r(float d) => d * MathF.PI / 180f;
        var c = (MathF.Sin(r(BandMinElevation)) - MathF.Sin(r(BandLatitude)) * MathF.Sin(r(decDeg))) / (MathF.Cos(r(BandLatitude)) * MathF.Cos(r(decDeg)));
        return MathF.Acos(Math.Clamp(c, -1f, 1f)) * 180f / MathF.PI;
    }

    // The nodes, j (declination) outer: node j * GridHour + i.
    private static (float Hour, float Dec)[] PotentialGrid()
    {
        var maxDec = Obliquity + 5f;
        var grid = new (float, float)[GridHour * GridDec];
        for (var j = 0; j < GridDec; j++)
        {
            var dec = -maxDec + j * 2f * maxDec / (GridDec - 1);
            for (var i = 0; i < GridHour; i++)
                grid[j * GridHour + i] = ((-1f + i * 2f / (GridHour - 1)) * DayHalfSpan(dec), dec);
        }
        return grid;
    }

    // Where a sun falls on the grid (fractional node coordinates), or false outside the band.
    private bool PotentialGridAt(Vector3 toSun, out float fi, out float fj, out float hour, out float dec)
    {
        (hour, dec) = Equatorial(toSun, BandLatitude, potentialNorth);
        var maxDec = Obliquity + 5f;
        fi = fj = 0f;
        if (MathF.Abs(dec) > maxDec + 1e-3f) return false;
        var u = hour / DayHalfSpan(dec);
        if (MathF.Abs(u) > 1f + 1e-4f) return false;
        fi = Math.Clamp((u + 1f) / 2f * (GridHour - 1), 0f, GridHour - 1);
        fj = Math.Clamp((dec + maxDec) / (2f * maxDec) * (GridDec - 1), 0f, GridDec - 1);
        return true;
    }

    private string PotentialSunsPathFor(string transportKey)
    {
        var spec = string.Create(CultureInfo.InvariantCulture,
            $"{transportKey};grid {GridHour}x{GridDec};band {BandLatitude} {Obliquity} {BandMinElevation};north {BandNorth(out _):F5};sun {EffectiveSunIrradiance.X:F4},{EffectiveSunIrradiance.Y:F4},{EffectiveSunIrradiance.Z:F4};rays {sunNodeRays} {(sunNodeGuide && !sunNodeCpuSolve ? "guided" : "cosine")};lift {sunNodeLift:G4};lookup {sunNodePlane:G3} {sunNodeSigma:G3};discover {sunNodeDiscoverStride};unknown sun {sunNodeUnknownSun:G3};buried {LightmapBuriedRule};rank {SunNodeBlocks.Rank} block {SunNodeBlocks.Block};v{PotentialSunsVersion}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(spec)))[..16].ToLowerInvariant();
        return Path.ChangeExtension(TransportCachePath(transportKey), $".sunnodes-{hash}");
    }

    private static ulong LiveChecksum(uint[] live)
    {
        ulong h = 1469598103934665603UL;
        foreach (var id in live) h = (h ^ id) * 1099511628211UL;
        return h;
    }

    private void DeclarePotentialSunsPass(Func<string[], ShaderInterface> reflect)
    {
        potentialResolveInterface = reflect(new[] { "texel_nodes_resolve.comp" });
        potentialResolvePass = graph.ComputePass("potential-suns-resolve").Shader(potentialResolveInterface).Handle;
    }

    private void CreatePotentialSunsPipeline(string shaderDir) =>
        potentialResolvePipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_nodes_resolve.comp.spv")), potentialResolveInterface, "texel_nodes_resolve")), "texel_nodes_resolve"));

    // SunNodes' Waiting stage, under --potential-suns: the cooked nodes from the cache when they are there (true: no
    // cook), once the texels are up.
    private bool TryLoadPotentialSuns()
    {
        potentialSunsPath ??= PotentialSunsPathFor(lightBakeKey!);
        if (!File.Exists(potentialSunsPath)) return false;
        // Only the texels and their resting light: none of the cook's machinery (its sun bits, its guides) is wanted.
        if (texelLiveIds is null || texelResolvedBuffer.Equals(default(GpuBufferHandle))) return true;
        var clock = Stopwatch.StartNew();
        var live = texelLiveIds!;
        try
        {
            using var stream = File.OpenRead(potentialSunsPath);
            using var r = new BinaryReader(stream);
            if (r.ReadString() != "blix-sun-nodes" || r.ReadInt32() != PotentialSunsVersion || r.ReadInt32() != texelCount
                || r.ReadInt32() != live.Length || r.ReadUInt64() != LiveChecksum(live) || r.ReadInt32() != GridHour
                || r.ReadInt32() != GridDec || r.ReadInt32() != SunNodeBlocks.Rank || r.ReadInt32() != SunNodeBlocks.Block)
            {
                Console.WriteLine($"[VulkanSponza] potential suns: {Path.GetFileName(potentialSunsPath)} is for other texels; cooking again.");
                File.Delete(potentialSunsPath);
                return false;
            }
            var cols = r.ReadInt32();
            var basis = new ushort[r.ReadInt32()];
            stream.ReadExactly(MemoryMarshal.AsBytes(basis.AsSpan()));
            var coefs = new ushort[r.ReadInt32()];
            stream.ReadExactly(MemoryMarshal.AsBytes(coefs.AsSpan()));
            UploadPotentialSuns(basis, coefs, cols, string.Create(Inv,
                $"read from the cache ({(basis.Length + coefs.Length) * 2 / 1048576.0:0} MB, {clock.Elapsed.TotalSeconds:0.0} s)"));
        }
        catch (IOException e)
        {
            Console.WriteLine($"[VulkanSponza] potential suns: the cache did not read ({e.Message}); cooking.");
            return false;
        }
        sunNodeStage = SunNodeStage.Done;
        return true;
    }

    // The cook's readback (SunNodes): a slice's sums, each texel's light at every node, into the blocks.
    // stride: the suns a texel's sums hold; suns: the grid's, which are compressed (a boot sun past them is not).
    private void StagePotentialRows(Vector4[] sums, int count, int stride, int suns, int passes, bool last)
    {
        var cols = suns * 3;
        potentialPending ??= new float[SunNodeBlocks.Block * cols];
        potentialCookCoefs ??= new ushort[(long)texelLiveIds!.Length * SunNodeBlocks.Rank];
        potentialCookBasis ??= new ushort[(long)((texelLiveIds.Length + SunNodeBlocks.Block - 1) / SunNodeBlocks.Block) * SunNodeBlocks.Rank * cols];
        for (var t = 0; t < count; t++)
        {
            var row = potentialPending.AsSpan(potentialPendingRows * cols, cols);
            for (var k = 0; k < suns; k++)
            {
                var s = sums[(long)t * stride + k];
                row[3 * k] = s.X / passes;
                row[3 * k + 1] = s.Y / passes;
                row[3 * k + 2] = s.Z / passes;
            }
            if (++potentialPendingRows == SunNodeBlocks.Block) FlushPotentialBlock(cols);
        }
        if (last && potentialPendingRows > 0) FlushPotentialBlock(cols);
    }

    private void FlushPotentialBlock(int cols)
    {
        var rows = potentialPendingRows;
        var block = potentialNextBlock++;
        var a = potentialPending!;
        potentialPending = new float[SunNodeBlocks.Block * cols];
        potentialPendingRows = 0;
        var basisOut = potentialCookBasis!;
        var coefOut = potentialCookCoefs!;
        potentialCompressions.Add(Task.Run(() =>
        {
            var (basis, coefs) = SunNodeBlocks.Compress(a, rows, cols);
            var basisAt = (long)block * SunNodeBlocks.Rank * cols;
            for (var i = 0; i < basis.Length; i++) basisOut[basisAt + i] = BitConverter.HalfToUInt16Bits((Half)basis[i]);
            var coefAt = (long)block * SunNodeBlocks.Block * SunNodeBlocks.Rank;
            for (var i = 0; i < coefs.Length; i++) coefOut[coefAt + i] = BitConverter.HalfToUInt16Bits((Half)coefs[i]);
            // How much of the light the halves' basis and coefficients keep: |reconstructed - cooked| over |cooked|.
            double err = 0, sum = 0;
            for (var row = 0; row < rows; row++)
            for (var c = 0; c < cols; c++)
            {
                var v = 0f;
                for (var k = 0; k < SunNodeBlocks.Rank; k++)
                    v += (float)BitConverter.UInt16BitsToHalf(coefOut[coefAt + row * SunNodeBlocks.Rank + k])
                         * (float)BitConverter.UInt16BitsToHalf(basisOut[basisAt + k * cols + c]);
                var x = a[row * cols + c];
                err += Math.Abs(v - x);
                sum += Math.Abs(x);
            }
            lock (potentialLock) { potentialErrorSum += err; potentialLightSum += sum; }
        }));
    }

    // The cook is done: the last blocks compressed, saved, and up.
    private void FinishPotentialSuns(int suns)
    {
        var clock = Stopwatch.StartNew();
        Task.WaitAll(potentialCompressions.ToArray());
        potentialCompressions.Clear();
        var cols = suns * 3;
        var basis = potentialCookBasis!;
        var coefs = potentialCookCoefs!;
        potentialCookBasis = potentialCookCoefs = null;
        potentialPending = null;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] potential suns: {potentialNextBlock:N0} blocks compressed (rank {SunNodeBlocks.Rank}, {clock.Elapsed.TotalSeconds:0.0} s waited at the end): {100 * potentialErrorSum / Math.Max(potentialLightSum, 1e-30):0.00}% of the nodes' light off."));
        var path = potentialSunsPath!;
        var live = texelLiveIds!;
        var partial = path + ".partial";
        try
        {
            using (var w = new BinaryWriter(new BufferedStream(File.Create(partial), 1 << 22)))
            {
                w.Write("blix-sun-nodes");
                w.Write(PotentialSunsVersion);
                w.Write(texelCount);
                w.Write(live.Length);
                w.Write(LiveChecksum(live));
                w.Write(GridHour);
                w.Write(GridDec);
                w.Write(SunNodeBlocks.Rank);
                w.Write(SunNodeBlocks.Block);
                w.Write(cols);
                w.Write(basis.Length);
                w.Write(MemoryMarshal.AsBytes(basis.AsSpan()));
                w.Write(coefs.Length);
                w.Write(MemoryMarshal.AsBytes(coefs.AsSpan()));
            }
            File.Move(partial, path, overwrite: true);
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] potential suns: saved ({new FileInfo(path).Length / 1048576.0:0} MB): {Path.GetFileName(path)}"));
        }
        catch (IOException e)
        {
            try { File.Delete(partial); } catch (IOException) { }
            Console.WriteLine($"[VulkanSponza] potential suns: not saved ({e.Message}).");
        }
        UploadPotentialSuns(basis, coefs, cols, "cooked");
    }

    private void UploadPotentialSuns(ushort[] basis, ushort[] coefs, int cols, string how)
    {
        var live = texelLiveIds!;
        potentialCols = cols;
        potentialBlocks = (live.Length + SunNodeBlocks.Block - 1) / SunNodeBlocks.Block;
        potentialBasis = Own(device.CreateGpuBuffer(checked(basis.Length * 2), MemoryMarshal.AsBytes(basis.AsSpan()), "sponza.potential-suns.basis"));
        potentialCoefs = Own(device.CreateGpuBuffer(checked(coefs.Length * 2), MemoryMarshal.AsBytes(coefs.AsSpan()), "sponza.potential-suns.coefs"));
        potentialLive = sunNodeLive.Equals(default(GpuBufferHandle))
            ? Own(device.CreateGpuBuffer(live.Length * 4, MemoryMarshal.AsBytes(live.AsSpan()), "sponza.potential-suns.live"))
            : sunNodeLive;
        // The boot sun's bake, kept to hold the nodes against (PotentialSunsView.AgainstBake).
        device.WaitIdle();
        var baked = device.ReadGpuBuffer(texelResolvedBuffer, 0, texelCount * 8);
        potentialBaked = Own(device.CreateGpuBuffer(baked.Length, baked, "sponza.potential-suns.baked"));
        potentialBakedSun = transportRelightSun;
        potentialNorth = BandNorth(out _);
        potentialLoaded = true;
        ReleaseSunNodeCook();
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] potential suns: {GridHour} x {GridDec} nodes {how}; up: basis {basis.Length * 2 / 1048576.0:0} MB, coefficients {coefs.Length * 2 / 1048576.0:0} MB, the boot bake {baked.Length / 1048576.0:0} MB."));
    }

    // Whether the nodes answer the sun now (the settings, the band); the patches' relight still runs (FollowSun).
    private bool PotentialSunsAnswer(Vector3 toSun) =>
        potentialLoaded && render.PotentialSuns && TexelsMayRest && PotentialGridAt(toSun, out _, out _, out _, out _);

    // RecordTexels, each frame: the resting light from the nodes when the sun (or the view) changed.
    private void RecordPotentialSuns()
    {
        if (!potentialLoaded) return;
        var toSun = -Vector3.Normalize(sunDirection);
        var inBand = PotentialGridAt(toSun, out var fi, out var fj, out var hour, out var dec);
        var answer = render.PotentialSuns && TexelsMayRest && inBand;
        if (!answer)
        {
            potentialStatus = !render.PotentialSuns ? "off: the bake answers"
                : string.Create(Inv, $"sun outside the band (hour {hour:0.0}, dec {dec:0.0}): the bake answers");
            if (potentialAnswering)
            {
                // Handed back: the bake for this sun (the resting light shows the nodes' until it settles).
                potentialAnswering = false;
                potentialResolvedKey = new Vector4(float.NaN);
                texelEpoch++;
                RebakeForSun(transportRelightSun);
            }
            return;
        }
        var view = (int)render.PotentialSunsView;
        var key = new Vector4(toSun, view * 1000 + render.PotentialSunsCoefficient + render.PotentialSunsHeat * 0.001f);
        potentialStatus = string.Create(Inv,
            $"answering: hour {hour:0.0}, dec {dec:0.0} -> node ({fi:0.00}, {fj:0.00}) of {GridHour} x {GridDec}; view {render.PotentialSunsView}");
        if (potentialAnswering && key == potentialResolvedKey) return;
        AbortLightBake("the potential suns answer");
        potentialAnswering = true;
        potentialResolvedKey = key;
        // Catmull-Rom over the 4 x 4 nodes around (fi, fj), clamped at the grid's edges.
        static float[] Cr(float t) => new[] { (-t * t * t + 2 * t * t - t) / 2, (3 * t * t * t - 5 * t * t + 2) / 2, (-3 * t * t * t + 4 * t * t + t) / 2, (t * t * t - t * t) / 2 };
        int i0 = Math.Min((int)fi, GridHour - 2), j0 = Math.Min((int)fj, GridDec - 2);
        float[] wu = Cr(fi - i0), wd = Cr(fj - j0);
        var weights = new Dictionary<int, float>();
        for (var x = 0; x < 4; x++)
        for (var y = 0; y < 4; y++)
        {
            var node = Math.Clamp(j0 - 1 + y, 0, GridDec - 1) * GridHour + Math.Clamp(i0 - 1 + x, 0, GridHour - 1);
            weights[node] = weights.GetValueOrDefault(node) + wu[x] * wd[y];
        }
        var pairs = new Vector4[8];
        var q = 0;
        foreach (var (node, w) in weights)
        {
            if (q % 2 == 0) pairs[q / 2] = new Vector4(node, w, 0f, 0f);
            else pairs[q / 2] = pairs[q / 2] with { Z = node, W = w };
            q++;
        }
        graph.Dispatch(potentialResolvePass, new DispatchCommand(potentialResolvePipeline, potentialBlocks, 1, 1,
            new ShaderUniform[]
            {
                new("uResolve", new Vector4Uniform(new Vector4(potentialBlocks, texelLiveIds!.Length, potentialCols, view))),
                new("uView", new Vector4Uniform(new Vector4(render.PotentialSunsCoefficient, render.PotentialSunsHeat, 0f, 0f))),
                // Raw: a matrix's rows land as the shader's columns.
                new("uWeights", new Matrix4x4ArrayUniform(new[]
                {
                    new Matrix4x4(pairs[0].X, pairs[0].Y, pairs[0].Z, pairs[0].W, pairs[1].X, pairs[1].Y, pairs[1].Z, pairs[1].W,
                                  pairs[2].X, pairs[2].Y, pairs[2].Z, pairs[2].W, pairs[3].X, pairs[3].Y, pairs[3].Z, pairs[3].W),
                    new Matrix4x4(pairs[4].X, pairs[4].Y, pairs[4].Z, pairs[4].W, pairs[5].X, pairs[5].Y, pairs[5].Z, pairs[5].W,
                                  pairs[6].X, pairs[6].Y, pairs[6].Z, pairs[6].W, pairs[7].X, pairs[7].Y, pairs[7].Z, pairs[7].W),
                })),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("SunNodesLive", potentialLive),
                new ShaderBufferBinding("PotentialBasis", potentialBasis),
                new ShaderBufferBinding("PotentialCoefs", potentialCoefs),
                new ShaderBufferBinding("TexelResolved", texelResolvedBuffer),
                new ShaderBufferBinding("TexelBaked", potentialBaked),
            }));
        texelResting = true;
        texelResolveCurrent = true;
        texelsSettled = true;
        texelSettledEpoch = texelEpoch;
    }

    private void ReportPotentialSuns(Blix.Diagnostics.DebugContext debug)
    {
        if (!potentialSunsAsked) return;
        using (debug.Scope("Potential suns"))
        {
            debug.Values.Value("potential-suns", potentialStatus);
            if (potentialLoaded && PotentialGridAt(potentialBakedSun.ToSun, out var bi, out var bj, out _, out _))
                debug.Values.Value("baked-sun-node", string.Create(Inv, $"({bi:0.00}, {bj:0.00}): AgainstBake holds the nodes against this sun's bake"));
        }
    }
}
