using Blix.Diagnostics;
using RTSGame.Rendering;
using RTSGame.Simulation;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Threat;

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
    /// <summary>
    /// Whether the light comes from the calendar and the sun's own cycle rather than from these dials.
    /// </summary>
    /// <remarks>
    /// <b>On, because a year that looks like one afternoon is the flattest thing about this scene.</b> The
    /// season is the most important state in this economy — a field's three windows, a winter's fuel, a
    /// harvest that arrives or does not — and the only thing that ever said what month it was, was a line of
    /// text. See <c>Rendering/Atmosphere.cs</c>.
    /// <para>
    /// Turning it off pins the sun where the two dials below put it, which is what you want when judging a
    /// material or a shadow rather than a mood: a moving sun makes two screenshots incomparable.
    /// </para>
    /// </remarks>
    [Tune(Label = "sun follows the year", Group = "sun")]
    public bool SunFollowsTheYear = true;

    /// <summary>How much the year is allowed to change the light, from a fixed look to full swing.</summary>
    [Tune(0.0, 1.0, Label = "seasonality", Group = "sun")]
    public float Seasonality = 1f;

    /// <summary>Seconds in one sun cycle: dawn, day, dusk, night.</summary>
    /// <remarks>
    /// Its own period rather than the calendar's day, which is twenty seconds because a day is the unit a
    /// ration is measured in. The dissonance is deliberate and named in <c>Atmosphere.DayLengthSeconds</c>.
    /// </remarks>
    [Tune(60.0, 1800.0, Label = "sun cycle (s)", Group = "sun")]
    public float DayLengthSeconds
    {
        get => Atmosphere.DayLengthSeconds;
        set => Atmosphere.DayLengthSeconds = value;
    }

    /// <summary>How far a plant leans at a metre above its own root, in metres.</summary>
    /// <remarks>
    /// <b>The amplitude of the only thing in this scene that moves without being told to.</b> Small on
    /// purpose and easy to overdo: at a tenth of a metre an eight-metre pine's crown travels about
    /// twenty-eight centimetres, which is a lean you notice only once you have seen it stop. The square
    /// root of height is doing the shape of it — see <c>Shaders/world.vert</c>.
    /// <para>
    /// Worth knowing before turning it up: the shadow casters do <em>not</em> lean. The sun's box is 150 m
    /// across a 2048 map, so a texel is 7 cm and the crown's travel at this amplitude is about four of
    /// them, softened again by the penumbra — a discrepancy under the resolution of the thing that would
    /// show it. Past about 0.25 that argument stops holding and the shadows want the same displacement,
    /// which means the caster's push constants growing to carry the wind.
    /// </para>
    /// </remarks>
    [Tune(0.0, 0.4, Label = "wind sway (m)", Group = "wind")]
    public float WindSway = 0.045f;

    /// <summary>How fast the gusts come, in radians a simulated second.</summary>
    [Tune(0.0, 2.0, Label = "gust rate", Group = "wind")]
    public float WindGustRate = 0.31f;

    /// <summary>How brightly the fire itself shows, for the few metres at which it can be seen at all.</summary>
    /// <remarks>
    /// <b>What this is <em>not</em> any more is the load-bearing part.</b> It was a lit pane on a wall, and
    /// it was reported as modern lighting twice — lowering it and re-tinting it did not help, because a flat
    /// quad of even brightness is a lamp whatever colour it is. It is now a spark a few centimetres across:
    /// visible from the observational camera as a fire in a doorway, sub-pixel from strategic height, and
    /// incapable of reading as a fixture at either. The light you actually see at a distance is
    /// <see cref="HearthSpill"/>.
    /// </remarks>
    [Tune(0.0, 8.0, Label = "hearth spark", Group = "night")]
    public float HearthSpark = 1.0f;

    /// <summary>How much light the settlement's fires put on the ground and the walls around them.</summary>
    /// <remarks>
    /// <b>This is the one that does the work</b>, now that there is no lit pane to look at: a warm patch on
    /// the ground outside a doorway, with no edge, no orientation and no opinion about which wall the door
    /// is in — which is why it can be right about all three where a quad could not be right about any.
    /// Turning it off leaves only the sparks, which is what you want when judging the night palette itself.
    /// </remarks>
    [Tune(0.0, 6.0, Label = "hearth spill", Group = "night")]
    public float HearthSpill = 2.4f;

    /// <summary>How far a hearth's light carries, in metres.</summary>
    /// <remarks>
    /// A hard cutoff rather than a fade to nothing, and it is what makes a bounded set of lights safe: a
    /// fire dropped from the nearest twelve was already contributing nothing at the distance it was
    /// dropped. It is also a look — a short reach is a hearth in a doorway and a long one is a bonfire in
    /// the square.
    /// </remarks>
    [Tune(2.0, 30.0, Label = "hearth reach (m)", Group = "night")]
    public float HearthReach = 11f;

    /// <summary>How hard the chimneys smoke, over what the season already asked for.</summary>
    /// <remarks>
    /// Zero is off, which is worth having: smoke is the only thing in the frame that can hide a building,
    /// and a judgement about a roof wants it out of the way. The season and the hour decide the rest — see
    /// <c>Rendering/Hearths.cs</c>, where a winter morning is the smokiest thing in the game.
    /// </remarks>
    [Tune(0.0, 2.0, Label = "chimney smoke", Group = "wind")]
    public float SmokeDensity = 1f;

    /// <summary>How fast smoke is carried downwind, in metres a simulated second.</summary>
    [Tune(0.0, 3.0, Label = "smoke drift (m/s)", Group = "wind")]
    public float SmokeDrift = 0.75f;

    /// <summary>Which way the wind is going, in degrees.</summary>
    /// <remarks>
    /// Shared by the trees and the smoke, which is the point of having it at all: a plume crossing a wood
    /// that leans the other way is two effects rather than one piece of weather.
    /// </remarks>
    [Tune(0.0, 360.0, Label = "wind bearing (deg)", Group = "wind")]
    public float WindBearingDegrees = 115f;

    /// <summary>How much saturation a high sun takes out of the frame.</summary>
    /// <remarks>
    /// A proxy onto <see cref="Atmosphere.MiddaySaturationDrop"/>, which is where the argument for it is
    /// written. On a slider because "how much is too colourful" is a judgement and not a measurement — and
    /// because at zero you can see what was being complained about.
    /// </remarks>
    [Tune(0.0, 0.5, Label = "midday saturation drop", Group = "sun")]
    public float MiddaySaturationDrop
    {
        get => Atmosphere.MiddaySaturationDrop;
        set => Atmosphere.MiddaySaturationDrop = value;
    }

    /// <summary>How much chroma a high sun takes out of green specifically.</summary>
    [Tune(0.0, 0.6, Label = "midday green drop", Group = "sun")]
    public float MiddayGreenDrop
    {
        get => Atmosphere.MiddayGreenDrop;
        set => Atmosphere.MiddayGreenDrop = value;
    }

    /// <summary>Multipliers over whatever the season asked for, for taste.</summary>
    [Tune(0.2, 3.0, Label = "sun scale", Group = "sun")]
    public float SunScale = 1f;

    [Tune(0.2, 3.0, Label = "ambient scale", Group = "sun")]
    public float AmbientScale = 1f;

    [Tune(8.0, 88.0, Label = "sun elevation (deg)", Group = "sun")]
    public float SunElevationDegrees = 22f;

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
    // Wider than it was, because it can be now. The old kernel's banding got worse the further it spread,
    // so 1.5 was a compromise with the artefact rather than a choice about softness; with the disc rotated
    // per pixel the structure is gone and the only thing width costs is a slightly softer edge, which is
    // the thing we were after.
    public float ShadowPenumbraTexels = 3.2f;

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
    public float ShadowFloorMetres = 60f;

    /// <summary>
    /// How far outside the view the sun's box reaches, for casters that are not on screen, in metres.
    /// </summary>
    /// <remarks>
    /// A shadow is <c>1 / tan(elevation)</c> times its caster's height, so at 42° it is 1.11× — and the
    /// tallest thing in the settlement is a tree at about six metres. Eight covers it. Turn it up if
    /// something ever casts from off screen and its shadow is clipped at the edge of the view; turn it down
    /// and every texel that buys goes back into sharpness.
    /// </remarks>
    [Tune(0.0, 60.0, Label = "shadow margin (m)", Group = "sun")]
    public float ShadowMarginMetres = 8f;

    /// <summary>The tallest thing expected to cast a shadow, in metres.</summary>
    /// <remarks>
    /// <b>Not a look dial — the term that keeps the shadow margin honest when the sun moves.</b> A shadow is
    /// <c>height / tan(elevation)</c> long, so the eight metres of margin that covered a six-metre tree at
    /// 42° covers only a two-and-a-bit-metre one at 22°, and dropping the sun would have silently clipped
    /// every tree shadow reaching in from off screen. The margin is floored by this instead, so lowering the
    /// sun widens the box by itself.
    /// </remarks>
    [Tune(1.0, 20.0, Label = "tallest caster (m)", Group = "sun")]
    public float TallestCasterMetres = 7f;

    /// <summary>How much colour distance takes out of the world, from none to all of it.</summary>
    /// <remarks>
    /// <b>Aerial perspective, and it is the half of haze that was missing.</b> Fog was a straight mix toward
    /// one colour, which fades a scene evenly and flattens it: a distant forest went pale and stayed just as
    /// green. Air scatters short wavelengths and takes <em>saturation</em> before it takes value, which is
    /// why a far hillside reads as grey-blue rather than as bright green seen through milk — and why doing
    /// this makes a settlement pop out of its own landscape without touching the settlement.
    /// </remarks>
    [Tune(0.0, 1.0, Label = "distance drains colour", Group = "sun")]
    public float HazeDesaturation = 0.30f;

    /// <summary>
    /// How much the haze warms when looking toward the sun, from none to fully.
    /// </summary>
    /// <remarks>
    /// The cheapest half of real atmospheric scattering: air lit from behind glows, air lit from in front
    /// stays cold. One dot product between the view ray and the sun, and it turns a single fog colour into
    /// a sky that has a direction in it — which at a low sun is most of what makes the light feel like a
    /// time of day rather than a setting.
    /// </remarks>
    [Tune(0.0, 1.0, Label = "haze glows toward the sun", Group = "sun")]
    public float HazeSunGlow = 0.22f;

    /// <summary>How hard the ground wears where somebody stands, per second.</summary>
    /// <remarks>
    /// A body standing still for <c>1 / gain</c> seconds wears its patch fully. At 0.02 that is fifty
    /// seconds, which is about a shift at a field — so a worked field wears its own edges within a season
    /// and a road wears where carts actually run, while somebody merely walking past leaves almost nothing.
    /// </remarks>
    [Tune(0.0, 0.2, Label = "ground wears (per s)", Group = "ground")]
    public float WearGain = 0.02f;

    /// <summary>How fast grass grows back, as a rate per second.</summary>
    /// <remarks>
    /// The information is in the <em>contrast</em> between where people go and where they used to, so
    /// without this a settlement ends its first year uniformly trodden and says nothing. At 0.004 a
    /// disused path is half gone in about three minutes of simulated time.
    /// </remarks>
    [Tune(0.0, 0.05, Label = "grass grows back (per s)", Group = "ground")]
    public float WearFadeRate = 0.004f;

    /// <summary>How much a worn patch changes the ground it is on, from nothing to bare earth.</summary>
    [Tune(0.0, 1.0, Label = "wear shows", Group = "ground")]
    public float WearStrength = 0.75f;

    /// <summary>How much the far distance washes out toward the sky.</summary>
    [Tune(0.0, 1.0, Label = "aerial perspective", Group = "air")]
    public float FogStrength = 0.20f;

    /// <summary>Where the haze begins, as a share of the detail radius.</summary>
    /// <remarks>
    /// <b>A share rather than a multiple of the zoom, because haze exists to hide the edge of detail.</b> It
    /// was a multiple of the camera's standoff and therefore unrelated to the thing it was hiding: at the
    /// widest zoom the haze began four hundred metres past a tree cull at two hundred, so the world ended in
    /// a hard circle in plain sight. It reaches full strength at the detail radius by construction — there is
    /// no separate end, because the end is where detail stops.
    /// </remarks>
    [Tune(0.05, 0.95, Label = "haze starts at", Group = "sun")]
    public float FogStartShare = 0.52f;

    /// <summary>Stops of exposure applied before the tonemap curve.</summary>
    /// <remarks>
    /// Worth turning down rather than up, against instinct: everything lit in this scene lands above one
    /// and the curve's shoulder is up there, so lowering exposure pulls the frame back into the part of
    /// the curve that still has slope — which is where a difference in surface angle survives as a
    /// difference on screen.
    /// </remarks>
    [Tune(0.1, 4.0, Label = "exposure", Group = "grade")]
    public float Exposure = 0.85f;

    /// <summary>How far the eye is allowed to go colourblind in the dark, from not at all to fully.</summary>
    /// <remarks>
    /// The Purkinje shift, applied per pixel in the present pass — see <c>blix_scotopic</c>. It is a
    /// correction to the <em>observer</em> rather than to the light: rods have no colour and peak further
    /// into the blue, which is why a moonlit field reads blue-grey under light that is nearly white, and why
    /// deep shadow at noon reads blue too. Turning it off gives a night that is merely dim, which is what a
    /// renderer does and not what seeing does.
    /// </remarks>
    [Tune(0.0, 1.0, Label = "night vision (Purkinje)", Group = "grade")]
    public float ScotopicShift = 0.8f;

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

    // <b>The checker and the per-block variation are gone with the ground they described.</b> Both were
    // properties of a ground made of one flat plate every few metres: the checker gave an untextured plane
    // the crossing edges that make a walking body look like it is walking, and the variation stopped the
    // checker reading as tiling. Ground meshed from the height field has its own edges and its own shading,
    // and §52's macro colour variation in the world shader does the rest — so these two were describing a
    // representation that no longer exists.

    /// <summary>
    /// Trees sharing a ten-metre cell before one of them drops to the middle level of detail.
    /// </summary>
    /// <remarks>
    /// Crowding, not distance, is what picks a tree's level of detail — see the remarks on
    /// <c>RtsGameLoop.RebuildCanopyDensity</c> for the two distance ladders that were built and taken back
    /// out. This is the threshold where a copse becomes texture: below it a tree keeps every triangle
    /// however far away it is.
    /// </remarks>
    [Tune(1.0, 12.0, Label = "tree crowd \u2192 mid", Group = "ground")]
    public float TreeCrowdMid = 3f;

    /// <summary>
    /// Trees sharing a ten-metre cell before one of them drops to the coarsest level of detail.
    /// </summary>
    /// <remarks>
    /// The coarse level sheds interior leaf cards, so it only holds up where the neighbours fill the mass
    /// back in. That is what this threshold is: thick enough that nobody can tell which trunk is which.
    /// </remarks>
    [Tune(2.0, 20.0, Label = "tree crowd \u2192 far", Group = "ground")]
    public float TreeCrowdFar = 6f;

    /// <summary>How far from what the camera is looking at trees are still drawn, in metres.</summary>
    /// <remarks>
    /// Not a look dial — a budget one, and it is here because it is judged the same way: turn it down
    /// until the forest visibly ends inside the view, then back up. A dense woodland is thousands of
    /// models and the camera can see about ninety metres of it, so this is the difference between a frame
    /// that fits and one that does not.
    /// </remarks>
    /// <remarks>
    /// <b>Retired as a distance and kept as a ceiling.</b> A tree matters if it or its shadow can be seen,
    /// and the sun's box already answers both, so the draw radius is the box — see the note beside
    /// <c>treeDrawRadius</c>. What is still worth having is a hard stop, because pulling the camera all the
    /// way out on a dense map is thousands of models and the frame is allowed to give up before the
    /// simulation does.
    /// </remarks>
    /// <remarks>
    /// <b>Expressed as texels rather than metres, because that is what the cap protects.</b> The sun's box is
    /// twice the detail radius across and the shadow map is a fixed number of texels wide, so "how far may the
    /// box reach" and "how coarse may a texel be" are one statement — and only the second stays true if the
    /// map's resolution ever changes. 23.4 cm at 2,048 texels is the 240 m ceiling this replaces, to the metre.
    /// </remarks>
    /// <summary>Draws every tree on the map, bypassing both the distance bound and the frustum test.</summary>
    /// <remarks>
    /// <b>An A/B switch for "is culling doing this", because four rounds of reasoning have not settled it.</b>
    /// With it on, nothing about visibility is decided by this file — every tree the simulation has is submitted.
    /// If what you are looking at does not change, the culling is innocent and the answer is elsewhere: correct
    /// perspective as the camera descends, the ground under them, or the models themselves.
    /// <para>
    /// Expensive by design and not a setting to leave on: on a wooded map that is thirty thousand trees.
    /// </para>
    /// </remarks>
    [Tune(Label = "draw every tree", Group = "ground")]
    public bool DrawEveryTree = false;

    [Tune(6.0, 60.0, Label = "coarsest shadow texel (cm)", Group = "ground")]
    public float CoarsestShadowTexelCentimetres = 23.4f;

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

/// <summary>
/// The settlement's own dials: how long a transfer takes, and how a defence makes up its mind.
/// </summary>
/// <remarks>
/// Same proxy pattern as <see cref="WoodlandSettings"/> — the numbers live where they are used and this
/// exposes them, so nothing is duplicated and a headless run reads the same constants a watched one does.
/// These are here because none of them is derivable from another and every one of them changes how the
/// game <em>feels</em> rather than only how it performs, which is the test for whether something earns a
/// slider.
/// </remarks>
internal sealed class SettlementSettings
{
    /// <summary>Seconds a body spends handing a load over or picking one up.</summary>
    /// <remarks>
    /// <b>Very nearly nothing, by decision, and it was four seconds.</b> The argument for four was real —
    /// a hauling network with instant transfer has no reason to want more haulers than routes, and the
    /// queue at a busy granary is one of the things the congestion field exists to price — and it lost to
    /// the thing that matters more: it reads as villagers standing about. Every transfer in the settlement
    /// pays it, several times per round trip, and watching a settlement is watching people fetch and carry.
    /// Turn it back up and the queues come back.
    /// </remarks>
    [Tune(0.0, 8.0, Label = "handover (s)", Group = "settlement")]
    public float HandoverSeconds
    {
        get => EconomySystem.HandoverSeconds;
        set => EconomySystem.HandoverSeconds = value;
    }

    /// <summary>How much stronger than the assailants a defence wants to be before it stands.</summary>
    [Tune(1.0, 3.0, Label = "defence margin", Group = "settlement")]
    public float StandMargin
    {
        get => ThreatSystem.StandMargin;
        set => ThreatSystem.StandMargin = value;
    }

    /// <summary>Seconds a defender may be away and still count toward whether the fight is winnable.</summary>
    /// <remarks>
    /// The width of a defence, in effect: longer gathers a bigger party from further away and gets it
    /// there later, which is the trade the whole three-question decision is built around.
    /// </remarks>
    [Tune(2.0, 30.0, Label = "rally window (s)", Group = "settlement")]
    public float RallySeconds
    {
        get => ThreatSystem.RallySeconds;
        set => ThreatSystem.RallySeconds = value;
    }

    /// <summary>How near a hostile has to be to something to be threatening it, in metres.</summary>
    [Tune(4.0, 40.0, Label = "threat reach (m)", Group = "settlement")]
    public float ThreatMetres
    {
        get => ThreatSystem.ThreatMetres;
        set => ThreatSystem.ThreatMetres = value;
    }

    /// <summary>How far a body will carry a load to put it somewhere safe, in metres.</summary>
    [Tune(0.0, 120.0, Label = "carry a load to safety (m)", Group = "settlement")]
    public float HavenMetres
    {
        get => SimulationWorld.HavenMetres;
        set => SimulationWorld.HavenMetres = value;
    }
}
