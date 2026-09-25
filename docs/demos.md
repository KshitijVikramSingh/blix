# Applications and proving grounds

Blix applications are explicit C# roots over shared libraries. A game, renderer
experiment, viewer, capture tool, benchmark, and headless check are all apps;
their difference is purpose, not a separate plugin or scripting model.

This page is the catalogue and routing guide. [Workflow](workflow.md) explains
app discovery and launch mechanics, [Renderer](renderer.md) owns current render
technique detail, and [Assets](assets.md) owns cooking and loading behavior.

## Find and run an app

Build-generated indexes are the authoritative inventory:

```sh
./blix ls
dotnet build Blix.sln
./blix ls
```

Tools declare stable short names such as `view`, `shot`, and `check`. Most demos
are also discoverable by their assembly name and by an unambiguous suffix:

```sh
./blix view --model path/to/model.glb
./blix shot --rig path/to/character.glb --clip Walking_A --out walk.png
./blix run Blix.Demos.Pong
./blix run Blix.Demos.Runner --frames 120
```

The scripts under `tools/run-*.sh` remain useful compatibility launchers,
especially for macOS apphost publishing and Sponza's external asset setup. New
applications should use `[BlixApp]`, `WindowOptions.FromArgs`, and the `./blix`
front door rather than adding another bespoke launcher.

The `demos` project currently declares no `blix test` gate. Bounded demo runs,
Vulkan validation, deterministic captures, and task-specific scripts are
separate verification surfaces; none should be described as part of the root
fast gate unless `blix.project` actually names it.

## Application families

| Family | Applications | Purpose |
| --- | --- | --- |
| Playable games | Pong, Runner, Tank Arena, Bulwark | End-to-end pressure on game-facing and rendering APIs |
| Focused renderer references | Chassis, Vulkan Hello, Graph, Instanced, Lit, Particles | Small executable specifications of one layer or composition |
| Renderer research | Vulkan Sponza | Heavy-scene experimentation, measurement, and promotion decisions |
| Asset and Studio tools | `inspect`, `check`, `cook`, `view`, `shot` | Report, judge, transform, inspect interactively, and capture deterministically |
| Character instruments | `room`, `room-shot`, `Blix.Labs.Character.Probe` | Isolate authored contact geometry and later character work |

## Playable games

These are applications first and engine probes second. Their rules, world
models, AI, presentation, and tuning stay local unless another credible
consumer establishes a reusable contract.

### Pong

Source: `src/Demos/Blix.Demos.Pong/`

Pong is the Vulkan 2D proving ground: `SpriteBatch`, baked `Font` atlases,
fixed-step gameplay, OpenAL effects, a supersampled offscreen target, and a
fullscreen CRT presentation pass. It owns the rules, playfield, and CRT look.

Controls: `W`/`S` and arrow keys move the paddles; `Space` serves; `R` resets;
`Esc` quits.

### Runner

Source: `src/Demos/Blix.Demos.Runner/`

Runner combines an instanced scrolling world, skinned glTF animation, a
bone-palette binding, `PhysicsHost3D`, overlap queries, sprites, fonts, audio,
and diagnostic collision geometry. Its lanes, spawning, scoring, speed ramp,
and game-over policy remain application-owned.

Controls: left/right or `A`/`D` change lane; `Space`, `W`, or up jumps;
`Enter`/`R` restarts. `--frames N` provides a bounded smoke run.

### Tank Arena

Source: `src/Demos/Blix.Demos.TankArena/`

Tank Arena is the main `Transform3D` parenting example. Hull, turret, and barrel
form a hierarchy; a shell begins at the muzzle and detaches with its world pose
preserved before entering a physics arc. The game also exercises authored-node
glTF fitting, instanced props, collision, steering, HDR rendering, shadows, and
live `[Tune]` state.

Controls: `W`/`S` drive, `A`/`D` steer, arrows aim and elevate, and `Space`
fires. `--debug` enables its tuning surface; `--frames N` bounds the run.

### Bulwark

Source: `src/Demos/Blix.Demos.Bulwark/`

Bulwark pressures pointer-to-world picking, dynamic grid navigation, placement
validation, parented turret aiming, waves, and a skinned animated crowd. It
deliberately keeps A*, tower policy, economy, and crowd composition local. The
generic engine pieces are camera rays, geometry queries, transforms, animation,
binding, and rendering primitives.

Left-click builds or upgrades, right-click sells, `Space` starts a wave, and
the arrow keys orbit the camera. `--selftest` exercises game invariants;
`--frames N` is the headed smoke path.

## Focused renderer references

### Chassis

Source: `src/Demos/Blix.Demos.Chassis/`

The smallest application shape: host-owned arguments, a clear-only frame, UI
without requiring `IDebuggable`, and input capture ownership. It is the starting
point for a new headed app, not a renderer template. The same assembly also
declares `chassis-tune`, which proves reflected `[Tune]` state.

```sh
./blix run Blix.Demos.Chassis --frames 60
./blix chassis-tune --frames 60
```

### Vulkan Hello

Source: `src/Demos/Blix.Demos.VulkanHello/`

The narrowest known-good 3D Vulkan path: one cube, depth, descriptors,
per-frame uniforms, fullscreen presentation, diagnostics, and shared window
arguments. Use it when a failure may be below the render graph or asset layers.

### Vulkan Graph

Source: `src/Demos/Blix.Demos.VulkanGraph/`

The smallest render-graph composition: render a cube offscreen, run an invert
pass through a declared read edge, then bridge the graph output to swapchain
presentation. Start here for graph lifecycle and resource ordering.

### Vulkan Instanced

Source: `src/Demos/Blix.Demos.VulkanInstanced/`

The `InstanceBuffer`/`InstancedBatch` proof: 5,000 cubes in one instanced draw,
with per-instance transform and tint read through a frames-in-flight replicated
storage buffer. A bounded validation run should span more than one frame slot.

### Vulkan Lit

Source: `src/Demos/Blix.Demos.VulkanLit/`

An application-owned forward-lit composition with metallic/roughness PBR,
procedural IBL, directional, spot, and point shadows, skinned meshes, HDR bloom,
and tonemapped presentation. It is a useful complete pipeline example, but its
scene and pass choices are not a global Blix default.

### Vulkan Particles

Source: `src/Demos/Blix.Demos.VulkanParticles/`

The `ParticleBatch`, transient-vertex, soft-depth, and fullscreen post-process
proof. The batch owns particle geometry and simulation transport; this app owns
the shaders, blend modes, fountain/explosion/vortex effects, bloom, camera, and
look. Use `--debug` for live effect tuning and `--frames N` for a bounded run.

## Vulkan Sponza: renderer research

Source: `src/Demos/Blix.Demos.VulkanSponza/`

Vulkan Sponza is the heavy-scene rendering and measurement application, not the
getting-started renderer. It combines cooked multi-pack assets, deferred work,
bundled geometry, indirect submission, SSE LOD, cascade culling, a depth/normal
pre-pass, Hi-Z, GTAO, probe-based indirect light, an incident-light field, TAA,
temporal froxel fog, extended material work, and extensive A/B and capture
instrumentation.

The graph and research ownership are documented in
[Renderer](renderer.md#vulkan-sponza-research-renderer). The detailed command
surface remains application-owned and evolves with the experiments. Common
families include `--ab`, `--viz`, `--tune`, `--shot`, `--probe-reference`, LOD
controls, shadow controls, and feature toggles.

Sponza assets are not committed. Prepare the configured pack location with:

```sh
tools/setup-sponza-modern.sh
tools/run-vulkan-sponza.sh
```

Set `BLIX_SPONZA_ASSETS` when the cooked pack set lives outside the checkout.

## Asset and Studio tools

The current tool family is not a single “toolchain lab.” It is a set of
specialized apps sharing contracts and, where useful, the Studio reference
rendering pipeline:

```text
discover -> inspect -> check -> cook -> view -> shot -> verify
```

| App | Contract |
| --- | --- |
| `inspect` | Report what a source or cooked artifact contains. It describes; it does not certify. |
| `check` | Judge supported asset, rig, clip, cooked-load, and related contracts. Failure is a non-zero exit. |
| `cook` | Discover and run built-in or project-owned recipes and report their status. |
| `view` | Interactively inspect a model or rig under the Studio reference pipeline. |
| `shot` | Produce a deterministic PNG or image sequence from the same Studio composition. |

`Blix.Tools.Studio` owns shared scene-facing model/rig views, `StudioLook`, and
the reference renderer. `Blix.Tools.Studio.Shell` owns reusable ImGui-facing
panels and viewport glue. `view` and `shot` are separate application roots over
those libraries; `inspect`, `check`, and `cook` remain headless and do not need
a graphics device unless their specific work says otherwise.

### Inspect, check, and cook

```sh
./blix inspect path/to/asset.glb
./blix inspect path/to/asset.blixmesh
./blix check --model path/to/model.glb
./blix check --rig path/to/character.glb --verbose
./blix check --cooked path/to/cooked-tree
./blix cook list
./blix cook status path/to/project-or-tree
./blix cook run <recipe-id> <source> [output]
```

Keep the semantic distinction: `inspect` reports and `check` judges. Cooking is
normally build-driven through declarations; the `cook` app is the explicit
discovery, diagnosis, and one-off execution surface.

### View

```sh
./blix view --model path/to/model.glb
./blix view --rig path/to/character.glb --clip Walking_A
./blix view --rig path/to/character.glb --clip Walking_A --blend Running_A
./blix view --rig path/to/character.glb --clip Walking_A --instances 3
```

`--model` preserves the authored node hierarchy so pivots, bounds, transforms,
materials, and selection remain inspectable. `--rig` preserves skeletons and
clips. The rig path supports single, blended, additive, and masked composition,
root-motion inspection, independent multi-instance clocks, skeleton gizmos,
material auditioning, and a second-camera viewport. The caller chooses model or
rig semantics explicitly; the viewer does not guess.

### Shot

```sh
./blix shot --model path/to/model.glb --out model.png
./blix shot --rig path/to/character.glb --clip Walking_A --time 0.35 --xray --out walk.png
./blix shot --rig path/to/character.glb --clip Walking_A --frames-out 24 --out walk.png
./blix shot --rig path/to/character.glb --instances 3 --lockstep --out control.png
```

`shot` captures the HDR scene target and applies the CPU twin of the Studio
tonemap. Fixed clip times and fixed-step sequences make output reproducible.
`--xray` exposes skeletons inside opaque meshes; `--viewport` captures the
second camera; `--advance` plus `--drive-root` visualizes integrated root motion;
multi-instance and lockstep modes provide positive and negative controls.

## Character instruments

The Character project is a separate specialized app family, not a mode hidden
inside the Studio viewer:

- `room` renders authored contact geometry with the same triangles used for
  collision and exposes it for inspection.
- `room-shot` captures that room deterministically.
- `Blix.Labs.Character.Probe` judges the room's authored claims headlessly and
  forms the Character project's declared `blix test` gate.

Run from `src/Character` to use that project scope:

```sh
cd src/Character
../../blix ls
../../blix test
```

The family exists so physics, contact, controller, and camera questions can be
observed without game policy or a complex scene obscuring the answer.

## Verification surfaces

Choose the smallest instrument that can actually falsify the claim:

| Question | Surface |
| --- | --- |
| Does a project satisfy its routine invariants? | `./blix test` in that project scope |
| Does a headed path initialize and render for several frame slots? | `--frames N` where the app supports it |
| Is Vulkan usage validation-clean? | A bounded run with `BLIX_VK_VALIDATE=1` |
| Is a model, rig, or cooked tree structurally acceptable? | `check` |
| Did a visual result change? | `shot`, `room-shot`, or an application-owned capture mode |
| Does Studio output match a stored control set? | `tools/lab-baseline.sh` |
| Does an asset inlet handle the conformance corpus? | `tools/fetch-gltf-corpus.sh` plus the baseline/check workflow |
| Is a Sponza technique worth keeping? | Its in-process A/B, pass timings, censuses, debug views, and captures |

A bounded run proves termination and basic execution. It does not replace a
visual comparison, a domain-specific assertion, or an A/B measurement.

## What belongs where

- Put reusable mechanisms in engine libraries only after their contract is
  clear and another credible consumer exists.
- Keep game rules, scene policy, renderer composition, camera feel, and tuning
  in the application that decides them.
- Prefer another small executable over adding mode switches to one giant root.
- Give substantial subsystems a viewer, probe, capture, benchmark, or other
  instrument appropriate to the questions they raise.
- Keep historical design narratives in plans or dated artifacts; keep this page
  aligned with the commands and boundaries that work now.
