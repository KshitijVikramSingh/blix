# Renderer

The renderer is split into three layers below the game code:

- **`Blix.Render`** — the engine-facing API. Game code talks to `Mesh`, `Material`, `MaterialResolver`, `SpriteBatch`, `Font`, `DebugDraw`. Low-level GL handles never leak into draw sites.
- **`Blix.Graphics`** — the graphics command language. Typed handles (`PipelineHandle`, `VertexBufferHandle`, etc.), pipeline state, render surfaces, render passes, vertex layouts, shader sources, the GLSL include preprocessor.
- **`Blix.Graphics.OpenGL`** — the OpenGL backend. Owns context lifetime, shader compilation, command execution, framebuffer setup, resource registry.

Plus two helpers and the runtime adapter:
- **`Blix.Graphics.Images`** — PNG/JPEG decode (StbImageSharp) and `ImageData → TextureHandle` upload.
- **`Blix.Diagnostics`** — contribution-based debug system. Game code implements `IDebuggable`; the runtime hands it a `DebugContext` per frame; the OpenTK adapter renders the contributions through ImGui.
- **`Blix.Runtime.OpenTK`** — OpenTK window, GL context, ImGui-backed diagnostics adapter. Implements `IRenderHost`, `IAudioHost`, `IDebugHost`.

See [`architecture.md`](architecture.md) for the project graph and host contracts. See [`blix.md`](blix.md) for what lives above the renderer.

## Engine-facing API (Blix.Render)

The renderer's typed entry points. Game code rarely sees `Blix.Graphics` directly — it constructs `Mesh` + `Material` once at load, then calls `pass.DrawMesh(...)` per frame.

### Mesh

```csharp
public sealed class Mesh
{
    public string Name { get; }
    public VertexBufferHandle VertexBuffer { get; }
    public IndexBufferHandle IndexBuffer { get; }
    public int IndexCount { get; }
    public Bounds3 Bounds { get; }
}
```

Bundles vertex + index + count + mesh-local AABB under a name. Bounds are carried for downstream consumers (debug draw, culling) so they don't rescan vertex data.

Construct from a `MeshData` (the asset-pipeline intermediate) via the `IGraphicsDevice.CreateMesh(MeshData, name)` extension in `Blix.Render`. Demo procedural geometry constructs `Mesh` directly from primitives in `Blix.Graphics/Primitives/`.

### Material

```csharp
var material = new Material("scene.lit", litPipeline)
    .SetTexture("uTexture", cubeTexture, slot: 0)
    .SetTexture("uShadowMap", shadowMapTexture, slot: 1)
    .SetUniform("uLightDirection", new Vector3Uniform(lightDirection));
```

Binds a pipeline to a fluent bag of shared uniforms and texture bindings. `Set*` upserts by name and returns `this`. Materials are intentionally mutable but conceptually frozen after construction — `Set*` exists for setup-time fluency, not per-frame mutation. Per-frame state goes through `perDrawUniforms` / `perDrawTextures` at the draw site.

### MaterialResolver

```csharp
var resolver = new MaterialResolver(graphicsDevice, assets, SamplerDescription.LinearClamp)
    .RegisterPipeline("scene.lit", litPipeline)
    .RegisterPipeline("glass", glassPipeline);

var concrete = resolver.Resolve(AssetId.Parse("materials/concrete"), m =>
{
    m.SetTexture("uShadowMap", shadowMapTexture, slot: 1);
    m.SetTexture("uEnvMap", environmentCubemap, slot: 2);
    m.SetTexture("uNormalMap", flatNormalTexture, slot: 3);  // runtime fallback
});
```

Turns a `MaterialData` (loaded by `MaterialImporter` from a JSON material) into a runtime `Material`. Pipelines are referenced by name; the resolver knows them via `RegisterPipeline`. Texture asset references are resolved against the supplied `AssetDatabase` and cached: two materials referencing the same `AssetId` upload that image once.

Runtime-only bindings (provided through the `Resolve(id, customize)` callback) apply *before* the JSON's bindings, so the JSON cleanly overrides defaults while leaving shared bindings (shadow map, env cubemap) in place.

### DrawMesh

```csharp
pass.DrawMesh(cubeMesh, sceneMaterial, perDrawUniforms:
[
    new ShaderUniform("uModel", new Matrix4x4Uniform(cubeModel)),
    viewUniform,
    projectionUniform,
]);
```

The extension on `RenderPassBuilder` (defined in `Blix.Render`). Merges material state with per-draw overrides — material first, then per-draw. The same uniform set twice means per-draw wins (`glUniform` call order: second wins, which gives intuitive precedence). Texture bindings follow the same rule.

### PbrSceneRenderer

A small helper that walks a `GltfSceneInstance` and issues `DrawMesh` per submesh, optionally with a conservative AABB frustum cull.

```csharp
pbrRenderer.DrawScene(pass, sceneInstance, sharedUniforms, cullFrustum: cameraFrustum, cullMargin: 0.0f);
pbrRenderer.DrawCascadeShadow(pass, sceneInstance, cascadeVP, cullFrustum: cascadeFrustum, cullMargin: 0.0f);
```

`Frustum.FromViewProjection(proj * view)` extracts six planes via Gribb-Hartmann (`row4 ± row{1,2,3}` of the column-vector matrix). `Intersects(Bounds3, margin)` does the p-vertex test with optional outward expansion; the margin escape hatch is there for cases where bounds are slightly under-tight or where author-side authoring quirks land an AABB just outside what the frustum strictly contains.

`DrawCascadeShadow` and `DrawCubeShadowFace` also pull each submesh's albedo binding + base-color factor + alpha cutoff through `perDrawUniforms` so a single shared shadow material can serve every casting material — required for foliage / fabric to cast leaf-shape shadows (see `Alpha-cutout shadow casters` below).

`DrawScene` accepts an optional `IOccluder` (Blix.Render) for GPU occlusion culling — currently disabled by default while we work out a less-conservative test for loose-bounded foliage. See `Diagnostics → Occlusion culling` for the design.

### GltfSceneInstance — logical vs physical units

A glTF scene at runtime has two parallel representations:

- **`Submeshes : IReadOnlyList<SubmeshInstance>`** — *physical* render units. What `PbrSceneRenderer.DrawScene` iterates and `pass.DrawMesh`'s. With `BatchMergeByMaterial` (default on), primitives sharing the same runtime `Material` + vertex layout get concatenated into a single merged VBO/IBO/Mesh per group — cuts opaque + cascade draw counts ~5–7× on Sponza main.
- **`Primitives : IReadOnlyList<PrimitiveSource>`** — *logical* units, one per source glTF primitive regardless of batching. What diagnostics enumerate: `IDebugSelectable` / `IDebugInspectable` / `IDebugGeometrySource` all use `Primitives` so picking selects a single primitive (not "every primitive sharing its material") and per-primitive cull-viz draws TIGHT bounds, not the merged batch's union.

Each `PrimitiveSource` carries a `BatchIndex` pointing back to the `SubmeshInstance` that draws it, so an inspector can show both halves of the story (logical identity + physical batch). Merged batches always use UInt32 indices — concatenated vertex counts easily exceed the ushort range.

Tradeoff: merged batches have UNION bounds, so per-batch frustum cull rejects fewer of them than per-primitive would. For Sponza-shaped scenes (content packed into one atrium) the per-draw saving dominates; scenes with spatially scattered same-material primitives may eventually want a spatial sub-batching layer. Set `BatchMergeByMaterial = false` on `GltfSceneOptions` to fall back to the original per-primitive layout for cull-fidelity debugging.

### SpriteBatch + Font + DebugDraw

Covered below in their own sections. All three are `Blix.Render` types built on top of `IGraphicsDevice`.

## Graphics command language (Blix.Graphics)

`IGraphicsDevice` is the device interface (`Create*` / `Destroy*` for every resource type, `Execute(RenderCommandList)`, `SnapshotResources()`). The OpenGL backend implements it; the API stays byte-oriented (`ReadOnlySpan<byte>`) so no file I/O crosses the device boundary.

### Handles

Every resource is referenced through an opaque handle: `VertexBufferHandle`, `IndexBufferHandle`, `TextureHandle`, `ShaderProgramHandle`, `PipelineHandle`, `RenderSurfaceHandle`. Backend-owned; consumers see only the integer id.

### Render commands + passes

`RenderCommandList` is the per-frame submission unit. Inside `IGameLoop.OnRender` the game builds one:

```csharp
commandList.Pass("scene",
    new RenderPassDescription(
        sceneSurface.Handle,
        ClearColors: [bgColor],
        ClearDepth: true),
    pass =>
    {
        foreach (var obj in opaqueObjects)
            pass.DrawMesh(obj.Mesh, obj.Material, perDrawUniforms: [...]);
    });
```

`Pass(name, description, record)` creates a named pass that records `DrawIndexed` commands. Pass names appear in `FrameDebugPacket` for diagnostics. The OpenGL backend executes passes sequentially.

`RenderPassDescription.ClearColors` is `IReadOnlyList<GraphicsColor?>` with one ergonomic rule:
- **Empty list**: no color clear.
- **1 element**: broadcast to every color attachment.
- **N elements** (N > 1): per-attachment — `ClearColors[i]` either clears attachment `i` to that color or preserves it (`null`).

Clears use `glClearBufferfv(GL_COLOR, i, value)` per attachment. Depth clears use `glClearBufferfv(GL_DEPTH, 0, [1.0])` with `glDepthMask(true)` first so the clear isn't masked by a previous pipeline's `WriteEnabled = false`.

### Pipelines

```csharp
var pipeline = device.CreatePipeline(
    new PipelineDescription(
        shaderProgram: shader,
        vertexLayout: VertexPosition3NormalTexture.Layout,
        topology: PrimitiveTopology.Triangles,
        depth: DepthState.LessEqualWrite,
        rasterizer: RasterizerState.BackFaceCulling,
        blend: BlendState.Disabled),
    name: "lit");
```

`PipelineDescription.ColorBlends` is `IReadOnlyList<BlendState>` for MRT support — convenience constructors wrap a single `BlendState` for the common case. The backend applies `state[i]` to color attachment `i` via `glEnable/glDisable(IndexedEnableCap.Blend, i)`. Color attachments beyond the list default to `BlendState.Disabled`.

`RasterizerState.NoCulling` and `RasterizerState.BackFaceCulling` cover the two common cases. `DepthState` has `Disabled`, `LessEqualWrite`, and `LessEqualNoWrite` (skybox-style). `BlendState` has `Disabled` and `AlphaBlend`.

### Vertex types

Built-in vertex layouts (in `Blix.Graphics`):

| Type | Stride | For |
| --- | --- | --- |
| `VertexPositionColor` | 28 | Plain coloured lines/wires |
| `VertexPositionTexture` | 20 | Simple textured quads (legacy) |
| `VertexPosition3Color` | 28 | Coloured 3D lines (DebugDraw) |
| `VertexPosition3Texture` | 20 | Textured 3D without normals |
| `VertexPosition3TextureColor` | 36 | SpriteBatch + UI text |
| `VertexPosition3NormalTexture` | 32 | Static lit meshes (OBJ assets) |
| `VertexPosition3NormalTextureSkin4Tangent` | 80 | Skinned meshes with per-vertex tangents (glTF) |

Each layout has `Layout` (static `VertexLayout`), `Pack(vertices)` (bytes for `VertexBufferData`), and `WriteVertex(span, v)` helpers.

### Uniforms

`ShaderUniformValue` discriminates:
- `FloatUniform`, `Vector2Uniform`, `Vector3Uniform`, `Vector4Uniform`
- `Matrix4x4Uniform`, `Matrix4x4ArrayUniform` (the latter drives bone palettes)

Pass to `Material.SetUniform` or as per-draw overrides. The OpenGL backend's matrix upload uses a pre-sized buffer (64 mat4 = 1024 floats, sufficient for any practical bone count).

### Primitives

Reusable mesh data in `Blix.Graphics.Primitives.{Icosphere, Cylinder, Torus, TorusKnot, CapsuleMesh, FullscreenQuad, PlaneMesh}`. Each exposes `Vertices` (`VertexPosition3NormalTexture[]`) and `Indices` (`ushort[]`). The demo loads `cube.obj` from disk (so the asset pipeline gets exercised); the other primitives are constructed in code.

### Shader sources

`ShaderSources(VertexSource, FragmentSource, VertexName, FragmentName, VertexSourceMap?, FragmentSourceMap?)` carries text + optional labels + an optional source-id-to-filename map (populated by the preprocessor; consumed by the compile-error formatter). Names flow into the resource registry and into OpenGL object labels (`glObjectLabel` when `GL_KHR_debug` is available — typically on Windows/Linux; macOS uses the labels internally but they don't surface to GL debuggers).

The OpenGL backend reports compile failures with the stage, source name, the GL info log, and the full source dumped with line numbers. When a source map is present, a `--- Source map ---` block prints before the info log so error messages of the form `1:42: ...` decode to a real filename. Link failures include both stage labels.

### GLSL include preprocessor

`Blix.Graphics.GlslPreprocessor.PreprocessDetailed(source, sourceName, readInclude)` returns a `GlslPreprocessResult(ExpandedSource, SourceMap)`:

- Resolves `#include "filename"` directives. Recursive; cycle-detected via a visiting set.
- Honors `#pragma once` — a file that declares it is inlined once across sibling include sites; files without the pragma inline every time (matches C preprocessor semantics, lets non-idempotent snippets work).
- Emits `#line N <source-id>` directives around every inclusion so GLSL compile errors report the original file's line number, not the post-expansion counter. `<source-id>` is an integer that maps to a filename via `SourceMap`.
- Skips the leading `#line` on the top-level source so a leading `#version` directive isn't preceded by another directive — Apple's GLSL parser rejects that.
- File-system access stays in the caller via the `readInclude` callback so the engine layer remains FS-free.

The simpler `Preprocess(source, readInclude)` overload returns just the expanded text for callers that don't need the source map.

Library files start with `#pragma once` and use `blix_`-prefixed symbol names; the convention is described in [Shader library](#shader-library).

Limits: only the double-quoted `#include "name"` form is recognised. Angle-bracket `#include <name>` is reserved for a future library search-path resolution.

### ShaderLoader

`Blix.Graphics.ShaderLoader.LoadVertexFragment(vertexPath, fragmentPath, includeDirs?, defines?)` bundles the common load flow:

```csharp
var sources = ShaderLoader.LoadVertexFragment(
    Path.Combine(shadersDir, "lit.vert"),
    Path.Combine(shadersDir, "lit.frag"),
    defines: new Dictionary<string, string> { ["BLIX_PBR_LITE"] = "1" });
var program = device.CreateShaderProgram(sources);
```

- Reads both files from disk, runs the preprocessor on each, returns a `ShaderSources` with the source maps populated.
- Resolves `#include "name"` first next to the requesting file, then through the optional `includeDirs`.
- The optional `defines` dictionary is injected as `#define KEY VALUE` lines right after `#version`, before any other content. Same defines apply to both stages — call twice with different sources if you need stage-specific defines.

Demos place the engine shader library in `Shaders/lib/<file>.glsl` of their bin output (copied from `src/Blix.Shaders` at build time), so `#include "lib/tonemap.glsl"` resolves correctly with no `includeDirs` argument.

### Shader library

Engine-shared GLSL lives at `src/Blix.Shaders/*.glsl`. It isn't a code project — the files are copied into each demo's `bin/.../Shaders/lib/` at build time via `<None Include="..\Blix.Shaders\**\*.glsl" Link="Shaders\lib\%(RecursiveDir)%(Filename)%(Extension)">` in the demo csproj.

Current library files:

| File | What's in it |
| --- | --- |
| `tonemap.glsl` | ACES Filmic (Narkowicz), AgX (Sobotka), Reinhard, Neutral; mode selector; saturation + contrast grade. |
| `noise.glsl` | 2D/3D Inigo-Quilez hashes; screen-space hash for jitter; value noise 2D/3D; 4-octave FBM 2D/3D (2D rotates between octaves to break axis alignment). |
| `pbr.glsl` | GGX distribution, Smith geometry, Schlick + Lazarov roughness-aware Fresnel, Cook-Torrance BRDF, Karis windowed inverse-square attenuation. |

Conventions:
- Every library symbol gets a `blix_` prefix (functions and `BLIX_*` for macros). Keeps the library composable with third-party GLSL.
- Each file starts with `#pragma once`.
- One concept per file. No `utils.glsl` grab-bag.
- Demo shaders that pre-date the prefix sometimes `#define short_name blix_full_name` at the include site so existing call sites stay readable.

Demo consumption examples:
- `composite.frag` — `#include "lib/tonemap.glsl"`; calls `blix_tonemap`, `blix_saturate`, `blix_contrast`.
- `lit.frag` — `#include "lib/pbr.glsl"`; calls `blix_distributionGGX`, `blix_geometrySmith`, `blix_fresnelSchlick`, `blix_fresnelLazarov`.
- `volume.frag` / `flame.frag` — `#include "lib/noise.glsl"`; call `blix_vnoise3`, `blix_fbm3`, etc.
- `ssr.frag` — `#include "lib/noise.glsl"`; calls `blix_screenHash` for per-pixel jitter.

## Render surfaces + attachments

Render passes target either `RenderSurfaceHandle.Default` (the window framebuffer) or an offscreen `RenderSurface` created by `IGraphicsDevice.CreateRenderSurface`. Surfaces declare one or more color attachments and an optional depth attachment.

```csharp
var scene = graphicsDevice.CreateRenderSurface(new RenderSurfaceDescription(
    Name: "scene",
    Size: new MatchDefaultRenderSurfaceSize(),
    ColorAttachments:
    [
        new ColorAttachmentDescription(TextureFormat.Rgba16F, SamplerDescription.LinearClamp), // HDR
        new ColorAttachmentDescription(TextureFormat.Rgba8,    SamplerDescription.LinearClamp), // luminance
        new ColorAttachmentDescription(TextureFormat.Rgba8,    SamplerDescription.LinearClamp), // normals
    ],
    Depth: new DepthTexture(SamplerDescription.LinearClamp)));
```

`MatchDefaultRenderSurfaceSize` follows the runtime-owned framebuffer size — the backend preserves the `RenderSurfaceHandle` and every attachment `TextureHandle` across resize by rebuilding the underlying GL framebuffer + attachment textures. `FixedRenderSurfaceSize(w, h)` opts out.

Fragment shaders write to color attachments via explicit `layout (location = N) out` declarations. The backend emits `glDrawBuffers([...N])` so every attachment receives writes; for depth-only surfaces (0 color attachments) it emits `glDrawBuffer(NONE)`.

### Depth attachments

Three forms:
- `DepthTexture(sampler)` — sampleable depth, `sampler2D` or `sampler2DShadow` (the latter when `sampler.Compare = true`).
- `DepthCubeFace(cube, face)` — one face of an existing cube depth texture, used for omnidirectional shadow mapping.
- `DepthRenderbuffer` — write-only, no sampling; faster but unused in the current demo.

### Cubemaps

| API | For |
| --- | --- |
| `CreateTextureCube(faceSize, sampler, [...face data])` | RGBA8 environment cubemap (legacy LDR path) |
| `CreateTextureCubeHdr(faceSize, sampler, [...face Half data])` | Rgba16F linear HDR environment cubemap with mip chain |
| `CreateTextureCubeDepth(faceSize, sampler)` | Depth cubemap for point-light shadow mapping (sampled as `samplerCubeShadow`) |

### Attachment naming

User-created resources accept an optional `string? name`; when omitted the backend synthesizes `"texture#7"` etc. Render-surface attachment textures get derived names from the parent: `"{surfaceName}.color[i]"` and `"{surfaceName}.depth"`. `TextureKind` (in `ResourceRegistrySnapshot`) distinguishes `UserUploaded`, `RenderSurfaceColor`, `RenderSurfaceDepth` for diagnostics filtering.

## Frame pipelines (the demos' spines)

The two demos exercise the renderer in different shapes. The Walkthrough demo's pipeline is documented in [`walkthrough.md`](walkthrough.md). ShaderLab's pipeline below covers the multi-light PCSS / glass / fur / hologram path.

### ShaderLab

Every pass in `Blix.Demos.ShaderLab`, in execution order.

| # | Pass name | Target | Reads | What it does |
| --- | --- | --- | --- | --- |
| 1 | `shadow` | `shadowSurface` (2048², depth) | `lightViewProjection` | Sun directional shadow map. Back-face cull. Skinned + static casters. |
| 2 | `shadow.point.{i}.face{j}` | `pointShadowFaceSurfaces[i, j]` (512², depth) | point light VP per face | Per-shadow-casting point light, six per-face passes into one depth cubemap. Up to 2 casters. |
| 3 | `shadow.spot.{i}` | `spotShadowSurfaces[i]` (1024², depth) | spot light VP | One pass per shadow-casting spot. Up to 4 casters. |
| 4 | `scene` | `sceneSurface` MRT (HDR/luminance/normals + depth) | sun + spot + point shadow maps, env cubemap | Skybox + opaque PBR lit pass + skinned PBR lit pass. |
| 5 | `fur` | `sceneSurface` | scene | Multi-shell fur on demo's bunny. |
| 6 | `hologram` | `sceneSurface` | scene | Rim-lit hologram + scanlines + glitch on demo's suzanne. |
| 7 | `scene.copy` | `sceneCopySurface` (HDR) | `sceneSurface.color[0]` | Snapshots the opaque HDR scene for glass refraction sampling. |
| 8 | `glass` | `sceneSurface.color[0]` | `sceneCopySurface`, env cubemap | Refractive/reflective torus knot rendered back into HDR scene. |
| 9 | `bloom.bright[i]` × 3 | per-level bright (HDR, half/quarter/eighth) | `sceneSurface.color[0]` | Three-level bloom bright-pass extraction. |
| 10 | `bloom.blurH[i]` × 3, `bloom.blurV[i]` × 3 | bright + temp ping-pong | per-level bright | Separable Gaussian per level. |
| 11 | `present` *or* `present.bloom` | Default framebuffer | `sceneSurface`, bloom levels | Composite + tone map + linear→sRGB. The `present.bloom` variant sums the three bloom levels; `present` is the no-bloom debug path. |
| 12 | `debug` | Default framebuffer | — | Appended by `Window` when diagnostics are enabled. Lines/AABBs/grid/frustum from `debug.Draw.*` commands. |
| 13 | `hud` | Default framebuffer | — | SpriteBatch + Font HUD overlay (FPS, camera position, keys hint). |

The `present` mode is driven by the diagnostics UI's view dropdown — selecting "shadow map" or "normals" or "bloom 0" routes that surface to the present pass instead of the composited scene.

## PBR shading

The lit pipeline (`cube.frag` for static, `skin.lit.frag` for skinned, both `#include`-ing `pbr_core.glsl`) runs Cook-Torrance with:

- **GGX (Trowbridge-Reitz) normal distribution** with `α = roughness²` for perceptual-linear roughness.
- **Smith geometry** with Schlick-GGX masking-shadowing (`k = (roughness + 1)² / 8` for direct lighting).
- **Schlick fresnel** with `F0 = mix(0.04, baseColor, metallic)` — dielectrics get the standard 4% reflectance, metals use their albedo as F0.
- **Energy-conserving Lambertian diffuse**: `(1 - F) × (1 - metallic) × albedo / π`.

### Material inputs (glTF metallic-roughness convention)

| Uniform | Source |
| --- | --- |
| `uTexture` (sRGB → linear at sample) | baseColor texture or white fallback |
| `uMetallicRoughnessMap` | G channel = roughness, B channel = metallic. 1×1 `(0, 255, 255)` neutral when no texture. |
| `uNormalMap` + `uNormalScale` | Tangent-space normal map (sRGB-flat fallback when no texture; `uNormalScale = 0` disables sampling). |
| `uBaseColorFactor` | `Vector4` multiplier on sampled baseColor. |
| `uMetallicFactor`, `uRoughnessFactor` | Scalar multipliers on the MR texture samples. |
| `uEnvMap` + `uEnvMapMipCount` | Cubemap for IBL specular (textureLod-driven by roughness). |

### IBL (image-based lighting)

Two IBL paths coexist:

**Simple mip-LOD path (ShaderLab).** The env cubemap is auto-mipmapped (linear box filter). Specular IBL samples at `lod = roughness × (mipCount - 1)`; diffuse irradiance samples at the highest mip. Cheap, visually correct in trend, not physically exact.

**Karis split-sum (Walkthrough).** Full PBR pipeline:
- A **GGX-prefiltered specular cubemap** generated by `Blix.Graphics.Images.PbrIblBaker.BakeSpecularPrefilteredMips`. Each mip is convolved with a GGX lobe at progressively higher roughness via importance sampling. Sampled in the lit shader with `textureLod(prefiltered, R, roughness * (mipCount - 1))`.
- A **cos-weighted diffuse irradiance cubemap** generated by `BakeDiffuseIrradiance`. Sampled directly (no LOD).
- A **2D BRDF LUT** generated by `BakeBrdfLut` — pre-integrated `(F0 * scale + bias)` for any `(NdotV, roughness)` pair. The split-sum specular contribution is `prefilteredColor * (F0 * brdf.x + brdf.y)`.

`PbrIblBaker` clamps each environment sample's magnitude (firefly suppression) before integration. Polyhaven HDRIs' single-pixel suns otherwise produce visible speckle on normal-mapped surfaces; the clamp at ~50 cleans this up without affecting the perceptual look.

### HDR IBL bake pipeline

`Blix.Graphics.Images` houses the offline IBL bake. Three pieces:

| Type | Purpose |
| --- | --- |
| `ImageLoader.LoadRgba32F(path)` | Loads `.hdr` (RGBE) into a `HdrImageData` with float32 pixels. Uses StbImageSharp's HDR decoder. |
| `EquirectangularToCubemap.Convert(equirect, faceSize)` | Reprojects an equirectangular HDR (Polyhaven et al.) into 6 cubemap faces. CPU-side: per-face inverse mapping with bilinear filtering. |
| `PbrIblBaker.BakeSpecularPrefilteredMips`, `BakeDiffuseIrradiance`, `BakeBrdfLut` | Importance-sampled GGX convolution, cos-weighted hemisphere convolution, and the 2D pre-integrated BRDF LUT. |

Plus the convenience `HdrSunFinder.FindSunDirection(hdrImage)` — scans the upper hemisphere of an equirect for the brightest pixel cluster and returns the world direction the sun lives at. The Walkthrough demo uses it to auto-align the directional light to whatever HDR the user drops in; without it the visible sky and the directional shadows disagree on where the sun is.

### sRGB and linear space

All lighting runs in linear space. BaseColor and Emissive textures are uploaded with `TextureFormat.Rgba8Srgb` (mapped to `GL_SRGB8_ALPHA8` on the GL side), and compressed sRGB variants use `COMPRESSED_SRGB_ALPHA_BPTC_UNORM` — the GPU does the sRGB → linear conversion **before** filtering, so bilinear / trilinear / mip-pyramid averaging happens in linear space (the physically correct behaviour). The fragment shaders treat the sampler output as already-linear; there's no per-fragment `pow(2.2)`. Normal, MetallicRoughness and Occlusion textures stay linear (`Rgba8` / `Bc7Unorm`) per glTF spec. The final composite/tone-map shader does linear → sRGB encode for display.

`GltfSceneInstance.BindMaterialTexture` promotes `Rgba8 → Rgba8Srgb` automatically when the slot is `"albedo"` or `"emissive"` so callers don't have to think about it.

### Tangent-space normal mapping

For static meshes (`cube.frag`), the tangent frame is synthesised per-fragment from screen-space derivatives (`dFdx` / `dFdy`) of world position + UV. Cheap and works for low-frequency normal-map detail. The trade-off is constant-per-triangle TBN; very high-frequency normal-map detail can show faint triangle boundaries.

For skinned meshes (`skin.lit.frag`), tangents come from the glTF `TANGENT` accessor (vec4: XYZ direction + W bitangent sign, baked by the asset's authoring tool — typically MikkTSpace). When an asset doesn't provide them, the importer emits `(0, 0, 0, 0)` as a sentinel and the fragment shader falls back to derivative synthesis.

## Shadows

Four shadow paths.

### Directional sun (simple)

One 2048² depth render surface, sampled as `sampler2DShadow` with `Compare: true`. The shadow pass uses back-face culling and a slope-scaled bias. ShaderLab's hand-sized 7×7 ortho is fine for a small scene.

### Cascade shadow maps (CSM)

The Walkthrough demo uses three 2048² cascades to cover Sponza's ~40m view distance without one giant low-resolution shadow. Each cascade has its own depth surface; the lit shader picks one per fragment from the linearised view-space depth (`viewDepth`) via the `cascadeSplits[]` boundary array.

Cascade fitting uses a **stable sphere bound**: per cascade, take the view-space sub-frustum's 8 corners, compute their bounding sphere, snap the centre to texel boundaries in light space. The sphere bound's radius is view-space-only (independent of light direction) so cascades don't "swing" as the sun rotates. Texel-snap eliminates the per-frame jitter that would otherwise visible-flicker shadow boundaries.

`Visualize CSM (R/G/B by cascade)` in the debug UI tints each fragment by which cascade it sampled — handy when tuning splits.

### Point cubemap

Up to **2** shadow-casting point lights in ShaderLab, **4** in the Walkthrough demo. Each gets a depth cubemap (`CreateTextureCubeDepth`) with `samplerCubeShadow` sampling. Per-frame work: 6 passes per caster (one per cube face), each with the standard OpenGL cubemap orientation matrix. Far plane = `light.Range` so the cubemap depth and the shader's normalised reference depth agree.

Point cube shadows are **incrementally re-baked**: the demo tracks each light's last-baked position and only re-renders that light's six faces when the position drifts past a small epsilon. Static lighting in the steady state costs zero per frame.

### Spot

Up to **4** shadow-casting spot lights (ShaderLab). Each gets a 1024² depth surface. Per-frame: one pass per caster with the spot's view-projection.

### PCSS sampling

`pbr_core.glsl` implements three-stage PCSS for the directional + spot paths:

1. **Blocker search** — 8 Poisson taps at a fixed search radius. Count how many are shadowed.
2. **Penumbra estimation** — use the blocker fraction as a proxy for occluder proximity, scale a per-fragment kernel radius from it.
3. **PCF with the variable-width kernel** — 16 Poisson taps at the per-fragment radius.

Result: shadows close to their occluder are sharp; shadows farther away widen and soften naturally.

Point cubemap shadows use a direction-space PCSS analog: per-fragment tangent basis perpendicular to the light-to-fragment direction, Poisson disk offsets projected onto that tangent plane, blocker search + variable-kernel PCF.

Back-facing fragments (`dot(N, L) ≤ 0`) skip the shadow lookup and use `shadow = 1`. Reason: with front-face culling in the shadow pass, the shadow map records the back-of-geometry from the light's POV. A back-facing fragment sits *at* that recorded depth, so the depth compare is borderline-stable and produces noise. Back-faces can't be cast-shadowed anyway, so the gate is physically correct and removes the noise source.

### Alpha-cutout shadow casters

`shadow.frag` and `shadow_cube.frag` sample the casting material's albedo and `discard` fragments below `uAlphaCutoff` — so MASK-mode foliage (cypress leaves, etc.) writes leaf-shape gaps into the shadow map instead of a solid bounding rectangle. Volumetric fog god-rays sample these maps too, so the sun-streak shape passes through individual leaf gaps. Opaque materials pass `uAlphaCutoff = 0` and skip the texture sample entirely.

`PbrSceneRenderer.DrawCascadeShadow` and `DrawCubeShadowFace` carry the per-submesh `uAlbedo` binding + `uBaseColorFactor` + `uAlphaCutoff` through `perDrawUniforms` / `perDrawTextures` so a single shared shadow material can serve every primitive.

### Limits

- Per-spot and per-cubemap shadow-map sampling uses fixed-index unrolling in the shader (`if (i == 0) ... else if (i == 1) ...`) because dynamic indexing of sampler arrays isn't portable across drivers. Easy to extend; just adds branches.
- Single sun caster (no cascades, no atlasing).
- Bias is shader-tuned for the demo; no backend-level `glPolygonOffset` wrapper yet.
- PCSS blocker search uses the compare-result proxy, not true blocker depth. The textbook fix is a parallel `sampler2D` binding to the same shadow texture; deferred.

## HDR pipeline

Scene lighting runs in linear HDR; post-process passes consume the HDR scene buffer and the final composite tonemaps + gamma encodes to the swapchain. The scene color attachment is `Rgba16F` because lit-pass output routinely exceeds 1.0 on specular highlights, bright reflective surfaces, and emissive volumetrics.

### Environment cubemap

`CreateTextureCubeHdr` allocates a `Rgba16F` cubemap and uploads `Half`-typed face data. Two sources feed it:

- **Procedural sky** (`CubemapBaker.BakeSky` in the Walkthrough demo, similar inline code in ShaderLab) — generates six faces from a sun direction + horizon tint. Cheap, no HDR file required.
- **HDR equirect → cube** via `EquirectangularToCubemap.Convert` (see [HDR IBL bake pipeline](#hdr-ibl-bake-pipeline)). Drop a `.hdr` into the demo's assets and it auto-aligns the sun direction via `HdrSunFinder`.

Auto-mipmapped via `SamplerDescription.LinearClampMipmap` so the IBL roughness-LOD path has 9 mip levels at 256² face size. The Walkthrough demo's full PBR pipeline replaces the simple mip-LOD heuristic with the Karis split-sum baked probes (see [IBL](#ibl-image-based-lighting)).

The skybox pass uses `DepthState.LessEqualNoWrite` and `RasterizerState.NoCulling`; the skybox vertex shader forces `clip.z = clip.w` so every fragment lands at the far plane and only paints where depth is still 1.

### Material G-buffer (MRT)

The Walkthrough demo's `hdrSceneSurface` has **two** color attachments:

- **Attachment 0** (`Rgba16F`): the HDR scene color. Sampled by bloom, SSR, fog, and the final composite.
- **Attachment 1** (`Rgba8`): per-fragment material info. R = roughness; GBA spare. Sampled by SSR to gate matte surfaces (cloth, plaster, brick) so reflection rays only fire from genuinely-smooth materials.

Every shader that draws to `hdrSceneSurface` declares `layout(location = 1) out vec4 fragMaterial` and writes a value appropriate to its blend mode:
- **Lit**: writes the actual roughness used by the PBR pipeline; blend disabled on both attachments (overwrite).
- **Skybox / volume / flame**: writes 1.0 (matte) so SSR rays hitting those pixels get gated out.
- **Fog**: writes 0 with additive blend so the underlying surface's roughness is preserved.

Pipelines targeting `hdrSceneSurface` declare a length-2 `ColorBlends` array (the API supports per-attachment blend states; `PipelineDescription.ColorBlends[i]` applies to attachment `i`).

This is **the** way to keep SSR honest: a geometric gate (upward normal + below camera) cannot distinguish marble from cloth and ends up reflecting balcony rails draped in fabric. A material G-buffer is the right level at which to filter.

### Screen-space reflections (SSR)

The Walkthrough demo's SSR pass marches a reflection ray in NDC/screen space:

1. **Gate**: reconstructed normal must point up (smoothstep over `dot(N, +Y)`); fragment must sit below the camera by ≥ 0.3m; roughness sampled from the material G-buffer must be below `uRoughnessCutoff` (default 0.4).
2. **Ray-march**: project ray start and end into clip space, perspective-divide to NDC, march `uSteps` (default 40) linearly between `uvStart` and `uvEnd` in screen space — natural pixel-sized steps that match the depth buffer's precision. Per-pixel hash jitter on the step offset breaks step boundaries into noise rather than visible bands.
3. **Binary-search refinement**: when a sample crosses the depth surface, do 5 bisections between the last-no-hit and first-hit positions for sub-step precision. Without this you see step quantisation as the camera moves.
4. **HDR clamp on the sample**: cap the reflected colour at 2.0 per channel so a fire volume's white-hot core (~3.5 HDR) doesn't show as over-saturated blobs in the marble.
5. **Edge + distance fades**: smoothly fade contribution near screen edges and at the end of the marched segment.

Normal reconstruction uses screen-space derivatives only for the upward-mask gate; the actual reflection vector uses a hardcoded `(0, 1, 0)` because depth-derivative normals are too noisy at typical scene distances on 24-bit depth and produce kaleidoscope artefacts otherwise.

Output goes to a separate `ssrSurface` (no read-write hazard with `hdrSceneSurface`); the composite combines them.

### Volumetric fog

Full-screen pass that, per pixel:
1. Reconstructs the scene's world-space far point from sampled depth.
2. Marches the view ray from camera to that point in `uFogSteps` (16-48).
3. Per step samples the directional shadow map to gate visibility — unshadowed steps accumulate sun in-scatter (this is what makes god-rays appear).
4. Accumulates inscatter with a Henyey-Greenstein phase function (`g = 0.6`, forward-peaked).
5. **Point-light scatter** is added analytically without per-step march: for each lamp, the closest distance from the ray to the lamp determines a bounded smooth halo. Smoothstep over the marched-segment endpoints prevents hard edges where the lamp's projection crosses the camera or the scene far. Uses the lamp's *hue* (not its intensity) modulated by fog density; otherwise any non-zero scatter blasts the scene.

Output is additive over the HDR scene buffer (so bloom picks up the god-rays).

### Bloom

Dual-filter bloom in the Walkthrough demo: a 4-level downsample chain (`bloom_down.frag`) followed by a tent-filter upsample (`bloom_up.frag`) with additive blend. Each mip is `Rgba16F` at half-resolution-per-level. ShaderLab keeps the simpler 3-level Gaussian chain — the same composite handles both.

### Composite + tonemap

`composite.frag` is the final swapchain pass:

1. Samples HDR scene, bloom upsample mip 0, and SSR contribution.
2. **Grade in linear HDR**: temperature multiplier, saturation, contrast. Grading runs *before* tonemap so the curve sees punchy values; post-tonemap grading just shifts already-clipped LDR.
3. **Tonemap operator selectable via uniform**: ACES Filmic / AgX (approximated) / Reinhard / Neutral (clamp). All four live in `lib/tonemap.glsl`.
4. Gamma encode (`x^(1/2.2)`); write to swapchain.

Debug toggles: `Show SSR only` isolates the SSR contribution; `Flip V` A/B-tests the SSR vertical convention.

## Glass refraction

The glass torus knot is the demo's hero refractive surface. It can't sample the HDR scene buffer while writing to it, so the renderer takes a snapshot via the `scene.copy` pass between `scene` and `glass`. The glass shader:

- Samples the opaque scene snapshot offset by a screen-space refraction vector derived from surface normal and `uThickness`.
- Samples the environment cubemap reflection along the reflected view vector.
- Mixes refraction and reflection using Schlick's Fresnel approximation with base reflectance `uF0`.
- Applies a slight `uTint` to the refracted contribution for a faint bluish-green glass look.

The glass pass writes back into the HDR scene buffer so bloom and the final composite see glass highlights naturally. It also writes depth — the torus knot self-overlaps heavily while rotating, and without depth writes the self-overlapping fragments would draw in submission order and appear to cut through their own surface.

The torus knot is intentionally excluded from the shadow pass; its refractive, self-overlapping geometry made the shadow map noisier than useful. A future glass-on-opaque shadow pass would render the silhouette only.

## Sprite batching, text, and UI

### SpriteBatch

`Blix.Render.SpriteBatch` is the 2D drawing primitive. MonoGame-shaped: no `Sprite` type — `Draw(TextureHandle, ...)` is the API. Sprites are textured quads parameterised by texture, position, size, optional source rect, color tint, depth, and a `flipV` knob.

```csharp
spriteBatch.Begin(uiCamera.GetViewProjection(frame.Width, frame.Height), SpriteSortMode.Deferred);
spriteBatch.Draw(cubeTexture, position: new Vector2(-64, -64), size: new Vector2(128, 128));
spriteBatch.Draw(otherTexture, position, size, sourceRect: new Rect(0, 0, 32, 32), color: tint, depth: 0.5f);
spriteBatch.End(pass);
```

`Begin` takes a precomputed view-projection rather than a camera so SpriteBatch can live in `Blix.Render` without depending on the `Blix` layer above it (where `Camera2D` lives). The caller (typically owning a `Camera2D`) resolves the matrix and hands it in.

Capacity is 4 096 sprites per `Begin/End` (16 384 vertices, 24 576 indices — pre-baked sequential quad index buffer). Vertex format is `VertexPosition3TextureColor` (36-byte stride).

#### Sort modes

`SpriteSortMode` controls draw order and batch grouping:
- `Deferred` *(default)* — preserves submission order. Batching breaks every time the texture changes between consecutive draws.
- `BackToFront` — sort by depth descending. Correct for alpha-blended sprites that overlap.
- `FrontToBack` — sort by depth ascending. Early-Z-friendly for opaque sprites.
- `Texture` — sort by `TextureHandle.Id`. Minimises batch breaks at the cost of submission order.

After sorting, `End` walks the list partitioning by `TextureHandle` — each partition becomes one `DrawIndexed` call with `perDrawTextures: [currentTexture]`.

#### flipV

`Draw(..., flipV: true)` (the default) matches textures pre-flipped by `ImageLoader.LoadRgba32` (the stb_image default). The font baker doesn't pre-flip its atlas (the bitmap is authored directly in code with rows ordered top-to-bottom), so the HUD path uses `flipV: false`.

### Font + text

`Blix.Assets.FontImporter` is an `IAssetImporter<FontData>` (dispatch key `font.json`). It reads a small `font.json` spec file that points at a TTF + the pixel sizes to bake:

```json
{ "ttf": "Roboto-Regular.ttf", "sizes": [14, 20, 28, 40, 56] }
```

The TTF path resolves relative to the spec file. Each requested size gets its own square atlas baked via StbTrueTypeSharp (auto-sized from 128 up to 4096 via power-of-two retry). The result is `FontData` — CPU-side: per-size alpha bitmaps + glyph table (atlas rect, offset, advance) + scaled v-metrics. Game code loads it like any other asset: `assets.Load<FontData>(AssetId.Parse("fonts/roboto"))`.

`Blix.Render.Font.Upload(device, fontData)` allocates one `TextureHandle` per baked size. Alpha is expanded to RGBA8 = `(255, 255, 255, a)` on upload so the existing sprite shader tints text via vertex color with no new shader. `Font.NearestSize(pixelSize)` picks the closest baked size for HiDPI selection.

`SpriteBatchUiExtensions` adds `DrawText` (newline-aware), `MeasureText`, `DrawSolidRect`, and `DrawNineSlice`. All route through `SpriteBatch.Draw`; the partition-by-texture batching collapses a HUD with text + panel + 9-slice frame to one draw per unique texture.

#### HiDPI conventions

Screen-space ortho is `GraphicsMatrices.CreateOrthographicOffCenter(0, width, height, 0, -1, 1)` — origin top-left, Y growing down. Pass *logical* width/height (`Host.LogicalSize`), not framebuffer pixels — units stay in points across DPI scales.

`DrawText` / `MeasureText` take a `dpiScale` (= framebuffer / logical width). The atlas pick uses `pixelSize × dpiScale` so retina lands on a higher-res baked size; the quad still renders at logical size, giving 1:1 atlas-pixel to physical-pixel mapping. The demo bakes Roboto at `[14, 20, 28, 40, 56]` to cover 1× + 2× DPI of the three nominal display sizes.

#### Limits

- ASCII 32–126 only. No CJK, no emoji, no Latin-1 supplement, no shaping. Promoting to Unicode-aware would replace `BakeFontBitmap` with `PackFontRange` over multiple codepoint ranges.
- No kerning, no ligatures, no complex shaping. Pen advance is straight `xadvance`.
- One `Font` value per font face. Multi-font fallback would compose `Font` instances per codepoint at draw time.
- No retained-mode UI tree, no layout solver, no theming.
- One `TextureHandle` per baked size. A custom shelf packer could pack every size into one mega-atlas; SpriteBatch's per-texture batching makes the per-size approach OK.
- Bitmap-only. No SDF / MSDF.
- No text wrapping / alignment / RTL. `\n` wraps but there's no width-based auto-wrap.
- Atlas is RGBA8 not R8 (3× memory cost). Adding `TextureFormat.R8` + a font-specific shader sampling `r` is a small follow-up.

## Diagnostics

`Blix.Diagnostics` is a contribution-based observability layer with three independent extension axes: **channels** (what data shape gets produced), **producers** (who emits the data), and **sinks** (who consumes finished frames). Game code never calls ImGui directly — it contributes through typed channels and the OpenTK runtime renders them.

### Frame model

Each frame is an immutable `DebugFrame` snapshot. `DebugSystem` keeps the most recent ~120 finished frames in a ring (`DebugFrameHistory`), drives the per-frame producer walk, and fans the snapshot out to registered sinks.

```
BeginFrame(renderFrameContext)         // mints a DebugContext
Run(IDebuggable[])                     // pull-mode producers
  ... game render ...                  // push producers via IDebugHost.CurrentDebug
EndFrame()                             // snapshot -> ring -> sinks; Current cleared
```

`Freeze()` / `Freeze(int frameNumber)` / `Unfreeze()` lets the UI render against a frozen `DebugFrame` while the game keeps producing new frames behind it. The frozen frame is held independently of the ring so a long inspection survives ring overwrite.

### Channels

All six channels live on `DebugContext` and snapshot into `DebugFrame`:

| Channel | API | Aggregation |
| --- | --- | --- |
| `Values` | `Value(name, object?)` | last-write per path |
| `Controls` | `Toggle`/`Float`/`Enum`/`Button` | last-write per path; UI mutations round-trip through `pendingControlValues` |
| `Draw` | `Line/Aabb/Grid/Frustum/Sphere/Plane/Ray/Capsule/Obb/Cross/Cone/Arrow/MeshWireframe/Normals` | append-only |
| `Stats` | `Count(name, delta)` / `Increment(name)` / `Gauge(name, value)` | sum (Count) / last (Gauge), eager per path |
| `Timers` | `using (Timers.Measure("Opaque")) { ... }` | sum `TotalMs`, `++CallCount` per path |
| `Events` | `Info/Warn/Error(message, payload?)` | chronological, no aggregation |

Path resolution is uniform across channels — emissions inherit the current `Scope`. A producer named `"physics"` emitting `Stats.Count("draws", 1)` from inside `using (ctx.Scope("colliders"))` resolves to path `"physics/colliders/draws"`.

Frame-level CPU timer ("frame") is auto-recorded by `DebugSystem.EndFrame` so every frame has a baseline. The OpenGL backend can emit per-pass GPU timings via `glQueryCounter` (when `GL_ARB_timer_query` is available); the runtime drains them into `Timers` under scope `"gpu/passes"` 1–N frames after issue.

**GPU timing is off by default and has real platform caveats.** Two issues, both found the hard way on macOS:
- Issuing `glQueryCounter` calls forces a partial sync on Apple's GL→Metal translation layer, costing 5–15 ms of every frame *we wanted to measure*. Classic observer effect — the diagnostic itself dragged the budget down. Hence opt-in via `OpenGLGraphicsDevice.GpuTimingEnabled`.
- Even with the queries enabled, the macOS driver returns timestamps at command-submit time rather than GPU-execute time. Begin/end deltas come back as ~0 for every pass, making the column unusable on Apple's GL. Linux / Windows drivers should populate normally. The Perf tab detects all-zero results and surfaces a hint explaining why.

CPU phase timers (`frame`, `build-commands`, `execute`, `overlay`, `swap`) are unaffected — they measure CPU-side wall-clock and are trustworthy on every platform. The `swap` timer specifically reveals when `SwapBuffers` is the bottleneck (vsync wait, GPU-still-busy fence, or display-link sync) — a value > 20 ms there means the ceiling is below us, not in our rendering work.

### Producer interfaces

Every producer implements `IDebugContributor` (just `string DebugName { get; }`) and registers once with `DebugSystem.Register(contributor)`. Three specialisations stack independently:

| Interface | Role | When invoked |
| --- | --- | --- |
| `IDebuggable` | Pull-mode state production (`Values`, `Controls`, `Stats`, etc.) | Every `Run()`, under auto-scope of `DebugName` |
| `IDebugGeometrySource` | Spatial geometry emission (debug AABBs, normals, wireframes) | Every `Run()`, only when `State.IsPathVisible(DebugName)` — disabled layers skip the call entirely (zero CPU) |
| `IDebugSelectable` | Picking surface (`(EntityPath, Bounds3)` collected into a destination list for ray-vs-AABB) | On demand via `DebugSystem.CollectSelectables()` |
| `IDebugInspectable` | Per-selection inspector (emits Values under `"selection/"` scope when path matches) | Every `Run()` if `SelectedPath != null` |
| `IDebugUi` (in `Blix.Runtime.OpenTK`) | Custom ImGui panel | Every frame in the HUD's Custom tab |

A class can implement any combination — the same registry holds it once.

### Push hook (graphics-side)

`IFrameRecorder` in `Blix.Graphics` lets the render-command layer feed events back to diagnostics without coupling. `RenderCommandList.Pass()` invokes `OnPassBegin`/`OnPassEnd`; `RenderPassBuilder.DrawIndexed` invokes `OnDraw(in DrawIndexedCommand)`. `DiagnosticsFrameRecorder` (the diagnostics-side implementation, wired by `Window`) translates these into `Stats` (`draws`, `triangles`, both top-level and per-pass under `passes/<name>/`) and per-pass `Timers` (`passes/<name>/build`).

### Sinks

`IDebugFrameSink.Consume(DebugFrame)` runs after each `EndFrame` snapshot. The runtime registers two by default:

- **`ConsoleEventSink`** — prints `Events` at or above min-severity (default `Warn`) to stderr.
- **`JsonDumpSink`** — `F12` dumps the current display frame (frozen if frozen, else latest) to `dumps/frame-NNNNNN.json`. Schema-stable DTO keyed by `Kind` discriminator. Polymorphic payloads (e.g. `AssetLoadReport`) captured via `JsonNode` so the runtime type is preserved on disk.

A throwing sink is caught and logged; the loop keeps running.

### Selection + picking

Demos own the camera + ray construction (the runtime is camera-agnostic). The typical pick flow:

```csharp
var selectables = debugSystem.CollectSelectables();
var bestVolume = float.PositiveInfinity;
DebugSelectable? best = null;
foreach (var s in selectables)
{
    if (Intersection.Raycast(ray, s.Bounds, float.PositiveInfinity) is { } hit)
    {
        var v = Volume(s.Bounds);
        if (v < bestVolume) { bestVolume = v; best = s; }
    }
}
if (best is { } pick) debugSystem.Select(pick.EntityPath, pick.Bounds);
else debugSystem.ClearSelection();
```

Smallest-AABB-volume preference is the right heuristic when scenes have overlapping bounds (Sponza's structural pieces encompass their decor) — without true mesh-level picking, ray-entry-time alone always grabs the floor.

`SelectedPath` and `SelectedBounds` are cross-frame state, snapshotted into `DebugFrame.SelectedPath`. The selection sweep at the end of `Run()` auto-emits a bright **magenta** outline (`Aabb` + `Sphere` at the bounds centre + `Cross` for orientation) under path `"selection/<entity-path>"`. These bypass the layer filter — they're system feedback, not user content. Registered `IDebugInspectable` producers also receive `Inspect(SelectedPath, ctx)` and emit data under the same `"selection"` scope; the ImGui Selection tab pulls those Values and the State tab filters them out.

### IDebugHost

`Window` implements `IDebugHost`, exposing both the active `DebugContext` (for push-mode draws / inspect data during render) and the full `DebugSystem` (for registry + freeze + selection from `OnLoad`):

```csharp
if (Host is IDebugHost { System: { } sys })
{
    sys.Register(uploader);                  // IDebuggable producer
    sys.Register(myMesh);                    // IDebugSelectable + IDebugInspectable
    sys.State.LayersEnabled["scene"] = false; // start with scene viz hidden
}

// ... per-frame in OnRender:
if (Host is IDebugHost { CurrentDebug: { } dbg })
{
    dbg.Draw.ViewProjection = projection * view;   // required — see footgun below
}
```

**Footgun:** `Draw.ViewProjection` defaults to `Matrix4x4.Identity` each frame. A demo that emits debug-draw primitives but never assigns the matrix sees nothing — lines are multiplied by identity and clipped. The runtime prints a one-shot stderr warning the first time this happens.

### Layer toggles

`DebugState.LayersEnabled : Dictionary<string, bool>` gates debug-draw rendering by path prefix. `IsPathVisible(path)` walks the path leaf → root and short-circuits on the first explicit `false`. Disabling `"physics"` hides everything under it; disabling `"physics/aabb"` keeps `"physics/velocities"` visible. Missing-key defaults to visible.

The ImGui Layers tab builds a tree of observed prefixes with `(visible/total)` counts and `[All]`/`[None]` buttons per node. `IDebugGeometrySource` producers are gated at the registry level — disabled layers don't even call `EmitGeometry`.

### ImGui HUD

`Blix.Runtime.OpenTK.ImGuiOverlayRenderer` lays out a single resizable window:

- **Status bar** (always visible) — `frame N • fps • ms • draws • tris • sel:<path>` plus Freeze/Unfreeze.
- **Tab bar** — `Selection` (visible only when picked; auto-focuses on a new pick) • `Perf` • `Stats` • `Timers` • `Events` (only if any) • `Controls` • `State` • `Layers` • `Custom` (only if any `IDebugUi` registered).
- Stats / Timers rows are text-only; click the `·` icon per row to expand a sparkline drawn from `DebugFrameHistory`.
- **`** (backtick) toggles the HUD entirely.

The **Perf tab** is the canonical "what's expensive?" surface: three small tables that roll up existing Stats + Timers without any extra instrumentation.

- **Phases** — top-level CPU phase timers (`frame`, `build-commands`, `execute`, `overlay`, `swap`, `run-debuggables`). The wall-clock split for one tick.
- **Passes** — auto-discovered from `passes/<name>/*` entries. Columns: Draws, Tris, CPU build ms, GPU ms (when the platform supports it).
- **Packs** — auto-discovered from `submeshes/<pack>/*`. Columns: Batches, Primitives, Tris, Opaque/Mask/Blend counts. The Primitives:Batches ratio shows the glTF batcher's effect at a glance.

Auto-discovery scans the latest frame's entries for matching scope prefixes — no hardcoded pack/pass names, generic across demos.

### Lifecycle

`Blix.Runtime.OpenTK.Window` creates a `DebugSystem` when the game loop implements `IDebuggable`. Per frame:

1. `BeginFrame` mints a `DebugContext`.
2. `Run(gameLoop)` walks registered contributors then the game-root `IDebuggable`. `IDebugGeometrySource` producers run in a separate gated sweep; selection sweep runs last when `SelectedPath != null`.
3. Game records render passes; `RenderPassBuilder.DrawIndexed` feeds the `IFrameRecorder` hook.
4. Window appends one runtime-owned `debug` pass when there are draw commands.
5. Render the ImGui HUD.
6. `EndFrame` records the frame timer, snapshots into history, fans out to sinks, clears `Current`.

### Frame debug packets

`IRenderer.Execute(RenderCommandList)` returns a `FrameDebugPacket` (separate from the diagnostics frame) describing what the backend just executed: pass names + resolved sizes + target surfaces + clear flags + per-draw pipeline/buffer/uniform/texture metadata. `Window` forwards each packet — along with a `ResourceRegistrySnapshot` from `IGraphicsDevice.SnapshotResources()` — to an optional `IRuntimeDiagnosticsSink` (in `Blix.Core`). `ConsoleFrameDebugSink` is a minimal sink that prints a per-frame summary at a configurable cadence.

### Resource registry

Every backend resource is named at creation time. `IGraphicsDevice.SnapshotResources()` returns a `ResourceRegistrySnapshot` with entries for vertex buffers, index buffers, textures, shader programs, pipelines, and render surfaces, plus `Find*(handle)` lookups for diagnostic correlation. Snapshots are built on demand and never cached.

Names are propagated to OpenGL as object labels via `glObjectLabel` (when `GL_KHR_debug` is available — typically not on macOS, but on Windows/Linux they surface in RenderDoc / Nsight / `KHR_debug` callbacks). Labels are re-applied on framebuffer-resize rebuilds so they survive viewport changes.

## Debug draw

`Blix.Render.DebugDraw` is the renderer-side line batch. Construction allocates a dynamic vertex buffer at max capacity (64 000 vertices = 32 000 lines), a pre-baked sequential index buffer, an embedded debug shader/pipeline, and a `Material` wrapping them.

Game code rarely owns a `DebugDraw` directly — it contributes commands through diagnostics, and the runtime's `Window` owns the actual instance:

```csharp
dbg.Draw.ViewProjection = projection * view;            // required (see footgun above)
dbg.Draw.Grid("World Grid", Vector3.Zero, 4.0f, 8, gray);
dbg.Draw.Frustum("Light Frustum", lightViewProjection, yellow);
dbg.Draw.Aabb("Bunny", worldBounds.Min, worldBounds.Max, green);
dbg.Draw.Sphere("Picked", center, 0.25f, magenta);
```

`Window` converts collected commands into one appended `debug` pass and submits through its runtime-owned `DebugDraw`. `Submit` uploads accumulated vertices, issues one `DrawIndexed`, and clears the buffer for next frame. **Depth-disabled** by default — lines always render on top of the scene.

Available primitives: `Line`, `Aabb`, `Grid`, `Frustum`, `Sphere`, `Plane`, `Ray`, `Capsule`, `Obb`, `Cross`, `Cone`, `Arrow`, `MeshWireframe`, `Normals`. Mesh primitives carry vertex/edge arrays by reference (producer-side immutability is the contract); the JSON dump emits summary counts only.

### Derived bounds

Imported mesh bounds are computed by `ObjImporter` into `MeshData.Bounds`; `IGraphicsDevice.CreateMesh(MeshData)` carries those into `Blix.Render.Mesh`. World-space debug AABBs are derived per frame:

```csharp
var worldBounds = BoundsTransform.Transform(mesh.Bounds, model);
debug.Draw.Aabb(name, worldBounds.Min, worldBounds.Max, color);
```

`BoundsTransform` (in `Blix.Diagnostics`) transforms the 8 local AABB corners and rebuilds the enclosing world AABB. Intentionally conservative for rotating meshes; avoids hand-authored demo-specific boxes.

## Asset pipeline

Source assets (`.png`, `.jpg`, `.obj`, `.glb`, `.gltf`, `.wav`, `.ttf`, `.json`) are converted to runtime intermediates by importers, then uploaded to the device. The engine deliberately doesn't let runtime code load every source format directly — that gets harder to manage once a half-dozen asset types exist.

### Three layers

1. **Source** — what an artist or tool produces.
2. **Importer** — `IAssetImporter<TOutput>.Import(AssetImportContext) → TOutput`. Pure decode.
3. **Runtime intermediate** — plain data records (`ImageData`, `MeshData`, `MaterialData`, `FontData`, `AudioClipData`, `GltfModel`). The device or `Blix.Render` turns them into resource handles.

**Convention:** runtime-intermediate types live with their *consumer* subsystem, not with the importer. `ImageData` lives in `Blix.Graphics.Images`, `AudioClipData` lives in `Blix.Audio`, `GltfModel` lives in `Blix` (where the skinned-rendering layer consumes it). `MeshData`, `MaterialData`, and `FontData` happen to live in `Blix.Assets` because their consumers (`Blix.Render`) reference `Blix.Assets` directly.

### AssetDatabase

```csharp
var assets = new AssetDatabase()
    .RegisterImporter(new TextureImporter())
    .RegisterImporter(new ObjImporter())
    .RegisterImporter(new MaterialImporter())
    .RegisterImporter(new GltfImporter())
    .RegisterImporter(new WavImporter())
    .RegisterImporter(new FontImporter())
    .LoadManifest(Path.Combine(AppContext.BaseDirectory, "Assets", "manifest.json"));

var concrete = assets.Load<MaterialData>(AssetId.Parse("materials/concrete"));
var ambient  = assets.Load<AudioClipData>(AssetId.Parse("audio/ambient_chord"));
var hudFont  = assets.Load<FontData>(AssetId.Parse("fonts/roboto"));
```

`AssetId.Parse` validates at construction (rejects empty, leading/trailing slashes, `..`, whitespace). Type safety: a method taking `AssetId` can't accept a stray string.

Caching is deferred at the database layer — every `Load` re-invokes the importer. Texture caching lives at the `MaterialResolver` layer (one upload per `AssetId`) — a separate responsibility from "loaded the source file" because GPU upload is what's expensive to repeat.

The asset database is intentionally GPU-free — it knows about disk → data, not data → GPU. There's no `Register<T>` direct-registration path either; every asset is declared in the manifest. Programmatically-generated runtime resources (procedural textures, env cubemap) bypass the asset pipeline entirely and become `TextureHandle`s directly via the device.

### Importers

| Name (dispatch key) | Output | Notes |
| --- | --- | --- |
| `texture.rgba8` | `ImageData` | PNG/JPEG via StbImageSharp. |
| `static-mesh.obj` | `MeshData` | OBJ parser. Position, normal (synthesized if missing), UV (spherical fallback). Indices `ushort`. |
| `material.json` | `MaterialData` | Pipeline-by-name + uniforms (float, Vector2/3/4) + texture bindings (by AssetId). |
| `audio.wav` | `AudioClipData` | RIFF/WAVE PCM. 8-bit unsigned + 16-bit signed, mono + stereo, any rate. Rejects float/24-bit/ADPCM. |
| `font.json` | `FontData` | TTF + per-font baked pixel sizes via StbTrueTypeSharp. Source file is a small JSON spec pointing at the TTF; see "Font + text" above. |
| `rigged-model.gltf` | `GltfModel` | Skinned mesh + skeleton + animations + materials + textures. SharpGLTF-backed. |

### Manifest

```json
{
  "assets": [
    { "id": "textures/cube",       "importer": "texture.rgba8",     "source": "cube_texture.png" },
    { "id": "models/cube",         "importer": "static-mesh.obj",   "source": "cube.obj" },
    { "id": "models/fox",          "importer": "rigged-model.gltf", "source": "models/fox.glb" },
    { "id": "materials/glass",     "importer": "material.json",     "source": "materials/glass.material.json" },
    { "id": "audio/ambient_chord", "importer": "audio.wav",         "source": "audio/ambient_chord.wav" },
    { "id": "fonts/roboto",        "importer": "font.json",         "source": "fonts/Roboto-Regular.font.json" }
  ]
}
```

Each entry's `importer` field selects from the importers registered via `RegisterImporter` — the importer's `Name` property is the dispatch key. `source` paths resolve relative to the manifest file. The manifest is the only registration path.

### Importer error format

Importer errors raise `AssetImportException(SourcePath, LineNumber?, Message)`. The exception's `ToString()` formats as `path:line: message` (or `path: message` when no line is relevant), and structured fields are available for tooling. `ObjImporter` throws a private `ObjFormatException` internally and wraps it at the `Import` boundary so source-path threading doesn't pollute every helper signature. `WavImporter` validates chunk sizes against the file length before reading them so malformed files raise a structured error instead of looping forever or throwing from `Array.Copy`.

### Material asset

`MaterialData(PipelineName, IReadOnlyList<ShaderUniform> Uniforms, IReadOnlyList<MaterialTextureBinding> Textures)` is the runtime intermediate.

```json
{
  "pipeline": "scene.lit",
  "uniforms": {
    "uMetallicFactor": 0.0,
    "uRoughnessFactor": 0.7,
    "uNormalScale": 1.0,
    "uTint": [0.91, 0.97, 1.0]
  },
  "textures": {
    "uTexture":   { "asset": "textures/concrete",   "slot": 0 },
    "uNormalMap": { "asset": "textures/concrete_n", "slot": 3 }
  }
}
```

Uniform values are typed by JSON shape: a number becomes `FloatUniform`, a 2-element array `Vector2Uniform`, 3-element `Vector3Uniform`, 4-element `Vector4Uniform`. Anything else throws an `AssetImportException`. Texture entries always carry an explicit `slot` so the JSON matches the sampler binding the pipeline's shader expects.

`MaterialResolver` (see "Engine-facing API" above) is what turns `MaterialData` into a runtime `Material`.

### Outstanding limits

- No caching at the `AssetDatabase` layer — every `Load` re-imports.
- No cooked binary formats; runtime parses source files directly.
- No hot reload.
- Sibling-asset lookup (an importer requesting another asset by ID) is not wired. `MaterialImporter` produces `MaterialTextureBinding(name, AssetId, slot)` and the consumer (`MaterialResolver`) resolves the asset reference. Works because the resolver is the right place to cache textures.

## Outstanding renderer work

Not in priority order; each lands when there's a real consumer.

- **Particles** — generic GPU/CPU emitter with sorted billboards, soft-particle depth fade, HDR + bloom integration. First consumer of the new shader library; will use `lib/noise.glsl` for procedural detail and follow the MRT G-buffer pattern.
- **Planar reflection probe** for ground-floor marble. Cleaner than SSR for hero floors (re-renders the scene flipped-Y at low resolution and samples it in the lit shader's marble path). ~150 LOC. SSR stays the general fallback.
- **Stencil support** — would let SSR / fog / volume passes mask via stencil bits instead of (or alongside) the material G-buffer.
- **Pipeline definitions as assets** — material JSON references pipelines by name; pipelines themselves are still constructed in code.
- **Configurable blend factors on `BlendState`** — currently fixed `SrcAlpha / OneMinusSrcAlpha` and `One / One`. Per-attachment write-masking would also live here.
- **`IndexFormat.UInt32`** — enables imported meshes >65 535 vertices.
- **Cooked binary mesh format** (offline import → `.meshbin` for fast load).
- **Per-material sampler control** on textures (resolver currently uses one `defaultSampler` for every cached texture).
- **`Sprite` runtime type + texture cache** so `SpriteData` resolves to GPU handles without manual bridging.
- **Depth-tested debug-draw mode** for occlusion-aware overlays.
- **Hot reload** for shaders. The preprocessor returns a stable source map, so the diagnostic story is in place; the missing piece is a watcher + rebuild path.

Done since the initial doc pass: GGX-prefiltered specular IBL + BRDF LUT (Karis split-sum), cascade shadow maps, screen-space reflections, dual-filter bloom, ACES/AgX/Reinhard/Neutral tonemap, material G-buffer, GLSL `#include` preprocessor with `#pragma once` + `#line` directives, `ShaderLoader`, the `Blix.Shaders` library.
