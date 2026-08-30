using System.Numerics;
using RTSGame.Debug;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Commands;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;
using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Persistence;

/// <summary>
/// Saves a world and loads it back as the identical world.
/// </summary>
/// <remarks>
/// §5 of the design makes this core loop rather than a save feature: a career ends, usually
/// violently, and the next one begins on the same map, which has not reset. Old roads still carry
/// their speed multiplier, cleared land is still cleared, a burnt palisade is still a line the enemy
/// must route around. None of that is possible without the world outliving the process.
/// <para>
/// <b>What "identical" has to mean.</b> Not "looks the same" and not "close enough" — the acceptance
/// test is that the determinism fingerprint of the loaded world equals the fingerprint of the saved
/// one, and then that both of them, ticked forward together, stay equal. The second half is the one
/// that finds things. A save can round-trip every value the fingerprint reads and still resume as a
/// different world, because the fingerprint's job is to <em>detect</em> divergence and a save's job
/// is to <em>reproduce the future</em>, and the second is strictly harder. Two places in this
/// codebase show it: the congestion field's running totals, which decide when the next revision
/// publishes, and the path pool's free list, which decides which handle the next route takes. The
/// fingerprint reads neither and is right not to. A save that skipped them resumes and diverges on
/// the next tick.
/// </para>
/// <para>
/// <b>What is not saved.</b> Anything the ledger in <see cref="DeterminismCheck"/> calls derived,
/// which is the same set for the same reason: caches keyed by a revision, per-tick scratch, the
/// broad-phase index, and the navigation raster — that last one because it is a pure function of the
/// terrain and the placement grid, so rebuilding it on load costs milliseconds and saves megabytes.
/// The revision it was built at <em>is</em> saved, because every flow field ever cached is keyed by
/// it. Wall clock is not saved and must not be.
/// </para>
/// </remarks>
internal static class WorldSave
{
    /// <summary>"BLIXRTS0" — so a wrong file is refused rather than interpreted.</summary>
    private const long Magic = 0x3053_5452_5849_4C42;

    /// <summary>
    /// Bumped whenever the byte layout changes in a way an older save cannot satisfy.
    /// </summary>
    private const int Version = 6;

    /// <summary>
    /// Kinds of order that can be sitting in the queue when a save is taken.
    /// </summary>
    /// <remarks>
    /// A tag per kind rather than a type name, so the format does not depend on how classes are
    /// spelled. Adding a command without adding it here throws on save — see
    /// <c>SimulationSelfTests.EveryOrderKindSurvivesASave</c>, which reflects over the command
    /// hierarchy and fails if any kind is unaccounted for, so the discovery happens in the session
    /// that adds it rather than the first time somebody saves mid-order.
    /// </remarks>
    private enum CommandTag
    {
        Move,
        Stop,
        Follow,
        Patrol,
        Chase,
        Flee,
        ToggleObstacle,
        Assign,
    }

    public static void Save(SimulationWorld world, Stream stream)
    {
        using var writer = new WorldWriter(stream);
        writer.Long(Magic);
        writer.Int(Version);
        // The layout of a body, as the determinism schema sees it. Agent state is saved as raw
        // bytes, so a save written by a build whose AgentState has a different shape cannot be
        // loaded by this one — and would load as plausible garbage rather than failing, which is the
        // one outcome worth spending eight bytes to make impossible.
        writer.Int(BodyLayoutSignature());
        writer.Float(world.ExtentMeters);
        world.Write(writer);
    }

    /// <summary>Loads a world saved by <see cref="Save"/>.</summary>
    public static SimulationWorld Load(Stream stream)
    {
        using var reader = new WorldReader(stream);
        if (reader.Long() != Magic) throw new InvalidDataException("Not an RTSGame save.");
        var version = reader.Int();
        if (version != Version)
        {
            throw new InvalidDataException($"Save is version {version}; this build reads {Version}.");
        }

        var layout = reader.Int();
        if (layout != BodyLayoutSignature())
        {
            throw new InvalidDataException(
                "This save was written by a build whose unit or node state had a different shape, so " +
                "its bodies cannot be read back. Saves do not survive a change to AgentState or to " +
                "EconomyNode — see WorldSave.Version.");
        }

        var world = new SimulationWorld(reader.Float());
        world.Read(reader);
        return world;
    }

    /// <summary>Round-trips a world through memory, for tests and for a quick continuation.</summary>
    public static SimulationWorld RoundTrip(SimulationWorld world)
    {
        using var buffer = new MemoryStream();
        Save(world, buffer);
        buffer.Position = 0;
        return Load(buffer);
    }

    /// <summary>
    /// A number that changes whenever the shape of a body changes.
    /// </summary>
    /// <remarks>
    /// Built from the determinism schema — the count of a body's values and their dotted names,
    /// plus its size in bytes — because that schema is derived from the struct itself and therefore
    /// cannot fall out of step with it. A hand-maintained version number would need bumping by
    /// whoever added a field, in a different commit from the one that added it, which is the exact
    /// failure mode the determinism work was undertaken to remove.
    /// </remarks>
    private static int BodyLayoutSignature()
    {
        var signature = new FoldingStateSink();
        signature.Add("size", (ulong)System.Runtime.InteropServices.Marshal.SizeOf<AgentState>());
        // A node's size too, because nodes are saved as a blob and a node that grew a field would
        // otherwise be read back as plausible garbage — every field after the new one shifted by four
        // bytes, silently, with no version to bump because nobody remembered to bump it. Adding
        // Growth and Privation to a house is exactly that change, and this closes the hole for the
        // next one.
        signature.Add("node", (ulong)System.Runtime.InteropServices.Marshal.SizeOf<EconomyNode>());
        foreach (var leaf in AgentStateSchema.Leaves)
        {
            foreach (var character in leaf.Name) signature.Add("name", character);
        }

        return (int)signature.Value;
    }

    internal static void WriteCommand(WorldWriter writer, AgentCommand command)
    {
        switch (command)
        {
            case MoveGroupCommand move:
                writer.Int((int)CommandTag.Move);
                writer.Blob<AgentId>(move.Agents);
                writer.Vector(move.Target);
                return;
            case StopGroupCommand stop:
                writer.Int((int)CommandTag.Stop);
                writer.Blob<AgentId>(stop.Agents);
                return;
            case FollowGroupCommand follow:
                writer.Int((int)CommandTag.Follow);
                writer.Blob<AgentId>(follow.Agents);
                writer.Int(follow.Target.Value);
                return;
            case PatrolGroupCommand patrol:
                writer.Int((int)CommandTag.Patrol);
                writer.Blob<AgentId>(patrol.Agents);
                writer.Vector(patrol.End);
                return;
            case ChaseGroupCommand chase:
                writer.Int((int)CommandTag.Chase);
                writer.Blob<AgentId>(chase.Agents);
                writer.Int(chase.Target.Value);
                return;
            case FleeGroupCommand flee:
                writer.Int((int)CommandTag.Flee);
                writer.Blob<AgentId>(flee.Agents);
                writer.Int(flee.Target.Value);
                return;
            case ToggleObstacleCommand toggle:
                writer.Int((int)CommandTag.ToggleObstacle);
                writer.Int(toggle.Cell.X);
                writer.Int(toggle.Cell.Z);
                return;
            case AssignGroupCommand assign:
                writer.Int((int)CommandTag.Assign);
                writer.Blob<AgentId>(assign.Agents);
                WriteAssignment(writer, assign.Assignment);
                return;
            default:
                throw new NotSupportedException(
                    $"{command.GetType().Name} has no save format. Add a CommandTag for it and a " +
                    "case here, or an order in flight is silently dropped by every save.");
        }
    }

    internal static AgentCommand ReadCommand(WorldReader reader) => (CommandTag)reader.Int() switch
    {
        CommandTag.Move => new MoveGroupCommand(reader.Blob<AgentId>(), reader.Vector()),
        CommandTag.Stop => new StopGroupCommand(reader.Blob<AgentId>()),
        CommandTag.Follow => new FollowGroupCommand(reader.Blob<AgentId>(), new AgentId(reader.Int())),
        CommandTag.Patrol => new PatrolGroupCommand(reader.Blob<AgentId>(), reader.Vector()),
        CommandTag.Chase => new ChaseGroupCommand(reader.Blob<AgentId>(), new AgentId(reader.Int())),
        CommandTag.Flee => new FleeGroupCommand(reader.Blob<AgentId>(), new AgentId(reader.Int())),
        CommandTag.ToggleObstacle => new ToggleObstacleCommand(new GridCell(reader.Int(), reader.Int())),
        CommandTag.Assign => ReadAssign(reader),
        var tag => throw new InvalidDataException($"Unknown order tag {tag} in the save."),
    };

    /// <summary>
    /// Every field of an assignment, because four of them was not enough.
    /// </summary>
    /// <remarks>
    /// This used to write the kind, both anchors and the dwell — which was the whole of an assignment when
    /// the only ones a player could issue were a post and a shuttle, both of which are two points and a
    /// duration. It has not been the whole of one for three stages: a haul names two nodes and a cargo, a
    /// shift of work names its site and has a different duration at each end, and a route names both.
    /// Everything unwritten came back as <c>default</c> — which for a node id is node <em>zero</em>, so an
    /// order in flight across a save turned into an order about the first node in the world.
    /// <para>
    /// It went unnoticed because the round-trip test queued a shuttle, whose unwritten fields were all
    /// already default. Making <see cref="Assignment.Hold"/> and <see cref="Assignment.Shuttle"/> say
    /// <c>NodeId.None</c> instead of leaving it to default is what finally made the two differ.
    /// </para>
    /// </remarks>
    private static void WriteAssignment(WorldWriter writer, Assignment assignment)
    {
        writer.Int((int)assignment.Kind);
        writer.Vector(assignment.Anchor);
        writer.Vector(assignment.FarAnchor);
        writer.Float(assignment.DwellSeconds);
        writer.Int(assignment.Source.Value);
        writer.Int(assignment.Sink.Value);
        writer.Int((int)assignment.Cargo);
        writer.Float(assignment.PlaceExtent);
        writer.Float(assignment.FarPlaceExtent);
        writer.Float(assignment.FarDwellSeconds);
    }

    private static AgentCommand ReadAssign(WorldReader reader)
    {
        var agents = reader.Blob<AgentId>();
        var kind = (AssignmentKind)reader.Int();
        var anchor = reader.Vector();
        var farAnchor = reader.Vector();
        var dwell = reader.Float();
        var source = new NodeId(reader.Int());
        var sink = new NodeId(reader.Int());
        var cargo = (Resource)reader.Int();
        var extent = reader.Float();
        var farExtent = reader.Float();
        var farDwell = reader.Float();
        return new AssignGroupCommand(
            agents,
            new Assignment(kind, anchor, farAnchor, dwell, source, sink, cargo, extent, farExtent, farDwell));
    }

    /// <summary>Every kind of order the save format handles, for the census that keeps it honest.</summary>
    internal static IReadOnlySet<string> SavedCommandKinds { get; } = new HashSet<string>
    {
        nameof(MoveGroupCommand), nameof(StopGroupCommand), nameof(FollowGroupCommand),
        nameof(PatrolGroupCommand), nameof(ChaseGroupCommand), nameof(FleeGroupCommand),
        nameof(ToggleObstacleCommand), nameof(AssignGroupCommand),
    };
}
