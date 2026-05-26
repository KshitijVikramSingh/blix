# Vulkan-backend API friction notes

Running log of places where the existing engine surface (`IGraphicsDevice`,
`Material`, `MaterialResolver`, `RenderCommandList`, etc.) creaks against
Vulkan during implementation of `Blix.Graphics.Vulkan`. Each entry should
be short — one paragraph max — describing the symptom, the root mismatch,
and a rough redesign direction. The audit at the end of the validation
phase consolidates this into the concrete reshape plan.

Append-only during a push; consolidate during the audit.

---

## Pre-implementation: friction points named before any Vk code was written

These were called out in the strategic discussion before the cube push,
so they don't have a "discovered during" anchor — they're known up front.

### F-001 — `CreateShaderProgram(ShaderSources)` takes GLSL text

GL-style: shader source is GLSL, compiled at runtime against the driver.
Vulkan-style: shader is SPIR-V bytecode, compiled offline. Runtime GLSL→SPV
needs libshaderc or DXC. **Direction:** make SPIR-V bytes the primary
surface (`CreateShaderProgram(byte[] vertSpv, byte[] fragSpv, ...)`); add
an optional layered helper for runtime GLSL compilation when we want hot
reload. Today we have a Vulkan-only `CreateShaderProgramFromSpv` as a
side channel; the main `CreateShaderProgram` throws NotImplemented.

### F-002 — `Material.SetUniform(string name, ...)` / `SetTexture(string name, ...)`

GL-style: uniforms found by name via reflection (`glGetUniformLocation`).
Vulkan-style: bindings are explicit `(set, binding)` pairs declared in the
SPIR-V; names are not consulted at draw time. Today on Vulkan we'd have to
either (a) reflect the SPIR-V to recover names → bindings (`SPIRV-Reflect`),
or (b) declare the layout out-of-band and translate names → slots at the
material layer. **Direction:** material bindings become explicit
`(set, binding)` references, with names kept as developer-facing aliases
in the JSON material descriptor (resolved at cook time, not runtime).

### F-003 — The dozen `CreateTextureCube*` / `CreateTexture2D*` overloads

GL-style: each overload encodes a specific GL upload convention
(`Half[]` mip arrays, packed face-major byte layouts, sRGB-vs-linear via
the format enum, etc.). Vulkan-style: a texture is a `VkImage` + memory +
view + format + extent + miplevels + arraylayers + samples — one
descriptor, many possible payloads. **Direction:** collapse the surface
to one or two entry points (`CreateTexture(TextureDescription, byte[] data)`
and maybe `AllocateTexture(TextureDescription)` for streamed uploads); push
format/aspect/conventions into `TextureDescription`.

### F-004 — `Mesh` + `DrawMesh` lacks instancing / indirect / offsets

GL VAO-style: one mesh = one (vertex, index) pair bound to a pipeline.
Vulkan-style: command buffers can record `vkCmdDrawIndexedIndirect`,
multi-instance, and bind vertex buffers with per-buffer offsets. Today's
abstraction doesn't expose any of this. **Direction:** add `instanceCount`
+ optional `instanceBuffer` to `DrawIndexedCommand`; add a `DrawIndirect`
variant; let `Mesh` carry sub-range offsets so a single big vertex buffer
can host many meshes.

### F-005 — No compute-pass concept

GL 4.1 can't issue compute dispatches at all (compute is 4.3+). The engine
never modeled compute as a result. Vulkan has compute as a first-class
queue/pipeline type. Today's `RenderPass` / `DrawIndexedCommand` shape
has no slot for it. **Direction:** add `ComputePass` (no
RenderPassDescription, no framebuffer, just dispatches) and
`DispatchCommand(pipeline, groupCount, ...)`. Required for froxel fog,
GPU-driven culling, anything modern.

### F-006 — Implicit synchronization everywhere

GL: serial command stream, driver inserts whatever sync is needed.
Vulkan: explicit barriers, queue dependencies, image layout transitions —
the consumer of `IGraphicsDevice` has no way to express "this pass reads
from that pass" so the backend can't insert barriers correctly without
guessing. Today this works for the trivial case (one pass writing to the
swapchain image, declared via subpass dependency in the render pass).
Anything beyond that needs explicit dependency declaration.
**Direction:** add `PassDependency` to `RenderPassDescription`
(reads-from / writes-to resource lists), or fold the whole thing into a
render-graph where dependencies fall out automatically from declared
input/output resources.

---

## During implementation

Sections per push, dated. Each push's section is "what we learned today."

### Push: spinning cube (depth + descriptors + UBO + name→offset)

Validation push to feel where the existing API creaked when we plumbed
real per-frame matrices through the cross-backend `ShaderUniform` flow.
All observations below were captured inline in the code with `// FRICTION`
markers pointing back to entries here.

### F-007 — Per-draw UBO collision when draws share a program

In `TranslateDrawIndexed` (Swapchain.cs) we route each draw's
`perDrawUniforms` into the program's single per-frame UBO. Two draws
issued in the same frame sharing the same shader program would
overwrite each other's uniforms before either GPU draw executed. The
hello-cube has one draw so it works; Sponza wouldn't.
**Direction (in order of escalation):** push constants for small per-draw
data (4 mat4s fits in 256 bytes which is the conservative push-constant
budget); dynamic-offset UBOs with one big ring buffer for everything;
per-draw descriptor sets allocated from a per-frame transient pool. The
cross-backend `DrawIndexedCommand.Uniforms` shape doesn't telegraph
which of these is intended — it implicitly assumes "set this many uniforms
just for this draw" with no rules about lifetime. Needs explicit
classification: per-pass vs per-material vs per-draw.

### F-008 — `System.Numerics.Matrix4x4` row-vector ↔ GLSL column-vector

REVISED after observation: the row-major memory layout of .NET's
`Matrix4x4` IS the transpose, because GLSL `std140` reads mat4 bytes
column-major. Writing .NET's row-vector matrix directly to the UBO
makes GLSL see it as the column-vector equivalent — mathematically
identical transformation. **No explicit `Matrix4x4.Transpose()` is
needed.** First write of `WriteUniformValue` added one, which
double-transposed and scrambled the cube's projection into garbage clip
positions — the visible symptom was "clipped + stretched pyramid"
because most vertices landed outside the [-1,1] x [-1,1] x [0,1] clip
cube.
**Direction:** no API change needed. Worth a brief note in the engine
docs once they exist — "matrix uniforms: pass System.Numerics matrices
unchanged; GLSL std140 column-major reading already produces the
correct transform." Footgun risk is real (the failure mode is silent
visual corruption, not a compile error), so the rule belongs somewhere
authoritative once the redesign lands.

### F-009 — Array uniforms hit std140 padding rules we don't model

`Matrix4x4ArrayUniform`, `Vector3ArrayUniform`, `FloatArrayUniform` are
all in the cross-backend API today but the Vulkan backend's
`WriteUniformValue` throws `NotImplemented` for them. The reason is
std140's array-stride rule (every array element is padded to a
multiple of 16 bytes — `float arr[4]` takes 64 bytes, not 16), and we
haven't modeled per-array stride in `UniformBlockMember`.
**Direction:** either extend `UniformBlockMember` with `ElementStride`
and write loops, or move arrays out of UBOs and into SSBOs (Vulkan's
preferred path for >handful arrays). Bone palettes specifically are
better as SSBOs.

### F-010 — No Vulkan-NDC-aware perspective helper

`HelloLoop.VulkanPerspective` is hand-rolled in the demo because
`System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView` returns a
GL-convention matrix (Y up, depth [-1,1]) and Vulkan wants Y down +
depth [0,1]. Every Vulkan-targeting demo will re-derive this.
**Direction:** add `Blix.Graphics.Matrices.PerspectiveVulkan(fovY,
aspect, near, far)` (or just make the engine's projection helpers
backend-aware via a static convention). Same story for orthographic
+ off-center, and same story for `CreateLookAt` (which is right-handed
in System.Numerics and matches Vulkan view space — no fix needed there).

### F-011 — `DescriptorSetLayout` shape is hardcoded in the backend

`CreateDescriptorInfrastructure` declares one UBO at binding 0 visible
to vertex+fragment stages. That's the only thing it'll ever produce.
Real shaders have multiple bindings (uniform blocks + samplers +
storage), distributed across sets (per-frame, per-pass, per-material,
per-draw), with stage-specific visibility. The cross-backend API has
no place to declare this today — `UniformBlockLayout` describes the
inside of one block, not the set's binding shape.
**Direction:** introduce `ShaderProgramInterface` carried alongside the
SPIR-V bytes: list of `(set, binding, type, stages, count, blockLayout?)`.
Could be cooked offline from SPIR-V reflection; could be hand-declared.
Material descriptor sets layer on top by referencing `(set, binding)`
explicitly.

### F-012 — `ClearDepth` is a `bool` but render passes always clear depth now

`RenderPassDescription.ClearDepth: bool` was fine for GL (you either
clear or you don't). With Vulkan's render-pass-baked load ops, the
choice is baked at `VkRenderPass` create time, not per `vkCmdBeginRenderPass`.
The bool today is effectively ignored — the depth attachment always
clears via the render pass's `LoadOp.Clear`. Same will become true of
color: when offscreen render passes land, their LoadOp/StoreOp need
to be declared at surface-creation time, not at pass-record time.
**Direction:** move clear ops + load/store ops to `RenderSurfaceDescription`
(or to a new `PassDescriptor` baked at cook time). Per-frame `Pass()`
calls just pick a pre-baked pass shape.

### F-015 — Render-pass compatibility forces identical subpass dependencies

The Phase-2 overlay pass needed `LoadOp.Load` to draw on top of the
cube's clear pass. To reuse the existing per-swapchain-image framebuffer
across both render passes, the framebuffer must be created with a render
pass that's **compatible** with the one used at `vkCmdBeginRenderPass`
time. The Vulkan spec lists what compatibility allows to differ (load
ops, initial/final layouts, etc.) — but the validator treats subpass
dependencies as part of compatibility too. Each diff in a dependency
field (stageMask, accessMask, dependencyFlags) produces a validation
error. Practical consequence: the default pass and the overlay pass
have to share a single conservative `SubpassDependency` shape even
though their actual needs differ. The shape ends up being a superset:
all stages + all accesses that either pass might need.
**Direction:** the render-graph abstraction (likely outcome of the
reshape) should bake `(loadOps, dependencies)` together at pass
declaration. Multiple "passes" sharing the same swapchain framebuffer
all use one render-pass instance under the hood, and dependencies are
derived from the declared resource reads/writes — eliminating the
manual-unification-of-dependency-shapes burden entirely.

### F-014 — `dFdx`/`dFdy` cross product sign flips between GL and Vulkan

In OpenGL framebuffers Y is up; in Vulkan Y is down. Code that derives
a face normal from `cross(dFdx(worldPos), dFdy(worldPos))` produces an
OUTWARD normal in GL and an INWARD normal in Vulkan — same source, same
shader, silently flipped lighting. First cube draft used the GL order
and the top face ended up dark while the bottom face lit, which read
as "the light is coming from the wrong direction."
**Direction:** the shader library (`src/Blix.Shaders/`) should grow a
`blix_face_normal(worldPos)` helper that does the right thing for the
current backend. Could be backend-conditional via `#define`, or just
documented as "use `cross(dy, dx)` for Vulkan / `cross(dx, dy)` for GL"
in the shader-authoring guide. The cross order is one character but
the failure mode is subtle visual wrongness, so it belongs in a helper.

### F-016 — Backends require different .NET-side matrix conventions (audit discovery)

Surfaced during the audit when verifying the F-008 disposition: the
GL backend's `WriteColumnMajor` reorders M-fields on upload (so a
`Matrix4x4` built in **column-vector form** — M[r,c] = math (row, col) —
lands in GLSL correctly), while the Vulkan backend's `WriteUniformValue`
does direct memcpy (so a `Matrix4x4` must be in **row-vector form** —
M[r,c] = math (col, row) — to land in GLSL correctly). Concretely:
existing `GraphicsMatrices.CreatePerspective` puts the perspective-divide
flag at M43 (column-vector layout); the new `CreatePerspectiveVulkan`
puts it at M34 (row-vector layout). Demos that use the wrong helper on
the wrong backend silently produce garbage geometry (this is exactly
the "clipped pyramid" bug from the cube push).

**Direction:** the engine adopts **.NET row-vector form** as its single
convention. The GL backend's upload path switches to
`glUniformMatrix4(..., transpose: true, ...)` with raw .NET bytes
(functionally equivalent to today's `WriteColumnMajor`, just simpler).
All existing `GraphicsMatrices` helpers (`CreatePerspective`,
`CreateLookAt`, `CreateModel`, `CreateNormalMatrix`, etc.) get rewritten
to produce row-vector form. The Vector-D-shipped
`CreatePerspectiveVulkan` folds back into the unified `CreatePerspective`
(no backend-specific variants once the engine speaks one convention).

**Migration plan:** F-016 ships as its own commit *before* any Vector A
binding work, with explicit acceptance criteria:
- GL cube (existing demo) still renders correctly post-migration
- Vulkan cube still renders correctly post-migration
- Numerical tests on `CreatePerspective`, `CreateLookAt`, `CreateNormalMatrix`
- `Vector4.Transform` test against the new convention
- Matrix-upload golden-byte-layout test (memory contents match expected
  std140 bytes after upload)
- **Backend-symmetry test:** identical `Matrix4x4` input → identical
  fragment output on both backends (off-screen render, pixel compare)
The migration touches enough mental assumptions that smuggling it into
Vector A would conflate two redesigns. Doing it dedicated makes every
subsequent Vector A failure isolatable to binding-layer code, not
matrix-convention code.

**STATUS: RESOLVED.** Migration shipped. Key changes:
- `Blix.Test.Graphics` test project added with 35 invariant tests
  covering System.Numerics conformance, `GraphicsMatrices.*` row-vector
  shapes, end-to-end backend symmetry (.NET → upload → simulated GLSL
  M*v_col → matches Vector4.Transform). Run via
  `dotnet run --project src/Blix.Test.Graphics`.
- `GraphicsMatrices.cs` rewritten: `CreatePerspective` perspective-divide
  flag moved from M43 to M34, `CreateModel` composition order reversed
  (`T*R*S` → `S*R*T`), `CreateNormalMatrix` no longer transposes (just
  Invert), `CreateLookAt` delegates to `System.Numerics.Matrix4x4.CreateLookAt`
  (already row-vector form), `CreateOrthographic*` translation moved
  to last row, `TransformPoint`/`TransformDirection` rewritten with
  row-vector formulas. Private `CreateTranslation`/`CreateScale`/
  `CreateRotation` removed — `System.Numerics` equivalents used directly.
- `OpenGLGraphicsDevice.State.cs`: `WriteColumnMajor` removed, replaced
  by `WriteMatrixBytes` (direct memcpy) + `glUniformMatrix4(transpose: true)`.
  Matches Vulkan backend's direct-memcpy upload path. Engine + backends
  speak one matrix convention end-to-end now.
- `GltfStaticImporter.cs`, `GltfImporter.cs`, `BoneTransform.cs`: removed
  `Matrix4x4.Transpose` calls at the SharpGLTF → engine boundary. Both
  layers are row-vector now; transpose was bridging conventions that no
  longer differ.
- `Camera3D.GetViewProjection` and `Camera2D.GetViewProjection` composition
  order reversed (`proj * view` → `view * proj`) to match row-vector
  composition semantics. `Camera3D.ScreenPointToRay` switched from
  hand-rolled `TransformColumnVector4` to `Vector4.Transform`.
- `Camera2D.GetView` matrix rewritten with translation in last row
  (M41-M43) instead of last column (M14-M24).

The Vector-D-shipped `CreatePerspectiveVulkan` stays as a sibling to
`CreatePerspective` — both are now in row-vector form but with different
projection math (Vulkan: Y-flip + [0,1] depth; GL: Y-up + [-1,1] depth).

### F-013 — Friction is concentrated in `Material` / uniform plumbing, not in handles

Strong signal: handle-based resource APIs (`VertexBuffer`, `IndexBuffer`,
`Pipeline`, `ShaderProgram`) survive cleanly. Resource lifecycles
(`Create*` / `Destroy*`) survive cleanly. The shape of `RenderCommandList`
+ `Pass` + `DrawIndexedCommand` survives cleanly. What doesn't survive:
the *binding* layer — `ShaderUniform`, `ShaderTextureBinding`,
`Material.SetUniform(name, ...)`, `MaterialResolver.RegisterPipeline(name)`,
all the name-keyed reflection-driven stuff. That layer is the reshape
target. Below it (handles, lists, passes) is portable.

---

## Audit phase consolidation

Fifteen friction entries consolidated into four redesign vectors along
a single axis: how much of the binding/dispatch story moves from
per-frame runtime decisions into pre-baked declarations.

### Render-graph philosophy decision

The reshape adopts a **"focused middle"** render-graph position — between
bgfx's imperative passes and Unreal RDG's per-frame rebuild:

- Passes are **baked at engine init**, not rebuilt per-frame.
- I/O shape (targets + load/store ops + reads + shader interface) is
  **fixed at pass registration**.
- Per-frame calls supply runtime parameters (uniforms, descriptor binds,
  toggle flags) and trigger `Execute`.
- Persistent resources only; transient/aliased resource lifetimes are a
  deferred concern.
- Sync (barriers + image layout transitions) and render-pass compatibility
  are derived automatically from the declared graph.

Explicitly NOT going for: full RDG-style per-frame graph rebuild (overkill,
unbounded maintenance), nor bgfx-style purely imperative passes (loses
the auto-sync leverage that justifies the design).

### Vector A — Shader interface + material binding

**Friction notes:** F-001, F-002, F-007, F-009, F-011
**Status:** Planned. Gates Vector B.
**Effort:** 1–2 weeks design + implementation.

Replace name-keyed bindings with explicit `(set, binding)` declarations
carried by a `ShaderInterface` alongside the SPIR-V bytes. Reserve
descriptor sets 0–3 by lifetime: per-frame, per-pass, per-material,
per-draw. `Material` becomes the carrier for set-2 only; per-frame
and per-pass bindings live on the pass; per-draw goes through push
constants (256-byte budget covers MVP + a handful of small uniforms).
SSBOs replace UBOs for bone palettes and other >handful arrays.

API outline (subject to refinement when Vector A starts):

```csharp
public sealed record ShaderInterface(
    IReadOnlyList<DescriptorSetSlot> Slots,
    IReadOnlyList<PushConstantRange> PushConstants,
    VertexLayout VertexInput);

public sealed record DescriptorSetSlot(
    int Set, int Binding,
    ShaderResourceType Type,        // UniformBuffer, StorageBuffer, SampledImage, StorageImage, Sampler
    ShaderStages Stages,
    int Count = 1,
    UniformBlockLayout? BlockLayout = null);

// Set conventions, enforced by the engine:
//   set 0: per-frame    (viewProj, sun direction, env probe)
//   set 1: per-pass     (shadow map, gbuffer reads)
//   set 2: per-material (albedo + normal + MR textures, factors)
//   set 3: per-draw     (push constants when ≤256B; descriptor set otherwise)
```

Disposition of contributing friction notes:
- **F-001:** Resolved — `CreateShaderProgram` takes SPIR-V bytes + `ShaderInterface`. GLSL text path becomes optional runtime helper.
- **F-002:** Resolved — bindings are explicit `(set, binding)`. Names survive only as cook-time aliases in material descriptors.
- **F-007:** Resolved by classification — per-draw is push constants, per-frame/pass/material each have their own descriptor set with appropriate lifetime.
- **F-009:** Resolved — `UniformBlockMember.ElementStride` shipped (default 0 for scalars, non-zero for array members documenting the std140 element stride). The array-uniform writer is deferred until the ShaderLab port's lit shader needs it; the declaration shape is complete. Bone palettes move to SSBOs as planned.
- **F-011:** Resolved — descriptor set layout is derived from `ShaderInterface` instead of hardcoded.

### Vector B — Render graph + pass declaration

**Friction notes:** F-005, F-006, F-012, F-015
**Status:** Planned. Depends on Vector A (graph nodes need a shader-interface shape to bind into).
**Effort:** 2–3 weeks design + implementation.

Pre-bake the frame's pass topology at init. Each pass declares its
render targets (with load/store ops), the resources it reads from
other passes, and its `ShaderInterface`. Backend computes barriers,
framebuffer compatibility, and dependency shapes from the declared
graph. Compute passes are first-class — they declare dispatches
instead of draws. Per-frame `Execute` just supplies parameters.

API outline:

```csharp
var graph = new RenderGraph(device);

graph.GraphicsPass("shadow")
     .Target(shadowMap, LoadOp.Clear, StoreOp.Store)
     .Shader(shadowProgram);

graph.GraphicsPass("scene")
     .Target(hdrScene, LoadOp.Clear)
     .Target(depth, LoadOp.Clear)
     .Read(shadowMap)              // declares dependency
     .Shader(litProgram);

graph.ComputePass("fog-froxels")
     .Read(depth)
     .Write(fogVolume)
     .Shader(fogComputeProgram);

graph.GraphicsPass("composite")
     .Target(swapchain, LoadOp.DontCare)
     .Read(hdrScene)
     .Read(fogVolume)
     .Shader(compositeProgram);

graph.Compile();                   // sync/barriers/framebuffer resolution

// per-frame:
graph.Execute(parameters);
```

Disposition of contributing friction notes:
- **F-005:** Resolved — `ComputePass` is a first-class graph node.
- **F-006:** Resolved — sync derived from declared `Read`/`Write` edges.
- **F-012:** Resolved — `LoadOp` lives on `.Target(...)` at pass declaration time, not on the per-frame call.
- **F-015:** Resolved — graph generates one render-pass per compatible group of graphics passes; dep unification is internal.

### Vector C — Resource shape cleanup

**Friction notes:** F-003, F-004
**Status:** Planned. Parallelizable with Vector A (low-risk filler).
**Effort:** 1–3 days.

Collapse texture creation overloads to `CreateTexture(TextureDescription,
byte[])` + `AllocateTexture(TextureDescription)` for streamed uploads.
Add `instanceCount` + `instanceBuffer` + `firstInstance` to
`DrawIndexedCommand`; introduce `DrawIndirectCommand`. Let `Mesh` carry
sub-range offsets so a single big VBO can host many meshes — gateway
for later GPU-driven submission work.

Disposition:
- **F-003:** Resolved — texture creation surface collapses to one or two entry points.
- **F-004:** Resolved — instancing + indirect added to draw commands; `Mesh` carries offsets.

### Vector D — Convention helpers

**Friction notes:** F-008, F-010, F-014, F-016
**Status:** Do FIRST.
**Effort:** Half a day.

Disposition per note (this is what Vector D actually ships):

- **F-010 — RESOLVED.** `Blix.Graphics.GraphicsMatrices.CreatePerspectiveVulkan`
  (Y-down + [0,1] depth, row-vector form). Demo uses it directly. Sibling
  to the existing `CreatePerspective` until Vector A unifies conventions.
- **F-014 — RESOLVED inline.** `cube.frag` already uses `cross(dFdy, dFdx)`
  with a comment block explaining why. A proper shared `blix_face_normal`
  helper in `src/Blix.Shaders/` is deferred until the shader-library
  access path is reworked in Vector A or B (the existing `<None Include>`
  copy-glob is GL-shaped; Vulkan uses glslc `-I` includes which the
  current csproj doesn't wire up).
- **F-008 — RESOLVED.** Closed by Vector A binding model + F-016 matrix
  migration. BlockLayout is the source of truth for UBO member offsets
  (see Q-005 resolution); name-keyed ShaderUniform writes flow through
  BlockLayout member lookup, no `[StructLayout]` reflection in the hot
  path. .NET row-vector matrices are written via direct memcpy to
  host-visible UBO memory on Vulkan and via `glUniformMatrix4(transpose:
  false)` on GL — both paths reach GLSL as column-vector form via the
  std140 reinterpret. Convention is settled engine-wide.

### Order of attack

1. **Vector D first** — half a day. Clears the small footguns; gives quick confidence that the audit produces real changes.
2. **Vector A** — gates B because the render graph needs to know shader interfaces to wire descriptor bindings correctly. Also unlocks the first real material-driven demo.
3. **Vector C in parallel with A** — pure cleanup, low risk; lands whenever it doesn't conflict.
4. **Vector B** — biggest payoff, biggest design surface. Built on Vector A.
5. **Port a scene** to the reshape (likely Sponza Modern — simpler than the Walkthrough flagship).
6. **Visual rework** on the reshaped engine (TAA, froxel fog, etc.) — the original goal that started this branch.
7. **Sunset OpenGL** — once the reshape and one ported demo are stable.
