using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --transport-census (plan.md, "Cook the transport, not the light", N2): what storing the scene's transport -- not its
// light -- would take. The sweep's rays, cosine-stratified with no light in them (run with --sun-nodes-no-guide
// --sun-nodes-no-lights --no-far-field), each written as a relationship (texel_nodes_shade.glsl, NodeTransport): the
// sky's direction bin it escaped into, the texel it met within its texel's reach (on its own object or another --
// CensusObjects, the welded primitives), or the patch it met past the reach. texel_transport_census.comp counts each
// texel's distinct relationships, and what is left if only the K heaviest near texels are kept and the rest merge into
// their patches. Printed when the sweep ends: entries a texel, bytes, Sponza's total.
internal sealed partial class SponzaLoop
{
    private bool transportCensus;
    private GpuBufferHandle transportRecords, transportStats;
    private ShaderInterface transportCensusInterface = null!;
    private PipelineHandle transportCensusPipeline;
    private const int TransportRecords = 256, TransportStatWords = 1289;

    private void ReadTransportCensusArgs(AppArgs args) => transportCensus = args.Flag("transport-census");

    private void DeclareTransportCensus(Func<string[], ShaderInterface> reflect) =>
        transportCensusInterface = reflect(new[] { "texel_transport_census.comp" });

    private void CreateTransportCensusPipeline(string shaderDir) =>
        transportCensusPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_transport_census.comp.spv")), transportCensusInterface, "texel_transport_census")),
            "texel_transport_census"));

    // UploadSunNodes: the records' buffer for a slice and the counters.
    private void BeginTransportCensus()
    {
        if (!transportCensus || !splitCooking) return;
        if (sunNodeGuide || sunNodeLights || farField)
            Console.WriteLine("[VulkanSponza] transport census: WARNING rays guided by light or cut by the far field -- run with --sun-nodes-no-guide --sun-nodes-no-lights --no-far-field for light-free relationships.");
        if (censusObjects.Equals(default(GpuBufferHandle))) BuildCensusObjects();
        transportRecords = Own(device.CreateGpuBuffer(checked(sunNodeSlice * TransportRecords * 8), name: "sponza.transport.records"));
        transportStats = Own(device.CreateGpuBuffer(TransportStatWords * 4, new byte[TransportStatWords * 4], "sponza.transport.stats"));
    }

    private bool TransportCensusOn(int discover) =>
        transportCensus && splitCooking && discover == 0 && sunNodeRefineRound == 0 && !transportRecords.Equals(default(GpuBufferHandle));

    private Vector4 TransportUniform(int discover) => new(TransportCensusOn(discover) ? 1f : 0f, transportSpacing,
        (cookedPacked?.Length ?? 0) / 3, patchDirectDone && CensusOn(discover) ? 1f : 0f);
    private GpuBufferHandle TransportRecordsBuffer => transportRecords.Equals(default(GpuBufferHandle)) ? sunNodeAccum : transportRecords;

    // After a sweep slice's passes: its texels' relationships counted.
    private void TransportCensusFinish(bool now, int slice, int passes, PassHandle? censusPass = null)
    {
        if (!TransportCensusOn(0)) return;
        var declared = transportCensusInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        var command = new DispatchCommand(transportCensusPipeline, slice, 1, 1,
            new ShaderUniform[] { new("uCensus", new Vector4Uniform(new Vector4(slice, Math.Min(passes, 8) * 32, 0f, 0f))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("NodeTransport", transportRecords),
                new ShaderBufferBinding("TransportStats", transportStats),
            }.Where(b => declared.Contains(b.Name)).ToArray());
        if (now) device.DispatchNow(new[] { command }, "sun-nodes-transport-census");
        else graph.Dispatch(censusPass ?? transportCensusPass, command);
    }

    private PassHandle transportCensusPass;

    // BeginSunNodeRefineRound after the sweep: what storing the transport would take.
    private void ReportTransportCensus()
    {
        if (transportRecords.Equals(default(GpuBufferHandle))) return;
        device.WaitIdle();
        var stats = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(transportStats, 0, TransportStatWords * 4).AsSpan()).ToArray();
        RetireGpuBuffer(transportRecords);
        RetireGpuBuffer(transportStats);
        transportRecords = transportStats = default;
        var texels = (double)Math.Max(stats[1288], 1u);
        string[] names = { "sky bins", "near, own object", "near, another object", "far patches", "near patches" };
        var mean = new double[5];
        for (var k = 0; k < 5; k++)
        {
            long n = 0; double sum = 0; var cdf = new long[257];
            for (var c = 0; c <= 256; c++) { var v = stats[k * 257 + c]; n += v; sum += (double)c * v; cdf[c] = n; }
            mean[k] = sum / Math.Max(n, 1);
            int P(double q) { for (var c = 0; c <= 256; c++) if (cdf[c] >= q * n) return c; return 256; }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport census: {names[k],-21} a texel: mean {mean[k]:0.0}, p50 {P(0.5)}, p90 {P(0.9)}, p99 {P(0.99)}{(k < 4 ? "" : " (the near hits' patches)")}."));
        }
        // Bytes: an entry a 4-byte id and a 2-byte weight; a sky bin a byte and a 2-byte weight.
        double Bytes(double near) => 6 * (near + mean[3]) + 3 * mean[0];
        var near = mean[1] + mean[2];
        var raw = Bytes(near);
        var count = texelCount;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] transport census ({stats[1288]:N0} texels, {TransportRecords} light-free rays each): raw {near + mean[3] + mean[0]:0.0} entries, {raw:0} B a texel -> {raw * count / 1048576.0:N0} MB for {count:N0} texels; the near part {near:0.0} entries ({100 * mean[2] / Math.Max(near, 1e-9):0.0}% on another object)."));
        for (var c = 0; c < 3; c++)
        {
            var capped = stats[1285 + c] / texels;
            var bytes = Bytes(capped);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza]   near capped at {8 << c} texels (the rest into their patches): {capped:0.0} near entries, {bytes:0} B a texel -> {bytes * count / 1048576.0:N0} MB."));
        }
    }
}
