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
/// <b>Configure and fire, which is the opposite of what I argued for and the right answer.</b> The first
/// version reloaded on change, on the reasoning that a setting with no effect until you find the keyboard looks
/// broken. That is true of a cheap setting and false of an expensive one: a map costs about a second to
/// generate, so reload-on-change means you cannot set three things and then go — you get a map after the first,
/// a map after the second, and a settle delay in between to stop a dragged slider queueing sixty. Reported from
/// the chair as "because this is a slow operation the interface becomes kind of worse", which is exactly it.
/// </para>
/// <para>
/// So nothing here reloads anything. Three settings and two actions, and the actions are the only things that
/// touch the world. The settle delay is gone with the model that needed it.
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

    /// <summary>Builds the configured map on the seed already loaded.</summary>
    /// <remarks>
    /// <b>Two actions rather than one, because the two questions a map browser asks are different questions.</b>
    /// This one keeps the seed, so changing the archetype or the region and firing shows the <em>same
    /// landscape under a different regime</em> — which is the comparison worth making and impossible to make if
    /// every generate also rerolled.
    /// <para>
    /// <b>A real button.</b> These were checkboxes for a while on my claim that the panel had no button control,
    /// which was simply not true — <c>DebugControls.Button</c> has been there all along, rendering an
    /// <c>ImGui.Button</c>, and only <c>[Tune]</c> had never surfaced it. A checkbox for an action is wrong
    /// twice: it asks you to tick a thing and then guess whether it happened, and it leaves a box sitting there
    /// ticked as though it described a state. Fixed in the layer, so every tunable object gets buttons.
    /// </para>
    /// </remarks>
    [Tune(Label = "generate", Group = "map", Action = true)]
    public bool Generate;

    /// <summary>Builds the configured map on a fresh seed.</summary>
    /// <remarks>
    /// The other question: another map like this. SPACE does the same thing, for when the mouse is elsewhere.
    /// </remarks>
    [Tune(Label = "new seed", Group = "map", Action = true)]
    public bool NewSeed;
}
