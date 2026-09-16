# Architecture

The engine is organised so that game code lives in `Blix` (the root namespace, the layer game code targets), the renderer spine lives below it (`Blix.Graphics` → `Blix.Graphics.Vulkan`), and platform contracts (windowing, input, audio host, diagnostics) live below everything in `Blix.Core`.

One backend + runtime ship today: the Vulkan + Silk.NET pair. The original OpenGL backend (`Blix.Graphics.OpenGL`) and its OpenTK runtime (`Blix.Runtime.OpenTK`) were sunset, along with the GL-only `Blix.Render` engine-facing API (name-keyed `Material`/`MaterialResolver`/`PostProcessStack`/`PbrSceneRenderer`) and the four heavy GL demos. Eleven Vulkan demos ship: `Blix.Demos.VulkanHello`, `Blix.Demos.VulkanGraph`, `Blix.Demos.VulkanLit`, `Blix.Demos.VulkanInstanced`, and `Blix.Demos.VulkanSponza` cover validation, render-graph topology, the full lit/shadow/PBR/IBL/bloom scene, the per-instance instancing foundation (5000-cube gate), and the GPU-driven Intel Sponza performance + asset-pipeline target; `Blix.Demos.VulkanParticles` is the CPU-particle + post-process VFX showcase; and four are complete games — `Blix.Demos.Pong` (2D, on the rebuilt Vulkan `SpriteBatch`), `Blix.Demos.Runner` (a 3D endless runner exercising instancing + skeletal animation + kinematic physics), `Blix.Demos.TankArena` (a survival shooter on the `Transform3D` parenting rig over a sun-shadow + HDR graph), and `Blix.Demos.Bulwark` (a tower defense exercising pointer picking, multi-front A\* navigation, and skinned-mesh instancing). `Blix.Demos.Chassis` is the newest and the smallest — an executable spec for the application chassis itself, deliberately *not* a diagnostics producer, which is what lets it prove that an application gets a window, an interface and a bounded run without being one. The engine was progressively reshaped around the Vulkan target — name-keyed materials gave way to SPIR-V-reflected binding (see [the Vulkan binding model](#the-vulkan-binding-model) below).

**Library, not framework.** The engine is a set of composable primitives game code calls — not a control-inverting framework. The split: the **engine owns asset loading, reading, and bundling** (decode/upload/dedup/stream textures, pack geometry into shared buffers, run an off-thread load queue); the **game owns synthesis and composition** (which passes run, how draws are recorded, material/pipeline choice, render-graph topology). There is no `SceneRenderer` that owns read→cull→draw: `VulkanSponza` composes the engine primitives (`MeshBundler`, `AsyncLoadQueue`, `GltfTextureLoader`, the `RenderGraph`) itself and keeps its own draw groups + LOD/cull policy. New rendering capability lands as a primitive the game calls, not a stage the engine runs for you.

This doc orients you. For the game-engine layer game code targets, see [`blix.md`](blix.md). For the renderer — render graph, recording draws, shaders, and rendering techniques — see [`renderer.md`](renderer.md); `src/Blix.Demos.VulkanLit/` and `src/Blix.Demos.VulkanSponza/` are the working references it points at. For the non-negotiable conventions at a glance — transform/matrix/asset/demo — and where each is enforced, see [`conventions.md`](conventions.md).

## Project graph

```
Blix.Demos.VulkanHello         ← Vulkan validation demo (cube + debug overlay)
Blix.Demos.VulkanGraph         ← Vulkan render-graph topology demo (3-pass invert)
Blix.Demos.VulkanLit           ← Vulkan PBR + IBL + shadows + skinning + bloom
Blix.Demos.VulkanInstanced     ← per-instance instancing foundation (5000-cube gate)
Blix.Demos.VulkanSponza        ← Khronos Intel Sponza on Vulkan (GPU-driven indirect,
                                  SSE LOD, cascaded shadows, froxel fog, streamed cooked assets)
Blix.Demos.VulkanParticles     ← CPU particle system + post-process VFX showcase
Blix.Demos.Pong                ← 2D game (SpriteBatch + Font, CRT post-FX)
Blix.Demos.Runner              ← 3D game: endless runner (instanced world + props,
                                  skinned animated character, kinematic physics, sky/fog/HUD/audio)
Blix.Demos.TankArena           ← 3D game: survival shooter (Transform3D parenting rig,
                                  articulated glTF tank, cover, sun-shadow + HDR graph)
Blix.Demos.Bulwark             ← 3D game: tower defense (picking + multi-front A* nav +
                                  skinned-mesh instancing, sun-shadow + HDR graph)
Blix.Demos.Chassis             ← application-chassis spec (no diagnostics, own ImGui panel,
                                  host-owned --frames; 25-line csproj, no shaders)
Blix.Tools.Studio            ← LAB library: lit scene + render graph + shaders + glTF
                                  node/pivot model, reflected binding (library content)
   ↑        ↑
Viewer  Probe  Capture        ← three executables over one lab; none declares a shader
                                  nor contains render code. Probe opens no window;
                                  Capture reads the HDR target back and writes a PNG.
        ↑
Blix.Runtime.Silk              ← Vulkan window/runtime adapter
                                  (Silk.NET window + IVkSurface + MoltenVK bootstrap,
                                   VkLineDrawer for debug overlay)
        ↑
Blix                           ← layer game code targets
   ↑   ↑      ↑       ↑           (loop, scene, animation, physics, audio,
   │   │      │       │            glTF import + GltfTextureLoader, ViewPicking)
   │   │      │   Blix.Assets    ← asset DB + importers + cooked formats
   │   │      │       ↑             (texture, OBJ, font, WAV, .blixmesh)
   │   │   Blix.Render          ← engine-facing rendering + asset pipeline
   │   │      ↑                    (Mesh, GraphicsDeviceMeshExtensions, ResourceUploader,
   │   │                            MeshBundler, AsyncLoadQueue; SpriteBatch + Font + UI
   │   │                            — Vulkan 2D path; FullscreenPass + PostChain
   │   │                            — fullscreen/post-process primitives)
   │ Blix.Geometry              ← primitives + intersection tests
   │                              (Bounds3/2, Sphere, Capsule, OBB, mesh colliders)
Blix.Graphics                  ← graphics command language
   ↑                              (handles, pipelines, surfaces, vertex types,
   │                               shader sources, GLSL preprocessor + ShaderLoader)
   Blix.Graphics.Vulkan         ← Vulkan backend (Silk.NET.Vulkan bindings,
                                   instance/device/swapchain, per-draw transient
                                   descriptor pool, RenderGraph, MaterialBindings,
                                   ShaderReflection: build-time SPIR-V binding +
                                   std140 layout reflection via spirv-cross sidecars)
Blix.Graphics.Images           ← image decode + HDR IBL bake pipeline
                                  (StbImageSharp, EquirectangularToCubemap,
                                   PbrIblBaker, HdrSunFinder)
Blix.Shaders                   ← engine-level GLSL library (no csproj; .glsl
                                  included by game/demo shaders and expanded by
                                  Blix.Tools.Shader; pbr/tonemap/noise/fullscreen/bloom)
Blix.Tools.Shader              ← build-only GLSL preprocessor + glslc driver
Blix.Diagnostics               ← contribution-based debug system
        ↑                         (DebugFrame snapshots + history ring,
                                   Values/Controls/Draw/Stats/Timers/Events
                                   channels, named views + retained trails,
                                   sinks, selection + picking,
                                   //@tune + [Tune] live-tuning panels,
                                   PeriodicConsoleSummarySink for stdout digest)
Blix.Core                      ← platform contracts (no implementations)
                                  (IRenderHost, IAudioHost, IDebugHost,
                                   IInputHandler, IUiSource, IRuntimeDiagnosticsSink,
                                   Key, MouseButton, RenderFrameContext,
                                   View/ViewId/ViewDeclaration/ViewTable)

Blix.Audio                     ← audio command language (IAudioDevice)
Blix.Audio.OpenAL              ← OpenAL Soft backend
```

Every cross-project dependency in the source tree fits one of the arrows above. Nothing above `Blix.Core` depends on a windowing/audio backend directly — `Blix.Runtime.Silk` is the only project that wires `IRenderHost`/`IAudioHost`/`IDebugHost` to concrete implementations.

## The Vulkan binding model

The renderer's defining choice is that the binding model is *derived*, not declared by hand. At build time the compiled SPIR-V is reflected (via spirv-cross sidecars) into a `ShaderInterface`: descriptor sets + std140 UBO layouts + push-constant ranges, keyed by the set/binding/member names in the shader. `CreateMaterial(program, setIndex, …)` then allocates a `MaterialBindings` against one reflected set — `SetUniform("uTint", …)` / `SetTexture(binding, …)` write into it by name, and `.Handle` is the backend-neutral `MaterialHandle` a `GameObject` stores. Sets are organised by lifetime (frame-global, per-material, per-draw), and per-draw data rides push constants or a transient descriptor pool refilled each frame. There is no parallel hand-maintained binding table to drift out of sync with the shader source.

The Vulkan path is now the sole renderer, and in `VulkanSponza` it has moved well past the old GL feature set: that SPIR-V-reflected binding model, per-material descriptor sets, push constants, per-draw transient descriptor pools, a declarative render graph (`Blix.Graphics.Vulkan/RenderGraph.cs`), glTF + skinning (via the existing `Blix.Assets` importers), PBR + IBL (procedural-sky or cooked-probe environment + irradiance cube + split-sum BRDF LUT), HDR + ACES/AgX tonemap, and a separable-Gaussian bloom chain. `VulkanSponza` adds cascaded directional shadows (texel-snapped + cached), a depth pre-pass, froxel volumetric fog, GPU-driven indirect rendering, screen-space-error LOD over meshopt chains, and the cooked-asset pipeline (`.blixmesh`/`.blixtex`/`.blixprobe`) streamed through the engine's `GltfTextureLoader` + `AsyncLoadQueue` + `MeshBundler`. The 2D path (`SpriteBatch` + `Font`, used by `Pong`) was rebuilt on the Vulkan binding model. SSR and the dual-filter bloom were GL-only techniques and did not survive the sunset; froxel fog now lives on Vulkan in VulkanSponza.

`Blix.Shaders` isn't a code project — it's a folder of `blix_`-prefixed `.glsl` library files. Game and demo shaders `#include "<file>.glsl"`; the shared `BlixCompileSpirV` target in the root `Directory.Build.targets` runs the build-only `Blix.Tools.Shader`, which expands the source with the same Blix preprocessor before invoking `glslc`. A project declares *what* it compiles (`@(GlslShader)`, `@(GlslInclude)`) and, if unusual, *how* (`BlixShaderMode` for a library that ships its own `.spv`; `BlixShaderCustomTarget` to stand the shared one aside). Library files stay in the target's `Inputs` so edits retrigger the cook (see **Shader library** under Conventions below).

## Views, and the frame that is not one picture

A **view** is somewhere a world is seen from, and where that picture lands:
`ViewDeclaration(Id, Name, ViewProjection, Target, LogicalViewport, PhysicalViewport)`
in `Blix.Core`. It is the generalisation of `RenderFrameContext(Width, Height)` — the
single implicit view Blix used to assume — and it is what makes viewports, picking,
editor cameras, off-screen capture and second-camera inspection one mechanism instead
of five features.

Three properties are load-bearing:

- **A view is told its matrix; it never owns a camera.** `Camera3D` is a layer above
  `Blix.Core` and carries policy — projection convention, field of view, how orbiting
  feels. A view that holds a `Matrix4x4` stays inert, so a game camera, an editor
  camera, a shadow cascade and a hand-composed matrix all feed the same path.
- **A view knows a surface handle, never a renderer.** `RenderSurfaceHandle` is an
  opaque int from the graphics abstraction. `Blix.Core` does not reference
  `Blix.Render` and must not start: a view says where a picture goes, not how it is
  drawn.
- **Views are declared per frame; their ids are not.** A declaration is a value, so a
  frozen frame cannot re-render through a camera that has since moved. `ViewId` is
  interned from the name by the one `ViewTable` and stays stable for the process,
  because "this thing, in *that* view, over the last N frames" needs an identity that
  outlives the declaration carrying it.

**What views do not do yet.** A view's rect never reaches the renderer — it is carried
for picking and DPI and is not applied as a viewport or scissor, so a view draws across
its whole target. And debug passes are appended after the game's entire command list,
so debug geometry written into a game-owned intermediate target cannot be presented
that frame. An inspector viewport therefore is not yet achievable: picking into one
works and is tested, rendering one does not. Both are left for a real consumer to force
rather than guessed at.

Debug primitives are emitted into a view with `using (debug.Draw.In(view))`, and each
command stores the resolved `ViewId` — ambient at the call site, recorded in the data,
exactly as scope-built `Path` already works. Emitting a primitive with no view in scope
**throws**: before views it silently took the identity matrix and rendered into clip
space, invisible, which is a bug in the producer and now reads as one.

`DebugTrails` is the one part of diagnostics that remembers anything across frames —
a bounded, path-keyed ring of time-stamped points behind `debug.Draw.Trail(...)`. It
stores and computes nothing: no smoothing, resampling or reduction. A trail samples
once per path per frame however many views it is drawn into, so asking twice is one
history painted twice rather than two histories drifting apart.

The one place policy could walk back in: the selection highlight is painted into
*every* declared view. That is a **local default for the single selection mechanism
that exists**, not an engine law — "is this view an audience for overlays?" is not
intrinsic to being a view, and a shadow cascade is not. When a second consumer
disagrees, selection should learn which views it addresses rather than a view growing
a kind.

Frame dumps are **schema 2**: a `Views` array plus a per-command `View` name, replacing
the single frame-wide camera matrix, and a `SchemaVersion` field that schema 1 did not
have.

## Reading a frame back

`VulkanGraphicsDevice.ReadTexture` copies a rendered colour texture to CPU bytes. It is
the first *pull* in an otherwise push-only device, and Blix had none — colour attachments
were created without `TransferSrcBit`, so they were not legal copy sources and capture
was impossible **at the point the image was made**, not at the point somebody asked.

The absence had already shaped the project: TankArena fits its tank model by eye through
the overlay, with a comment reading "Screenshots don't work", and three rendering bugs in
the view and lab arcs were each caught only because a person looked at a picture while
green bounded runs, zero validation errors and healthy draw counts said nothing.

It is **synchronous** — submit, wait the queue idle, map, copy. Milliseconds, which is
wrong for a per-frame path and right for a tool taking one picture; an asynchronous ring
belongs to whoever first needs a capture every frame. `Blix.Graphics.Images.PngWriter`
encodes the result, hand-rolled with stored-deflate blocks so no encoder dependency is
added — the files are larger than real deflate would make them, and they are screenshots,
not assets.

**Picking through a view has one caller**: `Blix.Tools.View` turns a click into a
ray with `ViewPicking.RayThrough` and tests it against node bounds. Note what that requires
— the view must be declared with a LOGICAL rectangle matching the coordinates a pointer
arrives in. The whole-surface shorthand `debug.Draw.Declare(name, vp)` fills both rectangles
from `RenderFrameContext`, which is physical pixels, so a view declared that way and then
picked through is off by the backing scale on a Retina display. Take the logical rect from
`IRenderHost.LogicalSize`.

**Capture draws over the scene now.** Debug geometry can be aimed at any surface — the line
drawer bakes a pipeline per render target, where it used to have exactly one against the
default pass, making debug drawing silently swapchain-only. An off-screen surface used to
have no `LoadOp.Load` render pass variant either, so a pass aimed at one **cleared it** and
debug lines landed on a freshly cleared image rather than over the scene. That is closed:
every graphics pass now bakes a load-form render pass alongside its declared form
(`BackendPass.RenderPassLoad`), a synthetic render surface carries it, and
`RenderPassDescription.LoadExisting` is how an externally-routed `commandList.Pass` asks
for it. The toolchain lab's capture is the consumer — its skeleton overlay is drawn into
the scene target and comes back in the PNG.

**What capture still cannot do.** Read back a graph colour target at a size the graph has
never allocated: the read path copies the image as it stands, so a capture is whatever the
swapchain extent was. And nothing yet captures a *sequence* — one file per frame with a
fixed timestep — which is what turning "does this motion look right" into a diffable
artifact would need.

## How an application is put together

The toolchain lab's viewer is the worked example, and it earned the section by going wrong first:
it reached **1,645 lines** with the camera, the animation clocks, picking and the entire UI in one
type. The problem was never the line count on its own — it was that *what is shown* and *what is
true* had become impossible to tell apart. A checkbox that hides a skeleton and a clock that
advances one are different kinds of fact, and a reader had to know the codebase to say which a given
field was.

**The shape.** A root constructs its parts and calls them. Nothing else:

```
Program            parses args, owns the window
  ViewerLoop       the root: load, update, render, debug, input
    StudioCamera      x2 — the window's view and the panel's viewport
    RigAnimation     clocks, composition, palettes
    StudioSelection   what is selected, and what a click selects
    ViewerPanels   every panel, and the display state they toggle
```

No discovery, no registration, no "which tool is active" branch. **A different executable simply
builds a different root** — which is what the capture tool and the probe already are.

**What crosses into the library, and what does not.** These are two different bars and conflating
them is how a lab grows a framework:

- **Into `Blix.Tools.Studio` requires a second consumer.** `StudioCamera` went because the viewer had
  *two* cameras with duplicated orbit arithmetic — the §4 bar met without either copy leaving the
  file. `RigAnimation` went because the capture tool had independently grown its own pose composition,
  root strip, palette packing and distinct-pose count; the viewer runs it live and the capture runs
  it a fixed step at a time, which is one set of decisions on two clocks.
- **Staying in the executable needs no second consumer at all.** `StudioSelection` and `ViewerPanels`
  are local decomposition: only the viewer picks, and only the viewer has panels. The bar there is
  simply that the file had stopped being readable.

**A view may hold its model.** `ViewerPanels` keeps a reference to the root and reads what it needs
through a short, explicit list of internal accessors. That is not a circular dependency worth
avoiding — it is the ordinary shape for a view, and it is what keeps each panel method's dependency
list from being a dozen parameters. The rule that matters is the other direction: **the root never
asks the panels what to do.** It reads their toggles, which is data, not control.

**What this is not.** Not plugins, not a tool registry, not an "application framework". The parts
are ordinary classes with constructors, called in an order you can read top to bottom.

## Host contracts

`Blix.Core` owns the platform-facing interfaces. The runtime (`Blix.Runtime.Silk.Window`) implements all of them; game code consumes them. Game code never references `Blix.Runtime.Silk` directly.

| Contract | Defined in | What it does |
| --- | --- | --- |
| `IRenderHost` | `Blix.Core` | Runtime knobs: `SetTitle`, `RequestClose`, `SetCursorCaptured`, `LogicalSize`. |
| `IAudioHost` | `Blix.Core` | Hands out the `IAudioDevice` (`Blix.Audio`) for the running session. |
| `IDebugHost` | `Blix.Diagnostics` | Exposes the active `DebugContext` (for per-frame writes) and the full `DebugSystem` (for contributor registration, freeze, selection). |
| `IInputHandler` | `Blix.Core` | Edge-triggered input events: `OnKeyDown/Up`, `OnMouseDown/Up`, `OnMouseMove`, `OnMouseWheel`. A press decides who owns the gesture and its matching release goes to the same place — see `Blix.Core.GestureOwnership`. So a press the UI took delivers no release to the application, and a press the application took delivers its release even if focus has since moved to a panel. |
| `IUiSource` | `Blix.Core` | The application draws its own interface: `UiName`, `DrawUi()`. No UI types in the signature, so `Blix.Core` declares the hook without depending on a UI library — the application brings its own `ImGui.NET`. Independent of diagnostics: an application that produces none still gets a UI. |
| `IRuntimeDiagnosticsSink` | `Blix.Core` | Per-frame backend introspection: receives `FrameDebugPacket` + `ResourceRegistrySnapshot`. |

The game implements `IGameLoop` (in `Blix`) and optionally `IInputHandler`, `IUiSource` and `IDebuggable`. The runtime forwards events only when the interface is present.

**`IUiSource` and `IDebugUi` are different hooks, deliberately.** `IUiSource` (`Blix.Core`) is *the application's* interface: it exists whether or not the loop produces diagnostics, and the host builds an ImGui frame for it regardless. `IDebugUi` (`Blix.Diagnostics.Overlay`) is a *diagnostics producer's* panel, drawn by `DebugOverlayUi` inside the overlay under a header keyed on `DebugName`, and visible only when the overlay is. One is an application having a face; the other is a subsystem adding a tab to the inspector.

`Blix.Runtime.Silk.Window` implements `IRenderHost`, `IAudioHost`, and `IDebugHost` simultaneously — game code reads them via `Host`, `Host as IAudioHost`, `Host as IDebugHost` from inside `Game`.

## Conventions

The non-negotiables (transform/matrix/asset/demo) are indexed in
[`conventions.md`](conventions.md) with pointers to where each is enforced. The
graphics-facing ones are spelled out below.

**Coordinate system.** World space is right-handed. `+X` right, `+Y` up, default camera looks down `-Z`. A camera at `(0, 0, 2)` sees objects around the origin.

**Matrices (F-016).** CPU-side matrices are `System.Numerics.Matrix4x4` in its **native row-vector form** throughout — translation in `M41/M42/M43`, a point transforms as `v_row * M`, and `M = A * B * C` composed left-to-right applies `A` first, `B` second, `C` third. `GraphicsMatrices.CreateModel` is exactly `Scale * Rotation * Translation` in this form (it's built from `System.Numerics.CreateScale`/`CreateFromQuaternion`/`CreateTranslation`), so hand-rolled `System.Numerics` composition and `GraphicsMatrices`/`Transform3D.ToMatrix`/`WorldMatrix` are the **same convention and mix freely** — `VulkanLit` and `VulkanInstanced` build model matrices both ways. `Matrix4x4.Decompose` reads them directly; there are no manual transposes anywhere in the engine's transform/skeletal math.

The Vulkan backend uploads these row-major bytes **untransposed**. GLSL's std140 reads them column-major, which is the transpose — i.e. the column-vector form — so a shader's `M * v_col` computes the same transformation `v_row * M` does on the CPU. Net: one convention, no transposes, model matrices feed a `model * v` shader (or `InstanceData.Model`) directly. The composition + this upload→GLSL convention are pinned by `Blix.Test.Graphics` Section AH (it simulates the GLSL `M*v` against known world points).

**Gotcha — the projection builders are different.** The hand-built projection matrices in `GraphicsMatrices` (`CreatePerspectiveVulkan`, the orthographics) place their terms in column form because they're authored directly as the shader-space matrix; don't pattern-match off them when reasoning about *model* matrices. Model/view matrices are the row-vector form above.

**Pixel coordinates.** `IRenderHost.LogicalSize` returns the window's client area in logical pixels (same coordinate system as mouse events). `RenderFrameContext.Width/Height` is the framebuffer in physical pixels (typically 2× on Retina). Don't mix them — `Camera3D.ScreenPointToRay` needs logical pixels because mouse coords are logical.

A `ViewDeclaration` carries **both** rectangles, `LogicalViewport` and `PhysicalViewport`, so neither is derived at a call site: `ViewPicking.RayThrough` compares against the logical one, the renderer uses the physical one. That is the same Retina gotcha removed rather than documented — and it is why a view is given its rectangles by whoever makes it, which is the only place that knows the backing scale.

**GLSL includes.** `Blix.Graphics.GlslPreprocessor.PreprocessDetailed` resolves `#include "filename"` directives by inlining the referenced content. It is recursive, cycle-detected, and consumes Blix-owned `#pragma once` directives rather than sending them to `glslc`; stable source identities deduplicate the same canonical file even when it is reached through different relative spellings. It emits `#line N <source-id>` directives around every inclusion so shader compile errors report the original file's line numbers; the source-id-to-filename map flows through `ShaderSources` to diagnostic formatting. File I/O stays in the caller via a source-aware resolver, and `ShaderLoader.PreprocessFile(...)` supplies the canonical file implementation used at build time.

`Blix.Graphics.ShaderLoader.LoadVertexFragment(vertPath, fragPath, includeDirs?, defines?)` bundles read + preprocess + naming + source-map plumbing. The optional `defines` dictionary injects `#define KEY VALUE` lines right after `#version` so the same library function can serve multiple variants.

**Shader library.** Engine-shared GLSL lives at `src/Blix.Shaders/*.glsl` with a `blix_` prefix on every symbol. Games and demos consume it with `#include "<file>.glsl"`; `Blix.Tools.Shader` expands those includes before invoking `glslc` (the library files are also in each target's `Inputs`, so editing one re-cooks every dependent shader). New library files start with `#pragma once`; do not add parallel `#ifndef` file guards.

**GLSL ASCII only.** Shaders are compiled offline to SPIR-V with `glslc` (Khronos); the old Apple GL 4.1 compiler quirks no longer apply at runtime. Pure ASCII remains the `glslc`-portability convention for the shader library — keep shader files pure ASCII.

## Where to find things

| If you want... | Look at... |
| --- | --- |
| Render a frame, write a shader, set up a pipeline | [`renderer.md`](renderer.md); `src/Blix.Demos.VulkanGraph/` (smallest graph) and `src/Blix.Demos.VulkanLit/` (full pipeline) |
| See HDR + IBL + shadows + bloom wired together on Vulkan | `src/Blix.Demos.VulkanLit/` and `src/Blix.Demos.VulkanSponza/` |
| Make a `Game` subclass, place an object, animate it, query collisions | [`blix.md`](blix.md) |
| Add a host facet (audio, gamepads, networking) | `src/Blix.Core/` for the contract, then implement in `src/Blix.Runtime.Silk/` |
| Add a new asset type | `src/Blix.Assets/` (importer + intermediate data type) |
| Bundle meshes into shared buffers / stream glTF textures | `Blix.Render.MeshBundler`, `Blix.Render.AsyncLoadQueue<T>`, `GltfTextureLoader` — `src/Blix.Demos.VulkanSponza/` composes them |
| Add a new debug control / stat / timer / event | `IDebuggable.Debug(DebugContext)` — `Blix.Diagnostics` |
| Draw debug geometry at all | Declare a view, then `using (debug.Draw.In(view))` — drawing outside one throws |
| Show where something has been | `debug.Draw.Trail(name, point, colour, seconds)` |
| Give the application its own UI panel | Implement `IUiSource` on the game loop; add an `ImGui.NET` package reference |
| Add a panel to the diagnostics overlay instead | Implement `IDebugUi` on a registered contributor (`Blix.Diagnostics.Overlay`) |
| Turn a click into a ray, in any view | `Blix.ViewPicking.RayThrough(view, pointer)` — panels and off-screen targets included |
| Run bounded (CI, a smoke test, a capture) | `--frames N`, honoured by the host for every application |
| Start a new executable | `src/Blix.Demos.Chassis/` is the smallest working one — 25-line csproj, no shader boilerplate |
| Expose a value for live tuning in the overlay | `//@tune lo..hi = default` in a GLSL uniform, or `[Tune(min,max)]` on a C# field |
| Register a debug producer (subsystem, asset, scene instance) | `debugSystem.Register(contributor)` from `OnLoad` — implement `IDebuggable` / `IDebugGeometrySource` / `IDebugSelectable` / `IDebugInspectable` / `IDebugUi` independently |
| Save a frame snapshot to disk | Press `F12` (runtime-owned) — writes `dumps/frame-NNNNNN.json` via `JsonDumpSink` |
| Toggle the diagnostics overlay | Press `` ` `` (backtick) |
| Add a reusable shader primitive | `src/Blix.Shaders/<concept>.glsl` (one concept per file, `blix_`-prefixed symbols) |

## Build + run

```sh
dotnet build Blix.sln
# Vulkan demos need MoltenVK env vars on macOS — use the launcher scripts:
tools/run-vulkan-hello.sh
tools/run-vulkan-sponza.sh
tools/run-chassis.sh --frames 60      # any app: the host honours --frames
```

**Launchers exec the apphost; they never use `dotnet run`.** On macOS Homebrew's
`$prefix/bin/dotnet` is a `#!/bin/bash` wrapper script, and `/bin/bash` is
SIP-protected — dyld strips `DYLD_*` from a protected binary's environment, so the
Vulkan loader path is laundered away before the application starts and Silk reports
*"doesn't support Vulkan on this computer"* while Vulkan is installed and working. A
launcher exports `DOTNET_ROOT`, builds, then `exec`s
`bin/Debug/net8.0/<App>` directly. Copy an existing `tools/run-*.sh` when adding one.

See top-level [`README.md`](../README.md) for the full per-demo run commands and platform notes (macOS OpenAL Soft, shader ASCII, MoltenVK setup).
