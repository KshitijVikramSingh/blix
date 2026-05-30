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

Seven demos ship in `src/`. Each is a standalone entry point. The Vulkan demos are the active development target — they exercise the new shader-interface binding model, declarative render graph, and per-draw transient descriptor pool ([`docs/vulkan-friction.md`](docs/vulkan-friction.md) tracks the in-flight reshape). The OpenGL demos still run unchanged.

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

The Khronos Intel Sponza scene (main + curtains + ivy + trees packs) on the Vulkan backend — the renderer's performance and asset-pipeline proving ground. Loads cooked siblings only (`.blixtex` BC7/BC5 textures, `.blixprobe` IBL, `.blixmesh` geometry with LOD): 3-cascade directional shadows with per-cascade resolution + rotated-Vogel PCF, depth pre-pass, R11G11B10F HDR scene target at 4× MSAA, GGX/IBL + Fresnel glass, ACES/AgX tonemap, and an optional froxel volumetric-fog compute pass (`--fog`). Geometry uses screen-space-error LOD over cook-time meshopt chains + spatial split, all consolidated into one shared vertex/index buffer (draws are sub-ranges), with cutout foliage rendered as depth-writing MASK + alpha-to-coverage to avoid overdraw. The diagnostics overlay surfaces per-pass draw/triangle counts, the LOD histogram, and a CPU-phase frame breakdown (`cpu-wait` / `cpu-encode` / `cpu-submit`) for separating GPU-bound from draw-encode-bound frames.

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
- **More demos** — beyond Sponza + ShaderLab. Likely a focused VFX scene to validate the particle system, then something with gameplay.

The shader library at `src/Blix.Shaders/` (tonemap, noise, PBR primitives) is the substrate both new features build on; the include preprocessor + `ShaderLoader` make new shaders cheap to author.

## Platform notes

- **macOS audio requires OpenAL Soft.** Apple's bundled `OpenAL.framework` has been deprecated since macOS 10.15 and silently no-ops on most source calls (looping, playback transitions). Install via `brew install openal-soft` — `OpenALAudioDevice` probes the standard Homebrew prefixes and points the loader at the working library.
- **macOS GLSL is ASCII-only.** Apple's GL 4.1 / GLSL 4.10 compiler reports non-ASCII characters (including in comments) as "premature EOF on last line." Keep shaders pure ASCII.
- **macOS GLSL reserves `noise1..4`.** Defining a local `noise3` triggers a "return type differs" error. Prefix with `v` (value-noise) or use longer names.
