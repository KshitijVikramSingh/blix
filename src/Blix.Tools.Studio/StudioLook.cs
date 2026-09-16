using System.Numerics;
using Blix.Diagnostics;

namespace Blix.Tools.Studio;

/// <summary>
/// Every dial that decides what the stage looks like — Blix's house style, written down.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defaults on this type ARE the house style.</b> <c>new StudioLook()</c> is what Blix thinks
/// an ordinary model should look like, and <c>view</c> takes it without being asked. That is the only
/// place the engine expresses a visual opinion: nothing in <c>Blix.*</c> says how bright a sun is,
/// and nothing has to, because the answer lives here where a person can look at it, diff it across
/// versions, and take it or leave it.
/// </para>
/// <para>
/// <b>This type used to be argued against, in a comment, by me.</b> The argument was that gathering
/// these dissolves the moment they are sorted by what READS them — the sun and the ambient belong to
/// the lit pass, the shadow extent to the caster, the exposure and tonemap to the present pass, which
/// is three sets of pass parameters and not one concept. That is correct, and it answers a different
/// question. Sorted by who DECIDES them they are plainly one thing: one person answers all of these in
/// one sitting while looking at one picture, and changing any of them is the same kind of act. Both
/// groupings are real and they cut across each other, which is what <see cref="TuneAttribute.Group"/>
/// is for — the members below are grouped by pass for the panel while remaining one authored artifact.
/// <c>RTSGame</c>'s <c>LookSettings</c> reached the same shape independently, spanning the same passes,
/// and did not dissolve either.
/// </para>
/// <para>
/// <b>None of these can be settled by measurement, which is why none of them is a constant in a
/// shader.</b> A lighting constant looks exactly like a physical one — "sun intensity 2.05" reads as
/// though somebody derived it — so a number nobody can measure should be visibly a question. Settle
/// one and it belongs here with a note saying what it was judged against, not pushed back into GLSL.
/// </para>
/// <para>
/// <b>Two kinds of member, and the difference is load-bearing.</b> Most are read every frame: move the
/// slider, see it. A few are <see cref="TuneAttribute.Structural"/> — read once when the pipelines and
/// targets are built, and never again. Those are flags rather than sliders, and the panel shows them
/// among the values rather than offering to move them, because a control that changes nothing is worse
/// than one that does not exist.
/// </para>
/// </remarks>
public sealed class StudioLook : ITunable
{
    /// <summary>Degrees around Y, from +Z toward +X.</summary>
    [Tune(0, 360, Group = "sun")] public float SunAzimuth { get; set; } = 52.125f;

    /// <summary>Degrees above the horizon. Not 90: straight down has no stable up vector.</summary>
    [Tune(0, 89, Group = "sun")] public float SunElevation { get; set; } = 54.526f;

    /// <summary>Scales the sun's tint. One is the light this stage was authored under.</summary>
    [Tune(0, 3, Group = "sun")] public float SunIntensity { get; set; } = 1f;

    /// <summary>How much light reaches what the sun does not.</summary>
    /// <remarks>
    /// Zero is a legitimate inspection mode rather than a broken one: flattening the fill is how a
    /// silhouette becomes readable, and how you find out whether a shape is being carried by the key
    /// light or by the ambient.
    /// </remarks>
    [Tune(0, 0.5f, Group = "sun")] public float AmbientStrength { get; set; } = 0.06f;

    /// <summary>Half-width of the sun's orthographic box, in metres.</summary>
    /// <remarks>
    /// A knob because it is a trade every subject settles differently: too wide and a small rig gets
    /// a few texels of shadow map, too narrow and a large one is cut off at the edge of the light.
    /// </remarks>
    [Tune(2, 40, Group = "shadow")] public float ShadowExtent { get; set; } = 9f;

    /// <summary>Whether the floor is drawn. Off is how you look at a thing against nothing.</summary>
    /// <remarks>
    /// It is a lit mesh rather than a gizmo, and it has to be: a shadow needs something to land on.
    /// The GRID over it is a gizmo and always was — <c>debug.Draw.Grid</c>, engine-native, drawn by
    /// the tool. The two were never one thing; they only ever looked like one.
    /// </remarks>
    [Tune(Group = "stage")] public bool Ground { get; set; } = true;

    /// <summary>Exposure applied before tonemapping.</summary>
    [Tune(0, 4, Group = "present")] public float Exposure { get; set; } = 1.0f;

    /// <summary>0 = ACES, 1 = AgX, 2 = Reinhard, 3 = neutral. Matches blix_tonemap.</summary>
    [Tune(0, 3, Group = "present")] public float TonemapMode { get; set; }

    /// <summary>Direction TOWARD the sun. Derived from the two angles.</summary>
    public Vector3 SunDirection { get; private set; } = Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.35f));

    /// <summary>Derived: the tint this stage was authored with, scaled by <see cref="SunIntensity"/>.</summary>
    public Vector3 SunColour { get; private set; } = new(3.2f, 3.05f, 2.75f);

    private static readonly Vector3 SunTint = new(3.2f, 3.05f, 2.75f);

    /// <summary>
    /// Derives the look once, so the declared angles and the derived vector agree from frame one.
    /// </summary>
    /// <remarks>
    /// <b>Without this the two disagreed silently.</b> The vector fields carry the hardcoded direction
    /// this stage was authored with, and nothing recomputed them until something MOVED — so a run with
    /// no flags lit the scene from the old vector while the panel showed angles that did not produce
    /// it. The capture is what caught it: it came back matching the picture from before the angles
    /// existed, byte for byte, which is exactly what "the knob is ignored" looks like when the default
    /// happens to be close.
    /// </remarks>
    public StudioLook() => Recompute();

    /// <summary>A declared value moved. Recompute what is derived from it.</summary>
    public void OnChanged(TunableChange change) => Recompute();

    /// <summary>Derive the sun's vectors from its angles. Also run once at construction.</summary>
    public void Recompute()
    {
        var elevation = SunElevation * (MathF.PI / 180f);
        var azimuth = SunAzimuth * (MathF.PI / 180f);
        var horizontal = MathF.Cos(elevation);

        SunDirection = Vector3.Normalize(new Vector3(
            horizontal * MathF.Sin(azimuth),
            MathF.Sin(elevation),
            horizontal * MathF.Cos(azimuth)));

        SunColour = SunTint * SunIntensity;
    }
}
