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
    int Iterations);

internal sealed partial class SponzaLoop
{
    private TransportSolveData? transportSolve;
    private const int SolveGroup = 32;
    private const int SolveBouncesPerFrame = 6;
    private ShaderInterface sunNodeSolveInterface = null!;
    private PassHandle sunNodeSolveAPass, sunNodeSolveBPass;
    private PipelineHandle sunNodeSolvePipeline;
    private RayBatchPasses sunNodeSunletBatch;

    private Task<(uint[] Start, uint[] Pairs, uint[] SunletStart, uint[] SunletPairs, Vector4[] Sunlets, Vector4[] Terms)>? solvePack;
    private GpuBufferHandle[] solveBuffers = Array.Empty<GpuBufferHandle>();
    private GpuBufferHandle solveFirst, solveIn, solveOut, solveOrigins, solveOccluded;
    private int solveGroupStart, solveSun, solveBounce = -1, solveSunlets;
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

    private bool SunNodeGpuSolve => transportSolve is not null && FrameRaysOnHardware && frameTracer is not null;

    // BeginSunNodes: the solve's inputs packed off the frame thread (CSR couplings, the sunlets, a patch's terms).
    private void BeginGpuSolve()
    {
        var d = transportSolve!;
        solveClock.Restart();
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
            return (start, pairs, sStart, sPairs, sunlets, terms);
        });
        solveActive = true;
        solveGroupStart = 0;
        solveSun = 0;
        solveBounce = -1;
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
        var suns = sunNodeSuns!.Length;
        var group = Math.Min(SolveGroup, suns - solveGroupStart);
        if (solveSun < group)
        {
            var k = solveGroupStart + solveSun;
            var toSun = new Vector3(sunNodeDirs[k].X, sunNodeDirs[k].Y, sunNodeDirs[k].Z);
            graph.Dispatch(sunNodeSolveAPass, SolveDispatch(0, group, solveSun, toSun, solveSunlets));
            RecordShadowBatch(sunNodeSunletBatch, solveOrigins, solveOccluded, solveSunlets, toSun, 3u);
            graph.Dispatch(sunNodeSolveBPass, SolveDispatch(1, group, solveSun, toSun, d.Patches));
            solveSun++;
            return false;
        }
        var items = d.Patches * group;
        if (solveBounce < 0)
        {
            graph.Dispatch(sunNodeSolveBPass, SolveDispatch(2, group, 0, Vector3.Zero, items));
            solveBounce = 0;
        }
        for (var b = 0; b < SolveBouncesPerFrame && solveBounce < d.Iterations; b++, solveBounce++)
        {
            graph.Dispatch(sunNodeSolveBPass, SolveDispatch(3, group, 0, Vector3.Zero, items));
            graph.Dispatch(sunNodeSolveBPass, SolveDispatch(4, group, 0, Vector3.Zero, items));
        }
        if (solveBounce < d.Iterations) return false;
        graph.Dispatch(sunNodeSolveBPass, SolveDispatch(5, group, 0, Vector3.Zero, items));
        solveGroupStart += group;
        solveSun = 0;
        solveBounce = -1;
        if (solveGroupStart < suns) return false;
        solveActive = false;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sun nodes: {d.Patches:N0} patches' light for {suns} suns solved on the GPU in {solveClock.Elapsed.TotalSeconds:0.0} s ({solveSunlets:N0} sunlet rays a sun, {d.Iterations} bounces in groups of {SolveGroup})."));
        return true;
    }

    private DispatchCommand SolveDispatch(int mode, int group, int sun, Vector3 toSun, int threads)
    {
        var groups = (threads + 63) / 64;
        var x = Math.Min(groups, 65535);
        var y = (groups + x - 1) / x;
        return new DispatchCommand(sunNodeSolvePipeline, x, y, 1,
            new ShaderUniform[]
            {
                new("uSolve", new Vector4Uniform(new Vector4(mode, transportSolve!.Patches, group, sun))),
                new("uSun", new Vector4Uniform(new Vector4(toSun, 0f))),
                new("uIrr", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
                new("uCount", new Vector4Uniform(new Vector4(solveSunlets, solveGroupStart, sunNodeSuns!.Length, 0f))),
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
            });
    }

    private void UploadGpuSolve((uint[] Start, uint[] Pairs, uint[] SunletStart, uint[] SunletPairs, Vector4[] Sunlets, Vector4[] Terms) packed)
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
        sunNodePatchLight = Own(device.CreateGpuBuffer(checked(d.Patches * sunNodeSuns!.Length * 8), name: "sponza.sun-nodes.patch-light"));
        var bytes = n * 48L + (packed.SunletStart.Length + packed.SunletPairs.Length + packed.Start.Length + packed.Pairs.Length) * 4L
            + packed.Terms.Length * 16L + 3L * items + n * 20L;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sun nodes: the solve's inputs packed and up in {solveClock.Elapsed.TotalSeconds:0.0} s ({bytes / 1048576.0:0} MB: {packed.Pairs.Length / 2:N0} couplings, {packed.SunletPairs.Length / 2:N0} sunlet couplings, {n:N0} sunlets)."));
    }

    // The solve's buffers, once the nodes' table is filled.
    private void ReleaseGpuSolve()
    {
        foreach (var b in solveBuffers.Append(solveFirst).Append(solveIn).Append(solveOut).Append(solveOrigins).Append(solveOccluded))
            if (!b.Equals(default(GpuBufferHandle))) RetireGpuBuffer(b);
        solveBuffers = Array.Empty<GpuBufferHandle>();
        solveFirst = solveIn = solveOut = solveOrigins = solveOccluded = default;
    }
}
