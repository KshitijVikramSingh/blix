using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The texels' light, baked. Gathered as the image reads them (texel_mark.comp), a view's texels arrive young and noisy
// and settle over minutes -- hundreds of thousands of 4 cm atlas texels at 64 settled a frame -- and until then the
// image is a patchwork of half-gathered estimates. The scene is static and the sun mostly so: this gathers EVERY live
// texel (the hashed records, and the atlas's not buried in a solid) to the target up front, a slice a frame
// (texel_bake.comp queues it, texel_gather.comp traces it unchanged), and saves the result beside the transport cache,
// keyed by it, the solved sun and the target. A later run loads it, and every texel is settled from its first frame: the
// mark finds nothing to queue, and the gather costs nothing until the sun moves (the epoch, as before).
// --no-light-bake: gathered as the image reads them, every run, as before.
internal sealed partial class SponzaLoop
{
    // --light-bake-budget N: texels a frame while baking, one 32-ray pass each. 16384: ~0.5 M rays a frame.
    private int lightBakeBudget = 16384;
    private bool lightBakeOff;
    private ShaderInterface lightBakeInterface = null!;
    private PassHandle lightBakePassHandle;
    private PipelineHandle lightBakePipeline;
    private GpuBufferHandle lightBakeLiveBuffer;
    private int lightBakeLive;
    private long lightBakeIssued, lightBakeTotal;
    private bool lightBakeActive;
    // Where the light goes when it is done (null: nothing to save), the sun it is for, and when its last slice went out.
    private string? lightBakePath;
    private (Vector3 ToSun, Vector3 Irradiance) lightBakeSun;
    private Stopwatch? lightBakeClock;
    private uint lightBakeDoneFrame;
    private bool lightBakeSaving;
    private int lightBakeReported = -1;

    private const int LightBakeVersion = 1;
    // Per record on disk: light rgb (half), its rays (float), the blend rgb (half), the moment and the openness (half),
    // the openness's ray count (float).
    private const int LightBakeRecordBytes = 3 * 2 + 4 + 3 * 2 + 2 + 2 + 4;

    private void ReadLightBakeArgs(AppArgs args)
    {
        lightBakeOff = args.Flag("no-light-bake");
        if (args.Int("light-bake-budget") is { } b) lightBakeBudget = Math.Clamp(b, 1024, 1 << 18);
    }

    // The baked light's file: the transport cache's key (the scene, the lightmap files, the texel settings), the sun
    // the patches were solved for, and the target.
    private string LightBakePathFor(string transportKey, (Vector3 ToSun, Vector3 Irradiance) sun)
    {
        var spec = string.Create(CultureInfo.InvariantCulture,
            $"{transportKey};sun {sun.ToSun.X:F5},{sun.ToSun.Y:F5},{sun.ToSun.Z:F5};{sun.Irradiance.X:F4},{sun.Irradiance.Y:F4},{sun.Irradiance.Z:F4};target {texelTarget};buried {(lightmapKeepBuried ? "kept" : lightmapBackface.ToString(CultureInfo.InvariantCulture))}/16;v{LightBakeVersion}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(spec)))[..16].ToLowerInvariant();
        return Path.ChangeExtension(TransportCachePath(transportKey), $".light-{hash}");
    }

    internal sealed record TexelLightState(Vector4[] Light, float[] Moment, Vector4[] Blend, Vector4[] Open);

    private static TexelLightState? ReadLightBake(string path, int count)
    {
        if (!File.Exists(path)) return null;
        var clock = Stopwatch.StartNew();
        try
        {
            using var stream = new BufferedStream(File.OpenRead(path), 1 << 22);
            using var r = new BinaryReader(stream);
            if (r.ReadString() != "blix-texel-light" || r.ReadInt32() != LightBakeVersion || r.ReadInt32() != count) return null;
            var state = new TexelLightState(new Vector4[count], new float[count], new Vector4[count], new Vector4[count]);
            var record = new byte[LightBakeRecordBytes];
            for (var i = 0; i < count; i++)
            {
                if (r.Read(record, 0, record.Length) != record.Length) return null;
                float H(int o) => (float)BitConverter.ToHalf(record, o);
                var rays = BitConverter.ToSingle(record, 6);
                state.Light[i] = new Vector4(H(0), H(2), H(4), rays);
                state.Blend[i] = new Vector4(H(10), H(12), H(14), rays);
                state.Moment[i] = H(16);
                state.Open[i] = new Vector4(H(18), BitConverter.ToSingle(record, 20), 0f, 0f);
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] light bake: {count:N0} texels' light read from the cache ({new FileInfo(path).Length / 1048576.0:0} MB, {clock.Elapsed.TotalSeconds:0.0} s): {Path.GetFileName(path)}"));
            return state;
        }
        catch (IOException) { return null; }
    }

    private static void WriteLightBake(string path, TexelLightState state)
    {
        var partial = path + ".partial";
        try
        {
            using (var stream = new BufferedStream(File.Create(partial), 1 << 22))
            using (var w = new BinaryWriter(stream))
            {
                w.Write("blix-texel-light");
                w.Write(LightBakeVersion);
                w.Write(state.Light.Length);
                var record = new byte[LightBakeRecordBytes];
                for (var i = 0; i < state.Light.Length; i++)
                {
                    void H(int o, float v) => BitConverter.TryWriteBytes(record.AsSpan(o, 2), (Half)v);
                    H(0, state.Light[i].X); H(2, state.Light[i].Y); H(4, state.Light[i].Z);
                    BitConverter.TryWriteBytes(record.AsSpan(6, 4), state.Light[i].W);
                    H(10, state.Blend[i].X); H(12, state.Blend[i].Y); H(14, state.Blend[i].Z);
                    H(16, state.Moment[i]);
                    H(18, state.Open[i].X);
                    BitConverter.TryWriteBytes(record.AsSpan(20, 4), state.Open[i].Y);
                    w.Write(record);
                }
            }
            File.Move(partial, path, overwrite: true);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] light bake: saved ({new FileInfo(path).Length / 1048576.0:0} MB): {Path.GetFileName(path)}"));
        }
        catch (IOException e)
        {
            try { File.Delete(partial); } catch (IOException) { }
            Console.WriteLine($"[VulkanSponza] light bake: not saved ({e.Message}).");
        }
    }

    // After the texels are on the GPU with no baked light (UploadTexels): every live texel, and the bake under way.
    // Below firstAtlas every record is hashed and live; from it, the atlas's, live unless buried (|w| 1).
    private void BeginLightBake(Vector4[] texels, int count, int firstAtlas)
    {
        var live = new List<uint>(count);
        for (var i = 0; i < count; i++)
            if (transferDumpPath is not null ? TransferSampleKeeps(texels, i, firstAtlas) : i < firstAtlas || MathF.Abs(texels[2 * i].W) >= 2f)
                live.Add((uint)i);
        // --transfer-dump: a region's sample, baked for the dump and not saved; and its points for the path-traced capture.
        if (transferDumpPath is not null)
        {
            lightBakePath = null;
            transferCapturePoints = new List<Vector4>(live.Count * 2);
            foreach (var i in live)
            {
                var n = Vector3.Normalize(new Vector3(texels[2 * i + 1].X, texels[2 * i + 1].Y, texels[2 * i + 1].Z));
                transferCapturePoints.Add(new Vector4(new Vector3(texels[2 * i].X, texels[2 * i].Y, texels[2 * i].Z) + n * 0.01f, 0f));
                transferCapturePoints.Add(new Vector4(n, 0f));
            }
        }
        if (live.Count == 0) return;
        lightBakeLive = live.Count;
        lightBakeLiveBuffer = Own(device.CreateGpuBuffer(live.Count * 4, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(live)), "sponza.light-bake.live"));
        var passes = (texelTarget + 31) / 32;
        lightBakeTotal = (long)lightBakeLive * passes;
        lightBakeIssued = 0;
        lightBakeActive = true;
        lightBakeClock = Stopwatch.StartNew();
        lightBakeReported = -1;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] light bake: {lightBakeLive:N0} live texels of {count:N0}, {passes} passes of 32 rays each ({lightBakeTotal * 32 / 1e9:0.0} G rays), {lightBakeBudget:N0} a frame."));
    }

    // RecordTexels, while baking: the next slice into the gather's queue. Returns the gather's budget this frame.
    private int RecordLightBake()
    {
        graph.Dispatch(lightBakePassHandle, new DispatchCommand(lightBakePipeline, (lightBakeBudget + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uBake", new Vector4Uniform(new Vector4(lightBakeIssued % lightBakeLive, lightBakeBudget, texelFrame & 1u, lightBakeLive))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("TexelLive", lightBakeLiveBuffer),
                new ShaderBufferBinding("TexelQueue", texelQueueBuffer),
            }));
        lightBakeIssued += lightBakeBudget;
        var percent = (int)(100 * Math.Min(lightBakeIssued, lightBakeTotal) / lightBakeTotal);
        if (percent / 5 != lightBakeReported / 5)
        {
            lightBakeReported = percent;
            var elapsed = lightBakeClock!.Elapsed.TotalMinutes;
            var left = percent > 0 ? elapsed * (100 - percent) / percent : double.NaN;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] light bake: {percent}% ({elapsed:0.0} min{(percent > 0 ? $", ~{left:0.0} left" : "")})."));
        }
        if (lightBakeIssued >= lightBakeTotal)
        {
            lightBakeActive = false;
            lightBakeDoneFrame = texelFrame;
            transferBakeDone = transferDumpPath is not null;
            transferBakeDoneFrame = texelFrame;
            lightBakeSaving = lightBakePath is not null;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] light bake: done in {lightBakeClock.Elapsed.TotalMinutes:0.0} min."));
        }
        return lightBakeBudget;
    }

    // A few frames after the last slice (its gather finished on the GPU): the light read back and written in the
    // background.
    private void SaveLightBakeWhenDone()
    {
        if (!lightBakeSaving || texelFrame - lightBakeDoneFrame < 4u) return;
        lightBakeSaving = false;
        var path = lightBakePath!;
        lightBakePath = null;
        var state = new TexelLightState(
            MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelLightBuffer, 0, texelCount * 16).AsSpan()).ToArray(),
            MemoryMarshal.Cast<byte, float>(device.ReadGpuBuffer(texelMomentBuffer, 0, texelCount * 4).AsSpan()).ToArray(),
            MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelBlendBuffer, 0, texelCount * 16).AsSpan()).ToArray(),
            MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelOpenBuffer, 0, texelCount * 16).AsSpan()).ToArray());
        Task.Run(() => WriteLightBake(path, state));
    }

    // The sun turned while baking: the patches' light the texels gather from is re-solved and the epoch moves on, so
    // what is baked so far is for a sun that is gone. Stop; the texels then gather as the image reads them.
    private void AbortLightBake(string why)
    {
        if (!lightBakeActive) return;
        lightBakeActive = false;
        lightBakePath = null;
        Console.WriteLine($"[VulkanSponza] light bake: stopped ({why}); not saved.");
    }
}
