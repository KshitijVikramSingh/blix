using System.Numerics;
using RTSGame.Simulation.Spatial;

using RTSGame.Simulation.Persistence;

namespace RTSGame.Simulation.Terrain;

internal sealed class TerrainMap
{
    private readonly float[] vertexHeights;
    private readonly TerrainSurface[] surfaces;
    private readonly Dictionary<int, bool[]> walkableNeighborhoods = new();
    private int walkableNeighborhoodRevision = -1;

    public const float MaximumTraversableGrade = 0.82f;
    public const float MaximumStepHeight = 0.46f;
    public GridTransform Transform { get; }
    public float HalfExtent => Transform.Width * Transform.CellSize * 0.5f;
    public Vector2 Minimum => Transform.Origin;
    public Vector2 Maximum => Transform.Maximum;
    public int Revision { get; private set; }

    public TerrainMap(GridTransform transform)
    {
        Transform = transform;
        vertexHeights = new float[(transform.Width + 1) * (transform.Height + 1)];
        surfaces = Enumerable.Repeat(TerrainSurface.Grass, transform.Width * transform.Height).ToArray();
    }

    public bool Contains(Vector2 position, float inset = 0f) =>
        position.X >= Minimum.X + inset && position.X <= Maximum.X - inset &&
        position.Y >= Minimum.Y + inset && position.Y <= Maximum.Y - inset;

    public Vector2 ClampPosition(Vector2 position, float inset = 0.55f) => new(
        Math.Clamp(position.X, Minimum.X + inset, Maximum.X - inset),
        Math.Clamp(position.Y, Minimum.Y + inset, Maximum.Y - inset));

    public TerrainSurface SampleSurface(Vector2 position)
    {
        var clamped = Vector2.Clamp(position, Minimum, Maximum - new Vector2(0.0001f));
        Transform.TryWorldToCell(clamped, out var cell);
        return Surface(cell);
    }

    public TerrainSurface Surface(GridCell cell) => Transform.Contains(cell)
        ? surfaces[Transform.Index(cell)]
        : TerrainSurface.Impassable;

    public bool IsPassable(Vector2 position) => TerrainSurfaceRules.IsPassable(SampleSurface(position));
    public float PathCost(Vector2 position) => TerrainSurfaceRules.PathCost(SampleSurface(position));
    public float SpeedMultiplier(Vector2 position) => TerrainSurfaceRules.SpeedMultiplier(SampleSurface(position));

    public void SetSurface(GridCell cell, TerrainSurface surface)
    {
        if (!Transform.Contains(cell)) throw new ArgumentOutOfRangeException(nameof(cell));
        var index = Transform.Index(cell);
        if (surfaces[index] == surface) return;
        surfaces[index] = surface;
        Revision++;
    }

    public float VertexHeight(int x, int z)
    {
        x = Math.Clamp(x, 0, Transform.Width);
        z = Math.Clamp(z, 0, Transform.Height);
        return vertexHeights[z * (Transform.Width + 1) + x];
    }

    public void SetVertexHeight(int x, int z, float height)
    {
        if (x < 0 || x > Transform.Width || z < 0 || z > Transform.Height)
            throw new ArgumentOutOfRangeException($"Terrain vertex ({x}, {z}) is outside the height field.");
        var index = z * (Transform.Width + 1) + x;
        if (MathF.Abs(vertexHeights[index] - height) < 0.00001f) return;
        vertexHeights[index] = height;
        Revision++;
    }

    /// <summary>
    /// Replaces the whole height field at once.
    /// </summary>
    /// <remarks>
    /// One revision bump for the lot, which is the reason it exists: generating a 600 m map through
    /// <see cref="SetVertexHeight"/> moves the revision 1.4 million times, and everything keyed against it
    /// — the navigation raster, every cached flow field — is looking at a number that has no business
    /// having moved that far. A generator writes the field; it does not edit it a vertex at a time.
    /// </remarks>
    /// <summary>Where the water on this ground goes, or null on a map with no relief.</summary>
    /// <remarks>
    /// <b>Derived, so it is neither saved nor fingerprinted.</b> It is a pure function of the height field
    /// and <see cref="Drainage"/> is written to be reproducible on any machine, so a load re-solves it and
    /// gets the same answer rather than carrying it in the manifest. Which is the right call for a field of
    /// this size — it would be the largest thing in a save and the least necessary.
    /// <para>
    /// Read by both layers, like <see cref="Biomes"/> and for the same reason: generation paints the
    /// surfaces the water implies, and the renderer draws the water and picks what grows near it.
    /// </para>
    /// </remarks>
    public Drainage? Drainage { get; private set; }

    internal void SetDrainage(Drainage? drainage) => Drainage = drainage;

    /// <summary>What kind of country this whole landscape is.</summary>
    /// <remarks>
    /// Read by both layers, and for different halves of itself: the classifier reads its wetness ranks, which
    /// decide surfaces and are therefore simulation truth; the renderer reads its palette and its flora, which
    /// are dressing. One choice, two consumers, the same split as <see cref="Biomes"/>.
    /// </remarks>
    public Region Region { get; private set; } = Region.Downland;

    internal void SetRegion(Region region) => Region = region;

    internal void ReplaceHeights(ReadOnlySpan<float> heights)
    {
        if (heights.Length != vertexHeights.Length)
        {
            throw new ArgumentException(
                $"This terrain has {vertexHeights.Length} height vertices and was given {heights.Length}. " +
                "A height field is (width + 1) x (height + 1), not one per cell.",
                nameof(heights));
        }

        heights.CopyTo(vertexHeights);
        Revision++;
    }

    /// <summary>The authored ground: heights at the corners, a surface per cell, and the revision.</summary>
    /// <remarks>
    /// The revision goes out and comes back rather than being re-derived, because everything keyed
    /// against it — the navigation raster, the flow-field cache — would otherwise be looking at a
    /// number that had moved for no reason. Writing the heights back in on load bumps it once per
    /// vertex, which is why <see cref="RestoreRevision"/> exists.
    /// </remarks>
    internal void Write(WorldWriter writer)
    {
        writer.Int(Revision);
        writer.Blob<float>(vertexHeights);
        writer.Blob<TerrainSurface>(surfaces);
    }

    internal void Read(WorldReader reader)
    {
        var revision = reader.Int();
        reader.Blob<float>(vertexHeights);
        reader.Blob<TerrainSurface>(surfaces);
        walkableNeighborhoodRevision = -1;
        RestoreRevision(revision);
    }

    /// <summary>Puts the revision back where a load found it.</summary>
    internal void RestoreRevision(int revision) => Revision = revision;

    public float SampleHeight(Vector2 position)
    {
        var local = (Vector2.Clamp(position, Minimum, Maximum) - Minimum) / Transform.CellSize;
        var x0 = Math.Clamp((int)MathF.Floor(local.X), 0, Transform.Width - 1);
        var z0 = Math.Clamp((int)MathF.Floor(local.Y), 0, Transform.Height - 1);
        var tx = Math.Clamp(local.X - x0, 0f, 1f);
        var tz = Math.Clamp(local.Y - z0, 0f, 1f);
        var h00 = VertexHeight(x0, z0);
        var h10 = VertexHeight(x0 + 1, z0);
        var h01 = VertexHeight(x0, z0 + 1);
        var h11 = VertexHeight(x0 + 1, z0 + 1);
        // The simulation and renderer share the same diagonal: (0,0)-(1,1)
        // is not used; the cell is split across the (1,0)-(0,1) edge.
        if (tx + tz <= 1f)
        {
            return h00 + (h10 - h00) * tx + (h01 - h00) * tz;
        }
        var fromRight = 1f - tx;
        var fromBottom = 1f - tz;
        return h11 + (h01 - h11) * fromRight + (h10 - h11) * fromBottom;
    }

    public Vector3 SampleNormal(Vector2 position)
    {
        var offset = Transform.CellSize * 0.5f;
        var dx = SampleHeight(position + Vector2.UnitX * offset) -
                 SampleHeight(position - Vector2.UnitX * offset);
        var dz = SampleHeight(position + Vector2.UnitY * offset) -
                 SampleHeight(position - Vector2.UnitY * offset);
        return Vector3.Normalize(new Vector3(-dx, offset * 2f, -dz));
    }

    public float SampleGrade(Vector2 position)
    {
        var normal = SampleNormal(position);
        return MathF.Sqrt(normal.X * normal.X + normal.Z * normal.Z) / MathF.Max(normal.Y, 0.0001f);
    }

    public bool CanTraverse(Vector2 from, Vector2 to)
    {
        if (!Contains(to) || !IsPassable(to)) return false;
        var distance = Vector2.Distance(from, to);
        if (distance < 0.0001f) return true;
        var heightDelta = MathF.Abs(SampleHeight(to) - SampleHeight(from));
        return heightDelta <= MaximumStepHeight && heightDelta / distance <= MaximumTraversableGrade;
    }

    /// <summary>
    /// Whether a body of <paramref name="radius"/> centred here stands entirely on
    /// passable, walkably-graded ground.
    /// </summary>
    /// <remarks>
    /// The honest answer samples the body's outline — nine points, each needing a
    /// surface lookup and a grade, and each grade is four bilinear height samples
    /// of four clamped vertex fetches. Around a hundred and fifty height reads to
    /// answer one question, asked per sample of every swept step and per candidate
    /// of the solver's terrain fallback.
    /// <para>
    /// Most of the time the answer is knowable without looking: if every cell the outline's probes could
    /// reach is passable, and none of them is steep enough for a probe to refuse, the outline cannot fail
    /// and the only remaining question is whether the body is inside the map — which the bounds test above
    /// has already answered. <see cref="WalkableNeighborhood"/> precomputes where that holds, so the
    /// outline is only walked near ground steep enough to be in doubt. This is a memo, not an
    /// approximation: where it answers, it answers identically.
    /// </para>
    /// </remarks>
    public bool IsBodyTraversable(Vector2 center, float radius)
    {
        if (!Contains(center, radius + BodyFootprint.NavigationMargin)) return false;
        if (Transform.TryWorldToCell(center, out var bodyCell) &&
            WalkableNeighborhood(NeighborhoodCells(radius))[Transform.Index(bodyCell)])
        {
            return true;
        }
        for (var sample = 0; sample < 9; sample++)
        {
            var position = sample == 0
                ? center
                : center + new Vector2(
                    MathF.Cos((sample - 1) * MathF.Tau / 8f),
                    MathF.Sin((sample - 1) * MathF.Tau / 8f)) * radius;
            if (!Contains(position) || !IsPassable(position) ||
                SampleGrade(position) > MaximumTraversableGrade + 0.015f)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// How far, in cells, a body's grade probes can reach from the cell it stands in.
    /// </summary>
    /// <remarks>
    /// The outline reaches <c>radius</c>, each grade probe a further half cell, and
    /// bilinear height sampling a further cell's worth of vertices. Rounded up and
    /// widened by one, so the region tested is a superset of the region read — a
    /// larger level region is a stronger precondition, never a weaker one.
    /// </remarks>
    private int NeighborhoodCells(float radius) =>
        (int)MathF.Ceiling((radius + Transform.CellSize) / Transform.CellSize) + 1;

    /// <summary>
    /// Cells whose surroundings, out to <paramref name="radiusCells"/>, are all passable and all gentle
    /// enough that no probe inside them can refuse.
    /// </summary>
    /// <remarks>
    /// <b>It used to ask whether the neighbourhood was at one single height, and that is a proxy that only
    /// ever answered on flat ground.</b> The question it exists to shortcut is "could any grade probe
    /// inside this body's outline exceed the traversable limit", and a map that rolls gently cannot answer
    /// that with a height comparison — every neighbourhood on it spans some height, so the memo declined
    /// everywhere and every query walked nine outline samples of four bilinear height reads each, about a
    /// hundred and fifty vertex fetches to answer a question whose answer was obviously yes. Measured: a
    /// stress of two hundred bodies converging cost 13 ms a tick on the flat and 23 ms with relief, the
    /// same 1.75x at every amplitude — which is the signature of a fast path that has stopped firing rather
    /// than of work that scales with the ground.
    /// <para>
    /// So the memo bounds the grade instead. A cell's own four vertices bound the steepest gradient
    /// anywhere inside it: <c>sqrt(maxDx² + maxDz²) / cellSize</c>, where each term is the larger of the
    /// cell's two opposite edges. <see cref="SampleGrade"/> is a central difference over one cell's width,
    /// so it straddles at most a cell in each direction — dilating the bound by one extra cell covers every
    /// window a probe can read, and dilating it by the body's reach covers every probe the outline can
    /// place.
    /// </para>
    /// <para>
    /// It remains a memo and not an approximation, which is the property worth protecting: where it answers
    /// true, every probe the long path would take is provably under the limit, so the two cannot disagree.
    /// And on ground at one height the bound is zero, so a flat map takes the fast path exactly as before.
    /// </para>
    /// <para>
    /// Built by dilating passability and the slope bound separably — one pass per axis over the map rather
    /// than a neighbourhood scan per cell — and rebuilt when the terrain revision moves, which is the same
    /// signal the navigation raster keys on.
    /// </para>
    /// </remarks>
    private bool[] WalkableNeighborhood(int radiusCells)
    {
        if (walkableNeighborhoodRevision != Revision)
        {
            walkableNeighborhoods.Clear();
            walkableNeighborhoodRevision = Revision;
        }
        if (walkableNeighborhoods.TryGetValue(radiusCells, out var cached)) return cached;

        var width = Transform.Width;
        var height = Transform.Height;
        var count = width * height;
        var passable = new bool[count];
        var slope = new float[count];
        var cellSize = Transform.CellSize;
        for (var z = 0; z < height; z++)
        for (var x = 0; x < width; x++)
        {
            var index = z * width + x;
            passable[index] = TerrainSurfaceRules.IsPassable(surfaces[index]);
            var h00 = VertexHeight(x, z);
            var h10 = VertexHeight(x + 1, z);
            var h01 = VertexHeight(x, z + 1);
            var h11 = VertexHeight(x + 1, z + 1);
            // The steepest this cell gets on each axis is the larger of its two opposite edges, and the two
            // combine as a gradient does. Exact for the bilinear patch the height sampler interpolates.
            var acrossX = MathF.Max(MathF.Abs(h10 - h00), MathF.Abs(h11 - h01));
            var acrossZ = MathF.Max(MathF.Abs(h01 - h00), MathF.Abs(h11 - h10));
            slope[index] = MathF.Sqrt(acrossX * acrossX + acrossZ * acrossZ) / cellSize;
        }

        // One extra cell, because a grade probe is a central difference and reads half a cell either side
        // of wherever the outline put it.
        var reach = radiusCells + 1;

        // Cells outside the map are treated as impassable so a body near the edge never takes the fast
        // path on the strength of ground that does not exist.
        var rowPassable = new bool[count];
        var rowSlope = new float[count];
        for (var z = 0; z < height; z++)
        for (var x = 0; x < width; x++)
        {
            var all = true;
            var steepest = 0f;
            for (var offset = -reach; offset <= reach; offset++)
            {
                var sample = x + offset;
                if (sample < 0 || sample >= width) { all = false; break; }
                var index = z * width + sample;
                all &= passable[index];
                steepest = MathF.Max(steepest, slope[index]);
            }

            var target = z * width + x;
            rowPassable[target] = all;
            rowSlope[target] = steepest;
        }

        var result = new bool[count];
        for (var z = 0; z < height; z++)
        for (var x = 0; x < width; x++)
        {
            var all = true;
            var steepest = 0f;
            for (var offset = -reach; offset <= reach; offset++)
            {
                var sample = z + offset;
                if (sample < 0 || sample >= height) { all = false; break; }
                var index = sample * width + x;
                all &= rowPassable[index];
                steepest = MathF.Max(steepest, rowSlope[index]);
            }

            result[z * width + x] = all && steepest <= MaximumTraversableGrade;
        }

        walkableNeighborhoods[radiusCells] = result;
        return result;
    }

    /// <summary>
    /// What share of the map the body-traversal memo can answer without walking an outline.
    /// </summary>
    /// <remarks>
    /// The number that says whether the fast path is firing, which is not the same question as whether the
    /// tick got faster — a memo can be repaired and the time still be somewhere else, which is exactly what
    /// happened when this one was. Reported by the relief sweep per amplitude.
    /// </remarks>
    internal float NeighborhoodCoverage(float radius)
    {
        var memo = WalkableNeighborhood(NeighborhoodCells(radius));
        var answered = 0;
        foreach (var cell in memo)
        {
            if (cell) answered++;
        }

        return memo.Length == 0 ? 0f : answered / (float)memo.Length;
    }

    public bool CanPlace(Vector2 center, Vector2 halfExtents)
    {
        Span<Vector2> samples = stackalloc Vector2[5]
        {
            center,
            center + new Vector2(-halfExtents.X, -halfExtents.Y),
            center + new Vector2( halfExtents.X, -halfExtents.Y),
            center + new Vector2(-halfExtents.X,  halfExtents.Y),
            center + new Vector2( halfExtents.X,  halfExtents.Y),
        };
        var minimumHeight = float.PositiveInfinity;
        var maximumHeight = float.NegativeInfinity;
        foreach (var sample in samples)
        {
            if (!Contains(sample) || !IsPassable(sample)) return false;
            var height = SampleHeight(sample);
            minimumHeight = MathF.Min(minimumHeight, height);
            maximumHeight = MathF.Max(maximumHeight, height);
        }
        return maximumHeight - minimumHeight <= 0.32f && SampleGrade(center) <= 0.45f;
    }

    /// <summary>
    /// Where a ray meets the ground, or false if it never does.
    /// </summary>
    /// <remarks>
    /// The march used to start at the eye and give up after 150 m, which was more than a 30 m
    /// square could ever need and less than the near edge of a 600 m one: a camera pulled back
    /// far enough to see the map put every blade of grass out of range, so the pointer found no
    /// ground and orders were silently dropped over most of the screen. It read as "beyond a
    /// certain distance they will not take instructions", which is exactly what it was.
    /// <para>
    /// It now clips the ray against the map's own bounds before stepping — entry and exit in
    /// the horizontal plane, and the height above which no ground can exist — so the march
    /// covers the part of the ray that could possibly hit something and no more. That makes it
    /// correct at any extent and cheaper than the fixed window it replaces, since a ray aimed
    /// at the sky now costs nothing instead of seven hundred and fifty samples.
    /// </para>
    /// </remarks>
    public bool TryRaycast(
        Vector3 origin,
        Vector3 direction,
        out Vector2 world,
        float maximumDistance = float.PositiveInfinity)
    {
        world = default;
        const float step = 0.20f;
        // Nothing on this map is higher than this, so a descending ray cannot touch ground
        // until it is below it, and an ascending ray already above it never will.
        const float highestGround = 64f;
        var enter = 0f;
        var exit = MathF.Min(maximumDistance, HorizontalExit(origin, direction, ref enter));
        if (direction.Y < -0.0001f)
        {
            enter = MathF.Max(enter, (origin.Y - highestGround) / -direction.Y);
        }
        else if (origin.Y > highestGround)
        {
            return false;
        }

        enter = MathF.Max(0f, enter);
        if (exit <= enter) return false;

        var previousTime = enter;
        var previousInside = false;
        var previousDifference = float.PositiveInfinity;
        for (var time = enter; time <= exit; time += step)
        {
            var point = origin + direction * time;
            var horizontal = new Vector2(point.X, point.Z);
            var inside = Contains(horizontal);
            if (inside)
            {
                var difference = point.Y - SampleHeight(horizontal);
                if (difference <= 0f && previousInside && previousDifference > 0f)
                {
                    var low = previousTime;
                    var high = time;
                    for (var iteration = 0; iteration < 10; iteration++)
                    {
                        var middle = (low + high) * 0.5f;
                        var middlePoint = origin + direction * middle;
                        var middleWorld = new Vector2(middlePoint.X, middlePoint.Z);
                        if (middlePoint.Y > SampleHeight(middleWorld)) low = middle;
                        else high = middle;
                    }
                    var hit = origin + direction * ((low + high) * 0.5f);
                    world = new Vector2(hit.X, hit.Z);
                    return true;
                }
                previousDifference = difference;
            }
            previousInside = inside;
            previousTime = time;
        }

        return false;
    }

    /// <summary>
    /// Range of ray parameters over which the ray is inside the map's horizontal bounds.
    /// </summary>
    private float HorizontalExit(Vector3 origin, Vector3 direction, ref float enter)
    {
        var exit = float.PositiveInfinity;
        Slab(origin.X, direction.X, Minimum.X, Maximum.X, ref enter, ref exit);
        Slab(origin.Z, direction.Z, Minimum.Y, Maximum.Y, ref enter, ref exit);
        return exit;

        static void Slab(float start, float delta, float low, float high, ref float enter, ref float exit)
        {
            if (MathF.Abs(delta) < 0.0001f)
            {
                // Parallel to this pair of edges: either always between them or never.
                if (start < low || start > high) exit = float.NegativeInfinity;
                return;
            }

            var first = (low - start) / delta;
            var second = (high - start) / delta;
            if (first > second) (first, second) = (second, first);
            enter = MathF.Max(enter, first);
            exit = MathF.Min(exit, second);
        }
    }
}
