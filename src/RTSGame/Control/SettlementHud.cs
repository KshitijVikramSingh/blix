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
            if (body.CarryCapacity <= 0) continue;
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
        var hovered = pointerOnTerrain
            ? EconomySystem.NodeAt(world.Nodes, pointer, PickRadius)
            : NodeId.None;
        if (world.Nodes.Contains(hovered)) DescribeNode(world, hovered);
        // The settlement's prompts are about a settlement. In the lab there is not one, and offering to
        // build a granary is worse than offering nothing.
        if (lab is null) Prompts(world, selected, hovered, additive, routeSource);

        var blockHeight = lines.Count * step;
        Block(size, new Vector2(pad, height - pad - blockHeight), step);
        batch.End(pass);
    }

    /// <summary>How near the pointer counts as pointing at a node. Matches the route key's reach.</summary>
    private const float PickRadius = 6f;

    private void Describe(SimulationWorld world, IReadOnlyCollection<AgentId> selected)
    {
        if (selected.Count == 0)
        {
            lines.Add(("NOTHING SELECTED", Body));
            return;
        }

        var farming = 0;
        var cutting = 0;
        var carting = 0;
        var building = 0;
        var idle = 0;
        var carried = 0;
        var carts = 0;
        foreach (var id in selected)
        {
            if (!world.Agents.Contains(id)) continue;
            ref readonly var body = ref world.Agents.Get(id);
            if (!body.IsAlive) continue;
            carried += body.Jobs.CarriedUnits;
            if (body.HasCart) carts++;
            switch (body.Jobs.Assignment.Kind)
            {
                case AssignmentKind.Work when body.Jobs.Assignment.Cargo == Resource.Wood:
                    cutting++;
                    break;
                case AssignmentKind.Work:
                    farming++;
                    break;
                case AssignmentKind.Haul:
                case AssignmentKind.Carry:
                    carting++;
                    break;
                case AssignmentKind.Hold:
                    building++;
                    break;
                default:
                    idle++;
                    break;
            }
        }

        var doing = new List<string>();
        if (farming > 0) doing.Add($"{farming} farming");
        if (cutting > 0) doing.Add($"{cutting} cutting");
        if (carting > 0) doing.Add($"{carting} carting");
        if (building > 0) doing.Add($"{building} posted");
        if (idle > 0) doing.Add($"{idle} idle");
        var summary = $"{selected.Count} SELECTED";
        if (doing.Count > 0) summary += " · " + string.Join(", ", doing);
        if (carts > 0) summary += $" · {carts} with carts";
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
            NodeKind.Granary => "GRANARY",
            NodeKind.ForwardDepot => "DEPOT",
            NodeKind.Farm => "FIELD",
            NodeKind.House => "HOUSE",
            NodeKind.Tree => "TREE",
            _ => "HEAP",
        };

        if (node.IsUnderConstruction)
        {
            lines.Add((
                $"{name} SITE · {Construction.StateOf(in node)}",
                node.TimberWanted > 0 ? Warning : Action));
            return;
        }

        var says = node.Kind switch
        {
            NodeKind.Farm => CropCycle.StateOf(in node, world.Date.Season),
            NodeKind.Tree => $"{Woodland.StateOf(in node)}, {node.Stock.Wood} wood",
            NodeKind.Outcrop => $"{Quarrying.StateOf(in node)}, {node.Stock.Stone} stone",
            NodeKind.House => $"{node.Occupants}/{node.Occupancy} living here" +
                              (node.Privation > 0.5f ? " · GOING HUNGRY" : string.Empty),
            // <b>Total, not two named fields.</b> Both of these read the resources rather than listing them,
            // because a panel that names the resources it knows about is a panel that silently stops mentioning
            // the next one — a stone pile read "0 lying on the ground" and a store holding nothing but stone
            // read "0 grain, 0 wood". The same shape as the ledger sweeps in Resource.cs, in the layer where
            // being wrong is merely invisible rather than unbalanced.
            NodeKind.Pile => $"{node.Stock.Total} lying on the ground",
            _ => Held(in node),
        };

        var hands = node.Hands > 0 ? $" · {node.Hands} at work" : string.Empty;
        var hungry = node.IsSink && node.Privation > 0.5f;
        lines.Add(($"{name} · {says}{hands}", hungry ? Warning : Body));
    }

    /// <summary>
    /// The keys that would do something right now, and nothing else.
    /// </summary>
    /// <remarks>
    /// The whole point of the panel. What a player wants after clicking a villager is not the twenty keys
    /// the game has, it is the two that apply to a villager standing next to a tree — so a prompt appears
    /// only when its precondition holds, and it says what it will <em>do</em> rather than what it is called.
    /// "U — CUT THIS TREE" rather than "U: post".
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
                "TAB SPARE HANDS · DRAG TO SELECT · D GRANARY · A FIELD · CTRL+A HOUSE · W DEPOT",
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
            if (node.IsUnderConstruction) lines.Add(("U — BUILD THIS", Action));
            else if (node.Kind == NodeKind.Farm) lines.Add(("U — WORK THIS FIELD", Action));
            else if (node.Kind == NodeKind.Tree) lines.Add(("U — CUT THIS TREE", Action));

            var haulable = node.Stores || node.IsPile || node.TimberWanted > 0;
            if (haulable)
            {
                lines.Add((
                    routeSource is null
                        ? $"CTRL+O — HAUL FROM HERE ({SimulationWorld.CartTimber} WOOD A CART)"
                        : "CTRL+O — DELIVER HERE",
                    Action));
            }
        }
        else
        {
            lines.Add(("U — POST HERE · RIGHT-CLICK TO MOVE", Action));
        }

        lines.Add(("S STOP · Y OFF WORK · Z FOLLOW CAMERA", Body));
        if (additive) lines.Add(("CTRL HELD — ADDING TO SELECTION", Body));
    }

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
