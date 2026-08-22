namespace RTSGame.Simulation.Terrain;

/// <summary>
/// Cuts valleys into a composed height field, so the map's shape is something water made.
/// </summary>
/// <remarks>
/// <b>What this replaces.</b> <see cref="ReliefPlan"/> built its shapes analytically and then added a second
/// octave of noise at forty-five metres, whose own comment explains exactly what it was standing in for:
/// "what breaks that on earth is drainage — water running off a hill cuts gullies and leaves spurs between
/// them". That was an honest fake and it bought a lot; what it cannot buy is <em>organisation</em>. Noise
/// gullies do not join each other, do not run downhill from a common divide, and do not leave a ridge
/// between two of them, so a hillside made of them reads as a rough hill rather than as a hillside with
/// water coming off it. The complaint was that the map is "a few mounds"; a mound with noise on it is still
/// a mound.
/// <para>
/// <b>The rule, which is one line.</b> Erosion rate goes as <c>K · A^m · S^n</c> — how much water passes
/// (upslope area) times how fast it is moving (slope). That is the stream-power law, and everything
/// recognisable about river country falls out of it without being asked for: valleys, because a channel
/// deepens itself and so gathers more water and so deepens faster; a <em>network</em> of them, because two
/// neighbouring channels compete for the same divide and the winner captures the loser; ridges, because a
/// divide is simply where nothing has yet won; and concave valley profiles against convex hilltops, because
/// A grows downstream while S falls.
/// </para>
/// <para>
/// <b>Hillslope diffusion is the other half and is not optional.</b> Stream power only acts where water has
/// gathered, so on its own it cuts knife-thin slots and leaves everything between them exactly as smooth as
/// it was. Real hillslopes creep: soil moves downhill everywhere, in proportion to how steep it is. That is
/// a Laplacian, it rounds the interfluves, and it is what makes the result read as land rather than as a
/// hill with grooves scratched in it.
/// </para>
/// <para>
/// <b>Uplift, because "erode hard" without it erodes to nothing.</b> Run incision long enough with a fixed
/// starting shape and the whole map converges on a plain. So the composed landforms are treated as an uplift
/// <em>rate</em> rather than as the finished ground: each pass adds a little of them back and then cuts. The
/// shape that emerges is the balance between the two, which is what a real landscape is, and it is why the
/// landforms still decide where the high ground is while erosion decides what the high ground looks like.
/// </para>
/// <para>
/// Deterministic throughout — see the note on <see cref="Drainage"/>. Fixed pass count, fixed traversal
/// order, no randomness anywhere in here.
/// </para>
/// </remarks>
internal static class Erosion
{
    /// <summary>How strongly area counts against slope in the incision law.</summary>
    /// <remarks>
    /// The ratio <c>m/n</c> is what sets the shape of a valley's long profile, and a half is the value
    /// measured on real rivers often enough to be the textbook default. It is worth stating as a ratio
    /// rather than as two knobs, because that is the number the result is sensitive to.
    /// </remarks>
    private const float AreaExponent = 0.5f;

    private const float SlopeExponent = 1.0f;

    /// <summary>
    /// Carves the lattice in place and returns the drainage of the final surface.
    /// </summary>
    /// <param name="heights">The composed relief, which is read as an uplift pattern as well as a start.</param>
    /// <param name="side">Samples per side.</param>
    /// <param name="cellMetres">Lattice spacing.</param>
    /// <param name="passes">How many solve-and-cut rounds. More is a more mature landscape.</param>
    /// <param name="strength">
    /// How much of the relief one pass may remove, as a fraction — the whole point of expressing it this way
    /// is that it is scale-free, so the same number behaves the same on a 600 m map and a 1200 m one.
    /// </param>
    public static Drainage Carve(
        float[] heights,
        int side,
        float cellMetres,
        int passes,
        float strength,
        float[]? inflow = null)
    {
        ArgumentNullException.ThrowIfNull(heights);
        var uplift = (float[])heights.Clone();
        var relief = Relief(heights);
        var drainage = Drainage.Solve(heights, side, cellMetres, inflow);
        if (relief <= 1e-3f || passes <= 0) return drainage;

        for (var pass = 0; pass < passes; pass++)
        {
            drainage = Drainage.Solve(heights, side, cellMetres, inflow);
            // <b>Normalised against the largest catchment actually present, not against the map's area.</b>
            // Those are the same number only when the map is a closed catchment, and once an inherited river
            // enters from off-map the trunk carries two or three times the whole tile — so a coefficient
            // scaled to the tile let the trunk cut several times what `strength` promised. It survived only
            // because the no-pit clamp caught it, which meant the channel was dropping straight to its
            // receiver's level every pass: a staircase of the clamp rather than a stream-power profile, with
            // walls where the steps were. Measured, that came out as a grade of 3.33 against a budget of 0.82.
            //
            // Taken from the field each pass, the promise holds again whatever is draining through: the
            // wettest cell on the map loses `strength` of the relief and everything else loses proportionally
            // less, which is what makes the constant portable between maps.
            var wettest = 0f;
            foreach (var candidate in drainage.Area) wettest = MathF.Max(wettest, candidate);
            var reference = MathF.Pow(MathF.Max(1f, wettest), AreaExponent);
            Incise(heights, drainage, side, cellMetres, strength * relief / reference);
            // <b>Fourteen hundredths, down from twenty-two.</b> Creep is what rounds a slot into a
            // hillside, and too much of it is what rounds a hillside into a dome: at 0.22 over
            // forty-eight passes the diffusion was outrunning the incision and taking the definition
            // back out of the valleys it had just cut.
            Creep(heights, side, 0.14f);

            // Uplift last, so the pass ends with the land being pushed up and the next one cuts into it.
            // A twentieth per pass: enough that a long run reaches a balance rather than a plain, small
            // enough that the composed shape is not simply re-imposed over the valleys each round.
            for (var i = 0; i < heights.Length; i++) heights[i] += uplift[i] * 0.05f;
        }

        // The balance between cutting and uplift settles at its own amplitude, which is not the one the plan
        // budgeted. Rescaled to the plan's relief rather than left where it lands, because the amplitude is
        // a decision somebody made — see ReliefPlan's grade budget — and erosion is meant to change the
        // *shape* of the ground rather than how much of it there is.
        Rescale(heights, relief);
        return Drainage.Solve(heights, side, cellMetres, inflow);
    }

    /// <summary>Cuts each cell by how much water crosses it and how fast that water is moving.</summary>
    private static void Incise(
        float[] heights,
        Drainage drainage,
        int side,
        float cellMetres,
        float coefficient)
    {
        var cut = new float[heights.Length];
        for (var i = 0; i < heights.Length; i++)
        {
            var into = drainage.Receiver[i];
            if (into < 0) continue;
            var run = StepMetres(i, into, side, cellMetres);
            var slope = MathF.Max(0f, (heights[i] - heights[into]) / run);
            if (slope <= 0f) continue;
            cut[i] = coefficient
                * MathF.Pow(drainage.Area[i], AreaExponent)
                * MathF.Pow(slope, SlopeExponent);
        }

        // <b>A cell may not be cut below what it drains into.</b> Without this the law inverts its own
        // premise: a steep cell with a lot of water above it can be lowered past its receiver in a single
        // pass, which creates a pit where a channel should be, and the next solve fills that pit and stalls
        // the flow that made it. Clamping is the standard stability condition and it is cheaper than
        // shortening the pass.
        for (var i = 0; i < heights.Length; i++)
        {
            var into = drainage.Receiver[i];
            if (into < 0) continue;
            var floor = heights[into];
            heights[i] = MathF.Max(floor, heights[i] - cut[i]);
        }
    }

    /// <summary>Soil creep: everything slides a little downhill, everywhere.</summary>
    private static void Creep(float[] heights, int side, float rate)
    {
        var next = new float[heights.Length];
        Array.Copy(heights, next, heights.Length);
        for (var z = 1; z < side - 1; z++)
        for (var x = 1; x < side - 1; x++)
        {
            var index = z * side + x;
            var around = heights[index - 1] + heights[index + 1]
                + heights[index - side] + heights[index + side];
            next[index] = heights[index] + rate * (around * 0.25f - heights[index]);
        }

        Array.Copy(next, heights, heights.Length);
    }

    private static float StepMetres(int from, int into, int side, float cellMetres)
    {
        var dx = Math.Abs(from % side - into % side);
        var dz = Math.Abs(from / side - into / side);
        return (dx == 1 && dz == 1 ? 1.41421356f : 1f) * cellMetres;
    }

    private static float Relief(float[] heights)
    {
        var low = float.MaxValue;
        var high = float.MinValue;
        foreach (var height in heights)
        {
            low = MathF.Min(low, height);
            high = MathF.Max(high, height);
        }

        return high - low;
    }

    private static void Rescale(float[] heights, float wanted)
    {
        var have = Relief(heights);
        if (have <= 1e-4f) return;
        var low = float.MaxValue;
        foreach (var height in heights) low = MathF.Min(low, height);
        var scale = wanted / have;
        for (var i = 0; i < heights.Length; i++) heights[i] = low + (heights[i] - low) * scale;
    }
}
