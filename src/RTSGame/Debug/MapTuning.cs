using Blix.Diagnostics;
using RTSGame.Simulation.Terrain;
using Archetype = RTSGame.Simulation.Terrain.Archetype;
using Region = RTSGame.Simulation.Terrain.Region;

namespace RTSGame.Debug;

/// <summary>
/// Which map to generate: a browser on the panel rather than a set of keys.
/// </summary>
/// <remarks>
/// <b>These are parameters, not actions, which is why they are not keys.</b> The lab cycles archetype and region
/// with Q, E and I because it is a browser and cycling is what a browser does; the village is not a browser,
/// every letter in it is already a command, and "which archetype" is a setting somebody picks once and then
/// rolls seeds against.
/// <para>
/// <b>Dropdowns, because they are enumerations.</b> They were float sliders with a round-and-clamp on read —
/// <c>[Tune(0, 7)] float Archetype</c> — which is a worse control in every way: it shows 3.0 where it means
/// <c>TwinBasins</c>, it invites values between two archetypes that do not exist, and its range has to be kept
/// in step with the enum by hand. <see cref="TuneAttribute"/> has handled enums natively all along, rendering
/// the member's own names — I wrote the arithmetic instead of reading the tool. The float survives only where
/// the quantity really is continuous, which is the relief.
/// </para>
/// <para>
/// <b>And the panel reloads the map itself.</b> Changing a setting that has no effect until you find the
/// keyboard is a setting that looks broken; the point of a browser is that turning the dial shows you the thing.
/// See <c>RtsGameLoop.FollowMapPanel</c> for the settle delay, which exists because a map costs about a second
/// to generate and a dragged slider would otherwise queue one per frame.
/// </para>
/// </remarks>
internal sealed class MapTuning
{
    /// <summary>Which topology the next map has. Rendered as its own names.</summary>
    [Tune(Label = "archetype", Group = "map")]
    public Archetype Archetype;

    /// <summary>Which climate the next map has.</summary>
    [Tune(Label = "region", Group = "map")]
    public Region Region;

    /// <summary>How much relief the next map has, in metres of total range.</summary>
    /// <remarks>
    /// Genuinely a slider: relief is continuous, and it means its own units — §64 and §65 spent a long time
    /// making amplitude mean "metres of relief" rather than "metres plus whatever the tilt and the primitives
    /// happened to add", and a number that means what it says is worth a dial.
    /// </remarks>
    [Tune(0.0, 60.0, Label = "relief (m)", Group = "map")]
    public float ReliefMetres;

    /// <summary>Rolls the next seed of the same map. A button, spelled as a toggle.</summary>
    /// <remarks>
    /// The panel has no button control, and a toggle that is read and cleared in the same frame is one — which
    /// is worth the small dishonesty because "another one like this" is the single most-pressed thing in a map
    /// browser and it should not require reaching for the keyboard. SPACE still does it.
    /// </remarks>
    [Tune(Label = "next seed", Group = "map")]
    public bool NextSeed;
}
