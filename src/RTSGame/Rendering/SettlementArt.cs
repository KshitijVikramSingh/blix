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
        PropModel[] treesMid,
        PropModel[] treesFar,
        PropModel stumps,
        PropModel[] rocks,
        PropModel[] scatter,
        PropModel[] undergrowth,
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
        TreesMid = treesMid;
        TreesFar = treesFar;
        Stumps = stumps;
        Rocks = rocks;
        Scatter = scatter;
        Undergrowth = undergrowth;
        GrainHeap = grainHeap;
        WoodHeap = woodHeap;
        Villager = villager;
        owned.AddRange(new[] { granary, depot, fieldPlot, stumps, grainHeap, woodHeap });
        owned.AddRange(rocks);
        owned.AddRange(scatter);
        owned.AddRange(undergrowth);
        owned.AddRange(houses);
        owned.AddRange(crop);
        owned.AddRange(trees);
        owned.AddRange(treesMid);
        owned.AddRange(treesFar);
        if (villager is not null) owned.Add(villager);
    }

    /// <summary>The same species at a middling level of detail, for the band past the near one.</summary>
    public PropModel[] TreesMid { get; private init; } = Array.Empty<PropModel>();

    /// <summary>The same species again, coarsest, for the band past that.</summary>
    public PropModel[] TreesFar { get; private init; } = Array.Empty<PropModel>();

    /// <summary>Loose stone: an outcrop on scree, and the one piece of ground cover that is not alive.</summary>
    public PropModel[] Rocks { get; private init; } = Array.Empty<PropModel>();

    /// <summary>
    /// What the art staged this frame, as instances and as the triangles they actually amount to.
    /// </summary>
    /// <remarks>
    /// <b>The number the render statistics do not report.</b> A pass counter sums each draw's mesh once,
    /// which for instanced geometry is the cost of <em>one</em> of them — so a frame drawing four thousand
    /// trees of six thousand triangles each reported a couple of hundred thousand triangles and looked
    /// cheap. It was twenty-eight million. Anything that draws the same mesh many times needs the product,
    /// not the sum.
    /// </remarks>
    public (int Instances, long Triangles, long Casters) StagedLoad()
    {
        var instances = 0;
        long triangles = 0;
        long casters = 0;
        foreach (var model in owned)
        {
            var count = model.InstanceCount;
            if (count <= 0) continue;
            instances += count;
            triangles += (long)count * model.TriangleCount;
            casters += (long)count * model.CasterTriangleCount;
        }

        return (instances, triangles, casters);
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

    /// <summary>
    /// Things growing on the ground, out in the open and at the foot of a trunk.
    /// </summary>
    /// <remarks>
    /// <b>Real bushes, from Quaternius's Ultimate Nature Pack, which retires the squashed tree.</b> A tree
    /// flattened and sunk to its collar was a decent stand-in and it was still a tree — and it cost a
    /// tree's triangles to look like a shrub. These are shrubs: a metre and a bit across, a metre tall,
    /// and cheaper than the thing they replace.
    /// <para>
    /// Same artist as the buildings, which is worth more than the models. The pack names its materials
    /// <c>Green</c>, <c>DarkGreen</c>, <c>Leaves</c>, <c>Wood</c>, <c>Berry</c> — the same vocabulary the
    /// glTF pack uses — so every one of these classifies itself through the existing rules and picks up the
    /// foliage shading with no code at all.
    /// </para>
    /// <para>
    /// Split in two lists because the two jobs want different silhouettes. Out in the open a scatter wants
    /// variety and some height; at the foot of a trunk it wants low, wide things that hide a join without
    /// standing in front of the tree.
    /// </para>
    /// </remarks>
    public PropModel[] Scatter { get; }

    public PropModel[] Undergrowth { get; }

    /// <summary>
    /// <b>Stone is not scenery.</b>
    /// </summary>
    /// <remarks>
    /// A rock scatter was tried and taken out again, and the reason is worth keeping: stone is about to be a
    /// <em>resource</em>, mined from a deposit somebody chooses to work. Strewing rocks over the whole map
    /// as decoration teaches the player that a rock is nothing to look at, which is precisely the wrong
    /// lesson to teach a fortnight before rocks start mattering. It also read badly — the pack's stone is
    /// near-white against every green in the scene and its cluster model is a metre and a half across, so
    /// "incidental detail" arrived as bright objects the size of a cart.
    /// <para>
    /// The scatter is low scrub instead, drawn from the broadleaf trees already loaded — no new asset, no
    /// extra batch, and it is foliage, so it picks up the leaf shading for free. See
    /// <c>RtsGameLoop.DrawScatter</c>.
    /// </para>
    /// </remarks>

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
        var measured = new List<(PropModel Model, Bounds3 Walls)>();
        var midError = 0f;
        var farError = 0f;

        // <b>The OBJ pack, loaded the same way the glTF one is.</b> WavefrontParts splits a file by
        // material and reads each colour out of the MTL, so the two paths meet at the same shape — a
        // sequence of mesh-and-colour pairs — and everything downstream is identical, classification
        // included. Nothing here casts a shadow by default: a shadow-map draw for a tuft of grass costs
        // what a building's does and buys a smudge.
        PropModel Nature(
            string file,
            bool casts = false,
            float surface = MaterialClass.Foliage)
        {
            var path = Path.Combine(directory, "nature", file + ".obj");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"The settlement's art is missing 'nature/{file}.obj'. Models live in " +
                    "src/RTSGame/Assets/models and are copied to the output by the csproj; a missing one " +
                    "is a build or a rename, not something to draw a box for.", path);
            }

            var parts = WavefrontParts.Import(path)
                .Select(part => (part.Mesh, Tint: Classified(part.Color, part.Material, surface)))
                .ToArray();
            var bounds = parts.Select(part => part.Mesh).CombinedBounds();
            return PropModel.Create(
                device, file, parts, sceneShader, scenePipeline,
                casts ? casterShader : null, casts ? casterPipeline : null,
                PropModel.NormaliseToUnitFootprint(bounds));
        }

        // <b>The nature kit's models come in for their geometry and are coloured here by hand.</b> Its
        // materials are textured, and their textures are palettes rather than pictures — the pine's leaf
        // sheet averages to pure white and the grass's to near-white, because the colour lives in a separate
        // gradient the shader is meant to index. So averaging a texture is the wrong tool, a texture path
        // for props is a great deal of work for flat-shaded low-poly art, and this pack's twelve material
        // names are a perfectly good key.
        //
        // Which is also the rule this file already follows for the models it had: a flat colour per
        // material, authored, in linear space because that is what the shader works in. Two of these are
        // measured from the pack's own colour sheets — the dead bark's grey and the twisted bark's brown —
        // and the rest are chosen to sit in the palette the settlement already uses.
        static Vector4 KitColour(string? material, float surface) => material switch
        {
            "Bark_NormalTree" => new Vector4(0.085f, 0.058f, 0.038f, MaterialClass.Timber),
            "Bark_TwistedTree" => new Vector4(0.091f, 0.075f, 0.066f, MaterialClass.Timber),
            "Bark_DeadTree" => new Vector4(0.054f, 0.047f, 0.052f, MaterialClass.Timber),
            "Leaves_NormalTree" => new Vector4(0.075f, 0.135f, 0.040f, MaterialClass.Foliage),
            // Bluer and darker than broadleaf, which is most of what separates a conifer from an oak at
            // three hundred metres.
            "Leaves_Pine" => new Vector4(0.040f, 0.088f, 0.048f, MaterialClass.Foliage),
            // Scrub on poor ground: olive rather than green.
            "Leaves_TwistedTree" => new Vector4(0.095f, 0.100f, 0.045f, MaterialClass.Foliage),
            "Leaves" => new Vector4(0.070f, 0.130f, 0.050f, MaterialClass.Foliage),
            "Grass" => new Vector4(0.110f, 0.155f, 0.060f, MaterialClass.Foliage),
            "Flowers" => new Vector4(0.450f, 0.200f, 0.160f, MaterialClass.Foliage),
            "Mushrooms" => new Vector4(0.420f, 0.281f, 0.167f, MaterialClass.Foliage),
            "Rocks" or "PathRocks" => new Vector4(0.083f, 0.103f, 0.066f, MaterialClass.Stone),
            _ => new Vector4(0.6f, 0.6f, 0.6f, surface),
        };

        /// <remarks>
        /// <b>lod picks a level out of the cooked chain</b>, which is the whole of the distance story for
        /// this art: the models are solid low-poly geometry rather than alpha-cut cards, so they decimate —
        /// a tree goes 4,345 / 2,076 / 972 / 454 triangles once the cook is allowed to prune components —
        /// and a decimated tree still reads as a tree where an impostor reads as a shard. Levels past the
        /// end of a chain clamp to the coarsest, so a model that would not decimate simply repeats itself
        /// rather than failing.
        /// </remarks>
        // <b>casterLod is how far the shadow is allowed to fall behind the scene draw.</b> A shadow is a
        // silhouette resolved to an eleven-centimetre texel, so it survives geometry the camera would not
        // accept — and the sun's pass draws every caster in the box whether or not the camera can see it,
        // which makes it the pass a woodland actually costs. Zero means the caster shares the scene mesh.
        PropModel Kit(
            string file,
            bool casts = false,
            float surface = MaterialClass.Foliage,
            int lod = 0,
            int casterLod = 0)
        {
            var path = Path.Combine(directory, "kit", file + ".gltf");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"The nature kit is missing '{file}.gltf'. Models live in " +
                    "src/RTSGame/Assets/models/kit and are copied to the output by the csproj.", path);
            }

            var model = new GltfStaticImporter().Import(
                new AssetImportContext(AssetId.Parse(file), path));
            var worst = 0f;
            var parts = model.Primitives
                .Select(prim =>
                {
                    var mesh = AtLevel(prim.Mesh, lod, out var error);
                    worst = MathF.Max(worst, error);
                    return (Mesh: mesh, Tint: KitColour(prim.Material?.Name, surface));
                })
                .ToArray();
            if (lod == 1) midError = MathF.Max(midError, worst);
            if (lod >= 2) farError = MathF.Max(farError, worst);
            var bounds = parts.Select(part => part.Mesh).CombinedBounds();
            var bake = PropModel.NormaliseToUnitFootprint(bounds);
            // Taken from the model's own chain rather than from the scene parts, because those have already
            // been flattened to one level and no longer carry the chain to step down.
            var shadowParts = casts && casterLod > lod
                ? model.Primitives.Select(prim => AtLevel(prim.Mesh, casterLod, out _)).ToArray()
                : null;
            var built = PropModel.Create(
                device, file, parts, sceneShader, scenePipeline,
                casts ? casterShader : null, casts ? casterPipeline : null, bake, shadowParts);
            measured.Add((built, LowerExtent(parts.Select(part => part.Mesh), bounds, bake)));
            return built;
        }

        PropModel Prop(
            string file,
            bool casts = true,
            bool stretchToSquare = false,
            float surface = MaterialClass.Crafted)
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
                    Tint: Classified(
                        prim.Material?.BaseColorFactor ?? new Vector4(0.6f, 0.6f, 0.6f, 1f),
                        prim.Material?.Name,
                        surface)))
                .ToArray();
            var bounds = parts.Select(part => part.Mesh).CombinedBounds();
            var bake = stretchToSquare
                ? StretchToUnitSquare(bounds)
                : PropModel.NormaliseToUnitFootprint(bounds);
            var built = PropModel.Create(
                device, file, parts, sceneShader, scenePipeline,
                casts ? casterShader : null, casts ? casterPipeline : null, bake);
            // What anything hung on this building has to be placed against, rather than its bounding box.
            // See WallsOf: the widest part of a cottage is its roof.
            measured.Add((built, LowerExtent(parts.Select(part => part.Mesh), bounds, bake)));
            return built;
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
        //
        // <b>And stretched to the square, because the ground is square.</b> Reported twice: the barn is
        // 1.81 x 1.43, so normalised to a 7.5 m footprint it drew 7.5 x 5.93 and left a metre and a half of
        // blocked ground with nothing standing on it. A footprint the player cannot see is a footprint they
        // walk into. The cost is a 26% stretch in depth on a barn, which is a barn slightly the wrong shape
        // — much the cheaper of the two lies.
        var art = new SettlementArt(
            granary: Prop("Storage_SecondAge_Level3", stretchToSquare: true),
            depot: Prop("Storage_SecondAge_Level1", stretchToSquare: true),
            houses: new[]
            {
                Prop("Houses_SecondAge_1_Level2"),
                Prop("Houses_SecondAge_2_Level2"),
                Prop("Houses_SecondAge_3_Level2"),
            },
            fieldPlot: Prop("Farm_FirstAge_Level3", casts: false, stretchToSquare: true, surface: MaterialClass.Terrain),
            crop: new[]
            {
                Prop("Farm_SecondAge_Level1_Wheat", stretchToSquare: true, surface: MaterialClass.Crop),
                Prop("Farm_SecondAge_Level2_Wheat", stretchToSquare: true, surface: MaterialClass.Crop),
                Prop("Farm_SecondAge_Level3_Wheat", stretchToSquare: true, surface: MaterialClass.Crop),
            },
            // <b>Ordered, and the order is the meaning: broadleaf first, conifer last.</b> DrawTree picks
            // between them by what the ground under the trunk is doing rather than by the tree's id —
            // conifer on the steep and the high, broadleaf on the level — so which slot is which matters.
            // See ConiferSlot.
            // <b>Four species in ordered ranges, and the ranges are the meaning.</b> DrawTree picks a range
            // by what the ground is — see TreeKindAt — and an index inside it from the tree's id, so which
            // slot is which matters as much as it does for the scatter. Broadleaf on good level ground,
            // conifer high and steep, twisted scrub on exposed poor ground, dead in the wet bottoms.
            // <b>Three levels of the same ten species, picked by distance.</b> Not three sets of models:
            // the same cooked chain read at level 0, 2 and 3, sharing one vertex buffer each, so the middle
            // and far bands cost index lists and nothing else.
            treesMid: new[]
            {
                Kit("CommonTree_1", casts: true, lod: 1, casterLod: 3), Kit("CommonTree_2", casts: true, lod: 1, casterLod: 3),
                Kit("CommonTree_3", casts: true, lod: 1, casterLod: 3), Kit("Pine_1", casts: true, lod: 1, casterLod: 3),
                Kit("Pine_2", casts: true, lod: 1, casterLod: 3), Kit("Pine_3", casts: true, lod: 1, casterLod: 3),
                Kit("TwistedTree_1", casts: true, lod: 1, casterLod: 3), Kit("TwistedTree_2", casts: true, lod: 1, casterLod: 3),
                Kit("DeadTree_1", casts: true, lod: 1, casterLod: 3), Kit("DeadTree_2", casts: true, lod: 1, casterLod: 3),
            },
            treesFar: new[]
            {
                // <b>The far band does not cast.</b> It begins past ninety-five metres and the haze begins
                // at about seventy-seven, so its shadows fall on ground the fog has already taken — and it
                // is four thousand trees, which is most of the sun's pass for a contribution nobody can
                // see. The near and middle bands cast their own geometry, which is already decimated, so
                // the shadow map gets the same level of detail the scene does for nothing.
                Kit("CommonTree_1", casts: true, lod: 2, casterLod: 3), Kit("CommonTree_2", casts: true, lod: 2, casterLod: 3),
                Kit("CommonTree_3", casts: true, lod: 2, casterLod: 3), Kit("Pine_1", casts: true, lod: 2, casterLod: 3),
                Kit("Pine_2", casts: true, lod: 2, casterLod: 3), Kit("Pine_3", casts: true, lod: 2, casterLod: 3),
                Kit("TwistedTree_1", casts: true, lod: 2, casterLod: 3), Kit("TwistedTree_2", casts: true, lod: 2, casterLod: 3),
                Kit("DeadTree_1", casts: true, lod: 2, casterLod: 3), Kit("DeadTree_2", casts: true, lod: 2, casterLod: 3),
            },
            trees: new[]
            {
                // <b>Each level casts its own shadow.</b> The blob substitution that stood in for this is
                // gone: a tier already costs what its own level of detail costs, so the sun's pass gets the
                // decimated geometry for free and there is nothing left for a stand-in to save.
                Kit("CommonTree_1", casts: true, casterLod: 2),
                Kit("CommonTree_2", casts: true, casterLod: 2),
                Kit("CommonTree_3", casts: true, casterLod: 2),
                Kit("Pine_1", casts: true, casterLod: 2),
                Kit("Pine_2", casts: true, casterLod: 2),
                Kit("Pine_3", casts: true, casterLod: 2),
                Kit("TwistedTree_1", casts: true, casterLod: 2),
                Kit("TwistedTree_2", casts: true, casterLod: 2),
                Kit("DeadTree_1", casts: true, casterLod: 2),
                Kit("DeadTree_2", casts: true, casterLod: 2),
            },
            // A real stump, at last: Resource_Tree_Group_Cut was a cluster of cut trunks standing in for one.
            stumps: Nature("TreeStump", casts: false, surface: MaterialClass.Timber),
            rocks: new[]
            {
                Kit("Rock_Medium_1", casts: true, surface: MaterialClass.Stone),
                Kit("Rock_Medium_2", casts: true, surface: MaterialClass.Stone),
            },
            // <b>Grass, not bushes.</b> The round shrubs are out of the open scatter entirely: at any
            // density they read as objects placed on a lawn rather than as ground cover, and a map dotted
            // evenly with them looks arranged. They still do the job they are good at, which is hiding the
            // foot of a trunk — see Undergrowth.
            //
            // Ordered, because DrawScatter picks by index and the meaning of each slot is the point: the
            // short grass is the base layer that goes everywhere in patches, the tall grass thickens in
            // woodland, and the flowers are rare and only in the open.
            // <b>Ordered, and every slot means something to DrawScatter.</b> Two grass species rather than
            // one, because that is what makes one stretch of country look unlike another: the common grass
            // is pasture and meadow, the wispy is moor and poor ground. Then clover for damp bottoms, a
            // fern for shade, flowers for open meadow, and stone for scree — which is ground cover too,
            // where nothing will grow.
            scatter: new[]
            {
                Kit("Grass_Common_Short"),
                Kit("Grass_Common_Tall"),
                Kit("Grass_Wispy_Short"),
                Kit("Grass_Wispy_Tall"),
                Kit("Clover_1"),
                Kit("Fern_1"),
                Kit("Flower_3_Group"),
                Kit("Flower_4_Group"),
                Kit("Pebble_Round_1", surface: MaterialClass.Stone),
                Kit("Pebble_Square_1", surface: MaterialClass.Stone),
            },
            undergrowth: new[]
            {
                Kit("Bush_Common"),
                Kit("Fern_1"),
                Kit("Plant_1"),
                Kit("Plant_7"),
                Kit("Bush_Common_Flowers"),
                Kit("Mushroom_Common"),
            },

            // <b>One crate, not a stack of them, and the reason is the normalisation.</b> Props are baked to
            // a unit footprint, so a model gets its height from its own proportions — and a stack of crates
            // is 0.12 m across and 0.25 m tall, better than twice as tall as it is wide. A heap of forty
            // units asks for two metres across and therefore got four metres of crates towering over the
            // houses. A single crate is very nearly cubic, so a metre across is a metre tall.
            grainHeap: Prop("Crate", surface: MaterialClass.Timber),
            woodHeap: Prop("Logs", surface: MaterialClass.Timber),
            villager: LoadVillager(device, directory, sceneShader, scenePipeline, casterShader, casterPipeline));
        foreach (var (model, extent) in measured) art.walls[model] = extent;
        art.MidError = midError;
        art.FarError = farError;
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
            new[] { (mesh, new Vector4(0.33f, 0.25f, 0.19f, MaterialClass.Body)) },
            sceneShader,
            scenePipeline,
            casterShader,
            casterPipeline,
            bake);
    }

    /// <summary>
    /// What kind of surface a primitive is, written into the fourth channel of its colour.
    /// </summary>
    /// <remarks>
    /// <b>The missing half of the art direction, and it costs no memory at all.</b> Every surface in the
    /// scene was shaded by one rule — <c>tint × (ambient + sun × shadow)</c> — so plaster, roof tile, timber,
    /// leaf, wheat and earth all responded identically and the whole weight of looking like anything fell on
    /// the base colour. A shader cannot shade a leaf like a leaf if it does not know which of its fragments
    /// are leaves.
    /// <para>
    /// Carried in alpha because an opaque pass has no use for alpha, and because that keeps the change out
    /// of <c>InstanceData</c>: the geometry primitive still knows only about a matrix and four floats, and
    /// what the fourth one means is this game's decision. Extending the shared struct would have put a
    /// material system inside a layer whose whole rule is that it owns geometry and nothing else.
    /// </para>
    /// <para>
    /// Classified from the glTF material's own name, which the pack supplies and which is exactly the
    /// vocabulary wanted — <c>Walls</c>, <c>Wood</c>, <c>Stone</c>, <c>Fabric</c>, <c>Leaves</c>. So a barn's
    /// plaster and its beams part company without anybody labelling them by hand, and a model added
    /// tomorrow classifies itself.
    /// </para>
    /// </remarks>
    internal static class MaterialClass
    {
        /// <summary>Ground: land, and the only thing that wants macro variation across the map.</summary>
        internal const float Terrain = 0.05f;

        /// <summary>Anything built, and the default: crisper, with a little sheen on the top faces.</summary>
        internal const float Crafted = 0.15f;

        /// <summary>Plaster and render — matte, bright, and the lightest thing in a village.</summary>
        internal const float Plaster = 0.25f;

        /// <summary>Roof tile — saturated, and the surface a settlement is recognised by from above.</summary>
        internal const float Roof = 0.35f;

        /// <summary>Sawn and hewn timber, which is most of what a settlement is made of.</summary>
        internal const float Timber = 0.45f;

        /// <summary>Stone: cooler, flatter, the least interesting light of anything here.</summary>
        internal const float Stone = 0.55f;

        /// <summary>Leaf: soft, translucent, and most of the screen.</summary>
        internal const float Foliage = 0.65f;

        /// <summary>Standing crop, which is foliage that catches light along a row.</summary>
        internal const float Crop = 0.75f;

        /// <summary>A person. Kept separate because people are read as silhouettes, not as surfaces.</summary>
        internal const float Body = 0.85f;

        /// <summary>
        /// The surface of water: a translucent sheet at a level, over a bed that shows through it.
        /// </summary>
        /// <remarks>
        /// <b>Above the run rather than inside it, and that is a decision about churn.</b> Every tenth from
        /// 0.05 to 0.95 was already spoken for, and the channel is a float in a storage buffer rather than a
        /// normalised colour — so there is room above one. Renumbering the ten below to make space would have
        /// touched every constant in this table, and the shader's copy of it, for the sake of one addition.
        /// </remarks>
        internal const float Water = 1.05f;

        /// <summary>Anything that makes its own light: a lit window, a lantern, embers at a work site.</summary>
        /// <remarks>
        /// The only class the sun has no opinion about. Shaded by nothing, lit by nothing, and scaled by how
        /// far into the night it is — see <c>kEmber</c> in <c>Shaders/materials.glsl</c>.
        /// </remarks>
        internal const float Ember = 0.95f;
    }

    /// <summary>
    /// Puts a material class in a colour's fourth channel, read off the glTF material's name.
    /// </summary>
    /// <remarks>
    /// Substring matching on purpose. The pack names materials <c>Wood_Light</c>, <c>Stone_Light</c>,
    /// <c>Walls</c>, <c>Main</c> — variants of a handful of ideas — and a lookup table of exact names would
    /// need editing every time a model arrives, which is the same as not having one. Anything unrecognised
    /// falls back to whatever the model asked for, so a new prop is never worse than it was.
    /// </remarks>
    private static Vector4 Classified(Vector4 tint, string? material, float fallback)
    {
        var name = material?.ToLowerInvariant() ?? string.Empty;
        var surface = name switch
        {
            // The pack calls a canopy "Green" and a trunk "Wood", so a tree splits into leaf and timber for
            // free — which is the whole argument for classifying off the artist's own names rather than off a
            // table somebody has to maintain.
            var n when n.Contains("leaf") || n.Contains("leaves") || n.Contains("foliage") ||
                       n.Contains("green") => MaterialClass.Foliage,
            var n when n.Contains("wheat") || n.Contains("crop") => MaterialClass.Crop,
            var n when n.Contains("wall") || n.Contains("plaster") => MaterialClass.Plaster,
            var n when n.Contains("roof") || n.Contains("tile") || n.Contains("main") => MaterialClass.Roof,
            var n when n.Contains("wood") || n.Contains("timber") || n.Contains("log") => MaterialClass.Timber,
            var n when n.Contains("stone") || n.Contains("rock") || n.Contains("metal") => MaterialClass.Stone,
            var n when n.Contains("dirt") || n.Contains("soil") || n.Contains("ground") => MaterialClass.Terrain,
            _ => fallback,
        };
        return new Vector4(tint.X, tint.Y, tint.Z, surface);
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

    /// <summary>
    /// One level out of a cooked LOD chain, as a mesh in its own right.
    /// </summary>
    /// <remarks>
    /// All levels index the same vertex buffer, so a coarser level is a different index list over the same
    /// vertices — which is why a decimated model costs nothing extra in memory and why building one is a
    /// record copy rather than a re-import. The chain rides along in <c>MeshData.Lods</c> whenever a cooked
    /// .blixmesh sits beside the glTF; without one there is a single level and every request clamps to it.
    /// </remarks>
    private static MeshData AtLevel(MeshData mesh, int lod, out float error)
    {
        error = 0f;
        if (lod <= 0 || mesh.Lods is not { Count: > 1 } chain) return mesh;
        var level = chain[Math.Min(lod, chain.Count - 1)];
        error = level.Error;
        return mesh with
        {
            Indices = level.Indices16 ?? Array.Empty<ushort>(),
            Indices32 = level.Indices32,
            Lods = null,
        };
    }

    /// <summary>
    /// The world-space geometric error of the mid and far tree levels, in metres.
    /// </summary>
    /// <remarks>
    /// <b>What the cook measured when it decimated, which is what lets the runtime choose by pixels rather
    /// than by a magic distance.</b> A level's error is how far its surface can be from the original, so
    /// projecting it — <c>error × viewportHeight / (2 × distance × tan(fov/2))</c> — gives the size of the
    /// mistake on screen. Choosing the coarsest level whose mistake is under a pixel or two is correct at
    /// every zoom and every window size by construction, where a hand-tuned radius is correct at one.
    /// <para>
    /// The largest error across the species, because the tiers are shared arrays and a band has to be safe
    /// for the worst model in it.
    /// </para>
    /// </remarks>
    public float MidError { get; private set; }

    public float FarError { get; private set; }

    /// <summary>
    /// The footprint of a model's <em>walls</em>, which is not the footprint of its roof.
    /// </summary>
    /// <remarks>
    /// <b>Written to explain lit windows that looked like they were floating, and it disproved its own
    /// hypothesis — which is the reason to measure rather than reason about a model.</b> The theory was
    /// overhanging eaves: a cottage's widest point would be its roof, so a window at the bounding box would
    /// hang under the overhang with nothing behind it. Measured, the overhang is <b>zero</b> — the walls
    /// reach 0.47 of the footprint where the roof reaches 0.47, and 0.45 against 0.50 on the other axis.
    /// The floating was a different fault entirely (an emissive bright enough to clip spreading through the
    /// MSAA resolve, so a 40 cm box read as a metre and a half of white card).
    /// <para>
    /// Kept anyway, and not out of sentiment: it is five centimetres per metre of footprint on one axis of
    /// the cottage, it is the difference between flush and floating at this scale, and the next pack of
    /// models will not have the same answer. Anything that puts something <em>on</em> a building asks for
    /// these; the full bounds remain the right answer for the ridge a chimney comes out of.
    /// </para>
    /// </remarks>
    public Bounds3 WallsOf(PropModel model) =>
        walls.TryGetValue(model, out var measured) ? measured : model.Bounds;

    private readonly Dictionary<PropModel, Bounds3> walls = new();

    /// <summary>
    /// The extent of whatever sits in the bottom share of a model, in the model's own space.
    /// </summary>
    /// <remarks>
    /// A third of the height, which is under the eaves of everything in this pack and above the plinth of
    /// all of it. Measured off the source geometry and then carried through the fitting transform — legal
    /// because the fit is a translation and a uniform scale, so it takes an axis-aligned box to an
    /// axis-aligned box, and it saves transforming every vertex a second time.
    /// </remarks>
    private static Bounds3 LowerExtent(IEnumerable<MeshData> meshes, Bounds3 whole, Matrix4x4 fit)
    {
        var cut = whole.Min.Y + (whole.Max.Y - whole.Min.Y) * 0.34f;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in meshes)
        {
            var stride = mesh.Layout.Stride;
            if (stride < 12) continue;
            for (var v = 0; v < mesh.VertexCount; v++)
            {
                var at = v * stride;
                var position = new Vector3(
                    BitConverter.ToSingle(mesh.VertexBytes, at),
                    BitConverter.ToSingle(mesh.VertexBytes, at + 4),
                    BitConverter.ToSingle(mesh.VertexBytes, at + 8));
                if (position.Y > cut) continue;
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }
        }

        if (min.X > max.X) return new Bounds3(
            Vector3.Transform(whole.Min, fit), Vector3.Transform(whole.Max, fit));
        return new Bounds3(Vector3.Transform(min, fit), Vector3.Transform(max, fit));
    }

    /// <summary>
    /// The cottage a given house is drawn as, which is decided by its id and nothing else.
    /// </summary>
    /// <remarks>
    /// One owner for the rule, because there are now four callers of it — the house itself, the site it
    /// rises on, its lit windows and its chimney — and the last two need the model in order to ask it how
    /// big it is. Picked by id rather than at random so it survives a save and a reload as the same village.
    /// </remarks>
    public PropModel HouseFor(int id) => Houses[id % Houses.Length];

    /// <summary>
    /// Which way a building's front faces, given the turn it was placed with.
    /// </summary>
    /// <remarks>
    /// One owner for the sign, because there are now three callers that need to agree about which wall is
    /// the front — the lit windows, the pool of light they cast, and the chimney the smoke comes out of —
    /// and three copies of a rotation are three chances to put the lantern on the back of the house. These
    /// matrices are the row-vector System.Numerics form, so local +x lands on (cos, −sin) in world x/z.
    /// </remarks>
    public static Vector2 FaceDirection(float yaw) => new(MathF.Cos(yaw), -MathF.Sin(yaw));

    public void Dispose()
    {
        foreach (var model in owned) model.Dispose();
        owned.Clear();
    }
}
