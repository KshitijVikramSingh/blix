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
//
// Done means what the mark means by it (texel.glsl, texelSettled): the target's rays, and then either the noise under
// --texel-noise (5% standard error) or --texel-max-rays. A sweep of every live texel to the target leaves about half of
// them short of that (Sponza: 50.7% settled at 512 rays, the rest at a median 13.6% error), and a run that loaded such a
// bake went on gathering them -- ~2 M Metal rays a frame with the sun still, the frame 4x slower. So after the sweep the
// bake REFINES: it reads the light back, keeps the texels the mark would still queue, gives each RefinePasses more
// passes, and again, until none is left. A loaded bake is then settled everywhere, and a frame gathers only what
// changed (the sun, an epoch) or was never baked.
internal sealed partial class SponzaLoop
{
    // --light-bake-budget N: texels a frame while baking, one 32-ray pass each. By default 16,384 (~0.5 M rays) on the
    // software traversal; on Metal 131,072 (~4.2 M rays, ~550 MB of wavefront buffers) -- a rebake of Sponza's 11 M live
    // texels took ~47 minutes at the software budget, the image's own texels waiting behind it.
    private int? lightBakeBudgetAsked;
    private int LightBakeBudget => lightBakeBudgetAsked ?? (FrameRaysOnHardware ? 131072 : 16384);
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

    // 4: refined until settled (above); a version 3 bake stopped at the target.
    private const int LightBakeVersion = 4;
    // Passes (of 32 rays) a refining round gives each texel still unsettled before the light is read back again.
    private const int RefinePasses = 4;
    // 0: the sweep to the target; then the refining rounds.
    private int lightBakeRound;
    private long lightBakeRays;
    // Per record on disk: light rgb (half), its rays (float), the blend rgb (half), the moment and the openness (half),
    // the openness's ray count (float).
    private const int LightBakeRecordBytes = 3 * 2 + 4 + 3 * 2 + 2 + 2 + 4;

    private void ReadLightBakeArgs(AppArgs args)
    {
        lightBakeOff = args.Flag("no-light-bake");
        sunRebake = !args.Flag("no-sun-rebake");
        if (args.Int("light-bake-budget") is { } b) lightBakeBudgetAsked = Math.Clamp(b, 1024, 1 << 18);
    }

    // The baked light's file: the transport cache's key (the scene, the lightmap files, the texel settings), the sun
    // the patches were solved for, and the target.
    private string LightBakePathFor(string transportKey, (Vector3 ToSun, Vector3 Irradiance) sun)
    {
        var spec = string.Create(CultureInfo.InvariantCulture,
            $"{transportKey};sun {sun.ToSun.X:F5},{sun.ToSun.Y:F5},{sun.ToSun.Z:F5};{sun.Irradiance.X:F4},{sun.Irradiance.Y:F4},{sun.Irradiance.Z:F4};target {texelTarget};settled {texelNoise:G6} {texelMaxRays};buried {LightmapBuriedRule};v{LightBakeVersion}");
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
    private void BeginLightBake(uint[] live, int count)
    {
        if (live.Length == 0) return;
        lightBakeLive = live.Length;
        lightBakeLiveBuffer = Own(device.CreateGpuBuffer(live.Length * 4, MemoryMarshal.AsBytes(live.AsSpan()), "sponza.light-bake.live"));
        var passes = (texelTarget + 31) / 32;
        lightBakeTotal = (long)lightBakeLive * passes;
        lightBakeIssued = 0;
        lightBakeRound = 0;
        lightBakeRays = lightBakeTotal * 32;
        lightBakeActive = true;
        lightBakeClock = Stopwatch.StartNew();
        lightBakeReported = -1;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] light bake: {lightBakeLive:N0} live texels of {count:N0}, {passes} passes of 32 rays each ({lightBakeTotal * 32 / 1e9:0.0} G rays), {LightBakeBudget:N0} a frame."));
    }

    // RecordTexels, while baking: the next slice into the gather's queue. Returns the gather's budget this frame.
    private int RecordLightBake()
    {
        // The sweep (or the round) is out: what it left unsettled gets another round, read back from what has run.
        if (lightBakeIssued >= lightBakeTotal && !BeginRefineRound()) return FinishLightBake();
        // No longer than the list: a texel twice in one slice would be gathered twice at once, into one sum.
        var slice = (int)Math.Min(LightBakeBudget, lightBakeLive);
        graph.Dispatch(lightBakePassHandle, new DispatchCommand(lightBakePipeline, (slice + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uBake", new Vector4Uniform(new Vector4(lightBakeIssued % lightBakeLive, slice, texelFrame & 1u, lightBakeLive))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("TexelLive", lightBakeLiveBuffer),
                new ShaderBufferBinding("TexelQueue", texelQueueBuffer),
            }));
        lightBakeIssued += slice;
        var percent = (int)(100 * Math.Min(lightBakeIssued, lightBakeTotal) / lightBakeTotal);
        if (percent / 5 != lightBakeReported / 5)
        {
            lightBakeReported = percent;
            var elapsed = lightBakeClock!.Elapsed.TotalMinutes;
            var left = percent > 0 ? elapsed * (100 - percent) / percent : double.NaN;
            // The sweep's estimate is the sweep's; a refining round's, its round's (how many follow is not known yet).
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] light bake{(lightBakeRound > 0 ? $", refining round {lightBakeRound}" : "")}: {percent}% ({elapsed:0.0} min{(percent > 0 && lightBakeRound == 0 ? $", ~{left:0.0} left in the sweep" : "")})."));
        }
        return slice;
    }

    // The bake is settled: the queue goes back to the mark.
    private int FinishLightBake()
    {
        lightBakeActive = false;
        lightBakeDoneFrame = texelFrame;
        // Saved resolved, at rest (SponzaLoop.TexelRest.cs: 8 B a texel); the full state only when the texels cannot rest
        // (--texel-inspect, the texel debug views), as before.
        lightBakeSaving = lightBakePath is not null && !TexelsMayRest;
        if (lightBakePath is not null && TexelsMayRest) pendingResolvedSave = ResolvedPathFor(lightBakePath);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] light bake: done in {lightBakeClock!.Elapsed.TotalMinutes:0.0} min ({lightBakeRays / 1e9:0.0} G rays, {lightBakeRound} refining round{(lightBakeRound == 1 ? "" : "s")})."));
        // Refined until settled (a saved bake: BeginRefineRound): the texels rest once filtered.
        if (lightBakePath is not null) SettleTexels("the bake settled");
        if (!lightBakeSaving) lightBakePath = null;
        // The frame still runs the gather with no slice queued: the queue's counts for this parity are the mark's to
        // fill from the next frame, so this one gathers nothing.
        return 0;
    }

    // Reads back the live texels' light and moments (after everything submitted so far: the last slice's gather was
    // recorded in an earlier frame) and keeps those the mark would still queue (texelSettled, as texel.glsl has it).
    // False when none is left. Only a bake that is saved refines.
    private bool BeginRefineRound()
    {
        if (lightBakePath is null) return false;
        var clock = Stopwatch.StartNew();
        var live = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(lightBakeLiveBuffer, 0, lightBakeLive * 4).AsSpan()).ToArray();
        var light = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelLightBuffer, 0, texelCount * 16).AsSpan());
        var moment = MemoryMarshal.Cast<byte, float>(device.ReadGpuBuffer(texelMomentBuffer, 0, texelCount * 4).AsSpan());
        var unsettled = new List<uint>();
        foreach (var id in live)
            if (!TexelSettled(light[(int)id], moment[(int)id])) unsettled.Add(id);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] light bake: {live.Length - unsettled.Count:N0} of {live.Length:N0} texels settled after {(lightBakeRound == 0 ? "the sweep" : $"round {lightBakeRound}")} ({clock.Elapsed.TotalSeconds:0.0} s to read back); {unsettled.Count:N0} refine."));
        if (unsettled.Count == 0) return false;
        Disown(lightBakeLiveBuffer);
        lightBakeLiveBuffer = Own(device.CreateGpuBuffer(unsettled.Count * 4, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(unsettled)), "sponza.light-bake.refine"));
        lightBakeLive = unsettled.Count;
        lightBakeTotal = (long)lightBakeLive * RefinePasses;
        lightBakeIssued = 0;
        lightBakeRound++;
        lightBakeReported = -1;
        lightBakeRays += lightBakeTotal * 32;
        return true;
    }

    // texel.glsl's texelSettled, on the CPU: the target's rays, then the noise under --texel-noise or the most rays.
    private bool TexelSettled(Vector4 light, float m2)
    {
        if (light.W < texelTarget) return false;
        if (light.W >= texelMaxRays) return true;
        var mean = 0.2126f * light.X + 0.7152f * light.Y + 0.0722f * light.Z;
        var variance = MathF.Max(m2 - mean * mean, 0f);
        var standardError = MathF.Sqrt(variance / (light.W / 32f));
        return standardError <= texelNoise * MathF.Max(mean, TexelNoiseFloor);
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

    // The sun turned and the patches' light was re-solved for it (FollowSun): the texels converge on the new sun the way
    // they did on the boot's -- every live texel baked and refined until settled, then the filter sweep and rest, the
    // result saved under the new sun's key -- rather than only where the mark happens to look. Gathered from the mark
    // alone, texels the view sampled rarely kept the old sun's light for long: ghost outlines of the old patch. While it
    // bakes, a texel from the old epoch counts as one pass (texel_gather.comp) and fades as the new rays land.
    // --no-sun-rebake: the mark alone, as before (the A/B).
    private bool sunRebake = true;
    private string? lightBakeKey;
    // The live texels' ids (what a bake gathers), kept for a re-bake: 4 B a texel, not the texels themselves.
    private uint[]? texelLiveIds;

    private void RebakeForSun((Vector3 ToSun, Vector3 Irradiance) sun)
    {
        AbortLightBake("the sun turned");
        if (!sunRebake || lightBakeOff || lightBakeKey is null || texelLiveIds is null
) return;
        if (!lightBakeLiveBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(lightBakeLiveBuffer);
        lightBakePath = LightBakePathFor(lightBakeKey, sun);
        WakeTexels(keepResting: true);
        BeginLightBake(texelLiveIds, texelCount);
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
