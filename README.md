# Blix

Blix is a small, code-first native game engine kit for C# / .NET. It provides explicit building blocks for rendering, audio, assets, animation, collision, diagnostics, and game loops — without forcing an ECS, editor-first workflow, or monolithic scene model.

Every type has named fields and a deliberate-limits list. Concrete `Camera3D` / `DirectionalLight` / `GameObject` instead of entity-component soup. One host adapter (`Blix.Runtime.OpenTK`) on top of a typed graphics-command layer with an OpenGL backend. PBR-grade rendering underneath (multi-light PCSS shadows, HDR + IBL, glass, fur), kinematic physics + collision, glTF skeletal animation, OpenAL positional audio, sprite/text UI. One demo (`Blix.Demos.ShaderLab`) exercises every subsystem.

No editor, no scripting, no plugin system, no asset cache, no hot reload. No shipped games yet. Built to be read.

## Documentation

- [`docs/architecture.md`](docs/architecture.md) — orientation: project graph, host contracts, conventions, where to find things.
- [`docs/renderer.md`](docs/renderer.md) — the renderer spine: graphics language, frame pipeline, PBR, shadows, HDR, glass, sprite+text, diagnostics, asset pipeline.
- [`docs/blix.md`](docs/blix.md) — the layer game code targets: loop, scene primitives, cameras, lights, animation, skeletal, physics, geometry, audio, picking.

## Run the demo

```sh
dotnet run --project src/Blix.Demos.ShaderLab/Blix.Demos.ShaderLab.csproj
```

### Keys

- `W/A/S/D` — move the camera.
- Mouse look — rotate the camera. Left-drag rotates the directional light.
- `Cmd/Ctrl+C` — toggle dev mode (single binding flips cursor capture and the diagnostics overlay together). Captured = camera mouselook on, overlay off (game mode). Released = mouselook locked, ImGui + debug draw usable (debug mode).
- `R` — reset camera and lighting.
- Left-click (in dev mode) — pick a world object.
- `Esc` — quit.

### Diagnostics UI

- Present/debug view dropdown: final, scene, opaque, luminance, normals, depth, shadow map, bloom levels.
- Sliders for direct light intensity, ambient fill, sky/environment intensity, bloom, exposure, glass thickness, glass Fresnel reflectance, fur shells/length/density/wind, hologram opacity/rim/scanlines/glitch.
- Read-only state for camera position/angles, light direction, and last input / pick.

## Build

```sh
dotnet build Blix.sln
```

Target framework: net8.0. The solution has 14 projects under `src/`; the demo is the only entry point. The 2D physics CLI test harness lives at `src/Blix.Test.Physics2D` — run with `dotnet run --project src/Blix.Test.Physics2D/Blix.Test.Physics2D.csproj`.

## Platform notes

- **macOS audio requires OpenAL Soft.** Apple's bundled `OpenAL.framework` has been deprecated since macOS 10.15 and silently no-ops on most source calls (looping, playback transitions). Install via `brew install openal-soft` — `OpenALAudioDevice` probes the standard Homebrew prefixes and points the loader at the working library.
- **macOS GLSL is ASCII-only.** Apple's GL 4.1 / GLSL 4.10 compiler reports non-ASCII characters (including in comments) as "premature EOF on last line." Keep shaders pure ASCII.
