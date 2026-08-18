using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Placement;

internal sealed class PlacementGrid
{
    private readonly bool[] occupied;
    private readonly List<GridCell> occupiedCells = new();

    public GridTransform Transform { get; }

    public int Revision { get; private set; }

    public PlacementGrid(GridTransform transform)
    {
        Transform = transform;
        occupied = new bool[transform.Width * transform.Height];
    }

    public bool IsOccupied(GridCell cell) => !Transform.Contains(cell) || occupied[Transform.Index(cell)];

    /// <summary>Occupied cells, in ascending index order.</summary>
    /// <remarks>
    /// Kept as a list because the renderer wants to draw the blocks and scanning the grid for
    /// them costs the area of the map every frame — free at 400 cells, 640,000 checks at 1200 m
    /// to find the handful of walls somebody actually built.
    /// </remarks>
    public IReadOnlyList<GridCell> OccupiedCells => occupiedCells;

    public bool SetOccupied(GridCell cell, bool value)
    {
        if (!Transform.Contains(cell)) return false;
        var index = Transform.Index(cell);
        if (occupied[index] == value) return false;
        occupied[index] = value;
        // Kept sorted by cell index, so every reader of the list sees the same order however
        // the cells were placed. Insertion is a binary search rather than a re-sort because a
        // wall is built one cell at a time.
        var position = occupiedCells.BinarySearch(cell, GridCellIndexOrder.Instance);
        if (value)
        {
            if (position < 0) occupiedCells.Insert(~position, cell);
        }
        else if (position >= 0)
        {
            occupiedCells.RemoveAt(position);
        }

        Revision++;
        return true;
    }

    private sealed class GridCellIndexOrder : IComparer<GridCell>
    {
        public static readonly GridCellIndexOrder Instance = new();

        public int Compare(GridCell first, GridCell second)
        {
            var byRow = first.Z.CompareTo(second.Z);
            return byRow != 0 ? byRow : first.X.CompareTo(second.X);
        }
    }
}
