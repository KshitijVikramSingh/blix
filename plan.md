# Blix — the current plan

What is open, and nothing else. Finished work is not recorded here: its reasoning lives where it
governs behaviour — code comments, [`docs/`](docs/), and the suites that pin it (conventions §9) — and
its history in git.

---

## Open decisions, and the arcs after them

**OBJ, now that glTF has no importer.** `ObjImporter` has no consumer outside the tests, and
`WavefrontParts` has one, the Check tool. No recipe cooks an OBJ, so both read the source every time,
and their cooked-sibling path, with the `recenter` validation in front of it, is reachable only by a file
no recipe in the tree writes. The open decision: give OBJ a recipe and route it through the cook as glTF
now is, or retire both readers.

**Later, as their own arcs, when something asks:** morph deformation (targets plus a `weights` clip
channel; today refused where they take effect, `MeshRecipe.OpenSource`), then `KHR_animation_pointer` (not
forced into skeletal animation).

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

**A skin that mirrors only some of its joints within one primitive** draws with one front face for the
whole draw, chosen from the first weighted joint's palette. No asset or consumer has it, and there is
no single correct front face for such a draw.

**What each renderer draws of a material** is the table in [`docs/renderer.md`](docs/renderer.md)
("glTF materials: read versus drawn"): Studio ignores eleven `KHR_materials_*` extensions, `unlit`
among them, so an unlit material is drawn lit. Each row closes when something asks for it.

---

## Parked

**The RTS moves to this engine on its next pin bump**, and these break for it. From PR #42:
`vk.LastCpuFrameTiming` and the tuple `vk.GpuPassTotals` are gone (read `host.Timing`); submitted work is
literal (a zero-instance draw counts zero, a negative count throws); a `uniform sampler` with no
`//@sampler` on the line before is a build error; `blix test <word>` reads a leading word as a project;
`RenderGraph`, its handles and builders, `TextureView`, `ShaderInterface`, `ShaderReflection` and
`UniformBlockLayout` are in `Blix.Graphics` (take `IGraphicsDevice`; the casts compile but nothing needs
them). From PRs #44-#51: `ModelData`/`Model` (and `BlixMeshFile.Flat`), `.blixcook`, `SkeletonPlacement`,
a format v18 re-cook, `CreateMaterial(arrayLengths:)` in element counts (was bytes), `WithArrayLength`,
`DestroyIndirectBuffer`, `Transformed` refusing skinned or unknown layouts, `PropModel.Dispose` freeing its
meshes, and `OnUnload` teardowns (`--validate` fails on a leftover). From §J (#52): the GameObject family,
`IAnimated` and the clip animations are gone; `IAnimation.Advance(delta)` replaces `Sample(Time)`;
`IRenderHost.ResetFixedClock` (an implementer must add it), `IFixedGameLoop`, and `Game`'s fixed step owned
by the host with no step cap. From §K (#53, #54, #56): `Bone(Name, ParentIndex, Rest, Offset)` with `Rest`
required and no inverse bind; a palette is `SkinBinding.ComputePalette(BoneWorlds, …)`
(`Skeleton.ComputeBonePalette` is gone); `BonePaletteSet(SkinBinding, capacity)` with `Add(worlds, post)`;
`JointCount` for `BoneCount` on `BonePalette`, `BonePaletteSet` and `BoneBuffers`; `ModelData.Skin(Binding,
JointNodes, Placement)` and `Model.Skins` as bindings; `FindWeightedJoints`; attachments' `BoneName`/
`BoneIndex` and `CarrierNode`; masks validated against their skeleton; recipe 18 (a re-cook); and a
skinned load with `Colour` gets the 92-byte vertex, so its pipeline must declare that layout. From this
arc: `Blix.Import` is gone — `GltfImporter`, `GltfStaticImporter` and the `Gltf*` types they returned — so a
glTF opens by cooking (`CookCache.Resolve`, then `ModelData.Load`); `AssetImportContext` takes an id and a
path only.

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
