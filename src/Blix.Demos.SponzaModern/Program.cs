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
    // Live-tunable from the Debug panel. Index 0 is the camera near; index N
    // is the maximum shadow distance. Each cascade slot covers (splits[i],
    // splits[i+1]) along the camera's forward axis.
    private readonly float[] cascadeSplits = { 0.1f, 6.0f, 22.0f, 60.0f };
    private float cameraNear = 0.1f;
    private float cameraFar = 500.0f;
    private bool enableFrustumCulling = true;
    // Inflates submesh AABBs by this many world units at frustum-test time.
    // 0 = canonical conservative cull; positive values expand the effective
    // frustum to compensate for grazing-plane false negatives.
    private float cullMargin = 0.0f;
    // Back-face culling toggle, plumbed through OpenGLGraphicsDevice's static
    // DebugForceDisableCullFace override so we don't rebuild the pipeline
    // graph per frame.
    private bool enableBackFaceCulling = true;

    // Camera + input state (the boilerplate any first-person walkthrough wants).
    private Camera3D camera = null!;
    private float yaw = -MathF.PI / 2.0f;    // initial: looking +X
    private float pitch = 0.0f;
    private const float PitchClamp = MathF.PI * 0.49f;
    private bool[] keys = new bool[256];
    private bool sprintHeld = false;
    private float cameraFov = MathF.PI / 3.0f;
    private bool mouseCaptured = true;
    // Last known cursor position in window-logical coords. Tracked even
    // when mouseCaptured is true (where it's typically pinned to the
    // window centre) so click-to-pick after pressing C has the right
    // coordinate to ray-cast through.
    private float lastMouseX;
    private float lastMouseY;

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
    // Modern Sponza is brighter than classic Sponza -- whiter textures,
    // bigger open atrium, full HDR sky bouncing everywhere. Default
    // exposure 1.0 (Walkthrough's value) puts most fragments above the
    // ACES knee and reads as washed-out white. 0.3 lands the scene in
    // the operator's mid-tone range; user can slide it back up if they
    // want a brighter look.
    private float exposure = 0.3f;
    private RenderSurface[] cascadeShadowSurfaces = null!;
    private TextureHandle[] cascadeShadowMaps = null!;
    private Matrix4x4[] cascadeLightVPs = new Matrix4x4[CascadeCount];
    private float shadowSunDistance = 40.0f;

    // Scene geometry. One GltfSceneInstance per pack (main + opt-in add-ons).
    // Loaded asynchronously: each pack's glTF parse + texture decode runs on
    // a background thread (Task.Run inside OnLoad), and OnUpdate promotes the
    // task result into a GltfSceneInstance on the main thread when ready.
    // This keeps OnLoad fast (~3s of env-probe + shader compile) so the
    // window can render the sky immediately while the rest streams in.
    private GltfSceneInstance? mainScene;
    private GltfSceneInstance? curtainsScene;
    private GltfSceneInstance? ivyScene;
    private GltfSceneInstance? treesScene;
    private readonly List<PendingPack> pendingPacks = new();
    // Set once after the main pack finishes building, so we move the camera
    // to the scene centre exactly once.
    private bool cameraReseatedToMain = false;

    private sealed record PendingPack(
        string Name,
        Task<GltfModel> Import,
        // Called on the main thread when Import completes. Runs the GL-side
        // GltfSceneInstance.Build using the loaded model + assigns it to
        // the demo's scene field.
        Action BuildAndAssign);

    // Fallback textures + lit pipelines.
    private TextureHandle whitePixel;
    private TextureHandle flatNormal;
    private TextureHandle neutralMetallicRoughness;
    private TextureHandle fullOcclusion;
    private PipelineHandle litOpaquePipeline;
    private PipelineHandle litDoubleSidedPipeline;
    private PipelineHandle litAlphaBlendPipeline;
    private Material shadowMaterial = null!;
    private Material cubeShadowMaterial = null!;
    private Material skyboxMaterial = null!;
    private Mesh skyMesh = null!;
    private PbrSceneRenderer pbrRenderer = null!;
    // Deferred GPU texture uploader. GltfSceneInstance enqueues per-material
    // texture uploads here at Build time; we drain a few ms each frame so
    // the scene renders immediately with placeholder textures and real ones
    // pop in over ~1-2 seconds.
    private ResourceUploader uploader = null!;
    private const double UploadBudgetMillis = 4.0;

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
    // Per-frame draw counters. Captured during command-list build and read
    // back into the HUD next frame so we can see frustum culling working.
    private int lastOpaqueDrawn;
    private int lastCascadeDrawn;

    // Live PBR-tuning knobs. These shadow the lit shader's `uMetalFloor`,
    // `uIndirectShadowBase`, `uIndirectShadowRange` uniforms so we can dial
    // them from the debug panel.
    private float metalFloor = 0.18f;
    private float indirectShadowBase = 0.60f;
    private float indirectShadowRange = 0.40f;
    private float iblDiffuseBoost = 1.0f;
    private float iblSpecAttenuation = 0.8f;
    // Normal map Y convention. OpenGL/glTF: Y up (sampled.g maps positive
    // = bump points up in tangent space). DirectX/UE: Y down (G channel
    // inverted). Many tools export DX style by default. Setting this to
    // -1 flips Y on sample so DX-authored maps light correctly under
    // GL/glTF assumptions.
    private float normalMapFlipY = 1.0f;

    // Lit shader debug view selector. Index must match the lit.frag switch.
    // State lives inside LightingDebugView (a registered IDebugUi); this
    // class reads from lightingDebug.SelectedViewIndex / .VisualizeCascades
    // in the render path. lightingDebug is constructed in OnLoad and
    // registered with the debug system so it produces its own scope and
    // owns its own custom panel.
    private LightingDebugView lightingDebug = null!;

    private static readonly string[] DebugViewNames =
    {
        "PBR (real)",
        "Albedo",
        "Emissive",
        "Indirect diffuse",
        "Indirect specular",
        "Direct sun",
        "Roughness",
        "Metallic",
        "World normal",
        // Below: raw-texture views to diagnose import/upload issues.
        // If "Metallic" shows white but "MR sample" shows the correct
        // dark blue channel, the bug is in uHasMetallicMap gating, not
        // the texture itself.
        "MR sample (raw)",
        "Normal sample (raw)",
        "Gates (R=hasMR G=normScale B=hasAO)",
        // Shadow / cascade diagnostic views.
        "Shadow value (1=lit 0=shadow)",
        "Selected cascade (R=0 G=1 B=2)",
        // Geometry attribute views. UV maps to red/green channels so any
        // constant-color BLOCKS (instead of smooth gradients) tell us UV
        // data is missing/wrong for that submesh.
        "UV (R=u G=v)",
        "UV mod 1 (tiles per UV unit)",
        // Sampling diagnostics. Mip level shows which mip the GPU actually
        // reads at the current view distance/angle -- if surfaces sample
        // mip 8/9/10 routinely they're losing all per-texel detail to mip
        // averaging, and the fix is anisotropic filtering, NOT more mips
        // or sharper textures.
        "Mip level sampled (grayscale)",
        "Mip level sampled (heatmap)",
    };

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
            // R=AO=1, G=roughness=1, B=metallic=0. Materials with no MR map
            // sample (1,1,0,1); the uHasMetallicMap=0 gate makes the lit
            // shader bypass this anyway, but staying close to the spec
            // intent keeps the bake correct if the gate is ever removed.
            new byte[] { 255, 255, 0, 255 }, name: "sm.neutral_mr");
        fullOcclusion = GraphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            new byte[] { 255, 255, 255, 255 }, name: "sm.full_ao");

        // --- Environment probe ------------------------------------------
        // Preferred path: load a pre-cooked .blixprobe sibling of the HDR.
        // Bypasses ~2s of equirect convolution + ~1.6s BRDF LUT integration
        // by reading the already-baked cube + LUT blobs straight into GL
        // textures. Falls back to a live bake when no cooked file is found
        // or when the source is procedural.
        var hdrPath = Path.Combine(assetsDir, "textures", "sky_hdr.hdr");
        var probePath = Path.ChangeExtension(hdrPath, ".blixprobe");
        if (File.Exists(probePath) && File.Exists(hdrPath))
        {
            Console.WriteLine($"Loading cooked probe: {probePath}");
            var probeSw = System.Diagnostics.Stopwatch.StartNew();
            var data = BlixProbeReader.Read(probePath);
            var baked = EnvironmentBaker.UploadCookedProbe(GraphicsDevice, data, "sm");
            envProbe = baked.Probe;
            brdfLut = baked.BrdfLut;
            envProfile = new EnvironmentProfile
            {
                // Profile is retained for HUD/debug readouts; runtime bake
                // is never triggered through it once we've loaded a cooked
                // probe. HDR image bytes aren't re-loaded -- they would only
                // be needed for a re-bake.
                Source = new ProceduralEnvironmentSource(sunDirection),
                EnvCubeFaceSize = data.EnvFaceSize,
                IrradianceFaceSize = data.IrradianceFaceSize,
                SpecularPrefilterBaseSize = data.PrefilterBaseSize,
                SpecularPrefilterMipCount = data.PrefilterMipCount,
            };
            if (envProbe.SunDirectionFromEquirect is { } hdrSun)
            {
                hdrSunDirectionFromEquirect = hdrSun;
                sunDirection = hdrSun;
                sunPitch = MathF.Asin(Math.Clamp(sunDirection.Y, -1.0f, 1.0f));
                sunYaw = MathF.Atan2(sunDirection.Z, sunDirection.X);
                Console.WriteLine($"  sun aligned to HDR: {sunDirection}");
            }
            Console.WriteLine($"  cooked probe loaded in {probeSw.ElapsedMilliseconds} ms");
        }
        else
        {
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
            var envSw = System.Diagnostics.Stopwatch.StartNew();
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
            Console.WriteLine($"  env probe baked in {envSw.ElapsedMilliseconds} ms");
            var brdfSw = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine("Baking BRDF LUT...");
            brdfLut = EnvironmentBaker.BakeBrdfLut(GraphicsDevice, 256, "sm.brdf_lut");
            Console.WriteLine($"  BRDF LUT baked in {brdfSw.ElapsedMilliseconds} ms");
        }

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
        // BLEND materials -- depth tested but no depth write, alpha blended.
        // No back-to-front sorting yet so order is submission order; a fix
        // for a future pass if it bites visibly. Material G-buffer attachment
        // gets alpha-blended too so SSR's roughness gate still kicks in.
        litAlphaBlendPipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                litShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualNoWrite,
                RasterizerState.BackFaceCulling,
                new[] { BlendState.AlphaBlend, BlendState.AlphaBlend }),
            name: "sm.lit.alphablend");

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
        uploader = new ResourceUploader(GraphicsDevice);

        lightingDebug = new LightingDebugView(DebugViewNames);

        // Register subsystem-level contributors with the diagnostics
        // registry so they get their own scopes and (where applicable)
        // their own ImGui panels. The Host is the Window; IDebugHost.System
        // returns null when the runtime wasn't built with diagnostics —
        // tolerated, since our own Debug() body still runs without it.
        if (Host is Blix.Diagnostics.IDebugHost { System: { } debugSystem })
        {
            debugSystem.Register(uploader);
            debugSystem.Register(lightingDebug);
            // Sponza ships ~200 submesh AABBs across its packs — useful
            // when investigating a specific cull bug, overwhelming as
            // a startup default. Layer is off; user toggles it on per
            // pack ("scene/main", "scene/curtains", ...) from the
            // Layers panel.
            debugSystem.State.LayersEnabled["scene"] = false;
        }

        var defaults = new GltfDefaultTextures(
            WhitePixel: whitePixel,
            FlatNormal: flatNormal,
            NeutralMetallicRoughness: neutralMetallicRoughness,
            FullOcclusion: fullOcclusion);
        // GenerateMipmaps=false: every material texture loads through the
        // cooked .blixtex path now, which supplies all mips pre-computed.
        // glGenerateMipmap would either regenerate over them (wasting work)
        // or fight the per-level UploadTextureMip path the ResourceUploader
        // walks for progressive uploads.
        // Trilinear filtering across the pre-baked mip chain. The cooked
        // .blixtex carries all 11 mip levels, ResourceUploader.EnqueueLazy
        // walks them onto the GPU in smallest-first order, and
        // ApplySamplerNoMipGen sets MIN_FILTER from this flag WITHOUT
        // ever calling glGenerateMipmap (so the pre-baked mips stay
        // intact). Without trilinear, sampling at grazing angles or far
        // distance reads only mip 0, producing severe aliasing that on
        // Sponza Modern reads as washed-out / greyed-out walls.
        var sampler = new SamplerDescription(
            TextureFilter.Linear, TextureFilter.Linear,
            TextureWrap.Repeat, TextureWrap.Repeat,
            GenerateMipmaps: true, Compare: false);

        // SponzaModern has no point lights, but lit.frag still declares
        // uPointShadowMap0..3 as samplerCubeShadow. If those samplers are
        // left unbound, Apple's GL driver defaults them to texture unit 0
        // (where uAlbedo lives) -- a sampler-type mismatch
        // (samplerCubeShadow vs sampler2D) which triggers GL_INVALID_OPERATION
        // and silently kills every PBR draw. Bind a single 8x8 dummy cube
        // depth texture (Compare:true so it matches the samplerCubeShadow
        // type) to all four slots. The shader only reads them when
        // uPointLightCount > 0, which we never set, so the dummy is never
        // actually sampled.
        var dummyCubeShadowSampler = new SamplerDescription(
            MinFilter: TextureFilter.Linear,
            MagFilter: TextureFilter.Linear,
            WrapU: TextureWrap.ClampToEdge,
            WrapV: TextureWrap.ClampToEdge,
            GenerateMipmaps: false,
            Compare: true);
        var dummyPointShadowCube = GraphicsDevice.CreateTextureCubeDepth(
            8, dummyCubeShadowSampler, name: "sm.pshadow.dummy");

        Action<Material, GltfMaterial?> bindShared = (material, _) =>
        {
            material.SetTexture("uShadowMap", cascadeShadowMaps[0], 4);
            material.SetTexture("uShadowMap1", cascadeShadowMaps[1], 13);
            material.SetTexture("uShadowMap2", cascadeShadowMaps[2], 14);
            material.SetTexture("uEnvMap", envProbe.EnvCubemap, 5);
            material.SetTexture("uDiffuseIrradiance", envProbe.DiffuseIrradiance, 10);
            material.SetTexture("uSpecularPrefilter", envProbe.PrefilteredSpecular, 11);
            material.SetTexture("uBrdfLut", brdfLut, 12);
            material.SetTexture("uPointShadowMap0", dummyPointShadowCube, 6);
            material.SetTexture("uPointShadowMap1", dummyPointShadowCube, 7);
            material.SetTexture("uPointShadowMap2", dummyPointShadowCube, 8);
            material.SetTexture("uPointShadowMap3", dummyPointShadowCube, 9);
            material.SetUniform("uEnvMapMipCount", new FloatUniform((float)envProbe.EnvCubeMipCount));
        };

        // Cache shared options so all packs share the same factories.
        // Each importer + GltfSceneInstance.Build call closes over these.
        // Sponza Modern's walls are authored as inside-facing single-sided
        // geometry. With back-face culling on, the camera sees through any
        // wall it ends up "behind" (e.g. exterior of an upper-floor walkway
        // wall whose textured side faces the courtyard). Route Opaque +
        // AlphaMask through the double-sided pipeline so both faces render.
        // Cost is negligible -- Sponza is mostly thin shell geometry, not
        // closed solids where back-face culling would meaningfully help.
        // Sponza Modern's walls render with the double-sided pipeline (see
        // pipeline assignments above). We also flip DoubleSided=true on every
        // imported GltfMaterial so the lit shader's uDoubleSided uniform
        // ends up at 1.0 -- that triggers the back-face normal-flip in
        // lit.frag (`if (uDoubleSided > 0.5 && !gl_FrontFacing) N0 = -N0`),
        // which gives back-facing fragments correct Lambertian + IBL
        // contribution instead of inverted/black lighting.
        Func<GltfMaterial, GltfMaterial> forceDoubleSided = gm => gm with { DoubleSided = true };
        var packOptions = new GltfSceneOptions
        {
            Opaque = litDoubleSidedPipeline,
            OpaqueDoubleSided = litDoubleSidedPipeline,
            AlphaMask = litDoubleSidedPipeline,
            AlphaMaskDoubleSided = litDoubleSidedPipeline,
            AlphaBlend = litAlphaBlendPipeline,
            AlphaBlendDoubleSided = litAlphaBlendPipeline,
            Uploader = uploader,
            Defaults = defaults,
            Sampler = sampler,
            OnMaterialBuilt = bindShared,
            OverrideMaterial = forceDoubleSided,
        };

        // Kick off each pack's glTF parse + texture decode on a background
        // thread. The model bytes go through ModelRoot.Load (synchronous file
        // IO + JSON parse) and then through GltfStaticImporter.Import (which
        // internally Parallel.ForEaches the PNG decode). Returns a model with
        // CPU-side meshes + decoded GltfTextures. The GL-side build happens
        // in OnUpdate when the task completes.
        SchedulePack("main", mainGltfPath, packOptions, scene => mainScene = scene);
        var curtainsPath = Path.Combine(assetsDir, "curtains", "NewSponza_Curtains_glTF.gltf");
        if (File.Exists(curtainsPath))
            SchedulePack("curtains", curtainsPath, packOptions, scene => curtainsScene = scene);
        var ivyPath = Path.Combine(assetsDir, "ivy", "NewSponza_IvyGrowth_glTF.gltf");
        if (File.Exists(ivyPath))
            SchedulePack("ivy", ivyPath, packOptions, scene => ivyScene = scene);
        var treesPath = Path.Combine(assetsDir, "trees", "NewSponza_CypressTree_glTF.gltf");
        if (File.Exists(treesPath))
        {
            // Cypress source declares the leaf material as alphaMode=BLEND.
            // Without a back-to-front sort the BLEND pipeline composites leaf
            // cards in submission order with alpha-blend, leaving the dark
            // patches inside the canopy. The texture's alpha is effectively
            // binary anyway (foliage cutout), so re-tag as MASK at load --
            // depth-write + alpha discard means leaves z-sort against
            // themselves cheaply and no sort pass is needed.
            var treesOptions = packOptions with
            {
                OverrideMaterial = gm =>
                {
                    // Start from the pack-level double-sided promotion; then
                    // re-tag BLEND -> MASK for leaf cards (see top of file
                    // for why the source's BLEND tag produces dark patches).
                    var m = forceDoubleSided(gm);
                    if (m.AlphaMode == GltfAlphaMode.Blend && m.BaseColorTexture is not null)
                    {
                        m = m with
                        {
                            AlphaMode = GltfAlphaMode.Mask,
                            AlphaCutoff = m.AlphaCutoff > 0.0f ? m.AlphaCutoff : 0.5f,
                        };
                    }
                    return m;
                },
            };
            SchedulePack("trees", treesPath, treesOptions, scene => treesScene = scene);
        }

        // --- Camera ---------------------------------------------------
        // No scene bounds yet (packs are still loading). Spawn at a placeholder
        // position; once the main pack arrives we re-seat to its centroid.
        camera = new Camera3D
        {
            Transform = new Transform3D { Position = new Vector3(0, 1.7f, 0) },
            VerticalFieldOfView = cameraFov,
            NearPlane = 0.1f,
            FarPlane = 500.0f,
        };
        (Host as IRenderHost)?.SetCursorCaptured(true);
    }

    // Schedules a pack's glTF parse + decode on the thread pool and queues
    // a build-on-main-thread step to run once the import completes. The
    // import is the heavy CPU work (parse + parallel PNG decode); the build
    // is GL work (VB/IB uploads + texture-enqueue via ResourceUploader).
    private void SchedulePack(
        string name,
        string gltfPath,
        GltfSceneOptions sharedOptions,
        Action<GltfSceneInstance> assignToField)
    {
        Console.WriteLine($"Scheduling pack '{name}': {gltfPath}");
        var importer = new GltfStaticImporter();
        var task = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var model = importer.Import(new AssetImportContext(AssetId.Parse($"models/{name}"), gltfPath));
            Console.WriteLine($"  '{name}' imported in {sw.ElapsedMilliseconds} ms");
            return model;
        });
        var perPackOptions = sharedOptions with { Prefix = name };
        pendingPacks.Add(new PendingPack(name, task, BuildAndAssign: () =>
        {
            var built = GltfSceneInstance.Build(GraphicsDevice, task.Result, perPackOptions);
            assignToField(built);
            // Register the new scene with the diagnostics registry so its
            // per-submesh AABBs appear in the Layers panel under
            // "scene/<name>" without any further demo wiring. Late
            // registration is fine — the producer's first emission lands
            // on the next frame after Build completes.
            if (Host is Blix.Diagnostics.IDebugHost { System: { } debugSystem })
            {
                debugSystem.Register(built);
            }
            Console.WriteLine($"  '{name}' built: {built.Submeshes.Count} submeshes, {built.Materials.All.Count} materials");
        }));
    }

    // Drains completed packs into the demo's scene fields. Called once per
    // frame from OnUpdate; runs on the GL thread.
    private void PromoteCompletedPacks()
    {
        for (var i = pendingPacks.Count - 1; i >= 0; i--)
        {
            var pack = pendingPacks[i];
            if (!pack.Import.IsCompleted) continue;
            pendingPacks.RemoveAt(i);
            if (pack.Import.IsFaulted)
            {
                Console.WriteLine($"  pack '{pack.Name}' failed: {pack.Import.Exception?.GetBaseException().Message}");
                continue;
            }
            pack.BuildAndAssign();
        }

        if (!cameraReseatedToMain && mainScene is not null)
        {
            // Now that main has bounds, drop the camera into the atrium.
            var b = mainScene.Bounds;
            var centre = (b.Min + b.Max) * 0.5f;
            camera.Transform.Position = new Vector3(centre.X, b.Min.Y + 1.7f, centre.Z);
            cameraReseatedToMain = true;
            Console.WriteLine($"Camera re-seated to main bounds centre: {camera.Transform.Position}");
        }
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
        // Drain any packs that finished their background import. Runs the
        // GL-side GltfSceneInstance.Build on the main thread.
        PromoteCompletedPacks();

        sunDirection = SunDirectionFromYawPitch(sunYaw, sunPitch);

        // WASD on the horizontal plane. Identity rotation looks down -Z;
        // forwardHoriz at yaw=0 is +Z so W subtracts (moves -Z = camera-forward).
        // Mirrors the Walkthrough demo's convention.
        var forwardHoriz = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        var rightHoriz = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        var move = Vector3.Zero;
        if (keys[(int)Key.W]) move -= forwardHoriz;
        if (keys[(int)Key.S]) move += forwardHoriz;
        if (keys[(int)Key.A]) move -= rightHoriz;
        if (keys[(int)Key.D]) move += rightHoriz;
        if (keys[(int)Key.Space]) move += Vector3.UnitY;
        if (keys[(int)Key.LeftControl]) move -= Vector3.UnitY;
        if (move.LengthSquared() > 1e-6f)
        {
            move = Vector3.Normalize(move);
            var speed = sprintHeld ? 12.0f : 4.0f;
            camera.Transform.Position += move * speed * (float)time.Delta;
        }

        // Compose rotation as yaw-about-world-Y then pitch-about-local-X,
        // matching the Walkthrough's known-good ordering.
        var pitchQ = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch);
        var yawQ = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        camera.Transform.Rotation = yawQ * pitchQ;

        var dt = (float)time.Delta;
        var instantaneousFps = dt > 1e-5f ? 1.0f / dt : 60.0f;
        fpsSmoothed += (instantaneousFps - fpsSmoothed) * 0.05f;
    }

    public override void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        // mainScene may be null for the first ~1-15 seconds while the pack
        // import runs in the background. Continue rendering anyway -- the
        // sky still paints, the scene pass renders nothing into the HDR
        // colour, and the user gets a sky-only view rather than a black
        // hang. AllScenes() returns an empty enumerable until packs land.

        // Drain pending texture uploads. Costs at most UploadBudgetMillis of
        // GL time per frame; the queue typically clears within ~1 second of
        // scene load. Always runs FIRST so the lit pass sees the latest
        // texture bindings.
        uploader.Drain(UploadBudgetMillis);

        camera.VerticalFieldOfView = cameraFov;
        camera.NearPlane = cameraNear;
        camera.FarPlane = cameraFar;
        var aspect = (float)frame.Width / Math.Max(frame.Height, 1);
        var view = camera.GetView();
        var proj = camera.GetProjection(aspect);
        var invViewProj = InvertOrIdentity(proj * view);

        // Feed the debug-draw channel the world->clip matrix every frame.
        // Without this, debug lines pass through Matrix4x4.Identity and
        // get culled in clip space — symptom: selection outlines and any
        // other Draw primitives are silently invisible.
        if (Host is Blix.Diagnostics.IDebugHost { CurrentDebug: { } dbg })
        {
            dbg.Draw.ViewProjection = proj * view;
        }
        // Setting this to null disables the per-submesh frustum cull so we
        // can A/B test whether geometry being missing is from our culling
        // step or downstream (back-face, depth, etc.).
        Frustum? cameraFrustum = enableFrustumCulling
            ? Frustum.FromViewProjection(proj * view)
            : null;

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
            Frustum? cascadeFrustum = enableFrustumCulling
                ? Frustum.FromViewProjection(cascadeVP)
                : null;
            commandList.Pass(
                $"sm.shadow.cascade{cascadeIndex}",
                new RenderPassDescription(
                    cascadeShadowSurfaces[cascadeIndex].Handle,
                    ClearColors: Array.Empty<GraphicsColor?>(),
                    ClearDepth: true),
                pass =>
                {
                    int drawn = 0;
                    foreach (var sceneInstance in AllScenes())
                    {
                        drawn += pbrRenderer.DrawCascadeShadow(pass, sceneInstance, cascadeVP, cascadeFrustum, cullMargin);
                    }
                    // Sum across all cascades into the cumulative counter, reset
                    // by the first cascade so the HUD shows total cascade draws.
                    if (cascadeIndex == 0) lastCascadeDrawn = 0;
                    lastCascadeDrawn += drawn;
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
        // Lit shader's demo-tuning uniforms. The shader multiplies IBL
        // diffuse + specular by these so leaving them unset (= 0) zeros
        // out indirect lighting -- only direct sun contributes -- and
        // sun strength alone clips to white in ACES tonemap. Defaults
        // copied from Walkthrough's known-good values.
        var demoTuning = new ShaderUniform[]
        {
            // Walkthrough's EmissiveBoost (2.5) is tuned for the brazier
            // flames; Modern Sponza has no large emissive surfaces so a
            // 1.0 baseline reads correctly. Tunable from the debug panel.
            new("uEmissiveBoost", new FloatUniform(1.0f)),
            new("uIblSpecAttenuation", new FloatUniform(iblSpecAttenuation)),
            new("uNormalMapFlipY", new FloatUniform(normalMapFlipY)),
            new("uIblDiffuseBoost", new FloatUniform(iblDiffuseBoost)),
            new("uMetalFloor", new FloatUniform(metalFloor)),
            new("uIndirectShadowBase", new FloatUniform(indirectShadowBase)),
            new("uIndirectShadowRange", new FloatUniform(indirectShadowRange)),
            new("uHorizonFadeStrength", new FloatUniform(0.85f)),
            new("uHorizonFadeStart", new FloatUniform(0.15f)),
            new("uSkyTint", new Vector3Uniform(Vector3.One)),
            new("uPointLightSpecScale", new FloatUniform(0.35f)),
            new("uPointShadowFarPlane", new FloatUniform(12.0f)),
            new("uPointShadowBias", new FloatUniform(0.005f)),
            new("uPointShadowFilterRadius", new FloatUniform(0.08f)),
            new("uDebugView", new FloatUniform(lightingDebug.SelectedViewIndex)),
        };
        var frameContext = new PbrFrameContext
        {
            View = view,
            Projection = proj,
            CameraPosition = camera.Transform.Position,
            SunDirection = sunDirection,
            SunColor = new Vector3(1.0f, 0.94f, 0.82f) * sunStrength,
            Environment = envProbe,
            Cascades = new CascadeShadowState(cascadeLightVPs, splitFloats, lightingDebug.VisualizeCascades),
            Exposure = exposure,
            ExtraUniforms = demoTuning,
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
                int drawn = 0;
                foreach (var sceneInstance in AllScenes())
                {
                    drawn += pbrRenderer.DrawScene(pass, sceneInstance, sharedUniforms, cameraFrustum, cullMargin);
                }
                lastOpaqueDrawn = drawn;
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
        // Compute the 8 corners of the CAMERA FRUSTUM SLICE [nearSplit, farSplit]
        // in view space, then transform to world. The old code used NDC z=[-1,1]
        // (full camera frustum, near=0.1 to far=500) for every cascade -- all
        // three cascades landed on the same huge box, blowing precision and
        // stamping phantom shadows everywhere. Slicing properly gives each
        // cascade a snug ortho sized to its depth range.
        //
        // We don't have aspect / FOV directly but can recover (right, top) at
        // a known near distance by unprojecting an NDC corner. View-space corner
        // = invView * (invProj * NDC); we get there in one step via invViewProj
        // then re-projecting through view.
        var nearNdc = TransformVec4(invViewProj, new Vector4(1.0f, 1.0f, -1.0f, 1.0f));
        var nearWorld = new Vector3(nearNdc.X, nearNdc.Y, nearNdc.Z) / nearNdc.W;
        var nearView = GraphicsMatrices.TransformPoint(view, nearWorld);
        var camNear = -nearView.Z; // view-space z is negative going forward
        var tanHalfFovX = nearView.X / camNear;
        var tanHalfFovY = nearView.Y / camNear;
        // Cascade slice corners in view space (z negative going forward).
        var nrx = nearSplit * tanHalfFovX;
        var nry = nearSplit * tanHalfFovY;
        var frx = farSplit  * tanHalfFovX;
        var fry = farSplit  * tanHalfFovY;
        var sliceView = new Vector3[8]
        {
            new(-nrx, -nry, -nearSplit), new( nrx, -nry, -nearSplit),
            new(-nrx,  nry, -nearSplit), new( nrx,  nry, -nearSplit),
            new(-frx, -fry, -farSplit),  new( frx, -fry, -farSplit),
            new(-frx,  fry, -farSplit),  new( frx,  fry, -farSplit),
        };
        Matrix4x4.Invert(view, out var invView);
        var corners = new Vector3[8];
        for (var i = 0; i < 8; i++) corners[i] = GraphicsMatrices.TransformPoint(invView, sliceView[i]);
        // Centroid + bounding sphere of the slice. Sphere bound is loose vs an
        // OBB but keeps the shadow box rotation-invariant which is what makes
        // the texel snap work.
        var center = Vector3.Zero;
        for (var i = 0; i < 8; i++) center += corners[i];
        center /= 8.0f;
        float radius = 0;
        for (var i = 0; i < 8; i++) radius = MathF.Max(radius, Vector3.Distance(corners[i], center));
        radius = MathF.Ceiling(radius);
        // Snap centre to texel grid in light space.
        var L = Vector3.Normalize(-sunDir);
        var up = MathF.Abs(L.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var lightView = GraphicsMatrices.CreateLookAt(center + L * (shadowSunDistance + radius), center, up);
        var texelSize = (2.0f * radius) / shadowMapSize;
        var centreLight = GraphicsMatrices.TransformPoint(lightView, center);
        centreLight.X = MathF.Round(centreLight.X / texelSize) * texelSize;
        centreLight.Y = MathF.Round(centreLight.Y / texelSize) * texelSize;
        Matrix4x4.Invert(lightView, out var invLightView);
        var snappedC3 = GraphicsMatrices.TransformPoint(invLightView, centreLight);
        var lightView2 = GraphicsMatrices.CreateLookAt(snappedC3 + L * (shadowSunDistance + radius), snappedC3, up);
        var projection = GraphicsMatrices.CreateOrthographicOffCenter(
            -radius, radius, -radius, radius,
            0.0f, 2.0f * (shadowSunDistance + radius));
        return projection * lightView2;
    }

    // Vector4 transform for column-vector matrices. System.Numerics's built-in
    // Vector4.Transform assumes row-vector storage and would silently drop the
    // M14/M24/M34 translation column on a Blix matrix.
    private static Vector4 TransformVec4(Matrix4x4 m, Vector4 v) => new(
        m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z + m.M14 * v.W,
        m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z + m.M24 * v.W,
        m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z + m.M34 * v.W,
        m.M41 * v.X + m.M42 * v.Y + m.M43 * v.Z + m.M44 * v.W);

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
        if (key == Key.P)
        {
            PickCenterScreen();
        }
    }

    // Two-mode picking: P at screen centre (works in FPS-capture mode);
    // left-click at cursor pos (works after pressing C to free the
    // cursor). Both funnel through DoPick() so the raycast + selection
    // bookkeeping lives in one place.
    private void PickCenterScreen()
    {
        var (logicalW, logicalH) = ((IRenderHost)Host).LogicalSize;
        var ray = camera.ScreenPointToRay(logicalW * 0.5f, logicalH * 0.5f, logicalW, logicalH);
        DoPick(ray);
    }

    private void PickAtCursor()
    {
        var (logicalW, logicalH) = ((IRenderHost)Host).LogicalSize;
        var ray = camera.ScreenPointToRay(lastMouseX, lastMouseY, logicalW, logicalH);
        DoPick(ray);
    }

    private void DoPick(Blix.Geometry.Ray ray)
    {
        if (Host is not Blix.Diagnostics.IDebugHost { System: { } debugSystem })
        {
            return;
        }
        var selectables = debugSystem.CollectSelectables();

        // Sponza has heavily overlapping submesh AABBs: large structural
        // pieces (floor, walls, ceilings) encompass dozens of smaller
        // decoration submeshes. Picking by ray-entry time alone almost
        // always lands on the big box, because the floor's AABB starts
        // near the camera's feet — its t_entry beats a chair sitting on
        // the floor every time. Two-pass:
        //   1. Collect every AABB the ray hits, with its hit-time.
        //   2. Among those, prefer the SMALLEST world-space volume —
        //      "click selects the most-specific thing your ray touches".
        // This trades a small amount of "I wanted the room, not the
        // sconce" surprise for far more "I clicked the chair and got
        // the chair." Without true mesh-level picking, it's the right
        // heuristic for Sponza-shaped scenes.
        DebugSelectable? best = null;
        var bestVolume = float.PositiveInfinity;
        var bestT = float.PositiveInfinity;
        for (var i = 0; i < selectables.Count; i++)
        {
            var s = selectables[i];
            if (Blix.Geometry.Intersection.Raycast(ray, s.Bounds, float.PositiveInfinity) is { } hit)
            {
                var ext = s.Bounds.Max - s.Bounds.Min;
                var volume = ext.X * ext.Y * ext.Z;
                if (volume < bestVolume)
                {
                    bestVolume = volume;
                    bestT = hit.Time;
                    best = s;
                }
            }
        }

        if (best is { } pick)
        {
            debugSystem.Select(pick.EntityPath, pick.Bounds);
            Console.WriteLine($"[diagnostics] picked {pick.EntityPath} at t={bestT:0.00}, volume={bestVolume:0.00}");
        }
        else
        {
            debugSystem.ClearSelection();
            Console.WriteLine("[diagnostics] pick missed; selection cleared");
        }
    }

    public void OnKeyUp(Key key)
    {
        if ((int)key < keys.Length) keys[(int)key] = false;
        if (key is Key.LeftSuper or Key.RightSuper) sprintHeld = false;
    }

    public void OnMouseDown(MouseButton button)
    {
        // Click-to-pick when the cursor is free (post C-toggle).
        // FPS-capture mode keeps the cursor pinned, so click-pick there
        // would always hit the screen centre — P key handles that case.
        if (button == MouseButton.Left && !mouseCaptured)
        {
            PickAtCursor();
        }
    }
    public void OnMouseUp(MouseButton button) { }
    // IMPORTANT: signature must match IInputHandler exactly -- the interface
    // declares OnMouseMove(float, float, float, float) with a default no-op
    // body, so a mismatching method (e.g. one taking Vector2 position +
    // Vector2 delta) silently does NOT override the interface and the
    // default no-op runs instead. Symptom: WASD works (keyboard signature
    // matches) but mouse-look does nothing.
    public void OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        lastMouseX = x;
        lastMouseY = y;
        if (!mouseCaptured) return;
        const float sensitivity = 0.002f;
        yaw -= deltaX * sensitivity;
        pitch = Math.Clamp(pitch - deltaY * sensitivity, -PitchClamp, PitchClamp);
    }
    public void OnMouseWheel(float offsetX, float offsetY) { }

    // --- IDebuggable ------------------------------------------------

    public void Debug(DebugContext debug)
    {
        debug.State.Enabled = true;
        // ShowOverlay used to be forced true here every frame, which
        // fought the runtime's tilde-toggle (Window swaps ShowOverlay;
        // next frame we'd flip it back). Initialise once via the field
        // default (true) and let the user own it from then on.
        debug.State.ShowDebugDraw = true;

        using (debug.Scope("Frame"))
        {
            debug.Values.Value("FPS", $"{fpsSmoothed:0}");
            debug.Values.Value("Position", camera.Transform.Position);
            debug.Values.Value("Submeshes (main)", mainScene?.Submeshes.Count ?? 0);
            debug.Values.Value("Submeshes (curtains)", curtainsScene?.Submeshes.Count ?? 0);
            debug.Values.Value("Submeshes (ivy)", ivyScene?.Submeshes.Count ?? 0);
            debug.Values.Value("Submeshes (trees)", treesScene?.Submeshes.Count ?? 0);
            int totalSubs = (mainScene?.Submeshes.Count ?? 0)
                + (curtainsScene?.Submeshes.Count ?? 0)
                + (ivyScene?.Submeshes.Count ?? 0)
                + (treesScene?.Submeshes.Count ?? 0);
            debug.Values.Value("Drawn opaque", $"{lastOpaqueDrawn} / {totalSubs}");
            debug.Values.Value("Drawn cascade", $"{lastCascadeDrawn} / {totalSubs * CascadeCount}");
        }

        // ResourceUploader and LightingDebugView are registered with the
        // DebugSystem in OnLoad; the registry walks them automatically
        // each frame under their own top-level scopes ("uploader",
        // "lighting-debug"), so no manual delegation is needed here.

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
            exposure = debug.Controls.Float("Exposure", exposure, 0.001f, 4.0f);
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

        // The lighting debug view (debug-view enum + cascade-color toggle)
        // is now a LightingDebugView registered with the debug system. Its
        // custom ImGui panel appears as its own collapsing header, and its
        // state is read directly via lightingDebug.SelectedViewIndex /
        // .VisualizeCascades in the render path above.

        using (debug.Scope("PBR tuning"))
        {
            // uMetalFloor: floor on indirect specular for metals. Default 0.18
            // produces near-black metals with dark-albedo authoring (Sponza
            // Modern metals are sRGB ~56 -> linear ~0.04). Crank to 1.0+ to
            // make metals visible; physically less correct above ~1.0 but
            // recovers asset visual.
            metalFloor = debug.Controls.Float("Metal floor", metalFloor, 0.0f, 4.0f);
            indirectShadowBase = debug.Controls.Float("Indirect shadow base", indirectShadowBase, 0.0f, 1.0f);
            indirectShadowRange = debug.Controls.Float("Indirect shadow range", indirectShadowRange, 0.0f, 1.0f);
            iblDiffuseBoost = debug.Controls.Float("IBL diffuse boost", iblDiffuseBoost, 0.0f, 4.0f);
            iblSpecAttenuation = debug.Controls.Float("IBL spec attenuation", iblSpecAttenuation, 0.0f, 1.0f);
            // Slider so you can move between +1 (GL/glTF Y up) and -1 (DX Y
            // down) live. If bumps look inverted (highlight on wrong side
            // of crevices) flipping this to -1 fixes it.
            normalMapFlipY = debug.Controls.Float("Normal Y (+1 GL / -1 DX)", normalMapFlipY, -1.0f, 1.0f);
        }

        using (debug.Scope("Camera"))
        {
            // Live FoV / near / far for debugging projection issues. cameraFov
            // is already wired separately for the input handler's zoom; tying
            // it here too keeps both in sync.
            cameraFov = debug.Controls.Float("FoV (rad)", cameraFov, 0.3f, 2.5f);
            cameraNear = debug.Controls.Float("Near", cameraNear, 0.01f, 5.0f);
            cameraFar = debug.Controls.Float("Far", cameraFar, 50.0f, 2000.0f);
            debug.Values.Value("Position", camera.Transform.Position);
        }

        using (debug.Scope("Cascades + culling"))
        {
            // A/B test: flip frustum culling off to see if missing geometry
            // reappears. If it does, my cull logic is excluding something it
            // shouldn't; if not, the bug is downstream (back-face, depth, etc.).
            enableFrustumCulling = debug.Controls.Toggle("Frustum cull", enableFrustumCulling);
            // Inflate submesh AABBs by this many world units before testing.
            // Diagnostic: if a small (~0.5m) margin makes "missing geometry"
            // reappear, our AABBs or the Gribb-Hartmann planes have a small
            // numerical offset that we should hunt down.
            cullMargin = debug.Controls.Float("Cull margin (m)", cullMargin, 0.0f, 10.0f);
            // Toggling this drives OpenGLGraphicsDevice.DebugForceDisableCullFace
            // -- when off, the GL backend ignores every pipeline's authored
            // CullMode and runs with cull-face disabled. Lets us see whether
            // missing geometry is back-faces (single-sided geometry seen from
            // the wrong side) without rebuilding any pipelines.
            enableBackFaceCulling = debug.Controls.Toggle("Back-face cull", enableBackFaceCulling);
            Blix.Graphics.OpenGL.OpenGLGraphicsDevice.DebugForceDisableCullFace = !enableBackFaceCulling;
            // Cascade splits along camera forward (distance from camera in
            // world units). [0]=near, [N]=max shadow distance. Bands must
            // stay monotonic ([i+1] > [i]); we clamp to enforce that.
            cascadeSplits[0] = debug.Controls.Float("Split 0 (near)", cascadeSplits[0], 0.01f, 10.0f);
            cascadeSplits[1] = debug.Controls.Float("Split 1", cascadeSplits[1], cascadeSplits[0] + 0.1f, 50.0f);
            cascadeSplits[2] = debug.Controls.Float("Split 2", cascadeSplits[2], cascadeSplits[1] + 0.1f, 150.0f);
            cascadeSplits[3] = debug.Controls.Float("Split 3 (max)", cascadeSplits[3], cascadeSplits[2] + 0.1f, 500.0f);
            debug.Values.Value("Drawn opaque", $"{lastOpaqueDrawn} / {(mainScene?.Submeshes.Count ?? 0) + (curtainsScene?.Submeshes.Count ?? 0) + (ivyScene?.Submeshes.Count ?? 0) + (treesScene?.Submeshes.Count ?? 0)}");
        }
    }
}
