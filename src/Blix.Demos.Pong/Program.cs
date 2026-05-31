using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Audio;
using Blix.Core;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;
using Blix.Runtime.Silk;

using var window = new Window(
    new PongGame(),
    new WindowOptions("Blix · Pong", 1152, 576));
window.Run();

internal sealed class PongGame : Game, IInputHandler
{
    // Virtual playfield is the coordinate space gameplay logic uses; sprites
    // render straight to the swapchain through a Vulkan-NDC ortho that maps the
    // playfield over the whole window. Window is 1152x576 (2:1) and the playfield
    // is 960x480 (2:1), so the play rect fills the window without letterboxing.
    private const float PlayfieldWidth = 960.0f;
    private const float PlayfieldHeight = 480.0f;
    // Offscreen render target: 2x the playfield for free AA, then the CRT
    // post-FX downsamples it to the swapchain. Fixed so uTexel is constant.
    private const int OffscreenWidth = 1920;
    private const int OffscreenHeight = 960;
    // Tight bezel — the play rect fills 95% of each axis; the post-FX frames
    // the rest with a vignette gradient. In screen UVs (left, bottom, right, top).
    private const float PlayRectLeft = 0.025f;
    private const float PlayRectRight = 0.975f;
    private const float PlayRectBottom = 0.025f;
    private const float PlayRectTop = 0.975f;
    // Font crispness knob (DrawText picks a baked size near pixelSize*dpiScale).
    private const float FontDpiScale = OffscreenHeight / PlayfieldHeight;

    private const float PaddleWidth = 12.0f;
    private const float PaddleHeight = 88.0f;
    private const float PaddleInset = 28.0f;
    // Max speed the paddle accelerates toward when a direction is held. Real
    // velocity lerps via PaddleAccelTau so input feels weighted, not teleporty.
    private const float PaddleMaxSpeed = 680.0f;
    private const float PaddleAccelTau = 0.07f;

    private const float BallSize = 22.0f;
    private const float BallSpeedStart = 480.0f;
    private const float BallSpeedMax = 860.0f;
    private const float BallSpeedupPerHit = 44.0f;

    // Hitstop durations. Paddle hits get a brief freeze; goals get longer so the
    // shake + score change land before the next serve resets the field.
    private const float HitstopPaddle = 0.05f;
    private const float HitstopGoal = 0.10f;

    // Trail samples. One per fixed step → 1/120s spacing. 20 samples = ~166ms
    // of history; ramps below keep dots thin + low-alpha.
    private const int TrailLength = 20;

    // First-to-N. 11 is the long-running Pong/ping-pong convention.
    private const int WinScore = 11;
    private const float WinFlashDuration = 0.9f;

    protected override double FixedStep => 1.0 / 120.0;

    private readonly HashSet<Key> heldKeys = new();
    private readonly Random rng = new();

    private float leftPaddleY = PlayfieldHeight * 0.5f;
    private float rightPaddleY = PlayfieldHeight * 0.5f;
    private float leftPaddleVel;
    private float rightPaddleVel;
    private float leftPaddleRecoil;
    private float rightPaddleRecoil;

    private Vector2 ballPosition = new(PlayfieldWidth * 0.5f, PlayfieldHeight * 0.5f);
    private Vector2 ballVelocity = Vector2.Zero;
    private int leftScore;
    private int rightScore;
    private int nextServeDir;
    private bool waitingForServe = true;
    private double totalTime;

    // 0 = match in progress, -1 = left player won, +1 = right player won.
    private int winner;
    private float winFlashTimer;

    private float hitstopTimer;
    private float squashTimer;
    private float squashDuration;
    private int squashAxis; // 0 = X-compress (paddle hit), 1 = Y-compress (wall/goal)
    private float shakeAmp;
    private Vector2 shakeOffset;

    private readonly Vector2[] trail = new Vector2[TrailLength];
    private int trailCount;
    private int trailHead;

    private SpriteBatch spriteBatch = null!;
    private TextureHandle whitePixel;
    private Font? hudFont;

    // Render graph: sprites -> offscreen (graph pass), then a CRT post-FX
    // present (imperative) samples that offscreen into the swapchain.
    private RenderGraph graph = null!;
    private GraphResourceHandle sceneColor;
    private PassHandle spritePass;
    private ShaderProgramHandle postfxShader;
    private PipelineHandle postfxPipeline;
    private VertexBufferHandle fullscreenVB;
    private IndexBufferHandle fullscreenIB;
    private readonly byte[] postfxPush = new byte[64];

    private AudioClipHandle paddleClip = AudioClipHandle.Invalid;
    private AudioClipHandle wallClip = AudioClipHandle.Invalid;
    private AudioClipHandle scoreClip = AudioClipHandle.Invalid;
    private AudioSource? paddleSource;
    private AudioSource? wallSource;
    private AudioSource? scoreSource;

    protected override void OnLoad()
    {
        Host.SetTitle("Blix · Pong");

        var vk = (VulkanGraphicsDevice)GraphicsDevice;

        // Graph: one offscreen color target + a sprite pass that draws into it.
        // The post-FX present (imperative, below) samples it into the swapchain.
        graph = new RenderGraph(vk);
        sceneColor = graph.ColorTarget("pong.scene", TextureFormat.Rgba8,
            new FixedGraphSize(OffscreenWidth, OffscreenHeight));
        spritePass = graph.GraphicsPass("pong.sprites")
            .Target(sceneColor, LoadOp.Clear, StoreOp.Store)
            .Shader(SpriteBatch.Interface)
            .Handle;
        graph.Compile();

        // SpriteBatch's pipeline bakes against the sprite pass's offscreen
        // surface (Rgba8) for render-pass compatibility.
        spriteBatch = new SpriteBatch(vk, renderTarget: graph.GetPassSurface(spritePass));
        whitePixel = GraphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.PixelatedRepeat),
            new byte[] { 255, 255, 255, 255 },
            name: "pong.white");

        // CRT post-FX present pipeline: fullscreen triangle (gl_VertexIndex),
        // offscreen sampled at set 0 / slot 0, params via a fragment push
        // constant. Targets the swapchain (RenderTarget: null).
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var postfxInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Fragment, 0, 64) });
        postfxShader = vk.CreateShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "postfx.vert.spv")),
            File.ReadAllBytes(Path.Combine(shaderDir, "postfx.frag.spv")),
            postfxInterface, "pong.postfx");
        postfxPipeline = vk.CreatePipeline(new PipelineDescription(
            postfxShader,
            VertexPosition3Texture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.Disabled,
            RasterizerState.NoCulling,
            BlendState.Disabled), "pong.postfx");

        // Dummy 3-vertex buffers — the fullscreen-triangle vertex shader
        // generates positions from gl_VertexIndex and ignores these.
        var dummyVerts = new VertexPosition3Texture[]
        {
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector2(0, 0)),
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector2(0, 0)),
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector2(0, 0)),
        };
        fullscreenVB = vk.CreateVertexBuffer(
            VertexPosition3Texture.CreateBufferData(dummyVerts), "pong.fs.vb");
        fullscreenIB = vk.CreateIndexBuffer(new ushort[] { 0, 1, 2 }, name: "pong.fs.ib");

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
            Console.WriteLine($"Font unavailable, score will not render: {ex.Message}");
        }

        // AudioDevice is null on the Silk runtime (not an IAudioHost yet), so the
        // beeps are silent for now — the calls below no-op gracefully.
        if (AudioDevice is { } audio)
        {
            paddleClip = audio.CreateClip(SynthBeep(660.0f, 0.06f, 0.6f), name: "pong.paddle");
            wallClip   = audio.CreateClip(SynthBeep(330.0f, 0.05f, 0.5f), name: "pong.wall");
            scoreClip  = audio.CreateClip(SynthBeep(180.0f, 0.30f, 0.7f), name: "pong.score");
            paddleSource = AudioSource.Create(audio, paddleClip, name: "pong.paddle");
            wallSource   = AudioSource.Create(audio, wallClip,   name: "pong.wall");
            scoreSource  = AudioSource.Create(audio, scoreClip,  name: "pong.score");
        }

        ResetBall(serveDir: 0);
    }

    public override void OnUnload()
    {
        spriteBatch?.Dispose();
        graph?.Dispose();
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
        else if (key == Key.R)
        {
            ResetMatch();
        }
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

        // Decay all feel-timers at variable rate so a slow frame doesn't make
        // hitstop "stick." Anything that runs visibly should live in OnUpdate.
        hitstopTimer = MathF.Max(0.0f, hitstopTimer - dt);
        squashTimer  = MathF.Max(0.0f, squashTimer - dt);
        shakeAmp    *= MathF.Exp(-dt / 0.12f);
        leftPaddleRecoil  *= MathF.Exp(-dt / 0.07f);
        rightPaddleRecoil *= MathF.Exp(-dt / 0.07f);
        winFlashTimer = MathF.Max(0.0f, winFlashTimer - dt);

        // Resample a random unit vector each frame so the shake doesn't read as
        // a sustained pan — it should jitter.
        var angle = (float)(rng.NextDouble() * Math.PI * 2.0);
        shakeOffset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * shakeAmp;
    }

    public override void OnFixedUpdate(Time time)
    {
        var step = (float)time.Delta;

        // During hitstop, freeze paddles + ball entirely. Timers still decay
        // in OnUpdate so the freeze elapses naturally and play resumes.
        if (hitstopTimer > 0.0f) return;

        // Paddle physics: target velocity from input, real velocity converges
        // via exponential lerp. accelFactor = 1 - e^(-dt/tau).
        var accelFactor = 1.0f - MathF.Exp(-step / PaddleAccelTau);

        var leftTarget = 0.0f;
        if (heldKeys.Contains(Key.W)) leftTarget -= PaddleMaxSpeed;
        if (heldKeys.Contains(Key.S)) leftTarget += PaddleMaxSpeed;
        leftPaddleVel += (leftTarget - leftPaddleVel) * accelFactor;
        leftPaddleY += leftPaddleVel * step;

        var rightTarget = 0.0f;
        if (heldKeys.Contains(Key.Up))   rightTarget -= PaddleMaxSpeed;
        if (heldKeys.Contains(Key.Down)) rightTarget += PaddleMaxSpeed;
        rightPaddleVel += (rightTarget - rightPaddleVel) * accelFactor;
        rightPaddleY += rightPaddleVel * step;

        var halfH = PaddleHeight * 0.5f;
        if (leftPaddleY < halfH) { leftPaddleY = halfH; leftPaddleVel = 0.0f; }
        else if (leftPaddleY > PlayfieldHeight - halfH) { leftPaddleY = PlayfieldHeight - halfH; leftPaddleVel = 0.0f; }
        if (rightPaddleY < halfH) { rightPaddleY = halfH; rightPaddleVel = 0.0f; }
        else if (rightPaddleY > PlayfieldHeight - halfH) { rightPaddleY = PlayfieldHeight - halfH; rightPaddleVel = 0.0f; }

        if (waitingForServe) return;

        ballPosition += ballVelocity * step;

        // Trail sample once per physics step. Ring buffer makes the render walk
        // straight from oldest to newest without per-frame reallocation.
        trail[trailHead] = ballPosition;
        trailHead = (trailHead + 1) % TrailLength;
        if (trailCount < TrailLength) trailCount++;

        var half = BallSize * 0.5f;
        if (ballPosition.Y - half < 0.0f && ballVelocity.Y < 0.0f)
        {
            ballPosition.Y = half;
            ballVelocity.Y = -ballVelocity.Y;
            OnWallHit();
        }
        else if (ballPosition.Y + half > PlayfieldHeight && ballVelocity.Y > 0.0f)
        {
            ballPosition.Y = PlayfieldHeight - half;
            ballVelocity.Y = -ballVelocity.Y;
            OnWallHit();
        }

        CheckPaddle(PaddleInset + PaddleWidth * 0.5f, leftPaddleY, requireVxSign: -1, isLeft: true);
        CheckPaddle(PlayfieldWidth - PaddleInset - PaddleWidth * 0.5f, rightPaddleY, requireVxSign: +1, isLeft: false);

        if (ballPosition.X + half < 0.0f)
        {
            rightScore++;
            OnGoal();
            ResetBall(serveDir: -1);
        }
        else if (ballPosition.X - half > PlayfieldWidth)
        {
            leftScore++;
            OnGoal();
            ResetBall(serveDir: +1);
        }
    }

    private void CheckPaddle(float paddleCenterX, float paddleCenterY, int requireVxSign, bool isLeft)
    {
        var pHalfW = PaddleWidth * 0.5f;
        var pHalfH = PaddleHeight * 0.5f;
        var bHalf = BallSize * 0.5f;

        var dx = ballPosition.X - paddleCenterX;
        var dy = ballPosition.Y - paddleCenterY;
        if (Math.Abs(dx) >= pHalfW + bHalf) return;
        if (Math.Abs(dy) >= pHalfH + bHalf) return;
        if (Math.Sign(ballVelocity.X) != requireVxSign) return;

        ballPosition.X = requireVxSign < 0
            ? paddleCenterX + pHalfW + bHalf
            : paddleCenterX - pHalfW - bHalf;

        ballVelocity.X = -ballVelocity.X;
        // Five-zone quantised paddle: zones make the deflection a skill —
        // players can aim for {-1, -0.5, 0, +0.5, +1}. Center zone widest.
        var dyN = Math.Clamp(dy / pHalfH, -1.0f, 1.0f);
        var zone = Math.Clamp((int)MathF.Floor((dyN + 1.0f) * 2.5f), 0, 4);
        var offsetT = (zone - 2) * 0.5f;
        var speed = ballVelocity.Length();
        ballVelocity.Y += offsetT * speed * 1.0f;
        var newSpeed = Math.Min(speed + BallSpeedupPerHit, BallSpeedMax);
        ballVelocity = Vector2.Normalize(ballVelocity) * newSpeed;

        // Feel triggers, all proportional to current ball speed.
        var speedT = newSpeed / BallSpeedMax;
        hitstopTimer = HitstopPaddle;
        squashTimer = HitstopPaddle * 1.2f;
        squashDuration = squashTimer;
        squashAxis = 0;
        shakeAmp = MathF.Max(shakeAmp, 0.006f + 0.010f * speedT);
        if (isLeft) leftPaddleRecoil = -8.0f;
        else        rightPaddleRecoil = 8.0f;

        PlaySfx(paddleSource, pitch: 0.85f + 0.4f * Math.Abs(offsetT));
    }

    private void OnWallHit()
    {
        var speedT = ballVelocity.Length() / BallSpeedMax;
        squashTimer = 0.06f;
        squashDuration = 0.06f;
        squashAxis = 1;
        shakeAmp = MathF.Max(shakeAmp, 0.003f + 0.004f * speedT);
        PlaySfx(wallSource, pitch: 1.0f);
    }

    private void OnGoal()
    {
        hitstopTimer = HitstopGoal;
        shakeAmp = MathF.Max(shakeAmp, 0.022f);
        PlaySfx(scoreSource, pitch: 1.0f);

        if (leftScore >= WinScore) winner = -1;
        else if (rightScore >= WinScore) winner = +1;
        if (winner != 0)
        {
            winFlashTimer = WinFlashDuration;
            shakeAmp = MathF.Max(shakeAmp, 0.04f);
            hitstopTimer = MathF.Max(hitstopTimer, 0.30f);
        }
    }

    private void ResetMatch()
    {
        leftScore = 0;
        rightScore = 0;
        winner = 0;
        winFlashTimer = 0.0f;
        ResetBall(serveDir: 0);
    }

    private void ResetBall(int serveDir)
    {
        ballPosition = new Vector2(PlayfieldWidth * 0.5f, PlayfieldHeight * 0.5f);
        ballVelocity = Vector2.Zero;
        nextServeDir = serveDir;
        waitingForServe = true;
        trailCount = 0;
        trailHead = 0;
    }

    private void LaunchBall()
    {
        var dirX = nextServeDir == 0
            ? (rng.NextDouble() < 0.5 ? -1.0f : 1.0f)
            : (float)nextServeDir;
        // Always angled — magnitude in [15°, ~38°] off horizontal, Y-sign random.
        var minAngle = (float)(Math.PI / 12.0);    // 15°
        var maxAngle = (float)(Math.PI / 4.74);    // ~38°
        var angleMag = minAngle + (float)rng.NextDouble() * (maxAngle - minAngle);
        var angleSign = rng.NextDouble() < 0.5 ? -1.0f : 1.0f;
        var angle = angleMag * angleSign;
        var dir = new Vector2(dirX * MathF.Cos(angle), MathF.Sin(angle));
        ballVelocity = Vector2.Normalize(dir) * BallSpeedStart;
        waitingForServe = false;
    }

    public override void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return;

        // Vulkan-NDC screen ortho: maps the virtual playfield over the offscreen
        // target, top-left origin, Y down.
        var gameOrtho = GraphicsMatrices.CreateOrthographicOffCenterVulkan(
            left: 0.0f, right: PlayfieldWidth,
            bottom: PlayfieldHeight, top: 0.0f,
            nearPlane: -1.0f, farPlane: 1.0f);

        // Sprites -> offscreen (graph pass). The graph leaves the stored color
        // target sampleable for the post-FX present below.
        graph.Pass(spritePass, scope =>
        {
            spriteBatch.Begin(gameOrtho);
            DrawScores();
            DrawCentreNet();
            DrawPaddle(PaddleInset, leftPaddleY, leftPaddleRecoil);
            DrawPaddle(PlayfieldWidth - PaddleInset - PaddleWidth, rightPaddleY, rightPaddleRecoil);
            DrawTrail();
            DrawBall();
            DrawHud();
            spriteBatch.End(scope);
        }, clearColor: new GraphicsColor(0.03f, 0.02f, 0.06f, 1.0f));

        graph.Execute(commandList);

        // CRT post-FX present (imperative): sample the offscreen into the
        // swapchain. Params go in a fragment push constant (see postfx.frag's
        // Push block layout: uPlayRect@0, uTexel@16, uShake@24, uWinFlash@32,
        // uTime@48).
        var sceneTex = graph.GetColorTexture(sceneColor);
        var flashT = winFlashTimer / WinFlashDuration;
        // Win flash: rgb = winner's side colour (cyan for P1, magenta for P2).
        var flashRgb = winner < 0
            ? new Vector3(0.30f, 0.85f, 1.0f)
            : new Vector3(1.0f, 0.45f, 0.85f);
        var playRect = new Vector4(PlayRectLeft, PlayRectBottom, PlayRectRight, PlayRectTop);
        var texel = new Vector2(1.0f / OffscreenWidth, 1.0f / OffscreenHeight);
        var winFlash = new Vector4(flashRgb.X, flashRgb.Y, flashRgb.Z, flashT);
        var elapsed = (float)totalTime;
        var push = postfxPush.AsSpan();
        MemoryMarshal.Write(push.Slice(0, 16), in playRect);
        MemoryMarshal.Write(push.Slice(16, 8), in texel);
        MemoryMarshal.Write(push.Slice(24, 8), in shakeOffset);
        MemoryMarshal.Write(push.Slice(32, 16), in winFlash);
        MemoryMarshal.Write(push.Slice(48, 4), in elapsed);

        commandList.Pass(
            "pong.postfx",
            new RenderPassDescription(
                RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new(0, 0, 0, 1) },
                ClearDepth: true),
            pass =>
            {
                pass.DrawIndexed(
                    fullscreenVB,
                    fullscreenIB,
                    postfxPipeline,
                    indexCount: 3,
                    Array.Empty<ShaderUniform>(),
                    new ShaderTextureBinding[] { new("uScene", sceneTex, Slot: 0) },
                    postfxPush);
            });
    }

    private void DrawCentreNet()
    {
        const float dashW = 6.0f;
        const float dashH = 22.0f;
        const float dashStep = 36.0f;
        var x = PlayfieldWidth * 0.5f - dashW * 0.5f;
        var dim = new GraphicsColor(0.34f, 0.24f, 0.55f, 1.0f);
        for (var y = 8.0f; y < PlayfieldHeight; y += dashStep)
        {
            spriteBatch.DrawSolidRect(whitePixel, new Rect(x, y, dashW, dashH), dim);
        }
    }

    private void DrawPaddle(float x, float centerY, float recoilOffset)
    {
        var y = centerY - PaddleHeight * 0.5f;
        spriteBatch.DrawSolidRect(
            whitePixel,
            new Rect(x + recoilOffset, y, PaddleWidth, PaddleHeight),
            new GraphicsColor(0.85f, 0.98f, 1.0f, 1.0f));
    }

    // Speed → temperature gradient: cool cyan-blue at launch, white at mid-speed,
    // hot magenta/red at the cap.
    private GraphicsColor BallColor(float alpha = 1.0f)
    {
        var speed = ballVelocity.Length();
        var t = MathF.Min(speed / BallSpeedMax, 1.0f);
        float r, g, b;
        if (t < 0.5f)
        {
            var u = t * 2.0f;
            r = Lerp(0.45f, 1.00f, u);
            g = Lerp(0.85f, 0.97f, u);
            b = Lerp(1.00f, 0.95f, u);
        }
        else
        {
            var u = (t - 0.5f) * 2.0f;
            r = Lerp(1.00f, 1.00f, u);
            g = Lerp(0.97f, 0.45f, u);
            b = Lerp(0.95f, 0.55f, u);
        }
        return new GraphicsColor(r, g, b, alpha);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private void DrawTrail()
    {
        if (trailCount == 0 || waitingForServe) return;
        // Walk oldest → newest. Linear ramp on alpha + size so the head melts
        // into the ball.
        for (var i = 0; i < trailCount; i++)
        {
            var idx = ((trailHead - trailCount + i) % TrailLength + TrailLength) % TrailLength;
            var p = trail[idx];
            var t = (i + 1) / (float)trailCount;
            var size = BallSize * (0.08f + 0.32f * t);
            var alpha = 0.02f + 0.12f * t;
            var h = size * 0.5f;
            spriteBatch.DrawSolidRect(
                whitePixel,
                new Rect(p.X - h, p.Y - h, size, size),
                BallColor(alpha));
        }
    }

    private void DrawBall()
    {
        if (waitingForServe)
        {
            var half = BallSize * 0.5f;
            spriteBatch.DrawSolidRect(
                whitePixel,
                new Rect(ballPosition.X - half, ballPosition.Y - half, BallSize, BallSize),
                BallColor());
            return;
        }

        // Squash/stretch: velocity-axis stretch + impact-axis compress.
        var speed = ballVelocity.Length();
        var speedT = MathF.Min(speed / BallSpeedMax, 1.0f);
        float scaleX = 1.0f, scaleY = 1.0f;
        if (speed > 1.0f)
        {
            var vxRel = MathF.Abs(ballVelocity.X) / speed;
            var vyRel = MathF.Abs(ballVelocity.Y) / speed;
            if (vxRel > vyRel)
            {
                scaleX = 1.0f + 0.30f * speedT;
                scaleY = 1.0f - 0.15f * speedT;
            }
            else
            {
                scaleY = 1.0f + 0.30f * speedT;
                scaleX = 1.0f - 0.15f * speedT;
            }
        }

        if (squashTimer > 0.0f && squashDuration > 0.0f)
        {
            var t = squashTimer / squashDuration;   // 1 at impact, 0 at end
            var amount = 0.45f * t;
            if (squashAxis == 0)   // X-compress (paddle)
            {
                scaleX = 1.0f - amount;
                scaleY = 1.0f + amount;
            }
            else                   // Y-compress (wall/goal)
            {
                scaleY = 1.0f - amount;
                scaleX = 1.0f + amount;
            }
        }

        var w = BallSize * scaleX;
        var h = BallSize * scaleY;
        spriteBatch.DrawSolidRect(
            whitePixel,
            new Rect(ballPosition.X - w * 0.5f, ballPosition.Y - h * 0.5f, w, h),
            BallColor());
    }

    private void DrawScores()
    {
        if (hudFont is null) return;
        const float scoreSize = 200.0f;
        var color = new GraphicsColor(0.55f, 0.70f, 0.95f, 0.28f);

        DrawScoreCentered(leftScore.ToString("00"), PlayfieldWidth * 0.25f, scoreSize, color);
        DrawScoreCentered(rightScore.ToString("00"), PlayfieldWidth * 0.75f, scoreSize, color);
    }

    private void DrawScoreCentered(string text, float centerX, float pixelSize, GraphicsColor color)
    {
        var measured = SpriteBatchUiExtensions.MeasureText(hudFont!, pixelSize, text, FontDpiScale);
        var pos = new Vector2(centerX - measured.X * 0.5f, PlayfieldHeight * 0.5f - measured.Y * 0.5f);
        spriteBatch.DrawText(hudFont!, pixelSize, text, pos, color, dpiScale: FontDpiScale);
    }

    private void DrawHud()
    {
        if (hudFont is null) return;

        if (winner != 0)
        {
            var label = winner < 0 ? "P1 WINS" : "P2 WINS";
            var winColor = winner < 0
                ? new GraphicsColor(0.55f, 0.95f, 1.00f, 0.95f)
                : new GraphicsColor(1.00f, 0.65f, 0.92f, 0.95f);
            const float winSize = 96.0f;
            var winMeasured = SpriteBatchUiExtensions.MeasureText(hudFont, winSize, label, FontDpiScale);
            spriteBatch.DrawText(hudFont, winSize, label,
                new Vector2(PlayfieldWidth * 0.5f - winMeasured.X * 0.5f,
                            PlayfieldHeight * 0.5f - winMeasured.Y * 0.5f),
                winColor, dpiScale: FontDpiScale);

            const string sub = "PRESS SPACE";
            const float subSize = 26.0f;
            var subMeasured = SpriteBatchUiExtensions.MeasureText(hudFont, subSize, sub, FontDpiScale);
            spriteBatch.DrawText(hudFont, subSize, sub,
                new Vector2(PlayfieldWidth * 0.5f - subMeasured.X * 0.5f,
                            PlayfieldHeight * 0.5f + winMeasured.Y * 0.5f + 16.0f),
                new GraphicsColor(0.95f, 0.55f, 0.85f, 0.85f),
                dpiScale: FontDpiScale);
        }
        else if (waitingForServe)
        {
            const string prompt = "PRESS SPACE";
            const float promptSize = 38.0f;
            var measured = SpriteBatchUiExtensions.MeasureText(hudFont, promptSize, prompt, FontDpiScale);
            spriteBatch.DrawText(hudFont, promptSize, prompt,
                new Vector2(PlayfieldWidth * 0.5f - measured.X * 0.5f, PlayfieldHeight - 100.0f),
                new GraphicsColor(0.95f, 0.55f, 0.85f, 0.95f),
                dpiScale: FontDpiScale);
        }

        const string controls = "W / S      UP / DN      R      ESC";
        const float controlsSize = 18.0f;
        var cMeasured = SpriteBatchUiExtensions.MeasureText(hudFont, controlsSize, controls, FontDpiScale);
        spriteBatch.DrawText(hudFont, controlsSize, controls,
            new Vector2(PlayfieldWidth * 0.5f - cMeasured.X * 0.5f, PlayfieldHeight - 36.0f),
            new GraphicsColor(0.55f, 0.55f, 0.78f, 0.75f),
            dpiScale: FontDpiScale);
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
