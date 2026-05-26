# Vector B — render graph (reduced scope)

Status: reviewed via /plan-eng-review at commit b8ee06f. Scope reduced from
the full graph in `docs/vulkan-reshape-shaderlab-target.md` Section 3 to
the load-bearing subset that ShaderLab port (step 6) needs day 1, deferring
abstractions that have clear future triggers.

Vector A binding model + step 4 offscreen-pass machinery are the
foundation this builds on. The graph compiles down to existing
primitives: `VkRenderPass`, `VkFramebuffer`, `RenderPassDescription`,
`DrawIndexedCommand`. No reinventing wheels.

## What's in scope

Declarative pass topology with derived barriers, baked at engine init.
The "focused middle" position from the audit (saved-context line 315).

Concretely:
- `RenderGraph` + `GraphicsPass` declarative API
- Graph-level resource factories: `ColorTarget`, `DepthTarget`, `DepthCube`
- `TextureView` with cube-face addressing (`cube.Face(int) → TextureView`)
- `Read` / `Target` / `Depth` edges
- `Compile()` performs validation + backend resource allocation
- Backend infers barriers from declared `Read` edges
- `graph.Execute(commandList, ...)` records pass commands into the
  existing `RenderCommandList` per-frame path
- Window resize triggers reallocation of `matchSwapchain` resources

Closes F-006 (derived sync) and F-012 (LoadOp on declaration).

## What's NOT in scope

Each deferred item has a clear future trigger so it gets revisited at
the right time, not earlier.

- **`ComputePass.Execute`** — interface DECLARED in VB.i; calling
  `Execute()` throws `NotImplementedException`. Triggered by step 8
  (froxel fog). F-005 closes then.
- **VkRenderPass compatibility-group collapse / subpass collapse** —
  one `VkRenderPass` per `GraphicsPass`. Saved-context says explicitly:
  "no subpass collapse in v1." Triggered by post-v1 measurement showing
  pass-boundary cost matters. F-015 closes then.
- **Conditional passes (Q-007 in design doc)** — graph passes always
  execute. Triggered by shadow-cache need (point-light shadow rebake
  only when light dirty).
- **`TextureView` mip-level + array-layer subviews** — cube faces only.
  Triggered by bloom mip chain (step 6 late) and IBL env prefilter
  (step 6 or 7).
- **Transient/aliased resource lifetimes** — saved-context "persistent
  only." Triggered when VRAM pressure shows the cost.
- **Migration of existing imperative demos** — VulkanHello cube stays
  on `commandList.Pass(...)` imperative as the known-good baseline.
  Graph demos (VB.vii validation + step 6 ShaderLab port) are separate
  call sites.
- **GL backend integration** — graph is Vulkan-only. GL backend doesn't
  see graph at all; existing GL demos keep using `Blix.Render.Material`
  imperative path. F-005 / F-006 / F-012 / F-015 are Vulkan-side
  closures; GL gets sunset in step 9 regardless.

## What already exists

- **`VkRenderSurfaceEntry`** (step 4) — graph reuses the per-surface
  `VkRenderPass + VkFramebuffer + memory + layout-transition` machinery
  for offscreen graph targets. No reimplementation.
- **`RenderPassDescription`** — graph compiles to this. The existing
  `Execute(commandList)` per-frame loop handles begin/end/submit.
- **`DrawIndexedCommand`** — graph's per-pass scope records into the
  same command shape (uniforms, textures, material, push constants).
- **`MaterialBindings`** (set 2) — graph respects per-material set
  lifetime. Draw scopes bind materials per-draw exactly like step 4 +
  2e do today.
- **`ShaderInterface`** — graph passes carry a list of these via
  `.Shader(...)`. Compile() validates set-1 compatibility across
  listed shaders.
- **Push constants** (set 3) — per-draw lifetime unchanged. Graph
  scopes pass `pushConstants:` to `DrawIndexed` calls as today.

## Decisions locked in this review

| Decision | Choice | Rationale |
|---|---|---|
| Scope size | Reduced (option B in D2) | Full graph (~12-15 new types) is overbuilt for current step needs |
| TextureView ordering | Land with graph resource handles (VB.ii) | ShaderLab point-shadow passes need it day 1 of step 6 |
| Pass execution order | Declaration order = execution order; Read edges validate only | Explicit > clever; misordered declarations fail loud at Compile() |
| `graph.Execute` integration | Records into existing `RenderCommandList` | Step-4-shaped demos keep working; graph migration is per-demo, not big-bang |
| Window resize | In scope for Vector B | Avoids "don't resize the window" folklore once graph owns many `matchSwapchain` resources |

## Pinned design rules (no decisions, documenting for clarity)

- **Per-pass `Shader(...)`** is a closed set. Compile() rejects shaders
  with incompatible set-1 layouts in the same pass. Per Q-008
  resolution in `docs/vulkan-reshape-shaderlab-target.md`.
- **Validation** runs in `graph.Compile()` and throws
  `InvalidOperationException` with named errors. Checks: no cycle in
  `Read` edges, every `Read(handle)` references a declared resource,
  every pass has at least one `Target`, no two passes with same name,
  set-1 layout compatibility across each pass's shader list.
- **Resource ownership**: graph owns resources created via
  `graph.ColorTarget` / `DepthTarget` / `DepthCube`. Externally-passed
  resources (`graph.Read(externalCubemap)`) are referenced, not owned;
  destroying the graph leaves them alone.
- **Set ownership across the graph**:
  - Set 0 (per-frame): graph-managed via `graph.SetPerFrame(interface, struct)`
  - Set 1 (per-pass): graph-managed via `graph.SetPerPass(passHandle, struct)`
  - Set 2 (per-material): MaterialBindings instances; bound per-draw
  - Set 3 (per-draw): push constants; passed per-draw via `DrawIndexed(..., pushConstants:)`
- **Zero-alloc per-frame**: graph.Execute uses cached instance-field
  scratch buffers cleared per call, matching Vector A's
  `uniformMappedPtrs` / `uniformMappedBuffers` pattern.
- **Per-pass RenderPassDescription caching**: each compiled pass owns
  a long-lived `RenderPassDescription` instance reused across frames.
  Clear-color values are pinned per-pass at compile time (they're
  static for v1; conditional/dynamic clears are deferred). Viewport
  + scissor follow the resolved attachment extent. The instance is
  invalidated + rebuilt only on resize (VB.vi). This is the explicit
  invalidation key the outside-voice review flagged.

## Sub-step sequence

Each sub-step ends with `dotnet build` clean and tests passing.

- **VB.i** — Public API types (Blix.Graphics.Vulkan).
  - `RenderGraph` class (constructor takes `VulkanGraphicsDevice`).
  - `GraphicsPassBuilder` (fluent: `.Target` / `.Depth` / `.Read` / `.Shader`).
  - `ComputePassBuilder` (fluent: `.Read` / `.Write` / `.Shader`; `.Dispatch()` later).
  - `GraphResourceHandle` (color / depth discriminated).
  - `TextureView` record with cube-face addressing.
  - `LoadOp` / `StoreOp` enums.
  - Pure type declarations. No backend wiring.
  - **Effort:** ~half day.

- **VB.ii** — Graph resource factories + pure-function validation.
  - `graph.ColorTarget(name, format, size)` / `DepthTarget(name, size)` / `DepthCube(name, faceSize)`.
  - `graph.Compile()` runs pure-function validation: cycle detection,
    undeclared-read check, duplicate names, empty pass list,
    per-pass set-1 layout compatibility.
  - Throws `InvalidOperationException` with named errors.
  - Section K tests cover every validation branch (no Vulkan device needed).
  - **Effort:** ~1 day.

- **VB.iii** — Backend compile: VkRenderPass + VkFramebuffer per pass.
  - `Compile()` walks passes; reuses `VkRenderSurfaceEntry`-style
    machinery (`AllocateAttachmentImage`, `CreateSurfaceRenderPass`,
    `CreateSurfaceFramebuffer` — promote those to internal helpers).
  - Per-pass descriptor pool sized at compile time (set 0 + set 1
    bindings × MaxFramesInFlight).
  - Per-pass `VkImage` + `VkImageView` for declared `ColorTarget` /
    `DepthTarget` / `DepthCube` resources.
  - Cube-face attachments use `TextureView` to pick the right
    `VkImageView` per pass.
  - **MoltenVK smoke gate:** create a 2×2 cube-face attachment via
    `TextureView.Face(0)`, render-pass-begin + render-pass-end at
    least once during VB.iii bring-up to verify
    `VK_IMAGE_VIEW_TYPE_2D` on a cube image works on MoltenVK
    (known quirky around `VK_KHR_maintenance1` semantics). Fail
    loud at startup if not, with a pointer to the MoltenVK version
    that does.
  - **Effort:** ~1-2 days.

- **VB.iv** — Barrier inference from Read edges.
  - For each pass: walk its `Read` set; for each read, look up the
    producer pass (most recent prior pass with that handle as Target);
    emit a barrier with appropriate src/dst stage + access masks.
  - Implementation as a pure function on the pass list → list of
    `BarrierOp` records → translated to `vkCmdPipelineBarrier` at
    record time.
  - Pure-function unit-testable via toy graphs.
  - **Effort:** ~1 day.

- **VB.v** — Per-frame execute path.
  - `graph.Execute(commandList, parameters)` — appends passes' worth
    of `RenderPassDescription` + `DrawIndexedCommand`s to `commandList`.
  - `graph.SetPerFrame(shaderInterface, struct)` writes set 0 UBO.
  - `graph.SetPerPass(passHandle, struct)` writes set 1 UBO.
  - `graph.Pass(handle, scope)` opens a per-pass scope where the
    caller emits draws (mesh, material, push constants).
  - Scope wraps the existing `pass.DrawIndexed(...)` surface.
  - **Effort:** ~1 day.

- **VB.vi** — Resize handling.
  - Hook into existing `RecreateSwapchain` flow at
    `Swapchain.cs:710`.
  - Graph subscribes; on resize, walks `matchSwapchain` resources,
    destroys + recreates `VkImage` + `VkImageView`, rebuilds affected
    `VkRenderPass` (when format changes) + `VkFramebuffer`.
  - **Pipeline cache interaction:** pipelines were created against
    pre-resize `VkRenderPass` objects. When a render pass is
    recreated on resize, every pipeline that referenced its layout
    must be recreated too (Vulkan render-pass compatibility rules).
    The graph tracks pipeline ↔ render-pass dependency and
    recreates dependent pipelines transparently. Without this,
    resize crashes draw-time with a validation-layer error about
    incompatible render passes.
  - Per-frame draws against the rebuilt graph see the new extent.
  - **Effort:** ~1 day (was half-day; pipeline-cache piece adds the
    other half).

- **VB.vii** — Validation graph demo.
  - New project `Blix.Demos.VulkanGraph` (or extend VulkanHello with a
    `--graph` switch — decide at implementation time).
  - Three passes: cubes → offscreen HDR → invert post-process → present
    to swapchain. Each declares its target + reads.
  - **Verification gate (revised):** "remove the Read edge to see
    breakage" is unfalsifiable on MoltenVK — undefined-layout reads
    can stably return previous-frame contents. Instead the gate is:
    (a) validation-layer clean run (no warnings or errors), (b) the
    inverted-pass output is deterministically inverted from the
    offscreen pass — write a "screenshot equality" test that runs
    one frame and asserts pixel inversion at a known location, OR a
    debug overlay shows numerical inverted-tint = white-tint − 1.
  - Visual gate: drag-resize the window; demo continues to render.
  - **Effort:** ~half day.

- **VB.viii** — Section K tests.
  - Pure-function tests for `Compile()` validation branches.
  - Pure-function tests for barrier inference (toy 2-pass graphs).
  - `TextureView` cube-face equality.
  - Pass-ordering assertions (declaration order preserved through
    `Compile()`).
  - **Effort:** ~half day.

**Total estimated effort:** ~6-8 days focused work.

## ASCII diagram — graph data flow

Pass declaration / compile / execute cycle. Saved-context's "focused
middle" — declarative I/O, baked once, parameters per frame.

```
+---------------------------------------------------------------+
|                       Engine init time                         |
|                                                                |
|   var graph = new RenderGraph(device);                         |
|                                                                |
|   ColorTarget("hdr-scene", Rgba16F, matchSwap)        --+      |
|   DepthTarget("scene-depth", matchSwap)                 |      |
|   DepthCube("point-shadow", faceSize: 512)              |      |
|                                                         |      |
|   GraphicsPass("point-shadow.face0")                    |      |
|       .Target(pointShadow.Face(0), Clear)               |      |
|       .Shader(ShadowShader.Interface)                   |      |
|                                                         |      |
|   GraphicsPass("scene")                                 |      |
|       .Target(hdrScene, Clear)                          |      |
|       .Depth(sceneDepth, Clear)                         |      |
|       .Read(pointShadow)         <-- Read edge          |      |
|       .Shader(LitShader.Interface)                      |      |
|                                                         v      |
|                                            graph.Compile()     |
|                                                  |             |
|   +----------------------------------------------v---------+   |
|   |  Validation (pure):                                    |   |
|   |   - cycle detection                                    |   |
|   |   - undeclared-read check                              |   |
|   |   - duplicate pass names                               |   |
|   |   - empty pass list                                    |   |
|   |   - per-pass set-1 layout compatibility                |   |
|   +--------------------------------+-----------------------+   |
|                                    |                           |
|   +--------------------------------v-----------------------+   |
|   |  Backend compile:                                      |   |
|   |   - allocate VkImage/View/Memory per graph resource    |   |
|   |   - VkRenderPass per GraphicsPass                      |   |
|   |   - VkFramebuffer per GraphicsPass                     |   |
|   |   - descriptor pools sized for set 0 + set 1           |   |
|   |   - barrier table from Read edges                      |   |
|   +--------------------------------------------------------+   |
+---------------------------------------------------------------+
                          |
                          v
+---------------------------------------------------------------+
|                       Per-frame time                          |
|                                                                |
|   graph.SetPerFrame(LitShader.Interface, perFrameStruct);     |
|   graph.SetPerPass("scene", perPassScene);                    |
|                                                                |
|   graph.Pass("point-shadow.face0", scope => {                 |
|       scope.Draw(mesh, shadowMaterial, pushConstants: model); |
|   });                                                          |
|                                                                |
|   graph.Pass("scene", scope => {                              |
|       scope.Draw(mesh, litMaterial,                           |
|                  pushConstants: model);                       |
|   });                                                          |
|                                                                |
|   graph.Execute(commandList);                                 |
|     |                                                          |
|     +-> for each pass:                                         |
|         - emit barriers from inference table                   |
|         - append RenderPassDescription + DrawIndexedCommand    |
|           to commandList                                       |
|     |                                                          |
|     v                                                          |
|   device.Execute(commandList)  <-- existing per-frame loop     |
+---------------------------------------------------------------+
```

## ASCII diagram — barrier inference

For pass B's Read edge to resource X (written by pass A), the inferred
barrier sits between A's command-buffer end and B's begin. Vulkan's
subpass dependencies on the per-surface VkRenderPass already cover the
external→subpass transition at A's end; the inference table adds the
between-pass image-memory barrier with the right access masks.

```
Pass A                   Pass B
+---------+              +---------+
| writes  |              | reads   |
|   X     |              |   X     |
+----+----+              +----+----+
     |                        ^
     | A's finalLayout:       |
     | ColorAttachmentOptimal |
     |                        | B expects:
     |                        | ShaderReadOnlyOptimal
     v                        |
  vkCmdPipelineBarrier   <----+
    srcStage:  ColorAttachmentOutputBit
    srcAccess: ColorAttachmentWriteBit
    dstStage:  FragmentShaderBit
    dstAccess: ShaderReadBit
    oldLayout: ColorAttachmentOptimal
    newLayout: ShaderReadOnlyOptimal
```

## Failure modes

For each Vector B codepath, one realistic production failure:

| Codepath | Failure mode | Test coverage | Error handling | User-visible? |
|---|---|---|---|---|
| `Compile()` validation | Developer declares cycle (rare) | ★★★ planned | Throws with named edge | Clear error at startup |
| Backend compile | Out of VRAM allocating attachment | ☐ no test | Throw via existing `FindMemoryTypeIndex` | Clear error at startup |
| Barrier inference | Two passes simultaneously write same X (no Read between) | ★★★ planned | Defined as "last writer wins, no barrier between" | Silent (correct) |
| Per-frame execute | `SetPerFrame` called with mismatched struct size | ☐ planned | Throw if `Marshal.SizeOf<T>() != block.TotalSize` | Clear error first frame |
| Resize handling | Resize fires mid-frame + pipeline cache invalid | ☐ no test | `Vk.DeviceWaitIdle` covers timing; pipeline-cache recreation (VB.vi) prevents incompatible-render-pass crashes | None if VB.vi pipeline-cache piece lands; otherwise loud validation-layer crash |
| Compute pass | User calls `compute.Dispatch()` in v1 | ☐ trivially | Throws `NotImplementedException` with "step 8" pointer | Clear error |

**Critical gap flagged:** SetPerFrame struct-size mismatch can produce
silently-corrupted GPU state if not caught. Adding the size assert in
VB.v is non-negotiable — already in the plan.

## Worktree parallelization

Mostly sequential. Outside-voice review noted the original parallel
claim oversold: barrier inference's PURE function (stage/access mask
derivation from edges) can land standalone, but actual
`vkCmdPipelineBarrier` emission needs backend-resolved image handles +
per-image current-layout state from VB.iii. The "pure function" split
holds at the API boundary; the "Lane B parallel with VB.iii" overlap
is shallow.

| Step | Modules touched | Depends on |
|---|---|---|
| VB.i | Blix.Graphics.Vulkan (types only) | — |
| VB.ii | Blix.Graphics.Vulkan + Blix.Test.Graphics | VB.i |
| VB.iii | Blix.Graphics.Vulkan | VB.ii |
| VB.iv | Blix.Graphics.Vulkan (pure mask derivation) | VB.ii |
| VB.iv-emit | Blix.Graphics.Vulkan (emit + layout state) | VB.iii + VB.iv |
| VB.v | Blix.Graphics.Vulkan | VB.iv-emit |
| VB.vi | Blix.Graphics.Vulkan + Blix.Runtime.Silk | VB.iii |
| VB.vii | Blix.Demos.VulkanGraph (new) | VB.v + VB.vi |
| VB.viii | Blix.Test.Graphics | VB.ii + VB.iv |

**Lane A**: VB.iii → VB.iv-emit → VB.v → VB.vi → VB.vii
**Lane B** (parallel after VB.ii): VB.iv (pure derivation) → VB.viii

Conflict risk: both lanes touch `Blix.Graphics.Vulkan` — merge
coordinate at the `VulkanGraphicsDevice` partial-class boundary, ideally
in separate new `.cs` files (`VulkanGraphicsDevice.RenderGraph.cs`,
`VulkanGraphicsDevice.RenderGraph.Compile.cs`).

## Tests

Surface-shape + pure-function tests live in `Blix.Test.Graphics`
Section K. No live `VkDevice` needed for any of them. Backend wiring
is exercised by the demo's visual gate at VB.vii.

Required test cases:
- K.1: graph resource factory round-trips (`ColorTarget`, `DepthTarget`,
  `DepthCube`, `cube.Face(i)` equality + bounds)
- K.2: fluent pass-builder chain returns builder + records edges
  correctly
- K.3: Compile() rejects cycle (named edge in error message)
- K.4: Compile() rejects undeclared read (named resource in error)
- K.5: Compile() rejects duplicate pass name
- K.6: Compile() rejects empty graph
- K.7: Compile() rejects set-1-incompatible shader list
- K.8: Barrier inference: A writes X, B reads X → one barrier with
  correct stage/access masks
- K.9: Barrier inference: A writes X, B writes X (no read) → no
  barrier between
- K.10: Pass execution order matches declaration order through Compile

## Implementation Tasks

Synthesized from this review's findings. Each derives from a specific
finding or sub-step.

- [ ] **T1 (P1, human: ~half day / CC: ~1h)** — `Blix.Graphics.Vulkan` —
  Declare `RenderGraph` / `GraphicsPassBuilder` / `ComputePassBuilder`
  / `GraphResourceHandle` / `TextureView` (cube-face) / `LoadOp` /
  `StoreOp` types. Pure declarations, no backend wiring.
  - Surfaced by: VB.i sub-step
  - Files: `Blix.Graphics.Vulkan/RenderGraph.cs`,
    `Blix.Graphics.Vulkan/GraphicsPassBuilder.cs`,
    `Blix.Graphics.Vulkan/TextureView.cs`
  - Verify: `dotnet build` clean

- [ ] **T2 (P1, human: ~1 day / CC: ~3h)** — `Blix.Graphics.Vulkan` —
  Graph resource factories + pure-function validation in `Compile()`.
  - Surfaced by: VB.ii + Finding 1 (TextureView ordering)
  - Files: `Blix.Graphics.Vulkan/RenderGraph.cs`,
    `Blix.Graphics.Vulkan/RenderGraph.Validate.cs`
  - Verify: Section K tests K.1-K.7 pass

- [ ] **T3 (P1, human: ~1.5 days / CC: ~4h)** — `Blix.Graphics.Vulkan` —
  Backend compile: `VkRenderPass` + `VkFramebuffer` + descriptor pools
  per pass. Reuse step-4's attachment helpers (promote from private to
  internal).
  - Surfaced by: VB.iii
  - Files: `Blix.Graphics.Vulkan/VulkanGraphicsDevice.RenderGraph.cs`,
    `Blix.Graphics.Vulkan/VulkanGraphicsDevice.RenderSurfaces.cs`
    (refactor)
  - Verify: VB.vii demo builds clean (no execute path yet)

- [ ] **T4 (P1, human: ~1 day / CC: ~3h)** — `Blix.Graphics.Vulkan` —
  Barrier inference as a pure function on the compiled pass list.
  - Surfaced by: VB.iv + Finding 2 (declaration order)
  - Files: `Blix.Graphics.Vulkan/RenderGraph.BarrierInference.cs`
  - Verify: Section K tests K.8-K.10 pass

- [ ] **T5 (P1, human: ~1 day / CC: ~3h)** — `Blix.Graphics.Vulkan` —
  Per-frame execute: `SetPerFrame` / `SetPerPass` / `Pass(handle,
  scope)` / `Execute(commandList)`.
  - Surfaced by: VB.v + Finding 3 (records into existing
    `RenderCommandList`)
  - Files:
    `Blix.Graphics.Vulkan/VulkanGraphicsDevice.RenderGraph.Execute.cs`
  - Verify: VB.vii demo renders one pass correctly

- [ ] **T6 (P2, human: ~1 day / CC: ~3h)** —
  `Blix.Graphics.Vulkan` + `Blix.Runtime.Silk` — Resize handling:
  graph subscribes to swapchain-resize signal; reallocates
  `matchSwapchain` resources; recreates dependent pipelines (the
  pipeline-cache piece called out by outside-voice review).
  - Surfaced by: Finding 4 (resize in scope) + outside-voice finding 8
    (pipeline cache invalidation)
  - Files: `Blix.Graphics.Vulkan/VulkanGraphicsDevice.RenderGraph.cs`,
    swapchain integration point in
    `VulkanGraphicsDevice.Swapchain.cs`
  - Verify: VB.vii demo survives drag-resize with validation layer
    clean (no incompatible-render-pass warnings)

- [ ] **T7 (P1, human: ~half day / CC: ~2h)** —
  `Blix.Demos.VulkanGraph` (new project) — Validation graph demo:
  three-pass topology (cubes → offscreen HDR → invert → present).
  - Surfaced by: VB.vii
  - Files: new `src/Blix.Demos.VulkanGraph/` project + shaders
  - Verify: visual gate — demo renders all three passes; window resize
    works; debug overlay still draws

- [ ] **T8 (P1, human: ~half day / CC: ~2h)** — `Blix.Test.Graphics` —
  Section K tests for pure-function validation + barrier inference.
  - Surfaced by: VB.viii
  - Files: `src/Blix.Test.Graphics/Program.cs`
  - Verify: `dotnet run --project src/Blix.Test.Graphics/...` reports
    full pass count

Total: 8 tasks, ~6.5-7.5 days human / ~21-22 hours CC.

## Unresolved decisions

None. All seven decisions resolved:
- D2: scope reduced (option B)
- D3: TextureView lands early with graph resource handles
- D4: declaration order = execution order; Read edges validate only
- D5: graph.Execute records into existing RenderCommandList
- D6: resize handling in scope
- D8: build Vector B first, then port (cross-model tension 1)
- D9: D4 stands (cross-model tension 2)
- D10: defer point-shadow optimization to step 6 measurement (cross-model tension 3)

The remaining design rules (per-pass Shader semantics, validation API,
resource ownership, migration path) have clear answers from
`docs/vulkan-reshape-shaderlab-target.md` Section 3 + Q-005/Q-006/Q-008.

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
|--------|---------|-----|------|--------|----------|
| CEO Review | `/plan-ceo-review` | Scope & strategy | 0 | — | (not run; scope-set in this session) |
| Codex Review | `/codex review` | Independent 2nd opinion | 0 | — | (codex unavailable on this machine) |
| Eng Review | `/plan-eng-review` | Architecture & tests (required) | 1 | CLEAR | 4 arch decisions resolved + 3 cross-model tensions resolved + 5 plan-doc fixes applied |
| Design Review | `/plan-design-review` | UI/UX gaps | 0 | — | (no UI scope in Vector B) |
| DX Review | `/plan-devex-review` | Developer experience gaps | 0 | — | (internal engine; n/a) |

- **OUTSIDE VOICE:** Claude subagent ran (codex unavailable). Raised
  8 findings; 3 produced cross-model tensions (D8, D9, D10) all
  resolved in favor of current plan; 5 produced plan-doc spec
  corrections (Lane B parallelization overstated, MoltenVK cube-face
  smoke gate, RenderPassDescription cache invalidation key, VB.vii
  verification gate revised for MoltenVK, pipeline-cache invalidation
  on resize). All applied.
- **CROSS-MODEL:** Reviewer ratified outside-voice plan-doc
  corrections; declined to re-open D4 (declaration order) or scope
  multiview into Vector B per D9/D10.
- **UNRESOLVED:** 0
- **VERDICT:** ENG CLEARED — Vector B reduced scope, 8 sub-steps,
  ready to implement.
