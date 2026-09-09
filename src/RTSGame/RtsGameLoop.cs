using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Primitives;
using Blix.Graphics.Vulkan;
using Blix.Render;
using RTSGame.AI;
using RTSGame.Control;
using RTSGame.Debug;
using RTSGame.Rendering;
using RTSGame.Simulation;
using RTSGame.Simulation.Navigation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;
using RTSGame.Simulation.Movement;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame;

internal enum PerformanceCameraMotion
{
    Still,
    Pan,
    Rotate,
    Zoom,
}

internal sealed class RtsGameLoop : IGameLoop, IInputHandler, IDebuggable, IDisposable
{
    // Body feel is judged by eye, not by the benchmark: a crowd can score well on route
    // length and stall time and still look wrong. Sliders, with the crowd metrics beside
    // them so a change can be judged against something.
    /// <summary>Side of the world the game runs on, in metres.</summary>
    /// <remarks>
    /// Not <c>SimulationWorld.DefaultExtentMeters</c>, which is the 30 m square every
    /// locomotion threshold was calibrated against and must not move. This is the world the
    /// <em>game</em> is played on, and it is 600 m for reasons argued in
    /// <c>plan-rts-game.md</c> §3: at walking pace that is a map crossed in about six
    /// minutes, a sixteen per cent contested fraction, and roughly one and a half times the
    /// area of an AoE2 Large map per unit — where 1200 m was seven times emptier than one.
    /// <para>
    /// <b>600 down to 480, judged from the chair once there were two settlements on it.</b> §145. §3's
    /// arithmetic was done before anything lived here: a crossing time is the right unit and six minutes was
    /// the wrong number for it, because what a player actually waits on is the distance between the two
    /// settlements — 270 m of it on the run this was reported from, most of it empty. 480 m is a crossing in
    /// about four and a half minutes and a third less ground to fill, and it is the extent every scenario
    /// takes its default from, so the two-settlement separation comes down with it.
    /// </para>
    /// </remarks>
    public const float DefaultWorldExtentMeters = 480f;

    /// <summary>Sim seconds per wall-clock second the game starts at.</summary>
    /// <remarks>
    /// 1.5, which is what §3 proposed and what judging it on the slider settled on: 1x is
    /// unbearably slow to sit through, 2x reads as agitated, and 1.5 is the one that felt like
    /// the world running at its own pace rather than at the player's. It is a tick-rate
    /// multiplier and not a speed multiplier, so nothing about the body or the geometry moves
    /// with it — only how long the waiting takes in the chair.
    /// </remarks>
    public const float DefaultCompression = 1.5f;

    /// <summary>Sim seconds per wall-clock second a played village starts at.</summary>
    /// <remarks>
    /// <b>3x, because what is worth sitting through changed.</b> §144. 1.5 was judged on the slider when a
    /// settlement was a handful of hands and the longest thing worth waiting for was a shuttle's round trip
    /// (§3). The unit of interest now is a year — three windows of field work, a harvest, a barracks, a
    /// garrison — and at 1.5 that is twenty-four minutes in the chair. At 3x it is twelve, a day is forty
    /// seconds, and a season is between three and six minutes.
    /// <para>
    /// It remains a tick-rate multiplier and not a speed multiplier: nothing about a body or the geometry
    /// moves with it, only how long the waiting takes. And it is the <em>village's</em> default, not this
    /// class's — every headless scenario and every performance case still starts where it did, so no figure
    /// quoted anywhere in the plan becomes incomparable.
    /// </para>
    /// </remarks>
    public const float DefaultVillageCompression = 3f;

    /// <summary>Seconds a hand-assigned unit spends at each place before moving on.</summary>
    /// <remarks>
    /// Six seconds, which is long enough to read as work being done rather than as a unit
    /// bouncing, and short enough that a shuttle's rhythm is visible inside a minute of
    /// watching. Nothing depends on it — when the economy arrives, how long the work takes is
    /// the work's own number.
    /// </remarks>
    private const float PostDwellSeconds = 6f;

    private readonly BodyFeelSettings bodyFeel = new();
    private readonly ClockSettings clock = new();
    private readonly LookSettings look = new();
    private readonly WoodlandSettings woodland = new();
    private readonly FogSettings fogSettings = new();

    /// <summary>
    /// What the player has seen of this map, and what they are watching now.
    /// </summary>
    /// <remarks>
    /// Renderer-side and never handed to the simulation. See <see cref="FogOfWar"/> for why that direction
    /// is the whole design rather than an implementation detail.
    /// </remarks>
    // Named for the readout rather than for the feature, and the rename was not cosmetic: `fog` is
    // already a local Vector4 in the frame's push writer, carrying the haze range. The two met at a
    // compile error, which is the good outcome — see the note on the SCOUTED line for the same
    // collision caught earlier by reading.
    private readonly FogOfWar scouted = new();

    /// <summary>The world the fog was sized to, so a new map starts unexplored.</summary>
    /// <remarks>
    /// Identity rather than extent, because two maps of the same size are still two maps and rolling a new
    /// one has to forget the last. Six places in this file build a <see cref="SimulationWorld"/> and a save
    /// load builds a seventh; comparing the reference catches all of them without a hook in each.
    /// </remarks>
    private SimulationWorld? fogWorld;

    /// <summary>
    /// Static renderables bucketed by the same ten-metre cells as the information mask.
    /// </summary>
    /// <remarks>
    /// A wooded Village has roughly thirty-four thousand nodes and only four thousand of them are on known
    /// ground. Walking every tree merely to ask the fog whether it is hidden cost five to nine milliseconds
    /// per frame. The index is renderer-owned and rebuilt whenever the node population changes; simulation
    /// continues to own the nodes and never reads this cache.
    /// </remarks>
    private List<NodeId>[] renderNodeCells = Array.Empty<List<NodeId>>();
    private SimulationWorld? renderNodeWorld;
    private int renderNodeSlots = -1;
    private int renderNodeLive = -1;
    private int indexedStandingTrees;
    private int indexedOutcrops;
    private readonly RaidSettings raids = new();

    /// <summary>
    /// The scripted adversary. Scaffolding, driven from outside the simulation on purpose.
    /// </summary>
    /// <remarks>
    /// §28: in the real game the thing over the hill is another player, so the thief is a fixture of the
    /// same kind as the pen-escape crowd and none of the simulation may come to depend on it. It drives the
    /// world through the same queued orders a player uses, which is also the honest test that those are
    /// enough to play with.
    /// </remarks>
    private RaidDirector? raiders;
    // The world this session is judging the body on. Session 2 exists because a body cannot
    // be judged on a 30 m square and then assumed to feel the same crossing a kilometre.
    private readonly float worldExtentMeters;
    private readonly CrowdMetrics crowdMetrics = new();
    private readonly ObjectTunables tunables;
    // The palette is tuned for a tonemapped HDR frame, which is a different job from tuning it for
    // the swapchain. Values here are albedos — what share of the light a surface throws back — so
    // they sit in the 0.2 to 0.7 band and the sun does the work of making them bright. The old
    // palette was written when a colour went to the screen more or less as typed, which is why
    // everything read as pastel: it was already near white before any light hit it.
    // The checker exists as a scale reference: perceived speed on an untextured plane comes almost
    // entirely from crossing edges, so a 1.5 m/s body on a flat colour looks like it is sliding. But
    // these two were 20% apart, which is a chessboard rather than a reference — and a chessboard is the
    // single most conspicuous "this is a prototype" signal in the frame. Six per cent still gives the eye
    // the edges and stops announcing itself.
    //
    // <b>And the ground now shares the art's palette rather than having its own.</b> These were written
    // against a greybox where the ground was the brightest thing in the frame by construction; against
    // the CC0 pack, whose greens sit between 0.09 and 0.23, a 0.42 grass is two to three times brighter
    // than the foliage standing on it — so a flat plain read as a washed-out sheet with dark models
    // scattered over it. The palette is one palette now, and the terrain is in it.
    // Three per cent apart, not six: after a filmic curve and a saturation lift, a difference in
    // albedo lands on screen larger than it was written, and six per cent was still a chessboard.
    /// <summary>The grass, before the checker splits it. Contrast is a slider.</summary>
    private static readonly Vector4 Grass = new(0.144f, 0.195f, 0.075f, 1f);
    private static readonly Vector4 UnitColor = new(0.74f, 0.40f, 0.18f, 1f);
    private static readonly Vector4 SelectedUnitColor = new(0.98f, 0.72f, 0.24f, 1f);
    private static readonly Vector4 RaiderColor = new(0.62f, 0.10f, 0.09f, 1f);
    private static readonly Vector4 MilitiaColor = new(0.30f, 0.38f, 0.46f, 1f);
    private static readonly Vector4 SelectionColor = new(0.26f, 0.86f, 0.94f, 1f);

    /// <summary>
    /// The one colour anything picked is marked in.
    /// </summary>
    /// <remarks>
    /// <b>One hue, two alphas, and no per-kind colours at all.</b> There were three in play — orange on a
    /// selected body, dark teal on a hovered one, pale cyan on the mark underneath — so the same state looked
    /// different depending on what you had picked. Hover and selected differ by <em>strength</em>, which is the
    /// one axis that does not need learning.
    /// <para>
    /// Deeper and more saturated than the pale cyan it replaces, because that washed out against lit grass at
    /// the alphas a see-through marker wants. A marker that has to be hunted for is not a marker.
    /// </para>
    /// </remarks>
    private static readonly Vector4 HighlightColor = new(0.10f, 0.62f, 0.95f, 1f);
    private static readonly Vector4 DestinationColor = new(0.98f, 0.82f, 0.32f, 1f);
    private static readonly Vector4 ObstacleColor = new(0.33f, 0.35f, 0.37f, 1f);
    // A store, a field and a woodlot, told apart at a glance because the whole point of the
    // low-attention mode is that a settlement is legible without reading a number.
    // Buildings: a wall colour and a darker, warmer roof over it. The pair is what makes the
    // silhouette read; a single tint on a cube reads as a cube whatever the tint is.
    private static readonly Vector4 GranaryColor = new(0.72f, 0.58f, 0.36f, 1f);

    private static readonly Vector4 GranaryRoofColor = new(0.40f, 0.26f, 0.16f, 1f);
    private static readonly Vector4 DepotColor = new(0.56f, 0.49f, 0.38f, 1f);
    private static readonly Vector4 DepotRoofColor = new(0.31f, 0.25f, 0.19f, 1f);
    // A field, as ground: dry earth, broken earth, the furrows cut into it, and the crop standing on
    // it — young green through to ripe gold. The whole crop cycle is these five colours.
    private static readonly Vector4 FallowSoilColor = new(0.47f, 0.40f, 0.28f, 1f);
    private static readonly Vector4 TilledSoilColor = new(0.27f, 0.19f, 0.13f, 1f);
    private static readonly Vector4 FurrowColor = new(0.20f, 0.14f, 0.10f, 1f);
    private static readonly Vector4 YoungCropColor = new(0.38f, 0.55f, 0.20f, 1f);
    private static readonly Vector4 RipeCropColor = new(0.78f, 0.63f, 0.20f, 1f);
    // Trees. Two canopy greens, mixed per tree, because a forest of one colour reads as one object.
    private static readonly Vector4 TrunkColor = new(0.33f, 0.24f, 0.17f, 1f);
    private static readonly Vector4 CanopyDarkColor = new(0.14f, 0.29f, 0.15f, 1f);
    private static readonly Vector4 CanopyLightColor = new(0.25f, 0.42f, 0.19f, 1f);
    // Goods, wherever they are: on a cart's back or lying in the road. The same two colours for both,
    // so a heap and a load read as the same substance in two places.
    /// <summary>The colour of a lit window from outside, which is a hue and so belongs beside the others.</summary>
    /// <remarks>
    /// <b>Deeper than the first version, which was 1.00/0.74/0.44 at a glow of 3.2 and clipped to pure
    /// white.</b> That is what made these read as floating cards rather than as windows: a blown-out
    /// emissive loses its hue, and the scene resolves from a 4x target, so the white spread and a
    /// forty-centimetre pane looked like a metre and a half of paper. A source is recognised by its colour
    /// at the edges, so the colour has to survive the top of the curve.
    /// </remarks>
    private static readonly Vector4 EmberTint =
        new(1.00f, 0.34f, 0.10f, SettlementArt.MaterialClass.Ember);

    private static readonly Vector4 HouseColor = new(0.68f, 0.58f, 0.46f, 1f);
    private static readonly Vector4 HouseRoofColor = new(0.44f, 0.22f, 0.16f, 1f);
    // The pack's own Wheat (0.384, 0.300, 0.065) and Wood (0.246, 0.144, 0.054), so a load on a
    // villager's back is the same colour as the crop it came from and the timber it was cut from.
    private static readonly Vector4 GrainColor = new(0.384f, 0.300f, 0.065f, 1f);
    private static readonly Vector4 WoodColor = new(0.246f, 0.144f, 0.054f, 1f);

    /// <summary>Pale, cool and desaturated: the one thing in this palette that is not earth or leaf.</summary>
    private static readonly Vector4 StoneColor = new(0.300f, 0.306f, 0.318f, 1f);
    private static readonly Vector4 PalisadeWallColor = new(0.34f, 0.20f, 0.09f, 1f);
    private static readonly Vector4 StoneWallColor = new(0.36f, 0.37f, 0.39f, 1f);
    private static readonly Vector4 BuildValidColor = new(0.30f, 0.84f, 0.72f, 1f);
    private static readonly Vector4 BuildRemoveColor = new(0.96f, 0.53f, 0.28f, 1f);
    private static readonly Vector4 BuildInvalidColor = new(0.82f, 0.24f, 0.22f, 1f);
    private static readonly Vector4 NavWalkableColor = new(0.30f, 0.64f, 0.62f, 1f);
    private static readonly Vector4 NavClearanceColor = new(0.82f, 0.56f, 0.23f, 1f);
    private static readonly Vector4 NavBlockedColor = new(0.70f, 0.22f, 0.20f, 1f);
    private static readonly Vector4 CongestionClearColor = new(0.18f, 0.34f, 0.30f, 1f);
    private static readonly Vector4 CongestionHotColor = new(0.95f, 0.28f, 0.18f, 1f);
    // Also brought into the pack's range, and against its own materials where there is one to match:
    // Stone (0.244, 0.246, 0.215) for a road, Dirt (0.095, 0.074, 0.029) for mud, Water for a pond.
    private static readonly Vector4 RoadColor = new(0.255f, 0.240f, 0.190f, 1f);
    /// <summary>Moor: bleached olive, the colour of grass that has had a poor season every season.</summary>
    private static readonly Vector4 HeathColor = new(0.170f, 0.163f, 0.090f, 1f);

    private static readonly Vector4 RoughColor = new(0.230f, 0.190f, 0.115f, 1f);
    private static readonly Vector4 MudColor = new(0.125f, 0.098f, 0.062f, 1f);
    private static readonly Vector4 ImpassableColor = new(0.071f, 0.213f, 0.246f, 1f);

    /// <summary>Silt: what a river lays on its own bed where the water is too deep to disturb it.</summary>
    private static readonly Vector4 RiverbedColor = new(0.088f, 0.079f, 0.058f, 1f);

    /// <summary>Gravel and shingle, which is what a ford is made of and why a ford is crossable.</summary>
    private static readonly Vector4 ShingleColor = new(0.196f, 0.184f, 0.156f, 1f);

    /// <summary>Water you can see the bottom of, which is why it is lighter and browner than the deep.</summary>
    /// <remarks>
    /// Between the river and the bed under it rather than a paler version of the river, because that is what
    /// shallow water looks like: most of what reaches the eye off a ford is the gravel, tinted.
    /// </remarks>
    private static readonly Vector4 ShallowsColor = new(0.146f, 0.235f, 0.223f, 1f);

    /// <summary>Ground inside a stand of trees: leaf litter, dark because nothing reaches it.</summary>
    /// <remarks>
    /// Distinct from the water this shares its impassability with, and darker than grass, so that the shape
    /// of the wood is legible from above even where the canopy does not quite close over it. It is the one
    /// place a player can see where the forest actually ends, which since it is now a wall they have to cut
    /// through is the thing they most need to see.
    /// </remarks>
    private static readonly Vector4 ForestFloorColor = new(0.062f, 0.082f, 0.038f, 1f);
    private static readonly Vector4 AvoidanceColliderColor = new(0.20f, 0.78f, 0.92f, 1f);
    private static readonly Vector4 PlacementColliderColor = new(0.34f, 0.86f, 0.44f, 1f);
    private static readonly Vector4 InteractionColliderColor = new(0.78f, 0.38f, 0.92f, 1f);
    private static readonly Vector4 MovementColliderColor = new(0.20f, 0.42f, 0.96f, 1f);

    /// <summary>Ground nothing walks through, ground nothing builds on, in the overlay.</summary>
    private static readonly Vector4 SolidColliderColor = new(0.95f, 0.30f, 0.22f, 1f);

    private static readonly Vector4 BlockerColliderColor = new(0.95f, 0.72f, 0.18f, 1f);
    private static readonly Vector4 PreferredVelocityColor = new(0.22f, 0.92f, 0.66f, 1f);
    private static readonly Vector4 ResolvedVelocityColor = new(0.96f, 0.30f, 0.72f, 1f);
    private static readonly Vector4 StuckUnitColor = new(0.86f, 0.18f, 0.22f, 1f);
    private static readonly Vector4 QueuedUnitColor = new(0.24f, 0.58f, 0.90f, 1f);
    private static readonly Vector4 PathColor = new(0.96f, 0.90f, 0.30f, 1f);
    private static readonly Vector4 IdleStateColor = new(0.62f, 0.64f, 0.66f, 1f);
    private static readonly Vector4 MoveStateColor = new(0.94f, 0.54f, 0.20f, 1f);
    private static readonly Vector4 FollowStateColor = new(0.30f, 0.78f, 0.42f, 1f);
    private static readonly Vector4 PatrolStateColor = new(0.68f, 0.38f, 0.88f, 1f);
    private static readonly Vector4 ChaseStateColor = new(0.92f, 0.28f, 0.24f, 1f);
    private static readonly Vector4 FleeStateColor = new(0.24f, 0.66f, 0.92f, 1f);
    private static readonly int[] StressScenarioCounts = { 50, 200, 500, 30 };

    private readonly int exitAfterFrames;

    /// <summary>A sealed, input-independent run used to compare frame costs.</summary>
    private readonly bool performanceRun;
    private readonly PerformanceCameraMotion performanceCameraMotion;
    private readonly float performanceHour;
    private bool performanceCameraInitialized;
    private Vector2 performanceCameraOrigin;
    private float performanceCameraYaw;
    /// <summary>
    /// Whether a sealed run keeps the display's cadence.
    /// </summary>
    /// <remarks>
    /// <b>Off by default, and the reason is that the first matrix run measured the monitor.</b> The swapchain
    /// takes FIFO unless told otherwise, so a frame time is then how many refresh intervals the frame waited
    /// for — a 26 ms figure at a near view and a 33 ms figure at a wide one can both be "missed the train",
    /// and neither is a cost. A sealed run therefore presents through Mailbox and reports what the frame
    /// actually took, tearing included, because nobody is watching it.
    /// <para>
    /// Kept as a flag because the other question is real too: pacing as experienced <em>is</em> vsync-bound,
    /// and the choppiness the player feels is which refresh intervals get missed. That is a different
    /// measurement, and the two must never be quoted as one — hence <c>vsync=</c> in the report.
    /// </para>
    /// </remarks>
    private readonly bool performanceVsync;

    /// <summary>
    /// Gives the coarse cascades a sixteen-triangle stand-in instead of the model's own silhouette.
    /// </summary>
    /// <remarks>
    /// <b>Off by default, on the eye's verdict.</b> §84 measured it at 7-10 ms and said its one unverified
    /// claim was the look. The look lost: at 118 m the real geometry's tree shadows read plainly better, and
    /// the frame is comfortable either way once the fixture stopped charging the frame for its own
    /// diagnostics. Kept as a switch because the measurement stands and weaker hardware or a bigger map may
    /// want it — and because it is the only way to keep both arms in one binary, which is the only kind of
    /// comparison that survived §84: a before-and-after separated by a rebuild compares two thermal states,
    /// not two configurations.
    /// </remarks>
    private readonly bool shadowProxies;

    /// <summary>Restores the queue-draining fog upload, so the fix can be measured against itself.</summary>
    /// <remarks>
    /// Kept for the same reason <see cref="shadowProxies"/> is a switch rather than a rewrite: a change of
    /// this size has to be provable in one session, on one thermal state, by alternating the two arms. The
    /// old path is also still the right one everywhere its guarantee is needed — see QueueTextureUpload.
    /// </remarks>
    private readonly bool performanceBlockingUpload;

    /// <summary>Draws every tree from the pre-kit models, at one detail level, at every distance.</summary>
    /// <remarks>
    /// §90 worked out arithmetically that a tree of about a third the kit's triangles could be drawn in full
    /// everywhere for today's budget. The pre-kit art turned out to be an eighth of it — 345 to 552 triangles
    /// — so the arithmetic can be checked against something real instead of imagined.
    /// </remarks>
    private readonly bool cheapTrees;

    /// <summary>Shifts every tree this many detail levels coarser. A lever, not a look control.</summary>
    /// <remarks>
    /// <b>The control arm for the fill question.</b> §87 found the frame peaks at 200-300 m while submitted
    /// geometry keeps rising, which points at fragment cost rather than triangles — but "points at" is not
    /// evidence. Coarser geometry changes triangles and leaves pixels alone; a smaller window changes pixels
    /// and leaves triangles alone. Whichever moves the frame is the one paying for it.
    /// </remarks>
    private readonly int performanceTierBias;

    /// <summary>The recorder for a sealed run, absent otherwise. See PerformanceRun.</summary>
    private readonly PerformanceRun? performance;

    /// <summary>
    /// This frame's delta, unsmoothed.
    /// </summary>
    /// <remarks>
    /// Separate from <c>frameMilliseconds</c>, which is an exponential average for the on-screen line. A
    /// percentile taken over a smoothed series is a statement about the smoothing constant.
    /// </remarks>
    private double rawFrameMilliseconds;

    /// <summary>
    /// The two halves of the frame, and the fog inside the first of them.
    /// </summary>
    /// <remarks>
    /// <b>Added because the first matrix closed to 8 ms of an accounted 45.</b> BUILD reports the render
    /// build's own phases and nothing else, so everything in <c>OnUpdate</c> — the tick, the fog refresh, the
    /// fog texture upload, the cover resolve — was outside every number on the line. That is how a wide view
    /// came to look GPU-bound: the reported phases were small, so the rest was assumed to be the GPU, and one
    /// of the largest terms in it was a per-frame texture upload nobody was timing.
    /// <para>
    /// With all three, the frame closes to a residual: whatever is left after update and render is the host's
    /// own — acquiring an image, submitting, and waiting on the GPU. A residual is a real answer; an
    /// unexplained majority is not.
    /// </para>
    /// </remarks>
    private double updateMilliseconds, fogMilliseconds, renderMilliseconds;

    /// <summary>What each cascade was handed this frame. Filled from the art; see StagedCasterLoad.</summary>
    private readonly int[] stagedCasterInstances = new int[ShadowCascades.Count];
    private readonly long[] stagedCasterTriangles = new long[ShadowCascades.Count];
    private bool debugStateInitialized;

    /// <summary>
    /// Rolls a new map every so many frames, so the regeneration path can be soaked without a keyboard.
    /// </summary>
    /// <remarks>
    /// <b>Because a key nobody can press in a test is a key nobody has tested.</b> Rolling a map is the single
    /// heaviest thing this loop does — a new world, a new drainage solve, a new settlement, and a full rebuild
    /// of every instance buffer — and it had shipped as a keypress that silently did nothing at all. A headless
    /// cadence turns "does Space work" into a question a gate leg can answer, and turns "does it survive being
    /// pressed fifty times" into the same question run fifty times.
    /// </remarks>
    private readonly int rollEveryFrames;

    /// <summary>Metres of relief the village is generated with. Zero is the flat ground everything is
    /// calibrated against — see §54's migration plan.</summary>
    private float reliefAmplitudeMetres;

    /// <summary>Camera standoff to open at, so a wide-zoom frame can be measured. Zero keeps the default.</summary>
    private readonly float startingZoomMetres;
    private LiveMovementTrace? movementTrace;
    private SimulationWorld simulation;
    private readonly SelectionController selection = new();
    private readonly Camera3D camera = new()
    {
        VerticalFieldOfView = MathF.PI * 42f / 180f,
        NearPlane = 0.25f,
        // Recomputed every frame from how far back the camera is standing — see UpdateCamera. It was a
        // flat 150 m, which is fine at the zoom it was written for and silently wrong at any other: the
        // zoom range went to eight hundred metres on a 600 m map, so pulling back past a hundred and fifty
        // clipped the entire world away and the screen went to sky.
        FarPlane = 150f,
    };
    // What the camera is looking at. It used to be the origin, full stop, which is fine on
    // a thirty-metre square and useless on a kilometre one: the body has to be watched
    // covering distance, and that means going with it.
    private Vector2 cameraFocus;
    /// <summary>
    /// Whether the camera chases the selection. Off by default, now that panning exists.
    /// </summary>
    /// <remarks>
    /// It was on, because following was the only way the camera ever went anywhere. With a pan it is a
    /// surprise: a view that drifts whenever a selected body walks is a view you are fighting. Z turns it
    /// back on for the thing it is genuinely good at, which is watching one long walk.
    /// </remarks>
    private bool cameraFollowsSelection;

    private IRenderHost host = null!;
    private IGraphicsDevice graphicsDevice = null!;
    private InstanceBuffer overlayBuffer = null!;
    private InstanceBuffer unitBuffer = null!;
    // Overlays only: the navigation raster, paths, velocities, collider discs, the build
    // ghost, the tilled plots. Everything in here is flat, drawn a few centimetres off the
    // ground, and has no business casting a shadow — which is why it is no longer the same
    // batch as the buildings.
    private InstancedBatch overlayBatch = null!;
    // The coarse ground gets its own batch rather than sharing the debug one. They have
    // different sizes and different lifetimes, and a shared batch means the ground can run a
    // per-cell overlay past the batch's instance ceiling — which it did, immediately.
    // Per-cell detail gets its own batch: the coarse ground alone is fourteen thousand
    // instances and the ceiling is sixteen, so sharing one leaves no room for the thing the
    // detail exists to draw.
    // The ground does not change between frames and was being rebuilt from scratch on every
    // one of them: fourteen thousand blocks, each with a bilinear surface and height sample.
    // That is 18 ms of a 16 ms frame spent redrawing a field that had not moved.
    private InstancedBatch unitBatch = null!;

    /// <summary>The people, rigged. Null when there is no character asset — the prop villager stands in.</summary>
    private SkinnedBodies? bodies;

    private ShaderProgramHandle skinnedShader;
    private PipelineHandle skinnedPipeline;
    private ShaderProgramHandle skinnedCasterShader;
    private PipelineHandle skinnedCasterPipeline;

    /// <summary>
    /// The skinned caster's own cascade payloads: a light matrix and the bone stride.
    /// </summary>
    /// <remarks>
    /// Separate arrays from the leaning caster's, because the second sixteen bytes mean different things to
    /// the two pipelines — wind there, stride here. Same size, so nothing else about the cascade loop changes.
    /// </remarks>
    private readonly byte[][] skinnedCascadePush = new byte[ShadowCascades.Count][];
    private VulkanGraphicsDevice vk = null!;
    private ShaderProgramHandle worldShader;
    private PipelineHandle worldPipeline;
    private PipelineHandle groundBlendPipeline;

    // Daylight: a RenderGraph — sun shadow depth pass → HDR scene pass (procedural sky,
    // shadowed sun, aerial perspective) → present (expose, grade, tonemap). Lifted from
    // TankArena and Bulwark, which already prove the seam; the mood is this game's.
    //
    // It is not decoration. A settlement is read from above as a plan, and a plan drawn in
    // flat ambient with no cast shadows has no depth cue at all: every building is a
    // coloured rectangle lying in the same plane as the ground it stands on, so its height,
    // its footprint and its distance from its neighbour are all unreadable. Shadows are how
    // a box becomes a building.
    private RenderGraph graph = null!;
    private GraphResourceHandle hdrHandle, hdrMsaaHandle, sceneDepthHandle;
    private PassHandle scenePassHandle;

    /// <summary>One depth target and one pass per cascade. See ShadowCascades for why three.</summary>
    private readonly GraphResourceHandle[] cascadeTargets = new GraphResourceHandle[ShadowCascades.Count];
    private readonly PassHandle[] cascadePasses = new PassHandle[ShadowCascades.Count];

    /// <summary>Each cascade's shadow push, which is its own fitted view-projection.</summary>
    private readonly byte[][] cascadePush = BuildCascadePushes();

    /// <summary>A cascade's light view-projection, then the wind. See shadow_caster.vert.</summary>
    private const int CascadePushSize = 64 + 16;

    /// <summary>
    /// The most bones one skinned body may have, which is what the palette buffer is sized for.
    /// </summary>
    /// <remarks>
    /// <b>A capacity, not the asset's bone count.</b> The shader interface has to be declared before any
    /// character file is opened, so coupling the SSBO's size to the skeleton would mean the renderer could
    /// not be built until the art was chosen. The real stride travels to the shader at draw time
    /// (<c>uSkin.x</c>) and the buffer only has to be big enough — and a skeleton over it is refused at load
    /// with a clear reason rather than writing past the end of a buffer.
    /// <para>
    /// <b>Was sixty-four, which a real humanoid immediately exceeded.</b> The Unreal-convention skeleton
    /// these characters use is sixty-five joints once it has finger chains, so the first properly dressed
    /// villager was turned away by one bone. A hundred and twenty-eight leaves room for twist and IK bones
    /// on top of a full hand rig, and costs 2 MB a frame slot at the body cap — which is nothing beside
    /// being unable to load the asset.
    /// </para>
    /// </remarks>
    private const int SkinnedBoneCapacity = 128;

    private static byte[][] BuildCascadePushes()
    {
        var pushes = new byte[ShadowCascades.Count][];
        for (var c = 0; c < pushes.Length; c++) pushes[c] = new byte[CascadePushSize];
        return pushes;
    }
    private PipelineHandle casterPipeline, skyPipeline, presentPipeline;
    private ShaderProgramHandle casterShader;
    private FullscreenPass fullscreen = null!;

    // <b>Smoke, and the first thing in this scene that is not opaque.</b> Its own pipeline because it needs
    // two states nothing else here does — alpha blending, and a depth test that does not write — and its
    // own shader pair because it is the first surface whose fourth colour channel means opacity rather than
    // which material it is. Drawn last of the solids so it blends over a finished frame.
    private ShaderProgramHandle smokeShader;
    private PipelineHandle smokePipeline;
    private InstanceBuffer smokeBuffer = null!;
    private InstancedBatch smokeBatch = null!;
    // <b>Contact shadows: one soft dark disc per object where it meets the ground.</b> Its own pipeline for
    // the same two reasons the smoke needs one — alpha blending and a depth test that does not write — and
    // its own mesh, a fan whose texture coordinate carries the radius so the fragment stage can fade
    // without being told where the instance is.
    private ShaderProgramHandle contactShader;
    private PipelineHandle contactPipeline;
    private InstanceBuffer contactBuffer = null!;
    private InstanceBuffer selectionBuffer = null!;
    private InstanceBuffer selectionPlateBuffer = null!;
    private InstancedBatch contactBatch = null!;

    private PipelineHandle selectionDecalPipeline;
    private ShaderProgramHandle selectionDecalShader;
    private InstancedBatch selectionDecalBatch = null!;
    private InstancedBatch selectionPlateBatch = null!;
    private readonly List<InstanceData> selectionInstances = new();

    /// <summary>Square markers, kept apart from the round ones because they are a different mesh.</summary>
    private readonly List<InstanceData> selectionPlateInstances = new();
    // <b>The far level of detail for trees, and the reason a screenful of them stopped being nine
    // seconds.</b> Measured at a wide zoom: 9,330 trees in view at 5,940 triangles each is 55M triangles a
    // frame, drawn again for the shadow map, for a frame time of 125 ms. Eight triangles each is 75k.

    /// <summary>
    /// The stand-ins every tree casts its shadow from, near ones included.
    /// </summary>
    /// <remarks>
    /// <b>A tree was being drawn twice: once as itself and once into the shadow map.</b> Measured, the near
    /// band was 8.4M triangles in the scene pass and the same again in the sun's — and the sun's map is 2048
    /// texels over a couple of hundred metres, so a nine-centimetre texel cannot tell a leaf-card canopy
    /// from a faceted blob. Casting the stand-in halves the cost of every tree in view for a difference
    /// nobody can see.
    /// </remarks>

    private readonly List<InstanceData> contactInstances = new();
    private readonly byte[] contactPush = new byte[96];   // viewProj, fade range, camera

    private readonly Hearths hearths = new();
    // <b>What was actually sent, so the geometry line cannot describe a frame that was not drawn.</b> It
    // recomputed the fog range from the dial, which stopped being the whole story the moment the mist
    // started multiplying it — and a diagnostic that recomputes a value instead of reporting it is a
    // diagnostic that will eventually disagree with the shader. §51's recurring finding, in the instrument.
    private Vector4 sentFog;
    private readonly Vector4[] hearthLights = new Vector4[Hearths.MaximumLights];
    private int hearthLightCount;
    private int habitationLights;
    private int treesDrawn;

    /// <summary>Tree nodes in the world, tree nodes the draw loop reached, and trees actually drawn.</summary>
    /// <remarks>
    /// Three counts because the gaps between them are the question: alive-minus-offered is what the loop skipped
    /// before ever calling DrawTree, and offered-minus-drawn is what DrawTree itself refused.
    /// </remarks>
    private int treeNodesAlive;
    private int treesOffered;

    /// <summary>Time inside the tree branch, and inside the undergrowth it draws, in Stopwatch ticks.</summary>
    /// <remarks>
    /// <b>Split because the node phase is six kinds of work under one number.</b> It read 16.4 ms at a wide
    /// zoom and every guess about why — the species lookup, the woodland field, the frustum test — was a guess.
    /// A phase that names one thing and measures six is the same fault as the `overlay` label that turned out
    /// to be the whole render graph.
    /// </remarks>
    private long treeTicks;

    /// <summary>How many trees were actually timed, so the sample can be scaled back to the whole.</summary>
    private int treeSamples;

    /// <summary>
    /// How much of the per-tree work to actually do, for attributing the node phase's cost.
    /// </summary>
    /// <remarks>
    /// <b>An ablation, because six readings of the code named nothing.</b> Every function in the tree path
    /// measures cheap on inspection — the heightfield is a bilinear array read, the woodland and country fields
    /// are baked grids, the tier is one grid index, a placement is two SIMD matrix multiplies, and a model turns
    /// out to have 1.8 parts so a submission is under four appends. Summed, that is about a third of what the
    /// phase costs, and a profiler could not close the gap either: <c>sample</c> cannot symbolise JIT frames and
    /// macOS CoreCLR writes no perf map.
    /// <para>
    /// So the remaining honest measurement is subtraction. Level 2 does the iteration, the height sample and
    /// the frustum test and stops; level 1 adds everything computed per surviving tree; level 0 adds the
    /// submission. The differences attribute the phase without needing to know which line inside each stage is
    /// responsible — and unlike a guess, a difference is the number you would actually save by removing it.
    /// </para>
    /// </remarks>
    private int treeWork;

    /// <summary>Cycles <see cref="treeWork"/> and reports the node phase at each level, then exits.</summary>
    private bool treeProfile;

    private readonly double[] treeWorkMilliseconds = new double[3];
    private readonly int[] treeWorkFrames = new int[3];
    private readonly int[] treeWorkOffered = new int[3];
    private readonly int[] treeWorkDrawn = new int[3];
    private long undergrowthTicks;

    /// <summary>Trees the frustum threw away after the coarse bound let them through.</summary>
    private int treesOutOfView;

    /// <summary>Trees within twenty metres of the camera that the frustum refused. Should be near zero.</summary>
    private int treesNearRejected;

    /// <summary>
    /// The camera's frustum, as six outward planes, rebuilt each frame.
    /// </summary>
    /// <remarks>
    /// <b>Everything was being culled by distance from the camera's focus, which is a disc — and a camera
    /// looks at a wedge.</b> At a working standoff the wedge is about a third of the disc, so two trees in
    /// three were being drawn behind the viewer or off the sides of the screen. Distance culling is the
    /// right first cut because it is one subtraction; it is not the last one.
    /// <para>
    /// The planes are pulled straight out of the view-projection, which is the standard trick and worth
    /// stating because it looks like magic: a row of that matrix is the linear functional whose sign tells
    /// you which side of a clip plane a point is on, so the planes are sums and differences of its rows.
    /// </para>
    /// <para>
    /// <b>Expanded by a margin, because a shadow caster need not be visible.</b> A tree behind the camera
    /// can still throw its shadow across what the camera sees, and a tier's instance list feeds both the
    /// scene and the sun's pass. Culling tightly would make shadows appear and vanish as you pan, which is
    /// far worse than drawing some geometry nobody sees — so the test is loose by the width of a long
    /// shadow.
    /// </remarks>
    private readonly Vector4[] frustumPlanes = new Vector4[6];

    private void UpdateFrustum(Matrix4x4 viewProjection)
    {
        var m = viewProjection;
        // Row-vector convention: the clip-space coordinates are v * M, so a clip plane is a combination of
        // the matrix's columns as read here.
        Plane(0, m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41);
        Plane(1, m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41);
        Plane(2, m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42);
        Plane(3, m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42);
        Plane(4, m.M13, m.M23, m.M33, m.M43);
        Plane(5, m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43);

        void Plane(int index, float x, float y, float z, float w)
        {
            var length = MathF.Sqrt(x * x + y * y + z * z);
            if (length < 1e-6f) length = 1f;
            frustumPlanes[index] = new Vector4(x / length, y / length, z / length, w / length);
        }
    }

    /// <summary>How much slack the frustum test is given, in metres. See UpdateFrustum.</summary>
    private const float FrustumMarginMetres = 45f;

    private bool InView(Vector2 at, float ground, float height, float radius)
    {
        var centre = new Vector3(at.X, ground + height * 0.5f, at.Y);
        var reach = radius + height * 0.5f + FrustumMarginMetres;
        foreach (var plane in frustumPlanes)
        {
            if (plane.X * centre.X + plane.Y * centre.Y + plane.Z * centre.Z + plane.W < -reach)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>How many trees each level of detail drew, which is the shape of the frame's tree budget.</summary>
    private (int Near, int Mid, int Far, int Deep) treeTiers;
    private (int Instances, long Triangles, long Casters) stagedLoad;
    // viewProj, camPos, sunDir, sunLight, skyLight, fog, haze, then the fog of war's four dial blocks and the
    // wind. As with the world block, this length is also the declared push-constant range, so the two cannot
    // disagree.
    //
    // <b>Smoke carries the veil's dials because it is hidden by the veil.</b> It was the last thing on the map
    // still escaping it — a chimney is a settlement's position and a plume shows from further off than the
    // building under it — and the arithmetic is shared with world.frag through Shaders/veil.glsl rather than
    // copied, for the reason lean.glsl exists: the two shaders have different push layouts, so a function that
    // read the dials out of a block could only ever live in one of them.
    private readonly byte[] smokePush = new byte[240];

    // Solid things: buildings, heaps, hand-built walls, trees. Held as a list rather than
    // batched directly because each one is drawn twice — once lit into the scene and once
    // depth-only into the shadow map — and the two draws must agree exactly or a building
    // casts a shadow from somewhere it is not.
    private readonly List<InstanceData> propInstances = new();
    // Everything drawn as a cylinder: bodies, their loads, and tree trunks.
    private readonly List<InstanceData> unitInstances = new();
    // Canopies. Foliage is the one thing in the scene a box cannot stand in for — a woodland of drums
    // reads as a machine yard — and it is also what makes the wood line legible from across the map,
    // which is the whole of Stage B's interface.
    private readonly List<InstanceData> canopyInstances = new();
    // The art. Buildings, fields, crops, trees, heaps and bodies, each a PropModel drawn once into the
    // shadow map and once into the scene. The greybox boxes and cylinders below are what these replaced,
    // and they are still what a debug overlay is drawn with.
    private SettlementArt? art;
    private InstancedBatch propBatch = null!;
    private InstancedBatch canopyBatch = null!;
    private InstancedBatch[] propCasters = null!, unitCasters = null!, canopyCasters = null!;
    private InstanceBuffer propBuffer = null!;
    private InstanceBuffer canopyBuffer = null!;
    private InstanceBuffer[] propCasterBuffers = null!, unitCasterBuffers = null!, canopyCasterBuffers = null!;
    private readonly List<InstanceData>[] propCasterInstances = CascadeInstanceLists();
    private readonly List<InstanceData>[] unitCasterInstances = CascadeInstanceLists();
    private readonly List<InstanceData>[] canopyCasterInstances = CascadeInstanceLists();

    // viewProj, camPos, sunDir, sunVP, fog, shadow, light, haze, sunTint, skyAmbient, groundAmbient,
    // hazeAway, hazeToward, wind, hearth, then a vec4 per fire in the village. Every one of the five palette
    // entries used to be a constant in
    // world.frag, which is why a year looked like one afternoon — see Rendering/Atmosphere.cs. This length
    // is also the declared push-constant range, in OnLoad, so the two cannot drift apart.
    /// <summary>
    /// Where the cascade block starts in <see cref="worldPush"/>: after the hearth lights, which are the
    /// block's variable-length tail. Appended rather than inserted so not one existing offset moves — the
    /// alternative renumbers thirteen writes and two shader declarations to save nothing.
    /// </summary>
    private const int CascadeBlockOffset = 336 + Hearths.MaximumLights * 16;

    /// <summary>
    /// Two further view-projections (the third is uSunShadowVP at 96), then the sides, the texel fractions,
    /// the split distances, the camera's own axis, the fog of war's grid span and the two layer densities, the
    /// cloud the veil is drawn as, how that cloud is lit and pulled about, and the deep bank's own four.
    /// </summary>
    private const int CascadeBlockSize = 64 * 2 + 16 * 10;

    /// <summary>
    /// Where the skinned stride sits: the last sixteen bytes of the world push, appended after everything
    /// else for the reason the cascade block was. x is how many bone matrices one body owns, which is how
    /// <c>world_skinned.vert</c> finds a body's slice of the shared palette. Zero while nothing is skinned.
    /// </summary>
    private const int SkinStrideOffset = CascadeBlockOffset + 64 * 2 + 16 * 9;

    private readonly byte[] worldPush = new byte[CascadeBlockOffset + CascadeBlockSize];
    private readonly byte[] skyPush = new byte[128];     // invViewProj, camPos, sunDir, zenith, horizon
    private readonly byte[] gradePush = new byte[32];    // grade, then the eye's night response

    /// <summary>A coarse bound on how far a tree is worth frustum-testing, squared. Not a visibility rule.</summary>
    private float treeCullBoundSquared = 1f;

    /// <summary>How far from the focus the sun's box still contains a caster, squared.</summary>

    /// <summary>Ground colouring the built instances were made with, so a slider forces a rebuild.</summary>

    /// <summary>Side of the sun's shadow map, in texels.</summary>
    private const int ShadowMapSize = 2048;

    /// <summary>Samples per pixel in the scene pass.</summary>
    /// <remarks>
    /// Four. Two is visibly not enough on a scene made entirely of hard geometric edges, and eight buys
    /// almost nothing over four for four times the bandwidth on a frame that is already resolving a
    /// 16-bit-per-channel target.
    /// </remarks>
    /// <summary>
    /// Scene multisampling, four unless a run says otherwise.
    /// </summary>
    /// <remarks>
    /// <b>A setting rather than a constant since §88 pointed at fragments.</b> The peak at 200-300 m is
    /// fragment-bound, and the most expensive fragment work in the frame is a 4x resolve of a scene whose
    /// content at that standoff is a mush of subpixel trees — the case where antialiasing costs most and buys
    /// least. Whether that trade is worth changing is a look judgement, so this exists to let the look be
    /// judged at each setting rather than argued about at one.
    /// <para>
    /// One sample is not "MSAA off with a spare resolve": there is nothing to resolve, so the scene renders
    /// straight into the target the present pass samples and the resolve attachment is not declared at all. A
    /// single-sample source with a resolve declared is invalid, and the graph does not check.
    /// </para>
    /// </remarks>
    private readonly int msaaSamples = 4;

    // The penumbra width and the normal-offset distance both live in the shaders, next to the ambient
    // and the sun, because they are part of a look rather than facts about the scene. What the shaders
    // need from here is the map's geometry: how big a texel is as a fraction of the map, and how many
    // metres the map covers — between them, how many centimetres one texel is worth, which is the scale
    // every bias in the technique is measured in.


    /// <summary>
    /// Side of the box the sun's shadow map covers, in metres — a slider, not a constant.
    /// </summary>
    /// <remarks>
    /// Centred on what the camera is looking at rather than on the map, because the map is 600 m and one
    /// 2048 map over that is 0.3 m a texel, which makes every shadow edge a visible staircase. The default
    /// is a little wider than the camera can see at full zoom-out. The cost of a small box is that the sun
    /// stops casting outside it, which is why it is a dial: crispness against how far shadows reach.
    /// </remarks>
    /// <summary>How far the camera is looking down, shared by the camera and the sun's box.</summary>
    private const float CameraElevation = 0.82f;

    /// <summary>
    /// How far from the focus the furthest visible patch of ground is, in metres.
    /// </summary>
    /// <remarks>
    /// Derived rather than chosen, from the four things that actually decide it: how far back the camera
    /// stands, how far down it looks, how wide its lens is and how wide the window is. The far edge of the
    /// visible ground sits at <c>h / tan(elevation − halfFov)</c> from the point beneath the eye, the eye is
    /// <c>cos(elevation) · d</c> from the focus, and the far corner is that distance and the half-width at
    /// that range in quadrature.
    /// <para>
    /// This is the number everything else about seeing distance ought to be written against, and none of it
    /// was.
    /// </para>
    /// </remarks>
    /// <summary>The window's logical size, read once a frame instead of dozens of times.</summary>
    /// <remarks>
    /// <b>Asking the host costs about half a millisecond, and eighteen call sites were asking it.</b> That is
    /// not a number anybody would guess: it is a property that looks like a field, and it crosses into the
    /// window library to answer. <see cref="VisibleGroundRadius"/> reads it, and that in turn is read by
    /// <c>DetailRadius</c> and <c>GroundDrawRadius</c>, both of which were being evaluated <em>inside the
    /// per-chunk loop</em> — three host queries per chunk, twenty-five chunks, thirty-eight milliseconds a
    /// frame of doing nothing.
    /// <para>
    /// Which is why a wooded map looked eight times more expensive than a pastoral one: the wooded map draws
    /// more chunks. The trees were never the cost. Measured before this: 3.5M triangles at 85 ms, against 8.6M
    /// at 22 ms earlier in the same renderer — a tenth of the geometry at four times the price, which is the
    /// shape of a per-item constant rather than of geometry.
    /// </para>
    /// </remarks>
    private (int Width, int Height) windowSize = (1280, 720);

    private float VisibleGroundRadius
    {
        get
        {
            var (width, height) = windowSize;
            var aspect = height > 0 ? width / (float)height : 1.78f;
            var half = camera.VerticalFieldOfView * 0.5f;
            var pitch = MathF.Max(half + 0.05f, CameraElevation);
            var eyeHeight = MathF.Sin(pitch) * cameraDistance;
            var farAlong = eyeHeight / MathF.Tan(pitch - half) - MathF.Cos(pitch) * cameraDistance;
            var slant = eyeHeight / MathF.Sin(pitch - half);
            var halfWidth = slant * MathF.Tan(half) * aspect;
            return MathF.Sqrt(farAlong * farAlong + halfWidth * halfWidth);
        }
    }

    /// <summary>
    /// The sun's box, sized to what can be seen rather than to a number chosen once.
    /// </summary>
    /// <remarks>
    /// <b>It was a flat 150 m, which is the same bug the far plane already had a comment about.</b> Measured
    /// against the visible ground radius at the three zooms the camera allows:
    /// <list type="bullet">
    /// <item>closest, 31 m back — visible radius 43 m against a 75 m half-box, <b>1.73× oversized</b>, so
    /// the shadow map spends two thirds of its texels on ground nobody is looking at;</item>
    /// <item>default, 46 m back — visible radius 64 m, <b>1.16×</b>. Correct, and this is where 150 came
    /// from;</item>
    /// <item>furthest, 78 m back — visible radius 109 m against 75, <b>0.69×</b>, so <b>the box is smaller
    /// than the view and shadows are simply missing at the edges.</b></item>
    /// </list>
    /// The last of those is a visible bug rather than a quality question, and it is the one a fixed number
    /// guarantees: right at the zoom it was tuned for and wrong at both ends. Tracking the view gives 5.0 cm
    /// texels pulled in and shadows that exist pulled out.
    /// <para>
    /// The margin is for casters standing <em>outside</em> the view whose shadows fall into it. At a sun
    /// elevation of 42° a shadow is 1.11× the caster's height, and the tallest thing here is a tree at
    /// about six metres, so eight metres covers it with room to spare.
    /// </para>
    /// </remarks>
    /// <summary>
    /// How far outside the view a caster can stand and still reach into it, in metres.
    /// </summary>
    /// <remarks>
    /// <c>height / tan(elevation)</c>, which is the length of a shadow — so this grows as the sun drops and
    /// the margin never has to be re-guessed. At 42° a seven-metre tree throws 7.8 m; at 22° it throws 17.3.
    /// The slider is a floor under it rather than the value, because a number that is correct at one sun
    /// angle and silently wrong at every other is the mistake this file has now made three times.
    /// </remarks>
    private float ShadowReachMetres
    {
        get
        {
            var degrees = look.SunFollowsTheYear ? sky.SunElevationDegrees : look.SunElevationDegrees;
            // <b>Clamped, or the box breathes and the shadows pop.</b> A shadow's length is
            // height / tan(elevation), which runs away near the horizon: at six degrees a seven-metre tree
            // throws sixty-six metres. Letting the box follow that means it swells and shrinks every dawn
            // and dusk, and since the box's size sets the texel size, every shadow in the scene changes
            // resolution as the sun moves — which is a good part of what still read as choppy.
            //
            // Twenty degrees is the floor: below it the margin stops growing and shadows from casters
            // further out than that are simply missing, which nobody can see at a raking sun because the
            // things casting them are edge-on and tiny.
            var elevation = MathF.Max(20f, degrees) * MathF.PI / 180f;
            return MathF.Max(look.ShadowMarginMetres, look.TallestCasterMetres / MathF.Tan(elevation));
        }
    }

    /// <summary>
    /// How far out the world is drawn in full: trees, shadows and where the haze finishes.
    /// </summary>
    /// <remarks>
    /// <b>One radius, because three numbers that must agree cannot each be chosen separately.</b> Reported
    /// as the tree cull "misaligning with the camera", and it was: the zoom pulls back to 240 m, at which
    /// the visible ground reaches 337 m — while trees were culled at a ceiling of 220 m and the haze did not
    /// start until <c>cameraDistance × 1.8</c>, which is 432 m. So the world ended in a hard circle a
    /// hundred metres inside the view with nothing to hide it.
    /// <para>
    /// Everything about seeing distance now derives from this: the shadow box is twice it, trees and scrub
    /// are culled at it, and the haze reaches full strength <em>at</em> it — so the edge where detail stops
    /// is the edge where there is nothing left to see through. Which is also what makes the ceiling
    /// affordable: pulled all the way out the shadow map is spread over 480 m and its texels are coarse, and
    /// it does not matter, because the fog got there first.
    /// </para>
    /// </remarks>
    private float DetailRadius => MathF.Min(
        CoarsestShadowReachMetres,
        // <b>From the relief-corrected reach, not the flat one, because the box has to cover what is drawn.</b>
        // Sized from Sees it covered 173 m while trees reached 460 — so shadows stopped in a circle in the
        // middle of the view and everything beyond it stood in flat light. Reported from the chair as only a
        // disc of the view being shaded, which is exactly what a box centred on the focus and smaller than the
        // draw distance looks like.
        MathF.Max(look.MinimumDetailRadiusMetres, VisibleReach + ShadowReachMetres));

    /// <summary>
    /// The furthest the sun's box may reach before its texels are coarser than the dial allows.
    /// </summary>
    /// <remarks>
    /// <b>The cap was in metres and the thing it protects is measured in centimetres per texel.</b> The box is
    /// <c>2 × DetailRadius</c> across and <see cref="ShadowMapSize"/> texels wide, so a reach limit and a texel
    /// limit are the same statement — but only one of them survives a change to the shadow map's resolution.
    /// Doubling the map with the old dial bought nothing; with this one it buys reach, automatically, which is
    /// what anybody doubling it wanted.
    /// <para>
    /// 240 m at 2,048 texels is 23.4 cm a texel, so the dial's default reproduces the old ceiling exactly. What
    /// changes is what the number means and what it stays true under.
    /// </para>
    /// </remarks>
    private float CoarsestShadowReachMetres =>
        look.CoarsestShadowTexelCentimetres / 100f * ShadowMapSize * 0.5f;

    /// <summary>
    /// How far the ground itself is drawn, which is a different question from how far it is shadowed.
    /// </summary>
    /// <remarks>
    /// <b>Ground chunks were culled against <see cref="DetailRadius"/>, and that conflated two budgets.</b>
    /// The detail radius is capped by a look dial because it sizes the sun's box, and a box stretched to a
    /// kilometre has metre-wide texels — so the cap is real and has to stay. But it was also deciding how much
    /// ground existed, which meant pulling the camera back past the cap made the world end in a circle instead
    /// of showing more of itself. The same class of bug this file already has a note about at the far plane.
    /// <para>
    /// Uncapped, because ground is the cheapest thing on the screen: a chunk is sixty-four render cells a side
    /// at whatever step the map's size implies, so eight thousand triangles, and the whole of an 1800 m canvas
    /// is under two hundred thousand. Nothing about drawing the ground was ever what cost anything — foliage
    /// was.
    /// </para>
    /// </remarks>
    private float GroundDrawRadius => Sees * GroundBoundShare;

    /// <summary>
    /// How far this camera can see ground: the one distance every other draw distance is a fraction of.
    /// </summary>
    /// <remarks>
    /// <b>There were a dozen of these, each chosen on its own, and that is the bug class this file has now hit
    /// six times.</b> The far plane, the detail radius, the ground draw radius, the shadow box, the tree bound,
    /// the scatter radius, the contact radius — every one a distance from a 2D <c>cameraFocus</c>, every one
    /// picked or tuned separately, and <see cref="GeometryLine"/> exists solely to print "the distances that are
    /// supposed to agree with each other", which is this file admitting the problem in code.
    /// <para>
    /// So there is one measurement and the rest are ratios to it. A ratio cannot silently disagree with the
    /// view the way a metre value can, and the shares below say what they mean: <em>contact shadows are paid for
    /// out to seven tenths of what can be seen</em> is a sentence; <em>ninety metres</em> is a number that was
    /// right at one zoom.
    /// </para>
    /// <para>
    /// It does not fix the deeper fault — this is still 3D frustum information collapsed into a scalar, which is
    /// what let a radius centred on the focus disagree with a trapezoid reaching past it. What it does is make
    /// the collapse happen <b>once</b>, in one place, where the next person can see it.
    /// </para>
    /// </remarks>
    private float Sees => VisibleGroundRadius;

    /// <summary>
    /// How far ground can actually be seen, once the map's relief is allowed for.
    /// </summary>
    /// <remarks>
    /// <b><see cref="Sees"/> is a flat-plane answer, and this is the correction every user of it needs.</b> That
    /// calculation intersects the frustum's bottom edge with a <em>horizontal plane through the focus</em>, so
    /// it is exact on a plain and short everywhere else: looking down a valley, the real ground lies below that
    /// plane and carries on well past where the plane was cut.
    /// <para>
    /// <b>The ground cull was given this correction today and the trees were not, which is the whole bug.</b>
    /// Ground chunks reached <c>Sees + span / descent</c> and trees reached <c>2 x Sees</c>, so on relief the
    /// grass was drawn where the trees were culled — and it got worse the closer the camera came, because Sees
    /// shrinks with camera distance while the hill in front of you does not. Reported from the chair as zooming
    /// in and seeing less, and confirmed by a switch that draws every tree: with the bound gone the picture is
    /// right, and the frustum was refusing twelve trees out of thirty-four thousand.
    /// </para>
    /// <para>
    /// A hill <c>span</c> metres proud of the focus plane meets the same bottom edge <c>span / tan(pitch −
    /// halfFov)</c> further out, which is the term. Seventh flat-ground constant in a world with hills, and the
    /// first one to be fixed by putting the correction where <em>both</em> callers have to go through it.
    /// </para>
    /// </remarks>
    private float VisibleReach
    {
        get
        {
            var half = camera.VerticalFieldOfView * 0.5f;
            var descent = MathF.Max(0.08f, MathF.Tan(MathF.Max(half + 0.05f, CameraElevation) - half));
            return Sees + labFloorSpan.Span / descent;
        }
    }

    /// <summary>
    /// The span of view-ray distances over which this camera can see ground at all, in metres.
    /// </summary>
    /// <remarks>
    /// <b>The cascades split this, not the frustum, and the difference is two wasted cascades.</b> Slicing
    /// 0 to the shadowed depth is what every CSM tutorial does, and it assumes a camera standing among the
    /// things it looks at. This one is not: it hangs a hundred metres back at forty-seven degrees, so the
    /// nearest ground on screen is already most of the way to the focus, and a slice from half a metre to
    /// thirty is empty air. Painted by cascade, that showed as no red anywhere on the map and the coarsest
    /// map shading nearly everything — three cascades costing three passes and doing one cascade's work.
    /// <para>
    /// Flat-ground trigonometry, which is the whole of what this file keeps getting wrong, so the relief span
    /// is applied to both ends: a hill rising toward the camera brings the near edge in, and one falling away
    /// pushes the far edge out. Same correction VisibleReach makes, for the same reason.
    /// </para>
    /// </remarks>
    private (float Near, float Far) GroundBand
    {
        get
        {
            var half = camera.VerticalFieldOfView * 0.5f;
            var pitch = MathF.Max(half + 0.05f, CameraElevation);
            var eyeHeight = MathF.Sin(pitch) * cameraDistance;
            // <b>The relief pad is capped at a share of the standoff, and it has to be.</b> Taken raw, a 30 m
            // map under a camera 34 m up says the nearest ground could be four metres away — which is true if
            // a hilltop happens to sit directly beneath the eye, and if it does the camera is inside the
            // terrain and worse things are already wrong. Uncapped it hands the near cascade nearly the whole
            // band back, which is the bug this property exists to fix.
            var span = MathF.Min(labFloorSpan.Span, eyeHeight * 0.4f);
            // The bottom of the screen looks down most steeply, so it strikes the ground nearest.
            var near = MathF.Max(2f, eyeHeight - span) / MathF.Sin(pitch + half);
            // The far end keeps the whole span, uncapped: ground falling away from the camera really is that
            // much further along the ray, and cutting the far edge short is what leaves a wood unshadowed.
            var far = (eyeHeight + labFloorSpan.Span) / MathF.Sin(MathF.Max(0.04f, pitch - half));
            far = MathF.Min(far, camera.FarPlane);
            return (Math.Clamp(near, 1f, far * 0.75f), far);
        }
    }

    /// <summary>Coarse bound on ground chunks, as a share of what can be seen. The frustum decides.</summary>
    /// <remarks>
    /// Twice, because it is no longer a visibility rule — chunks are frustum-tested against their own height
    /// range — and its only remaining job is to keep the far half of a large map out of the loop. It used to be
    /// <c>visible × 1.08 + 12</c>, which was a visibility rule, and being one is what made it wrong on relief.
    /// </remarks>
    private const float GroundBoundShare = 2f;


    /// <summary>Metres between candidate ground-cover positions.</summary>
    /// <remarks>
    /// Tighter than the trees, because a tuft of grass is a few centimetres and stops being a tuft well before
    /// a trunk stops being a trunk.
    /// </remarks>
    private const float ScatterSpacingMetres = 2.4f;

    /// <summary>How many ground-cover candidates this loop will walk in a frame.</summary>
    /// <remarks>
    /// The real limit on the scatter, and the thing the old 110 m radius was a proxy for. A budget in cells
    /// survives a change to the spacing; a radius in metres silently changes how much work it means the moment
    /// the spacing moves.
    /// </remarks>
    private const float CoverCellBudget = 8_000f;

    /// <summary>
    /// How far ground cover is generated, as a share of what can be seen.
    /// </summary>
    /// <remarks>
    /// Not a visibility cull but a generation bound — the scatter walks a grid of candidate positions, so this
    /// decides how many cells it visits. Under one because a tuft of grass stops reading as a tuft well before
    /// a trunk stops reading as a trunk, which is the same argument the old 110 m constant was making without
    /// being able to say it relative to anything.
    /// </remarks>
    private const float ScatterShare = 0.85f;

    // Ground-cover placement is deterministic and the expensive questions behind it — patch noise, country,
    // slope, hollow and occupancy — have exactly the same answers while the camera and map stand still. Cache
    // the resulting placements; the PropModels are still repopulated each frame because their buffers are
    // frame-local, but the eight-thousand-cell procedural search is not repeated for an unchanged view.
    private readonly List<(int Kind, Matrix4x4 Model)> scatterPlacements = new();
    private SimulationWorld? scatterWorld;
    private int scatterTerrainRevision = -1;
    private int scatterPlacementRevision = -1;
    private Vector2 scatterFocus = new(float.NaN, float.NaN);
    private float scatterRadius = float.NaN;

    /// <summary>
    /// How far contact shadows are paid for, as a share of what can be seen.
    /// </summary>
    /// <remarks>
    /// A close-up effect that costs by the pixel: every disc is overdraw on ground already shaded, and at range
    /// the thing it grounds is a few pixels tall. Faded over its last third, so the bound is never a line.
    /// </remarks>
    private const float ContactShare = 0.7f;

    // <b>SunOrthoExtent is gone, and so is the single box that replaced it.</b> The extent was 2 x
    // DetailRadius — a square sized from a flat-plane radius around the focus. Then it was one box fitted to
    // the whole frustum's corners, which was correct and could not be sharp: one box wide enough to reach the
    // far corners is a box whose texels are too coarse near the camera, and capping its width to keep the
    // texels left the far corners unshadowed. Three boxes is the answer to that, not a better single one.
    // See FitCascade and ShadowCascades; what each box measures is reported by cascadeSideMetres.

    private const float SunDistance = 220f;

    /// <summary>
    /// Direction toward the sun, from the elevation and bearing on the look sliders.
    /// </summary>
    /// <remarks>
    /// Two angles rather than a vector, because those are the two things somebody adjusting the light
    /// actually wants to say — how high and from where — and a normalised triple is neither.
    /// </remarks>
    /// <summary>
    /// The light, worked out once a frame from the date and the sun's own cycle.
    /// </summary>
    /// <remarks>
    /// Cached per frame rather than recomputed per use, because the shadow box, the sky, the world shader
    /// and the panel all ask for it and they must every one of them get the same answer — a sun that moved
    /// between the shadow pass and the scene pass would light the world from one place and shadow it from
    /// another.
    /// </remarks>
    private Atmosphere sky = Atmosphere.For(default, 0.0, 37f, 1f);

    /// <summary>Midday, for the sun mode that holds the time of day still.</summary>
    private static double NoonSeconds => 12.0 / 24.0 * Atmosphere.DayLengthSeconds;

    private Vector3 SunDirection
    {
        get
        {
            if (look.SunFollowsTheYear) return sky.SunDirection;
            var elevation = look.SunElevationDegrees * MathF.PI / 180f;
            var bearing = look.SunBearingDegrees * MathF.PI / 180f;
            var horizontal = MathF.Cos(elevation);
            return Vector3.Normalize(new Vector3(
                horizontal * MathF.Sin(bearing),
                MathF.Sin(elevation),
                horizontal * MathF.Cos(bearing)));
        }
    }
    private TerrainMap? renderedTerrain;
    private int renderedTerrainRevision = -1;
    private SpriteBatch selectionUi = null!;
    private TextureHandle selectionPixel = new(-1);

    /// <summary>
    /// On-screen prompts: what is selected, what is under the pointer, and which key does something.
    /// </summary>
    /// <remarks>
    /// The twenty-line key reference printed at startup is no use while playing. The question a player has
    /// is never "what are all the keys", it is "I have clicked this thing, now what" — and that depends on
    /// what is selected and what is under the cursor, so it is computed from those two.
    /// </remarks>
    private SettlementHud? hud;

    private double simulationAccumulator;
    private float aspect = 16f / 9f;
    private float cameraYaw = MathF.PI * 0.25f;
    private float cameraDistance = 31f;

    /// <summary>Where the wheel has asked the camera to be; <c>cameraDistance</c> eases toward it.</summary>
    private float cameraDistanceTarget = 31f;

    /// <summary>How close the camera may come. Near enough to read one body.</summary>
    /// <remarks>
    /// Eight metres rather than nineteen. Nineteen was the floor for a session about how a crowd moves,
    /// where the thing being judged is a dozen bodies at once; a settlement wants to be looked at from
    /// close enough to see what one villager is carrying.
    /// </remarks>
    private const float CameraNearestDistance = 6f;

    /// <summary>The ground under the focus, eased, which is where the camera aims.</summary>
    /// <remarks>
    /// A field rather than a fresh sample each time because it is eased across frames, and because two
    /// different heights for one focus inside one frame would put the eye and the frustum in different places.
    /// </remarks>
    private float cameraGroundHeight;

    /// <summary>How far back the camera may stand.</summary>
    /// <remarks>
    /// Enough to hold the settlement and its whole tree line, and no more. It was the map's own extent
    /// times 1.35 — eight hundred metres — which is a viewpoint from which a villager is a subpixel and
    /// which, with a fixed far plane, showed nothing at all.
    /// </remarks>
    // Reported as still slightly too far. Past this the settlement is a smudge in the middle of a green
    // field and the shadow map is spread so thin that everything it draws is a suggestion.
    /// <remarks>
    /// <b>Pulled in from a hundred and seventy, because the far end of the zoom was a viewpoint nothing was
    /// built to serve.</b> At a hundred and seventy the camera sees two hundred and thirty-eight metres of
    /// ground, which is nine thousand trees, and every one of them either costs six thousand triangles or
    /// gets swapped for something cheap that the player can see is cheap. A hundred and eighteen sees about
    /// a hundred and sixty-five — the settlement, its fields, its tree line and the shoulder of the nearest
    /// high ground, which is everything a decision is made against.
    /// </remarks>
    /// <remarks>
    /// <b>No longer the ceiling, as of §86 — it is the floor of the ceiling.</b> §82 said in as many words
    /// that the 6-118 m limits were prior judgements rather than protected facts, and stage 0 then moved the
    /// frame twice: the coarse cascades stopped casting real geometry (§84, off by default) and the fog upload
    /// stopped draining the queue (§85, about 10 ms back at every standoff). A cap measured against the frame
    /// as it was in §73 has no authority over the frame as it is now, and holding it while the envelope is
    /// being re-measured means the measurement cannot see past the answer it is trying to check. What the
    /// wheel obeys is now derived from the map; see <see cref="cameraFurthest"/>.
    /// </remarks>
    private const float CameraFurthestDistance = 118f;

    /// <summary>
    /// Share of the world the camera may stand back by, when nothing says otherwise.
    /// </summary>
    /// <remarks>
    /// The map lab's own rule, generalised: the camera sees roughly one and two fifths of its own standoff on
    /// the ground at this pitch, so three quarters of the extent puts the whole map in frame and no more. It
    /// is a bound about the map's size rather than a judgement about what is worth drawing — the judgement is
    /// what --zoom-limit is for, once there is a measured one to pin.
    /// </remarks>
    private const float FurthestShareOfExtent = 0.75f;

    /// <summary>
    /// How far back the camera may actually stand, which the map lab raises.
    /// </summary>
    /// <remarks>
    /// <b>The constant above is a judgement about a settlement, and the lab is not judging a settlement.</b>
    /// A hundred and eighteen metres was measured against everything a decision is made against — the
    /// village, its fields, its tree line, the shoulder of the nearest high ground — and it is exactly right
    /// for that. It is meaningless for a canvas three times the map wide, where the thing being judged is
    /// whether a whole landscape has one idea in it, and you cannot judge that through a hole showing a
    /// seventh of it.
    /// <para>
    /// Set from the extent rather than to a bigger number, because the two questions scale differently: the
    /// game's limit is about how much detail is worth drawing, and the lab's is about fitting the canvas on
    /// the screen. Three quarters of the extent puts the whole thing in frame — the camera sees roughly one
    /// and two fifths of its own standoff on the ground at this pitch, measured off the dressing line.
    /// </para>
    /// <para>
    /// It costs nothing that matters, because the lab has no trees in it: woodland is scattered by the
    /// settlement scenario, and the lab deliberately places nothing. The far end of the game's zoom is
    /// expensive for reasons that are entirely about foliage.
    /// </para>
    /// </remarks>
    private float cameraFurthest = CameraFurthestDistance;

    /// <summary>Metres a second the arrow keys pan, as a share of how far back the camera is.</summary>
    /// <remarks>
    /// A share rather than a speed, because a pan that crosses the screen in a second when zoomed in
    /// takes a minute when zoomed out — what a player means by "pan left" is a fraction of what they can
    /// see, not a distance in metres.
    /// </remarks>
    private const float CameraPanSharePerSecond = 1.1f;

    private bool panLeft, panRight, panUp, panDown;
    private bool turnLeft, turnRight;

    /// <summary>How fast Q and E swing the camera, in degrees a second.</summary>
    /// <remarks>
    /// Continuous rather than a quarter turn a press. The snap was defensible while the buildings were
    /// greybox cubes square to the grid — a quarter turn kept them square — and it is not now: the thing you
    /// turn the camera for is to see round a wood or behind a barn, and that wants the angle you want rather
    /// than the nearest of four. A hundred and ten degrees a second is a little over three seconds for a
    /// full turn, which is quick enough not to wait for and slow enough to stop where you meant.
    /// </remarks>
    private const float CameraTurnDegreesPerSecond = 110f;
    private bool draggingCamera;
    private float mouseX;
    private float mouseY;
    private Vector2 pointerWorld;
    private bool pointerOnTerrain;
    private bool additiveSelection;
    /// <summary>Whether Shift is down. Only crews read it; Ctrl already means "the other thing" everywhere.</summary>
    private bool shiftHeld;
    /// <summary>The sets the player has named. View-layer only, by ruling — see ControlGroups.</summary>
    private readonly ControlGroups crews = new();
    private bool obstacleEditMode;
    private int navigationDebugMode;
    /// <summary>
    /// Nothing, then a selected body's own four proxies, then <b>every collider in the world</b>.
    /// </summary>
    /// <remarks>
    /// <b>The third setting is the one that did not exist, and it is the one everything was doubted
    /// against.</b> This overlay drew the four discs of a selected agent and nothing else — no tree, no
    /// building, no wall, no impassable cell — so the alignment anybody actually suspected, whether a
    /// trunk's collider matches the trunk that is drawn, had never once been visible.
    /// <para>
    /// Everything it draws is read from the collider itself: its own centre, its own shape, its own size.
    /// Nothing is recomputed from the drawing code, because a disc drawn from the same numbers as the mesh
    /// would agree with the mesh by construction and prove nothing at all.
    /// </para>
    /// </remarks>
    private int colliderOverlay;
    private bool velocityDebug;
    private bool pathDebug;
    private bool stateDebug;
    private bool timingDebug;
    private int stressScenarioIndex = -1;
    private int penScenarioVariant;
    private int frameCount;
    private int rolledAt = -1;
    private (float Floor, float Span) labFloorSpan;

    /// <summary>
    /// The node under the pointer, decided once a frame.
    /// </summary>
    /// <remarks>
    /// <b>Computed here rather than in the HUD, because two answers to "what is the pointer over" is one
    /// answer too many.</b> The panel picked its own with its own radius; the marker on the ground would have
    /// picked another. That is the shape of half the bugs in this file — a number correct in the layer that
    /// owns it and disagreeing with the same number next door — and a hover highlight that lights a different
    /// building from the one the panel is describing is exactly how it would show up.
    /// </remarks>
    private NodeId hoveredNode = NodeId.None;

    /// <summary>What the player has clicked on, which is the beginning of the building layer.</summary>
    /// <remarks>
    /// Buildings were selectable only in the sense that the panel described whatever the pointer happened to
    /// be over — nothing was <em>held</em>, so there was nothing to issue an order to and nothing to look at
    /// while deciding. A site you are about to commit timber to is the first thing that needs to stay picked
    /// while you move the mouse somewhere else.
    /// </remarks>
    private NodeId selectedNode = NodeId.None;

    /// <summary>The body under the pointer, so hovering reads the same on a villager as on a building.</summary>
    private AgentId? hoveredAgent;
    private float furthestChunk;
    private double groundSubmitMs;
    private int groundLayersDrawn;

    /// <summary>Outcrops offered to the frustum, and how many survived it. "Can I see any stone from here."</summary>
    private int outcropsSeen;
    private int outcropsDrawn;
    private int rolls;
    private double nextTimingReport;

    /// <summary>
    /// The faction the person at the keyboard is playing.
    /// </summary>
    /// <remarks>
    /// Zero, and a constant rather than a setting — every scenario founds the player's settlement first and
    /// nothing yet lets somebody play the other side. It exists as a name so that the places which must ask
    /// "is this mine" say so, rather than assuming the answer the way selection did until §133.
    /// </remarks>
    private static readonly FactionId PlayerFaction = new(0);

    /// <summary>§8's autonomy time as a word, matching what the settlement report prints.</summary>
    private static string Seasons(float seasons) =>
        float.IsPositiveInfinity(seasons) ? "no net draw" : $"{seasons:F1} seas";

    /// <summary>The neighbour's player, when one was asked for. Null in a single-settlement village.</summary>
    private AI.Planning.Planner? opponentBot;

    /// <summary>
    /// A bot on the player's own settlement, when the whole map is being left to play itself.
    /// </summary>
    /// <remarks>
    /// §140. Two rule-bots on one map is the state §7's acceptance test actually wants to watch: a settlement
    /// nobody is steering shows what the rules produce, rather than what the rules plus a person's attention
    /// produce. It is the same class with the same verbs — there is deliberately no second implementation for
    /// "the player's" side, because a bot that ran the player's settlement differently would be measuring the
    /// wrong thing.
    /// </remarks>
    private AI.Planning.Planner? playerBot;

    private readonly bool startOpponent;

    /// <summary>Whether every settlement on the map is left to a rule-bot, the player's included.</summary>
    /// <summary>
    /// Prints what every selected body is doing, once a second.
    /// </summary>
    /// <remarks>
    /// <b>For settling arguments about what a body is up to.</b> "They just stand next to trees" and "the
    /// animation glitches" are both reports about state that cannot be read off the screen: a body chopping
    /// at a tenth of a unit a second looks identical to a body doing nothing, and a pose flickering between
    /// two actions looks like a rendering fault rather than a state that is genuinely changing. One line a
    /// second per selected body says which it is — the action, the assignment, whether the jobs layer counts
    /// it as working, how far it is from its place, and how fast it is moving.
    /// </remarks>
    private readonly bool bodyLog;

    private float bodyLogDue;

    private readonly bool handsOffEverybody;

    /// <summary>Routing as it stood at the last report, so each second is a window rather than a total.</summary>
    private RouteAttribution routesAtLastReport = new();

    private long routeLogAtLastReport;

    private sealed record TerrainSurfaceLayer(
        Mesh Mesh,
        InstanceBuffer Buffer,
        InstancedBatch Batch,
        Vector4 Color,
        bool Blend,
        bool Water = false);

    public RtsGameLoop(
        int exitAfterFrames,
        float reliefAmplitudeMetres = 0f,
        float startingZoomMetres = 0f,
        bool traceMovement = false,
        bool startTerrainLab = false,
        bool debugAll = false,
        bool timingsOnly = false,
        float extentMeters = DefaultWorldExtentMeters,
        float compression = DefaultCompression,
        bool startVillage = false,
        bool startMapLab = false,
        Region? region = null,
        Archetype? archetype = null,
        uint? mapSeed = null,
        int rollEveryFrames = 0,
        bool profileTreeWork = false,
        bool fogOfWar = false,
        bool showFogCells = false,
        bool fogDisabled = false,
        bool performanceRun = false,
        PerformanceCameraMotion performanceCameraMotion = PerformanceCameraMotion.Still,
        float performanceHour = -1f,
        bool performanceVsync = false,
        int performanceCascades = -1,
        bool shadowProxies = false,
        bool performanceBlockingUpload = false,
        float zoomLimitMetres = 0f,
        int performanceTierBias = 0,
        int msaaSamples = 4,
        (float Mid, float Far)? treeCrowd = null,
        bool cheapTrees = false,
        // Appended rather than slotted in beside the other village flags: this list is passed positionally
        // from Program.cs, and inserting a bool into the middle of twenty-nine arguments silently rebinds
        // every one after it. The compiler caught it because the neighbours happen to be an int and a float.
        bool startOpponent = false,
        bool handsOffEverybody = false,
        LookSettings.SunMotion? sunMotion = null,
        MapTuning.MapOverlay? overlay = null,
        bool eroded = false,
        bool bodyLog = false)
    {
        this.performanceRun = performanceRun;
        this.performanceCameraMotion = performanceCameraMotion;
        this.performanceHour = performanceHour;
        this.performanceVsync = performanceVsync;
        this.startOpponent = startOpponent;
        // Hands off implies there is somebody else to watch: a lone bot settlement is the headless year leg
        // with a camera on it, which --twovillages already answers better.
        this.bodyLog = bodyLog;
        this.handsOffEverybody = handsOffEverybody;
        if (sunMotion is { } motion) look.Motion = motion;
        if (overlay is { } asked) mapTuning.Overlay = asked;
        // §152's switch, reachable from the played game as well as the sweep: the two generators have to be
        // comparable in the chair for the same reason they have to be comparable in a table.
        this.eroded = eroded;
        this.shadowProxies = shadowProxies;
        this.performanceBlockingUpload = performanceBlockingUpload;
        this.cheapTrees = cheapTrees;
        this.performanceTierBias = Math.Clamp(performanceTierBias, 0, 3);
        // <b>The other end of the art lever.</b> These two thresholds decide when a copse stops keeping every
        // triangle; at their maxima (12 and 20) nothing on a village map is ever crowded enough to coarsen, so
        // every tree draws in full. Together with --perf-tier-bias 3, which puts every tree at the coarsest
        // level the chain holds, they bracket what tree detail costs across the whole zoom range — which is
        // the measurement a decision about cheaper art needs, and it does not require the art to exist yet.
        if (treeCrowd is { } crowd)
        {
            look.TreeCrowdMid = Math.Clamp(crowd.Mid, 1f, 12f);
            look.TreeCrowdFar = Math.Clamp(crowd.Far, 2f, 20f);
        }
        // Powers of two only, and only the ones a device is required to support for a colour target.
        this.msaaSamples = msaaSamples switch
        {
            1 or 2 or 4 or 8 => msaaSamples,
            _ => throw new ArgumentOutOfRangeException(
                nameof(msaaSamples), msaaSamples, "MSAA must be 1, 2, 4 or 8 samples."),
        };
        if (performanceCascades >= 0)
        {
            performanceCascadeMask = (1 << Math.Min(performanceCascades, ShadowCascades.Count)) - 1;
        }
        this.rollEveryFrames = rollEveryFrames;
        // <b>Reachable from the command line because the panel cannot be clicked from a gate.</b> The fog's
        // masks are checked by reading the SCOUTED counts out of a run. A village is the playable game and
        // therefore starts with its information boundary in force; bare movement and map-lab runs stay clear
        // unless a fixture asks for fog explicitly. --fogcells adds the overlay and turns on the timings that
        // print the line, while the ordinary village default does not turn the diagnostic panel on.
        // --nofog wins over the village default, because the ablation is asking what the fog costs and the
        // answer cannot be obtained on the one map where it cannot be switched off.
        fogSettings.Enabled = !fogDisabled && (startVillage || fogOfWar || showFogCells);
        fogSettings.ShowCells = showFogCells;
        if (fogOfWar || showFogCells) timingDebug = true;
        // The ablation starts at the cheapest level and works up, so nothing it measures was warmed by the
        // level above it. See treeWork.
        treeProfile = profileTreeWork;
        if (profileTreeWork)
        {
            treeWork = 2;
            timingDebug = true;
        }

        mapLab = startMapLab;
        if (region is { } chosen) labRegion = chosen;
        if (archetype is { } wanted)
        {
            labArchetype = wanted;
            // Naming an archetype means asking about that archetype, so the canvas is filled with it rather
            // than with the family.
            labPinned = true;
        }

        if (mapSeed is { } given) labSeed = given;
        worldExtentMeters = extentMeters;
        clock.Compression = compression;
        // A camera sized for a thirty-metre square shows a kilometre map as a patch of
        // ground, which is the one thing this session must not do — the body has to be
        // watched crossing real distances as well as stepping round a doorway.
        cameraDistance = cameraDistanceTarget = 46f;
        camera.FarPlane = MathF.Max(150f, extentMeters * 3f);
        // What is left on the panel is what is still a question. Everything the locomotion
        // work settled — solver relaxation, congestion decay, contact yielding, the recovery
        // timings — is a constant in the code with its measurements beside it, and everything
        // that is a fixed proportion of another number is now written as that proportion.
        // What is left on the panel is what is still a question. Everything the locomotion
        // work settled — solver relaxation, congestion decay, contact yielding, the recovery
        // timings — is a constant in the code with its measurements beside it, and everything
        // that is a fixed proportion of another number is now written as that proportion.
        // Forty-five sliders to eight, and none of the eight is derivable from another.
        // <b>Order is visibility.</b> Sixteen groups and about ninety controls render as one column, so a
        // group's position in this list decides whether anybody can reach it — `map` was thirteenth, behind
        // seventy-five sliders, and reported from the chair as missing. It was registered the whole time,
        // which is why `Describe()` above prints the roster: "never registered" and "registered and off the
        // end of a long panel" look identical from the chair and want opposite fixes.
        //
        // So the list is ordered by what is currently being worked on rather than by when it was written.
        // Which map to generate is the live question; the sun's seventeen knobs are settled.
        tunables = new ObjectTunables(
            mapTuning,
            cascades,
            bodyFeel,
            clock,
            woodland,
            new SettlementSettings(),
            raids,
            look,
            fogSettings,
            new WallSettings(),
            new RoutingSettings(),
            new GroupSettings());
        // Behind --debug-all: it answers "is my control registered, and as what", which is a question you ask
        // when something is missing and never otherwise. Printed unconditionally it was ninety controls of
        // startup noise.
        if (debugAll) Console.WriteLine($"  panel controls: {tunables.Describe()}");
        this.exitAfterFrames = exitAfterFrames;
        this.reliefAmplitudeMetres = reliefAmplitudeMetres;
        // <b>The panel starts where the command line pointed, or the first roll would contradict it.</b> A
        // slider that owns a value must be initialised from that value: launch with --archetype Estuary and a
        // panel sitting at index zero would have thrown the archetype away the moment somebody pressed Space.
        // The amplitude matters more. Every calibrated scenario runs on flat ground, so a hard-coded 32 on the
        // dial would mean one keypress silently generated terrain under a measurement that was taken without
        // it — the dial reads zero on a flat village, and stays there until somebody asks otherwise.
        // <b>The village plays a generated map; only the headless runs want a plain.</b> --relief-amplitude
        // defaults to zero, which is exactly right for the calibrated scenarios — every rate in the economy was
        // measured on flat ground and a fertility of exactly one depends on there being no soil field — and
        // exactly wrong for the thing a person opens to look at. It is why the map on screen kept reading
        // FLAT GROUND and 0 M RELIEF, why there was never any stone (bare rock needs relief to be bare on),
        // and why the woodland never showed its shaped form.
        //
        // The flag still wins when given, and so does the panel. This is only what happens when nobody said.
        // <b>The lab gets the village's relief default too, because a flat canvas is not a map.</b> §160:
        // this line covered --village only, so --maplab with no --relief-amplitude opened at zero amplitude —
        // which takes Apply's early return, produces no landforms and no drainage at all, and presents a
        // perfectly flat plane to somebody who came to compose terrain. Reported from the chair as "all maps
        // are entirely flat", and it was true of every map the lab could make.
        if ((startVillage || startMapLab) && reliefAmplitudeMetres <= 0f)
        {
            this.reliefAmplitudeMetres = DefaultVillageRelief;
        }
        mapTuning.Archetype = labArchetype;
        mapTuning.Region = labRegion;
        mapTuning.ReliefMetres = this.reliefAmplitudeMetres;
        this.startingZoomMetres = startingZoomMetres;
        // <b>The wheel's ceiling, derived rather than declared.</b> Three things feed it, and each has been a
        // bug on its own: the map's extent (a canvas three times the map wide cannot be judged through a hole
        // showing a seventh of it), an explicit --zoom-limit (so a measured envelope can be pinned without a
        // rebuild), and the requested opening standoff — because --zoom 150 opening at 150 and then having the
        // first notch of wheel refuse to return there is the worst of both answers, and is exactly what
        // happened.
        cameraFurthest = zoomLimitMetres > 0f
            ? MathF.Max(CameraNearestDistance + 1f, zoomLimitMetres)
            : MathF.Max(
                MathF.Max(CameraFurthestDistance, worldExtentMeters * FurthestShareOfExtent),
                startingZoomMetres);
        if (performanceRun)
        {
            // <b>The label is the case.</b> Two runs whose numbers differ and whose labels do not are two
            // numbers nobody can attribute, which is how the last wide-view figure ended up being quoted
            // without anybody being sure which zoom it was taken at.
            var standoff = startingZoomMetres > 0f ? startingZoomMetres : 46f;
            var light = performanceHour >= 0f ? $"{performanceHour:F0}h" : "live";
            var label =
                $"{performanceCameraMotion.ToString().ToLowerInvariant()}-{light}-" +
                $"{standoff:F0}m-fog{(fogSettings.Enabled ? "on" : "off")}" +
                (performanceCascades >= 0 ? $"-cast{performanceCascades}" : string.Empty) +
                (shadowProxies ? "-proxy" : string.Empty) +
                (performanceBlockingUpload ? "-blockingupload" : string.Empty) +
                (performanceTierBias > 0 ? $"-tier+{performanceTierBias}" : string.Empty) +
                (this.msaaSamples != 4 ? $"-msaa{this.msaaSamples}" : string.Empty) +
                (treeCrowd is { } shown ? $"-crowd{shown.Mid:F0}.{shown.Far:F0}" : string.Empty) +
                (cheapTrees ? "-cheaptrees" : string.Empty);
            // A quarter of the run, capped: long enough to cover first presentation, terrain meshing and the
            // first cover resolve, short enough that a sixty-frame smoke still reports a steady window.
            var warmUpFrames = exitAfterFrames > 0 ? Math.Clamp(exitAfterFrames / 4, 1, 120) : 120;
            performance = new PerformanceRun(label, warmUpFrames, performanceVsync);
        }

        movementTrace = traceMovement || debugAll ? new LiveMovementTrace() : null;
        if (debugAll)
        {
            timingDebug = true;
            colliderOverlay = 2;
            velocityDebug = true;
            pathDebug = true;
            stateDebug = true;
            navigationDebugMode = 4;
        }
        simulation = new SimulationWorld(worldExtentMeters);
        cameraFocus = Vector2.Zero;
        if (startVillage) raiders = new RaidDirector(raids);
        if (mapLab)
        {
            // <b>Time stands still in the lab, at mid-morning.</b> It had a running clock, so a canvas left
            // alone for a few minutes was being judged at half past six at night — and a landscape at night is
            // a landscape you cannot see. The lab is for looking at ground, and the light it is looked at in
            // should be a constant rather than whatever the session has drifted to.
            clock.Compression = 0f;
            simulation.StartAtSeconds(34_000f);
            // Terrain only. The lab is about ground, and a settlement standing on it is both a distraction
            // and a lie — the whole point of a window is that nothing has been placed for it yet.
            LoadRelief();
            // <b>The opening standoff was set inside the settlement scenario, which the lab does not run.</b>
            // So --zoom silently did nothing here and the camera sat at the default forty-six metres — which
            // reads as the zoom being clamped, since the wheel could not get out of it either. Worth the note:
            // the symptom was "the canvas scenario will not zoom out" and one of its two causes was a limit,
            // the other was an argument that never arrived.
            cameraDistance = cameraDistanceTarget = startingZoomMetres > 0f
                ? MathF.Min(startingZoomMetres, cameraFurthest)
                : cameraFurthest * 0.72f;
            ReportPick();
            SurveyWindows();
        }
        else if (startVillage)
        {
            LoadSettlementScenario();
        }
        else if (startTerrainLab)
        {
            LoadTerrainScenario();
        }
        else
        {
            SpawnScenarioAgents(30);
        }
        if (movementTrace is not null) Console.WriteLine("  live movement trace: ON");
        if (timingsOnly) timingDebug = true;
        if (debugAll) Console.WriteLine("  all overlays ON: congestion, colliders, velocity, paths, states, timings");
    }

    /// <summary>
    /// Selects everybody with nothing to do, which is who the HUD has been counting all along.
    /// </summary>
    /// <remarks>
    /// <b>The panel said "5 SPARE" and there was no way to get at them.</b> Spare labour is the one thing
    /// a settlement always has some of and the one thing a player is always looking for — it is what a new
    /// farm is staffed from, what a cart is bought for, and what a building site is finished by — so
    /// telling somebody they have five and making them hunt for which five is a strange thing to have
    /// shipped. Tab, because every letter on the board was taken twice over.
    /// <para>
    /// Idle is defined here exactly as the HUD defines it — no assignment and not mid-interrupt — because
    /// two definitions of spare would drift and the number on screen is the promise this has to keep.
    /// </para>
    /// </remarks>
    private void SelectSpareHands()
    {
        var spare = new List<AgentId>();
        if (additiveSelection) spare.AddRange(selection.Selected);
        foreach (ref readonly var agent in simulation.Agents.All)
        {
            if (!agent.IsAlive || agent.Faction.Value != 0 || agent.Role != AgentRole.Villager) continue;
            if (agent.Jobs.HasAssignment || agent.Jobs.IsInterrupted) continue;
            spare.Add(agent.Id);
        }

        selection.ReplaceWith(spare);
        Console.WriteLine(
            selection.Selected.Count == 0
                ? "  nobody spare — everybody has a job"
                : $"  {selection.Selected.Count} unit(s) selected");
    }

    /// <summary>
    /// Sets, extends or recalls the crew on a digit.
    /// </summary>
    /// <remarks>
    /// <b>Three verbs on one key, and the third one is the point.</b> Ctrl sets, Shift adds, the bare digit
    /// recalls — the arrangement every RTS has used for thirty years, and worth matching exactly because the
    /// affordance being tested here is muscle memory rather than novelty. §82's complaint was that a player
    /// had to reconstruct a set by marquee every time they wanted it; a crew is the answer to that and to
    /// nothing else.
    /// <para>
    /// Recalling the crew you are already holding jumps the camera to it. Standard behaviour, arrived at
    /// here without a double-tap timer: "the selection already equals this crew" is exactly the state a
    /// second press produces, so it can be asked directly instead of timed. One less piece of state, and it
    /// also does the right thing when the set was reached some other way.
    /// </para>
    /// <para>
    /// A crew is not a cohort. Recalling one and then ordering it produces a cohort, the same as any other
    /// selection would; the crew keeps its membership through that order and through the next one, which is
    /// the whole difference between the two representations.
    /// </para></remarks>
    private void Crew(int slot)
    {
        if (additiveSelection)
        {
            // Snapshot rather than the live set. Every other place that hands a set of ids anywhere in this
            // game sorts by id first, and a crew that stored them in hash order would be the one exception
            // for no reason — the order is invisible today, and something that reads it later should find
            // the same order this codebase means everywhere else.
            crews.Assign(slot, selection.Snapshot());
            var size = crews.Members(slot, simulation.Agents).Count;
            Console.WriteLine(
                size == 0
                    ? $"  crew {slot} cleared"
                    : $"  crew {slot} is now {size} unit(s)");
            return;
        }

        if (shiftHeld)
        {
            var before = crews.Members(slot, simulation.Agents).Count;
            crews.Add(slot, selection.Snapshot());
            var after = crews.Members(slot, simulation.Agents).Count;
            Console.WriteLine($"  crew {slot}: {before} -> {after} unit(s)");
            return;
        }

        // Asked before the recall, because recalling is what makes it true.
        var alreadyHeld = crews.Holds(slot, selection.Selected, simulation.Agents);
        var members = crews.Members(slot, simulation.Agents);
        if (members.Count == 0)
        {
            Console.WriteLine($"  crew {slot} is empty — Ctrl+{slot} sets it to the selection");
            return;
        }

        selection.ReplaceWith(members);
        if (!alreadyHeld)
        {
            Console.WriteLine($"  crew {slot}: {members.Count} unit(s) selected");
            return;
        }

        var centroid = Vector2.Zero;
        foreach (var id in members) centroid += simulation.Agents.Get(id).Position;
        cameraFocus = centroid / members.Count;
        Console.WriteLine($"  crew {slot}: {members.Count} unit(s) — camera moved to them");
    }

    private void SpawnScenarioAgents(int count)
    {
        MovementStressScenarios.Populate(simulation, count, issueGroupMove: false);
    }

    /// <summary>
    /// Generates the ground, and reports whether there is any. The one place relief is made.
    /// </summary>
    /// <remarks>
    /// <b>Relief before anything is placed on it, because everything placed is laid out against the
    /// ground.</b> Trees are refused where nobody can stand and the site is chosen from the map, so
    /// generating the land after populating it would be describing a different world from the one the people
    /// were put in.
    /// <para>
    /// Off by default until §54's milestones are through: relief is the parameter the whole migration plan
    /// rests on, and zero is exactly the ground every calibrated scenario was measured against. Which is also
    /// why this returns a bool rather than being called unconditionally — "there is no relief" has to stay a
    /// distinct case from "there is relief of zero metres".
    /// </para>
    /// </remarks>
    private bool LoadRelief()
    {
        if (reliefAmplitudeMetres <= 0f) return false;
        // <b>One generator, for the lab and for play.</b> The village used to build its ground with
        // <see cref="ReliefPlan.For"/> — six landforms scattered from a seed — while the lab built composed
        // archetypes, so every judgement made in the lab was about terrain nobody would ever play on. The
        // legacy path stays for <c>--relief</c>, which sweeps amplitudes and needs a shape whose steepest grade
        // is known analytically.
        var plan = ReliefPlan.FromLayout(
                // <b>A field of statements, not one statement stretched.</b> Ask a canvas three times the map
                // wide for a single archetype and you get one ridge system a mile and a half long, which no
                // 600 m window can contain any of — reported from the chair as "there's just no interesting
                // 600 m map possible". Filled at frame scale instead, so every window has one or two
                // statements in it and neighbouring windows are different maps.
                // <b>One statement, at map scale, and the canvas is gone.</b> The larger canvas existed to be
                // <em>searched</em>: archetypes were not legible at 600 m, so the answer was to generate a lot
                // of ground and go hunting for a framing that happened to contain something. Now that the areal
                // primitives and the ridge heightfield make an archetype read at map scale — which is where
                // <c>--shapes</c> judges them — searching solves a problem that no longer exists.
                //
                // The canvas's one real contribution was the fragment property: a river arriving from off-map,
                // a ridge carrying on past the edge. That never needed a bigger canvas, it needed the right
                // boundary conditions, and those are already right. The inherited catchment is an absolute two
                // tiles' worth of upstream country, so a 600 m map's river comes from somewhere by
                // construction, and a ridge that leaves the frame is a path whose end is outside it.
                //
                // What replaces searching is re-rolling. A 600 m map generates in about a third of a second,
                // so twenty seeds cost less than one canvas did — and choosing between whole maps is a better
                // question than choosing between framings of one.
            MapLayout.Composed(labArchetype, worldExtentMeters, labSeed, reliefAmplitudeMetres),
            worldExtentMeters,
            labSeed);
        // <b>Before the country is painted, because the region decides what the country is.</b> The
        // classifier reads its wetness ranks, so setting it afterwards would paint one climate's surfaces and
        // then claim another's.
        //
        // No longer lab-only. The whole point of the lab was to judge maps the game would then play, and a lab
        // that generates through one path while the game generates through another judges nothing.
        simulation.Terrain.SetRegion(labRegion);
        // <b>The builder's dials, or the defaults when nothing has an opinion.</b> §154: the sweep and the
        // gate ask for none of these, so the figures §152 measured are the figures they keep measuring.
plan.DrainageFirst = !eroded && mapTuning.DrainageFirst;
        if (plan.DrainageFirst)
        {
            plan.ChannelFallPer100M = mapTuning.ChannelFallPer100M;
            plan.ValleyFlank = mapTuning.ValleyFlank;
            plan.ValleyShoulderMetres = mapTuning.ValleyShoulderMetres;
            plan.Tributaries = (int)MathF.Round(mapTuning.Tributaries);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        plan.Apply(simulation.Terrain);
        var shaped = clock.Elapsed.TotalMilliseconds;
        labPlan = plan;
        SettlementScenarios.PaintCountry(simulation);
        var painted = clock.Elapsed.TotalMilliseconds - shaped;
        // <b>Measured here rather than read off the renderer's country field, which does not exist yet.</b>
        // That field is rebuilt inside the render pass and guards on the device being up, so anything asking
        // about country during generation gets an empty grid and the answer "all meadow" — which is exactly
        // what the window survey reported before this line existed: one kind of country on every framing of
        // a canvas with seven on it.
        (labFloor, labSpan) = SettlementScenarios.InteriorReliefOf(simulation);
        labFloorSpan = (labFloor, labSpan);

        // <b>The verdict, once, here.</b> §154: measured after the country is painted because walkability is
        // read off the navigation raster and an unpainted map answers "all crossable" — the exact mistake
        // §151 made in the sweep and had to correct. Rasterised first for the same reason: PaintCountry
        // writes surfaces and bumps the revision, and nothing sees them until somebody rebuilds.
        simulation.RebuildTerrainNavigation();
        labCriteria = TerrainCriteria.Measure(simulation);
        Console.WriteLine($"  criteria: {labCriteria}");
        foreach (var shortfall in labCriteria.Value.Shortfalls())
        {
            Console.WriteLine($"    SHORT: {shortfall}");
        }
        var measured = clock.Elapsed.TotalMilliseconds - shaped - painted;
        // <b>Skipped in the lab, and it was fourteen seconds of every roll.</b> The lab has no agents in it:
        // nothing routes, nothing is placed, nothing asks whether a cell is walkable. Rebuilding the walkable
        // rectangles and the region partition over thirteen million cells to answer questions nobody is going
        // to ask is the single largest thing a roll was paying for.
        if (!mapLab) simulation.RebuildTerrainNavigation();
        var navigated = clock.Elapsed.TotalMilliseconds - shaped - painted - measured;
        Console.WriteLine($"  relief: {plan.Describe()}");
        // The pick, on every run rather than only in the lab, so a map somebody liked while playing can be
        // asked for again.
        var picked = RegionProfile.For(labRegion);
        Console.WriteLine(
            $"  map: --region {labRegion} --archetype {labArchetype} --mapseed {labSeed}");
        Console.WriteLine($"    {plan.Layout?.Sentence ?? "no layout"}");
        Console.WriteLine($"    {picked.Name}: {picked.Character}");
        Console.WriteLine(
            $"    generated in {clock.Elapsed.TotalMilliseconds:F0} ms — shape {shaped:F0}, " +
            $"country {painted:F0}, measure {measured:F0}, navigation {navigated:F0}");
        if (simulation.Terrain.Drainage is { } water)
        {
            var bodies = water.Bodies();
            var report = bodies.Length == 0
                ? "none"
                : string.Join(
                    ", ",
                    bodies.Select(body =>
                        $"{body.AreaMetres2 / 10_000f:F1} ha ({body.SpanMetres:F0} m across, " +
                        $"{body.DeepestMetres:F1} m deep)"));
            var (widest, where) = water.Widest();
            // <b>Crossable against blocked, because that is what a connector is for.</b> A map can have a
            // beautiful river and be two maps if nothing can get over it, and no figure printed so far could
            // tell the difference — water area, thickness and depth are all silent on whether there is a way
            // across.
            var grid = simulation.Navigation.Transform;
            var fordable = 0;
            var blocked = 0;
            for (var z = 0; z < grid.Height; z += 6)
            for (var x = 0; x < grid.Width; x += 6)
            {
                switch (simulation.Terrain.Surface(new GridCell(x, z)))
                {
                    case TerrainSurface.Shallows: fordable++; break;
                    case TerrainSurface.Impassable: blocked++; break;
                }
            }

            Console.WriteLine(
                $"    water: {report}; thickest {widest:F0} m at ({where.X:F0}, {where.Y:F0}); " +
                $"{fordable} crossable samples against {blocked} blocked");
        }
        return true;
    }

    /// <summary>
    /// Drops a working settlement into the live world, in the middle of a harvest.
    /// </summary>
    /// <remarks>
    /// The same arrangement <c>--settlement</c> asserts about for a year, laid out tighter so it fits a
    /// screen: half the ring radius, and started at day 155 rather than at day zero because spring
    /// brings in no grain by design and a demo that opened there would be a demo of waiting. What there
    /// is to watch is hands standing in fields, carts deciding for themselves which yard to empty next,
    /// and the granary filling — and then the season turning and the wood going the other way.
    /// </remarks>
    private void LoadSettlementScenario()
    {
        // <b>One recipe, named, shared with the fixtures.</b> These numbers used to be written here and the
        // headless legs had their own: nineteen people at dawn against thirteen at mid-morning, which is why
        // §120's fixture could not reproduce a stall from the chair. See SettlementScenarios.VillageRecipe.
        var recipe = SettlementScenarios.VillageRecipe.AsPlayed;
        simulation = new SimulationWorld(worldExtentMeters);
        simulation.StartAtSeconds(recipe.StartSeconds);
        selection.Clear();
        // <b>Relief before the settlement, because the settlement is laid out against the ground.</b> Trees
        // are refused on ground nobody can stand on and the site is chosen from the map, so generating the
        // land after populating it would be describing a different world from the one the people were put
        // in. Off by default until §54's milestones are through: this is the parameter the whole migration
        // plan rests on, and zero is exactly the ground every calibrated scenario was measured against.
        if (LoadRelief())
        {
            var plan = labPlan!;
            // Where the landforms actually are, relative to where the settlement is going. A generator
            // that scatters shapes over a map says nothing about whether any of them is near the place the
            // player will be looking at, and "the ground looks flat" is the same observation as "the site
            // landed on the plain" until somebody prints both.
            var site = SettlementScenarios.ChooseSite(simulation, worldExtentMeters);
            foreach (var landform in plan.Landforms)
            {
                Console.WriteLine(
                    $"    landform at ({landform.Centre.X:F0}, {landform.Centre.Y:F0}) — " +
                    $"{landform.Height:F1} m tall, summit {landform.TopRadius:F0} m, " +
                    $"flank {landform.FlankWidth:F0} m at grade {landform.FlankGrade:F2}, " +
                    $"stretch {landform.Stretch:F1}, lobing {landform.Lobing:F2}, " +
                    $"{Vector2.Distance(landform.Centre, site) - landform.BaseRadius:F0} m " +
                    "of plain between it and the site");
            }

            // <b>And how wet it is, which is the number that was missing when the village drowned.</b> Height
            // and grade both looked perfect for a site in the middle of a river: it is the flattest ground on
            // the map and its height is unremarkable. Nothing printed said "there is half a metre of water on
            // it", so nothing caught it until somebody watched their men wade.
            var wet = simulation.Terrain.Drainage is { } water
                ? water.LevelAt(site) - simulation.Terrain.SampleHeight(site)
                : 0f;
            Console.WriteLine(
                $"    the site itself stands at {simulation.Terrain.SampleHeight(site):F2} m, " +
                $"grade {simulation.Terrain.SampleGrade(site):F3}, " +
                $"under {MathF.Max(0f, wet) * 100f:F0} cm of water");
        }

        // <b>The land first, then a place in it.</b> The village used to be laid at the origin whatever the
        // ground was doing; it is now founded where the terrain says to found — level enough to lay a field,
        // with slope inside a cutter's reach and something at its back. On a map with no relief that is the
        // same corner it always was, to the metre.
        var founded = SettlementScenarios.ChooseSite(simulation, worldExtentMeters);
        SettlementScenarios.Populate(
            simulation,
            recipe.Farms,
            recipe.Woodcutters,
            recipe.Quarriers,
            recipe.Carts,
            recipe.Wagons,
            centre: founded,
            faction: PlayerFaction);

        // <b>And a bot on it too, if the map is being left to play itself.</b> §140.
        playerBot = handsOffEverybody ? new AI.Planning.Planner(PlayerFaction) : null;
        if (playerBot is not null)
        {
            Console.WriteLine(
                $"  hands off: faction {PlayerFaction.Value} is run by a rule-bot as well — " +
                "selection and orders still work, and anything issued is an interrupt the bot leaves alone");
        }

        // <b>A neighbour, and somebody to run it.</b> §133: the pieces for this have all been proved headless
        // — two settlements that feed themselves (§130), per-faction knowledge (§131), and a bot that plays
        // through the player's own verbs (§132) — and none of them had ever been on screen. What a chair run
        // answers that a fixture cannot: whether two settlements READ as two, whether it is clear whose is
        // whose, and whether the knowledge asymmetry means anything a person can perceive.
        //
        // dressMap is false for the second, because Populate paints the biomes and scatters the woodland over
        // the whole map and doing it twice re-forests the first settlement's cleared ground — §130, which cost
        // a session to find and is one parameter to avoid.
        opponentBot = null;
        if (startOpponent)
        {
            var neighbour = SettlementScenarios.NeighbourSite(
                simulation, founded, worldExtentMeters * 0.33f);
            var theirs = new FactionId(1);
            SettlementScenarios.Populate(
                simulation,
                recipe.Farms,
                recipe.Woodcutters,
                recipe.Quarriers,
                recipe.Carts,
                recipe.Wagons,
                centre: neighbour,
                faction: theirs,
                dressMap: false);
            opponentBot = new AI.Planning.Planner(theirs);
            Console.WriteLine(
                $"  opponent: faction {theirs.Value} founded at ({neighbour.X:F0}, {neighbour.Y:F0}), " +
                $"{Vector2.Distance(founded, neighbour):F0} m away, run by a rule-bot");
        }
        cameraFocus = founded;
        cameraDistance = cameraDistanceTarget = startingZoomMetres > 0f ? startingZoomMetres : 78f;
        Console.WriteLine(
            $"  settlement: {simulation.Nodes.LiveCount} nodes, {simulation.Agents.LiveCount} people, " +
            $"{simulation.Date}");
        Console.WriteLine(
            "  watch the panel's settlement scope: what is stored, and how many seasons it lasts");
    }

    private void LoadStressScenario(int count)
    {
        simulation = new SimulationWorld(worldExtentMeters);
        cameraFocus = Vector2.Zero;
        selection.Clear();
        simulationAccumulator = 0;
        MovementStressScenarios.Populate(simulation, count, issueGroupMove: count != 30);
        RebuildTerrainSurfaceLayersIfReady();
        Console.WriteLine($"  stress scenario: {count} agents{(count == 30 ? " (playground)" : " (group move running)")}");
    }

    private void LoadPenEscapeScenario()
    {
        simulation = new SimulationWorld(worldExtentMeters);
        cameraFocus = Vector2.Zero;
        simulationAccumulator = 0;
        stressScenarioIndex = -1;
        var seeds = new[] { 17, 73, 211, 419 };
        var seed = seeds[penScenarioVariant++ % seeds.Length];
        var blockPrimary = penScenarioVariant % 2 == 0;
        var scenario = MovementStressScenarios.PopulatePenEscape(
            simulation,
            seed: seed,
            blockPrimary: blockPrimary);
        RebuildTerrainSurfaceLayersIfReady();
        selection.ReplaceWith(scenario.Agents);
        Console.WriteLine($"  stress scenario: 30-agent pen escape, seed {seed}, " +
                          $"main exit {(blockPrimary ? "body-blocked" : "open")}, " +
                          $"{scenario.ExitCenters.Length - 1} alternate exits");
    }

    /// <summary>
    /// Terrain to move over: the laboratory on the tuned world, a real map on a larger one.
    /// </summary>
    /// <remarks>
    /// The laboratory is a fixture — thirty metres of ramp, hills and pond that two self-tests
    /// assert against — and it stays exactly that. It is not a map, and stretching it over six
    /// hundred metres produced a few chunky rectangles adrift in a plain. Anything bigger than
    /// the world it was drawn for gets terrain drawn to its own scale instead.
    /// </remarks>
    private void LoadTerrainScenario()
    {
        simulation = new SimulationWorld(worldExtentMeters);
        cameraFocus = Vector2.Zero;
        simulationAccumulator = 0;
        stressScenarioIndex = -1;
        var laboratory = worldExtentMeters <= SimulationWorld.DefaultExtentMeters * 1.5f;
        var ids = laboratory
            ? TerrainStressScenarios.Populate(simulation)
            : WorldTerrainScenarios.Populate(simulation);
        RebuildTerrainSurfaceLayersIfReady();
        selection.ReplaceWith(ids);
        Console.WriteLine(laboratory
            ? "  terrain laboratory: road, mud, rough ground, cliff, ramp, and impassable pond"
            : "  world terrain: a ridge with one pass, a lake in a basin, a road through it");
    }

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        // <b>The deadline, from the display rather than from the run, and read here because the host does not
        // exist until now.</b> The first version asked in the constructor and crashed every case with a null
        // reference — a fixture that cannot start is at least loud about it, which is more than the three
        // estimators it replaced managed. A vsync-on case reports its cadence against this; an explicit
        // --perf-refresh still wins, for measuring against a deadline the panel does not have.
        if (performanceRun && PerformanceRun.RefreshMilliseconds <= 0.01 &&
            host.DisplayRefreshHz is { } refreshHz && refreshHz > 0)
        {
            PerformanceRun.RefreshMilliseconds = 1000.0 / refreshHz;
            Console.WriteLine(
                $"  performance fixture: display reports {refreshHz} Hz " +
                $"({PerformanceRun.RefreshMilliseconds:F2} ms)");
        }
        this.graphicsDevice = graphicsDevice;
        vk = (VulkanGraphicsDevice)graphicsDevice;
        if (performanceRun && !performanceVsync)
        {
            // Both halves: the window's own swap interval and the swapchain's present mode. Setting one and
            // not the other leaves the frame capped by whichever was missed.
            host.SetVSync(false);
            vk.VsyncEnabled = false;
            Console.WriteLine("  performance fixture: vsync OFF (Mailbox) — frame times are cost, not cadence");
        }

        // <b>The same stride, with the texture coordinate declared.</b> The world shaders read position and
        // normal only, so the shared layout stopped at two attributes — and a pipeline whose shader declares
        // a third fails to create, with an initialisation error that names nothing. The contact decal needs
        // it: a disc carries its own radius in that channel, which is what lets a fragment fade without
        // being told where its instance is.
        var decalLayout = new VertexLayout(
            Stride: VertexPosition3NormalTexture.Layout.Stride,
            Attributes: new[]
            {
                new VertexAttribute(0, VertexAttributeFormat.Float3, 0),
                new VertexAttribute(1, VertexAttributeFormat.Float3, 3 * sizeof(float)),
                new VertexAttribute(2, VertexAttributeFormat.Float2, 6 * sizeof(float)),
            });
        // <b>Location 2 was already in every one of these buffers, unbound.</b> Every mesh drawn through
        // the world pipeline is a VertexPosition3NormalTexture — the glTF importer, the OBJ importer and
        // the terrain builder all pack that one struct — so its two texture floats have always been
        // uploaded and thrown away. Binding them costs no new vertex format, no wider buffer and no second
        // upload path: props supply their own texture coordinates and never read them back, and the ground
        // supplies how much of itself covers each corner. Which is what a shader input is allowed to be,
        // since the pipeline's attributes need only be a superset of what a stage consumes — the shadow
        // caster and the smoke share this layout and declare neither.
        var meshLayout = new VertexLayout(
            Stride: VertexPosition3NormalTexture.Layout.Stride,
            Attributes: new[]
            {
                new VertexAttribute(0, VertexAttributeFormat.Float3, 0),
                new VertexAttribute(1, VertexAttributeFormat.Float3, 3 * sizeof(float)),
                new VertexAttribute(2, VertexAttributeFormat.Float2, 6 * sizeof(float)),
            });
        // The world shader now reads the sun shadow map (set 0) and needs the camera, the sun, the
        // sun's shadow view-projection and the fog range in both stages, which is 176 bytes rather
        // than the bare view-projection it used to take.
        var shaderInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                new DescriptorSetSlot(0, 1, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                // The two further cascades. Binding 1 stays the wear texture rather than being renumbered,
                // because renaming a binding that works is how a shader and its interface drift apart.
                new DescriptorSetSlot(0, 2, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                new DescriptorSetSlot(0, 3, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                // What the player has scouted. Appended at 4 for the same reason the cascades were appended
                // to the push block: a binding that works is worth more than a tidy numbering.
                new DescriptorSetSlot(0, 4, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                InstanceBuffer.Slot,
            },
            PushConstants: new[]
            {
                // <b>It was four places, not three, and the fourth is what caught out the wind vec4.</b>
                // The block is declared in world.vert, again in world.frag, its size again here, and the
                // payload's own length once more where worldPush is allocated — so editing both shaders and
                // the buffer still bought a draw-time payload-length error, and the number it complained
                // about was this one. The recurring finding in a fifth costume: one fact, four owners.
                //
                // Two of them are now one: the range is the payload's length, because a range that
                // disagrees with the array it describes is never the answer to anything. The shaders still
                // have to be kept in step by hand, since the size lives in their layout.
                new PushConstantRange(ShaderStages.Vertex | ShaderStages.Fragment, 0, worldPush.Length),
            });

        // See the remarks on ShadowCascades.Count: the shader has three of these written out by name.
        if (ShadowCascades.Count != 3)
        {
            throw new InvalidOperationException(
                $"world.frag samples three cascades by name; ShadowCascades.Count is {ShadowCascades.Count}. " +
                "Change both or neither.");
        }

        var shaderDirectory = Path.Combine(AppContext.BaseDirectory, "Shaders");
        byte[] Spv(string name) => File.ReadAllBytes(Path.Combine(shaderDirectory, name));

        // The frame: sun shadow depth → HDR scene → present. The scene target is Rgba16F so
        // the lighting can live above 1.0 and the tonemap has a range to work with; writing
        // the same values straight at the swapchain is what made a sunlit wall and a sunlit
        // roof the same shade of nothing.
        graph = new RenderGraph(vk);
        var fullSize = new MatchSwapchainGraphSize(1.0f);
        // One depth target per cascade, each at the resolution CascadeMapSize gives it.
        for (var c = 0; c < ShadowCascades.Count; c++)
        {
            var side = CascadeMapSize(c);
            cascadeTargets[c] = graph.DepthTarget(
                $"sun-cascade-{c}", new FixedGraphSize(side, side));
        }
        // The scene renders at 4x and resolves to 1x for the present. Everything in this world is
        // untextured geometry, so <b>every</b> edge in the frame is a geometric edge and there is nothing
        // else for the eye to look at — which is why an unresolved frame reads as jagged here far more
        // than it would in a textured scene. MSAA is the whole fix for that, and a resolve is cheaper
        // than any post-process approximation of one.
        // Rgba16F rather than the packed R11G11B10F, and that is measured rather than assumed. The packed
        // format is half the bandwidth and the obvious choice for a 4x target that resolves every frame,
        // and on this device it is <b>slower</b>: 14.2 ms of GPU work against 10.2 for the wider format,
        // repeatably. Presumably the resolve path for it is not the fast one here. Worth re-measuring on
        // other hardware before copying this choice anywhere.
        hdrHandle = graph.ColorTarget("hdr", TextureFormat.R11G11B10F, fullSize);
        if (msaaSamples > 1)
        {
            hdrMsaaHandle = graph.ColorTarget(
                "hdr-msaa", TextureFormat.R11G11B10F, fullSize, samples: msaaSamples);
        }

        sceneDepthHandle = graph.DepthTarget("scene-depth", fullSize, samples: msaaSamples);

        var casterInterface = new ShaderInterface(
            Slots: new[] { InstanceBuffer.Slot },
            PushConstants: new[]
            {
                // A light matrix and the wind. The wind is here so a caster leans exactly as the scene shader
                // leans the same geometry — see shadow_caster.vert.
                new PushConstantRange(ShaderStages.Vertex, 0, CascadePushSize),
            });
        // <b>The skinned pair's interfaces: the same slots plus a bone palette at set 2.</b> Set 0 is the
        // world's textures and set 3 is the instance buffer, so set 2 is the one the engine leaves free —
        // which is what lets a body carry a palette without any of the other draws learning about it.
        var boneLayout = new UniformBlockLayout(
            TotalSize: SkinnedBodies.MaxBodies * SkinnedBoneCapacity * 64,
            Members: new[]
            {
                new UniformBlockMember(
                    "bones", 0, SkinnedBodies.MaxBodies * SkinnedBoneCapacity * 64, ElementStride: 64),
            });
        var boneSlot = new DescriptorSetSlot(
            2, 0, ShaderResourceType.StorageBuffer, ShaderStages.Vertex, BlockLayout: boneLayout);
        var skinnedInterface = new ShaderInterface(
            // Plus the body's albedo at set 0 binding 5, appended after the maps the plain stage declares —
            // see world_skinned.frag. Bound per primitive, because a character is several materials.
            Slots: shaderInterface.Slots
                .Append(boneSlot)
                .Append(new DescriptorSetSlot(0, 5, ShaderResourceType.SampledImage, ShaderStages.Fragment))
                .ToArray(),
            PushConstants: shaderInterface.PushConstants);
        var skinnedCasterInterface = new ShaderInterface(
            Slots: new[] { InstanceBuffer.Slot, boneSlot },
            PushConstants: new[]
            {
                // A light matrix and the bone stride, which is the same byte count as the leaning caster's
                // matrix-and-wind — see the note in shadow_caster_skinned.vert on why that matters.
                new PushConstantRange(ShaderStages.Vertex, 0, CascadePushSize),
            });
        var skyInterface = new ShaderInterface(
            Slots: Array.Empty<DescriptorSetSlot>(),
            PushConstants: new[] { new PushConstantRange(ShaderStages.Fragment, 0, 128) });
        var contactInterface = new ShaderInterface(
            Slots: new[] { InstanceBuffer.Slot },
            PushConstants: new[]
            {
                new PushConstantRange(ShaderStages.Vertex | ShaderStages.Fragment, 0, contactPush.Length),
            });
        var smokeInterface = new ShaderInterface(
            // The scouted mask, so a plume can be hidden by the same cloud that hides the ground under it.
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                InstanceBuffer.Slot,
            },
            PushConstants: new[]
            {
                new PushConstantRange(ShaderStages.Vertex | ShaderStages.Fragment, 0, smokePush.Length),
            });
        var presentInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Fragment, 0, 32) });

        for (var c = 0; c < ShadowCascades.Count; c++)
        {
            cascadePasses[c] = graph.GraphicsPass($"sun-cascade-{c}")
                .Depth(cascadeTargets[c], LoadOp.Clear, StoreOp.Store)
                .Shader(casterInterface, skinnedCasterInterface)
                .Handle;
        }
        // <b>The pipelines' sample count comes from this pass's colour target</b> — the graph reads it off the
        // first one and registers the surface with it — so switching the target is the whole of switching
        // MSAA. Nothing below needs to know, which is why this branch is two lines rather than a second path.
        var scenePass = graph.GraphicsPass("scene");
        scenePass = msaaSamples > 1
            ? scenePass.Target(hdrMsaaHandle, LoadOp.Clear, StoreOp.Store).ResolveColor(hdrHandle)
            : scenePass.Target(hdrHandle, LoadOp.Clear, StoreOp.Store);
        scenePassHandle = scenePass
            .Depth(sceneDepthHandle, LoadOp.Clear, StoreOp.Store)
            .Read(cascadeTargets[0])
            .Read(cascadeTargets[1])
            .Read(cascadeTargets[2])
            .Shader(skyInterface, shaderInterface, skinnedInterface, smokeInterface, contactInterface)
            .Handle;
        graph.Compile();

        worldShader = vk.CreateShaderProgramFromSpv(
            Spv("world.vert.spv"), Spv("world.frag.spv"), shaderInterface, "rts-world");
        worldPipeline = vk.CreatePipeline(
            new PipelineDescription(
                worldShader,
                meshLayout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                new[] { BlendState.Disabled },
                RenderTarget: graph.GetPassSurface(scenePassHandle)),
            "rts-world");
        // <b>The same shader and the same layout as the world, blending instead of replacing.</b> The
        // ground is drawn twice where two kinds of country meet: an opaque coat in whichever class owns the
        // cell, then the minority classes over it at their own share. Coplanar with what it covers, so the
        // depth test has to be LessEqual — and it still writes depth, because the result is ground and
        // everything standing on it must test against it.
        groundBlendPipeline = vk.CreatePipeline(
            new PipelineDescription(
                worldShader,
                meshLayout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                new[] { BlendState.AlphaBlend },
                RenderTarget: graph.GetPassSurface(scenePassHandle)),
            "rts-ground-blend");
        casterShader = vk.CreateShaderProgramFromSpv(
            Spv("shadow_caster.vert.spv"), Spv("shadow_caster.frag.spv"), casterInterface, "rts-caster");
        casterPipeline = vk.CreatePipeline(
            new PipelineDescription(
                casterShader,
                meshLayout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                // No culling: a shadow caster is a solid, and back-face culling on a depth-only
                // pass throws away the faces nearest the light on anything the camera sees the
                // inside of.
                RasterizerState.NoCulling,
                Array.Empty<BlendState>(),
                RenderTarget: graph.GetPassSurface(cascadePasses[0])),
            "rts-caster");
        // <b>The skinned pair, sharing both fragment stages with their unskinned twins.</b> A body is lit,
        // fogged, shadowed and veiled by exactly the rules everything else in the settlement obeys, because
        // it goes through world.frag unchanged — a second lighting path for people would drift from the
        // first inside a session. Only the vertex stage differs, and only by where the position comes from.
        skinnedShader = vk.CreateShaderProgramFromSpv(
            Spv("world_skinned.vert.spv"), Spv("world_skinned.frag.spv"),
            skinnedInterface, "rts-world-skinned");
        skinnedPipeline = vk.CreatePipeline(
            new PipelineDescription(
                skinnedShader,
                VertexPosition3NormalTextureSkin4Tangent.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                new[] { BlendState.Disabled },
                RenderTarget: graph.GetPassSurface(scenePassHandle)),
            "rts-world-skinned");
        skinnedCasterShader = vk.CreateShaderProgramFromSpv(
            Spv("shadow_caster_skinned.vert.spv"), Spv("shadow_caster.frag.spv"),
            skinnedCasterInterface, "rts-caster-skinned");
        skinnedCasterPipeline = vk.CreatePipeline(
            new PipelineDescription(
                skinnedCasterShader,
                VertexPosition3NormalTextureSkin4Tangent.Layout,
                PrimitiveTopology.Triangles,
                // No culling, for the reason the unskinned caster gives.
                DepthState.LessEqualWrite,
                RasterizerState.NoCulling,
                Array.Empty<BlendState>(),
                RenderTarget: graph.GetPassSurface(cascadePasses[0])),
            "rts-caster-skinned");
        contactShader = vk.CreateShaderProgramFromSpv(
            Spv("contact.vert.spv"), Spv("contact.frag.spv"), contactInterface, "rts-contact");
        selectionDecalShader = vk.CreateShaderProgramFromSpv(
            Spv("selection.vert.spv"), Spv("selection.frag.spv"), contactInterface, "rts-selection");
        selectionDecalPipeline = vk.CreatePipeline(
            new PipelineDescription(
                selectionDecalShader,
                decalLayout,
                PrimitiveTopology.Triangles,
                // The same decal discipline the contact shadow uses, and for the same reasons: tested against
                // the ground so it cannot float over a wall, writing no depth because it is not a thing, and
                // uncoulled so a disc on a slope does not wink out at a grazing angle.
                DepthState.LessEqualNoWrite,
                RasterizerState.NoCulling,
                new[] { BlendState.AlphaBlend },
                RenderTarget: graph.GetPassSurface(scenePassHandle)),
            "rts-selection");
        contactPipeline = vk.CreatePipeline(
            new PipelineDescription(
                contactShader,
                decalLayout,
                PrimitiveTopology.Triangles,
                // Tested against the ground it darkens, writing nothing: a disc is not a thing in the
                // world, it is a mark on the thing under it.
                DepthState.LessEqualNoWrite,
                // No culling, because a disc laid on a slope can be seen from either side at a grazing
                // camera and a one-sided one winks out.
                RasterizerState.NoCulling,
                new[] { BlendState.AlphaBlend },
                RenderTarget: graph.GetPassSurface(scenePassHandle)),
            "rts-contact");
        smokeShader = vk.CreateShaderProgramFromSpv(
            Spv("smoke.vert.spv"), Spv("smoke.frag.spv"), smokeInterface, "rts-smoke");
        smokePipeline = vk.CreatePipeline(
            new PipelineDescription(
                smokeShader,
                meshLayout,
                PrimitiveTopology.Triangles,
                // Tested against the world it drifts through, but writing no depth: a plume is a hundred
                // overlapping puffs and each one occluding the next is the one way to make smoke look like
                // a pile of spheres.
                DepthState.LessEqualNoWrite,
                // The near hemisphere only. A puff fades at its own silhouette, so the far side of the
                // sphere contributes a second, brighter copy of the same fade and doubles every edge.
                RasterizerState.BackFaceCulling,
                new[] { BlendState.AlphaBlend },
                RenderTarget: graph.GetPassSurface(scenePassHandle)),
            "rts-smoke");
        var skyShader = vk.CreateShaderProgramFromSpv(
            Spv("sky.vert.spv"), Spv("sky.frag.spv"), skyInterface, "rts-sky");
        skyPipeline = vk.CreatePipeline(
            new PipelineDescription(
                skyShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.Disabled,
                RasterizerState.NoCulling,
                new[] { BlendState.Disabled },
                RenderTarget: graph.GetPassSurface(scenePassHandle)),
            "rts-sky");
        var presentShader = vk.CreateShaderProgramFromSpv(
            Spv("present.vert.spv"), Spv("present.frag.spv"), presentInterface, "rts-present");
        presentPipeline = vk.CreatePipeline(
            new PipelineDescription(
                presentShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.Disabled,
                RasterizerState.NoCulling,
                new[] { BlendState.Disabled }),
            "rts-present");
        fullscreen = new FullscreenPass(vk, "rts-fullscreen");

        var cubeMesh = CreateMesh(vk, "terrain-cube", Cube.Vertices, Cube.Indices);
        var cylinderMesh = CreateMesh(vk, "agent-cylinder", Cylinder.Vertices, Cylinder.Indices);
        var canopyMesh = CreateMesh(vk, "tree-canopy", Icosphere.Vertices, Icosphere.Indices);

        overlayBuffer = new InstanceBuffer(vk, worldShader, "rts-overlays");
        unitBuffer = new InstanceBuffer(vk, worldShader, "rts-agents");
        overlayBatch = new InstancedBatch(cubeMesh, worldPipeline, overlayBuffer);
        unitBatch = new InstancedBatch(cylinderMesh, worldPipeline, unitBuffer);
        propBuffer = new InstanceBuffer(vk, worldShader, "rts-props");
        propBatch = new InstancedBatch(cubeMesh, worldPipeline, propBuffer);
        // A caster batch needs its own instance buffer: the buffer's material is created
        // against a shader, and the caster's shader is not the world's.
        propCasterBuffers = new InstanceBuffer[ShadowCascades.Count];
        unitCasterBuffers = new InstanceBuffer[ShadowCascades.Count];
        canopyCasterBuffers = new InstanceBuffer[ShadowCascades.Count];
        propCasters = new InstancedBatch[ShadowCascades.Count];
        unitCasters = new InstancedBatch[ShadowCascades.Count];
        canopyCasters = new InstancedBatch[ShadowCascades.Count];
        for (var c = 0; c < ShadowCascades.Count; c++)
        {
            propCasterBuffers[c] = new InstanceBuffer(vk, casterShader, $"rts-props-caster-{c}");
            propCasters[c] = new InstancedBatch(cubeMesh, casterPipeline, propCasterBuffers[c]);
            unitCasterBuffers[c] = new InstanceBuffer(vk, casterShader, $"rts-agents-caster-{c}");
            unitCasters[c] = new InstancedBatch(cylinderMesh, casterPipeline, unitCasterBuffers[c]);
            canopyCasterBuffers[c] = new InstanceBuffer(vk, casterShader, $"rts-canopies-caster-{c}");
            canopyCasters[c] = new InstancedBatch(canopyMesh, casterPipeline, canopyCasterBuffers[c]);
        }
        canopyBuffer = new InstanceBuffer(vk, worldShader, "rts-canopies");
        canopyBatch = new InstancedBatch(canopyMesh, worldPipeline, canopyBuffer);
        // The same sphere the greybox canopies use, which is what a puff of smoke wants to be.
        var discMesh = CreateMesh(vk, "contact-disc", Disc.Vertices, Disc.Indices);
        contactBuffer = new InstanceBuffer(vk, contactShader, "rts-contact");
        selectionBuffer = new InstanceBuffer(vk, selectionDecalShader, "rts-selection");
        selectionPlateBuffer = new InstanceBuffer(vk, selectionDecalShader, "rts-selection-plate");
        contactBatch = new InstancedBatch(discMesh, contactPipeline, contactBuffer);
        selectionDecalBatch = new InstancedBatch(discMesh, selectionDecalPipeline, selectionBuffer);
        var plateMesh = CreateMesh(vk, "selection-plate", SquarePlate.Vertices, SquarePlate.Indices);
        selectionPlateBatch = new InstancedBatch(plateMesh, selectionDecalPipeline, selectionPlateBuffer);
        smokeBuffer = new InstanceBuffer(vk, smokeShader, "rts-smoke");
        smokeBatch = new InstancedBatch(canopyMesh, smokePipeline, smokeBuffer);
        art = SettlementArt.Load(
            vk, worldShader, worldPipeline, casterShader, casterPipeline, ShadowCascades.Count,
            distantShadowProxies: shadowProxies,
            cheapTrees: cheapTrees);
        for (var c = 0; c < skinnedCascadePush.Length; c++) skinnedCascadePush[c] = new byte[CascadePushSize];
        // <b>A rigged body, if the pack has one.</b> Height matched to the prop villager it replaces rather
        // than to the asset's own proportions: 1.45 m is what the locomotion layer was calibrated against,
        // and a body that walks at a different scale from the one the radii were tuned for would make every
        // crowd figure incomparable to every previous one.
        bodies = SkinnedBodies.Load(
            vk,
            Path.Combine(AppContext.BaseDirectory, "Assets", "models"),
            // <b>A dressed body driven by the animation library's clips.</b> Cooked by
            // tools/cook-character.sh: an outfit-compatible Quaternius character (five skinned meshes,
            // which the importer would have reduced to one) joined into a single skin, its bones renamed to
            // the deform naming the 53-bone library uses, and the library's forty-five clips carried onto
            // it. The mannequin it replaces is still in art/characters — it is where the clips come from.
            "villager_peasant.glb",
            skinnedShader, skinnedPipeline,
            skinnedCasterShader, skinnedCasterPipeline,
            ShadowCascades.Count,
            // <b>Unit height, because the placement matrix already carries the metres.</b> Normalising to
            // 1.45 m here and then letting the placement scale by body height again made a 2.1 m villager —
            // the same double-scale the prop path avoids by normalising to a unit cube and nothing more.
            metresTall: 1f,
            boneCapacity: SkinnedBoneCapacity);
        // <b>What the buildings measured, because two bugs came out of assuming it.</b> A fitted model's
        // bounding box is its roof and its height is whatever its proportions gave it — so anything hung on
        // a building (a lit window, a lantern, a chimney) has to be placed against numbers from the asset
        // rather than against a unit cube. Printed once at load: it is four lines, and it is the difference
        // between "the lights float" and knowing why.
        // <b>What the art costs, per model, because "it is the trees" is a claim about triangles.</b> A
        // frame that is slow zoomed out is slow in proportion to what is in view, and what is in view is
        // thousands of trees — so the per-tree cost is the number the whole question turns on and it was
        // nowhere on screen.
        var treeTriangles = 0;
        foreach (var tree in art.Trees) treeTriangles += tree.TriangleCount;
        var coverTriangles = 0;
        foreach (var cover in art.Scatter) coverTriangles += cover.TriangleCount;
        var underTriangles = 0;
        foreach (var under in art.Undergrowth) underTriangles += under.TriangleCount;
        var midTriangles = 0;
        foreach (var tree in art.TreesMid) midTriangles += tree.TriangleCount;
        var farTriangles = 0;
        foreach (var tree in art.TreesFar) farTriangles += tree.TriangleCount;
        // <b>The deep tier was missing from its own report.</b> Four tiers exist and three were printed, so the
        // one a dense wood actually uses — the tier every tree in a thick stand is drawn at — was the one nobody
        // could see a number for. An instrument that omits a case is how "the log about drawing is lying" gets
        // to be true while every line in it is correct.
        var deepTriangles = 0;
        foreach (var tree in art.TreesDeep) deepTriangles += tree.TriangleCount;
        // <b>Parts, because a part is an append and appends are what the node phase spends itself on.</b>
        // PropModel.Add pushes one InstanceData — eighty bytes — into every part's own list, and again into
        // every caster part's, so a model with a dozen materials costs twenty-four scattered writes per copy
        // rather than one. Twelve thousand trees a frame makes that the difference between a hundred thousand
        // appends and a quarter of a million, and nothing in the report said which it was: triangle counts
        // describe what the GPU is given and say nothing about what the loop does to hand it over.
        var deepParts = 0;
        foreach (var tree in art.TreesDeep) deepParts += tree.PartCount;
        var nearParts = 0;
        foreach (var tree in art.Trees) nearParts += tree.PartCount;
        Console.WriteLine(
            $"  art: a tree is {nearParts / MathF.Max(1, art.Trees.Length):F1} parts near and " +
            $"{deepParts / MathF.Max(1, art.TreesDeep.Length):F1} deep, so one placement is that many " +
            $"instance appends (twice, where it casts)");
        Console.WriteLine(
            $"  art: a tree is {treeTriangles / MathF.Max(1, art.Trees.Length):F0} triangles near, " +
            $"{midTriangles / MathF.Max(1, art.TreesMid.Length):F0} at the middle level, " +
            $"{farTriangles / MathF.Max(1, art.TreesFar.Length):F0} far and " +
            $"{deepTriangles / MathF.Max(1, art.TreesDeep.Length):F0} deep " +
            $"({art.Trees.Length} species, deep tier has {art.TreesDeep.Length}); " +
            $"cover {coverTriangles / MathF.Max(1, art.Scatter.Length):F0}, " +
            $"undergrowth {underTriangles / MathF.Max(1, art.Undergrowth.Length):F0}, " +
            $"villager {art.Villager?.TriangleCount ?? 0}, granary {art.Granary.TriangleCount}; " +
            $"decimation error {art.MidError:F2} m at the middle level and {art.FarError:F2} m far");
        // <b>What a tree costs each of the sun's three passes, which stopped being one number.</b> The near
        // cascade takes the tier's own decimated silhouette; the coarse two take a sixteen-triangle proxy,
        // because at 16 and 45 cm texels over 124-284 m there is no silhouette left to read. Printed per
        // cascade for the same reason the tier triangle counts above are printed at all: a caster cost that
        // nobody can see a number for is a caster cost that quietly returns to the near geometry the next
        // time somebody adds a tier. If these three ever read the same again, the proxy has been lost.
        var casterByCascade = new long[ShadowCascades.Count];
        foreach (var tier in new[] { art.Trees, art.TreesMid, art.TreesFar, art.TreesDeep })
        {
            foreach (var tree in tier)
            {
                for (var c = 0; c < ShadowCascades.Count && c < tree.CasterPassCount; c++)
                {
                    casterByCascade[c] += tree.CasterTriangleCountIn(c);
                }
            }
        }

        var tierSpecies = MathF.Max(
            1,
            art.Trees.Length + art.TreesMid.Length + art.TreesFar.Length + art.TreesDeep.Length);
        Console.WriteLine(
            $"  art: a tree casts {casterByCascade[0] / tierSpecies:F0} triangles into the near cascade, " +
            $"{casterByCascade[1] / tierSpecies:F0} into the middle and " +
            $"{casterByCascade[2] / tierSpecies:F0} into the far one (averaged over all four tiers)");

        foreach (var (name, model) in new[]
                 {
                     ("house", art.HouseFor(0)), ("granary", art.Granary), ("depot", art.Depot),
                 })
        {
            var roof = model.Bounds;
            var wall = art.WallsOf(model);
            Console.WriteLine(
                $"  {name}: roof to {roof.Max.X:F2} x {roof.Max.Z:F2}, ridge {roof.Max.Y:F2}, " +
                $"walls to {wall.Max.X:F2} x {wall.Max.Z:F2} — an overhang of " +
                $"{(roof.Max.X - wall.Max.X) * 100f:F0} cm on a metre of footprint");
        }
        RebuildTerrainSurfaceLayers();
        selectionUi = new SpriteBatch(vk);
        hud = SettlementHud.Load(vk);
        selectionPixel = graphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.PixelatedRepeat),
            new byte[] { 255, 255, 255, 255 },
            "rts-selection-pixel");
        // Smooth-sampled, because a path is a smudge and not a grid: at 192 cells over 600 m each is three
        // metres, and nearest sampling would draw the footfall grid itself rather than the paths in it.
        wearTexture = graphicsDevice.CreateTexture2D(
            new TextureDescription(WearCells, WearCells, TextureFormat.R8, SamplerDescription.LinearClamp),
            wear,
            "rts-wear");
        // <b>Sized from the fog's own grid, so the texel grid and the cell grid are one thing.</b> EnsureFog
        // first because the cell count comes from the map and this runs before the first frame does.
        // Linear-clamped for the reason the wear texture is: the filtering between texel centres is what
        // makes a fog edge a ten-metre ramp instead of a staircase, and it is free.
        EnsureFog();
        fogTexture = graphicsDevice.CreateTexture2D(
            new TextureDescription(scouted.Cells, scouted.Cells, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            scouted.Texels.ToArray(),
            "rts-scouted");
        fogTextureCells = scouted.Cells;
        scouted.MarkUploaded();

        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        mouseX = host.LogicalSize.Width * 0.5f;
        mouseY = host.LogicalSize.Height * 0.5f;
        UpdateCamera();
        UpdatePointerWorld();

        // The automated Vulkan smoke exercises the same queued group-command seam
        // as player input; it does not mutate agent movement state directly. It does not do it to a
        // settlement, though: an order is an interrupt, so this correctly pulled all twelve hands out
        // of their fields and the smoke run then reported a village where nobody was working.
        if (exitAfterFrames > 0 && simulation.Nodes.LiveCount == 0)
        {
            simulation.QueueMove(AllAgentIds(), new Vector2(7f, 5f));
        }

        Console.WriteLine("RTSGame — Agent Movement Layer");
        Console.WriteLine("  left-click/drag: select   Ctrl: additive selection   right-click: move / work target");
        Console.WriteLine("  B: toggle block-edit mode   left-click in block mode: add/remove block");
        Console.WriteLine("  S: stop   F: follow   P: patrol to pointer   H: chase   X: flee   Backspace: despawn selected");
        Console.WriteLine("  U: post selected at pointer   O: shuttle (press twice for both ends)   Y: off work");
        Console.WriteLine("  D: storehouse   Ctrl+D: barracks   A: farm   Ctrl+A: house   W: camp   Ctrl+W: palisade");
        Console.WriteLine("  Ctrl+right-click a sound palisade to upgrade it; damaged structures repair first");
        Console.WriteLine("  right-click a site, field, tree or outcrop to work it; U explicitly posts");
        Console.WriteLine("  Ctrl+O: haul route — press on the source node, then on the destination node");
        Console.WriteLine("     hauling is a job, not a unit: it costs wood, and Y takes the cart away");
        Console.WriteLine("  houses are the only things that eat: one outside every catchment goes hungry");
        Console.WriteLine("  wood is finite and standing: when no store can reach a tree, build a depot at the line");
        Console.WriteLine("  --village starts a working settlement mid-harvest; --compression <x> sets the clock");
        Console.WriteLine("  a standing job survives an order: give one, let go, and they go back to it");
        Console.WriteLine("  N: nav / surface / slope / congestion overlays   C: colliders   V: velocity   K: paths   I: states");
        Console.WriteLine("  T: per-phase timings   M: live movement trace (cohort/slot, contacts, worst overlap)");
        Console.WriteLine("  G: cycle 50 / 200 / 500-agent stress scenarios");
        Console.WriteLine("  J: cycle pen-escape variants (main exit + random alternates)");
        Console.WriteLine("  L: load terrain laboratory (ramp, cliff, road, mud, rough, pond)");
        Console.WriteLine("  blue unit: yielding under crowd pressure   red unit: navigation progress failure");
        Console.WriteLine(
            msaaSamples > 1
                ? $"  scene: {msaaSamples}x MSAA, resolved for the present"
                : "  scene: MSAA OFF (--msaa 1) — no resolve, every edge is a hard geometric edge");
        Console.WriteLine(
            $"  arrows: pan   middle-drag: drag the ground   Q/E: rotate 90°   " +
            $"wheel: zoom {CameraNearestDistance:F0}-{cameraFurthest:F0} m" +
            (cameraFurthest > CameraFurthestDistance
                ? $" (past the old {CameraFurthestDistance:F0} m cap — --zoom-limit pins it)"
                : string.Empty));
        Console.WriteLine("  0-9: recall a crew   Ctrl+digit: set it to the selection   Shift+digit: add to it");
        Console.WriteLine("     recalling the crew you already have jumps the camera to it; crews are not saved");
        Console.WriteLine("  Z: camera follows the selection (off by default)   Esc: quit");
        Console.WriteLine($"  {simulation.Agents.Count} agents   simulation: 30 Hz fixed step");
        Console.WriteLine("  placement grid: 1.5 m   navigation grid: 0.5 m   collider hash: 2.0 m");
    }

    /// <summary>
    /// First end of a shuttle being laid out, waiting for the second. Interface state only —
    /// nothing in the simulation knows about it.
    /// </summary>
    private Vector2? shuttleAnchor;

    /// <summary>First end of a hauling route being laid out. Interface state only.</summary>
    private NodeId? routeSource;

    /// <summary>Builds a node at the pointer, and turns a hauler loose if the settlement has none.</summary>
    /// <remarks>
    /// A granary with nobody to carry to it is a granary that never fills, and the board only ever
    /// notices a cart that already exists — so the first store built brings its own carts. That is a
    /// convenience of this testbed and not a mechanic: when construction is real, carts are built.
    /// </remarks>
    private void Build(NodeKind kind, int capacity, Resource produces, int occupancy = 0)
    {
        if (!pointerOnTerrain) return;
        // Placed as a site, not as a building. It stands on its ground from this moment — bodies route
        // around it, and completion re-rasterises nothing — but it stores nothing, feeds nobody and owns no
        // catchment until its timber has been carried out and somebody has stood at it long enough.
        var built = !Construction.NeedsBuilding(kind);
        var id = simulation.AddNode(
            kind, pointerWorld, capacity, produces, occupancy: occupancy, built: built);
        if (!built)
        {
            Console.WriteLine(
                $"  {kind} site: wants {Construction.TimberFor(kind)} timber" +
                (Construction.StoneFor(kind) > 0 ? $" and {Construction.StoneFor(kind)} stone" : string.Empty) +
                $" carried out and " +
                $"{Construction.LabourFor(kind):F0} labour-seconds — right-click it with villagers selected");
        }

        Console.WriteLine(
            $"  {kind} at ({pointerWorld.X:F0}, {pointerWorld.Y:F0}) — " +
            $"{simulation.Nodes.LiveCount} node(s). Right-click to assign hands; carts find their own work");
        if (kind != NodeKind.Granary) return;

        var carts = 0;
        foreach (ref readonly var agent in simulation.Agents.All)
        {
            if (agent.IsAlive && agent.CarryCapacity >= UnitType.HaulerCart.CarryCapacity) carts++;
        }

        for (var i = carts; i < 4; i++)
        {
            simulation.SpawnAgent(
                pointerWorld + new Vector2(3f + i * 1.4f, -3f), UnitType.HaulerCart);
        }

        if (carts < 4) Console.WriteLine($"  {4 - carts} hauler cart(s) turned loose");
    }

    /// <summary>Puts the selection to work standing at the pointer.</summary>
    private void AssignPost(NodeId? explicitNode = null)
    {
        if (!pointerOnTerrain || selection.Selected.Count == 0) return;
        shuttleAnchor = null;
        // Posting on a building posts at the building: the place is its centre and its extent is its
        // footprint, so hands gather round the yard instead of trying to occupy the middle of it.
        var node = explicitNode is { } picked && simulation.Nodes.Contains(picked)
            ? picked
            : EconomySystem.NodeAt(simulation.Nodes, pointerWorld, radius: 3.5f);
        var at = simulation.Nodes.Contains(node) ? simulation.Nodes.Get(node).Position : pointerWorld;
        var extent = simulation.Nodes.Contains(node)
            ? simulation.Nodes.Get(node).FootprintRadius
            : 0f;
        // <b>spread: the player pointed at one thing and meant "work this sort of thing here".</b> Six
        // villagers on one trunk become six woodcutters; the bot's own orders never take this, because its
        // intents are reconciled per node and would never be satisfied.
        simulation.QueueAssign(
            selection.Snapshot(), Assignment.Hold(at, PostDwellSeconds, extent), spread: true);
        Console.WriteLine(
            $"  {selection.Selected.Count} unit(s) posted at ({at.X:F1}, {at.Y:F1})" +
            (extent > 0f ? $" — working the {simulation.Nodes.Get(node).Kind}" : string.Empty));
    }

    /// <summary>Right-click works what it visibly targets; otherwise it remains the ordinary move command.</summary>
    /// <remarks>
    /// A construction site is a command target, not merely a coordinate. Sending villagers to its ground
    /// with a move order produced the most literal possible interface failure: they arrived, stood beside the
    /// work and never acquired the standing project assignment. The contextual command uses the same visual
    /// pick as the hover marker and HUD, then enters through <see cref="AssignPost"/> so fields, deposits and
    /// unfinished buildings retain one assignment seam. Empty ground still means move and therefore remains
    /// a temporary interrupt rather than silently replacing a standing job.
    /// </remarks>
    private void IssueContextCommand()
    {
        if (!pointerOnTerrain) return;
        var target = PickNode();
        if (simulation.Nodes.Contains(target))
        {
            ref readonly var node = ref simulation.Nodes.Get(target);

            // <b>Somebody else's building takes none of the friendly branches.</b> §167. Pointing at a
            // foreign barracks must not train your villagers in it, and pointing at a foreign field must not
            // post them to work it, so hostility is tested before anything else a right-click can mean.
            if (simulation.IsHostile(selection.Selected, target))
            {
                IssueRaid(target);
                return;
            }

            if (additiveSelection && node.IsStructure && !node.HasStructuralProject)
            {
                var began = node.Condition < node.MaxCondition - 0.0001f
                    ? simulation.BeginRepair(target)
                    : simulation.BeginUpgrade(target, NodeKind.StoneWall);
                if (began)
                {
                    AssignPost(target);
                    ref readonly var project = ref simulation.Nodes.Get(target);
                    Console.WriteLine(
                        project.StructuralProject == StructuralProjectKind.Repair
                            ? $"  repairing {project.Kind} #{target.Value}"
                            : $"  upgrading {project.Kind} #{target.Value} to {project.StructuralTarget}");
                    return;
                }
            }

            if (!additiveSelection && node.Kind == NodeKind.Barracks && node.IsBuilt)
            {
                var villagers = selection.Snapshot().Where(id =>
                    simulation.Agents.Contains(id) &&
                    simulation.Agents.Get(id).Role == AgentRole.Villager).ToArray();
                if (villagers.Length > 0)
                {
                    simulation.QueueTrainMilitia(villagers, target);
                    Console.WriteLine(
                        $"  {villagers.Length} villager(s) committed to militia training " +
                        $"at barracks #{target.Value}");
                    return;
                }
            }

            if (node.IsWorkSite)
            {
                AssignPost(target);
                return;
            }
        }

        simulation.QueueMove(selection.Snapshot(), pointerWorld);
    }

    /// <summary>Right-clicking a hostile node: the party you sent decides what happens to it. §167.</summary>
    /// <remarks>
    /// <b>There is no loot key and no attack key, and no number anywhere.</b> Which is the whole answer to
    /// "how does a player say loot half a load": they do not, because no order in this game carries a
    /// magnitude — every one of them points at a thing, and the quantities all live in plans. So the verb
    /// comes off the selection instead. Hands that can carry rob the place; hands that cannot fight it.
    /// <para>
    /// Which makes a raid fall out of one click rather than needing to be a command. Send militia and
    /// villagers together at a granary and the escort starts breaking it while the carriers take a load and
    /// leave — the composition §166 promised, expressed by who you picked. And it scales with the roster
    /// without an interface: how much you steal is how many carriers you brought and what each can hold.
    /// </para>
    /// <para>
    /// An empty store has nothing to say to carriers, so everybody attacks it. That is deliberate: it means
    /// pointing at a stripped granary does the obvious thing instead of nothing at all.
    /// </para>
    /// </remarks>
    private void IssueRaid(NodeId target)
    {
        if (selection.Selected.Count == 0) return;
        shuttleAnchor = null;
        ref readonly var node = ref simulation.Nodes.Get(target);

        var carriers = new List<AgentId>();
        var fighters = new List<AgentId>();
        foreach (var id in selection.Snapshot())
        {
            if (!simulation.Agents.Contains(id)) continue;
            (simulation.Agents.Get(id).CarryCapacity > 0 ? carriers : fighters).Add(id);
        }

        var worthRobbing = node.Stores && node.Stock.Total > 0 && carriers.Count > 0;
        if (worthRobbing) simulation.QueueLoot(carriers, target);
        else fighters.AddRange(carriers);

        if (fighters.Count > 0) simulation.QueueAttack(fighters, AgentId.None, target);

        Console.WriteLine(
            $"  {node.Kind} #{target.Value} of faction {node.Faction.Value}: " +
            (worthRobbing ? $"{carriers.Count} carrier(s) looting" : "nothing to carry") +
            (fighters.Count > 0 ? $", {fighters.Count} attacking" : string.Empty));
    }

    /// <summary>
    /// Lays out a shuttle in two presses: the first marks one end, the second the other.
    /// </summary>
    /// <remarks>
    /// A shuttle is hauling with the cargo left out, which makes this the most useful thing to
    /// be able to set by hand right now — watching twenty units run a lane both ways is how
    /// the congestion terms get judged before there is any cargo to judge them with.
    /// </remarks>
    private void AssignShuttle()
    {
        if (!pointerOnTerrain || selection.Selected.Count == 0) return;
        if (shuttleAnchor is not { } anchor)
        {
            shuttleAnchor = pointerWorld;
            Console.WriteLine(
                $"  shuttle: first end at ({pointerWorld.X:F1}, {pointerWorld.Y:F1}) — " +
                "press O again for the other end");
            return;
        }

        shuttleAnchor = null;
        simulation.QueueAssign(
            selection.Snapshot(), Assignment.Shuttle(anchor, pointerWorld, PostDwellSeconds));
        Console.WriteLine(
            $"  {selection.Selected.Count} unit(s) shuttling ({anchor.X:F1}, {anchor.Y:F1}) <-> " +
            $"({pointerWorld.X:F1}, {pointerWorld.Y:F1})");
    }

    /// <summary>
    /// Puts the selection on a hauling route: two presses, source node then sink node.
    /// </summary>
    /// <remarks>
    /// <b>Hauling is a job you give somebody, not a unit you build.</b> Which is what this key is for: pick
    /// villagers, point at where the goods are, point at where they should go. Each of them spends a sack
    /// of the settlement's timber on a handcart and runs that route until the source is empty — after
    /// which they still have the cart, and the board finds them stranded stock or a heap to fetch.
    /// <para>
    /// Refused, out loud, if there is no timber to build a cart from. That is not an inconvenience, it is
    /// the dependency Stage B's receding wood line is supposed to create: you cannot cart wood in until
    /// you have some wood.
    /// </para>
    /// <para>
    /// Both ends have to be nodes rather than points on the ground, because a route is between two places
    /// that hold goods and a patch of grass is not one. Naming them by id also means a route survives its
    /// granary being rebuilt somewhere else, and dies with it being destroyed, rather than pointing at a
    /// spot in an empty field forever.
    /// </para>
    /// </remarks>
    private void AssignRoute()
    {
        if (!pointerOnTerrain || selection.Selected.Count == 0) return;
        var node = EconomySystem.NodeAt(simulation.Nodes, pointerWorld, RoutePickRadius);
        if (!simulation.Nodes.Contains(node))
        {
            Console.WriteLine("  route: point at a store, a yard or a heap — a route runs between places");
            return;
        }

        if (routeSource is not { } source)
        {
            routeSource = node;
            Console.WriteLine(
                $"  route: collecting from {simulation.Nodes.Get(node).Kind} at " +
                $"({pointerWorld.X:F1}, {pointerWorld.Y:F1}) — press Ctrl+O again on the destination");
            return;
        }

        routeSource = null;
        if (source == node)
        {
            Console.WriteLine("  route: a route needs two different places");
            return;
        }

        ref readonly var from = ref simulation.Nodes.Get(source);
        ref readonly var to = ref simulation.Nodes.Get(node);
        if (!simulation.TryChooseRouteCargo(source, node, Resource.Grain, out var nextCargo))
        {
            Console.WriteLine($"  route: {to.Kind} cannot receive anything from {from.Kind}");
            return;
        }
        var put = 0;
        var refused = 0;
        foreach (var id in selection.Snapshot())
        {
            if (simulation.TryAssignRoute(id, source, node)) put++;
            else refused++;
        }

        Console.WriteLine(
            $"  route: {put} carting useful goods from {from.Kind} to {to.Kind}; next load {nextCargo}" +
            (refused > 0
                ? $"; {refused} refused — a cart costs {SimulationWorld.CartTimber} wood and no store " +
                  "within reach has it"
                : $" ({SimulationWorld.CartTimber} wood a cart)"));
    }

    /// <summary>How near the pointer has to be to a node to be pointing at it.</summary>
    /// <remarks>
    /// Generous — a heap is 40 cm across and a granary is 7.5 m, and a player pointing near either means
    /// that one. The node search takes the nearest within this, so overlap resolves the obvious way.
    /// </remarks>
    private const float RoutePickRadius = 6f;


    private AgentId[] AllAgentIds()
    {
        var ids = new AgentId[simulation.Agents.Count];
        for (var i = 0; i < ids.Length; i++) ids[i] = new AgentId(i);
        return ids;
    }

    private static Mesh CreateMesh(
        VulkanGraphicsDevice vk,
        string name,
        VertexPosition3NormalTexture[] vertices,
        ushort[] indices)
    {
        var vertexBuffer = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(vertices), $"{name}.vb");
        var indexBuffer = vk.CreateIndexBuffer(indices, name: $"{name}.ib");
        return new Mesh(name, vertexBuffer, indexBuffer, indices.Length,
            new Bounds3(new Vector3(-0.5f), new Vector3(0.5f)));
    }

    private void RebuildTerrainSurfaceLayersIfReady()
    {
        if (vk is not null) RebuildTerrainSurfaceLayers();
    }

    /// <summary>
    /// Largest cell count the one-mesh-per-surface ground can carry.
    /// </summary>
    /// <remarks>
    /// The surface meshes index with <c>ushort</c>, and the worst case is a map that is all
    /// one surface: half its cells land in each parity layer, six vertices each, so the
    /// ceiling is 65,535/3 cells. Beyond that the ground is drawn coarsely instead — which is
    /// also the right greybox affordance at that size, since a half-metre checkerboard over a
    /// kilometre is not a scale reference, it is noise.
    /// </remarks>
    private const int FineGroundCellLimit = 21_000;

    private const int GroundChunkRenderCells = 64;

    /// <summary>Render cells the whole map is drawn with, along one side.</summary>
    /// <remarks>
    /// <b>The ground is meshed at whatever resolution makes the whole map affordable, which is the number
    /// that decides everything else here.</b> Three hundred and sixteen a side is a hundred thousand render
    /// cells and two hundred thousand triangles for an entire map — less than the near-field mesh and the
    /// plates it replaces were costing together. It also makes the chunk count roughly independent of the
    /// extent: a 600 m map draws at two metres a cell and a 1200 m map at four, and both come out at
    /// twenty-five chunks.
    /// </remarks>
    private const int GroundRenderCellsPerSide = 316;

    /// <summary>
    /// Navigation cells per render cell: how coarsely the ground is drawn against how finely it is walked.
    /// </summary>
    /// <remarks>
    /// Derived rather than fixed, and clamped at one, because the thirty metre laboratory wants its ground
    /// at the navigation grid's own resolution — its features are a metre or two across — while a played
    /// map cannot afford that and does not need it.
    /// </remarks>
    private int GroundRenderStep => Math.Clamp(
        (int)MathF.Ceiling(simulation.Navigation.Width / (float)GroundRenderCellsPerSide), 1, 16);

    private int GroundChunkCells => GroundChunkRenderCells * GroundRenderStep;

    /// <summary>
    /// Navigation cells per water-sheet quad. The ground's own step, and it has to divide the chunk exactly.
    /// </summary>
    /// <remarks>
    /// <b>Sized from the chunk rather than from an index budget, because the budget did not divide it.</b> The
    /// previous form was <c>ceil(GroundChunkCells / 96)</c> — six, for a chunk of five hundred and twelve cells
    /// — and 512 is not a multiple of 6. The loop runs to <c>x &lt;= toX</c>, so the last quad in every chunk
    /// reached four cells <em>past</em> the boundary and into its neighbour.
    /// <para>
    /// The ground coats overlap the same way and it has never mattered, because they are opaque: drawing the
    /// same ground twice looks like drawing it once. Translucent water drawn twice does not — the alpha
    /// compounds, and the overlap appears as a dark line along every chunk edge. Reported as "dam-like
    /// separators between the deeper waters", which is exactly what a grid of them looks like from above at a
    /// forty-five degree yaw.
    /// </para>
    /// <para>
    /// A chunk is sixty-four render cells by construction, so the ground's step tiles it exactly. What is given
    /// up is the finer shoreline the half-step was for — and that turns out to cost nothing, because the shore
    /// is a <em>depth</em> fade rather than a mesh edge: the opacity goes to zero as the water thins whatever
    /// resolution the quads are.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b>Half the ground's, from §162, because the waterline now has detail worth resolving.</b> The note
    /// above is about a step that did not divide the chunk and produced a dark seam at every boundary; half
    /// of the ground's step always does, since a chunk is sixty-four render cells by construction. What
    /// changed is that it is worth paying for: with the depth measured against the real terrain and each cell
    /// cut to the waterline, the mesh can follow a crevice, and at the ground's own step it had nowhere to
    /// put the vertices to do it.
    /// </remarks>
    private int WaterRenderStep => Math.Max(1, GroundRenderStep / 2);

    private readonly Dictionary<(int X, int Z), List<TerrainSurfaceLayer>> groundChunks = new();

    /// <summary>The lowest and highest ground in each chunk, so a chunk can be frustum-tested as a box.</summary>
    private readonly Dictionary<(int X, int Z), (float Low, float High)> groundChunkHeights = new();

    /// <summary>Chunks staged this frame, so the draw pass submits exactly what was staged.</summary>
    private readonly List<(int X, int Z)> drawnChunks = new();

    /// <summary>Woodland-field queries this frame, which is the first thing to measure about a cheap-looking field.</summary>
    private long woodlandAsked;

    /// <summary>How long the nodes took, so the ground's own figure is the ground's own.</summary>
    private double nodeMilliseconds;

    /// <summary>How tall the window is, so ground detail can be judged in pixels rather than in metres.</summary>
    private float viewportPixels;



    /// <summary>The chunks close enough for their transition coats to be worth drawing.</summary>
    private readonly HashSet<(int X, int Z)> blendedChunks = new();

    /// <summary>The plates actually drawn this frame: the ones no meshed chunk has covered.</summary>

    /// <summary>
    /// Meshes the ground the camera can actually see, and lets plates cover the rest.
    /// </summary>
    /// <remarks>
    /// <b>Height belongs where it says something — and now it can say it everywhere close enough to read.</b>
    /// The fine ground has always been the right representation and could never be afforded on a played map,
    /// because one mesh cannot index it. Chunked, it can: a chunk near the camera is meshed at the
    /// navigation grid's own resolution, so the ground the player is looking at follows the terrain exactly,
    /// with no resolution mismatch between what the simulation walks on and what the renderer draws. Beyond
    /// the detail radius the sloped plates take over, and they are hazed by then.
    /// <para>
    /// Camera-dependent, which is a thing this file deliberately removed once — the detail patches used to
    /// rebuild fourteen thousand blocks whenever the camera crossed one. The difference is caching: a chunk
    /// is built once and kept, so crossing a boundary costs one chunk rather than the map, and a terrain
    /// edit is the only thing that throws them all away.
    /// </para>
    /// <para>
    /// The parity checker is dropped here. It exists as a scale reference on an untextured plane, and ground
    /// that visibly rolls is its own scale reference — while keeping it would double the draw count of the
    /// most numerous thing in the frame for a contrast of three per cent.
    /// </para>
    /// </remarks>
    private void EnsureGroundChunks()
    {
        if (vk is null) return;

        var transform = simulation.Navigation.Transform;
        var columns = (transform.Width + GroundChunkCells - 1) / GroundChunkCells;
        var rows = (transform.Height + GroundChunkCells - 1) / GroundChunkCells;
        for (var z = 0; z < rows; z++)
        for (var x = 0; x < columns; x++)
        {
            if (groundChunks.ContainsKey((x, z))) continue;
            groundChunks[(x, z)] = BuildGroundChunk(x, z);
            // One chunk a frame. Twenty-five of them built together is a visible hitch on a map load and
            // over half a second is not — and there is nothing underneath any more to cover for a chunk
            // that has not arrived, which is the one argument the plates had left.
            return;
        }
    }

    private List<TerrainSurfaceLayer> BuildGroundChunk(int chunkX, int chunkZ)
    {
        var transform = simulation.Navigation.Transform;
        var fromX = chunkX * GroundChunkCells;
        var fromZ = chunkZ * GroundChunkCells;
        var toX = Math.Min(fromX + GroundChunkCells - 1, transform.Width - 1);
        var toZ = Math.Min(fromZ + GroundChunkCells - 1, transform.Height - 1);
        var cover = GroundCover.Build(simulation.Terrain, fromX, fromZ, toX, toZ, GroundRenderStep);
        var layers = new List<TerrainSurfaceLayer>();

        // <b>How tall this chunk is, kept so the frustum can be asked about it.</b> Sampled on a coarse lattice
        // rather than per vertex — a corner-and-middle grid over a chunk is enough to bound it, and the bound
        // only has to be conservative. See the cull in the render pass for what this is for.
        var low = float.MaxValue;
        var high = float.MinValue;
        for (var z = 0; z <= 4; z++)
        for (var x = 0; x <= 4; x++)
        {
            var cell = new Vector2(
                fromX + (toX - fromX) * (x / 4f),
                fromZ + (toZ - fromZ) * (z / 4f));
            var at = transform.Origin + cell * transform.CellSize;
            var height = simulation.Terrain.SampleHeight(at);
            low = MathF.Min(low, height);
            high = MathF.Max(high, height);
        }

        groundChunkHeights[(chunkX, chunkZ)] = (low, MathF.Max(low + 0.5f, high));

        // Every opaque coat before any transition coat, because a transition blends against whatever is
        // already there and a chunk is the unit that has to be self-consistent. Across chunks the order is
        // free: they do not overlap.
        for (var pass = 0; pass < 2; pass++)
        for (var slot = 0; slot < GroundCover.Classes.Length; slot++)
        {
            var blend = pass == 1;
            var surface = GroundCover.Classes[slot];
            var (vertices, indices) = BuildTerrainSurfaceMesh(
                simulation.Terrain, cover, slot, blend, fromX, fromZ, toX, toZ, GroundRenderStep);
            if (indices.Length == 0) continue;
            var kind = surface.ToString().ToLowerInvariant() + (blend ? "-blend" : string.Empty);
            var name = $"ground-{chunkX}-{chunkZ}-{kind}";
            var mesh = CreateMesh(vk, name, vertices, indices);
            var buffer = new InstanceBuffer(vk, worldShader, $"{name}-instance");
            layers.Add(new TerrainSurfaceLayer(
                mesh,
                buffer,
                new InstancedBatch(mesh, blend ? groundBlendPipeline : worldPipeline, buffer),
                TerrainClassed(TerrainColor(surface)),
                blend));
        }

        // <b>The water goes on last, and it is a surface rather than a coat.</b> Every layer above is the
        // ground in a colour; this one is a separate sheet at the water's own level, so it is the one thing
        // here that is not coplanar with the terrain. Drawn after the coats because it lies over them, and
        // through the blend pipeline because it is translucent — which is what makes a shore a gradient
        // instead of a line.
        if (simulation.Terrain.Drainage is not null)
        {
            // <b>Finer than the ground's step, because the shoreline is the detail and the ground is not</b> —
            // a water edge that steps in whole render cells puts a six-metre staircase round a lake, which is
            // the artefact the ground blend exists to remove, reintroduced by the one surface that does not use
            // it.
            //
            // <b>But derived from the index budget rather than from the ground's step, which is a crash I
            // shipped.</b> "Half the ground's step" makes the cell count across a chunk constant at 128
            // whatever the map size — 16,384 cells at six vertices each is 98,304, and these meshes are
            // 16-bit indexed. So it overflowed on every map and threw the moment a chunk was full. Sized from
            // the limit instead: ninety-six cells a side is 55,296 vertices, comfortably inside 65,535, and
            // still finer than the ground everywhere.
            var (vertices, indices) = BuildWaterMesh(
                simulation.Terrain, fromX, fromZ, toX, toZ, WaterRenderStep);
            if (indices.Length > 0)
            {
                var name = $"ground-{chunkX}-{chunkZ}-water";
                var mesh = CreateMesh(vk, name, vertices, indices);
                var buffer = new InstanceBuffer(vk, worldShader, $"{name}-instance");
                layers.Add(new TerrainSurfaceLayer(
                    mesh,
                    buffer,
                    new InstancedBatch(mesh, groundBlendPipeline, buffer),
                    new Vector4(ShallowsColor.X, ShallowsColor.Y, ShallowsColor.Z, SettlementArt.MaterialClass.Water),
                    Blend: false,
                    Water: true));
            }
        }

        return layers;
    }

    /// <summary>
    /// The sheet of water lying over a chunk, at the level the drainage says it stands.
    /// </summary>
    /// <remarks>
    /// <b>Emitted only where there is depth, which is what makes the shoreline.</b> A cell whose four corners
    /// are all dry contributes nothing, so the mesh ends where the water does — and because the per-vertex
    /// opacity goes to zero as the depth does, the last few metres fade out rather than stopping at a polygon
    /// edge. The shore is not drawn; it is where two surfaces meet.
    /// <para>
    /// The two spare texture floats carry <b>opacity</b> and <b>depth</b>. Opacity because a shallow ford
    /// should show its gravel and a river should not; depth because water gets darker with it, and the two are
    /// not the same curve — opacity saturates within a metre and colour keeps deepening past three.
    /// </para>
    /// <para>
    /// Normals point straight up rather than following the sheet. The sheet <em>is</em> flat where it is a lake
    /// and very nearly flat where it is a channel, and a normal derived from a nearly-flat surface is mostly
    /// noise — which on something this reflective reads as crumpled foil.
    /// </para>
    /// </remarks>
    private static (VertexPosition3NormalTexture[] Vertices, ushort[] Indices) BuildWaterMesh(
        TerrainMap terrain,
        int fromX,
        int fromZ,
        int toX,
        int toZ,
        int step)
    {
        var vertices = new List<VertexPosition3NormalTexture>();
        var indices = new List<ushort>();
        if (terrain.Drainage is not { } water) return (vertices.ToArray(), indices.ToArray());
        var grid = terrain.Transform;
        var size = grid.CellSize;

        // Below this a body of water is a damp patch, and drawing it puts a translucent film over every hollow
        // on the map. Two centimetres is under a boot sole.
        const float wetEnough = 0.02f;

        // Twice the width Biomes will not trust, which is the narrowest thing this mesh can depict honestly:
        // a quad at the ground's render step is several metres across.
        const float MinimumRenderedChannelMetres = 2f * Biomes.FordableWidthMetres;

        // <b>Against the ground that is actually drawn, not the lattice the surface was solved on.</b> §162,
        // and it is why water read as tiles rather than as liquid: BedAt samples the four-metre drainage
        // lattice, so a crevice two metres across does not exist in the field the depth was measured against
        // and no amount of mesh resolution could have filled it. The surface stays where the solver put it —
        // it is smooth by construction and belongs to the lattice — and what it is compared to is the terrain
        // a player can see.
        VertexPosition3NormalTexture Vertex(Vector2 at)
        {
            var level = water.LevelAt(at);
            var depth = MathF.Max(0f, level - terrain.SampleHeight(at));
            // Opacity saturates within a metre and a half: past that there is no more bed to hide. Eased
            // rather than linear so the shallows hold their transparency further out — a hard ramp puts a
            // visible ring of half-opaque water round every shore, which is the thing a shore should not have.
            var eased = Math.Clamp(depth / 1.5f, 0f, 1f);
            var opacity = 0.88f * eased * eased * (3f - 2f * eased);
            // <b>Depth against the wadeable line rather than against a rendering constant.</b> §145: the
            // simulation already divides water into Shallows and Impassable at Biomes.WadeableDepthMetres —
            // see the note there on fords — and the surface never showed which was which. "I should be able
            // to tell what people can walk over and what they cannot" asks for a fact that is already true to
            // be depicted, so the shader is handed the fact and not a normalised number: 1.0 is exactly the
            // depth at which the ground under it stops being crossable.
            var wade = depth / Biomes.WadeableDepthMetres;
            // <b>The flow field as a tilt of the normal, which is the only encoding that survives the vertex
            // stage.</b> §149. The first version put (flow.x, speed, flow.z) in the normal slot and was
            // broken two ways at once. world.vert does <c>normalize(mat3(model) * inNormal)</c>, so the
            // magnitude — the speed — could never arrive; and <b>still water has no flow at all</b>, so
            // FlowAt returns a zero vector, and <c>normalize(vec3(0))</c> is NaN. Every lake vertex handed
            // the fragment stage a NaN normal, which propagated through the whole water block and rasterised
            // white. That is the flat grey-white sheet, and it is why the surface got worse rather than
            // better: the previous hard-coded (0,1,0) was at least a number.
            //
            // Encoded as an actual surface tilt instead: the normal of a sheet leaning downstream in
            // proportion to its speed. Normalisation scales all three components together, so the shader
            // recovers flow times speed exactly as <c>xz / y</c>, and still water is (0,1,0) — which is both
            // NaN-free and the true normal of a flat pond, so anything else that reads it is right too.
            var (flow, speed) = water.FlowAt(at);
            return new VertexPosition3NormalTexture(
                new GraphicsVector3(at.X, level, at.Y),
                new GraphicsVector3(flow.X * speed, 1f, flow.Y * speed),
                new GraphicsVector2(opacity, wade));
        }

        float DepthAt(Vector2 at) => water.LevelAt(at) - terrain.SampleHeight(at);

        /// Keeps the part of a triangle that is under water, cutting its edges where the depth reaches zero.
        void ClipToWaterline(Vector2 a, Vector2 b, Vector2 c)
        {
            Span<Vector2> corners = stackalloc Vector2[3] { a, b, c };
            Span<float> depths = stackalloc float[3] { DepthAt(a), DepthAt(b), DepthAt(c) };
            Span<Vector2> kept = stackalloc Vector2[4];
            var count = 0;
            for (var i = 0; i < 3; i++)
            {
                var j = (i + 1) % 3;
                var wetHere = depths[i] > 0f;
                var wetNext = depths[j] > 0f;
                if (wetHere) kept[count++] = corners[i];
                if (wetHere == wetNext) continue;
                // Where the edge crosses, by the depths at its ends. The crossing point has zero depth, so
                // its own opacity is zero and the sheet fades out exactly at its own silhouette.
                var t = depths[i] / (depths[i] - depths[j]);
                kept[count++] = Vector2.Lerp(corners[i], corners[j], t);
            }

            for (var i = 2; i < count; i++) AddTriangle(kept[0], kept[i - 1], kept[i]);
        }

        void AddTriangle(Vector2 first, Vector2 second, Vector2 third)
        {
            var start = checked((ushort)vertices.Count);
            vertices.Add(Vertex(first));
            vertices.Add(Vertex(second));
            vertices.Add(Vertex(third));
            indices.Add(start);
            indices.Add((ushort)(start + 1));
            indices.Add((ushort)(start + 2));
        }

        for (var z = fromZ; z <= toZ; z += step)
        for (var x = fromX; x <= toX; x += step)
        {
            var x0 = grid.Origin.X + x * size;
            var z0 = grid.Origin.Y + z * size;
            var x1 = x0 + step * size;
            var z1 = z0 + step * size;
            var c00 = new Vector2(x0, z0);
            var c10 = new Vector2(x1, z0);
            var c01 = new Vector2(x0, z1);
            var c11 = new Vector2(x1, z1);
            // <b>Sampled a step wider than the quad, so the sheet's silhouette sits inside its own fade.</b>
            // §148: the test was the four corners, which puts exactly one quad of gradient outside the
            // waterline — and at this render step a quad is several metres, so the staircase outline of the
            // quad grid lands squarely in the visible part of it and reads as a sawtooth shore. One ring
            // further out doubles the fade and hides the stairs in it.
            var wettest = 0f;
            var widest = 0f;
            var pooled = 0f;
            for (var cz = -1; cz <= 2; cz++)
            for (var cx = -1; cx <= 2; cx++)
            {
                var corner = new Vector2(x0 + cx * step * size, z0 + cz * step * size);
                wettest = MathF.Max(wettest, water.LevelAt(corner) - water.BedAt(corner));
                widest = MathF.Max(widest, water.WidthAt(corner));
                // <b>Does water stand here, not is this cell under a fill.</b> §159: LakeDepthAt is
                // filled - ground, true of every hollow, so an apron the model had already refused to call a
                // lake was drawn as one anyway. See Drainage.StandsAt.
                if (water.StandsAt(corner)) pooled = MathF.Max(pooled, 1f);
            }

            if (wettest <= wetEnough) continue;

            // <b>And a watercourse narrower than a stride is wet ground, not a water surface.</b> §148,
            // reported as "impossibly thin/shallow joins": a two-metre channel rendered as a translucent
            // sheet is a pale thread lying across a field, and at this render step it cannot be anything else
            // — the quad it is drawn on is wider than the stream. Biomes already treats anything under
            // FordableWidthMetres as crossable whatever its depth says, for the same reason: at a four-metre
            // lattice cell the width of something two metres across is not a number to trust. So the surface
            // stops where the trust does. A standing body is exempt: a pond can be narrow and still be a pond.
            if (pooled <= 0.02f && widest < MinimumRenderedChannelMetres) continue;
            // <b>Clipped to the waterline, so the edge is a contour and not a staircase of quads.</b> §162:
            // emitting whole cells made the outline a picture of the render grid — "trying to be tiles
            // instead of filling the crevices of the map like a liquid". Each triangle is cut against the
            // line where the water meets the ground, which is exactly where depth reaches zero.
            //
            // Two triangles clipped independently rather than sixteen marching-squares cases: the contour
            // comes out identical, and a case table is a place for one entry to be wrong for a year.
            ClipToWaterline(c00, c01, c10);
            ClipToWaterline(c11, c10, c01);
        }

        return (vertices.ToArray(), indices.ToArray());
    }

    private void DisposeGroundChunk((int X, int Z) chunk)
    {
        if (!groundChunks.Remove(chunk, out var layers)) return;
        foreach (var layer in layers)
        {
            layer.Buffer.Dispose();
            vk!.DestroyVertexBuffer(layer.Mesh.VertexBuffer);
            vk.DestroyIndexBuffer(layer.Mesh.IndexBuffer);
        }
    }

    /// <summary>
    /// Throws the ground away when the terrain under it changes shape.
    /// </summary>
    /// <remarks>
    /// A felling is a terrain edit, so this is also the cost of cutting a tree: the chunks are rebuilt one
    /// a frame afterwards. It used to also build a whole-map mesh for small worlds and hand large ones to
    /// plates — one ground for the laboratory and another for a played map, which is two representations
    /// and therefore two sets of artefacts. There is one now, at whatever resolution the map can afford.
    /// </remarks>
    /// <summary>How many times the ground has been rebuilt, which should be a handful for a whole session.</summary>
    private int terrainRebuilds;

    private void RebuildTerrainSurfaceLayers()
    {
        if (vk is null) return;
        terrainRebuilds++;
        vk.WaitIdle();
        foreach (var chunk in groundChunks.Keys.ToArray()) DisposeGroundChunk(chunk);
        renderedTerrain = simulation.Terrain;
        renderedTerrainRevision = simulation.Terrain.Revision;

        // What the map's height range is, for anything that wants "how high is this, relative to here" —
        // the treeline, for one. A hundred and one samples a side against a terrain that has just changed
        // shape, which is a fraction of what the chunks about to be rebuilt will cost.
        var lowest = float.MaxValue;
        var highest = float.MinValue;
        for (var z = 0; z <= 100; z++)
        for (var x = 0; x <= 100; x++)
        {
            // The interior, not the whole map: the frontier stands well above everything inside it, so a
            // span measured across it would put every interior hill in the bottom third and no country would
            // ever be classified as high. See SettlementScenarios.InteriorExtent.
            var interior = MathF.Max(
                simulation.ExtentMeters * 0.25f,
                simulation.ExtentMeters - 2f * ReliefPlan.RimWidthMetres);
            var at = new Vector2(x / 100f - 0.5f, z / 100f - 0.5f) * interior;
            var height = simulation.Terrain.SampleHeight(at);
            lowest = MathF.Min(lowest, height);
            highest = MathF.Max(highest, height);
        }

        reliefFloor = lowest;
        reliefSpan = highest - lowest;
        RebuildCountryField();
    }

    /// <summary>
    /// One coat of ground: the cells a visual class owns, or the cells it merely reaches into.
    /// </summary>
    /// <remarks>
    /// <b>The staircase is gone because a surface no longer belongs to a cell — it covers a corner by an
    /// amount.</b> <see cref="GroundCover"/> measures that amount over a noise-warped disc, and it arrives
    /// here as the vertex's second texture float, which the world shader reads as coverage. A cell is drawn
    /// once opaquely in whichever class owns it and again, blended, in each class that reaches into it, so a
    /// boundary is a crossfade whose line wanders off the grid it was rasterised on.
    /// <para>
    /// The two agree at every shared edge without being made to: an owned cell and its neighbour read the
    /// <em>same</em> corner shares, so the crossfade is continuous across the line where ownership flips.
    /// That is the property that makes this work, and it is why coverage is a function of position rather
    /// than of which mesh is asking.
    /// </para>
    /// </remarks>
    private static (VertexPosition3NormalTexture[] Vertices, ushort[] Indices) BuildTerrainSurfaceMesh(
        TerrainMap terrain,
        GroundCover cover,
        int slot,
        bool blend,
        int fromX,
        int fromZ,
        int toX,
        int toZ,
        int step = 1)
    {
        var vertices = new List<VertexPosition3NormalTexture>();
        var indices = new List<ushort>();
        var grid = terrain.Transform;
        var size = grid.CellSize;

        // <b>Normals from the height field, not from the triangle.</b> A face normal is right for a crate
        // and wrong for ground: the ground is split into triangles on an alternating diagonal, so two
        // triangles covering the same gentle slope get noticeably different normals and the pair reads as a
        // herringbone. Over a hillside that is a field of diagonal stripes, which is what "elevation changes
        // have visible artefacts" was seeing — and it is worse the gentler the slope, because the facet
        // difference stays the same while the slope it is describing shrinks.
        //
        // Sampled per vertex instead, so the surface is shaded as the smooth thing it is interpolated from
        // and a slope reads as a slope. The geometry is unchanged; only what it claims about its own
        // curvature is.
        VertexPosition3NormalTexture Vertex((Vector3 At, float Coverage) corner)
        {
            var normal = terrain.SampleNormal(new Vector2(corner.At.X, corner.At.Z));
            return new VertexPosition3NormalTexture(
                new GraphicsVector3(corner.At.X, corner.At.Y, corner.At.Z),
                new GraphicsVector3(normal.X, normal.Y, normal.Z),
                // x is coverage. y is spare — two floats were already being uploaded and this only needed
                // one, and inventing a use for the other is how a channel acquires two meanings.
                new GraphicsVector2(corner.Coverage, 0f));
        }

        void AddTriangle(
            (Vector3 At, float Coverage) first,
            (Vector3 At, float Coverage) second,
            (Vector3 At, float Coverage) third)
        {
            var start = checked((ushort)vertices.Count);
            vertices.Add(Vertex(first));
            vertices.Add(Vertex(second));
            vertices.Add(Vertex(third));
            indices.Add(start);
            indices.Add((ushort)(start + 1));
            indices.Add((ushort)(start + 2));
        }

        // <b>The render grid is not the navigation grid, and that is the whole of what makes this
        // affordable.</b> Meshed cell for cell, the ground the camera can see at a two hundred metre
        // standoff is 1.8M triangles — measured, on the first version of this. The navigation grid is half a
        // metre because that is the resolution a body's clearance is decided at; nobody can see half a
        // metre from up there. A step of four samples the same height field every two metres, which is
        // sixteen times fewer triangles for a surface whose <em>shape</em> is unchanged — the heights come
        // from the same interpolation the simulation walks on, so the mesh sits on the ground rather than
        // near it.
        //
        // What it used to cost was colour resolution, and that is what coverage buys back: the step is
        // still two metres, but a boundary inside a cell is now a gradient across it rather than a decision
        // taken at its corner.
        for (var z = fromZ; z <= toZ; z += step)
        for (var x = fromX; x <= toX; x += step)
        {
            var cellX = (x - fromX) / step;
            var cellZ = (z - fromZ) / step;
            var owns = cover.DominantAt(cellX, cellZ) == slot;
            if (owns == blend) continue;

            var c00 = blend ? cover.Share(cellX, cellZ, slot) : 1f;
            var c10 = blend ? cover.Share(cellX + 1, cellZ, slot) : 1f;
            var c01 = blend ? cover.Share(cellX, cellZ + 1, slot) : 1f;
            var c11 = blend ? cover.Share(cellX + 1, cellZ + 1, slot) : 1f;
            // A coat nobody can see costs a draw and a quarter of a megabyte of vertices. Two per cent is
            // below what one step of an eight-bit channel can express.
            if (blend && MathF.Max(MathF.Max(c00, c10), MathF.Max(c01, c11)) < 0.02f) continue;

            var x0 = grid.Origin.X + x * size;
            var z0 = grid.Origin.Y + z * size;
            var x1 = x0 + step * size;
            var z1 = z0 + step * size;
            var v00 = (new Vector3(x0, terrain.VertexHeight(x, z), z0), c00);
            var v10 = (new Vector3(x1, terrain.VertexHeight(x + step, z), z0), c10);
            var v01 = (new Vector3(x0, terrain.VertexHeight(x, z + step), z1), c01);
            var v11 = (new Vector3(x1, terrain.VertexHeight(x + step, z + step), z1), c11);
            AddTriangle(v00, v01, v10);
            AddTriangle(v11, v10, v01);
        }
        return (vertices.ToArray(), indices.ToArray());
    }


    /// <summary>
    /// Slides the pick window, in steps of an eighth of it.
    /// </summary>
    /// <remarks>
    /// An eighth rather than a free drag, because the thing being chosen is a <em>framing</em> and a framing
    /// wants to be repeatable. Quantised steps mean a promising window can be nudged, compared against its
    /// neighbour and gone back to, and the number written down is one somebody can type again.
    /// </remarks>
    private void MoveWindow(float x, float z)
    {
        if (!mapLab) return;
        var step = LabWindowMetres * 0.125f;
        var room = MathF.Max(0f, (worldExtentMeters - LabWindowMetres) * 0.5f);
        labWindow = new Vector2(
            Math.Clamp(labWindow.X + x * step, -room, room),
            Math.Clamp(labWindow.Y + z * step, -room, room));
        cameraFocus = labWindow;
    }

    /// <summary>Draws the pick window on the ground, so a framing is something you can see.</summary>
    private void DrawWindow()
    {
        if (!mapLab) return;
        var half = LabWindowMetres * 0.5f;
        var a = labWindow + new Vector2(-half, -half);
        var b = labWindow + new Vector2(half, -half);
        var c = labWindow + new Vector2(half, half);
        var d = labWindow + new Vector2(-half, half);
        // Followed along the ground rather than drawn as four straight lines, because on relief a straight
        // line between two corners is mostly underground — and a window that disappears into a ridge is
        // exactly the window somebody is trying to judge.
        void Edge(Vector2 from, Vector2 to)
        {
            const int steps = 24;
            var previous = from;
            for (var i = 1; i <= steps; i++)
            {
                var next = Vector2.Lerp(from, to, i / (float)steps);
                AddGroundLine(previous, next, WindowColor, 0.6f);
                previous = next;
            }
        }

        Edge(a, b);
        Edge(b, c);
        Edge(c, d);
        Edge(d, a);
    }

    private static readonly Vector4 WindowColor = new(1.00f, 0.86f, 0.32f, 1f);

    /// <summary>
    /// What a 600 m window would be worth as a map, and the sentence describing it.
    /// </summary>
    /// <remarks>
    /// <b>"One map, one geographic sentence" is computable, so the lab should not make anybody hunt.</b> The
    /// rule the archetype layer is built on says a good map has one or two large statements, a few ways
    /// through, and a spread of country — every one of which is a thing this can count. So it counts them for
    /// every candidate window and says which framings are worth looking at.
    /// <para>
    /// The terms are the rule, stated as arithmetic:
    /// <list type="bullet">
    /// <item><b>Statements</b> — separators crossing the window. Two is the target, one is thin, four is
    /// theme-park geography, so the score peaks at two and falls off either side.</item>
    /// <item><b>Ways through</b> — connectors inside it. Two to four; none means a window cut in half.</item>
    /// <item><b>Variety</b> — how many kinds of country are present at more than a token share. A window that
    /// is all pasture has nothing to decide about.</item>
    /// <item><b>Somewhere to live</b> — the share that is level, dry and open. A dramatic window nobody can
    /// found in is not a map.</item>
    /// </list>
    /// </para>
    /// <para>
    /// It scores framings rather than choosing one, which is the point: it is an instrument, and the eye still
    /// decides. Its real job is to say when there is nothing good on a canvas at all — which is a fact about
    /// the generator and not about the person looking for it.
    /// </para>
    /// </remarks>
    private (float Score, string Why) ScoreWindow(Vector2 centre)
    {
        var layout = labPlan?.Layout;
        var half = LabWindowMetres * 0.5f;

        var statements = 0;
        if (layout is not null)
        {
            foreach (var separator in layout.Separators)
            {
                foreach (var point in separator.Path)
                {
                    if (MathF.Abs(point.X - centre.X) > half || MathF.Abs(point.Y - centre.Y) > half) continue;
                    statements++;
                    break;
                }
            }
        }

        var ways = 0;
        if (layout is not null)
        {
            foreach (var connector in layout.Connectors)
            {
                if (MathF.Abs(connector.At.X - centre.X) > half) continue;
                if (MathF.Abs(connector.At.Y - centre.Y) > half) continue;
                ways++;
            }
        }

        // The country, sampled on a coarse grid over the window. Coarse because this is a share and a share
        // does not need every cell — and it is run over dozens of candidate framings.
        var counts = new int[Enum.GetValues<Biome>().Length];
        const int samples = 24;
        var total = 0;
        var livable = 0;
        for (var z = 0; z < samples; z++)
        for (var x = 0; x < samples; x++)
        {
            var at = centre + new Vector2(
                (x + 0.5f) / samples - 0.5f,
                (z + 0.5f) / samples - 0.5f) * LabWindowMetres;
            if (!simulation.Terrain.Contains(at)) continue;
            var biome = Biomes.At(simulation.Terrain, at, labFloor, MathF.Max(1f, labSpan));
            counts[(int)biome]++;
            total++;
            if (biome is Biome.Meadow or Biome.Floodplain &&
                simulation.Terrain.SampleGrade(at) < 0.08f)
            {
                livable++;
            }
        }

        if (total == 0) return (0f, "off the canvas");
        var variety = 0;
        foreach (var count in counts)
        {
            if (count / (float)total >= 0.04f) variety++;
        }

        var livableShare = livable / (float)total;

        // Peaks at two statements, tails off at four. Written as a triangle rather than a curve because the
        // rule it encodes is a range and not a preference.
        var statementScore = statements switch
        {
            0 => 0f,
            1 => 0.55f,
            2 => 1f,
            3 => 0.85f,
            4 => 0.5f,
            _ => 0.2f,
        };
        var wayScore = ways switch { 0 => 0.15f, 1 => 0.6f, 2 => 1f, 3 => 1f, 4 => 0.9f, _ => 0.6f };
        // Four kinds of country is a map with regions in it; two is a map with a gradient.
        var varietyScore = Math.Clamp((variety - 1) / 3f, 0f, 1f);
        // A third of the window wanting to be lived on is comfortable; a tenth is a gauntlet.
        var livableScore = Math.Clamp((livableShare - 0.08f) / 0.30f, 0f, 1f);

        var score = statementScore * 0.34f + wayScore * 0.20f + varietyScore * 0.26f + livableScore * 0.20f;
        var why = $"{statements} statements, {ways} ways through, {variety} kinds of country, " +
                  $"{livableShare * 100f:F0}% you could found on";
        return (score, why);
    }

    /// <summary>
    /// Walks every framing on the canvas and reports the best few.
    /// </summary>
    /// <remarks>
    /// On an eighth-of-a-window grid, which is the same step the arrows move in — so every framing this
    /// suggests is one the arrows can actually reach and one the printed pick can name.
    /// </remarks>
    private void SurveyWindows()
    {
        if (!mapLab) return;
        var step = LabWindowMetres * 0.125f;
        var room = MathF.Max(0f, (worldExtentMeters - LabWindowMetres) * 0.5f);
        var span = (int)MathF.Floor(room / step);
        var found = new List<(float Score, Vector2 At, string Why)>();
        for (var z = -span; z <= span; z++)
        for (var x = -span; x <= span; x++)
        {
            var at = new Vector2(x * step, z * step);
            var (score, why) = ScoreWindow(at);
            found.Add((score, at, why));
        }

        found.Sort((first, second) => second.Score.CompareTo(first.Score));
        Console.WriteLine($"  best framings on this canvas ({found.Count} considered):");
        var shown = 0;
        var taken = new List<Vector2>();
        foreach (var candidate in found)
        {
            // Spread out, or the top five are five nudges of the same framing.
            var tooClose = false;
            foreach (var already in taken)
            {
                if (Vector2.Distance(already, candidate.At) < LabWindowMetres * 0.6f) tooClose = true;
            }

            if (tooClose) continue;
            taken.Add(candidate.At);
            Console.WriteLine(
                $"    {candidate.Score:F2}  --window {candidate.At.X:F0},{candidate.At.Y:F0}   " +
                $"{candidate.Why}");
            if (++shown >= 5) break;
        }
    }

    /// <summary>
    /// The lab's state and its controls, for the screen rather than the log.
    /// </summary>
    /// <remarks>
    /// Two lines: what this canvas <em>is</em>, and what the keys do. The second one exists because a lab has
    /// eight bindings nobody can be expected to remember, and the first because every one of them was already
    /// working while appearing not to — the state was printed to a console that a windowed run does not have
    /// in front of it.
    /// </remarks>
    private string LabStatus()
    {
        var profile = RegionProfile.For(labRegion);
        var (score, why) = ScoreWindow(labWindow);
        return
            $"{labArchetype} · {profile.Name} — {profile.Character}\n" +
            $"seed {labSeed} · scores {score:F2} ({why})\n" +
            $"{(mapTuning.DrainageFirst ? "DRAINAGE FIRST" : "eroded")} · {LabVerdict()}\n" +
            "Q/E archetype · I region · Y next seed · enter print";
    }

    /// <summary>
    /// Whether the map on screen is one the criteria would accept, and what is wrong with it if not.
    /// </summary>
    /// <remarks>
    /// <b>§154, and it is the point of the whole builder.</b> §151 wrote down what a map has to be true of and
    /// §152 measured two generators against it — in a table, from a sweep, over fifty-five maps at once. None
    /// of that told anybody looking at <em>this</em> map whether it was any good. So the verdict sits on the
    /// screen beside the seed that produced it: generate, look, read, turn a dial.
    /// <para>
    /// Cached on generation rather than computed per frame: it walks the navigation grid and floods every
    /// pool, which is a hundred thousand cells and change — fine once a map, absurd sixty times a second.
    /// </para>
    /// </remarks>
    private string LabVerdict()
    {
        if (labCriteria is not { } criteria) return "criteria not measured";
        var shortfalls = string.Join("; ", criteria.Shortfalls());
        return shortfalls.Length == 0
            ? $"PASSES — {criteria}"
            : $"FAILS — {shortfalls}";
    }

    private TerrainCriteria? labCriteria;

    /// <summary>
    /// Whether the woodland is patchy or evenly sprinkled, which no figure printed so far could tell.
    /// </summary>
    /// <remarks>
    /// <b>"There is a roughly equal density of trees everywhere that can have trees" is a claim about a
    /// distribution, and every number reported about woodland so far has been a total.</b> A count and a
    /// percentage-of-map-closed are both silent on whether that wood is one forest and a plain or an even haze
    /// over everything — which is exactly the difference being complained about.
    /// <para>
    /// Counted on a fifty-metre grid, which is about the size of a stand. The shape of the histogram is the
    /// answer: an even sprinkle piles every cell into one bucket, and real woodland is bimodal — a lot of cells
    /// with almost nothing and a lot with a great deal, and fewer in between than at either end.
    /// </para>
    /// </remarks>
    private void ReportWoodlandSpread()
    {
        const float cellMetres = 50f;
        var across = Math.Max(1, (int)MathF.Round(simulation.ExtentMeters / cellMetres));
        var counts = new int[across * across];
        foreach (ref readonly var node in simulation.Nodes.All)
        {
            if (!node.IsAlive || !node.IsStanding) continue;
            var local = (node.Position + new Vector2(simulation.ExtentMeters * 0.5f)) / cellMetres;
            var x = Math.Clamp((int)local.X, 0, across - 1);
            var z = Math.Clamp((int)local.Y, 0, across - 1);
            counts[z * across + x]++;
        }

        // Buckets in trees per hectare, since a fifty-metre cell is a quarter of one.
        var bare = 0;
        var thin = 0;
        var wooded = 0;
        var dense = 0;
        foreach (var count in counts)
        {
            var perHectare = count * 4;
            if (perHectare < 12) bare++;
            else if (perHectare < 90) thin++;
            else if (perHectare < 280) wooded++;
            else dense++;
        }

        var cells = (float)counts.Length;
        Console.WriteLine(
            $"    spread over {cellMetres:F0} m cells: {bare / cells * 100f:F0}% open, " +
            $"{thin / cells * 100f:F0}% stragglers, {wooded / cells * 100f:F0}% wooded, " +
            $"{dense / cells * 100f:F0}% dense forest");
    }

    /// <summary>Rolls a new landscape on the same archetype, or steps to the next one.</summary>
    private void RollLab(int archetypeStep, bool reseed)
    {
        // <b>No lab-only guard, and its absence is the whole fix.</b> This began as a lab function and kept
        // <c>if (!mapLab) return;</c> when the village branch was added below — so the village path compiled,
        // read correctly, and could never run. The key was wired, the handler was reached, the trace would have
        // printed, and nothing happened: an early return one screen above the code it disables is invisible at
        // the call site and invisible in the branch it skips.
        if (archetypeStep != 0)
        {
            var all = MapLayout.All;
            var index = Array.IndexOf(all, labArchetype);
            labArchetype = all[((index + archetypeStep) % all.Length + all.Length) % all.Length];
            // <b>Naming an archetype means asking about that archetype, so cycling pins.</b> It did not, and
            // the result was a key that did nothing at all: the canvas is filled from the pinned archetype or
            // from the whole family, pinning was off by default, and the seed did not move either — so Q and E
            // spent two seconds regenerating a byte-identical landscape. The state changed and the ground could
            // not. Same rule the --archetype flag already followed.
            labPinned = true;
            Console.WriteLine($"  pinned to {labArchetype}");
        }

        // <b>The panel is the source of truth for which map, outside the lab.</b> The lab cycles with keys
        // because it is a browser; the village reads the sliders, so a roll is always "another seed of the map I
        // asked for" rather than "another seed of whatever the last key press left behind".
        if (!mapLab)
        {
            labArchetype = mapTuning.Archetype;
            labRegion = mapTuning.Region;
            reliefAmplitudeMetres = MathF.Max(0f, mapTuning.ReliefMetres);
        }

        if (reseed)
        {
            // An integer avalanche on the old seed, so the sequence of rolls is itself reproducible: the
            // fourth landscape from a starting seed is always the same fourth landscape.
            var next = labSeed * 2654435761u + 0x9E3779B9u;
            labSeed = (next ^ (next >> 15)) * 2246822519u;
        }

        // A fresh world, because a roll is a different landscape and the surfaces, the navigation and the
        // drainage all belong to the one it replaces.
        // <b>Whatever scenario is running gets rebuilt, not just the ground.</b> The lab wants terrain and
        // nothing else; the village wants terrain <em>and a settlement founded on it</em>, because a village
        // standing where the old map's valley floor used to be is worse than no village. LoadSettlementScenario
        // builds its own world and calls LoadRelief itself, so the two paths differ by which one is asked.
        if (mapLab)
        {
            simulation = new SimulationWorld(worldExtentMeters);
            LoadRelief();
        }
        else
        {
            selection.Clear();
            LoadSettlementScenario();
        }

        ReportPick();
        if (mapLab) SurveyWindows();
    }

    /// <summary>
    /// Prints the triple that identifies this landscape, and the sentence it was built from.
    /// </summary>
    /// <remarks>
    /// The sentence is the acceptance test, not a label. A layout that cannot be said in a line has too much
    /// happening on six hundred metres of ground, so printing it beside the pick puts the rule where it will
    /// actually be read — next to the map it is judging.
    /// </remarks>
    private void ReportPick()
    {
        // Lab only, because outside it LoadRelief has already printed the triple, the sentence and the region
        // on its own — and a roll that reports itself twice reads like two rolls.
        if (!mapLab) return;
        var sentence = labPlan?.Layout?.Sentence ?? "no layout";
        var profile = RegionProfile.For(labRegion);
        // <b>The whole identity of a map, and it no longer needs a window.</b> Three values, because the map
        // is the frame: everything under them is deterministic, so this triple regenerates the ground exactly.
        Console.WriteLine(
            $"  pick: --region {labRegion} --archetype {labArchetype} --mapseed {labSeed}");
        Console.WriteLine($"    {profile.Name}: {profile.Character}");
        Console.WriteLine($"    {sentence}");
        if (labPlan is { } plan) Console.WriteLine($"    relief: {plan.Describe()}");
        var (score, why) = ScoreWindow(labWindow);
        Console.WriteLine($"    this framing scores {score:F2}: {why}");
    }

    public void OnResize(int width, int height)
    {
        if (height > 0) aspect = width / (float)height;
    }

    public string DebugName => "rts-movement";

    /// <summary>
    /// Draws each cascade's fitted box and the slice of camera frustum it was fitted to.
    /// </summary>
    /// <remarks>
    /// <b>Both shapes, because the failure mode is the two disagreeing.</b> A box that does not contain its
    /// slice produces shadows that stop somewhere in the middle of the view — which is exactly the bug the
    /// single box had twice, and which took four rounds of screenshots to characterise each time. Drawn
    /// together, "the green box does not reach the green slice" is a glance.
    /// <para>
    /// The slice is drawn as a frustum of its own, built from the same near and far the box was fitted to, so
    /// the two gizmos cannot disagree about what the slice is even if the fit is wrong.
    /// </para>
    /// </remarks>
    private void ShowCascades(DebugContext debug)
    {
        if (!cascades.ShowBoxes) return;
        debug.Draw.ViewProjection = camera.GetViewProjection(aspect);
        debug.Draw.Arrow(
            "sun/dir",
            new Vector3(cameraFocus.X, simulation.Terrain.SampleHeight(cameraFocus) + 30f, cameraFocus.Y),
            new Vector3(cameraFocus.X, simulation.Terrain.SampleHeight(cameraFocus), cameraFocus.Y),
            new GraphicsColor(1f, 0.92f, 0.3f, 1f));

        using (debug.Scope("cascades"))
        {
            for (var c = 0; c < ShadowCascades.Count; c++)
            {
                var tint = ShadowCascades.TintOf(c);
                debug.Draw.Frustum($"box/{c}", cascadeViewProjection[c], tint);
                // The slice itself: the same camera, clipped to this cascade's near and far.
                debug.Draw.Frustum(
                    $"slice/{c}",
                    camera.GetViewProjection(aspect, cascadeEdges[c], cascadeEdges[c + 1]),
                    new GraphicsColor(tint.Red * 0.55f, tint.Green * 0.55f, tint.Blue * 0.55f, 0.6f));
                debug.Values.Value(
                    $"cascade {c}",
                    $"{cascadeEdges[c]:F0}-{cascadeEdges[c + 1]:F0} m, box {cascadeSideMetres[c]:F0} m, " +
                    $"texel {cascadeSideMetres[c] / ShadowMapSize * 100f:F1} cm");
            }
        }
    }

    public void Debug(DebugContext debug)
    {
        // Always on for this testbed — it exists to be watched. The backtick key still
        // hides the panels, and freezing them does not stop a slider taking effect.
        debug.State.Enabled = true;
        if (!debugStateInitialized)
        {
            // A performance run still feeds the timing sink, but drawing and populating the interactive
            // panels would make the instrument part of the thing being measured.
            if (performanceRun) debug.State.ShowOverlay = false;
            debugStateInitialized = true;
        }

        // Closing the panel should close its producer too. ReportEconomy in particular asks several
        // settlement-wide questions; continuing to answer them while nothing can display the answers cost
        // more than four milliseconds in a wide Village frame. --timings deliberately keeps the producer
        // alive because its console sink is the requested output.
        if (!debug.State.ShowOverlay && !timingDebug) return;
        tunables.BuildControls(debug);
        crowdMetrics.Report(debug);
        ReportBodyScale(debug);
        using (debug.Scope("sim"))
        {
            debug.Values.Value("agents", simulation.Agents.Count);
            debug.Values.Value("tick", simulation.TickNumber);
            debug.Values.Value("contacts", simulation.LastContactCount);
            debug.Values.Value("flow-fields", simulation.FlowFieldBuilds);
            debug.Values.Value("astar", simulation.PathQueries);
            debug.Values.Value("detours", simulation.CongestionRepathCount);
            debug.Values.Value("congestion-peak", MathF.Round(simulation.Congestion.Peak, 1));
            var solves = Math.Max(1L, simulation.AvoidanceSolves);
            debug.Values.Value(
                "infeasible-%",
                MathF.Round(simulation.AvoidanceInfeasible * 100f / solves, 2));
            debug.Values.Value("dead-stops", simulation.AvoidanceTerrainDeadStops);
            debug.Stats.Gauge("contacts", simulation.LastContactCount);
            debug.Stats.Gauge("congestion-peak", simulation.Congestion.Peak);
        }

        ShowCascades(debug);
        ReportJobs(debug);
        ReportEconomy(debug);
    }

    /// <summary>
    /// The one thing §8 says the interface is for: how long this lasts without you.
    /// </summary>
    /// <remarks>
    /// Autonomy time is the score, the win condition and the soak assertion all at once — "granary: 2.4
    /// seasons at current draw" — so it is displayed as seasons rather than as a stock level. A stock
    /// level tells a player a number; seasons-until-empty tells them whether to do something about it,
    /// which is the only question the low-attention mode can afford to ask.
    /// </remarks>
    private void ReportEconomy(DebugContext debug)
    {
        if (simulation.Nodes.LiveCount == 0) return;
        var date = simulation.Date;
        using var scope = debug.Scope("settlement");
        debug.Values.Value("year", date.Year + 1);
        debug.Values.Value("season", date.Season.ToString());
        debug.Values.Value("day", date.Day);

        foreach (var resource in Resources.All)
        {
            var outlook = simulation.Economy.Outlook(
                resource, simulation.Nodes, simulation.Agents, date.Season);
            debug.Values.Value($"{resource} stored", outlook.Stored);
            debug.Values.Value(
                $"{resource} lasts",
                float.IsPositiveInfinity(outlook.Seasons) ? "growing" : $"{outlook.Seasons:F1} seasons");
            debug.Stats.Gauge($"{resource} stored", outlook.Stored);
        }

        var hands = simulation.Economy.HandsAtWork(simulation.Nodes);

        // Who is here, how much room is left for more, and whether the settlement can afford another
        // mouth. Readiness is the number worth watching: it is the brake on growth and it is continuous,
        // so the answer to a low figure is more farms rather than more houses — which a bare headcount
        // cannot tell you. Spare hands is the other half: a newcomer arrives idle on purpose, so this is
        // the count of people waiting to be given something to do.
        var room = 0;
        var hungry = 0;
        foreach (var id in simulation.Nodes.SettlementNodes)
        {
            ref readonly var node = ref simulation.Nodes.Get(id);
            if (!node.IsSink) continue;
            room += node.Housing;
            if (node.Privation > 0.5f) hungry++;
        }

        var spare = 0;
        var carts = 0;
        foreach (ref readonly var body in simulation.Agents.All)
        {
            if (!body.IsAlive) continue;
            if (body.HasCart) carts++;
            if (body.Role == AgentRole.Villager && !body.Jobs.HasAssignment && !body.Jobs.IsInterrupted) spare++;
        }

        debug.Values.Value("people", simulation.Agents.LiveCount);
        debug.Values.Value("housing spare", room);
        debug.Values.Value(
            "can feed one more", $"{simulation.Economy.Readiness * 100f:F0}%");
        debug.Values.Value("born / left", $"{simulation.Economy.Born} / {simulation.Economy.Emigrated}");
        if (hungry > 0) debug.Values.Value("households going hungry", hungry);
        debug.Values.Value("hands at work", hands);
        debug.Values.Value("spare hands", spare);
        debug.Values.Value("carts", carts);

        // What structural projects are waiting for, because "wants timber" and "idle site" are two
        // different problems fixed by opposite actions — one wants a cart, the other wants people — and a
        // settlement with no carter cannot get materials anywhere at all. Silence on this was the one gap
        // in the loop: a site with nobody to carry to it simply sat there.
        var sites = 0;
        var worst = string.Empty;
        foreach (ref readonly var node in simulation.Nodes.All)
        {
            if (!node.IsAlive || !node.HasStructuralProject) continue;
            sites++;
            if (worst.Length == 0 || node.WantsMaterials) worst = StructuralProjects.StateOf(in node);
        }

        if (sites > 0) debug.Values.Value("structural projects", $"{sites} — {worst}");
        debug.Values.Value("unhoused", simulation.UnhousedCount);
        debug.Values.Value("nodes", simulation.Nodes.LiveCount);
        debug.Values.Value("hauls", simulation.Economy.HaulsAssigned);
        debug.Values.Value("went short", simulation.Economy.Unmet.Grain + simulation.Economy.Unmet.Wood);
        debug.Stats.Gauge("people", simulation.Agents.LiveCount);
        debug.Stats.Gauge("readiness", simulation.Economy.Readiness);
    }

    /// <summary>
    /// What the standing arrangement is currently doing, which is the quantity the whole design
    /// is about: how much is happening without the player.
    /// </summary>
    /// <remarks>
    /// Four numbers rather than a list, because the useful reading is a proportion — how many
    /// of the units that have a job are actually working it, against how many are interrupted
    /// or cannot reach their place. When autonomy time becomes measurable in Session 6, this is
    /// the panel it grows out of.
    /// </remarks>
    private void ReportJobs(DebugContext debug)
    {
        var assigned = 0;
        var working = 0;
        var interrupted = 0;
        var stranded = 0;
        var legs = 0;
        foreach (ref readonly var agent in simulation.Agents.All)
        {
            if (!agent.IsAlive || !agent.Jobs.HasAssignment) continue;
            assigned++;
            legs += agent.Jobs.LegsCompleted;
            if (agent.Jobs.IsInterrupted) interrupted++;
            else if (agent.Jobs.CannotReachWork) stranded++;
            else working++;
        }

        using var scope = debug.Scope("jobs");
        debug.Values.Value("assigned", assigned);
        debug.Values.Value("working", working);
        debug.Values.Value("interrupted", interrupted);
        debug.Values.Value("cannot reach", stranded);
        debug.Values.Value("legs done", legs);
        debug.Stats.Gauge("working", working);
    }

    /// <summary>
    /// What the body sliders mean in the units the design argues in: distance, minutes, and
    /// how much map a player can answer for.
    /// </summary>
    /// <remarks>
    /// The point of this session is a decision, not a preference, and the decision is
    /// coupled: top speed sets reach, reach sets the contested fraction of the map, and the
    /// contested fraction is what the map size was chosen for. A slider that only shows
    /// "1.5" invites tuning it until the crowd looks nice and discovering afterwards that the
    /// map is now the wrong size. These are the same quantities §3 of the plan derives the
    /// map from, computed live from whatever the sliders currently say.
    /// </remarks>
    private void ReportBodyScale(DebugContext debug)
    {
        using var scope = debug.Scope("body scale");
        var speed = MathF.Max(0.01f, bodyFeel.MaximumSpeed);
        var compression = MathF.Max(0.01f, clock.Compression);
        var crossSeconds = simulation.ExtentMeters / speed;
        debug.Values.Value("map (m)", MathF.Round(simulation.ExtentMeters));
        debug.Values.Value("cross map (sim min)", MathF.Round(crossSeconds / 60f, 1));
        debug.Values.Value("cross map (wall min)", MathF.Round(crossSeconds / 60f / compression, 1));
        // How far a body gets in the time a player is willing to be away from one place.
        // This is "reach", and it is what decides how much of a map is contested.
        debug.Values.Value("reach in 40s (m)", MathF.Round(speed * 40f));
        debug.Values.Value(
            "spin-up (s)",
            MathF.Round(speed / MathF.Max(0.01f, bodyFeel.Acceleration), 2));
        debug.Values.Value(
            "stopping (m)",
            MathF.Round(speed * speed / (2f * MathF.Max(0.01f, bodyFeel.Deceleration)), 2));
        // A body that cannot complete a right-angle turn inside its own body length is
        // steering a vehicle, whatever the model looks like.
        debug.Values.Value(
            "quarter-turn (m)",
            MathF.Round(speed * (MathF.PI * 0.5f / MathF.Max(0.01f, bodyFeel.MaximumTurnSpeed)), 2));
    }

    /// <summary>
    /// Builds a map when the panel says to, and never otherwise.
    /// </summary>
    /// <remarks>
    /// <b>Configure and fire.</b> Nothing here watches the settings for changes — see the remarks on
    /// <see cref="MapTuning"/> for why reload-on-change is the wrong model for an operation that costs a
    /// second. Two toggles, read and cleared, and the only difference between them is whether the seed moves.
    /// </remarks>
    private void FollowMapPanel()
    {
        if (mapLab) return;

        // <b>Two ways in, one decision.</b> A button member is <em>driven</em> by the panel: BuildControls
        // writes it false on every frame nobody clicks, so setting it from code is overwritten before it can be
        // read — which silently stopped the soak the moment these became real buttons. So a scripted press is
        // its own request rather than a poke at the button's state, and both arrive here.
        var newSeed = mapTuning.NewSeed || askedNewSeed;
        var generate = mapTuning.Generate || askedGenerate;
        askedNewSeed = false;
        askedGenerate = false;
        // Read and clear, which is the contract for a button whether or not the panel would have cleared it
        // for us on the next frame. It also stops the compiler calling these write-only-by-reflection, which
        // is a warning worth not learning to ignore.
        mapTuning.NewSeed = false;
        mapTuning.Generate = false;
        if (newSeed)
        {
            RollLab(0, reseed: true);
            return;
        }

        if (generate) RollLab(0, reseed: false);
    }

    /// <summary>A scripted press of "new seed", for the headless soak. See FollowMapPanel.</summary>
    private bool askedNewSeed;

    /// <summary>A scripted press of "generate", for the headless soak.</summary>
    private bool askedGenerate;

    /// <summary>
    /// Which map is on screen, for the corner of the screen.
    /// </summary>
    /// <remarks>
    /// <b>Because the lab already learned this and the village had to learn it again.</b> The remarks above
    /// <c>SettlementHud</c>'s lab block say it outright — "a lab you have to read a terminal for is not a lab.
    /// Every one of its controls worked and none of them appeared to" — and then the village shipped its map
    /// panel with the identity printed only to the console. Reported from the chair as a suspicion that
    /// changing the dropdowns reloaded without changing the map. It did change it: measured across the eight
    /// archetypes at one seed, the share of low ground runs from 5% to 88%. <b>What was missing was any way to
    /// tell</b>, and two downland river valleys are hard to tell apart by eye even when everything about their
    /// topology differs.
    /// <para>
    /// So the seed is on it too. A seed is the one thing that makes "did that do anything" answerable at a
    /// glance, because it changes on a reroll and holds still on a regenerate.
    /// </para>
    /// </remarks>
    private string MapStatus()
    {
        var profile = RegionProfile.For(labRegion);
        var sentence = labPlan?.Layout?.Sentence ?? "flat ground";
        return $"{labArchetype} · {profile.Name} · {reliefAmplitudeMetres:F0} m relief · seed {labSeed}\n" +
               sentence;
    }

    public void OnUpdate(Time time)
    {
        var updateStart = Stopwatch.GetTimestamp();
        FollowMapPanel();

        // Between frames rather than inside one, which is where a keypress lands too: a roll replaces the
        // world the render is holding references into.
        if (rollEveryFrames > 0 && frameCount > 0 && frameCount % rollEveryFrames == 0 && frameCount != rolledAt)
        {
            rolledAt = frameCount;
            rolls++;
            // <b>Through the panel, not through RollLab.</b> Calling the function directly proved the function
            // and nothing else — and the bug this soak was written for was never in the function, it was a
            // guard one screen above it. What a person actually does is set the controls and fire, so that is
            // what this does: alternate a "new seed" press with "step the archetype dropdown, then generate",
            // which between them exercise both actions and the configure-then-fire order.
            if (rolls % 2 == 1)
            {
                Console.WriteLine($"  panel asks for a new seed at frame {frameCount}");
                askedNewSeed = true;
            }
            else
            {
                var all = MapLayout.All;
                var next = all[(Array.IndexOf(all, mapTuning.Archetype) + 1 + all.Length) % all.Length];
                Console.WriteLine($"  panel sets {next} and generates at frame {frameCount}");
                mapTuning.Archetype = next;
                askedGenerate = true;
            }
        }

        // Before stepping, so a slider moved this frame is felt this frame.
        bodyFeel.Observe(simulation);
        bodyFeel.Apply(simulation);
        // Compression scales wall-clock time on the way in, never the bodies. The spiral
        // guard scales with it too: a quarter-second of catch-up at 1x is a quarter-second
        // of catch-up at 3x only if the ceiling moves as well.
        var compression = Math.Clamp(clock.Compression, 0.25f, 6f);
        simulationAccumulator += Math.Clamp(time.Delta * compression, 0.0, 0.25 * compression);
        while (simulationAccumulator >= SimulationWorld.FixedDeltaSeconds)
        {
            simulation.Tick((float)SimulationWorld.FixedDeltaSeconds);
            // Inside the fixed step, so a raid arrives at the same moment whatever the time compression is
            // and a watched run sees what a headless one would.
            raiders?.Update(simulation, (float)SimulationWorld.FixedDeltaSeconds);
            // Inside the fixed step, on the same reasoning as the raid director above: a player that decides
            // with the tick decides at the same moment however fast the clock is running, so what is watched
            // from the chair is what a headless run would have produced.
            opponentBot?.Update(simulation);
            playerBot?.Update(simulation);
            // Look at the raid when it appears. A test-bench convenience: finding out whether defence is
            // interesting requires being able to see the fight.
            if (raiders?.TakeLookAt() is { } lookAt) cameraFocus = lookAt;
            movementTrace?.Observe(simulation, (float)SimulationWorld.FixedDeltaSeconds);
            simulationAccumulator -= SimulationWorld.FixedDeltaSeconds;
        }
        crowdMetrics.Sample(simulation);

        if (timingDebug && time.Total >= nextTimingReport)
        {
            Console.WriteLine($"  {simulation.Timings.Format(simulation.Agents.Count, simulation.TickNumber)}");
            // <b>Who asked for routing this second, because §119's stall was invisible without it.</b> A
            // player clicked, the settlement stalled for ten seconds, and the timings line could say only
            // that the time was in the Jobs stage — which is where the cost lands, not what caused it. The
            // phase columns above are stages; this is callers, and the two answer different questions.
            //
            // Silent when nothing asked, which is nearly every second: this prints beside a line that is
            // already dense, and a "routing: nothing" every second would train the eye to skip both.
            foreach (var line in simulation.Routes.Describe(routesAtLastReport, routeLogAtLastReport))
            {
                Console.WriteLine($"  ROUTES {line}");
            }
            routesAtLastReport = simulation.Routes.Snapshot();
            routeLogAtLastReport = simulation.Routes.Recorded;

            // <b>Whether the opponent is alive, which the chair could not otherwise tell.</b> §133's run
            // mentioned faction 1 exactly twice in three hundred and fifty kilobytes of log — once at its
            // founding — so a neighbour standing idle and a neighbour thriving looked identical from here.
            // That is §130's failure mode exactly: an inert settlement satisfies every impression of a
            // working one, and the only reason the headless probe catches it is that it asserts on a number
            // that has to move.
            //
            // <b>A diagnostic and never a HUD element.</b> It reports the neighbour's stores, which a player
            // has no business seeing — it is behind --timings, which is a development flag, and it must not
            // migrate into anything a person plays with. The bot's own counters are the honest half: they say
            // whether it is deciding and acting, which is the question being asked.
            if (opponentBot is { } bot)
            {
                var theirGrain = simulation.Economy.Outlook(
                    Resource.Grain, simulation.Nodes, simulation.Agents, simulation.Date.Season, bot.Faction);
                var theirWood = simulation.Economy.Outlook(
                    Resource.Wood, simulation.Nodes, simulation.Agents, simulation.Date.Season, bot.Faction);
                var theirPeople = 0;
                foreach (ref readonly var body in simulation.Agents.All)
                {
                    if (body.IsAlive && body.Faction == bot.Faction) theirPeople++;
                }

                Console.WriteLine(
                    $"  OPPONENT faction {bot.Faction.Value}: {theirPeople} people, " +
                    $"grain {theirGrain.Stored:N0} ({Seasons(theirGrain.Seasons)}), " +
                    $"wood {theirWood.Stored:N0} ({Seasons(theirWood.Seasons)}) · " +
                    $"{bot.Decisions:N0} decisions, {bot.OrdersIssued:N0} assignments issued");
            }
            Console.WriteLine($"  DRESSING {GeometryLine()}");
            nextTimingReport = time.Total + 1.0;
        }

        var frame = (float)Math.Clamp(time.Delta, 0.0, 0.25);
        // Smoothed over about half a second, so the figure is readable rather than a flicker.
        frameMilliseconds += (time.Delta * 1000.0 - frameMilliseconds) * 0.08;
        rawFrameMilliseconds = time.Delta * 1000.0;
        // Before anything that reads the light: the shadow box, the sky and the world shader all take their
        // sun from here and a disagreement between them is a scene lit from one place and shadowed from
        // another.
        // Simulated seconds, not wall time: the calendar runs on the tick count, so anything meant to keep
        // step with it has to read the same clock. At three times compression the wall clock advances a
        // third as fast as the date does.
        // <b>Which clock the sun reads, which is now three answers and not two.</b> §144: the hour comes
        // from the sim clock and the declination from the date, and those were always separate inputs with
        // one switch between them. Year-only pins the hour at noon and leaves the date running, so a year's
        // swing is watchable without the daily cycle making every two frames incomparable — at 3x
        // compression a day is forty seconds, which is the frequency that spoils a comparison.
        var skySeconds = performanceHour >= 0f
            ? performanceHour / 24f * Atmosphere.DayLengthSeconds
            : look.Motion == LookSettings.SunMotion.YearOnly
                ? NoonSeconds
                : simulation.TickNumber / 30.0;
        sky = Atmosphere.For(
            simulation.Date,
            skySeconds,
            look.SunBearingDegrees,
            look.Seasonality);
        AdvanceWear(frame);
        frameSeconds = frame;
        var fogStart = Stopwatch.GetTimestamp();
        AdvanceFog();
        UploadFogTexture();
        fogMilliseconds = (Stopwatch.GetTimestamp() - fogStart) * 1000.0 / Stopwatch.Frequency;
        ApplyWoodlandCover(frame);
        TurnAndZoom(frame);
        PanCamera(frame);
        ApplyPerformanceCameraMotion();
        UpdateCameraFocus(frame);
        UpdateCamera();
        UpdatePointerWorld();
        updateMilliseconds = (Stopwatch.GetTimestamp() - updateStart) * 1000.0 / Stopwatch.Frequency;
    }

    private void UpdateCamera()
    {
        const float elevation = CameraElevation;
        var horizontal = MathF.Cos(elevation) * cameraDistance;
        // <b>The focus sits on the ground, and it used to sit at sea level.</b> This is one bug behind two
        // complaints, and it is worth writing out because everything geometric in this file is measured from
        // this point.
        //
        // The village's ground is at -47.7 m. With the focus pinned to y = 0 the camera aimed at a point
        // forty-eight metres up in clear air, and at full zoom-in its eye — 5.9 m above the focus — stood
        // <em>fifty-three metres above the terrain</em>. So the closest the camera could get was still a
        // middle-distance view, reported from the chair as not being able to zoom in far enough. And the pan
        // speed is a share of the standoff, which at that zoom is 8.8 m/s while the eye is fifty metres up
        // seeing a hundred metres of ground: reported as the camera massively slowing down. Same cause.
        //
        // It also puts the derived reaches on their proper datum. VisibleGroundRadius and GroundBand both take
        // the eye height as sin(pitch) x cameraDistance — the height above <em>this</em> point — so with the
        // focus off the ground every draw distance in the frame was computed for a camera much lower than the
        // one that was drawing it. The comment on the ground-chunk cull already describes the symptom: the far
        // edge of the view comes from intersecting the frustum with a plane at the focus height, "exactly
        // right on the flat ground it was written against and wrong the moment the map has hills in it".
        // Putting the focus on the ground is what makes that plane the right plane.
        //
        // Eased, because the alternative is a camera that bobs over every hillock it pans across, and snapped
        // when the step is large so a map load or a jump to a selection does not fly the camera in.
        var groundAtFocus = simulation.Terrain.SampleHeight(cameraFocus);
        if (MathF.Abs(groundAtFocus - cameraGroundHeight) > 25f || frameSeconds <= 0f)
        {
            cameraGroundHeight = groundAtFocus;
        }
        else
        {
            cameraGroundHeight += (groundAtFocus - cameraGroundHeight) *
                (1f - MathF.Exp(-frameSeconds * 6f));
        }

        var focus = new Vector3(cameraFocus.X, cameraGroundHeight, cameraFocus.Y);
        var eye = focus + new Vector3(
            MathF.Sin(cameraYaw) * horizontal,
            MathF.Sin(elevation) * cameraDistance,
            MathF.Cos(cameraYaw) * horizontal);
        camera.Transform.Position = eye;
        camera.Transform.LookAt(focus, Vector3.UnitY);
        // The far plane has to follow the zoom or one of the two is always wrong: too near and the world
        // is clipped away when you pull back, too far and the depth buffer's precision is spent on empty
        // sky when you are close. Four times the standoff plus a margin covers the ground behind the focus
        // at this elevation at every zoom.
        camera.FarPlane = cameraDistance * 4f + 120f;
    }

    /// <summary>
    /// Repaints forest cover when its dials have stopped moving.
    /// </summary>
    /// <remarks>
    /// Debounced rather than applied on change, because a repaint is 80 ms and the navigation rebuild that
    /// follows it is about a second on a 600 m map with ten thousand trees — so a slider dragged across its
    /// range would fire fifty of them and the game would stop. Waiting for the value to hold still for a
    /// third of a second gives one repaint per adjustment, which is what a person dragging a slider
    /// actually wants.
    /// <para>
    /// The raster rebuild itself is not triggered here: painting bumps the terrain revision and
    /// <c>Tick</c> notices, so a hundred thousand changed cells still cost one rebuild.
    /// </para>
    /// </remarks>
    private void ApplyWoodlandCover(float deltaSeconds)
    {
        var current = new Vector2(Woodland.CoverTrees, Woodland.CoverRadius);
        if (current != woodlandRequested)
        {
            woodlandRequested = current;
            woodlandSettle = 0.33f;
            return;
        }

        if (woodlandRequested == woodlandApplied) return;
        woodlandSettle -= deltaSeconds;
        if (woodlandSettle > 0f) return;
        woodlandApplied = woodlandRequested;
        simulation.RefreshForestCover();
        Console.WriteLine(
            $"  woodland: {Woodland.CoverTrees} trees within {Woodland.CoverRadius:F1} m closes ground " +
            $"— {ClosedGroundShare(simulation) * 100f:F1}% of the map is now wood");
        ReportWoodlandSpread();
    }

    private Vector2 woodlandRequested = new(-1f, -1f);
    private Vector2 woodlandApplied = new(-1f, -1f);
    private float woodlandSettle;

    /// <summary>Share of the map that is impassable wood, which is what the dials are judged on.</summary>
    private static float ClosedGroundShare(SimulationWorld world)
    {
        var transform = world.Terrain.Transform;
        var closed = 0;
        for (var z = 0; z < transform.Height; z++)
        for (var x = 0; x < transform.Width; x++)
        {
            if (world.Terrain.Surface(new GridCell(x, z)) == TerrainSurface.Forest) closed++;
        }

        return closed / (float)MathF.Max(1, transform.Width * transform.Height);
    }

    /// <summary>
    /// Moves the camera over the map: held arrow keys, and dragging the ground with the middle button.
    /// </summary>
    /// <remarks>
    /// There was no panning at all, which made the camera's only way of getting anywhere the fact that it
    /// followed whatever was selected — so looking at a corner of the map meant selecting something in it,
    /// and the view drifted whenever a selected body walked. Following is still available on Z and is off
    /// by default now, because a camera that moves on its own is a surprise once there is a way to move it
    /// deliberately.
    /// <para>
    /// Arrow panning is in <em>screen</em> space rather than world space: "left" means left on the monitor,
    /// which after a 90-degree rotation is a different compass direction. Anything else is unusable the
    /// first time somebody presses Q.
    /// </para>
    /// </remarks>
    /// <summary>How near an edge the pointer has to be to start pushing the camera, in pixels.</summary>
    /// <remarks>
    /// A band rather than the last pixel, because the last pixel is unreachable on a trackpad and because
    /// the camera should start moving <em>before</em> the thing you are chasing has left the screen. Twelve
    /// is about a finger's width of slop at this scale and does not trigger while reading the panels, which
    /// sit inside it.
    /// </remarks>
    private const float EdgePanBand = 12f;

    /// <summary>Swings the camera while Q or E is held, and eases the zoom toward where the wheel put it.</summary>
    /// <remarks>
    /// <b>Both are the same complaint: a camera that arrives instead of moving.</b> The wheel used to set the
    /// distance outright, so every notch was a jump — which at a proportional step is a jump that gets
    /// bigger the further out you are. Easing it costs one lerp and turns a series of jumps into a motion,
    /// and because the target is still set instantly the control stays as responsive as it was.
    /// </remarks>
    private void TurnAndZoom(float deltaSeconds)
    {
        var turn = (turnRight ? 1f : 0f) - (turnLeft ? 1f : 0f);
        if (turn != 0f)
        {
            cameraYaw += turn * CameraTurnDegreesPerSecond * MathF.PI / 180f * deltaSeconds;
        }

        // Exponential ease, so it is frame-rate independent and has no overshoot: a spring would wobble at
        // the end of every notch, which on a zoom reads as the ground breathing.
        var follow = 1f - MathF.Exp(-deltaSeconds * 14f);
        cameraDistance += (cameraDistanceTarget - cameraDistance) * follow;
    }

    private void PanCamera(float deltaSeconds)
    {
        // <b>Not on a measurement run.</b> Edge panning follows the pointer, and a --frames run opens a
        // window under whatever the mouse was doing — so four hundred frames later the camera is in an empty
        // corner and the numbers describe nothing. Measured that way once: 62 trees and no chimneys in view
        // on a map with nine thousand trees, and a frame time to match.
        if (exitAfterFrames > 0) return;
        var x = (panRight ? 1f : 0f) - (panLeft ? 1f : 0f);
        var z = (panUp ? 1f : 0f) - (panDown ? 1f : 0f);

        // <b>And the pointer against an edge pushes too</b>, which is how every game of this shape has
        // worked for thirty years and is the only way to pan while dragging a selection. Proportional
        // within the band rather than on or off: a pointer just inside the edge nudges and one hard against
        // it moves at full speed, so a small correction does not fling the camera.
        var (width, height) = host.LogicalSize;
        if (width > 0 && height > 0 && !selection.IsPointerDown)
        {
            x += EdgePush(mouseX, width);
            z -= EdgePush(mouseY, height);
        }

        if (x == 0f && z == 0f) return;
        // Screen right and screen "into the distance", on the ground plane, at the current yaw.
        //
        // <b>Right was left.</b> Reported, and it had been wrong since the arrows were wired: rotating the
        // forward vector the other way round the Y axis gives the vector that points off the left of the
        // screen, so every sideways pan and now every edge push went the opposite way from the key or the
        // pointer that asked for it. The only reason it survived is that a symmetrical control is still
        // usable while being exactly wrong.
        var forward = new Vector2(MathF.Sin(cameraYaw), MathF.Cos(cameraYaw));
        var right = new Vector2(forward.Y, -forward.X);
        var move = right * x - forward * z;
        if (move.LengthSquared() > 1f) move = Vector2.Normalize(move);
        cameraFocus = simulation.Terrain.ClampPosition(
            cameraFocus + move * (cameraDistance * CameraPanSharePerSecond * deltaSeconds));
    }

    /// <summary>Moves a sealed performance run without accepting live input.</summary>
    private void ApplyPerformanceCameraMotion()
    {
        if (!performanceRun || performanceCameraMotion == PerformanceCameraMotion.Still) return;
        if (!performanceCameraInitialized)
        {
            performanceCameraOrigin = cameraFocus;
            performanceCameraYaw = cameraYaw;
            performanceCameraInitialized = true;
        }

        // Frame-derived rather than wall-time-derived: two runs visit the same view on the same frame even
        // when the very performance difference being measured changes how long that frame took.
        var phase = frameCount * 0.035f;
        switch (performanceCameraMotion)
        {
            case PerformanceCameraMotion.Pan:
            {
                var right = new Vector2(MathF.Cos(performanceCameraYaw), -MathF.Sin(performanceCameraYaw));
                cameraFocus = simulation.Terrain.ClampPosition(
                    performanceCameraOrigin + right * (MathF.Sin(phase) * cameraDistance * 0.42f));
                break;
            }
            case PerformanceCameraMotion.Rotate:
                cameraYaw = performanceCameraYaw + frameCount * 1.5f * MathF.PI / 180f;
                break;
            case PerformanceCameraMotion.Zoom:
            {
                var far = MathF.Max(46f, MathF.Min(cameraFurthest, startingZoomMetres > 0f
                    ? startingZoomMetres
                    : cameraFurthest));
                const float near = 24f;
                cameraDistance = cameraDistanceTarget =
                    near + (far - near) * (0.5f + 0.5f * MathF.Sin(phase));
                break;
            }
        }
    }

    /// <summary>How hard the pointer is pushing against one axis, from -1 to 1.</summary>
    private static float EdgePush(float at, float extent)
    {
        if (at <= EdgePanBand) return -(1f - MathF.Max(0f, at) / EdgePanBand);
        if (at >= extent - EdgePanBand) return 1f - MathF.Max(0f, extent - at) / EdgePanBand;
        return 0f;
    }

    /// <summary>
    /// Keeps the camera over whatever is selected, so a long walk can be watched rather than
    /// inferred from the moment it leaves the screen.
    /// </summary>
    private void UpdateCameraFocus(float deltaSeconds)
    {
        if (!cameraFollowsSelection) return;
        var centroid = Vector2.Zero;
        var counted = 0;
        foreach (var id in selection.Snapshot())
        {
            ref readonly var agent = ref simulation.Agents.Get(id);
            if (!agent.IsAlive) continue;
            centroid += agent.Position;
            counted++;
        }

        if (counted == 0) return;
        centroid /= counted;
        // Eased rather than snapped: a camera that tracks a crowd exactly makes the crowd
        // look stationary, which is the opposite of what a session about how movement feels
        // wants to show.
        var follow = 1f - MathF.Exp(-deltaSeconds * 2.2f);
        cameraFocus += (centroid - cameraFocus) * follow;
    }

    private void UpdatePointerWorld()
    {
        pointerOnTerrain = TryScreenToGround(mouseX, mouseY, out var world) && simulation.Terrain.Contains(world);
        if (pointerOnTerrain) pointerWorld = simulation.Terrain.ClampPosition(world);
    }

    private bool TryScreenToGround(float screenX, float screenY, out Vector2 world)
    {
        var (width, height) = host.LogicalSize;
        var ray = camera.ScreenPointToRay(screenX, screenY, width, height);
        return simulation.Terrain.TryRaycast(ray.Origin, ray.Direction, out world);
    }

    /// <summary>
    /// The sun's shadow view-projection for this frame, snapped to its own texel grid.
    /// </summary>
    /// <remarks>
    /// Centred on what the camera is looking at, because the box is 150 m and the map is 600.
    /// Snapped because a box that slides continuously slides in sub-texel steps, and every
    /// shadow edge in the scene then crawls and shimmers as the camera pans — the artefact is
    /// far more distracting than the low resolution it would otherwise be hiding.
    /// </remarks>
    /// <summary>
    /// The sun's box, fitted to the camera frustum itself rather than to a radius around the focus.
    /// </summary>
    /// <remarks>
    /// <b>The frustum is the authority on what is being looked at, and this was the last thing still guessing at
    /// it.</b> Every other consumer was moved onto <see cref="InView"/> or onto the frustum-fitted ground cull;
    /// the shadow box went on being a square of side <c>2 x DetailRadius</c> centred on <c>cameraFocus</c>, sized
    /// from a scalar that is itself a flat-plane estimate. That is why shadows stopped in a circle inside the
    /// view: a disc and a trapezoid, again, one layer up.
    /// <para>
    /// <b>Fitted properly: unproject the frustum's eight corners, put them in the light's frame, take the
    /// bounds.</b> Then the box is exactly the shape of what the camera can see, from the sun's point of view —
    /// tight when looking down, long when looking along the ground, and correct on relief without a correction
    /// term, because frustum corners carry no assumption about where the ground is.
    /// </para>
    /// <para>
    /// <b>What it is fitted to is a slice, and which slices there are is the interesting question.</b> One box
    /// over the whole frustum has to be truncated somewhere or it spends every texel on ground nobody is
    /// looking at — and truncating it is what left the far corners in flat light. Three boxes over three
    /// slices of <see cref="GroundBand"/> is the answer, and the band matters as much as the count: slices of
    /// the raw frustum put two of the three in empty air above the terrain.
    /// </para>
    /// <para>
    /// Still snapped to its own texel grid, and it has to be: a box that slides continuously slides in sub-texel
    /// steps and every shadow edge in the scene crawls as the camera pans. Snapping now happens in the light's
    /// frame rather than in world XZ, which is where it always belonged — the old version snapped the centre in
    /// world space while the box orientation came from the sun, so the grid it snapped to was not the grid the
    /// texels were on.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Fits a box to one slice of the camera frustum, in the light's frame.
    /// </summary>
    /// <remarks>
    /// <b>A bounding sphere of the slice's corners rather than an axis-aligned fit, which is what the Sponza
    /// implementation does and is better than what I wrote for the single box.</b> A sphere is invariant under
    /// rotation, so the box stops changing size as the camera turns — and a box that resizes as you pan changes
    /// its texel size every frame, which makes every shadow edge in the scene breathe. An axis-aligned fit to
    /// the same corners does not have that property.
    /// </remarks>
    private Matrix4x4 FitCascade(float near, float far, int mapSize, int cascadeIndex, out float side)
    {
        var eye = camera.Transform.Position;
        var ahead = camera.Transform.Forward;
        var sideways = camera.Transform.Right;
        var overhead = camera.Transform.Up;
        var tall = MathF.Tan(camera.VerticalFieldOfView * 0.5f);
        var wide = tall * aspect;

        Span<Vector3> corners = stackalloc Vector3[8];
        var at = 0;
        for (var slice = 0; slice < 2; slice++)
        {
            var away = slice == 0 ? near : far;
            var middle = eye + ahead * away;
            var h = away * tall;
            var w = away * wide;
            corners[at++] = middle - sideways * w - overhead * h;
            corners[at++] = middle + sideways * w - overhead * h;
            corners[at++] = middle - sideways * w + overhead * h;
            corners[at++] = middle + sideways * w + overhead * h;
        }

        var centre = Vector3.Zero;
        for (var i = 0; i < corners.Length; i++) centre += corners[i];
        centre /= corners.Length;
        var radius = 0f;
        for (var i = 0; i < corners.Length; i++)
        {
            radius = MathF.Max(radius, Vector3.Distance(corners[i], centre));
        }

        radius = MathF.Ceiling(radius);
        side = MathF.Max(20f, radius * 2f + ShadowReachMetres);

        var toLight = Vector3.Normalize(SunDirection);
        var up = MathF.Abs(toLight.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var forward = -toLight;
        var right = Vector3.Normalize(Vector3.Cross(up, forward));
        var above = Vector3.Cross(forward, right);
        // Snapped on the light's own axes, which is the grid the texels are on. Snapping the centre in world XZ
        // — as the single box did — snaps to a grid the texels are not aligned with unless the sun happens to
        // be axis-aligned, so the shimmer it is meant to stop only partly stops.
        var texel = side / mapSize;
        var snapped =
            right * (MathF.Round(Vector3.Dot(centre, right) / texel) * texel) +
            above * (MathF.Round(Vector3.Dot(centre, above) / texel) * texel) +
            forward * Vector3.Dot(centre, forward);

        cascadeCentre[cascadeIndex] = snapped;
        var away2 = radius + SunDistance;
        return GraphicsMatrices.SunShadowViewProjection(
            SunDirection, snapped, away2, side, 20f, away2 * 2f + side);
    }

    /// <summary>Fits every cascade for this frame. Cheap: eight corners and a sphere each.</summary>
    private void FitCascades()
    {
        var band = GroundBand;
        cascades.Slices(band.Near, band.Far, cascadeEdges);
        for (var c = 0; c < ShadowCascades.Count; c++)
        {
            cascadeViewProjection[c] = FitCascade(
                cascadeEdges[c], cascadeEdges[c + 1], CascadeMapSize(c), c, out var side);
            cascadeSideMetres[c] = side;
            // <b>What a caster is tested against, and it has to be the box rather than the focus.</b> The
            // single box was centred on the focus, so a radius round the focus was the box; these are fitted
            // to slices of frustum lying away down the view, and the same radius culls a tree that is well
            // inside the far cascade for being far from the camera's own centre. That is the flat-ground
            // constant in yet another costume — a bound derived from where the camera is standing, applied to
            // geometry positioned by where it is looking.
            //
            // A radius rather than the square, because the box was fitted to a sphere of exactly this size:
            // the corners of the square hold nothing the sphere did not.
            cascadeCastAt[c] = new Vector2(cascadeCentre[c].X, cascadeCentre[c].Z);
            cascadeCastRadiusSquared[c] = side * 0.48f * side * 0.48f;
            MemoryMarshal.Write(cascadePush[c].AsSpan(0, 64), in cascadeViewProjection[c]);
        }
    }

    /// <summary>Where the shadowed depth is split, and whether to draw the boxes. See ShadowCascades.</summary>
    private readonly ShadowCascades cascades = new();

    /// <summary>Each cascade's fitted view-projection, kept for the gizmos and, later, for the passes.</summary>
    private readonly Matrix4x4[] cascadeViewProjection = new Matrix4x4[ShadowCascades.Count];

    /// <summary>Each cascade's near and far distance along the view, in metres.</summary>
    private readonly float[] cascadeEdges = new float[ShadowCascades.Count + 1];

    /// <summary>Each cascade's box side in metres, which is what its texel size is computed from.</summary>
    private readonly float[] cascadeSideMetres = new float[ShadowCascades.Count];

    /// <summary>Each cascade's box centre in world space, snapped to its own texel grid.</summary>
    private readonly Vector3[] cascadeCentre = new Vector3[ShadowCascades.Count];

    /// <summary>Each box as a circle on the ground, which is what the caster test needs.</summary>
    private readonly Vector2[] cascadeCastAt = new Vector2[ShadowCascades.Count];

    private readonly float[] cascadeCastRadiusSquared = new float[ShadowCascades.Count];

    private static List<InstanceData>[] CascadeInstanceLists()
    {
        var result = new List<InstanceData>[ShadowCascades.Count];
        for (var c = 0; c < result.Length; c++) result[c] = new List<InstanceData>();
        return result;
    }

    /// <summary>
    /// Which cascades a sealed run is allowed to record casters into.
    /// </summary>
    /// <remarks>
    /// <b>An ablation, and the cheapest one available: the whole caster path passes through one mask.</b>
    /// §83 left the frame attributed to the GPU by inference and named per-cascade caster geometry as the
    /// largest lever — 16.7M submitted caster triangles against the scene's 8.1M at the wide view. Then the
    /// art turned out to be casting from the coarsest level its chain holds already (454 triangles a tree,
    /// <c>casterLod: 3</c>), so there is no coarser mesh to give the far boxes and the 16.7M is three copies
    /// of a mesh that is already as cheap as the cook made it.
    /// <para>
    /// Which makes the question "what would the two coarse cascades be worth if their casters cost nothing" —
    /// and that is the ceiling on any proxy-geometry work, measurable in an afternoon instead of assumed after
    /// building one. Masking here rather than skipping the draws keeps the batches' Begin/End pairing intact:
    /// a batch begun and not ended is a different bug wearing this experiment's clothes.
    /// </para>
    /// </remarks>
    private int performanceCascadeMask = -1;

    /// <summary>Every fitted shadow box which can receive a caster at this ground position.</summary>
    private int CascadeMaskAt(Vector2 at)
    {
        var mask = 0;
        for (var c = 0; c < ShadowCascades.Count; c++)
        {
            if (Vector2.DistanceSquared(at, cascadeCastAt[c]) <= cascadeCastRadiusSquared[c])
            {
                mask |= 1 << c;
            }
        }

        return mask & performanceCascadeMask;
    }

    /// <summary>Whether anything at <paramref name="at"/> can appear in any cascade's map.</summary>
    /// <remarks>
    /// Outside all three, a caster rasterises into nothing and the work has nowhere to go — which was worth
    /// 32 ms when the single box's version of this was added.
    /// <para>
    /// <b>This said "inside one of them it is drawn into all three, which is the redraw this does not yet
    /// fix", and that has not been true since the fitted boxes landed.</b> §144: PartitionCasters puts an
    /// instance only into the cascades whose box contains it, and the per-cascade instance buffers are what
    /// make that sound — see the note there. The stale sentence was load-bearing in the wrong direction: I
    /// had it recorded as an unpaid cost and would have gone looking for it again.
    /// </para>
    /// </remarks>
    private bool CastsIntoAnyCascade(Vector2 at) => CascadeMaskAt(at) != 0;

    private void PartitionCasters(
        ReadOnlySpan<InstanceData> source,
        IReadOnlyList<List<InstanceData>> cascaded)
    {
        foreach (var instances in cascaded) instances.Clear();
        foreach (ref readonly var instance in source)
        {
            var mask = CascadeMaskAt(new Vector2(instance.Model.M41, instance.Model.M43));
            for (var c = 0; c < ShadowCascades.Count; c++)
            {
                if ((mask & (1 << c)) != 0) cascaded[c].Add(instance);
            }
        }
    }

    /// <summary>How many texels a side cascade <paramref name="cascade"/>'s map has.</summary>
    /// <remarks>
    /// <b>Full resolution for the first two and half for the last</b>, which is what Sponza settled on and not
    /// what I reached for first. Halving outward is the tidy answer and it is wrong in the middle: sharpness is
    /// a box's width over its texel count, and the middle cascade is already three times the near one's width,
    /// so halving its map puts it six times coarser rather than three. The near cascade is the one that can
    /// afford to be over-sharp, because its box is small; the middle distance is most of what a player looks at
    /// in this game and gets kept.
    /// <para>
    /// What it costs: 2048² + 2048² + 1024² of depth, about 36 MB. What the alternative costs is a visible
    /// coarsening at the first split, which is a line across the ground that moves with the camera.
    /// </para>
    /// </remarks>
    private static int CascadeMapSize(int cascade) => cascade switch
    {
        0 => ShadowMapSize,
        1 => ShadowMapSize,
        2 => ShadowMapSize / 2,
        _ => throw new ArgumentOutOfRangeException(nameof(cascade), cascade, "There are three cascades."),
    };

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        var renderStart = Stopwatch.GetTimestamp();
        frameCount++;
        // Fitted every frame whether or not the cascades are rendered yet, because the gizmos are the point
        // of this stage: the boxes have to be watchable before three passes are wired to them.
        FitCascades();
        var viewProjection = camera.GetViewProjection(aspect);
        // Cascade 0 rides in the slot the single box used to occupy, so uSunShadowVP keeps its offset and
        // its meaning — the map covering whatever is nearest — and only the other two are new.
        var sunViewProjection = cascadeViewProjection[0];
        var cameraPosition = new Vector4(camera.Transform.Position, 1f);
        var sun = new Vector4(SunDirection, 0f);
        // Aerial perspective sized to the camera rather than to the map: what it is for is
        // separating the near ground from the far ground, and how far away the far ground is
        // depends on how far back the camera is standing.
        // <b>How much air there is tonight.</b> The season, the night and the morning, multiplied — see
        // Atmosphere.Haze, where the foggiest hour of the year comes out as a winter dawn without anybody
        // writing that down. Both the strength and where it starts: a mist that only deepens in the
        // distance is a mist that begins at the same place a clear day does, and half of what a foggy
        // morning does is close the middle distance in.
        var mist = look.SunFollowsTheYear ? sky.Haze : 1f;
        var fog = new Vector4(
            // <b>Against the detail radius, not the camera's standoff.</b> Haze exists here to hide the
            // edge where the world stops being drawn, and it cannot do that if it is measured against
            // something else — tied to the zoom it started four hundred metres past a cull at two hundred.
            // The square root, because the strength saturates and the start does not: past about a quarter
            // more air than a clear day the far distance is already fully hazed, so everything a thicker
            // morning has left to say it says by closing the middle distance — and dividing the start by
            // the whole of a threefold mist puts the wall thirty metres from the camera.
            DetailRadius * look.FogStartShare / MathF.Max(1f, MathF.Sqrt(mist)),
            DetailRadius,
            MathF.Min(1f, look.FogStrength * mist),
            0f);
        // What the shaders need about the shadow map's geometry — a texel as a fraction of the map, and
        // how many metres the map covers — plus the two biases, which are a look and not geometry.
        // <b>The fitted box, not the estimate.</b> The shaders scale their bias by how many metres a texel
        // covers, so handing them a different width from the one the matrix was built with makes every bias
        // wrong by that ratio — quietly, as acne or peter-panning rather than as anything that looks like a
        // mismatch.
        var shadow = new Vector4(
            1f / CascadeMapSize(0),
            cascadeSideMetres[0],
            look.ShadowPenumbraTexels,
            look.ShadowNormalOffsetTexels);
        // <b>Each cascade's own geometry, because the bias is measured in texels and a texel is not one size
        // any more.</b> Three boxes of different widths on maps of different resolutions means three different
        // world sizes for a texel — and handing the far cascade the near one's figure is the same mistake as
        // handing the shaders an estimate of a fitted box, in a fourth costume.
        var cascadeSides = new Vector4(
            cascadeSideMetres[0],
            cascadeSideMetres[1],
            cascadeSideMetres[2],
            cascades.ShowSelection ? 1f : 0f);
        var cascadeTexels = new Vector4(
            1f / CascadeMapSize(0), 1f / CascadeMapSize(1), 1f / CascadeMapSize(2), 0f);
        // <b>Which cascade a fragment belongs to is decided by depth, not by which box happens to contain
        // it.</b> Containment was the first answer here and it degenerates: every box is a bounding sphere of
        // its slice plus a margin, so the middle cascade's box is wide enough to hold the whole visible ground,
        // wins every test, and the outer cascade is never sampled at all. Painted by cascade that showed as two
        // colours where there should be three, and a boundary that curved with the box rather than running
        // across the view. Depth first, containment only to fall outward when the chosen box misses — which is
        // what SponzaLoop's shader does, arrived at the long way round.
        var cascadeSplits = new Vector4(cascadeEdges[1], cascadeEdges[2], cascadeEdges[3], 0f);
        // The axis those distances are measured along. Without it the shader can only measure radially from the
        // eye, which is a different quantity by up to a fifth at the corners of the screen.
        var cameraAhead = new Vector4(camera.Transform.Forward, 0f);
        var following = look.SunFollowsTheYear;
        var light = new Vector4(
            (following ? sky.SunIntensity : look.SunIntensity) * look.SunScale,
            (following ? sky.AmbientScale : look.Ambient) * look.AmbientScale,
            look.TerminatorWrap,
            // How chromatic green is allowed to be under this sun. One when the light is pinned, because
            // pinning the sun is for judging a material and a hidden chroma pull would be lying about it.
            following ? sky.Grade.FoliageChroma : 1f);
        var sunTint = new Vector4(following ? sky.SunColor : new Vector3(1.00f, 0.94f, 0.80f), 0f);
        var skyAmbient = new Vector4(following ? sky.SkyAmbient : new Vector3(0.36f, 0.44f, 0.55f), 0f);
        var groundAmbient = new Vector4(
            following ? sky.GroundAmbient : new Vector3(0.24f, 0.20f, 0.15f), 0f);
        var hazeAway = new Vector4(following ? sky.HazeAway : new Vector3(0.60f, 0.70f, 0.84f), 0f);
        var hazeToward = new Vector4(following ? sky.HazeToward : new Vector3(0.92f, 0.82f, 0.66f), 0f);
        // z carries the map's own size so the shader can turn a world position into a wear lookup: the map
        // is centred on the origin, so uv is worldPos.xz / extent + 0.5 and needs no second uniform.
        var haze = new Vector4(
            look.HazeDesaturation, look.HazeSunGlow, simulation.ExtentMeters, look.WearStrength);
        // <b>The grade is the season's, and the sliders are multipliers over it.</b> One exposure and one
        // saturation for the whole year meant the palette could change every hue in the scene and never
        // change how the frame was shot — and a winter photograph is recognisable as much by being flat and
        // drained as by being blue. See SkyGrade.
        var grade = new Vector4(
            look.Exposure * (following ? sky.Grade.Exposure : 1f),
            (float)look.Tonemap,
            look.Saturation * (following ? sky.Grade.Saturation : 1f),
            look.Contrast * (following ? sky.Grade.Contrast : 1f));
        Matrix4x4.Invert(viewProjection, out var inverseViewProjection);

        MemoryMarshal.Write(worldPush.AsSpan(0, 64), in viewProjection);
        MemoryMarshal.Write(worldPush.AsSpan(64, 16), in cameraPosition);
        MemoryMarshal.Write(worldPush.AsSpan(80, 16), in sun);
        MemoryMarshal.Write(worldPush.AsSpan(96, 64), in sunViewProjection);
        sentFog = fog;
        MemoryMarshal.Write(worldPush.AsSpan(160, 16), in fog);
        MemoryMarshal.Write(worldPush.AsSpan(176, 16), in shadow);
        MemoryMarshal.Write(worldPush.AsSpan(192, 16), in light);
        MemoryMarshal.Write(worldPush.AsSpan(208, 16), in haze);
        MemoryMarshal.Write(worldPush.AsSpan(224, 16), in sunTint);
        MemoryMarshal.Write(worldPush.AsSpan(240, 16), in skyAmbient);
        MemoryMarshal.Write(worldPush.AsSpan(256, 16), in groundAmbient);
        MemoryMarshal.Write(worldPush.AsSpan(272, 16), in hazeAway);
        MemoryMarshal.Write(worldPush.AsSpan(288, 16), in hazeToward);
        // <b>Wind on the same clock as the sun, which is the simulated one.</b> Wall time would be a third
        // opinion about when it is: at three times compression the people would be hurrying about under
        // trees stirring at a leisurely real-world rate, and the scene would come apart at exactly the
        // moment the player asked it to go faster.
        var wind = new Vector4(
            look.WindSway,
            (float)(simulation.TickNumber / 30.0),
            look.WindGustRate,
            look.WindBearingDegrees * MathF.PI / 180f);
        MemoryMarshal.Write(worldPush.AsSpan(304, 16), in wind);
        // <b>The same four numbers into every caster push.</b> A tree that leans in the scene and stands
        // straight in the shadow map has a shadow detached from its trunk, and one that leans on a different
        // clock has a shadow that swims. Written here rather than in FitCascades because the wind is derived
        // further down the frame than the matrices are.
        for (var c = 0; c < ShadowCascades.Count; c++)
        {
            MemoryMarshal.Write(cascadePush[c].AsSpan(64, 16), in wind);
        }
        // <b>What the settlement lights of itself, and how far into the night it is.</b> One ramp decides
        // both the windows and their pools, and it is the sun's height rather than the clock — so the
        // village comes up over the same twilight in which the palette goes blue, and neither can be seen
        // to switch.
        var nightness = following ? sky.Nightness : 0f;
        hearthLightCount = nightness < 0.02f
            ? 0
            : hearths.CollectLights(
                simulation,
                simulation.Date,
                sky.HourOfDay,
                (float)(simulation.TickNumber / 30.0),
                cameraFocus,
                DetailRadius,
                look.HearthSpill,
                hearthLights);
        var hearth = new Vector4(
            nightness, look.HearthReach, look.HearthSpark, hearthLightCount);
        MemoryMarshal.Write(worldPush.AsSpan(320, 16), in hearth);
        MemoryMarshal.Write(worldPush.AsSpan(CascadeBlockOffset, 64), in cascadeViewProjection[1]);
        MemoryMarshal.Write(worldPush.AsSpan(CascadeBlockOffset + 64, 64), in cascadeViewProjection[2]);
        MemoryMarshal.Write(worldPush.AsSpan(CascadeBlockOffset + 128, 16), in cascadeSides);
        MemoryMarshal.Write(worldPush.AsSpan(CascadeBlockOffset + 144, 16), in cascadeTexels);
        MemoryMarshal.Write(worldPush.AsSpan(CascadeBlockOffset + 160, 16), in cascadeSplits);
        MemoryMarshal.Write(worldPush.AsSpan(CascadeBlockOffset + 176, 16), in cameraAhead);
        // How many bones one body owns, which is how world_skinned.vert finds a body's slice of the shared
        // palette. Zero when nothing is rigged, and read by nothing else.
        var skinStride = new Vector4(bodies?.BonesPerBody ?? 0, 0f, 0f, 0f);
        MemoryMarshal.Write(worldPush.AsSpan(SkinStrideOffset, 16), in skinStride);
        // <b>One over the grid's span, and the shader adds the map's extent.</b> Two lengths, and they are not
        // interchangeable: scouted.Cells is a ceiling plus one, so the grid spans more ground than the map does.
        // The divisor is the span; the offset that centres it is the extent. I wrote the warning about this and
        // then used the span for both, which slides the veil half a cell — invisible as an offset, and visible
        // only as fog disagreeing with the cell overlay by a row at the edges. The derivation is in world.frag,
        // and it is written out there because the wear lookup beside it legitimately uses the extent twice.
        var scoutedDials = new Vector4(
            1f / MathF.Max(1f, scouted.SpanMetres),
            fogSettings.MemoryDensity,
            fogSettings.UnknownDensity,
            fogSettings.MemoryDrain);
        MemoryMarshal.Write(worldPush.AsSpan(CascadeBlockOffset + 192, 16), in scoutedDials);
        // <b>One over the billow size, because the shader multiplies.</b> A wavelength divides and a
        // frequency multiplies, and handing the shader metres to divide by would be one more place for the
        // fog's two length scales to be confused for each other.
        var veil = new Vector4(
            1f / MathF.Max(1f, fogSettings.CloudMetres),
            fogSettings.Wispiness,
            fogSettings.CloudDriftMetresPerSecond,
            fogSettings.CloudBrightness);
        MemoryMarshal.Write(worldPush.AsSpan(CascadeBlockOffset + 208, 16), in veil);
        // <b>The stretch goes down as a multiplier on the along-wind axis, so smaller is longer.</b> Named for
        // what it does to the shape rather than for what it does to the number, which is why the slider reads
        // "stretch" and the value shrinks.
        var veilAir = new Vector4(
            fogSettings.Scatter,
            fogSettings.SunGlow,
            MathF.Max(0.05f, fogSettings.CloudStretch),
            fogSettings.GustRoll);
        MemoryMarshal.Write(worldPush.AsSpan(CascadeBlockOffset + 224, 16), in veilAir);
        // The deep bank's own four, because a thick layer is not a thin one turned up: it is paler, less
        // directional, more solid, and it has to keep off ground the player already knows.
        var veilDeep = new Vector4(
            fogSettings.DeepBrightness,
            fogSettings.DeepScatterShare,
            MathF.Max(0.05f, fogSettings.DeepSolidity),
            MathF.Max(1f, fogSettings.DeepEdgeFalloff));
        MemoryMarshal.Write(worldPush.AsSpan(CascadeBlockOffset + 240, 16), in veilDeep);
        // <b>The water's four look numbers, on dials, because I had guessed them three times.</b> §148.
        // Reflection, glint, how opaque deep water gets, and how far the shore film reaches were constants
        // chosen without being able to see the result — and two of the three rounds of "the water looks
        // worse" traced to one of them. A look number that can only be changed by a rebuild is a look number
        // set by whoever is not looking.
        var water = new Vector4(
            look.WaterReflection, look.WaterGlint, look.WaterOpacity, look.WaterShoreMetres);
        MemoryMarshal.Write(worldPush.AsSpan(CascadeBlockOffset + 256, 16), in water);
        for (var i = 0; i < Hearths.MaximumLights; i++)
        {
            // The tail is zeroed rather than left stale: the count bounds the loop, but a light left in the
            // buffer from a frame when there were more of them is a light that comes back when the count
            // rises again, in the wrong place.
            var fire = i < hearthLightCount ? hearthLights[i] : Vector4.Zero;
            MemoryMarshal.Write(worldPush.AsSpan(336 + i * 16, 16), in fire);
        }
        // Smoke takes the light already resolved into one colour each, because it has no shadow map, no
        // material class and no wear to look up — it is a thin thing that carries the ambient and a little
        // of the beam, and packing the palette down to two vec4s keeps its whole layout at 128 bytes.
        var sunLight = new Vector4(new Vector3(sunTint.X, sunTint.Y, sunTint.Z) * light.X, 0f);
        var skyLight = new Vector4(new Vector3(skyAmbient.X, skyAmbient.Y, skyAmbient.Z) * light.Y, 0f);
        // Faded over the last third of the visible ground, so the far field is not stippled.
        var contactReach = Sees * ContactShare;
        var contactFade = new Vector4(contactReach * 0.62f, contactReach, 0f, 0f);
        MemoryMarshal.Write(contactPush.AsSpan(0, 64), in viewProjection);
        MemoryMarshal.Write(contactPush.AsSpan(64, 16), in contactFade);
        MemoryMarshal.Write(contactPush.AsSpan(80, 16), in cameraPosition);
        MemoryMarshal.Write(smokePush.AsSpan(0, 64), in viewProjection);
        MemoryMarshal.Write(smokePush.AsSpan(64, 16), in cameraPosition);
        MemoryMarshal.Write(smokePush.AsSpan(80, 16), in sun);
        MemoryMarshal.Write(smokePush.AsSpan(96, 16), in sunLight);
        MemoryMarshal.Write(smokePush.AsSpan(112, 16), in skyLight);
        MemoryMarshal.Write(smokePush.AsSpan(128, 16), in fog);
        // <b>The extent rides in the spare fourth channel.</b> Smoke's uHaze means something different from the
        // world shader's — a colour rather than four scalars — so the one number the shared veil function needs
        // and this block did not have goes where there was room, rather than growing a vec4 to hold a float.
        var smokeHaze = new Vector4(hazeAway.X, hazeAway.Y, hazeAway.Z, simulation.ExtentMeters);
        MemoryMarshal.Write(smokePush.AsSpan(144, 16), in smokeHaze);
        MemoryMarshal.Write(smokePush.AsSpan(160, 16), in scoutedDials);
        MemoryMarshal.Write(smokePush.AsSpan(176, 16), in veil);
        MemoryMarshal.Write(smokePush.AsSpan(192, 16), in veilAir);
        MemoryMarshal.Write(smokePush.AsSpan(208, 16), in veilDeep);
        MemoryMarshal.Write(smokePush.AsSpan(224, 16), in wind);

        // <b>The plumes, which are dressing and therefore derived rather than stepped.</b> Advance only
        // decides whether a chimney's turn has come round; where a puff has got to is a function of its
        // age, worked out when it is drawn. See Rendering/Hearths.cs.
        hearths.Advance(
            simulation,
            art,
            simulation.Date,
            sky.HourOfDay,
            (float)(simulation.TickNumber / 30.0),
            cameraFocus,
            DetailRadius,
            look.SmokeDensity);
        smokeBatch.Begin(smokePush);
        hearths.Emit(
            smokeBatch,
            (float)(simulation.TickNumber / 30.0),
            look.WindBearingDegrees * MathF.PI / 180f,
            look.WindGustRate,
            look.SmokeDrift);
        MemoryMarshal.Write(skyPush.AsSpan(0, 64), in inverseViewProjection);
        MemoryMarshal.Write(skyPush.AsSpan(64, 16), in cameraPosition);
        MemoryMarshal.Write(skyPush.AsSpan(80, 16), in sun);
        var zenith = new Vector4(following ? sky.SkyZenith : new Vector3(0.16f, 0.38f, 0.80f), 0f);
        var horizon = new Vector4(following ? sky.SkyHorizon : new Vector3(0.64f, 0.78f, 0.92f), 0f);
        MemoryMarshal.Write(skyPush.AsSpan(96, 16), in zenith);
        MemoryMarshal.Write(skyPush.AsSpan(112, 16), in horizon);
        MemoryMarshal.Write(gradePush.AsSpan(0, 16), in grade);
        var night = new Vector4(look.ScotopicShift, 0f, 0f, 0f);
        MemoryMarshal.Write(gradePush.AsSpan(16, 16), in night);

        // <b>Exactly the shadow box, because that is the honest answer and it was two numbers before.</b>
        // A tree matters if it can be seen or if its shadow can, and the sun's box is already sized to
        // cover both — the visible ground plus a margin for casters standing outside it. So there is nothing
        // left for an independent draw distance to decide: beyond the box a tree cannot cast into view, and
        // inside it a tree might.
        //
        // It was max(150 m, box × 0.75), which is two numbers pretending to agree. At the default zoom that
        // drew trees to 150 m against 64 m of visible ground — <b>2.3× as far as anybody can see</b>, and
        // more than twice as far as the box that decides whether they cast anything. Pulled in it is now
        // 73 m, which is the same picture for a quarter of the trees.
        // <b>As far as the ground is drawn, not as far as the shadow box reaches.</b> The tree radius was the
        // detail radius, which is capped by a look dial because it also sizes the sun's box — and once the
        // ground culling was fixed to follow the frustum, the ground began reaching past that cap. So trees
        // stopped in an arc *inside* the visible field: a hard curved edge with grass beyond it, which is
        // exactly the artefact this file's own LOD note calls the one thing an LOD scheme must not have.
        //
        // Trees past the shadow box simply do not cast, which is what already happened to everything beyond the
        // detail radius. A missing shadow at 150 m is not noticeable; a wall where the forest ends is.
        // Twice what can be seen, so nothing in view is ever outside it and the loop still refuses the far
        // half of a big map. Not a decision about visibility — see the note at its use.
        // <b>The far plane, which is to say: no distance bound at all.</b> Three attempts to size a disc around
        // cameraFocus so that it covered the view all failed the same way, and the last one failed at the
        // corners — which is the tell, because the corners are the furthest points from the centre of a disc and
        // the nearest thing to a proof that <em>a disc cannot match a trapezoid over undulating ground</em>. The
        // arithmetic said Sees reaches the corner and the relief term covers the drop; the screen said otherwise,
        // and the screen is right. A radius has one number and the view has four edges and a heightfield.
        //
        // So the frustum decides, alone, and this is only the sanity cap that keeps the loop from testing the
        // whole map: nothing past the far plane can be in any frustum. Affordable because it was measured —
        // drawing every tree on the map, thirty-four thousand of them, costs five to ten frames a second, so
        // the bound was never buying much and was costing correctness the whole time.
        var treeDrawRadius = camera.FarPlane;
        // Whether a thing casts is now CastsIntoAnyCascade, which asks the boxes rather than a radius round
        // the focus. What is still one number is the redraw: a caster inside any box is drawn into all three
        // passes, so passes/sun-cascade-N/triangles prints the same figure three times — and measured, that is
        // 59k triangles a cascade against a 488k frame, so it is not what the frame is spent on. The partition
        // that would fix it — cast into cascade c only outside cascade c-1's box, since a receiver reads the
        // first box that contains it — needs an InstanceBuffer per cascade as well as a list per cascade, for
        // the reason given where the passes are recorded. Not worth it at 59k.
        treeCullBoundSquared = treeDrawRadius * treeDrawRadius;

        // <b>Render-side phase timings, because the simulation's own breakdown cannot see any of this.</b>
        // A frame went from sixty-odd to five and the sim tick had not moved, which says the cost is in
        // building the frame rather than in stepping the world — and this session has been wrong three times
        // guessing which part of a frame is expensive.
        UpdateFrustum(viewProjection);
        // <b>Logical pixels, not the framebuffer's.</b> VisibleGroundRadius derives from host.LogicalSize, so
        // a metres-per-pixel figure built from frame.Height mixes two units — and on a retina display the two
        // differ by a factor of two, which silently doubled every screen-space size computed from it. The rule
        // that culls transition bands under three pixels was therefore measuring six.
        windowSize = host.LogicalSize;
        viewportPixels = windowSize.Height;
        woodlandAsked = -WoodlandCover.Asked;
        RebuildCanopyDensity();
        // <b>"terrain" measured the terrain <em>and every node on it</em>, and that cost me an hour.</b>
        // BuildTerrainInstances calls BuildNodeInstances, so a figure labelled terrain was mostly tree drawing —
        // which sent me looking for a chunk rebuild that was not happening while thirty-five milliseconds of
        // woodland queries sat in plain sight under the wrong name. An instrument that lies is worse than no
        // instrument, because it is believed.
        //
        // Split, and the ground phase is now the ground. The label is the fix, not the timing.
        var buildClock = Stopwatch.StartNew();
        BuildTerrainInstances();
        var terrainMs = buildClock.Elapsed.TotalMilliseconds - nodeMilliseconds;
        buildClock.Restart();
        // <b>Before the bodies are added, which is not where the other batches are begun.</b> unitBatch and
        // friends are begun further down and fed from lists this fills, so putting this beside them cleared
        // every body the moment after it was added — thirteen villagers added, thirteen thrown away, and an
        // empty draw. Invisible people, and nothing in the matrices to suggest why.
        bodies?.Begin();
        WatchForHurt(frameSeconds);
        if (bodyLog)
        {
            ReportActionChanges();
            // <b>The same census the gate's hands-off leg runs, pointed at the game as played.</b> §183.
            // The gate's version says nothing is stranded there — 171 spells, worst 5.4 s, every spell over
            // two seconds followed by work, zero still stalled at the end — while red cylinders were
            // reported from the chair four times. So either the fault is not a stall or the gate's village
            // is not the game's, and there is no way to tell those apart without the same numbers from the
            // run where the fault appears.
            //
            // Sampled per frame rather than per tick, which caps the resolution of a spell's length at one
            // frame. Against a threshold of a third of a second that is ten-odd samples per spell, and the
            // shortest thing this needs to resolve is a spell, not a tick.
            stalls.Sample(simulation, frameSeconds);
            bodyLogDue -= frameSeconds;
            if (bodyLogDue <= 0f)
            {
                bodyLogDue = 1f;
                ReportStuckBodies();
                ReportSelectedBodies();
            }

            stallCensusDue -= frameSeconds;
            if (stallCensusDue <= 0f)
            {
                stallCensusDue = StallCensusInterval;
                Console.WriteLine(stalls.Describe("as played, so far"));
                Console.WriteLine(stalls.DescribeKeptFromWork());
            }
        }

        BuildAgentInstances((float)time.Total);
        var agentMs = buildClock.Elapsed.TotalMilliseconds;
        buildClock.Restart();
        DrawScatter();
        var scatterMs = buildClock.Elapsed.TotalMilliseconds;
        buildClock.Restart();
        DrawColliderOverlay();
        DrawFogOverlay();
        var overlayMs = buildClock.Elapsed.TotalMilliseconds;
        buildClock.Restart();

        var props = CollectionsMarshal.AsSpan(propInstances);
        var units = CollectionsMarshal.AsSpan(unitInstances);
        var canopies = CollectionsMarshal.AsSpan(canopyInstances);
        PartitionCasters(props, propCasterInstances);
        PartitionCasters(units, unitCasterInstances);
        PartitionCasters(canopies, canopyCasterInstances);
        propBatch.Begin(worldPush);
        propBatch.SetInstances(props);
        unitBatch.Begin(worldPush);
        unitBatch.SetInstances(units);
        canopyBatch.Begin(worldPush);
        canopyBatch.SetInstances(canopies);
        for (var c = 0; c < ShadowCascades.Count; c++)
        {
            propCasters[c].Begin(cascadePush[c]);
            propCasters[c].SetInstances(CollectionsMarshal.AsSpan(propCasterInstances[c]));
            unitCasters[c].Begin(cascadePush[c]);
            unitCasters[c].SetInstances(CollectionsMarshal.AsSpan(unitCasterInstances[c]));
            canopyCasters[c].Begin(cascadePush[c]);
            canopyCasters[c].SetInstances(CollectionsMarshal.AsSpan(canopyCasterInstances[c]));
        }
        art?.StageCascades(worldPush, cascadePush);
        // The frame's poses, once, after the last body was added. Then each cascade's own payload: the same
        // light matrix the leaning caster gets, with the bone stride where its wind would be.
        if (bodies is not null)
        {
            // The cascade payloads first, because staging hands them to the caster batches: the same light
            // matrix the leaning caster gets, with the bone stride where its wind would be.
            var stride = new Vector4(bodies.BonesPerBody, 0f, 0f, 0f);
            for (var c = 0; c < ShadowCascades.Count; c++)
            {
                cascadePush[c].AsSpan(0, 64).CopyTo(skinnedCascadePush[c]);
                MemoryMarshal.Write(skinnedCascadePush[c].AsSpan(64, 16), in stride);
            }

            bodies.Stage(worldPush, skinnedCascadePush);
        }
        var stageMs = buildClock.Elapsed.TotalMilliseconds;
        buildClock.Restart();

        // Shadow depth: only the solids. The ground is a receiver and not a caster — a large
        // near-flat mesh shadowing itself is all acne and no shadow — and the overlays are
        // annotations on top of the world rather than things in it.
        //
        // <b>A list and a GPU buffer per cascade.</b> InstanceBuffer writes one current-frame slot, so sharing
        // a buffer between different subsets makes every recorded draw see the last upload. Distinct storage
        // is the correctness condition that lets the fitted boxes stop the old threefold redraw.
        for (var c = 0; c < ShadowCascades.Count; c++)
        {
            var cascade = c;
            graph.Pass(cascadePasses[cascade], scope =>
            {
                propCasters[cascade].End(scope);
                unitCasters[cascade].End(scope);
                canopyCasters[cascade].End(scope);
                art?.DrawShadow(scope, cascade);
                // <b>A posed body has to cast the shadow of the pose it is in.</b> Casting the bind pose
                // would put a standing silhouette under a walking villager, which reads as a second body.
                bodies?.DrawShadow(scope, cascade);
            });
        }

        var smokeBinding = new[] { new ShaderTextureBinding("uScoutedMap", fogTexture, Slot: 0) };
        var shadowBinding = new[]
        {
            new ShaderTextureBinding(
                "uSunShadowMap", graph.GetDepthTexture(cascadeTargets[0]), Slot: 0),
            new ShaderTextureBinding("uWear", wearTexture, Slot: 1),
            new ShaderTextureBinding("uScoutedMap", fogTexture, Slot: 4),
            new ShaderTextureBinding(
                "uCascade1Map", graph.GetDepthTexture(cascadeTargets[1]), Slot: 2),
            new ShaderTextureBinding(
                "uCascade2Map", graph.GetDepthTexture(cascadeTargets[2]), Slot: 3),
        };
        graph.Pass(scenePassHandle, scope =>
        {
            fullscreen.Draw(scope, skyPipeline, Array.Empty<ShaderTextureBinding>(), skyPush);
            var groundClock = Stopwatch.StartNew();
            var groundDraws = 0;
            foreach (var chunk in drawnChunks)
            foreach (var layer in groundChunks[chunk])
            {
                // <b>A transition coat is a detail, and past the detail radius it is an invisible one.</b>
                // The coats are alpha-blended, so each one that covers screen pays fill rate whether or not
                // anybody can tell it is there — and at a standoff where the whole canvas is in frame the
                // render step is metres wide, which puts a crossfade band comfortably under a pixel.
                // Measured on the 1800 m lab canvas: 219 layers at 81 ms, 25 at 19 ms, and no visible
                // difference at that zoom because there is nothing there to see.
                if (layer.Blend && !blendedChunks.Contains(chunk)) continue;

                layer.Batch.End(scope, shadowBinding);
                groundDraws++;
            }

            // <b>Timed on its own, because the last attribution was arithmetic rather than measurement.</b>
            // Ground layers went from 8 to 96 between a flat map and a hilly one while `record` grew 4.4 ms,
            // and dividing one by the other gave 50 microseconds a draw — a figure far too large to believe and
            // arrived at by assuming every extra millisecond belonged to the ground. This says what the ground
            // actually costs.
            groundSubmitMs = groundClock.Elapsed.TotalMilliseconds;
            groundLayersDrawn = groundDraws;

            // After the ground and before anything standing on it: a contact shadow is a mark on the ground,
            // and the object that casts it draws over its own middle.
            contactBatch.SetInstances(CollectionsMarshal.AsSpan(contactInstances));
            contactBatch.End(scope);
            // After the contact shadows, so a selected thing's plate sits over its own occlusion rather than
            // being mottled by it.
            selectionDecalBatch.SetInstances(CollectionsMarshal.AsSpan(selectionInstances));
            selectionDecalBatch.End(scope);
            selectionPlateBatch.SetInstances(CollectionsMarshal.AsSpan(selectionPlateInstances));
            selectionPlateBatch.End(scope);

            propBatch.End(scope, shadowBinding);
            unitBatch.End(scope, shadowBinding);
            canopyBatch.End(scope, shadowBinding);
            art?.DrawScene(scope, shadowBinding);
            // The people, lit by world.frag exactly as everything above it is — same textures, same push.
            bodies?.DrawScene(scope, shadowBinding);
            // After every opaque thing and before the annotations: smoke blends over a finished frame, and
            // an overlay is a mark on the picture rather than something in the world for smoke to drift in
            // front of.
            smokeBatch.End(scope, smokeBinding);
            overlayBatch.End(scope, shadowBinding);
        });
        graph.Execute(commandList);

        var hdr = graph.GetColorTexture(hdrHandle);
        commandList.Pass(
            "present",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0f, 0f, 0f, 1f) },
                ClearDepth: true),
            pass =>
            {
                fullscreen.Draw(
                    pass,
                    presentPipeline,
                    new[] { new ShaderTextureBinding("uHdr", hdr, Slot: 0) },
                    gradePush);
                DrawSelectionMarquee(pass);
                hud?.Draw(
                    pass,
                    selectionPixel,
                    simulation,
                    selection.Selected,
                    pointerWorld,
                    pointerOnTerrain,
                    additiveSelection,
                    routeSource,
                    raiders?.Status,
                    look.SunFollowsTheYear
                        ? $"{(int)sky.HourOfDay:00}:{(int)(sky.HourOfDay % 1f * 60f):00} {sky.Description}"
                        : null,
                    colliderOverlay > 0 ? GeometryLine() : null,
                    mapLab ? LabStatus() : MapStatus(),
                    crews.Summary(simulation.Agents),
                    hoveredNode,
                    selectedNode,
                    frame.Width,
                    frame.Height);
            });

        if (timingDebug && frameCount % 60 == 0)
        {
            Console.WriteLine(
                $"  window: logical {host.LogicalSize.Width}x{host.LogicalSize.Height}, " +
                $"frame {windowSize.Width}x{windowSize.Height}, " +
                $"scale {windowSize.Width / MathF.Max(1f, host.LogicalSize.Width):F2}\n" +
                $"  ground: {drawnChunks.Count} chunks drawn, {blendedChunks.Count} blended, " +
                $"furthest {furthestChunk:F0} m against a flat-horizon reach of {GroundDrawRadius:F0} m " +
                $"over {labFloorSpan.Span:F0} m of relief, zoom {cameraDistance:F0} m");
        }

        buildPhases = (terrainMs, agentMs, scatterMs, overlayMs, stageMs, buildClock.Elapsed.TotalMilliseconds);
        stagedLoad = art?.StagedLoad() ?? (0, 0L, 0L);
        if (art is not null)
        {
            art.StagedCasterLoad(stagedCasterInstances, stagedCasterTriangles);
        }
        else
        {
            Array.Clear(stagedCasterInstances);
            Array.Clear(stagedCasterTriangles);
        }

        // Sampled here rather than in OnUpdate because this is the point at which both halves of the frame
        // are known: what the build spent, and what it handed the passes. Past graph.Execute and the present
        // pass, so the render figure covers recording as well as building.
        renderMilliseconds = (Stopwatch.GetTimestamp() - renderStart) * 1000.0 / Stopwatch.Frequency;
        // <b>Both deltas, because the chair and the fixture disagreed by six times and one of them was
        // reading the wrong clock.</b> The recorder samples OnUpdate's delta; the engine's F1 perf HUD counts
        // OnRender's. If the host ever runs the two callbacks at different cadences those are different
        // numbers, and a frame budget argued from the wrong one is argued from nothing. Reported side by side
        // so the question is answered by the log rather than by reading Silk's loop.
        var hostFrame = vk.LastCpuFrameTiming;
        performance?.Observe(new PerformanceRun.Sample(
            rawFrameMilliseconds,
            time.Delta * 1000.0,
            hostFrame.WaitMs,
            hostFrame.EncodeMs,
            hostFrame.SubmitPresentMs,
            updateMilliseconds,
            fogMilliseconds,
            renderMilliseconds,
            buildPhases.Terrain,
            nodeMilliseconds,
            buildPhases.Agents,
            buildPhases.Scatter,
            buildPhases.Overlay,
            buildPhases.Stage,
            buildPhases.Record,
            stagedLoad.Instances,
            stagedLoad.Triangles,
            stagedLoad.Casters,
            stagedCasterInstances[0],
            stagedCasterInstances[1],
            stagedCasterInstances[2],
            stagedCasterTriangles[0],
            stagedCasterTriangles[1],
            stagedCasterTriangles[2],
            treesDrawn,
            drawnChunks.Count,
            cameraDistance));
        // The GPU baseline is taken on the first steady frame, so the pass means describe the same window the
        // frame percentiles do rather than the warm-up's first presentation and terrain meshing.
        if (performance is { SteadyFrames: 1 } && gpuBaseline is null) CaptureGpuBaseline();

        if (exitAfterFrames > 0 && frameCount >= exitAfterFrames)
        {
            ReportPerformanceRun();
            host.RequestClose();
        }
    }


    private void BuildTerrainInstances()
    {
        if (renderedTerrain != simulation.Terrain || renderedTerrainRevision != simulation.Terrain.Revision)
        {
            RebuildTerrainSurfaceLayers();
        }

        contactBatch.Begin(contactPush);
        selectionDecalBatch.Begin(contactPush);
        selectionPlateBatch.Begin(contactPush);
        EnsureGroundChunks();
        // <b>Built everywhere, drawn where it can be seen.</b> Meshing the whole map is cheap — it happens
        // once per terrain change and the chunks are kept — but <em>drawing</em> it all is not: measured,
        // the ground was 204,800 of the scene pass's 276,315 triangles, three quarters of the frame's
        // geometry, most of it behind the camera or beyond the fog. The distinction is the one this file
        // keeps having to relearn: what a thing costs to prepare and what it costs to submit are different
        // budgets.
        drawnChunks.Clear();
        blendedChunks.Clear();
        var chunkMetres = GroundChunkCells * simulation.Navigation.Transform.CellSize;

        // <b>A transition band is a detail, so what decides it is how wide it is on screen.</b> Culling blends
        // against the detail radius alone works when the map is larger than the view and fails when it is not:
        // at 600 m the whole map sits inside the detail radius, so every blend coat drew, each covering a large
        // share of the screen with alpha blending on.
        //
        // A band is about one and a half render cells across. Under three pixels there is nothing in it to see,
        // and the fade it provides is invisible. At a gameplay standoff it is eight pixels and draws; looking at
        // a whole map it is under two and does not.
        //
        // <b>Hoisted out of the chunk loop, where it cost eleven milliseconds a frame.</b>
        // <see cref="VisibleGroundRadius"/> asks the host for the window size, and asking twenty-five times a
        // frame took the terrain build phase from 1.4 ms to 13.1. The figure is one number about the camera and
        // has no business being recomputed per chunk — the same mistake as any other loop-invariant, made
        // louder by the invariant being a call across an interop boundary.
        // Frame constants, computed once. Cheap now that the window size is cached, and still wrong to
        // recompute per chunk — a number about the camera does not vary between chunks.
        furthestChunk = 0f;
        outcropsSeen = 0;
        outcropsDrawn = 0;
        hoveredNode = PickNode();
        hoveredAgent = PickAgent();
        if (selectedNode.IsValid && !simulation.Nodes.Contains(selectedNode)) selectedNode = NodeId.None;
        var visible = VisibleGroundRadius;
        var groundReach = GroundDrawRadius;
        // How much further a hill can enter the view than flat ground at the same bearing. The bottom edge of
        // the frustum descends at (pitch - halfFov), so ground standing `span` metres proud of the focus plane
        // meets it `span / tan(pitch - halfFov)` further out.
        // The same relief-corrected reach the trees use, so the two can no longer disagree about how far the
        // world goes — which is what they were doing.
        var outerReach = VisibleReach * GroundBoundShare;
        var detailReach = DetailRadius;
        var metresPerPixel = viewportPixels > 0f ? 2f * visible / viewportPixels : 0.001f;
        var bandsWorthDrawing =
            GroundRenderStep * simulation.Navigation.Transform.CellSize * 1.5f / metresPerPixel >= 3f;
        foreach (var (chunk, layers) in groundChunks)
        {
            var minimum = simulation.Navigation.Transform.Origin + new Vector2(chunk.X, chunk.Z) * chunkMetres;
            // Against the nearest corner, so a chunk the camera stands on the edge of is drawn.
            var nearest = Vector2.Clamp(cameraFocus, minimum, minimum + new Vector2(chunkMetres));
            var away = Vector2.DistanceSquared(nearest, cameraFocus);

            // <b>The frustum, with the chunk's own height, rather than a radius from a flat-earth horizon.</b>
            // GroundDrawRadius derives the far edge of the view by intersecting the bottom of the frustum with
            // a <em>plane</em> at the camera's focus height — eye height over cameraDistance, straight
            // trigonometry — which is exactly right on the flat ground it was written against and wrong the
            // moment the map has hills in it. High ground beyond that plane's horizon still projects into the
            // frame, and was being culled: measured at zoom 78 m on CentralHighGround, chunks inside the
            // frustum but cut by the radius went 0 on flat ground, <b>4 at 32 m of relief and 5 at 60 m</b>,
            // against 8, 7 and 6 actually drawn. Nearly half the visible ground, gone — reported from the
            // chair as weird clipping when looking around, which is what it is.
            //
            // The radius stays as the cheap outer bound because it still means something (nothing past it is
            // worth considering however tall it is) but it is widened by what relief can add: a hill of the
            // map's own height can enter the view from that much further away, along the same bottom edge.
            // Then the frustum test decides, on the chunk's real low and high.
            if (away > outerReach * outerReach) continue;
            var box = groundChunkHeights.TryGetValue(chunk, out var range) ? range : (0f, 1f);
            if (!InView(
                    minimum + new Vector2(chunkMetres * 0.5f),
                    box.Item1,
                    box.Item2 - box.Item1,
                    chunkMetres * 0.71f))
            {
                continue;
            }

            drawnChunks.Add(chunk);
            furthestChunk = MathF.Max(furthestChunk, MathF.Sqrt(away));
            var blended = away <= detailReach * detailReach && bandsWorthDrawing;
            if (blended) blendedChunks.Add(chunk);
            foreach (var layer in layers)
            {
                // <b>Staged and drawn have to be the same set.</b> Skipping only the draw leaves the batch
                // open, and the next frame's Begin throws — which is the primitive being right: a Begin
                // without an End is a staged instance list nothing ever submitted, and it should be loud.
                if (layer.Blend && !blended) continue;
                layer.Batch.Begin(worldPush);
                layer.Batch.Add(Matrix4x4.Identity, layer.Color);
            }
        }

        overlayBatch.Begin(worldPush);
        propInstances.Clear();
        unitInstances.Clear();
        canopyInstances.Clear();
        art?.Begin();
        // Before the nodes are built, because that is what draws the trees and therefore their skirts.
        undergrowthDrawn = 0;
        treesDrawn = 0;
        treeNodesAlive = 0;
        treesOffered = 0;
        treeTicks = 0L;
        treeSamples = 0;
        undergrowthTicks = 0L;
        treesOutOfView = 0;
        treesNearRejected = 0;
        treeTiers = (0, 0, 0, 0);
        habitationLights = 0;
        contactInstances.Clear();
        selectionInstances.Clear();
        selectionPlateInstances.Clear();
        BuildObstacleInstances();
        DrawWindow();
        DrawMapOverlay();
        var nodeClock = Stopwatch.StartNew();
        BuildNodeInstances();
        nodeMilliseconds = nodeClock.Elapsed.TotalMilliseconds;
        AdvanceTreeProfile();
        BuildNavigationOverlay();
        BuildPathDebug();
        BuildVelocityDebug();
    }

    /// <summary>
    /// Ground for maps too large to mesh cell by cell: one flat block per checker square,
    /// coloured by the surface under its centre.
    /// </summary>
    /// <remarks>
    /// Block size scales with the map so the pattern stays a scale reference rather than
    /// becoming either a blur or a single flat colour — about a hundred and twenty squares
    /// across at any extent, which is 10 m squares on a 1200 m map. That matters more than it
    /// sounds for a session about how movement feels: perceived speed on an untextured plane
    /// comes almost entirely from crossing edges, so the ground is what makes a 1.5 m/s body
    /// look like it is walking rather than sliding.
    /// </remarks>
    /// <summary>How far a cell may sit from its block's height before it is drawn itself.</summary>
    /// <remarks>
    /// Generous on purpose. Ground that rolls gently is described perfectly well by a flat
    /// plate every five metres, and demanding agreement to the centimetre means every block on
    /// a natural landscape counts as disputed — which is how the map became a few islands
    /// floating in the sky. What this is for is catching a step: a bank, a ridge, the lip of a
    /// basin, where a flat plate stops being an answer at all.
    /// </remarks>
    private const float BlockHeightTolerance = 0.35f;
    /// <summary>
    /// The parts of the map that are not plain grass at ground level, drawn as whole patches.
    /// </summary>
    /// <remarks>
    /// This used to be per-cell within forty metres of the camera, which put a visible seam
    /// across the map — a ridge stepped finely near the crowd and blocky beyond it. Any fixed
    /// radius does that and moving it only moves the seam. A patch of ground that is one surface
    /// at one height is exactly describable by a single box however large and however distant, so
    /// the terrain is merged into those patches once per edit and every one of them is drawn.
    /// Detail now exists where the ground has detail rather than where the camera happens to be.
    /// </remarks>
    /// <summary>How far from the camera the per-cell debug overlay is drawn, in metres.</summary>
    /// <remarks>
    /// The overlay is one instance per navigation cell, which is 3,600 on the tuned world and
    /// 5.76M on a 1200 m one. A window is not an optimisation here, it is the difference
    /// between a debug view and a hang — and since the overlay exists to be read, drawing it
    /// where nobody is looking was never worth anything.
    /// </remarks>
    private const float NavigationOverlayRadius = 26f;

    private void BuildNavigationOverlay()
    {
        if (navigationDebugMode == 0) return;
        var grid = simulation.Navigation;
        var cellScale = grid.Transform.CellSize * 0.82f;
        // Windowed on a map big enough that the whole-map overlay would be a hang rather than a view.
        var windowed = simulation.Navigation.Width * simulation.Navigation.Height > 21_000;
        var minimumX = 0;
        var minimumZ = 0;
        var maximumX = grid.Width - 1;
        var maximumZ = grid.Height - 1;
        if (windowed)
        {
            var cellSize = grid.Transform.CellSize;
            var low = (cameraFocus - new Vector2(NavigationOverlayRadius) - grid.Transform.Origin) / cellSize;
            var high = (cameraFocus + new Vector2(NavigationOverlayRadius) - grid.Transform.Origin) / cellSize;
            minimumX = Math.Clamp((int)MathF.Floor(low.X), 0, grid.Width - 1);
            minimumZ = Math.Clamp((int)MathF.Floor(low.Y), 0, grid.Height - 1);
            maximumX = Math.Clamp((int)MathF.Ceiling(high.X), 0, grid.Width - 1);
            maximumZ = Math.Clamp((int)MathF.Ceiling(high.Y), 0, grid.Height - 1);
        }

        for (var z = minimumZ; z <= maximumZ; z++)
        for (var x = minimumX; x <= maximumX; x++)
        {
            var cell = new GridCell(x, z);
            var center = grid.CellCenter(cell);
            var color = navigationDebugMode switch
            {
                2 => TerrainCostColor(simulation.Terrain.SampleSurface(center)),
                3 => SlopeColor(simulation.Terrain.SampleGrade(center)),
                4 => CongestionColor(simulation.Congestion.At(cell)),
                _ => grid.IsBlocked(cell)
                    ? NavBlockedColor
                    : grid.IsWalkable(cell, AgentDefaults.Radius)
                        ? NavWalkableColor
                        : NavClearanceColor,
            };
            var height = simulation.Terrain.SampleHeight(center);
            var model = Matrix4x4.CreateScale(cellScale, 0.018f, cellScale) *
                        Matrix4x4.CreateTranslation(center.X, height + 0.022f, center.Y);
            overlayBatch.Add(model, color);
        }
    }

    /// <summary>Whether a placement cell is part of some building's footprint.</summary>
    private bool StandsOnANode(Vector2 cellCentre)
    {
        foreach (ref readonly var node in simulation.Nodes.All)
        {
            if (!node.IsAlive || !NodeFootprint.Blocks(node.Kind)) continue;
            var half = node.HalfExtent + 0.01f;
            if (MathF.Abs(cellCentre.X - node.Position.X) <= half &&
                MathF.Abs(cellCentre.Y - node.Position.Y) <= half)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Draws the nodes, so a settlement is something you can see rather than infer.
    /// </summary>
    /// <remarks>
    /// Three silhouettes rather than one, because a greybox has nothing but silhouette to work with.
    /// <list type="bullet">
    /// <item><b>A store or a house is a building</b> — a body with a wider, darker roof slab over it.
    /// The overhang is the whole trick: it puts a hard horizontal shadow line across the wall below, so
    /// the thing reads as having a top and sides rather than being a coloured rectangle lying in the
    /// same plane as the ground it stands on.</item>
    /// <item><b>A field is ground</b> — a tilled plot with a crop standing on it, both a few
    /// centimetres tall. See <see cref="DrawField"/>.</item>
    /// <item><b>A heap is spillage</b> — low, wide, and the colour of what it is.</item>
    /// </list>
    /// <para>
    /// A store's height still reads how full it is and a house's how many live in it, which is the one
    /// number worth being legible from across the map without selecting anything.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Runs the tree-work ablation: a hundred frames at each level, then the attribution and out.
    /// </summary>
    /// <remarks>
    /// One run rather than three, because three runs is three different camera positions and three different
    /// tree counts — and comparing a phase across those is exactly the mistake made earlier tonight, when
    /// <c>nodes</c> from one run was held against <c>TREENODES</c> from another and called a contradiction. The
    /// levels have to be measured against the same frame to be subtractable.
    /// <para>
    /// The first twenty frames of each level are discarded. A level change moves what the loop touches, and the
    /// first frames after it pay for cache that the previous level left cold.
    /// </para>
    /// </remarks>
    private void AdvanceTreeProfile()
    {
        if (!treeProfile) return;

        // <b>Two harness designs, each with its own bias, and the second one was mine too.</b>
        //
        // A block of frames per level drifts: the simulation runs on between blocks and the camera is still
        // easing toward its zoom, so the last level measures a different scene from the first. That showed as a
        // negative cost for submission — the whole phase reading cheaper than the cull alone.
        //
        // Interleaving the levels frame by frame fixes the drift and introduces a worse bias. A level-0 frame
        // writes megabytes of instances and evicts the node array, so with the levels cycling, the cull is
        // always measured on a cold sweep and the fuller levels on a warm one. The differences come out
        // understated by however much that is worth, and nothing in the output says so.
        //
        // So: blocks again, with a long enough settle that the camera has arrived, and — the part that was
        // missing both times — <b>the scene reported per level, so the comparison can be checked rather than
        // assumed.</b> If the three counts agree, the differences mean what they say. If they do not, the run is
        // void and it says so itself instead of printing three numbers that look fine.
        const int settle = 90;
        const int measured = 120;
        var level = 2 - treeWork;
        var frames = ++treeWorkFrames[level];
        if (frames > settle)
        {
            treeWorkMilliseconds[level] += nodeMilliseconds;
            treeWorkOffered[level] = treesOffered;
            treeWorkDrawn[level] = treesDrawn;
        }

        if (frames < settle + measured) return;

        if (treeWork > 0)
        {
            treeWork--;
            return;
        }

        var cull = treeWorkMilliseconds[0] / measured;
        var compute = treeWorkMilliseconds[1] / measured;
        var whole = treeWorkMilliseconds[2] / measured;
        var spread = treeWorkOffered.Max() - treeWorkOffered.Min();
        Console.WriteLine(
            $"  tree work — offered {treeWorkOffered[0]:N0}/{treeWorkOffered[1]:N0}/{treeWorkOffered[2]:N0}, " +
            $"past the frustum {treeWorkDrawn[0]:N0}/{treeWorkDrawn[1]:N0}/{treeWorkDrawn[2]:N0}, " +
            $"{measured} frames a level");
        if (spread > treeWorkOffered.Max() / 100)
        {
            Console.WriteLine(
                $"    VOID — the scene moved by {spread:N0} trees between levels, so the differences below are " +
                "not differences in the same world. Freeze the camera and try again.");
        }

        Console.WriteLine(
            $"    iterate + height + frustum {cull:F1} ms · " +
            $"per-tree fields + placement +{compute - cull:F1} ms · " +
            $"submission +{whole - compute:F1} ms · whole phase {whole:F1} ms");
        host.RequestClose();
    }

    private void EnsureRenderNodeIndex()
    {
        var nodes = simulation.Nodes;
        var cellCount = scouted.Cells * scouted.Cells;
        if (renderNodeWorld == simulation && renderNodeSlots == nodes.Count &&
            renderNodeLive == nodes.LiveCount && renderNodeCells.Length == cellCount)
        {
            return;
        }

        renderNodeCells = new List<NodeId>[cellCount];
        for (var i = 0; i < renderNodeCells.Length; i++) renderNodeCells[i] = new List<NodeId>();
        indexedStandingTrees = 0;
        indexedOutcrops = 0;
        foreach (ref readonly var node in nodes.All)
        {
            if (!node.IsAlive) continue;
            renderNodeCells[scouted.Index(node.Position)].Add(node.Id);
            if (node.IsStanding) indexedStandingTrees++;
            if (node.Kind == NodeKind.Outcrop) indexedOutcrops++;
        }

        renderNodeWorld = simulation;
        renderNodeSlots = nodes.Count;
        renderNodeLive = nodes.LiveCount;
    }

    private void BuildNodeInstances()
    {
        DrawStumps();
        EnsureRenderNodeIndex();
        treeNodesAlive = indexedStandingTrees;
        outcropsSeen = indexedOutcrops;
        var nodesAllowedByFog = 0;
        for (var cell = 0; cell < renderNodeCells.Length; cell++)
        {
            // One fog decision per ten-metre cell instead of one per node. Every static thing in a cell reads
            // the same mask texel, so repeating the lookup for each of its trees cannot change the answer.
            if (!scouted.DrawsStaticCell(cell)) continue;
            foreach (var id in renderNodeCells[cell])
            {
                if (!simulation.Nodes.Contains(id)) continue;
                ref readonly var node = ref simulation.Nodes.Get(id);
                nodesAllowedByFog++;

            if (node.IsPile)
            {
                DrawPile(in node);
                continue;
            }

            // Picked out by colour, the way a body is. Decided before the per-kind branches so every kind can
            // use it, and passed down rather than drawn here, because what a building is <em>made of</em> is
            // the thing being overridden.
            // <b>No tint on anything the player did not build out of one material.</b> Repainting was too
            // strong and it had to be: world.frag reads a tint as `albedo = vTint.rgb; surface = vTint.a`, so
            // an override does not highlight a building, it <em>replaces</em> it — six materials flattened to
            // one flat colour, and the material class swapped along with them. That is why a hovered granary
            // came out looking like a painted block rather than a lit one.
            //
            // A villager survives being tinted because a villager is already one colour; a building is not.
            // So the plate carries both states, faint for hover and full for selected, and the model is left
            // to look like itself. A real highlight — mixing toward a colour while keeping the materials —
            // wants a term in the shader rather than a value smuggled through the albedo, and the tint
            // plumbing is gone rather than left switched off: a dead path that still works is the thing
            // somebody reaches for next time.
            if ((node.Id == selectedNode || node.Id == hoveredNode) && IsDrawnThisFrame(in node))
            {
                // <b>Sized from the thing, not from a per-kind fudge.</b> A building's and a field's extent is
                // the footprint the simulation enforces, so the mark is that plus a hair — which is also the
                // honest answer to the observation that a model can sit well inside its cell: the square is
                // the ground the thing <em>owns</em>, and owning it is what the marker is about.
                //
                // A tree or a rock has a nominal footprint far smaller than the thing you see, so those are
                // sized to what is drawn instead. Nothing here is a magic number per kind; each is the extent
                // that kind is actually described by.
                var square = node.Kind is NodeKind.Granary or NodeKind.ForwardDepot or NodeKind.Barracks
                    or NodeKind.House or NodeKind.Farm or NodeKind.PalisadeWall or NodeKind.StoneWall;
                var radius = node.Kind switch
                {
                    NodeKind.Tree => 2.2f,
                    NodeKind.Outcrop => 3.0f,
                    NodeKind.Pile => 1.3f,
                    _ => MathF.Max(1.6f, node.HalfExtent + 0.35f),
                };
                DrawSelectionMark(
                    node.Position,
                    radius,
                    node.Id == selectedNode ? SelectionPlateAlpha : HoverPlateAlpha,
                    square);
            }

            if (node.Kind == NodeKind.Farm)
            {
                DrawField(in node);
                continue;
            }

            if (node.IsStanding)
            {
                // <b>The frustum decides whether a tree is drawn; this only keeps the far field out.</b>
                // A radius used to decide it, and a radius cannot: VisibleGroundRadius is frustum trigonometry
                // measured from the <em>camera</em> — eye height, tan(pitch − halfFov), slant, aspect — and it
                // was then applied as a distance from <em>cameraFocus</em>, which is a different point. The
                // visible ground is an asymmetric trapezoid reaching well past the focus, so a circle centred
                // there under-covers its far side, and under-covers it more the closer the camera gets, because
                // the focus sits proportionally deeper into the view.
                //
                // Since the ground began following the frustum this session, the two disagreed visibly: grass
                // drawn where trees were culled, and trees vanishing as you zoomed in. The cap is deliberately
                // generous — twice the visible radius — because its only job now is to stop the loop
                // frustum-testing the whole map at maximum zoom-out. Everything inside it is InView's call.
                // Culled against what the camera is looking at, because a dense woodland is thousands of
                // models and the camera can see about ninety metres of it. The radius has to cover the
                // shadow box as well as the view — a tree behind the camera still casts into the frame —
                // so it is compared against both. Not an optimisation so much as the thing that makes a
                // forest affordable at all: without it the frame draws the whole map every frame.
                if (!look.DrawEveryTree &&
                    Vector2.DistanceSquared(node.Position, cameraFocus) > treeCullBoundSquared)
                {
                    continue;
                }

                treesOffered++;
                // Timed around the branch rather than inside it, so "is the node phase trees" is answerable
                // before anything is guessed about which part of a tree costs.
                //
                // <b>Sampled, because the instrument was a sixth of what it was measuring.</b> Probing every
                // node meant two Stopwatch.GetTimestamp calls thirty-four thousand times a frame — around
                // 4.5 ms of the 26 it reported, all of it the cost of asking. One node in thirty-two, scaled
                // back up: the answer to "how long does a tree take" needs an average and not a census, and a
                // thousand samples is a fine average. The probe overhead drops with it, so the number stops
                // including a sixth of itself.
                var timed = timingDebug && (treesOffered & 31) == 0;
                var treeStart = timed ? Stopwatch.GetTimestamp() : 0L;
                DrawTree(in node);
                if (timed)
                {
                    treeTicks += Stopwatch.GetTimestamp() - treeStart;
                    treeSamples++;
                }

                continue;
            }

            if (node.Kind == NodeKind.Outcrop)
            {
                // <b>Not the tree cull, and my own comment here used to say why while doing the opposite.</b>
                // It read "same cull as a tree and for the same reason, though there are two orders of magnitude
                // fewer of them" — which is the argument <em>against</em> that cull, written down and then not
                // followed. The tree radius exists because a woodland is thousands of models and the camera can
                // see ninety metres of it; a map has fifteen to thirty outcrops. Culling them to the foliage
                // budget bought nothing and cost the thing they are for.
                //
                // Reported from the chair as not being able to see stone on any map, and on the village's
                // default map the nearest rock is 282 m from the village against a ~130 m tree radius — so
                // every outcrop on it was invisible from anywhere a player would stand. <b>A quarry is a
                // landmark.</b> You are supposed to see the rock from across the valley and decide to go
                // there, which is the whole of how an unreachable resource becomes a reason to expand.
                //
                // The frustum still decides, inside DrawOutcrop.
                DrawOutcrop(in node);
                continue;
            }

            var ground = simulation.Terrain.SampleHeight(node.Position);
            var width = node.HalfExtent * 2f;
            if (node.Kind is NodeKind.PalisadeWall or NodeKind.StoneWall)
            {
                DrawWall(in node, ground, width);
                continue;
            }

            if (art is not null)
            {
                // Scaled to the footprint the simulation enforces rather than to anything about the
                // model: a granary is 7.5 m because NodeFootprint says it occupies five placement cells
                // and bodies route around exactly that square. The art fits the game, not the reverse.
                var yaw = SettlementArt.SquareYawOf(node.Id.Value);
                DrawHabitationLights(in node, ground, width, yaw);
                if (node.IsUnderConstruction)
                {
                    AddContactShadow(node.Position, width * 0.62f, 0.55f);
                }

                if (node.IsUnderConstruction)
                {
                    DrawSite(in node, ground, width, yaw);
                    continue;
                }

                var placement = SettlementArt.Placement(node.Position, ground, width, yaw);
                // A building's contact reaches a little past its walls, which is where the ground is
                // sheltered from the sky by its eaves.
                AddContactShadow(node.Position, width * 0.78f, 0.75f);

                switch (node.Kind)
                {
                    case NodeKind.Granary:
                        AddCascaded(art.Granary, placement);
                        continue;
                    case NodeKind.Barracks:
                        AddCascaded(art.Granary, placement);
                        continue;
                    case NodeKind.House:
                        // A village of one cottage repeated is a village nobody believes — see HouseFor,
                        // which owns the id-to-cottage rule for all four callers of it.
                        AddCascaded(art.HouseFor(node.Id.Value), placement);
                        continue;
                    default:
                        AddCascaded(art.Depot, placement);
                        continue;
                }
            }

                DrawGreyboxBuilding(in node, ground, width);
            }
        }

        // Exact without revisiting hidden buckets: every live node is indexed, and the count above names
        // precisely those admitted by the shared static gate.
        nodesBehindTheVeil = simulation.Nodes.LiveCount - nodesAllowedByFog;
    }

    /// <summary>
    /// The small warm signals that say people live here: lit windows, a lantern, embers at a site.
    /// </summary>
    /// <remarks>
    /// <b>The cheapest thing in this whole visual pass and probably the strongest.</b> A landscape at night
    /// recedes — the palette goes blue, the colour drains out of it, the distance disappears into haze — and
    /// a settlement with a dozen warm pixels in it becomes the only thing in the frame the eye will look at.
    /// That is a composition the day cannot produce at any price: at noon the terrain is the subject, at
    /// golden hour the architecture is, and at night it is the village. Three different pictures out of one
    /// cycle, for the cost of some boxes and a texture read.
    /// <para>
    /// Each of them is a fact rather than a decoration, which is the rule the wear and the stumps follow
    /// too. <b>A house is lit if somebody lives in it and dark if nobody does</b> — so an unhoused
    /// settlement's empty cottages are visibly empty, which is a thing §51 wanted the interface to say and
    /// this says without a word of text. A fuller house throws a stronger pool. A site has a brazier while
    /// it is being built and none once it is finished.
    /// </para>
    /// <para>
    /// Kept small deliberately, and smaller twice over: the failure mode of night lighting is not one
    /// source being too bright, it is pools of orange everywhere, at which point the settlement stops being
    /// a warm island in a cool landscape and the whole composition is gone.
    /// </para>
    /// </remarks>
    private void DrawHabitationLights(in EconomyNode node, float ground, float width, float yaw)
    {
        if (art is null || !look.SunFollowsTheYear || sky.Nightness < 0.02f || look.HearthSpark <= 0f) return;

        // <b>Measured off the model, not off an assumed unit box — which is what had the lights floating in
        // the air.</b> NormaliseToUnitFootprint makes the <em>longer</em> horizontal axis one and scales the
        // other two to match, so a cottage that is 1.0 by 0.7 has walls at ±0.5 on one axis and ±0.35 on
        // the other, and its ridge is wherever its height happens to land. Placing a window at 0.5 therefore
        // hung it half a metre off the side of the building on one axis out of two, and at a height that
        // was above the roof of anything squat. The bounds are baked, so they are exactly the extents the
        // placement will scale.
        var model = node.Kind is NodeKind.Granary or NodeKind.Barracks
            ? art.Granary
            : node.IsSink ? art.HouseFor(node.Id.Value) : art.Depot;
        // The walls, measured at load rather than assumed from the bounding box — which for this pack turns
        // out to be nearly the same thing on one axis and five centimetres per metre out on the other. See
        // WallsOf, including what measuring disproved.
        var box = art.WallsOf(model);
        var eaves = model.Bounds.Max.Y - model.Bounds.Min.Y;

        // Counted for the geometry line, which is where every other "how much of this is on screen"
        // number in this file already lives.
        habitationLights++;

        if (node.IsUnderConstruction)
        {
            // A brazier on the ground clear of the footprint, which is where you would actually put one:
            // the middle is where the walls are going.
            AddEmber(
                node.Position, ground, width, yaw,
                new Vector3(box.Max.X + 0.10f, 0.05f, 0f), new Vector3(0.06f));
            return;
        }

        // <b>The visible source is gone, and that is the fix rather than a retreat from one.</b> Twice now
        // a lit rectangle on a wall has been reported as modern lighting, and lowering it and re-tinting it
        // did not help, because the problem is not where it is or what colour it is: <em>a flat quad of
        // uniform brightness is a lamp</em>. It has an edge, it has an even face, and nothing about a fire
        // is even. Worse, it has to be put on a particular face of the model, and the door of a cottage in
        // this pack is not reliably on the axis the fitting happens to call +x — so half of them were
        // glowing out of a blank wall.
        //
        // What is left is a spark you can only see up close and the pool of light on the ground outside,
        // which is what "a hearth showing faintly through a door" actually looks like from any distance
        // worth drawing it at: not a bright shape, a warm patch. The pool has no edge, no orientation and
        // no opinion about which wall the door is in, which is why it can be right about all three.
        var spark = new Vector3(0.020f);
        var hearthFloor = box.Min.Y + eaves * 0.035f;

        if (node.IsSink)
        {
            if (node.Occupants <= 0) return;
            AddEmber(node.Position, ground, width, yaw, new Vector3(box.Max.X, hearthFloor, 0f), spark);
            return;
        }

        if (node.Kind is NodeKind.Granary or NodeKind.Barracks)
        {
            AddEmber(
                node.Position, ground, width, yaw,
                new Vector3(box.Max.X, hearthFloor, 0f), spark * 1.3f);
        }
    }

    /// <summary>
    /// One small self-lit box, placed in a building's own frame rather than the world's.
    /// </summary>
    /// <remarks>
    /// Everything is given as a share of the footprint's width, because that is what the models are fitted
    /// to — so a window sits on the wall of a cottage and of a granary without either number being about
    /// any one building. Scale, then the offset within the model, then the building's turn, then the world:
    /// the order matters, and getting it wrong puts the window on the ground beside the house.
    /// </remarks>
    private void AddEmber(
        Vector2 position, float ground, float width, float yaw, Vector3 local, Vector3 size)
    {
        propInstances.Add(new InstanceData(
            Matrix4x4.CreateScale(size * width) *
            Matrix4x4.CreateTranslation(local * width) *
            Matrix4x4.CreateRotationY(yaw) *
            Matrix4x4.CreateTranslation(position.X, ground, position.Y),
            EmberTint));
    }

    /// <summary>
    /// A building site: the timber lying on it, and the walls as far up as they have got.
    /// </summary>
    /// <remarks>
    /// Two things a player needs at a glance and they are two different facts. <b>Material on the ground</b>
    /// shows the unspent loads physically waiting to be incorporated; <b>height</b> shows how far labour has
    /// advanced the structure. Collapsing them into one "under construction" marker would hide whether the
    /// project needs more material, more hands, or simply time from the hands already present.
    /// </remarks>
    private void DrawSite(in EconomyNode site, float ground, float width, float yaw)
    {
        // <b>Unspent material physically waiting at the site.</b> The stacks grow as loads arrive and shrink
        // as builders incorporate them; finished structure progress is drawn independently below.
        var cost = Construction.CostFor(site.Kind);
        var owed = cost.Total;
        var here = 0;
        foreach (var resource in Resources.All) here += Math.Min(cost[resource], site.Stock[resource]);
        var waiting = owed <= 0 ? 0f : MathF.Min(1f, here / (float)owed);
        var timber = cost.Wood;
        if (waiting > 0.02f && art!.WoodHeap is { } logs)
        {
            // Stacked round the footprint rather than in the middle of it, because the middle is where the
            // walls are going and because a stack that grows outward reads as materials arriving.
            var stacks = 1 + (int)(waiting * 3.99f);
            for (var i = 0; i < stacks; i++)
            {
                var angle = yaw + (i + 0.5f) / stacks * MathF.Tau;
                var at = site.Position +
                         new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (width * 0.42f);
                AddCascaded(logs, SettlementArt.Placement(at, ground, width * 0.30f, angle));
            }
        }

        var labour = Construction.LabourFor(site.Kind);
        var raised = labour <= 0f ? 1f : MathF.Min(1f, site.BuildWork / labour);
        if (raised <= 0.01f) return;
        var rising = SettlementArt.Rising(site.Position, ground, width, yaw, raised);
        var model = site.Kind switch
        {
            NodeKind.Granary => art!.Granary,
            NodeKind.Barracks => art!.Granary,
            NodeKind.House => art!.HouseFor(site.Id.Value),
            _ => art!.Depot,
        };
        AddCascaded(model, rising);
    }

    /// <summary>
    /// A box with a roof on it, which is what a building was before there were models.
    /// </summary>
    /// <remarks>
    /// Kept, and not as sentiment: it is the fallback if the art fails to load, and it is the shape every
    /// judgement about scale in §21 was made against — a granary at 7.5 m and a villager at 1.45 was
    /// settled here, and a model that reads as the wrong size is a model to rescale rather than a reason
    /// to move the footprint. A store's height reads how full it is, and a house's how many live in it.
    /// </remarks>
    private void DrawGreyboxBuilding(in EconomyNode node, float ground, float width)
    {
        var (wall, roof) = node.Kind switch
        {
            NodeKind.Granary => (GranaryColor, GranaryRoofColor),
            NodeKind.Barracks => (GranaryColor, GranaryRoofColor),
            NodeKind.House => (HouseColor, HouseRoofColor),
            _ => (DepotColor, DepotRoofColor),
        };
        var fullness = node.IsSink
            ? node.Occupancy <= 0 ? 0f : MathF.Min(1f, node.Occupants / (float)node.Occupancy)
            : node.Capacity <= 0 ? 0f : MathF.Min(1f, node.Stock.Total / (float)node.Capacity);
        var height = AgentDefaults.BodyHeight * (1.35f + fullness * 0.75f);
        propInstances.Add(new InstanceData(
            Matrix4x4.CreateScale(width, height, width) *
            Matrix4x4.CreateTranslation(node.Position.X, ground + height * 0.5f, node.Position.Y),
            wall));
        var roofWidth = width + 0.55f;
        const float roofThickness = 0.42f;
        propInstances.Add(new InstanceData(
            Matrix4x4.CreateScale(roofWidth, roofThickness, roofWidth) *
            Matrix4x4.CreateTranslation(
                node.Position.X, ground + height + roofThickness * 0.35f, node.Position.Y),
            roof));
    }

    /// <summary>A stable wall segment whose material changes only when its upgrade completes.</summary>
    private void DrawWall(in EconomyNode node, float ground, float width)
    {
        var labour = StructuralProjects.LabourFor(in node);
        var projectShare = node.HasStructuralProject && labour > 0f
            ? Math.Clamp(StructuralProjects.WorkFor(in node) / labour, 0f, 1f)
            : 1f;
        var palisadeShare = node.Kind == NodeKind.PalisadeWall
            ? node.IsUnderConstruction ? projectShare : 1f
            : 0f;
        const float wallHeight = 2.25f;

        if (palisadeShare > 0.01f)
        {
            var height = wallHeight * palisadeShare;
            // Eight rough posts around a square make the segment orientation-free for this first proof. The
            // footprint, collision and later stone shell are the same square; authored wall runs can decide
            // connection art when content work reaches them without reopening the structural operation.
            for (var i = 0; i < 8; i++)
            {
                var edge = i / 2;
                var side = i % 2 == 0 ? -0.34f : 0.34f;
                var local = edge switch
                {
                    0 => new Vector2(side, -0.38f),
                    1 => new Vector2(0.38f, side),
                    2 => new Vector2(-side, 0.38f),
                    _ => new Vector2(-0.38f, -side),
                };
                propInstances.Add(new InstanceData(
                    Matrix4x4.CreateScale(width * 0.16f, height, width * 0.16f) *
                    Matrix4x4.CreateTranslation(
                        node.Position.X + local.X * width,
                        ground + height * 0.5f,
                        node.Position.Y + local.Y * width),
                    PalisadeWallColor));
            }
        }

        var stoneShare = node.Kind == NodeKind.StoneWall
            ? 1f
            : node.StructuralProject == StructuralProjectKind.Upgrade ? projectShare : 0f;
        if (stoneShare <= 0.01f) return;
        var stoneHeight = wallHeight * stoneShare;
        propInstances.Add(new InstanceData(
            Matrix4x4.CreateScale(width * 0.92f, stoneHeight, width * 0.92f) *
            Matrix4x4.CreateTranslation(
                node.Position.X, ground + stoneHeight * 0.5f, node.Position.Y),
            StoneWallColor));
    }

    /// <summary>
    /// A field: tilled ground with this year's crop standing on it.
    /// </summary>
    /// <remarks>
    /// <b>This is the crop cycle made visible, and it is the only interface it has.</b> A field's
    /// trouble is always in the past — a ceiling not set in spring cannot be diagnosed at harvest from
    /// anything a body is doing — so <see cref="CropCycle.StateOf"/> exists to have the field say so out
    /// loud. Saying it in a console table is not saying it to a player. Here the same fact is the ground:
    /// bare tilled soil is a field with nothing on it, a low green crop is one being tended, and a tall
    /// gold one is a crop standing unreaped that shrinks patch by patch as it comes in. A player who
    /// never reads a number can see that one field in twelve is the wrong colour.
    /// <para>
    /// The plot is stretched to fill its square footprint rather than scaled, so a dozen fields tile edge
    /// to edge into one patchwork — which is only possible because a field is not a wall
    /// (<see cref="NodeFootprint.Blocks"/>) and can therefore abut its neighbour.
    /// </para>
    /// <para>
    /// Three wheat models rather than one scaled: the crop is <em>shorter and sparser</em> when it is
    /// young, not merely smaller, and a single mesh scaled down its Y axis reads as a mown lawn.
    /// </para>
    /// </remarks>
    /// <summary>How far a tilled plot sits above the ground it is drawn on, to break the tie.</summary>
    private const float FieldPlotLift = 0.02f;

    /// <summary>The pack's own dirt, which <see cref="LookSettings.SoilBrightness"/> scales.</summary>
    private static readonly Vector3 TilledEarth = new(0.0946f, 0.0740f, 0.0289f);

    private void DrawField(in EconomyNode field)
    {
        var ground = simulation.Terrain.SampleHeight(field.Position);
        var width = field.HalfExtent * 2f;
        var phase = CropCycle.PhaseOf(simulation.Date.Season);
        var ceiling = CropCycle.CeilingOf(in field);
        var reapTarget = CropCycle.ReapTargetOf(in field);
        // How much of this year's crop is still standing in the field, from nothing to all of it.
        var standing = phase switch
        {
            CropPhase.Maintain => ceiling * CropCycle.RetentionOf(in field) * 0.55f,
            CropPhase.Reap => reapTarget <= 0f
                ? 0f
                : CropCycle.PotentialOf(in field) * (1f - MathF.Min(1f, field.ReapWork / reapTarget)),
            _ => 0f,
        };

        if (art is not null)
        {
            // Fields keep the grid: a plot at seventeen degrees to its neighbour is not a patchwork, and
            // the furrows in the model are what make the block read as cultivated ground from above.
            //
            // Lifted two centimetres. The plot is a flat slab and the coarse ground under it is a flat
            // slab at exactly the same height, and two coplanar surfaces do not draw one in front of the
            // other — they z-fight, and the winner is chosen per pixel, which is why the tilled ground
            // was not visible at all where it should have been most obvious. Two centimetres is well
            // inside the depth buffer's precision at this range and invisible from any camera angle the
            // game allows.
            // <b>Neighbouring plots run crosswise, which is what makes a block of fields a patchwork.</b>
            // Every field kept the same orientation, so twelve of them tiled edge to edge lined their
            // furrows up into continuous forty-metre rows and the whole block read as ribbons rather than
            // as fields. A quarter turn on a checkerboard of the plot's own position breaks the rows at
            // every boundary while keeping each field square to the grid — the plot and its crop share the
            // yaw, so the wheat still grows along the furrows it is planted in.
            var parity = (int)MathF.Floor(field.Position.X / 1.5f) +
                         (int)MathF.Floor(field.Position.Y / 1.5f);
            var yaw = (parity & 1) == 0 ? 0f : MathF.PI * 0.5f;
            var placement = SettlementArt.Placement(field.Position, ground + FieldPlotLift, width, yaw);
            // Tilled earth, lifted off the pack's near-black dirt. See LookSettings.SoilBrightness: the
            // stripes were the contrast between 0.09 soil and 0.38 wheat, not a misalignment.
            // The plot carries the tint when there is one, because a field's own colour <em>is</em> a tint —
            // there is no material to override, so the override is simply a different colour.
            AddCascaded(art.FieldPlot, placement, new Vector4(TilledEarth * look.SoilBrightness, 1f));
            if (standing > 0.02f)
            {
                var stage = standing >= 0.66f ? 2 : standing >= 0.33f ? 1 : 0;
                AddCascaded(art.Crop[stage], placement);
            }

            return;
        }

        var soil = Vector4.Lerp(FallowSoilColor, TilledSoilColor, MathF.Min(1f, ceiling * 1.4f));
        propInstances.Add(new InstanceData(
            Matrix4x4.CreateScale(width, 0.09f, width) *
            Matrix4x4.CreateTranslation(field.Position.X, ground + 0.045f, field.Position.Y),
            soil));
        if (standing <= 0.01f) return;
        var cropHeight = 0.14f + standing * 0.50f;
        var cropColor = Vector4.Lerp(
            YoungCropColor, RipeCropColor, phase == CropPhase.Reap ? 1f : 0.22f);
        propInstances.Add(new InstanceData(
            Matrix4x4.CreateScale(width * 0.9f, cropHeight, width * 0.9f) *
            Matrix4x4.CreateTranslation(
                field.Position.X, ground + 0.09f + cropHeight * 0.5f, field.Position.Y),
            cropColor));
    }


    private void AddContactShadow(Vector2 at, float radius, float strength)
    {
        if (contactInstances.Count >= MaximumContactShadows) return;
        var contactReach = Sees * ContactShare;
        if (Vector2.DistanceSquared(at, cameraFocus) > contactReach * contactReach) return;
        var ground = simulation.Terrain.SampleHeight(at);
        var normal = simulation.Terrain.SampleNormal(at);
        // Lean the disc onto the ground: x and z stay unit length so the footprint is exact, and only y
        // leans — the same shear the ground plates used before they were meshed away.
        var lean = Matrix4x4.Identity;
        lean.M12 = -normal.X / MathF.Max(0.2f, normal.Y);
        lean.M32 = -normal.Z / MathF.Max(0.2f, normal.Y);
        contactInstances.Add(new InstanceData(
            Matrix4x4.CreateScale(radius, 1f, radius) * lean *
            Matrix4x4.CreateTranslation(at.X, ground + 0.035f, at.Y),
            new Vector4(0f, 0f, 0f, strength)));
    }

    /// <summary>
    /// How many contact discs may be drawn at once.
    /// </summary>
    /// <remarks>
    /// A ceiling in the same spirit as the grass budget: contact shadows are the second most numerous thing
    /// in the frame after ground cover, one per tree in view, and the frame should degrade by losing the
    /// furthest of them rather than by dropping frames.
    /// </remarks>
    private const int MaximumContactShadows = 6_000;

    /// <summary>
    /// Where each species sits in the tree list, as a range. Ordered by construction — see SettlementArt.
    /// </summary>
    /// <remarks>
    /// Broadleaf on good level ground, conifer where it is steep and stony, twisted scrub on the exposed
    /// moor, and dead trees standing in the wet bottoms. Four species is the difference between a map with a
    /// forest on it and a map with country in it.
    /// </remarks>
    private static readonly (int First, int Count) Broadleaf = (0, 3);
    private static readonly (int First, int Count) Conifer = (3, 3);
    private static readonly (int First, int Count) Twisted = (6, 2);
    private static readonly (int First, int Count) Dead = (8, 2);

    /// <summary>
    /// Which kind of tree stands here, from what the ground is doing rather than from the tree's id.
    /// </summary>
    /// <remarks>
    /// <b>Conifer on the steep and the high, broadleaf on the level.</b> The same rule that decides where
    /// woodland survives at all decides what kind it is, and for the same reason: the ground people can
    /// plough is the ground they clear, and what is left on the slopes and the tops is the poorer, colder
    /// stand. It costs one grade sample per drawn tree and it is the difference between a forest painted
    /// over a landscape and a forest that belongs to it.
    /// <para>
    /// The boundary is jittered per tree rather than sharp, which is the whole trick: a clean line between
    /// two species follows a contour and reads as a stencil. A threshold that moves a little from trunk to
    /// trunk gives a band of mixed wood tens of metres deep, which is what a treeline looks like.
    /// </para>
    /// <para>
    /// Dressing by §52's test — a function of a world position and an id, holding no state, and read by no
    /// decision. The simulation's trees are all the same tree.
    /// </para>
    /// </remarks>
    private int TreeKindAt(Vector2 at, int id)
    {
        if (art is null || art.Trees.Length < Dead.First + Dead.Count) return 0;
        // A stable jitter per trunk, so a species boundary is a band of mixed wood tens of metres deep
        // rather than a line following a contour. A clean line between two species reads as a stencil.
        var hash = (uint)(id * 2654435761u);
        hash = (hash ^ (hash >> 15)) * 2246822519u;
        var jitter = ((hash ^ (hash >> 13)) & 0xFFFFu) / (float)0x10000u;

        // The biome decides the species and the jitter decides where the boundary falls, by nudging the
        // ground the classifier is asked about rather than by second-guessing its answer — so the mixing
        // happens in the same units the rule is written in.
        var wobble = new Vector2(jitter - 0.5f, (hash & 0xFFu) / 255f - 0.5f) * 22f;
        // <b>The region decides what a wood is made of, the biome decides the exception, and how crowded a
        // place is decides the rest.</b> Biome alone gave every region the same wood; region and biome together
        // still gave a closed forest and a field with three trees in it the same species mix, which is the one
        // distinction a person actually reads. A closed wood is nearly a monoculture — whatever won there won
        // everywhere — and open ground keeps the survivors, which are the crooked and the dead.
        var conifers = RegionProfile.For(simulation.Terrain.Region).ConiferShare;
        // <b>The field, not the renderer's canopy counts.</b> Those counts exist for level of detail and are a
        // dressing-side <em>proxy</em> for woodland pressure: they measure how many trees were placed nearby,
        // which is downstream of the thing that decided to place them. Species is a question about the wood, so
        // it asks the wood.
        var pressure = simulation.Terrain.Woodland?.At(at) ?? 1f;
        var range = CountryAt(at + wobble) switch
        {
            // <b>The biome exceptions come first, because they are about what can live there at all.</b> No
            // amount of crowding makes a marsh grow pine.
            Biome.Scree => Conifer,
            Biome.Crag => Conifer,
            Biome.Moor => Twisted,
            Biome.Marsh => Dead,
            // <b>Floodplain gets the twisted form, and it is standing in for a willow.</b> What survives on
            // a river flat is the thing that tolerates having its roots underwater half the year and being
            // grazed the rest, and in this kit that is the low crooked shape rather than the tall clean one.
            Biome.Floodplain => Twisted,
            // <b>A closed wood is almost one species.</b> Whatever suits the region best crowds out the rest,
            // so past the crowding threshold the mix collapses toward the region's own — the jitter is squared,
            // which pushes it to whichever end it was already leaning.
            _ when pressure >= 0.85f => jitter * jitter < conifers ? Conifer : Broadleaf,
            // <b>And a straggler is a survivor.</b> One tree in a field is there because nothing removed it:
            // a crooked hedgerow oak, or a standing dead one. Half the stragglers are the shapes nobody would
            // plant — which is exactly what makes open country read as open rather than as thin forest.
            _ when pressure <= 0.22f => jitter < 0.42f ? Twisted : (jitter < 0.58f ? Dead : Broadleaf),
            _ => jitter < conifers ? Conifer : Broadleaf,
        };

        return range.First + id % range.Count;
    }

    /// <summary>
    /// The map lab: a large canvas to roll landscapes on and a window to pick one out of.
    /// </summary>
    /// <remarks>
    /// <b>What it is for.</b> A generator is only as good as the loop you can tune it in, and every dial in
    /// this one had been tuned by editing a constant, rebuilding and squinting at a village. The lab rolls a
    /// whole landscape at a stroke, cycles the archetypes, and draws a six-hundred-metre window on the ground
    /// so a promising patch can be framed and written down.
    /// <para>
    /// <b>And the canvas is larger than the game's map on purpose, which is the part that matters.</b> A
    /// 600 m window cut out of a coherent 1800 m landscape is a <em>fragment</em> — its river genuinely comes
    /// from off-window, its ridge genuinely continues past the edge, and the ground has a context the window
    /// does not contain. That is the property the inherited-inflow constant was faking, and framing a crop
    /// makes it true instead.
    /// </para>
    /// <para>
    /// The pick is <c>(archetype, seed, window centre)</c>, printed on demand, and that triple is the whole
    /// identity of a map — enough to regenerate it exactly, because everything under it is deterministic.
    /// </para>
    /// </remarks>
    private readonly bool mapLab;

    private readonly MapTuning mapTuning = new();

    /// <summary>Whether this session builds its ground around an authored drainage network. §152.</summary>
    private readonly bool eroded;

    /// <summary>How much relief the village generates when nobody has said. See the constructor.</summary>
    /// <remarks>
    /// Thirty-two metres over 600 is the amplitude every archetype was judged at in <c>--shapes</c> and the one
    /// the gate's generated-terrain leg uses, give or take two. A number already argued about is a better
    /// default than a new one.
    /// </remarks>
    private const float DefaultVillageRelief = 32f;

    /// <summary>
    /// Which map the village opens on.
    /// </summary>
    /// <remarks>
    /// <b>Three valleys meeting at a lowland junction, because it is the archetype whose woods read.</b> The
    /// default was DiagonalRiver, and on the default seed that archetype's intensity roll comes up open pastoral
    /// — 16% of the map at closed canopy against YValley's 54% on the same seed. A first impression should not
    /// be the sparsest thing the generator makes.
    /// </remarks>
    private Archetype labArchetype = Archetype.YValley;

    /// <summary>
    /// Whether the canvas is filled with one archetype or with the whole family.
    /// </summary>
    /// <remarks>
    /// Mixed by default, because a canvas of nine different neighbourhoods is nine candidate maps and a canvas
    /// of nine of the same is one map nine times. Pinning it to a single archetype is for judging that
    /// archetype — which is what the cycle keys are for — rather than for finding a map.
    /// </remarks>
    private bool labPinned;

    /// <summary>Which kind of country the canvas is. One region per canvas, because a region is regional.</summary>
    private Region labRegion = Region.Downland;

    private Archetype? labOnlyArchetype => labPinned ? labArchetype : null;
    private uint labSeed = 0x5EED1234u;
    private ReliefPlan? labPlan;
    private Vector2 labWindow = Vector2.Zero;
    private float labFloor;
    private float labSpan;

    /// <summary>How much ground a picked map covers. The game's own extent, and the point of the window.</summary>
    private const float LabWindowMetres = 600f;

    /// <summary>How much height the map has, and where its floor is. Recomputed when the terrain moves.</summary>
    private float reliefSpan;
    private float reliefFloor;

    /// <summary>
    /// The country, the hollows and the cover density, sampled once per terrain change instead of per frame.
    /// </summary>
    /// <remarks>
    /// <b>Measured: the scatter was costing 68 ms a frame on its own, and the frame had been sixty.</b> The
    /// cause was not the drawing — the triangle count barely moved — it was that ground cover walks forty
    /// thousand candidate cells at a two hundred metre standoff, and both the relief coupling and the biome
    /// classifier were sampling the height field for every one of them, before anything had decided whether
    /// that cell places so much as a tuft. About eight hundred thousand bilinear height reads a frame to
    /// answer questions whose answers change over tens of metres.
    /// <para>
    /// So they are answered on an eight metre grid, once, when the terrain moves — five and a half thousand
    /// cells against forty thousand queries a frame, and the queries become an array index. Eight metres
    /// because that is finer than anything here varies at: a biome is a feature of a hillside and a hollow
    /// is twenty metres across by definition.
    /// </para>
    /// <para>
    /// Nearest-cell for the biome, because it is a discrete choice and the species jitter already ragged its
    /// edges; bilinear for the two continuous fields, because banding in cover density would show.
    /// </para>
    /// </remarks>
    private const float CountryCellMetres = 8f;

    private byte[] countryBiome = Array.Empty<byte>();
    private float[] countryHollow = Array.Empty<float>();
    private float[] countryCover = Array.Empty<float>();
    private int countryCells;

    private void RebuildCountryField()
    {
        var extent = simulation.ExtentMeters;
        countryCells = Math.Max(2, (int)MathF.Ceiling(extent / CountryCellMetres) + 1);
        var total = countryCells * countryCells;
        if (countryBiome.Length != total)
        {
            countryBiome = new byte[total];
            countryHollow = new float[total];
            countryCover = new float[total];
        }

        var span = MathF.Max(1f, reliefSpan);
        for (var z = 0; z < countryCells; z++)
        for (var x = 0; x < countryCells; x++)
        {
            var at = CountryPosition(x, z);
            var index = z * countryCells + x;
            countryBiome[index] = (byte)Biomes.At(simulation.Terrain, at, reliefFloor, span);
            countryCover[index] = GroundCoverRelief(at, out var hollow);
            countryHollow[index] = hollow;
        }
    }

    private Vector2 CountryPosition(int x, int z) =>
        new(
            x * CountryCellMetres - simulation.ExtentMeters * 0.5f,
            z * CountryCellMetres - simulation.ExtentMeters * 0.5f);

    private Biome CountryAt(Vector2 at)
    {
        if (countryCells == 0) return Biome.Meadow;
        var local = (at + new Vector2(simulation.ExtentMeters * 0.5f)) / CountryCellMetres;
        var x = Math.Clamp((int)MathF.Round(local.X), 0, countryCells - 1);
        var z = Math.Clamp((int)MathF.Round(local.Y), 0, countryCells - 1);
        return (Biome)countryBiome[z * countryCells + x];
    }

    /// <summary>Cover density and hollowness here, interpolated so neither bands at the grid.</summary>
    private float CoverAt(Vector2 at, out float hollow)
    {
        hollow = 0f;
        if (countryCells == 0) return 1f;
        var local = (at + new Vector2(simulation.ExtentMeters * 0.5f)) / CountryCellMetres;
        var x0 = Math.Clamp((int)MathF.Floor(local.X), 0, countryCells - 2);
        var z0 = Math.Clamp((int)MathF.Floor(local.Y), 0, countryCells - 2);
        var tx = Math.Clamp(local.X - x0, 0f, 1f);
        var tz = Math.Clamp(local.Y - z0, 0f, 1f);

        float Sample(float[] field)
        {
            var a = field[z0 * countryCells + x0];
            var b = field[z0 * countryCells + x0 + 1];
            var c = field[(z0 + 1) * countryCells + x0];
            var d = field[(z0 + 1) * countryCells + x0 + 1];
            return (a + (b - a) * tx) * (1f - tz) + (c + (d - c) * tx) * tz;
        }

        hollow = Sample(countryHollow);
        return Sample(countryCover);
    }

    /// <summary>Milliseconds the last frame spent building each part of itself.</summary>
    /// <summary>
    /// Where the frame's CPU went, by phase.
    /// </summary>
    /// <remarks>
    /// <b>The last field used to be called <c>Overlay</c> and was not the overlay.</b> It was the build clock
    /// read at the end, so it held everything after the scatter — the collider overlay, every batch's instance
    /// staging, and the whole of the render graph's record and execute — under the name of the one component in
    /// there that <c>--timings</c> exists to switch off. So the biggest single line item in the frame, 8.1 ms of
    /// 16.6, read as something you could ignore because you had already turned it off.
    /// <para>
    /// The third instrument this session that was named after a subset of what it measured, after <c>BUILD
    /// terrain</c> also timing the tree instances and <c>relief: flat</c> counting only landforms. The failure
    /// mode is always the same: it stays plausible, so nobody looks, so the cost hides in the one place a
    /// reader has already decided is uninteresting.
    /// </para>
    /// </remarks>
    private (
        double Terrain,
        double Agents,
        double Scatter,
        double Overlay,
        double Stage,
        double Record) buildPhases;

    /// <summary>
    /// Wall clock for a whole frame, smoothed.
    /// </summary>
    /// <remarks>
    /// The number the report is actually about. A build breakdown says where the time inside a frame goes and
    /// says nothing about whether the frame is fast — and this session lost a factor of twelve without any
    /// figure on screen that would have shown it, which is the argument for putting one there.
    /// </remarks>
    private double frameMilliseconds;

    /// <summary>The colour a unit of a resource is, on a back or in a heap.</summary>
    /// <remarks>
    /// A switch rather than the <c>Grain ? grain : wood</c> pairs this replaced in three places. Those were
    /// harmless while there were two resources and became wrong in the same instant stone existed: a quarrier
    /// walked home carrying something the colour of timber.
    /// </remarks>
    private static Vector4 ColourOf(Resource resource) => resource switch
    {
        Resource.Grain => GrainColor,
        Resource.Stone => StoneColor,
        _ => WoodColor,
    };

    /// <summary>
    /// An outcrop: a rock the size of what is still in it.
    /// </summary>
    /// <remarks>
    /// <b>The models were loaded and deliberately never drawn, waiting for exactly this.</b>
    /// <c>SettlementArt.Rocks</c> holds two of them with a note explaining why they were kept out of the open
    /// scatter — "stone is about to be a <em>resource</em>, mined from a deposit somebody chooses to work.
    /// Strewing rocks over the whole map as decoration teaches the player that a rock is nothing to look at,
    /// which is precisely the wrong lesson to teach a fortnight before rocks start mattering." A fortnight
    /// later, here they are, and a rock in this scene means stone.
    /// <para>
    /// Shrinking as it is worked, on the cube root of what is left rather than the square root a tree uses: a
    /// quarry is eaten into in three dimensions and a trunk is felled in one, so a half-worked outcrop should
    /// still read as a substantial rock rather than as half a rock.
    /// </para>
    /// </remarks>
    /// <summary>
    /// A perimeter on the ground around a node, following the ground.
    /// </summary>
    /// <remarks>
    /// <b>Four bars rather than one plate, and each sampled at its own midpoint.</b> A single quad at the
    /// node's centre height is right on the flat and wrong everywhere else — on a slope one edge floats and
    /// the opposite edge is buried, which is the same flat-ground assumption this file has already been caught
    /// making four times about view distances. Sampling each bar where it actually lies costs four height
    /// lookups for a thing there is at most a handful of on screen.
    /// <para>
    /// A perimeter and not a filled plate because the ground under a building is worth seeing: what is being
    /// answered is "which one is picked", not "what colour is this square". It is drawn a little outside the
    /// footprint the simulation enforces, so the outline reads as a boundary around the thing rather than as
    /// part of it.
    /// </para>
    /// </remarks>
    /// <summary>
    /// What the pointer is actually on, tested against the objects rather than against the ground.
    /// </summary>
    /// <remarks>
    /// <b>Because the ground under the cursor is not the thing the cursor is over.</b> This used to pick with
    /// <c>NodeAt</c>: raycast the terrain, then find the nearest node to where the ray met the ground. That is
    /// right for a flat marker painted on the earth and wrong for anything that stands up out of it — the ray
    /// goes through the granary's roof and lands on the ground <em>behind</em> the granary, overshooting its
    /// centre by about <c>height / tan(pitch)</c>. For a four-metre building at this camera's pitch that is
    /// 5.7 m, which is further than the six-metre pick radius was ever going to forgive: you point at the
    /// building and select nothing, or select its neighbour.
    /// <para>
    /// Reported from the chair as the mouse not lining up with the screen, and "not as deep as Vulkan — more
    /// like elevation or camera angle", which is exactly right. Two other candidates were measured and killed
    /// first: the drawn ground departs from the simulation's heightfield by <b>under two centimetres</b> on a
    /// 2 m mesh, so the mesh is not the culprit, and the raycast's 64 m ceiling is not breached either — 60 m
    /// of relief tops out at 55. Neither could have produced an error you can see.
    /// </para>
    /// <para>
    /// So the pointer is tested against each node as a box standing on the ground, and the nearest one along
    /// the ray wins. A box rather than the model: the footprint is what the simulation enforces and what the
    /// selection marker draws, so picking the same box means the thing you click is the thing that lights up.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The body under the pointer, tested against the body rather than against the ground beneath it.
    /// </summary>
    /// <remarks>
    /// Same test and the same reason as <see cref="PickNode"/>: a villager is 1.7 m tall, so the ray that
    /// passes through its chest lands on the ground well behind its feet. Note this is only the <em>hover</em>
    /// — clicking still goes through SelectionController, which projects each body to the screen and now does
    /// it at the height the body is actually standing at.
    /// </remarks>
    private AgentId? PickAgent()
    {
        if (!pointerOnTerrain) return null;
        var (width, height) = host.LogicalSize;
        if (width <= 0 || height <= 0) return null;
        var ray = camera.ScreenPointToRay(mouseX, mouseY, width, height);
        var ground = GroundHitDistance(ray);

        AgentId? best = null;
        var nearest = float.MaxValue;
        foreach (ref readonly var agent in simulation.Agents.All)
        {
            if (!agent.IsAlive || agent.Sheltered) continue;
            if (Vector2.DistanceSquared(agent.Position, pointerWorld) > NodePickSweep * NodePickSweep) continue;
            var floor = simulation.Terrain.SampleHeight(agent.Position);
            var half = MathF.Max(0.35f, agent.Radius);
            if (!RayHitsBox(ray, agent.Position, half, floor, floor + AgentDefaults.BodyHeight, out var away))
            {
                continue;
            }

            // Behind the terrain is behind the terrain. See the same test in PickNode.
            if (away > ground) continue;
            if (away >= nearest) continue;
            nearest = away;
            best = agent.Id;
        }

        return best;
    }

    private NodeId PickNode()
    {
        if (!pointerOnTerrain) return NodeId.None;
        var (width, height) = host.LogicalSize;
        if (width <= 0 || height <= 0) return NodeId.None;
        var ray = camera.ScreenPointToRay(mouseX, mouseY, width, height);
        var ground = GroundHitDistance(ray);

        var best = NodeId.None;
        var nearest = float.MaxValue;
        foreach (ref readonly var node in simulation.Nodes.All)
        {
            if (!node.IsAlive || !IsDrawnThisFrame(in node)) continue;
            // Only what is near the ray's ground hit is worth the arithmetic; the box test then decides. The
            // margin is generous because the overshoot this exists to fix is exactly what makes the ground hit
            // a poor filter.
            if (Vector2.DistanceSquared(node.Position, pointerWorld) > NodePickSweep * NodePickSweep) continue;

            var (half, tall) = PickBoxOf(node.Kind, node.HalfExtent);
            var floor = simulation.Terrain.SampleHeight(node.Position);
            if (!RayHitsBox(ray, node.Position, half, floor, floor + tall, out var away)) continue;
            // <b>The ground is opaque, and the box test did not know it.</b> A ray fired at a nearby hillside
            // carries on through it and keeps hitting boxes on the far side, so the pointer picked things it
            // was pointing <em>at the back of a hill</em> — reported from the chair as cutting through the
            // surface and selecting what the cursor cannot see. Anything the ray reaches after it has already
            // met the terrain is behind the terrain.
            if (away > ground) continue;
            if (away >= nearest) continue;
            nearest = away;
            best = node.Id;
        }

        return best;
    }

    /// <summary>
    /// The box a kind is picked by: as near as possible to the space its model actually occupies.
    /// </summary>
    /// <remarks>
    /// <b>A box taller than the model is empty air that can be clicked, and at a shallow camera angle it is
    /// clicked from a long way off.</b> Trees had 7 m of height against a drawn tree of about 4.5, on the
    /// reasoning that a generous box makes the leaves easy to hit. What it actually bought was two metres of
    /// sky above every trunk: the ray descends slowly at this pitch, so on its way to the ground under the
    /// cursor it clips that sky over a tree metres to one side and picks it. Measured from the chair: a tree
    /// picked <b>11.1 m from where the pointer met the ground</b>, with nothing under the cursor at all.
    /// <para>
    /// So each box is the model's own extent, and generosity is bought sideways rather than upward — width
    /// costs a near miss, height costs a distant false hit. The pick was never wrong about the ray; the box
    /// was wrong about the tree.
    /// </para>
    /// </remarks>
    private static (float Half, float Tall) PickBoxOf(NodeKind kind, float footprintHalf) => kind switch
    {
        // A canopy is about 2.2 m across and around 4.5 m up. Wider than the trunk the simulation routes
        // around, because what you aim at is the leaves.
        NodeKind.Tree => (1.15f, 4.4f),
        NodeKind.Outcrop => (2.0f, 2.4f),
        NodeKind.Farm => (MathF.Max(1.6f, footprintHalf), 0.6f),
        NodeKind.Pile => (1.0f, 1.0f),
        _ => (MathF.Max(1.2f, footprintHalf), MathF.Max(3.0f, footprintHalf * 1.2f)),
    };

    /// <summary>
    /// How far from the ray's ground hit a node may be and still be worth testing.
    /// </summary>
    /// <remarks>
    /// Sized from the overshoot it exists to forgive rather than picked: a thing of height <c>h</c> has its base
    /// up to <c>h / tan(pitch)</c> beyond where the ray through its top meets the ground, which for the tallest
    /// box here is about seven metres. Sixteen was twice what any box could justify, and every surplus metre is
    /// a chance to pick something the cursor is nowhere near.
    /// </remarks>
    private const float NodePickSweep = 9f;

    /// <summary>
    /// Whether this node has a model on screen, which is the only thing the pointer may find.
    /// </summary>
    /// <remarks>
    /// <b>A pointer that can pick what is not drawn is a pointer that lies.</b> Trees are culled to the detail
    /// radius, about 130 m, because a woodland is thousands of models — while the ground is drawn as far as the
    /// frustum reaches, which since the culling fix is 246 m on a hilly map. So the far third of the visible
    /// ground carries tree <em>nodes</em> with no trunks on them, and the picker found them: the panel said
    /// "tree" and the marker lit an empty patch of grass, reported from the chair as random empty spots that
    /// misalign with the cursor.
    /// <para>
    /// The cull and the pick have to be the same test, so this is the test, asked by both. Nothing else is
    /// distance-culled — outcrops are drawn map-wide because there are a few dozen of them, and buildings
    /// likewise — so this reduces to the one kind that is.
    /// </para>
    /// </remarks>
    private bool IsDrawnThisFrame(in EconomyNode node) =>
        node.Kind != NodeKind.Tree ||
        Vector2.DistanceSquared(node.Position, cameraFocus) <= treeCullBoundSquared;

    /// <summary>
    /// How far along the ray the ground is, which is as far as anything can be seen.
    /// </summary>
    /// <remarks>
    /// Derived from the terrain hit the pointer already has rather than raycast a second time: the point is
    /// known, so the distance is a projection onto the ray. A small allowance past it, because a thing standing
    /// on the ground has its base <em>at</em> the surface and floating-point equality is not a thing to bet a
    /// selection on.
    /// </remarks>
    private float GroundHitDistance(Ray ray)
    {
        var hit = new Vector3(
            pointerWorld.X,
            simulation.Terrain.SampleHeight(pointerWorld),
            pointerWorld.Y);
        return Vector3.Dot(hit - ray.Origin, ray.Direction) + 1.5f;
    }

    /// <summary>Slab test: the nearest positive distance at which a ray enters an axis-aligned box.</summary>
    private static bool RayHitsBox(
        Ray ray,
        Vector2 centre,
        float half,
        float low,
        float high,
        out float away)
    {
        away = 0f;
        var enter = 0f;
        var exit = float.MaxValue;

        // Three slabs, one per axis. A direction component of zero means the ray is parallel to that pair of
        // faces, so it either starts between them or can never be inside.
        Span<float> from = stackalloc float[] { centre.X - half, low, centre.Y - half };
        Span<float> to = stackalloc float[] { centre.X + half, high, centre.Y + half };
        Span<float> origin = stackalloc float[] { ray.Origin.X, ray.Origin.Y, ray.Origin.Z };
        Span<float> step = stackalloc float[] { ray.Direction.X, ray.Direction.Y, ray.Direction.Z };
        for (var axis = 0; axis < 3; axis++)
        {
            if (MathF.Abs(step[axis]) < 1e-6f)
            {
                if (origin[axis] < from[axis] || origin[axis] > to[axis]) return false;
                continue;
            }

            var first = (from[axis] - origin[axis]) / step[axis];
            var second = (to[axis] - origin[axis]) / step[axis];
            if (first > second) (first, second) = (second, first);
            enter = MathF.Max(enter, first);
            exit = MathF.Min(exit, second);
            if (exit < enter) return false;
        }

        away = enter;
        return true;
    }



    /// <summary>
    /// A translucent disc on the ground under something that is selected.
    /// </summary>
    /// <remarks>
    /// <b>The contact-shadow decal, borrowed, and its own docstring is the argument for using it here.</b> That
    /// pipeline exists to draw "a mark on the thing under it" rather than a thing in the world: alpha-blended,
    /// depth-tested but not depth-writing, and drawn with no culling so a disc on a slope survives a grazing
    /// camera. Every one of those is a property the outline I removed did not have — it was solid geometry
    /// standing on the ground, which is why at any thickness it read as a strip laid there rather than as a
    /// highlight belonging to the thing.
    /// <para>
    /// A circle rather than a square, matching the disc under a selected villager: the marker says "this one",
    /// and it should not be trying to also say how big the footprint is — the model already does that.
    /// </para>
    /// </remarks>
    /// <summary>
    /// A marker on the ground under something picked: round for what grew there, square for what was built.
    /// </summary>
    /// <remarks>
    /// <b>Shape says what kind of thing it is.</b> A tree, a rock and a villager occupy a patch of ground with
    /// no orientation to it, and a circle is the honest description of that. A building and a field occupy a
    /// square of the placement grid — that <em>is</em> their extent, it is what bodies route around, and a
    /// circle drawn round it either cuts the corners off or floats clear of the walls. So one shader draws both
    /// and the mesh decides which: see <see cref="SquarePlate"/> for how a square carries its own
    /// distance-to-edge in the channel a disc uses for radius.
    /// <para>
    /// <b>Leaned onto the ground rather than laid flat on it</b>, using the same shear the contact shadows use:
    /// x and z stay unit length so the footprint is exact and only y tilts. A horizontal disc on a hillside
    /// buries one edge and floats the other, which on this terrain is most of the map.
    /// </para>
    /// </remarks>
    private void DrawSelectionMark(Vector2 at, float radius, float alpha, bool square)
    {
        var ground = simulation.Terrain.SampleHeight(at);
        var normal = simulation.Terrain.SampleNormal(at);
        var lean = Matrix4x4.Identity;
        lean.M12 = -normal.X / MathF.Max(0.2f, normal.Y);
        lean.M32 = -normal.Z / MathF.Max(0.2f, normal.Y);
        var instance = new InstanceData(
            Matrix4x4.CreateScale(radius, 1f, radius) * lean *
            Matrix4x4.CreateTranslation(at.X, ground + 0.06f, at.Y),
            new Vector4(HighlightColor.X, HighlightColor.Y, HighlightColor.Z, alpha));
        if (square) selectionPlateInstances.Add(instance);
        else selectionInstances.Add(instance);
    }

    /// <summary>How see-through a selection plate is.</summary>
    /// <remarks>
    /// Enough to read as a highlight and not enough to hide what it is drawn on, which is the whole difference
    /// between a marker and a patch of paint.
    /// </remarks>
    private const float SelectionPlateAlpha = 0.95f;

    /// <summary>How see-through a hover mark is. Present, but plainly not the chosen thing.</summary>
    private const float HoverPlateAlpha = 0.55f;

    // <b>The ground outline is gone, and it was mine to try and mine to withdraw.</b> A perimeter of four
    // bars around a footprint was the wrong answer to a right question: at any thickness it read as a strip
    // laid on the earth rather than as a property of the thing it surrounded, and making it screen-constant
    // fixed the arithmetic without fixing that. What replaced it is the mechanism this game already had and
    // that already works — a body is tinted when selected, so a building is too.

    private void DrawOutcrop(in EconomyNode rock)
    {
        if (art is null || art.Rocks.Length == 0) return;
        var ground = simulation.Terrain.SampleHeight(rock.Position);
        var left = MathF.Max(0.22f, rock.Stock.Stone / MathF.Max(1f, Quarrying.StonePerOutcrop));
        // Varied by id, so the same outcrop is the same outcrop across a save.
        var spread = 0.90f + (rock.Id.Value * 29 % 11) / 11f * 0.45f;
        var width = 5.2f * spread * MathF.Cbrt(left);
        if (!InView(rock.Position, ground, width * 1.2f, width * 0.7f)) return;
        outcropsDrawn++;
        // Its own materials unless it is the thing being pointed at: the pack's stone already reads as stone,
        // and the classifier routes it through MaterialClass.Stone for the specular response.
        AddCascaded(
            art.Rocks[rock.Id.Value % art.Rocks.Length],
            SettlementArt.Placement(
                rock.Position, ground, width, SettlementArt.FreeYawOf(rock.Id.Value)));
    }

    /// <summary>The most any tree can measure across or stand, for a conservative early cull.</summary>
    /// <remarks>
    /// Above every term that scales a crown — the widest spread, a full stock, the most open canopy — so a tree
    /// this test rejects is a tree no exact test could keep. Deliberately not derived from those terms: a bound
    /// that tracked them exactly would need them computed, which is the cost this exists to avoid.
    /// </remarks>
    private const float WidestTreeMetres = 9f;

    /// <summary>Places a prop, with or without its shadow, depending on whether the light can see it.</summary>
    private void Cast(PropModel model, Matrix4x4 placement, bool casts)
    {
        if (casts) AddCascaded(model, placement);
        else model.AddUnlit(placement);
    }

    /// <summary>
    /// Which clip a body is in, and how far through it. §168.
    /// </summary>
    /// <remarks>
    /// <b>A table, because the readable states are few and the sim already knows all of them.</b> At this
    /// camera distance what a person reads is: moving or not, swinging or standing, fighting or working. Four
    /// binary reads, not ten states — so this maps the jobs layer's own vocabulary onto five clips and stops.
    /// The same shape as the bot's rule table for the same reason: a mapping you can read down the page is
    /// one you can argue with.
    /// <para>
    /// It names <see cref="BodyAction"/>s and never a clip, so which animation serves which action is data —
    /// see <see cref="CharacterClips"/>. A character from a different pipeline, naming its clips a different
    /// way, binds without a line changing here.
    /// </para>
    /// </remarks>
    /// <summary>
    /// How long an action holds before a different one may replace it.
    /// </summary>
    /// <remarks>
    /// <b>Anti-flicker, and it is the second time the same fault has been reported.</b> A pose chosen fresh
    /// every frame follows every twitch of the state behind it: a body jostled at its work crosses the
    /// walking threshold for single frames, and a construction project that runs out of materials starts
    /// and stops being worked from one tick to the next. Both read as the animation glitching rather than
    /// proceeding, because that is what a pose alternating at frame rate looks like.
    /// <para>
    /// Fixing each cause separately was the first instinct and would not have held — the next one would
    /// have been a third report. A quarter of a second is short enough that a genuine change still looks
    /// immediate and long enough that nothing alternates.
    /// </para>
    /// <para>
    /// <b>Interrupts are exempt</b>: taking a hit and dying must show at once, or the dwell would swallow
    /// the very things it matters most to see.
    /// </para>
    /// </remarks>
    private const float ActionHoldSeconds = 0.25f;

    private BodyAction[] heldAction = new BodyAction[256];
    private float[] heldUntil = new float[256];

    /// <summary>
    /// What is actually on screen for this body, as against what was last wanted for it.
    /// </summary>
    /// <remarks>
    /// <b>The reporters must print the drawn pose, not a second opinion about it.</b> §181. Both log
    /// reporters used to assemble <see cref="BodyActions.For"/>'s four arguments themselves and then reach
    /// into <see cref="heldAction"/> separately — three copies of one decision, which is the only real
    /// duplication the §179 audit found once its count was redone honestly. It had already bitten once: the
    /// log printed "wanted Build" while the renderer had chosen Idle, because one copy left the waiting flag
    /// out. The fix then was to make the third copy match. The fix now is that there is one copy.
    /// <para>
    /// <paramref name="wanted"/> is the fallback for a body the hold has never seen, which is a body whose
    /// first frame has not been drawn yet.
    /// </para>
    /// </remarks>
    private BodyAction ShownAction(int index, BodyAction wanted) =>
        index >= 0 && index < heldAction.Length ? heldAction[index] : wanted;

    /// <summary>Whether this action may cut in before the held one has finished its dwell.</summary>
    private static bool Interrupts(BodyAction action) =>
        action is BodyAction.Flinch or BodyAction.Fall;

    /// <summary>
    /// The clip to draw for this body, and — via <paramref name="drawn"/> — which action it is.
    /// </summary>
    /// <remarks>
    /// The action is reported out because §199 needs it: a strike's phase comes from the body's own swing
    /// rather than from wall time, and only this method knows which action survived the hold. Deriving it
    /// again at the call site would be a second answer to a question already settled here, which is the
    /// fault §181 spent a section removing from the two log reporters.
    /// </remarks>
    private AnimationClip? ClipFor(in AgentState agent, out bool locomotion, out BodyAction drawn)
    {
        locomotion = false;
        drawn = BodyAction.Idle;
        if (bodies is null) return null;
        var wanted = ActionFor(in agent, out locomotion);

        // Hold the previous action until its dwell is up, so nothing alternates at frame rate.
        var id = agent.Id.Value;
        if (id >= 0)
        {
            if (id >= heldAction.Length)
            {
                var grown = Math.Max(id + 1, heldAction.Length * 2);
                Array.Resize(ref heldAction, grown);
                Array.Resize(ref heldUntil, grown);
            }

            if (heldUntil[id] > 0f && !Interrupts(wanted) && wanted != heldAction[id])
            {
                wanted = heldAction[id];
                locomotion = wanted is BodyAction.Walk or BodyAction.Carry;
            }
            else if (wanted != heldAction[id] || heldUntil[id] <= 0f)
            {
                heldAction[id] = wanted;
                heldUntil[id] = ActionHoldSeconds;
            }

            heldUntil[id] = MathF.Max(0f, heldUntil[id] - frameSeconds);
        }

        drawn = wanted;
        return bodies.For(wanted);
    }

    /// <summary>What this body is doing, as far as the screen is concerned.</summary>
    /// <remarks>
    /// The decision itself is <see cref="BodyActions.For"/> — static, pure, and therefore testable without
    /// a window. All this adds is the view's own wounded flag.
    /// </remarks>
    private BodyAction ActionFor(in AgentState agent, out bool locomotion)
    {
        var id = agent.Id.Value;
        var hurt = id >= 0 && id < hurtUntil.Length && hurtUntil[id] > 0f;
        var builder = BodyActions.ForBuilder(
            in agent, AtItsProject(in agent), simulation.ProjectCanBeWorked(agent.Jobs.Project));
        return BodyActions.For(
            in agent, hurt, WaitingOnMaterials(in agent), builder, out locomotion);
    }

    /// <summary>
    /// How long a body keeps flinching after it is hurt, and the health it was last seen at.
    /// </summary>
    /// <remarks>
    /// <b>View state, deliberately.</b> A flinch is a fact about the screen and not about the world, so
    /// putting a "was hurt recently" timer in the simulation would add a field the determinism census and
    /// every save would have to carry, for something no rule reads. Watching health fall from out here
    /// costs one float per body and is never fingerprinted.
    /// </remarks>
    private const float FlinchSeconds = 0.45f;

    private float[] hurtUntil = new float[256];
    private float[] lastHealth = new float[256];

    /// <summary>
    /// Whether this body is standing at the thing it is building.
    /// </summary>
    /// <remarks>
    /// Geometry rather than state, deliberately: a builder walking to a store for timber is not at its
    /// project and should read as walking, and a builder beside the half-built wall is at it whether or not
    /// the jobs layer has an activity open this tick. The distance is to the PROJECT, not to
    /// <c>Jobs.Place</c> — on the fetching leg the place is the store, and a body standing at a granary is
    /// not building anything.
    /// </remarks>
    private bool AtItsProject(in AgentState agent)
    {
        var project = agent.Jobs.Project;
        if (!simulation.Nodes.Contains(project)) return false;
        ref readonly var node = ref simulation.Nodes.Get(project);
        var reach = node.FootprintRadius + agent.Radius * 2f + 1f;
        return Vector2.DistanceSquared(agent.Position, node.Position) <= reach * reach;
    }

    /// <summary>
    /// Whether this body's work cannot proceed for want of materials on the project.
    /// </summary>
    /// <remarks>
    /// The stable half of a state the jobs layer keeps changing its mind about. A builder standing at a
    /// site that still wants timber, with none in its own hands, is waiting — and it stays waiting until a
    /// delivery arrives, which is a fact that changes on the scale of a walk rather than of a tick.
    /// </remarks>
    private bool WaitingOnMaterials(in AgentState agent)
    {
        if (agent.Jobs.Assignment.Kind != AssignmentKind.Build) return false;
        if (agent.Jobs.CarriedUnits > 0) return false;
        var project = agent.Jobs.Project;
        if (!simulation.Nodes.Contains(project)) return false;
        ref readonly var node = ref simulation.Nodes.Get(project);
        return node.WantsMaterials;
    }

    /// <summary>The action each body was last reported at, so a change can be logged as it happens.</summary>
    private BodyAction[] loggedAction = new BodyAction[256];

    /// <summary>
    /// How long a spell of not getting anywhere lasts, over the run as played. §183.
    /// </summary>
    /// <remarks>
    /// Not fingerprinted, not saved, never read by a rule — an instrument, like the flinch timer beside it.
    /// It exists because the stall population the chair reported has only ever been watched: the per-frame
    /// stuck lines say which bodies are red right now, and no accumulated figure said whether any of them
    /// ever got where it was going.
    /// </remarks>
    private readonly StallCensus stalls = new();

    private const float StallCensusInterval = 30f;
    private float stallCensusDue = StallCensusInterval;

    /// <summary>
    /// Logs the instant a selected body's action changes.
    /// </summary>
    /// <remarks>
    /// <b>A snapshot once a second cannot see a glitch.</b> "Glitching" means a pose alternating faster than
    /// the eye can separate, and sampling at 1 Hz shows one of the two states and no hint that there are
    /// two. Logging on change makes the frequency the thing you read: a burst of lines a few milliseconds
    /// apart IS the glitch, and a state that genuinely changed once prints once.
    /// </remarks>
    private void ReportActionChanges()
    {
        foreach (var id in selection.Snapshot())
        {
            if (!simulation.Agents.Contains(id)) continue;
            var index = id.Value;
            if (index < 0) continue;
            if (index >= loggedAction.Length)
            {
                Array.Resize(ref loggedAction, Math.Max(index + 1, loggedAction.Length * 2));
            }

            ref readonly var agent = ref simulation.Agents.Get(id);
            // The one decision the draw makes, not a rebuilt copy of it — and then what the hold is
            // actually showing, which is the pair worth printing: a wanted pose that keeps losing to the
            // hold is a different fault from a pose that genuinely alternates.
            var wanted = ActionFor(in agent, out _);
            var shown = ShownAction(index, wanted);
            if (shown == loggedAction[index]) continue;

            loggedAction[index] = shown;
            Console.WriteLine(
                $"  [flip] #{index} -> {shown}" +
                (wanted != shown ? $" (wanted {wanted})" : string.Empty) +
                $" at {simulation.EpochTicks * SimulationWorld.FixedDeltaSeconds:F2}s" +
                $" | {agent.Jobs.Assignment.Kind}/{agent.Jobs.Assignment.Cargo}" +
                $" act={agent.Jobs.Activity}" +
                (JobSystem.IsWorking(in agent) ? " underway" : " en route") +
                $" waiting={WaitingOnMaterials(in agent)}" +
                $" toPlace={JobSystem.DistanceToPlace(in agent):F2} speed={agent.Velocity.Length():F2}");
        }
    }

    /// <summary>
    /// How long a body must be making no progress before the log calls it stuck.
    /// </summary>
    /// <remarks>
    /// The same figure the movement overlay uses to paint a body red, held apart from it rather than read
    /// from the collision layer: an instrument that depends on a constant belonging to the thing it measures
    /// cannot be used to compare two versions of that thing, which is exactly what it was needed for.
    /// <para>
    /// §182 briefly pointed this at the simulation's own <c>StalledSeconds</c> while consolidating thirteen
    /// copies of the number, which would have quietly broken every paired A/B run. It points at the pinned
    /// instrument constant instead, which is where the argument above always belonged.
    /// </para>
    /// </remarks>
    private const float RedStuckSeconds = StallReporting.StalledSeconds;

    /// <summary>
    /// Reports any body the movement layer has given up on, whether or not it is selected.
    /// </summary>
    /// <remarks>
    /// <b>Because the bodies that go wrong are the ones nobody is holding.</b> The per-selection log is
    /// useless in a hands-off run: both villages are bot-driven, nothing is selected, and the log stays
    /// silent while the overlay counts red bodies. So the ones actually in trouble report themselves.
    /// <para>
    /// And it prints the distinction that decides the fix, which I had been conflating: <c>StuckSeconds</c>
    /// is lack of PROGRESS, not overlap. A body can be red with nothing touching it — an unreachable
    /// destination, a blocked path, an oscillation — and that wants a routing fix, while a body red because
    /// it is wedged wants a separation fix. <c>contact</c> and <c>toPlace</c> and <c>retries</c> separate
    /// them at a glance.
    /// </para>
    /// </remarks>
    private void ReportStuckBodies()
    {
        var reported = 0;
        foreach (ref readonly var agent in simulation.Agents.All)
        {
            if (!agent.IsAlive || agent.StuckSeconds <= RedStuckSeconds) continue;
            if (reported++ >= 4) break;

            var jobs = agent.Jobs;
            Console.WriteLine(
                $"  [stuck] #{agent.Id.Value} {agent.Role} f{agent.Faction.Value} " +
                $"stuck={agent.StuckSeconds:F2}s contact={agent.HadAgentContactThisTick} " +
                // <b>Named at the leg's start, so it is the leg's purpose and not the act underway.</b>
                // §183: a stuck line read "activity=Building" with "toPlace=18.18" — a body eighteen metres
                // from its site, printed as though it were building. The pose is unaffected, because that
                // gates on arrival, but a log line that invites the misreading is the same near-synonym
                // fault this session has paid for four times. So the line says which of the two it is.
                $"| {jobs.Assignment.Kind}/{jobs.Assignment.Cargo} leg{jobs.Leg} " +
                $"act={jobs.Activity}" +
                (JobSystem.IsWorking(in agent) ? " underway" : " en route") +
                $" interrupt={jobs.Interrupt} " +
                // <b>The fields that separate the candidates.</b> §186: three villagers spent a whole run
                // under an Order interrupt inside the enemy's village, and the log could not say which of
                // three things had ordered them there — the threat system marching them, a stow errand
                // re-issuing a move every time it lost its destination, or the bot. Each leaves a different
                // trace, and none of those traces was printed. What a body is carrying, whether it is on a
                // putting-down errand and where to, and what its own locomotion thinks it is doing.
                $"| carry={jobs.CarriedUnits}/{agent.CarryCapacity} of {jobs.Carrying} " +
                $"stowing={agent.PuttingDown}" +
                (agent.PuttingDown ? $"->{agent.StowInto.Value}" : string.Empty) +
                $" locomotion={agent.LocomotionState} " +
                $"| at=({agent.Position.X:F1},{agent.Position.Y:F1}) " +
                $"want=({agent.RequestedDestination.X:F1},{agent.RequestedDestination.Y:F1}) " +
                $"hasDest={agent.HasDestination} toPlace={JobSystem.DistanceToPlace(in agent):F2} " +
                $"retries={jobs.Retries} settled={jobs.SettledNearby} " +
                $"speed={agent.Velocity.Length():F2} yielding={agent.IsVisiblyYielding}");
        }
    }

    /// <summary>One line per selected body: what it is doing and why.</summary>
    private void ReportSelectedBodies()
    {
        if (selection.Selected.Count == 0) return;
        Console.WriteLine($"  [bodies] {simulation.Date}");
        foreach (var id in selection.Snapshot())
        {
            if (!simulation.Agents.Contains(id)) continue;
            ref readonly var agent = ref simulation.Agents.Get(id);
            var action = ActionFor(in agent, out _);
            var clip = bodies?.For(action);
            var jobs = agent.Jobs;
            var place = jobs.Place;
            var gap = Vector2.Distance(agent.Position, place);
            var held = ShownAction(id.Value, action);

            Console.WriteLine(
                $"    #{id.Value} {agent.Role} {action}" +
                (held != action ? $" (held {held})" : string.Empty) +
                $" clip={clip?.Name.Split('|')[^1] ?? "none"}" +
                $" | {jobs.Assignment.Kind}/{jobs.Assignment.Cargo} leg{jobs.Leg}" +
                $" act={jobs.Activity}{(JobSystem.IsWorking(in agent) ? " underway" : " en route")}" +
                $" waiting={WaitingOnMaterials(in agent)} interrupt={jobs.Interrupt}" +
                $" | place=({place.X:F1},{place.Y:F1}) gap={gap:F2} extent={jobs.PlaceExtent:F2}" +
                $" toPlace={JobSystem.DistanceToPlace(in agent):F2} settled={jobs.SettledNearby}" +
                $" retries={jobs.Retries}" +
                $" | speed={agent.Velocity.Length():F2} carry={jobs.CarriedUnits}/{agent.CarryCapacity}" +
                $" stuck={agent.StuckSeconds:F1}");
        }
    }

    /// <summary>Notices which bodies lost health since the last frame. Call once per frame.</summary>
    private void WatchForHurt(float deltaSeconds)
    {
        foreach (ref readonly var agent in simulation.Agents.All)
        {
            var id = agent.Id.Value;
            if (id < 0) continue;
            if (id >= hurtUntil.Length)
            {
                var grown = Math.Max(id + 1, hurtUntil.Length * 2);
                Array.Resize(ref hurtUntil, grown);
                Array.Resize(ref lastHealth, grown);
            }

            if (hurtUntil[id] > 0f) hurtUntil[id] = MathF.Max(0f, hurtUntil[id] - deltaSeconds);
            if (!agent.IsAlive)
            {
                lastHealth[id] = 0f;
                continue;
            }

            // A body that has just appeared has no history, so its first frame is not a wound.
            if (lastHealth[id] > 0f && agent.Health < lastHealth[id] - 0.01f)
            {
                hurtUntil[id] = FlinchSeconds;
            }

            lastHealth[id] = agent.Health;
        }
    }

    /// <summary>
    /// How far a body's gait advances per metre walked, so feet never skate.
    /// </summary>
    /// <remarks>
    /// <b>Driven by distance, not by the clock, and that is the whole point of it.</b> A clock-driven walk
    /// cycle plays at one rate whatever the body is doing, so a villager slowed by a crowd or by a full load
    /// keeps striding at full pace and slides — which is the complaint that started this: people who look
    /// like they are floating. Advancing the phase by metres covered means a laden body visibly trudges,
    /// §167's speed penalty becomes something you can see, and a body stopped dead stops moving its legs.
    /// <para>
    /// <b>A fallback now, not the figure in use.</b> SkinnedBodies measures the real stride out of the walk
    /// clip — a foot's fore-and-aft swing relative to the hips — and this stands in only for an asset with
    /// no walk clip or no foot bone to measure.
    /// </para>
    /// </remarks>
    private const float GaitMetresPerCycle = 1.5f;

    /// <summary>The heading each body is drawn at, turned towards its target rather than snapped to it.</summary>
    /// <remarks>
    /// <b>View state, and the reason a body stops whipping round on arrival.</b> A walking body faces its
    /// velocity and a working body faces its work, and approaching a trunk from the far side puts half a
    /// turn between those two answers — so the frame it stopped, it spun. Turning at a finite rate makes
    /// the disagreement into a movement instead of a cut, and costs one float per body that no rule reads.
    /// </remarks>
    private float[] drawnYaw = new float[256];
    private bool[] hasDrawnYaw = new bool[256];

    /// <summary>Moves this body's drawn heading towards <paramref name="target"/> at the turn rate.</summary>
    private float TurnedTowards(int id, float target)
    {
        if (id < 0) return target;
        if (id >= drawnYaw.Length)
        {
            var grown = Math.Max(id + 1, drawnYaw.Length * 2);
            Array.Resize(ref drawnYaw, grown);
            Array.Resize(ref hasDrawnYaw, grown);
        }

        // A body seen for the first time faces where it should, rather than turning from an invented angle.
        if (!hasDrawnYaw[id])
        {
            hasDrawnYaw[id] = true;
            drawnYaw[id] = target;
            return target;
        }

        // Shortest way round, so a turn through north does not go the long way.
        var delta = MathF.IEEERemainder(target - drawnYaw[id], MathF.Tau);
        var step = bodyFeel.TurnDegreesPerSecond * MathF.PI / 180f * frameSeconds;
        drawnYaw[id] += MathF.Abs(delta) <= step ? delta : MathF.Sign(delta) * step;
        return drawnYaw[id];
    }

    /// <summary>Per-body gait phase in seconds, indexed by agent id. View state; never fingerprinted.</summary>
    private float[] gaitPhase = new float[256];

    /// <summary>Advances one body's gait and returns where in its clip to sample.</summary>
    /// <remarks>
    /// Three clocks, and which one a body is on is the whole of this method. A walk runs on <b>distance</b>,
    /// so feet do not scuff. A strike runs on <b>the swing</b>, so the blow and the picture are the same
    /// event. Everything else runs on wall time with a per-body offset, so a crowd does not breathe in
    /// unison.
    /// </remarks>
    private double GaitOf(
        in AgentState agent, AnimationClip clip, bool locomotion, float deltaSeconds,
        BodyAction action = BodyAction.Idle)
    {
        var id = agent.Id.Value;
        if (id < 0) return 0.0;
        if (id >= gaitPhase.Length) Array.Resize(ref gaitPhase, Math.Max(id + 1, gaitPhase.Length * 2));

        // <b>A strike is on the swing's own clock.</b> §199. §198 gave harm a moment, so the clip can be
        // placed rather than left to run: the phase comes from how far through its swing the body is, and
        // the measured impact frame is subtracted so that the frame where the hand moves fastest arrives
        // exactly when the harm does. The previous blow's follow-through and the next one's wind-up fill
        // the rest of the cycle, which is what a sequence of blows looks like.
        //
        // Only when the impact was actually measurable off the rig — see SkinnedBodies.ImpactFraction. If
        // it was not, the clip runs free, because a deliberate-looking sync that is off by four tenths of a
        // second reads as a bug where a free-running clip reads as noise.
        if (action == BodyAction.Strike && bodies is { ImpactFraction: > 0f } rig)
        {
            var period = MathF.Max(0.0001f, Simulation.Threat.ThreatSystem.SwingSeconds);
            var through = Math.Clamp(agent.ActCharge / period, 0f, 1f);
            var span = MathF.Max(0.0001f, (float)clip.Duration);
            return ((through + rig.ImpactFraction) % 1f) * span;
        }

        if (locomotion)
        {
            // <b>The asset's own stride if it has one, the constant only if it does not.</b> A guessed
            // stride is what makes feet scuff — too short and they gabble, too long and they slide — and
            // SkinnedBodies recovers the real figure from the walk clip, so the guess is now a fallback
            // rather than the number in use.
            var stride = bodies is { StridePerCycle: > 0.01f } ? bodies.StridePerCycle : GaitMetresPerCycle;
            var metres = agent.Velocity.Length() * deltaSeconds;
            gaitPhase[id] += (float)(metres / stride * clip.Duration);
        }
        else
        {
            gaitPhase[id] += deltaSeconds;
        }

        var duration = (float)MathF.Max(0.0001f, (float)clip.Duration);
        // Offset by id so a crowd does not breathe in unison, which reads as a rank of clones.
        var offset = (id * 0.37f) % duration;
        gaitPhase[id] %= duration;
        return (gaitPhase[id] + offset) % duration;
    }

    private void AddCascaded(PropModel model, Matrix4x4 placement)
    {
        model.Add(placement, CascadeMaskAt(new Vector2(placement.M41, placement.M43)));
    }

    private void AddCascaded(PropModel model, Matrix4x4 placement, Vector4 tint)
    {
        model.Add(placement, tint, CascadeMaskAt(new Vector2(placement.M41, placement.M43)));
    }

    private void DrawTree(in EconomyNode tree)
    {
        var ground = simulation.Terrain.SampleHeight(tree.Position);
        var left = MathF.Max(0.12f, tree.Stock.Wood / MathF.Max(1f, Woodland.WoodPerTree));
        // Varied by id rather than by a random draw, so the same tree is the same tree across a save.
        var spread = 0.86f + (tree.Id.Value * 37 % 13) / 13f * 0.40f;
        if (art is not null)
        {
            // A tree is drawn wider than the trunk it is routed around, because a canopy overhangs and
            // nothing walks into a canopy. Its footprint in the simulation is the trunk.
            // <b>An open tree has a wide crown and a forest tree has a narrow one, and that is not decoration.</b>
            // A crown grows into whatever light it can reach: standing alone it spreads, and in a closed stand
            // it is squeezed by its neighbours into a column. Which means the same amount of woodland reads
            // completely differently at the two ends — scattered parkland of broad crowns against a wall of
            // narrow ones — and until now density changed only how many trees there were, so more woodland was
            // more copies of the same tree.
            //
            // A fifth either side. Small enough that no single tree looks wrong and large enough that a
            // hillside of them does not look stamped.
            // <b>Culled before anything is computed about it, on the largest a tree can be.</b> The frustum
            // test used to come after the crown width, which needs the woodland field — so every tree the
            // frustum was about to reject still cost a WoodlandCover lookup first. Now that the frustum is the
            // only thing deciding visibility, that would have been every tree within the coarse bound, on every
            // frame.
            //
            // Conservative on purpose: a fixed generous extent, so the early test can only ever reject a tree
            // the exact test would also have rejected. A cheap conservative cull followed by an exact one is
            // the whole trick, and getting it backwards is what made the ordering matter.
            if (!look.DrawEveryTree &&
                !InView(tree.Position, ground, WidestTreeMetres, WidestTreeMetres * 0.5f))
            {
                treesOutOfView++;
                // <b>Of the trees the frustum throws away, how many were right next to the camera.</b>
                // "Trees near the camera vanish as I zoom in" is a claim about this number and nothing else:
                // the distance bound is 86 m at the closest zoom and the view is 43 m deep, so the bound cannot
                // be the culprit and the frustum test is the only thing left that can refuse a near tree.
                var eye = camera.Transform.Position;
                if (Vector2.DistanceSquared(tree.Position, new Vector2(eye.X, eye.Z)) < 20f * 20f)
                {
                    treesNearRejected++;
                }

                return;
            }
            // Ablation level 2 stops here: iteration, one height sample and the frustum test. See treeWork.
            if (treeWork >= 2)
            {
                treesDrawn++;
                return;
            }

            var canopy = simulation.Terrain.Woodland is { } cover
                ? 1.22f - 0.34f * Math.Clamp(cover.At(tree.Position), 0f, 1.2f)
                : 1f;
            var width = NodeFootprint.TreeHalfExtent * 2f * 3.1f * spread * MathF.Sqrt(left) * canopy;
            // <b>Outside the sun's box a tree is drawn but casts nothing.</b> The box is 2 x DetailRadius
            // across and centred on the focus, so anything past that radius rasterises into a shadow map it
            // cannot appear in. Measured at maximum zoom on a wooded map: casters were 8.2M of 18.8M triangles
            // submitted, and the trees between the box's edge and the draw bound owned most of it.
            //
            // No visual cost by construction — a shadow that lands outside the map was never on screen. This is
            // the same mistake as the instance upload earlier today, in a different currency: work whose result
            // has nowhere to go.
            var casts = CastsIntoAnyCascade(tree.Position);
            var kind = TreeKindAt(tree.Position, tree.Id.Value);
            var placement = SettlementArt.Placement(
                tree.Position, ground, width, SettlementArt.FreeYawOf(tree.Id.Value));

            // <b>The same tree at three levels of detail, chosen by how crowded it is and by nothing
            // else.</b> Our foliage is solid low-poly geometry rather than alpha-cut cards, so it decimates
            // — 5,940 / 1,041 / 603 triangles for a species — and a decimated tree still reads as a tree
            // where an impostor reads as a shard. What it does not keep is <em>mass</em>: the coarse levels
            // shed interior leaf cards, so a stand of them reads thinner than the stand it replaced.
            //
            // <b>Which is why distance was taken out of this decision entirely.</b> Coarsening by distance
            // thins whole regions of the map at once, and a distant wood going sparse as you pull back is
            // the one artefact of it a player cannot help seeing — the far half of the valley looks logged.
            // Crowding does not have that failure: where trees overlap, the neighbours fill in the mass the
            // coarse level lost, so the thinning lands exactly where it is covered. A tree standing on its
            // own is looked at and keeps every triangle at any distance; a tree in a thicket is texture.
            // It also spends the detail where a settlement is, since the ground round a village is cleared.
            //
            // The far limit is <c>treeCullBoundSquared</c>, and it stays a limit rather than a ladder:
            // beyond what the camera can see, and soon beyond what the player has scouted.
            // <b>Two tiers, and they answer different questions.</b> The natural one is what the scheme says
            // this tree's crowding deserves; the drawn one is that shifted by the measurement bias. They were
            // one variable, which made --perf-tier-bias silently take the undergrowth and the contact shadows
            // away with the geometry — so the geometry lever could not be looked at without also previewing a
            // world with no ground cover, and the look question it was meant to inform could not be asked.
            var tier = CanopyTierAt(tree.Position);
            var drawTier = Math.Min(3, tier + performanceTierBias);

            // Ablation level 1 stops here: everything computed about a tree, nothing submitted. See treeWork.
            if (treeWork >= 1)
            {
                treesDrawn++;
                return;
            }

            // The dressing goes with what the tree IS, not with which mesh was picked for it: a tree the
            // scheme calls near-field has undergrowth at its foot and a shadow under its canopy whatever
            // geometry the run chose to draw it with.
            if (tier == 0)
            {
                // Sized to the canopy rather than the trunk, because what shades the ground is the canopy.
                AddContactShadow(tree.Position, width * 0.42f, 0.55f);
                var underStart = timingDebug ? Stopwatch.GetTimestamp() : 0L;
                DrawUndergrowth(in tree, left);
                if (timingDebug) undergrowthTicks += Stopwatch.GetTimestamp() - underStart;
            }

            switch (drawTier)
            {
                case 0:
                    Cast(art.Trees[kind], placement, casts);
                    treeTiers.Near++;
                    break;
                case 1:
                    Cast(art.TreesMid[kind], placement, casts);
                    treeTiers.Mid++;
                    break;
                case 2:
                    Cast(art.TreesFar[kind], placement, casts);
                    treeTiers.Far++;
                    break;
                default:
                    Cast(art.TreesDeep[kind], placement, casts);
                    treeTiers.Deep++;
                    break;
            }

            treesDrawn++;
            return;
        }

        if (art is not null)
        {
            // <b>Far trees are a trunk and a twenty-triangle blob, and the shape matters as much as the
            // count.</b> The first version was a cross of quads at eight triangles, which is cheaper still
            // and was reported as a "tree magnifying glass": two flat planes seen from above are a shard,
            // and a wood of them reads as broken glass rather than as canopy. A faceted blob on a stick is
            // thirty-two triangles and reads as a tree, which is the whole job — at this distance nobody is
            // counting leaves, they are reading a silhouette and a mass.
            //
            // Decimation is not an option here and it is worth writing down why: a tree model is a few
            // thousand disconnected leaf cards, so an edge-collapse simplifier has nothing to collapse.
            // Measured through the cook tool, a 4,345-triangle tree reduced to 3,975 and stopped. Distant
            // foliage needs a substitute, not a reduction — which is what the cook tool means by leaving
            // foliage whole "for the impostor track to own".
        }

        var trunkHeight = (2.2f + spread * 1.5f) * MathF.Sqrt(left);
        var trunkWidth = NodeFootprint.TreeHalfExtent * 0.62f;
        unitInstances.Add(new InstanceData(
            Matrix4x4.CreateScale(trunkWidth, trunkHeight, trunkWidth) *
            Matrix4x4.CreateTranslation(tree.Position.X, ground + trunkHeight * 0.5f, tree.Position.Y),
            TrunkColor));
        var canopyRadius = NodeFootprint.TreeHalfExtent * (2.4f + spread * 0.9f) * MathF.Sqrt(left);
        var canopyHeight = canopyRadius * 1.35f;
        canopyInstances.Add(new InstanceData(
            Matrix4x4.CreateScale(canopyRadius * 2f, canopyHeight, canopyRadius * 2f) *
            Matrix4x4.CreateTranslation(
                tree.Position.X, ground + trunkHeight + canopyHeight * 0.28f, tree.Position.Y),
            Vector4.Lerp(CanopyDarkColor, CanopyLightColor, (tree.Id.Value * 29 % 7) / 7f)));
    }

    /// <summary>
    /// Scrub round the foot of a tree, which is what stops a trunk looking pushed into the floor.
    /// </summary>
    /// <remarks>
    /// <b>Reported as too flat and straight up where the trunk meets the ground</b>, with the ask being for
    /// something to blend the two. Undergrowth is that something, and it is better than a decal: a trunk
    /// meeting bare ground at ninety degrees looks wrong because in a real wood it never does — there is
    /// always litter and scrub in the way, and the eye is not looking for a soft gradient so much as for the
    /// mess that hides the join.
    /// <para>
    /// Which is also the second half of the ask — denser shrubs under trees, to fill a forest out — so one
    /// mechanism does both, with no alpha blending, no decal pass and no new asset. Two or three squashed
    /// bushes per trunk, placed from the tree's own id so they never move and survive a save.
    /// </para>
    /// <para>
    /// Scaled by how much of the tree is left, so a trunk being cut down takes its undergrowth with it
    /// rather than leaving a ring of bushes round nothing.
    /// </para>
    /// </remarks>
    /// <summary>How far out undergrowth is drawn, and how much of it, per frame.</summary>
    /// <remarks>
    /// <b>Both learnt by overflowing the instance ceiling: 17,439 against a limit of 16,384.</b> The forest
    /// is scattered at 1.6 m now, so the detail radius holds thousands of trunks, and two or three bushes
    /// each is more instances than one batch can hold — the primitive is right to refuse rather than to
    /// quietly drop them.
    /// <para>
    /// Thirty metres, because this is a close-up effect: it exists to hide the join where a trunk meets the
    /// ground, and at seventy metres there is no join to see. And a hard budget on top, so a walk into the
    /// thickest part of a wood cannot find a density the batch cannot hold — the far trees lose their skirts
    /// first, which is exactly the right thing to lose.
    /// </para>
    /// </remarks>
    private const float UndergrowthMetres = 30f;

    private const int UndergrowthBudget = 2400;

    private int undergrowthDrawn;

    private void DrawUndergrowth(in EconomyNode tree, float left)
    {
        if (art is null || art.Undergrowth.Length == 0) return;
        if (undergrowthDrawn >= UndergrowthBudget) return;
        if (Vector2.DistanceSquared(tree.Position, cameraFocus) >
            UndergrowthMetres * UndergrowthMetres)
        {
            return;
        }

        // <b>Undergrowth peaks at a wood's edge, not in its middle.</b> Which is the opposite of the obvious
        // rule — more trees, more scrub — and it is what a wood actually looks like: a closed canopy shades its
        // own floor bare, while the margin gets light from the side and chokes. So a wood ends up with a thick
        // edge and a walkable interior, which is a far better read than uniform scrub, and it is also the thing
        // that makes the edge legible from outside as an edge.
        //
        // A triangle on pressure, peaking around the half-closed mark. Open ground gets almost none, because a
        // lone tree in a field has grass under it rather than bramble.
        var pressure = simulation.Terrain.Woodland?.At(tree.Position) ?? 0.6f;
        var edge = 1f - MathF.Abs(Math.Clamp(pressure, 0f, 1.2f) - 0.55f) / 0.65f;
        if (edge <= 0.08f) return;

        var id = tree.Id.Value;
        var clumps = 1 + (int)MathF.Round(edge * (1f + id * 31 % 2));
        for (var i = 0; i < clumps; i++)
        {
            var angle = (id * 47 + i * 137) % 360 * MathF.PI / 180f;
            // Just outside the trunk, so a bush sits against it rather than inside it.
            var reach = NodeFootprint.TreeHalfExtent * (1.1f + (id * 13 + i * 7) % 9 / 9f * 0.9f);
            var at = tree.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * reach;
            var width = (0.55f + (id * 19 + i * 23) % 11 / 11f * 0.55f) * MathF.Sqrt(left);
            art.Undergrowth[(id + i) % art.Undergrowth.Length].Add(SettlementArt.Placement(
                at,
                simulation.Terrain.SampleHeight(at),
                width,
                SettlementArt.FreeYawOf(id * 7 + i)));
            undergrowthDrawn++;
        }
    }

    /// <summary>
    /// Low scrub, generated from the ground rather than stored anywhere.
    /// </summary>
    /// <remarks>
    /// <b>Derived, not authored, and that is what makes it free.</b> A scatter dense enough to matter is
    /// thousands of objects; storing them would be thousands of nodes in the fingerprint, the save file and
    /// every iteration over the economy, for things that decide nothing. Instead the visible area is walked
    /// as a grid and each cell hashes its own coordinates into "is there a stone here, which one, where in
    /// the cell, and how big" — so the same cell always answers the same way, nothing is stored, nothing is
    /// saved, and a scatter appears and disappears with the camera at no cost but the draw.
    /// <para>
    /// Kept off worked ground for the reason §22 gave about trees: a bush in a wheat field is not a
    /// collision, it is a lie about what that ground is being used for. Placement occupancy answers that for
    /// buildings and fields alike, and the check is one grid lookup.
    /// </para>
    /// <para>
    /// <b>Scrub rather than stones</b>, and see <c>SettlementArt</c> for why: stone is about to be something
    /// you mine, and scattering it as decoration teaches the player that rocks are nothing to look at.
    /// </para>
    /// </remarks>
    private void DrawScatter()
    {
        if (art is null || art.Scatter.Length < 4) return;
        // <b>Ground cover is drawn where it can be seen and not to the horizon.</b> §52 called it the most
        // numerous thing in the scene and the least missed, and the numbers agree: the candidate grid is a
        // square of the radius over the spacing, so it grows as the square — at a two hundred and forty
        // metre standoff that is forty thousand cells walked to decide the fate of a tuft of grass, ten
        // milliseconds of it, for cover that is well under a pixel at the far end.
        //
        // A hundred and ten metres is past anything a player is looking at when they can see individual
        // plants at all, and it takes the grid to about eight thousand cells. The trees keep the full detail
        // radius, because a tree at two hundred metres is still a tree.
        // <b>Bounded by a cell budget, which is what the old constant was measuring without saying so.</b>
        // A hundred and ten metres was justified as "it takes the grid to about eight thousand cells" — a
        // statement about how much work this loop does, not about how far a tuft of grass can be seen. Turning
        // it into a share of the view was the wrong kind of substitution: it would have grown to 140 m at the
        // widest zoom, where the old number was deliberately a ceiling.
        //
        // So the share decides it near in, and the budget decides it far out, and the budget is stated in the
        // units it always meant. Eight thousand cells at 2.4 m is 107 m, which is the number it replaces.
        var budgeted = MathF.Sqrt(CoverCellBudget) * ScatterSpacingMetres * 0.5f;
        var radius = MathF.Min(MathF.Min(MathF.Sqrt(treeCullBoundSquared), Sees * ScatterShare), budgeted);
        // Tighter than the trees, because a tuft of grass is a few centimetres and stops being a tuft well
        // before a trunk stops being a trunk.
        const float spacing = ScatterSpacingMetres;
        var cells = (int)MathF.Ceiling(radius / spacing);
        var originX = MathF.Floor(cameraFocus.X / spacing);
        var originZ = MathF.Floor(cameraFocus.Y / spacing);
        var cacheValid = scatterWorld == simulation &&
                         scatterTerrainRevision == simulation.Terrain.Revision &&
                         scatterPlacementRevision == simulation.Placement.Revision &&
                         scatterFocus == cameraFocus &&
                         scatterRadius == radius;
        if (cacheValid)
        {
            foreach (var (kind, model) in scatterPlacements) art.Scatter[kind].Add(model);
            return;
        }

        scatterPlacements.Clear();
        scatterWorld = simulation;
        scatterTerrainRevision = simulation.Terrain.Revision;
        scatterPlacementRevision = simulation.Placement.Revision;
        scatterFocus = cameraFocus;
        scatterRadius = radius;
        var placed = 0;
        for (var dz = -cells; dz <= cells; dz++)
        for (var dx = -cells; dx <= cells; dx++)
        {
            if (placed >= ScatterBudget) goto Replay;
            var cx = (int)originX + dx;
            var cz = (int)originZ + dz;

            var at = new Vector2(
                (cx + 0.15f + ScatterHash(cx, cz, 1) * 0.7f) * spacing,
                (cz + 0.15f + ScatterHash(cx, cz, 2) * 0.7f) * spacing);
            if (Vector2.DistanceSquared(at, cameraFocus) > radius * radius) continue;

            // <b>Patches, not a per-cell coin toss.</b> A uniform probability spreads vegetation evenly at
            // whatever rate it is given, and evenly is the one thing ground cover never is — it grows in
            // runs and drifts, thick here and bare a few metres away. Two octaves of lattice noise give
            // that for nothing: the coarse one decides where a patch is at all and the finer one varies
            // its density inside itself, so a patch has a length and an edge that nobody authored.
            // <b>Cheapest and most selective first, and the order is worth a good deal.</b> Ground cover
            // walks forty thousand candidate cells at a wide zoom and places a few thousand, so every test
            // done before the one that rejects nine cells in ten is done nine times more often than it needs
            // to be. The patch noise is both the cheapest and the most selective, so it goes first, and the
            // terrain bounds, the occupancy lookup and the country field all move behind it.
            var patch = PatchDensity(at);
            if (patch <= 0.05f) continue;
            if (!simulation.Terrain.Contains(at)) continue;
            if (simulation.TryGetPlacementCell(at, out var cell) &&
                simulation.Placement.IsOccupied(cell))
            {
                continue;
            }

            patch *= CoverAt(at, out var hollow);
            if (patch <= 0.04f) continue;

            // Denser under trees, which the terrain already knows: forest cover marks every cell with two
            // trees crowding it, so "am I in a wood" is a lookup rather than a spatial query.
            var wooded = simulation.Terrain.Contains(at) &&
                         simulation.Terrain.SampleSurface(at) == TerrainSurface.Forest;

            // <b>Tall where the water goes.</b> A hollow collects moisture and a shoulder sheds it, so the
            // same signal that thins the cover on a slope decides what kind grows where it is thick. It
            // stacks with the canopy rather than replacing it: rank growth is under trees <em>and</em> in
            // the bottoms, which between them is where a person walking would actually find it.
            var rank = Math.Clamp((wooded ? 0.95f : 0.44f) + hollow * 0.30f, 0.1f, 1.2f);
            var basal = Math.Clamp((wooded ? 0.72f : 0.34f) - hollow * 0.10f, 0.05f, 1f);

            // <b>Which country this is, which decides what grows here.</b> Two grass species rather than
            // one is most of the read: common grass is pasture, wispy grass is moor and poor ground, and a
            // map where those two swap over as the land rises is a map with regions in it.
            var biome = CountryAt(at);
            var (shortGrass, tallGrass, third) = biome switch
            {
                // Wiry stuff, and the tall form is what stands up on an exposed top.
                Biome.Moor => (ScatterWispyShort, ScatterWispyTall, ScatterWispyTall),
                // Nothing much grows on stone, so what is scattered here is stone.
                Biome.Scree => (ScatterPebbleRound, ScatterPebbleSquare, ScatterWispyShort),
                // Bare rock, and only the square broken sort — a crag sheds angular stone, and rounded
                // pebbles are what a river makes, which is the other end of the map entirely.
                Biome.Crag => (ScatterPebbleSquare, ScatterPebbleSquare, ScatterPebbleSquare),
                // Rank and damp: tall wispy growth and clover in the bottoms.
                Biome.Marsh => (ScatterClover, ScatterWispyTall, ScatterClover),
                // <b>The lushest ground on the map, and it shows it in the grass rather than in trees.</b>
                // Silt and water and no trees to shade it out — see WoodlandFor for why the trees are gone —
                // so what stands here is deep pasture with clover through it. Which is also the read the
                // economy wants: the best ground looks like the best ground.
                Biome.Floodplain => (ScatterCommonTall, ScatterCommonTall, ScatterClover),
                // Pasture, and the only country that gets flowers.
                _ => (ScatterCommonShort, ScatterCommonTall, ScatterFlowers),
            };

            // <b>How much of it there is, which is as much of the read as which kind it is.</b> The species
            // mapping alone gave every country the same amount of cover in a different shape, so a moor and
            // a water meadow were equally shaggy. Around one, so pasture is untouched and a flat map — all
            // meadow by classification — cannot move.
            var lushness = RegionProfile.For(simulation.Terrain.Region).Lushness * biome switch
            {
                Biome.Water => 0f,
                Biome.Crag => 0.22f,
                Biome.Scree => 0.40f,
                Biome.Moor => 0.62f,
                Biome.Marsh => 1.15f,
                Biome.Floodplain => 1.30f,
                _ => 1f,
            };
            if (lushness <= 0f) continue;
            rank *= lushness;
            basal *= lushness;

            var roll = ScatterHash(cx, cz, 0);
            var kind = -1;
            var width = 1f;
            if (roll < patch * basal)
            {
                // The base layer. Short cover in sustained patches, and most of what gets drawn.
                kind = shortGrass;
                width = 0.5f + ScatterHash(cx, cz, 4) * 0.5f;
            }
            else if (roll < patch * rank)
            {
                // The taller form, thick in woodland and in the hollows, occasional on open level ground.
                kind = tallGrass;
                width = (0.55f + ScatterHash(cx, cz, 4) * 0.55f) * (1f + hollow * 0.18f);
            }
            else if (roll > 0.978f - hollow * 0.004f && (!wooded || biome != Biome.Meadow))
            {
                // The rare one. Flowers in pasture, and in the other countries whatever that country's
                // third thing is — never flowers under a canopy, which is the one rule §52 settled here.
                kind = biome == Biome.Meadow && wooded ? -1 : third;
                width = 0.4f + ScatterHash(cx, cz, 4) * 0.3f;
            }
            else if (biome == Biome.Scree && roll > 0.94f)
            {
                // An outcrop, on ground where the soil has gone.
                kind = ScatterHash(cx, cz, 6) < 0.5f ? ScatterPebbleRound : ScatterPebbleSquare;
                width = 0.7f + ScatterHash(cx, cz, 4) * 0.9f;
            }

            if (kind < 0) continue;
            scatterPlacements.Add((kind, SettlementArt.Placement(
                at,
                simulation.Terrain.SampleHeight(at),
                width,
                SettlementArt.FreeYawOf(cx * 73 + cz * 179))));
            placed++;
        }

        Replay:
        foreach (var (kind, model) in scatterPlacements) art.Scatter[kind].Add(model);
    }

    /// <summary>
    /// What the shape of the ground does to the cover on it: thin on the steep, rank in the hollows.
    /// </summary>
    /// <remarks>
    /// <b>§52 asked for this by name</b> — "once relief exists, cover wants to thin on slopes and gather in
    /// hollows, which is the moment the two layers start informing each other rather than merely stacking".
    /// Two signals, both cheap, and both about water rather than about looks: a slope sheds its soil and its
    /// rain, so cover is sparse and short on one; a hollow collects both, so it is thick and rank in one.
    /// <para>
    /// The hollow is a four-sample Laplacian — this point against the mean of its neighbours eight metres
    /// out — which is negative on a knoll and positive in a bottom. Eight metres because that is the scale
    /// a patch of ground cover has; the same measurement at a landform's scale would say "you are on a hill"
    /// rather than "you are in a dip", which is a different question and belongs to the woodland.
    /// </para>
    /// <para>
    /// Faded out where the map has no relief, so flat ground keeps exactly the cover it had — the same
    /// guarantee the woodland's coupling makes, for the same reason.
    /// </para>
    /// </remarks>
    private float GroundCoverRelief(Vector2 at, out float hollow)
    {
        hollow = 0f;
        var strength = Math.Clamp(reliefSpan / 8f, 0f, 1f);
        if (strength <= 0f) return 1f;

        const float reach = 8f;
        var terrain = simulation.Terrain;
        var here = terrain.SampleHeight(at);
        var around = (terrain.SampleHeight(at + new Vector2(reach, 0f)) +
                      terrain.SampleHeight(at - new Vector2(reach, 0f)) +
                      terrain.SampleHeight(at + new Vector2(0f, reach)) +
                      terrain.SampleHeight(at - new Vector2(0f, reach))) * 0.25f;
        // Normalised by the drop a slope of a tenth would give over the same reach, so this is "how much of
        // a dip is this" rather than a number of metres — and clamped, because a quarry is not a meadow.
        hollow = Math.Clamp((around - here) / (reach * 0.10f), -1f, 1f) * strength;

        var slope = MathF.Min(1f, terrain.SampleGrade(at) / 0.24f);
        return Math.Clamp(1f + strength * (0.34f * hollow - 0.55f * slope), 0.12f, 1.4f);
    }

    /// <summary>
    /// How far out trees are drawn as their models rather than as a cross of quads, in metres.
    /// </summary>
    /// <remarks>
    /// The near band is the one that costs: a model is six thousand triangles and a cross is eight, so the
    /// frame's tree budget is very nearly this radius squared. Measured at a wide zoom, ninety metres kept
    /// fourteen hundred models in view and 8.3M of the frame's 8.67M triangles — the whole remaining cost
    /// was the near band, and it goes as the square, so fifty-five metres is a third of it.
    /// <para>
    /// Fifty-five is also about where a tree stops being something you look <em>at</em> and becomes part of
    /// the mass you look <em>through</em>: it is inside the contact-shadow radius and inside the ground
    /// cover's, so the near band is the band where everything else is detailed too.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Where each level of tree detail gives way to the next this frame, in metres, and measured from the
    /// eye rather than from what the camera is looking at.
    /// </summary>
    /// <remarks>
    /// <b>Two things were wrong and the first was visible.</b> The bands were measured from
    /// <c>cameraFocus</c> — the point on the ground the camera is aimed at, which projects to the middle of
    /// the screen — so the detailed band appeared as a circle around screen centre, reported as looking
    /// through "a circular lens" with "tree god eyes". Distance from the eye instead: on a tilted camera
    /// that reads as bands running up the screen, which is what a level of detail is supposed to look like,
    /// because it is what the change in a tree's projected size actually follows.
    /// <para>
    /// <b>Two ways of choosing by distance were built and both were taken back out, which is worth
    /// recording because they are the obvious answers.</b> Screen-space error first: the cook records each
    /// level's world-space geometric error, and projecting it — <c>error × viewportHeight / (2 × distance ×
    /// tan(fov/2))</c> — is the textbook selector, right at any zoom and window size. Measured, it put the
    /// crossovers at four hundred metres and left every tree in the frame at full detail, because
    /// decimating a canopy moves leaf clusters by tens of centimetres: the error is enormous and invisible,
    /// where the same error on a wall would be a hole. The metric is calibrated for surfaces whose
    /// silhouette is what you are looking at, and a canopy's is not. Hand-tuned bands next, as depth past
    /// the focal plane so they ran up the screen rather than ringing its middle — correct, and still wrong,
    /// because any distance ladder thins the far half of the map, and a wood that goes sparse as you pull
    /// back reads as logged. Crowding is what survived. The errors are still reported at load, because they
    /// are worth knowing; they do not choose.
    /// </para>
    /// </remarks>
    /// <summary>
    /// How many standing trees share each ten-metre cell, which is what chooses their level of detail.
    /// </summary>
    /// <remarks>
    /// Ten metres because that is about two canopies across: finer and a tree is alone in its own cell no
    /// matter how thick the wood, coarser and the cleared ring round a village averages into the wood
    /// beside it. Counted rather than read off the terrain's forest cover, which is a binary — cover says
    /// "two trees crowd this cell" and cannot tell a copse from a forest, and three levels of detail need
    /// more than one bit to choose between them.
    /// <para>
    /// Rebuilt once a frame from the node table, which is one pass over the nodes and no spatial query. It
    /// has to be a frame's field rather than a cached one because felling changes it: clear a stand and the
    /// survivors stop being texture and start being trees you are looking at, which is exactly when they
    /// should get their triangles back.
    /// </para>
    /// <para>
    /// Dressing-side, and it never touches simulation state — nothing here is remembered between frames or
    /// fingerprinted. §52's rule: an observational field the renderer keeps to itself.
    /// </para>
    /// </remarks>
    /// <summary>
    /// How much ground one canopy cell covers, which is <see cref="FogOfWar.CellMetres"/> and must stay so.
    /// </summary>
    /// <remarks>
    /// <b>An alias, not a second constant.</b> This grid and the fog's are the same grid — the fog was put on
    /// the canopy geometry deliberately, because the per-cell draw gate has to read the fog and iterate the
    /// trees, and two ten-metre grids computed by two copies of one formula is precisely the failure this
    /// file keeps producing. A tree popping along a boundary the player can see the fog at is what the
    /// duplicate would have looked like.
    /// </remarks>
    private const float CanopyCellMetres = FogOfWar.CellMetres;

    private int[] canopyCounts = Array.Empty<int>();

    private void RebuildCanopyDensity()
    {
        EnsureFog();
        var total = scouted.Cells * scouted.Cells;
        if (canopyCounts.Length != total) canopyCounts = new int[total];
        else Array.Clear(canopyCounts);

        foreach (ref readonly var node in simulation.Nodes.All)
        {
            if (!node.IsAlive || !node.IsStanding) continue;
            canopyCounts[CanopyIndex(node.Position)]++;
        }
    }

    private int CanopyIndex(Vector2 at) => scouted.Index(at);

    /// <summary>Which level of detail the crowding here earns: 0 full, 1 middle, 2 coarse.</summary>
    // <b>The drawn-density cap is out again, and it is worth recording why.</b> It capped how many trees a
    // canopy cell draws and widened the survivors, on the reasoning that a closed roof stops changing long
    // before the trees under it stop being added. It did what it claimed — 4,130 trees and 4.1M triangles down
    // to 1,915 and 2.45M — and the frame went 85.2 ms to 74.4 ms, which is nothing. So it cost two thousand
    // visible trees and bought almost no time: whatever this frame is spending itself on, it is not tree
    // geometry, and thinning the forest to find out was the wrong order of operations.

    private int CanopyTierAt(Vector2 at)
    {
        if (scouted.Cells == 0 || canopyCounts.Length == 0) return 0;
        var crowd = canopyCounts[CanopyIndex(at)];
        // <b>A fourth tier at twice the crowding the third needs.</b> The rule is the same one all the way up:
        // a coarse level loses mass and the neighbours put it back, so the deeper into a wood a tree is the
        // less its own shape is doing. Twice, rather than a new dial, because that is the statement — "twice
        // as crowded as crowded" — and a number would invite tuning where a relationship does not.
        // <b>The natural tier, unbiased.</b> --perf-tier-bias is applied where the mesh is chosen, not here,
        // because this answer also decides which trees get undergrowth and a contact shadow — and a
        // measurement lever has no business changing the dressing.
        return crowd >= look.TreeCrowdFar * 2f ? 3
            : crowd >= look.TreeCrowdFar ? 2
            : crowd >= look.TreeCrowdMid ? 1
            : 0;
    }

    /// <summary>
    /// Where each level of tree detail gave way to the next before this was measured, in metres.
    /// </summary>
    /// <remarks>
    /// <b>And the last of them is deliberately beyond what the camera can see.</b> Written when the zoom
    /// capped at 118 m, which sees about 165 m of ground — §86 unpinned that, so the claim below is now a
    /// claim about a standoff the wheel can exceed; the tiers are chosen by crowding rather than by distance,
    /// which is why unpinning the zoom did not break them. A limit at 190 m was a limit nothing is ever drawn
    /// <em>across</em>
    /// — trees do not appear at the edge of view, they were always there. A cutoff inside the visible radius
    /// is the one thing an LOD scheme must not have, however cheap it makes the frame.
    /// <para>
    /// The near band is small because it is the expensive one — cost goes as its radius squared — and it
    /// only has to cover what a player is actually looking at closely. The middle band does the work: a
    /// tenth of the triangles over four times the area.
    /// </para>
    /// </remarks>



    /// <summary>
    /// Where each kind of ground cover sits in the scatter list. Ordered by construction — see SettlementArt.
    /// </summary>
    private const int ScatterCommonShort = 0;
    private const int ScatterCommonTall = 1;
    private const int ScatterWispyShort = 2;
    private const int ScatterWispyTall = 3;
    private const int ScatterClover = 4;
    private const int ScatterFern = 5;
    private const int ScatterFlowers = 6;
    private const int ScatterPebbleRound = 8;
    private const int ScatterPebbleSquare = 9;

    /// <summary>How much ground cover belongs at a point, from bare to thick.</summary>
    /// <remarks>
    /// Two octaves of value noise on a lattice, smoothstep-interpolated: about thirty metres for where a
    /// patch is and about eleven for how it varies inside itself. The same idea as the terrain's macro
    /// variation in the shader, on the CPU because placement is a CPU decision — and deliberately the same
    /// <em>kind</em> of idea, so that dressing and ground colour drift together rather than arguing.
    /// </remarks>
    private static float PatchDensity(Vector2 at)
    {
        var broad = LatticeNoise(at * (1f / 31f));
        var fine = LatticeNoise(at * (1f / 11f) + new Vector2(17.3f, 5.1f));
        // Skewed low, so most of the map is thin and the thick parts are worth noticing.
        var combined = broad * 0.7f + fine * 0.3f;
        return Math.Clamp((combined - 0.34f) / 0.5f, 0f, 1f);
    }

    private static float LatticeNoise(Vector2 at)
    {
        var x0 = (int)MathF.Floor(at.X);
        var z0 = (int)MathF.Floor(at.Y);
        var fx = at.X - x0;
        var fz = at.Y - z0;
        fx = fx * fx * (3f - 2f * fx);
        fz = fz * fz * (3f - 2f * fz);
        var a = ScatterHash(x0, z0, 11);
        var b = ScatterHash(x0 + 1, z0, 11);
        var c = ScatterHash(x0, z0 + 1, 11);
        var d = ScatterHash(x0 + 1, z0 + 1, 11);
        return float.Lerp(float.Lerp(a, b, fx), float.Lerp(c, d, fx), fz);
    }

    /// <summary>
    /// Where people have been walking, accumulated, and painted onto the ground.
    /// </summary>
    /// <remarks>
    /// <b>The one visual system here that is ours rather than borrowed from a reference image.</b> Every
    /// reference has worn paths between its buildings, and every reference had them <em>painted by hand</em> —
    /// a still frame cannot have paths that came from anywhere else. We have every body's position thirty
    /// times a second, so ours can come from where people actually walk: they deepen along a real haul route,
    /// they fork where a route forks, they appear round a new field the season it is worked, and they fade
    /// when nobody goes that way any more.
    /// <para>
    /// Which makes it more than decoration. The receding wood line, the catchment a house sits outside, the
    /// route a cart runs — all of those are currently legible only from a panel, and a settlement that wears
    /// its own history into the ground says them without a single interface element.
    /// </para>
    /// <para>
    /// Cosmetic, so it lives here and not in the simulation: it decides nothing, it is not fingerprinted,
    /// and it is not saved. A loaded world starts with clean ground and wears it again, which is honest for
    /// something that is a record of watching rather than a fact about the world.
    /// </para>
    /// </remarks>
    private const int WearCells = 320;

    private readonly byte[] wear = new byte[WearCells * WearCells];
    private readonly float[] wearAccumulator = new float[WearCells * WearCells];
    private TextureHandle wearTexture;
    private int wearUploadCountdown;

    /// <summary>
    /// Adds this frame's footfall and decays what is there, then uploads if it is time.
    /// </summary>
    /// <remarks>
    /// Decay and upload are both throttled rather than per-frame. Wear changes over minutes — a path is not
    /// a thing that appears in a frame — so re-uploading a 36 KB texture four times a second is already far
    /// finer than the phenomenon, and the accumulator carries the fractional part that a byte cannot.
    /// </remarks>
    private void AdvanceWear(float deltaSeconds)
    {
        var extent = simulation.ExtentMeters;
        var scale = WearCells / extent;
        foreach (ref readonly var agent in simulation.Agents.All)
        {
            if (!agent.IsAlive || agent.Sheltered) continue;
            var x = (int)((agent.Position.X + extent * 0.5f) * scale);
            var z = (int)((agent.Position.Y + extent * 0.5f) * scale);
            if (x < 0 || z < 0 || x >= WearCells || z >= WearCells) continue;
            // <b>Spread across the four cells it stands between, not dumped into one.</b> A body deposited
            // into whichever cell contained it, so a walk laid down a chain of hard two-metre squares and a
            // route read as a dotted line of blocks rather than as a path. Weighting by how near the body is
            // to each of its neighbours makes the deposit continuous in position, so the same walk leaves a
            // smooth smear — and it costs three more multiplies.
            var fx = (agent.Position.X + extent * 0.5f) * scale - x;
            var fz = (agent.Position.Y + extent * 0.5f) * scale - z;
            var gain = deltaSeconds * look.WearGain;
            Deposit(x, z, (1f - fx) * (1f - fz) * gain);
            Deposit(x + 1, z, fx * (1f - fz) * gain);
            Deposit(x, z + 1, (1f - fx) * fz * gain);
            Deposit(x + 1, z + 1, fx * fz * gain);
        }

        wearUploadCountdown--;
        if (wearUploadCountdown > 0) return;
        wearUploadCountdown = 15;

        // Grass grows back. Without this a settlement ends its first year uniformly trodden, which says
        // nothing — the information is in the contrast between where people go and where they used to.
        var keep = MathF.Exp(-look.WearFadeRate * 0.5f);
        for (var i = 0; i < wearAccumulator.Length; i++) wearAccumulator[i] *= keep;

        // <b>Blurred into the texture, never back into the accumulator.</b> A path wants soft edges — real
        // ground does not end at a line — and a three-tap blur each way gives that for a couple of hundred
        // microseconds. But blurring the accumulator would compound: every upload would diffuse the record
        // a little further until, after a few minutes, there were no paths left to see, only a warm patch
        // over the whole settlement. So the accumulator stays the sharp truth and the texture is a smoothed
        // view of it.
        for (var z = 0; z < WearCells; z++)
        for (var x = 0; x < WearCells; x++)
        {
            var total = 0f;
            var weight = 0f;
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var sx = x + dx;
                var sz = z + dz;
                if (sx < 0 || sz < 0 || sx >= WearCells || sz >= WearCells) continue;
                // A small Gaussian: four in the middle, two on the edges, one on the corners.
                var w = dx == 0 && dz == 0 ? 4f : dx == 0 || dz == 0 ? 2f : 1f;
                total += wearAccumulator[sz * WearCells + sx] * w;
                weight += w;
            }

            wear[z * WearCells + x] = (byte)(Math.Clamp(total / weight, 0f, 1f) * 255f);
        }

        // Queued, not uploaded: see IGraphicsDevice.QueueTextureUpload. Every fifteenth frame is rare
        // enough that the old drain was survivable here, which is exactly why the fog's — every frame —
        // was not, and why this one was hiding in plain sight beside it.
        graphicsDevice.QueueTextureUpload(wearTexture, 0, wear);
    }

    /// <summary>How many pieces of ground cover one frame may draw.</summary>
    /// <remarks>
    /// A ceiling learnt the hard way once already — the instanced batch refuses past 16,384 and the trees,
    /// their skirts and this all share it. Grass is the most numerous thing in the scene and the least
    /// missed, so it is the one that gets a hard cap.
    /// </remarks>
    private const int ScatterBudget = 5200;

    /// <summary>Adds wear to one cell, ignoring anything off the map.</summary>
    private void Deposit(int x, int z, float amount)
    {
        if (x < 0 || z < 0 || x >= WearCells || z >= WearCells || amount <= 0f) return;
        var at = z * WearCells + x;
        wearAccumulator[at] = MathF.Min(1f, wearAccumulator[at] + amount);
    }

    /// <summary>A stable 0..1 from a cell and a channel, so a stone is always the same stone.</summary>
    private static float ScatterHash(int x, int z, int channel)
    {
        var hash = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ (uint)(channel * 83492791);
        hash = (hash ^ (hash >> 16)) * 0x7FEB352Du;
        hash = (hash ^ (hash >> 15)) * 0x846CA68Bu;
        return ((hash ^ (hash >> 16)) & 0xFFFFFFu) / (float)0x1000000u;
    }

    /// <summary>
    /// What is left where a tree came down.
    /// </summary>
    /// <remarks>
    /// The one visible record that a wood used to be bigger than it is. Cutting is otherwise invisible in
    /// hindsight: a tree shrinks while it is being felled and then simply is not there, so a decade of a
    /// settlement's work on its wood line left the ground looking as though nobody had ever been. Stumps
    /// are what make a cleared band read as cleared rather than as a wood that happens to start further
    /// out — which matters, because the receding wood line is Stage B's entire interface.
    /// <para>
    /// Drawn from <c>SimulationWorld.RecentFellings</c>, which is cosmetic and bounded: the oldest
    /// clearing loses its stumps when the ring wraps, and a loaded save has none until something is felled.
    /// Both are honest for a thing that decides nothing.
    /// </para>
    /// </remarks>
    private void DrawStumps()
    {
        if (art is null) return;
        foreach (var where in simulation.RecentFellings)
        {
            if (Vector2.DistanceSquared(where, cameraFocus) > treeCullBoundSquared) continue;
            var ground = simulation.Terrain.SampleHeight(where);
            // Small: a stump is what is left of a trunk, not what is left of a canopy, so it is sized off
            // the footprint the simulation routed bodies around rather than off the tree that was drawn.
            var width = NodeFootprint.TreeHalfExtent * 2f * 1.15f;
            art.Stumps.Add(SettlementArt.Placement(where, ground, width, StumpYawOf(where)));
        }
    }

    /// <summary>A turn taken from the ground itself, so a stump does not spin between frames.</summary>
    private static float StumpYawOf(Vector2 where)
    {
        var hash = (int)(where.X * 7.3f) * 73856093 ^ (int)(where.Y * 7.3f) * 19349663;
        return (hash & 0x7FFFFFFF) % 360 * MathF.PI / 180f;
    }

    /// <summary>
    /// A heap of goods on the ground, low and wide, the shape of what it is.
    /// </summary>
    /// <remarks>
    /// Drawn small so it reads as spillage rather than as a building — a thing to go and fetch. Which is
    /// the point: a cart lost on the road leaves this behind, and whoever gets to it first has it, so it
    /// needs to be visible from across the map without being mistaken for a store.
    /// </remarks>
    private void DrawPile(in EconomyNode pile)
    {
        var ground = simulation.Terrain.SampleHeight(pile.Position);
        foreach (var resource in Resources.All)
        {
            var units = pile.Stock[resource];
            if (units <= 0) continue;
            // A heap of forty is a cart's worth and about a metre across; bigger heaps spread rather than
            // tower, because a pile of sacks does.
            // <b>Capped, because a prop's height comes from its own proportions.</b> A heap of a hundred
            // and twenty asked for nearly three metres across and got a crate three metres tall standing
            // over the houses — reported, accurately, as "the largest crates in existence". A cart's worth
            // is about a metre; more than that spreads a little and then stops.
            var spread = 0.55f + MathF.Min(0.75f, MathF.Sqrt(units / 40f) * 0.45f);
            if (art is not null)
            {
                var model = resource switch
                {
                    Resource.Grain => art.GrainHeap,
                    Resource.Stone when art.Rocks.Length > 0 => art.Rocks[0],
                    _ => art.WoodHeap,
                };
                AddCascaded(model, SettlementArt.Placement(
                    pile.Position, ground, spread, SettlementArt.FreeYawOf(pile.Id.Value + (int)resource)));
                continue;
            }

            var height = 0.22f + MathF.Min(0.5f, units / 200f);
            propInstances.Add(new InstanceData(
                Matrix4x4.CreateScale(spread, height, spread) *
                Matrix4x4.CreateTranslation(pile.Position.X, ground + height * 0.5f, pile.Position.Y),
                ColourOf(resource)));
        }
    }

    private void BuildObstacleInstances()
    {
        var grid = simulation.Placement;
        var blockWidth = grid.Transform.CellSize * 0.78f;
        const float blockHeight = 1.45f;
        foreach (var cell in grid.OccupiedCells)
        {
            // Skip ground a building already stands on. The building draws itself, as one solid thing of
            // the right size; drawing its cells as well made every farm a tray of nine grey ice cubes on
            // a coloured plate, which is what "ridiculously blocky" looked like on screen. A wall
            // somebody built by hand still draws per cell, because that is what it is.
            var center = grid.Transform.CellCenter(cell);
            if (StandsOnANode(center)) continue;
            var terrainHeight = simulation.Terrain.SampleHeight(center);
            var model = Matrix4x4.CreateScale(blockWidth, blockHeight, blockWidth) *
                        Matrix4x4.CreateTranslation(center.X, terrainHeight + blockHeight * 0.5f, center.Y);
            propInstances.Add(new InstanceData(model, ObstacleColor));
        }

        if (!obstacleEditMode || !pointerOnTerrain || !simulation.TryGetPlacementCell(pointerWorld, out var hoverCell)) return;
        var hoverCenter = grid.Transform.CellCenter(hoverCell);
        var isRemoval = grid.IsOccupied(hoverCell);
        var valid = simulation.CanToggleObstacle(hoverCell);
        var hoverTerrainHeight = simulation.Terrain.SampleHeight(hoverCenter);
        var hoverHeight = hoverTerrainHeight + (isRemoval ? blockHeight + 0.04f : 0.04f);
        var hoverModel = Matrix4x4.CreateScale(grid.Transform.CellSize * 0.88f, 0.065f, grid.Transform.CellSize * 0.88f) *
                         Matrix4x4.CreateTranslation(hoverCenter.X, hoverHeight, hoverCenter.Y);
        overlayBatch.Add(hoverModel, !valid ? BuildInvalidColor : isRemoval ? BuildRemoveColor : BuildValidColor);
    }

    private void DrawSelectionMarquee(RenderPassBuilder pass)
    {
        if (!selection.IsMarqueeVisible || obstacleEditMode) return;
        var minimum = Vector2.Min(selection.DragStart, selection.DragCurrent);
        var maximum = Vector2.Max(selection.DragStart, selection.DragCurrent);
        var size = maximum - minimum;
        var (width, height) = host.LogicalSize;
        var ortho = GraphicsMatrices.CreateOrthographicOffCenterVulkan(
            0f, width, height, 0f, -1f, 1f);
        selectionUi.Begin(ortho);

        var fill = new GraphicsColor(0.26f, 0.86f, 0.94f, 0.10f);
        var border = new GraphicsColor(0.26f, 0.86f, 0.94f, 0.95f);
        const float thickness = 2f;
        selectionUi.DrawSolidRect(selectionPixel,
            new Rect(minimum.X, minimum.Y, size.X, size.Y), fill);
        selectionUi.DrawSolidRect(selectionPixel,
            new Rect(minimum.X, minimum.Y, size.X, thickness), border);
        selectionUi.DrawSolidRect(selectionPixel,
            new Rect(minimum.X, maximum.Y - thickness, size.X, thickness), border);
        selectionUi.DrawSolidRect(selectionPixel,
            new Rect(minimum.X, minimum.Y, thickness, size.Y), border);
        selectionUi.DrawSolidRect(selectionPixel,
            new Rect(maximum.X - thickness, minimum.Y, thickness, size.Y), border);
        selectionUi.End(pass);
    }

    private void BuildVelocityDebug()
    {
        if (!velocityDebug) return;
        foreach (ref readonly var agent in simulation.Agents.All)
        {
            if (!agent.IsAlive || !selection.Contains(agent.Id)) continue;
            AddGroundLine(agent.Position, agent.Position + agent.PreferredVelocity * 0.32f,
                PreferredVelocityColor, 0.035f);
            AddGroundLine(agent.Position, agent.Position + agent.Velocity * 0.32f,
                ResolvedVelocityColor, 0.055f);
        }
    }

    private void BuildPathDebug()
    {
        if (!pathDebug) return;
        foreach (ref readonly var agent in simulation.Agents.All)
        {
            if (!agent.IsAlive || !selection.Contains(agent.Id)) continue;
            var start = agent.Position;
            foreach (var waypoint in simulation.GetRemainingPath(agent.Id))
            {
                AddGroundLine(start, waypoint, PathColor, 0.075f);
                start = waypoint;
            }
        }
    }

    /// <summary>
    /// Draws what the map is doing, for the fault that would not attribute itself.
    /// </summary>
    /// <remarks>
    /// §153. Every overlay here answers a question a criterion in <see cref="Debug.TerrainCriteria"/> asks
    /// with a number, and uses the same test the criterion does — so what is drawn is exactly what is
    /// counted. That is the whole point: §152 spent four hypotheses on 134 shortfalls without ever seeing
    /// one of them.
    /// </remarks>
    private void DrawMapOverlay()
    {
        if (mapTuning.Overlay == MapTuning.MapOverlay.None) return;
        if (simulation.Terrain.Drainage is not { } water) return;

        switch (mapTuning.Overlay)
        {
            case MapTuning.MapOverlay.Drainage:
                DrawDrainage(water);
                break;
            case MapTuning.MapOverlay.Standing:
                DrawStanding(water);
                break;
            case MapTuning.MapOverlay.Grade:
            case MapTuning.MapOverlay.Blocked:
                DrawGround(mapTuning.Overlay == MapTuning.MapOverlay.Grade);
                break;
        }
    }

    /// <summary>One line per channel cell, toward whatever it drains into.</summary>
    private void DrawDrainage(Simulation.Terrain.Drainage water)
    {
        var side = water.Side;
        var step = water.CellMetres;
        var receiver = water.Receiver;
        var lake = water.LakeDepth;
        var uphill = 0;
        var drawn = 0;
        var worstRise = 0f;
        var worstAt = Vector2.Zero;
        for (var index = 0; index < receiver.Length && drawn < OverlayLineBudget; index++)
        {
            var at = water.Origin + new Vector2(index % side, index / side) * step;
            if (!Within(at)) continue;
            if (water.WidthAt(at) <= Simulation.Terrain.Drainage.TraceWidthMetres) continue;
            var here = water.LevelAt(at);
            if (here - water.BedAt(at) <= 0.02f) continue;
            var to = receiver[index];
            if (to < 0 || to == index) continue;
            var next = water.Origin + new Vector2(to % side, to / side) * step;

            // The one thing this overlay exists for. A channel entering standing water is below its surface
            // by definition — see the note in TerrainCriteria — so those are left out of the count here for
            // the same reason they are left out there.
            var rise = water.LevelAt(next) - here;
            var wrong = lake[to] <= 0.05f && rise > 0.01f;
            if (wrong)
            {
                uphill++;
                // <b>The worst one, named, because localising was the entire point.</b> §153: a count with no
                // position is what §152 failed on four times over. This says where to go and look.
                if (rise > worstRise)
                {
                    worstRise = rise;
                    worstAt = at;
                }
            }
            var weight = Math.Clamp(water.WidthAt(at) / 24f, 0.15f, 1f);
            AddGroundLine(
                at,
                next,
                wrong
                    ? new Vector4(1f, 0.16f, 0.12f, 1f)
                    : new Vector4(0.30f, 0.55f + 0.40f * weight, 0.95f, 0.55f + 0.45f * weight),
                wrong ? 1.4f : 0.5f + weight);
            drawn++;
        }

        // <b>Keyed on the uphill count and not on the drawn count.</b> The drawn count changes every frame
        // the camera moves, because only what is near it is drawn — so the first version printed a line per
        // frame while panning, which is the opposite of legible.
        Report(
            $"uphill:{uphill}",
            uphill == 0
                ? $"drainage: {drawn:N0} reach(es) in view, none running uphill"
                : $"drainage: {drawn:N0} reach(es) in view, {uphill:N0} running uphill; " +
                  $"worst rises {worstRise:F2} m at ({worstAt.X:F0}, {worstAt.Y:F0})");
    }

    /// <summary>Standing water, ringed, and coloured by whether there is a basin under it.</summary>
    private void DrawStanding(Simulation.Terrain.Drainage water)
    {
        var side = water.Side;
        var step = water.CellMetres;
        var lake = water.LakeDepth;
        var pools = 0;
        var basinless = 0;
        var drawn = 0;
        var seen = new bool[lake.Length];
        var stack = new Stack<int>();
        for (var start = 0; start < lake.Length; start++)
        {
            if (seen[start] || lake[start] <= 0.05f) continue;
            stack.Push(start);
            seen[start] = true;
            var members = new List<int>();
            var deepest = 0f;
            var lowX = int.MaxValue;
            var highX = int.MinValue;
            var lowZ = int.MaxValue;
            var highZ = int.MinValue;
            while (stack.Count > 0)
            {
                var index = stack.Pop();
                members.Add(index);
                deepest = MathF.Max(deepest, lake[index]);
                var cx = index % side;
                var cz = index / side;
                lowX = Math.Min(lowX, cx);
                highX = Math.Max(highX, cx);
                lowZ = Math.Min(lowZ, cz);
                highZ = Math.Max(highZ, cz);
                Spread(cx - 1, cz);
                Spread(cx + 1, cz);
                Spread(cx, cz - 1);
                Spread(cx, cz + 1);
            }

            pools++;
            var span = MathF.Max(highX - lowX, highZ - lowZ) * step;
            var hasBasin = deepest >= span * 0.0125f;
            if (!hasBasin) basinless++;

            // The margin only: a filled interior tells you nothing a colour cannot, and the shape of the
            // edge is the thing that says "apron" or "basin" at a glance.
            var colour = hasBasin
                ? new Vector4(0.25f, 0.75f, 1f, 0.85f)
                : new Vector4(1f, 0.55f, 0.10f, 0.95f);
            foreach (var index in members)
            {
                if (drawn >= OverlayLineBudget) break;
                var cx = index % side;
                var cz = index / side;
                if (!Edge(cx, cz)) continue;
                var at = water.Origin + new Vector2(cx, cz) * step;
                if (!Within(at)) continue;
                AddGroundLine(at - new Vector2(step * 0.4f, 0f), at + new Vector2(step * 0.4f, 0f), colour, 0.9f);
                drawn++;
            }
        }

        Report(
            $"pools:{pools}/{basinless}",
            $"standing water: {pools:N0} pool(s) in view, {basinless:N0} without a basin under them");
        return;

        void Spread(int x, int z)
        {
            if (x < 0 || z < 0 || x >= side || z >= side) return;
            var index = z * side + x;
            if (seen[index] || lake[index] <= 0.05f) return;
            seen[index] = true;
            stack.Push(index);
        }

        bool Edge(int x, int z)
        {
            for (var k = 0; k < 4; k++)
            {
                var nx = x + (k == 0 ? -1 : k == 1 ? 1 : 0);
                var nz = z + (k == 2 ? -1 : k == 3 ? 1 : 0);
                if (nx < 0 || nz < 0 || nx >= side || nz >= side) return true;
                if (lake[nz * side + nx] <= 0.05f) return true;
            }

            return false;
        }
    }

    /// <summary>Ground the criteria would fault: too steep where it should be crossable, or blocked.</summary>
    private void DrawGround(bool steepOnly)
    {
        var grid = simulation.Navigation.Transform;
        var rim = (int)MathF.Ceiling(Simulation.Terrain.ReliefPlan.RimWidthMetres / grid.CellSize);
        var marked = 0;
        var stride = Math.Max(1, grid.Width / 190);
        for (var z = rim; z < grid.Height - rim && marked < OverlayLineBudget; z += stride)
        for (var x = rim; x < grid.Width - rim && marked < OverlayLineBudget; x += stride)
        {
            var cell = new Simulation.Spatial.GridCell(x, z);
            var at = grid.CellCenter(cell);
            if (!Within(at)) continue;
            bool bad;
            if (steepOnly)
            {
                // The criterion's own test, so the picture and the count cannot disagree.
                if (!Simulation.Terrain.TerrainSurfaceRules.IsPassable(simulation.Terrain.Surface(cell)))
                {
                    continue;
                }

                bad = simulation.Terrain.SampleGrade(at) > Simulation.Terrain.TerrainMap.MaximumTraversableGrade;
            }
            else
            {
                bad = simulation.Navigation.IsBlocked(cell);
            }

            if (!bad) continue;
            var reach = grid.CellSize * stride * 0.45f;
            AddGroundLine(
                at - new Vector2(reach, 0f),
                at + new Vector2(reach, 0f),
                steepOnly ? new Vector4(1f, 0.20f, 0.55f, 0.9f) : new Vector4(0.85f, 0.35f, 0.95f, 0.55f),
                0.8f);
            marked++;
        }

        Report(
            steepOnly ? $"steep:{marked}" : $"blocked:{marked}",
            steepOnly
                ? $"grade: {marked:N0} sample(s) over the traversable limit on ground meant to be crossed"
                : $"blocked: {marked:N0} sample(s) a body cannot walk on");
    }

    /// <summary>
    /// Only what the camera can see, because an overlay of the whole map is a fog of lines.
    /// </summary>
    /// <remarks>
    /// Generous — twice the drawn-ground reach — so panning does not make marks appear at the edge of
    /// attention, which is the thing that makes an overlay feel like it is lying.
    /// </remarks>
    private bool Within(Vector2 at) =>
        Vector2.DistanceSquared(at, cameraFocus) <= OverlayReachMetres * OverlayReachMetres;

    private const float OverlayReachMetres = 260f;

    /// <summary>Lines one overlay may spend. A budget, because these are debug draws and not a renderer.</summary>
    private const int OverlayLineBudget = 6000;

    /// <summary>
    /// Said when the finding changes, not when the picture does.
    /// </summary>
    /// <param name="key">
    /// What counts as the same finding. Deliberately narrower than the message: an overlay draws only what is
    /// near the camera, so a figure that includes how much was drawn changes on every frame of a pan and
    /// reports nothing worth reading.
    /// </param>
    private void Report(string key, string line)
    {
        if (key == lastOverlayReport) return;
        lastOverlayReport = key;
        Console.WriteLine($"  {line}");
    }

    private string? lastOverlayReport;

    private void AddGroundLine(Vector2 start, Vector2 end, Vector4 color, float height)
    {
        var delta = end - start;
        var length = delta.Length();
        if (length < 0.001f) return;
        var midpoint = (start + end) * 0.5f;
        var yaw = MathF.Atan2(-delta.Y, delta.X);
        var model = Matrix4x4.CreateScale(length, 0.045f, 0.075f) *
                    Matrix4x4.CreateRotationY(yaw) *
                    Matrix4x4.CreateTranslation(midpoint.X, simulation.Terrain.SampleHeight(midpoint) + height, midpoint.Y);
        overlayBatch.Add(model, color);
    }

    /// <summary>
    /// The base colour of a surface, before the checker and the per-block variation.
    /// </summary>
    /// <remarks>
    /// One colour per surface rather than a light and a dark pair. The pair was how the checker used to be
    /// expressed, and it meant the contrast was baked into five pairs of constants that had to be kept in
    /// step with each other; now the checker is one slider applied to every surface the same way.
    /// </remarks>
    /// <summary>Stamps the terrain material class into a colour, leaving its hue alone.</summary>
    private static Vector4 TerrainClassed(Vector4 color) =>
        new(color.X, color.Y, color.Z, SettlementArt.MaterialClass.Terrain);

    /// <summary>
    /// The ground's own colour, which is a fact about the region as much as about the surface.
    /// </summary>
    /// <remarks>
    /// <b>It was five static constants, and that was the whole reason every map came out green.</b> A palette
    /// fixed in the code is a climate fixed in the code: the archetype layer could vary the shape of the land
    /// all it liked and the answer to "what colour is grass here" was the same on every one of them. Now the
    /// four grounds a region has an opinion about — pasture, heath, rough and mud — come from its profile.
    /// <para>
    /// Water and road do not, because they do not vary that way. Water is water, and a made road is the
    /// colour of what it was made from wherever it is.
    /// </para>
    /// </remarks>
    private Vector4 TerrainColor(TerrainSurface surface)
    {
        var profile = RegionProfile.For(simulation.Terrain.Region);
        return surface switch
        {
            TerrainSurface.Road => RoadColor,
            TerrainSurface.Heath => profile.Heath,
            TerrainSurface.Rough => profile.Rough,
            TerrainSurface.Mud => profile.Mud,
            // <b>The bed, not the water — and this is what "the edges are too hard" was.</b> These two
            // surfaces are how the simulation knows water is there, and they were also being drawn as water:
            // a dark teal patch stamped at the classifier's own resolution, with a step in it every three
            // metres, under a translucent sheet that had a soft edge. The hard edge won, because it was the
            // opaque one.
            //
            // Now the water is the sheet and only the sheet, and these draw what lies under it — silt in the
            // deep, gravel in the shallows. Which is also why a shore works at all: the bed shows through the
            // margin, so the transition is a change in what you are seeing through the water rather than a
            // line where one colour stops.
            TerrainSurface.Impassable => RiverbedColor,
            TerrainSurface.Shallows => ShingleColor,
            // Forest cover is a navigation fact painted the colour of what grows under a wood, which is the
            // same ground as beside it. See the long note that used to live here.
            _ => profile.Pasture,
        };
    }

    private static Vector4 TerrainColorLegacy(TerrainSurface surface) => surface switch
    {
        TerrainSurface.Road => RoadColor,
        TerrainSurface.Heath => HeathColor,
        TerrainSurface.Rough => RoughColor,
        TerrainSurface.Mud => MudColor,
        TerrainSurface.Impassable => ImpassableColor,
        TerrainSurface.Shallows => ShallowsColor,
        // <b>Forest cover is not a colour, and this is why the woodland had patches in it.</b> Cover is a
        // navigation fact — cells with two trees crowding them are closed — written into the surface channel
        // because that is where the raster reads terrain from. It was never meant to be seen: what you see
        // under a wood is trees, and the ground between them is the same ground. Averaging the block helped
        // and did not fix it, because the honest fix is that this colour should not exist. Grass, and the
        // canopy above it does the work.
        TerrainSurface.Forest => Grass,
        _ => Grass,
    };

    private static Vector4 TerrainCostColor(TerrainSurface surface) => surface switch
    {
        TerrainSurface.Road => new Vector4(0.25f, 0.72f, 0.88f, 1f),
        TerrainSurface.Grass => NavWalkableColor,
        TerrainSurface.Rough => new Vector4(0.92f, 0.67f, 0.20f, 1f),
        TerrainSurface.Mud => new Vector4(0.82f, 0.32f, 0.16f, 1f),
        _ => NavBlockedColor,
    };

    private static Vector4 CongestionColor(float pressure)
    {
        // Pressure rarely exceeds a couple of units outside a hard jam, so scale
        // to that rather than to the field's ceiling or the overlay reads flat.
        var amount = Math.Clamp(pressure / 2.5f, 0f, 1f);
        return Vector4.Lerp(CongestionClearColor, CongestionHotColor, amount);
    }

    private static Vector4 SlopeColor(float grade)
    {
        var amount = Math.Clamp(grade / TerrainMap.MaximumTraversableGrade, 0f, 1f);
        return Vector4.Lerp(NavWalkableColor, NavBlockedColor, amount);
    }

    private void BuildAgentInstances(float totalSeconds)
    {
        var interpolation = (float)(simulationAccumulator / SimulationWorld.FixedDeltaSeconds);
        agentsBehindTheVeil = 0;

        foreach (ref readonly var agent in simulation.Agents.All)
        {
            // Indoors, so not drawn. A raider rummaging in the granary is in the granary, and the way you
            // know it is in there is that it went in and has not come out — see AgentState.Sheltered.
            if (!agent.IsAlive || agent.Sheltered) continue;
            var position = Vector2.Lerp(agent.PreviousPosition, agent.Position, interpolation);
            // Keyed to watched rather than explored, because where a body <em>was</em> is not where it is —
            // which is the entire reason the fog keeps two masks. The player's own bodies pass this by
            // construction, since a body is what makes the cell it stands in watched; what it actually hides is
            // somebody else's.
            if (!scouted.DrawsMobile(position))
            {
                agentsBehindTheVeil++;
                continue;
            }

            var height = simulation.Terrain.SampleHeight(position);
            var selected = selection.Contains(agent.Id);
            var bodyScale = agent.Radius / AgentDefaults.Radius;

            if (selected && colliderOverlay > 0)
            {
                AddColliderDisc(agent.Colliders.Avoidance, position, height + 0.012f, AvoidanceColliderColor);
                AddColliderDisc(agent.Colliders.Placement, position, height + 0.022f, PlacementColliderColor);
                AddColliderDisc(agent.Colliders.Interaction, position, height + 0.032f, InteractionColliderColor);
                AddColliderDisc(agent.Colliders.Movement, position, height + 0.042f, MovementColliderColor);
            }
            else if (selected)
            {
                // <b>Sized off the body's radius in metres, which is not what bodyScale is.</b> That is a
                // ratio against the default villager, so multiplying it by 1.1 produced 1.1 m for a villager
                // — nearly three times the body — and 2.6 m for a wagon. A dimensionless number used as a
                // length: right magnitude by luck at the default size and wrong everywhere else.
                DrawSelectionMark(
                    position,
                    MathF.Max(0.42f, agent.Radius * 1.45f),
                    SelectionPlateAlpha,
                    square: false);
            }
            else if (hoveredAgent == agent.Id)
            {
                // <b>The half of the pair I left out.</b> Removing the hover tint took the only thing a hovered
                // body had, because the mark was written for the selected case and never given the other one —
                // so villagers alone had a hover state that showed nothing while every node had one. Same mark,
                // same size, the weaker alpha.
                DrawSelectionMark(
                    position,
                    MathF.Max(0.42f, agent.Radius * 1.45f),
                    HoverPlateAlpha,
                    square: false);
            }

            // Width is the footprint; height is not. Height used to track radius one for one, which was
            // fine when there was a single body class and became silly the moment there were three: a
            // 0.90 m wagon is 2.4 times a villager across, and drawing it 2.4 times as tall made it a
            // five-metre crate. A cube root or so keeps the original intent — a smaller body still
            // reads as a smaller body — while letting a wide thing be wide: the wagon comes out 2.4
            // times across and a third taller, which is what a cart looks like.
            var bodyHeight = AgentDefaults.BodyHeight * MathF.Pow(bodyScale, 0.3f);
            var unitModel = Matrix4x4.CreateScale(bodyScale, bodyHeight, bodyScale) *
                            Matrix4x4.CreateTranslation(position.X, height + bodyHeight * 0.5f, position.Y);
            // A person rather than a cylinder, turned to face the way it is walking. Static — the pack
            // has no rig, so a villager slides — and still a large improvement on a drum, because the
            // thing a player reads at this distance is silhouette and facing, not gait.
            var person = art?.Villager;
            var drawnAsAPerson = person is not null || bodies is not null;
            // <b>Where a load rides.</b> Above the body by default — which is where a cylinder has to carry
            // it — and replaced by the hands' own position when a rigged body reports one.
            var carriedAt = new Vector3(position.X, height + bodyHeight, position.Y);
            // A body's own contact, tight and faint. This is the one that matters most for a scene read
            // close up: a person standing on ground with nothing under their feet reads as hovering
            // however good the sun shadow is, because a sun shadow at midday is somewhere else entirely.
            AddContactShadow(position, agent.Radius * 1.5f, 0.42f);
            // Either kind of body wants the same placement, so the rigged path does not need the prop to
            // exist — deleting Villager.obj is still how you go back to cylinders, and now deleting the glb
            // is how you go back to the prop.
            if (person is not null || bodies is not null)
            {
                // Facing is a direction rather than an angle, which is what the steering layer wants; a
                // model needs the angle, and the sign is negated because the world's Z runs the other way
                // from a right-handed yaw.
                // <b>Where it is actually going, not where its steering says it is pointed.</b> Reported
                // from the chair as bodies walking backwards: Facing is the steering layer's own heading and
                // lags — or opposes — the travel direction while a body is being pushed about in a crowd.
                // Velocity cannot disagree with the direction of travel because it IS the direction of
                // travel. Facing still decides which way a body stands when it has stopped, since a
                // stationary body has no velocity to read.
                // <b>A body at its work faces its work.</b> §172. Standing bodies took their heading from
                // Facing — the last direction they were steering — so a villager who walked past a tree to
                // reach its free side then chopped at thin air with the trunk behind them. Where the
                // pathing happened to approach from says nothing about where the work is. Reported from the
                // chair exactly that way: facing has to account for where the thing being worked is
                // relative to where pathing left the body.
                //
                // <b>And the act is tested BEFORE the velocity, which is the third sighting of one fault.</b>
                // §184. Reported from the chair: bodies that "whip around like motorbikes while bent over
                // playing the building animation". The order above used to put velocity first, and the bar
                // for velocity is eight centimetres a second — so a body standing correctly at a build site,
                // shoved a few millimetres a frame by depenetration and its neighbours, handed a fresh random
                // direction to the slew every frame and chased it at five hundred degrees a second.
                //
                // This is exactly the fault the pose had, fixed in §176 with exactly this reordering, and the
                // comment there says why it is safe: IsWorking is only ever true of a body already AT its
                // place, so testing it first cannot steal a frame from something genuinely walking there.
                // The general rule, now that it has cost three sightings: <b>anything read off a body at its
                // work must consult the act before the velocity, because an arrived body's velocity is not a
                // direction of travel — it is noise about a fixed point.</b>
                var heading = BodyActions.HeadingOf(in agent);
                var yaw = heading.LengthSquared() > 0.0001f
                    ? -MathF.Atan2(heading.Y, heading.X)
                    : 0f;
                // <b>Plus whatever this asset's own forward is.</b> The line above was written for the prop
                // villager, whose mesh faces +X; a rigged glTF humanoid usually faces along Z instead, and
                // the difference reads from the chair as bodies walking sideways. SkinnedBodies measures it
                // off the rig — ankle to toe is forward on any humanoid — so no asset needs a hand-set dial.
                var skinnedYaw = TurnedTowards(
                    agent.Id.Value,
                    yaw + (bodies?.FacingOffsetRadians ?? 0f) +
                    MathF.Round(bodyFeel.YawQuarters) * MathF.PI * 0.5f);
                var placement = Matrix4x4.CreateScale(bodyHeight) *
                                Matrix4x4.CreateRotationY(yaw) *
                                Matrix4x4.CreateTranslation(position.X, height, position.Y);
                // Selection has to survive the art pass. The ring under the feet says which bodies are
                // selected; tinting the body too is what makes one picked out of a crowd of twenty
                // findable at a glance, which is the thing the greybox did for free by being one colour.
                // Hostile bodies in a hostile colour, which is the one thing about a body that has to be
                // readable before anything else on the screen is.
                // <b>Rigged if there is a rig, the prop if not.</b> §168. The tint rules below are unchanged:
                // a hostile body in a hostile colour is the one thing about a person that has to read before
                // anything else on screen, and that is true of a posed body exactly as it was of a static one.
                var clip = ClipFor(in agent, out var locomotion, out var drawnAction);
                var posed = false;
                if (bodies is not null && clip is not null)
                {
                    var tint = agent.Faction.Value != 0
                        ? RaiderColor
                        : agent.Role == AgentRole.Militia ? MilitiaColor : (Vector4?)null;
                    posed = bodies.Add(
                        Matrix4x4.CreateScale(bodyHeight) *
                        Matrix4x4.CreateRotationY(skinnedYaw) *
                        Matrix4x4.CreateTranslation(position.X, height, position.Y),
                        tint,
                        clip,
                        GaitOf(in agent, clip, locomotion, frameSeconds, drawnAction),
                        CascadeMaskAt(position),
                        out carriedAt);
                }

                if (posed || person is null)
                {
                    // Nothing more to do: the skinned batch carries this body into the scene pass and every
                    // cascade its box reaches, from the one Add above. `person is null` lands here too —
                    // the palette was full or there is no prop to fall back to, and the cylinder below is
                    // what remains.
                }
                else if (agent.Faction.Value != 0) AddCascaded(person, placement, RaiderColor);
                else if (agent.Role == AgentRole.Militia) AddCascaded(person, placement, MilitiaColor);
                // <b>No tint on a body either, and the reason is consistency rather than taste.</b> A selected
                // villager was repainted the old orange while nothing else was, and a hovered one went dark
                // teal — so a villager spoke two colours no other object spoke, and the marker underneath it
                // spoke a third. One language: everything picked gets the same mark on the ground, at two
                // strengths, and models keep their own materials.
                else AddCascaded(person, placement);

                // The cart, drawn behind them, because a carter has to be findable in a crowd. A wider
                // body is the honest difference — 0.37 m to 0.55 — and at this camera distance it is
                // thirteen per cent of height and nothing you would notice. The pack has no cart, so a
                // crate on the ground behind the body stands in: what matters is that the silhouette says
                // "this one is hauling" without reading the panel.
                if (agent.HasCart && art!.GrainHeap is { } cart)
                {
                    var behind = position - agent.Facing * (agent.Radius + 0.42f);
                    AddCascaded(cart, SettlementArt.Placement(behind, height, agent.Radius * 1.7f, yaw));
                }
            }
            // A load is physically on the body, so it is drawn on the body: a cart you can see is loaded
            // is a cart you can see is worth intercepting, which is the whole of §7's return trip.
            if (agent.Jobs.CarriedUnits > 0)
            {
                // A sack, not a barrel. This was 0.72 of a body's width and half a metre tall, which on a
                // 1.45 m person drawn as a person rather than a cylinder reads as a marshmallow balanced
                // on their head; and the colour was a 0.82 albedo, which tonemaps to white.
                var loadWidth = bodyScale * 0.46f;
                var loadHeight = 0.12f + 0.20f * MathF.Min(1f, agent.Jobs.CarriedUnits /
                    MathF.Max(1f, agent.CarryCapacity));
                // <b>In the hands, not on the head.</b> The load used to be placed a fixed height above the
                // body's origin, which on a rigged villager sat on top of their skull and stayed there while
                // the arms moved beneath it. SkinnedBodies reports the midpoint of the two hands in the pose
                // it just drew, so the sack is where the body is holding it — and a carry cycle's arms and
                // the thing they are carrying now agree.
                unitInstances.Add(new InstanceData(
                    Matrix4x4.CreateScale(loadWidth, loadHeight, loadWidth) *
                    Matrix4x4.CreateTranslation(carriedAt),
                    ColourOf(agent.Jobs.Carrying)));
            }

            var crowdYielding = agent.IsVisiblyYielding;
            var unitColor = crowdYielding
                ? QueuedUnitColor
                : agent.StuckSeconds > AgentDefaults.WedgedSeconds ? StuckUnitColor
                : stateDebug ? StateColor(agent.LocomotionState)
                : UnitColor;
            // The cylinder still draws whenever it is carrying information the model cannot: a body
            // yielding under crowd pressure, a body failing to make progress, or the state overlay. Those
            // are the colours the whole locomotion layer is judged by and they must not be lost to an art
            // pass. Otherwise the person stands in for it.
            var saysSomething = crowdYielding || agent.StuckSeconds > AgentDefaults.WedgedSeconds ||
                                stateDebug;
            if (!drawnAsAPerson || saysSomething)
            {
                unitInstances.Add(new InstanceData(unitModel, unitColor));
            }

            if (selected && agent.HasDestination)
            {
                var pulse = 1f + 0.10f * MathF.Sin(totalSeconds * 7f + agent.Id.Value * 0.17f);
                var destinationHeight = simulation.Terrain.SampleHeight(agent.Destination);
                var destinationModel = Matrix4x4.CreateScale(bodyScale * 0.82f * pulse, 0.045f, bodyScale * 0.82f * pulse) *
                                       Matrix4x4.CreateTranslation(agent.Destination.X, destinationHeight + 0.03f, agent.Destination.Y);
                unitInstances.Add(new InstanceData(destinationModel, DestinationColor));
            }
        }
    }

    private static Vector4 StateColor(AgentLocomotionState state) => state switch
    {
        AgentLocomotionState.Idle => IdleStateColor,
        AgentLocomotionState.Move => MoveStateColor,
        AgentLocomotionState.Follow => FollowStateColor,
        AgentLocomotionState.Patrol => PatrolStateColor,
        AgentLocomotionState.Chase => ChaseStateColor,
        AgentLocomotionState.Flee => FleeStateColor,
        _ => UnitColor,
    };

    /// <summary>
    /// Every collider in the world, drawn from its own numbers, so a mismatch with the art is visible.
    /// </summary>
    /// <remarks>
    /// Read from <c>ColliderWorld.All</c> and nothing else — a proxy's own centre, kind and size — because
    /// the whole value of the overlay is that it is <em>not</em> derived from what the renderer decided to
    /// draw. A tree whose disc sits half a metre from its trunk, a barn whose box is wider than its walls,
    /// a placement blocker with nothing standing on it: all of those are invisible until something draws
    /// the collider rather than the model.
    /// <para>
    /// Colour says what a thing does rather than what it is, because that is the question being asked of
    /// it: solid ground you cannot walk through, ground you cannot build on, and something you can reach.
    /// Disabled proxies are drawn too, dimmed — a body indoors keeps its four proxies dark, and "where did
    /// that collider go" is exactly the kind of thing this exists to answer.
    /// </para>
    /// <para>
    /// Culled to the tree draw radius. There are nine and a half thousand trunks on a 600 m map and each is
    /// a proxy; drawing all of them would replace the frame budget with an overlay.
    /// </para>
    /// </remarks>
    private void DrawColliderOverlay()
    {
        if (colliderOverlay < 2) return;
        // <b>Frustum-tested and capped, because removing the draw radius made this crash.</b> The overlay
        // shared treeCullBoundSquared, which is now the far plane rather than a disc round the focus — so on a
        // wooded map every collider on the map queued up and overran the prop batch. A hard exception on a
        // debug toggle, which is the worst place for one: the toggle exists to diagnose, and it took the
        // process down instead.
        //
        // The frustum is the right test for the same reason it is right for the trees. The cap is here because
        // a debug overlay must not be able to end the run whatever the frustum contains — twenty thousand
        // wireframe plates were never legible anyway, so what it drops it was not communicating.
        var room = propBuffer.Capacity - propInstances.Count;
        collidersDrawn = 0;
        collidersDropped = 0;
        foreach (var proxy in simulation.Colliders.All)
        {
            // Bodies already draw their own four, in their own colours, when selected.
            if ((proxy.Layer & ColliderLayer.Agent) != 0) continue;
            if (Vector2.DistanceSquared(proxy.Center, cameraFocus) > treeCullBoundSquared) continue;
            if (!InView(proxy.Center, simulation.Terrain.SampleHeight(proxy.Center), 0.5f,
                    MathF.Max(proxy.Shape.Radius, proxy.Shape.HalfExtents.Length())))
            {
                continue;
            }

            if (collidersDrawn >= room)
            {
                collidersDropped++;
                continue;
            }

            collidersDrawn++;

            var role = (proxy.Roles & ColliderRole.MovementSolid) != 0
                ? SolidColliderColor
                : (proxy.Roles & ColliderRole.PlacementBlocker) != 0
                    ? BlockerColliderColor
                    : InteractionColliderColor;
            if (!proxy.Enabled) role *= new Vector4(0.35f, 0.35f, 0.35f, 1f);

            var ground = simulation.Terrain.SampleHeight(proxy.Center) + 0.06f;
            var extent = proxy.Shape.Kind == ColliderShapeKind.Circle
                ? new Vector2(proxy.Shape.Radius)
                : proxy.Shape.HalfExtents;
            propInstances.Add(new InstanceData(
                Matrix4x4.CreateScale(extent.X * 2f, 0.03f, extent.Y * 2f) *
                Matrix4x4.CreateTranslation(proxy.Center.X, ground, proxy.Center.Y),
                role));
        }
    }

    /// <summary>How many collider plates the overlay drew, and how many it had no room for.</summary>
    private int collidersDrawn, collidersDropped;

    /// <summary>
    /// What the fog refused this frame: nodes never offered, and bodies never drawn.
    /// </summary>
    /// <remarks>
    /// Reported because a gate is the one kind of optimisation whose success and whose bug look identical from
    /// the chair — in both cases something is not on the screen. A count that moves with the fog and returns to
    /// zero when the fog is off is the difference between the two.
    /// </remarks>
    private int nodesBehindTheVeil, agentsBehindTheVeil;

    /// <summary>Sizes the fog to the current map, forgetting whatever was known about the last one.</summary>
    /// <remarks>
    /// Called from both the frame update and the canopy rebuild, because either can be the first to touch the
    /// grid on a given frame and an unsized grid has no valid cell to index. Idempotent and a reference
    /// compare, so calling it twice costs nothing.
    /// </remarks>
    private void EnsureFog()
    {
        if (ReferenceEquals(fogWorld, simulation)) return;
        fogWorld = simulation;
        scouted.Resize(simulation.ExtentMeters);
    }

    private void AdvanceFog()
    {
        EnsureFog();
        // Once, and only where it can be read: the mapping is a property of the grid, so it either holds for
        // every cell or for none, and checking it per frame would be three thousand comparisons to learn the
        // same thing twice a second.
        if (!fogMappingChecked)
        {
            fogMappingChecked = true;
            var fault = scouted.MappingFault(simulation.ExtentMeters);
            Console.WriteLine(fault is null
                ? $"  fog: {scouted.Cells}x{scouted.Cells} cells of {FogOfWar.CellMetres:F0} m spanning " +
                  $"{scouted.SpanMetres:F0} m over a {simulation.ExtentMeters:F0} m map; " +
                  "veil and masks agree on every cell"
                : $"  fog: MAPPING FAULT — {fault}");
        }

        scouted.Advance(simulation, fogSettings, frameSeconds);
    }

    private bool fogMappingChecked;

    /// <summary>This frame's delta, so the veil can ease on the same clock everything else does.</summary>
    private float frameSeconds;

    /// <summary>Hands the masks to the GPU, on the frames they changed.</summary>
    /// <remarks>
    /// The masks change once per refresh cycle, so this uploads about once every eight frames rather than
    /// every frame — the dirty flag is the throttle and there is no second one, because the thing that sets
    /// it is the only thing that can change the image.
    /// <para>
    /// The cell count is fixed for the process: every map is built at the one extent this session was
    /// launched with. If that ever stops being true this is where it shows, so it says so rather than
    /// uploading the wrong number of bytes into a texture of the wrong size.
    /// </para>
    /// </remarks>
    private void UploadFogTexture()
    {
        if (!scouted.TexelsDirty) return;
        if (scouted.Cells != fogTextureCells)
        {
            Console.WriteLine(
                $"  fog: grid went from {fogTextureCells} cells to {scouted.Cells} — the texture cannot follow " +
                "it without being recreated, so the veil is now stale. Size it from the largest map instead.");
            scouted.MarkUploaded();
            return;
        }

        // <b>The single most expensive line in the frame, until it stopped draining the queue.</b>
        // UploadTextureMip waits for the whole graphics queue to go idle, which on a mask reuploaded
        // every frame the fog changes meant the frame never overlapped CPU and GPU at all: 20 ms of a
        // 28 ms frame at a wide standoff, and about 7 ms on the frames that happened to find the mask
        // clean. Queued into the frame's own commands, the wait is gone and the veil is a frame late,
        // which is a frame nobody can see at the rate fog eases.
        if (performanceBlockingUpload)
        {
            graphicsDevice.UploadTextureMip(fogTexture, 0, scouted.Texels.ToArray());
        }
        else
        {
            graphicsDevice.QueueTextureUpload(fogTexture, 0, scouted.Texels.ToArray());
        }
        scouted.MarkUploaded();
        fogUploads++;
    }

    private TextureHandle fogTexture;
    private int fogTextureCells;

    /// <summary>How many times the veil has been handed to the GPU, which is the cycle count.</summary>
    private int fogUploads;

    /// <summary>
    /// Paints every fog cell in the colour of its tier.
    /// </summary>
    /// <remarks>
    /// <b>The instrument, and it exists before anything reads the masks.</b> §73: three cascades were four
    /// rounds of screenshots until a debug tint answered it in one, and fog is the worse case — correct fog
    /// and broken fog both look like dark ground from the chair, so there is no version of "watch it and see"
    /// that works here.
    /// <para>
    /// <b>Opaque, so the tint replaces the ground rather than multiplying it.</b> §73's other lesson, and it
    /// comes free here: alpha is coverage for terrain and one for everything else, and these plates are
    /// props. Multiplied, an unexplored blue over grass would be a plausible dusk and the question "is this
    /// cell unexplored" would be a judgement about a hue.
    /// </para>
    /// <para>
    /// <b>Frustum-tested and capped, because a debug overlay must not be able to end the run.</b> Learnt
    /// the hard way one commit ago, when the collider overlay lost its bound and took the process down on a
    /// keystroke — the worst possible place for a hard exception, since the toggle exists to diagnose.
    /// </para>
    /// <para>
    /// Plates are drawn a little short of the cell so the gutters show the grid. That is not decoration:
    /// the gate that will read these masks works a whole cell at a time, and seeing the quantum is seeing
    /// what the gate can actually resolve.
    /// </para>
    /// </remarks>
    private void DrawFogOverlay()
    {
        fogCellsDrawn = 0;
        fogCellsDropped = 0;
        if (!fogSettings.ShowCells || scouted.Cells == 0) return;

        var room = propBuffer.Capacity - propInstances.Count;
        var side = FogOfWar.CellMetres * 0.86f;
        var total = scouted.Cells * scouted.Cells;
        for (var index = 0; index < total; index++)
        {
            var centre = scouted.CentreOf(index);
            if (Vector2.DistanceSquared(centre, cameraFocus) > treeCullBoundSquared) continue;
            var ground = simulation.Terrain.SampleHeight(centre);
            if (!InView(centre, ground, 0.5f, FogOfWar.CellMetres)) continue;

            if (fogCellsDrawn >= room)
            {
                fogCellsDropped++;
                continue;
            }

            fogCellsDrawn++;
            propInstances.Add(new InstanceData(
                Matrix4x4.CreateScale(side, 0.03f, side) *
                Matrix4x4.CreateTranslation(centre.X, ground + 0.35f, centre.Y),
                scouted.TierOf(index) switch
                {
                    FogTier.Visible => FogVisibleColor,
                    FogTier.Remembered => FogRememberedColor,
                    _ => FogUnexploredColor,
                }));
        }
    }

    /// <summary>How many fog plates the overlay drew, and how many it had no room for.</summary>
    private int fogCellsDrawn, fogCellsDropped;

    // Three hues chosen to be nothing the ground ever is, for the reason §73 records: a debug colour that
    // could be mistaken for the scene is not telling you anything, and this scene already contains dark
    // ground, dim ground and lit ground.
    private static readonly Vector4 FogUnexploredColor = new(0.05f, 0.06f, 0.20f, 1f);
    private static readonly Vector4 FogRememberedColor = new(0.55f, 0.13f, 0.52f, 1f);
    private static readonly Vector4 FogVisibleColor = new(0.16f, 0.78f, 0.80f, 1f);

    /// <summary>
    /// The distances that are supposed to agree with each other, on one line.
    /// </summary>
    /// <remarks>
    /// <b>Because "geometries that should tie together don't" is a feeling until it is arithmetic.</b> Four
    /// numbers describe how far this game can see, and each is chosen independently: how far the camera is
    /// from the ground, how far out trees are drawn, how wide the sun's shadow box is, and where the fog
    /// starts. A tree drawn beyond the shadow box casts nothing; a shadow box far wider than the view
    /// spends its texels on ground nobody is looking at, and the texel size is what shadow quality
    /// <em>is</em>. Printed together, a mismatch is a number rather than a suspicion.
    /// </remarks>
    private string GeometryLine()
    {
        // <b>Against the visible ground radius, not against the camera's standoff.</b> Comparing the box
        // to <c>cameraDistance</c> was this line's own first bug: it reported the box as 3.3x oversized when
        // the box is a full width, the standoff is not a radius, and the honest comparison said 1.16x at
        // that zoom and 0.69x — too small — at the far one. A tie-together line whose terms are not
        // commensurable invents mismatches and hides real ones.
        var seen = Sees;
        // <b>The last cascade's reach and each cascade's texel</b>, which are the numbers a cascaded map lives
        // or dies by: how far anything is shadowed at all, and how sharp it is at each of the three bands.
        var shadowReach = cascadeEdges[ShadowCascades.Count];
        var texels =
            $"{cascadeSideMetres[0] / CascadeMapSize(0) * 100f:F1}/" +
            $"{cascadeSideMetres[1] / CascadeMapSize(1) * 100f:F1}/" +
            $"{cascadeSideMetres[2] / CascadeMapSize(2) * 100f:F1}";
        return
            $"SEES {seen:F0} m (reach {VisibleReach:F0}) · DETAIL {DetailRadius:F0} m " +
            $"({DetailRadius / seen:F2}x) · " +
            // <b>The two numbers that have to agree, side by side.</b> A tree drawn past where the cascades
            // reach stands in flat light, so TREES exceeding SHADOW is ground drawn unshadowed — which is now
            // expected rather than a bug, because the cascades cover the depth the texel budget affords and
            // the far plane covers everything the frustum can hold. What the line is for is the size of the
            // gap: a little is fog, and a lot is a wood standing in flat light.
            // <b>The band's near edge leads, because it is the number that says whether cascade 0 is used at
            // all.</b> The splits alone cannot tell you: 85/137/198 looks healthy whether the nearest visible
            // ground is at 37 m — a third of the frame in the near cascade — or at 140 m, in which case the
            // near cascade is shading empty air and this reads exactly the same.
            $"SHADOW {GroundBand.Near:F0}>{cascadeEdges[1]:F0}>{cascadeEdges[2]:F0}>" +
            $"{shadowReach:F0} m (texels {texels} cm) · " +
            $"TREES {MathF.Sqrt(treeCullBoundSquared):F0} m · " +
            $"FOG {sentFog.X:F0}-{sentFog.Y:F0} m at {sentFog.Z:F2} · " +
            // What the dressing is spending, on the same line as what can be seen, because both of the
            // questions they answer are "is this frame drawing more than it needs to".
            $"FRAME {frameMilliseconds:F1} ms · " +
            $"BUILD ground {buildPhases.Terrain:F1} nodes {nodeMilliseconds:F1} agents {buildPhases.Agents:F1} " +
            $"scatter {buildPhases.Scatter:F1} overlay {buildPhases.Overlay:F1} stage {buildPhases.Stage:F1} " +
            $"record {buildPhases.Record:F1} ms · " +
            $"LOAD {stagedLoad.Instances:N0} instances = {stagedLoad.Triangles / 1000L:N0}k triangles " +
            $"+ {stagedLoad.Casters / 1000L:N0}k cast · " +
            // <b>Per cascade, and the sun mode beside it, because the question is whether a mode pays for
            // shadows it does not use.</b> §144. These three numbers were already recorded for the
            // performance cases and were not visible from the chair, which is where the sun modes are judged.
            // What they establish: caster staging is fitted to the camera and the cascade boxes and has
            // nothing to do with where the sun is — so the figures should be identical across the three
            // modes, and a difference between them would be a real fault rather than a tuning question.
            $"CASTERS {stagedCasterInstances[0]:N0}/{stagedCasterInstances[1]:N0}/" +
            $"{stagedCasterInstances[2]:N0} = " +
            $"{stagedCasterTriangles[0] / 1000L:N0}k/{stagedCasterTriangles[1] / 1000L:N0}k/" +
            $"{stagedCasterTriangles[2] / 1000L:N0}k tri · " +
            $"SUN {look.Motion} " +
            $"{(look.SunFollowsTheYear ? $"{sky.SunElevationDegrees:F0}°" : "pinned")} · " +
            (colliderOverlay >= 2
                ? $"COLLIDERS {collidersDrawn:N0} drawn" +
                  (collidersDropped > 0 ? $", {collidersDropped:N0} over budget · " : " · ")
                : string.Empty) +
            $"STONE {outcropsDrawn} of {outcropsSeen} outcrops drawn · " +
            $"TREENODES {treeNodesAlive} alive, {treesOffered} offered, {treesOutOfView} out of view " +
            $"({treesNearRejected} of them within 20 m of the eye), " +
            $"{treesDrawn} drawn in " +
            $"{treeTicks * 1000.0 / Stopwatch.Frequency * treesOffered / MathF.Max(1, treeSamples):F1} ms " +
            $"(from {treeSamples} samples) " +
            $"(undergrowth {undergrowthTicks * 1000.0 / Stopwatch.Frequency:F1} ms) · " +
            $"TREES {treeTiers.Near}/{treeTiers.Mid}/{treeTiers.Far}/{treeTiers.Deep} near/mid/far/deep over " +
            $"{look.TreeCrowdMid:F0}/{look.TreeCrowdFar:F0} per {CanopyCellMetres:F0} m · " +
            $"{undergrowthDrawn} under · " +
            // <b>Called SCOUTED and not FOG, because this line already has a FOG and it is the haze.</b> Two
            // unrelated things under one label on the one line whose entire purpose is catching numbers that
            // disagree would be a good way to spend an evening comparing a view distance against a cell count.
            //
            // Counted per tier because "the map is dark" is what working fog and broken fog both look like —
            // and the three counts sum to the cell total, so a sum that does not is the instrument telling you
            // it is lying rather than you finding out later. The cost is on the same line for the reason §74
            // ended up needing it: the refresh is round-robin precisely to keep off this frame's budget, and a
            // budget nobody prints is a budget nobody notices being exceeded.
            $"SCOUTED {scouted.Counts.Visible:N0} seen + {scouted.Counts.Remembered:N0} known + " +
            $"{scouted.Counts.Unexplored:N0} dark of {scouted.Cells * scouted.Cells:N0} cells at " +
            $"{FogOfWar.CellMetres:F0} m ({(fogSettings.Enabled ? "on" : "off")}, " +
            $"{scouted.SeededCells:N0} known at start, " +
            $"{scouted.WatcherCount.Bodies} bodies + {scouted.WatcherCount.Buildings} buildings watching, " +
            $"{scouted.QueriesLastFrame:N0} asks in " +
            $"{scouted.MillisecondsLastFrame:F2} ms, cycle asked {scouted.LastCycle.Asked:N0} granted " +
            $"{scouted.LastCycle.Granted:N0}, widest reach {scouted.LastCycle.WidestReach:F0} m) · " +
            // Zero with the fog off, which is the check that the gate is a consequence of the fog and not a
            // second opinion about what is worth drawing.
            $"VEILED {nodesBehindTheVeil:N0} nodes + {agentsBehindTheVeil:N0} bodies not offered" +
            (fogSettings.ShowCells
                ? $" · FOGCELLS {fogCellsDrawn:N0} drawn" +
                  (fogCellsDropped > 0 ? $", {fogCellsDropped:N0} over budget" : string.Empty)
                : string.Empty) +
            " · " +
            $"GROUNDSUBMIT {groundLayersDrawn} layers in {groundSubmitMs:F2} ms · " +
            $"GROUND {drawnChunks.Count} of {groundChunks.Count} chunks, " +
            $"{drawnChunks.Sum(chunk => groundChunks[chunk].Count(layer => !layer.Blend && !layer.Water))} coats " +
            $"+ {blendedChunks.Sum(chunk => groundChunks[chunk].Count(layer => layer.Blend))} blends " +
            $"+ {drawnChunks.Sum(chunk => groundChunks[chunk].Count(layer => layer.Water))} water at " +
            $"{GroundRenderStep * simulation.Navigation.Transform.CellSize:F1} m · " +
            $"WOODASK {woodlandAsked + WoodlandCover.Asked:N0} · " +
            $"REBUILDS {terrainRebuilds} · " +
            $"CONTACT {contactInstances.Count} · " +
            // <b>The eye's height above the ground, not above the origin.</b> The distinction is the whole of
            // the focus-datum bug: at the village these differed by forty-eight metres, and every reach on
            // this line is derived from the standoff as though it were height over terrain.
            $"EYE {MathF.Sin(CameraElevation) * cameraDistance:F1} m over ground " +
            $"(focus at {cameraGroundHeight:F1} m, standoff {cameraDistance:F0} m) · " +
            $"HEIGHT {simulation.Terrain.SampleHeight(cameraFocus):F1} m " +
            $"(grade {simulation.Terrain.SampleGrade(cameraFocus):F2}) · " +
            $"NIGHT {sky.Nightness:F2} (lights {habitationLights}) · " +
            $"SMOKE {hearths.Drawn} from {hearths.Chimneys}";
    }

    private void AddColliderDisc(ColliderId id, Vector2 position, float height, Vector4 color)
    {
        var collider = simulation.Colliders.Get(id);
        if (collider.Shape.Kind != ColliderShapeKind.Circle) return;
        var bodyScale = collider.Shape.Radius / AgentDefaults.Radius;
        var model = Matrix4x4.CreateScale(bodyScale, 0.035f, bodyScale) *
                    Matrix4x4.CreateTranslation(position.X, height, position.Y);
        unitInstances.Add(new InstanceData(model, color));
    }

    public void OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        if (performanceRun) return;
        // Middle-drag grabs the ground. Both ends of the drag are raycast against the terrain with the
        // <em>same</em> camera, so the world moves exactly as far under the cursor as the cursor moved —
        // which is the only version of this that feels like dragging a map rather than nudging a dial.
        // Approximating it as "screen pixels times some function of the zoom" is off by the perspective
        // and drifts under the cursor as you drag toward the horizon.
        if (draggingCamera &&
            TryScreenToGround(x - deltaX, y - deltaY, out var from) &&
            TryScreenToGround(x, y, out var to))
        {
            cameraFocus = simulation.Terrain.ClampPosition(cameraFocus - (to - from));
        }

        mouseX = x;
        mouseY = y;
        selection.Update(x, y);
        UpdatePointerWorld();
    }

    public void OnMouseDown(MouseButton button)
    {
        if (performanceRun) return;
        if (obstacleEditMode)
        {
            if (button == MouseButton.Left && pointerOnTerrain) simulation.QueueToggleObstacle(pointerWorld);
            return;
        }

        if (button == MouseButton.Middle)
        {
            draggingCamera = true;
        }
        else if (button == MouseButton.Left)
        {
            selection.Begin(mouseX, mouseY);
        }
        else if (button == MouseButton.Right)
        {
            UpdatePointerWorld();
            IssueContextCommand();
        }
    }

    public void OnMouseUp(MouseButton button)
    {
        if (performanceRun) return;
        // <b>Where the pointer thinks it is, in every space at once.</b> Reported from the chair as the mouse
        // not lining up with the screen, and it is not answerable by reading: every link in the chain is
        // self-consistent — the marquee, the ground raycast and ImGui all pair the cursor with the window's
        // logical size — <em>provided the cursor is delivered in logical points</em>. On a HiDPI display, if
        // the window library reports it in physical pixels instead, everything downstream is wrong by the
        // scale factor and every piece of it looks correct in isolation.
        //
        // So: click the bottom-right corner. If the pointer reads near the frame size rather than near the
        // logical size, the cursor is in physical pixels and that is the bug.
        if (timingDebug)
        {
            var (lw, lh) = host.LogicalSize;
            Console.WriteLine(
                $"  pointer ({mouseX:F0}, {mouseY:F0}) · logical {lw}x{lh} · frame " +
                $"{windowSize.Width}x{windowSize.Height} · scale {windowSize.Width / (float)MathF.Max(1, lw):F2} " +
                $"· on terrain {pointerOnTerrain} at ({pointerWorld.X:F1}, {pointerWorld.Y:F1})");
        }

        // <b>Why the thing you clicked is or is not on screen.</b> Three theories about ghost markers were
        // wrong — the tree draw radius, stale instance lists, a species failing to load — and each cost a round
        // trip to the chair. This prints the whole drawing story of whatever was picked, so the next answer
        // comes from one click instead of a guess.
        if (timingDebug && hoveredNode.IsValid && simulation.Nodes.Contains(hoveredNode))
        {
            ref readonly var it = ref simulation.Nodes.Get(hoveredNode);
            var ground = simulation.Terrain.SampleHeight(it.Position);
            var away = Vector2.Distance(it.Position, cameraFocus);
            var species = it.Kind == NodeKind.Tree ? TreeKindAt(it.Position, it.Id.Value) : -1;
            var tier = it.Kind == NodeKind.Tree ? CanopyTierAt(it.Position) : -1;
            var pressure = simulation.Terrain.Woodland?.At(it.Position) ?? -1f;
            Console.WriteLine(
                $"  picked {it.Kind} #{it.Id.Value} at ({it.Position.X:F1}, {it.Position.Y:F1}) " +
                $"ground {ground:F2} m, {away:F0} m from focus (tree radius {MathF.Sqrt(treeCullBoundSquared):F0}) " +
                $"· species {species} tier {tier} pressure {pressure:F2} " +
                $"· stock {it.Stock.Wood}w/{it.Stock.Stone}s · drawn-test {IsDrawnThisFrame(in it)} " +
                $"· pointer at ({pointerWorld.X:F1}, {pointerWorld.Y:F1}), {Vector2.Distance(it.Position, pointerWorld):F1} m away");
        }

        if (button == MouseButton.Middle) draggingCamera = false;
        if (obstacleEditMode) return;
        if (button != MouseButton.Left) return;
        selection.Update(mouseX, mouseY);
        var (width, height) = host.LogicalSize;
        selection.End(
            simulation.Agents,
            simulation.Terrain.SampleHeight,
            camera.GetViewProjection(aspect),
            width,
            height,
            additiveSelection,
            PlayerFaction);

        // <b>A click that caught no bodies, on something on the ground, picks that instead.</b> Ordered after
        // the marquee on purpose: dragging a box is about units and must not be hijacked, and a drag that
        // selected units has already answered the question. Only a click that came up empty falls through to
        // the map.
        if (selection.Selected.Count > 0)
        {
            selectedNode = NodeId.None;
            return;
        }

        selectedNode = hoveredNode;
        if (selectedNode.IsValid)
        {
            ref readonly var picked = ref simulation.Nodes.Get(selectedNode);
            Console.WriteLine($"  picked {picked.Kind} at ({picked.Position.X:F0}, {picked.Position.Y:F0})");
        }
    }

    public void OnMouseWheel(float offsetX, float offsetY)
    {
        if (performanceRun) return;
        // Zoom steps proportionally, so pulling back over a kilometre does not take a
        // hundred notches of wheel that were sized for a thirty-metre square.
        var step = MathF.Max(2f, cameraDistanceTarget * 0.12f);
        cameraDistanceTarget = Math.Clamp(
            cameraDistanceTarget - offsetY * step,
            CameraNearestDistance,
            cameraFurthest);
    }

    private AgentId? FindNearestUnselectedTarget()
    {
        var selected = selection.Snapshot();
        if (selected.Length == 0) return null;
        var center = Vector2.Zero;
        foreach (var id in selected) center += simulation.Agents.Get(id).Position;
        center /= selected.Length;

        AgentId? nearest = null;
        var nearestDistance = float.PositiveInfinity;
        foreach (ref readonly var agent in simulation.Agents.All)
        {
            if (!agent.IsAlive || selection.Contains(agent.Id)) continue;
            var distance = Vector2.DistanceSquared(center, agent.Position);
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearest = agent.Id;
        }
        return nearest;
    }

    private void IssueTargetBehavior(Action<AgentId[], AgentId> issue, string label)
    {
        var selected = selection.Snapshot();
        var target = FindNearestUnselectedTarget();
        if (selected.Length == 0 || target is not { } targetId)
        {
            Console.WriteLine($"  {label}: select units and leave at least one unselected target");
            return;
        }
        issue(selected, targetId);
        Console.WriteLine($"  {label}: {selected.Length} unit(s) -> {targetId}");
    }

    public void OnKeyDown(Key key)
    {
        if (performanceRun && key != Key.Escape) return;
        // <b>Every key that arrives, and whether a modifier was up when it did.</b> Whether Ctrl+letter reaches
        // a game is not answerable by reading: the mapping table can be correct, the dispatch unfiltered, and
        // the combination still swallowed by the window library or the OS. One line per press settles it, and
        // it is behind --debug-all so it costs nothing the rest of the time.
        if (timingDebug) Console.WriteLine($"  key {key}{(additiveSelection ? " (ctrl held)" : string.Empty)}");

        switch (key)
        {
            case Key.Q when mapLab:
                RollLab(-1, reseed: false);
                break;
            case Key.E when mapLab:
                RollLab(1, reseed: false);
                break;
            case Key.Y when mapLab:
                RollLab(0, reseed: true);
                break;
            case Key.I when mapLab:
                labRegion = RegionProfile.All[
                    (Array.IndexOf(RegionProfile.All, labRegion) + 1) % RegionProfile.All.Length];
                RollLab(0, reseed: false);
                break;
            case Key.U when mapLab:
                labPinned = !labPinned;
                Console.WriteLine(
                    labPinned
                        ? $"  pinned to {labArchetype} — every neighbourhood the same statement"
                        : "  mixed — every neighbourhood its own statement");
                RollLab(0, reseed: false);
                break;
            case Key.Up when mapLab:
                MoveWindow(0f, -1f);
                break;
            case Key.Down when mapLab:
                MoveWindow(0f, 1f);
                break;
            case Key.Left when mapLab:
                MoveWindow(-1f, 0f);
                break;
            case Key.Right when mapLab:
                MoveWindow(1f, 0f);
                break;
            case Key.Enter when mapLab:
                ReportPick();
                break;
            case Key.O when mapLab:
                SurveyWindows();
                break;
            // <b>Rolling a new map while playing, on the modifier that already means "the other thing".</b>
            // Every letter is taken in the village — Q and E turn the camera, Y unassigns — and Ctrl is the
            // established way to say a second meaning here: Ctrl+A places a house where A places a field.
            //
            // Worth having at all because the generator is now one generator. The lab and the village build
            // their ground the same way, so "show me another map" is the same question in both, and answering
            // it only in the tool that cannot be played was an accident of which one was written first.
            // <b>Space, and only Space, because the panel owns the rest.</b> Which archetype and which region
            // are parameters, not actions: they are chosen once and then rolled against, so they belong on the
            // tuning panel where a value can be seen as well as changed. Cycling them from keys as well left
            // two writers for one piece of state — the roll below reads the sliders, so a key that stepped the
            // archetype would have been overwritten by the panel on the very next line.
            //
            // That leaves one action, and it gets the one free single key. Ctrl+letter is mapped and forwarded
            // and ought to work, but "ought to" is doing a lot of work in that sentence: modifier delivery
            // depends on the window library and on what the OS keeps for itself. (Shift was not in the
            // <c>Key</c> enum at all when this was written; §108 added it, along with the digits, and the
            // same caution applies to it.) Rolling a map is the thing somebody presses fifty times in a row,
            // and it should not rest on a combination I cannot verify by reading the source.
            case Key.Space when !mapLab:
                RollLab(0, reseed: true);
                break;
            case Key.Escape:
                if (obstacleEditMode)
                {
                    obstacleEditMode = false;
                    Console.WriteLine("  block-edit mode: OFF");
                }
                else host.RequestClose();
                break;
            case Key.B:
                obstacleEditMode = !obstacleEditMode;
                Console.WriteLine($"  block-edit mode: {(obstacleEditMode ? "ON" : "OFF")}");
                break;
            case Key.N:
                navigationDebugMode = (navigationDebugMode + 1) % 5;
                Console.WriteLine($"  terrain overlay: {navigationDebugMode switch { 1 => "navigation", 2 => "surface cost", 3 => "slope", 4 => "congestion pressure", _ => "off" }}");
                break;
            case Key.C:
                colliderOverlay = (colliderOverlay + 1) % 3;
                Console.WriteLine(
                    "  collider overlay: " + colliderOverlay switch
                    {
                        1 => "selected bodies",
                        2 => "everything — trees, buildings, walls and bodies, drawn from their colliders",
                        _ => "off",
                    });
                break;
            case Key.V:
                velocityDebug = !velocityDebug;
                Console.WriteLine($"  velocity debug: {(velocityDebug ? "ON" : "OFF")} (green preferred, pink resolved)");
                break;
            case Key.K:
                pathDebug = !pathDebug;
                Console.WriteLine($"  path debug: {(pathDebug ? "ON" : "OFF")} (selected units)");
                break;
            case Key.I:
                stateDebug = !stateDebug;
                Console.WriteLine($"  state debug: {(stateDebug ? "ON" : "OFF")} (idle grey, move orange, follow green, patrol purple, chase red, flee blue)");
                break;
            case Key.M:
                movementTrace = movementTrace is null ? new LiveMovementTrace() : null;
                Console.WriteLine($"  movement trace: {(movementTrace is null ? "OFF" : "ON")}");
                break;
            case Key.T:
                timingDebug = !timingDebug;
                nextTimingReport = 0;
                Console.WriteLine($"  timing counters: {(timingDebug ? "ON" : "OFF")}");
                break;
            case Key.G:
                stressScenarioIndex = (stressScenarioIndex + 1) % StressScenarioCounts.Length;
                LoadStressScenario(StressScenarioCounts[stressScenarioIndex]);
                break;
            case Key.J:
                LoadPenEscapeScenario();
                break;
            case Key.L:
                LoadTerrainScenario();
                break;
            case Key.Z:
                cameraFollowsSelection = !cameraFollowsSelection;
                Console.WriteLine(
                    $"  camera follows selection: {(cameraFollowsSelection ? "ON" : "OFF")}");
                break;
            case Key.R:
                cameraFocus = Vector2.Zero;
                Console.WriteLine("  camera recentred");
                break;
            case Key.S:
                simulation.QueueStop(selection.Snapshot());
                break;
            case Key.Backspace:
                var removed = simulation.DespawnAgents(selection.Snapshot());
                selection.Clear();
                Console.WriteLine($"  despawned {removed} unit(s); {simulation.Agents.LiveCount} remain");
                break;
            case Key.F:
                IssueTargetBehavior(simulation.QueueFollow, "follow");
                break;
            case Key.P:
                if (pointerOnTerrain) simulation.QueuePatrol(selection.Snapshot(), pointerWorld);
                break;
            case Key.H:
                IssueTargetBehavior(simulation.QueueChase, "chase");
                break;
            case Key.X:
                IssueTargetBehavior(simulation.QueueFlee, "flee");
                break;
            case Key.U:
                AssignPost();
                break;
            case Key.O:
                // A route is a shuttle with cargo, so it is the shuttle key with a modifier — the same
                // shape as Ctrl+A placing a house instead of a farm. Every letter on the board is taken.
                if (additiveSelection) AssignRoute();
                else AssignShuttle();
                break;
            case Key.Y:
                simulation.QueueAssign(selection.Snapshot(), Assignment.None);
                shuttleAnchor = null;
                routeSource = null;
                Console.WriteLine($"  {selection.Selected.Count} unit(s) taken off work");
                break;
            case Key.D:
                if (additiveSelection) Build(NodeKind.Barracks, capacity: 0, Resource.Wood);
                else Build(NodeKind.Granary, capacity: 2000, Resource.Grain);
                break;
            case Key.A:
                // Ctrl to place the sink rather than the source: houses are the only thing in the
                // economy that consumes, so they want a key, and every letter was already taken.
                if (additiveSelection) Build(NodeKind.House, capacity: 0, Resource.Grain, occupancy: 4);
                else Build(NodeKind.Farm, capacity: 150, Resource.Grain);
                break;
            case Key.W:
                // A lumber camp is a forward depot at the tree line. It is not a special building: it
                // is a store, and the reason it works is that nobody lives near it — so the wood in it
                // is stranded, and stranded stock is what the hauling board collects.
                if (additiveSelection) Build(NodeKind.PalisadeWall, capacity: 0, Resource.Wood);
                else Build(NodeKind.ForwardDepot, capacity: 400, Resource.Wood);
                break;
            case Key.Tab:
                SelectSpareHands();
                break;
            case Key.LeftControl:
            case Key.RightControl:
                additiveSelection = true;
                break;
            case Key.LeftShift:
            case Key.RightShift:
                shiftHeld = true;
                break;
            // <b>The digits, which the Key enum did not have.</b> Not an oversight worth working around: a
            // control group is bound to a number in every game that has ever had one, and there was no way
            // to say "4" at all. Added to the enum and to the Silk mapping, number row and keypad alike.
            case >= Key.Number0 and <= Key.Number9:
                Crew(key - Key.Number0);
                break;
            // Rotation is Q and E; the arrows pan. They used to do both, which meant there was no way to
            // move the camera sideways at all and pressing Left to look left spun the world instead.
            case Key.Q:
                turnLeft = true;
                break;
            case Key.E:
                turnRight = true;
                break;
            case Key.Left:
                panLeft = true;
                break;
            case Key.Right:
                panRight = true;
                break;
            case Key.Up:
                panUp = true;
                break;
            case Key.Down:
                panDown = true;
                break;
        }
    }

    public void OnKeyUp(Key key)
    {
        if (performanceRun) return;
        if (key is Key.LeftControl or Key.RightControl) additiveSelection = false;
        if (key is Key.LeftShift or Key.RightShift) shiftHeld = false;
        // Held rather than edge-triggered: key events fire once, and a pan has to keep going for as long
        // as the key is down, so the state lives here and PanCamera reads it every frame.
        if (key == Key.Left) panLeft = false;
        if (key == Key.Right) panRight = false;
        if (key == Key.Up) panUp = false;
        if (key == Key.Down) panDown = false;
        if (key == Key.Q) turnLeft = false;
        if (key == Key.E) turnRight = false;
    }

    /// <summary>
    /// Prints the sealed run's epilogue, once, wherever the run happens to end.
    /// </summary>
    /// <remarks>
    /// Called both from the frame limit and from teardown on purpose: a run closed by Escape or by the window
    /// going away is still a run somebody was measuring, and an instrument that only reports when the exit was
    /// the expected one teaches you to distrust the exits.
    /// </remarks>
    /// <summary>Cumulative GPU pass totals as they stood when the steady window opened.</summary>
    /// <remarks>
    /// Snapshot-and-subtract rather than a reset, because the device's accumulator has other readers and a
    /// measurement that clears shared state is a measurement that breaks the next one.
    /// </remarks>
    private Dictionary<string, (double TotalMs, long Samples)>? gpuBaseline;

    private void CaptureGpuBaseline()
    {
        if (vk is null) return;
        gpuBaseline = new Dictionary<string, (double, long)>(vk.GpuPassTotals);
    }

    /// <summary>Mean GPU milliseconds per pass over the steady window, heaviest first.</summary>
    private List<(string Pass, double MeanMs)> GpuPassMeans()
    {
        var means = new List<(string, double)>();
        if (vk is null || gpuBaseline is null) return means;
        foreach (var (pass, now) in vk.GpuPassTotals)
        {
            var before = gpuBaseline.TryGetValue(pass, out var found) ? found : (TotalMs: 0.0, Samples: 0L);
            var samples = now.Samples - before.Samples;
            if (samples <= 0) continue;
            means.Add((pass, (now.TotalMs - before.TotalMs) / samples));
        }

        means.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return means;
    }

    private void ReportPerformanceRun() =>
        performance?.Report(
            simulation.Timings.AverageOf(SimulationPhase.TotalTick),
            simulation.Agents.Count,
            GpuPassMeans(),
            vk?.GpuTimestampsSupported ?? false);

    public void Dispose()
    {
        ReportPerformanceRun();
        hud?.Dispose();
        selectionUi?.Dispose();
        if (selectionPixel.Id >= 0) graphicsDevice?.DestroyTexture(selectionPixel);
        overlayBuffer?.Dispose();
        unitBuffer?.Dispose();
        propBuffer?.Dispose();
        if (propCasterBuffers is not null)
        {
            foreach (var buffer in propCasterBuffers) buffer?.Dispose();
        }
        if (unitCasterBuffers is not null)
        {
            foreach (var buffer in unitCasterBuffers) buffer?.Dispose();
        }
        canopyBuffer?.Dispose();
        if (canopyCasterBuffers is not null)
        {
            foreach (var buffer in canopyCasterBuffers) buffer?.Dispose();
        }
        if (vk is not null)
        {
            foreach (var chunk in groundChunks.Keys.ToArray()) DisposeGroundChunk(chunk);

        }
        // The graph owns render passes and offscreen images that are not in the device's auto-freed
        // tables, and Dispose runs after WaitIdle, which is the only safe place to free them.
        art?.Dispose();
        bodies?.Dispose();
        fullscreen?.Dispose();
        graph?.Dispose();
    }
}
