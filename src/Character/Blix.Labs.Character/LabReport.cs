using System.Numerics;
using Blix.Diagnostics;

namespace Blix.Labs.Character;

/// <summary>
/// Publishes the whole of the lab's state into the debug frame, so a dump carries it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written because a session spent arguing about pictures.</b> Five things were reported from the
/// chair across the camera work — a crooked walk, a camera at the wrong distance, a curved trail, a
/// facing that pointed the wrong way, a marker that leaned — and every one of them was a number
/// somebody already had and nobody could see. Four turned out to be the instrument rather than the
/// mechanism. The fix for that is not more care in describing screenshots; it is a snapshot both
/// sides can read.
/// </para>
/// <para>
/// <b>It invents no mechanism.</b> The runtime has had a JSON dump on F12 since the chassis arc —
/// schema 2, values, stats, events, views and every draw command including trails. What was missing
/// is that the lab published five values into it. This publishes the state that gets argued about:
/// where the camera is and which way it looks, where the body is and which way it points, what it is
/// standing on, what it touched and where on its body, and every policy number behind those.
/// </para>
/// <para>
/// <b>Shared by the viewer and the capture</b>, which is the point rather than a convenience: a dump
/// taken from a window and one taken from a headless capture describe the same fields in the same
/// units, so "it looks different in the viewer" becomes a diff rather than a discussion.
/// </para>
/// </remarks>
public static class LabReport
{
    /// <summary>What the body is pointing at, for the report.</summary>
    public enum FacingRule
    {
        /// <summary>Turned toward where it is walking.</summary>
        Travel,

        /// <summary>Turned to match the camera's heading — pointing away from the viewer.</summary>
        CameraHeading,
    }

    public static void Publish(
        DebugContext debug,
        Room room,
        RoomCamera camera,
        CharacterMotor motor,
        float bodyFacing,
        FacingRule facing,
        Vector3 lastTravel)
    {
        ArgumentNullException.ThrowIfNull(debug);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(motor);

        PublishCamera(debug, camera);
        PublishBody(debug, camera, motor, bodyFacing, facing, lastTravel);
        PublishContacts(debug, motor);

        using (debug.Scope("room"))
        {
            debug.Values.Value("triangles", room.TriangleCount);
            debug.Values.Value("parts", room.Parts.Count);
            debug.Values.Value("solids", room.SolidStarts.Count);
        }
    }

    private static void PublishCamera(DebugContext debug, RoomCamera camera)
    {
        using var scope = debug.Scope("camera");

        debug.Values.Value("rig", camera.Rig.ToString());
        debug.Values.Value("yaw", camera.Yaw);
        debug.Values.Value("pitch", camera.Pitch);
        debug.Values.Value("distance", camera.Distance);
        debug.Values.Value("shoulder", camera.ShoulderOffset);
        debug.Values.Value("aim-height", camera.AimHeight);
        debug.Values.Value("fov-deg", camera.FieldOfView * 180f / MathF.PI);
        debug.Values.Value("projection", camera.Rig == CameraRig.Isometric ? "orthographic" : "perspective");
        debug.Values.Value("position", camera.Position);
        debug.Values.Value("target", camera.Target);

        // THE TWO VECTORS THIS SESSION TURNED ON, side by side. `forward` is what the body is steered
        // by; `looks-along` is the flattened direction the camera actually sees. They agreed by
        // construction until the shoulder offset was applied to the eye alone, and then they were
        // 7.6 to 25 degrees apart — a number nobody could see from a screenshot and the first thing
        // a reader of a dump would notice.
        var (forward, right) = camera.GroundBasis;
        debug.Values.Value("steers-along", forward);
        debug.Values.Value("right", right);

        var toTarget = camera.Target - camera.Position;
        var flat = new Vector3(toTarget.X, 0f, toTarget.Z);
        if (flat.LengthSquared() > 1e-8f)
        {
            var looks = Vector3.Normalize(flat);
            debug.Values.Value("looks-along", looks);
            debug.Values.Value("steer-vs-look-deg", AngleBetweenDegrees(forward, looks));
        }
    }

    private static void PublishBody(
        DebugContext debug, RoomCamera camera, CharacterMotor motor,
        float bodyFacing, FacingRule facing, Vector3 lastTravel)
    {
        using var scope = debug.Scope("body");

        debug.Values.Value("feet", motor.Feet);
        debug.Values.Value("grounded", motor.Grounded);
        debug.Values.Value("standing", motor.Standing);
        debug.Values.Value("ground-slope-deg", motor.Grounded ? motor.GroundSlopeDegrees : -1f);
        debug.Values.Value("ground-normal", motor.GroundNormal);
        debug.Values.Value("vertical-speed", motor.VerticalSpeed);
        debug.Values.Value("stepped-up", motor.SteppedUp);

        var heading = new Vector3(-MathF.Sin(bodyFacing), 0f, -MathF.Cos(bodyFacing));
        debug.Values.Value("facing-rule", facing.ToString());
        debug.Values.Value("facing-rad", bodyFacing);
        debug.Values.Value("facing", heading);

        // Signed, in degrees, and the readout the chair was asking for: 0 means the body points where
        // the camera looks, which for a third-person camera means away from the viewer.
        debug.Values.Value(
            "facing-vs-camera-deg",
            MathF.IEEERemainder(bodyFacing - camera.Yaw, MathF.Tau) * 180f / MathF.PI);

        var travel = new Vector3(lastTravel.X, 0f, lastTravel.Z);
        debug.Values.Value("travelled-last-step", travel.Length());
        if (travel.LengthSquared() > 1e-10f)
        {
            var direction = Vector3.Normalize(travel);
            debug.Values.Value("travel-direction", direction);

            // A body walking sideways is fine; a body whose facing and travel disagree by a constant
            // angle while a straight input is held is a turn rate too low. Only the number tells them
            // apart, and drawing both arrows was the previous answer to the same question.
            debug.Values.Value("facing-vs-travel-deg", AngleBetweenDegrees(heading, direction));
        }

        using (debug.Scope("policy"))
        {
            debug.Values.Value("radius", motor.Radius);
            debug.Values.Value("height", motor.Height);
            debug.Values.Value("walk-speed", motor.WalkSpeed);
            debug.Values.Value("gravity", motor.Gravity);
            debug.Values.Value("slope-limit-deg", motor.SlopeLimitDegrees);
            debug.Values.Value("step-height", motor.StepHeight);
            debug.Values.Value("slide-speed", motor.SlideSpeed);
        }
    }

    private static void PublishContacts(DebugContext debug, CharacterMotor motor)
    {
        using var scope = debug.Scope("contacts");
        debug.Values.Value("count", motor.Contacts.Count);

        for (var i = 0; i < motor.Contacts.Count; i++)
        {
            var contact = motor.Contacts[i];
            using var one = debug.Scope($"c{i}");

            var height = contact.Point.Y - motor.Feet.Y;
            debug.Values.Value("point", contact.Point);
            debug.Values.Value("normal", contact.Normal);
            debug.Values.Value("slope-deg", Room.SlopeDegrees(contact.Normal));
            debug.Values.Value("height-on-body", height);

            // WHERE ON THE BODY, named rather than left as a number to interpret. "Overhead" is the
            // one that surprises — a body deflected sideways by something its head caught reads as a
            // curved path with no cause, because nothing is in the way at the height you are looking.
            debug.Values.Value(
                "where",
                height < 0.45f ? "underfoot" : height > motor.Height - 0.5f ? "overhead" : "waist");
        }
    }

    private static float AngleBetweenDegrees(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f)) * 180f / MathF.PI;
}
