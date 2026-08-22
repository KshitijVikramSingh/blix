namespace RTSGame.Simulation.Terrain;

using System.Numerics;

/// <summary>A topological starting condition: what happened here that makes this ground interesting.</summary>
/// <remarks>
/// Not a map. A <em>statement</em>, which the relief then realises — see <see cref="MapLayout"/>.
/// </remarks>
internal enum Archetype
{
    /// <summary>Two ridges with a fertile floor between them.</summary>
    SplitValley,

    /// <summary>A river cutting the ground asymmetrically, with two or three crossings.</summary>
    DiagonalRiver,

    /// <summary>Valuable exposed high ground, with the living done around its feet.</summary>
    CentralHighGround,

    /// <summary>Two fertile pockets, one saddle between them.</summary>
    TwinBasins,

    /// <summary>Rough outer edges and an open contested middle.</summary>
    CornerHighlands,

    /// <summary>One high side and one low side, joined by a few ramps.</summary>
    Escarpment,

    /// <summary>Three approaches converging on one lowland junction.</summary>
    YValley,

    /// <summary>Several short ridges making pockets rather than one clean wall.</summary>
    BrokenRidge,
}

/// <summary>What a separator is made of. The same topology reads completely differently as each of these.</summary>
internal enum SeparatorKind
{
    Ridge,
    River,
    Escarpment,
}

/// <summary>What a way through is made of.</summary>
internal enum ConnectorKind
{
    /// <summary>A low place in a ridge.</summary>
    Saddle,

    /// <summary>A shallow, narrow place in a river.</summary>
    Ford,

    /// <summary>A climbable break in an escarpment.</summary>
    Ramp,
}

/// <summary>One of the map's large geographic statements: a line that divides the ground.</summary>
/// <param name="WidthMetres">
/// For a ridge, how wide it is. For an escarpment, how far its <em>face</em> takes to fall — not how far the
/// shelf extends, which is <paramref name="ReachMetres"/>.
/// </param>
/// <param name="ReachMetres">
/// How far from its path a separator has any effect at all. Zero means the whole map.
/// </param>
/// <remarks>
/// <b>Reach exists because a shelf is areal and a canvas holds several.</b> An escarpment is high everywhere on
/// one side, so it has to be applied over a half-plane rather than a band — and applied over the <em>whole</em>
/// lattice, nine of them on one canvas each lift their own high side by the full amplitude and stack to three
/// times the relief the map asked for. Measured: a pinned Escarpment canvas came out at 99 m on a 30 m
/// amplitude. So a separator authored at frame scale is bounded at frame scale, and one authored for a whole
/// map reaches the whole map.
/// </remarks>
internal readonly record struct Separator(
    SeparatorKind Kind,
    Vector2[] Path,
    float WidthMetres,
    float HeightMetres,
    float ReachMetres = 0f);

/// <summary>Which collection a connector's target lives in.</summary>
/// <remarks>
/// Explicit rather than inferred from <see cref="ConnectorKind"/>, because the inference would nearly work: a
/// saddle is always in a ridge and a ford always in a river, but a <em>ramp</em> is a way up a face and the two
/// things with faces are an escarpment and an upland. "Ramp means upland if the layout has one" is exactly the
/// sort of implicit rule that reads fine and then silently targets the wrong thing.
/// </remarks>
internal enum ConnectorOf
{
    Separator,
    Upland,
}

/// <summary>
/// A way through one particular feature, at a point along it.
/// </summary>
/// <remarks>
/// <b>It knows what it is a way through, and until now it did not.</b> A connector was a position and a radius,
/// and every kind of it did the same thing: suppress relief in a circle. So a saddle authored for a ridge also
/// punched a hole in whatever upland it happened to overlap, a ramp did not reduce any gradient, and a ford did
/// not touch its river — <c>AuthoredWidths</c> painted the channel at full width straight across it. Three
/// names for one operation.
/// <para>
/// Naming the target is what lets the three become three operations. A <b>saddle</b> lowers a ridge's crest. A
/// <b>ramp</b> stretches a face so the same height falls over a longer run, which is what makes it climbable. A
/// <b>ford</b> narrows and shallows a riverbed. Those are not variations on each other.
/// </para>
/// </remarks>
internal readonly record struct Connector(
    ConnectorKind Kind,
    Vector2 At,
    float WidthMetres,
    ConnectorOf Of = ConnectorOf.Separator,
    int Index = 0);

/// <summary>
/// A hollow deep enough to hold water: a lake, authored rather than hoped for.
/// </summary>
/// <remarks>
/// <b>A lake has to be authored, because erosion is a basin-destroying process.</b> §57 recorded that already
/// and drew the wrong conclusion from it — that lakes needed "a reason to exist that erosion cannot take
/// away", and then left them to whatever the frontier happened to enclose. What that produced was ponds: the
/// best of them 0.76 ha and 60 cm deep, which is a puddle at this scale. Meanwhile the layer whose entire job
/// is to state what is interesting about a piece of ground had no way to say "there is a lake here".
/// <para>
/// So a basin is a statement like a ridge is, and it is realised the same way — as a shape cut into the
/// lattice, after erosion rather than before, so that the thing erosion is best at removing is put in once
/// erosion has finished. Depression filling then does the rest: a basin fills to its own lip, which is what
/// gives the lake its level and therefore its shore.
/// </para>
/// <para>
/// <b>And digging alone does not make a lake, which cost a measurement to learn.</b> A bowl cut into a river's
/// course is still a course: its downstream side sits lower than its upstream side, so the water runs through
/// a wider deeper stretch of channel and ponds nowhere. Measured, the thickest water on a canvas with three
/// authored lakes on it was thirty-five metres — the river, and nothing else. A lake needs something to stand
/// behind, so a basin carries the direction its water leaves and a <b>sill</b> goes across it: a bar of ground
/// downstream, high enough to hold the bowl full and low enough to spill over. Physically that is a moraine or
/// a rock bar, which is what most lakes of this size actually sit behind.
/// </para>
/// </remarks>
/// <param name="Downstream">
/// Which way the water leaves, so the sill can be put across it.
/// </param>
/// <summary>
/// The sea, taking one side of the map, with a shoreline that wanders.
/// </summary>
/// <remarks>
/// <b>A datum, which is the thing this terrain has never had.</b> Every water level so far has been relative —
/// a lake fills to its own spill point, a channel stands a little above its own bed — and relative levels
/// cannot answer "how high is this place" in any absolute sense. A sea is a fixed height that everything else
/// is above or below, and a great deal falls out of having one: rivers flow <em>to</em> somewhere instead of
/// off an edge, the low ground has a floor it cannot go under, and "how far above the water is this" becomes a
/// real question with a real answer.
/// <para>
/// <b>It also does the deleted frontier's job properly.</b> §63 removed the mountain rim because it ate 45% of
/// a 600 m map to hide the boundary. A coast hides the same boundary at no cost to gameplay, because water
/// already blocks — there is no need to invent impassable scenery when the map can simply end in the sea.
/// </para>
/// <para>
/// <c>Seaward</c> is the direction the water lies in; <c>InsetMetres</c> is how far the shoreline sits from
/// that edge. The line itself is warped by noise, because a straight coast is the one thing no coast is.
/// </para>
/// </remarks>
internal readonly record struct Coast(Vector2 Seaward, float InsetMetres, float WanderMetres);

/// <summary>
/// High ground with area: a plateau, a massif, a whole side of a map that simply stands higher.
/// </summary>
/// <remarks>
/// <b>The primitive that makes negatives legible.</b> A basin cut into ground that is already the lowest thing
/// on the map is not a basin — it is indistinguishable from the plain around it, which is exactly what
/// happened when TwinBasins got its two basins and still read as "a ridge with low ground either side". A
/// depression is only a depression relative to something, and there was nothing for it to be relative to.
/// <para>
/// <b>Areal, and deliberately not built out of landforms.</b> A row of overlapping mounds is a row of
/// overlapping mounds however carefully it is blended — every member brings its own summit and its own flank
/// and the eye finds the primitive. This is one field: flat across its middle, falling away over its margin,
/// with a noise-warped edge so it is a massif rather than a disc. Erosion then dissects it into spurs and
/// re-entrants, which is what an upland actually looks like and is exactly the job erosion is good at.
/// </para>
/// <para>
/// Cut before erosion, like <see cref="Trough"/> and unlike <see cref="Basin"/> — it drains, so there is
/// nothing for the flow router to fill.
/// </para>
/// </remarks>
internal readonly record struct Upland(Vector2 Centre, float RadiusMetres, float HeightMetres);

/// <summary>
/// A valley floor: low ground with a length and a direction, cut rather than left over.
/// </summary>
/// <remarks>
/// <b>The primitive whose absence made half the archetypes lies.</b> Every statement in this layer was a
/// <em>positive</em> — a ridge, a shelf — so "two ridges with a fertile floor between them" was implemented as
/// two ridges and no floor at all. The valley was merely where no hills had been put, which is not the same
/// shape: a floor between two hills is flat, wide, and lower than the ground beyond them, and the gap between
/// two mounds is none of those.
/// <para>
/// <b>Cut before erosion, unlike <see cref="Basin"/>, and the difference is whether it drains.</b> A trough is
/// an open negative — water runs along it and out — so erosion does not fill it, it deepens it, and cutting it
/// early is what gives erosion something worth finishing. A basin is a closed negative and erosion's first act
/// is to fill it, so that one has to go in afterwards. Open negatives before, closed negatives after.
/// </para>
/// </remarks>
internal readonly record struct Trough(Vector2[] Path, float WidthMetres, float DepthMetres);

internal readonly record struct Basin(
    Vector2 Centre,
    float RadiusMetres,
    float DepthMetres,
    Vector2 Downstream);

/// <summary>
/// The abstract shape of a map, decided before any ground exists.
/// </summary>
/// <remarks>
/// <b>This layer exists because composing landforms and hoping was not working.</b> Six mounds placed from a
/// seed gave a map that was locally plausible everywhere and had no idea in it — reported from the chair as
/// "everything seems nice locally but doesn't make any overall sense/shape". The mistake was one of framing
/// rather than of tuning: at six hundred metres there is not enough canvas for regional geology, so a
/// generator that reasons about watersheds and mountain ranges is answering a question the map is too small
/// to ask.
/// <para>
/// <b>The rule this layer is built to enforce: two large geographic statements and three to five secondary
/// consequences.</b> Beyond that is theme-park geography. So a layout carries one or two
/// <see cref="Separator"/>s and two to four <see cref="Connector"/>s and nothing else — and everything a
/// player will actually notice about the map is a consequence of those, worked out by the causal layers that
/// already exist: drainage decides where the water and the wet ground are, the wetness index decides the
/// country, and the country decides the flora.
/// </para>
/// <para>
/// <b>And the geography is deliberately compressed.</b> A ridge here is 150-250 m long and stands 20 m over
/// its surroundings; a river is 8-15 m wide, comes in one edge, bends twice and leaves. Those are not
/// realistic proportions and they are not meant to be — a real river valley at true scale would eat the whole
/// board. At the scale a person watches a crowd from, a ten-metre rise is already substantial and a
/// forty-metre one is a mountain. Miniaturised landscape logic, which is the same thing that makes the rest
/// of this game read as a diorama.
/// </para>
/// <para>
/// <b>The test of a layout is whether it fits in a sentence.</b> Each one carries the sentence it was built
/// from, and that is not decoration — if the geography cannot be said in a line then there is too much
/// happening on six hundred metres of ground. The generator prints it.
/// </para>
/// </remarks>
internal sealed class MapLayout
{
    private MapLayout(
        Archetype kind,
        string sentence,
        Separator[] separators,
        Connector[] connectors,
        Vector2[] regions,
        Vector2 contested,
        float amplitudeMetres,
        Basin[]? basins = null,
        Trough[]? troughs = null,
        Upland[]? uplands = null,
        Coast? coast = null)
    {
        Coast = coast;
        Basins = basins ?? Array.Empty<Basin>();
        Troughs = troughs ?? Array.Empty<Trough>();
        Uplands = uplands ?? Array.Empty<Upland>();
        Kind = kind;
        Sentence = sentence;
        Separators = separators;
        Connectors = connectors;
        Regions = regions;
        Contested = contested;
        AmplitudeMetres = amplitudeMetres;
    }

    public Archetype Kind { get; }

    /// <summary>The one line this map's geography can be said in.</summary>
    public string Sentence { get; }

    /// <summary>One or two, never more.</summary>
    public Separator[] Separators { get; }

    /// <summary>Two to four ways through.</summary>
    public Connector[] Connectors { get; }

    /// <summary>Where the four sides live. Not settlements — the ground that would suit one.</summary>
    public Vector2[] Regions { get; }

    /// <summary>The one thing worth fighting over, which is not always in the middle.</summary>
    public Vector2 Contested { get; }

    /// <summary>Total relief this statement wants, in metres.</summary>
    public float AmplitudeMetres { get; }

    /// <summary>Standing water this layout asks for. Usually none, sometimes one, rarely two.</summary>
    public Basin[] Basins { get; }

    /// <summary>Valley floors this layout asks for, as low ground rather than as absent high ground.</summary>
    public Trough[] Troughs { get; }

    /// <summary>Areal high ground, which is what gives the negatives something to be lower than.</summary>
    public Upland[] Uplands { get; }

    /// <summary>The sea, if this map has one. The only absolute height on the map.</summary>
    public Coast? Coast { get; }

    /// <summary>Every archetype there is, for a lab that cycles them.</summary>
    public static Archetype[] All { get; } = Enum.GetValues<Archetype>();

    /// <summary>
    /// Builds a layout, then distorts it so the same archetype is a different map every seed.
    /// </summary>
    /// <remarks>
    /// The archetypes are written in a canonical orientation on a unit square and then rotated, mirrored and
    /// jittered. Which is the point of having archetypes at all: the strategic grammar stays legible — this
    /// map is "two basins and a saddle" — while the ground it lands on is never the same twice. Authoring
    /// eight maps would have given eight maps; authoring eight <em>relationships</em> and distorting them
    /// gives as many as anybody wants.
    /// </remarks>
    public static MapLayout For(
        Archetype kind,
        float frameMetres,
        uint seed,
        float amplitudeMetres,
        Vector2 centre = default)
    {
        var random = new Roll(seed == 0 ? 0x9E3779B9u : seed);
        // A whole turn, so an archetype has no favoured axis and a "diagonal" river is diagonal in the
        // sense that matters rather than in the sense of pointing at a particular corner.
        var turn = random.Next() * MathF.Tau;
        var mirror = random.Next() < 0.5f ? -1f : 1f;
        var cos = MathF.Cos(turn);
        var sin = MathF.Sin(turn);

        // <b>Scaled by the frame, never by the canvas.</b> Scaling a layout to the extent meant an 1800 m
        // canvas got one ridge system a mile and a half long instead of nine at the size a ridge should be, so
        // no 600 m window in it contained anything. The compressed sizes this layer is built on — a ridge of
        // 150-250 m, a valley of 80, a hill of 30-60 — are absolute.
        //
        // <b>Inscribed, but far less than it was, because the reason for inscribing hard has gone.</b> Seven
        // tenths was chosen in §63 to stop rotation pushing part of an archetype off the map, because
        // <see cref="ReliefPlan.Place"/> silently drops landforms that do not fit — "rotate the archetype" had
        // meant "randomly amputate the archetype".
        //
        // Nothing rejects features any more. §64 made ridges heightfields, and <c>Crest</c>, <c>Raise</c> and
        // <c>Excavate</c> all clamp to the lattice and draw whatever is in range — so a feature that runs off
        // the edge is <em>clipped</em>, which is the fragment property and the same thing <see cref="Through"/>
        // does to rivers on purpose.
        //
        // What the old value cost is the whole reason to revisit it. Features lived within seven tenths of the
        // half-width, so the outer ring was three tenths of the <em>radius</em> and therefore
        // <b>fifty-one per cent of the area</b> — empty on every map, by construction. Reported from the chair
        // as half the map being interesting and the other half plain. At 0.88 the same ring is 23%.
        const float inscribe = 0.88f;

        // <b>Widths scale with the frame, and they did not.</b> Paths are authored in canonical coordinates and
        // scaled by the frame; widths were authored in metres and were not — so a secondary statement at half
        // frame kept hundred-metre ridges while its paths shrank to eighty-eight metres, making every ridge
        // wider than it was long. The end-taper then flattened what was left, and a second statement that was
        // meant to hold its own arrived in the bottom two height bands. A width is a proportion of a statement,
        // not a constant of the world.
        var scale = frameMetres / FrameMetres;

        Vector2 Place(float x, float z)
        {
            // <b>A coherent warp, and it replaces per-call jitter that was breaking incidence.</b> The jitter
            // drew fresh randomness on every call, so two identical canonical coordinates — the river's bend
            // and the ford that is meant to sit on it — became two different points up to fifteen metres apart
            // on each axis. That makes incidence <em>probabilistic</em>, which is fatal for a topology system:
            // a ford not on its river is not a ford, and a saddle not in its ridge is a hole in a field.
            //
            // A function of position instead. Same input point, same output point; nearby points, similar
            // displacement. Which also reads far more geographical than independently perturbed control
            // points, because real landforms bend together rather than each wandering off on its own.
            var jx = x * mirror;
            var rx = jx * cos - z * sin;
            var rz = jx * sin + z * cos;
            var at = centre + new Vector2(rx, rz) * (frameMetres * inscribe);
            // A wavelength of about a third of the frame, so the warp bends a whole statement rather than
            // wobbling its parts; a twelfth of the frame of swing, which is enough to take the straightness
            // out of an authored line and not enough to move a feature somewhere else.
            var wave = 3.1f / MathF.Max(1f, frameMetres);
            var warp = new Vector2(
                LatticeNoise.Value(at * wave) - 0.5f,
                LatticeNoise.Value(at * wave + new Vector2(53.7f, 21.3f)) - 0.5f);
            return at + warp * (frameMetres * 0.085f);
        }

        // Heights are stated as fractions of the map's amplitude so an archetype is legible at any relief.
        var high = amplitudeMetres;
        var ridge = amplitudeMetres * 0.78f;

        return kind switch
        {
            Archetype.SplitValley => new MapLayout(
                kind,
                "two ridges with a fertile floor between them",
                new[]
                {
                    // <b>The trunk first, because the first river inherits the catchment.</b> A valley drains,
                    // and its river is the reason the floor is a floor — running it down the trough is what
                    // makes the two features one statement instead of two that happen to overlap.
                    new Separator(SeparatorKind.River, new[] { Place(-0.52f, -0.01f), Place(-0.10f, 0.04f), Place(0.20f, -0.02f), Place(0.52f, 0.02f) }, 13f * scale, 0f),
                    new Separator(SeparatorKind.Ridge, new[] { Place(-0.42f, -0.26f), Place(0.10f, -0.30f), Place(0.44f, -0.22f) }, 90f * scale, ridge),
                    new Separator(SeparatorKind.Ridge, new[] { Place(-0.44f, 0.24f), Place(-0.02f, 0.31f), Place(0.42f, 0.26f) }, 90f * scale, ridge * 0.88f),
                },
                new[]
                {
                    // Indices name the separator each way-through belongs to: 0 is the river, 1 and 2 the two
                    // ridges. A saddle in the wrong ridge is a hole in a hillside.
                    new Connector(ConnectorKind.Saddle, Place(-0.16f, -0.28f), 70f * scale, Index: 1),
                    new Connector(ConnectorKind.Saddle, Place(0.26f, 0.28f), 70f * scale, Index: 2),
                    new Connector(ConnectorKind.Ford, Place(0.06f, 0.02f), 30f * scale, Index: 0),
                },
                new[] { Place(-0.34f, 0f), Place(0.34f, 0f), Place(0f, -0.42f), Place(0f, 0.42f) },
                Place(0f, 0.02f),
                amplitudeMetres,
                // The floor. Wide, shallow and running the length of the map between the two ridges, which is
                // what makes this a valley rather than a gap.
                troughs: new[]
                {
                    new Trough(
                        new[] { Place(-0.48f, 0.00f), Place(-0.06f, 0.03f), Place(0.46f, -0.01f) },
                        frameMetres * 0.20f,
                        amplitudeMetres * 0.34f),
                }),

            Archetype.DiagonalRiver => new MapLayout(
                kind,
                "settlements facing each other across a river valley",
                new[]
                {
                    // <b>Twenty-six metres, and it was the narrowest of the eight at twelve.</b> The archetype
                    // named for its river had less river than the ones where a river is a side effect, which is
                    // backwards — and it is half of why every map read as "a river through it": the widths said
                    // every river was equally important, so every river carved an equally dominant valley.
                    new Separator(SeparatorKind.River, new[] { Place(-0.50f, -0.34f), Place(-0.14f, -0.06f), Place(0.12f, 0.14f), Place(0.50f, 0.36f) }, 26f * scale, 0f),
                    new Separator(SeparatorKind.Ridge, new[] { Place(-0.30f, -0.44f), Place(0.22f, -0.40f) }, 80f * scale, ridge * 0.8f),
                },
                new[]
                {
                    new Connector(ConnectorKind.Ford, Place(-0.14f, -0.06f), 26f * scale, Index: 0),
                    new Connector(ConnectorKind.Ford, Place(0.30f, 0.24f), 22f * scale, Index: 0),
                },
                new[] { Place(-0.36f, 0.16f), Place(0.34f, -0.18f), Place(-0.10f, 0.42f), Place(0.14f, -0.42f) },
                Place(0.02f, 0.06f),
                amplitudeMetres),

            Archetype.CentralHighGround => new MapLayout(
                kind,
                "four sides around one defensible hill",
                // No ridge — a defensible height is <em>areal</em>, and three overlapping mounds are three
                // hills that happen to touch. But a river, skirting it: high ground sheds water, so a stream
                // round its foot is the consequence of the statement rather than an addition to it.
                new[]
                {
                    new Separator(SeparatorKind.River, new[] { Place(-0.52f, 0.30f), Place(-0.16f, 0.26f), Place(0.18f, 0.34f), Place(0.52f, 0.26f) }, 10f * scale, 0f),
                },
                new[]
                {
                    // Up the plateau, so these belong to the upland and not to any separator. This is the case
                    // the ConnectorOf discriminator exists for.
                    new Connector(ConnectorKind.Ramp, Place(-0.12f, 0.14f), 80f * scale, ConnectorOf.Upland, 0),
                    new Connector(ConnectorKind.Ramp, Place(0.20f, -0.10f), 70f * scale, ConnectorOf.Upland, 0),
                },
                new[] { Place(-0.40f, -0.36f), Place(0.40f, -0.36f), Place(-0.40f, 0.36f), Place(0.40f, 0.36f) },
                Place(0f, 0f),
                amplitudeMetres,
                uplands: new[] { new Upland(Place(0f, 0f), frameMetres * 0.23f, high) }),

            Archetype.TwinBasins => new MapLayout(
                kind,
                "two fertile basins joined by one saddle",
                new[]
                {
                    new Separator(SeparatorKind.River, new[] { Place(-0.52f, 0.16f), Place(-0.28f, 0.06f), Place(0.06f, 0.14f), Place(0.34f, 0.10f), Place(0.52f, 0.20f) }, 11f * scale, 0f),
                    new Separator(SeparatorKind.Ridge, new[] { Place(0.02f, -0.50f), Place(-0.06f, -0.10f), Place(0.04f, 0.24f), Place(-0.02f, 0.50f) }, 110f * scale, high),
                },
                new[]
                {
                    new Connector(ConnectorKind.Saddle, Place(-0.04f, 0.04f), 64f * scale, Index: 1),
                },
                new[] { Place(-0.32f, -0.28f), Place(-0.30f, 0.30f), Place(0.32f, -0.26f), Place(0.30f, 0.30f) },
                Place(-0.04f, 0.04f),
                amplitudeMetres,
                // <b>Two basins, because it is called TwinBasins and had none.</b> Cut either side of the
                // dividing ridge, which is the whole statement: two pockets of good ground that cannot see each
                // other, joined by one saddle.
                basins: new[]
                {
                    new Basin(Place(-0.30f, -0.02f), frameMetres * 0.19f, amplitudeMetres * 0.62f, Vector2.UnitX),
                    new Basin(Place(0.30f, 0.04f), frameMetres * 0.18f, amplitudeMetres * 0.56f, -Vector2.UnitX),
                },
                // The country the basins are sunk into. Without it they are two dips in a plain and the
                // archetype reads as its dividing ridge and nothing else.
                uplands: new[] { new Upland(Place(0f, 0f), frameMetres * 0.46f, amplitudeMetres * 0.66f) }),

            Archetype.CornerHighlands => new MapLayout(
                kind,
                "rough highland corners around an open middle",
                new[]
                {
                    new Separator(SeparatorKind.River, new[] { Place(-0.52f, 0.34f), Place(-0.18f, 0.08f), Place(0.16f, -0.10f), Place(0.52f, -0.34f) }, 12f * scale, 0f),
                    new Separator(SeparatorKind.Ridge, new[] { Place(-0.50f, -0.22f), Place(-0.34f, -0.36f), Place(-0.20f, -0.50f) }, 100f * scale, ridge),
                    new Separator(SeparatorKind.Ridge, new[] { Place(0.50f, 0.22f), Place(0.34f, 0.36f), Place(0.20f, 0.50f) }, 100f * scale, ridge * 0.9f),
                },
                new[]
                {
                    new Connector(ConnectorKind.Saddle, Place(-0.34f, -0.34f), 60f * scale, Index: 1),
                    new Connector(ConnectorKind.Saddle, Place(0.34f, 0.34f), 60f * scale, Index: 2),
                },
                new[] { Place(-0.34f, -0.30f), Place(0.34f, 0.30f), Place(0.36f, -0.30f), Place(-0.36f, 0.30f) },
                Place(0f, 0f),
                amplitudeMetres),

            Archetype.Escarpment => new MapLayout(
                kind,
                "a high shelf above a low plain, with a few ways up",
                new[]
                {
                    // Along the foot, one face-width below it. Water collects where a slope meets a plain, so
                    // this is the consequence of the shelf and not a second statement.
                    new Separator(SeparatorKind.River, new[] { Place(-0.52f, 0.06f), Place(-0.10f, 0.14f), Place(0.24f, 0.02f), Place(0.52f, 0.10f) }, 14f * scale, 0f),
                    new Separator(SeparatorKind.Escarpment, new[] { Place(-0.50f, -0.10f), Place(-0.10f, -0.02f), Place(0.24f, -0.14f), Place(0.50f, -0.06f) }, 70f * scale, high, frameMetres * 0.52f),
                },
                new[]
                {
                    // Up the face, so they belong to the escarpment: separator 1, behind the river at 0.
                    new Connector(ConnectorKind.Ramp, Place(-0.28f, -0.08f), 66f * scale, Index: 1),
                    new Connector(ConnectorKind.Ramp, Place(0.16f, -0.10f), 58f * scale, Index: 1),
                    new Connector(ConnectorKind.Ramp, Place(0.44f, -0.08f), 46f * scale, Index: 1),
                },
                new[] { Place(-0.32f, -0.34f), Place(0.30f, -0.36f), Place(-0.30f, 0.32f), Place(0.32f, 0.30f) },
                Place(-0.06f, -0.24f),
                amplitudeMetres),

            Archetype.YValley => new MapLayout(
                kind,
                "three valleys meeting at one lowland junction",
                // <b>No enclosing ridges — the Y is cut, and the interfluves are what the cutting leaves.</b>
                // But the water is the Y: a stem with two tributaries down the arms, on the same paths as the
                // troughs. Which is the archetype's whole point stated twice in agreement, once as shape and
                // once as drainage, rather than a valley system with a river somewhere else in it.
                new[]
                {
                    new Separator(SeparatorKind.River, new[] { Place(0f, 0.04f), Place(0.02f, 0.28f), Place(-0.02f, 0.52f) }, 15f * scale, 0f),
                    new Separator(SeparatorKind.River, new[] { Place(-0.40f, -0.48f), Place(-0.12f, -0.12f), Place(0f, 0.04f) }, 12f * scale, 0f),
                    new Separator(SeparatorKind.River, new[] { Place(0.42f, -0.46f), Place(0.14f, -0.10f), Place(0f, 0.04f) }, 12f * scale, 0f),
                },
                new[]
                {
                    // <b>Fords, not saddles.</b> These were authored as saddles in the enclosing ridges, and
                    // §64 deleted those ridges when the Y became cut valleys — leaving two connectors pointing
                    // at features that no longer existed. A crossing on each arm is what this archetype's ways
                    // through actually are: separators 1 and 2 are the two arms.
                    new Connector(ConnectorKind.Ford, Place(-0.18f, -0.22f), 40f * scale, Index: 1),
                    new Connector(ConnectorKind.Ford, Place(0.20f, -0.20f), 40f * scale, Index: 2),
                },
                new[] { Place(-0.34f, -0.34f), Place(0.34f, -0.34f), Place(0f, 0.36f), Place(-0.02f, 0.10f) },
                Place(0f, -0.02f),
                amplitudeMetres,
                // <b>The three floors, and the interfluves are whatever is left.</b> Generating the enclosing
                // ridges and hoping a Y appeared between them is what made this archetype unrecognisable —
                // cutting the Y itself makes the high ground fall out of it for free, which is how a valley
                // system actually forms.
                troughs: new[]
                {
                    new Trough(
                        new[] { Place(-0.40f, -0.46f), Place(-0.12f, -0.12f), Place(0f, 0.04f) },
                        frameMetres * 0.11f,
                        amplitudeMetres * 0.78f),
                    new Trough(
                        new[] { Place(0.42f, -0.44f), Place(0.14f, -0.10f), Place(0f, 0.04f) },
                        frameMetres * 0.11f,
                        amplitudeMetres * 0.78f),
                    new Trough(
                        new[] { Place(0f, 0.04f), Place(0.02f, 0.28f), Place(-0.02f, 0.50f) },
                        frameMetres * 0.15f,
                        amplitudeMetres * 0.88f),
                },
                uplands: new[] { new Upland(Place(0f, -0.06f), frameMetres * 0.52f, amplitudeMetres * 0.92f) }),

            _ => new MapLayout(
                Archetype.BrokenRidge,
                "an open plain fractured by wooded ridges",
                new[]
                {
                    new Separator(SeparatorKind.River, new[] { Place(-0.52f, 0.22f), Place(-0.20f, 0.06f), Place(0.02f, -0.10f), Place(0.28f, -0.06f), Place(0.52f, -0.24f) }, 11f * scale, 0f),
                    new Separator(SeparatorKind.Ridge, new[] { Place(-0.44f, -0.18f), Place(-0.20f, -0.26f) }, 70f * scale, ridge),
                    new Separator(SeparatorKind.Ridge, new[] { Place(0.06f, 0.08f), Place(0.34f, 0.22f) }, 70f * scale, ridge * 0.86f),
                },
                new[]
                {
                    new Connector(ConnectorKind.Saddle, Place(-0.06f, -0.22f), 90f * scale, Index: 1),
                    new Connector(ConnectorKind.Saddle, Place(-0.10f, 0.14f), 90f * scale, Index: 2),
                },
                new[] { Place(-0.36f, 0.28f), Place(0.34f, -0.30f), Place(-0.34f, -0.36f), Place(0.36f, 0.32f) },
                Place(-0.02f, -0.06f),
                amplitudeMetres),
        };
    }

    /// <summary>
    /// The size of map an archetype is authored for. Everything in a layout is a fraction of this.
    /// </summary>
    /// <remarks>
    /// Six hundred metres, because that is the map a player is given, and the archetypes are statements about
    /// <em>a map</em> rather than about a region. A canvas larger than this is a place to find maps in — see
    /// <see cref="Field"/>.
    /// </remarks>
    public const float FrameMetres = 600f;

    /// <summary>
    /// Two archetypes on one map: a primary statement and a secondary one in the ground it leaves open.
    /// </summary>
    /// <remarks>
    /// <b>One archetype is a thin map, and the score had been saying so.</b> The rule this layer is built on
    /// asks for <em>two</em> large geographic statements and three to five consequences; an archetype
    /// contributes about one. Measured, SplitValley scored 0.95 with three statements in frame and
    /// CentralHighGround scored 0.76 with one — the statement term peaks at two, and a lone plateau on a plain
    /// never reaches it however good the plateau is.
    /// <para>
    /// So neither one archetype nor nine. Nine was the canvas, which was illegible at map scale and is gone;
    /// one is legible and thin. Two is the rule, and it is also what makes a 600 m map worth exploring — a
    /// plateau <em>and</em> a broken ridge line gives four regions that differ from each other, which is the
    /// thing that turns terrain into a set of relationships rather than a backdrop.
    /// </para>
    /// <para>
    /// <b>The secondary goes where the primary is open, and that is what <see cref="Regions"/> is for.</b> It
    /// has been carried and unread since it was added. A primary's regions are, by construction, the ground it
    /// does not itself occupy — so centring the second statement on one of them is both the cheapest placement
    /// rule and the right one: two statements competing for the same ground is mush, and the primary already
    /// knows where its own spare ground is.
    /// </para>
    /// <para>
    /// Half scale and seven tenths the relief, so the map has a <em>主</em> statement rather than two arguing.
    /// And the secondary's river is retargeted onto the primary's — a map with two unconnected watercourses on
    /// it is the "meets nothing" complaint again, one composition layer up.
    /// </para>
    /// </remarks>
    public static MapLayout Composed(
        Archetype primary,
        float frameMetres,
        uint seed,
        float amplitudeMetres,
        Archetype? secondaryChoice = null)
    {
        // <b>Salted with the primary, or every map picks the same partner.</b> Seeded from the seed alone, the
        // first draw is the same draw whatever the primary is — so a sweep across all eight archetypes at one
        // seed paired seven of them with the same secondary. Which is correct determinism answering a question
        // nobody asked: the pairing should be a property of <em>this combination</em>, not of the seed.
        var random = new Roll(seed * 2246822519u + 1013u + (uint)primary * 40503u);
        var main = For(primary, frameMetres, seed, amplitudeMetres);

        // A different archetype, chosen from the seed unless asked for. Never the same one twice: two
        // plateaux is one plateau, and two split valleys is a rougher split valley.
        var secondary = secondaryChoice ?? All[(int)(random.Next() * All.Length) % All.Length];
        if (secondary == primary) secondary = All[((int)primary + 1 + (int)(random.Next() * 3f)) % All.Length];

        // <b>The emptiest region, measured against what the primary actually put down.</b> "Furthest from the
        // middle" was a proxy for empty and a poor one: an archetype's regions are all roughly the same distance
        // out, so it picked by index in practice, and it had no idea whether the corner it chose already had a
        // ridge running through it. Which is how a secondary ended up beside the primary while two quarters of
        // the map stayed bare.
        //
        // Distance to the nearest thing the primary built — any point on any of its paths, any upland, any basin
        // — is the measure that means what the proxy was reaching for. Ties by index, like everything else that
        // decides ground.
        var where = Vector2.Zero;
        var emptiest = -1f;
        foreach (var region in main.Regions)
        {
            var nearest = float.MaxValue;
            foreach (var separator in main.Separators)
            {
                foreach (var point in separator.Path)
                {
                    nearest = MathF.Min(nearest, Vector2.Distance(region, point));
                }
            }

            foreach (var trough in main.Troughs)
            {
                foreach (var point in trough.Path)
                {
                    nearest = MathF.Min(nearest, Vector2.Distance(region, point));
                }
            }

            foreach (var upland in main.Uplands)
            {
                // To the shoulder rather than to the centre: a plateau's middle can be far away while its edge
                // is underfoot, and it is the edge that makes ground busy.
                nearest = MathF.Min(
                    nearest,
                    MathF.Abs(Vector2.Distance(region, upland.Centre) - upland.RadiusMetres));
            }

            foreach (var basin in main.Basins)
            {
                nearest = MathF.Min(
                    nearest,
                    MathF.Abs(Vector2.Distance(region, basin.Centre) - basin.RadiusMetres));
            }

            if (nearest <= emptiest) continue;
            emptiest = nearest;
            where = region;
        }

        // <b>Pulled in so the secondary's own frame fits on the map.</b> A primary's regions sit near its
        // corners by design — they are the ground it leaves open — and a half-scale frame centred on one of
        // them reaches a hundred and thirty metres further out again, which on a 600 m map is off the edge.
        // Measured before this clamp: CentralHighGround's secondary was centred at 226 m radius with a reach of
        // 130, so a third of the second statement was outside the world and what remained showed up in the
        // bottom three height bands. The whole point of a second statement is that it is a statement.
        var aside = frameMetres * 0.50f;
        var worst = aside * 0.70f * 0.62f;
        var room = MathF.Max(0f, frameMetres * 0.5f - worst);
        if (where.Length() > room && where.LengthSquared() > 1e-4f)
        {
            where = Vector2.Normalize(where) * room;
        }

        // <b>Rule two: the secondary may not dam the primary's drainage.</b> Placed by "furthest region" alone,
        // a secondary's ridges landed across the primary's valley — and measured, that flooded it: a
        // SplitValley whose whole statement is a fertile floor came out with sixteen per cent of the map under
        // water and a lake a hundred and thirty metres wide sitting in the farmland. Priority-Flood was doing
        // exactly its job; the composition had built a dam and not noticed.
        //
        // Pushed clear along the river's own perpendicular, which keeps it in open ground rather than shoving it
        // toward the middle where the primary already is. A statement that has to be moved to avoid a river is
        // still in the quarter it was assigned; it is just no longer standing in the water's way.
        if (main.River() is { Path.Length: > 1 } course)
        {
            var keepOut = worst * 0.85f;
            var (away, foot) = ToCourse(where, course.Path);
            if (away < keepOut)
            {
                var push = where - foot;
                if (push.LengthSquared() < 1e-4f)
                {
                    var run = Vector2.Normalize(course.Path[^1] - course.Path[0]);
                    push = new Vector2(-run.Y, run.X);
                }

                where = foot + Vector2.Normalize(push) * keepOut;
                if (where.Length() > room && where.LengthSquared() > 1e-4f)
                {
                    where = Vector2.Normalize(where) * room;
                }
            }
        }

        var second = For(
            secondary,
            aside,
            seed * 2654435761u + 7919u,
            amplitudeMetres * 0.78f,
            where);

        // Rule three: whatever is meant to cross the map, crosses it. See Through.
        var half = frameMetres * 0.5f;
        var separators = new List<Separator>();
        foreach (var separator in main.Separators)
        {
            separators.Add(
                separator.Kind == SeparatorKind.River
                    ? separator with { Path = Through(separator.Path, half) }
                    : separator);
        }
        // The primary's trunk keeps the inherited catchment, so it has to stay first. Everything the secondary
        // brings arrives behind it, which makes its river a tributary of the map's river rather than a rival.
        foreach (var separator in second.Separators)
        {
            separators.Add(
                separator.Kind == SeparatorKind.River
                    ? separator with { Path = JoinedTo(separator.Path, main) }
                    : separator);
        }

        // <b>Rule one: if there is a sea, it lies where the river was already going.</b> A coast placed
        // independently of the drainage gives a river that runs off the wrong edge while the sea sits behind it
        // — which is the "flows into nothing" complaint with an extra feature on top. Taking the direction from
        // the trunk's own course means the water reaches the water without anything being routed to make it.
        //
        // Not every map. A coast is a strong statement and a map that is all coast is one map; about two in
        // five is enough that the sea is a thing this generator does and not a thing it always does.
        Coast? coast = null;
        if (random.Next() < 0.42f)
        {
            var trunk = main.River();
            var seaward = trunk is { Path.Length: > 1 }
                ? Vector2.Normalize(trunk.Value.Path[^1] - trunk.Value.Path[0])
                : new Vector2(MathF.Cos(random.Next() * MathF.Tau), MathF.Sin(random.Next() * MathF.Tau));
            coast = new Coast(seaward, frameMetres * (0.17f + random.Next() * 0.10f), frameMetres * 0.10f);
        }

        var sentence = coast is null
            ? $"{main.Sentence}, and {second.Sentence} off to one side"
            : $"{main.Sentence}, and {second.Sentence} off to one side, with the sea along one edge";
        return new MapLayout(
            primary,
            sentence,
            separators.ToArray(),
            // <b>The secondary's indices are rebased, because merging shifts what they point at.</b> A
            // secondary authored with "saddle in separator 1" means its own separator 1 — and after
            // concatenation that slot holds one of the primary's. Left unshifted, every composed map would cut
            // the second statement's passes through the first statement's ridges, which is a bug that would
            // have looked like bad authoring for a long time.
            main.Connectors
                .Concat(second.Connectors.Select(connector => connector with
                {
                    Index = connector.Index + (connector.Of == ConnectorOf.Upland
                        ? main.Uplands.Length
                        : main.Separators.Length),
                }))
                .ToArray(),
            main.Regions.Concat(second.Regions).ToArray(),
            main.Contested,
            amplitudeMetres,
            main.Basins.Concat(second.Basins).ToArray(),
            main.Troughs
                .Select(trough => trough with { Path = Through(trough.Path, half) })
                .Concat(second.Troughs)
                .ToArray(),
            main.Uplands.Concat(second.Uplands).ToArray(),
            coast);
    }

    /// <summary>
    /// Pushes a linear feature's two ends out past the map, so it goes somewhere.
    /// </summary>
    /// <remarks>
    /// <b>The inscribe factor was fixing one bug and causing another.</b> §63 pulled every canonical coordinate
    /// in to 0.70 so that rotating an archetype could not push part of it off the map, because
    /// <c>ReliefPlan.Place</c> silently drops what does not fit. That is right for a hill and wrong for
    /// anything that is meant to <em>leave</em>: a river authored to ±0.52 came out ending at ±218 m on a map
    /// whose half-width is 300, and a valley floor at ±0.48 stopped 98 m short of the edge.
    /// <para>
    /// A trough that does not reach the edge is a <b>closed basin</b>. Measured, that is what flooded
    /// SplitValley: sixteen per cent of the map under water and a hundred-and-thirty-metre lake sitting in the
    /// fertile floor, because the valley had nowhere to drain to. It is also, in hindsight, the whole of "the
    /// river meets nothing and flows into nothing" — it was ending in a field.
    /// </para>
    /// <para>
    /// So positives stay inscribed and linear features get extended along their own terminal directions until
    /// they are clear of the boundary. Which is the distinction that was missing: an inscribed hill is contained
    /// on purpose, and an inscribed river is a river that stops.
    /// </para>
    /// </remarks>
    private static Vector2[] Through(Vector2[] path, float halfExtent)
    {
        if (path.Length < 2) return path;
        var reach = halfExtent * 1.12f;
        var extended = (Vector2[])path.Clone();

        Vector2 Push(Vector2 from, Vector2 toward)
        {
            var run = toward - from;
            if (run.LengthSquared() < 1e-4f) return toward;
            var step = Vector2.Normalize(run);
            // Far enough that the feature is unambiguously off the map, and along its own heading so the last
            // bend is not straightened out to get there.
            var out_ = toward;
            var guard = 0;
            while (MathF.Max(MathF.Abs(out_.X), MathF.Abs(out_.Y)) < reach && guard++ < 64)
            {
                out_ += step * (halfExtent * 0.08f);
            }

            return out_;
        }

        extended[0] = Push(path[1], path[0]);
        extended[^1] = Push(path[^2], path[^1]);
        return extended;
    }

    /// <summary>Distance from a point to a course, and the nearest point on it.</summary>
    private static (float Away, Vector2 Foot) ToCourse(Vector2 at, Vector2[] path)
    {
        var best = float.MaxValue;
        var foot = at;
        for (var i = 1; i < path.Length; i++)
        {
            var from = path[i - 1];
            var span = path[i] - from;
            var lengthSquared = span.LengthSquared();
            if (lengthSquared <= 1e-4f) continue;
            var t = Math.Clamp(Vector2.Dot(at - from, span) / lengthSquared, 0f, 1f);
            var nearest = from + span * t;
            var away = Vector2.Distance(at, nearest);
            if (away >= best) continue;
            best = away;
            foot = nearest;
        }

        return (best, foot);
    }

    /// <summary>
    /// Bends a secondary river's mouth onto the primary's nearest watercourse.
    /// </summary>
    /// <remarks>
    /// A confluence, for the reason the canvas already taught: two watercourses that never meet read as two
    /// unrelated lines rather than one drainage. Only the mouth moves — the head stays where the archetype put
    /// it, so the secondary statement is not dragged out of its own region to reach the water.
    /// </remarks>
    private static Vector2[] JoinedTo(Vector2[] path, MapLayout primary)
    {
        Vector2? mouth = null;
        var best = float.MaxValue;
        foreach (var river in primary.Rivers())
        {
            for (var i = 1; i < river.Path.Length; i++)
            {
                var from = river.Path[i - 1];
                var span = river.Path[i] - from;
                var lengthSquared = span.LengthSquared();
                if (lengthSquared <= 1e-4f) continue;
                var t = Math.Clamp(Vector2.Dot(path[^1] - from, span) / lengthSquared, 0f, 1f);
                var nearest = from + span * t;
                var away = Vector2.DistanceSquared(path[^1], nearest);
                if (away >= best) continue;
                best = away;
                mouth = nearest;
            }
        }

        if (mouth is not { } join) return path;
        var joined = (Vector2[])path.Clone();
        joined[^1] = join;
        // The point before it eased toward the join too, or the last segment turns a corner the water would
        // not take.
        if (joined.Length >= 3) joined[^2] = Vector2.Lerp(joined[^2], join, 0.35f);
        return joined;
    }

    /// <summary>
    /// Fills a canvas with statements at their own size, so every window in it has something in it.
    /// </summary>
    /// <remarks>
    /// <b>One canvas, many statements, and one river through the lot.</b> The instances are placed on a
    /// jittered grid at frame scale, each rotated and mirrored independently, and each drawn from the
    /// archetype family unless one was asked for — so a single roll of an 1800 m canvas gives nine
    /// neighbourhoods of different character, and the windows over them are genuinely different maps rather
    /// than nine views of one.
    /// <para>
    /// The river is the exception and spans the whole canvas, because a river is the one feature that really
    /// is regional: it is what makes the canvas one place instead of a patchwork, and it is what gives a
    /// window its off-window context. Instance rivers become tributaries — they are carved but given no
    /// inherited catchment, so they carry only what the ground above them sheds, which is what a tributary is.
    /// </para>
    /// <para>
    /// The seams that tiling would produce are handled by not tiling: the grid is jittered by a third of a
    /// cell, the instances overlap, and every downstream layer composes rather than partitions — ridges
    /// combine through the soft maximum, and drainage and the classifier only ever read the finished surface.
    /// </para>
    /// </remarks>
    public static MapLayout Field(
        float extentMetres,
        uint seed,
        float amplitudeMetres,
        Archetype? only = null)
    {
        var random = new Roll(seed == 0 ? 0x2545F491u : seed * 2246822519u + 7u);
        var across = Math.Max(1, (int)MathF.Round(extentMetres / FrameMetres));
        var step = extentMetres / across;
        var separators = new List<Separator>();
        var connectors = new List<Connector>();
        var regions = new List<Vector2>();
        var kinds = new List<Archetype>();
        var troughs = new List<Trough>();
        var uplands = new List<Upland>();

        // <b>The trunk meanders, and the first version did not.</b> Five points with one sine bend across the
        // whole canvas is a diagonal with a bow in it — reported exactly as "straight lines running through the
        // map, curves nowhere". A river's meander wavelength is roughly ten to fourteen times its width, so a
        // twenty-six metre river turns about every three hundred metres: six bends on an 1800 m canvas, not
        // one. Two octaves, the second at a third the amplitude and two and a bit times the rate, so the bends
        // are not identical and the line never reads as a sine wave either.
        var bearing = random.Next() * MathF.Tau;
        var along = new Vector2(MathF.Cos(bearing), MathF.Sin(bearing));
        var sideways = new Vector2(-along.Y, along.X);
        var half = extentMetres * 0.5f;
        var turns = MathF.Max(3f, extentMetres / 300f);
        var phase = random.Next() * MathF.Tau;
        var trunk = new Vector2[Math.Max(9, (int)MathF.Round(turns * 4f))];
        for (var i = 0; i < trunk.Length; i++)
        {
            var t = i / (float)(trunk.Length - 1);
            var swing = MathF.Sin(t * MathF.Tau * turns * 0.5f + phase) * 0.86f
                + MathF.Sin(t * MathF.Tau * turns * 1.17f + phase * 2.3f) * 0.31f;
            // A tenth of the canvas per bend. Wider and the river doubles back into its own valley; narrower
            // and the meander is a wobble.
            trunk[i] = along * ((t - 0.5f) * 2f * half * 1.02f) + sideways * (swing * extentMetres * 0.105f);
        }

        // <b>Twenty-six metres, not twelve, and now that the authored width is honoured it means something.</b>
        // A river that has to be found a crossing for is the most useful single feature a map of this size can
        // carry, and twelve metres drawn at a canvas standoff is a line.
        separators.Add(new Separator(SeparatorKind.River, trunk, 26f, 0f));

        // <b>Tributaries that actually meet the trunk, because "meets nothing" was literally true.</b> An
        // instance's own river was kept as a separate watercourse and never joined anything — so a canvas had
        // several unrelated lines on it rather than one system. A tributary now starts in the hills and ends
        // <em>on a point of the trunk</em>, which is what makes a confluence, and confluences are most of what
        // makes a drainage read as one thing.
        var joins = 2 + (int)MathF.Round(random.Next() * 2f);
        for (var i = 0; i < joins; i++)
        {
            var t = 0.18f + (i + random.Next() * 0.6f) / joins * 0.7f;
            var onto = Math.Clamp((int)MathF.Round(t * (trunk.Length - 1)), 1, trunk.Length - 2);
            var mouth = trunk[onto];
            // Out to one side, far enough to have drained something on the way down.
            var hand = random.Next() < 0.5f ? 1f : -1f;
            var reach = extentMetres * (0.16f + random.Next() * 0.14f);
            var head = mouth + sideways * (hand * reach) + along * ((random.Next() - 0.5f) * reach);
            var stream = new Vector2[4];
            for (var k = 0; k < stream.Length; k++)
            {
                var u = k / (float)(stream.Length - 1);
                var wobble = MathF.Sin(u * MathF.PI * 1.7f + i) * reach * 0.16f;
                stream[k] = Vector2.Lerp(head, mouth, u) + along * wobble * hand;
            }

            // Narrower than the trunk, and the width it is authored at is the width it gets — a tributary that
            // arrives as wide as what it joins is not a tributary.
            separators.Add(new Separator(SeparatorKind.River, stream, 9f + random.Next() * 5f, 0f));
        }

        // <b>One or two lakes, on the river, because a lake off a river has nothing to fill it.</b> The
        // catchment rule that keeps standing water honest — see Drainage.LakeCatchmentMetres2 — will refuse a
        // basin nothing drains into, so placing them anywhere else would author a lake and then have the
        // classifier quietly delete it.
        // <b>Lakes, and they were far too small and far too few.</b> One or two at a hundred metres across on
        // an 1800 m canvas is a widening of the river, not a body of water — measured, the largest thing on the
        // map was the river itself at 2476 m long and nothing compact existed at all. Scaled with the canvas
        // like the neighbourhoods are, and sized so one fills a serious share of a 600 m window: a lake worth
        // framing a map around is two to four hundred metres across.
        var basins = new List<Basin>();
        var lakes = Math.Max(1, (int)MathF.Round(across * across / 2.6f));
        for (var i = 0; i < lakes; i++)
        {
            var t = (i + 0.5f + (random.Next() - 0.5f) * 0.5f) / lakes;
            var index = Math.Clamp((int)MathF.Round(t * (trunk.Length - 1)), 1, trunk.Length - 2);
            var at = Vector2.Lerp(trunk[index], trunk[index + 1], random.Next());
            // Downstream is the trunk's own direction here, so the sill lands across the flow rather than
            // along it — a bar parallel to the river dams nothing.
            var flow = trunk[Math.Min(index + 1, trunk.Length - 1)] - trunk[index];
            basins.Add(new Basin(
                at,
                MathF.Min(extentMetres * 0.14f, 96f + random.Next() * 108f),
                MathF.Max(5f, amplitudeMetres * (0.34f + random.Next() * 0.22f)),
                flow.LengthSquared() > 1e-4f ? Vector2.Normalize(flow) : along));
        }

        for (var z = 0; z < across; z++)
        for (var x = 0; x < across; x++)
        {
            var kind = only ?? All[(int)(random.Next() * All.Length) % All.Length];
            kinds.Add(kind);
            var cell = new Vector2(
                (x + 0.5f) / across - 0.5f,
                (z + 0.5f) / across - 0.5f) * extentMetres;
            // A third of a cell of jitter, which loosens the grid without letting two instances sit on top
            // of each other.
            var offset = new Vector2(random.Next() - 0.5f, random.Next() - 0.5f) * step * 0.33f;
            var instance = For(
                kind,
                step * 0.92f,
                seed * 2654435761u + (uint)(z * across + x) * 40503u + 11u,
                amplitudeMetres,
                cell + offset);

            foreach (var separator in instance.Separators)
            {
                // One trunk per canvas. An instance's own river is kept as a tributary only if it is not the
                // instance's sole statement, so an archetype that is *about* its river does not lose it.
                if (separator.Kind == SeparatorKind.River && instance.Separators.Length == 1) continue;
                separators.Add(separator);
            }

            connectors.AddRange(instance.Connectors);
            regions.AddRange(instance.Regions);
            troughs.AddRange(instance.Troughs);
            basins.AddRange(instance.Basins);
            uplands.AddRange(instance.Uplands);
        }

        var tally = kinds.GroupBy(kind => kind)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Count() > 1 ? $"{group.Count()}x {group.Key}" : group.Key.ToString());
        var water = basins.Count == 1 ? "a lake on it" : $"{basins.Count} lakes on it";
        return new MapLayout(
            only ?? kinds[0],
            $"a river with {water}, through {across * across} neighbourhoods: {string.Join(", ", tally)}",
            separators.ToArray(),
            connectors.ToArray(),
            regions.ToArray(),
            Vector2.Zero,
            amplitudeMetres,
            basins.ToArray(),
            troughs.ToArray(),
            uplands.ToArray());
    }

    /// <summary>Every river in this layout, trunk first.</summary>
    public IEnumerable<Separator> Rivers()
    {
        foreach (var separator in Separators)
        {
            if (separator.Kind == SeparatorKind.River) yield return separator;
        }
    }

    /// <summary>The river in this layout, if it has one.</summary>
    public Separator? River()
    {
        foreach (var separator in Separators)
        {
            if (separator.Kind == SeparatorKind.River) return separator;
        }

        return null;
    }

    /// <summary>How much a connector opens the ground at a place: 1 fully open, 0 untouched.</summary>
    /// <remarks>
    /// A connector is expressed as a <em>suppression</em> of whatever separator runs through it rather than as
    /// a shape of its own. That is what makes a saddle a saddle: it is not a thing added to a ridge, it is the
    /// ridge not being there. The same mechanism already cuts the frontier's gates.
    /// </remarks>
    public float Opening(Vector2 at, ConnectorOf of, int index)
    {
        var open = 0f;
        foreach (var connector in Connectors)
        {
            // Only the connectors that belong to this feature. Before this test a saddle cut for a ridge also
            // cut whatever else happened to be under it, which on a composed map is usually an upland.
            if (connector.Of != of || connector.Index != index) continue;
            // Relief-modifying kinds only. A ford is a connector and lowers nothing.
            if (connector.Kind is not (ConnectorKind.Saddle or ConnectorKind.Ramp)) continue;
            // A box test before the square root: this runs once per lattice cell per connector.
            if (MathF.Abs(at.X - connector.At.X) > connector.WidthMetres) continue;
            if (MathF.Abs(at.Y - connector.At.Y) > connector.WidthMetres) continue;
            var reach = Math.Clamp(1f - Vector2.Distance(at, connector.At) / connector.WidthMetres, 0f, 1f);
            open = MathF.Max(open, reach * reach * (3f - 2f * reach));
        }

        return open;
    }

    /// <summary>
    /// How much a ford relaxes the water of separator <paramref name="index"/> here: 0 none, 1 fully.
    /// </summary>
    /// <remarks>
    /// A ford is a shallow, broad-bedded place in a channel — gravel rather than a gorge — so it acts on the
    /// river's width and its depth rather than on the ground's height. Which is why it is a separate query from
    /// <see cref="Opening"/>: suppressing relief at a ford would dig a pit in the riverbed, and a pit is the
    /// opposite of a crossing.
    /// </remarks>
    public float Ford(Vector2 at, int index)
    {
        var eased = 0f;
        foreach (var connector in Connectors)
        {
            if (connector.Kind != ConnectorKind.Ford) continue;
            if (connector.Of != ConnectorOf.Separator || connector.Index != index) continue;
            if (MathF.Abs(at.X - connector.At.X) > connector.WidthMetres) continue;
            if (MathF.Abs(at.Y - connector.At.Y) > connector.WidthMetres) continue;
            var reach = Math.Clamp(1f - Vector2.Distance(at, connector.At) / connector.WidthMetres, 0f, 1f);
            eased = MathF.Max(eased, reach * reach * (3f - 2f * reach));
        }

        return eased;
    }

    /// <summary>
    /// A repeatable sequence, because a layout decides ground and ground is fingerprinted.
    /// </summary>
    private struct Roll(uint seed)
    {
        private uint state = seed;

        public float Next()
        {
            state += 0x9E3779B9u;
            var z = state;
            z = (z ^ (z >> 16)) * 0x21F0AAADu;
            z = (z ^ (z >> 15)) * 0x735A2D97u;
            z ^= z >> 15;
            return (z & 0xFFFFFFu) / (float)0x1000000u;
        }
    }
}
