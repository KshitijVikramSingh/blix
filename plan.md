# Blix — the current plan

Stages A–E of the portability and input arc are done and merged (PRs #37–#39).
Their reasoning lives where it governs behaviour — code comments, `docs/`, and the
suites that pin it — rather than here, per conventions §9. So does the arguments
and headless-host arc that followed (`AppArgs`, `HeadlessHost`, typed parameters,
`[BlixStartup]`, gate legs with arguments), recorded in [`docs/workflow.md`](docs/workflow.md)
and pinned by `Blix.Test.Apps` and the `demos` gate. The same holds for PR #42: the host's frame
timing (`IRenderHost.Timing`), graph targets' resting layouts, and separate images with
shader-declared samplers are in [`docs/architecture.md`](docs/architecture.md) and
[`docs/renderer.md`](docs/renderer.md). What follows is what is still open, and nothing else.

---

## G — the engine reads cooked models only

Decided 2026-09-30: the engine stops parsing glTF. Models reach it as `.blixmesh`, so every repair a
source needs — patches, normal-map conventions, tangents, validation — happens once, in the cook,
and there is one reader of each fact instead of two (the occlusion strength that both paths misread
is the case). Tools that open an arbitrary `.glb` cook it on open into a cache. RTSGame loads no
`.glb` at runtime.

**Done** (branch `cooked-only`): MikkTSpace in the cook (046c780); the complete static vertex,
`.blixmesh` v11, repacked to the layout a load asks for (2db2e0c); cook-on-open for tools,
`CookCache` (83cecf5); stage 1 below, the scene-graph `.blixmesh` v12 (b270e8d); stage 2, one
`ModelData` and one `Model`, with unread attributes recorded in the file, v13 (7c4dd90). Stage 4 goes
before 3: v12 dissolved the rig-or-static case that made configuration a prerequisite, and converting
source paths that stage 4 deletes would be wasted. Stage 4 done: runtime loads cooked only (d4bc611),
`Blix.Import` takes the parsers and SharpGLTF (ddfa39e), the engine's types lose `Gltf` — `PbrMaterial`
(not `MaterialData`, taken by the `.material` asset record), `TextureData`, `MaterialTextureLoader`,
`AlphaMode`, `UnreadAttribute` (2b6d43a). Stage 3 done: a project's cook configuration (`CookConfig`, `.blixcook`)
replaces `.blixpatch`, read by `blix cook --config`, `blix cook project` and the build's `<BlixCookConfig>`;
each entry stamped by its own hash; Sponza's four patches are one `sponza.blixcook` (972d4b1). Stage 5
done: Studio's static pipeline reads the complete vertex and the cooked tangent frame; the derivative
frame is gone (81a0f80). **§G is complete.**

**The format mirrors glTF's structure, not its encoding.** "Rig or static" is not a question glTF
asks: every mesh reaches a scene through a node, and a rigged file is a scene graph in which some node
has a skin. It was a split Blix made when it had two importers, and the cooked format inherited it —
a static cook bakes world transforms into vertices and keeps the hierarchy as a side table that the
loader un-bakes by inverse (lossy where a transform will not invert); a rig cook keeps no hierarchy at
all, which is why TankArena still parses `tank.glb` at runtime. What stays Blix's is the encoding:
the complete vertex, LOD chains, cooked images, patches applied, tangents generated. Accessors,
buffer views and sparse data stay behind in the cook.

1. **One scene graph in `.blixmesh` (v12).** A node table (hierarchy, local transforms, names) in
   every file; meshes referenced by nodes, stored once however many nodes place them; vertices in
   mesh space; skins as joint-node lists with inverse binds; clips targeting nodes. Splitting and LOD
   error are computed at each node's world scale, so they stay in metres. Read as one `ModelData`.
2. **One resident `Model`.** `Rig` folds into it: skins and clips are optional parts of a model, and
   skinning-only members (`CreateBoneBuffers`, palette packing) refuse by name without a skin.
   `device.CreateRig` goes. Drawing skinned, posed or at bind pose is a consumer's choice, not a type.
3. **A cook configuration per project.** What a project decides about each asset it cooks — material
   rules and normal-map conventions (once `.blixpatch`), split, flipV — in one file the project
   names, which the build, `blix cook` and the Sponza script all read. The stamp records each entry's
   hash. No configuration means glTF's defaults. It is what a cook UI would one day edit.
4. **The engine refuses a `.glb`**, naming the cook. The importers and SharpGLTF move out of `Blix`
   into a separate `Blix.Import` project the cook depends on; the data types lose `Gltf` (`ModelData`,
   `MaterialData`, `TextureData`, `MaterialTextureLoader`, …) and load through their own entry points
   (`ModelData.Load`).
5. **Studio reads the vertex frame.** A static layout carrying tangent, colour and uv1, so the
   derivative frame in `studio_lit.frag` goes.

---

## H — glTF animation as glTF defines it: nodes, not skin 0's joints

glTF animates NODES. Blix read clips only on skin 0's joints, as bone indices; everything else was
dropped or refused. A survey of the corpus (94 files added for this arc) found each case live:
rigid node animation with no skin (AnimatedTriangle, BoxAnimated, CesiumMilkTruck, InterpolationTest),
tracks on non-joint nodes above joints (BrainStem), skins with different joint lists under one clip set
(RecursiveSkeletons: 84 skins, refused), and STEP / CUBICSPLINE sampling (refused outright: the cooked
format stores only time/value keys). Morph weights and KHR_animation_pointer stay out of scope, recorded
as unread.

1. **Sampling.** `.blixmesh` v14 records each channel's interpolation and CUBICSPLINE's in/out tangents;
   the curves evaluate STEP and CUBICSPLINE by the spec's formulas. The cook writes tracks for every
   animated node, not only skin 0's joints.
2. **One animated hierarchy per model.** Its bones are every skin's joints plus every animated node
   (static in-between nodes stay offsets). Clips are bone indices against it; one pose drives every skin,
   each gathering its joints' worlds with its own inverse binds. For a one-skin file with no other
   animated node it is exactly skin 0's skeleton, so existing consumers see no change.
3. **Rigid parts follow the pose.** A part under an animated node is placed by that node's posed world;
   attachments are the special case where the node is a joint. A model with clips is posed whether or
   not it has a skin.
4. **Proof.** A conformance check samples every clip in the corpus at several times and holds skinned
   vertices and rigid part worlds to the spec: node locals from rest TRS plus spec-sampled channels,
   composed up the tree in the test. (Not SharpGLTF's evaluator: its cubic rotation disagrees with the
   spec, checked by hand.)

**Done** (branch `rig-alpha`): stage 1 (762e00a) — sampling, v14, every animated node cooked, 94 corpus
files. Stages 2-4 — the animated hierarchy (`ModelData.Skeleton`, `Skin.Bones`, palettes gathered),
rigid parts under any animated node, posing without a skin in the tools, and the conformance check over
22 animated files (RecursiveSkeletons' 84 skins, BrainStem, InterpolationTest, BoxAnimated, the milk truck).
Out of scope: morph weights, `KHR_animation_pointer`.

---

## I — glTF spec support: the audit, and what it found

The corpus is 150 files (after §H's additions). `Blix.Test.Recipes` now cooks and loads every one and
samples every clip: 137 load, 13 are refused for a listed reason (`ExpectedRefusals`), and the list can
only shrink on purpose. Loading is not reading: a grep of what the reader touches (with a known-positive
control) found the second half. In a proposed order — correctness of what already loads first, then the
refusals, then features:

1. **Primitive modes — done.** Strips, fans and non-indexed primitives unroll to triangle lists in the
   cook (recipe 10), checked triangle-for-triangle against the spec's unrolling; points and lines are
   refused by name (decided: pipelines for them wait for a consumer).
2. **Samplers — done.** Each cooked image row carries its glTF sampler (format v15); the engine maps
   wrap (incl. MIRRORED_REPEAT) and min/mag/mip filters; TextureSettingsTest's six sampler tests pass.
3. **One image, two roles — done.** An image used two ways cooks once per role and convention
   (`ImageVariants`); TextureEncodingTest and TextureLinearInterpolationTest load. Files whose REQUIRED
   extensions Blix does not read are now refused (glTF's rule): SheenChair and TextureTransformMultiTest
   wait for item 5.
3b. **Single-sided culling, double-sided back faces, mirrored nodes — done.** Studio culls a single-sided
   rigid part's back faces, lights a doubleSided back face with its frame reversed, and gives a mirrored
   part (negative determinant) clockwise front faces whether culled or not; TextureSettingsTest and
   NegativeScaleTest pass every row. A mirrored skin (its palette's determinant negative) takes clockwise
   skinned pipelines the same way (RiggedSimple_mirrored: identical to the unmirrored rig).
3c. **Extension texture channels — done.** All 14 KHR_materials_* texture channels cook (colour ones as
   sRGB, the clearcoat normal as a normal map); every textured channel kind in the corpus (16) reaches the
   cooked file. Renderers decide which they draw — Studio draws none of the extensions. Found on the way:
   same-named embedded images overwrote each other's cooked file; each extraction now takes a unique stem.
4. **Refused files — done, and one of them was NOT valid.** A sparse INDEX accessor (Accessor_Sparse_03)
   and a skin with no inverse binds (identity, §5.27) are valid and load. Animation_Skin_06 is not: its two
   joints are separate scene roots, and §5.27 says a skin's joints MUST have a common root (the Khronos
   validator: SKIN_NO_COMMON_ROOT). Commit 8d3b169 called it valid and turned SharpGLTF's validation off
   to read it — the validator was right. *Corrected (on gltf-scene):* sources are validated strictly again;
   reading a skin without a common root is a declared COMPATIBILITY POLICY
   (`MeshRecipe.LenientSkinWithoutCommonRoot`: the joint worlds are still defined by the node graph), and
   only that file is re-read unvalidated, saying so in the cook log. Anything else the validator refuses is
   refused. Test.Recipes `ValidatorRejectionsAreAccounted` lists what strict refuses in the corpus (5 files:
   three unread extensions, primitive restart, Animation_Skin_06) and holds each to a refusal or the
   declared leniency. With the validator off for a lenient file, three MUSTs it had been checking became
   Blix's own and are now explicit: a TRIANGLES index count not a non-zero multiple of three (was silently
   trimmed) and a strip/fan of fewer than three are refused; an inverseBindMatrices accessor with MORE
   elements than joints is valid (§5.27 says at least n) and loads, the first n read.
5. **KHR_texture_transform — done.** Each core channel's offset/rotation/scale cooks into the material
   (format v16) and a texCoord override replaces the channel's set; Studio applies them per draw
   (`uUvRows`, set 1). TextureTransformMultiTest and SheenChair load; the multi-test's checkmarks show,
   and with the transform bypassed its fail symbols do. Other renderers (Sponza, the demos) read the
   transforms off PbrMaterial when they want them.
6. **Scene structure the reader ignores:** scene selection (MultipleScenes), cameras, KHR_node_visibility,
   EXT_mesh_gpu_instancing, KHR_lights_punctual, KHR_materials_variants. Branch `gltf-scene` (on rig-alpha).
   Two of these were silently wrong, not merely absent: both listed as *used*, not required, so they loaded
   — SimpleInstancing drew one copy of its grid, NodeVisibilityTest drew its hidden nodes. MultipleScenes
   drew both scenes on top of each other. DirectionalLight (lights *required*) was refused.

   **One format version (v18) for all six**, so Sponza re-cooks once. Every one is a FACT the file states;
   what a consumer does with it is the consumer's (conventions §10):
   - *Scenes:* each scene's root nodes and the default (SharpGLTF reads an absent `scene` as 0, which the
     spec leaves to the client — kept). `ModelData.Load(…, scene:)` picks one, default the file's; nodes
     outside it are not placed. Node indices stay the file's, so skins and tracks need no remap.
   - *Visibility:* each node's own `visible`; shown = its own AND every ancestor's (the spec's rule). Hides
     meshes (and lights), never cameras. Static here: animating it needs `animation_pointer` (item 7).
   - *Instancing:* each node's instance transforms, applied before its world (row-vector `instance * world`).
     The non-instanced mesh is not drawn. A skinned instanced node is refused by name (the spec defines none).
     Per instance mirroring: an instance's own negative scale flips its front face.
   - *Cameras / lights:* the records and the node that carries each. Studio and `blix shot` can view through a
     file's camera (`--camera`); nothing draws punctual lights yet — that is a lighting arc a consumer asks for.
   - *Variants:* variant names and, per primitive, the material each variant maps it to. A resident `Model`
     keeps every variant's material resolved, so switching is a lookup, not a reload.

   **Done (format v18, recipe 16).** One rule for what a scene draws, on the cooked file
   (`BlixMeshFile.Placement` / `DrawnPrimitives`), read by `ModelData` and the sky-visibility bake alike —
   the bake had its own node walk and would have voxelised hidden, out-of-scene and un-instanced geometry.
   `ModelData.Load(…, scene:)`, `IsPlaced`/`IsShown`/`DrawnWorlds`, `Cameras`/`Lights`/`Variants`,
   `Primitive.MaterialFor(variant)`; `Model` uploads the placed scene only and carries each node's `Shown`,
   `Instances`, camera and light, and every variant's material resolved (`Part.MaterialFor`). Studio draws
   shown nodes, once per instance, in the chosen variant; `blix shot --scene/--variant/--camera`, the viewer's
   *scene* panel (variant, view through a file camera), `blix inspect`'s *scene level*. Instruments:
   Test.Recipes `SceneLevelMatchesGltf` (157 scenes, 6 hidden nodes, 125 instances from the raw accessors'
   T·R·S, 6 cameras, 6 lights, 7 mapped primitives from the JSON), `SceneLevelReachesModelData`,
   `FlattenKeepsFacesUnderMirrors` (a real control: with the fix off, 24/4228 mirrored triangles face right
   and 12% of frames, against 4076/4228 and 71%≈76% unmirrored); nine lab modes (instancing, visibility,
   scenes, scene0, camera-persp, camera-ortho, variant-beach, variant-street, lights). Corpus +3:
   LightsPunctualLamp, DirectionalLight, MaterialsVariantsShoe.

   **Found while looking at Sponza:** `ModelData.Flattened()` (Sponza's load path) bakes a node's world into
   the vertices and, for a mirroring world, neither reversed the winding nor negated the tangent's `w`.
   Sponza has no mirrored node (measured: 0 of 138 mesh nodes), so it never showed; any other flat consumer
   would draw a mirrored part inside-out with its normal map's bitangent backwards.

   **Sponza against 3b/3c, measured from its sources:** its culling already follows `doubleSided` per material
   (single-sided → back-face culled, double-sided → unculled with `gl_FrontFacing` flipping N in the lit and
   pre-pass shaders) — glTF's rule. Shadow casters are unculled on purpose (thin and two-sided casters). One
   UV set read, no texture transform, one sampler (repeat, trilinear) — so 3c's per-channel sets, transforms
   and samplers change nothing it draws today. 373 primitives carry an unread TEXCOORD_1 (lightmap UVs). Its
   main file carries 24 KHR_lights_punctual lights (23 point, 1 sun, all authored at intensity 0) and six
   cameras: the first real consumer for both, if Sponza wants its lamps or authored views.
6b. **Review corrections (on gltf-scene).** From an outside review of rig-alpha, each checked against the spec
   text before changing anything:
   - *Validation:* see item 4 — strict again, two named fallbacks (`MeshRecipe.ValidatorFallbacks`): the
     declared leniency (no common root, invalid) and the validator's own over-strictness (spare inverse
     binds, valid — SharpGLTF demands equality where §5.27 says at least; RiggedSimple_extraibm, derived,
     its third matrix a 100 m translation that must not be read).
   - *Generated tangents follow the normal texture's TEXCOORD set* (§3.7.2.1, "texture coordinates
     associated with the normal texture", its texCoord or a KHR_texture_transform override). They were
     always built over set 0. NormalTangentMirrorTest_normaluv1 (derived: no TANGENT, normal map on a
     quarter-turned TEXCOORD_1): 5240/5240 triangles follow set 1; CONTROL, generation forced to set 0 and
     run: 80/5240. Not handled: a texture transform's ROTATION also turns the frame the map was baked in.
   - *Index counts refused, not trimmed:* a TRIANGLES list not a non-zero multiple of three, a strip or fan
     of fewer than three.
   - *A golden sharing nothing with the reader:* `InterpolationGolden` decodes InterpolationTest's
     accessors from the .glb's own bytes and evaluates Appendix C by hand — the other two animation
     references both took keys through `GltfImporter.SampleKeys`, so a mis-read CUBICSPLINE triple would
     have sat on both sides. 369 points, worst 1e-6; t=1.9 rotation is the hand-computed literal.
   - *Watched, not done:* `Skeleton` now means two things. A skin's skeleton owns inverse binds; the model's
     animated hierarchy (`ModelData.Skeleton`, in the multi-skin or node-animated case) is a pose hierarchy
     with no inverse binds of its own, yet `Bone` carries `InverseBindPose` and the hierarchy inherits skin
     0's where they exist. The runtime never reads them there (every palette gathers through its own skin),
     so it works — but the type says otherwise. The split is "pose hierarchy" vs "skin binding"; do it when
     a consumer is misled by it, or when the next change to Skeleton touches it anyway.

7. **Out of scope unless asked:** Draco, meshopt, KTX2/BasisU, WEB3D quantized (refused by name), morph
   targets and KHR_animation_pointer (recorded as unread), KHR_xmp (metadata).

Material extensions are already broadly read (transmission, diffuse transmission, sheen, volume,
specular, IOR, clearcoat, iridescence, anisotropy, dispersion, unlit, emissive strength); whether each
RENDERS right is the Compare* set's job, judged against each README.

---

## J — time-driven things: the scene layer goes, animation layers arrive, the clock follows

One branch, one PR. The pieces are one whole: what owns time, and how time-driven things compose.

**Read by what each type is, not by how many use it.** The audit (2026-10-01): the GameObject family
(`GameObject`, `AnimatedGameObject`, `SkinnedGameObject`, `PhysicsGameObject`, `Submesh`) was a scene
vocabulary that `Model` replaced. `SkinnedGameObject` is actively wrong now: it composes a mesh-node
transform glTF ignores for skins, and palettes skin 0 only. The `IAnimation` family has the better
COMPOSITION shape: one contract, an ordered host, one-shots that leave. `ClipPlayer` has the better
SOURCE shape: an owned clock (rate, reverse, pause, scrub), root motion across the seam, and the reset
to rest that every hand-rolled consumer got wrong. They are layers of one design, not rivals.

Decided with the user:
- **Delta-driven, each unit owns its time.** `IAnimation.Sample(Time)` keyed off `Time.Total − StartTime`,
  so nothing could pause, change rate, reverse or restart without being rebuilt. It becomes
  `bool Advance(double delta)` — the shape `ClipPlayer.Advance` already proved, and what a fixed step
  will drive.
- **The pose layer stack is separate from the host.** Pose layers apply onto the previous layer's
  result; tweens have no such order. The stack can sit inside a host as one `IAnimation`.
- **A finished one-shot layer holds its last frame by default.** Removing it is a per-layer setting the
  caller chooses (selection is the caller's).
- **Weights only, as stage D decided.** The stack is a flat ordered list of (source, mode, weight,
  mask), with no nodes, parameters, states, transitions or durations. It is not a blend tree. A
  crossfade is still the caller moving a weight.

Stages:
- **J1 — the scene layer goes.** Delete the GameObject family and `IAnimated`, plus `ClipAnimation`,
  `BlendedClipAnimation` and `AdditiveClipAnimation`, which become layer modes in J3. `docs/blix.md`'s
  surface list and getting-started example are rewritten to what Blix is (`Model`, `ClipPlayer`,
  `Transform3D`, `CameraController`); its glTF line still names types that moved to `Blix.Import`.
- **J2 — `IAnimation` on its own clock.** `Advance(delta)`; `AnimationHost.Advance(delta)`; the tweens
  (`FloatAnimation`, `Transform3DAnimation`, `CallbackAnimation`) keep their elapsed time over the curves,
  which stay as they are. There are no tests today: pin the lifecycle (alive, removed, finite end value
  written on the last tick, removal order) in `Blix.Test.Graphics`.
- **J3 — the pose layer stack** (stage D, in reusable form). Ordered layers: a source (a `ClipPlayer`; an
  interface waits for a second kind of source), a mode (override, blend, additive), a weight, an
  optional `BoneMask`, and on-finish (hold, the default, or remove). Advancing it advances every
  layer's player; evaluating starts from rest and applies each layer in order through `PoseBlend` /
  `PoseDelta`, which already do the maths. Studio's `RigAnimation.Compose()` becomes a two-layer stack,
  and the instrument is the lab baseline: IDENTICAL, mode by mode. Plus stage F's controls (a mask set
  to all bones breaks the legs; set to none leaves the walk bit-for-bit unchanged).
- **J1–J3 done** (`b0f83de`, `394cfcd`, and J3): the scene layer deleted; `IAnimation.Advance(delta)` with
  Test.Graphics BR; `PoseStack` with Test.Graphics BS (each mode bit-for-bit its engine call, both mask
  controls, hold/remove). `blix shot` gained `--blend` / `--additive` / `--mask` / `--weight` (the viewer's
  flags) and the lab three `compose-*` modes, recorded on the old composition and IDENTICAL on the stack;
  CONTROL, the additive weight nudged 1% changes compose-additive alone. `lab-baseline.sh` takes a mode
  regex, so a change to a few modes is proven without opening a window for all of them.
- **J4-A — a deterministic clock in the windowed host** (fixed-step A). `--step` means in a window what it
  means headless: every frame advances by exactly that much. It promises deterministic APPLICATION time,
  not execution: wall-clock budgets (a `Drain(ms)`) stay out of scope. One `FrameClock` (Blix.Core) is
  both hosts' sample, taken once per frame; render reads the sample its update saw, and the callback's own
  delta is kept for the FPS readout and ImGui only. `SetVSync` now reaches the device (it set Silk's GL flag,
  a no-op on Vulkan).
- **J4-A done:** Test.Apps (clock: stepped, measured, negative/NaN clamped, 0/∞ refused) and Test.Input
  (`--step` parse). Proof: Runner `--step 0.0166 --dump-frame 100`, three runs IDENTICAL (23 m, speed
  14.467719, 10 coins); CONTROL, measured runs differ (26 m vs 29 m). A focused window takes stray
  keystrokes: two runs that differed only in lane and coins were contaminated by input, not time.
- **J4-B — fixed-update dispatch, owned by the host** (decided 2026-10-01). An optional `IFixedGameLoop`
  (FixedStep, FixedTimeScale, OnFixedUpdate(Time)); `Game` implements it and loses `FixedStepClock`.
  Order per frame: flip input → OnUpdate (input becomes intents; the scale is chosen) → accumulate
  `min(delta, 0.25) × scale` and run OnFixedUpdate per whole step → OnRender with
  `RenderFrameContext.FixedAlpha`. No step cap (30 Hz at 6× is 45 steps); scale 0 is pause; the fixed
  `Total` is its own simulation clock; `ResetFixedClock(total)` clears the residual. No `FixedInput`: one
  would be explicit, never a phase-switching `Host.Input`. Presentation that reads simulation state
  belongs in OnRender; there is no late-update hook.
- **J4-B done:** `IFixedGameLoop` (Blix.Core) with a default scale of 1; `FixedStepClock` rewritten as the
  hosts' scheduler (`Run(loop, frameDelta)`, `Total`, `Alpha`, `Reset`), the step cap gone;
  `RenderFrameContext.FixedAlpha`; `IRenderHost.ResetFixedClock`. `Game` keeps `FixedStep` and gains
  `FixedTimeScale`, both protected and bridged. `IUpdateable`/`IFixedUpdateable` stay as they are: the host
  ticks neither, and a loop calls them on what it owns. Test.Apps: a press latched in OnUpdate reaches
  exactly one step across a stepless frame; the order is update, step, render; the step's Total is simulation
  time; alpha; 32 Hz at 6× runs 48 steps in a quarter-second frame and in a two-second stall (CONTROL: 8
  at 1×); pause runs none and holds alpha; a reset drops the residual; bad step or scale refused. Pong
  reads held keys in its step, so the order does not change it.
- **Review fixes (before merge):** `FixedStepClock` counts its steps up front and refuses a frame that asks
  for more than it can count. A finite `delta × scale` can overflow to ∞, and past 2^52 steps subtracting a
  step changes nothing: either way the old loop hung. A reset from inside a step ends that frame's steps.
  `PoseStack.Add` requires the player's `Skeleton` INSTANCE; a matching bone count is shape, not meaning.
  `AnimationHost.Advance` runs only the animations present when it began, so one added by a callback starts
  on the next advance. `Elapsed`'s doc no longer promises a scrub that writes the target. Headless `--step`
  refuses ∞ while parsing, as the window does.
- **Open, deliberately not here:** `BoneMask.Subtree(skeleton, …)` forgets which skeleton resolved it, and
  later checks compare bone counts only. `All(count)`/`None(count)` are rightly skeleton-agnostic, so the
  fix is a mask that knows its skeleton when it has one, not a blanket rule. (Done in §K1.)

---

## K — which skeleton is this about?

The last sediment of the old rig model: indices that mean a bone only in one hierarchy, carried by types
that do not say which. One theme, three PRs, because K2 deliberately breaks a lot of source and deserves a
clean diff and acceptance story of its own.

- **K1 — mask provenance (branch `mask-provenance`).** `BoneMask.Skeleton` is the skeleton that resolved it:
  `Subtree` sets it, `Inverted` keeps it, `All(count)`/`None(count)` name none. One rule,
  `BoneMask.ValidateFor(skeleton)`: a mask that names a skeleton is valid for that instance only, one that
  names none for any skeleton of its size. Applied wherever the skeleton is known: `PoseStack.Add`, the
  `PoseLayer.Mask` setter (unchecked before; it validates against `Source.Skeleton`, which `Add` already
  holds to the stack's), and `SkeletonGizmo.Draw`. `PoseBlend` keeps its count check, the strongest claim a
  pose (indexed locals, no skeleton) supports: same-sized poses from unrelated rigs still blend there, a
  documented limit, not a reason to give `Pose` an identity. Test.Graphics BS.7; lab `compose-.*` IDENTICAL.
- **K2 — `Skeleton` becomes the pose hierarchy; `SkinBinding` holds the skin.** Decided with the user:
  - `Bone(Name, ParentIndex, Rest, Offset)`: `Rest` required, no `InverseBindPose`. `CreateRestPose` is the
    bones' authored rest, with no reconstruction from inverse binds (hand-built rigs state their rest).
  - `SkinBinding(Skeleton, Bones[] skin joint → hierarchy bone, InverseBinds[])`. It keeps the `Skeleton` it
    indexes: `Bones` without it is indices into some hierarchy somewhere. The palette is a binding operation
    (`Skeleton.ComputeBonePalette` and `BonePaletteSet.Add(skeleton, pose)` go; `AddGathered` becomes
    binding-shaped and checks the worlds against `Binding.Skeleton`). The check tool's fact is
    `JointBindWorlds` (inverse of each inverse bind), matrices, never a `Pose`.
  - `ModelData`: one `Skeleton` (the animated hierarchy); `Skin(Binding, JointNodes, Placement)`.
    `AnimatedHierarchy` stops copying skin 0's inverse binds onto nodes. Bone order is kept as a
    compatibility convention: skin 0's joint order when the animated set is exactly its joints, else node order.
  - Studio's `CountDistinctPoses` fingerprints bone worlds, not a palette computed from skin 0's binds.
  - The cooked format is already binding-shaped (no version bump expected). RTSGame breaks on purpose:
    "skeleton + pose → palette" must say which binding.
  - Instruments: `SkinsMatchGltf`, lab rig modes and the demos gate, all bit-identical.
  - **Done (branch `skin-binding`, PR #54).** As decided, plus what review and measurement added:
    - **`BoneWorlds`**: a skeleton's bone worlds, written only by `Compute(pose)` from that skeleton. The
      binding's palette takes it and refuses another rig's worlds by INSTANCE. A raw `Matrix4x4[]` erased
      ownership again one step after K1 fixed it for masks, and the old doc claimed a check the code did not make.
      The limit is one level down: a `Pose` has no skeleton (as with `PoseBlend`).
    - **Joints, not bones, on every palette type**: `BonePalette`, `BonePaletteSet` and `BoneBuffers` say
      `JointCount`. The old `BoneCount` on the stride is the name that wrote the bug below.
    - **Found by reading, not by any instrument**: Studio's stride came from `SkinSlot.Skeleton.BoneCount`,
      which compiled and became the hierarchy's count. It is equal on every lab rig; on BrainStem (18 joints,
      19 bones) bodies 1 and up shatter. Lab mode `brainstem-inst3` now guards it (control: the old stride's
      render differs from the baseline).
    - `DemoRigsSkinTheSameThroughModel` resolves skin 0's joint tree independently and asserts the joint-order
      convention (control: disabling it fails cesium_man). The Check tool's output on BrainStem matches main's.
- **K3 — conformance cleanup.**
  - A `KHR_mesh_quantization` fixture (accepted today, exercised by nothing).
  - The normal map under `KHR_texture_transform`: a derived regression asset (rotation and non-uniform scale),
    and the fix in the normal-map shading path from the channel's full 2×2 linear part and handedness, not
    baked into the mesh (variants can give one mesh several transforms).
  - Morph targets refused by name when they can take effect: nonzero default weights (mesh or node), or a
    `weights` animation channel. All-zero weights with nothing animating them render as the base geometry,
    which is correct, and load with the targets recorded as unread. Measured: all four corpus morph files
    are refused under that rule; no repo asset has targets.
  - A renderer-support table measured apart from reader support: a `PbrMaterial` field existing is not the
    extension being drawn.
- **Later, as their own arcs:** morph targets (deformation + a `weights` clip channel), then
  `KHR_animation_pointer` (not forced into skeletal animation). A skin that mirrors only some joints of one
  primitive stays an explicit rendering limitation: no asset, no consumer, no single front face.

---

## F — a body in the character room

The one stage not started. `src/Demos/Character/` holds a shared library — room,
motor, camera, renderer, resolver — with `room` (walk it), `room-shot` (capture it)
and a probe (87 assertions, headless) over it.

**The prerequisite nothing listed: there is no body.** The room draws a capsule and
has zero references to `ClipPlayer`, `AnimationClip` or `Pose`. Every consumer of
`BoneMask` and `RootMotion` in the tree is a tool, a test or the preview tool — none
is a character that moves. So "contact drives weights" is not two existing things
being wired together.

**It is a sibling executable**, `Blix.Demos.Character.Body`, over the same library.
That is what the collection's shape is for, and it keeps `room` answering the
question it already answers. The body takes the room's geometry and motor from the
library; what it adds is a rig on top of the capsule.

**First observable, and the whole of the first step:** a skinned Rogue standing in
the room, facing the right way, feet on the floor. Nothing blended, nothing masked.
The unknown is the rig fit against a 0.35 m radius, 1.8 m capsule, and it should
surface on its own rather than tangled in a blending design.

Then, and only once a body walks:

**Contact drives weights, and there are no states.** Speed and groundedness come out
of the resolver and drive blend weights directly — no states, no transitions, no
dwell timers. `BoneMask`'s first consumer that is not a tool: locomotion on the legs,
a one-shot on the upper body. The Rogue carries what this needs — `Walking_A/B/C`,
`Running_A/B`, `Idle`, `Unarmed_Melee_Attack_Punch_A/B`.

*Negative control:* the mask set to `All` must visibly break the legs, and set to
`None` must leave the walk bit-for-bit unchanged. A layer that changes nothing and a
layer that changes everything are a mask's two failures, and both are invisible
unless asked for.

**And then the clip drives the contact.** Root motion says how far, the room says
where you can go. Honest expectation: 4 travelling clips of 76, so this may end a
**decided-no**, and reaching that with the instrument built is the output.

**Acceptance is from the chair.** Walk the room: ramps, stairs, ledges, walls. Every
stage of this arc had at least one fault only a person watching could see, and four
of five were **legibility, not mechanism**.

---

## Acceptance steps that are access, not work

These keep this record open. None can be closed by writing anything.

**No clean Mac has run a published bundle.** Every stage-B measurement was taken on
the machine that built it. "Zero images mapped from `/opt/homebrew`" is the strongest
available proxy and is not the test. Take a `.app` to a Mac with no Homebrew, no
.NET, no Vulkan SDK and no OpenAL, and launch it.

**A `win-x64` publish ships no OpenAL native.** `vulkan-1.dll` is correctly absent —
the GPU driver installs it — but OpenAL Soft is neither a system library nor in a
NuGet runtime pack. Stage B's closure question, unanswered for Windows.

**No physical gamepad has been held.** The lifetime, the trigger range and
re-acquisition are proven against the state machine; the Silk seam is not.
Specifically unverified: that thumbstick index 0/1 is left/right on a real device,
that a resting trigger reads 0.00 rather than 1.00 — the direct observable of the
range fix — and that holding a button while alt-tabbing back does not fire it.
`blix run Chassis --frames 900` prints the lifetime lines; plug, unplug, and
switch away holding something.

**`blix.cmd` is only as verified as CI exercises it**, and the Windows branch of the
native build targets has only ever run on a GitHub runner.

---

## Known gaps that are not scheduled

**The `demos` gate is not in CI, and now cannot be as it stands.** Its headed legs run each demo
for 45 frames under `--validate`, which needs a GPU and the validation layers, and CI runners have
neither. The root gate is the deviceless one. A CI home for the `demos` gate would first need its
deviceless legs (the probe, `chassis-tune --headless`) separable from the headed ones, which is
what named tiers are for.

**Linux is absent from the CI matrix.** A second red job teaches nothing the first
has not; the shape of what Windows needed should be known before it is copied.

---

## Parked

**The RTS moves to this engine on its next pin bump**, and five things will break for it:
`vk.LastCpuFrameTiming` and the tuple `vk.GpuPassTotals` are gone (read `host.Timing`, whose
`GpuPassTotals` is live, so copy the entries to start a window); submitted work is literal, so a
zero-instance draw counts zero and a negative count throws; a `uniform sampler` with no
`//@sampler` on the line before is a build error; and `blix test <word>` reads a leading word as a
project name. PR #42 lists those four. The fifth: `RenderGraph`, its handles and builders, `TextureView`,
`ShaderInterface`, `ShaderReflection` and `UniformBlockLayout` are in `Blix.Graphics` now, so a file
that had only `using Blix.Graphics.Vulkan;` for them needs `using Blix.Graphics;`. Its casts still
compile, but nothing needs them: take `IGraphicsDevice`, and read isolation from `host.Timing`.

**The teardown SIGSEGV** (exit 139 after a demo's exit line; `UMEntryThunk::Decode` under
`GetDelegateForFunctionPointer`, with a small number, `0xb`, `0xd` or `0x80000001`, where a function
pointer belongs). Pre-existing and rare: two legs in one `demos` gate run, then none in fifteen
targeted reruns and two full gates. `VulkanGraphicsDevice.Init.cs` holds what is known. Waiting for
a report taken with `BLIX_TEARDOWN_TRACE=1` exported, which names the step; gate legs inherit it.

---

## Deferred until something asks

Probe density, `BakeMerge`, clip-lookup-by-name, further `PropModel` adoption.

**Asset manifests** (finding assets by logical name, declared external asset roots). Every
in-repo program finds its assets through `AppFiles` and the importer's cooked-sibling lookup,
and Sponza's external pack set is one required variable. The hand-run preparation steps do not
fit either: EXR conversion needs Blender, and the sky bake is a scene-level driver by design.
Each waits for a consumer, which is conventions §5 and §8, and waiting does not keep
a plan record open. The probe-density measurements and the approaches already refuted
are in [`docs/renderer.md`](docs/renderer.md) so the same ground is not walked twice.
