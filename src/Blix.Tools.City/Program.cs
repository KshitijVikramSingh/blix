using System.Globalization;
using System.Numerics;
using Blix.Core;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace Blix.Tools.City;

/// <summary>
/// <c>blix city</c> — writes a seeded, parametric city as a glTF source: the scale scene.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a generator and not another download.</b> A fixed scene answers "how fast is this frame"; it
/// cannot answer "how does the frame grow with the world", which is the question a huge world asks. This
/// draws the same kind of city at any size from a seed, so doubling <c>--blocks</c> is a controlled
/// experiment and two runs with one seed are one scene.
/// </para>
/// <para>
/// <b>It authors a source; it does not cook.</b> The output is plain glTF, read by the cook like any other
/// (tools/city/setup.sh), so every stage downstream is exercised exactly as an artist's file would be.
/// A handful of unique meshes are placed by many nodes, so the instance count grows with the city while
/// the geometry it is made of does not: what a renderer that instances well should find cheap.
/// </para>
/// </remarks>
public static class Program
{
    public static int Main(string[] args) => BlixApps.Main(args);

    [BlixApp("city", Summary = "write a seeded city as glTF (blocks of buildings, trees, lamps): the scale scene", Default = true)]
    public static int City(AppArgs args)
    {
        var options = new CityOptions(
            Seed: args.Int("seed") ?? 1,
            Blocks: args.Int("blocks") ?? 16,
            BlockSize: args.Float("block-size") ?? 64f,
            Street: args.Float("street") ?? 14f,
            Variants: args.Int("variants") ?? 12,
            Lights: !args.Flag("no-lights"));
        var output = args.String("out") ?? "city.gltf";
        if (options.Blocks < 1 || options.Variants < 1 || options.BlockSize < 16f || options.Street < 4f)
        {
            throw new AppArgsException("--blocks and --variants must be at least 1, --block-size at least 16, --street at least 4.");
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var city = CityBuilder.Build(options);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        city.Scene.ToGltf2().SaveGLTF(output);
        Console.WriteLine(
            $"city: seed {options.Seed}, {options.Blocks}x{options.Blocks} blocks of {options.BlockSize:0} m "
            + $"({options.Extent:0} m across): {city.Buildings:N0} buildings, {city.Trees:N0} trees, {city.Lamps:N0} lamps "
            + $"from {city.UniqueMeshes} unique meshes, {city.Triangles:N0} placed triangles -> {output} "
            + $"in {started.Elapsed.TotalSeconds:0.0} s");
        return 0;
    }
}

/// <summary>What the city is: a seed and its sizes, in metres.</summary>
public sealed record CityOptions(int Seed, int Blocks, float BlockSize, float Street, int Variants, bool Lights)
{
    /// <summary>The city's width and depth.</summary>
    public float Extent => Blocks * (BlockSize + Street);
}

/// <summary>A built city, and the counts it reports.</summary>
public sealed record BuiltCity(SceneBuilder Scene, int Buildings, int Trees, int Lamps, int UniqueMeshes, long Triangles);

internal static class CityBuilder
{
    // The glTF vertex the cook reads in full: position, normal, one UV set (the cook mirrors set 0
    // into set 1 and generates the tangent).
    private sealed class Mesh : MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>
    {
        public Mesh(string name) : base(name) { }
    }

    private const float FloorHeight = 3.2f;
    private const float Bay = 3.0f;

    public static BuiltCity Build(CityOptions o)
    {
        var random = new Random(o.Seed);
        var materials = new Materials();
        var scene = new SceneBuilder("city");
        long triangles = 0;

        // The unique meshes. Variants differ in footprint, storeys and palette; every one is reused.
        var variants = Enumerable.Range(0, o.Variants).Select(v =>
        {
            var width = 12f + 3f * random.Next(0, 5);
            var depth = 12f + 3f * random.Next(0, 4);
            var storeys = 3 + random.Next(0, 9);
            var mesh = Building($"building_{v}", width, depth, storeys, materials.Facade(random), materials);
            return (Mesh: mesh, Width: width, Depth: depth, Triangles: TriangleCount(mesh));
        }).ToArray();
        var tree = Tree(materials);
        var lamp = Lamp(materials);
        var block = Ground("block", o.BlockSize, o.BlockSize, 8, materials.Pavement);
        var street = Ground("street", o.BlockSize + o.Street, o.Street, 8, materials.Asphalt);
        int treeTris = TriangleCount(tree), lampTris = TriangleCount(lamp), blockTris = TriangleCount(block), streetTris = TriangleCount(street);

        int buildings = 0, trees = 0, lamps = 0;
        var pitch = o.BlockSize + o.Street;
        var origin = -0.5f * o.Extent;
        for (var bx = 0; bx < o.Blocks; bx++)
        for (var bz = 0; bz < o.Blocks; bz++)
        {
            var x0 = origin + bx * pitch;
            var z0 = origin + bz * pitch;
            scene.AddRigidMesh(block, Matrix4x4.CreateTranslation(x0 + 0.5f * o.BlockSize, 0f, z0 + 0.5f * o.BlockSize));
            scene.AddRigidMesh(street, Matrix4x4.CreateTranslation(x0 + 0.5f * pitch, -0.15f, z0 + o.BlockSize + 0.5f * o.Street));
            scene.AddRigidMesh(street, Matrix4x4.CreateRotationY(MathF.PI / 2f)
                * Matrix4x4.CreateTranslation(x0 + o.BlockSize + 0.5f * o.Street, -0.15f, z0 + 0.5f * pitch));
            triangles += blockTris + 2 * streetTris;

            // Lots around the block's edge, facing out; the middle is a courtyard.
            var lotsPerSide = Math.Max(1, (int)(o.BlockSize / 22f));
            var lot = o.BlockSize / lotsPerSide;
            for (var side = 0; side < 4; side++)
            for (var i = 0; i < lotsPerSide; i++)
            {
                if (random.NextDouble() < 0.12) continue; // the odd empty lot keeps a skyline from being a wall
                var v = variants[random.Next(variants.Length)];
                var along = (i + 0.5f) * lot;
                var inset = 0.5f * v.Depth + 1.5f;
                var (px, pz, yaw) = side switch
                {
                    0 => (along, inset, 0f),
                    1 => (o.BlockSize - inset, along, MathF.PI / 2f),
                    2 => (o.BlockSize - along, o.BlockSize - inset, MathF.PI),
                    _ => (inset, o.BlockSize - along, -MathF.PI / 2f),
                };
                scene.AddRigidMesh(v.Mesh, Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(x0 + px, 0f, z0 + pz));
                triangles += v.Triangles;
                buildings++;
            }

            // Street trees and lamps along the block's two street-facing kerbs.
            for (var s = 6f; s < o.BlockSize; s += 12f)
            {
                if (random.NextDouble() < 0.7)
                {
                    scene.AddRigidMesh(tree, Matrix4x4.CreateRotationY((float)random.NextDouble() * MathF.Tau)
                        * Matrix4x4.CreateTranslation(x0 + s, 0f, z0 + o.BlockSize + 2f));
                    triangles += treeTris;
                    trees++;
                }

                if ((int)(s / 12f) % 2 == 0)
                {
                    var at = new Vector3(x0 + o.BlockSize + 2f, 0f, z0 + s);
                    scene.AddRigidMesh(lamp, Matrix4x4.CreateTranslation(at));
                    if (o.Lights)
                    {
                        var light = new LightBuilder.Point { Color = new Vector3(1f, 0.82f, 0.6f), Intensity = 60f, Range = 18f };
                        scene.AddLight(light, Matrix4x4.CreateTranslation(at + new Vector3(0f, 5.6f, 0f)));
                    }

                    triangles += lampTris;
                    lamps++;
                }
            }
        }

        return new BuiltCity(scene, buildings, trees, lamps, variants.Length + 4, triangles);
    }

    private static int TriangleCount(Mesh mesh) => mesh.Primitives.Sum(p => p.Triangles.Count);

    // A facade of bays and storeys, each bay a window set into the wall: frame, four reveals and a glass
    // pane. Enough relief to be geometry rather than a box, and cheap enough to place thousands of.
    private static Mesh Building(string name, float width, float depth, int storeys, MaterialBuilder facade, Materials m)
    {
        var mesh = new Mesh(name);
        var wall = mesh.UsePrimitive(facade);
        var glass = mesh.UsePrimitive(m.Glass);
        var height = storeys * FloorHeight;
        var hw = 0.5f * width;
        var hd = 0.5f * depth;

        // Four facades: origin corner, the direction along it, its length, and the outward normal.
        var faces = new (Vector3 Corner, Vector3 Along, float Length, Vector3 Normal)[]
        {
            (new(-hw, 0, -hd), Vector3.UnitX, width, -Vector3.UnitZ),
            (new(hw, 0, -hd), Vector3.UnitZ, depth, Vector3.UnitX),
            (new(hw, 0, hd), -Vector3.UnitX, width, Vector3.UnitZ),
            (new(-hw, 0, hd), -Vector3.UnitZ, depth, -Vector3.UnitX),
        };
        foreach (var (corner, along, length, normal) in faces)
        {
            var bays = Math.Max(1, (int)(length / Bay));
            var bay = length / bays;
            for (var f = 0; f < storeys; f++)
            for (var b = 0; b < bays; b++)
            {
                var o = corner + along * (b * bay) + Vector3.UnitY * (f * FloorHeight);
                Window(wall, glass, o, along * bay, Vector3.UnitY * FloorHeight, normal, f == 0 ? 0.55f : 0.35f);
            }
        }

        // Roof.
        Quad(wall, new(-hw, height, -hd), new(width, 0, 0), new(0, 0, depth), Vector3.UnitY);
        return mesh;
    }

    // One bay: the wall around a window opening (frame), four reveals stepping in, and the pane.
    private static void Window(PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture1, VertexEmpty> wall,
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture1, VertexEmpty> glass,
        Vector3 origin, Vector3 u, Vector3 v, Vector3 n, float margin)
    {
        var mu = u * (margin * 0.5f);
        var mv = v * (margin * 0.5f);
        var inner = origin + mu + mv;
        var iu = u - 2f * mu;
        var iv = v - 2f * mv;
        var depth = -n * 0.25f;

        Quad(wall, origin, u, mv, n);                    // sill band
        Quad(wall, origin + v - mv, u, mv, n);           // head band
        Quad(wall, origin + mv, mu, iv, n);              // left jamb
        Quad(wall, origin + u - mu + mv, mu, iv, n);     // right jamb
        Quad(wall, inner, iu, depth, Vector3.Normalize(v));             // reveal: bottom
        Quad(wall, inner + iv + depth, iu, -depth, -Vector3.Normalize(v)); // reveal: top
        Quad(wall, inner + depth, iv, -depth, Vector3.Normalize(u));    // reveal: left
        Quad(wall, inner + iu, iv, depth, -Vector3.Normalize(u));       // reveal: right
        Quad(glass, inner + depth, iu, iv, n);                          // pane
    }

    private static Mesh Tree(Materials m)
    {
        var mesh = new Mesh("tree");
        var bark = mesh.UsePrimitive(m.Bark);
        const int sides = 8;
        for (var i = 0; i < sides; i++)
        {
            var a0 = MathF.Tau * i / sides;
            var a1 = MathF.Tau * (i + 1) / sides;
            var p0 = new Vector3(MathF.Cos(a0), 0, MathF.Sin(a0)) * 0.22f;
            var p1 = new Vector3(MathF.Cos(a1), 0, MathF.Sin(a1)) * 0.22f;
            Quad(bark, p0, p1 - p0, new Vector3(0, 3.2f, 0), Vector3.Normalize(p0 + p1));
        }

        Sphere(mesh.UsePrimitive(m.Leaves), new Vector3(0, 5.2f, 0), 2.6f, 2);
        return mesh;
    }

    private static Mesh Lamp(Materials m)
    {
        var mesh = new Mesh("lamp");
        var iron = mesh.UsePrimitive(m.Iron);
        Box(iron, new Vector3(0, 2.8f, 0), new Vector3(0.09f, 2.8f, 0.09f));
        Box(mesh.UsePrimitive(m.LampGlass), new Vector3(0, 5.6f, 0), new Vector3(0.25f, 0.3f, 0.25f));
        return mesh;
    }

    private static Mesh Ground(string name, float width, float depth, int cells, MaterialBuilder material)
    {
        var mesh = new Mesh(name);
        var p = mesh.UsePrimitive(material);
        var cw = width / cells;
        var cd = depth / cells;
        for (var i = 0; i < cells; i++)
        for (var j = 0; j < cells; j++)
        {
            Quad(p, new Vector3(-0.5f * width + i * cw, 0, -0.5f * depth + j * cd), new Vector3(cw, 0, 0), new Vector3(0, 0, cd), Vector3.UnitY);
        }

        return mesh;
    }

    private static void Box(PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture1, VertexEmpty> p, Vector3 c, Vector3 h)
    {
        Quad(p, c + new Vector3(-h.X, -h.Y, h.Z), new(2 * h.X, 0, 0), new(0, 2 * h.Y, 0), Vector3.UnitZ);
        Quad(p, c + new Vector3(h.X, -h.Y, -h.Z), new(-2 * h.X, 0, 0), new(0, 2 * h.Y, 0), -Vector3.UnitZ);
        Quad(p, c + new Vector3(h.X, -h.Y, h.Z), new(0, 0, -2 * h.Z), new(0, 2 * h.Y, 0), Vector3.UnitX);
        Quad(p, c + new Vector3(-h.X, -h.Y, -h.Z), new(0, 0, 2 * h.Z), new(0, 2 * h.Y, 0), -Vector3.UnitX);
        Quad(p, c + new Vector3(-h.X, h.Y, h.Z), new(2 * h.X, 0, 0), new(0, 0, -2 * h.Z), Vector3.UnitY);
    }

    private static void Sphere(PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture1, VertexEmpty> p, Vector3 c, float r, int rings)
    {
        var lat = 4 * rings;
        var lon = 8 * rings;
        Vector3 At(int i, int j)
        {
            var t = MathF.PI * i / lat;
            var f = MathF.Tau * j / lon;
            return new Vector3(MathF.Sin(t) * MathF.Cos(f), MathF.Cos(t), MathF.Sin(t) * MathF.Sin(f));
        }

        for (var i = 0; i < lat; i++)
        for (var j = 0; j < lon; j++)
        {
            var a = At(i, j); var b = At(i + 1, j); var d = At(i, j + 1); var e = At(i + 1, j + 1);
            Vertex V(Vector3 n, float u, float v) => new(c + n * r, n, new Vector2(u, v));
            p.AddTriangle(V(a, j / (float)lon, i / (float)lat), V(d, (j + 1) / (float)lon, i / (float)lat), V(b, j / (float)lon, (i + 1) / (float)lat));
            p.AddTriangle(V(d, (j + 1) / (float)lon, i / (float)lat), V(e, (j + 1) / (float)lon, (i + 1) / (float)lat), V(b, j / (float)lon, (i + 1) / (float)lat));
        }
    }

    private readonly record struct Vertex(Vector3 P, Vector3 N, Vector2 Uv)
    {
        public static implicit operator VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(Vertex v) =>
            new(new VertexPositionNormal(v.P, v.N), new VertexTexture1(v.Uv));
    }

    // A quad from a corner and two edges, wound so that the given normal faces out. UVs are in metres,
    // so a material tiles at the same scale on every face.
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

    private sealed class Materials
    {
        public MaterialBuilder Glass { get; } = Pbr("glass", new Vector4(0.08f, 0.1f, 0.12f, 1f), 0.9f, 0.08f);
        public MaterialBuilder Pavement { get; } = Pbr("pavement", new Vector4(0.42f, 0.41f, 0.39f, 1f), 0f, 0.85f);
        public MaterialBuilder Asphalt { get; } = Pbr("asphalt", new Vector4(0.12f, 0.12f, 0.13f, 1f), 0f, 0.9f);
        public MaterialBuilder Bark { get; } = Pbr("bark", new Vector4(0.25f, 0.17f, 0.11f, 1f), 0f, 0.9f);
        public MaterialBuilder Leaves { get; } = Pbr("leaves", new Vector4(0.18f, 0.32f, 0.12f, 1f), 0f, 0.75f);
        public MaterialBuilder Iron { get; } = Pbr("iron", new Vector4(0.05f, 0.05f, 0.05f, 1f), 0.8f, 0.45f);
        public MaterialBuilder LampGlass { get; } = Pbr("lamp_glass", new Vector4(1f, 0.9f, 0.7f, 1f), 0f, 0.3f)
            .WithEmissive(new Vector3(1f, 0.8f, 0.55f));

        private readonly List<MaterialBuilder> facades = new();

        // A small palette of plaster and brick, reused: a material per variant would make every building
        // its own draw group, which is a test of something else.
        public MaterialBuilder Facade(Random random)
        {
            if (facades.Count < 6)
            {
                var tone = 0.35f + 0.4f * (float)random.NextDouble();
                var warm = (float)random.NextDouble();
                facades.Add(Pbr($"facade_{facades.Count}",
                    new Vector4(tone + 0.15f * warm, tone + 0.05f * warm, tone - 0.05f * warm, 1f), 0f, 0.8f));
            }

            return facades[random.Next(facades.Count)];
        }

        private static MaterialBuilder Pbr(string name, Vector4 colour, float metallic, float roughness) =>
            new MaterialBuilder(name).WithMetallicRoughnessShader().WithBaseColor(colour).WithMetallicRoughness(metallic, roughness);
    }
}
