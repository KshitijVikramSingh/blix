namespace RTSGame.Simulation.Agents;

internal readonly record struct AgentId(int Value)
{
    /// <summary>
    /// No body. Negative, because <c>default(AgentId)</c> is agent zero and agent zero is somebody.
    /// </summary>
    /// <remarks>
    /// <b>§166 learned this the hard way.</b> An attack order with no body target carried <c>default</c>, and
    /// its end condition asked whether the target was still alive — which for agent zero is very often yes.
    /// So an attacker on a building was never released, because it was notionally also hunting the first
    /// villager ever spawned. <see cref="Economy.NodeId.None"/> and
    /// <see cref="Collision.FactionId.None"/> are both negative for exactly this reason and this one was
    /// missing.
    /// </remarks>
    public static AgentId None => new(-1);

    public bool IsValid => Value >= 0;

    public override string ToString() => Value < 0 ? "agent:none" : $"agent:{Value}";
}
