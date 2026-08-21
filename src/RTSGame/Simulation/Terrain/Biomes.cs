using System.Numerics;

namespace RTSGame.Simulation.Terrain;

/// <summary>What kind of country a piece of ground is.</summary>
internal enum Biome
{
    /// <summary>Level, well-drained, good soil. Pasture and meadow, and what a settlement is founded on.</summary>
    Meadow,

    /// <summary>High and exposed. Thin soil, wiry grass, twisted scrub.</summary>
    Moor,

    /// <summary>Steep. What the slope has not held on to has gone, so what is left is stone.</summary>
    Scree,

    /// <summary>Low and concave. Where the water collects and does not leave.</summary>
    Marsh,
}

/// <summary>
/// Which kind of country a place is, from the shape of the ground alone.
/// </summary>
/// <remarks>
/// <b>Read by both layers, which is why it lives here rather than in either of them.</b> Generation paints
/// the surface a biome implies — and a surface is simulation truth, since path cost derives from it — while
/// the renderer picks which trees and which grass grow there. Two consumers of one rule, so the rule is one
/// function and cannot drift.
/// <para>
/// <b>It reads heights and nothing else.</b> Deliberately: the generator's output is the surface, so a
/// classifier that consulted surfaces would be reading its own answer and the two layers would no longer
/// agree about a cell whose surface had been edited since — a road, a felling, a building's ground.
/// </para>
/// <para>
/// Three signals, all of them about water, which is what actually decides what grows where. <b>Grade</b>:
/// a slope keeps neither soil nor rain. <b>Height</b>: high ground is colder, poorer and more exposed.
/// <b>Concavity</b>: a hollow collects what the slopes above it shed. The thresholds are the same ones the
/// woodland and the ground cover already use, so the three agree by construction rather than by being
/// tuned against each other.
/// </para>
/// </remarks>
internal static class Biomes
{
    /// <summary>How far apart the samples are that decide whether a place is a hollow.</summary>
    /// <remarks>
    /// Twenty metres, which is larger than the eight the ground cover uses and smaller than a landform.
    /// A marsh is a feature of a valley bottom rather than of a dip between two tufts, and the same
    /// measurement at the cover's scale would call every furrow a marsh.
    /// </remarks>
    private const float HollowReach = 20f;

    public static Biome At(TerrainMap terrain, Vector2 at, float floor, float span)
    {
        // A map with no relief is all one country, and that is the flat map every scenario is calibrated
        // on: no surface is painted, nothing moves.
        if (span < 1f) return Biome.Meadow;

        var grade = terrain.SampleGrade(at);
        if (grade > 0.24f) return Biome.Scree;

        var here = terrain.SampleHeight(at);
        var above = Math.Clamp((here - floor) / span, 0f, 1f);
        var around = (terrain.SampleHeight(at + new Vector2(HollowReach, 0f)) +
                      terrain.SampleHeight(at - new Vector2(HollowReach, 0f)) +
                      terrain.SampleHeight(at + new Vector2(0f, HollowReach)) +
                      terrain.SampleHeight(at - new Vector2(0f, HollowReach))) * 0.25f;
        // Normalised against the drop a tenth grade would give over the same reach, so this is "how much of
        // a bowl is this" rather than a number of metres.
        var hollow = (around - here) / (HollowReach * 0.10f);

        // A bowl in the low ground holds water. A bowl high up drains out of one side of itself.
        if (hollow > 0.40f && above < 0.38f) return Biome.Marsh;
        if (above > 0.52f) return Biome.Moor;
        return Biome.Meadow;
    }

    /// <summary>The ground a biome is made of, which is what the simulation reads.</summary>
    public static TerrainSurface SurfaceOf(Biome biome) => biome switch
    {
        Biome.Moor => TerrainSurface.Heath,
        Biome.Scree => TerrainSurface.Rough,
        Biome.Marsh => TerrainSurface.Mud,
        _ => TerrainSurface.Grass,
    };
}
