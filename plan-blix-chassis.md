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
