using System.Numerics;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Labs.Character;

/// <summary>Which camera you are looking through.</summary>
/// <remarks>
/// <b>Four rigs rather than four sliders.</b> A lab about how a body moves is a lab about how a body
/// FEELS to move, and that is mostly the camera: the same resolver reads as heavy from behind the
/// shoulder, precise down a barrel, and tactical from above. Each of these makes different faults
/// obvious — the third-person one shows sliding and step hops, the first-person one shows every
/// millimetre of vertical jitter, and the isometric one shows the path across the floor.
/// </remarks>
public enum CameraRig
{
    /// <summary>Free orbit around a point you can pan. For looking at the room rather than the body.</summary>
    Orbit,

    /// <summary>Over the shoulder, pulled in by anything it would otherwise see through.</summary>
    ThirdPerson,

    /// <summary>At the body's eyes.</summary>
    FirstPerson,

    /// <summary>High, fixed angle, orthographic. Tactical.</summary>
    Isometric,
}

/// <summary>
/// The lab's camera: one set of orbit parameters, four ways of turning them into a view.
/// </summary>
/// <remarks>
/// <b>One type, because they share almost everything.</b> Yaw, pitch and distance mean the same thing
/// in all four, and the ground basis every rig hands the body is the same arithmetic — splitting them
/// into four classes would duplicate that and let the copies drift, which is exactly what happened to
/// the ground basis when there were two of it.
/// <para>
/// What differs is three lines each: where the eye goes, what it looks at, and whether the projection
/// is perspective or orthographic.
/// </para>
/// </remarks>
public sealed class RoomCamera
{
    private CameraRig rig = CameraRig.ThirdPerson;

    /// <summary>
    /// Starts on the rig it says it starts on, with that rig's own framing.
    /// </summary>
    /// <remarks>
    /// <b>Because the property setter cannot do it.</b> It returns early when the rig is unchanged —
    /// which is right, or every mouse-move that re-selected the same radio button would snap the
    /// camera back — but it means the INITIAL rig is the one rig whose defaults never ran. The
    /// camera opened at the field initialisers instead: distance 11 and pitch 0.42, the orbit
    /// camera's framing, on a rig that wants 4.2 and 0.22. A capture of the third-person rig came
    /// back framing the whole hall with the body four pixels tall, which is what made it visible —
    /// in the viewer you cannot tell a default you never got from one you disagree with.
    /// </remarks>
    public RoomCamera() => ApplyDefaults();

    /// <summary>Which rig. Setting it applies that rig's defaults — a camera you must re-tune by hand every time you switch is one you stop switching.</summary>
    public CameraRig Rig
    {
        get => rig;
        set
        {
            if (rig == value) return;
            rig = value;
            ApplyDefaults();
        }
    }

    /// <summary>What it looks at. Written by <see cref="Place"/> for every rig except Orbit, which pans.</summary>
    public Vector3 Target { get; set; } = Room.SpawnPoint + new Vector3(0f, 0.9f, 0f);

    /// <summary>Where the eye ended up. Written by <see cref="Place"/>.</summary>
    public Vector3 Position { get; private set; } = new(0f, 4f, 10f);

    public float Yaw { get; set; } = 0.9f;

    public float Pitch { get; set; } = 0.42f;

    /// <summary>How far back the eye sits. Ignored by <see cref="CameraRig.FirstPerson"/>.</summary>
    public float Distance { get; set; } = 11f;

    public float FieldOfView { get; set; } = MathF.PI / 3.2f;

    public float NearPlane { get; set; } = 0.1f;

    public float FarPlane { get; set; } = 200f;

    public float MinPitch { get; private set; } = 0.05f;

    public float MaxPitch { get; private set; } = 1.45f;

    /// <summary>
    /// How far to the side of the body the third-person camera sits. 0 is straight behind.
    /// </summary>
    /// <remarks>
    /// <b>Zero by default, and the reason is worth more than the setting.</b> An over-the-shoulder
    /// offset puts the body OFF the view axis — and a direction parallel to the view axis, seen from
    /// off-axis, projects toward the vanishing point rather than straight up the screen. So the
    /// body's facing marker leans, by exactly the angle the offset subtends, while being exactly
    /// correct. Reported from the chair across several rounds as the facing pointing somewhere other
    /// than expected, and resolved by the reporter setting this to 0 and finding it read right.
    /// <para>
    /// Nothing about the steering changes with it: the probe checks that the camera steers where it
    /// looks at offsets of 0, 0.55 and −1.2, and all three pass. What changes is only how a marker
    /// that points away from you PROJECTS, which is a fact about perspective and not about the body.
    /// </para>
    /// <para>
    /// A shooter earns the offset by keeping the character out of its own reticle. A lab about
    /// watching a body move earns nothing by it and pays in exactly the ambiguity above, so the
    /// slider stays and the default is centred.
    /// </para>
    /// </remarks>
    public float ShoulderOffset { get; set; }

    /// <summary>
    /// How far up the body the camera aims, in metres from the feet.
    /// </summary>
    /// <remarks>
    /// <b>The framing control, and it was a hidden constant.</b> Aiming at a body's chest puts it in
    /// the middle of the screen with the floor filling everything below — which is a follow camera.
    /// Aiming ABOVE its head pushes it into the lower third and fills the frame with the ground it
    /// is walking into, which is what a third-person action camera is for and why it can be played
    /// with. Nothing about it is derivable from the body's height; it is a taste, so it is a dial.
    /// </remarks>
    public float AimHeight { get; set; } = 1.6f;

    /// <summary>Whether the body's own gizmo is worth drawing through this rig.</summary>
    public bool ShowsBody => Rig != CameraRig.FirstPerson;

    /// <summary>Whether the body should be steered relative to this camera rather than the world.</summary>
    /// <remarks>
    /// All four, as it happens — even the isometric one, where screen-relative movement is what
    /// every game with this camera does and world-relative movement is what every prototype does
    /// once before changing it.
    /// </remarks>
    public bool SteersTheBody => true;

    /// <summary>
    /// The camera's own ground plane: where "forward" and "right" are for anything it steers.
    /// </summary>
    /// <remarks>
    /// <b>One basis, because two copies of it were both wrong in the same way.</b> The camera's pan
    /// and the body's walk each derived their own, and each wrote <c>right = (forward.z, 0, -forward.x)</c>
    /// — which is <c>cross(up, forward)</c>, the LEFT vector. Reported from the chair as movement
    /// "at a slightly weird angle and keys-inverted", and both halves of that are the same sign: A
    /// and D swap, and every diagonal mirrors about the forward axis, which reads as the whole
    /// frame being rotated rather than flipped.
    /// <para>
    /// Screen-right is <c>cross(forward, up)</c>. Checked against the case with no algebra in it:
    /// looking down −Z with +Y up, right is +X.
    /// </para>
    /// </remarks>
    public (Vector3 Forward, Vector3 Right) GroundBasis
    {
        get
        {
            var forward = new Vector3(-MathF.Sin(Yaw), 0f, -MathF.Cos(Yaw));
            return (forward, new Vector3(-forward.Z, 0f, forward.X));
        }
    }

    /// <summary>Put the eye where this rig says it goes, given where the body is.</summary>
    /// <remarks>
    /// <paramref name="world"/> is used by the third-person rig alone, and only to stop the eye
    /// ending up on the far side of a wall — see <see cref="PullIn"/>.
    /// </remarks>
    public void Place(Vector3 feet, float bodyHeight, TriangleMesh3D? world)
    {
        var eyeHeight = bodyHeight - 0.15f;

        switch (Rig)
        {
            case CameraRig.Orbit:
                // The only rig whose target is not the body's: it is whatever you last panned to.
                Position = Target + Spherical(Yaw, Pitch, Distance);
                break;

            case CameraRig.ThirdPerson:
            {
                // <b>The shoulder offsets the TARGET as well as the eye</b>, and that is the whole
                // difference between an over-the-shoulder camera and a broken one. Offsetting only
                // the eye leaves it looking ACROSS the body, so the view direction is no longer the
                // yaw direction — and the body is steered by the yaw, so pressing forward walks it
                // diagonally across the screen. Off by atan(shoulder / (distance * cos pitch)):
                // 7.6° at the default 4.2 m, and 25° once the camera pulls in to 1.2 m against a
                // wall. Reported from the chair as 15-30° off in every rig but first person — which
                // has no shoulder offset, which is exactly why that one felt right.
                //
                // Offsetting both keeps the eye looking straight down the yaw, and the body simply
                // sits to one side of the screen. That IS the over-the-shoulder framing; the
                // crooked walk never was.
                var (_, right) = GroundBasis;
                Target = feet + new Vector3(0f, AimHeight, 0f) + (right * ShoulderOffset);
                var wanted = Target + Spherical(Yaw, Pitch, Distance);

                // The pull-in slides the eye along the line to the target, so it shortens the view
                // without turning it. That is why it can be this simple.
                Position = world is null ? wanted : PullIn(Target, wanted, world);
                break;
            }

            case CameraRig.FirstPerson:
            {
                // The eye is AT the body, so there is nothing to pull in and nothing to orbit. The
                // target is a point in front of it, which is the one place the pitch has to become a
                // real direction rather than an elevation.
                Position = feet + new Vector3(0f, eyeHeight, 0f);
                var forward = new Vector3(
                    -MathF.Sin(Yaw) * MathF.Cos(Pitch),
                    -MathF.Sin(Pitch),
                    -MathF.Cos(Yaw) * MathF.Cos(Pitch));
                Target = Position + forward;
                break;
            }

            case CameraRig.Isometric:
                Target = feet + new Vector3(0f, AimHeight * 0.4f, 0f);
                Position = Target + Spherical(Yaw, Pitch, Distance);
                break;
        }
    }

    public Matrix4x4 ViewProjection(float aspect)
    {
        var safeAspect = aspect > 0.0001f && float.IsFinite(aspect) ? aspect : 16f / 9f;
        var up = Vector3.UnitY;

        // Looking straight down the pole makes the up vector degenerate and the view matrix NaN,
        // which propagates into every gizmo and every pick. The pitch clamps stop short of it, and
        // this is the belt to that pair of braces.
        var toTarget = Target - Position;
        if (toTarget.LengthSquared() > 1e-8f &&
            MathF.Abs(Vector3.Dot(Vector3.Normalize(toTarget), Vector3.UnitY)) > 0.999f)
        {
            up = Vector3.UnitZ;
        }

        var view = Matrix4x4.CreateLookAt(Position, Target, up);

        // TRULY orthographic for the isometric rig rather than a long lens faking it. The difference
        // is the point of the rig: parallel projection is what makes two objects the same size at
        // any depth, which is what a tactical camera is FOR.
        if (Rig == CameraRig.Isometric)
        {
            var height = MathF.Max(2f, Distance * 0.62f);
            return view * GraphicsMatrices.CreateOrthographicVulkan(
                height * safeAspect, height, 0.1f, FarPlane);
        }

        return view * GraphicsMatrices.CreatePerspectiveVulkan(FieldOfView, safeAspect, NearPlane, FarPlane);
    }

    public void Orbit(float deltaX, float deltaY)
    {
        Yaw -= deltaX * 0.008f;
        Pitch = Math.Clamp(Pitch + (deltaY * 0.006f), MinPitch, MaxPitch);
    }

    public void Zoom(float wheelDelta)
    {
        if (Rig == CameraRig.FirstPerson) return;   // there is nowhere to zoom to from inside your own head
        Distance = Math.Clamp(Distance - (wheelDelta * 1.4f), 1.2f, 70f);
    }

    /// <summary>
    /// Slide the target across the floor, in the camera's own frame. Orbit only.
    /// </summary>
    /// <remarks>
    /// Along the ground rather than along the view: dragging up on a pitched camera should walk you
    /// further down the hall, not lift you off the floor. Clamped to the hall so a pan cannot lose
    /// the room behind you — a camera that can get lost is a camera you spend the session re-finding.
    /// </remarks>
    public void Pan(float deltaX, float deltaY)
    {
        if (Rig != CameraRig.Orbit) return;

        var (forward, right) = GroundBasis;
        var scale = Distance * 0.0016f;

        var moved = Target + (right * (-deltaX * scale)) + (forward * (-deltaY * scale));
        Target = new Vector3(
            Math.Clamp(moved.X, -Room.HallHalfX, Room.HallHalfX),
            Math.Clamp(moved.Y, 0f, 6f),
            Math.Clamp(moved.Z, -Room.HallHalfZ, Room.HallHalfZ));
    }

    /// <summary>Look at one feature of the room from far enough back to see all of it. Orbit only.</summary>
    public void Frame(Vector3 centre, float size)
    {
        Rig = CameraRig.Orbit;
        Target = centre;
        Distance = Math.Clamp(size * 2.6f, 4f, 70f);
    }

    private static Vector3 Spherical(float yaw, float pitch, float distance) => new(
        MathF.Cos(pitch) * MathF.Sin(yaw) * distance,
        MathF.Sin(pitch) * distance,
        MathF.Cos(pitch) * MathF.Cos(yaw) * distance);

    /// <summary>
    /// Bring the eye forward until nothing is between it and the body.
    /// </summary>
    /// <remarks>
    /// <b>A sphere sweep, which makes this the SECOND consumer of the character arc's sweep.</b> The
    /// plan holds that open as stage C-C's question — whether <see cref="BodyResolver"/>'s loop
    /// belongs in the engine — and this is a first look at the answer: the camera wants the SWEEP,
    /// which is already engine math, and none of the loop. It needs no deflection, no step rule and
    /// no ground state; it needs one cast and a clamp. On this evidence the resolver stays in the
    /// lab, and the note is here rather than in a commit message because this is where a future
    /// reader will ask.
    /// <para>
    /// A sphere rather than a ray so the near plane does not clip a wall the ray squeaked past, and
    /// the pull-in keeps a small gap for the same reason.
    /// </para>
    /// </remarks>
    private static Vector3 PullIn(Vector3 from, Vector3 to, TriangleMesh3D world)
    {
        const float probeRadius = 0.25f;
        const float gap = 0.1f;

        var motion = to - from;
        var distance = motion.Length();
        if (distance < 1e-4f) return to;

        var probe = new Capsule(from, from, probeRadius);
        if (Intersection.Sweep(probe, motion, world) is not { } hit) return to;

        var allowed = MathF.Max(0f, (hit.Time * distance) - gap);
        return from + (motion / distance * allowed);
    }

    private void ApplyDefaults()
    {
        switch (rig)
        {
            case CameraRig.Orbit:
                (MinPitch, MaxPitch) = (0.05f, 1.45f);
                Pitch = 0.42f;
                Distance = 11f;
                break;

            case CameraRig.ThirdPerson:
                // Behind and ABOVE the shoulder, looking down. The first cut was level with the body
                // at 12.6° and read as a follow camera: the character sat dead centre, the floor
                // filled everything below it and a quarter of the frame was empty sky. An action
                // camera looks DOWN at the character so the ground it is about to walk into is the
                // subject — which is also what stops a wall filling the screen the moment you turn.
                (MinPitch, MaxPitch) = (-0.2f, 1.1f);
                Pitch = 0.34f;
                Distance = 4.0f;
                AimHeight = 1.6f;
                ShoulderOffset = 0f;
                break;

            case CameraRig.FirstPerson:
                // Free to look up and down, and nowhere near the poles, where the view matrix's up
                // vector degenerates.
                (MinPitch, MaxPitch) = (-1.4f, 1.4f);
                Pitch = 0f;
                break;

            case CameraRig.Isometric:
                // The classic tactical angle. Pitch is fixed by convention rather than by clamping:
                // you can still drag it, but it starts where the rig means to be.
                (MinPitch, MaxPitch) = (0.5f, 1.4f);
                Pitch = 0.95f;
                Yaw = MathF.PI * 0.25f;

                // 22 m showed the whole hall and made the body a speck — a map view rather than a
                // camera. 14 gives about nine metres of visible height, which is a body you can read
                // and a couple of features around it, which is what this camera is for.
                Distance = 14f;
                break;
        }

        Pitch = Math.Clamp(Pitch, MinPitch, MaxPitch);
    }
}
