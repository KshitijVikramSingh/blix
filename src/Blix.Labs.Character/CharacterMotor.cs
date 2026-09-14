using System.Numerics;
using Blix.Geometry;

namespace Blix.Labs.Character;

/// <summary>
/// A body that stands, walks, climbs, slides and falls — the policy on top of the resolver.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every number here is a decision, not a fact.</b> <see cref="BodyResolver"/> answers where a
/// motion gets to and has one right answer; what counts as ground, how steep is too steep, how tall
/// a step is climbable and whether gravity may push a body sideways are all things two games would
/// answer differently. That is why they are fields on this type with a panel behind them: a lab is
/// where the right number is FOUND, and the last arc's lesson about guessing one twice (the gizmo
/// trail's six seconds) applies to every one of them.
/// </para>
/// <para>
/// <b>Order matters and is the whole design.</b> Probe the ground, lay the walk along it, move
/// horizontally with steep faces treated as walls, climb a step if something low blocked it, fall,
/// then stick to the ground if it is still within reach. Doing gravity before the walk makes a body
/// on a slope slide before it can push into the hill; doing the ground probe after the walk makes
/// every slope decision one frame stale.
/// </para>
/// </remarks>
public sealed class CharacterMotor
{
    private readonly BodyResolver resolver = new();
    private readonly List<MoveContact> contacts = new();

    public float Radius { get; set; } = 0.35f;

    public float Height { get; set; } = 1.8f;

    /// <summary>
    /// The steepest surface a body can stand on. Above it, it slides.
    /// </summary>
    /// <remarks>
    /// 46°, deliberately between the room's 45° ramp and its 60° one and NOT on either — a limit
    /// that sits exactly on a test surface makes every run a coin toss on the last bit of a float.
    /// The room's fan exists to let this be moved and the consequence watched rather than argued.
    /// </remarks>
    public float SlopeLimitDegrees { get; set; } = 46f;

    /// <summary>How high a lip the body can climb without jumping.</summary>
    /// <remarks>
    /// 0.35 clears the room's 0.10, 0.20 and 0.30 risers and refuses its 0.50 ledge test, which is
    /// the pair of answers a step rule exists to give. It is also the body's radius, which is a
    /// coincidence worth not relying on.
    /// </remarks>
    public float StepHeight { get; set; } = 0.35f;

    public float Gravity { get; set; } = 20f;

    public float WalkSpeed { get; set; } = 3.5f;

    /// <summary>How fast a body slides down ground too steep to stand on.</summary>
    public float SlideSpeed { get; set; } = 6f;

    /// <summary>Where the feet are. The capsule is built from this.</summary>
    public Vector3 Feet { get; set; }

    public bool Grounded { get; private set; }

    public Vector3 GroundNormal { get; private set; } = Vector3.UnitY;

    public float GroundSlopeDegrees => Room.SlopeDegrees(GroundNormal);

    /// <summary>Grounded, and on something shallow enough to stand on.</summary>
    public bool Standing => Grounded && GroundSlopeDegrees <= SlopeLimitDegrees;

    public float VerticalSpeed { get; private set; }

    /// <summary>Whether the last step climbed something rather than walking round it.</summary>
    public bool SteppedUp { get; private set; }

    /// <summary>Contacts from the last step, for drawing.</summary>
    public IReadOnlyList<MoveContact> Contacts => contacts;

    /// <summary>The body as the collider sees it.</summary>
    public Capsule Body => new(
        Feet + new Vector3(0f, Radius, 0f),
        Feet + new Vector3(0f, Height - Radius, 0f),
        Radius);

    public void Step(Vector3 wish, float deltaSeconds, TriangleMesh3D world)
    {
        ArgumentNullException.ThrowIfNull(world);
        contacts.Clear();
        SteppedUp = false;

        var dt = MathF.Min(MathF.Max(deltaSeconds, 0f), 1f / 30f);
        if (dt <= 0f) return;

        ProbeGround(world);
        var wasStanding = Standing;

        // ── The walk, laid along the ground ──────────────────────────────────────────────────────
        // Projected onto the ground plane rather than left horizontal: a body walking at a 30° ramp
        // with a purely horizontal motion loses the into-slope component to the deflection and
        // arrives at cos(30) of its speed, so a walk up a hill is quietly slower than the same walk
        // on the flat. Projecting first means the speed is what the speed says it is.
        var horizontal = Vector3.Zero;
        if (wish.LengthSquared() > 1e-6f)
        {
            horizontal = Vector3.Normalize(new Vector3(wish.X, 0f, wish.Z)) * WalkSpeed * dt;
            if (Standing) horizontal = ProjectOnPlane(horizontal, GroundNormal);
        }

        // Ground too steep to stand on takes the body downhill whatever it wants — which is the
        // other half of a slope limit, and the half that makes it visible.
        if (Grounded && !Standing) horizontal += Downhill(GroundNormal) * SlideSpeed * dt;

        var beforeWalk = Feet;
        resolver.Deflect = DeflectAgainstSteepAsWall;
        var walked = resolver.Move(Body, horizontal, world);
        Feet += walked.Position;
        contacts.AddRange(walked.Contacts);

        // ── Climb, if something low was in the way ───────────────────────────────────────────────
        //
        // <b>"Blocked" is measured as ground not covered, not as motion left over.</b> The first
        // version asked whether the move had a residual — and a wall deflection leaves exactly zero,
        // because the whole remaining motion was pointing into the wall and the policy removed all
        // of it. So a body walked into every flight of stairs in the room, was stopped dead by the
        // first riser, reported nothing left to spend, and never tried to climb. Reaching 0.005 m up
        // a 1.8 m staircase is a memorable way to find that out.
        //
        // What a step-up actually asks is "did I get as far along my intended direction as I asked
        // for", and the retry is the WHOLE original motion from a lifted position rather than the
        // remains of it.
        if (wasStanding && StepHeight > 0f && horizontal.LengthSquared() > 1e-10f)
        {
            var direction = Vector3.Normalize(horizontal);
            var flatGain = Vector3.Dot(Feet - beforeWalk, direction);
            if (flatGain < horizontal.Length() - 1e-4f)
            {
                var flatFeet = Feet;
                Feet = beforeWalk;
                if (!TryStepOver(horizontal, direction, flatGain, world))
                {
                    Feet = flatFeet;
                    ProbeGround(world);
                }
            }
        }

        // ── Fall ─────────────────────────────────────────────────────────────────────────────────
        // <b>Gravity may not slide you.</b> Resolved with a STOP rather than a deflection, because a
        // fall deflected along a slope is downhill motion — applied every frame, for ever. That is
        // the micro-slide, and it is not a tolerance problem to be tuned away: a body at rest on a
        // hill is at rest because nothing moved it, not because what moved it was small.
        VerticalSpeed = Standing && VerticalSpeed <= 0f ? 0f : VerticalSpeed - (Gravity * dt);
        if (MathF.Abs(VerticalSpeed) > 1e-6f)
        {
            resolver.Deflect = StopDead;
            var fell = resolver.Move(Body, new Vector3(0f, VerticalSpeed * dt, 0f), world);
            Feet += fell.Position;
            contacts.AddRange(fell.Contacts);
            if (fell.Contacts.Count > 0 && VerticalSpeed < 0f) VerticalSpeed = 0f;
        }

        // ── Stay on the ground ───────────────────────────────────────────────────────────────────
        // Step DOWN, and the reason it is a separate probe rather than extra gravity: a body walking
        // off the top of a flight of stairs is briefly over nothing, and letting gravity find the
        // next tread launches it into a bounce down the whole flight. Only applied if there IS
        // ground within a step — walk off the ledge and this finds nothing, which is the fall.
        if (wasStanding && VerticalSpeed <= 0f) SnapDown(world);

        ProbeGround(world);
    }

    /// <summary>Place the body at <paramref name="feet"/> and forget everything it was doing.</summary>
    public void Teleport(Vector3 feet)
    {
        Feet = feet;
        VerticalSpeed = 0f;
        Grounded = false;
        GroundNormal = Vector3.UnitY;
        contacts.Clear();
    }

    /// <summary>
    /// Is there ground under the body right now, and how steep is it.
    /// </summary>
    /// <remarks>
    /// A short downward sweep rather than a distance test. A body resting on a surface is already
    /// touching it, so the sweep answers at time zero — and a vertical wall beside the body is
    /// tangential to a downward motion, so the sweep correctly refuses to call it ground. That
    /// falls out of the rule stage R-C added and would otherwise be a special case here.
    /// </remarks>
    private void ProbeGround(TriangleMesh3D world)
    {
        var hit = Intersection.Sweep(Body, new Vector3(0f, -GroundProbeDepth, 0f), world);
        Grounded = hit is not null;
        GroundNormal = hit?.Normal ?? Vector3.UnitY;
    }

    /// <summary>Lift, move, drop — and keep it only if it got further AND landed somewhere standable.</summary>
    /// <remarks>
    /// Tried only when the flat move fell short, so a step costs nothing on open ground. Both
    /// conditions on the result matter: a lip that leads onto a 60° face is not a step up, it is a
    /// shorter way to start sliding, and one that ends no further along than walking into the wall
    /// did is a body bobbing up and down against it every frame.
    /// </remarks>
    private bool TryStepOver(Vector3 horizontal, Vector3 direction, float flatGain, TriangleMesh3D world)
    {
        var start = Feet;

        resolver.Deflect = StopDead;
        var lift = resolver.Move(Body, new Vector3(0f, StepHeight, 0f), world);
        if (lift.Position.Y < StepHeight - 1e-3f) { Feet = start; return false; }   // no headroom
        Feet += lift.Position;

        // FAR ENOUGH TO PUT THE AXIS ON THE TREAD, not just as far as this frame asked for. A
        // capsule is round underneath, so landing with its axis still short of the step's edge
        // leaves it BALANCED on that edge — and the contact normal there is the edge's, which came
        // back as 56.6° on a flat 0.30 m tread and was rejected as unstandable. Correctly: a body
        // perched on a corner is not standing on the stair, and accepting it would have it slide
        // straight back off.
        //
        // The cost is a small lurch: a blocked frame that would have advanced 5.8 cm advances a
        // radius instead. That is the price of a round body on a square step, it only fires on the
        // frame that was going nowhere anyway, and it is visible in the viewer as a hop onto each
        // tread rather than hidden in an average.
        var reach = MathF.Max(horizontal.Length(), Radius + 0.02f);
        resolver.Deflect = DeflectAgainstSteepAsWall;
        var across = resolver.Move(Body, direction * reach, world);
        Feet += across.Position;

        resolver.Deflect = StopDead;
        var down = resolver.Move(Body, new Vector3(0f, -(StepHeight + GroundProbeDepth), 0f), world);
        Feet += down.Position;

        ProbeGround(world);

        var gain = Vector3.Dot(Feet - start, direction);
        if (Standing && gain > flatGain + 1e-4f)
        {
            SteppedUp = true;
            return true;
        }

        Feet = start;
        ProbeGround(world);
        return false;
    }

    private void SnapDown(TriangleMesh3D world)
    {
        var start = Feet;

        resolver.Deflect = StopDead;
        var down = resolver.Move(Body, new Vector3(0f, -(StepHeight + GroundProbeDepth), 0f), world);
        if (down.Contacts.Count == 0) return;    // nothing within a step: this is a fall, not a stair

        Feet += down.Position;
        ProbeGround(world);
        if (Standing) return;

        // It found something, but nothing to stand on. Better to fall off a cliff edge than to be
        // stuck to its face.
        Feet = start;
        ProbeGround(world);
    }

    /// <summary>
    /// A surface too steep to stand on deflects like a WALL: sideways only, never upward.
    /// </summary>
    /// <remarks>
    /// The plain slide would let a body walking into a 60° face convert its forward motion into
    /// up-the-face motion and climb it at walking pace, which is the classic way a slope limit gets
    /// reported as "not working" when the limit itself is fine. Flattening the deflection is what
    /// makes the limit mean anything.
    /// </remarks>
    private Vector3 DeflectAgainstSteepAsWall(Vector3 remaining, Vector3 normal)
    {
        var slid = CollisionResponse.RemoveNormalComponent(remaining, normal);
        if (Room.SlopeDegrees(normal) <= SlopeLimitDegrees) return slid;

        var flat = new Vector3(normal.X, 0f, normal.Z);
        if (flat.LengthSquared() < 1e-8f) return slid;

        var wall = CollisionResponse.RemoveNormalComponent(remaining, Vector3.Normalize(flat));
        return new Vector3(wall.X, MathF.Min(wall.Y, 0f), wall.Z);
    }

    private static Vector3 StopDead(Vector3 remaining, Vector3 normal) => Vector3.Zero;

    private static Vector3 ProjectOnPlane(Vector3 v, Vector3 normal) =>
        v - (normal * Vector3.Dot(v, normal));

    /// <summary>Straight down the slope: gravity with the into-surface part taken out.</summary>
    private static Vector3 Downhill(Vector3 normal)
    {
        var along = ProjectOnPlane(-Vector3.UnitY, normal);
        return along.LengthSquared() < 1e-8f ? Vector3.Zero : Vector3.Normalize(along);
    }

    /// <summary>How far below the feet counts as "the ground I am on".</summary>
    /// <remarks>
    /// Small: this asks whether the body is standing, not whether there is a floor somewhere below.
    /// Stepping DOWN is a separate probe with its own reach, because "am I grounded" and "is there a
    /// stair here" are different questions and answering both with one number makes a body hover a
    /// stair's height above the floor.
    /// </remarks>
    private const float GroundProbeDepth = 0.02f;
}
