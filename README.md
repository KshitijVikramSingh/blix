# Blix

Blix is a readable native game engine for C# and .NET. It is used as a set of
libraries, not as a framework that takes ownership of an application.

An application owns its world representation, update policy, render-graph
composition, draw grouping, culling, and LOD decisions. Blix supplies the
reusable mechanisms underneath: a typed graphics command model, Vulkan
execution, render graphs, cooked assets, deferred loading and upload,
diagnostics, audio, geometry and collision, animation, runtime hosting, and a
small set of rendering primitives.

The engine is intentionally explicit. There is no required ECS, scene format,
editor, scripting layer, fixed renderer, or engine-owned game loop.

## Start here

Follow [Getting started](docs/getting-started.md) for one linear first path:
install the supported macOS prerequisites, bootstrap and prove the checkout,
run the minimal Chassis application, run Vulkan Lit, and make one visible code
change. The path is designed to take about ten minutes after prerequisite
downloads.

Then see [Workflow](docs/workflow.md) for projects, app declaration and
discovery, bounded runs, verification gates, and the complete launcher model.

A game may keep Blix as a pinned source checkout rather than living in this
repository. The two imports under `build/` expose paths and build mechanisms
without adding engine references or application policy; the external-project
shape is documented in [Workflow](docs/workflow.md#using-blix-from-another-repository).

## The shape of the engine

The stable split is mechanism in the engine, policy at the call site:

| Layer | Owns |
| --- | --- |
| `Blix.Core` | Host, input, UI, view, and app contracts |
| `Blix.Cooked` | Recipe declarations, cooked provenance, load outcomes and refusals |
| `Blix.Graphics` | Backend-neutral handles, resources, layouts, and recorded commands |
| `Blix.Graphics.Vulkan` | Vulkan execution, render graphs, pipelines, binding and residency |
| `Blix.Graphics.Images` | Image decode, environment processing, cooked image formats and CPU tonemap |
| `Blix.Diagnostics` | Values, controls, timing, events, views, selection and history |
| `Blix.Geometry` | Geometry, intersections, sweeps, and collision worlds |
| `Blix.Assets` | Importers, runtime asset data, and cooked readers |
| `Blix.Recipes` | Blix's build-time asset transformations |
| `Blix.Render` | Mesh upload and bundling, deferred upload, instancing, sprites, particles and fullscreen work |
| `Blix` | The game-facing loop, transforms, cameras, animation, physics and glTF composition |
| `Blix.Runtime.Silk` | Window, input, Vulkan surface, audio and diagnostics hosting |

Applications compose these pieces directly. `Blix.Tools.Studio` sits beside,
not inside, the game-facing engine: it is an optional reference rendering
composition for tools, with an authored `StudioLook`. A game can take all, some,
or none of it.

## Projects, apps, and tools

A Blix project is a folder identified by `blix.project`. It can contain many
assemblies and many headed or headless apps. `[BlixApp]` declarations are
indexed during the build, so `./blix ls` discovers them without loading every
assembly. The same project marker may declare the apps run by `./blix test`.

Tools are ordinary apps:

| App | Purpose |
| --- | --- |
| `inspect` | Report what is in a source or cooked asset |
| `check` | Judge the asset contracts Blix can verify |
| `cook` | Run and report declared cooking recipes |
| `view` | View a model or rig using the Studio reference pipeline |
| `shot` | Render a deterministic model or rig capture |

The older `tools/run-*.sh` scripts remain for compatibility and specialized
setup, but they are not the pattern for a new application. Use `./blix` and an
app declaration instead.

## Assets: declared build, observable load

Asset cooking is part of the build rather than a command that must be
remembered. Projects declare mesh, texture, probe, font, or project-owned recipe
inputs in their project files. Shared build targets run only out-of-date recipes
and stage newly produced artifacts in the same build.

Every cooked artifact records its format and recipe versions, source identity,
settings, and provenance. Runtime loaders report whether they used source,
cooked, fallback, or missing data. Heavy CPU work can be produced off-thread and
drained within a frame budget; cooked texture mips upload progressively behind a
stable GPU handle, smallest first.

Blix currently ships `.blixmesh`, `.blixtex`, `.blixprobe`, and `.blixfont`
artifacts. Projects can declare their own recipes through the same mechanism;
the external-consumer contract and its in-tree fixture exercise that boundary.

See [Assets](docs/assets.md) for the full lifecycle: declarations, recipes,
provenance, runtime reports, deferred work, and residency ownership.

## Rendering: machinery, not a renderer

Blix provides rendering *capabilities* and ships no default renderer.
`Blix.Graphics`, `Blix.Graphics.Vulkan`, `Blix.Render`, and `Blix.Shaders` give
the command model, render graph, reflected binding, buffers, upload and batching
helpers, fullscreen work, sprites, particles, and a shared shader vocabulary.
They do not decide which passes an application runs or what it should look like.
An application composes its own graph.

Two things sit on top, and neither is a default:

**Studio** (`Blix.Tools.Studio`) is the render graph Blix's own model and rig
tools use. `StudioLook` owns its lighting, environment, shadows, exposure,
tonemap, and MSAA defaults, outside engine core. It is a reasonable thing to
prototype against, and a project may reuse all, some, or none of it.

**Vulkan Sponza** is a demo. It is where heavy-scene asset flow, GPU-driven
submission, screen-space-error LOD, cascaded shadows, Hi-Z, GTAO, probe-based
indirect light, incident fields, TAA, temporal volumetric fog, material
response, and measurement tooling are implemented and stressed — a worked
reference for how far the machinery goes and how those techniques are built.
It is not a renderer to adopt and its graph is not an engine promise. A
mechanism moves into an engine project only when it has a reusable contract and
another credible consumer.

![Intel Sponza rendered in Blix](docs/sponza.jpg)

Sponza's source assets are not committed. `tools/setup-sponza-modern.sh`
prepares the local asset tree; the application remains runnable through the
Blix front door once built.

## Demos and proving grounds

The repository contains complete games, focused executable specifications,
tools, and isolated laboratories:

- Pong, Runner, Tank Arena, and Bulwark are end-to-end playable games.
- Vulkan Hello, Graph, Lit, Instanced, Particles, and Chassis isolate engine
  surfaces.
- Vulkan Sponza is a heavy-scene rendering demo and measurement scene.
- Character labs isolate contact, controller, camera, rig, and capture work.

Use `./blix ls` for the runnable inventory. See [Demos](docs/demos.md) for the
current application catalogue, ownership boundaries, and verification routing.

## Documentation

- [Getting started](docs/getting-started.md) — the first build, repository gate,
  two representative applications, and one visible edit.
- [Workflow](docs/workflow.md) — projects, apps, discovery, running, shared
  arguments, and verification gates.
- [Assets](docs/assets.md) — declared cooking, recipes, provenance, runtime load
  decisions, deferred work, and GPU residency.
- [Architecture](docs/architecture.md) — engine organization, host contracts,
  views, binding, and code locations.
- [Renderer](docs/renderer.md) — graphics commands, render graph, pipelines,
  shaders, and shared rendering techniques.
- [Game-facing API](docs/blix.md) — loops, transforms, cameras, animation,
  geometry, collision, audio, and picking.
- [Conventions](docs/conventions.md) — the design rules behind engine/caller
  boundaries and executable specifications.
- [Demos](docs/demos.md) — applications, proving grounds, tools, and how to verify them.
- [Plan status](docs/plans.md) — the active design records that still own open work.

These pages describe the current tree. Where prose and executable behavior ever
disagree, the code and tests are the present source of truth; correct the prose
instead of preserving a second historical account beside it.

## Shared application arguments

Applications that use `WindowOptions.FromArgs` inherit:

```text
--frames N       close after N rendered frames
--width N        set the window width
--height N       set the window height
--title TEXT     set the window title
--debug          start with diagnostics visible
--dump-frame N   write the diagnostics dump for frame N
```

Application-specific arguments remain owned by the application.

## Platform notes

- The root `./blix` script establishes the macOS Vulkan loader environment and
  execs an apphost. This avoids Homebrew's `dotnet` shell shim losing `DYLD_*`
  variables across macOS SIP boundaries.
- `BLIX_VK_VALIDATE=1` enables Vulkan validation layers.
- `BLIX_DIAG_INTERVAL=<frames>` controls the periodic console diagnostics
  cadence; `BLIX_DIAG=off` disables it.
- Keep shared GLSL ASCII. Shaders compile offline to SPIR-V through `glslc`.
- OpenAL Soft is required for working audio on current macOS; Apple's legacy
  OpenAL framework is deprecated and frequently silent.
- Windows show the Blix mark as their icon unless an application sets
  `WindowOptions.Icons`. macOS windows carry no icon at all, so there the mark
  goes on the dock tile instead — the only icon a macOS application has, and
  otherwise the generic one for a terminal-launched apphost.

## License

Blix is licensed under the [Apache License 2.0](LICENSE). Third-party components
remain under the terms listed in [Third-party notices](THIRD_PARTY_NOTICES.md).

Blix is under active development. APIs and compositions are still moving, but
the intended character is stable: serious native machinery that remains small
enough to inspect, understand, and change.
