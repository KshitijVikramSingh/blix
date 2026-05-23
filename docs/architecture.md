# Architecture

The engine is organised so that game code lives in `Blix` (the root namespace, the layer game code targets), the renderer spine lives below it (`Blix.Render` → `Blix.Graphics` → `Blix.Graphics.OpenGL`), and platform contracts (windowing, input, audio host, diagnostics) live below everything in `Blix.Core`.

This doc orients you. For detail:
- Renderer architecture: [`renderer.md`](renderer.md)
- Game-engine layer reference: [`blix.md`](blix.md)
- Flagship demo deep dive: [`walkthrough.md`](walkthrough.md)

## Project graph

```
Blix.Demos.Walkthrough         ← classic Sponza HDR walkthrough (flagship demo)
Blix.Demos.SponzaModern        ← Khronos Intel Sponza + add-ons (PBR-MR scene)
Blix.Demos.ShaderLab           ← shader-feature acceptance demo
        ↑
Blix.Runtime.OpenTK            ← the current window/runtime adapter
        ↑                         (OpenTK window + GL context + ImGui overlay)
Blix                           ← layer game code targets
   ↑   ↑      ↑       ↑           (loop, scene, animations, physics, audio, glTF)
   │   │      │       │
   │   │      │   Blix.Assets    ← asset DB + importers
   │   │      │       ↑             (texture, OBJ, material, font, WAV)
   │   │   Blix.Render          ← engine-facing rendering
   │   │      ↑                    (Mesh, Material, MaterialResolver, SpriteBatch, Font, DebugDraw)
   │ Blix.Geometry              ← primitives + intersection tests
   │                              (Bounds3/2, Sphere, Capsule, OBB, mesh colliders)
Blix.Graphics                  ← graphics command language
   ↑                              (handles, pipelines, surfaces, vertex types,
   │                               shader sources, GLSL preprocessor + ShaderLoader)
Blix.Graphics.OpenGL           ← OpenGL backend
Blix.Graphics.Images           ← image decode + HDR IBL bake pipeline
                                  (StbImageSharp, EquirectangularToCubemap,
                                   PbrIblBaker, HdrSunFinder)
Blix.Shaders                   ← engine-level GLSL library (no csproj; .glsl
                                  files copied into each demo's bin via csproj
                                  globs; tonemap.glsl, noise.glsl, pbr.glsl)
Blix.Diagnostics               ← contribution-based debug system
        ↑                         (IDebuggable, DebugContext, scoped channels)
Blix.Core                      ← platform contracts (no implementations)
                                  (IRenderHost, IAudioHost, IDebugHost,
                                   IInputHandler, IRuntimeDiagnosticsSink, Key,
                                   MouseButton, RenderFrameContext)

Blix.Audio                     ← audio command language (IAudioDevice)
Blix.Audio.OpenAL              ← OpenAL Soft backend
```

Every cross-project dependency in the source tree fits one of the arrows above. Nothing above `Blix.Core` depends on a windowing/audio backend directly — `Blix.Runtime.OpenTK` is the only project that wires `IRenderHost`/`IAudioHost`/`IDebugHost` to concrete implementations.

`Blix.Shaders` isn't a code project — it's a folder of `.glsl` files copied into each demo's output via `<None Include="..\Blix.Shaders\**\*.glsl" Link="Shaders\lib\...">` in the demo csproj. Demo shaders write `#include "lib/tonemap.glsl"` and the include preprocessor resolves it at load time. See [renderer.md → Shader library](renderer.md#shader-library).

## Host contracts

`Blix.Core` owns the platform-facing interfaces. The runtime (`Blix.Runtime.OpenTK.Window`) implements all of them; game code consumes them. Game code never references `Blix.Runtime.OpenTK` directly.

| Contract | Defined in | What it does |
| --- | --- | --- |
| `IRenderHost` | `Blix.Core` | Runtime knobs: `SetTitle`, `RequestClose`, `SetCursorCaptured`, `LogicalSize`. |
| `IAudioHost` | `Blix.Core` | Hands out the `IAudioDevice` (`Blix.Audio`) for the running session. |
| `IDebugHost` | `Blix.Diagnostics` | Exposes the active `DebugContext` so game code can gate debug-only work. |
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
| Add a new debug control | `IDebuggable.Debug(DebugContext)` — see "Diagnostics" in [`renderer.md`](renderer.md) |
| Add a reusable shader primitive | `src/Blix.Shaders/<concept>.glsl` (one concept per file, `blix_`-prefixed symbols) |

## Build + run

```sh
dotnet build Blix.sln
dotnet run --project src/Blix.Demos.Walkthrough/Blix.Demos.Walkthrough.csproj
# or
dotnet run --project src/Blix.Demos.ShaderLab/Blix.Demos.ShaderLab.csproj
```

See top-level [`README.md`](../README.md) for platform notes (macOS OpenAL Soft, GLSL ASCII).
