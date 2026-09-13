# Demos

Full writeups for the demos that ship in `src/`. Each is a standalone entry point on
the Vulkan backend, and each is an executable spec for an engine subsystem (see
[`conventions.md` §4](conventions.md)). The capabilities demo, **Vulkan Sponza**, is
written up in the [README](../README.md#vulkan-sponza--capabilities-demo); everything
else lives here.

Four are complete, end-to-end playable games; one is a VFX showcase; five are focused
references. (The OpenGL backend and its four heavy demos were sunset; Pong was rebuilt
on the Vulkan `SpriteBatch`.)

## Games

### Pong — 2D game (SpriteBatch / Font)

```sh
tools/run-pong.sh
```

The `SpriteBatch` + `Font` proving ground: two-player Pong with 1/120s fixed-step
paddle physics, five-zone quantised deflection, ball speed ramp, squash/stretch,
hitstop, screen-shake, a fading ball trail, first-to-11 scoring, and a win flash. Solid
rects and bitmap text are drawn via `SpriteBatch` (one alpha-blended pipeline, texture
at set 0, view-projection via push constant) and `DrawText` over a baked `Font` atlas
into a 2×-supersampled offscreen (a `RenderGraph` pass), then a fullscreen CRT post-FX
present grades it onto the swapchain — bezel vignette, luminance-gated chromatic
aberration, dual-radius bloom, scanlines, and a win-flash tint. Square-wave SFX play
through the OpenAL device the Silk runtime provides.

- `W` / `S` — left paddle. `↑` / `↓` — right paddle. `Space` — serve / new match. `R` — reset. `Esc` — quit.
- macOS audio needs OpenAL Soft (`brew install openal-soft`); without it the beeps no-op but the game runs.

### Runner — 3D game (instancing + skeletal animation)

```sh
tools/run-runner.sh
```

The engine's instancing + skeletal-animation showcase: a 3D endless runner. The whole
scrolling world — recycling ground tiles plus barrel obstacles and spinning coins — is
drawn through per-mesh instanced batches: `InstanceBuffer` (a frames-in-flight-replicated
set-3 storage buffer of per-instance transform + tint) and `InstancedBatch` (the
staging/draw ergonomics), each mesh issued as one `vkCmdDrawIndexed(instanceCount=N)`
reading `gl_InstanceIndex`. The player is a skinned, animated glTF character (the CC0
KayKit Rogue) drawn through the engine's bone-palette skinning path — a `Running_A` clip
whose cadence scales with the run speed, switching to a jump clip mid-air. Three lanes
with frame-rate-independent lane-lerp, a `PhysicsHost3D` gravity jump,
`CollisionWorld3D.Overlap` for coin pickups and obstacle hits, distance + coin scoring
with a speed ramp and game-over/restart. A fullscreen analytic sky + exponential distance
fog set the scene; a `SpriteBatch` + `Font` HUD shows the score; synthesized SFX play
through the OpenAL device. Implements `IDebuggable`, so the diagnostics overlay draws the
live collision-sphere gizmos.

- `←` / `A`, `→` / `D` — switch lanes. `Space` / `W` / `↑` — jump. `Enter` / `R` — restart after game-over. `Esc` — quit. `` ` `` — toggle the diagnostics overlay.
- Character + props are CC0 KayKit assets (Kay Lousberg, kaylousberg.com); see `src/Blix.Demos.Runner/Assets/models/CREDITS.txt`.

### Tank Arena — 3D game (Transform parenting)

```sh
tools/run-tank.sh                # interactive; --frames N auto-exits for a headless validation run
```

A survival shooter built on `Transform3D` parenting, and the engine's broadest
game-layer + asset workout. Each tank is a **hull → turret → barrel** hierarchy: the
hull drives, the turret is its child (yaws to aim independently), the barrel the
turret's child — each part's `WorldMatrix` composes the chain, so driving carries the
turret along while it tracks its own target. Firing is the parenting model's centrepiece:
a shell spawns **as a child of the barrel** at the muzzle, then
`SetParent(null, keepWorldPose: true)` detaches it into world space with its pose
preserved — leaving exactly where the moving, aimed barrel points and flying a
`PhysicsHost3D` gravity arc.

The tank is a real articulated glTF model (Quaternius, CC0) mapped onto the rig per-part
via `GltfStaticImporter.ImportNodes` and the measured-pivot fit (see *Fitting an
articulated model onto a rig* in [`blix.md`](blix.md)); barrels and crates are static
props (material-coloured, instanced) that act as **cover** — they block driving and flat
shots in the collision world, but a high lob clears them, and **exploding barrels**
detonate with AoE damage + chain reactions. Enemies steer around cover toward the player
(obstacle-avoidance) while their turrets stay locked on, hold fire through cover, and
spawn at a fair distance band. The whole scene renders through a `RenderGraph` sun-shadow
+ HDR pipeline, with combat *juice* — screen shake, muzzle flash, gun recoil, and
impact/death/blast debris bursts. Drive `W`/`S`/`A`/`D`, aim the turret with `←`/`→`,
elevate the gun with `↑`/`↓` (pitch sets the range), `Space` to fire, `Enter` to restart.
Run with `--debug` then press `` ` `` for live `[Tune]` overlays grouped into feel
(drive/turn/aim/camera/ballistics/enemies), model-fit, and juice.

### Bulwark — 3D game (tower defense)

```sh
tools/run-bulwark.sh             # interactive; --frames N auto-exits for a headless validation run
```

Blix game #2 — a tower defense, built to pressure the engine where the others didn't:
**pointer-driven picking**, **navigation**, and a **skinned animated crowd**. Defend a
central core from waves that converge on four fronts. The cursor casts a ray
(`Camera3D.ScreenPointToRay`) onto the ground plane and snaps to a grid cell; left-click
builds a tower (or upgrades an existing one), right-click sells. Each front runs its own
**A\*** path to the core, recomputed on every build, and a placement that would wall off
*any* front is rejected. Towers aim a `Transform3D` turret→barrel rig and fire homing
shots; kills pay scrap, leaks cost lives — clear five escalating waves to win (`Space`
launches a wave, arrows orbit the camera, `Enter` restarts).

The enemies are an **animated robot crowd via skinned-mesh instancing** — the one
genuinely new engine capability this game drove: a single `[N×bones]` world-baked
bone-palette buffer indexed by `gl_InstanceIndex` draws the whole walking,
phase-staggered, shadow-casting crowd in one instanced draw per primitive (each playing a
Death clip on its kill). Props are CC0 (Quaternius Turret Cannon + iPoly3D Crystal; see
`src/Blix.Demos.Bulwark/Assets/models/CREDITS.md`); the scene renders through a
`RenderGraph` sun-shadow + HDR pipeline with a procedural sky, `ParticleBatch`
impact/death bursts, OpenAL SFX, and a `SpriteBatch`/font HUD. Building it also produced
two deliberate *non-extractions* — the turret rig and grid-A\* are 2nd consumers of Tank
Arena patterns but were **not** promoted to the engine ([`conventions.md` §4](conventions.md)),
game #2 earning its keep as an engine-shaping exercise.

## VFX showcase

### Vulkan Particles — VFX + post-process showcase

```sh
tools/run-particles.sh           # interactive; --frames N auto-exits for a headless validation run
```

The particle and post-process showcase. `ParticleBatch` (`Blix.Render`) is a
CPU-simulated billboard system — colour- and size-over-life ramps, velocity drag — that
expands its live set into one per-frame slice of the transient vertex arena. It stays a
pure geometry primitive: the caller brings the pipeline, push constants, and texture
bindings. The demo drives three effects (a spark fountain with drifting smoke, a periodic
explosion burst, and a swirling vortex) over two blend-variant pipelines — a shared
additive pipeline for sparks/explosion/vortex, premultiplied-alpha for the depth-sorted
smoke — with **soft-particle depth fade** (billboards dissolve into geometry instead of
clipping through it).

It's also the proving ground for the fullscreen/post-process primitives. Every
present/post/sky/CRT pass in the engine draws through **`FullscreenPass`** (the dummy-VB +
`gl_VertexIndex` fullscreen-triangle draw — caller brings the pipeline/textures/push).
Bloom runs as a **`PostChain`**: a linear chain of fullscreen image→image passes
(`bright → blurH → blurV`) over auto-managed intermediate targets that declares its own
targets+passes and yields an output texture, while the demo's present pass keeps composite
+ tonemap. The full `RenderGraph` is `depth pre-pass → scene → bloom chain → ACES tonemap
present`. The bloom/fullscreen kernels live in the shared `Blix.Shaders` library
(`fullscreen.glsl`, `bloom.glsl`, `tonemap.glsl`), consumed via a wired `glslc -I` include
path. Interactive orbit camera + a live `--debug` tuning overlay.

## References

### Vulkan Instanced — instancing foundation

```sh
tools/run-instanced.sh           # interactive; --frames N auto-exits for a headless validation run
```

The proof gate for per-instance instanced rendering: 5,000 cubes drawn in a single
`vkCmdDrawIndexed(instanceCount=5000)`, each pulling its own transform + tint from a set-3
storage buffer indexed by `gl_InstanceIndex`. Isolates the two engine layers the Runner
builds its world on — `InstanceBuffer` (the replicated SSBO data layer) and
`InstancedBatch` (the staging + draw ergonomics) — with no built-in shader, so the demo
supplies its own. The headless `--frames N` mode is the validation harness (run under
`BLIX_VK_VALIDATE=1`).

### Vulkan Lit — PBR renderer reference

```sh
# Run via the launcher (sets DYLD_FALLBACK_LIBRARY_PATH for MoltenVK on macOS).
prefix=$(brew --prefix) && \
  DYLD_FALLBACK_LIBRARY_PATH="$prefix/lib" \
  VK_ICD_FILENAMES="$prefix/etc/vulkan/icd.d/MoltenVK_icd.json" \
  VK_LAYER_PATH="$prefix/share/vulkan/explicit_layer.d" \
  dotnet run --project src/Blix.Demos.VulkanLit/Blix.Demos.VulkanLit.csproj
```

The full lit/shadow/PBR/IBL/bloom path in isolation: cube + skinned glTF + a PBR sphere
rig + ground plane, lit by directional sun + two spot lights + one point light, each with
PCF-filtered shadow maps. Procedural-sky IBL (env cube + diffuse irradiance + split-sum
BRDF LUT), separable-Gaussian bloom chain, ACES tonemap. Free-fly camera, ImGui-driven
debug surface (sun yaw/pitch, exposure, per-light toggles, per-pixel shader-channel
inspection).

Render graph: `sun-shadow → spot0-shadow → spot1-shadow → 6× point-cube-faces → lit-scene
→ bloom-bright → bloom-blurH → bloom-blurV → present`. Skinning rides a per-frame
bone-palette SSBO via `MaterialBindings(framesInFlight = MaxFramesInFlight)`.

### Vulkan Graph — render-graph reference

```sh
dotnet run --project src/Blix.Demos.VulkanGraph/Blix.Demos.VulkanGraph.csproj
```

Smaller demo of the render-graph topology: `cube-offscreen → invert → present`. Validates
resource handles, Read edges, `MatchSwapchainGraphSize` resizing, and the
graph→imperative-command-list bridge.

## Labs

A **lab** is not a demo. Demos are executable specs for an engine subsystem; a lab is a
testbed for *shape* — the toolchain, the pipeline layering, and how several executables
sit over one body of work.

### Toolchain lab — `Blix.Labs.Toolchain`

```sh
tools/run-lab.sh                      # the viewer
dotnet run --project src/Blix.Labs.Toolchain.Probe   # the probe; no window, no launcher
```

One library and three executables. The library owns the scene, the renderer and the
shaders; **neither executable declares a shader or contains render code**. The `.spv`
and their reflection sidecars are compiled once and arrive through content propagation —
the mechanism `Blix.Render` already used for `SpriteBatch`, now carrying a whole
pipeline.

**The viewer** renders a lit scene — one sun with a 2048² shadow map, HDR target,
tonemapped present — with the chassis conventions: its own ImGui panel, orbit input with
UI capture, host-owned `--frames`, and a named view carrying a grid, a sun arrow and the
sun's trail.

Pass `--model <path.glb>` and it becomes an **asset viewer**:

```sh
tools/run-lab.sh --model src/Blix.Demos.Runner/Assets/models/Rogue.glb
```

It keeps the **authored node hierarchy** rather than fusing the mesh, because a fused mesh
renders identically and answers none of the questions an asset raises — where a part's
pivot is, what the author thinks forward is, why a turret rotates about a point inside the
hull. Each node's composed world transform is a walk up its parent chain, and that composed
translation *is* the rig pivot a game drives.

`blix-cook inspect` has printed those numbers for a long time, and printing them is where
asset work had to stop. The measured-pivot fit has been done by hand at least three times
in this project — TankArena's tank rig, the CC0 sourcing workflow, the RTS villager — each
time by turning knobs in an overlay until the model sat right. An axis triad at a node's
composed translation is that claim, drawn, and it hides behind the hull like a thing in the
world rather than floating in front of it.

Click a part to select it: `Blix.ViewPicking` turns the pointer into a ray through the
named view, tested against the node bounds the viewer already draws, so what gets picked is
exactly what is outlined. The panel shows the selected node's parent, composed pivot and
decomposed local TRS.

Materials sample each glTF's base-colour texture, deduped per image, with a white 1×1
stand-in where a material has none — glTF defines the factor as multiplying the texture, so
white is the identity. Worth knowing when reading a render: `tank.glb` has **no** textures
(material factors only), `Rogue.glb` has one palette image across twelve parts. The load
line reports both counts.

**The capture tool** renders the lab and writes what it rendered to a PNG. It captures
the **HDR scene target**, not the swapchain — so the lab's render path needed no change,
and the tonemap is applied on the CPU, which means a capture holds real radiance and the
curve is an offline choice rather than baked in.

```sh
tools/run-lab-capture.sh --frames 10 --out shot.png
```

**Which asset tool to reach for.** Two things report on assets and they answer different
questions, deliberately kept apart:

| | answers | exit code |
| --- | --- | --- |
| `blix-cook inspect <asset>` | *what is in this file* — node hierarchy, composed pivots, assembled bounds | always 0; it reports |
| `Toolchain.Probe --model <asset>` | *is this asset sound* — clip lengths, skeleton, mesh-node transform | non-zero when not; it judges |

They were not merged. The overlap is in subject, not in purpose, and the honest fix for
"two tools answer the same question" is to make the questions different rather than fuse
the tools: a check that returns an exit code belongs next to the thing it gates, and a
listing belongs next to the cookers that produce the files.

**The probe** never opens a window, so it needs no launcher and no MoltenVK. It reads
the binding model out of the sidecars and prints it — every set, binding, stage and
std140 offset — then checks the reflected push-constant totals against the renderer's
own constants and exits non-zero when they disagree. That check exists because the lab's
first run died on *"payload length 96 does not match the shader's declared total
push-constant size 64"*: a C# constant disagreeing with the SPIR-V it describes, caught
by the device at draw time, which is late.

Everything here is built on the engine's shared GLSL library — `blix_cookTorranceBrdf`,
`blix_sun_shadow`, `blix_tonemap` — with no local copy of a BRDF or a shadow lookup.
Deliberately absent: cascades, texel snapping, bloom, IBL, MSAA, a depth pre-pass. Those
are earned in TankArena and VulkanSponza; a lab that grew them by default would be
claiming to be a renderer.

Proves: several executables over one lab · reflected binding off the GPU · the shared
build targets (`BlixShaderMode=Library` + `BlixShaderReflect`) · the chassis · glTF import,
node hierarchy and pivots · picking through a named view · depth-tested gizmos · capture.
Owns: camera feel, the panel's controls, what the scene contains.

### Chassis — application-chassis reference

```sh
tools/run-chassis.sh              # runs until closed
tools/run-chassis.sh --frames 60  # bounded run
```

The smallest working Blix executable, and the only one that is deliberately **not** a
diagnostics producer. Every other application here that wants an interface is also an
`IDebuggable`, which is precisely why none of them can answer the two questions this
one exists for.

**Does an application get a UI without producing diagnostics?** Until the chassis work,
no: `gameLoop is IDebuggable` decided whether ImGui was created at all, so "produces
diagnostics" and "may have an interface" were the same question. This loop implements
`IUiSource` and not `IDebuggable`, and counts its own `DrawUi` calls — which the host
only makes inside an ImGui frame it has built — so the answer is a log line rather than
a pair of eyes:

```
chassis: UI drawn on 3709/3709 frame(s) with NO IDebuggable on this loop.
```

**Does UI capture actually suppress game input?** `WantCaptureKeyboard` existed from the
day `VkImGuiRenderer` was written and was never read, so typing into an ImGui field also
drove the game — unnoticed because the diagnostics overlay has almost no text fields.
The loop counts every input event it receives and remembers the count when its text
field takes focus; while focused that number must not move however much is typed, and
the panel says so live.

**Releases follow their press.** An earlier version delivered every release
unconditionally, which fixed one bug and created its mirror — a press the UI owned still
handed the application a release it never had a press for. `GestureOwnership` settles it
where it was always settled in the reasoning: at the press. So typing into the field
moves neither the down count nor the up count, while a drag begun in the world and
finished over a panel still delivers its release.

It also renders — a single clear pass whose colour walks with time, with **no shaders of
its own**. The project file is 25 lines and declares none, against the ~80 a Blix
executable used to need with half of it restating how the engine compiles a shader. That
is the point of the demo as much as the panel is: it is what starting something new
costs now.

Proves: `IUiSource` · UI input capture · host-owned `--frames` · shared shader/build
infrastructure by declaring none of it.
Owns: nothing — it has no gameplay, no assets and no world on purpose.

### Vulkan Hello — bring-up reference

```sh
tools/run-vulkan-hello.sh
```

The narrowest known-good Vulkan call site: a spinning depth-tested cube via MoltenVK,
swapchain + per-frame sync + VkQueryPool timing infrastructure, debug-line overlay. The
simplest reference when something further up the stack breaks.

Setup (one-time): `brew install molten-vk vulkan-loader vulkan-headers vulkan-tools
vulkan-validationlayers shaderc`. `BLIX_VK_VALIDATE=1` enables Khronos validation layers.
`BLIX_DIAG_INTERVAL=<frames>` controls the periodic console digest cadence (default 60;
`BLIX_DIAG=off` disables).
