namespace RTSGame.Simulation.Terrain;

using System.Numerics;

/// <summary>
/// Where the water on a height field goes, and how much of it passes through each place.
/// </summary>
/// <remarks>
/// <b>This is the field <see cref="Biomes"/> was approximating.</b> That classifier reads grade, height and
/// a twenty-metre concavity test, and says so in its own remarks: "three signals, all of them about water,
/// which is what actually decides what grows where". They are three local stand-ins for one global
/// quantity, and a local stand-in cannot know that the hollow it is standing in is fed by half a hillside.
/// Upslope area can, and everything else follows from it — a river is where there is a lot of it, a marsh is
/// where there is a lot of it and nowhere for it to go, a moor is where there is none, and the best soil on
/// the map is where a moderate amount of it moves slowly across level ground.
/// <para>
/// <b>Solved on a four-metre lattice, not the navigation grid.</b> The navigation grid is half a metre
/// because that is the resolution a body's clearance is decided at; a catchment is sixty-six metres and a
/// valley is a few tens, so a four-metre lattice resolves everything this is for at a thirty-second of the
/// cells. That matters because erosion runs this solver dozens of times.
/// </para>
/// <para>
/// <b>Every step is deterministic, and that is a requirement rather than a preference.</b> Terrain is
/// simulation truth: it is fingerprinted, it is saved, and two runs of the same world have to agree about it
/// on any machine. So the priority queue breaks ties on index, the eight-neighbour search breaks ties on
/// index, and the accumulation walks cells in a fully-ordered sequence rather than in whatever order a
/// parallel reduction happens to finish in.
/// </para>
/// </remarks>
internal sealed class Drainage
{
    /// <summary>
    /// The lift given to a filled cell over the one it drains to, in metres.
    /// </summary>
    /// <remarks>
    /// Priority-Flood fills a depression to its outlet's height, which leaves a dead flat lake surface that
    /// has no downhill direction anywhere on it — so flow routing stalls and a lake accumulates nothing. A
    /// tenth of a millimetre per cell is far below anything the renderer or the router can see, and it is
    /// enough to give every cell on a flat a defined receiver.
    /// </remarks>
    private const float FlatGradient = 1e-4f;

    /// <summary>Standing water shallower than this is wet ground, not water.</summary>
    public const float PondDepthMetres = 0.35f;

    /// <summary>
    /// The catchment a body of standing water needs above it before it is water at all.
    /// </summary>
    /// <remarks>
    /// <b>A lake cannot be bigger than what drains into it, and depression filling does not know that.</b>
    /// Priority-Flood raises every hollow to its spill point because routing requires it, so a broad shallow
    /// dish high on a hillside comes back with twenty centimetres of "water" across the whole of it — a large
    /// lake sitting upstream of anything that could fill it. Which is the one thing about water nobody
    /// accepts.
    /// <para>
    /// It lives here rather than in <see cref="Biomes"/> because two things now ask it: the classifier, which
    /// decides whether a cell is water, and <see cref="LevelAt"/>, which decides how high that water stands.
    /// Those two disagreeing would put a water surface over dry ground or leave a lake with no surface.
    /// </para>
    /// </remarks>
    public const float LakeCatchmentMetres2 = 12_000f;

    /// <summary>Below this width a line of water is a damp trace, not a watercourse.</summary>
    public const float TraceWidthMetres = 1.5f;

    private readonly int side;
    private readonly float cellMetres;
    private readonly float[] filled;
    private readonly int[] receiver;
    private readonly float[] area;
    private readonly float[] lake;
    private readonly float[] ground;
    private float[]? wetness;
    private float[]? ranked;
    private float[]? level;

    /// <summary>
    /// Width painted along an authored watercourse, in metres, or zero where none was authored.
    /// </summary>
    /// <remarks>
    /// <b>The layout said "a twelve-metre river" and nothing read it.</b> Width came only from
    /// <see cref="WidthOf"/>, so the authored number shaped the trough erosion was invited to deepen and had
    /// no say at all in the water that ended up drawn — which is most of why no map ever had a body of water on
    /// it, only lines. A statement about geography that the geography ignores is not a statement.
    /// <para>
    /// The larger of the two wins. Accumulation can still make a river wider than it was authored — a trunk
    /// gathering half a canvas should — but it can no longer make it narrower than the thing that put it
    /// there.
    /// </para>
    /// </remarks>
    private float[]? authored;

    /// <summary>
    /// The height the sea stands at, or negative infinity on a map with no coast.
    /// </summary>
    /// <remarks>
    /// <b>The only absolute level here, and everything else is relative.</b> A lake fills to its own spill
    /// point and a channel stands above its own bed — both are answers to "how high relative to what is
    /// nearby". The sea is a height full stop, so any cell below it is under water without needing a
    /// depression, a catchment or a channel width to justify it.
    /// </remarks>
    private float sea = float.NegativeInfinity;

    internal void SetSeaLevel(float level) => sea = level;

    /// <summary>
    /// How readily this climate holds standing water, as a multiplier on the catchment a lake needs.
    /// </summary>
    /// <remarks>
    /// <b>Climate has to reach the water that nobody authored, or half of it escapes.</b> Scaling the layout's
    /// own rivers and basins covers what a statement asked for — and measured, it changed nothing on a map whose
    /// largest body was <em>emergent</em>: the composed secondary's ridges dammed the primary's trough and
    /// filled a hollow the layout never mentioned. Which is the composition working, and a hole in the climate.
    /// <para>
    /// A dry region needs a bigger catchment before a hollow holds water, which is what evaporation is when you
    /// only have one number for it. Dry scrub asks for about two and a half times the drainage; fen country
    /// asks for less than three quarters.
    /// </para>
    /// </remarks>
    private float thirst = 1f;

    internal void SetWaterScale(float scale) => thirst = MathF.Max(0.05f, scale);

    /// <summary>Whether this map has a coast at all.</summary>
    public bool HasSea => !float.IsNegativeInfinity(sea);

    internal void PaintAuthoredWidth(float[] widths) => authored = widths;

    private Drainage(
        int side,
        float cellMetres,
        float[] filled,
        int[] receiver,
        float[] area,
        float[] lake,
        float[] ground)
    {
        this.side = side;
        this.cellMetres = cellMetres;
        this.filled = filled;
        this.receiver = receiver;
        this.area = area;
        this.lake = lake;
        this.ground = ground;
    }

    /// <summary>Samples per side of the lattice this was solved on.</summary>
    public int Side => side;

    /// <summary>Metres a side of one drainage cell, for a reader that has to walk the lattice itself.</summary>
    /// <remarks>
    /// §151's criteria walk the receiver chain to check that a watercourse never runs uphill, which needs the
    /// lattice's own spacing rather than the navigation grid's.
    /// </remarks>
    public float CellMetres => cellMetres;

    /// <summary>World position of the lattice's first sample, so world queries need no second lookup.</summary>
    public Vector2 Origin { get; internal set; }

    /// <summary>
    /// How wide the watercourse at a place is, in metres, or zero where there is not one.
    /// </summary>
    /// <remarks>
    /// <b>Width goes as the square root of upslope area, which is not a fit — it is what the arithmetic
    /// gives.</b> A channel carries discharge roughly in proportion to the area draining into it; a channel
    /// deep enough to carry it is roughly as deep as it is wide over a broad range; so width goes as the
    /// root of area. Which is why one coefficient covers the whole map: the same rule that makes the trunk
    /// six metres across makes its headwaters something you step over, and nobody had to place either.
    /// <para>
    /// The coefficient is set from the wanted trunk width on a 600 m map and then left alone, because
    /// changing it changes every crossing on every map at once — that is the point of having one.
    /// </para>
    /// <para>
    /// <b>It has been recalibrated twice, and both times because the catchment changed rather than the
    /// river.</b> First when the frontier arrived, which funnels the water into gates instead of letting it
    /// leak off all four sides and roughly doubled the largest catchment. Then when the inherited inflow
    /// arrived, which multiplied it again. Each time every channel on the map got wider for a reason that had
    /// nothing to do with how wide a channel should be — which is exactly the coefficient doing its job, and
    /// the reason to keep exactly one of it rather than a table of widths per stream.
    /// </para>
    /// <para>
    /// At 0.008 with a doubled inflow the trunk lands around seven metres — a river to be crossed at a ford —
    /// the largest local tributary lands under two, which is a wade, and a stream draining a twentieth of the
    /// map lands near one, which is below the width anything is drawn at. That spread is the point: it is the
    /// difference between a map with a river on it and a map with drainage everywhere.
    /// </para>
    /// </remarks>
    public static float WidthOf(float upslopeArea) => 0.008f * MathF.Sqrt(MathF.Max(0f, upslopeArea));

    /// <summary>Square metres draining through a world position.</summary>
    public float AreaAt(Vector2 world) => Sample(area, world - Origin);

    /// <summary>Standing water at a world position, in metres.</summary>
    public float LakeDepthAt(Vector2 world) => Sample(lake, world - Origin);

    /// <summary>The watercourse's width at a world position, in metres.</summary>
    /// <remarks>
    /// <b>Nearest cell, not interpolated, and the difference was a map three times too wet.</b> Upslope area
    /// is the most non-linear field here — a trunk channel carries a hundred times what the ground a cell
    /// away does — so a bilinear read of it hands a fraction of the trunk's discharge to its neighbours, and
    /// a fraction of a hundred is still a river. Painted that way, seven per cent of the map came out as
    /// water on a map with about three thousand square metres of actual channel in it.
    /// <para>
    /// Interpolation is right for <see cref="AreaAt"/>, which feeds a wetness index that wants to vary
    /// smoothly across a hillside, and wrong for a question whose answer is a boundary. The cost of the
    /// nearest read is honest and worth stating: a channel is a whole lattice cell wide, so four metres is
    /// the narrowest stream this can express. A one-metre brook needs the centreline drawn as a polyline and
    /// the water painted about it, which is the next thing to build and not this.
    /// </para>
    /// </remarks>
    public float WidthAt(Vector2 world)
    {
        var local = world - Origin;
        var x = Math.Clamp((int)MathF.Round(local.X / cellMetres), 0, side - 1);
        var z = Math.Clamp((int)MathF.Round(local.Y / cellMetres), 0, side - 1);
        var index = z * side + x;
        var gathered = WidthOf(area[index]);
        return authored is null ? gathered : MathF.Max(gathered, authored[index]);
    }

    /// <summary>The depression-filled surface: what the ground would be if every hole were full.</summary>
    public float[] Filled => filled;

    /// <summary>Index of the cell each cell drains into, or -1 where water leaves the map.</summary>
    public int[] Receiver => receiver;

    /// <summary>Square metres of ground draining through each cell, including its own.</summary>
    public float[] Area => area;

    /// <summary>How deep the standing water is, which is zero everywhere that drains.</summary>
    /// <remarks>
    /// The difference between the filled surface and the real one. It costs nothing to keep, because
    /// depression filling has to find every sink in order to route water at all — so "which of these holes
    /// would hold water" is a subtraction rather than a second algorithm. A high tarn and a valley lake are
    /// the two most recognisable things a map of this size can have.
    /// </remarks>
    public float[] LakeDepth => lake;

    /// <param name="inflow">
    /// Extra upslope area entering particular cells from outside the map, in square metres.
    /// </param>
    /// <remarks>
    /// <b>The inflow is what stops the map being a fractal of identical little valleys.</b> Without it every
    /// cell is a source of exactly its own area, which is uniform rain over the tile — and uniform rain means
    /// every knoll on the map grows its own dendritic network from its own flank. Each one is correct and the
    /// whole is featureless, because nothing is bigger than anything else. Reported from the chair as
    /// "everything seems nice locally but doesn't make any overall sense".
    /// <para>
    /// The real mistake underneath was treating six hundred metres as a whole catchment. Drainage organises
    /// at kilometres; a 600 m tile is a fragment, and the river crossing a fragment <em>came from somewhere
    /// else</em>. Its size has nothing to do with the thirty-six hectares you can see. So an inherited
    /// discharge enters at one edge, which makes the trunk dominate by a factor no local hillside can reach —
    /// and once the trunk dominates, the local streams fall below the width a watercourse needs and stop being
    /// drawn at all. One valley with a couple of tributaries, which is what a fragment of country looks like.
    /// </para>
    /// </remarks>
    public static Drainage Solve(float[] heights, int side, float cellMetres, float[]? inflow = null)
    {
        ArgumentNullException.ThrowIfNull(heights);
        if (heights.Length != side * side)
        {
            throw new ArgumentException(
                $"A {side}x{side} lattice has {side * side} samples and was given {heights.Length}.",
                nameof(heights));
        }

        var filled = FillDepressions(heights, side);
        var receiver = RouteDownhill(filled, side, cellMetres);
        var area = Accumulate(filled, receiver, side, cellMetres, inflow);
        var lake = new float[heights.Length];
        for (var i = 0; i < heights.Length; i++) lake[i] = MathF.Max(0f, filled[i] - heights[i]);
        // <b>Wetness is not computed here, and that was worth 2.5 seconds a roll.</b> Erosion solves the
        // whole flow field a dozen times and never asks about wetness once — only the classifier does, of the
        // final surface. Computing it eagerly meant twelve passes over every cell's eight neighbours plus
        // twelve sorts of the whole lattice, all of it thrown away.
        return new Drainage(side, cellMetres, filled, receiver, area, lake, (float[])heights.Clone());
    }

    /// <summary>
    /// Priority-Flood: raises every sink to the lowest lip it could spill over.
    /// </summary>
    /// <remarks>
    /// <b>Nothing downstream works without this.</b> A composed height field is full of small pits — two
    /// landforms meeting, an octave of noise landing badly — and a pit swallows all the water that reaches
    /// it, so accumulation stops there and every river on the map is a few cells long. Filling first means
    /// every cell has a downhill path to the edge, so a river is a river all the way out.
    /// <para>
    /// The algorithm is the classic one and its shape is the reason it is correct: start from the boundary,
    /// always expand from the <em>lowest</em> cell reached so far, and give each newly reached cell the
    /// higher of its own height and its discoverer's. Because expansion is always from the lowest available
    /// lip, the first path that reaches a cell is the lowest path there is, so the height it is given is
    /// exactly the level it would fill to. One pass, no iteration to convergence.
    /// </para>
    /// </remarks>
    private static float[] FillDepressions(float[] heights, int side)
    {
        var filled = new float[heights.Length];
        var settled = new bool[heights.Length];
        Array.Copy(heights, filled, heights.Length);

        // Keyed on (height, index) so two cells at the same height always come out in the same order. A
        // plain height key leaves the order to the heap's internal array moves, which is stable within one
        // run and not across a rebuild.
        var queue = new PriorityQueue<int, (float Height, int Index)>();
        for (var z = 0; z < side; z++)
        for (var x = 0; x < side; x++)
        {
            if (x != 0 && z != 0 && x != side - 1 && z != side - 1) continue;
            var index = z * side + x;
            settled[index] = true;
            queue.Enqueue(index, (heights[index], index));
        }

        while (queue.TryDequeue(out var current, out _))
        {
            var cx = current % side;
            var cz = current / side;
            for (var k = 0; k < 8; k++)
            {
                var nx = cx + Offsets[k].X;
                var nz = cz + Offsets[k].Z;
                if (nx < 0 || nz < 0 || nx >= side || nz >= side) continue;
                var next = nz * side + nx;
                if (settled[next]) continue;
                settled[next] = true;
                filled[next] = MathF.Max(heights[next], filled[current] + FlatGradient);
                queue.Enqueue(next, (filled[next], next));
            }
        }

        return filled;
    }

    /// <summary>
    /// D8: every cell sends all its water to whichever of its eight neighbours is steepest downhill.
    /// </summary>
    /// <remarks>
    /// Single-receiver on purpose. Spreading flow over every downhill neighbour gives smoother
    /// accumulation and blurs the thing this is for: a channel is a channel because the water in it is
    /// <em>concentrated</em>, and a multiple-flow-direction field of the same catchment gives a broad damp
    /// smear where a valley floor should have a river in it. The known cost is that a D8 channel can only
    /// run in eight directions, so it zigzags on a diagonal — at four metres a cell that is below what the
    /// ribbon drawn along its centreline shows.
    /// </remarks>
    private static int[] RouteDownhill(float[] filled, int side, float cellMetres)
    {
        var receiver = new int[filled.Length];
        for (var z = 0; z < side; z++)
        for (var x = 0; x < side; x++)
        {
            var index = z * side + x;
            if (x == 0 || z == 0 || x == side - 1 || z == side - 1)
            {
                // The edge of the map is the sea, or is somewhere else's problem. Either way water leaves.
                receiver[index] = -1;
                continue;
            }

            var best = -1;
            var bestFall = 0f;
            for (var k = 0; k < 8; k++)
            {
                var nx = x + Offsets[k].X;
                var nz = z + Offsets[k].Z;
                var next = nz * side + nx;
                // Per metre travelled, so a diagonal step is not preferred merely for being longer.
                var fall = (filled[index] - filled[next]) / (Offsets[k].Distance * cellMetres);
                if (fall <= bestFall) continue;
                bestFall = fall;
                best = next;
            }

            receiver[index] = best;
        }

        return receiver;
    }

    /// <summary>
    /// Pushes each cell's own area, plus everything already arrived, into its receiver.
    /// </summary>
    /// <remarks>
    /// Walking the cells from the highest filled surface downwards is what makes one pass enough: a cell's
    /// receiver is always lower than it is, so by the time a cell is visited every cell that drains into it
    /// has already been visited and paid in. No iteration, no topological sort — the height field already
    /// is the topological order.
    /// </remarks>
    private static float[] Accumulate(
        float[] filled,
        int[] receiver,
        int side,
        float cellMetres,
        float[]? inflow)
    {
        var area = new float[filled.Length];
        var own = cellMetres * cellMetres;
        for (var i = 0; i < area.Length; i++) area[i] = own + (inflow is null ? 0f : inflow[i]);

        // <b>Sorted on a key array rather than with a comparison delegate, which is several times faster on
        // two hundred thousand cells and is safe here for a reason worth stating.</b> The delegate version
        // broke ties on index to be deterministic; a key sort is unstable, so equal keys come out in an
        // unspecified order. That cannot change the answer: two cells at exactly the same filled height never
        // drain into each other, because <see cref="RouteDownhill"/> requires a strictly positive fall. So
        // their relative order is not merely stable enough — it is unobservable.
        var order = new int[filled.Length];
        var keys = new float[filled.Length];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
            keys[i] = -filled[i];
        }

        Array.Sort(keys, order);

        foreach (var index in order)
        {
            var into = receiver[index];
            if (into >= 0) area[into] += area[index];
        }

        return area;
    }

    /// <summary>
    /// How wet a place is: the topographic wetness index, <c>ln(area / grade)</c>.
    /// </summary>
    /// <remarks>
    /// High where a lot of water arrives and little of it leaves, which is a fen; low where little arrives and
    /// it leaves fast, which is a moor. A logarithm because upslope area spans four orders of magnitude across
    /// one map, so a ridge and a valley floor should be a few units apart rather than a factor of ten thousand.
    /// </remarks>
    /// <summary>
    /// How high the water stands here, or the ground itself where there is none.
    /// </summary>
    /// <remarks>
    /// <b>Water had no <em>level</em>, and that is the whole of why it did not read as water.</b> It was a
    /// colour painted on the ground: so a lake was a wet-looking patch of sloping hillside rather than a flat
    /// plane, a channel followed every ravine because a per-cell colour has no banks, and the reported effect
    /// was water that "seeps into tributaries and ravines and just lays low and still". All three are the same
    /// missing thing. A surface at a level gives a lake its flatness, a river its banks, and a shore its line
    /// — the shore is simply where the level meets the ground, and nobody has to draw it.
    /// <para>
    /// Two sources, taken as the higher. A <b>lake</b> stands at its spill level, which depression filling
    /// already computed, subject to the catchment rule above. A <b>channel</b> stands a little above its bed,
    /// as the root of its width — which keeps the trunk visibly deeper than a tributary without a second
    /// table, for the same reason width itself is a root of area.
    /// </para>
    /// </remarks>
    public float LevelAt(Vector2 world) => Sample(EnsureLevel(), world - Origin);

    /// <summary>The bed under the water, so a depth is a subtraction.</summary>
    public float BedAt(Vector2 world) => Sample(ground, world - Origin);

    private float[] EnsureLevel()
    {
        if (level is not null) return level;
        level = new float[ground.Length];

        // <b>A lake is judged as a lake, not cell by cell, and that was the bug that made every basin a
        // thread.</b> The catchment rule is right — standing water needs something draining into it — but
        // asking it of each cell asks the wrong thing. Flow across a filled surface runs to the outlet, so the
        // cells on the far side of a lake drain almost nothing and fail a per-cell test while the handful along
        // the path to the outlet pass it. What came out was a twelve-metre-deep basin a hundred and seventy
        // metres across, rendered as a channel thirty-five metres wide — which is why three authored lakes
        // changed nothing anybody could see.
        //
        // So the depression is labelled first and asked about as a whole: how deep does it get anywhere, and
        // how much drains through it at its busiest point. Both are properties of the body. Same two
        // thresholds, finally applied to the thing they were always about.
        var standing = new bool[ground.Length];
        var seen = new bool[ground.Length];
        var stack = new Stack<int>();
        var members = new List<int>();
        for (var start = 0; start < ground.Length; start++)
        {
            if (seen[start] || filled[start] - ground[start] <= 0.02f) continue;
            stack.Push(start);
            seen[start] = true;
            members.Clear();
            var deepest = 0f;
            var gathered = 0f;
            var lowX = int.MaxValue;
            var highX = int.MinValue;
            var lowZ = int.MaxValue;
            var highZ = int.MinValue;
            while (stack.Count > 0)
            {
                var index = stack.Pop();
                members.Add(index);
                deepest = MathF.Max(deepest, filled[index] - ground[index]);
                gathered = MathF.Max(gathered, area[index]);
                var cx = index % side;
                var cz = index / side;
                lowX = Math.Min(lowX, cx);
                highX = Math.Max(highX, cx);
                lowZ = Math.Min(lowZ, cz);
                highZ = Math.Max(highZ, cz);
                for (var k = 0; k < 8; k++)
                {
                    var nx = cx + Offsets[k].X;
                    var nz = cz + Offsets[k].Z;
                    if (nx < 0 || nz < 0 || nx >= side || nz >= side) continue;
                    var next = nz * side + nx;
                    if (seen[next] || filled[next] - ground[next] <= 0.02f) continue;
                    seen[next] = true;
                    stack.Push(next);
                }
            }

            if (deepest <= PondDepthMetres || gathered <= LakeCatchmentMetres2 / thirst) continue;

            // <b>And it has to be deep for how broad it is.</b> §147, reported from the chair as water lying
            // where nothing about the ground suggests any: "1.8 ha (684 m across, 12.0 m deep), 0.2 ha (237 m
            // across, 0.9 m deep)". The second of those is not a lake. A depression fills to its outlet, and
            // on a gentle valley floor an outlet a metre above the low point floods a couple of hundred
            // metres of ground a metre deep — an apron with no basin, which then breaks into patches as the
            // renderer's depth fade cuts in and out along its margin. Hence "clearly disjoint".
            //
            // The two tests above ask how deep a body gets and how much drains through it, and neither asks
            // how much ground it covers to be that deep. One in eighty is the shape of a real lake basin at
            // this scale: two hundred metres across wants two and a half metres somewhere in it. The deep
            // basin in that same report passes at twelve metres over six hundred.
            var span = MathF.Max(highX - lowX, highZ - lowZ) * cellMetres;
            if (deepest < span * LakeBasinSteepness) continue;

            foreach (var index in members) standing[index] = true;
        }

        for (var i = 0; i < level.Length; i++)
        {
            var width = authored is null
                ? WidthOf(area[i])
                : MathF.Max(WidthOf(area[i]), authored[i]);
            // A metre and a half of channel stands about 37 cm deep and a seven-metre trunk about 80, which
            // is shallow in absolute terms and exactly right relative to a body: one you wade, one you do not.
            // A metre and a half of channel stands about 37 cm deep and a twenty-six-metre river about a
            // metre and a half — shallow in absolute terms and right relative to a body: one you wade, one you
            // do not. The root is the same reasoning as width's own: a channel is about as deep as it is wide
            // over a broad range, so both come off the same curve rather than off two tables.
            var channel = width > TraceWidthMetres ? 0.30f * MathF.Sqrt(width) : 0f;

            // <b>Two faults reported from the chair, and both are this one line's doing.</b> §145: "streams
            // creep upstairs and vanish into nothing". A channel's level is its bed plus a depth that depends
            // only on how much drains through it, so the surface follows the ground <em>wherever the ground
            // goes</em> — including up a hillside, where thirty centimetres of water reads as a wet ribbon
            // draped over a slope rather than as a stream. And the depth is gated on a hard step at
            // TraceWidthMetres, so a headwater does not thin out, it stops: one cell has a river in it and
            // its neighbour has dry grass.
            //
            // <b>Confinement.</b> Water lies in a sheet only where the ground can hold it. The test is the
            // bed's own gradient: a channel bed is near-level along its length and a hillside is not, and on
            // anything steeper than a gentle valley floor real water is moving too fast to stand — it is
            // whitewater, which is not what a flat translucent sheet depicts either. Tapered rather than cut
            // off, so a stream shallows as it climbs and dries where it steepens.
            var slope = BedGradient(i);
            channel *= 1f - Smooth(ChannelSlopeGentle, ChannelSlopeDry, slope);

            // <b>And a headwater ramp.</b> The same figure the step used, faded over an octave of width
            // rather than switched at it: a stream begins as a trickle nobody can see and grows.
            channel *= Smooth(TraceWidthMetres, TraceWidthMetres * 2.2f, width);
            // The sea last, and as a plain maximum: it needs no catchment and no depression, because it is
            // not filling anything — the land is simply below it.
            var inland = MathF.Max(standing[i] ? filled[i] : ground[i], ground[i] + channel);
            level[i] = ground[i] < sea ? MathF.Max(inland, sea) : inland;
        }

        return level;
    }

    /// <summary>How deep a standing body must get for how broad it is, before it counts as one.</summary>
    /// <remarks>
    /// One in eighty. Deliberately a ratio and not a depth: the existing depth floor is
    /// <see cref="PondDepthMetres"/> and it is right — a pond really is only thirty-five centimetres deep —
    /// and what it cannot express is that thirty-five centimetres over three hundred metres is not a pond,
    /// it is a flooded field. Two quantities, two rules.
    /// </remarks>
    private const float LakeBasinSteepness = 0.0125f;

    /// <summary>Gentlest bed gradient at which a channel starts to thin, and where it is dry.</summary>
    /// <remarks>
    /// Six per cent to twenty-two. The lower figure is about the steepest a valley floor gets while still
    /// carrying a pool-and-riffle stream; the upper is where a watercourse is a cascade. Both are shallower
    /// than the eleven per cent §51 calls buildable, which is the sense check: ground a village would happily
    /// stand on is already too steep to hold standing water.
    /// </remarks>
    private const float ChannelSlopeGentle = 0.06f;

    private const float ChannelSlopeDry = 0.22f;

    /// <summary>The bed's own gradient at one cell, as a rise over run on the unfilled ground.</summary>
    /// <remarks>
    /// Central differences on the raw ground rather than on the filled surface: the fill is level across a
    /// depression by construction, so asking it about gradient would answer zero everywhere it matters least.
    /// </remarks>
    private float BedGradient(int index)
    {
        var x = index % side;
        var z = index / side;
        var left = ground[z * side + Math.Max(0, x - 1)];
        var right = ground[z * side + Math.Min(side - 1, x + 1)];
        var up = ground[Math.Max(0, z - 1) * side + x];
        var down = ground[Math.Min(side - 1, z + 1) * side + x];
        var run = 2f * cellMetres;
        var dx = (right - left) / run;
        var dz = (down - up) / run;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static float Smooth(float edge0, float edge1, float value)
    {
        var t = Math.Clamp((value - edge0) / MathF.Max(0.0001f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Which way the water goes here and how fast, for anything that has to depict it.
    /// </summary>
    /// <remarks>
    /// §145. The receiver field is a D8 flow direction and has been there since the solve; nothing had ever
    /// asked it. Speed comes off the same two quantities a real channel's does — how much is coming through
    /// and how steeply it is falling — normalised so that a valley-floor river is around a third and a
    /// mountain torrent approaches one, because the consumer is a shader and not a hydrologist.
    /// </remarks>
    public (Vector2 Direction, float Speed) FlowAt(Vector2 world)
    {
        // <b>From the gradient of the filled surface, not from the receiver, and the receiver version put a
        // checkerboard on every lake.</b> §147. The receiver field is D8: eight directions, constant inside a
        // cell and jumping at every boundary. Sampled per vertex and interpolated across a water quad, that
        // makes the wave phase — which is a dot product with the flow direction — swing wildly from one quad
        // to the next, and the surface came out as a lattice of bright blobs the size of the mesh's own
        // quads. A discrete field cannot be interpolated and should not have been asked to be.
        //
        // The filled surface's gradient is continuous, points downhill by construction, and is exactly zero
        // across a lake — which is the same reason the fall was measured on it before. One field, sampled the
        // way every other reader here samples: bilinearly.
        var local = world - Origin;
        var step = cellMetres;
        var east = Sample(filled, local + new Vector2(step, 0f));
        var west = Sample(filled, local - new Vector2(step, 0f));
        var south = Sample(filled, local + new Vector2(0f, step));
        var north = Sample(filled, local - new Vector2(0f, step));
        var gradient = new Vector2((east - west) / (2f * step), (south - north) / (2f * step));
        var fall = gradient.Length();
        if (fall < 0.0005f) return (Vector2.Zero, 0f);

        // Downhill is against the gradient. Speed off the two quantities a channel's own comes from: how much
        // is coming through and how steeply it is falling, normalised for a shader rather than a hydrologist.
        var discharge = MathF.Sqrt(MathF.Max(0f, AreaAt(world))) / 600f;
        var speed = Math.Clamp(MathF.Sqrt(fall * 8f) * Math.Clamp(discharge, 0.15f, 1f), 0f, 1f);
        return (-gradient / fall, speed);
    }

    public float WetnessAt(Vector2 world) => Sample(EnsureWetness(), world - Origin);

    private float[] EnsureWetness() => wetness ??= Wetness(ground, area, side, cellMetres);

    private float[] EnsureRanked()
    {
        if (ranked is not null) return ranked;
        ranked = (float[])EnsureWetness().Clone();
        Array.Sort(ranked);
        return ranked;
    }

    /// <summary>
    /// The wetness that this particular landscape's <paramref name="quantile"/> falls at.
    /// </summary>
    /// <remarks>
    /// <b>Thresholds became ranks after five rounds of chasing them, and the chase was the evidence.</b> An
    /// absolute wetness cannot be portable: a bigger canvas genuinely has longer hillslopes and therefore
    /// bigger catchments, so ground that is merely damp at 600 m is a fen by the same number at 1800 m. Every
    /// attempt to fix that by rescaling the index moved one end of the range and broke the other.
    /// <para>
    /// A rank has no units, so it cannot slide. And it is arguably the more meaningful claim anyway — "wetter
    /// than nineteen twentieths of this landscape" is what a fen actually <em>is</em>, whereas a number of
    /// log-square-metres is a proxy for it that happens to work on maps of one size.
    /// </para>
    /// <para>
    /// It also turns the distribution from something that emerges into something that is asked for, which is
    /// what makes it tunable: see the share constants in <see cref="Biomes"/>. The causal story is untouched —
    /// what decides that a place is wet is still how much water arrives and how fast it leaves.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Where this place's wetness falls in the landscape's own distribution: 0 driest, 1 wettest.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="WetnessQuantile"/>, and it exists because a rank is what anything downstream
    /// actually wants. A wetness of 11.4 means nothing on its own; "wetter than four fifths of this landscape"
    /// means something on every map, which is the whole argument §60 made for ranks in the first place.
    /// <para>
    /// A binary search over the sorted table, so it costs a handful of comparisons rather than a pass.
    /// </para>
    /// </remarks>
    public float WetnessRankAt(Vector2 world)
    {
        var sorted = EnsureRanked();
        if (sorted.Length == 0) return 0.5f;
        var value = WetnessAt(world);
        var low = 0;
        var high = sorted.Length - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (sorted[middle] < value) low = middle + 1;
            else high = middle;
        }

        return low / (float)MathF.Max(1, sorted.Length - 1);
    }

    public float WetnessQuantile(float quantile)
    {
        var sorted = EnsureRanked();
        if (sorted.Length == 0) return 0f;
        var index = (int)MathF.Round(Math.Clamp(quantile, 0f, 1f) * (sorted.Length - 1));
        return sorted[index];
    }

    private static float[] Wetness(float[] heights, float[] area, int side, float cellMetres)
    {
        var wetness = new float[heights.Length];
        for (var z = 0; z < side; z++)
        for (var x = 0; x < side; x++)
        {
            var index = z * side + x;
            var steepest = 0f;
            for (var k = 0; k < 8; k++)
            {
                var nx = x + Offsets[k].X;
                var nz = z + Offsets[k].Z;
                if (nx < 0 || nz < 0 || nx >= side || nz >= side) continue;
                var run = Offsets[k].Distance * cellMetres;
                steepest = MathF.Max(steepest, MathF.Abs(heights[nz * side + nx] - heights[index]) / run);
            }

            wetness[index] = MathF.Log(area[index] / MathF.Max(0.02f, steepest));
        }

        return wetness;
    }

    /// <summary>
    /// The bodies of water on this landscape, largest first: how big, how deep, how far across.
    /// </summary>
    /// <remarks>
    /// <b>An instrument, and it exists because "we just never have large water bodies" was said three times
    /// before anything measured whether that was true.</b> Total water area cannot answer it — a hundred
    /// hair-thin channels and one lake come to the same number and are completely different maps. What
    /// distinguishes them is <em>connectedness</em>, so this labels connected components and reports the
    /// biggest, which is the figure the complaint was actually about.
    /// <para>
    /// Span is the diagonal of the body's bounding box rather than its area: a river is long and thin and a
    /// lake is not, so area alone would call a winding channel a large body of water. Area against span is
    /// what tells the two apart.
    /// </para>
    /// </remarks>
    public (float AreaMetres2, float DeepestMetres, float SpanMetres)[] Bodies(int most = 3)
    {
        var levels = EnsureLevel();
        var seen = new bool[levels.Length];
        var found = new List<(float, float, float)>();
        var stack = new Stack<int>();
        var cell = cellMetres * cellMetres;
        for (var start = 0; start < levels.Length; start++)
        {
            if (seen[start] || levels[start] - ground[start] <= 0.05f) continue;
            stack.Push(start);
            seen[start] = true;
            var cells = 0;
            var deepest = 0f;
            var lowX = int.MaxValue;
            var lowZ = int.MaxValue;
            var highX = int.MinValue;
            var highZ = int.MinValue;
            while (stack.Count > 0)
            {
                var index = stack.Pop();
                cells++;
                deepest = MathF.Max(deepest, levels[index] - ground[index]);
                var cx = index % side;
                var cz = index / side;
                lowX = Math.Min(lowX, cx);
                lowZ = Math.Min(lowZ, cz);
                highX = Math.Max(highX, cx);
                highZ = Math.Max(highZ, cz);
                for (var k = 0; k < 8; k++)
                {
                    var nx = cx + Offsets[k].X;
                    var nz = cz + Offsets[k].Z;
                    if (nx < 0 || nz < 0 || nx >= side || nz >= side) continue;
                    var next = nz * side + nx;
                    if (seen[next] || levels[next] - ground[next] <= 0.05f) continue;
                    seen[next] = true;
                    stack.Push(next);
                }
            }

            var wide = (highX - lowX + 1) * cellMetres;
            var tall = (highZ - lowZ + 1) * cellMetres;
            found.Add((cells * cell, deepest, MathF.Sqrt(wide * wide + tall * tall)));
        }

        found.Sort((first, second) => second.Item1.CompareTo(first.Item1));
        return found.Take(most).ToArray();
    }

    /// <summary>
    /// How thick the water gets: the widest circle that fits inside a body of water, in metres.
    /// </summary>
    /// <remarks>
    /// <b>The number the complaint was actually about, and neither area nor span could express it.</b> "We just
    /// never have large water bodies" is a statement about <em>thickness</em> — a river and a lake of the same
    /// area are completely different things, and a lake sitting on a river is connected to it, so a
    /// connected-component measurement calls the pair one body two and a half kilometres across and says
    /// nothing useful. An inscribed diameter separates them at a glance: a twenty-six metre river measures
    /// twenty-six whatever else it is joined to, and a three-hundred metre lake measures three hundred.
    /// <para>
    /// A two-pass chamfer transform, which is the cheap standard way and accurate to a few per cent on the
    /// diagonal — far better than this needs to be to tell a channel from a lake.
    /// </para>
    /// </remarks>
    public (float WidestMetres, Vector2 At) Widest()
    {
        var levels = EnsureLevel();
        var far = side * 2f;
        var distance = new float[levels.Length];
        for (var i = 0; i < levels.Length; i++)
        {
            distance[i] = levels[i] - ground[i] > 0.05f ? far : 0f;
        }

        void Relax(int index, int nx, int nz, float step)
        {
            if (nx < 0 || nz < 0 || nx >= side || nz >= side) return;
            var candidate = distance[nz * side + nx] + step;
            if (candidate < distance[index]) distance[index] = candidate;
        }

        const float diagonal = 1.41421356f;
        for (var z = 0; z < side; z++)
        for (var x = 0; x < side; x++)
        {
            var index = z * side + x;
            if (distance[index] <= 0f) continue;
            Relax(index, x - 1, z, 1f);
            Relax(index, x, z - 1, 1f);
            Relax(index, x - 1, z - 1, diagonal);
            Relax(index, x + 1, z - 1, diagonal);
        }

        for (var z = side - 1; z >= 0; z--)
        for (var x = side - 1; x >= 0; x--)
        {
            var index = z * side + x;
            if (distance[index] <= 0f) continue;
            Relax(index, x + 1, z, 1f);
            Relax(index, x, z + 1, 1f);
            Relax(index, x + 1, z + 1, diagonal);
            Relax(index, x - 1, z + 1, diagonal);
        }

        var best = 0f;
        var at = Vector2.Zero;
        for (var i = 0; i < distance.Length; i++)
        {
            if (distance[i] <= best) continue;
            best = distance[i];
            at = Origin + new Vector2(i % side, i / side) * cellMetres;
        }

        // Twice the radius, and the edge cell is half in — so the diameter is 2r rounded the way a person
        // would measure it across the water rather than across the cells.
        return ((best * 2f - 1f) * cellMetres, at);
    }

    /// <summary>Bilinear read of a lattice field at a world position.</summary>
    public float Sample(float[] field, Vector2 local)
    {
        var fx = Math.Clamp(local.X / cellMetres, 0f, side - 1.001f);
        var fz = Math.Clamp(local.Y / cellMetres, 0f, side - 1.001f);
        var x0 = (int)fx;
        var z0 = (int)fz;
        var tx = fx - x0;
        var tz = fz - z0;
        var a = field[z0 * side + x0];
        var b = field[z0 * side + x0 + 1];
        var c = field[(z0 + 1) * side + x0];
        var d = field[(z0 + 1) * side + x0 + 1];
        return (a + (b - a) * tx) * (1f - tz) + (c + (d - c) * tx) * tz;
    }

    /// <summary>The eight neighbours, with the distance each step covers in cells.</summary>
    private static readonly (int X, int Z, float Distance)[] Offsets =
    {
        (1, 0, 1f), (-1, 0, 1f), (0, 1, 1f), (0, -1, 1f),
        (1, 1, 1.41421356f), (1, -1, 1.41421356f), (-1, 1, 1.41421356f), (-1, -1, 1.41421356f),
    };
}
