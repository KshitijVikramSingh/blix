using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --occlusion-audit [--occlusion-audit-every N]: is two-phase occlusion culling dropping what it should not? Every N
// frames (30) the last completed frame's verdicts (the final list's Culled bits) are read back with its early pyramid,
// and scene_occlusion.comp's hidden() is replayed on the CPU against it, with that frame's matrices. A placement in the
// frustum that the frame left undrawn was judged hidden behind the EARLY pyramid; where the replay against that
// pyramid's own contents says visible (A), the test read something other than what the pyramid ended up holding (a
// pyramid not yet written, another frame's, a state word raced) -- a visible placement dropped, the tear. The
// pyramid's own reduction is checked level by level too (under-reduced texels). The replay assumes a camera that held
// still over the frame read (the matrices are the previous frame's); moves are skipped.
//
// It also held the verdicts against the frame's FINAL depth (B: the early pyramid holding depth the frame did not
// draw), through the main Hi-Z pyramid; that pyramid went with GTAO, its only other reader.
internal sealed partial class SponzaLoop
{
    private int occlusionAuditEvery;
    private AuditPyramid? auditPrevEarly;
    private long occlusionAuditFrame;
    private Matrix4x4 auditPrevViewProj, auditPrevView;
    private bool auditPrevValid;
    private int auditCount;
    private long auditCulled, auditA;

    private void AuditOcclusion()
    {
        if (occlusionAuditEvery <= 0 || !fullyLoaded) return;
        var still = auditPrevValid && auditPrevViewProj == viewProj && auditPrevView == cameraView;
        var due = ++occlusionAuditFrame % occlusionAuditEvery == 0;
        var frameViewProj = auditPrevViewProj;
        var frameView = auditPrevView;
        auditPrevViewProj = viewProj;
        auditPrevView = cameraView;
        auditPrevValid = true;
        if (!due || !still || !(occlusionCull && render.OcclusionCull)) return;

        device.WaitIdle();
        var early = ReadPyramid(occZHandles);
        if (early is null) return;
        var n = opaquePlacements.Count;
        var finalBase = SceneListVisibleBase(SceneListCameraFinal);
        var state = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(cullState, finalBase * 4, n * 4).AsSpan()).ToArray();

        var planes = FrustumPlanes(frameViewProj);
        int culled = 0, a = 0;
        var examples = new List<string>();
        for (var i = 0; i < n; i++)
        {
            if ((state[i] & 0x80000000u) == 0u) continue;
            var bounds = opaquePlacements[i].Bounds;
            if (!Intersects(planes, bounds.Min, bounds.Max, CameraCullMargin)) continue;
            culled++;
            var hiddenEarly = Hidden(early, frameViewProj, frameView, bounds.Min, bounds.Max);
            if (hiddenEarly) continue;
            a++;
            if (examples.Count < 6)
            {
                var c = (bounds.Min + bounds.Max) * 0.5f;
                var s = bounds.Max - bounds.Min;
                examples.Add(string.Create(CultureInfo.InvariantCulture,
                    $"placement {i} at ({c.X:0.0}, {c.Y:0.0}, {c.Z:0.0}) size ({s.X:0.0}, {s.Y:0.0}, {s.Z:0.0})"));
            }
        }
        auditCount++;
        auditCulled += culled;
        auditA += a;
        if (a > 0 || auditCount % 20 == 1)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] occlusion audit #{auditCount} (post-load frame {postLoadFrames}): {culled:N0} in-frustum placements culled; " +
                $"A (the early pyramid itself says visible) {a}; run so far: {auditCulled:N0} culled, A {auditA}."));
            foreach (var e in examples) Console.WriteLine($"[VulkanSponza]   {e}");
            Console.WriteLine($"[VulkanSponza]   under-reduced texels per level (a level nearer than its parent's max): {string.Join(" ", Enumerable.Range(1, HiZLevels - 1).Select(l => UnderReduced(early, l)))}");
            for (var l = 1; l < HiZLevels; l++)
            {
                var (pw, ph) = early.Size[l - 1];
                var (w, h) = early.Size[l];
                var step = (int)MathF.Round((float)pw / w);
                var span = (int)MathF.Ceiling((float)pw / w);
                var map = auditCount <= 4 ? new byte[w * h * 4] : null;
                int bad = 0, eqPrevEarly = 0, edge = 0;
                int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    float FromParent(float[] parent)
                    {
                        var m = float.MinValue;
                        for (var sy = 0; sy < span; sy++)
                        for (var sx = 0; sx < span; sx++)
                            m = MathF.Max(m, parent[Math.Clamp(y * step + sy, 0, ph - 1) * pw + Math.Clamp(x * step + sx, 0, pw - 1)]);
                        return m;
                    }
                    var k = y * w + x;
                    var e = early.Farthest[l][k];
                    if (map is not null)
                    {
                        var g = (byte)Math.Clamp(255f / (1f + e * 0.1f), 0f, 255f);
                        map[k * 4] = map[k * 4 + 1] = map[k * 4 + 2] = g; map[k * 4 + 3] = 255;
                    }
                    if (e >= FromParent(early.Farthest[l - 1]) * (1f - 1f / 512f)) continue;
                    if (map is not null) { map[k * 4] = 255; map[k * 4 + 1] = 0; map[k * 4 + 2] = 0; }
                    bad++;
                    bx0 = Math.Min(bx0, x); by0 = Math.Min(by0, y); bx1 = Math.Max(bx1, x); by1 = Math.Max(by1, y);
                    if (x >= w - 8 || y >= h - 8) edge++;
                    if (auditPrevEarly is { } pe && e == pe.Farthest[l][k]) eqPrevEarly++;
                }
                if (map is not null && bad > 0) Blix.Graphics.Images.PngWriter.WriteRgba8(Path.Combine(Path.GetTempPath(), $"blix-occz-audit{auditCount}-level{l}.png"), map, w, h);
                if (bad > 0) Console.WriteLine($"[VulkanSponza]   early level {l}: {bad} under-reduced; equal to the last audit's early level {eqPrevEarly}; level {w}x{h}, bad within x {bx0}..{bx1} y {by0}..{by1}, {edge} in the last 8 rows/columns");
            }
        }
        auditPrevEarly = early;
    }

    // Texels at this level whose farthest depth is nearer than the farthest of the parent texels they cover (as
    // hiz_build.frag picks them): the reduction lost depth, which makes the test too eager to hide.
    private static int UnderReduced(AuditPyramid p, int level)
    {
        var (pw, ph) = p.Size[level - 1];
        var (w, h) = p.Size[level];
        var parent = p.Farthest[level - 1];
        var mine = p.Farthest[level];
        var stepX = (int)MathF.Round((float)pw / w);
        var stepY = (int)MathF.Round((float)ph / h);
        var spanX = (int)MathF.Ceiling((float)pw / w);
        var spanY = (int)MathF.Ceiling((float)ph / h);
        var count = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var expect = float.MinValue;
            for (var sy = 0; sy < spanY; sy++)
            for (var sx = 0; sx < spanX; sx++)
            {
                var px = Math.Clamp(x * stepX + sx, 0, pw - 1);
                var py = Math.Clamp(y * stepY + sy, 0, ph - 1);
                expect = MathF.Max(expect, parent[py * pw + px]);
            }
            if (mine[y * w + x] < expect * (1f - 1f / 512f)) count++;
        }
        return count;
    }

    private sealed record AuditPyramid(float[][] Farthest, (int W, int H)[] Size);

    private AuditPyramid? ReadPyramid(GraphResourceHandle[] levels)
    {
        var far = new float[HiZLevels][];
        var size = new (int, int)[HiZLevels];
        for (var l = 0; l < HiZLevels; l++)
        {
            var pixels = device.ReadTexture(graph.GetColorTexture(levels[l]), out var w, out var h, out var format);
            // The levels are Rg16F (min, max); this read refused anything but Rgba16F, so every audit stopped here.
            if (format != TextureFormat.Rg16F && format != TextureFormat.Rgba16F) return null;
            var stride = format == TextureFormat.Rg16F ? 4 : 8;
            var f = new float[w * h];
            for (var i = 0; i < w * h; i++) f[i] = (float)BitConverter.ToHalf(pixels, i * stride + 2);
            far[l] = f;
            size[l] = (w, h);
        }
        return new AuditPyramid(far, size);
    }

    // scene_cull.glsl's planes, from the row-vector view-projection (its columns are the GLSL rows).
    private static Vector4[] FrustumPlanes(Matrix4x4 m)
    {
        Vector4 Col(int c) => c switch
        {
            0 => new Vector4(m.M11, m.M21, m.M31, m.M41),
            1 => new Vector4(m.M12, m.M22, m.M32, m.M42),
            2 => new Vector4(m.M13, m.M23, m.M33, m.M43),
            _ => new Vector4(m.M14, m.M24, m.M34, m.M44),
        };
        Vector4 Norm(Vector4 p) => p / new Vector3(p.X, p.Y, p.Z).Length();
        var r0 = Col(0); var r1 = Col(1); var r2 = Col(2); var r3 = Col(3);
        return new[] { Norm(r3 + r0), Norm(r3 - r0), Norm(r3 + r1), Norm(r3 - r1), Norm(r2), Norm(r3 - r2) };
    }

    private static bool Intersects(Vector4[] planes, Vector3 bmin, Vector3 bmax, float margin)
    {
        foreach (var p in planes)
        {
            var v = new Vector3(p.X >= 0 ? bmax.X : bmin.X, p.Y >= 0 ? bmax.Y : bmin.Y, p.Z >= 0 ? bmax.Z : bmin.Z);
            if (Vector3.Dot(new Vector3(p.X, p.Y, p.Z), v) + p.W < -margin) return false;
        }
        return true;
    }

    // scene_occlusion.comp's hidden(), line for line.
    private bool Hidden(AuditPyramid pyr, Matrix4x4 cull, Matrix4x4 view, Vector3 bmin, Vector3 bmax)
    {
        var lo = new Vector2(1e30f);
        var hi = new Vector2(-1e30f);
        var nearest = 1e30f;
        for (var c = 0; c < 8; c++)
        {
            var corner = new Vector3((c & 1) == 0 ? bmin.X : bmax.X, (c & 2) == 0 ? bmin.Y : bmax.Y, (c & 4) == 0 ? bmin.Z : bmax.Z);
            var depth = -Vector4.Transform(new Vector4(corner, 1f), view).Z;
            if (depth <= CameraNearPlane) return false;
            nearest = MathF.Min(nearest, depth);
            var clip = Vector4.Transform(new Vector4(corner, 1f), cull);
            var ndc = new Vector2(clip.X, clip.Y) / clip.W;
            lo = Vector2.Min(lo, ndc);
            hi = Vector2.Max(hi, ndc);
        }
        lo = Vector2.Clamp(lo * 0.5f + new Vector2(0.5f), Vector2.Zero, Vector2.One);
        hi = Vector2.Clamp(hi * 0.5f + new Vector2(0.5f), Vector2.Zero, Vector2.One);
        var size0 = new Vector2(pyr.Size[0].W, pyr.Size[0].H);
        var extent = (hi - lo) * size0;
        var level = Math.Clamp((int)MathF.Ceiling(MathF.Log2(MathF.Max(MathF.Max(extent.X, extent.Y), 1f) / 4f)), 0, HiZLevels - 1);
        var (w, h) = pyr.Size[level];
        var t0x = Math.Clamp((int)MathF.Floor(lo.X * w) - 1, 0, w - 1);
        var t0y = Math.Clamp((int)MathF.Floor(lo.Y * h) - 1, 0, h - 1);
        var t1x = Math.Clamp((int)MathF.Floor(hi.X * w) + 1, 0, w - 1);
        var t1y = Math.Clamp((int)MathF.Floor(hi.Y * h) + 1, 0, h - 1);
        if (t1x - t0x >= 16 || t1y - t0y >= 16) return false;
        var deepest = 0f;
        var f = pyr.Farthest[level];
        for (var y = t0y; y <= t1y; y++)
        for (var x = t0x; x <= t1x; x++)
            deepest = MathF.Max(deepest, f[y * w + x]);
        return nearest > deepest * (1f + 1f / 1024f);
    }
}
