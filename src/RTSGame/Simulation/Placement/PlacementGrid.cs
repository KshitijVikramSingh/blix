using System.Numerics;
using RTSGame.Simulation.Persistence;
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
        // <b>What changed, not just that something did.</b> The navigation raster is rebuilt whenever this
        // grid moves, and a full rebuild is 774 ms on a village against the few metres of ground a building
        // actually covers. Accumulated as one rectangle rather than a list: two buildings finishing in the
        // same tick want one slightly larger window, not two passes, and a rectangle cannot get the answer
        // wrong — only larger than it needed to be.
        Transform.CellBounds(cell, out var cellMinimum, out var cellMaximum);
        if (dirtyPending)
        {
            dirtyMinimum = Vector2.Min(dirtyMinimum, cellMinimum);
            dirtyMaximum = Vector2.Max(dirtyMaximum, cellMaximum);
        }
        else
        {
            dirtyMinimum = cellMinimum;
            dirtyMaximum = cellMaximum;
            dirtyPending = true;
        }

        return true;
    }

    private Vector2 dirtyMinimum;
    private Vector2 dirtyMaximum;
    private bool dirtyPending;

    /// <summary>
    /// Takes the rectangle covering everything that has changed since the last time it was taken.
    /// </summary>
    /// <remarks>
    /// Consuming rather than peeking, because the only correct use is "re-rasterise this and then forget it":
    /// a reader that looked without clearing would re-do the same window forever, and one that cleared
    /// without re-rasterising would leave the grid stale with nothing to say so.
    /// </remarks>
    public bool ConsumeDirtyBounds(out Vector2 minimum, out Vector2 maximum)
    {
        minimum = dirtyMinimum;
        maximum = dirtyMaximum;
        var pending = dirtyPending;
        dirtyPending = false;
        return pending;
    }

    /// <summary>What is built on, and the revision anything cached against it is keyed by.</summary>
    internal void Write(WorldWriter writer)
    {
        writer.Int(Revision);
        writer.Blob<bool>(occupied);
        writer.Blob<GridCell>(occupiedCells.ToArray());
    }

    internal void Read(WorldReader reader)
    {
        Revision = reader.Int();
        reader.Blob<bool>(occupied);
        occupiedCells.Clear();
        occupiedCells.AddRange(reader.Blob<GridCell>());
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
