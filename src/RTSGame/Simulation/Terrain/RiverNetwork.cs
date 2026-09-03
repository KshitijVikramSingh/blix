using System;
using System.Collections.Generic;
using System.Numerics;

namespace RTSGame.Simulation.Terrain;

/// <summary>
/// A drainage network authored before the ground it runs through.
/// </summary>
/// <remarks>
/// <b>§150's inversion.</b> The old order sampled a landform vocabulary — troughs, uplands, separators — then
/// eroded it, then asked <see cref="Drainage"/> to <em>discover</em> water in whatever came out. That
/// vocabulary has no concept of a drainage network, so water had to find a story in ground not built to tell
/// one, and §151 measured the result: <b>every one of fifty-five maps had watercourses running uphill</b>,
/// between six and six hundred and forty-three of them, and forty-six of the fifty-five had less than two
/// metres of fall per hundred for water to move on.
/// <para>
/// So the river comes first. An outlet, a stem grown inland, tributaries hung off it, and <b>every reach's
/// elevation set by how far it is from the outlet along its own channel</b> — which makes monotone downhill a
/// property of construction rather than something a taper approximates afterwards. The ground is then built
/// around it: a valley exists because a river does.
/// </para>
/// </remarks>
internal sealed class RiverNetwork
{
    /// <summary>One straight length of channel, with the water's height at each end.</summary>
    /// <remarks>
    /// Straight because the query below is a point-to-segment distance and a curve would be a spline nobody
    /// needs: a reach is short enough that its own meander is the step length, and the network's shape comes
    /// from how the reaches are strung together.
    /// </remarks>
    internal readonly record struct Reach(
        Vector2 From,
        Vector2 To,
        float FromHeight,
        float ToHeight,
        float Discharge)
    {
        /// <summary>Metres across, off the same curve the solver's own width uses.</summary>
        public float WidthMetres => Drainage.WidthOf(Discharge);
    }

    private readonly List<Reach> reaches = new();

    public IReadOnlyList<Reach> Reaches => reaches;

    /// <summary>Where the water leaves the map, which is the lowest point on it by construction.</summary>
    public Vector2 Outlet { get; private init; }

    /// <summary>The highest channel head, which is what the ground's amplitude is measured against.</summary>
    public float Headwater { get; private set; }

    /// <summary>
    /// Grows a network from an outlet on the map's edge.
    /// </summary>
    /// <param name="extent">Map side in metres.</param>
    /// <param name="seed">Deterministic, because this decides ground that gets fingerprinted.</param>
    /// <param name="fallPerMetre">
    /// How much a channel climbs per metre of its own length. This is the number §151 found the old generator
    /// short of on forty-six maps out of fifty-five, and here it is an input rather than an outcome.
    /// </param>
    /// <param name="branches">How many tributaries to hang off the network, before the length floor stops it.</param>
    public static RiverNetwork Grow(float extent, uint seed, float fallPerMetre, int branches)
    {
        var random = new Splitmix(seed ^ 0x51ED270Bu);
        var half = extent * 0.5f;

        // The outlet sits on one edge, away from the corners — a corner outlet gives the map one drainage
        // direction and a diagonal of dead ground behind it.
        var side = random.Next(4);
        var along = (random.Unit() * 0.5f + 0.25f) * extent - half;
        var outlet = side switch
        {
            0 => new Vector2(along, -half),
            1 => new Vector2(along, half),
            2 => new Vector2(-half, along),
            _ => new Vector2(half, along),
        };

        // Inward, with the stem aiming across the map rather than at its centre, so the far bank has room for
        // an interfluve instead of ending at the middle.
        var inward = Vector2.Normalize(-outlet + new Vector2(0.0001f, 0f));
        var network = new RiverNetwork { Outlet = outlet };
        var heads = new List<(Vector2 At, float Height, Vector2 Heading, float Discharge, float Budget)>
        {
            (outlet, 0f, inward, StemDischarge, extent * 0.95f),
        };

        // Breadth-first, so the stem is laid before the tributaries that hang off it and every tributary
        // starts from a channel that already has an elevation.
        var hung = 0;
        while (heads.Count > 0)
        {
            var (at, height, heading, discharge, budget) = heads[0];
            heads.RemoveAt(0);
            var walked = 0f;
            while (walked < budget)
            {
                // A meander: the heading turns a little each step and is pulled back toward the map when it
                // strays, so a channel wanders without leaving the ground it is draining.
                var turn = (random.Unit() - 0.5f) * MeanderRadians;
                heading = Rotate(heading, turn);
                var next = at + heading * StepMetres;
                if (MathF.Abs(next.X) > half * 0.94f || MathF.Abs(next.Y) > half * 0.94f)
                {
                    heading = Vector2.Normalize(Vector2.Lerp(heading, -Vector2.Normalize(at), 0.65f));
                    next = at + heading * StepMetres;
                }

                // <b>The whole point: height is distance from the outlet along the channel.</b> Upstream is
                // always higher than downstream because it is always further, and no later pass is allowed to
                // change that without the criteria noticing.
                var rise = StepMetres * fallPerMetre;
                network.reaches.Add(new Reach(at, next, height, height + rise, discharge));
                network.Headwater = MathF.Max(network.Headwater, height + rise);

                at = next;
                height += rise;
                walked += StepMetres;

                // A tributary joins here, taking a share of the discharge and heading off at an angle. Hung
                // from the point rather than grown to it, so the junction's elevation is the stem's and the
                // tributary climbs away from it.
                if (hung < branches && walked > StepMetres * 2f && random.Unit() < BranchChance)
                {
                    hung++;
                    var handed = discharge * (0.25f + 0.2f * random.Unit());
                    discharge -= handed * 0.5f;
                    var away = Rotate(heading, (random.Unit() < 0.5f ? 1f : -1f) * ForkRadians);
                    heads.Add((at, height, away, handed, MathF.Max(budget - walked, extent * 0.2f)));
                }
            }
        }

        return network;
    }

    /// <summary>
    /// The lowest water this point sits above, and how far away that channel is.
    /// </summary>
    /// <remarks>
    /// The minimum over reaches rather than the nearest one: at a confluence the ground belongs to whichever
    /// water is lower, which is what makes a junction a junction rather than a ridge between two valleys.
    /// </remarks>
    public (float Ground, float Water, float Distance, float Width) Floor(
        Vector2 at,
        float flank,
        float shoulder)
    {
        // <b>Minimised on the ground height each reach would give, and the first version mixed two
        // criteria.</b> §152: it chose the reach with the lowest <em>water</em> and then used that reach's
        // distance for the rise, so two neighbouring cells could pick different reaches and take unrelated
        // heights — bumps with no landform behind them. What a valley floor actually is, is the lowest
        // surface any nearby water can put under this point, so that is the quantity minimised.
        var bestGround = float.MaxValue;
        var bestWater = 0f;
        var bestDistance = 0f;
        var bestWidth = 0f;
        foreach (var reach in reaches)
        {
            var span = reach.To - reach.From;
            var length = span.LengthSquared();
            var t = length <= 1e-6f ? 0f : Math.Clamp(Vector2.Dot(at - reach.From, span) / length, 0f, 1f);
            var on = reach.From + span * t;
            var distance = Vector2.Distance(at, on);
            var water = reach.FromHeight + (reach.ToHeight - reach.FromHeight) * t;
            var bank = MathF.Max(reach.WidthMetres * 0.5f, 3f);
            var out_ = MathF.Max(0f, distance - bank);
            var ground = water + shoulder * (1f - MathF.Exp(-out_ * flank / MathF.Max(1f, shoulder)));
            if (ground >= bestGround) continue;
            bestGround = ground;
            bestWater = water;
            bestDistance = distance;
            bestWidth = reach.WidthMetres;
        }

        return bestGround >= float.MaxValue
            ? (0f, 0f, 0f, 0f)
            : (bestGround, bestWater, bestDistance, bestWidth);
    }

    /// <summary>Metres of channel per step. Short enough to meander, long enough not to be a spline.</summary>
    private const float StepMetres = 18f;

    /// <summary>How far a channel may turn in one step.</summary>
    private const float MeanderRadians = 0.55f;

    /// <summary>The angle a tributary leaves its trunk at.</summary>
    private const float ForkRadians = 1.05f;

    private const float BranchChance = 0.16f;

    /// <summary>
    /// Upslope area the trunk carries, in the units <see cref="Drainage.WidthOf"/> expects.
    /// </summary>
    /// <remarks>
    /// Sized so the trunk comes out around twenty metres across — a river a village is built on one side of,
    /// which is the width §7's fords and crossings were designed against.
    /// </remarks>
    private const float StemDischarge = 6_000_000f;

    private static Vector2 Rotate(Vector2 vector, float radians)
    {
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return Vector2.Normalize(new Vector2(vector.X * cos - vector.Y * sin, vector.X * sin + vector.Y * cos));
    }

    /// <summary>The same integer avalanche the relief plan uses, for the same reason: this ground is saved.</summary>
    private struct Splitmix
    {
        private uint state;

        public Splitmix(uint seed) => state = seed == 0 ? 0x9E3779B9u : seed;

        public float Unit()
        {
            state += 0x9E3779B9u;
            var z = state;
            z = (z ^ (z >> 16)) * 0x21F0AAADu;
            z = (z ^ (z >> 15)) * 0x735A2D97u;
            z ^= z >> 15;
            return (z & 0xFFFFFFu) / (float)0x1000000u;
        }

        public int Next(int bound) => (int)(Unit() * bound) % bound;
    }
}
