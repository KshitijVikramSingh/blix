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
