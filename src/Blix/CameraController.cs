using System.Globalization;
using System.Numerics;
using Blix.Core;
using Blix.Diagnostics;

namespace Blix;

/// <summary>
/// Steers a <see cref="Camera3D"/>: look, orbit, fly and zoom, as one camera rather than two modes.
/// </summary>
/// <remarks>
/// <b>Orbit and fly are two ways of driving the same camera.</b> The camera has a position, a direction it
/// looks (yaw and pitch), and a pivot a distance ahead of it. Looking turns it in place and the pivot swings
/// with the view; orbiting swings the camera round the pivot; moving carries both; zooming closes the
/// distance. Nothing switches, so orbiting, flying off and orbiting again never jumps.
/// <para>
/// <b>It drives a <see cref="Camera3D"/>, not a camera of its own.</b> Every change is written to the
/// camera's transform, so the lens, the view and projection, the frustum slices and the screen ray all stay
/// the camera's one implementation.
/// </para>
/// <para>
/// <b>One convention.</b> Yaw 0 looks down -Z and turns towards +X; pitch is where the camera looks, up
/// positive. The pose is written and read as <c>--cam x,y,z,yaw,pitch</c> in degrees, which is the form of
/// every saved Sponza viewpoint, so those still work.
/// </para>
/// <para>
/// <b>Input is optional.</b> The operations take deltas, because a view drawn into a panel gets its drags
/// through the UI rather than the host. <see cref="DriveDefault"/> is the host route, with Blix's default
/// layout that <see cref="DescribeDefaultKeys"/> lists. A default, not the definition: an application
/// with another layout calls the operations itself, as the Studio viewport panel does.
/// </para>
/// </remarks>
public sealed class CameraController
{
    private Vector3 position;
    private float yaw;
    private float pitch;
    private float distance = 5f;

    public CameraController(Camera3D camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Camera = camera;
        Apply();
    }

    /// <summary>The camera this steers.</summary>
    public Camera3D Camera { get; }

    /// <summary>Where the camera is.</summary>
    public Vector3 Position
    {
        get => position;
        set { Guard(); position = value; Apply(); }
    }

    /// <summary>Turn about world up, in degrees: 0 looks down -Z, positive turns towards +X. Setting it turns in place.</summary>
    public float Yaw
    {
        get => yaw * Rad2Deg;
        set { Guard(); yaw = MathF.IEEERemainder(value * Deg2Rad, MathF.Tau); Apply(); }
    }

    /// <summary>Where the camera looks, in degrees above the horizon, held inside <see cref="MinPitch"/>..<see cref="MaxPitch"/>. Setting it turns in place.</summary>
    public float Pitch
    {
        get => pitch * Rad2Deg;
        set { Guard(); pitch = Math.Clamp(value * Deg2Rad, MinPitch * Deg2Rad, MaxPitch * Deg2Rad); Apply(); }
    }

    /// <summary>How far the camera is from its pivot, in metres. Setting it keeps the pivot and moves the camera.</summary>
    /// <remarks>
    /// The pose is not <c>[Tune]</c>, deliberately: a position cannot be one, so yaw, pitch and distance flags
    /// could never put a camera back where it was, and a yaw slider would mean turning in place to a flying
    /// camera and orbiting to a viewer. <c>--cam</c> (<see cref="ReadArgs"/>, <see cref="Pose"/>) is the pose,
    /// whole; the lens and the feel are what tune.
    /// </remarks>
    public float Distance
    {
        get => distance;
        set
        {
            Guard();
            var pivot = Pivot;
            distance = Math.Clamp(value, MinDistance, MaxDistance);
            position = pivot - Forward * distance;
            Apply();
        }
    }

    /// <summary>The camera's vertical field of view, in radians. Proxied so it tunes beside the pose.</summary>
    [Tune(0.3f, 1.8f, Group = "camera")]
    public float FieldOfView
    {
        get => Camera.VerticalFieldOfView;
        set => Camera.VerticalFieldOfView = value;
    }

    /// <summary>Metres per second when flying; held Shift or Cmd triples it.</summary>
    [Tune(0.3f, 60f, Group = "camera")]
    public float MoveSpeed { get; set; } = 4f;

    /// <summary>Radians per logical pixel of a drag, for looking and orbiting alike.</summary>
    [Tune(0.0005f, 0.02f, Group = "camera")]
    public float Sensitivity { get; set; } = 0.0035f;

    /// <summary>The pitch limits, in degrees. A viewer orbiting a subject on a floor keeps above it.</summary>
    public float MinPitch { get; set; } = -89f;

    /// <inheritdoc cref="MinPitch"/>
    public float MaxPitch { get; set; } = 89f;

    /// <summary>The pivot distance limits, in metres.</summary>
    public float MinDistance { get; set; } = 0.1f;

    /// <inheritdoc cref="MinDistance"/>
    public float MaxDistance { get; set; } = 500f;

    /// <summary>The direction the camera looks, from yaw and pitch: (cos p sin y, sin p, -cos p cos y).</summary>
    /// <remarks>
    /// From the pose, not read back from the camera: if it read <c>Camera.Transform</c>, a write that went
    /// round the controller would half take effect (seen here, not in yaw and pitch). See <see cref="Guard"/>.
    /// </remarks>
    public Vector3 Forward => new(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), -MathF.Cos(pitch) * MathF.Cos(yaw));

    /// <summary>What orbiting turns round and zooming closes on: <see cref="Distance"/> ahead of the camera.</summary>
    public Vector3 Pivot => position + Forward * distance;

    /// <summary>The pose as <c>x,y,z,yaw,pitch</c> in degrees: pasteable back as <c>--cam</c>.</summary>
    public string Pose => string.Create(CultureInfo.InvariantCulture,
        $"{position.X:0.##},{position.Y:0.##},{position.Z:0.##},{Yaw:0.##},{Pitch:0.##}");

    /// <summary>Turns the camera in place by a drag, in logical pixels. The pivot swings with the view.</summary>
    public void Look(float deltaX, float deltaY) => Turn(deltaX * Sensitivity, -deltaY * Sensitivity);

    /// <summary>Turns in place by these angles, in radians.</summary>
    public void Turn(float yawRadians, float pitchRadians)
    {
        Guard();
        yaw = MathF.IEEERemainder(yaw + yawRadians, MathF.Tau);
        pitch = Math.Clamp(pitch + pitchRadians, MinPitch * Deg2Rad, MaxPitch * Deg2Rad);
        Apply();
    }

    /// <summary>Swings the camera round the pivot by a drag, in logical pixels. The pivot stays put.</summary>
    public void Orbit(float deltaX, float deltaY) => OrbitBy(deltaX * Sensitivity, -deltaY * Sensitivity);

    /// <summary>Swings round the pivot by these angles, in radians.</summary>
    public void OrbitBy(float yawRadians, float pitchRadians)
    {
        Guard();
        var pivot = Pivot;
        Turn(yawRadians, pitchRadians);
        position = pivot - Forward * distance;
        Apply();
    }

    /// <summary>Moves by a direction in the camera's own terms (x right, y world up, z forward), for <paramref name="seconds"/>.</summary>
    public void Move(Vector3 local, float seconds, bool fast = false)
    {
        Guard();
        if (local == Vector3.Zero) return;
        var forward = Forward;
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var world = right * local.X + Vector3.UnitY * local.Y + forward * local.Z;
        position += Vector3.Normalize(world) * MoveSpeed * (fast ? 3f : 1f) * seconds;
        Apply();
    }

    /// <summary>Closes on the pivot, or backs away, by a wheel notch. The pivot stays put.</summary>
    public void Zoom(float wheelDelta)
    {
        Guard();
        var pivot = Pivot;
        distance = Math.Clamp(distance * MathF.Pow(0.85f, wheelDelta), MinDistance, MaxDistance);
        position = pivot - Forward * distance;
        Apply();
    }

    /// <summary>Looks at <paramref name="pivot"/> from <paramref name="fromDistance"/> away, keeping the direction.</summary>
    public void Focus(Vector3 pivot, float fromDistance)
    {
        Guard();
        distance = Math.Clamp(fromDistance, MinDistance, MaxDistance);
        position = pivot - Forward * distance;
        Apply();
    }

    /// <summary>Stands at <paramref name="from"/> and looks at <paramref name="target"/>, which becomes the pivot.</summary>
    public void LookAt(Vector3 from, Vector3 target)
    {
        Guard();
        var offset = target - from;
        var length = offset.Length();
        if (length <= 1e-5f) return;
        position = from;
        var d = offset / length;
        yaw = MathF.Atan2(d.X, -d.Z);
        pitch = Math.Clamp(MathF.Asin(Math.Clamp(d.Y, -1f, 1f)), MinPitch * Deg2Rad, MaxPitch * Deg2Rad);
        distance = Math.Clamp(length, MinDistance, MaxDistance);
        Apply();
    }

    /// <summary>The view-projection through a viewport of this aspect, which a bad aspect cannot break.</summary>
    /// <remarks>A panel's rectangle is only known after layout, and a zero-sized one would otherwise throw.</remarks>
    public Matrix4x4 ViewProjection(float aspect) =>
        Camera.GetViewProjection(aspect > 0.0001f && float.IsFinite(aspect) ? aspect : 16f / 9f);

    /// <summary>Reads <c>--cam x,y,z,yaw,pitch</c> (degrees), the pasteable pose, if it was given.</summary>
    /// <exception cref="AppArgsException">It was given and is not five numbers.</exception>
    public void ReadArgs(AppArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.String("cam") is not { } cam) return;
        var parts = cam.Split(',');
        var n = new float[5];
        if (parts.Length != 5 || !Enumerable.Range(0, 5).All(i =>
                float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i])))
        {
            throw new AppArgsException($"--cam expects x,y,z,yaw,pitch (degrees), got '{cam}'.");
        }

        position = new Vector3(n[0], n[1], n[2]);
        yaw = MathF.IEEERemainder(n[3] * Deg2Rad, MathF.Tau);
        pitch = Math.Clamp(n[4] * Deg2Rad, MinPitch * Deg2Rad, MaxPitch * Deg2Rad);
        Apply();
    }

    /// <summary>
    /// The default layout: right-drag looks, WASD with Space and Ctrl flies, Shift or Cmd goes faster,
    /// left-drag orbits the pivot, and the wheel zooms (or, while looking, sets the flying speed).
    /// </summary>
    /// <remarks>
    /// The cursor is captured while looking, so a long drag does not run out of screen. A left drag reaches
    /// here only when nothing else took it: the overlay's pick mode and the UI both take theirs first.
    /// </remarks>
    public void DriveDefault(IRenderHost host, float seconds)
    {
        ArgumentNullException.ThrowIfNull(host);
        var input = host.Input;
        var looking = input[MouseButton.Right].Down;
        if (looking != cursorCaptured)
        {
            cursorCaptured = looking;
            host.SetCursorCaptured(looking);
        }

        if (looking && input.MouseDelta != Vector2.Zero) Look(input.MouseDelta.X, input.MouseDelta.Y);
        else if (input[MouseButton.Left].Down && input.MouseDelta != Vector2.Zero) Orbit(input.MouseDelta.X, input.MouseDelta.Y);

        if (input.MouseWheel.Y != 0f)
        {
            if (looking) MoveSpeed = Math.Clamp(MoveSpeed * (input.MouseWheel.Y > 0 ? 1.25f : 0.8f), 0.3f, 60f);
            else Zoom(input.MouseWheel.Y);
        }

        var move = Vector3.Zero;
        if (input[Key.W].Down) move.Z += 1f;
        if (input[Key.S].Down) move.Z -= 1f;
        if (input[Key.D].Down) move.X += 1f;
        if (input[Key.A].Down) move.X -= 1f;
        if (input[Key.Space].Down) move.Y += 1f;
        if (input[Key.LeftControl].Down) move.Y -= 1f;
        var fast = input[Key.LeftShift].Down || input[Key.RightShift].Down
                   || input[Key.LeftSuper].Down || input[Key.RightSuper].Down;
        Move(move, seconds, fast);
    }

    private bool cursorCaptured;

    /// <summary>Lists <see cref="DriveDefault"/>'s layout in the overlay's key list, and the pose as a pasteable value.</summary>
    public void DescribeDefaultKeys(DebugContext debug)
    {
        ArgumentNullException.ThrowIfNull(debug);
        debug.Keys.Describe("Right-drag", "look around (the wheel sets flying speed meanwhile)");
        debug.Keys.Describe("WASD", "fly");
        debug.Keys.Describe("Space / LeftControl", "up / down");
        debug.Keys.Describe("Shift", "held, fly three times faster");
        debug.Keys.Describe("Left-drag", "orbit the pivot");
        debug.Keys.Describe("Wheel", "zoom towards the pivot");
        debug.Values.Value("--cam", Pose);
    }

    // <b>The one place the pose reaches the camera</b>: yaw about world up after pitch about the camera's
    // right, which makes the camera's own forward come out as Forward (Test.Graphics BO.2, BO.6).
    // The chain is yaw/pitch -> pose -> Camera3D.Transform -> view, one way, and what was written is
    // remembered so a second writer is caught (Guard) instead of becoming a second source of truth.
    private void Apply()
    {
        applied = (position, Quaternion.CreateFromYawPitchRoll(-yaw, pitch, 0f));
        Camera.Transform.Position = applied.Position;
        Camera.Transform.Rotation = applied.Rotation;
    }

    private (Vector3 Position, Quaternion Rotation) applied;

    // A camera this steers is this controller's to move. Anything that writes its transform directly has
    // made a second source of truth, and the next operation would silently undo it or build on half of
    // it; so the next operation refuses, naming what happened.
    private void Guard()
    {
        if (Camera.Transform.Position != applied.Position || Camera.Transform.Rotation != applied.Rotation)
        {
            throw new InvalidOperationException(
                "The camera's transform was changed outside its CameraController. Move it through the " +
                "controller (Position, Yaw, Pitch, LookAt, ...), or stop steering it with one.");
        }
    }

    private const float Deg2Rad = MathF.PI / 180f;
    private const float Rad2Deg = 180f / MathF.PI;
}
