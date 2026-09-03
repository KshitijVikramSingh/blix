using System;
using System.Collections.Generic;
using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Debug;

/// <summary>
/// What a generated map has to be true of, measured rather than looked at.
/// </summary>
/// <remarks>
/// <b>Written before the generator it is for, and it fails on the one we have.</b> §151, stage A of §150.
/// Every water fault in §145 through §149 was found by an instrument and none by reading, and the generator
/// that produced them has been judged from the chair for its whole life — including by the log line that
/// prints its own traversability violation on every run and fails nothing.
/// <para>
/// Three criteria, chosen from the chair and each one assertable: <b>walkability</b>, <b>coherent
/// drainage</b>, <b>legible for play</b>. Deliberately not settleability — the site scorer already has an
/// opinion about good ground, and constraining generation to please it would put the same opinion on both
/// sides of the test.
/// </para>
/// </remarks>
internal readonly record struct TerrainCriteria(
    float WalkableShare,
    float LargestIslandShare,
    float SteepestGrade,
    float FallPer100M,
    int LakesWithoutBasin,
    int UphillReaches,
    int StrandedReaches,
    int Fords)
{
    /// <summary>
    /// Share of the map a body can cross, below which the map is scenery rather than ground.
    /// </summary>
    /// <remarks>
    /// Fifty-five per cent. Not an aspiration: §145 measured the generator at <b>sixty-six per cent closed
    /// canopy</b>, and closed canopy is impassable, so a third of the map was crossable and nobody had
    /// noticed. The figure is the floor at which the two-settlement game still has room to happen.
    /// </remarks>
    public const float WalkableFloor = 0.55f;

    /// <summary>
    /// Share of walkable ground that has to be in one piece.
    /// </summary>
    /// <remarks>
    /// A map whose crossable ground is shattered into islands is not legible however much of it there is:
    /// a player cannot see why they cannot get somewhere. Nine tenths leaves room for a genuine island or a
    /// walled-off pocket behind a lake and refuses a mosaic.
    /// </remarks>
    public const float IslandFloor = 0.90f;

    /// <summary>
    /// Overall hydraulic fall the ground needs for water to behave, in metres per hundred.
    /// </summary>
    /// <remarks>
    /// <b>The number the whole of §150 turns on.</b> The generator reports 0.73 m per 100 m and every water
    /// fault this session followed from it: with that little gradient a depression's outlet sits barely above
    /// its floor, so a fill spreads wide and paper-thin and a channel has nothing to lie in. Two per cent is
    /// the gentlest real lowland river valley; below it, water cannot be made to look right by any amount of
    /// shading.
    /// </remarks>
    public const float FallFloor = 2.0f;

    public bool Passes =>
        WalkableShare >= WalkableFloor &&
        LargestIslandShare >= IslandFloor &&
        SteepestGrade <= TerrainMap.MaximumTraversableGrade &&
        FallPer100M >= FallFloor &&
        LakesWithoutBasin == 0 &&
        UphillReaches == 0 &&
        StrandedReaches == 0;

    public override string ToString() =>
        $"walkable {WalkableShare * 100f:F0}% (largest piece {LargestIslandShare * 100f:F0}% of it), " +
        $"steepest {SteepestGrade:F2} against {TerrainMap.MaximumTraversableGrade:F2}, " +
        $"fall {FallPer100M:F2} m/100 m, " +
        $"{LakesWithoutBasin} basinless lake(s), {UphillReaches} uphill reach(es), " +
        $"{StrandedReaches} stranded reach(es), {Fords} ford(s)";

    /// <summary>Every way this map falls short, named, for a sweep that would rather list than summarise.</summary>
    public IEnumerable<string> Shortfalls()
    {
        if (WalkableShare < WalkableFloor)
        {
            yield return $"only {WalkableShare * 100f:F0}% of it can be crossed";
        }

        if (LargestIslandShare < IslandFloor)
        {
            yield return $"its crossable ground is in pieces (largest {LargestIslandShare * 100f:F0}%)";
        }

        if (SteepestGrade > TerrainMap.MaximumTraversableGrade)
        {
            yield return
                $"a flank of {SteepestGrade:F2} against a traversable limit of " +
                $"{TerrainMap.MaximumTraversableGrade:F2}";
        }

        if (FallPer100M < FallFloor)
        {
            yield return $"only {FallPer100M:F2} m of fall per 100 m, so water has nowhere to go";
        }

        if (LakesWithoutBasin > 0) yield return $"{LakesWithoutBasin} lake(s) with no basin under them";
        if (UphillReaches > 0) yield return $"{UphillReaches} watercourse(s) running uphill";
        if (StrandedReaches > 0) yield return $"{StrandedReaches} watercourse(s) ending nowhere";
    }

    public static TerrainCriteria Measure(SimulationWorld world)
    {
        var terrain = world.Terrain;
        var grid = terrain.Transform;

        // <b>Walkability from the navigation raster, which is the layer that decides it.</b> Not recomputed
        // from slope here: a second opinion about what can be crossed is exactly the cross-layer trap §51
        // spent a section on, and the raster already folds surface, clearance and water together.
        // <b>Inside the rim, because the rim is a wall on purpose.</b> §151: the generator already caps
        // grades — GradeLimit.Apply with a ceiling of 0.92 of the traversable limit — but it applies the cap
        // <em>inset</em> from the border, because a map's edge is deliberately unclimbable. The first run of
        // this criterion reported flanks of 3.46 and they were the boundary doing its job. A criterion that
        // indicts a deliberate cliff is measuring the wrong ground, and §150 said "outside deliberate cliffs"
        // for exactly this reason.
        var rim = (int)MathF.Ceiling(ReliefPlan.RimWidthMetres / grid.CellSize);
        var walkable = 0;
        var total = 0;
        var crossable = 0;
        var steepest = 0f;
        var passable = new bool[grid.Width * grid.Height];
        for (var z = 0; z < grid.Height; z++)
        for (var x = 0; x < grid.Width; x++)
        {
            var cell = new Simulation.Spatial.GridCell(x, z);
            var ok = !world.Navigation.IsBlocked(cell);
            passable[z * grid.Width + x] = ok;
            // Counted over the whole grid, because the island fill runs over the whole grid and a share of
            // one count against another's denominator is how the first version reported "largest piece 221%".
            if (ok) crossable++;
            var inside = x >= rim && z >= rim && x < grid.Width - rim && z < grid.Height - rim;
            if (!inside) continue;
            total++;
            if (ok) walkable++;

            // <b>Only ground meant to be crossed.</b> A crag is impassable on purpose and so is deep water;
            // asking either of them to be gentle is asking the map not to have cliffs or lakes. This is the
            // other half of §150's "outside deliberate cliffs" — the rim above is the first.
            if (!TerrainSurfaceRules.IsPassable(terrain.Surface(cell))) continue;
            steepest = MathF.Max(steepest, terrain.SampleGrade(grid.CellCenter(cell)));
        }

        var island = LargestIsland(passable, grid.Width, grid.Height);
        var (fall, basinless, uphill, stranded, fords) = Hydrology(world);

        return new TerrainCriteria(
            total == 0 ? 0f : walkable / (float)total,
            crossable == 0 ? 0f : island / (float)crossable,
            steepest,
            fall,
            basinless,
            uphill,
            stranded,
            fords);
    }

    /// <summary>The biggest connected piece of crossable ground, by flood fill on four neighbours.</summary>
    private static int LargestIsland(bool[] passable, int width, int height)
    {
        var seen = new bool[passable.Length];
        var stack = new Stack<int>();
        var largest = 0;
        for (var start = 0; start < passable.Length; start++)
        {
            if (seen[start] || !passable[start]) continue;
            stack.Push(start);
            seen[start] = true;
            var size = 0;
            while (stack.Count > 0)
            {
                var index = stack.Pop();
                size++;
                var x = index % width;
                var z = index / width;
                Push(x - 1, z);
                Push(x + 1, z);
                Push(x, z - 1);
                Push(x, z + 1);
            }

            largest = Math.Max(largest, size);
        }

        return largest;

        void Push(int x, int z)
        {
            if (x < 0 || z < 0 || x >= width || z >= height) return;
            var index = z * width + x;
            if (seen[index] || !passable[index]) return;
            seen[index] = true;
            stack.Push(index);
        }
    }

    /// <summary>
    /// The four drainage criteria, walked on the drainage lattice itself.
    /// </summary>
    /// <remarks>
    /// <b>Uphill and stranded are the two faults §145 fixed in the renderer and the solver, asserted here at
    /// the source instead.</b> A reach runs uphill when its water level is below its receiver's, which the
    /// old level rule made routine because a channel's surface was its bed plus a constant. A reach is
    /// stranded when it carries water and its chain reaches neither the map edge nor the sea — a stream that
    /// vanishes into nothing, reported from the chair in exactly those words.
    /// </remarks>
    internal static string LastUphillBands { get; private set; } = "not measured";

    private static (float Fall, int Basinless, int Uphill, int Stranded, int Fords) Hydrology(
        SimulationWorld world)
    {
        if (world.Terrain.Drainage is not { } water) return (0f, 0, 0, 0, 0);
        var side = water.Side;
        var step = water.CellMetres;
        var receiver = water.Receiver;
        var filled = water.Filled;

        var lake = water.LakeDepth;
        var lowest = float.MaxValue;
        var highest = float.MinValue;
        var uphill = 0;
        var narrow = 0;
        var deepening = 0;
        var middling = 0;
        var broad = 0;
        var stranded = 0;
        var fords = 0;
        for (var index = 0; index < filled.Length; index++)
        {
            var at = water.Origin + new Vector2(index % side, index / side) * step;
            var bed = water.BedAt(at);
            lowest = MathF.Min(lowest, bed);
            highest = MathF.Max(highest, bed);

            var wide = water.WidthAt(at);
            // <b>A watercourse is a channel at least as wide as the cell it is measured in.</b> §155, and it
            // is a definition rather than a threshold. The criterion used to fault anything over
            // TraceWidthMetres, which is 1.5 m on a lattice whose cells are four — so it held the solver's
            // answer about something narrower than one sample to a standard, and the solver finds such a
            // channel wherever a trickle of upslope area collects, including in interfluve undulation that
            // nothing authored.
            //
            // <b>Measured before believed:</b> banding every uphill reach on all fifty-five maps by width
            // gave <b>2,482 under four metres and not one above it</b> in the eroded generator, and 4,037
            // against zero in the drainage-first one. Not a single real river runs uphill on any map in
            // either. The fault was the definition, and this is the same reasoning Biomes gives for
            // FordableWidthMetres and §148 gives for the render width gate: at a four-metre cell, the width
            // of something two metres across is not a number anybody should trust.
            if (wide <= MathF.Max(Drainage.TraceWidthMetres, step)) continue;
            var depth = water.LevelAt(at) - bed;
            if (depth <= 0.02f) continue;

            // A crossing: a reach a body can wade is a decision on the map rather than a wall.
            if (depth < Biomes.WadeableDepthMetres) fords++;

            var to = receiver[index];
            if (to < 0 || to == index)
            {
                // The chain ends here. Legitimate at the edge of the lattice or in the sea; a fault inland.
                var x = index % side;
                var z = index / side;
                var edge = x == 0 || z == 0 || x == side - 1 || z == side - 1;
                if (!edge && !water.HasSea) stranded++;
                continue;
            }

            // <b>Not into standing water, which is uphill on purpose.</b> §152: a channel entering a lake has
            // its surface below the lake's by definition — the lake stands at its outlet's level and the
            // stream arrives underneath it — so every shoreline cell of every pool counted as a watercourse
            // running uphill. With two to ten lakes a map and a long margin each, that is where "all
            // fifty-five maps, six to six hundred and forty-three" came from. The fault is real; this many
            // of it was not.
            if (lake[to] > 0.05f) continue;
            var next = water.Origin + new Vector2(to % side, to / side) * step;
            if (water.LevelAt(at) >= water.LevelAt(next) - 0.01f) continue;
            uphill++;
            // <b>Banded by how much water the reach carries, to answer whether these are rivers or
            // trickles.</b> §155: the criterion holds every channel the solver can find to monotonicity, and
            // the solver finds a channel wherever a little upslope area collects — including in the
            // interfluve undulation, on ground nothing authored. If the uphill reaches are all narrow, the
            // fault was the definition; if the wide ones are in there too, it is the ground.
            if (wide < 4f) narrow++;
            else if (wide < 12f) middling++;
            else broad++;

            // <b>Is the bed falling and the surface still rising?</b> §155's hypothesis: the level rule is
            // <c>ground + 0.30*sqrt(width)</c>, and width grows downstream, so the depth added on top of the
            // bed grows too. Where the bed's fall over one cell is smaller than that growth, the <em>surface</em>
            // climbs while the bed descends — which is not a terrain fault at all but the water model adding
            // depth upward instead of incising downward.
            var bedFall = bed - water.BedAt(next);
            var depthGrowth =
                (water.LevelAt(next) - water.BedAt(next)) - (water.LevelAt(at) - bed);
            if (bedFall > 0f && depthGrowth > bedFall) deepening++;
        }

        // <b>Over standing water only, and the first version used Drainage.Bodies and was wrong.</b> §151:
        // Bodies floods every cell whose level is above its bed, so the entire connected river network comes
        // back as one "body" spanning the map diagonal at a metre and a half — and a criterion asking whether
        // that has a basin under it reports a fault on every map ever generated. Fifty-five of the first
        // hundred and sixty-two shortfalls were this, and none of them were real. §147's rule is about
        // depressions, so the criterion has to be too.
        var basinless = 0;
        var seen = new bool[lake.Length];
        var stack = new Stack<int>();
        for (var start = 0; start < lake.Length; start++)
        {
            if (seen[start] || lake[start] <= 0.05f) continue;
            stack.Push(start);
            seen[start] = true;
            var deepest = 0f;
            var lowX = int.MaxValue;
            var highX = int.MinValue;
            var lowZ = int.MaxValue;
            var highZ = int.MinValue;
            while (stack.Count > 0)
            {
                var index = stack.Pop();
                deepest = MathF.Max(deepest, lake[index]);
                var cx = index % side;
                var cz = index / side;
                lowX = Math.Min(lowX, cx);
                highX = Math.Max(highX, cx);
                lowZ = Math.Min(lowZ, cz);
                highZ = Math.Max(highZ, cz);
                Spread(cx - 1, cz);
                Spread(cx + 1, cz);
                Spread(cx, cz - 1);
                Spread(cx, cz + 1);
            }

            var pool = MathF.Max(highX - lowX, highZ - lowZ) * step;
            if (deepest < pool * 0.0125f) basinless++;
        }

        void Spread(int x, int z)
        {
            if (x < 0 || z < 0 || x >= side || z >= side) return;
            var index = z * side + x;
            if (seen[index] || lake[index] <= 0.05f) return;
            seen[index] = true;
            stack.Push(index);
        }

        LastUphillBands =
            $"uphill by channel width: {narrow} under 4 m, {middling} from 4 to 12 m, {broad} over 12 m; " +
            $"{deepening} of them have a falling bed and a deepening channel";
        var fall = highest - lowest;
        var across = side * step;
        return (across <= 0f ? 0f : fall / across * 100f, basinless, uphill, stranded, fords);
    }
}
