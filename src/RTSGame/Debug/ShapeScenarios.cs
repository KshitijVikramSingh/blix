using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Debug;

/// <summary>
/// The merciless view: every archetype as bare shape, with nothing to hide behind.
/// </summary>
/// <remarks>
/// <b>The test this exists for.</b> Print all eight archetypes as height alone — no trees, no ground cover, no
/// biome colour, no water, no shading, no erosion detail worth looking at — hide the labels, and try to name
/// them. If they cannot be told apart from the low-pass shape, nothing downstream can rescue them: vegetation
/// and grading and drainage all decorate a shape, and eight decorations of one shape is what "the presets all
/// look the same" has meant every time it has been said.
/// <para>
/// <b>Deliberately headless and textual, and that is the point rather than a limitation.</b> Every judgement
/// about this terrain so far has needed somebody to look at a window and say what they saw, which makes the
/// loop as slow as a conversation and means the person writing the generator is working blind. A contour map in
/// a terminal is a worse picture and an enormously better instrument: it can be produced in seconds, compared
/// against the previous one, and read by whoever is holding the keyboard.
/// </para>
/// <para>
/// Ten height bands, which is a contour map with the contours filled in. Bands rather than lines because a
/// line drawing in a character grid loses its thin features to the sampling, while a filled band survives it —
/// and what is being judged here is <em>areal</em> shape: where the high ground is, where the low ground is,
/// and what the boundary between them looks like.
/// </para>
/// </remarks>
internal static class ShapeScenarios
{
    /// <summary>Low to high. Ten bands, chosen so the eye reads them as a gradient.</summary>
    private const string Bands = " .,:;-+*#@";

    public static void Run(float extentMetres, float amplitudeMetres, uint seed, int rows)
    {
        // The frontier is a feature of every map and therefore tells you nothing about any of them. It also
        // occupies half the area and is the tallest thing present, which would flatten every band in the ramp.
        var frontier = ReliefPlan.Frontier;
        ReliefPlan.Frontier = false;

        Console.WriteLine(
            $"RTSGame shapes — {extentMetres:F0} m, amplitude {amplitudeMetres:F0} m, seed {seed}, " +
            $"frontier off");
        Console.WriteLine(
            "  height in ten bands, low to high: '" + Bands.Replace(" ", "_") + "'");
        Console.WriteLine();

        foreach (var archetype in MapLayout.All)
        {
            var world = new SimulationWorld(extentMetres);
            // <b>Composed, because that is what a map is.</b> Judging a lone archetype judges half of what the
            // generator produces — and the half that scored 0.76 for having only one statement in it.
            var layout = MapLayout.Composed(archetype, extentMetres, seed, amplitudeMetres);
            var plan = ReliefPlan.FromLayout(layout, extentMetres, seed);
            plan.Apply(world.Terrain);
            Report(world, layout, plan, extentMetres, rows);
        }

        ReliefPlan.Frontier = frontier;
    }

    private static void Report(
        SimulationWorld world,
        MapLayout layout,
        ReliefPlan plan,
        float extentMetres,
        int rows)
    {
        var columns = rows * 2;
        var heights = new float[rows * columns];
        var low = float.MaxValue;
        var high = float.MinValue;
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var at = Sample(column, row, columns, rows, extentMetres);
            var height = world.Terrain.SampleHeight(at);
            heights[row * columns + column] = height;
            low = MathF.Min(low, height);
            high = MathF.Max(high, height);
        }

        var span = MathF.Max(0.01f, high - low);
        Console.WriteLine($"=== {layout.Kind}");
        Console.WriteLine($"    \"{layout.Sentence}\"");

        // <b>What the shape is made of, in numbers, beside the picture.</b> The picture says whether a person
        // can name it; these say whether it is what it claims. A layout describing two ridges and a floor
        // should put a serious share of its area low and a serious share high — if nine tenths of it sits in
        // the middle bands then whatever else is true, there is no valley and no ridge, only undulation.
        var lowGround = 0;
        var midGround = 0;
        var highGround = 0;
        foreach (var height in heights)
        {
            var t = (height - low) / span;
            if (t < 0.30f) lowGround++;
            else if (t > 0.66f) highGround++;
            else midGround++;
        }

        var cells = (float)heights.Length;
        Console.WriteLine(
            $"    relief {span:F1} m · low {lowGround / cells * 100f:F0}% · " +
            $"middle {midGround / cells * 100f:F0}% · high {highGround / cells * 100f:F0}% · " +
            $"{plan.Landforms.Count} landforms, {layout.Separators.Length} separators, " +
            $"{layout.Connectors.Length} connectors, {layout.Basins.Length} basins");

        for (var row = 0; row < rows; row++)
        {
            var line = new System.Text.StringBuilder("    ");
            for (var column = 0; column < columns; column++)
            {
                var t = Math.Clamp((heights[row * columns + column] - low) / span, 0f, 0.9999f);
                line.Append(Bands[(int)(t * Bands.Length)]);
            }

            Console.WriteLine(line.ToString());
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Where a character stands, in world metres.
    /// </summary>
    /// <remarks>
    /// Twice as many columns as rows, because a terminal character is about twice as tall as it is wide — so a
    /// square map printed square comes out squashed, and a squashed map is one whose shape cannot be judged.
    /// </remarks>
    private static Vector2 Sample(int column, int row, int columns, int rows, float extentMetres)
    {
        // A hair inside the edge, so the outermost sample is on the map rather than on its boundary.
        var reach = extentMetres * 0.98f;
        return new Vector2(
            (column / (float)(columns - 1) - 0.5f) * reach,
            (row / (float)(rows - 1) - 0.5f) * reach);
    }
}
