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
    /// <summary>
    /// What to draw on the ground about how this map was made.
    /// </summary>
    /// <remarks>
    /// <b>§153. Because the fault that will not go away has no picture.</b> §151 counted watercourses running
    /// uphill on every map and §152 failed four times to attribute them — three of the four guesses were
    /// wrong, and the one instrument that would have settled it was the one nobody had built: <em>which
    /// cells, on which reach, by how much</em>. Every finding in this file that stuck came from an
    /// attribution and every one that did not came from reasoning about the code.
    /// <para>
    /// Immediate rather than configure-and-fire, unlike the generation dials above it: these cost a frame's
    /// worth of lines and nothing else, so the right model is the one where you turn it and look.
    /// </para>
    /// </remarks>
    [Tune(Label = "overlay", Group = "map")]
    public MapOverlay Overlay = MapOverlay.None;

    /// <summary>What the ground can be asked to show about itself.</summary>
    internal enum MapOverlay
    {
        /// <summary>The map as it is meant to be looked at.</summary>
        None,

        /// <summary>
        /// The drainage network, one line per channel cell toward the cell it drains into.
        /// </summary>
        /// <remarks>
        /// Coloured by what is wrong rather than by what is there: a reach whose water rises downstream is
        /// red, and the rest fade from pale to deep with discharge. So the answer to "where are the 300
        /// uphill reaches" is a picture and not a number.
        /// </remarks>
        Drainage,

        /// <summary>
        /// Standing water, ringed, and told apart by whether it has a basin under it.
        /// </summary>
        /// <remarks>
        /// §147 added a rule that a lake must be deep for how broad it is, §151 measured 34 maps still
        /// failing it, and §152 measured the drainage-first path making it <em>worse</em> — 1–5 becoming
        /// 2–11 — with no explanation offered. This is where that gets looked at.
        /// </remarks>
        Standing,

        /// <summary>
        /// Ground too steep to cross, on surfaces that are meant to be crossed.
        /// </summary>
        /// <remarks>
        /// The same test the criteria use — inside the rim, skipping deliberately impassable surfaces — so
        /// what is marked here is exactly what is counted there. Sixteen maps of it.
        /// </remarks>
        Grade,

        /// <summary>Everything a body cannot walk on, whatever the reason.</summary>
        Blocked,
    }

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
