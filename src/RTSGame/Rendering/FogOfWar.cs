using System.Diagnostics;
using System.Numerics;
using Blix.Diagnostics;
using RTSGame.Simulation;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;

namespace RTSGame.Rendering;

/// <summary>How much of a piece of ground the player is entitled to be shown.</summary>
/// <remarks>
/// Three states rather than two, because "have I ever been here" and "can I see it now" are different
/// questions with different answers and different consumers. A static thing is shown on the strength of the
/// first; a moving one only on the strength of the second.
/// </remarks>
internal enum FogTier
{
    /// <summary>Never scouted.</summary>
    Unexplored,

    /// <summary>Scouted once, not currently watched. What is drawn here is a memory.</summary>
    Remembered,

    /// <summary>Watched right now, by a body or by a building.</summary>
    Visible,
}

/// <summary>The dials on the fog: who sees how far, and how much work a frame may spend finding out.</summary>
internal sealed class FogSettings
{
    /// <summary>Whether anything is revealed at all, or the whole map is treated as watched.</summary>
    /// <remarks>
    /// Off by default while this is an instrument. Fog changes what every other subsystem is allowed to
    /// draw, so it goes in behind a switch that can be thrown back — and the switch has a second job, which
    /// is the verification that fog is genuinely outside the simulation: the determinism fingerprint and
    /// <c>--years</c> must produce identical results with it on and off, and that is only checkable if both
    /// are reachable from one build.
    /// </remarks>
    [Tune(Label = "fog of war", Group = "fog")]
    public bool Enabled = false;

    /// <summary>Paints every fog cell in the colour of its tier.</summary>
    /// <remarks>
    /// <b>The instrument, and it comes before anything reads the masks.</b> §73's lesson from the cascades,
    /// which cost four rounds of screenshots before a debug tint answered the question in one: a spatial
    /// field that is wrong is invisible except as an absence somewhere, and an absence is exactly what the
    /// eye is worst at. Fog is a worse case than the cascades were, because a fog bug and correct fog look
    /// the same from the chair — dark ground is what both of them produce.
    /// </remarks>
    [Tune(Label = "show fog cells", Group = "fog")]
    public bool ShowCells = false;

    /// <summary>How far a granary watches, in metres.</summary>
    /// <remarks>
    /// §7's detection radius at the settlement, reused rather than derived — and flagged as reused, because
    /// that number was chosen for the threat layer's question ("how much warning does a defence get") and
    /// this is a different question ("how much of the map is lit"). They may want to diverge. What makes a
    /// building's reach matter more than a body's is arithmetic: a villager sees 22 m on a 600 m map, so
    /// unit vision alone is a keyhole and the settlement is what makes the middle distance legible.
    /// </remarks>
    [Tune(10.0, 260.0, Label = "granary sight (m)", Group = "fog")]
    public float GranarySightMetres = 105f;

    /// <summary>How far a forward depot watches, in metres.</summary>
    /// <remarks>§7's outpost figure. An outpost's whole purpose is to see, so it sees furthest.</remarks>
    [Tune(10.0, 320.0, Label = "depot sight (m)", Group = "fog")]
    public float DepotSightMetres = 190f;

    /// <summary>How far a house or a field watches, in metres.</summary>
    /// <remarks>
    /// Not from §7, which has nothing to say about a house. Between a body's 22 m and the granary's 105:
    /// enough that a settlement's built area is continuously lit rather than reading as a row of separate
    /// lamps, which is the artefact a too-small figure here produces.
    /// </remarks>
    [Tune(5.0, 160.0, Label = "dwelling sight (m)", Group = "fog")]
    public float DwellingSightMetres = 45f;

    /// <summary>How many watchers get their view recomputed each frame.</summary>
    /// <remarks>
    /// <b>The budget that keeps this off the frame's critical path, and it is a real constraint rather than
    /// caution.</b> <see cref="SimulationWorld.CanSee"/> marches the ray at the navigation raster's half
    /// metre, so one granary asking about every cell in its 105 m reach is some three hundred and fifty
    /// cells at up to two hundred samples each — around thirty-five thousand terrain lookups for one
    /// building. Refreshing every watcher every frame would put a millisecond-scale spike in a phase this
    /// session just spent an evening measuring at 11 ms.
    /// <para>
    /// So watchers are refreshed round-robin, a few per frame, and the masks are at most one cycle stale.
    /// That is invisible for exploration — a cycle is a fraction of a second and a body walks under a metre
    /// in it, against a ten metre cell — and it is what lets fog share the sim's own sight predicate rather
    /// than approximating it with something cheaper. Sharing the predicate is the point; a cheaper copy is
    /// the drift bug.
    /// </para>
    /// </remarks>
    [Tune(1, 24, Label = "watchers per frame", Group = "fog")]
    public int WatchersPerFrame = 4;

    /// <summary>How thick the deep bank over never-scouted ground is.</summary>
    /// <remarks>
    /// <b>A density now, and the two tiers are two layers rather than one blended number.</b> They were a
    /// single lerp from unknown through remembered to watched, which is what made them impossible to tune
    /// against each other: the only way to make the unknown denser was to drag the whole ramp with it, so
    /// every gain in "you cannot see out there" was paid for in visibility on ground the player had already
    /// scouted. Reported from the chair exactly that way.
    /// <para>
    /// Composited instead — the memory layer, then the deep bank over the top of it. What that buys is a
    /// guarantee rather than a compromise: on fully known ground the deep layer's contribution is
    /// <em>identically zero</em>, so this dial can go as high as it likes and cost nothing where the player
    /// has been. See <see cref="DeepEdgeFalloff"/> for the only place the two still meet.
    /// </para>
    /// </remarks>
    [Tune(0.0, 1.0, Label = "unknown density", Group = "fog")]
    public float UnknownDensity = 0.97f;

    /// <summary>How thick the veil is over ground scouted once and no longer watched.</summary>
    /// <remarks>
    /// The memory tier, and it is most of the frame once the opening reveal lands, so it is the one the polish
    /// is for. Thin: it has to read as information rather than as weather, because the player is expected to
    /// make decisions looking at it.
    /// </remarks>
    [Tune(0.0, 1.0, Label = "memory density", Group = "fog")]
    public float MemoryDensity = 0.42f;

    /// <summary>How brightly the deep bank is lit, against the memory layer's own brightness.</summary>
    /// <remarks>
    /// Its own number because a metre of mist and a kilometre of cloud are not the same thing lit harder. A
    /// deep bank scatters so much that almost nothing gets through it, which reads as flatter and paler than
    /// the thin layer, not as darker.
    /// </remarks>
    [Tune(0.0, 3.0, Label = "deep brightness", Group = "fog")]
    public float DeepBrightness = 1.25f;

    /// <summary>How much of the sun-direction colouring the deep bank takes.</summary>
    /// <remarks>
    /// Less than the thin layer by default, and that is the physics rather than a preference: the further
    /// light has been scattered the less it remembers which way it came from, so a thick bank is more uniform
    /// than a thin one. Setting this to one makes the deep bank as directional as the mist and it immediately
    /// reads as a sheet of coloured glass.
    /// </remarks>
    [Tune(0.0, 1.0, Label = "deep scatter share", Group = "fog")]
    public float DeepScatterShare = 0.45f;

    /// <summary>How solid the deep bank's cloud is, as a curve on the shared noise.</summary>
    /// <remarks>
    /// Below one it fills in — the thin parts of the same cloud field thicken while the thick parts stay
    /// thick, so the deep layer reads as a mass with texture rather than as the mist turned up. Above one it
    /// breaks apart. Sharing the field rather than sampling a second one is deliberate: it is one weather
    /// system, and two independent noise fields drifting over each other never resolve into a sky.
    /// </remarks>
    [Tune(0.3, 2.5, Label = "deep solidity", Group = "fog")]
    public float DeepSolidity = 0.62f;

    /// <summary>How sharply the deep bank retreats from ground that is known.</summary>
    /// <remarks>
    /// <b>The one place the two layers still touch, and the dial that decides how much that costs.</b> The
    /// masks are blurred to keep the boundary soft, so across the blur's width the deep bank does reach a
    /// little way into known ground — which is the visibility loss that made blending the wrong shape in the
    /// first place, just confined to the transition now. Raising this exponent concentrates the bank in
    /// genuinely unscouted ground and pulls it back off the fringe, at the cost of a tighter edge.
    /// </remarks>
    [Tune(1.0, 6.0, Label = "deep edge falloff", Group = "fog")]
    public float DeepEdgeFalloff = 2.4f;

    /// <summary>How much colour drains out of ground that is not being watched.</summary>
    /// <remarks>
    /// <b>Because dimming alone reads as dusk, not as memory.</b> The same trick the aerial perspective uses
    /// twenty lines further down world.frag: air drains saturation before it drains value, and a scene that
    /// only loses brightness looks like the same scene at a different hour. Draining the colour too makes
    /// remembered ground read as a record of ground rather than as ground at night — which matters most on
    /// this map, whose whole seasonal grade is carried in hue.
    /// </remarks>
    [Tune(0.0, 1.0, Label = "memory colour drain", Group = "fog")]
    public float MemoryDrain = 0.55f;

    /// <summary>How many metres across one billow of cloud is.</summary>
    /// <remarks>
    /// <b>Two scales are drawn, and this is the broad one.</b> A single octave reads as a texture laid over
    /// the map; two at different sizes, drifting at different rates, read as layers with depth between them
    /// — which is most of what makes cloud look like cloud rather than like noise.
    /// <para>
    /// Sized against the fog cell rather than against the screen, deliberately. At ninety metres a billow is
    /// nine cells across, so the cloud is much larger than the quantum underneath it and the ten-metre grid
    /// stops being legible in the result. A wavelength near the cell size would do the opposite and advertise
    /// the grid.
    /// </para>
    /// </remarks>
    [Tune(20.0, 400.0, Label = "cloud size (m)", Group = "fog")]
    public float CloudMetres = 95f;

    /// <summary>How much the cloud thins and thickens across itself.</summary>
    /// <remarks>
    /// Zero is a flat wash — the first version, and what "too strong" partly meant: an even sheet has no
    /// wisps and so reads as a filter rather than as weather. High values tear holes in it and leak
    /// information about ground the player has not scouted, which is the reason this has a ceiling below one.
    /// </remarks>
    [Tune(0.0, 0.8, Label = "wispiness", Group = "fog")]
    public float Wispiness = 0.45f;

    /// <summary>How fast the cloud rolls, in metres of simulated time per second.</summary>
    /// <remarks>
    /// <b>Downwind, on the wind's own clock.</b> The bearing and the clock are already in the push block for
    /// the trees and the water, so the fog rolls the same way the canopy leans and at the same rate the
    /// ripples cross — three things agreeing about the weather rather than three opinions about it. Slow, in
    /// absolute terms: cloud that moves as fast as a unit walks reads as smoke.
    /// </remarks>
    [Tune(0.0, 12.0, Label = "cloud drift (m/s)", Group = "fog")]
    public float CloudDriftMetresPerSecond = 2.2f;

    /// <summary>How bright the cloud is against the sky's own ambient.</summary>
    /// <remarks>
    /// <b>Lit by the sky rather than painted a colour.</b> A constant grey would be wrong twice a day: the
    /// same overcast at noon and at midnight. Scaling the sky ambient means the fog is grey-blue at dusk and
    /// nearly black at night for free, and it can never be brighter than the light falling on it.
    /// </remarks>
    [Tune(0.0, 3.0, Label = "cloud brightness", Group = "fog")]
    public float CloudBrightness = 1.15f;

    /// <summary>How much the veil takes its colour from which way the sun is, rather than from the sky.</summary>
    /// <remarks>
    /// <b>The same two colours the aerial perspective uses, so the fog is made of the same air.</b> Twenty
    /// lines of world.frag already pick between a cold sky-scatter and a warm toward-the-sun glow for
    /// distance haze; the veil was mixing toward flat sky ambient instead, which is why it sat on the scene
    /// rather than in it. Sharing the pair means the fog is seasonal and hourly for free — Atmosphere.cs
    /// derives them per frame from the date — and it can never disagree with the haze about what colour the
    /// air is today.
    /// <para>
    /// Its own intensity rather than the haze's, because the two are not the same thickness of air: distance
    /// haze is kilometres of it and this is a bank sitting on the ground, so the directional term reads
    /// stronger here at the same sun.
    /// </para>
    /// </remarks>
    [Tune(0.0, 1.5, Label = "veil scatter", Group = "fog")]
    public float Scatter = 0.85f;

    /// <summary>How much the veil glows when looked at against the sun.</summary>
    /// <remarks>
    /// Forward scattering, and it is the single most recognisable thing fog does with light: a bank between
    /// you and a low sun is brighter than the sun-lit ground beside it. Tightly focused — a sixth power — so
    /// it is a glow around the sun's bearing and not a general brightening, which would just wash the veil out.
    /// </remarks>
    [Tune(0.0, 3.0, Label = "veil sun glow", Group = "fog")]
    public float SunGlow = 1.1f;

    /// <summary>How far the cloud is drawn out along the wind, as a fraction of its width.</summary>
    /// <remarks>
    /// <b>Isotropic noise that translates looks like a texture sliding; a bank pulled out along its own
    /// motion looks like weather sweeping.</b> The noise is sampled in wind-aligned coordinates and the
    /// along-wind axis is compressed, which stretches the features that come out of it downwind. One is
    /// round; a half makes every billow twice as long as it is wide.
    /// </remarks>
    [Tune(0.15, 1.0, Label = "cloud stretch", Group = "fog")]
    public float CloudStretch = 0.42f;

    /// <summary>How much the gust makes the veil breathe.</summary>
    /// <remarks>
    /// On the wind's own gust rate, in bands running across the wind, so the fog thickens and thins in waves
    /// travelling through it rather than pulsing all at once. This is the rolling; the stretch is the sweeping.
    /// </remarks>
    [Tune(0.0, 0.6, Label = "gust roll", Group = "fog")]
    public float GustRoll = 0.22f;

    /// <summary>How long the veil takes to open or close over a cell, in seconds.</summary>
    /// <remarks>
    /// <b>Because the masks change eight frames at a time and the eye sees the step.</b> A watcher's view is
    /// recomputed once per refresh cycle, so without this the lit region jumps a whole cell at a time a few
    /// times a second — which was most of what "blocky" meant at the dim-to-lit boundary. Easing decouples
    /// what the veil looks like from how often the truth underneath it is recomputed.
    /// <para>
    /// It also earns its keep as motion: fog that opens over half a second reads as fog withdrawing, and fog
    /// that switches reads as a stencil being moved.
    /// </para>
    /// </remarks>
    [Tune(0.0, 3.0, Label = "veil fade (s)", Group = "fog")]
    public float FadeSeconds = 0.65f;

    /// <summary>How far around the settlement is already known when the map opens, in metres.</summary>
    /// <remarks>
    /// <b>Because nobody arrives somewhere knowing nothing about it.</b> A settlement mid-harvest has been
    /// standing for a year in the fiction, so a map that opens with the fog closed up against the granary
    /// wall says the villagers have never walked anywhere — and it puts the first thing the player sees
    /// behind a scouting errand.
    /// <para>
    /// Revealed as a plain disc around each of the player's own buildings, and deliberately <em>not</em>
    /// through <see cref="SimulationWorld.CanSee"/>: this is ground the settlement is presumed to know, not
    /// ground it can currently see, so a wood in the way should not punch a hole in the neighbourhood. It
    /// seeds the explored mask only — what is <em>watched</em> is still decided every cycle by sight.
    /// </para>
    /// </remarks>
    [Tune(0.0, 400.0, Label = "known at start (m)", Group = "fog")]
    public float StartRevealMetres = 150f;
}

/// <summary>
/// What the player has seen of the map, and what they are watching now.
/// </summary>
/// <remarks>
/// <b>Two masks on one grid, and the grid is the canopy grid's.</b> Ten metre cells, indexed by the same
/// arithmetic as the tree density field, because the fog is going to be read by two things that must never
/// disagree: the shader that dims the ground, and the per-cell gate that decides whether the trees in a cell
/// are submitted at all. Two grids for those two jobs is the bug this file's history is made of — a rule
/// implemented locally at each call site until the copies drift — and it would surface as trees popping in
/// and out along a boundary the player can see the fog at. One indexing function makes the disagreement
/// unrepresentable rather than unlikely.
/// <para>
/// <b>Dressing, in §52's sense, and deliberately so.</b> Nothing here is on <see cref="SimulationWorld"/>,
/// nothing is fingerprinted, nothing is saved. It is derived from unit positions and is therefore perfectly
/// deterministic, which is exactly the argument that would justify moving it inside — and the reason not to
/// is that the moment a simulation decision reads it, "what the player can see" becomes "what the world
/// does" and view state is inside the determinism fingerprint for good. The rule is one-way and it is worth
/// stating as a direction rather than as a location: <b>fog may read the simulation; the simulation may
/// never read fog.</b>
/// </para>
/// <para>
/// It holds cheaply because the thing shared across the seam is a pure predicate —
/// <see cref="SimulationWorld.CanSee"/> — rather than state. Fog calls it and mutates nothing.
/// </para>
/// <para>
/// <b>What is not here.</b> A per-faction knowledge aggregate — "which cells does faction F see" — is what
/// an AI opponent and a §30 defence decision will need, and that <em>is</em> simulation state, carried and
/// fingerprinted. It is a different structure from these two masks and it is not built. When it arrives the
/// player's fog should be derived from it rather than computed alongside it, or the two will drift in the
/// one way a player notices: seeing a unit the simulation has decided is hidden.
/// </para>
/// <para>
/// <b>The masks say where; they do not say what.</b> Whether unexplored ground is black or merely dim, and
/// whether a landmark is exempt from being hidden at all, are decisions for each consumer and are not
/// encoded here. The landform is exempt — §71's rock is meant to be seen from across the valley and that is
/// how an unreachable resource becomes a reason to expand — but that is the ground renderer's business to
/// honour, not a third mask.
/// </para>
/// </remarks>
internal sealed class FogOfWar
{
    /// <summary>
    /// How much ground one fog cell covers, in metres.
    /// </summary>
    /// <remarks>
    /// Ten, which is the canopy grid's cell and is not a coincidence: this <em>is</em> that grid, and the
    /// argument for ten is recorded there — about two canopies across, so a tree is never alone in its own
    /// cell however thick the wood, and a village's cleared ring does not average into the wood beside it.
    /// <para>
    /// It is coarse for a fog mask and that is the right trade. The edge does not need to be finer than the
    /// filtering that will smooth it, and ten metres of soft boundary is what fog edges look like anyway.
    /// What the coarseness buys is that the gate reading this is testing four thousand cells instead of
    /// thirty-four thousand nodes.
    /// </remarks>
    public const float CellMetres = 10f;

    /// <summary>Has this ground ever been watched. Monotonic: it only ever rises.</summary>
    /// <remarks>
    /// Monotonic is what makes the round-robin refresh safe. A partial update can only add, so there is no
    /// frame in which explored ground reads as unexplored because its watcher's turn has not come round —
    /// which is the flicker a decaying exploration mask would produce.
    /// </remarks>
    private float[] explored = Array.Empty<float>();

    /// <summary>Is this ground watched now. Replaced wholesale at the end of each cycle.</summary>
    private float[] visible = Array.Empty<float>();

    /// <summary>The cycle in progress, which becomes <see cref="visible"/> when it completes.</summary>
    /// <remarks>
    /// <b>Double-buffered rather than cleared and restamped in place.</b> Round-robin and "currently
    /// visible" pull against each other: clearing the live mask each frame would blank every watcher whose
    /// turn has not come, and never clearing it would leave vision smeared behind a walking body forever.
    /// Accumulating a whole cycle into a scratch mask and swapping resolves it — the live mask is always a
    /// complete answer, just an answer from up to one cycle ago.
    /// </remarks>
    private float[] pending = Array.Empty<float>();

    /// <summary>The watchers, rebuilt once per cycle rather than once per frame.</summary>
    /// <remarks>
    /// <b>Once per cycle, because finding the buildings means walking the nodes.</b> Buildings are a handful
    /// among tens of thousands of nodes, so collecting them is the same full sweep this session measured at
    /// seven milliseconds — and adding a second one per frame to feed the thing meant to remove the first
    /// would be its own joke. Amortised over a cycle it is a few hundred nodes a frame.
    /// <para>
    /// <b>And it is left as a sweep on purpose.</b> The obvious fix is to cache the buildings and rebuild
    /// only when they change, and the obvious fix is wrong here for the same reason the compact tree array
    /// was wrong: the next piece of work is a spatial index over these nodes, which makes this scan cheap for
    /// every caller rather than cheap for this one. A cache written now is a cache thrown away then. What
    /// this owes in the meantime is honesty about the cost, which is what the cycle figures in the readout
    /// are for — the frame that starts a cycle is the expensive one and it says so.
    /// </para>
    /// </remarks>
    private readonly List<(Vector2 At, float Sight)> watchers = new();

    /// <summary>
    /// Whose fog this is.
    /// </summary>
    /// <remarks>
    /// <b>Faction zero, and the filter is the point rather than a detail.</b> Without it every agent on the
    /// map is a watcher, so a raider walking in from the edge scouts the map <em>for the player</em> — the
    /// exact inverse of what fog is for, and a bug that would have been invisible on a village run because
    /// there are no raiders on one. The convention is <c>SimulationWorld</c>'s: an unspecified faction
    /// resolves to zero and <c>RaidDirector</c> takes one.
    /// <para>
    /// This is also the seam the knowledge layer will widen. One mask pair per faction is the shape that
    /// makes an AI opponent read its own fog rather than the player's, and it is why the field is named for
    /// a faction rather than hard-coded into the loops below.
    /// </para>
    /// </remarks>
    private static readonly FactionId Player = new(0);

    private int cursor;
    private int cells;
    private float extent;

    /// <summary>Whether the masks currently hold the "fog off" fill rather than anything scouted.</summary>
    private bool revealed;

    /// <summary>Frames left to wait before looking for watchers again, after finding none.</summary>
    private int emptyCycleWait;

    /// <summary>Whether the settlement's presumed neighbourhood still has to be marked known.</summary>
    /// <remarks>
    /// A flag rather than a call at the resize, because the seed needs the world and <see cref="Resize"/>
    /// deliberately does not take one — and because there are two moments that need it, a new map and the fog
    /// being switched back on, which would otherwise be two copies of the same loop.
    /// </remarks>
    private bool needsSeed;

    /// <summary>
    /// The two masks as one RGBA8 image, a texel per cell: red is explored, green is watched.
    /// </summary>
    /// <remarks>
    /// <b>Native resolution, and the smoothing is the sampler's job.</b> The first plan here was to upsample
    /// into something wear-sized with a blur — and it is redundant, because a linear-filtered texture already
    /// interpolates between texel centres, which is exactly the CPU bilinear it would have paid for. Uploaded
    /// at one texel per cell, a sixty-one square map is seven kilobytes and the ramp between a watched cell
    /// and its neighbour is ten metres wide, which is what a fog edge should look like anyway.
    /// <para>
    /// If it ever reads as a diamond lattice — the artefact bilinear on a coarse grid gives — the fix is a
    /// blur on the way out, and the wear texture's note says why it must be a blur of the mask and never
    /// back into it: smoothing the truth compounds until there is no truth left.
    /// </para>
    /// <para>
    /// <b>Four bytes a texel for two channels, because there is no two-channel format.</b>
    /// <c>TextureFormat</c> offers R8 and Rgba8 and nothing between, and the whole image is fifteen
    /// kilobytes on a six-hundred-metre map — so the alternative, threading an RG8 through the graphics
    /// layer and the Vulkan backend, would be engine work to save twelve kilobytes uploaded twice a second.
    /// Blue and alpha are spare and deliberately left so: the obvious tenant is a second faction's mask,
    /// which is the shape the knowledge layer wants.
    /// </para>
    /// <para>
    /// <b>The UV mapping is the trap.</b> The grid spans <see cref="SpanMetres"/>, which is <em>not</em> the
    /// map extent — the cell count is a ceiling plus one, so the grid overhangs the map by up to two cells.
    /// Sampling this with the extent, the way the wear texture is sampled, puts the fog half a cell out and
    /// leaves it there.
    /// </para>
    /// </remarks>
    private byte[] texels = Array.Empty<byte>();

    /// <summary>
    /// The masks as the veil shows them: eased toward the truth, and blurred on the way to the texture.
    /// </summary>
    /// <remarks>
    /// <b>Blurred into the texture and never back into the masks.</b> The wear texture's rule, and it applies
    /// for the same reason: a blur folded back into its own source compounds every time it runs, until after
    /// a minute there is no boundary left anywhere and the whole map is a uniform smear. So
    /// <see cref="explored"/> and <see cref="visible"/> stay the sharp binary truth — which is also what the
    /// draw gate will read, because a gate wants a decision and not a gradient — and these are a view of them.
    /// <para>
    /// <b>Two separable passes, because one is not enough to hide a ten-metre grid.</b> A linear-filtered
    /// binary mask ramps over exactly one cell with a kink at each texel centre, and a kink in the gradient is
    /// what the eye reads as a facet: reported from the chair as blocks, most visibly where dim meets lit.
    /// Two 1-2-1 passes spread the boundary over about three cells, so the ramp the sampler interpolates is
    /// already smooth before it is magnified.
    /// </para>
    /// </remarks>
    private float[] shownExplored = Array.Empty<float>();
    private float[] shownVisible = Array.Empty<float>();
    private float[] blurFront = Array.Empty<float>();
    private float[] blurBack = Array.Empty<float>();

    /// <summary>The image, ready to upload.</summary>
    public ReadOnlySpan<byte> Texels => texels;

    /// <summary>Whether the image has changed since it was last uploaded.</summary>
    public bool TexelsDirty { get; private set; }

    /// <summary>How much ground the grid covers, which is what the shader divides by. Not the map extent.</summary>
    public float SpanMetres => cells * CellMetres;

    /// <summary>Says the image has been handed to the GPU.</summary>
    public void MarkUploaded() => TexelsDirty = false;

    /// <summary>
    /// Eases the shown masks toward the truth, blurs them, and packs the result for upload.
    /// </summary>
    /// <remarks>
    /// Runs every frame rather than once a cycle, which is the point: the truth changes in eight-frame steps
    /// and the veil must not. Fifteen kilobytes and about a tenth of a millisecond, against a frame this
    /// session has measured at ninety.
    /// </remarks>
    private void RebuildTexels(float deltaSeconds, float fadeSeconds)
    {
        // <b>Snapped rather than eased when there is no time to ease over.</b> A zero delta is the first frame
        // and a zero fade is the slider turned off; both want the truth immediately, and easing by zero would
        // leave the veil shut on a map that is supposed to open already partly known.
        var step = fadeSeconds <= 0.001f || deltaSeconds <= 0f
            ? 1f
            : Math.Clamp(deltaSeconds / fadeSeconds, 0f, 1f);
        for (var i = 0; i < explored.Length; i++)
        {
            shownExplored[i] += (explored[i] - shownExplored[i]) * step;
            shownVisible[i] += (visible[i] - shownVisible[i]) * step;
        }

        Soften(shownExplored, 0);
        Soften(shownVisible, 1);
        TexelsDirty = true;
    }

    /// <summary>Blurs one mask twice and writes it into a channel of the image.</summary>
    /// <remarks>
    /// Separable 1-2-1, twice, which spreads a hard edge over about three cells — enough that what the sampler
    /// magnifies is already a smooth ramp rather than a one-cell step with a kink in it. Edges clamp by
    /// reusing the nearest row, so the map's border does not fade to unexplored and put a false frontier round
    /// the outside of the world.
    /// </remarks>
    private void Soften(float[] source, int channel)
    {
        Pass(source, blurFront);
        Pass(blurFront, blurBack);
        for (var i = 0; i < blurBack.Length; i++)
        {
            texels[i * 4 + channel] = (byte)Math.Clamp((int)(blurBack[i] * 255f + 0.5f), 0, 255);
            texels[i * 4 + 3] = 255;
        }

        void Pass(float[] from, float[] into)
        {
            for (var z = 0; z < cells; z++)
            for (var x = 0; x < cells; x++)
            {
                var row = z * cells;
                var left = from[row + Math.Max(0, x - 1)];
                var right = from[row + Math.Min(cells - 1, x + 1)];
                blurScratch[row + x] = (left + from[row + x] * 2f + right) * 0.25f;
            }

            for (var z = 0; z < cells; z++)
            for (var x = 0; x < cells; x++)
            {
                var up = blurScratch[Math.Max(0, z - 1) * cells + x];
                var down = blurScratch[Math.Min(cells - 1, z + 1) * cells + x];
                into[z * cells + x] = (up + blurScratch[z * cells + x] * 2f + down) * 0.25f;
            }
        }
    }

    private float[] blurScratch = Array.Empty<float>();

    /// <summary>How many cells the grid is on a side.</summary>
    public int Cells => cells;

    /// <summary>
    /// How many watchers the current cycle covers, split by what kind of thing is doing the watching.
    /// </summary>
    /// <remarks>
    /// <b>Split, because one total cannot say whether the granary is in it.</b> The first run of this reported
    /// twenty-nine watchers on a settlement of thirteen people and sixteen buildings, and twenty-nine is
    /// consistent with two completely different worlds: every body and every building, or every body and no
    /// building at all. It was the second, and a single count had no way of saying so — §74's lesson about
    /// instruments that print plausible numbers, arriving on schedule.
    /// </remarks>
    public (int Bodies, int Buildings) WatcherCount { get; private set; }

    /// <summary>Sight queries asked on the last frame, and what they cost.</summary>
    public int QueriesLastFrame { get; private set; }

    /// <summary>
    /// Over the last complete cycle: cells asked about, cells granted, and the longest reach in the list.
    /// </summary>
    /// <remarks>
    /// <b>The three numbers that separate "occlusion is working" from "the loop is not running".</b> A small
    /// lit area is the expected output of a settlement ringed by woodland and also the expected output of a
    /// stamp loop that never iterates — and per-frame counts cannot tell them apart, because the round-robin
    /// smears one watcher's work across several frames and every frame's figure looks equally small.
    /// <para>
    /// Asked against granted says how much of the reach the trees are eating. The widest reach says whether
    /// the granary's hundred metres is in the list at all, which is the other way a small lit area happens.
    /// </para>
    /// </remarks>
    public (int Asked, int Granted, float WidestReach) LastCycle { get; private set; }

    private int askedThisCycle;
    private int grantedThisCycle;

    /// <summary>Milliseconds the last frame's share of the cycle took.</summary>
    public double MillisecondsLastFrame { get; private set; }

    /// <summary>How many cells sit in each tier, which is what says the fog is doing anything at all.</summary>
    /// <remarks>
    /// <b>Counted, because "the map is dark" is what both a working fog and a broken one look like.</b> §74's
    /// lesson from the ablation harnesses that read plausible numbers while being wrong: an instrument needs
    /// to report the thing that would tell you it is lying. Three counts that sum to the cell total, moving
    /// in the directions a walking villager should move them, is that thing.
    /// </remarks>
    public (int Unexplored, int Remembered, int Visible) Counts { get; private set; }

    /// <summary>Sizes the grid to a map, discarding whatever was known about the last one.</summary>
    /// <remarks>
    /// The cell count is the canopy grid's formula exactly, and if that ever changes this has to change with
    /// it — which is the argument for the canopy field reading its geometry from here rather than computing
    /// its own.
    /// </remarks>
    public void Resize(float extentMetres)
    {
        extent = extentMetres;
        cells = Math.Max(2, (int)MathF.Ceiling(extentMetres / CellMetres) + 1);
        var total = cells * cells;
        if (explored.Length != total)
        {
            explored = new float[total];
            visible = new float[total];
            pending = new float[total];
            shownExplored = new float[total];
            shownVisible = new float[total];
            blurFront = new float[total];
            blurBack = new float[total];
            blurScratch = new float[total];
            texels = new byte[total * 4];
        }
        else
        {
            Array.Clear(explored);
            Array.Clear(visible);
            Array.Clear(pending);
            Array.Clear(shownExplored);
            Array.Clear(shownVisible);
        }

        watchers.Clear();
        cursor = 0;
        askedThisCycle = 0;
        grantedThisCycle = 0;
        // Cleared along with the masks, or a map rolled while the fog is switched off comes up dark: the
        // fill that "off" depends on would be skipped as already done, against arrays that were just zeroed.
        revealed = false;
        needsSeed = true;
        Counts = (total, 0, 0);
        // All unexplored, which is what a new map should look like before anyone has seen any of it. The
        // first Advance eases up from here.
        Array.Clear(texels);
        TexelsDirty = true;
    }

    /// <summary>Which cell a place falls in.</summary>
    public int Index(Vector2 at)
    {
        var local = (at + new Vector2(extent * 0.5f)) / CellMetres;
        var x = Math.Clamp((int)local.X, 0, cells - 1);
        var z = Math.Clamp((int)local.Y, 0, cells - 1);
        return z * cells + x;
    }

    /// <summary>Where the centre of a cell is, which is the point its visibility is decided at.</summary>
    /// <remarks>
    /// <b>One sample per cell, at its centre, and the coarseness is admitted rather than hidden.</b> A ten
    /// metre cell is all-or-nothing on its middle, so a wood's edge cutting a cell in half resolves to
    /// whichever side the centre is on. Sampling the corners too would cost four times as much to soften a
    /// boundary that the mask's own filtering softens anyway.
    /// </remarks>
    public Vector2 CentreOf(int index)
    {
        var x = index % cells;
        var z = index / cells;
        return new Vector2(
            (x + 0.5f) * CellMetres - extent * 0.5f,
            (z + 0.5f) * CellMetres - extent * 0.5f);
    }

    /// <summary>What the player is entitled to be shown of a place.</summary>
    public FogTier TierAt(Vector2 at) => TierOf(Index(at));

    /// <summary>What the player is entitled to be shown of a cell.</summary>
    public FogTier TierOf(int index) =>
        visible[index] > 0.5f ? FogTier.Visible
        : explored[index] > 0.5f ? FogTier.Remembered
        : FogTier.Unexplored;

    /// <summary>
    /// Advances one frame's share of the refresh cycle.
    /// </summary>
    /// <remarks>
    /// Reads the simulation and writes nothing back to it. The one-way rule in this class's remarks is
    /// enforced by that being the only direction any call here points.
    /// </remarks>
    public void Advance(SimulationWorld simulation, FogSettings settings, float deltaSeconds)
    {
        if (cells == 0) return;

        QueriesLastFrame = 0;
        MillisecondsLastFrame = 0.0;

        // <b>Everything watched, rather than a second code path.</b> Fog off has to mean the fog says yes to
        // everything, not that its consumers learn to skip it — a gate with two behaviours is a gate that
        // gets one of them wrong. So the masks are filled and the tiers are honest; nothing downstream needs
        // to know the switch exists.
        //
        // <b>And the transitions are handled in both directions, which the first cut got wrong.</b> Filling
        // the masks is destructive: switch off and on again and the map is explored for good, because "off"
        // has written the same ones into <see cref="explored"/> that scouting would have. Off wins nothing by
        // being subtle here — the switch is a reset — so the fill happens once on the way off, and the way
        // back on clears. What must not happen is a toggle that silently reveals the map and leaves no trace
        // of having done it.
        if (!settings.Enabled)
        {
            if (revealed) return;
            revealed = true;
            Array.Fill(explored, 1f);
            Array.Fill(visible, 1f);
            // Snapped, not eased: switching the feature off should look like it was never on rather than like
            // the weather clearing, and snapping is what lets every later frame return early costing nothing.
            Array.Fill(shownExplored, 1f);
            Array.Fill(shownVisible, 1f);
            Counts = (0, 0, cells * cells);
            RebuildTexels(0f, 0f);
            return;
        }

        if (revealed)
        {
            revealed = false;
            Array.Clear(explored);
            Array.Clear(visible);
            Array.Clear(pending);
            watchers.Clear();
            cursor = 0;
            askedThisCycle = 0;
            grantedThisCycle = 0;
            Counts = (cells * cells, 0, 0);
            needsSeed = true;
        }

        if (needsSeed)
        {
            needsSeed = false;
            Seed(simulation, settings);
        }

        var clock = Stopwatch.StartNew();

        if (cursor >= watchers.Count)
        {
            // <b>A map with nobody on it must not rebuild the list every frame.</b> The cycle-start condition
            // is "the cursor has run off the end", and an empty list satisfies it immediately and permanently
            // — so on the map lab, or a settlement that has died out, this would sweep all thirty-four
            // thousand nodes on every single frame looking for a granary that is not there. Which is the
            // precise cost this session spent an evening measuring, reintroduced by a subsystem meant to help
            // remove it.
            if (watchers.Count == 0 && emptyCycleWait > 0)
            {
                emptyCycleWait--;
                // Still eased and still uploaded: an empty map is a reason not to look for watchers, not a
                // reason to freeze the veil half open.
                RebuildTexels(deltaSeconds, settings.FadeSeconds);
                MillisecondsLastFrame = clock.Elapsed.TotalMilliseconds;
                return;
            }

            StartCycle(simulation, settings);
            // Half a second before asking again. Long enough that an empty map costs nothing, short enough
            // that the first building put down is lit before the player has finished looking at it.
            if (watchers.Count == 0) emptyCycleWait = 30;
        }

        var until = Math.Min(watchers.Count, cursor + Math.Max(1, settings.WatchersPerFrame));
        for (; cursor < until; cursor++)
        {
            Stamp(simulation, watchers[cursor]);
        }

        // The cycle closed on this frame: the scratch mask is now a complete answer, so it becomes the
        // answer. Swapped rather than copied, and the outgoing mask is cleared to become the next scratch.
        if (cursor >= watchers.Count && watchers.Count > 0)
        {
            (visible, pending) = (pending, visible);
            Array.Clear(pending);
            Recount();

            var widest = 0f;
            foreach (var watcher in watchers) widest = MathF.Max(widest, watcher.Sight);
            LastCycle = (askedThisCycle, grantedThisCycle, widest);
            askedThisCycle = 0;
            grantedThisCycle = 0;
        }

        // <b>Every frame, and this is the fix for the blocky edge.</b> The masks change once a cycle, in whole
        // cells; an image that followed them would step eight frames at a time and the boundary would read as a
        // row of squares switching on. Easing here decouples what the veil looks like from how often the truth
        // beneath it is recomputed.
        RebuildTexels(deltaSeconds, settings.FadeSeconds);
        MillisecondsLastFrame = clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Marks the ground the settlement is presumed to know already.
    /// </summary>
    /// <remarks>
    /// A disc around each of the player's own buildings, into the explored mask only. Not through
    /// <see cref="SimulationWorld.CanSee"/> on purpose — see the note on
    /// <see cref="FogSettings.StartRevealMetres"/>. Nothing here touches <see cref="visible"/>: what is
    /// watched is still earned every cycle by sight, so the seed cannot hand the player a view of anything
    /// moving.
    /// </remarks>
    private void Seed(SimulationWorld simulation, FogSettings settings)
    {
        var reach = settings.StartRevealMetres;
        if (reach <= 0f) return;

        var span = (int)MathF.Ceiling(reach / CellMetres);
        var reachSquared = reach * reach;
        var marked = 0;

        foreach (ref readonly var node in simulation.Nodes.All)
        {
            if (!node.IsAlive || !node.IsBuilt || node.Faction != Player) continue;
            if (node.Kind is not (NodeKind.Granary or NodeKind.ForwardDepot
                or NodeKind.House or NodeKind.Farm))
            {
                continue;
            }

            var origin = Index(node.Position);
            var originX = origin % cells;
            var originZ = origin / cells;
            for (var dz = -span; dz <= span; dz++)
            for (var dx = -span; dx <= span; dx++)
            {
                var x = originX + dx;
                var z = originZ + dz;
                if (x < 0 || z < 0 || x >= cells || z >= cells) continue;

                var index = z * cells + x;
                if (explored[index] > 0.5f) continue;
                if (Vector2.DistanceSquared(CentreOf(index), node.Position) > reachSquared) continue;
                explored[index] = 1f;
                marked++;
            }
        }

        SeededCells = marked;
        if (marked > 0) Recount();
    }

    /// <summary>How many cells the opening reveal marked known, which says whether it ran.</summary>
    public int SeededCells { get; private set; }

    /// <summary>Collects who is watching, for the cycle about to start.</summary>
    private void StartCycle(SimulationWorld simulation, FogSettings settings)
    {
        watchers.Clear();
        cursor = 0;
        var bodies = 0;

        foreach (ref readonly var agent in simulation.Agents.All)
        {
            // A sheltered body is indoors, and the building it is in does its own watching. Counting both
            // would put a body's reach on top of its house's for no reason anybody could see.
            if (!agent.IsAlive || agent.Sheltered) continue;
            if (agent.Faction != Player) continue;
            watchers.Add((agent.Position, agent.SightMetres));
            bodies++;
        }

        foreach (ref readonly var node in simulation.Nodes.All)
        {
            if (!node.IsAlive || !node.IsBuilt) continue;
            if (node.Faction != Player) continue;
            var sight = node.Kind switch
            {
                NodeKind.Granary => settings.GranarySightMetres,
                NodeKind.ForwardDepot => settings.DepotSightMetres,
                NodeKind.House or NodeKind.Farm => settings.DwellingSightMetres,
                _ => 0f,
            };
            if (sight > 0f) watchers.Add((node.Position, sight));
        }

        WatcherCount = (bodies, watchers.Count - bodies);
    }

    /// <summary>Marks every cell one watcher can see.</summary>
    private void Stamp(SimulationWorld simulation, (Vector2 At, float Sight) watcher)
    {
        var reach = watcher.Sight;
        if (reach <= 0f) return;

        // The square of cells the disc could touch, then the disc, then the sight test. Cheapest rejection
        // first: a bounding box compare, a squared distance, and only then a ray march.
        var span = (int)MathF.Ceiling(reach / CellMetres);
        var origin = Index(watcher.At);
        var originX = origin % cells;
        var originZ = origin / cells;
        var reachSquared = reach * reach;

        for (var dz = -span; dz <= span; dz++)
        for (var dx = -span; dx <= span; dx++)
        {
            var x = originX + dx;
            var z = originZ + dz;
            if (x < 0 || z < 0 || x >= cells || z >= cells) continue;

            var index = z * cells + x;
            var centre = CentreOf(index);
            if (Vector2.DistanceSquared(centre, watcher.At) > reachSquared) continue;

            // Already stamped this cycle by another watcher, and visibility is not a quantity — a second
            // yes is the same yes, and the ray march it would cost is the expensive part.
            if (pending[index] > 0.5f) continue;

            QueriesLastFrame++;
            askedThisCycle++;
            if (!simulation.CanSee(watcher.At, reach, centre)) continue;

            grantedThisCycle++;
            pending[index] = 1f;
            explored[index] = 1f;
        }
    }

    /// <summary>
    /// Checks that the shader's world-to-texel mapping lands on the cells this class thinks it does.
    /// </summary>
    /// <remarks>
    /// <b>One mapping, two implementations, and the second one cannot be looked at.</b> World position to
    /// cell is written here in <see cref="Index"/> and <see cref="CentreOf"/>, and again in world.frag as a
    /// UV — and the shader's copy is unreadable from a log, unverifiable from a screenshot (a veil half a
    /// cell out looks exactly like a veil), and wrong the first time it was written. So the arithmetic is
    /// asserted instead of inspected: for every cell, the shader's expression evaluated on the centre of
    /// that cell must land on that cell's own texel centre.
    /// <para>
    /// It does not prove the shader compiles to this. It proves the expression I put in the shader agrees
    /// with the grid the masks are built on, which is the half that was actually wrong — and it fails loudly
    /// if either side is edited without the other, which is the failure this file produces most.
    /// </para>
    /// </remarks>
    public string? MappingFault(float extentMetres)
    {
        var span = SpanMetres;
        for (var index = 0; index < cells * cells; index++)
        {
            var centre = CentreOf(index);
            // Exactly the expression in world.frag: offset by the map extent, scaled by one over the span.
            var u = (centre.X + extentMetres * 0.5f) / span;
            var v = (centre.Y + extentMetres * 0.5f) / span;
            // And exactly the sampler's convention: texel i covers [i, i+1) and its centre is at i + 0.5.
            var wantX = index % cells + 0.5f;
            var wantZ = index / cells + 0.5f;
            if (MathF.Abs(u * cells - wantX) > 1e-3f || MathF.Abs(v * cells - wantZ) > 1e-3f)
            {
                return $"cell {index} centres at ({centre.X:F2}, {centre.Y:F2}) m, which the shader's UV " +
                       $"sends to texel ({u * cells:F3}, {v * cells:F3}) when it should be " +
                       $"({wantX:F3}, {wantZ:F3}). The veil and the masks are on different grids.";
            }
        }

        return null;
    }

    private void Recount()
    {
        var unexplored = 0;
        var remembered = 0;
        var seen = 0;
        for (var i = 0; i < explored.Length; i++)
        {
            if (visible[i] > 0.5f) seen++;
            else if (explored[i] > 0.5f) remembered++;
            else unexplored++;
        }

        Counts = (unexplored, remembered, seen);
    }
}
