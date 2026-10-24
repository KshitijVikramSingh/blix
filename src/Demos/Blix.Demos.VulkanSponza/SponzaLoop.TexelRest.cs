using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The texels at rest. Once they are settled and filtered (RecordTexels' rest), every texel's answer is resolved once
// (texel_resolve.comp: RGB9E5 + a confidence, 8 bytes) and the incident pass reads that (uTexelDebug.y); a few frames
// later the gather's own buffers -- light, prior, filtered, blend, open, moment, stamp, epoch, the live list, ~100
// bytes a texel, 1.5 GB on Sponza -- are let go. When the light changes they are made again (zeroed: the re-bake
// gathers every live texel afresh), and the resolved answers stay what the image shows until the new bake has
// settled and is resolved in its place -- a sun change then shows no half-gathered patchwork. A gather by the mark
// alone (no re-bake: --no-sun-rebake, --no-light-bake) reads the gather's buffers again at once.
// Not with --texel-inspect or the texel debug views, which read the gather's buffers.
internal sealed partial class SponzaLoop
{
    private GpuBufferHandle texelResolvedBuffer;
    private ShaderInterface texelResolveInterface = null!;
    private PassHandle texelResolvePassHandle;
    private PipelineHandle texelResolvePipeline;
    // The incident pass reads the resolved answers (they are current, or the last light's while a re-bake runs).
    private bool texelResting;
    private bool texelGatherFreed;
    // The resolved answers are this light's (false from a wake until the next rest resolves again).
    private bool texelResolveCurrent;
    private uint texelResolvedFrame;
    private GpuBufferHandle texelRestPlaceholder;
    private bool TexelsMayRest => texelInspect is null && !texelDebug && !texelDebugRecords;
    // Where this light's resolved answers go once they are resolved (a bake just settled, or an old full-state cache
    // read): the cache a later run starts at rest from (ReadResolved). Null: nothing to save.
    private string? pendingResolvedSave;

    // The light's cache in its resting form: beside the light bake's path (the same key: transport, sun, settings).
    private static string ResolvedPathFor(string lightBakePath) => lightBakePath + ".resolved";
    private const int ResolvedVersion = 1;

    private static uint[]? ReadResolved(string path, int count)
    {
        if (!File.Exists(path)) return null;
        var clock = Stopwatch.StartNew();
        try
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);
            if (r.ReadString() != "blix-texel-resolved" || r.ReadInt32() != ResolvedVersion || r.ReadInt32() != count) return null;
            var words = new uint[count * 2];
            var bytes = MemoryMarshal.AsBytes(words.AsSpan());
            if (stream.Read(bytes) != bytes.Length) return null;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] texels: {count:N0} resolved answers read from the cache ({bytes.Length / 1048576.0:0} MB, {clock.Elapsed.TotalSeconds:0.0} s): {Path.GetFileName(path)}"));
            return words;
        }
        catch (IOException) { return null; }
    }

    private static void WriteResolved(string path, uint[] words)
    {
        var partial = path + ".partial";
        try
        {
            using (var stream = File.Create(partial))
            using (var w = new BinaryWriter(stream))
            {
                w.Write("blix-texel-resolved");
                w.Write(ResolvedVersion);
                w.Write(words.Length / 2);
                w.Write(MemoryMarshal.AsBytes(words.AsSpan()));
            }
            File.Move(partial, path, overwrite: true);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] texels: resolved answers saved ({new FileInfo(path).Length / 1048576.0:0} MB): {Path.GetFileName(path)}"));
        }
        catch (IOException e)
        {
            try { File.Delete(partial); } catch (IOException) { }
            Console.WriteLine($"[VulkanSponza] texels: resolved answers not saved ({e.Message}).");
        }
    }

    // UploadTexels with a resolved cache: the texels start at rest -- the resolved answers up, the gather's buffers never
    // made (placeholders until a light change wakes them), no filter sweep.
    private void StartTexelsAtRest(uint[] resolved)
    {
        texelResolvedBuffer = Own(device.CreateGpuBuffer(resolved.Length * 4, MemoryMarshal.AsBytes(resolved.AsSpan()), "sponza.texels.resolved"));
        texelResting = true;
        texelResolveCurrent = true;
        texelGatherFreed = true;
        texelsSettled = true;
        texelSettledEpoch = texelEpoch;
        texelsRestReported = true;
        filterSweepIssued = -1;
        Console.WriteLine("[VulkanSponza] texels: at rest from the cache -- the gather's buffers are not made until the light changes.");
    }

    // RecordTexels, at rest: resolve once, and a few frames on let the gather's buffers go.
    private void RestTexels()
    {
        if (!TexelsMayRest || texelCount == 0) return;
        if (!texelResolveCurrent)
        {
            if (texelResolvedBuffer.Equals(default(GpuBufferHandle)))
                texelResolvedBuffer = Own(device.CreateGpuBuffer(texelCount * 8, name: "sponza.texels.resolved"));
            lightmapCellsCurrent = false;   // the cells were the compose's light (SponzaLoop.LightmapCells.cs)
            // Every texel's light rewritten: the incident pass recomputes, as after a compose. A camera still four frames
            // had it skipped past this, the last answer kept -- Cornell's gate points 20-35% off, one run of four, under
            // the half-res lighting's skip at rest (2026-10-24).
            MarkIncidentDirty();
            graph.Dispatch(texelResolvePassHandle, new DispatchCommand(texelResolvePipeline, (texelCount + 63) / 64, 1, 1,
                new ShaderUniform[] { new("uResolve", new Vector4Uniform(new System.Numerics.Vector4(texelCount, texelFilter ? 1f : 0f, texelTrust, 0f))) },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new[]
                {
                    new ShaderBufferBinding("TexelLight", texelLightBuffer),
                    new ShaderBufferBinding("TexelPrior", texelPriorBuffer),
                    new ShaderBufferBinding("TexelFiltered", texelFilteredBuffer),
                    new ShaderBufferBinding("TexelBlend", texelBlendBuffer),
                    new ShaderBufferBinding("TexelResolved", texelResolvedBuffer),
                }));
            texelResting = true;
            texelResolveCurrent = true;
            texelResolvedFrame = texelFrame;
            litClipmapBuffer = null;
            return;
        }
        // Freed once the resolve has run (four frames, as RetireGpuBuffer counts) and no bake's save still reads them.
        if (texelGatherFreed || lightBakeSaving || texelFrame - texelResolvedFrame < 4u) return;
        if (pendingResolvedSave is { } savePath)
        {
            pendingResolvedSave = null;
            var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(texelResolvedBuffer, 0, texelCount * 8).AsSpan()).ToArray();
            Task.Run(() => WriteResolved(savePath, words));
        }
        if (texelRestPlaceholder.Equals(default(GpuBufferHandle)))
            texelRestPlaceholder = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.texels.rest-placeholder"));
        foreach (var b in new[] { texelLightBuffer, texelPriorBuffer, texelFilteredBuffer, texelBlendBuffer, texelOpenBuffer,
                     texelMomentBuffer, texelStampBuffer, texelEpochBuffer, texelLiveBuffer })
            if (!b.Equals(texelRestPlaceholder)) RetireGpuBuffer(b);
        texelLightBuffer = texelPriorBuffer = texelFilteredBuffer = texelBlendBuffer = texelOpenBuffer = texelRestPlaceholder;
        texelMomentBuffer = texelStampBuffer = texelEpochBuffer = texelLiveBuffer = texelRestPlaceholder;
        texelGatherFreed = true;
        litClipmapBuffer = null;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] texels: at rest -- the resolved answers read ({texelCount * 8 / 1048576.0:0} MB), the gather's buffers let go (~{texelCount * 100L / 1048576.0:N0} MB)."));
    }

    // Before any gather (a light change, a re-bake): the gather's buffers back, zeroed. keepResting: the incident pass
    // goes on reading the last resolved answers until the new ones are resolved (a re-bake settles them all).
    private void WakeTexels(bool keepResting)
    {
        texelResting = texelResting && keepResting && texelGatherFreed;
        texelResolveCurrent = false;
        if (!texelGatherFreed) return;
        var stamps = new uint[texelCount];
        Array.Fill(stamps, 0xFFFFFFFFu);
        texelLightBuffer = Own(device.CreateGpuBuffer(texelCount * 16, name: "sponza.texels.light"));
        texelPriorBuffer = Own(device.CreateGpuBuffer(texelCount * 16, name: "sponza.texels.prior"));
        texelFilteredBuffer = Own(device.CreateGpuBuffer(texelCount * 16, name: "sponza.texels.filtered"));
        texelBlendBuffer = Own(device.CreateGpuBuffer(texelCount * 16, name: "sponza.texels.blend"));
        texelOpenBuffer = Own(device.CreateGpuBuffer(texelCount * 16, name: "sponza.texels.open"));
        texelMomentBuffer = Own(device.CreateGpuBuffer(texelCount * 4, name: "sponza.texels.moment"));
        texelStampBuffer = Own(device.CreateGpuBuffer(texelCount * 4, MemoryMarshal.AsBytes(stamps.AsSpan()), "sponza.texels.stamp"));
        texelEpochBuffer = Own(device.CreateGpuBuffer(texelCount * 4, name: "sponza.texels.epoch"));
        var live = texelLiveIds ?? Array.Empty<uint>();
        texelLiveBuffer = Own(device.CreateGpuBuffer(Math.Max(live.Length, 1) * 4, MemoryMarshal.AsBytes(live.AsSpan()), "sponza.texels.live"));
        texelGatherFreed = false;
        litClipmapBuffer = null;
        Console.WriteLine($"[VulkanSponza] texels: awake -- the gather's buffers back{(texelResting ? "; the last resolved light shows until the new one settles" : "")}.");
    }
}
