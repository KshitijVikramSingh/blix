using System.Numerics;
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

    public static int Run(float extentMetres, uint seed, bool drainageFirst = false)
    {
        var faults = new List<string>();
        // <b>Counted separately from faults, because they are a debt and not a break.</b> §151: the criteria
        // fail on today's generator by design — that is the point of writing them before the generator §150
        // replaces it — and a gate leg that went red immediately would block every other piece of work until
        // the whole terrain arc landed. So they ratchet: the count may fall and may not rise.
        var shortfalls = new List<string>();
        var generated = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Every archetype against every relief, which is the axis the crash lived on.
        foreach (var archetype in MapLayout.All)
        foreach (var amplitude in Amplitudes)
        {
            generated++;
            Try(archetype, Region.Downland, amplitude, extentMetres, seed, faults, shortfalls, drainageFirst);
        }

        // Every region at the extremes, because a region scales the authored channel widths and basin depths
        // through WaterScale — so it multiplies exactly the numbers the relief ceiling is compared against.
        foreach (var region in RegionProfile.All)
        foreach (var amplitude in new[] { 1f, 4f, 60f })
        {
            generated++;
            Try(Archetype.DiagonalRiver, region, amplitude, extentMetres, seed, faults, shortfalls, drainageFirst);
        }

        Console.WriteLine(
            $"RTSGame map sweep — {generated} maps at {extentMetres:F0} m, seed {seed}, " +
            $"{clock.Elapsed.TotalSeconds:F1} s");
        foreach (var fault in faults) Console.WriteLine($"  FAULT: {fault}");
        foreach (var shortfall in shortfalls) Console.WriteLine($"  SHORT: {shortfall}");
        Console.WriteLine(
            $"  {shortfalls.Count} criteria shortfall(s) against a recorded {KnownShortfalls} — " +
            (shortfalls.Count > KnownShortfalls
                ? "WORSE, and this leg fails on that alone"
                : shortfalls.Count < KnownShortfalls
                    ? "better; lower the recorded figure"
                    : "unchanged"));
        if (shortfalls.Count > KnownShortfalls)
        {
            faults.Add(
                $"terrain criteria went backwards: {shortfalls.Count} shortfalls against {KnownShortfalls}");
        }
        Console.WriteLine(
            faults.Count == 0
                ? $"  every one of {generated} generated"
                : $"  {faults.Count} of {generated} failed");
        return faults.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// How far the ground the player sees departs from the ground the simulation has.
    /// </summary>
    /// <remarks>
    /// <b>Because "the mouse does not line up, something to do with elevation" is a feeling until it is
    /// arithmetic.</b> The pointer is turned into a world position by raycasting the <em>simulation's</em>
    /// heightfield — marched at 20 cm and bisected ten times, so accurate to a fraction of a millimetre. The
    /// ground on screen is a different surface: a mesh whose vertices sit every
    /// <c>GroundRenderStep</c> navigation cells, two metres apart on a 600 m map, with straight lines between
    /// them. Where the true ground curves between two vertices — a ridge crest, a gully — the drawn triangle
    /// cuts the corner and the two surfaces disagree.
    /// <para>
    /// That vertical disagreement becomes a <em>horizontal</em> one under the cursor, because the ray arrives at
    /// an angle: an error of <c>dh</c> displaces the apparent hit by roughly <c>dh / tan(pitch)</c>. So the
    /// symptom is exactly what was reported — it grows with relief, and it grows as the camera flattens.
    /// </para>
    /// <para>
    /// Reported rather than asserted. Whether it is worth spending triangles on is a judgement about how much
    /// slop a pointer may have, and that belongs to whoever is holding the mouse; what this owes them is the
    /// number.
    /// </para>
    /// </remarks>
    private static void MeasureDrawnGroundError(SimulationWorld world, float amplitude)
    {
        var transform = world.Navigation.Transform;
        // The same step the ground mesh uses: see RtsGameLoop.GroundRenderStep.
        var step = Math.Clamp((int)MathF.Ceiling(transform.Width / 316f), 1, 16);
        var lattice = transform.CellSize * step;
        var worst = 0f;
        var total = 0.0;
        var samples = 0;
        var reach = world.ExtentMeters * 0.45f;
        // Offset by a third of a cell on both axes so the samples land between lattice points rather than on
        // them: on a vertex the two surfaces agree by construction, which would measure zero and prove nothing.
        for (var z = -reach; z <= reach; z += lattice)
        for (var x = -reach; x <= reach; x += lattice)
        {
            var at = new Vector2(x + lattice / 3f, z + lattice / 3f);
            if (!world.Terrain.Contains(at)) continue;
            var drawn = BilinearOnLattice(world, at, lattice);
            var error = MathF.Abs(drawn - world.Terrain.SampleHeight(at));
            worst = MathF.Max(worst, error);
            total += error;
            samples++;
        }

        if (samples == 0) return;
        var mean = (float)(total / samples);
        // A pointer error, at the pitch the camera actually sits at.
        const float pitchRadians = 0.62f;
        var slip = worst / MathF.Tan(pitchRadians);
        // <b>And the ceiling the pointer's raycast assumes.</b> TerrainMap.TryRaycast starts its march from
        // "nothing on this map is higher than this", a constant 64 m written when the relief dial did not exist.
        // A summit above it means the ray begins <em>inside</em> the hill, misses the near slope entirely and
        // reports the far side — which is an elevation-dependent pointer error of tens of metres, not
        // centimetres.
        var highest = float.MinValue;
        var lowest = float.MaxValue;
        for (var z = -reach; z <= reach; z += lattice * 2f)
        for (var x = -reach; x <= reach; x += lattice * 2f)
        {
            var here = world.Terrain.SampleHeight(new Vector2(x, z));
            highest = MathF.Max(highest, here);
            lowest = MathF.Min(lowest, here);
        }

        Console.WriteLine(
            $"    at {amplitude:F0} m relief: drawn ground departs by {mean * 100f:F0} cm mean / " +
            $"{worst * 100f:F0} cm worst on a {lattice:F1} m mesh ({slip:F2} m of pointer slip); " +
            $"ground runs {lowest:F0} to {highest:F0} m against the raycast's 64 m ceiling" +
            (highest > 64f ? "  <-- OVER THE CEILING" : string.Empty));
    }

    /// <summary>The height the ground mesh would draw at a point: its four lattice corners, interpolated.</summary>
    private static float BilinearOnLattice(SimulationWorld world, Vector2 at, float lattice)
    {
        var cell = at / lattice;
        var x0 = MathF.Floor(cell.X) * lattice;
        var z0 = MathF.Floor(cell.Y) * lattice;
        var tx = (at.X - x0) / lattice;
        var tz = (at.Y - z0) / lattice;
        var h00 = world.Terrain.SampleHeight(new Vector2(x0, z0));
        var h10 = world.Terrain.SampleHeight(new Vector2(x0 + lattice, z0));
        var h01 = world.Terrain.SampleHeight(new Vector2(x0, z0 + lattice));
        var h11 = world.Terrain.SampleHeight(new Vector2(x0 + lattice, z0 + lattice));
        return (h00 * (1f - tx) + h10 * tx) * (1f - tz) + (h01 * (1f - tx) + h11 * tx) * tz;
    }

    /// <summary>
    /// Criteria shortfalls this generator is known to have, so the sweep can refuse to make it worse.
    /// </summary>
    /// <remarks>
    /// §151. Measured, not chosen: 106 across the sweep, and the shape of them is the argument for §150.
    /// <b>Every single map has between 139 and 422 watercourses running uphill</b> — "creeps upstairs",
    /// counted, and untouched by §145's slope taper, which reduced how much water sat on a slope without
    /// making the level field monotone. Most maps fall short of the two metres per hundred that water needs
    /// to behave, some at a fifth of it. The steepest flank reaches 3.46 against a traversable 0.82. And
    /// every map carries at least one lake with no basin under it, which §147 was supposed to have stopped —
    /// so that rule does less than its section claims and is the first thing to look at.
    /// <para>
    /// Lower this number when the figure drops. It is a ratchet and not a target: the drainage-first
    /// generator should take it to zero, and until it does, nothing may add to it.
    /// </para>
    /// </remarks>
    private const int KnownShortfalls = 106;

    private static void Try(
        Archetype archetype,
        Region region,
        float amplitude,
        float extentMetres,
        uint seed,
        List<string> faults,
        List<string> shortfalls,
        bool drainageFirst)
    {
        var what = $"{archetype} in {region} at {amplitude:F0} m";
        try
        {
            var world = new SimulationWorld(extentMetres);
            world.Terrain.SetRegion(region);
            var layout = MapLayout.Composed(archetype, extentMetres, seed, amplitude);
            var plan = ReliefPlan.FromLayout(layout, extentMetres, seed);
            plan.DrainageFirst = drainageFirst;
            plan.Apply(world.Terrain);
            SettlementScenarios.PaintCountry(world);
            // <b>And rasterise it, or nothing downstream can see the country that was just painted.</b>
            // §151: PaintCountry writes surfaces and bumps the terrain revision; the navigation grid is not
            // rebuilt until somebody asks. This sweep never did, so IsBlocked answered about bare relief and
            // the first run of the walkability criterion reported <b>100% on every map</b> — on a generator
            // §145 had just measured at forty-two per cent closed canopy. An instrument that cannot see the
            // fault it was written for is worse than no instrument, because it argues the other way.
            world.RebuildTerrainNavigation();

            // Cheap sanity beyond "it did not throw", because a generator can fail by producing nonsense as
            // easily as by crashing, and a NaN propagates silently until something downstream divides by it.
            var (floor, span) = SettlementScenarios.InteriorReliefOf(world);
            if (!float.IsFinite(floor) || !float.IsFinite(span)) faults.Add($"{what}: relief is not finite");
            else if (span < 0f) faults.Add($"{what}: negative relief span {span:F2}");

            // <b>What the map has to be true of, and today it is not.</b> §151. Until now this sweep asserted
            // that the relief was finite and non-negative, which no generator has ever failed — so the panel
            // could ask for every map it knows and the only thing proved was that none of them threw. The
            // criteria are reported whether or not they pass, because the baseline is the point: the new
            // generator in §150 is judged against these numbers on these seeds.
            var criteria = TerrainCriteria.Measure(world);
            Console.WriteLine($"    criteria: {criteria}");
            Console.WriteLine($"    {TerrainCriteria.LastUphillBands}");
            if (criteria.UphillReaches > 0)
            {
                Console.WriteLine($"      {TerrainCriteria.LastWorstUphill}");
            }
            foreach (var shortfall in criteria.Shortfalls()) shortfalls.Add($"{what}: {shortfall}");
            MeasureDrawnGroundError(world, amplitude);
        }
        catch (Exception error)
        {
            faults.Add($"{what}: {error.GetType().Name}: {error.Message}");
        }
    }
}
