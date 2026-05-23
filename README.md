# Blix

Blix is a small, code-first native game engine kit for C# / .NET. It provides explicit building blocks for rendering, audio, assets, animation, collision, diagnostics, and game loops — without forcing an ECS, editor-first workflow, or monolithic scene model.

Every type has named fields and a deliberate-limits list. Concrete `Camera3D` / `DirectionalLight` / `GameObject` instead of entity-component soup. One host adapter (`Blix.Runtime.OpenTK`) on top of a typed graphics-command layer with an OpenGL backend. PBR-grade rendering underneath — HDR + IBL, cascade and cube shadow maps, SSR, volumetric fog, bloom, ACES/AgX tonemapping — kinematic physics + collision, glTF skeletal animation, OpenAL positional audio, sprite/text UI.

No editor, no scripting, no plugin system, no asset cache, no hot reload. No shipped games yet. Built to be read.

## Documentation

- [`docs/architecture.md`](docs/architecture.md) — orientation: project graph, host contracts, conventions, where to find things.
- [`docs/renderer.md`](docs/renderer.md) — the renderer spine: graphics language, frame pipeline, PBR, shadows, HDR + IBL, post-process stack, glass, sprite+text, diagnostics, asset pipeline, the shader library.
- [`docs/blix.md`](docs/blix.md) — the layer game code targets: loop, scene primitives, cameras, lights, animation, skeletal, physics, geometry, audio, picking.
- [`docs/walkthrough.md`](docs/walkthrough.md) — the Sponza demo's architecture: how the cascade + cube shadows, HDR scene buffer, material G-buffer, IBL, SSR, fog, and bloom passes hook together inside a single `IGameLoop`.

## Demos

Three demos ship in `src/`. Each one is an entry point, all run against the same engine.

### Sponza Walkthrough (flagship)

```sh
dotnet run --project src/Blix.Demos.Walkthrough/Blix.Demos.Walkthrough.csproj
```

Full PBR walkthrough through the Sponza atrium with an HDR sky probe, 3-cascade directional shadows, per-brazier cube shadow maps, ground-floor SSR gated by a material G-buffer, GGX-prefiltered specular IBL with BRDF LUT, volumetric fog with sun god-rays and point-light scatter, volumetric fire (VDB-backed), dual-filter bloom, and selectable ACES / AgX / Reinhard / Neutral tonemap with full grade controls. Every dial is wired to a debug slider.

- `WASD` / `Space` / `Ctrl` — move; `Cmd` to sprint.
- Mouse look. `C` releases the cursor for slider use.
- `Esc` — quit.

### ShaderLab

```sh
dotnet run --project src/Blix.Demos.ShaderLab/Blix.Demos.ShaderLab.csproj
```

Lower-key acceptance demo exercising the rest of the engine: multi-light PCSS shadows, glass, fur, holograms, sprites + text, glTF skeletal animation, OpenAL positional audio, picking.

- `WASD` — move. Mouse-drag rotates the directional light.
- `Cmd+C` / `Ctrl+C` — toggle dev mode (cursor capture + ImGui overlay flip together).
- `R` — reset; `Esc` — quit. Left-click in dev mode picks a world object.

### Sponza Modern

```sh
# First time: populate the Assets/ dir from your local Khronos Sponza download.
tools/setup-sponza-modern.sh

dotnet run --project src/Blix.Demos.SponzaModern/Blix.Demos.SponzaModern.csproj
```

The Khronos Intel Sponza PBR-MR scene plus the optional curtains / ivy / trees add-on packs. Validates the engine subsystems that grew out of the original Walkthrough — `GltfSceneInstance` for the per-pack import, `EnvironmentProbe` for HDR sky + IBL bake, `PbrSceneRenderer` for the lit + cascade-shadow draws, `PostProcessStack` for fog + SSR + bloom + composite — on a scene with foliage, double-sided geometry, and alpha-test cutouts the classic Sponza didn't exercise.

Source assets are multi-GB and not committed; the setup script copies just the runtime-needed `.gltf` + `.bin` + textures from your `~/Downloads`. Source: <https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/IntelSponza>.

## Build

```sh
dotnet build Blix.sln
```

Target framework: net8.0. The 2D physics CLI test harness lives at `src/Blix.Test.Physics2D` — run with `dotnet run --project src/Blix.Test.Physics2D/Blix.Test.Physics2D.csproj`.

## Roadmap

- **Particles** — generic GPU/CPU emitter with sorted billboards, soft-particle depth, HDR + bloom integration. First consumer of the new shader library.
- **More demos** — beyond Sponza + ShaderLab. Likely a focused VFX scene to validate the particle system, then something with gameplay.

The shader library at `src/Blix.Shaders/` (tonemap, noise, PBR primitives) is the substrate both new features build on; the include preprocessor + `ShaderLoader` make new shaders cheap to author.

## Platform notes

- **macOS audio requires OpenAL Soft.** Apple's bundled `OpenAL.framework` has been deprecated since macOS 10.15 and silently no-ops on most source calls (looping, playback transitions). Install via `brew install openal-soft` — `OpenALAudioDevice` probes the standard Homebrew prefixes and points the loader at the working library.
- **macOS GLSL is ASCII-only.** Apple's GL 4.1 / GLSL 4.10 compiler reports non-ASCII characters (including in comments) as "premature EOF on last line." Keep shaders pure ASCII.
- **macOS GLSL reserves `noise1..4`.** Defining a local `noise3` triggers a "return type differs" error. Prefix with `v` (value-noise) or use longer names.
