using System.Linq;

namespace Blix;

/// <summary>
/// Per-bone weights in [0, 1] — how much of a layer reaches each bone.
/// </summary>
/// <remarks>
/// <para>
/// <b>The answer to "which bones", and it is a subtree rather than a list.</b> A mask written as bone
/// indices is an asset's private numbering leaking into a game's source: re-export the rig with one
/// more finger joint and every index moves. A subtree is named by its root, and the name is the thing
/// the artist and the programmer already agree on.
/// </para>
/// <para>
/// <b>And "resolved how" is one multiplication.</b> The weight here is multiplied by whatever weight
/// the caller is blending at, and that is the whole rule — no modes, no override/additive/replace
/// enumeration. The engine composes poses from explicit weights and knows nothing about states,
/// links or durations; a crossfade is the caller moving a weight and this is what it scales.
/// </para>
/// <para>
/// Deferred from the animation arc until a consumer asked, on the grounds that "masks are policy:
/// which bones, resolved how, blended in what space". The consumer asked with a body playing a
/// one-shot chop while walking, its legs frozen mid-swing because one pose source cannot do both.
/// </para>
/// </remarks>
public sealed class BoneMask
{
    private readonly float[] weights;

    private BoneMask(float[] weights) => this.weights = weights;

    public int BoneCount => weights.Length;

    /// <summary>How much of a layer reaches this bone.</summary>
    public float this[int bone] => weights[bone];

    public IReadOnlyList<float> Weights => weights;

    /// <summary>Every bone, fully.</summary>
    public static BoneMask All(int boneCount, float weight = 1f)
    {
        var w = new float[boneCount];
        Array.Fill(w, Math.Clamp(weight, 0f, 1f));
        return new BoneMask(w);
    }

    /// <summary>No bone at all. A layer through this changes nothing, bit for bit.</summary>
    public static BoneMask None(int boneCount) => new(new float[boneCount]);

    /// <summary>The bone named <paramref name="rootBone"/> and everything beneath it.</summary>
    /// <exception cref="ArgumentException">There is no bone with that name.</exception>
    public static BoneMask Subtree(Skeleton skeleton, string rootBone, float weight = 1f, int falloff = 0)
    {
        ArgumentNullException.ThrowIfNull(skeleton);

        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            if (skeleton.Bones[i].Name == rootBone) return Subtree(skeleton, i, weight, falloff);
        }

        // Named rather than silently empty: a mask over a bone that does not exist is a typo, and a
        // typo that produces a layer which quietly changes nothing is the worst shape a bug can take.
        throw new ArgumentException(
            $"No bone named '{rootBone}' in this skeleton. It has {skeleton.BoneCount}: " +
            string.Join(", ", skeleton.Bones.Select(b => b.Name).Take(12)) +
            (skeleton.BoneCount > 12 ? ", ..." : string.Empty),
            nameof(rootBone));
    }

    /// <summary>The bone at <paramref name="rootBone"/> and everything beneath it.</summary>
    /// <remarks>
    /// <para>
    /// One forward pass, because <see cref="Skeleton"/> guarantees parents precede children — so a
    /// bone is in the subtree exactly when it IS the root or its parent already is. No recursion, no
    /// child lists, and the guarantee is checked in the skeleton's own constructor rather than
    /// assumed here.
    /// </para>
    /// <para>
    /// <b>The falloff is the question the deferral did not name.</b> A hard subtree boundary kinks at
    /// the waist: the spine is fully driven by the upper layer and the pelvis is not at all, so the
    /// join between them takes the whole discontinuity. Fading over a few bones UP the chain from the
    /// root spreads it. How many bones is not derivable from anything — it depends on the rig and on
    /// taste — so it is a parameter with no default but zero, and the tooling exists to find it.
    /// </para>
    /// </remarks>
    public static BoneMask Subtree(Skeleton skeleton, int rootBone, float weight = 1f, int falloff = 0)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        if (rootBone < 0 || rootBone >= skeleton.BoneCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rootBone), rootBone, $"This skeleton has {skeleton.BoneCount} bones.");
        }

        var clamped = Math.Clamp(weight, 0f, 1f);
        var w = new float[skeleton.BoneCount];
        w[rootBone] = clamped;

        for (var i = rootBone + 1; i < skeleton.BoneCount; i++)
        {
            var parent = skeleton.Bones[i].ParentIndex;
            if (parent >= 0 && w[parent] >= clamped) w[i] = clamped;
        }

        // Up the chain from the root, fading out. Step k of n gets 1 - k/(n+1), so a falloff of 2
        // hands the parent two thirds and the grandparent one third, and the bone beyond it zero.
        var ancestor = skeleton.Bones[rootBone].ParentIndex;
        for (var step = 1; step <= falloff && ancestor >= 0; step++)
        {
            w[ancestor] = clamped * (1f - (step / (float)(falloff + 1)));
            ancestor = skeleton.Bones[ancestor].ParentIndex;
        }

        return new BoneMask(w);
    }

    /// <summary>What this mask does not cover — the lower body, given the upper.</summary>
    /// <remarks>
    /// The complement rather than a second subtree, so the two halves of a body are guaranteed to sum
    /// to exactly one at every bone. Two independently-built masks are not.
    /// </remarks>
    public BoneMask Inverted()
    {
        var w = new float[weights.Length];
        for (var i = 0; i < weights.Length; i++) w[i] = 1f - weights[i];
        return new BoneMask(w);
    }

    /// <summary>How many bones this reaches at all — for a panel, and for a probe that judges a mask.</summary>
    public int Reach(float above = 0f)
    {
        var n = 0;
        foreach (var w in weights)
        {
            if (w > above) n++;
        }
        return n;
    }
}
