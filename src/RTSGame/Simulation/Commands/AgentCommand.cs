using System.Numerics;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Commands;

internal abstract record AgentCommand;

internal sealed record MoveGroupCommand(AgentId[] Agents, Vector2 Target) : AgentCommand;

internal sealed record StopGroupCommand(AgentId[] Agents) : AgentCommand;

internal sealed record FollowGroupCommand(AgentId[] Agents, AgentId Target) : AgentCommand;

internal sealed record PatrolGroupCommand(AgentId[] Agents, Vector2 End) : AgentCommand;

internal sealed record ChaseGroupCommand(AgentId[] Agents, AgentId Target) : AgentCommand;

internal sealed record FleeGroupCommand(AgentId[] Agents, AgentId Target) : AgentCommand;

internal sealed record ToggleObstacleCommand(GridCell Cell) : AgentCommand;
