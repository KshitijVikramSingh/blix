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
| Reusable mechanisms | `Blix.Graphics`, `Blix.Render`, `Blix.Shaders`, realised by `Blix.Graphics.Vulkan` | Commands, resources, graph execution, binding, batches, and shader vocabulary. No authored look or mandatory pass topology. |
| Application pipeline | Each game, demo, or tool | Chooses passes, shaders, formats, quality, and presentation for its own needs. |
| Studio reference rendering pipeline | `Blix.Tools.Studio` | Optional coherent rendering setup for model and rig inspection. `StudioLook` owns its authored defaults. |
| Research renderer | `Blix.Demos.VulkanSponza` | Heavy-scene experiments, measurements, diagnostics, and promotion decisions. Its graph is not an engine promise. |

The distinction is the main rule for reading this repository: the presence of
a technique in Sponza does not make it a shared feature, and the presence of a
default in `StudioLook` does not impose that choice on a game.

## Layer map

- **`Blix.Graphics`** is the backend-independent command and resource
  vocabulary: opaque handles, `IGraphicsDevice` (the whole device, so nothing
  casts to a backend), `RenderCommandList`, draw and dispatch commands,
  pipeline descriptions, render surfaces, vertex layouts, `RenderGraph`'s
  declaration and validation, SPIR-V reflection into `ShaderInterface`,
  `IMaterialBindings`, and the GLSL preprocessor.
- **`Blix.Graphics.Vulkan`** is the only current backend, and only
  `Blix.Runtime.Silk` references it. It owns Vulkan device and swapchain work,
  realising a `RenderGraph` (images, barriers, execution), `MaterialBindings`,
  transient descriptor and vertex storage, indirect drawing, compute dispatch,
  timing and pass isolation, and the live resource registry.
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
`IGraphicsDevice.MaxMsaaSamples`. Depth uses `SAMPLE_ZERO` resolution: an
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
uniform layouts, and push-constant ranges. `IMaterialBindings` allocates one
reflected set and writes uniforms or textures by their reflected names and
bindings. There is no second hand-maintained binding table.

Sets are grouped by lifetime rather than by object type: frame-global,
per-pass, per-material, and per-draw. Small per-draw values use push constants;
descriptor-backed values use transient or persistent material bindings.
Recorded push data is copied, so callers may safely reuse scratch storage while
recording subsequent draws.

### Shader facts in C#

Three things a shader declares also have a C# side, and in each case the shader's
declaration is the one the build reads:

- **Push-constant blocks.** `[PushConstants("a.vert", "a.frag")] partial struct APush;`
  gets its fields from the stages' reflection, written by `Blix.Shaders.Generator`
  with the shader's offsets and padding, so `WriteTo(buffer)` or `ToBytes()`
  produce the bytes the shader reads. Names are exact unless the declaration says
  otherwise: `Prefix = "u"` drops a prefix where a member has it, and
  `[ShaderName("uMVP", "ModelViewProjection")]` renames one member. Stages that
  disagree about the block, a type C# cannot hold, or a stage with no reflection is
  a build error.
- **Values that pick a branch.** `[ShaderEnum("present.frag", "uTonemap")]` on a
  C# enum is checked at build against the `//@tune enum{ … }` above that member.
  The names must match, ignoring case, spaces, dashes and underscores, in the same
  order, because the shader compares against positions.
- **Textures.** `new ShaderTextureBinding("uHdr", texture)` binds by the sampler's
  reflected name, and `"uCascades[2]"` names one element of an array. A name the
  program does not have is an error listing the names it has. A binding that also
  gives a `Slot` must agree with reflection about what that slot is called.

A project with `GlslShader` items gets the generator and its inputs from the shared
build targets; nothing needs adding to a csproj. The generator reads the
`.spv.refl.json` and `.spv.tune.json` files the shader build writes, so an IDE
shows the generated fields once a build has produced them.

### Separate images and samplers

A shader can take a texture in two forms, and they sample identically:

| Form | GLSL | Descriptors | Counts against |
| --- | --- | --- | --- |
| Combined | `sampler2D uMap` | one, with the texture's own sampler | samplers **and** sampled images |
| Separate | `texture2D uMap` plus `sampler uLinearClamp` | an image, and a sampler several images share | sampled images; samplers only for the samplers |

`texture(sampler2D(uMap, uLinearClamp), uv)` compiles to the same sample as
`texture(uMap, uv)` on a combined sampler with that state. The difference is the
per-stage limits: MoltenVK allows 16 samplers per stage and 256 sampled images, and
a combined sampler uses one of each. Keep the combined form until a stage nears 16
textures. Sponza's lit shader reads 25, so its per-pass set is separate images
through one sampler, and the stage holds 7 samplers.

**A separate sampler's state is declared in the shader,** on the line before it:

```glsl
//@sampler LinearClamp
layout(set = 1, binding = 19) uniform sampler uLinearClamp;
layout(set = 1, binding = 0) uniform textureCube uIrradiance;
```

The preset is a `SamplerDescription` preset by name (`LinearClamp`,
`LinearClampMipmap`, `LinearRepeat`, `NearestClamp`, `PixelatedRepeat`). The shader
build writes it to `.spv.samplers.json`, and the device builds each one into the
descriptor-set layout as an immutable sampler, so nothing binds it per draw. A
`uniform sampler` without `//@sampler`, a `//@sampler` not followed by one, and an
unknown preset are all build errors.

**A separate image ignores its texture's own sampler.** It is read through
whichever sampler the shader pairs it with, so a texture created with repeat
wrapping and read through `uLinearClamp` is clamped. Textures are still bound by
name, `new ShaderTextureBinding("uIrradiance", texture)`, exactly as for combined.
Note that `LinearClamp` and `LinearClampMipmap` are the same sampler state:
`GenerateMipmaps` is a texture-creation flag, and every sampler leaves `MaxLod`
unclamped so the texture's mip count limits it.

**The library functions come in both forms.** GLSL builds `sampler2D(t, s)` only
where it samples, so it cannot be passed to a function. The sampling functions in
`shadow.glsl`, `sky_visibility.glsl`, `probe_volume.glsl` and `sheen.glsl`
therefore exist as overload pairs, and the separate one takes a single sampler
after its textures:

```glsl
blix_sun_shadow_cascaded(uCascade0, uCascade1, uCascade2, vp0, ...);               // combined
blix_sun_shadow_cascaded(uCascade0, uCascade1, uCascade2, uLinearClamp, vp0, ...); // separate
```

Each library writes its sampling functions once, in `<lib>.sampled.glsl`, against
the macros `sampling_form.glsl` defines, and includes that file twice, once per
form. `ibl.glsl` and `bloom.glsl` are combined only; add the pair the same way when
a separate-form caller needs one.

Fresh per-frame data has two distinct homes:

- `AllocVertices` returns a `TransientVertexSlice` from a frames-in-flight ring.
  It is for descriptor-less vertex/index data bound by offset. `SpriteBatch`,
  debug lines, and `ParticleBatch` use it.
- `IMaterialBindings` owns descriptor-backed uniform and storage buffers.
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

Two of these have a C# counterpart, and both are pinned by a test rather than by
a comment asking the next reader to remember. `tonemap.glsl` has a **shipped**
twin in `Blix.Graphics.Images/Tonemap.cs`, because a capture is read back from
the HDR target before the present pass and something has to apply the curve on
the CPU; `Blix.Test.Graphics` section **BD** fails when a constant stops
appearing in the shader. `sheen.glsl` has a **test-local** twin in section
**BE**, which exists only so the Charlie lobe and the diffuse-transmission terms
can be evaluated without a GPU; shipping it would create the second evaluator
the shared-vocabulary rule exists to prevent. Each shader names its twin at the
top of the file.

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
  Studio.

`StudioLook` is the authored policy boundary. Its defaults are Blix's reference
look: sun, environment, shadow fit, exposure, tonemap, MSAA, and related values.
Most are live per-frame settings. Members marked
`[Tune(Structural = true)]` must be set before graph and pipeline construction;
changing one after Studio seals the structure is reported rather than
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
| `Blix.Demos.Bulwark` | Larger game-owned render policy and diagnostics |

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
- extended material work including sheen and diffuse transmission, the latter
  read per material out of the baked albedo grid's alpha rather than from one
  scene-wide control; and
- debug views, command-line A/Bs, pass timings, resource and draw censuses,
  captures, and CPU/path-traced reference checks.

Three material responses are deliberately absent, each for a stated reason
rather than a backlog position. There is **no second, cheaper sheen BRDF**: a
consumer wanting one writes it at its own call site, because an engine that
ships a fast lobe beside the real one has made the fast one the default. There
is **no `KHR_materials_volume` response** — thickness and attenuation are read
and stored, but responding to them means refraction against a scene-colour
copy, which is a transmission pass and its own piece of work. And there is **no
sheen in the voxel bake**: sheen is a view-dependent rim and a probe has no
view, so diffuse transmission is the half of cloth that indirect light can
carry.

**Ambient banding is probe spacing, and automatic placement has been tried.** The
cause is measured rather than suspected: across 14,072 adjacent probe pairs at
1.57 m, the irradiance ratio between neighbours is median 1.41x, p90 5.15x and
p99 25.7x. It reads worst on curtains because they are large, smooth and have
nothing to hide it. Eight competing explanations were refuted by measurement and
four separate attempts at automatic density heuristics failed — so a fifth is not
the next idea. The live one is **authored** density as nested uniform volumes,
following the `.blixpatch` precedent of letting a person state what a bake cannot
infer. Recorded here so the refuted ground is not walked again.

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
- To build a game renderer, start from the mechanisms in `Blix.Graphics` and
  `Blix.Render`, then compose passes in the
  application.
- To give a content tool a coherent inspection view, adopt
  `Blix.Tools.Studio` and set structural `StudioLook` choices before loading.
- To investigate or measure a rendering technique, use Vulkan Sponza and keep
  the experiment local until its reusable boundary is demonstrated.
- To understand when a texture is usable versus fully sharp, read
  [Assets](assets.md), especially the deferred CPU and progressive GPU paths.
