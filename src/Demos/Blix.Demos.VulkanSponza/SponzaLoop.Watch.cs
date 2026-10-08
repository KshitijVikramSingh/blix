using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --watch: does a surface's light depend on where the camera stands? (stage 4g-viii) It should not; the clipmap
// follows the camera, so as it walks a point is answered by one level and then a coarser one -- what the user saw as
// distant surfaces brightening and darkening while walking. At --watch-at (the walk's start, with --walk; else frame
// 1500) a 24x16 grid of CPU rays from the camera picks fixed surface points; every frame after, clipmap_watch.comp
// evaluates the shipped blended answer at those points and it is read back. At the shot the series is reported
// over the walk and over the hold after it: each point's spread (std / mean), range, and largest frame-to-frame step.
// A still camera's run is the floor (the probes keep re-solving): anything above it is the camera's doing.
internal sealed partial class SponzaLoop
{
    private bool watch;
    private int watchAt = 1500;
    private int watchCount;
    private GpuBufferHandle watchPoints;
    private GpuBufferHandle watchOut;
    private ShaderInterface clipmapWatchInterface = null!;
    private PassHandle clipmapWatchPassHandle;
    private PipelineHandle clipmapWatchPipeline;
    private readonly List<(int Frame, float[] Lum)> watchSeries = new();

    private void ReadWatchArgs(AppArgs args)
    {
        watch = args.Flag("watch");
        if (args.Int("watch-at") is { } at) watchAt = Math.Max(1, at);
    }

    private int WatchStart => walkTo is not null ? walkAt : watchAt;

    // Picks the points at the start frame, from the camera as it stands.
    private void PickWatchPoints()
    {
        if (!watch || watchCount > 0 || rayQueries is not { } scene || clipmap is null || !fullyLoaded) return;
        if (postLoadFrames < WatchStart) return;
        Matrix4x4.Invert(viewProj, out var invViewProj);
        var data = new List<Vector4>();
        const int gx = 24, gy = 16;
        for (var y = 0; y < gy; y++)
        for (var x = 0; x < gx; x++)
        {
            var ndc = new Vector2(((x + 0.5f) / gx * 2f - 1f) * 0.9f, ((y + 0.5f) / gy * 2f - 1f) * 0.9f);
            var far = Vector4.Transform(new Vector4(ndc, 1f, 1f), invViewProj);
            var dir = Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - cameraPosition);
            if (scene.Closest(new Ray(cameraPosition, dir), 0f, float.PositiveInfinity, (uint)(y * gx + x + 1)) is not { } hit) continue;
            var inst = scene.Instances[hit.Instance];
            var m = inst.Mesh;
            var a = Vector3.Transform(m.Positions[m.Indices[hit.Triangle * 3]], inst.World);
            var b = Vector3.Transform(m.Positions[m.Indices[hit.Triangle * 3 + 1]], inst.World);
            var c = Vector3.Transform(m.Positions[m.Indices[hit.Triangle * 3 + 2]], inst.World);
            var n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() <= 0f) continue;
            n = Vector3.Normalize(n);
            if (Vector3.Dot(n, dir) > 0f) n = -n;
            var p = cameraPosition + dir * hit.T;
            data.Add(new Vector4(p, (p - cameraPosition).Length()));
            data.Add(new Vector4(n, 0f));
        }
        watchCount = data.Count / 2;
        if (watchCount == 0) return;
        watchPoints = Own(device.CreateGpuBuffer(data.Count * 16, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(data)), "sponza.clipmap.watch-points"));
        watchOut = Own(device.CreateGpuBuffer(watchCount * 16, name: "sponza.clipmap.watch-out"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] watch: {watchCount} surface points from the camera at {cameraPosition} (frame {postLoadFrames}), "
            + $"distance median {data.Where((_, k) => k % 2 == 0).Select(v => v.W).OrderBy(v => v).ElementAt(watchCount / 2):0.0} m."));
    }

    private void RecordClipmapWatch()
    {
        if (watchCount == 0 || clipmap is null) return;
        var origins = clipmap.Origins;
        var surface = SurfaceUniforms(ClipmapLevels * clipmap.ProbesPerLevel);
        graph.Dispatch(clipmapWatchPassHandle, new DispatchCommand(clipmapWatchPipeline, (watchCount + 63) / 64, 1, 1, new ShaderUniform[]
        {
            new("uDims", new Vector4Uniform(new Vector4(ClipmapDims.X, ClipmapDims.Y, ClipmapDims.Z, clipmapSpacing))),
            new("uParams", new Vector4Uniform(new Vector4(ClipmapBlend, watchCount, 0f, 0f))),
            new("uOrigin0", new Vector4Uniform(new Vector4(origins[0].X, origins[0].Y, origins[0].Z, 0f))),
            new("uOrigin1", new Vector4Uniform(new Vector4(origins[1].X, origins[1].Y, origins[1].Z, 0f))),
            new("uOrigin2", new Vector4Uniform(new Vector4(origins[2].X, origins[2].Y, origins[2].Z, 0f))),
            new("uOrigin3", new Vector4Uniform(new Vector4(origins[3].X, origins[3].Y, origins[3].Z, 0f))),
            new("uSurfaceGrid", new Vector4Uniform(surface.Grid)),
            new("uSurfaceDims", new Vector4Uniform(surface.Dims)),
            new("uSurfaceAtlas", new Vector4Uniform(surface.Atlas)),
        }, new[]
        {
            new ShaderTextureBinding("uClipmapIrradiance", clipmapIrradiance),
            new ShaderTextureBinding("uClipmapDepth", clipmapDepth),
        }, Buffers: new[]
        {
            new ShaderBufferBinding("ClipmapState", clipmapState),
            new ShaderBufferBinding("WatchPoints", watchPoints),
            new ShaderBufferBinding("WatchOut", watchOut),
            new ShaderBufferBinding("SurfaceIndex", surfaceIndex),
        }));
    }

    // Each frame: what the last completed frame evaluated at the points.
    private void SampleWatch()
    {
        if (watchCount == 0 || shotPath is null || shotWritten) return;
        var words = MemoryMarshal.Cast<byte, float>(device.ReadGpuBuffer(watchOut, 0, watchCount * 16).AsSpan());
        var lum = new float[watchCount];
        for (var i = 0; i < watchCount; i++)
        {
            lum[i] = words[i * 4 + 3] > 0.5f
                ? 0.2126f * words[i * 4] + 0.7152f * words[i * 4 + 1] + 0.0722f * words[i * 4 + 2]
                : float.NaN;
        }
        watchSeries.Add((postLoadFrames, lum));
    }

    private void WriteWatch()
    {
        if (watchSeries.Count < 2) return;
        WriteWatchedProbes();
        var walkEnd = walkTo is not null ? walkAt + walkFrames : WatchStart;
        // Two frames in: the frame that picks the points reads a buffer nothing has written yet.
        var first = WatchStart + 2;
        foreach (var (name, lo, hi) in new[] { ("walking", first, walkEnd), (walkTo is not null ? "after it stopped" : "camera still", Math.Max(walkEnd, first), int.MaxValue) })
        {
            var frames = watchSeries.Where(s => s.Frame >= lo && s.Frame < hi).Select(s => s.Lum).ToArray();
            if (frames.Length < 2) continue;
            var spread = new List<double>();
            var range = new List<double>();
            var step = new List<double>();
            for (var i = 0; i < watchCount; i++)
            {
                var v = frames.Select(f => (double)f[i]).ToArray();
                if (v.Any(double.IsNaN)) continue;
                var mean = v.Average();
                if (mean < 1e-5) continue;
                var std = Math.Sqrt(v.Select(x => (x - mean) * (x - mean)).Average());
                spread.Add(std / mean);
                range.Add((v.Max() - v.Min()) / mean);
                step.Add(Enumerable.Range(1, v.Length - 1).Max(k => Math.Abs(v[k] - v[k - 1])) / mean);
            }
            if (spread.Count == 0) continue;
            var firstMean = frames[0].Where(v => !float.IsNaN(v)).DefaultIfEmpty(0f).Average();
            var lastMean = frames[^1].Where(v => !float.IsNaN(v)).DefaultIfEmpty(0f).Average();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] watch, {name}: mean light over the points {firstMean:0.0000} at its first frame, {lastMean:0.0000} at its last"));
            string Q(List<double> x) { x.Sort(); return string.Create(CultureInfo.InvariantCulture, $"median {100 * x[x.Count / 2]:0.00}%, p90 {100 * x[x.Count * 9 / 10]:0.00}%"); }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] watch, {name} ({frames.Length} frames, {spread.Count} points): spread (std/mean) {Q(spread)}; range {Q(range)}; largest step {Q(step)}"));
        }
    }

    // The surface probes behind the watched points: how many exist, their solves, and whether the image stamped them
    // lately -- what a series that does or does not move is made of.
    private void WriteWatchedProbes()
    {
        if (!SurfaceProbesOn || watchCount == 0 || clipmap is null) return;
        var cells = (int)((long)surfaceDims.X * surfaceDims.Y * surfaceDims.Z);
        var index = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(surfaceIndex, 0, cells * 4).AsSpan()).ToArray();
        var clipSlots = ClipmapLevels * clipmap.ProbesPerLevel;
        var states = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(clipmapState, 0, (clipSlots + surfaceCapacity) * 16).AsSpan()).ToArray();
        var seenAll = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(clipmapSeen, 0, (clipSlots + surfaceCapacity) * 4).AsSpan()).ToArray();
        var points = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(watchPoints, 0, watchCount * 32).AsSpan()).ToArray();
        var solves = new List<uint>();
        int missing = 0, stale = 0;
        for (var i = 0; i < watchCount; i++)
        {
            var p = new Vector3(points[2 * i].X, points[2 * i].Y, points[2 * i].Z) + new Vector3(points[2 * i + 1].X, points[2 * i + 1].Y, points[2 * i + 1].Z) * (0.25f * clipmapSpacing);
            var g = p / clipmapSpacing - new Vector3(0.5f);
            var cell = new Int3((int)MathF.Floor(g.X), (int)MathF.Floor(g.Y), (int)MathF.Floor(g.Z));
            var c = new Int3(cell.X - surfaceMinCell.X, cell.Y - surfaceMinCell.Y, cell.Z - surfaceMinCell.Z);
            if (c.X < 0 || c.Y < 0 || c.Z < 0 || c.X >= surfaceDims.X || c.Y >= surfaceDims.Y || c.Z >= surfaceDims.Z) { missing++; continue; }
            var v = index[(c.Z * surfaceDims.Y + c.Y) * surfaceDims.X + c.X];
            if (v == 0 || v >= 0xFFFFFFFEu) { missing++; continue; }
            var slot = clipSlots + (int)(v - 1);
            solves.Add((states[slot * 4 + 3] >> 8) & 0xFFu);
            if (seenAll[slot] + 2 <= clipmapFrame) stale++;
        }
        solves.Sort();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] watch, the probes behind the points (one corner each): {solves.Count} found, {missing} missing; solves median {(solves.Count > 0 ? solves[solves.Count / 2] : 0)}, min {(solves.Count > 0 ? solves[0] : 0)}, max {(solves.Count > 0 ? solves[^1] : 0)}; not stamped in the last two frames {stale}; clipmap frame {clipmapFrame}."));
    }
}
