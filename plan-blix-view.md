# View arc — plan

> Finishing what `ViewDeclaration` started. Same shape as the toolchain and animation arcs: one
> real consumer forces the decisions, a probe checks what it can headlessly, and engine
> extractions happen only where pressure has already shown.

## Where it actually stands

`Blix.Core.View` is the right inert value — a name, a view-projection, a target handle, and
**two** rectangles (logical and physical), because a pointer arrives in logical pixels and a
framebuffer is measured in physical ones. `ViewPicking.RayThrough` turns a pointer into a ray
through one. The toolchain lab is its only caller, and it works.

Four things stop it being a view *system*, each verified in the code rather than assumed:

1. **The canonical table lives in diagnostics.** `DebugState.Views` is the one `ViewTable`
   (`src/Blix.Diagnostics/DebugState.cs`). Views are therefore reachable only through a
   `DebugContext`, which makes "where is my camera pointing" a diagnostics question.

2. **The Vulkan pass ignores `PhysicalViewport`.** `VulkanGraphicsDevice.Swapchain.cs` sets
   `CmdSetViewport` to the whole target extent, every pass, unconditionally. A view that
   describes a sub-rectangle of a target is describing something the backend cannot do.

3. ~~**ImGui cannot show a rendered texture.**~~ **Closed by stage A.** `VkImGuiRenderer.Submit`
   bound `fontTexture` for every draw command and never read `cmd.TextureId`. An embedded viewport
   is a textured quad in a panel, so this was the hard gate.

4. **UI layout runs after the views are declared.** Per frame:
   `BeginFrame` → `Run(debuggable)` (**views declared here**) → `OnRender` → debug lines →
   **ImGui layout** → `Execute`. An immediate-mode panel's rectangle does not exist when a view
   is recorded. Its own `uiFrameBuilt` flag has the same shape: `WantCaptureMouse` only means
   something after layout, which is most of the way through the frame.

## The consumer that settles it

**One genuine embedded 3D viewport**: a scene rendered to an off-screen target, shown inside an
application panel, orbited with the mouse, picked into with Retina-correct coordinates, with
gizmos and debug geometry drawn through the same view and a capture that reads it back.

That is the whole editor-view substrate, and it arrives **without inventing `Blix.Editor`** —
the lab is the consumer, and everything it forces is a primitive rather than a framework.

## Stage A — ImGui can draw an engine texture — **DONE**

Shipped: `IRenderHost.RegisterUiTexture` / `ReleaseUiTexture`, a texture registry in
`VkImGuiRenderer` with the font atlas registered as just another entry (id 1, via
`io.Fonts.SetTexID`), per-command binding from `cmd.TextureId`, and an **images** window in the
lab showing every distinct albedo the asset uploaded plus the sun's depth buffer.

**Found by doing it:**

- The panel drew nothing at first and the code was correct. It was a collapsing header at the
  bottom of a sidebar that already held six sections, and ImGui **clips items scrolled out of a
  window and emits no draw for them**. The imgui pass reported 3 draws before the change and 3
  after — which is exactly what a working feature looks like when nothing can see it. The draw
  count is the instrument that caught it; the picture could not have.
- A depth image sampled by a colour shader arrives as `(d, 0, 0, 1)` — a red-scale map. It is
  readable enough for the coarse question (is the caster drawing, does the sun cover the subject)
  and a shader that knew it was depth would be a second pipeline the arc has not earned.
- An unknown texture id falls back to the font atlas rather than skipping the draw. A panel that
  quietly renders nothing sends you looking in the wrong place; a rectangle of font atlas is
  unmistakably "wrong texture here", and neither can crash.

## Stage B — a scene in a panel — **DONE**

A second camera on the same scene, its own colour + depth target at half the swapchain's size,
its own graph pass, shown by an ImGui `Image`. Capture reads it back with `--viewport`.

**Found by doing it:**

- **The viewport needed no pipelines of its own.** Two render passes whose attachments match in
  format and sample count are render-pass *compatible*, so every lit and skinned pipeline baked
  against the scene pass is legal in the viewport pass. Giving the panel an `Rgba8` target — the
  obvious choice, since a panel wants display-referred colour — would have doubled the pipeline
  families for a picture that is the same picture from a different chair.
- **The cost of that is untonemapped HDR.** The curve lives in the present pass and a panel has
  no present pass; ImGui samples a texture and draws it. Anything over 1.0 clips. Accepted and
  written down — the fix is a fragment stage that tonemaps, and it is not worth two pipeline
  families until the clipping is in the way.
- **The one-frame lag is real and taken.** The panel's rect is read during layout, which runs
  after views are declared and after the scene is recorded, so this frame's picture is drawn at
  last frame's size. A resize shows one frame of stale aspect. Stage D decides whether that is
  what the substrate wants or whether layout and submission should be split.

## Stage C — picking and orbiting through the panel — **DONE**

**Found by doing it:**

- **The interaction cannot go through `IInputHandler`, and that is correct rather than a
  limitation.** The viewport is an ImGui window, so ImGui captures the pointer over it,
  `GestureOwnership` gives the press to the UI, and the application's handler is never called.
  The picture is an ImGui *item*, so the only thing that can ask "is the pointer on it" is the
  widget, during layout. The engine needed **no change** to allow this — the capture rule was
  already right, and a widget asking about itself is what the rule leaves room for.
- **The pickable rect is the IMAGE's, not the panel's.** The picture is letterboxed inside the
  panel to keep its aspect, so the two differ by the letterbox — and a ray cast through the panel
  rect is wrong by exactly that, silently, and only on panels whose shape happens not to match.
- **Per-view debug routing already worked and had never had a second view.** Naming
  `ViewportSurface` on the declaration is all it takes to put the grid, the sun arrow and three
  skeletons inside the panel: the runtime groups debug commands by `ViewId` and submits each
  group to its own view's target. Confirmed as two passes, `debug:main` and `debug:viewport`.
- **Trails are per-name and cross views.** A trail keyed by name and fed from two views
  interleaves two cameras' worth of points into one history. The panel view draws no trails.

Section **AR** pins what a full-window view could never exercise: an offset rect, the half-open
far edge, two abutting views claiming a shared pixel exactly once, and the Retina invariant —
the same logical pointer must give the same ray when the physical extent doubles.

## Stage D — who owns a view — **DECIDED: nothing moves**

The evidence from B and C, which is what this stage was waiting for:

- `ViewTable` has **one** instance in the whole engine, on `DebugState`. Every reader of
  `.Views` is diagnostics or the runtime's debug-line routing.
- A `ViewId` is used for exactly one thing: **grouping across frames** — which debug commands
  belong to which picture, and which trail remembers which points. Nothing else reads it.
- `ViewPicking.RayThrough` takes a `ViewDeclaration` and never touches the table or the id.
  Section **AR** picks through declarations built by hand, with no table anywhere.
- Two stages of a real consumer — an embedded viewport that renders, orbits, picks, draws gizmos
  and captures — and **the table's location caused no friction at all**.

So the premise was wrong. It looked like "views are diagnostics-owned"; it is actually "a view is
a `Blix.Core` value anyone can build, and interning its name is how you ask diagnostics to route
geometry into it". Moving the table would have been a refactor with nothing behind it, and would
have fixed none of what B and C actually hit.

**What did bite is timing, not ownership.** A view is declared in `Debug(ctx)` and consumed during
UI layout, so the application stashes the declaration in a field between the two. Relocating the
table does nothing about that. The real options are the one-frame lag (taken, in stage B) or
splitting layout from submission — and that is a question about who owns the frame, which wants
its own consumer asking.

The change this stage produced is therefore a **sentence, not a refactor**: `ViewTable`'s own
documentation now says you do not need one to have a view, and why. Conventions §4's worked
examples say the honest answer to "should this be extracted/moved" is often no; this is one.

## Stage E — `PhysicalViewport` in the backend

Several views into **one** target: split-screen, a picture-in-picture minimap, a comparison pair.

Note that stage B does **not** need this — an embedded viewport renders into its own target, so
its viewport is that target's whole extent. This stage waits for the consumer that genuinely
wants two cameras in one image, and may never come.

## Probe work

Headless, exit code, no device:

- a `ViewDeclaration` whose logical and physical rects disagree by the backing scale yields the
  same ray for the same *logical* point — the Retina invariant, in arithmetic;
- a pointer outside a view's rect produces no ray rather than an extrapolated one;
- panel-rect → view-rect mapping round-trips.

## What this arc will not build

`Blix.Editor` · a docking layout · a scene graph panel · a property inspector · gizmo
manipulation (translate/rotate handles you can drag) · multiple windows.

Each is a decision two consumers would disagree about. The arc's job is to make a view a thing
the engine can render into and a pointer can reach — not to become an editor.

## Order, and why

A gates everything (nothing can be shown in a panel until a texture can be). B needs A. C needs
B — there is no panel rect to pick through until there is a panel. D needs B and C, because
ownership should be decided by what two working stages actually reached for. E is independent
and waits for a consumer that does not exist yet.

The honest stopping point is after C: at that point Blix can put a rendered view in a panel and
let a person point at it, which is what "embedded viewport" means. D is bookkeeping that should
be driven by what A–C learned, and E is speculative until something asks.
