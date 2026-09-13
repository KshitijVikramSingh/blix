using System.Numerics;

namespace RTSGame.Simulation.Collision;

internal sealed class SpatialHash
{
    private readonly float cellSize;
    private readonly Dictionary<(int X, int Z), List<int>> buckets = new();

    public SpatialHash(float cellSize)
    {
        if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize));
        this.cellSize = cellSize;
    }

    public void Rebuild(IReadOnlyList<ColliderProxy> colliders)
    {
        buckets.Clear();
        foreach (var collider in colliders)
        {
            if (!collider.Enabled) continue;
            collider.Shape.Bounds(collider.Center, out var minimum, out var maximum);
            var minCell = Bucket(minimum);
            var maxCell = Bucket(maximum);
            for (var z = minCell.Z; z <= maxCell.Z; z++)
            for (var x = minCell.X; x <= maxCell.X; x++)
            {
                var key = (x, z);
                if (!buckets.TryGetValue(key, out var values)) buckets[key] = values = new List<int>();
                values.Add(collider.Id.Value);
            }
        }
    }

    public void Gather(Vector2 minimum, Vector2 maximum, HashSet<int> results)
    {
        results.Clear();
        var minCell = Bucket(minimum);
        var maxCell = Bucket(maximum);
        for (var z = minCell.Z; z <= maxCell.Z; z++)
        for (var x = minCell.X; x <= maxCell.X; x++)
        {
            if (!buckets.TryGetValue((x, z), out var values)) continue;
            foreach (var value in values) results.Add(value);
        }
    }

    private (int X, int Z) Bucket(Vector2 point) =>
        ((int)MathF.Floor(point.X / cellSize), (int)MathF.Floor(point.Y / cellSize));
}
