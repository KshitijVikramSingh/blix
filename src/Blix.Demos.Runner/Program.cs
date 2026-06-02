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
using Blix.Graphics.Vulkan;
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
public static class Program
{
    public static void Main(string[] args)
    {
        var exitAfterFrames = 0; // 0 = interactive; --frames N for the headless gate
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--frames" && int.TryParse(args[i + 1], out var n)) exitAfterFrames = n;
        }

        var loop = new RunnerLoop(exitAfterFrames);
        using var window = new Window(loop, new WindowOptions("Blix — Endless Runner", 1280, 720));
        window.Run();
    }
}

internal sealed class RunnerLoop : IGameLoop, IInputHandler, IDebuggable
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
    private VulkanGraphicsDevice vk = null!;
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
    private AudioSource? jumpSfx;
    private AudioSource? coinSfx;
    private AudioSource? crashSfx;

    // --- Animated character (KayKit Rogue, skinned glTF) ----------------------
    private const float CharScale = 0.9f;
    private const float CharFacing = MathF.PI;     // face -Z (into the screen); flip if backwards
    private ShaderProgramHandle skinnedShader;
    private PipelineHandle skinnedPipeline;
    private VertexBufferHandle[] charVBs = Array.Empty<VertexBufferHandle>();
    private IndexBufferHandle[] charIBs = Array.Empty<IndexBufferHandle>();
    private int[] charIndexCounts = Array.Empty<int>();
    private MaterialHandle charSkinMaterial;       // set 2: albedo (shared, 1 material)
    private MaterialBindings charBones = null!;    // set 3: bone palette, framesInFlight
    private Skeleton charSkeleton = null!;
    private Pose charRestPose = null!;
    private Pose charPose = null!;
    private BonePalette charPalette = null!;
    private byte[] charPalettePayload = Array.Empty<byte>();
    private Matrix4x4 charMeshNodeTransform = Matrix4x4.Identity;
    private AnimationClip charRun = null!;
    private AnimationClip charJump = null!;
    private double charAnimTime;
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
        vk = (VulkanGraphicsDevice)graphicsDevice;

        var vb = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(Cube.Vertices), "cube.vb");
        var ib = vk.CreateIndexBuffer(Cube.Indices, name: "cube.ib");
        var cube = new Mesh("cube", vb, ib, Cube.Indices.Length,
            new Bounds3(new Vector3(-0.5f), new Vector3(0.5f)));

        // The runner supplies its own lit+fog instanced shader (world.vert/frag) +
        // pipeline; the engine's instancing layers only provide the per-instance
        // SSBO (InstanceBuffer) and the staging/draw (InstancedBatch). Fog lives
        // here, in the runner's material. The shader declares InstanceBuffer.Slot at
        // set 3 plus a 112-byte push the runner packs each frame.
        var worldInterface = new ShaderInterface(
            Slots: new[] { InstanceBuffer.Slot },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex | ShaderStages.Fragment, 0, 112) });
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        worldShader = vk.CreateShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "world.vert.spv")),
            File.ReadAllBytes(Path.Combine(shaderDir, "world.frag.spv")),
            worldInterface, "runner.world");
        // Pipeline consumes position + normal (stride matched to the cube vertex).
        var meshLayout = new VertexLayout(
            Stride: VertexPosition3NormalTexture.Layout.Stride,
            Attributes: new[]
            {
                new VertexAttribute(0, VertexAttributeFormat.Float3, 0),
                new VertexAttribute(1, VertexAttributeFormat.Float3, 3 * sizeof(float)),
            });
        var worldPipeline = vk.CreatePipeline(
            new PipelineDescription(worldShader, meshLayout, PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite, RasterizerState.BackFaceCulling, new[] { BlendState.Disabled }),
            "runner.world");
        // Three batches sharing the one world pipeline; tiles stay cubes, obstacles
        // and coins use CC0 KayKit prop meshes (flat-tinted through the same shader).
        var barrel = LoadStaticMesh("barrel.glb") ?? cube;
        var coin = LoadStaticMesh("coin.glb") ?? cube;
        tileBatch = new InstancedBatch(cube, worldPipeline, new InstanceBuffer(vk, worldShader, "tiles"));
        obstacleBatch = new InstancedBatch(barrel, worldPipeline, new InstanceBuffer(vk, worldShader, "obstacles"));
        coinBatch = new InstancedBatch(coin, worldPipeline, new InstanceBuffer(vk, worldShader, "coins"));

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
        hud = new SpriteBatch(vk); // null render target = swapchain
        try
        {
            var assets = new AssetDatabase()
                .RegisterImporter(new FontImporter())
                .LoadManifest(Path.Combine(AppContext.BaseDirectory, "Assets", "manifest.json"));
            hudFont = Font.Upload(vk, assets.Load<FontData>(AssetId.Parse("fonts/bowlby")));
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
        jumpSfx = AudioSource.Create(a, a.CreateClip(SynthBlip(520f, 0.10f, 0.5f, rising: true), "runner.jump"), "runner.jump");
        coinSfx = AudioSource.Create(a, a.CreateClip(SynthBlip(900f, 0.08f, 0.45f, rising: true), "runner.coin"), "runner.coin");
        crashSfx = AudioSource.Create(a, a.CreateClip(SynthBlip(150f, 0.35f, 0.7f, rising: false), "runner.crash"), "runner.crash");
    }

    // Animated character (KayKit Rogue): skinned glTF rendered as its own pass via
    // a bone-palette SSBO (set 3) + albedo (set 2). Best-effort — any failure
    // leaves charLoaded false and the player falls back to the placeholder box.
    private void CreateCharacter(string shaderDir)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "models", "Rogue.glb");
        GltfModel model;
        try
        {
            model = new GltfImporter().Import(new AssetImportContext(AssetId.Parse("models/rogue"), path));
            if (model.Animations.Length == 0) throw new InvalidOperationException("no animations");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Character unavailable, using placeholder box: {ex.Message}");
            return;
        }

        charSkeleton = model.Skeleton;
        charRestPose = charSkeleton.CreateRestPose();
        charPose = charSkeleton.CreateRestPose();
        charPalette = new BonePalette(charSkeleton.BoneCount);
        charPalettePayload = new byte[charSkeleton.BoneCount * 64];
        charMeshNodeTransform = model.MeshNodeTransform;
        charRun = FindClip(model, "Running_A") ?? model.Animations[0];
        charJump = FindClip(model, "Jump_Idle") ?? FindClip(model, "Jump_Full_Short") ?? charRun;

        var boneLayout = new UniformBlockLayout(
            TotalSize: charSkeleton.BoneCount * 64,
            Members: new[] { new UniformBlockMember("bones", 0, charSkeleton.BoneCount * 64, ElementStride: 64) });
        var iface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(2, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                new DescriptorSetSlot(3, 0, ShaderResourceType.StorageBuffer, ShaderStages.Vertex, BlockLayout: boneLayout),
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 128) });
        skinnedShader = vk.CreateShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "skinned.vert.spv")),
            File.ReadAllBytes(Path.Combine(shaderDir, "skinned.frag.spv")), iface, "runner.skinned");
        skinnedPipeline = vk.CreatePipeline(
            new PipelineDescription(skinnedShader, VertexPosition3NormalTextureSkin4Tangent.Layout,
                PrimitiveTopology.Triangles, DepthState.LessEqualWrite, RasterizerState.BackFaceCulling, new[] { BlendState.Disabled }),
            "runner.skinned");

        // One VB/IB per primitive; all 12 share one skin material + one bone palette.
        var n = model.Primitives.Length;
        charVBs = new VertexBufferHandle[n];
        charIBs = new IndexBufferHandle[n];
        charIndexCounts = new int[n];
        GltfTexture? albedo = null;
        for (var i = 0; i < n; i++)
        {
            var mesh = model.Primitives[i].Mesh;
            charVBs[i] = vk.CreateVertexBuffer(
                new VertexBufferData(new VertexBufferDescription(mesh.Layout, mesh.VertexCount, GraphicsBufferUsage.Static), mesh.VertexBytes),
                $"rogue.vb{i}");
            charIBs[i] = mesh.Indices32 is { } u32
                ? vk.CreateIndexBuffer(u32, name: $"rogue.ib{i}")
                : vk.CreateIndexBuffer(mesh.Indices, name: $"rogue.ib{i}");
            charIndexCounts[i] = mesh.IndexCount;
            albedo ??= model.Primitives[i].Material?.BaseColorTexture;
        }

        charSkinMaterial = vk.CreateMaterial(skinnedShader, name: "rogue.skin").SetTexture(0, UploadAlbedo(albedo)).Handle;
        charBones = vk.CreateMaterial(skinnedShader, setIndex: 3, framesInFlight: vk.MaxFramesInFlightCount, name: "rogue.bones");
        charLoaded = true;
    }

    // Advance the character's animation, upload its bone palette for this frame,
    // and pack the skinned push (model + view-projection). Run cadence scales with
    // run speed; airborne switches to the jump clip; game-over freezes the pose.
    private void UpdateCharacter(Time time)
    {
        var clip = grounded ? charRun : charJump;
        var rate = gameOver ? 0f : (grounded ? currentSpeed / BaseSpeed : 1f);
        charAnimTime += time.Delta * rate;
        var dur = clip.Duration > 0 ? clip.Duration : 1.0;
        charPose.CopyFrom(charRestPose);
        clip.Sample(charAnimTime % dur, charPose);
        charSkeleton.ComputeBonePalette(charPose, charPalette);
        PackPalette(charPalette, charPalettePayload);
        charBones.WriteBuffer(vk.CurrentFrameSlot, 0, charPalettePayload);

        var user = Matrix4x4.CreateScale(CharScale)
                 * Matrix4x4.CreateRotationY(CharFacing)
                 * Matrix4x4.CreateTranslation(player.Position.X, player.Position.Y, 0f);
        var model = charMeshNodeTransform * user;
        MemoryMarshal.Write(skinnedPush.AsSpan(0, 64), in model);
        MemoryMarshal.Write(skinnedPush.AsSpan(64, 64), in viewProj);
    }

    private static AnimationClip? FindClip(GltfModel model, string name)
    {
        foreach (var c in model.Animations) if (c.Name == name) return c;
        return null;
    }

    // Load a static CC0 prop glb (single-material → one primitive) as a Mesh in the
    // stride-32 VertexPosition3NormalTexture layout the world pipeline expects.
    // Returns null on any failure so the caller can fall back to the cube.
    private Mesh? LoadStaticMesh(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "models", fileName);
        try
        {
            var model = new GltfStaticImporter().Import(new AssetImportContext(AssetId.Parse($"models/{fileName}"), path));
            if (model.Primitives.Length == 0) return null;
            var m = model.Primitives[0].Mesh;
            var vb = vk.CreateVertexBuffer(
                new VertexBufferData(new VertexBufferDescription(m.Layout, m.VertexCount, GraphicsBufferUsage.Static), m.VertexBytes),
                $"{fileName}.vb");
            var ib = m.Indices32 is { } u32
                ? vk.CreateIndexBuffer(u32, name: $"{fileName}.ib")
                : vk.CreateIndexBuffer(m.Indices, name: $"{fileName}.ib");
            return new Mesh(fileName, vb, ib, m.IndexCount, m.Bounds);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Prop '{fileName}' unavailable, using cube: {ex.Message}");
            return null;
        }
    }

    private TextureHandle UploadAlbedo(GltfTexture? tex)
    {
        if (tex?.MipBytes is { Count: > 0 } mips)
        {
            return vk.CreateTexture2D(
                new TextureDescription(tex.Width, tex.Height, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
                mips[0], "rogue.albedo");
        }
        var px = new byte[4 * 4 * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = 210; px[i + 1] = 180; px[i + 2] = 140; px[i + 3] = 255; }
        return vk.CreateTexture2D(
            new TextureDescription(4, 4, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat), px, "rogue.albedo.fallback");
    }

    // Pack the bone palette into std430 bytes (row-major; read column-major in GLSL
    // = transpose, which matches the engine's row-vector matrices). Mirrors VulkanLit.
    private static void PackPalette(BonePalette palette, byte[] dst)
    {
        var f = MemoryMarshal.Cast<byte, float>(dst.AsSpan());
        for (var i = 0; i < palette.BoneCount; i++)
        {
            var m = palette.Matrices[i];
            var o = i * 16;
            f[o + 0] = m.M11; f[o + 1] = m.M12; f[o + 2] = m.M13; f[o + 3] = m.M14;
            f[o + 4] = m.M21; f[o + 5] = m.M22; f[o + 6] = m.M23; f[o + 7] = m.M24;
            f[o + 8] = m.M31; f[o + 9] = m.M32; f[o + 10] = m.M33; f[o + 11] = m.M34;
            f[o + 12] = m.M41; f[o + 13] = m.M42; f[o + 14] = m.M43; f[o + 15] = m.M44;
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
        skyVb = vk.CreateVertexBuffer(new VertexBufferData(new VertexBufferDescription(fsLayout, 3, GraphicsBufferUsage.Static), bytes), "sky.vb");
        skyIb = vk.CreateIndexBuffer(new ushort[] { 0, 1, 2 }, name: "sky.ib");

        var skyInterface = new ShaderInterface(
            Slots: Array.Empty<DescriptorSetSlot>(),
            PushConstants: new[] { new PushConstantRange(ShaderStages.Fragment, 0, 96) });

        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var vert = File.ReadAllBytes(Path.Combine(shaderDir, "sky.vert.spv"));
        var frag = File.ReadAllBytes(Path.Combine(shaderDir, "sky.frag.spv"));
        skyProgram = vk.CreateShaderProgramFromSpv(vert, frag, skyInterface, "sky");

        skyPipeline = vk.CreatePipeline(
            new PipelineDescription(
                skyProgram, fsLayout, PrimitiveTopology.Triangles,
                DepthState.Disabled, RasterizerState.NoCulling, new[] { BlendState.Disabled }),
            name: "sky");
    }

    public void OnUpdate(Time time)
    {
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
                    for (var i = 0; i < charVBs.Length; i++)
                    {
                        pass.DrawIndexed(charVBs[i], charIBs[i], skinnedPipeline, charIndexCounts[i],
                            Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(),
                            charSkinMaterial, charBones.Handle, skinnedPush);
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

    public void OnKeyDown(Key key)
    {
        if (key == Key.Escape) { host.RequestClose(); return; }

        if (gameOver)
        {
            if (key is Key.Enter or Key.R) Restart();
            return;
        }

        switch (key)
        {
            case Key.Left or Key.A:
                laneIndex = Math.Max(0, laneIndex - 1);
                break;
            case Key.Right or Key.D:
                laneIndex = Math.Min(LaneX.Length - 1, laneIndex + 1);
                break;
            case Key.Up or Key.W or Key.Space:
                if (grounded) { physics.Velocity = new Vector3(0f, JumpSpeed, 0f); grounded = false; PlaySfx(jumpSfx, 1f); }
                break;
        }
    }

    // Diagnostics: toggle the overlay with the ` (grave) key. Draws the exact
    // collision spheres ResolveCollisions tests, so coin↔obstacle overlap is
    // visible (a coin run from one segment can reach an adjacent segment's lane).
    public string DebugName => "runner";

    public void Debug(DebugContext debug)
    {
        debug.Draw.ViewProjection = viewProj;

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
