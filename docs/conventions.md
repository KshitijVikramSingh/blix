# Conventions

The non-negotiable principles Blix is built on, in one place. This is the index;
the detail (and the reasoning) lives in [`architecture.md`](architecture.md) and
[`blix.md`](blix.md), and the behaviour is pinned by `Blix.Test.Graphics`. The
goal here is to make the engine's existing primitives **impossible to
misunderstand** — these calcify *principles*, not API signatures. APIs stay fluid;
these don't.

A convention earns a place here only if breaking it silently corrupts downstream
work (matrix transposes, parent compose order) or quietly grows the engine in a
direction it hasn't earned (a scene graph, an ECS, an asset registry). Everything
else is a local decision, not a convention.

Each section ends with **Enforced by** — the doc that explains it, the test
section that pins it, and the `F-###` finding where one exists. If you change
behaviour here, that test should fail first.

---

## 1. Transform & pose

- `Transform3D` stores **local** TRS. `Position` / `Rotation` / `Scale` are
  relative to the parent (or to world, for a root).
- Parenting is **pose-level, not ownership**. `Transform3D.Parent` composes a
  pose chain; it does **not** create an engine-owned scene graph. There is no
  container that owns children, draw order, or lifetimes — game code keeps its
  own object lists and wires `Transform.Parent` where it wants articulation.
- `WorldMatrix = local * parentWorld` (row-vector compose, matching `Skeleton`'s
  hierarchy walk). `WorldPosition` / `WorldRotation` decompose it for consumers
  outside the hierarchy (a chase camera, a muzzle spawn point).
- Identity rotation looks down **−Z**. `Forward` is `−Z` transformed by
  `Rotation`; `LookAt` solves the rotation that aligns local `−Z` with the
  target. This matches the default-camera convention so a default object and a
  default camera face the same way.
- `SetParent(p, keepWorldPose: true)` recomputes the local TRS so the **world**
  pose survives the re-parent — the detach-and-keep-flying op (a shell leaving a
  moving barrel). Without the flag, the local TRS is reinterpreted under the new
  parent.
- No dirty-flag cache, no `Origin`/`Pivot`, no non-uniform scale on shared
  parents (it shears children). These are deliberate limits, documented inline.
- **A clip sample starts from a base pose, always.** `AnimationClip.Sample`
  writes only the channels a clip has tracks for; without resetting to the rest
  pose first, every untouched bone keeps the *previous* frame's value. That is
  invisible on a full clip and is a character whose legs lag its arms on a
  partial one. `ClipPlayer` owns the reset, the loop wrap and the zero-duration
  guard so it is made once rather than remembered three times.
- **A palette matrix is not a joint position.** `ComputeBonePalette` produces
  `InverseBindPose × world` — a *rest vertex's* displacement, exactly zero at
  rest. Where a joint **is** comes from the hierarchy walk's `world` term alone.
  Drawing a skeleton from palette translations is correct arithmetic answering
  the wrong question, and it looks like a knot at the origin.
- **Root motion is a delta, taken across the loop.** `RootMotion` reports the
  travel between two clip times in the root's parent frame; `AcrossLoop` walks
  to the end of the cycle and on from its start rather than subtracting, which
  is the one place every implementation of this is wrong. Applying it — rotating
  it into world, driving a body with it — stays the caller's decision.
- **Taking a delta obliges you to strip it.** A clip that walks its root already
  moves the mesh. A caller that also drives its object transform by the delta
  applies the travel twice: double speed, and a snap back once per loop.
  `RootMotion.Strip` reverts every parentless bone to rest, and the pairing is
  the whole division — the clip says how far, the game says where.
- **N posed bodies share ONE palette buffer, sliced at `i * BoneCount`.** A
  descriptor set's buffer is not copied at record time, so two draws in a frame
  sharing one palette binding both read the second pose. `BonePaletteSet` owns
  the stride — the sentence a C# packing loop and a GLSL `gl_InstanceIndex *
  stride` both have to mean — and deliberately owns nothing else: whether the
  world placement is baked into the palette or carried in an instance buffer is
  where its consumers genuinely differ.
- **A one-shot clip finishes at the boundary in its direction of travel.**
  `Rate` may be negative, so a non-looping clip played backwards ends at `t = 0`
  as surely as a forward one ends at `Duration`. "Finished" is not "reached the
  chronological end".

**Enforced by:** [`blix.md` §Transform3D](blix.md) (incl. *Deliberate limits*) ·
`Blix.Test.Graphics` Sections **AH** (compose / reparent / cycle), **AJ**
(pose basis / `LookAt` / `WorldRotation`) and **AQ** (rest reset, loop-seam
travel, palette-vs-joint, strip, direction-aware finish, palette stride) ·
`Toolchain.Probe --rig` and its no-arg binding check.

## 2. Matrices & the graphics backend

- CPU matrices are **`System.Numerics.Matrix4x4` in native row-vector form**
  throughout: translation in `M41/M42/M43`, a point transforms as `v_row * M`,
  and `M = A * B * C` applies `A` first. `GraphicsMatrices.CreateModel` is
  `Scale * Rotation * Translation` in this form. Hand-rolled `System.Numerics`
  composition and `GraphicsMatrices` / `Transform3D.ToMatrix` / `WorldMatrix`
  are the **same convention and mix freely**.
- The backend uploads these row-major bytes **untransposed**. GLSL's std140
  reads them column-major (= the transpose = column-vector form), so a shader's
  `M * v_col` computes the same transform the CPU's `v_row * M` does. **There are
  no manual transposes anywhere** in the transform/skeletal math — feed
  `WorldMatrix` (or `InstanceData.Model`) straight to a `model * v` shader.
- **Gotcha:** the hand-built *projection* matrices (`CreatePerspectiveVulkan`,
  the orthographics) are authored directly in shader-space (column form). Don't
  pattern-match off them when reasoning about *model* matrices.
- **Vulkan is the sole backend.** There is no cross-backend parity promise; new
  rendering capability is allowed to be Vulkan-shaped. The binding model *can be
  derived* — SPIR-V reflection → `ShaderInterface` (`spirv-cross --reflect` →
  `ShaderReflection.Load`) is the path that removes the hand-maintained table
  that drifts from the shader, and the streaming target (`VulkanSponza`) uses it.
  Small demos still hand-author `ShaderInterface` at load — acceptable at demo
  scale, the same honest line §3 draws for raw-vs-cooked assets. What's
  non-negotiable is that the layout is a `ShaderInterface` value (not scattered
  magic offsets), so reflection can replace authoring without touching consumers.
- World space is right-handed: `+X` right, `+Y` up, camera looks down `−Z`.
  Logical pixels (mouse, `LogicalSize`) and physical pixels
  (`RenderFrameContext.Width/Height`, ~2× on Retina) don't mix.

**Enforced by:** [`architecture.md` §Conventions + §The Vulkan binding model](architecture.md)
· `F-016` · `Blix.Test.Graphics` Sections **A/B/C** (ground truth + upload
symmetry) and **AH.7** (`WorldMatrix` through the GLSL `M*v` path).

## 3. Assets — source in, engine types out

- **The format boundary is the contract.** Third-party formats (glTF via
  SharpGLTF, OBJ, WAV, images via Stb) are translated into **engine types**
  (`MeshData`, `Skeleton`, `AnimationClip`, `Mesh`, decoded pixels) at import.
  Runtime types never carry a SharpGLTF / Stb type — so a future FBX or
  proprietary importer hits the same surface and nothing downstream knows the
  difference.
- **Raw is source; cooked is runtime-optimized.** The cooked formats
  (`.blixmesh` / `.blixtex` / `.blixprobe`) are the runtime path for the heavy
  streaming target — produced by the cook tool, streamed through
  `GltfTextureLoader` + `AsyncLoadQueue` + `MeshBundler`. `VulkanSponza` is the
  reference.
- **Importers normalize at the boundary, explicitly.** Authored offsets are
  baked out at import (`ObjImporter.RecenterToOrigin`), not carried as a runtime
  `Pivot`. `ImportNodes` keeps node hierarchy + *local* transforms (for rigs);
  `Import` world-bakes into one blob. The caller picks which.
- **Honest about the current line:** small demos still import raw glTF/OBJ at
  load (`TankArena` runs `ImportNodes` on a `.glb`) — acceptable at demo scale.
  The cooked pipeline is what the streaming target uses, not a blanket
  requirement. What is non-negotiable is the *boundary* (no third-party type
  leaks past import) and that the runtime can **explain** what it loaded
  (`blix-cook inspect`).

**Enforced by:** [`blix.md` §glTF import + §Project dependencies](blix.md) ·
[`architecture.md`](architecture.md) (cooked-asset pipeline) ·
`Blix.Test.Graphics` Section **AI** (`ImportNodes` preserves names / parent /
local transform) · Section **AJ** (imported nodes compose to the right world
pose).

## 4. Demos are executable specs

- **Each demo proves a subsystem.** Demos are the engine's regression corpus and
  worked references — not throwaway samples. Every major demo carries a header
  block stating **what it proves** (the engine subsystem it exercises) and **what
  it intentionally owns** (gameplay, tuning, scene rules that must stay local).
- **Extraction happens under repeated pressure, not on sight.** Demo-local code
  is allowed to be ugly while it's still experimental. A helper graduates into an
  engine primitive when a *second* consumer wants the same decision — not because
  one demo found it convenient. Duplication across demos is information-gathering;
  premature abstraction is the more expensive mistake here.
  - *Worked example (a `Bulwark` was built partly to generate this signal):* a
    second consumer is necessary but **not sufficient**. `Bulwark` and `TankArena`
    both grew a turret→barrel aim rig and a navigation system — yet **neither was
    extracted.** The turret rig is a few lines composing primitives that already
    exist (`Transform3D` + `LookAt` + `WorldPosition`) — shared incidentally, no new
    capability to abstract. The nav systems are *different algorithms* (grid A* vs
    continuous steering) — unifying them would force one shape onto two. The test
    isn't "is it duplicated?" but "do two consumers want the *same decision*, and
    does naming it add capability?" Often the honest answer is no.
  - *Worked example (build infrastructure obeys the same rule):* thirteen projects
    carried a copy of the SPIR-V compile target, and the raw count argued for one
    shared target. They were not thirteen copies of one thing — eight were identical
    (the app shape), two were a **library** shape building into their own source tree,
    and two were different **algorithms** (reflection sidecars; `#define` variants).
    The shared `BlixCompileSpirV` absorbs eleven; the other two opt out. Folding them
    in would have produced a shader build *system*, which is policy, and policy waits
    for a third consumer. The argument for extracting at all was never tidiness — it
    was that copies **drift**, and one already had: a project missing `@(GlslInclude)`
    from its `Inputs` would not rebuild when a shared `.glsl` changed.
- **Library, not framework.** New rendering capability lands as a *primitive the
  game calls*, not a stage the engine runs for you. There is no `SceneRenderer`
  that owns read→cull→draw; demos compose engine primitives and keep their own
  draw groups, pass routing, and policy.

- **A view is a value, not a registration.** `ViewDeclaration` — a camera, a
  target and two rectangles — is all anything needs to render into a picture or
  turn a pointer into a ray through it; `ViewPicking.RayThrough` never reads the
  id. The one `ViewTable` lives on `DebugState` and exists to **group across
  frames**: which debug commands belong to which picture, which trail remembers
  which points. Build a declaration to point at a picture; intern a name only to
  ask diagnostics to route geometry into it. (Settled by the toolchain lab's
  embedded viewport — two stages of a real consumer, no friction from the
  table's location. `plan-blix-view.md` §D.)

**Enforced by:** [`architecture.md` §"Library, not framework"](architecture.md)
· the *Proves / Owns* header block on each `src/Blix.Demos.*/Program.cs` · the
*Deliberate limits* sections throughout [`blix.md`](blix.md).

---

## 5. Remove assumptions; let policy wait

Blix spent its early life assuming **one view, one frame, one world, one executable,
one host** — a single camera per frame, no memory across frames, no container but the
game's own, an application that finds its own assets and builds its own shaders, and a
host that decided from one type test what an application was allowed to have. Those
were never designed; they were what "one of each" looks like before anything needs two.

The rule that unwound them, and the one to apply next time:

> **Removing a singular assumption is almost never wrong. Adding a policy usually is.**

Removing a constraint changes what is *sayable* and nothing downstream has to agree
with it — named views, trails, a project scope, a host that composes rather than
decides. Adding policy encodes a decision two consumers can disagree about — gizmo
semantics, an IK solver, a navigation abstraction, a container that decides what a
world contains. The first can be done ahead of a consumer; the second cannot, and §4
is the same rule wearing different clothes.

Two corollaries worth keeping:

- **An instrument that filters on the predicate a bug lives in cannot see the bug.**
  A facing test gated on `IsWorking` discarded the exact frame the fault occurred on
  and reported calm for a year.
- **A green build is not evidence that a build step ran.** A shared target that
  silently overrode two projects shipped no shaders under "Build succeeded"; only
  rebuilding the baseline to compare caught it.

**Enforced by:** the absence list below · `Blix.Test.Graphics` Section **AN**
(picking agrees with the camera) · `Blix.Test.Diagnostics` (a primitive with no view
throws; a view keeps its identity across frames).

---

## Where the surface stands

Blix now reaches across the corners it set out to cover — rendering,
assets/streaming, animation, physics, audio, 2D, diagnostics, and the
game-layer — each *proven by a demo or game* rather than declared. That breadth
is the milestone worth naming.

The **application chassis** is the more recent one: an application now gets a window,
its own interface, named views, retained trails, picking into any of them, shared
shader compilation, shared arguments and a bounded run without restating any of it —
and `src/Blix.Demos.Chassis/` is the proof, at 25 lines of project file and no shaders
of its own. The layering that made room for it is unchanged; what moved was the set of
things a single executable was assumed to own. It is **not** a stability promise: the principles
above are frozen, but signatures still move, a primitive may be reshaped, and a
corner may be restructured when a real consumer shows the current shape is
wrong. New capability lands under the extract-under-pressure rule (§4), inside
the existing layering until something earns a change to it. Pin behaviour you
rely on with a `Blix.Test.Graphics` section, not with a frozen signature.

---

## What Blix deliberately does *not* have

Stated so the absence reads as a decision, not an oversight. Each answers a
question the engine hasn't earned yet; add one only when a specific, repeated
pain forces it:

ECS · engine-owned scene graph / GameObject hierarchy · prefab system · editor /
scenes-as-assets format · asset registry · material graph · scripting boundary ·
constraint-solver physics · navigation · project templates · reusable
enemy/projectile/gameplay framework · **preview/inspection world container** · **transform
gizmos** · **a shader build system** (as opposed to one shared compile target).

The last three are recent and were declined on §5 grounds rather than for lack of
time: the engine can hand you a ray through any view and remember where a thing has
been, but what a world *contains* — and what selecting something in it means — is a
decision its two would-be consumers already disagree about.

The per-subsystem **Deliberate limits** blocks in [`blix.md`](blix.md) record the
smaller versions of the same call.
