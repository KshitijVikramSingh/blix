using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using Blix.Geometry;

namespace Blix.Demos.VulkanSponza;

internal sealed partial class SponzaLoop
{
    // --texel-centroid-why: where the hashed texels' gathers start. A texel sits at the centroid of its cell's samples in
    // one normal bin, so a cell holding a face and a recess (or a moulding) in the same bin puts it off both, possibly
    // inside the solid; its rays then meet back faces first and read the patch on the far side (texel_nodes_shade.glsl,
    // the hit cells' side). Per level: the centroid's depth below its surface, and the share of 32 cosine rays from the
    // gather's origin whose first hit is a back face; the worst texels with their positions.
    private bool texelCentroidWhy;

    // --texel-lift: a centroid below its own surface moves up onto it. From 10 cm up its normal, a probe back down; the
    // first surface it meets, if a front face turned the texel's way, is the one the centroid sank under, and the texel
    // sits on it (its gather's origin 1 cm above, as every texel's). Per texel at load, from the cooked texels.
    private bool texelLift;

    private void LiftTexels(RayQueryScene scene, TexelBake bake)
    {
        var lifted = new long[texelLevels];
        Parallel.For(0, bake.Count, i =>
        {
            var pw = bake.Texels[2 * i];
            var nw = bake.Texels[2 * i + 1];
            var p = new Vector3(pw.X, pw.Y, pw.Z);
            var n = Vector3.Normalize(new Vector3(nw.X, nw.Y, nw.Z));
            if (scene.Closest(new Ray(p + n * 0.1f, -n), 0f, 0.1f, 1u) is not { FrontFace: true } h) return;
            var depth = 0.1f - h.T;
            if (depth <= 1e-3f) return;
            var q = p + n * depth;
            bake.Texels[2 * i] = new Vector4(q, pw.W);
            Interlocked.Increment(ref lifted[Math.Clamp((int)nw.W, 0, texelLevels - 1)]);
        });
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] texels lifted onto their surface, by level: {string.Join(" / ", lifted.Select(c => c.ToString("N0", CultureInfo.InvariantCulture)))}."));
    }

    private void TexelCentroidWhy(RayQueryScene scene, TexelBake bake)
    {
        var levels = texelLevels;
        var depthBins = new long[levels, 4];   // at or above (1 mm), below by 1 mm-1 cm, 1-3 cm, more
        var backBins = new long[levels, 4];    // back-face share: 0, < 10%, < 30%, more
        var worst = new ConcurrentBag<(float Back, int Level, Vector3 P, Vector3 N, float Depth)>();
        const int Rays = 32;
        Parallel.For(0, bake.Count, i =>
        {
            var p = new Vector3(bake.Texels[2 * i].X, bake.Texels[2 * i].Y, bake.Texels[2 * i].Z);
            var nw = bake.Texels[2 * i + 1];
            var n = Vector3.Normalize(new Vector3(nw.X, nw.Y, nw.Z));
            var level = Math.Clamp((int)nw.W, 0, levels - 1);
            var probe = scene.Closest(new Ray(p + n * 0.05f, -n), 0f, 0.1f, 1u);
            var depth = probe is { } h ? 0.05f - h.T : -1f;
            Interlocked.Increment(ref depthBins[level, depth <= 1e-3f ? 0 : depth <= 0.01f ? 1 : depth <= 0.03f ? 2 : 3]);
            var origin = p + n * 0.01f;
            var t = MathF.Abs(n.X) > 0.9f ? Vector3.UnitY : Vector3.UnitX;
            var a = Vector3.Normalize(Vector3.Cross(n, t));
            var b = Vector3.Cross(n, a);
            var back = 0;
            for (var k = 0; k < Rays; k++)
            {
                var u = (k + 0.5f) / Rays;
                var phi = k * 2.399963f;
                var r = MathF.Sqrt(u);
                var d = a * (r * MathF.Cos(phi)) + b * (r * MathF.Sin(phi)) + n * MathF.Sqrt(1f - u);
                if (scene.Closest(new Ray(origin, d), 0f, float.PositiveInfinity, (uint)(i * Rays + k)) is { FrontFace: false }) back++;
            }
            var share = back / (float)Rays;
            Interlocked.Increment(ref backBins[level, share <= 0f ? 0 : share < 0.1f ? 1 : share < 0.3f ? 2 : 3]);
            if (share >= 0.3f && (i & 7) == 0) worst.Add((share, level, p, n, depth));
        });
        for (var level = 0; level < levels; level++)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] centroid why, level {level}: depth at or above {depthBins[level, 0]:N0}, below 1 mm-1 cm {depthBins[level, 1]:N0}, 1-3 cm {depthBins[level, 2]:N0}, more {depthBins[level, 3]:N0}; back-face share of the gather's first hits 0 {backBins[level, 0]:N0}, < 10% {backBins[level, 1]:N0}, < 30% {backBins[level, 2]:N0}, more {backBins[level, 3]:N0}."));
        foreach (var w in worst.OrderByDescending(w => w.Back).Take(40))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] centroid why: level {w.Level} at {w.P.X:0.00},{w.P.Y:0.00},{w.P.Z:0.00} normal {w.N.X:0.00},{w.N.Y:0.00},{w.N.Z:0.00}: back {w.Back:P0}, depth {w.Depth * 100:0.0} cm."));
    }
}
