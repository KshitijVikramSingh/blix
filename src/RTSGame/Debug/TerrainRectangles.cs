using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Debug;

/// <summary>
/// The whole map merged into rectangles of identical ground, for drawing.
/// </summary>
/// <remarks>
/// The renderer's problem was the same one the router had, one layer over. Ground was drawn
/// coarsely everywhere and finely within forty metres of the camera, which put a visible seam
/// across the map: a ridge rendered as a stepped slope near the crowd and as five-metre blocks
/// beyond it. Any fixed radius does that, and moving the radius only moves the seam.
/// <para>
/// There is no need for a radius. A patch of ground that is all one surface at all one height is
/// exactly describable by a single box however large it is and however far away, and the
/// decomposition that finds those patches is the same row-run merge the routing layer uses —
/// run here over every cell rather than only the walkable ones, because a lake and a cliff face
/// have to be drawn even though nothing can stand on them.
/// </para>
/// <para>
/// So detail exists where the ground has detail, at any distance, and costs what the terrain
/// costs rather than what the map covers.
/// </para>
/// </remarks>
internal sealed class TerrainRectangles
{
    /// <summary>Height difference two cells may have and still be drawn as one flat box.</summary>
    private const float HeightTolerance = 0.05f;

    internal readonly record struct Patch(
        int MinimumX,
        int MinimumZ,
        int MaximumX,
        int MaximumZ,
        TerrainSurface Surface,
        float Height);

    private readonly List<Patch> patches = new();

    public IReadOnlyList<Patch> All => patches;
    public int Revision { get; }

    private TerrainRectangles(int revision) => Revision = revision;

    public static TerrainRectangles Build(TerrainMap terrain)
    {
        var grid = terrain.Transform;
        var result = new TerrainRectangles(terrain.Revision);

        var openStart = new int[grid.Width];
        var openEnd = new int[grid.Width];
        var openTop = new int[grid.Width];
        var openSurface = new TerrainSurface[grid.Width];
        var openHeight = new float[grid.Width];
        var openCount = 0;

        var runStart = new int[grid.Width];
        var runEnd = new int[grid.Width];
        var runSurface = new TerrainSurface[grid.Width];
        var runHeight = new float[grid.Width];

        for (var z = 0; z < grid.Height; z++)
        {
            var runCount = 0;
            var x = 0;
            while (x < grid.Width)
            {
                var cell = new GridCell(x, z);
                var surface = terrain.Surface(cell);
                var height = terrain.SampleHeight(grid.CellCenter(cell));
                var end = x;
                while (end + 1 < grid.Width)
                {
                    var next = new GridCell(end + 1, z);
                    if (terrain.Surface(next) != surface) break;
                    if (MathF.Abs(terrain.SampleHeight(grid.CellCenter(next)) - height) > HeightTolerance) break;
                    end++;
                }

                runStart[runCount] = x;
                runEnd[runCount] = end;
                runSurface[runCount] = surface;
                runHeight[runCount] = height;
                runCount++;
                x = end + 1;
            }

            var carried = 0;
            var carriedStart = new int[runCount];
            var carriedEnd = new int[runCount];
            var carriedTop = new int[runCount];
            var carriedSurface = new TerrainSurface[runCount];
            var carriedHeight = new float[runCount];
            var matched = new bool[openCount];

            for (var run = 0; run < runCount; run++)
            {
                var extended = -1;
                for (var open = 0; open < openCount; open++)
                {
                    if (matched[open]) continue;
                    if (openStart[open] != runStart[run] || openEnd[open] != runEnd[run]) continue;
                    if (openSurface[open] != runSurface[run]) continue;
                    if (MathF.Abs(openHeight[open] - runHeight[run]) > HeightTolerance) continue;
                    extended = open;
                    break;
                }

                carriedStart[carried] = runStart[run];
                carriedEnd[carried] = runEnd[run];
                carriedSurface[carried] = runSurface[run];
                carriedHeight[carried] = runHeight[run];
                carriedTop[carried] = extended >= 0 ? openTop[extended] : z;
                if (extended >= 0) matched[extended] = true;
                carried++;
            }

            for (var open = 0; open < openCount; open++)
            {
                if (matched[open]) continue;
                result.patches.Add(new Patch(
                    openStart[open],
                    openTop[open],
                    openEnd[open],
                    z - 1,
                    openSurface[open],
                    openHeight[open]));
            }

            for (var i = 0; i < carried; i++)
            {
                openStart[i] = carriedStart[i];
                openEnd[i] = carriedEnd[i];
                openTop[i] = carriedTop[i];
                openSurface[i] = carriedSurface[i];
                openHeight[i] = carriedHeight[i];
            }

            openCount = carried;
        }

        for (var open = 0; open < openCount; open++)
        {
            result.patches.Add(new Patch(
                openStart[open],
                openTop[open],
                openEnd[open],
                grid.Height - 1,
                openSurface[open],
                openHeight[open]));
        }

        return result;
    }
}
