using System.Diagnostics;
using System.Globalization;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Navigation;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Debug;

/// <summary>
/// What a second body radius costs the routing partition, measured before any unit type exists.
/// </summary>
/// <remarks>
/// <c>plan-rts-game.md</c> §13 puts unit types next and §14 says why that session is load-bearing:
/// the first unit with a body radius other than 0.37 m decides whether one rectangle decomposition
/// serves every unit or whether the map needs one per radius. Everything from the jobs layer
/// onwards sits on the answer, so it is worth having as a number rather than a preference.
/// <para>
/// The machinery for the expensive answer already exists — <c>PathService.Mesh</c> keys its cache
/// on <c>(navigation revision, radius)</c> — so this measures what that key would cost if it were
/// ever used with more than one value: the build time, the resident bytes and the crossing degree
/// per class, and, more importantly, whether the classes differ at all.
/// </para>
/// <para>
/// <b>What it found, and why it does not depend on the map.</b> Every map here is a placeholder and
/// none of them is evidence about terrain nobody has authored. They did not need to be. Clearance is
/// the distance from a cell centre to the nearest obstacle box, every box has its faces on the 0.5 m
/// lattice — impassable cells, cliff edges half a cell off a centre, placement cells three nav cells
/// wide, and the map bounds — and every cell centre sits a quarter-cell off that lattice. So each
/// component of that distance is 0 or 0.25 + 0.5k, and the only clearances the rasteriser can ever
/// produce are the hypotenuses over those: 0.250, 0.354, 0.750, 0.791, 1.061, 1.250. Four maps,
/// including one cut at forty-five degrees, return exactly that set and differ only in how many
/// cells sit on each rung.
/// </para>
/// <para>
/// <b>So the ladder is a property of the raster.</b> Two radii between 0.319 m and 0.715 m see the
/// same ground on any map this rasteriser can produce, whatever the terrain turns out to look like,
/// and a worker, a soldier and a cart are one decomposition rather than three. The condition on that
/// is the lattice: give the rasteriser an obstacle whose faces are not on it — a rotated building, an
/// arbitrary footprint — and the achievable clearances become dense and this stops being true.
/// </para>
/// <para>
/// Four maps, because they fail differently, and the per-map tables are only worth their rung
/// spectrum. Ridge, lake and road is the terrain placeholder; the walled map is authored
/// construction, where every aperture is a multiple of the 1.5 m placement cell; the graded defile
/// reads the widths off directly; the diagonal one is there to show the ladder does not come from
/// the ground running along the grid.
/// </para>
/// </remarks>
internal static class BodyRadiusSweep
{
    /// <summary>
    /// Radii to sweep, spanning every body the design asks for and both sides of it.
    /// </summary>
    /// <remarks>
    /// §3 fixes speeds for a soldier, a cart and a scout and leaves radius at 0.37 m for all of
    /// them, so there is no roster to read — only the engineering baseline. That baseline supplies
    /// two real numbers, <see cref="AgentDefaults.Radius"/> and <see cref="AgentDefaults.CrowdRadius"/>,
    /// and one ceiling: a 1.5 m placement cell means the narrowest gap a player can build is 1.5 m
    /// wide, which admits a body of radius 0.715 m — the same figure the raster's own ladder lands
    /// on, which is not a coincidence, since both are the 0.5 m lattice counted differently. So the
    /// sweep runs from well under the crowd body to well over anything that could fit through a
    /// gate, and the breakpoints are what name the classes rather than the other way round.
    /// </remarks>
    private static readonly float[] DefaultRadii =
    {
        0.20f, AgentDefaults.CrowdRadius, 0.30f, AgentDefaults.Radius, 0.45f,
        0.55f, 0.65f, 0.75f, 0.95f, 1.20f,
    };

    /// <summary>Side of the graded defile map, in metres.</summary>
    private const float DefileExtentMeters = 90f;

    /// <summary>Gap widths cut into the defile wall, in navigation cells of 0.5 m.</summary>
    private static readonly int[] DefileGapCells = { 1, 2, 3, 4, 5, 6, 8, 10 };

    private readonly record struct Sample(
        float Radius,
        int WalkableCells,
        int Rectangles,
        int Crossings,
        int MedianDegree,
        int NinetyNinthDegree,
        int WorstDegree,
        int Components,
        int LargestComponentCells,
        double BuildMilliseconds,
        long Bytes);

    public static int Run(float[]? radii = null, float extentMeters = 600f)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        var set = radii is { Length: > 0 } ? radii : DefaultRadii;

        Console.WriteLine("RTSGame body radius against the routing partition");
        Console.WriteLine(
            "  One decomposition per radius, or one for everybody. Walkable is the ground a body of");
        Console.WriteLine(
            "  that radius may stand on; components and the largest of them say whether a difference");
        Console.WriteLine(
            "  is ground lost along an edge or a route lost entirely.");
        Console.WriteLine();

        var terrain = new SimulationWorld(extentMeters);
        WorldTerrainScenarios.Populate(terrain, issueGroupMove: false);
        Report($"ridge, lake and road | {terrain.ExtentMeters:F0} m", terrain, set);

        var walled = RegionRoutingScenarios.Build();
        Report($"walls with gaps | {walled.ExtentMeters:F0} m | built, so every gap is 1.5 m cells",
            walled, set);

        var defile = BuildDefile(out var gapStarts, out var wallRow);
        Report($"graded defile | {defile.ExtentMeters:F0} m | one wall, gaps of 0.5 m to 5 m",
            defile, set);
        ReportGaps(defile, gapStarts, wallRow, set);

        Report(
            $"diagonal defile | {DefileExtentMeters:F0} m | the same wall at 45 degrees",
            BuildDiagonalDefile(),
            set);

        ReportSubCellGaps();

        return 0;
    }

    /// <summary>
    /// Every clearance a cell centre on this map actually holds, in the band radii live in.
    /// </summary>
    /// <remarks>
    /// This is the part of the measurement that does not depend on the map. A body may stand on a
    /// cell when <c>Clearance >= radius + 0.035</c>, so the walkable set is a step function of
    /// radius and the steps are exactly the distinct clearances the terrain produces. Two unit
    /// types sharing a step share a decomposition; that is arithmetic, not judgement.
    /// <para>
    /// Which is why the spectrum is the output worth keeping and the per-map tables above are not.
    /// It also turned out to be the same spectrum on every map tried, for the lattice reason in this
    /// class's own remarks — so the steps are fixed and only the cells sitting on each of them are
    /// terrain. Run it against real terrain when there is some: if the rungs are still these six,
    /// the sharing argument holds unchanged, and if they are not, something has put an obstacle off
    /// the lattice and that is the thing to go and look at.
    /// </para>
    /// </remarks>
    private static void ReportSpectrum(SimulationWorld world, float[] radii)
    {
        var lowest = radii.Min() + 0.035f;
        var highest = radii.Max() + 0.035f;
        var counts = new SortedDictionary<float, int>();
        var grid = world.Navigation;
        for (var z = 0; z < grid.Height; z++)
        for (var x = 0; x < grid.Width; x++)
        {
            var clearance = grid.Clearance(new GridCell(x, z));
            if (clearance < lowest || clearance > highest) continue;
            // Millimetres: distances here are sums and diagonals of a 0.5 m cell, so anything
            // finer is float noise rather than a step a body could ever feel.
            var step = MathF.Round(clearance, 3);
            counts[step] = counts.GetValueOrDefault(step) + 1;
        }

        if (counts.Count == 0)
        {
            Console.WriteLine("    no cell on this map has a clearance any of these radii could" +
                              " straddle — every body sees the same ground");
            Console.WriteLine();
            return;
        }

        Console.WriteLine(
            $"    rungs in [{lowest:F3}, {highest:F3}] — a radius crossing one loses those cells");
        foreach (var (clearance, cells) in counts)
        {
            Console.WriteLine(
                $"      clearance {clearance:F3} m | widest body {clearance - 0.035f:F3} m | " +
                $"{cells,8:N0} cells");
        }

        Console.WriteLine();
    }

    private static void Report(string title, SimulationWorld world, float[] radii)
    {
        Console.WriteLine($"  {title}");
        Console.WriteLine(
            "    radius | walkable  | rects  | crossings | degree med/p99/worst | parts | largest   " +
            "| built  | bytes");

        var previous = -1;
        foreach (var radius in radii)
        {
            var sample = Measure(world, radius);
            // Ground lost against the radius before this one, which is what a shared decomposition
            // would take away from the narrower body.
            var lost = previous < 0 ? 0 : previous - sample.WalkableCells;
            Console.WriteLine(
                $"    {sample.Radius:F3}  | {sample.WalkableCells,9:N0} | {sample.Rectangles,6:N0} | " +
                $"{sample.Crossings,9:N0} | {sample.MedianDegree,5}/{sample.NinetyNinthDegree,4}/" +
                $"{sample.WorstDegree,-6} | {sample.Components,5:N0} | {sample.LargestComponentCells,9:N0} " +
                $"| {sample.BuildMilliseconds,5:F1}ms | {sample.Bytes / 1024f,7:F0} KB" +
                (lost > 0 ? $"  (-{lost:N0} cells)" : lost < 0 ? "  (+)" : "  (identical)"));
            previous = sample.WalkableCells;
        }

        Console.WriteLine();
        ReportSpectrum(world, radii);
    }

    private static Sample Measure(SimulationWorld world, float radius)
    {
        // Wall time here is as noisy as everywhere else in this codebase; best of three, as
        // plan-rts.md §4 requires of every timing it records.
        var best = double.MaxValue;
        WalkableRectangles mesh = null!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var watch = Stopwatch.StartNew();
            mesh = world.DecomposeWalkable(radius);
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }

        var degrees = new int[mesh.Count];
        var worst = 0;
        for (var r = 0; r < mesh.Count; r++)
        {
            degrees[r] = mesh.CrossingsOf(r).Length;
            worst = Math.Max(worst, degrees[r]);
        }

        Array.Sort(degrees);
        var median = degrees.Length == 0 ? 0 : degrees[degrees.Length / 2];
        var ninetyNinth = degrees.Length == 0 ? 0 : degrees[(int)(degrees.Length * 0.99f)];

        var (components, largest) = Connectivity(mesh);
        return new Sample(
            radius,
            mesh.CoveredCells,
            mesh.Count,
            mesh.Crossings.Count,
            median,
            ninetyNinth,
            worst,
            components,
            largest,
            best,
            mesh.ResidentBytes);
    }

    /// <summary>
    /// How many pieces the walkable ground falls into, and how big the biggest one is.
    /// </summary>
    /// <remarks>
    /// This is the column that separates the two ways a wider body sees less ground. Erosion along
    /// every wall costs cells and changes nothing about where a body can get to; a gap closing
    /// costs a route. Cell counts alone cannot tell those apart, and the second is the only one
    /// that would force a decomposition per class.
    /// </remarks>
    private static (int Components, int LargestCells) Connectivity(WalkableRectangles mesh)
    {
        var parent = new int[mesh.Count];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int node)
        {
            while (parent[node] != node)
            {
                parent[node] = parent[parent[node]];
                node = parent[node];
            }

            return node;
        }

        foreach (var crossing in mesh.Crossings)
        {
            var a = Find(crossing.RectangleA);
            var b = Find(crossing.RectangleB);
            if (a != b) parent[a] = b;
        }

        var cells = new Dictionary<int, int>();
        for (var i = 0; i < mesh.Count; i++)
        {
            var root = Find(i);
            cells[root] = cells.GetValueOrDefault(root) + mesh.All[i].Area;
        }

        var largest = 0;
        foreach (var component in cells.Values) largest = Math.Max(largest, component);
        return (cells.Count, largest);
    }

    /// <summary>
    /// One wall, cut with gaps from one cell to ten, so the width a body stops fitting through
    /// can be read off rather than inferred.
    /// </summary>
    /// <remarks>
    /// Painted impassable rather than raised, because a raised cell is a height sampled at four
    /// corners and the cells at the foot of a slope come out at intermediate heights — real
    /// terrain, and the wrong instrument for measuring a gap whose width is supposed to be exact.
    /// </remarks>
    private static SimulationWorld BuildDefile(out int[] gapStarts, out int wallRow)
    {
        var world = new SimulationWorld(DefileExtentMeters);
        var grid = world.Terrain.Transform;
        wallRow = grid.Height / 2;

        var spacing = grid.Width / (DefileGapCells.Length + 1);
        var open = new HashSet<int>();
        gapStarts = new int[DefileGapCells.Length];
        for (var i = 0; i < DefileGapCells.Length; i++)
        {
            var start = spacing * (i + 1) - DefileGapCells[i] / 2;
            gapStarts[i] = start;
            for (var x = start; x < start + DefileGapCells[i]; x++) open.Add(x);
        }

        for (var x = 0; x < grid.Width; x++)
        {
            if (open.Contains(x)) continue;
            world.Terrain.SetSurface(new GridCell(x, wallRow), TerrainSurface.Impassable);
        }

        world.RebuildTerrainNavigation();
        return world;
    }

    private static void ReportGaps(SimulationWorld world, int[] gapStarts, int wallRow, float[] radii)
    {
        Console.WriteLine("  the ladder — clearance is measured at cell centres, so a gap admits");
        Console.WriteLine("  whatever the best-placed centre in it admits, not half its width");
        Console.WriteLine("    gap        | best clearance | widest body | admits");

        for (var i = 0; i < DefileGapCells.Length; i++)
        {
            var cells = DefileGapCells[i];
            var best = 0f;
            for (var x = gapStarts[i]; x < gapStarts[i] + cells; x++)
            {
                best = MathF.Max(best, world.Navigation.Clearance(new GridCell(x, wallRow)));
            }

            // IsWalkable wants clearance >= radius + 0.035.
            var widest = best - 0.035f;
            var admits = radii.Where(r => r <= widest).ToArray();
            Console.WriteLine(
                $"    {cells,2} cells, {cells * 0.5f:F1} m | {best,10:F3} m   | {widest,8:F3} m  | " +
                (admits.Length == 0
                    ? "nothing"
                    : string.Join(", ", admits.Select(r => r.ToString("F3")))));
        }

        Console.WriteLine();
    }

    /// <summary>
    /// The same graded wall, cut at forty-five degrees.
    /// </summary>
    /// <remarks>
    /// Obstacle boxes are axis-aligned whatever shape the ground is, so a wall that runs along the
    /// grid puts every cell centre at a whole or half multiple of the cell from it and the rungs
    /// come out half a metre apart. Ground that does not run along the grid does not have that
    /// property, and real terrain is mostly ground that does not run along the grid.
    /// <para>
    /// This exists to bound the question rather than answer it: it says how finely terrain could
    /// ever separate two body radii, which is a fact about the raster, where the axis-aligned
    /// version only said what these particular placeholder maps happen to do.
    /// </para>
    /// </remarks>
    private static SimulationWorld BuildDiagonalDefile()
    {
        var world = new SimulationWorld(DefileExtentMeters);
        var grid = world.Terrain.Transform;
        var diagonal = grid.Width;

        // Gaps are cut as runs along the wall, so a run of n cells is a gap of n/sqrt(2) cells
        // measured across it — which is the point: widths the axis-aligned map cannot express.
        var spacing = diagonal / (DefileGapCells.Length + 1);
        var open = new HashSet<int>();
        for (var i = 0; i < DefileGapCells.Length; i++)
        {
            var start = spacing * (i + 1) - DefileGapCells[i] / 2;
            for (var step = start; step < start + DefileGapCells[i]; step++) open.Add(step);
        }

        for (var x = 0; x < grid.Width; x++)
        for (var z = 0; z < grid.Height; z++)
        {
            if (x + z != diagonal - 1 && x + z != diagonal) continue;
            if (open.Contains(x)) continue;
            world.Terrain.SetSurface(new GridCell(x, z), TerrainSurface.Impassable);
        }

        world.RebuildTerrainNavigation();
        return world;
    }

    /// <summary>
    /// What one clearance sample per cell can measure about a gap that is not on the lattice.
    /// </summary>
    /// <remarks>
    /// The lattice argument above says a gap painted cell by cell can only ever be one of a handful
    /// of widths. Generated terrain does not have to be painted that way — a tree is a thing at a
    /// position, and <c>ObstacleIndex</c> already takes boxes at any coordinate — so the obvious
    /// next question is whether sub-cell obstacle geometry buys the discrimination the design wants:
    /// a tree line a villager slips through and a cart does not.
    /// <para>
    /// It does not, and the reason has nothing to do with the lattice. Clearance is one sample per
    /// cell, taken at its centre, and centres are half a metre apart — so the best-placed centre in
    /// a gap sits anywhere from its middle to a quarter of a metre off it, and the measured width
    /// swings by that quarter metre depending only on where the grid happens to fall. The swing is
    /// a property of the sampling, so it survives any obstacle geometry at all.
    /// </para>
    /// <para>
    /// Which sets the rule this whole sweep was after: <b>two body radii can only be told apart by
    /// terrain if they differ by more than half a navigation cell</b>. Below that there is no gap
    /// width that reliably admits the smaller and reliably stops the larger — the same gap does
    /// either, depending on where it landed.
    /// </para>
    /// </remarks>
    private static void ReportSubCellGaps()
    {
        const float cell = 0.5f;
        Console.WriteLine("  a gap placed off the lattice — what one sample per cell can see of it");
        Console.WriteLine(
            "    true gap | measured        | body admitted     | a body this size passes");
        Console.WriteLine(
            "             | worst / best    | worst / best      |");

        for (var width = 0.80f; width <= 1.85f; width += 0.10f)
        {
            var lowest = float.MaxValue;
            var highest = 0f;
            // Slide the gap across one cell. Everything repeats after that, so this is the whole
            // range of answers the same piece of ground can give.
            for (var offset = 0f; offset < cell; offset += 0.005f)
            {
                var left = offset;
                var right = offset + width;
                var best = 0f;
                for (var k = -2; k <= 8; k++)
                {
                    var centre = k * cell + cell * 0.5f;
                    if (centre <= left || centre >= right) continue;
                    best = MathF.Max(best, MathF.Min(centre - left, right - centre));
                }

                lowest = MathF.Min(lowest, best);
                highest = MathF.Max(highest, best);
            }

            // IsWalkable wants clearance >= radius + 0.035.
            var always = lowest - 0.035f;
            var sometimes = highest - 0.035f;
            Console.WriteLine(
                $"    {width,5:F2} m  | {lowest:F3} / {highest:F3}   | {always,6:F3} / {sometimes:F3}    | " +
                (always < 0.2f
                    ? $"nothing reliably; up to {sometimes:F3} if the grid falls well"
                    : $"anything up to {always:F3} m"));
        }

        Console.WriteLine();
        Console.WriteLine(
            $"    the swing is {cell * 0.5f:F2} m wide however the obstacles are shaped, because it is");
        Console.WriteLine(
            "    the sampling and not the geometry — so terrain separates two radii only when they");
        Console.WriteLine(
            $"    differ by more than {cell * 0.5f:F2} m.");
        Console.WriteLine();
    }
}
