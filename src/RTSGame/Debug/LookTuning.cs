using Blix.Diagnostics;
using RTSGame.Simulation.Economy;

namespace RTSGame.Debug;

/// <summary>Which tonemap curve the present pass applies. Matches <c>Blix.Shaders/tonemap.glsl</c>.</summary>
internal enum TonemapCurve
{
    /// <summary>ACES filmic. Saturated shadows, smooth highlight rolloff, a warm bias.</summary>
    Aces,

    /// <summary>AgX. Filmic but more neutral than ACES; keeps hue better in bright colour.</summary>
    Agx,

    /// <summary>Reinhard. Flatter, gentler rolloff. For a scene that already has heavy contrast.</summary>
    Reinhard,

    /// <summary>None, just a clamp. The reference that shows where things are actually clipping.</summary>
    Neutral,
}

/// <summary>
/// Every dial that decides how the world looks, on a slider.
/// </summary>
/// <remarks>
/// <b>None of these can be settled by measurement, so none of them is a constant in a shader.</b> That is
/// the same rule the movement layer already follows and for the same reason: a number nobody can measure
/// should be visibly a question rather than quietly indistinguishable from an answer. The distinction is
/// worth being strict about here because it is unusually tempting not to be — a lighting constant looks
/// exactly like a physical one, and "sun intensity 2.05" reads as though somebody derived it.
/// <para>
/// So the shaders take these through the push constants rather than declaring them, which costs a couple
/// of vec4s per draw and buys a frame you can adjust while looking at it. The things that <em>are</em>
/// facts stay out: the shadow map's texel size and world extent are geometry, and the fit of a model to a
/// building's footprint is a fact about the simulation.
/// </para>
/// <para>
/// Settle a value here and it belongs in this file with a note saying what it was judged against — not
/// pushed back into the shader, because the next person to look at the scene will want the slider again.
/// </para>
/// </remarks>
internal sealed class LookSettings
{
    /// <summary>Height of the sun above the horizon, in degrees.</summary>
    /// <remarks>
    /// The one dial that decides whether a top-down camera can read a building's height at all: a shadow
    /// is as long as the thing is tall at 45°, twice as long at 27°, and hidden underneath it near the
    /// zenith. Low is dramatic and long-shadowed; high flattens everything.
    /// </remarks>
    [Tune(8.0, 88.0, Label = "sun elevation (deg)", Group = "sun")]
    public float SunElevationDegrees = 42f;

    /// <summary>Compass bearing the light comes from, in degrees.</summary>
    [Tune(0.0, 360.0, Label = "sun bearing (deg)", Group = "sun")]
    public float SunBearingDegrees = 37f;

    /// <summary>How bright the sun is, against an albedo of one.</summary>
    /// <remarks>
    /// Above one on purpose — the scene is rendered to a float target and tonemapped, so a lit surface is
    /// meant to sit above display range and be brought back down by the curve. Written straight to the
    /// swapchain these values would clip, which is the whole reason for having somewhere to put them.
    /// </remarks>
    [Tune(0.2, 8.0, Label = "sun intensity", Group = "sun")]
    public float SunIntensity = 3.0f;

    /// <summary>Multiplier on the sky-and-ground ambient, which is what fills the shadows.</summary>
    /// <remarks>
    /// This and <see cref="SunIntensity"/> together are the scene's contrast, and they trade against each
    /// other: the same apparent brightness with more ambient is a hazier, flatter day.
    /// </remarks>
    [Tune(0.0, 3.0, Label = "ambient", Group = "sun")]
    public float Ambient = 0.85f;

    /// <summary>How far past the terminator the light wraps, as a share of the full turn.</summary>
    /// <remarks>
    /// Straight N·L puts a hard line across a surface exactly where the sun grazes it, which on low-poly
    /// geometry lands on a facet boundary and reads as a crease. Wrapping softens the turn.
    /// <para>
    /// <b>And it is the single biggest flattener in the frame, which is not obvious.</b> At a quarter, a
    /// face turned ninety degrees from the sun still collects a fifth of it — so a wall in shade is lit,
    /// and every surface of a building lands within a stop of every other. Worse, it lands them all in
    /// the tonemap's shoulder, where the curve is flat by design: a face-on surface at 1.25 and a side
    /// face at 0.43 come out 0.78 and 0.42 — an output ratio under two to one on a light ratio of three.
    /// Wax, in other words, and turning this down is most of what "more dramatic" means.
    /// </para>
    /// </remarks>
    [Tune(0.0, 0.8, Label = "terminator wrap", Group = "sun")]
    public float TerminatorWrap = 0.08f;

    /// <summary>Width of a shadow's penumbra, in shadow-map texels.</summary>
    /// <remarks>
    /// <b>Softness is tap density, not kernel width.</b> The filter is sixteen taps whatever this says, so
    /// widening it spreads the same sixteen yes-or-no answers over more ground and they quantise into
    /// blotches — a shadow that is blurrier and dirtier at once. Narrow is clean.
    /// </remarks>
    [Tune(0.2, 6.0, Label = "shadow penumbra (texels)", Group = "shadows")]
    public float ShadowPenumbraTexels = 1.5f;

    /// <summary>How far off its own surface a fragment is moved before the shadow lookup, in texels.</summary>
    /// <remarks>
    /// The cure for acne — the broad dirty smears a flat surface casts on itself. Too little and they come
    /// back; too much and a shadow visibly parts company with the wall that cast it.
    /// </remarks>
    [Tune(0.0, 8.0, Label = "shadow normal offset (texels)", Group = "shadows")]
    public float ShadowNormalOffsetTexels = 2.5f;

    /// <summary>Side of the box the sun's shadow map covers, in metres.</summary>
    /// <remarks>
    /// Crispness against coverage, and the trade is direct: the map is a fixed 2048 texels, so halving
    /// this doubles the resolution of every shadow in it and halves how far from the camera a shadow
    /// exists at all.
    /// </remarks>
    [Tune(40.0, 400.0, Label = "shadow box (m)", Group = "shadows")]
    public float ShadowExtentMetres = 150f;

    /// <summary>How much the far distance washes out toward the sky.</summary>
    [Tune(0.0, 1.0, Label = "aerial perspective", Group = "air")]
    public float FogStrength = 0.72f;

    /// <summary>Where the wash begins, as a multiple of how far back the camera is standing.</summary>
    /// <remarks>
    /// A multiple rather than a distance, because what the fog is for is separating near ground from far
    /// ground, and how far away the far ground is depends on the zoom.
    /// </remarks>
    [Tune(0.2, 8.0, Label = "fog start (x zoom)", Group = "air")]
    public float FogStartZooms = 1.8f;

    [Tune(1.0, 30.0, Label = "fog end (x zoom)", Group = "air")]
    public float FogEndZooms = 7f;

    /// <summary>Stops of exposure applied before the tonemap curve.</summary>
    /// <remarks>
    /// Worth turning down rather than up, against instinct: everything lit in this scene lands above one
    /// and the curve's shoulder is up there, so lowering exposure pulls the frame back into the part of
    /// the curve that still has slope — which is where a difference in surface angle survives as a
    /// difference on screen.
    /// </remarks>
    [Tune(0.1, 4.0, Label = "exposure", Group = "grade")]
    public float Exposure = 0.85f;

    [Tune(Label = "tonemap", Group = "grade")]
    public TonemapCurve Tonemap = TonemapCurve.Aces;

    /// <summary>Saturation lift, applied in HDR before the curve.</summary>
    /// <remarks>
    /// Before rather than after, which is what the shared library asks for: a lift applied after
    /// tonemapping is a lift applied to values that have already been clipped. A greybox-adjacent scene
    /// wants some, because with no texture and no material variation every distinction it can make is
    /// carried by hue and value alone — and a filmic curve desaturates as it rolls off.
    /// </remarks>
    [Tune(0.0, 2.0, Label = "saturation", Group = "grade")]
    public float Saturation = 1.10f;

    [Tune(0.5, 1.8, Label = "contrast", Group = "grade")]
    public float Contrast = 1.05f;

    /// <summary>How far apart the two checker greens are, as a share of their brightness.</summary>
    /// <remarks>
    /// The checker is a scale reference: perceived speed on an untextured plane comes almost entirely from
    /// crossing edges, so a walking body on a flat colour looks like it is sliding. Turned up it is a
    /// chessboard, which is the single most conspicuous "this is a prototype" signal a frame can carry.
    /// </remarks>
    [Tune(0.0, 0.30, Label = "checker contrast", Group = "ground")]
    public float CheckerContrast = 0.03f;

    /// <summary>Random variation between neighbouring ground blocks, as a share of brightness.</summary>
    /// <remarks>
    /// A regular grid of two colours reads as tiling however faint it is, because the eye finds the
    /// period. The same faint contrast with the tiles individually varied reads as ground.
    /// </remarks>
    [Tune(0.0, 0.30, Label = "ground variation", Group = "ground")]
    public float GroundVariation = 0.06f;

    /// <summary>How far from what the camera is looking at trees are still drawn, in metres.</summary>
    /// <remarks>
    /// Not a look dial — a budget one, and it is here because it is judged the same way: turn it down
    /// until the forest visibly ends inside the view, then back up. A dense woodland is thousands of
    /// models and the camera can see about ninety metres of it, so this is the difference between a frame
    /// that fits and one that does not.
    /// </remarks>
    [Tune(40.0, 600.0, Label = "tree draw distance (m)", Group = "ground")]
    public float TreeDrawMetres = 150f;

    /// <summary>How much brighter tilled soil is drawn than the pack authored it.</summary>
    /// <remarks>
    /// <b>The field's stripes are a contrast problem, not an alignment one.</b> The plot and the crop line
    /// up now, and the fields still read as ribbons — because the pack's dirt is 0.09 linear and its wheat
    /// is 0.38, so a crop that covers its plot in rows puts four-to-one contrast between every row and the
    /// gap beside it, and twelve fields tiled edge to edge turn that into forty-metre stripes. Lifting the
    /// soil is the cheap half of the fix (the other half is that neighbouring plots now run crosswise), and
    /// it is a dial because the right amount is a judgement about the whole frame: too far and the fields
    /// stop being distinct from the grass they are cut out of.
    /// </remarks>
    [Tune(1.0, 3.0, Label = "tilled soil brightness", Group = "ground")]
    public float SoilBrightness = 1.85f;
}

/// <summary>
/// How solid a wood is: the two numbers that decide which ground a stand of trees closes.
/// </summary>
/// <remarks>
/// Sliders because there is no measurement that settles them. The <em>routing</em> consequence is
/// measured and hard — a gap narrower than 1.5 m is refused to every body, which is why individual trunks
/// cannot block — but how much of a wood should be wall and how much should be walkable fringe is a
/// question about what the map plays like, and the honest place for that is a dial next to the thing it
/// changes.
/// <para>
/// Changing either repaints the cover and rebuilds the navigation raster, which takes about a second on a
/// 600 m map with ten thousand trees. So it is applied a moment <em>after</em> the value stops moving
/// rather than on every frame of a drag — see <c>RtsGameLoop.ApplyWoodlandCover</c>. That is the only
/// reason these are not in <see cref="LookSettings"/>: a look dial is free to change and these are not.
/// </para>
/// </remarks>
internal sealed class WoodlandSettings
{
    /// <summary>Trees within the radius below that make a patch of ground impassable.</summary>
    /// <remarks>
    /// The lower this is the more solid a wood becomes. Two is nearly everything inside a stand; four
    /// leaves lanes through all but the thickest. It also decides whether the <em>near</em> band stays
    /// walkable — the thinned stragglers a settlement's first cutters work — because a band scattered at a
    /// spacing floor of <c>s</c> can never put three trees inside a circle of radius less than
    /// <c>s / sqrt(3)</c>, and if it does close, the cutters who start there have nothing they can reach.
    /// </remarks>
    [Tune(1.0, 8.0, Label = "trees that close ground", Group = "woodland")]
    public int CoverTrees
    {
        get => Woodland.CoverTrees;
        set => Woodland.CoverTrees = value;
    }

    /// <summary>How far a tree's crowding reaches, in metres.</summary>
    /// <remarks>
    /// Bigger makes a wood both more solid and <em>larger</em> than the trees that justify it, because the
    /// impassable mass swells past the trunks — past about a metre and a half over the scatter spacing it
    /// starts closing ground with no tree visibly on it, which reads as an invisible wall.
    /// </remarks>
    [Tune(1.0, 6.0, Label = "crowding reach (m)", Group = "woodland")]
    public float CoverRadius
    {
        get => Woodland.CoverRadius;
        set => Woodland.CoverRadius = value;
    }
}
