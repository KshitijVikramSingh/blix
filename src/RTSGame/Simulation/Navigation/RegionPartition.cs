using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Navigation;

/// <summary>
/// Fixed square partition of the fine navigation grid. It is the unit of every
/// hierarchical thing above that grid: portals sit on region borders, abstract edge
/// costs are searched one region at a time, and a flow field is stored as one tile
/// per region instead of one array per map.
/// </summary>
/// <remarks>
/// 64 cells a side — 32 m on the 0.5 m fine grid — is not a round number picked for
/// tidiness. It makes a region 4,096 cells, deliberately comparable to the 3,600-cell
/// world every constant in <c>plan-rts.md</c> was measured on, so a search bounded to
/// one region is a search of the size this codebase already knows how to reason about.
/// <para>
/// It also makes the tuned 30 m world exactly one region, with no borders and therefore
/// no portals, which is the property that lets the hierarchy land without moving a single
/// number in the existing suite: on that world the abstract layer has nothing to say and
/// the local tile is the whole map.
/// </para>
/// <para>
/// Edge regions are partial — a 1602-cell grid is 25 regions of 64 and one of 2 — but a
/// tile is always allocated at full size. 16 KB either way, and an index that never has to
/// ask how wide the region it lands in happens to be.
/// </para>
/// </remarks>
internal sealed class RegionPartition
{
    /// <summary>Fine cells along one side of a region.</summary>
    public const int CellsPerSide = 64;
    /// <summary>Fine cells in a full region, which is also a tile's length.</summary>
    public const int CellsPerRegion = CellsPerSide * CellsPerSide;

    private readonly GridTransform transform;

    public int Columns { get; }
    public int Rows { get; }
    public int Count => Columns * Rows;

    public RegionPartition(GridTransform transform)
    {
        this.transform = transform;
        Columns = (transform.Width + CellsPerSide - 1) / CellsPerSide;
        Rows = (transform.Height + CellsPerSide - 1) / CellsPerSide;
    }

    public int RegionOf(GridCell cell) =>
        cell.Z / CellsPerSide * Columns + cell.X / CellsPerSide;

    public int Column(int region) => region % Columns;
    public int Row(int region) => region / Columns;

    /// <summary>Inclusive cell bounds of a region, clipped to the grid.</summary>
    public void Bounds(
        int region,
        out int minimumX,
        out int minimumZ,
        out int maximumX,
        out int maximumZ)
    {
        minimumX = Column(region) * CellsPerSide;
        minimumZ = Row(region) * CellsPerSide;
        maximumX = Math.Min(minimumX + CellsPerSide, transform.Width) - 1;
        maximumZ = Math.Min(minimumZ + CellsPerSide, transform.Height) - 1;
    }

    public bool Contains(int region, GridCell cell)
    {
        Bounds(region, out var minimumX, out var minimumZ, out var maximumX, out var maximumZ);
        return cell.X >= minimumX && cell.X <= maximumX &&
               cell.Z >= minimumZ && cell.Z <= maximumZ;
    }

    /// <summary>Index of a cell within its region's tile.</summary>
    public int TileIndex(GridCell cell) =>
        cell.Z % CellsPerSide * CellsPerSide + cell.X % CellsPerSide;
}
