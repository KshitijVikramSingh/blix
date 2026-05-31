# Blix

Blix is a small, code-first native game engine kit for C# / .NET. It provides explicit building blocks for rendering, audio, assets, animation, collision, diagnostics, and game loops — without forcing an ECS, editor-first workflow, or monolithic scene model.

Every type has named fields and a deliberate-limits list. Concrete `Camera3D` / `DirectionalLight` / `GameObject` instead of entity-component soup. A typed graphics-command layer (`Blix.Graphics`) over a Vulkan backend (`Blix.Graphics.Vulkan`), driven through a Silk.NET host adapter (`Blix.Runtime.Silk`). PBR-grade rendering underneath — HDR + IBL, cascade and cube shadow maps, froxel volumetric fog, bloom, ACES/AgX tonemapping, GPU-driven indirect draw with screen-space-error LOD — kinematic physics + collision, glTF skeletal animation, OpenAL positional audio, sprite/text UI (`SpriteBatch` + `Font`).

No editor, no scripting, no plugin system, no asset cache, no hot reload. No shipped games yet. Built to be read.

## Documentation

- [`docs/architecture.md`](docs/architecture.md) — orientation: project graph, host contracts, conventions, where to find things.
- [`docs/renderer.md`](docs/renderer.md) — **historical**: the sunset OpenGL renderer, retained for its rendering-technique writeups (PBR, shadows, HDR + IBL, tonemap). For the current Vulkan backend see [`docs/vulkan-friction.md`](docs/vulkan-friction.md) and [`docs/architecture.md`](docs/architecture.md).
- [`docs/blix.md`](docs/blix.md) — the layer game code targets: loop, scene primitives, cameras, lights, animation, skeletal, physics, geometry, audio, picking.
- [`docs/walkthrough.md`](docs/walkthrough.md) — **historical**: the retired GL Walkthrough demo's architecture, retained for reference. For current scene-grade demos see VulkanSponza / VulkanLit below.

## Demos

Five demos ship in `src/`, all on the Vulkan backend. Each is a standalone entry point. They exercise the shader-interface binding model, declarative render graph, and per-draw transient descriptor pool ([`docs/vulkan-friction.md`](docs/vulkan-friction.md) tracks the reshape). The OpenGL backend and its four heavy demos were sunset; Pong was ported to the Vulkan `SpriteBatch` and kept as the gameplay demo.

### Vulkan Lit (active development, headline demo)

```sh
# Run via the launcher (sets DYLD_FALLBACK_LIBRARY_PATH for MoltenVK on macOS).
prefix=$(brew --prefix) && \
  DYLD_FALLBACK_LIBRARY_PATH="$prefix/lib" \
  VK_ICD_FILENAMES="$prefix/etc/vulkan/icd.d/MoltenVK_icd.json" \
  VK_LAYER_PATH="$prefix/share/vulkan/explicit_layer.d" \
  dotnet run --project src/Blix.Demos.VulkanLit/Blix.Demos.VulkanLit.csproj
```

PBR scene end-to-end on the Vulkan backend: cube + skinned glTF + a PBR sphere rig + ground plane, lit by directional sun + two spot lights + one point light, each with PCF-filtered shadow maps. Procedural-sky IBL (env cube + diffuse irradiance + split-sum BRDF LUT), separable-Gaussian bloom chain, ACES tonemap. Free-fly camera, ImGui-driven debug surface (sun yaw/pitch, exposure, per-light toggles, per-pixel shader-channel inspection).

Render graph: `sun-shadow → spot0-shadow → spot1-shadow → 6× point-cube-faces → lit-scene → bloom-bright → bloom-blurH → bloom-blurV → present`. Skinning rides a per-frame bone-palette SSBO via `MaterialBindings(framesInFlight = MaxFramesInFlight)`.

### Vulkan Sponza (perf target)

```sh
tools/run-vulkan-sponza.sh
```

The Khronos Intel Sponza scene (main + curtains + ivy + trees packs) on the Vulkan backend — the renderer's performance and asset-pipeline proving ground. Loads cooked siblings only (`.blixtex` BC7/BC5 textures, `.blixprobe` IBL, `.blixmesh` geometry with LOD): 3-cascade directional shadows with per-cascade resolution + rotated-Vogel PCF, depth pre-pass, R11G11B10F HDR scene target at 4× MSAA, GGX/IBL + Fresnel glass, ACES/AgX tonemap, and an optional froxel volumetric-fog compute pass (`--fog`). Geometry uses screen-space-error LOD over cook-time meshopt chains + spatial split, all bundled into one shared vertex/index buffer by the engine's `MeshBundler` (draws are sub-ranges), with cutout foliage rendered as depth-writing MASK + alpha-to-coverage to avoid overdraw. The Vulkan binding model (descriptor sets + std140 UBO layouts + push ranges) is reflected from the compiled SPIR-V at build time; cooked textures stream in through the engine's `GltfTextureLoader` + `AsyncLoadQueue`; and shader/scene tunables are live-editable in the overlay via `//@tune` / `[Tune]` decorators. The diagnostics overlay also surfaces per-pass draw/triangle counts, the LOD histogram, and a CPU-phase frame breakdown (`cpu-wait` / `cpu-encode` / `cpu-submit`) for separating GPU-bound from draw-encode-bound frames.

Assets are multi-GB and not committed — run `tools/setup-sponza-modern.sh` once to populate + cook from a local Khronos download.

### Vulkan Graph

```sh
dotnet run --project src/Blix.Demos.VulkanGraph/Blix.Demos.VulkanGraph.csproj
```

Smaller demo of the render-graph topology: `cube-offscreen → invert → present`. Validates resource handles, Read edges, `MatchSwapchainGraphSize` resizing, and the graph→imperative-command-list bridge.

### Vulkan Hello

```sh
tools/run-vulkan-hello.sh
```

Original Vulkan validation demo. Spinning depth-tested cube via MoltenVK, swapchain + per-frame sync + VkQueryPool timing infrastructure, debug-line overlay. The narrowest known-good Vulkan call site — useful as the simplest reference when something else breaks.

Setup (one-time): `brew install molten-vk vulkan-loader vulkan-headers vulkan-tools vulkan-validationlayers shaderc`. `BLIX_VK_VALIDATE=1` enables Khronos validation layers. `BLIX_DIAG_INTERVAL=<frames>` controls the periodic console digest cadence (default 60; `BLIX_DIAG=off` disables).

### Pong (gameplay demo)

```sh
tools/run-pong.sh
```

The engine's gameplay demo and the `SpriteBatch` + `Font` proving ground: two-player Pong with 1/120s fixed-step paddle physics, five-zone quantised deflection, ball speed ramp, squash/stretch, hitstop, screen-shake, a fading ball trail, first-to-11 scoring, and a win flash. Renders entirely through the 2D path — solid rects and bitmap text drawn to the swapchain via `SpriteBatch` (one alpha-blended pipeline, texture at set 0, view-projection via push constant) and `DrawText` over a baked `Font` atlas.

- `W` / `S` — left paddle. `↑` / `↓` — right paddle. `Space` — serve / new match. `R` — reset. `Esc` — quit.
- Audio is silent for now (the Silk runtime isn't an `IAudioHost` yet); the GL build's CRT post-FX present (offscreen supersample + chromatic aberration + bloom + scanlines) is a deferred follow-up.

## Cooked asset pipeline

VulkanSponza runs entirely off cooked siblings; `tools/setup-sponza-modern.sh` populates the sources from a local Khronos download and produces the cooked split, and the cook scripts re-cook on demand. Source assets are multi-GB and not committed. Source: <https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/IntelSponza>.

Three sibling binary formats let the runtime skip the slow paths (PNG decode, equirect → IBL convolution, glTF JSON+`.bin` parse + accessor walk). All cookers live in `src/Blix.Tools.Cook` (`blix-cook textures|probe|mesh`).

| Format | Cook input | What it skips at load | Code |
| --- | --- | --- | --- |
| `.blixtex` | PNG / JPEG | StbImage decode + mip generation; supports BC7/BC5 | `src/Blix.Graphics.Images/BlixTex.cs` |
| `.blixprobe` | HDR equirect | Equirect → cube + diffuse irradiance + GGX prefilter + BRDF LUT bake | `src/Blix.Graphics.Images/BlixProbe.cs` |
| `.blixmesh` | `.gltf` / `.glb` | SharpGLTF `.bin` validation + per-accessor walk + vertex packing; also bakes a meshopt LOD chain (+ optional spatial split) per primitive | `src/Blix.Assets/BlixMesh.cs` |

When a `.blixmesh` sibling exists, the importer also switches `ModelRoot.Load` to a lite path (`ReadContext.Create` + `ValidationMode.Skip` + an empty buffer reader) — the JSON still parses for material descriptors, but the multi-megabyte `.bin` validation is skipped entirely. Textures upload progressively through `ResourceUploader`, smallest mip first, so materials bind a usable-if-blurry texture within a frame and sharpen over the next few.

`.blixmesh` also carries geometry LOD: `blix-cook mesh` bakes a meshoptimizer-decimated chain per primitive (each level tagged with its world-space geometric error) and, with `--split N`, recursively splits oversized primitives into spatial chunks so a huge floor/wall/ivy mesh can coarsen its far half independently of its near half. The runtime picks a level by screen-space error. See [`docs/renderer.md` → Geometry LOD](docs/renderer.md#geometry-lod).

## Build

```sh
dotnet build Blix.sln
```

Target framework: net8.0. The 2D physics CLI test harness lives at `src/Blix.Test.Physics2D` — run with `dotnet run --project src/Blix.Test.Physics2D/Blix.Test.Physics2D.csproj`.

## Roadmap

- **Particles** — generic GPU/CPU emitter with sorted billboards, soft-particle depth, HDR + bloom integration. First consumer of the new shader library.
- **More demos** — beyond the current Vulkan set. Likely a focused VFX scene to validate the particle system, then something with gameplay.

The shader library at `src/Blix.Shaders/` (tonemap, noise, PBR primitives) is the substrate both new features build on; the include preprocessor + `ShaderLoader` make new shaders cheap to author.

## Platform notes

- **macOS audio requires OpenAL Soft.** Apple's bundled `OpenAL.framework` has been deprecated since macOS 10.15 and silently no-ops on most source calls (looping, playback transitions). Install via `brew install openal-soft` — `OpenALAudioDevice` probes the standard Homebrew prefixes and points the loader at the working library.
- **Keep shaders ASCII.** Shaders are compiled offline to SPIR-V with `glslc` (Khronos), so the old Apple GL 4.1 compiler quirks no longer bite at runtime. Pure ASCII is still the portability convention for the shader library.
