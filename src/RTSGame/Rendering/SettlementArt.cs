using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;
using RTSGame.Simulation.Economy;

namespace RTSGame.Rendering;

/// <summary>
/// The settlement's art: which file is a granary, how big it stands, and which way up.
/// </summary>
/// <remarks>
/// <b>Everything here that could be measured is measured.</b> The pack is a hundred and thirty CC0
/// models authored to a loose convention — every node transform is identity and every model stands on
/// Y ≈ 0, but the horizontal centring wanders by up to half a unit because a model is drawn wherever it
/// looked right inside its tile. So the fit is read off the geometry at load
/// (<see cref="PropModel.NormaliseToUnitFootprint"/>): recentre on the measured footprint, drop the
/// measured base to the ground, scale so the wider ground dimension is one unit. Adding the
/// hundred-and-thirty-first model then needs no fitting pass, and there is no table of per-asset
/// constants to drift out of step with the assets.
/// <para>
/// The one thing that is <em>not</em> measured is how wide each building should be in metres, and that
/// is correct: it is not a fact about the model, it is a fact about the simulation. A granary is 7.5 m
/// because <see cref="NodeFootprint"/> says it occupies five placement cells and bodies route around
/// exactly that square. So the art is scaled to the footprint the game already enforces, rather than
/// the game being adjusted to suit the art.
/// </para>
/// <para>
/// No textures are involved anywhere. The whole pack is untextured, position-and-normal only, with a
/// flat linear base colour per material — twenty-one distinct colours describe all of it — which is why
/// this needs no sampler, no cook and no texture memory: each primitive becomes one instanced batch
/// tinted by its material.
/// </para>
/// </remarks>
internal sealed class SettlementArt : IDisposable
{
    private readonly List<PropModel> owned = new();

    private SettlementArt(
        PropModel granary,
        PropModel depot,
        PropModel[] houses,
        PropModel fieldPlot,
        PropModel[] crop,
        PropModel[] trees,
        PropModel stumps,
        PropModel grainHeap,
        PropModel woodHeap,
        PropModel? villager)
    {
        Granary = granary;
        Depot = depot;
        Houses = houses;
        FieldPlot = fieldPlot;
        Crop = crop;
        Trees = trees;
        Stumps = stumps;
        GrainHeap = grainHeap;
        WoodHeap = woodHeap;
        Villager = villager;
        owned.AddRange(new[] { granary, depot, fieldPlot, stumps, grainHeap, woodHeap });
        owned.AddRange(houses);
        owned.AddRange(crop);
        owned.AddRange(trees);
        if (villager is not null) owned.Add(villager);
    }

    public PropModel Granary { get; }

    public PropModel Depot { get; }

    /// <summary>Three cottages, picked per house so a village is not one building repeated.</summary>
    public PropModel[] Houses { get; }

    /// <summary>Tilled ground. Flat, so it receives the sun and casts nothing.</summary>
    public PropModel FieldPlot { get; }

    /// <summary>Standing wheat at three heights, which is the crop cycle in geometry.</summary>
    public PropModel[] Crop { get; }

    /// <summary>Two broadleaves and a pine, picked per tree.</summary>
    public PropModel[] Trees { get; }

    /// <summary>What a felled tree leaves behind.</summary>
    public PropModel Stumps { get; }

    public PropModel GrainHeap { get; }

    public PropModel WoodHeap { get; }

    /// <summary>A person. Static — the pack has no rig — but a person rather than a cylinder.</summary>
    public PropModel? Villager { get; }

    public IReadOnlyList<PropModel> All => owned;

    /// <summary>
    /// Loads every model the settlement draws, or throws saying which one is missing.
    /// </summary>
    /// <remarks>
    /// Deliberately not best-effort. A demo that silently falls back to boxes when an asset is absent is
    /// a demo where nobody notices for a week that half the settlement is boxes — and the whole point of
    /// this pass was that the greybox could not be read. If a file is missing the run says so and stops.
    /// </remarks>
    public static SettlementArt Load(
        VulkanGraphicsDevice device,
        ShaderProgramHandle sceneShader,
        PipelineHandle scenePipeline,
        ShaderProgramHandle casterShader,
        PipelineHandle casterPipeline)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "models");

        PropModel Prop(string file, bool casts = true, bool stretchToSquare = false)
        {
            var path = Path.Combine(directory, file + ".gltf");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"The settlement's art is missing '{file}.gltf'. Models live in " +
                    "src/RTSGame/Assets/models and are copied to the output by the csproj; a missing one " +
                    "is a build or a rename, not something to draw a box for.", path);
            }

            var model = new GltfStaticImporter().Import(
                new AssetImportContext(AssetId.Parse(file), path));
            var parts = model.Primitives
                .Select(prim => (
                    prim.Mesh,
                    Tint: prim.Material?.BaseColorFactor ?? new Vector4(0.6f, 0.6f, 0.6f, 1f)))
                .ToArray();
            var bounds = parts.Select(part => part.Mesh).CombinedBounds();
            var bake = stretchToSquare
                ? StretchToUnitSquare(bounds)
                : PropModel.NormaliseToUnitFootprint(bounds);
            return PropModel.Create(
                device, file, parts, sceneShader, scenePipeline,
                casts ? casterShader : null, casts ? casterPipeline : null, bake);
        }

        // <b>Both stretched to the square, so the crop and the dirt it grows in are the same rectangle.</b>
        // The crop was left unstretched for a while, on the reasoning that distorting standing wheat is
        // visible where distorting flat ground is not — true, and it produced a worse problem: a 1.72 x 1.45
        // crop uniform-scaled into a square plot covers 84% of it, so every field had the wheat sitting
        // inset from its own soil with a margin of bare dirt on two sides. Aligning them matters more than
        // the last few per cent of stalk shape, and the SecondAge crop is nearly square anyway, so the
        // stretch it now takes is about three per cent.
        // <b>SecondAge throughout, and it is purely a look.</b> The pack ships two ages and three levels of
        // most buildings; we have no age or upgrade mechanism of our own and are not acquiring one, so this
        // is the same choice as picking a colour. The FirstAge buildings are open-frame shelters — four
        // posts and a roof — which from a top-down camera read as tables, and a player who cannot recognise
        // a house cannot tell whether their population is capped by housing. Measured: nine houses with room
        // for thirty-six, and the report was "there just aren't any houses".
        //
        // <b>Except the field's plot, which stays FirstAge because the SecondAge farm is not a plot.</b> It
        // is a 1.84 m farmstead building with seven materials, and a field is ground — the decision §21
        // settled and the reason a dozen of them tile into a patchwork instead of standing about as crates.
        // The crop does move up an age, and gains from it: the SecondAge wheat is very nearly square
        // (1.51 x 1.46 against 1.72 x 1.45), so stretching it to fill a square plot barely distorts it,
        // which is what fixes the crop sitting inset from the dirt it grows in.
        //
        // <b>Both stores are barns, at the two sizes the game actually has.</b> Two wrong answers came
        // before this one and both are worth keeping. The pack's town centre, which at level three is a
        // stone plaza with a fountain and a bronze of two stags — a fine landmark and a terrible granary,
        // because the one building the settlement carries its food to read as a monument. Then a windmill,
        // which says grain without needing a label and <em>still</em> failed, for a reason that is the more
        // useful lesson: <b>a granary is five placement cells, 7.5 m of ground, and a windmill's bounding
        // box is mostly sails.</b> Normalised to that footprint it drew a slim tower in the middle of a
        // large square, and the report was that the granary "doesn't match the area of its visual model".
        // Exactly right — the model has to fill the ground it occupies, or the footprint is a lie the player
        // walks into.
        //
        // So: the big barn at 7.5 m and the small one at 4.5 m, same family, and the size is the read. Which
        // is also true — they are both stores, and one is bigger.
        var art = new SettlementArt(
            granary: Prop("Storage_SecondAge_Level3"),
            depot: Prop("Storage_SecondAge_Level1"),
            houses: new[]
            {
                Prop("Houses_SecondAge_1_Level2"),
                Prop("Houses_SecondAge_2_Level2"),
                Prop("Houses_SecondAge_3_Level2"),
            },
            fieldPlot: Prop("Farm_FirstAge_Level3", casts: false, stretchToSquare: true),
            crop: new[]
            {
                Prop("Farm_SecondAge_Level1_Wheat", stretchToSquare: true),
                Prop("Farm_SecondAge_Level2_Wheat", stretchToSquare: true),
                Prop("Farm_SecondAge_Level3_Wheat", stretchToSquare: true),
            },
            trees: new[]
            {
                Prop("Resource_Tree1"),
                Prop("Resource_Tree2"),
                Prop("Resource_PineTree"),
            },
            stumps: Prop("Resource_Tree_Group_Cut", casts: false),
            // <b>One crate, not a stack of them, and the reason is the normalisation.</b> Props are baked to
            // a unit footprint, so a model gets its height from its own proportions — and a stack of crates
            // is 0.12 m across and 0.25 m tall, better than twice as tall as it is wide. A heap of forty
            // units asks for two metres across and therefore got four metres of crates towering over the
            // houses. A single crate is very nearly cubic, so a metre across is a metre tall.
            grainHeap: Prop("Crate"),
            woodHeap: Prop("Logs"),
            villager: LoadVillager(device, directory, sceneShader, scenePipeline, casterShader, casterPipeline));
        return art;
    }

    /// <summary>
    /// The villager body, from the OBJ, normalised by <em>height</em> rather than footprint.
    /// </summary>
    /// <remarks>
    /// A person is the one thing in the settlement whose scale is set by how tall it is: a villager is
    /// 1.45 m because that is what the locomotion layer was calibrated against, and normalising it by its
    /// footprint would make its size depend on how far apart its feet happen to be in the source model.
    /// <para>
    /// Best-effort, unlike the buildings, and for a reason: this is the one asset with no rig, so a
    /// villager slides rather than walks. If it reads worse than the cylinder it replaced, dropping the
    /// file is how you go back, and that should not stop the settlement loading.
    /// </para>
    /// </remarks>
    private static PropModel? LoadVillager(
        VulkanGraphicsDevice device,
        string directory,
        ShaderProgramHandle sceneShader,
        PipelineHandle scenePipeline,
        ShaderProgramHandle casterShader,
        PipelineHandle casterPipeline)
    {
        var path = Path.Combine(directory, "Villager.obj");
        if (!File.Exists(path)) return null;
        var mesh = new ObjImporter { RecenterToOrigin = false }.Import(
            new AssetImportContext(AssetId.Parse("villager"), path));
        var bake = NormaliseToUnitHeight(mesh.Bounds);
        return PropModel.Create(
            device,
            "villager",
            // Wool and linen, in the pack's range. It was 0.62 — an albedo written against the greybox —
            // which under a sun of two and a bit tonemaps to very nearly white, so seventeen villagers
            // read as seventeen bright specks rather than as people.
            new[] { (mesh, new Vector4(0.33f, 0.25f, 0.19f, 1f)) },
            sceneShader,
            scenePipeline,
            casterShader,
            casterPipeline,
            bake);
    }

    /// <summary>Centred on its footprint, standing on the ground, exactly one unit tall.</summary>
    private static Matrix4x4 NormaliseToUnitHeight(Bounds3 bounds)
    {
        var size = bounds.Max - bounds.Min;
        var scale = size.Y > 1e-4f ? 1f / size.Y : 1f;
        var centre = new Vector3(
            (bounds.Min.X + bounds.Max.X) * 0.5f,
            bounds.Min.Y,
            (bounds.Min.Z + bounds.Max.Z) * 0.5f);
        return Matrix4x4.CreateTranslation(-centre) * Matrix4x4.CreateScale(scale);
    }

    /// <summary>Centred, standing on the ground, and filling a 1 x 1 square on the ground plane.</summary>
    private static Matrix4x4 StretchToUnitSquare(Bounds3 bounds)
    {
        var size = bounds.Max - bounds.Min;
        var centre = new Vector3(
            (bounds.Min.X + bounds.Max.X) * 0.5f,
            bounds.Min.Y,
            (bounds.Min.Z + bounds.Max.Z) * 0.5f);
        // Height rides on the smaller of the two ground scales, so stretching a plot to fill its square
        // does not also make the wheat on it taller.
        var scaleX = size.X > 1e-4f ? 1f / size.X : 1f;
        var scaleZ = size.Z > 1e-4f ? 1f / size.Z : 1f;
        return Matrix4x4.CreateTranslation(-centre) *
               Matrix4x4.CreateScale(scaleX, MathF.Min(scaleX, scaleZ), scaleZ);
    }

    /// <summary>Begins a frame on every model, so nothing carries last frame's copies.</summary>
    public void Begin()
    {
        foreach (var model in owned) model.Begin();
    }

    public void Stage(ReadOnlySpan<byte> scenePush, ReadOnlySpan<byte> shadowPush)
    {
        foreach (var model in owned) model.Stage(scenePush, shadowPush);
    }

    public void DrawScene(RenderPassBuilder pass, IReadOnlyList<ShaderTextureBinding>? textures)
    {
        foreach (var model in owned) model.DrawScene(pass, textures);
    }

    public void DrawShadow(RenderPassBuilder pass)
    {
        foreach (var model in owned) model.DrawShadow(pass);
    }

    /// <summary>
    /// Where a copy of a unit-footprint prop goes: a metre width, a yaw, and the ground under it.
    /// </summary>
    /// <remarks>
    /// The whole placement API, and it is one line because the fit was baked at load. Yaw is varied per
    /// building by the caller from a stable id rather than a random draw, so a settlement is not a row of
    /// identically-oriented models and is still the same settlement after a save.
    /// </remarks>
    public static Matrix4x4 Placement(Vector2 position, float groundHeight, float widthMetres, float yaw) =>
        Matrix4x4.CreateScale(widthMetres) *
        Matrix4x4.CreateRotationY(yaw) *
        Matrix4x4.CreateTranslation(position.X, groundHeight, position.Y);

    /// <summary>
    /// A building coming out of the ground: full footprint, a share of its height.
    /// </summary>
    /// <remarks>
    /// The cheapest honest way to draw construction, and the only one that needs no second model. It
    /// distorts the geometry — a squashed roof is a squashed roof — and the distortion is exactly the read
    /// you want, because a building that is half up <em>should</em> look wrong. The alternative is a
    /// scaffolding model the pack does not have, or drawing the site as a pile of timber, which loses the
    /// one thing a player wants to see at a glance: how far along it is.
    /// </remarks>
    public static Matrix4x4 Rising(
        Vector2 position,
        float groundHeight,
        float widthMetres,
        float yaw,
        float heightShare) =>
        Matrix4x4.CreateScale(widthMetres, widthMetres * MathF.Max(0.02f, heightShare), widthMetres) *
        Matrix4x4.CreateRotationY(yaw) *
        Matrix4x4.CreateTranslation(position.X, groundHeight, position.Y);

    /// <summary>
    /// A stable pseudo-random turn for a node, so buildings do not all face the same way.
    /// </summary>
    /// <remarks>
    /// Quarter turns for a building, because these models are authored square to their tile and a
    /// building at seventeen degrees to the grid it occupies reads as a mistake rather than as variety.
    /// Anything organic gets the free rotation below.
    /// </remarks>
    public static float SquareYawOf(int id) => (id * 3 % 4) * MathF.PI * 0.5f;

    /// <summary>Any angle at all, for a tree or a heap, which nobody aligned to anything.</summary>
    public static float FreeYawOf(int id) => (id * 47 % 360) * MathF.PI / 180f;

    public void Dispose()
    {
        foreach (var model in owned) model.Dispose();
        owned.Clear();
    }
}
