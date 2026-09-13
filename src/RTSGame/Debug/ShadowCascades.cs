using System.Numerics;
using Blix.Diagnostics;
using Blix.Graphics;

namespace RTSGame.Debug;

/// <summary>
/// Where the camera frustum is split for cascaded sun shadows, and the boxes fitted to each slice.
/// </summary>
/// <remarks>
/// <b>The instrument before the feature, deliberately.</b> One fitted box already replaced a radius around the
/// focus, and finding out whether it was right took four rounds of screenshots — a box that is wrong is
/// invisible except as an absence of shadow somewhere, which is exactly the kind of bug this session spent a day
/// on. Three boxes will be three times as hard to reason about and no harder to look at, so the gizmos and the
/// splits come first and the cascades are ported into a view that can already show them.
/// <para>
/// Splits are tunable rather than derived, to begin with. Derived is where this should end up — every other draw
/// distance in this loop is now a share of one measured reach, and leaving the cascade splits as free numbers
/// would reintroduce in one subsystem exactly what that work removed. But a derivation is a guess until the
/// thing it feeds can be seen, and these can be dragged while looking at the result.
/// </para>
/// <para>
/// Held as fractions of the shadowed depth rather than as metres, so a split means the same thing at every zoom
/// — which is the property the metre-valued distances kept failing to have.
/// </para>
/// </remarks>
internal sealed class ShadowCascades
{
    /// <summary>How many slices the shadowed depth is cut into.</summary>
    /// <remarks>
    /// Three, matching the working implementation in <c>SponzaLoop.Shadows.cs</c> this is ported from. Fixed
    /// rather than tunable because a count change is a change to the shader's cascade select and to how many
    /// passes the graph declares — not something a slider can honestly offer.
    /// <para>
    /// <b>Not a number that can be changed here alone.</b> world.frag declares three samplers and unrolls
    /// three containment tests by name, because a push-constant block cannot hold an array of mat4 and a
    /// sampler array would need a different descriptor layout. So this constant and those declarations are one
    /// fact with two owners — the recurring finding in this file's costume — and RtsGameLoop asserts they agree
    /// at construction rather than leaving the mismatch to surface as a cascade that is never sampled.
    /// </para>
    /// </remarks>
    public const int Count = 3;

    /// <summary>Where the first cascade ends, as a fraction of the shadowed depth.</summary>
    /// <remarks>
    /// <b>Near a third, now that the band is the band that holds ground.</b> While this was a fraction of the
    /// whole frustum, 0.12 was right for the wrong reason: it was the only value that put cascade 0 anywhere
    /// near the ground at all. Over a band that starts at the nearest visible ground, even thirds are the
    /// honest default and the near cascade is still the sharpest, because the band's near end is narrower on
    /// screen — a slice of frustum a fixed depth deep is a smaller box the closer it is.
    /// </remarks>
    [Tune(0.02, 0.9, Label = "cascade 0 ends at", Group = "shadows")]
    public float FirstSplit = 0.30f;

    /// <summary>Where the second cascade ends, as a fraction of the shadowed depth.</summary>
    [Tune(0.1, 0.95, Label = "cascade 1 ends at", Group = "shadows")]
    public float SecondSplit = 0.62f;

    /// <summary>Draws each cascade's fitted box, and the slice of camera frustum it was fitted to.</summary>
    /// <remarks>
    /// Both, because the failure this is for is the two disagreeing: a box that does not contain its slice is
    /// the bug, and either one alone looks reasonable.
    /// </remarks>
    [Tune(Label = "show cascade boxes", Group = "shadows")]
    public bool ShowBoxes = false;

    /// <summary>Tints every lit surface by the cascade that shaded it.</summary>
    /// <remarks>
    /// <b>The boxes show where the maps are; this shows which one each pixel actually read.</b> Those are
    /// different claims, and the gap between them is where a cascaded map goes wrong: the fit can be perfect
    /// and the select can still miss, in which case a fragment falls through to the next box out and loses
    /// sharpness, or through all three and is drawn in flat light. Both failures look like nothing at all
    /// until the frame is painted red, green and blue — and then the near band, the middle and the far read
    /// off the screen at a glance, along with any grey where no cascade claimed the ground.
    /// </remarks>
    [Tune(Label = "tint by cascade", Group = "shadows")]
    public bool ShowSelection = false;

    /// <summary>The near and far distance of each cascade, in metres, across the band that holds ground.</summary>
    /// <remarks>
    /// <b>Fractions of the band, not of the frustum.</b> The first version took one depth and split from zero,
    /// which is what a first-person camera wants; this camera stands well back from everything it looks at, so
    /// the near two thirds of its frustum is empty air and two of three cascades landed in it. See
    /// RtsGameLoop.GroundBand — the tint view found this immediately, which is what it was built for.
    /// <para>
    /// Sorted and separated on the way out rather than clamped on the way in, so dragging one slider past
    /// another cannot produce a cascade with negative depth — the sliders are independent and a user is entitled
    /// to drag them into any order.
    /// </para>
    /// </remarks>
    public void Slices(float nearDepth, float farDepth, Span<float> edges)
    {
        var depth = MathF.Max(1f, farDepth - nearDepth);
        var first = Math.Clamp(FirstSplit, 0.02f, 0.9f);
        var second = Math.Clamp(MathF.Max(SecondSplit, first + 0.02f), 0.04f, 0.95f);
        // <b>The near cascade is fitted from the camera, not from the band, and this is nearly free.</b> A
        // frustum slice's bounding sphere is dominated by its far cross-section — fitting 0.5 to 141 m gives a
        // 238 m box where 80 to 141 m gives 226 m, five per cent for covering every metre in front of the
        // camera. Fitted from the band instead, ground nearer than the band goes in no box at all, and a hill
        // rising toward the eye is exactly that: an unshadowed strip along the bottom of the screen.
        //
        // So the band decides where the *interior* splits fall, which was always the point of it, and the near
        // edge stays at the camera.
        edges[0] = 0.5f;
        edges[1] = nearDepth + depth * first;
        edges[2] = nearDepth + depth * second;
        edges[3] = nearDepth + depth;
    }

    /// <summary>A colour per cascade, so a box and the shadows it produced can be told apart by eye.</summary>
    public static GraphicsColor TintOf(int cascade) => cascade switch
    {
        0 => new GraphicsColor(1f, 0.35f, 0.35f, 0.85f),
        1 => new GraphicsColor(0.35f, 1f, 0.4f, 0.85f),
        _ => new GraphicsColor(0.4f, 0.55f, 1f, 0.85f),
    };
}
