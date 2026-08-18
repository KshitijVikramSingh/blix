using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Navigation;

/// <summary>
/// One crossing of a region border: a pair of adjacent cells in different regions
/// that a body of the graph's radius can actually step between.
/// </summary>
internal readonly record struct Portal(
    int RegionA,
    int RegionB,
    GridCell CellA,
    GridCell CellB);

/// <summary>
/// Where a body can get from one region to the next, and nothing else. Purely
/// structural: it knows which crossings exist, not what any of them cost — costs
/// belong to whatever is routing, because they depend on congestion and on which
/// way you are going, and this changes only when the terrain does.
/// </summary>
/// <remarks>
/// Nodes are portal <em>sides</em> rather than portals, so a crossing is an edge with a
/// price rather than a free teleport between regions. Two nodes per portal doubles the
/// node count and is worth it: the alternative undercharges every crossing by a cell
/// step, and on a route across forty regions that is four seconds of route cost that
/// simply is not there — in a layer whose whole discipline is that cost means seconds.
/// <para>
/// Openings are found as maximal runs of steppable cell pairs along a border, one node
/// pair per run. A long run gets several, because a single node in the middle of a wide
/// open border makes every route through it detour to that midpoint: bodies would file
/// through the centre of a gap sixty-four cells wide. <see cref="CellsPerOpening"/> is
/// what stops that, and it is the one number here that trades node count against how
/// straight a long crossing can be.
/// </para>
/// </remarks>
internal sealed class PortalGraph
{
    /// <summary>Widest run of open border a single portal is allowed to stand for.</summary>
    /// <remarks>
    /// Eight metres. Wide enough that ordinary doorways, ramps and passes are one portal
    /// each — so the abstract graph has a node where the map has a decision — and narrow
    /// enough that a wholly open border becomes four evenly spaced crossings rather than
    /// one funnel in the middle.
    /// </remarks>
    public static int CellsPerOpening = 16;

    private readonly Portal[] portals;
    private readonly int[] regionNodeStart;
    private readonly int[] regionNodes;

    public RegionPartition Partition { get; }
    /// <summary>Terrain revision this graph was built against.</summary>
    public int NavigationRevision { get; }
    /// <summary>Body radius this graph was built for; clearance decides what is a portal.</summary>
    public float AgentRadius { get; }

    public int PortalCount => portals.Length;
    /// <summary>Two per portal — one standing in each of the regions it joins.</summary>
    public int NodeCount => portals.Length * 2;

    public Portal PortalAt(int portal) => portals[portal];

    public static int NodeOf(int portal, int side) => portal * 2 + side;
    public static int PortalOfNode(int node) => node >> 1;
    public static int SideOfNode(int node) => node & 1;
    /// <summary>The node on the far side of the same crossing.</summary>
    public static int OppositeNode(int node) => node ^ 1;

    public int RegionOfNode(int node)
    {
        var portal = portals[PortalOfNode(node)];
        return SideOfNode(node) == 0 ? portal.RegionA : portal.RegionB;
    }

    public GridCell CellOfNode(int node)
    {
        var portal = portals[PortalOfNode(node)];
        return SideOfNode(node) == 0 ? portal.CellA : portal.CellB;
    }

    /// <summary>Nodes standing inside a region, in ascending node order.</summary>
    public ReadOnlySpan<int> NodesIn(int region) => regionNodes.AsSpan(
        regionNodeStart[region],
        regionNodeStart[region + 1] - regionNodeStart[region]);

    private PortalGraph(
        RegionPartition partition,
        int navigationRevision,
        float agentRadius,
        Portal[] portals)
    {
        Partition = partition;
        NavigationRevision = navigationRevision;
        AgentRadius = agentRadius;
        this.portals = portals;

        // Region -> nodes, as a counted-then-filled flat array rather than lists, because
        // this is read on every abstract expansion and allocated once per terrain edit.
        var counts = new int[partition.Count + 1];
        for (var portal = 0; portal < portals.Length; portal++)
        {
            counts[portals[portal].RegionA + 1]++;
            counts[portals[portal].RegionB + 1]++;
        }

        for (var region = 0; region < partition.Count; region++)
        {
            counts[region + 1] += counts[region];
        }

        regionNodeStart = counts;
        regionNodes = new int[portals.Length * 2];
        var cursor = new int[partition.Count];
        for (var portal = 0; portal < portals.Length; portal++)
        {
            var a = portals[portal].RegionA;
            var b = portals[portal].RegionB;
            regionNodes[regionNodeStart[a] + cursor[a]++] = NodeOf(portal, 0);
            regionNodes[regionNodeStart[b] + cursor[b]++] = NodeOf(portal, 1);
        }

        // Ascending within each region, which is what keeps every sweep over a region's
        // nodes independent of the order the borders happened to be scanned in.
        for (var region = 0; region < partition.Count; region++)
        {
            Array.Sort(regionNodes, regionNodeStart[region], cursor[region]);
        }
    }

    /// <summary>
    /// Finds every crossing a body of <paramref name="agentRadius"/> can use, by walking
    /// each region's right-hand and lower borders once.
    /// </summary>
    public static PortalGraph Build(
        NavigationGrid grid,
        RegionPartition partition,
        float agentRadius)
    {
        var portals = new List<Portal>();
        var open = new List<int>();

        for (var region = 0; region < partition.Count; region++)
        {
            partition.Bounds(region, out var minimumX, out var minimumZ, out var maximumX, out var maximumZ);

            // Right border: the column of this region against the first column of the next.
            if (partition.Column(region) + 1 < partition.Columns && maximumX + 1 < grid.Width)
            {
                open.Clear();
                for (var z = minimumZ; z <= maximumZ; z++)
                {
                    var here = new GridCell(maximumX, z);
                    var there = new GridCell(maximumX + 1, z);
                    open.Add(grid.CanTraverse(here, there, agentRadius) ? z : int.MinValue);
                }

                EmitRuns(open, minimumZ, run =>
                {
                    portals.Add(new Portal(
                        region,
                        region + 1,
                        new GridCell(maximumX, run),
                        new GridCell(maximumX + 1, run)));
                });
            }

            // Lower border, the same way one row down.
            if (partition.Row(region) + 1 < partition.Rows && maximumZ + 1 < grid.Height)
            {
                open.Clear();
                for (var x = minimumX; x <= maximumX; x++)
                {
                    var here = new GridCell(x, maximumZ);
                    var there = new GridCell(x, maximumZ + 1);
                    open.Add(grid.CanTraverse(here, there, agentRadius) ? x : int.MinValue);
                }

                EmitRuns(open, minimumX, run =>
                {
                    portals.Add(new Portal(
                        region,
                        region + partition.Columns,
                        new GridCell(run, maximumZ),
                        new GridCell(run, maximumZ + 1)));
                });
            }
        }

        return new PortalGraph(partition, grid.Revision, agentRadius, portals.ToArray());
    }

    /// <summary>
    /// Splits a border into maximal open runs and hands back a representative coordinate
    /// for each, adding more than one when a run is wider than <see cref="CellsPerOpening"/>.
    /// </summary>
    private static void EmitRuns(List<int> border, int origin, Action<int> emit)
    {
        var runStart = -1;
        for (var i = 0; i <= border.Count; i++)
        {
            var isOpen = i < border.Count && border[i] != int.MinValue;
            if (isOpen)
            {
                if (runStart < 0) runStart = i;
                continue;
            }

            if (runStart < 0) continue;
            var length = i - runStart;
            var openings = Math.Max(1, (length + CellsPerOpening - 1) / CellsPerOpening);
            for (var opening = 0; opening < openings; opening++)
            {
                // Centre of this opening's share of the run, so two openings on a
                // thirty-two cell border sit at a quarter and three quarters rather than
                // both crowding the middle.
                var offset = (int)((opening + 0.5f) * length / openings);
                emit(origin + runStart + Math.Min(offset, length - 1));
            }

            runStart = -1;
        }
    }
}
