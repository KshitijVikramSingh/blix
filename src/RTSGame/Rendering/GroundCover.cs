using System.Numerics;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Rendering;

/// <summary>
/// How much of each kind of ground there is around every corner of a chunk's render grid, which is what
/// turns a boundary between two surfaces from a staircase into a transition.
/// </summary>
/// <remarks>
/// <b>The problem this exists for.</b> The ground was one mesh per surface, each drawn in a single flat
/// colour, and a surface belonged to a render cell if the navigation cell at that cell's own corner said
/// so. Both halves of that produce the same artefact from different directions: the boundary lands on an
/// axis-aligned two-metre grid, and there is nothing either side of it but two flat fills. Pasture meeting
/// moor came out as a staircase, and a road — which is painted per navigation cell and is narrower than the
/// steps — came out as a diagonal flight of them. The note in <c>BuildTerrainSurfaceMesh</c> predicted
/// exactly this and said the answer would be "a finer step near the camera or colour carried per vertex".
/// This is the second one.
/// <para>
/// <b>What it computes.</b> For every corner of the render grid, the share of each visual ground class in a
/// disc around it, where the disc's centre is first displaced by a low-frequency noise warp. The disc turns
/// a hard edge into a gradient; the warp is what stops the gradient being a neat offset copy of the
/// staircase, because a boundary that wanders by a few metres on an eight-metre wavelength reads as
/// something that grew rather than something that was rasterised.
/// </para>
/// <para>
/// <b>Visual classes, not surfaces.</b> Forest cover is painted the same colour as grass on purpose — it is
/// a navigation fact and what you see under a wood is trees — so it collapses into grass here rather than
/// becoming a layer that blends against grass to no effect. One fewer draw per chunk and one fewer
/// meaningless transition.
/// </para>
/// <para>
/// Dressing, in §52's sense: a function of world position and the surface field, held by the renderer,
/// never consulted by the simulation and never saved. It is rebuilt with the chunk it belongs to.
/// </para>
/// </remarks>
internal sealed class GroundCover
{
    /// <summary>The distinct ground colours there are, which is what a corner holds shares of.</summary>
    /// <remarks>
    /// Derived from the enum rather than counted by hand, so adding a surface cannot leave this behind —
    /// the array stride and every loop bound below come from it.
    /// </remarks>
    public static readonly TerrainSurface[] Classes = Enum.GetValues<TerrainSurface>()
        .Where(surface => Visual(surface) == surface)
        .ToArray();

    private readonly float[] shares;
    private readonly byte[] dominant;
    private readonly int cornersX;
    private readonly int cornersZ;

    private GroundCover(float[] shares, byte[] dominant, int cornersX, int cornersZ)
    {
        this.shares = shares;
        this.dominant = dominant;
        this.cornersX = cornersX;
        this.cornersZ = cornersZ;
    }

    /// <summary>Which class's colour this surface is drawn in.</summary>
    public static TerrainSurface Visual(TerrainSurface surface) =>
        surface == TerrainSurface.Forest ? TerrainSurface.Grass : surface;

    /// <summary>
    /// Builds the field for one chunk, on the corners of its render grid.
    /// </summary>
    /// <remarks>
    /// Per corner rather than per vertex, and that is a sixfold saving rather than a nicety: the ground's
    /// triangles do not share vertices, so a chunk of sixty-four cells a side has four thousand corners and
    /// twenty-five thousand vertices. The taps are the expensive part, so they are paid for once per corner
    /// and read four times.
    /// </remarks>
    public static GroundCover Build(TerrainMap terrain, int fromX, int fromZ, int toX, int toZ, int step)
    {
        var grid = terrain.Transform;
        var metres = grid.CellSize;
        var cornersX = (toX - fromX) / step + 2;
        var cornersZ = (toZ - fromZ) / step + 2;
        var shares = new float[cornersX * cornersZ * Classes.Length];

        // A disc a render cell and a half across. Wider and a road three cells wide would be blended out of
        // existence by its own verges; narrower and the transition is thinner than the staircase it is
        // meant to hide, which looks like a bevel on a step rather than like ground.
        var radius = step * metres * 1.5f;
        var taps = new Vector2[9];
        taps[0] = Vector2.Zero;
        for (var i = 0; i < 8; i++)
        {
            var angle = i * MathF.Tau / 8f;
            // The inner ring at six tenths, so the disc is sampled rather than only its rim — a rim-only
            // average makes a cell surrounded by its own kind read as a boundary.
            var reach = (i % 2 == 0) ? radius : radius * 0.6f;
            taps[i + 1] = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * reach;
        }

        for (var cz = 0; cz < cornersZ; cz++)
        for (var cx = 0; cx < cornersX; cx++)
        {
            var at = new Vector2(
                grid.Origin.X + (fromX + cx * step) * metres,
                grid.Origin.Y + (fromZ + cz * step) * metres);
            // <b>The warp is the whole difference between a blend and a bevel.</b> Averaging a disc gives a
            // smooth ramp that still follows the rasterised boundary exactly, so the staircase survives as
            // a soft staircase. Displacing the sample point by a couple of metres on an eight-metre
            // wavelength makes the boundary wander across the grid it was drawn on, and a wandering
            // boundary has no grid in it to see.
            var warp = new Vector2(
                LatticeNoise.Value(at * 0.125f) - 0.5f,
                LatticeNoise.Value(at * 0.125f + new Vector2(31.7f, 12.3f)) - 0.5f) * (radius * 1.35f);

            var basis = cz * cornersX * Classes.Length + cx * Classes.Length;
            foreach (var tap in taps)
            {
                var sampled = Visual(terrain.SampleSurface(at + warp + tap));
                for (var k = 0; k < Classes.Length; k++)
                {
                    if (Classes[k] != sampled) continue;
                    shares[basis + k] += 1f / taps.Length;
                    break;
                }
            }
        }

        // Which class owns each cell, decided by the cell's four corners together. This is what gets the
        // opaque coat, so it has to be a whole-cell answer: a cell with no owner is a hole to the sky.
        var cellsX = cornersX - 1;
        var cellsZ = cornersZ - 1;
        var dominant = new byte[Math.Max(1, cellsX * cellsZ)];
        var cover = new GroundCover(shares, dominant, cornersX, cornersZ);
        for (var z = 0; z < cellsZ; z++)
        for (var x = 0; x < cellsX; x++)
        {
            var best = 0;
            var bestShare = -1f;
            for (var k = 0; k < Classes.Length; k++)
            {
                var total = cover.Share(x, z, k) + cover.Share(x + 1, z, k)
                    + cover.Share(x, z + 1, k) + cover.Share(x + 1, z + 1, k);
                if (total <= bestShare) continue;
                bestShare = total;
                best = k;
            }

            dominant[z * cellsX + x] = (byte)best;
        }

        return cover;
    }

    /// <summary>The share of class <paramref name="slot"/> at a corner of the render grid.</summary>
    public float Share(int cornerX, int cornerZ, int slot)
    {
        var x = Math.Clamp(cornerX, 0, cornersX - 1);
        var z = Math.Clamp(cornerZ, 0, cornersZ - 1);
        return shares[z * cornersX * Classes.Length + x * Classes.Length + slot];
    }

    /// <summary>Which class gets the opaque coat over this cell.</summary>
    public int DominantAt(int cellX, int cellZ)
    {
        var cellsX = cornersX - 1;
        var x = Math.Clamp(cellX, 0, cellsX - 1);
        var z = Math.Clamp(cellZ, 0, cornersZ - 2);
        return dominant[z * cellsX + x];
    }

    /// <summary>The slot a class occupies, or -1 for a surface that is drawn as another's colour.</summary>
    public static int SlotOf(TerrainSurface surface) => Array.IndexOf(Classes, Visual(surface));
}
