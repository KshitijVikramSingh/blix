# Architecture

The engine is organised so that game code lives in `Blix` (the root namespace, the layer game code targets), the renderer spine lives below it (`Blix.Render` → `Blix.Graphics` → `Blix.Graphics.OpenGL`), and platform contracts (windowing, input, audio host, diagnostics) live below everything in `Blix.Core`.

This doc orients you. For detail:
- Renderer architecture: [`renderer.md`](renderer.md)
- Game-engine layer reference: [`blix.md`](blix.md)

## Project graph

```
Blix.Demos.ShaderLab           ← the acceptance demo + entry point
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
   ↑                              (handles, pipelines, surfaces, vertex types, shader sources)
Blix.Graphics.OpenGL           ← OpenGL backend
Blix.Graphics.Images           ← image decode (StbImageSharp)
Blix.Diagnostics               ← contribution-based debug system
        ↑                         (IDebuggable, DebugContext, scoped channels)
Blix.Core                      ← platform contracts (no implementations)
                                  (IRenderHost, IAudioHost, IDebugHost,
                                   IInputHandler, IRuntimeDiagnosticsSink, Key,
                                   MouseButton, RenderFrameContext)

Blix.Audio                     ← audio command language (IAudioDevice)
Blix.Audio.OpenAL              ← OpenAL Soft backend
```

Every cross-project dependency in the source tree fits one of the arrows above. Nothing above Blix.Core depends on a windowing/audio backend directly — `Blix.Runtime.OpenTK` is the only project that wires `IRenderHost`/`IAudioHost`/`IDebugHost` to concrete implementations.

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

**GLSL includes.** `Blix.Graphics.GlslPreprocessor.Preprocess(source, readInclude)` resolves `#include "filename"` directives by inlining the referenced content. Recursive; cycle-detected. File I/O stays in the caller via the `readInclude` callback. The demo wires its shaders through it; `pbr_core.glsl` is shared by `cube.frag` (static lit) and `skin.lit.frag` (skinned lit).

**GLSL ASCII only.** Apple's GL 4.1 / GLSL 4.10 compiler rejects non-ASCII characters even in comments. Keep shader files pure ASCII.

## Where to find things

| If you want... | Look at... |
| --- | --- |
| Render a frame, write a shader, set up a pipeline | [`renderer.md`](renderer.md) |
| Make a `Game` subclass, place an object, animate it, query collisions | [`blix.md`](blix.md) |
| Add a host facet (audio, gamepads, networking) | `src/Blix.Core/` for the contract, then implement in `src/Blix.Runtime.OpenTK/` |
| Add a new asset type | `src/Blix.Assets/` (importer + intermediate data type) |
| Add a new debug control | `IDebuggable.Debug(DebugContext)` — see "Diagnostics" in [`renderer.md`](renderer.md) |

## Build + run

```sh
dotnet build Blix.sln
dotnet run --project src/Blix.Demos.ShaderLab/Blix.Demos.ShaderLab.csproj
```

See top-level [`README.md`](../README.md) for platform notes (macOS OpenAL Soft, GLSL ASCII).
