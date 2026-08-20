using System.Numerics;
using Blix.Graphics;
using Blix.Graphics.Primitives;
using Blix.Render;
using RTSGame.Simulation;
using RTSGame.Simulation.Economy;

namespace RTSGame.Rendering;

/// <summary>
/// Smoke from the chimneys of houses that have somebody in them.
/// </summary>
/// <remarks>
/// <b>Dressing, by §52's test: nothing here is state.</b> A puff is a position, a birth time and a seed;
/// everything else about it — where it has got to, how big it is, how much of it is left — is a function of
/// its age, evaluated when it is drawn. So there is nothing to integrate, nothing to save, and a loaded
/// game has no smoke for a second and then has smoke again. The ring is bounded and overwrites its oldest,
/// which is the same shape as the felling record the stumps come from.
/// <para>
/// <b>And it is a second channel for the season, which is why it is worth more than it looks.</b> A hearth
/// is lit because it is cold or because a meal is being cooked, so it smokes hard all winter, twice a day
/// in summer, and somewhere between the two in spring and autumn. That is one function of the date and the
/// hour, it costs nothing, and it means a glance at the village tells you the month even in flat light —
/// the property the whole seasonal palette is built for, arriving here for free.
/// </para>
/// </remarks>
internal sealed class Hearths
{
    /// <summary>How many puffs may be alive at once.</summary>
    /// <remarks>
    /// A hard ceiling rather than a rate that scales, for the reason the grass has one: smoke is the sort
    /// of thing that looks fine at ten houses and costs a frame at two hundred. At the default rate and
    /// lifetime a chimney carries about eight puffs, so this is room for forty smoking houses and no more.
    /// </remarks>
    private const int Capacity = 448;

    /// <summary>How long a puff lasts, in simulated seconds.</summary>
    private const float LifeSeconds = 9f;

    /// <summary>How fast smoke climbs, in metres a simulated second.</summary>
    private const float RiseMetresPerSecond = 1.15f;

    /// <summary>A puff's radius when it leaves the chimney, and how fast it swells.</summary>
    /// <remarks>
    /// Smaller at birth and swelling faster than the first version. A wisp is a thing that comes out thin
    /// and spreads until it is gone; a puff that starts fat and grows slowly is a bubble that drifts.
    /// </remarks>
    private const float BirthRadius = 0.22f;
    private const float GrowthPerSecond = 0.52f;

    /// <summary>Puffs a second from one lit chimney, before the season has its say.</summary>
    /// <remarks>
    /// Twice the first rate, which is the other half of making smoke wispy: each puff now carries about a
    /// third of the opacity it did, so a plume that still reads has to be built out of more of them. The
    /// two changes together are the same total density arranged as a stream rather than as a chain.
    /// </remarks>
    private const float PuffsPerSecond = 2.3f;

    private readonly struct Puff
    {
        public Puff(Vector2 origin, float baseY, float born, float seed)
        {
            Origin = origin;
            BaseY = baseY;
            Born = born;
            Seed = seed;
        }

        public readonly Vector2 Origin;
        public readonly float BaseY;
        public readonly float Born;
        public readonly float Seed;
    }

    private readonly Puff[] puffs = new Puff[Capacity];
    private int next;
    private float lastSeconds = float.NaN;

    /// <summary>How many puffs were drawn last frame, for the diagnostics line.</summary>
    internal int Drawn { get; private set; }

    /// <summary>How many chimneys were smoking last frame.</summary>
    internal int Chimneys { get; private set; }

    /// <summary>
    /// Lights the chimneys that should be lit and puts a puff over each when its turn comes round.
    /// </summary>
    /// <remarks>
    /// <b>The turn comes round without a timer per chimney.</b> Each house's phase is its own id hashed
    /// into the interval, so the spawns are spread evenly across it and no two houses puff in step —
    /// without a dictionary, without a field per node, and identically after a reload. It is the same
    /// trick the tree models and yaws use, and it is why this class has three fields rather than a table.
    /// </remarks>
    internal void Advance(
        SimulationWorld world, SettlementArt? art, CalendarDate date, float hourOfDay, float simSeconds,
        Vector2 focus, float radiusMeters, float density)
    {
        var previous = float.IsNaN(lastSeconds) ? simSeconds : lastSeconds;
        lastSeconds = simSeconds;
        Chimneys = 0;
        if (density <= 0f) return;

        var rate = PuffsPerSecond * density * Activity(date.Season, hourOfDay);
        if (rate <= 0.001f) return;
        var interval = 1f / rate;
        var radiusSquared = radiusMeters * radiusMeters;

        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || !node.IsSink || node.Occupants <= 0) continue;
            // Only what can be seen, on the same argument the trees are culled by: a plume forty metres
            // behind the camera is eight draws of nothing.
            if (Vector2.DistanceSquared(node.Position, focus) > radiusSquared) continue;
            Chimneys++;

            var offset = Hash01((uint)node.Id.Value * 2654435761u);
            var was = MathF.Floor(previous / interval + offset);
            var now = MathF.Floor(simSeconds / interval + offset);
            if (now <= was) continue;

            var width = node.HalfExtent * 2f;
            var ground = world.Terrain.SampleHeight(node.Position);
            // The chimney rather than the middle of the roof, placed against the model's own yaw so it
            // stays on the same corner of the same cottage for the life of the village.
            var yaw = SettlementArt.SquareYawOf(node.Id.Value);
            var along = SettlementArt.FaceDirection(yaw);
            // <b>Off the model's own bounds, because a fitted model is not a unit cube.</b> Normalising to
            // a unit footprint makes the longer horizontal axis one and leaves the other two wherever they
            // fall, so "the ridge is about a width up" is wrong for anything but a cottage as tall as it is
            // wide — and smoke starting above the roof of one is smoke coming out of the air.
            var model = art?.HouseFor(node.Id.Value);
            var ridge = model is null ? 1f : model.Bounds.Max.Y;
            // Off the walls rather than the roof, so a chimney comes out of the ridge and not of the eaves.
            var reach = model is null || art is null
                ? 0.5f
                : MathF.Min(art.WallsOf(model).Max.X, art.WallsOf(model).Max.Z);
            var chimney = node.Position + along * (width * reach * 0.52f) +
                          new Vector2(-along.Y, along.X) * (width * reach * 0.40f);
            puffs[next] = new Puff(
                chimney, ground + width * ridge * 0.96f, simSeconds, Hash01((uint)next * 747796405u));
            next = (next + 1) % Capacity;
        }
    }

    /// <summary>
    /// Puts the live puffs into a batch, each one derived entirely from how old it is.
    /// </summary>
    internal void Emit(
        InstancedBatch batch, float simSeconds, float windBearingRadians, float gustRate, float driftSpeed)
    {
        Drawn = 0;
        var downwind = new Vector2(MathF.Sin(windBearingRadians), MathF.Cos(windBearingRadians));
        var across = new Vector2(downwind.Y, -downwind.X);
        foreach (ref readonly var puff in puffs.AsSpan())
        {
            if (puff.Born <= 0f) continue;
            var age = simSeconds - puff.Born;
            if (age < 0f || age >= LifeSeconds) continue;
            var life = age / LifeSeconds;

            // The same gust wave the trees lean to, sampled at the puff's own origin — so a gust arrives
            // at a plume and at the wood behind it together.
            var gust = 0.55f + 0.45f * MathF.Sin(
                simSeconds * gustRate + (puff.Origin.X + puff.Origin.Y * 0.7f) * 0.02f);
            // Downwind travel grows with age, and a little across it that reverses, which is what gives a
            // plume its kink rather than making it a straight ramp.
            var carried = downwind * (driftSpeed * gust * age) +
                          across * MathF.Sin(age * 0.9f + puff.Seed * 6.28f) * (driftSpeed * 0.30f * age);
            // Rising slows as it cools and spreads.
            var height = RiseMetresPerSecond * age * (1f - 0.18f * life);
            var at = new Vector3(
                puff.Origin.X + carried.X, puff.BaseY + height, puff.Origin.Y + carried.Y);

            var radius = BirthRadius + GrowthPerSecond * age;
            // In fast, out slow: smoke appears at the chimney mouth and thins away over the whole of the
            // rest of its life. Fading in as slowly as it fades out puts a gap above every chimney.
            // A third of what it was, because the shader now shows only a small cap of each puff and the
            // density comes from their number instead — see PuffsPerSecond.
            var opacity = 0.075f * MathF.Min(1f, life / 0.10f) * MathF.Pow(1f - life, 1.5f);
            // <b>Not a sphere, and no two the same shape.</b> A round puff is a bubble however softly it is
            // shaded, so each one is a lumpy ellipsoid: its own proportions from its own seed, and stretched
            // upward as it ages, because a column of rising air pulls what is in it into streaks. This is
            // most of what separates smoke from soap.
            var lumpX = 0.62f + puff.Seed * 0.70f;
            var lumpZ = 0.62f + (1f - puff.Seed) * 0.70f;
            var drawn = Vector3.Lerp(new Vector3(0.26f, 0.24f, 0.23f), new Vector3(0.68f, 0.68f, 0.70f), life);
            batch.Add(
                Matrix4x4.CreateScale(
                    radius * lumpX, radius * (1.0f + 1.5f * life), radius * lumpZ) *
                Matrix4x4.CreateTranslation(at),
                new Vector4(drawn.X, drawn.Y, drawn.Z, opacity));
            Drawn++;
        }
    }

    /// <summary>
    /// How hard the hearths are going, from the season and the hour.
    /// </summary>
    /// <remarks>
    /// Two reasons a fire is lit and they have different shapes. <b>Heating</b> is a season: constant
    /// through a winter day and absent in summer. <b>Cooking</b> is an hour: twice a day, every day of the
    /// year, and it is what keeps a summer village from looking abandoned. Adding them rather than choosing
    /// between them is what makes a winter morning the smokiest thing in the game.
    /// </remarks>
    private static float Activity(Season season, float hourOfDay)
    {
        var heating = season switch
        {
            Season.Winter => 1.00f,
            Season.Harvest => 0.45f,
            Season.Spring => 0.35f,
            _ => 0.05f,
        };
        // Morning and evening meals, as two soft windows rather than two switches.
        var morning = Window(hourOfDay, 6.5f, 2.0f);
        var evening = Window(hourOfDay, 18.5f, 2.5f);
        var cooking = 0.55f * MathF.Max(morning, evening);
        // Banked overnight rather than out: a house with people asleep in it is still warm in winter.
        var night = hourOfDay < 5f || hourOfDay > 22f ? 0.45f : 1f;
        return (heating + cooking) * night;
    }

    private static float Window(float hour, float centre, float halfWidth)
    {
        var distance = MathF.Abs(hour - centre) / halfWidth;
        return distance >= 1f ? 0f : 1f - distance * distance;
    }

    /// <summary>A stable number in [0,1) from an integer, for staggering without storing anything.</summary>
    private static float Hash01(uint seed)
    {
        seed ^= seed >> 16;
        seed *= 2246822519u;
        seed ^= seed >> 13;
        seed *= 3266489917u;
        seed ^= seed >> 16;
        return (seed & 0xFFFFFF) / (float)0x1000000;
    }
}
