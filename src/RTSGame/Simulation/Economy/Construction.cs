namespace RTSGame.Simulation.Economy;

/// <summary>
/// What a building costs to put up: timber on site, and hands standing at it.
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
    /// Timber a building needs delivered before work can start, in whole units.
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
        _ => 0,
    };

    /// <summary>
    /// Labour-seconds of standing at a site that finishes it, once its timber is there.
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
        _ => 0f,
    };

    /// <summary>Whether this kind of node has to be built at all.</summary>
    public static bool NeedsBuilding(NodeKind kind) => LabourFor(kind) > 0f || TimberFor(kind) > 0;

    /// <summary>What a site says about itself, which is what the player needs to read.</summary>
    /// <remarks>
    /// Three states and they are three different problems. <em>Wanting timber</em> is a hauling problem —
    /// nobody has carried the materials out. <em>Idle</em> is a labour problem: the timber is there and
    /// nobody is working. <em>Building</em> is neither, and the number is how far along. A single "under
    /// construction" would collapse the three and leave the player guessing which one they have.
    /// </remarks>
    public static string StateOf(in EconomyNode node)
    {
        if (node.IsBuilt) return "built";
        var timber = TimberFor(node.Kind);
        if (node.Stock.Wood < timber) return $"wants {timber - node.Stock.Wood} timber";
        var labour = LabourFor(node.Kind);
        return node.Hands == 0
            ? "idle site"
            : $"building {node.BuildWork / MathF.Max(1f, labour) * 100f:F0}%";
    }

    /// <summary>Timber in villagers' sacks, which is the unit everything else here is measured in.</summary>
    private static int Sacks(int count) => count * Agents.UnitType.Villager.CarryCapacity;
}
