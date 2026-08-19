using RTSGame.Simulation.Persistence;

namespace RTSGame.Simulation.Collision;

internal sealed class FactionRelations
{
    private readonly Dictionary<(int Source, int Target), RelationMask> overrides = new();

    public RelationMask Between(FactionId source, FactionId target)
    {
        if (source == FactionId.None || target == FactionId.None) return RelationMask.Neutral;
        if (overrides.TryGetValue((source.Value, target.Value), out var relationship)) return relationship;
        return source == target ? RelationMask.Ally : RelationMask.Enemy;
    }

    /// <summary>Only what has been overridden; everything else is the ally-or-enemy default.</summary>
    internal void Write(WorldWriter writer)
    {
        writer.Int(overrides.Count);
        foreach (var (pair, relationship) in overrides.OrderBy(entry => entry.Key.Source)
                     .ThenBy(entry => entry.Key.Target))
        {
            writer.Int(pair.Source);
            writer.Int(pair.Target);
            writer.Int((int)relationship);
        }
    }

    internal void Read(WorldReader reader)
    {
        overrides.Clear();
        var count = reader.Int();
        for (var i = 0; i < count; i++)
        {
            var source = reader.Int();
            var target = reader.Int();
            overrides[(source, target)] = (RelationMask)reader.Int();
        }
    }

    public void Set(FactionId first, FactionId second, RelationMask relationship)
    {
        if (relationship is not (RelationMask.Ally or RelationMask.Neutral or RelationMask.Enemy))
        {
            throw new ArgumentOutOfRangeException(nameof(relationship), "A faction relationship must be ally, neutral or enemy.");
        }
        overrides[(first.Value, second.Value)] = relationship;
        overrides[(second.Value, first.Value)] = relationship;
    }
}
