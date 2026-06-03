# Blix

**Blix is a code-first native engine kit for C# / .NET — for games that want to own their engine stack.**

An in-development engine workbench: rendering, assets, streaming, audio, physics, UI, animation, runtime hosting, and diagnostics, all exposed as explicit pieces game code wires together directly. Blix doesn't try to make the engine disappear — it makes the important machinery visible.

The split is deliberate. The engine owns the reusable hard parts — cooked asset formats, background loading, progressive texture upload, mesh bundling, the typed graphics-command layer, shader interfaces, render graphs, Vulkan execution, and diagnostics. The game owns how those become a frame — which passes run, how draw groups are built, what gets culled, how LOD is chosen, how the world is represented. No single scene renderer or fixed world model is imposed on top, and there's no ECS, editor, scripting, or hot reload.

The stack is Vulkan-first: `Blix.Graphics` is the typed command layer, `Blix.Graphics.Vulkan` the backend, and `Blix.Runtime.Silk` hosts the window, input, Vulkan surface, audio device, and diagnostics. Visibility is part of that surface — systems contribute debug values, controls, timers, events, overlays, stats, selections, inspectors, and live-tunable parameters, so the engine can answer practical questions while it runs: what was loaded, streamed, bundled, submitted, culled, and drawn, and where the frame time went.

Today that spans cooked binary asset formats (`.blixtex`, `.blixprobe`, `.blixmesh`), progressive texture streaming, mesh bundling, screen-space-error LOD, GPU-driven indirect drawing, per-instance instanced rendering, PBR + HDR/IBL, cascaded and cubemap shadows, volumetric fog, bloom, tonemapping, glTF skinning, OpenAL positional audio, kinematic collision, and a Vulkan `SpriteBatch`/font path — driving two complete games (2D Pong and a 3D endless runner) alongside the Sponza capabilities scene.

Blix is still early — APIs are changing, the demos do real engine work, and some systems are exposed before they're polished. The goal is a readable native engine kit: serious enough to push multi-GB scenes through a modern Vulkan frame, small enough to understand, change, and own.

## Documentation

- [`docs/architecture.md`](docs/architecture.md) — orientation: project graph, host contracts, the Vulkan binding model, conventions, where to find things.
- [`docs/renderer.md`](docs/renderer.md) — the Vulkan renderer: render graph, recording draws, pipelines, shaders, and the rendering techniques (PBR, IBL, shadows, bloom, fog).
- [`docs/blix.md`](docs/blix.md) — the layer game code targets: loop, scene primitives, cameras, lights, animation, skeletal, physics, geometry, audio, picking.

## Demos

Nine demos ship in `src/`, all on the Vulkan backend, each a standalone entry point. **Vulkan Sponza** is the capabilities demo — the forward edge of what the engine can pull off. Two are complete, end-to-end playable games: **Pong** (2D, the `SpriteBatch`/font path) and the **Runner** (a 3D endless runner — an instanced world, a skinned animated character, kinematic physics, procedural sky + fog, HUD, and audio). **Vulkan Particles** is the VFX showcase — the CPU-simulated particle system over the engine's fullscreen/post-process primitives. The other five are focused references: **Vulkan Instanced** (the per-instance instancing foundation the Runner builds on), **Vehicle Parenting** (the `Transform3D` hierarchy: a drivable hull → turret → barrel), **Vulkan Lit** (the lit/shadow/PBR/IBL/bloom path), **Vulkan Graph** (render-graph topology), and **Vulkan Hello** (the Vulkan bring-up path). (The OpenGL backend and its four heavy demos were sunset; Pong was rebuilt on the Vulkan `SpriteBatch`.)

### Vulkan Sponza — capabilities demo

![Intel Sponza rendered in Blix: PBR stone and draped cloth, cascaded sun shadows, alpha-cutout foliage, and image-based lighting — all from cooked, streamed, screen-space-error-LOD'd assets.](docs/sponza.jpg)

```sh
tools/run-vulkan-sponza.sh
```

The forward edge of the engine, and the demo where the whole pipeline has to come together to keep a multi-GB scene playable: the Khronos Intel Sponza scene (main + curtains + ivy + trees packs). It loads cooked siblings only (`.blixtex` BC7/BC5 textures, `.blixprobe` IBL, `.blixmesh` geometry with LOD): 3-cascade directional shadows with per-cascade resolution + rotated-Vogel PCF, depth pre-pass, R11G11B10F HDR scene target at 4× MSAA, GGX/IBL + Fresnel glass, ACES/AgX tonemap, and an optional froxel volumetric-fog compute pass (`--fog`). Geometry uses screen-space-error LOD over cook-time meshopt chains + spatial split, all bundled into one shared vertex/index buffer by the engine's `MeshBundler` (draws are sub-ranges), with cutout foliage rendered as depth-writing MASK + alpha-to-coverage to avoid overdraw. The Vulkan binding model (descriptor sets + std140 UBO layouts + push ranges) is reflected from the compiled SPIR-V at build time; cooked textures stream in through the engine's `GltfTextureLoader` + `AsyncLoadQueue`; and shader/scene tunables are live-editable in the overlay via `//@tune` / `[Tune]` decorators. The diagnostics overlay surfaces per-pass draw/triangle counts, the LOD histogram, and a CPU-phase frame breakdown (`cpu-wait` / `cpu-encode` / `cpu-submit`) for separating GPU-bound from draw-encode-bound frames — the levers you actually pull to make the scene fast.

Assets are multi-GB and not committed — run `tools/setup-sponza-modern.sh` once to populate + cook from a local Khronos download.

### Pong — complete game

```sh
tools/run-pong.sh
```

The one end-to-end playable game, and the `SpriteBatch` + `Font` proving ground: two-player Pong with 1/120s fixed-step paddle physics, five-zone quantised deflection, ball speed ramp, squash/stretch, hitstop, screen-shake, a fading ball trail, first-to-11 scoring, and a win flash. Solid rects and bitmap text are drawn via `SpriteBatch` (one alpha-blended pipeline, texture at set 0, view-projection via push constant) and `DrawText` over a baked `Font` atlas into a 2×-supersampled offscreen (a `RenderGraph` pass), then a fullscreen CRT post-FX present grades it onto the swapchain — bezel vignette, luminance-gated chromatic aberration, dual-radius bloom, scanlines, and a win-flash tint. Square-wave SFX play through the OpenAL device the Silk runtime provides.

- `W` / `S` — left paddle. `↑` / `↓` — right paddle. `Space` — serve / new match. `R` — reset. `Esc` — quit.
- macOS audio needs OpenAL Soft (`brew install openal-soft`); without it the beeps no-op but the game runs.

### Runner — 3D game (instancing + skeletal animation)

```sh
tools/run-runner.sh
```

The second complete game, and the engine's instancing + skeletal-animation showcase: a 3D endless runner. The whole scrolling world — recycling ground tiles plus barrel obstacles and spinning coins — is drawn through per-mesh instanced batches: `InstanceBuffer` (a frames-in-flight-replicated set-3 storage buffer of per-instance transform + tint) and `InstancedBatch` (the staging/draw ergonomics), each mesh issued as one `vkCmdDrawIndexed(instanceCount=N)` reading `gl_InstanceIndex`. The player is a skinned, animated glTF character (the CC0 KayKit Rogue) drawn through the engine's bone-palette skinning path — a `Running_A` clip whose cadence scales with the run speed, switching to a jump clip mid-air. Three lanes with frame-rate-independent lane-lerp, a `PhysicsHost3D` gravity jump, `CollisionWorld3D.Overlap` for coin pickups and obstacle hits, distance + coin scoring with a speed ramp and game-over/restart. A fullscreen analytic sky + exponential distance fog set the scene; a `SpriteBatch` + `Font` HUD shows the score; synthesized SFX play through the OpenAL device. Implements `IDebuggable`, so the diagnostics overlay draws the live collision-sphere gizmos.

- `←` / `A`, `→` / `D` — switch lanes. `Space` / `W` / `↑` — jump. `Enter` / `R` — restart after game-over. `Esc` — quit. `` ` `` — toggle the diagnostics overlay.
- Character + props are CC0 KayKit assets (Kay Lousberg, kaylousberg.com); see `src/Blix.Demos.Runner/Assets/models/CREDITS.txt`.

### Vulkan Particles — VFX + post-process showcase

```sh
tools/run-particles.sh           # interactive; --frames N auto-exits for a headless validation run
```

The particle and post-process showcase. `ParticleBatch` (`Blix.Render`) is a CPU-simulated billboard system — colour- and size-over-life ramps, velocity drag — that expands its live set into one per-frame slice of the transient vertex arena. It stays a pure geometry primitive: the caller brings the pipeline, push constants, and texture bindings. The demo drives three effects (a spark fountain with drifting smoke, a periodic explosion burst, and a swirling vortex) over two blend-variant pipelines — a shared additive pipeline for sparks/explosion/vortex, premultiplied-alpha for the depth-sorted smoke — with **soft-particle depth fade** (billboards dissolve into geometry instead of clipping through it).

It's also the proving ground for the fullscreen/post-process primitives. Every present/post/sky/CRT pass in the engine draws through **`FullscreenPass`** (the dummy-VB + `gl_VertexIndex` fullscreen-triangle draw — caller brings the pipeline/textures/push). Bloom runs as a **`PostChain`**: a linear chain of fullscreen image→image passes (`bright → blurH → blurV`) over auto-managed intermediate targets that declares its own targets+passes and yields an output texture, while the demo's present pass keeps composite + tonemap. The full `RenderGraph` is `depth pre-pass → scene → bloom chain → ACES tonemap present`. The bloom/fullscreen kernels live in the shared `Blix.Shaders` library (`fullscreen.glsl`, `bloom.glsl`, `tonemap.glsl`), consumed via a wired `glslc -I` include path. Interactive orbit camera + a live `--debug` tuning overlay.

### Vulkan Instanced — instancing foundation

```sh
tools/run-instanced.sh           # interactive; --frames N auto-exits for a headless validation run
```

The proof gate for per-instance instanced rendering: 5,000 cubes drawn in a single `vkCmdDrawIndexed(instanceCount=5000)`, each pulling its own transform + tint from a set-3 storage buffer indexed by `gl_InstanceIndex`. Isolates the two engine layers the Runner builds its world on — `InstanceBuffer` (the replicated SSBO data layer) and `InstancedBatch` (the staging + draw ergonomics) — with no built-in shader, so the demo supplies its own. The headless `--frames N` mode is the validation harness (run under `BLIX_VK_VALIDATE=1`).

### Vehicle Parenting — Transform hierarchy reference

```sh
tools/run-vehicle.sh             # interactive; --frames N auto-exits for a headless validation run
```

The `Transform3D` parenting reference: a drivable tank whose **hull → turret → barrel** form a transform hierarchy. The hull is the root (driven by input); the turret is its child (yaws independently); the barrel is the turret's child (pitches). Each part's `WorldMatrix` composes the chain, so turning the hull carries the turret + barrel while they keep their own local aim. Firing is the centrepiece of the parenting model: a shell is spawned **as a child of the barrel** at the muzzle, then `SetParent(null, keepWorldPose: true)` detaches it into world space with its world pose preserved — so it leaves the muzzle exactly where the moving, aimed barrel points and flies a free gravity arc (`PhysicsHost3D`). Everything draws through one `InstancedBatch` (each part/target/shell is a unit cube whose model is `scale(visual) * WorldMatrix`). Drive `W`/`S`/`A`/`D`, aim with the arrow keys, `Space` to fire.

### Vulkan Lit — PBR renderer reference

```sh
# Run via the launcher (sets DYLD_FALLBACK_LIBRARY_PATH for MoltenVK on macOS).
prefix=$(brew --prefix) && \
  DYLD_FALLBACK_LIBRARY_PATH="$prefix/lib" \
  VK_ICD_FILENAMES="$prefix/etc/vulkan/icd.d/MoltenVK_icd.json" \
  VK_LAYER_PATH="$prefix/share/vulkan/explicit_layer.d" \
  dotnet run --project src/Blix.Demos.VulkanLit/Blix.Demos.VulkanLit.csproj
```

The full lit/shadow/PBR/IBL/bloom path in isolation: cube + skinned glTF + a PBR sphere rig + ground plane, lit by directional sun + two spot lights + one point light, each with PCF-filtered shadow maps. Procedural-sky IBL (env cube + diffuse irradiance + split-sum BRDF LUT), separable-Gaussian bloom chain, ACES tonemap. Free-fly camera, ImGui-driven debug surface (sun yaw/pitch, exposure, per-light toggles, per-pixel shader-channel inspection).

Render graph: `sun-shadow → spot0-shadow → spot1-shadow → 6× point-cube-faces → lit-scene → bloom-bright → bloom-blurH → bloom-blurV → present`. Skinning rides a per-frame bone-palette SSBO via `MaterialBindings(framesInFlight = MaxFramesInFlight)`.

### Vulkan Graph — render-graph reference

```sh
dotnet run --project src/Blix.Demos.VulkanGraph/Blix.Demos.VulkanGraph.csproj
```

Smaller demo of the render-graph topology: `cube-offscreen → invert → present`. Validates resource handles, Read edges, `MatchSwapchainGraphSize` resizing, and the graph→imperative-command-list bridge.

### Vulkan Hello — bring-up reference

```sh
tools/run-vulkan-hello.sh
```

The narrowest known-good Vulkan call site: a spinning depth-tested cube via MoltenVK, swapchain + per-frame sync + VkQueryPool timing infrastructure, debug-line overlay. The simplest reference when something further up the stack breaks.

Setup (one-time): `brew install molten-vk vulkan-loader vulkan-headers vulkan-tools vulkan-validationlayers shaderc`. `BLIX_VK_VALIDATE=1` enables Khronos validation layers. `BLIX_DIAG_INTERVAL=<frames>` controls the periodic console digest cadence (default 60; `BLIX_DIAG=off` disables).

## Cooked asset pipeline

VulkanSponza runs entirely off cooked siblings; `tools/setup-sponza-modern.sh` populates the sources from a local Khronos download and produces the cooked split, and the cook scripts re-cook on demand. Source assets are multi-GB and not committed. Source: <https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/IntelSponza>.

Three sibling binary formats let the runtime skip the slow paths (PNG decode, equirect → IBL convolution, glTF JSON+`.bin` parse + accessor walk). All cookers live in `src/Blix.Tools.Cook` (`blix-cook textures|probe|mesh`).

| Format | Cook input | What it skips at load | Code |
| --- | --- | --- | --- |
| `.blixtex` | PNG / JPEG | StbImage decode + mip generation; supports BC7/BC5 | `src/Blix.Graphics.Images/BlixTex.cs` |
| `.blixprobe` | HDR equirect | Equirect → cube + diffuse irradiance + GGX prefilter + BRDF LUT bake | `src/Blix.Graphics.Images/BlixProbe.cs` |
| `.blixmesh` | `.gltf` / `.glb` | SharpGLTF `.bin` validation + per-accessor walk + vertex packing; also bakes a meshopt LOD chain (+ optional spatial split) per primitive | `src/Blix.Assets/BlixMesh.cs` |

When a `.blixmesh` sibling exists, the importer also switches `ModelRoot.Load` to a lite path (`ReadContext.Create` + `ValidationMode.Skip` + an empty buffer reader) — the JSON still parses for material descriptors, but the multi-megabyte `.bin` validation is skipped entirely. Textures upload progressively through `ResourceUploader`, smallest mip first, so materials bind a usable-if-blurry texture within a frame and sharpen over the next few.

`.blixmesh` also carries geometry LOD: `blix-cook mesh` bakes a meshoptimizer-decimated chain per primitive (each level tagged with its world-space geometric error) and, with `--split N`, recursively splits oversized primitives into spatial chunks so a huge floor/wall/ivy mesh can coarsen its far half independently of its near half. The runtime picks a level by screen-space error; `src/Blix.Demos.VulkanSponza/` is the working reference.

## Build

```sh
dotnet build Blix.sln
```

Target framework: net8.0. The 2D physics CLI test harness lives at `src/Blix.Test.Physics2D` — run with `dotnet run --project src/Blix.Test.Physics2D/Blix.Test.Physics2D.csproj`.

## Platform notes

- **macOS audio requires OpenAL Soft.** Apple's bundled `OpenAL.framework` has been deprecated since macOS 10.15 and silently no-ops on most source calls (looping, playback transitions). Install via `brew install openal-soft` — `OpenALAudioDevice` probes the standard Homebrew prefixes and points the loader at the working library.
- **Keep shaders ASCII.** Shaders are compiled offline to SPIR-V with `glslc` (Khronos), so the old Apple GL 4.1 compiler quirks no longer bite at runtime. Pure ASCII is still the portability convention for the shader library.
