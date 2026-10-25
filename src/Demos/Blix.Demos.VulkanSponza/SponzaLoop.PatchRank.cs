using System.Globalization;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --patch-rank: a sun change's gather ordered and refined by patch, not texel by texel (tools/gi-model/patch_layers.py).
// Each patch weighs what of it is on screen -- its texels' pixels (texel_visible.comp, TexelPixels) per texel, plus a
// share for a texel off screen -- and how far the relit instant moved its light (texel_ratio.comp, SunMoved), the
// runtime's sign of its error. The coarse sweep and the fine round trace their patches by weight x moved (all of a
// patch's texels together); the refine rounds rank their candidates by the resolve's score x their patch's weight. The
// model, Crytek Sponza at today's in-frame throughput: the screen's error at the change's end 0.023-0.026 against
// 0.041-0.054 with the same rays (5/20/60 deg steps, 2026-10-25); the off-screen pays a little (0.028-0.031 vs
// 0.019-0.022).
internal sealed partial class SponzaLoop
{
    private bool patchRank;
    // --patch-rank-share S (0.05): a texel off screen weighs S pixels.
    private float patchRankShare = 0.05f;
    private GpuBufferHandle texelPixelsBuffer, sunMovedBuffer;
    // The host's patch of each texel (the cook's pick, as liveTexelPatch holds it); -1 none.
    private int[]? texelPatchHost;
    // Per patch: its screen weight (pixels a texel + the share) and its texels' mean move; null before a change's view.
    private float[]? patchWeight, patchMoved;
    private uint[]? texelPixelsHost;

    private void ParsePatchRank(AppArgs args)
    {
        patchRank = patchRank || args.Flag("patch-rank");
        if (args.Float("patch-rank-share") is { } s) patchRankShare = Math.Max(0f, s);
    }

    private bool PatchRankNow => patchRank && liveSkyGathered && texelPatchHost is not null;

    // The visible pass's pixel counts (texel_visible.comp binding 8): zeroed with its first band.
    private GpuBufferHandle TexelPixelsBinding(bool firstBand)
    {
        if (!PatchRankNow) return texelVisibleBits;
        if (texelPixelsBuffer.Equals(default(GpuBufferHandle)))
            texelPixelsBuffer = Own(device.CreateReadableGpuBuffer(checked(texelCount * 4), "sponza.patch-rank.texel-pixels"));
        if (firstBand) device.WriteMappedGpuBuffer(texelPixelsBuffer, 0, new byte[texelCount * 4]);
        return texelPixelsBuffer;
    }

    // ReadTexelVisible: the counts read where they lie (the same frames in flight past as the bits).
    private void ReadTexelPixels()
    {
        if (!PatchRankNow || texelPixelsBuffer.Equals(default(GpuBufferHandle))) return;
        texelPixelsHost ??= new uint[texelCount];
        device.ReadMappedGpuBuffer(texelPixelsBuffer, 0, MemoryMarshal.AsBytes(texelPixelsHost.AsSpan()));
    }

    // The ratio pass's moves (texel_ratio.comp binding 8): zeroed at mode 2's dispatch.
    private GpuBufferHandle SunMovedBinding(bool clear)
    {
        if (!PatchRankNow) return sunRatioBuffer;
        if (sunMovedBuffer.Equals(default(GpuBufferHandle)))
            sunMovedBuffer = Own(device.CreateReadableGpuBuffer((texelCount + 3) / 4 * 4, "sponza.patch-rank.sun-moved"));
        if (clear) device.WriteMappedGpuBuffer(sunMovedBuffer, 0, new byte[(texelCount + 3) / 4 * 4]);
        return sunMovedBuffer;
    }

    // SelectSunChangeTexels (the moves readable): each patch's weight and mean move over its live texels.
    private void BuildPatchRank()
    {
        patchWeight = patchMoved = null;
        if (!PatchRankNow || sunMovedBuffer.Equals(default(GpuBufferHandle)) || texelLiveIds is not { } live || transportSolve is not { } d) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var moved = new byte[(texelCount + 3) / 4 * 4];
        device.ReadMappedGpuBuffer(sunMovedBuffer, 0, moved);
        var patches = d.Patches;
        var pixels = new double[patches];
        var count = new int[patches];
        var move = new double[patches];
        var map = texelPatchHost!;
        var pix = texelPixelsHost;
        foreach (var id in live)
        {
            var p = map[id];
            if (p < 0 || p >= patches) continue;
            count[p]++;
            move[p] += moved[id] / 64.0;
            if (pix is not null) pixels[p] += pix[id] / 16.0;
        }
        var weight = new float[patches];
        var meanMove = new float[patches];
        var onScreen = 0;
        for (var p = 0; p < patches; p++)
        {
            if (count[p] == 0) continue;
            weight[p] = (float)(pixels[p] / count[p]) + patchRankShare;
            meanMove[p] = (float)(move[p] / count[p]);
            if (pixels[p] > 0) onScreen++;
        }
        patchWeight = weight;
        patchMoved = meanMove;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] patch rank: {onScreen:N0} of {patches:N0} patches on screen ({(pix is null ? 0 : pixels.Sum()):N0} pixels), their moves read; {clock.Elapsed.TotalMilliseconds:0} ms."));
    }

    private float PatchWeightOf(uint id)
    {
        if (patchWeight is not { } w) return 1f;
        var p = texelPatchHost![id];
        return p >= 0 && p < w.Length ? w[p] : patchRankShare;
    }

    // A pass's ids by their patch's weight x (its move + a floor), the patch's texels together, in id order within.
    private uint[] OrderByPatch(uint[] ids, string what)
    {
        if (patchWeight is not { } w || patchMoved is not { } m || ids.Length == 0) return ids;
        var map = texelPatchHost!;
        var keys = new float[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            var p = map[ids[i]];
            // Descending by score, then by patch (its texels together); a texel with no patch last.
            keys[i] = p >= 0 && p < w.Length ? -w[p] * (m[p] + 0.02f) : 0f;
        }
        var order = Enumerable.Range(0, ids.Length).ToArray();
        Array.Sort(order, (x, y) =>
        {
            var c = keys[x].CompareTo(keys[y]);
            if (c != 0) return c;
            c = map[ids[x]].CompareTo(map[ids[y]]);
            return c != 0 ? c : ids[x].CompareTo(ids[y]);
        });
        var sorted = order.Select(i => ids[i]).ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] patch rank: {what}'s {ids.Length:N0} texels by patch (screen weight x move); the first patch's score {-keys[order[0]]:G3}."));
        return sorted;
    }
}
