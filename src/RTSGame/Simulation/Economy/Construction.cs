namespace RTSGame.Simulation.Economy;

/// <summary>
/// What a building costs to put up: physical material at the site, and hands standing at it.
/// </summary>
/// <remarks>
/// <b>This is where attention converts into infrastructure</b>, which is the whole of §2's identity read
/// from the other end. Every structure in the design buys back attention — a granary is autonomy, a depot
/// is reach, a house is people — and until now they all appeared instantly, so the exchange rate was
/// infinite and the identity was a claim with nothing on one side of it. A building is now paid for in the
/// only two currencies the game has: material, and somebody's time.
/// <para>
/// <b>A building under construction is that building, unfinished.</b> Not a separate kind of node with its
/// own rules — the same node, with its labour not yet spent, which is why every predicate on
/// <see cref="EconomyNode"/> asks <c>IsBuilt</c> rather than every caller remembering to. An unfinished
/// granary stores nothing, feeds nobody and owns no catchment, and it does so in twenty-six places without
/// twenty-six chances to forget.
/// </para>
/// <para>
/// It also means the footprint never changes. A site occupies exactly the ground the finished building
/// will, so bodies route around it from the moment it is placed and completion re-rasterises nothing —
/// which matters because placing a building is already the one operation that rebuilds the navigation
/// raster, and doing it twice per building would double the cost of the most expensive thing the player
/// can do.
/// </para>
/// </remarks>
internal static class Construction
{
    /// <summary>
    /// Timber a building needs incorporated while work advances, in whole units.
    /// </summary>
    /// <remarks>
    /// In sacks, because a villager's carry is the unit everything else in this economy is measured in and
    /// "six sacks of timber" is a thing a player can hold in their head where "180 wood" is not. A
    /// settlement burns about two thousand a year, so a house is a month of firewood and a granary is three
    /// — dear enough to plan for, and nothing like a wall of a tech tree.
    /// <para>
    /// <b>A field costs no timber</b>, and no labour either. Not an oversight: breaking ground is already
    /// the crop cycle's Prepare window, which is 900 labour-seconds inside a 1,200-second spring and is the
    /// most expensive thing a farmhand does all year. Charging construction on top would be charging twice
    /// for the same work, and a field that had to be <em>built</em> and then <em>prepared</em> would put a
    /// season between deciding to plough and ploughing.
    /// </para>
    /// </remarks>
    public static int TimberFor(NodeKind kind) => kind switch
    {
        NodeKind.Granary => Sacks(18),
        NodeKind.House => Sacks(6),
        // A lumber camp is the answer to a receding wood line, and the answer must not cost more than the
        // problem. Four sacks is affordable out of a settlement that is already running short — which is
        // exactly when it is wanted, and it is the pressure that makes acting early rather than late a
        // real decision instead of a hint.
        NodeKind.ForwardDepot => Sacks(4),
        NodeKind.PalisadeWall => Sacks(2),
        NodeKind.Barracks => Sacks(10),
        _ => 0,
    };

    /// <summary>
    /// Stone a building needs delivered, in whole units.
    /// </summary>
    /// <remarks>
    /// <b>Stone's first sink, and the granary is the right one to be it.</b> A resource with no demand is
    /// bookkeeping — the objection <see cref="Resource"/>'s own remarks raised against a third resource — and
    /// until now stone came out of the rock and sat in a store with nothing to spend it on.
    /// <para>
    /// <b>Only the granary.</b> A cottage is timber and thatch and always was. The depot must stay cheap for
    /// the reason written above it: it is the answer to a receding wood line, and an answer that needs a
    /// resource you may have no reachable deposit of is no answer at all — on some maps the nearest quarry is
    /// 88 m from anywhere a village can stand, so a depot priced in stone would be unbuildable exactly where
    /// it is most needed.
    /// </para>
    /// <para>
    /// So the cost lands on the one building that is a <em>statement of permanence</em>: a second granary is a
    /// second centre, the thing a settlement builds when it means to stay. Eight sacks against the granary's
    /// eighteen of timber — enough that a quarry has to be working, not enough to gate the building behind an
    /// expedition.
    /// </para>
    /// </remarks>
    public static int StoneFor(NodeKind kind) => kind switch
    {
        NodeKind.Granary => Sacks(8),
        NodeKind.Barracks => Sacks(4),
        _ => 0,
    };

    /// <summary>Everything a completed building incorporates.</summary>
    /// <remarks>
    /// One place that answers "what does this cost", so a new material is a line here rather than a hunt
    /// through every caller that used to ask about timber by name.
    /// </remarks>
    public static NodeStock CostFor(NodeKind kind)
    {
        var cost = default(NodeStock);
        cost.Wood = TimberFor(kind);
        cost.Stone = StoneFor(kind);
        return cost;
    }

    /// <summary>
    /// Labour-seconds of standing at a site that finishes it as material is supplied.
    /// </summary>
    /// <remarks>
    /// Sized against the seasons, like the crop windows: a house is 1,200 against a 1,200-second spring, so
    /// one pair of hands takes a season and four take a quarter of one. A granary is three times that
    /// because it is three times the building. A depot is 600 — half a season for one person — because a
    /// frontier holding that took a season to raise would always be raised too late.
    /// <para>
    /// Accrued <em>per site from the hands standing at it</em> rather than per body, unlike a crop. A field
    /// accrues per body because the reaper carries the crop away in its own hands and the grain has to go
    /// somewhere; a building has no output, so there is nothing to attribute and four builders are simply
    /// four times the work.
    /// </para>
    /// </remarks>
    public static float LabourFor(NodeKind kind) => kind switch
    {
        NodeKind.Granary => 3_600f,
        NodeKind.House => 1_200f,
        NodeKind.ForwardDepot => 600f,
        NodeKind.PalisadeWall => 300f,
        NodeKind.Barracks => 1_800f,
        _ => 0f,
    };

    /// <summary>Whether this kind of node has to be built at all.</summary>
    public static bool NeedsBuilding(NodeKind kind) => LabourFor(kind) > 0f || CostFor(kind).Total > 0;

    /// <summary>What a site says about itself, which is what the player needs to read.</summary>
    /// <remarks>
    /// Progress and shortages are independent facts now that work can follow partial deliveries. The state
    /// therefore says whether hands are working or paused, how far the structure has advanced, and which
    /// physical materials remain outstanding. A single "under construction" would collapse those facts and
    /// leave the player guessing which action is needed.
    /// </remarks>
    public static string StateOf(in EconomyNode node)
    {
        if (node.IsBuilt) return "built";
        // Named per material, because "wants materials" tells a player nothing about which cart to send.
        var missing = new List<string>();
        foreach (var resource in Resources.All)
        {
            var short_ = node.Wanted(resource);
            if (short_ > 0) missing.Add($"{short_} {(resource == Resource.Wood ? "timber" : resource.ToString().ToLowerInvariant())}");
        }

        var labour = LabourFor(node.Kind);
        var progress = node.BuildWork / MathF.Max(1f, labour) * 100f;
        var state = node.Hands == 0
            ? progress > 0f ? $"paused at {progress:F0}%" : "waiting for hands"
            : $"building {progress:F0}%";
        return missing.Count > 0 ? $"{state}, needs {string.Join(" and ", missing)}" : state;
    }

    /// <summary>Timber in villagers' sacks, which is the unit everything else here is measured in.</summary>
    private static int Sacks(int count) => count * Agents.UnitType.Villager.CarryCapacity;
}
