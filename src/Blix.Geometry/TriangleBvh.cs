using System.Numerics;

namespace Blix.Geometry;

/// <summary>Where a ray first meets a triangle: what a ray query reports, on the CPU or the GPU.</summary>
/// <remarks>
/// The fields are the ones <c>VK_KHR_ray_query</c> commits, so a hardware backend and the software one answer in
/// the same terms: the distance along the ray (in the units of its direction), the placement and the triangle
/// (its index in the mesh's index list, divided by three), the barycentrics of the second and third vertex
/// (the first's is 1 - x - y), and whether the ray met the counter-clockwise side.
/// </remarks>
public readonly record struct RayHit(float T, int Instance, int Triangle, Vector2 Barycentrics, bool FrontFace);

/// <summary>A ray prepared for the watertight triangle test: its dominant axis and the shear that aligns it with it.</summary>
public readonly struct ShearedRay
{
    public readonly Vector3 Origin;
    public readonly Vector3 Direction;
    public readonly Vector3 InverseDirection;
    public readonly int Kx, Ky, Kz;
    public readonly float Sx, Sy, Sz;

    public ShearedRay(Vector3 origin, Vector3 direction)
    {
        Origin = origin;
        Direction = direction;
        InverseDirection = new Vector3(1f / direction.X, 1f / direction.Y, 1f / direction.Z);
        var a = Vector3.Abs(direction);
        Kz = a.X >= a.Y ? (a.X >= a.Z ? 0 : 2) : (a.Y >= a.Z ? 1 : 2);
        Kx = (Kz + 1) % 3;
        Ky = (Kx + 1) % 3;
        // Keep the winding of the projected triangle when the dominant axis points backwards.
        if (BvhBuilder.Component(direction, Kz) < 0f) (Kx, Ky) = (Ky, Kx);
        var dz = BvhBuilder.Component(direction, Kz);
        Sx = BvhBuilder.Component(direction, Kx) / dz;
        Sy = BvhBuilder.Component(direction, Ky) / dz;
        Sz = 1f / dz;
    }
}

/// <summary>
/// What makes a ray's chance of meeting a partly covered triangle its own: a seed the caller gives the ray, and the
/// triangle's identity in the scene -- the placement it belongs to and its index in that placement's mesh. For an
/// instanced hierarchy that is <paramref name="Placement"/> and the triangle's own index; a region's triangles carry
/// theirs (<paramref name="OwnerPlacement"/>, <paramref name="OwnerTriangle"/>, mirror bit masked). Keyed on the
/// triangle, not on where a hierarchy happens to list it, so a triangle referenced twice (split across cells or
/// nodes) flips one coin, not two.
/// </summary>
public readonly record struct RayCoverageKey(uint Seed, uint Placement, int[]? OwnerPlacement = null, int[]? OwnerTriangle = null);

/// <summary>The ray tests a traversal is built from, written once for the CPU and mirrored by the GPU's.</summary>
public static class RayTests
{
    /// <summary>Whether a ray meets a triangle with this much coverage: always at 255, never at 0, else by an integer hash.</summary>
    /// <remarks>
    /// The hash (PCG's output function, twice) of the seed and the triangle's identity (its placement and its index in
    /// that placement's mesh), its top byte against the coverage byte: integer arithmetic only, so ray_query.glsl gets
    /// the same answer bit for bit. Over many rays a triangle is met in proportion to how much of it the material's
    /// alpha keeps.
    /// </remarks>
    public static bool Covered(byte coverage, uint seed, uint placement, uint triangle)
    {
        if (coverage == 255) return true;
        if (coverage == 0) return false;
        return (Pcg(seed + Pcg(placement * 0x9E3779B9u + triangle)) >> 24) < coverage;
    }

    /// <summary>PCG's output permutation as a hash (Jarzynski and Olano, "Hash Functions for GPU Rendering", 2020).</summary>
    public static uint Pcg(uint v)
    {
        unchecked
        {
            var state = v * 747796405u + 2891336453u;
            var word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
            return (word >> 22) ^ word;
        }
    }

    /// <summary>The watertight ray-triangle test (Woop, Benthin and Wald, 2013).</summary>
    /// <remarks>
    /// The triangle is sheared into the ray's frame and tested by three edge functions, each computed from the
    /// edge's own two vertices, so a ray through an edge two triangles share lands in exactly one of them and none
    /// passes between. No double-precision fallback for edge functions that round to zero: Metal has no doubles,
    /// and the GPU must give the same answer. Both sides count. Returns the hit's distance in (tMin, tMax) and the
    /// barycentrics of v1 and v2.
    /// </remarks>
    public static bool Triangle(in ShearedRay r, Vector3 v0, Vector3 v1, Vector3 v2, float tMin, float tMax,
        out float t, out Vector2 barycentrics, out bool frontFace)
    {
        t = 0f;
        barycentrics = default;
        frontFace = false;
        var a = v0 - r.Origin;
        var b = v1 - r.Origin;
        var c = v2 - r.Origin;
        var az = BvhBuilder.Component(a, r.Kz);
        var bz = BvhBuilder.Component(b, r.Kz);
        var cz = BvhBuilder.Component(c, r.Kz);
        var ax = BvhBuilder.Component(a, r.Kx) - r.Sx * az;
        var ay = BvhBuilder.Component(a, r.Ky) - r.Sy * az;
        var bx = BvhBuilder.Component(b, r.Kx) - r.Sx * bz;
        var by = BvhBuilder.Component(b, r.Ky) - r.Sy * bz;
        var cx = BvhBuilder.Component(c, r.Kx) - r.Sx * cz;
        var cy = BvhBuilder.Component(c, r.Ky) - r.Sy * cz;

        var u = cx * by - cy * bx;
        var v = ax * cy - ay * cx;
        var w = bx * ay - by * ax;
        if ((u < 0f || v < 0f || w < 0f) && (u > 0f || v > 0f || w > 0f)) return false;
        var det = u + v + w;
        if (det == 0f) return false;

        var tScaled = u * r.Sz * az + v * r.Sz * bz + w * r.Sz * cz;
        var inv = 1f / det;
        t = tScaled * inv;
        if (!(t > tMin && t < tMax)) return false;
        barycentrics = new Vector2(v * inv, w * inv);
        frontFace = Vector3.Dot(Vector3.Cross(v1 - v0, v2 - v0), r.Direction) < 0f;
        return true;
    }

    /// <summary>How far a box's exit distance is widened so rounding never drops a ray the box truly holds.</summary>
    /// <remarks>
    /// 1 + 2γ₃ (Ize, "Robust BVH Ray Traversal", 2013), with γₙ = nε / (1 - nε) and ε = 2⁻²⁴: the most the three
    /// roundings in a slab distance can move it. Without it a ray through a vertex lying on a node's face can
    /// round out of the node, and the watertight triangle test never sees it (Test.Graphics BV.3).
    /// </remarks>
    public const float BoxExitWidening = 1f + 2f * (3f * 5.9604645e-8f / (1f - 3f * 5.9604645e-8f));

    /// <summary>The slab test: where the ray enters a box, if it does before <paramref name="tMax"/>.</summary>
    public static bool Box(in ShearedRay r, in BvhNode node, float tMin, float tMax, out float entry)
    {
        var t0 = (node.Min - r.Origin) * r.InverseDirection;
        var t1 = (node.Max - r.Origin) * r.InverseDirection;
        var near = Vector3.Min(t0, t1);
        var far = Vector3.Max(t0, t1);
        entry = MathF.Max(MathF.Max(near.X, near.Y), MathF.Max(near.Z, tMin));
        var exit = MathF.Min(MathF.Min(far.X, far.Y), MathF.Min(far.Z, tMax)) * BoxExitWidening;
        return entry <= exit;
    }
}

/// <summary>A mesh's triangles under a bounding volume hierarchy: the bottom level of a ray query.</summary>
/// <remarks>
/// Built over one primitive's positions and index list, in its own space; a placement moves the ray, not the
/// mesh (<see cref="RayQueryScene"/>). Leaves hold up to <see cref="MaxLeafSize"/> triangles, named in
/// <see cref="Order"/> by triangle index (index-list position / 3).
/// </remarks>
public sealed class TriangleBvh
{
    public const int MaxLeafSize = 4;

    private TriangleBvh(Vector3[] positions, uint[] indices, BvhNode[] nodes, int[] order)
    {
        Positions = positions;
        Indices = indices;
        Nodes = nodes;
        Order = order;
    }

    public Vector3[] Positions { get; }
    public uint[] Indices { get; }
    public BvhNode[] Nodes { get; }
    public int[] Order { get; }

    public int TriangleCount => Indices.Length / 3;

    /// <summary>Per triangle in leaf order (<see cref="Order"/>'s positions), how much of it is there: 255 solid, 0 none.</summary>
    /// <remarks>
    /// Null is solid throughout. Set from a bake of the material's alpha (ray_query.glsl's surfaces block holds the
    /// same bytes): a ray meets a partly covered triangle with that probability, by <see cref="RayTests.Covered"/>.
    /// </remarks>
    public byte[]? Coverage { get; set; }

    public Bounds3 Bounds => Nodes[0].Bounds;

    public static TriangleBvh Build(Vector3[] positions, uint[] indices)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Length % 3 != 0) throw new ArgumentException($"{indices.Length} indices are not whole triangles.", nameof(indices));

        var boxes = new Bounds3[indices.Length / 3];
        for (var i = 0; i < boxes.Length; i++)
        {
            var a = positions[indices[i * 3]];
            var b = positions[indices[i * 3 + 1]];
            var c = positions[indices[i * 3 + 2]];
            boxes[i] = new Bounds3(Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)));
        }
        var (nodes, order) = BvhBuilder.Build(boxes, MaxLeafSize);
        return new TriangleBvh(positions, indices, nodes, order);
    }

    /// <summary>As above, over the boxes the caller gives each triangle rather than its vertices' bounds.</summary>
    /// <remarks>
    /// A triangle clipped to a cell of space (<see cref="RayQueryScene"/>'s regions) is bounded by the part of it
    /// inside the cell, so the hierarchy's boxes, and its root's, do not reach into the next cell. A triangle is
    /// still tested whole: a hit outside its box is a real hit, and the cell beside holds the rest of it.
    /// </remarks>
    public static TriangleBvh Build(Vector3[] positions, uint[] indices, Bounds3[] boxes)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(boxes);
        if (boxes.Length * 3 != indices.Length) throw new ArgumentException($"{boxes.Length} boxes for {indices.Length / 3} triangles.", nameof(boxes));
        var (nodes, order) = BvhBuilder.Build(boxes, MaxLeafSize);
        return new TriangleBvh(positions, indices, nodes, order);
    }

    /// <summary>The nearest hit in (tMin, tMax), front to back: the nearer child first, and nothing past the best so far.</summary>
    public bool Closest(in ShearedRay ray, float tMin, float tMax, out float t, out int triangle, out Vector2 barycentrics, out bool frontFace) =>
        Closest(ray, tMin, tMax, default, out t, out triangle, out barycentrics, out frontFace);

    /// <summary>As above, with partly covered triangles met by chance: <paramref name="key"/> names the ray and the placement.</summary>
    public bool Closest(in ShearedRay ray, float tMin, float tMax, RayCoverageKey key, out float t, out int triangle, out Vector2 barycentrics, out bool frontFace)
    {
        t = tMax;
        triangle = -1;
        barycentrics = default;
        frontFace = false;
        if (TriangleCount == 0) return false;
        Span<uint> stack = stackalloc uint[64];
        var top = 0;
        if (!RayTests.Box(ray, Nodes[0], tMin, t, out _)) return false;
        stack[top++] = 0;
        while (top > 0)
        {
            var node = Nodes[stack[--top]];
            if (node.IsLeaf)
            {
                for (var k = 0; k < node.Count; k++)
                {
                    var tri = Order[node.Index + k];
                    if (Hit(ray, tri, tMin, t, out var th, out var bc, out var front) && Covered(key, node.Index + (uint)k))
                    {
                        t = th;
                        triangle = tri;
                        barycentrics = bc;
                        frontFace = front;
                    }
                }
                continue;
            }

            var hitL = RayTests.Box(ray, Nodes[node.Index], tMin, t, out var entryL);
            var hitR = RayTests.Box(ray, Nodes[node.Index + 1], tMin, t, out var entryR);
            if (hitL && hitR)
            {
                // Far child first onto the stack, so the near one is popped next.
                var nearIsLeft = entryL <= entryR;
                stack[top++] = nearIsLeft ? node.Index + 1 : node.Index;
                stack[top++] = nearIsLeft ? node.Index : node.Index + 1;
            }
            else if (hitL) stack[top++] = node.Index;
            else if (hitR) stack[top++] = node.Index + 1;
        }
        return triangle >= 0;
    }

    /// <summary>Whether anything is hit in (tMin, tMax): stops at the first triangle found.</summary>
    public bool Any(in ShearedRay ray, float tMin, float tMax) => Any(ray, tMin, tMax, default);

    public bool Any(in ShearedRay ray, float tMin, float tMax, RayCoverageKey key)
    {
        if (TriangleCount == 0) return false;
        Span<uint> stack = stackalloc uint[64];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            var node = Nodes[stack[--top]];
            if (!RayTests.Box(ray, node, tMin, tMax, out _)) continue;
            if (!node.IsLeaf)
            {
                stack[top++] = node.Index + 1;
                stack[top++] = node.Index;
                continue;
            }
            for (var k = 0; k < node.Count; k++)
            {
                if (Hit(ray, Order[node.Index + k], tMin, tMax, out _, out _, out _) && Covered(key, node.Index + (uint)k)) return true;
            }
        }
        return false;
    }

    private bool Covered(RayCoverageKey key, uint leafPosition)
    {
        if (Coverage is not { } coverage) return true;
        var triangle = Order[leafPosition];
        return key.OwnerPlacement is { } owners
            ? RayTests.Covered(coverage[leafPosition], key.Seed, (uint)owners[triangle], (uint)(key.OwnerTriangle![triangle] & 0x7FFFFFFF))
            : RayTests.Covered(coverage[leafPosition], key.Seed, key.Placement, (uint)triangle);
    }

    /// <summary>One triangle by index, through the same test the traversal uses.</summary>
    public bool Hit(in ShearedRay ray, int triangle, float tMin, float tMax, out float t, out Vector2 barycentrics, out bool frontFace) =>
        RayTests.Triangle(ray,
            Positions[Indices[triangle * 3]], Positions[Indices[triangle * 3 + 1]], Positions[Indices[triangle * 3 + 2]],
            tMin, tMax, out t, out barycentrics, out frontFace);
}
