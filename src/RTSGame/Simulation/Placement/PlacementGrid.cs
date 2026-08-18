using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Placement;

internal sealed class PlacementGrid
{
    private readonly bool[] occupied;

    public GridTransform Transform { get; }

    public int Revision { get; private set; }

    public PlacementGrid(GridTransform transform)
    {
        Transform = transform;
        occupied = new bool[transform.Width * transform.Height];
    }

    public bool IsOccupied(GridCell cell) => !Transform.Contains(cell) || occupied[Transform.Index(cell)];

    public bool SetOccupied(GridCell cell, bool value)
    {
        if (!Transform.Contains(cell)) return false;
        var index = Transform.Index(cell);
        if (occupied[index] == value) return false;
        occupied[index] = value;
        Revision++;
        return true;
    }
}
