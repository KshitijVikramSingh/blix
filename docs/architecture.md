# Architecture

The engine is organised so that game code lives in `Blix` (the root namespace, the layer game code targets), the renderer spine lives below it (`Blix.Graphics` → `Blix.Graphics.Vulkan`), and platform contracts (windowing, input, audio host, diagnostics) live below everything in `Blix.Core`.

One backend + runtime ship today: the Vulkan + Silk.NET pair. The original OpenGL backend (`Blix.Graphics.OpenGL`) and its OpenTK runtime (`Blix.Runtime.OpenTK`) were sunset, along with the GL-only `Blix.Render` engine-facing API (name-keyed `Material`/`MaterialResolver`/`PostProcessStack`/`PbrSceneRenderer`) and the four heavy GL demos. Ten Vulkan demos ship: `Blix.Demos.VulkanHello`, `Blix.Demos.VulkanGraph`, `Blix.Demos.VulkanLit`, `Blix.Demos.VulkanInstanced`, and `Blix.Demos.VulkanSponza` cover validation, render-graph topology, the full lit/shadow/PBR/IBL/bloom scene, the per-instance instancing foundation (5000-cube gate), and the GPU-driven Intel Sponza performance + asset-pipeline target; `Blix.Demos.VulkanParticles` is the CPU-particle + post-process VFX showcase; and four are complete games — `Blix.Demos.Pong` (2D, on the rebuilt Vulkan `SpriteBatch`), `Blix.Demos.Runner` (a 3D endless runner exercising instancing + skeletal animation + kinematic physics), `Blix.Demos.TankArena` (a survival shooter on the `Transform3D` parenting rig over a sun-shadow + HDR graph), and `Blix.Demos.Bulwark` (a tower defense exercising pointer picking, multi-front A\* navigation, and skinned-mesh instancing). The engine was progressively reshaped around the Vulkan target — name-keyed materials gave way to SPIR-V-reflected binding (see [the Vulkan binding model](#the-vulkan-binding-model) below).

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
        ↑
Blix.Runtime.Silk              ← Vulkan window/runtime adapter
                                  (Silk.NET window + IVkSurface + MoltenVK bootstrap,
                                   VkLineDrawer for debug overlay)
        ↑
Blix                           ← layer game code targets
   ↑   ↑      ↑       ↑           (loop, scene, animation, physics, audio,
   │   │      │       │            glTF import + GltfTextureLoader)
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
                                  included by demo shaders, resolved via a
                                  glslc -I path; pbr/tonemap/noise/fullscreen/bloom)
Blix.Diagnostics               ← contribution-based debug system
        ↑                         (DebugFrame snapshots + history ring,
                                   Values/Controls/Draw/Stats/Timers/Events
                                   channels, sinks, selection + picking,
                                   //@tune + [Tune] live-tuning panels,
                                   PeriodicConsoleSummarySink for stdout digest)
Blix.Core                      ← platform contracts (no implementations)
                                  (IRenderHost, IAudioHost, IDebugHost,
                                   IInputHandler, IRuntimeDiagnosticsSink, Key,
                                   MouseButton, RenderFrameContext)

Blix.Audio                     ← audio command language (IAudioDevice)
Blix.Audio.OpenAL              ← OpenAL Soft backend
```

Every cross-project dependency in the source tree fits one of the arrows above. Nothing above `Blix.Core` depends on a windowing/audio backend directly — `Blix.Runtime.Silk` is the only project that wires `IRenderHost`/`IAudioHost`/`IDebugHost` to concrete implementations.

## The Vulkan binding model

The renderer's defining choice is that the binding model is *derived*, not declared by hand. At build time the compiled SPIR-V is reflected (via spirv-cross sidecars) into a `ShaderInterface`: descriptor sets + std140 UBO layouts + push-constant ranges, keyed by the set/binding/member names in the shader. `CreateMaterial(program, setIndex, …)` then allocates a `MaterialBindings` against one reflected set — `SetUniform("uTint", …)` / `SetTexture(binding, …)` write into it by name, and `.Handle` is the backend-neutral `MaterialHandle` a `GameObject` stores. Sets are organised by lifetime (frame-global, per-material, per-draw), and per-draw data rides push constants or a transient descriptor pool refilled each frame. There is no parallel hand-maintained binding table to drift out of sync with the shader source.

The Vulkan path is now the sole renderer, and in `VulkanSponza` it has moved well past the old GL feature set: that SPIR-V-reflected binding model, per-material descriptor sets, push constants, per-draw transient descriptor pools, a declarative render graph (`Blix.Graphics.Vulkan/RenderGraph.cs`), glTF + skinning (via the existing `Blix.Assets` importers), PBR + IBL (procedural-sky or cooked-probe environment + irradiance cube + split-sum BRDF LUT), HDR + ACES/AgX tonemap, and a separable-Gaussian bloom chain. `VulkanSponza` adds cascaded directional shadows (texel-snapped + cached), a depth pre-pass, froxel volumetric fog, GPU-driven indirect rendering, screen-space-error LOD over meshopt chains, and the cooked-asset pipeline (`.blixmesh`/`.blixtex`/`.blixprobe`) streamed through the engine's `GltfTextureLoader` + `AsyncLoadQueue` + `MeshBundler`. The 2D path (`SpriteBatch` + `Font`, used by `Pong`) was rebuilt on the Vulkan binding model. SSR and the dual-filter bloom were GL-only techniques and did not survive the sunset; froxel fog now lives on Vulkan in VulkanSponza.

`Blix.Shaders` isn't a code project — it's a folder of `blix_`-prefixed `.glsl` library files. Demo shaders `#include "<file>.glsl"`; each demo's `CompileSpirV` target passes `glslc -I <src/Blix.Shaders>` so the include resolves at cook time, and lists the library files in the target's `Inputs` so edits retrigger the cook (see **Shader library** under Conventions below).

## Host contracts

`Blix.Core` owns the platform-facing interfaces. The runtime (`Blix.Runtime.Silk.Window`) implements all of them; game code consumes them. Game code never references `Blix.Runtime.Silk` directly.

| Contract | Defined in | What it does |
| --- | --- | --- |
| `IRenderHost` | `Blix.Core` | Runtime knobs: `SetTitle`, `RequestClose`, `SetCursorCaptured`, `LogicalSize`. |
| `IAudioHost` | `Blix.Core` | Hands out the `IAudioDevice` (`Blix.Audio`) for the running session. |
| `IDebugHost` | `Blix.Diagnostics` | Exposes the active `DebugContext` (for per-frame writes) and the full `DebugSystem` (for contributor registration, freeze, selection). |
| `IInputHandler` | `Blix.Core` | Edge-triggered input events: `OnKeyDown/Up`, `OnMouseDown/Up`, `OnMouseMove`, `OnMouseWheel`. |
| `IRuntimeDiagnosticsSink` | `Blix.Core` | Per-frame backend introspection: receives `FrameDebugPacket` + `ResourceRegistrySnapshot`. |

The game implements `IGameLoop` (in `Blix`) and optionally `IInputHandler` and `IDebuggable`. The runtime forwards events only when the interface is present.

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

**GLSL includes.** `Blix.Graphics.GlslPreprocessor.PreprocessDetailed` resolves `#include "filename"` directives by inlining the referenced content. Recursive, cycle-detected, honors `#pragma once`. Emits `#line N <source-id>` directives around every inclusion so shader compile errors report the original file's line numbers; the source-id-to-filename map flows through `ShaderSources` to the diagnostic formatter, which prints a `--- Source map ---` block before the info log so messages like `1:42: ...` decode to a real file. File I/O stays in the caller via a `readInclude` callback.

`Blix.Graphics.ShaderLoader.LoadVertexFragment(vertPath, fragPath, includeDirs?, defines?)` bundles read + preprocess + naming + source-map plumbing. The optional `defines` dictionary injects `#define KEY VALUE` lines right after `#version` so the same library function can serve multiple variants.

**Shader library.** Engine-shared GLSL lives at `src/Blix.Shaders/*.glsl` with a `blix_` prefix on every symbol. Demo shaders consume them with `#include "<file>.glsl"`, resolved by the `glslc -I <src/Blix.Shaders>` path each demo's `CompileSpirV` target passes (the library files are also in that target's `Inputs`, so editing one re-cooks every dependent shader). New library files start with `#pragma once`.

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
# or the demos with no special setup:
dotnet run --project src/Blix.Demos.VulkanGraph/Blix.Demos.VulkanGraph.csproj
```

See top-level [`README.md`](../README.md) for the full per-demo run commands and platform notes (macOS OpenAL Soft, shader ASCII, MoltenVK setup).
