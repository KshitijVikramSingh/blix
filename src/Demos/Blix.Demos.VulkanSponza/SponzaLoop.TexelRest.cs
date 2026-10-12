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

    // RecordTexels, at rest: resolve once, and a few frames on let the gather's buffers go.
    private void RestTexels()
    {
        if (!TexelsMayRest || texelCount == 0) return;
        if (!texelResolveCurrent)
        {
            if (texelResolvedBuffer.Equals(default(GpuBufferHandle)))
                texelResolvedBuffer = Own(device.CreateGpuBuffer(texelCount * 8, name: "sponza.texels.resolved"));
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
