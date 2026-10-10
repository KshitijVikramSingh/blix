using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --mover NAME (stage 4e-vi): one placement instance swings, so every temporal consumer meets a surface that moves.
// The instance is the first (or --mover-pick'th) whose drawable name contains NAME; all its rows move together, about
// a horizontal axis through the top of their bounds, by --mover-angle degrees either side, one swing per
// --mover-period seconds of frame time (60 frames a second, counted from the first lit frame, so two runs pose it
// identically). --mover-hold F holds it at frame F's pose for the whole run: the still, converged answer a moving run's
// shot is held against.
//
// Each frame the moved rows' previous matrices are written before their new ones: on the GPU path by mover.comp
// (GPU buffers take no host writes after creation, and a dispatch is ordered after every earlier read of what it
// binds), on the CPU path into this frame slot's set 3. Bounds cover the whole swing, so culling needs no update.
// The ray scene moves with it: its rows are dynamic placements whose reach is the swing (RayQueryScene.Instance.Reach),
// moved in the CPU scene and rewritten in the GPU's instance rows by mover_rays.comp, so probes and the clipmap trace
// the curtain where it is drawn. (Left at rest, a probe on the swung curtain traced into its own resting copy: the
// moving curtain's probes read 0.0042 mean radiance against 0.0109 held still, 0.0007 swung toward the wall.)
internal sealed partial class SponzaLoop
{
    // Rows per mover.comp dispatch (its uniform arrays); an instance with more takes several.
    private const int MoverBatchRows = 32;
    private string? moverName;
    private int moverPick;
    private float moverAngleDegrees = 25f;
    private float moverPeriodSeconds = 2f;
    private int? moverHold;
    private int[] moverRows = Array.Empty<int>();
    private Matrix4x4[] moverRest = Array.Empty<Matrix4x4>();
    private readonly HashSet<uint> moverKeys = new();
    private Vector3 moverPivot, moverAxis;
    // Everything the mover can occupy through its swing: what a path must cross to depend on it (stage 4f).
    private Bounds3 moverReach;
    private Matrix4x4 moverMotion = Matrix4x4.Identity;
    private int moverFrame;
    private bool moverMoved;
    // The pose (pivot motion) of the last three frames, oldest first, beside surfaceCheckViewProj.
    private readonly Matrix4x4[] moverMotionHistory = { Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity };
    // The CPU path's whole previous-transform table (set 3 binding 4 is written whole per frame slot).
    private Matrix4x4[]? moverPreviousCpu;
    private PassHandle moverPassHandle;
    private PipelineHandle moverPipeline;
    private ShaderInterface? moverInterface;
    private PassHandle moverRaysPassHandle;
    private PipelineHandle moverRaysPipeline;
    private ShaderInterface? moverRaysInterface;

    private bool MoverActive => moverRows.Length > 0;

    private void ParseMoverArgs()
    {
        moverName = args.String("mover");
        if (args.Int("mover-pick") is { } pick) moverPick = Math.Max(0, pick);
        if (args.Float("mover-angle") is { } angle) moverAngleDegrees = Math.Clamp(angle, 0f, 80f);
        if (args.Float("mover-period") is { } period) moverPeriodSeconds = Math.Max(0.1f, period);
        moverHold = args.Int("mover-hold");
        // The cooked transport treats the scene as static, and the cook (in the background, by default) reads the same
        // ray scene the mover moves: cooking it mid-swing would be wrong whichever thread did it. What moving geometry
        // does to cooked transport is stage 5's R4; until then the two are exclusive.
        if (moverName is not null && (args.Flag("transport-gpu") || args.Flag("transport-texels") || args.Flag("transport")))
            throw new AppArgsException("--mover cannot run with the cooked transport (--transport, --transport-gpu, --transport-texels): the cook takes the scene as static, and moving geometry is stage 5's R4.");
    }

    // The swing at a frame: about the pivot, row-vector form (world point · rest · motion).
    private Matrix4x4 MoverMotionAt(int frame)
    {
        var theta = moverAngleDegrees * MathF.PI / 180f * MathF.Sin(2f * MathF.PI * frame / (60f * moverPeriodSeconds));
        return Matrix4x4.CreateTranslation(-moverPivot) * Matrix4x4.CreateFromAxisAngle(moverAxis, theta)
            * Matrix4x4.CreateTranslation(moverPivot);
    }

    // A world box widened to everything it sweeps through the swing, with a centimetre for the arcs between samples.
    private Bounds3 Swept(Bounds3 box)
    {
        var swept = box;
        for (var k = 0; k <= 16; k++)
        {
            var theta = moverAngleDegrees * MathF.PI / 180f * (k / 8f - 1f);
            var m = Matrix4x4.CreateTranslation(-moverPivot) * Matrix4x4.CreateFromAxisAngle(moverAxis, theta)
                * Matrix4x4.CreateTranslation(moverPivot);
            swept = Union(swept, WorldBounds(box, m));
        }
        return new Bounds3(swept.Min - new Vector3(0.01f), swept.Max + new Vector3(0.01f));
    }

    // After the placement rows exist, before the ray scene is built: choose the instance, its pivot and axis, widen its
    // bounds to the swing, and make its ray rows dynamic with the swing as their reach.
    private void SelectMover(List<RayQueryScene.Instance>? rayInstances)
    {
        if (moverName is null) return;
        var candidates = new List<PlacementInstance>();
        foreach (var (list, drawables) in new[] { (opaquePlacements, opaqueDrawables), (blendPlacements, blendDrawables) })
            foreach (var p in list)
                if (drawables[p.Drawable].Name.Contains(moverName, StringComparison.OrdinalIgnoreCase)
                    && !candidates.Contains(sceneTransformInstances[p.Transform]))
                    candidates.Add(sceneTransformInstances[p.Transform]);
        if (candidates.Count == 0) throw new AppArgsException($"--mover {moverName}: no drawable's name contains it.");
        for (var c = 0; c < candidates.Count; c++)
        {
            var names = new SortedSet<string>();
            Bounds3? b = null;
            foreach (var (list, drawables) in new[] { (opaquePlacements, opaqueDrawables), (blendPlacements, blendDrawables) })
                foreach (var p in list)
                    if (sceneTransformInstances[p.Transform] == candidates[c]) { names.Add(drawables[p.Drawable].Name); b = b is { } u ? Union(u, p.Bounds) : p.Bounds; }
            var e = b!.Value.Max - b.Value.Min;
            Console.WriteLine(string.Create(Inv, $"    mover candidate {c}: {string.Join(' ', names.Take(3))}{(names.Count > 3 ? " ..." : "")}  {e.X:0.00} x {e.Y:0.00} x {e.Z:0.00} m at {b.Value.Min.X:0.0},{b.Value.Min.Y:0.0},{b.Value.Min.Z:0.0}"));
        }
        var chosen = candidates[Math.Min(moverPick, candidates.Count - 1)];
        var rows = Enumerable.Range(0, sceneTransforms.Count).Where(r => sceneTransformInstances[r] == chosen).ToArray();
        moverRows = rows;
        moverRest = rows.Select(r => sceneTransforms[r]).ToArray();
        foreach (var r in rows)
        {
            sceneTransformSurfaceKeys[r] |= DynamicSurface;
            moverKeys.Add(sceneTransformSurfaceKeys[r]);
        }

        Bounds3? union = null;
        foreach (var list in new[] { opaquePlacements, blendPlacements })
            foreach (var p in list)
                if (sceneTransformInstances[p.Transform] == chosen) union = union is { } u ? Union(u, p.Bounds) : p.Bounds;
        var rest = union!.Value;
        var size = rest.Max - rest.Min;
        moverPivot = new Vector3((rest.Min.X + rest.Max.X) * 0.5f, rest.Max.Y, (rest.Min.Z + rest.Max.Z) * 0.5f);
        // Swing about the longer horizontal extent, so a sheet (a curtain) swings out of its plane.
        moverAxis = size.X >= size.Z ? Vector3.UnitX : Vector3.UnitZ;

        // Every placement of the instance covers its whole swing.
        foreach (var list in new[] { opaquePlacements, blendPlacements })
            for (var i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (sceneTransformInstances[p.Transform] != chosen) continue;
                list[i] = p with { Bounds = Swept(p.Bounds) };
            }
        moverReach = Swept(rest);
        if (rayInstances is not null)
            foreach (var r in rows)
                rayInstances[r] = rayInstances[r] with
                {
                    Dynamic = true, Reach = Swept(RayQueryScene.WorldBounds(rayInstances[r].Mesh.Bounds, rayInstances[r].World)),
                };
        if (!gpuCull) moverPreviousCpu = sceneTransforms.ToArray();
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] mover: {chosen} ({candidates.Count} candidate instances for '{moverName}'), {rows.Length} rows, keys {string.Join(',', moverKeys.Select(k => (k & 0x3FFFFFFFu).ToString()))}; rest bounds {size.X:0.00} x {size.Y:0.00} x {size.Z:0.00} m, pivot {moverPivot.X:0.00},{moverPivot.Y:0.00},{moverPivot.Z:0.00}, axis {(moverAxis.X > 0 ? "x" : "z")}, +-{moverAngleDegrees:0} deg every {moverPeriodSeconds:0.0} s{(moverHold is { } h ? $", held at frame {h}" : "")}"));
    }

    // Once per lit frame, before anything culls or draws: the new pose, and last frame's as the rows' previous.
    private void UpdateMover()
    {
        if (!MoverActive) return;
        var previous = moverMotion;
        moverMotion = MoverMotionAt(moverHold ?? moverFrame);
        moverFrame++;
        moverMoved = previous != moverMotion;
        var now = new Matrix4x4[moverRows.Length];
        var then = new Matrix4x4[moverRows.Length];
        for (var i = 0; i < moverRows.Length; i++)
        {
            now[i] = moverRest[i] * moverMotion;
            then[i] = moverRest[i] * previous;
            sceneTransforms[moverRows[i]] = now[i];
        }

        if (gpuCull)
        {
            for (var start = 0; start < moverRows.Length; start += MoverBatchRows)
            {
                var count = Math.Min(MoverBatchRows, moverRows.Length - start);
                var rows = new float[MoverBatchRows];
                var batchNow = new Matrix4x4[MoverBatchRows];
                var batchThen = new Matrix4x4[MoverBatchRows];
                for (var i = 0; i < count; i++)
                {
                    rows[i] = moverRows[start + i];
                    batchNow[i] = now[start + i];
                    batchThen[i] = then[start + i];
                }
                graph.Dispatch(moverPassHandle, new DispatchCommand(moverPipeline, 1, 1, 1,
                new ShaderUniform[]
                {
                    new("uCount", new Vector4Uniform(new Vector4(count, 0f, 0f, 0f))),
                    new("uRows", new FloatArrayUniform(rows)),
                    new("uNow", new Matrix4x4ArrayUniform(batchNow)),
                    new("uPrevious", new Matrix4x4ArrayUniform(batchThen)),
                },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new ShaderBufferBinding[]
                {
                    new("SceneTransforms", sceneTransformBuffer), new("ScenePreviousTransforms", scenePreviousTransformBuffer),
                }));
            }
        }
        else if (sceneInstances is { } instances && moverPreviousCpu is { } previousCpu)
        {
            for (var i = 0; i < moverRows.Length; i++) previousCpu[moverRows[i]] = then[i];
            instances.WriteBuffer(device.CurrentFrameSlot, 0, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(sceneTransforms)));
            instances.WriteBuffer(device.CurrentFrameSlot, 4, MemoryMarshal.AsBytes(previousCpu.AsSpan()));
        }

        // The ray scene: the CPU's (the oracle checks read) and the GPU's instance rows (what every tracer reads).
        if (rayQueries is null) return;
        for (var i = 0; i < moverRows.Length; i++) rayQueries.Move(moverRows[i], now[i]);
        if (rayBlockBuffers.FirstOrDefault(b => b.Name == "BlixRayInstances") is not { } rayInstancesBuffer) return;
        for (var start = 0; start < moverRows.Length; start += MoverBatchRows)
        {
            var count = Math.Min(MoverBatchRows, moverRows.Length - start);
            var entries = new float[MoverBatchRows];
            var worldToLocal = new Matrix4x4[MoverBatchRows];
            for (var i = 0; i < count; i++)
            {
                entries[i] = rayQueries.EntryOf(moverRows[start + i]);
                Matrix4x4.Invert(now[start + i], out worldToLocal[i]);
            }
            graph.Dispatch(moverRaysPassHandle, new DispatchCommand(moverRaysPipeline, 1, 1, 1,
                new ShaderUniform[]
                {
                    new("uCount", new Vector4Uniform(new Vector4(count, 0f, 0f, 0f))),
                    new("uEntries", new FloatArrayUniform(entries)),
                    new("uWorldToLocal", new Matrix4x4ArrayUniform(worldToLocal)),
                },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new[] { rayInstancesBuffer }));
        }
    }

    private static Bounds3 Union(Bounds3 a, Bounds3 b) => new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));

    private void RecordMoverPose()
    {
        moverMotionHistory[0] = moverMotionHistory[1];
        moverMotionHistory[1] = moverMotionHistory[2];
        moverMotionHistory[2] = moverMotion;
    }

    // At the shot: the pose index, and which pixels the mover covers (its keys in the surface-key target), as a PGM
    // a comparison can dilate into the region where history around the mover is at stake.
    private void WriteMoverShot(string basePath)
    {
        if (!MoverActive) return;
        Console.WriteLine($"[VulkanSponza] mover at the shot: frame {moverFrame - 1} pose (hold {(moverHold?.ToString() ?? "none")}), moved this frame: {moverMoved}");
        if (!SurfaceTargets) return;
        var keys = device.ReadTexture(graph.GetColorTexture(surfaceKeyHandle), out var w, out var h, out _);
        var pgm = new byte[w * h];
        var covered = 0;
        for (var i = 0; i < w * h; i++)
            if (moverKeys.Contains(BitConverter.ToUInt32(keys, i * 4))) { pgm[i] = 255; covered++; }
        using var f = File.Create(basePath + ".mover.pgm");
        f.Write(System.Text.Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n"));
        f.Write(pgm);
        Console.WriteLine($"[VulkanSponza] mover covers {covered} pixels ({100.0 * covered / (w * h):0.00}%)");

        // What is presented (TAA's resolve, as --stability reads it) and the incident light the lit pass shades with,
        // as luminance, raw little-endian floats: a moving run's are held against a held run's, around the mask.
        WriteLuminance(basePath + ".resolved.f32", render.Taa > 0f ? taaHandles[taaWrite] : hdrHandle);
        WriteLuminance(basePath + ".incident.f32", incidentHandle);
        // And the lit image before TAA: what TAA's history adds is the resolved image's error less this one's.
        WriteLuminance(basePath + ".scene.f32", hdrHandle);
    }

    private void WriteLuminance(string path, GraphResourceHandle target)
    {
        static float Lum(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;
        var pixels = device.ReadTexture(graph.GetColorTexture(target), out var w, out var h, out var format);
        var lum = new float[w * h];
        for (var i = 0; i < w * h; i++)
        {
            if (format == TextureFormat.Rgba16F)
            {
                lum[i] = Lum((float)BitConverter.ToHalf(pixels, i * 8), (float)BitConverter.ToHalf(pixels, i * 8 + 2), (float)BitConverter.ToHalf(pixels, i * 8 + 4));
            }
            else
            {
                var p = BitConverter.ToUInt32(pixels, i * 4);
                lum[i] = Lum(UnpackFloat(p & 0x7FF, 6), UnpackFloat((p >> 11) & 0x7FF, 6), UnpackFloat((p >> 22) & 0x3FF, 5));
            }
        }
        using var f = File.Create(path);
        f.Write(BitConverter.GetBytes(w));
        f.Write(BitConverter.GetBytes(h));
        f.Write(MemoryMarshal.AsBytes(lum.AsSpan()));
    }
}
