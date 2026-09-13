using System.Numerics;
using Blix.Assets;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;

namespace RTSGame.Control;

/// <summary>
/// What is selected, what is under the pointer, and which key does something about it.
/// </summary>
/// <remarks>
/// <b>Contextual rather than a key list.</b> There is a twenty-line reference printed to the console at
/// startup and it is no use at all while playing: the question a player has is never "what are all the
/// keys", it is "I have clicked this thing, now what" — and the answer depends on what is selected and what
/// is under the cursor. So the prompts are computed from exactly those two, and only the ones that would
/// currently <em>do</em> something are shown.
/// <para>
/// Which also makes it a place where a mechanic can announce itself. A field says what the crop cycle
/// thinks of it, a site says whether it is short of timber or short of hands, and a tree says how much wood
/// is left in it — each in the words the simulation already uses, because those were written to be read.
/// The alternative is a player who has to infer three stages of economy from the colour of the ground.
/// </para>
/// <para>
/// Terse on purpose. The only font in the repo is a heavy display face, which is legible at a glance and
/// ruinous for paragraphs, so every line here is a handful of words. That is the right constraint anyway:
/// a heads-up display that needs reading is not heads-up.
/// </para>
/// </remarks>
internal sealed class SettlementHud : IDisposable
{
    private readonly SpriteBatch batch;
    private readonly Font? font;
    private TextureHandle plate = new(-1);
    private readonly List<(string Text, GraphicsColor Colour)> lines = new();

    private static readonly GraphicsColor Heading = new(0.96f, 0.93f, 0.82f, 1f);
    private static readonly GraphicsColor Body = new(0.86f, 0.90f, 0.94f, 1f);
    private static readonly GraphicsColor Action = new(0.98f, 0.82f, 0.34f, 1f);
    private static readonly GraphicsColor Warning = new(0.97f, 0.51f, 0.36f, 1f);
    private static readonly GraphicsColor Shadow = new(0.02f, 0.03f, 0.04f, 0.82f);

    private SettlementHud(SpriteBatch batch, Font? font)
    {
        this.batch = batch;
        this.font = font;
    }

    /// <summary>Whether text will actually appear. False if the font could not be loaded.</summary>
    public bool HasFont => font is not null;

    /// <summary>
    /// Loads the HUD, or returns one that draws nothing if the font is missing.
    /// </summary>
    /// <remarks>
    /// Best-effort, unlike the settlement's art. A missing model means half the settlement silently
    /// becomes boxes and nobody notices for a week, which is worth failing over; a missing font means the
    /// prompts are absent and it is obvious on the first frame.
    /// </remarks>
    public static SettlementHud Load(VulkanGraphicsDevice device)
    {
        var batch = new SpriteBatch(device);
        try
        {
            var assets = new AssetDatabase()
                .RegisterImporter(new FontImporter())
                .LoadManifest(Path.Combine(AppContext.BaseDirectory, "Assets", "manifest.json"));
            return new SettlementHud(batch, Font.Upload(device, assets.Load<FontData>(AssetId.Parse("fonts/hud"))));
        }
        catch (Exception failure)
        {
            Console.WriteLine($"  no HUD font, on-screen prompts are off: {failure.Message}");
            return new SettlementHud(batch, null);
        }
    }

    /// <summary>
    /// Draws the state of the settlement, of the selection, and of whatever is being pointed at.
    /// </summary>
    public void Draw(
        RenderPassBuilder pass,
        TextureHandle pixel,
        SimulationWorld world,
        IReadOnlyCollection<AgentId> selected,
        Vector2 pointer,
        bool pointerOnTerrain,
        bool additive,
        NodeId? routeSource,
        string? raid,
        string? light,
        string? geometry,
        string? lab,
        string? crews,
        NodeId pointed,
        NodeId picked,
        int width,
        int height)
    {
        if (font is null) return;
        plate = pixel;
        var ortho = GraphicsMatrices.CreateOrthographicOffCenterVulkan(0f, width, height, 0f, -1f, 1f);
        batch.Begin(ortho);

        var size = MathF.Max(11f, height * 0.019f);
        var step = size * 1.45f;
        var pad = size * 1.2f;

        // Top left: the two questions the low-attention mode is about — what time of year is it, and how
        // long will the stores last. Seasons rather than stock levels, because a number tells you a number
        // and seasons-until-empty tells you whether to do something.
        lines.Clear();
        var date = world.Date;
        // The light's own name on the date line, because the season is now readable from the light and the
        // time of day is not readable from anything else — a dark frame should say "dusk" rather than leave
        // you wondering whether something is broken.
        var when = light is null ? string.Empty : $" · {light.ToUpperInvariant()}";
        lines.Add((
            $"YEAR {date.Year + 1} · {date.Season.ToString().ToUpperInvariant()} · DAY {date.Day}{when}",
            Heading));
        foreach (var resource in Resources.All)
        {
            var outlook = world.Economy.Outlook(resource, world.Nodes, world.Agents, date.Season);
            var lasts = float.IsPositiveInfinity(outlook.Seasons)
                ? "growing"
                : $"{outlook.Seasons:F1} seasons";
            var colour = outlook.Seasons < 1f ? Warning : Body;
            lines.Add(($"{resource.ToString().ToUpperInvariant()} {outlook.Stored:N0} · {lasts}", colour));
        }

        // <b>Mine, not everybody's.</b> This counted every live body in the world, which is fine right up
        // until something hostile is standing on the map — and then the settlement appears to have eighty-two
        // people when it has twenty-six and there are fifty-six raiders walking in. A headcount that includes
        // the people robbing you is not a headcount.
        var people = 0;
        var spare = 0;
        foreach (ref readonly var body in world.Agents.All)
        {
            if (!body.IsAlive || body.Faction.Value != 0) continue;
            people++;
            if (body.Role != AgentRole.Villager) continue;
            if (!body.Jobs.HasAssignment && !body.Jobs.IsInterrupted) spare++;
        }

        lines.Add((
            $"{people} PEOPLE · {spare} SPARE · {world.Economy.Readiness * 100f:F0}% FED",
            spare > 0 ? Action : Body));
        if (raid is not null)
        {
            // Top of the screen, not the bottom, because it is the one thing that is worth interrupting
            // whatever you were doing — and in red when it is happening rather than pending.
            lines.Add((raid.ToUpperInvariant(), raid.Contains("RAIDERS") ? Warning : Body));

            // What the settlement decided to do about it, which is otherwise invisible: nobody is ordered
            // to defend, so without this the only way to tell a defence from a crowd is to watch where
            // people walk. "Left it to somebody closer" is the answer worth showing — it is the one that
            // says the settlement is answering the raid with a party rather than with everybody.
            var threat = world.Threat;
            var stowing = world.PuttingDownCount;
            if (threat.Standing + threat.Fleeing + threat.Surplus + stowing > 0)
            {
                lines.Add((
                    $"{threat.Standing} DEFENDING · {stowing} PUTTING LOADS DOWN · " +
                    $"{threat.Fleeing} RUNNING · {threat.Surplus} LEFT IT TO SOMEBODY CLOSER",
                    threat.Standing > 0 ? Action : Warning));
            }
        }

        // <b>The distances that are supposed to agree, while the overlay is up.</b> Four numbers picked
        // independently — how far the camera is, how far trees are drawn, how wide the shadow box is, where
        // fog starts — and "geometries that should tie together don't" cannot be checked without seeing
        // them next to each other.
        if (geometry is not null) lines.Add((geometry.ToUpperInvariant(), Body));
        // The crews, if there are any. Slot and size only: the panel's job is to say a set exists and is
        // still that size after a raid, not to list it — that is what recalling it is for.
        if (crews is not null) lines.Add(($"CREWS · {crews}", Body));
        // <b>The map lab's whole state, on screen, because a lab you have to read a terminal for is not a
        // lab.</b> Every one of its controls worked and none of them appeared to: the region, the archetype
        // and the pick were printed to stdout, which on a windowed run is a file nobody is watching. Reported
        // as "I don't think I can toggle these", and the keys were fine — the feedback was missing.
        if (lab is not null)
        {
            foreach (var line in lab.Split('\n'))
            {
                if (line.Length > 0) lines.Add((line.ToUpperInvariant(), Action));
            }
        }

        Block(size, new Vector2(pad, pad), step);

        // Bottom left: what is selected, what is under the pointer, and what to press.
        lines.Clear();
        Describe(world, selected);
        // <b>Given, not picked again.</b> This used to run its own NodeAt with its own radius, which was fine
        // while the panel was the only thing that cared. It stopped being fine the moment a marker was drawn on
        // the ground: two picks means two answers, and a highlight under one building while the panel describes
        // another is the failure that would follow. The loop decides; both readers agree by construction.
        var hovered = pointerOnTerrain ? pointed : NodeId.None;
        // What is held outranks what the pointer happens to be crossing — the panel should not change under
        // you while you move the mouse toward a key.
        var described = world.Nodes.Contains(picked) ? picked : hovered;
        if (world.Nodes.Contains(described)) DescribeNode(world, described);
        else if (pointerOnTerrain) DescribeGround(world, pointer);
        // The settlement's prompts are about a settlement. In the lab there is not one, and offering to
        // build a granary is worse than offering nothing.
        if (lab is null) Prompts(world, selected, described, additive, routeSource);

        var blockHeight = lines.Count * step;
        Block(size, new Vector2(pad, height - pad - blockHeight), step);
        batch.End(pass);
    }

    /// <summary>How near the pointer counts as pointing at a node. Matches the route key's reach.</summary>


    private void Describe(SimulationWorld world, IReadOnlyCollection<AgentId> selected)
    {
        if (selected.Count == 0)
        {
            lines.Add(("NOTHING SELECTED", Body));
            return;
        }

        var farming = 0;
        var cutting = 0;
        var quarrying = 0;
        var carting = 0;
        var building = 0;
        var training = 0;
        var militia = 0;
        var collectingMaterials = 0;
        var carryingMaterials = 0;
        var posted = 0;
        var delivering = 0;
        var unable = 0;
        var routeCargo = new int[Resources.All.Length];
        var idle = 0;
        var carried = 0;
        var carts = 0;
        foreach (var id in selected)
        {
            if (!world.Agents.Contains(id)) continue;
            ref readonly var body = ref world.Agents.Get(id);
            if (!body.IsAlive) continue;
            carried += body.Jobs.CarriedUnits;
            if (body.Role == AgentRole.Militia) militia++;
            if (body.HasCart) carts++;
            if (body.Jobs.CannotReachWork) unable++;
            switch (body.Jobs.Assignment.Kind)
            {
                case AssignmentKind.Work when body.Jobs.Assignment.Cargo == Resource.Wood:
                    cutting++;
                    break;
                case AssignmentKind.Work when body.Jobs.Assignment.Cargo == Resource.Stone:
                    quarrying++;
                    break;
                case AssignmentKind.Work when body.Jobs.Assignment.Cargo == Resource.Grain:
                    farming++;
                    break;
                case AssignmentKind.Haul:
                case AssignmentKind.Carry:
                    carting++;
                    routeCargo[(int)body.Jobs.Assignment.Cargo]++;
                    break;
                case AssignmentKind.Hold when
                    world.Nodes.Contains(body.Jobs.Assignment.Source) &&
                    world.Nodes.Get(body.Jobs.Assignment.Source).HasStructuralProject:
                    building++;
                    break;
                case AssignmentKind.Build:
                    building++;
                    if (body.Jobs.CarriedUnits > 0) carryingMaterials++;
                    else if (body.Jobs.ReservedUnits > 0) collectingMaterials++;
                    break;
                case AssignmentKind.Train:
                    training++;
                    if (body.Jobs.CarriedUnits > 0) carryingMaterials++;
                    else if (body.Jobs.ReservedUnits > 0) collectingMaterials++;
                    break;
                case AssignmentKind.Hold:
                    posted++;
                    break;
                default:
                    idle++;
                    break;
            }
            if (body.Jobs.Assignment.Kind == AssignmentKind.Work && body.Jobs.Leg % 2 != 0) delivering++;
        }

        var doing = new List<string>();
        if (farming > 0) doing.Add($"{farming} farming");
        if (cutting > 0) doing.Add($"{cutting} cutting");
        if (quarrying > 0) doing.Add($"{quarrying} quarrying");
        if (carting > 0) doing.Add($"{carting} carting");
        if (building > 0) doing.Add($"{building} building");
        if (training > 0) doing.Add($"{training} training");
        if (militia > 0) doing.Add($"{militia} militia");
        if (posted > 0) doing.Add($"{posted} posted");
        if (idle > 0) doing.Add($"{idle} idle");
        var summary = $"{selected.Count} SELECTED";
        if (doing.Count > 0) summary += " · " + string.Join(", ", doing);
        if (carts > 0) summary += $" · {carts} with carts";
        var nextLoads = new List<string>();
        foreach (var resource in Resources.All)
        {
            if (routeCargo[(int)resource] > 0)
            {
                nextLoads.Add($"{routeCargo[(int)resource]} {resource.ToString().ToLowerInvariant()}");
            }
        }
        if (nextLoads.Count > 0) summary += $" · next {string.Join(", ", nextLoads)}";
        if (delivering > 0) summary += $" · {delivering} delivering";
        if (collectingMaterials > 0) summary += $" · {collectingMaterials} collecting materials";
        if (carryingMaterials > 0) summary += $" · {carryingMaterials} carrying to site";
        if (unable > 0) summary += $" · {unable} cannot reach work";
        if (carried > 0) summary += $" · carrying {carried}";
        lines.Add((summary, Heading));
    }

    /// <summary>
    /// What the thing under the pointer would say about itself.
    /// </summary>
    /// <remarks>
    /// In the simulation's own words — <see cref="CropCycle.StateOf"/>, <see cref="Construction.StateOf"/>,
    /// <see cref="Woodland.StateOf"/> — because those were written to be read by somebody and until now the
    /// only thing reading them was a console table nobody sees while playing.
    /// </remarks>
    private void DescribeNode(SimulationWorld world, NodeId id)
    {
        ref readonly var node = ref world.Nodes.Get(id);
        var name = node.Kind switch
        {
            NodeKind.Granary => "STOREHOUSE",
            NodeKind.ForwardDepot => "CAMP",
            NodeKind.Farm => "FIELD",
            NodeKind.House => "HOUSE",
            NodeKind.Tree => "TREE",
            NodeKind.Outcrop => "OUTCROP",
            NodeKind.PalisadeWall => "PALISADE WALL",
            NodeKind.StoneWall => "STONE WALL",
            NodeKind.Barracks => "BARRACKS",
            _ => "HEAP",
        };

        if (node.HasStructuralProject)
        {
            var siteAssigned = AssignedTo(world, id);
            var projectName = node.IsUnderConstruction
                ? $"{name} SITE"
                : node.StructuralProject == StructuralProjectKind.Repair
                    ? $"{name} REPAIR"
                    : $"{name} → {node.StructuralTarget.ToString().ToUpperInvariant()}";
            lines.Add((
                $"{projectName} · {StructuralProjects.StateOf(in node)} · " +
                $"{siteAssigned} assigned, {node.Hands} working",
                node.WantsMaterials ? Warning : Action));
            var cost = StructuralProjects.CostFor(in node);
            foreach (var resource in Resources.All)
            {
                if (cost[resource] <= 0) continue;
                var incoming = IncomingTo(world, id, resource);
                var label = resource == Resource.Wood ? "TIMBER" : resource.ToString().ToUpperInvariant();
                lines.Add((
                    $"{label} · {StructuralProjects.ConsumedFor(in node, resource)}/{cost[resource]} incorporated · " +
                    $"{node.Stock[resource]} on site · {incoming} incoming · " +
                    $"{Math.Max(0, node.Wanted(resource) - incoming)} unclaimed",
                    node.Wanted(resource) > incoming ? Warning : Body));
            }
            return;
        }

        var says = node.Kind switch
        {
            NodeKind.Farm =>
                $"{node.Fertility * 100f:F0}% fertility, {CropCycle.StateOf(in node, world.Date.Season)}",
            NodeKind.Tree => $"{Woodland.StateOf(in node)}, {node.Stock.Wood} wood",
            NodeKind.Outcrop => $"{Quarrying.StateOf(in node)}, {node.Stock.Stone} stone",
            NodeKind.House => $"{node.Occupants}/{node.Occupancy} living here" +
                              (node.Privation > 0.5f ? " · GOING HUNGRY" : string.Empty),
            NodeKind.Barracks => BarracksState(world, id),
            // <b>Total, not two named fields.</b> Both of these read the resources rather than listing them,
            // because a panel that names the resources it knows about is a panel that silently stops mentioning
            // the next one — a stone pile read "0 lying on the ground" and a store holding nothing but stone
            // read "0 grain, 0 wood". The same shape as the ledger sweeps in Resource.cs, in the layer where
            // being wrong is merely invisible rather than unbalanced.
            NodeKind.Pile => $"{node.Stock.Total} lying on the ground",
            _ => Held(in node),
        };

        var assigned = AssignedTo(world, id);
        var hands = node.IsWorkSite
            ? $" · {assigned} assigned, {node.Hands} working"
            : string.Empty;
        var delivery = DeliversTo(world, in node);
        var hungry = node.IsSink && node.Privation > 0.5f;
        var condition = node.IsStructure
            ? $" · condition {node.Condition:F0}/{node.MaxCondition:F0}"
            : string.Empty;
        lines.Add(($"{name} · {says}{condition}{hands}{delivery}", hungry ? Warning : Body));
    }

    /// <summary>The country under the pointer before a field is committed to it.</summary>
    private void DescribeGround(SimulationWorld world, Vector2 pointer)
    {
        var fertility = world.Terrain.Soil?.FertilityAt(pointer) ?? 1f;
        var surface = world.Terrain.SampleSurface(pointer).ToString().ToUpperInvariant();
        lines.Add(($"GROUND · {surface} · FIELD FERTILITY {fertility * 100f:F0}%", Body));
    }

    private static int AssignedTo(SimulationWorld world, NodeId site)
    {
        var assigned = 0;
        foreach (ref readonly var body in world.Agents.All)
        {
            if (!body.IsAlive) continue;
            if (body.Jobs.Assignment.Kind == AssignmentKind.Build && body.Jobs.Project == site)
            {
                assigned++;
                continue;
            }

            if (body.Jobs.Assignment.Source != site) continue;
            if (body.Jobs.Assignment.Kind is AssignmentKind.Work or AssignmentKind.Hold) assigned++;
        }
        return assigned;
    }

    private static int IncomingTo(SimulationWorld world, NodeId site, Resource resource)
    {
        var incoming = 0;
        foreach (ref readonly var body in world.Agents.All)
        {
            if (!body.IsAlive) continue;
            if (body.Jobs.Assignment.Kind == AssignmentKind.Build && body.Jobs.Project == site)
            {
                incoming += body.Jobs.CarriedUnits > 0 && body.Jobs.Carrying == resource
                    ? body.Jobs.CarriedUnits
                    : body.Jobs.Assignment.Cargo == resource ? body.Jobs.ReservedUnits : 0;
                continue;
            }

            if (!body.Jobs.Assignment.MovesCargo || body.Jobs.Assignment.Sink != site ||
                body.Jobs.Assignment.Cargo != resource)
            {
                continue;
            }

            incoming += body.Jobs.CarriedUnits > 0
                ? body.Jobs.CarriedUnits
                : Math.Min(
                    body.CarryCapacity,
                    world.Nodes.Contains(body.Jobs.Assignment.Source)
                        ? world.Nodes.Get(body.Jobs.Assignment.Source).Stock[resource]
                        : 0);
        }

        return incoming;
    }

    private static string DeliversTo(SimulationWorld world, in EconomyNode node)
    {
        var resource = node.Kind switch
        {
            NodeKind.Farm => Resource.Grain,
            NodeKind.Tree => Resource.Wood,
            NodeKind.Outcrop => Resource.Stone,
            _ => (Resource?)null,
        };
        if (resource is not { } cargo) return string.Empty;
        var store = EconomySystem.NearestStoreWithRoom(world.Nodes, cargo, node.Faction, node.Position);
        return world.Nodes.Contains(store)
            ? $" · delivers to {world.Nodes.Get(store).Kind.ToString().ToLowerInvariant()}"
            : " · no store has room";
    }

    private static string BarracksState(SimulationWorld world, NodeId barracks)
    {
        var trainees = 0;
        var progress = 0f;
        foreach (ref readonly var body in world.Agents.All)
        {
            if (!body.IsAlive || body.Jobs.Assignment.Kind != AssignmentKind.Train ||
                body.Jobs.Project != barracks)
            {
                continue;
            }

            trainees++;
            progress += body.Jobs.TrainingWork;
        }

        ref readonly var node = ref world.Nodes.Get(barracks);
        var cost = MilitiaTraining.Cost;
        if (trainees == 0)
        {
            return $"ready · militia costs {cost.Wood} timber + {cost.Stone} stone + " +
                   $"{MilitiaTraining.Seconds:F0} s · {node.Stock.Wood} timber, {node.Stock.Stone} stone on hand";
        }

        var state = $"{trainees} training · {progress / trainees:F0}/{MilitiaTraining.Seconds:F0} s average";
        return $"{state} · equipment {node.Stock.Wood}/{trainees * cost.Wood} timber, " +
               $"{node.Stock.Stone}/{trainees * cost.Stone} stone";
    }

    /// <summary>
    /// The keys that would do something right now, and nothing else.
    /// </summary>
    /// <remarks>
    /// The whole point of the panel. What a player wants after clicking a villager is not the twenty keys
    /// the game has, it is the two that apply to a villager standing next to a tree — so a prompt appears
    /// only when its precondition holds, and it says what it will <em>do</em> rather than what it is called.
    /// "RIGHT-CLICK — CUT THIS TREE" rather than "context command".
    /// </remarks>
    private void Prompts(
        SimulationWorld world,
        IReadOnlyCollection<AgentId> selected,
        NodeId hovered,
        bool additive,
        NodeId? routeSource)
    {
        if (selected.Count == 0)
        {
            // Tab first, because it is the answer to the question the panel above just raised by saying
            // how many people are spare.
            lines.Add((
                "TAB SPARE HANDS · DRAG SELECT · D STOREHOUSE · CTRL+D BARRACKS · A FIELD · CTRL+A HOUSE · W CAMP · CTRL+W PALISADE",
                Action));
            // The map keys, on the line below, because they are about the world rather than about the
            // settlement — and because eight bindings nobody can be expected to remember is what the lab
            // learned to print.
            lines.Add((
                "SPACE NEW MAP · ` PANEL: SET ARCHETYPE / REGION / RELIEF, THEN GENERATE",
                Body));
            return;
        }

        if (world.Nodes.Contains(hovered))
        {
            ref readonly var node = ref world.Nodes.Get(hovered);

            // <b>Somebody else's building, and what the hint says depends on who you are holding.</b> §167.
            // The order carries no number, so the only way a player can read how much they are about to
            // steal is off their own selection — which makes the hint doing the counting part of the design
            // rather than decoration. It comes first for the same reason the command does: none of the
            // friendly readings below apply to a stranger's wall.
            if (world.IsHostile(selected, hovered))
            {
                var load = 0;
                foreach (var id in selected)
                {
                    if (world.Agents.Contains(id)) load += world.Agents.Get(id).CarryCapacity;
                }

                var worthRobbing = node.Stores && node.Stock.Total > 0 && load > 0;
                lines.Add((
                    worthRobbing
                        ? $"RIGHT-CLICK — LOOT THIS {node.Kind.ToString().ToUpperInvariant()} " +
                          $"(UP TO {Math.Min(load, node.Stock.Total)}, ONE TRIP)"
                        : $"RIGHT-CLICK — ATTACK THIS {node.Kind.ToString().ToUpperInvariant()}",
                    Action));
                if (worthRobbing && selected.Any(id =>
                        world.Agents.Contains(id) && world.Agents.Get(id).CarryCapacity <= 0))
                {
                    lines.Add(("ESCORT ATTACKS WHILE CARRIERS LOAD", Body));
                }

                return;
            }

            if (node.HasStructuralProject)
            {
                var verb = node.IsUnderConstruction
                    ? "BUILD THIS"
                    : node.StructuralProject == StructuralProjectKind.Repair ? "REPAIR THIS" : "UPGRADE THIS";
                lines.Add(($"RIGHT-CLICK — {verb}", Action));
            }
            else if (node.Kind == NodeKind.Farm) lines.Add(("RIGHT-CLICK — WORK THIS FIELD", Action));
            else if (node.Kind == NodeKind.Tree) lines.Add(("RIGHT-CLICK — CUT THIS TREE", Action));
            else if (node.Kind == NodeKind.Outcrop) lines.Add(("RIGHT-CLICK — QUARRY THIS OUTCROP", Action));
            else if (node.Kind == NodeKind.Barracks && selected.Any(id =>
                         world.Agents.Contains(id) && world.Agents.Get(id).Role == AgentRole.Villager))
                lines.Add(("RIGHT-CLICK — TRAIN SELECTED VILLAGERS AS MILITIA", Action));
            else if (node.IsStructure && node.Condition < node.MaxCondition - 0.0001f)
                lines.Add(("CTRL+RIGHT-CLICK — BEGIN REPAIR", Action));
            else if (node.Kind == NodeKind.PalisadeWall)
                lines.Add(("CTRL+RIGHT-CLICK — UPGRADE TO STONE", Action));
            else lines.Add(("RIGHT-CLICK — MOVE HERE", Action));

            var haulable = node.Stores || node.IsPile || node.WantsMaterials;
            if (haulable)
            {
                lines.Add((
                    routeSource is null
                        ? $"CTRL+O — HAUL FROM HERE ({SimulationWorld.CartTimber} WOOD A CART)"
                        : RouteDestinationPrompt(world, routeSource.Value, hovered),
                    Action));
            }
        }
        else
        {
            lines.Add(("RIGHT-CLICK — MOVE HERE · U — POST HERE", Action));
        }

        lines.Add(("S STOP · Y OFF WORK · Z FOLLOW CAMERA", Body));
        if (additive) lines.Add(("CTRL HELD — ADDING TO SELECTION", Body));
    }

    private static string RouteDestinationPrompt(SimulationWorld world, NodeId source, NodeId sink) =>
        world.TryChooseRouteCargo(source, sink, Resource.Grain, out var cargo)
            ? $"CTRL+O — DELIVER HERE · NEXT {cargo.ToString().ToUpperInvariant()}"
            : "CTRL+O — THIS PLACE CANNOT RECEIVE THAT STOCK";

    /// <summary>
    /// Draws the staged lines, each over a dark plate so it reads on any ground.
    /// </summary>
    /// <remarks>
    /// A plate rather than an outline. This is white-ish text over a scene whose brightness ranges from
    /// sunlit wheat to the inside of a wood, and there is no single text colour that survives both — the
    /// cheapest thing that always works is to stop the scene showing through.
    /// </remarks>
    private void Block(float size, Vector2 at, float step)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var (text, colour) = lines[i];
            var position = at + new Vector2(0f, i * step);
            var measured = SpriteBatchUiExtensions.MeasureText(font!, size, text);
            batch.DrawSolidRect(
                plate,
                new Rect(position.X - size * 0.35f, position.Y - size * 0.2f,
                    measured.X + size * 0.7f, measured.Y + size * 0.35f),
                Shadow);
            batch.DrawText(font!, size, text, position, colour);
        }
    }

    /// <summary>What a store is holding, naming only what is actually in it.</summary>
    /// <remarks>
    /// Built from <see cref="Resources.All"/> so a resource added later appears here without anybody
    /// remembering to add it, and skipping empties so a granary with grain in it does not read
    /// "4,200 grain, 0 wood, 0 stone" — the zeros are the least interesting thing on a crowded panel, and one
    /// per resource is a line that gets longer every time the game gets richer.
    /// </remarks>
    private static string Held(in EconomyNode node)
    {
        var parts = new List<string>();
        foreach (var resource in Resources.All)
        {
            var units = node.Stock[resource];
            if (units != 0) parts.Add($"{units:N0} {resource.ToString().ToLowerInvariant()}");
        }

        return parts.Count == 0 ? "empty" : string.Join(", ", parts);
    }

    public void Dispose() => batch.Dispose();
}
