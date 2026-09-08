using System.Numerics;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Jobs;
using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Commands;

internal abstract record AgentCommand;

internal sealed record MoveGroupCommand(AgentId[] Agents, Vector2 Target) : AgentCommand;

/// <summary>
/// Gives units a standing commitment, or takes it away with <see cref="Assignment.None"/>.
/// </summary>
/// <remarks>
/// The only command in the list that is not an order. Every other one interrupts what a unit
/// is committed to; this one changes what it is committed to. Clearing an assignment is
/// therefore how a unit is actually taken off work — telling it to stop merely interrupts it,
/// and it goes back to the job when the grace runs out, which is the point of §7.
/// </remarks>
/// <param name="Spread">
/// Whether a crowd sent at one work site may be distributed across nearby places like it.
/// </param>
/// <remarks>
/// <b>A courtesy for a human, and poison for the planner.</b> Somebody clicking one tree with six villagers
/// selected means "cut wood here", so sending them to six trunks is what they meant. The bot means something
/// narrower: its intents reconcile per node — <c>gap = target − (standing + outstanding)</c> — so silently
/// redirecting its hands to a different node leaves the node it asked about permanently short. It reissues,
/// spends every spare pair of hands on the same unsatisfiable intent, and never has anybody left to train.
/// The gate caught exactly that: "faction 1's bot has a barracks and no militia".
/// <para>
/// Hence the flag rather than a rule. Both paths go through the same command, per §132 — the bot plays
/// through the player's own verbs — so the difference has to be carried, not inferred.
/// </para>
/// </remarks>
internal sealed record AssignGroupCommand(
    AgentId[] Agents, Assignment Assignment, bool Spread = false) : AgentCommand;

internal sealed record StopGroupCommand(AgentId[] Agents) : AgentCommand;

internal sealed record FollowGroupCommand(AgentId[] Agents, AgentId Target) : AgentCommand;

internal sealed record PatrolGroupCommand(AgentId[] Agents, Vector2 End) : AgentCommand;

internal sealed record ChaseGroupCommand(AgentId[] Agents, AgentId Target) : AgentCommand;

internal sealed record FleeGroupCommand(AgentId[] Agents, AgentId Target) : AgentCommand;

internal sealed record ToggleObstacleCommand(GridCell Cell) : AgentCommand;
