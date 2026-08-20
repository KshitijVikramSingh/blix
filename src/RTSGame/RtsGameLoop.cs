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
using RTSGame.Control;
using RTSGame.Debug;
using RTSGame.Rendering;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame;

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
    /// </remarks>
    public const float DefaultWorldExtentMeters = 600f;

    /// <summary>Sim seconds per wall-clock second the game starts at.</summary>
    /// <remarks>
    /// 1.5, which is what §3 proposed and what judging it on the slider settled on: 1x is
    /// unbearably slow to sit through, 2x reads as agitated, and 1.5 is the one that felt like
    /// the world running at its own pace rather than at the player's. It is a tick-rate
    /// multiplier and not a speed multiplier, so nothing about the body or the geometry moves
    /// with it — only how long the waiting takes in the chair.
    /// </remarks>
    public const float DefaultCompression = 1.5f;

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
    private static readonly Vector4 SelectionColor = new(0.26f, 0.86f, 0.94f, 1f);
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
    private static readonly Vector4 HouseColor = new(0.68f, 0.58f, 0.46f, 1f);
    private static readonly Vector4 HouseRoofColor = new(0.44f, 0.22f, 0.16f, 1f);
    // The pack's own Wheat (0.384, 0.300, 0.065) and Wood (0.246, 0.144, 0.054), so a load on a
    // villager's back is the same colour as the crop it came from and the timber it was cut from.
    private static readonly Vector4 GrainColor = new(0.384f, 0.300f, 0.065f, 1f);
    private static readonly Vector4 WoodColor = new(0.246f, 0.144f, 0.054f, 1f);
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
    private static readonly Vector4 RoughColor = new(0.230f, 0.190f, 0.115f, 1f);
    private static readonly Vector4 MudColor = new(0.125f, 0.098f, 0.062f, 1f);
    private static readonly Vector4 ImpassableColor = new(0.071f, 0.213f, 0.246f, 1f);

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
    private InstancedBatch groundBatch = null!;
    private InstanceBuffer groundBuffer = null!;
    // Per-cell detail gets its own batch: the coarse ground alone is fourteen thousand
    // instances and the ceiling is sixteen, so sharing one leaves no room for the thing the
    // detail exists to draw.
    private InstancedBatch detailBatch = null!;
    private InstanceBuffer detailBuffer = null!;
    // The ground does not change between frames and was being rebuilt from scratch on every
    // one of them: fourteen thousand blocks, each with a bilinear surface and height sample.
    // That is 18 ms of a 16 ms frame spent redrawing a field that had not moved.
    private readonly List<InstanceData> groundInstances = new();
    private readonly List<InstanceData> detailInstances = new();
    private int groundTerrainRevision = -1;
    private InstancedBatch unitBatch = null!;
    private readonly List<TerrainSurfaceLayer> terrainSurfaceLayers = new();
    private VulkanGraphicsDevice vk = null!;
    private ShaderProgramHandle worldShader;
    private PipelineHandle worldPipeline;

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
    private GraphResourceHandle hdrHandle, hdrMsaaHandle, sceneDepthHandle, sunShadowHandle;
    private PassHandle shadowPassHandle, scenePassHandle;
    private PipelineHandle casterPipeline, skyPipeline, presentPipeline;
    private ShaderProgramHandle casterShader;
    private FullscreenPass fullscreen = null!;

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
    private InstancedBatch propBatch = null!, propCaster = null!, unitCaster = null!;
    private InstancedBatch canopyBatch = null!, canopyCaster = null!;
    private InstanceBuffer propBuffer = null!, propCasterBuffer = null!, unitCasterBuffer = null!;
    private InstanceBuffer canopyBuffer = null!, canopyCasterBuffer = null!;

    // viewProj, camPos, sunDir, sunVP, fog, shadow, light, haze, sunTint, skyAmbient, groundAmbient,
    // hazeAway, hazeToward, wind. Every one of the last five palette entries used to be a constant in
    // world.frag, which is why a year looked like one afternoon — see Rendering/Atmosphere.cs. This length
    // is also the declared push-constant range, in OnLoad, so the two cannot drift apart.
    private readonly byte[] worldPush = new byte[320];
    private readonly byte[] skyPush = new byte[128];     // invViewProj, camPos, sunDir, zenith, horizon
    private readonly byte[] shadowPush = new byte[64];   // sun shadow VP
    private readonly byte[] gradePush = new byte[32];    // grade, then the eye's night response

    /// <summary>How far from the camera's focus a tree is still drawn, squared.</summary>
    private float treeDrawRadiusSquared = 1f;

    /// <summary>Ground colouring the built instances were made with, so a slider forces a rebuild.</summary>
    private Vector2 groundLookApplied = new(-1f, -1f);

    /// <summary>Side of the sun's shadow map, in texels.</summary>
    private const int ShadowMapSize = 2048;

    /// <summary>Samples per pixel in the scene pass.</summary>
    /// <remarks>
    /// Four. Two is visibly not enough on a scene made entirely of hard geometric edges, and eight buys
    /// almost nothing over four for four times the bandwidth on a frame that is already resolving a
    /// 16-bit-per-channel target.
    /// </remarks>
    private const int MsaaSamples = 4;

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
    private float VisibleGroundRadius
    {
        get
        {
            var (width, height) = host.LogicalSize;
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
        look.DetailCeilingMetres,
        MathF.Max(look.ShadowFloorMetres * 0.5f, VisibleGroundRadius + ShadowReachMetres));

    private float SunOrthoExtent => 2f * DetailRadius;

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
    private const float CameraNearestDistance = 8f;

    /// <summary>How far back the camera may stand.</summary>
    /// <remarks>
    /// Enough to hold the settlement and its whole tree line, and no more. It was the map's own extent
    /// times 1.35 — eight hundred metres — which is a viewpoint from which a villager is a subpixel and
    /// which, with a fixed far plane, showed nothing at all.
    /// </remarks>
    // Reported as still slightly too far. Past this the settlement is a smudge in the middle of a green
    // field and the shadow map is spread so thin that everything it draws is a suggestion.
    private const float CameraFurthestDistance = 170f;

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
    private double nextTimingReport;

    private sealed record TerrainSurfaceLayer(
        Mesh Mesh,
        InstanceBuffer Buffer,
        InstancedBatch Batch,
        Vector4 Color);

    public RtsGameLoop(
        int exitAfterFrames,
        bool traceMovement = false,
        bool startTerrainLab = false,
        bool debugAll = false,
        float extentMeters = DefaultWorldExtentMeters,
        float compression = DefaultCompression,
        bool startVillage = false)
    {
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
        tunables = new ObjectTunables(
            bodyFeel,
            clock,
            look,
            woodland,
            new SettlementSettings(),
            raids,
            new WallSettings(),
            new RoutingSettings(),
            new GroupSettings());
        this.exitAfterFrames = exitAfterFrames;
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
        if (startVillage)
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
            if (!agent.IsAlive || agent.Faction.Value != 0) continue;
            if (agent.Jobs.HasAssignment || agent.Jobs.IsInterrupted) continue;
            spare.Add(agent.Id);
        }

        selection.ReplaceWith(spare);
        Console.WriteLine(
            selection.Selected.Count == 0
                ? "  nobody spare — everybody has a job"
                : $"  {selection.Selected.Count} unit(s) selected");
    }

    private void SpawnScenarioAgents(int count)
    {
        MovementStressScenarios.Populate(simulation, count, issueGroupMove: false);
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
        simulation = new SimulationWorld(worldExtentMeters);
        simulation.StartAtSeconds(3100f);
        selection.Clear();
        SettlementScenarios.Populate(
            simulation, farms: 8, woodcutters: 4, carts: 5, wagons: 0, ringRadius: 30f);
        cameraFocus = Vector2.Zero;
        cameraDistance = cameraDistanceTarget = 78f;
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
        this.graphicsDevice = graphicsDevice;
        vk = (VulkanGraphicsDevice)graphicsDevice;

        var meshLayout = new VertexLayout(
            Stride: VertexPosition3NormalTexture.Layout.Stride,
            Attributes: new[]
            {
                new VertexAttribute(0, VertexAttributeFormat.Float3, 0),
                new VertexAttribute(1, VertexAttributeFormat.Float3, 3 * sizeof(float)),
            });
        // The world shader now reads the sun shadow map (set 0) and needs the camera, the sun, the
        // sun's shadow view-projection and the fog range in both stages, which is 176 bytes rather
        // than the bare view-projection it used to take.
        var shaderInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                new DescriptorSetSlot(0, 1, ShaderResourceType.SampledImage, ShaderStages.Fragment),
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

        var shaderDirectory = Path.Combine(AppContext.BaseDirectory, "Shaders");
        byte[] Spv(string name) => File.ReadAllBytes(Path.Combine(shaderDirectory, name));

        // The frame: sun shadow depth → HDR scene → present. The scene target is Rgba16F so
        // the lighting can live above 1.0 and the tonemap has a range to work with; writing
        // the same values straight at the swapchain is what made a sunlit wall and a sunlit
        // roof the same shade of nothing.
        graph = new RenderGraph(vk);
        var fullSize = new MatchSwapchainGraphSize(1.0f);
        sunShadowHandle = graph.DepthTarget("sun-shadow", new FixedGraphSize(ShadowMapSize, ShadowMapSize));
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
        hdrMsaaHandle = graph.ColorTarget("hdr-msaa", TextureFormat.R11G11B10F, fullSize, samples: MsaaSamples);
        hdrHandle = graph.ColorTarget("hdr", TextureFormat.R11G11B10F, fullSize);
        sceneDepthHandle = graph.DepthTarget("scene-depth", fullSize, samples: MsaaSamples);

        var casterInterface = new ShaderInterface(
            Slots: new[] { InstanceBuffer.Slot },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });
        var skyInterface = new ShaderInterface(
            Slots: Array.Empty<DescriptorSetSlot>(),
            PushConstants: new[] { new PushConstantRange(ShaderStages.Fragment, 0, 128) });
        var presentInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Fragment, 0, 32) });

        shadowPassHandle = graph.GraphicsPass("sun-shadow")
            .Depth(sunShadowHandle, LoadOp.Clear, StoreOp.Store)
            .Shader(casterInterface)
            .Handle;
        scenePassHandle = graph.GraphicsPass("scene")
            .Target(hdrMsaaHandle, LoadOp.Clear, StoreOp.Store)
            .ResolveColor(hdrHandle)
            .Depth(sceneDepthHandle, LoadOp.Clear, StoreOp.Store)
            .Read(sunShadowHandle)
            .Shader(skyInterface, shaderInterface)
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
                RenderTarget: graph.GetPassSurface(shadowPassHandle)),
            "rts-caster");
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
        groundBuffer = new InstanceBuffer(vk, worldShader, "rts-coarse-ground");
        groundBatch = new InstancedBatch(cubeMesh, worldPipeline, groundBuffer);
        detailBuffer = new InstanceBuffer(vk, worldShader, "rts-ground-detail");
        detailBatch = new InstancedBatch(cubeMesh, worldPipeline, detailBuffer);
        unitBatch = new InstancedBatch(cylinderMesh, worldPipeline, unitBuffer);
        propBuffer = new InstanceBuffer(vk, worldShader, "rts-props");
        propBatch = new InstancedBatch(cubeMesh, worldPipeline, propBuffer);
        // A caster batch needs its own instance buffer: the buffer's material is created
        // against a shader, and the caster's shader is not the world's.
        propCasterBuffer = new InstanceBuffer(vk, casterShader, "rts-props-caster");
        propCaster = new InstancedBatch(cubeMesh, casterPipeline, propCasterBuffer);
        unitCasterBuffer = new InstanceBuffer(vk, casterShader, "rts-agents-caster");
        unitCaster = new InstancedBatch(cylinderMesh, casterPipeline, unitCasterBuffer);
        canopyBuffer = new InstanceBuffer(vk, worldShader, "rts-canopies");
        canopyBatch = new InstancedBatch(canopyMesh, worldPipeline, canopyBuffer);
        canopyCasterBuffer = new InstanceBuffer(vk, casterShader, "rts-canopies-caster");
        canopyCaster = new InstancedBatch(canopyMesh, casterPipeline, canopyCasterBuffer);
        art = SettlementArt.Load(vk, worldShader, worldPipeline, casterShader, casterPipeline);
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
        Console.WriteLine("  left-click/drag: select   Ctrl: additive selection   right-click: group move");
        Console.WriteLine("  B: toggle block-edit mode   left-click in block mode: add/remove block");
        Console.WriteLine("  S: stop   F: follow   P: patrol to pointer   H: chase   X: flee   Backspace: despawn selected");
        Console.WriteLine("  U: post selected at pointer   O: shuttle (press twice for both ends)   Y: off work");
        Console.WriteLine("  D: granary at pointer   A: farm   Ctrl+A: house   W: forward depot (lumber camp)");
        Console.WriteLine("  U: post a villager — on a field it farms it, on a tree it cuts it");
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
        Console.WriteLine("  arrows: pan   middle-drag: drag the ground   Q/E: rotate 90°   wheel: zoom");
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
                $"  {kind} site: wants {Construction.TimberFor(kind)} timber carried out and " +
                $"{Construction.LabourFor(kind):F0} labour-seconds — post villagers on it with U");
        }

        Console.WriteLine(
            $"  {kind} at ({pointerWorld.X:F0}, {pointerWorld.Y:F0}) — " +
            $"{simulation.Nodes.LiveCount} node(s). Post hands with U; carts find their own work");
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
    private void AssignPost()
    {
        if (!pointerOnTerrain || selection.Selected.Count == 0) return;
        shuttleAnchor = null;
        // Posting on a building posts at the building: the place is its centre and its extent is its
        // footprint, so hands gather round the yard instead of trying to occupy the middle of it.
        var node = EconomySystem.NodeAt(simulation.Nodes, pointerWorld, radius: 3.5f);
        var at = simulation.Nodes.Contains(node) ? simulation.Nodes.Get(node).Position : pointerWorld;
        var extent = simulation.Nodes.Contains(node)
            ? simulation.Nodes.Get(node).FootprintRadius
            : 0f;
        simulation.QueueAssign(selection.Snapshot(), Assignment.Hold(at, PostDwellSeconds, extent));
        Console.WriteLine(
            $"  {selection.Selected.Count} unit(s) posted at ({at.X:F1}, {at.Y:F1})" +
            (extent > 0f ? $" — working the {simulation.Nodes.Get(node).Kind}" : string.Empty));
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

        // Whatever the source actually holds, preferring wood, because that is what a route is usually
        // for: a lumber camp fills with timber nobody lives near and somebody has to bring it in.
        ref readonly var from = ref simulation.Nodes.Get(source);
        var cargo = from.Stock.Wood >= from.Stock.Grain ? Resource.Wood : Resource.Grain;
        var put = 0;
        var refused = 0;
        foreach (var id in selection.Snapshot())
        {
            if (simulation.TryAssignRoute(id, source, node, cargo)) put++;
            else refused++;
        }

        Console.WriteLine(
            $"  route: {put} carting {cargo} from {from.Kind} to {simulation.Nodes.Get(node).Kind}" +
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

    private bool UsesFineGround =>
        simulation.Navigation.Width * simulation.Navigation.Height <= FineGroundCellLimit;

    private void RebuildTerrainSurfaceLayers()
    {
        if (vk is null) return;
        if (terrainSurfaceLayers.Count > 0)
        {
            vk.WaitIdle();
            DisposeTerrainSurfaceLayers();
        }

        renderedTerrain = simulation.Terrain;
        renderedTerrainRevision = simulation.Terrain.Revision;
        if (!UsesFineGround) return;

        foreach (var surface in Enum.GetValues<TerrainSurface>())
        for (var parity = 0; parity < 2; parity++)
        {
            var (vertices, indices) = BuildTerrainSurfaceMesh(simulation.Terrain, surface, parity);
            if (indices.Length == 0) continue;
            var name = $"terrain-{surface.ToString().ToLowerInvariant()}-{parity}";
            var mesh = CreateMesh(vk, name, vertices, indices);
            var buffer = new InstanceBuffer(vk, worldShader, $"{name}-instance");
            terrainSurfaceLayers.Add(new TerrainSurfaceLayer(
                mesh,
                buffer,
                new InstancedBatch(mesh, worldPipeline, buffer),
                // The fine ground's checker is the parity of its own cell, scaled by the same slider.
                // Terrain-classed, like the coarse blocks: both are land and want the same shading.
                TerrainClassed(
                    TerrainColor(surface) *
                    (1f + (parity == 0 ? look.CheckerContrast : -look.CheckerContrast) * 0.5f))));
        }
        renderedTerrain = simulation.Terrain;
        renderedTerrainRevision = simulation.Terrain.Revision;
    }

    private static (VertexPosition3NormalTexture[] Vertices, ushort[] Indices) BuildTerrainSurfaceMesh(
        TerrainMap terrain,
        TerrainSurface surface,
        int parity)
    {
        var vertices = new List<VertexPosition3NormalTexture>();
        var indices = new List<ushort>();
        var grid = terrain.Transform;
        var size = grid.CellSize;

        VertexPosition3NormalTexture Vertex(Vector3 position, Vector3 normal, float u, float v) =>
            new(
                new GraphicsVector3(position.X, position.Y, position.Z),
                new GraphicsVector3(normal.X, normal.Y, normal.Z),
                new GraphicsVector2(u, v));

        void AddTriangle(Vector3 first, Vector3 second, Vector3 third)
        {
            var normal = Vector3.Normalize(Vector3.Cross(second - first, third - first));
            var start = checked((ushort)vertices.Count);
            vertices.Add(Vertex(first, normal, 0f, 0f));
            vertices.Add(Vertex(second, normal, 0f, 1f));
            vertices.Add(Vertex(third, normal, 1f, 0f));
            indices.Add(start);
            indices.Add((ushort)(start + 1));
            indices.Add((ushort)(start + 2));
        }

        for (var z = 0; z < grid.Height; z++)
        for (var x = 0; x < grid.Width; x++)
        {
            var cell = new GridCell(x, z);
            if (terrain.Surface(cell) != surface || (x + z) % 2 != parity) continue;
            var x0 = grid.Origin.X + x * size;
            var z0 = grid.Origin.Y + z * size;
            var x1 = x0 + size;
            var z1 = z0 + size;
            var v00 = new Vector3(x0, terrain.VertexHeight(x, z), z0);
            var v10 = new Vector3(x1, terrain.VertexHeight(x + 1, z), z0);
            var v01 = new Vector3(x0, terrain.VertexHeight(x, z + 1), z1);
            var v11 = new Vector3(x1, terrain.VertexHeight(x + 1, z + 1), z1);
            AddTriangle(v00, v01, v10);
            AddTriangle(v11, v10, v01);
        }
        return (vertices.ToArray(), indices.ToArray());
    }

    private void DisposeTerrainSurfaceLayers()
    {
        foreach (var layer in terrainSurfaceLayers)
        {
            layer.Buffer.Dispose();
            vk.DestroyVertexBuffer(layer.Mesh.VertexBuffer);
            vk.DestroyIndexBuffer(layer.Mesh.IndexBuffer);
        }
        terrainSurfaceLayers.Clear();
        renderedTerrain = null;
        renderedTerrainRevision = -1;
    }

    public void OnResize(int width, int height)
    {
        if (height > 0) aspect = width / (float)height;
    }

    public string DebugName => "rts-movement";

    public void Debug(DebugContext debug)
    {
        // Always on for this testbed — it exists to be watched. The backtick key still
        // hides the panels, and freezing them does not stop a slider taking effect.
        debug.State.Enabled = true;
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

        var hands = 0;
        foreach (ref readonly var node in simulation.Nodes.All) hands += node.Hands;

        // Who is here, how much room is left for more, and whether the settlement can afford another
        // mouth. Readiness is the number worth watching: it is the brake on growth and it is continuous,
        // so the answer to a low figure is more farms rather than more houses — which a bare headcount
        // cannot tell you. Spare hands is the other half: a newcomer arrives idle on purpose, so this is
        // the count of people waiting to be given something to do.
        var room = 0;
        var hungry = 0;
        foreach (ref readonly var node in simulation.Nodes.All)
        {
            if (!node.IsAlive || !node.IsSink) continue;
            room += node.Housing;
            if (node.Privation > 0.5f) hungry++;
        }

        var spare = 0;
        var carts = 0;
        foreach (ref readonly var body in simulation.Agents.All)
        {
            if (!body.IsAlive) continue;
            if (body.HasCart) carts++;
            if (!body.Jobs.HasAssignment && !body.Jobs.IsInterrupted) spare++;
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

        // What the building sites are waiting for, because "wants timber" and "idle site" are two
        // different problems fixed by opposite actions — one wants a cart, the other wants people — and a
        // settlement with no carter cannot get materials anywhere at all. Silence on this was the one gap
        // in the loop: a site with nobody to carry to it simply sat there.
        var sites = 0;
        var worst = string.Empty;
        foreach (ref readonly var node in simulation.Nodes.All)
        {
            if (!node.IsAlive || !node.IsUnderConstruction) continue;
            sites++;
            if (worst.Length == 0 || node.TimberWanted > 0) worst = Construction.StateOf(in node);
        }

        if (sites > 0) debug.Values.Value("building sites", $"{sites} — {worst}");
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

    public void OnUpdate(Time time)
    {
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
            nextTimingReport = time.Total + 1.0;
        }

        var frame = (float)Math.Clamp(time.Delta, 0.0, 0.25);
        // Before anything that reads the light: the shadow box, the sky and the world shader all take their
        // sun from here and a disagreement between them is a scene lit from one place and shadowed from
        // another.
        // Simulated seconds, not wall time: the calendar runs on the tick count, so anything meant to keep
        // step with it has to read the same clock. At three times compression the wall clock advances a
        // third as fast as the date does.
        sky = Atmosphere.For(
            simulation.Date,
            simulation.TickNumber / 30.0,
            look.SunBearingDegrees,
            look.Seasonality);
        AdvanceWear(frame);
        ApplyWoodlandCover(frame);
        TurnAndZoom(frame);
        PanCamera(frame);
        UpdateCameraFocus(frame);
        UpdateCamera();
        UpdatePointerWorld();
    }

    private void UpdateCamera()
    {
        const float elevation = CameraElevation;
        var horizontal = MathF.Cos(elevation) * cameraDistance;
        var focus = new Vector3(cameraFocus.X, 0f, cameraFocus.Y);
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
    private Matrix4x4 SunShadowViewProjection()
    {
        var texel = SunOrthoExtent / ShadowMapSize;
        var focus = new Vector3(
            MathF.Round(cameraFocus.X / texel) * texel,
            simulation.Terrain.SampleHeight(cameraFocus),
            MathF.Round(cameraFocus.Y / texel) * texel);
        return GraphicsMatrices.SunShadowViewProjection(
            SunDirection, focus, SunDistance, SunOrthoExtent, 20f, SunDistance + SunOrthoExtent);
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;
        var viewProjection = camera.GetViewProjection(aspect);
        var sunViewProjection = SunShadowViewProjection();
        var cameraPosition = new Vector4(camera.Transform.Position, 1f);
        var sun = new Vector4(SunDirection, 0f);
        // Aerial perspective sized to the camera rather than to the map: what it is for is
        // separating the near ground from the far ground, and how far away the far ground is
        // depends on how far back the camera is standing.
        var fog = new Vector4(
            // <b>Against the detail radius, not the camera's standoff.</b> Haze exists here to hide the
            // edge where the world stops being drawn, and it cannot do that if it is measured against
            // something else — tied to the zoom it started four hundred metres past a cull at two hundred.
            DetailRadius * look.FogStartShare,
            DetailRadius,
            look.FogStrength,
            0f);
        // What the shaders need about the shadow map's geometry — a texel as a fraction of the map, and
        // how many metres the map covers — plus the two biases, which are a look and not geometry.
        var shadow = new Vector4(
            1f / ShadowMapSize,
            SunOrthoExtent,
            look.ShadowPenumbraTexels,
            look.ShadowNormalOffsetTexels);
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
            look.WindSway, (float)(simulation.TickNumber / 30.0), look.WindGustRate, 0f);
        MemoryMarshal.Write(worldPush.AsSpan(304, 16), in wind);
        MemoryMarshal.Write(skyPush.AsSpan(0, 64), in inverseViewProjection);
        MemoryMarshal.Write(skyPush.AsSpan(64, 16), in cameraPosition);
        MemoryMarshal.Write(skyPush.AsSpan(80, 16), in sun);
        var zenith = new Vector4(following ? sky.SkyZenith : new Vector3(0.16f, 0.38f, 0.80f), 0f);
        var horizon = new Vector4(following ? sky.SkyHorizon : new Vector3(0.64f, 0.78f, 0.92f), 0f);
        MemoryMarshal.Write(skyPush.AsSpan(96, 16), in zenith);
        MemoryMarshal.Write(skyPush.AsSpan(112, 16), in horizon);
        MemoryMarshal.Write(shadowPush.AsSpan(0, 64), in sunViewProjection);
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
        var treeDrawRadius = DetailRadius;
        treeDrawRadiusSquared = treeDrawRadius * treeDrawRadius;

        BuildTerrainInstances();
        BuildAgentInstances((float)time.Total);
        DrawScatter();
        DrawColliderOverlay();

        var props = CollectionsMarshal.AsSpan(propInstances);
        var units = CollectionsMarshal.AsSpan(unitInstances);
        var canopies = CollectionsMarshal.AsSpan(canopyInstances);
        propBatch.Begin(worldPush);
        propBatch.SetInstances(props);
        unitBatch.Begin(worldPush);
        unitBatch.SetInstances(units);
        canopyBatch.Begin(worldPush);
        canopyBatch.SetInstances(canopies);
        propCaster.Begin(shadowPush);
        propCaster.SetInstances(props);
        unitCaster.Begin(shadowPush);
        unitCaster.SetInstances(units);
        canopyCaster.Begin(shadowPush);
        canopyCaster.SetInstances(canopies);
        art?.Stage(worldPush, shadowPush);

        // Shadow depth: only the solids. The ground is a receiver and not a caster — a large
        // near-flat mesh shadowing itself is all acne and no shadow — and the overlays are
        // annotations on top of the world rather than things in it.
        graph.Pass(shadowPassHandle, scope =>
        {
            propCaster.End(scope);
            unitCaster.End(scope);
            canopyCaster.End(scope);
            art?.DrawShadow(scope);
        });

        var shadowBinding = new[]
        {
            new ShaderTextureBinding("uSunShadowMap", graph.GetDepthTexture(sunShadowHandle), Slot: 0),
            new ShaderTextureBinding("uWear", wearTexture, Slot: 1),
        };
        graph.Pass(scenePassHandle, scope =>
        {
            fullscreen.Draw(scope, skyPipeline, Array.Empty<ShaderTextureBinding>(), skyPush);
            foreach (var layer in terrainSurfaceLayers) layer.Batch.End(scope, shadowBinding);
            groundBatch.End(scope, shadowBinding);
            detailBatch.End(scope, shadowBinding);
            propBatch.End(scope, shadowBinding);
            unitBatch.End(scope, shadowBinding);
            canopyBatch.End(scope, shadowBinding);
            art?.DrawScene(scope, shadowBinding);
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
                    frame.Width,
                    frame.Height);
            });

        if (exitAfterFrames > 0 && frameCount >= exitAfterFrames)
        {
            host.RequestClose();
        }
    }


    private void BuildTerrainInstances()
    {
        if (renderedTerrain != simulation.Terrain || renderedTerrainRevision != simulation.Terrain.Revision)
        {
            RebuildTerrainSurfaceLayers();
        }
        foreach (var layer in terrainSurfaceLayers)
        {
            layer.Batch.Begin(worldPush);
            layer.Batch.Add(Matrix4x4.Identity, layer.Color);
        }
        RebuildGroundInstancesIfStale();
        groundBatch.Begin(worldPush);
        groundBatch.SetInstances(CollectionsMarshal.AsSpan(groundInstances));
        detailBatch.Begin(worldPush);
        detailBatch.SetInstances(CollectionsMarshal.AsSpan(detailInstances));
        overlayBatch.Begin(worldPush);
        propInstances.Clear();
        unitInstances.Clear();
        canopyInstances.Clear();
        art?.Begin();
        // Before the nodes are built, because that is what draws the trees and therefore their skirts.
        undergrowthDrawn = 0;
        BuildObstacleInstances();
        BuildNodeInstances();
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
    private void BuildCoarseGround()
    {
        if (UsesFineGround) return;
        var terrain = simulation.Terrain;
        var grid = simulation.Navigation.Transform;
        var block = CoarseGroundBlockSize;
        var blocks = (int)MathF.Ceiling(simulation.ExtentMeters / block);
        for (var z = 0; z < blocks; z++)
        for (var x = 0; x < blocks; x++)
        {
            var center = grid.Origin + new Vector2((x + 0.5f) * block, (z + 0.5f) * block);
            if (!terrain.Contains(center)) continue;
            // The coarse pass always draws. An earlier version skipped blocks whose cells did
            // not all agree, on the theory that the detail pass would cover them — true only
            // where the detail pass reaches. On terrain that rolls, no block agrees with itself
            // to the centimetre, so nothing was drawn beyond the detail radius and the map
            // became a few islands floating in the sky.
            // The checker plus a little variation per block. A regular grid of two colours reads as
            // tiling however faint it is, because the eye finds the period; the same faint contrast with
            // the tiles individually varied reads as ground. Deterministic from the block's own
            // coordinates, so it is stable across a rebuild and identical between two runs.
            var checker = 1f + ((x + z) % 2 == 0 ? look.CheckerContrast : -look.CheckerContrast) * 0.5f;
            var jitter = 1f + (BlockJitter(x, z) - 0.5f) * look.GroundVariation;
            var color = BlockColor(terrain, center, block) * (checker * jitter);
            color.W = SettlementArt.MaterialClass.Terrain;
            // Sized exactly to its spacing. An earlier version grew each block by a hair to
            // close sub-pixel cracks and bought a far worse artefact: neighbours then overlap
            // in a five-centimetre band of coplanar surface, which z-fights into speckle and
            // long bright seams running the width of the map.
            groundInstances.Add(new InstanceData(
                GroundColumn(center, terrain.SampleHeight(center), block),
                color));
        }

        BuildTerrainFeatures();
    }

    /// <summary>
    /// Rebuilds the ground instance lists, but only when something they depend on has moved.
    /// </summary>
    /// <remarks>
    /// Nothing here depends on the camera any more, so a rebuild happens on a terrain edit and
    /// at no other time.
    /// </remarks>
    private void RebuildGroundInstancesIfStale()
    {
        // Terrain only. The detail used to be a window around the camera, so this also had to
        // watch where the camera was and rebuild fourteen thousand blocks whenever it crossed
        // one; patches do not move when the viewer does.
        // The ground is also rebuilt when its two colour sliders move, which is the price of baking the
        // checker and the per-block variation into the instance tints rather than computing them in the
        // shader. Worth it: the ground is fourteen thousand blocks that never change, and rebuilding it
        // when somebody drags a slider is free compared with paying for it every frame forever.
        var groundLook = new Vector2(look.CheckerContrast, look.GroundVariation);
        if (groundTerrainRevision == simulation.Terrain.Revision && groundLookApplied == groundLook) return;
        groundTerrainRevision = simulation.Terrain.Revision;
        groundLookApplied = groundLook;
        groundInstances.Clear();
        detailInstances.Clear();
        BuildCoarseGround();
    }

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
    private void BuildTerrainFeatures()
    {
        if (UsesFineGround) return;
        var grid = simulation.Navigation.Transform;
        var cell = grid.CellSize;
        foreach (var patch in TerrainPatches().All)
        {
            if (patch.Surface == TerrainSurface.Grass && MathF.Abs(patch.Height) < 0.02f) continue;
            if (detailInstances.Count == MaximumDetailPatches) return;
            var width = (patch.MaximumX - patch.MinimumX + 1) * cell;
            var depth = (patch.MaximumZ - patch.MinimumZ + 1) * cell;
            var centre = grid.Origin + new Vector2(
                (patch.MinimumX + (patch.MaximumX - patch.MinimumX + 1) * 0.5f) * cell,
                (patch.MinimumZ + (patch.MaximumZ - patch.MinimumZ + 1) * 0.5f) * cell);
            // Checkered off the patch's own position so a large feature still reads as ground
            // rather than as one flat slab of colour.
            var light = (patch.MinimumX / 8 + patch.MinimumZ / 8) % 2 == 0;
            var model = GroundColumn(centre, patch.Height + 0.012f, 1f);
            model = Matrix4x4.CreateScale(width, 1f, depth) * model;
            detailInstances.Add(new InstanceData(
                model,
                TerrainColor(patch.Surface) *
                (1f + (light ? look.CheckerContrast : -look.CheckerContrast) * 0.5f)));
        }
    }

    private TerrainRectangles TerrainPatches()
    {
        if (terrainPatches is null || terrainPatches.Revision != simulation.Terrain.Revision)
        {
            terrainPatches = TerrainRectangles.Build(simulation.Terrain);
        }

        return terrainPatches;
    }

    private TerrainRectangles? terrainPatches;

    /// <summary>Patches one batch can carry, minus room to spare.</summary>
    private const int MaximumDetailPatches = 16_000;

    /// <summary>
    /// A patch of ground as a column standing on a floor, rather than a plate floating at its
    /// own height.
    /// </summary>
    /// <remarks>
    /// Flat plates cannot describe a slope. On level ground nobody notices, and every version of
    /// this before was only ever looked at on a plain; put a twenty-metre ridge in front of it
    /// and the ground becomes a staircase of disconnected tiles with the sky visible between
    /// them. A column from a floor below the map up to the ground's own height has no gaps
    /// between neighbours at all, whatever the step between them.
    /// </remarks>
    private static Matrix4x4 GroundColumn(Vector2 centre, float height, float width)
    {
        const float floor = -1.5f;
        var thickness = MathF.Max(0.02f, height - floor);
        return Matrix4x4.CreateScale(width, thickness, width) *
               Matrix4x4.CreateTranslation(centre.X, height - thickness * 0.5f, centre.Y);
    }

    /// <summary>
    /// A stable value in [0,1) for a ground block, from its coordinates alone.
    /// </summary>
    /// <remarks>
    /// A hash rather than a <c>Random</c>: the ground is rebuilt whenever the terrain is edited, and a
    /// sequence would give the same block a different shade every time a wall was placed — the whole map
    /// would shimmer on an edit. It also has to be identical between two runs of the same world, which is
    /// the rule everything in this project follows.
    /// </remarks>
    private static float BlockJitter(int x, int z)
    {
        var hash = (uint)(x * 73856093) ^ (uint)(z * 19349663);
        hash = (hash ^ (hash >> 16)) * 0x7FEB352Du;
        hash = (hash ^ (hash >> 15)) * 0x846CA68Bu;
        return ((hash ^ (hash >> 16)) & 0xFFFFFFu) / (float)0x1000000u;
    }

    /// <summary>
    /// A ground block's colour, averaged over the block rather than sampled at its centre.
    /// </summary>
    /// <remarks>
    /// <b>The green patches in the woodland, and they are a resolution mismatch rather than a look.</b>
    /// Forest cover is a fact about <em>navigation cells</em> — <c>RefreshForestCover</c> writes
    /// <c>TerrainSurface.Forest</c> onto every half-metre cell with two trees crowding it — and a coarse
    /// ground block is <b>five metres</b>. So a block spans a hundred cells and took its colour from
    /// <em>one sample at its centre</em>: one cell in a hundred decided the colour of all hundred.
    /// <para>
    /// The two colours are far apart — grass at 0.144 green against a forest floor at 0.082, better than
    /// twice as dark — so in patchy woodland adjacent blocks flipped between them on a coin toss, and what
    /// that looks like is bright green squares scattered among the trees. Reported as exactly that, and
    /// dating from the session dense trees arrived, which is when a surface first varied inside a block.
    /// </para>
    /// <para>
    /// Averaging the samples rather than taking a majority, because the honest answer for a block that is
    /// half wooded is <em>half</em>: it makes a wood's edge a gradient over a few blocks instead of a
    /// staircase, which is what an edge looks like. Five by five is twenty-five samples of a hundred cells,
    /// enough that a single stray cell cannot carry a whole block, and this runs only when the terrain
    /// revision changes.
    /// </para>
    /// </remarks>
    private static Vector4 BlockColor(Simulation.Terrain.TerrainMap terrain, Vector2 center, float block)
    {
        const int side = 5;
        var half = block * 0.5f;
        var step = block / side;
        var total = Vector4.Zero;
        var counted = 0;
        for (var j = 0; j < side; j++)
        for (var i = 0; i < side; i++)
        {
            var at = center + new Vector2(
                -half + (i + 0.5f) * step,
                -half + (j + 0.5f) * step);
            if (!terrain.Contains(at)) continue;
            total += TerrainColor(terrain.SampleSurface(at));
            counted++;
        }

        return counted == 0 ? TerrainColor(terrain.SampleSurface(center)) : total / counted;
    }

    /// <summary>Checker squares across the map, bounded by what one batch can hold.</summary>
    /// <remarks>
    /// A hundred and twenty is what makes the pattern read as ground rather than as a blur or
    /// a flat colour. The clamp is the instanced batch's ceiling — 16,384 instances in one
    /// Begin/End — which a square grid hits at 128 a side, so the constant is not free to grow.
    /// </remarks>
    private const int CoarseGroundBlocksPerSide = 120;

    private float CoarseGroundBlockSize
    {
        get
        {
            var cell = simulation.Navigation.Transform.CellSize;
            var target = simulation.ExtentMeters / CoarseGroundBlocksPerSide;
            return MathF.Max(cell, MathF.Round(target / cell) * cell);
        }
    }

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
        var windowed = !UsesFineGround;
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
    private void BuildNodeInstances()
    {
        DrawStumps();
        foreach (ref readonly var node in simulation.Nodes.All)
        {
            if (!node.IsAlive) continue;
            if (node.IsPile)
            {
                DrawPile(in node);
                continue;
            }

            if (node.Kind == NodeKind.Farm)
            {
                DrawField(in node);
                continue;
            }

            if (node.IsStanding)
            {
                // Culled against what the camera is looking at, because a dense woodland is thousands of
                // models and the camera can see about ninety metres of it. The radius has to cover the
                // shadow box as well as the view — a tree behind the camera still casts into the frame —
                // so it is compared against both. Not an optimisation so much as the thing that makes a
                // forest affordable at all: without it the frame draws the whole map every frame.
                if (Vector2.DistanceSquared(node.Position, cameraFocus) > treeDrawRadiusSquared) continue;
                DrawTree(in node);
                continue;
            }

            var ground = simulation.Terrain.SampleHeight(node.Position);
            var width = node.HalfExtent * 2f;
            if (art is not null)
            {
                // Scaled to the footprint the simulation enforces rather than to anything about the
                // model: a granary is 7.5 m because NodeFootprint says it occupies five placement cells
                // and bodies route around exactly that square. The art fits the game, not the reverse.
                var yaw = SettlementArt.SquareYawOf(node.Id.Value);
                if (node.IsUnderConstruction)
                {
                    DrawSite(in node, ground, width, yaw);
                    continue;
                }

                var placement = SettlementArt.Placement(node.Position, ground, width, yaw);

                switch (node.Kind)
                {
                    case NodeKind.Granary:
                        art.Granary.Add(placement);
                        continue;
                    case NodeKind.House:
                        // A village of one cottage repeated is a village nobody believes. Picked by id
                        // rather than at random so it survives a save and a reload as the same village.
                        art.Houses[node.Id.Value % art.Houses.Length].Add(placement);
                        continue;
                    default:
                        art.Depot.Add(placement);
                        continue;
                }
            }

            DrawGreyboxBuilding(in node, ground, width);
        }
    }

    /// <summary>
    /// A building site: the timber lying on it, and the walls as far up as they have got.
    /// </summary>
    /// <remarks>
    /// Two things a player needs at a glance and they are two different problems. <b>Timber on the
    /// ground</b> says the materials have arrived, and its absence says a cart is wanted; <b>height</b> says
    /// how far the labour has got, and a site that has stopped rising has nobody standing at it. Collapsing
    /// them into one "under construction" marker would leave the player unable to tell a hauling problem
    /// from a labour one, which are fixed by opposite actions.
    /// </remarks>
    private void DrawSite(in EconomyNode site, float ground, float width, float yaw)
    {
        var timber = Construction.TimberFor(site.Kind);
        var delivered = timber <= 0 ? 1f : MathF.Min(1f, site.Stock.Wood / (float)timber);
        if (delivered > 0.02f && art!.WoodHeap is { } logs)
        {
            // Stacked round the footprint rather than in the middle of it, because the middle is where the
            // walls are going and because a stack that grows outward reads as materials arriving.
            var stacks = 1 + (int)(delivered * 3.99f);
            for (var i = 0; i < stacks; i++)
            {
                var angle = yaw + (i + 0.5f) / stacks * MathF.Tau;
                var at = site.Position +
                         new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (width * 0.42f);
                logs.Add(SettlementArt.Placement(at, ground, width * 0.30f, angle));
            }
        }

        var labour = Construction.LabourFor(site.Kind);
        var raised = labour <= 0f ? 1f : MathF.Min(1f, site.BuildWork / labour);
        if (raised <= 0.01f) return;
        var rising = SettlementArt.Rising(site.Position, ground, width, yaw, raised);
        var model = site.Kind switch
        {
            NodeKind.Granary => art!.Granary,
            NodeKind.House => art!.Houses[site.Id.Value % art.Houses.Length],
            _ => art!.Depot,
        };
        model.Add(rising);
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
            art.FieldPlot.Add(placement, new Vector4(TilledEarth * look.SoilBrightness, 1f));
            if (standing > 0.02f)
            {
                var stage = standing >= 0.66f ? 2 : standing >= 0.33f ? 1 : 0;
                art.Crop[stage].Add(placement);
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

    /// <summary>
    /// A tree, shrinking as it is cut.
    /// </summary>
    /// <remarks>
    /// Three species picked by id, at a size that reads how much wood is left in it, because Stage B's
    /// only interface is <b>the wood line as a thing you look at</b>: thinned to stragglers where people
    /// have been cutting for years, closing up into unbroken canopy further out. A part-cut tree standing
    /// smaller means felling is visible while it happens rather than as a tree that is suddenly absent.
    /// </remarks>
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
            var width = NodeFootprint.TreeHalfExtent * 2f * 3.1f * spread * MathF.Sqrt(left);
            art.Trees[tree.Id.Value % art.Trees.Length].Add(
                SettlementArt.Placement(
                    tree.Position, ground, width, SettlementArt.FreeYawOf(tree.Id.Value)));
            DrawUndergrowth(in tree, left);
            return;
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

        var id = tree.Id.Value;
        var clumps = 1 + id * 31 % 2;
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
        var radius = MathF.Sqrt(treeDrawRadiusSquared);
        // Tighter than the trees, because a tuft of grass is a few centimetres and stops being a tuft well
        // before a trunk stops being a trunk.
        const float spacing = 2.4f;
        var cells = (int)MathF.Ceiling(radius / spacing);
        var originX = MathF.Floor(cameraFocus.X / spacing);
        var originZ = MathF.Floor(cameraFocus.Y / spacing);
        var placed = 0;
        for (var dz = -cells; dz <= cells; dz++)
        for (var dx = -cells; dx <= cells; dx++)
        {
            if (placed >= ScatterBudget) return;
            var cx = (int)originX + dx;
            var cz = (int)originZ + dz;

            var at = new Vector2(
                (cx + 0.15f + ScatterHash(cx, cz, 1) * 0.7f) * spacing,
                (cz + 0.15f + ScatterHash(cx, cz, 2) * 0.7f) * spacing);
            if (Vector2.DistanceSquared(at, cameraFocus) > treeDrawRadiusSquared) continue;
            if (!simulation.Terrain.Contains(at)) continue;
            if (simulation.TryGetPlacementCell(at, out var cell) &&
                simulation.Placement.IsOccupied(cell))
            {
                continue;
            }

            // <b>Patches, not a per-cell coin toss.</b> A uniform probability spreads vegetation evenly at
            // whatever rate it is given, and evenly is the one thing ground cover never is — it grows in
            // runs and drifts, thick here and bare a few metres away. Two octaves of lattice noise give
            // that for nothing: the coarse one decides where a patch is at all and the finer one varies
            // its density inside itself, so a patch has a length and an edge that nobody authored.
            var patch = PatchDensity(at);
            if (patch <= 0.04f) continue;

            // Denser under trees, which the terrain already knows: forest cover marks every cell with two
            // trees crowding it, so "am I in a wood" is a lookup rather than a spatial query.
            var wooded = simulation.Terrain.Contains(at) &&
                         simulation.Terrain.SampleSurface(at) == TerrainSurface.Forest;

            var roll = ScatterHash(cx, cz, 0);
            var kind = -1;
            var width = 1f;
            if (roll < patch * (wooded ? 0.72f : 0.34f))
            {
                // The base layer. Short grass in sustained patches, and most of what gets drawn.
                kind = 0;
                width = 0.5f + ScatterHash(cx, cz, 4) * 0.5f;
            }
            else if (roll < patch * (wooded ? 0.95f : 0.44f))
            {
                // Tall grass, thick in woodland and occasional in the open.
                kind = ScatterHash(cx, cz, 5) < 0.5f ? 1 : 2;
                width = 0.55f + ScatterHash(cx, cz, 4) * 0.55f;
            }
            else if (!wooded && roll > 0.985f)
            {
                // Flowers, rare and never under a canopy.
                kind = 3;
                width = 0.4f + ScatterHash(cx, cz, 4) * 0.3f;
            }

            if (kind < 0) continue;
            art.Scatter[kind].Add(SettlementArt.Placement(
                at,
                simulation.Terrain.SampleHeight(at),
                width,
                SettlementArt.FreeYawOf(cx * 73 + cz * 179)));
            placed++;
        }
    }

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

        graphicsDevice.UploadTextureMip(wearTexture, 0, wear);
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
            if (Vector2.DistanceSquared(where, cameraFocus) > treeDrawRadiusSquared) continue;
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
                var model = resource == Resource.Grain ? art.GrainHeap : art.WoodHeap;
                model.Add(SettlementArt.Placement(
                    pile.Position, ground, spread, SettlementArt.FreeYawOf(pile.Id.Value + (int)resource)));
                continue;
            }

            var height = 0.22f + MathF.Min(0.5f, units / 200f);
            propInstances.Add(new InstanceData(
                Matrix4x4.CreateScale(spread, height, spread) *
                Matrix4x4.CreateTranslation(pile.Position.X, ground + height * 0.5f, pile.Position.Y),
                resource == Resource.Grain ? GrainColor : WoodColor));
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

    private static Vector4 TerrainColor(TerrainSurface surface) => surface switch
    {
        TerrainSurface.Road => RoadColor,
        TerrainSurface.Rough => RoughColor,
        TerrainSurface.Mud => MudColor,
        TerrainSurface.Impassable => ImpassableColor,
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

        foreach (ref readonly var agent in simulation.Agents.All)
        {
            // Indoors, so not drawn. A raider rummaging in the granary is in the granary, and the way you
            // know it is in there is that it went in and has not come out — see AgentState.Sheltered.
            if (!agent.IsAlive || agent.Sheltered) continue;
            var position = Vector2.Lerp(agent.PreviousPosition, agent.Position, interpolation);
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
                var ringModel = Matrix4x4.CreateScale(bodyScale * 1.42f, 0.055f, bodyScale * 1.42f) *
                                Matrix4x4.CreateTranslation(position.X, height + 0.035f, position.Y);
                unitInstances.Add(new InstanceData(ringModel, SelectionColor));
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
            var drawnAsAPerson = person is not null;
            if (person is not null)
            {
                // Facing is a direction rather than an angle, which is what the steering layer wants; a
                // model needs the angle, and the sign is negated because the world's Z runs the other way
                // from a right-handed yaw.
                var yaw = agent.Facing.LengthSquared() > 0.0001f
                    ? -MathF.Atan2(agent.Facing.Y, agent.Facing.X)
                    : 0f;
                var placement = Matrix4x4.CreateScale(bodyHeight) *
                                Matrix4x4.CreateRotationY(yaw) *
                                Matrix4x4.CreateTranslation(position.X, height, position.Y);
                // Selection has to survive the art pass. The ring under the feet says which bodies are
                // selected; tinting the body too is what makes one picked out of a crowd of twenty
                // findable at a glance, which is the thing the greybox did for free by being one colour.
                // Hostile bodies in a hostile colour, which is the one thing about a body that has to be
                // readable before anything else on the screen is.
                if (agent.Faction.Value != 0) person.Add(placement, RaiderColor);
                else if (selected) person.Add(placement, SelectedUnitColor);
                else person.Add(placement);

                // The cart, drawn behind them, because a carter has to be findable in a crowd. A wider
                // body is the honest difference — 0.37 m to 0.55 — and at this camera distance it is
                // thirteen per cent of height and nothing you would notice. The pack has no cart, so a
                // crate on the ground behind the body stands in: what matters is that the silhouette says
                // "this one is hauling" without reading the panel.
                if (agent.HasCart && art!.GrainHeap is { } cart)
                {
                    var behind = position - agent.Facing * (agent.Radius + 0.42f);
                    cart.Add(SettlementArt.Placement(behind, height, agent.Radius * 1.7f, yaw));
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
                unitInstances.Add(new InstanceData(
                    Matrix4x4.CreateScale(loadWidth, loadHeight, loadWidth) *
                    Matrix4x4.CreateTranslation(
                        position.X, height + bodyHeight + loadHeight * 0.5f, position.Y),
                    agent.Jobs.Carrying == Resource.Grain ? GrainColor : WoodColor));
            }

            var crowdYielding = agent.IsVisiblyYielding;
            var unitColor = crowdYielding
                ? QueuedUnitColor
                : agent.StuckSeconds > 0.35f ? StuckUnitColor
                : stateDebug ? StateColor(agent.LocomotionState)
                : selected ? SelectedUnitColor : UnitColor;
            // The cylinder still draws whenever it is carrying information the model cannot: a body
            // yielding under crowd pressure, a body failing to make progress, or the state overlay. Those
            // are the colours the whole locomotion layer is judged by and they must not be lost to an art
            // pass. Otherwise the person stands in for it.
            var saysSomething = crowdYielding || agent.StuckSeconds > 0.35f || stateDebug;
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
        foreach (var proxy in simulation.Colliders.All)
        {
            // Bodies already draw their own four, in their own colours, when selected.
            if ((proxy.Layer & ColliderLayer.Agent) != 0) continue;
            if (Vector2.DistanceSquared(proxy.Center, cameraFocus) > treeDrawRadiusSquared) continue;

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
        var seen = VisibleGroundRadius;
        var half = SunOrthoExtent * 0.5f;
        var texelCentimetres = SunOrthoExtent / ShadowMapSize * 100f;
        return
            $"SEES {seen:F0} m · DETAIL {DetailRadius:F0} m ({DetailRadius / seen:F2}x) · " +
            $"SHADOW {half:F0} m (texel {texelCentimetres:F1} cm) · " +
            $"FOG {DetailRadius * look.FogStartShare:F0}-{DetailRadius:F0} m";
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
            if (pointerOnTerrain) simulation.QueueMove(selection.Snapshot(), pointerWorld);
        }
    }

    public void OnMouseUp(MouseButton button)
    {
        if (button == MouseButton.Middle) draggingCamera = false;
        if (obstacleEditMode) return;
        if (button != MouseButton.Left) return;
        selection.Update(mouseX, mouseY);
        var (width, height) = host.LogicalSize;
        selection.End(
            simulation.Agents,
            camera.GetViewProjection(aspect),
            width,
            height,
            additiveSelection);
    }

    public void OnMouseWheel(float offsetX, float offsetY)
    {
        // Zoom steps proportionally, so pulling back over a kilometre does not take a
        // hundred notches of wheel that were sized for a thirty-metre square.
        var step = MathF.Max(2f, cameraDistanceTarget * 0.12f);
        cameraDistanceTarget = Math.Clamp(
            cameraDistanceTarget - offsetY * step,
            CameraNearestDistance,
            CameraFurthestDistance);
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
        switch (key)
        {
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
                Build(NodeKind.Granary, capacity: 2000, Resource.Grain);
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
                Build(NodeKind.ForwardDepot, capacity: 400, Resource.Wood);
                break;
            case Key.Tab:
                SelectSpareHands();
                break;
            case Key.LeftControl:
            case Key.RightControl:
                additiveSelection = true;
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
        if (key is Key.LeftControl or Key.RightControl) additiveSelection = false;
        // Held rather than edge-triggered: key events fire once, and a pan has to keep going for as long
        // as the key is down, so the state lives here and PanCamera reads it every frame.
        if (key == Key.Left) panLeft = false;
        if (key == Key.Right) panRight = false;
        if (key == Key.Up) panUp = false;
        if (key == Key.Down) panDown = false;
        if (key == Key.Q) turnLeft = false;
        if (key == Key.E) turnRight = false;
    }

    public void Dispose()
    {
        hud?.Dispose();
        selectionUi?.Dispose();
        if (selectionPixel.Id >= 0) graphicsDevice?.DestroyTexture(selectionPixel);
        overlayBuffer?.Dispose();
        groundBuffer?.Dispose();
        detailBuffer?.Dispose();
        unitBuffer?.Dispose();
        propBuffer?.Dispose();
        propCasterBuffer?.Dispose();
        unitCasterBuffer?.Dispose();
        canopyBuffer?.Dispose();
        canopyCasterBuffer?.Dispose();
        if (vk is not null) DisposeTerrainSurfaceLayers();
        // The graph owns render passes and offscreen images that are not in the device's auto-freed
        // tables, and Dispose runs after WaitIdle, which is the only safe place to free them.
        art?.Dispose();
        fullscreen?.Dispose();
        graph?.Dispose();
    }
}
