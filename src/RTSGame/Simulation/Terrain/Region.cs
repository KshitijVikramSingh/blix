namespace RTSGame.Simulation.Terrain;

using System.Numerics;

/// <summary>What kind of country this whole landscape is.</summary>
/// <remarks>
/// Not what kind of ground a place is — <see cref="Biome"/> answers that, and it answers it the same way
/// everywhere. This is the layer above: <em>what sort of country is this a place in</em>.
/// </remarks>
internal enum Region
{
    /// <summary>Temperate pasture and rough grazing. Green, with moor on the tops.</summary>
    Downland,

    /// <summary>Low, wet, slow water. Willow and alder, reed beds, and very little high ground.</summary>
    FenCountry,

    /// <summary>High, thin-soiled, wind-scoured. Heather over most of it and few trees.</summary>
    UplandHeath,

    /// <summary>Dry. Straw-coloured grass over pale stone, bare earth showing, watercourses deeply cut.</summary>
    DryScrub,

    /// <summary>Cold and forested. Conifer to the horizon, moss, and bog in the hollows.</summary>
    Boreal,
}

/// <summary>
/// Everything a region changes about the ground: what the country is made of and what it looks like.
/// </summary>
/// <remarks>
/// <b>This layer exists because eight archetypes were producing eight shapes of one country.</b> Reported from
/// the chair: "the presets look mostly the same terrain type, grasslands or highlands with some water here or
/// there, green all over." Correct, and structural — <see cref="Biomes"/> is one classifier with one palette,
/// so it had exactly one climate in it, temperate north-west European. The archetype layer varies
/// <em>topology</em>, and nothing varied <em>character</em>. They are orthogonal axes, and only one of them
/// existed.
/// <para>
/// <b>It composes with the classifier rather than replacing it, and that is only possible because the
/// classifier reasons in ranks.</b> §60 moved it off absolute wetness thresholds onto quantiles of the
/// landscape's own distribution — for portability between map sizes, at the time. The payoff turns out to be
/// this: a dry region is the same rule with the fen rank pushed to the top of the distribution, and a fen
/// country is the same rule with it pulled down. No second classifier, no special cases, and the causal story
/// is untouched — what makes a place wet is still how much water arrives and how fast it leaves.
/// </para>
/// <para>
/// Eight archetypes times five regions is forty kinds of map, from two small tables.
/// </para>
/// </remarks>
internal readonly record struct RegionProfile(
    string Name,
    string Character,
    float FenRank,
    float FloodplainRank,
    float MoorRank,
    float MoorAbove,
    float ScreeGrade,
    float WaterScale,
    float TreeDensity,
    float Rockiness,
    float ConiferShare,
    float Lushness,
    Vector4 Pasture,
    Vector4 Heath,
    Vector4 Rough,
    Vector4 Mud)
{
    /// <summary>Every region there is, for a lab that cycles them.</summary>
    public static Region[] All { get; } = Enum.GetValues<Region>();

    /// <summary>
    /// The profile for a region.
    /// </summary>
    /// <remarks>
    /// <b>The ranks are the interesting column, and they read as claims about rainfall.</b> A fen rank of 0.78
    /// says "the wettest fifth of this landscape is fen", which is what low wet country is; 0.99 says "almost
    /// nowhere here is fen", which is what dry country is.
    /// <para>
    /// <b>The moor rank reads the same way round, and I wrote it the other way round first.</b> The test is
    /// <c>wetness &lt; quantile(MoorRank)</c>, so it is the share of the landscape dry enough to count — a
    /// <em>higher</em> number is more moor. Set backwards, upland heath came out with less heather on it than
    /// downland, which is the kind of inversion that is invisible in the code and obvious in one measurement.
    /// </para>
    /// <para>
    /// <b><c>Rockiness</c> is how much of this country's stone is above ground, and it reads off the same
    /// descriptions.</b> Upland heath is "thin soil, stone breaking through" and dry scrub is "straw grass over
    /// pale stone" — both were already saying it in prose, and a fen was already saying the opposite. It scales
    /// how big a deposit is rather than whether one is there, so a rocky country has real quarries and a wet one
    /// has the odd boulder. Note what follows without being written down: stone lives on thin soil, thin soil is
    /// poor farmland, and the site scorer wants fertile ground — <b>so the countries richest in stone are the
    /// ones a settlement least wants to sit in</b>, and that tension is the mechanic rather than a side effect.
    /// </para>
    /// <para>
    /// <b><c>WaterScale</c> is the climate acting on the composition rather than on the picture.</b> Every
    /// other column here changes what the ground looks like; this one changes how much water the layout's own
    /// statements are allowed to carry — it multiplies authored channel widths and basin depths. So dry country
    /// does not merely <em>look</em> dry: its river is a stream in a bed too big for it and its lake is a pan,
    /// which is what dry country is. A region that only recoloured the ground would have a full river running
    /// through a desert.
    /// <para>
    /// <c>MoorAbove</c> is the other half of the moor rank, and it matters as much: moor also has to be high,
    /// and on a heath the whole point is that it is not confined to the tops. <c>ScreeGrade</c> is how steep ground has
    /// to be before the soil has gone — thin-soiled country shows stone on a gentler slope than pasture does,
    /// and without it every region had identical scree.
    /// <para>
    /// Colours are in linear space, like everything else the shader works in, and they are the ground's own
    /// albedo before the seasonal grade. Which is why they can be this flat: the grade, the wear and the
    /// macro noise all act on top, so a region needs to state its hue and its value and nothing more.
    /// </para>
    /// </remarks>
    public static RegionProfile For(Region region) => region switch
    {
        // The one everything was calibrated against, and the defaults are its numbers unchanged.
        Region.Downland => new RegionProfile(
            "downland",
            "temperate pasture, rough grazing on the tops",
            FenRank: 0.95f,
            FloodplainRank: 0.80f,
            MoorRank: 0.45f,
            MoorAbove: 0.42f,
            ScreeGrade: 0.30f,
            WaterScale: 1.00f,
            TreeDensity: 1.00f,
            Rockiness: 1.00f,
            ConiferShare: 0.25f,
            Lushness: 1.00f,
            Pasture: new Vector4(0.144f, 0.195f, 0.075f, 1f),
            Heath: new Vector4(0.170f, 0.163f, 0.090f, 1f),
            Rough: new Vector4(0.230f, 0.190f, 0.115f, 1f),
            Mud: new Vector4(0.125f, 0.098f, 0.062f, 1f)),

        // Wet, level, and almost no high ground. The greens go blue and the browns come forward, because what
        // you see across a fen is water and dead reed as much as it is grass.
        Region.FenCountry => new RegionProfile(
            "fen country",
            "wet levels, willow and reed, water everywhere",
            FenRank: 0.78f,
            FloodplainRank: 0.55f,
            MoorRank: 0.18f,
            MoorAbove: 0.60f,
            ScreeGrade: 0.38f,
            WaterScale: 1.40f,
            TreeDensity: 0.55f,
            // Wet levels, and the one place in this table where the ground is deep everywhere: peat and silt
            // over more peat. What little stone a fen has, somebody carted in.
            Rockiness: 0.30f,
            ConiferShare: 0.05f,
            Lushness: 1.30f,
            Pasture: new Vector4(0.118f, 0.183f, 0.098f, 1f),
            Heath: new Vector4(0.150f, 0.150f, 0.104f, 1f),
            Rough: new Vector4(0.170f, 0.160f, 0.120f, 1f),
            Mud: new Vector4(0.105f, 0.091f, 0.066f, 1f)),

        // Heather over most of it. The moor rank is the low one here, which is what puts heath on every
        // shoulder rather than only on the tops.
        Region.UplandHeath => new RegionProfile(
            "upland heath",
            "heather and thin soil, stone breaking through",
            FenRank: 0.985f,
            FloodplainRank: 0.93f,
            MoorRank: 0.80f,
            MoorAbove: 0.18f,
            ScreeGrade: 0.22f,
            WaterScale: 0.90f,
            TreeDensity: 0.32f,
            // "Heather and thin soil, stone breaking through" — its own description, now a number.
            Rockiness: 1.75f,
            ConiferShare: 0.60f,
            Lushness: 0.70f,
            Pasture: new Vector4(0.132f, 0.166f, 0.086f, 1f),
            // Toward the purple-brown a heather moor actually is, which is the single most recognisable
            // colour in upland Britain and nothing else in this palette goes near it.
            Heath: new Vector4(0.163f, 0.128f, 0.108f, 1f),
            Rough: new Vector4(0.205f, 0.196f, 0.176f, 1f),
            Mud: new Vector4(0.115f, 0.094f, 0.070f, 1f)),

        // Dry. Straw over pale stone, and the fen rank almost at the ceiling so a marsh is a rarity rather
        // than a feature.
        Region.DryScrub => new RegionProfile(
            "dry scrub",
            "straw grass over pale stone, water deep in its bed",
            FenRank: 0.992f,
            FloodplainRank: 0.94f,
            MoorRank: 0.72f,
            MoorAbove: 0.34f,
            ScreeGrade: 0.24f,
            WaterScale: 0.42f,
            TreeDensity: 0.22f,
            // "Straw grass over pale stone." Dry country sheds its soil and shows what is underneath.
            Rockiness: 1.55f,
            ConiferShare: 0.35f,
            Lushness: 0.52f,
            Pasture: new Vector4(0.242f, 0.216f, 0.106f, 1f),
            Heath: new Vector4(0.244f, 0.204f, 0.124f, 1f),
            Rough: new Vector4(0.290f, 0.262f, 0.196f, 1f),
            Mud: new Vector4(0.176f, 0.146f, 0.098f, 1f)),

        // Forest to the horizon, moss underfoot, bog in the hollows. The tree density is the highest here and
        // it is what the region is: everything else is what shows between the trunks.
        _ => new RegionProfile(
            "boreal forest",
            "conifer to the horizon, moss and bog between",
            FenRank: 0.87f,
            FloodplainRank: 0.82f,
            MoorRank: 0.42f,
            MoorAbove: 0.45f,
            ScreeGrade: 0.34f,
            WaterScale: 1.15f,
            TreeDensity: 1.65f,
            // Glaciated: plenty of bare rock, but moss and bog fill the hollows between it.
            Rockiness: 1.10f,
            ConiferShare: 0.92f,
            Lushness: 0.92f,
            Pasture: new Vector4(0.098f, 0.154f, 0.086f, 1f),
            Heath: new Vector4(0.121f, 0.140f, 0.098f, 1f),
            Rough: new Vector4(0.176f, 0.170f, 0.152f, 1f),
            Mud: new Vector4(0.096f, 0.088f, 0.062f, 1f)),
    };
}
