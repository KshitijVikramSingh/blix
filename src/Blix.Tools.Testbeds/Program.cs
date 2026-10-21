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
    /// Walls, floor and ceiling are one closed 0.2 m shell (no face inside another), so light that reaches the inside
    /// other than through the skylight and the open front is a leak. The reflectances are the box's published ones (white 0.73,
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
        var s = skylight / 2f;
        var mesh = new Mesh("cornell");
        CornellShell(mesh, white, red, green, size, skylight);

        // The two blocks, white: a tall one back left, a short one front right, each turned as in the original.
        // Standing on the floor: no bottom face (it would lie on the floor, seen from nowhere).
        var tall = new Mesh("tall_block");
        Box(tall.UsePrimitive(white), new Vector3(-0.3f * h, 0f, -0.3f * h), new Vector3(0.3f * h, 1.2f * h, 0.3f * h), bottom: false);
        var shortBlock = new Mesh("short_block");
        Box(shortBlock.UsePrimitive(white), new Vector3(-0.3f * h, 0f, -0.3f * h), new Vector3(0.3f * h, 0.6f * h, 0.3f * h), bottom: false);

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
        Console.WriteLine($"cornell: {size:0.##} m inside, {skylight:0.##} m skylight, a 0.2 m shell{(panel ? ", a hanging panel" : "")} -> {output}");
        return 0;
    }

    /// <summary>
    /// The Cornell box's room with cloth in the sun's shaft instead of its blocks: the scene for light through thin sheets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Material response and transport through cloth judged on their own, as the Cornell box judges bounce: the room is
    /// Cornell's (<see cref="CornellShell"/>: 6 m, a 1.5 m skylight, red and green walls) and nothing else in it is new. Two
    /// sheets, double-sided and matte, scattering part of the light through themselves (KHR_materials_diffuse_transmission):
    /// </para>
    /// <list type="bullet">
    /// <item>a flat white veil across the -X half of the shaft, 1 m under the skylight: the floor below it gets the sun
    /// through cloth beside the sun itself, one edge between them;</item>
    /// <item>a pleated red curtain hanging in the shaft, its folds 30 cm apart and 6 cm deep, the sun grazing them (17
    /// degrees off vertical): fold shading, light through the folds, and the room behind it lit through red cloth.</item>
    /// </list>
    /// <para>
    /// <c>--veil T</c>, <c>--curtain T</c>: the sheets' diffuse transmission (0.5, 0.4); 0 makes either opaque, the
    /// same scene for the A/B. The curtain transmits its own hue; the veil white.
    /// </para>
    /// </remarks>
    [BlixApp("drapes", Summary = "write the Cornell room with a veil and a pleated curtain in the sun's shaft, as glTF: a scene for light through cloth")]
    public static int Drapes(AppArgs args)
    {
        var output = args.String("out") ?? "drapes.gltf";
        var veilT = Math.Clamp(args.Float("veil") ?? 0.5f, 0f, 1f);
        var curtainT = Math.Clamp(args.Float("curtain") ?? 0.4f, 0f, 1f);
        const float size = 6f, skylight = 1.5f;
        var white = Matte("white", new Vector3(0.73f, 0.73f, 0.73f));
        var red = Matte("red", new Vector3(0.63f, 0.065f, 0.05f));
        var green = Matte("green", new Vector3(0.14f, 0.45f, 0.091f));
        var room = new Mesh("drapes_room");
        CornellShell(room, white, red, green, size, skylight);

        // The sun travels (-0.112, -0.957, -0.268) by default (SponzaLoop), so the shaft drifts -X and -Z as it falls:
        // at height y its square is the skylight's offset by (6 - y) / 0.957 x (-0.112, -0.268).
        static Vector2 Shaft(float y) => (6f - y) / 0.957f * new Vector2(-0.112f, -0.268f);
        var s = skylight / 2f;
        var veilAt = 5f;
        var vo = Shaft(veilAt);
        var veil = new Mesh("veil");
        Quad(veil.UsePrimitive(Cloth("veil", new Vector3(0.8f, 0.8f, 0.8f), veilT, Vector3.One)),
            new Vector3(-s - 0.1f + vo.X, veilAt, -s - 0.1f + vo.Y), new(s + 0.1f, 0, 0), new(0, 0, skylight + 0.2f), -Vector3.UnitY);

        // The curtain: a sheet along X at the shaft's middle depth halfway down, its folds a sine in Z, smooth normals.
        var curtainRed = new Vector3(0.6f, 0.12f, 0.1f);
        var curtain = new Mesh("curtain");
        var cp = curtain.UsePrimitive(Cloth("curtain", curtainRed, curtainT, curtainRed / MathF.Max(curtainRed.X, MathF.Max(curtainRed.Y, curtainRed.Z))));
        const float period = 0.3f, depth = 0.06f, step = 0.02f, top = 4.6f, bottom = 1.2f;
        var co = Shaft(3f);
        float x0 = -2f, x1 = 1f, zc = co.Y;
        var n = (int)MathF.Round((x1 - x0) / step);
        for (var i = 0; i < n; i++)
        {
            Vertex At(float x, float y)
            {
                var k = 2f * MathF.PI / period;
                var z = zc + 0.5f * depth * MathF.Sin(k * (x - x0));
                var slope = 0.5f * depth * k * MathF.Cos(k * (x - x0));
                return new Vertex(new Vector3(x, y, z), Vector3.Normalize(new Vector3(-slope, 0f, 1f)), new Vector2(x - x0, y));
            }
            float xa = x0 + i * step, xb = xa + step;
            var a = At(xa, bottom);
            var b = At(xb, bottom);
            var c = At(xb, top);
            var d = At(xa, top);
            cp.AddTriangle(a, b, c);
            cp.AddTriangle(a, c, d);
        }

        var scene = new SceneBuilder();
        scene.AddRigidMesh(room, Matrix4x4.Identity);
        scene.AddRigidMesh(veil, Matrix4x4.Identity);
        scene.AddRigidMesh(curtain, Matrix4x4.Identity);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        scene.ToGltf2().SaveGLTF(output);
        Console.WriteLine($"drapes: the Cornell room, a veil (transmission {veilT:0.##}) and a pleated curtain ({curtainT:0.##}) in the shaft -> {output}");
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

    // The Cornell box's room, size m inside: one closed 0.2 m shell, a skylight of side `skylight` in its ceiling, its front
    // (+Z) open; white floor, back and ceiling, red left (-X) and green right (+X). Cornell and its variants share it.
    private static void CornellShell(Mesh mesh, MaterialBuilder white, MaterialBuilder red, MaterialBuilder green, float size, float skylight)
    {
        var h = size / 2f;
        const float t = 0.2f;                          // the shell's thickness
        var w = mesh.UsePrimitive(white);
        // One closed shell, 0.2 m thick: the inside's faces span the room exactly, the outside's the room and the shell,
        // and rims close the open front and the skylight's shaft. Overlapping slabs (as before, 2026-10-21) were
        // watertight but buried faces in one another -- the floor ran under the walls, each wall down into the floor --
        // and the lightmap put texels there, the ones at the corners half inside a wall: the walls came out dirty in the
        // cook and the image. Every face here is seen from somewhere, and its edges meet its neighbours' exactly.
        var s = skylight / 2f;
        // The inside: floor, back, red left, green right, and the ceiling around the skylight (the front, +Z, open).
        Quad(w, new Vector3(-h, 0f, -h), new(2f * h, 0, 0), new(0, 0, 2f * h), Vector3.UnitY);
        Quad(w, new Vector3(-h, 0f, -h), new(2f * h, 0, 0), new(0, size, 0), Vector3.UnitZ);
        Quad(mesh.UsePrimitive(red), new Vector3(-h, 0f, -h), new(0, 0, 2f * h), new(0, size, 0), Vector3.UnitX);
        Quad(mesh.UsePrimitive(green), new Vector3(h, 0f, -h), new(0, 0, 2f * h), new(0, size, 0), -Vector3.UnitX);
        Ring(w, size, -h, h, -h, h, s, -Vector3.UnitY);
        // The skylight's shaft, through the shell's thickness, each side facing into the opening.
        Quad(w, new Vector3(-s, size, -s), new(0, 0, 2f * s), new(0, t, 0), Vector3.UnitX);
        Quad(w, new Vector3(s, size, -s), new(0, 0, 2f * s), new(0, t, 0), -Vector3.UnitX);
        Quad(w, new Vector3(-s, size, -s), new(2f * s, 0, 0), new(0, t, 0), Vector3.UnitZ);
        Quad(w, new Vector3(-s, size, s), new(2f * s, 0, 0), new(0, t, 0), -Vector3.UnitZ);
        // The outside: under, behind, the two sides, and the roof around the skylight; flush with the opening in front.
        Quad(w, new Vector3(-h - t, -t, -h - t), new(2f * (h + t), 0, 0), new(0, 0, 2f * h + t), -Vector3.UnitY);
        Quad(w, new Vector3(-h - t, -t, -h - t), new(2f * (h + t), 0, 0), new(0, size + 2f * t, 0), -Vector3.UnitZ);
        Quad(w, new Vector3(-h - t, -t, -h - t), new(0, 0, 2f * h + t), new(0, size + 2f * t, 0), -Vector3.UnitX);
        Quad(w, new Vector3(h + t, -t, -h - t), new(0, 0, 2f * h + t), new(0, size + 2f * t, 0), Vector3.UnitX);
        Ring(w, size + t, -h - t, h + t, -h - t, h, s, Vector3.UnitY);
        // The front rim around the opening (z = h): below the floor, above the ceiling, beside each wall.
        Quad(w, new Vector3(-h - t, -t, h), new(2f * (h + t), 0, 0), new(0, t, 0), Vector3.UnitZ);
        Quad(w, new Vector3(-h - t, size, h), new(2f * (h + t), 0, 0), new(0, t, 0), Vector3.UnitZ);
        Quad(w, new Vector3(-h - t, 0f, h), new(t, 0, 0), new(0, size, 0), Vector3.UnitZ);
        Quad(w, new Vector3(h, 0f, h), new(t, 0, 0), new(0, size, 0), Vector3.UnitZ);
    }

    // The glTF vertex the cook reads in full: position, normal, one UV set (the cook generates the tangent).
    private sealed class Mesh : MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>
    {
        public Mesh(string name) : base(name) { }
    }

    private static MaterialBuilder Matte(string name, Vector3 albedo) =>
        new MaterialBuilder(name).WithMetallicRoughnessShader().WithBaseColor(new Vector4(albedo, 1f)).WithMetallicRoughness(0f, 1f);

    // A matte sheet seen from both sides that scatters `transmission` of the light reaching it through itself, tinted by
    // `transmitted` (KHR_materials_diffuse_transmission; its colour defaults to white, so it is always written).
    private static MaterialBuilder Cloth(string name, Vector3 albedo, float transmission, Vector3 transmitted) =>
        Matte(name, albedo).WithDoubleSide(true)
            .WithDiffuseTransmissionFactor(null, transmission)
            .WithDiffuseTransmissionColor(null, transmitted);

    // A horizontal face at height y over [x0, x1] x [z0, z1] around a square hole of half-width s at the centre: four
    // rectangles, facing n.
    private static void Ring(PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture1, VertexEmpty> p,
        float y, float x0, float x1, float z0, float z1, float s, Vector3 n)
    {
        Quad(p, new Vector3(x0, y, z0), new(-s - x0, 0, 0), new(0, 0, z1 - z0), n);
        Quad(p, new Vector3(s, y, z0), new(x1 - s, 0, 0), new(0, 0, z1 - z0), n);
        Quad(p, new Vector3(-s, y, z0), new(2f * s, 0, 0), new(0, 0, -s - z0), n);
        Quad(p, new Vector3(-s, y, s), new(2f * s, 0, 0), new(0, 0, z1 - s), n);
    }

    // An axis-aligned box from its two corners, all six faces outward (bottom: false, the -Y face left out).
    private static void Box(PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture1, VertexEmpty> p, Vector3 lo, Vector3 hi, bool bottom = true)
    {
        var d = hi - lo;
        Quad(p, new Vector3(lo.X, lo.Y, hi.Z), new(d.X, 0, 0), new(0, d.Y, 0), Vector3.UnitZ);
        Quad(p, new Vector3(hi.X, lo.Y, lo.Z), new(-d.X, 0, 0), new(0, d.Y, 0), -Vector3.UnitZ);
        Quad(p, new Vector3(hi.X, lo.Y, hi.Z), new(0, 0, -d.Z), new(0, d.Y, 0), Vector3.UnitX);
        Quad(p, new Vector3(lo.X, lo.Y, lo.Z), new(0, 0, d.Z), new(0, d.Y, 0), -Vector3.UnitX);
        Quad(p, new Vector3(lo.X, hi.Y, hi.Z), new(d.X, 0, 0), new(0, 0, -d.Z), Vector3.UnitY);
        if (bottom) Quad(p, new Vector3(lo.X, lo.Y, lo.Z), new(d.X, 0, 0), new(0, 0, d.Z), -Vector3.UnitY);
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
