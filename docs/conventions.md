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
- **A `ShaderUniform` on sets 0–1 is per-DRAW.** Its block is bump-allocated from
  a per-frame uniform arena and bound with a **dynamic offset**, so two draws in a
  frame — or two passes sharing one program — may hold different values. A draw
  that writes nothing reuses the last writer's slice; a draw whose bytes are
  unchanged reuses it too, so the common "one per-pass block, handed to every
  draw" shape costs one allocation, not one per draw.
  - This was **not** true until the uniform arena landed. A program owned one
    buffer per frame slot, so the last writer won for every draw in the frame —
    deterministic aliasing, producing a picture internally consistent and wrong.
    It cost two bugs in one session and no instrument saw either.
  - **Sets 2–3 are still per-program-per-frame.** Materials own their buffers
    through `MaterialBindings`, which is already per-consumer and cannot alias;
    making them dynamic would put an offset at ~33 call sites for no gain.
  - Under `BLIX_VK_VALIDATE=1` the device **throws** when two draws disagree about
    a uniform member *on the static path*, naming both passes. Two draws writing
    the same value is normal and does not fire.
  - Push constants remain the right home for small per-draw payloads (copied at
    record time, up to the guaranteed 128 bytes) — they need no descriptor at all.
- **A recorded command is read at Execute, so its payload must not move.** A pass
  body *records*; the backend reads uniforms and texture bindings when the frame is
  submitted. Anything a caller can still change between those two moments is read in
  its final state, by every draw that shares it.
  - **Values are frozen.** Scalar and vector uniforms hold a struct by copy and never
    could move; the three *array* uniforms (`Matrix4x4ArrayUniform`,
    `Vector3ArrayUniform`, `FloatArrayUniform`) copy their payload on construction.
    Measured before it was done: 0 B/frame in every application that can be run here,
    since only Sponza's cascades use the path at all.
  - **Lists are detected, not copied.** A reused `List<ShaderUniform>` rewritten
    between two draws is still a fault. Under `BLIX_VK_VALIDATE=1` each command
    fingerprints its lists at record and the device re-checks at execute, throwing
    with the uniform's name and the pass's. Copying the list is one allocation per
    draw and waits for a draw-heavy consumer to price it — every game in the tree
    passes *zero* inline uniforms today.
  - The rule for a caller is short: **hand each draw its own list**, or reuse one only
    when nothing touches it between the two draws.
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
  (`blix inspect`).

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
  ask diagnostics to route geometry into it. (Settled by Studio's
  embedded viewport — two stages of a real consumer, with no friction from the
  table's location.)

- **Decomposing an application and extracting a library are different bars.**
  Moving code into a shared library needs a *second consumer* wanting the same
  decision. Splitting one executable into several classes needs only that the
  file had stopped being readable — and those classes stay in the executable
  until something else asks for them. A root constructs its parts and calls
  them; there is no discovery, registration, or active-tool branch, and a
  different executable simply builds a different root.

**Enforced by:** [`architecture.md` §"Library, not framework"](architecture.md)
· [`architecture.md` §"How an application is put together"](architecture.md)
· the *Proves / Owns* header block on each `src/Demos/Blix.Demos.*/Program.cs` · the
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

## 6. Mechanism in the engine, policy at the call site

§5 says when to add policy: later than you think. This says where it goes once you
genuinely need it — and it is the rule that had to be re-derived from scratch four
times in a single day before anyone wrote it down.

> **The engine owns the mechanism. The caller declares the policy.**

The engine knows *how* to read a vertex channel, collect equipment, pose a skeleton,
advance a clock. It does not know *whether this caller wants it*. Those are different
questions, and a type that answers both forces every consumer to share one answer.

Worked examples, all arrived at by argument rather than by rule:

- `COLOR_0` is read by the importer (mechanism); `includeColour` says whether to, because
  six applications pin a 32-byte vertex layout and one wants 36.
- Attachments are collected by the importer (mechanism); which are *visible* is the
  caller's, because four of the Rogue's six hang off one hand and a game shows one.
- Every skin in a file is read (mechanism); which palette a part draws against is
  carried per part, because the file has no opinion about that either.
- The clock advances (mechanism); `RigAnimation.EchoStep` says how N bodies come to
  differ, because the viewer wants visible drift and a capture wants reproducibility.

**The failure signature is specific and worth memorising:**

> If a second consumer's only way to differ is to *not use your type*, the policy is in
> the wrong place.

That is not hypothetical. `RigAnimation.Advance` stepped every extra body at
`Subject.Rate * (1 + (i + 1) * 0.17)` — a decision about how obviously un-synchronised
bodies should *look*, which is a viewer aesthetic. The capture tool needed determinism,
had no way to say so, and kept its own copy of the whole class. The duplication then
went unnoticed long enough that the class's own header claimed a second consumer it did
not have, and per-skin palette packing had to be written twice.

Two more of the same kind, both from the glTF importer:

- **A workaround must never justify the limit that forced it.** The one-skin rule was
  defended by a 689-line Blender merge that exists to satisfy the one-skin rule. A
  policy baked into a mechanism will manufacture its own evidence.
- **An authoring convention is not engine behaviour.** `COLOR_0` was applied as ambient
  occlusion because every asset in this tree uses it that way. It is a base-colour
  multiplier, and one reference model said so in a single run.

**Enforced by:** `Blix.Test.Graphics` Section **AZ** (colour is opt-in; the default path
is byte-identical) · Section **BB** (every skin read, each with its own remap) ·
`GltfIgnored` (what the engine did not read, said out loud rather than decided quietly).

---

## 7. Conformance is required; policy waits

§5 says adding policy usually is wrong, and §6 says where policy goes when you need it. Both are
about **decisions**. This is about the thing that is not a decision, and conflating the two costs
real capability.

> **For a format Blix reads, the SPECIFICATION is the requirement. Owning an asset that exercises it
> is not a precondition — it is a download.**

Two different questions wear the same clothes:

- **Conformance.** Does Blix read and render what glTF defines? There is one right answer and the
  spec has it. A reference asset is how the work is *verified*, not whether it may *begin*.
- **Policy and architecture.** How should textures be grouped for residency? How should transparent
  surfaces be ordered? Several answers are defensible, the wrong one calcifies, and a real consumer
  is what tells them apart. That is §5, and it still holds.

**The failure this prevents has a signature:** *"no asset in the tree needs it."* The tree's contents
are an accident of what was downloaded, so that sentence measures our library and not the engine. It
was said four times in one day — about cutout shadows, alpha blending, a second UV set, and a fifth
bone influence — while three public corpora sat one `curl` away, and while a synthetic fixture had
already been authored by hand that same day for a different gap.

It is the same circle as a workaround justifying the limit that forced it (§6): a contingent fact is
promoted to a requirement, and then defended with the evidence it produced. The reply to *"nothing
asks for it"* is **go and get something that asks for it** — see [[blix-gltf-sample-corpus]] for the
three corpora and what each is for.

**Where the line actually falls**, using transparency as the case: *rendering* a BLEND material is
conformance and the spec settles it. *Sorting* blended surfaces correctly is policy — depth peeling,
per-triangle sorting, order-independent blending are all defensible — so that half waits, and the
limitation is stated rather than half-built.

**Enforced by:** `Blix.Test.Graphics` Section **AZ** and **BB** (synthetic fixtures authored for gaps
no owned asset covers) · the glTF attribute table recorded in `GltfStaticImporter` from the spec
rather than from a sweep of the content.

---

## 8. A rule against inventing is not a rule against finishing

- **The failure has one signature: a citation used to stop *completing* something rather
  than to stop *inventing* something.** §4 and §5 exist to prevent pre-empting a pattern —
  naming an abstraction before two consumers want the same decision, adding policy before
  anything disagrees. Neither says a capability may not exist until somebody asks for it,
  and neither gates finishing a pattern that is already named and has instances.
- **Once a pattern is named and instantiated, a further instance is a cost question, not a
  permission question.** Weigh what it costs to build and to carry. "Nobody has asked" is
  not an argument about a pattern that four things already are.
- **A deferral must name what would END it, in terms someone can check.** "When a consumer
  asks" is not checkable — consumers do not file requests, and the phrase survives forever
  because nothing can disprove it. "When a second project declares a recipe", "when a scene
  exceeds a budget", "when Sponza is re-measured" are checkable, and they expire.
- **Three questions before citing a rule to defer. A "no" to any one means it does not apply:**
  1. Am I preventing an *invention* — a shape nobody has built — or the next *instance* of a
     shape already built?
  2. Is the absence I am citing a fact about the **design**, or about **this checkout**?
     Content on another drive, a demo nobody ran, an asset nobody downloaded: none of those is
     a missing requirement. (§7 is the same idea for formats — the spec is the requirement,
     not the contents of the tree.)
  3. If I defer, does something stay **half** built? A half-built thing with a citation on it
     costs more than finishing or deleting it, because the citation makes it look decided and
     the next reader re-derives the same argument instead of the same question.

- *Worked example, and it is ours.* **2026-05-24** (`d376b39`) landed `.blixtex`, `.blixprobe`
  and `.blixmesh` together, and the commit records what they bought: *"Sponza Modern startup:
  ~6s → ~0.5s."* The formats were proven. **2026-05-31** (`568c1d4`) moved Sponza's pack set to
  an external SSD — a portability decision about a multi-GB download, with no design content at
  all. **2026-09-15** (`2aa1faa`), 107 days later, the cook census counted *"0 external images…
  the `.blixtex` sideload path cannot fire on anything in the repository"* and that absence was
  read as **absence of a requirement**, which parked D5 and K-F behind it.
  The rule was applied to an artifact of storage. Nothing about the design had changed since the
  day it was proven; what changed was which drive the content sat on.
- *Second example, same shape.* `AssetLoadReport` was declared **2026-05-25** naming the four
  states a load can be in, and had **zero emitters until 2026-09-16** — 114 days during which the
  emptiness was written up as a finding rather than treated as a thing to finish. It took one
  stage (K-E) once anyone tried.

**Enforced by:** a deferral in any `plan-*.md` naming a checkable end condition rather than a
consumer · `git log` for the two examples above (`d376b39`, `568c1d4`, `2aa1faa`) · and by §4's
own text, whose test is *"do two consumers want the same decision, and does naming it add
capability?"* — a question about abstraction, not about permission.

---

## 9. Comments describe the contract; version control preserves the journey

- **Keep a source comment when it explains something the code cannot:** a present ownership
  boundary, invariant, unit, lifetime, failure mode, or reason a tempting alternative is unsafe.
- **Write that explanation in the present tense.** “This handle stays stable while mips arrive” is
  a contract. A diary of the bug that led there belongs in commit history, an issue or review, or
  a regression test.
- **Keep measurements only while they govern behaviour.** Name the conditions and provenance when
  a threshold, budget, or default still depends on them. Distil a one-off census or completed arc
  into the current documentation and tests it justified, then let version control preserve the
  working record.
- **Do not let prose substitute for enforcement.** A compatibility requirement belongs in a test,
  refusal, assertion, type, or build dependency where one is practical; the nearby comment explains
  why that mechanism exists.
- **Headers state current ownership and usage.** They do not recount which file a type used to live
  in, how many copies preceded an extraction, or which development session discovered the issue.

The test is simple: could a future maintainer act correctly from the comment without reconstructing
the chronology? If yes, keep it near the code. If chronology is the useful part, version control is
its home; current documentation should link only to material that still answers a live question.

**Enforced by:** current-contract comments in `Directory.Build.targets`, `Blix.Recipes/MeshRecipe.cs`,
and `Blix.Tools.Studio/StudioRenderer.cs` · active design records indexed by
[`plans.md`](plans.md) · regression tests that retain the failure after its diary is gone.

---

## Where the surface stands

Blix now reaches across the corners it set out to cover — rendering,
assets/streaming, animation, physics, audio, 2D, diagnostics, and the
game-layer — each *proven by a demo or game* rather than declared. That breadth
is the milestone worth naming.

The **application chassis** is the more recent one: an application now gets a window,
its own interface, named views, retained trails, picking into any of them, shared
shader compilation, shared arguments and a bounded run without restating any of it —
and `src/Demos/Blix.Demos.Chassis/` is the proof, at 25 lines of project file and no shaders
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
