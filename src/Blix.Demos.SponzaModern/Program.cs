using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Graphics.Primitives;
using Blix.Render;
using Blix.Runtime.OpenTK;

using var window = new Window(
    new SponzaModernGame(),
    new WindowOptions("Blix . Sponza Modern", 1440, 810));
window.Run();

// Modern Sponza walkthrough -- the Khronos Intel Sponza PBR-MR scene plus
// optional add-on packs (curtains, ivy, trees). Built to validate the new
// engine subsystems (GltfSceneInstance + EnvironmentProbe + PbrSceneRenderer
// + PostProcessStack) on a scene with foliage, double-sided geometry, and
// alpha-test cutouts that the classic Sponza didn't exercise.
//
// Asset story: the source packs are multi-GB and not committed. Run
// `tools/setup-sponza-modern.sh` once to populate Assets/ from your local
// copies; the demo logs a clear "missing assets" message and exits if the
// main glTF isn't found.
internal sealed class SponzaModernGame : Game, IInputHandler, IDebuggable
{
    public string DebugName => "SponzaModern";

    private const int EnvCubeFaceSize = 256;
    private const int ShadowMapSize = 2048;
    private const int CascadeCount = 3;
    private static readonly float[] cascadeSplits = { 0.1f, 6.0f, 22.0f, 60.0f };

    // Camera + input state (the boilerplate any first-person walkthrough wants).
    private Camera3D camera = null!;
    private float yaw = -MathF.PI / 2.0f;    // initial: looking +X
    private float pitch = 0.0f;
    private const float PitchClamp = MathF.PI * 0.49f;
    private bool[] keys = new bool[256];
    private bool sprintHeld = false;
    private float cameraFov = MathF.PI / 3.0f;
    private bool mouseCaptured = true;

    // Environment + IBL.
    private EnvironmentProfile envProfile = null!;
    private EnvironmentProbe envProbe = null!;
    private TextureHandle brdfLut;
    private Vector3? hdrSunDirectionFromEquirect;

    // Sun + cascades.
    private Vector3 sunDirection = Vector3.Normalize(new Vector3(0.3f, -0.7f, 0.2f));
    private float sunYaw = 0.6f;
    private float sunPitch = -1.0f;
    private float sunStrength = 4.0f;
    private float exposure = 1.0f;
    private RenderSurface[] cascadeShadowSurfaces = null!;
    private TextureHandle[] cascadeShadowMaps = null!;
    private Matrix4x4[] cascadeLightVPs = new Matrix4x4[CascadeCount];
    private float shadowSunDistance = 40.0f;

    // Scene geometry. One GltfSceneInstance per pack (main + opt-in add-ons).
    private GltfSceneInstance? mainScene;
    private GltfSceneInstance? curtainsScene;
    private GltfSceneInstance? ivyScene;
    private GltfSceneInstance? treesScene;

    // Fallback textures + lit pipelines.
    private TextureHandle whitePixel;
    private TextureHandle flatNormal;
    private TextureHandle neutralMetallicRoughness;
    private TextureHandle fullOcclusion;
    private PipelineHandle litOpaquePipeline;
    private PipelineHandle litDoubleSidedPipeline;
    private Material shadowMaterial = null!;
    private Material cubeShadowMaterial = null!;
    private Material skyboxMaterial = null!;
    private Mesh skyMesh = null!;
    private PbrSceneRenderer pbrRenderer = null!;

    // Post-process.
    private RenderSurface hdrSceneSurface = null!;
    private PostProcessStack post = null!;
    private bool bloomEnabled = true;
    private float bloomStrength = 0.10f;
    private bool ssrEnabled = true;
    private float ssrIntensity = 0.6f;
    private float ssrMaxDistance = 30.0f;
    private float ssrSteps = 40.0f;
    private float ssrThickness = 0.005f;
    private float ssrRoughnessCutoff = 0.4f;
    private bool fogEnabled = true;
    private float fogDensity = 0.02f;
    private float fogScatter = 0.20f;
    private float fogSteps = 28.0f;
    private float fogMaxDistance = 60.0f;
    private int tonemapMode = 0;
    private float saturation = 1.0f;
    private float contrast = 1.0f;
    private Vector3 colorTemp = Vector3.One;

    private float fpsSmoothed = 60.0f;

    protected override void OnLoad()
    {
        var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        var mainGltfPath = Path.Combine(assetsDir, "main_sponza", "NewSponza_Main_glTF_003.gltf");
        if (!File.Exists(mainGltfPath))
        {
            Console.WriteLine("Sponza Modern assets not found.");
            Console.WriteLine($"  expected: {mainGltfPath}");
            Console.WriteLine("Run `tools/setup-sponza-modern.sh` (with your local download of the");
            Console.WriteLine("Khronos Intel Sponza packs in ~/Downloads) to populate the Assets dir.");
            (Host as IRenderHost)?.RequestClose();
            return;
        }

        // --- Cascade shadow surfaces (depth-only) ----------------------
        cascadeShadowSurfaces = new RenderSurface[CascadeCount];
        cascadeShadowMaps = new TextureHandle[CascadeCount];
        var shadowSampler = new SamplerDescription(
            TextureFilter.Linear, TextureFilter.Linear,
            TextureWrap.ClampToEdge, TextureWrap.ClampToEdge,
            GenerateMipmaps: false, Compare: true);
        for (var c = 0; c < CascadeCount; c++)
        {
            cascadeShadowSurfaces[c] = GraphicsDevice.CreateRenderSurface(new RenderSurfaceDescription(
                Name: $"sm.shadow.cascade{c}",
                Size: new FixedRenderSurfaceSize(ShadowMapSize, ShadowMapSize),
                ColorAttachments: Array.Empty<ColorAttachmentDescription>(),
                Depth: new DepthTexture(shadowSampler)));
            cascadeShadowMaps[c] = cascadeShadowSurfaces[c].DepthTexture!.Value;
        }

        // --- Fallback textures -----------------------------------------
        whitePixel = GraphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            new byte[] { 255, 255, 255, 255 }, name: "sm.white");
        flatNormal = GraphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            new byte[] { 128, 128, 255, 255 }, name: "sm.flat_normal");
        neutralMetallicRoughness = GraphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            new byte[] { 0, 255, 0, 255 }, name: "sm.neutral_mr");
        fullOcclusion = GraphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            new byte[] { 255, 255, 255, 255 }, name: "sm.full_ao");

        // --- Environment probe ------------------------------------------
        var hdrPath = Path.Combine(assetsDir, "textures", "sky_hdr.hdr");
        if (File.Exists(hdrPath))
        {
            Console.WriteLine($"Loading HDR sky: {hdrPath}");
            var hdrImage = ImageLoader.LoadRgba32F(hdrPath);
            envProfile = new EnvironmentProfile
            {
                Source = new HdrEnvironmentSource(hdrImage),
                EnvCubeFaceSize = EnvCubeFaceSize,
            };
        }
        else
        {
            Console.WriteLine("No HDR sky -- falling back to procedural sky.");
            envProfile = new EnvironmentProfile
            {
                Source = new ProceduralEnvironmentSource(sunDirection),
                EnvCubeFaceSize = EnvCubeFaceSize,
            };
        }
        Console.WriteLine("Baking environment probe...");
        envProbe = EnvironmentBaker.Bake(GraphicsDevice, envProfile, "sm");
        if (envProbe.SunDirectionFromEquirect is { } hdrSun)
        {
            hdrSunDirectionFromEquirect = hdrSun;
            sunDirection = hdrSun;
            sunPitch = MathF.Asin(Math.Clamp(sunDirection.Y, -1.0f, 1.0f));
            sunYaw = MathF.Atan2(sunDirection.Z, sunDirection.X);
            Console.WriteLine($"  sun aligned to HDR: {sunDirection}");
        }
        Console.WriteLine("Baking BRDF LUT...");
        brdfLut = EnvironmentBaker.BakeBrdfLut(GraphicsDevice, 256, "sm.brdf_lut");

        // --- Lit shader + pipelines ------------------------------------
        var litShader = GraphicsDevice.CreateShaderProgram(LoadShader("lit.vert", "lit.frag"));
        litOpaquePipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                litShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                new[] { BlendState.Disabled, BlendState.Disabled }),
            name: "sm.lit.opaque");
        litDoubleSidedPipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                litShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.NoCulling,
                new[] { BlendState.Disabled, BlendState.Disabled }),
            name: "sm.lit.doublesided");

        // --- Shadow caster pipelines ------------------------------------
        var shadowShader = GraphicsDevice.CreateShaderProgram(LoadShader("shadow.vert", "shadow.frag"));
        var shadowPipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                shadowShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                BlendState.Disabled),
            name: "sm.shadow");
        shadowMaterial = new Material("sm.shadow", shadowPipeline);

        var cubeShadowShader = GraphicsDevice.CreateShaderProgram(LoadShader("shadow_cube.vert", "shadow_cube.frag"));
        var cubeShadowPipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                cubeShadowShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                BlendState.Disabled),
            name: "sm.shadow_cube");
        cubeShadowMaterial = new Material("sm.shadow_cube", cubeShadowPipeline);

        pbrRenderer = new PbrSceneRenderer("sm", shadowMaterial, cubeShadowMaterial);

        // --- Sky -----------------------------------------------------
        var skyShader = GraphicsDevice.CreateShaderProgram(LoadShader("skybox.vert", "skybox.frag"));
        var skyPipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                skyShader,
                VertexPositionTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualNoWrite,
                RasterizerState.NoCulling,
                new[] { BlendState.Disabled, BlendState.Disabled }),
            name: "sm.sky");
        skyboxMaterial = new Material("sm.sky", skyPipeline);
        skyboxMaterial.SetTexture("uEnvMap", envProbe.EnvCubemap, 0);
        var skyVerts = GraphicsDevice.CreateVertexBuffer(
            VertexPositionTexture.CreateBufferData(FullscreenQuad.Vertices),
            name: "sm.sky.verts");
        var skyIndices = GraphicsDevice.CreateIndexBuffer(FullscreenQuad.Indices, name: "sm.sky.indices");
        skyMesh = new Mesh("sm.sky", skyVerts, skyIndices, FullscreenQuad.Indices.Length, Bounds3.Empty);

        // --- HDR scene surface (color + material G-buffer + depth) -----
        var sceneDepthSampler = new SamplerDescription(
            TextureFilter.Linear, TextureFilter.Linear,
            TextureWrap.ClampToEdge, TextureWrap.ClampToEdge,
            GenerateMipmaps: false, Compare: false);
        hdrSceneSurface = GraphicsDevice.CreateRenderSurface(new RenderSurfaceDescription(
            Name: "sm.hdr_scene",
            Size: new MatchDefaultRenderSurfaceSize(1.0f),
            ColorAttachments: new[]
            {
                new ColorAttachmentDescription(TextureFormat.Rgba16F, SamplerDescription.LinearClamp),
                new ColorAttachmentDescription(TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            },
            Depth: new DepthTexture(sceneDepthSampler)));

        // --- Post-process resources ------------------------------------
        post = PostProcessStack.Create(
            GraphicsDevice,
            Path.Combine(AppContext.BaseDirectory, "Shaders"),
            namePrefix: "sm",
            bloomMipCount: 4);
        post.FogMaterial.SetTexture("uShadowMap", cascadeShadowMaps[CascadeCount - 1], 1);

        // --- Load Modern Sponza + add-ons via GltfSceneInstance --------
        var defaults = new GltfDefaultTextures(
            WhitePixel: whitePixel,
            FlatNormal: flatNormal,
            NeutralMetallicRoughness: neutralMetallicRoughness,
            FullOcclusion: fullOcclusion);
        var sampler = new SamplerDescription(
            TextureFilter.Linear, TextureFilter.Linear,
            TextureWrap.Repeat, TextureWrap.Repeat,
            GenerateMipmaps: true, Compare: false);

        Action<Material, GltfMaterial?> bindShared = (material, _) =>
        {
            material.SetTexture("uShadowMap", cascadeShadowMaps[0], 4);
            material.SetTexture("uShadowMap1", cascadeShadowMaps[1], 13);
            material.SetTexture("uShadowMap2", cascadeShadowMaps[2], 14);
            material.SetTexture("uEnvMap", envProbe.EnvCubemap, 5);
            material.SetTexture("uDiffuseIrradiance", envProbe.DiffuseIrradiance, 10);
            material.SetTexture("uSpecularPrefilter", envProbe.PrefilteredSpecular, 11);
            material.SetTexture("uBrdfLut", brdfLut, 12);
            material.SetUniform("uEnvMapMipCount", new FloatUniform((float)envProbe.EnvCubeMipCount));
        };

        mainScene = LoadPack(mainGltfPath, "main",
            litOpaquePipeline, litDoubleSidedPipeline, defaults, sampler, bindShared);

        var curtainsPath = Path.Combine(assetsDir, "curtains", "NewSponza_Curtains_glTF.gltf");
        if (File.Exists(curtainsPath))
        {
            curtainsScene = LoadPack(curtainsPath, "curtains",
                litOpaquePipeline, litDoubleSidedPipeline, defaults, sampler, bindShared);
        }
        var ivyPath = Path.Combine(assetsDir, "ivy", "NewSponza_IvyGrowth_glTF.gltf");
        if (File.Exists(ivyPath))
        {
            ivyScene = LoadPack(ivyPath, "ivy",
                litOpaquePipeline, litDoubleSidedPipeline, defaults, sampler, bindShared);
        }
        var treesPath = Path.Combine(assetsDir, "trees", "NewSponza_CypressTree_glTF.gltf");
        if (File.Exists(treesPath))
        {
            treesScene = LoadPack(treesPath, "trees",
                litOpaquePipeline, litDoubleSidedPipeline, defaults, sampler, bindShared);
        }

        // --- Camera ---------------------------------------------------
        camera = new Camera3D
        {
            Transform = new Transform3D { Position = new Vector3(-8.0f, 2.0f, 0.0f) },
            VerticalFieldOfView = cameraFov,
            NearPlane = 0.1f,
            FarPlane = 200.0f,
        };
        (Host as IRenderHost)?.SetCursorCaptured(true);
    }

    private GltfSceneInstance LoadPack(
        string gltfPath, string name,
        PipelineHandle opaque, PipelineHandle doubleSided,
        GltfDefaultTextures defaults, SamplerDescription sampler,
        Action<Material, GltfMaterial?> bindShared)
    {
        Console.WriteLine($"Loading pack '{name}': {gltfPath}");
        var importer = new GltfStaticImporter();
        var model = importer.Import(new AssetImportContext(AssetId.Parse($"models/{name}"), gltfPath));
        // AlphaMask reuses the opaque pipeline -- the lit shader's existing
        // `discard(a < 0.5)` covers the common case. AlphaMask + DoubleSided
        // pairs the no-cull pipeline with the same discard. We don't declare
        // an AlphaBlend pipeline yet; BLEND materials fall back to Opaque
        // with a warning (acceptable for Sponza Modern + addons -- nothing
        // in the source uses BLEND).
        var sceneInstance = GltfSceneInstance.Build(GraphicsDevice, model, new GltfSceneOptions
        {
            Opaque = opaque,
            OpaqueDoubleSided = doubleSided,
            AlphaMask = opaque,
            AlphaMaskDoubleSided = doubleSided,
            Defaults = defaults,
            Sampler = sampler,
            Prefix = name,
            OnMaterialBuilt = bindShared,
        });
        Console.WriteLine($"  {sceneInstance.Submeshes.Count} submeshes, {sceneInstance.Materials.All.Count} unique materials.");
        return sceneInstance;
    }

    private static ShaderSources LoadShader(string vertName, string fragName)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        return ShaderLoader.LoadVertexFragment(Path.Combine(dir, vertName), Path.Combine(dir, fragName));
    }

    private IEnumerable<GltfSceneInstance> AllScenes()
    {
        if (mainScene is not null) yield return mainScene;
        if (curtainsScene is not null) yield return curtainsScene;
        if (ivyScene is not null) yield return ivyScene;
        if (treesScene is not null) yield return treesScene;
    }

    public override void OnUpdate(Time time)
    {
        sunDirection = SunDirectionFromYawPitch(sunYaw, sunPitch);

        var forwardHoriz = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        var rightHoriz = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        var move = Vector3.Zero;
        if (keys[(int)Key.W]) move += forwardHoriz;
        if (keys[(int)Key.S]) move -= forwardHoriz;
        if (keys[(int)Key.D]) move += rightHoriz;
        if (keys[(int)Key.A]) move -= rightHoriz;
        if (keys[(int)Key.Space]) move += Vector3.UnitY;
        if (keys[(int)Key.LeftControl]) move -= Vector3.UnitY;
        if (move.LengthSquared() > 1e-6f)
        {
            move = Vector3.Normalize(move);
            var speed = sprintHeld ? 12.0f : 4.0f;
            camera.Transform.Position += move * speed * (float)time.Delta;
        }

        var fwd = Vector3.Normalize(new Vector3(
            MathF.Sin(yaw) * MathF.Cos(pitch),
            MathF.Sin(pitch),
            MathF.Cos(yaw) * MathF.Cos(pitch)));
        camera.Transform.Rotation = Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0.0f);

        var dt = (float)time.Delta;
        var instantaneousFps = dt > 1e-5f ? 1.0f / dt : 60.0f;
        fpsSmoothed += (instantaneousFps - fpsSmoothed) * 0.05f;
    }

    public override void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        if (mainScene is null) return;
        camera.VerticalFieldOfView = cameraFov;
        var aspect = (float)frame.Width / Math.Max(frame.Height, 1);
        var view = camera.GetView();
        var proj = camera.GetProjection(aspect);
        var invViewProj = InvertOrIdentity(proj * view);

        for (int c = 0; c < CascadeCount; c++)
        {
            cascadeLightVPs[c] = ComputeCascadeLightViewProjection(
                sunDirection, view, invViewProj,
                cascadeSplits[c], cascadeSplits[c + 1],
                ShadowMapSize);
        }

        // --- Cascade shadows: one pass per cascade --------------------
        for (int c = 0; c < CascadeCount; c++)
        {
            var cascadeIndex = c;
            var cascadeVP = cascadeLightVPs[c];
            commandList.Pass(
                $"sm.shadow.cascade{cascadeIndex}",
                new RenderPassDescription(
                    cascadeShadowSurfaces[cascadeIndex].Handle,
                    ClearColors: Array.Empty<GraphicsColor?>(),
                    ClearDepth: true),
                pass =>
                {
                    foreach (var sceneInstance in AllScenes())
                    {
                        pbrRenderer.DrawCascadeShadow(pass, sceneInstance, cascadeVP);
                    }
                });
        }

        // --- Skybox view-projection (inverse, for vertex-shader ray) --
        var skyUniforms = new ShaderUniform[]
        {
            new("uInvViewProjection", new Matrix4x4Uniform(invViewProj)),
            new("uExposure", new FloatUniform(exposure)),
            new("uSkyTint", new Vector3Uniform(Vector3.One)),
        };

        // --- Lit shader shared uniforms via PbrSceneRenderer ----------
        var splitFloats = new float[CascadeCount + 1];
        Array.Copy(cascadeSplits, splitFloats, CascadeCount + 1);
        var frameContext = new PbrFrameContext
        {
            View = view,
            Projection = proj,
            CameraPosition = camera.Transform.Position,
            SunDirection = sunDirection,
            SunColor = new Vector3(1.0f, 0.94f, 0.82f) * sunStrength,
            Environment = envProbe,
            Cascades = new CascadeShadowState(cascadeLightVPs, splitFloats, false),
            Exposure = exposure,
        };
        var sharedUniforms = pbrRenderer.PackSceneUniforms(frameContext);

        // --- Opaque + sky pass ----------------------------------------
        commandList.Pass(
            "sm.scene",
            new RenderPassDescription(
                hdrSceneSurface.Handle,
                ClearColors: new GraphicsColor?[] { new(0.55f, 0.66f, 0.82f, 1.0f) },
                ClearDepth: true),
            pass =>
            {
                foreach (var sceneInstance in AllScenes())
                {
                    pbrRenderer.DrawScene(pass, sceneInstance, sharedUniforms);
                }
                pass.DrawMesh(skyMesh, skyboxMaterial,
                    perDrawUniforms: skyUniforms, perDrawTextures: null);
            });

        var hdrColorTex = hdrSceneSurface.ColorAttachments[0];
        var hdrRoughnessTex = hdrSceneSurface.ColorAttachments[1];

        // --- Fog (additive over scene) --------------------------------
        if (fogEnabled && hdrSceneSurface.DepthTexture is { } sceneDepthHandle)
        {
            post.FogMaterial.SetTexture("uSceneDepth", sceneDepthHandle, 0);
            var fogUniforms = new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                new("uLightViewProjection", new Matrix4x4Uniform(cascadeLightVPs[CascadeCount - 1])),
                new("uCameraPosition", new Vector3Uniform(camera.Transform.Position)),
                new("uSunDirection", new Vector3Uniform(sunDirection)),
                new("uSunColor", new Vector3Uniform(new Vector3(1.0f, 0.94f, 0.82f) * sunStrength)),
                new("uFogDensity", new FloatUniform(fogDensity)),
                new("uFogScatter", new FloatUniform(fogScatter)),
                new("uFogSteps", new FloatUniform(fogSteps)),
                new("uFogMaxDistance", new FloatUniform(fogMaxDistance)),
                new("uPointLightPositions", new Vector3ArrayUniform(new Vector3[8])),
                new("uPointLightColors", new Vector3ArrayUniform(new Vector3[8])),
                new("uPointLightRanges", new FloatArrayUniform(new float[8])),
                new("uPointLightCount", new FloatUniform(0)),
                new("uFogPointScatter", new FloatUniform(0.0f)),
            };
            commandList.Pass(
                "sm.fog",
                new RenderPassDescription(
                    hdrSceneSurface.Handle,
                    ClearColors: Array.Empty<GraphicsColor?>(),
                    ClearDepth: false),
                pass => pass.DrawMesh(post.FullscreenQuad, post.FogMaterial,
                    perDrawUniforms: fogUniforms, perDrawTextures: null));
        }

        // --- SSR -----------------------------------------------------
        if (ssrEnabled && hdrSceneSurface.DepthTexture is { } ssrDepthHandle)
        {
            Matrix4x4.Invert(proj * view, out var ssrInvVP);
            post.SsrMaterial.SetTexture("uHdrScene", hdrColorTex, 0);
            post.SsrMaterial.SetTexture("uSceneDepth", ssrDepthHandle, 1);
            post.SsrMaterial.SetTexture("uRoughnessMap", hdrRoughnessTex, 2);
            commandList.Pass(
                "sm.ssr",
                new RenderPassDescription(
                    post.SsrSurface.Handle,
                    ClearColors: new GraphicsColor?[] { new(0, 0, 0, 0) },
                    ClearDepth: false),
                pass => pass.DrawMesh(post.FullscreenQuad, post.SsrMaterial,
                    perDrawUniforms: new ShaderUniform[]
                    {
                        new("uView", new Matrix4x4Uniform(view)),
                        new("uProjection", new Matrix4x4Uniform(proj)),
                        new("uInvViewProj", new Matrix4x4Uniform(ssrInvVP)),
                        new("uCameraPosition", new Vector3Uniform(camera.Transform.Position)),
                        new("uMaxDistance", new FloatUniform(ssrMaxDistance)),
                        new("uSteps", new FloatUniform(ssrSteps)),
                        new("uThickness", new FloatUniform(ssrThickness)),
                        new("uIntensity", new FloatUniform(ssrIntensity)),
                        new("uRoughnessCutoff", new FloatUniform(ssrRoughnessCutoff)),
                    }, perDrawTextures: null));
        }

        // --- Bloom (dual-filter down + up) ---------------------------
        if (bloomEnabled)
        {
            for (int i = 0; i < post.BloomMips.Count; i++)
            {
                var src = (i == 0) ? hdrColorTex : post.BloomMips[i - 1].ColorAttachments[0];
                var srcScale = (i == 0) ? 1.0f : (1.0f / MathF.Pow(2.0f, i));
                var srcTexel = new Vector2(1.0f / (frame.Width * srcScale), 1.0f / (frame.Height * srcScale));
                var localBloomMip = post.BloomMips[i];
                var localSrc = src;
                var localTexel = srcTexel;
                commandList.Pass(
                    $"sm.bloom.down{i}",
                    new RenderPassDescription(
                        localBloomMip.Handle,
                        ClearColors: new GraphicsColor?[] { new(0, 0, 0, 0) },
                        ClearDepth: false),
                    pass =>
                    {
                        post.BloomDownMaterial.SetTexture("uSrc", localSrc, 0);
                        pass.DrawMesh(post.FullscreenQuad, post.BloomDownMaterial,
                            perDrawUniforms: new ShaderUniform[] { new("uSrcTexel", new Vector2Uniform(localTexel)) },
                            perDrawTextures: null);
                    });
            }
            for (int i = post.BloomMips.Count - 2; i >= 0; i--)
            {
                var srcMip = post.BloomMips[i + 1];
                var srcScale = 1.0f / MathF.Pow(2.0f, i + 2);
                var srcTexel = new Vector2(1.0f / (frame.Width * srcScale), 1.0f / (frame.Height * srcScale));
                var localDstMip = post.BloomMips[i];
                var localSrcTex = srcMip.ColorAttachments[0];
                var localTexel = srcTexel;
                commandList.Pass(
                    $"sm.bloom.up{i}",
                    new RenderPassDescription(
                        localDstMip.Handle,
                        ClearColors: Array.Empty<GraphicsColor?>(),
                        ClearDepth: false),
                    pass =>
                    {
                        post.BloomUpMaterial.SetTexture("uSrc", localSrcTex, 0);
                        pass.DrawMesh(post.FullscreenQuad, post.BloomUpMaterial,
                            perDrawUniforms: new ShaderUniform[] { new("uSrcTexel", new Vector2Uniform(localTexel)) },
                            perDrawTextures: null);
                    });
            }
        }

        // --- Composite + tonemap (to swap chain) ---------------------
        post.CompositeMaterial.SetTexture("uHdrScene", hdrColorTex, 0);
        post.CompositeMaterial.SetTexture("uBloom",
            bloomEnabled ? post.BloomMips[0].ColorAttachments[0] : hdrColorTex, 1);
        post.CompositeMaterial.SetTexture("uSsr",
            ssrEnabled ? post.SsrSurface.ColorAttachments[0] : hdrColorTex, 2);
        commandList.Pass(
            "sm.composite",
            new RenderPassDescription(
                RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new(0, 0, 0, 1) },
                ClearDepth: true),
            pass => pass.DrawMesh(post.FullscreenQuad, post.CompositeMaterial,
                perDrawUniforms: new ShaderUniform[]
                {
                    new("uBloomStrength", new FloatUniform(bloomEnabled ? bloomStrength : 0.0f)),
                    new("uSsrStrength", new FloatUniform(ssrEnabled ? 1.0f : 0.0f)),
                    new("uSsrOnly", new FloatUniform(0.0f)),
                    new("uSsrFlipV", new FloatUniform(0.0f)),
                    new("uColorTemp", new Vector3Uniform(colorTemp)),
                    new("uSaturation", new FloatUniform(saturation)),
                    new("uContrast", new FloatUniform(contrast)),
                    new("uTonemapMode", new FloatUniform(tonemapMode)),
                }, perDrawTextures: null));
    }

    // --- Helpers ----------------------------------------------------

    private Matrix4x4 ComputeCascadeLightViewProjection(
        Vector3 sunDir, Matrix4x4 view, Matrix4x4 invViewProj,
        float nearSplit, float farSplit, int shadowMapSize)
    {
        var corners = new Vector3[8];
        float[] ndcZ = { -1.0f, 1.0f };
        var idx = 0;
        for (var x = -1.0f; x <= 1.0f; x += 2.0f)
            for (var y = -1.0f; y <= 1.0f; y += 2.0f)
                for (var k = 0; k < 2; k++)
                {
                    var ndc = new Vector4(x, y, ndcZ[k], 1.0f);
                    var w = Vector4.Transform(ndc, invViewProj);
                    corners[idx++] = new Vector3(w.X, w.Y, w.Z) / w.W;
                }
        var center = Vector3.Zero;
        for (var i = 0; i < 8; i++) center += corners[i];
        center /= 8.0f;
        // Clamp the view-space frustum to the cascade's depth slice.
        // Approximation: just take the world-space sphere bound of all 8 corners.
        float radius = 0;
        for (var i = 0; i < 8; i++) radius = MathF.Max(radius, Vector3.Distance(corners[i], center));
        // Snap centre to texel grid in light space.
        var L = Vector3.Normalize(-sunDir);
        var up = MathF.Abs(L.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var lightView = GraphicsMatrices.CreateLookAt(center + L * (shadowSunDistance + radius), center, up);
        var texelSize = (2.0f * radius) / shadowMapSize;
        // Project centre to light space, snap, project back.
        var clip = Vector4.Transform(new Vector4(center, 1), lightView);
        var snappedClip = new Vector4(
            MathF.Round(clip.X / texelSize) * texelSize,
            MathF.Round(clip.Y / texelSize) * texelSize,
            clip.Z, clip.W);
        Matrix4x4.Invert(lightView, out var invLightView);
        var snappedCentre = Vector4.Transform(snappedClip, invLightView);
        var snappedC3 = new Vector3(snappedCentre.X, snappedCentre.Y, snappedCentre.Z);
        var lightView2 = GraphicsMatrices.CreateLookAt(snappedC3 + L * (shadowSunDistance + radius), snappedC3, up);
        var projection = GraphicsMatrices.CreateOrthographicOffCenter(
            -radius, radius, -radius, radius,
            0.0f, 2.0f * (shadowSunDistance + radius));
        return projection * lightView2;
    }

    private static Matrix4x4 InvertOrIdentity(Matrix4x4 m)
        => Matrix4x4.Invert(m, out var inv) ? inv : Matrix4x4.Identity;

    private static Vector3 SunDirectionFromYawPitch(float yawRad, float pitchRad)
    {
        var cp = MathF.Cos(pitchRad);
        return Vector3.Normalize(new Vector3(
            cp * MathF.Cos(yawRad), MathF.Sin(pitchRad), cp * MathF.Sin(yawRad)));
    }

    // --- IInputHandler ---------------------------------------------

    public void OnKeyDown(Key key)
    {
        if ((int)key < keys.Length) keys[(int)key] = true;
        if (key is Key.LeftSuper or Key.RightSuper) sprintHeld = true;
        if (key == Key.Escape) (Host as IRenderHost)?.RequestClose();
        if (key == Key.C)
        {
            mouseCaptured = !mouseCaptured;
            (Host as IRenderHost)?.SetCursorCaptured(mouseCaptured);
        }
    }

    public void OnKeyUp(Key key)
    {
        if ((int)key < keys.Length) keys[(int)key] = false;
        if (key is Key.LeftSuper or Key.RightSuper) sprintHeld = false;
    }

    public void OnMouseDown(MouseButton button) { }
    public void OnMouseUp(MouseButton button) { }
    public void OnMouseMove(Vector2 position, Vector2 delta)
    {
        if (!mouseCaptured) return;
        const float sensitivity = 0.002f;
        yaw -= delta.X * sensitivity;
        pitch = Math.Clamp(pitch - delta.Y * sensitivity, -PitchClamp, PitchClamp);
    }
    public void OnMouseWheel(float delta) { }

    // --- IDebuggable ------------------------------------------------

    public void Debug(DebugContext debug)
    {
        debug.State.Enabled = true;
        debug.State.ShowOverlay = true;
        debug.State.ShowDebugDraw = true;

        using (debug.Scope("Frame"))
        {
            debug.Values.Value("FPS", $"{fpsSmoothed:0}");
            debug.Values.Value("Position", camera.Transform.Position);
            debug.Values.Value("Submeshes (main)", mainScene?.Submeshes.Count ?? 0);
            debug.Values.Value("Submeshes (curtains)", curtainsScene?.Submeshes.Count ?? 0);
            debug.Values.Value("Submeshes (ivy)", ivyScene?.Submeshes.Count ?? 0);
            debug.Values.Value("Submeshes (trees)", treesScene?.Submeshes.Count ?? 0);
        }

        using (debug.Scope("Sun"))
        {
            sunYaw = debug.Controls.Float("Yaw (rad)", sunYaw, -MathF.PI, MathF.PI);
            sunPitch = debug.Controls.Float("Pitch (rad)", sunPitch, -MathF.PI / 2.0f + 0.1f, -0.05f);
            sunStrength = debug.Controls.Float("Strength", sunStrength, 0.0f, 12.0f);
            debug.Values.Value("Live dir", sunDirection);
            if (hdrSunDirectionFromEquirect.HasValue && debug.Controls.Button("Sync sun to HDR"))
            {
                var d = hdrSunDirectionFromEquirect.Value;
                sunDirection = d;
                sunPitch = MathF.Asin(Math.Clamp(d.Y, -1.0f, 1.0f));
                sunYaw = MathF.Atan2(d.Z, d.X);
            }
        }

        using (debug.Scope("Tonemap"))
        {
            exposure = debug.Controls.Float("Exposure", exposure, 0.1f, 4.0f);
            bloomEnabled = debug.Controls.Toggle("Bloom", bloomEnabled);
            bloomStrength = debug.Controls.Float("Bloom strength", bloomStrength, 0.0f, 1.0f);
            tonemapMode = (int)debug.Controls.Float("Operator (0=ACES, 1=AgX, 2=Reinhard, 3=Neutral)",
                tonemapMode, 0.0f, 3.0f);
            saturation = debug.Controls.Float("Saturation", saturation, 0.0f, 2.0f);
            contrast = debug.Controls.Float("Contrast", contrast, 0.5f, 1.5f);
        }

        using (debug.Scope("Fog"))
        {
            fogEnabled = debug.Controls.Toggle("Enabled", fogEnabled);
            fogDensity = debug.Controls.Float("Density", fogDensity, 0.0f, 0.2f);
            fogScatter = debug.Controls.Float("Sun scatter", fogScatter, 0.0f, 1.0f);
            fogMaxDistance = debug.Controls.Float("Max distance", fogMaxDistance, 1.0f, 200.0f);
            fogSteps = debug.Controls.Float("Steps", fogSteps, 4.0f, 64.0f);
        }

        using (debug.Scope("SSR"))
        {
            ssrEnabled = debug.Controls.Toggle("Enabled", ssrEnabled);
            ssrIntensity = debug.Controls.Float("Intensity", ssrIntensity, 0.0f, 3.0f);
            ssrMaxDistance = debug.Controls.Float("Max distance", ssrMaxDistance, 1.0f, 80.0f);
            ssrSteps = debug.Controls.Float("Steps", ssrSteps, 4.0f, 64.0f);
            ssrThickness = debug.Controls.Float("Hit thickness (NDC.z)", ssrThickness, 0.0005f, 0.05f);
            ssrRoughnessCutoff = debug.Controls.Float("Roughness cutoff", ssrRoughnessCutoff, 0.0f, 1.0f);
        }
    }
}
