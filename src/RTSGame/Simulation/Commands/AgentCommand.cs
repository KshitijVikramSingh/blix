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
internal sealed record AssignGroupCommand(AgentId[] Agents, Assignment Assignment) : AgentCommand;

internal sealed record StopGroupCommand(AgentId[] Agents) : AgentCommand;

internal sealed record FollowGroupCommand(AgentId[] Agents, AgentId Target) : AgentCommand;

internal sealed record PatrolGroupCommand(AgentId[] Agents, Vector2 End) : AgentCommand;

internal sealed record ChaseGroupCommand(AgentId[] Agents, AgentId Target) : AgentCommand;

internal sealed record FleeGroupCommand(AgentId[] Agents, AgentId Target) : AgentCommand;

internal sealed record ToggleObstacleCommand(GridCell Cell) : AgentCommand;
