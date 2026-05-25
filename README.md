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

Four demos ship in `src/`. Each is an entry point against the engine. The Vulkan demo is the active development target; the OpenGL demos are stable but in the process of being sunset as the engine reshapes around Vulkan as the primary backend (see [`docs/vulkan-friction.md`](docs/vulkan-friction.md) for the in-flight reshape notes).

### Vulkan Cube (active development)

```sh
tools/run-vulkan-hello.sh
```

Spinning depth-tested 24-vertex cube on the Vulkan backend via MoltenVK on macOS. Exercises swapchain creation, per-frame command buffers + sync, descriptor sets + per-frame UBO ring, VkQueryPool timing infrastructure, and a debug-line overlay pass drawn on top of the scene (world axes, light direction arrow, cube OBB wireframe). Diagnostics surface (Values / Stats / Timers / GPU pass timings / Events) flows through the same `IDebuggable` API as the OpenGL demos.

Runs at vsync with full validation-layer cleanliness (`BLIX_VK_VALIDATE=1` to enable Khronos validation layers). `BLIX_DIAG_INTERVAL=<frames>` controls how often the periodic console digest fires (default every 60 frames; `BLIX_DIAG=off` disables).

Setup: `brew install molten-vk vulkan-loader vulkan-headers vulkan-tools vulkan-validationlayers shaderc`. The launcher script (`tools/run-vulkan-hello.sh`) sets the `DYLD_FALLBACK_LIBRARY_PATH` + `VK_ICD_FILENAMES` + `VK_LAYER_PATH` env vars before `dotnet run` since dyld snapshots `DYLD_*` at process start and won't pick them up from runtime `setenv`.

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

# Cook PNG/JPEG -> .blixtex, sky HDR -> .blixprobe, glTF -> .blixmesh. One-shot;
# rerun only when sources change. ~6s startup -> ~0.5s startup.
tools/cook-sponza-modern.sh

dotnet run --project src/Blix.Demos.SponzaModern/Blix.Demos.SponzaModern.csproj
```

The Khronos Intel Sponza PBR-MR scene plus the optional curtains / ivy / trees add-on packs. Validates the engine subsystems that grew out of the original Walkthrough — `GltfSceneInstance` for the per-pack import, `EnvironmentProbe` for HDR sky + IBL bake, `PbrSceneRenderer` for the lit + cascade-shadow draws, `PostProcessStack` for fog + SSR + bloom + composite — on a scene with foliage, double-sided geometry, and alpha-test cutouts the classic Sponza didn't exercise.

Source assets are multi-GB and not committed; the setup script copies just the runtime-needed `.gltf` + `.bin` + textures from your `~/Downloads`. Source: <https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/IntelSponza>.

#### Cooked asset pipeline

Three sibling binary formats let the runtime skip the slow paths (PNG decode, equirect → IBL convolution, glTF JSON+`.bin` parse + accessor walk). All cookers live in `src/Blix.Tools.Cook` (`blix-cook textures|probe|mesh`).

| Format | Cook input | What it skips at load | Code |
| --- | --- | --- | --- |
| `.blixtex` | PNG / JPEG | StbImage decode + glGenerateMipmap; supports BC7/BC5 | `src/Blix.Graphics.Images/BlixTex.cs` |
| `.blixprobe` | HDR equirect | Equirect → cube + diffuse irradiance + GGX prefilter + BRDF LUT bake | `src/Blix.Graphics.Images/BlixProbe.cs` |
| `.blixmesh` | `.gltf` / `.glb` | SharpGLTF `.bin` validation + per-accessor walk + vertex packing | `src/Blix.Assets/BlixMesh.cs` |

When a `.blixmesh` sibling exists, the importer also switches `ModelRoot.Load` to a lite path (`ReadContext.Create` + `ValidationMode.Skip` + an empty buffer reader) — the JSON still parses for material descriptors, but the multi-megabyte `.bin` validation is skipped entirely. Textures upload progressively through `ResourceUploader`, smallest mip first, so materials bind a usable-if-blurry texture within a frame and sharpen over the next few.

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
