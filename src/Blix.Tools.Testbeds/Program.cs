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
    /// <para>
    /// <c>--panel</c> adds an occluder that moves across the light over surfaces that do not move (plan.md, stage 5:
    /// what a moved occluder does to cooked transfer): a white panel, 1.2 m by the skylight's width and 5 cm thick,
    /// hung from the skylight's -X edge. The renderer's <c>--mover panel</c> swings it about that edge, into the
    /// sun's shaft (a sun travelling +X, as the testbeds' are) and out again.
    /// </para>
    /// </remarks>
    [BlixApp("cornell", Summary = "write the Cornell box at room scale, sunlit through a skylight, as glTF: a GI correctness scene", Default = true)]
    public static int Cornell(AppArgs args)
    {
        var output = args.String("out") ?? "cornell.gltf";
        var size = args.Float("size") ?? 6f;          // the inside, cubic
        var skylight = args.Float("skylight") ?? 1.5f; // the square opening in the ceiling
        var panel = args.Flag("panel");
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
        if (panel)
        {
            var hanging = new Mesh("skylight_panel");
            Box(hanging.UsePrimitive(white), new Vector3(-s - 0.025f, size - 1.2f, -s), new Vector3(-s + 0.025f, size, s));
            scene.AddRigidMesh(hanging, Matrix4x4.Identity);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        scene.ToGltf2().SaveGLTF(output);
        Console.WriteLine($"cornell: {size:0.##} m inside, {skylight:0.##} m skylight, 0.2 m slabs{(panel ? ", a hanging panel" : "")} -> {output}");
        return 0;
    }

    /// <summary>
    /// Two rooms sharing one thin wall: one open to the sky, one sealed. Every bit of indirect light in the sealed
    /// room is light that crossed the wall -- a leak, with nothing true to confuse it with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Room A (x &lt; 0) has no roof: the sun and the sky flood it. Room B (x &gt; 0) is closed on every side by
    /// 0.2 m slabs, so the truth inside it is exactly zero. They share a single wall --wall m thick (0.1 by
    /// default), much thinner than the coarse probes are apart (0.5 / 1 / 2 / 4 m): what keeps A's light out of B
    /// is only how the GI tells a probe on the far side of a wall from one on this side.
    /// </para>
    /// <para>
    /// The camera stands inside B (the scene profile's pose), facing the shared wall; a second pose in A gives the
    /// light the leak is a fraction of. Matte white throughout, so the leak is not coloured by anything.
    /// </para>
    /// </remarks>
    [BlixApp("thinwall", Summary = "write two rooms sharing a thin wall, one open to the sky and one sealed, as glTF: a GI leak scene")]
    public static int ThinWall(AppArgs args)
    {
        var output = args.String("out") ?? "thinwall.gltf";
        var size = args.Float("size") ?? 6f;
        var wall = args.Float("wall") ?? 0.1f;
        if (size < 2f || wall <= 0f || wall > 1f)
            throw new AppArgsException("--size must be at least 2 m and --wall between 0 and 1 m.");

        var white = Matte("white", new Vector3(0.73f, 0.73f, 0.73f));
        var mesh = new Mesh("thinwall");
        var p = mesh.UsePrimitive(white);
        const float t = 0.2f;
        var h = size / 2f;
        var w = wall / 2f;
        // Room A spans x in [-size - w, -w], room B x in [w, size + w]; both z in [-h, h], y in [0, size].
        var left = -size - w;
        var right = size + w;
        Box(p, new Vector3(left - t, -t, -h - t), new Vector3(right + t, 0f, h + t));      // one floor under both
        Box(p, new Vector3(left - t, 0f, -h - t), new Vector3(right + t, size, -h));       // back wall, both rooms
        Box(p, new Vector3(left - t, 0f, h), new Vector3(right + t, size, h + t));         // front wall, both rooms
        Box(p, new Vector3(left - t, 0f, -h), new Vector3(left, size, h));                  // A's outer wall
        Box(p, new Vector3(right, 0f, -h), new Vector3(right + t, size, h));                // B's outer wall
        Box(p, new Vector3(-w, 0f, -h), new Vector3(w, size + t, h));                       // the shared thin wall
        Box(p, new Vector3(-w, size, -h - t), new Vector3(right + t, size + t, h + t));     // B's roof (A has none)

        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        scene.ToGltf2().SaveGLTF(output);
        Console.WriteLine($"thinwall: two {size:0.##} m rooms sharing a {wall:0.###} m wall (A open to the sky, B sealed) -> {output}");
        return 0;
    }

    /// <summary>
    /// A courtyard open to the sky with the places Sponza's shade lives in: a deep slot, a niche under a shelf, an L-shaped
    /// corridor whose far leg sees no sky and no sun, and two walls a few centimetres apart in the sun. In each of them a
    /// surface's light comes through a few narrow paths -- what a gather must find rarely and a filter or a blend can
    /// bias, and what the GI gate's other scenes (open boxes, flat walls) never ask.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The courtyard is --size m square (10 by default) inside 4 m walls of 0.2 m slabs, no roof. Its back wall (-Z) is
    /// 1.5 m thick and has a slot cut 1.3 m into it, 0.12 m wide, 2.5 m tall, closed at the back. Along the left wall (-X) a
    /// shelf 1.2 m deep stands 0.4 m off the floor. The right wall (+X) opens into a corridor 1 m wide and 2.5 m tall,
    /// roofed: 3 m straight out, then 3 m toward -Z to a dead end whose last block is red (0.63/0.065/0.05) -- its leg
    /// is lit by bounce alone. Near the front, two free-standing walls 2 m long and 0.1 m thick stand 0.08 m apart.
    /// </para>
    /// <para>
    /// White 0.73 elsewhere, matte. The scene profile's pose looks across the courtyard at the slot and the shelf; the
    /// gate adds one into the corridor (tools/testbeds/gate.sh).
    /// </para>
    /// </remarks>
    [BlixApp("crevices", Summary = "write a courtyard with a slot, a shelf niche, an L corridor and a narrow gap, as glTF: the GI gate's shade scene")]
    public static int Crevices(AppArgs args)
    {
        var output = args.String("out") ?? "crevices.gltf";
        var size = args.Float("size") ?? 10f;
        if (size < 8f) throw new AppArgsException("--size must be at least 8 m (the features need the room).");
        // --albedo A: the grey everything but the red block is (0.73 by default). Dark (crevices-dark: 0.25), a surface's
        // light comes through fewer bounces and rarer paths: the noisy gathers a blend or a filter can bias.
        var grey = args.Float("albedo") ?? 0.73f;
        if (grey <= 0f || grey >= 1f) throw new AppArgsException("--albedo must lie between 0 and 1.");
        var white = Matte("white", new Vector3(grey));
        var red = Matte("red", new Vector3(0.63f, 0.065f, 0.05f));
        var mesh = new Mesh("crevices");
        var p = mesh.UsePrimitive(white);
        const float t = 0.2f, height = 4f;
        var h = size / 2f;
        // Floor, under the corridor too.
        Box(p, new Vector3(-h - t, -t, -h - 1.5f), new Vector3(h + 4.5f, 0f, h + t));
        // Front (+Z) and left (-X) walls.
        Box(p, new Vector3(-h - t, 0f, h), new Vector3(h + t, height, h + t));
        Box(p, new Vector3(-h - t, 0f, -h - 1.5f), new Vector3(-h, height, h));
        // The back wall, 1.5 m thick, with a slot 0.12 m wide and 2.5 m tall at x = -1, closed at its back.
        const float slot = 0.12f, slotTall = 2.5f;
        var back0 = -h - 1.5f;
        Box(p, new Vector3(-h, 0f, back0), new Vector3(-1f - slot / 2f, height, -h));
        Box(p, new Vector3(-1f + slot / 2f, 0f, back0), new Vector3(h + t, height, -h));
        Box(p, new Vector3(-1f - slot / 2f, slotTall, back0), new Vector3(-1f + slot / 2f, height, -h));
        Box(p, new Vector3(-1f - slot / 2f, 0f, back0), new Vector3(-1f + slot / 2f, slotTall, back0 + t));   // its closed back
        // The right wall (+X), its opening into the corridor (z in [1, 2], 2.5 m tall).
        Box(p, new Vector3(h, 0f, -h), new Vector3(h + t, height, 1f));
        Box(p, new Vector3(h, 0f, 2f), new Vector3(h + t, height, h));
        Box(p, new Vector3(h, 2.5f, 1f), new Vector3(h + t, height, 2f));
        // The corridor: 1 m wide, 2.5 m tall, roofed; out along +X to x = h + 4, then toward -Z to z = -2.
        var c0 = h + t;
        Box(p, new Vector3(c0, 0f, 2f), new Vector3(c0 + 4f, 2.5f, 2f + t));                 // its +Z side, along the first leg
        Box(p, new Vector3(c0, 0f, -2f), new Vector3(c0 + 3f, 2.5f, 1f));                    // the inner corner block
        Box(p, new Vector3(c0 + 4f, 0f, -2f - t), new Vector3(c0 + 4f + t, 2.5f, 2f + t));   // its far +X side
        Box(p, new Vector3(c0 + 3f, 0f, -2f - t), new Vector3(c0 + 4f, 2.5f, -2f));          // the dead end
        Box(p, new Vector3(c0, 2.5f, -2f - t), new Vector3(c0 + 4f + t, 2.5f + t, 2f + t));  // the roof
        Box(mesh.UsePrimitive(red), new Vector3(c0 + 3f, 0f, -2f), new Vector3(c0 + 4f, 0.6f, -1.6f));   // red, at the dead end
        // The shelf along the left wall: 1.2 m deep, 0.4 m up, 3 m long, 0.1 m thick.
        Box(p, new Vector3(-h, 0.4f, -2f), new Vector3(-h + 1.2f, 0.5f, 1f));
        // Two walls 0.08 m apart near the front, 2 m long and 2.5 m tall.
        Box(p, new Vector3(1f, 0f, 2.5f), new Vector3(3f, 2.5f, 2.6f));
        Box(p, new Vector3(1f, 0f, 2.68f), new Vector3(3f, 2.5f, 2.78f));

        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        scene.ToGltf2().SaveGLTF(output);
        Console.WriteLine($"crevices: a {size:0.##} m courtyard with a slot, a shelf niche, an L corridor and a narrow gap, grey {grey:0.##} -> {output}");
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
