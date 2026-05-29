using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Graphics.Vulkan;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanSponza;

// Step 7 of the Vulkan reshape: SponzaModern port onto the Vector A
// binding model + Vector B render graph. This file is the SCAFFOLD —
// just enough wiring to load the main Sponza glTF, push every primitive
// through the lit shader, and present an HDR-tonemapped result. Cascade
// shadows, IBL, per-material variation, post-process, and the add-on
// packs are deliberately out of scope — each lands in a follow-up.
//
// Graph topology (v1):
//   lit-scene  : HDR Rgba16F + depth   (one draw per glTF primitive)
//   present    : imperative -> swapchain  (HDR -> Reinhard -> sRGB)
//
// Asset story: shares the existing Blix.Demos.SponzaModern/Assets tree
// (csproj links it in, runtime reads from the demo's own Assets/ copy
// in bin/). Missing-asset path prints a clear instruction and exits 0.
public static class Program
{
    public static void Main()
    {
        var loop = new SponzaLoop();
        using var window = new Window(loop, new WindowOptions("Blix — Vulkan Sponza (scaffold)", 1440, 810));
        window.Run();
    }
}

internal sealed class SponzaLoop : IGameLoop, IInputHandler, IDebuggable, IDisposable
{
    public string DebugName => "vulkan-sponza";

    private IRenderHost host = null!;
    private VulkanGraphicsDevice vk = null!;
    private RenderGraph graph = null!;

    // Graph resources + passes.
    private GraphResourceHandle hdrHandle;
    private GraphResourceHandle depthHandle;
    private PassHandle litPassHandle;

    // Lit pipelines — four variants spanning (Opaque|Mask vs Blend) ×
    // (BackFaceCulling vs NoCulling). All share one shader program; only
    // the depth/blend/rasterizer state varies. AlphaMode.Mask runs through
    // the opaque pipeline with the alpha-discard branch active in the
    // fragment shader (its `alphaCutoff > 0` guard skips when it's zero).
    private ShaderProgramHandle litProgram;
    private PipelineHandle opaqueSolidPipeline;
    private PipelineHandle opaqueDoubleSidedPipeline;
    private PipelineHandle blendSolidPipeline;
    private PipelineHandle blendDoubleSidedPipeline;

    // Skybox program + pipeline. Drawn between opaque/mask and blend so
    // translucent windows composite over the sky correctly.
    private ShaderProgramHandle skyProgram;
    private PipelineHandle skyPipeline;

    // Present.
    private ShaderProgramHandle presentProgram;
    private PipelineHandle presentPipeline;
    private VertexBufferHandle presentDummyVB;
    private IndexBufferHandle presentDummyIB;

    // --- Froxel volumetric fog --------------------------------------------
    // A compute pass fills a view-aligned 3D grid with sun in-scattering and
    // transmittance (shadow-aware, sampling the cascade maps), ordered after
    // the shadow passes and before the lit pass, which composites it per
    // fragment. The first real consumer of the compute layer + 3D textures.
    // Grid resolution. Each (x,y) thread marches all Z slices sampling the
    // cascades, so cost scales with X*Y*Z — this should become a renderer
    // quality preset (quarter/half/full) rather than a fixed size.
    private const int FroxelGridX = 128;
    private const int FroxelGridY = 72;
    private const int FroxelGridZ = 48;
    private ShaderProgramHandle froxelProgram;
    private PipelineHandle froxelPipeline;
    private TextureHandle froxelGridTexture;
    private PassHandle froxelPassHandle;
    private bool fogEnabled = false;    // demo toggle (compute cost); off by default
    private bool fogStress;             // --fog-stress: auto-toggle fog to exercise the on/off barrier transitions under validation
    private int fogStressFrame;
    private float fogDensity = 0.018f;  // extinction scale — subtle haze, not a wash
    private float fogScatter = 0.6f;    // scattering albedo
    private float fogPhaseG = 0.6f;     // Henyey-Greenstein anisotropy (forward)
    private float fogAmbient = 0.005f;  // ambient in-scatter floor
    private float fogFar = 60f;         // grid far distance (metres)

    // --- Cascaded sun shadow maps -----------------------------------------
    // Three depth-only cascades fitted to camera-frustum slices, snapped to
    // the texel grid for swim-free motion. One depth target + one graphics
    // pass per cascade; a single shadow pipeline serves all three (the
    // per-cascade light view-proj rides the shadow.vert push constant). The
    // lit pass samples all three via a Count=3 sampler array at set 1
    // binding 3 and picks one per fragment by view-space depth.
    private const int CascadeCount = 3;
    private const int ShadowMapSize = 2048;
    // How far behind the scene slab the light "eye" sits, in world units.
    // Larger keeps the whole atrium height inside each cascade's near/far.
    // Live-tunable from the overlay (Shadows scope).
    private float shadowSunDistance = 40f;
    // View-space depth boundaries: cascade i covers (Splits[i], Splits[i+1]).
    // [1..3] (the cascade far distances) are live-tunable from the overlay.
    private readonly float[] cascadeSplits = { 0.1f, 6f, 22f, 60f };
    // Per-cascade frustum culling of shadow casters (overlay toggle + margin).
    private bool cullEnabled = true;
    private float cullMargin = 0.5f;
    // Shadow depth-bias slope term: bias *= (1 + (1-NdotL) * slopeScale).
    private float slopeScale = 3f;
    private readonly GraphResourceHandle[] cascadeHandles = new GraphResourceHandle[CascadeCount];
    private readonly PassHandle[] cascadePassHandles = new PassHandle[CascadeCount];
    private readonly Matrix4x4[] cascadeViewProj = new Matrix4x4[CascadeCount];
    // Shadow-map caching: the last VP a cascade's depth map was rendered with.
    // A cascade's texel-snapped VP is bit-identical frame-to-frame while the
    // camera + sun + splits hold (and for sub-texel camera moves, thanks to the
    // snap), so we skip re-rendering an unchanged cascade and let the lit pass
    // sample the persisted depth target. Drops the full ~11.5M-tri redraw per
    // static cascade — the dominant GPU cost when the view is still.
    private readonly Matrix4x4[] cachedCascadeViewProj = new Matrix4x4[CascadeCount];
    // Whether each cascade re-rendered this frame (vs. served from cache) —
    // surfaced in the overlay so the caching is visible.
    private readonly bool[] cascadeRendered = new bool[CascadeCount];
    // Per-cascade base depth bias in NDC units, derived each frame from that
    // cascade's world-space texel size ÷ ortho depth range (≈ BiasTexels
    // shadow texels of slope-independent offset). The fragment shader adds a
    // grazing-angle slope term on top.
    private readonly float[] cascadeDepthBias = new float[CascadeCount];
    // Shadow depth bias in shadow-texels (live-tunable from the overlay).
    private float biasTexels = 1.5f;
    // Per-cascade shadow-caster survivor counts after frustum culling,
    // surfaced live in the diagnostics overlay (see Debug()).
    private readonly int[] cascadeDrawCounts = new int[CascadeCount];
    // Two shadow caster pipelines: opaque casters use a push-only program (no
    // descriptor sets → zero per-draw transient allocations), mask foliage uses
    // the alpha-cutout program (binds albedo). Routed per drawable by cutoff.
    private ShaderProgramHandle shadowOpaqueProgram;
    private PipelineHandle shadowOpaquePipeline;
    private ShaderProgramHandle shadowMaskProgram;
    private PipelineHandle shadowMaskPipeline;
    private bool shadowsEnabled = true;
    private bool visualizeCascades;
    // Diagnostics overlay visibility, toggled with Cmd+C. Applied to
    // DebugState.Enabled each frame in Debug() (which runs unconditionally).
    private bool overlayEnabled = true;

    // Default per-material textures used when a glTF material doesn't
    // supply the corresponding channel:
    //   fallbackAlbedo   1×1 white sRGB           — falls back to BaseColorFactor
    //   flatNormal       1×1 (128,128,255) linear — tangent-space "up"
    //   blackEmissive    1×1 (0,0,0) sRGB         — no glow
    //   defaultMr        1×1 (255,255,255) linear — (ao=1, roughnessFactor*1, metallicFactor*1)
    //   defaultAo        1×1 (255,_,_) linear      — no occlusion
    private TextureHandle fallbackAlbedo;
    private TextureHandle flatNormal;
    private TextureHandle blackEmissive;
    private TextureHandle defaultMr;
    private TextureHandle defaultAo;

    // IBL textures (procedural sky bake, CPU-side, generated once at load).
    //   envCube           prefiltered specular stand-in (mip chain at increasing roughness)
    //   irradianceCube    cosine-weighted hemisphere convolution of the sky
    //   brdfLut           split-sum BRDF integration (R = F0 scale, G = F0 bias)
    // Bound at set 1 (per-pass), so every draw in the lit pass samples them.
    private TextureHandle envCubeTexture;
    private TextureHandle irradianceCubeTexture;
    private TextureHandle brdfLutTexture;
    private const int EnvFaceSize = 64;
    private const int EnvMips = 7;            // log2(64) + 1
    private const int IrradianceFaceSize = 16;
    private const int BrdfLutSize = 128;
    // Mip count of whatever's bound to uPrefilteredEnv — the procedural env
    // cube (EnvMips) by default, or the cooked probe's GGX-prefilter mip count
    // when a .blixprobe is loaded. Drives the roughness→LOD mapping in lit.frag.
    private float iblPrefilterMips = EnvMips;

    // Bake-time sun direction (the irradiance + env cubes are baked once
    // with this sun position; live changes to the runtime sun direction
    // don't relight the IBL). Matches the runtime sun for consistency.
    private static readonly Vector3 SkyBakeSunDirection =
        Vector3.Normalize(new Vector3(0.35f, -0.85f, 0.25f));

    // Per-source-texture upload caches, one per material channel. The
    // static importer dedupes by glTF image index, so two materials
    // referencing the same image share one GltfTexture instance — we
    // mirror that on the GPU side so each unique PNG uploads exactly
    // once. Across Sponza Main's 405 primitives the BaseColor channel
    // collapses to ~25 uniques; normal + MR are smaller; emissive is empty.
    private readonly Dictionary<GltfTexture, TextureHandle> albedoCache = new();
    private readonly Dictionary<GltfTexture, TextureHandle> normalCache = new();
    private readonly Dictionary<GltfTexture, TextureHandle> emissiveCache = new();
    private readonly Dictionary<GltfTexture, TextureHandle> mrCache = new();
    private readonly Dictionary<GltfTexture, TextureHandle> aoCache = new();

    // Per-primitive draw payload. PipelineHandle is picked once at load
    // time from the material's AlphaMode + DoubleSided combination.
    private sealed record Drawable(
        VertexBufferHandle Vb,
        IndexBufferHandle Ib,
        int IndexCount,
        MaterialHandle Material,
        PipelineHandle Pipeline,
        // World-space AABB (the static importer bakes node transforms into the
        // vertices, so object bounds == world bounds) — used for per-cascade
        // shadow frustum culling.
        Bounds3 Bounds,
        // Shadow-caster cutout inputs: the albedo handle the shadow.frag
        // samples for MASK foliage, the alpha cutoff (0 for OPAQUE), and
        // baseColorFactor.a (glTF effective-alpha multiplier).
        TextureHandle Albedo,
        float AlphaCutoff,
        float BaseColorAlpha,
        // Pre-built once at load so the per-frame mask shadow draws don't
        // allocate a binding array each (×3 cascades × every frame).
        ShaderTextureBinding[] ShadowAlbedoBinding);
    // Two-bucket draw order: opaque/mask first, blend last. Within each
    // bucket draws stay in glTF primitive order; back-to-front sort for
    // the blend bucket is a deferred polish (would matter when the demo
    // moves at speed through translucent surfaces).
    private readonly List<Drawable> opaqueDrawables = new();
    private readonly List<Drawable> blendDrawables = new();
    private bool sceneLoaded;

    // Per-frame-reused, content-constant buffers built once at load (avoids
    // re-allocating them every frame). identityPush: the per-draw model push
    // (always identity — transforms are baked into the vertices). passBindings:
    // the lit pass's set-1 IBL + shadow-cascade textures (all stable handles).
    private byte[] identityPush = null!;
    private ShaderTextureBinding[] passBindings = null!;
    private ShaderTextureBinding[] froxelBindings = null!;
    // Mask shadow pushes differ per draw (alpha params), so they can't share one
    // buffer like opaque casters. Pool + reuse the byte[]s across frames instead
    // of allocating per draw: CmdPushConstants copies the bytes at record time,
    // so a buffer is free for reuse once the frame's commands are recorded. The
    // cursor resets each frame and the pool grows to the per-frame high-water mark.
    private readonly List<byte[]> maskPushPool = new();
    private int maskPushCursor;

    // Camera + input. Yaw 0 = looking down -Z; positive X is "right".
    // Initial pose aims at the +X end-wall lavabo (wall fountain) so the
    // sculpture is in the first frame, useful for normal-map debugging.
    private Vector3 cameraPosition = new(-9f, 3f, 0f);
    private Vector3 cameraForward = Vector3.UnitX;
    private float camYaw = MathF.PI / 2f;
    private float camPitch;
    private float aspect = 16f / 9f;
    private float fovYRadians = MathF.PI / 3f;
    private float moveSpeed = 4.5f;
    private bool mouseLook;
    private readonly HashSet<Key> heldKeys = new();
    private Matrix4x4 viewProj;

    // Sun + IBL strength (hardcoded; cascade shadows land in a follow-up).
    // SunIntensity scales the Lambert N·L term; IblIntensity scales the
    // combined diffuse + specular IBL contribution. Tuned so the atrium
    // floor reads in mid-tone without crushing the sunlit areas.
    // Sun travel direction, recomputed from sunYaw/sunPitch (overlay Sun
    // scope). Initialised in OnLoad from the default direction below. Note the
    // IBL cubes are baked once with SkyBakeSunDirection, so live sun changes
    // relight the direct sun + shadows but not the indirect IBL.
    private Vector3 sunDirection = Vector3.Normalize(new Vector3(0.35f, -0.85f, 0.25f));
    private float sunYaw;
    private float sunPitch;
    // Sun + IBL strength (live-tunable from the diagnostics overlay).
    private float sunIntensity = 3.0f;
    private readonly Vector3 ambientColor = new(0.42f, 0.50f, 0.62f);  // unused; kept for layout compat
    private float ambientIntensity = 1.6f;

    // Shader-debug knobs surfaced as uShaderParams (overlay Material scope):
    //   metallicThreshold — step() cutoff folding Sponza's ~0.35 "metalness"
    //                       to 0/1 (the "metallic floor" hack); 1 = all dielectric.
    //   normalStrength    — global multiplier on tangent-space normal x/y.
    private float metallicThreshold = 0.5f;
    private float normalStrength = 1.0f;

    private float exposure = 0.5f;
    // Present tonemap operator (overlay Render → Tonemap); index into present.frag's branch.
    private static readonly string[] TonemapNames = { "Reinhard", "ACES", "AgX", "Hejl" };
    private int tonemapMode = 2; // AgX

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        vk = (VulkanGraphicsDevice)graphicsDevice;
        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;

        // Volumetric fog is off by default (it adds a per-frame compute pass);
        // launch with --fog to start with it on, or toggle it in the overlay.
        var cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Contains("--fog")) fogEnabled = true;
        // --fog-stress flips fog on/off every ~90 frames so a validation run
        // exercises the compute storage-image layout transitions across the
        // disabled↔enabled boundary (the highest-risk sync path).
        if (cmdArgs.Contains("--fog-stress")) { fogStress = true; fogEnabled = true; }

        // Seed sun yaw/pitch from the default direction so the Sun controls
        // start matching the baked look.
        sunPitch = MathF.Asin(Math.Clamp(sunDirection.Y, -1f, 1f));
        sunYaw = MathF.Atan2(sunDirection.X, -sunDirection.Z);

        // Diagnostics overlay: GPU info + this loop's shadow/camera controls,
        // live values, and cascade gizmos (see Debug()). Replaces the old
        // Console-log + hardcoded-key debugging.
        if (host is IDebugHost debugHost && debugHost.System is { } debugSystem)
        {
            // Only register the GPU contributor. The runtime already runs this
            // loop's Debug() via Run(debuggable) since it implements IDebuggable
            // — also registering it would run (and render its controls) twice.
            graphicsDevice.RegisterDebug(debugSystem);
        }

        // --- Locate Sponza glTF -----------------------------------------
        // The setup-sponza-modern.sh script populates this path; when it
        // hasn't been run, exit cleanly so CI on a vanilla checkout doesn't
        // fail. The glTF filename varies across Khronos pack revisions, so
        // we glob for the first .gltf under main_sponza/.
        var assetsRoot = Path.Combine(AppContext.BaseDirectory, "Assets");
        var mainPackDir = Path.Combine(assetsRoot, "main_sponza");
        if (!Directory.Exists(mainPackDir))
        {
            Console.WriteLine($"[VulkanSponza] Main Sponza assets not found at {mainPackDir}.");
            Console.WriteLine("[VulkanSponza] Run tools/setup-sponza-modern.sh once to populate from your local Khronos packs.");
            host.RequestClose();
            return;
        }
        var gltfPath = Directory.EnumerateFiles(mainPackDir, "*.gltf", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (gltfPath is null)
        {
            Console.WriteLine($"[VulkanSponza] No .gltf file in {mainPackDir}. Re-run tools/setup-sponza-modern.sh.");
            host.RequestClose();
            return;
        }

        // --- Default per-material textures ------------------------------
        // 1×1 stand-ins routed to set-2 bindings when a material doesn't
        // supply the corresponding channel. The lit fragment shader always
        // samples all three; defaults keep the math well-defined without
        // shader branching.
        fallbackAlbedo = CreateFallbackAlbedoTexture(vk);
        flatNormal = vk.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearRepeat),
            new byte[] { 128, 128, 255, 255 }, "sponza.default.normal");
        blackEmissive = vk.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            new byte[] { 0, 0, 0, 255 }, "sponza.default.emissive");
        // Default MR (occlusion-roughness-metallic packed). All-ones so
        // materials without an MR texture pass the per-material factors
        // through unchanged: roughness = roughnessFactor × 1, metallic =
        // metallicFactor × 1, AO = 1 (no occlusion).
        defaultMr = vk.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearRepeat),
            new byte[] { 255, 255, 255, 255 }, "sponza.default.mr");
        // Default AO = 1.0 (full unobstructed light). Used for materials
        // without an OcclusionTexture; Sponza Modern packs MR into a single
        // PNG but ships AO as a separate channel-only texture.
        defaultAo = vk.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearRepeat),
            new byte[] { 255, 255, 255, 255 }, "sponza.default.ao");

        // --- IBL: cooked .blixprobe (real GGX prefilter) or procedural ------
        // Prefer a cooked probe baked from the HDR sky (real GGX importance-
        // sampled specular + cosine irradiance + split-sum BRDF LUT, RGBA16F).
        // Falls back to the procedural analytic-sky bake when no probe is
        // present (vanilla checkout that hasn't run the cook step).
        // Probe preference: autumn_field (has a sun → high light/dark contrast
        // for punchy shadows; we align our directional sun to its detected sun)
        // → rogland overcast (sunless ambient, our own sun) → old sky → procedural.
        // The sun-alignment is automatic: HdrSunFinder returns null for skies
        // with no clear sun, so we only align when the probe actually has one.
        string[] probeCandidates = { "autumn_field_4k.blixprobe", "rogland_overcast_4k.blixprobe", "sky_hdr.blixprobe" };
        var probePath = probeCandidates
            .Select(p => Path.Combine(assetsRoot, "textures", p))
            .FirstOrDefault(File.Exists);
        if (probePath is not null)
        {
            try
            {
                var baked = EnvironmentBaker.UploadCookedProbe(vk, BlixProbeReader.Read(probePath), "sponza.ibl");
                envCubeTexture = baked.Probe.PrefilteredSpecular;
                irradianceCubeTexture = baked.Probe.DiffuseIrradiance;
                brdfLutTexture = baked.BrdfLut;
                iblPrefilterMips = baked.Probe.PrefilteredSpecularMipCount;
                Console.WriteLine($"[VulkanSponza] IBL: cooked probe {Path.GetFileName(probePath)} ({iblPrefilterMips} GGX prefilter mips).");
                // Align the directional sun (key light + shadow caster) to the
                // probe's detected sun so cast shadows match the visible sky sun.
                // FROM-sun-into-scene convention, matching sunDirection.
                if (baked.Probe.SunDirectionFromEquirect is { } hdrSun)
                {
                    sunDirection = Vector3.Normalize(hdrSun);
                    sunPitch = MathF.Asin(Math.Clamp(sunDirection.Y, -1f, 1f));
                    sunYaw = MathF.Atan2(sunDirection.X, -sunDirection.Z);
                    Console.WriteLine($"[VulkanSponza]   sun aligned to probe: {sunDirection}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VulkanSponza] probe load failed ({ex.Message}); using procedural IBL.");
                BakeProceduralIbl();
            }
        }
        else
        {
            Console.WriteLine("[VulkanSponza] IBL: no cooked probe (run tools/setup-sponza-modern.sh / blix-cook probe); using procedural sky.");
            BakeProceduralIbl();
        }

        // --- Render graph ------------------------------------------------
        graph = new RenderGraph(vk);
        var fullSize = new MatchSwapchainGraphSize(1.0f);
        hdrHandle = graph.ColorTarget("hdr", TextureFormat.Rgba16F, fullSize);
        depthHandle = graph.DepthTarget("scene-depth", fullSize);

        // One fixed-size depth target per cascade (no 2D-array creation API
        // yet; the lit pass binds the three as a Count=3 sampler array).
        var shadowSize = new FixedGraphSize(ShadowMapSize, ShadowMapSize);
        for (var c = 0; c < CascadeCount; c++)
        {
            cascadeHandles[c] = graph.DepthTarget($"sun-cascade{c}", shadowSize);
        }

        // Per-frame UBO carries view-projection + sun + IBL strength + camera
        // position (the latter for Fresnel + reflection vector computation
        // in the lit shader). std140-padded:
        //   [0..63]   mat4 viewProjection
        //   [64..79]  vec3 sunDirection + float sunIntensity
        //   [80..95]  vec3 ambientColor (unused now)  + float iblIntensity
        //   [96..111] vec3 cameraPos + float envMipCount
        //   [112..123] vec3 cameraForward + [124] float shadowStrength
        //   [128..319] mat4 cascadeViewProj[3]   (std140 mat4 array, stride 64)
        //   [320..335] vec4 cascadeSplits        (.xyz = far depth of cascade 0/1/2)
        //   [336..351] vec4 shadowParams         (.x = visualizeCascades)
        //   [352..367] vec4 cascadeBias          (.xyz = per-cascade base depth bias)
        //   [368..383] vec4 shaderParams         (x=metallicThreshold, y=normalStrength, z=slopeScale)
        //   [384..399] vec4 fog                  (x=screenW, y=screenH, z=fogFar, w=enabled)
        var frameUbo = new UniformBlockLayout(
            TotalSize: 400,
            Members: new[]
            {
                new UniformBlockMember("uViewProjection",   0,   64),
                new UniformBlockMember("uSunDirection",     64,  12),
                new UniformBlockMember("uSunIntensity",     76,  4),
                new UniformBlockMember("uAmbientColor",     80,  12),
                new UniformBlockMember("uIblIntensity",     92,  4),
                new UniformBlockMember("uCameraPos",        96,  12),
                new UniformBlockMember("uEnvMipCount",      108, 4),
                new UniformBlockMember("uCameraForward",    112, 12),
                new UniformBlockMember("uShadowStrength",   124, 4),
                new UniformBlockMember("uCascadeViewProj",  128, 192),
                new UniformBlockMember("uCascadeSplits",    320, 16),
                new UniformBlockMember("uShadowParams",     336, 16),
                new UniformBlockMember("uCascadeBias",      352, 16),
                new UniformBlockMember("uShaderParams",     368, 16),
                new UniformBlockMember("uFog",              384, 16),
            });

        // Per-material UBO: BaseColorFactor (rgba), EmissiveFactor (rgb +
        // strength packed into .a), MaterialParams (alphaCutoff, normalScale).
        // std140 packs three vec4s = 48 bytes.
        var materialUbo = new UniformBlockLayout(
            TotalSize: 48,
            Members: new[]
            {
                new UniformBlockMember("uBaseColorFactor", 0,  16),
                new UniformBlockMember("uEmissiveFactor",  16, 16),
                new UniformBlockMember("uMaterialParams",  32, 16),
            });

        // The skybox shares the per-frame UBO and the per-pass IBL bindings
        // exactly so both interfaces can coexist in the same lit-scene pass
        // without RenderGraph set-1 compatibility complaints. Sky doesn't
        // need any set-2 material slots, so its interface stops at set 1.
        var skyInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer,
                    ShaderStages.Vertex | ShaderStages.Fragment, BlockLayout: frameUbo),
                new DescriptorSetSlot(1, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                new DescriptorSetSlot(1, 1, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                new DescriptorSetSlot(1, 2, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                // Declared for set-1 layout compatibility with the lit pipeline
                // sharing this pass; the sky shader never samples these.
                new DescriptorSetSlot(1, 3, ShaderResourceType.SampledImage, ShaderStages.Fragment, Count: CascadeCount),
                new DescriptorSetSlot(1, 4, ShaderResourceType.SampledImage, ShaderStages.Fragment), // uFroxelGrid (3D)
            },
            PushConstants: Array.Empty<PushConstantRange>());

        var litInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer,
                    ShaderStages.Vertex | ShaderStages.Fragment, BlockLayout: frameUbo),
                // Set 1 — per-pass IBL textures. Lifetime: bound by every
                // lit draw, identical references across all primitives in
                // the lit pass.
                new DescriptorSetSlot(1, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment), // uIrradiance (cube)
                new DescriptorSetSlot(1, 1, ShaderResourceType.SampledImage, ShaderStages.Fragment), // uPrefilteredEnv (cube)
                new DescriptorSetSlot(1, 2, ShaderResourceType.SampledImage, ShaderStages.Fragment), // uBrdfLut (2D)
                new DescriptorSetSlot(1, 3, ShaderResourceType.SampledImage, ShaderStages.Fragment, Count: CascadeCount), // uCascadeShadowMaps[3]
                new DescriptorSetSlot(1, 4, ShaderResourceType.SampledImage, ShaderStages.Fragment), // uFroxelGrid (3D fog grid)
                // Set 2 — per-material.
                new DescriptorSetSlot(2, 0, ShaderResourceType.UniformBuffer,
                    ShaderStages.Fragment, BlockLayout: materialUbo),
                new DescriptorSetSlot(2, 1, ShaderResourceType.SampledImage, ShaderStages.Fragment), // albedo
                new DescriptorSetSlot(2, 2, ShaderResourceType.SampledImage, ShaderStages.Fragment), // normal
                new DescriptorSetSlot(2, 3, ShaderResourceType.SampledImage, ShaderStages.Fragment), // emissive
                new DescriptorSetSlot(2, 4, ShaderResourceType.SampledImage, ShaderStages.Fragment), // mr (roughness + metallic packed)
                new DescriptorSetSlot(2, 5, ShaderResourceType.SampledImage, ShaderStages.Fragment), // ao (R channel)
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });

        var presentInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Fragment, 0, 8) });

        // Opaque shadow interface: push-only (model + cascadeViewProj = 128B),
        // no descriptor sets — opaque casters allocate zero transient sets.
        var shadowOpaqueInterface = new ShaderInterface(
            Slots: Array.Empty<DescriptorSetSlot>(),
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 128) });

        // Mask shadow interface: albedo sampler (set 0) for alpha cutout + a
        // 144-byte push spanning both stages ([model | cascadeViewProj] vertex,
        // [alphaParams] fragment). Only MASK foliage draws use this.
        var shadowMaskInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex | ShaderStages.Fragment, 0, 144) });

        // One graphics pass per cascade, each writing its own depth target.
        // Both shadow programs are render-pass-compatible with these passes.
        for (var c = 0; c < CascadeCount; c++)
        {
            cascadePassHandles[c] = graph.GraphicsPass($"sun-cascade{c}")
                .Depth(cascadeHandles[c], LoadOp.Clear, StoreOp.Store)
                .Shader(shadowOpaqueInterface, shadowMaskInterface)
                .Handle;
        }

        // Froxel fog compute pass — fills the 3D scattering grid. Declared
        // between the cascade passes and the lit pass so it runs after the
        // shadow maps are rendered (it samples them) and before the lit pass
        // composites its result. Set 0: UBO (binding 0), the storage grid
        // (binding 1), the cascade shadow maps (binding 2, Count=3).
        var froxelUbo = new UniformBlockLayout(
            TotalSize: 352,
            Members: new[]
            {
                new UniformBlockMember("uInvViewProj",   0,   64),
                new UniformBlockMember("uCamPos",        64,  16), // xyz pos, w = fogFar
                new UniformBlockMember("uCamForward",    80,  16), // xyz fwd, w = density
                new UniformBlockMember("uSunDir",        96,  16), // xyz into-scene, w = intensity
                new UniformBlockMember("uSunColor",      112, 16), // rgb, w = scatter
                new UniformBlockMember("uFogParams",     128, 16), // x=phaseG, y=ambient
                new UniformBlockMember("uCascadeVP",     144, 192),
                new UniformBlockMember("uCascadeSplits", 336, 16),
            });
        var froxelInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer, ShaderStages.Compute, BlockLayout: froxelUbo),
                new DescriptorSetSlot(0, 1, ShaderResourceType.StorageImage, ShaderStages.Compute),
                new DescriptorSetSlot(0, 2, ShaderResourceType.SampledImage, ShaderStages.Compute, Count: CascadeCount),
            },
            PushConstants: Array.Empty<PushConstantRange>());
        var froxelPass = graph.ComputePass("froxel-fog").Shader(froxelInterface);
        for (var c = 0; c < CascadeCount; c++)
        {
            froxelPass = froxelPass.Read(cascadeHandles[c]);
        }
        froxelPassHandle = froxelPass.Handle;

        var litPass = graph.GraphicsPass("lit-scene")
            .Target(hdrHandle, LoadOp.Clear, StoreOp.Store)
            .Depth(depthHandle, LoadOp.Clear, StoreOp.Store)
            .Shader(litInterface, skyInterface);
        // Declare the cascade depth targets as inputs so the graph orders the
        // shadow passes before the lit pass and transitions them to
        // shader-read layout.
        for (var c = 0; c < CascadeCount; c++)
        {
            litPass = litPass.Read(cascadeHandles[c]);
        }
        litPassHandle = litPass.Handle;
        graph.Compile();

        // --- Shader programs + pipelines --------------------------------
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var litVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "lit.vert.spv"));
        var litFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "lit.frag.spv"));
        litProgram = vk.CreateShaderProgramFromSpv(litVertSpv, litFragSpv, litInterface, "lit");
        // Opaque + Mask share the depth-write, no-blend pipeline group.
        // Mask materials trigger the discard branch via the per-material
        // UBO's alphaCutoff > 0; opaque materials leave it at 0.
        opaqueSolidPipeline = vk.CreatePipeline(new PipelineDescription(
            litProgram,
            VertexPosition3NormalTangentTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.BackFaceCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPassHandle)), "lit.opaque");
        opaqueDoubleSidedPipeline = vk.CreatePipeline(new PipelineDescription(
            litProgram,
            VertexPosition3NormalTangentTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPassHandle)), "lit.opaque.doubleSided");

        // Blend: depth-test (so windows don't draw behind walls) but no
        // depth-write (so successive translucent fragments don't z-fight),
        // src-alpha blend. Back-to-front sort within the blend bucket is a
        // deferred polish.
        blendSolidPipeline = vk.CreatePipeline(new PipelineDescription(
            litProgram,
            VertexPosition3NormalTangentTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualNoWrite,
            RasterizerState.BackFaceCulling,
            new[] { BlendState.AlphaBlend },
            RenderTarget: graph.GetPassSurface(litPassHandle)), "lit.blend");
        blendDoubleSidedPipeline = vk.CreatePipeline(new PipelineDescription(
            litProgram,
            VertexPosition3NormalTangentTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualNoWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.AlphaBlend },
            RenderTarget: graph.GetPassSurface(litPassHandle)), "lit.blend.doubleSided");

        // Sky pipeline. Depth-test LessEqual with NO write, so the sky only
        // draws where the depth buffer still holds the clear value (1.0)
        // and never overwrites opaque-geometry depth. Drawn between
        // opaque/mask and blend in OnRender so blend windows composite
        // over the sky.
        var skyVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "skybox.vert.spv"));
        var skyFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "skybox.frag.spv"));
        skyProgram = vk.CreateShaderProgramFromSpv(skyVertSpv, skyFragSpv, skyInterface, "skybox");
        skyPipeline = vk.CreatePipeline(new PipelineDescription(
            skyProgram,
            VertexPosition3NormalTexture.Layout, // ignored — sky vert synthesises positions
            PrimitiveTopology.Triangles,
            DepthState.LessEqualNoWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPassHandle)), "skybox");

        // Shadow caster pipelines. NoCulling so Sponza's double-sided foliage
        // and thin geometry still write depth from both faces; depth-write
        // LessEqual into the cascade target. Both are render-pass-compatible
        // with every cascade pass (all depth-only, same format), so we build
        // them against cascade 0's surface and reuse across cascades.
        var shadowVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow.vert.spv"));
        var shadowFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow.frag.spv"));
        shadowOpaqueProgram = vk.CreateShaderProgramFromSpv(shadowVertSpv, shadowFragSpv, shadowOpaqueInterface, "shadow.opaque");
        shadowOpaquePipeline = vk.CreatePipeline(new PipelineDescription(
            shadowOpaqueProgram,
            VertexPosition3NormalTangentTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(cascadePassHandles[0])), "shadow.opaque");

        var shadowMaskVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow_mask.vert.spv"));
        var shadowMaskFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow_mask.frag.spv"));
        shadowMaskProgram = vk.CreateShaderProgramFromSpv(shadowMaskVertSpv, shadowMaskFragSpv, shadowMaskInterface, "shadow.mask");
        shadowMaskPipeline = vk.CreatePipeline(new PipelineDescription(
            shadowMaskProgram,
            VertexPosition3NormalTangentTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(cascadePassHandles[0])), "shadow.mask");

        var presentVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.vert.spv"));
        var presentFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.frag.spv"));
        presentProgram = vk.CreateShaderProgramFromSpv(presentVertSpv, presentFragSpv, presentInterface, "present");
        presentPipeline = vk.CreatePipeline(new PipelineDescription(
            presentProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.Disabled,
            RasterizerState.NoCulling,
            BlendState.Disabled), "present");

        // Dummy VB/IB so the fullscreen-triangle present pass has something
        // to bind. The vertex shader synthesises positions from gl_VertexIndex.
        var dummyVerts = new VertexPosition3NormalTexture[]
        {
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0)),
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0)),
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0)),
        };
        presentDummyVB = vk.CreateVertexBuffer(
            VertexPosition3NormalTexture.CreateBufferData(dummyVerts), "present.dummy.vb");
        presentDummyIB = vk.CreateIndexBuffer(new ushort[] { 0, 1, 2 }, name: "present.dummy.ib");

        // --- Froxel fog compute program + grid ---------------------------
        var froxelSpv = File.ReadAllBytes(Path.Combine(shaderDir, "froxel.comp.spv"));
        froxelProgram = vk.CreateComputeShaderProgramFromSpv(froxelSpv, froxelInterface, "froxel");
        froxelPipeline = vk.CreateComputePipeline(froxelProgram, "froxel");
        // View-aligned 3D scattering grid, sampled trilinearly by the lit pass.
        froxelGridTexture = vk.CreateStorageTexture3D(
            FroxelGridX, FroxelGridY, FroxelGridZ,
            TextureFormat.Rgba16F, SamplerDescription.LinearClamp, "sponza.froxel_grid");

        // Build the per-frame-constant buffers once (graph compiled + all
        // textures created by now). Reused every frame in OnRender.
        identityPush = ModelPushBytes(Matrix4x4.Identity);
        passBindings = new[]
        {
            new ShaderTextureBinding("uIrradiance",     irradianceCubeTexture, Slot: 0),
            new ShaderTextureBinding("uPrefilteredEnv", envCubeTexture,        Slot: 1),
            new ShaderTextureBinding("uBrdfLut",        brdfLutTexture,        Slot: 2),
            new ShaderTextureBinding("uCascadeShadowMaps[0]", graph.GetDepthTexture(cascadeHandles[0]), Slot: 3, ArrayIndex: 0),
            new ShaderTextureBinding("uCascadeShadowMaps[1]", graph.GetDepthTexture(cascadeHandles[1]), Slot: 3, ArrayIndex: 1),
            new ShaderTextureBinding("uCascadeShadowMaps[2]", graph.GetDepthTexture(cascadeHandles[2]), Slot: 3, ArrayIndex: 2),
            new ShaderTextureBinding("uFroxelGrid",           froxelGridTexture, Slot: 4),
        };

        // Froxel compute set-0 image bindings (constant handles): the storage
        // grid it writes (binding 1) + the cascade shadow maps it samples
        // (binding 2, Count=3). The UBO (binding 0) is written per frame.
        froxelBindings = new[]
        {
            new ShaderTextureBinding("uGrid", froxelGridTexture, Slot: 1),
            new ShaderTextureBinding("uCascadeShadowMaps[0]", graph.GetDepthTexture(cascadeHandles[0]), Slot: 2, ArrayIndex: 0),
            new ShaderTextureBinding("uCascadeShadowMaps[1]", graph.GetDepthTexture(cascadeHandles[1]), Slot: 2, ArrayIndex: 1),
            new ShaderTextureBinding("uCascadeShadowMaps[2]", graph.GetDepthTexture(cascadeHandles[2]), Slot: 2, ArrayIndex: 2),
        };

        // --- Load Sponza geometry ---------------------------------------
        // Static-mesh importer: Sponza has no skinning. Each glTF primitive
        // becomes one engine-side mesh with a baked-in node transform.
        // Materials are deferred for v1 — every primitive uses the fallback
        // albedo through one shared MaterialHandle.
        try
        {
            var importer = new GltfStaticImporter();
            // flipTextureV: the Intel Sponza assets are authored bottom-up
            // (OpenGL V origin); flip to the top-down origin Vulkan samples
            // with so textures aren't vertically inverted. includeTangents:
            // forward the authored glTF TANGENT so the lit shader uses a real
            // per-vertex TBN. Per-import config — see AssetImportContext.
            var ctx = new AssetImportContext(AssetId.Parse("models/sponza_main"), gltfPath, flipTextureV: true, includeTangents: true);
            var model = importer.Import(ctx);
            BuildDrawables(model);
            sceneLoaded = true;
            Console.WriteLine($"[VulkanSponza] main pack: {model.Primitives.Length} primitives from {Path.GetFileName(gltfPath)}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VulkanSponza] Failed to load {gltfPath}: {ex.Message}");
            Console.WriteLine("[VulkanSponza] Continuing with empty scene; window will render the clear color.");
        }

        // Optional add-on packs. Each ships its own glTF + bin + textures
        // under Assets/<packname>/ and is loaded through the same static
        // importer + BuildDrawables path. Missing packs are silent — the
        // setup script skips them when their source folder isn't present.
        LoadOptionalPack(assetsRoot, "curtains", "addons/curtains");
        LoadOptionalPack(assetsRoot, "ivy",      "addons/ivy");
        LoadOptionalPack(assetsRoot, "trees",    "addons/trees");
        // Candles intentionally not loaded — they're tiny emissive point-light
        // stand-ins with no actual light source in this renderer, so they just
        // add draw count without contributing to the daylight + sun-shadow look
        // we're building toward.

        Console.WriteLine($"[VulkanSponza] textures cached: {albedoCache.Count} albedo, {normalCache.Count} normal, {mrCache.Count} MR, {aoCache.Count} AO, {emissiveCache.Count} emissive.");
        Console.WriteLine($"[VulkanSponza] total draws: {opaqueDrawables.Count} opaque/mask, {blendDrawables.Count} blend.");

        UpdateCamera();
    }

    private void LoadOptionalPack(string assetsRoot, string packDirName, string assetIdPrefix)
    {
        var packDir = Path.Combine(assetsRoot, packDirName);
        if (!Directory.Exists(packDir))
        {
            return;
        }
        var gltf = Directory.EnumerateFiles(packDir, "*.gltf", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (gltf is null)
        {
            return;
        }
        try
        {
            var model = new GltfStaticImporter().Import(
                new AssetImportContext(AssetId.Parse(assetIdPrefix), gltf, flipTextureV: true, includeTangents: true));
            var beforeOpaque = opaqueDrawables.Count;
            var beforeBlend  = blendDrawables.Count;
            BuildDrawables(model);
            var addedOpaque = opaqueDrawables.Count - beforeOpaque;
            var addedBlend  = blendDrawables.Count - beforeBlend;
            Console.WriteLine($"[VulkanSponza] {packDirName} pack: +{addedOpaque} opaque/mask, +{addedBlend} blend ({model.Primitives.Length} primitives total from {Path.GetFileName(gltf)}).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VulkanSponza] {packDirName} pack failed to load: {ex.Message}");
        }
    }

    // Spatial sub-batch cap: primitives sharing a material merge into one draw,
    // but in chunks of this many (sorted along their longest axis) so each
    // merged drawable keeps a tight bounding box and per-cascade frustum
    // culling stays effective. Mirrors the engine's GltfSceneInstance batcher.
    private const int MaxPrimitivesPerBatch = 16;

    private void BuildDrawables(GltfModel model)
    {
        // Opaque/mask primitives are batched by material (shared textures +
        // factors + pipeline + cutout state → one Material, merged VB/IB; the
        // importer bakes transforms into the verts so concatenation + index-
        // rebasing is exact). Blend primitives stay per-primitive — back-to-
        // front sorting (a deferred polish) needs per-prim granularity.
        var opaqueByMaterial = new Dictionary<GltfMaterial, List<GltfPrimitive>>();
        var opaqueOrder = new List<GltfMaterial>();
        var noMaterialPrims = new List<GltfPrimitive>();  // untextured group (gm == null)
        var blendPrims = new List<GltfPrimitive>();
        foreach (var prim in model.Primitives)
        {
            if (prim.Material?.AlphaMode == GltfAlphaMode.Blend)
            {
                blendPrims.Add(prim);
            }
            else if (prim.Material is { } gm)
            {
                if (!opaqueByMaterial.TryGetValue(gm, out var list))
                {
                    list = new List<GltfPrimitive>();
                    opaqueByMaterial[gm] = list;
                    opaqueOrder.Add(gm);
                }
                list.Add(prim);
            }
            else
            {
                noMaterialPrims.Add(prim);
            }
        }

        foreach (var gm in opaqueOrder)
        {
            EmitBatchedGroup(gm, opaqueByMaterial[gm]);
        }
        if (noMaterialPrims.Count > 0)
        {
            EmitBatchedGroup(null, noMaterialPrims);
        }

        foreach (var prim in blendPrims)
        {
            var material = BuildMaterial(prim.Material, out var albedo, out var alphaCutoff, out var baseColorAlpha);
            var pipeline = PickPipeline(GltfAlphaMode.Blend, prim.Material?.DoubleSided ?? false);
            var mesh = prim.Mesh;
            var vb = vk.CreateVertexBuffer(new VertexBufferData(
                new VertexBufferDescription(mesh.Layout, mesh.VertexCount, GraphicsBufferUsage.Static), mesh.VertexBytes),
                "sponza.blend.vb");
            var ib = mesh.Indices32 is { } i32
                ? vk.CreateIndexBuffer(i32, name: "sponza.blend.ib32")
                : vk.CreateIndexBuffer(mesh.Indices, name: "sponza.blend.ib16");
            var indexCount = mesh.Indices32?.Length ?? mesh.Indices.Length;
            blendDrawables.Add(new Drawable(
                vb, ib, indexCount, material, pipeline,
                mesh.Bounds, albedo, alphaCutoff, baseColorAlpha,
                new[] { new ShaderTextureBinding("uAlbedo", albedo, Slot: 0) }));
        }
    }

    // Emit batched opaque/mask drawables for one material group: one shared
    // Material, then spatially-chunked merged VB/IB drawables.
    private void EmitBatchedGroup(GltfMaterial? gm, List<GltfPrimitive> prims)
    {
        var material = BuildMaterial(gm, out var albedo, out var alphaCutoff, out var baseColorAlpha);
        var pipeline = PickPipeline(gm?.AlphaMode ?? GltfAlphaMode.Opaque, gm?.DoubleSided ?? false);
        var shadowBinding = new[] { new ShaderTextureBinding("uAlbedo", albedo, Slot: 0) };
        foreach (var chunk in SpatialChunks(prims))
        {
            var (vbBytes, indices, layout, bounds, vertCount) = MergeChunk(chunk);
            var vb = vk.CreateVertexBuffer(new VertexBufferData(
                new VertexBufferDescription(layout, vertCount, GraphicsBufferUsage.Static), vbBytes),
                "sponza.batch.vb");
            var ib = vk.CreateIndexBuffer(indices, name: "sponza.batch.ib");
            opaqueDrawables.Add(new Drawable(
                vb, ib, indices.Length, material, pipeline,
                bounds, albedo, alphaCutoff, baseColorAlpha, shadowBinding));
        }
    }

    // One engine Material per glTF material. BaseColorFactor / EmissiveFactor /
    // MaterialParams (alphaCutoff, normalScale, roughness, metallic) UBO + the
    // five channel textures (defaults when a channel is absent).
    private MaterialHandle BuildMaterial(
        GltfMaterial? gm, out TextureHandle albedo, out float alphaCutoff, out float baseColorAlpha)
    {
        albedo = GetOrUploadAlbedo(gm);
        var normal = GetOrUploadNormal(gm);
        var emissive = GetOrUploadEmissive(gm);
        var mr = GetOrUploadMr(gm);
        var ao = GetOrUploadAo(gm);

        var baseColorFactor = gm?.BaseColorFactor ?? Vector4.One;
        var emissiveFactor = gm is null ? Vector3.Zero : gm.EmissiveFactor;
        var emissiveStrength = gm?.EmissiveStrength ?? 1.0f;
        alphaCutoff = gm?.AlphaMode == GltfAlphaMode.Mask ? (gm?.AlphaCutoff ?? 0.5f) : 0.0f;
        baseColorAlpha = baseColorFactor.W;
        var normalScale = 1.0f;
        var roughness = gm?.RoughnessFactor ?? 0.8f;
        var metallic = gm?.MetallicFactor ?? 0.0f;

        return vk.CreateMaterial(litProgram, name: "sponza.material")
            .SetUniform(binding: 0, "uBaseColorFactor", baseColorFactor)
            .SetUniform(binding: 0, "uEmissiveFactor",
                new Vector4(emissiveFactor.X, emissiveFactor.Y, emissiveFactor.Z, emissiveStrength))
            .SetUniform(binding: 0, "uMaterialParams",
                new Vector4(alphaCutoff, normalScale, roughness, metallic))
            .SetTexture(binding: 1, albedo)
            .SetTexture(binding: 2, normal)
            .SetTexture(binding: 3, emissive)
            .SetTexture(binding: 4, mr)
            .SetTexture(binding: 5, ao)
            .Handle;
    }

    // Slice primitives into spatially-coherent chunks of <= MaxPrimitivesPerBatch,
    // sorted by centroid along the group's longest axis so each merged batch
    // stays compact (keeps frustum culling useful).
    private static IEnumerable<List<GltfPrimitive>> SpatialChunks(List<GltfPrimitive> prims)
    {
        if (prims.Count <= MaxPrimitivesPerBatch)
        {
            yield return prims;
            yield break;
        }
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var p in prims)
        {
            min = Vector3.Min(min, p.Mesh.Bounds.Min);
            max = Vector3.Max(max, p.Mesh.Bounds.Max);
        }
        var size = max - min;
        var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        var sorted = prims.OrderBy(p => AxisValue(p.Mesh.Bounds.Center, axis)).ToList();
        for (var i = 0; i < sorted.Count; i += MaxPrimitivesPerBatch)
        {
            yield return sorted.GetRange(i, Math.Min(MaxPrimitivesPerBatch, sorted.Count - i));
        }
    }

    private static float AxisValue(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    // Concatenate a chunk's vertex bytes and rebase its indices into one buffer.
    // All members share the vertex layout (48-byte tangent layout here).
    private static (byte[] Vb, uint[] Indices, VertexLayout Layout, Bounds3 Bounds, int VertCount) MergeChunk(
        List<GltfPrimitive> chunk)
    {
        var layout = chunk[0].Mesh.Layout;
        var totalVerts = 0;
        var totalIndices = 0;
        foreach (var p in chunk)
        {
            totalVerts += p.Mesh.VertexCount;
            totalIndices += p.Mesh.Indices32?.Length ?? p.Mesh.Indices.Length;
        }

        var vb = new byte[totalVerts * layout.Stride];
        var indices = new uint[totalIndices];
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        var vByteOffset = 0;
        var indexOffset = 0;
        var vertexBase = 0u;
        foreach (var p in chunk)
        {
            var m = p.Mesh;
            Array.Copy(m.VertexBytes, 0, vb, vByteOffset, m.VertexBytes.Length);
            vByteOffset += m.VertexBytes.Length;
            if (m.Indices32 is { } i32)
            {
                foreach (var idx in i32) indices[indexOffset++] = idx + vertexBase;
            }
            else
            {
                foreach (var idx in m.Indices) indices[indexOffset++] = idx + vertexBase;
            }
            vertexBase += (uint)m.VertexCount;
            min = Vector3.Min(min, m.Bounds.Min);
            max = Vector3.Max(max, m.Bounds.Max);
        }
        return (vb, indices, layout, new Bounds3(min, max), totalVerts);
    }

    private PipelineHandle PickPipeline(GltfAlphaMode mode, bool doubleSided) =>
        (mode, doubleSided) switch
        {
            (GltfAlphaMode.Blend, true)  => blendDoubleSidedPipeline,
            (GltfAlphaMode.Blend, false) => blendSolidPipeline,
            (_, true)                    => opaqueDoubleSidedPipeline,
            _                            => opaqueSolidPipeline,
        };

    // Upload or look up the GPU handle for a glTF material's BaseColorTexture.
    // glTF BaseColor is sRGB-encoded per spec, so we use Rgba8Srgb so the
    // sampler hardware decodes to linear at fetch time. LinearRepeat sampler
    // matches the authored intent of tiled stone/brick textures.
    private TextureHandle GetOrUploadAlbedo(GltfMaterial? material) =>
        UploadOrFallback(material?.BaseColorTexture, albedoCache, fallbackAlbedo,
            TextureFormat.Rgba8Srgb, "albedo");

    private TextureHandle GetOrUploadNormal(GltfMaterial? material) =>
        // Normal maps are LINEAR data (encoded direction vectors), NOT sRGB.
        // Upload as Rgba8 so the sampler hardware doesn't run gamma decode
        // on the (x, y, z) components.
        UploadOrFallback(material?.NormalTexture, normalCache, flatNormal,
            TextureFormat.Rgba8, "normal");

    private TextureHandle GetOrUploadEmissive(GltfMaterial? material) =>
        // Emissive textures are sRGB-encoded per glTF spec.
        UploadOrFallback(material?.EmissiveTexture, emissiveCache, blackEmissive,
            TextureFormat.Rgba8Srgb, "emissive");

    private TextureHandle GetOrUploadMr(GltfMaterial? material) =>
        // Metallic-roughness — LINEAR encoded. G = roughness, B = metallic.
        // R may carry AO when the asset packs ORM into one texture; in
        // Sponza Modern's case R is empty (zero) and AO ships separately
        // via material.OcclusionTexture. The shader reads ao from the AO
        // sampler (binding 5), not from MR.R, so this empty R is harmless.
        UploadOrFallback(material?.MetallicRoughnessTexture, mrCache, defaultMr,
            TextureFormat.Rgba8, "mr");

    private TextureHandle GetOrUploadAo(GltfMaterial? material) =>
        // glTF OcclusionTexture: R channel = ambient occlusion (linear).
        // Default-1.0 texture means missing-AO materials get no extra
        // attenuation.
        UploadOrFallback(material?.OcclusionTexture, aoCache, defaultAo,
            TextureFormat.Rgba8, "ao");

    private TextureHandle UploadOrFallback(
        GltfTexture? tex,
        Dictionary<GltfTexture, TextureHandle> cache,
        TextureHandle fallback,
        TextureFormat uploadFormat,
        string channelTag)
    {
        if (tex is null) return fallback;
        if (cache.TryGetValue(tex, out var cached)) return cached;
        if (tex.MipBytes is not { Count: > 0 } mips || tex.Format != TextureFormat.Rgba8)
        {
            // Cooked-.blixtex compressed path or already-released CPU bytes
            // — not handled by this scaffold.
            cache[tex] = fallback;
            return fallback;
        }
        var handle = vk.CreateTexture2D(
            new TextureDescription(tex.Width, tex.Height, uploadFormat, SamplerDescription.LinearRepeat),
            mips[0],
            $"sponza.{channelTag}.{tex.Name}");
        cache[tex] = handle;
        return handle;
    }

    private static TextureHandle CreateFallbackAlbedoTexture(VulkanGraphicsDevice vk)
    {
        // 1×1 white. Untextured materials (glass / light_bulb / lamp_glass_01
        // in Sponza, ~76 primitives) carry their colour in BaseColorFactor;
        // the shader multiplies sampled albedo × baseColorFactor, so white
        // here means the factor is the colour.
        return vk.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            new byte[] { 255, 255, 255, 255 }, "sponza.default.albedo");
    }

    public void OnUpdate(Time time)
    {
        var dt = (float)time.Delta;

        // Translation: WASD + Space/Ctrl. Sprint via Cmd/Super (engine's Key
        // enum has no Shift).
        var move = Vector3.Zero;
        if (heldKeys.Contains(Key.W)) move += cameraForward;
        if (heldKeys.Contains(Key.S)) move -= cameraForward;
        var right = Vector3.Normalize(Vector3.Cross(cameraForward, Vector3.UnitY));
        if (heldKeys.Contains(Key.D)) move += right;
        if (heldKeys.Contains(Key.A)) move -= right;
        if (heldKeys.Contains(Key.Space)) move += Vector3.UnitY;
        if (heldKeys.Contains(Key.LeftControl)) move -= Vector3.UnitY;
        var sprint = heldKeys.Contains(Key.LeftSuper) || heldKeys.Contains(Key.RightSuper);
        var speed = sprint ? moveSpeed * 3f : moveSpeed;
        if (move != Vector3.Zero)
        {
            cameraPosition += Vector3.Normalize(move) * speed * dt;
        }

        // Rotation: arrow keys (keyboard look — WASD already handles movement).
        // Left/Right yaw, Up/Down pitch; matches the mouse-look sign convention.
        const float lookSpeed = 1.8f; // rad/s
        var look = lookSpeed * dt;
        if (heldKeys.Contains(Key.Left))  camYaw -= look;
        if (heldKeys.Contains(Key.Right)) camYaw += look;
        if (heldKeys.Contains(Key.Up))    camPitch += look;
        if (heldKeys.Contains(Key.Down))  camPitch -= look;
        var pitchLimit = MathF.PI / 2f - 0.01f;
        camPitch = Math.Clamp(camPitch, -pitchLimit, pitchLimit);

        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var cp = MathF.Cos(camPitch);
        cameraForward = Vector3.Normalize(new Vector3(
            cp * MathF.Sin(camYaw),
            MathF.Sin(camPitch),
            -cp * MathF.Cos(camYaw)));
        var view = Matrix4x4.CreateLookAt(cameraPosition, cameraPosition + cameraForward, Vector3.UnitY);
        // Sponza atrium spans tens of metres; far plane needs to be generous.
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(fovYRadians, aspect, 0.1f, 200f);
        viewProj = view * proj;
        UpdateCascades();
    }

    // Recompute the sun travel direction from the overlay-driven yaw/pitch.
    private void UpdateSunDirection()
    {
        var cp = MathF.Cos(sunPitch);
        sunDirection = Vector3.Normalize(new Vector3(
            cp * MathF.Sin(sunYaw),
            MathF.Sin(sunPitch),
            -cp * MathF.Cos(sunYaw)));
    }

    // Refit the cascade light view-projections to the current camera. Each
    // cascade bounds a slice [Splits[c], Splits[c+1]] of the camera frustum:
    // we take the slice's 8 corners, wrap them in a bounding sphere (so the
    // ortho is rotation-invariant), aim the light at the sphere centre, and
    // snap that centre to the shadow-map texel grid so shadows don't swim as
    // the camera moves. Ported from the GL SponzaModern reference, expressed
    // in Vulkan depth-[0,1] conventions (CreateOrthoVulkan + System.Numerics
    // right-handed CreateLookAt, same matrix-multiply order as the camera's
    // viewProj = view * proj).
    private void UpdateCascades()
    {
        var fwd = cameraForward;
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
        var camUp = Vector3.Cross(right, fwd);
        var tanV = MathF.Tan(fovYRadians * 0.5f);
        var tanH = tanV * aspect;

        // Light travel direction; place the eye opposite it, behind the slab.
        var L = Vector3.Normalize(sunDirection);
        var sunUp = MathF.Abs(L.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

        Span<Vector3> corners = stackalloc Vector3[8];
        for (var c = 0; c < CascadeCount; c++)
        {
            var nearD = cascadeSplits[c];
            var farD = cascadeSplits[c + 1];
            var k = 0;
            for (var di = 0; di < 2; di++)
            {
                var d = di == 0 ? nearD : farD;
                var centre = cameraPosition + fwd * d;
                var hh = d * tanV;
                var hw = d * tanH;
                corners[k++] = centre - right * hw - camUp * hh;
                corners[k++] = centre + right * hw - camUp * hh;
                corners[k++] = centre - right * hw + camUp * hh;
                corners[k++] = centre + right * hw + camUp * hh;
            }

            // Bounding sphere of the slice corners.
            var center = Vector3.Zero;
            for (var i = 0; i < 8; i++) center += corners[i];
            center /= 8f;
            var radius = 0f;
            for (var i = 0; i < 8; i++) radius = MathF.Max(radius, Vector3.Distance(corners[i], center));
            radius = MathF.Ceiling(radius);

            // Texel-snap the sphere centre in light space so the ortho footprint
            // lands on a stable grid (kills the shimmer under camera motion).
            var eye = center - L * (shadowSunDistance + radius);
            var lightView = Matrix4x4.CreateLookAt(eye, center, sunUp);
            var texelSize = (2f * radius) / ShadowMapSize;
            var centreLight = Vector3.Transform(center, lightView);
            centreLight.X = MathF.Round(centreLight.X / texelSize) * texelSize;
            centreLight.Y = MathF.Round(centreLight.Y / texelSize) * texelSize;
            Matrix4x4.Invert(lightView, out var invLightView);
            var snapped = Vector3.Transform(centreLight, invLightView);

            var eye2 = snapped - L * (shadowSunDistance + radius);
            var lightView2 = Matrix4x4.CreateLookAt(eye2, snapped, sunUp);
            var farPlane = 2f * (shadowSunDistance + radius);
            var ortho = CreateOrthoVulkan(2f * radius, 2f * radius, 0.1f, farPlane);
            cascadeViewProj[c] = lightView2 * ortho;

            // Base depth bias = BiasTexels shadow-texels of world offset,
            // converted to this cascade's NDC depth units (ortho z is linear,
            // so world→NDC depth scale is 1/farPlane). Keeps the bias visually
            // constant across cascades despite their very different extents.
            cascadeDepthBias[c] = (biasTexels * texelSize) / farPlane;
        }
    }

    // Symmetric orthographic projection for Vulkan clip space: x/y in [-1,1],
    // z in [0,1], Y flipped (matches VulkanLit's shadow ortho + CreatePerspectiveVulkan).
    private static Matrix4x4 CreateOrthoVulkan(float width, float height, float near, float far)
    {
        var fn = far - near;
        return new Matrix4x4(
            2f / width, 0f,          0f,         0f,
            0f,        -2f / height, 0f,         0f,
            0f,         0f,         -1f / fn,    0f,
            0f,         0f,         -near / fn,  1f);
    }

    public void OnResize(int width, int height)
    {
        if (height > 0)
        {
            aspect = width / (float)height;
            UpdateCamera();
        }
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        if (!sceneLoaded)
        {
            // Nothing to draw yet. The graph still needs an Execute so the
            // swapchain image transitions and the present pass clears.
            graph.Pass(litPassHandle, _ => { },
                clearColor: new GraphicsColor(0.05f, 0.07f, 0.10f, 1f));
            graph.Execute(commandList);
            RecordPresentPass(commandList);
            return;
        }

        // Stress hook: flip fog every 90 frames to exercise the on/off barrier
        // transitions under validation (no effect without --fog-stress).
        if (fogStress && (++fogStressFrame % 90 == 0)) fogEnabled = !fogEnabled;

        var perFrame = new ShaderUniform[]
        {
            new("uViewProjection",   new Matrix4x4Uniform(viewProj)),
            new("uSunDirection",     new Vector3Uniform(sunDirection)),
            new("uSunIntensity",     new FloatUniform(sunIntensity)),
            new("uAmbientColor",     new Vector3Uniform(ambientColor)),
            new("uIblIntensity",     new FloatUniform(ambientIntensity)),
            new("uCameraPos",        new Vector3Uniform(cameraPosition)),
            new("uEnvMipCount",      new FloatUniform(iblPrefilterMips)),
            new("uCameraForward",    new Vector3Uniform(cameraForward)),
            new("uShadowStrength",   new FloatUniform(shadowsEnabled ? 1f : 0f)),
            new("uCascadeViewProj",  new Matrix4x4ArrayUniform(cascadeViewProj)),
            // .xyz = far view-depth bound of cascade 0,1,2 (Splits[1..3]).
            new("uCascadeSplits",    new Vector4Uniform(
                new Vector4(cascadeSplits[1], cascadeSplits[2], cascadeSplits[3], 0f))),
            new("uShadowParams",     new Vector4Uniform(
                new Vector4(visualizeCascades ? 1f : 0f, 0f, 0f, 0f))),
            new("uCascadeBias",      new Vector4Uniform(
                new Vector4(cascadeDepthBias[0], cascadeDepthBias[1], cascadeDepthBias[2], 0f))),
            new("uShaderParams",     new Vector4Uniform(
                new Vector4(metallicThreshold, normalStrength, slopeScale, 0f))),
            new("uFog",              new Vector4Uniform(
                new Vector4(frame.Width, frame.Height, fogFar, fogEnabled ? 1f : 0f))),
        };

        // Per-pass set-1 bindings (passBindings) and the identity model push
        // are built once at load (constant handles / identity transform) and
        // reused here — see OnLoad.

        // --- Shadow passes: opaque/mask occluders into each cascade ---------
        // Each cascade frustum-culls the opaque set against its ortho box, so
        // the near cascade only redraws nearby geometry instead of the whole
        // scene ×3. Blend drawables (windows) are skipped — translucent
        // surfaces shouldn't cast solid shadows. Casters route by alpha mode:
        // OPAQUE → push-only pipeline (no descriptor set); MASK → alpha-cutout
        // pipeline binding the albedo, so foliage casts leaf-shaped shadows.
        var cull = cullEnabled;
        var margin = cullMargin;
        // Reset the mask-push pool cursor; the cascade scopes rent monotonically
        // during graph.Execute, so each draw this frame gets a distinct buffer.
        maskPushCursor = 0;
        for (var c = 0; c < CascadeCount; c++)
        {
            var ci = c;
            // Shadows off: skip the pass (the lit shader early-outs without
            // sampling) and invalidate the cache so re-enabling forces a redraw.
            if (!shadowsEnabled)
            {
                cachedCascadeViewProj[ci] = default;
                cascadeRendered[ci] = false;
                continue;
            }
            var vp = cascadeViewProj[ci];
            // Cached: this cascade's VP is unchanged since it was last rendered,
            // so its depth target still holds the right result — skip the pass
            // entirely (no begin-render-pass → contents persist in ShaderReadOnly,
            // which is exactly what the lit pass samples).
            if (vp == cachedCascadeViewProj[ci])
            {
                cascadeRendered[ci] = false;
                continue;
            }
            cachedCascadeViewProj[ci] = vp;
            cascadeRendered[ci] = true;
            // Frustum.FromViewProjection expects a column-vector clip matrix
            // (clip = M·world); our cascade VP is the System.Numerics
            // row-vector form (clip = Vector4.Transform(world, M)), so transpose
            // to hand it the clip-coordinate generators as rows.
            var cascadeFrustum = Frustum.FromViewProjection(Matrix4x4.Transpose(vp));
            // Every opaque caster in this cascade pushes the same bytes (identity
            // model + this cascade's VP), so build it once per cascade instead of
            // allocating a byte[] per caster — the dominant per-frame garbage and
            // the source of the GC build-command spikes. Safe to share: DrawIndexed
            // holds the push by reference, but all these draws want identical
            // content. Mask casters still push per-draw (alpha params differ).
            var cascadeOpaquePush = ShadowOpaquePushBytes(Matrix4x4.Identity, vp);
            graph.Pass(cascadePassHandles[ci], scope =>
            {
                var drawn = 0;
                foreach (var d in opaqueDrawables)
                {
                    if (cull && !cascadeFrustum.Intersects(d.Bounds, margin)) continue;
                    if (d.AlphaCutoff > 0f)
                    {
                        scope.DrawIndexed(
                            vertexBuffer: d.Vb,
                            indexBuffer: d.Ib,
                            pipeline: shadowMaskPipeline,
                            indexCount: d.IndexCount,
                            uniforms: Array.Empty<ShaderUniform>(),
                            textures: d.ShadowAlbedoBinding,
                            pushConstants: RentMaskPush(Matrix4x4.Identity, vp, d.AlphaCutoff, d.BaseColorAlpha));
                    }
                    else
                    {
                        scope.DrawIndexed(
                            vertexBuffer: d.Vb,
                            indexBuffer: d.Ib,
                            pipeline: shadowOpaquePipeline,
                            indexCount: d.IndexCount,
                            uniforms: Array.Empty<ShaderUniform>(),
                            textures: Array.Empty<ShaderTextureBinding>(),
                            pushConstants: cascadeOpaquePush);
                    }
                    drawn++;
                }
                cascadeDrawCounts[ci] = drawn;
            });
        }

        // Froxel fog: fill the 3D scattering grid. Recorded here — after the
        // shadow scopes, before the lit scope — so this code reads in frame
        // order. (Execution is by graph declaration order regardless: the
        // froxel ComputePass is declared between the cascades and the lit pass,
        // so it dispatches after the shadow maps render and before the lit pass
        // samples the grid.) Skipped when fog is off — and the lit shader gates
        // on uFog.w, so when disabled the grid is never sampled (its contents
        // are undefined/stale; "disabled" must mean "never sample").
        if (fogEnabled)
        {
            Matrix4x4.Invert(viewProj, out var invViewProj);
            var froxelUniforms = new ShaderUniform[]
            {
                new("uInvViewProj",   new Matrix4x4Uniform(invViewProj)),
                new("uCamPos",        new Vector4Uniform(new Vector4(cameraPosition, fogFar))),
                new("uCamForward",    new Vector4Uniform(new Vector4(cameraForward, fogDensity))),
                new("uSunDir",        new Vector4Uniform(new Vector4(sunDirection, sunIntensity))),
                new("uSunColor",      new Vector4Uniform(new Vector4(1f, 1f, 1f, fogScatter))),
                new("uFogParams",     new Vector4Uniform(new Vector4(fogPhaseG, fogAmbient, 0f, 0f))),
                new("uCascadeVP",     new Matrix4x4ArrayUniform(cascadeViewProj)),
                new("uCascadeSplits", new Vector4Uniform(
                    new Vector4(cascadeSplits[1], cascadeSplits[2], cascadeSplits[3], 0f))),
            };
            graph.Dispatch(froxelPassHandle, new DispatchCommand(
                froxelPipeline,
                (FroxelGridX + 7) / 8, (FroxelGridY + 7) / 8, 1,
                froxelUniforms, froxelBindings));
        }

        graph.Pass(litPassHandle, scope =>
        {
            // Opaque + Mask first (depth-write enabled), then Blend
            // (depth-test only), so translucent surfaces composite over
            // the resolved opaque depth without writing into it.
            foreach (var d in opaqueDrawables)
            {
                scope.DrawIndexed(
                    vertexBuffer: d.Vb,
                    indexBuffer: d.Ib,
                    pipeline: d.Pipeline,
                    indexCount: d.IndexCount,
                    uniforms: perFrame,
                    textures: passBindings,
                    material: d.Material,
                    pushConstants: identityPush);
            }
            // Sky after opaque, before blend. Fullscreen triangle drawn
            // from the dummy VB; positions are synthesised in skybox.vert.
            scope.DrawIndexed(
                vertexBuffer: presentDummyVB,
                indexBuffer: presentDummyIB,
                pipeline: skyPipeline,
                indexCount: 3,
                uniforms: perFrame,
                textures: passBindings);
            foreach (var d in blendDrawables)
            {
                scope.DrawIndexed(
                    vertexBuffer: d.Vb,
                    indexBuffer: d.Ib,
                    pipeline: d.Pipeline,
                    indexCount: d.IndexCount,
                    uniforms: perFrame,
                    textures: passBindings,
                    material: d.Material,
                    pushConstants: identityPush);
            }
        }, clearColor: new GraphicsColor(0.05f, 0.07f, 0.10f, 1f));

        graph.Execute(commandList);

        RecordPresentPass(commandList);
    }

    // --- IDebuggable ------------------------------------------------------
    // Replaces the old Console cull-count log + hardcoded C/L key toggles.
    // Controls are read-back: the returned value feeds this frame's render.
    public void Debug(DebugContext debug)
    {
        // Cmd+C toggles this; Debug() runs unconditionally so it re-applies.
        debug.State.Enabled = overlayEnabled;
        // Overlay off → emit nothing. The panels gate on State.Enabled, but the
        // debug-line pass just renders whatever's queued, so we must skip the
        // Draw emissions too or the cascade/sun gizmos linger when hidden.
        if (!overlayEnabled) return;

        // Live tuning, grouped by scope. Controls are read-back: the returned
        // value feeds this frame's render (Debug() runs before OnRender).
        using (debug.Scope("Sun"))
        {
            var deg = 180f / MathF.PI;
            sunYaw   = debug.Controls.Float("Yaw (deg)", sunYaw * deg, -180f, 180f) / deg;
            sunPitch = debug.Controls.Float("Pitch (deg)", sunPitch * deg, -89f, -1f) / deg;
            UpdateSunDirection();
            sunIntensity     = debug.Controls.Float("Sun intensity", sunIntensity, 0f, 8f);
            ambientIntensity = debug.Controls.Float("Ambient (IBL)", ambientIntensity, 0f, 4f);
        }
        using (debug.Scope("Shadows"))
        {
            shadowsEnabled    = debug.Controls.Toggle("Sun shadows", shadowsEnabled);
            visualizeCascades = debug.Controls.Toggle("Visualize cascades", visualizeCascades);
            biasTexels        = debug.Controls.Float("Bias (texels)", biasTexels, 0f, 6f);
            slopeScale        = debug.Controls.Float("Bias slope scale", slopeScale, 0f, 12f);
            shadowSunDistance = debug.Controls.Float("Sun distance", shadowSunDistance, 10f, 120f);
        }
        using (debug.Scope("Cascades"))
        {
            cullEnabled   = debug.Controls.Toggle("Frustum cull", cullEnabled);
            cullMargin    = debug.Controls.Float("Cull margin", cullMargin, 0f, 5f);
            // Far distance of each cascade; clamped into ascending-ish bands so
            // the splits stay ordered as you drag.
            cascadeSplits[1] = debug.Controls.Float("Far: cascade 0", cascadeSplits[1], 1f, 20f);
            cascadeSplits[2] = debug.Controls.Float("Far: cascade 1", cascadeSplits[2], cascadeSplits[1], 50f);
            cascadeSplits[3] = debug.Controls.Float("Far: cascade 2", cascadeSplits[3], cascadeSplits[2], 150f);
        }
        using (debug.Scope("Fog"))
        {
            fogEnabled = debug.Controls.Toggle("Volumetric fog", fogEnabled);
            fogDensity = debug.Controls.Float("Density", fogDensity, 0f, 0.5f);
            fogScatter = debug.Controls.Float("Scatter albedo", fogScatter, 0f, 1f);
            fogPhaseG  = debug.Controls.Float("Phase g", fogPhaseG, -0.9f, 0.9f);
            fogAmbient = debug.Controls.Float("Ambient scatter", fogAmbient, 0f, 0.2f);
            fogFar     = debug.Controls.Float("Far distance", fogFar, 10f, 150f);
        }
        using (debug.Scope("Material"))
        {
            metallicThreshold = debug.Controls.Float("Metallic threshold", metallicThreshold, 0f, 1f);
            normalStrength    = debug.Controls.Float("Normal-map strength", normalStrength, 0f, 2f);
        }
        using (debug.Scope("Render"))
        {
            exposure   = debug.Controls.Float("Exposure", exposure, 0.05f, 16f);
            tonemapMode = debug.Controls.Enum("Tonemap", tonemapMode, TonemapNames);
            moveSpeed  = debug.Controls.Float("Fly speed", moveSpeed, 0.3f, 60f);
        }

        debug.Values.Value("shadow-map", $"{ShadowMapSize}²×{CascadeCount}");
        debug.Values.Value("splits-m", $"{cascadeSplits[1]:0}/{cascadeSplits[2]:0}/{cascadeSplits[3]:0}");
        // Per-cascade caster counts after frustum cull (one frame stale — set
        // during the previous OnRender's graph.Execute).
        debug.Values.Value("cascade-casters", $"{cascadeDrawCounts[0]}/{cascadeDrawCounts[1]}/{cascadeDrawCounts[2]} of {opaqueDrawables.Count}");
        // Shadow-map cache hits: R = re-rendered this frame, · = served cached.
        debug.Values.Value("cascade-cache", $"{(cascadeRendered[0] ? 'R' : '·')}{(cascadeRendered[1] ? 'R' : '·')}{(cascadeRendered[2] ? 'R' : '·')}");
        debug.Values.Value("blend-draws", blendDrawables.Count);
        debug.Values.Value("cam-pos", cameraPosition);

        // Spatial gizmos: sun direction + the three cascade ortho boxes.
        debug.Draw.ViewProjection = viewProj;
        debug.Draw.Arrow("sun/dir", -sunDirection * 6f, Vector3.Zero,
            new GraphicsColor(1f, 0.92f, 0.3f, 1f));
        var cascadeTints = new[]
        {
            new GraphicsColor(1f, 0.35f, 0.35f, 0.8f),
            new GraphicsColor(0.35f, 1f, 0.35f, 0.8f),
            new GraphicsColor(0.4f, 0.5f, 1f, 0.8f),
        };
        for (var c = 0; c < CascadeCount; c++)
        {
            debug.Draw.Frustum($"cascade/{c}", cascadeViewProj[c], cascadeTints[c]);
        }
    }

    private void RecordPresentPass(RenderCommandList commandList)
    {
        var hdrTex = graph.GetColorTexture(hdrHandle);
        var push = new byte[8];
        MemoryMarshal.Write(push.AsSpan(0, 4), in exposure);
        var tonemap = (uint)tonemapMode;
        MemoryMarshal.Write(push.AsSpan(4, 4), in tonemap);
        commandList.Pass(
            "present",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0, 0, 0, 1) },
                ClearDepth: true),
            pass =>
            {
                pass.DrawIndexed(
                    vertexBuffer: presentDummyVB,
                    indexBuffer: presentDummyIB,
                    pipeline: presentPipeline,
                    indexCount: 3,
                    uniforms: Array.Empty<ShaderUniform>(),
                    textures: new[]
                    {
                        new ShaderTextureBinding("uHdr", hdrTex, Slot: 0),
                    },
                    pushConstants: push);
            });
    }

    private static byte[] ModelPushBytes(Matrix4x4 model)
    {
        var bytes = new byte[64];
        MemoryMarshal.Write(bytes.AsSpan(0, 64), in model);
        return bytes;
    }

    // Opaque shadow caster push: [model | cascadeViewProj] = 128 bytes,
    // matching shadow.vert's PushConstants block.
    private static byte[] ShadowOpaquePushBytes(Matrix4x4 model, Matrix4x4 cascadeViewProj)
    {
        var bytes = new byte[128];
        MemoryMarshal.Write(bytes.AsSpan(0, 64), in model);
        MemoryMarshal.Write(bytes.AsSpan(64, 64), in cascadeViewProj);
        return bytes;
    }

    // Mask shadow caster push: [model | cascadeViewProj | alphaParams] = 144
    // bytes, matching shadow_mask's PushConstants block. alphaParams.xy =
    // (alphaCutoff, baseColorAlpha). Rents a pooled buffer (reset per frame via
    // maskPushCursor) rather than allocating, since these differ per draw.
    private byte[] RentMaskPush(
        Matrix4x4 model, Matrix4x4 cascadeViewProj, float alphaCutoff, float baseColorAlpha)
    {
        if (maskPushCursor >= maskPushPool.Count) maskPushPool.Add(new byte[144]);
        var bytes = maskPushPool[maskPushCursor++];
        MemoryMarshal.Write(bytes.AsSpan(0, 64), in model);
        MemoryMarshal.Write(bytes.AsSpan(64, 64), in cascadeViewProj);
        var alphaParams = new Vector4(alphaCutoff, baseColorAlpha, 0f, 0f);
        MemoryMarshal.Write(bytes.AsSpan(128, 16), in alphaParams);
        return bytes;
    }

    // --- IInputHandler ----------------------------------------------------

    public void OnKeyDown(Key key)
    {
        heldKeys.Add(key);
        switch (key)
        {
            // Cmd+C toggles the diagnostics overlay. Plain C does nothing.
            case Key.C:
                if (heldKeys.Contains(Key.LeftSuper) || heldKeys.Contains(Key.RightSuper))
                {
                    overlayEnabled = !overlayEnabled;
                }
                break;
            // Exposure / sun / shadow tuning live in the overlay Controls now;
            // arrow keys drive the camera (see OnUpdate).
            case Key.Escape:
                host.RequestClose();
                break;
        }
    }

    public void OnKeyUp(Key key) => heldKeys.Remove(key);

    public void OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        if (!mouseLook) return;
        const float sensitivity = 0.0035f;
        camYaw += deltaX * sensitivity;
        camPitch -= deltaY * sensitivity;
        var limit = MathF.PI / 2f - 0.01f;
        camPitch = Math.Clamp(camPitch, -limit, limit);
        UpdateCamera();
    }

    public void OnMouseDown(MouseButton button)
    {
        if (button == MouseButton.Right)
        {
            mouseLook = true;
            host.SetCursorCaptured(true);
        }
    }

    public void OnMouseUp(MouseButton button)
    {
        if (button == MouseButton.Right)
        {
            mouseLook = false;
            host.SetCursorCaptured(false);
        }
    }

    public void OnMouseWheel(float offsetX, float offsetY)
    {
        moveSpeed = Math.Clamp(moveSpeed * (offsetY > 0 ? 1.25f : 0.8f), 0.3f, 60f);
    }

    public void Dispose() { }

    // Procedural-sky IBL fallback: the analytic-sky env cube (specular
    // stand-in), cosine irradiance cube, and split-sum BRDF LUT. Used when no
    // cooked .blixprobe is present. iblPrefilterMips stays EnvMips here.
    private void BakeProceduralIbl()
    {
        envCubeTexture = vk.CreateTextureCube(
            EnvFaceSize, TextureFormat.Rgba8, EnvMips,
            BuildEnvCubeWithMips(EnvFaceSize, EnvMips),
            SamplerDescription.LinearClamp, "sponza.ibl.env");
        irradianceCubeTexture = vk.CreateTextureCube(
            IrradianceFaceSize, TextureFormat.Rgba8, 1,
            BuildIrradianceCube(IrradianceFaceSize),
            SamplerDescription.LinearClamp, "sponza.ibl.irradiance");
        brdfLutTexture = vk.CreateTexture2D(
            new TextureDescription(BrdfLutSize, BrdfLutSize, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            BuildBrdfLut(BrdfLutSize), "sponza.ibl.brdfLut");
        iblPrefilterMips = EnvMips;
    }

    // ===================================================================
    // IBL bake helpers. Pure CPU math; runs once at OnLoad. Adapted from
    // the equivalent helpers in Blix.Demos.VulkanLit.Program — same sky
    // model, same split-sum integration, same cube convention. Tuned for
    // a daylight Sponza palette (warm horizon, blue zenith, soft sun glow).
    // ===================================================================

    // Analytic sky radiance (linear) for a world direction. Zenith→horizon
    // gradient, dim ground below, plus a soft warm glow toward the sun.
    private static Vector3 SkyColor(Vector3 d)
    {
        d = Vector3.Normalize(d);
        var zenith  = new Vector3(0.22f, 0.42f, 0.82f);
        var horizon = new Vector3(0.70f, 0.78f, 0.90f);
        var ground  = new Vector3(0.16f, 0.15f, 0.14f);
        Vector3 baseCol;
        if (d.Y >= 0f)
        {
            var k = MathF.Pow(Math.Clamp(d.Y, 0f, 1f), 0.5f);
            baseCol = Vector3.Lerp(horizon, zenith, k);
        }
        else
        {
            var k = Math.Clamp(-d.Y, 0f, 1f);
            baseCol = Vector3.Lerp(horizon, ground, k);
        }
        // Sun glow comes FROM the opposite of the sun direction. Uses the
        // bake-time constant so live sun changes don't invalidate the IBL.
        var toSun = -SkyBakeSunDirection;
        var glow = MathF.Pow(MathF.Max(Vector3.Dot(d, toSun), 0f), 32f);
        baseCol += new Vector3(0.5f, 0.42f, 0.30f) * glow;
        return Vector3.Clamp(baseCol, Vector3.Zero, Vector3.One);
    }

    // Cubemap face (u,v)∈[-1,1] → world direction. Canonical Vulkan/GL cube
    // convention; generation and samplerCube lookup agree.
    private static Vector3 CubeDir(int face, float u, float v) => face switch
    {
        0 => new Vector3( 1f, -v, -u),   // +X
        1 => new Vector3(-1f, -v,  u),   // -X
        2 => new Vector3( u,  1f,  v),   // +Y
        3 => new Vector3( u, -1f, -v),   // -Y
        4 => new Vector3( u, -v,  1f),   // +Z
        _ => new Vector3(-u, -v, -1f),   // -Z
    };

    private static byte LinByte(float c) => (byte)(Math.Clamp(c, 0f, 1f) * 255f + 0.5f);

    private static void WriteEnvFace(byte[] dst, int offset, int face, int size)
    {
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var u = 2f * ((x + 0.5f) / size) - 1f;
            var v = 2f * ((y + 0.5f) / size) - 1f;
            var c = SkyColor(CubeDir(face, u, v));
            var i = offset + (y * size + x) * 4;
            dst[i] = LinByte(c.X); dst[i + 1] = LinByte(c.Y); dst[i + 2] = LinByte(c.Z); dst[i + 3] = 255;
        }
    }

    // Env cube, face-major then mip-major. Each mip re-evaluates the
    // analytic sky at that resolution (cleaner than box-downsampling for
    // a smooth sky). Mip chain stands in for prefiltered-specular at
    // increasing roughness; rougher surfaces fetch coarser mips.
    private static byte[] BuildEnvCubeWithMips(int faceSize, int mips)
    {
        long total = 0;
        for (var f = 0; f < 6; f++)
            for (var m = 0; m < mips; m++) { var s = faceSize >> m; total += s * s * 4; }
        var data = new byte[total];
        var offset = 0;
        for (var face = 0; face < 6; face++)
        for (var m = 0; m < mips; m++)
        {
            var s = faceSize >> m;
            WriteEnvFace(data, offset, face, s);
            offset += s * s * 4;
        }
        return data;
    }

    // Diffuse irradiance cube: for each output direction N, cosine-weighted
    // average of the sky over the hemisphere around N.
    private static byte[] BuildIrradianceCube(int faceSize)
    {
        var data = new byte[6 * faceSize * faceSize * 4];
        var faceBytes = faceSize * faceSize * 4;
        for (var face = 0; face < 6; face++)
        for (var y = 0; y < faceSize; y++)
        for (var x = 0; x < faceSize; x++)
        {
            var u = 2f * ((x + 0.5f) / faceSize) - 1f;
            var v = 2f * ((y + 0.5f) / faceSize) - 1f;
            var N = Vector3.Normalize(CubeDir(face, u, v));
            var up = MathF.Abs(N.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
            var tangent = Vector3.Normalize(Vector3.Cross(up, N));
            var bitangent = Vector3.Cross(N, tangent);

            var sum = Vector3.Zero;
            var weight = 0f;
            const int phiSteps = 24;
            const int thetaSteps = 12;
            for (var pi = 0; pi < phiSteps; pi++)
            for (var ti = 0; ti < thetaSteps; ti++)
            {
                var phi = 2f * MathF.PI * (pi + 0.5f) / phiSteps;
                var theta = 0.5f * MathF.PI * (ti + 0.5f) / thetaSteps;
                var st = MathF.Sin(theta);
                var local = new Vector3(st * MathF.Cos(phi), st * MathF.Sin(phi), MathF.Cos(theta));
                var dir = local.X * tangent + local.Y * bitangent + local.Z * N;
                var w = MathF.Cos(theta) * MathF.Sin(theta);
                sum += SkyColor(dir) * w;
                weight += w;
            }
            var irr = sum / MathF.Max(weight, 1e-4f);
            var idx = face * faceBytes + (y * faceSize + x) * 4;
            data[idx] = LinByte(irr.X); data[idx + 1] = LinByte(irr.Y); data[idx + 2] = LinByte(irr.Z); data[idx + 3] = 255;
        }
        return data;
    }

    // Split-sum BRDF integration LUT. R = scale on F0, G = bias. Encoded
    // in R/G channels of an Rgba8 texture.
    private static byte[] BuildBrdfLut(int size)
    {
        var data = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var NdotV = (x + 0.5f) / size;
            var roughness = (y + 0.5f) / size;
            var (a, b) = IntegrateBrdf(NdotV, roughness);
            var i = (y * size + x) * 4;
            data[i] = LinByte(a); data[i + 1] = LinByte(b); data[i + 2] = 0; data[i + 3] = 255;
        }
        return data;
    }

    private static (float A, float B) IntegrateBrdf(float NdotV, float roughness)
    {
        var V = new Vector3(MathF.Sqrt(1f - NdotV * NdotV), 0f, NdotV);
        float a = 0f, b = 0f;
        const int samples = 256;
        var N = new Vector3(0, 0, 1);
        for (var i = 0; i < samples; i++)
        {
            var xi = Hammersley(i, samples);
            var H = ImportanceSampleGgx(xi, roughness, N);
            var L = Vector3.Normalize(2f * Vector3.Dot(V, H) * H - V);
            var NdotL = MathF.Max(L.Z, 0f);
            var NdotH = MathF.Max(H.Z, 0f);
            var VdotH = MathF.Max(Vector3.Dot(V, H), 0f);
            if (NdotL > 0f)
            {
                var g = GeometrySmithIbl(NdotV, NdotL, roughness);
                var gVis = g * VdotH / MathF.Max(NdotH * NdotV, 1e-5f);
                var fc = MathF.Pow(1f - VdotH, 5f);
                a += (1f - fc) * gVis;
                b += fc * gVis;
            }
        }
        return (a / samples, b / samples);
    }

    private static Vector2 Hammersley(int i, int n)
    {
        uint bits = (uint)i;
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
        var rdi = bits * 2.3283064365386963e-10f;
        return new Vector2(i / (float)n, rdi);
    }

    private static Vector3 ImportanceSampleGgx(Vector2 xi, float roughness, Vector3 _)
    {
        var a = roughness * roughness;
        var phi = 2f * MathF.PI * xi.X;
        var cosT = MathF.Sqrt((1f - xi.Y) / (1f + (a * a - 1f) * xi.Y));
        var sinT = MathF.Sqrt(1f - cosT * cosT);
        return new Vector3(MathF.Cos(phi) * sinT, MathF.Sin(phi) * sinT, cosT);
    }

    private static float GeometrySmithIbl(float NdotV, float NdotL, float roughness)
    {
        var k = (roughness * roughness) / 2f;
        float GeomG(float c) => c / (c * (1f - k) + k);
        return GeomG(NdotV) * GeomG(NdotL);
    }
}
