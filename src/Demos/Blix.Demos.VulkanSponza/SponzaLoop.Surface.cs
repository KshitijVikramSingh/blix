using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// Stage 4g-ix: world-anchored surface probes (Blix.Shaders/probe_surface.glsl). The clipmap follows the camera, so a
// surface is answered by one level and then a coarser one as the camera walks, and its light changes (--watch: 6.7%
// spread, 16% range, walking ~3 to ~15 m; the levels hold different answers, and blending or a taller level 0 only
// moved where that shows). A surface probe is level 0 fixed in the world: allocated by clipmap_mark.comp in the cells
// around surfaces the image sees (within --surface-radius of the camera), solved by clipmap_inject.comp as a slot after
// the clipmap's, read first by the incident pass and at probe rays' hits, the clipmap answering where none exists.
//
// --surface-probes N: the pool (0: off). Its index is dense over the scene's bounds at the base spacing -- 640 KB for
// Sponza, sized for a building; a hash replaces it when a world outgrows that. No eviction yet: a full pool stops
// allocating (the census says when), and the clipmap answers there.
internal sealed partial class SponzaLoop
{
    private int surfaceCapacity;
    private float surfaceRadius = 40f;
    private Int3 surfaceMinCell;
    private Int3 surfaceDims = new(1, 1, 1);
    private int surfaceTileRow0;
    private int surfaceColumns = 1;
    private GpuBufferHandle surfaceIndex;
    private GpuBufferHandle surfaceAlloc;

    private bool SurfaceProbesOn => surfaceCapacity > 0 && clipmap is not null;

    private void ReadSurfaceArgs(AppArgs args)
    {
        if (args.Int("surface-probes") is { } n) surfaceCapacity = Math.Clamp(n, 0, 1 << 18);
        if (args.Float("surface-radius") is { } r) surfaceRadius = Math.Max(1f, r);
    }

    // Called from CreateClipmap (before the scene's bounds are fitted): where surface tiles start in the atlases, and a
    // placeholder index (one cell nothing addresses) until CreateSurfaceIndex has the bounds. Returns the atlas height
    // the clipmap and the surface tiles need together.
    private int CreateSurfaceProbes(int clipmapAtlasHeight, int atlasWidth)
    {
        surfaceColumns = atlasWidth / 8;
        surfaceTileRow0 = clipmapAtlasHeight;
        surfaceIndex = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.surface.index.none"));
        surfaceAlloc = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.surface.alloc"));
        if (surfaceCapacity == 0) return clipmapAtlasHeight;
        return clipmapAtlasHeight + (surfaceCapacity + surfaceColumns - 1) / surfaceColumns * 8;
    }

    // Called once the scene's bounds are fitted (FitSceneVolumeToDrawables): the dense index over them.
    private void CreateSurfaceIndex()
    {
        if (!SurfaceProbesOn) return;
        var s = clipmapSpacing;
        var lo = new Int3((int)MathF.Floor(sceneBoundsMin.X / s) - 2, (int)MathF.Floor(sceneBoundsMin.Y / s) - 2, (int)MathF.Floor(sceneBoundsMin.Z / s) - 2);
        var hi = sceneBoundsMin + sceneBoundsSpan;
        var top = new Int3((int)MathF.Ceiling(hi.X / s) + 2, (int)MathF.Ceiling(hi.Y / s) + 2, (int)MathF.Ceiling(hi.Z / s) + 2);
        var dims = new Int3(top.X - lo.X, top.Y - lo.Y, top.Z - lo.Z);
        var cells = (long)dims.X * dims.Y * dims.Z;
        if (cells > 64L << 20) throw new InvalidOperationException($"surface probes: the scene's index would be {cells:N0} cells; this dense index is sized for a building (a hash is the next step).");
        surfaceMinCell = lo;
        surfaceDims = dims;
        surfaceIndex = Own(device.CreateGpuBuffer((int)cells * 4, new byte[cells * 4], "sponza.surface.index"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] surface probes: {surfaceCapacity:N0} slots at {s:0.##} m, within {surfaceRadius:0} m of the camera; index {dims.X}x{dims.Y}x{dims.Z} cells ({cells * 4 / 1024.0:0} KB); tiles from atlas row {surfaceTileRow0}."));
    }

    // The three vectors every reader takes (probe_surface.glsl's BlixSurfaceGrid).
    private (Vector4 Grid, Vector4 Dims, Vector4 Atlas) SurfaceUniforms(int clipSlots) => (
        new Vector4(surfaceMinCell.X, surfaceMinCell.Y, surfaceMinCell.Z, clipmapSpacing),
        new Vector4(surfaceDims.X, surfaceDims.Y, surfaceDims.Z, clipSlots),
        new Vector4(surfaceTileRow0, surfaceColumns, surfaceCapacity, SurfaceProbesOn ? 1f : 0f));

    private void WriteSurfaceCensus(uint[] stateWords, uint[] seenStamps, int clipSlots)
    {
        if (!SurfaceProbesOn) return;
        var allocated = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(surfaceAlloc, 0, 4).AsSpan())[0];
        var used = (int)Math.Min(allocated, (uint)surfaceCapacity);
        var solves = new List<uint>();
        var visible = 0;
        for (var k = 0; k < used; k++)
        {
            var w = stateWords[(clipSlots + k) * 4 + 3];
            if ((w & 1u) != 0) solves.Add((w >> 8) & 0xFFu);
            if (seenStamps[clipSlots + k] + 2 > clipmapFrame) visible++;
        }
        solves.Sort();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] surface probes: {allocated:N0} allocated of {surfaceCapacity:N0}{(allocated > surfaceCapacity ? " (FULL: the clipmap answers where they ran out)" : "")}, {solves.Count:N0} solved, "
            + $"solves median {(solves.Count > 0 ? solves[solves.Count / 2] : 0)} (p10 {(solves.Count > 0 ? solves[solves.Count / 10] : 0)}); {visible:N0} read by the image now."));
    }
}
