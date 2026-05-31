# Architecture

The engine is organised so that game code lives in `Blix` (the root namespace, the layer game code targets), the renderer spine lives below it (`Blix.Render` → `Blix.Graphics` → backend), and platform contracts (windowing, input, audio host, diagnostics) live below everything in `Blix.Core`.

Two backends + runtimes ship today: the original OpenGL + OpenTK pair (used by the Sponza/ShaderLab demos), and the newer Vulkan + Silk.NET pair. The Vulkan path is the active development target — `Blix.Demos.VulkanHello`, `Blix.Demos.VulkanGraph`, `Blix.Demos.VulkanLit`, and `Blix.Demos.VulkanSponza` cover validation, render-graph topology, the full lit/shadow/PBR/IBL/bloom scene, and the GPU-driven Intel Sponza performance + asset-pipeline target respectively. The engine has been progressively reshaped around the Vulkan target; see [`vulkan-friction.md`](vulkan-friction.md) for the friction notes that drove the reshape.

**Library, not framework.** The engine is a set of composable primitives game code calls — not a control-inverting framework. The split: the **engine owns asset loading, reading, and bundling** (decode/upload/dedup/stream textures, pack geometry into shared buffers, run an off-thread load queue); the **game owns synthesis and composition** (which passes run, how draws are recorded, material/pipeline choice, render-graph topology). There is no `SceneRenderer` that owns read→cull→draw: `VulkanSponza` composes the engine primitives (`MeshBundler`, `AsyncLoadQueue`, `GltfTextureLoader`, the `RenderGraph`) itself and keeps its own draw groups + LOD/cull policy. New rendering capability lands as a primitive the game calls, not a stage the engine runs for you.

This doc orients you. For detail:
- Renderer architecture: [`renderer.md`](renderer.md)
- Game-engine layer reference: [`blix.md`](blix.md)
- Flagship demo deep dive: [`walkthrough.md`](walkthrough.md)
- Vulkan-backend reshape notes: [`vulkan-friction.md`](vulkan-friction.md)

## Project graph

```
Blix.Demos.Walkthrough         ← classic Sponza HDR walkthrough (flagship GL demo)
Blix.Demos.SponzaModern        ← Khronos Intel Sponza + add-ons (PBR-MR scene)
Blix.Demos.ShaderLab           ← shader-feature acceptance demo
Blix.Demos.VulkanHello         ← Vulkan validation demo (cube + debug overlay)
Blix.Demos.VulkanGraph         ← Vulkan render-graph topology demo (3-pass invert)
Blix.Demos.VulkanLit           ← Vulkan PBR + IBL + shadows + skinning + bloom
Blix.Demos.VulkanSponza        ← Khronos Intel Sponza on Vulkan (GPU-driven indirect,
                                  SSE LOD, cascaded shadows, froxel fog, streamed cooked assets)
        ↑
Blix.Runtime.OpenTK            ← OpenGL window/runtime adapter
Blix.Runtime.Silk              ← Vulkan window/runtime adapter
                                  (Silk.NET window + IVkSurface + MoltenVK bootstrap,
                                   VkLineDrawer for debug overlay)
        ↑
Blix                           ← layer game code targets
   ↑   ↑      ↑       ↑           (loop, scene, animation, physics, audio,
   │   │      │       │            glTF import + GltfTextureLoader)
   │   │      │   Blix.Assets    ← asset DB + importers
   │   │      │       ↑             (texture, OBJ, material, font, WAV)
   │   │   Blix.Render          ← engine-facing rendering + asset pipeline
   │   │      ↑                    (Mesh, Material, MaterialResolver, SpriteBatch, Font,
   │   │                            DebugDraw; MeshBundler, AsyncLoadQueue, ResourceUploader)
   │ Blix.Geometry              ← primitives + intersection tests
   │                              (Bounds3/2, Sphere, Capsule, OBB, mesh colliders)
Blix.Graphics                  ← graphics command language
   ↑   ↑                          (handles, pipelines, surfaces, vertex types,
   │   │                           shader sources, GLSL preprocessor + ShaderLoader)
   │ Blix.Graphics.OpenGL       ← OpenGL backend
   Blix.Graphics.Vulkan         ← Vulkan backend (Silk.NET.Vulkan bindings,
                                   instance/device/swapchain, per-draw transient
                                   descriptor pool, RenderGraph, MaterialBindings,
                                   ShaderReflection: build-time SPIR-V binding +
                                   std140 layout reflection via spirv-cross sidecars)
Blix.Graphics.Images           ← image decode + HDR IBL bake pipeline
                                  (StbImageSharp, EquirectangularToCubemap,
                                   PbrIblBaker, HdrSunFinder)
Blix.Shaders                   ← engine-level GLSL library (no csproj; .glsl
                                  files copied into each demo's bin via csproj
                                  globs; tonemap.glsl, noise.glsl, pbr.glsl)
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

Every cross-project dependency in the source tree fits one of the arrows above. Nothing above `Blix.Core` depends on a windowing/audio backend directly — the two runtime projects (`Blix.Runtime.OpenTK`, `Blix.Runtime.Silk`) are the only ones that wire `IRenderHost`/`IAudioHost`/`IDebugHost` to concrete implementations.

The Vulkan path has caught up and, in `VulkanSponza`, moved ahead: a SPIR-V-reflected binding model (descriptor sets + std140 UBO layouts + push ranges derived from the compiled `.spv`, not hand-authored), per-material descriptor sets, push constants, per-draw transient descriptor pools, a declarative render graph (`Blix.Graphics.Vulkan/RenderGraph.cs`), glTF + skinning (via the existing `Blix.Assets` importers), PBR + IBL (procedural-sky or cooked-probe environment + irradiance cube + split-sum BRDF LUT), HDR + ACES tonemap, and a separable-Gaussian bloom chain. `VulkanSponza` adds cascaded directional shadows (texel-snapped + cached), froxel volumetric fog, GPU-driven indirect rendering, screen-space-error LOD over meshopt chains, and the cooked-asset pipeline (`.blixmesh`/`.blixtex`/`.blixprobe`) streamed through the engine's `GltfTextureLoader` + `AsyncLoadQueue` + `MeshBundler`. What's still GL-only is SSR and the dual-filter bloom (the Vulkan demos use the simpler Gaussian variant); the Vulkan path is the active target and the GL Sponza/ShaderLab demos are being wound down rather than ported feature-for-feature.

`Blix.Shaders` isn't a code project — it's a folder of `.glsl` files copied into each demo's output via `<None Include="..\Blix.Shaders\**\*.glsl" Link="Shaders\lib\...">` in the demo csproj. Demo shaders write `#include "lib/tonemap.glsl"` and the include preprocessor resolves it at load time. See [renderer.md → Shader library](renderer.md#shader-library).

## Host contracts

`Blix.Core` owns the platform-facing interfaces. The runtime (`Blix.Runtime.OpenTK.Window`) implements all of them; game code consumes them. Game code never references `Blix.Runtime.OpenTK` directly.

| Contract | Defined in | What it does |
| --- | --- | --- |
| `IRenderHost` | `Blix.Core` | Runtime knobs: `SetTitle`, `RequestClose`, `SetCursorCaptured`, `LogicalSize`. |
| `IAudioHost` | `Blix.Core` | Hands out the `IAudioDevice` (`Blix.Audio`) for the running session. |
| `IDebugHost` | `Blix.Diagnostics` | Exposes the active `DebugContext` (for per-frame writes) and the full `DebugSystem` (for contributor registration, freeze, selection). |
| `IInputHandler` | `Blix.Core` | Edge-triggered input events: `OnKeyDown/Up`, `OnMouseDown/Up`, `OnMouseMove`, `OnMouseWheel`. |
| `IRuntimeDiagnosticsSink` | `Blix.Core` | Per-frame backend introspection: receives `FrameDebugPacket` + `ResourceRegistrySnapshot`. |

The game implements `IGameLoop` (in `Blix`) and optionally `IInputHandler` and `IDebuggable`. The runtime forwards events only when the interface is present.

`Blix.Runtime.OpenTK.Window` implements `IRenderHost`, `IAudioHost`, and `IDebugHost` simultaneously — game code reads them via `Host`, `Host as IAudioHost`, `Host as IDebugHost` from inside `Game`.

## Conventions

**Coordinate system.** World space is right-handed. `+X` right, `+Y` up, default camera looks down `-Z`. A camera at `(0, 0, 2)` sees objects around the origin.

**Matrices.** Column-vector convention everywhere: `projection * view * model * position`. CPU-side matrices are `System.Numerics.Matrix4x4`, but they're treated as column-vector matrices by the engine — `GraphicsMatrices` builds them in that form, and `Blix.Graphics.OpenGL` uploads them with `transpose: false`.

**Gotcha:** `System.Numerics.Matrix4x4.CreateTranslation` produces *row-vector* matrices (translation in `M41/M42/M43`). The engine's `GraphicsMatrices.CreateModel` produces column-vector (translation in `M14/M24/M34`). Mixing them silently produces wrong skinning, wrong lighting, wrong bounds. Stay inside `GraphicsMatrices` for any math that flows into shader uniforms.

**Pixel coordinates.** `IRenderHost.LogicalSize` returns the window's client area in logical pixels (same coordinate system as mouse events). `RenderFrameContext.Width/Height` is the framebuffer in physical pixels (typically 2× on Retina). Don't mix them — `Camera3D.ScreenPointToRay` needs logical pixels because mouse coords are logical.

**GLSL includes.** `Blix.Graphics.GlslPreprocessor.PreprocessDetailed` resolves `#include "filename"` directives by inlining the referenced content. Recursive, cycle-detected, honors `#pragma once`. Emits `#line N <source-id>` directives around every inclusion so GLSL compile errors report the original file's line numbers; the source-id-to-filename map flows through `ShaderSources` → the OpenGL diagnostic formatter, which prints a `--- Source map ---` block before the info log so messages like `1:42: ...` decode to a real file. File I/O stays in the caller via a `readInclude` callback.

`Blix.Graphics.ShaderLoader.LoadVertexFragment(vertPath, fragPath, includeDirs?, defines?)` bundles read + preprocess + naming + source-map plumbing; both demos use it. The optional `defines` dictionary injects `#define KEY VALUE` lines right after `#version` so the same library function can serve multiple variants.

**Shader library.** Engine-shared GLSL lives at `src/Blix.Shaders/*.glsl` with a `blix_` prefix on every symbol. Demo shaders consume them with `#include "lib/<file>.glsl"`. New library files start with `#pragma once`. See [renderer.md → Shader library](renderer.md#shader-library).

**GLSL ASCII only.** Apple's GL 4.1 / GLSL 4.10 compiler rejects non-ASCII characters even in comments. Keep shader files pure ASCII.

**Don't shadow GLSL builtins.** GLSL reserves `noise1..4`; defining a local `noise3` triggers a "return type differs" error on macOS. Prefix with `v`/`h` or use longer names.

## Where to find things

| If you want... | Look at... |
| --- | --- |
| Render a frame, write a shader, set up a pipeline | [`renderer.md`](renderer.md) |
| See HDR + IBL + shadows + post-process wired together | [`walkthrough.md`](walkthrough.md) |
| Make a `Game` subclass, place an object, animate it, query collisions | [`blix.md`](blix.md) |
| Add a host facet (audio, gamepads, networking) | `src/Blix.Core/` for the contract, then implement in `src/Blix.Runtime.OpenTK/` |
| Add a new asset type | `src/Blix.Assets/` (importer + intermediate data type) |
| Bundle meshes into shared buffers / stream glTF textures | `Blix.Render.MeshBundler`, `Blix.Render.AsyncLoadQueue<T>`, `GltfTextureLoader` — see "Asset pipeline" in [`renderer.md`](renderer.md) |
| Add a new debug control / stat / timer / event | `IDebuggable.Debug(DebugContext)` — see "Diagnostics" in [`renderer.md`](renderer.md) |
| Expose a value for live tuning in the overlay | `//@tune lo..hi = default` in a GLSL uniform, or `[Tune(min,max)]` on a C# field — see "Live tuning" in [`renderer.md`](renderer.md) |
| Register a debug producer (subsystem, asset, scene instance) | `debugSystem.Register(contributor)` from `OnLoad` — implement `IDebuggable` / `IDebugGeometrySource` / `IDebugSelectable` / `IDebugInspectable` / `IDebugUi` independently |
| Save a frame snapshot to disk | Press `F12` (runtime-owned) — writes `dumps/frame-NNNNNN.json` via `JsonDumpSink` |
| Toggle the diagnostics overlay | Press `` ` `` (backtick) |
| Add a reusable shader primitive | `src/Blix.Shaders/<concept>.glsl` (one concept per file, `blix_`-prefixed symbols) |

## Build + run

```sh
dotnet build Blix.sln
dotnet run --project src/Blix.Demos.Walkthrough/Blix.Demos.Walkthrough.csproj
# or
dotnet run --project src/Blix.Demos.ShaderLab/Blix.Demos.ShaderLab.csproj
```

See top-level [`README.md`](../README.md) for platform notes (macOS OpenAL Soft, GLSL ASCII).
