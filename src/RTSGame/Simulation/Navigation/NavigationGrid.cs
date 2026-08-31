using System.Numerics;
using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Navigation;

internal sealed class NavigationGrid
{
    /// <summary>What a region's ground is, when every cell of it is the same.</summary>
    private readonly record struct RegionGround(
        bool Blocked,
        float Clearance,
        float Height,
        float TraversalCost,
        float SpeedMultiplier);

    // Chunked by region, and a region whose cells all agree keeps five numbers instead of
    // 4,096 of each. On a 600 m map that is 1.44M cells of raster — 24.5 MB — describing a
    // ridge, a lake and a road: the same complaint the congestion field answered one commit
    // ago, in the layer that holds the most of it. Open ground away from an obstacle is
    // uniform by construction, which is most of any map.
    // One interleaved array per region rather than five parallel ones. Everything that reads
    // this grid reads several of the five about the same cell — CanTraverse alone wants
    // blocked, clearance and height for two cells — so five arrays is five chunk lookups and
    // five cache lines to answer one question. Interleaved it is one of each. Measured: split
    // arrays made a move order on the ridge map 1.7x slower than the dense grid they replaced,
    // and this is what takes it back.
    private readonly RegionGround[]?[] chunks;
    private readonly RegionGround[] uniform;
    private readonly RegionPartition partition;

    public GridTransform Transform { get; }
    public int Width => Transform.Width;
    public int Height => Transform.Height;
    public int Revision { get; private set; }

    /// <summary>
    /// Whether any two cells on this map differ in height.
    /// </summary>
    /// <remarks>
    /// One bool, filled by the pass that already walks every cell, and it exists so that ground with no
    /// relief in it pays nothing at all for the machinery that prices relief. That is not an optimisation
    /// so much as the migration guarantee: every scenario in the suite is calibrated on the flat, and a
    /// climb term that is skipped rather than evaluated-to-zero cannot move any of those numbers even in
    /// the last bit — which matters, because a different number in the last bit is a different route out of
    /// a Dijkstra.
    /// </remarks>
    public bool HasRelief { get; private set; }

    public NavigationGrid(GridTransform transform)
    {
        Transform = transform;
        partition = new RegionPartition(transform);
        chunks = new RegionGround[partition.Count][];
        uniform = new RegionGround[partition.Count];
        Array.Fill(uniform, new RegionGround(false, 0f, 0f, 1f, 1f));
    }

    /// <summary>Region a cell falls in. Both halves are shifts: a region is 64 cells.</summary>
    private int ChunkOf(GridCell cell) => (cell.Z >> 6) * partition.Columns + (cell.X >> 6);

    private static int SlotOf(GridCell cell) =>
        (cell.Z & (RegionPartition.CellsPerSide - 1)) * RegionPartition.CellsPerSide +
        (cell.X & (RegionPartition.CellsPerSide - 1));

    /// <summary>Regions holding a full-resolution chunk rather than five numbers.</summary>
    public int ChunkedRegions
    {
        get
        {
            var count = 0;
            foreach (var chunk in chunks)
            {
                if (chunk is not null) count++;
            }

            return count;
        }
    }

    /// <summary>Bytes of raster currently held.</summary>
    public long ResidentBytes =>
        ChunkedRegions * (long)RegionPartition.CellsPerRegion * 20L +
        uniform.LongLength * 20L +
        chunks.LongLength * 8L;

    /// <summary>Everything known about one cell's ground, in one read.</summary>
    private RegionGround GroundAt(GridCell cell)
    {
        var chunk = ChunkOf(cell);
        var values = chunks[chunk];
        return values is null ? uniform[chunk] : values[SlotOf(cell)];
    }

    public bool Contains(GridCell cell) => Transform.Contains(cell);
    public bool TryWorldToCell(Vector2 world, out GridCell cell) => Transform.TryWorldToCell(world, out cell);
    public Vector2 CellCenter(GridCell cell) => Transform.CellCenter(cell);
    public bool IsBlocked(GridCell cell) => !Contains(cell) || GroundAt(cell).Blocked;
    public float Clearance(GridCell cell) => Contains(cell) ? GroundAt(cell).Clearance : 0f;
    public float HeightAt(GridCell cell) => Contains(cell) ? GroundAt(cell).Height : 0f;
    public float TraversalCost(GridCell cell) =>
        Contains(cell) ? GroundAt(cell).TraversalCost : float.PositiveInfinity;
    public float SpeedMultiplier(GridCell cell) =>
        Contains(cell) ? GroundAt(cell).SpeedMultiplier : 0f;

    public bool IsWalkable(GridCell cell, float agentRadius)
    {
        if (!Contains(cell)) return false;
        var ground = GroundAt(cell);
        return !ground.Blocked && ground.Clearance >= agentRadius + BodyFootprint.NavigationMargin;
    }

    public bool CanTraverse(GridCell from, GridCell to, float agentRadius)
    {
        // One ground read each rather than three, which is the whole point of interleaving:
        // this is the hottest predicate under every search and it wants blocked, clearance and
        // height about both cells.
        if (!Contains(from) || !Contains(to)) return false;
        var fromGround = GroundAt(from);
        if (fromGround.Blocked || fromGround.Clearance < agentRadius + BodyFootprint.NavigationMargin) return false;
        var toGround = GroundAt(to);
        if (toGround.Blocked || toGround.Clearance < agentRadius + BodyFootprint.NavigationMargin) return false;
        var heightDelta = MathF.Abs(toGround.Height - fromGround.Height);
        if (heightDelta > Terrain.TerrainMap.MaximumStepHeight) return false;
        // Level ground has no grade, so there is nothing for the distance to divide into.
        // Not an approximation — zero over any positive number is zero, and the grade limit
        // is positive — and it matters because this is the hottest predicate under the
        // routing searches: up to five calls per diagonal edge, eight edges per cell, each
        // otherwise paying two cell-centre reconstructions and a square root to arrive at
        // that same answer. Most of a map is flat, and all of an unsculpted one is.
        if (heightDelta <= 0f) return true;
        var horizontalDistance = Vector2.Distance(CellCenter(from), CellCenter(to));
        return heightDelta / MathF.Max(horizontalDistance, 0.0001f) <=
               Terrain.TerrainMap.MaximumTraversableGrade;
    }

    /// <summary>
    /// Whether a diagonal step is walkable without cutting either corner.
    /// </summary>
    /// <remarks>
    /// <b>The same five pair tests the callers used to make separately, over four ground reads instead of
    /// ten.</b> A diagonal edge involves exactly four cells — the two ends and the two corners — and asking
    /// <see cref="CanTraverse"/> five times reads each of them two or three times over. Measured on a region
    /// tile fill: the corner tests were 45% of the whole search, 100.6 ms against 55.0 with them removed, and
    /// GroundAt is what they were spending it on.
    /// <para>
    /// The result is identical, not approximate. The old answer was the AND of five predicates with no side
    /// effects, so evaluating them in any order over the same ground values gives the same boolean; only the
    /// number of reads changes.
    /// </para>
    /// </remarks>
    public bool CanTraverseDiagonal(GridCell from, GridCell to, float agentRadius)
    {
        var firstCorner = new GridCell(to.X, from.Z);
        var secondCorner = new GridCell(from.X, to.Z);
        if (!Contains(from) || !Contains(to) || !Contains(firstCorner) || !Contains(secondCorner)) return false;

        var fromGround = GroundAt(from);
        var toGround = GroundAt(to);
        var firstGround = GroundAt(firstCorner);
        var secondGround = GroundAt(secondCorner);
        var clearanceNeeded = agentRadius + BodyFootprint.NavigationMargin;
        if (fromGround.Blocked || fromGround.Clearance < clearanceNeeded) return false;
        if (toGround.Blocked || toGround.Clearance < clearanceNeeded) return false;
        if (firstGround.Blocked || firstGround.Clearance < clearanceNeeded) return false;
        if (secondGround.Blocked || secondGround.Clearance < clearanceNeeded) return false;

        return StepAllowed(from, fromGround, to, toGround) &&
               StepAllowed(from, fromGround, firstCorner, firstGround) &&
               StepAllowed(from, fromGround, secondCorner, secondGround) &&
               StepAllowed(firstCorner, firstGround, to, toGround) &&
               StepAllowed(secondCorner, secondGround, to, toGround);
    }

    /// <summary>The height half of <see cref="CanTraverse"/>, over ground already read.</summary>
    private bool StepAllowed(GridCell from, RegionGround fromGround, GridCell to, RegionGround toGround)
    {
        var heightDelta = MathF.Abs(toGround.Height - fromGround.Height);
        if (heightDelta > Terrain.TerrainMap.MaximumStepHeight) return false;
        if (heightDelta <= 0f) return true;
        var horizontalDistance = Vector2.Distance(CellCenter(from), CellCenter(to));
        return heightDelta / MathF.Max(horizontalDistance, 0.0001f) <=
               Terrain.TerrainMap.MaximumTraversableGrade;
    }

    /// <summary>
    /// Takes a freshly rasterised map and keeps, per region, either every cell of it or the
    /// five numbers that describe all of them.
    /// </summary>
    /// <remarks>
    /// The rasteriser still works in whole-map arrays, so this is where the saving is made
    /// rather than where it is avoided — the dense arrays exist for the length of a rebuild
    /// and are then dropped. Making the rasteriser itself region-aware is the next thing, and
    /// worth measuring before it is assumed: a rebuild happens on a terrain edit, not per tick.
    /// </remarks>
    /// <summary>
    /// Puts the revision back where a load found it, after the raster has been rebuilt from the
    /// restored terrain.
    /// </summary>
    /// <remarks>
    /// The raster itself is not saved — it is a pure function of the terrain and the placement grid,
    /// both of which are — so a load rebuilds it and gets bit-identical ground for a fraction of the
    /// bytes. What cannot be rebuilt is the <em>number</em>: the rebuild bumps the revision, and
    /// every flow field ever cached is keyed by it, so a loaded world that had a different revision
    /// from the one it was saved from would be the same ground under a different name.
    /// </remarks>
    internal void RestoreRevision(int revision) => Revision = revision;

    internal void ReplaceRaster(
        bool[] newBlocked,
        float[] newClearance,
        float[] newHeights,
        float[] newTraversalCosts,
        float[] newSpeedMultipliers)
    {
        var cells = Width * Height;
        if (newBlocked.Length != cells || newClearance.Length != cells ||
            newHeights.Length != cells || newTraversalCosts.Length != cells ||
            newSpeedMultipliers.Length != cells)
        {
            throw new ArgumentException("Navigation raster dimensions do not match the grid.");
        }

        HasRelief = false;
        for (var index = 1; index < newHeights.Length; index++)
        {
            if (newHeights[index] == newHeights[0]) continue;
            HasRelief = true;
            break;
        }

        for (var region = 0; region < partition.Count; region++)
        {
            partition.Bounds(region, out var minimumX, out var minimumZ, out var maximumX, out var maximumZ);
            var first = Transform.Index(new GridCell(minimumX, minimumZ));
            var ground = new RegionGround(
                newBlocked[first],
                newClearance[first],
                newHeights[first],
                newTraversalCosts[first],
                newSpeedMultipliers[first]);

            var agrees = true;
            for (var z = minimumZ; z <= maximumZ && agrees; z++)
            for (var x = minimumX; x <= maximumX; x++)
            {
                var index = Transform.Index(new GridCell(x, z));
                if (newBlocked[index] == ground.Blocked &&
                    newClearance[index] == ground.Clearance &&
                    newHeights[index] == ground.Height &&
                    newTraversalCosts[index] == ground.TraversalCost &&
                    newSpeedMultipliers[index] == ground.SpeedMultiplier)
                {
                    continue;
                }

                agrees = false;
                break;
            }

            if (agrees)
            {
                uniform[region] = ground;
                chunks[region] = null;
                continue;
            }

            var chunk = new RegionGround[RegionPartition.CellsPerRegion];
            for (var z = minimumZ; z <= maximumZ; z++)
            for (var x = minimumX; x <= maximumX; x++)
            {
                var cell = new GridCell(x, z);
                var index = Transform.Index(cell);
                chunk[SlotOf(cell)] = new RegionGround(
                    newBlocked[index],
                    newClearance[index],
                    newHeights[index],
                    newTraversalCosts[index],
                    newSpeedMultipliers[index]);
            }

            chunks[region] = chunk;
        }

        Revision++;
    }
}
