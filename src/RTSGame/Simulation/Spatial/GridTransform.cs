using System.Numerics;

namespace RTSGame.Simulation.Spatial;

internal readonly record struct GridCell(int X, int Z);

internal sealed class GridTransform
{
    public int Width { get; }
    public int Height { get; }
    public float CellSize { get; }
    public Vector2 Origin { get; }
    public Vector2 Maximum => Origin + new Vector2(Width * CellSize, Height * CellSize);

    public GridTransform(int width, int height, float cellSize, Vector2 origin)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize));
        Width = width;
        Height = height;
        CellSize = cellSize;
        Origin = origin;
    }

    public bool Contains(GridCell cell) =>
        cell.X >= 0 && cell.X < Width && cell.Z >= 0 && cell.Z < Height;

    public bool TryWorldToCell(Vector2 world, out GridCell cell)
    {
        var local = world - Origin;
        cell = new GridCell((int)MathF.Floor(local.X / CellSize), (int)MathF.Floor(local.Y / CellSize));
        return Contains(cell);
    }

    public Vector2 CellCenter(GridCell cell) => Origin + new Vector2(
        (cell.X + 0.5f) * CellSize,
        (cell.Z + 0.5f) * CellSize);

    public void CellBounds(GridCell cell, out Vector2 minimum, out Vector2 maximum)
    {
        minimum = Origin + new Vector2(cell.X * CellSize, cell.Z * CellSize);
        maximum = minimum + new Vector2(CellSize);
    }

    public int Index(GridCell cell) => cell.Z * Width + cell.X;

    public GridCell Cell(int index) => new(index % Width, index / Width);
}
