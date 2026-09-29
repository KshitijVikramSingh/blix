# Blix — the current plan

Stages A–E of the portability and input arc are done and merged (PRs #37–#39).
Their reasoning lives where it governs behaviour — code comments, `docs/`, and the
suites that pin it — rather than here, per conventions §9. What follows is what is
still open, and nothing else.

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

## G — programs take arguments, and run without a window

Two consumers wrote the same glue. The RTS dispatches 36 scenarios through 720 lines of
hand-written flag parsing; Sponza parses about 52 flags its own way and never calls
`WindowOptions.FromArgs`, so `--frames` does nothing there. And the RTS's declared apps
run before its culture is set, which prints "8,65,710 wood" on some machines.

**One parse, many readers.** Blix parses the command line once, culture-invariant, into a
read-only view. Each layer reads the flags it understands: the window its size and
`--debug`, a headless host `--frames` and `--dump-frame`, the app everything else. There is
no central schema. Every read is recorded, and an argument nothing read is a warning
line, in interactive and bounded runs alike. The `string[]` app signature goes; in-repo
apps migrate rather than carry it.

**A headless host** runs the same `IGameLoop` with no window and no device: fixed-step
time, `OnRender` against a recording-only command list, a scriptable `InputState`, and
the same diagnostics sinks. The same arguments mean the same thing with or without a
window, and the window adds a few of its own.

Stages:

- **G1. The loop contract moves down.** `IGameLoop`, `Game`, `Time`, `FixedStepClock`,
  `IUpdateable` and `IFixedUpdateable` are built by `Blix.Core`, still in namespace
  `Blix`, so a loop no longer drags in `Blix.Render` and Vulkan. *Done.*
- **G2. The argument view.** `AppArgs` in `Blix.Core`; `BlixApps.Main` is every program's one-line
  `Main`, runs the named app or the one marked `Default`, and prints what nothing read;
  `WindowOptions.FromArgs` and `ObjectTunables.Apply` read from it. Every in-repo app
  migrated, Sponza's 52 flags and all ten `cook` verbs included. The three build-infrastructure
  executables (`Blix.Cli`, `Blix.Tools.Apps`, `Blix.Tools.Shader`) keep their own parsing: MSBuild
  and the launcher call them, and none is a Blix app. *Done.*
- **G3.** The headless host, in its own project, referencing no backend.
- **G4.** Typed parameters as sugar over the view: kebab-case flags (the squashed form
  accepted too), a missing default means required, usage recorded in the app index.
- **G5.** A startup hook `Dispatch` runs before any app.
- **G6.** `blix.project` gate lines carry arguments, one app per line.

*Done when* Sponza reads `--frames` through the view, the same loop runs headed and
headless with the same arguments and writes dumps of the same shape, and a gate line
passes arguments. The RTS pins its engine and migrates later, not in this arc.

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

**`demos` has a gate of one probe.** Eleven demos are still verified by being run and
looked at, which is right for a demo. Future experiments append to that line; the
gate growing is a consequence, not a plan.

**The `demos` gate is not in CI.** The workflow runs the root gate only. Closing it
is one step in `ci.yml` — deliberately not taken here rather than slipped in.

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
Each waits for a consumer, which is conventions §5 and §8, and waiting does not keep
a plan record open. The probe-density measurements and the approaches already refuted
are in [`docs/renderer.md`](docs/renderer.md) so the same ground is not walked twice.
