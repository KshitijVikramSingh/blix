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
exactly what is outlined. With `--rig` the same ray picks a **joint**, against the cross the
overlay draws rather than against the skinned triangles — picking the mesh would be more
"accurate" and would select bones the picture cannot explain, an elbow through a sleeve.
Hidden bones are unpickable, which is the other half of the same rule. The panel shows the
selected node's parent, composed pivot and decomposed local TRS — or, for a rig, the bone's
local TRS, its world position, and how far it has moved off rest in metres and degrees.

Materials sample each glTF's base-colour texture, deduped per image, with a white 1×1
stand-in where a material has none — glTF defines the factor as multiplying the texture, so
white is the identity. Worth knowing when reading a render: `tank.glb` has **no** textures
(material factors only), `Rogue.glb` has one palette image across twelve parts. The load
line reports both counts.

#### `--rig` — the same lab, asking a different question

`--model` keeps an asset's **node hierarchy**; `--rig` keeps its **skeleton and clips**.
Two flags rather than one that guesses, because they are two questions answered by two
importers, and "which importer" is not something a viewer should infer from whether a file
happens to contain a skin.

```sh
tools/run-lab.sh --rig src/Blix.Demos.Runner/Assets/models/Rogue.glb
```

**See a pose.** Bones as lines parent→child, joints as crosses, the selected bone's local
axes, and the rest pose as a grey ghost behind the current one. Without it, *"the character
folded inside out"* has two causes that look identical from the outside — the clip was
already wrong, or the thing that read it was. With bones drawn over the mesh they separate
at a glance: bones in the right places under a mangled mesh is a skinning fault, bones in
the wrong places is a clip fault.

The overlay needed **no new debug primitive** — lines, crosses and polylines were all
already there. What was missing was that nothing had ever asked the drawing layer about a
skeleton.

Two things the first drawn skeleton taught, both of which look like bugs and are not:

- **A palette matrix is not a joint position.** `palette[i] = InverseBindPose[i] × world[i]`
  maps a *rest vertex* to where it ends up; its translation is a displacement, and at rest
  it is exactly zero. A skeleton drawn from palette translations collapses into a knot at
  the origin. The joint is at the hierarchy walk's `world` term on its own —
  `LabRig.ComputeBoneWorlds`.
- **Half a rig is not skinned, and "half" is two different numbers.** The Rogue has 41 bones.
  **20 are weighted** — some vertex names them. **21 must be drawn**, because `root` is
  weighted by nothing and is the parent of everything, and a chain drawn without the joints
  that carry it is a set of floating segments. `LabRig.WeightedBones` is the census and
  `LabRig.DeformHierarchy` is what the overlay filters on; they were one property once, whose
  name asked about weights while its value had been promoted up the ancestry. The remaining 20
  are IK handles and roll controls (`kneeIK.l`, `control-heel-roll.r`, `handIK.l`) parented
  straight to the root, which is why drawing all of them makes a starburst at the feet.

**Play a clip.** Transport (space plays, ←/→ step a thirtieth of a second, a rate slider
that runs backwards, a scrub that pauses), and a filter box because seventy-six clips is
past the point where a list is browsable. Three composition modes, each one an engine
primitive rather than lab-local maths:

| mode | what runs it |
| --- | --- |
| single | `ClipPlayer` — the reset, the wrap, the clock |
| blend A→B | `PoseBlend.Lerp` over two players on two clocks |
| additive B on A | `PoseDelta.LayerOnto` per bone, B read as an offset from rest |

`--clip <name>` starts on a named clip; `--blend <name>` / `--additive <name>` name the
second clip *and* pick the composition with it, because those are one decision rather than
two settings that happen to agree. They also mean a bounded `--frames` run reaches the
blend and additive paths — without a flag the only way in is a combo box, and a path a
headless run cannot reach is a path nothing checks.

```sh
tools/run-lab.sh --rig .../Rogue.glb --clip Walking_A --blend Running_A
```

`PoseBlend` and `PoseDelta` had unit tests and **no callers outside them** before this,
which is its own kind of unverified however many assertions cover the maths.

**Where a clip travels.** `RootMotion` takes the delta the root bone covers over an
interval and hands it back; nothing in the tree did that before (RTSGame's
`StripRootMotion` reverts the root to rest, which throws the travel away — correct for an
RTS, and it meant root-driven locomotion was *absent* rather than unextracted). The lab
draws the integrated delta as a path, because a root-motion bug has a shape: a straight
line at even spacing is right, a line that stutters once per cycle is the loop wrap
handled by subtraction, and a line that drifts sideways is a delta taken in the wrong
frame.

**Driving means the clip stops moving the body and the transform starts.** `RootMotion.Strip`
reverts the root to rest; without it a travelling clip moves the mesh *and* the transform, so
the body runs at double speed and snaps back once per cycle. The lab's toggle is the contrast:
off shows a clip as authored (a dodge lurches back at every loop), on shows the same clip
driving a body in a straight line. That is also why `Strip` is an engine call and not a lab
helper — taking a delta and leaving it in the pose is the mistake, and the two halves belong
next to each other.

Worth knowing before reaching for it: of the Rogue's 76 clips, **four travel** — the
dodges. Walk and run are authored in place, and the game owns locomotion.

#### Many bodies, one rig

`--instances N` (viewer and capture) draws N copies of the rig, each on its own clock.

```sh
tools/run-lab.sh --rig .../Rogue.glb --clip Walking_A --instances 3
tools/run-lab-capture.sh --rig .../Rogue.glb --instances 3 --xray --out varied.png
tools/run-lab-capture.sh --rig .../Rogue.glb --instances 3 --lockstep --xray --out control.png
```

**The gap this closes.** A bone palette is a descriptor set, and a descriptor set's *buffer* is
not copied at record time the way a push payload is — so two draws in one frame sharing one
palette binding both read whatever it held at Execute: the second pose, twice. Fine for a lab
with one subject, and the first thing a game breaks.

The fix is not more bindings but a wider one: **every instance's matrices in a single buffer at
a known stride, indexed by `gl_InstanceIndex`**. Bulwark and RTSGame had both already reached
that shape independently — Bulwark packs `i * EnemyBones * 16` floats against a
`#define BONE_COUNT 15` in two shaders (with a throw at load if the asset disagrees, because
nothing checks earlier); RTSGame packs `count * BoneCount` against `gl_InstanceIndex * uSkin.x`.
`Blix.BonePaletteSet` is the stride they were each restating.

It names the stride and **not** where the model matrix lives, because that is the half the two
consumers genuinely disagree about: Bulwark bakes it into the palette (world-space, no instance
buffer at all), RTSGame keeps the palette in model space and carries `model` and `tint`
alongside. A set that insisted on either would force one shape onto the other — the mistake
conventions §4 names with the turret rigs. The lab takes Bulwark's, and `Add(skeleton, pose,
post)` is where the choice is made.

The lab's shader passes the stride as **data** (`uMaterial.z`) rather than a `#define`, so one
compiled shader serves a 15-bone robot and a 41-bone rogue and the CPU packing cannot disagree
with the GPU reading. The array bound still has to be a literal — the build's SPIR-V target
passes no `-D` — so that number does live in two files, and the probe is what makes it safe: it
reads the reflected block size back and fails non-zero when it stops matching
`LabRig.MaxBones × MaxInstances`. It checks the **caster's** palette too, since both stages
share one material and a shadow reading a different body's pose would leave the lit pass looking
perfect.

**There is no single-body path.** Drawing one rig is an instance count of one through the same
line of code. A simpler path for the common case is how "it works with one and breaks with
three" becomes possible.

**How you know it is not phase-, clip-, frame- or state-locked.** Staggering one clip across three
bodies proves the *phases* are independent and nothing else — a mechanism that forced every body
onto one clip would pass that test by construction, because there is only one clip. So each
instance runs its **own clip, own rate and own clock**, and the check runs in both directions:

| | expectation |
| --- | --- |
| varied (default) | one distinct pose per body |
| `--lockstep` | exactly **one** pose in total, repeated |

The second is the negative control, and it is the half that makes the first mean anything: a set
that quietly wrote every body into slot 0 passes "these differ" whenever anything differs.
`BonePaletteSet.Fingerprint` is what makes both checkable rather than eyeballed — and it hashes
the **pose**, not the drawn slice, because placement is baked into each palette and three bodies
standing apart are never bit-identical whatever their poses are. That distinction is what turned
the control from a formality into a test.

Three places carry it: the viewer's panel prints `N/N distinct poses` beside the lockstep
checkbox, the capture prints a per-instance pose fingerprint, and `Toolchain.Probe --rig` runs
both directions headlessly with an exit code. `Blix.Test.Graphics` AQ.17/AQ.18 add the two
state-lock cases — advancing one body must not disturb another's pose, and two bodies on one clip
at different rates must drift apart.

What this deliberately is not: a crowd. The clips are taken in order from the rig's own list — no
AI, no director, no spawning. Eight slots, because the question is "are these poses independent",
which three bodies answer and three hundred only make slower.

#### The images window

Every distinct base-colour image the asset uploaded, drawn, plus the sun's depth buffer.

The lab has reported texture **counts** since it learned to load a model — *"1 image across 12
parts"* — and a count is the least interesting fact about a texture. Which image, at what size,
and whether it is the one you meant are all answerable by looking, and until the UI layer could
read `cmd.TextureId` there was nowhere to look: `VkImGuiRenderer` bound the font atlas on every
draw, so a panel could show text and nothing else.

`IRenderHost.RegisterUiTexture` turns a `TextureHandle` into the opaque id `ImGui.Image` takes.
The font atlas is registered the same way (id 1) rather than special-cased, so there is one
lookup and no "is this the font?" branch to get wrong. This is **stage A of the view arc** — see
`plan-blix-view.md`; an embedded 3D viewport is a textured quad in a panel, so nothing downstream
was possible until this moved.

The sun's depth buffer reads **red-scale**, which is the format and not a fault: a single-channel
depth image sampled by a colour shader is `(d, 0, 0, 1)`. It answers the coarse question — is the
caster pass drawing anything, and does the sun's frustum cover the subject.

#### The viewport panel — a second camera, in a window

Stages B and C of the view arc. A **second camera** on the same scene, rendered into its own
target at half the swapchain's size by its own graph pass, shown inside an ImGui window, orbited
and picked through.

A second camera and not a mirror: showing the main scene target in a panel would prove a texture
can be drawn (stage A did that) and nothing about views.

Three things it settled:

- **A second camera needs its own shader PROGRAM, not just its own target.** The passes are
  render-pass compatible, so sharing the scene's pipelines is legal — and it rendered both
  pictures through one camera. A program owns one per-frame uniform buffer per frame slot; two
  passes sharing a program share that buffer, and since every host write happens during recording
  while the GPU reads at execution, the last `uViewProjection` written wins for *every* draw in
  the frame. Clean validation, correct draw counts, and two captures that each looked right —
  because each read one target and both held the same camera. What found it was a person saying
  "the two cameras seem in sync". The target's format is still shared with the scene's, which
  means the panel holds untonemapped HDR and anything over 1.0 clips; written down rather than
  fixed.
- **The panel's rect is one frame old.** UI layout runs *after* views are declared and after the
  scene is recorded, so this frame's picture is drawn at last frame's size and a resize shows one
  frame of stale aspect. That is what every immediate-mode editor does; the alternative is
  splitting layout from submission, which moves who owns the frame.
- **The interaction cannot go through `IInputHandler`, and that is correct.** The viewport is an
  ImGui window, so ImGui captures the pointer, `GestureOwnership` gives the press to the UI, and
  the application's handler is never called. The picture is an ImGui *item*, so the widget asks
  about itself during layout. **The engine needed no change** — the capture rule was already
  right, and this is what it leaves room for.

**What gets picked is the image's rect, not the panel's.** The picture is letterboxed inside the
panel to keep its aspect, so the two differ by the letterbox, and a ray cast through the panel
rect is wrong by exactly that — silently, and only on panels whose shape happens not to match.
`ViewPicking.RayThrough` reads the view's own logical rectangle, so no scale factor is written at
the call site at all.

**Gizmos land in the panel by naming its surface** — the runtime groups debug commands by `ViewId`
and submits each group to that view's own target, which is two passes, `debug:main` and
`debug:viewport`. The routing had existed since the view arc began and had never had a second view
to prove it, and when it finally got one, **the camera was wrong**: `VkLineDrawer` has one shader
program and passed each view's `uViewProjection` as an inline uniform, so every view wrote the same
64 bytes and the last one won. The main window's grid and skeletons were drawn through the panel's
camera. The matrix is a **push constant** now — copied per draw, 64 bytes against a 128-byte
minimum — which is what the drawer already did for its *vertex* data and had not done for its
uniform.

`Blix.Test.Graphics` Section **AR** pins what a full-window view could never exercise: an offset
rect, the half-open far edge, two abutting views claiming a shared pixel exactly once, and the
Retina invariant — the same logical pointer gives the same ray when the physical extent doubles.

**The capture tool** renders the lab and writes what it rendered to a PNG. It captures
the **HDR scene target**, not the swapchain — so the lab's render path needed no change,
and the tonemap is applied on the CPU, which means a capture holds real radiance and the
curve is an offline choice rather than baked in.

```sh
tools/run-lab-capture.sh --frames 10 --out shot.png
tools/run-lab-capture.sh --rig .../Rogue.glb --clip Walking_A --time 0.35 --xray --out walk.png
tools/run-lab-capture.sh --rig .../Rogue.glb --clip Dodge_Forward --advance 2.0 --drive-root --out travel.png
tools/run-lab-capture.sh --rig .../Rogue.glb --instances 3 --viewport --xray --out panel.png
tools/run-lab-capture.sh --rig .../Rogue.glb --clip Walking_A --frames-out 24 --out walk.png
```

`--frames-out N` writes **N files**, one per fixed 17 ms step — `walk.000.png`, `walk.001.png`,
zero-padded so they sort in play order. A still frame answers *"is this pose right"*; it cannot
answer *"is this motion right"*, which is a question about how one frame follows another. Nothing
reads a wall clock, so the Nth file of a run is the Nth file of every run with those arguments —
which is what makes a regression diffable rather than arguable. Every instance advances on its own
clock, so a sequence of a three-body scene shows three bodies moving, not one moving and two
frozen.

`--viewport` reads back the **panel camera's** target rather than the main scene's, aims the debug
geometry at it, and **also writes `<name>.scene.png` from the main camera**. Two files from one run
is the instrument this arc turned out to need: two cameras rendering one picture happened twice
here — two passes sharing a program's uniform buffer, then the line drawer doing the same per view
— and both times validation was clean, the draw counts were right, and a capture of either target
alone looked exactly as it should, because each held the same camera and neither could contradict
the other. If the pair shows one camera, something upstream is sharing state between the views. The panel is an ImGui window and this tool draws no UI, so without it
the second camera could only ever be checked by a person looking at a running window — which is
the "verified by eye, once" the capture tool exists to replace.

`--advance <seconds>` runs the clock a fixed number of fixed 17 ms steps — no wall time
anywhere, so the run stays reproducible — and draws the integrated root path with a tick every
sixth sample. It exists for the one acceptance criterion a still frame cannot carry: a
travelling clip's delta must integrate to a **straight line at even spacing** across the loop
seam, which is a shape rather than a number. 17 ms rather than a sixtieth on purpose: the
Rogue's clips are authored at 30 fps, so 1/60 divides most of them exactly and every wrap would
land on a seam — the instrument would draw a clean line whatever the code did. The camera frames
across the travel, never down it, because a path seen end-on is a point.

A clip and a time make a capture **reproducible**: "Walking_A at 0.35 s looked like this"
can be re-rendered anywhere and diffed, where "the walk looked wrong" cannot. Nothing in
the rig path reads the clock, so two runs of the same arguments produce the same bytes.

`--xray` turns off depth testing for gizmos. Depth-tested is the right default — it is what
makes a line's place in the scene readable — but a skeleton lives *inside* an opaque mesh,
and the first rig capture drew only the root's IK children fanning out at the feet. The
bones were right and the picture was lying.

**Which asset tool to reach for.** Two things report on assets and they answer different
questions, deliberately kept apart:

| | answers | exit code |
| --- | --- | --- |
| `blix-cook inspect <asset>` | *what is in this file* — node hierarchy, composed pivots, assembled bounds | always 0; it reports |
| `Toolchain.Probe --model <asset>` | *is this asset sound* — clip lengths, skeleton, mesh-node transform | non-zero when not; it judges |
| `Toolchain.Probe --rig <asset>` | *is this rig sound* — hierarchy, rest palette, deform census, finiteness across every clip, root-motion loop continuity | non-zero when not; it judges |

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

`--rig <asset>` checks a rig, still with no device in sight. Each of these reaches the
screen as the same thing — a character folded inside out — and on screen they are
indistinguishable:

- hierarchy order and root count;
- **the rest pose must build the identity palette** (`BindWorld × InverseBindPose = I` by
  construction), which is exact, needs no clip, and catches a bad export on its own;
- the deform/control census, and whether the bone count fits the shader's palette;
- **finiteness sampled across every clip**, not only at its ends — a NaN at t=0.7 is a body
  folding inside out three-quarters through a swing while both endpoints read perfectly
  finite;
- **root-motion continuity at the loop seam**, twice: analytically (travel across the seam
  against travel across an equal interior span) and by integration (running the real
  `ClipPlayer` at a step chosen *not* to divide the duration, over three cycles, and
  checking the sum).

It also stopped calling a zero-duration clip a fault. The Rogue ships seven of them —
`T-Pose`, `Lie_Pose`, four Sit/Unarmed poses — and reporting seven problems on a sound file
is worse than reporting nothing, because it trains a reader to ignore the output.

Everything here is built on the engine's shared GLSL library — `blix_cookTorranceBrdf`,
`blix_sun_shadow`, `blix_tonemap` — with no local copy of a BRDF or a shadow lookup.
Deliberately absent: cascades, texel snapping, bloom, IBL, MSAA, a depth pre-pass. Those
are earned in TankArena and VulkanSponza; a lab that grew them by default would be
claiming to be a renderer.

**How the viewer is put together.** `Program` builds a `ViewerLoop` root, which explicitly
constructs and calls `LabCamera` (twice — the window's view and the panel's), `RigSession`,
`LabSelection` and `ViewerPanels`. It reached 1,645 lines as one type first, and the reason to split
it was not length: *what is shown* and *what is true* had become indistinguishable. See
[`architecture.md` §"How an application is put together"](architecture.md) for the two bars — a
second consumer to reach the library, readability alone to split an executable.

Proves: several executables over one lab · reflected binding off the GPU · the shared
build targets (`BlixShaderMode=Library` + `BlixShaderReflect`) · the chassis · glTF import,
node hierarchy and pivots · picking through a named view · depth-tested gizmos · capture ·
skeletal skinning through a reflected set-3 palette · `ClipPlayer`, `PoseBlend`,
`PoseDelta`, `RootMotion`. Owns: camera feel, the panel's controls, what the scene
contains, which clip feeds which player.

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
