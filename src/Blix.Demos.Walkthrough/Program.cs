using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Primitives;
using Blix.Render;
using Blix.Runtime.OpenTK;

using var window = new Window(
    new WalkthroughGame(),
    new WindowOptions("Blix . Sponza Walkthrough", 1440, 810));
window.Run();

internal sealed class WalkthroughGame : Game, IInputHandler, IDebuggable
{
    private const float PitchClamp = MathF.PI * 0.49f;   // just under 90 degrees
    private const int EnvCubeFaceSize = 256;
    private const int ShadowMapSize = 2048;
    private static readonly Vector3 SceneCenter = new(0.0f, 5.5f, 0.0f);

    // --- Tunable fields (backing debug sliders) ----------------------------
    // FP camera + controls
    private float walkSpeed = 3.5f;
    private float sprintMultiplier = 3.0f;
    private float mouseLookSensitivity = 0.0025f;
    private float cameraFov = MathF.PI / 3.0f;
    private float cameraNearPlane = 0.1f;
    private float cameraFarPlane = 100.0f;

    // Sun & exposure. The bake-time sun direction is held separately from the
    // live `sunDirection` so the user can scrub the sliders without forcing
    // an IBL re-bake every tick — the rebake button commits.
    private float sunYaw = MathF.Atan2(-0.30f, -0.45f);   // matches initial dir
    private float sunPitch = MathF.Asin(-0.85f);
    private float sunStrength = 3.2f;
    private float exposure = 1.0f;
    private Vector3 sunDirection = Vector3.Normalize(new Vector3(-0.45f, -0.85f, -0.30f));
    private Vector3 bakedSunDirection = Vector3.Normalize(new Vector3(-0.45f, -0.85f, -0.30f));

    // Shadow projection
    private float shadowOrthoExtent = 18.0f;
    private float shadowSunDistance = 22.0f;
    private float shadowNearPlane = 0.1f;
    private float shadowFarPlane = 50.0f;

    // IBL / shader tuning (uniforms)
    private float emissiveBoost = 2.5f;
    private float iblSpecAttenuation = 0.8f;
    private float iblDiffuseBoost = 1.0f;
    private float metalFloor = 0.18f;
    private float indirectShadowBase = 0.60f;
    private float indirectShadowRange = 0.40f;

    // Pending rebake — set by the Debug button, consumed in OnUpdate.
    private bool rebakeRequested;

    // Cached scene resources -------------------------------------------------
    private record SpongeSubmesh(Mesh Mesh, Material LitMaterial);
    private readonly List<SpongeSubmesh> sceneSubmeshes = new();

    private PipelineHandle litPipeline;
    private Material shadowMaterial = null!;
    private Material skyboxMaterial = null!;
    private Mesh skyMesh = null!;
    private TextureHandle whitePixel;
    private TextureHandle flatNormal;
    private TextureHandle neutralMetallicRoughness;
    private TextureHandle shadowMapTexture;
    private TextureHandle envCubemap;
    private float envCubeMipCount;

    private RenderSurface sceneSurface = null!;
    private RenderSurface shadowSurface = null!;
    private Camera3D camera = null!;
    private SpriteBatch spriteBatch = null!;
    private Font? hudFont;

    private Matrix4x4 lightViewProjection = Matrix4x4.Identity;

    // Input state ------------------------------------------------------------
    private readonly HashSet<Key> heldKeys = new();
    private float yaw = -MathF.PI * 0.5f;   // start looking down -X (Sponza's long axis)
    private float pitch;
    // Captured = mouselook on, debug overlay off (game mode).
    // Uncaptured = camera locked, ImGui sliders interactable (debug mode).
    // Toggled with Cmd/Ctrl+C, matching the ShaderLab demo's convention.
    private bool cursorCaptured = true;
    private float fpsSmoothed;
    private bool ShowDebug => !cursorCaptured;

    public string DebugName => "Walkthrough";

    protected override void OnLoad()
    {
        Host.SetTitle("Blix . Sponza Walkthrough");
        Host.SetCursorCaptured(true);

        // --- Load Sponza ------------------------------------------------
        Console.WriteLine("Loading Sponza...");
        var assets = new AssetDatabase()
            .RegisterImporter(new GltfStaticImporter())
            .RegisterImporter(new FontImporter())
            .LoadManifest(Path.Combine(AppContext.BaseDirectory, "Assets", "manifest.json"));

        var sponza = assets.Load<GltfModel>(AssetId.Parse("models/sponza"));
        try
        {
            var fontData = assets.Load<FontData>(AssetId.Parse("fonts/bowlby"));
            hudFont = Font.Upload(GraphicsDevice, fontData);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Font unavailable: {ex.Message}");
        }
        Console.WriteLine($"Sponza loaded: {sponza.Primitives.Length} primitives.");

        // --- Shadow surface (depth-only, sampler2DShadow-ready) -------
        // Compare=true enables the hardware sampler2DShadow PCF path: each
        // texture() call returns a 0..1 occlusion via bilinear-interpolated
        // depth-vs-reference comparisons. Linear filter is required for the
        // bilinear part to do real work.
        var shadowSampler = new SamplerDescription(
            MinFilter: TextureFilter.Linear,
            MagFilter: TextureFilter.Linear,
            WrapU: TextureWrap.ClampToEdge,
            WrapV: TextureWrap.ClampToEdge,
            GenerateMipmaps: false,
            Compare: true);
        shadowSurface = GraphicsDevice.CreateRenderSurface(new RenderSurfaceDescription(
            Name: "walk.shadow",
            Size: new FixedRenderSurfaceSize(ShadowMapSize, ShadowMapSize),
            ColorAttachments: Array.Empty<ColorAttachmentDescription>(),
            Depth: new DepthTexture(shadowSampler)));
        shadowMapTexture = shadowSurface.DepthTexture
            ?? throw new InvalidOperationException("Shadow surface has no depth texture.");

        // --- Pipeline + fallback textures -------------------------------
        whitePixel = GraphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            new byte[] { 255, 255, 255, 255 },
            name: "walk.white");
        // Tangent-space "no perturbation": (128, 128, 255) decodes to (0, 0, 1).
        flatNormal = GraphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            new byte[] { 128, 128, 255, 255 },
            name: "walk.flat_normal");
        // glTF MR convention: G = roughness, B = metallic. Default to full
        // rough, no metal so factors alone drive the BRDF when no MR texture.
        neutralMetallicRoughness = GraphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            new byte[] { 0, 255, 0, 255 },
            name: "walk.neutral_mr");

        // --- HDR procedural cubemap (baked once at startup) -------------
        // CPU-bake the sky into a Half-float cubemap. Used by both the
        // skybox shader (visible backdrop) and the lit shader (IBL source).
        // GenerateMipmaps on the sampler means the GL driver auto-builds the
        // mip chain after upload — those mips serve as our cheap
        // roughness-prefilter approximation for specular IBL.
        Console.WriteLine($"Baking {EnvCubeFaceSize}x{EnvCubeFaceSize} HDR sky cubemap...");
        var cubePixels = CubemapBaker.BakeSky(EnvCubeFaceSize, bakedSunDirection);
        envCubemap = GraphicsDevice.CreateTextureCubeHdr(
            EnvCubeFaceSize, cubePixels,
            SamplerDescription.LinearClampMipmap,
            name: "walk.env_cube");
        envCubeMipCount = MathF.Floor(MathF.Log2(EnvCubeFaceSize)) + 1.0f;

        var litShader = GraphicsDevice.CreateShaderProgram(new ShaderSources(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "lit.vert")),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "lit.frag")),
            VertexName: "walk.lit.vert",
            FragmentName: "walk.lit.frag"));
        litPipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                litShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                BlendState.Disabled),
            name: "walk.lit");

        // Depth-only pipeline for the shadow pass. Uses the same vertex layout
        // (Sponza primitives are bound with this layout) but the shadow.vert
        // only reads aPosition; normal/uv slots are silently ignored.
        var shadowShader = GraphicsDevice.CreateShaderProgram(new ShaderSources(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "shadow.vert")),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "shadow.frag")),
            VertexName: "walk.shadow.vert",
            FragmentName: "walk.shadow.frag"));
        var shadowPipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                shadowShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                BlendState.Disabled),
            name: "walk.shadow");
        shadowMaterial = new Material("walk.shadow", shadowPipeline);

        // --- Skybox pipeline + mesh -------------------------------------
        // Rendered AFTER geometry inside the scene pass with LessEqual depth +
        // no depth write. The vertex shader puts the quad at NDC z = 1, so
        // only pixels that geometry didn't already cover get the sky shader.
        var skyShader = GraphicsDevice.CreateShaderProgram(new ShaderSources(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "skybox.vert")),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "skybox.frag")),
            VertexName: "walk.sky.vert",
            FragmentName: "walk.sky.frag"));
        var skyPipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                skyShader,
                VertexPositionTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualNoWrite,
                RasterizerState.NoCulling,
                BlendState.Disabled),
            name: "walk.sky");
        skyboxMaterial = new Material("walk.sky", skyPipeline);
        skyboxMaterial.SetTexture("uEnvMap", envCubemap, 0);
        var skyVerts = GraphicsDevice.CreateVertexBuffer(
            VertexPositionTexture.CreateBufferData(FullscreenQuad.Vertices),
            name: "walk.sky.verts");
        var skyIndices = GraphicsDevice.CreateIndexBuffer(FullscreenQuad.Indices, name: "walk.sky.indices");
        skyMesh = new Mesh("walk.sky", skyVerts, skyIndices,
            FullscreenQuad.Indices.Length, Bounds3.Empty);

        // --- Upload Sponza primitives + build materials -----------------
        // Texture cache keyed by reference identity so an albedo image
        // referenced by N primitives only uploads once.
        var textureCache = new Dictionary<GltfTexture, TextureHandle>(ReferenceEqualityComparer.Instance);

        TextureHandle UploadOrFallback(GltfTexture? tex, TextureHandle fallback, string nameHint)
        {
            if (tex is null) return fallback;
            if (textureCache.TryGetValue(tex, out var existing)) return existing;
            var handle = GraphicsDevice.CreateTexture2D(
                new TextureDescription(tex.Width, tex.Height, TextureFormat.Rgba8,
                    new SamplerDescription(
                        MinFilter: TextureFilter.Linear,
                        MagFilter: TextureFilter.Linear,
                        WrapU: TextureWrap.Repeat,
                        WrapV: TextureWrap.Repeat,
                        GenerateMipmaps: true,
                        Compare: false)),
                tex.RgbaPixels,
                name: $"sponza.{nameHint}.{tex.Name}");
            textureCache[tex] = handle;
            return handle;
        }

        foreach (var prim in sponza.Primitives)
        {
            var vb = GraphicsDevice.CreateVertexBuffer(
                new VertexBufferData(
                    new VertexBufferDescription(prim.Mesh.Layout, prim.Mesh.VertexCount, GraphicsBufferUsage.Static),
                    prim.Mesh.VertexBytes),
                name: $"sponza.vb.{prim.Mesh.Name}");
            var ib = GraphicsDevice.CreateIndexBuffer(prim.Mesh.Indices, name: $"sponza.ib.{prim.Mesh.Name}");
            var mesh = new Mesh(prim.Mesh.Name, vb, ib, prim.Mesh.Indices.Length, prim.Mesh.Bounds);

            var matName = prim.Material?.Name ?? "default";
            var material = new Material($"sponza.mat.{matName}", litPipeline);

            var baseColor = prim.Material?.BaseColorFactor ?? new Vector4(1, 1, 1, 1);
            var metallicFactor = prim.Material?.MetallicFactor ?? 1.0f;
            var roughnessFactor = prim.Material?.RoughnessFactor ?? 1.0f;
            // Raw glTF emissive factor — global boost lives in the shader so
            // it can be tuned live via the debug slider without rebinding.
            var emissiveFactor = prim.Material?.EmissiveFactor ?? Vector3.Zero;
            var albedoTex = UploadOrFallback(prim.Material?.BaseColorTexture, whitePixel, "albedo");
            var normalTex = UploadOrFallback(prim.Material?.NormalTexture, flatNormal, "normal");
            var mrTex = UploadOrFallback(prim.Material?.MetallicRoughnessTexture, neutralMetallicRoughness, "mr");
            // White fallback (not black) — glTF spec: when no emissive texture
            // is bound, all texture components default to 1.0, so the factor
            // alone drives emission. A black fallback would zero out factor-
            // only emissive materials (Sponza's hanging lamps).
            var emissiveTex = UploadOrFallback(prim.Material?.EmissiveTexture, whitePixel, "emissive");
            var hasNormal = prim.Material?.NormalTexture is null ? 0.0f : 1.0f;
            var hasMR = prim.Material?.MetallicRoughnessTexture is null ? 0.0f : 1.0f;

            material.SetUniform("uBaseColorFactor", new Vector4Uniform(baseColor));
            material.SetUniform("uMetallicFactor", new FloatUniform(metallicFactor));
            material.SetUniform("uRoughnessFactor", new FloatUniform(roughnessFactor));
            material.SetUniform("uEmissiveFactor", new Vector3Uniform(emissiveFactor));
            material.SetUniform("uNormalScale", new FloatUniform(hasNormal));
            material.SetUniform("uHasMetallicMap", new FloatUniform(hasMR));
            material.SetTexture("uAlbedo", albedoTex, 0);
            material.SetTexture("uNormalMap", normalTex, 1);
            material.SetTexture("uMetallicRoughness", mrTex, 2);
            material.SetTexture("uEmissive", emissiveTex, 3);
            material.SetTexture("uShadowMap", shadowMapTexture, 4);
            material.SetTexture("uEnvMap", envCubemap, 5);
            material.SetUniform("uEnvMapMipCount", new FloatUniform(envCubeMipCount));

            sceneSubmeshes.Add(new SpongeSubmesh(mesh, material));
        }
        Console.WriteLine($"Uploaded {sceneSubmeshes.Count} submeshes, {textureCache.Count} textures.");

        // --- Camera -----------------------------------------------------
        camera = new Camera3D
        {
            Transform = new Transform3D { Position = new Vector3(7.0f, 1.7f, 0.0f) },
            VerticalFieldOfView = cameraFov,
            NearPlane = cameraNearPlane,
            FarPlane = cameraFarPlane
        };

        // --- HUD --------------------------------------------------------
        spriteBatch = new SpriteBatch(GraphicsDevice);
        sceneSurface = new RenderSurface(RenderSurfaceHandle.Default, Array.Empty<TextureHandle>(), null);
    }

    void IInputHandler.OnKeyDown(Key key)
    {
        heldKeys.Add(key);
        if (key == Key.Escape) Host.RequestClose();
        else if (key == Key.C &&
                 (heldKeys.Contains(Key.LeftControl) ||
                  heldKeys.Contains(Key.RightControl) ||
                  heldKeys.Contains(Key.LeftSuper) ||
                  heldKeys.Contains(Key.RightSuper)))
        {
            // Cmd/Ctrl+C: single dev-mode toggle. Cursor capture controls
            // both mouselook (off when uncaptured) and ImGui interaction
            // (mouse forwarded to ImGui when uncaptured). ShowDebug is
            // derived from !cursorCaptured so the overlay follows.
            cursorCaptured = !cursorCaptured;
            Host.SetCursorCaptured(cursorCaptured);
        }
    }

    void IInputHandler.OnKeyUp(Key key) => heldKeys.Remove(key);

    void IInputHandler.OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        if (!cursorCaptured) return;
        yaw -= deltaX * mouseLookSensitivity;
        pitch -= deltaY * mouseLookSensitivity;
        pitch = Math.Clamp(pitch, -PitchClamp, PitchClamp);
    }

    public override void OnUpdate(Time time)
    {
        var dt = (float)time.Delta;
        if (dt <= 0) return;

        // Derive live sun direction from yaw/pitch sliders. The IBL cubemap
        // only re-bakes when the user hits the Rebake button — moving the
        // sliders previews direct lighting without the full upload cost.
        sunDirection = SunDirectionFromYawPitch(sunYaw, sunPitch);
        if (rebakeRequested)
        {
            RebakeSky();
            rebakeRequested = false;
        }

        // Sync slider-driven camera params.
        camera.VerticalFieldOfView = cameraFov;
        camera.NearPlane = cameraNearPlane;
        camera.FarPlane = cameraFarPlane;

        lightViewProjection = ComputeLightViewProjection(sunDirection);

        // Yaw rotates around world-Y so the camera's "forward" stays horizontal
        // when WASD-walking. Pitch is applied on top for mouse-look. WASD moves
        // on the horizontal plane (no flying via W/S); Space/Ctrl ascend/descend.
        var forwardHoriz = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        var rightHoriz = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));

        var move = Vector3.Zero;
        if (heldKeys.Contains(Key.W)) move -= forwardHoriz;
        if (heldKeys.Contains(Key.S)) move += forwardHoriz;
        if (heldKeys.Contains(Key.A)) move -= rightHoriz;
        if (heldKeys.Contains(Key.D)) move += rightHoriz;
        if (heldKeys.Contains(Key.Space)) move += Vector3.UnitY;
        if (heldKeys.Contains(Key.LeftControl) || heldKeys.Contains(Key.RightControl)) move -= Vector3.UnitY;

        if (move.LengthSquared() > 1e-6f) move = Vector3.Normalize(move);
        var speed = walkSpeed;
        if (heldKeys.Contains(Key.LeftSuper) || heldKeys.Contains(Key.RightSuper)) speed *= sprintMultiplier;

        camera.Transform.Position += move * speed * dt;

        // Apply yaw + pitch to the camera's rotation. Identity rotation looks
        // down -Z; we build pitch about local X then yaw about world Y.
        var pitchQ = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch);
        var yawQ = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        camera.Transform.Rotation = yawQ * pitchQ;

        // FPS HUD smoothing
        var instantFps = 1.0f / dt;
        fpsSmoothed = fpsSmoothed == 0.0f ? instantFps : fpsSmoothed * 0.9f + instantFps * 0.1f;
    }

    public override void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return;
        var aspect = (float)frame.Width / frame.Height;

        var view = camera.GetView();
        var proj = camera.GetProjection(aspect);

        // Shadow pass: render every primitive depth-only from the sun's POV
        // into the shadow surface. lit.frag samples this in the scene pass.
        var shadowUniforms = new ShaderUniform[]
        {
            new("uLightViewProjection", new Matrix4x4Uniform(lightViewProjection)),
            new("uModel", new Matrix4x4Uniform(Matrix4x4.Identity))
        };
        commandList.Pass(
            "walk.shadow",
            new RenderPassDescription(
                shadowSurface.Handle,
                ClearColors: Array.Empty<GraphicsColor?>(),
                ClearDepth: true),
            pass =>
            {
                foreach (var sub in sceneSubmeshes)
                {
                    pass.DrawMesh(sub.Mesh, shadowMaterial,
                        perDrawUniforms: shadowUniforms, perDrawTextures: null);
                }
            });

        var sharedUniforms = new ShaderUniform[]
        {
            new("uView", new Matrix4x4Uniform(view)),
            new("uProjection", new Matrix4x4Uniform(proj)),
            new("uLightViewProjection", new Matrix4x4Uniform(lightViewProjection)),
            new("uCameraPosition", new Vector3Uniform(camera.Transform.Position)),
            new("uSunDirection", new Vector3Uniform(sunDirection)),
            new("uSunColor", new Vector3Uniform(new Vector3(1.0f, 0.94f, 0.82f) * sunStrength)),
            new("uEnvMapMipCount", new FloatUniform(envCubeMipCount)),
            new("uExposure", new FloatUniform(exposure)),
            new("uModel", new Matrix4x4Uniform(Matrix4x4.Identity)),
            new("uNormalMatrix", new Matrix4x4Uniform(Matrix4x4.Identity)),
            // Slider-driven shader tuning (consumed by lit.frag).
            new("uEmissiveBoost", new FloatUniform(emissiveBoost)),
            new("uIblSpecAttenuation", new FloatUniform(iblSpecAttenuation)),
            new("uIblDiffuseBoost", new FloatUniform(iblDiffuseBoost)),
            new("uMetalFloor", new FloatUniform(metalFloor)),
            new("uIndirectShadowBase", new FloatUniform(indirectShadowBase)),
            new("uIndirectShadowRange", new FloatUniform(indirectShadowRange))
        };

        // Inverse view-projection for the skybox: lets its vertex shader
        // unproject NDC corners back into world space to compute the view ray
        // per fragment. The fragment shader samples the env cubemap (the same
        // cube bound for IBL) so backdrop + reflections show the same sun.
        var viewProjection = proj * view;
        Matrix4x4.Invert(viewProjection, out var invViewProj);
        var skyUniforms = new ShaderUniform[]
        {
            new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
            new("uCameraPosition", new Vector3Uniform(camera.Transform.Position)),
            new("uExposure", new FloatUniform(exposure))
        };

        commandList.Pass(
            "walk.scene",
            new RenderPassDescription(
                RenderSurfaceHandle.Default,
                // Clear colour is overwritten by the skybox fill pass at the
                // end. Kept as a sane sky-blue so the very first frame before
                // the skybox draws doesn't flash black.
                ClearColors: new GraphicsColor?[] { new(0.55f, 0.66f, 0.82f, 1.0f) },
                ClearDepth: true),
            pass =>
            {
                foreach (var sub in sceneSubmeshes)
                {
                    pass.DrawMesh(sub.Mesh, sub.LitMaterial,
                        perDrawUniforms: sharedUniforms, perDrawTextures: null);
                }
                // Sky fills the un-drawn pixels (depth=1 from the clear) using
                // LessEqualNoWrite. Drawn last so it costs only sky pixels and
                // doesn't waste fragment work behind opaque geometry.
                pass.DrawMesh(skyMesh, skyboxMaterial,
                    perDrawUniforms: skyUniforms, perDrawTextures: null);
            });

        DrawHud(time, frame, commandList);
    }

    // Light view-projection from the current sun direction. Eye sits opposite
    // sunDirection from the scene centre; ortho frustum is sized to contain
    // Sponza with margin. Up vector picks +Y unless the sun is near vertical.
    private Matrix4x4 ComputeLightViewProjection(Vector3 sunDir)
    {
        var L = Vector3.Normalize(-sunDir);
        var eye = SceneCenter + L * shadowSunDistance;
        var up = MathF.Abs(L.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var view = GraphicsMatrices.CreateLookAt(eye, SceneCenter, up);
        var projection = GraphicsMatrices.CreateOrthographic(
            shadowOrthoExtent * 2.0f, shadowOrthoExtent * 2.0f,
            shadowNearPlane, shadowFarPlane);
        return projection * view;
    }

    // Spherical-coords sun direction. Yaw = azimuth around +Y axis, pitch =
    // altitude (positive = up). Returned vector points FROM the sun INTO the
    // scene, so a yaw=0 pitch=-pi/2 means a sun directly overhead pointing
    // straight down.
    private static Vector3 SunDirectionFromYawPitch(float yawRad, float pitchRad)
    {
        var cp = MathF.Cos(pitchRad);
        return Vector3.Normalize(new Vector3(
            cp * MathF.Cos(yawRad),
            MathF.Sin(pitchRad),
            cp * MathF.Sin(yawRad)));
    }

    // Re-bake the IBL cubemap from the current bakedSunDirection and rebind it
    // on every material that samples uEnvMap. Called by the debug "Rebake Sky"
    // button. The old cube handle is left to leak — it's a debug action, and
    // the engine doesn't expose a public texture-delete on the device.
    private void RebakeSky()
    {
        bakedSunDirection = sunDirection;
        Console.WriteLine("Rebaking sky cubemap...");
        var pixels = CubemapBaker.BakeSky(EnvCubeFaceSize, bakedSunDirection);
        envCubemap = GraphicsDevice.CreateTextureCubeHdr(
            EnvCubeFaceSize, pixels,
            SamplerDescription.LinearClampMipmap,
            name: "walk.env_cube");
        foreach (var sub in sceneSubmeshes)
        {
            sub.LitMaterial.SetTexture("uEnvMap", envCubemap, 5);
        }
        skyboxMaterial.SetTexture("uEnvMap", envCubemap, 0);
    }

    public void Debug(DebugContext debug)
    {
        debug.State.Enabled = ShowDebug;
        debug.State.ShowOverlay = ShowDebug;
        debug.State.ShowDebugDraw = ShowDebug;

        using (debug.Scope("Frame"))
        {
            debug.Values.Value("FPS", $"{fpsSmoothed:0}");
            debug.Values.Value("Position", camera.Transform.Position);
            debug.Values.Value("Yaw deg", yaw * 180.0f / MathF.PI);
            debug.Values.Value("Pitch deg", pitch * 180.0f / MathF.PI);
            debug.Values.Value("Submeshes", sceneSubmeshes.Count);
        }

        using (debug.Scope("Camera"))
        {
            cameraFov = debug.Controls.Float("FoV (rad)", cameraFov, MathF.PI / 8.0f, MathF.PI / 1.5f);
            cameraNearPlane = debug.Controls.Float("Near", cameraNearPlane, 0.01f, 1.0f);
            cameraFarPlane = debug.Controls.Float("Far", cameraFarPlane, 20.0f, 500.0f);
        }

        using (debug.Scope("Controls"))
        {
            walkSpeed = debug.Controls.Float("Walk speed", walkSpeed, 0.5f, 20.0f);
            sprintMultiplier = debug.Controls.Float("Sprint x", sprintMultiplier, 1.0f, 10.0f);
            mouseLookSensitivity = debug.Controls.Float("Mouse sens", mouseLookSensitivity, 0.0005f, 0.01f);
        }

        using (debug.Scope("Sun"))
        {
            // Yaw is wrapped; pitch clamped to keep the sun above the horizon
            // bias the cubemap was tuned for. Direct lighting follows live;
            // IBL only updates on Rebake Sky.
            sunYaw = debug.Controls.Float("Yaw (rad)", sunYaw, -MathF.PI, MathF.PI);
            sunPitch = debug.Controls.Float("Pitch (rad)", sunPitch, -MathF.PI / 2.0f + 0.1f, -0.05f);
            sunStrength = debug.Controls.Float("Strength", sunStrength, 0.0f, 12.0f);
            debug.Values.Value("Live dir", sunDirection);
            debug.Values.Value("Baked dir", bakedSunDirection);
            if (debug.Controls.Button("Rebake Sky"))
            {
                rebakeRequested = true;
            }
        }

        using (debug.Scope("Shadow"))
        {
            shadowOrthoExtent = debug.Controls.Float("Ortho extent", shadowOrthoExtent, 4.0f, 60.0f);
            shadowSunDistance = debug.Controls.Float("Sun distance", shadowSunDistance, 5.0f, 80.0f);
            shadowNearPlane = debug.Controls.Float("Near", shadowNearPlane, 0.01f, 1.0f);
            shadowFarPlane = debug.Controls.Float("Far", shadowFarPlane, 10.0f, 200.0f);
        }

        using (debug.Scope("Tonemap"))
        {
            exposure = debug.Controls.Float("Exposure", exposure, 0.1f, 4.0f);
        }

        using (debug.Scope("IBL"))
        {
            iblSpecAttenuation = debug.Controls.Float("Spec atten", iblSpecAttenuation, 0.0f, 1.5f);
            iblDiffuseBoost = debug.Controls.Float("Diffuse boost", iblDiffuseBoost, 0.0f, 4.0f);
            metalFloor = debug.Controls.Float("Metal floor", metalFloor, 0.0f, 1.0f);
            indirectShadowBase = debug.Controls.Float("Shadow base", indirectShadowBase, 0.0f, 1.0f);
            indirectShadowRange = debug.Controls.Float("Shadow range", indirectShadowRange, 0.0f, 1.0f);
            debug.Values.Value("Env mip count", envCubeMipCount);
        }

        using (debug.Scope("Emissive"))
        {
            emissiveBoost = debug.Controls.Float("Boost", emissiveBoost, 0.0f, 10.0f);
        }
    }

    private void DrawHud(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        if (hudFont is null) return;
        var (logicalW, logicalH) = Host.LogicalSize;
        if (logicalW <= 0 || logicalH <= 0) return;
        var dpiScale = frame.Width / (float)logicalW;
        var ortho = GraphicsMatrices.CreateOrthographicOffCenter(
            0, logicalW, logicalH, 0, -1, 1);

        commandList.Pass(
            "walk.hud",
            new RenderPassDescription(
                RenderSurfaceHandle.Default,
                ClearColors: Array.Empty<GraphicsColor?>(),
                ClearDepth: false),
            pass =>
            {
                spriteBatch.Begin(ortho);

                var pos = camera.Transform.Position;
                var info =
                    $"FPS  {fpsSmoothed:0}\n" +
                    $"POS  {pos.X:0.0} {pos.Y:0.0} {pos.Z:0.0}";
                spriteBatch.DrawText(hudFont, 18.0f, info,
                    new Vector2(20, 20),
                    new GraphicsColor(0.85f, 0.90f, 1.00f, 0.85f),
                    dpiScale: dpiScale);

                const string controls = "WASD MOVE   SPACE/CTRL UP/DOWN   CMD SPRINT   C RELEASE MOUSE   ESC";
                const float controlsSize = 14.0f;
                var cm = SpriteBatchUiExtensions.MeasureText(hudFont, controlsSize, controls, dpiScale);
                spriteBatch.DrawText(hudFont, controlsSize, controls,
                    new Vector2(logicalW * 0.5f - cm.X * 0.5f, logicalH - 28.0f),
                    new GraphicsColor(0.55f, 0.65f, 0.85f, 0.70f),
                    dpiScale: dpiScale);

                spriteBatch.End(pass);
            });
    }
}
