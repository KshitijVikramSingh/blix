using System.Numerics;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --surface-check: are the pre-pass's SurfaceKey and velocity targets true? At the shot, for a grid of pixels, a CPU
// ray through the pixel finds the surface it really shows (RayQueryScene, whose instances are the placement rows),
// and the targets the last completed frame wrote are held against it: the key must be that row's SurfaceKey, and the
// velocity that point's motion under the same two cameras. With a still scene and a moving camera (--orbit) this is
// the control stage 4e-iv promised: velocity equals camera-only reprojection. Pixels whose neighbours show another
// surface are left out: there TAA's jitter may have rasterised either one.
internal sealed partial class SponzaLoop
{
    private bool surfaceCheck;
    // The last three frames' un-jittered view-projections, oldest first: the targets read back at the shot were
    // written by the frame before the one recording it, against the frame before that.
    private readonly Matrix4x4[] surfaceCheckViewProj = new Matrix4x4[3];

    private void RecordSurfaceCheckCamera()
    {
        surfaceCheckViewProj[0] = surfaceCheckViewProj[1];
        surfaceCheckViewProj[1] = surfaceCheckViewProj[2];
        surfaceCheckViewProj[2] = viewProj;
    }

    private void WriteSurfaceCheck()
    {
        if (!surfaceCheck) return;
        if (!SurfaceTargets || rayQueries is null)
        {
            Console.WriteLine("[VulkanSponza] surface check: needs single-sample targets and the ray scene.");
            return;
        }
        var keys = device.ReadTexture(graph.GetColorTexture(surfaceKeyHandle), out var w, out var h, out _);
        var velocity = device.ReadTexture(graph.GetColorTexture(velocityHandle), out _, out _, out _);
        var now = surfaceCheckViewProj[1];
        var then = surfaceCheckViewProj[0];
        Matrix4x4.Invert(now, out var invNow);
        uint KeyAt(int x, int y) => BitConverter.ToUInt32(keys, (Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)) * 4);
        Vector2 Uv(Matrix4x4 m, Vector3 p)
        {
            var c = Vector4.Transform(new Vector4(p, 1f), m);
            return new Vector2(c.X / c.W, c.Y / c.W) * 0.5f + new Vector2(0.5f);
        }

        // Each key's drawable name (the cooked "mesh.prim#chunk"), for reading what a mismatch is between.
        var nameOfKey = new Dictionary<uint, string>();
        foreach (var (list, drawables) in new[] { (opaquePlacements, opaqueDrawables), (blendPlacements, blendDrawables) })
            foreach (var pl in list) nameOfKey.TryAdd(sceneTransformSurfaceKeys[pl.Transform], drawables[pl.Drawable].Name);
        var mismatches = new Dictionary<string, int>();
        const int grid = 64;
        int sampled = 0, keyRight = 0, keyBehind = 0, edges = 0, background = 0;
        var errors = new List<double>();
        double cameraMotion = 0;
        for (var gy = 0; gy < grid * 9 / 16; gy++)
        for (var gx = 0; gx < grid; gx++)
        {
            var px = (int)((gx + 0.5f) / grid * w);
            var py = (int)((gy + 0.5f) / (grid * 9 / 16) * h);
            var key = KeyAt(px, py);
            if (key == 0) { background++; continue; }
            if (KeyAt(px - 2, py) != key || KeyAt(px + 2, py) != key || KeyAt(px, py - 2) != key || KeyAt(px, py + 2) != key) { edges++; continue; }
            var ndc = new Vector2((px + 0.5f) / w * 2f - 1f, (py + 0.5f) / h * 2f - 1f);
            var near = Vector4.Transform(new Vector4(ndc, 0f, 1f), invNow);
            var far = Vector4.Transform(new Vector4(ndc, 1f, 1f), invNow);
            var origin = new Vector3(near.X, near.Y, near.Z) / near.W;
            var dir = Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - origin);
            if (rayQueries.Closest(new Ray(origin, dir)) is not { } hit) continue;
            sampled++;
            // A decal or a cut-out leaf is a thin layer the raster's key target may not show (the pre-pass draws no
            // blended layer, and coverage is stochastic): continue the ray past differing layers, a few deep.
            var behind = false;
            if (sceneTransformSurfaceKeys[hit.Instance] != key)
            {
                var t = hit.T;
                for (var layer = 0; layer < 4 && !behind; layer++)
                {
                    if (rayQueries.Closest(new Ray(origin, dir), t + 1e-3f, float.PositiveInfinity) is not { } next) break;
                    t = next.T;
                    behind = sceneTransformSurfaceKeys[next.Instance] == key;
                }
            }
            if (sceneTransformSurfaceKeys[hit.Instance] == key) keyRight++;
            else if (behind) keyBehind++;
            else
            {
                var pair = $"target {nameOfKey.GetValueOrDefault(key, "?")} / ray {nameOfKey.GetValueOrDefault(sceneTransformSurfaceKeys[hit.Instance], "?")}";
                mismatches[pair] = mismatches.GetValueOrDefault(pair) + 1;
            }
            var p = origin + dir * hit.T;
            var expected = (Uv(now, p) - Uv(then, p)) * new Vector2(w, h);
            var o = (py * w + px) * 4;
            var got = new Vector2((float)BitConverter.ToHalf(velocity, o), (float)BitConverter.ToHalf(velocity, o + 2)) * new Vector2(w, h);
            errors.Add((got - expected).Length());
            cameraMotion += expected.Length();
        }
        errors.Sort();
        if (sampled == 0) { Console.WriteLine("[VulkanSponza] surface check: no pixel sampled."); return; }
        foreach (var (pair, n) in mismatches.OrderByDescending(m => m.Value).Take(8)) Console.WriteLine($"    key mismatch x{n}: {pair}");
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] surface check over {sampled} pixels ({edges} on surface edges and {background} background left out): key matches the hit row's {100.0 * keyRight / sampled:0.0}%, a row behind a thin layer (decal, leaf) {100.0 * keyBehind / sampled:0.0}%, none along the ray {100.0 * (sampled - keyRight - keyBehind) / sampled:0.0}%; velocity against camera reprojection, pixels: median error {errors[errors.Count / 2]:0.000}, p99 {errors[errors.Count * 99 / 100]:0.000}, max {errors[^1]:0.000} (mean motion {cameraMotion / sampled:0.00} px)"));
    }
}
