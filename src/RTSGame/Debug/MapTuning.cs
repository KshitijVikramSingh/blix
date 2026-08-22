using Blix.Diagnostics;
using RTSGame.Simulation.Terrain;
using Archetype = RTSGame.Simulation.Terrain.Archetype;
using Region = RTSGame.Simulation.Terrain.Region;

namespace RTSGame.Debug;

/// <summary>
/// Which map to generate, on sliders rather than on keys.
/// </summary>
/// <remarks>
/// <b>These are parameters, not actions, and they were on keys because keys were what the lab had.</b> The lab
/// cycles archetype and region with Q, E and I because it is a browser and cycling is what a browser does; the
/// village is not a browser, every letter in it is already a command, and "which archetype" is a setting
/// somebody picks once and then rolls seeds against.
/// <para>
/// It also sidesteps a question I could not answer from the source. Modifier combinations were the obvious home
/// for a second set of bindings — Ctrl is mapped, forwarded unfiltered, and <c>Ctrl+A</c> has apparently been
/// placing houses all along — and they are reported not to work here. A slider needs no modifier, and rolling a
/// seed keeps the one free single key.
/// </para>
/// <para>
/// Held as floats because that is what a slider is. Rounded on read, and clamped by the panel to the range the
/// enum actually has, so a dial cannot name an archetype that does not exist.
/// </para>
/// </remarks>
internal sealed class MapTuning
{
    /// <summary>Which archetype the next roll uses. Index into <see cref="MapLayout.All"/>.</summary>
    [Tune(0.0, 7.0, Label = "archetype", Group = "map")]
    public float Archetype;

    /// <summary>Which climate the next roll uses. Index into <see cref="RegionProfile.All"/>.</summary>
    [Tune(0.0, 4.0, Label = "region", Group = "map")]
    public float Region;

    /// <summary>How much relief the next roll asks for, in metres of total range.</summary>
    /// <remarks>
    /// On the panel because it finally means its own units. §64 and §65 spent a long time making amplitude
    /// mean "metres of relief" rather than "metres of relief plus whatever the tilt and the primitives happened
    /// to add" — and a number that means what it says is a number worth putting a slider on.
    /// </remarks>
    [Tune(0.0, 60.0, Label = "relief (m)", Group = "map")]
    public float ReliefMetres;

    public Archetype ArchetypeOf() =>
        MapLayout.All[Math.Clamp((int)MathF.Round(Archetype), 0, MapLayout.All.Length - 1)];

    public Region RegionOf() =>
        RegionProfile.All[Math.Clamp((int)MathF.Round(Region), 0, RegionProfile.All.Length - 1)];
}
