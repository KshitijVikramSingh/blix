# Architecture

Blix is a code-first library and a family of explicit applications. It does not
own a universal scene, editor, project model, or renderer. A game or tool builds
an ordinary C# root, chooses the capabilities it needs, and calls them in an
order visible in that root.

One graphics backend and one desktop runtime ship today: Vulkan through
`Blix.Graphics.Vulkan`, hosted by Silk.NET in `Blix.Runtime.Silk`. OpenGL and
OpenTK have been removed. OpenAL remains the audio backend.

The central ownership rule is:

- engine projects provide contracts, data, algorithms, command vocabulary,
  importers, and reusable rendering mechanisms;
- recipes decide how source data becomes an engine-readable artifact;
- Studio provides an optional reference rendering pipeline;
- applications own scene policy, pass topology, feature selection, lifetime,
  loading budgets, and domain behavior; and
- tools are applications over the same libraries, not privileged engine modes.

There is no `SceneRenderer` that owns load, cull, and draw. Vulkan Sponza, for
example, composes `MeshBundler`, `AsyncLoadQueue`, `GltfTextureLoader`, and
`RenderGraph`, while retaining its own grouping, culling, LOD, lighting, and
research policy.

For the game-facing library see [Game-facing API](blix.md). For formats,
recipes, loading, and residency see [Assets](assets.md). For render commands,
graphs, shaders, and techniques see [Renderer](renderer.md). For application
discovery and verification see [Workflow](workflow.md).

## Project map

The checkout currently contains 50 project files and four project roots:
`blix`, `demos`, `character`, and `rts`. The table is a map of responsibility,
not a claim that every row is one strict dependency tier.

The `rts` root is a separate game currently co-located with the engine. Treat it
as an application consumer with project-owned policy, tests, tools, and cooking;
its possible future move to another repository is an organizational change, not
an engine-layer migration.

| Responsibility | Projects or directories |
| --- | --- |
| Host and application contracts | `Blix.Core` |
| Graphics and audio command vocabulary | `Blix.Graphics`, `Blix.Audio` |
| Cooked-asset contracts | `Blix.Cooked` |
| Geometry and collision algorithms | `Blix.Geometry` |
| Image and environment processing | `Blix.Graphics.Images` |
| Runtime asset data and importers | `Blix.Assets` |
| Diagnostics data and optional UI | `Blix.Diagnostics`, `Blix.Diagnostics.Overlay` |
| Vulkan and OpenAL backends | `Blix.Graphics.Vulkan`, `Blix.Audio.OpenAL` |
| Render and loading mechanisms | `Blix.Render` |
| Game-facing types | `Blix` |
| Blix-owned cooking decisions | `Blix.Recipes` |
| Desktop composition | `Blix.Runtime.Silk` |
| Reference rendering pipeline | `Blix.Tools.Studio` |
| Reusable headed-tool shell | `Blix.Tools.Studio.Shell` |
| App discovery and launch | `Blix.Cli`, `Blix.Tools.Apps` |
| Build-time and asset tools | `Blix.Tools.Shader`, `Blix.Tools.Cook` |
| User-facing asset tools | `Blix.Tools.Inspect`, `Blix.Tools.Check`, `Blix.Tools.View`, `Blix.Tools.Shot` |
| Shared assertion tally and suites | `Blix.Verify`, `Blix.Test.*` |
| Character subsystem instruments | `src/Character/Blix.Labs.Character*` |
| Executable specifications and games | `src/Demos/Blix.Demos.*` |
| Co-located RTS game and its cooking policy | `RTSGame`, `RTSGame.Cooking` |

### Dependency direction

The important edges in the current project references are:

```text
applications and tools
  -> Blix / Blix.Tools.Studio / project-specific libraries
  -> Blix.Render + Blix.Assets + Blix.Diagnostics
  -> Blix.Graphics.Vulkan + Blix.Graphics + Blix.Core
  -> Blix.Runtime.Silk at the executable composition edge

build-time cooking
  -> Blix.Tools.Cook -> Blix.Recipes -> engine formats and capabilities
  -> project recipe assemblies such as RTSGame.Cooking
```

Several details stop this from being a simplistic layered pyramid:

- `Blix.Core` is the host-contract assembly, not a dependency-free foundation;
  its contracts name graphics and audio handles from `Blix.Graphics` and
  `Blix.Audio`.
- `Blix.Render` is currently Vulkan-backed. It provides reusable rendering
  mechanisms, but it references `Blix.Graphics.Vulkan`; it is not a second
  backend abstraction.
- `Blix.Runtime.Silk` is the composition point that wires windowing, Vulkan,
  audio, diagnostics, UI, and the game-facing loop together. Executables
  reference it to construct the host; their domain code then consumes the host
  contracts.
- `Blix.Cooked` keeps recipe declarations, stamps, preambles, load reports, and
  import refusals low in the graph so build tools and project recipes can use
  them without taking a graphics device.
- `Blix.Cli`, `Blix.Tools.Apps`, and `Blix.Verify` deliberately avoid the engine
  graph. The launcher reads generated indexes, the indexer reads assembly
  metadata without loading assemblies, and the assertion tally has no project
  references.
- Consumer projects reference the modules they actually compose. There is no
  plugin registry that injects engine subsystems into them.

## Rendering roles

Blix has three distinct rendering roles. They should not be described as
successive versions of one default renderer.

### Reusable rendering mechanisms

`Blix.Graphics`, `Blix.Graphics.Vulkan`, `Blix.Render`, and `Blix.Shaders`
provide the command model, render graph, reflected binding, buffers, upload and
batching helpers, fullscreen work, sprites, particles, and shared shader
vocabulary. These are capabilities. They do not decide which passes an
application runs or what its authored look should be.

### Studio reference rendering pipeline

`Blix.Tools.Studio` is the optional reference pipeline used by Blix's model
and rig tools. `StudioLook` owns its lighting, environment, shadows, exposure,
tonemap, MSAA, and other authored defaults. `Blix.Tools.Studio.Shell` adds the
separate ImGui-dependent viewport/panel layer.

“Reference rendering pipeline” is the durable name. It communicates that this
is the coherent setup Blix uses to inspect content without implying that every
game must accept a global default renderer. A project may reuse all, some, or
none of it.

### Vulkan Sponza research renderer

Vulkan Sponza is the heavy-scene rendering and measurement application. It owns
its experimental graph, scene-specific packaging, diagnostic modes, comparisons,
and adoption decisions. A mechanism moves into an engine project only when it
has a reusable contract and another credible consumer. Sponza is therefore not
the getting-started renderer and its current graph is not an engine promise.

## The Vulkan binding model

The renderer's defining choice is that the binding model is *derived*, not declared by hand. At build time the compiled SPIR-V is reflected (via spirv-cross sidecars) into a `ShaderInterface`: descriptor sets + std140 UBO layouts + push-constant ranges, keyed by the set/binding/member names in the shader. `CreateMaterial(program, setIndex, …)` then allocates a `MaterialBindings` against one reflected set — `SetUniform("uTint", …)` / `SetTexture(binding, …)` write into it by name, and `.Handle` is the backend-neutral `MaterialHandle` a `GameObject` stores. Sets are organised by lifetime (frame-global, per-material, per-draw), and per-draw data rides push constants or a transient descriptor pool refilled each frame. There is no parallel hand-maintained binding table to drift out of sync with the shader source.

The Vulkan path is the sole renderer. Reflected descriptors, push constants,
transient per-draw data, the declarative render graph, glTF/skinning, PBR/IBL,
HDR presentation, sprites, particles, and instancing all use this binding model.
Vulkan Sponza applies it to the larger research graph described above; the
exact technique inventory belongs in [Renderer](renderer.md), not in the
architectural dependency map.

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

**A view describes and routes; it does not schedule a scene render.** An
application still declares the graph pass, target, camera, and draws that make a
picture. `ViewDeclaration.PhysicalViewport` is not automatically installed as
an arbitrary scene pass's Vulkan viewport or scissor. The current embedded
viewport fills its own off-screen target, so no sub-rectangle of that target is
needed.

The complete embedded path now exists in `Blix.Tools.View`:

```text
StudioRenderer second-camera pass
  -> off-screen HDR viewport target
  -> IRenderHost.RegisterUiTexture
  -> ViewportPanel inside ImGui
  -> ViewPicking.RayThrough on the panel's logical image rectangle
```

Debug geometry is routed to `StudioRenderer.ViewportSurface` through the same
view declaration and is loaded over the rendered scene before the texture is
presented in the panel. The panel has its own orbit camera and selection works
through its letterboxed image. This is an implemented consumer, not a future
editor claim.

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
for it. The Studio capture tool is the consumer — its skeleton overlay is drawn into
the scene target and comes back in the PNG.

**What capture still cannot do.** Read back a graph colour target at a size the graph has
never allocated: the read path copies the image as it stands, so a capture is whatever the
swapchain extent was. And nothing yet captures a *sequence* — one file per frame with a
fixed timestep — which is what turning "does this motion look right" into a diffable
artifact would need.

## How an application is put together

`Blix.Tools.View` is the worked example, and it earned the section by going wrong first:
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

`Blix.Core` owns the baseline platform-facing interfaces; `Blix.Diagnostics`
adds the diagnostics host facet. The runtime (`Blix.Runtime.Silk.Window`)
implements them and an executable references the runtime to construct that
host. Once running, loop and domain code consume the interfaces rather than
backend details.

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

A `ViewDeclaration` carries **both** rectangles, `LogicalViewport` and
`PhysicalViewport`, so neither has to be guessed from the other.
`ViewPicking.RayThrough` compares against the logical one. The physical one
records the intended output region for diagnostics and render routing, but it
does not currently install a Vulkan viewport or scissor for an arbitrary scene
pass; that pass still owns its target and draw state. The embedded Studio view
fills its own off-screen target, while its logical rectangle follows the
letterboxed image in the UI.

**GLSL includes.** `Blix.Graphics.GlslPreprocessor.PreprocessDetailed` resolves `#include "filename"` directives by inlining the referenced content. It is recursive, cycle-detected, and consumes Blix-owned `#pragma once` directives rather than sending them to `glslc`; stable source identities deduplicate the same canonical file even when it is reached through different relative spellings. It emits `#line N <source-id>` directives around every inclusion so shader compile errors report the original file's line numbers; the source-id-to-filename map flows through `ShaderSources` to diagnostic formatting. File I/O stays in the caller via a source-aware resolver, and `ShaderLoader.PreprocessFile(...)` supplies the canonical file implementation used at build time.

`Blix.Graphics.ShaderLoader.LoadVertexFragment(vertPath, fragPath, includeDirs?, defines?)` bundles read + preprocess + naming + source-map plumbing. The optional `defines` dictionary injects `#define KEY VALUE` lines right after `#version` so the same library function can serve multiple variants.

**Shader library.** Engine-shared GLSL lives at `src/Blix.Shaders/*.glsl` with a `blix_` prefix on every symbol. Games and demos consume it with `#include "<file>.glsl"`; `Blix.Tools.Shader` expands those includes before invoking `glslc` (the library files are also in each target's `Inputs`, so editing one re-cooks every dependent shader). New library files start with `#pragma once`; do not add parallel `#ifndef` file guards.

**GLSL ASCII only.** Shaders are compiled offline to SPIR-V with `glslc` (Khronos); the old Apple GL 4.1 compiler quirks no longer apply at runtime. Pure ASCII remains the `glslc`-portability convention for the shader library — keep shader files pure ASCII.

## Where to find things

| If you want... | Look at... |
| --- | --- |
| Render a frame, write a shader, set up a pipeline | [`renderer.md`](renderer.md); `src/Demos/Blix.Demos.VulkanGraph/` (smallest graph) and `src/Demos/Blix.Demos.VulkanLit/` (full pipeline) |
| See HDR + IBL + shadows + bloom wired together on Vulkan | `src/Demos/Blix.Demos.VulkanLit/`; use `src/Demos/Blix.Demos.VulkanSponza/` for renderer research rather than as a starting template |
| Make a `Game` subclass, place an object, animate it, query collisions | [`blix.md`](blix.md) |
| Add a host facet (audio, gamepads, networking) | `src/Blix.Core/` for the contract, then implement in `src/Blix.Runtime.Silk/` |
| Understand or extend asset cooking/loading | [`assets.md`](assets.md); runtime types in `src/Blix.Assets/`, recipe contracts in `src/Blix.Cooked/`, transformations in `src/Blix.Recipes/` or a project recipe assembly |
| Bundle meshes into shared buffers / stream glTF textures | `Blix.Render.MeshBundler`, `Blix.Render.AsyncLoadQueue<T>`, `GltfTextureLoader` — `src/Demos/Blix.Demos.VulkanSponza/` composes them |
| Add a new debug control / stat / timer / event | `IDebuggable.Debug(DebugContext)` — `Blix.Diagnostics` |
| Draw debug geometry at all | Declare a view, then `using (debug.Draw.In(view))` — drawing outside one throws |
| Show where something has been | `debug.Draw.Trail(name, point, colour, seconds)` |
| Give the application its own UI panel | Implement `IUiSource` on the game loop; add an `ImGui.NET` package reference |
| Add a panel to the diagnostics overlay instead | Implement `IDebugUi` on a registered contributor (`Blix.Diagnostics.Overlay`) |
| Turn a click into a ray, in any view | `Blix.ViewPicking.RayThrough(view, pointer)` — panels and off-screen targets included |
| Run bounded (CI, a smoke test, a capture) | `--frames N`, honoured by the host for every application |
| Start a new executable | `src/Demos/Blix.Demos.Chassis/` is the smallest working one — 25-line csproj, no shader boilerplate |
| Expose a value for live tuning in the overlay | `//@tune lo..hi = default` in a GLSL uniform, or `[Tune(min,max)]` on a C# field |
| Register a debug producer (subsystem, asset, scene instance) | `debugSystem.Register(contributor)` from `OnLoad` — implement `IDebuggable` / `IDebugGeometrySource` / `IDebugSelectable` / `IDebugInspectable` / `IDebugUi` independently |
| Save a frame snapshot to disk | Press `F12` (runtime-owned) — writes `dumps/frame-NNNNNN.json` via `JsonDumpSink` |
| Toggle the diagnostics overlay | Press `` ` `` (backtick) |
| Add a reusable shader primitive | `src/Blix.Shaders/<concept>.glsl` (one concept per file, `blix_`-prefixed symbols) |

## Build + run

```sh
dotnet build Blix.sln
./blix ls
./blix run <app> [args...]
./blix run <project>:<app> [args...]
./blix test
```

The root `blix` script owns the macOS Vulkan environment and then executes the
indexed application. The older `tools/run-*.sh` scripts remain for compatibility
and specialized setup; they are not the pattern for a new executable. See
[Workflow](workflow.md) for project discovery, app declarations, shared
arguments, and verification gates.
