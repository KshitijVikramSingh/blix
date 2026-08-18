using System.Numerics;
using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Navigation;

internal sealed class NavigationGrid
{
    private bool[] blocked;
    private float[] clearance;
    private float[] heights;
    private float[] traversalCosts;
    private float[] speedMultipliers;

    public GridTransform Transform { get; }
    public int Width => Transform.Width;
    public int Height => Transform.Height;
    public int Revision { get; private set; }

    public NavigationGrid(GridTransform transform)
    {
        Transform = transform;
        blocked = new bool[Width * Height];
        clearance = new float[Width * Height];
        heights = new float[Width * Height];
        traversalCosts = Enumerable.Repeat(1f, Width * Height).ToArray();
        speedMultipliers = Enumerable.Repeat(1f, Width * Height).ToArray();
    }

    public bool Contains(GridCell cell) => Transform.Contains(cell);
    public bool TryWorldToCell(Vector2 world, out GridCell cell) => Transform.TryWorldToCell(world, out cell);
    public Vector2 CellCenter(GridCell cell) => Transform.CellCenter(cell);
    public bool IsBlocked(GridCell cell) => !Contains(cell) || blocked[Transform.Index(cell)];
    public float Clearance(GridCell cell) => Contains(cell) ? clearance[Transform.Index(cell)] : 0f;
    public float HeightAt(GridCell cell) => Contains(cell) ? heights[Transform.Index(cell)] : 0f;
    public float TraversalCost(GridCell cell) => Contains(cell) ? traversalCosts[Transform.Index(cell)] : float.PositiveInfinity;
    public float SpeedMultiplier(GridCell cell) => Contains(cell) ? speedMultipliers[Transform.Index(cell)] : 0f;
    public bool IsWalkable(GridCell cell, float agentRadius) =>
        Contains(cell) && !IsBlocked(cell) && Clearance(cell) >= agentRadius + 0.035f;

    public bool CanTraverse(GridCell from, GridCell to, float agentRadius)
    {
        if (!IsWalkable(from, agentRadius) || !IsWalkable(to, agentRadius)) return false;
        var heightDelta = MathF.Abs(HeightAt(to) - HeightAt(from));
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

    internal void ReplaceRaster(
        bool[] newBlocked,
        float[] newClearance,
        float[] newHeights,
        float[] newTraversalCosts,
        float[] newSpeedMultipliers)
    {
        if (newBlocked.Length != blocked.Length || newClearance.Length != clearance.Length ||
            newHeights.Length != heights.Length || newTraversalCosts.Length != traversalCosts.Length ||
            newSpeedMultipliers.Length != speedMultipliers.Length)
        {
            throw new ArgumentException("Navigation raster dimensions do not match the grid.");
        }
        blocked = newBlocked;
        clearance = newClearance;
        heights = newHeights;
        traversalCosts = newTraversalCosts;
        speedMultipliers = newSpeedMultipliers;
        Revision++;
    }
}
