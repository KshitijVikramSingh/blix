# Renderer

The Vulkan renderer reference — the layers between game code and the GPU, and the techniques they implement. This is the complement to the other two docs: [`architecture.md`](architecture.md) covers the project graph, host contracts, and the **binding model**; [`blix.md`](blix.md) covers the game-facing layer (loop, scene, cameras, lights). This doc covers what sits in the middle: how you declare a frame, record draws, write shaders, and which rendering techniques ship.

The renderer is one layer of a larger pipeline — cooking, streaming, LOD, and bundling all feed it, and on a heavy scene those are what make a frame affordable (see [`README.md`](../README.md) and the asset-pipeline notes in [`architecture.md`](architecture.md)). This doc covers the rendering layer itself. There is no `SceneRenderer` that owns read→cull→draw — game code composes a frame itself out of the primitives below. `VulkanLit` and `VulkanSponza` (`src/Blix.Demos.VulkanLit/`, `src/Blix.Demos.VulkanSponza/`) are the working references; the shader files cited throughout are the authoritative implementation of each technique.

## Layers

- **`Blix.Graphics`** — the graphics command language. Opaque handles (`PipelineHandle`, `VertexBufferHandle`, `TextureHandle`, `MaterialHandle`, …), `PipelineDescription`, render surfaces, the `RenderCommandList`, vertex layouts, shader sources, and the GLSL include preprocessor. Backend-neutral.
- **`Blix.Graphics.Vulkan`** — the backend (the sole `IGraphicsDevice`). Owns the instance/device/swapchain, the `RenderGraph`, `MaterialBindings`, per-draw transient descriptor pools, and SPIR-V reflection. On macOS it runs through MoltenVK.
- **`Blix.Render`** — engine-facing rendering helpers: `Mesh`, `MeshBundler`, `ResourceUploader`, `AsyncLoadQueue<T>`, and the 2D path (`SpriteBatch` + `Font`).
- **`Blix.Graphics.Images`** — image decode (StbImageSharp) and the HDR IBL bake (`EquirectangularToCubemap`, `PbrIblBaker`, `BlixProbe`).
- **`Blix.Shaders`** — the shared GLSL library (`#include`d by demo shaders).

## The render graph

`RenderGraph` (`src/Blix.Graphics.Vulkan/RenderGraph.cs` + `RenderGraph.Builders.cs`) is declarative: you declare resources and passes once at setup, `Compile()` once, then `Execute()` per frame. The graph infers the image-layout barriers between passes from the declared `Read`/`Target`/`Write` edges.

**Declare resources:**

```csharp
var sceneColor = graph.ColorTarget("scene", TextureFormat.Rgba16F,
    new MatchSwapchainGraphSize(), samples: 4);          // 4× MSAA HDR target
var sceneDepth = graph.DepthTarget("depth", new MatchSwapchainGraphSize(), samples: 4);
var sunShadow  = graph.DepthTarget("sun.shadow", new FixedGraphSize(2048, 2048));
var pointCube  = graph.DepthCube("point.shadow", faceSize: 1024);  // .Face(0..5)
```

`GraphSize` is either `FixedGraphSize(w, h)` or `MatchSwapchainGraphSize(scale)` (resizes with the window).

**Declare passes** with the fluent builder, then `Compile`:

```csharp
var litPass = graph.GraphicsPass("lit")
    .Target(sceneColor, LoadOp.Clear, StoreOp.Store)
    .ResolveColor(presentColor)          // MSAA resolve destination
    .Depth(sceneDepth, LoadOp.Clear, StoreOp.Store)
    .Read(sunShadow)                     // sampled as a texture this pass
    .Shader(litInterface, skyInterface)  // shaders this pass supports
    .Build();

var fogPass = graph.ComputePass("froxel")
    .Read(sceneDepth).Write(froxelVolume).Shader(froxelInterface).Build();

graph.Compile();
```

After `Compile()`, query sampleable handles and synthetic render surfaces:
`GetColorTexture(handle)`, `GetDepthTexture(handle)`, `GetDepthCubeTexture(cube)`, `GetPassSurface(pass)` (pass that handle as `PipelineDescription.RenderTarget`).

**Record per frame** inside `OnRender`, then execute:

```csharp
graph.Pass(litPass, builder => { /* record draws — see below */ });
graph.Dispatch(fogPass, new DispatchCommand(froxelPipeline, gx, gy, gz, uniforms, textures));
graph.Execute(commandList);
```

`VulkanLit`'s topology, for reference:
`sun-shadow → spot0-shadow → spot1-shadow → 6× point-cube-faces → lit-scene → bloom-bright → bloom-blurH → bloom-blurV → present`.

## Recording draws

Inside a `graph.Pass` scope you get a `RenderPassBuilder` (`src/Blix.Graphics/RenderCommandList.cs`). The core call is `DrawIndexed`, with overloads that add a per-material descriptor set, a per-draw set, push constants, shared-buffer sub-ranges, and a scissor:

```csharp
builder.DrawIndexed(vb, ib, pipeline, indexCount, uniforms, textures);
builder.DrawIndexed(vb, ib, pipeline, indexCount, uniforms, textures, material);          // + set 2
builder.DrawIndexed(vb, ib, pipeline, indexCount, uniforms, textures, material,
    pushConstants, indexOffset, vertexOffset);                                            // shared VB/IB sub-range
```

For GPU-driven rendering, `DrawIndexedIndirect` issues one `vkCmdDrawIndexedIndirect` over a buffer of draw structs — group objects by `(pipeline, material)` and emit one per group. `VulkanSponza` fills the indirect buffer per cascade after frustum culling.

For drawing one mesh many times, `DrawIndexedInstanced` issues a single `vkCmdDrawIndexed(instanceCount=N)`; the vertex shader reads `gl_InstanceIndex` into a per-instance storage buffer for its transform/tint. Two `Blix.Render` layers wrap this, kept deliberately separate: **`InstanceBuffer`** is the data layer — a frames-in-flight-replicated set-3 SSBO of `InstanceData {mat4 model; vec4 tint}`, exposing `Write(span)`, its `Material` handle, and the static `Slot` contract a shader composes into its interface. **`InstancedBatch`** is the ergonomics layer — constructed with an already-built `(mesh, pipeline, InstanceBuffer)`, it stages instances via `Begin(push)/Add/End` and records the one draw. It owns no GPU resources and no shader: the caller brings the pipeline (and thus the material — lighting, fog, whatever), so the engine ships no built-in instanced shader. `Blix.Demos.VulkanInstanced` is the 5000-cube proof gate; `Blix.Demos.Runner` draws its whole world (tiles, obstacles, coins) through per-mesh `InstancedBatch`es.

**The binding model.** Materials bind by *reflected slot*, not by name-keyed bags. `CreateMaterial(program, setIndex, framesInFlight)` allocates a `MaterialBindings` against one SPIR-V-reflected descriptor set; `SetUniform(binding, "uName", value)` / `SetTexture(binding, handle)` write it by name, and `.Handle` is the `MaterialHandle` a draw (or `GameObject`) carries. Sets are organised by lifetime — set 0 per-frame, set 1 per-pass, set 2 per-material, set 3 per-draw — and per-draw data rides push constants (≤256 B) or a transient descriptor pool refilled each frame. Full detail: [`architecture.md` → The Vulkan binding model](architecture.md#the-vulkan-binding-model).

**Per-frame transient data — two substrates, one boundary.** Dynamic data uploaded fresh every frame has exactly two homes, split by whether it needs a descriptor:

- **Vertex/index data bound by offset → the transient arena.** `IGraphicsDevice.AllocVertices(span, stride)` sub-allocates from a ring of host-visible vertex buffers (`MaxFramesInFlight + 1` slots, mirroring the indirect ring) and returns a `TransientVertexSlice`; the draw binds the buffer at `slice.ByteOffset` (`DrawIndexedCommand.VertexBufferByteOffset`) and uses base-0 indices. This is the race-free replacement for the old "own one `Dynamic` vertex buffer and re-`UpdateVertexBuffer` it every frame" pattern, which collided with in-flight GPU reads. `SpriteBatch` and `VkLineDrawer` ride it; it's also what a `ParticleBatch`-style consumer expands its billboards into. The arena has **no descriptor** — a slice is just a `(buffer, offset, length)` triple.

- **Per-frame SSBO/UBO that needs a descriptor → `MaterialBindings`.** Per-instance transforms (`InstanceBuffer`, a set-3 SSBO) and skinned bone palettes are frames-in-flight-replicated *and carry their own descriptor set*. These stay in `MaterialBindings` — it already owns the buffer **and** the descriptor write correctly. They do **not** belong in the transient arena: pushing a descriptor-backed buffer through the arena would mean rebuilding the per-frame descriptor machinery `MaterialBindings` already provides, for no gain. The rule: *arena = descriptor-less vertex/index data bound by offset; `MaterialBindings` = descriptor-backed per-frame storage.*

## Meshes, pipelines, vertex types

`Mesh` (`Blix.Render`) bundles a vertex buffer + index buffer + count + mesh-local AABB under a name; build one from a `MeshData` via `IGraphicsDevice.CreateMesh(...)`, or bundle many primitives into one shared `(VB, IB)` with `MeshBundler.Bundle(...)` (draws become sub-ranges via `indexOffset`/`vertexOffset`).

`PipelineDescription` (`src/Blix.Graphics/PipelineDescription.cs`) is the immutable draw state: `ShaderProgram`, `VertexLayout`, `Topology`, `DepthState`, `RasterizerState`, a list of `BlendState`, an optional `RenderTarget` (null → swapchain — its attachment formats must match the render-pass it's drawn into), and `AlphaToCoverage` (antialiased alpha-cutout edges under MSAA).

Vertex layouts (`src/Blix.Graphics/`): `VertexPosition3Color`, `VertexPosition3Texture`, `VertexPosition3NormalTexture` (standard lit), `VertexPosition3NormalTangentTexture` (PBR + normal maps), `VertexPosition3NormalTextureSkin4Tangent` (+ 4-bone skinning), plus the 2D `VertexPositionTexture` / `VertexPosition3TextureColor` (sprites).

**Render surfaces & attachments** (`src/Blix.Graphics/RenderSurface.cs`): a surface is a set of color attachments + an optional depth attachment. Color formats include `Rgba8`, `Rgba16F` / `R11G11B10F` (HDR), and `Bc7` (compressed); depth is `D24` / `D32F`. Depth attachments can be a renderbuffer, a sampleable depth texture, or a single cubemap face (`DepthCubeFace`) for point-light shadows. MSAA targets declare `samples > 1` and resolve via the pass's `ResolveColor`.

## Shaders

Shaders are authored in GLSL and compiled offline to SPIR-V with `glslc`. The Vulkan binding model is then **reflected** from the compiled `.spv` (spirv-cross JSON sidecars) into a `ShaderInterface` — descriptor sets + std140 UBO layouts + push-constant ranges — by `ShaderReflection` (`src/Blix.Graphics.Vulkan/ShaderReflection.cs`). There is no hand-maintained binding table to drift out of sync with the shader source.

`GlslPreprocessor.PreprocessDetailed` resolves `#include "lib/<file>.glsl"` (recursive, cycle-detected, `#pragma once`) and emits `#line` directives so compile errors report the original file + line. `ShaderLoader.LoadVertexFragment(vert, frag, includeDirs?, defines?)` bundles read + preprocess + source-map plumbing; `defines` injects `#define` lines after `#version` so one library function serves multiple variants.

The shared library (`src/Blix.Shaders/`, every symbol `blix_`-prefixed) is deliberately small — three files copied into each demo's `Shaders/lib/`:

| File | Provides |
| --- | --- |
| `pbr.glsl` | Cook-Torrance BRDF — `blix_distributionGGX`, `blix_geometrySmith`, `blix_fresnelSchlick` |
| `tonemap.glsl` | `blix_acesFilm` + a `blix_tonemap(hdr, mode)` selector (ACES / AgX / Reinhard / Neutral) |
| `noise.glsl` | Pseudorandom jitter (PCF rotation, banding decorrelation) |

Other GLSL helpers are demo-local includes rather than shared library — e.g. `src/Blix.Demos.VulkanLit/Shaders/` carries `ibl.glsl` (diffuse + split-sum specular sampling), `brdf.glsl` (BRDF LUT integration), `shadows.glsl` (depth compare + PCF), and `normal_mapping.glsl`. A helper graduates into `Blix.Shaders` when a second demo needs it.

## Rendering techniques

All techniques run on Vulkan; the shader files below are the source of truth.

| Technique | Where it lives |
| --- | --- |
| PBR (metallic-roughness) | `lit.frag` + `pbr.glsl` (VulkanLit, VulkanSponza) |
| IBL — diffuse irradiance | irradiance cube sampled in `lit.frag` |
| IBL — specular (split-sum) | `uPrefilteredEnv` + `uBrdfLut` in `lit.frag` (`specularIBL = prefiltered · (F0·lut.x + lut.y)`) |
| HDR IBL bake | `EquirectangularToCubemap` + `PbrIblBaker`; cooked into `.blixprobe` (`BlixProbe`) — irradiance + GGX-prefiltered specular + BRDF LUT |
| Cascade shadow maps (3-cascade) | `shadow.vert/.frag`; `lit.frag` samples the cascade array (VulkanSponza) |
| Point cubemap shadows | `point_shadow.vert/.frag` (linear distance), sampled as `samplerCube` (VulkanLit) |
| Spot shadows | perspective shadow + `shadows.glsl` compare (VulkanLit) |
| PCF filtering | `shadows.glsl` (VulkanLit); rotated-Vogel PCF (VulkanSponza) |
| Alpha-cutout shadow casters | `depth_prepass_mask.frag` (alpha threshold + alpha-to-coverage) |
| Tonemap | `tonemap.glsl` — ACES (with an AgX grade option) |
| Bloom | separable-Gaussian chain: `bloom_bright.frag` → `bloom_blur.frag` (H then V) |
| Froxel volumetric fog | `froxel.comp` compute pass, froxel grid (VulkanSponza, `--fog`) |
| Depth pre-pass | `depth_prepass.frag` / `depth_prepass_mask.frag` (VulkanSponza) |
| Glass / transmissive | Fresnel + alpha-blend pipeline in `lit.frag` (no refraction) |
| GPU-driven indirect draw | `DrawIndexedIndirect`, per-material multi-draw (VulkanSponza) |
| Per-frame transient vertices | `AllocVertices` → `TransientVertexSlice`, ring of host-visible buffers bound by offset (SpriteBatch, VkLineDrawer, ParticleBatch) |
| Billboard particles | `ParticleBatch` (CPU sim, colour/size-over-life) → arena slice; VulkanParticles (fountain · explosion · vortex) |
| Per-instance instancing | `DrawIndexedInstanced` + `InstanceBuffer` (set-3 SSBO) / `InstancedBatch` (VulkanInstanced, Runner) |
| Skeletal animation (GPU skinning) | bone-palette set-3 SSBO; `skinned_lit.vert` (VulkanLit), `skinned.vert` (Runner) |
| Screen-space-error LOD | `.blixmesh` per-level geometric error; runtime selects by SSE (VulkanSponza) |
| Geometry bundling | `MeshBundler` packs primitives into one shared `(VB, IB)`; draws are sub-ranges |

**Not ported from the GL renderer.** Screen-space reflections (SSR), dual-filter (Kawase) bloom, and the MRT material G-buffer that fed SSR were GL-only techniques and did not survive the OpenGL sunset. Bloom on Vulkan is the separable-Gaussian chain above; reflections come from prefiltered-environment IBL, not SSR.

## Sprites, text, and UI

The 2D path (`src/Blix.Render/SpriteBatch.cs`, `Font.cs`) is rebuilt on the Vulkan binding model — one alpha-blended pipeline, texture at set 0, view-projection via push constant. `Pong` is the proving ground.

```csharp
spriteBatch.Begin(viewProjection);              // optionally a SpriteSortMode
spriteBatch.Draw(texture, position, size, sourceRect, color, depth);
spriteBatch.End(passBuilder);                   // emits one batched draw per texture partition
```

`Font.Upload(device, fontData)` uploads a baked glyph atlas; `NearestSize(pixelSize)` picks the closest baked size. Text draws as quads through the same `SpriteBatch`. Pong renders sprites + text into a 2×-supersampled offscreen `RenderGraph` target, then a fullscreen CRT post-FX pass (`postfx.vert/.frag`) grades it onto the swapchain.

## Asset pipeline, diagnostics, debug draw

These are backend-neutral and documented where they're owned:

- **Asset pipeline** — `AssetDatabase` + importers (`Blix.Assets`), the cooked `.blixmesh` / `.blixtex` / `.blixprobe` formats, and the runtime streaming primitives (`MeshBundler`, `ResourceUploader`, `AsyncLoadQueue<T>`, `GltfTextureLoader`). See [`README.md` → Cooked asset pipeline](../README.md) and the "Asset pipeline" arrows in [`architecture.md`](architecture.md).
- **Diagnostics & debug draw** — the contribution-based `DebugSystem` (`Blix.Diagnostics`): `IDebuggable.Debug(DebugContext)`, the Values/Controls/Draw/Stats/Timers/Events channels, selection + picking, and live tuning via `//@tune lo..hi = default` (GLSL) or `[Tune(min, max)]` (C#). The overlay panels live in `Blix.Diagnostics.Overlay` and render through `VkImGuiRenderer`. See the Diagnostics rows in [`architecture.md` → Where to find things](architecture.md#where-to-find-things).
