using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Audio;
using Blix.Core;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Primitives;
using Blix.Render;
using Blix.Runtime.OpenTK;

using var window = new Window(
    new Pong3DGame(),
    new WindowOptions("Blix . Pong 3D", 1280, 720));
window.Run();

internal sealed class Pong3DGame : Game, IInputHandler
{
    // Arena dims (world units). X is the long axis between paddles, Y is up,
    // Z is depth. Paddles live at the X ends, ball flies between them, four
    // rails (top/bottom/near/far) bounce the ball back into play.
    private const float ArenaHalfX = 10.0f;
    private const float ArenaHalfY = 4.0f;
    private const float ArenaHalfZ = 3.0f;
    private const float RailThickness = 0.20f;

    private const float PaddleSizeY = 1.60f;
    private const float PaddleSizeZ = 1.20f;
    private const float PaddleSizeX = 0.30f;
    private const float PaddleInset = 0.10f;        // distance behind ArenaHalfX
    private const float PaddleMaxSpeed = 9.0f;
    private const float PaddleAccelTau = 0.07f;

    private const float BallRadius = 0.30f;
    private const float BallSpeedStart = 7.0f;
    private const float BallSpeedMax = 13.0f;
    private const float BallSpeedupPerHit = 0.55f;

    private const float HitstopPaddle = 0.05f;
    private const float HitstopGoal = 0.12f;

    private const int TrailLength = 18;
    private const int WinScore = 11;
    private const float WinFlashDuration = 1.0f;

    protected override double FixedStep => 1.0 / 120.0;

    private readonly HashSet<Key> heldKeys = new();
    private readonly Random rng = new();

    // Paddle state: position is (Y, Z) on its end-plane; velocity matches.
    // Recoil is an outward X offset that decays after a hit.
    private Vector2 leftPaddle = Vector2.Zero;
    private Vector2 rightPaddle = Vector2.Zero;
    private Vector2 leftPaddleVel = Vector2.Zero;
    private Vector2 rightPaddleVel = Vector2.Zero;
    private float leftPaddleRecoil;
    private float rightPaddleRecoil;

    private Vector3 ballPos = Vector3.Zero;
    private Vector3 ballVel = Vector3.Zero;
    private int leftScore;
    private int rightScore;
    private int nextServeDir;
    private bool waitingForServe = true;
    private double totalTime;

    private float hitstopTimer;
    private float shakeAmp;
    private Vector3 shakeOffset;

    private int winner;
    private float winFlashTimer;

    private readonly Vector3[] trail = new Vector3[TrailLength];
    private int trailCount;
    private int trailHead;

    // GPU resources
    private Mesh cubeMesh = null!;
    private Mesh sphereMesh = null!;
    private Material litMaterial = null!;
    private Camera3D camera = null!;
    private Vector3 baseCameraEye;
    private Vector3 cameraTarget;

    // 2D HUD on top of the 3D scene.
    private SpriteBatch spriteBatch = null!;
    private TextureHandle whitePixel;
    private Font? hudFont;

    private AudioClipHandle paddleClip = AudioClipHandle.Invalid;
    private AudioClipHandle wallClip = AudioClipHandle.Invalid;
    private AudioClipHandle scoreClip = AudioClipHandle.Invalid;
    private AudioSource? paddleSource;
    private AudioSource? wallSource;
    private AudioSource? scoreSource;

    protected override void OnLoad()
    {
        Host.SetTitle("Blix . Pong 3D");

        // --- Meshes ---------------------------------------------------------
        var cube = BuildCubeMesh();
        var cubeVerts = GraphicsDevice.CreateVertexBuffer(
            VertexPosition3NormalTexture.CreateBufferData(cube.Vertices),
            name: "pong3d.cube.verts");
        var cubeIndices = GraphicsDevice.CreateIndexBuffer(cube.Indices, name: "pong3d.cube.indices");
        cubeMesh = new Mesh("pong3d.cube", cubeVerts, cubeIndices, cube.Indices.Length, Bounds3.Empty);

        var sphereVerts = GraphicsDevice.CreateVertexBuffer(
            VertexPosition3NormalTexture.CreateBufferData(Icosphere.Vertices),
            name: "pong3d.sphere.verts");
        var sphereIndices = GraphicsDevice.CreateIndexBuffer(Icosphere.Indices, name: "pong3d.sphere.indices");
        sphereMesh = new Mesh("pong3d.sphere", sphereVerts, sphereIndices, Icosphere.Indices.Length, Bounds3.Empty);

        // --- Lit pipeline ---------------------------------------------------
        var litShader = GraphicsDevice.CreateShaderProgram(new ShaderSources(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "lit.vert")),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "lit.frag")),
            VertexName: "pong3d.lit.vert",
            FragmentName: "pong3d.lit.frag"));
        var litPipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                litShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                BlendState.Disabled),
            name: "pong3d.lit");
        litMaterial = new Material("pong3d.lit", litPipeline);

        // --- Camera ---------------------------------------------------------
        baseCameraEye = new Vector3(0.0f, 5.5f, 14.5f);
        cameraTarget = new Vector3(0.0f, 0.0f, 0.0f);
        camera = new Camera3D
        {
            Transform = new Transform3D { Position = baseCameraEye },
            VerticalFieldOfView = MathF.PI / 4.2f,
            NearPlane = 0.5f,
            FarPlane = 80.0f
        };
        camera.Transform.LookAt(cameraTarget, Vector3.UnitY);

        // --- HUD ------------------------------------------------------------
        spriteBatch = new SpriteBatch(GraphicsDevice);
        whitePixel = GraphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.PixelatedRepeat),
            new byte[] { 255, 255, 255, 255 },
            name: "pong3d.white");

        try
        {
            var assets = new AssetDatabase()
                .RegisterImporter(new FontImporter())
                .LoadManifest(Path.Combine(AppContext.BaseDirectory, "Assets", "manifest.json"));
            var fontData = assets.Load<FontData>(AssetId.Parse("fonts/bowlby"));
            hudFont = Font.Upload(GraphicsDevice, fontData);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Font unavailable: {ex.Message}");
        }

        // --- Audio ----------------------------------------------------------
        if (AudioDevice is { } audio)
        {
            paddleClip = audio.CreateClip(SynthBeep(660.0f, 0.06f, 0.6f), name: "pong3d.paddle");
            wallClip   = audio.CreateClip(SynthBeep(330.0f, 0.05f, 0.5f), name: "pong3d.wall");
            scoreClip  = audio.CreateClip(SynthBeep(180.0f, 0.30f, 0.7f), name: "pong3d.score");
            paddleSource = AudioSource.Create(audio, paddleClip, name: "pong3d.paddle");
            wallSource   = AudioSource.Create(audio, wallClip,   name: "pong3d.wall");
            scoreSource  = AudioSource.Create(audio, scoreClip,  name: "pong3d.score");
        }

        ResetBall(serveDir: 0);
    }

    public override void OnUnload()
    {
        if (AudioDevice is { } audio)
        {
            paddleSource?.Dispose(audio);
            wallSource?.Dispose(audio);
            scoreSource?.Dispose(audio);
            if (paddleClip.Id != AudioClipHandle.Invalid.Id) audio.DeleteClip(paddleClip);
            if (wallClip.Id   != AudioClipHandle.Invalid.Id) audio.DeleteClip(wallClip);
            if (scoreClip.Id  != AudioClipHandle.Invalid.Id) audio.DeleteClip(scoreClip);
        }
    }

    void IInputHandler.OnKeyDown(Key key)
    {
        heldKeys.Add(key);
        if (key == Key.Escape) Host.RequestClose();
        else if (key == Key.R) ResetMatch();
        else if (key == Key.Space)
        {
            if (winner != 0) ResetMatch();
            else if (waitingForServe) LaunchBall();
        }
    }

    void IInputHandler.OnKeyUp(Key key) => heldKeys.Remove(key);

    public override void OnUpdate(Time time)
    {
        totalTime = time.Total;
        var dt = (float)time.Delta;
        if (dt <= 0.0f) return;

        hitstopTimer = MathF.Max(0.0f, hitstopTimer - dt);
        shakeAmp    *= MathF.Exp(-dt / 0.12f);
        leftPaddleRecoil  *= MathF.Exp(-dt / 0.07f);
        rightPaddleRecoil *= MathF.Exp(-dt / 0.07f);
        winFlashTimer = MathF.Max(0.0f, winFlashTimer - dt);

        // Refresh a random unit vector each frame so shake jitters rather than
        // pans. Y-amplitude halved so vertical wobble stays subtle.
        var ax = (float)(rng.NextDouble() * 2.0 * Math.PI);
        var ay = (float)(rng.NextDouble() * 2.0 * Math.PI);
        shakeOffset = new Vector3(MathF.Cos(ax), MathF.Sin(ay) * 0.5f, MathF.Sin(ax)) * shakeAmp;
    }

    public override void OnFixedUpdate(Time time)
    {
        var step = (float)time.Delta;
        if (hitstopTimer > 0.0f) return;

        // --- Paddle input + integration -----------------------------------
        // Left paddle: WASD (W up, S down, A near-side, D far-side relative to
        // camera). Right paddle: arrow keys (Up/Down on Y, Left/Right on Z).
        // Both players' "left" is screen-left (positive Z toward camera).
        var leftTarget = Vector2.Zero;
        if (heldKeys.Contains(Key.W)) leftTarget.X += PaddleMaxSpeed;
        if (heldKeys.Contains(Key.S)) leftTarget.X -= PaddleMaxSpeed;
        if (heldKeys.Contains(Key.A)) leftTarget.Y += PaddleMaxSpeed;
        if (heldKeys.Contains(Key.D)) leftTarget.Y -= PaddleMaxSpeed;

        var rightTarget = Vector2.Zero;
        if (heldKeys.Contains(Key.Up))    rightTarget.X += PaddleMaxSpeed;
        if (heldKeys.Contains(Key.Down))  rightTarget.X -= PaddleMaxSpeed;
        if (heldKeys.Contains(Key.Left))  rightTarget.Y += PaddleMaxSpeed;
        if (heldKeys.Contains(Key.Right)) rightTarget.Y -= PaddleMaxSpeed;

        var accel = 1.0f - MathF.Exp(-step / PaddleAccelTau);
        leftPaddleVel  += (leftTarget  - leftPaddleVel)  * accel;
        rightPaddleVel += (rightTarget - rightPaddleVel) * accel;
        leftPaddle  += leftPaddleVel  * step;
        rightPaddle += rightPaddleVel * step;

        ClampPaddle(ref leftPaddle, ref leftPaddleVel);
        ClampPaddle(ref rightPaddle, ref rightPaddleVel);

        if (waitingForServe) return;

        // --- Ball integration + trail sample -------------------------------
        ballPos += ballVel * step;
        trail[trailHead] = ballPos;
        trailHead = (trailHead + 1) % TrailLength;
        if (trailCount < TrailLength) trailCount++;

        // --- Rail bounces (top/bottom in Y, near/far in Z) -----------------
        if (ballPos.Y - BallRadius < -ArenaHalfY && ballVel.Y < 0) { ballPos.Y = -ArenaHalfY + BallRadius; ballVel.Y = -ballVel.Y; OnWallHit(); }
        else if (ballPos.Y + BallRadius > ArenaHalfY && ballVel.Y > 0) { ballPos.Y = ArenaHalfY - BallRadius; ballVel.Y = -ballVel.Y; OnWallHit(); }
        if (ballPos.Z - BallRadius < -ArenaHalfZ && ballVel.Z < 0) { ballPos.Z = -ArenaHalfZ + BallRadius; ballVel.Z = -ballVel.Z; OnWallHit(); }
        else if (ballPos.Z + BallRadius > ArenaHalfZ && ballVel.Z > 0) { ballPos.Z = ArenaHalfZ - BallRadius; ballVel.Z = -ballVel.Z; OnWallHit(); }

        // --- Paddle collisions ---------------------------------------------
        var leftPlaneX = -ArenaHalfX + PaddleInset + PaddleSizeX;
        var rightPlaneX =  ArenaHalfX - PaddleInset - PaddleSizeX;
        CheckPaddle(leftPaddle,  leftPlaneX,  requireVxSign: -1, isLeft: true);
        CheckPaddle(rightPaddle, rightPlaneX, requireVxSign: +1, isLeft: false);

        // --- Goal ----------------------------------------------------------
        if (ballPos.X + BallRadius < -ArenaHalfX) { rightScore++; OnGoal(); ResetBall(serveDir: -1); }
        else if (ballPos.X - BallRadius > ArenaHalfX) { leftScore++; OnGoal(); ResetBall(serveDir: +1); }
    }

    private void ClampPaddle(ref Vector2 p, ref Vector2 v)
    {
        var limY = ArenaHalfY - PaddleSizeY * 0.5f;
        var limZ = ArenaHalfZ - PaddleSizeZ * 0.5f;
        if (p.X < -limY) { p.X = -limY; v.X = 0; }
        else if (p.X > limY) { p.X = limY; v.X = 0; }
        if (p.Y < -limZ) { p.Y = -limZ; v.Y = 0; }
        else if (p.Y > limZ) { p.Y = limZ; v.Y = 0; }
    }

    private void CheckPaddle(Vector2 paddle, float planeX, int requireVxSign, bool isLeft)
    {
        // Test ball-AABB overlap with the paddle volume on its end-plane.
        // Paddle local: ±PaddleSizeY/2 on Y, ±PaddleSizeZ/2 on Z, ±PaddleSizeX/2 on X
        // around (planeX, paddle.X, paddle.Y).
        var hx = PaddleSizeX * 0.5f;
        var hy = PaddleSizeY * 0.5f;
        var hz = PaddleSizeZ * 0.5f;
        var dx = ballPos.X - planeX;
        var dy = ballPos.Y - paddle.X;
        var dz = ballPos.Z - paddle.Y;
        if (MathF.Abs(dx) >= hx + BallRadius) return;
        if (MathF.Abs(dy) >= hy + BallRadius) return;
        if (MathF.Abs(dz) >= hz + BallRadius) return;
        if (Math.Sign(ballVel.X) != requireVxSign) return;

        // Snap to just outside the paddle on X.
        ballPos.X = requireVxSign < 0
            ? planeX + hx + BallRadius
            : planeX - hx - BallRadius;

        // 5-zone quantised returns per axis (Y and Z). Each axis returns a
        // discrete offsetT in {-1, -0.5, 0, 0.5, 1}. Two axes give a 5x5 grid
        // of return angles — a real skill ceiling vs. the smooth gradient.
        var offsetY = ZoneOffset(dy / hy);
        var offsetZ = ZoneOffset(dz / hz);
        ballVel.X = -ballVel.X;
        var speed = ballVel.Length();
        ballVel.Y += offsetY * speed * 0.8f;
        ballVel.Z += offsetZ * speed * 0.8f;
        var newSpeed = MathF.Min(speed + BallSpeedupPerHit, BallSpeedMax);
        ballVel = Vector3.Normalize(ballVel) * newSpeed;

        var speedT = newSpeed / BallSpeedMax;
        hitstopTimer = HitstopPaddle;
        shakeAmp = MathF.Max(shakeAmp, 0.08f + 0.18f * speedT);
        if (isLeft) leftPaddleRecoil  = -0.20f;
        else        rightPaddleRecoil = 0.20f;

        PlaySfx(paddleSource, pitch: 0.85f + 0.4f * MathF.Max(MathF.Abs(offsetY), MathF.Abs(offsetZ)));
    }

    private static float ZoneOffset(float dyN)
    {
        var clamped = Math.Clamp(dyN, -1.0f, 1.0f);
        var zone = Math.Clamp((int)MathF.Floor((clamped + 1.0f) * 2.5f), 0, 4);
        return (zone - 2) * 0.5f;
    }

    private void OnWallHit()
    {
        var speedT = ballVel.Length() / BallSpeedMax;
        shakeAmp = MathF.Max(shakeAmp, 0.04f + 0.06f * speedT);
        PlaySfx(wallSource, pitch: 1.0f);
    }

    private void OnGoal()
    {
        hitstopTimer = HitstopGoal;
        shakeAmp = MathF.Max(shakeAmp, 0.22f);
        PlaySfx(scoreSource, pitch: 1.0f);
        if (leftScore >= WinScore) winner = -1;
        else if (rightScore >= WinScore) winner = +1;
        if (winner != 0)
        {
            winFlashTimer = WinFlashDuration;
            shakeAmp = MathF.Max(shakeAmp, 0.35f);
            hitstopTimer = MathF.Max(hitstopTimer, 0.35f);
        }
    }

    private void ResetMatch()
    {
        leftScore = 0; rightScore = 0;
        winner = 0;
        winFlashTimer = 0.0f;
        ResetBall(serveDir: 0);
    }

    private void ResetBall(int serveDir)
    {
        ballPos = Vector3.Zero;
        ballVel = Vector3.Zero;
        nextServeDir = serveDir;
        waitingForServe = true;
        trailCount = 0; trailHead = 0;
    }

    private void LaunchBall()
    {
        var dirX = nextServeDir == 0
            ? (rng.NextDouble() < 0.5 ? -1.0f : 1.0f)
            : (float)nextServeDir;
        // Random spherical-ish serve: pitch in [15, 38] off the X axis, yaw in
        // [-30, 30]. Ensures every serve has visible Y AND Z motion without
        // tipping over into a wall-bouncer.
        var pitchMag = (float)(Math.PI / 12.0 + rng.NextDouble() * (Math.PI / 4.74 - Math.PI / 12.0));
        var pitchSign = rng.NextDouble() < 0.5 ? -1.0f : 1.0f;
        var yawMag = (float)(rng.NextDouble() * (Math.PI / 6.0));
        var yawSign = rng.NextDouble() < 0.5 ? -1.0f : 1.0f;
        var pitch = pitchMag * pitchSign;
        var yaw = yawMag * yawSign;
        var dir = new Vector3(
            dirX * MathF.Cos(pitch) * MathF.Cos(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Sin(yaw));
        ballVel = Vector3.Normalize(dir) * BallSpeedStart;
        waitingForServe = false;
    }

    // ------------------------------------------------------------------- Render

    public override void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return;

        var aspect = (float)frame.Width / frame.Height;
        // Camera shake = perturb both eye and target by the same offset so the
        // view rotation stays put; only the position jitters. Decays in OnUpdate.
        camera.Transform.Position = baseCameraEye + shakeOffset;
        camera.Transform.LookAt(cameraTarget + shakeOffset, Vector3.UnitY);

        var view = camera.GetView();
        var proj = camera.GetProjection(aspect);

        var ballColor = BallColor();
        var (lightColor, lightIntensity) = BallLight(ballColor);

        var sharedUniforms = new ShaderUniform[]
        {
            new("uView", new Matrix4x4Uniform(view)),
            new("uProjection", new Matrix4x4Uniform(proj)),
            new("uCameraPosition", new Vector3Uniform(camera.Transform.Position)),
            new("uAmbient", new Vector3Uniform(new Vector3(0.08f, 0.07f, 0.13f))),
            new("uDirLightDir", new Vector3Uniform(Vector3.Normalize(new Vector3(-0.4f, -1.0f, -0.3f)))),
            new("uDirLightColor", new Vector3Uniform(new Vector3(0.55f, 0.60f, 0.75f))),
            new("uPointPosition", new Vector3Uniform(ballPos)),
            new("uPointColor", new Vector3Uniform(lightColor)),
            new("uPointIntensity", new FloatUniform(lightIntensity)),
            new("uPointRange", new FloatUniform(14.0f))
        };

        commandList.Pass(
            "pong3d.scene",
            new RenderPassDescription(
                RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { ScenedBackgroundColor() },
                ClearDepth: true),
            pass =>
            {
                DrawArena(pass, sharedUniforms);
                DrawPaddles(pass, sharedUniforms);
                DrawTrail(pass, sharedUniforms, ballColor);
                DrawBall(pass, sharedUniforms, ballColor);
            });

        DrawHud(frame, commandList);
    }

    private GraphicsColor ScenedBackgroundColor()
    {
        // Slight purple/indigo to read as "y2k cyber" before lighting kicks in.
        var flashT = winFlashTimer / WinFlashDuration;
        var baseBg = new Vector3(0.025f, 0.020f, 0.060f);
        var flashRgb = winner < 0
            ? new Vector3(0.10f, 0.30f, 0.35f)
            : new Vector3(0.30f, 0.15f, 0.30f);
        var col = baseBg + flashRgb * (flashT * flashT * 0.6f);
        return new GraphicsColor(col.X, col.Y, col.Z, 1.0f);
    }

    private void DrawArena(RenderPassBuilder pass, IReadOnlyList<ShaderUniform> shared)
    {
        // Rails: four long thin boxes along the corridor axis. Sit just outside
        // the play volume so the ball never visibly clips the wall.
        var railColor = new Vector3(0.22f, 0.18f, 0.42f);
        var spec = 0.35f; var shine = 28.0f; var emi = 0.0f;
        // Top + bottom rails (long along X, thin Y, full Z)
        DrawBox(pass, shared,
            new Vector3(0, ArenaHalfY + RailThickness * 0.5f, 0),
            new Vector3(ArenaHalfX * 2 + RailThickness, RailThickness, ArenaHalfZ * 2 + RailThickness),
            railColor, spec, shine, emi);
        DrawBox(pass, shared,
            new Vector3(0, -ArenaHalfY - RailThickness * 0.5f, 0),
            new Vector3(ArenaHalfX * 2 + RailThickness, RailThickness, ArenaHalfZ * 2 + RailThickness),
            railColor, spec, shine, emi);
        // Far rail only (long along X, full Y, thin Z, at -Z back wall).
        // We deliberately skip drawing the near rail (+Z): with the camera at
        // +Z looking back at origin, the near rail would be a solid wall right
        // in front of the lens, occluding the whole play volume. The ball
        // still bounces off that plane in physics — it's just an invisible
        // boundary on the front side.
        DrawBox(pass, shared,
            new Vector3(0, 0, -ArenaHalfZ - RailThickness * 0.5f),
            new Vector3(ArenaHalfX * 2 + RailThickness, ArenaHalfY * 2, RailThickness),
            new Vector3(0.28f, 0.22f, 0.50f), spec, shine, emi);
    }

    private void DrawPaddles(RenderPassBuilder pass, IReadOnlyList<ShaderUniform> shared)
    {
        var leftColor = new Vector3(0.40f, 0.85f, 1.00f);   // P1 cyan
        var rightColor = new Vector3(1.00f, 0.45f, 0.85f);  // P2 magenta
        var leftX = -ArenaHalfX + PaddleInset + PaddleSizeX * 0.5f + leftPaddleRecoil;
        var rightX = ArenaHalfX - PaddleInset - PaddleSizeX * 0.5f + rightPaddleRecoil;
        DrawBox(pass, shared,
            new Vector3(leftX, leftPaddle.X, leftPaddle.Y),
            new Vector3(PaddleSizeX, PaddleSizeY, PaddleSizeZ),
            leftColor, specular: 0.6f, shininess: 64.0f, emissive: 0.55f);
        DrawBox(pass, shared,
            new Vector3(rightX, rightPaddle.X, rightPaddle.Y),
            new Vector3(PaddleSizeX, PaddleSizeY, PaddleSizeZ),
            rightColor, specular: 0.6f, shininess: 64.0f, emissive: 0.55f);
    }

    private void DrawTrail(RenderPassBuilder pass, IReadOnlyList<ShaderUniform> shared, Vector3 ballColor)
    {
        if (trailCount == 0 || waitingForServe) return;
        for (var i = 0; i < trailCount; i++)
        {
            var idx = ((trailHead - trailCount + i) % TrailLength + TrailLength) % TrailLength;
            var p = trail[idx];
            var t = (i + 1) / (float)trailCount;
            var size = BallRadius * (0.20f + 0.65f * t);
            var emi = 0.20f + 0.50f * t;
            DrawSphere(pass, shared, p, size * 2.0f, ballColor, specular: 0.0f, shininess: 8.0f, emissive: emi);
        }
    }

    private void DrawBall(RenderPassBuilder pass, IReadOnlyList<ShaderUniform> shared, Vector3 ballColor)
    {
        DrawSphere(pass, shared, ballPos, BallRadius * 2.0f, ballColor,
            specular: 1.0f, shininess: 96.0f, emissive: 0.95f);
    }

    private void DrawBox(RenderPassBuilder pass, IReadOnlyList<ShaderUniform> shared,
        Vector3 position, Vector3 size, Vector3 albedo,
        float specular, float shininess, float emissive)
    {
        var model = GraphicsMatrices.CreateModel(position, Quaternion.Identity, size);
        DrawMeshLit(pass, cubeMesh, model, shared, albedo, specular, shininess, emissive);
    }

    private void DrawSphere(RenderPassBuilder pass, IReadOnlyList<ShaderUniform> shared,
        Vector3 position, float diameter, Vector3 albedo,
        float specular, float shininess, float emissive)
    {
        // Icosphere is a unit sphere of radius 0.5 -> scale by diameter to hit
        // the requested visible diameter.
        var model = GraphicsMatrices.CreateModel(position, Quaternion.Identity, new Vector3(diameter));
        DrawMeshLit(pass, sphereMesh, model, shared, albedo, specular, shininess, emissive);
    }

    private void DrawMeshLit(RenderPassBuilder pass, Mesh mesh, Matrix4x4 model,
        IReadOnlyList<ShaderUniform> shared,
        Vector3 albedo, float specular, float shininess, float emissive)
    {
        var perDraw = new ShaderUniform[shared.Count + 5];
        for (var i = 0; i < shared.Count; i++) perDraw[i] = shared[i];
        var k = shared.Count;
        perDraw[k++] = new ShaderUniform("uModel", new Matrix4x4Uniform(model));
        perDraw[k++] = new ShaderUniform("uNormalMatrix",
            new Matrix4x4Uniform(GraphicsMatrices.CreateNormalMatrix(model)));
        perDraw[k++] = new ShaderUniform("uMaterialAlbedo", new Vector3Uniform(albedo));
        perDraw[k++] = new ShaderUniform("uMaterialSpecular", new FloatUniform(specular));
        perDraw[k++] = new ShaderUniform("uMaterialShininess", new FloatUniform(shininess));
        // emissive packed via a fresh uniform slot (not in the loop above so it
        // can vary per draw cheaply)
        var withEmissive = new ShaderUniform[perDraw.Length + 1];
        Array.Copy(perDraw, withEmissive, perDraw.Length);
        withEmissive[perDraw.Length] = new ShaderUniform("uMaterialEmissive", new FloatUniform(emissive));

        pass.DrawMesh(mesh, litMaterial, perDrawUniforms: withEmissive, perDrawTextures: null);
    }

    private Vector3 BallColor()
    {
        var speed = ballVel.Length();
        var t = MathF.Min(speed / BallSpeedMax, 1.0f);
        if (t < 0.5f)
        {
            var u = t * 2.0f;
            return new Vector3(
                Lerp(0.45f, 1.00f, u),
                Lerp(0.85f, 0.97f, u),
                Lerp(1.00f, 0.95f, u));
        }
        else
        {
            var u = (t - 0.5f) * 2.0f;
            return new Vector3(
                Lerp(1.00f, 1.00f, u),
                Lerp(0.97f, 0.45f, u),
                Lerp(0.95f, 0.55f, u));
        }
    }

    private (Vector3 color, float intensity) BallLight(Vector3 ballColor)
    {
        var speed = ballVel.Length();
        var t = MathF.Min(speed / BallSpeedMax, 1.0f);
        return (ballColor, 1.2f + 0.6f * t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // ------------------------------------------------------------------- HUD

    private void DrawHud(RenderFrameContext frame, RenderCommandList commandList)
    {
        if (hudFont is null) return;
        var (logicalW, logicalH) = Host.LogicalSize;
        if (logicalW <= 0 || logicalH <= 0) return;
        var dpiScale = frame.Width / (float)logicalW;
        var ortho = GraphicsMatrices.CreateOrthographicOffCenter(
            0, logicalW, logicalH, 0, -1, 1);

        // Sprite-batch HUD on top of the 3D scene. SpriteBatch pipeline has
        // DepthState.Disabled so it ignores the scene's depth buffer; no clear.
        commandList.Pass(
            "pong3d.hud",
            new RenderPassDescription(
                RenderSurfaceHandle.Default,
                ClearColors: Array.Empty<GraphicsColor?>(),
                ClearDepth: false),
            pass =>
            {
                spriteBatch.Begin(ortho);

                // Scores. Centered on each half, semi-transparent so the 3D
                // scene still reads behind them.
                var scoreColor = new GraphicsColor(0.85f, 0.90f, 1.00f, 0.55f);
                const float scoreSize = 120.0f;
                DrawScore(leftScore.ToString("00"), logicalW * 0.25f, logicalH * 0.18f, scoreSize, dpiScale, scoreColor);
                DrawScore(rightScore.ToString("00"), logicalW * 0.75f, logicalH * 0.18f, scoreSize, dpiScale, scoreColor);

                if (winner != 0)
                {
                    var label = winner < 0 ? "P1 WINS" : "P2 WINS";
                    var winColor = winner < 0
                        ? new GraphicsColor(0.55f, 0.95f, 1.00f, 0.95f)
                        : new GraphicsColor(1.00f, 0.65f, 0.92f, 0.95f);
                    const float winSize = 96.0f;
                    var m = SpriteBatchUiExtensions.MeasureText(hudFont, winSize, label, dpiScale);
                    spriteBatch.DrawText(hudFont, winSize, label,
                        new Vector2(logicalW * 0.5f - m.X * 0.5f, logicalH * 0.5f - m.Y * 0.5f),
                        winColor, dpiScale: dpiScale);
                    const string sub = "PRESS SPACE";
                    const float subSize = 26.0f;
                    var sm = SpriteBatchUiExtensions.MeasureText(hudFont, subSize, sub, dpiScale);
                    spriteBatch.DrawText(hudFont, subSize, sub,
                        new Vector2(logicalW * 0.5f - sm.X * 0.5f, logicalH * 0.5f + m.Y * 0.5f + 16.0f),
                        new GraphicsColor(0.95f, 0.55f, 0.85f, 0.85f),
                        dpiScale: dpiScale);
                }
                else if (waitingForServe)
                {
                    const string prompt = "PRESS SPACE";
                    const float promptSize = 36.0f;
                    var m = SpriteBatchUiExtensions.MeasureText(hudFont, promptSize, prompt, dpiScale);
                    spriteBatch.DrawText(hudFont, promptSize, prompt,
                        new Vector2(logicalW * 0.5f - m.X * 0.5f, logicalH - 90.0f),
                        new GraphicsColor(0.95f, 0.55f, 0.85f, 0.95f),
                        dpiScale: dpiScale);
                }

                const string p1Help = "P1   W/A/S/D";
                const string p2Help = "P2   ARROWS";
                const float helpSize = 16.0f;
                spriteBatch.DrawText(hudFont, helpSize, p1Help,
                    new Vector2(28, logicalH - 32),
                    new GraphicsColor(0.55f, 0.85f, 1.00f, 0.65f),
                    dpiScale: dpiScale);
                var p2m = SpriteBatchUiExtensions.MeasureText(hudFont, helpSize, p2Help, dpiScale);
                spriteBatch.DrawText(hudFont, helpSize, p2Help,
                    new Vector2(logicalW - 28 - p2m.X, logicalH - 32),
                    new GraphicsColor(1.00f, 0.65f, 0.92f, 0.65f),
                    dpiScale: dpiScale);

                spriteBatch.End(pass);
            });
    }

    private void DrawScore(string text, float centerX, float centerY, float pixelSize, float dpiScale, GraphicsColor color)
    {
        var m = SpriteBatchUiExtensions.MeasureText(hudFont!, pixelSize, text, dpiScale);
        spriteBatch.DrawText(hudFont!, pixelSize, text,
            new Vector2(centerX - m.X * 0.5f, centerY - m.Y * 0.5f),
            color, dpiScale: dpiScale);
    }

    private void PlaySfx(AudioSource? source, float pitch)
    {
        if (source is null || AudioDevice is not { } audio) return;
        source.Pitch = pitch;
        source.Gain = 0.6f;
        source.Sync(audio);
        source.Stop(audio);
        source.Play(audio);
    }

    // ------------------------------------------------------------------- Cube

    // Procedural unit cube (size 1x1x1, centered at origin). One quad per face
    // with its own normal so cube faces shade flat instead of being smoothed
    // by per-vertex normal interpolation across an edge. 24 verts, 36 indices.
    private static (VertexPosition3NormalTexture[] Vertices, ushort[] Indices) BuildCubeMesh()
    {
        var verts = new List<VertexPosition3NormalTexture>(24);
        var inds = new List<ushort>(36);

        void Face(Vector3 origin, Vector3 right, Vector3 up)
        {
            var normal = Vector3.Normalize(Vector3.Cross(right, up));
            ushort baseIndex = (ushort)verts.Count;
            verts.Add(MakeVert(origin,                    normal, new Vector2(0, 0)));
            verts.Add(MakeVert(origin + right,            normal, new Vector2(1, 0)));
            verts.Add(MakeVert(origin + right + up,       normal, new Vector2(1, 1)));
            verts.Add(MakeVert(origin + up,               normal, new Vector2(0, 1)));
            inds.Add(baseIndex);     inds.Add((ushort)(baseIndex + 1)); inds.Add((ushort)(baseIndex + 2));
            inds.Add(baseIndex);     inds.Add((ushort)(baseIndex + 2)); inds.Add((ushort)(baseIndex + 3));
        }

        // Six faces, each four verts spanning the face, CCW from outside so
        // backface culling keeps the cube interior hidden. Origin is the
        // bottom-left corner of the face (as seen from outside), right and up
        // are unit vectors along the face's local axes.
        var h = 0.5f;
        Face(new Vector3( h, -h,  h), -Vector3.UnitZ,  Vector3.UnitY);   // +X
        Face(new Vector3(-h, -h, -h),  Vector3.UnitZ,  Vector3.UnitY);   // -X
        Face(new Vector3(-h,  h,  h),  Vector3.UnitX, -Vector3.UnitZ);   // +Y
        Face(new Vector3(-h, -h, -h),  Vector3.UnitX,  Vector3.UnitZ);   // -Y
        Face(new Vector3(-h, -h,  h),  Vector3.UnitX,  Vector3.UnitY);   // +Z
        Face(new Vector3( h, -h, -h), -Vector3.UnitX,  Vector3.UnitY);   // -Z

        return (verts.ToArray(), inds.ToArray());
    }

    private static VertexPosition3NormalTexture MakeVert(Vector3 p, Vector3 n, Vector2 uv) =>
        new(new GraphicsVector3(p.X, p.Y, p.Z),
            new GraphicsVector3(n.X, n.Y, n.Z),
            new GraphicsVector2(uv.X, uv.Y));

    // ------------------------------------------------------------------- Audio

    private static AudioClipData SynthBeep(float frequency, float seconds, float amplitude)
    {
        const int sampleRate = 44100;
        var sampleCount = (int)(sampleRate * seconds);
        var pcm = new byte[sampleCount * 2];
        var period = sampleRate / frequency;
        for (var i = 0; i < sampleCount; i++)
        {
            var envelope = 1.0f - (float)i / sampleCount;
            var phase = (i % period) / period;
            var square = phase < 0.5f ? 1.0f : -1.0f;
            var sample = (short)(square * envelope * amplitude * short.MaxValue);
            pcm[i * 2 + 0] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return new AudioClipData(SampleRate: sampleRate, Channels: 1, BitsPerSample: 16, PcmData: pcm);
    }
}
