using System.Numerics;
using Blix.Core;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace Blix.Tools.Testbeds;

/// <summary>
/// Correctness scenes: small, plain glTF sources whose lighting is simple enough to reason about and to hold
/// against a path trace -- the baselines a GI change answers to before Sponza and Bistro.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why scenes of our own.</b> Sponza and Bistro can only say "closer to the path trace". A scene built to
/// isolate one thing -- colour bleeding in an enclosure, light that must not cross a wall, a hand-off
/// between probe levels -- can say what is wrong and where, in a run of a minute.
/// </para>
/// <para>
/// <b>Authored, not cooked.</b> Like <c>blix city</c>, each writes glTF the cook reads like any other source
/// (tools/testbeds/setup.sh), so everything downstream is exercised as an artist's file would be.
/// </para>
/// </remarks>
public static class Program
{
    public static int Main(string[] args) => BlixApps.Main(args);

    /// <summary>
    /// The Cornell box at room scale, lit by the sun through a skylight: one sunlit patch whose bounce is all the
    /// light the room gets, coloured by a red and a green wall.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The classic box is 0.55 m and lit by a ceiling lamp. Neither suits this renderer: its probes are 0.5 m apart
    /// at the finest (a 0.55 m box sits inside one probe cell), and its GI takes light from the sun and the sky only
    /// (a probe ray's hit is albedo x (sun + indirect); nothing emits). So the box is 6 m inside and the lamp is a
    /// 1.5 m skylight the sun shines through -- the same experiment: one bright patch, everything else bounce.
    /// </para>
    /// <para>
    /// Walls, floor and ceiling are 0.2 m solid slabs, so light that reaches the inside other than through the
    /// skylight and the open front is a leak. The reflectances are the box's published ones (white 0.73,
    /// red 0.63/0.065/0.05, green 0.14/0.45/0.091), linear, matte. The front (+Z) is open; the camera looks in.
    /// </para>
    /// </remarks>
    [BlixApp("cornell", Summary = "write the Cornell box at room scale, sunlit through a skylight, as glTF: a GI correctness scene", Default = true)]
    public static int Cornell(AppArgs args)
    {
        var output = args.String("out") ?? "cornell.gltf";
        var size = args.Float("size") ?? 6f;          // the inside, cubic
        var skylight = args.Float("skylight") ?? 1.5f; // the square opening in the ceiling
        if (size < 2f || skylight <= 0f || skylight >= size - 0.5f)
            throw new AppArgsException("--size must be at least 2 m and --skylight between 0 and the size less 0.5 m.");

        var white = Matte("white", new Vector3(0.73f, 0.73f, 0.73f));
        var red = Matte("red", new Vector3(0.63f, 0.065f, 0.05f));
        var green = Matte("green", new Vector3(0.14f, 0.45f, 0.091f));

        var h = size / 2f;
        const float t = 0.2f;                          // slab thickness
        var mesh = new Mesh("cornell");
        var w = mesh.UsePrimitive(white);
        // Floor and back wall, white; overlapping at the corners so no seam lets light through.
        Box(w, new Vector3(-h - t, -t, -h - t), new Vector3(h + t, 0f, h));
        Box(w, new Vector3(-h - t, -t, -h - t), new Vector3(h + t, size + t, -h));
        // The ceiling: four slabs around the skylight.
        var s = skylight / 2f;
        Box(w, new Vector3(-h - t, size, -h - t), new Vector3(-s, size + t, h));
        Box(w, new Vector3(s, size, -h - t), new Vector3(h + t, size + t, h));
        Box(w, new Vector3(-s, size, -h - t), new Vector3(s, size + t, -s));
        Box(w, new Vector3(-s, size, s), new Vector3(s, size + t, h));
        // Left red, right green.
        Box(mesh.UsePrimitive(red), new Vector3(-h - t, -t, -h - t), new Vector3(-h, size + t, h));
        Box(mesh.UsePrimitive(green), new Vector3(h, -t, -h - t), new Vector3(h + t, size + t, h));

        // The two blocks, white: a tall one back left, a short one front right, each turned as in the original.
        var tall = new Mesh("tall_block");
        Box(tall.UsePrimitive(white), new Vector3(-0.3f * h, 0f, -0.3f * h), new Vector3(0.3f * h, 1.2f * h, 0.3f * h));
        var shortBlock = new Mesh("short_block");
        Box(shortBlock.UsePrimitive(white), new Vector3(-0.3f * h, 0f, -0.3f * h), new Vector3(0.3f * h, 0.6f * h, 0.3f * h));

        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        scene.AddRigidMesh(tall, Matrix4x4.CreateRotationY(17f * MathF.PI / 180f) * Matrix4x4.CreateTranslation(-0.35f * h, 0f, -0.35f * h));
        scene.AddRigidMesh(shortBlock, Matrix4x4.CreateRotationY(-17f * MathF.PI / 180f) * Matrix4x4.CreateTranslation(0.35f * h, 0f, 0.3f * h));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        scene.ToGltf2().SaveGLTF(output);
        Console.WriteLine($"cornell: {size:0.##} m inside, {skylight:0.##} m skylight, 0.2 m slabs -> {output}");
        return 0;
    }

    // The glTF vertex the cook reads in full: position, normal, one UV set (the cook generates the tangent).
    private sealed class Mesh : MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>
    {
        public Mesh(string name) : base(name) { }
    }

    private static MaterialBuilder Matte(string name, Vector3 albedo) =>
        new MaterialBuilder(name).WithMetallicRoughnessShader().WithBaseColor(new Vector4(albedo, 1f)).WithMetallicRoughness(0f, 1f);

    // An axis-aligned box from its two corners, all six faces outward.
    private static void Box(PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture1, VertexEmpty> p, Vector3 lo, Vector3 hi)
    {
        var d = hi - lo;
        Quad(p, new Vector3(lo.X, lo.Y, hi.Z), new(d.X, 0, 0), new(0, d.Y, 0), Vector3.UnitZ);
        Quad(p, new Vector3(hi.X, lo.Y, lo.Z), new(-d.X, 0, 0), new(0, d.Y, 0), -Vector3.UnitZ);
        Quad(p, new Vector3(hi.X, lo.Y, hi.Z), new(0, 0, -d.Z), new(0, d.Y, 0), Vector3.UnitX);
        Quad(p, new Vector3(lo.X, lo.Y, lo.Z), new(0, 0, d.Z), new(0, d.Y, 0), -Vector3.UnitX);
        Quad(p, new Vector3(lo.X, hi.Y, hi.Z), new(d.X, 0, 0), new(0, 0, -d.Z), Vector3.UnitY);
        Quad(p, new Vector3(lo.X, lo.Y, lo.Z), new(d.X, 0, 0), new(0, 0, d.Z), -Vector3.UnitY);
    }

    private readonly record struct Vertex(Vector3 P, Vector3 N, Vector2 Uv)
    {
        public static implicit operator VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(Vertex v) =>
            new(new VertexPositionNormal(v.P, v.N), new VertexTexture1(v.Uv));
    }

    // A quad from a corner and two edges, wound so the given normal faces out; UVs in metres.
    private static void Quad(PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture1, VertexEmpty> p,
        Vector3 o, Vector3 u, Vector3 v, Vector3 n)
    {
        var lu = u.Length();
        var lv = v.Length();
        if (lu < 1e-4f || lv < 1e-4f) return;
        var a = new Vertex(o, n, Vector2.Zero);
        var b = new Vertex(o + u, n, new Vector2(lu, 0));
        var c = new Vertex(o + u + v, n, new Vector2(lu, lv));
        var d = new Vertex(o + v, n, new Vector2(0, lv));
        if (Vector3.Dot(Vector3.Cross(u, v), n) >= 0f)
        {
            p.AddTriangle(a, b, c);
            p.AddTriangle(a, c, d);
        }
        else
        {
            p.AddTriangle(a, c, b);
            p.AddTriangle(a, d, c);
        }
    }
}
