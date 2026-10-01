using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Audio;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Primitives;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Demos.Runner;

// Blix 3D endless runner — the engine's instancing + skeletal-animation showcase.
//
// The scrolling world flows toward the camera (+Z) and wraps procedurally: ground
// tiles, barrel obstacles, and spinning coins (CC0 KayKit meshes) are each drawn
// through a per-mesh InstancedBatch (one vkCmdDrawIndexed(instanceCount=N) reading
// a per-instance transform/tint from a set-3 SSBO). The player is a skinned,
// animated glTF character (CC0 KayKit Rogue) on the engine's bone-palette path —
// a run clip whose cadence tracks the speed, switching to a jump clip mid-air.
// Three lanes (frame-rate-independent lerp) + PhysicsHost3D gravity jump;
// CollisionWorld3D.Overlap drives coin pickups + obstacle hits; distance/coin
// scoring with a speed ramp and game-over/restart. A fullscreen procedural sky +
// exponential distance fog set the scene, a SpriteBatch/Font HUD shows the score,
// synthesized SFX play through OpenAL, and IDebuggable surfaces collision gizmos.
//
// One file by design (a demo reads top-to-bottom): load/setup, OnUpdate (player +
// world + collision), the per-mesh emit helpers, OnRender (sky → world → character
// → HUD), input, and the IDebuggable Debug() pass.
//
// ── Executable spec for (engine primitives this demo proves) ──
//   • Per-mesh InstancedBatch (instanced world tiles / props / coins, set-3 SSBO)
//   • Skinned glTF animation: bone-palette path, clip cadence tracking + jump blend
//   • Kinematic PhysicsHost3D gravity + CollisionWorld3D.Overlap pickups/hits
//   • SpriteBatch/Font HUD, OpenAL SFX, fullscreen procedural sky + fog
// ── Intentionally owns (stays local; don't extract until a 2nd consumer needs it) ──
//   • runner gameplay: lanes, scoring, speed ramp, procedural spawn/wrap, game-over
public static class Program
{
    public static int Main(string[] args) => BlixApps.Main(args, Run);

    private static int Run(AppArgs args)
    {
        // No --frames is interactive; --frames N is the bounded gate run.
        var options = WindowOptions.FromArgs(args, new WindowOptions("Blix — Endless Runner", 1280, 720));
        var loop = new RunnerLoop(options.ExitAfterFrames);
        using var window = new Window(loop, options);
        window.Run();
        return 0;
    }
}

internal sealed class RunnerLoop : IGameLoop, IDebuggable
{
    // --- Track geometry -----------------------------------------------------
    private static readonly float[] LaneX = { -2.2f, 0f, 2.2f };
    private const float TrackWidth = 7.0f;
    private const float TileLength = 4.0f;
    private const int TileCount = 40;                       // 40 * 4 = 160 units of track
    private const float TrackLength = TileCount * TileLength;
    private const float SegmentSpacing = 8.0f;              // obstacle/coin cadence
    private const int SegmentCount = (int)(TrackLength / SegmentSpacing);
    private const int CoinsPerRun = 5;

    // Recycle window: objects flow toward +Z and wrap once they pass behind the
    // camera. zMax sits just behind the camera; zMin is one track-length ahead.
    private const float RecycleZ = 14.0f;
    private const float BaseSpeed = 14.0f;                  // world units / second
    private const float MaxSpeed = 34.0f;
    private const float SpeedRampPerMetre = 0.02f;

    // Tints
    private static readonly Vector4 TileA = new(0.28f, 0.30f, 0.36f, 1f);
    private static readonly Vector4 TileB = new(0.34f, 0.37f, 0.44f, 1f);
    private static readonly Vector4 ObstacleTint = new(0.85f, 0.25f, 0.22f, 1f);
    private static readonly Vector4 CoinTint = new(0.95f, 0.78f, 0.20f, 1f);

    // Direction toward the sun (used by the sky glow + later lighting). Kept low
    // on the horizon ahead (-Z) so it sits in the camera's downward-looking frame.
    private static readonly Vector3 SunToward = Vector3.Normalize(new Vector3(0.12f, 0.16f, -1.0f));

    // Distance fog (a runner/material concern — lives in the runner's own world
    // shader, not in the engine's InstancedBatch): near play-area clear, far track
    // fades into the sky-horizon colour.
    private static readonly Vector4 FogColor = new(0.66f, 0.77f, 0.88f, 1f);
    private const float FogDensity = 0.045f;
    private const float FogStart = 35f;

    // Per-mesh instanced batches (all share the world shader/pipeline + worldPush):
    // ground tiles (cube), obstacles (barrel), coins (coin disc). One InstanceBuffer
    // each so their per-frame writes don't collide.
    private const float ObstacleScale = 0.7f;   // barrel native 2.0 tall -> ~1.4
    private const float CoinScale = 2.0f;        // coin native 0.36 -> ~0.72 across

    private readonly int exitAfterFrames;
    private IGraphicsDevice device = null!;
    private IRenderHost host = null!;
    private InstancedBatch tileBatch = null!;
    private InstancedBatch obstacleBatch = null!;
    private InstancedBatch coinBatch = null!;
    private ShaderProgramHandle worldShader;
    private readonly byte[] worldPush = new byte[112];  // viewProj + camPos + fogColor + fogParams

    // Fullscreen procedural sky (drawn behind the world each frame).
    private VertexBufferHandle skyVb;
    private IndexBufferHandle skyIb;
    private ShaderProgramHandle skyProgram;
    private PipelineHandle skyPipeline;
    private readonly byte[] skyPush = new byte[96];  // mat4 invVP + vec4 camPos + vec4 sunDir

    // HUD (SpriteBatch + Font) drawn over the world.
    private SpriteBatch hud = null!;
    private Font? hudFont;

    // Audio (null if no backend). One-shot SFX synthesized at load — no assets.
    private IAudioDevice? audio;
    private readonly List<AudioClipHandle> clips = new();
    // Every mesh this loop uploaded (the cube, and each prop that loaded), freed in OnUnload.
    private readonly List<Mesh> ownedMeshes = new();
    private readonly List<InstanceBuffer> instanceBuffers = new();
    private readonly List<MaterialHandle> charMaterials = new();
    private AudioSource? jumpSfx;
    private AudioSource? coinSfx;
    private AudioSource? crashSfx;

    // --- Animated character (KayKit Rogue, skinned glTF) ----------------------
    private const float CharScale = 0.9f;
    private const float CharFacing = MathF.PI;     // face -Z (into the screen); flip if backwards
    private ShaderProgramHandle skinnedShader;
    private PipelineHandle skinnedPipeline;
    // The engine's resident Model: uploaded parts with their textures, the skin's palette packing and its
    // bone buffer. What stays here is what this game differs in: the pipeline, the albedo-only material,
    // the push, and which clip plays how fast.
    private Model charModel = null!;
    private MaterialTextureLoader charTextures = null!;
    private (Model.Part Part, MaterialHandle Material)[] charParts = Array.Empty<(Model.Part, MaterialHandle)>();
    private BonePaletteSet[] charPalettes = Array.Empty<BonePaletteSet>();
    private BoneBuffers charBones = null!;          // set 3: one buffer per skin, framesInFlight
    private readonly Pose[] charPoses = new Pose[1];
    private readonly Matrix4x4[] charPlacements = new Matrix4x4[1];
    private ClipPlayer charPlayer = null!;
    private AnimationClip charRun = null!;
    private AnimationClip charJump = null!;
    private readonly byte[] skinnedPush = new byte[128];  // uModel + uViewProjection
    private bool charLoaded;

    private Matrix4x4 viewProj;
    private Vector3 cameraEye;
    private float aspect = 16f / 9f;
    private int frameCount;

    // --- Player (kinematic): lane-lerp on X, PhysicsHost3D gravity on Y --------
    private readonly Transform3D player = new();
    private PhysicsHost3D physics = null!;
    private int laneIndex = 1;        // start centre lane
    private float currentX;
    private bool grounded = true;
    private const float JumpSpeed = 17f;
    private const float PlayerHalfHeight = 0.8f;
    private static readonly Vector4 PlayerTint = new(0.30f, 0.85f, 0.95f, 1f);
    private static readonly Vector4 PlayerDeadTint = new(0.45f, 0.45f, 0.50f, 1f);

    // --- Game state -----------------------------------------------------------
    private float scrollDistance;     // stateful so it freezes on game-over
    private float currentSpeed = BaseSpeed;
    private int coins;
    private bool gameOver;

    // Coins are procedural (recomputed each frame from scrollDistance); collected
    // ones are remembered by a lap-stable key so they stay gone for that lap but
    // return fresh on the next lap. Obstacles need no identity (hitting one ends
    // the run).
    private readonly HashSet<long> collectedKeys = new();
    private readonly record struct Collider(bool Obstacle, long CoinKey);
    private readonly CollisionWorld3D<Collider> collision = new();
    private readonly List<CollisionContact3D<Collider>> hits = new();

    // Spawn lists rebuilt each frame and shared by render + collision so the two
    // can never disagree about where an obstacle/coin is.
    private readonly List<Vector3> obstacles = new();
    private readonly List<(Vector3 Pos, long Key)> coinSpawns = new();

    public RunnerLoop(int exitAfterFrames) => this.exitAfterFrames = exitAfterFrames;

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        device = graphicsDevice;

        var vb = device.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(Cube.Vertices), "cube.vb");
        var ib = device.CreateIndexBuffer(Cube.Indices, name: "cube.ib");
        var cube = new Mesh("cube", vb, ib, Cube.Indices.Length,
            new Bounds3(new Vector3(-0.5f), new Vector3(0.5f)), VertexPosition3NormalTexture.Layout);

        // The runner supplies its own lit+fog instanced shader (world.vert/frag) +
        // pipeline; the engine's instancing layers only provide the per-instance
        // SSBO (InstanceBuffer) and the staging/draw (InstancedBatch). Fog lives
        // here, in the runner's material. The shader declares InstanceBuffer.Slot at
        // set 3 plus a 112-byte push the runner packs each frame.
        var shaderDir = AppFiles.Shaders;
        // The world fragment shader is cooked in two #define variants (see csproj):
        // base (no fog) and FOG. The runner wants the haze, so it selects the FOG
        // variant by convention via ShaderVariantPath — proving the cook + select
        // + load path end to end. The base variant ships alongside, unused here.
        var fog = new ShaderVariantKey("FOG");
        // Read from the VARIANT that is actually loaded below: a #define can change what a
        // shader declares, so reflecting the base while running FOG would be reading a
        // different program. InstanceBuffer supplies the unsized set-3 array's length.
        var worldInterface = InstanceBuffer.Size(
            ShaderReflection.ForProgram(shaderDir, "world.vert", "world.FOG.frag"));
        worldShader = device.CreateShaderProgramFromSpv(
            File.ReadAllBytes(ShaderVariantPath.Spv(shaderDir, "world", ".vert", ShaderVariantKey.Base)),
            File.ReadAllBytes(ShaderVariantPath.Spv(shaderDir, "world", ".frag", fog)),
            worldInterface, "runner.world");
        // Pipeline consumes position + normal (stride matched to the cube vertex).
        var meshLayout = new VertexLayout(
            Stride: VertexPosition3NormalTexture.Layout.Stride,
            Attributes: new[]
            {
                new VertexAttribute(0, VertexAttributeFormat.Float3, 0),
                new VertexAttribute(1, VertexAttributeFormat.Float3, 3 * sizeof(float)),
            });
        var worldDesc = new PipelineDescription(worldShader, meshLayout, PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite, RasterizerState.BackFaceCulling, new[] { BlendState.Disabled });
        var worldPipeline = device.GetOrCreatePipeline(worldDesc, "runner.world");
        // Cache self-check: a second GetOrCreatePipeline with a structurally-equal
        // description (note the SEPARATELY-allocated blend array — record equality
        // would miss it; PipelineKey compares blends by value) must return the SAME
        // handle, not rebuild. Proves the cache + key on a live device.
        var worldDescDup = new PipelineDescription(worldShader, meshLayout, PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite, RasterizerState.BackFaceCulling, new[] { BlendState.Disabled });
        var worldPipelineDup = device.GetOrCreatePipeline(worldDescDup, "runner.world");
        if (worldPipelineDup.Id != worldPipeline.Id)
        {
            throw new InvalidOperationException(
                $"PipelineCache identity failed: equal descriptions gave handles {worldPipeline.Id} vs {worldPipelineDup.Id}.");
        }
        // Three batches sharing the one world pipeline; tiles stay cubes, obstacles
        // and coins use CC0 KayKit prop meshes (flat-tinted through the same shader).
        var barrel = LoadStaticMesh("barrel.glb") ?? cube;
        var coin = LoadStaticMesh("coin.glb") ?? cube;
        ownedMeshes.Add(cube);
        foreach (var prop in new[] { barrel, coin }.Where(m => !ReferenceEquals(m, cube))) ownedMeshes.Add(prop);
        InstanceBuffer Instances(string name)
        {
            var buffer = new InstanceBuffer(device, worldShader, name);
            instanceBuffers.Add(buffer);
            return buffer;
        }

        tileBatch = new InstancedBatch(cube, worldPipeline, Instances("tiles"));
        obstacleBatch = new InstancedBatch(barrel, worldPipeline, Instances("obstacles"));
        coinBatch = new InstancedBatch(coin, worldPipeline, Instances("coins"));

        physics = new PhysicsHost3D { Target = player, Gravity = new Vector3(0f, -55f, 0f), GravityScale = 1f };
        player.Position = new Vector3(LaneX[laneIndex], 0f, 0f);
        currentX = LaneX[laneIndex];

        CreateSky();
        CreateHud(shaderDir);
        CreateAudio();
        CreateCharacter(shaderDir);
        host.SetTitle("Blix — Endless Runner");

        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        UpdateCamera();
    }

    // HUD: a SpriteBatch baked against the swapchain + the Bowlby font. Font load
    // is best-effort — a missing font just means no on-screen text, not a crash.
    private void CreateHud(string shaderDir)
    {
        hud = new SpriteBatch(device); // null render target = swapchain
        try
        {
            var assets = new AssetDatabase()
                .RegisterImporter(new FontImporter())
                .LoadManifest(AppFiles.Asset("manifest.json"));
            hudFont = Font.Upload(device, assets.Load<FontData>(AssetId.Parse("fonts/bowlby")));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Font unavailable, HUD text will not render: {ex.Message}");
        }
    }

    // One-shot SFX, synthesized at load (no assets). Audio is optional — the
    // device throws if no backend, so capture it defensively.
    private void CreateAudio()
    {
        try { audio = (host as IAudioHost)?.AudioDevice; }
        catch { audio = null; }
        if (audio is not { } a) return;
        AudioClipHandle Clip(AudioClipData samples, string name)
        {
            var clip = a.CreateClip(samples, name);
            clips.Add(clip);
            return clip;
        }

        jumpSfx = AudioSource.Create(a, Clip(SynthBlip(520f, 0.10f, 0.5f, rising: true), "runner.jump"), "runner.jump");
        coinSfx = AudioSource.Create(a, Clip(SynthBlip(900f, 0.08f, 0.45f, rising: true), "runner.coin"), "runner.coin");
        crashSfx = AudioSource.Create(a, Clip(SynthBlip(150f, 0.35f, 0.7f, rising: false), "runner.crash"), "runner.crash");
    }

    // Animated character (KayKit Rogue): skinned glTF rendered as its own pass via
    // a bone-palette SSBO (set 3) + albedo (set 2). Best-effort — any failure
    // leaves charLoaded false and the player falls back to the placeholder box.
    private void CreateCharacter(string shaderDir)
    {
        var path = AppFiles.Asset("models", "Rogue.blixmesh");
        ModelData model;
        try
        {
            model = ModelData.Load(path, new ModelNeeds(Skinned: true));
            if (model.Clips.Count == 0) throw new InvalidOperationException("no animations");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Character unavailable, using placeholder box: {ex.Message}");
            return;
        }

        charTextures = new MaterialTextureLoader(device);
        charModel = device.CreateModel(model, charTextures, "rogue");
        // Realised now: one character, and a streamed texture would show a frame of fallback white.
        charTextures.Drain(double.PositiveInfinity);
        // The clips are against the model's animated hierarchy; the player poses that, and every skin
        // gathers its palette from the one pose.
        charPlayer = new ClipPlayer(charModel.Skeleton);
        charRun = charModel.Clip("Running_A") ?? model.Clips[0];
        charJump = charModel.Clip("Jump_Idle") ?? charModel.Clip("Jump_Full_Short") ?? charRun;

        // The bone block is unsized in the shader; each skin's buffer is sized by CreateBoneBuffers.
        var iface = ShaderReflection.ForProgram(shaderDir, "skinned.vert", "skinned.frag");
        skinnedShader = device.CreateShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "skinned.vert.spv")),
            File.ReadAllBytes(Path.Combine(shaderDir, "skinned.frag.spv")), iface, "runner.skinned");
        skinnedPipeline = device.CreatePipeline(
            new PipelineDescription(skinnedShader, VertexPosition3NormalTextureSkin4Tangent.Layout,
                PrimitiveTopology.Triangles, DepthState.LessEqualWrite, RasterizerState.BackFaceCulling, new[] { BlendState.Disabled }),
            "runner.skinned");

        // Each skinned part with an albedo material of its own texture (one per distinct texture: the
        // Rogue's 12 primitives share one).
        var materials = new Dictionary<TextureHandle, MaterialHandle>();
        charParts = charModel.SkinnedParts.Select(part =>
        {
            var albedo = part.Textures.Albedo;
            if (!materials.TryGetValue(albedo, out var material))
            {
                material = device.CreateMaterial(skinnedShader, name: $"rogue.skin.{materials.Count}").SetTexture(0, albedo).Handle;
                materials[albedo] = material;
                charMaterials.Add(material);
            }

            return (part, material);
        }).ToArray();
        charPalettes = charModel.CreatePaletteSets(1);
        charBones = charModel.CreateBoneBuffers(skinnedShader, maxInstances: 1);
        charLoaded = true;
    }

    // Advance the character's animation, upload its bone palette for this frame,
    // and pack the skinned push (model + view-projection). Run cadence scales with
    // run speed; airborne switches to the jump clip; game-over freezes the pose.
    private void UpdateCharacter(Time time)
    {
        // <b>Three lines and three fields lighter than it was.</b> The rest reset, the loop wrap and
        // the zero-duration guard were written here by hand, and identically in Bulwark and the external RTSGame consumer —
        // one non-obvious decision (a clip writes only the channels it has tracks for, so an
        // un-reset pose keeps last frame's values on every other bone) copied three times.
        //
        // Rate carries what the demo means: game over holds the pose, airborne runs the jump at
        // authored speed, and on the ground the run cadence scales with how fast the player is
        // actually moving. The player has no opinion about any of that — it owns a clip and a clock.
        charPlayer.Clip = grounded ? charRun : charJump;
        charPlayer.Rate = gameOver ? 0f : (grounded ? currentSpeed / BaseSpeed : 1f);
        charPlayer.Advance(time.Delta);
        // The body's placement is baked into its palettes (SkeletonPlacement, then this), so the push's
        // model matrix is the identity.
        charPoses[0] = charPlayer.Pose;
        charPlacements[0] = Matrix4x4.CreateScale(CharScale)
                          * Matrix4x4.CreateRotationY(CharFacing)
                          * Matrix4x4.CreateTranslation(player.Position.X, player.Position.Y, 0f);
        charModel.PackPalettes(charPoses, charPlacements, charPalettes);
        for (var s = 0; s < charPalettes.Length; s++) charBones.Upload(s, charPalettes[s]);
        var model = Matrix4x4.Identity;
        MemoryMarshal.Write(skinnedPush.AsSpan(0, 64), in model);
        MemoryMarshal.Write(skinnedPush.AsSpan(64, 64), in viewProj);
    }

    // Load a static CC0 prop glb (single-material → one primitive) as a Mesh in the
    // stride-32 VertexPosition3NormalTexture layout the world pipeline expects.
    // Returns null on any failure so the caller can fall back to the cube.
    private Mesh? LoadStaticMesh(string fileName)
    {
        var path = AppFiles.Asset("models", Path.ChangeExtension(fileName, ".blixmesh"));
        try
        {
            var primitives = ModelData.Load(path).Flattened().Select(p => p.Primitive).ToArray();
            if (primitives.Length == 0) return null;
            var m = primitives[0].Mesh;
            var vb = device.CreateVertexBuffer(
                new VertexBufferData(new VertexBufferDescription(m.Layout, m.VertexCount, GraphicsBufferUsage.Static), m.VertexBytes),
                $"{fileName}.vb");
            var ib = m.Indices32 is { } u32
                ? device.CreateIndexBuffer(u32, name: $"{fileName}.ib")
                : device.CreateIndexBuffer(m.Indices, name: $"{fileName}.ib");
            return new Mesh(fileName, vb, ib, m.IndexCount, m.Bounds, m.Layout);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Prop '{fileName}' unavailable, using cube: {ex.Message}");
            return null;
        }
    }

    // Called by the host with the GPU idle. Everything this loop made, in reverse of its making; the
    // world pipeline is the device's (GetOrCreatePipeline), and so is the program it was built from.
    // BLIX_TEARDOWN_TRACE=1 lists whatever is still live after this.
    public void OnUnload()
    {
        hud?.Dispose();
        if (hudFont is not null)
        {
            foreach (var size in hudFont.Sizes) device.DestroyTexture(size.Atlas);
        }

        if (charLoaded)
        {
            charBones.Dispose();
            foreach (var material in charMaterials) device.DestroyMaterial(material);
            device.DestroyPipeline(skinnedPipeline);
            device.DestroyShaderProgram(skinnedShader);
            charModel.Dispose();
            charTextures.Dispose();
        }

        device.DestroyPipeline(skyPipeline);
        device.DestroyShaderProgram(skyProgram);
        device.DestroyVertexBuffer(skyVb);
        device.DestroyIndexBuffer(skyIb);

        foreach (var buffer in instanceBuffers) buffer.Dispose();
        foreach (var mesh in ownedMeshes)
        {
            device.DestroyVertexBuffer(mesh.VertexBuffer);
            device.DestroyIndexBuffer(mesh.IndexBuffer);
        }

        if (audio is { } a)
        {
            jumpSfx?.Dispose(a);
            coinSfx?.Dispose(a);
            crashSfx?.Dispose(a);
            foreach (var clip in clips) a.DeleteClip(clip);
        }
    }

    // Fullscreen-triangle sky: 3 vertices at NDC corners (far plane), a push-only
    // shader interface, depth-disabled so it fills the background before the world.
    private void CreateSky()
    {
        var fsLayout = new VertexLayout(
            Stride: 3 * sizeof(float),
            Attributes: new[] { new VertexAttribute(0, VertexAttributeFormat.Float3, 0) });
        var corners = new float[] { -1f, -1f, 1f,   3f, -1f, 1f,   -1f, 3f, 1f };
        var bytes = new byte[corners.Length * sizeof(float)];
        System.Buffer.BlockCopy(corners, 0, bytes, 0, bytes.Length);
        skyVb = device.CreateVertexBuffer(new VertexBufferData(new VertexBufferDescription(fsLayout, 3, GraphicsBufferUsage.Static), bytes), "sky.vb");
        skyIb = device.CreateIndexBuffer(new ushort[] { 0, 1, 2 }, name: "sky.ib");

        var shaderDir = AppFiles.Shaders;
        var skyInterface = ShaderReflection.ForProgram(shaderDir, "sky.vert", "sky.frag");
        var vert = File.ReadAllBytes(Path.Combine(shaderDir, "sky.vert.spv"));
        var frag = File.ReadAllBytes(Path.Combine(shaderDir, "sky.frag.spv"));
        skyProgram = device.CreateShaderProgramFromSpv(vert, frag, skyInterface, "sky");

        skyPipeline = device.CreatePipeline(
            new PipelineDescription(
                skyProgram, fsLayout, PrimitiveTopology.Triangles,
                DepthState.Disabled, RasterizerState.NoCulling, new[] { BlendState.Disabled }),
            name: "sky");
    }

    public void OnUpdate(Time time)
    {
        ReadInput();

        var dt = (float)time.Delta;

        if (!gameOver)
        {
            // Forward motion + difficulty ramp (frozen once the run ends).
            currentSpeed = MathF.Min(MaxSpeed, BaseSpeed + scrollDistance * SpeedRampPerMetre);
            scrollDistance += currentSpeed * dt;

            // Vertical: gravity + jump impulse via PhysicsHost3D, clamped to the
            // ground plane (y = 0). Velocity.X/Z stay 0 so only Y is physics-driven.
            physics.FixedUpdate(time);
            var y = player.Position.Y;
            if (y <= 0f)
            {
                y = 0f;
                physics.Velocity = Vector3.Zero;
                grounded = true;
            }

            // Lateral: frame-rate-independent lerp toward the active lane.
            currentX += (LaneX[laneIndex] - currentX) * (1f - MathF.Exp(-12f * dt));
            player.Position = new Vector3(currentX, y, 0f);
        }

        BuildSpawns(scrollDistance);
        if (!gameOver) ResolveCollisions();
        UpdateCamera();
    }

    // Player sphere vs every obstacle/coin via CollisionWorld3D.Overlap. Obstacle
    // contact ends the run; coin contact scores once (the lap-stable key dedupes
    // repeat hits while the coin sits in the overlap zone across frames).
    private void ResolveCollisions()
    {
        collision.Clear();
        foreach (var o in obstacles) collision.Add(new Collider(true, 0), new BoundingSphere(o, 0.9f));
        foreach (var (pos, key) in coinSpawns) collision.Add(new Collider(false, key), new BoundingSphere(pos, 0.45f));

        var playerSphere = new BoundingSphere(new Vector3(currentX, player.Position.Y + PlayerHalfHeight, 0f), 0.6f);
        hits.Clear();
        collision.Overlap(playerSphere, hits);

        foreach (var hit in hits)
        {
            if (hit.Owner.Obstacle)
            {
                gameOver = true;
                PlaySfx(crashSfx, 1f);
                return;
            }
            if (collectedKeys.Add(hit.Owner.CoinKey))
            {
                coins++;
                PlaySfx(coinSfx, 1f + (coins % 6) * 0.04f); // slight rising pitch per coin
            }
        }
    }

    private void Restart()
    {
        scrollDistance = 0f;
        currentSpeed = BaseSpeed;
        coins = 0;
        gameOver = false;
        laneIndex = 1;
        currentX = LaneX[laneIndex];
        grounded = true;
        physics.Velocity = Vector3.Zero;
        player.Position = new Vector3(currentX, 0f, 0f);
        collectedKeys.Clear();
    }

    public void OnResize(int width, int height)
    {
        if (height > 0) aspect = width / (float)height;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        // Behind + above the player, panning partway with its lane so switches
        // read clearly without locking the camera rigidly to the player's X.
        cameraEye = new Vector3(currentX * 0.5f, 6.5f, 11f);
        var view = Matrix4x4.CreateLookAt(cameraEye, new Vector3(currentX, 1.0f, -10f), Vector3.UnitY);
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3f, aspect, 0.1f, 400f);
        viewProj = view * proj;
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;
        var t = (float)time.Total;

        // Pack the world shader's push: viewProj (vertex) + camera + fog (fragment).
        MemoryMarshal.Write(worldPush.AsSpan(0, 64), in viewProj);
        var worldCam = new Vector4(cameraEye, 1f);
        var fogParams = new Vector4(FogDensity, FogStart, 0f, 0f);
        MemoryMarshal.Write(worldPush.AsSpan(64, 16), in worldCam);
        MemoryMarshal.Write(worldPush.AsSpan(80, 16), in FogColor);
        MemoryMarshal.Write(worldPush.AsSpan(96, 16), in fogParams);

        tileBatch.Begin(worldPush);
        obstacleBatch.Begin(worldPush);
        coinBatch.Begin(worldPush);
        EmitTiles(scrollDistance);
        EmitSpawns(t);
        if (!charLoaded) EmitPlayer();   // box fallback when the character didn't load

        if (charLoaded) UpdateCharacter(time);

        // Sky push: inverse view-projection (for the per-pixel ray) + camera + sun.
        Matrix4x4.Invert(viewProj, out var invViewProj);
        MemoryMarshal.Write(skyPush.AsSpan(0, 64), in invViewProj);
        var camPos = new Vector4(cameraEye, 1f);
        var sunDir = new Vector4(SunToward, 0f);
        MemoryMarshal.Write(skyPush.AsSpan(64, 16), in camPos);
        MemoryMarshal.Write(skyPush.AsSpan(80, 16), in sunDir);

        commandList.Pass(
            "runner-world",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0.07f, 0.09f, 0.13f, 1f) },
                ClearDepth: true),
            pass =>
            {
                // Sky first (depth-disabled background), then the world over it,
                // then the HUD on top (SpriteBatch is depth-disabled + alpha-blended).
                pass.DrawIndexed(skyVb, skyIb, skyPipeline, 3,
                    Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(), skyPush);
                tileBatch.End(pass);
                obstacleBatch.End(pass);
                coinBatch.End(pass);
                if (charLoaded)
                {
                    // Skinned character: each primitive shares the set-3 bone palette
                    // and the set-2 albedo; depth-tested into the same buffer as the world.
                    foreach (var (part, material) in charParts)
                    {
                        pass.DrawIndexed(part.Mesh.VertexBuffer, part.Mesh.IndexBuffer, skinnedPipeline, part.Mesh.IndexCount,
                            Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(),
                            material, charBones.For(part.SkinIndex).Handle, skinnedPush);
                    }
                }
                DrawHud(pass, frame.Width, frame.Height);
            });

        if (exitAfterFrames > 0 && frameCount >= exitAfterFrames) host.RequestClose();
    }

    // Score / distance top-left; a centred game-over overlay. Screen-space ortho in
    // framebuffer pixels (so pixelSize is in physical px, dpiScale = 1).
    private void DrawHud(RenderPassBuilder pass, int width, int height)
    {
        if (hudFont is null) return;
        var ortho = GraphicsMatrices.CreateOrthographicOffCenterVulkan(0f, width, height, 0f, -1f, 1f);
        hud.Begin(ortho);

        var pad = height * 0.03f;
        var size = height * 0.045f;
        hud.DrawText(hudFont, size, $"COINS {coins}", new Vector2(pad, pad), new GraphicsColor(0.96f, 0.82f, 0.30f, 1f));
        hud.DrawText(hudFont, size, $"{(int)scrollDistance} M", new Vector2(pad, pad + size * 1.25f), new GraphicsColor(0.85f, 0.92f, 1.0f, 1f));

        if (gameOver)
        {
            var big = height * 0.11f;
            var sub = height * 0.045f;
            const string over = "GAME OVER";
            var overSize = SpriteBatchUiExtensions.MeasureText(hudFont, big, over);
            hud.DrawText(hudFont, big, over, new Vector2((width - overSize.X) * 0.5f, height * 0.34f), new GraphicsColor(0.96f, 0.36f, 0.30f, 1f));
            const string prompt = "ENTER TO RESTART";
            var promptSize = SpriteBatchUiExtensions.MeasureText(hudFont, sub, prompt);
            hud.DrawText(hudFont, sub, prompt, new Vector2((width - promptSize.X) * 0.5f, height * 0.34f + big), new GraphicsColor(0.90f, 0.92f, 0.96f, 1f));
        }

        hud.End(pass);
    }

    private void PlaySfx(AudioSource? source, float pitch)
    {
        if (source is null || audio is not { } a) return;
        source.Pitch = pitch;
        source.Gain = 0.6f;
        source.Sync(a);
        source.Stop(a);  // rewind so rapid re-triggers restart cleanly
        source.Play(a);
    }

    // Square-wave blip with a decay envelope and a pitch sweep (rising = up,
    // falling = down). 16-bit mono PCM — synthesized, no asset.
    private static AudioClipData SynthBlip(float baseFreq, float seconds, float amplitude, bool rising)
    {
        const int rate = 44100;
        var n = (int)(rate * seconds);
        var pcm = new byte[n * 2];
        var phase = 0f;
        for (var i = 0; i < n; i++)
        {
            var u = (float)i / n;
            var env = 1f - u;
            var freq = rising ? baseFreq * (1f + 0.8f * u) : baseFreq * (1f - 0.5f * u);
            phase += freq / rate;
            if (phase >= 1f) phase -= 1f;
            var square = phase < 0.5f ? 1f : -1f;
            var s = (short)(square * env * amplitude * short.MaxValue);
            pcm[i * 2 + 0] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return new AudioClipData(SampleRate: rate, Channels: 1, BitsPerSample: 16, PcmData: pcm);
    }

    // Contiguous scrolling ground: one wide, thin tile per track segment.
    private void EmitTiles(float scroll)
    {
        for (var i = 0; i < TileCount; i++)
        {
            var z = WrapZ(i * TileLength + scroll);
            var model =
                Matrix4x4.CreateScale(TrackWidth, 0.5f, TileLength) *
                Matrix4x4.CreateTranslation(0f, -0.25f, z);
            tileBatch.Add(model, (i & 1) == 0 ? TileA : TileB);
        }
    }

    // Deterministic obstacle + coin layout per segment (no RNG so the field is
    // stable as it scrolls). Fills the shared spawn lists used by both render and
    // collision. Collected coins (lap-stable key) are skipped so they vanish.
    private void BuildSpawns(float scroll)
    {
        obstacles.Clear();
        coinSpawns.Clear();

        // Obstacles first, so coins (whose runs span Z into adjacent segments) can
        // be tested against ALL of them and skip any that would sit inside a barrel.
        for (var seg = 0; seg < SegmentCount; seg++)
        {
            var h = Hash((uint)seg);
            obstacles.Add(new Vector3(LaneX[(int)(h % 3)], 0.65f, WrapZ(seg * SegmentSpacing + scroll)));
        }

        for (var seg = 0; seg < SegmentCount; seg++)
        {
            var h = Hash((uint)seg);
            var segZ = seg * SegmentSpacing;
            var obstacleLane = (int)(h % 3);
            var coinLane = (obstacleLane + 1 + (int)((h >> 3) & 1)) % 3;
            var laneX = LaneX[coinLane];
            for (var c = 0; c < CoinsPerRun; c++)
            {
                var raw = segZ + scroll - c * 1.4f - 2f;
                var lap = (int)MathF.Floor((raw - RecycleZ) / TrackLength);
                var key = ((long)(lap + 1024)) * 1_000_000 + seg * 100 + c;
                if (collectedKeys.Contains(key)) continue;
                var z = WrapZ(raw);
                if (CoinHitsObstacle(laneX, z)) continue;   // never spawn a coin inside a barrel
                coinSpawns.Add((new Vector3(laneX, 1.0f, z), key));
            }
        }
    }

    // True if a coin at (laneX, z) would sit inside any barrel: same lane (lanes are
    // ~2.2 apart, so an exact X match is enough) and within a Z clearance covering
    // both the barrel and coin footprints.
    private bool CoinHitsObstacle(float laneX, float z)
    {
        const float clearance = 1.8f;
        foreach (var o in obstacles)
            if (MathF.Abs(o.X - laneX) < 0.1f && MathF.Abs(o.Z - z) < clearance)
                return true;
        return false;
    }

    // Render obstacles + coins from the spawn lists. Coins spin via their
    // per-instance transform (a live read of the per-instance SSBO).
    private void EmitSpawns(float t)
    {
        // Barrel sits on the ground (its origin is at the base); the collision
        // sphere stays where BuildSpawns put it.
        foreach (var o in obstacles)
            obstacleBatch.Add(Matrix4x4.CreateScale(ObstacleScale) * Matrix4x4.CreateTranslation(o.X, 0f, o.Z), ObstacleTint);
        // Coin disc stood upright (rotate 90° about X) then spun about Y.
        foreach (var (pos, _) in coinSpawns)
            coinBatch.Add(
                Matrix4x4.CreateScale(CoinScale) * Matrix4x4.CreateRotationX(MathF.PI * 0.5f) *
                Matrix4x4.CreateRotationY(t * 3f) * Matrix4x4.CreateTranslation(pos),
                CoinTint);
    }

    // Placeholder player box — only used as a fallback when the animated character
    // fails to load. Drawn through the tile (cube) batch.
    private void EmitPlayer()
    {
        tileBatch.Add(
            Matrix4x4.CreateScale(0.8f, PlayerHalfHeight * 2f, 0.8f) *
            Matrix4x4.CreateTranslation(player.Position.X, player.Position.Y + PlayerHalfHeight, 0f),
            gameOver ? PlayerDeadTint : PlayerTint);
    }

    // Map a raw advancing z into the recycle window [RecycleZ - TrackLength, RecycleZ).
    private static float WrapZ(float z)
    {
        var m = (z - RecycleZ) % TrackLength;
        if (m > 0) m -= TrackLength; // ((z-RecycleZ) mod L) in (-L, 0]
        return RecycleZ + m;
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    /// <summary>Read the devices, once, at a point this loop chose.</summary>
    /// <remarks>
    /// Lane changes and the jump are <c>Pressed</c>, which is what they always meant: one press,
    /// one lane. Running them from the platform callback made them one per key-down EVENT, which
    /// is the same thing only as long as the backend never repeats.
    /// </remarks>
    private void ReadInput()
    {
        var input = host.Input;
        if (input[Key.Escape].Pressed) { host.RequestClose(); return; }

        if (gameOver)
        {
            if (input[Key.Enter].Pressed || input[Key.R].Pressed) Restart();
            return;
        }

        if (input[Key.Left].Pressed || input[Key.A].Pressed) laneIndex = Math.Max(0, laneIndex - 1);
        if (input[Key.Right].Pressed || input[Key.D].Pressed) laneIndex = Math.Min(LaneX.Length - 1, laneIndex + 1);
        if ((input[Key.Up].Pressed || input[Key.W].Pressed || input[Key.Space].Pressed) && grounded)
        {
            physics.Velocity = new Vector3(0f, JumpSpeed, 0f);
            grounded = false;
            PlaySfx(jumpSfx, 1f);
        }
    }

    // Diagnostics: toggle the overlay with the ` (grave) key. Draws the exact
    // collision spheres ResolveCollisions tests, so coin↔obstacle overlap is
    // visible (a coin run from one segment can reach an adjacent segment's lane).
    public string DebugName => "runner";

    public void Debug(DebugContext debug)
    {
        // The keys this game handles itself.
        debug.Keys.Describe("Left / Right (A / D)", "change lane");
        debug.Keys.Describe("Up / W / Space", "jump");
        debug.Keys.Describe("Enter / R", "restart, once the run is over");
        debug.Keys.Describe(Key.Escape, "quit");

        // Every primitive below belongs to this view. Scoped rather than assigned: the old
        // per-channel matrix meant a frame could only ever be one world seen one way.
        using var view = debug.Draw.In("main", viewProj);

        var red = new GraphicsColor(0.95f, 0.30f, 0.25f, 0.9f);
        var gold = new GraphicsColor(0.95f, 0.80f, 0.25f, 0.9f);
        var cyan = new GraphicsColor(0.30f, 0.90f, 0.95f, 0.9f);
        for (var i = 0; i < obstacles.Count; i++)
            debug.Draw.Sphere($"obstacle/{i}", obstacles[i], 0.9f, red);
        for (var i = 0; i < coinSpawns.Count; i++)
            debug.Draw.Sphere($"coin/{i}", coinSpawns[i].Pos, 0.45f, gold);
        debug.Draw.Sphere("player", new Vector3(currentX, player.Position.Y + PlayerHalfHeight, 0f), 0.6f, cyan);

        using (debug.Scope("game"))
        {
            debug.Values.Value("coins", coins);
            debug.Values.Value("distance-m", (int)scrollDistance);
            debug.Values.Value("speed", currentSpeed);
            debug.Values.Value("lane", laneIndex);
            debug.Values.Value("grounded", grounded);
            debug.Values.Value("gameOver", gameOver);
            debug.Values.Value("character", charLoaded);
            debug.Values.Value("obstacles", obstacles.Count);
            debug.Values.Value("coins-onscreen", coinSpawns.Count);
        }
    }
}
