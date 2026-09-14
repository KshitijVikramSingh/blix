using System.Numerics;
using Blix.Geometry;

namespace Blix.Labs.Character;

/// <summary>One contact a move ran into, kept so the picture can show what the arithmetic did.</summary>
public readonly record struct MoveContact(Vector3 Point, Vector3 Normal, float Time, Vector3 Before, Vector3 After);

/// <summary>What a move actually managed.</summary>
/// <remarks>
/// The residual matters as much as the position. A move that used every iteration and still has
/// motion left is a body wedged in a corner, and the difference between "arrived" and "gave up
/// here" is invisible in a position alone.
/// </remarks>
public readonly record struct MoveResult(
    Vector3 Position,
    Vector3 Residual,
    int Iterations,
    int Depenetrations,
    IReadOnlyList<MoveContact> Contacts);

/// <summary>
/// Moves a capsule through a triangle mesh: sweep, stop at the contact, deflect what is left, repeat.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lab-local, deliberately.</b> <see cref="Intersection.Sweep(Capsule, Vector3, TriangleMesh3D, float)"/>
/// is engine math with one right answer, and <see cref="CollisionResponse.RemoveNormalComponent"/> is the
/// deflection primitive — both already in <c>Blix.Geometry</c>. What is HERE is the loop, and a loop is
/// policy: how many times to deflect, how much of a gap to leave, what to do with a body that starts
/// inside something, and whether to keep the motion it could not spend. Two games would disagree about
/// every one of those. Stage C-C is the stage that decides whether this crosses into the engine, and it
/// decides it by whether the follow camera — a genuinely different consumer — wants the same loop.
/// </para>
/// <para>
/// <b>Deliberately not here:</b> gravity, jumping, ground state, slope limits, step-up. Those are stage
/// R-D, and they are policy on top of policy. This answers exactly one question — given a body and a
/// motion, where does it end up — and answering more would make the stage that finds the right slope
/// limit unable to change it without changing this.
/// </para>
/// </remarks>
public sealed class BodyResolver
{
    /// <summary>
    /// How many times a single move may hit something and carry on.
    /// </summary>
    /// <remarks>
    /// Four covers a floor, a wall, and the two faces of the corner they make — past that a body is
    /// in a crevice, and the honest answer there is to stop with motion left over rather than to
    /// keep deflecting into an ever-smaller wedge. The residual says it happened.
    /// </remarks>
    public int MaxIterations { get; set; } = 4;

    /// <summary>
    /// The gap left between the body and whatever it stopped against.
    /// </summary>
    /// <remarks>
    /// <b>Not cosmetic.</b> A body advanced exactly to its contact starts the next sweep touching,
    /// where the gap is zero and the sweep answers "contact, at time zero" before it has moved —
    /// so the deflected motion never gets spent and the body sticks to the wall it grazed. Backing
    /// off along the direction of travel (rather than along the normal) is what keeps this from
    /// lifting a body off a slope it is resting on.
    /// </remarks>
    public float SkinWidth { get; set; } = 0.005f;

    /// <summary>
    /// How far out of a solid a body already inside one is pushed, per pass.
    /// </summary>
    /// <remarks>
    /// Single-pass depenetration is what the engine's response layer documents as its limit, and a
    /// body touching two solids may need several. The loop allows a few and then gives up, because
    /// a body that cannot be freed in a few passes is in a place the room should not have.
    /// </remarks>
    public int MaxDepenetrations { get; set; } = 4;

    /// <summary>Whether to deflect at all. Off, the body simply stops at its first contact.</summary>
    /// <remarks>
    /// <b>The negative control, and it is a field rather than a test double.</b> Every invariant this
    /// resolver is judged by — never inside a surface, never through one, tangential speed preserved
    /// along a wall — has to be shown to FAIL without the loop, or the suite is asserting properties
    /// of the geometry rather than of the resolver. A test that can only pass is not a test.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// What a contact means: given the motion left and the surface's normal, what is still owed.
    /// </summary>
    /// <remarks>
    /// <b>The seam between the loop and the policy.</b> Null is the plain slide —
    /// <see cref="CollisionResponse.RemoveNormalComponent"/>, the engine's own primitive. A caller
    /// that knows more substitutes its own: <see cref="CharacterMotor"/> passes one that treats a
    /// surface too steep to stand on as a wall, so gravity cannot deflect a body UP a cliff, and a
    /// different one for the fall, because gravity that slides is a body that never comes to rest.
    /// <para>
    /// It lives here rather than the policies living here: the loop is the same loop whatever a
    /// contact means, and putting a slope limit in the resolver would make the stage that finds the
    /// right limit unable to change it without changing this.
    /// </para>
    /// </remarks>
    public Func<Vector3, Vector3, Vector3>? Deflect { get; set; }

    private readonly List<MoveContact> contacts = new();

    /// <summary>Slide <paramref name="body"/> by <paramref name="motion"/> through <paramref name="world"/>.</summary>
    public MoveResult Move(Capsule body, Vector3 motion, TriangleMesh3D world)
    {
        ArgumentNullException.ThrowIfNull(world);
        contacts.Clear();

        var position = Vector3.Zero;          // displacement from the body's starting place
        var remaining = motion;
        var iterations = 0;
        var depenetrations = 0;

        // FREE THE BODY BEFORE MOVING IT. A sweep from inside a solid is meaningless — every
        // direction is already in contact — so a body that starts overlapped is pushed out first,
        // and the motion is not charged for it. Getting stuck is a state the resolver should be
        // able to leave, not one it should refuse to describe.
        while (depenetrations < MaxDepenetrations)
        {
            var here = Offset(body, position);
            if (Intersection.Test(here, world) is not { Depth: > 0f } overlap) break;

            position += overlap.Normal * (overlap.Depth + SkinWidth);
            depenetrations++;
        }

        while (iterations < MaxIterations)
        {
            var distance = remaining.Length();
            if (distance < 1e-7f) break;

            var here = Offset(body, position);
            if (Intersection.Sweep(here, remaining, world) is not { } hit)
            {
                position += remaining;
                remaining = Vector3.Zero;
                break;
            }

            iterations++;

            // Back off along the direction of travel, never along the normal: a body resting on a
            // ramp that is pushed out along the contact normal climbs a little every frame.
            var safeTime = MathF.Max(0f, hit.Time - (SkinWidth / distance));
            var advance = remaining * safeTime;
            var before = remaining;

            position += advance;
            remaining -= advance;

            if (!Enabled)
            {
                // The control: stop dead at the first thing hit. Everything below is the loop.
                contacts.Add(new MoveContact(hit.Point, hit.Normal, hit.Time, before, Vector3.Zero));
                remaining = Vector3.Zero;
                break;
            }

            // DEFLECT WHAT IS LEFT ALONG THE SURFACE. The engine's own primitive, and the reason
            // this loop needs no vector maths of its own: kill the component going INTO the
            // surface, keep the rest. A body walking into a wall at an angle keeps the along-wall
            // half of its speed, which is the difference between sliding past a doorframe and
            // sticking to it.
            remaining = Deflect is { } policy
                ? policy(remaining, hit.Normal)
                : CollisionResponse.RemoveNormalComponent(remaining, hit.Normal);
            contacts.Add(new MoveContact(hit.Point, hit.Normal, hit.Time, before, remaining));
        }

        return new MoveResult(position, remaining, iterations, depenetrations, contacts.ToArray());
    }

    private static Capsule Offset(Capsule body, Vector3 delta) =>
        new(body.PointA + delta, body.PointB + delta, body.Radius);
}
