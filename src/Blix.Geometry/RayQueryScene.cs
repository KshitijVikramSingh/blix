using System.Numerics;

namespace Blix.Geometry;

/// <summary>Meshes placed in a world, under one hierarchy over the placements: the top level of a ray query.</summary>
/// <remarks>
/// <para>
/// Each placement is a <see cref="TriangleBvh"/> (shared by every placement of the same mesh) and a world matrix
/// in the System.Numerics row-vector form (world = Vector3.Transform(local, matrix)). A ray reaching a placement is
/// carried into that mesh's space by the inverse, direction unnormalised, so a distance found there is the same
/// distance in the world.
/// </para>
/// <para>
/// This is the CPU reference: the oracle the GPU's traversal is tested against, and the answer wherever a CPU
/// needs one. A placement that moves rebuilds the scene for now; refitting the top level is for when something
/// moves every frame.
/// </para>
/// </remarks>
public sealed class RayQueryScene
{
    public readonly record struct Instance(TriangleBvh Mesh, Matrix4x4 World);

    private readonly Instance[] instances;
    private readonly Matrix4x4[] worldToLocal;

    private RayQueryScene(Instance[] instances, Matrix4x4[] worldToLocal, BvhNode[] nodes, int[] order)
    {
        this.instances = instances;
        this.worldToLocal = worldToLocal;
        Nodes = nodes;
        Order = order;
    }

    public IReadOnlyList<Instance> Instances => instances;
    public BvhNode[] Nodes { get; }
    public int[] Order { get; }

    /// <exception cref="ArgumentException">A placement's matrix has no inverse, so a ray cannot be carried into it.</exception>
    public static RayQueryScene Build(IReadOnlyList<Instance> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        var instances = placements.ToArray();
        var inverses = new Matrix4x4[instances.Length];
        var boxes = new Bounds3[instances.Length];
        for (var i = 0; i < instances.Length; i++)
        {
            if (!Matrix4x4.Invert(instances[i].World, out inverses[i]))
            {
                throw new ArgumentException($"placement {i}'s world matrix has no inverse.", nameof(placements));
            }
            boxes[i] = WorldBounds(instances[i].Mesh.Bounds, instances[i].World);
        }
        var (nodes, order) = BvhBuilder.Build(boxes, maxLeafSize: 1);
        return new RayQueryScene(instances, inverses, nodes, order);
    }

    /// <summary>The nearest hit along the ray in (tMin, tMax), if any.</summary>
    public RayHit? Closest(in Ray ray, float tMin = 0f, float tMax = float.PositiveInfinity)
    {
        if (instances.Length == 0) return null;
        var world = new ShearedRay(ray.Origin, ray.Direction);
        RayHit? best = null;
        var t = tMax;
        Span<uint> stack = stackalloc uint[64];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            var node = Nodes[stack[--top]];
            // Re-tested on the way out: a hit found since it was pushed may have moved t in front of it.
            if (!RayTests.Box(world, node, tMin, t, out _)) continue;
            if (!node.IsLeaf)
            {
                // Nearer child popped first, so the nearest hit is found early and prunes the rest.
                var hitL = RayTests.Box(world, Nodes[node.Index], tMin, t, out var entryL);
                var hitR = RayTests.Box(world, Nodes[node.Index + 1], tMin, t, out var entryR);
                if (hitL && hitR)
                {
                    var nearIsLeft = entryL <= entryR;
                    stack[top++] = nearIsLeft ? node.Index + 1 : node.Index;
                    stack[top++] = nearIsLeft ? node.Index : node.Index + 1;
                }
                else if (hitL) stack[top++] = node.Index;
                else if (hitR) stack[top++] = node.Index + 1;
                continue;
            }
            for (var k = 0; k < node.Count; k++)
            {
                var i = Order[node.Index + k];
                var local = Local(ray, i);
                if (!instances[i].Mesh.Closest(local, tMin, t, out var th, out var tri, out var bc, out var front)) continue;
                t = th;
                // Facing is the mesh's own, as hardware ray queries define it: a mirroring placement mirrors the
                // normals with the triangles, so the authored outside stays the outside.
                best = new RayHit(th, i, tri, bc, front);
            }
        }
        return best;
    }

    /// <summary>Whether anything is hit along the ray in (tMin, tMax).</summary>
    public bool Any(in Ray ray, float tMin = 0f, float tMax = float.PositiveInfinity)
    {
        if (instances.Length == 0) return false;
        var world = new ShearedRay(ray.Origin, ray.Direction);
        Span<uint> stack = stackalloc uint[64];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            var node = Nodes[stack[--top]];
            if (!RayTests.Box(world, node, tMin, tMax, out _)) continue;
            if (!node.IsLeaf)
            {
                stack[top++] = node.Index + 1;
                stack[top++] = node.Index;
                continue;
            }
            for (var k = 0; k < node.Count; k++)
            {
                if (instances[Order[node.Index + k]].Mesh.Any(Local(ray, Order[node.Index + k]), tMin, tMax)) return true;
            }
        }
        return false;
    }

    private ShearedRay Local(in Ray ray, int instance) => new(
        Vector3.Transform(ray.Origin, worldToLocal[instance]),
        Vector3.TransformNormal(ray.Direction, worldToLocal[instance]));

    /// <summary>The world box around a mesh-space box under a matrix: its eight corners, carried and enclosed.</summary>
    public static Bounds3 WorldBounds(in Bounds3 local, in Matrix4x4 world)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var c = 0; c < 8; c++)
        {
            var p = Vector3.Transform(new Vector3(
                (c & 1) == 0 ? local.Min.X : local.Max.X,
                (c & 2) == 0 ? local.Min.Y : local.Max.Y,
                (c & 4) == 0 ? local.Min.Z : local.Max.Z), world);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return new Bounds3(min, max);
    }
}
