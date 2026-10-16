using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The sun nodes' patch light on the GPU (texel_nodes_solve.comp): the transport's relight for every node's sun, a group
// of SolveGroup suns at a time. On the CPU it was the cook's slowest part (Sponza, 154 suns: 15 minutes a sun at a
// time, 12 batched): every bounce reads all 26 M couplings for every sun. Here a frame does one sun's sunlet rays (a
// SHADOW batch) and its first bounce, and once the group's suns all have theirs, SolveBouncesPerFrame of its 48
// bounces a frame; the group's answers go straight into the nodes' table (SunNodePatchLight), never back to the CPU.
// Only with sunlets (the transport cook's default); without them the CPU's relight answers.
internal sealed record TransportSolveData(
    int Patches,
    (int To, float W)[][] Couplings,
    (int To, float W)[][] SunletCouplings,
    List<Vector3> SunletPos,
    List<Vector3> SunletNrm,
    List<Vector3> SunletAlbedo,
    List<Vector3> Albedo,
    Vector3[] SkyIn,
    Func<Vector3[]> BootIncident,
    int Iterations,
    // Sheets that transmit (KHR_materials_diffuse_transmission, --cloth-transmission): per patch, its placement's
    // transmittance (zero: none) and its twin -- the patch on the sheet's other side, at the same point (-1: none).
    // The sheet's far side glows with the bounce light reaching its near side (texel_nodes_solve.comp).
    Vector3[]? Transmit = null,
    int[]? Twin = null);

internal sealed partial class SponzaLoop
{
    private TransportSolveData? transportSolve;
    private Func<Vector3[], Vector3[], float[]>? transportBuildGuides;
    private Func<Vector3[], float[]>? transportBuildLights;
    private Func<float, (uint[] Start, uint[] Ids, float[] Weights)>? transportBuildFarLists;
    // The light lists built beside the guides (BuildLights), for the light strategy; null until built or with it off.
    private float[]? sunNodeLightLists;
    // The band's averages (texel_nodes_solve.comp modes 6, 7), and the nodes' guides built from them (null: no guide,
    // --sun-nodes-no-guide or the CPU's solve).
    private GpuBufferHandle solveAvgPatch, solveAvgSunlet;
    private Task<float[]>? sunNodeGuideTask;
    private const int SolveGroup = 32;
    // Bounces a frame, by the couplings each reads: a frame's command buffer past macOS's GPU watchdog loses the device.
    // Six a frame held at Sponza's 30 M couplings (512 rays a patch); at 80.6 M (2,048 rays) the frame timed out and the
    // device was lost (2026-10-14). So a frame reads at most SolveReadsPerFrame couplings x components -- the six at 30 M
    // and two components -- and at least one bounce.
    private const long SolveReadsPerFrame = 360_000_000;
    private int SolveBouncesPerFrame(int components) =>
        (int)Math.Clamp(SolveReadsPerFrame / Math.Max(1L, (long)solveCouplings * Math.Max(1, components)), 1, 6);
    private long solveCouplings;
    // --sun-nodes-bounces N: bounces after the warm start (the transport's own: 48). 0 leaves the boot sun's answer.
    private int? sunNodeBouncesAsked;
    private int SolveBounces => solveBouncesOverride ?? sunNodeBouncesAsked ?? transportSolve!.Iterations;
    // What a solve solves: its components, each a sun (or none) and how much of the sky (0 or 1) -- light is linear, so a
    // sun's light and the sky's, solved apart, sum to the two together; whether its bounces start from the boot sun's
    // answer (the cook's warm start) or from nothing (the parts apart: the boot answer is the two together); how deep.
    private (Vector3 ToSun, bool Sun, float Sky)[] solveComps = Array.Empty<(Vector3, bool, float)>();
    private bool solveWarm = true;
    private int? solveBouncesOverride;
    private GpuBufferHandle solveSkyWeights;
    private ShaderInterface sunNodeSolveInterface = null!;
    private PassHandle sunNodeSolveAPass, sunNodeSolveBPass;
    private PipelineHandle sunNodeSolvePipeline;
    private RayBatchPasses sunNodeSunletBatch;

    private Task<(uint[] Start, uint[] Pairs, uint[] SunletStart, uint[] SunletPairs, Vector4[] Sunlets, Vector4[] Terms,
        Vector4[] Transmit)>? solvePack;
    private GpuBufferHandle[] solveBuffers = Array.Empty<GpuBufferHandle>();
    private GpuBufferHandle solveFirst, solveIn, solveOut, solveOrigins, solveOccluded;
    private int solveGroupStart, solveSun, solveBounce = -1, solveSunlets;
    // Sheets that transmit (TransportSolveData.Transmit): per patch what its sheet passes and its twin.
    private GpuBufferHandle solveTransmit;
    private bool solveActive;
    private readonly Stopwatch solveClock = new();

    private void DeclareSunNodeSolvePasses(Func<string[], ShaderInterface> reflect)
    {
        sunNodeSolveInterface = reflect(new[] { "texel_nodes_solve.comp" });
        sunNodeSolveAPass = graph.ComputePass("sun-nodes-solve-a").Shader(sunNodeSolveInterface).Handle;
        sunNodeSunletBatch = DeclareShadowBatch("sun-nodes-sunlets");
        sunNodeSolveBPass = graph.ComputePass("sun-nodes-solve-b").Shader(sunNodeSolveInterface).Handle;
    }

    private void CreateSunNodeSolvePipeline(string shaderDir) =>
        sunNodeSolvePipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_nodes_solve.comp.spv")), sunNodeSolveInterface, "texel_nodes_solve")), "texel_nodes_solve"));

    private bool SunNodeGpuSolve => !sunNodeCpuSolve && transportSolve is not null && FrameRaysOnHardware && frameTracer is not null;

    // BeginSunNodes: the solve's inputs packed off the frame thread (CSR couplings, the sunlets, a patch's terms).
    private void BeginGpuSolve((Vector3 ToSun, bool Sun, float Sky)[] comps, bool warm, int? bounces = null)
    {
        var d = transportSolve!;
        solveClock.Restart();
        solveComps = comps;
        solveWarm = warm;
        solveBouncesOverride = bounces;
        solveActive = true;
        solveGroupStart = 0;
        solveSun = 0;
        solveBounce = -1;
        // The inputs once: a later solve on them (another set of components) keeps them.
        if (solveBuffers.Length > 0)
        {
            EnsureSolveTable();
            return;
        }
        solveSunlets = d.SunletPos.Count;
        solvePack = Task.Run(() =>
        {
            (uint[], uint[]) Csr((int To, float W)[][] rows)
            {
                var start = new uint[rows.Length + 1];
                long total = 0;
                for (var p = 0; p < rows.Length; p++) { start[p] = (uint)total; total += rows[p]?.Length ?? 0; }
                start[rows.Length] = (uint)total;
                var pairs = new uint[total * 2];
                Parallel.For(0, rows.Length, p =>
                {
                    long at = start[p];
                    if (rows[p] is null) return;
                    foreach (var (to, w) in rows[p])
                    {
                        pairs[at * 2] = (uint)to;
                        pairs[at * 2 + 1] = BitConverter.SingleToUInt32Bits(w);
                        at++;
                    }
                });
                return (start, pairs);
            }
            var (start, pairs) = Csr(d.Couplings);
            solveCouplings = pairs.Length / 2;
            var (sStart, sPairs) = Csr(d.SunletCouplings);
            var n = d.SunletPos.Count;
            var sunlets = new Vector4[n * 3];
            for (var s = 0; s < n; s++)
            {
                sunlets[s] = new Vector4(d.SunletPos[s], 0f);
                sunlets[n + s] = new Vector4(d.SunletNrm[s], 0f);
                sunlets[2 * n + s] = new Vector4(d.SunletAlbedo[s], 0f);
            }
            var boot = d.BootIncident();
            var terms = new Vector4[d.Patches * 3];
            for (var p = 0; p < d.Patches; p++)
            {
                terms[3 * p] = new Vector4(d.Albedo[p], 0f);
                terms[3 * p + 1] = new Vector4(d.SkyIn[p], 0f);
                terms[3 * p + 2] = new Vector4(boot[p], 0f);
            }
            // Sheets: per patch what of the light on its twin's side it passes, per unit of its own albedo -- the
            // readers multiply its albedo in, so the light through is the factor (times its colour factor: white for
            // the curtains, whose colour is their texture) in the cloth's own hue. Dividing the transmittance by the
            // albedo channel by channel (the first try) multiplied green cloth's light by (7, 1.75, 12): magenta and far
            // too bright (2026-10-15). And its twin.
            var transmit = new Vector4[d.Patches];
            var sheets = 0;
            if (d.Transmit is { } tr && d.Twin is { } tw)
                for (var p = 0; p < d.Patches; p++)
                {
                    if (tw[p] < 0 || tr[p] == Vector3.Zero) continue;
                    // The cloth's hue at the factor's strength: its albedo over its brightest channel -- one number a
                    // patch (the factor over that channel), so no channel is lifted alone. The albedo as it is (0.35 x
                    // 0.05-0.2) passed 2-7% of the sun: opaque again by eye (2026-10-15).
                    var a = d.Albedo[p];
                    var top = MathF.Max(MathF.Max(a.X, MathF.Max(a.Y, a.Z)), 0.05f);
                    // Capped at what its lit face reflects, as lit.frag caps it.
                    var factor = MathF.Max(tr[p].X, MathF.Max(tr[p].Y, tr[p].Z));
                    var scale = MathF.Min(1f, (1f - factor) * top / MathF.Max(factor, 1e-4f)) / top;
                    transmit[p] = new Vector4(tr[p] * scale, BitConverter.Int32BitsToSingle(tw[p]));
                    sheets++;
                }
            if (sheets > 0)
                Console.WriteLine(string.Create(Inv, $"[VulkanSponza] sun nodes: {sheets:N0} patches on sheets that transmit -- their far sides glow with the bounce light through them."));
            return (start, pairs, sStart, sPairs, sunlets, terms, transmit);
        });
    }

    // The nodes' table and the components' sky weights, sized for this solve's components.
    private void EnsureSolveTable()
    {
        var d = transportSolve!;
        foreach (var b in new[] { sunNodePatchLight, solveSkyWeights })
            if (!b.Equals(default(GpuBufferHandle))) RetireGpuBuffer(b);
        sunNodePatchLight = Own(device.CreateGpuBuffer(checked(d.Patches * solveComps.Length * 8), name: "sponza.sun-nodes.patch-light"));
        var sky = solveComps.Select(c => c.Sky).ToArray();
        solveSkyWeights = Own(device.CreateGpuBuffer(Math.Max(16, sky.Length * 4), MemoryMarshal.AsBytes(sky.AsSpan()), "sponza.sun-nodes.solve.sky-weights"));
    }

    // RecordSunNodes while Relighting on the GPU: a sun's sunlets and first bounce a frame, then the group's bounces.
    // True when every group is in the table.
    private bool StepGpuSolve()
    {
        if (!solveActive) return true;
        if (solveBuffers.Length == 0)
        {
            if (solvePack is not { IsCompleted: true }) return false;
            UploadGpuSolve(solvePack.Result);
            solvePack = null;
            return false;
        }
        var d = transportSolve!;
        var suns = solveComps.Length;
        var group = Math.Min(SolveGroup, suns - solveGroupStart);
        if (solveSun < group)
        {
            var comp = solveComps[solveGroupStart + solveSun];
            if (comp.Sun)
            {
                graph.Dispatch(sunNodeSolveAPass, SolveDispatch(0, group, solveSun, comp.ToSun, solveSunlets));
                RecordShadowBatch(sunNodeSunletBatch, solveOrigins, solveOccluded, solveSunlets, comp.ToSun, 3u);
                graph.Dispatch(sunNodeSolveBPass, SolveDispatch(1, group, solveSun, comp.ToSun, d.Patches));
                graph.Dispatch(sunNodeSolveBPass, SolveDispatch(7, group, solveSun, comp.ToSun, solveSunlets));
            }
            else graph.Dispatch(sunNodeSolveBPass, SolveDispatch(1, group, solveSun, Vector3.Zero, d.Patches, noSun: true));
            solveSun++;
            return false;
        }
        var items = d.Patches * group;
        if (solveBounce < 0)
        {
            graph.Dispatch(sunNodeSolveBPass, SolveDispatch(2, group, 0, Vector3.Zero, items));
            solveBounce = 0;
        }
        for (var b = 0; b < SolveBouncesPerFrame(group) && solveBounce < SolveBounces; b++, solveBounce++)
        {
            graph.Dispatch(sunNodeSolveBPass, SolveDispatch(3, group, 0, Vector3.Zero, items));
            graph.Dispatch(sunNodeSolveBPass, SolveDispatch(4, group, 0, Vector3.Zero, items));
        }
        if (solveBounce < SolveBounces) return false;
        graph.Dispatch(sunNodeSolveBPass, SolveDispatch(5, group, 0, Vector3.Zero, items));
        graph.Dispatch(sunNodeSolveBPass, SolveDispatch(6, group, 0, Vector3.Zero, d.Patches));
        solveGroupStart += group;
        solveSun = 0;
        solveBounce = -1;
        if (solveGroupStart < suns) return false;
        solveActive = false;
        if (sunNodeGuide && transportBuildGuides is { } build && sunNodeStage != SunNodeStage.Off)
        {
            device.WaitIdle();
            var outg = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(solveAvgPatch, 0, d.Patches * 16).AsSpan()).ToArray()
                .Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray();
            var sOut = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(solveAvgSunlet, 0, solveSunlets * 16).AsSpan()).ToArray()
                .Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray();
            var clock = Stopwatch.StartNew();
            var lights = sunNodeLights ? transportBuildLights : null;
            sunNodeLightLists = null;
            sunNodeGuideTask = Task.Run(() =>
            {
                var cdf = build(outg, sOut);
                if (lights is not null) sunNodeLightLists = lights(outg);
                Console.WriteLine(string.Create(Inv, $"[VulkanSponza] sun nodes: the band's guides{(lights is not null ? " and light lists" : "")} built in {clock.Elapsed.TotalSeconds:0.0} s ({cdf.Length / 64:N0} patches)."));
                return cdf;
            });
        }
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sun nodes: {d.Patches:N0} patches' light for {suns} components solved on the GPU in {solveClock.Elapsed.TotalSeconds:0.0} s ({solveSunlets:N0} sunlet rays a sun, {SolveBounces} bounces in groups of {SolveGroup})."));
        return true;
    }

    private DispatchCommand SolveDispatch(int mode, int group, int sun, Vector3 toSun, int threads, bool noSun = false)
    {
        var groups = (threads + 63) / 64;
        var x = Math.Min(groups, 65535);
        var y = (groups + x - 1) / x;
        return new DispatchCommand(sunNodeSolvePipeline, x, y, 1,
            new ShaderUniform[]
            {
                new("uSolve", new Vector4Uniform(new Vector4(mode, transportSolve!.Patches, group, sun))),
                new("uSun", new Vector4Uniform(new Vector4(toSun, noSun ? 1f : 0f))),
                new("uIrr", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, solveWarm ? 1f : 0f))),
                new("uCount", new Vector4Uniform(new Vector4(solveSunlets, solveGroupStart, solveComps.Length, 1f / solveComps.Length))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("SunletPos", solveBuffers[0]),
                new ShaderBufferBinding("SunletNrm", solveBuffers[1]),
                new ShaderBufferBinding("SunletAlbedo", solveBuffers[2]),
                new ShaderBufferBinding("SunletOrigins", solveOrigins),
                new ShaderBufferBinding("SunletOccluded", solveOccluded),
                new ShaderBufferBinding("SunletCouplingStart", solveBuffers[3]),
                new ShaderBufferBinding("SunletCouplings", solveBuffers[4]),
                new ShaderBufferBinding("CouplingStart", solveBuffers[5]),
                new ShaderBufferBinding("Couplings", solveBuffers[6]),
                new ShaderBufferBinding("PatchTerms", solveBuffers[7]),
                new ShaderBufferBinding("NodeFirst", solveFirst),
                new ShaderBufferBinding("NodeIncoming", solveIn),
                new ShaderBufferBinding("NodeOutgoing", solveOut),
                new ShaderBufferBinding("SunNodePatchLight", sunNodePatchLight),
                new ShaderBufferBinding("PatchAverage", solveAvgPatch),
                new ShaderBufferBinding("SunletAverage", solveAvgSunlet),
                new ShaderBufferBinding("SolveSkyWeights", solveSkyWeights),
                new ShaderBufferBinding("PatchTransmit", solveTransmit),
            });
    }

    private void UploadGpuSolve((uint[] Start, uint[] Pairs, uint[] SunletStart, uint[] SunletPairs, Vector4[] Sunlets, Vector4[] Terms,
        Vector4[] Transmit) packed)
    {
        var d = transportSolve!;
        var n = solveSunlets;
        GpuBufferHandle Up<T>(T[] data, string name) where T : unmanaged =>
            Own(device.CreateGpuBuffer(checked(Math.Max(16, data.Length * Marshal.SizeOf<T>())), MemoryMarshal.AsBytes(data.AsSpan()), name));
        GpuBufferHandle Slice(Vector4[] all, int part, string name) =>
            Own(device.CreateGpuBuffer(Math.Max(16, n * 16), MemoryMarshal.AsBytes(all.AsSpan(part * n, n)), name));
        solveBuffers = new[]
        {
            Slice(packed.Sunlets, 0, "sponza.sun-nodes.solve.sunlet-pos"),
            Slice(packed.Sunlets, 1, "sponza.sun-nodes.solve.sunlet-nrm"),
            Slice(packed.Sunlets, 2, "sponza.sun-nodes.solve.sunlet-albedo"),
            Up(packed.SunletStart, "sponza.sun-nodes.solve.sunlet-start"),
            Up(packed.SunletPairs, "sponza.sun-nodes.solve.sunlet-couplings"),
            Up(packed.Start, "sponza.sun-nodes.solve.start"),
            Up(packed.Pairs, "sponza.sun-nodes.solve.couplings"),
            Up(packed.Terms, "sponza.sun-nodes.solve.terms"),
        };
        var items = checked(d.Patches * SolveGroup * 16);
        solveFirst = Own(device.CreateGpuBuffer(items, name: "sponza.sun-nodes.solve.first"));
        solveIn = Own(device.CreateGpuBuffer(items, name: "sponza.sun-nodes.solve.in"));
        solveOut = Own(device.CreateGpuBuffer(items, name: "sponza.sun-nodes.solve.out"));
        solveOrigins = Own(device.CreateGpuBuffer(Math.Max(16, n * 16), name: "sponza.sun-nodes.solve.origins"));
        solveOccluded = Own(device.CreateGpuBuffer(Math.Max(16, n * 4), name: "sponza.sun-nodes.solve.occluded"));
        solveTransmit = Up(packed.Transmit, "sponza.sun-nodes.solve.transmit");
        solveAvgPatch = Own(device.CreateGpuBuffer(d.Patches * 16, new byte[d.Patches * 16], "sponza.sun-nodes.solve.patch-average"));
        solveAvgSunlet = Own(device.CreateGpuBuffer(Math.Max(16, n * 16), new byte[Math.Max(16, n * 16)], "sponza.sun-nodes.solve.sunlet-average"));
        EnsureSolveTable();
        var bytes = n * 48L + (packed.SunletStart.Length + packed.SunletPairs.Length + packed.Start.Length + packed.Pairs.Length) * 4L
            + packed.Terms.Length * 16L + 3L * items + n * 20L;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sun nodes: the solve's inputs packed and up in {solveClock.Elapsed.TotalSeconds:0.0} s ({bytes / 1048576.0:0} MB: {packed.Pairs.Length / 2:N0} couplings, {packed.SunletPairs.Length / 2:N0} sunlet couplings, {n:N0} sunlets)."));
    }

    // The solve's buffers, once the nodes' table is filled.
    private void ReleaseGpuSolve()
    {
        foreach (var b in solveBuffers.Append(solveFirst).Append(solveIn).Append(solveOut).Append(solveOrigins).Append(solveOccluded).Append(solveAvgPatch).Append(solveAvgSunlet).Append(solveSkyWeights)
                     .Append(solveTransmit))
            if (!b.Equals(default(GpuBufferHandle))) RetireGpuBuffer(b);
        solveBuffers = Array.Empty<GpuBufferHandle>();
        solveFirst = solveIn = solveOut = solveOrigins = solveOccluded = solveAvgPatch = solveAvgSunlet = solveSkyWeights = default;
        solveTransmit = default;
    }
}
