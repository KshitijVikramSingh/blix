using System.Numerics;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Labs.Character;

/// <summary>
/// One named piece of the room, and the claim it exists to make.
/// </summary>
/// <remarks>
/// A part is a range of triangles, not a transform and a mesh — the room is already in world space and
/// has no model matrices. The claims are what turn the room from scenery into an instrument: a ramp
/// whose slope is "about thirty degrees" tests nothing, because a resolver that comes to rest at 31°
/// and one that slides at 29° would both look right.
/// </remarks>
public sealed record RoomPart(
    string Name,
    int FirstTriangle,
    int TriangleCount,
    Vector3 Colour,
    /// <summary>What every upward-facing triangle in this part should measure, in degrees from flat.</summary>
    float? WalkSlopeDegrees = null,
    /// <summary>The exact rise between consecutive walkable levels, for a flight of steps.</summary>
    float? RiserHeight = null,
    /// <summary>The exact clear distance between this part's two solids.</summary>
    float? ClearWidth = null,
    /// <summary>The height of the walkable top above the floor, for a ledge or a beam's underside.</summary>
    float? TopHeight = null,
    /// <summary>Radius of curvature, for the dome — slope at horizontal distance r is asin(r / R).</summary>
    float? CurvatureRadius = null,
    Vector3 Centre = default);

/// <summary>
/// The room: every surface a body can stand on, slide down, trip over or fail to climb.
/// </summary>
/// <remarks>
/// <para>
/// <b>One source.</b> <see cref="Vertices"/> and <see cref="Collider"/> are built from the same
/// triangles in the same pass. Nothing here authors a collider beside a mesh — the recurring lesson of
/// the last arc is that two things which should agree and cannot contradict each other hide bugs for
/// weeks, and "the collider is not quite the floor you can see" is the single most expensive version of
/// that in a physics lab.
/// </para>
/// <para>
/// <b>Built, not imported.</b> Every feature's ground truth is a closed-form number known before
/// anything runs: this ramp is exactly 30°, this riser is exactly 0.2 m, this gap is exactly 0.6 m
/// wide. An imported level would make each of those a measurement, and then a resolver bug and an
/// asset bug would be indistinguishable.
/// </para>
/// <para>
/// <b>Deliberately not here:</b> moving platforms, triggers, doors, materials with friction, anything
/// destructible. The room is a set of static claims. A body that cannot yet walk up a fixed ramp has no
/// business meeting a moving one.
/// </para>
/// </remarks>
public sealed class Room
{
    /// <summary>Half-extent of the floor in X, and the inside face of the east and west walls.</summary>
    public const float HallHalfX = 14f;

    /// <summary>Half-extent of the floor in Z.</summary>
    public const float HallHalfZ = 9f;

    private Room(
        IReadOnlyList<RoomPart> parts,
        IReadOnlyList<int> solidStarts,
        VertexPosition3NormalTexture[] vertices,
        Vector3[] positions,
        TriangleMesh3D collider)
    {
        Parts = parts;
        SolidStarts = solidStarts;
        Vertices = vertices;
        Positions = positions;
        Collider = collider;
    }

    public IReadOnlyList<RoomPart> Parts { get; }

    /// <summary>First triangle of each closed solid. See <see cref="SolidBuilder.SolidStarts"/>.</summary>
    public IReadOnlyList<int> SolidStarts { get; }

    /// <summary>Render vertices, three per triangle, flat-shaded. Index i is triangle i/3.</summary>
    /// <remarks>
    /// No index buffer: every triangle owns its three vertices because the shading is flat and adjacent
    /// faces share no normal. Sharing vertices would mean sharing a normal, which is the smoothing this
    /// room deliberately does not do.
    /// </remarks>
    public VertexPosition3NormalTexture[] Vertices { get; }

    /// <summary>The same triangles' positions, in the same order — what the collider was built from.</summary>
    public Vector3[] Positions { get; }

    /// <summary>The collider, built from <see cref="Positions"/>. Not authored separately.</summary>
    public TriangleMesh3D Collider { get; }

    public int TriangleCount => Positions.Length / 3;

    /// <summary>Where a body should start: on the floor, clear of everything, facing the ramp fan.</summary>
    /// <remarks>
    /// Moved once already, by the probe rather than by eye: turning the ramp fan around put its foot
    /// where the old spawn stood. A spawn point inside a solid is a bug that looks exactly like a
    /// resolver bug on the first frame of every run, which is why it is a claim the probe checks.
    /// </remarks>
    public static Vector3 SpawnPoint => new(-1f, 0f, 0f);

    /// <summary>
    /// Nothing but a floor. What the Motion lab stands on.
    /// </summary>
    /// <remarks>
    /// <b>The isolation the plan asks for, as a method.</b> Motion is about clips and states, and a
    /// body that walks into a ramp while its clip is wrong gives you two suspects for one symptom.
    /// The floor is the same floor the full room has, built by the same code, so nothing about
    /// standing on it can differ between the two labs.
    /// </remarks>
    public static Room FlatGround()
    {
        var builder = new SolidBuilder();
        builder.Box(new(-HallHalfX, -0.5f, -HallHalfZ), new(HallHalfX, 0f, HallHalfZ));

        var parts = new List<RoomPart>
        {
            new("floor", 0, builder.TriangleCount, new Vector3(0.42f, 0.44f, 0.47f), WalkSlopeDegrees: 0f),
        };

        return Assemble(parts, builder);
    }

    public static Room Build()
    {
        var builder = new SolidBuilder();
        var parts = new List<RoomPart>();

        void Part(
            string name, Vector3 colour, Action build,
            float? slope = null, float? riser = null, float? clear = null,
            float? top = null, float? radius = null, Vector3 centre = default)
        {
            var first = builder.TriangleCount;
            build();
            parts.Add(new RoomPart(
                name, first, builder.TriangleCount - first, colour,
                slope, riser, clear, top, radius, centre));
        }

        Part("floor", new Vector3(0.42f, 0.44f, 0.47f),
            () => builder.Box(new(-HallHalfX, -0.5f, -HallHalfZ), new(HallHalfX, 0f, HallHalfZ)),
            slope: 0f);

        // TWO WALLS, MEETING AT A CORNER — one long, one short. Reported from the chair: four of them
        // is a prison, and a room you can only look into from above is a room you cannot read. An L
        // gives the hall a back and a side to sit against and leaves the other two open, so a camera
        // outside the corner sees every surface face-on instead of over a parapet.
        //
        // Three metres rather than five, for the same reason: a wall exists here to stop a body and
        // to catch a shadow, and every centimetre above that is something between you and the room.
        Part("walls", new Vector3(0.30f, 0.31f, 0.34f), () =>
        {
            const float t = 0.5f, h = 3f;
            builder.Box(new(-HallHalfX - t, 0f, -HallHalfZ - t), new(HallHalfX + t, h, -HallHalfZ));
            builder.Box(new(-HallHalfX - t, 0f, -HallHalfZ), new(-HallHalfX, h, HallHalfZ));
        });

        // THE RAMP FAN. One run, five rises — so the angles are exact ratios rather than the result of
        // rounding a tangent, and the pair that straddles a plausible slope limit (30 and 45) sit beside
        // each other where one run can try both.
        //
        // <b>They rise toward the closed corner, so their faces look into the open hall.</b> Twice now
        // the fan has been pointed the wrong way: first into the west wall, where the walkable faces
        // had a metre of floor in front of them, and then away from the only open side, where they
        // showed five vertical backs to anyone looking. Neither was a claim the probe could check —
        // every angle was exact both times. A ramp you approach from the wrong side is a ramp nobody
        // climbs.
        //
        // The run is 2.5 m because the rise is what it costs: at 60° a 3 m run stands 5.2 m tall,
        // which is taller than the walls.
        const float rampRun = 2.5f;
        const float rampWidth = 2.2f;
        foreach (var (degrees, zCentre) in new[] { (5f, -7.2f), (15f, -3.6f), (30f, 0f), (45f, 3.6f), (60f, 7.2f) })
        {
            var rise = rampRun * MathF.Tan(degrees * MathF.PI / 180f);
            var z = zCentre;
            Part($"ramp-{degrees:00}", RampColour(degrees),
                () => builder.Ramp(new Vector3(-8.5f, 0f, z), -Vector3.UnitX, rampRun, rise, rampWidth),
                slope: degrees);
        }

        // THE STAIRS. Three flights whose risers straddle where a step-up rule has to draw its line:
        // a 0.1 m lip is a stumble, a 0.3 m riser is a climb, and 0.2 is where the argument is.
        foreach (var (riser, zCentre) in new[] { (0.10f, -6f), (0.20f, -2f), (0.30f, 2f) })
        {
            var r = riser;
            var z = zCentre;
            Part($"stairs-{riser * 100f:00}", new Vector3(0.52f, 0.45f, 0.38f), () =>
            {
                // Each step is a box from the flight's far (high) end back toward the foot, one tread
                // shorter than the last — so every tread below the top stays exposed instead of being
                // buried under the step above it. Climbs west, like the ramps, and is met from the
                // open hall.
                //
                // <b>0.7 m deep, which is DOUBLE a real stair's tread and the point of the number.</b>
                // The first version used 0.35 — the same as the body's radius — and no body could
                // climb it: a capsule that lands its axis on such a tread is already touching the
                // next riser, so every step attempt ended perched on an edge at 75° and was rejected
                // as unstandable. A tread has to be deeper than the body is wide or there is nowhere
                // on it to stand, and that is a property of the pair rather than of either. Games
                // build stairs deeper than buildings do for exactly this reason.
                const float tread = 0.7f;
                const float foot = -3.2f;
                for (var step = 1; step <= 6; step++)
                {
                    builder.Box(
                        new(foot - (6 * tread), 0f, z - 1f),
                        new(foot - ((step - 1) * tread), step * r, z + 1f));
                }
            }, riser: r);
        }

        Part("ledge", new Vector3(0.46f, 0.40f, 0.52f),
            () => builder.Box(new(1f, 0f, -7.5f), new(6f, 1.2f, -2.5f)),
            slope: 0f, top: 1.2f);

        // A bar to walk under and hit your head on: the case a capsule's TOP cap decides, which a
        // ground-only controller gets wrong without ever failing a slope test.
        Part("beam", new Vector3(0.55f, 0.47f, 0.30f),
            () => builder.Box(new(0f, 1.2f, 2.2f), new(8f, 1.6f, 2.6f)),
            top: 1.2f);

        // Two walls 0.6 m apart. Wider than a 0.35 m capsule and narrower than two of them, so a
        // resolver that depenetrates along the wrong axis squeezes the body out sideways here first.
        Part("gap", new Vector3(0.38f, 0.48f, 0.44f), () =>
        {
            builder.Box(new(7f, 0f, 4.5f), new(9f, 2f, 6.5f));
            builder.Box(new(7f, 0f, 7.1f), new(9f, 2f, 8.6f));
        }, clear: 0.6f);

        var domeCentre = new Vector3(11f, 0f, -4f);
        Part("dome", new Vector3(0.36f, 0.50f, 0.56f),
            () => builder.Dome(domeCentre, 3f, 0f),
            radius: 3f, centre: domeCentre);

        return Assemble(parts, builder);
    }

    /// <summary>Turn a finished builder into the render vertices and the collider, from one source.</summary>
    private static Room Assemble(IReadOnlyList<RoomPart> parts, SolidBuilder builder)
    {
        var positions = builder.Positions.ToArray();
        var normals = builder.Normals.ToArray();

        var vertices = new VertexPosition3NormalTexture[positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            var p = positions[i];
            var n = normals[i];
            vertices[i] = new VertexPosition3NormalTexture(
                new GraphicsVector3(p.X, p.Y, p.Z),
                new GraphicsVector3(n.X, n.Y, n.Z),
                // A world-space planar UV, so a texture would tile with the room rather than with the
                // triangle. Nothing samples it yet; the layout the lit pipeline takes declares one.
                new GraphicsVector2(p.X * 0.25f, p.Z * 0.25f));
        }

        var triangles = new Triangle[positions.Length / 3];
        for (var i = 0; i < triangles.Length; i++)
        {
            triangles[i] = new Triangle(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
        }

        return new Room(parts, builder.SolidStarts.ToArray(), vertices, positions, new TriangleMesh3D(triangles));
    }

    /// <summary>The triangle at <paramref name="index"/>, as the collider holds it.</summary>
    public Triangle TriangleAt(int index) =>
        new(Positions[index * 3], Positions[index * 3 + 1], Positions[index * 3 + 2]);

    /// <summary>The normal the BUILDER claimed for this triangle's face.</summary>
    public Vector3 ClaimedNormal(int index) => Vertices[index * 3] is var v
        ? Vector3.Normalize(new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z))
        : Vector3.UnitY;

    /// <summary>The normal this triangle's WINDING produces — what a collider reads.</summary>
    public Vector3 WoundNormal(int index)
    {
        var t = TriangleAt(index);
        return Vector3.Normalize(Vector3.Cross(t.V1 - t.V0, t.V2 - t.V0));
    }

    /// <summary>Degrees from flat, for an upward-facing surface. 0 is a floor, 90 is a wall.</summary>
    public static float SlopeDegrees(Vector3 normal) =>
        MathF.Acos(Math.Clamp(normal.Y, -1f, 1f)) * 180f / MathF.PI;

    private static Vector3 RampColour(float degrees) => degrees switch
    {
        <= 5f => new Vector3(0.30f, 0.55f, 0.35f),
        <= 15f => new Vector3(0.40f, 0.58f, 0.32f),
        <= 30f => new Vector3(0.62f, 0.58f, 0.28f),
        <= 45f => new Vector3(0.66f, 0.42f, 0.24f),
        _ => new Vector3(0.62f, 0.26f, 0.24f),
    };
}
