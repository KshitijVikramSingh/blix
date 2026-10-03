using System.Diagnostics;
using System.Numerics;
using Blix.Geometry;

namespace Blix.Demos.VulkanSponza;

// --ray-scene: the scene under CPU ray-query hierarchies (Blix.Geometry), built at load. One TriangleBvh per
// unique primitive over its full-detail triangles, one RayQueryScene over every placement. Stage 3's first
// measure: whether a load-time build fits these scenes, and what a CPU ray costs in them.
internal sealed partial class SponzaLoop
{
    private bool rayScene;
    private RayQueryScene? rayQueries;

    private TriangleBvh[] BuildRayMeshes(List<DrawableStaging> ordered)
    {
        var clock = Stopwatch.StartNew();
        var stride = sharedLayout.Stride;
        var position = Blix.Graphics.VertexSemantics.Of(sharedLayout)?.Position ?? 0;
        var meshes = new TriangleBvh[ordered.Count];
        Parallel.For(0, ordered.Count, i =>
        {
            var s = ordered[i];
            var positions = new Vector3[s.VertexCount];
            for (var v = 0; v < s.VertexCount; v++)
            {
                var at = v * stride + position;
                positions[v] = new Vector3(
                    BitConverter.ToSingle(s.VertexBytes, at), BitConverter.ToSingle(s.VertexBytes, at + 4), BitConverter.ToSingle(s.VertexBytes, at + 8));
            }
            var lod0 = s.Lods[0];
            var indices = lod0.Indices32 ?? Array.ConvertAll(lod0.Indices16!, x => (uint)x);
            meshes[i] = TriangleBvh.Build(positions, indices);
        });
        var triangles = meshes.Sum(m => (long)m.TriangleCount);
        var nodes = meshes.Sum(m => (long)m.Nodes.Length);
        var sah = meshes.Where(m => m.TriangleCount > 0).Select(m => BvhBuilder.SahCost(m.Nodes) / m.TriangleCount).DefaultIfEmpty(0f).Average();
        Console.WriteLine(string.Create(Inv, 
            $"[VulkanSponza] ray scene: {meshes.Length} mesh hierarchies over {triangles:N0} triangles in {clock.Elapsed.TotalMilliseconds:0} ms ({Environment.ProcessorCount} threads); {nodes:N0} nodes ({nodes * BvhNode.SizeInBytes / 1048576.0:0.0} MB) + order {triangles * 4 / 1048576.0:0.0} MB; mean SAH cost {sah:0.000} of testing every triangle."));
        return meshes;
    }

    private void BuildRayScene(TriangleBvh[] meshes, List<RayQueryScene.Instance> instances)
    {
        var clock = Stopwatch.StartNew();
        rayQueries = RayQueryScene.Build(instances);
        Console.WriteLine(string.Create(Inv, 
            $"[VulkanSponza] ray scene: top level over {instances.Count:N0} placements in {clock.Elapsed.TotalMilliseconds:0} ms, {rayQueries.Nodes.Length:N0} nodes."));
    }

    // Camera rays through a grid over the view, traced on every core, at the end of a shot: what a CPU ray costs here.
    private void WriteRayBenchmark()
    {
        if (rayQueries is null) return;
        const int W = 320, H = 180;
        Matrix4x4.Invert(viewProj, out var invViewProj);
        var hits = 0;
        long hitCount = 0;
        var rays = new Ray[W * H];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var ndc = new Vector2((x + 0.5f) / W * 2f - 1f, (y + 0.5f) / H * 2f - 1f);
            var far = Vector4.Transform(new Vector4(ndc, 1f, 1f), invViewProj);
            var dir = Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - cameraPosition);
            rays[y * W + x] = new Ray(cameraPosition, dir);
        }
        var single = Stopwatch.StartNew();
        for (var i = 0; i < 4096; i++) if (rayQueries.Closest(rays[i * 13 % rays.Length]) is not null) hits++;
        var singleRate = 4096 / single.Elapsed.TotalSeconds;
        var all = Stopwatch.StartNew();
        Parallel.For(0, rays.Length, () => 0L, (i, _, n) => rayQueries.Closest(rays[i]) is not null ? n + 1 : n,
            n => Interlocked.Add(ref hitCount, n));
        var allRate = rays.Length / all.Elapsed.TotalSeconds;
        Console.WriteLine(string.Create(Inv, 
            $"[VulkanSponza] ray scene: {W}x{H} camera rays, closest hit: {singleRate / 1e6:0.00} M rays/s on one core, {allRate / 1e6:0.00} M rays/s on {Environment.ProcessorCount}; {100.0 * hitCount / rays.Length:0.0}% hit."));
    }
}
