using System.Globalization;
using System.Numerics;
using System.Text;

namespace Blix.Labs.Character;

/// <summary>
/// One line per frame of what the body and the camera were doing, to a file.
/// </summary>
/// <remarks>
/// <para>
/// <b>A dump is a moment; this is the interval.</b> F12 answers "what is true right now", which
/// settles a facing or a distance. It cannot settle "the trail curves and I only pressed W", because
/// that is a claim about a sequence — and the frame where it went wrong is not the frame you are
/// looking at when you notice. A trace makes the sequence readable after the fact, by whoever is
/// reading, without either of us having been at the keyboard.
/// </para>
/// <para>
/// <b>JSON lines, flushed every row.</b> One self-describing object per line: greppable, diffable,
/// and loadable a line at a time by anything. Flushed immediately because a trace exists for the
/// runs that end badly, and a buffered one loses precisely the last moments — which are the ones
/// worth having.
/// </para>
/// <para>
/// <b>It records what was ARGUED about.</b> Position and heading, because "the trail should be
/// straighter" is a question about their history; the camera, because every steering question is
/// really about the camera; the ground and the contacts, because the answer to the curved trail
/// turned out to be a contact at head height that nothing on screen distinguished from any other.
/// </para>
/// </remarks>
public sealed class LabTrace : IDisposable
{
    private readonly StreamWriter writer;
    private readonly StringBuilder line = new();

    private LabTrace(string path, StreamWriter writer)
    {
        Path = path;
        this.writer = writer;
    }

    public string Path { get; }

    public int Rows { get; private set; }

    /// <summary>Begins a trace, creating the directory if it is not there.</summary>
    public static LabTrace Start(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var writer = new StreamWriter(full, append: false) { AutoFlush = true };
        return new LabTrace(full, writer);
    }

    public void Row(
        int frame,
        double seconds,
        RoomCamera camera,
        CharacterMotor motor,
        float bodyFacing,
        Vector3 lastTravel)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(motor);

        var travel = new Vector3(lastTravel.X, 0f, lastTravel.Z);

        // Contacts are summarised rather than listed: a count and WHERE THE HIGHEST ONE WAS. The
        // whole point of the summary is the second number — a trail that bends on the frames where
        // "highest contact" jumps to head height has explained itself.
        var highest = 0f;
        foreach (var contact in motor.Contacts)
        {
            highest = MathF.Max(highest, contact.Point.Y - motor.Feet.Y);
        }

        line.Clear();
        line.Append('{');
        Number("frame", frame);
        Number("t", seconds);
        Number("x", motor.Feet.X);
        Number("y", motor.Feet.Y);
        Number("z", motor.Feet.Z);
        Number("facing_deg", bodyFacing * 180f / MathF.PI);
        Number("travel", travel.Length());
        Number("travel_deg", travel.LengthSquared() > 1e-12f ? MathF.Atan2(-travel.X, -travel.Z) * 180f / MathF.PI : 0f);
        Text("rig", camera.Rig.ToString());
        Number("cam_yaw_deg", camera.Yaw * 180f / MathF.PI);
        Number("cam_pitch_deg", camera.Pitch * 180f / MathF.PI);
        Number("cam_dist", camera.Distance);
        Flag("grounded", motor.Grounded);
        Flag("standing", motor.Standing);
        Number("slope_deg", motor.Grounded ? motor.GroundSlopeDegrees : -1f);
        Number("vspeed", motor.VerticalSpeed);
        Number("contacts", motor.Contacts.Count);
        Number("highest_contact", highest);
        Flag("stepped", motor.SteppedUp);
        line.Length -= 1;   // the trailing comma
        line.Append('}');

        writer.WriteLine(line.ToString());
        Rows++;

        void Number(string name, double value) =>
            line.Append('"').Append(name).Append("\":")
                .Append(value.ToString("0.#####", CultureInfo.InvariantCulture)).Append(',');

        void Flag(string name, bool value) =>
            line.Append('"').Append(name).Append("\":").Append(value ? "true" : "false").Append(',');

        void Text(string name, string value) =>
            line.Append('"').Append(name).Append("\":\"").Append(value).Append("\",");
    }

    public void Dispose() => writer.Dispose();
}
