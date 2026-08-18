namespace RTSGame.Simulation.Agents;

internal readonly record struct AgentId(int Value)
{
    public override string ToString() => $"agent:{Value}";
}
