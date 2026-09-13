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

## Stage B — a scene in a panel

An off-screen colour target, sized from the panel's rectangle, rendered before the UI submits
and shown by it.

**The frame-ordering decision lands here, and the answer is probably "one frame".** The panel's
rect is known only after layout; the view needs it before. Every immediate-mode editor resolves
this the same way — *this* frame's picture is drawn at *last* frame's size — and a resize costs
one frame of stale aspect. The alternative is splitting layout from submission so panel rects
are known before rendering, which is the real fix and a much larger change to who owns the
frame. Stage B should take the one-frame lag, **write down that it did**, and let stage D decide
whether the lag or the split is what the substrate wants.

## Stage C — picking and orbiting through the panel

The first use of `ViewDeclaration`'s two rectangles for something other than the whole window.
A pointer in window-logical pixels has to be mapped into the panel's rect before it becomes a
ray, and on a 2x display the physical rect is what the framebuffer was.

**Why this is the stage that proves the type:** `RayThrough` has been correct and untested
against anything but a full-window view. A panel at an offset, on a Retina display, is the case
the two-rectangle design exists for, and the case where an off-by-the-backing-scale bug is
invisible in a screenshot and obvious to a hand.

## Stage D — who owns a view

Only after B and C. The question is not "should the table move out of `DebugState`" — it is
*what the substrate needs*, and two stages of a real consumer is the evidence. Candidates: a
host-owned `ViewTable`, a view registry beside the graphics device, or leaving it where it is
and admitting that views are a diagnostics concept with one game-facing accessor.

**Resist moving it before then.** The table is currently reachable and correct; relocating it on
principle would be a refactor with no consumer behind it, which is the thing conventions §4
warns about.

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
