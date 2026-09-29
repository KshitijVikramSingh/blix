# Blix — the current plan

Stages A–E of the portability and input arc are done and merged (PRs #37–#39).
Their reasoning lives where it governs behaviour — code comments, `docs/`, and the
suites that pin it — rather than here, per conventions §9. So does the arguments
and headless-host arc that followed (`AppArgs`, `HeadlessHost`, typed parameters,
`[BlixStartup]`, gate legs with arguments), recorded in [`docs/workflow.md`](docs/workflow.md)
and pinned by `Blix.Test.Apps` and the `demos` gate. What follows is what is still open,
and nothing else.

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

## Tiers and measurement — parked

Discussed and deliberately not started. Two findings hold whatever shape it takes:

- **Named tiers are small and already needed.** `test <tier>:` lines would let the `demos`
  gate's deviceless legs run in CI apart from its GPU legs, and would give the RTS's long gate
  (simulated years, now a shell script) a declared home.
- **Measurement cannot ride `DebugContext`.** The RTS's sealed `--perf-run` switches its own
  diagnostics producer off, because keeping it alive cost over 4 ms a wide-village frame and
  had already skewed two sections' absolute numbers. What it measures instead comes from the
  device (`LastCpuFrameTiming`, `GpuPassTotals` over the steady window), the game's own
  stopwatches and counts, and the display's refresh for cadence, summarised as warm-up plus
  nearest-rank percentiles into one `PERFCASE` line its scripts tabulate. A Blix version would
  be a cheap channel live only in a measured run, the host's own numbers, and one run record per
  run, with cases, arms and ABBA ordering left to the project.

Open: whether the engine owns case execution, what an app hands the measurement channel, whether
Sponza's in-process `--ab` feeds the same records, and whether simulation benches belong at all.

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

**Sponza's lit shader declares 25 samplers in its fragment stage; MoltenVK allows 16.** The
device now says so by name when the program is created (`lit`, and `flat`, `depth_prepass` and
`depth_prepass_mask`, which share its interface), and `--validate` fails a Sponza run on it.
Sixteen is Metal's limit on sampler objects, not on textures: this device reports 256 sampled
images per stage (`vulkaninfo`). So the fix is separate images and a few shared samplers in the
lit shaders, `texture2D`
plus `sampler` in GLSL. Reflection already parses `separate_images` and `separate_samplers`;
what has not been checked is that the device binds a separate sampler rather than treating every
image slot as a combined one. The alternative, Metal argument buffers for all of MoltenVK
(`MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS`), lifts the limit engine-wide and changes every program's
binding path, so it is not the first thing to try. Until then Sponza is not a `demos` gate leg.

**The `demos` gate is not in CI, and now cannot be as it stands.** Its headed legs run each demo
for 45 frames under `--validate`, which needs a GPU and the validation layers, and CI runners have
neither. The root gate is the deviceless one. A CI home for the `demos` gate would first need its
deviceless legs (the probe, `chassis-tune --headless`) separable from the headed ones, which is
what named tiers are for.

**Linux is absent from the CI matrix.** A second red job teaches nothing the first
has not; the shape of what Windows needed should be known before it is copied.

**The Studio baseline needs re-recording.** `.baseline/` is local and gitignored; the
set on this machine dates from 2026-09-17, and 15 of its 68 artifacts no longer match.
Every `.log` and `.exit` does, so the pose fingerprints, travel numbers and exit codes
are unchanged and only pixels moved — the picture was looked at and is right. Bisecting
which commit moved it was abandoned as not worth the hunt. Re-record with
`tools/lab-baseline.sh record`; until then the control is expired, not failing.

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
