# Renderer

Blix has one active graphics backend and several renderer compositions. This page separates
the contracts that applications can reuse from the rendering choices made by
Studio and from the active research in Vulkan Sponza.

For adjacent concerns, see [Architecture](architecture.md) for project
boundaries and the binding model, [Assets](assets.md) for cooking and deferred
loading, [Workflow](workflow.md) for the supported commands, and
[Blix](blix.md) for the game-facing loop, views, cameras, and lights.

## Rendering roles

There is deliberately no engine-owned `SceneRenderer` or universal default
pipeline. An application declares its frame and records its draws explicitly.
Four roles make that practical without turning every application into a copy of
the same renderer:

| Role | Owner | Contract |
| --- | --- | --- |
| Reusable mechanisms | `Blix.Graphics`, `Blix.Graphics.Vulkan`, `Blix.Render`, `Blix.Shaders` | Commands, resources, graph execution, binding, batches, and shader vocabulary. No authored look or mandatory pass topology. |
| Application pipeline | Each game, demo, or tool | Chooses passes, shaders, formats, quality, and presentation for its own needs. |
| Studio reference rendering pipeline | `Blix.Tools.Studio` | Optional coherent rendering setup for model and rig inspection. `StudioLook` owns its authored defaults. |
| Research renderer | `Blix.Demos.VulkanSponza` | Heavy-scene experiments, measurements, diagnostics, and promotion decisions. Its graph is not an engine promise. |

The distinction is the main rule for reading this repository: the presence of
a technique in Sponza does not make it a shared feature, and the presence of a
default in `StudioLook` does not impose that choice on a game.

## Layer map

- **`Blix.Graphics`** is the backend-independent command and resource
  vocabulary: opaque handles, `IGraphicsDevice`, `RenderCommandList`, draw and
  dispatch commands, pipeline descriptions, render surfaces, vertex layouts,
  shader interfaces, and the GLSL preprocessor.
- **`Blix.Graphics.Vulkan`** is the only current backend. It owns Vulkan device
  and swapchain work, `RenderGraph`, SPIR-V reflection, `MaterialBindings`,
  transient descriptor and vertex storage, indirect drawing, compute dispatch,
  timing, and the live resource registry.
- **`Blix.Render`** contains higher-level helpers such as `Mesh`,
  `MeshBundler`, `FullscreenPass`, `PostChain`, `SpriteBatch`, `Font`,
  `ParticleBatch`, `InstanceBuffer`, `InstancedBatch`, and upload queues. It is
  engine-facing but currently Vulkan-backed; it is not a backend-neutral
  abstraction layer.
- **`Blix.Graphics.Images`** owns image decode, CPU tonemapping, environment
  conversion, probe baking, and cooked probe upload.
- **`Blix.Shaders`** is the shared, `blix_`-prefixed GLSL vocabulary included by
  application shaders.
- **Application projects** own shader programs, graph composition, culling,
  draw grouping, material policy, presentation, and the final visual result.

## Frame construction with `RenderGraph`

`RenderGraph` is a persistent frame topology. Setup declares resources and
passes, `Compile()` validates and allocates them, each frame records only the
work needed that frame, and `Execute()` emits the recorded graphics and compute
work.

```csharp
var graph = new RenderGraph(device);
var size = new MatchSwapchainGraphSize();

var hdr = graph.ColorTarget("hdr", TextureFormat.Rgba16F, size);
var depth = graph.DepthTarget("depth", size);
var shadow = graph.DepthTarget("sun-shadow", new FixedGraphSize(2048, 2048));

var shadowPass = graph.GraphicsPass("shadow")
    .Depth(shadow, LoadOp.Clear, StoreOp.Store)
    .Shader(shadowInterface)
    .Handle;

var litPass = graph.GraphicsPass("lit")
    .Target(hdr, LoadOp.Clear, StoreOp.Store)
    .Depth(depth, LoadOp.Clear, StoreOp.Store)
    .Read(shadow)
    .Shader(litInterface)
    .Handle;

graph.Compile();

// Per frame:
graph.Pass(shadowPass, pass => RecordCasters(pass));
graph.Pass(litPass, pass => RecordScene(pass));
graph.Execute(commandList);
```

Graph resources are persistent across frames. `FixedGraphSize` keeps an exact
size; `MatchSwapchainGraphSize(scale)` follows swapchain recreation at the
requested scale. Current graph-owned resource factories cover two-dimensional
colour and depth targets plus depth cubes. After compilation, callers retrieve
the concrete handles with `GetColorTexture`, `GetDepthTexture`,
`GetDepthCubeTexture`, and `GetPassSurface`.

Pass declaration order is execution order. Read and write edges validate the
topology and drive image transitions and compute barriers; they do not schedule
or reorder passes. A pass that is not recorded in a frame is skipped. This is
how the two TAA parity passes in Sponza share one declared topology while only
one runs on a given frame.

Compilation rejects invalid topology early: duplicate or empty names, missing
attachments, undeclared shaders, ordinary reads before a producer, incompatible
resolves, and other resource errors. A pipeline is created against the
synthetic surface returned for its pass, so attachment formats and sample counts
remain part of pipeline compatibility.

### Graphics, compute, and external resources

A graphics pass declares colour targets, one depth target, resolves, sampled
reads, and the shader interfaces it permits. A compute pass declares sampled or
read-only inputs, storage-image writes, and one shader interface; per-frame work
is recorded with `graph.Dispatch(...)` and is interleaved with graphics work in
declaration order.

Graph colour targets can serve as compute storage images. Device-created
storage textures, including three-dimensional textures, and buffers are not
graph-owned resources. Applications may use them in dispatch bindings, but
their lifetime and any ordering not represented by graph edges remain the
caller's responsibility. Sponza's froxel and probe atlases are the important
current example of that boundary.

### MSAA and resolves

A multisampled attachment cannot be sampled as an ordinary texture. Render to
the multisampled target, resolve colour into a single-sample colour target, and
resolve depth into a single-sample depth target when a later pass needs to read
it:

```csharp
var hdrMsaa = graph.ColorTarget("hdr-msaa", TextureFormat.Rgba16F, size, samples: 4);
var hdr = graph.ColorTarget("hdr", TextureFormat.Rgba16F, size);
var depthMsaa = graph.DepthTarget("depth-msaa", size, samples: 4);
var depth = graph.DepthTarget("depth", size);

var scene = graph.GraphicsPass("scene")
    .Target(hdrMsaa, LoadOp.Clear, StoreOp.Store)
    .ResolveColor(hdr)
    .Depth(depthMsaa, LoadOp.Clear, StoreOp.Store)
    .ResolveDepth(depth)
    .Shader(sceneInterface)
    .Handle;
```

Colour and depth sample counts must agree. Clamp an application request to
`VulkanGraphicsDevice.MaxMsaaSamples`. Depth uses `SAMPLE_ZERO` resolution: an
average across a silhouette would invent a depth surface that was never drawn.

### Cross-frame history

`ReadHistory(resource)` means “sample the previous frame's contents.” It keeps
the real transition and barrier but exempts that read from the usual
producer-before-consumer ordering check. Temporal accumulation therefore has
two obligations outside the graph:

- gate history blending until the target has been written once; and
- invalidate history after resize or resource recreation.

`MatchSwapchainResourceGeneration` changes after the graph reallocates any
`MatchSwapchainGraphSize` resources. Cache it and refuse temporal history for
one frame whenever it changes. This includes same-sized swapchain recreation,
such as a present-mode change; a window-size callback alone is not sufficient.

Sponza follows that generation for GTAO accumulation and its two-target TAA
ping-pong. Its froxel history is device-owned rather than graph-owned, so the
application invalidates that separately when it replaces the froxel grid. Use
an ordinary `Read` whenever the producer is in the current frame.

## Recording, binding, and dynamic data

Inside `graph.Pass`, a `RenderPassBuilder` records indexed, instanced, and
indirect draws. The basic shape is explicit:

```csharp
pass.DrawIndexed(vb, ib, pipeline, indexCount, uniforms, textures);
pass.DrawIndexed(vb, ib, pipeline, indexCount, uniforms, textures, material);
pass.DrawIndexed(
    vb, ib, pipeline, indexCount, uniforms, textures, material,
    pushConstants, indexOffset, vertexOffset);
```

SPIR-V reflection supplies each `ShaderInterface`: descriptor sets, std140
uniform layouts, and push-constant ranges. `MaterialBindings` allocates one
reflected set and writes uniforms or textures by their reflected names and
bindings. There is no second hand-maintained binding table.

Sets are grouped by lifetime rather than by object type: frame-global,
per-pass, per-material, and per-draw. Small per-draw values use push constants;
descriptor-backed values use transient or persistent material bindings.
Recorded push data is copied, so callers may safely reuse scratch storage while
recording subsequent draws.

Fresh per-frame data has two distinct homes:

- `AllocVertices` returns a `TransientVertexSlice` from a frames-in-flight ring.
  It is for descriptor-less vertex/index data bound by offset. `SpriteBatch`,
  debug lines, and `ParticleBatch` use it.
- `MaterialBindings` owns descriptor-backed uniform and storage buffers.
  `InstanceBuffer` and bone palettes use it because each frame slot needs both
  storage and a correct descriptor.

Do not update one long-lived dynamic vertex buffer under in-flight GPU reads,
and do not rebuild descriptor machinery inside the transient vertex arena.

`DrawIndexedIndirect` consumes GPU draw structs; Sponza groups compatible draws
and maintains per-cascade command buffers. `DrawIndexedInstanced` is the lower
level primitive behind `InstanceBuffer` and `InstancedBatch`. Those helpers own
instance transport and batching, not a shader or lighting policy.

## Resources, lifetime, and residency

The device registry exposes live buffers, textures, shader programs, pipelines,
and surfaces for diagnostics. Texture entries report dimensions, format, byte
size, kind, mip count, and `Pending`, `Streaming`, or `Resident` state.

That residency describes the progressive upload path, not render-graph
availability. A cooked mip chain may allocate its stable texture handle first,
upload the smallest mip, and sharpen over later frames. Render targets,
compute-written storage images, and textures uploaded in full are resident
immediately. See [Assets](assets.md) for the loading lifecycle, registry
identity, and upload budgets.

Every owner still destroys what it creates. Be careful with aliased handles:
for example, a procedural environment can return one cube through several
semantic fields, so teardown must deduplicate rather than destroy by field.

## Shared rendering primitives

The reusable layer is intentionally made of small pieces rather than a hidden
scene pipeline:

| Primitive | Responsibility | Deliberately does not own |
| --- | --- | --- |
| `Mesh`, `CreateMesh` | GPU vertex/index buffers, count, name, and local bounds | Materials, transforms, culling |
| `MeshBundler` | Packs primitives into shared buffers and preserves draw subranges | Draw policy or LOD selection |
| `FullscreenPass` | Records one fullscreen triangle with caller bindings | Shader, effect, or presentation policy |
| `PostChain` | Declares and records a linear image-to-image graph chain | Composite, exposure, tonemap |
| `SpriteBatch`, `Font` | Texture-partitioned quad and text batching | Game UI policy |
| `ParticleBatch` | CPU particle evolution, depth sorting, billboard expansion | Particle shader, soft-depth policy, post stack |
| `InstanceBuffer` | Frames-in-flight replicated instance SSBO | Mesh, pipeline, scene ownership |
| `InstancedBatch` | Stages instances and emits one instanced draw | GPU resources or shader |

`PipelineDescription` remains the immutable draw-state boundary: program,
vertex layout, topology, depth and raster state, blends, alpha-to-coverage, and
the compatible render target. Changing culling or blending means a distinct
pipeline; changing a live uniform does not.

## Shaders

Shaders are authored in GLSL and compiled offline to SPIR-V. The shared build
target expands includes through `GlslPreprocessor`, invokes `glslc`, and emits
reflection sidecars. Include processing supports recursive includes,
cycle detection, Blix-owned `#pragma once`, canonical source identity, injected
defines after `#version`, and `#line` mappings for useful compiler errors.

`src/Blix.Shaders/` is shared vocabulary, not a monolithic shader framework:

| Include | Shared vocabulary |
| --- | --- |
| `pbr.glsl` | Cook-Torrance metallic/roughness BRDF |
| `ibl.glsl` | Diffuse irradiance and split-sum specular ambient |
| `shadow.glsl` | Shared soft sun-shadow sampling and cascade selection |
| `tonemap.glsl` | ACES, AgX, Reinhard, and neutral curves |
| `fullscreen.glsl` | Fullscreen-triangle vertex synthesis |
| `bloom.glsl` | Bright extraction and separable Gaussian blur |
| `froxel.glsl` | Shared froxel addressing and integration helpers |
| `probe_volume.glsl`, `sky_visibility.glsl`, `octahedral.glsl` | Probe-volume and directional-field sampling |
| `sheen.glsl`, `coverage.glsl`, `noise.glsl` | Material sheen, coverage shaping, and stochastic helpers |

Application shaders stay with the application. A helper should graduate into
`Blix.Shaders` only when it has a stable reusable contract, not merely because a
large experiment uses it.

## Studio reference rendering pipeline

`StudioRenderer` is the optional pipeline used by Blix's own model and rig
tools. Its current composition includes:

- three fitted, texel-snapped sun-shadow cascades;
- optional depth pre-pass, off by default because it did not pay for the small
  inspection stage;
- HDR PBR lighting for static and skinned meshes;
- procedural or cooked-probe IBL and a BRDF LUT;
- opaque, cutout, double-sided, and blended material routing;
- configurable MSAA with colour and depth resolves;
- tonemapped presentation plus a separate half-resolution inspection view; and
- a pre-compile extension window for a tool to add graph passes without forking
  the renderer.

`StudioLook` is the authored policy boundary. Its defaults are Blix's reference
look: sun, environment, shadow fit, exposure, tonemap, MSAA, and related values.
Most are live per-frame settings. Members marked
`[Tune(Structural = true)]` must be set before graph and pipeline construction;
changing one after the renderer seals the structure is reported rather than
pretending the control took effect.

Studio owns the composition and its defaults, not the underlying capabilities.
A game can reuse all, some, or none of it. This is why “Studio reference
rendering pipeline” is the durable name and `DefaultRenderer` is not.

## Application-owned pipelines

The smaller applications are focused examples of explicit composition:

| Application | What it demonstrates |
| --- | --- |
| `Blix.Demos.VulkanGraph` | Small render-graph composition |
| `Blix.Demos.VulkanInstanced` | `InstanceBuffer`/`InstancedBatch` at scale |
| `Blix.Demos.VulkanLit` | PBR, IBL, multiple shadow types, HDR, bloom, and skinning |
| `Blix.Demos.VulkanParticles` | Depth pre-pass, soft particles, HDR bloom, and present |
| `Blix.Demos.Pong` | Sprites, fonts, supersampled offscreen rendering, and CRT post-effect |
| `Blix.Demos.Runner` | Game-owned instanced world rendering and skinned characters |
| `Blix.Demos.Bulwark`, `RTSGame` | Larger game-owned render policy and diagnostics |

They are examples and proving consumers, not stages in a renderer inheritance
hierarchy.

## Vulkan Sponza research renderer

Vulkan Sponza is being rebuilt as a heavy-scene rendering instrument. Its live
graph is approximately:

```text
three shadow cascades
    -> sky/probe compute work and froxel fog
    -> depth + normal pre-pass and MSAA resolves
    -> Hi-Z pyramid
    -> GTAO -> temporal/spatial denoise
    -> half-resolution incident light -> full-resolution resolve
    -> HDR lit scene
    -> parity-selected TAA resolve
    -> present and diagnostics
```

The exact graph continues to move. The important current research areas are:

- GPU-driven indirect submission, draw grouping, culling, and screen-space
  error LOD over cooked mesh chains;
- cascade fitting plus caster/receiver reasoning and survivor counts;
- a depth/normal pre-pass, six-level Hi-Z pyramid, half-resolution GTAO,
  temporal history, bilateral reconstruction, and denoising;
- baked sky visibility and occupancy/albedo volumes;
- runtime probe injection, usage marking, sleeping, carry/ping-pong policy,
  transport, and indirect-light sampling;
- a half-resolution incident-light field and full-resolution resolve;
- colour TAA and temporally accumulated volumetric froxel fog;
- extended material work including sheen and transmission; and
- debug views, command-line A/Bs, pass timings, resource and draw censuses,
  captures, and CPU/path-traced reference checks.

Some storage textures and probe resources are device-owned rather than graph
resources, so Sponza also exposes where the graph contract is not yet broad
enough. Local comments retain present contracts and measurements that still
govern the implementation; completed experiments and superseded alternatives
belong in dated reports or plans. Neither is automatically normative engine
documentation.

When a Sponza result is ready to promote, move the reusable mechanism or shader
vocabulary into an engine project, give it another credible consumer, and leave
Sponza with only the scene-specific composition and policy.

## Technique ownership at a glance

| Scope | Current examples |
| --- | --- |
| Shared mechanism or vocabulary | Reflected binding, graphics/compute graph passes, history reads, MSAA resolves, transient vertices, instancing, indirect draw, mesh bundling, PBR/IBL/shadow/tonemap helpers, fullscreen work, sprites, particles |
| Studio-authored reference | Three-cascade inspection lighting, procedural fallback environment, reference exposure/tonemap/MSAA, optional pre-pass, inspection viewport |
| Application-owned | Point and spot shadows in Vulkan Lit, bloom chains, Pong CRT presentation, game culling and draw grouping |
| Sponza research | Hi-Z/GTAO/TAA, probe transport and incident field, froxel fog, heavy-scene LOD/culling, material experiments, A/B and reference machinery |

Screen-space reflections, dual-filter/Kawase bloom, and the MRT material
G-buffer from the retired OpenGL renderer are not present in the current Vulkan
renderer. Vulkan bloom uses the separable Gaussian path; current reflections
come from environment lighting rather than SSR.

## Where to start

- To understand the minimum graph lifecycle, read
  `src/Demos/Blix.Demos.VulkanGraph/`.
- To build a game renderer, start from the mechanisms in `Blix.Graphics`,
  `Blix.Graphics.Vulkan`, and `Blix.Render`, then compose passes in the
  application.
- To give a content tool a coherent inspection view, adopt
  `Blix.Tools.Studio` and set structural `StudioLook` choices before loading.
- To investigate or measure a rendering technique, use Vulkan Sponza and keep
  the experiment local until its reusable boundary is demonstrated.
- To understand when a texture is usable versus fully sharp, read
  [Assets](assets.md), especially the deferred CPU and progressive GPU paths.
