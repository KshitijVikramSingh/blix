# Blix chassis — reshaping the engine for ambitious applications

> Branch `view-first-class`. First arc of a larger reshape.

The goal is not to cut duplication. It is to change the **shape** of Blix so that
more ambitious applications are expressible at all — with Spear (motion, contact,
camera, combat) as the first consumer that will pressure it.

## §1 — The five singulars

Blix today assumes **one view, one frame, one world, one executable, one host**.
Every item on the chassis wish-list is an instance of breaking one of them:

| Singular | Where it is baked in | What breaking it buys |
| --- | --- | --- |
| **One view** | `DebugDrawChannel.ViewProjection` is per-channel mutable state; `DebugFrame.DrawViewProjection` is one matrix; the dump schema has one matrix | viewports, picking, editor cameras, offscreen capture, second-camera inspection — all one mechanism |
| **One frame** | diagnostics is current-frame only | trails, histories, event streams — the shape motion questions actually have |
| **One world** | no second spatial container | inspection without dictating how a game represents anything |
| **One executable** | 97 flags behind one `RTSGame/Program.cs` dispatcher; 33 `AppContext.BaseDirectory` sites | probes as first-class, shared content, sibling apps |
| **One host** | `Window` decides ImGui exists only for `IDebuggable`, owns the debug pass, the dump trigger, input routing | applications compose rather than inherit |

The organising rule for sequencing is **not** risk:

> Removing a singular assumption is almost never wrong. Adding a policy usually is.

Named views, time-aware diagnostics, a project scope and decomposing `Window`
*remove constraints* — they change what is sayable and nothing downstream must
agree with them. Gizmo semantics, IK solvers, navigation abstractions and character
controllers *encode decisions*, and that is where two consumers genuinely disagree.
The doc that started this applied exactly that caution to navigation
("RTSGame and Bulwark already demonstrated that two navigation systems can want
completely different abstractions") and did not apply it to the editor toolkit.
It applies there too.

**In scope for this arc:** making the view first-class, end to end.
**Explicitly deferred until Spear pushes:** `EditorWorld3D`, gizmos, a selection
registry, animation players/IK, capsule sweeps.

## §2 — Why the view goes first

It is the assumption the other four hang off. Viewports, preview worlds, picking,
multi-view capture and DPI all become *consequences* of a first-class view rather
than separate work, and it forces the `Window` decomposition on its own terms
instead of as a cleanup.

Two measurements make the timing obvious:

- **51** `Draw.*` call sites in the whole tree; only **8** places set
  `ViewProjection`. This change is as cheap as it will ever be, and every
  application added makes it worse.
- **There is no schema version field** in `JsonDumpSink`, whose comment claims a
  "stable on-disk schema". If the dump is to be the contract with downstream
  tools, that gap closes in the same move.

### The premise being retired

```
// ViewProjection is per-channel state (not per-command) because every
// primitive in a single frame shares one camera.
```

That is a claim about the world, not a cost decision — and cost was never the
objection: `DebugDrawFrustum` already carries a full `Matrix4x4` per command and
nobody minded. There is no tradeoff to relitigate, only a false premise.

### The evidence it is binding

§212 (the RTS "motorbike") is the case study. It cost several sessions because the
drawn yaw lived only in the render loop, so the only instrument was a window and a
pair of eyes; two fixes were judged on headed runs that died early and both
measured *worse*. What was actually needed was to watch one body from a fixed
vantage while the game camera did its own thing — **a second view over the same
geometry** — and there was no way to ask for it.

## §3 — The design

`View` lands in **`Blix.Core`**. The boundary this requires already exists:
`Blix.Graphics` is pure abstraction (`readonly record struct TextureHandle(int Id)`
— an opaque int, no device, no backend), `Blix.Core` already references it, and
**neither Core nor Diagnostics references `Blix.Render`**. So "a view may know a
handle but never a render graph" is enforced by project references today.

Core also already contains the degenerate single view:

```csharp
public readonly record struct RenderFrameContext(int Width, int Height);
// "Render-side per-frame info. Width/Height describe the default framebuffer"
```

A view is not a new concept being imported into Core. It is the generalisation of
a type that has been sitting there — and note `DebugFrame` currently carries the
surface (`RenderFrameContext`) and the camera (`DrawViewProjection`) as two
separate singular fields. A view is those two facts fused and named.

```csharp
public readonly record struct ViewId(int Id);          // interned from name

public readonly record struct ViewDeclaration(
    ViewId Id,
    string Name,
    Matrix4x4 ViewProjection,        // told, never computed
    RenderSurfaceHandle Target,      // opaque int
    Rect LogicalViewport,
    Rect PhysicalViewport);          // DPI resolved once, centrally
```

### Decisions, and why

**A view is TOLD its matrix; it never owns a camera.** `Camera3D` lives a layer
above Core and carries policy — projection convention, FOV, orbit/fly behaviour.
A view that holds a matrix stays inert, and then a game camera, an editor camera,
a shadow cascade and a hand-built matrix all feed the identical mechanism.

**Declared per frame.** The app declares its views each frame; commands reference
them by id. `DebugFrame`'s own contract is that snapshots must not be
retroactively mutable, because `Freeze()` holds frames past the live builder's
lifetime — so a frame holding a *reference* to a live view would silently
re-render from a camera that has since moved, a bug that would look exactly like
the diagnostics lying. A declaration is already a value, so that class of bug
cannot exist. Apps may still keep long-lived view objects; what reaches
diagnostics is a per-frame value.

**`ViewId` is interned from the name and stable across the session.** Trails and
histories must correlate "the same view" over N frames. This is the one thing that
must not be per-frame, and it is why time-series work is downstream of this arc.

**Commands bind by ambient scope, storing the resolved id.** This is exactly how
`Path` already works — a `Stack<string>` with `IDisposable Scope(name)`, ambient
at the call site, resolved and *stored* on the command. Ambient-only would repeat
the mutable global that needed an emit-once footgun warning; stored-only would be
noisy across 51 sites.

```csharp
using (debug.Draw.In(mainView)) { ... }
```

**Do NOT overload `Path` to carry the view.** `Path` is already the layer-toggle
key — "what kind of thing is this". A view is "where is this seen from". Fusing
them is the near-synonym fault this codebase has paid for repeatedly (see
`ThreatSystem.HarmReach`, and `JobSystem.IsAtItsPlace`'s explicit warning that
"two notions of 'is it there' is the exact near-synonym fault that has cost this
codebase days").

**Drawing with no view in scope throws.** Today it silently produces an identity
matrix, renders in clip space, is invisible, and needs a warning to explain
itself. §180 settled this class of question — "blocked should not happen, might as
well crash and throw when that happens so we can debug" — and `BeginActivity` now
throws on a nameless act. A primitive with no view is the same bug.

**No implicit default view.** `Window` auto-declaring "main" would keep all 8 sites
working untouched, at the cost of re-creating the mutable ambient default that
caused the footgun. Every frame is self-describing instead; the break is small and
one-time.

**`Target` stays on the declaration.** It is the one field about rendering rather
than about where things are seen from, and dropping it would make `View` purely
observational. But off-screen targets are exactly what editor panels need, and the
picking story wants the target and the rect together. Kept as an opaque handle.

### What falls out

- **Picking**: pointer in physical coords → the view whose physical rect contains
  it → view-local coords → ray from that view's matrix. Editor viewports stop
  being special.
- **DPI**: resolved once where a view is made, instead of in each application.
- **Capture**: per-view and windowless, because a view names its own target.

## §4 — The dump schema

The only piece here with a downstream contract, and the most valuable artifact for
how this project actually works.

- `DrawViewProjection: Matrix4x4` → `views: [ViewDeclaration]`
- each draw command gains its `view`
- **add `schemaVersion`**, which does not exist today

## §5 — Migration

`DebugFrame`, the dump schema and its readers, `Window`'s routing, the overlay UI,
and 51 call sites (8 of which are the real ones). Acceptance: a single frame
carries two views over the same geometry, both present in one dump, and a headless
run can capture either without a window.

## §6 — Built

Shipped on `view-first-class`. The whole solution builds; 191/191 diagnostics,
389/389 graphics, 43/43 physics.

| Piece | Where |
| --- | --- |
| `ViewId`, `ViewDeclaration`, `ViewTable` | `Blix.Core/View.cs` |
| `ViewId` on every draw command | `Blix.Diagnostics/DebugDrawCommand.cs` |
| `In(view)` scope, `Declare(...)`, `CurrentView` that throws | `Blix.Diagnostics/DebugDrawChannel.cs` |
| the one view table | `DebugState.Views` |
| `Views` replacing `DrawViewProjection` | `Blix.Diagnostics/DebugFrame.cs` |
| schema 2 — `Views[]`, per-command `View`, `SchemaVersion` | `Blix.Diagnostics/JsonDumpSink.cs` |
| one pass per view, each to its own target | `Blix.Runtime.Silk/Window.cs` |
| ranged submit so one buffer serves many views | `Blix.Runtime.Silk/VkLineDrawer.cs` |

### What the throw found immediately

Making a viewless primitive throw was supposed to replace a warning. It also
surfaced **six producers that had been drawing into nowhere** — emitting geometry
having never set a camera, so it went to the identity matrix, into clip space, and
was never seen by anyone. Five were in the test suite. The sixth was the engine's
own **selection highlight**, which asks a question the old model could not:

> which view does system feedback belong to?

Answer taken: **every view declared this frame**. A selected object should be
outlined wherever it can be seen, and a second viewport showing the same object
should show it selected too. The selection sweep already runs after every
contributor, which is where an application declares what it drew into, so the list
is complete by then. No views declared means the application drew no picture, and
there is nothing to annotate — skipped, not an error.

### Acceptance

`Blix.Test.Diagnostics` now asserts the §5 criterion directly: two views over the
same geometry in one frame, distinct ids, each command naming its own view, and a
view keeping its identity across frames — which is what any trail will stand on.
Plus schema 2 is asserted by version, by `Views`, by per-command view *name*
(process-local ids mean nothing in a file), and by the absence of the old
frame-wide camera.

### Verified on the GPU, after fixing the thing that hid it

`Window.AppendDebugLinesPass` groups commands per view and submits one pass per
view, to that view's own target. A headed run reports:

```
passes/debug:main/draws: 1
passes/debug:main/triangles: 21
passes/debug:main/instances: 1
```

`debug:main` is the pass named for the declared view — so the routing works, and a
single-view application reduces to exactly what it did before.

Getting there needed a launcher fix that had nothing to do with this arc and had
been hiding in plain sight. `tools/run-vulkan-hello.sh` set
`DYLD_FALLBACK_LIBRARY_PATH` and the app never saw it, because Homebrew's
`$prefix/bin/dotnet` is a **`#!/bin/bash` wrapper script** and `/bin/bash` is
SIP-protected: dyld strips `DYLD_*` from the environment of any protected binary it
execs, so the muxer laundered the variable away before the app started. Silk.NET
then reported "doesn't support Vulkan on this computer" — about as misleading as an
error gets, since Vulkan was installed and fine.

Every other launcher in `tools/` already execs the apphost directly and says why in
its header. This was the last one still on `dotnet run`, which is why RTS, Tank and
Bulwark headed runs worked and this demo had apparently never run.

(`VulkanHello` also does not parse `--frames`; it runs until closed.)

### Still open from §1

The other four singulars. Nearest: time (trails and histories now have a stable view
to hang off), then `Window`'s decomposition, which this arc has already loosened by
taking the debug pass's single-camera assumption out of it.

## §7 — Time, and the part of it that already worked

The second singular, and it turned out to be half-broken already.

**Scalar history exists and always did.** `DebugFrameHistory` is a ring of finished
frames, and `DebugOverlayUi` already draws sparklines over it for every stat and
timer via a path-keyed selector. Cross-frame *aggregates* are computable from that
ring by any consumer — which is exactly what the sparkline selector is. So most of
"improve scalar histories and plots" was a request for something already built, and
building it again would have been the near-synonym fault with a graph attached.

**What was genuinely impossible: a draw command outliving its frame.** Channels
clear on `BeginFrame`, so geometry is discarded every frame and a trail could not be
built on top of anything. That is a real constraint, it is small, and a path through
space is the commonest temporal question there is about anything that moves —
which is Spear's entire domain.

### Evidence, and its weakness

Stated honestly: the pressure here is **thin**. Sweeping the tree for hand-rolled
cross-frame accumulation finds essentially one real consumer — RTSGame's
`StallCensus` — with the other hits (`SimulationWorld`, `EconomySystem`,
`FactionKnowledge`) being game state that legitimately accumulates and is nobody
else's business. One consumer is not the repeated pressure `docs/conventions.md` §4
asks for before extracting.

So the scope is deliberately the constraint removal and nothing else:
`DebugTrails` **stores points and computes nothing**. No smoothing, no resampling,
no summarising, no reductions. Remembering removes a constraint; deciding what a
path MEANS is policy, and policy waits for a second consumer to disagree with the
first.

### What was built

| Piece | Where |
| --- | --- |
| bounded, path-keyed, time-stamped point store | `Blix.Diagnostics/DebugTrails.cs` |
| `Trail(name, point, colour, seconds)` and `Polyline(...)` | `DebugDrawChannel` |
| `DebugDrawPolyline` | `DebugDrawCommand.cs` |
| the store, on state that survives a frame | `DebugState.Trails` |
| stale-trail expiry once a frame | `DebugSystem.EndFrame` |
| polyline rendering | `Window.AppendDebugLinesPass` |
| points written out, not counted | `JsonDumpSink` |

`DebugTrails.Append` takes the current time **as an argument** rather than reading a
clock, which is the only reason anything that ages out is testable at all.

### What the tests found

Drawing one trail into two views **sampled it twice a frame**, so the two views
disagreed about the body's history by one point. A trail is where something has
been — a fact about the frame, not about how many times somebody asked to look at
it. Now sampled once per path per frame however often it is drawn, which is what
makes "one history seen twice" true rather than nearly true.

The subtler test asserts that a sealed frame still reports the trail it *actually*
had: the store rewrites its list every frame, so the command copies. Same rule as
the view declarations, same reason — `Freeze()` holds frames past the builder.

### Verified

206/206 diagnostics, 389/389 graphics, 43/43 physics. Headed, with two trails added
to `VulkanHello`'s spinning cube corners, the debug pass climbs from ~93 triangles
to a steady **~340** and plateaus — points accruing until the two-second window
fills, then ageing out as fast as they arrive.

### Caps are not silent

`MaxPointsPerTrail` is a memory guard, not a tuning knob, and when it bites the
trail is shorter than the duration asked for. It says so once per path rather than
returning a truncated answer that looks complete.

## §8 — One host

`Window` decided what an application was allowed to have, from a single type test.

```csharp
if (gameLoop is IDebuggable) { ... imguiRenderer = new VkImGuiRenderer(...); }
```

That one line made two unrelated questions into the same question: *does this
application produce diagnostics?* and *may it have an interface?* An application
wanting a panel of its own had two options, neither good — pretend to be a
diagnostics producer, or go without. And the frame itself was hard-wired:
`VkImGuiRenderer.BeginFrame` called `ui.Layout(debugSystem)` directly, so even with
ImGui alive the only thing that could ever be in it was the diagnostics overlay.

### What changed

`IUiSource` lands in `Blix.Core` with **no UI types in its signature** — just
`string UiName` and `void DrawUi()`. ImGui is an immediate-mode global, so an
implementation calls it inside the method and Core never takes a dependency on a UI
library on an application's behalf. The demo adds its own `ImGui.NET` package
reference, which is the design working rather than a wart: same discipline as a
`ViewDeclaration` knowing a surface handle and never a renderer.

`VkImGuiRenderer` is a renderer again rather than a renderer of one particular
thing. There were two near-identical copies of the IO setup — one that drew the
overlay panels, one that drew the perf HUD — differing only in what happened
between `NewFrame` and `Render`. That difference now belongs to the caller:

```csharp
BeginFrame(..., content: () => {
    appUi?.DrawUi();
    if (overlayUp) imguiRenderer.LayoutDiagnostics(debugSystem!);
    else if (hudUp) imguiRenderer.DrawPerfHudText(hudText);
});
```

One frame, filled by everyone with something to draw, instead of two mutually
exclusive paths neither of which an application could enter.

### The bug the decomposition found

**`WantCaptureKeyboard` had been defined since `VkImGuiRenderer` was written and was
never once read.** Typing into any ImGui text field also drove the game — every
keystroke arriving twice. It had gone unnoticed because the only interface that
existed was the diagnostics overlay, which has almost no text fields; the moment an
application can have its own panel, it has fields.

Mouse capture had a narrower version of the same fault: `OverlayWantsMouse` required
`debugSystem.State.ShowOverlay`, so an application's panels could be clicked
straight through into the game beneath them.

Both are now `UiWants*` — about whether *a* UI wants the input, not about which UI
it is — and gated on an ImGui frame having actually been built, which is the only
point at which those flags describe anything.

Key **release** is always delivered, even while the UI has focus. That is the
asymmetry `OnMouseUp` already documents and paid for: a press decides who owns a
gesture, a release only ends one, and the thing it ends belongs to whoever got the
press. Guarding it would strand a key the game believes is still held the moment
focus moves to a panel mid-keypress. Applied here before it was paid for twice.

The runtime's own bindings (F12 dump, `` ` `` overlay) answer before capture, so a
focused text field cannot swallow the two keys most needed exactly when something
has gone wrong.

### Verified, and what is not

206/206 diagnostics, 389/389 graphics, 43/43 physics. Headed, `VulkanHello` now
carries its own panel and the `imgui` pass records 2 draws alongside the trails.

**Interactively confirmed from the chair**: dragging the Hello panel's `seconds`
slider works, which is the panel rendering and responding.

**Not proven, and stated as such**: neither capture fix is exercised, because
`HelloLoop` is not an `IInputHandler` at all — there is no game input for a UI to
steal, so nothing here can demonstrate that it doesn't. Nor is the non-`IDebuggable`
path proven: every demo that wants UI is also a diagnostics producer. Both want the
tiny custom-app executable from the next singular, which has to exist anyway.

## §9 — One executable

Three things were per-executable that are not properties of an executable: how to
compile a shader, how to read the arguments every Blix application shares, and how
to stop after N frames.

### The shared shader target

Thirteen projects carried a copy of `CompileSpirV`. The argument is not tidiness —
**copies drift, and one already had**: `Blix.Demos.VulkanInstanced` omits
`@(GlslInclude)` from its `Inputs`, so editing a shared `.glsl` would not retrigger
its compile. Harmless only because that project includes nothing; the next one to
add an `#include` inherits a silent staleness bug, and nothing about a stale shader
announces itself.

They were not thirteen copies of one thing. Eight are byte-identical (the app
shape). Two are a **library** shape — `Blix.Render` and `Blix.Runtime.Silk` build
into their own source tree so the `.spv` ships with the library. Two are different
**algorithms**: `VulkanSponza` reflects each module into a sidecar, `Runner`
compiles `#define` variants from item metadata. Those two opt out. Folding them in
would make this a shader build *system*, which is policy.

**The near miss.** `Directory.Build.targets` is imported *after* the project body,
and a target redefined under the same name **replaces** the earlier one. Naming the
shared one `CompileSpirV` silently overrode the two projects that kept their own,
and the opt-out condition then turned the replacement into a no-op. They built clean
and shipped no shaders — Sponza 20 modules to 6, Runner 13 to 6 — with
"Build succeeded" over the top. Renamed `BlixCompileSpirV`, which cannot collide.

> A green build is not evidence that a build step ran.

Caught only by stashing the change and rebuilding the baseline to compare. The
second trap was avoided the same way: the library copies used
`$(MSBuildThisFileDirectory)`, which in a `.csproj` means the project directory but
in a root-level shared file means the repo root.

### Arguments and bounded runs belong to the host

Six applications each carried the same loop looking for `--frames`, then threaded
the answer into their own game loop, which counted its own frames. `VulkanHello`
never implemented it at all — its launcher accepted `--frames` and silently ignored
it, which is how this session came to tell the chair to expect a clean exit that was
never going to happen.

`WindowOptions.FromArgs` claims the handful of arguments that are about *being a
Blix application* — `--frames`, `--width`, `--height`, `--title`, `--debug` — and
ignores the rest, because an application knows its own flags and this should not
pretend to. The host counts frames in `OnRender`, so every application gets a
bounded run whether or not it thought about one.

### `Blix.Demos.Chassis` — the executable spec

A 25-line project file and 137 lines of application, declaring **no shaders at
all**. Compare the ~80 lines a Blix executable used to need, about half of it
restating how the engine compiles a shader.

It exists to answer the two questions §8 left open, because every other application
that wants an interface is also a diagnostics producer:

**1. Does an application get a UI without being `IDebuggable`?** This loop
deliberately is not. It counts calls to `DrawUi` — which the host only makes inside
an ImGui frame it has built — so the answer is a log line rather than a pair of
eyes, which matters because a loop with no diagnostics has no overlay to report pass
stats:

```
chassis: UI drawn on 40/40 frame(s) with NO IDebuggable on this loop.
Exiting after 40 frame(s) as asked.
```

Both halves are results: the panel drew, and the host stopped an application that
never mentions `--frames`.

**2. Does UI capture actually suppress game input?** The loop counts every input
event it receives and remembers the count when the text field takes focus. While the
field is focused that number must not move, however much is typed — and the panel
says so live, in green or red. Still wants a human, but it is now checkable in one
glance instead of being asserted.

## §10 — One world, and the half of it that has no consumer

The last singular, and the one where the honest answer is "part of this is not ready
to be built."

### What was genuinely blocked

`IDebugSelectable` carried its own reason in its header:

> The runtime never picks for the demo — it doesn't know about cameras or screen
> space. Picking math lives where the camera lives.

True when written. **The view arc retired it.** A `ViewDeclaration` is exactly a
camera and a screen rectangle with a name on it.

`Camera3D.ScreenPointToRay(x, y, width, height)` exists and is used in four places,
so picking was never missing — but it takes a viewport size and **recomputes** the
view-projection from its aspect ratio, which quietly assumes the view fills the
window and that its matrix is the one the camera would derive. Neither holds for a
view drawn into a panel, which is what views were made first-class to allow.

`Blix.ViewPicking.RayThrough(view, pointer)` reads the view's **own** matrix and its
**own** rectangle. Section AN pins it:

- for a full-window view it returns **exactly** the camera's ray — a picking routine
  that disagrees with the camera is worse than none, so the new path is tested
  against the old one Section X already covers;
- a panel view picks at its own centre, offset and all — the case
  `ScreenPointToRay` cannot express, because it has nowhere to put the offset;
- a pointer outside the rectangle yields **no ray**, which is how "which view is the
  cursor over?" gets answered;
- an off-screen target picks identically to an on-screen one, which is the whole
  reason an inspector viewport stops being a special case;
- a non-invertible matrix yields no ray rather than throwing.

Logical coordinates throughout, compared against `LogicalViewport` — the physical
rectangle is the renderer's. A view carries both so neither is derived at a call
site, which is the retina bug every application has had the chance to write.

### What was NOT built, and why

The **preview world** — a container holding models, transforms, lights and pick
bounds for inspection — is not here. It has **no consumer**. Sponza inspects its own
scene; RTSGame picks its own entities; the chassis app has no world at all. The two
applications in this tree that pick things already disagree about what an entity is,
what its bounds mean and what selecting one does.

That is the same argument §1 made for deferring `EditorWorld3D`, and it did not stop
applying because this is the last item on a list:

> Removing a singular assumption is almost never wrong. Adding a policy usually is.

A ray through a named view removes a constraint. A container that decides what a
world contains is policy, and it should be written against Spear's first application
rather than guessed at one arc early. What exists now is the half that makes such a
container *possible* to build later without the engine having an opinion about it.
