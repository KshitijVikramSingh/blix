namespace Blix;

/// <summary>How a <see cref="PoseLayer"/> applies onto the layers below it.</summary>
public enum PoseLayerMode
{
    /// <summary>Interpolate the result so far toward this layer's pose by its weight (<see cref="PoseBlend.Lerp(Pose, Pose, float, Pose)"/>). At weight 1 the layer replaces what is below it.</summary>
    Blend,

    /// <summary>Add this layer's offset from rest onto the result so far, scaled by its weight (<see cref="PoseDelta.LayerOnto"/>).</summary>
    Additive,
}

/// <summary>What a layer does when its one-shot clip has finished.</summary>
public enum PoseLayerFinish
{
    /// <summary>Stay, holding the clip's last frame. The default: what happens next is the caller's to choose.</summary>
    Hold,

    /// <summary>Leave the stack on the advance it finished.</summary>
    Remove,
}

/// <summary>One layer of a <see cref="PoseStack"/>: a source, how it applies, how much, and over which bones.</summary>
/// <remarks>
/// Weights only, as animation stage D decided: a crossfade is the caller moving <see cref="Weight"/>, and
/// a layer knows nothing of states, transitions or durations.
/// </remarks>
public sealed class PoseLayer
{
    internal PoseLayer(ClipPlayer source, PoseLayerMode mode, float weight, BoneMask? mask)
    {
        Source = source;
        Mode = mode;
        Weight = weight;
        Mask = mask;
    }

    /// <summary>The player whose pose this layer applies. It belongs to this layer: the stack advances it.</summary>
    public ClipPlayer Source { get; }

    public PoseLayerMode Mode { get; set; }

    /// <summary>How strongly the layer applies, clamped to [0, 1] when evaluated.</summary>
    public float Weight { get; set; }

    /// <summary>A per-bone weight multiplied into <see cref="Weight"/>; null reaches every bone.</summary>
    public BoneMask? Mask { get; set; }

    /// <summary>Hold the last frame (the default) or leave the stack once a one-shot clip finishes.</summary>
    public PoseLayerFinish OnFinish { get; set; } = PoseLayerFinish.Hold;
}

/// <summary>
/// An ordered stack of pose layers: each applies onto the result of the layers below it, starting from
/// the rest pose. A walk at the bottom and a masked swing above it is two layers.
/// </summary>
/// <remarks>
/// <para>
/// <b>A flat list, not a blend tree.</b> Each layer is a source, a mode, a weight and an optional mask,
/// applied in order; there are no nodes, parameters, states, transitions or durations. The maths is
/// <see cref="PoseBlend"/>'s and <see cref="PoseDelta"/>'s; what this owns is the order, the rest pose they
/// start from, and advancing every layer's player on one delta.
/// </para>
/// <para>
/// <b>Separate from <see cref="AnimationHost"/>,</b> because pose layers have an order a host does not:
/// each applies onto the previous result. It is an <see cref="IAnimation"/>, so a stack can sit inside a
/// host as one entry; it never finishes on its own.
/// </para>
/// <para>
/// Root motion is the caller's to take from whichever layer drives the body (<see cref="ClipPlayer.RootDelta"/>),
/// and the palette is built from <see cref="Pose"/> by whoever draws.
/// </para>
/// </remarks>
public sealed class PoseStack : IAnimation
{
    private readonly List<PoseLayer> layers = new();

    public PoseStack(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        Skeleton = skeleton;
        RestPose = skeleton.CreateRestPose();
        Pose = skeleton.CreateRestPose();
    }

    /// <summary>The skeleton every layer's player poses.</summary>
    public Skeleton Skeleton { get; }

    /// <summary>What evaluation starts from, and what additive layers are offsets from. Never mutated by this type.</summary>
    public Pose RestPose { get; }

    /// <summary>The composed pose, rewritten by every <see cref="Evaluate"/>.</summary>
    public Pose Pose { get; }

    /// <summary>The layers, bottom first.</summary>
    public IReadOnlyList<PoseLayer> Layers => layers;

    /// <summary>Adds a layer on top.</summary>
    /// <exception cref="ArgumentException">The player poses a different <see cref="Skeleton"/> instance, or is already a layer's source: the stack advances each player once per advance.</exception>
    public PoseLayer Add(ClipPlayer source, PoseLayerMode mode = PoseLayerMode.Blend, float weight = 1f, BoneMask? mask = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        // The same instance, not the same count: locals are applied by index, and two rigs of 60 bones each
        // share a shape, not a meaning. Bone 12 of one is not bone 12 of the other.
        if (!ReferenceEquals(source.Skeleton, Skeleton))
        {
            throw new ArgumentException(
                $"the player poses another skeleton ({source.Skeleton.BoneCount} bones); a layer's player must pose this stack's own.", nameof(source));
        }

        if (layers.Any(l => ReferenceEquals(l.Source, source)))
        {
            throw new ArgumentException(
                "the player is already a layer's source; two layers sharing one would advance it twice per advance.", nameof(source));
        }

        if (mask is not null && mask.BoneCount != Skeleton.BoneCount)
        {
            throw new ArgumentException(
                $"the mask covers {mask.BoneCount} bones and this stack's skeleton has {Skeleton.BoneCount}.", nameof(mask));
        }

        var layer = new PoseLayer(source, mode, weight, mask);
        layers.Add(layer);
        return layer;
    }

    /// <summary>Takes a layer out; false if it was not in the stack.</summary>
    public bool Remove(PoseLayer layer) => layers.Remove(layer);

    /// <summary>Advances every layer's player by <paramref name="delta"/>, drops finished layers set to leave, and evaluates.</summary>
    /// <returns>True: a stack does not finish on its own.</returns>
    public bool Advance(double delta)
    {
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            var running = layer.Source.Advance(delta);
            if (!running && layer.Source.Finished && layer.OnFinish == PoseLayerFinish.Remove) layers.RemoveAt(i);
        }

        Evaluate();
        return true;
    }

    /// <summary>Composes <see cref="Pose"/> from the layers as they stand, without moving any clock — after a weight, mask or mode change.</summary>
    public void Evaluate()
    {
        Pose.CopyFrom(RestPose);
        foreach (var layer in layers)
        {
            var weight = Math.Clamp(layer.Weight, 0f, 1f);
            var source = layer.Source.Pose;
            switch (layer.Mode)
            {
                case PoseLayerMode.Blend when layer.Mask is null && weight >= 1f:
                    // The whole layer replaces what is below it: a copy, exact by construction.
                    Pose.CopyFrom(source);
                    break;

                case PoseLayerMode.Blend when layer.Mask is null:
                    if (weight > 0f) PoseBlend.Lerp(Pose, source, weight, Pose);
                    break;

                case PoseLayerMode.Blend:
                    PoseBlend.Lerp(Pose, source, weight, layer.Mask!, Pose);
                    break;

                default:
                    for (var i = 0; i < Pose.BoneCount; i++)
                    {
                        var w = layer.Mask is { } mask ? weight * mask[i] : weight;
                        if (w <= 0f) continue;
                        Pose.Locals[i] = PoseDelta.LayerOnto(Pose.Locals[i], RestPose.Locals[i], source.Locals[i], w);
                    }

                    break;
            }
        }
    }
}
