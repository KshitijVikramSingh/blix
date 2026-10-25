using System.Globalization;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --patch-layers (with --patch-rank's order): a sun change's coarse sweep reaches the screen a layer at a time. The image
// reads the fine texels, and they take the coarse cells' light only at the sweep's end (the round end's fine copy): the
// screen sat on the relit light ~40 s into a 20 deg change on Crytek Sponza, whatever the sweep's order (2026-10-25). The
// sweep's ids, ordered by patch, are cut into PatchLayerCount layers at patch boundaries; once the sweep is past a
// layer's last texel, the fine texels of its patches copy from the coarse (transfer_fine.comp) a chunk a frame, and the
// compose shows them. The interim copy reads the coarse cells unfiltered (the filter writes the parts in place: run per
// layer it would filter cells twice) and writes its flags into scratch; the round end's filter and full copy follow as
// before and decide the fine picks. Each coarse refine round's end copies every layer again (the sweep took ~9 s of a 20
// deg change, its refine rounds ~30 s more, unseen until the round end's copy, 2026-10-25).
internal sealed partial class SponzaLoop
{
    private bool patchLayers;
    private const int PatchLayerCount = 16;
    // Each layer's end in the sweep's ids, and its fine texels; -1: no layers under way.
    private int[] patchLayerEnds = Array.Empty<int>();
    private uint[][] patchLayerFine = Array.Empty<uint[]>();
    private int patchLayerNext = -1, patchLayerCopyFirst, patchLayerRound;
    private GpuBufferHandle patchLayerIds, patchLayerFlags;
    private int patchLayerIdsCount;
    private readonly System.Diagnostics.Stopwatch patchLayerClock = new();

    // --patch-layers-coarse-rounds N (2): a sun change's coarse refine rounds before its fine round -- the fine picks (the
    // contacts, the edges: their own gather) waited ~30 s behind them, the screen with them. A 20 deg change on Crytek
    // Sponza, the screen at 20 / 40 s and landed: all rounds 0.121 / 0.093, 0.080 (110 s); 0 rounds 0.103 / 0.093, 0.090
    // (91 s); 2 rounds 0.098 / 0.087, 0.083 (95 s); today without layers 0.200 / 0.092, 0.086 (88 s), 2026-10-25.
    private int patchLayersCoarseRounds = 2;
    private bool PatchLayersCoarseNow => patchLayers && sunChangeMask is not null && !fineGathering && !visibleChangedGathering;

    private void ParsePatchLayers(AppArgs args)
    {
        patchLayers = args.Flag("patch-layers");
        if (args.Int("patch-layers-coarse-rounds") is { } cr) patchLayersCoarseRounds = Math.Max(0, cr);
        if (patchLayers) patchRank = true;
    }

    // SelectSunChangeTexels, the sweep's ids ordered by patch: the layers and their fine texels.
    private void BuildPatchLayers(uint[] sweep)
    {
        patchLayerNext = -1;
        if (!patchLayers || patchWeight is null || sweep.Length == 0 || texelLiveIds is not { } live || liveCoarseLevels is not { } levels) return;
        var map = texelPatchHost!;
        var ends = new List<int>();
        var layerOf = new Dictionary<int, int>();
        var target = (sweep.Length + PatchLayerCount - 1) / PatchLayerCount;
        var start = 0;
        for (var i = 0; i < sweep.Length; i++)
        {
            var p = map[sweep[i]];
            var last = i + 1 == sweep.Length;
            // A layer ends at its share's patch boundary (a patch's texels together in one layer).
            if (!last && (i + 1 - start < target || map[sweep[i + 1]] == p))
            {
                if (p >= 0) layerOf.TryAdd(p, ends.Count);
                continue;
            }
            if (p >= 0) layerOf.TryAdd(p, ends.Count);
            ends.Add(i + 1);
            start = i + 1;
        }
        // The fine texels the copy writes (FineCopyIds' rule), each with its patch's layer; patches the sweep left out none.
        var visible = visibleChangedMask;
        var moved = SelectFineNow ? sunChangeMask : null;
        var lists = Enumerable.Range(0, ends.Count).Select(_ => new List<uint>()).ToArray();
        foreach (var id in live)
        {
            if (levels[id] >= transferMinLevel) continue;
            if (visible is not null && (visible[id >> 5] >> (int)(id & 31) & 1u) != 0) continue;
            if (moved is not null && (moved[id >> 5] >> (int)(id & 31) & 1u) == 0) continue;
            var p = map[id];
            if (p >= 0 && layerOf.TryGetValue(p, out var layer)) lists[layer].Add(id);
        }
        patchLayerEnds = ends.ToArray();
        patchLayerFine = lists.Select(l => l.ToArray()).ToArray();
        patchLayerNext = 0;
        patchLayerCopyFirst = -1;
        patchLayerRound = 0;
        patchLayerClock.Restart();
        if (patchLayerFlags.Equals(default(GpuBufferHandle)))
            patchLayerFlags = Own(device.CreateReadableGpuBuffer((16 + 2 * Math.Max(1, transferFineCount)) * 4, "sponza.patch-layers.flags"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] patch layers: the sweep's {sweep.Length:N0} texels in {ends.Count} layers over {layerOf.Count:N0} patches; their fine texels {patchLayerFine.Sum(f => f.Length):N0} (the first layer's {patchLayerFine[0].Length:N0})."));
    }

    // Each frame of the coarse sweep: the next layer's fine copy, a chunk a frame, once the sweep is past its end.
    private void StepPatchLayers()
    {
        if (patchLayerNext < 0 && patchLayerRound < 0) return;
        if (fineGathering) { patchLayerNext = patchLayerRound = -1; return; }   // the round end's full copy took over
        if (sunNodeStage != SunNodeStage.Tracing || visibleChangedGathering) return;
        // A refine round of the coarse began: its layers again from the first, at once (the round's texels anywhere).
        if (sunNodeRefineRound > patchLayerRound && patchLayerCopyFirst < 0)
        {
            patchLayerRound = sunNodeRefineRound;
            patchLayerNext = 0;
        }
        if (patchLayerNext < 0 || patchLayerNext >= patchLayerEnds.Length) { patchLayerNext = -1; return; }
        if (patchLayerCopyFirst < 0)
        {
            if (sunNodeRefineRound == 0 && sunNodeFirst < patchLayerEnds[patchLayerNext]) return;
            var fine = patchLayerFine[patchLayerNext];
            if (fine.Length == 0) { NextPatchLayer(); return; }
            if (!patchLayerIds.Equals(default(GpuBufferHandle))) RetireGpuBuffer(patchLayerIds);
            patchLayerIds = Own(SweepBuffer(fine.Length * 4, MemoryMarshal.AsBytes(fine.AsSpan()), "sponza.patch-layers.ids"));
            patchLayerIdsCount = fine.Length;
            patchLayerCopyFirst = 0;
            // The copy appends its flagged texels at the scratch's counter (transfer_fine.comp, fineFlags[0]): zeroed each layer.
            // Never reset, the refine rounds' copies ran it past the buffer and over the refine's lists: a garbage round, the
            // screen green and the device lost (2026-10-25). A layer's still in flight when zeroed adds at most its own texels.
            device.WriteMappedGpuBuffer(patchLayerFlags, 0, new byte[64]);
        }
        // The scratch flags in place of the round end's (indexed by its list's places): swapped for this dispatch.
        var keep = fineFlagsBuffer;
        fineFlagsBuffer = patchLayerFlags;
        FineDispatch(patchLayerIds, patchLayerIdsCount, default, patchLayerCopyFirst, sweepFineChunk);
        fineFlagsBuffer = keep;
        patchLayerCopyFirst += sweepFineChunk;
        splitComposeDirty = true;
        if (patchLayerCopyFirst >= patchLayerIdsCount) NextPatchLayer();
    }

    private void NextPatchLayer()
    {
        if (patchLayerRound == 0 || patchLayerNext + 1 == patchLayerEnds.Length)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] patch layers: layer {patchLayerNext + 1} of {patchLayerEnds.Length} on screen ({patchLayerFine[patchLayerNext].Length:N0} fine texels copied){(patchLayerRound > 0 ? $" after refine round {patchLayerRound}" : "")}, {patchLayerClock.Elapsed.TotalSeconds:0.0} s into the sweep."));
        patchLayerNext++;
        patchLayerCopyFirst = -1;
    }
}
