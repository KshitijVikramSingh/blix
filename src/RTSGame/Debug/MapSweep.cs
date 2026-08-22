using RTSGame.Simulation;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Debug;

/// <summary>
/// Generates every map the panel can ask for, and reports the ones that fail.
/// </summary>
/// <remarks>
/// <b>Written because the panel made a whole range reachable that nothing tested.</b> The gate generates terrain
/// at one point — one archetype, one region, thirty metres of relief — and drew the reasonable conclusion that
/// generation worked. Then the relief dial could be turned to four metres and generation threw outright:
/// <c>Math.Clamp</c> with a constant floor of 1.1 m against a ceiling of <c>Amplitude × 0.17</c>, which inverts
/// below about six and a half metres. A crash, in the first minute, on the second thing anybody would try.
/// <para>
/// <b>The lesson is about who decides what gets tested.</b> While the map was chosen by a command-line flag,
/// the tested set and the reachable set were the same set — whatever I passed was what existed. A control panel
/// hands that choice to whoever is holding the mouse, and the reachable set becomes the whole product space,
/// which nothing had ever run. That is a property of adding <em>any</em> interface, not of this one.
/// </para>
/// <para>
/// <b>Terrain and country, not a settlement.</b> This is deliberately the depth the panel drives — relief,
/// drainage, biomes, soil, woodland — rather than a founded village, because a full populate per combination
/// would cost minutes and the failures being hunted are in the generator. The settlement legs already run the
/// deeper path at one point each.
/// </para>
/// <para>
/// The extremes are in the list on purpose, and the low end especially: nearly-flat maps are where every
/// constant expressed in metres stops being small relative to the relief, and they are one drag of a slider
/// away.
/// </para>
/// </remarks>
internal static class MapSweep
{
    /// <summary>
    /// Relief settings to try, in metres.
    /// </summary>
    /// <remarks>
    /// Both ends of the dial and the awkward middle: 1 m is almost flat, 4 m is the value that crashed, 7 m is
    /// just above where the river ceiling crosses its floor, 30 m is what the gate uses, 60 m is the top of the
    /// slider. Bugs in this family live at the ends, so the ends are not optional.
    /// </remarks>
    private static readonly float[] Amplitudes = { 1f, 4f, 7f, 30f, 60f };

    public static int Run(float extentMetres, uint seed)
    {
        var faults = new List<string>();
        var generated = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Every archetype against every relief, which is the axis the crash lived on.
        foreach (var archetype in MapLayout.All)
        foreach (var amplitude in Amplitudes)
        {
            generated++;
            Try(archetype, Region.Downland, amplitude, extentMetres, seed, faults);
        }

        // Every region at the extremes, because a region scales the authored channel widths and basin depths
        // through WaterScale — so it multiplies exactly the numbers the relief ceiling is compared against.
        foreach (var region in RegionProfile.All)
        foreach (var amplitude in new[] { 1f, 4f, 60f })
        {
            generated++;
            Try(Archetype.DiagonalRiver, region, amplitude, extentMetres, seed, faults);
        }

        Console.WriteLine(
            $"RTSGame map sweep — {generated} maps at {extentMetres:F0} m, seed {seed}, " +
            $"{clock.Elapsed.TotalSeconds:F1} s");
        foreach (var fault in faults) Console.WriteLine($"  FAULT: {fault}");
        Console.WriteLine(
            faults.Count == 0
                ? $"  every one of {generated} generated"
                : $"  {faults.Count} of {generated} failed");
        return faults.Count > 0 ? 1 : 0;
    }

    private static void Try(
        Archetype archetype,
        Region region,
        float amplitude,
        float extentMetres,
        uint seed,
        List<string> faults)
    {
        var what = $"{archetype} in {region} at {amplitude:F0} m";
        try
        {
            var world = new SimulationWorld(extentMetres);
            world.Terrain.SetRegion(region);
            var layout = MapLayout.Composed(archetype, extentMetres, seed, amplitude);
            var plan = ReliefPlan.FromLayout(layout, extentMetres, seed);
            plan.Apply(world.Terrain);
            SettlementScenarios.PaintCountry(world);

            // Cheap sanity beyond "it did not throw", because a generator can fail by producing nonsense as
            // easily as by crashing, and a NaN propagates silently until something downstream divides by it.
            var (floor, span) = SettlementScenarios.InteriorReliefOf(world);
            if (!float.IsFinite(floor) || !float.IsFinite(span)) faults.Add($"{what}: relief is not finite");
            else if (span < 0f) faults.Add($"{what}: negative relief span {span:F2}");
        }
        catch (Exception error)
        {
            faults.Add($"{what}: {error.GetType().Name}: {error.Message}");
        }
    }
}
